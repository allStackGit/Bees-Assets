"""Compatibility-boundary tests for the continual-learning evaluator.

Run from the Bees Assets root:
    python Training\bees_continual_evaluate_compatibility_tests.py
"""

from __future__ import annotations

import hashlib
from pathlib import Path
import tempfile
import unittest

from bees_continual_evaluate import MatchSummary, evaluate_candidate
from bees_continual_learning import ValidationError


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
                "min_matches_vs_champion": 5,
                "min_historical_matches_per_opponent": 3,
            }
        }
        self.models = {}
        for model_id, status in (
            ("candidate", "candidate"),
            ("champion", "champion"),
            ("old-history", "historical"),
        ):
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
        self.models["old-history"]["reward_schema_version"] = 0

    def initialize(self):
        pass

    def get_model(self, model_id):
        return dict(self.models[model_id])

    def list_models(self, status=None):
        values = [dict(value) for value in self.models.values()]
        return [value for value in values if status is None or value["status"] == status]

    def current_champion_id(self):
        return "champion"


class EvaluatorCompatibilityTests(unittest.TestCase):
    def test_incompatible_historical_models_are_ignored(self):
        with tempfile.TemporaryDirectory() as temp:
            store = FakeStore(temp)
            calls = []

            def fake_runner(**kwargs):
                calls.append(kwargs)
                return MatchSummary(
                    kwargs["matches"], kwargs["matches"], 0, 0, 0, 1.0
                )

            report = evaluate_candidate(
                store,
                candidate_model_id="candidate",
                environment_path="fake.exe",
                match_runner=fake_runner,
            )

            self.assertEqual(report["historical"], [])
            self.assertEqual(len(calls), 1)
            self.assertEqual(Path(calls[0]["opponent_model_path"]).stem, "champion")

    def test_incompatible_requested_historical_model_is_rejected(self):
        with tempfile.TemporaryDirectory() as temp:
            store = FakeStore(temp)

            with self.assertRaisesRegex(ValidationError, "compatible historical"):
                evaluate_candidate(
                    store,
                    candidate_model_id="candidate",
                    environment_path="fake.exe",
                    historical_model_ids=["old-history"],
                    match_runner=lambda **kwargs: MatchSummary(
                        kwargs["matches"], kwargs["matches"], 0, 0, 0, 1.0
                    ),
                )


if __name__ == "__main__":
    unittest.main()
