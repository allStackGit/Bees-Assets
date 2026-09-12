"""Focused tests for the strict public live-RL telemetry schema."""

from __future__ import annotations

import copy
import json
from pathlib import Path
import tempfile
import unittest

from bees_continual_bootstrap import bootstrap_champion
from bees_continual_learning import CompatibilityError, ContinualLearningStore, ValidationError
from bees_continual_live_telemetry import validate_live_telemetry_payload


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


class LiveTelemetryValidationTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name) / "store"
        self.artifacts = Path(self.temp.name) / "artifacts"
        self.artifacts.mkdir()
        self.store = ContinualLearningStore(self.root, copy.deepcopy(TEST_CONFIG))
        self.store.initialize()
        model_path = self.artifacts / "champion.onnx"
        model_path.write_bytes(b"telemetry-test-model")
        model = self.store.register_model(
            model_path,
            training_run_id="telemetry-test",
            training_step=100,
            game_build_version="training-build",
            status="candidate",
        )
        bootstrap_champion(self.store, model["model_id"], reason="telemetry test baseline")
        self.model = self.store.get_model(model["model_id"])
        self.deployment_id = "deploy-" + "a" * 24
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
            "match_id": "match-telemetry-1",
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
                    "agent_key": "side-0:ship-17",
                    "decision_index": 0,
                    "observation": [0.0, 0.25, -0.5, 1.0],
                    "continuous_action": [0.0, -1.0, 1.0],
                    "discrete_action": [1, 2],
                },
                {
                    "agent_key": "side-1:ship-44",
                    "decision_index": 0,
                    "observation": [1.0, 0.0, 0.5, -0.25],
                    "continuous_action": [0.1, 0.2, -0.3],
                    "discrete_action": [0, 1],
                },
                {
                    "agent_key": "side-0:ship-17",
                    "decision_index": 1,
                    "observation": [0.2, 0.3, 0.4, 0.5],
                    "continuous_action": [-0.1, 0.0, 0.1],
                    "discrete_action": [0, 0],
                },
            ],
        }

    def test_exact_known_deployment_and_action_abi_are_accepted(self):
        result = validate_live_telemetry_payload(self.store, self.payload())
        self.assertEqual(result["match_id"], "match-telemetry-1")
        self.assertEqual(result["model_id"], self.model["model_id"])
        self.assertEqual(result["deployment_id"], self.deployment_id)
        self.assertEqual(result["step_count"], 3)
        self.assertEqual(result["agent_count"], 2)
        self.assertEqual(result["discrete_branch_sizes"], [2, 3])

    def test_unknown_or_wrong_deployment_model_is_rejected(self):
        payload = self.payload()
        payload["deployment_id"] = "deploy-" + "b" * 24
        with self.assertRaisesRegex(CompatibilityError, "unknown deployment"):
            validate_live_telemetry_payload(self.store, payload)

        payload = self.payload()
        manifest = (
            self.root / "deployment" / "packages" / self.deployment_id / "manifest.json"
        )
        body = json.loads(manifest.read_text(encoding="utf-8"))
        body["identity"]["model_id"] = "bees-rl-v8-" + "c" * 24
        manifest.write_text(json.dumps(body), encoding="utf-8")
        with self.assertRaisesRegex(CompatibilityError, "does not contain the declared model_id"):
            validate_live_telemetry_payload(self.store, payload)

    def test_policy_signature_and_model_hash_are_bound(self):
        payload = self.payload()
        payload["policy_signature"] = "wrong-signature"
        with self.assertRaisesRegex(CompatibilityError, "policy_signature"):
            validate_live_telemetry_payload(self.store, payload)

        payload = self.payload()
        payload["model_sha256"] = "f" * 64
        with self.assertRaisesRegex(CompatibilityError, "model_sha256"):
            validate_live_telemetry_payload(self.store, payload)

    def test_observation_shape_and_finite_values_are_enforced(self):
        payload = self.payload()
        payload["steps"][0]["observation"] = [0.0]
        with self.assertRaisesRegex(ValidationError, "exactly 4"):
            validate_live_telemetry_payload(self.store, payload)

        payload = self.payload()
        payload["steps"][0]["observation"][2] = float("nan")
        with self.assertRaisesRegex(ValidationError, "finite"):
            validate_live_telemetry_payload(self.store, payload)

    def test_continuous_and_discrete_action_bounds_are_enforced(self):
        payload = self.payload()
        payload["steps"][0]["continuous_action"][1] = 1.01
        with self.assertRaisesRegex(ValidationError, "action range"):
            validate_live_telemetry_payload(self.store, payload)

        payload = self.payload()
        payload["steps"][0]["discrete_action"][0] = 2
        with self.assertRaisesRegex(ValidationError, "branch range"):
            validate_live_telemetry_payload(self.store, payload)

    def test_per_agent_decision_indexes_must_increase_even_when_interleaved(self):
        payload = self.payload()
        payload["steps"][2]["decision_index"] = 0
        with self.assertRaisesRegex(ValidationError, "must increase"):
            validate_live_telemetry_payload(self.store, payload)

    def test_empty_steps_invalid_result_and_wrong_schema_fail_closed(self):
        payload = self.payload()
        payload["steps"] = []
        with self.assertRaisesRegex(ValidationError, "non-empty"):
            validate_live_telemetry_payload(self.store, payload)

        payload = self.payload()
        payload["result"] = "victory-ish"
        with self.assertRaisesRegex(ValidationError, "result must be one of"):
            validate_live_telemetry_payload(self.store, payload)

        payload = self.payload()
        payload["schema_version"] = 2
        with self.assertRaises(CompatibilityError):
            validate_live_telemetry_payload(self.store, payload)

    def test_configured_payload_and_step_limits_are_enforced(self):
        payload = self.payload()
        strict = copy.deepcopy(TEST_CONFIG)
        strict["ingestion"]["max_steps_per_match"] = 2
        store = ContinualLearningStore(self.root, strict)
        with self.assertRaisesRegex(ValidationError, "step count"):
            validate_live_telemetry_payload(store, payload)

        strict = copy.deepcopy(TEST_CONFIG)
        strict["ingestion"]["max_payload_bytes"] = 100
        store = ContinualLearningStore(self.root, strict)
        with self.assertRaisesRegex(ValidationError, "maximum size"):
            validate_live_telemetry_payload(store, payload)


if __name__ == "__main__":
    unittest.main()
