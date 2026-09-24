import unittest
from unittest.mock import patch

from bees_continual_evaluate_checked import evaluate_and_record_checked


class FakeStore:
    pass


class ContinualEvaluateCheckedTests(unittest.TestCase):
    def test_checked_entry_point_delegates_to_canonical_evaluator(self):
        store = FakeStore()
        expected = {
            "report": {
                "candidate_model_id": "candidate",
                "behavior_sanity_passed": True,
                "evaluator": {
                    "behavior_sanity": {
                        "passed": True,
                        "reasons": [],
                    }
                },
            },
            "recorded": {
                "report_id": "eval-canonical",
                "passed": True,
            },
            "league_updates": [],
        }

        with patch(
            "bees_continual_evaluate_checked.evaluate_and_record",
            return_value=expected,
        ) as canonical:
            actual = evaluate_and_record_checked(
                store,
                candidate_model_id="candidate",
                environment_path="fake.exe",
                seed=17,
            )

        self.assertIs(actual, expected)
        canonical.assert_called_once_with(
            store,
            candidate_model_id="candidate",
            environment_path="fake.exe",
            seed=17,
        )

    def test_checked_entry_point_preserves_canonical_failure_evidence(self):
        store = FakeStore()
        expected = {
            "report": {
                "candidate_model_id": "candidate",
                "behavior_sanity_passed": False,
                "evaluator": {
                    "behavior_sanity": {
                        "passed": False,
                        "reasons": ["every candidate evaluation match timed out"],
                    }
                },
            },
            "recorded": {
                "report_id": "eval-canonical-failed",
                "passed": False,
            },
            "league_updates": [],
        }

        with patch(
            "bees_continual_evaluate_checked.evaluate_and_record",
            return_value=expected,
        ):
            actual = evaluate_and_record_checked(store, candidate_model_id="candidate")

        self.assertFalse(actual["recorded"]["passed"])
        self.assertFalse(actual["report"]["behavior_sanity_passed"])
        self.assertEqual(
            actual["report"]["evaluator"]["behavior_sanity"]["reasons"],
            ["every candidate evaluation match timed out"],
        )


if __name__ == "__main__":
    unittest.main()
