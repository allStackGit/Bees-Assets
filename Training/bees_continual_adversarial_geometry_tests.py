"""Focused tests for immutable player-derived tactical geometry descriptors."""

from __future__ import annotations

import importlib.util
from pathlib import Path
from types import SimpleNamespace
import sys
import tempfile
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


class FakeStore:
    def __init__(self, root: Path):
        self.root = root
        self.experience_dir = root / "experience"
        self.experience_dir.mkdir(parents=True)
        self.compatibility = SimpleNamespace(policy_abi_version=7)

    def _require_initialized(self):
        return None


class AdversarialGeometryTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.store = FakeStore(self.root / "store")
        self.approval = self.root / "approval.json"
        self.approval.write_bytes(b'{"approved":true}\n')
        self.batch_id = "demo-" + "a" * 24

    def tearDown(self):
        self.temp.cleanup()

    def approved_archive(self, _store, batch_id):
        self.assertEqual(batch_id, self.batch_id)
        return {
            "approval_path": self.approval,
            "row": {"payload_sha256": "1" * 64},
            "demo_sha256": "2" * 64,
            "envelope": {
                "model_id": "bees-rl-v7-test",
                "game_build_version": "2026.09.11+test",
            },
        }

    def register(self, **kwargs):
        values = {
            "bee_composition": "Wasp",
            "human_composition": "Gunship",
            "target_fraction": 0.1,
            "rationale": "Repeated long-range engagement geometry.",
        }
        values.update(kwargs)
        with patch.object(adversarial, "_approved_archive", side_effect=self.approved_archive):
            return adversarial.register_player_derived_scenario(
                self.store,
                [self.batch_id],
                **values,
            )

    def test_geometry_is_content_addressed_and_encoded_separately_from_matchup_pressure(self):
        scenario = self.register(map_size=96, spawn_separation_ratio=0.25)
        identity = scenario["scenario"]["identity"]

        self.assertEqual(
            identity["geometry"],
            {
                "schema_version": 1,
                "map_size": 96.0,
                "spawn_separation_ratio": 0.25,
            },
        )
        with patch.object(adversarial, "_approved_archive", side_effect=self.approved_archive):
            pressure = adversarial.encode_scenarios_for_unity(
                self.store,
                [scenario["scenario_id"]],
            )
            geometry = adversarial.encode_geometry_catalog_for_unity(
                self.store,
                [scenario["scenario_id"]],
            )

        self.assertEqual(
            pressure,
            f"{scenario['scenario_id']}:Wasp>Gunship@0.1",
        )
        self.assertEqual(
            geometry,
            f"{scenario['scenario_id']}:96,0.25",
        )

    def test_existing_no_geometry_scenarios_remain_valid_and_emit_no_catalog_entry(self):
        scenario = self.register()
        self.assertNotIn("geometry", scenario["scenario"]["identity"])

        with patch.object(adversarial, "_approved_archive", side_effect=self.approved_archive):
            geometry = adversarial.encode_geometry_catalog_for_unity(
                self.store,
                [scenario["scenario_id"]],
            )
        self.assertEqual(geometry, "")

    def test_geometry_changes_immutable_scenario_identity(self):
        baseline = self.register()
        geometry = self.register(map_size=96, spawn_separation_ratio=0.5)
        self.assertNotEqual(baseline["scenario_id"], geometry["scenario_id"])

    def test_partial_or_unsafe_geometry_fails_closed(self):
        for kwargs in (
            {"map_size": 96},
            {"spawn_separation_ratio": 0.5},
            {"map_size": 9, "spawn_separation_ratio": 0.5},
            {"map_size": 96, "spawn_separation_ratio": 0},
            {"map_size": 96, "spawn_separation_ratio": 0.751},
            {"map_size": float("nan"), "spawn_separation_ratio": 0.5},
        ):
            with self.subTest(kwargs=kwargs):
                with self.assertRaises(continual.ValidationError):
                    self.register(**kwargs)


if __name__ == "__main__":
    unittest.main()
