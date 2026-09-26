from __future__ import annotations

import json
import os
import signal
import tempfile
import unittest
from unittest import mock
import zipfile
from pathlib import Path

import bees_training_control as control
import bees_training_worker_agent as agent
import bees_remote_worker as remote_worker
from bees_package_training_build import package_build


class FakeClient:
    def __init__(self, archive: Path):
        self.archive = archive

    def download_artifact(self, _url: str, destination: Path) -> None:
        destination.write_bytes(self.archive.read_bytes())


class TrainingControlClientTests(unittest.TestCase):
    def _runtime_pointer_fixture(self, root: Path, build_id: str, version: str):
        runtime_root = root / f"runtime-{build_id}"
        runtime_root.mkdir(parents=True)
        (runtime_root / "bees-runtime-version.txt").write_text(
            version + "\n",
            encoding="ascii",
        )
        service = runtime_root / "bees_continual_elastic_wan_service.py"
        service.write_text("# pinned service\n", encoding="utf-8")
        python_executable = root / f"python-{build_id}"
        python_executable.write_bytes(b"python")
        command = [
            str(python_executable),
            str(service),
            "--training-env={env}",
            "--run-id={run_id}",
        ]
        return runtime_root, python_executable, command

    def test_central_runtime_pointer_requires_verified_runtime_and_service(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            version = "a" * 64
            runtime_root, python_executable, command = self._runtime_pointer_fixture(
                root,
                "build-a",
                version,
            )
            pointer = root / "release-runtime.json"
            pointer.write_text(
                json.dumps(
                    {
                        "schema_version": 1,
                        "build_id": "build-a",
                        "runtime_version": version,
                        "runtime_root": str(runtime_root),
                        "python_executable": str(python_executable),
                        "launch_command": command,
                    }
                ),
                encoding="utf-8",
            )

            loaded = agent._load_runtime_cutover_pointer(str(pointer))
            self.assertEqual(loaded["build_id"], "build-a")
            self.assertEqual(loaded["runtime_version"], version)
            self.assertEqual(loaded["launch_command"], command)

            (runtime_root / "bees-runtime-version.txt").write_text(
                "b" * 64 + "\n",
                encoding="ascii",
            )
            with self.assertRaisesRegex(ValueError, "version marker"):
                agent._load_runtime_cutover_pointer(str(pointer))

    def test_pending_runtime_pointer_does_not_replace_current_cached_runtime(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            pointer = root / "release-runtime.json"
            cache = {}

            old_root, old_python, old_command = self._runtime_pointer_fixture(
                root,
                "build-old",
                "a" * 64,
            )
            pointer.write_text(
                json.dumps(
                    {
                        "schema_version": 1,
                        "build_id": "build-old",
                        "runtime_version": "a" * 64,
                        "runtime_root": str(old_root),
                        "python_executable": str(old_python),
                        "launch_command": old_command,
                    }
                ),
                encoding="utf-8",
            )
            self.assertEqual(
                agent._runtime_launch_template(
                    str(pointer),
                    "build-old",
                    ["fallback", "service", "--training-env={env}"],
                    cache,
                ),
                old_command,
            )

            new_root, new_python, new_command = self._runtime_pointer_fixture(
                root,
                "build-new",
                "b" * 64,
            )
            pointer.write_text(
                json.dumps(
                    {
                        "schema_version": 1,
                        "build_id": "build-new",
                        "runtime_version": "b" * 64,
                        "runtime_root": str(new_root),
                        "python_executable": str(new_python),
                        "launch_command": new_command,
                    }
                ),
                encoding="utf-8",
            )

            self.assertEqual(
                agent._runtime_launch_template(
                    str(pointer),
                    "build-old",
                    ["wrong", "service", "--training-env={env}"],
                    cache,
                ),
                old_command,
            )
            self.assertEqual(
                agent._runtime_launch_template(
                    str(pointer),
                    "build-new",
                    ["wrong", "service", "--training-env={env}"],
                    cache,
                ),
                new_command,
            )

    def test_runtime_state_records_actual_active_child_runtime(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            runtime_root, python_executable, command = self._runtime_pointer_fixture(
                root,
                "build-a",
                "c" * 64,
            )
            state = root / "active-runtime.json"

            agent._write_runtime_state(
                str(state),
                build_id="build-a",
                command=command,
            )

            value = json.loads(state.read_text(encoding="utf-8"))
            self.assertEqual(value["build_id"], "build-a")
            self.assertEqual(value["runtime_version"], "c" * 64)
            self.assertEqual(Path(value["runtime_root"]), runtime_root.resolve())
            self.assertEqual(
                Path(value["python_executable"]),
                python_executable.resolve(),
            )

    def test_heartbeat_reports_release_preparation_error_separately(self):
        heartbeat = control.default_heartbeat(
            trainer_id="remote-a",
            role="dedicated",
            platform="LinuxPlayer",
            process_state="running",
            applied_revision=7,
            build={"build_id": "old", "archive_sha256": "a" * 64},
            prepared_build_id="",
            preparation_error="runtime download failed",
            last_error="runtime download failed",
        )

        self.assertEqual(
            heartbeat["preparation_error"],
            "runtime download failed",
        )
        self.assertEqual(heartbeat["last_error"], "runtime download failed")
        self.assertEqual(heartbeat["prepared_build_id"], "")

    def test_worker_control_requests_fail_fast_inside_server_lease(self):
        self.assertEqual(
            agent._parser().get_default("request_timeout_seconds"),
            5.0,
        )

    def test_managed_process_uses_separate_posix_process_group_and_stops_tree(self):
        fake = mock.Mock()
        fake.pid = 4242
        fake.poll.return_value = None
        fake.wait.return_value = 0

        with (
            mock.patch.object(agent.os, "name", "posix"),
            mock.patch.object(agent.subprocess, "Popen", return_value=fake) as popen,
            mock.patch.object(agent.os, "killpg") as killpg,
        ):
            managed = agent.ManagedProcess()
            managed.start(
                ["python", "worker.py"],
                revision=1,
                build_sha256="a" * 64,
                build_id="build-a",
                run_id="run-a",
                compatibility_key="b" * 64,
                state_file=Path("state.json"),
                environment_args=(),
            )
            self.assertTrue(popen.call_args.kwargs["start_new_session"])
            environment = popen.call_args.kwargs["env"]
            self.assertEqual(environment["BEES_TRAINING_RUN_ID"], "run-a")
            self.assertEqual(environment[agent.BUILD_ID_ENV], "build-a")
            self.assertEqual(environment[agent.COMPATIBILITY_KEY_ENV], "b" * 64)
            self.assertEqual(environment["PYTHONUNBUFFERED"], "1")
            self.assertTrue(
                Path(environment[agent.THROUGHPUT_METRICS_ENV]).as_posix().endswith(
                    "worker-throughput.json"
                )
            )
            self.assertTrue(
                Path(environment["BEES_TRAINING_LOG_DIR"]).as_posix().endswith(
                    "logs/run-a"
                )
            )
            self.assertTrue(
                Path(
                    environment["BEES_TRAINING_MODEL_SNAPSHOT_REQUEST_FILE"]
                ).as_posix().endswith("model-snapshot.request")
            )
            self.assertTrue(
                Path(
                    environment["BEES_TRAINING_MODEL_SNAPSHOT_RESPONSE_FILE"]
                ).as_posix().endswith("model-snapshot.response.json")
            )
            managed.stop()

        killpg.assert_called_once_with(4242, signal.SIGTERM)

    def test_managed_process_retains_ownership_when_forced_stop_cannot_confirm_exit(self):
        fake = mock.Mock()
        fake.pid = 4342
        fake.poll.return_value = None
        fake.wait.side_effect = TimeoutError("still running")

        with (
            mock.patch.object(agent.os, "name", "posix"),
            mock.patch.object(agent.os, "killpg") as killpg,
        ):
            managed = agent.ManagedProcess()
            managed.process = fake

            with self.assertRaisesRegex(RuntimeError, "did not stop"):
                managed.stop()

        self.assertIs(managed.process, fake)
        self.assertEqual(
            killpg.call_args_list,
            [
                mock.call(4342, signal.SIGTERM),
                mock.call(4342, signal.SIGKILL),
            ],
        )
        fake.kill.assert_called_once()

    def test_central_managed_process_requests_checkpoint_finalization_before_force_kill(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            fake = mock.Mock()
            fake.pid = 4343
            fake.poll.side_effect = [None, None, 0, 0]
            fake.wait.return_value = 0

            with (
                mock.patch.object(agent.os, "name", "posix"),
                mock.patch.object(agent.subprocess, "Popen", return_value=fake) as popen,
                mock.patch.object(agent.os, "killpg") as killpg,
                mock.patch.object(agent.time, "sleep"),
            ):
                managed = agent.ManagedProcess()
                managed.start(
                    ["python", "service.py"],
                    revision=1,
                    build_sha256="a" * 64,
                    build_id="build-a",
                    run_id="run-a",
                    compatibility_key="b" * 64,
                    state_file=root / "control-state.json",
                    environment_args=(),
                    graceful_checkpoint=True,
                )
                environment = popen.call_args.kwargs["env"]
                stop_file = Path(environment[agent.MANAGED_STOP_FILE_ENV])
                self.assertFalse(stop_file.exists())

                progress = mock.Mock()
                managed.stop(progress_callback=progress)

            self.assertFalse(stop_file.exists())
            progress.assert_called()
            killpg.assert_not_called()

    def test_remote_managed_process_requests_graceful_stop_before_force_kill(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            fake = mock.Mock()
            fake.pid = 4344
            fake.poll.side_effect = [None, None, 0, 0]
            fake.wait.return_value = 0

            with (
                mock.patch.object(agent.os, "name", "posix"),
                mock.patch.object(agent.subprocess, "Popen", return_value=fake) as popen,
                mock.patch.object(agent.os, "killpg") as killpg,
                mock.patch.object(agent.time, "sleep"),
            ):
                managed = agent.ManagedProcess()
                managed.start(
                    ["python", "actor.py"],
                    revision=1,
                    build_sha256="a" * 64,
                    build_id="build-a",
                    run_id="run-a",
                    compatibility_key="b" * 64,
                    state_file=root / "control-state.json",
                    environment_args=(),
                    graceful_remote_stop=True,
                )
                environment = popen.call_args.kwargs["env"]
                stop_file = Path(environment[agent.MANAGED_STOP_FILE_ENV])
                self.assertFalse(stop_file.exists())

                progress = mock.Mock()
                managed.stop(progress_callback=progress)

            self.assertFalse(stop_file.exists())
            progress.assert_called()
            killpg.assert_not_called()

    def test_central_checkpoint_timeout_refuses_force_kill(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            fake = mock.Mock()
            fake.pid = 4444
            fake.poll.return_value = None

            with (
                mock.patch.object(agent.os, "name", "posix"),
                mock.patch.object(agent.subprocess, "Popen", return_value=fake),
                mock.patch.object(agent.os, "killpg") as killpg,
                mock.patch.object(agent, "GRACEFUL_CHECKPOINT_STOP_SECONDS", 0.0),
            ):
                managed = agent.ManagedProcess()
                managed.start(
                    ["python", "service.py"],
                    revision=1,
                    build_sha256="a" * 64,
                    build_id="build-a",
                    run_id="run-a",
                    compatibility_key="b" * 64,
                    state_file=root / "control-state.json",
                    environment_args=(),
                    graceful_checkpoint=True,
                )

                with self.assertRaisesRegex(RuntimeError, "refusing forced termination"):
                    managed.stop()

            self.assertIs(managed.process, fake)
            killpg.assert_not_called()

    def test_managed_process_stops_windows_process_tree(self):
        fake = mock.Mock()
        fake.pid = 5252
        fake.poll.return_value = None
        fake.wait.return_value = 0

        with (
            mock.patch.object(agent.os, "name", "nt"),
            mock.patch.object(agent.subprocess, "run") as run,
        ):
            managed = agent.ManagedProcess()
            managed.process = fake
            managed.stop()

        run.assert_called_once()
        self.assertEqual(
            run.call_args.args[0],
            ["taskkill", "/PID", "5252", "/T", "/F"],
        )

    def test_training_log_uploader_flushes_only_selected_run(self):
        class UploadClient:
            def __init__(self):
                self.files = {}

            def upload_log_chunk(
                self,
                *,
                trainer_id,
                run_id,
                relative_path,
                offset,
                data,
                reset=False,
            ):
                key = (trainer_id, run_id, relative_path)
                current = self.files.get(key, b"")
                if reset:
                    current = b""
                if len(current) != offset:
                    return -len(current) - 1
                current += data
                self.files[key] = current
                return len(current)

        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            (root / "run-old").mkdir()
            (root / "run-new").mkdir()
            (root / "run-old" / "Player-0.log").write_bytes(b"old-data")
            (root / "run-new" / "Player-0.log").write_bytes(b"new-data")
            uploader = agent.TrainingLogUploader(root)
            client = UploadClient()

            uploader.flush_all(client, trainer_id="worker-a", run_id="run-old")

            self.assertEqual(
                client.files[("worker-a", "run-old", "Player-0.log")],
                b"old-data",
            )
            self.assertNotIn(("worker-a", "run-new", "Player-0.log"), client.files)

    def test_episode_metrics_reset_when_run_changes(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            old = root / "run-old"
            new = root / "run-new"
            old.mkdir()
            new.mkdir()
            old_line = (
                "RL 1v1 episode=1 timeout=False duration=10.0s "
                "bee_tsv=100->50 human_tsv=100->0 "
                "bee_fire_requests=1 bee_shots=1 bee_hits=1 bee_damage=10 "
                "human_fire_requests=1 human_shots=1 human_hits=0 human_damage=0\n"
            )
            new_line = (
                "RL 1v1 episode=2 timeout=True duration=20.0s "
                "bee_tsv=100->50 human_tsv=100->50 "
                "bee_fire_requests=1 bee_shots=1 bee_hits=0 bee_damage=0 "
                "human_fire_requests=1 human_shots=1 human_hits=0 human_damage=0\n"
            )
            (old / "Player-0.log").write_text(old_line, encoding="utf-8")
            (new / "Player-0.log").write_text(new_line, encoding="utf-8")
            metrics = agent.EpisodeLogMetrics(root, window=10)

            self.assertEqual(metrics.refresh("run-old")["last_episode"], 1)
            current = metrics.refresh("run-new")
            self.assertEqual(current["last_episode"], 2)
            self.assertEqual(current["window_episodes"], 1)

    def test_full_game_canonical_change_is_deferred_while_process_is_alive(self):
        managed = agent.ManagedProcess()
        fake = mock.Mock()
        fake.poll.return_value = None
        managed.process = fake
        managed.build_sha256 = "a" * 64
        managed.environment_args = ("--rl-map-size", "64")

        self.assertTrue(
            agent.full_game_update_requires_deferred_restart(
                managed,
                "b" * 64,
                ("--rl-map-size", "64"),
            )
        )
        self.assertTrue(
            agent.full_game_update_requires_deferred_restart(
                managed,
                "a" * 64,
                ("--rl-map-size", "128"),
            )
        )
        self.assertFalse(
            agent.full_game_update_requires_deferred_restart(
                managed,
                "a" * 64,
                ("--rl-map-size", "64"),
            )
        )
        fake.poll.return_value = 0
        self.assertFalse(
            agent.full_game_update_requires_deferred_restart(
                managed,
                "b" * 64,
                ("--rl-map-size", "128"),
            )
        )

    def test_render_command_expands_build_and_environment_arguments(self):
        rendered = agent.render_command(
            [
                "python",
                "worker.py",
                "--env",
                "{env}",
                "--build-id={build_id}",
                "--run-id={run_id}",
                "--env-args",
                "{env_args}",
            ],
            Path("/tmp/Bees.x86_64"),
            ["--rl-map-size", "64"],
            "release-42",
            "run-42",
        )
        self.assertEqual(
            rendered,
            [
                "python",
                "worker.py",
                "--env",
                "/tmp/Bees.x86_64",
                "--build-id=release-42",
                "--run-id=run-42",
                "--env-args",
                "--rl-map-size",
                "64",
            ],
        )

    def test_render_command_expands_server_tuned_worker_env_count(self):
        rendered = agent.render_command(
            [
                "python",
                "worker.py",
                "--env",
                "{env}",
                "--envs",
                "{worker_envs}",
            ],
            Path("/tmp/Bees.x86_64"),
            (),
            worker_env_count=17,
        )
        self.assertEqual(rendered[-2:], ["--envs", "17"])

    def test_throughput_metrics_require_current_process_and_env_count(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "worker-throughput.json"
            path.write_text(
                json.dumps(
                    {
                        "pid": 123,
                        "env_count": 17,
                        "accepted_steps_total": 4567,
                        "accepted_trajectories_total": 89,
                        "learner_consumed_steps_total": 4321,
                        "upload_queue_depth": 2,
                        "network_sent_bytes_total": 3 * 1024 * 1024,
                        "network_received_bytes_total": 5 * 1024 * 1024,
                        "network_mib_per_s": 1.75,
                        "session_failures_total": 3,
                        "seconds_since_last_session_failure": 7.5,
                        "last_session_failure_type": "IndexError",
                    }
                ),
                encoding="utf-8",
            )
            metrics = agent.read_throughput_metrics(
                path,
                expected_pid=123,
                expected_env_count=17,
            )
            self.assertEqual(metrics["accepted_steps_total"], 4567)
            self.assertEqual(metrics["learner_consumed_steps_total"], 4321)
            self.assertEqual(metrics["network_sent_bytes_total"], 3 * 1024 * 1024)
            self.assertEqual(metrics["network_received_bytes_total"], 5 * 1024 * 1024)
            self.assertEqual(metrics["network_mib_per_s"], 1.75)
            self.assertEqual(metrics["session_failures_total"], 3)
            self.assertEqual(metrics["seconds_since_last_session_failure"], 7.5)
            self.assertEqual(metrics["last_session_failure_type"], "IndexError")
            self.assertEqual(
                agent.read_throughput_metrics(path, expected_pid=999),
                {},
            )
            self.assertEqual(
                agent.read_throughput_metrics(path, expected_env_count=18),
                {},
            )

            invalid = json.loads(path.read_text(encoding="utf-8"))
            invalid["network_mib_per_s"] = -1
            path.write_text(json.dumps(invalid), encoding="utf-8")
            self.assertEqual(
                agent.read_throughput_metrics(
                    path,
                    expected_pid=123,
                    expected_env_count=17,
                ),
                {},
            )

    def test_training_control_protocol_schema_matches_server_generation(self):
        self.assertEqual(control.CONTROL_SCHEMA_VERSION, 5)

    def test_episode_log_metrics_reports_recent_training_statistics(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            log = root / "Player-0.log"
            log.write_text(
                "RL 1v1 episode=1 timeout=False duration=10.0s "
                "bee_tsv=100->50 human_tsv=100->0 "
                "bee_fire_requests=4 bee_shots=3 bee_hits=2 bee_damage=10 "
                "human_fire_requests=2 human_shots=2 human_hits=1 human_damage=5 "
                "bee_aim_samples=2 bee_aim_error=10.00deg bee_aim_within_5deg=50.00% "
                "bee_turret_aligned=100.00% "
                "human_aim_samples=1 human_aim_error=20.00deg human_aim_within_5deg=0.00% "
                "human_turret_aligned=100.00%\n"
                "RL 1v1 episode=2 timeout=True duration=20.0s "
                "bee_tsv=100->25 human_tsv=100->25 "
                "bee_fire_requests=4 bee_shots=4 bee_hits=1 bee_damage=5 "
                "human_fire_requests=4 human_shots=4 human_hits=2 human_damage=10 "
                "bee_aim_samples=1 bee_aim_error=40.00deg bee_aim_within_5deg=0.00% "
                "bee_turret_aligned=0.00% "
                "human_aim_samples=3 human_aim_error=50.00deg human_aim_within_5deg=33.33% "
                "human_turret_aligned=66.67%\n",
                encoding="utf-8",
            )
            metrics = agent.EpisodeLogMetrics(root, window=10).refresh()
            self.assertEqual(metrics["window_episodes"], 2)
            self.assertEqual(metrics["last_episode"], 2)
            self.assertEqual(metrics["timeout_pct"], 50.0)
            self.assertEqual(metrics["bee_win_pct"], 50.0)
            self.assertAlmostEqual(metrics["bee_hit_pct"], 100.0 * 3 / 7, places=2)
            self.assertAlmostEqual(metrics["human_hit_pct"], 50.0, places=2)
            self.assertAlmostEqual(metrics["bee_hits_per_shot"], 3 / 7, places=3)
            self.assertAlmostEqual(metrics["human_hits_per_shot"], 0.5, places=3)
            self.assertEqual(metrics["bee_aim_samples"], 3)
            self.assertEqual(metrics["human_aim_samples"], 4)
            self.assertAlmostEqual(metrics["bee_aim_error_deg"], 20.0, places=2)
            self.assertAlmostEqual(metrics["human_aim_error_deg"], 42.5, places=2)
            self.assertAlmostEqual(
                metrics["bee_aim_within_5_pct"],
                100.0 / 3.0,
                places=2,
            )
            self.assertAlmostEqual(
                metrics["human_aim_within_5_pct"],
                24.9975,
                places=2,
            )
            self.assertAlmostEqual(
                metrics["bee_turret_aligned_pct"],
                200.0 / 3.0,
                places=2,
            )
            self.assertAlmostEqual(
                metrics["human_turret_aligned_pct"],
                75.0025,
                places=2,
            )

    def test_managed_build_is_hash_verified_and_installed_versioned(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            archive = root / "build.zip"
            with zipfile.ZipFile(archive, "w") as bundle:
                bundle.writestr("Bees.x86_64", b"binary")
                bundle.writestr("Bees_Data/data.bin", b"data")

            descriptor = {
                "role": "dedicated",
                "platform": "LinuxPlayer",
                "build_id": "build-123",
                "archive_sha256": control.file_sha256(archive),
                "archive_size_bytes": archive.stat().st_size,
                "entrypoint": "Bees.x86_64",
                "artifact_url": "/v1/artifact/LinuxPlayer",
            }
            store = control.ManagedBuildStore(root / "managed")
            entrypoint, installed = store.ensure(FakeClient(archive), descriptor)

            self.assertTrue(entrypoint.is_file())
            self.assertEqual(entrypoint.read_bytes(), b"binary")
            self.assertEqual(installed["archive_sha256"], descriptor["archive_sha256"])
            current = json.loads((root / "managed" / "current.json").read_text(encoding="utf-8"))
            self.assertEqual(current["build_id"], "build-123")

    def test_managed_build_prepare_does_not_activate_until_ensure(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            archive = root / "build.zip"
            with zipfile.ZipFile(archive, "w") as bundle:
                bundle.writestr("Bees.x86_64", b"binary")
            descriptor = {
                "role": "dedicated",
                "platform": "LinuxPlayer",
                "build_id": "build-prepared",
                "archive_sha256": control.file_sha256(archive),
                "archive_size_bytes": archive.stat().st_size,
                "entrypoint": "Bees.x86_64",
                "artifact_url": "/v1/artifact/dedicated/LinuxPlayer/build-prepared",
            }
            store = control.ManagedBuildStore(root / "managed")
            prepared, _ = store.prepare(FakeClient(archive), descriptor)
            self.assertTrue(prepared.is_file())
            self.assertTrue(store.is_prepared(descriptor))
            self.assertFalse((root / "managed" / "current.json").exists())

            activated, _ = store.ensure(FakeClient(archive), descriptor)
            self.assertEqual(activated, prepared)
            current = json.loads(
                (root / "managed" / "current.json").read_text(encoding="utf-8")
            )
            self.assertEqual(current["build_id"], "build-prepared")

    def test_managed_build_rejects_archive_hash_mismatch(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            archive = root / "build.zip"
            with zipfile.ZipFile(archive, "w") as bundle:
                bundle.writestr("Bees.exe", b"binary")

            descriptor = {
                "role": "dedicated",
                "platform": "WindowsPlayer",
                "build_id": "build-1",
                "archive_sha256": "0" * 64,
                "archive_size_bytes": archive.stat().st_size,
                "entrypoint": "Bees.exe",
                "artifact_url": "/v1/artifact/WindowsPlayer",
            }
            store = control.ManagedBuildStore(root / "managed")
            with self.assertRaisesRegex(ValueError, "SHA-256"):
                store.ensure(FakeClient(archive), descriptor)

    def test_managed_build_rejects_path_traversal(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            archive = root / "build.zip"
            with zipfile.ZipFile(archive, "w") as bundle:
                bundle.writestr("../escape.txt", b"bad")
                bundle.writestr("Bees.exe", b"binary")

            descriptor = {
                "role": "dedicated",
                "platform": "WindowsPlayer",
                "build_id": "build-2",
                "archive_sha256": control.file_sha256(archive),
                "archive_size_bytes": archive.stat().st_size,
                "entrypoint": "Bees.exe",
                "artifact_url": "/v1/artifact/WindowsPlayer",
            }
            store = control.ManagedBuildStore(root / "managed")
            with self.assertRaisesRegex(ValueError, "unsafe artifact member"):
                store.ensure(FakeClient(archive), descriptor)
            self.assertFalse((root / "escape.txt").exists())

    def test_managed_build_keeps_role_specific_windows_artifacts_separate(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            training_archive = root / "training.zip"
            game_archive = root / "game.zip"
            with zipfile.ZipFile(training_archive, "w") as bundle:
                bundle.writestr("Bees RL Training.exe", b"training")
            with zipfile.ZipFile(game_archive, "w") as bundle:
                bundle.writestr("Bees.exe", b"game")

            store = control.ManagedBuildStore(root / "managed")
            training = {
                "role": "dedicated",
                "platform": "WindowsPlayer",
                "build_id": "release-1",
                "archive_sha256": control.file_sha256(training_archive),
                "archive_size_bytes": training_archive.stat().st_size,
                "entrypoint": "Bees RL Training.exe",
                "artifact_url": "/v1/artifact/dedicated/WindowsPlayer/release-1",
            }
            game = {
                "role": "full-game",
                "platform": "WindowsPlayer",
                "build_id": "release-1",
                "archive_sha256": control.file_sha256(game_archive),
                "archive_size_bytes": game_archive.stat().st_size,
                "entrypoint": "Bees.exe",
                "artifact_url": "/v1/artifact/full-game/WindowsPlayer/release-1",
            }
            training_entry, _ = store.ensure(FakeClient(training_archive), training)
            game_entry, _ = store.ensure(FakeClient(game_archive), game)
            self.assertNotEqual(training_entry.parent, game_entry.parent)
            self.assertEqual(training_entry.read_bytes(), b"training")
            self.assertEqual(game_entry.read_bytes(), b"game")

    def test_dedicated_process_safety_requires_exact_desired_identity(self):
        managed = agent.ManagedProcess()
        process = mock.Mock()
        process.poll.return_value = None
        managed.process = process
        managed.build_id = "build-a"
        managed.build_sha256 = "a" * 64
        managed.run_id = "run-a"
        managed.compatibility_key = "b" * 64
        managed.environment_args = ("--rl-map-size=32",)
        managed.worker_env_count = 8
        descriptor = {
            "build_id": "build-a",
            "archive_sha256": "a" * 64,
        }

        self.assertTrue(
            agent.dedicated_process_matches_desired(
                managed,
                mode="training",
                descriptor=descriptor,
                run_id="run-a",
                compatibility_key="b" * 64,
                environment_args=("--rl-map-size=32",),
                worker_env_count=8,
            )
        )

        mismatches = (
            {"mode": "stopped"},
            {"descriptor": {"build_id": "build-b", "archive_sha256": "a" * 64}},
            {"descriptor": {"build_id": "build-a", "archive_sha256": "c" * 64}},
            {"run_id": "run-b"},
            {"compatibility_key": "d" * 64},
            {"environment_args": ("--rl-map-size=64",)},
            {"worker_env_count": 9},
        )
        base = {
            "mode": "training",
            "descriptor": descriptor,
            "run_id": "run-a",
            "compatibility_key": "b" * 64,
            "environment_args": ("--rl-map-size=32",),
            "worker_env_count": 8,
        }
        for mismatch in mismatches:
            with self.subTest(mismatch=mismatch):
                self.assertFalse(
                    agent.dedicated_process_matches_desired(
                        managed,
                        **{**base, **mismatch},
                    )
                )

        process.poll.return_value = 0
        self.assertFalse(
            agent.dedicated_process_matches_desired(managed, **base)
        )

    def test_full_game_local_state_defaults_offline_to_inference(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "state.json"
            agent.write_local_state(path, desired=None, online=False, last_error="offline")
            value = json.loads(path.read_text(encoding="utf-8"))
            self.assertEqual(value["desired_mode"], "inference")
            self.assertFalse(value["online"])
            self.assertEqual(value["lease_seconds"], 20.0)
            self.assertGreater(value["updated_unix_seconds"], 0)


    def test_legacy_remote_worker_honors_server_owned_environment_arguments(self):
        original = os.environ.get(remote_worker.CONTROL_ENV_ARGS_VARIABLE)
        try:
            os.environ[remote_worker.CONTROL_ENV_ARGS_VARIABLE] = json.dumps(
                ["--rl-map-size", "128"]
            )
            self.assertEqual(
                remote_worker.controlled_environment_args(["--rl-map-size", "32"]),
                ("--rl-map-size", "128"),
            )
        finally:
            if original is None:
                os.environ.pop(remote_worker.CONTROL_ENV_ARGS_VARIABLE, None)
            else:
                os.environ[remote_worker.CONTROL_ENV_ARGS_VARIABLE] = original

    def test_build_packager_preserves_expected_relative_entrypoint(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            source = root / "source"
            source.mkdir()
            (source / "Bees.exe").write_bytes(b"exe")
            data = source / "Bees_Data"
            data.mkdir()
            (data / "globalgamemanagers").write_bytes(b"data")
            archive = root / "build.zip"

            package_build(source, archive, "Bees.exe")

            with zipfile.ZipFile(archive, "r") as bundle:
                self.assertEqual(
                    sorted(bundle.namelist()),
                    ["Bees.exe", "Bees_Data/globalgamemanagers"],
                )

if __name__ == "__main__":
    unittest.main()
