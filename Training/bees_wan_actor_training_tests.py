"""Focused tests for WAN actor/learner transport invariants."""

from __future__ import annotations

import json
import tempfile
import unittest
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

    def test_stale_policy_version_is_rejected(self):
        payload = {
            "actor_id": 0,
            "control_epoch": 1,
            "policy_versions": {self.behavior: 0},
            "trajectories": [FakeTrajectory(self.behavior, "agent_0-7")],
        }
        with self.assertRaisesRegex(wan.StaleActorStateError, "policy versions"):
            self.broker.submit_trajectory_batch(payload)

    def test_stale_control_epoch_is_rejected(self):
        payload = {
            "actor_id": 0,
            "control_epoch": 0,
            "policy_versions": {self.behavior: 1},
            "trajectories": [FakeTrajectory(self.behavior, "agent_0-7")],
        }
        with self.assertRaisesRegex(wan.StaleActorStateError, "control epoch"):
            self.broker.submit_trajectory_batch(payload)

    def test_actor_cannot_claim_another_actors_worker_ids(self):
        payload = {
            "actor_id": 0,
            "control_epoch": 1,
            "policy_versions": {self.behavior: 1},
            # actor 0 owns workers 0-3; worker 4 belongs to actor 1.
            "trajectories": [FakeTrajectory(self.behavior, "agent_4-99")],
        }
        with self.assertRaisesRegex(ValueError, "outside actor 0"):
            self.broker.submit_trajectory_batch(payload)

    def test_current_native_trajectory_is_accepted(self):
        payload = {
            "actor_id": 0,
            "control_epoch": 1,
            "policy_versions": {self.behavior: 1},
            "trajectories": [FakeTrajectory(self.behavior, "agent_3-99")],
        }
        self.assertEqual(self.broker.submit_trajectory_batch(payload), 1)
        batch = self.broker.next_trajectory_batch(0.01)
        self.assertEqual(batch["actor_id"], 0)
        self.assertEqual(len(batch["trajectories"]), 1)

    def test_policy_or_control_change_drops_queued_old_batches(self):
        payload = {
            "actor_id": 0,
            "control_epoch": 1,
            "policy_versions": {self.behavior: 1},
            "trajectories": [FakeTrajectory(self.behavior, "agent_2-12")],
        }
        self.broker.submit_trajectory_batch(payload)
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
