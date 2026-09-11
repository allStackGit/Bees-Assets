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

import json
import math
import random
import sqlite3
from collections import OrderedDict
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
DEFAULT_POLICY_CACHE_SIZE = 4
AUTHORITATIVE_EVALUATION_TAG = "authoritative_candidate_evaluation"
EVALUATION_TAG_PREFIX = "evaluation:"
# Promotion-policy schema 3 changed historical regression from raw win rate to
# draw-aware score rate. Earlier authoritative rows are valid audit history but must
# never affect current adaptive opponent sampling.
DRAW_AWARE_PROMOTION_POLICY_SCHEMA_VERSION = 3


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


def _validate_cache_size(value: Any) -> int:
    if isinstance(value, bool):
        raise HistoricalOpponentError("Historical policy cache size must be a positive integer.")
    try:
        cache_size = int(value)
    except (TypeError, ValueError) as exc:
        raise HistoricalOpponentError(
            "Historical policy cache size must be a positive integer."
        ) from exc
    if cache_size <= 0 or isinstance(value, float) and not value.is_integer():
        raise HistoricalOpponentError("Historical policy cache size must be a positive integer.")
    return cache_size


def _preflight_onnx_runtime(provider: Optional[str]) -> None:
    """Fail before training starts when frozen-policy inference cannot run."""
    try:
        import onnxruntime as ort
    except ImportError as exc:
        raise HistoricalOpponentError(
            "Persistent historical-opponent training requires onnxruntime in the "
            "Bees ML-Agents virtual environment."
        ) from exc

    requested = provider or "CPUExecutionProvider"
    available = tuple(ort.get_available_providers())
    if requested not in available:
        raise HistoricalOpponentError(
            f"ONNX Runtime provider {requested!r} is unavailable; available providers: "
            + ", ".join(available)
        )


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


def _evaluation_report_id(tags: Sequence[Any]) -> Optional[str]:
    report_ids = [
        value[len(EVALUATION_TAG_PREFIX):].strip()
        for value in tags
        if isinstance(value, str) and value.startswith(EVALUATION_TAG_PREFIX)
    ]
    report_ids = [value for value in report_ids if value]
    if len(report_ids) != 1:
        return None
    return report_ids[0]


def _matches_draw_aware_evaluation(
    report: Any,
    *,
    current_model_id: str,
    opponent_model_id: str,
    current_score_rate: Any,
    previous_score_rate: Any,
    match_count: int,
) -> bool:
    """Prove a persisted pressure row came from the draw-aware evaluator contract."""
    if not isinstance(report, Mapping):
        return False
    policy = report.get("promotion_policy")
    if not isinstance(policy, Mapping):
        return False
    schema_version = policy.get("schema_version")
    if (
        not isinstance(schema_version, int)
        or isinstance(schema_version, bool)
        or schema_version < DRAW_AWARE_PROMOTION_POLICY_SCHEMA_VERSION
    ):
        return False
    if report.get("candidate_model_id") != current_model_id:
        return False

    historical = report.get("historical")
    if not isinstance(historical, list):
        return False
    matching = [
        item
        for item in historical
        if isinstance(item, Mapping)
        and item.get("opponent_model_id") == opponent_model_id
    ]
    if len(matching) != 1:
        return False
    item = matching[0]
    candidate_score_rate = item.get("candidate_score_rate")
    baseline_score_rate = item.get("baseline_score_rate")
    matches = item.get("matches")
    if (
        not isinstance(matches, int)
        or isinstance(matches, bool)
        or matches != match_count
    ):
        return False
    for value in (candidate_score_rate, baseline_score_rate, current_score_rate, previous_score_rate):
        if (
            not isinstance(value, (int, float))
            or isinstance(value, bool)
            or not math.isfinite(float(value))
        ):
            return False
    return math.isclose(
        float(candidate_score_rate),
        float(current_score_rate),
        rel_tol=0.0,
        abs_tol=1e-12,
    ) and math.isclose(
        float(baseline_score_rate),
        float(previous_score_rate),
        rel_tol=0.0,
        abs_tol=1e-12,
    )


def _latest_authoritative_pressure(
    store: ContinualLearningStore,
    opponent_model_ids: Optional[Sequence[str]] = None,
) -> Dict[str, Dict[str, Any]]:
    """Return newest compatible draw-aware authoritative evidence per opponent.

    Historical matchup rows predate explicit score semantics. Rather than mutate or
    delete that audit history, validate each row against the immutable evaluation
    report named in its tags. Only promotion-policy schema 3+ reports can influence
    current sampling because schema 3 introduced draw-aware historical score rates.

    Rows are processed newest-first and evaluation reports are fetched lazily. Once
    every requested active opponent has validated evidence, older rows are not read.
    """
    db_path = getattr(store, "db_path", None)
    if db_path is None or not Path(db_path).is_file():
        return {}

    requested = (
        None
        if opponent_model_ids is None
        else {str(value) for value in opponent_model_ids}
    )
    if requested == set():
        return {}

    compatibility = store.compatibility.to_dict()
    result: Dict[str, Dict[str, Any]] = {}
    report_cache: Dict[str, Optional[Any]] = {}
    try:
        db = sqlite3.connect(str(db_path), timeout=30.0)
        db.row_factory = sqlite3.Row
        rows = db.execute(
            """
            SELECT h.rowid AS matchup_rowid,
                   h.current_model_id, h.opponent_model_id,
                   h.current_win_rate, h.previous_win_rate,
                   h.match_count, h.updated_at, h.tags_json
            FROM historical_matchups h
            JOIN models current_model
              ON current_model.model_id = h.current_model_id
            WHERE current_model.behavior_name = ?
              AND current_model.policy_abi_version = ?
              AND current_model.observation_schema_version = ?
              AND current_model.action_schema_version = ?
              AND current_model.reward_schema_version = ?
              AND current_model.scenario_schema_version = ?
              AND h.tags_json LIKE ?
            ORDER BY h.updated_at DESC, h.rowid DESC
            """,
            (
                compatibility["behavior_name"],
                compatibility["policy_abi_version"],
                compatibility["observation_schema_version"],
                compatibility["action_schema_version"],
                compatibility["reward_schema_version"],
                compatibility["scenario_schema_version"],
                f'%"{AUTHORITATIVE_EVALUATION_TAG}"%',
            ),
        )
        for row in rows:
            opponent_id = str(row["opponent_model_id"])
            if requested is not None and opponent_id not in requested:
                continue
            if opponent_id in result:
                continue
            try:
                tags = json.loads(row["tags_json"])
            except (TypeError, json.JSONDecodeError) as exc:
                raise HistoricalOpponentError(
                    f"Historical matchup tags are corrupted for opponent {opponent_id}."
                ) from exc
            if not isinstance(tags, list) or AUTHORITATIVE_EVALUATION_TAG not in tags:
                continue
            report_id = _evaluation_report_id(tags)
            if report_id is None:
                continue
            if report_id not in report_cache:
                evaluation = db.execute(
                    "SELECT report_json FROM evaluations WHERE report_id = ?",
                    (report_id,),
                ).fetchone()
                if evaluation is None:
                    report_cache[report_id] = None
                else:
                    try:
                        report_cache[report_id] = json.loads(evaluation["report_json"])
                    except (TypeError, json.JSONDecodeError):
                        report_cache[report_id] = None
            report = report_cache[report_id]
            if not _matches_draw_aware_evaluation(
                report,
                current_model_id=str(row["current_model_id"]),
                opponent_model_id=opponent_id,
                current_score_rate=row["current_win_rate"],
                previous_score_rate=row["previous_win_rate"],
                match_count=int(row["match_count"]),
            ):
                continue
            result[opponent_id] = {
                "current_model_id": str(row["current_model_id"]),
                "current_win_rate": row["current_win_rate"],
                "previous_win_rate": row["previous_win_rate"],
                "match_count": int(row["match_count"]),
                "tags": tags,
                "evaluation_report_id": report_id,
            }
            if requested is not None and requested.issubset(result):
                break
    except sqlite3.Error as exc:
        raise HistoricalOpponentError(
            f"Could not read authoritative historical regression pressure: {exc}"
        ) from exc
    finally:
        if "db" in locals():
            db.close()
    return result


def _historical_training_weights(
    store: ContinualLearningStore,
    champion_model_id: str,
) -> List[Dict[str, Any]]:
    """Overlay validated post-champion regressions onto base league weights.

    The registry table predates draw-aware scoring, so its persisted regression value
    cannot be trusted by itself. Use the registry only to enumerate compatible league
    opponents, reset each to the configured base weight, then overlay pressure that can
    be proven against a schema-3+ immutable evaluation report. This makes an upgraded
    store safe without deleting historical audit rows.
    """
    weighted = [
        dict(item)
        for item in store.historical_sampling_weights(champion_model_id)
    ]
    if not weighted:
        return weighted

    settings = store.config["historical_league"]
    base = float(settings["base_weight"])
    trigger = float(settings["weakness_trigger_regression"])
    scale = float(settings["weakness_bonus_scale"])
    cap = float(settings["max_weight_multiplier"])

    # Discard any pressure calculated directly from legacy historical_matchups rows.
    # A validated authoritative report below is the only source allowed to raise it.
    for item in weighted:
        item["weight"] = base
        item["regression"] = 0.0
        item["match_count"] = 0
        item["tags"] = []
        item.pop("pressure_model_id", None)
        item.pop("pressure_evaluation_report_id", None)

    pressure = _latest_authoritative_pressure(
        store,
        [str(item["model_id"]) for item in weighted],
    )
    if not pressure:
        return weighted

    for item in weighted:
        opponent_id = str(item["model_id"])
        evidence = pressure.get(opponent_id)
        if evidence is None:
            continue
        current = evidence["current_win_rate"]
        previous = evidence["previous_win_rate"]
        regression = 0.0
        if current is not None and previous is not None:
            regression = max(
                0.0,
                float(previous) - float(current) - trigger,
            )
        multiplier = min(cap, 1.0 + regression * scale)
        item["weight"] = base * multiplier
        item["regression"] = regression
        item["match_count"] = evidence["match_count"]
        item["tags"] = list(evidence["tags"])
        item["pressure_model_id"] = evidence["current_model_id"]
        item["pressure_evaluation_report_id"] = evidence["evaluation_report_id"]
    return weighted


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
    # ML-Agents 1.1.0 exports recurrent_in even for feed-forward policies; the
    # zero-width memory tensor is a placeholder, not recurrent state. Only a
    # positive exported memory size makes the frozen ONNX policy recurrent.
    if bool(getattr(template_policy, "use_recurrent", False)) or engine.memory_size > 0:
        raise HistoricalOpponentError(
            "Persistent historical-opponent training currently supports feed-forward "
            "Bees policies only. Add global-agent recurrent-memory ownership before "
            "enabling it for recurrent networks."
        )
    engine.validate_behavior_spec(template_policy.behavior_spec)

    class FrozenOnnxPolicy(Policy):
        # Bees' cross-worker batching path may combine calls for policies that set
        # this marker. Recurrent policies must never set it because local agent IDs
        # are not globally unique across environment workers.
        bees_batch_inference_safe = True

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
        cache_size: int = DEFAULT_POLICY_CACHE_SIZE,
        policy_factory: Optional[PolicyFactory] = None,
    ) -> None:
        self.store = store
        self.ratio = _validate_ratio(ratio)
        self.provider = provider
        self.cache_size = _validate_cache_size(cache_size)
        self.rng = random.Random(seed)
        self.policy_factory = policy_factory or _build_frozen_onnx_policy
        self._policy_cache: OrderedDict[Tuple[Any, ...], Any] = OrderedDict()
        # Once an external opponent participates, ML-Agents' built-in ELO can no
        # longer attribute episode outcomes solely to entries in its Torch snapshot
        # table. Keep it disabled for the remainder of this process; authoritative
        # continual evaluation owns cross-generation performance instead.
        self.external_history_used = False

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
            self._policy_cache.move_to_end(key)
            return cached
        path = _validated_model_path(self.store, model_id)
        policy = self.policy_factory(template_policy, path, self.provider)
        self._policy_cache[key] = policy
        self._policy_cache.move_to_end(key)
        while len(self._policy_cache) > self.cache_size:
            self._policy_cache.popitem(last=False)
        return policy

    def clear_cache(self) -> None:
        self._policy_cache.clear()

    def override_non_learning_teams(self, trainer: Any) -> List[HistoricalOverride]:
        setattr(trainer, HISTORICAL_ACTIVE_ATTRIBUTE, False)
        if self.ratio <= 0.0:
            self._record_active_stat(trainer, False)
            return []

        current_model_id = self.store.current_champion_id()
        if not current_model_id:
            self._record_active_stat(trainer, False)
            return []

        weighted = _historical_training_weights(self.store, current_model_id)
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
        if active:
            self.external_history_used = True
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
        self.scheduler.clear_cache()
        self._restored = True


def install_historical_opponents(
    store: ContinualLearningStore,
    *,
    ratio: float,
    seed: int = 0,
    provider: Optional[str] = None,
    cache_size: int = DEFAULT_POLICY_CACHE_SIZE,
    ghost_trainer_cls: Optional[Any] = None,
    policy_factory: Optional[PolicyFactory] = None,
) -> HistoricalOpponentPatch:
    """Patch ML-Agents 1.1.0 GhostTrainer for persistent ONNX league opponents."""
    validated_ratio = _validate_ratio(ratio)
    validated_cache_size = _validate_cache_size(cache_size)
    if validated_ratio > 0.0 and policy_factory is None:
        _preflight_onnx_runtime(provider)

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
        cache_size=validated_cache_size,
        policy_factory=policy_factory,
    )
    original_swap = ghost_trainer_cls._swap_snapshots
    original_process = ghost_trainer_cls._process_trajectory

    def swap_with_persistent_history(trainer: Any) -> Any:
        result = original_swap(trainer)
        scheduler.override_non_learning_teams(trainer)
        return result

    def process_trajectory_without_false_elo(trainer: Any, trajectory: Any) -> Any:
        # An episode may span a snapshot swap boundary. Once the external league
        # has been used, a later terminal result cannot be safely attributed to
        # ML-Agents' in-memory opponent index, even if the current interval has
        # switched back to an internal snapshot. Disable that diagnostic rather
        # than write a confidently wrong ELO update.
        if scheduler.external_history_used:
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
