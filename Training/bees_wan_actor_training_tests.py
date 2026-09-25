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
