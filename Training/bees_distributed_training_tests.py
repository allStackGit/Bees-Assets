"""Focused tests for Bees distributed ML-Agents topology and remote session specs."""

from __future__ import annotations

import json
import tempfile
import unittest
from pathlib import Path

import bees_distributed_training as distributed
import bees_remote_worker as remote


class DistributedOptionTests(unittest.TestCase):
    def test_extract_external_envs_and_spec(self):
        cleaned, options = distributed.extract_distributed_options(
            [
                "config.yaml",
                "--num-envs=8",
                "--bees-external-envs",
                "3",
                "--bees-remote-spec",
                "D:/run/remote.json",
                "--resume",
            ]
        )
        self.assertEqual(cleaned, ["config.yaml", "--num-envs=8", "--resume"])
        self.assertEqual(options.external_envs, 3)
        self.assertEqual(options.remote_spec, "D:/run/remote.json")

    def test_external_envs_require_remote_spec(self):
        with self.assertRaisesRegex(SystemExit, distributed.REMOTE_SPEC_FLAG):
            distributed.extract_distributed_options(
                ["config.yaml", "--num-envs=8", "--bees-external-envs", "3"]
            )

    def test_remote_spec_without_external_envs_is_rejected(self):
        with self.assertRaisesRegex(SystemExit, distributed.EXTERNAL_ENVS_FLAG):
            distributed.extract_distributed_options(
                ["config.yaml", "--bees-remote-spec", "remote.json"]
            )

    def test_external_workers_are_suffix(self):
        args = ["config.yaml", "--num-envs", "8", "--base-port=6000"]
        options = distributed.DistributedOptions(external_envs=3, remote_spec="remote.json")
        total, base_port, workers = distributed.training_topology(args, options)
        self.assertEqual(total, 8)
        self.assertEqual(base_port, 6000)
        self.assertEqual(workers, (5, 6, 7))
        self.assertEqual(distributed.external_worker_ports(base_port, workers), (6005, 6006, 6007))

    def test_external_count_cannot_exceed_total(self):
        with self.assertRaises(SystemExit):
            distributed.training_topology(
                ["config.yaml", "--num-envs=2"],
                distributed.DistributedOptions(external_envs=3, remote_spec="remote.json"),
            )

    def test_zero_external_envs_preserves_standard_topology(self):
        total, base_port, workers = distributed.training_topology(
            ["config.yaml"], distributed.DistributedOptions()
        )
        self.assertEqual(total, 1)
        self.assertEqual(base_port, distributed.DEFAULT_BASE_PORT)
        self.assertEqual(workers, ())

    def test_unity_environment_args_are_exact_remainder(self):
        args = [
            "config.yaml",
            "--run-id",
            "run-1",
            "--env-args",
            "--rl-map-size",
            "64",
            "--rl-timeout-seconds=30",
        ]
        self.assertEqual(
            distributed.unity_environment_args(args),
            ("--rl-map-size", "64", "--rl-timeout-seconds=30"),
        )

    def test_remote_spec_round_trip_pins_worker_ids_run_and_unity_args(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "remote.json"
            written = distributed.write_remote_worker_spec(
                path,
                [
                    "config.yaml",
                    "--run-id=distributed-001",
                    "--env-args",
                    "--rl-map-size",
                    "96",
                    "--rl-health=0.5",
                ],
                base_port=5005,
                worker_ids=(6, 7),
            )
            spec = distributed.load_remote_worker_spec(written)

            self.assertEqual(spec["worker_ids"], [6, 7])
            self.assertEqual(spec["base_port"], 5005)
            self.assertEqual(spec["run_id"], "distributed-001")
            self.assertEqual(
                spec["unity_args"],
                ["--rl-map-size", "96", "--rl-health=0.5"],
            )
            self.assertEqual(len(spec["identity_sha256"]), 64)

    def test_remote_spec_tampering_fails_closed(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "remote.json"
            distributed.write_remote_worker_spec(
                path,
                ["config.yaml", "--run-id=distributed-001"],
                base_port=5005,
                worker_ids=(1,),
            )
            value = json.loads(path.read_text(encoding="utf-8"))
            value["base_port"] = 6000
            path.write_text(json.dumps(value), encoding="utf-8")

            with self.assertRaisesRegex(ValueError, "identity hash"):
                distributed.load_remote_worker_spec(path)


class RemoteWorkerTests(unittest.TestCase):
    def test_parse_worker_ranges(self):
        self.assertEqual(remote.parse_worker_ids("8-10,12,14-15"), (8, 9, 10, 12, 14, 15))

    def test_duplicate_worker_rejected(self):
        with self.assertRaises(ValueError):
            remote.parse_worker_ids("8-10,10")

    def test_remote_machine_may_select_only_workers_assigned_by_spec(self):
        spec = {"worker_ids": [8, 9, 10, 11]}
        self.assertEqual(remote.select_worker_ids(spec, "9-10"), (9, 10))
        with self.assertRaisesRegex(ValueError, "not assigned"):
            remote.select_worker_ids(spec, "12")

    def test_ssh_forwards_match_worker_ports(self):
        command = remote.ssh_command("ssh", "trainer", (5013, 5014), ())
        rendered = " ".join(command)
        self.assertIn("127.0.0.1:5013:127.0.0.1:5013", rendered)
        self.assertIn("127.0.0.1:5014:127.0.0.1:5014", rendered)
        self.assertIn("ExitOnForwardFailure=yes", rendered)

    def test_unity_uses_forwarded_port_and_pinned_args(self):
        command = remote.unity_command(
            "Bees.exe", 5013, no_graphics=True, unity_args=("--rl-map-size", "64")
        )
        self.assertEqual(command[:3], ["Bees.exe", "-nographics", "-batchmode"])
        self.assertIn("--mlagents-port", command)
        self.assertEqual(command[command.index("--mlagents-port") + 1], "5013")
        self.assertEqual(command[-2:], ["--rl-map-size", "64"])


if __name__ == "__main__":
    unittest.main()
