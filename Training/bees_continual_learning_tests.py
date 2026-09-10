"""Focused tests for Training/bees_continual_learning.py.

Run from the Bees Assets root:
    python Training\bees_continual_learning_tests.py
"""

from __future__ import annotations

import importlib.util
from pathlib import Path
import random
import sys
import tempfile
import unittest


MODULE_PATH = Path(__file__).with_name("bees_continual_learning.py")
SPEC = importlib.util.spec_from_file_location("bees_continual_learning", MODULE_PATH)
continual = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = continual
assert SPEC.loader is not None
SPEC.loader.exec_module(continual)


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


class StoreTestCase(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name) / "store"
        self.store = continual.ContinualLearningStore(self.root, TEST_CONFIG)
        self.store.initialize()
        self.artifact_dir = Path(self.temp.name) / "artifacts"
        self.artifact_dir.mkdir()

    def tearDown(self):
        self.temp.cleanup()

    def artifact(self, name, content):
        path = self.artifact_dir / name
        path.write_bytes(content)
        return path

    def register(self, name, content, step, parent=None):
        return self.store.register_model(
            self.artifact(name, content),
            training_run_id="test-run",
            training_step=step,
            game_build_version="test-build",
            parent_model_id=parent,
            status="candidate",
        )

    def compatibility_fields(self, model_id):
        compatibility = self.store.compatibility.to_dict()
        return {
            "model_id": model_id,
            **compatibility,
        }

    def passing_report(self, candidate_id, champion_id=None):
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
        report = self.store.record_evaluation(
            self.passing_report(model["model_id"])
        )
        return self.store.promote(model["model_id"], report["report_id"])


class ModelRegistryTests(StoreTestCase):
    def test_registration_is_content_addressed_and_immutable(self):
        first = self.register("first.onnx", b"model-one", 100)
        second = self.store.register_model(
            self.artifact("copy.onnx", b"model-one"),
            training_run_id="test-run",
            training_step=100,
            game_build_version="test-build",
        )

        self.assertEqual(first["model_id"], second["model_id"])
        self.assertTrue(Path(first["artifact_path"]).is_file())
        self.assertTrue(first["model_id"].startswith("bees-rl-v6-"))

    def test_same_bytes_with_conflicting_lineage_are_rejected(self):
        self.register("first.onnx", b"same-bytes", 100)
        with self.assertRaises(continual.ValidationError):
            self.store.register_model(
                self.artifact("second.onnx", b"same-bytes"),
                training_run_id="different-run",
                training_step=100,
                game_build_version="test-build",
            )

    def test_parent_must_be_known_and_compatible(self):
        with self.assertRaises(continual.ContinualLearningError):
            self.register("child.onnx", b"child", 200, parent="missing-model")


class PromotionTests(StoreTestCase):
    def test_candidate_requires_evaluation_before_promotion(self):
        model = self.register("candidate.onnx", b"candidate", 100)
        with self.assertRaises(continual.PromotionError):
            self.store.promote(model["model_id"], "missing-report")

    def test_first_champion_then_challenger_moves_old_champion_to_history(self):
        first = self.register("first.onnx", b"first", 100)
        self.promote_first(first)

        second = self.register("second.onnx", b"second", 200, parent=first["model_id"])
        evaluation = self.store.record_evaluation(
            self.passing_report(second["model_id"], first["model_id"])
        )
        promoted = self.store.promote(second["model_id"], evaluation["report_id"])

        self.assertEqual(self.store.current_champion_id(), second["model_id"])
        self.assertEqual(promoted["status"], "champion")
        self.assertEqual(self.store.get_model(first["model_id"])["status"], "historical")

    def test_candidate_below_champion_threshold_cannot_promote(self):
        first = self.register("first.onnx", b"first", 100)
        self.promote_first(first)
        second = self.register("second.onnx", b"second", 200, parent=first["model_id"])

        report = self.passing_report(second["model_id"], first["model_id"])
        report["candidate_vs_champion"] = {"wins": 5, "losses": 5, "draws": 0}
        evaluation = self.store.record_evaluation(report)

        self.assertFalse(evaluation["passed"])
        with self.assertRaises(continual.PromotionError):
            self.store.promote(second["model_id"], evaluation["report_id"])

    def test_evaluation_becomes_stale_when_champion_changes(self):
        first = self.register("first.onnx", b"first", 100)
        self.promote_first(first)

        second = self.register("second.onnx", b"second", 200, parent=first["model_id"])
        third = self.register("third.onnx", b"third", 300, parent=first["model_id"])

        second_eval = self.store.record_evaluation(
            self.passing_report(second["model_id"], first["model_id"])
        )
        third_eval = self.store.record_evaluation(
            self.passing_report(third["model_id"], first["model_id"])
        )
        self.store.promote(second["model_id"], second_eval["report_id"])

        with self.assertRaises(continual.PromotionError):
            self.store.promote(third["model_id"], third_eval["report_id"])

    def test_rollback_restores_previous_champion_without_retraining(self):
        first = self.register("first.onnx", b"first", 100)
        self.promote_first(first)
        second = self.register("second.onnx", b"second", 200, parent=first["model_id"])
        second_eval = self.store.record_evaluation(
            self.passing_report(second["model_id"], first["model_id"])
        )
        self.store.promote(second["model_id"], second_eval["report_id"])

        rolled_back = self.store.rollback()

        self.assertEqual(rolled_back["model_id"], first["model_id"])
        self.assertEqual(self.store.current_champion_id(), first["model_id"])
        self.assertEqual(self.store.get_model(second["model_id"])["status"], "historical")


class HistoricalLeagueTests(StoreTestCase):
    def setUp(self):
        super().setUp()
        self.first = self.register("first.onnx", b"first", 100)
        self.promote_first(self.first)
        self.second = self.register("second.onnx", b"second", 200, parent=self.first["model_id"])
        second_eval = self.store.record_evaluation(
            self.passing_report(self.second["model_id"], self.first["model_id"])
        )
        self.store.promote(self.second["model_id"], second_eval["report_id"])
        self.current = self.register("current.onnx", b"current", 300, parent=self.second["model_id"])

    def test_regression_increases_weight_but_is_capped(self):
        self.store.record_historical_matchup(
            current_model_id=self.current["model_id"],
            opponent_model_id=self.first["model_id"],
            current_win_rate=0.20,
            previous_win_rate=0.80,
            match_count=20,
            tags=["old-counter"],
        )
        self.store.record_historical_matchup(
            current_model_id=self.current["model_id"],
            opponent_model_id=self.second["model_id"],
            current_win_rate=0.65,
            previous_win_rate=0.68,
            match_count=20,
        )

        weights = {
            item["model_id"]: item
            for item in self.store.historical_sampling_weights(self.current["model_id"])
        }

        self.assertGreater(weights[self.first["model_id"]]["weight"], weights[self.second["model_id"]]["weight"])
        self.assertLessEqual(weights[self.first["model_id"]]["weight"], 4.0)
        self.assertEqual(weights[self.first["model_id"]]["tags"], ["old-counter"])

    def test_regression_weight_returns_to_baseline_after_recovery(self):
        self.store.record_historical_matchup(
            current_model_id=self.current["model_id"],
            opponent_model_id=self.first["model_id"],
            current_win_rate=0.20,
            previous_win_rate=0.80,
            match_count=20,
        )
        regressed = {
            item["model_id"]: item
            for item in self.store.historical_sampling_weights(self.current["model_id"])
        }
        self.assertGreater(regressed[self.first["model_id"]]["weight"], 1.0)

        self.store.record_historical_matchup(
            current_model_id=self.current["model_id"],
            opponent_model_id=self.first["model_id"],
            current_win_rate=0.78,
            previous_win_rate=0.80,
            match_count=20,
        )
        recovered = {
            item["model_id"]: item
            for item in self.store.historical_sampling_weights(self.current["model_id"])
        }

        self.assertEqual(recovered[self.first["model_id"]]["regression"], 0.0)
        self.assertEqual(recovered[self.first["model_id"]]["weight"], 1.0)

    def test_weighted_sampler_can_select_from_history(self):
        selection = self.store.sample_historical_opponent(
            self.current["model_id"], rng=random.Random(1)
        )
        self.assertIn(selection["model_id"], {self.first["model_id"], self.second["model_id"]})


class EvaluationTests(StoreTestCase):
    def test_historical_regression_and_critical_competency_block_promotion(self):
        first = self.register("first.onnx", b"first", 100)
        self.promote_first(first)
        second = self.register("second.onnx", b"second", 200, parent=first["model_id"])

        report = self.passing_report(second["model_id"], first["model_id"])
        report["historical"] = [
            {
                "model_id": first["model_id"],
                "candidate_win_rate": 0.40,
                "baseline_win_rate": 0.70,
                "matches": 10,
                "critical": True,
            }
        ]
        report["competencies"] = [
            {
                "name": "large-map-aiming",
                "score": 0.4,
                "minimum": 0.6,
                "critical": True,
            }
        ]
        evaluation = self.store.record_evaluation(report)

        self.assertFalse(evaluation["passed"])
        self.assertTrue(any("regressed" in reason for reason in evaluation["reasons"]))
        self.assertTrue(any("large-map-aiming" in reason for reason in evaluation["reasons"]))


class IngestionTests(StoreTestCase):
    def setUp(self):
        super().setUp()
        self.model = self.register("model.onnx", b"model", 100)
        self.promote_first(self.model)

    def telemetry(self, match_id="match-1"):
        return {
            "match_id": match_id,
            "game_build_version": "test-build",
            "mode": "campaign",
            "result": "bee_win",
            **self.compatibility_fields(self.model["model_id"]),
            "steps": [
                {"observation": [0.0, 1.0], "ai_action": [0.25, -0.5]}
            ],
        }

    def demonstration(self, demo_id="demo-1"):
        return {
            "demonstration_id": demo_id,
            "game_build_version": "test-build",
            **self.compatibility_fields(self.model["model_id"]),
            "examples": [
                {"observation": [0.0, 1.0], "action": [0.2, 0.3]}
            ],
        }

    def test_live_telemetry_is_archived_but_never_marked_on_policy(self):
        result = self.store.ingest_telemetry(self.telemetry())
        self.assertFalse(result["trusted_for_on_policy_rl"])
        self.assertTrue(Path(result["archive_path"]).is_file())

    def test_exact_duplicate_telemetry_is_idempotent(self):
        first = self.store.ingest_telemetry(self.telemetry())
        second = self.store.ingest_telemetry(self.telemetry())
        self.assertFalse(first["duplicate"])
        self.assertTrue(second["duplicate"])
        self.assertEqual(first["batch_id"], second["batch_id"])

    def test_schema_mismatch_is_rejected(self):
        payload = self.telemetry()
        payload["action_schema_version"] = 999
        with self.assertRaises(continual.CompatibilityError):
            self.store.ingest_telemetry(payload)

    def test_human_demonstrations_are_stored_in_separate_archive(self):
        result = self.store.ingest_demonstration(self.demonstration())
        self.assertEqual(result["example_count"], 1)
        self.assertIn("human-demos", result["archive_path"])
        self.assertEqual(self.store.status()["telemetry_batches"], 0)
        self.assertEqual(self.store.status()["demonstration_batches"], 1)

    def test_non_finite_values_are_rejected(self):
        payload = self.demonstration("bad-demo")
        payload["examples"][0]["action"] = [float("nan")]
        with self.assertRaises(continual.ValidationError):
            self.store.ingest_demonstration(payload)


if __name__ == "__main__":
    unittest.main()
