"""Focused tests for managed remote worker defaults, identity, and tailnet transport."""

from __future__ import annotations

import tempfile
import unittest
from argparse import Namespace
from pathlib import Path
from unittest import mock

import bees_managed_remote_worker as managed


class ManagedRemoteWorkerTests(unittest.TestCase):
    def test_default_envs_are_four_times_available_threads(self):
        with mock.patch.object(managed, "_available_cpu_threads", return_value=6):
            self.assertEqual(managed._default_envs(), 24)

    def test_default_envs_respect_actor_capacity_cap(self):
        with mock.patch.object(managed, "_available_cpu_threads", return_value=32):
            self.assertEqual(managed._default_envs(), managed.MAX_ENVS_PER_ACTOR)

    def test_actor_key_is_persistent_per_installation(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            first = managed._load_actor_key(root)
            second = managed._load_actor_key(root)
            self.assertEqual(first, second)
            self.assertEqual(len(first), 32)

    def test_managed_worker_command_uses_actor_key_and_local_tailnet_broker(self):
        args = Namespace(
            control_port=7150,
            bootstrap_port=7151,
            broker_port=55051,
            worker_token_file="worker.token",
            install_root="install",
            envs=24,
            wan_token_file="wan.token",
            torch_device="cpu",
        )
        command = managed._worker_command(args, Path("/runtime"), "a" * 32)
        self.assertIn("--actor-key", command)
        self.assertIn("a" * 32, command)
        self.assertNotIn("--actor-id", command)
        self.assertNotIn("--ssh", command)
        self.assertEqual(command[command.index("--broker-host") + 1], "127.0.0.1")
        self.assertEqual(command[command.index("--broker-port") + 1], "55051")
        self.assertEqual(command[command.index("--envs") + 1], "24")
        self.assertIn("--runtime-ready-file", command)
        ready = command[command.index("--runtime-ready-file") + 1]
        self.assertTrue(ready.endswith("runtime-ready-build.txt"))

    def test_tailnet_forward_command_maps_control_and_broker(self):
        args = Namespace(
            tailnet_bridge="bridge",
            tailnet_state="state",
            tailnet_hostname="bees-worker-test",
            tailnet_target="100.64.0.10",
            control_port=7150,
            bootstrap_port=7151,
            broker_port=55051,
        )
        command = managed._tailnet_forward_command(args)
        self.assertIn("forward-multi", command)
        self.assertIn("127.0.0.1:7150=100.64.0.10:7150", command)
        self.assertIn("127.0.0.1:55051=100.64.0.10:55051", command)
        self.assertIn("127.0.0.1:7151=100.64.0.10:7151", command)
        self.assertNotIn("ssh", " ".join(command).lower())


if __name__ == "__main__":
    unittest.main()
