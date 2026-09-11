"""Promotion staleness regressions for the continual-learning control plane.

Run from the Bees Assets root:
    python Training\bees_continual_promotion_staleness_tests.py
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


class PromotionStalenessTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name) / "store"
        self.artifacts = Path(self.temp.name) / "artifacts"
        self.artifacts.mkdir()
        self.store = continual.ContinualLearningStore(self.root, TEST_CONFIG)
        self.store.initialize()

    def tearDown(self):
        self.temp.cleanup()

    def register(self, name: str, content: bytes, step: int, parent=None):
        artifact = self.artifacts / name
        artifact.write_bytes(content)
        return self.store.register_model(
            artifact,
            training_run_id="promotion-staleness-test",
            training_step=step,
            game_build_version="test-build",
            parent_model_id=parent,
            status="candidate",
        )

    @staticmethod
    def report(candidate_id: str, champion_id: str, historical=None):
        return {
            "candidate_model_id": candidate_id,
            "champion_model_id": champion_id,
            "candidate_vs_champion": {"wins": 6, "losses": 4, "draws": 0},
            "historical": list(historical or []),
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

    def bootstrap(self, model):
        bootstrap_champion(
            self.store,
            model["model_id"],
            reason="Promotion staleness test baseline",
        )

    def test_old_report_is_stale_after_champion_round_trip_changes_history(self):
        first = self.register("first.onnx", b"first", 100)
        self.bootstrap(first)
        second = self.register("second.onnx", b"second", 200, parent=first["model_id"])
        third = self.register("third.onnx", b"third", 300, parent=first["model_id"])

        second_eval = self.store.record_evaluation(
            self.report(second["model_id"], first["model_id"])
        )
        third_eval = self.store.record_evaluation(
            self.report(third["model_id"], first["model_id"])
        )
        self.assertTrue(second_eval["passed"])
        self.assertTrue(third_eval["passed"])

        self.store.promote(second["model_id"], second_eval["report_id"])
        self.store.rollback(first["model_id"])
        self.assertEqual(self.store.current_champion_id(), first["model_id"])
        self.assertEqual(self.store.get_model(second["model_id"])["status"], "historical")

        with self.assertRaisesRegex(continual.PromotionError, "Historical league changed"):
            self.store.promote(third["model_id"], third_eval["report_id"])

    def test_old_report_is_stale_when_promotion_policy_changes(self):
        first = self.register("first.onnx", b"first", 100)
        self.bootstrap(first)
        second = self.register("second.onnx", b"second", 200, parent=first["model_id"])

        evaluation = self.store.record_evaluation(
            self.report(second["model_id"], first["model_id"])
        )
        self.assertTrue(evaluation["passed"])

        stricter_config = {
            **TEST_CONFIG,
            "promotion": {
                **TEST_CONFIG["promotion"],
                "min_win_rate_vs_champion": 0.65,
            },
        }
        stricter_store = continual.ContinualLearningStore(self.root, stricter_config)

        with self.assertRaisesRegex(continual.PromotionError, "Promotion policy changed"):
            stricter_store.promote(second["model_id"], evaluation["report_id"])

    def test_historical_result_requires_champion_baseline(self):
        first = self.register("first.onnx", b"first", 100)
        self.bootstrap(first)
        second = self.register("second.onnx", b"second", 200, parent=first["model_id"])
        second_eval = self.store.record_evaluation(
            self.report(second["model_id"], first["model_id"])
        )
        self.store.promote(second["model_id"], second_eval["report_id"])

        third = self.register("third.onnx", b"third", 300, parent=second["model_id"])
        evaluation = self.store.record_evaluation(
            self.report(
                third["model_id"],
                second["model_id"],
                historical=[
                    {
                        "opponent_model_id": first["model_id"],
                        "matches": 10,
                        "candidate_win_rate": 0.75,
                        "critical": False,
                    }
                ],
            )
        )

        self.assertFalse(evaluation["passed"])
        self.assertTrue(
            any("baseline_win_rate is required" in reason for reason in evaluation["reasons"])
        )


if __name__ == "__main__":
    unittest.main()
