"""Static regression coverage for the persistent training worker's log metrics."""

from __future__ import annotations

import tempfile
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


class BackgroundBuildPreparerTests(unittest.TestCase):
    def test_wait_for_build_joins_only_matching_preparation(self):
        preparer = agent.BackgroundBuildPreparer.__new__(agent.BackgroundBuildPreparer)
        preparer._lock = threading.Lock()
        preparer._requested_build_id = "build-a"
        preparer._thread = mock.Mock()

        preparer.wait_for_build({"build_id": "build-b"})
        preparer._thread.join.assert_not_called()

        preparer.wait_for_build({"build_id": "build-a"})
        preparer._thread.join.assert_called_once_with()


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
