"""Focused tests for managed remote worker defaults, identity, and tailnet transport."""

from __future__ import annotations

import io
import tempfile
import unittest
import zipfile
from argparse import Namespace
from pathlib import Path
from unittest import mock

import bees_managed_remote_worker as managed


class ManagedRemoteWorkerTests(unittest.TestCase):
    def test_default_envs_are_four_times_available_threads_when_memory_allows(self):
        with (
            mock.patch.object(managed, "_available_cpu_threads", return_value=6),
            mock.patch.object(managed, "_memory_env_limit", return_value=64),
        ):
            self.assertEqual(managed._default_envs(), 24)

    def test_default_envs_respect_actor_capacity_cap(self):
        with (
            mock.patch.object(managed, "_available_cpu_threads", return_value=32),
            mock.patch.object(managed, "_memory_env_limit", return_value=64),
        ):
            self.assertEqual(managed._default_envs(), managed.MAX_ENVS_PER_ACTOR)

    def test_default_envs_are_capped_by_available_memory(self):
        gib = 1024 * 1024 * 1024
        with (
            mock.patch.object(managed, "_available_cpu_threads", return_value=4),
            mock.patch.object(managed, "_available_memory_bytes", return_value=int(3.8 * gib)),
        ):
            self.assertEqual(managed._memory_env_limit(), 2)
            self.assertEqual(managed._default_envs(), 2)

    def test_default_envs_fall_back_to_cpu_when_memory_is_unknown(self):
        with (
            mock.patch.object(managed, "_available_cpu_threads", return_value=4),
            mock.patch.object(managed, "_available_memory_bytes", return_value=None),
        ):
            self.assertEqual(managed._memory_env_limit(), managed.MAX_ENVS_PER_ACTOR)
            self.assertEqual(managed._default_envs(), 16)

    def test_actor_key_is_persistent_per_installation(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            first = managed._load_actor_key(root)
            second = managed._load_actor_key(root)
            self.assertEqual(first, second)
            self.assertEqual(len(first), 32)

    def test_shutdown_request_file_stops_supervisor_loop(self):
        with tempfile.TemporaryDirectory() as temp:
            request = Path(temp) / managed.REMOTE_STOP_REQUEST_FILE
            request.write_text("stop", encoding="ascii")
            stop = [False]

            managed._watch_shutdown_request(request, stop, poll_seconds=0.0)

            self.assertTrue(stop[0])

    def test_pid_file_cleanup_only_removes_current_supervisor_record(self):
        with tempfile.TemporaryDirectory() as temp:
            pid_file = Path(temp) / managed.REMOTE_PID_FILE
            pid_file.write_text("123\n", encoding="ascii")

            managed._clear_pid_file_if_owned(pid_file, 456)
            self.assertTrue(pid_file.exists())

            managed._clear_pid_file_if_owned(pid_file, 123)
            self.assertFalse(pid_file.exists())

    def test_release_metadata_accepts_utf8_bom(self):
        payload = b"\xef\xbb\xbf" + b'{"build_id":"build-1"}'
        self.assertEqual(
            managed._decode_release_metadata(payload)["build_id"],
            "build-1",
        )

    def test_release_metadata_requires_json_object(self):
        with self.assertRaisesRegex(ValueError, "must be a JSON object"):
            managed._decode_release_metadata(b'["build-1"]')

    def test_python_executable_path_preserves_venv_symlink(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            base_python = root / "python-base"
            base_python.write_bytes(b"")
            venv_python = root / "venv" / "bin" / "python"
            venv_python.parent.mkdir(parents=True)
            try:
                venv_python.symlink_to(base_python)
            except OSError as exc:
                self.skipTest(f"symlink creation unavailable: {exc}")

            preserved = managed._python_executable_path(venv_python)

            self.assertEqual(preserved, venv_python.absolute())
            self.assertNotEqual(preserved, venv_python.resolve())

    def test_remote_dependency_health_check_requires_successful_imports(self):
        completed = mock.Mock(returncode=0)
        with mock.patch.object(managed.subprocess, "run", return_value=completed) as run:
            self.assertTrue(managed._python_remote_dependencies_ok(Path("/tmp/python")))
        self.assertIn(
            "import pkg_resources, mlagents, torch, numpy",
            run.call_args.args[0],
        )

        completed.returncode = 1
        with mock.patch.object(managed.subprocess, "run", return_value=completed):
            self.assertFalse(managed._python_remote_dependencies_ok(Path("/tmp/python")))

    def test_unhealthy_python_waits_for_repair_cutover(self):
        args = Namespace()
        process = mock.Mock()
        process.poll.return_value = None
        updater = mock.Mock()
        expected = Path("/tmp/repaired-runtime")
        with (
            mock.patch.object(
                managed,
                "_python_remote_dependencies_ok",
                return_value=False,
            ),
            mock.patch.object(
                managed,
                "_runtime_cutover_selected",
                side_effect=[None, expected],
            ) as selected,
            mock.patch.object(
                managed,
                "_remote_status_summary",
                return_value="[status]",
            ),
            mock.patch.object(managed.time, "sleep"),
        ):
            result = managed._wait_for_dependency_repair_cutover(
                args,
                "trainer-1",
                updater,
                process,
                [False],
            )

        self.assertEqual(result, expected)
        self.assertEqual(selected.call_count, 2)

    def test_healthy_python_does_not_wait_for_repair_cutover(self):
        args = Namespace()
        process = mock.Mock()
        updater = mock.Mock()
        with (
            mock.patch.object(
                managed,
                "_python_remote_dependencies_ok",
                return_value=True,
            ),
            mock.patch.object(
                managed,
                "_runtime_cutover_selected",
            ) as selected,
        ):
            result = managed._wait_for_dependency_repair_cutover(
                args,
                "trainer-1",
                updater,
                process,
                [False],
            )

        self.assertIsNone(result)
        selected.assert_not_called()

    def test_runtime_version_is_read_from_executing_root_not_mutable_archive(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            active = "a" * 40
            (root / "bees-runtime-version.txt").write_text(active, encoding="ascii")
            self.assertEqual(managed._runtime_version_from_root(root), active)

    def test_sha256_runtime_version_is_read_from_packaged_zip(self):
        buffer = io.BytesIO()
        expected = "b" * 64
        with zipfile.ZipFile(buffer, "w") as bundle:
            bundle.writestr("bees-runtime-version.txt", expected)
        self.assertEqual(
            managed._runtime_version_from_zip(buffer.getvalue()),
            expected,
        )

    def test_runtime_version_rejects_missing_or_malformed_marker(self):
        with tempfile.TemporaryDirectory() as temp:
            self.assertEqual(managed._runtime_version_from_root(Path(temp)), "")
        buffer = io.BytesIO()
        with zipfile.ZipFile(buffer, "w") as bundle:
            bundle.writestr("bees-runtime-version.txt", "not-a-sha")
        self.assertEqual(managed._runtime_version_from_zip(buffer.getvalue()), "")

    def test_legacy_gameplay_port_argument_is_accepted_but_not_forwarded(self):
        args = managed._parser().parse_args([
            "--tailnet-bridge", "bridge",
            "--tailnet-state", "state",
            "--tailnet-hostname", "worker",
            "--tailnet-target", "100.64.0.10",
            "--gameplay-port", "7146",
            "--install-root", "install",
            "--runtime-archive", "runtime.zip",
            "--bootstrap-token-file", "bootstrap.token",
            "--worker-token-file", "worker.token",
            "--wan-token-file", "wan.token",
        ])
        self.assertEqual(args.gameplay_port, 7146)
        command = managed._tailnet_forward_command(args)
        self.assertNotIn("127.0.0.1:7146=100.64.0.10:7146", command)

    def test_managed_worker_command_uses_actor_key_and_local_tailnet_broker(self):
        args = Namespace(
            control_port=7150,
            bootstrap_port=7151,
            broker_port=55051,
            worker_token_file="worker.token",
            install_root="install",
            envs=24,
            min_envs=2,
            max_envs=40,
            auto_envs=True,
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
        self.assertEqual(command[command.index("--worker-envs") + 1], "24")
        self.assertEqual(command[command.index("--worker-envs-min") + 1], "2")
        self.assertEqual(command[command.index("--worker-envs-max") + 1], "40")
        self.assertIn("--auto-worker-envs", command)
        self.assertEqual(command[command.index("--envs") + 1], "{worker_envs}")
        self.assertIn("--runtime-ready-file", command)
        ready = command[command.index("--runtime-ready-file") + 1]
        self.assertTrue(ready.endswith("runtime-ready-build.txt"))

    def test_fixed_env_override_disables_auto_optimizer(self):
        args = Namespace(
            control_port=7150,
            broker_port=55051,
            worker_token_file="worker.token",
            install_root="install",
            envs=12,
            min_envs=12,
            max_envs=12,
            auto_envs=False,
            wan_token_file="wan.token",
            torch_device="cpu",
        )
        command = managed._worker_command(args, Path("/runtime"), "b" * 32)
        self.assertNotIn("--auto-worker-envs", command)
        self.assertEqual(command[command.index("--worker-envs") + 1], "12")
        self.assertEqual(command[command.index("--worker-envs-min") + 1], "12")
        self.assertEqual(command[command.index("--worker-envs-max") + 1], "12")

    def test_unhealthy_active_python_stages_repair_without_runtime_change(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            runtime_archive = root / "runtime.zip"
            runtime_archive.write_bytes(b"same-runtime")
            bridge = root / "bridge"
            bridge.write_bytes(b"same-bridge")
            args = Namespace(
                runtime_archive=str(runtime_archive),
                tailnet_bridge=str(bridge),
                worker_token_file=str(root / "worker.token"),
                wan_token_file=str(root / "wan.token"),
                bootstrap_token_file=str(root / "bootstrap.token"),
                bootstrap_port=7151,
            )
            updater = managed.RuntimeUpdater(args, root / "install")
            release = b'{"build_id":"build-1"}'
            with (
                mock.patch.object(
                    updater,
                    "_fetch_bootstrap",
                    return_value=(
                        b"same-runtime",
                        b"worker-token",
                        b"wan-token",
                        b"same-bridge",
                        release,
                    ),
                ),
                mock.patch.object(
                    managed,
                    "_runtime_version_from_zip",
                    return_value="",
                ),
                mock.patch.object(
                    managed,
                    "_python_remote_dependencies_ok",
                    return_value=False,
                ),
                mock.patch.object(
                    updater,
                    "_prepare_python_for_requirements",
                    return_value=root / "healthy-python",
                ) as prepare,
            ):
                updater._stage_once()

            prepare.assert_called_once()
            _, staged_root, _, staged_python, staged_build_id, error = updater.staged()
            self.assertEqual(staged_root, Path(managed.__file__).resolve().parent)
            self.assertEqual(staged_python, root / "healthy-python")
            self.assertEqual(staged_build_id, "build-1")
            self.assertEqual(error, "")

    def test_private_transport_requires_consecutive_authenticated_control_successes(self):
        args = Namespace(
            control_port=7150,
            broker_port=55051,
            bootstrap_port=7151,
        )
        process = mock.Mock()
        process.poll.return_value = None
        stop = [False]

        with (
            mock.patch.object(managed, "_wait_for_ports", return_value=True),
            mock.patch.object(
                managed,
                "_control_status",
                side_effect=[{"desired": {}}, None, {"desired": {}}, {"desired": {}}],
            ) as status,
            mock.patch.object(managed.time, "sleep"),
        ):
            self.assertTrue(
                managed._wait_for_private_transport(
                    args,
                    process,
                    stop,
                    timeout=5.0,
                    required_successes=2,
                )
            )

        self.assertEqual(status.call_count, 4)

    def test_private_transport_does_not_probe_control_until_local_forwarders_exist(self):
        args = Namespace(
            control_port=7150,
            broker_port=55051,
            bootstrap_port=7151,
        )
        process = mock.Mock()
        stop = [False]

        with (
            mock.patch.object(managed, "_wait_for_ports", return_value=False),
            mock.patch.object(managed, "_control_status") as status,
        ):
            self.assertFalse(
                managed._wait_for_private_transport(
                    args,
                    process,
                    stop,
                    timeout=5.0,
                )
            )

        status.assert_not_called()

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


    def test_run_scoped_log_sink_writes_into_worker_uploaded_log_tree(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            sink = managed._RunScopedLogSink(root / "ManagedBuilds" / "logs")
            sink.write("before-run\n")
            self.assertFalse(any(root.rglob("remote-supervisor.log")))

            sink.set_run_id("bees-v20-test")
            sink.write("[Bees WAN actor] session failed: example\n")

            log = (
                root
                / "ManagedBuilds"
                / "logs"
                / "bees-v20-test"
                / "remote-supervisor.log"
            )
            self.assertTrue(log.is_file())
            self.assertIn("session failed", log.read_text(encoding="utf-8"))

    def test_status_summary_assigns_supervisor_log_to_active_run(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            sink = managed._RunScopedLogSink(root / "logs")
            args = Namespace(envs=8)
            status = {
                "desired": {
                    "run_id": "bees-v20-active",
                    "canonical_build_id": "build-a",
                },
                "trainers": [
                    {
                        "trainer_id": "remote-test",
                        "process_state": "running",
                        "stale": False,
                        "build_id": "build-a",
                        "applied_revision": 4,
                        "last_error": "",
                        "worker_capacity": {
                            "auto": True,
                            "current_envs": 8,
                            "min_envs": 1,
                            "max_envs": 16,
                        },
                        "env_optimizer": {
                            "phase": "measuring",
                            "desired_envs": 10,
                            "measured_sps": 1234.5,
                        },
                    }
                ],
            }
            with mock.patch.object(managed, "_control_status", return_value=status):
                summary = managed._remote_status_summary(
                    args,
                    "remote-test",
                    log_sink=sink,
                )

            self.assertIn("state=running", summary)
            self.assertIn("envs=8->10", summary)
            self.assertIn("optimizer=measuring", summary)
            self.assertIn("accepted_sps=1234.5", summary)
            sink.write("captured\n")
            self.assertTrue(
                (root / "logs" / "bees-v20-active" / "remote-supervisor.log").is_file()
            )


if __name__ == "__main__":
    unittest.main()
