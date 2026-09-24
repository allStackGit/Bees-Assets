import hashlib
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

import bees_continual_evaluate as evaluator
from bees_continual_evaluate import MatchSummary, evaluate_candidate


class FakeCompatibility:
    behavior_name = "BeesRL1v1"

    def to_dict(self):
        return {
            "behavior_name": "BeesRL1v1",
            "policy_abi_version": 6,
            "observation_schema_version": 6,
            "action_schema_version": 6,
            "reward_schema_version": 1,
            "scenario_schema_version": 1,
        }


class FakeStore:
    def __init__(self, root):
        self.root = Path(root)
        self.compatibility = FakeCompatibility()
        self.config = {
            "promotion": {
                "min_matches_vs_champion": 3,
                "min_historical_matches_per_opponent": 2,
                "max_inference_batch_milliseconds": 50.0,
            }
        }
        self.models = {}
        for model_id, status in (("candidate", "candidate"), ("champion", "champion")):
            path = self.root / f"{model_id}.onnx"
            path.write_bytes(model_id.encode("ascii"))
            self.models[model_id] = {
                "model_id": model_id,
                "status": status,
                "artifact_path": str(path),
                "artifact_sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
                **self.compatibility.to_dict(),
                "metadata": {},
            }

    def initialize(self):
        pass

    def get_model(self, model_id):
        return dict(self.models[model_id])

    def list_models(self, status=None):
        values = [dict(value) for value in self.models.values()]
        return [value for value in values if status is None or value["status"] == status]

    def current_champion_id(self):
        return "champion"


def authoritative_summary(matches, *, wins=0, timeouts=0):
    draws = timeouts
    losses = matches - wins - draws
    return MatchSummary(
        matches=matches,
        wins=wins,
        losses=losses,
        draws=draws,
        timeouts=timeouts,
        total_duration_seconds=float(matches),
        candidate_starting_tsv=16 * matches,
        candidate_final_tsv=0,
        opponent_starting_tsv=16 * matches,
        opponent_final_tsv=0,
        candidate_inference_calls=1,
        candidate_inference_total_seconds=0.001,
        candidate_inference_max_seconds=0.001,
        opponent_inference_calls=1,
        opponent_inference_total_seconds=0.001,
        opponent_inference_max_seconds=0.001,
        telemetry_validated=True,
    )


class ContinualEvaluateBehaviorIntegrationTests(unittest.TestCase):
    def _evaluate(self, fake_runner):
        with tempfile.TemporaryDirectory() as temp:
            store = FakeStore(temp)
            with patch.object(evaluator, "run_match_group", side_effect=fake_runner) as runner:
                return evaluate_candidate(
                    store,
                    candidate_model_id="candidate",
                    environment_path="fake.exe",
                    match_runner=runner,
                )

    def test_authoritative_all_timeout_policy_fails_behavior_gate(self):
        def fake_runner(**kwargs):
            return authoritative_summary(
                kwargs["matches"],
                timeouts=kwargs["matches"],
            )

        report = self._evaluate(fake_runner)

        self.assertFalse(report["behavior_sanity_passed"])
        evidence = report["evaluator"]["behavior_sanity"]
        self.assertFalse(evidence["passed"])
        self.assertIn("every candidate evaluation match timed out", evidence["reasons"])
        self.assertIn(
            "candidate showed no weapon activity, damage, or wins in any evaluated match",
            evidence["reasons"],
        )

    def test_authoritative_nonshooting_winner_can_pass_behavior_gate(self):
        def fake_runner(**kwargs):
            return authoritative_summary(
                kwargs["matches"],
                wins=kwargs["matches"],
            )

        report = self._evaluate(fake_runner)

        self.assertTrue(report["behavior_sanity_passed"])
        evidence = report["evaluator"]["behavior_sanity"]
        self.assertTrue(evidence["passed"])
        self.assertEqual(evidence["reasons"], [])
        self.assertTrue(evidence["checks"]["combat_activity_or_success_observed"])


if __name__ == "__main__":
    unittest.main()
