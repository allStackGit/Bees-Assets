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

from bees_continual_behavior_sanity import apply_behavior_sanity
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


class DeploymentTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name) / "store"
        self.artifact_dir = Path(self.temp.name) / "artifacts"
        self.artifact_dir.mkdir()
        self.store = ContinualLearningStore(self.root, copy.deepcopy(TEST_CONFIG))
        self.store.initialize()
        self.validated_onnx_paths = []

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

    def validate_synthetic_onnx(self, path: Path):
        self.validated_onnx_paths.append(Path(path))

    def package(self, store=None):
        return build_current_champion_package(
            store or self.store,
            onnx_validator=self.validate_synthetic_onnx,
        )

    def publish(self):
        return publish_current_champion(
            self.store,
            onnx_validator=self.validate_synthetic_onnx,
        )

    @staticmethod
    def candidate_summary(*, wins=2, losses=0, draws=0, timeouts=0, shots=2, hits=1, damage=10):
        matches = wins + losses + draws
        return {
            "matches": matches,
            "wins": wins,
            "losses": losses,
            "draws": draws,
            "timeouts": timeouts,
            "candidate_starting_tsv": matches * 10,
            "candidate_final_tsv": matches * 4,
            "candidate_shots": shots,
            "candidate_hits": hits,
            "candidate_damage": damage,
        }

    @classmethod
    def passing_report(cls, candidate_id: str, champion_id: str):
        report = {
            "candidate_model_id": candidate_id,
            "champion_model_id": champion_id,
            "candidate_vs_champion": cls.candidate_summary(),
            "historical": [],
            "competencies": [],
            "runtime_compatible": True,
            "runtime_checks_passed": True,
            "evaluator": {
                "authoritative_match_runner": True,
                "authoritative_telemetry_validated": True,
            },
        }
        return apply_behavior_sanity(report)

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
            self.package()

    def test_generation_zero_package_is_content_addressed_and_idempotent(self):
        champion = self.bootstrap()
        first = self.package()
        second = self.package()

        self.assertEqual(first["deployment_id"], second["deployment_id"])
        self.assertEqual(first["model_id"], champion["model_id"])
        self.assertEqual(first["promotion_evidence_type"], "generation-zero-bootstrap")
        self.assertEqual(
            sha256_file(Path(first["model_path"])),
            champion["artifact_sha256"],
        )
        self.assertEqual(
            self.validated_onnx_paths,
            [Path(champion["artifact_path"]), Path(champion["artifact_path"])],
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
        package = self.package()

        self.assertEqual(package["model_id"], champion["model_id"])
        self.assertEqual(package["promotion_evidence_type"], "passing-evaluation")
        manifest = json.loads(Path(package["manifest_path"]).read_text(encoding="utf-8"))
        evidence = manifest["identity"]["promotion_evidence"]
        self.assertEqual(evidence["report_id"], champion["evaluation_report_id"])
        self.assertEqual(len(evidence["report_sha256"]), 64)
        self.assertTrue(evidence["promotion_policy_fingerprint"])

    def test_registry_rejects_spoofed_behavior_pass_before_deployment(self):
        first = self.bootstrap()
        second = self.register(
            "timeout-champion.onnx",
            b"timeout-champion",
            200,
            parent=first["model_id"],
        )
        spoofed = {
            "candidate_model_id": second["model_id"],
            "champion_model_id": first["model_id"],
            "candidate_vs_champion": self.candidate_summary(
                wins=0,
                losses=0,
                draws=2,
                timeouts=2,
                shots=0,
                hits=0,
                damage=0,
            ),
            "historical": [],
            "competencies": [],
            "behavior_sanity_passed": True,
            "runtime_compatible": True,
            "runtime_checks_passed": True,
            "evaluator": {
                "authoritative_match_runner": True,
                "authoritative_telemetry_validated": True,
                "behavior_sanity": {
                    "schema_version": 1,
                    "passed": True,
                    "checks": {},
                    "reasons": [],
                    "match_groups": 1,
                    "totals": {},
                },
            },
        }

        with self.assertRaisesRegex(ValidationError, "behavior_sanity"):
            self.store.record_evaluation(spoofed)
        self.assertEqual(self.store.get_model(second["model_id"])["status"], "candidate")
        self.assertEqual(self.validated_onnx_paths, [])

    def test_publish_pointer_is_idempotent_and_tracks_rollback(self):
        first = self.bootstrap()
        self.promote_second(first)

        published_second = self.publish()
        repeated = self.publish()
        self.assertTrue(published_second["pointer_changed"])
        self.assertFalse(repeated["pointer_changed"])
        self.assertEqual(repeated["deployment_id"], published_second["deployment_id"])

        rolled_back = self.store.rollback()
        self.assertEqual(rolled_back["model_id"], first["model_id"])
        published_first = self.publish()
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
            self.package()
        self.assertEqual(self.validated_onnx_paths, [])

    def test_deployment_requires_frozen_policy_signature(self):
        champion = self.bootstrap()
        unsigned_config = copy.deepcopy(TEST_CONFIG)
        unsigned_config.pop("policy_signature")
        unsigned_store = ContinualLearningStore(self.root, unsigned_config)
        self.assertEqual(unsigned_store.current_champion_id(), champion["model_id"])
        with self.assertRaisesRegex(ValidationError, "policy_signature"):
            self.package(unsigned_store)

    def test_deployment_rejects_non_onnx_champion_artifact(self):
        model = self.register("candidate.bin", b"not-onnx", 20)
        bootstrap_champion(self.store, model["model_id"], reason="test non-onnx rejection")
        with self.assertRaisesRegex(ValidationError, "must be ONNX"):
            self.package()


if __name__ == "__main__":
    unittest.main()
