import json
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

    @staticmethod
    def record_authoritative_pressure(
        store,
        *,
        candidate_id,
        champion_id,
        opponent_id,
        candidate_score_rate,
        baseline_score_rate,
        label,
    ):
        report = {
            "candidate_model_id": candidate_id,
            "champion_model_id": champion_id,
            "candidate_vs_champion": {"wins": 1, "losses": 0, "draws": 0},
            "historical": [
                {
                    "opponent_model_id": opponent_id,
                    "matches": 50,
                    "candidate_win_rate": candidate_score_rate,
                    "baseline_win_rate": baseline_score_rate,
                    "candidate_score_rate": candidate_score_rate,
                    "baseline_score_rate": baseline_score_rate,
                    "critical": False,
                }
            ],
            "competencies": [],
            "behavior_sanity_passed": True,
            "runtime_compatible": True,
            "runtime_checks_passed": True,
        }
        recorded = store.record_evaluation(report)
        store.record_historical_matchup(
            current_model_id=candidate_id,
            opponent_model_id=opponent_id,
            current_win_rate=candidate_score_rate,
            previous_win_rate=baseline_score_rate,
            match_count=50,
            tags=[
                AUTHORITATIVE_EVALUATION_TAG,
                f"evaluation:{recorded['report_id']}",
                f"test:{label}",
            ],
        )
        return recorded["report_id"]

    @staticmethod
    def rewrite_policy_schema(store, report_id, schema_version):
        with store._connect() as db:
            row = db.execute(
                "SELECT report_json FROM evaluations WHERE report_id = ?",
                (report_id,),
            ).fetchone()
            report = json.loads(row["report_json"])
            report["promotion_policy"]["schema_version"] = schema_version
            report["promotion_policy_fingerprint"] = store._promotion_policy_fingerprint(
                report["promotion_policy"]
            )
            db.execute(
                "UPDATE evaluations SET report_json = ? WHERE report_id = ?",
                (json.dumps(report, sort_keys=True, separators=(",", ":")), report_id),
            )

    def establish_history(self, store, temp):
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
        return first, champion

    def test_failed_candidate_pressure_reaches_training_and_recovers(self):
        with tempfile.TemporaryDirectory() as temp:
            store = self.make_store(temp)
            first, champion = self.establish_history(store, temp)

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
            regressed_report_id = self.record_authoritative_pressure(
                store,
                candidate_id=regressed["model_id"],
                champion_id=champion["model_id"],
                opponent_id=first["model_id"],
                candidate_score_rate=0.20,
                baseline_score_rate=0.80,
                label="regressed",
            )

            candidate_weights = {
                item["model_id"]: item
                for item in store.historical_sampling_weights(regressed["model_id"])
            }
            direct = candidate_weights[first["model_id"]]
            self.assertEqual(direct["weight"], 4.0)
            self.assertTrue(direct["pressure_provenance_valid"])

            training_weights = {
                item["model_id"]: item
                for item in _historical_training_weights(store, champion["model_id"])
            }
            exposed = training_weights[first["model_id"]]
            self.assertEqual(exposed["weight"], 4.0)
            self.assertGreater(exposed["regression"], 0.0)
            self.assertEqual(exposed["pressure_model_id"], regressed["model_id"])
            self.assertEqual(
                exposed["pressure_evaluation_report_id"], regressed_report_id
            )

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

            recovered_report_id = self.record_authoritative_pressure(
                store,
                candidate_id=recovered["model_id"],
                champion_id=champion["model_id"],
                opponent_id=first["model_id"],
                candidate_score_rate=0.78,
                baseline_score_rate=0.80,
                label="recovered",
            )
            recovered_weights = {
                item["model_id"]: item
                for item in _historical_training_weights(store, champion["model_id"])
            }
            normalized = recovered_weights[first["model_id"]]
            self.assertEqual(normalized["regression"], 0.0)
            self.assertEqual(normalized["weight"], 1.0)
            self.assertEqual(normalized["pressure_model_id"], recovered["model_id"])
            self.assertEqual(
                normalized["pressure_evaluation_report_id"], recovered_report_id
            )

    def test_pre_draw_aware_pressure_cannot_bias_sampling(self):
        with tempfile.TemporaryDirectory() as temp:
            store = self.make_store(temp)
            first, champion = self.establish_history(store, temp)
            stale = self.register(
                store,
                temp,
                "stale",
                b"stale-policy",
                300,
                parent=champion["model_id"],
            )
            report_id = self.record_authoritative_pressure(
                store,
                candidate_id=stale["model_id"],
                champion_id=champion["model_id"],
                opponent_id=first["model_id"],
                candidate_score_rate=0.20,
                baseline_score_rate=0.80,
                label="legacy",
            )

            # Simulate a genuine pre-schema-3 report by changing the recorded policy
            # schema and recomputing its policy fingerprint. The historical row stays
            # in the audit database but must become inert everywhere it can be sampled.
            self.rewrite_policy_schema(store, report_id, 2)

            registry_weights = {
                item["model_id"]: item
                for item in store.historical_sampling_weights(stale["model_id"])
            }
            registry_entry = registry_weights[first["model_id"]]
            self.assertEqual(registry_entry["weight"], 1.0)
            self.assertEqual(registry_entry["regression"], 0.0)
            self.assertFalse(registry_entry["pressure_provenance_valid"])

            training_weights = {
                item["model_id"]: item
                for item in _historical_training_weights(store, champion["model_id"])
            }
            sanitized = training_weights[first["model_id"]]
            self.assertEqual(sanitized["weight"], 1.0)
            self.assertEqual(sanitized["regression"], 0.0)
            self.assertNotIn("pressure_model_id", sanitized)
            self.assertNotIn("pressure_evaluation_report_id", sanitized)

    def test_newer_candidate_evidence_wins_even_if_older_evaluation_finishes_later(self):
        with tempfile.TemporaryDirectory() as temp:
            store = self.make_store(temp)
            first, champion = self.establish_history(store, temp)

            older = self.register(
                store,
                temp,
                "older-candidate",
                b"older-candidate-policy",
                300,
                parent=champion["model_id"],
            )
            newer = self.register(
                store,
                temp,
                "newer-candidate",
                b"newer-candidate-policy",
                400,
                parent=older["model_id"],
            )

            newer_report_id = self.record_authoritative_pressure(
                store,
                candidate_id=newer["model_id"],
                champion_id=champion["model_id"],
                opponent_id=first["model_id"],
                candidate_score_rate=0.78,
                baseline_score_rate=0.80,
                label="newer-recovered-first",
            )
            # Simulate a slow evaluation of the older policy completing afterward.
            self.record_authoritative_pressure(
                store,
                candidate_id=older["model_id"],
                champion_id=champion["model_id"],
                opponent_id=first["model_id"],
                candidate_score_rate=0.20,
                baseline_score_rate=0.80,
                label="older-regressed-late",
            )

            weights = {
                item["model_id"]: item
                for item in _historical_training_weights(store, champion["model_id"])
            }
            recovered = weights[first["model_id"]]
            self.assertEqual(recovered["weight"], 1.0)
            self.assertEqual(recovered["regression"], 0.0)
            self.assertEqual(recovered["pressure_model_id"], newer["model_id"])
            self.assertEqual(
                recovered["pressure_evaluation_report_id"], newer_report_id
            )

    def test_newer_invalid_pressure_does_not_hide_older_valid_weakness(self):
        with tempfile.TemporaryDirectory() as temp:
            store = self.make_store(temp)
            first, champion = self.establish_history(store, temp)

            valid = self.register(
                store,
                temp,
                "valid-regression",
                b"valid-regression-policy",
                300,
                parent=champion["model_id"],
            )
            valid_report_id = self.record_authoritative_pressure(
                store,
                candidate_id=valid["model_id"],
                champion_id=champion["model_id"],
                opponent_id=first["model_id"],
                candidate_score_rate=0.20,
                baseline_score_rate=0.80,
                label="valid-regression",
            )

            newer_invalid = self.register(
                store,
                temp,
                "newer-invalid",
                b"newer-invalid-policy",
                400,
                parent=valid["model_id"],
            )
            invalid_report_id = self.record_authoritative_pressure(
                store,
                candidate_id=newer_invalid["model_id"],
                champion_id=champion["model_id"],
                opponent_id=first["model_id"],
                candidate_score_rate=0.78,
                baseline_score_rate=0.80,
                label="newer-invalid",
            )
            with store._connect() as db:
                row = db.execute(
                    "SELECT report_json FROM evaluations WHERE report_id = ?",
                    (invalid_report_id,),
                ).fetchone()
                report = json.loads(row["report_json"])
                report["promotion_policy_fingerprint"] = "invalid-fingerprint"
                db.execute(
                    "UPDATE evaluations SET report_json = ? WHERE report_id = ?",
                    (json.dumps(report, sort_keys=True, separators=(",", ":")), invalid_report_id),
                )

            training_weights = {
                item["model_id"]: item
                for item in _historical_training_weights(store, champion["model_id"])
            }
            exposed = training_weights[first["model_id"]]
            self.assertEqual(exposed["weight"], 4.0)
            self.assertEqual(exposed["pressure_model_id"], valid["model_id"])
            self.assertEqual(
                exposed["pressure_evaluation_report_id"], valid_report_id
            )


if __name__ == "__main__":
    unittest.main()
