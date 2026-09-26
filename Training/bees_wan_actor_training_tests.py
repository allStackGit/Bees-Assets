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
        with tempfile.TemporaryDirectory() as temp:
            managed = Path(temp) / "managed-logs"
            with mock.patch.dict(os.environ, {"BEES_TRAINING_LOG_DIR": str(managed)}):
                resolved = actor.ActorSession._run_logs_dir(options)
            self.assertEqual(Path(resolved), managed.resolve())
            self.assertTrue(managed.is_dir())
            self.assertEqual(options.checkpoint_settings.run_logs_dir, "default-run-logs")

    def test_actor_uses_checkpoint_log_directory_without_managed_override(self):
        options = SimpleNamespace(
            checkpoint_settings=SimpleNamespace(run_logs_dir="default-run-logs")
        )
        with mock.patch.dict(os.environ, {}, clear=True):
            self.assertEqual(
                actor.ActorSession._run_logs_dir(options),
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
