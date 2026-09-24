"""Focused tests for platform-specific champion AssetBundle publication."""

from __future__ import annotations

import json
from pathlib import Path
from types import SimpleNamespace
import tempfile
import unittest
from unittest.mock import patch

import bees_continual_hot_bundle as hot
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


class HotBundlePublicationTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.build_dir = self.root / "unity"
        self.build_dir.mkdir()
        self.bundle = self.build_dir / "bees-rl-policy"
        self.bundle.write_bytes(b"unity-asset-bundle")
        self.deployment_manifest = self.root / "deployment-manifest.json"
        self.deployment_manifest.write_text(
            json.dumps({"identity": {"model_sha256": MODEL_SHA}}),
            encoding="utf-8",
        )
        self.metadata = self.build_dir / "bees-rl-policy.metadata.json"
        self.write_metadata()
        self.deployment = {
            "deployment_id": DEPLOYMENT_ID,
            "model_id": MODEL_ID,
            "manifest_path": str(self.deployment_manifest),
            "manifest_sha256": MANIFEST_SHA,
        }
        self.store = FakeStore()

    def tearDown(self):
        self.temp.cleanup()

    def write_metadata(self, **overrides):
        body = {
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
        body.update(overrides)
        self.metadata.write_text(json.dumps(body), encoding="utf-8")

    def publish(self):
        with patch.object(hot, "publish_current_champion", return_value=self.deployment):
            return hot.publish_hot_bundle(
                self.store,
                self.metadata,
                self.root / "distribution",
            )

    def test_verified_bundle_is_published_immutably_with_platform_pointer(self):
        result = self.publish()
        published = Path(result["bundle_path"])
        pointer = json.loads(Path(result["pointer_path"]).read_text(encoding="utf-8"))

        self.assertEqual(published.read_bytes(), self.bundle.read_bytes())
        self.assertEqual(pointer["identity"]["deployment_id"], DEPLOYMENT_ID)
        self.assertEqual(pointer["identity"]["platform"], "WindowsPlayer")
        self.assertEqual(pointer["identity"]["bundle_sha256"], sha256_file(self.bundle))
        self.assertEqual(
            pointer["identity"]["bundle_path"],
            f"packages/{DEPLOYMENT_ID}/WindowsPlayer/{hot.HOT_BUNDLE_FILE}",
        )
        self.assertTrue(result["pointer_changed"])

    def test_identical_republication_is_idempotent(self):
        first = self.publish()
        second = self.publish()
        self.assertEqual(first["pointer_identity_sha256"], second["pointer_identity_sha256"])
        self.assertFalse(second["pointer_changed"])

    def test_bundle_for_noncurrent_deployment_is_rejected(self):
        self.write_metadata(deployment_id="deploy-" + "e" * 24)
        with self.assertRaises(ValidationError):
            self.publish()

    def test_bundle_hash_or_size_mismatch_is_rejected(self):
        self.write_metadata(bundle_sha256="f" * 64)
        with self.assertRaises(ValidationError):
            self.publish()

        self.write_metadata(bundle_size_bytes=self.bundle.stat().st_size + 1)
        with self.assertRaises(ValidationError):
            self.publish()

    def test_wrong_policy_contract_is_rejected(self):
        self.write_metadata(policy_abi_version=7)
        with self.assertRaises(ValidationError):
            self.publish()
        self.write_metadata(policy_signature="other")
        with self.assertRaises(ValidationError):
            self.publish()

    def test_webgl_or_wrong_build_target_is_not_publishable(self):
        self.write_metadata(platform="WebGLPlayer", build_target="WebGL")
        with self.assertRaises(ValidationError):
            self.publish()
        self.write_metadata(platform="WindowsPlayer", build_target="StandaloneLinux64")
        with self.assertRaises(ValidationError):
            self.publish()

    def test_asset_addresses_are_part_of_runtime_contract(self):
        self.write_metadata(model_address="different")
        with self.assertRaises(ValidationError):
            self.publish()


if __name__ == "__main__":
    unittest.main()
