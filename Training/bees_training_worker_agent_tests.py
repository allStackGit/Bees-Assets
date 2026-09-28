"""Static regression coverage for the persistent training worker's log metrics."""

from __future__ import annotations

import tempfile
import threading
import unittest
from unittest import mock
from pathlib import Path

import bees_training_worker_agent as worker


class EpisodeLogMetricsTests(unittest.TestCase):
    def test_initial_bounded_scan_keeps_episode_when_tail_starts_on_line_boundary(self):
        tail_bytes = 4 * 1024 * 1024
        episode = (
            "RL 1v1 episode=1 timeout=False duration=10s "
            "bee_tsv=100->0 human_tsv=100->0 "
            "bee_fire_requests=1 bee_shots=1 bee_hits=1 bee_damage=1 "
            "human_fire_requests=0 human_shots=0 human_hits=0 human_damage=0\n"
        ).encode("ascii")
        prefix = b"x" * (tail_bytes - 1) + b"\n"
        padding = b"x" * (tail_bytes - len(episode))
        self.assertGreaterEqual(len(padding), 0)

        with tempfile.TemporaryDirectory() as directory:
            (Path(directory) / "Player-0.log").write_bytes(prefix + episode + padding)
            metrics = worker.EpisodeLogMetrics(Path(directory))

            snapshot = metrics.refresh()

        self.assertEqual(snapshot["window_episodes"], 1)
        self.assertEqual(snapshot["last_episode"], 1)


    def test_malformed_or_non_finite_episode_duration_is_ignored(self):
        valid = (
            "RL 1v1 episode=2 timeout=False duration=10s "
            "bee_tsv=100->0 human_tsv=100->0 "
            "bee_fire_requests=1 bee_shots=1 bee_hits=1 bee_damage=1 "
            "human_fire_requests=0 human_shots=0 human_hits=0 human_damage=0"
        )
        malformed = valid.replace("episode=2", "episode=1").replace(
            "duration=10s", "duration=1..2s"
        )
        non_finite = valid.replace("episode=2", "episode=3").replace(
            "duration=10s", "duration=" + ("9" * 400) + "s"
        )
        oversized_integer = valid.replace("episode=2", "episode=" + ("1" * 5000))

        with tempfile.TemporaryDirectory() as directory:
            log_path = Path(directory) / "Player-0.log"
            log_path.write_text(
                malformed + "\n" + non_finite + "\n" + oversized_integer + "\n" + valid + "\n",
                encoding="utf-8",
            )
            snapshot = worker.EpisodeLogMetrics(Path(directory)).refresh()

        self.assertEqual(snapshot["window_episodes"], 1)
        self.assertEqual(snapshot["last_episode"], 2)
        self.assertEqual(snapshot["avg_duration_s"], 10.0)

class BackgroundBuildPreparerTests(unittest.TestCase):
    def test_retry_clears_stale_prepared_marker(self):
        preparer = worker.BackgroundBuildPreparer.__new__(worker.BackgroundBuildPreparer)
        preparer.builds = mock.Mock()
        preparer.builds.is_prepared.return_value = False
        preparer.client = mock.Mock()
        preparer._lock = threading.Lock()
        preparer._thread = None
        preparer._requested_build_id = "build-a"
        preparer.prepared_build_id = "build-a"
        preparer.last_error = "previous preparation completed"

        thread = mock.Mock()
        with mock.patch.object(worker.threading, "Thread", return_value=thread):
            preparer.request({"build_id": "build-a"})

        self.assertEqual(preparer.prepared_build_id, "")
        thread.start.assert_called_once_with()

    def test_wait_for_build_heartbeats_and_can_yield_for_new_state(self):
        preparer = worker.BackgroundBuildPreparer.__new__(worker.BackgroundBuildPreparer)
        preparer._lock = threading.Lock()
        preparer._requested_build_id = "build-a"
        preparer.prepared_build_id = ""
        preparer._thread = mock.Mock()
        preparer._thread.is_alive.side_effect = [True, True]
        progress = mock.Mock(return_value=False)

        ready = preparer.wait_for_build(
            {"build_id": "build-a"},
            progress_callback=progress,
            poll_seconds=0.5,
        )

        self.assertFalse(ready)
        preparer._thread.join.assert_called_once_with(timeout=0.5)
        progress.assert_called_once_with()

    def test_wait_for_build_joins_only_matching_preparation(self):
        preparer = worker.BackgroundBuildPreparer.__new__(worker.BackgroundBuildPreparer)
        preparer._lock = threading.Lock()
        preparer._requested_build_id = "build-a"
        preparer.prepared_build_id = ""
        preparer._thread = mock.Mock()

        preparer.wait_for_build({"build_id": "build-b"})
        preparer._thread.join.assert_not_called()

        preparer.wait_for_build({"build_id": "build-a"})
        preparer._thread.join.assert_called_once_with()


class ManagedProcessHealthTests(unittest.TestCase):
    def test_explicit_child_health_error_is_not_reported_as_starting(self):
        manager = worker.ManagedProcess()
        manager.process = mock.Mock()
        manager.process.poll.return_value = None
        manager.health_required = True
        manager.health_file = Path("child-health.json")
        manager.health_token = "health-token"

        with mock.patch.object(
            worker,
            "read_managed_health",
            return_value={"state": "error", "error": "learner initialization failed"},
        ):
            self.assertEqual(
                manager.health_error(),
                "learner initialization failed",
            )
            self.assertEqual(manager.state("dedicated"), "error")


    def test_fresh_starting_health_remains_starting_after_process_grace(self):
        manager = worker.ManagedProcess()
        manager.process = mock.Mock()
        manager.process.poll.return_value = None
        manager.health_required = True
        manager.started_monotonic = 1.0
        health = {
            "state": "starting",
            "error": "",
            "updated_unix_seconds": 1005.0,
        }

        with (
            mock.patch.object(worker, "read_managed_health", return_value=health),
            mock.patch.object(worker.time, "monotonic", return_value=100.0),
            mock.patch.object(worker.time, "time", return_value=1005.0),
        ):
            self.assertEqual(manager.health_error(), "")
            self.assertEqual(manager.state("dedicated"), "starting")

    def test_stale_starting_health_fails_closed(self):
        manager = worker.ManagedProcess()
        manager.process = mock.Mock()
        manager.process.poll.return_value = None
        manager.health_required = True
        health = {
            "state": "starting",
            "error": "",
            "updated_unix_seconds": 1000.0,
        }

        with (
            mock.patch.object(worker, "read_managed_health", return_value=health),
            mock.patch.object(
                worker.time,
                "time",
                return_value=1000.0 + worker.CHILD_HEALTH_STALE_SECONDS + 1.0,
            ),
        ):
            self.assertIn("startup health has not refreshed", manager.health_error())
            self.assertEqual(manager.state("dedicated"), "error")


class ManagedProcessRestartTests(unittest.TestCase):
    def test_same_launch_records_exit_observed_before_restart(self):
        with tempfile.TemporaryDirectory() as directory:
            state_file = Path(directory) / "state.json"
            manager = worker.ManagedProcess()
            manager.process = mock.Mock()
            manager.process.poll.return_value = 1
            manager.process.returncode = 1
            manager.command = ("python", "trainer.py")
            manager.build_sha256 = "a" * 64
            manager.build_id = "build-a"
            manager.run_id = "run-a"
            manager.compatibility_key = "b" * 64
            manager.state_file = str(state_file.resolve())
            manager.started_monotonic = worker.time.monotonic() - 1.0

            with mock.patch.object(worker, "popen_owned") as popen:
                with self.assertRaisesRegex(RuntimeError, "restart deferred"):
                    manager.start(
                        ("python", "trainer.py"),
                        revision=5,
                        build_sha256="a" * 64,
                        build_id="build-a",
                        run_id="run-a",
                        compatibility_key="b" * 64,
                        state_file=state_file,
                        environment_args=(),
                    )

            self.assertEqual(manager.restart_failure_streak, 1)
            self.assertEqual(manager.last_exit_code, 1)
            self.assertFalse(popen.called)


    def test_changed_environment_args_bypass_same_command_backoff(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            manager = worker.ManagedProcess()
            manager.command = ("python", "trainer.py")
            manager.revision = 4
            manager.build_sha256 = "a" * 64
            manager.build_id = "build-a"
            manager.run_id = "run-a"
            manager.compatibility_key = "b" * 64
            manager.environment_args = ("--rl-map-size=64",)
            manager.worker_env_count = 2
            manager.state_file = str((root / "old-state.json").resolve())
            manager.restart_failure_streak = 5
            manager.restart_not_before_monotonic = worker.time.monotonic() + 120.0
            state_file = root / "new-state.json"

            with mock.patch.object(worker, "popen_owned") as popen:
                manager.start(
                    ("python", "trainer.py"),
                    revision=4,
                    build_sha256="a" * 64,
                    build_id="build-a",
                    run_id="run-a",
                    compatibility_key="b" * 64,
                    state_file=state_file,
                    environment_args=("--rl-map-size=128",),
                    worker_env_count=2,
                )

            self.assertEqual(manager.restart_failure_streak, 0)
            self.assertEqual(manager.restart_not_before_monotonic, 0.0)
            self.assertTrue(popen.called)


    def test_control_revision_does_not_bypass_same_launch_backoff(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            state_file = root / "state.json"
            manager = worker.ManagedProcess()
            manager.command = ("python", "trainer.py")
            manager.revision = 4
            manager.build_sha256 = "a" * 64
            manager.build_id = "build-a"
            manager.run_id = "run-a"
            manager.compatibility_key = "b" * 64
            manager.environment_args = ("--rl-map-size=64",)
            manager.worker_env_count = 2
            manager.state_file = str(state_file.resolve())
            manager.restart_failure_streak = 5
            manager.restart_not_before_monotonic = worker.time.monotonic() + 120.0

            with mock.patch.object(worker, "popen_owned") as popen:
                with self.assertRaisesRegex(RuntimeError, "restart deferred"):
                    manager.start(
                        ("python", "trainer.py"),
                        revision=5,
                        build_sha256="a" * 64,
                        build_id="build-a",
                        run_id="run-a",
                        compatibility_key="b" * 64,
                        state_file=state_file,
                        environment_args=("--rl-map-size=64",),
                        worker_env_count=2,
                    )

            self.assertEqual(manager.restart_failure_streak, 5)
            self.assertFalse(popen.called)


if __name__ == "__main__":
    unittest.main()
