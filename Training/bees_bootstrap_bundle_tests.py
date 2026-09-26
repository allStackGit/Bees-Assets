"""Focused tests for atomic remote-bootstrap bundle publication."""

from __future__ import annotations

import hashlib
import json
from pathlib import Path
import tempfile
import unittest
from unittest import mock
import zipfile

import bees_bootstrap_bundle as bootstrap


def _runtime_archive(path: Path, version: str) -> bytes:
    with zipfile.ZipFile(path, "w", compression=zipfile.ZIP_DEFLATED) as bundle:
        bundle.writestr("bees-runtime-version.txt", version)
        bundle.writestr("bees_managed_remote_worker.py", "# worker\n")
    return path.read_bytes()


class BootstrapBundleTests(unittest.TestCase):
    def _inputs(self, root: Path, *, release_sha_override: str | None = None):
        version = "a" * 64
        runtime = root / "training-runtime.zip"
        runtime_bytes = _runtime_archive(runtime, version)
        runtime_sha = hashlib.sha256(runtime_bytes).hexdigest()

        release = root / "latest-training-release.json"
        release.write_text(
            json.dumps(
                {
                    "build_id": "build-123",
                    "training_runtime": {
                        "archive_sha256": release_sha_override or runtime_sha,
                        "runtime_version": version,
                    },
                }
            ),
            encoding="utf-8",
        )
        worker = root / "training-worker.token"
        worker.write_text("worker-secret", encoding="ascii")
        wan = root / "wan.token"
        wan.write_text("wan-secret", encoding="ascii")
        windows = root / "bridge.exe"
        windows.write_bytes(b"windows-bridge")
        linux = root / "bridge-linux"
        linux.write_bytes(b"linux-bridge")
        return runtime, worker, wan, release, windows, linux, runtime_sha, version

    def test_atomic_replace_retries_transient_windows_sharing_violation(self):
        with (
            mock.patch.object(
                bootstrap.os,
                "replace",
                side_effect=[PermissionError("busy"), None],
            ) as replace,
            mock.patch.object(
                bootstrap.time,
                "monotonic",
                side_effect=[0.0, 0.1],
            ),
            mock.patch.object(bootstrap.time, "sleep") as sleep,
        ):
            bootstrap._replace_with_retry(Path("source.tmp"), Path("bundle.zip"))

        self.assertEqual(replace.call_count, 2)
        sleep.assert_called_once_with(0.1)

    def test_bundle_contains_one_consistent_release_snapshot(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            (
                runtime,
                worker,
                wan,
                release,
                windows,
                linux,
                runtime_sha,
                version,
            ) = self._inputs(root)
            output = root / "bees-bootstrap-bundle.zip"

            result = bootstrap.create_bundle(
                output=output,
                runtime=runtime,
                worker_token=worker,
                wan_token=wan,
                release=release,
                windows_bridge=windows,
                linux_bridge=linux,
            )

            self.assertEqual(result["build_id"], "build-123")
            self.assertEqual(result["runtime_sha256"], runtime_sha)
            self.assertEqual(result["runtime_version"], version)
            self.assertEqual(
                result["bundle_sha256"],
                hashlib.sha256(output.read_bytes()).hexdigest(),
            )
            with zipfile.ZipFile(output, "r") as bundle:
                self.assertEqual(
                    set(bundle.namelist()),
                    {name for name, _key, _mode in bootstrap.ENTRY_SPECS},
                )
                self.assertEqual(bundle.read("bees-remote-runtime.zip"), runtime.read_bytes())
                self.assertEqual(
                    json.loads(bundle.read("latest-training-release.json")),
                    json.loads(release.read_text(encoding="utf-8")),
                )

    def test_mismatched_runtime_is_rejected_without_replacing_published_bundle(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            runtime, worker, wan, release, windows, linux, _sha, _version = self._inputs(
                root,
                release_sha_override="0" * 64,
            )
            output = root / "bees-bootstrap-bundle.zip"
            output.write_bytes(b"previous-good-bundle")

            with self.assertRaisesRegex(ValueError, "SHA-256 does not match"):
                bootstrap.create_bundle(
                    output=output,
                    runtime=runtime,
                    worker_token=worker,
                    wan_token=wan,
                    release=release,
                    windows_bridge=windows,
                    linux_bridge=linux,
                )

            self.assertEqual(output.read_bytes(), b"previous-good-bundle")

    def test_missing_or_empty_secret_fails_closed(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            runtime, worker, wan, release, windows, linux, _sha, _version = self._inputs(root)
            wan.write_bytes(b"")
            output = root / "bundle.zip"

            with self.assertRaisesRegex(ValueError, "WAN token.*empty"):
                bootstrap.create_bundle(
                    output=output,
                    runtime=runtime,
                    worker_token=worker,
                    wan_token=wan,
                    release=release,
                    windows_bridge=windows,
                    linux_bridge=linux,
                )

            self.assertFalse(output.exists())


if __name__ == "__main__":
    unittest.main()
