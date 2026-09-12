"""Focused tests for retained hot-bundle reactivation."""

from __future__ import annotations

import json
from pathlib import Path
from types import SimpleNamespace
import tempfile
import unittest
from unittest.mock import patch

import bees_continual_hot_bundle as hot
import bees_continual_hot_bundle_reactivate as reactivate
from bees_continual_learning import ValidationError, sha256_file


DEPLOYMENT_ID = "deploy-" + "a" * 24
MODEL_ID = "bees-rl-v8-" + "b" * 24
MODEL_SHA = "c" * 64
MANIFEST_SHA = "d" * 64
POLICY_SIGNATURE = "test-policy-v8"


class FakeStore:
    def __init__(self):
        self.compatibility = SimpleNamespace(policy_abi_version=8)
        self.config = {"policy_signature": POLICY_SIGNATURE}

    def _require_initialized(self):
        return None


class HotBundleReactivationTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.build_dir = self.root / "unity"
        self.distribution = self.root / "distribution"
        self.build_dir.mkdir()
        self.bundle = self.build_dir / "bees-rl-policy"
        self.bundle.write_bytes(b"retained-unity-asset-bundle")
        self.deployment_manifest = self.root / "deployment-manifest.json"
        self.deployment_manifest.write_text(
            json.dumps({"identity": {"model_sha256": MODEL_SHA}}),
            encoding="utf-8",
        )
        self.deployment = {
            "deployment_id": DEPLOYMENT_ID,
            "model_id": MODEL_ID,
            "manifest_path": str(self.deployment_manifest),
            "manifest_sha256": MANIFEST_SHA,
        }
        self.metadata = self.build_dir / "bees-rl-policy.metadata.json"
        self.metadata.write_text(
            json.dumps(
                {
                    "schema_version": 1,
                    "platform": "WindowsPlayer",
                    "build_target": "StandaloneWindows64",
                    "unity_version": "6000.0-test",
                    "deployment_id": DEPLOYMENT_ID,
                    "model_id": MODEL_ID,
                    "model_sha256": MODEL_SHA,
                    "manifest_sha256": MANIFEST_SHA,
                    "policy_abi_version": 8,
                    "policy_signature": POLICY_SIGNATURE,
                    "bundle_file": self.bundle.name,
                    "bundle_sha256": sha256_file(self.bundle),
                    "bundle_size_bytes": self.bundle.stat().st_size,
                    "model_address": hot.EXPECTED_MODEL_ADDRESS,
                    "manifest_address": hot.EXPECTED_MANIFEST_ADDRESS,
                }
            ),
            encoding="utf-8",
        )
        self.store = FakeStore()
        with patch.object(hot, "publish_current_champion", return_value=self.deployment):
            hot.publish_hot_bundle(self.store, self.metadata, self.distribution)

    def tearDown(self):
        self.temp.cleanup()

    def reactivate(self):
        with patch.object(
            reactivate,
            "publish_current_champion",
            return_value=self.deployment,
        ):
            return reactivate.reactivate_current_hot_bundle(
                self.store,
                self.distribution,
                "WindowsPlayer",
            )

    def test_identical_current_pointer_is_idempotent(self):
        result = self.reactivate()
        self.assertFalse(result["pointer_changed"])
        self.assertEqual(result["deployment_id"], DEPLOYMENT_ID)
        self.assertEqual(result["model_id"], MODEL_ID)

    def test_stale_platform_pointer_is_atomically_reactivated(self):
        pointer_path = self.distribution / "current-WindowsPlayer.json"
        pointer_path.write_text(
            json.dumps(
                {
                    "schema_version": 1,
                    "identity_sha256": "f" * 64,
                    "identity": {"deployment_id": "deploy-" + "e" * 24},
                    "published_at": "stale",
                }
            ),
            encoding="utf-8",
        )

        result = self.reactivate()
        pointer = json.loads(pointer_path.read_text(encoding="utf-8"))

        self.assertTrue(result["pointer_changed"])
        self.assertEqual(pointer["identity"]["deployment_id"], DEPLOYMENT_ID)
        self.assertEqual(pointer["identity"]["model_id"], MODEL_ID)
        self.assertEqual(pointer["identity"]["platform"], "WindowsPlayer")
        self.assertEqual(pointer["identity"]["bundle_sha256"], sha256_file(self.bundle))

    def test_corrupted_retained_bundle_is_rejected_without_pointer_change(self):
        pointer_path = self.distribution / "current-WindowsPlayer.json"
        before = pointer_path.read_bytes()
        retained = self.distribution / "packages" / DEPLOYMENT_ID / "WindowsPlayer" / hot.HOT_BUNDLE_FILE
        retained.write_bytes(b"corrupt")

        with self.assertRaisesRegex(ValidationError, "byte size|SHA-256"):
            self.reactivate()

        self.assertEqual(pointer_path.read_bytes(), before)

    def test_retained_identity_for_different_model_is_rejected(self):
        package_metadata = (
            self.distribution
            / "packages"
            / DEPLOYMENT_ID
            / "WindowsPlayer"
            / hot.HOT_BUNDLE_METADATA_FILE
        )
        body = json.loads(package_metadata.read_text(encoding="utf-8"))
        body["identity"]["model_id"] = "bees-rl-v8-" + "e" * 24
        package_metadata.write_text(json.dumps(body), encoding="utf-8")

        with self.assertRaisesRegex(ValidationError, "does not exactly match"):
            self.reactivate()

    def test_unsupported_platform_is_rejected(self):
        with patch.object(
            reactivate,
            "publish_current_champion",
            return_value=self.deployment,
        ):
            with self.assertRaisesRegex(ValidationError, "Unsupported"):
                reactivate.reactivate_current_hot_bundle(
                    self.store,
                    self.distribution,
                    "WebGLPlayer",
                )


if __name__ == "__main__":
    unittest.main()
