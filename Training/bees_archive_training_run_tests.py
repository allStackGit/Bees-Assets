from __future__ import annotations

import json
import tempfile
import unittest
from pathlib import Path
from unittest import mock

import bees_archive_training_run as archive


class ArchiveTrainingRunTests(unittest.TestCase):
    def test_sync_chunks_logs_and_preserves_source_bytes(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            assets = root / "Assets"
            assets.mkdir()
            run_id = "run-1"
            source_root = root / "Training" / "TrainerLogs" / run_id / "worker-a"
            source_root.mkdir(parents=True)
            source = source_root / "Player-0.log"
            payload = b"0123456789abcdefghijklmnopqrstuvwxyz"
            source.write_bytes(payload)

            with mock.patch.object(archive, "CHUNK_BYTES", 10):
                history = archive.sync_run_history(assets, root, run_id, "test")

            manifest = json.loads((history / "manifest.json").read_text(encoding="utf-8"))
            entry = next(
                item for item in manifest["files"]
                if item["source"].endswith("Player-0.log")
            )
            self.assertEqual(entry["source_bytes"], len(payload))
            self.assertEqual(len(entry["parts"]), 4)
            rebuilt = b"".join(
                (history / "trainer-logs" / "worker-a" / part["file"]).read_bytes()
                for part in entry["parts"]
            )
            self.assertEqual(rebuilt, payload)

    def test_run_git_uses_explicit_git_executable(self):
        assets = Path("C:/Bees/Assets")
        with mock.patch.object(archive.subprocess, "run") as run:
            run.return_value.returncode = 0
            archive._run_git(
                assets,
                ["status"],
                git_executable="C:/Tools/Git/cmd/git.exe",
            )

        command = run.call_args.args[0]
        self.assertEqual(command[0], "C:/Tools/Git/cmd/git.exe")
        self.assertEqual(command[1:], ["status"])

    def test_main_forwards_git_executable_to_archive_commit(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            assets = root / "Assets"
            assets.mkdir()
            history = assets / "TrainingHistory~" / "runs" / "run-1"
            history.mkdir(parents=True)

            with (
                mock.patch.object(archive, "sync_run_history", return_value=history),
                mock.patch.object(archive, "commit_and_push") as commit,
            ):
                result = archive.main([
                    "--assets-root", str(assets),
                    "--bees-root", str(root),
                    "--run-id", "run-1",
                    "--git-executable", "C:/Tools/Git/cmd/git.exe",
                ])

        self.assertEqual(result, 0)
        self.assertEqual(
            commit.call_args.kwargs["git_executable"],
            "C:/Tools/Git/cmd/git.exe",
        )

    def test_operator_passes_resolved_git_to_archive_helper(self):
        operator = Path(__file__).resolve().parents[1] / "bees.ps1"
        source = operator.read_text(encoding="utf-8")
        start = source.index("function Archive-TrainingRun")
        end = source.index("\n}", start)
        block = source[start:end]
        self.assertIn("$git=Resolve-Git", block)
        self.assertIn("'--git-executable',$git", block)

    def test_archive_does_not_touch_durable_checkpoint_tree(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            assets = root / "Assets"
            assets.mkdir()
            checkpoint = root / "Training" / "trainer-results" / "run-1" / "checkpoint.pt"
            checkpoint.parent.mkdir(parents=True)
            checkpoint.write_bytes(b"durable-checkpoint")

            archive.sync_run_history(assets, root, "run-1", "test")

            self.assertEqual(checkpoint.read_bytes(), b"durable-checkpoint")
            self.assertFalse(
                any("checkpoint.pt" in str(path) for path in (assets / "TrainingHistory~").rglob("*"))
            )


if __name__ == "__main__":
    unittest.main()
