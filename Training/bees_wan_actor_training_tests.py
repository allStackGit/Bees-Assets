"""Focused tests for WAN actor/learner transport invariants."""

from __future__ import annotations

import os
import queue
import tempfile
import unittest
from unittest import mock
from pathlib import Path
from types import SimpleNamespace

import bees_continual_wan_service as wan_service
import bees_wan_actor_training as wan
import bees_wan_actor_worker as actor


class FakeObservationSpec:
    def __init__(self, shape=(4,)):
        self.shape = shape
        self.dimension_property = ()
        self.observation_type = "DEFAULT"


class FakeActionSpec:
    continuous_size = 2
    discrete_branches = (3, 2)


class FakeBehaviorSpec:
    observation_specs = (FakeObservationSpec(),)
    action_spec = FakeActionSpec()


class FakeTrajectory:
    def __init__(self, behavior_id: str, agent_id: str, count: int = 2):
        self.behavior_id = behavior_id
        self.agent_id = agent_id
        self.steps = [object() for _ in range(count)]


def fake_run_options():
    return SimpleNamespace(
        checkpoint_settings=SimpleNamespace(run_id="wan-test"),
    )


class WanActorLogGenerationTests(unittest.TestCase):
    def test_managed_actor_logs_are_scoped_to_broker_session(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            options = SimpleNamespace(
                checkpoint_settings=SimpleNamespace(
                    run_logs_dir=str(root / "fallback")
                )
            )
            session = object.__new__(actor.ActorSession)
            session.actor_id = 3

            with mock.patch.dict(
                os.environ,
                {"BEES_TRAINING_LOG_DIR": str(root)},
                clear=False,
            ):
                session.session_id = "session-a"
                first = Path(session._run_logs_dir(options))
                session.session_id = "session-b"
                second = Path(session._run_logs_dir(options))

            self.assertNotEqual(first, second)
            self.assertEqual(first.parent, root)
            self.assertEqual(second.parent, root)
            self.assertTrue(first.name.startswith("actor-3-session-"))
            self.assertTrue(second.name.startswith("actor-3-session-"))
            self.assertTrue(first.is_dir())
            self.assertTrue(second.is_dir())


class WanActorStepDiagnosticTests(unittest.TestCase):
    def test_worker_diagnostics_identify_waiting_unity_process(self):
        class FakeProcess:
            def __init__(self, pid, alive):
                self.pid = pid
                self._alive = alive

            def is_alive(self):
                return self._alive

        session = object.__new__(actor.ActorSession)
        session.worker_offset = 12
        session.manager = SimpleNamespace(
            env_workers=[
                SimpleNamespace(
                    process=FakeProcess(101, True),
                    waiting=False,
                    closed=False,
                ),
                SimpleNamespace(
                    process=FakeProcess(102, True),
                    waiting=True,
                    closed=False,
                ),
            ]
        )

        diagnostics = session._unity_worker_diagnostics()

        self.assertIn("worker=12 local=0 pid=101 alive=True waiting=False", diagnostics)
        self.assertIn("worker=13 local=1 pid=102 alive=True waiting=True", diagnostics)


class WanActorPolicyConstructionTests(unittest.TestCase):
    def tearDown(self):
        from bees_mlagents_ppo_compat import (
            restore_continuous_sigma_guard,
            restore_inactive_continuous_action_masking,
        )
        from bees_mlagents_structured_policy import restore_structured_policy

        restore_continuous_sigma_guard()
        restore_inactive_continuous_action_masking()
        restore_structured_policy()

    def test_remote_actor_reconstructs_structured_bees_policy(self):
        from mlagents.trainers.settings import NetworkSettings
        from mlagents_envs.base_env import (
            ActionSpec,
            BehaviorSpec,
            DimensionProperty,
            ObservationSpec,
            ObservationType,
        )

        behavior_spec = BehaviorSpec(
            observation_specs=[
                ObservationSpec(
                    shape=(7743,),
                    dimension_property=(DimensionProperty.NONE,),
                    observation_type=ObservationType.DEFAULT,
                    name="vector",
                )
            ],
            action_spec=ActionSpec(
                continuous_size=16,
                discrete_branches=(2, 2, 2, 2, 2, 5),
            ),
        )
        settings = SimpleNamespace(
            hyperparameters=SimpleNamespace(),
            network_settings=NetworkSettings(
                normalize=True,
                hidden_units=128,
                num_layers=3,
            ),
            reward_signals={},
        )
        run_options = SimpleNamespace(
            behaviors={"BeesRL1v1": settings},
        )

        policy = actor._build_template_policy(
            "BeesRL1v1?team=0",
            behavior_spec,
            run_options,
            seed=123,
        )

        self.assertEqual(
            type(policy.actor.network_body).__name__,
            "BeesStructuredNetworkBody",
        )
        self.assertEqual(
            type(policy.actor.action_model).__name__,
            "BeesStructuredActionModel",
        )


class WanPolicyWirePayloadTests(unittest.TestCase):
    def test_torch_policy_payload_moves_tensor_weights_to_cpu(self):
        import torch
        import mlagents.trainers.policy.torch_policy as torch_policy_module

        class FakeTorchPolicy:
            def get_weights(self):
                device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
                return {"weight": torch.ones(2, device=device)}

            def get_current_step(self):
                return 7

        with mock.patch.object(torch_policy_module, "TorchPolicy", FakeTorchPolicy):
            payload = wan._policy_wire_payload(FakeTorchPolicy())

        self.assertEqual(payload["kind"], "torch")
        self.assertEqual(payload["step"], 7)
        self.assertEqual(payload["weights"]["weight"].device.type, "cpu")

class WanHttpResponseTests(unittest.TestCase):
    @staticmethod
    def _handler(write_side_effect):
        return SimpleNamespace(
            send_response=mock.Mock(),
            send_header=mock.Mock(),
            end_headers=mock.Mock(),
            wfile=SimpleNamespace(write=mock.Mock(side_effect=write_side_effect)),
        )

    def test_peer_disconnect_during_response_write_is_ignored(self):
        handler = self._handler(ConnectionAbortedError("peer disconnected"))

        wan._send_http_response(
            handler,
            status=200,
            content_type="application/json",
            body=b"{}",
        )

        handler.wfile.write.assert_called_once_with(b"{}")

    def test_unrelated_response_write_error_is_not_hidden(self):
        handler = self._handler(OSError("unexpected write failure"))

        with self.assertRaisesRegex(OSError, "unexpected write failure"):
            wan._send_http_response(
                handler,
                status=200,
                content_type="application/json",
                body=b"{}",
            )


class WanOptionTests(unittest.TestCase):
    def test_actor_topology_is_deterministic_and_non_overlapping(self):
        options = wan.WanActorOptions(
            actor_count=12,
            envs_per_actor=32,
            auth_token_file="token.txt",
        )
        self.assertEqual(wan.actor_worker_ids(options, 0), tuple(range(0, 32)))
        self.assertEqual(wan.actor_worker_ids(options, 11), tuple(range(352, 384)))
        all_workers = {
            worker
            for actor_id in range(options.actor_count)
            for worker in wan.actor_worker_ids(options, actor_id)
        }
        self.assertEqual(len(all_workers), 384)

    def test_extract_requires_complete_wan_topology(self):
        with self.assertRaisesRegex(SystemExit, wan.WAN_ENVS_PER_ACTOR_FLAG):
            wan.extract_wan_actor_options([wan.WAN_ACTORS_FLAG, "12"])
        with self.assertRaisesRegex(SystemExit, wan.WAN_AUTH_TOKEN_FILE_FLAG):
            wan.extract_wan_actor_options(
                [wan.WAN_ACTORS_FLAG, "12", wan.WAN_ENVS_PER_ACTOR_FLAG, "32"]
            )

    def test_extract_strips_only_wan_options(self):
        cleaned, options = wan.extract_wan_actor_options(
            [
                "config.yaml",
                "--run-id=unified",
                "--bees-wan-actors=12",
                "--bees-wan-envs-per-actor=32",
                "--bees-wan-min-actors=4",
                "--bees-wan-broker-port=56051",
                "--bees-wan-auth-token-file=C:/secret/token.txt",
                "--bees-wan-max-queued-batches=48",
                "--resume",
            ]
        )
        self.assertEqual(cleaned, ["config.yaml", "--run-id=unified", "--resume"])
        self.assertEqual(options.actor_count, 12)
        self.assertEqual(options.envs_per_actor, 32)
        self.assertEqual(options.total_envs, 384)
        self.assertEqual(options.min_actors, 4)
        self.assertEqual(options.broker_port, 56051)
        self.assertEqual(options.max_queued_batches, 48)

    def test_auth_token_requires_nontrivial_single_token(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "token.txt"
            path.write_text("short\n", encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "32-1024"):
                wan.load_auth_token(path)
            path.write_text("a" * 32 + "\n", encoding="utf-8")
            self.assertEqual(wan.load_auth_token(path), "a" * 32)

    def test_payload_codec_round_trips_native_python_objects(self):
        value = {"a": [1, 2, 3], "nested": {"ok": True}}
        self.assertEqual(wan.decode_payload(wan.encode_payload(value)), value)

    def test_broker_client_tracks_and_persists_wan_payload_traffic(self):
        class FakeResponse:
            status = 200
            headers = {}

            def __enter__(self):
                return self

            def __exit__(self, exc_type, exc, tb):
                return False

            def read(self):
                return b"response-bytes"

        with tempfile.TemporaryDirectory() as temp:
            throughput_path = Path(temp) / "throughput.json"
            request_payload = {"trajectories": [1, 2, 3]}
            encoded_request = wan.encode_payload(request_payload)
            with mock.patch.dict(
                os.environ,
                {
                    actor.THROUGHPUT_METRICS_ENV: str(throughput_path),
                    actor.TRAINING_RUN_ID_ENV: "run-a",
                },
                clear=False,
            ):
                client = actor.BrokerClient("127.0.0.1", 56051, "a" * 32)
                with mock.patch.object(actor.urllib.request, "urlopen", return_value=FakeResponse()):
                    status, _headers, body = client._request(
                        "POST",
                        "/traffic-test",
                        payload=request_payload,
                    )
                self.assertEqual(status, 200)
                self.assertEqual(body, b"response-bytes")
                snapshot = client.traffic_snapshot()
                self.assertEqual(snapshot["network_sent_bytes_total"], len(encoded_request))
                self.assertEqual(snapshot["network_received_bytes_total"], len(b"response-bytes"))
                self.assertIn("network_mib_per_s", snapshot)

                reloaded = actor.BrokerClient("127.0.0.1", 56051, "a" * 32)
                persisted = reloaded.traffic_snapshot()
                self.assertEqual(
                    persisted["network_sent_bytes_total"],
                    snapshot["network_sent_bytes_total"],
                )
                self.assertEqual(
                    persisted["network_received_bytes_total"],
                    snapshot["network_received_bytes_total"],
                )

    def test_wan_service_replaces_local_num_envs_with_actor_total(self):
        self.assertEqual(
            wan_service._replace_num_envs(
                ["config.yaml", "--num-envs", "32", "--resume"], 384
            ),
            ["config.yaml", "--resume", "--num-envs=384"],
        )
        self.assertEqual(
            wan_service._replace_num_envs(
                ["config.yaml", "--num-envs=32", "--resume"], 384
            ),
            ["config.yaml", "--resume", "--num-envs=384"],
        )

    def test_actor_resolves_mlagents_random_seed_sentinel_stably(self):
        first = actor._resolve_actor_seed(
            -1,
            session_id="session-a",
            worker_offset=0,
            env_count=4,
        )
        second = actor._resolve_actor_seed(
            -1,
            session_id="session-a",
            worker_offset=0,
            env_count=4,
        )
        self.assertEqual(first, second)
        self.assertGreaterEqual(first, 0)
        self.assertLessEqual(first + 3, actor.MAX_UNITY_SEED)

    def test_actor_explicit_seed_includes_worker_offset_without_overflow(self):
        self.assertEqual(
            actor._resolve_actor_seed(
                123,
                session_id="session-a",
                worker_offset=8,
                env_count=4,
            ),
            131,
        )
        wrapped = actor._resolve_actor_seed(
            actor.MAX_UNITY_SEED,
            session_id="session-a",
            worker_offset=64,
            env_count=64,
        )
        self.assertGreaterEqual(wrapped, 0)
        self.assertLessEqual(wrapped + 63, actor.MAX_UNITY_SEED)

    def test_actor_rejects_invalid_negative_seed(self):
        with self.assertRaisesRegex(ValueError, "invalid ML-Agents seed"):
            actor._resolve_actor_seed(
                -2,
                session_id="session-a",
                worker_offset=0,
                env_count=4,
            )

    def test_remote_actor_uses_rolling_restart_budget_without_lifetime_cap(self):
        session = object.__new__(actor.ActorSession)
        session.central_run_options = SimpleNamespace(
            env_settings=SimpleNamespace(
                env_path="",
                base_port=5005,
                num_envs=1,
                seed=123,
                max_lifetime_restarts=10,
                restarts_rate_limit_n=1,
                restarts_rate_limit_period_s=60,
            ),
            engine_settings=SimpleNamespace(no_graphics=False),
            torch_settings=SimpleNamespace(device="cpu"),
        )
        session.env_path = Path("/tmp/bees-training")
        session.local_base_port = 6005
        session.env_count = 3
        session.worker_offset = 64
        session.session_id = "session-a"
        session.graphics = False
        session.torch_device = "cpu"

        options = session._remote_run_options()

        self.assertEqual(options.env_settings.max_lifetime_restarts, -1)
        self.assertEqual(options.env_settings.restarts_rate_limit_n, 1)
        self.assertEqual(options.env_settings.restarts_rate_limit_period_s, 60)
        self.assertEqual(options.env_settings.num_envs, 3)

    def test_transient_broker_unavailability_retries_without_replacing_actor_session(self):
        session = object.__new__(actor.ActorSession)
        session.stop = actor.threading.Event()
        operation = mock.Mock(
            side_effect=[
                actor.BrokerUnavailable("temporary forward reset"),
                "recovered",
            ]
        )

        with mock.patch.object(session.stop, "wait", return_value=False) as wait:
            result = session._retry_broker_unavailable(
                operation,
                label="test synchronization",
            )

        self.assertEqual(result, "recovered")
        self.assertEqual(operation.call_count, 2)
        wait.assert_called_once_with(1.0)

    def test_run_routes_state_resynchronization_through_in_place_broker_retry(self):
        session = object.__new__(actor.ActorSession)
        session.stop = actor.threading.Event()
        session._thread_error = queue.Queue()
        session._session_changed = actor.threading.Event()
        session._state_changed = actor.threading.Event()
        session._state_changed.set()
        session._stale = actor.threading.Event()
        session._synchronize_state = mock.Mock()
        session._report_runtime_progress = mock.Mock()

        def retry(operation, *, label):
            self.assertEqual(label, "policy/control synchronization")
            operation()
            session.stop.set()

        session._retry_broker_unavailable = mock.Mock(side_effect=retry)
        session.run()

        session._retry_broker_unavailable.assert_called_once()
        session._synchronize_state.assert_called_once_with()
        session._report_runtime_progress.assert_called_once_with()

    def test_missing_initial_reset_is_central_availability_not_session_failure(self):
        session = object.__new__(actor.ActorSession)
        session.central_run_options = SimpleNamespace(
            env_settings=SimpleNamespace(timeout_wait=0.0)
        )
        session.stop = actor.threading.Event()
        session.client = mock.Mock()
        session.client.control.return_value = None
        session.session_id = "session-a"
        session.control_epoch = 0

        with (
            mock.patch.object(actor.time, "monotonic", side_effect=[0.0, 31.0]),
            mock.patch.object(actor.time, "sleep"),
        ):
            with self.assertRaisesRegex(
                actor.BrokerUnavailable,
                "waiting for initial central reset",
            ):
                session._initial_control()

    def test_empty_initial_policy_set_is_central_availability_not_session_failure(self):
        session = object.__new__(actor.ActorSession)
        session.central_run_options = SimpleNamespace(
            env_settings=SimpleNamespace(timeout_wait=0.0)
        )
        session.stop = actor.threading.Event()
        session.client = mock.Mock()
        session.client.state.return_value = {
            "control_epoch": 0,
            "policy_epoch": -1,
            "policy_versions": {},
        }
        session.session_id = "session-a"
        session.control_epoch = 0
        session.policy_epoch = -1
        session.policy_versions = {}
        session.templates = {"Behavior?team=0": object()}

        with (
            mock.patch.object(actor.time, "monotonic", side_effect=[0.0, 31.0]),
            mock.patch.object(actor.time, "sleep"),
        ):
            with self.assertRaisesRegex(
                actor.BrokerUnavailable,
                "complete central policy set",
            ):
                session._synchronize_state(require_policy=True)

    def test_control_record_advancing_during_sync_resynchronizes_in_place(self):
        session = object.__new__(actor.ActorSession)
        session.central_run_options = SimpleNamespace(
            env_settings=SimpleNamespace(timeout_wait=30.0)
        )
        session.stop = actor.threading.Event()
        session.client = mock.Mock()
        session.client.state.return_value = {
            "control_epoch": 2,
            "policy_epoch": 0,
            "policy_versions": {},
        }
        session.client.control.return_value = {
            "epoch": 3,
            "kind": "parameters",
            "config": {"difficulty": 3},
        }
        session.session_id = "session-a"
        session.control_epoch = 1
        session.policy_epoch = 0
        session.policy_versions = {}
        session.templates = {}
        session.manager = mock.Mock()
        session.worker_offset = 0
        session._upload_queue = queue.Queue()
        session._state_changed = actor.threading.Event()
        session._stale = actor.threading.Event()

        with (
            mock.patch.object(actor, "_drain_inflight_without_training"),
            mock.patch.object(actor, "_clear_partial_trajectories"),
        ):
            session._synchronize_state()

        self.assertEqual(session.control_epoch, 1)
        self.assertTrue(session._state_changed.is_set())
        session.manager.reset.assert_not_called()
        session.manager.set_env_parameters.assert_not_called()

    def test_partial_wrong_policy_set_remains_protocol_failure(self):
        session = object.__new__(actor.ActorSession)
        session.central_run_options = SimpleNamespace(
            env_settings=SimpleNamespace(timeout_wait=0.0)
        )
        session.stop = actor.threading.Event()
        session.client = mock.Mock()
        session.client.state.return_value = {
            "control_epoch": 0,
            "policy_epoch": 1,
            "policy_versions": {"Behavior?team=0": 1},
        }
        session.session_id = "session-a"
        session.control_epoch = 0
        session.policy_epoch = -1
        session.policy_versions = {}
        session.templates = {
            "Behavior?team=0": object(),
            "Behavior?team=1": object(),
        }

        with (
            mock.patch.object(actor.time, "monotonic", side_effect=[0.0, 31.0]),
            mock.patch.object(actor.time, "sleep"),
        ):
            with self.assertRaisesRegex(RuntimeError, "complete central policy set"):
                session._synchronize_state(require_policy=True)

    def test_actor_counts_only_steps_in_accepted_trajectories(self):
        trajectories = [
            FakeTrajectory("Behavior?team=0", "agent-1", count=3),
            FakeTrajectory("Behavior?team=0", "agent-2", count=2),
        ]
        self.assertEqual(actor._trajectory_step_count(trajectories), 5)

    def test_actor_publishes_accepted_step_metrics_atomically(self):
        with tempfile.TemporaryDirectory() as temp:
            metrics_path = Path(temp) / "throughput.json"
            session = object.__new__(actor.ActorSession)
            session._throughput_metrics_path = metrics_path
            session._throughput_lock = actor.threading.Lock()
            session._accepted_steps_total = 0
            session._accepted_trajectories_total = 0
            session._learner_consumed_steps_total = 4
            session._learner_consumed_steps_per_sec = None
            session._last_throughput_write = 0.0
            session._upload_queue = queue.Queue()
            session._session_failure_telemetry = SimpleNamespace(
                snapshot=lambda: {
                    "session_failures_total": 2,
                    "seconds_since_last_session_failure": 12.5,
                    "last_session_failure_type": "UnityCommunicatorStoppedException",
                }
            )
            session.env_count = 7
            session.client = SimpleNamespace(
                traffic_snapshot=lambda: {
                    "network_sent_bytes_total": 3 * 1024 * 1024,
                    "network_received_bytes_total": 2 * 1024 * 1024,
                    "network_mib_per_s": 1.25,
                }
            )

            session._record_accepted_trajectories(
                [
                    FakeTrajectory("Behavior?team=0", "agent-1", count=3),
                    FakeTrajectory("Behavior?team=0", "agent-2", count=2),
                ]
            )
            session._write_throughput_metrics(force=True)

            payload = actor.json.loads(metrics_path.read_text(encoding="utf-8"))
            self.assertEqual(payload["pid"], os.getpid())
            self.assertEqual(payload["env_count"], 7)
            self.assertEqual(payload["accepted_steps_total"], 5)
            self.assertEqual(payload["accepted_trajectories_total"], 2)
            self.assertEqual(payload["learner_consumed_steps_total"], 4)
            self.assertEqual(payload["upload_queue_depth"], 0)
            self.assertEqual(payload["session_failures_total"], 2)
            self.assertEqual(payload["seconds_since_last_session_failure"], 12.5)
            self.assertEqual(
                payload["last_session_failure_type"],
                "UnityCommunicatorStoppedException",
            )
            self.assertEqual(payload["network_sent_bytes_total"], 3 * 1024 * 1024)
            self.assertEqual(payload["network_received_bytes_total"], 2 * 1024 * 1024)
            self.assertEqual(payload["network_mib_per_s"], 1.25)

    def test_actor_player_log_tail_reports_missing_logs(self):
        with tempfile.TemporaryDirectory() as temp:
            with mock.patch("builtins.print") as printer:
                actor.ActorSession._print_player_log_tails(temp, lines=10)
        self.assertTrue(
            any(
                "no Unity Player-*.log files found" in str(call)
                for call in printer.call_args_list
            )
        )

    def test_actor_player_log_tail_prints_recent_lines(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "Player-0.log"
            path.write_text("one\ntwo\nthree\n", encoding="utf-8")
            with mock.patch("builtins.print") as printer:
                actor.ActorSession._print_player_log_tails(temp, lines=2)
        rendered = "\n".join(str(call) for call in printer.call_args_list)
        self.assertIn("Unity log tail", rendered)
        self.assertNotIn("one", rendered)
        self.assertIn("two", rendered)
        self.assertIn("three", rendered)

    def test_actor_managed_log_directory_does_not_mutate_checkpoint_settings(self):
        class ReadOnlyCheckpointSettings:
            @property
            def run_logs_dir(self):
                return "default-run-logs"

        options = SimpleNamespace(checkpoint_settings=ReadOnlyCheckpointSettings())
        session = object.__new__(actor.ActorSession)
        session.actor_id = 2
        session.session_id = "managed-session"
        with tempfile.TemporaryDirectory() as temp:
            managed = Path(temp) / "managed-logs"
            with mock.patch.dict(os.environ, {"BEES_TRAINING_LOG_DIR": str(managed)}):
                resolved = Path(session._run_logs_dir(options))
            self.assertEqual(resolved.parent, managed.resolve())
            self.assertTrue(resolved.is_dir())
            self.assertTrue(resolved.name.startswith("actor-2-session-"))
            self.assertEqual(options.checkpoint_settings.run_logs_dir, "default-run-logs")

    def test_actor_uses_checkpoint_log_directory_without_managed_override(self):
        options = SimpleNamespace(
            checkpoint_settings=SimpleNamespace(run_logs_dir="default-run-logs")
        )
        session = object.__new__(actor.ActorSession)
        session.actor_id = 2
        session.session_id = "fallback-session"
        with mock.patch.dict(os.environ, {}, clear=True):
            self.assertEqual(
                session._run_logs_dir(options),
                "default-run-logs",
            )

    def test_rollout_horizon_scales_to_ppo_buffer_and_env_count(self):
        settings = SimpleNamespace(
            time_horizon=2048,
            hyperparameters=SimpleNamespace(buffer_size=16384),
        )
        self.assertEqual(actor.rollout_horizon(settings, 384), 43)
        self.assertEqual(actor.rollout_horizon(settings, 32), 513)
        tiny = SimpleNamespace(
            time_horizon=128,
            hyperparameters=SimpleNamespace(buffer_size=1_000_000),
        )
        self.assertEqual(actor.rollout_horizon(tiny, 32), 128)


class BrokerInvariantTests(unittest.TestCase):
    def setUp(self):
        self.options = wan.WanActorOptions(
            actor_count=2,
            envs_per_actor=4,
            min_actors=1,
            broker_port=55051,
            auth_token_file="unused-direct-test",
            max_queued_batches=2,
        )
        self.broker = wan.WanActorBroker(self.options, fake_run_options(), "x" * 32)
        self.broker.initialize_control({"difficulty": 1})
        self.behavior = "BeesRL1v1?team=0"
        self.broker.register_actor(
            {
                "actor_id": 0,
                "control_epoch": 1,
                "behavior_specs": {self.behavior: FakeBehaviorSpec()},
            }
        )
        self.broker._policy_snapshots[self.behavior] = wan._PolicySnapshot(1, "d", b"policy")
        self.broker._policy_epoch = 1

    def _payload(self, actor_id: int, agent_id: str, version: int = 1):
        return {
            "actor_id": actor_id,
            "control_epoch": 1,
            "policy_versions": {self.behavior: version},
            "trajectories": [FakeTrajectory(self.behavior, agent_id)],
        }

    def test_broker_restarts_http_server_only_after_a_started_server_dies(self):
        never_started = wan.WanActorBroker(self.options, fake_run_options(), "x" * 32)
        with mock.patch.object(never_started, "start") as start:
            never_started.ensure_server_alive()
        start.assert_not_called()

        self.broker._server_started_once = True
        self.broker._server = None
        self.broker._server_thread = None
        with mock.patch.object(self.broker, "start") as start:
            self.broker.ensure_server_alive()
        start.assert_called_once_with()

    def test_stale_policy_version_is_rejected(self):
        with self.assertRaisesRegex(wan.StaleActorStateError, "policy versions"):
            self.broker.submit_trajectory_batch(self._payload(0, "agent_0-7", version=0))

    def test_stale_control_epoch_is_rejected(self):
        payload = self._payload(0, "agent_0-7")
        payload["control_epoch"] = 0
        with self.assertRaisesRegex(wan.StaleActorStateError, "control epoch"):
            self.broker.submit_trajectory_batch(payload)

    def test_actor_cannot_claim_another_actors_worker_ids(self):
        with self.assertRaisesRegex(ValueError, "outside actor 0"):
            self.broker.submit_trajectory_batch(self._payload(0, "agent_4-99"))

    def test_current_native_trajectory_is_accepted(self):
        self.assertEqual(self.broker.submit_trajectory_batch(self._payload(0, "agent_3-99")), 1)
        batch = self.broker.next_trajectory_batch(0.01)
        self.assertEqual(batch["actor_id"], 0)
        self.assertEqual(len(batch["trajectories"]), 1)

    def test_policy_or_control_change_drops_queued_old_batches(self):
        self.broker.submit_trajectory_batch(self._payload(0, "agent_2-12"))
        self.broker.request_parameters({"difficulty": 2})
        with self.assertRaises(TimeoutError):
            self.broker.next_trajectory_batch(0.001)

    def test_behavior_spec_mismatch_between_actors_fails_closed(self):
        different = FakeBehaviorSpec()
        different.observation_specs = (FakeObservationSpec((5,)),)
        with self.assertRaisesRegex(ValueError, "behavior specifications differ"):
            self.broker.register_actor(
                {
                    "actor_id": 1,
                    "control_epoch": 1,
                    "behavior_specs": {self.behavior: different},
                }
            )

    def test_partial_initial_policy_set_is_not_exposed_to_actors(self):
        second_behavior = "BeesRL1v1?team=1"
        broker = wan.WanActorBroker(self.options, fake_run_options(), "x" * 32)
        broker.initialize_control(None)
        broker.register_actor(
            {
                "actor_id": 0,
                "control_epoch": 1,
                "behavior_specs": {
                    self.behavior: FakeBehaviorSpec(),
                    second_behavior: FakeBehaviorSpec(),
                },
            }
        )
        broker._policy_snapshots[self.behavior] = wan._PolicySnapshot(1, "a", b"one")
        self.assertEqual(broker.policy_versions, {})
        broker._policy_snapshots[second_behavior] = wan._PolicySnapshot(1, "b", b"two")
        self.assertEqual(broker.policy_versions, {self.behavior: 1, second_behavior: 1})

    def test_cohort_requires_distinct_actor_machines(self):
        options = wan.WanActorOptions(
            actor_count=2,
            envs_per_actor=4,
            min_actors=2,
            auth_token_file="unused-direct-test",
            max_queued_batches=4,
        )
        broker = wan.WanActorBroker(options, fake_run_options(), "x" * 32)
        broker.initialize_control(None)
        for actor_id in (0, 1):
            broker.register_actor(
                {
                    "actor_id": actor_id,
                    "control_epoch": 1,
                    "behavior_specs": {self.behavior: FakeBehaviorSpec()},
                }
            )
        broker._policy_snapshots[self.behavior] = wan._PolicySnapshot(1, "d", b"policy")
        broker._policy_epoch = 1
        broker.submit_trajectory_batch(
            {
                "actor_id": 0,
                "control_epoch": 1,
                "policy_versions": {self.behavior: 1},
                "trajectories": [FakeTrajectory(self.behavior, "agent_0-1")],
            }
        )
        broker.submit_trajectory_batch(
            {
                "actor_id": 1,
                "control_epoch": 1,
                "policy_versions": {self.behavior: 1},
                "trajectories": [FakeTrajectory(self.behavior, "agent_4-1")],
            }
        )
        cohort = broker.next_trajectory_cohort(0.01)
        self.assertEqual({batch["actor_id"] for batch in cohort}, {0, 1})
        with self.assertRaises(queue.Full):
            broker.submit_trajectory_batch(
                {
                    "actor_id": 0,
                    "control_epoch": 1,
                    "policy_versions": {self.behavior: 1},
                    "trajectories": [FakeTrajectory(self.behavior, "agent_0-2")],
                }
            )


class RemoteActorHelperTests(unittest.TestCase):
    def test_ssh_tunnel_exposes_only_loopback_broker_forward(self):
        command = actor.ssh_command(
            "ssh",
            "exeter",
            local_port=55051,
            broker_port=56051,
            options=(),
        )
        rendered = " ".join(command)
        self.assertIn("127.0.0.1:55051:127.0.0.1:56051", rendered)
        self.assertIn("ExitOnForwardFailure=yes", rendered)

    def test_session_validation_enforces_actor_assignment(self):
        session = {
            "protocol_version": wan.WAN_PROTOCOL_VERSION,
            "mlagents_version": wan.EXPECTED_MLAGENTS_VERSION,
            "session_id": "session",
            "actor_count": 2,
            "envs_per_actor": 32,
            "run_options": object(),
        }
        session_id, envs, _options = actor._validate_session(session, 1)
        self.assertEqual(session_id, "session")
        self.assertEqual(envs, 32)
        with self.assertRaisesRegex(RuntimeError, "outside central actor count"):
            actor._validate_session(session, 2)


if __name__ == "__main__":
    unittest.main()
