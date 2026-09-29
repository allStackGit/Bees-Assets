"""Narrow compatibility fixes and exploration guardrails for Bees PPO/MA-POCA training."""

from __future__ import annotations

import math
import threading
from typing import Callable, Optional


VALUE_KEY_PROBE = "__bees_value_key_probe__"
MAX_CONTINUOUS_SIGMA = 1.5
BEES_MOVEMENT_CONTINUOUS_ACTIONS = 2
BEES_WEAPON_SLOTS = 5
BEES_WEAPON_AIM_ACTIONS_PER_SLOT = 2
BEES_COMMUNICATION_CONTINUOUS_ACTIONS = 4
BEES_CONTINUOUS_ACTIONS = (
    BEES_MOVEMENT_CONTINUOUS_ACTIONS
    + BEES_WEAPON_SLOTS * BEES_WEAPON_AIM_ACTIONS_PER_SLOT
    + BEES_COMMUNICATION_CONTINUOUS_ACTIONS
)
BEES_DISCRETE_BRANCHES = (2,) * BEES_WEAPON_SLOTS + (5,)
BEES_OBSERVATION_SIZE = 7614
BEES_SELF_SHIP_TYPE_INDEX = 1
BEES_SELF_IS_MOBILE_INDEX = 16
BEES_CAPABILITY_START = 25
BEES_CAPABILITY_SPECIAL_PHASE_INDEX = BEES_CAPABILITY_START + 4
BEES_BARGE_SHIP_TYPE_SCALAR = -1.0 / 23.0
BEES_HEALING_SPECIAL_ACTION = 3
ACTION_ENTROPY_EPSILON = 1e-7

_ORIGINAL_GAUSSIAN_FORWARD = None
_ORIGINAL_ACTION_MODEL_FORWARD = None
_ORIGINAL_ACTION_MODEL_EVALUATE = None
_ORIGINAL_PPO_UPDATE = None
_ORIGINAL_POCA_UPDATE = None
_ORIGINAL_POCA_UPDATE_POLICY = None
_ORIGINAL_TRUST_REGION_POLICY_LOSS = None
_ORIGINAL_MASKED_MEAN = None
_ORIGINAL_BC_UPDATE_BATCH = None
_ORIGINAL_BC_LOSS = None
_ORIGINAL_PPO_CREATE_OPTIMIZER = None
_ORIGINAL_PPO_PROCESS_TRAJECTORY = None
_POLICY_DIMENSION_MASK_STATE = threading.local()
_BC_MASK_STATE = threading.local()


def _is_bees_action_spec(action_spec) -> bool:
    if action_spec is None or action_spec.continuous_size != BEES_CONTINUOUS_ACTIONS:
        return False
    return tuple(int(size) for size in action_spec.discrete_branches) == BEES_DISCRETE_BRANCHES


def _build_bees_continuous_activity_mask(action_spec, masks, reference):
    """Return 1 for continuous actions that physically exist for each agent sample.

    The first two continuous actions are movement. Each of the five weapon slots
    reserves two aim outputs. Unity masks the Fire action of a weapon branch when
    that slot has no turret, making the matching discrete mask a reliable per-sample
    signal for whether that aim pair can affect the environment.
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


def _bees_movement_activity(policy, batch):
    """Return 1 where the ship can use the policy movement outputs."""

    import numpy as np
    from mlagents.trainers.trajectory import ObsUtil

    if not _is_bees_action_spec(policy.behavior_spec.action_spec):
        return None
    if len(policy.behavior_spec.observation_specs) != 1:
        return None

    observations = ObsUtil.from_buffer(batch, 1)
    raw = np.asarray(observations[0].to_ndarray(), dtype=np.float32)
    if raw.ndim != 2 or raw.shape[1] != BEES_OBSERVATION_SIZE:
        return None

    mobile = raw[:, BEES_SELF_IS_MOBILE_INDEX] > 0.5
    # Barge charge phases 1-3 all lock ordinary movement orders. The charge phase
    # is already part of the frozen capability observation, so this requires no ABI change.
    is_barge = np.isclose(
        raw[:, BEES_SELF_SHIP_TYPE_INDEX],
        BEES_BARGE_SHIP_TYPE_SCALAR,
        rtol=0.0,
        atol=1.0e-5,
    )
    barge_charge_active = (
        is_barge
        & (raw[:, BEES_CAPABILITY_SPECIAL_PHASE_INDEX] > 1.0e-6)
    )
    return (mobile & ~barge_charge_active).astype(np.float32)


def _bees_bc_weapon_activity(policy, mini_batch):
    """Return per-sample turret activity from the frozen self-weapon observation slots."""

    import numpy as np
    from mlagents.trainers.trajectory import ObsUtil
    from bees_mlagents_structured_policy import (
        BEES_OBSERVATION_SIZE,
        SELF_WEAPON_SIZE,
        SELF_WEAPON_START,
    )

    if not _is_bees_action_spec(policy.behavior_spec.action_spec):
        return None
    if len(policy.behavior_spec.observation_specs) != 1:
        return None

    observations = ObsUtil.from_buffer(mini_batch, 1)
    raw = np.asarray(observations[0].to_ndarray(), dtype=np.float32)
    if raw.ndim != 2 or raw.shape[1] != BEES_OBSERVATION_SIZE:
        return None

    # SelfWeaponObservation index 10 is the explicit "is turret" flag. Only turrets
    # have aim/fire actions; other weapon objects occupying a slot must remain inactive.
    activity = np.zeros((raw.shape[0], BEES_WEAPON_SLOTS), dtype=np.float32)
    for slot in range(BEES_WEAPON_SLOTS):
        turret_index = SELF_WEAPON_START + slot * SELF_WEAPON_SIZE + 10
        activity[:, slot] = (raw[:, turret_index] > 0.5).astype(np.float32)
    return activity


def _bees_masked_behavioral_cloning_loss(
    policy,
    selected_actions,
    log_probs,
    expert_actions,
    weapon_activity,
    movement_activity=None,
):
    """BC loss that excludes movement/weapon actions with no physical effect."""

    from mlagents.torch_utils import torch
    from mlagents.trainers.torch_entities.utils import ModelUtils

    action_spec = policy.behavior_spec.action_spec
    if weapon_activity is None or not _is_bees_action_spec(action_spec):
        return None

    reference = (
        selected_actions.continuous_tensor
        if action_spec.continuous_size > 0
        else log_probs.all_discrete_tensor
    )
    activity = torch.as_tensor(
        weapon_activity,
        dtype=reference.dtype,
        device=reference.device,
    )

    loss = reference.new_tensor(0.0)
    if action_spec.continuous_size > 0:
        continuous_mask = torch.ones_like(selected_actions.continuous_tensor)
        if movement_activity is not None:
            movement = torch.as_tensor(
                movement_activity,
                dtype=reference.dtype,
                device=reference.device,
            )
            if movement.shape[0] == continuous_mask.shape[0]:
                continuous_mask[:, :BEES_MOVEMENT_CONTINUOUS_ACTIONS] = (
                    movement.unsqueeze(1)
                )
        for slot in range(BEES_WEAPON_SLOTS):
            aim_start = (
                BEES_MOVEMENT_CONTINUOUS_ACTIONS
                + slot * BEES_WEAPON_AIM_ACTIONS_PER_SLOT
            )
            continuous_mask[
                :,
                aim_start : aim_start + BEES_WEAPON_AIM_ACTIONS_PER_SLOT,
            ] = activity[:, slot : slot + 1]

        # Passive Human/HiveMind recorders have no communication control surface and
        # intentionally leave these RL-private channels at zero. Zero is not an expert
        # message, so never use demonstration loss to suppress emergent communication.
        communication_start = (
            BEES_MOVEMENT_CONTINUOUS_ACTIONS
            + BEES_WEAPON_SLOTS * BEES_WEAPON_AIM_ACTIONS_PER_SLOT
        )
        continuous_mask[
            :,
            communication_start :
            communication_start + BEES_COMMUNICATION_CONTINUOUS_ACTIONS,
        ] = 0.0

        squared_error = (
            selected_actions.continuous_tensor - expert_actions.continuous_tensor
        ) ** 2
        loss = loss + (squared_error * continuous_mask).sum() / torch.clamp(
            continuous_mask.sum(),
            min=1.0,
        )

    if action_spec.discrete_size > 0:
        one_hot_expert_actions = ModelUtils.actions_to_onehot(
            expert_actions.discrete_tensor,
            action_spec.discrete_branches,
        )
        log_prob_branches = ModelUtils.break_into_branches(
            log_probs.all_discrete_tensor,
            action_spec.discrete_branches,
        )
        branch_losses = []
        expert_special = expert_actions.discrete_tensor[:, BEES_WEAPON_SLOTS]
        fire_effective = (
            expert_special != BEES_HEALING_SPECIAL_ACTION
        ).to(reference.dtype)
        for branch_index, (log_prob_branch, expert_branch) in enumerate(
            zip(log_prob_branches, one_hot_expert_actions)
        ):
            per_sample = torch.sum(
                -torch.nn.functional.log_softmax(log_prob_branch, dim=1)
                * expert_branch,
                dim=1,
            )
            if branch_index < BEES_WEAPON_SLOTS:
                branch_activity = (
                    activity[:, branch_index] * fire_effective
                )
                active_count = branch_activity.sum()
                if float(active_count.detach().cpu().item()) <= 0.0:
                    continue
                branch_losses.append(
                    (per_sample * branch_activity).sum()
                    / torch.clamp(active_count, min=1.0)
                )
            else:
                # Ordinary passive demonstrations intentionally write NoSpecialAction.
                # ML-Agents 1.1.0 demo buffers do not preserve action masks, so the
                # authoritative signal is the nonzero action itself: only explicit
                # capability-event demonstrations supervise this branch.
                special = (
                    expert_special != 0
                ).to(per_sample.dtype)
                active_count = special.sum()
                if float(active_count.detach().cpu().item()) <= 0.0:
                    continue
                branch_losses.append(
                    (per_sample * special).sum()
                    / torch.clamp(active_count, min=1.0)
                )

        if branch_losses:
            loss = loss + torch.mean(torch.stack(branch_losses))

    return loss


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
        policy_dimension_mask = getattr(
            _POLICY_DIMENSION_MASK_STATE,
            "mask",
            None,
        )
        if (
            activity is not None
            and policy_dimension_mask is not None
            and policy_dimension_mask.shape[0] == activity.shape[0]
            and policy_dimension_mask.shape[1] >= BEES_CONTINUOUS_ACTIONS
        ):
            activity = activity * policy_dimension_mask[
                :, :BEES_CONTINUOUS_ACTIONS
            ].to(activity.dtype)
        if activity is None:
            entropies.append(dists.continuous.entropy())
        else:
            continuous_log_prob = continuous_log_prob * activity
            per_dimension_entropy = 0.5 * torch.log(
                2 * math.pi * math.e * dists.continuous.std**2
                + ACTION_ENTROPY_EPSILON
            )
            entropies.append(
                (per_dimension_entropy * activity).sum(dim=1, keepdim=True)
            )

    if dists.discrete is not None:
        discrete_log_probs = []
        all_discrete_log_probs = []
        policy_dimension_mask = getattr(
            _POLICY_DIMENSION_MASK_STATE,
            "mask",
            None,
        )
        for branch_index, (discrete_action, discrete_dist) in enumerate(zip(
            actions.discrete_list,
            dists.discrete,
        )):
            discrete_log_probs.append(discrete_dist.log_prob(discrete_action))
            all_discrete_log_probs.append(discrete_dist.all_log_prob())
            branch_entropy = discrete_dist.entropy()
            if (
                policy_dimension_mask is not None
                and policy_dimension_mask.shape[0] == branch_entropy.shape[0]
                and policy_dimension_mask.shape[1]
                >= BEES_CONTINUOUS_ACTIONS + action_model.action_spec.discrete_size
            ):
                branch_activity = policy_dimension_mask[
                    :,
                    BEES_CONTINUOUS_ACTIONS + branch_index,
                ].to(branch_entropy.dtype)
                branch_entropy = branch_entropy * branch_activity.unsqueeze(1)
            entropies.append(branch_entropy)

    action_log_probs = ActionLogProbs(
        continuous_log_prob,
        discrete_log_probs,
        all_discrete_log_probs,
    )
    entropy_sum = torch.sum(torch.cat(entropies, dim=1), dim=1)
    return action_log_probs, entropy_sum


def _build_bees_policy_dimension_mask(
    action_spec,
    action_masks,
    movement_activity=None,
    discrete_actions=None,
    communication_activity=None,
):
    """Build PPO loss weights that omit action dimensions with no physical effect."""

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
    if (
        movement_activity is not None
        and movement_activity.shape[0] == continuous_activity.shape[0]
    ):
        continuous_activity[:, :BEES_MOVEMENT_CONTINUOUS_ACTIONS] = (
            movement_activity.to(
                dtype=continuous_activity.dtype,
                device=continuous_activity.device,
            ).unsqueeze(1)
        )
    if (
        communication_activity is not None
        and communication_activity.shape[0] == continuous_activity.shape[0]
    ):
        communication_start = (
            BEES_MOVEMENT_CONTINUOUS_ACTIONS
            + BEES_WEAPON_SLOTS * BEES_WEAPON_AIM_ACTIONS_PER_SLOT
        )
        continuous_activity[
            :,
            communication_start :
            communication_start + BEES_COMMUNICATION_CONTINUOUS_ACTIONS,
        ] = communication_activity.to(
            dtype=continuous_activity.dtype,
            device=continuous_activity.device,
        ).unsqueeze(1)

    # A masked fire branch has only "cease" available, so its selected log-probability
    # is effectively constant and has zero gradient. Exclude it from the loss denominator
    # as well; otherwise ships with fewer turrets receive systematically smaller updates.
    discrete_activity = torch.ones(
        (action_masks.shape[0], action_spec.discrete_size),
        dtype=continuous_activity.dtype,
        device=continuous_activity.device,
    )
    fire_effective = None
    if (
        discrete_actions is not None
        and discrete_actions.ndim == 2
        and discrete_actions.shape[0] == action_masks.shape[0]
        and discrete_actions.shape[1] >= len(BEES_DISCRETE_BRANCHES)
    ):
        fire_effective = (
            discrete_actions[:, BEES_WEAPON_SLOTS]
            != BEES_HEALING_SPECIAL_ACTION
        ).to(discrete_activity.dtype)

    for slot in range(BEES_WEAPON_SLOTS):
        fire_action_index = slot * 2 + 1
        slot_activity = (
            action_masks[:, fire_action_index] > 0.5
        ).to(discrete_activity.dtype)
        if fire_effective is not None:
            slot_activity = slot_activity * fire_effective
        discrete_activity[:, slot] = slot_activity

    special_start = sum(BEES_DISCRETE_BRANCHES[:-1])
    discrete_activity[:, BEES_WEAPON_SLOTS] = (
        torch.sum(
            action_masks[
                :,
                special_start + 1 :
                special_start + BEES_DISCRETE_BRANCHES[-1],
            ],
            dim=1,
        ) > 0.5
    ).to(discrete_activity.dtype)
    return torch.cat((continuous_activity, discrete_activity), dim=1)


def _trust_region_policy_loss_with_dimension_mask(
    advantages,
    log_probs,
    old_log_probs,
    loss_masks,
    epsilon,
    dimension_mask,
    sample_weights=None,
):
    """PPO/POCA loss with inactive actions and optional group-size weighting."""

    from mlagents.torch_utils import torch

    advantage = advantages.unsqueeze(-1)
    r_theta = torch.exp(log_probs - old_log_probs)
    p_opt_a = r_theta * advantage
    p_opt_b = torch.clamp(r_theta, 1.0 - epsilon, 1.0 + epsilon) * advantage
    element_loss = -torch.min(p_opt_a, p_opt_b)

    valid_steps = loss_masks.to(element_loss.dtype).unsqueeze(-1)
    valid_dimensions = dimension_mask.to(element_loss.dtype)
    weights = valid_steps * valid_dimensions
    if sample_weights is not None and sample_weights.shape[0] == weights.shape[0]:
        weights = weights * sample_weights.to(element_loss.dtype).unsqueeze(-1)
    return (element_loss * weights).sum() / torch.clamp(weights.sum(), min=1.0)


def _poca_communication_activity(policy, batch, batch_size):
    """Return 1 where at least one live MA-POCA groupmate can receive communication."""

    import numpy as np
    from mlagents.trainers.trajectory import GroupObsUtil

    n_obs = len(policy.behavior_spec.observation_specs)
    if n_obs <= 0 or batch_size <= 0:
        return None

    groupmate_obs = GroupObsUtil.from_buffer(batch, n_obs)
    active = np.zeros(batch_size, dtype=np.float32)
    for groupmate in groupmate_obs:
        if not groupmate:
            continue
        first_obs = np.asarray(groupmate[0])
        if first_obs.shape[0] != batch_size:
            return None
        first_value = first_obs.reshape(batch_size, -1)[:, 0]
        active = np.maximum(
            active,
            (~np.isnan(first_value)).astype(np.float32),
        )
    return active


def _poca_inverse_group_size_weight_array(policy, batch, batch_size):
    """Return exact 1/N active-group weights as a NumPy vector."""

    import numpy as np
    from mlagents.trainers.buffer import BufferKey
    from mlagents.trainers.trajectory import GroupObsUtil

    n_obs = len(policy.behavior_spec.observation_specs)
    if n_obs <= 0 or batch_size <= 0:
        return None
    groupmate_obs = GroupObsUtil.from_buffer(batch, n_obs)

    group_size = np.ones(batch_size, dtype=np.float32)
    for groupmate in groupmate_obs:
        if not groupmate:
            continue
        first_obs = np.asarray(groupmate[0])
        if first_obs.shape[0] != batch_size:
            return None
        first_value = first_obs.reshape(batch_size, -1)[:, 0]
        group_size += (~np.isnan(first_value)).astype(np.float32)

    weights = 1.0 / np.maximum(group_size, 1.0)
    loss_masks = np.asarray(batch[BufferKey.MASKS].get_batch(), dtype=np.float32)
    if loss_masks.shape[0] == batch_size:
        weights *= (loss_masks > 0.0).astype(np.float32)
    return weights


def _poca_inverse_group_size_weights(policy, batch, reference):
    """Weight each agent sample so one team-timestep has roughly unit total weight.

    MA-POCA exposes each sample's groupmates explicitly. A timestep with N active
    agents therefore contributes N actor samples. Giving each sample weight 1/N
    prevents large fleets from overwhelming updates merely because they contain
    more policy-controlled ships.
    """

    weights = _poca_inverse_group_size_weight_array(
        policy,
        batch,
        int(reference.shape[0]),
    )
    return None if weights is None else reference.new_tensor(weights)


def _normalize_poca_advantages(policy, batch):
    """Standardize advantages with the same 1/N team-timestep weighting as the loss."""

    import numpy as np
    from mlagents.trainers.buffer import BufferKey

    advantages = np.asarray(
        batch[BufferKey.ADVANTAGES].get_batch(),
        dtype=np.float32,
    )
    if advantages.size == 0:
        return advantages

    weights = _poca_inverse_group_size_weight_array(
        policy,
        batch,
        int(advantages.shape[0]),
    )
    if weights is None:
        weights = np.ones(advantages.shape[0], dtype=np.float32)

    weight_sum = float(weights.sum())
    if weight_sum <= 0.0:
        normalized = np.zeros_like(advantages, dtype=np.float32)
    else:
        mean = float(np.sum(weights * advantages) / weight_sum)
        centered = advantages - mean
        variance = float(np.sum(weights * centered * centered) / weight_sum)
        normalized = centered / (math.sqrt(max(0.0, variance)) + 1e-10)

    batch[BufferKey.ADVANTAGES].set(normalized)
    return normalized


def install_inactive_continuous_action_masking() -> Optional[Callable]:
    """Mask nonexistent weapon actions and normalize MA-POCA fleet-size gradients."""

    from mlagents.trainers.buffer import BufferKey
    from mlagents.trainers.poca.optimizer_torch import TorchPOCAOptimizer
    from mlagents.trainers.poca.trainer import POCATrainer
    from mlagents.trainers.torch_entities.components.bc.module import BCModule
    from mlagents.trainers.ppo.optimizer_torch import TorchPPOOptimizer
    from mlagents.trainers.torch_entities.action_model import ActionModel
    from mlagents.trainers.torch_entities.utils import ModelUtils

    global _ORIGINAL_ACTION_MODEL_FORWARD
    global _ORIGINAL_ACTION_MODEL_EVALUATE
    global _ORIGINAL_PPO_UPDATE
    global _ORIGINAL_POCA_UPDATE
    global _ORIGINAL_POCA_UPDATE_POLICY
    global _ORIGINAL_TRUST_REGION_POLICY_LOSS
    global _ORIGINAL_MASKED_MEAN
    global _ORIGINAL_BC_UPDATE_BATCH
    global _ORIGINAL_BC_LOSS

    if _ORIGINAL_ACTION_MODEL_FORWARD is not None:
        return None

    original_forward = ActionModel.forward
    original_evaluate = ActionModel.evaluate
    original_ppo_update = TorchPPOOptimizer.update
    original_poca_update = TorchPOCAOptimizer.update
    original_poca_update_policy = POCATrainer._update_policy
    original_policy_loss = ModelUtils.trust_region_policy_loss
    original_masked_mean = ModelUtils.masked_mean
    original_bc_update_batch = BCModule._update_batch
    original_bc_loss = BCModule._behavioral_cloning_loss

    def masked_bc_update_batch(self, mini_batch_demo, n_sequences):
        _BC_MASK_STATE.weapon_activity = _bees_bc_weapon_activity(
            self.policy,
            mini_batch_demo,
        )
        _BC_MASK_STATE.movement_activity = _bees_movement_activity(
            self.policy,
            mini_batch_demo,
        )
        try:
            return original_bc_update_batch(self, mini_batch_demo, n_sequences)
        finally:
            _BC_MASK_STATE.weapon_activity = None
            _BC_MASK_STATE.movement_activity = None

    def masked_bc_loss(self, selected_actions, log_probs, expert_actions):
        masked = _bees_masked_behavioral_cloning_loss(
            self.policy,
            selected_actions,
            log_probs,
            expert_actions,
            getattr(_BC_MASK_STATE, "weapon_activity", None),
            getattr(_BC_MASK_STATE, "movement_activity", None),
        )
        if masked is not None:
            return masked
        return original_bc_loss(
            self,
            selected_actions,
            log_probs,
            expert_actions,
        )

    def weighted_poca_update_policy(self):
        """ML-Agents 1.1.0 on-policy update with team-timestep-weighted advantages."""

        import numpy as np
        from collections import defaultdict
        from mlagents.trainers.buffer import BufferKey

        buffer_length = self.update_buffer.num_experiences
        self.cumulative_returns_since_policy_update.clear()

        batch_size = (
            self.hyperparameters.batch_size
            - self.hyperparameters.batch_size % self.policy.sequence_length
        )
        batch_size = max(batch_size, self.policy.sequence_length)
        n_sequences = max(
            int(self.hyperparameters.batch_size / self.policy.sequence_length),
            1,
        )

        _normalize_poca_advantages(self.policy, self.update_buffer)

        num_epoch = self.hyperparameters.num_epoch
        batch_update_stats = defaultdict(list)
        for _ in range(num_epoch):
            self.update_buffer.shuffle(
                sequence_length=self.policy.sequence_length
            )
            buffer = self.update_buffer
            max_num_batch = buffer_length // batch_size
            for i in range(0, max_num_batch * batch_size, batch_size):
                minibatch = buffer.make_mini_batch(i, i + batch_size)
                update_stats = self.optimizer.update(minibatch, n_sequences)
                update_stats.update(
                    self.optimizer.update_reward_signals(minibatch)
                )
                for stat_name, value in update_stats.items():
                    batch_update_stats[stat_name].append(value)

        for stat, stat_list in batch_update_stats.items():
            self._stats_reporter.add_stat(stat, np.mean(stat_list))

        if self.optimizer.bc_module:
            update_stats = self.optimizer.bc_module.update()
            for stat, val in update_stats.items():
                self._stats_reporter.add_stat(stat, val)
        self._clear_update_buffer()
        return True

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

    def weighted_masked_mean(tensor, masks):
        sample_weights = getattr(
            _POLICY_DIMENSION_MASK_STATE,
            "sample_weights",
            None,
        )
        if (
            sample_weights is None
            or masks is None
            or sample_weights.shape[0] != masks.shape[0]
        ):
            return original_masked_mean(tensor, masks)
        effective_masks = masks.to(sample_weights.dtype) * sample_weights
        return original_masked_mean(tensor, effective_masks)

    def masked_policy_loss(
        advantages,
        log_probs,
        old_log_probs,
        loss_masks,
        epsilon,
    ):
        dimension_mask = getattr(_POLICY_DIMENSION_MASK_STATE, "mask", None)
        sample_weights = getattr(
            _POLICY_DIMENSION_MASK_STATE,
            "sample_weights",
            None,
        )
        if dimension_mask is None or tuple(dimension_mask.shape) != tuple(log_probs.shape):
            if sample_weights is None:
                return original_policy_loss(
                    advantages,
                    log_probs,
                    old_log_probs,
                    loss_masks,
                    epsilon,
                )
            from mlagents.torch_utils import torch

            advantage = advantages.unsqueeze(-1)
            r_theta = torch.exp(log_probs - old_log_probs)
            p_opt_a = r_theta * advantage
            p_opt_b = torch.clamp(
                r_theta,
                1.0 - epsilon,
                1.0 + epsilon,
            ) * advantage
            return -weighted_masked_mean(
                torch.min(p_opt_a, p_opt_b),
                loss_masks,
            )
        return _trust_region_policy_loss_with_dimension_mask(
            advantages,
            log_probs,
            old_log_probs,
            loss_masks,
            epsilon,
            dimension_mask,
            sample_weights=sample_weights,
        )

    def _set_dimension_mask(
        optimizer,
        batch,
        communication_activity=None,
    ):
        action_masks = ModelUtils.list_to_tensor(batch[BufferKey.ACTION_MASK])
        movement_activity = _bees_movement_activity(
            optimizer.policy,
            batch,
        )
        if movement_activity is not None:
            movement_activity = action_masks.new_tensor(movement_activity)
        discrete_actions = ModelUtils.list_to_tensor(
            batch[BufferKey.DISCRETE_ACTION]
        )
        if communication_activity is not None:
            communication_activity = action_masks.new_tensor(
                communication_activity
            )
        dimension_mask = _build_bees_policy_dimension_mask(
            optimizer.policy.behavior_spec.action_spec,
            action_masks,
            movement_activity=movement_activity,
            discrete_actions=discrete_actions,
            communication_activity=communication_activity,
        )
        _POLICY_DIMENSION_MASK_STATE.mask = dimension_mask
        return action_masks

    def masked_ppo_update(self, batch, num_sequences):
        _set_dimension_mask(self, batch)
        _POLICY_DIMENSION_MASK_STATE.sample_weights = None
        try:
            return original_ppo_update(self, batch, num_sequences)
        finally:
            _POLICY_DIMENSION_MASK_STATE.mask = None
            _POLICY_DIMENSION_MASK_STATE.sample_weights = None

    def masked_poca_update(self, batch, num_sequences):
        communication_activity = _poca_communication_activity(
            self.policy,
            batch,
            len(batch[BufferKey.MASKS]),
        )
        action_masks = _set_dimension_mask(
            self,
            batch,
            communication_activity=communication_activity,
        )
        _POLICY_DIMENSION_MASK_STATE.sample_weights = (
            _poca_inverse_group_size_weights(
                self.policy,
                batch,
                action_masks,
            )
        )
        try:
            return original_poca_update(self, batch, num_sequences)
        finally:
            _POLICY_DIMENSION_MASK_STATE.mask = None
            _POLICY_DIMENSION_MASK_STATE.sample_weights = None

    ActionModel.forward = masked_forward
    ActionModel.evaluate = masked_evaluate
    TorchPPOOptimizer.update = masked_ppo_update
    TorchPOCAOptimizer.update = masked_poca_update
    POCATrainer._update_policy = weighted_poca_update_policy
    ModelUtils.trust_region_policy_loss = staticmethod(masked_policy_loss)
    ModelUtils.masked_mean = staticmethod(weighted_masked_mean)
    BCModule._update_batch = masked_bc_update_batch
    BCModule._behavioral_cloning_loss = masked_bc_loss

    _ORIGINAL_ACTION_MODEL_FORWARD = original_forward
    _ORIGINAL_ACTION_MODEL_EVALUATE = original_evaluate
    _ORIGINAL_PPO_UPDATE = original_ppo_update
    _ORIGINAL_POCA_UPDATE = original_poca_update
    _ORIGINAL_POCA_UPDATE_POLICY = original_poca_update_policy
    _ORIGINAL_TRUST_REGION_POLICY_LOSS = original_policy_loss
    _ORIGINAL_MASKED_MEAN = original_masked_mean
    _ORIGINAL_BC_UPDATE_BATCH = original_bc_update_batch
    _ORIGINAL_BC_LOSS = original_bc_loss
    return original_forward


def restore_inactive_continuous_action_masking() -> None:
    """Restore ML-Agents action statistics and optimizer methods."""

    global _ORIGINAL_ACTION_MODEL_FORWARD
    global _ORIGINAL_ACTION_MODEL_EVALUATE
    global _ORIGINAL_PPO_UPDATE
    global _ORIGINAL_POCA_UPDATE
    global _ORIGINAL_POCA_UPDATE_POLICY
    global _ORIGINAL_TRUST_REGION_POLICY_LOSS
    global _ORIGINAL_MASKED_MEAN
    global _ORIGINAL_BC_UPDATE_BATCH
    global _ORIGINAL_BC_LOSS

    if _ORIGINAL_ACTION_MODEL_FORWARD is None:
        return

    from mlagents.trainers.poca.optimizer_torch import TorchPOCAOptimizer
    from mlagents.trainers.poca.trainer import POCATrainer
    from mlagents.trainers.ppo.optimizer_torch import TorchPPOOptimizer
    from mlagents.trainers.torch_entities.components.bc.module import BCModule
    from mlagents.trainers.torch_entities.action_model import ActionModel
    from mlagents.trainers.torch_entities.utils import ModelUtils

    ActionModel.forward = _ORIGINAL_ACTION_MODEL_FORWARD
    ActionModel.evaluate = _ORIGINAL_ACTION_MODEL_EVALUATE
    TorchPPOOptimizer.update = _ORIGINAL_PPO_UPDATE
    TorchPOCAOptimizer.update = _ORIGINAL_POCA_UPDATE
    POCATrainer._update_policy = _ORIGINAL_POCA_UPDATE_POLICY
    ModelUtils.trust_region_policy_loss = staticmethod(
        _ORIGINAL_TRUST_REGION_POLICY_LOSS
    )
    ModelUtils.masked_mean = staticmethod(_ORIGINAL_MASKED_MEAN)
    BCModule._update_batch = _ORIGINAL_BC_UPDATE_BATCH
    BCModule._behavioral_cloning_loss = _ORIGINAL_BC_LOSS
    _POLICY_DIMENSION_MASK_STATE.mask = None
    _BC_MASK_STATE.weapon_activity = None
    _BC_MASK_STATE.movement_activity = None
    _POLICY_DIMENSION_MASK_STATE.sample_weights = None

    _ORIGINAL_ACTION_MODEL_FORWARD = None
    _ORIGINAL_ACTION_MODEL_EVALUATE = None
    _ORIGINAL_PPO_UPDATE = None
    _ORIGINAL_POCA_UPDATE = None
    _ORIGINAL_POCA_UPDATE_POLICY = None
    _ORIGINAL_TRUST_REGION_POLICY_LOSS = None
    _ORIGINAL_MASKED_MEAN = None
    _ORIGINAL_BC_UPDATE_BATCH = None
    _ORIGINAL_BC_LOSS = None


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


def install_value_estimate_key_fix() -> Optional[Callable[[str], object]]:
    """Install Bees' ML-Agents 1.1.0 PPO compatibility fixes.

    ML-Agents 1.1.0 defines RewardSignalUtil.value_estimates_key() using the
    RETURNS prefix, so PPO overwrites the old critic predictions with calculated
    returns before the optimizer can use value clipping correctly. The Bees
    launcher already pins/guards ML-Agents 1.1.0; this adds a second structural
    guard so an unexpected vendor change cannot be patched silently.

    This installer also enables the continuous-sigma guard and inactive continuous
    weapon-action masking. PPO beta is left entirely to ML-Agents' configured
    constant beta schedule so exploration pressure can be adjusted manually.

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
    except Exception:
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

    restore_inactive_continuous_action_masking()
    restore_continuous_sigma_guard()
