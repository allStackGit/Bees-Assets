"""Safety-evidence regressions for the continual-learning evaluator.

Run from the Bees Assets root:
    python Training\bees_continual_evaluator_safety_tests.py
"""

from __future__ import annotations

import hashlib
from pathlib import Path
import tempfile
import unittest

from bees_continual_evaluate import (
    EpisodeResult,
    EvaluationError,
    MatchSummary,
    _validate_match_summary,
    evaluate_candidate,
    summarize_results,
)


def episode(
    number: int,
    *,
    candidate_is_bee: bool = True,
    winner_team: int = 0,
    timed_out: bool = False,
    bee_starting_tsv: int = 16,
    bee_final_tsv: int = 8,
    human_starting_tsv: int = 5,
    human_final_tsv: int = 0,
    bee_shots: int = 4,
    bee_hits: int = 2,
    bee_damage: int = 100,
    human_shots: int = 3,
    human_hits: int = 1,
    human_damage: int = 60,
) -> EpisodeResult:
    bee_team = 0 if candidate_is_bee else 1
    human_team = 1 - bee_team
    winning_side = 0 if winner_team < 0 else (3 if winner_team == bee_team else 2)
    return EpisodeResult(
        episode_number=number,
        bee_team_id=bee_team,
        human_team_id=human_team,
        winning_side=winning_side,
        winning_team_id=winner_team,
        timed_out=timed_out,
        duration_seconds=12.5,
        bee_starting_tsv=bee_starting_tsv,
        bee_final_tsv=bee_final_tsv,
        human_starting_tsv=human_starting_tsv,
        human_final_tsv=human_final_tsv,
        bee_shots=bee_shots,
        bee_hits=bee_hits,
        bee_damage=bee_damage,
        human_shots=human_shots,
        human_hits=human_hits,
        human_damage=human_damage,
    )


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
    def __init__(self, root: Path):
        self.root = root
        self.compatibility = FakeCompatibility()
        self.config = {
            "promotion": {
                "min_matches_vs_champion": 2,
                "min_historical_matches_per_opponent": 2,
            }
        }
        self.models = {}
        for model_id, status in (("candidate", "candidate"), ("champion", "champion")):
            path = root / f"{model_id}.onnx"
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


class EvaluatorSafetyEvidenceTests(unittest.TestCase):
    def test_summary_retains_candidate_side_telemetry_across_team_swaps(self):
        first = episode(1, candidate_is_bee=True, winner_team=0)
        second = episode(
            2,
            candidate_is_bee=False,
            winner_team=0,
            bee_starting_tsv=20,
            bee_final_tsv=10,
            human_starting_tsv=7,
            human_final_tsv=4,
            bee_shots=9,
            bee_hits=4,
            bee_damage=200,
            human_shots=6,
            human_hits=3,
            human_damage=90,
        )

        summary = summarize_results([first, second], candidate_team_id=0)

        self.assertTrue(summary.telemetry_validated)
        self.assertEqual(summary.matches, 2)
        self.assertEqual(summary.candidate_starting_tsv, 23)
        self.assertEqual(summary.candidate_final_tsv, 12)
        self.assertEqual(summary.candidate_shots, 10)
        self.assertEqual(summary.candidate_hits, 5)
        self.assertEqual(summary.candidate_damage, 190)
        self.assertEqual(summary.opponent_starting_tsv, 25)
        self.assertEqual(summary.opponent_final_tsv, 10)

    def test_invalid_negative_episode_telemetry_is_rejected(self):
        bad = episode(1, bee_damage=-1)
        with self.assertRaisesRegex(EvaluationError, "bee_damage"):
            summarize_results([bad], candidate_team_id=0)

    def test_zero_starting_tsv_is_rejected(self):
        bad = episode(1, bee_starting_tsv=0)
        with self.assertRaisesRegex(EvaluationError, "starting TSV"):
            summarize_results([bad], candidate_team_id=0)

    def test_timeout_cannot_claim_nonzero_winning_side(self):
        bad = episode(1, winner_team=-1, timed_out=True)
        bad = EpisodeResult(**{**bad.__dict__, "winning_side": 3})
        with self.assertRaisesRegex(EvaluationError, "winning side"):
            summarize_results([bad], candidate_team_id=0)

    def test_match_summary_must_exactly_match_requested_count(self):
        summary = MatchSummary(4, 4, 0, 0, 0, 10.0)
        with self.assertRaisesRegex(EvaluationError, "requested 5"):
            _validate_match_summary(summary, expected_matches=5)

    def test_match_summary_rejects_inconsistent_outcome_totals(self):
        summary = MatchSummary(5, 5, 1, 0, 0, 10.0)
        with self.assertRaisesRegex(EvaluationError, "outcome counts"):
            _validate_match_summary(summary, expected_matches=5)

    def test_fake_summary_without_side_channel_evidence_is_not_authoritative(self):
        summary = MatchSummary(5, 5, 0, 0, 0, 10.0)
        validated = _validate_match_summary(summary, expected_matches=5)
        self.assertFalse(validated.telemetry_validated)

    def test_injected_match_runner_cannot_claim_authoritative_runtime_checks(self):
        with tempfile.TemporaryDirectory() as temp:
            store = FakeStore(Path(temp))

            def fake_runner(**kwargs):
                matches = kwargs["matches"]
                return MatchSummary(matches, matches, 0, 0, 0, 1.0)

            report = evaluate_candidate(
                store,
                candidate_model_id="candidate",
                environment_path="fake.exe",
                match_runner=fake_runner,
            )

        self.assertFalse(report["behavior_sanity_passed"])
        self.assertFalse(report["runtime_compatible"])
        self.assertFalse(report["runtime_checks_passed"])
        self.assertFalse(report["evaluator"]["authoritative_match_runner"])
        self.assertFalse(report["evaluator"]["authoritative_telemetry_validated"])


if __name__ == "__main__":
    unittest.main()
