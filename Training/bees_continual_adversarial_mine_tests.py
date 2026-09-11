"""Focused tests for explainable player-derived tactic mining."""

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
mine = _load("bees_continual_adversarial_mine")


OBSERVATION_SIZE = 4701
CONTINUOUS_ACTIONS = 34
DISCRETE_BRANCHES = [2] * 16 + [5, 65, 65, 65]


def normalize_positive(value: float, scale: float) -> float:
    return value / (value + scale)


def squash_distance(value: float) -> float:
    absolute = abs(value)
    return 0.0 if absolute == 0 else (1 if value > 0 else -1) * absolute / (absolute + 40.0)


def set_bits(values, start, value, bits=6):
    for bit in range(bits):
        values[start + bit] = 1.0 if value & (1 << bit) else 0.0


def tactic_observation(*, enemy_distance=30.0, edge=0.8, self_ship=13, enemy_ship=21, max_range=20.0):
    values = [0.0] * OBSERVATION_SIZE
    usable = 86.0
    values[suggest.LEVEL_SIZE_X_INDEX] = normalize_positive(usable, 100.0)
    values[suggest.LEVEL_SIZE_Y_INDEX] = normalize_positive(usable, 100.0)
    values[suggest.SELF_POSITION_X_INDEX] = edge
    values[suggest.SELF_POSITION_Y_INDEX] = 0.0
    values[mine.SELF_MAX_RANGE_INDEX] = normalize_positive(max_range, 80.0)
    set_bits(values, mine.SELF_SHIP_BIT_START, self_ship)
    values[suggest.FIRST_ENEMY_SLOT_INDEX] = 1.0
    values[suggest.FIRST_ENEMY_X_INDEX] = squash_distance(enemy_distance)
    values[suggest.FIRST_ENEMY_Y_INDEX] = 0.0
    set_bits(values, mine.ENEMY_SHIP_BIT_START, enemy_ship)
    return values


def actions(*, moving=False, firing=False, special=0):
    continuous = [0.0] * CONTINUOUS_ACTIONS
    if moving:
        continuous[0] = 1.0
    discrete = [0] * len(DISCRETE_BRANCHES)
    if firing:
        discrete[0] = 1
    discrete[mine.SPECIAL_ACTION_BRANCH] = special
    return continuous, discrete


class FakeStore:
    def __init__(self):
        self.compatibility = SimpleNamespace(policy_abi_version=7)

    def _require_initialized(self):
        return None


class AdversarialTacticMiningTests(unittest.TestCase):
    def test_beyond_range_edge_hold_sustained_fire_is_identified_explainably(self):
        observations = [tactic_observation() for _ in range(10)]
        continuous = []
        discrete = []
        for index in range(10):
            cont, disc = actions(firing=index < 8)
            continuous.append(cont)
            discrete.append(disc)

        result = mine.analyze_tactical_signature(observations, continuous, discrete)

        self.assertEqual(result["self_ship_name"], "Gunship")
        self.assertEqual(result["first_enemy_ship_name"], "Wasp")
        self.assertEqual(result["range_style"], "beyond-range")
        self.assertEqual(result["movement_style"], "edge-hold")
        self.assertEqual(result["fire_style"], "sustained")
        self.assertAlmostEqual(result["median_fire_range_ratio"], 1.5, places=5)
        self.assertEqual(
            result["signature"],
            "self=13|enemy=21|range=beyond-range|movement=edge-hold|fire=sustained|special=no-special",
        )

    def test_increasing_enemy_distance_with_active_movement_is_classified_as_retreat(self):
        observations = [
            tactic_observation(enemy_distance=10.0 + index * 2.0, edge=0.1)
            for index in range(10)
        ]
        continuous = []
        discrete = []
        for _ in range(10):
            cont, disc = actions(moving=True, firing=False)
            continuous.append(cont)
            discrete.append(disc)

        result = mine.analyze_tactical_signature(observations, continuous, discrete)
        self.assertEqual(result["movement_style"], "retreat")
        self.assertEqual(result["fire_style"], "none")
        self.assertGreater(result["retreat_transition_count"], 0)
        self.assertEqual(result["approach_transition_count"], 0)

    def test_short_capability_style_demo_is_not_treated_as_a_repeated_tactic(self):
        observations = [tactic_observation() for _ in range(mine.MIN_TACTIC_RECORDS - 1)]
        continuous = []
        discrete = []
        for _ in observations:
            cont, disc = actions(firing=True)
            continuous.append(cont)
            discrete.append(disc)
        with self.assertRaises(continual.ValidationError):
            mine.analyze_tactical_signature(observations, continuous, discrete)

    def test_miner_groups_repeated_approved_signatures_without_auto_registration(self):
        behavior = SimpleNamespace(
            observation_specs=[SimpleNamespace(shape=(OBSERVATION_SIZE,))],
            action_spec=SimpleNamespace(
                continuous_size=CONTINUOUS_ACTIONS,
                discrete_branches=tuple(DISCRETE_BRANCHES),
            ),
        )
        capture = {
            "observationSize": OBSERVATION_SIZE,
            "continuousActionCount": CONTINUOUS_ACTIONS,
            "discreteBranchSizes": DISCRETE_BRANCHES,
        }
        batches = ["demo-" + "a" * 24, "demo-" + "b" * 24]

        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)

            def approved_archive(_store, batch_id):
                return {
                    "demo": root / f"{batch_id}.demo",
                    "capture_manifest_metadata": capture,
                    "approval": {"quality_score": 0.9},
                }

            def loader(_path):
                return behavior, [SimpleNamespace(index=index) for index in range(11)], 11

            def observation_reader(pair, _behavior):
                return tactic_observation(enemy_distance=30.0, edge=0.8)

            def action_reader(pair):
                return actions(firing=pair.index < 8)

            with patch.object(mine, "_approved_archive", side_effect=approved_archive) as approval:
                result = mine.mine_approved_tactics(
                    FakeStore(),
                    batches,
                    loader=loader,
                    observation_reader=observation_reader,
                    action_reader=action_reader,
                )

        self.assertEqual(approval.call_count, 2)
        self.assertEqual(len(result["analyzed"]), 2)
        self.assertEqual(len(result["repeated_tactics"]), 1)
        repeated = result["repeated_tactics"][0]
        self.assertEqual(repeated["count"], 2)
        self.assertEqual(repeated["batch_ids"], sorted(batches))
        self.assertTrue(repeated["review_required"])
        self.assertFalse(result["automatic_scenario_registration"])

    def test_frozen_ship_id_labels_match_policy_schema_source(self):
        source = (PROJECT_ROOT / "Scripts" / "Scenes" / "RlPolicySchema.cs").read_text(
            encoding="utf-8"
        )
        self.assertIn("ConfigData.ShipTypes.Gunship, 13", source)
        self.assertIn("ConfigData.ShipTypes.Wasp, 21", source)
        self.assertEqual(mine.SHIP_TYPE_NAMES[13], "Gunship")
        self.assertEqual(mine.SHIP_TYPE_NAMES[21], "Wasp")


if __name__ == "__main__":
    unittest.main()
