from __future__ import annotations

import json
import tempfile
import unittest
from datetime import datetime, timezone
from pathlib import Path

import bees_run_lifecycle as lifecycle


class RunLifecycleTests(unittest.TestCase):
    def _assets(self, root: Path) -> Path:
        assets = root / "Assets"
        training = assets / "Training"
        scenes = assets / "Scripts" / "Scenes"
        training.mkdir(parents=True)
        scenes.mkdir(parents=True)
        (training / "continual_learning_config.json").write_text(
            json.dumps({
                "behavior_name": "BeesRL1v1",
                "policy_abi_version": 18,
                "policy_signature": "signature-v18",
                "observation_schema_version": 10,
                "action_schema_version": 8,
                "reward_schema_version": 3,
                "scenario_schema_version": 1,
            }),
            encoding="utf-8",
        )
        (training / "rl_1v1_config.yaml").write_text(
            "behaviors:\n"
            "  BeesRL1v1:\n"
            "    trainer_type: ppo\n"
            "    network_settings:\n"
            "      normalize: true\n"
            "      hidden_units: 128\n"
            "      num_layers: 3\n"
            "    max_steps: 1000\n",
            encoding="utf-8",
        )
        (scenes / "RlOneVsOneReward.cs").write_text("reward-v1\n", encoding="utf-8")
        (scenes / "RlPolicySchema.cs").write_text("policy-v18\n", encoding="utf-8")
        (scenes / "RlCombatPerception.cs").write_text("perception-v1\n", encoding="utf-8")
        (scenes / "RlOneVsOneAgent.cs").write_text("actions-v1\n", encoding="utf-8")
        (scenes / "RlTeamExplorationGrid.cs").write_text("exploration-v1\n", encoding="utf-8")
        (scenes / "RlEpisodeShipIdentity.cs").write_text("identity-v1\n", encoding="utf-8")
        return assets

    def test_compatible_build_keeps_same_run(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            assets = self._assets(root)
            state = root / "current.json"
            first = lifecycle.plan_run(
                assets, state, datetime(2026, 9, 24, 12, 0, tzinfo=timezone.utc)
            )
            lifecycle.commit_plan(state, first)
            second = lifecycle.plan_run(
                assets, state, datetime(2026, 9, 24, 13, 0, tzinfo=timezone.utc)
            )
            self.assertFalse(second["incompatible"])
            self.assertFalse(second["new_run"])
            self.assertEqual(second["run_id"], first["run_id"])
            self.assertEqual(second["compatibility_key"], first["compatibility_key"])

    def test_reward_change_creates_new_run_even_without_manual_version_bump(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            assets = self._assets(root)
            state = root / "current.json"
            first = lifecycle.plan_run(
                assets, state, datetime(2026, 9, 24, 12, 0, tzinfo=timezone.utc)
            )
            lifecycle.commit_plan(state, first)
            reward = assets / "Scripts" / "Scenes" / "RlOneVsOneReward.cs"
            reward.write_text("reward-v2\n", encoding="utf-8")
            second = lifecycle.plan_run(
                assets, state, datetime(2026, 9, 24, 13, 0, tzinfo=timezone.utc)
            )
            self.assertTrue(second["incompatible"])
            self.assertTrue(second["new_run"])
            self.assertNotEqual(second["run_id"], first["run_id"])
            self.assertNotEqual(second["compatibility_key"], first["compatibility_key"])

    def test_observation_implementation_change_creates_new_run(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            assets = self._assets(root)
            state = root / "current.json"
            first = lifecycle.plan_run(assets, state)
            lifecycle.commit_plan(state, first)
            perception = assets / "Scripts" / "Scenes" / "RlCombatPerception.cs"
            perception.write_text("perception-v2\n", encoding="utf-8")
            second = lifecycle.plan_run(assets, state)
            self.assertTrue(second["incompatible"])
            self.assertNotEqual(second["run_id"], first["run_id"])

    def test_network_architecture_change_creates_new_run(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            assets = self._assets(root)
            state = root / "current.json"
            first = lifecycle.plan_run(assets, state)
            lifecycle.commit_plan(state, first)
            trainer = assets / "Training" / "rl_1v1_config.yaml"
            trainer.write_text(
                trainer.read_text(encoding="utf-8").replace(
                    "hidden_units: 128", "hidden_units: 256"
                ),
                encoding="utf-8",
            )
            second = lifecycle.plan_run(assets, state)
            self.assertTrue(second["incompatible"])
            self.assertNotEqual(second["run_id"], first["run_id"])


if __name__ == "__main__":
    unittest.main()
