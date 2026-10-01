"""Narrow compatibility fixes and exploration guardrails for Bees PPO/MA-POCA training."""

from __future__ import annotations

import math
import threading
import time
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

_ORIGINAL_GAUSSIAN_FORWARD = None
_ORIGINAL_ACTION_MODEL_FORWARD = None
_ORIGINAL_ACTION_MODEL_EVALUATE = None
_ORIGINAL_PPO_UPDATE = None
_ORIGINAL_POCA_UPDATE = None
_ORIGINAL_POCA_TRAJECTORY_VALUES = None
_ORIGINAL_POCA_UPDATE_POLICY = None
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


def _poca_record_timing(label, seconds):
    totals = getattr(_POCA_TIMING_STATE, "timing_totals", None)
    counts = getattr(_POCA_TIMING_STATE, "timing_counts", None)
    if totals is None or counts is None:
        return
    totals[label] = totals.get(label, 0.0) + float(seconds)
    counts[label] = counts.get(label, 0) + 1


def _poca_average_timing(label):
    totals = getattr(_POCA_TIMING_STATE, "timing_totals", None) or {}
    counts = getattr(_POCA_TIMING_STATE, "timing_counts", None) or {}
    count = counts.get(label, 0)
    return totals.get(label, 0.0) / count if count else 0.0


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

        def encode_members(members, member_valid_rows):
            outputs = [None] * len(members)
            pending = []

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

        if obs:
            obs_attn_mask = self._get_masks_from_nans(obs)
            encoded_obs = encode_members(
                obs,
                valid_rows[: len(obs)],
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
            encoded_obs_only = encode_members(
                obs_only,
                obs_only_valid_rows,
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

    def weighted_poca_update_policy(self):
        """ML-Agents 1.1.0 on-policy update with team-timestep-weighted advantages."""

        import numpy as np
        from collections import defaultdict

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
        max_num_batch = buffer_length // batch_size
        total_minibatches = num_epoch * max_num_batch
        update_started = time.perf_counter()
        _POCA_TIMING_STATE.timing_totals = {}
        _POCA_TIMING_STATE.timing_counts = {}
        print(
            "[Bees PPO timing] update begin "
            f"buffer={buffer_length} batch={batch_size} epochs={num_epoch} "
            f"minibatches={total_minibatches}",
            flush=True,
        )

        completed_minibatches = 0
        for _epoch_index in range(num_epoch):
            self.update_buffer.shuffle(
                sequence_length=self.policy.sequence_length
            )
            buffer = self.update_buffer
            for i in range(0, max_num_batch * batch_size, batch_size):
                completed_minibatches += 1
                minibatch_started = time.perf_counter()
                minibatch = buffer.make_mini_batch(i, i + batch_size)
                update_stats = self.optimizer.update(minibatch, n_sequences)

                reward_started = time.perf_counter()
                reward_stats = self.optimizer.update_reward_signals(minibatch)
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

        update_seconds = time.perf_counter() - update_started
        print(
            "[Bees PPO timing] update end "
            f"minibatches={completed_minibatches}/{total_minibatches} "
            f"seconds={update_seconds:.6f} "
            f"avg_minibatch={_poca_average_timing('minibatch_total'):.6f} "
            f"prepare={_poca_average_timing('prepare'):.6f} "
            f"actor={_poca_average_timing('actor_get_stats'):.6f} "
            f"critic={_poca_average_timing('critic_pass'):.6f} "
            f"baseline={_poca_average_timing('baseline'):.6f} "
            f"backward={_poca_average_timing('backward'):.6f} "
            f"optimizer_step={_poca_average_timing('optimizer_step'):.6f} "
            f"optimizer_total={_poca_average_timing('optimizer_total'):.6f} "
            f"reward={_poca_average_timing('reward_signals'):.6f}",
            flush=True,
        )

        _POCA_TIMING_STATE.timing_totals = None
        _POCA_TIMING_STATE.timing_counts = None

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

    def compact_poca_trajectory_values(
        self,
        batch,
        next_obs,
        next_groupmate_obs,
        done,
        agent_id="",
    ):
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

    def masked_poca_update(self, batch, num_sequences):
        prepare_started = time.perf_counter()
        batch_size = len(batch[BufferKey.MASKS])
        groupmate_counts = _poca_groupmate_counts(
            self.policy,
            batch,
            batch_size,
        )
        communication_activity = _poca_communication_activity(
            groupmate_counts,
        )
        _POCA_GROUP_BATCH_STATE.valid_rows = (
            _poca_groupmate_valid_row_indices(groupmate_counts)
        )
        _POCA_GROUP_BATCH_STATE.encoded_cache = {}
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
        from bees_mlagents_structured_policy import (
            reset_training_slot_limits,
            set_training_slot_limits,
        )
        from mlagents.torch_utils import torch

        slot_limits = _structured_training_slot_limits(
            self.policy,
            batch,
        )
        slot_token = set_training_slot_limits(slot_limits)
        _poca_record_timing(
            "prepare",
            time.perf_counter() - prepare_started,
        )
        optimizer_started = time.perf_counter()

        patched = []

        def patch_phase(target, name, label):
            had_instance_value = hasattr(target, "__dict__") and name in target.__dict__
            previous_instance_value = (
                target.__dict__.get(name) if had_instance_value else None
            )
            original = getattr(target, name)

            def timed(*args, **kwargs):
                started = time.perf_counter()
                try:
                    return original(*args, **kwargs)
                finally:
                    _poca_record_timing(
                        label,
                        time.perf_counter() - started,
                    )

            object.__setattr__(target, name, timed)
            patched.append(
                (target, name, had_instance_value, previous_instance_value)
            )

        original_autograd_backward = torch.autograd.backward

        def timed_autograd_backward(*args, **kwargs):
            started = time.perf_counter()
            try:
                return original_autograd_backward(*args, **kwargs)
            finally:
                _poca_record_timing(
                    "backward",
                    time.perf_counter() - started,
                )

        try:
            patch_phase(self.policy.actor, "get_stats", "actor_get_stats")
            patch_phase(self.critic, "critic_pass", "critic_pass")
            patch_phase(self.critic, "baseline", "baseline")
            patch_phase(self.optimizer, "step", "optimizer_step")
            torch.autograd.backward = timed_autograd_backward
            return original_poca_update(self, batch, num_sequences)
        finally:
            torch.autograd.backward = original_autograd_backward
            for target, name, had_instance_value, previous_instance_value in reversed(patched):
                if had_instance_value:
                    object.__setattr__(target, name, previous_instance_value)
                else:
                    try:
                        object.__delattr__(target, name)
                    except AttributeError:
                        pass
            _poca_record_timing(
                "optimizer_total",
                time.perf_counter() - optimizer_started,
            )
            reset_training_slot_limits(slot_token)
            _POLICY_DIMENSION_MASK_STATE.mask = None
            _POLICY_DIMENSION_MASK_STATE.sample_weights = None
            _POCA_GROUP_BATCH_STATE.valid_rows = None
            _POCA_GROUP_BATCH_STATE.encoded_cache = None

    ActionModel.forward = masked_forward
    ActionModel.evaluate = masked_evaluate
    TorchPPOOptimizer.update = masked_ppo_update
    TorchPOCAOptimizer.update = masked_poca_update
    TorchPOCAOptimizer.get_trajectory_and_baseline_value_estimates = (
        compact_poca_trajectory_values
    )
    POCATrainer._update_policy = weighted_poca_update_policy
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

    _ORIGINAL_ACTION_MODEL_FORWARD = None
    _ORIGINAL_ACTION_MODEL_EVALUATE = None
    _ORIGINAL_PPO_UPDATE = None
    _ORIGINAL_POCA_UPDATE = None
    _ORIGINAL_POCA_TRAJECTORY_VALUES = None
    _ORIGINAL_POCA_UPDATE_POLICY = None
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
