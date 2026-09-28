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
import bees_process_safety as process_safety
import bees_training_worker_agent as worker_agent


class WorkerAgentHealthTests(unittest.TestCase):
    def test_managed_child_health_error_is_reported_when_no_stronger_error_exists(self):
        self.assertEqual(
            worker_agent.heartbeat_last_error("", "", "child failed"),
            "child failed",
        )
        self.assertEqual(
            worker_agent.heartbeat_last_error("control failed", "", "child failed"),
            "control failed",
        )
        self.assertEqual(
            worker_agent.heartbeat_last_error("", "prepare failed", "child failed"),
            "prepare failed",
        )


    def test_fresh_starting_health_does_not_become_error_from_process_age(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            health_path = root / "child-health.json"
            health_path.write_text(
                '{"schema_version":1,"token":"token","state":"starting",'
                '"error":"","updated_unix_seconds":1000.0,"pid":123}\n',
                encoding="utf-8",
            )
            managed_process = worker_agent.ManagedProcess()
            managed_process.process = mock.Mock()
            managed_process.process.poll.return_value = None
            managed_process.health_required = True
            managed_process.health_file = health_path
            managed_process.health_token = "token"
            managed_process.started_monotonic = 1.0

            with mock.patch.object(worker_agent.time, "time", return_value=1005.0):
                self.assertEqual(managed_process.health_error(), "")

    def test_throughput_pid_prefers_authenticated_child_health_pid(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            health_path = root / "child-health.json"
            health_path.write_text(
                '{"schema_version":1,"token":"token","state":"ready",'
                '"error":"","updated_unix_seconds":1000.0,"pid":13108}\n',
                encoding="utf-8",
            )
            managed_process = worker_agent.ManagedProcess()
            managed_process.process = mock.Mock()
            managed_process.process.pid = 2884
            managed_process.health_required = True
            managed_process.health_file = health_path
            managed_process.health_token = "token"

            self.assertEqual(managed_process.throughput_expected_pid(), 13108)

    def test_throughput_pid_falls_back_to_launcher_without_valid_child_health(self):
        managed_process = worker_agent.ManagedProcess()
        managed_process.process = mock.Mock()
        managed_process.process.pid = 2884
        managed_process.health_required = False

        self.assertEqual(managed_process.throughput_expected_pid(), 2884)

    def test_stale_starting_health_is_reported_as_hung_startup(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            health_path = root / "child-health.json"
            health_path.write_text(
                '{"schema_version":1,"token":"token","state":"starting",'
                '"error":"","updated_unix_seconds":1000.0,"pid":123}\n',
                encoding="utf-8",
            )
            managed_process = worker_agent.ManagedProcess()
            managed_process.process = mock.Mock()
            managed_process.process.poll.return_value = None
            managed_process.health_required = True
            managed_process.health_file = health_path
            managed_process.health_token = "token"

            with mock.patch.object(
                worker_agent.time,
                "time",
                return_value=1000.0 + worker_agent.CHILD_HEALTH_STALE_SECONDS + 1.0,
            ):
                error = managed_process.health_error()

            self.assertIn("startup health has not refreshed", error)

class WorkerLiveThroughputTests(unittest.TestCase):
    def test_live_throughput_accepts_recent_learner_consumption_rate(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "worker-throughput.json"
            path.write_text(
                '{"pid":13108,"env_count":1,"accepted_steps_total":1000,'
                '"accepted_trajectories_total":10,"learner_consumed_steps_total":900,'
                '"learner_consumed_steps_per_sec":96.25,"upload_queue_depth":0}\n',
                encoding="utf-8",
            )

            result = worker_agent.read_throughput_metrics(
                path,
                expected_pid=13108,
                expected_env_count=1,
            )

            self.assertEqual(result["learner_consumed_steps_total"], 900)
            self.assertEqual(result["learner_consumed_steps_per_sec"], 96.25)


class WorkerControlRetryTests(unittest.TestCase):
    def test_failed_heartbeat_retries_quickly_without_extending_normal_cadence(self):
        self.assertEqual(worker_agent.heartbeat_retry_delay(True, 5.0), 5.0)
        self.assertEqual(worker_agent.heartbeat_retry_delay(False, 5.0), 1.0)
        self.assertEqual(worker_agent.heartbeat_retry_delay(False, 0.5), 0.5)


class WorkerTrafficMetricsTests(unittest.TestCase):
    def test_persisted_network_totals_fill_session_gap_for_same_run(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            throughput = root / "worker-throughput.json"
            traffic = root / worker_agent.NETWORK_TRAFFIC_STATE_FILE
            traffic.write_text(
                '{"run_id":"run-a","sent_bytes_total":1073741824,'
                '"received_bytes_total":536870912}\n',
                encoding="utf-8",
            )
            snapshot = {}

            worker_agent._add_persisted_network_traffic(
                snapshot,
                throughput,
                run_id="run-a",
            )

            self.assertEqual(
                snapshot["throughput"]["network_sent_bytes_total"],
                1073741824,
            )
            self.assertEqual(
                snapshot["throughput"]["network_received_bytes_total"],
                536870912,
            )
            self.assertNotIn("network_mib_per_s", snapshot["throughput"])

    def test_persisted_network_totals_never_cross_run_boundary(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            throughput = root / "worker-throughput.json"
            traffic = root / worker_agent.NETWORK_TRAFFIC_STATE_FILE
            traffic.write_text(
                '{"run_id":"run-old","sent_bytes_total":123,'
                '"received_bytes_total":456}\n',
                encoding="utf-8",
            )
            snapshot = {}

            worker_agent._add_persisted_network_traffic(
                snapshot,
                throughput,
                run_id="run-new",
            )

            self.assertEqual(snapshot, {})

    def test_malformed_persisted_network_totals_are_ignored(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            throughput = root / "worker-throughput.json"
            traffic = root / worker_agent.NETWORK_TRAFFIC_STATE_FILE
            traffic.write_text(
                '{"run_id":"run-a","sent_bytes_total":-1,'
                '"received_bytes_total":200}\n',
                encoding="utf-8",
            )

            self.assertEqual(
                worker_agent.read_persisted_network_traffic(
                    throughput,
                    expected_run_id="run-a",
                ),
                {},
            )

    def test_live_session_network_metrics_win_over_persisted_fallback(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            throughput = root / "worker-throughput.json"
            traffic = root / worker_agent.NETWORK_TRAFFIC_STATE_FILE
            traffic.write_text(
                '{"run_id":"run-a","sent_bytes_total":100,'
                '"received_bytes_total":200}\n',
                encoding="utf-8",
            )
            snapshot = {
                "throughput": {
                    "network_sent_bytes_total": 300,
                    "network_received_bytes_total": 400,
                    "network_mib_per_s": 1.25,
                }
            }

            worker_agent._add_persisted_network_traffic(
                snapshot,
                throughput,
                run_id="run-a",
            )

            self.assertEqual(snapshot["throughput"]["network_sent_bytes_total"], 300)
            self.assertEqual(snapshot["throughput"]["network_received_bytes_total"], 400)
            self.assertEqual(snapshot["throughput"]["network_mib_per_s"], 1.25)


class ManagedRemoteWorkerTests(unittest.TestCase):
    def test_default_envs_start_at_one_per_available_thread_when_memory_allows(self):
        with (
            mock.patch.object(managed, "_available_cpu_threads", return_value=6),
            mock.patch.object(managed, "_memory_env_limit", return_value=64),
        ):
            self.assertEqual(managed._cpu_env_start_limit(), 6)
            self.assertEqual(managed._default_envs(), 6)

    def test_default_envs_are_capped_by_available_memory(self):
        gib = 1024 * 1024 * 1024
        with (
            mock.patch.object(managed, "_available_cpu_threads", return_value=4),
            mock.patch.object(managed, "_available_memory_bytes", return_value=int(3.8 * gib)),
        ):
            self.assertEqual(managed._memory_env_limit(), 5)
            self.assertEqual(managed._default_envs(), 5)

    def test_eight_gib_worker_allows_fourteen_envs_with_one_gib_reserve(self):
        gib = 1024 * 1024 * 1024
        with mock.patch.object(
            managed,
            "_available_memory_bytes",
            return_value=8 * gib,
        ):
            self.assertEqual(managed._memory_env_limit(), 14)

    def test_transient_low_free_memory_does_not_permanently_reduce_auto_capacity(self):
        gib = 1024 * 1024 * 1024
        with (
            mock.patch.object(
                managed,
                "_available_memory_bytes",
                return_value=int(1.4 * gib),
            ),
            mock.patch.object(
                managed,
                "_total_memory_bytes",
                return_value=16 * gib,
            ),
            mock.patch.object(managed, "_available_cpu_threads", return_value=8),
        ):
            self.assertEqual(managed._memory_env_limit(), 1)
            self.assertEqual(managed._default_envs(), 1)

        source = Path(managed.__file__).read_text(encoding="utf-8")
        self.assertIn("args.max_envs = requested_max", source)
        self.assertNotIn("memory_capacity_cap", source)
        self.assertNotIn("cpu_capacity_cap", source)

    def test_four_thread_high_memory_worker_starts_at_four_without_cpu_ceiling(self):
        gib = 1024 * 1024 * 1024
        with (
            mock.patch.object(managed, "_available_cpu_threads", return_value=4),
            mock.patch.object(managed, "_available_memory_bytes", return_value=31 * gib),
        ):
            self.assertEqual(managed._memory_env_limit(), 60)
            self.assertEqual(managed._cpu_env_start_limit(), 4)
            self.assertEqual(managed._default_envs(), 4)

        source = Path(managed.__file__).read_text(encoding="utf-8")
        self.assertIn("args.max_envs = requested_max", source)
        self.assertIn("cpu_start_cap={cpu_start_cap}", source)
        self.assertNotIn("REMOTE_CPU_MAX_ENVS_PER_THREAD", source)
        self.assertNotIn("cpu_capacity_cap", source)

    def test_atomic_text_publication_retries_transient_sharing_failure(self):
        with tempfile.TemporaryDirectory() as temp:
            target = Path(temp) / "state.json"
            real_replace = process_safety.os.replace
            attempts = []

            def flaky_replace(source, destination):
                attempts.append((source, destination))
                if len(attempts) < 3:
                    raise PermissionError("simulated sharing violation")
                return real_replace(source, destination)

            with (
                mock.patch.object(
                    process_safety.os,
                    "replace",
                    side_effect=flaky_replace,
                ),
                mock.patch.object(process_safety.time, "sleep") as sleep,
            ):
                process_safety.atomic_write_text(target, "{\"ok\": true}\n")

            self.assertEqual(target.read_text(encoding="utf-8"), "{\"ok\": true}\n")
            self.assertEqual(len(attempts), 3)
            self.assertEqual(sleep.call_count, 2)
            self.assertEqual(list(target.parent.glob("state.json.tmp-*")), [])

    def test_runtime_updater_retries_transient_connection_reset_without_publishing_error(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            token = root / "bootstrap.token"
            token.write_text("token", encoding="ascii")
            args = Namespace(
                runtime_archive=str(root / "runtime.zip"),
                bootstrap_token_file=str(token),
                bootstrap_port=7151,
            )
            updater = managed.RuntimeUpdater(args, root / "install")
            payload = io.BytesIO()
            with zipfile.ZipFile(payload, "w") as bundle:
                bundle.writestr("bees-remote-runtime.zip", b"runtime")
                bundle.writestr("training-worker.token", b"worker")
                bundle.writestr("wan.token", b"wan")
                bundle.writestr("bees-tailnet-bridge-windows.exe", b"bridge")
                bundle.writestr("bees-tailnet-bridge-linux", b"bridge")
                bundle.writestr("latest-training-release.json", b"{}")

            response = mock.MagicMock()
            response.__enter__.return_value.read.return_value = payload.getvalue()
            response.__exit__.return_value = False
            with (
                mock.patch.object(
                    managed.urllib.request,
                    "urlopen",
                    side_effect=[ConnectionResetError(10054, "reset"), response],
                ) as urlopen,
                mock.patch.object(updater._stop, "wait", return_value=False),
            ):
                result = updater._fetch_bootstrap()

            self.assertEqual(urlopen.call_count, 2)
            self.assertEqual(result[0], b"runtime")
            self.assertEqual(result[1], b"worker")
            self.assertEqual(updater.last_error, "")

    def test_default_envs_fall_back_to_cpu_when_memory_is_unknown(self):
        with (
            mock.patch.object(managed, "_available_cpu_threads", return_value=4),
            mock.patch.object(managed, "_available_memory_bytes", return_value=None),
        ):
            self.assertEqual(managed._memory_env_limit(), managed.MAX_ENVS_PER_ACTOR)
            self.assertEqual(managed._default_envs(), 4)

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

    def test_generated_remote_launchers_pass_the_gameplay_forward_port(self):
        root = Path(__file__).resolve().parents[1]
        windows = (root / "Training" / "bees_remote_bootstrap.ps1").read_text(
            encoding="utf-8"
        )
        linux = (root / "Training" / "bees_remote_bootstrap.sh").read_text(
            encoding="utf-8"
        )
        generator = (root / "Training" / "operator" / "tailnet.js").read_text(
            encoding="utf-8"
        )

        for source in (windows, linux):
            self.assertIn("__BEES_GAMEPLAY_PORT__", source)
            self.assertIn("--gameplay-port", source)
        self.assertEqual(
            generator.count("'__BEES_GAMEPLAY_PORT__': String(gameplayPort)"),
            2,
        )
        self.assertIn("GAMEPLAY_SERVER_PORT", generator)

    def test_tailnet_transport_includes_gameplay_server_and_child_override(self):
        args = Namespace(
            tailnet_bridge="/tmp/bees-tailnet-bridge",
            tailnet_state="/tmp/tailnet-state",
            tailnet_hostname="bees-worker-test",
            tailnet_target="100.80.169.87",
            control_port=7150,
            broker_port=55051,
            bootstrap_port=7151,
            gameplay_port=7146,
        )

        command = managed._tailnet_forward_command(args)
        self.assertIn(
            "127.0.0.1:7146=100.80.169.87:7146",
            command,
        )

        with mock.patch.dict(managed.os.environ, {}, clear=True):
            environment = managed._worker_environment(args)
        self.assertEqual(
            environment[managed.TRAINING_GAMEPLAY_HOST_ENV],
            "127.0.0.1",
        )
        self.assertEqual(
            environment[managed.TRAINING_GAMEPLAY_PORT_ENV],
            "7146",
        )

    def test_supervisor_launches_worker_through_owned_process_container(self):
        process = mock.Mock()
        process.stdout = io.StringIO("")
        with mock.patch.object(managed, "popen_owned", return_value=process) as owned:
            returned, thread = managed._start_logged_process(["python", "worker.py"])
            thread.join(timeout=1.0)

        self.assertIs(returned, process)
        owned.assert_called_once()
        self.assertEqual(owned.call_args.args[0], ["python", "worker.py"])

    def test_terminate_raises_when_child_exit_cannot_be_confirmed(self):
        process = mock.Mock()
        process.pid = 7331
        process.poll.return_value = None
        process.wait.side_effect = TimeoutError("still running")

        with self.assertRaisesRegex(RuntimeError, "did not stop"):
            managed._terminate(process)

        process.terminate.assert_called_once()
        process.kill.assert_called_once()

    def test_supervisor_requests_worker_agent_shutdown_before_force_kill(self):
        with tempfile.TemporaryDirectory() as temp:
            request = Path(temp) / "worker-agent-stop.request"
            process = mock.Mock()
            process.poll.return_value = None
            process.wait.return_value = 0

            self.assertTrue(
                managed._request_graceful_worker_stop(
                    process,
                    request,
                    timeout=7.0,
                )
            )

            self.assertEqual(request.read_text(encoding="ascii"), "stop\n")
            process.wait.assert_called_once_with(timeout=7.0)

    def test_runtime_alignment_waits_until_current_runtime_is_verified_for_canonical_build(self):
        args = Namespace()
        process = mock.Mock()
        process.poll.return_value = None
        updater = mock.Mock()
        updater.verified.side_effect = [
            ("", ""),
            ("build-1", ""),
        ]
        updater.staged.return_value = (
            "",
            None,
            None,
            None,
            "",
            "",
        )
        with (
            mock.patch.object(
                managed,
                "_runtime_cutover_selected",
                return_value=None,
            ) as selected,
            mock.patch.object(
                managed,
                "_control_status",
                return_value={"desired": {"canonical_build_id": "build-1"}},
            ),
            mock.patch.object(managed.time, "sleep"),
        ):
            aligned, cutover = managed._wait_for_runtime_alignment(
                args,
                "trainer-1",
                updater,
                process,
                [False],
            )

        self.assertTrue(aligned)
        self.assertIsNone(cutover)
        updater.start.assert_called_once_with()
        updater.request_refresh.assert_called()
        self.assertGreaterEqual(selected.call_count, 2)

    def test_runtime_alignment_does_not_treat_staged_but_unselected_runtime_as_active(self):
        args = Namespace()
        process = mock.Mock()
        process.poll.side_effect = [None, None, 1]
        updater = mock.Mock()
        updater.verified.return_value = ("build-1", "")
        updater.staged.return_value = (
            "sha",
            Path("/tmp/staged-runtime"),
            None,
            None,
            "build-1",
            "",
        )
        with (
            mock.patch.object(
                managed,
                "_runtime_cutover_selected",
                return_value=None,
            ),
            mock.patch.object(
                managed,
                "_control_status",
                return_value={"desired": {"canonical_build_id": "build-1"}},
            ),
            mock.patch.object(managed.time, "sleep"),
        ):
            aligned, cutover = managed._wait_for_runtime_alignment(
                args,
                "trainer-1",
                updater,
                process,
                [False],
            )

        self.assertFalse(aligned)
        self.assertIsNone(cutover)
        updater.request_refresh.assert_called()

    def test_runtime_alignment_selects_staged_canonical_runtime_before_worker_launch(self):
        args = Namespace()
        process = mock.Mock()
        process.poll.return_value = None
        updater = mock.Mock()
        expected = Path("/tmp/staged-runtime")
        with (
            mock.patch.object(
                managed,
                "_runtime_cutover_selected",
                return_value=expected,
            ),
            mock.patch.object(managed, "_control_status") as status,
        ):
            aligned, cutover = managed._wait_for_runtime_alignment(
                args,
                "trainer-1",
                updater,
                process,
                [False],
            )

        self.assertTrue(aligned)
        self.assertEqual(cutover, expected)
        updater.start.assert_called_once_with()
        status.assert_not_called()

    def test_runtime_updater_stop_is_safe_before_thread_start(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            args = Namespace(runtime_archive=str(root / "missing.zip"))
            updater = managed.RuntimeUpdater(args, root / "install")
            updater.stop()
            self.assertFalse(updater._started)

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

    def test_gameplay_port_argument_is_forwarded_over_tailnet(self):
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
        self.assertIn("127.0.0.1:7146=100.64.0.10:7146", command)

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
        self.assertIn("--shutdown-request-file", command)
        stop_request = command[command.index("--shutdown-request-file") + 1]
        self.assertTrue(
            stop_request.endswith(managed.REMOTE_WORKER_AGENT_STOP_REQUEST_FILE)
        )

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

    def test_runtime_stage_rejects_payload_that_disagrees_with_release_identity(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            runtime_archive = root / "runtime.zip"
            runtime_archive.write_bytes(b"current-runtime")
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
            release = (
                '{"build_id":"build-1","training_runtime":'
                '{"archive_sha256":"' + "0" * 64 + '",'
                '"runtime_version":"' + "a" * 64 + '"}}'
            ).encode("utf-8")
            with (
                mock.patch.object(
                    updater,
                    "_fetch_bootstrap",
                    return_value=(
                        b"downloaded-runtime",
                        b"worker-token",
                        b"wan-token",
                        b"same-bridge",
                        release,
                    ),
                ),
                mock.patch.object(
                    managed,
                    "_runtime_version_from_zip",
                    return_value="a" * 64,
                ),
            ):
                with self.assertRaisesRegex(ValueError, "SHA-256 does not match"):
                    updater._stage_once()

            self.assertFalse((root / "worker.token").exists())
            self.assertFalse((root / "wan.token").exists())

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
            gameplay_port=7146,
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
            gameplay_port=7146,
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
            gameplay_port=7146,
        )
        command = managed._tailnet_forward_command(args)
        self.assertIn("forward-multi", command)
        self.assertIn("127.0.0.1:7150=100.64.0.10:7150", command)
        self.assertIn("127.0.0.1:55051=100.64.0.10:55051", command)
        self.assertIn("127.0.0.1:7151=100.64.0.10:7151", command)
        self.assertIn("127.0.0.1:7146=100.64.0.10:7146", command)
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

    def test_run_scoped_log_sink_rotates_when_bounded_size_is_reached(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp) / "logs"
            sink = managed._RunScopedLogSink(root)
            sink.MAX_BYTES = 8
            sink.set_run_id("run-1")

            sink.write("12345678")
            sink.write("AB")

            current = root / "run-1" / "remote-supervisor.log"
            rotated = current.with_name(current.name + ".1")
            self.assertEqual(current.read_text(encoding="utf-8"), "AB")
            self.assertEqual(rotated.read_text(encoding="utf-8"), "12345678")

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
            self.assertIn("learner_sps=1234.5", summary)
            sink.write("captured\n")
            self.assertTrue(
                (root / "logs" / "bees-v20-active" / "remote-supervisor.log").is_file()
            )


    def test_version_directory_retention_preserves_symlinked_active_venv(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp) / "versions"
            root.mkdir(parents=True)
            base = Path(temp) / "base-python"
            base.write_bytes(b"")
            active = root / "old-active" / "bin" / "python"
            active.parent.mkdir(parents=True)
            try:
                active.symlink_to(base)
            except OSError as exc:
                self.skipTest(f"symlink creation unavailable: {exc}")
            for index in range(3):
                version = root / f"new-{index}"
                version.mkdir()
                (version / "marker").write_text(str(index), encoding="utf-8")

            managed._prune_version_directories(
                root,
                preserve_paths=[active],
                retain=1,
            )

            self.assertTrue(active.parent.parent.is_dir())

    def test_version_directory_retention_preserves_active_and_newest(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp) / "versions"
            root.mkdir(parents=True)
            versions = []
            for index in range(5):
                version = root / f"v{index}"
                version.mkdir()
                (version / "marker").write_text(str(index), encoding="utf-8")
                versions.append(version)

            managed._prune_version_directories(
                root,
                preserve_paths=[versions[0] / "marker", versions[-1] / "marker"],
                retain=2,
            )

            remaining = {path.name for path in root.iterdir() if path.is_dir()}
            self.assertIn("v0", remaining)
            self.assertIn("v4", remaining)
            self.assertLessEqual(len(remaining), 3)


if __name__ == "__main__":
    unittest.main()
