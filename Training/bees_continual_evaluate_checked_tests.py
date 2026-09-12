import unittest
from unittest.mock import patch

from bees_continual_evaluate_checked import evaluate_and_record_checked


def candidate_summary(*, wins=1, losses=1, draws=0, timeouts=0, shots=2, hits=1, damage=10):
    matches = wins + losses + draws
    return {
        "matches": matches,
        "wins": wins,
        "losses": losses,
        "draws": draws,
        "timeouts": timeouts,
        "candidate_starting_tsv": matches * 10,
        "candidate_final_tsv": matches * 4,
        "candidate_shots": shots,
        "candidate_hits": hits,
        "candidate_damage": damage,
    }


def evaluation_report(summary):
    return {
        "candidate_model_id": "candidate",
        "champion_model_id": "champion",
        "candidate_vs_champion": summary,
        "historical": [
            {
                "opponent_model_id": "history",
                "matches": summary["matches"],
                "candidate_score_rate": 0.5,
                "baseline_score_rate": 0.5,
                "candidate_summary": dict(summary),
            }
        ],
        "competencies": [],
        "behavior_sanity_passed": True,
        "runtime_compatible": True,
        "runtime_checks_passed": True,
        "evaluator": {
            "authoritative_match_runner": True,
            "authoritative_telemetry_validated": True,
        },
    }


class FakeStore:
    def __init__(self):
        self.recorded = []
        self.league = []

    def record_evaluation(self, report):
        self.recorded.append(report)
        return {
            "report_id": "eval-checked",
            "passed": report["behavior_sanity_passed"] is True,
        }

    def record_historical_matchup(self, **kwargs):
        self.league.append(dict(kwargs))
        return dict(kwargs)


class ContinualEvaluateCheckedTests(unittest.TestCase):
    def test_checked_evaluator_records_derived_behavior_evidence(self):
        store = FakeStore()
        raw = evaluation_report(candidate_summary())
        with patch("bees_continual_evaluate_checked.evaluate_candidate", return_value=raw):
            value = evaluate_and_record_checked(store, candidate_model_id="candidate")
        self.assertTrue(value["recorded"]["passed"])
        self.assertTrue(value["report"]["behavior_sanity_passed"])
        evidence = value["report"]["evaluator"]["behavior_sanity"]
        self.assertTrue(evidence["passed"])
        self.assertEqual(evidence["match_groups"], 2)
        self.assertEqual(len(store.league), 1)
        self.assertEqual(store.league[0]["tags"], [
            "authoritative_candidate_evaluation",
            "evaluation:eval-checked",
        ])

    def test_checked_evaluator_overrides_false_positive_behavior_flag(self):
        store = FakeStore()
        inactive = candidate_summary(
            wins=0,
            losses=2,
            draws=0,
            shots=0,
            hits=0,
            damage=0,
        )
        raw = evaluation_report(inactive)
        self.assertTrue(raw["behavior_sanity_passed"])
        with patch("bees_continual_evaluate_checked.evaluate_candidate", return_value=raw):
            value = evaluate_and_record_checked(store, candidate_model_id="candidate")
        self.assertFalse(value["recorded"]["passed"])
        self.assertFalse(value["report"]["behavior_sanity_passed"])
        self.assertIn(
            "candidate showed no weapon activity, damage, or wins in any evaluated match",
            value["report"]["evaluator"]["behavior_sanity"]["reasons"],
        )


if __name__ == "__main__":
    unittest.main()
