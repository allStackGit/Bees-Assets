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
suggest = _load("bees_continual_adversarial_suggest")
mine = _load("bees_continual_adversarial_mine")
replay = _load("bees_continual_adversarial_replay")
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

    def test_geometry_catalog_is_registry_derived_and_injected_after_pressure(self):
        encoded = "adv-aaaaaaaaaaaaaaaaaaaaaaaa:Wasp>Gunship@0.1"
        geometry = "adv-aaaaaaaaaaaaaaaaaaaaaaaa:96,0.25"
        injected = launcher.inject_unity_pressure_arg(
            ["Training/rl_1v1_config.yaml", "--run-id=run-geometry"],
            encoded,
            geometry,
        )
        self.assertEqual(injected[-3], "--env-args")
        self.assertEqual(injected[-2], "--bees-adversarial-matchups=" + encoded)
        self.assertEqual(
            injected[-1],
            "--bees-adversarial-geometry-catalog=" + geometry,
        )

        for manual in (
            "--bees-adversarial-geometry-catalog=manual",
            "--bees-rl-fixed-geometry=96,0.5",
        ):
            with self.subTest(manual=manual):
                with self.assertRaises(SystemExit):
                    launcher.inject_unity_pressure_arg([manual], encoded, geometry)

    def test_replay_catalog_is_registry_derived_and_injected_last(self):
        encoded = "adv-aaaaaaaaaaaaaaaaaaaaaaaa:Wasp>Gunship@0.1"
        geometry = "adv-aaaaaaaaaaaaaaaaaaaaaaaa:96,0.25"
        replay_path = str(TRAINING_DIR / "catalog.json")
        injected = launcher.inject_unity_pressure_arg(
            ["Training/rl_1v1_config.yaml", "--run-id=run-replay"],
            encoded,
            geometry,
            replay_path,
        )
        self.assertEqual(injected[-4], "--env-args")
        self.assertEqual(injected[-3], "--bees-adversarial-matchups=" + encoded)
        self.assertEqual(injected[-2], "--bees-adversarial-geometry-catalog=" + geometry)
        self.assertEqual(
            injected[-1],
            "--bees-adversarial-replay-catalog=" + replay_path,
        )

        with self.assertRaises(SystemExit):
            launcher.inject_unity_pressure_arg(
                ["--bees-adversarial-replay-catalog=manual.json"],
                encoded,
                geometry,
                replay_path,
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

    def test_same_run_cannot_change_registered_tactical_geometry(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            config = continual.load_config(TRAINING_DIR / "continual_learning_config.json")
            store = continual.ContinualLearningStore(Path(temp_dir) / "store", config)
            store.initialize()
            scenario = "adv-aaaaaaaaaaaaaaaaaaaaaaaa"
            encoded = scenario + ":Wasp>Gunship@0.1"

            launcher.record_run_selection(
                store,
                run_id="run-geometry",
                scenario_ids=[scenario],
                encoded=encoded,
                geometry_catalog=scenario + ":96,0.25",
            )
            with self.assertRaises(continual.ValidationError):
                launcher.record_run_selection(
                    store,
                    run_id="run-geometry",
                    scenario_ids=[scenario],
                    encoded=encoded,
                    geometry_catalog=scenario + ":96,0.5",
                )

    def test_same_run_cannot_attach_or_change_replay_catalog(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            config = continual.load_config(TRAINING_DIR / "continual_learning_config.json")
            store = continual.ContinualLearningStore(Path(temp_dir) / "store", config)
            store.initialize()
            scenario = "adv-aaaaaaaaaaaaaaaaaaaaaaaa"
            encoded = scenario + ":Wasp>Gunship@0.1"

            launcher.record_run_selection(
                store,
                run_id="run-replay",
                scenario_ids=[scenario],
                encoded=encoded,
                replay_catalog_sha256="a" * 64,
            )
            with self.assertRaises(continual.ValidationError):
                launcher.record_run_selection(
                    store,
                    run_id="run-replay",
                    scenario_ids=[scenario],
                    encoded=encoded,
                    replay_catalog_sha256="b" * 64,
                )

            launcher.record_run_selection(
                store,
                run_id="run-no-replay",
                scenario_ids=[scenario],
                encoded=encoded,
            )
            with self.assertRaises(continual.ValidationError):
                launcher.record_run_selection(
                    store,
                    run_id="run-no-replay",
                    scenario_ids=[scenario],
                    encoded=encoded,
                    replay_catalog_sha256="a" * 64,
                )


if __name__ == "__main__":
    unittest.main()
