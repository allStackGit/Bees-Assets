"""Focused tests for managed remote worker defaults and identity."""

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

    def test_managed_worker_command_uses_actor_key_not_fixed_slot(self):
        args = Namespace(
            control_port=7150,
            worker_token_file="worker.token",
            install_root="install",
            envs=24,
            learner="user@learner",
            wan_token_file="wan.token",
            torch_device="cpu",
            ssh_port=22,
        )
        command = managed._worker_command(args, Path("/runtime"), "a" * 32)
        self.assertIn("--actor-key", command)
        self.assertIn("a" * 32, command)
        self.assertNotIn("--actor-id", command)
        env_index = command.index("--envs")
        self.assertEqual(command[env_index + 1], "24")


if __name__ == "__main__":
    unittest.main()
