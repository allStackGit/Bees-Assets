"""Focused tests for managed remote worker defaults, identity, and tailnet transport."""

from __future__ import annotations

import http.client
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
    def test_reconciliation_metrics_use_desired_run_before_child_launch(self):
        self.assertEqual(
            worker_agent.effective_metrics_run_id(
                {"run_id": "run-current"},
                "",
            ),
            "run-current",
        )
        self.assertEqual(
            worker_agent.effective_metrics_run_id(
                {"run_id": ""},
                "run-managed",
            ),
            "run-managed",
        )

    def test_status_surfaces_active_reconciliation_before_historical_errors(self):
        root = Path(__file__).resolve().parents[1]
        status = (root / "Training" / "operator" / "status.js").read_text(
            encoding="utf-8"
        )
        worker = (root / "Training" / "bees_training_worker_agent.py").read_text(
            encoding="utf-8"
        )

        self.assertIn("const reconciliation = metrics.reconciliation;", status)
        self.assertIn(
            "'Reconcile, ' + ageLabel(seconds + snapshotLagSeconds) + ': ' + phase",
            status,
        )
        self.assertIn('set_reconciliation_phase("ensuring canonical build")', worker)
        self.assertIn('set_reconciliation_phase("launching managed actor")', worker)
        self.assertIn("metrics=current_metrics(metrics_run_id())", worker)

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

    def test_persisted_network_totals_survive_worker_agent_restart_before_actor_launch(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            traffic = root / worker_agent.NETWORK_TRAFFIC_STATE_FILE
            traffic.write_text(
                '{"run_id":"run-a","sent_bytes_total":3221225472,'
                '"received_bytes_total":1073741824}\n',
                encoding="utf-8",
            )
            snapshot = {}

            worker_agent._add_persisted_network_traffic(
                snapshot,
                None,
                run_id="run-a",
                install_root=root,
            )

            self.assertEqual(
                snapshot["throughput"]["network_sent_bytes_total"],
                3221225472,
            )
            self.assertEqual(
                snapshot["throughput"]["network_received_bytes_total"],
                1073741824,
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
            self.assertEqual(
                managed._default_envs(),
                4,
                "automatic startup must honor both the memory safety bound and the conservative CPU start limit",
            )

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
            response.__enter__.return_value.read.side_effect = [payload.getvalue(), b""]
            response.__enter__.return_value.headers.get.return_value = '"bundle-1"'
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
            self.assertEqual(result[6], '"bundle-1"')
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

    def test_windows_runtime_handoff_preserves_spaced_paths_as_argv(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp) / "Seagrams Crown" / "BeesTraining"
            python_executable = root / ".venv" / "Scripts" / "python.exe"
            script = root / "RuntimeVersions" / ("a" * 64) / "bees_managed_remote_worker.py"
            working_directory = root
            process = mock.Mock()
            process.pid = 8123
            process.poll.return_value = None

            with (
                mock.patch.object(managed.os, "name", "nt"),
                mock.patch.object(managed.os, "execv") as execv,
                mock.patch.object(managed.subprocess, "Popen", return_value=process) as popen,
                mock.patch.object(managed.time, "sleep"),
            ):
                returned = managed._activate_staged_runtime(
                    python_executable,
                    script,
                    ["--install-root", str(root)],
                    working_directory=working_directory,
                )

            self.assertIs(returned, process)
            execv.assert_not_called()
            command = popen.call_args.args[0]
            self.assertEqual(command[0], str(python_executable.absolute()))
            self.assertEqual(command[1], str(script))
            self.assertEqual(command[2:], ["--install-root", str(root)])
            self.assertEqual(
                popen.call_args.kwargs["cwd"],
                str(working_directory),
            )
            self.assertTrue(popen.call_args.kwargs["close_fds"])
            self.assertIs(popen.call_args.kwargs["stdout"], managed.sys.stdout)
            self.assertIs(popen.call_args.kwargs["stderr"], managed.sys.stderr)

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

    def test_windows_launcher_does_not_equate_alive_supervisor_with_connected_worker(self):
        root = Path(__file__).resolve().parents[1]
        windows = (root / "Training" / "bees_remote_bootstrap.ps1").read_text(
            encoding="utf-8"
        )

        self.assertIn("Get-FreshSupervisorTail", windows)
        self.assertIn("waiting for matching Training runtime before rollout", windows)
        self.assertIn("private tailnet forwarder failed to become ready", windows)
        self.assertIn("startup state:", windows)
        self.assertIn("supervisor started in the background", windows)
        self.assertNotIn(
            'worker started in the background (PID $($process.Id))',
            windows,
        )

    def test_linux_launcher_serializes_full_bootstrap_and_repair_transaction(self):
        root = Path(__file__).resolve().parents[1]
        linux = (root / "Training" / "bees_remote_bootstrap.sh").read_text(
            encoding="utf-8"
        )

        lock = linux.index('BOOTSTRAP_LOCK_FILE="$INSTALL_ROOT/remote-bootstrap.lock"')
        acquire = linux.index("\nacquire_bootstrap_lock\n", lock)
        pid_state = linux.index('SUPERVISOR_PID_FILE="$INSTALL_ROOT/remote-worker.pid"')
        runtime_stage = linux.index("[Bees remote] Stage 1/5", pid_state)
        self.assertLess(lock, acquire)
        self.assertLess(acquire, pid_state)
        self.assertLess(pid_state, runtime_stage)
        self.assertIn('flock -w 60 "$BOOTSTRAP_LOCK_FD"', linux)
        self.assertIn('ln -s "$$" "$BOOTSTRAP_LOCK_LINK"', linux)
        self.assertIn('[[ "$owner" == "$$" ]]', linux)
        self.assertIn("trap release_bootstrap_lock EXIT", linux)

    def test_windows_launcher_rerun_repairs_live_but_unhealthy_supervisor(self):
        root = Path(__file__).resolve().parents[1]
        windows = (root / "Training" / "bees_remote_bootstrap.ps1").read_text(
            encoding="utf-8"
        )

        self.assertTrue(windows.rstrip().endswith("exit 0"))
        self.assertEqual(windows.count("function Test-SupervisorControlHealthy"), 1)
        self.assertEqual(
            windows.count(
                "[Bees remote] close this shell freely; use bees-remote-worker.cmd stop to stop the worker."
            ),
            1,
        )
        self.assertIn("function Test-SupervisorControlHealthy", windows)
        self.assertIn("function Get-LocalTrainerId", windows)
        self.assertIn("/v1/status", windows)
        self.assertIn("request.Timeout=3000", windows)
        self.assertIn("for($attempt=1;$attempt -le 3;$attempt++)", windows)
        self.assertIn("if([bool]$record.stale){return $false}", windows)
        self.assertIn("ControlUnavailable:", windows)
        self.assertIn("function Test-TrainerFresh", windows)
        self.assertIn("$unhealthyCycles=0", windows)
        self.assertIn("$unhealthyCycles -ge 3", windows)
        self.assertIn("Invoke-LauncherRepair", windows)
        self.assertIn("$RepairTimeoutSeconds=180", windows)
        self.assertIn("remote-monitor-repair.out.log", windows)
        self.assertIn("remote-monitor-repair.err.log", windows)
        self.assertIn(
            "Start-Process -FilePath $env:COMSPEC",
            windows,
        )
        self.assertIn(
            "taskkill.exe /PID $script:RepairProcess.Id /T /F",
            windows,
        )
        self.assertNotIn(
            "try {& $env:COMSPEC /d /c $command *> $null} catch {}",
            windows,
        )
        self.assertIn("function Restart-UnhealthySupervisor", windows)
        self.assertIn("unhealthy supervisor stopped cleanly; continuing bootstrap", windows)
        self.assertIn("forcing the stale remote supervisor to terminate", windows)
        self.assertIn("if(Test-SupervisorControlHealthy)", windows)
        self.assertIn(
            "worker is already running and authenticated learner control is healthy",
            windows,
        )
        self.assertNotIn(
            'worker is already running in the background (PID $($existingProcess.Id)).',
            windows,
        )

    def test_linux_launcher_and_systemd_watchdog_repair_unhealthy_supervisor(self):
        root = Path(__file__).resolve().parents[1]
        linux = (root / "Training" / "bees_remote_bootstrap.sh").read_text(
            encoding="utf-8"
        )

        self.assertIn("supervisor_control_probe_once()", linux)
        self.assertIn("supervisor_control_healthy()", linux)
        self.assertIn('f"http://127.0.0.1:{port}/v1/status"', linux)
        self.assertIn("timeout=3.0", linux)
        self.assertIn('trainer_id = f"remote-{socket.gethostname().lower()}-{actor_key[:8]}"', linux)
        self.assertIn('bool(record.get("stale", False))', linux)
        self.assertIn('startswith("ControlUnavailable:")', linux)
        self.assertIn("restart_unhealthy_supervisor()", linux)
        self.assertIn("unhealthy supervisor stopped cleanly; continuing bootstrap", linux)
        self.assertIn("forcing the stale remote supervisor to terminate", linux)
        self.assertIn("if supervisor_control_healthy; then", linux)
        self.assertIn(
            "worker is already running and authenticated learner control is healthy",
            linux,
        )
        self.assertIn("CONTROL_FAILURES=0", linux)
        self.assertIn("CONTROL_FAILURES >= 3", linux)
        self.assertIn(
            "watchdog observed repeated authenticated control failures; invoking launcher repair",
            linux,
        )
        self.assertNotIn(
            'worker is already running in the background (PID $PID).',
            linux,
        )

    def test_generated_remote_launchers_and_learner_gateway_share_gameplay_port(self):
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
        gateway = (root / "Tools~" / "bees-tailnet-bridge" / "main.go").read_text(
            encoding="utf-8"
        )

        for source in (windows, linux):
            self.assertIn("__BEES_GAMEPLAY_PORT__", source)
            self.assertIn("--gameplay-port", source)
        self.assertEqual(
            generator.count("'__BEES_GAMEPLAY_PORT__': String(gameplayPort)"),
            2,
        )
        self.assertIn("const gameplayPort = GAMEPLAY_SERVER_PORT;", generator)
        self.assertIn("'--gameplay-port', String(gameplayPort)", generator)
        self.assertIn('fs.Int("gameplay-port", 7146', gateway)
        self.assertIn('"gameplay"', gateway)
        self.assertIn('localDial(fmt.Sprintf("127.0.0.1:%d", gameplayPort))', gateway)

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
        self.assertEqual(
            owned.call_args.kwargs.get("start_new_session", False),
            managed.os.name != "nt",
        )

    def test_supervisor_lock_dispatches_to_windows_mutex_on_windows(self):
        token = object()
        root = Path("C:/BeesTraining")
        with (
            mock.patch.object(managed.os, "name", "nt"),
            mock.patch.object(
                managed,
                "_acquire_windows_supervisor_lock",
                return_value=token,
            ) as acquire,
        ):
            self.assertIs(managed._acquire_supervisor_lock(root), token)
        acquire.assert_called_once_with(root)

    @unittest.skipIf(managed.os.name == "nt", "POSIX flock ownership only")
    def test_posix_supervisor_lock_allows_only_one_owner_per_install_root(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            first = managed._acquire_posix_supervisor_lock(root)
            self.assertIsNotNone(first)
            try:
                second = managed._acquire_posix_supervisor_lock(root)
                self.assertIsNone(second)
            finally:
                first.close()

            third = managed._acquire_posix_supervisor_lock(root)
            self.assertIsNotNone(third)
            third.close()

    def test_terminate_raises_when_child_exit_cannot_be_confirmed(self):
        process = mock.Mock()
        process.pid = 7331
        process.poll.return_value = None
        process.wait.side_effect = TimeoutError("still running")

        with (
            mock.patch.object(managed.os, "name", "posix"),
            mock.patch.object(
                managed.os,
                "killpg",
                side_effect=OSError("no group"),
                create=True,
            ),
            mock.patch.object(managed.signal, "SIGKILL", 9, create=True),
            self.assertRaisesRegex(RuntimeError, "did not stop"),
        ):
            managed._terminate(process)

        process.terminate.assert_called_once()
        process.kill.assert_called_once()

    def test_posix_terminate_kills_entire_managed_process_group(self):
        process = mock.Mock()
        process.pid = 7332
        process.poll.return_value = None
        process.wait.side_effect = [TimeoutError("term timed out"), 0]

        with (
            mock.patch.object(managed.os, "name", "posix"),
            mock.patch.object(managed.os, "killpg", create=True) as killpg,
            mock.patch.object(managed.signal, "SIGKILL", 9, create=True),
        ):
            managed._terminate(process)

        self.assertEqual(
            killpg.call_args_list,
            [
                mock.call(7332, managed.signal.SIGTERM),
                mock.call(7332, getattr(managed.signal, "SIGKILL", 9)),
            ],
        )
        process.terminate.assert_not_called()
        process.kill.assert_not_called()

    def test_windows_terminate_is_bounded_after_force_tree_kill(self):
        process = mock.Mock()
        process.pid = 8124
        process.poll.return_value = None
        process.wait.side_effect = [TimeoutError("taskkill wait"), 0]

        with (
            mock.patch.object(managed.os, "name", "nt"),
            mock.patch.object(managed.subprocess, "run") as run,
        ):
            managed._terminate(process)

        run.assert_called_once()
        self.assertEqual(
            run.call_args.args[0],
            ["taskkill", "/PID", "8124", "/T", "/F"],
        )
        self.assertEqual(
            process.wait.call_args_list,
            [mock.call(timeout=5), mock.call(timeout=5)],
        )
        process.kill.assert_called_once()

    def test_worker_recovery_uses_shorter_graceful_cleanup_budget(self):
        self.assertEqual(managed._worker_cleanup_grace_seconds(True), 8.0)
        self.assertEqual(managed._worker_cleanup_grace_seconds(False), 30.0)

    def test_tailnet_repair_preserves_managed_worker_lifecycle(self):
        source = Path(managed.__file__).read_text(encoding="utf-8")
        self.assertIn(
            "restarting private transport while keeping the managed worker alive",
            source,
        )
        self.assertIn(
            '"[Bees remote] private transport restored without restarting "',
            source,
        )
        self.assertIn('"the managed worker."', source)
        inner_loop = source[source.index("while (\n                        runtime_cutover is None"):]
        inner_loop = inner_loop[:inner_loop.index("            except KeyboardInterrupt:")]
        self.assertNotIn("and tailnet.poll() is None", inner_loop)

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
        args = Namespace(transport_watchdog_seconds=30.0)
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
        args = Namespace(transport_watchdog_seconds=30.0)
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
        args = Namespace(transport_watchdog_seconds=30.0)
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

    def test_runtime_alignment_preserves_tailnet_while_central_control_restarts(self):
        args = Namespace(transport_watchdog_seconds=30.0)
        process = mock.Mock()
        # Two iterations of central unavailability must not recycle a live forwarder. The
        # function returns only once the forwarder itself exits.
        process.poll.side_effect = [None, None, 1]
        updater = mock.Mock()
        updater.verified.return_value = ("", "")
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
            ),
            mock.patch.object(managed, "_control_status", return_value=None),
            mock.patch.object(
                managed.time,
                "monotonic",
                side_effect=[100.0, 130.0],
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
        self.assertEqual(process.poll.call_count, 3)
        self.assertGreaterEqual(updater.request_refresh.call_count, 2)

    def test_previous_stale_record_does_not_kill_fresh_worker_before_registration(self):
        record = {"stale": True}

        self.assertFalse(
            managed.stale_trainer_requires_recycle(
                record,
                grace_started_monotonic=100.0,
                now=129.9,
            )
        )
        self.assertTrue(
            managed.stale_trainer_requires_recycle(
                record,
                grace_started_monotonic=100.0,
                now=130.0,
            )
        )
        self.assertFalse(
            managed.stale_trainer_requires_recycle(
                {"stale": False},
                grace_started_monotonic=100.0,
                now=1000.0,
            )
        )

    def test_central_service_unavailability_is_not_a_private_transport_recycle_signal(self):
        source = Path(managed.__file__).read_text(encoding="utf-8")
        self.assertNotIn(
            "authenticated learner control has been unreachable for",
            source,
        )
        self.assertNotIn(
            "authenticated WAN broker has been unreachable for",
            source,
        )
        self.assertNotIn("session_failure_watchdog = _SessionFailureWatchdog()", source)
        self.assertIn(
            "Do not tear down a healthy private network merely because the central",
            source,
        )
        self.assertIn(
            "trainer heartbeat remained STALE beyond the",
            source,
        )
        self.assertIn(
            "stale_recycle_grace_started_monotonic = now",
            source,
        )

    def test_bootstrap_identity_probe_uses_head_without_downloading_bundle(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            token = root / "bootstrap.token"
            token.write_text("secret", encoding="ascii")
            args = Namespace(
                runtime_archive=str(root / "runtime.zip"),
                bootstrap_token_file=str(token),
                bootstrap_port=7151,
            )
            updater = managed.RuntimeUpdater(args, root / "install")
            response = mock.MagicMock()
            response.__enter__.return_value.headers.get.return_value = '"bundle-identity"'
            response.__exit__.return_value = False

            with mock.patch.object(
                managed.urllib.request,
                "urlopen",
                return_value=response,
            ) as urlopen:
                identity = updater._fetch_bootstrap_identity()

            self.assertEqual(identity, '"bundle-identity"')
            request = urlopen.call_args.args[0]
            self.assertEqual(request.get_method(), "HEAD")
            response.__enter__.return_value.read.assert_not_called()

    def test_bootstrap_identity_probe_falls_back_for_old_gateway(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            token = root / "bootstrap.token"
            token.write_text("secret", encoding="ascii")
            args = Namespace(
                runtime_archive=str(root / "runtime.zip"),
                bootstrap_token_file=str(token),
                bootstrap_port=7151,
            )
            updater = managed.RuntimeUpdater(args, root / "install")
            error = managed.urllib.error.HTTPError(
                "http://127.0.0.1:7151/bootstrap",
                404,
                "not found",
                {},
                None,
            )
            with mock.patch.object(
                managed.urllib.request,
                "urlopen",
                side_effect=error,
            ):
                self.assertEqual(updater._fetch_bootstrap_identity(), "")

    def test_unchanged_bootstrap_identity_skips_full_bundle_download(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            args = Namespace(runtime_archive=str(root / "missing.zip"))
            updater = managed.RuntimeUpdater(args, root / "install")
            updater.bootstrap_identity = '"bundle-stable"'
            with (
                mock.patch.object(
                    updater,
                    "_fetch_bootstrap_identity",
                    return_value='"bundle-stable"',
                ),
                mock.patch.object(updater, "_fetch_bootstrap") as full_fetch,
            ):
                updater._stage_once()

            full_fetch.assert_not_called()

    def test_runtime_stage_updates_exact_copied_launcher_path(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            runtime_archive = root / "runtime.zip"
            runtime_archive.write_bytes(b"same-runtime")
            bridge = root / "bridge"
            bridge.write_bytes(b"same-bridge")
            launcher = root / "Copied Worker.cmd"
            launcher.write_bytes(b"old launcher")
            args = Namespace(
                runtime_archive=str(runtime_archive),
                launcher_path=str(launcher),
                no_autostart=True,
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
                        b"new launcher",
                        '"bundle-launcher"',
                    ),
                ),
                mock.patch.object(
                    updater,
                    "_fetch_bootstrap_identity",
                    return_value='"bundle-launcher"',
                ),
                mock.patch.object(managed, "_runtime_version_from_zip", return_value=""),
                mock.patch.object(
                    managed,
                    "_python_remote_dependencies_ok",
                    return_value=True,
                ),
            ):
                updater._stage_once()

            self.assertEqual(launcher.read_bytes(), b"new launcher")
            self.assertEqual(
                updater._managed_launcher_path().read_bytes(),
                b"new launcher",
            )
            self.assertEqual(updater.bootstrap_identity, '"bundle-launcher"')

    def test_legacy_windows_launcher_path_is_discovered_from_bees_self(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            args = Namespace(runtime_archive=str(root / "missing.zip"), launcher_path="")
            updater = managed.RuntimeUpdater(args, root / "install")
            legacy = root / "Seagrams Crown" / "bees-remote-worker.cmd"
            with mock.patch.dict(
                managed.os.environ,
                {"BEES_SELF": str(legacy)},
                clear=True,
            ):
                self.assertEqual(updater._external_launcher_path(), legacy.resolve())

    def test_external_launcher_origin_survives_managed_launcher_takeover(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            install = root / "install"
            external = root / "Seagrams Crown" / "bees-remote-worker.cmd"
            external.parent.mkdir(parents=True)
            external.write_text("@echo off\n", encoding="utf-8")

            first = managed.RuntimeUpdater(
                Namespace(runtime_archive=str(root / "missing.zip"), launcher_path=""),
                install,
            )
            with mock.patch.dict(
                managed.os.environ,
                {"BEES_SELF": str(external)},
                clear=True,
            ):
                self.assertEqual(first._external_launcher_path(), external.resolve())

            managed_launcher = first._managed_launcher_path()
            managed_launcher.parent.mkdir(parents=True, exist_ok=True)
            managed_launcher.write_text("@echo off\n", encoding="utf-8")
            restarted = managed.RuntimeUpdater(
                Namespace(
                    runtime_archive=str(root / "missing.zip"),
                    launcher_path=str(managed_launcher),
                ),
                install,
            )
            with mock.patch.dict(managed.os.environ, {}, clear=True):
                self.assertEqual(
                    restarted._external_launcher_path(),
                    external.resolve(),
                )

    def test_no_autostart_prevents_managed_launcher_registration(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            args = Namespace(
                runtime_archive=str(root / "missing.zip"),
                no_autostart=True,
                torch_device="cpu",
                auto_envs=True,
                envs=4,
            )
            updater = managed.RuntimeUpdater(args, root / "install")
            with mock.patch.object(managed.subprocess, "run") as run:
                updater._adopt_managed_launcher(root / "bees-remote-worker.cmd")
            run.assert_not_called()

    def test_managed_launcher_adoption_clears_watchdog_child_suppression(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            args = Namespace(
                runtime_archive=str(root / "missing.zip"),
                no_autostart=False,
                torch_device="cpu",
                auto_envs=True,
                envs=4,
            )
            updater = managed.RuntimeUpdater(args, root / "install")
            completed = mock.Mock(returncode=0)
            with (
                mock.patch.dict(
                    managed.os.environ,
                    {"BEES_AUTOSTART_CHILD": "1", "PATH": "test-path"},
                    clear=True,
                ),
                mock.patch.object(
                    managed.subprocess,
                    "run",
                    return_value=completed,
                ) as run,
            ):
                updater._adopt_managed_launcher(root / "bees-remote-worker.cmd")

            environment = run.call_args.kwargs["env"]
            self.assertNotIn("BEES_AUTOSTART_CHILD", environment)
            self.assertEqual(environment["PATH"], "test-path")

    def test_runtime_refresh_requests_cannot_bypass_poll_interval(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            args = Namespace(
                runtime_archive=str(root / "missing.zip"),
                runtime_poll_seconds=20.0,
            )
            updater = managed.RuntimeUpdater(args, root / "install")
            updater._last_attempt_monotonic = 100.0
            with mock.patch.object(managed.time, "monotonic", return_value=105.0):
                updater.request_refresh()
            self.assertFalse(updater._refresh.is_set())

            with mock.patch.object(managed.time, "monotonic", return_value=121.0):
                updater.request_refresh()
            self.assertTrue(updater._refresh.is_set())

    def test_control_failure_total_reads_inner_worker_control_metrics(self):
        record = {
            "metrics": {
                "control": {
                    "failures_total": 7,
                    "seconds_since_last_failure": 0.5,
                    "last_failure_type": "ControlUnavailable",
                }
            }
        }
        self.assertEqual(managed._control_failure_total(record), 7)
        self.assertIsNone(managed._control_failure_total({"metrics": {}}))
        self.assertIsNone(
            managed._control_failure_total(
                {"metrics": {"control": {"failures_total": True}}}
            )
        )

    def test_stopped_training_worker_with_control_unavailable_is_stalled(self):
        status = {
            "desired": {
                "training_enabled": True,
                "pending_release": None,
            }
        }
        record = {
            "process_state": "stopped",
            "last_error": "ControlUnavailable: POST /v1/heartbeat: timed out",
            "metrics": {
                "control": {
                    "failures_total": 4,
                    "seconds_since_last_failure": 0.2,
                    "last_failure_type": "ControlUnavailable",
                }
            },
        }

        self.assertTrue(managed._inner_control_stalled(status, record))
        watchdog = managed._TransportWatchdog(10.0)
        self.assertFalse(watchdog.observe(False, 100.0))
        self.assertFalse(watchdog.observe(False, 109.9))
        self.assertTrue(watchdog.observe(False, 110.0))

    def test_control_stall_detector_ignores_running_or_release_stop(self):
        base_record = {
            "process_state": "running",
            "last_error": "ControlUnavailable: POST /v1/heartbeat: timed out",
            "metrics": {
                "control": {
                    "failures_total": 1,
                    "seconds_since_last_failure": 0.2,
                    "last_failure_type": "ControlUnavailable",
                }
            },
        }
        status = {"desired": {"training_enabled": True, "pending_release": None}}
        self.assertFalse(managed._inner_control_stalled(status, base_record))

        stopped = dict(base_record, process_state="stopped")
        rollout = {"desired": {"training_enabled": True, "pending_release": {"phase": "stopping"}}}
        self.assertFalse(managed._inner_control_stalled(rollout, stopped))

    def test_repeated_inner_control_failures_trip_transport_recycle_watchdog(self):
        watchdog = managed._SessionFailureWatchdog(
            threshold=3,
            window_seconds=60.0,
        )
        self.assertFalse(watchdog.observe(40, 100.0))
        self.assertFalse(watchdog.observe(41, 110.0))
        self.assertFalse(watchdog.observe(42, 120.0))
        self.assertTrue(watchdog.observe(43, 130.0))

        # A worker-agent restart resets its cumulative counter; the watchdog must adopt
        # the new baseline rather than treating the reset as another failure.
        self.assertFalse(watchdog.observe(0, 140.0))
        self.assertFalse(watchdog.observe(1, 150.0))

    def test_control_failure_watchdog_resets_across_global_control_outage(self):
        watchdog = managed._SessionFailureWatchdog(
            threshold=3,
            window_seconds=60.0,
        )
        self.assertFalse(watchdog.observe(10, 100.0))
        self.assertFalse(watchdog.observe(11, 110.0))
        watchdog.reset()
        self.assertFalse(watchdog.observe(15, 200.0))
        self.assertFalse(watchdog.observe(16, 210.0))

    def test_session_failure_watchdog_escalates_repeated_failures_and_expires_window(self):
        watchdog = managed._SessionFailureWatchdog(threshold=3, window_seconds=120.0)

        self.assertFalse(watchdog.observe(10, 100.0))
        self.assertFalse(watchdog.observe(11, 110.0))
        self.assertFalse(watchdog.observe(12, 150.0))
        self.assertTrue(watchdog.observe(13, 180.0))

        expired = managed._SessionFailureWatchdog(threshold=3, window_seconds=20.0)
        self.assertFalse(expired.observe(20, 100.0))
        self.assertFalse(expired.observe(21, 101.0))
        self.assertFalse(expired.observe(22, 130.0))
        self.assertFalse(expired.observe(23, 131.0))

    def test_broker_probe_uses_wan_token_and_only_applies_while_training(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            wan_token = root / "wan.token"
            wan_token.write_text("wan-secret", encoding="ascii")
            args = Namespace(wan_token_file=str(wan_token), broker_port=55051)
            response = mock.MagicMock()
            response.status = 200
            response.__enter__.return_value = response
            response.__exit__.return_value = False

            with mock.patch.object(
                managed.urllib.request,
                "urlopen",
                return_value=response,
            ) as urlopen:
                self.assertTrue(managed._broker_session_available(args))

            request = urlopen.call_args.args[0]
            self.assertEqual(request.full_url, "http://127.0.0.1:55051/session")
            self.assertEqual(
                request.headers.get("Authorization"),
                "Bearer wan-secret",
            )
            self.assertTrue(
                managed._training_desired(
                    {"desired": {"training_enabled": True}}
                )
            )
            self.assertFalse(
                managed._training_desired(
                    {"desired": {"training_enabled": False}}
                )
            )

    def test_transport_watchdog_requires_sustained_failure_and_resets_on_success(self):
        watchdog = managed._TransportWatchdog(30.0)

        self.assertFalse(watchdog.observe(False, 100.0))
        self.assertFalse(watchdog.observe(False, 129.9))
        self.assertTrue(watchdog.observe(False, 130.0))
        self.assertFalse(watchdog.observe(True, 131.0))
        self.assertIsNone(watchdog.failure_since)
        self.assertFalse(watchdog.observe(False, 200.0))
        self.assertFalse(watchdog.observe(False, 229.9))

    def test_incomplete_bootstrap_read_is_retryable_transport_failure(self):
        failure = http.client.IncompleteRead(b"partial", 128)
        self.assertTrue(managed._is_transient_transport_error(failure))

    def test_runtime_updater_keeps_http_failures_in_worker_state_instead_of_dying(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            args = Namespace(
                runtime_archive=str(root / "missing.zip"),
                runtime_poll_seconds=0.01,
            )
            updater = managed.RuntimeUpdater(args, root / "install")
            updater._stage_once = mock.Mock(
                side_effect=http.client.IncompleteRead(b"partial", 128)
            )
            updater.start()
            for _ in range(100):
                _build, error = updater.verified()
                if "IncompleteRead" in error:
                    break
                managed.time.sleep(0.01)
            updater.stop()

            self.assertIn("IncompleteRead", updater.last_error)
            self.assertGreaterEqual(updater._stage_once.call_count, 1)

    def test_runtime_updater_revives_dead_thread_without_stopping_supervisor(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            args = Namespace(
                runtime_archive=str(root / "missing.zip"),
                runtime_poll_seconds=60.0,
            )
            updater = managed.RuntimeUpdater(args, root / "install")
            updater._started = True
            updater._thread = mock.Mock()
            updater._thread.is_alive.return_value = False
            replacement = mock.Mock()
            replacement.is_alive.return_value = True
            with mock.patch.object(
                managed.threading,
                "Thread",
                return_value=replacement,
            ):
                self.assertTrue(updater.revive())

            replacement.start.assert_called_once_with()
            self.assertIs(updater._thread, replacement)

    def test_unsafe_supervisor_replacement_closes_windows_owned_job_before_spawn(self):
        replacement = mock.Mock()
        replacement.pid = 9911
        with (
            mock.patch.object(managed.os, "name", "nt"),
            mock.patch.object(managed, "close_windows_owned_child_job") as close_job,
            mock.patch.object(managed.subprocess, "Popen", return_value=replacement) as popen,
        ):
            result = managed._spawn_clean_supervisor_replacement()

        self.assertIs(result, replacement)
        close_job.assert_called_once_with()
        self.assertEqual(popen.call_args.args[0][0], managed.sys.executable)
        self.assertEqual(
            popen.call_args.kwargs["creationflags"],
            getattr(managed.subprocess, "CREATE_NO_WINDOW", 0),
        )
        self.assertNotIn("start_new_session", popen.call_args.kwargs)

    def test_runtime_updater_stuck_stop_requires_clean_process_replacement(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            args = Namespace(runtime_archive=str(root / "missing.zip"))
            updater = managed.RuntimeUpdater(args, root / "install")
            updater._started = True
            updater._thread = mock.Mock()
            updater._thread.is_alive.return_value = True

            with self.assertRaises(managed._SupervisorProcessRestartRequired):
                updater.stop()

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
                        b"",
                        '"bundle-bad"',
                    ),
                ),
                mock.patch.object(
                    updater,
                    "_fetch_bootstrap_identity",
                    return_value='"bundle-bad"',
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
                        b"",
                        '"bundle-repair"',
                    ),
                ),
                mock.patch.object(
                    updater,
                    "_fetch_bootstrap_identity",
                    return_value='"bundle-repair"',
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

    def test_private_transport_readiness_does_not_require_central_control(self):
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
            mock.patch.object(managed, "_wait_for_ports", return_value=True) as ports,
            mock.patch.object(managed, "_control_status") as status,
        ):
            self.assertTrue(
                managed._wait_for_private_transport(
                    args,
                    process,
                    stop,
                    timeout=5.0,
                )
            )

        ports.assert_called_once_with(
            (7150, 55051, 7151, 7146),
            process,
            stop,
            timeout=5.0,
        )
        status.assert_not_called()

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
