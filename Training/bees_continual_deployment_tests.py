"""Focused tests for Training/bees_continual_deployment.py.

Run from the Bees Assets root:
    python Training\bees_continual_deployment_tests.py
"""

from __future__ import annotations

import copy
import json
from pathlib import Path
import tempfile
import unittest

from bees_continual_bootstrap import bootstrap_champion
from bees_continual_deployment import (
    build_current_champion_package,
    publish_current_champion,
)
from bees_continual_learning import (
    ContinualLearningError,
    ContinualLearningStore,
    ValidationError,
    sha256_file,
)


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


class DeploymentTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name) / "store"
        self.artifact_dir = Path(self.temp.name) / "artifacts"
        self.artifact_dir.mkdir()
        self.store = ContinualLearningStore(self.root, copy.deepcopy(TEST_CONFIG))
        self.store.initialize()

    def tearDown(self):
        self.temp.cleanup()

    def register(self, name: str, content: bytes, step: int, parent=None):
        path = self.artifact_dir / name
        path.write_bytes(content)
        return self.store.register_model(
            path,
            training_run_id="deployment-test",
            training_step=step,
            game_build_version="test-build",
            parent_model_id=parent,
            status="candidate",
        )

    @staticmethod
    def passing_report(candidate_id: str, champion_id: str):
        return {
            "candidate_model_id": candidate_id,
            "champion_model_id": champion_id,
            "candidate_vs_champion": {"wins": 2, "losses": 0, "draws": 0},
            "historical": [],
            "competencies": [],
            "behavior_sanity_passed": True,
            "runtime_compatible": True,
            "runtime_checks_passed": True,
        }

    def bootstrap(self, content=b"generation-zero"):
        model = self.register("generation-zero.onnx", content, 100)
        bootstrap_champion(
            self.store,
            model["model_id"],
            reason="Trusted generation-zero deployment baseline",
        )
        return self.store.get_model(model["model_id"])

    def promote_second(self, first):
        second = self.register(
            "second.onnx",
            b"second-champion",
            200,
            parent=first["model_id"],
        )
        evaluation = self.store.record_evaluation(
            self.passing_report(second["model_id"], first["model_id"])
        )
        self.assertTrue(evaluation["passed"])
        return self.store.promote(second["model_id"], evaluation["report_id"])

    def test_package_requires_a_current_champion(self):
        self.register("candidate.onnx", b"candidate", 10)
        with self.assertRaisesRegex(ValidationError, "No current champion"):
            build_current_champion_package(self.store)

    def test_generation_zero_package_is_content_addressed_and_idempotent(self):
        champion = self.bootstrap()
        first = build_current_champion_package(self.store)
        second = build_current_champion_package(self.store)

        self.assertEqual(first["deployment_id"], second["deployment_id"])
        self.assertEqual(first["model_id"], champion["model_id"])
        self.assertEqual(first["promotion_evidence_type"], "generation-zero-bootstrap")
        self.assertEqual(
            sha256_file(Path(first["model_path"])),
            champion["artifact_sha256"],
        )
        manifest = json.loads(Path(first["manifest_path"]).read_text(encoding="utf-8"))
        self.assertEqual(manifest["deployment_id"], first["deployment_id"])
        self.assertEqual(manifest["identity"]["policy_signature"], TEST_CONFIG["policy_signature"])
        self.assertEqual(
            manifest["identity"]["promotion_evidence"]["type"],
            "generation-zero-bootstrap",
        )

    def test_normally_promoted_package_requires_and_records_passing_evaluation(self):
        first = self.bootstrap()
        champion = self.promote_second(first)
        package = build_current_champion_package(self.store)

        self.assertEqual(package["model_id"], champion["model_id"])
        self.assertEqual(package["promotion_evidence_type"], "passing-evaluation")
        manifest = json.loads(Path(package["manifest_path"]).read_text(encoding="utf-8"))
        evidence = manifest["identity"]["promotion_evidence"]
        self.assertEqual(evidence["report_id"], champion["evaluation_report_id"])
        self.assertEqual(len(evidence["report_sha256"]), 64)
        self.assertTrue(evidence["promotion_policy_fingerprint"])

    def test_publish_pointer_is_idempotent_and_tracks_rollback(self):
        first = self.bootstrap()
        self.promote_second(first)

        published_second = publish_current_champion(self.store)
        repeated = publish_current_champion(self.store)
        self.assertTrue(published_second["pointer_changed"])
        self.assertFalse(repeated["pointer_changed"])
        self.assertEqual(repeated["deployment_id"], published_second["deployment_id"])

        rolled_back = self.store.rollback()
        self.assertEqual(rolled_back["model_id"], first["model_id"])
        published_first = publish_current_champion(self.store)
        self.assertTrue(published_first["pointer_changed"])
        self.assertEqual(published_first["model_id"], first["model_id"])
        self.assertNotEqual(published_first["deployment_id"], published_second["deployment_id"])

        pointer = json.loads(Path(published_first["pointer_path"]).read_text(encoding="utf-8"))
        self.assertEqual(pointer["identity"]["model_id"], first["model_id"])
        self.assertEqual(pointer["identity"]["deployment_id"], published_first["deployment_id"])

    def test_tampered_champion_artifact_is_rejected_before_packaging(self):
        champion = self.bootstrap()
        Path(champion["artifact_path"]).write_bytes(b"tampered")
        with self.assertRaisesRegex(ContinualLearningError, "integrity verification"):
            build_current_champion_package(self.store)

    def test_deployment_requires_frozen_policy_signature(self):
        champion = self.bootstrap()
        unsigned_config = copy.deepcopy(TEST_CONFIG)
        unsigned_config.pop("policy_signature")
        unsigned_store = ContinualLearningStore(self.root, unsigned_config)
        self.assertEqual(unsigned_store.current_champion_id(), champion["model_id"])
        with self.assertRaisesRegex(ValidationError, "policy_signature"):
            build_current_champion_package(unsigned_store)

    def test_deployment_rejects_non_onnx_champion_artifact(self):
        model = self.register("candidate.bin", b"not-onnx", 20)
        bootstrap_champion(self.store, model["model_id"], reason="test non-onnx rejection")
        with self.assertRaisesRegex(ValidationError, "must be ONNX"):
            build_current_champion_package(self.store)


if __name__ == "__main__":
    unittest.main()
