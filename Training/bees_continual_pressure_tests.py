import tempfile
import unittest
from pathlib import Path

from bees_continual_bootstrap import bootstrap_champion
from bees_continual_historical import (
    AUTHORITATIVE_EVALUATION_TAG,
    _historical_training_weights,
)
from bees_continual_learning import ContinualLearningStore, load_config


class AdaptiveHistoricalPressureTests(unittest.TestCase):
    def make_store(self, root):
        config = load_config()
        config["promotion"] = dict(config["promotion"])
        config["promotion"]["min_matches_vs_champion"] = 1
        config["promotion"]["min_win_rate_vs_champion"] = 0.5
        config["promotion"]["min_competency_cases"] = 0
        store = ContinualLearningStore(root, config=config)
        store.initialize()
        return store

    @staticmethod
    def register(store, root, name, payload, step, parent=None):
        artifact = Path(root) / f"{name}.onnx"
        artifact.write_bytes(payload)
        return store.register_model(
            artifact,
            training_run_id="adaptive-pressure-test",
            training_step=step,
            game_build_version="test-build",
            parent_model_id=parent,
            status="candidate",
        )

    @staticmethod
    def promote_challenger(store, candidate_id, champion_id):
        report = {
            "candidate_model_id": candidate_id,
            "champion_model_id": champion_id,
            "candidate_vs_champion": {"wins": 1, "losses": 0, "draws": 0},
            "historical": [],
            "competencies": [],
            "behavior_sanity_passed": True,
            "runtime_compatible": True,
            "runtime_checks_passed": True,
        }
        evaluation = store.record_evaluation(report)
        if not evaluation["passed"]:
            raise AssertionError(evaluation["reasons"])
        store.promote(candidate_id, evaluation["report_id"])

    def test_failed_candidate_pressure_reaches_training_and_recovers(self):
        with tempfile.TemporaryDirectory() as temp:
            store = self.make_store(temp)
            first = self.register(store, temp, "first", b"first-policy", 100)
            bootstrap_champion(
                store,
                first["model_id"],
                reason="Adaptive-pressure test baseline",
            )

            champion = self.register(
                store,
                temp,
                "champion",
                b"second-policy",
                200,
                parent=first["model_id"],
            )
            self.promote_challenger(store, champion["model_id"], first["model_id"])

            baseline = {
                item["model_id"]: item
                for item in store.historical_sampling_weights(champion["model_id"])
            }
            self.assertEqual(baseline[first["model_id"]]["weight"], 1.0)

            regressed = self.register(
                store,
                temp,
                "regressed",
                b"regressed-policy",
                300,
                parent=champion["model_id"],
            )
            store.record_historical_matchup(
                current_model_id=regressed["model_id"],
                opponent_model_id=first["model_id"],
                current_win_rate=0.20,
                previous_win_rate=0.80,
                match_count=50,
                tags=[AUTHORITATIVE_EVALUATION_TAG, "evaluation:regressed"],
            )

            training_weights = {
                item["model_id"]: item
                for item in _historical_training_weights(store, champion["model_id"])
            }
            exposed = training_weights[first["model_id"]]
            self.assertEqual(exposed["weight"], 4.0)
            self.assertGreater(exposed["regression"], 0.0)
            self.assertEqual(exposed["pressure_model_id"], regressed["model_id"])

            recovered = self.register(
                store,
                temp,
                "recovered",
                b"recovered-policy",
                400,
                parent=regressed["model_id"],
            )

            # A newer but unevaluated snapshot must not silently erase a weakness that
            # was actually observed by the authoritative evaluator.
            still_exposed = {
                item["model_id"]: item
                for item in _historical_training_weights(store, champion["model_id"])
            }
            self.assertEqual(still_exposed[first["model_id"]]["weight"], 4.0)

            store.record_historical_matchup(
                current_model_id=recovered["model_id"],
                opponent_model_id=first["model_id"],
                current_win_rate=0.78,
                previous_win_rate=0.80,
                match_count=50,
                tags=[AUTHORITATIVE_EVALUATION_TAG, "evaluation:recovered"],
            )
            recovered_weights = {
                item["model_id"]: item
                for item in _historical_training_weights(store, champion["model_id"])
            }
            normalized = recovered_weights[first["model_id"]]
            self.assertEqual(normalized["regression"], 0.0)
            self.assertEqual(normalized["weight"], 1.0)
            self.assertEqual(normalized["pressure_model_id"], recovered["model_id"])


if __name__ == "__main__":
    unittest.main()
