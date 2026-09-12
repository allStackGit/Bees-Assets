"""Focused tests for fail-closed public live-RL telemetry ingestion."""

from __future__ import annotations

import copy
import json
from pathlib import Path
import tempfile
import unittest

from bees_continual_bootstrap import bootstrap_champion
from bees_continual_learning import ContinualLearningStore, ValidationError, load_config
from bees_continual_live_telemetry_ingest import ingest_live_telemetry_payload


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
        "telemetry_observation_size": 4,
        "telemetry_continuous_action_count": 3,
        "telemetry_discrete_branch_sizes": [2, 3],
    },
}


class LiveTelemetryIngestTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name) / "store"
        artifact_dir = Path(self.temp.name) / "artifacts"
        artifact_dir.mkdir()
        self.store = ContinualLearningStore(self.root, copy.deepcopy(TEST_CONFIG))
        self.store.initialize()

        model_path = artifact_dir / "champion.onnx"
        model_path.write_bytes(b"telemetry-ingest-test-model")
        model = self.store.register_model(
            model_path,
            training_run_id="telemetry-ingest-test",
            training_step=100,
            game_build_version="training-build",
            status="candidate",
        )
        bootstrap_champion(self.store, model["model_id"], reason="telemetry ingest test baseline")
        self.model = self.store.get_model(model["model_id"])
        self.deployment_id = "deploy-" + "d" * 24
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

    def tearDown(self):
        self.temp.cleanup()

    def payload(self):
        return {
            "schema_version": 1,
            "match_id": "match-ingest-1",
            "game_build_version": "public-client-build",
            "mode": "campaign",
            "result": "bee_win",
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

    def test_valid_payload_is_archived_untrusted_and_exact_retry_is_idempotent(self):
        payload = self.payload()
        first = ingest_live_telemetry_payload(self.store, payload)
        archive = first["archive"]
        self.assertFalse(archive["duplicate"])
        self.assertFalse(archive["trusted_for_on_policy_rl"])
        self.assertEqual(first["validation"]["step_count"], 1)

        archive_path = Path(archive["archive_path"])
        self.assertTrue(archive_path.is_file())
        self.assertEqual(json.loads(archive_path.read_text(encoding="utf-8")), payload)

        retry = ingest_live_telemetry_payload(self.store, payload)
        self.assertTrue(retry["archive"]["duplicate"])
        self.assertEqual(retry["archive"]["batch_id"], archive["batch_id"])

    def test_reused_match_id_with_different_valid_payload_is_rejected(self):
        payload = self.payload()
        ingest_live_telemetry_payload(self.store, payload)

        conflicting = self.payload()
        conflicting["result"] = "draw"
        with self.assertRaisesRegex(ValidationError, "different payload content"):
            ingest_live_telemetry_payload(self.store, conflicting)

    def test_invalid_payload_does_not_reach_archive(self):
        payload = self.payload()
        payload["steps"][0]["continuous_action"][0] = 2.0
        with self.assertRaisesRegex(ValidationError, "action range"):
            ingest_live_telemetry_payload(self.store, payload)
        self.assertEqual(list((self.root / "experience" / "raw-live").glob("*.json")), [])

    def test_production_config_defines_frozen_v8_telemetry_dimensions(self):
        ingestion = load_config()["ingestion"]
        self.assertEqual(ingestion["telemetry_observation_size"], 4722)
        self.assertEqual(ingestion["telemetry_continuous_action_count"], 34)
        self.assertEqual(ingestion["telemetry_discrete_branch_sizes"], [16, 16, 5, 65, 65, 65])


if __name__ == "__main__":
    unittest.main()
