"""Focused regression tests for elastic WAN rollout scaling."""

from __future__ import annotations

import io
import os
import queue
import tempfile
import threading
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest import mock

import bees_elastic_wan_actor_session as actor_session
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

    def test_local_actor_slot_can_extend_broker_capacity_to_thirteen(self):
        with tempfile.TemporaryDirectory() as temp:
            _cleaned, options = elastic.extract_elastic_wan_options(
                [
                    "config.yaml",
                    "--bees-wan-actors=13",
                    f"--bees-wan-auth-token-file={self._token(temp)}",
                ]
            )
        self.assertEqual(options.max_actors, 13)

    def test_more_than_thirteen_actor_slots_is_rejected(self):
        with self.assertRaisesRegex(SystemExit, "between 1 and 13"):
            elastic.extract_elastic_wan_options(
                ["config.yaml", "--bees-wan-actors=14"]
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


class ElasticActorStaleResyncTests(unittest.TestCase):
    def test_elastic_heartbeat_treats_stale_ack_as_resync(self):
        session = actor_session.ElasticActorSession.__new__(
            actor_session.ElasticActorSession
        )
        session.client = mock.Mock()
        session.client.reset_ack.side_effect = actor_worker.worker.BrokerStaleActor(
            "central epoch advanced"
        )
        session.session_id = "session"
        session.actor_id = 2
        session.control_epoch = 7
        session._state_changed = threading.Event()

        self.assertFalse(session._heartbeat())
        self.assertTrue(session._state_changed.is_set())

    def test_base_reset_ack_race_keeps_actor_paused_for_resync(self):
        session = actor_worker.worker.ActorSession.__new__(
            actor_worker.worker.ActorSession
        )
        session.client = mock.Mock()
        session.client.state.return_value = {
            "control_epoch": 2,
            "policy_epoch": 1,
            "policy_versions": {},
        }
        session.client.control.return_value = {
            "epoch": 2,
            "kind": "reset",
            "config": {"difficulty": 2},
        }
        session.client.reset_ack.side_effect = actor_worker.worker.BrokerStaleActor(
            "central epoch advanced again"
        )
        session.session_id = "session"
        session.actor_id = 0
        session.control_epoch = 1
        session.policy_epoch = 1
        session.policy_versions = {}
        session.templates = {}
        session.manager = mock.Mock()
        session.worker_offset = 0
        session.central_run_options = SimpleNamespace(
            env_settings=SimpleNamespace(timeout_wait=1.0)
        )
        session.stop = threading.Event()
        session._upload_queue = queue.Queue()
        session._state_changed = threading.Event()
        session._stale = threading.Event()

        with (
            mock.patch.object(
                actor_worker.worker,
                "_drain_inflight_without_training",
            ),
            mock.patch.object(actor_worker.worker, "_clear_partial_trajectories"),
            mock.patch.object(actor_worker.worker, "_drain_queue"),
            mock.patch.object(actor_worker.worker, "_remap_manager_initial_steps"),
        ):
            session._synchronize_state()

        self.assertTrue(session._state_changed.is_set())
        self.assertTrue(session._stale.is_set())
        self.assertEqual(session.control_epoch, 1)
        session.manager.reset.assert_called_once_with(config={"difficulty": 2})

class ElasticActorLiveResizeTests(unittest.TestCase):
    def _session(self, manager, env_count: int):
        session = actor_session.ElasticActorSession.__new__(
            actor_session.ElasticActorSession
        )
        session.manager = manager
        session.env_count = env_count
        session.worker_offset = 64
        session.client = mock.Mock()
        session.client.env_count = env_count
        session.session_id = "session"
        session.actor_id = 1
        session.control_epoch = 3
        session._current_env_config = {}
        session._behavior_specs = {"BeesRL1v1?team=0": FakeBehaviorSpec()}
        session._capacity_registration_pending = False
        session._resize_failed_target = None
        session._resize_failure = None
        session._registered_env_count = env_count
        session._downscale_registration_pending = False
        session._state_changed = threading.Event()
        session.stop = threading.Event()
        session._upload_queue = queue.Queue()
        session._upload_idle = threading.Event()
        session._upload_idle.set()
        session._report_env_count_changed = mock.Mock()
        session._write_throughput_metrics = mock.Mock()
        return session

    def test_transient_env_target_read_failure_keeps_current_capacity(self):
        session = self._session(mock.Mock(), 8)
        session._env_target_path = mock.Mock()
        session._env_target_path.read_text.side_effect = PermissionError(
            13,
            "file temporarily unavailable",
        )

        self.assertEqual(session._desired_env_count(), 8)

    def test_malformed_env_target_remains_a_hard_error(self):
        session = self._session(mock.Mock(), 8)
        session._env_target_path = mock.Mock()
        session._env_target_path.read_text.return_value = "not-an-integer"

        with self.assertRaisesRegex(RuntimeError, "not an integer"):
            session._desired_env_count()

    def test_scale_up_adds_only_tail_worker_and_preserves_existing_workers(self):
        from mlagents.trainers.subprocess_env_manager import (
            EnvironmentCommand,
            EnvironmentResponse,
        )

        existing = [SimpleNamespace(worker_id=0), SimpleNamespace(worker_id=1)]
        new_worker = mock.Mock()
        new_worker.worker_id = 2
        new_worker.waiting = False
        new_worker.recv.side_effect = [
            EnvironmentResponse(EnvironmentCommand.RESET, 2, {}),
            EnvironmentResponse(
                EnvironmentCommand.BEHAVIOR_SPECS,
                2,
                {"BeesRL1v1?team=0": FakeBehaviorSpec()},
            ),
        ]
        manager = SimpleNamespace(
            env_workers=list(existing),
            step_queue=queue.Queue(),
            env_factory=object(),
            run_options=object(),
            env_parameters={},
            recent_restart_timestamps=[[], []],
            restart_counts=[0, 0],
            workers_alive=2,
            agent_managers={},
            create_worker=mock.Mock(return_value=new_worker),
            process_steps=mock.Mock(),
        )
        session = self._session(manager, 2)

        self.assertTrue(session._scale_up_one(3))

        self.assertIs(manager.env_workers[0], existing[0])
        self.assertIs(manager.env_workers[1], existing[1])
        self.assertIs(manager.env_workers[2], new_worker)
        manager.create_worker.assert_called_once_with(
            2,
            manager.step_queue,
            manager.env_factory,
            manager.run_options,
        )
        self.assertEqual(
            [call.args[0] for call in new_worker.send.call_args_list[:2]],
            [EnvironmentCommand.RESET, EnvironmentCommand.BEHAVIOR_SPECS],
        )
        self.assertEqual(session.env_count, 3)
        self.assertEqual(session.client.env_count, 3)
        session.client.register.assert_called_once()
        self.assertEqual(manager.workers_alive, 3)
        self.assertEqual(len(manager.recent_restart_timestamps), 3)
        self.assertEqual(len(manager.restart_counts), 3)

    def test_failed_scale_up_retires_candidate_and_keeps_existing_actor_alive(self):
        from mlagents.trainers.subprocess_env_manager import (
            EnvironmentCommand,
            EnvironmentResponse,
        )

        existing = [SimpleNamespace(worker_id=0), SimpleNamespace(worker_id=1)]
        new_worker = mock.Mock()
        new_worker.worker_id = 2
        new_worker.waiting = False
        new_worker.process.is_alive.return_value = False
        new_worker.recv.side_effect = [
            EnvironmentResponse(EnvironmentCommand.RESET, 2, {}),
            EnvironmentResponse(
                EnvironmentCommand.BEHAVIOR_SPECS,
                2,
                {"DifferentBehavior?team=0": FakeBehaviorSpec()},
            ),
        ]
        manager = SimpleNamespace(
            env_workers=list(existing),
            step_queue=queue.Queue(),
            env_factory=object(),
            run_options=object(),
            env_parameters={},
            recent_restart_timestamps=[[], []],
            restart_counts=[0, 0],
            workers_alive=2,
            agent_managers={},
            create_worker=mock.Mock(return_value=new_worker),
            process_steps=mock.Mock(),
        )
        manager.step_queue.put(
            EnvironmentResponse(EnvironmentCommand.CLOSED, 2, None)
        )
        session = self._session(manager, 2)

        self.assertFalse(session._scale_up_one(3))

        self.assertEqual(manager.env_workers, existing)
        self.assertEqual(manager.workers_alive, 2)
        self.assertEqual(session.env_count, 2)
        self.assertEqual(session._resize_failed_target, 3)
        self.assertIn("behavior specifications do not match", session._resize_failure["error"])

    def test_capacity_registration_reclaims_expired_same_actor_slot(self):
        manager = SimpleNamespace(env_workers=[SimpleNamespace(worker_id=0)])
        session = self._session(manager, 1)
        session._behavior_specs = {"BeesRL1v1?team=0": object()}
        session.session_id = "session-a"
        session.actor_id = 0
        session.client.register.side_effect = [
            actor_session.worker.BrokerClaimRequired("claim expired"),
            None,
        ]
        session.client.claim = mock.Mock(return_value=0)

        self.assertTrue(session._register_current_capacity())

        session.client.claim.assert_called_once_with("session-a")
        self.assertEqual(session.client.register.call_count, 2)
        self.assertEqual(session._registered_env_count, 1)

    def test_capacity_registration_restarts_session_if_reclaim_moves_slot(self):
        manager = SimpleNamespace(env_workers=[SimpleNamespace(worker_id=0)])
        session = self._session(manager, 1)
        session._behavior_specs = {"BeesRL1v1?team=0": object()}
        session.session_id = "session-a"
        session.actor_id = 0
        session.client.register.side_effect = actor_session.worker.BrokerClaimRequired(
            "claim expired"
        )
        session.client.claim = mock.Mock(return_value=1)

        self.assertFalse(session._register_current_capacity())

        self.assertTrue(session._state_changed.is_set())

    def test_downscale_retires_locally_even_while_uploads_are_backpressured(self):
        tail = SimpleNamespace(worker_id=2, waiting=False)
        manager = SimpleNamespace(
            env_workers=[SimpleNamespace(worker_id=0), SimpleNamespace(worker_id=1), tail]
        )
        session = self._session(manager, 3)
        session._env_target_path = mock.Mock()
        session._desired_env_count = mock.Mock(return_value=2)
        session._upload_queue.put({"trajectories": [object()]})

        def retire():
            session.env_count = 2
            session._downscale_registration_pending = True
            return True

        session._scale_down_one = mock.Mock(side_effect=retire)

        self.assertTrue(session._reconcile_env_count())
        session._scale_down_one.assert_called_once()
        self.assertEqual(session.env_count, 2)
        self.assertTrue(session._downscale_registration_pending)
        session.client.register.assert_not_called()

    def test_downscale_waits_for_finite_upload_backlog_then_registers_once(self):
        manager = SimpleNamespace(
            env_workers=[SimpleNamespace(worker_id=0), SimpleNamespace(worker_id=1)]
        )
        session = self._session(manager, 2)
        session._env_target_path = mock.Mock()
        session._desired_env_count = mock.Mock(return_value=2)
        session._registered_env_count = 3
        session.client.env_count = 3
        session._downscale_registration_pending = True
        session._upload_queue.put({"trajectories": [object()]})

        self.assertTrue(session._reconcile_env_count())
        self.assertEqual(session._registered_env_count, 3)
        session.client.register.assert_not_called()
        self.assertIsNone(session._resize_failure)

        session._upload_queue.get_nowait()
        self.assertFalse(session._reconcile_env_count())

        self.assertFalse(session._downscale_registration_pending)
        self.assertEqual(session._registered_env_count, 2)
        self.assertEqual(session.client.env_count, 2)
        session.client.register.assert_called_once()
        session._report_env_count_changed.assert_called_once()

    def test_scale_down_failure_is_reported_without_crashing_actor(self):
        tail = SimpleNamespace(worker_id=2, waiting=False)
        manager = SimpleNamespace(
            env_workers=[
                SimpleNamespace(worker_id=0),
                SimpleNamespace(worker_id=1),
                tail,
            ]
        )
        session = self._session(manager, 3)
        session._env_target_path = mock.Mock()
        session._desired_env_count = mock.Mock(return_value=2)
        session._scale_down_one = mock.Mock(
            side_effect=RuntimeError("Unity worker 2 did not stop during live resize")
        )

        self.assertFalse(session._reconcile_env_count())

        self.assertEqual(session.env_count, 3)
        self.assertEqual(session._resize_failed_target, 2)
        self.assertIn("did not stop during live resize", session._resize_failure["error"])

    def test_scale_down_retires_only_tail_worker(self):
        from mlagents.trainers.subprocess_env_manager import (
            EnvironmentCommand,
            EnvironmentResponse,
        )

        first = SimpleNamespace(worker_id=0)
        second = SimpleNamespace(worker_id=1)
        tail = mock.Mock()
        tail.worker_id = 2
        tail.waiting = False
        tail.closed = False
        tail.process.is_alive.return_value = False
        step_queue = queue.Queue()
        step_queue.put(EnvironmentResponse(EnvironmentCommand.CLOSED, 2, None))
        manager = SimpleNamespace(
            env_workers=[first, second, tail],
            step_queue=step_queue,
            recent_restart_timestamps=[[], [], []],
            restart_counts=[0, 0, 0],
            workers_alive=3,
            agent_managers={},
        )
        session = self._session(manager, 3)

        self.assertTrue(session._scale_down_one())

        self.assertEqual(manager.env_workers, [first, second])
        tail.request_close.assert_called_once_with()
        self.assertEqual(manager.workers_alive, 2)
        self.assertEqual(session.env_count, 2)
        self.assertEqual(session.client.env_count, 3)
        self.assertEqual(session._registered_env_count, 3)
        self.assertTrue(session._downscale_registration_pending)
        session.client.register.assert_not_called()

        self.assertTrue(session._finish_pending_downscale_registration())
        self.assertEqual(session.client.env_count, 2)
        self.assertEqual(session._registered_env_count, 2)
        self.assertFalse(session._downscale_registration_pending)
        session.client.register.assert_called_once()


class ElasticActorThroughputTests(unittest.TestCase):
    def test_consumed_step_updates_publish_recent_rate(self):
        session = actor_session.ElasticActorSession.__new__(
            actor_session.ElasticActorSession
        )
        session.actor_id = 0
        session._throughput_lock = threading.Lock()
        session._learner_consumed_steps_total = 0
        session._learner_consumed_steps_per_sec = None
        session._last_consumed_sample = (100.0, 1000)
        session._write_throughput_metrics = mock.Mock()

        with mock.patch.object(actor_session.time, "monotonic", return_value=102.0):
            session._apply_central_throughput(
                {
                    "policy_cycle": 7,
                    "trainer_step": 600,
                    "consumed_steps_by_actor": {"0": 1200},
                }
            )

        self.assertEqual(session.policy_cycle, 7)
        self.assertEqual(session.learner_step, 600)
        self.assertEqual(session._learner_consumed_steps_total, 1200)
        self.assertAlmostEqual(session._learner_consumed_steps_per_sec, 100.0)
        session._write_throughput_metrics.assert_called_once_with()


class ElasticActorFailureTelemetryTests(unittest.TestCase):
    def test_session_failure_history_survives_actor_restart_within_same_run(self):
        with tempfile.TemporaryDirectory() as temp:
            state_path = Path(temp) / actor_worker.SESSION_FAILURE_STATE_FILE
            telemetry = actor_worker._SessionFailureTelemetry(
                state_path=state_path,
                run_id="run-a",
                runtime_version="a" * 64,
            )
            with mock.patch.object(actor_worker.time, "time", return_value=100.0):
                telemetry.record(RuntimeError("simulated failure"))

            restarted = actor_worker._SessionFailureTelemetry(
                state_path=state_path,
                run_id="run-a",
                runtime_version="a" * 64,
            )
            with mock.patch.object(actor_worker.time, "time", return_value=106.0):
                snapshot = restarted.snapshot()

            self.assertEqual(snapshot["session_failures_total"], 1)
            self.assertEqual(snapshot["seconds_since_last_session_failure"], 6.0)
            self.assertEqual(snapshot["last_session_failure_type"], "RuntimeError")
            self.assertEqual(
                snapshot["last_session_failure_message"],
                "simulated failure",
            )

            next_runtime = actor_worker._SessionFailureTelemetry(
                state_path=state_path,
                run_id="run-a",
                runtime_version="b" * 64,
            )
            self.assertEqual(next_runtime.snapshot()["session_failures_total"], 0)
            self.assertEqual(next_runtime.snapshot()["last_session_failure_type"], "")
            self.assertEqual(next_runtime.snapshot()["last_session_failure_message"], "")

            next_run = actor_worker._SessionFailureTelemetry(
                state_path=state_path,
                run_id="run-b",
                runtime_version="b" * 64,
            )
            self.assertEqual(next_run.snapshot()["session_failures_total"], 0)
            self.assertEqual(next_run.snapshot()["last_session_failure_type"], "")
            self.assertEqual(next_run.snapshot()["last_session_failure_message"], "")


class ElasticActorHealthTests(unittest.TestCase):
    def test_session_failure_message_is_bounded_and_single_line(self):
        telemetry = actor_worker._SessionFailureTelemetry()
        telemetry.record(TimeoutError("first line\n" + ("x" * 400)))
        snapshot = telemetry.snapshot()

        self.assertEqual(snapshot["last_session_failure_type"], "TimeoutError")
        self.assertNotIn("\n", snapshot["last_session_failure_message"])
        self.assertLessEqual(len(snapshot["last_session_failure_message"]), 240)
        self.assertTrue(
            snapshot["last_session_failure_message"].startswith("first line ")
        )

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
            heartbeat.set_ready("running", actor_id=3)
            writes_before_progress = health.call_count
            heartbeat.mark_progress()
            self.assertEqual(health.call_count, writes_before_progress)
            heartbeat.stop()

        self.assertGreaterEqual(health.call_count, 3)
        first = health.call_args_list[0]
        latest = health.call_args_list[-1]
        self.assertEqual(first.args[0], "starting")
        self.assertEqual(first.kwargs["details"]["phase"], "starting-session")
        self.assertEqual(latest.args[0], "ready")
        self.assertEqual(latest.kwargs["details"]["phase"], "running")
        self.assertEqual(latest.kwargs["details"]["actor_id"], 3)
        self.assertEqual(latest.kwargs["details"]["env_count"], 2)
        self.assertIn("phase_started_unix_seconds", latest.kwargs["details"])
        self.assertIn("progress_unix_seconds", latest.kwargs["details"])

    def test_rollout_progress_does_not_take_health_publication_lock(self):
        heartbeat = actor_worker._StartupHealthHeartbeat(
            actor_id=1,
            env_count=4,
            interval_seconds=60.0,
        )

        class FailingLock:
            def __enter__(self):
                raise AssertionError("rollout progress must not acquire publication lock")

            def __exit__(self, exc_type, exc, tb):
                return False

        heartbeat._lock = FailingLock()
        heartbeat.mark_progress()
        self.assertTrue(heartbeat._progress_pending.is_set())

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

    def test_elastic_broker_hot_path_checks_server_thread_liveness(self):
        broker, _specs = self._broker()
        with mock.patch.object(broker, "ensure_server_alive") as ensure:
            broker.active_actor_snapshot()
            broker.wait_for_minimum_registrations(0.01)
            broker.wait_state(-1, -1, 0.0)

        self.assertGreaterEqual(ensure.call_count, 3)

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

    def test_drain_is_fair_across_ready_actors(self):
        broker, specs = self._broker()
        for actor_id in (0, 1):
            broker.register_actor(
                {
                    **broker.release_identity,
                    "actor_id": actor_id,
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
        self.assertEqual(state["policy_cycle"], 0)
        self.assertEqual(state["trainer_step"], 0)
        broker.observe_trainer_step(321)
        self.assertEqual(
            broker.wait_state(broker._policy_epoch, broker.control_epoch, 0.0)["trainer_step"],
            321,
        )

        starting_epoch = broker.policy_publication_epoch()
        with broker._condition:
            broker._policy_epoch = starting_epoch + 2
        broker.complete_policy_cycle(starting_epoch)
        completed = broker.wait_state(
            broker._policy_epoch,
            broker.control_epoch,
            0.0,
        )
        self.assertEqual(completed["policy_cycle"], 1)

    def test_fair_drain_rotates_first_actor_between_calls(self):
        broker, specs = self._broker()
        for actor_id in (0, 1):
            broker.register_actor(
                {
                    **broker.release_identity,
                    "actor_id": actor_id,
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
        with self.assertRaisesRegex(
            base.ActorClaimRequiredError,
            "no active claim",
        ):
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
            "time",
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
