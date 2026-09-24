"""Focused tests for Training/bees_continual_release_health.py.

Run from the Bees Assets root:
    python Training\bees_continual_release_health_tests.py
"""

from __future__ import annotations

import copy
import json
from pathlib import Path
import tempfile
import unittest

from bees_continual_bootstrap import bootstrap_champion
from bees_continual_deployment import publish_current_champion
from bees_continual_hot_bundle import (
    EXPECTED_MANIFEST_ADDRESS,
    EXPECTED_MODEL_ADDRESS,
    publish_hot_bundle,
)
from bees_continual_learning import (
    ContinualLearningStore,
    canonical_json,
    sha256_bytes,
    sha256_file,
)
from bees_continual_release_health import inspect_release_health


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


class ReleaseHealthTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name) / "store"
        self.artifact_dir = Path(self.temp.name) / "artifacts"
        self.artifact_dir.mkdir()
        self.distribution_root = Path(self.temp.name) / "distribution"
        self.build_dir = Path(self.temp.name) / "unity"
        self.build_dir.mkdir()
        self.store = ContinualLearningStore(self.root, copy.deepcopy(TEST_CONFIG))
        self.store.initialize()

        source = self.artifact_dir / "generation-zero.onnx"
        source.write_bytes(b"generation-zero-model")
        model = self.store.register_model(
            source,
            training_run_id="release-health-test",
            training_step=100,
            game_build_version="test-build",
            status="candidate",
        )
        bootstrap_champion(
            self.store,
            model["model_id"],
            reason="Trusted release-health generation zero",
        )
        self.champion = self.store.get_model(model["model_id"])

    def tearDown(self):
        self.temp.cleanup()

    @staticmethod
    def validate_synthetic_onnx(_path):
        return None

    def publish_deployment(self):
        return publish_current_champion(
            self.store,
            onnx_validator=self.validate_synthetic_onnx,
        )

    def publish_windows_bundle(self):
        deployment = self.publish_deployment()
        manifest = json.loads(Path(deployment["manifest_path"]).read_text(encoding="utf-8"))
        identity = manifest["identity"]
        bundle = self.build_dir / "bees-rl-policy"
        bundle.write_bytes(b"synthetic-unity-asset-bundle")
        metadata = self.build_dir / "bees-rl-policy.metadata.json"
        metadata.write_text(
            json.dumps(
                {
                    "schema_version": 1,
                    "platform": "WindowsPlayer",
                    "build_target": "StandaloneWindows64",
                    "unity_version": "6000.0-test",
                    "deployment_id": deployment["deployment_id"],
                    "model_id": deployment["model_id"],
                    "model_sha256": identity["model_sha256"],
                    "manifest_sha256": deployment["manifest_sha256"],
                    "policy_abi_version": TEST_CONFIG["policy_abi_version"],
                    "policy_signature": TEST_CONFIG["policy_signature"],
                    "bundle_file": bundle.name,
                    "bundle_sha256": sha256_file(bundle),
                    "bundle_size_bytes": bundle.stat().st_size,
                    "model_address": EXPECTED_MODEL_ADDRESS,
                    "manifest_address": EXPECTED_MANIFEST_ADDRESS,
                }
            ),
            encoding="utf-8",
        )
        published = publish_hot_bundle(
            self.store,
            metadata,
            self.distribution_root,
            onnx_validator=self.validate_synthetic_onnx,
        )
        return deployment, published

    def test_healthy_registry_deployment_and_hot_bundle_chain(self):
        deployment, published = self.publish_windows_bundle()

        health = inspect_release_health(
            self.store,
            distribution_root=self.distribution_root,
            platforms=["WindowsPlayer"],
        )

        self.assertTrue(health["healthy"], health["issues"])
        self.assertEqual(health["issue_count"], 0)
        self.assertEqual(health["current_champion_model_id"], self.champion["model_id"])
        self.assertEqual(health["current_deployment"]["deployment_id"], deployment["deployment_id"])
        self.assertEqual(health["hot_platforms"][0]["bundle_path"], published["bundle_path"])

    def test_semantically_drifted_deployment_pointer_is_detected_even_with_valid_identity_hash(self):
        deployment = self.publish_deployment()
        pointer_path = Path(deployment["pointer_path"])
        pointer = json.loads(pointer_path.read_text(encoding="utf-8"))
        pointer["identity"]["model_id"] = "bees-rl-v8-" + "f" * 24
        pointer["identity_sha256"] = sha256_bytes(
            canonical_json(pointer["identity"]).encode("utf-8")
        )
        pointer_path.write_text(json.dumps(pointer), encoding="utf-8")

        health = inspect_release_health(self.store)
        codes = {issue["code"] for issue in health["issues"]}

        self.assertFalse(health["healthy"])
        self.assertIn("deployment_model_mismatch", codes)

    def test_corrupt_hot_bundle_bytes_are_detected(self):
        _deployment, published = self.publish_windows_bundle()
        bundle = Path(published["bundle_path"])
        bundle.write_bytes(b"corrupt-hot-bundle")

        health = inspect_release_health(
            self.store,
            distribution_root=self.distribution_root,
            platforms=["WindowsPlayer"],
        )
        codes = {issue["code"] for issue in health["issues"]}

        self.assertFalse(health["healthy"])
        self.assertTrue(
            {"hot_WindowsPlayer_bundle_size_mismatch", "hot_WindowsPlayer_bundle_hash_mismatch"}
            & codes
        )

    def test_missing_requested_platform_pointer_is_unhealthy(self):
        self.publish_deployment()

        health = inspect_release_health(
            self.store,
            distribution_root=self.distribution_root,
            platforms=["LinuxPlayer"],
        )
        codes = {issue["code"] for issue in health["issues"]}

        self.assertFalse(health["healthy"])
        self.assertIn("hot_LinuxPlayer_pointer_missing_or_invalid", codes)


if __name__ == "__main__":
    unittest.main()
