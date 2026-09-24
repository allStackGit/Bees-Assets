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
                state_file=Path("state.json"),
                environment_args=(),
            )
            self.assertTrue(popen.call_args.kwargs["start_new_session"])
            managed.stop()

        killpg.assert_called_once_with(4242, signal.SIGTERM)

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
            ["python", "worker.py", "--env", "{env}", "--env-args", "{env_args}"],
            Path("/tmp/Bees.x86_64"),
            ["--rl-map-size", "64"],
        )
        self.assertEqual(
            rendered,
            [
                "python",
                "worker.py",
                "--env",
                "/tmp/Bees.x86_64",
                "--env-args",
                "--rl-map-size",
                "64",
            ],
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
