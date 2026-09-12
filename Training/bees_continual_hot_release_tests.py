"""Focused tests for Training/bees_continual_hot_release.py.

Run from the Bees Assets root:
    python Training\bees_continual_hot_release_tests.py
"""

from __future__ import annotations

import copy
import json
from pathlib import Path
from types import SimpleNamespace
import tempfile
import unittest

from bees_continual_bootstrap import bootstrap_champion
from bees_continual_hot_bundle import (
    EXPECTED_MANIFEST_ADDRESS,
    EXPECTED_MODEL_ADDRESS,
    SUPPORTED_PLATFORMS,
)
from bees_continual_hot_release import (
    UNITY_METADATA_FILE,
    UNITY_OUTPUT_ARGUMENT,
    build_and_publish_hot_bundles,
)
from bees_continual_learning import ContinualLearningStore, ValidationError, sha256_file


TEST_CONFIG = {
    "behavior_name": "BeesRL1v1",
    "policy_abi_version": 8,
    "policy_signature": "test-policy-v8-signature",
    "observation_schema_version": 8,
    "action_schema_version": 6,
    "reward_schema_version": 2,
    "scenario_schema_version": 1,
    "promotion": {
        "min_matches_vs_champion": 2,
        "min_win_rate_vs_champion": 0.5,
        "max_critical_regressions": 0,
        "max_historical_regression": 0.2,
        "min_historical_matches_per_opponent": 1,
        "min_competency_cases": 0,
        "max_inference_batch_milliseconds": 100.0,
    },
    "historical_league": {
        "base_weight": 1.0,
        "weakness_trigger_regression": 0.05,
        "weakness_bonus_scale": 10.0,
        "max_weight_multiplier": 4.0,
        "training_ratio": 0.0,
        "training_onnx_provider": "CPUExecutionProvider",
        "training_policy_cache_size": 1,
    },
    "ingestion": {
        "max_payload_bytes": 1024 * 1024,
        "max_steps_per_match": 100,
    },
}


class HotReleaseTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.base = Path(self.temp.name)
        self.root = self.base / "store"
        self.assets = self.base / "UnityProject" / "Assets"
        (self.assets / "Scripts" / "Scenes").mkdir(parents=True)
        (self.assets / "Scripts" / "Scenes" / "RlPolicySchema.cs").write_text("// sentinel\n")
        self.unity = self.base / "Unity.exe"
        self.unity.write_bytes(b"fake-unity")
        self.distribution = self.base / "distribution"
        self.store = ContinualLearningStore(self.root, copy.deepcopy(TEST_CONFIG))
        self.store.initialize()

        artifact = self.base / "generation-zero.onnx"
        artifact.write_bytes(b"synthetic-generation-zero")
        model = self.store.register_model(
            artifact,
            training_run_id="hot-release-test",
            training_step=100,
            game_build_version="test-build",
            status="candidate",
        )
        bootstrap_champion(
            self.store,
            model["model_id"],
            reason="Trusted hot-release test generation zero",
        )
        self.champion = self.store.get_model(model["model_id"])
        self.commands = []
        self.runner_mode = "success"

    def tearDown(self):
        self.temp.cleanup()

    @staticmethod
    def validate_synthetic_onnx(_path):
        return None

    def fake_runner(self, command, **_kwargs):
        self.commands.append(list(command))
        if self.runner_mode == "nonzero":
            return SimpleNamespace(returncode=7, stdout="synthetic Unity failure")

        output_arg = next(value for value in command if value.startswith(UNITY_OUTPUT_ARGUMENT))
        output = Path(output_arg[len(UNITY_OUTPUT_ARGUMENT):])
        if self.runner_mode == "missing-metadata":
            return SimpleNamespace(returncode=0, stdout="synthetic success without metadata")

        target = command[command.index("-buildTarget") + 1]
        platform = next(key for key, value in SUPPORTED_PLATFORMS.items() if value == target)
        staged_manifest = self.assets / "Resources" / "RlPolicy" / "BeesRL1v1Deployment.json"
        manifest = json.loads(staged_manifest.read_text(encoding="utf-8"))
        identity = manifest["identity"]
        bundle = output / "bees-rl-policy"
        bundle.write_bytes(("synthetic-bundle-" + platform).encode("utf-8"))
        metadata = {
            "schema_version": 1,
            "platform": platform,
            "build_target": target,
            "unity_version": "6000.0-test",
            "deployment_id": manifest["deployment_id"],
            "model_id": identity["model_id"],
            "model_sha256": identity["model_sha256"],
            "manifest_sha256": sha256_file(staged_manifest),
            "policy_abi_version": TEST_CONFIG["policy_abi_version"],
            "policy_signature": TEST_CONFIG["policy_signature"],
            "bundle_file": bundle.name,
            "bundle_sha256": sha256_file(bundle),
            "bundle_size_bytes": bundle.stat().st_size,
            "model_address": EXPECTED_MODEL_ADDRESS,
            "manifest_address": EXPECTED_MANIFEST_ADDRESS,
        }
        if self.runner_mode == "stale-model":
            metadata["model_id"] = "bees-rl-v8-" + "f" * 24
        (output / UNITY_METADATA_FILE).write_text(json.dumps(metadata), encoding="utf-8")
        return SimpleNamespace(returncode=0, stdout="synthetic Unity success")

    def build(self, platforms):
        return build_and_publish_hot_bundles(
            self.store,
            unity_executable=self.unity,
            assets_root=self.assets,
            distribution_root=self.distribution,
            platforms=platforms,
            runner=self.fake_runner,
            onnx_validator=self.validate_synthetic_onnx,
        )

    def test_builds_and_publishes_requested_platforms_from_same_current_deployment(self):
        result = self.build(["WindowsPlayer", "LinuxPlayer"])

        self.assertEqual(result["model_id"], self.champion["model_id"])
        self.assertEqual([item["platform"] for item in result["platforms"]], ["WindowsPlayer", "LinuxPlayer"])
        self.assertEqual(len(self.commands), 2)
        self.assertIn("StandaloneWindows64", self.commands[0])
        self.assertIn("StandaloneLinux64", self.commands[1])
        for platform in ("WindowsPlayer", "LinuxPlayer"):
            pointer = self.distribution / f"current-{platform}.json"
            self.assertTrue(pointer.is_file())
            data = json.loads(pointer.read_text(encoding="utf-8"))
            self.assertEqual(data["identity"]["model_id"], self.champion["model_id"])
            self.assertEqual(data["identity"]["deployment_id"], result["deployment_id"])

    def test_unity_nonzero_exit_blocks_publication(self):
        self.runner_mode = "nonzero"

        with self.assertRaisesRegex(ValidationError, "exit code 7"):
            self.build(["WindowsPlayer"])

        self.assertFalse((self.distribution / "current-WindowsPlayer.json").exists())

    def test_unity_success_without_metadata_blocks_publication(self):
        self.runner_mode = "missing-metadata"

        with self.assertRaisesRegex(ValidationError, "did not produce"):
            self.build(["WindowsPlayer"])

        self.assertFalse((self.distribution / "current-WindowsPlayer.json").exists())

    def test_stale_unity_metadata_is_rejected_by_independent_publisher(self):
        self.runner_mode = "stale-model"

        with self.assertRaisesRegex(ValidationError, "model_id"):
            self.build(["WindowsPlayer"])

        self.assertFalse((self.distribution / "current-WindowsPlayer.json").exists())

    def test_duplicate_platform_is_built_once(self):
        result = self.build(["WindowsPlayer", "WindowsPlayer"])

        self.assertEqual(len(self.commands), 1)
        self.assertEqual(len(result["platforms"]), 1)


if __name__ == "__main__":
    unittest.main()
