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
        (scenes / "RlOneVsOneEpisodeCoordinator.cs").write_text(
            "episode-coordinator-v1\n",
            encoding="utf-8",
        )
        (scenes / "RlTeamExplorationGrid.cs").write_text("exploration-v1\n", encoding="utf-8")
        (scenes / "RlEpisodeShipIdentity.cs").write_text("identity-v1\n", encoding="utf-8")
        return assets

    def test_contract_fingerprint_is_stable_and_tracks_semantic_changes(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            assets = self._assets(root)
            first = lifecycle.contract_fingerprint(assets)
            second = lifecycle.contract_fingerprint(assets)

            self.assertEqual(first["compatibility_key"], second["compatibility_key"])
            self.assertEqual(first["contract"], second["contract"])

            reward = assets / "Scripts" / "Scenes" / "RlOneVsOneReward.cs"
            reward.write_text("reward-v2\n", encoding="utf-8")
            changed = lifecycle.contract_fingerprint(assets)
            self.assertNotEqual(
                changed["compatibility_key"],
                first["compatibility_key"],
            )

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

    def test_force_new_creates_new_run_without_contract_change(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            assets = self._assets(root)
            state = root / "current.json"
            first = lifecycle.plan_run(
                assets, state, datetime(2026, 9, 24, 12, 0, tzinfo=timezone.utc)
            )
            lifecycle.commit_plan(state, first)
            second = lifecycle.plan_run(
                assets,
                state,
                datetime(2026, 9, 24, 13, 0, tzinfo=timezone.utc),
                force_new=True,
                build_id="build-42",
                environment_args=("--rl-map-size=32", "--rl-health-ratio=.05"),
            )
            self.assertTrue(second["incompatible"])
            self.assertTrue(second["new_run"])
            self.assertTrue(second["forced_new_run"])
            self.assertEqual(second["build_id"], "build-42")
            self.assertEqual(
                second["environment_args"],
                ["--rl-map-size=32", "--rl-health-ratio=.05"],
            )
            self.assertNotEqual(second["run_id"], first["run_id"])
            self.assertEqual(second["compatibility_key"], first["compatibility_key"])
            self.assertEqual(second["contract"], first["contract"])

    def test_forced_new_build_binding_rejects_unsafe_or_non_forced_use(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            assets = self._assets(root)
            state = root / "current.json"

            with self.assertRaisesRegex(ValueError, "only valid for a forced-new"):
                lifecycle.plan_run(assets, state, build_id="build-42")
            with self.assertRaisesRegex(ValueError, "only valid for a forced-new"):
                lifecycle.plan_run(
                    assets,
                    state,
                    environment_args=("--rl-map-size=32",),
                )
            with self.assertRaisesRegex(ValueError, "safe release-id"):
                lifecycle.plan_run(
                    assets,
                    state,
                    force_new=True,
                    build_id="../unsafe",
                )
            with self.assertRaisesRegex(ValueError, "non-empty strings"):
                lifecycle.plan_run(
                    assets,
                    state,
                    force_new=True,
                    build_id="build-42",
                    environment_args=("",),
                )

    def test_ordinary_plan_has_no_build_binding(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            assets = self._assets(root)
            state = root / "current.json"

            plan = lifecycle.plan_run(assets, state)

            self.assertIsNone(plan["build_id"])
            self.assertIsNone(plan["environment_args"])
            self.assertFalse(plan["forced_new_run"])

    def test_comment_only_rl_source_change_keeps_run_compatible(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            assets = self._assets(root)
            state = root / "current.json"
            first = lifecycle.plan_run(assets, state)
            lifecycle.commit_plan(state, first)
            reward = assets / "Scripts" / "Scenes" / "RlOneVsOneReward.cs"
            reward.write_text("// explanatory comment\nreward-v1\n", encoding="utf-8")
            second = lifecycle.plan_run(assets, state)
            self.assertFalse(second["incompatible"])
            self.assertEqual(second["run_id"], first["run_id"])

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

    def test_episode_reward_coordinator_change_creates_new_run(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            assets = self._assets(root)
            state = root / "current.json"
            first = lifecycle.plan_run(assets, state)
            lifecycle.commit_plan(state, first)
            coordinator = (
                assets / "Scripts" / "Scenes" / "RlOneVsOneEpisodeCoordinator.cs"
            )
            coordinator.write_text("episode-coordinator-v2\n", encoding="utf-8")
            second = lifecycle.plan_run(assets, state)
            self.assertTrue(second["incompatible"])
            self.assertNotEqual(second["run_id"], first["run_id"])
            self.assertNotEqual(
                second["compatibility_key"],
                first["compatibility_key"],
            )

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
