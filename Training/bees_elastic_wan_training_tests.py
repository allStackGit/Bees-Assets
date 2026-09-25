"""Focused regression tests for elastic WAN rollout scaling."""

from __future__ import annotations

import os
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest import mock

import bees_elastic_wan_actor_worker as actor_worker
import bees_elastic_wan_training as elastic


class FakeObservationSpec:
    shape = (4,)
    dimension_property = ()
    observation_type = "DEFAULT"


class FakeActionSpec:
    continuous_size = 2
    discrete_branches = (3, 2)


class FakeBehaviorSpec:
    observation_specs = (FakeObservationSpec(),)
    action_spec = FakeActionSpec()


class ElasticWanOptionTests(unittest.TestCase):
    def _token(self, root: str) -> str:
        path = Path(root) / "token.txt"
        path.write_text("0123456789abcdef0123456789abcdef\n", encoding="utf-8")
        return str(path)

    def test_actor_count_is_capacity_not_required_online_count(self):
        with tempfile.TemporaryDirectory() as temp:
            cleaned, options = elastic.extract_elastic_wan_options(
                [
                    "config.yaml",
                    "--num-envs=32",
                    "--bees-wan-actors=12",
                    "--bees-wan-min-actors=0",
                    f"--bees-wan-auth-token-file={self._token(temp)}",
                ]
            )
        self.assertEqual(cleaned, ["config.yaml", "--num-envs=32"])
        self.assertEqual(options.max_actors, 12)
        self.assertEqual(options.min_actors, 0)
        self.assertTrue(options.enabled)

    def test_more_than_twelve_actor_slots_is_rejected(self):
        with self.assertRaisesRegex(SystemExit, "between 1 and 12"):
            elastic.extract_elastic_wan_options(
                ["config.yaml", "--bees-wan-actors=13"]
            )

    def test_fixed_envs_per_actor_setting_is_obsolete(self):
        with self.assertRaisesRegex(SystemExit, "obsolete"):
            elastic.extract_elastic_wan_options(
                [
                    "config.yaml",
                    "--bees-wan-actors=12",
                    "--bees-wan-envs-per-actor=32",
                ]
            )


class ElasticWorkerIdentityTests(unittest.TestCase):
    def setUp(self):
        self.options = elastic.ElasticWanOptions(max_actors=12, auth_token_file="token")

    def test_each_actor_gets_a_fixed_sixty_four_worker_id_slot(self):
        self.assertEqual(
            elastic.actor_worker_ids(self.options, 0, 1, 32),
            (32,),
        )
        actor_one = elastic.actor_worker_ids(self.options, 1, 64, 32)
        self.assertEqual(actor_one[0], 96)
        self.assertEqual(actor_one[-1], 159)
        actor_eleven = elastic.actor_worker_ids(self.options, 11, 7, 32)
        self.assertEqual(actor_eleven[0], 736)
        self.assertEqual(actor_eleven[-1], 742)

    def test_actor_environment_count_is_bounded_one_through_sixty_four(self):
        with self.assertRaises(ValueError):
            elastic.actor_worker_ids(self.options, 0, 0, 32)
        with self.assertRaises(ValueError):
            elastic.actor_worker_ids(self.options, 0, 65, 32)

    def test_actor_helper_adapts_session_to_its_own_env_count(self):
        session = {
            "max_actors": 12,
            "max_envs_per_actor": 64,
            "remote_worker_base": 32,
            "worker_stride": 64,
            "capacity_envs": 800,
        }
        compatible, worker_offset, capacity = actor_worker._elastic_session(
            session,
            actor_id=3,
            env_count=17,
        )
        self.assertEqual(compatible["actor_count"], 12)
        self.assertEqual(compatible["envs_per_actor"], 17)
        self.assertEqual(worker_offset, 32 + 3 * 64)
        self.assertEqual(capacity, 800)


class ElasticBrokerTests(unittest.TestCase):
    def _broker(self):
        options = elastic.ElasticWanOptions(
            max_actors=12,
            min_actors=0,
            auth_token_file="unused",
            actor_lease_seconds=120.0,
        )
        run_options = SimpleNamespace(
            checkpoint_settings=SimpleNamespace(run_id="elastic-test")
        )
        with mock.patch.dict(
            os.environ,
            {
                elastic.BUILD_ID_ENV: "elastic-build",
                elastic.RUN_ID_ENV: "elastic-test",
                elastic.COMPATIBILITY_KEY_ENV: "c" * 64,
            },
            clear=False,
        ):
            broker = elastic.ElasticWanBroker(
                options,
                run_options,
                "0123456789abcdef0123456789abcdef",
                local_envs=32,
            )
        specs = {"BeesRL1v1?team=0": FakeBehaviorSpec()}
        broker.set_reference_behavior_specs(specs)
        broker.initialize_control({})
        return broker, specs

    def test_zero_remote_actors_is_a_valid_local_training_state(self):
        broker, specs = self._broker()
        self.assertEqual(broker.active_actor_snapshot(), {})
        self.assertEqual(set(broker.merged_behavior_specs()), set(specs))
        session = broker.session_payload()
        self.assertEqual(session["remote_worker_base"], 32)
        self.assertEqual(session["capacity_envs"], 32 + 12 * 64)

    def test_mixed_remote_environment_counts_register_together(self):
        broker, specs = self._broker()
        broker.register_actor(
            {
                **broker.release_identity,
                "actor_id": 0,
                "env_count": 1,
                "control_epoch": 1,
                "behavior_specs": specs,
            }
        )
        broker.register_actor(
            {
                **broker.release_identity,
                "actor_id": 1,
                "env_count": 64,
                "control_epoch": 1,
                "behavior_specs": specs,
            }
        )
        self.assertEqual(broker.active_actor_snapshot(), {0: 1, 1: 64})

    def test_registration_rejects_more_than_sixty_four_envs(self):
        broker, specs = self._broker()
        with self.assertRaisesRegex(ValueError, "1,64"):
            broker.register_actor(
                {
                    **broker.release_identity,
                    "actor_id": 0,
                    "env_count": 65,
                    "control_epoch": 1,
                    "behavior_specs": specs,
                }
            )

    def test_learner_consumption_counter_advances_only_when_batch_is_drained(self):
        broker, specs = self._broker()
        broker.register_actor(
            {
                **broker.release_identity,
                "actor_id": 0,
                "env_count": 8,
                "control_epoch": 1,
                "behavior_specs": specs,
            }
        )
        broker._trajectory_batches.put_nowait(
            {
                "actor_id": 0,
                "policy_versions": {},
                "control_epoch": broker.control_epoch,
                "trajectories": [object()],
                "step_count": 37,
            }
        )

        before = broker.wait_state(
            broker.policy_epoch,
            broker.control_epoch,
            0.0,
        )
        self.assertEqual(before["consumed_steps_by_actor"]["0"], 0)
        self.assertEqual(before["trajectory_queue_depth"], 1)

        drained = broker.drain_current_batches(1)
        self.assertEqual(len(drained), 1)
        after = broker.wait_state(
            broker.policy_epoch,
            broker.control_epoch,
            0.0,
        )
        self.assertEqual(after["consumed_steps_by_actor"]["0"], 37)
        self.assertEqual(after["trajectory_queue_depth"], 0)

    def test_claim_rejects_actor_from_a_different_release(self):
        broker, _specs = self._broker()
        payload = {
            **broker.release_identity,
            "actor_key": "machine-stale",
            "actor_instance_id": "process-stale",
            "env_count": 8,
        }
        payload["compatibility_key"] = "d" * 64
        with self.assertRaisesRegex(ValueError, "release identity"):
            broker.claim_actor(payload)
        self.assertEqual(broker.active_actor_snapshot(), {})

    def test_actor_rejects_session_from_a_different_release(self):
        expected = {
            "build_id": "build-a",
            "run_id": "run-a",
            "compatibility_key": "a" * 64,
        }
        session = {"release_identity": dict(expected)}
        actor_worker._validate_session_release_identity(session, expected)

        # Compatible rolling releases may use a different executable build while preserving
        # the same semantic run lineage.
        session["release_identity"]["build_id"] = "build-b"
        actor_worker._validate_session_release_identity(session, expected)

        session["release_identity"]["compatibility_key"] = "b" * 64
        with self.assertRaisesRegex(RuntimeError, "does not match"):
            actor_worker._validate_session_release_identity(session, expected)

    def test_central_claims_first_available_actor_slots(self):
        broker, _specs = self._broker()
        first = broker.claim_actor({**broker.release_identity, "actor_key": "machine-a", "actor_instance_id": "process-a", "env_count": 8})
        second = broker.claim_actor({**broker.release_identity, "actor_key": "machine-b", "actor_instance_id": "process-b", "env_count": 12})
        self.assertEqual(first, 0)
        self.assertEqual(second, 1)

    def test_same_remote_identity_reclaims_its_slot(self):
        broker, specs = self._broker()
        actor_id = broker.claim_actor({**broker.release_identity, "actor_key": "machine-a", "actor_instance_id": "process-a", "env_count": 8})
        broker.register_actor(
            {
                **broker.release_identity,
                "actor_id": actor_id,
                "actor_key": "machine-a",
                "actor_instance_id": "process-a",
                "env_count": 8,
                "control_epoch": 1,
                "behavior_specs": specs,
            }
        )
        self.assertEqual(
            broker.claim_actor({**broker.release_identity, "actor_key": "machine-a", "actor_instance_id": "process-a2", "env_count": 16}),
            actor_id,
        )
        self.assertEqual(
            broker.claim_actor({**broker.release_identity, "actor_key": "machine-b", "actor_instance_id": "process-b", "env_count": 4}),
            1,
        )

    def test_claimed_slot_cannot_be_registered_by_another_identity(self):
        broker, specs = self._broker()
        actor_id = broker.claim_actor({**broker.release_identity, "actor_key": "machine-a", "actor_instance_id": "process-a", "env_count": 8})
        with self.assertRaisesRegex(ValueError, "no active claim|owned by another"):
            broker.register_actor(
                {
                    **broker.release_identity,
                    "actor_id": actor_id,
                    "actor_key": "machine-b",
                    "actor_instance_id": "process-b",
                    "env_count": 8,
                    "control_epoch": 1,
                    "behavior_specs": specs,
                }
            )


    def test_reclaim_transfers_slot_to_new_process_instance(self):
        broker, specs = self._broker()
        actor_id = broker.claim_actor(
            {
                **broker.release_identity,
                "actor_key": "machine-a",
                "actor_instance_id": "old-process",
                "env_count": 8,
            }
        )
        broker.register_actor(
            {
                **broker.release_identity,
                "actor_id": actor_id,
                "actor_key": "machine-a",
                "actor_instance_id": "old-process",
                "env_count": 8,
                "control_epoch": 1,
                "behavior_specs": specs,
            }
        )
        self.assertEqual(
            broker.claim_actor(
                {**broker.release_identity, "actor_key": "machine-a", "actor_instance_id": "new-process", "env_count": 8}
            ),
            actor_id,
        )
        broker.register_actor(
            {
                **broker.release_identity,
                "actor_id": actor_id,
                "actor_key": "machine-a",
                "actor_instance_id": "new-process",
                "env_count": 8,
                "control_epoch": 1,
                "behavior_specs": specs,
            }
        )
        with self.assertRaisesRegex(ValueError, "another remote process"):
            broker.acknowledge_reset(
                {
                    **broker.release_identity,
                    "actor_id": actor_id,
                    "actor_key": "machine-a",
                    "actor_instance_id": "old-process",
                    "control_epoch": 1,
                }
            )


class CapacityDiagnosticTests(unittest.TestCase):
    def test_meaningful_gain_reports_beneficial(self):
        status, gain = elastic.classify_capacity(
            100.0,
            112.0,
            queue_ratio=0.0,
            backpressure_events=0,
        )
        self.assertEqual(status, "beneficial")
        self.assertAlmostEqual(gain, 0.12)

    def test_low_gain_with_queue_pressure_marks_exeter_saturated(self):
        status, gain = elastic.classify_capacity(
            100.0,
            101.0,
            queue_ratio=0.75,
            backpressure_events=3,
        )
        self.assertEqual(status, "exeter-saturated")
        self.assertAlmostEqual(gain, 0.01)

    def test_low_gain_without_queue_pressure_is_not_called_central_saturation(self):
        status, _ = elastic.classify_capacity(
            100.0,
            101.0,
            queue_ratio=0.0,
            backpressure_events=0,
        )
        self.assertEqual(status, "no-measurable-gain")


if __name__ == "__main__":
    unittest.main()
