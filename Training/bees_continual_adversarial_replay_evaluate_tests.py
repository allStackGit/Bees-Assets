"""Focused tests for paired diagnostic evaluation of scripted adversarial replays."""

from __future__ import annotations

import importlib.util
from pathlib import Path
from types import SimpleNamespace
import sys
import unittest
from unittest.mock import patch


TRAINING_DIR = Path(__file__).parent


def _load(name: str):
    path = TRAINING_DIR / f"{name}.py"
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    assert spec.loader is not None
    spec.loader.exec_module(module)
    return module


continual = _load("bees_continual_learning")
train = _load("bees_continual_train")
native = _load("bees_continual_native_demo")
contributors = _load("bees_continual_demo_contributors")
curation = _load("bees_continual_demo_curation")
adversarial = _load("bees_continual_adversarial")
suggest = _load("bees_continual_adversarial_suggest")
mine = _load("bees_continual_adversarial_mine")
replay = _load("bees_continual_adversarial_replay")
evaluate = _load("bees_continual_evaluate")
adversarial_evaluate = _load("bees_continual_adversarial_evaluate")


SCENARIO_ID = "adv-" + "a" * 24


class FakeStore:
    def __init__(self):
        self.compatibility = SimpleNamespace(
            behavior_name="BeesRL1v1",
            policy_abi_version=7,
        )

    def initialize(self):
        return None


class AdversarialReplayEvaluationTests(unittest.TestCase):
    @staticmethod
    def summary(wins):
        matches = 4
        return evaluate.MatchSummary(
            matches=matches,
            wins=wins,
            losses=matches - wins,
            draws=0,
            timeouts=0,
            total_duration_seconds=4.0,
        )

    def test_manual_replay_catalog_cannot_override_registry(self):
        with self.assertRaises(continual.ValidationError):
            adversarial_evaluate._validate_base_env_args(
                ["--bees-adversarial-replay-catalog=manual.json"]
            )

    def test_replay_case_loads_evaluated_model_into_both_physical_team_slots(self):
        store = FakeStore()
        calls = []

        def runner(**kwargs):
            calls.append(kwargs)
            return self.summary(3 if "candidate" in str(kwargs["candidate_model_path"]) else 2)

        scenario = {
            "identity": {
                "bee_composition": ["Wasp"],
                "human_composition": ["Gunship"],
                "target_fraction": 0.1,
            }
        }
        replay_metadata = {
            "replay_id": "advreplay-" + "b" * 24,
            "side": "Human",
            "replay_sha256": "c" * 64,
            "frame_count": 100,
            "fixed_step_interval": 5,
            "catalog_sha256": "d" * 64,
        }

        with patch.object(adversarial_evaluate, "_safe_model", side_effect=lambda s, model_id: {"model_id": model_id}), patch.object(
            adversarial_evaluate,
            "_model_path",
            side_effect=lambda s, model_id: Path(model_id + ".onnx"),
        ), patch.object(adversarial_evaluate, "_read_scenario", return_value=scenario), patch.object(
            adversarial_evaluate, "_validate_registered_sources"
        ), patch.object(
            adversarial_evaluate,
            "_replay_evaluation_setup",
            return_value=(
                replay_metadata,
                ("--bees-adversarial-replay-catalog=/store/catalog.json",),
            ),
        ), patch.object(
            adversarial_evaluate,
            "_write_report",
            side_effect=lambda s, report: report,
        ):
            report = adversarial_evaluate.evaluate_adversarial_scenarios(
                store,
                candidate_model_id="candidate",
                baseline_model_id="baseline",
                opponent_model_id="opponent",
                environment_path="env.exe",
                scenario_ids=[SCENARIO_ID],
                matches_per_scenario=4,
                match_runner=runner,
            )

        self.assertEqual(len(calls), 2)
        self.assertEqual(calls[0]["candidate_model_path"], Path("candidate.onnx"))
        self.assertEqual(calls[0]["opponent_model_path"], Path("candidate.onnx"))
        self.assertEqual(calls[1]["candidate_model_path"], Path("baseline.onnx"))
        self.assertEqual(calls[1]["opponent_model_path"], Path("baseline.onnx"))
        self.assertIn("--bees-adversarial-replay-catalog=/store/catalog.json", calls[0]["env_args"])
        result = report["scenarios"][0]
        self.assertEqual(result["replay"], replay_metadata)
        self.assertFalse(result["model_opponent_used"])

    def test_non_replay_case_preserves_named_model_opponent_contract(self):
        store = FakeStore()
        calls = []

        def runner(**kwargs):
            calls.append(kwargs)
            return self.summary(2)

        scenario = {
            "identity": {
                "bee_composition": ["Wasp"],
                "human_composition": ["Gunship"],
                "target_fraction": 0.1,
            }
        }
        with patch.object(adversarial_evaluate, "_safe_model", side_effect=lambda s, model_id: {"model_id": model_id}), patch.object(
            adversarial_evaluate,
            "_model_path",
            side_effect=lambda s, model_id: Path(model_id + ".onnx"),
        ), patch.object(adversarial_evaluate, "_read_scenario", return_value=scenario), patch.object(
            adversarial_evaluate, "_validate_registered_sources"
        ), patch.object(
            adversarial_evaluate, "_replay_evaluation_setup", return_value=(None, ())
        ), patch.object(
            adversarial_evaluate,
            "_write_report",
            side_effect=lambda s, report: report,
        ):
            report = adversarial_evaluate.evaluate_adversarial_scenarios(
                store,
                candidate_model_id="candidate",
                baseline_model_id="baseline",
                opponent_model_id="opponent",
                environment_path="env.exe",
                scenario_ids=[SCENARIO_ID],
                matches_per_scenario=4,
                match_runner=runner,
            )

        self.assertEqual(calls[0]["opponent_model_path"], Path("opponent.onnx"))
        self.assertEqual(calls[1]["opponent_model_path"], Path("opponent.onnx"))
        self.assertTrue(report["scenarios"][0]["model_opponent_used"])
        self.assertIsNone(report["scenarios"][0]["replay"])


if __name__ == "__main__":
    unittest.main()
