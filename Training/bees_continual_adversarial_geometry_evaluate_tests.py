"""Focused argument tests for player-derived tactical-geometry evaluation."""

from __future__ import annotations

import importlib.util
from pathlib import Path
import sys
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
evaluate = _load("bees_continual_evaluate")
adversarial_evaluate = _load("bees_continual_adversarial_evaluate")


class AdversarialGeometryEvaluationArgumentTests(unittest.TestCase):
    def test_registered_geometry_becomes_exact_fixed_evaluation_setup(self):
        args = adversarial_evaluate._scenario_env_args(
            {
                "bee_composition": ["Wasp"],
                "human_composition": ["Gunship"],
                "geometry": {
                    "schema_version": 1,
                    "map_size": 96.0,
                    "spawn_separation_ratio": 0.25,
                },
            }
        )
        self.assertIn("--rl-matchup-mode=fixed", args)
        self.assertIn("--rl-map-size=96", args)
        self.assertIn("--bees-rl-fixed-geometry=96,0.25", args)

    def test_scenario_without_geometry_preserves_original_evaluation_setup(self):
        args = adversarial_evaluate._scenario_env_args(
            {
                "bee_composition": ["Wasp"],
                "human_composition": ["Gunship"],
            }
        )
        self.assertIn("--rl-matchup-mode=fixed", args)
        self.assertFalse(any(value.startswith("--bees-rl-fixed-geometry=") for value in args))

    def test_manual_geometry_or_replay_cannot_override_registry_during_diagnostic_evaluation(self):
        for value in (
            "--bees-rl-fixed-geometry=96,0.5",
            "--bees-adversarial-geometry-catalog=adv-aaaaaaaaaaaaaaaaaaaaaaaa:96,0.5",
            "--bees-adversarial-replay-catalog=manual.json",
        ):
            with self.subTest(value=value):
                with self.assertRaises(continual.ValidationError):
                    adversarial_evaluate._validate_base_env_args([value])


if __name__ == "__main__":
    unittest.main()
