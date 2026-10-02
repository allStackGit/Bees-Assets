"""Narrow compatibility fixes and exploration guardrails for Bees PPO/MA-POCA training."""

from __future__ import annotations

import copy
import math
import queue
import threading
import time
import weakref
from collections import deque
from types import SimpleNamespace
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
POCA_PIPELINE_MAX_READY_BATCHES = 8
POCA_PIPELINE_IDLE_SECONDS = 0.001
POCA_GPU_CACHE_MIN_RESERVE_BYTES = 2 * 1024 * 1024 * 1024
POCA_GPU_CACHE_RESERVE_FRACTION = 0.30

_ORIGINAL_GAUSSIAN_FORWARD = None
_ORIGINAL_ACTION_MODEL_FORWARD = None
_ORIGINAL_ACTION_MODEL_EVALUATE = None
_ORIGINAL_PPO_UPDATE = None
_ORIGINAL_POCA_UPDATE = None
_ORIGINAL_POCA_TRAJECTORY_VALUES = None
_ORIGINAL_POCA_UPDATE_POLICY = None
_ORIGINAL_POCA_ADVANCE = None
_ORIGINAL_GHOST_ADVANCE = None
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
_POCA_PIPELINE_REGISTRY_LOCK = threading.Lock()
_POCA_PIPELINES = {}
_POCA_POLICY_PUBLICATIONS = {}
_POCA_ACTIVE_OVERLAPS = set()


def note_poca_policy_publication(
    behavior_name: str,
    version: int,
    *,
    max_policy_lag: int,
) -> None:
    """Record the broker policy generation used to version threaded trajectories."""

    normalized_name = str(behavior_name or "").strip()
    if not normalized_name:
        return
    normalized_version = int(version)
    normalized_lag = max(0, int(max_policy_lag))
    if normalized_version < 1:
        return
    with _POCA_PIPELINE_REGISTRY_LOCK:
        _POCA_POLICY_PUBLICATIONS[normalized_name] = {
            "version": normalized_version,
            "max_policy_lag": normalized_lag,
        }


def poca_pipeline_overlap_active(behavior_name: Optional[str] = None) -> bool:
    with _POCA_PIPELINE_REGISTRY_LOCK:
        if behavior_name is None:
            return bool(_POCA_ACTIVE_OVERLAPS)
        normalized = str(behavior_name or "").strip()
        return any(
            id(pipeline) in _POCA_ACTIVE_OVERLAPS
            and pipeline.behavior_name == normalized
            for pipeline in _POCA_PIPELINES.values()
        )


def poca_pipeline_has_pending_work() -> bool:
    with _POCA_PIPELINE_REGISTRY_LOCK:
        pipelines = list(_POCA_PIPELINES.values())
    return any(pipeline.has_pending_work() for pipeline in pipelines)


def _poca_behavior_name(trainer) -> str:
    for policy_queue in getattr(trainer, "policy_queues", ()):
        value = str(getattr(policy_queue, "behavior_id", "") or "").strip()
        if value:
            return value
    return ""


def _trajectory_source_policy_version(trajectory) -> Optional[int]:
    source_method = getattr(trajectory, "source_policy_version", None)
    if callable(source_method):
        value = source_method()
        if isinstance(value, int) and not isinstance(value, bool) and value >= 1:
            return int(value)
    versions = getattr(trajectory, "policy_versions", None)
    behavior_id = str(getattr(trajectory, "behavior_id", "") or "")
    if isinstance(versions, dict):
        value = versions.get(behavior_id)
        if isinstance(value, int) and not isinstance(value, bool) and value >= 1:
            return int(value)
    return None


def _trajectory_source_policy_key(trajectory):
    behavior_id = str(getattr(trajectory, "behavior_id", "") or "").strip()
    version = _trajectory_source_policy_version(trajectory)
    if not behavior_id or version is None:
        return None
    return behavior_id, int(version)


def _poca_pipeline_supported(trainer) -> bool:
    from mlagents.trainers.torch_entities.components.reward_providers.extrinsic_reward_provider import (
        ExtrinsicRewardProvider,
    )

    policy = getattr(trainer, "policy", None)
    optimizer = getattr(trainer, "optimizer", None)
    if (
        policy is None
        or optimizer is None
        or policy.use_recurrent
        or len(policy.behavior_spec.observation_specs) != 1
        or tuple(policy.behavior_spec.observation_specs[0].shape)
        != (BEES_OBSERVATION_SIZE,)
        or not _is_bees_action_spec(policy.behavior_spec.action_spec)
    ):
        return False
    return bool(optimizer.reward_signals) and all(
        isinstance(provider, ExtrinsicRewardProvider)
        for provider in optimizer.reward_signals.values()
    )


class _PocaCriticSnapshot:
    __slots__ = ("behavior_id", "version", "policy", "critic")

    def __init__(
        self,
        behavior_id: str,
        version: int,
        policy,
        critic,
    ) -> None:
        self.behavior_id = str(behavior_id)
        self.version = int(version)
        self.policy = policy
        self.critic = critic


class _PocaTrajectoryPipeline:
    """Prepare policy-versioned trajectory values while PPO owns the live model."""

    def __init__(self, trainer, behavior_name: str) -> None:
        self.behavior_name = str(behavior_name)
        self._trainer_ref = weakref.ref(trainer)
        self._ready = queue.Queue(maxsize=POCA_PIPELINE_MAX_READY_BATCHES)
        self._held = deque()
        self._overlap = threading.Event()
        self._stop = threading.Event()
        self._state_lock = threading.Lock()
        self._snapshots = {}
        self._source_behavior_id = None
        self._inflight = False
        self._prepared_batches = 0
        self._prepared_steps = 0
        self._fallback_batches = 0
        self._fallback_steps = 0
        self._evaluation_seconds = 0.0
        self._evaluation_errors = 0
        self._thread = threading.Thread(
            target=self._run,
            name=f"bees-poca-preprocessor-{self.behavior_name}",
            daemon=True,
        )
        self._thread.start()

    def stop(self) -> None:
        self._stop.set()
        self._overlap.set()
        self._thread.join(timeout=2.0)

    def has_pending_work(self) -> bool:
        with self._state_lock:
            return (
                self._inflight
                or bool(self._held)
                or not self._ready.empty()
            )

    def overlap_drain_active(self) -> bool:
        return self._overlap.is_set()

    def stats_snapshot(self):
        with self._state_lock:
            return {
                "prepared_batches": int(self._prepared_batches),
                "prepared_steps": int(self._prepared_steps),
                "fallback_batches": int(self._fallback_batches),
                "fallback_steps": int(self._fallback_steps),
                "evaluation_seconds": float(self._evaluation_seconds),
                "evaluation_errors": int(self._evaluation_errors),
                "ready_batches": int(self._ready.qsize()),
            }

    def observe_trajectory(self, trajectory) -> None:
        behavior_id = str(
            getattr(trajectory, "behavior_id", "") or ""
        ).strip()
        if not behavior_id:
            return
        from mlagents.trainers.behavior_id_utils import BehaviorIdentifiers

        parsed = BehaviorIdentifiers.from_name_behavior_id(behavior_id)
        if parsed.brain_name != self.behavior_name:
            return
        with self._state_lock:
            self._source_behavior_id = behavior_id

    def _publication(self):
        with self._state_lock:
            behavior_id = self._source_behavior_id
        if not behavior_id:
            return None, None
        with _POCA_PIPELINE_REGISTRY_LOCK:
            value = _POCA_POLICY_PUBLICATIONS.get(behavior_id)
            return behavior_id, None if value is None else dict(value)

    def _snapshot_for(self, policy_key):
        if policy_key is None:
            return None
        behavior_id, version = policy_key
        with self._state_lock:
            return self._snapshots.get(
                (str(behavior_id), int(version))
            )

    def _capture_current_snapshot(self):
        behavior_id, publication = self._publication()
        trainer = self._trainer_ref()
        if (
            behavior_id is None
            or publication is None
            or trainer is None
        ):
            return None, 0.0
        version = int(publication["version"])
        snapshot_key = (behavior_id, version)
        with self._state_lock:
            existing = self._snapshots.get(snapshot_key)
        if existing is not None:
            return existing, 0.0

        from mlagents.torch_utils import torch

        started = time.perf_counter()
        critic = copy.deepcopy(trainer.optimizer.critic)
        critic.to(device=torch.device("cpu"))
        critic.eval()
        for parameter in critic.parameters():
            parameter.requires_grad_(False)
        policy_view = SimpleNamespace(
            use_recurrent=False,
            behavior_spec=copy.deepcopy(trainer.policy.behavior_spec),
        )
        snapshot = _PocaCriticSnapshot(
            behavior_id,
            version,
            policy_view,
            critic,
        )
        elapsed = time.perf_counter() - started

        minimum_version = max(
            1,
            version - int(publication["max_policy_lag"]),
        )
        with self._state_lock:
            self._snapshots[snapshot_key] = snapshot
            for old_key in tuple(self._snapshots):
                old_behavior_id, old_version = old_key
                if (
                    old_behavior_id == behavior_id
                    and old_version < minimum_version
                ):
                    self._snapshots.pop(old_key, None)
        return snapshot, elapsed

    def begin_overlap(self):
        try:
            snapshot, copy_seconds = self._capture_current_snapshot()
        except Exception as exc:
            print(
                "[Bees trajectory pipeline] snapshot capture failed; "
                f"falling back to synchronous trajectory processing: "
                f"{type(exc).__name__}: {exc}",
                flush=True,
            )
            return False, 0.0, None
        if snapshot is None:
            return False, 0.0, None
        with _POCA_PIPELINE_REGISTRY_LOCK:
            _POCA_ACTIVE_OVERLAPS.add(id(self))
        self._overlap.set()
        return True, float(copy_seconds), int(snapshot.version)

    def end_external_overlap(self) -> None:
        """Stop WAN/self-play intake but keep draining already-forwarded internal work."""

        with _POCA_PIPELINE_REGISTRY_LOCK:
            _POCA_ACTIVE_OVERLAPS.discard(id(self))

    def finish_overlap(self) -> None:
        self._overlap.clear()
        with _POCA_PIPELINE_REGISTRY_LOCK:
            _POCA_ACTIVE_OVERLAPS.discard(id(self))

    def drain_ready(self):
        results = []
        while True:
            try:
                results.append(self._ready.get_nowait())
            except queue.Empty:
                break
        return results

    def _next_trajectory(self, *, allow_queue: bool):
        with self._state_lock:
            if self._held:
                return self._held.popleft()
        if not allow_queue:
            return None
        trainer = self._trainer_ref()
        if trainer is None:
            return None
        from mlagents.trainers.agent_processor import AgentManagerQueue

        for trajectory_queue in trainer.trajectory_queues:
            try:
                return trajectory_queue.get_nowait()
            except AgentManagerQueue.Empty:
                continue
        return None

    def _hold_front(self, trajectory) -> None:
        with self._state_lock:
            self._held.appendleft(trajectory)

    def _gather_batch(self):
        allow_queue = self._overlap.is_set()
        first = self._next_trajectory(allow_queue=allow_queue)
        if first is None:
            return None

        policy_key = _trajectory_source_policy_key(first)
        trajectories = [first]
        experiences = len(first.steps)
        if policy_key is None:
            return None, trajectories, experiences

        while experiences < POCA_TRAJECTORY_BATCH_MAX_EXPERIENCES:
            candidate = self._next_trajectory(
                allow_queue=self._overlap.is_set()
            )
            if candidate is None:
                break
            candidate_key = _trajectory_source_policy_key(candidate)
            candidate_experiences = len(candidate.steps)
            if (
                candidate_key != policy_key
                or (
                    trajectories
                    and experiences + candidate_experiences
                    > POCA_TRAJECTORY_BATCH_MAX_EXPERIENCES
                )
            ):
                self._hold_front(candidate)
                break
            trajectories.append(candidate)
            experiences += candidate_experiences
        return policy_key, trajectories, experiences

    def _put_result(self, result) -> bool:
        while not self._stop.is_set():
            try:
                self._ready.put(result, timeout=0.05)
                return True
            except queue.Full:
                continue
        return False

    def _run(self) -> None:
        while not self._stop.is_set():
            with self._state_lock:
                has_held = bool(self._held)
            if not self._overlap.is_set() and not has_held:
                self._overlap.wait(timeout=0.05)
                continue

            # Mark the whole dequeue/gather/evaluate interval as in-flight so
            # the trainer thread cannot observe a false idle gap after the
            # worker has removed a trajectory but before evaluation starts.
            with self._state_lock:
                self._inflight = True
            try:
                gathered = self._gather_batch()
                if gathered is None:
                    time.sleep(POCA_PIPELINE_IDLE_SECONDS)
                    continue
                policy_key, trajectories, experiences = gathered

                snapshot = self._snapshot_for(policy_key)
                if snapshot is None:
                    result = {
                        "prepared": False,
                        "policy_key": policy_key,
                        "trajectories": trajectories,
                    }
                    with self._state_lock:
                        self._fallback_batches += 1
                        self._fallback_steps += int(experiences)
                else:
                    started = time.perf_counter()
                    try:
                        prepared = _prepare_poca_trajectory_batch_snapshot(
                            snapshot,
                            trajectories,
                        )
                    except Exception as exc:
                        elapsed = time.perf_counter() - started
                        print(
                            "[Bees trajectory pipeline] snapshot evaluation failed; "
                            f"falling back to synchronous processing for "
                            f"{experiences} steps: {type(exc).__name__}: {exc}",
                            flush=True,
                        )
                        result = {
                            "prepared": False,
                            "policy_key": policy_key,
                            "trajectories": trajectories,
                        }
                        with self._state_lock:
                            self._fallback_batches += 1
                            self._fallback_steps += int(experiences)
                            self._evaluation_seconds += float(elapsed)
                            self._evaluation_errors += 1
                    else:
                        elapsed = time.perf_counter() - started
                        result = {
                            "prepared": True,
                            "policy_key": policy_key,
                            **prepared,
                        }
                        with self._state_lock:
                            self._prepared_batches += 1
                            self._prepared_steps += int(experiences)
                            self._evaluation_seconds += float(elapsed)
                if not self._put_result(result):
                    return
            finally:
                with self._state_lock:
                    self._inflight = False


def _get_or_create_poca_pipeline(trainer):
    behavior_name = _poca_behavior_name(trainer)
    if not behavior_name or not _poca_pipeline_supported(trainer):
        return None
    with _POCA_PIPELINE_REGISTRY_LOCK:
        key = id(trainer)
        existing = _POCA_PIPELINES.get(key)
        if existing is not None:
            return existing
        pipeline = _PocaTrajectoryPipeline(trainer, behavior_name)
        _POCA_PIPELINES[key] = pipeline
        return pipeline


def _shutdown_all_poca_pipelines() -> None:
    with _POCA_PIPELINE_REGISTRY_LOCK:
        pipelines = list(_POCA_PIPELINES.values())
        _POCA_PIPELINES.clear()
        _POCA_ACTIVE_OVERLAPS.clear()
        _POCA_POLICY_PUBLICATIONS.clear()
    for pipeline in pipelines:
        pipeline.stop()


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


def _evaluate_poca_trajectory_batch(trainer, buffers, trajectories):
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

    merged = _merge_agent_buffers(buffers)
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
            groupmate_obs = GroupObsUtil.from_buffer(merged, n_obs)
            groupmate_obs = [
                [
                    ModelUtils.list_to_tensor(obs)
                    for obs in groupmate
                ]
                for groupmate in groupmate_obs
            ]
            groupmate_actions = AgentAction.group_from_buffer(merged)
            current_counts = _poca_groupmate_counts(
                trainer.policy,
                merged,
                merged.num_experiences,
            )
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
            next_groupmate_obs = GroupObsUtil.from_buffer(next_buffer, n_obs)
            next_groupmate_obs = [
                [
                    ModelUtils.list_to_tensor(obs)
                    for obs in groupmate
                ]
                for groupmate in next_groupmate_obs
            ]
            next_counts = _poca_groupmate_counts(
                trainer.policy,
                next_buffer,
                next_buffer.num_experiences,
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


def _process_poca_trajectory_batch(trainer, trajectories):
    """Process multiple short feed-forward trajectories with shared critic passes."""

    buffers = [trajectory.to_agentbuffer() for trajectory in trajectories]
    merged = _merge_agent_buffers(buffers)
    if trainer.is_training:
        trainer.policy.actor.update_normalization(merged)
        trainer.optimizer.critic.update_normalization(merged)

    values, baselines, next_values = _evaluate_poca_trajectory_batch(
        trainer,
        buffers,
        trajectories,
    )
    _apply_prepared_poca_trajectory_batch(
        trainer,
        {
            "trajectories": trajectories,
            "buffers": buffers,
            "values": values,
            "baselines": baselines,
            "next_values": next_values,
        },
        update_normalization=False,
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


def _poca_cpu_buffer_tensor(values, dtype):
    raw = values.to_ndarray() if hasattr(values, "to_ndarray") else values
    return _poca_cpu_tensor(raw, dtype)


def _poca_cpu_group_actions(buffer):
    import numpy as np
    from mlagents.torch_utils import torch
    from mlagents.trainers.buffer import BufferKey
    from mlagents.trainers.torch_entities.agent_action import AgentAction

    continuous = []
    if BufferKey.GROUP_CONTINUOUS_ACTION in buffer:
        continuous = list(
            buffer[BufferKey.GROUP_CONTINUOUS_ACTION].padded_to_batch()
        )
    discrete = []
    if BufferKey.GROUP_DISCRETE_ACTION in buffer:
        discrete = list(
            buffer[BufferKey.GROUP_DISCRETE_ACTION].padded_to_batch(
                dtype=np.int64
            )
        )

    actions = []
    for position in range(max(len(continuous), len(discrete))):
        continuous_tensor = (
            _poca_cpu_tensor(continuous[position], torch.float32)
            if position < len(continuous)
            else None
        )
        discrete_tensor = (
            _poca_cpu_tensor(discrete[position], torch.long)
            if position < len(discrete)
            else None
        )
        actions.append(
            AgentAction(
                continuous_tensor,
                None
                if discrete_tensor is None
                else [
                    discrete_tensor[..., index]
                    for index in range(discrete_tensor.shape[-1])
                ],
            )
        )
    return actions


def _prepare_poca_trajectory_batch_snapshot(snapshot, trajectories):
    """Evaluate one contiguous policy-version batch on a frozen CPU critic."""

    import numpy as np
    from mlagents.torch_utils import torch
    from mlagents.trainers.torch_entities.utils import ModelUtils
    from mlagents.trainers.trajectory import GroupObsUtil, ObsUtil
    from bees_mlagents_structured_policy import (
        reset_training_slot_limits,
        set_training_slot_limits,
    )

    buffers = [trajectory.to_agentbuffer() for trajectory in trajectories]
    merged = _merge_agent_buffers(buffers)
    # Normalization is part of the source-policy snapshot. Keep it immutable
    # while evaluating delayed trajectories; the live actor/critic normalizers
    # catch up on the trainer thread when these prepared trajectories commit.
    policy = snapshot.policy
    n_obs = len(policy.behavior_spec.observation_specs)
    next_buffer = _build_poca_next_observation_buffer(
        trajectories,
        n_obs,
    )
    slot_limits = _structured_training_slot_limits(
        policy,
        merged,
        extra_observations=[
            (trajectory.next_obs, trajectory.next_group_obs)
            for trajectory in trajectories
        ],
    )
    slot_token = set_training_slot_limits(slot_limits)
    try:
        with torch.inference_mode():
            current_obs = [
                _poca_cpu_buffer_tensor(obs, torch.float32)
                for obs in ObsUtil.from_buffer(merged, n_obs)
            ]
            groupmate_obs = GroupObsUtil.from_buffer(merged, n_obs)
            groupmate_obs = [
                [
                    _poca_cpu_tensor(obs, torch.float32)
                    for obs in groupmate
                ]
                for groupmate in groupmate_obs
            ]
            current_counts = _poca_groupmate_counts(
                policy,
                merged,
                merged.num_experiences,
            )
            _POCA_GROUP_BATCH_STATE.valid_rows = (
                _poca_groupmate_valid_row_indices(current_counts)
            )
            _POCA_GROUP_BATCH_STATE.encoded_cache = {}

            value_estimates, _ = snapshot.critic.critic_pass(
                [current_obs] + groupmate_obs,
                memories=None,
                sequence_length=merged.num_experiences,
            )
            baseline_estimates, _ = snapshot.critic.baseline(
                current_obs,
                (
                    groupmate_obs,
                    _poca_cpu_group_actions(merged),
                ),
                memories=None,
                sequence_length=merged.num_experiences,
            )

            next_obs = [
                _poca_cpu_buffer_tensor(obs, torch.float32)
                for obs in ObsUtil.from_buffer(next_buffer, n_obs)
            ]
            next_groupmate_obs = GroupObsUtil.from_buffer(
                next_buffer,
                n_obs,
            )
            next_groupmate_obs = [
                [
                    _poca_cpu_tensor(obs, torch.float32)
                    for obs in groupmate
                ]
                for groupmate in next_groupmate_obs
            ]
            next_counts = _poca_groupmate_counts(
                policy,
                next_buffer,
                next_buffer.num_experiences,
            )
            _POCA_GROUP_BATCH_STATE.valid_rows = (
                _poca_groupmate_valid_row_indices(next_counts)
            )
            _POCA_GROUP_BATCH_STATE.encoded_cache = {}
            next_value_estimates, _ = snapshot.critic.critic_pass(
                [next_obs] + next_groupmate_obs,
                memories=None,
                sequence_length=next_buffer.num_experiences,
            )
    finally:
        reset_training_slot_limits(slot_token)
        _POCA_GROUP_BATCH_STATE.valid_rows = None
        _POCA_GROUP_BATCH_STATE.encoded_cache = None

    return {
        "trajectories": trajectories,
        "buffers": buffers,
        "values": {
            name: ModelUtils.to_numpy(value)
            for name, value in value_estimates.items()
        },
        "baselines": {
            name: ModelUtils.to_numpy(value)
            for name, value in baseline_estimates.items()
        },
        "next_values": {
            name: ModelUtils.to_numpy(value)
            for name, value in next_value_estimates.items()
        },
    }


def _apply_prepared_poca_trajectory_batch(
    trainer,
    prepared,
    *,
    update_normalization: bool,
):
    """Commit pre-evaluated values while keeping all mutable trainer state on its owner thread."""

    import numpy as np
    from mlagents.trainers.trainer.rl_trainer import RLTrainer

    trajectories = prepared["trajectories"]
    buffers = prepared["buffers"]
    values = prepared["values"]
    baselines = prepared["baselines"]
    next_values = prepared["next_values"]

    offset = 0
    for trajectory_index, (trajectory, buffer) in enumerate(
        zip(trajectories, buffers)
    ):
        # Match stock POCA ordering: summary/checkpoint/step bookkeeping
        # happens before this trajectory mutates running normalization state.
        RLTrainer._process_trajectory(trainer, trajectory)
        if update_normalization and trainer.is_training:
            trainer.policy.actor.update_normalization(buffer)
            trainer.optimizer.critic.update_normalization(buffer)

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


def _build_poca_update_tensor_cache(optimizer, buffer):
    """Pre-pad and tensorize the feed-forward extrinsic POCA update buffer once."""

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
    padded_group_obs = GroupObsUtil.from_buffer(
        buffer,
        len(policy.behavior_spec.observation_specs),
    )
    groupmate_obs = [
        [
            _poca_cpu_tensor(
                np.asarray(obs, dtype=np.float32),
                torch.float32,
            )
            for obs in groupmate
        ]
        for groupmate in padded_group_obs
    ]

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
    """Keep the complete PPO tensor cache on GPU when VRAM can safely hold it.

    This eliminates the repeated CPU->GPU copy for every minibatch/epoch. Exeter's
    GTX 1660 has limited VRAM, so the promotion is conditional and reserves both a
    fixed 2 GiB and 30% of total VRAM (whichever is larger) for the model, gradients,
    activations, CUDA workspace, and allocator fragmentation.
    """

    from mlagents.torch_utils import default_device, torch

    if cache is None:
        return cache, {
            "storage": "off",
            "bytes": 0,
            "copy_seconds": 0.0,
            "free_before": 0,
            "reserve": 0,
        }

    cache_bytes = _poca_tensor_cache_nbytes(cache)
    device = default_device()
    result = {
        "storage": "cpu",
        "bytes": int(cache_bytes),
        "copy_seconds": 0.0,
        "free_before": 0,
        "reserve": 0,
    }
    if getattr(device, "type", str(device)) != "cuda" or not torch.cuda.is_available():
        return cache, result

    free_bytes, total_bytes = torch.cuda.mem_get_info(device)
    reserve_bytes = max(
        POCA_GPU_CACHE_MIN_RESERVE_BYTES,
        int(total_bytes * POCA_GPU_CACHE_RESERVE_FRACTION),
    )
    result["free_before"] = int(free_bytes)
    result["reserve"] = int(reserve_bytes)
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
    result["storage"] = "cuda"
    result["copy_seconds"] = time.perf_counter() - started
    return promoted, result


def _select_poca_update_tensor_cache(cache, indices):
    """Gather one shuffled PPO minibatch from the materialized cache."""

    import numpy as np
    from mlagents.torch_utils import default_device, torch
    from mlagents.trainers.torch_entities.agent_action import AgentAction

    indices = np.asarray(indices, dtype=np.int64)
    device = default_device()
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
        return selected.to(device=device)

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
    groupmate_obs = [
        [take(tensor) for tensor in groupmate]
        for groupmate in cache["groupmate_obs"][:max_groupmates]
    ]
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


def install_inactive_continuous_action_masking() -> Optional[Callable]:
    """Mask nonexistent weapon actions and normalize MA-POCA fleet-size gradients."""

    from mlagents.trainers.buffer import BufferKey
    from mlagents.trainers.ghost.trainer import GhostTrainer
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
    global _ORIGINAL_GHOST_ADVANCE
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
    original_poca_process_trajectory = POCATrainer._process_trajectory
    original_ghost_advance = GhostTrainer.advance
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

    def pipelined_ghost_advance(self):
        """Forward self-play trajectories while the wrapped PPO update is running.

        GhostTrainer normally forwards outer team-qualified trajectories into the
        wrapped trainer only before calling wrapped_trainer.advance(). During that
        call the outer queues can fill while PPO blocks. This wrapper drains them
        only while the POCA snapshot overlap is active. Learning-team trajectories
        are forwarded into the existing internal queue; ELO and ghost-step state
        remain owned by the GhostTrainer thread and are applied after the wrapped
        advance returns.
        """

        wrapped_trainer = self.trainer
        wrapped_advance = wrapped_trainer.advance
        forward_stop = threading.Event()
        forwarded_learning = []
        forwarded_ghost_steps = [0]
        forward_errors = []
        internal_queue_limit = 256

        def forward_during_overlap():
            from mlagents.trainers.agent_processor import AgentManagerQueue

            try:
                while not forward_stop.is_set():
                    if not poca_pipeline_overlap_active(self.brain_name):
                        forward_stop.wait(POCA_PIPELINE_IDLE_SECONDS)
                        continue

                    progressed = False
                    learning_team = self._learning_team
                    for trajectory_queue in self.trajectory_queues:
                        parsed_behavior_id = self._name_to_parsed_behavior_id[
                            trajectory_queue.behavior_id
                        ]
                        is_learning = (
                            parsed_behavior_id.team_id == learning_team
                        )
                        internal_queue = None
                        if is_learning:
                            internal_queue = self._internal_trajectory_queues[
                                parsed_behavior_id.brain_name
                            ]
                            if (
                                internal_queue.qsize()
                                >= internal_queue_limit
                            ):
                                continue

                        queue_size = trajectory_queue.qsize()
                        for _ in range(queue_size):
                            if (
                                forward_stop.is_set()
                                or not poca_pipeline_overlap_active(
                                    self.brain_name
                                )
                            ):
                                break
                            if (
                                is_learning
                                and internal_queue.qsize()
                                >= internal_queue_limit
                            ):
                                break
                            try:
                                trajectory = trajectory_queue.get_nowait()
                            except AgentManagerQueue.Empty:
                                break
                            progressed = True
                            if is_learning:
                                internal_queue.put(trajectory)
                                forwarded_learning.append(trajectory)
                            else:
                                forwarded_ghost_steps[0] += len(
                                    trajectory.steps
                                )

                    if not progressed:
                        forward_stop.wait(POCA_PIPELINE_IDLE_SECONDS)
            except Exception as exc:
                forward_errors.append(exc)
                forward_stop.set()

        def overlapped_wrapped_advance():
            forward_thread = threading.Thread(
                target=forward_during_overlap,
                name="bees-ghost-poca-forwarder",
                daemon=True,
            )
            forward_thread.start()
            wrapped_error = None
            try:
                return wrapped_advance()
            except BaseException as exc:
                wrapped_error = exc
                raise
            finally:
                forward_stop.set()
                forward_thread.join(timeout=2.0)
                if wrapped_error is None:
                    if forward_thread.is_alive():
                        raise RuntimeError(
                            "self-play trajectory forwarder did not stop"
                        )
                    if forward_errors:
                        raise RuntimeError(
                            "self-play trajectory forwarder failed"
                        ) from forward_errors[0]

                    # Preserve GhostTrainer ownership/order for ELO and ghost-step
                    # bookkeeping. These run before its team-change/save/swap logic.
                    for trajectory in forwarded_learning:
                        self._process_trajectory(trajectory)
                    self.ghost_step += int(forwarded_ghost_steps[0])

        wrapped_trainer.advance = overlapped_wrapped_advance
        try:
            return original_ghost_advance(self)
        finally:
            wrapped_trainer.advance = wrapped_advance

    def batched_poca_advance(self):
        """Process ordinary trajectories, run PPO once, then commit admitted overlap work."""

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
        from mlagents.trainers.agent_processor import AgentManagerQueue

        pipeline = _get_or_create_poca_pipeline(self)
        pending = []
        pending_experiences = 0
        queried = False

        def observe_trajectory(trajectory):
            if pipeline is not None:
                pipeline.observe_trajectory(trajectory)

        def flush_pending():
            nonlocal pending
            nonlocal pending_experiences
            if not pending:
                return
            if len(pending) == 1:
                original_poca_process_trajectory(self, pending[0])
            else:
                _process_poca_trajectory_batch(self, pending)
            pending = []
            pending_experiences = 0

        def next_bookkeeping_boundary():
            next_summary = self._next_summary_step
            if next_summary == 0:
                next_summary = self._get_next_interval_step(
                    self.summary_freq
                )
            next_save = self._next_save_step
            if next_save == 0:
                next_save = self._get_next_interval_step(
                    self.trainer_settings.checkpoint_interval
                )
            candidates = [
                step
                for step in (next_summary, next_save)
                if step > self.get_step
            ]
            return min(candidates) if candidates else None

        def apply_prepared_results():
            nonlocal queried
            if pipeline is None:
                return False
            applied = False
            for prepared in pipeline.drain_ready():
                applied = True
                queried = True
                for trajectory in prepared["trajectories"]:
                    observe_trajectory(trajectory)
                if prepared.get("prepared"):
                    _apply_prepared_poca_trajectory_batch(
                        self,
                        prepared,
                        update_normalization=True,
                    )
                else:
                    # Missing/failed snapshots are a performance fallback only.
                    # The stock path preserves correctness and lifecycle behavior.
                    for trajectory in prepared["trajectories"]:
                        original_poca_process_trajectory(
                            self,
                            trajectory,
                        )
            return applied

        def drain_synchronous_queues():
            nonlocal queried
            nonlocal pending_experiences

            for trajectory_queue in self.trajectory_queues:
                queue_size = trajectory_queue.qsize()
                for _ in range(queue_size):
                    try:
                        trajectory = trajectory_queue.get_nowait()
                    except AgentManagerQueue.Empty:
                        break
                    queried = True
                    observe_trajectory(trajectory)
                    trajectory_experiences = len(trajectory.steps)

                    boundary = next_bookkeeping_boundary()
                    if (
                        pending
                        and boundary is not None
                        and self.get_step
                        + pending_experiences
                        + trajectory_experiences
                        >= boundary
                    ):
                        flush_pending()
                        boundary = next_bookkeeping_boundary()

                    if (
                        not pending
                        and boundary is not None
                        and self.get_step != 0
                        and self.get_step + trajectory_experiences >= boundary
                    ):
                        original_poca_process_trajectory(
                            self,
                            trajectory,
                        )
                        continue

                    if (
                        pending
                        and pending_experiences + trajectory_experiences
                        > POCA_TRAJECTORY_BATCH_MAX_EXPERIENCES
                    ):
                        flush_pending()

                    if not trajectory.steps:
                        flush_pending()
                        original_poca_process_trajectory(
                            self,
                            trajectory,
                        )
                        continue

                    pending.append(trajectory)
                    pending_experiences += trajectory_experiences

            flush_pending()

        with hierarchical_timer("process_trajectory"):
            apply_prepared_results()
            # Do not race the snapshot worker for AgentManagerQueue ownership.
            if pipeline is None or not pipeline.has_pending_work():
                drain_synchronous_queues()
            if self.threaded and not queried:
                time.sleep(0.0001)

        did_update = False
        if self.should_still_train and self._is_ready_update():
            with hierarchical_timer("_update_policy"):
                if self._update_policy():
                    did_update = True
                    for policy_queue in self.policy_queues:
                        policy_queue.put(
                            self.get_policy(policy_queue.behavior_id)
                        )

        # weighted_poca_update_policy() stops new WAN/self-play intake before
        # returning but deliberately leaves the snapshot worker draining the
        # internal queue. Finish that finite set now, before GhostTrainer can
        # change learning teams. This prepares the next update buffer without
        # launching a second PPO update in this advance() call.
        if (
            did_update
            and pipeline is not None
            and pipeline.overlap_drain_active()
        ):
            with hierarchical_timer("process_trajectory"):
                while True:
                    applied = apply_prepared_results()
                    queued = sum(
                        trajectory_queue.qsize()
                        for trajectory_queue in self.trajectory_queues
                    )
                    if queued == 0 and not pipeline.has_pending_work():
                        break
                    if not applied:
                        time.sleep(POCA_PIPELINE_IDLE_SECONDS)

                pipeline.finish_overlap()
                apply_prepared_results()

                # A trajectory can be forwarded immediately before the global
                # overlap gate closes. If it arrived after the worker observed
                # an empty queue, process that final residue synchronously.
                drain_synchronous_queues()

    def weighted_poca_update_policy(self):
        """ML-Agents 1.1.0 update while frozen critics prepare the next trajectories."""

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

        update_started = time.perf_counter()
        pipeline = _get_or_create_poca_pipeline(self)
        pipeline_before = (
            pipeline.stats_snapshot() if pipeline is not None else None
        )
        pipeline_overlap = False
        pipeline_snapshot_seconds = 0.0
        pipeline_snapshot_version = None
        if pipeline is not None:
            (
                pipeline_overlap,
                pipeline_snapshot_seconds,
                pipeline_snapshot_version,
            ) = pipeline.begin_overlap()

        _POCA_TIMING_STATE.timing_totals = {}
        _POCA_TIMING_STATE.timing_counts = {}
        tensor_cache = None
        completed_minibatches = 0
        total_minibatches = 0
        batch_update_stats = defaultdict(list)

        try:
            normalization_started = time.perf_counter()
            _normalize_poca_advantages(
                self.policy,
                self.update_buffer,
            )
            _poca_record_timing(
                "advantage_normalization",
                time.perf_counter() - normalization_started,
            )

            materialize_started = time.perf_counter()
            tensor_cache = _build_poca_update_tensor_cache(
                self.optimizer,
                self.update_buffer,
            )
            materialize_seconds = (
                time.perf_counter() - materialize_started
            )
            _poca_record_timing("materialize", materialize_seconds)
            tensor_cache, device_cache = _promote_poca_update_tensor_cache(
                tensor_cache
            )
            _poca_record_timing(
                "device_cache_copy",
                float(device_cache["copy_seconds"]),
            )

            num_epoch = self.hyperparameters.num_epoch
            max_num_batch = buffer_length // batch_size
            total_minibatches = num_epoch * max_num_batch
            print(
                "[Bees PPO timing] update begin "
                f"buffer={buffer_length} batch={batch_size} "
                f"epochs={num_epoch} minibatches={total_minibatches} "
                f"tensor_cache={'on' if tensor_cache is not None else 'off'} "
                f"cache_storage={device_cache['storage']} "
                f"cache_mib={device_cache['bytes'] / (1024 * 1024):.1f} "
                f"cache_free_mib={device_cache['free_before'] / (1024 * 1024):.1f} "
                f"cache_reserve_mib={device_cache['reserve'] / (1024 * 1024):.1f} "
                f"cache_copy={device_cache['copy_seconds']:.6f} "
                f"materialize={materialize_seconds:.6f} "
                f"trajectory_pipeline={'on' if pipeline_overlap else 'off'} "
                f"snapshot_version={pipeline_snapshot_version if pipeline_snapshot_version is not None else '-'} "
                f"snapshot_copy={pipeline_snapshot_seconds:.6f}",
                flush=True,
            )

            _POCA_UPDATE_CACHE_STATE.cache = tensor_cache
            try:
                for _epoch_index in range(num_epoch):
                    epoch_order = None
                    if tensor_cache is None:
                        self.update_buffer.shuffle(
                            sequence_length=self.policy.sequence_length
                        )
                    else:
                        epoch_order = np.arange(
                            buffer_length,
                            dtype=np.int64,
                        )
                        np.random.shuffle(epoch_order)

                    for i in range(
                        0,
                        max_num_batch * batch_size,
                        batch_size,
                    ):
                        completed_minibatches += 1
                        minibatch_started = time.perf_counter()
                        if tensor_cache is None:
                            minibatch = (
                                self.update_buffer.make_mini_batch(
                                    i,
                                    i + batch_size,
                                )
                            )
                            _POCA_UPDATE_CACHE_STATE.indices = None
                        else:
                            _POCA_UPDATE_CACHE_STATE.indices = epoch_order[
                                i : i + batch_size
                            ]
                            minibatch = self.update_buffer

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
                            reward_stats = (
                                self.optimizer.update_reward_signals(
                                    minibatch
                                )
                            )
                        else:
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
                _POCA_UPDATE_CACHE_STATE.cache = None
                _POCA_UPDATE_CACHE_STATE.indices = None
                _POCA_UPDATE_CACHE_STATE.minibatch = None
        finally:
            if pipeline is not None and pipeline_overlap:
                pipeline.end_external_overlap()

        update_seconds = time.perf_counter() - update_started
        pipeline_after = (
            pipeline.stats_snapshot() if pipeline is not None else None
        )
        overlap_batches = 0
        overlap_steps = 0
        overlap_fallback_steps = 0
        overlap_eval_seconds = 0.0
        overlap_ready = 0
        if pipeline_before is not None and pipeline_after is not None:
            overlap_batches = max(
                0,
                pipeline_after["prepared_batches"]
                - pipeline_before["prepared_batches"],
            )
            overlap_steps = max(
                0,
                pipeline_after["prepared_steps"]
                - pipeline_before["prepared_steps"],
            )
            overlap_fallback_steps = max(
                0,
                pipeline_after["fallback_steps"]
                - pipeline_before["fallback_steps"],
            )
            overlap_eval_seconds = max(
                0.0,
                pipeline_after["evaluation_seconds"]
                - pipeline_before["evaluation_seconds"],
            )
            overlap_ready = int(pipeline_after["ready_batches"])

        print(
            "[Bees PPO timing] update end "
            f"minibatches={completed_minibatches}/{total_minibatches} "
            f"seconds={update_seconds:.6f} "
            f"normalize={_poca_average_timing('advantage_normalization'):.6f} "
            f"materialize={_poca_average_timing('materialize'):.6f} "
            f"cache_copy={_poca_average_timing('device_cache_copy'):.6f} "
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
            f"old_probs_masks={_poca_average_timing('old_probs_masks'):.6f} "
            f"losses={_poca_average_timing('losses'):.6f} "
            f"learning_rate={_poca_average_timing('learning_rate'):.6f} "
            f"zero_grad={_poca_average_timing('zero_grad'):.6f} "
            f"backward={_poca_average_timing('backward'):.6f} "
            f"optimizer_step={_poca_average_timing('optimizer_step'):.6f} "
            f"stats={_poca_average_timing('stats'):.6f} "
            f"optimizer_total={_poca_average_timing('optimizer_total'):.6f} "
            f"reward={_poca_average_timing('reward_signals'):.6f} "
            f"overlap_batches={overlap_batches} "
            f"overlap_steps={overlap_steps} "
            f"overlap_fallback_steps={overlap_fallback_steps} "
            f"overlap_eval={overlap_eval_seconds:.6f} "
            f"overlap_ready={overlap_ready}",
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

        started = time.perf_counter()
        run_out = self.policy.actor.get_stats(
            current_obs,
            actions,
            masks=act_masks,
            memories=memories,
            sequence_length=self.policy.sequence_length,
        )
        log_probs = run_out["log_probs"]
        entropy = run_out["entropy"]
        _poca_record_timing("actor_get_stats", time.perf_counter() - started)

        started = time.perf_counter()
        all_obs = [current_obs] + groupmate_obs
        values, _ = self.critic.critic_pass(
            all_obs,
            memories=value_memories,
            sequence_length=self.policy.sequence_length,
        )
        _poca_record_timing("critic_pass", time.perf_counter() - started)

        started = time.perf_counter()
        baselines, _ = self.critic.baseline(
            current_obs,
            (groupmate_obs, groupmate_actions),
            memories=baseline_memories,
            sequence_length=self.policy.sequence_length,
        )
        _poca_record_timing("baseline", time.perf_counter() - started)

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

        started = time.perf_counter()
        self.optimizer.zero_grad()
        _poca_record_timing("zero_grad", time.perf_counter() - started)

        started = time.perf_counter()
        loss.backward()
        _poca_record_timing("backward", time.perf_counter() - started)

        started = time.perf_counter()
        self.optimizer.step()
        _poca_record_timing("optimizer_step", time.perf_counter() - started)

        started = time.perf_counter()
        update_stats = {
            "Losses/Policy Loss": torch.abs(policy_loss).item(),
            "Losses/Value Loss": value_loss.item(),
            "Losses/Baseline Loss": baseline_loss.item(),
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
        cached = None

        if tensor_cache is not None and indices is not None:
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

        try:
            return profiled_poca_update(self, batch, num_sequences)
        finally:
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
    GhostTrainer.advance = pipelined_ghost_advance
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
    _ORIGINAL_GHOST_ADVANCE = original_ghost_advance
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
    global _ORIGINAL_GHOST_ADVANCE
    global _ORIGINAL_MULTI_AGENT_FORWARD
    global _ORIGINAL_TRUST_REGION_POLICY_LOSS
    global _ORIGINAL_MASKED_MEAN
    global _ORIGINAL_BC_UPDATE_BATCH
    global _ORIGINAL_BC_LOSS

    if _ORIGINAL_ACTION_MODEL_FORWARD is None:
        _shutdown_all_poca_pipelines()
        return

    _shutdown_all_poca_pipelines()

    from mlagents.trainers.ghost.trainer import GhostTrainer
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
    GhostTrainer.advance = _ORIGINAL_GHOST_ADVANCE
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
    _ORIGINAL_GHOST_ADVANCE = None
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
