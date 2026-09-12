"""Focused tests for Training/bees_continual_unity_bundle.py.

Run from the Bees Assets root:
    python Training\bees_continual_unity_bundle_tests.py
"""

from __future__ import annotations

import copy
import json
from pathlib import Path
import tempfile
import unittest

from bees_continual_bootstrap import bootstrap_champion
from bees_continual_learning import ContinualLearningStore, ValidationError, sha256_file
from bees_continual_unity_bundle import install_current_deployment_assets


TEST_CONFIG = {
    "behavior_name": "BeesRL1v1",
    "policy_abi_version": 7,
    "policy_signature": "test-policy-v7-signature",
    "observation_schema_version": 7,
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


class UnityBundleTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        base = Path(self.temp.name)
        self.store = ContinualLearningStore(base / "store", copy.deepcopy(TEST_CONFIG))
        self.store.initialize()
        self.artifact_dir = base / "artifacts"
        self.artifact_dir.mkdir()
        self.assets_root = base / "Bees-Assets"
        (self.assets_root / "Scripts" / "Scenes").mkdir(parents=True)
        (self.assets_root / "Scripts" / "Scenes" / "RlPolicySchema.cs").write_text(
            "// test sentinel\n", encoding="utf-8"
        )

    def tearDown(self):
        self.temp.cleanup()

    def register(self, name: str, content: bytes, step: int, parent=None):
        path = self.artifact_dir / name
        path.write_bytes(content)
        return self.store.register_model(
            path,
            training_run_id="unity-bundle-test",
            training_step=step,
            game_build_version="test-build",
            parent_model_id=parent,
            status="candidate",
        )

    def bootstrap(self):
        model = self.register("first.onnx", b"first-champion", 100)
        bootstrap_champion(self.store, model["model_id"], reason="test baseline")
        return self.store.get_model(model["model_id"])

    def promote_second(self, first):
        second = self.register("second.onnx", b"second-champion", 200, parent=first["model_id"])
        report = self.store.record_evaluation(
            {
                "candidate_model_id": second["model_id"],
                "champion_model_id": first["model_id"],
                "candidate_vs_champion": {"wins": 2, "losses": 0, "draws": 0},
                "historical": [],
                "competencies": [],
                "behavior_sanity_passed": True,
                "runtime_compatible": True,
                "runtime_checks_passed": True,
            }
        )
        self.assertTrue(report["passed"])
        return self.store.promote(second["model_id"], report["report_id"])

    def test_installs_exact_published_model_and_manifest(self):
        champion = self.bootstrap()
        installed = install_current_deployment_assets(self.store, self.assets_root)

        model_path = Path(installed["model_asset_source"])
        manifest_path = Path(installed["manifest_asset_source"])
        self.assertTrue(model_path.is_file())
        self.assertTrue(manifest_path.is_file())
        self.assertEqual(sha256_file(model_path), champion["artifact_sha256"])
        self.assertEqual(sha256_file(manifest_path), installed["manifest_sha256"])
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        self.assertEqual(manifest["deployment_id"], installed["deployment_id"])
        self.assertEqual(manifest["identity"]["model_id"], champion["model_id"])
        self.assertEqual(installed["resources_model_path"], "RlPolicy/BeesRL1v1")
        self.assertEqual(installed["resources_manifest_path"], "RlPolicy/BeesRL1v1Deployment")

    def test_new_champion_replaces_build_staging_bytes(self):
        first = self.bootstrap()
        first_install = install_current_deployment_assets(self.store, self.assets_root)
        first_model_hash = sha256_file(Path(first_install["model_asset_source"]))

        second = self.promote_second(first)
        second_install = install_current_deployment_assets(self.store, self.assets_root)
        self.assertNotEqual(first_install["deployment_id"], second_install["deployment_id"])
        self.assertEqual(sha256_file(Path(second_install["model_asset_source"])), second["artifact_sha256"])
        self.assertNotEqual(first_model_hash, second["artifact_sha256"])

    def test_rollback_restores_previous_champion_build_staging_bytes(self):
        first = self.bootstrap()
        self.promote_second(first)
        install_current_deployment_assets(self.store, self.assets_root)

        rolled_back = self.store.rollback()
        installed = install_current_deployment_assets(self.store, self.assets_root)
        self.assertEqual(installed["model_id"], rolled_back["model_id"])
        self.assertEqual(
            sha256_file(Path(installed["model_asset_source"])),
            rolled_back["artifact_sha256"],
        )

    def test_refuses_to_write_outside_a_bees_assets_root(self):
        self.bootstrap()
        wrong_root = Path(self.temp.name) / "not-assets"
        wrong_root.mkdir()
        with self.assertRaisesRegex(ValidationError, "RlPolicySchema"):
            install_current_deployment_assets(self.store, wrong_root)

    def test_replaces_tampered_staging_file_with_authoritative_package(self):
        champion = self.bootstrap()
        installed = install_current_deployment_assets(self.store, self.assets_root)
        model_path = Path(installed["model_asset_source"])
        model_path.write_bytes(b"tampered-build-staging")

        repaired = install_current_deployment_assets(self.store, self.assets_root)
        self.assertEqual(repaired["deployment_id"], installed["deployment_id"])
        self.assertEqual(sha256_file(model_path), champion["artifact_sha256"])


if __name__ == "__main__":
    unittest.main()
