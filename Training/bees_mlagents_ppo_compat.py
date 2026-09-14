"""Narrow compatibility fixes and exploration guardrails for Bees PPO training."""

from __future__ import annotations

import math
import threading
from typing import Callable, Optional


VALUE_KEY_PROBE = "__bees_value_key_probe__"
MAX_CONTINUOUS_SIGMA = 1.5
MAX_ADAPTIVE_BETA = 0.004
INITIAL_BETA_MULTIPLIER = 4.0
FAST_REWARD_ALPHA = 0.05
SLOW_REWARD_ALPHA = 0.005
REWARD_SCALE_ALPHA = 0.02
MIN_ADAPTIVE_EPISODES = 96
ADAPTIVE_EVALUATION_INTERVAL = 32
REWARD_TREND_THRESHOLD = 0.05
BETA_IMPROVEMENT_MULTIPLIER = 0.80
BETA_DECLINE_MULTIPLIER = 1.50
BETA_NEUTRAL_RELAXATION = 0.15

BEES_MOVEMENT_CONTINUOUS_ACTIONS = 2
BEES_WEAPON_SLOTS = 16
BEES_WEAPON_AIM_ACTIONS_PER_SLOT = 2
BEES_CONTINUOUS_ACTIONS = (
    BEES_MOVEMENT_CONTINUOUS_ACTIONS
    + BEES_WEAPON_SLOTS * BEES_WEAPON_AIM_ACTIONS_PER_SLOT
)
BEES_DISCRETE_BRANCHES = (2,) * BEES_WEAPON_SLOTS + (5, 65, 65, 65)
ACTION_ENTROPY_EPSILON = 1e-7

_ORIGINAL_GAUSSIAN_FORWARD = None
_ORIGINAL_ACTION_MODEL_FORWARD = None
_ORIGINAL_ACTION_MODEL_EVALUATE = None
_ORIGINAL_PPO_UPDATE = None
_ORIGINAL_TRUST_REGION_POLICY_LOSS = None
_ORIGINAL_PPO_CREATE_OPTIMIZER = None
_ORIGINAL_PPO_PROCESS_TRAJECTORY = None
_POLICY_DIMENSION_MASK_STATE = threading.local()


class AdaptiveExplorationController:
    """Adjust PPO entropy pressure from sustained episode-reward trends.

    Fresh training begins above the configured beta baseline. Improving rewards
    reduce exploration toward that baseline, sustained reward deterioration raises
    it temporarily, and neutral performance relaxes it gradually back toward the
    baseline. Fast and slow exponential moving averages plus an evaluation interval
    keep individual noisy self-play episodes from changing beta directly.

    Resumed training starts at the baseline because the reward-history controller is
    intentionally not checkpoint state; it can raise exploration again after enough
    new terminal episodes establish a sustained decline.
    """

    def __init__(
        self,
        baseline_beta: float,
        *,
        start_high: bool,
        max_beta: float = MAX_ADAPTIVE_BETA,
    ) -> None:
        if not math.isfinite(baseline_beta) or baseline_beta <= 0.0:
            raise ValueError(
                f"baseline_beta must be finite and positive; got {baseline_beta!r}."
            )
        if not math.isfinite(max_beta) or max_beta <= 0.0:
            raise ValueError(f"max_beta must be finite and positive; got {max_beta!r}.")
        if baseline_beta > max_beta:
            raise ValueError(
                f"baseline_beta {baseline_beta} exceeds adaptive maximum {max_beta}."
            )

        self.baseline_beta = baseline_beta
        self.max_beta = max_beta
        initial_beta = min(max_beta, baseline_beta * INITIAL_BETA_MULTIPLIER)
        self.current_beta = initial_beta if start_high else baseline_beta
        self.episode_count = 0
        self.fast_reward: Optional[float] = None
        self.slow_reward: Optional[float] = None
        self.reward_scale: Optional[float] = None
        self.normalized_trend = 0.0

    @staticmethod
    def _ema(previous: Optional[float], value: float, alpha: float) -> float:
        if previous is None:
            return value
        return previous + alpha * (value - previous)

    def observe_reward(self, reward: float) -> float:
        """Ingest one terminal episode reward and return the current beta target."""

        if not math.isfinite(reward):
            return self.current_beta

        self.episode_count += 1
        self.fast_reward = self._ema(self.fast_reward, reward, FAST_REWARD_ALPHA)
        self.slow_reward = self._ema(self.slow_reward, reward, SLOW_REWARD_ALPHA)
        self.reward_scale = self._ema(
            self.reward_scale, abs(reward), REWARD_SCALE_ALPHA
        )

        scale = max(self.reward_scale or 0.0, 1e-6)
        self.normalized_trend = (self.fast_reward - self.slow_reward) / scale

        if self.episode_count < MIN_ADAPTIVE_EPISODES:
            return self.current_beta
        if self.episode_count % ADAPTIVE_EVALUATION_INTERVAL != 0:
            return self.current_beta

        if self.normalized_trend <= -REWARD_TREND_THRESHOLD:
            self.current_beta = min(
                self.max_beta,
                max(
                    self.baseline_beta,
                    self.current_beta * BETA_DECLINE_MULTIPLIER,
                ),
            )
        elif self.normalized_trend >= REWARD_TREND_THRESHOLD:
            self.current_beta = max(
                self.baseline_beta,
                self.current_beta * BETA_IMPROVEMENT_MULTIPLIER,
            )
        elif scale > 1e-5:
            # Once the reward signal is established, stable performance should not
            # leave a temporary exploration boost stuck forever.
            self.current_beta = self.baseline_beta + (
                self.current_beta - self.baseline_beta
            ) * (1.0 - BETA_NEUTRAL_RELAXATION)

        return self.current_beta


class _AdaptiveBetaSchedule:
    """ML-Agents DecayedValue-compatible wrapper around adaptive beta state."""

    def __init__(self, controller: AdaptiveExplorationController) -> None:
        self.controller = controller

    def get_value(self, step: int) -> float:
        del step
        return self.controller.current_beta


def _is_bees_action_spec(action_spec) -> bool:
    if action_spec is None or action_spec.continuous_size != BEES_CONTINUOUS_ACTIONS:
        return False
    return tuple(int(size) for size in action_spec.discrete_branches) == BEES_DISCRETE_BRANCHES


def _build_bees_continuous_activity_mask(action_spec, masks, reference):
    """Return 1 for continuous actions that physically exist for each agent sample.

    Bees keeps a fixed 34-output policy ABI. The first two continuous actions are
    movement. Each of the 16 weapon slots reserves two aim outputs. Unity already
    masks the Fire action of a weapon branch when that slot has no Turret, so the
    existing discrete mask is a reliable per-sample signal for whether the matching
    aim pair can affect the environment.
    """

    if masks is None or reference is None or not _is_bees_action_spec(action_spec):
        return None
    if masks.ndim != 2 or reference.ndim != 2:
        return None
    if reference.shape[1] != BEES_CONTINUOUS_ACTIONS:
        return None
    if masks.shape[1] < sum(BEES_DISCRETE_BRANCHES):
        return None

    activity = reference.new_ones(reference.shape)
    for slot in range(BEES_WEAPON_SLOTS):
        fire_action_index = slot * 2 + 1
        slot_active = (masks[:, fire_action_index] > 0.5).to(activity.dtype)
        aim_start = (
            BEES_MOVEMENT_CONTINUOUS_ACTIONS
            + slot * BEES_WEAPON_AIM_ACTIONS_PER_SLOT
        )
        activity[:, aim_start : aim_start + BEES_WEAPON_AIM_ACTIONS_PER_SLOT] = (
            slot_active.unsqueeze(1)
        )
    return activity


def _masked_action_log_probs_and_entropy(action_model, actions, dists, masks):
    """Mirror ML-Agents 1.1.0 action statistics while excluding nonexistent aim slots."""

    from mlagents.torch_utils import torch
    from mlagents.trainers.torch_entities.action_log_probs import ActionLogProbs

    entropies = []
    continuous_log_prob = None
    discrete_log_probs = None
    all_discrete_log_probs = None

    if dists.continuous is not None:
        continuous_log_prob = dists.continuous.log_prob(actions.continuous_tensor)
        activity = _build_bees_continuous_activity_mask(
            action_model.action_spec,
            masks,
            continuous_log_prob,
        )
        if activity is None:
            entropies.append(dists.continuous.entropy())
        else:
            continuous_log_prob = continuous_log_prob * activity
            per_dimension_entropy = 0.5 * torch.log(
                2 * math.pi * math.e * dists.continuous.std**2
                + ACTION_ENTROPY_EPSILON
            )
            active_count = torch.clamp(activity.sum(dim=1, keepdim=True), min=1.0)
            entropies.append(
                (per_dimension_entropy * activity).sum(dim=1, keepdim=True)
                / active_count
            )

    if dists.discrete is not None:
        discrete_log_probs = []
        all_discrete_log_probs = []
        for discrete_action, discrete_dist in zip(
            actions.discrete_list,
            dists.discrete,
        ):
            discrete_log_probs.append(discrete_dist.log_prob(discrete_action))
            all_discrete_log_probs.append(discrete_dist.all_log_prob())
            entropies.append(discrete_dist.entropy())

    action_log_probs = ActionLogProbs(
        continuous_log_prob,
        discrete_log_probs,
        all_discrete_log_probs,
    )
    entropy_sum = torch.sum(torch.cat(entropies, dim=1), dim=1)
    return action_log_probs, entropy_sum


def _build_bees_policy_dimension_mask(action_spec, action_masks):
    """Build PPO loss weights that omit inactive continuous aim dimensions only."""

    from mlagents.torch_utils import torch

    if action_masks is None or not _is_bees_action_spec(action_spec):
        return None
    continuous_reference = torch.ones(
        (action_masks.shape[0], action_spec.continuous_size),
        dtype=action_masks.dtype,
        device=action_masks.device,
    )
    continuous_activity = _build_bees_continuous_activity_mask(
        action_spec,
        action_masks,
        continuous_reference,
    )
    if continuous_activity is None:
        return None

    # Preserve ML-Agents' existing treatment of discrete branches. This change is
    # deliberately scoped to continuous weapon-aim outputs that have no physical
    # effect for the current ship.
    discrete_activity = torch.ones(
        (action_masks.shape[0], action_spec.discrete_size),
        dtype=continuous_activity.dtype,
        device=continuous_activity.device,
    )
    return torch.cat((continuous_activity, discrete_activity), dim=1)


def _trust_region_policy_loss_with_dimension_mask(
    advantages,
    log_probs,
    old_log_probs,
    loss_masks,
    epsilon,
    dimension_mask,
):
    """ML-Agents PPO loss with inactive action dimensions removed from the mean."""

    from mlagents.torch_utils import torch

    advantage = advantages.unsqueeze(-1)
    r_theta = torch.exp(log_probs - old_log_probs)
    p_opt_a = r_theta * advantage
    p_opt_b = torch.clamp(r_theta, 1.0 - epsilon, 1.0 + epsilon) * advantage
    element_loss = -torch.min(p_opt_a, p_opt_b)

    valid_steps = loss_masks.to(element_loss.dtype).unsqueeze(-1)
    valid_dimensions = dimension_mask.to(element_loss.dtype)
    weights = valid_steps * valid_dimensions
    return (element_loss * weights).sum() / torch.clamp(weights.sum(), min=1.0)


def install_inactive_continuous_action_masking() -> Optional[Callable]:
    """Exclude nonexistent weapon aim slots from PPO learning without changing ABI."""

    from mlagents.trainers.buffer import BufferKey
    from mlagents.trainers.ppo.optimizer_torch import TorchPPOOptimizer
    from mlagents.trainers.torch_entities.action_model import ActionModel
    from mlagents.trainers.torch_entities.utils import ModelUtils

    global _ORIGINAL_ACTION_MODEL_FORWARD
    global _ORIGINAL_ACTION_MODEL_EVALUATE
    global _ORIGINAL_PPO_UPDATE
    global _ORIGINAL_TRUST_REGION_POLICY_LOSS

    if _ORIGINAL_ACTION_MODEL_FORWARD is not None:
        return None

    original_forward = ActionModel.forward
    original_evaluate = ActionModel.evaluate
    original_update = TorchPPOOptimizer.update
    original_policy_loss = ModelUtils.trust_region_policy_loss

    def masked_forward(self, inputs, masks):
        dists = self._get_dists(inputs, masks)
        actions = self._sample_action(dists)
        log_probs, entropy = _masked_action_log_probs_and_entropy(
            self,
            actions,
            dists,
            masks,
        )
        return actions, log_probs, entropy

    def masked_evaluate(self, inputs, masks, actions):
        dists = self._get_dists(inputs, masks)
        return _masked_action_log_probs_and_entropy(
            self,
            actions,
            dists,
            masks,
        )

    def masked_policy_loss(
        advantages,
        log_probs,
        old_log_probs,
        loss_masks,
        epsilon,
    ):
        dimension_mask = getattr(_POLICY_DIMENSION_MASK_STATE, "mask", None)
        if dimension_mask is None or tuple(dimension_mask.shape) != tuple(log_probs.shape):
            return original_policy_loss(
                advantages,
                log_probs,
                old_log_probs,
                loss_masks,
                epsilon,
            )
        return _trust_region_policy_loss_with_dimension_mask(
            advantages,
            log_probs,
            old_log_probs,
            loss_masks,
            epsilon,
            dimension_mask,
        )

    def masked_update(self, batch, num_sequences):
        from mlagents.trainers.torch_entities.utils import ModelUtils

        action_masks = ModelUtils.list_to_tensor(batch[BufferKey.ACTION_MASK])
        dimension_mask = _build_bees_policy_dimension_mask(
            self.policy.behavior_spec.action_spec,
            action_masks,
        )
        _POLICY_DIMENSION_MASK_STATE.mask = dimension_mask
        try:
            return original_update(self, batch, num_sequences)
        finally:
            _POLICY_DIMENSION_MASK_STATE.mask = None

    ActionModel.forward = masked_forward
    ActionModel.evaluate = masked_evaluate
    TorchPPOOptimizer.update = masked_update
    ModelUtils.trust_region_policy_loss = staticmethod(masked_policy_loss)

    _ORIGINAL_ACTION_MODEL_FORWARD = original_forward
    _ORIGINAL_ACTION_MODEL_EVALUATE = original_evaluate
    _ORIGINAL_PPO_UPDATE = original_update
    _ORIGINAL_TRUST_REGION_POLICY_LOSS = original_policy_loss
    return original_forward


def restore_inactive_continuous_action_masking() -> None:
    """Restore ML-Agents action statistics and PPO update methods."""

    global _ORIGINAL_ACTION_MODEL_FORWARD
    global _ORIGINAL_ACTION_MODEL_EVALUATE
    global _ORIGINAL_PPO_UPDATE
    global _ORIGINAL_TRUST_REGION_POLICY_LOSS

    if _ORIGINAL_ACTION_MODEL_FORWARD is None:
        return

    from mlagents.trainers.ppo.optimizer_torch import TorchPPOOptimizer
    from mlagents.trainers.torch_entities.action_model import ActionModel
    from mlagents.trainers.torch_entities.utils import ModelUtils

    ActionModel.forward = _ORIGINAL_ACTION_MODEL_FORWARD
    ActionModel.evaluate = _ORIGINAL_ACTION_MODEL_EVALUATE
    TorchPPOOptimizer.update = _ORIGINAL_PPO_UPDATE
    ModelUtils.trust_region_policy_loss = staticmethod(
        _ORIGINAL_TRUST_REGION_POLICY_LOSS
    )
    _POLICY_DIMENSION_MASK_STATE.mask = None

    _ORIGINAL_ACTION_MODEL_FORWARD = None
    _ORIGINAL_ACTION_MODEL_EVALUATE = None
    _ORIGINAL_PPO_UPDATE = None
    _ORIGINAL_TRUST_REGION_POLICY_LOSS = None


def install_continuous_sigma_guard(
    max_sigma: float = MAX_CONTINUOUS_SIGMA,
) -> Optional[Callable]:
    """Cap continuous-action exploration variance without changing the policy ABI.

    ML-Agents 1.1.0 learns one unconstrained ``log_sigma`` parameter per
    continuous action when ``conditional_sigma`` is disabled. Bees uses that
    default layout. Large values can make almost every sample hit ML-Agents'
    downstream action clamp, reducing movement/aiming to saturated directions.

    The guard clamps the learned unconditional parameter before every forward
    pass. Conditional distributions, if introduced later, have their emitted
    standard deviation clamped instead. The lower side is intentionally left
    unconstrained so the policy can still become precise; Bees keeps a nonzero
    PPO beta as the exploration floor.
    """

    if not math.isfinite(max_sigma) or max_sigma <= 0.0:
        raise ValueError(f"max_sigma must be finite and positive; got {max_sigma!r}.")

    from mlagents.torch_utils import torch
    from mlagents.trainers.torch_entities.distributions import GaussianDistribution

    global _ORIGINAL_GAUSSIAN_FORWARD
    if _ORIGINAL_GAUSSIAN_FORWARD is not None:
        return None

    original_forward = GaussianDistribution.forward
    max_log_sigma = math.log(max_sigma)

    def guarded_forward(self, inputs):
        if self.conditional_sigma:
            distribution = original_forward(self, inputs)
            distribution.std = torch.clamp(distribution.std, max=max_sigma)
            return distribution

        # Clamp the parameter itself so resumed checkpoints with pathological
        # sigma values are repaired before they can affect an action, loss, or
        # ONNX export. no_grad keeps the projection outside the PPO gradient.
        with torch.no_grad():
            self.log_sigma.clamp_(max=max_log_sigma)
        return original_forward(self, inputs)

    GaussianDistribution.forward = guarded_forward
    _ORIGINAL_GAUSSIAN_FORWARD = original_forward
    return original_forward


def restore_continuous_sigma_guard(original: Optional[Callable] = None) -> None:
    """Restore ML-Agents' Gaussian forward method after the trainer exits."""

    global _ORIGINAL_GAUSSIAN_FORWARD
    installed_original = _ORIGINAL_GAUSSIAN_FORWARD
    if installed_original is None:
        return

    from mlagents.trainers.torch_entities.distributions import GaussianDistribution

    GaussianDistribution.forward = original or installed_original
    _ORIGINAL_GAUSSIAN_FORWARD = None


def install_adaptive_exploration() -> Optional[Callable]:
    """Make PPO beta respond to sustained terminal-reward improvement or decline."""

    from mlagents.trainers.ppo.trainer import PPOTrainer

    global _ORIGINAL_PPO_CREATE_OPTIMIZER, _ORIGINAL_PPO_PROCESS_TRAJECTORY
    if _ORIGINAL_PPO_CREATE_OPTIMIZER is not None:
        return None

    original_create_optimizer = PPOTrainer.create_optimizer
    original_process_trajectory = PPOTrainer._process_trajectory

    def adaptive_create_optimizer(self):
        optimizer = original_create_optimizer(self)
        controller = AdaptiveExplorationController(
            float(self.hyperparameters.beta),
            start_high=not self.load,
        )
        self._bees_adaptive_exploration = controller
        optimizer.decay_beta = _AdaptiveBetaSchedule(controller)
        return optimizer

    def adaptive_process_trajectory(self, trajectory):
        original_process_trajectory(self, trajectory)
        if not trajectory.done_reached:
            return

        controller = getattr(self, "_bees_adaptive_exploration", None)
        if controller is None or not self.reward_buffer:
            return

        controller.observe_reward(float(self.reward_buffer[0]))
        self.stats_reporter.set_stat(
            "Policy/Adaptive Reward Fast", float(controller.fast_reward)
        )
        self.stats_reporter.set_stat(
            "Policy/Adaptive Reward Slow", float(controller.slow_reward)
        )
        self.stats_reporter.set_stat(
            "Policy/Adaptive Reward Trend", float(controller.normalized_trend)
        )
        self.stats_reporter.set_stat(
            "Policy/Adaptive Beta Target", float(controller.current_beta)
        )

    PPOTrainer.create_optimizer = adaptive_create_optimizer
    PPOTrainer._process_trajectory = adaptive_process_trajectory
    _ORIGINAL_PPO_CREATE_OPTIMIZER = original_create_optimizer
    _ORIGINAL_PPO_PROCESS_TRAJECTORY = original_process_trajectory
    return original_create_optimizer


def restore_adaptive_exploration() -> None:
    """Restore ML-Agents' PPO trainer methods after the trainer exits."""

    global _ORIGINAL_PPO_CREATE_OPTIMIZER, _ORIGINAL_PPO_PROCESS_TRAJECTORY
    if _ORIGINAL_PPO_CREATE_OPTIMIZER is None:
        return

    from mlagents.trainers.ppo.trainer import PPOTrainer

    PPOTrainer.create_optimizer = _ORIGINAL_PPO_CREATE_OPTIMIZER
    PPOTrainer._process_trajectory = _ORIGINAL_PPO_PROCESS_TRAJECTORY
    _ORIGINAL_PPO_CREATE_OPTIMIZER = None
    _ORIGINAL_PPO_PROCESS_TRAJECTORY = None


def install_value_estimate_key_fix() -> Optional[Callable[[str], object]]:
    """Install Bees' ML-Agents 1.1.0 PPO compatibility fixes.

    ML-Agents 1.1.0 defines RewardSignalUtil.value_estimates_key() using the
    RETURNS prefix, so PPO overwrites the old critic predictions with calculated
    returns before the optimizer can use value clipping correctly. The Bees
    launcher already pins/guards ML-Agents 1.1.0; this adds a second structural
    guard so an unexpected vendor change cannot be patched silently.

    This installer also enables the continuous-sigma guard, inactive continuous
    weapon-action masking, and reward-responsive beta controller. Keeping all of
    them behind the launcher's existing compatibility hook avoids changing the
    policy ABI or requiring a fork of ML-Agents.

    Returns the original static method when the value-key patch was installed,
    or None when the installed package already exposes the correct key.
    """

    from mlagents.trainers.buffer import RewardSignalKeyPrefix, RewardSignalUtil

    original = RewardSignalUtil.value_estimates_key
    value_key = original(VALUE_KEY_PROBE)
    returns_key = RewardSignalUtil.returns_key(VALUE_KEY_PROBE)
    correct_key = (RewardSignalKeyPrefix.VALUE_ESTIMATES, VALUE_KEY_PROBE)
    patched_value_key = False

    if value_key != correct_key:
        if value_key != returns_key:
            raise RuntimeError(
                "Unexpected ML-Agents reward-signal key layout: value_estimates_key() "
                f"returned {value_key!r}, returns_key() returned {returns_key!r}. "
                "Refuse to apply the Bees PPO compatibility patch to unknown internals."
            )

        def fixed_value_estimates_key(name: str):
            return RewardSignalKeyPrefix.VALUE_ESTIMATES, name

        RewardSignalUtil.value_estimates_key = staticmethod(fixed_value_estimates_key)
        patched_value_key = True

        installed_value_key = RewardSignalUtil.value_estimates_key(VALUE_KEY_PROBE)
        if installed_value_key != correct_key or installed_value_key == returns_key:
            RewardSignalUtil.value_estimates_key = staticmethod(original)
            raise RuntimeError(
                "Failed to separate ML-Agents PPO value-estimate and return keys."
            )

    try:
        install_continuous_sigma_guard()
        install_inactive_continuous_action_masking()
        install_adaptive_exploration()
    except Exception:
        restore_adaptive_exploration()
        restore_inactive_continuous_action_masking()
        restore_continuous_sigma_guard()
        if patched_value_key:
            RewardSignalUtil.value_estimates_key = staticmethod(original)
        raise

    return original if patched_value_key else None


def restore_value_estimate_key(original: Optional[Callable[[str], object]]) -> None:
    """Restore vendor PPO methods after the trainer exits."""

    if original is not None:
        from mlagents.trainers.buffer import RewardSignalUtil

        RewardSignalUtil.value_estimates_key = staticmethod(original)

    restore_adaptive_exploration()
    restore_inactive_continuous_action_masking()
    restore_continuous_sigma_guard()
