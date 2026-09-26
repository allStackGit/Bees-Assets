"""Focused tests for immutable training-runtime release packaging."""

from __future__ import annotations

import json
from pathlib import Path
import tempfile
import unittest
import zipfile

import bees_release_runtime as runtime


class ReleaseRuntimeTests(unittest.TestCase):
    def _training_root(self, root: Path) -> Path:
        training = root / "Assets" / "Training"
        training.mkdir(parents=True)
        for name in (
            "bees_training_worker_agent.py",
            "bees_continual_elastic_wan_service.py",
            "bees_managed_remote_worker.py",
            "helper.py",
        ):
            (training / name).write_text(f"# {name}\nVALUE = {name!r}\n", encoding="utf-8")
        (training / "bees_remote_requirements.txt").write_text("mlagents==1.1.0\n", encoding="utf-8")
        (training / "bees_learner_requirements.txt").write_text("mlagents==1.1.0\n", encoding="utf-8")
        (training / "requirements-continual.txt").write_text("numpy\n", encoding="utf-8")
        (training / "rl_1v1_config.yaml").write_text("behaviors: {}\n", encoding="utf-8")
        (training / "continual_learning_config.json").write_text("{}\n", encoding="utf-8")
        return training

    def test_package_verify_and_install_are_content_addressed(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            training = self._training_root(root)
            archive = root / "release" / "training-runtime.zip"

            packaged = runtime.package_runtime(
                assets_root=root / "Assets",
                output=archive,
                build_id="build-123",
                source_commit="abc123",
            )
            verified = runtime.verify_runtime(
                archive,
                expected_sha256=packaged["archive_sha256"],
                expected_version=packaged["runtime_version"],
                expected_build_id="build-123",
            )
            installed = runtime.install_runtime(
                archive,
                destination_root=root / "installed",
                expected_sha256=packaged["archive_sha256"],
                expected_version=packaged["runtime_version"],
                expected_build_id="build-123",
            )

            self.assertEqual(verified["runtime_version"], packaged["runtime_version"])
            installed_root = Path(installed["installed_root"])
            self.assertEqual(
                (installed_root / "helper.py").read_bytes(),
                (training / "helper.py").read_bytes(),
            )
            self.assertEqual(
                (installed_root / runtime.VERSION_NAME).read_text(encoding="ascii").strip(),
                packaged["runtime_version"],
            )
            manifest = json.loads(
                (installed_root / runtime.MANIFEST_NAME).read_text(encoding="utf-8")
            )
            self.assertEqual(manifest["build_id"], "build-123")
            self.assertEqual(manifest["source_commit"], "abc123")

            # Reinstall is idempotent and revalidates the immutable destination.
            again = runtime.install_runtime(
                archive,
                destination_root=root / "installed",
                expected_sha256=packaged["archive_sha256"],
                expected_version=packaged["runtime_version"],
                expected_build_id="build-123",
            )
            self.assertEqual(again["installed_root"], installed["installed_root"])

    def test_source_change_changes_runtime_version(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            training = self._training_root(root)
            first = runtime.package_runtime(
                assets_root=root / "Assets",
                output=root / "first.zip",
                build_id="build-a",
                source_commit="a",
            )
            (training / "helper.py").write_text("VALUE = 'changed'\n", encoding="utf-8")
            second = runtime.package_runtime(
                assets_root=root / "Assets",
                output=root / "second.zip",
                build_id="build-b",
                source_commit="b",
            )
            self.assertNotEqual(first["runtime_version"], second["runtime_version"])

    def test_verify_fails_closed_on_archive_identity_or_path_tampering(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            self._training_root(root)
            archive = root / "runtime.zip"
            packaged = runtime.package_runtime(
                assets_root=root / "Assets",
                output=archive,
                build_id="build-safe",
                source_commit="safe",
            )
            with self.assertRaisesRegex(ValueError, "SHA-256 mismatch"):
                runtime.verify_runtime(archive, expected_sha256="0" * 64)
            with self.assertRaisesRegex(ValueError, "build mismatch"):
                runtime.verify_runtime(archive, expected_build_id="other-build")

            unsafe = root / "unsafe.zip"
            with zipfile.ZipFile(unsafe, "w") as bundle:
                bundle.writestr("../escape.py", b"x")
                bundle.writestr(runtime.MANIFEST_NAME, b"{}")
                bundle.writestr(runtime.VERSION_NAME, b"x")
            with self.assertRaisesRegex(ValueError, "unsafe member path"):
                runtime.verify_runtime(unsafe)


if __name__ == "__main__":
    unittest.main()
