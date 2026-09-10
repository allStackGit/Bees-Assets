"""Registry and artifact-integrity regression tests for continual learning.

Run from the Bees Assets root:
    python Training\bees_continual_registry_safety_tests.py
"""

from __future__ import annotations

import copy
import importlib.util
from pathlib import Path
import sys
import tempfile
import unittest


MODULE_PATH = Path(__file__).with_name("bees_continual_learning.py")
SPEC = importlib.util.spec_from_file_location("bees_continual_learning", MODULE_PATH)
continual = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = continual
assert SPEC.loader is not None
SPEC.loader.exec_module(continual)


BASE_CONFIG = {
    "behavior_name": "BeesRL1v1",
    "policy_abi_version": 6,
    "observation_schema_version": 6,
    "action_schema_version": 6,
    "reward_schema_version": 1,
    "scenario_schema_version": 1,
    "promotion": {
        "min_matches_vs_champion": 10,
        "min_win_rate_vs_champion": 0.52,
        "max_critical_regressions": 0,
        "max_historical_regression": 0.15,
        "min_historical_matches_per_opponent": 5,
        "min_competency_cases": 1,
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


class RegistrySafetyTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name) / "store"
        self.artifacts = Path(self.temp.name) / "artifacts"
        self.artifacts.mkdir()
        self.store = continual.ContinualLearningStore(self.root, copy.deepcopy(BASE_CONFIG))
        self.store.initialize()

    def tearDown(self):
        self.temp.cleanup()

    def artifact(self, name: str, content: bytes) -> Path:
        path = self.artifacts / name
        path.write_bytes(content)
        return path

    def register(self, name: str, content: bytes, step: int, parent=None):
        return self.store.register_model(
            self.artifact(name, content),
            training_run_id="registry-safety-test",
            training_step=step,
            game_build_version="test-build",
            parent_model_id=parent,
            status="candidate",
        )

    @staticmethod
    def passing_report(candidate_id: str, champion_id=None):
        report = {
            "candidate_model_id": candidate_id,
            "champion_model_id": champion_id,
            "historical": [],
            "competencies": [
                {
                    "name": "smoke",
                    "score": 1.0,
                    "minimum": 0.5,
                    "critical": True,
                }
            ],
            "behavior_sanity_passed": True,
            "runtime_compatible": True,
            "runtime_checks_passed": True,
        }
        if champion_id is not None:
            report["candidate_vs_champion"] = {
                "wins": 6,
                "losses": 4,
                "draws": 0,
            }
        return report

    def promote_first(self, model):
        evaluation = self.store.record_evaluation(
            self.passing_report(model["model_id"])
        )
        return self.store.promote(model["model_id"], evaluation["report_id"])

    def test_registration_rejects_source_that_changes_after_identity_hash(self):
        source = self.artifact("changing.onnx", b"original-model")
        original_copy = self.store._copy_immutable

        def mutate_then_copy(source_path, destination, expected_sha256):
            source_path.write_bytes(b"changed-model")
            return original_copy(source_path, destination, expected_sha256)

        self.store._copy_immutable = mutate_then_copy

        with self.assertRaisesRegex(
            continual.ContinualLearningError,
            "changed while being registered",
        ):
            self.store.register_model(
                source,
                training_run_id="registry-safety-test",
                training_step=100,
                game_build_version="test-build",
            )

        self.assertEqual(self.store.list_models(), [])

    def test_tampered_candidate_cannot_be_promoted(self):
        candidate = self.register("candidate.onnx", b"candidate", 100)
        evaluation = self.store.record_evaluation(
            self.passing_report(candidate["model_id"])
        )
        Path(candidate["artifact_path"]).write_bytes(b"tampered")

        with self.assertRaisesRegex(
            continual.ContinualLearningError,
            "SHA-256 integrity verification",
        ):
            self.store.promote(candidate["model_id"], evaluation["report_id"])

        self.assertIsNone(self.store.current_champion_id())
        self.assertEqual(self.store.get_model(candidate["model_id"])["status"], "candidate")

    def test_failed_multi_artifact_promotion_keeps_database_paths_usable(self):
        first = self.register("first.onnx", b"first", 100)
        self.promote_first(first)
        second = self.register(
            "second.onnx", b"second", 200, parent=first["model_id"]
        )
        evaluation = self.store.record_evaluation(
            self.passing_report(second["model_id"], first["model_id"])
        )

        original_stage = self.store._stage_artifact_for_status
        calls = 0

        def fail_after_first_stage(row, status):
            nonlocal calls
            calls += 1
            if calls == 2:
                raise continual.ContinualLearningError("injected staging failure")
            return original_stage(row, status)

        self.store._stage_artifact_for_status = fail_after_first_stage

        with self.assertRaisesRegex(
            continual.ContinualLearningError,
            "injected staging failure",
        ):
            self.store.promote(second["model_id"], evaluation["report_id"])

        self.assertEqual(self.store.current_champion_id(), first["model_id"])
        first_after = self.store.get_model(first["model_id"])
        second_after = self.store.get_model(second["model_id"])
        self.assertEqual(first_after["status"], "champion")
        self.assertEqual(second_after["status"], "candidate")
        self.assertTrue(Path(first_after["artifact_path"]).is_file())
        self.assertTrue(Path(second_after["artifact_path"]).is_file())

    def test_league_excludes_reward_schema_mismatch(self):
        self._assert_league_excludes_schema_mismatch("reward_schema_version")

    def test_league_excludes_scenario_schema_mismatch(self):
        self._assert_league_excludes_schema_mismatch("scenario_schema_version")

    def _assert_league_excludes_schema_mismatch(self, field: str):
        old = self.register("old.onnx", b"old", 100)
        self.promote_first(old)

        changed_config = copy.deepcopy(BASE_CONFIG)
        changed_config[field] += 1
        changed_store = continual.ContinualLearningStore(self.root, changed_config)
        current_source = self.artifact(f"current-{field}.onnx", field.encode("utf-8"))
        current = changed_store.register_model(
            current_source,
            training_run_id="changed-schema-run",
            training_step=200,
            game_build_version="changed-schema-build",
            status="candidate",
        )

        weighted = changed_store.historical_sampling_weights(current["model_id"])
        self.assertNotIn(old["model_id"], {item["model_id"] for item in weighted})


if __name__ == "__main__":
    unittest.main()
