"""Focused tests for Training/bees_continual_release.py.

Run from the Bees Assets root:
    python Training\bees_continual_release_tests.py
"""

from __future__ import annotations

import copy
from pathlib import Path
import tempfile
import unittest

from bees_continual_bootstrap import bootstrap_champion
from bees_continual_learning import ContinualLearningStore, ValidationError
from bees_continual_release import ReleaseError, run_release_cycle


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


class ReleaseCycleTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name) / "store"
        self.artifact_dir = Path(self.temp.name) / "artifacts"
        self.artifact_dir.mkdir()
        self.store = ContinualLearningStore(self.root, copy.deepcopy(TEST_CONFIG))
        self.store.initialize()
        self.published_model_ids = []
        self.health_checked_model_ids = []

    def tearDown(self):
        self.temp.cleanup()

    def register(self, name: str, content: bytes, step: int, parent=None):
        path = self.artifact_dir / name
        path.write_bytes(content)
        return self.store.register_model(
            path,
            training_run_id="release-test",
            training_step=step,
            game_build_version="test-build",
            parent_model_id=parent,
            status="candidate",
        )

    def bootstrap(self):
        model = self.register("generation-zero.onnx", b"generation-zero", 100)
        bootstrap_champion(
            self.store,
            model["model_id"],
            reason="Trusted release-cycle generation zero",
        )
        return self.store.get_model(model["model_id"])

    def publisher(self, store):
        model_id = store.current_champion_id()
        self.published_model_ids.append(model_id)
        return {
            "model_id": model_id,
            "deployment_id": f"deploy-test-{len(self.published_model_ids)}",
            "pointer_changed": True,
        }

    def health_checker(self, store):
        model_id = store.current_champion_id()
        self.health_checked_model_ids.append(model_id)
        return {
            "status": "healthy",
            "current_champion_model_id": model_id,
            "deployment_id": f"deploy-test-{len(self.published_model_ids)}",
        }

    @staticmethod
    def _evaluation(store, candidate_model_id: str, *, passed: bool):
        champion_id = store.current_champion_id()
        report = {
            "candidate_model_id": candidate_model_id,
            "champion_model_id": champion_id,
            "candidate_vs_champion": (
                {"wins": 2, "losses": 0, "draws": 0}
                if passed
                else {"wins": 0, "losses": 2, "draws": 0}
            ),
            "historical": [],
            "competencies": [],
            "behavior_sanity_passed": True,
            "runtime_compatible": True,
            "runtime_checks_passed": True,
        }
        recorded = store.record_evaluation(report)
        return {"report": report, "recorded": recorded, "league_updates": []}

    @classmethod
    def passing_evaluator(cls, store, *, candidate_model_id, **_kwargs):
        return cls._evaluation(store, candidate_model_id, passed=True)

    @classmethod
    def failing_evaluator(cls, store, *, candidate_model_id, **_kwargs):
        return cls._evaluation(store, candidate_model_id, passed=False)

    def run_cycle(self, **kwargs):
        return run_release_cycle(
            self.store,
            environment_path="unused-test-environment",
            evaluator=kwargs.pop("evaluator", self.passing_evaluator),
            publisher=kwargs.pop("publisher", self.publisher),
            health_checker=kwargs.pop("health_checker", self.health_checker),
            **kwargs,
        )

    def test_default_cycle_processes_only_newest_compatible_candidate(self):
        first = self.bootstrap()
        older = self.register("older.onnx", b"older", 200, parent=first["model_id"])
        newer = self.register("newer.onnx", b"newer", 300, parent=first["model_id"])

        result = self.run_cycle()

        self.assertEqual(result["status"], "processed")
        self.assertEqual(result["initial_release_health_status"], "healthy")
        self.assertEqual(len(result["processed"]), 1)
        self.assertEqual(result["processed"][0]["candidate_model_id"], newer["model_id"])
        self.assertEqual(result["processed"][0]["decision"], "promoted")
        self.assertEqual(result["processed"][0]["release_health_status"], "healthy")
        self.assertEqual(self.store.current_champion_id(), newer["model_id"])
        self.assertEqual(self.store.get_model(older["model_id"])["status"], "candidate")
        self.assertEqual(self.published_model_ids, [first["model_id"], newer["model_id"]])
        self.assertEqual(self.health_checked_model_ids, [first["model_id"], newer["model_id"]])

    def test_failed_candidate_is_rejected_without_becoming_deployment(self):
        first = self.bootstrap()
        candidate = self.register("failure.onnx", b"failure", 200, parent=first["model_id"])

        result = self.run_cycle(evaluator=self.failing_evaluator)

        self.assertEqual(result["processed"][0]["decision"], "rejected")
        self.assertEqual(self.store.get_model(candidate["model_id"])["status"], "rejected")
        self.assertEqual(self.store.current_champion_id(), first["model_id"])
        self.assertEqual(self.published_model_ids, [first["model_id"]])
        self.assertEqual(self.health_checked_model_ids, [first["model_id"]])

    def test_unhealthy_starting_release_blocks_evaluation_and_leaves_candidate_queued(self):
        first = self.bootstrap()
        candidate = self.register("blocked.onnx", b"blocked", 200, parent=first["model_id"])
        evaluator_calls = []

        def evaluator(*args, **kwargs):
            evaluator_calls.append((args, kwargs))
            return self.passing_evaluator(*args, **kwargs)

        def unhealthy(_store):
            raise ValidationError("synthetic deployment corruption")

        with self.assertRaisesRegex(ReleaseError, "Release health validation failed"):
            self.run_cycle(evaluator=evaluator, health_checker=unhealthy)

        self.assertEqual(evaluator_calls, [])
        self.assertEqual(self.store.current_champion_id(), first["model_id"])
        self.assertEqual(self.store.get_model(candidate["model_id"])["status"], "candidate")
        self.assertEqual(self.published_model_ids, [first["model_id"]])

    def test_health_checker_identity_mismatch_blocks_evaluation(self):
        first = self.bootstrap()
        candidate = self.register("identity-blocked.onnx", b"identity-blocked", 200, parent=first["model_id"])

        def wrong_identity(_store):
            return {
                "status": "healthy",
                "current_champion_model_id": "bees-rl-v8-wrong",
                "deployment_id": "deploy-test-1",
            }

        with self.assertRaisesRegex(ReleaseError, "champion does not match"):
            self.run_cycle(health_checker=wrong_identity)

        self.assertEqual(self.store.get_model(candidate["model_id"])["status"], "candidate")
        self.assertEqual(self.store.current_champion_id(), first["model_id"])

    def test_evaluation_failure_leaves_candidate_and_champion_unchanged(self):
        first = self.bootstrap()
        candidate = self.register("retry.onnx", b"retry", 200, parent=first["model_id"])

        def broken_evaluator(*_args, **_kwargs):
            raise ValidationError("synthetic evaluator outage")

        with self.assertRaisesRegex(ValidationError, "synthetic evaluator outage"):
            self.run_cycle(evaluator=broken_evaluator)

        self.assertEqual(self.store.get_model(candidate["model_id"])["status"], "candidate")
        self.assertEqual(self.store.current_champion_id(), first["model_id"])
        self.assertEqual(self.published_model_ids, [first["model_id"]])
        self.assertEqual(self.health_checked_model_ids, [first["model_id"]])

    def test_publication_failure_after_promotion_is_reconciled_on_next_cycle(self):
        first = self.bootstrap()
        candidate = self.register("publish-retry.onnx", b"publish-retry", 200, parent=first["model_id"])
        calls = []

        def fail_after_initial_reconcile(store):
            model_id = store.current_champion_id()
            calls.append(model_id)
            if len(calls) == 2:
                raise ValidationError("synthetic publisher outage")
            return {
                "model_id": model_id,
                "deployment_id": "deploy-initial",
                "pointer_changed": False,
            }

        def initial_health(store):
            return {
                "status": "healthy",
                "current_champion_model_id": store.current_champion_id(),
                "deployment_id": "deploy-initial",
            }

        with self.assertRaisesRegex(ReleaseError, "deployment publication/health validation failed"):
            self.run_cycle(
                publisher=fail_after_initial_reconcile,
                health_checker=initial_health,
            )

        self.assertEqual(calls, [first["model_id"], candidate["model_id"]])
        self.assertEqual(self.store.current_champion_id(), candidate["model_id"])
        self.assertEqual(self.store.get_model(candidate["model_id"])["status"], "champion")

        recovered = self.run_cycle(publisher=self.publisher, health_checker=self.health_checker)
        self.assertEqual(recovered["status"], "idle")
        self.assertEqual(recovered["current_champion_model_id"], candidate["model_id"])
        self.assertEqual(self.published_model_ids, [candidate["model_id"]])
        self.assertEqual(self.health_checked_model_ids, [candidate["model_id"]])

    def test_health_failure_after_promotion_is_reconciled_on_next_cycle(self):
        first = self.bootstrap()
        candidate = self.register("health-retry.onnx", b"health-retry", 200, parent=first["model_id"])
        checks = []

        def fail_second_health(store):
            model_id = store.current_champion_id()
            checks.append(model_id)
            if len(checks) == 2:
                raise ValidationError("synthetic post-promotion corruption")
            return {
                "status": "healthy",
                "current_champion_model_id": model_id,
                "deployment_id": f"deploy-test-{len(self.published_model_ids)}",
            }

        with self.assertRaisesRegex(ReleaseError, "deployment publication/health validation failed"):
            self.run_cycle(health_checker=fail_second_health)

        self.assertEqual(checks, [first["model_id"], candidate["model_id"]])
        self.assertEqual(self.store.current_champion_id(), candidate["model_id"])
        self.assertEqual(self.store.get_model(candidate["model_id"])["status"], "champion")
        self.assertEqual(self.published_model_ids, [first["model_id"], candidate["model_id"]])

        recovered = self.run_cycle()
        self.assertEqual(recovered["status"], "idle")
        self.assertEqual(recovered["initial_release_health_status"], "healthy")
        self.assertEqual(self.store.current_champion_id(), candidate["model_id"])

    def test_required_competency_suite_blocks_before_publication_when_source_is_missing(self):
        first = self.bootstrap()
        strict_config = copy.deepcopy(TEST_CONFIG)
        strict_config["promotion"]["min_competency_cases"] = 1
        strict_store = ContinualLearningStore(self.root, strict_config)
        strict_store.pin_competency_suite(
            {
                "schema_version": 1,
                "cases": [
                    {
                        "name": "generation-zero-regression",
                        "opponent_model_id": first["model_id"],
                        "matches": 1,
                        "minimum": 0.0,
                        "metric": "score_rate",
                        "critical": True,
                        "env_args": [],
                    }
                ],
            }
        )
        published = []

        with self.assertRaisesRegex(ReleaseError, "requires --competency-suite"):
            run_release_cycle(
                strict_store,
                environment_path="unused-test-environment",
                evaluator=self.passing_evaluator,
                publisher=lambda _store: published.append(True) or {},
            )

        self.assertEqual(published, [])

    def test_invalid_candidate_limit_fails_before_state_changes(self):
        self.bootstrap()
        with self.assertRaisesRegex(ValidationError, "max_candidates"):
            self.run_cycle(max_candidates=0)


if __name__ == "__main__":
    unittest.main()
