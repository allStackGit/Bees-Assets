"""Concurrency regressions for continual telemetry and demonstration ingestion."""

from __future__ import annotations

import importlib.util
from pathlib import Path
import queue
import sys
import tempfile
import threading
import unittest


MODULE_PATH = Path(__file__).with_name("bees_continual_learning.py")
SPEC = importlib.util.spec_from_file_location("bees_continual_learning", MODULE_PATH)
continual = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = continual
assert SPEC.loader is not None
SPEC.loader.exec_module(continual)

from bees_continual_bootstrap import bootstrap_champion


TEST_CONFIG = {
    "behavior_name": "BeesRL1v1",
    "policy_abi_version": 6,
    "observation_schema_version": 6,
    "action_schema_version": 6,
    "reward_schema_version": 1,
    "scenario_schema_version": 1,
    "promotion": {
        "min_matches_vs_champion": 1,
        "min_win_rate_vs_champion": 0.0,
        "max_critical_regressions": 0,
        "max_historical_regression": 1.0,
        "min_historical_matches_per_opponent": 1,
        "min_competency_cases": 0,
    },
    "historical_league": {
        "base_weight": 1.0,
        "weakness_trigger_regression": 0.05,
        "weakness_bonus_scale": 10.0,
        "max_weight_multiplier": 4.0,
    },
    "ingestion": {
        "max_payload_bytes": 1024 * 1024,
        "max_steps_per_match": 100,
    },
}


class _CoordinatedArchiveStore(continual.ContinualLearningStore):
    """Widen the old check/write race without changing database behavior."""

    def __init__(self, root: Path, gate: threading.Barrier) -> None:
        super().__init__(root, TEST_CONFIG)
        self._archive_gate = gate

    def _write_json_immutable(self, path, value) -> None:
        # With the fixed BEGIN IMMEDIATE, only the winning writer can reach this
        # barrier before its transaction commits, so the timeout releases it.
        # Without serialization both writers reach it together after both duplicate
        # checks returned empty, making the former uniqueness race deterministic.
        try:
            self._archive_gate.wait(timeout=0.25)
        except threading.BrokenBarrierError:
            pass
        super()._write_json_immutable(path, value)


class ConcurrentIngestionTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name) / "store"
        self.store = continual.ContinualLearningStore(self.root, TEST_CONFIG)
        self.store.initialize()

        artifact = Path(self.temp.name) / "champion.onnx"
        artifact.write_bytes(b"champion")
        model = self.store.register_model(
            artifact,
            training_run_id="test-run",
            training_step=1,
            game_build_version="test-build",
        )
        bootstrap_champion(
            self.store,
            model["model_id"],
            reason="Concurrent ingestion test baseline",
        )
        self.model_id = model["model_id"]

    def tearDown(self) -> None:
        self.temp.cleanup()

    def _compatibility_fields(self):
        return {
            "model_id": self.model_id,
            **self.store.compatibility.to_dict(),
        }

    def _telemetry(self):
        return {
            "match_id": "concurrent-match",
            "game_build_version": "test-build",
            "mode": "campaign",
            "result": "bee_win",
            **self._compatibility_fields(),
            "steps": [
                {"observation": [0.0, 1.0], "ai_action": [0.25, -0.5]}
            ],
        }

    def _demonstration(self):
        return {
            "demonstration_id": "concurrent-demo",
            "game_build_version": "test-build",
            **self._compatibility_fields(),
            "examples": [
                {"observation": [0.0, 1.0], "action": [0.2, 0.3]}
            ],
        }

    def _ingest_twice_concurrently(self, method_name: str, payload):
        archive_gate = threading.Barrier(2)
        start_gate = threading.Barrier(3)
        stores = [
            _CoordinatedArchiveStore(self.root, archive_gate),
            _CoordinatedArchiveStore(self.root, archive_gate),
        ]
        results = queue.Queue()
        errors = queue.Queue()

        def worker(store) -> None:
            try:
                start_gate.wait(timeout=2.0)
                results.put(getattr(store, method_name)(payload))
            except BaseException as exc:  # Preserve unexpected SQLite failures for assertion.
                errors.put(exc)

        threads = [threading.Thread(target=worker, args=(store,)) for store in stores]
        for thread in threads:
            thread.start()
        start_gate.wait(timeout=2.0)
        for thread in threads:
            thread.join(timeout=5.0)

        self.assertTrue(all(not thread.is_alive() for thread in threads))
        captured_errors = []
        while not errors.empty():
            captured_errors.append(errors.get_nowait())
        self.assertEqual(captured_errors, [])

        captured_results = []
        while not results.empty():
            captured_results.append(results.get_nowait())
        self.assertEqual(len(captured_results), 2)
        return captured_results

    def test_exact_duplicate_telemetry_is_idempotent_under_concurrent_writers(self):
        results = self._ingest_twice_concurrently("ingest_telemetry", self._telemetry())

        self.assertEqual(sorted(result["duplicate"] for result in results), [False, True])
        self.assertEqual(len({result["batch_id"] for result in results}), 1)
        self.assertEqual(self.store.status()["telemetry_batches"], 1)

    def test_exact_duplicate_demonstration_is_idempotent_under_concurrent_writers(self):
        results = self._ingest_twice_concurrently(
            "ingest_demonstration",
            self._demonstration(),
        )

        self.assertEqual(sorted(result["duplicate"] for result in results), [False, True])
        self.assertEqual(len({result["batch_id"] for result in results}), 1)
        self.assertEqual(self.store.status()["demonstration_batches"], 1)


if __name__ == "__main__":
    unittest.main()
