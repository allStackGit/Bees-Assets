"""Bridge the persistent Bees historical league into ML-Agents self-play training.

ML-Agents 1.1.0 GhostTrainer keeps recent opponents as in-memory Torch snapshots.
The continual-learning league instead stores immutable ONNX artifacts that survive
processes and training generations. This module preserves GhostTrainer's normal
learning-team and snapshot behavior, then optionally overrides only the current
non-learning team's policy queue with a frozen historical ONNX policy.

The learning team's trajectories still come from the current Torch policy and remain
on-policy PPO data. Trajectories from the frozen opponent continue to be discarded by
GhostTrainer exactly like ordinary ghost-policy trajectories.
"""

from __future__ import annotations

import math
import random
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Callable, Dict, List, Mapping, Optional, Sequence, Tuple

from bees_continual_learning import (
    ContinualLearningError,
    ContinualLearningStore,
    sha256_file,
)


EXPECTED_MLAGENTS_VERSION = "1.1.0"
HISTORICAL_ACTIVE_ATTRIBUTE = "_bees_external_historical_active"
PATCHED_ATTRIBUTE = "_bees_historical_opponent_patch"


class HistoricalOpponentError(ContinualLearningError):
    """Raised when a persistent historical opponent cannot be used safely."""


PolicyFactory = Callable[[Any, Path, Optional[str]], Any]


@dataclass(frozen=True)
class HistoricalOverride:
    team_id: int
    model_id: str
    weight: float
    regression: float


def _validate_ratio(value: Any) -> float:
    if isinstance(value, bool):
        raise HistoricalOpponentError("Historical training ratio must be a number in [0,1].")
    try:
        ratio = float(value)
    except (TypeError, ValueError) as exc:
        raise HistoricalOpponentError(
            "Historical training ratio must be a number in [0,1]."
        ) from exc
    if not math.isfinite(ratio) or ratio < 0.0 or ratio > 1.0:
        raise HistoricalOpponentError("Historical training ratio must be in [0,1].")
    return ratio


def _behavior_signature(policy: Any) -> Tuple[Any, ...]:
    spec = policy.behavior_spec
    return (
        tuple(tuple(item.shape) for item in spec.observation_specs),
        int(spec.action_spec.continuous_size),
        tuple(int(value) for value in spec.action_spec.discrete_branches),
    )


def _validated_model_path(store: ContinualLearningStore, model_id: str) -> Path:
    """Resolve one compatible immutable artifact, tolerating a concurrent status move."""
    expected_compatibility = store.compatibility.to_dict()
    last_missing: Optional[Path] = None

    for _ in range(2):
        model = store.get_model(model_id)
        if model.get("status") not in ("historical", "champion"):
            raise HistoricalOpponentError(
                f"Historical opponent {model_id} has invalid status {model.get('status')!r}."
            )
        actual_compatibility = {
            key: model.get(key) for key in expected_compatibility
        }
        if actual_compatibility != expected_compatibility:
            raise HistoricalOpponentError(
                f"Historical opponent {model_id} is incompatible with this training store."
            )

        path = Path(str(model.get("artifact_path", "")))
        if not path.is_file():
            # Promotion/rollback stages the destination before committing the new DB
            # path and deletes the old source afterward. A reader that raced exactly
            # across that commit can observe the just-retired path once; re-read it.
            last_missing = path
            continue

        expected_hash = str(model.get("artifact_sha256", "")).strip().lower()
        if not expected_hash:
            raise HistoricalOpponentError(
                f"Historical opponent {model_id} is missing its artifact SHA-256."
            )
        if sha256_file(path).lower() != expected_hash:
            raise HistoricalOpponentError(
                f"Historical opponent {model_id} failed SHA-256 integrity verification."
            )
        return path

    raise HistoricalOpponentError(
        f"Historical opponent {model_id} artifact is missing: {last_missing}"
    )


def _build_frozen_onnx_policy(
    template_policy: Any,
    model_path: Path,
    provider: Optional[str],
) -> Any:
    """Create a Policy-compatible deterministic ONNX actor for a ghost team."""
    try:
        from mlagents.trainers.action_info import ActionInfo
        from mlagents.trainers.policy import Policy
        from mlagents_envs.base_env import ActionTuple
    except ImportError as exc:
        raise HistoricalOpponentError(
            "ML-Agents 1.1.0 is required for persistent historical-opponent training."
        ) from exc

    from bees_continual_evaluate import OnnxPolicy

    engine = OnnxPolicy(model_path, provider=provider)
    if bool(getattr(template_policy, "use_recurrent", False)) or engine.has_recurrent_input:
        raise HistoricalOpponentError(
            "Persistent historical-opponent training currently supports feed-forward "
            "Bees policies only. Add global-agent recurrent-memory ownership before "
            "enabling it for recurrent networks."
        )
    engine.validate_behavior_spec(template_policy.behavior_spec)

    class FrozenOnnxPolicy(Policy):
        def __init__(self) -> None:
            super().__init__(
                int(getattr(template_policy, "seed", 0)),
                template_policy.behavior_spec,
                template_policy.network_settings,
            )
            self._engine = engine
            self._step = 0

        def get_action(self, decision_requests: Any, worker_id: int = 0) -> Any:
            if len(decision_requests) == 0:
                return ActionInfo.empty()
            action = self._engine.actions(
                decision_requests,
                self.behavior_spec,
                ActionTuple,
            )
            self.check_nan_action(action)
            # AgentProcessor only needs the action output for ghost trajectories;
            # those trajectories are discarded by GhostTrainer and never reach PPO.
            outputs = {"action": action}
            return ActionInfo(
                action=action,
                env_action=action,
                outputs=outputs,
                agent_ids=list(decision_requests.agent_id),
            )

        def increment_step(self, n_steps: int) -> int:
            self._step += int(n_steps)
            return self._step

        def get_current_step(self) -> int:
            return self._step

        def load_weights(self, values: Sequence[Any]) -> None:
            raise HistoricalOpponentError(
                "Frozen historical ONNX policies cannot accept Torch weight updates."
            )

        def get_weights(self) -> List[Any]:
            raise HistoricalOpponentError(
                "Frozen historical ONNX policies do not expose Torch weights."
            )

        def init_load_weights(self) -> None:
            return None

    return FrozenOnnxPolicy()


class HistoricalOpponentScheduler:
    """Choose and publish persistent historical policies at GhostTrainer swap points."""

    def __init__(
        self,
        store: ContinualLearningStore,
        *,
        ratio: float,
        seed: int = 0,
        provider: Optional[str] = None,
        policy_factory: Optional[PolicyFactory] = None,
    ) -> None:
        self.store = store
        self.ratio = _validate_ratio(ratio)
        self.provider = provider
        self.rng = random.Random(seed)
        self.policy_factory = policy_factory or _build_frozen_onnx_policy
        self._policy_cache: Dict[Tuple[Any, ...], Any] = {}

    @staticmethod
    def _choose_weighted(
        weighted: Sequence[Mapping[str, Any]],
        rng: random.Random,
    ) -> Mapping[str, Any]:
        usable = []
        total = 0.0
        for item in weighted:
            try:
                weight = float(item.get("weight", 0.0))
            except (TypeError, ValueError) as exc:
                raise HistoricalOpponentError(
                    f"Historical opponent has invalid weight: {item!r}"
                ) from exc
            if not math.isfinite(weight) or weight < 0.0:
                raise HistoricalOpponentError(
                    f"Historical opponent has invalid weight {weight!r}."
                )
            if weight > 0.0:
                usable.append((item, weight))
                total += weight
        if total <= 0.0:
            raise HistoricalOpponentError(
                "Compatible historical opponent weights sum to zero."
            )

        target = rng.random() * total
        running = 0.0
        for item, weight in usable:
            running += weight
            if target <= running:
                return item
        return usable[-1][0]

    def _policy_for(
        self,
        model_id: str,
        behavior_id: str,
        template_policy: Any,
    ) -> Any:
        key = (model_id, behavior_id, _behavior_signature(template_policy), self.provider)
        cached = self._policy_cache.get(key)
        if cached is not None:
            return cached
        path = _validated_model_path(self.store, model_id)
        policy = self.policy_factory(template_policy, path, self.provider)
        self._policy_cache[key] = policy
        return policy

    def override_non_learning_teams(self, trainer: Any) -> List[HistoricalOverride]:
        setattr(trainer, HISTORICAL_ACTIVE_ATTRIBUTE, False)
        if self.ratio <= 0.0:
            self._record_active_stat(trainer, False)
            return []

        current_model_id = self.store.current_champion_id()
        if not current_model_id:
            self._record_active_stat(trainer, False)
            return []

        weighted = self.store.historical_sampling_weights(current_model_id)
        if not weighted:
            self._record_active_stat(trainer, False)
            return []

        overrides: List[HistoricalOverride] = []
        team_queues = trainer._team_to_name_to_policy_queue
        learning_team = trainer._learning_team
        for team_id in sorted(team_queues):
            if team_id == learning_team:
                continue
            if self.rng.random() >= self.ratio:
                continue

            selection = self._choose_weighted(weighted, self.rng)
            model_id = str(selection["model_id"])
            queues_for_team = team_queues[team_id]
            for brain_name in sorted(queues_for_team):
                behavior_id = f"{brain_name}?team={team_id}"
                template_policy = trainer.get_policy(behavior_id)
                frozen_policy = self._policy_for(
                    model_id,
                    behavior_id,
                    template_policy,
                )
                # GhostTrainer has already queued its normal recent snapshot. The
                # EnvManager drains the queue and deliberately keeps the last policy,
                # so this frozen policy becomes the active opponent for this interval.
                queues_for_team[brain_name].put(frozen_policy)

            override = HistoricalOverride(
                team_id=int(team_id),
                model_id=model_id,
                weight=float(selection.get("weight", 0.0)),
                regression=float(selection.get("regression", 0.0)),
            )
            overrides.append(override)

        active = bool(overrides)
        setattr(trainer, HISTORICAL_ACTIVE_ATTRIBUTE, active)
        self._record_active_stat(trainer, active)
        if active:
            reporter = getattr(trainer, "_stats_reporter", None)
            if reporter is not None:
                for item in overrides:
                    reporter.add_stat(
                        "Self-play/Historical League Selected Weight",
                        item.weight,
                    )
                    reporter.add_stat(
                        "Self-play/Historical League Selected Regression",
                        item.regression,
                    )
        return overrides

    @staticmethod
    def _record_active_stat(trainer: Any, active: bool) -> None:
        reporter = getattr(trainer, "_stats_reporter", None)
        if reporter is not None:
            reporter.add_stat(
                "Self-play/Historical League Active",
                1.0 if active else 0.0,
            )


class HistoricalOpponentPatch:
    def __init__(
        self,
        ghost_trainer_cls: Any,
        scheduler: HistoricalOpponentScheduler,
        original_swap: Callable[..., Any],
        original_process_trajectory: Callable[..., Any],
    ) -> None:
        self.ghost_trainer_cls = ghost_trainer_cls
        self.scheduler = scheduler
        self.original_swap = original_swap
        self.original_process_trajectory = original_process_trajectory
        self._restored = False

    def restore(self) -> None:
        if self._restored:
            return
        self.ghost_trainer_cls._swap_snapshots = self.original_swap
        self.ghost_trainer_cls._process_trajectory = self.original_process_trajectory
        if getattr(self.ghost_trainer_cls, PATCHED_ATTRIBUTE, None) is self:
            delattr(self.ghost_trainer_cls, PATCHED_ATTRIBUTE)
        self._restored = True


def install_historical_opponents(
    store: ContinualLearningStore,
    *,
    ratio: float,
    seed: int = 0,
    provider: Optional[str] = None,
    ghost_trainer_cls: Optional[Any] = None,
    policy_factory: Optional[PolicyFactory] = None,
) -> HistoricalOpponentPatch:
    """Patch ML-Agents 1.1.0 GhostTrainer for persistent ONNX league opponents."""
    validated_ratio = _validate_ratio(ratio)
    if ghost_trainer_cls is None:
        try:
            import mlagents.trainers
            from mlagents.trainers.ghost.trainer import GhostTrainer
        except ImportError as exc:
            raise HistoricalOpponentError(
                "ML-Agents is required for historical-opponent training."
            ) from exc
        if mlagents.trainers.__version__ != EXPECTED_MLAGENTS_VERSION:
            raise HistoricalOpponentError(
                "Persistent historical-opponent integration is verified only for "
                f"ML-Agents {EXPECTED_MLAGENTS_VERSION}; installed version is "
                f"{mlagents.trainers.__version__}."
            )
        ghost_trainer_cls = GhostTrainer

    existing_patch = getattr(ghost_trainer_cls, PATCHED_ATTRIBUTE, None)
    if existing_patch is not None:
        raise HistoricalOpponentError(
            "Historical-opponent GhostTrainer integration is already installed."
        )

    scheduler = HistoricalOpponentScheduler(
        store,
        ratio=validated_ratio,
        seed=seed,
        provider=provider,
        policy_factory=policy_factory,
    )
    original_swap = ghost_trainer_cls._swap_snapshots
    original_process = ghost_trainer_cls._process_trajectory

    def swap_with_persistent_history(trainer: Any) -> Any:
        result = original_swap(trainer)
        scheduler.override_non_learning_teams(trainer)
        return result

    def process_trajectory_without_false_elo(trainer: Any, trajectory: Any) -> Any:
        # ML-Agents' ELO table only knows its in-memory snapshot window. Updating
        # one of those ratings from a match actually played against an external
        # persistent ONNX policy would corrupt the diagnostic. PPO training is
        # unaffected because this method only owns GhostTrainer's ELO accounting.
        if bool(getattr(trainer, HISTORICAL_ACTIVE_ATTRIBUTE, False)):
            return None
        return original_process(trainer, trajectory)

    ghost_trainer_cls._swap_snapshots = swap_with_persistent_history
    ghost_trainer_cls._process_trajectory = process_trajectory_without_false_elo
    patch = HistoricalOpponentPatch(
        ghost_trainer_cls,
        scheduler,
        original_swap,
        original_process,
    )
    setattr(ghost_trainer_cls, PATCHED_ATTRIBUTE, patch)
    return patch
