import json
import struct
import tempfile
import unittest
from pathlib import Path

import numpy as np

from bees_continual_evaluate import (
    EVALUATION_PROTOCOL_VERSION,
    EpisodeResult,
    EvaluationError,
    MatchSummary,
    behavior_team_id,
    build_allow_action_mask,
    competency_score,
    evaluate_candidate,
    load_competency_suite,
    parse_episode_message,
    summarize_results,
)


class BufferMessage:
    def __init__(self, data):
        self.data = data
        self.offset = 0

    def read_int32(self):
        value = struct.unpack_from("<i", self.data, self.offset)[0]
        self.offset += 4
        return value

    def read_bool(self):
        value = struct.unpack_from("<?", self.data, self.offset)[0]
        self.offset += 1
        return value

    def read_float32(self):
        value = struct.unpack_from("<f", self.data, self.offset)[0]
        self.offset += 4
        return value


def encoded_result(*, version=EVALUATION_PROTOCOL_VERSION, winner_team=0, timed_out=False):
    ints_before_bool = [version, 7, 0, 1, 3, winner_team]
    ints_after_float = [16, 0, 5, 0, 4, 2, 100, 3, 1, 60]
    payload = b"".join(struct.pack("<i", value) for value in ints_before_bool)
    payload += struct.pack("<?", timed_out)
    payload += struct.pack("<f", 12.5)
    payload += b"".join(struct.pack("<i", value) for value in ints_after_float)
    return payload


def result(episode, winner, *, timeout=False):
    return EpisodeResult(
        episode_number=episode,
        bee_team_id=0 if episode % 2 else 1,
        human_team_id=1 if episode % 2 else 0,
        winning_side=1 if winner >= 0 else 0,
        winning_team_id=winner,
        timed_out=timeout,
        duration_seconds=10.0,
        bee_starting_tsv=16,
        bee_final_tsv=0,
        human_starting_tsv=5,
        human_final_tsv=0,
        bee_shots=0,
        bee_hits=0,
        bee_damage=0,
        human_shots=0,
        human_hits=0,
        human_damage=0,
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
            ("history", "historical"),
        ):
            path = self.root / f"{model_id}.onnx"
            path.write_bytes(model_id.encode("ascii"))
            self.models[model_id] = {
                "model_id": model_id,
                "status": status,
                "artifact_path": str(path),
                **self.compatibility.to_dict(),
                "metadata": {"critical_regression": model_id == "history"},
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


class ContinualEvaluateTests(unittest.TestCase):
    def test_result_channel_protocol_is_decoded_exactly(self):
        value = parse_episode_message(BufferMessage(encoded_result()))
        self.assertEqual(value.episode_number, 7)
        self.assertEqual(value.winning_team_id, 0)
        self.assertEqual(value.bee_starting_tsv, 16)
        self.assertEqual(value.human_damage, 60)
        self.assertAlmostEqual(value.duration_seconds, 12.5)

    def test_result_channel_rejects_protocol_mismatch(self):
        with self.assertRaises(EvaluationError):
            parse_episode_message(BufferMessage(encoded_result(version=99)))

    def test_timeout_is_scored_as_draw_and_retained_as_timeout(self):
        summary = summarize_results(
            [result(1, 0), result(2, 1), result(3, -1, timeout=True)],
            candidate_team_id=0,
        )
        self.assertEqual(
            (summary.wins, summary.losses, summary.draws, summary.timeouts),
            (1, 1, 1, 1),
        )
        self.assertAlmostEqual(summary.score_rate, 0.5)

    def test_duplicate_episode_is_rejected(self):
        with self.assertRaises(EvaluationError):
            summarize_results([result(1, 0), result(1, 1)], candidate_team_id=0)

    def test_behavior_team_parser_requires_explicit_team(self):
        self.assertEqual(behavior_team_id("BeesRL1v1?team=1"), 1)
        with self.assertRaises(EvaluationError):
            behavior_team_id("BeesRL1v1")

    def test_action_mask_is_inverted_for_onnx_allow_semantics(self):
        class Decisions:
            action_mask = [
                np.array([[False, True], [True, False]], dtype=np.bool_),
                np.array([[False, False, True], [True, True, False]], dtype=np.bool_),
            ]

            def __len__(self):
                return 2

        value = build_allow_action_mask(Decisions(), [2, 3])
        np.testing.assert_array_equal(
            value,
            np.array(
                [[1, 0, 1, 1, 0], [0, 1, 0, 0, 1]],
                dtype=np.float32,
            ),
        )

    def test_competency_suite_validates_metric_and_defaults(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "suite.json"
            path.write_text(
                json.dumps(
                    {
                        "schema_version": 1,
                        "cases": [
                            {
                                "name": "range-control",
                                "opponent_model_id": "history",
                                "minimum": 0.6,
                                "metric": "non_timeout_rate",
                                "env_args": ["--rl-map-size=128"],
                            }
                        ],
                    }
                ),
                encoding="utf-8",
            )
            cases = load_competency_suite(path, default_matches=11)
            self.assertEqual(cases[0].matches, 11)
            self.assertEqual(cases[0].env_args, ("--rl-map-size=128",))
            self.assertEqual(
                competency_score(
                    MatchSummary(10, 0, 0, 10, 2, 1.0), "non_timeout_rate"
                ),
                0.8,
            )

    def test_evaluate_candidate_builds_champion_historical_and_competency_report(self):
        with tempfile.TemporaryDirectory() as temp:
            store = FakeStore(temp)
            suite = Path(temp) / "suite.json"
            suite.write_text(
                json.dumps(
                    {
                        "schema_version": 1,
                        "cases": [
                            {
                                "name": "permanent-history-check",
                                "opponent_model_id": "history",
                                "matches": 4,
                                "minimum": 0.5,
                                "critical": True,
                                "env_args": ["--rl-health-ratio=1"],
                            }
                        ],
                    }
                ),
                encoding="utf-8",
            )
            calls = []

            def fake_runner(**kwargs):
                calls.append(kwargs)
                candidate_name = Path(kwargs["candidate_model_path"]).stem
                opponent_name = Path(kwargs["opponent_model_path"]).stem
                if candidate_name == "champion" and opponent_name == "history":
                    return MatchSummary(kwargs["matches"], 2, 1, 0, 0, 30.0)
                return MatchSummary(
                    kwargs["matches"], kwargs["matches"], 0, 0, 0, 20.0
                )

            report = evaluate_candidate(
                store,
                candidate_model_id="candidate",
                environment_path="fake.exe",
                competency_suite=suite,
                match_runner=fake_runner,
            )
            self.assertEqual(report["candidate_vs_champion"]["matches"], 5)
            self.assertEqual(len(report["historical"]), 1)
            self.assertAlmostEqual(report["historical"][0]["baseline_win_rate"], 2 / 3)
            self.assertTrue(report["historical"][0]["critical"])
            self.assertEqual(
                report["competencies"][0]["name"], "permanent-history-check"
            )
            self.assertEqual(report["competencies"][0]["score"], 1.0)
            self.assertEqual(len(calls), 4)
            self.assertEqual(calls[-1]["env_args"][-1], "--rl-health-ratio=1")


if __name__ == "__main__":
    unittest.main()
