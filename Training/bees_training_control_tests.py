from __future__ import annotations

import json
import tempfile
import unittest
import zipfile
from pathlib import Path

import bees_training_control as control
import bees_training_worker_agent as agent


class FakeClient:
    def __init__(self, archive: Path):
        self.archive = archive

    def download_artifact(self, _url: str, destination: Path) -> None:
        destination.write_bytes(self.archive.read_bytes())


class TrainingControlClientTests(unittest.TestCase):
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

    def test_full_game_local_state_defaults_offline_to_inference(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "state.json"
            agent.write_local_state(path, desired=None, online=False, last_error="offline")
            value = json.loads(path.read_text(encoding="utf-8"))
            self.assertEqual(value["desired_mode"], "inference")
            self.assertFalse(value["online"])


if __name__ == "__main__":
    unittest.main()
