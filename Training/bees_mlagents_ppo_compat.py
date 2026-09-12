"""Narrow compatibility fixes and exploration guardrails for Bees PPO training."""

from __future__ import annotations

import math
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
_ORIGINAL_GAUSSIAN_FORWARD = None
_ORIGINAL_PPO_CREATE_OPTIMIZER = None
_ORIGINAL_PPO_PROCESS_TRAJECTORY = None


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

    This installer also enables the continuous-sigma guard and reward-responsive
    beta controller. Keeping all three behind the launcher's existing compatibility
    hook avoids changing the policy ABI or requiring a fork of ML-Agents.

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
        install_adaptive_exploration()
    except Exception:
        restore_adaptive_exploration()
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
    restore_continuous_sigma_guard()
