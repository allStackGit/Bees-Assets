"""Promotion-safety regression tests for the continual-learning control plane.

Run from the Bees Assets root:
    python Training\bees_continual_promotion_safety_tests.py
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


class PromotionRaceTests(unittest.TestCase):
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
            training_run_id="promotion-race-test",
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
        bootstrap_champion(
            self.store,
            model["model_id"],
            reason="Promotion-race test baseline",
        )

    def test_champion_change_between_assessment_and_insert_cannot_relabel_report(self):
        first = self.register("first.onnx", b"first", 100)
        self.promote_first(first)

        second = self.register(
            "second.onnx", b"second", 200, parent=first["model_id"]
        )
        third = self.register(
            "third.onnx", b"third", 300, parent=first["model_id"]
        )
        second_evaluation = self.store.record_evaluation(
            self.passing_report(second["model_id"], first["model_id"])
        )

        original_assess = self.store.assess_evaluation
        champion_changed = False

        def assess_then_change_champion(report):
            nonlocal champion_changed
            decision = original_assess(report)
            if not champion_changed:
                champion_changed = True
                self.store.promote(second["model_id"], second_evaluation["report_id"])
            return decision

        self.store.assess_evaluation = assess_then_change_champion
        stale_evaluation = self.store.record_evaluation(
            self.passing_report(third["model_id"], first["model_id"])
        )

        self.assertTrue(stale_evaluation["passed"])
        self.assertEqual(self.store.current_champion_id(), second["model_id"])
        with self.store._connect() as db:
            stored = db.execute(
                "SELECT champion_model_id FROM evaluations WHERE report_id = ?",
                (stale_evaluation["report_id"],),
            ).fetchone()
        self.assertEqual(stored["champion_model_id"], first["model_id"])

        with self.assertRaises(continual.PromotionError):
            self.store.promote(third["model_id"], stale_evaluation["report_id"])

    def test_promotion_rejects_report_payload_and_row_champion_disagreement(self):
        first = self.register("first.onnx", b"first", 100)
        self.promote_first(first)
        second = self.register(
            "second.onnx", b"second", 200, parent=first["model_id"]
        )
        evaluation = self.store.record_evaluation(
            self.passing_report(second["model_id"], first["model_id"])
        )

        with self.store._connect() as db:
            db.execute(
                "UPDATE evaluations SET champion_model_id = NULL WHERE report_id = ?",
                (evaluation["report_id"],),
            )

        with self.assertRaisesRegex(
            continual.PromotionError,
            "Evaluation record champion does not match",
        ):
            self.store.promote(second["model_id"], evaluation["report_id"])


if __name__ == "__main__":
    unittest.main()
