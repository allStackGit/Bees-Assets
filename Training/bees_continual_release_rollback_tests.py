"""Focused tests for Training/bees_continual_release_rollback.py."""

from __future__ import annotations

import copy
from pathlib import Path
import tempfile
import unittest

from bees_continual_bootstrap import bootstrap_champion
from bees_continual_learning import ContinualLearningStore, ValidationError
from bees_continual_release_rollback import ReleaseRollbackError, rollback_release


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


class ReleaseRollbackTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name) / "store"
        self.artifacts = Path(self.temp.name) / "artifacts"
        self.distribution = Path(self.temp.name) / "distribution"
        self.artifacts.mkdir()
        self.store = ContinualLearningStore(self.root, copy.deepcopy(TEST_CONFIG))
        self.store.initialize()
        self.published = []
        self.reactivated = []
        self.health_checks = []

    def tearDown(self):
        self.temp.cleanup()

    def register(self, name: str, payload: bytes, step: int, parent=None):
        path = self.artifacts / name
        path.write_bytes(payload)
        return self.store.register_model(
            path,
            training_run_id="rollback-test",
            training_step=step,
            game_build_version="test-build",
            parent_model_id=parent,
            status="candidate",
        )

    def establish_two_champions(self):
        first = self.register("first.onnx", b"first", 100)
        bootstrap_champion(self.store, first["model_id"], reason="rollback test baseline")
        first = self.store.get_model(first["model_id"])
        second = self.register("second.onnx", b"second", 200, parent=first["model_id"])
        report = {
            "candidate_model_id": second["model_id"],
            "champion_model_id": first["model_id"],
            "candidate_vs_champion": {"wins": 2, "losses": 0, "draws": 0},
            "historical": [],
            "competencies": [],
            "behavior_sanity_passed": True,
            "runtime_compatible": True,
            "runtime_checks_passed": True,
        }
        recorded = self.store.record_evaluation(report)
        self.assertTrue(recorded["passed"])
        second = self.store.promote(second["model_id"], recorded["report_id"])
        return first, second

    @staticmethod
    def deployment_id(model_id: str) -> str:
        return "deploy-test-" + model_id[-12:]

    def publisher(self, store):
        model_id = store.current_champion_id()
        self.published.append(model_id)
        return {
            "model_id": model_id,
            "deployment_id": self.deployment_id(model_id),
            "pointer_changed": True,
        }

    def bundle_reactivator(self, store, root, platform):
        model_id = store.current_champion_id()
        self.reactivated.append((model_id, Path(root), platform))
        return {
            "model_id": model_id,
            "deployment_id": self.deployment_id(model_id),
            "platform": platform,
            "pointer_changed": True,
        }

    def health_checker(self, store, *, distribution_root=None, platforms=()):
        model_id = store.current_champion_id()
        self.health_checks.append((model_id, distribution_root, tuple(platforms)))
        return {
            "status": "healthy",
            "current_champion_model_id": model_id,
            "deployment_id": self.deployment_id(model_id),
        }

    def rollback(self, **kwargs):
        return rollback_release(
            self.store,
            publisher=kwargs.pop("publisher", self.publisher),
            bundle_reactivator=kwargs.pop("bundle_reactivator", self.bundle_reactivator),
            health_checker=kwargs.pop("health_checker", self.health_checker),
            **kwargs,
        )

    def test_default_rollback_restores_previous_champion_and_requested_platforms(self):
        first, second = self.establish_two_champions()

        result = self.rollback(
            distribution_root=self.distribution,
            platforms=("WindowsPlayer", "LinuxPlayer"),
        )

        self.assertTrue(result["registry_changed"])
        self.assertEqual(result["starting_champion_model_id"], second["model_id"])
        self.assertEqual(result["current_champion_model_id"], first["model_id"])
        self.assertEqual(self.store.current_champion_id(), first["model_id"])
        self.assertEqual(self.published, [first["model_id"]])
        self.assertEqual(
            [entry[2] for entry in self.reactivated],
            ["WindowsPlayer", "LinuxPlayer"],
        )
        self.assertEqual(result["status"], "healthy")

    def test_explicit_current_target_is_idempotent_reconciliation(self):
        first, _second = self.establish_two_champions()
        self.store.rollback(first["model_id"])

        result = self.rollback(
            target_model_id=first["model_id"],
            distribution_root=self.distribution,
            platforms=("WindowsPlayer",),
        )

        self.assertFalse(result["registry_changed"])
        self.assertEqual(self.store.current_champion_id(), first["model_id"])
        self.assertEqual(self.published, [first["model_id"]])
        self.assertEqual(len(self.reactivated), 1)

    def test_release_failure_leaves_rollback_target_authoritative_and_explicit_retry_converges(self):
        first, _second = self.establish_two_champions()
        calls = []

        def failing_reactivator(store, root, platform):
            calls.append((store.current_champion_id(), Path(root), platform))
            raise ValidationError("synthetic distribution outage")

        with self.assertRaisesRegex(ReleaseRollbackError, "Rerun with --target"):
            self.rollback(
                distribution_root=self.distribution,
                platforms=("WindowsPlayer",),
                bundle_reactivator=failing_reactivator,
            )

        self.assertEqual(self.store.current_champion_id(), first["model_id"])
        self.assertEqual(calls[0][0], first["model_id"])

        recovered = self.rollback(
            target_model_id=first["model_id"],
            distribution_root=self.distribution,
            platforms=("WindowsPlayer",),
        )
        self.assertFalse(recovered["registry_changed"])
        self.assertEqual(recovered["status"], "healthy")
        self.assertEqual(self.store.current_champion_id(), first["model_id"])

    def test_platform_request_without_distribution_root_fails_before_registry_change(self):
        _first, second = self.establish_two_champions()

        with self.assertRaisesRegex(ValidationError, "distribution_root"):
            self.rollback(platforms=("WindowsPlayer",))

        self.assertEqual(self.store.current_champion_id(), second["model_id"])
        self.assertEqual(self.published, [])

    def test_unsupported_platform_fails_before_registry_change(self):
        _first, second = self.establish_two_champions()

        with self.assertRaisesRegex(ValidationError, "Unsupported"):
            self.rollback(distribution_root=self.distribution, platforms=("WebGLPlayer",))

        self.assertEqual(self.store.current_champion_id(), second["model_id"])

    def test_health_identity_failure_does_not_reverse_completed_registry_rollback(self):
        first, _second = self.establish_two_champions()

        def wrong_health(store, **_kwargs):
            return {
                "status": "healthy",
                "current_champion_model_id": store.current_champion_id(),
                "deployment_id": "different-deployment",
            }

        with self.assertRaisesRegex(ReleaseRollbackError, "Rerun with --target"):
            self.rollback(health_checker=wrong_health)

        self.assertEqual(self.store.current_champion_id(), first["model_id"])


if __name__ == "__main__":
    unittest.main()
