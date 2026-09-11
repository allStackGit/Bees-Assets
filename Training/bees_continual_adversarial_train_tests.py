"""Focused tests for the player-derived adversarial continual-training launcher."""

from __future__ import annotations

import importlib.util
from pathlib import Path
import sys
import tempfile
import unittest


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
launcher = _load("bees_continual_adversarial_train")


class AdversarialTrainingLauncherTests(unittest.TestCase):
    def test_extracts_registry_selection_without_leaking_custom_flag_to_mlagents(self):
        cleaned, scenarios = launcher.extract_adversarial_scenarios(
            [
                "Training/rl_1v1_config.yaml",
                "--run-id=run-1",
                "--continual-adversarial-scenarios=adv-aaaaaaaaaaaaaaaaaaaaaaaa,adv-bbbbbbbbbbbbbbbbbbbbbbbb",
                "--resume",
            ]
        )

        self.assertEqual(
            cleaned,
            ["Training/rl_1v1_config.yaml", "--run-id=run-1", "--resume"],
        )
        self.assertEqual(
            scenarios,
            ("adv-aaaaaaaaaaaaaaaaaaaaaaaa", "adv-bbbbbbbbbbbbbbbbbbbbbbbb"),
        )

    def test_selection_flag_is_required_and_duplicate_ids_fail_closed(self):
        with self.assertRaises(SystemExit):
            launcher.extract_adversarial_scenarios(["Training/rl_1v1_config.yaml"])
        with self.assertRaises(SystemExit):
            launcher.extract_adversarial_scenarios(
                ["--continual-adversarial-scenarios=adv-aaaaaaaaaaaaaaaaaaaaaaaa,adv-aaaaaaaaaaaaaaaaaaaaaaaa"]
            )

    def test_injects_pressure_after_env_args_and_rejects_manual_override(self):
        encoded = "adv-aaaaaaaaaaaaaaaaaaaaaaaa:Wasp>Gunship@0.1"
        injected = launcher.inject_unity_pressure_arg(
            [
                "Training/rl_1v1_config.yaml",
                "--run-id=run-1",
                "--env-args",
                "--rl-matchup-mode=sampled",
            ],
            encoded,
        )
        self.assertEqual(injected[-1], "--bees-adversarial-matchups=" + encoded)
        self.assertEqual(injected.count("--env-args"), 1)

        fresh = launcher.inject_unity_pressure_arg(
            ["Training/rl_1v1_config.yaml", "--run-id=run-1"],
            encoded,
        )
        self.assertEqual(fresh[-2], "--env-args")
        self.assertEqual(fresh[-1], "--bees-adversarial-matchups=" + encoded)

        with self.assertRaises(SystemExit):
            launcher.inject_unity_pressure_arg(
                ["--bees-adversarial-matchups=manual"],
                encoded,
            )
        with self.assertRaises(SystemExit):
            launcher.inject_unity_pressure_arg(
                ["--env-args=--rl-matchup-mode=sampled"],
                encoded,
            )

    def test_run_selection_is_idempotent_but_same_run_cannot_change_pressure(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            config = continual.load_config(TRAINING_DIR / "continual_learning_config.json")
            store = continual.ContinualLearningStore(Path(temp_dir) / "store", config)
            store.initialize()
            encoded = "adv-aaaaaaaaaaaaaaaaaaaaaaaa:Wasp>Gunship@0.1"

            first = launcher.record_run_selection(
                store,
                run_id="run-1",
                scenario_ids=["adv-aaaaaaaaaaaaaaaaaaaaaaaa"],
                encoded=encoded,
            )
            second = launcher.record_run_selection(
                store,
                run_id="run-1",
                scenario_ids=["adv-aaaaaaaaaaaaaaaaaaaaaaaa"],
                encoded=encoded,
            )
            self.assertEqual(first, second)

            with self.assertRaises(continual.ValidationError):
                launcher.record_run_selection(
                    store,
                    run_id="run-1",
                    scenario_ids=["adv-bbbbbbbbbbbbbbbbbbbbbbbb"],
                    encoded="adv-bbbbbbbbbbbbbbbbbbbbbbbb:Hornet>Frigate@0.1",
                )


if __name__ == "__main__":
    unittest.main()
