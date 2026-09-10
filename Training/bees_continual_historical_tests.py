"""Focused tests for Training/bees_continual_historical.py.

These tests exercise scheduling and GhostTrainer patch ownership without importing
ML-Agents or ONNX Runtime. The real frozen-policy adapter is covered by the evaluator's
ONNX contract plus end-to-end training validation.
"""

from __future__ import annotations

import importlib.util
from pathlib import Path
import sys
import tempfile
import types
import unittest


MODULE_PATH = Path(__file__).with_name("bees_continual_historical.py")
SPEC = importlib.util.spec_from_file_location("bees_continual_historical", MODULE_PATH)
historical = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = historical
assert SPEC.loader is not None
SPEC.loader.exec_module(historical)


class _Compatibility:
    def to_dict(self):
        return {
            "behavior_name": "BeesRL1v1",
            "policy_abi_version": 6,
            "observation_schema_version": 6,
            "action_schema_version": 6,
            "reward_schema_version": 1,
            "scenario_schema_version": 1,
        }


class _FakeStore:
    def __init__(self, artifact: Path, *, weighted=None, champion="champion"):
        self.compatibility = _Compatibility()
        self.artifact = artifact
        self.champion = champion
        self.weighted = list(
            weighted
            if weighted is not None
            else [
                {
                    "model_id": "historical-1",
                    "status": "historical",
                    "weight": 1.0,
                    "regression": 0.15,
                }
            ]
        )
        self.weight_requests = []

    def current_champion_id(self):
        return self.champion

    def historical_sampling_weights(self, current_model_id):
        self.weight_requests.append(current_model_id)
        return list(self.weighted)

    def get_model(self, model_id):
        return {
            "model_id": model_id,
            "status": "historical",
            "artifact_path": str(self.artifact),
            "artifact_sha256": historical.sha256_file(self.artifact),
            **self.compatibility.to_dict(),
        }


class _ObservationSpec:
    def __init__(self, shape):
        self.shape = shape


class _ActionSpec:
    continuous_size = 3
    discrete_branches = (2, 4)


class _BehaviorSpec:
    observation_specs = (_ObservationSpec((8,)),)
    action_spec = _ActionSpec()


class _TemplatePolicy:
    behavior_spec = _BehaviorSpec()


class _Queue:
    def __init__(self):
        self.values = []

    def put(self, value):
        self.values.append(value)


class _Stats:
    def __init__(self):
        self.values = []

    def add_stat(self, name, value):
        self.values.append((name, value))


class _Trainer:
    def __init__(self):
        self._learning_team = 0
        self._team_to_name_to_policy_queue = {
            0: {"BeesRL1v1": _Queue()},
            1: {"BeesRL1v1": _Queue()},
        }
        self._stats_reporter = _Stats()
        self.policy = _TemplatePolicy()

    def get_policy(self, behavior_id):
        if behavior_id not in {"BeesRL1v1?team=0", "BeesRL1v1?team=1"}:
            raise AssertionError(f"unexpected behavior id {behavior_id}")
        return self.policy


class HistoricalSchedulerTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.artifact = Path(self.temp.name) / "historical.onnx"
        self.artifact.write_bytes(b"historical-policy")

    def tearDown(self):
        self.temp.cleanup()

    def test_ratio_one_overrides_only_non_learning_team(self):
        store = _FakeStore(self.artifact)
        created = []

        def policy_factory(template, path, provider):
            value = (template, path, provider, len(created))
            created.append(value)
            return value

        scheduler = historical.HistoricalOpponentScheduler(
            store,
            ratio=1.0,
            seed=7,
            provider="CPUExecutionProvider",
            policy_factory=policy_factory,
        )
        trainer = _Trainer()

        overrides = scheduler.override_non_learning_teams(trainer)

        self.assertEqual(store.weight_requests, ["champion"])
        self.assertEqual(len(overrides), 1)
        self.assertEqual(overrides[0].team_id, 1)
        self.assertEqual(overrides[0].model_id, "historical-1")
        self.assertEqual(trainer._team_to_name_to_policy_queue[0]["BeesRL1v1"].values, [])
        self.assertEqual(len(trainer._team_to_name_to_policy_queue[1]["BeesRL1v1"].values), 1)
        self.assertTrue(getattr(trainer, historical.HISTORICAL_ACTIVE_ATTRIBUTE))
        self.assertTrue(scheduler.external_history_used)

    def test_policy_is_cached_across_swap_intervals(self):
        store = _FakeStore(self.artifact)
        created = []

        def policy_factory(template, path, provider):
            created.append(path)
            return object()

        scheduler = historical.HistoricalOpponentScheduler(
            store,
            ratio=1.0,
            seed=3,
            policy_factory=policy_factory,
        )
        trainer = _Trainer()

        scheduler.override_non_learning_teams(trainer)
        scheduler.override_non_learning_teams(trainer)

        self.assertEqual(len(created), 1)
        self.assertEqual(
            len(trainer._team_to_name_to_policy_queue[1]["BeesRL1v1"].values),
            2,
        )

    def test_policy_cache_is_bounded_lru(self):
        store = _FakeStore(self.artifact)
        created = []

        def policy_factory(template, path, provider):
            value = object()
            created.append(value)
            return value

        scheduler = historical.HistoricalOpponentScheduler(
            store,
            ratio=1.0,
            cache_size=2,
            policy_factory=policy_factory,
        )
        template = _TemplatePolicy()

        first_a = scheduler._policy_for("a", "BeesRL1v1?team=1", template)
        scheduler._policy_for("b", "BeesRL1v1?team=1", template)
        self.assertIs(scheduler._policy_for("a", "BeesRL1v1?team=1", template), first_a)
        scheduler._policy_for("c", "BeesRL1v1?team=1", template)
        second_b = scheduler._policy_for("b", "BeesRL1v1?team=1", template)

        self.assertEqual(len(scheduler._policy_cache), 2)
        self.assertIsNot(second_b, created[1])
        self.assertEqual(len(created), 4)

    def test_no_champion_or_history_falls_back_without_override(self):
        for store in (
            _FakeStore(self.artifact, champion=None),
            _FakeStore(self.artifact, weighted=[]),
        ):
            scheduler = historical.HistoricalOpponentScheduler(
                store,
                ratio=1.0,
                policy_factory=lambda *_: object(),
            )
            trainer = _Trainer()

            self.assertEqual(scheduler.override_non_learning_teams(trainer), [])
            self.assertFalse(getattr(trainer, historical.HISTORICAL_ACTIVE_ATTRIBUTE))
            self.assertFalse(scheduler.external_history_used)

    def test_weighted_selection_is_deterministic_for_seed(self):
        weighted = [
            {"model_id": "old", "weight": 1.0, "regression": 0.0},
            {"model_id": "weakness", "weight": 4.0, "regression": 0.3},
        ]
        first = historical.HistoricalOpponentScheduler._choose_weighted(
            weighted,
            historical.random.Random(11),
        )
        second = historical.HistoricalOpponentScheduler._choose_weighted(
            weighted,
            historical.random.Random(11),
        )
        self.assertEqual(first["model_id"], second["model_id"])

    def test_invalid_ratio_and_cache_size_are_rejected(self):
        store = _FakeStore(self.artifact)
        for value in (-0.01, 1.01, float("nan"), True, "not-a-number"):
            with self.subTest(ratio=value):
                with self.assertRaises(historical.HistoricalOpponentError):
                    historical.HistoricalOpponentScheduler(store, ratio=value)
        for value in (0, -1, 1.5, True, "not-a-number"):
            with self.subTest(cache_size=value):
                with self.assertRaises(historical.HistoricalOpponentError):
                    historical.HistoricalOpponentScheduler(
                        store,
                        ratio=1.0,
                        cache_size=value,
                    )

    def test_onnx_provider_preflight_rejects_unavailable_provider(self):
        previous = sys.modules.get("onnxruntime")
        sys.modules["onnxruntime"] = types.SimpleNamespace(
            get_available_providers=lambda: ["CPUExecutionProvider"]
        )
        try:
            with self.assertRaisesRegex(
                historical.HistoricalOpponentError,
                "unavailable",
            ):
                historical._preflight_onnx_runtime("CUDAExecutionProvider")
            historical._preflight_onnx_runtime("CPUExecutionProvider")
        finally:
            if previous is None:
                del sys.modules["onnxruntime"]
            else:
                sys.modules["onnxruntime"] = previous

    def test_artifact_path_rereads_once_across_status_move_race(self):
        replacement = Path(self.temp.name) / "moved.onnx"
        replacement.write_bytes(b"moved-policy")
        missing = Path(self.temp.name) / "old-location.onnx"

        class MovingStore(_FakeStore):
            def __init__(self):
                super().__init__(replacement)
                self.calls = 0

            def get_model(self, model_id):
                self.calls += 1
                path = missing if self.calls == 1 else replacement
                return {
                    "model_id": model_id,
                    "status": "historical",
                    "artifact_path": str(path),
                    "artifact_sha256": historical.sha256_file(replacement),
                    **self.compatibility.to_dict(),
                }

        store = MovingStore()
        resolved = historical._validated_model_path(store, "historical-1")
        self.assertEqual(resolved, replacement)
        self.assertEqual(store.calls, 2)


class GhostTrainerPatchTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.artifact = Path(self.temp.name) / "historical.onnx"
        self.artifact.write_bytes(b"historical-policy")

    def tearDown(self):
        self.temp.cleanup()

    def test_patch_preserves_normal_swap_then_overrides_and_disables_false_elo(self):
        store = _FakeStore(self.artifact)

        class FakeGhostTrainer(_Trainer):
            def __init__(self):
                super().__init__()
                self.normal_swaps = 0
                self.elo_updates = 0

            def _swap_snapshots(self):
                self.normal_swaps += 1
                self._team_to_name_to_policy_queue[1]["BeesRL1v1"].put("normal")
                return "swapped"

            def _process_trajectory(self, trajectory):
                self.elo_updates += 1
                return "elo"

        patch = historical.install_historical_opponents(
            store,
            ratio=1.0,
            seed=5,
            ghost_trainer_cls=FakeGhostTrainer,
            policy_factory=lambda *_: "historical",
        )
        try:
            trainer = FakeGhostTrainer()
            result = trainer._swap_snapshots()
            elo_result = trainer._process_trajectory(object())

            self.assertEqual(result, "swapped")
            self.assertEqual(trainer.normal_swaps, 1)
            self.assertEqual(
                trainer._team_to_name_to_policy_queue[1]["BeesRL1v1"].values,
                ["normal", "historical"],
            )
            self.assertIsNone(elo_result)
            self.assertEqual(trainer.elo_updates, 0)

            # Even after returning to an internal snapshot, an episode can have
            # crossed the prior external-opponent boundary. Keep snapshot ELO off.
            store.weighted = []
            trainer._swap_snapshots()
            self.assertFalse(getattr(trainer, historical.HISTORICAL_ACTIVE_ATTRIBUTE))
            self.assertIsNone(trainer._process_trajectory(object()))
            self.assertEqual(trainer.elo_updates, 0)
        finally:
            patch.restore()

        trainer = FakeGhostTrainer()
        self.assertEqual(trainer._process_trajectory(object()), "elo")
        self.assertEqual(trainer.elo_updates, 1)

    def test_patch_keeps_normal_elo_when_history_is_never_active(self):
        store = _FakeStore(self.artifact, weighted=[])

        class FakeGhostTrainer(_Trainer):
            def _swap_snapshots(self):
                return None

            def _process_trajectory(self, trajectory):
                return "elo"

        patch = historical.install_historical_opponents(
            store,
            ratio=1.0,
            ghost_trainer_cls=FakeGhostTrainer,
            policy_factory=lambda *_: "historical",
        )
        try:
            trainer = FakeGhostTrainer()
            trainer._swap_snapshots()
            self.assertEqual(trainer._process_trajectory(object()), "elo")
        finally:
            patch.restore()


if __name__ == "__main__":
    unittest.main()
