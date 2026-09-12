import unittest

from bees_continual_behavior_sanity import assess_behavior_sanity, apply_behavior_sanity
from bees_continual_learning import ValidationError


def summary(
    *,
    matches=4,
    wins=1,
    losses=2,
    draws=1,
    timeouts=0,
    shots=3,
    hits=1,
    damage=10,
):
    return {
        "matches": matches,
        "wins": wins,
        "losses": losses,
        "draws": draws,
        "timeouts": timeouts,
        "candidate_starting_tsv": matches * 10,
        "candidate_final_tsv": matches * 3,
        "candidate_shots": shots,
        "candidate_hits": hits,
        "candidate_damage": damage,
    }


def report(candidate_summary):
    return {
        "candidate_model_id": "candidate",
        "candidate_vs_champion": candidate_summary,
        "historical": [],
        "competencies": [],
        "behavior_sanity_passed": True,
        "evaluator": {
            "authoritative_match_runner": True,
            "authoritative_telemetry_validated": True,
        },
    }


class ContinualBehaviorSanityTests(unittest.TestCase):
    def test_normal_authoritative_activity_passes(self):
        evidence = assess_behavior_sanity(report(summary()))
        self.assertTrue(evidence["passed"])
        self.assertEqual(evidence["totals"]["candidate_shots"], 3)
        self.assertEqual(evidence["match_groups"], 1)

    def test_non_shooting_winning_policy_can_pass(self):
        evidence = assess_behavior_sanity(
            report(summary(wins=2, losses=2, draws=0, shots=0, hits=0, damage=0))
        )
        self.assertTrue(evidence["passed"])
        self.assertTrue(evidence["checks"]["combat_activity_or_success_observed"])

    def test_zero_activity_and_zero_success_fails(self):
        evidence = assess_behavior_sanity(
            report(summary(wins=0, losses=4, draws=0, shots=0, hits=0, damage=0))
        )
        self.assertFalse(evidence["passed"])
        self.assertIn(
            "candidate showed no weapon activity, damage, or wins in any evaluated match",
            evidence["reasons"],
        )

    def test_all_timeouts_fail_even_when_policy_fires(self):
        evidence = assess_behavior_sanity(
            report(
                summary(
                    wins=0,
                    losses=0,
                    draws=4,
                    timeouts=4,
                    shots=20,
                    hits=2,
                    damage=10,
                )
            )
        )
        self.assertFalse(evidence["passed"])
        self.assertIn("every candidate evaluation match timed out", evidence["reasons"])

    def test_non_authoritative_report_fails(self):
        value = report(summary())
        value["evaluator"]["authoritative_match_runner"] = False
        evidence = assess_behavior_sanity(value)
        self.assertFalse(evidence["passed"])

    def test_apply_attaches_evidence_and_derived_boolean(self):
        value = report(summary(wins=0, losses=4, draws=0, shots=0, hits=0, damage=0))
        updated = apply_behavior_sanity(value)
        self.assertFalse(updated["behavior_sanity_passed"])
        self.assertFalse(updated["evaluator"]["behavior_sanity"]["passed"])
        self.assertTrue(value["behavior_sanity_passed"])
        self.assertNotIn("behavior_sanity", value["evaluator"])

    def test_invalid_hit_damage_telemetry_is_rejected(self):
        value = report(summary(hits=1, damage=0))
        with self.assertRaises(ValidationError):
            assess_behavior_sanity(value)


if __name__ == "__main__":
    unittest.main()
