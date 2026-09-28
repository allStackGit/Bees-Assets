"""Focused regression tests for elastic WAN rollout scaling."""

from __future__ import annotations

import io
import os
import queue
import tempfile
import threading
import time
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest import mock

import bees_elastic_wan_actor_worker as actor_worker
import bees_elastic_wan_actor_session as actor_session
import bees_elastic_wan_training as elastic


class ElasticTrajectoryHorizonTests(unittest.TestCase):
    def test_completed_old_topology_trajectory_uses_fixed_configured_horizon(self):
        trajectory = SimpleNamespace(steps=[object() for _ in range(16)])
        trainer_settings = SimpleNamespace(time_horizon=64)

        # A topology increase may lower the live target below an already completed trajectory.
        # Such a batch is still valid when it stays within the fixed ML-Agents time_horizon.
        elastic.validate_trajectory_length(
            trajectory,
            trainer_settings,
            "BeesRL1v1?team=0",
        )

    def test_trajectory_above_configured_horizon_is_rejected(self):
        trajectory = SimpleNamespace(steps=[object() for _ in range(17)])
        trainer_settings = SimpleNamespace(time_horizon=16)

        with self.assertRaisesRegex(RuntimeError, "configured time_horizon 16"):
            elastic.validate_trajectory_length(
                trajectory,
                trainer_settings,
                "BeesRL1v1?team=0",
            )

    def test_hybrid_and_zero_local_learners_use_the_stable_horizon_guard(self):
        training_dir = Path(__file__).parent
        for filename in (
            "bees_elastic_wan_training.py",
            "bees_elastic_wan_zero_local.py",
        ):
            source = (training_dir / filename).read_text(encoding="utf-8")
            self.assertIn("validate_trajectory_length", source)
            self.assertNotIn(
                "len(trajectory.steps) > manager._max_trajectory_length",
                source,
            )


class ElasticActorClaimShutdownTests(unittest.TestCase):
    def test_shutdown_waits_for_claim_keeper_request_to_finish(self):
        session = actor_session.ElasticActorSession.__new__(
            actor_session.ElasticActorSession
        )
        session._claim_keeper_stop = threading.Event()
        keeper = mock.Mock()
        session._claim_keeper = keeper

        session._stop_claim_keeper()

        self.assertTrue(session._claim_keeper_stop.is_set())
        keeper.join.assert_called_once_with()
        self.assertIsNone(session._claim_keeper)

    def test_upload_loop_drains_queued_batch_after_shutdown_signal(self):
        session = actor_worker.ActorSession.__new__(actor_worker.ActorSession)
        session._upload_stop = threading.Event()
        session._upload_stop.set()
        session._upload_drain_deadline = time.monotonic() + 1.0
        session._upload_queue = queue.Queue()
        session._upload_queue.put({"trajectories": [object()]})
        session.stop = threading.Event()
        session.stop.set()
        session.client = mock.Mock()
        session._record_accepted_trajectories = mock.Mock()

        session._upload_loop()

        session.client.trajectories.assert_called_once()
        session._record_accepted_trajectories.assert_called_once()


    def test_orderly_close_drains_session_then_stops_and_releases_lease(self):
        session = actor_session.ElasticActorSession.__new__(
            actor_session.ElasticActorSession
        )
        session.client = mock.Mock()
        session.session_id = "session-1"
        session._claim_keeper_stop = threading.Event()
        events = []
        keeper = mock.Mock()
        keeper.join.side_effect = lambda: events.append("stop-renewal")
        session._claim_keeper = keeper
        session.client.release.side_effect = lambda session_id: events.append(
            f"release-{session_id}"
        )

        def drain_session(_instance):
            events.append("drain")

        with mock.patch.object(
            actor_session.worker.ActorSession,
            "close",
            autospec=True,
            side_effect=drain_session,
        ):
            session.close()

        self.assertEqual(events, ["drain", "stop-renewal", "release-session-1"])
        session.client.release.assert_called_once_with("session-1")


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


class ElasticActorHealthTests(unittest.TestCase):
    def test_broker_absence_is_healthy_wait_not_child_error(self):
        exc = actor_worker.worker.BrokerUnavailable("central release phase")
        with mock.patch.object(actor_worker, "write_managed_health") as health:
            actor_worker._write_waiting_for_central_health(exc)

        health.assert_called_once()
        state = health.call_args.args[0]
        details = health.call_args.kwargs["details"]
        self.assertEqual(state, "ready")
        self.assertEqual(details["phase"], "waiting-for-central")
        self.assertIn("BrokerUnavailable", details["last_broker_error"])


    def test_startup_health_heartbeat_refreshes_phase_until_ready(self):
        with mock.patch.object(actor_worker, "write_managed_health") as health:
            heartbeat = actor_worker._StartupHealthHeartbeat(
                actor_id=3,
                env_count=2,
                interval_seconds=60.0,
            )
            heartbeat.start()
            heartbeat.set_phase("starting-unity")
            heartbeat.stop()

        self.assertGreaterEqual(health.call_count, 2)
        first = health.call_args_list[0]
        latest = health.call_args_list[-1]
        self.assertEqual(first.args[0], "starting")
        self.assertEqual(first.kwargs["details"]["phase"], "starting-session")
        self.assertEqual(latest.args[0], "starting")
        self.assertEqual(latest.kwargs["details"]["phase"], "starting-unity")
        self.assertEqual(latest.kwargs["details"]["actor_id"], 3)
        self.assertEqual(latest.kwargs["details"]["env_count"], 2)

    def test_actor_marks_broker_wait_as_ready_before_session_polling(self):
        source = Path(actor_worker.__file__).read_text(encoding="utf-8")
        wait = source.index("raw_session = worker._wait_for_broker")
        ready = source.rindex(
            '"phase": "waiting-for-central"',
            0,
            wait,
        )
        self.assertLess(ready, wait)


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

    def test_actor_helper_rejects_boolean_session_dimensions(self):
        valid = {
            "max_actors": 12,
            "max_envs_per_actor": 64,
            "remote_worker_base": 32,
            "worker_stride": 64,
            "capacity_envs": 800,
        }
        for field, value in (
            ("max_actors", True),
            ("remote_worker_base", True),
            ("capacity_envs", True),
        ):
            malformed = dict(valid)
            malformed[field] = value
            if field == "capacity_envs":
                malformed["remote_worker_base"] = 0
            with self.subTest(field=field):
                with self.assertRaisesRegex(RuntimeError, "invalid|incompatible"):
                    actor_worker._elastic_session(
                        malformed,
                        actor_id=0,
                        env_count=4,
                    )


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
                elastic.ENVIRONMENT_ID_ENV: "e" * 64,
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
                "actor_instance_id": "manual-process-0",
                "env_count": 1,
                "control_epoch": 1,
                "behavior_specs": specs,
            }
        )
        broker.register_actor(
            {
                **broker.release_identity,
                "actor_id": 1,
                "actor_instance_id": "manual-process-1",
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
                    "actor_instance_id": "manual-process-0",
                    "env_count": 65,
                    "control_epoch": 1,
                    "behavior_specs": specs,
                }
            )

    def test_drain_is_fair_across_ready_actors(self):
        broker, specs = self._broker()
        for actor_id in (0, 1):
            broker.register_actor(
                {
                    **broker.release_identity,
                    "actor_id": actor_id,
                    "actor_instance_id": f"manual-process-{actor_id}",
                    "env_count": 8,
                    "control_epoch": 1,
                    "behavior_specs": specs,
                }
            )

        for step_count in (10, 11, 12, 13, 14):
            broker._trajectory_batches.put_nowait(
                {
                    "actor_id": 0,
                    "policy_versions": {},
                    "control_epoch": broker.control_epoch,
                    "trajectories": [object()],
                    "step_count": step_count,
                }
            )
        broker._trajectory_batches.put_nowait(
            {
                "actor_id": 1,
                "policy_versions": {},
                "control_epoch": broker.control_epoch,
                "trajectories": [object()],
                "step_count": 7,
            }
        )

        drained = broker.drain_current_batches(2)

        self.assertEqual([batch["actor_id"] for batch in drained], [0, 1])
        state = broker.wait_state(
            broker._policy_epoch,
            broker.control_epoch,
            0.0,
        )
        self.assertEqual(state["consumed_steps_by_actor"]["0"], 10)
        self.assertEqual(state["consumed_steps_by_actor"]["1"], 7)
        self.assertEqual(state["trajectory_queue_depth"], 4)

    def test_fair_drain_rotates_first_actor_between_calls(self):
        broker, specs = self._broker()
        for actor_id in (0, 1):
            broker.register_actor(
                {
                    **broker.release_identity,
                    "actor_id": actor_id,
                    "actor_instance_id": f"manual-process-{actor_id}",
                    "env_count": 8,
                    "control_epoch": 1,
                    "behavior_specs": specs,
                }
            )
        for actor_id in (0, 1, 0, 1):
            broker._trajectory_batches.put_nowait(
                {
                    "actor_id": actor_id,
                    "policy_versions": {},
                    "control_epoch": broker.control_epoch,
                    "trajectories": [object()],
                    "step_count": 1,
                }
            )

        first = broker.drain_current_batches(1)
        second = broker.drain_current_batches(1)

        self.assertEqual(first[0]["actor_id"], 0)
        self.assertEqual(second[0]["actor_id"], 1)

    def test_learner_consumption_counter_advances_only_when_batch_is_drained(self):
        broker, specs = self._broker()
        broker.register_actor(
            {
                **broker.release_identity,
                "actor_id": 0,
                "actor_instance_id": "manual-process-0",
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
            broker._policy_epoch,
            broker.control_epoch,
            0.0,
        )
        self.assertEqual(before["consumed_steps_by_actor"]["0"], 0)
        self.assertEqual(before["trajectory_queue_depth"], 1)

        drained = broker.drain_current_batches(1)
        self.assertEqual(len(drained), 1)
        after = broker.wait_state(
            broker._policy_epoch,
            broker.control_epoch,
            0.0,
        )
        self.assertEqual(after["consumed_steps_by_actor"]["0"], 37)
        self.assertEqual(after["trajectory_queue_depth"], 0)

    def test_orderly_release_frees_slot_and_fences_old_process(self):
        broker, specs = self._broker()
        old_owner = {
            **broker.release_identity,
            "actor_key": "machine-orderly",
            "actor_instance_id": "process-old",
            "env_count": 8,
        }
        actor_id = broker.claim_actor(old_owner)
        broker.register_actor(
            {
                **old_owner,
                "actor_id": actor_id,
                "control_epoch": broker.control_epoch,
                "behavior_specs": specs,
            }
        )

        broker.release_actor({**old_owner, "actor_id": actor_id})

        self.assertEqual(broker.active_actor_snapshot(), {})
        self.assertNotIn("machine-orderly", broker._claims)
        new_owner = {**old_owner, "actor_instance_id": "process-new"}
        self.assertEqual(broker.claim_actor(new_owner), actor_id)
        broker.register_actor(
            {
                **new_owner,
                "actor_id": actor_id,
                "control_epoch": broker.control_epoch,
                "behavior_specs": specs,
            }
        )

        with self.assertRaisesRegex(ValueError, "another remote process"):
            broker.release_actor({**old_owner, "actor_id": actor_id})
        self.assertEqual(broker.active_actor_snapshot(), {actor_id: 8})


    def test_claim_accepts_compatible_actor_from_a_different_build(self):
        broker, _specs = self._broker()
        payload = {
            **broker.release_identity,
            "actor_key": "machine-compatible",
            "actor_instance_id": "process-compatible",
            "env_count": 8,
        }
        payload["build_id"] = "elastic-build-other"
        actor_id = broker.claim_actor(payload)
        self.assertEqual(actor_id, 0)


    def test_stale_generation_takes_precedence_over_duplicate_ack(self):
        broker, specs = self._broker()
        broker.register_actor(
            {
                **broker.release_identity,
                "actor_id": 0,
                "actor_instance_id": "manual-process-0",
                "env_count": 8,
                "control_epoch": 1,
                "behavior_specs": specs,
            }
        )
        payload = {
            **broker.release_identity,
            "actor_id": 0,
            "actor_instance_id": "manual-process-0",
            "env_count": 8,
            "control_epoch": 1,
            "batch_id": "lookup-straddled-reset",
            "policy_versions": {},
            "trajectories": [],
        }
        with broker._condition:
            broker._remember_accepted_batch_locked(0, payload["batch_id"], 1)
        broker.request_reset({"difficulty": 2})
        with broker._condition:
            # Model a cached duplicate result captured across generation invalidation.
            broker._remember_accepted_batch_locked(0, payload["batch_id"], 1)

        with self.assertRaisesRegex(elastic.base.StaleActorStateError, "control epoch"):
            broker.submit_trajectory_batch(payload)

    def test_actor_accepts_different_build_but_rejects_different_semantic_lineage(self):
        expected = {
            "build_id": "build-a",
            "run_id": "run-a",
            "compatibility_key": "a" * 64,
            "environment_id": "e" * 64,
        }
        session = {"release_identity": dict(expected)}
        actor_worker._validate_session_release_identity(session, expected)

        # Compatible code releases may overlap compiled build ids during rolling replacement.
        session["release_identity"]["build_id"] = "build-b"
        actor_worker._validate_session_release_identity(session, expected)

        session["release_identity"]["compatibility_key"] = "b" * 64
        with self.assertRaisesRegex(RuntimeError, "does not match"):
            actor_worker._validate_session_release_identity(session, expected)

    def test_claim_rejects_actor_from_a_different_environment(self):
        broker, _specs = self._broker()
        payload = {
            **broker.release_identity,
            "actor_key": "machine-env-stale",
            "actor_instance_id": "process-env-stale",
            "env_count": 8,
        }
        payload["environment_id"] = "f" * 64
        with self.assertRaisesRegex(ValueError, "release identity"):
            broker.claim_actor(payload)
        self.assertEqual(broker.active_actor_snapshot(), {})

    def test_actor_rejects_session_from_a_different_environment(self):
        expected = {
            "build_id": "build-a",
            "run_id": "run-a",
            "compatibility_key": "a" * 64,
            "environment_id": "e" * 64,
        }
        session = {"release_identity": dict(expected)}
        session["release_identity"]["environment_id"] = "f" * 64
        with self.assertRaisesRegex(RuntimeError, "does not match"):
            actor_worker._validate_session_release_identity(session, expected)

    def test_central_claims_first_available_actor_slots(self):
        broker, _specs = self._broker()
        first = broker.claim_actor({**broker.release_identity, "actor_key": "machine-a", "actor_instance_id": "process-a", "env_count": 8})
        second = broker.claim_actor({**broker.release_identity, "actor_key": "machine-b", "actor_instance_id": "process-b", "env_count": 12})
        self.assertEqual(first, 0)
        self.assertEqual(second, 1)

    def test_registered_actor_instance_renews_its_slot(self):
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
            broker.claim_actor({**broker.release_identity, "actor_key": "machine-a", "actor_instance_id": "process-a", "env_count": 8}),
            actor_id,
        )
        self.assertEqual(
            broker.claim_actor({**broker.release_identity, "actor_key": "machine-b", "actor_instance_id": "process-b", "env_count": 4}),
            1,
        )

    def test_active_manual_slot_cannot_be_overwritten_by_another_process(self):
        broker, specs = self._broker()
        broker.register_actor(
            {
                **broker.release_identity,
                "actor_id": 0,
                "actor_instance_id": "manual-process-a",
                "env_count": 8,
                "control_epoch": 1,
                "behavior_specs": specs,
            }
        )

        with self.assertRaisesRegex(ValueError, "already registered by another process"):
            broker.register_actor(
                {
                    **broker.release_identity,
                    "actor_id": 0,
                    "actor_instance_id": "manual-process-b",
                    "env_count": 8,
                    "control_epoch": 1,
                    "behavior_specs": specs,
                }
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
        with self.assertRaisesRegex(ValueError, "claimed by another remote process"):
            broker.claim_actor(
                {**broker.release_identity, "actor_key": "machine-a", "actor_instance_id": "new-process", "env_count": 8}
            )
        broker._claims["machine-a"]["last_seen"] -= broker.options.actor_lease_seconds + 1
        broker._registrations[actor_id]["last_seen"] -= broker.options.actor_lease_seconds + 1
        self.assertEqual(broker.active_actor_snapshot(), {})
        self.assertEqual(
            broker.claim_actor(
                {**broker.release_identity, "actor_key": "machine-a", "actor_instance_id": "new-process", "env_count": 8}
            ),
            actor_id,
        )
        self.assertEqual(
            broker._claims["machine-a"]["actor_instance_id"],
            "new-process",
        )
        self.assertEqual(
            broker.session_payload()["actor_lease_seconds"],
            broker.options.actor_lease_seconds,
        )
        with self.assertRaisesRegex(ValueError, "claimed by another remote process"):
            broker.claim_actor(
                {
                    **broker.release_identity,
                    "actor_key": "machine-a",
                    "actor_instance_id": "old-process",
                    "env_count": 8,
                }
            )
        self.assertEqual(broker.active_actor_snapshot(), {})
        with self.assertRaisesRegex(ValueError, "actor lease expired"):
            broker.submit_trajectory_batch({"actor_id": actor_id})
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
        self.assertEqual(
            broker.claim_actor(
                {**broker.release_identity, "actor_key": "machine-a", "actor_instance_id": "new-process", "env_count": 8}
            ),
            actor_id,
        )
        self.assertIn("machine-a", broker._claims)
        with self.assertRaisesRegex(ValueError, "claimed by another remote process"):
            broker.claim_actor(
                {**broker.release_identity, "actor_key": "machine-a", "actor_instance_id": "old-process", "env_count": 8}
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


    def test_invalid_control_epochs_do_not_renew_or_register_actor(self):
        broker, specs = self._broker()
        broker.register_actor(
            {
                **broker.release_identity,
                "actor_id": 0,
                "actor_instance_id": "manual-process-0",
                "env_count": 8,
                "control_epoch": broker.control_epoch,
                "behavior_specs": specs,
            }
        )
        last_seen = broker._registrations[0]["last_seen"]
        stale_ack = {
            **broker.release_identity,
            "actor_id": 0,
            "actor_instance_id": "manual-process-0",
            "control_epoch": broker.control_epoch - 1,
        }

        with self.assertRaisesRegex(elastic.base.StaleActorStateError, "control epoch"):
            broker.acknowledge_reset(stale_ack)

        self.assertEqual(broker._registrations[0]["last_seen"], last_seen)
        for stale_epoch in (broker.control_epoch - 1, True):
            stale_batch = {
                **broker.release_identity,
                "actor_id": 0,
                "actor_instance_id": "manual-process-0",
                "control_epoch": stale_epoch,
                "policy_versions": {},
                "trajectories": [],
            }
            with self.subTest(control_epoch=stale_epoch):
                with self.assertRaisesRegex(elastic.base.StaleActorStateError, "control epoch"):
                    broker.submit_trajectory_batch(stale_batch)
            self.assertEqual(broker._registrations[0]["last_seen"], last_seen)

        with self.assertRaisesRegex(elastic.base.StaleActorStateError, "control epoch"):
            broker.acknowledge_reset(
                {
                    **broker.release_identity,
                    "actor_id": 0,
                    "actor_instance_id": "manual-process-0",
                    "control_epoch": True,
                }
            )
        with self.assertRaisesRegex(elastic.base.StaleActorStateError, "control epoch"):
            broker.register_actor(
                {
                    **broker.release_identity,
                    "actor_id": 1,
                    "actor_instance_id": "manual-process-1",
                    "env_count": 4,
                    "control_epoch": True,
                    "behavior_specs": specs,
                }
            )
        self.assertNotIn(1, broker._registrations)
        self.assertEqual(broker._registrations[0]["last_seen"], last_seen)


class ActorFailureDiagnosticsTests(unittest.TestCase):
    def test_managed_stop_request_sets_actor_stop_event(self):
        with tempfile.TemporaryDirectory() as temp:
            request = Path(temp) / "managed-stop.request"
            request.write_text("stop\n", encoding="ascii")
            stop = actor_worker.threading.Event()

            actor_worker._watch_managed_stop_request(
                request,
                stop,
                poll_seconds=0.0,
            )

            self.assertTrue(stop.is_set())

    def test_session_failure_telemetry_reports_count_type_and_age(self):
        telemetry = actor_worker._SessionFailureTelemetry()
        with mock.patch.object(
            actor_worker.time,
            "monotonic",
            side_effect=[100.0, 107.5],
        ):
            telemetry.record(IndexError("bad action batch"))
            snapshot = telemetry.snapshot()

        self.assertEqual(snapshot["session_failures_total"], 1)
        self.assertEqual(snapshot["last_session_failure_type"], "IndexError")
        self.assertEqual(snapshot["seconds_since_last_session_failure"], 7.5)

    def test_reconnect_backoff_is_bounded_and_resettable(self):
        backoff = actor_worker._ReconnectBackoff(5.0, max_seconds=30.0)
        self.assertEqual(
            [backoff.next_delay() for _ in range(5)],
            [5.0, 10.0, 20.0, 30.0, 30.0],
        )
        backoff.reset()
        self.assertEqual(backoff.next_delay(), 5.0)

    def test_session_failure_reports_context_and_full_traceback(self):
        stderr = io.StringIO()
        session = SimpleNamespace(
            actor_id=2,
            env_count=3,
            worker_offset=128,
            total_envs=7,
            policy_epoch=11,
            control_epoch=5,
            topology_epoch=4,
            policy_versions={"BeesRL1v1?team=0": 11},
            manager=SimpleNamespace(env_workers=[]),
        )
        error = IndexError("index 15 is out of bounds for axis 0 with size 15")

        with (
            mock.patch.object(actor_worker.sys, "stderr", stderr),
            mock.patch.object(actor_worker.traceback, "print_exception") as print_exception,
        ):
            actor_worker._report_session_failure(error, session)

        output = stderr.getvalue()
        self.assertIn("IndexError", output)
        self.assertIn('"actor_id": 2', output)
        self.assertIn('"policy_epoch": 11', output)
        print_exception.assert_called_once()
        self.assertIs(print_exception.call_args.args[1], error)


class CapacityDiagnosticTests(unittest.TestCase):
    def test_trainer_rate_expires_when_progress_samples_are_stale(self):
        diagnostics = elastic.CapacityDiagnostics(local_envs=2)
        diagnostics._trainer_samples.extend([(100.0, 0), (110.0, 100)])

        rate = diagnostics.trainer_rate(
            now=110.0 + elastic.TRAINING_RATE_WINDOW_SECONDS + 1.0
        )

        self.assertIsNone(rate)

    def test_backpressure_reports_total_and_recent_window(self):
        diagnostics = elastic.CapacityDiagnostics(local_envs=2)
        diagnostics.observe_backpressure(now=100.0)
        diagnostics.observe_backpressure(now=130.0)
        diagnostics.observe_backpressure(now=165.0)

        recent, per_minute = diagnostics.backpressure_recent(now=170.0)

        self.assertEqual(diagnostics._backpressure_total, 3)
        self.assertEqual(recent, 2)
        self.assertAlmostEqual(per_minute, 2.0)

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


class ElasticWanUnityArgumentTests(unittest.TestCase):
    def test_wan_options_after_env_args_are_preserved_for_unity(self):
        argv = [
            "config.yaml",
            "--bees-wan-actors=2",
            "--bees-wan-auth-token-file=token",
            "--env-args",
            "--bees-wan-min-actors=1",
        ]

        cleaned, options = elastic.extract_elastic_wan_options(argv)

        self.assertEqual(cleaned, [
            "config.yaml",
            "--env-args",
            "--bees-wan-min-actors=1",
        ])
        self.assertEqual(options.min_actors, elastic.DEFAULT_MIN_REMOTE_ACTORS)

    def test_env_args_equals_form_is_rejected(self):
        with self.assertRaisesRegex(SystemExit, "Use --env-args as a separate token"):
            elastic.extract_elastic_wan_options([
                "config.yaml",
                "--bees-wan-actors=2",
                "--bees-wan-auth-token-file=token",
                "--env-args=--bees-wan-min-actors=1",
            ])
