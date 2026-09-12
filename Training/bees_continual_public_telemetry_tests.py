"""Focused tests for authenticated public live-RL telemetry quarantine import."""

from __future__ import annotations

import copy
import json
from pathlib import Path
import tempfile
import unittest

from bees_continual_bootstrap import bootstrap_champion
from bees_continual_learning import ContinualLearningStore, ValidationError, sha256_bytes
from bees_continual_public_telemetry import ingest_public_telemetry_quarantine


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
    "public_live_telemetry": {
        "max_batches_per_contributor": 2,
    },
    "ingestion": {
        "max_payload_bytes": 1024 * 1024,
        "max_steps_per_match": 100,
        "telemetry_observation_size": 4,
        "telemetry_continuous_action_count": 3,
        "telemetry_discrete_branch_sizes": [2, 3],
    },
}


class PublicTelemetryQuarantineTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        base = Path(self.temp.name)
        self.root = base / "store"
        self.incoming = base / "incoming"
        self.incoming.mkdir()
        artifacts = base / "artifacts"
        artifacts.mkdir()
        self.store = ContinualLearningStore(self.root, copy.deepcopy(TEST_CONFIG))
        self.store.initialize()

        model_path = artifacts / "champion.onnx"
        model_path.write_bytes(b"public-telemetry-model")
        model = self.store.register_model(
            model_path,
            training_run_id="public-telemetry-test",
            training_step=100,
            game_build_version="training-build",
            status="candidate",
        )
        bootstrap_champion(self.store, model["model_id"], reason="public telemetry baseline")
        self.model = self.store.get_model(model["model_id"])
        self.deployment_id = "deploy-" + "e" * 24
        package = self.root / "deployment" / "packages" / self.deployment_id
        package.mkdir(parents=True)
        (package / "manifest.json").write_text(
            json.dumps(
                {
                    "schema_version": 1,
                    "deployment_id": self.deployment_id,
                    "identity": {
                        "model_id": self.model["model_id"],
                        "model_sha256": self.model["artifact_sha256"],
                        "policy_signature": TEST_CONFIG["policy_signature"],
                    },
                }
            ),
            encoding="utf-8",
        )
        self.user_id = "76561198000000000"

    def tearDown(self):
        self.temp.cleanup()

    def payload(self, *, match_id="match-public-1", result="bee_win"):
        return {
            "schema_version": 1,
            "match_id": match_id,
            "game_build_version": "public-build",
            "mode": "campaign",
            "result": result,
            "model_id": self.model["model_id"],
            "model_sha256": self.model["artifact_sha256"],
            "deployment_id": self.deployment_id,
            "policy_signature": TEST_CONFIG["policy_signature"],
            "behavior_name": TEST_CONFIG["behavior_name"],
            "policy_abi_version": TEST_CONFIG["policy_abi_version"],
            "observation_schema_version": TEST_CONFIG["observation_schema_version"],
            "action_schema_version": TEST_CONFIG["action_schema_version"],
            "reward_schema_version": TEST_CONFIG["reward_schema_version"],
            "scenario_schema_version": TEST_CONFIG["scenario_schema_version"],
            "steps": [
                {
                    "agent_key": "side-0:ship-1",
                    "decision_index": 0,
                    "observation": [0.0, 0.25, -0.5, 1.0],
                    "continuous_action": [0.0, -1.0, 1.0],
                    "discrete_action": [1, 2],
                }
            ],
        }

    def write_quarantine(
        self,
        payload,
        *,
        directory=None,
        metadata_overrides=None,
        user_id=None,
    ):
        directory = Path(directory) if directory is not None else self.incoming
        directory.mkdir(parents=True, exist_ok=True)
        uploader_user_id = self.user_id if user_id is None else str(user_id)
        match_id = payload["match_id"]
        batch_hash = sha256_bytes(f"{uploader_user_id}\n{match_id}\n".encode("utf-8"))[:32]
        batch_id = f"rl-telemetry-{batch_hash}"
        payload_bytes = json.dumps(payload, sort_keys=True, separators=(",", ":")).encode("utf-8")
        payload_hash = sha256_bytes(payload_bytes)
        payload_path = directory / f"{batch_id}.json"
        payload_path.write_bytes(payload_bytes)
        metadata = {
            "schemaVersion": 1,
            "batchId": batch_id,
            "matchId": match_id,
            "uploaderUserId": uploader_user_id,
            "gameBuildVersion": payload["game_build_version"],
            "payloadSha256": payload_hash,
            "payloadBytes": len(payload_bytes),
            "modelId": payload["model_id"],
            "modelSha256": payload["model_sha256"],
            "deploymentId": payload["deployment_id"],
            "policyAbiVersion": payload["policy_abi_version"],
            "policySignature": payload["policy_signature"],
            "trust": "authenticated-quarantine",
            "readyForIngestion": False,
        }
        if metadata_overrides:
            metadata.update(metadata_overrides)
        metadata_path = directory / f"{batch_id}.metadata.json"
        metadata_path.write_text(json.dumps(metadata), encoding="utf-8")
        return metadata_path, payload_path

    def test_valid_quarantine_is_revalidated_archived_untrusted_and_drops_raw_user_identity(self):
        metadata_path, _ = self.write_quarantine(self.payload())
        result = ingest_public_telemetry_quarantine(self.store, metadata_path)

        archive = result["archive"]
        self.assertFalse(archive["trusted_for_on_policy_rl"])
        archived_payload = json.loads(Path(archive["archive_path"]).read_text(encoding="utf-8"))
        self.assertNotIn("uploaderUserId", archived_payload)
        self.assertNotIn(self.user_id, json.dumps(archived_payload))

        provenance = json.loads(Path(result["public_provenance_path"]).read_text(encoding="utf-8"))
        self.assertEqual(provenance["source_trust"], "authenticated-quarantine")
        self.assertFalse(provenance["trusted_for_on_policy_rl"])
        self.assertNotIn(self.user_id, json.dumps(provenance))

        contributor = json.loads(
            Path(result["public_contributor_record_path"]).read_text(encoding="utf-8")
        )
        self.assertEqual(len(contributor["contributor_bucket"]), 64)
        self.assertNotIn(self.user_id, json.dumps(contributor))

    def test_contributor_bucket_is_stable_per_user_and_distinct_between_users(self):
        first_path, _ = self.write_quarantine(
            self.payload(match_id="match-contributor-1"),
            directory=Path(self.temp.name) / "contributor-1",
        )
        second_path, _ = self.write_quarantine(
            self.payload(match_id="match-contributor-2"),
            directory=Path(self.temp.name) / "contributor-2",
        )
        third_path, _ = self.write_quarantine(
            self.payload(match_id="match-contributor-3"),
            directory=Path(self.temp.name) / "contributor-3",
            user_id="76561198000000001",
        )
        first = ingest_public_telemetry_quarantine(self.store, first_path)
        second = ingest_public_telemetry_quarantine(self.store, second_path)
        third = ingest_public_telemetry_quarantine(self.store, third_path)

        def bucket(result):
            return json.loads(
                Path(result["public_contributor_record_path"]).read_text(encoding="utf-8")
            )["contributor_bucket"]

        self.assertEqual(bucket(first), bucket(second))
        self.assertNotEqual(bucket(first), bucket(third))

    def test_hash_or_sidecar_identity_mismatch_is_rejected_before_archive(self):
        metadata_path, payload_path = self.write_quarantine(self.payload())
        payload_path.write_bytes(payload_path.read_bytes() + b"tamper")
        with self.assertRaisesRegex(ValidationError, "size mismatch"):
            ingest_public_telemetry_quarantine(self.store, metadata_path)
        self.assertEqual(list((self.root / "experience" / "raw-live").glob("telemetry-*.json")), [])

        other = Path(self.temp.name) / "identity-mismatch"
        metadata_path, _ = self.write_quarantine(
            self.payload(),
            directory=other,
            metadata_overrides={"deploymentId": "deploy-" + "f" * 24},
        )
        with self.assertRaisesRegex(ValidationError, "deployment_id"):
            ingest_public_telemetry_quarantine(self.store, metadata_path)

    def test_quarantine_cannot_claim_preapproval(self):
        metadata_path, _ = self.write_quarantine(
            self.payload(),
            metadata_overrides={"readyForIngestion": True},
        )
        with self.assertRaisesRegex(ValidationError, "must not claim readyForIngestion"):
            ingest_public_telemetry_quarantine(self.store, metadata_path)

    def test_strict_step_validation_still_runs_after_server_quarantine_checks(self):
        payload = self.payload()
        payload["steps"][0]["continuous_action"][1] = 1.5
        metadata_path, _ = self.write_quarantine(payload)
        with self.assertRaisesRegex(ValidationError, "action range"):
            ingest_public_telemetry_quarantine(self.store, metadata_path)
        self.assertEqual(list((self.root / "experience" / "raw-live").glob("telemetry-*.json")), [])

    def test_central_archive_rejects_same_match_with_different_valid_content(self):
        first_dir = Path(self.temp.name) / "first"
        second_dir = Path(self.temp.name) / "second"
        first_metadata, _ = self.write_quarantine(self.payload(), directory=first_dir)
        ingest_public_telemetry_quarantine(self.store, first_metadata)

        second_metadata, _ = self.write_quarantine(
            self.payload(result="draw"),
            directory=second_dir,
        )
        with self.assertRaisesRegex(ValidationError, "different payload content"):
            ingest_public_telemetry_quarantine(self.store, second_metadata)


if __name__ == "__main__":
    unittest.main()
