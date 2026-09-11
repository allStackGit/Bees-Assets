"""Focused tests for non-authoritative tactical geometry suggestions from approved demos."""

from __future__ import annotations

import importlib.util
from pathlib import Path
from types import SimpleNamespace
import sys
import tempfile
import unittest
from unittest.mock import patch


TRAINING_DIR = Path(__file__).parent
PROJECT_ROOT = TRAINING_DIR.parent


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


OBSERVATION_SIZE = 4701
CONTINUOUS_ACTIONS = 34
DISCRETE_BRANCHES = [2] * 16 + [5, 65, 65, 65]


def normalize_positive(value: float, scale: float) -> float:
    return value / (value + scale)


def squash_distance(value: float) -> float:
    absolute = abs(value)
    return 0.0 if absolute == 0 else (1 if value > 0 else -1) * absolute / (absolute + 40.0)


def observation(*, map_size=96.0, center_y=24.0, enemy_distance=None):
    values = [0.0] * OBSERVATION_SIZE
    usable = map_size - 10.0
    values[suggest.LEVEL_SIZE_X_INDEX] = normalize_positive(usable, 100.0)
    values[suggest.LEVEL_SIZE_Y_INDEX] = normalize_positive(usable, 100.0)
    values[suggest.SELF_POSITION_X_INDEX] = 0.0
    values[suggest.SELF_POSITION_Y_INDEX] = center_y / (usable * 0.5)
    if enemy_distance is not None:
        values[suggest.FIRST_ENEMY_SLOT_INDEX] = 1.0
        values[suggest.FIRST_ENEMY_X_INDEX] = squash_distance(enemy_distance)
        values[suggest.FIRST_ENEMY_Y_INDEX] = 0.0
    return values


class FakeStore:
    def __init__(self):
        self.compatibility = SimpleNamespace(policy_abi_version=7)

    def _require_initialized(self):
        return None


class TacticalGeometrySuggestionTests(unittest.TestCase):
    def test_v7_observation_inverts_map_spawn_and_first_contact_geometry(self):
        result = suggest.infer_geometry_from_observations(
            [
                observation(enemy_distance=None),
                observation(enemy_distance=20.0),
            ]
        )

        self.assertAlmostEqual(result["usable_level_size"]["x"], 86.0, places=5)
        self.assertAlmostEqual(result["map_size_estimate"], 96.0, places=5)
        self.assertAlmostEqual(result["first_record_center_distance"], 24.0, places=5)
        self.assertAlmostEqual(result["spawn_separation_estimate"], 48.0, places=5)
        self.assertAlmostEqual(result["spawn_separation_ratio_estimate"], 0.5, places=5)
        self.assertAlmostEqual(result["first_visible_enemy_distance"], 20.0, places=5)
        self.assertEqual(result["first_visible_enemy_record"], 1)
        self.assertTrue(result["square_training_map_compatible"])
        self.assertEqual(
            result["registration_candidate"],
            {
                "map_size": result["map_size_estimate"],
                "spawn_separation_ratio": result["spawn_separation_ratio_estimate"],
            },
        )

    def test_no_visible_enemy_is_reported_without_inventing_contact_distance(self):
        result = suggest.infer_geometry_from_observations([observation()])
        self.assertIsNone(result["first_visible_enemy_distance"])
        self.assertIsNone(result["first_visible_enemy_record"])

    def test_inconsistent_map_observations_fail_closed(self):
        with self.assertRaises(continual.ValidationError):
            suggest.infer_geometry_from_observations(
                [observation(map_size=96), observation(map_size=80)]
            )

    def test_rectangular_player_map_never_becomes_fake_square_registration_candidate(self):
        values = observation(map_size=96)
        # Preserve the 96-unit width but make the reconstructed height 64 units.
        values[suggest.LEVEL_SIZE_Y_INDEX] = normalize_positive(54.0, 100.0)
        result = suggest.infer_geometry_from_observations([values])

        self.assertAlmostEqual(result["map_width_estimate"], 96.0, places=5)
        self.assertAlmostEqual(result["map_height_estimate"], 64.0, places=5)
        self.assertFalse(result["square_training_map_compatible"])
        self.assertIsNone(result["map_size_estimate"])
        self.assertIsNone(result["spawn_separation_ratio_estimate"])
        self.assertIsNone(result["registration_candidate"])

    def test_suggestion_revalidates_approved_archive_and_remains_non_authoritative(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            demo = Path(temp_dir) / "approved.demo"
            demo.write_bytes(b"demo")
            capture = {
                "observationSize": OBSERVATION_SIZE,
                "continuousActionCount": CONTINUOUS_ACTIONS,
                "discreteBranchSizes": DISCRETE_BRANCHES,
            }
            archive = {
                "demo": demo,
                "capture_manifest_metadata": capture,
            }
            behavior = SimpleNamespace(
                observation_specs=[SimpleNamespace(shape=(OBSERVATION_SIZE,))],
                action_spec=SimpleNamespace(
                    continuous_size=CONTINUOUS_ACTIONS,
                    discrete_branches=tuple(DISCRETE_BRANCHES),
                ),
            )
            pairs = [object(), object()]

            def loader(_path):
                return behavior, pairs, len(pairs)

            observations = iter([observation(), observation(enemy_distance=30)])

            with patch.object(suggest, "_approved_archive", return_value=archive) as approval:
                result = suggest.suggest_tactical_geometry(
                    FakeStore(),
                    "demo-" + "a" * 24,
                    loader=loader,
                    observation_reader=lambda _pair, _behavior: next(observations),
                )

            approval.assert_called_once()
            self.assertFalse(result["authoritative"])
            self.assertIsNotNone(result["inferred"]["registration_candidate"])
            self.assertTrue(any("Review" in value for value in result["caveats"]))

    def test_only_policy_abi_v7_is_interpreted(self):
        store = FakeStore()
        store.compatibility.policy_abi_version = 8
        with self.assertRaises(continual.ValidationError):
            suggest.suggest_tactical_geometry(store, "demo-" + "a" * 24)

    def test_map_padding_assumption_matches_unity_observation_contract(self):
        source = (PROJECT_ROOT / "Scripts" / "ConfigData.Gameplay.cs").read_text(
            encoding="utf-8"
        )
        self.assertIn("MapEdgePadding = new Vector2(5, 5)", source)
        self.assertEqual(suggest.MAP_EDGE_PADDING_PER_SIDE, 5.0)


if __name__ == "__main__":
    unittest.main()
