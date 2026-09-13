"""Focused tests for Bees distributed ML-Agents topology helpers."""

from __future__ import annotations

import unittest

import bees_distributed_training as distributed
import bees_remote_worker as remote


class DistributedOptionTests(unittest.TestCase):
    def test_extract_external_envs(self):
        cleaned, options = distributed.extract_distributed_options(
            ["config.yaml", "--num-envs=8", "--bees-external-envs", "3", "--resume"]
        )
        self.assertEqual(cleaned, ["config.yaml", "--num-envs=8", "--resume"])
        self.assertEqual(options.external_envs, 3)

    def test_external_workers_are_suffix(self):
        args = ["config.yaml", "--num-envs", "8", "--base-port=6000"]
        options = distributed.DistributedOptions(external_envs=3)
        total, base_port, workers = distributed.training_topology(args, options)
        self.assertEqual(total, 8)
        self.assertEqual(base_port, 6000)
        self.assertEqual(workers, (5, 6, 7))
        self.assertEqual(distributed.external_worker_ports(base_port, workers), (6005, 6006, 6007))

    def test_external_count_cannot_exceed_total(self):
        with self.assertRaises(SystemExit):
            distributed.training_topology(
                ["config.yaml", "--num-envs=2"],
                distributed.DistributedOptions(external_envs=3),
            )

    def test_zero_external_envs_preserves_standard_topology(self):
        total, base_port, workers = distributed.training_topology(
            ["config.yaml"], distributed.DistributedOptions()
        )
        self.assertEqual(total, 1)
        self.assertEqual(base_port, distributed.DEFAULT_BASE_PORT)
        self.assertEqual(workers, ())


class RemoteWorkerTests(unittest.TestCase):
    def test_parse_worker_ranges(self):
        self.assertEqual(remote.parse_worker_ids("8-10,12,14-15"), (8, 9, 10, 12, 14, 15))

    def test_duplicate_worker_rejected(self):
        with self.assertRaises(ValueError):
            remote.parse_worker_ids("8-10,10")

    def test_ssh_forwards_match_worker_ports(self):
        command = remote.ssh_command("ssh", "trainer", (5013, 5014), ())
        rendered = " ".join(command)
        self.assertIn("127.0.0.1:5013:127.0.0.1:5013", rendered)
        self.assertIn("127.0.0.1:5014:127.0.0.1:5014", rendered)
        self.assertIn("ExitOnForwardFailure=yes", rendered)

    def test_unity_uses_forwarded_port(self):
        command = remote.unity_command(
            "Bees.exe", 5013, no_graphics=True, unity_args=("--rl-map-size", "64")
        )
        self.assertEqual(command[:3], ["Bees.exe", "-nographics", "-batchmode"])
        self.assertIn("--mlagents-port", command)
        self.assertEqual(command[command.index("--mlagents-port") + 1], "5013")
        self.assertEqual(command[-2:], ["--rl-map-size", "64"])


if __name__ == "__main__":
    unittest.main()
