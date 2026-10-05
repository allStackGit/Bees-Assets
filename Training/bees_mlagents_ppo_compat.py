"""Narrow compatibility fixes and exploration guardrails for Bees PPO/MA-POCA training."""

from __future__ import annotations

import math
import os
import shutil
import subprocess
import threading
import time
from dataclasses import dataclass
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
BEES_OBSERVATION_SIZE = 7743
BEES_SELF_SHIP_TYPE_INDEX = 1
BEES_SELF_IS_MOBILE_INDEX = 16
BEES_CAPABILITY_START = 25
BEES_CAPABILITY_SPECIAL_PHASE_INDEX = BEES_CAPABILITY_START + 4
BEES_BARGE_SHIP_TYPE_SCALAR = -1.0 / 23.0
BEES_HEALING_SPECIAL_ACTION = 3
ACTION_ENTROPY_EPSILON = 1e-7
POCA_ENCODER_CHUNK_ROWS = 2048
POCA_TRAJECTORY_BATCH_MAX_EXPERIENCES = 2048
POCA_TRAJECTORY_TIMING_REPORT_SECONDS = 30.0
POCA_PACKED_GROUP_CACHE_MAX_BYTES = 8 * 1024 * 1024 * 1024
POCA_PACKED_GROUP_CACHE_MIN_RAM_RESERVE_BYTES = 6 * 1024 * 1024 * 1024
POCA_GPU_CACHE_MIN_RESERVE_BYTES = 2 * 1024 * 1024 * 1024
POCA_GPU_CACHE_RESERVE_FRACTION = 0.30
POCA_CPU_PREPARE_RESERVED_LOGICAL_CPUS = 2
POCA_DEDUP_MIN_SAVED_ROWS = 8
POCA_HARDWARE_SAMPLE_SECONDS = 2.0
POCA_VARIABILITY_HISTORY_LIMIT = 64
_POCA_VARIABILITY_HISTORY = []


@dataclass(frozen=True)
class PocaLearnerOptimizationOptions:
    """Opt-in learner execution experiments; all preserve the logical PPO minibatch."""

    sync_cleanup: bool = False
    stream_shards: int = 1
    minibatch_prefetch: bool = False
    critic_baseline_overlap: bool = False
    cuda_graphs: bool = False

    @property
    def effective_sync_cleanup(self) -> bool:
        return bool(
            self.sync_cleanup
            or self.stream_shards > 1
            or self.critic_baseline_overlap
            or self.cuda_graphs
        )


_POCA_OPTIMIZATIONS = PocaLearnerOptimizationOptions()


def configure_poca_learner_optimizations(
    *,
    sync_cleanup: bool = False,
    stream_shards: int = 1,
    minibatch_prefetch: bool = False,
    critic_baseline_overlap: bool = False,
    cuda_graphs: bool = False,
) -> PocaLearnerOptimizationOptions:
    """Configure experimental execution optimizations before trainer construction."""

    global _POCA_OPTIMIZATIONS
    shards = int(stream_shards)
    if shards not in (1, 2, 4):
        raise ValueError("POCA stream_shards must be one of 1, 2, or 4.")
    _POCA_OPTIMIZATIONS = PocaLearnerOptimizationOptions(
        sync_cleanup=bool(sync_cleanup),
        stream_shards=shards,
        minibatch_prefetch=bool(minibatch_prefetch),
        critic_baseline_overlap=bool(critic_baseline_overlap),
        cuda_graphs=bool(cuda_graphs),
    )
    return _POCA_OPTIMIZATIONS


def poca_learner_optimization_options() -> PocaLearnerOptimizationOptions:
    return _POCA_OPTIMIZATIONS


_ORIGINAL_GAUSSIAN_FORWARD = None
_ORIGINAL_ACTION_MODEL_FORWARD = None
_ORIGINAL_ACTION_MODEL_EVALUATE = None
_ORIGINAL_PPO_UPDATE = None
_ORIGINAL_POCA_UPDATE = None
_ORIGINAL_POCA_TRAJECTORY_VALUES = None
_ORIGINAL_POCA_UPDATE_POLICY = None
_ORIGINAL_POCA_ADVANCE = None
_ORIGINAL_POCA_SAVE_MODEL = None
_ORIGINAL_MULTI_AGENT_FORWARD = None
_ORIGINAL_TRUST_REGION_POLICY_LOSS = None
_ORIGINAL_MASKED_MEAN = None
_ORIGINAL_BC_UPDATE_BATCH = None
_ORIGINAL_BC_LOSS = None
_ORIGINAL_PPO_CREATE_OPTIMIZER = None
_ORIGINAL_PPO_PROCESS_TRAJECTORY = None
_POLICY_DIMENSION_MASK_STATE = threading.local()
_BC_MASK_STATE = threading.local()
_POCA_TIMING_STATE = threading.local()
_POCA_GROUP_BATCH_STATE = threading.local()
_POCA_UPDATE_CACHE_STATE = threading.local()
_POCA_ASYNC_UPDATE_STATE = threading.local()
_POCA_UPDATE_BUSY_LOCK = threading.Lock()
_POCA_UPDATE_BUSY_SECONDS_TOTAL = 0.0
_POCA_TRAJECTORY_TIMING_LOCK = threading.Lock()
_POCA_TRAJECTORY_TIMING = {
    "last_report": 0.0,
    "batches": 0,
    "trajectories": 0,
    "experiences": 0,
    "prepare": 0.0,
    "evaluate": 0.0,
    "complete": 0.0,
    "total": 0.0,
}


def poca_update_busy_seconds_total() -> float:
    """Return cumulative wall time spent inside Bees POCA policy updates."""

    with _POCA_UPDATE_BUSY_LOCK:
        return float(_POCA_UPDATE_BUSY_SECONDS_TOTAL)


def _record_poca_update_busy_seconds(seconds: float) -> None:
    global _POCA_UPDATE_BUSY_SECONDS_TOTAL

    value = max(0.0, float(seconds))
    with _POCA_UPDATE_BUSY_LOCK:
        _POCA_UPDATE_BUSY_SECONDS_TOTAL += value


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


def _structured_training_slot_limits(policy, batch, extra_observations=()):
    """Find occupied structured-slot prefixes without padding MA-POCA group observations."""

    import numpy as np
    from mlagents.trainers.trajectory import GroupObsUtil, ObsUtil
    from bees_mlagents_structured_policy import (
        ALLY_COUNT,
        ALLY_SIZE,
        ALLY_START,
        BEES_OBSERVATION_SIZE,
        COLLISION_COUNT,
        COLLISION_SIZE,
        COLLISION_START,
        ENEMY_COUNT,
        ENEMY_SIZE,
        ENEMY_START,
        ENTITY_BASE_SIZE,
        ENTITY_WEAPON_COUNT,
        MAP_OBJECT_COUNT,
        MAP_OBJECT_SIZE,
        MAP_OBJECT_START,
        MINING_COUNT,
        MINING_SIZE,
        MINING_START,
        OBSERVED_WEAPON_SIZE,
        PARENT_SIZE,
        PARENT_START,
    )

    if len(policy.behavior_spec.observation_specs) != 1:
        return None

    current_obs = ObsUtil.from_buffer(batch, 1)
    current = np.asarray(current_obs[0].to_ndarray(), dtype=np.float32)
    if (
        current.ndim != 2
        or current.shape[0] <= 0
        or current.shape[1] != BEES_OBSERVATION_SIZE
    ):
        return None

    families = {
        "allies": (ALLY_START, ALLY_COUNT, ALLY_SIZE),
        "enemies": (ENEMY_START, ENEMY_COUNT, ENEMY_SIZE),
        "mining": (MINING_START, MINING_COUNT, MINING_SIZE),
        "map_objects": (MAP_OBJECT_START, MAP_OBJECT_COUNT, MAP_OBJECT_SIZE),
        "collisions": (COLLISION_START, COLLISION_COUNT, COLLISION_SIZE),
    }
    presence_indices = {
        name: np.asarray(
            [start + slot * size for slot in range(count)],
            dtype=np.int64,
        )
        for name, (start, count, size) in families.items()
    }
    weapon_presence_indices = []
    entity_families = (
        (PARENT_START, 1, PARENT_SIZE),
        (ALLY_START, ALLY_COUNT, ALLY_SIZE),
        (ENEMY_START, ENEMY_COUNT, ENEMY_SIZE),
    )
    for weapon_index in range(ENTITY_WEAPON_COUNT):
        indices = []
        for start, count, size in entity_families:
            indices.extend(
                start
                + slot * size
                + ENTITY_BASE_SIZE
                + weapon_index * OBSERVED_WEAPON_SIZE
                for slot in range(count)
            )
        weapon_presence_indices.append(np.asarray(indices, dtype=np.int64))

    highest = {name: 0 for name in families}
    entity_weapon_highest = 0

    def scan(values):
        nonlocal entity_weapon_highest
        candidate = np.asarray(values, dtype=np.float32)
        if candidate.ndim == 1:
            if candidate.shape[0] != BEES_OBSERVATION_SIZE:
                return
            candidate = candidate.reshape(1, -1)
        if candidate.ndim != 2 or candidate.shape[1] != BEES_OBSERVATION_SIZE:
            return

        for name, indices in presence_indices.items():
            presence = candidate[:, indices]
            occupied = np.any(
                np.isfinite(presence) & (presence > 0.0),
                axis=0,
            )
            occupied_indices = np.flatnonzero(occupied)
            if occupied_indices.size:
                highest[name] = max(
                    highest[name],
                    int(occupied_indices[-1]) + 1,
                )

        for weapon_index, indices in enumerate(weapon_presence_indices):
            presence = candidate[:, indices]
            if np.any(np.isfinite(presence) & (presence > 0.0)):
                entity_weapon_highest = max(
                    entity_weapon_highest,
                    weapon_index + 1,
                )

    scan(current)

    # Group observations are already stored as ragged lists on the CPU. Scan only
    # actual observations in small chunks instead of materializing a
    # [batch, max_group, 7743] padded representation merely to read presence bits.
    group_field = batch[GroupObsUtil.get_name_at(0)]
    group_chunk = []
    for group_entry in group_field:
        group_chunk.extend(group_entry)
        while len(group_chunk) >= 128:
            scan(np.asarray(group_chunk[:128], dtype=np.float32))
            del group_chunk[:128]
    if group_chunk:
        scan(np.asarray(group_chunk, dtype=np.float32))

    def add_extra(value):
        if isinstance(value, (list, tuple)):
            for item in value:
                add_extra(item)
            return
        scan(value)

    add_extra(extra_observations)

    return {
        "allies": max(1, min(ALLY_COUNT, highest["allies"])),
        "enemies": max(1, min(ENEMY_COUNT, highest["enemies"])),
        "entity_weapons": max(
            1,
            min(ENTITY_WEAPON_COUNT, entity_weapon_highest),
        ),
        "mining": max(1, min(MINING_COUNT, highest["mining"])),
        "map_objects": max(
            1,
            min(MAP_OBJECT_COUNT, highest["map_objects"]),
        ),
        "collisions": max(
            1,
            min(COLLISION_COUNT, highest["collisions"]),
        ),
    }


def _poca_groupmate_counts(policy, batch, batch_size):
    """Return raw MA-POCA groupmate counts without padding any observations."""

    import numpy as np
    from mlagents.trainers.trajectory import GroupObsUtil

    if len(policy.behavior_spec.observation_specs) != 1 or batch_size <= 0:
        return None

    group_field = batch[GroupObsUtil.get_name_at(0)]
    if len(group_field) != batch_size:
        return None

    try:
        counts = np.fromiter(
            (len(group_entry) for group_entry in group_field),
            dtype=np.int32,
            count=batch_size,
        )
    except TypeError:
        return None
    return counts


def _poca_groupmate_valid_row_indices(groupmate_counts):
    """Return valid minibatch rows for each raw MA-POCA groupmate position."""

    import numpy as np

    if groupmate_counts is None:
        return None
    counts = np.asarray(groupmate_counts, dtype=np.int32)
    if counts.ndim != 1:
        return None
    max_groupmates = int(counts.max()) if counts.size else 0
    return [
        np.flatnonzero(counts > position).astype(np.int64, copy=False)
        for position in range(max_groupmates)
    ]


def _poca_communication_activity(groupmate_counts):
    """Return 1 where at least one live MA-POCA groupmate can receive communication."""

    import numpy as np

    if groupmate_counts is None:
        return None
    counts = np.asarray(groupmate_counts, dtype=np.int32)
    if counts.ndim != 1:
        return None
    return (counts > 0).astype(np.float32, copy=False)


def _poca_inverse_group_size_weight_array(
    policy,
    batch,
    batch_size,
    groupmate_counts=None,
):
    """Return exact 1/N active-group weights as a NumPy vector."""

    import numpy as np
    from mlagents.trainers.buffer import BufferKey

    counts = (
        _poca_groupmate_counts(policy, batch, batch_size)
        if groupmate_counts is None
        else np.asarray(groupmate_counts, dtype=np.int32)
    )
    if counts is None or counts.ndim != 1 or counts.shape[0] != batch_size:
        return None

    weights = 1.0 / np.maximum(counts.astype(np.float32) + 1.0, 1.0)
    loss_masks = np.asarray(batch[BufferKey.MASKS].get_batch(), dtype=np.float32)
    if loss_masks.shape[0] == batch_size:
        weights *= (loss_masks > 0.0).astype(np.float32)
    return weights


def _poca_inverse_group_size_weights(
    policy,
    batch,
    reference,
    groupmate_counts=None,
):
    """Weight each sample so every active team-timestep contributes roughly unit weight."""

    weights = _poca_inverse_group_size_weight_array(
        policy,
        batch,
        int(reference.shape[0]),
        groupmate_counts=groupmate_counts,
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


def _poca_numeric_summary(values):
    clean = [float(value) for value in values if value is not None and math.isfinite(float(value))]
    if not clean:
        return {"count": 0, "avg": 0.0, "min": 0.0, "max": 0.0}
    return {
        "count": len(clean),
        "avg": sum(clean) / len(clean),
        "min": min(clean),
        "max": max(clean),
    }


def _poca_percentile(values, percentile):
    clean = sorted(
        float(value)
        for value in values
        if value is not None and math.isfinite(float(value))
    )
    if not clean:
        return 0.0
    if len(clean) == 1:
        return clean[0]
    position = (len(clean) - 1) * float(percentile) / 100.0
    lower = int(math.floor(position))
    upper = int(math.ceil(position))
    if lower == upper:
        return clean[lower]
    weight = position - lower
    return clean[lower] * (1.0 - weight) + clean[upper] * weight


def _poca_parse_nvidia_smi_sample(line):
    fields = [field.strip() for field in str(line).strip().split(",")]
    if len(fields) < 9:
        return None

    def number(value):
        text = str(value).strip()
        if not text or text.lower() in {"n/a", "[n/a]", "na"}:
            return None
        try:
            return float(text)
        except ValueError:
            return None

    return {
        "gpu_util": number(fields[0]),
        "memory_util": number(fields[1]),
        "memory_used_mib": number(fields[2]),
        "memory_total_mib": number(fields[3]),
        "temperature_c": number(fields[4]),
        "power_w": number(fields[5]),
        "graphics_clock_mhz": number(fields[6]),
        "memory_clock_mhz": number(fields[7]),
        "pstate": fields[8],
    }


class _PocaUpdateHardwareProfiler:
    """Low-overhead hardware sampler for the repeated PPO minibatch loop."""

    def __init__(self):
        self._stop = threading.Event()
        self._cpu_samples = []
        self._gpu_samples = []
        self._thread = None
        self._gpu_process = None
        self._process_cpu_start = 0.0
        self._cuda_start = {}
        self._psutil = None
        self._process = None
        self._nvidia_smi = self._find_nvidia_smi()

    @staticmethod
    def _find_nvidia_smi():
        resolved = shutil.which("nvidia-smi")
        if resolved:
            return resolved
        candidates = []
        if os.name == "nt":
            program_files = os.environ.get("ProgramW6432") or os.environ.get(
                "ProgramFiles",
                r"C:\Program Files",
            )
            system_root = os.environ.get("SystemRoot", r"C:\Windows")
            candidates.extend(
                [
                    os.path.join(
                        program_files,
                        "NVIDIA Corporation",
                        "NVSMI",
                        "nvidia-smi.exe",
                    ),
                    os.path.join(
                        system_root,
                        "System32",
                        "nvidia-smi.exe",
                    ),
                ]
            )
        for candidate in candidates:
            if os.path.isfile(candidate):
                return candidate
        return None

    def start(self):
        self._process_cpu_start = time.process_time()
        try:
            import psutil

            self._psutil = psutil
            self._process = psutil.Process(os.getpid())
            self._process.cpu_percent(None)
            psutil.cpu_percent(None, percpu=True)
        except (ImportError, OSError):
            self._psutil = None
            self._process = None

        try:
            from mlagents.torch_utils import torch

            if torch.cuda.is_available():
                device = int(torch.cuda.current_device())
                free_bytes, total_bytes = torch.cuda.mem_get_info(device)
                self._cuda_start = {
                    "device": device,
                    "allocated": int(torch.cuda.memory_allocated(device)),
                    "reserved": int(torch.cuda.memory_reserved(device)),
                    "free": int(free_bytes),
                    "total": int(total_bytes),
                }
                torch.cuda.reset_peak_memory_stats(device)
        except (ImportError, AttributeError, RuntimeError):
            self._cuda_start = {}

        self._thread = threading.Thread(
            target=self._sample_loop,
            name="bees-poca-hardware-profiler",
            daemon=True,
        )
        self._thread.start()
        return self

    def _sample_cpu(self):
        if self._psutil is None or self._process is None:
            return
        try:
            per_core = self._psutil.cpu_percent(None, percpu=True)
            memory = self._process.memory_info()
            system_memory = self._psutil.virtual_memory()
            context = self._process.num_ctx_switches()
            self._cpu_samples.append(
                {
                    "process_cpu": float(self._process.cpu_percent(None)),
                    "system_cpu": float(self._psutil.cpu_percent(None)),
                    "hottest_core": max(per_core) if per_core else 0.0,
                    "rss_mib": float(memory.rss) / (1024.0 * 1024.0),
                    "system_ram_percent": float(system_memory.percent),
                    "system_ram_available_mib": (
                        float(system_memory.available) / (1024.0 * 1024.0)
                    ),
                    "threads": float(self._process.num_threads()),
                    "ctx_voluntary": float(context.voluntary),
                    "ctx_involuntary": float(context.involuntary),
                    "swap_percent": float(self._psutil.swap_memory().percent),
                }
            )
        except (OSError, RuntimeError):
            return

    def _start_gpu_sampler(self):
        if not self._nvidia_smi:
            return None
        command = [
            self._nvidia_smi,
            "--query-gpu=utilization.gpu,utilization.memory,memory.used,memory.total,"
            "temperature.gpu,power.draw,clocks.current.graphics,clocks.current.memory,pstate",
            "--format=csv,noheader,nounits",
            "-l",
            str(max(1, int(POCA_HARDWARE_SAMPLE_SECONDS))),
        ]
        if self._cuda_start:
            command.extend(["-i", str(self._cuda_start.get("device", 0))])
        try:
            return subprocess.Popen(
                command,
                stdout=subprocess.PIPE,
                stderr=subprocess.DEVNULL,
                text=True,
                bufsize=1,
                creationflags=(
                    subprocess.CREATE_NO_WINDOW
                    if hasattr(subprocess, "CREATE_NO_WINDOW")
                    else 0
                ),
            )
        except OSError:
            return None

    @staticmethod
    def _terminate_gpu_process(process):
        if process is None or process.poll() is not None:
            return
        try:
            process.terminate()
            process.wait(timeout=1.0)
        except (OSError, subprocess.SubprocessError):
            try:
                process.kill()
            except OSError:
                pass

    def _sample_loop(self):
        self._gpu_process = self._start_gpu_sampler()
        process = self._gpu_process
        if process is None or process.stdout is None:
            while not self._stop.wait(POCA_HARDWARE_SAMPLE_SECONDS):
                self._sample_cpu()
            return

        try:
            while not self._stop.is_set():
                line = process.stdout.readline()
                if not line:
                    if process.poll() is not None:
                        break
                    continue
                sample = _poca_parse_nvidia_smi_sample(line)
                if sample is not None:
                    self._gpu_samples.append(sample)
                self._sample_cpu()
        finally:
            try:
                process.stdout.close()
            except OSError:
                pass
            self._terminate_gpu_process(process)

    def stop(self, wall_seconds):
        self._stop.set()
        process = self._gpu_process
        self._terminate_gpu_process(process)
        if self._thread is not None:
            self._thread.join(timeout=2.0)
            self._thread = None
        self._gpu_process = None

        process_cpu_seconds = max(
            0.0,
            time.process_time() - self._process_cpu_start,
        )
        result = {
            "process_cpu_seconds": process_cpu_seconds,
            "process_cpu_equivalent_percent": (
                process_cpu_seconds
                * 100.0
                / max(float(wall_seconds), 1e-9)
            ),
            "cpu_samples": len(self._cpu_samples),
            "gpu_samples": len(self._gpu_samples),
        }

        for key in (
            "process_cpu",
            "system_cpu",
            "hottest_core",
            "rss_mib",
            "system_ram_percent",
            "system_ram_available_mib",
            "threads",
            "swap_percent",
        ):
            summary = _poca_numeric_summary(
                sample.get(key) for sample in self._cpu_samples
            )
            result[f"{key}_avg"] = summary["avg"]
            result[f"{key}_min"] = summary["min"]
            result[f"{key}_max"] = summary["max"]

        if self._cpu_samples:
            first = self._cpu_samples[0]
            last = self._cpu_samples[-1]
            result["ctx_voluntary_delta"] = max(
                0.0,
                last["ctx_voluntary"] - first["ctx_voluntary"],
            )
            result["ctx_involuntary_delta"] = max(
                0.0,
                last["ctx_involuntary"] - first["ctx_involuntary"],
            )

        for key in (
            "gpu_util",
            "memory_util",
            "memory_used_mib",
            "temperature_c",
            "power_w",
            "graphics_clock_mhz",
            "memory_clock_mhz",
        ):
            summary = _poca_numeric_summary(
                sample.get(key) for sample in self._gpu_samples
            )
            result[f"{key}_avg"] = summary["avg"]
            result[f"{key}_min"] = summary["min"]
            result[f"{key}_max"] = summary["max"]

        pstates = [
            sample["pstate"]
            for sample in self._gpu_samples
            if sample.get("pstate")
        ]
        if pstates:
            result["pstate"] = max(set(pstates), key=pstates.count)

        try:
            from mlagents.torch_utils import torch

            if torch.cuda.is_available():
                device = int(torch.cuda.current_device())
                free_bytes, total_bytes = torch.cuda.mem_get_info(device)
                result.update(
                    {
                        "cuda_allocated_end_mib": (
                            int(torch.cuda.memory_allocated(device))
                            / (1024.0 * 1024.0)
                        ),
                        "cuda_reserved_end_mib": (
                            int(torch.cuda.memory_reserved(device))
                            / (1024.0 * 1024.0)
                        ),
                        "cuda_peak_allocated_mib": (
                            int(torch.cuda.max_memory_allocated(device))
                            / (1024.0 * 1024.0)
                        ),
                        "cuda_peak_reserved_mib": (
                            int(torch.cuda.max_memory_reserved(device))
                            / (1024.0 * 1024.0)
                        ),
                        "cuda_free_end_mib": int(free_bytes) / (1024.0 * 1024.0),
                        "cuda_total_mib": int(total_bytes) / (1024.0 * 1024.0),
                    }
                )
        except (ImportError, AttributeError, RuntimeError):
            pass
        return result


def _poca_structured_slot_profile(cache):
    """Describe actual occupied structured prefixes without changing training tensors."""

    import numpy as np
    from bees_mlagents_structured_policy import (
        ALLY_COUNT,
        ALLY_SIZE,
        ALLY_START,
        COLLISION_COUNT,
        COLLISION_SIZE,
        COLLISION_START,
        ENEMY_COUNT,
        ENEMY_SIZE,
        ENEMY_START,
        MAP_OBJECT_COUNT,
        MAP_OBJECT_SIZE,
        MAP_OBJECT_START,
        MINING_COUNT,
        MINING_SIZE,
        MINING_START,
    )

    if cache is None:
        return {}

    families = {
        "allies": (ALLY_START, ALLY_COUNT, ALLY_SIZE),
        "enemies": (ENEMY_START, ENEMY_COUNT, ENEMY_SIZE),
        "mining": (MINING_START, MINING_COUNT, MINING_SIZE),
        "map_objects": (MAP_OBJECT_START, MAP_OBJECT_COUNT, MAP_OBJECT_SIZE),
        "collisions": (COLLISION_START, COLLISION_COUNT, COLLISION_SIZE),
    }
    indices = {
        name: np.asarray(
            [start + slot * size for slot in range(count)],
            dtype=np.int64,
        )
        for name, (start, count, size) in families.items()
    }
    samples = {name: [] for name in families}
    tensors = []
    current = cache.get("current_obs") or []
    if current and getattr(current[0], "device", None) is not None:
        if current[0].device.type == "cpu":
            tensors.append(current[0])

    group_cache = cache.get("groupmate_obs")
    if isinstance(group_cache, _PocaPackedGroupObs) and group_cache.fields:
        for position in group_cache.fields[0]:
            if (
                position is not None
                and position.values.device.type == "cpu"
            ):
                tensors.append(position.values)

    for tensor in tensors:
        values = tensor.detach().numpy()
        if values.ndim != 2 or values.shape[1] != BEES_OBSERVATION_SIZE:
            continue
        for name, family_indices in indices.items():
            presence = values[:, family_indices]
            occupied = np.isfinite(presence) & (presence > 0.0)
            slot_numbers = np.arange(
                1,
                family_indices.size + 1,
                dtype=np.int16,
            )
            highest = np.max(
                np.where(occupied, slot_numbers, 0),
                axis=1,
                initial=0,
            )
            samples[name].extend(highest.tolist())

    result = {"slot_profile_rows": len(samples["allies"])}
    for name, values in samples.items():
        result[f"{name}_p50"] = _poca_percentile(values, 50)
        result[f"{name}_p90"] = _poca_percentile(values, 90)
        result[f"{name}_p99"] = _poca_percentile(values, 99)
        result[f"{name}_max"] = max(values) if values else 0
    return result


def _poca_update_workload_profile(cache, device_cache):
    import numpy as np

    if cache is None:
        return {}

    counts = np.asarray(cache.get("groupmate_counts", ()), dtype=np.int32)
    faction = np.asarray(cache.get("faction_values", ()), dtype=np.float32)
    bee_weight = np.clip((faction + 1.0) * 0.5, 0.0, 1.0)
    limits = cache.get("slot_limits") or {}
    result = {
        "experiences": int(cache.get("size", 0)),
        "groupmates_mean": float(counts.mean()) if counts.size else 0.0,
        "groupmates_p50": _poca_percentile(counts, 50),
        "groupmates_p90": _poca_percentile(counts, 90),
        "groupmates_p99": _poca_percentile(counts, 99),
        "groupmates_max": int(counts.max()) if counts.size else 0,
        "groupmate_rows": int(counts.astype(np.int64, copy=False).sum())
        if counts.size
        else 0,
        "bee_rows": int(np.count_nonzero(bee_weight >= 1.0)),
        "human_rows": int(np.count_nonzero(bee_weight <= 0.0)),
        "mixed_rows": int(
            np.count_nonzero((bee_weight > 0.0) & (bee_weight < 1.0))
        ),
        "cache_storage": str(device_cache.get("storage", "off")),
        "cache_mib": float(device_cache.get("bytes", 0)) / (1024.0 * 1024.0),
        "group_cache_storage": str(device_cache.get("group_storage", "off")),
        "group_cache_mib": float(device_cache.get("group_bytes", 0))
        / (1024.0 * 1024.0),
    }
    for name, value in limits.items():
        result[f"limit_{name}"] = int(value)
    result.update(_poca_structured_slot_profile(cache))
    return result


def _poca_timing_distribution(label):
    samples = getattr(_POCA_TIMING_STATE, "timing_samples", None) or {}
    values = samples.get(label, ())
    return {
        "p50": _poca_percentile(values, 50),
        "p90": _poca_percentile(values, 90),
        "max": max(values) if values else 0.0,
    }


def _poca_cuda_unattributed_distribution():
    samples = getattr(_POCA_TIMING_STATE, "timing_samples", None) or {}
    labels = (
        "cuda_optimizer_total",
        "cuda_actor_get_stats",
        "cuda_critic_pass",
        "cuda_baseline",
        "cuda_backward",
        "cuda_optimizer_step",
    )
    arrays = [samples.get(label, ()) for label in labels]
    if not arrays or not all(arrays):
        return {"p50": 0.0, "p90": 0.0, "max": 0.0}
    count = min(len(values) for values in arrays)
    values = []
    for index in range(count):
        span = arrays[0][index]
        accounted = sum(array[index] for array in arrays[1:])
        values.append(float(span) - float(accounted))
    return {
        "p50": _poca_percentile(values, 50),
        "p90": _poca_percentile(values, 90),
        "max": max(values) if values else 0.0,
    }


def _poca_record_variability_history(update_seconds, workload, hardware):
    minibatch = _poca_timing_distribution("minibatch_total")
    cuda_span = _poca_timing_distribution("cuda_optimizer_total")
    record = {
        "seconds": float(update_seconds),
        "gpu_util": float(hardware.get("gpu_util_avg", 0.0)),
        "gpu_clock": float(hardware.get("graphics_clock_mhz_avg", 0.0)),
        "process_cpu": float(hardware.get("process_cpu_avg", 0.0)),
        "hottest_core": float(hardware.get("hottest_core_avg", 0.0)),
        "group_p90": float(workload.get("groupmates_p90", 0.0)),
        "allies_p90": float(workload.get("allies_p90", 0.0)),
        "enemies_p90": float(workload.get("enemies_p90", 0.0)),
        "collisions_p90": float(workload.get("collisions_p90", 0.0)),
        "minibatch_p50": float(minibatch["p50"]),
        "cuda_span_p50": float(cuda_span["p50"]),
    }
    _POCA_VARIABILITY_HISTORY.append(record)
    if len(_POCA_VARIABILITY_HISTORY) > POCA_VARIABILITY_HISTORY_LIMIT:
        del _POCA_VARIABILITY_HISTORY[:-POCA_VARIABILITY_HISTORY_LIMIT]
    if len(_POCA_VARIABILITY_HISTORY) < 2:
        return
    fastest = min(_POCA_VARIABILITY_HISTORY, key=lambda item: item["seconds"])
    slowest = max(_POCA_VARIABILITY_HISTORY, key=lambda item: item["seconds"])
    ratio = slowest["seconds"] / max(fastest["seconds"], 1e-9)
    print(
        "[Bees PPO variability] contrast "
        f"n={len(_POCA_VARIABILITY_HISTORY)} "
        f"fast={fastest['seconds']:.3f} slow={slowest['seconds']:.3f} ratio={ratio:.2f} "
        f"gpu_util={fastest['gpu_util']:.1f}->{slowest['gpu_util']:.1f} "
        f"gpu_clock={fastest['gpu_clock']:.0f}->{slowest['gpu_clock']:.0f} "
        f"process_cpu={fastest['process_cpu']:.1f}->{slowest['process_cpu']:.1f} "
        f"hottest_core={fastest['hottest_core']:.1f}->{slowest['hottest_core']:.1f} "
        f"group_p90={fastest['group_p90']:.1f}->{slowest['group_p90']:.1f} "
        f"allies_p90={fastest['allies_p90']:.1f}->{slowest['allies_p90']:.1f} "
        f"enemies_p90={fastest['enemies_p90']:.1f}->{slowest['enemies_p90']:.1f} "
        f"collisions_p90={fastest['collisions_p90']:.1f}->{slowest['collisions_p90']:.1f} "
        f"minibatch_p50={fastest['minibatch_p50']:.3f}->{slowest['minibatch_p50']:.3f} "
        f"cuda_span_p50={fastest['cuda_span_p50']:.3f}->{slowest['cuda_span_p50']:.3f}",
        flush=True,
    )


def _poca_record_timing(label, seconds):
    totals = getattr(_POCA_TIMING_STATE, "timing_totals", None)
    counts = getattr(_POCA_TIMING_STATE, "timing_counts", None)
    if totals is None or counts is None:
        return
    value = float(seconds)
    totals[label] = totals.get(label, 0.0) + value
    counts[label] = counts.get(label, 0) + 1
    samples = getattr(_POCA_TIMING_STATE, "timing_samples", None)
    if samples is not None:
        samples.setdefault(label, []).append(value)


def _poca_cuda_timing_begin(label):
    """Record a CUDA event pair without synchronizing the training stream."""

    events = getattr(_POCA_TIMING_STATE, "cuda_events", None)
    if events is None:
        return None

    try:
        from mlagents.torch_utils import torch

        if not torch.cuda.is_available():
            return None
        start = torch.cuda.Event(enable_timing=True)
        end = torch.cuda.Event(enable_timing=True)
        start.record()
    except (AttributeError, RuntimeError):
        return None

    return label, start, end


def _poca_cuda_timing_end(marker):
    if marker is None:
        return

    events = getattr(_POCA_TIMING_STATE, "cuda_events", None)
    if events is None:
        return

    try:
        marker[2].record()
    except (AttributeError, RuntimeError):
        return
    events.append(marker)


def _poca_flush_cuda_timings():
    """Resolve queued CUDA events with one synchronization per PPO update."""

    events = getattr(_POCA_TIMING_STATE, "cuda_events", None)
    if not events:
        return

    try:
        from mlagents.torch_utils import torch

        torch.cuda.synchronize()
        for label, start, end in events:
            _poca_record_timing(
                f"cuda_{label}",
                float(start.elapsed_time(end)) / 1000.0,
            )
    except (AttributeError, RuntimeError):
        pass
    finally:
        events.clear()


def _poca_average_timing(label):
    totals = getattr(_POCA_TIMING_STATE, "timing_totals", None) or {}
    counts = getattr(_POCA_TIMING_STATE, "timing_counts", None) or {}
    count = counts.get(label, 0)
    return totals.get(label, 0.0) / count if count else 0.0


def _record_poca_trajectory_batch_timing(
    *,
    trajectories: int,
    experiences: int,
    prepare_seconds: float,
    evaluate_seconds: float,
    complete_seconds: float,
    total_seconds: float,
) -> None:
    now = time.monotonic()
    report = None
    with _POCA_TRAJECTORY_TIMING_LOCK:
        state = _POCA_TRAJECTORY_TIMING
        if state["last_report"] <= 0.0:
            state["last_report"] = now
        state["batches"] += 1
        state["trajectories"] += int(trajectories)
        state["experiences"] += int(experiences)
        state["prepare"] += float(prepare_seconds)
        state["evaluate"] += float(evaluate_seconds)
        state["complete"] += float(complete_seconds)
        state["total"] += float(total_seconds)
        if now - state["last_report"] >= POCA_TRAJECTORY_TIMING_REPORT_SECONDS:
            report = dict(state)
            state.update(
                {
                    "last_report": now,
                    "batches": 0,
                    "trajectories": 0,
                    "experiences": 0,
                    "prepare": 0.0,
                    "evaluate": 0.0,
                    "complete": 0.0,
                    "total": 0.0,
                }
            )
    if report is None:
        return
    batches = max(1, int(report["batches"]))
    total_seconds = max(1.0e-9, float(report["total"]))
    print(
        "[Bees trajectory timing] "
        f"batches={report['batches']} "
        f"trajectories={report['trajectories']} "
        f"experiences={report['experiences']} "
        f"avg_batch_exp={report['experiences'] / batches:.1f} "
        f"compute_exp_per_s={report['experiences'] / total_seconds:.1f} "
        f"avg_prepare={report['prepare'] / batches:.6f} "
        f"avg_evaluate={report['evaluate'] / batches:.6f} "
        f"avg_complete={report['complete'] / batches:.6f} "
        f"avg_total={report['total'] / batches:.6f}",
        flush=True,
    )


def _merge_agent_buffers(buffers):
    """Concatenate trajectory buffers without copying their NumPy payloads."""

    from mlagents.trainers.buffer import AgentBuffer

    merged = AgentBuffer()
    for buffer in buffers:
        for key, field in buffer.items():
            merged[key].extend(field)
    return merged


def _build_poca_next_observation_buffer(trajectories, n_obs):
    """Represent one bootstrap observation per trajectory as an AgentBuffer."""

    from mlagents.trainers.buffer import AgentBuffer
    from mlagents.trainers.trajectory import GroupObsUtil, ObsUtil

    buffer = AgentBuffer()
    for trajectory in trajectories:
        for obs_index in range(n_obs):
            buffer[ObsUtil.get_name_at(obs_index)].append(
                trajectory.next_obs[obs_index]
            )
            buffer[GroupObsUtil.get_name_at(obs_index)].append(
                [
                    groupmate_obs[obs_index]
                    for groupmate_obs in trajectory.next_group_obs
                ]
            )
    return buffer


def _poca_group_obs_tensors_from_buffer(policy, buffer, counts, device):
    """Create stock-equivalent NaN-padded group tensors while copying only real rows."""

    import numpy as np
    from mlagents.torch_utils import torch
    from mlagents.trainers.trajectory import GroupObsUtil

    counts = np.asarray(counts, dtype=np.int32)
    batch_size = int(counts.shape[0])
    max_groupmates = int(counts.max()) if counts.size else 0
    if max_groupmates <= 0:
        return []

    separated = []
    for obs_index, spec in enumerate(policy.behavior_spec.observation_specs):
        field = buffer[GroupObsUtil.get_name_at(obs_index)]
        positions = []
        for position in range(max_groupmates):
            valid_rows = np.flatnonzero(counts > position).astype(
                np.int64,
                copy=False,
            )
            if valid_rows.size == 0:
                continue
            compact = np.stack(
                [
                    np.asarray(
                        field[int(row)][position],
                        dtype=np.float32,
                    )
                    for row in valid_rows
                ],
                axis=0,
            )
            compact_tensor = torch.as_tensor(
                np.ascontiguousarray(compact),
                dtype=torch.float32,
                device=device,
            )
            padded = torch.full(
                (batch_size, *tuple(spec.shape)),
                float("nan"),
                dtype=torch.float32,
                device=device,
            )
            padded.index_copy_(
                0,
                torch.as_tensor(
                    valid_rows,
                    dtype=torch.long,
                    device=device,
                ),
                compact_tensor,
            )
            positions.append(padded)
        separated.append(positions)

    return [
        [separated[obs_index][position] for obs_index in range(len(separated))]
        for position in range(max_groupmates)
    ]


def _evaluate_poca_trajectory_batch(trainer, merged, trajectories):
    """Evaluate feed-forward POCA value, baseline and bootstrap targets in batches."""

    from mlagents.torch_utils import torch
    from mlagents.trainers.torch_entities.agent_action import AgentAction
    from mlagents.trainers.torch_entities.utils import ModelUtils
    from mlagents.trainers.trajectory import GroupObsUtil, ObsUtil
    from bees_mlagents_structured_policy import (
        reset_training_slot_limits,
        set_training_slot_limits,
    )

    if trainer.policy.use_recurrent:
        raise RuntimeError(
            "Batched Bees POCA trajectory evaluation requires a feed-forward policy."
        )

    n_obs = len(trainer.policy.behavior_spec.observation_specs)
    next_buffer = _build_poca_next_observation_buffer(trajectories, n_obs)
    slot_limits = _structured_training_slot_limits(
        trainer.policy,
        merged,
        extra_observations=[
            (trajectory.next_obs, trajectory.next_group_obs)
            for trajectory in trajectories
        ],
    )
    slot_token = set_training_slot_limits(slot_limits)
    try:
        with torch.no_grad():
            current_obs = [
                ModelUtils.list_to_tensor(obs)
                for obs in ObsUtil.from_buffer(merged, n_obs)
            ]
            current_counts = _poca_groupmate_counts(
                trainer.policy,
                merged,
                merged.num_experiences,
            )
            groupmate_obs = _poca_group_obs_tensors_from_buffer(
                trainer.policy,
                merged,
                current_counts,
                current_obs[0].device,
            )
            groupmate_actions = AgentAction.group_from_buffer(merged)
            _POCA_GROUP_BATCH_STATE.valid_rows = (
                _poca_groupmate_valid_row_indices(current_counts)
            )
            _POCA_GROUP_BATCH_STATE.encoded_cache = {}

            all_obs = [current_obs] + groupmate_obs
            value_estimates, _ = trainer.optimizer.critic.critic_pass(
                all_obs,
                memories=None,
                sequence_length=merged.num_experiences,
            )
            baseline_estimates, _ = trainer.optimizer.critic.baseline(
                current_obs,
                (groupmate_obs, groupmate_actions),
                memories=None,
                sequence_length=merged.num_experiences,
            )

            next_obs = [
                ModelUtils.list_to_tensor(obs)
                for obs in ObsUtil.from_buffer(next_buffer, n_obs)
            ]
            next_counts = _poca_groupmate_counts(
                trainer.policy,
                next_buffer,
                next_buffer.num_experiences,
            )
            next_groupmate_obs = _poca_group_obs_tensors_from_buffer(
                trainer.policy,
                next_buffer,
                next_counts,
                next_obs[0].device,
            )
            _POCA_GROUP_BATCH_STATE.valid_rows = (
                _poca_groupmate_valid_row_indices(next_counts)
            )
            _POCA_GROUP_BATCH_STATE.encoded_cache = {}
            next_value_estimates, _ = trainer.optimizer.critic.critic_pass(
                [next_obs] + next_groupmate_obs,
                memories=None,
                sequence_length=next_buffer.num_experiences,
            )
    finally:
        reset_training_slot_limits(slot_token)
        _POCA_GROUP_BATCH_STATE.valid_rows = None
        _POCA_GROUP_BATCH_STATE.encoded_cache = None

    return (
        {
            name: ModelUtils.to_numpy(value)
            for name, value in value_estimates.items()
        },
        {
            name: ModelUtils.to_numpy(value)
            for name, value in baseline_estimates.items()
        },
        {
            name: ModelUtils.to_numpy(value)
            for name, value in next_value_estimates.items()
        },
    )


def _complete_poca_trajectory(
    trainer,
    trajectory,
    agent_buffer_trajectory,
    value_estimates,
    baseline_estimates,
    value_next,
):
    """Finish stock ML-Agents 1.1.0 POCA bookkeeping after batched value inference."""

    import numpy as np
    from mlagents_envs.side_channel.stats_side_channel import (
        StatsAggregationMethod,
    )
    from mlagents.trainers.buffer import BufferKey, RewardSignalUtil
    from mlagents.trainers.trainer.trainer_utils import lambda_return

    agent_id = trajectory.agent_id

    for name, values in value_estimates.items():
        agent_buffer_trajectory[
            RewardSignalUtil.value_estimates_key(name)
        ].extend(values)
        agent_buffer_trajectory[
            RewardSignalUtil.baseline_estimates_key(name)
        ].extend(baseline_estimates[name])
        trainer._stats_reporter.add_stat(
            f"Policy/{trainer.optimizer.reward_signals[name].name.capitalize()} "
            "Baseline Estimate",
            np.mean(baseline_estimates[name]),
        )
        trainer._stats_reporter.add_stat(
            f"Policy/{trainer.optimizer.reward_signals[name].name.capitalize()} "
            "Value Estimate",
            np.mean(value_estimates[name]),
        )

    trainer.collected_rewards["environment"][agent_id] += np.sum(
        agent_buffer_trajectory[BufferKey.ENVIRONMENT_REWARDS]
    )
    trainer.collected_group_rewards[agent_id] += np.sum(
        agent_buffer_trajectory[BufferKey.GROUP_REWARD]
    )

    for name, reward_signal in trainer.optimizer.reward_signals.items():
        evaluate_result = (
            reward_signal.evaluate(agent_buffer_trajectory)
            * reward_signal.strength
        )
        agent_buffer_trajectory[RewardSignalUtil.rewards_key(name)].extend(
            evaluate_result
        )
        trainer.collected_rewards[name][agent_id] += np.sum(evaluate_result)

    tmp_advantages = []
    for name in trainer.optimizer.reward_signals:
        local_rewards = np.asarray(
            agent_buffer_trajectory[
                RewardSignalUtil.rewards_key(name)
            ].get_batch(),
            dtype=np.float32,
        )
        baseline_estimate = agent_buffer_trajectory[
            RewardSignalUtil.baseline_estimates_key(name)
        ].get_batch()
        v_estimates = agent_buffer_trajectory[
            RewardSignalUtil.value_estimates_key(name)
        ].get_batch()

        lambd_returns = lambda_return(
            r=local_rewards,
            value_estimates=v_estimates,
            gamma=trainer.optimizer.reward_signals[name].gamma,
            lambd=trainer.hyperparameters.lambd,
            value_next=value_next[name],
        )
        local_advantage = (
            np.asarray(lambd_returns)
            - np.asarray(baseline_estimate)
        )
        agent_buffer_trajectory[RewardSignalUtil.returns_key(name)].set(
            lambd_returns
        )
        agent_buffer_trajectory[RewardSignalUtil.advantage_key(name)].set(
            local_advantage
        )
        tmp_advantages.append(local_advantage)

    global_advantages = list(
        np.mean(np.asarray(tmp_advantages, dtype=np.float32), axis=0)
    )
    agent_buffer_trajectory[BufferKey.ADVANTAGES].set(global_advantages)
    trainer._append_to_update_buffer(agent_buffer_trajectory)

    if trajectory.done_reached:
        trainer._update_end_episode_stats(agent_id, trainer.optimizer)
        if not trajectory.all_group_dones_reached:
            trainer.collected_group_rewards.pop(agent_id)

    if trajectory.all_group_dones_reached and trajectory.done_reached:
        trainer.stats_reporter.add_stat(
            "Environment/Group Cumulative Reward",
            trainer.collected_group_rewards.get(agent_id, 0),
            aggregation=StatsAggregationMethod.HISTOGRAM,
        )
        trainer.collected_group_rewards.pop(agent_id)


def _process_prepared_poca_trajectory_batch(trainer, trajectories, buffers):
    """Finish a CPU-materialized trajectory batch once the learner model is available."""

    import numpy as np
    from mlagents.trainers.trainer.rl_trainer import RLTrainer

    batch_started = time.perf_counter()
    prepare_started = batch_started
    merged = _merge_agent_buffers(buffers)
    if trainer.is_training:
        trainer.policy.actor.update_normalization(merged)
        trainer.optimizer.critic.update_normalization(merged)
    prepare_seconds = time.perf_counter() - prepare_started

    evaluate_started = time.perf_counter()
    values, baselines, next_values = _evaluate_poca_trajectory_batch(
        trainer,
        merged,
        trajectories,
    )
    evaluate_seconds = time.perf_counter() - evaluate_started

    complete_started = time.perf_counter()
    offset = 0
    for trajectory_index, (trajectory, buffer) in enumerate(
        zip(trajectories, buffers)
    ):
        # Keep ML-Agents step/checkpoint/summary ordering per trajectory. Value
        # inference is intentionally batched before this bookkeeping; no policy
        # weights are changed by that inference.
        RLTrainer._process_trajectory(trainer, trajectory)
        length = buffer.num_experiences
        end = offset + length

        trajectory_values = {
            name: np.asarray(value[offset:end])
            for name, value in values.items()
        }
        trajectory_baselines = {
            name: np.asarray(value[offset:end])
            for name, value in baselines.items()
        }
        trajectory_next = {}
        terminal = (
            trajectory.all_group_dones_reached
            and trajectory.done_reached
            and not trajectory.interrupted
        )
        for name, value in next_values.items():
            next_slice = np.asarray(
                value[trajectory_index : trajectory_index + 1]
            ).copy()
            if (
                terminal
                and not trainer.optimizer.reward_signals[name].ignore_done
            ):
                next_slice[-1] = 0.0
            trajectory_next[name] = next_slice

        _complete_poca_trajectory(
            trainer,
            trajectory,
            buffer,
            trajectory_values,
            trajectory_baselines,
            trajectory_next,
        )
        offset = end

    complete_seconds = time.perf_counter() - complete_started
    _record_poca_trajectory_batch_timing(
        trajectories=len(trajectories),
        experiences=merged.num_experiences,
        prepare_seconds=prepare_seconds,
        evaluate_seconds=evaluate_seconds,
        complete_seconds=complete_seconds,
        total_seconds=time.perf_counter() - batch_started,
    )


def _process_poca_trajectory_batch(trainer, trajectories):
    """Process multiple short feed-forward trajectories with shared critic passes."""

    prepare_started = time.perf_counter()
    buffers = [trajectory.to_agentbuffer() for trajectory in trajectories]
    materialize_seconds = time.perf_counter() - prepare_started
    batch_started = time.perf_counter()
    _process_prepared_poca_trajectory_batch(trainer, trajectories, buffers)
    # The prepared helper reports model-side preparation separately. Preserve visibility
    # into raw trajectory materialization without putting it back on the critical GPU path.
    if materialize_seconds >= 0.05:
        print(
            "[Bees PPO pipeline] trajectory materialization "
            f"trajectories={len(trajectories)} seconds={materialize_seconds:.6f}",
            flush=True,
        )


def _poca_cpu_tensor(values, dtype):
    """Materialize one update-buffer field once on CPU for repeated PPO epochs."""

    import numpy as np
    from mlagents.torch_utils import torch

    array = np.ascontiguousarray(np.asarray(values))
    return torch.as_tensor(
        array,
        dtype=dtype,
        device=torch.device("cpu"),
    )


class _PocaRaggedGroupObs:
    """Borrow the update buffer's ragged group observations without duplicating them."""

    __slots__ = ("fields",)

    def __init__(self, fields) -> None:
        self.fields = tuple(fields)


class _PocaPackedGroupPosition:
    """One groupmate position packed only for rows where that member exists."""

    __slots__ = ("lookup", "values")

    def __init__(self, lookup, values) -> None:
        self.lookup = lookup
        self.values = values


class _PocaPackedGroupObs:
    """Compact CPU cache for ragged group observations reused across PPO epochs."""

    __slots__ = ("fields", "nbytes")

    def __init__(self, fields, nbytes: int) -> None:
        self.fields = tuple(tuple(field) for field in fields)
        self.nbytes = int(nbytes)


class _PocaPreparedGroupPosition:
    """Compact selected group rows plus their destination minibatch rows."""

    __slots__ = ("valid_rows", "values")

    def __init__(self, valid_rows, values) -> None:
        self.valid_rows = valid_rows
        self.values = values


class _PocaPreparedGroupObs:
    """CPU-prepared compact group observations before GPU padding."""

    __slots__ = ("fields", "batch_size")

    def __init__(self, fields, batch_size: int) -> None:
        self.fields = tuple(tuple(field) for field in fields)
        self.batch_size = int(batch_size)


def _build_poca_group_obs_cache(policy, buffer, groupmate_counts):
    """Pack actual groupmate rows once when the compact cache is safely bounded."""

    import numpy as np
    from mlagents.torch_utils import torch
    from mlagents.trainers.trajectory import GroupObsUtil

    counts = np.asarray(groupmate_counts, dtype=np.int32)
    size = int(counts.shape[0])
    max_groupmates = int(counts.max()) if counts.size else 0
    fields = tuple(
        buffer[GroupObsUtil.get_name_at(index)]
        for index in range(len(policy.behavior_spec.observation_specs))
    )
    if max_groupmates <= 0:
        return _PocaPackedGroupObs(
            tuple(() for _ in fields),
            0,
        )

    estimated_bytes = 0
    actual_members = int(counts.astype(np.int64, copy=False).sum())
    for spec in policy.behavior_spec.observation_specs:
        elements = int(np.prod(spec.shape, dtype=np.int64))
        estimated_bytes += actual_members * elements * np.dtype(np.float32).itemsize
    packed_limit = POCA_PACKED_GROUP_CACHE_MAX_BYTES
    try:
        import psutil

        memory = psutil.virtual_memory()
        ram_headroom = max(
            0,
            int(memory.available) - POCA_PACKED_GROUP_CACHE_MIN_RAM_RESERVE_BYTES,
        )
        packed_limit = min(packed_limit, ram_headroom)
    except (ImportError, AttributeError, OSError):
        pass
    if estimated_bytes > packed_limit:
        return _PocaRaggedGroupObs(fields)

    packed_fields = []
    packed_bytes = 0
    for field in fields:
        positions = []
        for position in range(max_groupmates):
            source_rows = np.flatnonzero(counts > position).astype(
                np.int64,
                copy=False,
            )
            if source_rows.size == 0:
                positions.append(None)
                continue
            compact = np.stack(
                [
                    np.asarray(
                        field[int(row)][position],
                        dtype=np.float32,
                    )
                    for row in source_rows
                ],
                axis=0,
            )
            values = _poca_cpu_tensor(compact, torch.float32)
            lookup = np.full(size, -1, dtype=np.int32)
            lookup[source_rows] = np.arange(
                source_rows.size,
                dtype=np.int32,
            )
            positions.append(_PocaPackedGroupPosition(lookup, values))
            packed_bytes += int(values.numel()) * int(values.element_size())
        packed_fields.append(tuple(positions))
    return _PocaPackedGroupObs(packed_fields, packed_bytes)


def _poca_group_obs_cache_nbytes(value) -> int:
    return int(value.nbytes) if isinstance(value, _PocaPackedGroupObs) else 0


def _move_poca_packed_group_obs(value, device):
    """Move packed group observation values while retaining CPU lookup metadata."""

    if not isinstance(value, _PocaPackedGroupObs):
        return value
    fields = []
    for field in value.fields:
        positions = []
        for position in field:
            if position is None:
                positions.append(None)
                continue
            positions.append(
                _PocaPackedGroupPosition(
                    position.lookup,
                    position.values.to(device=device),
                )
            )
        fields.append(tuple(positions))
    return _PocaPackedGroupObs(fields, value.nbytes)


def _select_poca_ragged_group_obs(
    source,
    indices,
    groupmate_counts,
    device,
    *,
    non_blocking: bool = False,
):
    """Pack only the selected minibatch's real groupmate rows.

    ML-Agents normally pads every groupmate position across the complete PPO buffer.
    For Bees' large vector observation that can consume tens of GiB. Keep the original
    ragged rows in AgentBuffer and construct the temporary padded representation only
    for the current minibatch. Missing groupmates remain NaN so stock POCA masking
    semantics are unchanged.
    """

    import numpy as np
    from mlagents.torch_utils import torch

    if not isinstance(
        source,
        (_PocaRaggedGroupObs, _PocaPackedGroupObs),
    ):
        return []
    indices = np.asarray(indices, dtype=np.int64)
    counts = np.asarray(groupmate_counts, dtype=np.int32)
    max_groupmates = int(counts.max()) if counts.size else 0
    if max_groupmates <= 0:
        return []

    separated = []
    for field in source.fields:
        positions = []
        for position in range(max_groupmates):
            valid_rows = np.flatnonzero(counts > position).astype(
                np.int64,
                copy=False,
            )
            if valid_rows.size == 0:
                continue
            if isinstance(source, _PocaPackedGroupObs):
                packed_position = field[position]
                if packed_position is None:
                    continue
                compact_ids = packed_position.lookup[indices]
                selected_ids = compact_ids[valid_rows].astype(
                    np.int64,
                    copy=False,
                )
                packed_device = packed_position.values.device
                compact_tensor = packed_position.values.index_select(
                    0,
                    torch.as_tensor(
                        selected_ids,
                        dtype=torch.long,
                        device=packed_device,
                    ),
                )
                if compact_tensor.device != device:
                    if (
                        non_blocking
                        and compact_tensor.device.type == "cpu"
                        and device.type == "cuda"
                        and not compact_tensor.is_pinned()
                    ):
                        compact_tensor = compact_tensor.pin_memory()
                    compact_tensor = compact_tensor.to(
                        device=device,
                        non_blocking=non_blocking,
                    )
            else:
                compact = np.stack(
                    [
                        np.asarray(
                            field[int(indices[row])][position],
                            dtype=np.float32,
                        )
                        for row in valid_rows
                    ],
                    axis=0,
                )
                compact_tensor = _poca_cpu_tensor(
                    compact,
                    torch.float32,
                )
                if (
                    non_blocking
                    and device.type == "cuda"
                    and not compact_tensor.is_pinned()
                ):
                    compact_tensor = compact_tensor.pin_memory()
                compact_tensor = compact_tensor.to(
                    device=device,
                    non_blocking=non_blocking,
                )
            padded = torch.full(
                (len(indices), *compact_tensor.shape[1:]),
                float("nan"),
                dtype=compact_tensor.dtype,
                device=device,
            )
            padded.index_copy_(
                0,
                torch.as_tensor(
                    valid_rows,
                    dtype=torch.long,
                    device=device,
                ),
                compact_tensor,
            )
            positions.append(padded)
        separated.append(positions)

    return [
        [separated[obs_index][position] for obs_index in range(len(separated))]
        for position in range(max_groupmates)
    ]


def _build_poca_update_tensor_cache(optimizer, buffer):
    """Tensorize fixed PPO fields once while retaining group observations ragged."""

    import numpy as np
    from mlagents.torch_utils import torch
    from mlagents.trainers.buffer import BufferKey, RewardSignalUtil
    from mlagents.trainers.trajectory import GroupObsUtil, ObsUtil
    from mlagents.trainers.torch_entities.components.reward_providers.extrinsic_reward_provider import (
        ExtrinsicRewardProvider,
    )

    policy = optimizer.policy
    if (
        policy.sequence_length != 1
        or len(policy.behavior_spec.observation_specs) != 1
        or not _is_bees_action_spec(policy.behavior_spec.action_spec)
        or any(
            not isinstance(provider, ExtrinsicRewardProvider)
            for provider in optimizer.reward_signals.values()
        )
    ):
        return None

    size = buffer.num_experiences
    if size <= 0:
        return None
    groupmate_counts = _poca_groupmate_counts(policy, buffer, size)
    if groupmate_counts is None:
        return None

    current_obs = [
        _poca_cpu_tensor(
            np.asarray(field.to_ndarray(), dtype=np.float32),
            torch.float32,
        )
        for field in ObsUtil.from_buffer(
            buffer,
            len(policy.behavior_spec.observation_specs),
        )
    ]
    groupmate_obs = _build_poca_group_obs_cache(
        policy,
        buffer,
        groupmate_counts,
    )

    def field_tensor(key, dtype):
        return _poca_cpu_tensor(buffer[key].get_batch(), dtype)

    continuous_actions = field_tensor(
        BufferKey.CONTINUOUS_ACTION,
        torch.float32,
    )
    discrete_actions = field_tensor(
        BufferKey.DISCRETE_ACTION,
        torch.long,
    )
    action_masks = field_tensor(BufferKey.ACTION_MASK, torch.float32)
    loss_masks = field_tensor(BufferKey.MASKS, torch.bool)
    advantages = field_tensor(BufferKey.ADVANTAGES, torch.float32)

    old_log_prob_parts = []
    if BufferKey.CONTINUOUS_LOG_PROBS in buffer:
        old_log_prob_parts.append(
            field_tensor(BufferKey.CONTINUOUS_LOG_PROBS, torch.float32)
        )
    if BufferKey.DISCRETE_LOG_PROBS in buffer:
        old_log_prob_parts.append(
            field_tensor(BufferKey.DISCRETE_LOG_PROBS, torch.float32)
        )
    if not old_log_prob_parts:
        return None
    old_log_probs = (
        old_log_prob_parts[0]
        if len(old_log_prob_parts) == 1
        else torch.cat(old_log_prob_parts, dim=1)
    )

    group_continuous = []
    if BufferKey.GROUP_CONTINUOUS_ACTION in buffer:
        group_continuous = [
            _poca_cpu_tensor(arr, torch.float32)
            for arr in buffer[
                BufferKey.GROUP_CONTINUOUS_ACTION
            ].padded_to_batch()
        ]
    group_discrete = []
    if BufferKey.GROUP_DISCRETE_ACTION in buffer:
        group_discrete = [
            _poca_cpu_tensor(arr, torch.long)
            for arr in buffer[
                BufferKey.GROUP_DISCRETE_ACTION
            ].padded_to_batch(dtype=np.int64)
        ]

    returns = {}
    old_values = {}
    old_baselines = {}
    for name in optimizer.reward_signals:
        returns[name] = field_tensor(
            RewardSignalUtil.returns_key(name),
            torch.float32,
        )
        old_values[name] = field_tensor(
            RewardSignalUtil.value_estimates_key(name),
            torch.float32,
        )
        old_baselines[name] = field_tensor(
            RewardSignalUtil.baseline_estimates_key(name),
            torch.float32,
        )

    movement_activity = _bees_movement_activity(policy, buffer)
    movement_tensor = (
        None
        if movement_activity is None
        else _poca_cpu_tensor(movement_activity, torch.float32)
    )
    from bees_mlagents_structured_policy import FACTION_INDEX
    faction_values = (
        current_obs[0][:, FACTION_INDEX]
        .detach()
        .cpu()
        .numpy()
        .astype(np.float32, copy=True)
    )

    return {
        "size": size,
        "current_obs": current_obs,
        "groupmate_obs": groupmate_obs,
        "continuous_actions": continuous_actions,
        "discrete_actions": discrete_actions,
        "group_continuous": group_continuous,
        "group_discrete": group_discrete,
        "action_masks": action_masks,
        "loss_masks": loss_masks,
        "advantages": advantages,
        "old_log_probs": old_log_probs,
        "returns": returns,
        "old_values": old_values,
        "old_baselines": old_baselines,
        "movement_activity": movement_tensor,
        "groupmate_counts": np.asarray(
            groupmate_counts,
            dtype=np.int32,
        ),
        "faction_values": faction_values,
        "slot_limits": _structured_training_slot_limits(policy, buffer),
        "storage": "cpu",
    }


def _poca_tensor_cache_nbytes(value) -> int:
    """Count unique torch tensor storage represented by a nested cache structure."""

    from mlagents.torch_utils import torch
    seen = set()

    def visit(item):
        if isinstance(item, torch.Tensor):
            identity = id(item)
            if identity in seen:
                return 0
            seen.add(identity)
            return int(item.numel()) * int(item.element_size())
        if isinstance(item, dict):
            return sum(visit(child) for child in item.values())
        if isinstance(item, (list, tuple)):
            return sum(visit(child) for child in item)
        return 0

    return int(visit(value))


def _poca_move_cache_tensors(value, device):
    """Recursively move only torch tensors, leaving numpy metadata on CPU."""

    from mlagents.torch_utils import torch
    if isinstance(value, torch.Tensor):
        return value.to(device=device)
    if isinstance(value, dict):
        return {
            key: _poca_move_cache_tensors(child, device)
            for key, child in value.items()
        }
    if isinstance(value, list):
        return [
            _poca_move_cache_tensors(child, device)
            for child in value
        ]
    if isinstance(value, tuple):
        return tuple(
            _poca_move_cache_tensors(child, device)
            for child in value
        )
    return value


def _promote_poca_update_tensor_cache(cache):
    """Keep the complete PPO tensor cache on GPU when VRAM can safely hold it."""

    from mlagents.torch_utils import default_device, torch

    if cache is None:
        return cache, {
            "storage": "off",
            "bytes": 0,
            "copy_seconds": 0.0,
            "group_storage": "off",
            "group_bytes": 0,
            "group_copy_seconds": 0.0,
            "empty_cache_seconds": 0.0,
            "group_empty_cache_seconds": 0.0,
            "free_before": 0,
            "reserve": 0,
        }

    cache_bytes = _poca_tensor_cache_nbytes(cache)
    device = default_device()
    group_cache = cache.get("groupmate_obs")
    group_cache_bytes = _poca_group_obs_cache_nbytes(group_cache)
    result = {
        "storage": "cpu",
        "bytes": int(cache_bytes),
        "copy_seconds": 0.0,
        "group_storage": (
            "cpu" if isinstance(group_cache, _PocaPackedGroupObs) else "off"
        ),
        "group_bytes": int(group_cache_bytes),
        "group_copy_seconds": 0.0,
        "empty_cache_seconds": 0.0,
        "group_empty_cache_seconds": 0.0,
        "free_before": 0,
        "reserve": 0,
    }
    if getattr(device, "type", str(device)) != "cuda" or not torch.cuda.is_available():
        return cache, result

    # PyTorch 2.1.1 mem_get_info() requires an integer or an explicitly
    # indexed CUDA device; ML-Agents default_device() returns torch.device("cuda")
    # with no index. Resolve the current device once and use it consistently.
    device_index = device.index
    if device_index is None:
        device_index = torch.cuda.current_device()
    device_index = int(device_index)
    device = torch.device("cuda", device_index)

    # Release only unused PyTorch allocator blocks before asking the CUDA
    # driver how much physical VRAM is actually available. On Windows/WDDM,
    # allocator-reserved virtual memory can grow beyond physical VRAM; it must
    # never be counted as additional cache capacity.
    cleanup_started = time.perf_counter()
    torch.cuda.empty_cache()
    result["empty_cache_seconds"] = time.perf_counter() - cleanup_started

    free_bytes, total_bytes = torch.cuda.mem_get_info(device_index)
    allocated_bytes = int(torch.cuda.memory_allocated(device_index))
    reserved_bytes = int(torch.cuda.memory_reserved(device_index))
    allocator_reusable_bytes = max(0, reserved_bytes - allocated_bytes)
    reserve_bytes = max(
        POCA_GPU_CACHE_MIN_RESERVE_BYTES,
        int(total_bytes * POCA_GPU_CACHE_RESERVE_FRACTION),
    )
    result["driver_free_before"] = int(free_bytes)
    result["allocator_reusable_before"] = int(allocator_reusable_bytes)
    result["free_before"] = int(free_bytes)
    result["reserve"] = int(reserve_bytes)

    # A ragged group cache means this update is already large enough that the
    # critic will materialize substantial temporary group tensors per minibatch.
    # Keep the fixed cache on CPU too so model/backprop working memory retains
    # the full physical-VRAM reserve instead of competing with ~550 MiB of
    # long-lived update tensors.
    if isinstance(group_cache, _PocaRaggedGroupObs):
        result["fixed_cache_reason"] = "ragged-group-reserve"
        return cache, result

    if cache_bytes > max(0, int(free_bytes) - int(reserve_bytes)):
        return cache, result

    started = time.perf_counter()
    try:
        promoted = _poca_move_cache_tensors(cache, device)
    except RuntimeError as exc:
        if "out of memory" not in str(exc).lower():
            raise
        torch.cuda.empty_cache()
        print(
            "[Bees PPO timing] GPU tensor cache promotion skipped after CUDA OOM; "
            "falling back to CPU minibatch transfers.",
            flush=True,
        )
        return cache, result

    promoted["storage"] = "cuda"
    if all(
        key in cache
        for key in ("current_obs", "groupmate_obs", "groupmate_counts")
    ):
        promoted["_cpu_group_source"] = {
            "current_obs": cache["current_obs"],
            "groupmate_obs": cache["groupmate_obs"],
            "groupmate_counts": cache["groupmate_counts"],
        }
    result["storage"] = "cuda"
    result["copy_seconds"] = time.perf_counter() - started

    # Packed group observations remain CPU-resident. Even when a multi-GiB
    # packed cache technically fits beside the model, keeping it resident on
    # the GTX 1660 leaves too little headroom for critic/baseline/backprop
    # temporaries and produced severe WDDM paging stalls in live training.
    # Per-minibatch group tensors are transferred on demand instead.
    result["group_storage"] = (
        "cpu" if isinstance(promoted.get("groupmate_obs"), _PocaPackedGroupObs)
        else "off"
    )
    result["group_promotion_reason"] = "cpu-resident"
    return promoted, result


def _select_poca_update_tensor_cache(
    cache,
    indices,
    *,
    device_override=None,
    non_blocking: bool = False,
    groupmate_obs_override=None,
):
    """Gather one shuffled PPO minibatch from the materialized cache."""

    import numpy as np
    from mlagents.torch_utils import default_device, torch
    from mlagents.trainers.torch_entities.agent_action import AgentAction

    indices = np.asarray(indices, dtype=np.int64)
    device = default_device() if device_override is None else device_override
    cache_storage = str(cache.get("storage", "cpu"))
    index_device = (
        device if cache_storage == "cuda"
        else torch.device("cpu")
    )
    index_tensor = torch.as_tensor(
        indices,
        dtype=torch.long,
        device=index_device,
    )

    def take(tensor):
        local_index = index_tensor
        if tensor.device != index_device:
            local_index = index_tensor.to(device=tensor.device)
        selected = tensor.index_select(0, local_index)
        if selected.device == device:
            return selected
        if (
            non_blocking
            and selected.device.type == "cpu"
            and device.type == "cuda"
            and not selected.is_pinned()
        ):
            selected = selected.pin_memory()
        return selected.to(device=device, non_blocking=non_blocking)

    selected_groupmate_counts = cache["groupmate_counts"][indices]
    max_groupmates = (
        int(selected_groupmate_counts.max())
        if selected_groupmate_counts.size
        else 0
    )
    selected_faction = cache["faction_values"][indices]
    bee_weight = np.clip(
        (selected_faction.astype(np.float32, copy=False) + 1.0) * 0.5,
        0.0,
        1.0,
    )
    faction_rows = {
        "bee": np.flatnonzero(bee_weight >= 1.0).astype(
            np.int64,
            copy=False,
        ),
        "human": np.flatnonzero(bee_weight <= 0.0).astype(
            np.int64,
            copy=False,
        ),
        "mixed": np.flatnonzero(
            (bee_weight > 0.0) & (bee_weight < 1.0)
        ).astype(np.int64, copy=False),
    }

    current_obs = [take(tensor) for tensor in cache["current_obs"]]
    groupmate_obs = (
        groupmate_obs_override
        if groupmate_obs_override is not None
        else _select_poca_ragged_group_obs(
            cache["groupmate_obs"],
            indices,
            selected_groupmate_counts,
            device,
            non_blocking=non_blocking,
        )
    )
    continuous_actions = take(cache["continuous_actions"])
    discrete_actions = take(cache["discrete_actions"])
    actions = AgentAction(
        continuous_actions,
        [
            discrete_actions[..., index]
            for index in range(discrete_actions.shape[-1])
        ],
    )

    group_actions = []
    group_count = min(
        max_groupmates,
        max(
            len(cache["group_continuous"]),
            len(cache["group_discrete"]),
        ),
    )
    for position in range(group_count):
        continuous = (
            take(cache["group_continuous"][position])
            if position < len(cache["group_continuous"])
            else None
        )
        discrete = (
            take(cache["group_discrete"][position])
            if position < len(cache["group_discrete"])
            else None
        )
        group_actions.append(
            AgentAction(
                continuous,
                None
                if discrete is None
                else [
                    discrete[..., index]
                    for index in range(discrete.shape[-1])
                ],
            )
        )

    return {
        "current_obs": current_obs,
        "groupmate_obs": groupmate_obs,
        "actions": actions,
        "groupmate_actions": group_actions,
        "action_masks": take(cache["action_masks"]),
        "loss_masks": take(cache["loss_masks"]),
        "advantages": take(cache["advantages"]),
        "old_log_probs": take(cache["old_log_probs"]),
        "returns": {
            name: take(tensor)
            for name, tensor in cache["returns"].items()
        },
        "old_values": {
            name: take(tensor)
            for name, tensor in cache["old_values"].items()
        },
        "old_baselines": {
            name: take(tensor)
            for name, tensor in cache["old_baselines"].items()
        },
        "movement_activity": (
            None
            if cache["movement_activity"] is None
            else take(cache["movement_activity"])
        ),
        "discrete_actions": discrete_actions,
        "groupmate_counts": selected_groupmate_counts,
        "faction_rows": faction_rows,
        "slot_limits": cache["slot_limits"],
    }


def _poca_prepare_compact_group_obs(
    source,
    indices,
    groupmate_counts,
):
    """Select only real group rows on CPU; leave minibatch padding for CUDA."""

    import numpy as np
    from mlagents.torch_utils import torch

    if not isinstance(
        source,
        (_PocaRaggedGroupObs, _PocaPackedGroupObs),
    ):
        return _PocaPreparedGroupObs((), len(indices))

    indices = np.asarray(indices, dtype=np.int64)
    counts = np.asarray(groupmate_counts, dtype=np.int32)
    max_groupmates = int(counts.max()) if counts.size else 0
    prepared_fields = []

    for field in source.fields:
        positions = []
        for position in range(max_groupmates):
            valid_rows = np.flatnonzero(counts > position).astype(
                np.int64,
                copy=False,
            )
            if valid_rows.size == 0:
                positions.append(None)
                continue

            if isinstance(source, _PocaPackedGroupObs):
                packed_position = field[position]
                if packed_position is None:
                    positions.append(None)
                    continue
                compact_ids = packed_position.lookup[indices]
                selected_ids = compact_ids[valid_rows].astype(
                    np.int64,
                    copy=False,
                )
                values = packed_position.values.index_select(
                    0,
                    torch.as_tensor(
                        selected_ids,
                        dtype=torch.long,
                        device=packed_position.values.device,
                    ),
                )
                if values.device.type != "cpu":
                    raise RuntimeError(
                        "POCA CPU group preparation received a non-CPU packed cache."
                    )
            else:
                compact = np.stack(
                    [
                        np.asarray(
                            field[int(indices[row])][position],
                            dtype=np.float32,
                        )
                        for row in valid_rows
                    ],
                    axis=0,
                )
                values = _poca_cpu_tensor(
                    compact,
                    torch.float32,
                )

            positions.append(
                _PocaPreparedGroupPosition(
                    valid_rows,
                    values,
                )
            )
        prepared_fields.append(tuple(positions))

    return _PocaPreparedGroupObs(
        prepared_fields,
        len(indices),
    )


def _poca_expand_prepared_group_obs(prepared, device):
    """Pad compact prepared rows on the target device in POCA's expected shape."""

    from mlagents.torch_utils import torch

    if not isinstance(prepared, _PocaPreparedGroupObs):
        return []
    if not prepared.fields:
        return []

    separated = []
    max_positions = max((len(field) for field in prepared.fields), default=0)
    for field in prepared.fields:
        positions = []
        for position in range(max_positions):
            prepared_position = (
                field[position]
                if position < len(field)
                else None
            )
            if prepared_position is None:
                positions.append(None)
                continue
            values = prepared_position.values
            if values.device != device:
                values = values.to(device=device, non_blocking=True)
            padded = torch.full(
                (prepared.batch_size, *tuple(values.shape[1:])),
                float("nan"),
                dtype=values.dtype,
                device=device,
            )
            padded.index_copy_(
                0,
                torch.as_tensor(
                    prepared_position.valid_rows,
                    dtype=torch.long,
                    device=device,
                ),
                values,
            )
            positions.append(padded)
        separated.append(tuple(positions))

    members = []
    for position in range(max_positions):
        member = []
        for field in separated:
            value = field[position]
            if value is None:
                raise RuntimeError(
                    "POCA prepared group fields disagree on groupmate positions."
                )
            member.append(value)
        members.append(member)
    return members


def _poca_select_group_prepare_source(source, indices):
    """Select CPU observations used by group padding and row deduplication."""

    import numpy as np
    from mlagents.torch_utils import torch

    indices = np.asarray(indices, dtype=np.int64)
    index_tensor = torch.as_tensor(indices, dtype=torch.long)
    counts = np.asarray(
        source["groupmate_counts"][indices],
        dtype=np.int32,
    )
    current_obs = [
        tensor.index_select(0, index_tensor)
        for tensor in source["current_obs"]
    ]
    return {
        "current_obs": current_obs,
        "prepared_groupmate_obs": _poca_prepare_compact_group_obs(
            source["groupmate_obs"],
            indices,
            counts,
        ),
        "groupmate_counts": counts,
    }


def _poca_build_observation_dedup_plan(cached):
    """Find bit-identical current/group observations off the CUDA critical path.

    Episode ship identity + progress + faction form a cheap candidate key. Full
    float32 bit equality is still required before two rows share an encoding, so
    hash/key collisions cannot change the network's numerical semantics.
    """

    import numpy as np
    from mlagents.torch_utils import torch
    from bees_mlagents_structured_policy import (
        EPISODE_PROGRESS_INDEX,
        FACTION_INDEX,
    )

    if cached is None or len(cached.get("current_obs", ())) != 1:
        return None
    current = cached["current_obs"][0]
    if current.device.type != "cpu" or current.ndim != 2:
        return None
    if int(current.shape[1]) != BEES_OBSERVATION_SIZE:
        return None

    counts = np.asarray(cached.get("groupmate_counts", ()), dtype=np.int32)
    if counts.shape[0] != int(current.shape[0]):
        return None

    current_rows = np.asarray(current.detach().numpy(), dtype=np.float32)
    unique_rows = []
    unique_bits = []
    candidates = {}

    def intern(row):
        row = np.asarray(row, dtype=np.float32)
        bits = row.view(np.uint32)
        key = (
            int(bits[0]),
            int(bits[EPISODE_PROGRESS_INDEX]),
            int(bits[FACTION_INDEX]),
        )
        for unique_index in candidates.get(key, ()):
            if np.array_equal(bits, unique_bits[unique_index]):
                return unique_index
        unique_index = len(unique_rows)
        unique_rows.append(row)
        unique_bits.append(bits)
        candidates.setdefault(key, []).append(unique_index)
        return unique_index

    current_inverse = np.empty((current_rows.shape[0],), dtype=np.int64)
    for row_index, row in enumerate(current_rows):
        current_inverse[row_index] = intern(row)

    group_inverse = []
    input_rows = int(current_rows.shape[0])
    prepared_group = cached.get("prepared_groupmate_obs")
    if isinstance(prepared_group, _PocaPreparedGroupObs):
        if len(prepared_group.fields) != 1:
            return None
        for prepared_position in prepared_group.fields[0]:
            if prepared_position is None:
                group_inverse.append(
                    torch.empty((0,), dtype=torch.long)
                )
                continue
            values = prepared_position.values
            if (
                values.device.type != "cpu"
                or values.ndim != 2
                or int(values.shape[1]) != BEES_OBSERVATION_SIZE
            ):
                return None
            member_rows = np.asarray(
                values.detach().numpy(),
                dtype=np.float32,
            )
            compact_inverse = np.empty(
                (member_rows.shape[0],),
                dtype=np.int64,
            )
            for compact_index, row in enumerate(member_rows):
                compact_inverse[compact_index] = intern(row)
            input_rows += int(member_rows.shape[0])
            group_inverse.append(torch.from_numpy(compact_inverse))
    else:
        for position, member in enumerate(cached.get("groupmate_obs", ())):
            if (
                len(member) != 1
                or member[0].device.type != "cpu"
                or member[0].ndim != 2
                or int(member[0].shape[1]) != BEES_OBSERVATION_SIZE
            ):
                return None
            valid_rows = np.flatnonzero(counts > position).astype(
                np.int64,
                copy=False,
            )
            compact_inverse = np.empty((valid_rows.size,), dtype=np.int64)
            if valid_rows.size:
                member_rows = np.asarray(
                    member[0].detach().numpy(),
                    dtype=np.float32,
                )
                for compact_index, row_index in enumerate(valid_rows):
                    compact_inverse[compact_index] = intern(
                        member_rows[int(row_index)]
                    )
            input_rows += int(valid_rows.size)
            group_inverse.append(torch.from_numpy(compact_inverse))

    unique_count = len(unique_rows)
    saved_rows = input_rows - unique_count
    if saved_rows < POCA_DEDUP_MIN_SAVED_ROWS:
        return None

    return {
        "unique_obs": [
            torch.from_numpy(np.stack(unique_rows, axis=0))
        ],
        "current_inverse": torch.from_numpy(current_inverse),
        "group_inverse": group_inverse,
        "input_rows": int(input_rows),
        "unique_rows": int(unique_count),
        "saved_rows": int(saved_rows),
    }


def _poca_transform_selected_minibatch(value, tensor_transform):
    """Apply one tensor transform while preserving AgentAction/container shape."""

    from mlagents.torch_utils import torch
    from mlagents.trainers.torch_entities.agent_action import AgentAction

    if isinstance(value, torch.Tensor):
        return tensor_transform(value)
    if isinstance(value, _PocaPreparedGroupObs):
        fields = []
        for field in value.fields:
            positions = []
            for position in field:
                if position is None:
                    positions.append(None)
                else:
                    positions.append(
                        _PocaPreparedGroupPosition(
                            position.valid_rows,
                            tensor_transform(position.values),
                        )
                    )
            fields.append(tuple(positions))
        return _PocaPreparedGroupObs(fields, value.batch_size)
    if isinstance(value, AgentAction):
        continuous = (
            None
            if value.continuous_tensor is None
            else tensor_transform(value.continuous_tensor)
        )
        discrete = (
            None
            if value.discrete_list is None
            else [tensor_transform(item) for item in value.discrete_list]
        )
        return AgentAction(continuous, discrete)
    if isinstance(value, dict):
        return {
            key: _poca_transform_selected_minibatch(child, tensor_transform)
            for key, child in value.items()
        }
    if isinstance(value, list):
        return [
            _poca_transform_selected_minibatch(child, tensor_transform)
            for child in value
        ]
    if isinstance(value, tuple):
        return tuple(
            _poca_transform_selected_minibatch(child, tensor_transform)
            for child in value
        )
    return value


def _poca_pin_selected_minibatch(cached):
    from mlagents.torch_utils import torch

    def pin(tensor):
        if tensor.device.type == "cpu" and not tensor.is_pinned():
            return tensor.pin_memory()
        return tensor

    return _poca_transform_selected_minibatch(cached, pin)


def _poca_move_selected_minibatch(cached, device):
    def move(tensor):
        if tensor.device == device:
            return tensor
        return tensor.to(device=device, non_blocking=True)

    return _poca_transform_selected_minibatch(cached, move)


def _poca_expand_dedup_encoding(
    unique_encoded,
    mapping,
    valid_rows,
    batch_size: int,
):
    """Fan unique encoded rows back to their exact POCA member positions."""

    from mlagents.torch_utils import torch

    mapping = mapping.to(
        device=unique_encoded.device,
        dtype=torch.long,
    )
    part = unique_encoded.index_select(0, mapping)
    if valid_rows is None:
        if int(part.shape[0]) != int(batch_size):
            raise RuntimeError(
                "POCA dedup current-observation mapping has unexpected size."
            )
        return part

    if int(part.shape[0]) != int(len(valid_rows)):
        raise RuntimeError(
            "POCA dedup group-observation mapping has unexpected size."
        )
    index = torch.as_tensor(
        valid_rows,
        dtype=torch.long,
        device=unique_encoded.device,
    )
    return part.new_zeros(
        (int(batch_size), int(part.shape[1]))
    ).index_copy(
        0,
        index,
        part,
    )


def _poca_cuda_streams(count: int):
    """Reuse non-default CUDA streams for opt-in intra-minibatch parallelism."""

    from mlagents.torch_utils import torch

    if count <= 0 or not torch.cuda.is_available():
        return ()
    pool = getattr(_POCA_TIMING_STATE, "stream_pool", None)
    if pool is None:
        pool = {}
        _POCA_TIMING_STATE.stream_pool = pool
    streams = pool.get(int(count))
    if streams is None:
        streams = tuple(torch.cuda.Stream() for _ in range(int(count)))
        pool[int(count)] = streams
    return streams


def _poca_remap_rows(rows, start: int, end: int):
    import numpy as np

    if rows is None:
        return None
    values = np.asarray(rows, dtype=np.int64)
    selected = values[(values >= start) & (values < end)]
    return selected - int(start)


def _poca_shard_faction_rows(rows, start: int, end: int):
    if rows is None:
        return None
    return {
        name: _poca_remap_rows(rows.get(name, ()), start, end)
        for name in ("bee", "human", "mixed")
    }


def _poca_prepare_parallel_max_agents(optimizer, groupmate_counts) -> None:
    """Advance POCA's running max-agent normalizer before parallel critic branches."""

    import numpy as np
    from mlagents.torch_utils import torch

    body = getattr(getattr(optimizer, "critic", None), "network_body", None)
    current = getattr(body, "_current_max_agents", None)
    if current is None:
        return
    counts = np.asarray(groupmate_counts, dtype=np.int32)
    batch_max = 1 + (int(counts.max()) if counts.size else 0)
    with torch.no_grad():
        candidate = current.new_tensor(batch_max)
        current.copy_(torch.maximum(current, candidate))


def _poca_parallel_forward(
    optimizer,
    current_obs,
    actions,
    act_masks,
    groupmate_obs,
    groupmate_actions,
    memories,
    value_memories,
    baseline_memories,
    cached,
):
    """Run one logical minibatch on independent CUDA streams without extra Adam steps."""

    opts = _POCA_OPTIMIZATIONS
    if opts.stream_shards <= 1 and not opts.critic_baseline_overlap:
        return None

    from mlagents.torch_utils import torch
    from mlagents.trainers.torch_entities.action_log_probs import ActionLogProbs
    from bees_mlagents_structured_policy import (
        reset_training_faction_rows,
        set_training_faction_rows,
    )

    if (
        cached is None
        or not torch.cuda.is_available()
        or current_obs[0].device.type != "cuda"
        or memories
        or value_memories
        or baseline_memories
    ):
        return None

    batch_size = int(current_obs[0].shape[0])
    shard_count = min(int(opts.stream_shards), batch_size)
    if shard_count <= 0:
        return None

    ranges = []
    for shard in range(shard_count):
        start = (batch_size * shard) // shard_count
        end = (batch_size * (shard + 1)) // shard_count
        if end > start:
            ranges.append((start, end))
    if not ranges:
        return None

    overlap = bool(opts.critic_baseline_overlap)
    streams_per_shard = 3 if overlap else 1
    streams = _poca_cuda_streams(len(ranges) * streams_per_shard)
    if len(streams) != len(ranges) * streams_per_shard:
        return None

    full_valid_rows = getattr(_POCA_GROUP_BATCH_STATE, "valid_rows", None)
    full_encoded_cache = getattr(_POCA_GROUP_BATCH_STATE, "encoded_cache", None)
    full_dimension_mask = getattr(_POLICY_DIMENSION_MASK_STATE, "mask", None)
    full_freeze = bool(
        getattr(_POCA_GROUP_BATCH_STATE, "freeze_max_agents", False)
    )
    faction_rows = cached.get("faction_rows")
    groupmate_counts = cached.get("groupmate_counts", ())
    _poca_prepare_parallel_max_agents(optimizer, groupmate_counts)

    default_stream = torch.cuda.current_stream()
    span_marker = _poca_cuda_timing_begin("parallel_forward")
    submit_started = time.perf_counter()

    actor_outputs = []
    value_outputs = []
    baseline_outputs = []

    def shard_obs(values, start, end):
        return [tensor[start:end] for tensor in values]

    def shard_group_obs(values, start, end):
        return [
            [tensor[start:end] for tensor in member]
            for member in values
        ]

    def shard_group_actions(values, start, end):
        return [value.slice(start, end) for value in values]

    def install_shard_context(start, end, encoded_cache, *, actor=False):
        if full_valid_rows is None:
            _POCA_GROUP_BATCH_STATE.valid_rows = None
        else:
            _POCA_GROUP_BATCH_STATE.valid_rows = [
                _poca_remap_rows(rows, start, end)
                for rows in full_valid_rows
            ]
        _POCA_GROUP_BATCH_STATE.encoded_cache = encoded_cache
        _POCA_GROUP_BATCH_STATE.freeze_max_agents = True
        if actor and full_dimension_mask is not None:
            _POLICY_DIMENSION_MASK_STATE.mask = full_dimension_mask[start:end]
        return set_training_faction_rows(
            _poca_shard_faction_rows(faction_rows, start, end)
        )

    try:
        for shard_index, (start, end) in enumerate(ranges):
            obs = shard_obs(current_obs, start, end)
            group_obs = shard_group_obs(groupmate_obs, start, end)
            shard_actions = actions.slice(start, end)
            shard_masks = act_masks[start:end]
            shard_group_act = shard_group_actions(
                groupmate_actions,
                start,
                end,
            )

            if overlap:
                actor_stream = streams[shard_index * 3]
                critic_stream = streams[shard_index * 3 + 1]
                baseline_stream = streams[shard_index * 3 + 2]

                with torch.cuda.stream(actor_stream):
                    actor_stream.wait_stream(default_stream)
                    faction_token = install_shard_context(
                        start,
                        end,
                        {},
                        actor=True,
                    )
                    try:
                        actor_marker = _poca_cuda_timing_begin(
                            "actor_get_stats"
                        )
                        actor_output = optimizer.policy.actor.get_stats(
                            obs,
                            shard_actions,
                            masks=shard_masks,
                            memories=[],
                            sequence_length=optimizer.policy.sequence_length,
                        )
                        _poca_cuda_timing_end(actor_marker)
                    finally:
                        reset_training_faction_rows(faction_token)
                        _POLICY_DIMENSION_MASK_STATE.mask = full_dimension_mask
                    actor_outputs.append(actor_output)

                with torch.cuda.stream(critic_stream):
                    critic_stream.wait_stream(default_stream)
                    faction_token = install_shard_context(
                        start,
                        end,
                        {},
                    )
                    try:
                        critic_marker = _poca_cuda_timing_begin("critic_pass")
                        values, _ = optimizer.critic.critic_pass(
                            [obs] + group_obs,
                            memories=[],
                            sequence_length=optimizer.policy.sequence_length,
                        )
                        _poca_cuda_timing_end(critic_marker)
                    finally:
                        reset_training_faction_rows(faction_token)
                    value_outputs.append(values)

                with torch.cuda.stream(baseline_stream):
                    baseline_stream.wait_stream(default_stream)
                    faction_token = install_shard_context(
                        start,
                        end,
                        {},
                    )
                    try:
                        baseline_marker = _poca_cuda_timing_begin("baseline")
                        baselines, _ = optimizer.critic.baseline(
                            obs,
                            (group_obs, shard_group_act),
                            memories=[],
                            sequence_length=optimizer.policy.sequence_length,
                        )
                        _poca_cuda_timing_end(baseline_marker)
                    finally:
                        reset_training_faction_rows(faction_token)
                    baseline_outputs.append(baselines)
            else:
                stream = streams[shard_index]
                encoded_cache = {}
                with torch.cuda.stream(stream):
                    stream.wait_stream(default_stream)
                    faction_token = install_shard_context(
                        start,
                        end,
                        encoded_cache,
                        actor=True,
                    )
                    try:
                        actor_marker = _poca_cuda_timing_begin(
                            "actor_get_stats"
                        )
                        actor_output = optimizer.policy.actor.get_stats(
                            obs,
                            shard_actions,
                            masks=shard_masks,
                            memories=[],
                            sequence_length=optimizer.policy.sequence_length,
                        )
                        _poca_cuda_timing_end(actor_marker)
                        _POLICY_DIMENSION_MASK_STATE.mask = full_dimension_mask

                        critic_marker = _poca_cuda_timing_begin("critic_pass")
                        values, _ = optimizer.critic.critic_pass(
                            [obs] + group_obs,
                            memories=[],
                            sequence_length=optimizer.policy.sequence_length,
                        )
                        _poca_cuda_timing_end(critic_marker)

                        baseline_marker = _poca_cuda_timing_begin("baseline")
                        baselines, _ = optimizer.critic.baseline(
                            obs,
                            (group_obs, shard_group_act),
                            memories=[],
                            sequence_length=optimizer.policy.sequence_length,
                        )
                        _poca_cuda_timing_end(baseline_marker)
                    finally:
                        reset_training_faction_rows(faction_token)
                    actor_outputs.append(actor_output)
                    value_outputs.append(values)
                    baseline_outputs.append(baselines)

        _poca_record_timing(
            "parallel_forward_submit",
            time.perf_counter() - submit_started,
        )
        join_started = time.perf_counter()
        for stream in streams:
            default_stream.wait_stream(stream)
        _poca_cuda_timing_end(span_marker)
        _poca_record_timing(
            "parallel_forward_join",
            time.perf_counter() - join_started,
        )
        _POCA_TIMING_STATE.parallel_streams = streams

        def cat_optional(values):
            non_null = [value for value in values if value is not None]
            return torch.cat(non_null, dim=0) if non_null else None

        log_probs = ActionLogProbs(
            cat_optional(
                [output["log_probs"].continuous_tensor for output in actor_outputs]
            ),
            (
                [
                    torch.cat(parts, dim=0)
                    for parts in zip(
                        *[
                            output["log_probs"].discrete_list
                            for output in actor_outputs
                        ]
                    )
                ]
                if actor_outputs
                and actor_outputs[0]["log_probs"].discrete_list is not None
                else None
            ),
            (
                [
                    torch.cat(parts, dim=0)
                    for parts in zip(
                        *[
                            output["log_probs"].all_discrete_list
                            for output in actor_outputs
                        ]
                    )
                ]
                if actor_outputs
                and actor_outputs[0]["log_probs"].all_discrete_list is not None
                else None
            ),
        )
        entropy = torch.cat(
            [output["entropy"] for output in actor_outputs],
            dim=0,
        )
        values = {
            name: torch.cat(
                [output[name] for output in value_outputs],
                dim=0,
            )
            for name in value_outputs[0]
        }
        baselines = {
            name: torch.cat(
                [output[name] for output in baseline_outputs],
                dim=0,
            )
            for name in baseline_outputs[0]
        }
        return log_probs, entropy, values, baselines
    finally:
        _POCA_GROUP_BATCH_STATE.valid_rows = full_valid_rows
        _POCA_GROUP_BATCH_STATE.encoded_cache = full_encoded_cache
        _POCA_GROUP_BATCH_STATE.freeze_max_agents = full_freeze
        _POLICY_DIMENSION_MASK_STATE.mask = full_dimension_mask




class _PocaCpuMinibatchPreparer:
    """Prepare and pin CPU minibatches concurrently; CUDA stays on its default stream."""

    def __init__(self, cache):
        from concurrent.futures import ThreadPoolExecutor
        from mlagents.torch_utils import default_device, torch

        self.cache = cache
        self.device = default_device()
        self.full_minibatch = bool(
            cache is not None
            and str(cache.get("storage", "cpu")) == "cpu"
        )
        self.group_source = (
            cache
            if self.full_minibatch
            else (
                cache.get("_cpu_group_source")
                if cache is not None
                else None
            )
        )
        logical_cpus = max(1, int(os.cpu_count() or 1))
        self.workers = max(
            1,
            logical_cpus - POCA_CPU_PREPARE_RESERVED_LOGICAL_CPUS,
        )
        self.enabled = bool(
            cache is not None
            and self.group_source is not None
            and _POCA_OPTIMIZATIONS.effective_sync_cleanup
            and _POCA_OPTIMIZATIONS.stream_shards == 1
            and not _POCA_OPTIMIZATIONS.critic_baseline_overlap
            and not _POCA_OPTIMIZATIONS.minibatch_prefetch
            and self.device.type == "cuda"
            and torch.cuda.is_available()
        )
        self.executor = (
            ThreadPoolExecutor(
                max_workers=self.workers,
                thread_name_prefix="bees-poca-cpu-prepare",
            )
            if self.enabled
            else None
        )
        self.depth = self.workers if self.enabled else 0

    def submit(self, indices):
        if not self.enabled:
            return None
        import numpy as np

        copied = np.asarray(indices, dtype=np.int64).copy()
        return self.executor.submit(self._prepare, copied)

    def _prepare(self, indices):
        from mlagents.torch_utils import torch

        started = time.perf_counter()
        observation_source = _poca_select_group_prepare_source(
            self.group_source,
            indices,
        )
        dedup_plan = _poca_build_observation_dedup_plan(
            observation_source
        )
        if self.full_minibatch:
            selected = _select_poca_update_tensor_cache(
                self.cache,
                indices,
                device_override=torch.device("cpu"),
                non_blocking=False,
                groupmate_obs_override=[],
            )
            selected["prepared_groupmate_obs"] = (
                observation_source["prepared_groupmate_obs"]
            )
            selected["observation_dedup"] = dedup_plan
        else:
            selected = {
                "prepared_groupmate_obs": (
                    observation_source["prepared_groupmate_obs"]
                ),
                "observation_dedup": dedup_plan,
            }
        selected = _poca_pin_selected_minibatch(selected)
        return selected, time.perf_counter() - started

    def consume(self, future):
        if future is None:
            return None
        wait_started = time.perf_counter()
        selected, prepare_seconds = future.result()
        _poca_record_timing(
            "cpu_prepare_wait",
            time.perf_counter() - wait_started,
        )
        _poca_record_timing("cpu_prepare_work", prepare_seconds)

        plan = selected.get("observation_dedup")
        if plan is not None:
            _POCA_TIMING_STATE.dedup_input_rows += int(
                plan.get("input_rows", 0)
            )
            _POCA_TIMING_STATE.dedup_unique_rows += int(
                plan.get("unique_rows", 0)
            )

        transfer_started = time.perf_counter()
        marker = _poca_cuda_timing_begin("cpu_prepare_transfer")
        selected = _poca_move_selected_minibatch(
            selected,
            self.device,
        )
        prepared_group = selected.pop(
            "prepared_groupmate_obs",
            None,
        )
        if prepared_group is not None:
            selected["groupmate_obs"] = (
                _poca_expand_prepared_group_obs(
                    prepared_group,
                    self.device,
                )
            )
        _poca_cuda_timing_end(marker)
        _poca_record_timing(
            "cpu_prepare_transfer_submit",
            time.perf_counter() - transfer_started,
        )
        return selected

    def close(self):
        if self.executor is not None:
            self.executor.shutdown(wait=True)
            self.executor = None


class _PocaMinibatchPrefetcher:
    """Prepare the next cached minibatch on a worker thread and CUDA copy stream."""

    def __init__(self, cache):
        from concurrent.futures import ThreadPoolExecutor
        from mlagents.torch_utils import default_device, torch

        self.cache = cache
        self.device = default_device()
        self.enabled = bool(
            _POCA_OPTIMIZATIONS.minibatch_prefetch
            and self.device.type == "cuda"
            and torch.cuda.is_available()
        )
        self.executor = ThreadPoolExecutor(
            max_workers=1,
            thread_name_prefix="bees-poca-prefetch",
        ) if self.enabled else None
        self.stream = torch.cuda.Stream() if self.enabled else None

    def submit(self, indices):
        if not self.enabled:
            return None
        import numpy as np

        copied = np.asarray(indices, dtype=np.int64).copy()
        return self.executor.submit(self._prepare, copied)

    def _prepare(self, indices):
        from mlagents.torch_utils import torch

        with torch.cuda.stream(self.stream):
            start = torch.cuda.Event(enable_timing=True)
            end = torch.cuda.Event(enable_timing=True)
            start.record()
            selected = _select_poca_update_tensor_cache(
                self.cache,
                indices,
                device_override=self.device,
                non_blocking=True,
            )
            end.record()
        # Keep temporary pinned sources alive until all asynchronous copies finish.
        end.synchronize()
        return selected, ("prefetch", start, end)

    def consume(self, future):
        if future is None:
            return None
        wait_started = time.perf_counter()
        selected, marker = future.result()
        _poca_record_timing(
            "prefetch_cpu_wait",
            time.perf_counter() - wait_started,
        )
        events = getattr(_POCA_TIMING_STATE, "cuda_events", None)
        if events is not None:
            events.append(marker)
        return selected

    def close(self):
        if self.executor is not None:
            self.executor.shutdown(wait=True)
            self.executor = None



def _poca_cuda_graph_minibatch_eligible(cached) -> bool:
    """Require all conditionally executed faction actor parameters to participate."""

    if not _POCA_OPTIMIZATIONS.cuda_graphs or cached is None:
        return False
    rows = cached.get("faction_rows")
    if not isinstance(rows, dict):
        return False
    bee = len(rows.get("bee", ()))
    human = len(rows.get("human", ()))
    mixed = len(rows.get("mixed", ()))
    return bool((bee or mixed) and (human or mixed))


def _poca_optimizer_step(optimizer, *, graph_eligible: bool = False) -> None:
    """Execute Adam eagerly or replay a captured CUDA optimizer step."""

    opts = _POCA_OPTIMIZATIONS
    if not opts.cuda_graphs:
        optimizer.step()
        return

    from mlagents.torch_utils import torch

    if not torch.cuda.is_available():
        optimizer.step()
        return

    state = getattr(optimizer, "_bees_cuda_graph_state", None)
    if state is None:
        state = {
            "prepared": False,
            "graph": None,
            "signature": None,
            "learning_rates": None,
            "captures": 0,
            "replays": 0,
            "warmups": 0,
            "stable_grads": False,
        }
        optimizer._bees_cuda_graph_state = state

    def all_parameters():
        return [
            parameter
            for group in optimizer.param_groups
            for parameter in group["params"]
        ]

    def active_parameters():
        return [
            parameter
            for parameter in all_parameters()
            if parameter.grad is not None
        ]

    def eager_step_and_invalidate() -> None:
        for group in optimizer.param_groups:
            group["capturable"] = False
        optimizer.step()
        state["graph"] = None
        state["signature"] = None
        state["learning_rates"] = None
        state["stable_grads"] = False

    def migrate_step_state() -> None:
        for group in optimizer.param_groups:
            group["capturable"] = True
            for parameter in group["params"]:
                parameter_state = optimizer.state.get(parameter)
                if not parameter_state:
                    continue
                step = parameter_state.get("step")
                if step is not None and step.device != parameter.device:
                    parameter_state["step"] = step.to(device=parameter.device)

    parameters = active_parameters()
    complete_parameter_set = len(parameters) == len(all_parameters())
    if not graph_eligible or not complete_parameter_set:
        eager_step_and_invalidate()
        _POCA_TIMING_STATE.graph_skips = int(
            getattr(_POCA_TIMING_STATE, "graph_skips", 0)
        ) + 1
        return

    needs_state = any(not optimizer.state.get(parameter) for parameter in parameters)
    if not state["prepared"] or needs_state:
        # Run this minibatch's real step eagerly so Adam can lazily allocate its
        # moment tensors. The next fully active minibatch can retain these gradient
        # buffers for graph replay without changing None-vs-zero gradient semantics.
        eager_step_and_invalidate()
        migrate_step_state()
        state["prepared"] = True
        state["stable_grads"] = True
        state["warmups"] += 1
        _POCA_TIMING_STATE.graph_warmups = int(
            getattr(_POCA_TIMING_STATE, "graph_warmups", 0)
        ) + 1
        return

    migrate_step_state()
    state["stable_grads"] = True
    signature = tuple(
        (
            id(parameter),
            int(parameter.data_ptr()),
            int(parameter.grad.data_ptr()),
            tuple(parameter.shape),
            str(parameter.dtype),
        )
        for parameter in parameters
    )
    learning_rates = tuple(
        float(group["lr"])
        for group in optimizer.param_groups
    )

    if (
        state["graph"] is not None
        and state["signature"] == signature
        and state["learning_rates"] == learning_rates
    ):
        replay_started = time.perf_counter()
        state["graph"].replay()
        state["replays"] += 1
        _POCA_TIMING_STATE.graph_replays = int(
            getattr(_POCA_TIMING_STATE, "graph_replays", 0)
        ) + 1
        _poca_record_timing(
            "cuda_graph_replay_submit",
            time.perf_counter() - replay_started,
        )
        return

    # Capturing executes this minibatch's real optimizer step exactly once.
    # If capture fails, propagate the failure rather than risk double-stepping.
    capture_started = time.perf_counter()
    torch.cuda.synchronize()
    graph = torch.cuda.CUDAGraph()
    with torch.cuda.graph(graph):
        optimizer.step()
    state["graph"] = graph
    state["signature"] = signature
    state["learning_rates"] = learning_rates
    state["captures"] += 1
    _POCA_TIMING_STATE.graph_captures = int(
        getattr(_POCA_TIMING_STATE, "graph_captures", 0)
    ) + 1
    _poca_record_timing(
        "cuda_graph_capture",
        time.perf_counter() - capture_started,
    )


def install_inactive_continuous_action_masking() -> Optional[Callable]:
    """Mask nonexistent weapon actions and normalize MA-POCA fleet-size gradients."""

    from mlagents.trainers.buffer import BufferKey
    from mlagents.trainers.poca.optimizer_torch import TorchPOCAOptimizer
    from mlagents.trainers.poca.trainer import POCATrainer
    from mlagents.trainers.torch_entities.components.bc.module import BCModule
    from mlagents.trainers.ppo.optimizer_torch import TorchPPOOptimizer
    from mlagents.trainers.torch_entities.action_model import ActionModel
    from mlagents.trainers.torch_entities.networks import MultiAgentNetworkBody
    from mlagents.trainers.torch_entities.utils import ModelUtils

    global _ORIGINAL_ACTION_MODEL_FORWARD
    global _ORIGINAL_ACTION_MODEL_EVALUATE
    global _ORIGINAL_PPO_UPDATE
    global _ORIGINAL_POCA_UPDATE
    global _ORIGINAL_POCA_TRAJECTORY_VALUES
    global _ORIGINAL_POCA_UPDATE_POLICY
    global _ORIGINAL_POCA_ADVANCE
    global _ORIGINAL_POCA_SAVE_MODEL
    global _ORIGINAL_MULTI_AGENT_FORWARD
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
    original_poca_trajectory_values = (
        TorchPOCAOptimizer.get_trajectory_and_baseline_value_estimates
    )
    original_poca_update_policy = POCATrainer._update_policy
    original_poca_advance = POCATrainer.advance
    original_poca_save_model = POCATrainer.save_model
    original_poca_process_trajectory = POCATrainer._process_trajectory
    original_multi_agent_forward = MultiAgentNetworkBody.forward
    original_policy_loss = ModelUtils.trust_region_policy_loss
    original_masked_mean = ModelUtils.masked_mean
    original_bc_update_batch = BCModule._update_batch
    original_bc_loss = BCModule._behavioral_cloning_loss

    def optimized_multi_agent_forward(
        self,
        obs_only,
        obs,
        actions,
        memories=None,
        sequence_length=1,
    ):
        """Batch Bees group-member encoding and reuse it across POCA value/baseline."""

        valid_rows = getattr(_POCA_GROUP_BATCH_STATE, "valid_rows", None)
        cache = getattr(_POCA_GROUP_BATCH_STATE, "encoded_cache", None)
        dedup_plan = getattr(_POCA_GROUP_BATCH_STATE, "dedup_plan", None)
        if (
            valid_rows is None
            or cache is None
            or not getattr(self.observation_encoder, "_bees", False)
            or len(getattr(self.observation_encoder, "processors", ())) != 1
        ):
            return original_multi_agent_forward(
                self,
                obs_only,
                obs,
                actions,
                memories=memories,
                sequence_length=sequence_length,
            )

        from mlagents.torch_utils import torch

        groupmate_count = len(valid_rows)
        if len(obs) > groupmate_count or max(0, len(obs_only) - 1) > groupmate_count:
            return original_multi_agent_forward(
                self,
                obs_only,
                obs,
                actions,
                memories=memories,
                sequence_length=sequence_length,
            )
        if obs and len(actions) != len(obs):
            return original_multi_agent_forward(
                self,
                obs_only,
                obs,
                actions,
                memories=memories,
                sequence_length=sequence_length,
            )

        reference_members = obs_only if obs_only else obs
        if not reference_members or len(reference_members[0]) != 1:
            return original_multi_agent_forward(
                self,
                obs_only,
                obs,
                actions,
                memories=memories,
                sequence_length=sequence_length,
            )
        batch_size = int(reference_members[0][0].shape[0])
        encoded_size = int(self.observation_encoder.total_enc_size)

        def dedup_unique_encoding():
            if dedup_plan is None:
                return None
            unique_obs = dedup_plan.get("unique_obs")
            if (
                not isinstance(unique_obs, list)
                or len(unique_obs) != 1
                or unique_obs[0].device != reference_members[0][0].device
            ):
                return None
            cache_key = ("dedup-unique", id(self))
            encoded = cache.get(cache_key)
            if encoded is None:
                encoded = self.observation_encoder(unique_obs)
                cache[cache_key] = encoded
            return encoded

        def encode_members(
            members,
            member_valid_rows,
            dedup_mappings=None,
        ):
            outputs = [None] * len(members)
            pending = []
            unique_encoded = (
                dedup_unique_encoding()
                if dedup_mappings is not None
                else None
            )

            for position, (member, valid) in enumerate(zip(members, member_valid_rows)):
                if len(member) != 1 or int(member[0].shape[0]) != batch_size:
                    raise RuntimeError(
                        "Bees POCA optimized group encoder received an unexpected observation shape."
                    )
                source = member[0]
                cache_key = id(source)
                cached = cache.get(cache_key)
                if cached is not None:
                    outputs[position] = cached
                    continue

                mapping = (
                    dedup_mappings[position]
                    if (
                        unique_encoded is not None
                        and position < len(dedup_mappings)
                    )
                    else None
                )
                if mapping is not None:
                    count = batch_size if valid is None else int(len(valid))
                    expected = count
                    if (
                        mapping.device == source.device
                        and int(mapping.numel()) == expected
                    ):
                        encoded = _poca_expand_dedup_encoding(
                            unique_encoded,
                            mapping,
                            valid,
                            batch_size,
                        )
                        if int(encoded.shape[1]) != encoded_size:
                            raise RuntimeError(
                                "POCA dedup encoded width changed unexpectedly."
                            )
                        cache[cache_key] = encoded
                        outputs[position] = encoded
                        continue

                count = batch_size if valid is None else int(len(valid))
                if count == 0:
                    encoded = source.new_zeros((batch_size, encoded_size))
                    cache[cache_key] = encoded
                    outputs[position] = encoded
                    continue
                pending.append((position, member, valid, count, cache_key))

            offset = 0
            while offset < len(pending):
                chunk = []
                chunk_rows = 0
                while offset < len(pending):
                    record = pending[offset]
                    if chunk and chunk_rows + record[3] > POCA_ENCODER_CHUNK_ROWS:
                        break
                    chunk.append(record)
                    chunk_rows += record[3]
                    offset += 1

                selected = []
                prepared = []
                for position, member, valid, count, cache_key in chunk:
                    source = member[0]
                    if valid is None:
                        selected_source = source
                        index = None
                    else:
                        index = torch.as_tensor(
                            valid,
                            dtype=torch.long,
                            device=source.device,
                        )
                        selected_source = source.index_select(0, index)
                    selected.append(selected_source)
                    prepared.append(
                        (position, valid, count, cache_key, index, source)
                    )

                merged = selected[0] if len(selected) == 1 else torch.cat(selected, dim=0)
                merged_encoded = self.observation_encoder([merged])

                encoded_offset = 0
                for position, valid, count, cache_key, index, source in prepared:
                    part = merged_encoded[encoded_offset : encoded_offset + count]
                    encoded_offset += count
                    if valid is None:
                        encoded = part
                    else:
                        encoded = part.new_zeros((batch_size, encoded_size)).index_copy(
                            0,
                            index,
                            part,
                        )
                    cache[cache_key] = encoded
                    outputs[position] = encoded

            return outputs

        self_attn_masks = []
        self_attn_inputs = []

        group_dedup_mappings = (
            dedup_plan.get("group_inverse", ())
            if dedup_plan is not None
            else None
        )
        current_dedup_mapping = (
            dedup_plan.get("current_inverse")
            if dedup_plan is not None
            else None
        )

        if obs:
            obs_attn_mask = self._get_masks_from_nans(obs)
            encoded_obs = encode_members(
                obs,
                valid_rows[: len(obs)],
                (
                    group_dedup_mappings[: len(obs)]
                    if group_dedup_mappings is not None
                    else None
                ),
            )
            concat_f_inp = []
            for encoded, action in zip(encoded_obs, actions):
                concat_f_inp.append(
                    torch.cat(
                        [
                            encoded,
                            action.to_flat(self.action_spec.discrete_branches),
                        ],
                        dim=1,
                    )
                )
            f_inp = torch.stack(concat_f_inp, dim=1)
            self_attn_masks.append(obs_attn_mask)
            self_attn_inputs.append(self.obs_action_encoder(None, f_inp))

        if obs_only:
            obs_only_attn_mask = self._get_masks_from_nans(obs_only)
            obs_only_valid_rows = [None]
            if len(obs_only) > 1:
                obs_only_valid_rows.extend(valid_rows[: len(obs_only) - 1])
            obs_only_dedup_mappings = None
            if (
                current_dedup_mapping is not None
                and group_dedup_mappings is not None
            ):
                obs_only_dedup_mappings = [
                    current_dedup_mapping,
                    *group_dedup_mappings[: max(0, len(obs_only) - 1)],
                ]
            encoded_obs_only = encode_members(
                obs_only,
                obs_only_valid_rows,
                obs_only_dedup_mappings,
            )
            g_inp = torch.stack(encoded_obs_only, dim=1)
            self_attn_masks.append(obs_only_attn_mask)
            self_attn_inputs.append(self.obs_encoder(None, g_inp))

        if not self_attn_inputs:
            return original_multi_agent_forward(
                self,
                obs_only,
                obs,
                actions,
                memories=memories,
                sequence_length=sequence_length,
            )

        encoded_entity = torch.cat(self_attn_inputs, dim=1)
        encoded_state = self.self_attn(encoded_entity, self_attn_masks)

        flipped_masks = 1 - torch.cat(self_attn_masks, dim=1)
        num_agents = torch.sum(flipped_masks, dim=1, keepdim=True)
        freeze_max_agents = bool(
            getattr(_POCA_GROUP_BATCH_STATE, "freeze_max_agents", False)
        )
        if not freeze_max_agents:
            if _POCA_OPTIMIZATIONS.effective_sync_cleanup:
                # Keep the running POCA normalization maximum entirely on-device. Calling
                # Tensor.item() here forces a host/device rendezvous twice per minibatch
                # (value + baseline) and drains CUDA's launch queue.
                with torch.no_grad():
                    candidate = torch.max(num_agents).to(
                        dtype=self._current_max_agents.dtype,
                        device=self._current_max_agents.device,
                    )
                    self._current_max_agents.copy_(
                        torch.maximum(self._current_max_agents, candidate)
                    )
            else:
                max_agents = torch.max(num_agents).item()
                if max_agents > self._current_max_agents:
                    self._current_max_agents = torch.nn.Parameter(
                        torch.as_tensor(max_agents),
                        requires_grad=False,
                    )

        num_agents = num_agents * 2.0 / self._current_max_agents - 1
        encoding = self.linear_encoder(encoded_state)
        if self.use_lstm:
            encoding = encoding.reshape([-1, sequence_length, self.h_size])
            encoding, memories = self.lstm(encoding, memories)
            encoding = encoding.reshape([-1, self.m_size // 2])
        encoding = torch.cat([encoding, num_agents], dim=1)
        return encoding, memories

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

    def _poca_async_update_buffer():
        return getattr(_POCA_ASYNC_UPDATE_STATE, "buffer", None)

    def _run_poca_async_update(trainer, frozen_buffer):
        # ML-Agents configures a process-wide preferred device, but PyTorch's temporary
        # default-device TorchFunctionMode must be entered on the thread that creates
        # implicit tensors. Do not call torch.set_default_device() here: in PyTorch
        # 2.1.x that function owns one global DeviceContext and attempts to exit the
        # trainer thread's context from this worker thread.
        from mlagents.torch_utils import default_device, torch

        device = default_device()
        _POCA_ASYNC_UPDATE_STATE.buffer = frozen_buffer
        try:
            if getattr(device, "type", None) == "cuda":
                with torch.cuda.device(device), torch.device(device):
                    return trainer._update_policy()
            with torch.device(device):
                return trainer._update_policy()
        finally:
            _POCA_ASYNC_UPDATE_STATE.buffer = None

    def _ensure_poca_pipeline_state(trainer):
        from collections import deque
        from concurrent.futures import ThreadPoolExecutor

        if getattr(trainer, "_bees_poca_update_executor", None) is None:
            trainer._bees_poca_update_executor = ThreadPoolExecutor(
                max_workers=1,
                thread_name_prefix="bees-poca-update",
            )
            trainer._bees_poca_update_future = None
            trainer._bees_poca_staged_trajectories = deque()
            trainer._bees_poca_staged_experiences = 0

    def _stage_poca_trajectories(trainer):
        from mlagents.trainers.agent_processor import AgentManagerQueue

        if getattr(trainer, "_bees_poca_staged_trajectories", None) is None:
            from collections import deque

            trainer._bees_poca_staged_trajectories = deque()
            trainer._bees_poca_staged_experiences = 0
        staged = trainer._bees_poca_staged_trajectories
        staged_experiences = int(trainer._bees_poca_staged_experiences)
        staging_limit = max(
            int(trainer.hyperparameters.buffer_size) * 2,
            int(trainer.hyperparameters.buffer_size)
            + POCA_TRAJECTORY_BATCH_MAX_EXPERIENCES,
        )
        queried = False
        for trajectory_queue in trainer.trajectory_queues:
            queue_size = trajectory_queue.qsize()
            for _ in range(queue_size):
                if staged_experiences >= staging_limit:
                    break
                try:
                    trajectory = trajectory_queue.get_nowait()
                except AgentManagerQueue.Empty:
                    break
                queried = True
                buffer = trajectory.to_agentbuffer()
                staged.append((trajectory, buffer))
                staged_experiences += len(trajectory.steps)
        trainer._bees_poca_staged_experiences = staged_experiences
        return queried

    def _drain_prepared_poca_trajectories(trainer):
        staged = trainer._bees_poca_staged_trajectories
        pending = []
        pending_experiences = 0

        def next_bookkeeping_boundary():
            next_summary = trainer._next_summary_step
            if next_summary == 0:
                next_summary = trainer._get_next_interval_step(
                    trainer.summary_freq
                )
            next_save = trainer._next_save_step
            if next_save == 0:
                next_save = trainer._get_next_interval_step(
                    trainer.trainer_settings.checkpoint_interval
                )
            candidates = [
                step
                for step in (next_summary, next_save)
                if step > trainer.get_step
            ]
            return min(candidates) if candidates else None

        def remove_staged():
            trajectory, buffer = staged.popleft()
            trainer._bees_poca_staged_experiences = max(
                0,
                int(trainer._bees_poca_staged_experiences)
                - len(trajectory.steps),
            )
            return trajectory, buffer

        def flush_pending():
            nonlocal pending
            nonlocal pending_experiences
            if not pending:
                return
            _process_prepared_poca_trajectory_batch(
                trainer,
                [record[0] for record in pending],
                [record[1] for record in pending],
            )
            pending = []
            pending_experiences = 0

        while staged and not trainer._is_ready_update():
            trajectory, buffer = staged[0]
            trajectory_experiences = len(trajectory.steps)
            boundary = next_bookkeeping_boundary()

            if (
                pending
                and boundary is not None
                and trainer.get_step
                + pending_experiences
                + trajectory_experiences
                >= boundary
            ):
                flush_pending()
                continue

            trajectory, buffer = remove_staged()
            if (
                not pending
                and boundary is not None
                and trainer.get_step != 0
                and trainer.get_step + trajectory_experiences >= boundary
            ):
                original_poca_process_trajectory(trainer, trajectory)
                continue

            if not trajectory.steps:
                flush_pending()
                original_poca_process_trajectory(trainer, trajectory)
                continue

            if (
                pending
                and pending_experiences + trajectory_experiences
                > POCA_TRAJECTORY_BATCH_MAX_EXPERIENCES
            ):
                flush_pending()

            pending.append((trajectory, buffer))
            pending_experiences += trajectory_experiences

        flush_pending()

    def _finish_poca_async_update(trainer):
        future = getattr(trainer, "_bees_poca_update_future", None)
        if future is None or not future.done():
            return False
        updated = future.result()
        trainer._bees_poca_update_future = None
        if updated:
            for policy_queue in trainer.policy_queues:
                policy_queue.put(
                    trainer.get_policy(policy_queue.behavior_id)
                )
        print(
            "[Bees PPO pipeline] async update complete "
            f"staged_experiences={int(trainer._bees_poca_staged_experiences)}",
            flush=True,
        )
        return True

    def _start_poca_async_update(trainer):
        from mlagents.trainers.buffer import AgentBuffer

        _ensure_poca_pipeline_state(trainer)
        if trainer._bees_poca_update_future is not None:
            return False
        frozen_buffer = trainer.update_buffer
        frozen_experiences = int(frozen_buffer.num_experiences)
        trainer.update_buffer = AgentBuffer()
        # Stock POCA clears this at update start. No trajectory completion occurs while
        # the async optimizer owns the model, so clearing here preserves that boundary.
        trainer.cumulative_returns_since_policy_update.clear()
        trainer._bees_poca_update_future = (
            trainer._bees_poca_update_executor.submit(
                _run_poca_async_update,
                trainer,
                frozen_buffer,
            )
        )
        print(
            "[Bees PPO pipeline] async update started "
            f"buffer={frozen_experiences} "
            f"staged_experiences={int(trainer._bees_poca_staged_experiences)}",
            flush=True,
        )
        return True

    def batched_poca_advance(self):
        """Pipeline WAN trajectory CPU staging with a frozen-buffer PPO update."""

        if self.policy is None or self.policy.use_recurrent:
            return original_poca_advance(self)
        behavior_spec = self.policy.behavior_spec
        observation_specs = behavior_spec.observation_specs
        if (
            not _is_bees_action_spec(behavior_spec.action_spec)
            or len(observation_specs) != 1
            or tuple(observation_specs[0].shape) != (BEES_OBSERVATION_SIZE,)
        ):
            return original_poca_advance(self)

        from mlagents_envs.timers import hierarchical_timer

        if self.threaded:
            _ensure_poca_pipeline_state(self)
            _finish_poca_async_update(self)
        else:
            if getattr(self, "_bees_poca_staged_trajectories", None) is None:
                from collections import deque

                self._bees_poca_staged_trajectories = deque()
                self._bees_poca_staged_experiences = 0

        queried = False
        with hierarchical_timer("process_trajectory"):
            queried = _stage_poca_trajectories(self)

            # The optimizer owns the policy/critic while it updates a frozen buffer.
            # Keep doing CPU-only trajectory materialization, but do not mutate
            # normalization, stats, checkpoints, or model state until it finishes.
            if getattr(self, "_bees_poca_update_future", None) is not None:
                if self.threaded and not queried:
                    time.sleep(0.0001)
                return

            _drain_prepared_poca_trajectories(self)

        if self.should_still_train and self._is_ready_update():
            if self.threaded:
                _start_poca_async_update(self)
            else:
                with hierarchical_timer("_update_policy"):
                    if self._update_policy():
                        for policy_queue in self.policy_queues:
                            policy_queue.put(
                                self.get_policy(policy_queue.behavior_id)
                            )
        elif self.threaded and not queried:
            time.sleep(0.0001)


    def pipelined_poca_save_model(self):
        """Wait for an in-flight asynchronous PPO update before final checkpoint export."""

        future = getattr(self, "_bees_poca_update_future", None)
        if future is not None:
            future.result()
            self._bees_poca_update_future = None
        executor = getattr(self, "_bees_poca_update_executor", None)
        if executor is not None:
            executor.shutdown(wait=True)
            self._bees_poca_update_executor = None
        return original_poca_save_model(self)

    def weighted_poca_update_policy(self):
        """ML-Agents 1.1.0 on-policy update with cached feed-forward minibatch tensors."""

        import numpy as np
        from collections import defaultdict

        async_buffer = _poca_async_update_buffer()
        update_buffer = self.update_buffer if async_buffer is None else async_buffer
        buffer_length = update_buffer.num_experiences
        if async_buffer is None:
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

        update_started = time.perf_counter()
        _POCA_TIMING_STATE.timing_totals = {}
        _POCA_TIMING_STATE.timing_counts = {}
        _POCA_TIMING_STATE.timing_samples = {}
        _POCA_TIMING_STATE.cuda_events = []
        _POCA_TIMING_STATE.graph_warmups = 0
        _POCA_TIMING_STATE.graph_captures = 0
        _POCA_TIMING_STATE.graph_replays = 0
        _POCA_TIMING_STATE.graph_skips = 0
        _POCA_TIMING_STATE.parallel_streams = ()
        _POCA_TIMING_STATE.dedup_input_rows = 0
        _POCA_TIMING_STATE.dedup_unique_rows = 0

        normalization_started = time.perf_counter()
        _normalize_poca_advantages(self.policy, update_buffer)
        _poca_record_timing(
            "advantage_normalization",
            time.perf_counter() - normalization_started,
        )

        materialize_started = time.perf_counter()
        tensor_cache = _build_poca_update_tensor_cache(
            self.optimizer,
            update_buffer,
        )
        materialize_seconds = time.perf_counter() - materialize_started
        _poca_record_timing("materialize", materialize_seconds)

        diagnostic_profile_started = time.perf_counter()
        workload_profile = _poca_update_workload_profile(
            tensor_cache,
            {},
        )
        diagnostic_profile_seconds = (
            time.perf_counter() - diagnostic_profile_started
        )
        _poca_record_timing(
            "diagnostic_profile",
            diagnostic_profile_seconds,
        )

        tensor_cache, device_cache = _promote_poca_update_tensor_cache(
            tensor_cache
        )
        workload_profile.update(
            {
                "cache_storage": str(device_cache.get("storage", "off")),
                "cache_mib": float(device_cache.get("bytes", 0))
                / (1024.0 * 1024.0),
                "group_cache_storage": str(
                    device_cache.get("group_storage", "off")
                ),
                "group_cache_mib": float(
                    device_cache.get("group_bytes", 0)
                )
                / (1024.0 * 1024.0),
            }
        )
        _poca_record_timing(
            "device_cache_copy",
            float(device_cache["copy_seconds"]),
        )
        _poca_record_timing(
            "group_cache_copy",
            float(device_cache.get("group_copy_seconds", 0.0)),
        )

        num_epoch = self.hyperparameters.num_epoch
        batch_update_stats = defaultdict(list)
        max_num_batch = buffer_length // batch_size
        total_minibatches = num_epoch * max_num_batch
        cpu_preparer = (
            _PocaCpuMinibatchPreparer(tensor_cache)
            if tensor_cache is not None
            else None
        )
        prefetcher = (
            _PocaMinibatchPrefetcher(tensor_cache)
            if (
                tensor_cache is not None
                and not (cpu_preparer is not None and cpu_preparer.enabled)
            )
            else None
        )
        print(
            "[Bees PPO timing] update begin "
            f"buffer={buffer_length} batch={batch_size} epochs={num_epoch} "
            f"minibatches={total_minibatches} "
            f"sync_cleanup={'on' if _POCA_OPTIMIZATIONS.effective_sync_cleanup else 'off'} "
            f"stream_shards={_POCA_OPTIMIZATIONS.stream_shards} "
            f"prefetch={'on' if _POCA_OPTIMIZATIONS.minibatch_prefetch else 'off'} "
            f"cpu_prepare={'on' if cpu_preparer is not None and cpu_preparer.enabled else 'off'} "
            f"cpu_prepare_workers={cpu_preparer.workers if cpu_preparer is not None and cpu_preparer.enabled else 0} "
            f"cpu_prepare_mode={('full' if cpu_preparer.full_minibatch else 'group-only') if cpu_preparer is not None and cpu_preparer.enabled else 'off'} "
            f"critic_baseline_overlap={'on' if _POCA_OPTIMIZATIONS.critic_baseline_overlap else 'off'} "
            f"cuda_graphs={'on' if _POCA_OPTIMIZATIONS.cuda_graphs else 'off'} "
            f"tensor_cache={'on' if tensor_cache is not None else 'off'} "
            f"cache_storage={device_cache['storage']} "
            f"cache_mib={device_cache['bytes'] / (1024 * 1024):.1f} "
            f"cache_free_mib={device_cache['free_before'] / (1024 * 1024):.1f} "
            f"cache_driver_free_mib={device_cache.get('driver_free_before', 0) / (1024 * 1024):.1f} "
            f"cache_allocator_reusable_mib={device_cache.get('allocator_reusable_before', 0) / (1024 * 1024):.1f} "
            f"cache_reserve_mib={device_cache['reserve'] / (1024 * 1024):.1f} "
            f"cache_cleanup={device_cache.get('empty_cache_seconds', 0.0):.6f} "
            f"fixed_cache_reason={device_cache.get('fixed_cache_reason', 'admitted')} "
            f"group_cache_policy={device_cache.get('group_promotion_reason', 'cpu-resident')} "
            f"group_cache_driver_free_mib={device_cache.get('group_driver_free_before', 0) / (1024 * 1024):.1f} "
            f"group_cache_allocator_reusable_mib={device_cache.get('group_allocator_reusable_before', 0) / (1024 * 1024):.1f} "
            f"group_cache_cleanup={device_cache.get('group_empty_cache_seconds', 0.0):.6f} "
            f"cache_copy={device_cache['copy_seconds']:.6f} "
            f"group_obs={'packed' if tensor_cache is not None and isinstance(tensor_cache.get('groupmate_obs'), _PocaPackedGroupObs) else 'ragged-minibatch'} "
            f"group_cache_storage={device_cache.get('group_storage', 'off')} "
            f"group_cache_mib={device_cache.get('group_bytes', 0) / (1024 * 1024):.1f} "
            f"group_cache_copy={device_cache.get('group_copy_seconds', 0.0):.6f} "
            f"materialize={materialize_seconds:.6f} "
            f"diagnostic_profile={diagnostic_profile_seconds:.6f}",
            flush=True,
        )
        print(
            "[Bees PPO variability] workload "
            f"experiences={int(workload_profile.get('experiences', 0))} "
            f"group_rows={int(workload_profile.get('groupmate_rows', 0))} "
            f"group_mean={workload_profile.get('groupmates_mean', 0.0):.2f} "
            f"group_p50={workload_profile.get('groupmates_p50', 0.0):.1f} "
            f"group_p90={workload_profile.get('groupmates_p90', 0.0):.1f} "
            f"group_p99={workload_profile.get('groupmates_p99', 0.0):.1f} "
            f"group_max={int(workload_profile.get('groupmates_max', 0))} "
            f"factions=bee:{int(workload_profile.get('bee_rows', 0))},"
            f"human:{int(workload_profile.get('human_rows', 0))},"
            f"mixed:{int(workload_profile.get('mixed_rows', 0))} "
            f"slot_rows={int(workload_profile.get('slot_profile_rows', 0))} "
            f"allies=p50:{workload_profile.get('allies_p50', 0.0):.1f},"
            f"p90:{workload_profile.get('allies_p90', 0.0):.1f},"
            f"p99:{workload_profile.get('allies_p99', 0.0):.1f},"
            f"max:{int(workload_profile.get('allies_max', 0))} "
            f"enemies=p50:{workload_profile.get('enemies_p50', 0.0):.1f},"
            f"p90:{workload_profile.get('enemies_p90', 0.0):.1f},"
            f"p99:{workload_profile.get('enemies_p99', 0.0):.1f},"
            f"max:{int(workload_profile.get('enemies_max', 0))} "
            f"collisions=p90:{workload_profile.get('collisions_p90', 0.0):.1f},"
            f"p99:{workload_profile.get('collisions_p99', 0.0):.1f},"
            f"max:{int(workload_profile.get('collisions_max', 0))} "
            f"mining_p90={workload_profile.get('mining_p90', 0.0):.1f} "
            f"map_p90={workload_profile.get('map_objects_p90', 0.0):.1f} "
            f"limits=allies:{int(workload_profile.get('limit_allies', 0))},"
            f"enemies:{int(workload_profile.get('limit_enemies', 0))},"
            f"weapons:{int(workload_profile.get('limit_entity_weapons', 0))},"
            f"mining:{int(workload_profile.get('limit_mining', 0))},"
            f"map:{int(workload_profile.get('limit_map_objects', 0))},"
            f"collisions:{int(workload_profile.get('limit_collisions', 0))} "
            f"cache={workload_profile.get('cache_storage', 'off')}:"
            f"{workload_profile.get('cache_mib', 0.0):.1f}MiB "
            f"group_cache={workload_profile.get('group_cache_storage', 'off')}:"
            f"{workload_profile.get('group_cache_mib', 0.0):.1f}MiB",
            flush=True,
        )

        completed_minibatches = 0
        _POCA_UPDATE_CACHE_STATE.cache = tensor_cache
        hardware_profiler = _PocaUpdateHardwareProfiler().start()
        hardware_started = time.perf_counter()
        hardware_summary = {}
        try:
            for _epoch_index in range(num_epoch):
                epoch_order = None
                if tensor_cache is None:
                    update_buffer.shuffle(
                        sequence_length=self.policy.sequence_length
                    )
                else:
                    epoch_order = np.arange(
                        buffer_length,
                        dtype=np.int64,
                    )
                    np.random.shuffle(epoch_order)

                offsets = list(
                    range(
                        0,
                        max_num_batch * batch_size,
                        batch_size,
                    )
                )
                pending_cpu = {}
                if (
                    tensor_cache is not None
                    and cpu_preparer is not None
                    and cpu_preparer.enabled
                    and offsets
                ):
                    for prepare_index in range(
                        min(cpu_preparer.depth, len(offsets))
                    ):
                        prepare_offset = offsets[prepare_index]
                        pending_cpu[prepare_index] = cpu_preparer.submit(
                            epoch_order[
                                prepare_offset :
                                prepare_offset + batch_size
                            ]
                        )

                pending_prefetch = None
                if (
                    tensor_cache is not None
                    and not pending_cpu
                    and prefetcher is not None
                    and prefetcher.enabled
                    and offsets
                ):
                    first = offsets[0]
                    pending_prefetch = prefetcher.submit(
                        epoch_order[first : first + batch_size]
                    )

                for offset_index, i in enumerate(offsets):
                    completed_minibatches += 1
                    minibatch_started = time.perf_counter()
                    if tensor_cache is None:
                        minibatch = update_buffer.make_mini_batch(
                            i,
                            i + batch_size,
                        )
                        _POCA_UPDATE_CACHE_STATE.indices = None
                    else:
                        indices = epoch_order[i : i + batch_size]
                        if (
                            cpu_preparer is not None
                            and cpu_preparer.enabled
                        ):
                            prepared_cpu = cpu_preparer.consume(
                                pending_cpu.pop(offset_index)
                            )
                            if cpu_preparer.full_minibatch:
                                selected_minibatch = prepared_cpu
                            else:
                                selected_minibatch = (
                                    _select_poca_update_tensor_cache(
                                        tensor_cache,
                                        indices,
                                        groupmate_obs_override=(
                                            prepared_cpu["groupmate_obs"]
                                        ),
                                    )
                                )
                                selected_minibatch[
                                    "observation_dedup"
                                ] = prepared_cpu.get(
                                    "observation_dedup"
                                )
                            _POCA_UPDATE_CACHE_STATE.minibatch = (
                                selected_minibatch
                            )
                            _POCA_UPDATE_CACHE_STATE.indices = None
                            next_prepare_index = (
                                offset_index + cpu_preparer.depth
                            )
                            if next_prepare_index < len(offsets):
                                prepare_offset = offsets[next_prepare_index]
                                pending_cpu[next_prepare_index] = (
                                    cpu_preparer.submit(
                                        epoch_order[
                                            prepare_offset :
                                            prepare_offset + batch_size
                                        ]
                                    )
                                )
                        elif (
                            prefetcher is not None
                            and prefetcher.enabled
                        ):
                            _POCA_UPDATE_CACHE_STATE.minibatch = (
                                prefetcher.consume(pending_prefetch)
                            )
                            _POCA_UPDATE_CACHE_STATE.indices = None
                            next_offset_index = offset_index + 1
                            pending_prefetch = (
                                prefetcher.submit(
                                    epoch_order[
                                        offsets[next_offset_index] :
                                        offsets[next_offset_index] + batch_size
                                    ]
                                )
                                if next_offset_index < len(offsets)
                                else None
                            )
                        else:
                            _POCA_UPDATE_CACHE_STATE.indices = indices
                        # The optimizer reads the selected rows from the cache.
                        # Passing the original buffer preserves the public
                        # ML-Agents optimizer signature without copying fields.
                        minibatch = update_buffer

                    try:
                        update_stats = self.optimizer.update(
                            minibatch,
                            n_sequences,
                        )
                    finally:
                        _POCA_UPDATE_CACHE_STATE.indices = None
                        _POCA_UPDATE_CACHE_STATE.minibatch = None

                    reward_started = time.perf_counter()
                    if tensor_cache is None:
                        reward_stats = self.optimizer.update_reward_signals(
                            minibatch
                        )
                    else:
                        # The cache is enabled only for extrinsic reward, whose
                        # update() is a no-op. Non-extrinsic providers use the
                        # stock AgentBuffer path above.
                        reward_stats = {}
                    _poca_record_timing(
                        "reward_signals",
                        time.perf_counter() - reward_started,
                    )
                    update_stats.update(reward_stats)
                    _poca_record_timing(
                        "minibatch_total",
                        time.perf_counter() - minibatch_started,
                    )
                    for stat_name, value in update_stats.items():
                        batch_update_stats[stat_name].append(value)
        finally:
            hardware_loop_seconds = time.perf_counter() - hardware_started
            hardware_summary = hardware_profiler.stop(
                hardware_loop_seconds
            )
            if cpu_preparer is not None:
                cpu_preparer.close()
            if prefetcher is not None:
                prefetcher.close()
            _POCA_UPDATE_CACHE_STATE.cache = None
            _POCA_UPDATE_CACHE_STATE.indices = None
            _POCA_UPDATE_CACHE_STATE.minibatch = None

        _poca_flush_cuda_timings()
        update_seconds = time.perf_counter() - update_started
        _record_poca_update_busy_seconds(update_seconds)
        print(
            "[Bees PPO timing] update end "
            f"minibatches={completed_minibatches}/{total_minibatches} "
            f"seconds={update_seconds:.6f} "
            f"normalize={_poca_average_timing('advantage_normalization'):.6f} "
            f"materialize={_poca_average_timing('materialize'):.6f} "
            f"diagnostic_profile={_poca_average_timing('diagnostic_profile'):.6f} "
            f"cache_copy={_poca_average_timing('device_cache_copy'):.6f} "
            f"group_cache_copy={_poca_average_timing('group_cache_copy'):.6f} "
            f"cache_storage={device_cache['storage']} "
            f"transfer={_poca_average_timing('cache_transfer'):.6f} "
            f"avg_minibatch={_poca_average_timing('minibatch_total'):.6f} "
            f"prepare={_poca_average_timing('prepare'):.6f} "
            f"decay={_poca_average_timing('decay'):.6f} "
            f"reward_tensors={_poca_average_timing('reward_tensors'):.6f} "
            f"current_obs={_poca_average_timing('current_obs'):.6f} "
            f"group_pad={_poca_average_timing('group_obs_padding'):.6f} "
            f"group_tensors={_poca_average_timing('group_obs_tensors'):.6f} "
            f"action_masks={_poca_average_timing('action_masks'):.6f} "
            f"current_actions={_poca_average_timing('current_actions'):.6f} "
            f"group_actions={_poca_average_timing('group_actions'):.6f} "
            f"memories={_poca_average_timing('memories'):.6f} "
            f"actor={_poca_average_timing('actor_get_stats'):.6f} "
            f"critic={_poca_average_timing('critic_pass'):.6f} "
            f"baseline={_poca_average_timing('baseline'):.6f} "
            f"cuda_actor={_poca_average_timing('cuda_actor_get_stats'):.6f} "
            f"cuda_critic={_poca_average_timing('cuda_critic_pass'):.6f} "
            f"cuda_baseline={_poca_average_timing('cuda_baseline'):.6f} "
            f"old_probs_masks={_poca_average_timing('old_probs_masks'):.6f} "
            f"losses={_poca_average_timing('losses'):.6f} "
            f"learning_rate={_poca_average_timing('learning_rate'):.6f} "
            f"zero_grad={_poca_average_timing('zero_grad'):.6f} "
            f"backward={_poca_average_timing('backward'):.6f} "
            f"optimizer_step={_poca_average_timing('optimizer_step'):.6f} "
            f"cuda_backward={_poca_average_timing('cuda_backward'):.6f} "
            f"cuda_optimizer_step={_poca_average_timing('cuda_optimizer_step'):.6f} "
            f"parallel_submit={_poca_average_timing('parallel_forward_submit'):.6f} "
            f"parallel_join={_poca_average_timing('parallel_forward_join'):.6f} "
            f"cuda_parallel={_poca_average_timing('cuda_parallel_forward'):.6f} "
            f"backward_join={_poca_average_timing('backward_stream_join'):.6f} "
            f"prefetch_wait={_poca_average_timing('prefetch_cpu_wait'):.6f} "
            f"cuda_prefetch={_poca_average_timing('cuda_prefetch'):.6f} "
            f"cpu_prepare_wait={_poca_average_timing('cpu_prepare_wait'):.6f} "
            f"cpu_prepare_work={_poca_average_timing('cpu_prepare_work'):.6f} "
            f"cpu_prepare_transfer={_poca_average_timing('cpu_prepare_transfer_submit'):.6f} "
            f"cuda_cpu_prepare_transfer={_poca_average_timing('cuda_cpu_prepare_transfer'):.6f} "
            f"dedup_rows={int(getattr(_POCA_TIMING_STATE, 'dedup_input_rows', 0))} "
            f"dedup_unique={int(getattr(_POCA_TIMING_STATE, 'dedup_unique_rows', 0))} "
            f"dedup_saved={max(0, int(getattr(_POCA_TIMING_STATE, 'dedup_input_rows', 0)) - int(getattr(_POCA_TIMING_STATE, 'dedup_unique_rows', 0)))} "
            f"graph_warmups={int(getattr(_POCA_TIMING_STATE, 'graph_warmups', 0))} "
            f"graph_captures={int(getattr(_POCA_TIMING_STATE, 'graph_captures', 0))} "
            f"graph_replays={int(getattr(_POCA_TIMING_STATE, 'graph_replays', 0))} "
            f"graph_skips={int(getattr(_POCA_TIMING_STATE, 'graph_skips', 0))} "
            f"graph_capture={_poca_average_timing('cuda_graph_capture'):.6f} "
            f"graph_replay_submit={_poca_average_timing('cuda_graph_replay_submit'):.6f} "
            f"stats={_poca_average_timing('stats'):.6f} "
            f"optimizer_total={_poca_average_timing('optimizer_total'):.6f} "
            f"cuda_optimizer_total={_poca_average_timing('cuda_optimizer_total'):.6f} "
            f"reward={_poca_average_timing('reward_signals'):.6f}",
            flush=True,
        )

        minibatch_distribution = _poca_timing_distribution(
            "minibatch_total"
        )
        cuda_span_distribution = _poca_timing_distribution(
            "cuda_optimizer_total"
        )
        cuda_unattributed = _poca_cuda_unattributed_distribution()
        print(
            "[Bees PPO variability] hardware "
            f"optimizer_wall={hardware_loop_seconds:.3f} "
            f"cpu_samples={int(hardware_summary.get('cpu_samples', 0))} "
            f"gpu_samples={int(hardware_summary.get('gpu_samples', 0))} "
            f"process_cpu_avg={hardware_summary.get('process_cpu_avg', 0.0):.1f} "
            f"process_cpu_max={hardware_summary.get('process_cpu_max', 0.0):.1f} "
            f"process_cpu_equiv={hardware_summary.get('process_cpu_equivalent_percent', 0.0):.1f} "
            f"system_cpu_avg={hardware_summary.get('system_cpu_avg', 0.0):.1f} "
            f"hottest_core_avg={hardware_summary.get('hottest_core_avg', 0.0):.1f} "
            f"hottest_core_max={hardware_summary.get('hottest_core_max', 0.0):.1f} "
            f"rss_max_mib={hardware_summary.get('rss_mib_max', 0.0):.1f} "
            f"ram_percent_max={hardware_summary.get('system_ram_percent_max', 0.0):.1f} "
            f"ram_available_min_mib={hardware_summary.get('system_ram_available_mib_min', 0.0):.1f} "
            f"swap_max={hardware_summary.get('swap_percent_max', 0.0):.1f} "
            f"ctx_voluntary={hardware_summary.get('ctx_voluntary_delta', 0.0):.0f} "
            f"ctx_involuntary={hardware_summary.get('ctx_involuntary_delta', 0.0):.0f} "
            f"gpu_util_avg={hardware_summary.get('gpu_util_avg', 0.0):.1f} "
            f"gpu_util_min={hardware_summary.get('gpu_util_min', 0.0):.1f} "
            f"gpu_util_max={hardware_summary.get('gpu_util_max', 0.0):.1f} "
            f"gpu_mem_util_avg={hardware_summary.get('memory_util_avg', 0.0):.1f} "
            f"gpu_clock_avg={hardware_summary.get('graphics_clock_mhz_avg', 0.0):.0f} "
            f"gpu_clock_min={hardware_summary.get('graphics_clock_mhz_min', 0.0):.0f} "
            f"gpu_clock_max={hardware_summary.get('graphics_clock_mhz_max', 0.0):.0f} "
            f"gpu_mem_clock_avg={hardware_summary.get('memory_clock_mhz_avg', 0.0):.0f} "
            f"temp_max_c={hardware_summary.get('temperature_c_max', 0.0):.1f} "
            f"power_avg_w={hardware_summary.get('power_w_avg', 0.0):.1f} "
            f"power_max_w={hardware_summary.get('power_w_max', 0.0):.1f} "
            f"pstate={hardware_summary.get('pstate', '-')} "
            f"vram_used_max_mib={hardware_summary.get('memory_used_mib_max', 0.0):.1f} "
            f"cuda_peak_alloc_mib={hardware_summary.get('cuda_peak_allocated_mib', 0.0):.1f} "
            f"cuda_peak_reserved_mib={hardware_summary.get('cuda_peak_reserved_mib', 0.0):.1f} "
            f"cuda_free_end_mib={hardware_summary.get('cuda_free_end_mib', 0.0):.1f}",
            flush=True,
        )
        print(
            "[Bees PPO variability] timing "
            f"minibatch_p50={minibatch_distribution['p50']:.6f} "
            f"minibatch_p90={minibatch_distribution['p90']:.6f} "
            f"minibatch_max={minibatch_distribution['max']:.6f} "
            f"cuda_span_p50={cuda_span_distribution['p50']:.6f} "
            f"cuda_span_p90={cuda_span_distribution['p90']:.6f} "
            f"cuda_span_max={cuda_span_distribution['max']:.6f} "
            f"cuda_unattributed_p50={cuda_unattributed['p50']:.6f} "
            f"cuda_unattributed_p90={cuda_unattributed['p90']:.6f} "
            f"cuda_unattributed_max={cuda_unattributed['max']:.6f}",
            flush=True,
        )
        _poca_record_variability_history(
            update_seconds,
            workload_profile,
            hardware_summary,
        )

        _POCA_TIMING_STATE.timing_totals = None
        _POCA_TIMING_STATE.timing_counts = None
        _POCA_TIMING_STATE.timing_samples = None
        _POCA_TIMING_STATE.cuda_events = None
        _POCA_TIMING_STATE.graph_warmups = 0
        _POCA_TIMING_STATE.graph_captures = 0
        _POCA_TIMING_STATE.graph_replays = 0
        _POCA_TIMING_STATE.graph_skips = 0
        _POCA_TIMING_STATE.parallel_streams = ()
        _POCA_TIMING_STATE.dedup_input_rows = 0
        _POCA_TIMING_STATE.dedup_unique_rows = 0

        for stat, stat_list in batch_update_stats.items():
            if (
                _POCA_OPTIMIZATIONS.effective_sync_cleanup
                and stat_list
                and hasattr(stat_list[0], "detach")
            ):
                from mlagents.torch_utils import torch

                stat_value = float(
                    torch.stack([value.detach() for value in stat_list])
                    .mean()
                    .cpu()
                    .item()
                )
            else:
                stat_value = np.mean(stat_list)
            self._stats_reporter.add_stat(stat, stat_value)

        if self.optimizer.bc_module:
            update_stats = self.optimizer.bc_module.update()
            for stat, val in update_stats.items():
                self._stats_reporter.add_stat(stat, val)
        update_buffer.reset_agent()
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

    def compact_poca_trajectory_values(
        self,
        batch,
        next_obs,
        next_groupmate_obs,
        done,
        agent_id="",
    ):
        from mlagents.torch_utils import torch
        from bees_mlagents_structured_policy import (
            reset_training_slot_limits,
            set_training_slot_limits,
        )

        slot_limits = _structured_training_slot_limits(
            self.policy,
            batch,
            extra_observations=(next_obs, next_groupmate_obs),
        )
        slot_token = set_training_slot_limits(slot_limits)
        try:
            # ML-Agents 1.1.0 places its bootstrap critic call outside the
            # internal no_grad block. Keep the entire feed-forward trajectory
            # value path graph-free.
            with torch.no_grad():
                return original_poca_trajectory_values(
                    self,
                    batch,
                    next_obs,
                    next_groupmate_obs,
                    done,
                    agent_id,
                )
        finally:
            reset_training_slot_limits(slot_token)

    def profiled_poca_update(self, batch, num_sequences):
        """Pinned ML-Agents 1.1.0 POCA update with non-overlapping phase timing."""

        from mlagents.trainers.buffer import RewardSignalUtil
        from mlagents.trainers.torch_entities.action_log_probs import ActionLogProbs
        from mlagents.trainers.torch_entities.agent_action import AgentAction
        from mlagents.trainers.trajectory import GroupObsUtil, ObsUtil
        from mlagents.torch_utils import torch

        cached = getattr(_POCA_UPDATE_CACHE_STATE, "minibatch", None)

        started = time.perf_counter()
        decay_lr = self.decay_learning_rate.get_value(self.policy.get_current_step())
        decay_eps = self.decay_epsilon.get_value(self.policy.get_current_step())
        decay_bet = self.decay_beta.get_value(self.policy.get_current_step())
        _poca_record_timing("decay", time.perf_counter() - started)

        started = time.perf_counter()
        if cached is None:
            returns = {}
            old_values = {}
            old_baseline_values = {}
            for name in self.reward_signals:
                old_values[name] = ModelUtils.list_to_tensor(
                    batch[RewardSignalUtil.value_estimates_key(name)]
                )
                returns[name] = ModelUtils.list_to_tensor(
                    batch[RewardSignalUtil.returns_key(name)]
                )
                old_baseline_values[name] = ModelUtils.list_to_tensor(
                    batch[RewardSignalUtil.baseline_estimates_key(name)]
                )
        else:
            returns = cached["returns"]
            old_values = cached["old_values"]
            old_baseline_values = cached["old_baselines"]
        _poca_record_timing("reward_tensors", time.perf_counter() - started)

        started = time.perf_counter()
        n_obs = len(self.policy.behavior_spec.observation_specs)
        if cached is None:
            current_obs = ObsUtil.from_buffer(batch, n_obs)
            current_obs = [ModelUtils.list_to_tensor(obs) for obs in current_obs]
        else:
            current_obs = cached["current_obs"]
        _poca_record_timing("current_obs", time.perf_counter() - started)

        started = time.perf_counter()
        if cached is None:
            groupmate_obs = GroupObsUtil.from_buffer(batch, n_obs)
        else:
            groupmate_obs = cached["groupmate_obs"]
        _poca_record_timing("group_obs_padding", time.perf_counter() - started)

        started = time.perf_counter()
        if cached is None:
            groupmate_obs = [
                [ModelUtils.list_to_tensor(obs) for obs in _groupmate_obs]
                for _groupmate_obs in groupmate_obs
            ]
        _poca_record_timing("group_obs_tensors", time.perf_counter() - started)

        started = time.perf_counter()
        act_masks = (
            ModelUtils.list_to_tensor(batch[BufferKey.ACTION_MASK])
            if cached is None
            else cached["action_masks"]
        )
        _poca_record_timing("action_masks", time.perf_counter() - started)

        started = time.perf_counter()
        actions = (
            AgentAction.from_buffer(batch)
            if cached is None
            else cached["actions"]
        )
        _poca_record_timing("current_actions", time.perf_counter() - started)

        started = time.perf_counter()
        groupmate_actions = (
            AgentAction.group_from_buffer(batch)
            if cached is None
            else cached["groupmate_actions"]
        )
        _poca_record_timing("group_actions", time.perf_counter() - started)

        started = time.perf_counter()
        if cached is None:
            memories = [
                ModelUtils.list_to_tensor(batch[BufferKey.MEMORY][i])
                for i in range(
                    0,
                    len(batch[BufferKey.MEMORY]),
                    self.policy.sequence_length,
                )
            ]
            if len(memories) > 0:
                memories = torch.stack(memories).unsqueeze(0)
            value_memories = [
                ModelUtils.list_to_tensor(batch[BufferKey.CRITIC_MEMORY][i])
                for i in range(
                    0,
                    len(batch[BufferKey.CRITIC_MEMORY]),
                    self.policy.sequence_length,
                )
            ]
            baseline_memories = [
                ModelUtils.list_to_tensor(batch[BufferKey.BASELINE_MEMORY][i])
                for i in range(
                    0,
                    len(batch[BufferKey.BASELINE_MEMORY]),
                    self.policy.sequence_length,
                )
            ]
            if len(value_memories) > 0:
                value_memories = torch.stack(value_memories).unsqueeze(0)
                baseline_memories = torch.stack(baseline_memories).unsqueeze(0)
        else:
            memories = []
            value_memories = []
            baseline_memories = []
        _poca_record_timing("memories", time.perf_counter() - started)

        parallel_forward = _poca_parallel_forward(
            self,
            current_obs,
            actions,
            act_masks,
            groupmate_obs,
            groupmate_actions,
            memories,
            value_memories,
            baseline_memories,
            cached,
        )
        if parallel_forward is None:
            started = time.perf_counter()
            cuda_timing = _poca_cuda_timing_begin("actor_get_stats")
            run_out = self.policy.actor.get_stats(
                current_obs,
                actions,
                masks=act_masks,
                memories=memories,
                sequence_length=self.policy.sequence_length,
            )
            _poca_cuda_timing_end(cuda_timing)
            log_probs = run_out["log_probs"]
            entropy = run_out["entropy"]
            _poca_record_timing("actor_get_stats", time.perf_counter() - started)

            started = time.perf_counter()
            cuda_timing = _poca_cuda_timing_begin("critic_pass")
            all_obs = [current_obs] + groupmate_obs
            values, _ = self.critic.critic_pass(
                all_obs,
                memories=value_memories,
                sequence_length=self.policy.sequence_length,
            )
            _poca_cuda_timing_end(cuda_timing)
            _poca_record_timing("critic_pass", time.perf_counter() - started)

            started = time.perf_counter()
            cuda_timing = _poca_cuda_timing_begin("baseline")
            baselines, _ = self.critic.baseline(
                current_obs,
                (groupmate_obs, groupmate_actions),
                memories=baseline_memories,
                sequence_length=self.policy.sequence_length,
            )
            _poca_cuda_timing_end(cuda_timing)
            _poca_record_timing("baseline", time.perf_counter() - started)
        else:
            log_probs, entropy, values, baselines = parallel_forward

        started = time.perf_counter()
        if cached is None:
            old_log_probs = ActionLogProbs.from_buffer(batch).flatten()
            loss_masks = ModelUtils.list_to_tensor(
                batch[BufferKey.MASKS],
                dtype=torch.bool,
            )
            advantages = ModelUtils.list_to_tensor(
                batch[BufferKey.ADVANTAGES]
            )
        else:
            old_log_probs = cached["old_log_probs"]
            loss_masks = cached["loss_masks"]
            advantages = cached["advantages"]
        log_probs = log_probs.flatten()
        _poca_record_timing("old_probs_masks", time.perf_counter() - started)

        started = time.perf_counter()
        baseline_loss = ModelUtils.trust_region_value_loss(
            baselines,
            old_baseline_values,
            returns,
            decay_eps,
            loss_masks,
        )
        value_loss = ModelUtils.trust_region_value_loss(
            values,
            old_values,
            returns,
            decay_eps,
            loss_masks,
        )
        policy_loss = ModelUtils.trust_region_policy_loss(
            advantages,
            log_probs,
            old_log_probs,
            loss_masks,
            decay_eps,
        )
        loss = (
            policy_loss
            + 0.5 * (value_loss + 0.5 * baseline_loss)
            - decay_bet * ModelUtils.masked_mean(entropy, loss_masks)
        )
        _poca_record_timing("losses", time.perf_counter() - started)

        started = time.perf_counter()
        ModelUtils.update_learning_rate(self.optimizer, decay_lr)
        _poca_record_timing("learning_rate", time.perf_counter() - started)

        graph_eligible = _poca_cuda_graph_minibatch_eligible(cached)
        graph_state = getattr(
            self.optimizer,
            "_bees_cuda_graph_state",
            None,
        )
        retain_graph_grads = bool(
            graph_eligible
            and graph_state is not None
            and graph_state.get("stable_grads", False)
        )
        started = time.perf_counter()
        self.optimizer.zero_grad(
            set_to_none=not retain_graph_grads
        )
        _poca_record_timing("zero_grad", time.perf_counter() - started)

        started = time.perf_counter()
        cuda_timing = _poca_cuda_timing_begin("backward")
        loss.backward()
        _poca_cuda_timing_end(cuda_timing)
        parallel_streams = getattr(_POCA_TIMING_STATE, "parallel_streams", ())
        if parallel_streams:
            join_started = time.perf_counter()
            current_stream = torch.cuda.current_stream()
            for stream in parallel_streams:
                current_stream.wait_stream(stream)
            _poca_record_timing(
                "backward_stream_join",
                time.perf_counter() - join_started,
            )
        _poca_record_timing("backward", time.perf_counter() - started)

        started = time.perf_counter()
        cuda_timing = _poca_cuda_timing_begin("optimizer_step")
        _poca_optimizer_step(
            self.optimizer,
            graph_eligible=graph_eligible,
        )
        _poca_cuda_timing_end(cuda_timing)
        _poca_record_timing("optimizer_step", time.perf_counter() - started)
        _POCA_TIMING_STATE.parallel_streams = ()

        started = time.perf_counter()
        if _POCA_OPTIMIZATIONS.effective_sync_cleanup:
            # Do not synchronize CUDA merely to report minibatch statistics. Detached
            # scalars are reduced after the update's one deliberate CUDA synchronization.
            policy_stat = torch.abs(policy_loss).detach()
            value_stat = value_loss.detach()
            baseline_stat = baseline_loss.detach()
        else:
            policy_stat = torch.abs(policy_loss).item()
            value_stat = value_loss.item()
            baseline_stat = baseline_loss.item()
        update_stats = {
            "Losses/Policy Loss": policy_stat,
            "Losses/Value Loss": value_stat,
            "Losses/Baseline Loss": baseline_stat,
            "Policy/Learning Rate": decay_lr,
            "Policy/Epsilon": decay_eps,
            "Policy/Beta": decay_bet,
        }
        _poca_record_timing("stats", time.perf_counter() - started)
        return update_stats

    def masked_poca_update(self, batch, num_sequences):
        prepare_started = time.perf_counter()
        tensor_cache = getattr(_POCA_UPDATE_CACHE_STATE, "cache", None)
        indices = getattr(_POCA_UPDATE_CACHE_STATE, "indices", None)
        cached = getattr(_POCA_UPDATE_CACHE_STATE, "minibatch", None)

        if cached is None and tensor_cache is not None and indices is not None:
            transfer_started = time.perf_counter()
            cached = _select_poca_update_tensor_cache(
                tensor_cache,
                indices,
            )
            _POCA_UPDATE_CACHE_STATE.minibatch = cached
            _poca_record_timing(
                "cache_transfer",
                time.perf_counter() - transfer_started,
            )

        if cached is not None:
            groupmate_counts = cached["groupmate_counts"]
            communication_activity = _poca_communication_activity(
                groupmate_counts
            )
            action_masks = cached["action_masks"]
            communication_tensor = (
                None
                if communication_activity is None
                else action_masks.new_tensor(communication_activity)
            )
            dimension_mask = _build_bees_policy_dimension_mask(
                self.policy.behavior_spec.action_spec,
                action_masks,
                movement_activity=cached["movement_activity"],
                discrete_actions=cached["discrete_actions"],
                communication_activity=communication_tensor,
            )
            _POLICY_DIMENSION_MASK_STATE.mask = dimension_mask

            import numpy as np

            weights = 1.0 / np.maximum(
                np.asarray(groupmate_counts, dtype=np.float32) + 1.0,
                1.0,
            )
            _POLICY_DIMENSION_MASK_STATE.sample_weights = (
                action_masks.new_tensor(weights)
                * cached["loss_masks"].to(action_masks.dtype)
            )
            slot_limits = cached["slot_limits"]
        else:
            batch_size = len(batch[BufferKey.MASKS])
            groupmate_counts = _poca_groupmate_counts(
                self.policy,
                batch,
                batch_size,
            )
            communication_activity = _poca_communication_activity(
                groupmate_counts,
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
                    groupmate_counts=groupmate_counts,
                )
            )
            slot_limits = _structured_training_slot_limits(
                self.policy,
                batch,
            )

        _POCA_GROUP_BATCH_STATE.valid_rows = (
            _poca_groupmate_valid_row_indices(groupmate_counts)
        )
        _POCA_GROUP_BATCH_STATE.encoded_cache = {}
        _POCA_GROUP_BATCH_STATE.dedup_plan = (
            cached.get("observation_dedup")
            if (
                cached is not None
                and _POCA_OPTIMIZATIONS.stream_shards == 1
                and not _POCA_OPTIMIZATIONS.critic_baseline_overlap
            )
            else None
        )

        from bees_mlagents_structured_policy import (
            reset_training_faction_rows,
            reset_training_slot_limits,
            set_training_faction_rows,
            set_training_slot_limits,
        )

        slot_token = set_training_slot_limits(slot_limits)
        faction_token = set_training_faction_rows(
            None if cached is None else cached["faction_rows"]
        )
        _poca_record_timing(
            "prepare",
            time.perf_counter() - prepare_started,
        )
        optimizer_started = time.perf_counter()
        cuda_timing = _poca_cuda_timing_begin("optimizer_total")

        try:
            return profiled_poca_update(self, batch, num_sequences)
        finally:
            _poca_cuda_timing_end(cuda_timing)
            _poca_record_timing(
                "optimizer_total",
                time.perf_counter() - optimizer_started,
            )
            reset_training_faction_rows(faction_token)
            reset_training_slot_limits(slot_token)
            _POLICY_DIMENSION_MASK_STATE.mask = None
            _POLICY_DIMENSION_MASK_STATE.sample_weights = None
            _POCA_GROUP_BATCH_STATE.valid_rows = None
            _POCA_GROUP_BATCH_STATE.encoded_cache = None
            _POCA_GROUP_BATCH_STATE.dedup_plan = None
            _POCA_UPDATE_CACHE_STATE.minibatch = None

    ActionModel.forward = masked_forward
    ActionModel.evaluate = masked_evaluate
    TorchPPOOptimizer.update = masked_ppo_update
    TorchPOCAOptimizer.update = masked_poca_update
    TorchPOCAOptimizer.get_trajectory_and_baseline_value_estimates = (
        compact_poca_trajectory_values
    )
    POCATrainer._update_policy = weighted_poca_update_policy
    POCATrainer.advance = batched_poca_advance
    POCATrainer.save_model = pipelined_poca_save_model
    MultiAgentNetworkBody.forward = optimized_multi_agent_forward
    ModelUtils.trust_region_policy_loss = staticmethod(masked_policy_loss)
    ModelUtils.masked_mean = staticmethod(weighted_masked_mean)
    BCModule._update_batch = masked_bc_update_batch
    BCModule._behavioral_cloning_loss = masked_bc_loss

    _ORIGINAL_ACTION_MODEL_FORWARD = original_forward
    _ORIGINAL_ACTION_MODEL_EVALUATE = original_evaluate
    _ORIGINAL_PPO_UPDATE = original_ppo_update
    _ORIGINAL_POCA_UPDATE = original_poca_update
    _ORIGINAL_POCA_TRAJECTORY_VALUES = original_poca_trajectory_values
    _ORIGINAL_POCA_UPDATE_POLICY = original_poca_update_policy
    _ORIGINAL_POCA_ADVANCE = original_poca_advance
    _ORIGINAL_POCA_SAVE_MODEL = original_poca_save_model
    _ORIGINAL_MULTI_AGENT_FORWARD = original_multi_agent_forward
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
    global _ORIGINAL_POCA_TRAJECTORY_VALUES
    global _ORIGINAL_POCA_UPDATE_POLICY
    global _ORIGINAL_POCA_ADVANCE
    global _ORIGINAL_POCA_SAVE_MODEL
    global _ORIGINAL_MULTI_AGENT_FORWARD
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
    from mlagents.trainers.torch_entities.networks import MultiAgentNetworkBody
    from mlagents.trainers.torch_entities.utils import ModelUtils

    ActionModel.forward = _ORIGINAL_ACTION_MODEL_FORWARD
    ActionModel.evaluate = _ORIGINAL_ACTION_MODEL_EVALUATE
    TorchPPOOptimizer.update = _ORIGINAL_PPO_UPDATE
    TorchPOCAOptimizer.update = _ORIGINAL_POCA_UPDATE
    TorchPOCAOptimizer.get_trajectory_and_baseline_value_estimates = (
        _ORIGINAL_POCA_TRAJECTORY_VALUES
    )
    POCATrainer._update_policy = _ORIGINAL_POCA_UPDATE_POLICY
    POCATrainer.advance = _ORIGINAL_POCA_ADVANCE
    POCATrainer.save_model = _ORIGINAL_POCA_SAVE_MODEL
    MultiAgentNetworkBody.forward = _ORIGINAL_MULTI_AGENT_FORWARD
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
    _POCA_GROUP_BATCH_STATE.valid_rows = None
    _POCA_GROUP_BATCH_STATE.encoded_cache = None
    _POCA_GROUP_BATCH_STATE.dedup_plan = None
    _POCA_UPDATE_CACHE_STATE.cache = None
    _POCA_UPDATE_CACHE_STATE.indices = None
    _POCA_UPDATE_CACHE_STATE.minibatch = None

    _ORIGINAL_ACTION_MODEL_FORWARD = None
    _ORIGINAL_ACTION_MODEL_EVALUATE = None
    _ORIGINAL_PPO_UPDATE = None
    _ORIGINAL_POCA_UPDATE = None
    _ORIGINAL_POCA_TRAJECTORY_VALUES = None
    _ORIGINAL_POCA_UPDATE_POLICY = None
    _ORIGINAL_POCA_ADVANCE = None
    _ORIGINAL_POCA_SAVE_MODEL = None
    _ORIGINAL_MULTI_AGENT_FORWARD = None
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
