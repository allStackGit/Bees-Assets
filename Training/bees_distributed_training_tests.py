"""Focused tests for Bees distributed ML-Agents topology and remote session specs."""

from __future__ import annotations

import json
import os
import tempfile
import unittest
from unittest.mock import patch
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

    def test_topology_rejects_worker_ports_above_tcp_range(self):
        with self.assertRaisesRegex(SystemExit, "valid TCP port range"):
            distributed.training_topology(
                ["config.yaml", "--num-envs=2", "--base-port=65535"],
                distributed.DistributedOptions(),
            )

        with self.assertRaisesRegex(SystemExit, "valid TCP port range"):
            distributed.training_topology(
                ["config.yaml", "--num-envs=8", "--base-port=65530"],
                distributed.DistributedOptions(external_envs=2, remote_spec="remote.json"),
            )

    def test_topology_accepts_highest_valid_worker_port(self):
        total, base_port, workers = distributed.training_topology(
            ["config.yaml", "--num-envs=2", "--base-port=65534"],
            distributed.DistributedOptions(),
        )
        self.assertEqual((total, base_port, workers), (2, 65534, ()))

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
            with patch.dict(os.environ, {distributed.TRAINING_BUILD_ID_ENV: "build-1"}):
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
            self.assertEqual(spec["build_id"], "build-1")
            self.assertEqual(
                spec["unity_args"],
                ["--rl-map-size", "96", "--rl-health=0.5"],
            )
            self.assertEqual(len(spec["identity_sha256"]), 64)

    def test_controlled_environment_args_cannot_override_pinned_spec(self):
        pinned = ("--rl-map-size", "96", "--rl-health=0.5")
        with patch.dict(os.environ, {remote.CONTROL_ENV_ARGS_VARIABLE: json.dumps(list(pinned))}):
            self.assertEqual(remote.controlled_environment_args(pinned), pinned)

        conflicting = ("--rl-map-size", "128")
        with patch.dict(os.environ, {remote.CONTROL_ENV_ARGS_VARIABLE: json.dumps(list(conflicting))}):
            with self.assertRaisesRegex(ValueError, "pinned remote session spec"):
                remote.controlled_environment_args(pinned)

    def test_remote_spec_tampering_fails_closed(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "remote.json"
            with patch.dict(os.environ, {distributed.TRAINING_BUILD_ID_ENV: "build-1"}):
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


class ExternalWorkerServerTests(unittest.TestCase):
    def _assert_startup_failure_stops_server(self, failure_point):
        from types import ModuleType
        from unittest.mock import Mock

        fake_server = Mock()
        fake_server.add_insecure_port.return_value = 0
        if failure_point == "bind":
            fake_server.add_insecure_port.return_value = 0
        elif failure_point == "start":
            fake_server.add_insecure_port.return_value = 5005
            fake_server.start.side_effect = RuntimeError("start failed")

        grpc_module = ModuleType("grpc")
        grpc_module.server = Mock(return_value=fake_server)
        proto_module = ModuleType("mlagents_envs.communicator_objects.unity_to_external_pb2_grpc")
        register_servicer = Mock()
        if failure_point == "registration":
            register_servicer.side_effect = RuntimeError("registration failed")
        proto_module.add_UnityToExternalProtoServicer_to_server = register_servicer
        rpc_module = ModuleType("mlagents_envs.rpc_communicator")
        rpc_module.UnityToExternalServicerImplementation = Mock(return_value=object())
        exception_module = ModuleType("mlagents_envs.exception")
        worker_in_use = type("UnityWorkerInUseException", (Exception,), {})
        exception_module.UnityWorkerInUseException = worker_in_use

        envs_package = ModuleType("mlagents_envs")
        envs_package.__path__ = []
        communicator_objects_package = ModuleType("mlagents_envs.communicator_objects")
        communicator_objects_package.__path__ = []
        modules = {
            "grpc": grpc_module,
            "mlagents_envs": envs_package,
            "mlagents_envs.communicator_objects": communicator_objects_package,
            "mlagents_envs.communicator_objects.unity_to_external_pb2_grpc": proto_module,
            "mlagents_envs.rpc_communicator": rpc_module,
            "mlagents_envs.exception": exception_module,
        }

        class CommunicatorHarness(distributed._LoopbackRpcCommunicatorMixin):
            def __init__(self):
                self.port = 5005
                self.server = object()
                self.is_open = True

            def check_port(self, port):
                self.asserted_port = port

        with patch.dict("sys.modules", modules), patch(
            "concurrent.futures.ThreadPoolExecutor"
        ):
            communicator = CommunicatorHarness()
            with self.assertRaises(worker_in_use):
                communicator.create_server()

        self.assertEqual(communicator.asserted_port, 5005)
        self.assertIsNone(communicator.server)
        self.assertFalse(communicator.is_open)
        fake_server.stop.assert_called_once_with(0)

    def test_registration_failure_stops_partial_server(self):
        self._assert_startup_failure_stops_server("registration")

    def test_bind_failure_stops_partial_server(self):
        self._assert_startup_failure_stops_server("bind")

    def test_start_failure_stops_partial_server(self):
        self._assert_startup_failure_stops_server("start")


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
