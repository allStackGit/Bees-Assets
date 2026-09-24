from __future__ import annotations

import argparse
import hashlib
import json
import os
import tempfile
import unittest
from unittest import mock
from pathlib import Path

import bees_trainer_agent as agent


class FakeClient:
    def __init__(self, platform: str, files: dict[str, bytes], entrypoint: str):
        self.platform = platform
        self.files = files
        self.entrypoint = entrypoint
        entries = []
        for name in sorted(files):
            data = files[name]
            entries.append({
                "path": name,
                "size": len(data),
                "sha256": hashlib.sha256(data).hexdigest(),
                "mode": 0o755 if name == entrypoint else 0o644,
            })
        identity = {
            "schema_version": 1,
            "platform": platform,
            "game_build_version": "v1",
            "entrypoint": entrypoint,
            "files": entries,
        }
        manifest_hash = hashlib.sha256(agent.canonical_json(identity).encode("utf-8")).hexdigest()
        self.value = {
            **identity,
            "build_id": f"build-{manifest_hash[:24]}",
            "manifest_sha256": manifest_hash,
            "file_count": len(entries),
            "total_size_bytes": sum(len(value) for value in files.values()),
        }
        self.downloads = 0

    def manifest(self, platform: str):
        self.assert_platform(platform)
        return self.value

    def assert_platform(self, platform: str):
        if platform != self.platform:
            raise AssertionError((platform, self.platform))

    def download_to(self, platform, relative_path, destination, expected_size, expected_sha256):
        self.assert_platform(platform)
        data = self.files[relative_path]
        self.assert_file(data, expected_size, expected_sha256)
        destination.parent.mkdir(parents=True, exist_ok=True)
        destination.write_bytes(data)
        self.downloads += 1

    @staticmethod
    def assert_file(data, size, sha):
        if len(data) != size or hashlib.sha256(data).hexdigest() != sha:
            raise AssertionError("bad expected file metadata")


class FakeActor:
    def __init__(self):
        self.is_running = False
        self.starts = 0
        self.stops = 0
        self.commands = []

    def running(self):
        return self.is_running

    def start(self, command, cwd):
        self.is_running = True
        self.starts += 1
        self.commands.append((list(command), Path(cwd)))

    def stop(self):
        if self.is_running:
            self.stops += 1
        self.is_running = False


class FakeInstaller:
    def __init__(self, executable: Path, build_id: str):
        self.executable = executable
        self.build_id = build_id
        self.calls = 0

    def current(self):
        return {"build_id": self.build_id}

    def ensure(self, desired):
        self.calls += 1
        if desired["build_id"] != self.build_id:
            raise AssertionError("unexpected build")
        return self.executable


class TrainerAgentTests(unittest.TestCase):
    def test_lease_expiration_is_fail_closed_for_rollout_training(self):
        self.assertTrue(agent.lease_expired(None, 30, now=100))
        self.assertFalse(agent.lease_expired(80, 30, now=100))
        self.assertTrue(agent.lease_expired(70, 30, now=100))

    def test_build_installer_downloads_and_reuses_exact_content_addressed_build(self):
        with tempfile.TemporaryDirectory() as root:
            client = FakeClient(
                "linux-x64",
                {
                    "Bees.x86_64": b"binary",
                    "Bees_Data/globalgamemanagers": b"data",
                },
                "Bees.x86_64",
            )
            installer = agent.BuildInstaller(Path(root), "linux-x64", client)
            executable = installer.ensure({"build_id": client.value["build_id"]})
            self.assertEqual(executable.read_bytes(), b"binary")
            self.assertEqual(client.downloads, 2)
            self.assertEqual(installer.current()["build_id"], client.value["build_id"])

            again = installer.ensure({"build_id": client.value["build_id"]})
            self.assertEqual(again, executable)
            self.assertEqual(client.downloads, 2)

    def test_managed_process_isolates_and_terminates_posix_process_group(self):
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
            managed.start(["python", "worker.py"], Path("."))
            self.assertTrue(popen.call_args.kwargs["start_new_session"])
            managed.stop()

        killpg.assert_called_once_with(4242, agent.signal.SIGTERM)

    def test_actor_command_always_uses_server_synchronized_executable(self):
        with tempfile.TemporaryDirectory() as root:
            assets = Path(root)
            training = assets / "Training"
            training.mkdir()
            (training / "bees_elastic_wan_actor_worker.py").write_text("", encoding="utf-8")
            command = agent.build_actor_command(
                python="python",
                assets_root=assets,
                actor_id=3,
                env_count=48,
                ssh_target="trainer@example",
                env_path=Path("/builds/current/Bees.x86_64"),
                wan_auth_token_file=Path("/tokens/wan.txt"),
                broker_port=55051,
                local_broker_port=55051,
                local_base_port=5005,
                torch_device="cpu",
                graphics=False,
                ssh_executable="ssh",
                ssh_options=["Compression=yes"],
            )
            self.assertIn("--actor-id=3", command)
            self.assertIn("--envs=48", command)
            self.assertIn("--env=/builds/current/Bees.x86_64", command)
            self.assertIn("--ssh-option", command)
            self.assertIn("Compression=yes", command)

    def test_new_server_epoch_or_config_revision_restarts_actor(self):
        with tempfile.TemporaryDirectory() as root:
            root_path = Path(root)
            token = root_path / "control-token.txt"
            token.write_text("abcdefghijklmnopqrstuvwxyz0123456789TOKEN", encoding="utf-8")
            wan = root_path / "wan-token.txt"
            wan.write_text("abcdefghijklmnopqrstuvwxyz0123456789WANXX", encoding="utf-8")
            training = root_path / "Training"
            training.mkdir()
            (training / "bees_elastic_wan_actor_worker.py").write_text("", encoding="utf-8")
            executable = root_path / "Bees.x86_64"
            executable.write_bytes(b"binary")
            args = argparse.Namespace(
                actor_id=1,
                envs=8,
                ssh="trainer@example",
                control_token_file=str(token),
                wan_auth_token_file=str(wan),
                install_root=str(root_path / "installed"),
                node_id="node-1",
                platform="linux-x64",
                assets_root=str(root_path),
                python="python",
                control_url="http://127.0.0.1:1",
                control_port=7148,
                local_control_port=7148,
                broker_port=55051,
                local_broker_port=55051,
                local_base_port=5005,
                ssh_executable="ssh",
                ssh_option=[],
                torch_device="cpu",
                graphics=False,
                heartbeat_seconds=5.0,
                control_timeout_seconds=1.0,
                tunnel_startup_seconds=0.0,
            )
            runner = agent.TrainerAgent(args)
            runner.actor = FakeActor()
            runner.installer = FakeInstaller(executable, "build-0123456789abcdef01234567")

            def response(epoch, revision):
                return {
                    "schema_version": 1,
                    "server_epoch": epoch,
                    "lease_seconds": 30,
                    "desired": {
                        "training_enabled": True,
                        "config_revision": revision,
                        "build": {"build_id": "build-0123456789abcdef01234567"},
                    },
                }

            runner._apply(response("epoch-a", "config-a"))
            self.assertEqual(runner.actor.starts, 1)
            self.assertEqual(runner.actor.stops, 0)

            runner._apply(response("epoch-a", "config-a"))
            self.assertEqual(runner.actor.starts, 1)
            self.assertEqual(runner.actor.stops, 0)

            runner._apply(response("epoch-b", "config-a"))
            self.assertEqual(runner.actor.starts, 2)
            self.assertEqual(runner.actor.stops, 1)

            runner._apply(response("epoch-b", "config-b"))
            self.assertEqual(runner.actor.starts, 3)
            self.assertEqual(runner.actor.stops, 2)

            runner._apply({
                "schema_version": 1,
                "server_epoch": "epoch-b",
                "lease_seconds": 30,
                "desired": {"training_enabled": False, "config_revision": "config-b", "build": None},
            })
            self.assertFalse(runner.actor.running())
            self.assertEqual(runner.actor.stops, 3)


if __name__ == "__main__":
    unittest.main()
