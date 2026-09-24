"""Permanent competency-suite trust-boundary regression tests.

Run from the Bees Assets root:
    python Training\bees_continual_competency_suite_tests.py
"""

from __future__ import annotations

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

from bees_continual_bootstrap import bootstrap_champion


TEST_CONFIG = {
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


class PermanentCompetencySuiteTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name) / "store"
        self.artifacts = Path(self.temp.name) / "artifacts"
        self.artifacts.mkdir()
        self.store = continual.ContinualLearningStore(self.root, TEST_CONFIG)
        self.store.initialize()
        self.champion = self.register("champion.onnx", b"champion", 100)
        bootstrap_champion(
            self.store,
            self.champion["model_id"],
            reason="Permanent competency test baseline",
        )

    def tearDown(self):
        self.temp.cleanup()

    def register(self, name: str, content: bytes, step: int, parent=None):
        artifact = self.artifacts / name
        artifact.write_bytes(content)
        return self.store.register_model(
            artifact,
            training_run_id="competency-suite-test",
            training_step=step,
            game_build_version="test-build",
            parent_model_id=parent,
            status="candidate",
        )

    def suite(self, minimum: float = 0.5):
        return {
            "schema_version": 1,
            "cases": [
                {
                    "name": "smoke",
                    "opponent_model_id": self.champion["model_id"],
                    "matches": 5,
                    "minimum": minimum,
                    "metric": "score_rate",
                    "critical": True,
                    "env_args": ["--rl-map-size", "64"],
                }
            ],
        }

    def report(self, candidate_id: str, minimum: float = 0.5):
        return {
            "candidate_model_id": candidate_id,
            "champion_model_id": self.champion["model_id"],
            "candidate_vs_champion": {"wins": 6, "losses": 4, "draws": 0},
            "historical": [],
            "competencies": [
                {
                    "name": "smoke",
                    "opponent_model_id": self.champion["model_id"],
                    "matches": 5,
                    "minimum": minimum,
                    "metric": "score_rate",
                    "critical": True,
                    "env_args": ["--rl-map-size", "64"],
                    "score": 1.0,
                }
            ],
            "behavior_sanity_passed": True,
            "runtime_compatible": True,
            "runtime_checks_passed": True,
        }

    def test_evaluation_fails_until_permanent_suite_is_pinned(self):
        candidate = self.register(
            "candidate.onnx", b"candidate", 200, parent=self.champion["model_id"]
        )
        evaluation = self.store.record_evaluation(self.report(candidate["model_id"]))

        self.assertFalse(evaluation["passed"])
        self.assertTrue(
            any("no permanent competency suite is pinned" in reason for reason in evaluation["reasons"])
        )

    def test_matching_pinned_contract_can_pass_and_is_visible_in_status(self):
        pinned = self.store.pin_competency_suite(self.suite())
        candidate = self.register(
            "candidate.onnx", b"candidate", 200, parent=self.champion["model_id"]
        )
        evaluation = self.store.record_evaluation(self.report(candidate["model_id"]))

        self.assertTrue(evaluation["passed"])
        status = self.store.status()["permanent_competency_suite"]
        self.assertEqual(status["fingerprint"], pinned["fingerprint"])
        self.assertEqual(status["case_count"], 1)

    def test_arbitrary_easier_report_contract_cannot_satisfy_gate(self):
        self.store.pin_competency_suite(self.suite(minimum=0.5))
        candidate = self.register(
            "candidate.onnx", b"candidate", 200, parent=self.champion["model_id"]
        )
        evaluation = self.store.record_evaluation(
            self.report(candidate["model_id"], minimum=0.1)
        )

        self.assertFalse(evaluation["passed"])
        self.assertTrue(
            any("do not match the pinned permanent competency suite" in reason for reason in evaluation["reasons"])
        )

    def test_different_suite_requires_explicit_replace(self):
        first = self.store.pin_competency_suite(self.suite(minimum=0.5))
        again = self.store.pin_competency_suite(self.suite(minimum=0.5))
        self.assertEqual(first["fingerprint"], again["fingerprint"])
        self.assertFalse(again["replaced"])

        with self.assertRaisesRegex(continual.ValidationError, "already pinned"):
            self.store.pin_competency_suite(self.suite(minimum=0.6))

        replaced = self.store.pin_competency_suite(
            self.suite(minimum=0.6), replace=True
        )
        self.assertTrue(replaced["replaced"])
        self.assertNotEqual(first["fingerprint"], replaced["fingerprint"])

    def test_suite_replacement_invalidates_previously_passing_evaluation(self):
        self.store.pin_competency_suite(self.suite(minimum=0.5))
        candidate = self.register(
            "candidate.onnx", b"candidate", 200, parent=self.champion["model_id"]
        )
        evaluation = self.store.record_evaluation(self.report(candidate["model_id"]))
        self.assertTrue(evaluation["passed"])

        self.store.pin_competency_suite(self.suite(minimum=0.6), replace=True)

        with self.assertRaisesRegex(continual.PromotionError, "Promotion policy changed"):
            self.store.promote(candidate["model_id"], evaluation["report_id"])


if __name__ == "__main__":
    unittest.main()
