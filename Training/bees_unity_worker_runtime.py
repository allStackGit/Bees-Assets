"""Stage a private disposable Unity player for one Hub-owned process lifetime.

Unity/ML-Agents may write timers and player state beside the player executable.
Never allow those writes to modify a source-verified installed worker release.
The clone is outside the job's export directory and is removed on normal exit.
"""
from __future__ import annotations

import os
from pathlib import Path
import shutil
import tempfile

from bees_unity_worker_identity import unity_worker_tree_sha256


class StagedUnityPlayer:
    def __init__(self, installed_executable: Path, job_root: Path):
        if not installed_executable.is_absolute() or (
            installed_executable.is_symlink() or
            installed_executable.resolve() != installed_executable
        ):
            raise ValueError("Bees Unity job requires a real installed player executable.")
        if not job_root.is_absolute() or job_root.is_symlink() or (
            not job_root.is_dir() or job_root.resolve() != job_root
        ):
            raise ValueError("Bees Unity runtime requires a real private job artifact owner.")
        self.source = installed_executable
        self.job_root = job_root
        self._temporary = None

    def start(self, expected_payload_sha256: str | None = None) -> Path:
        if self._temporary is not None:
            raise RuntimeError("Unity runtime staging has already been started.")
        scratch = self.job_root.parent / "_unity_runtime"
        scratch.mkdir(mode=0o700, exist_ok=True)
        if scratch.is_symlink() or scratch.resolve() != scratch or (
            os.name != "nt" and scratch.stat().st_mode & 0o077
        ):
            raise ValueError("Unity runtime scratch owner is redirected or not private.")
        self._temporary = tempfile.TemporaryDirectory(
            prefix=self.job_root.name + "-", dir=scratch,
            ignore_cleanup_errors=True,
        )
        try:
            original_hash = unity_worker_tree_sha256(self.source)
            if expected_payload_sha256 is not None and (
                original_hash != expected_payload_sha256
            ):
                raise ValueError("Installed Unity worker changed before runtime copy.")
            # The intermediate directory is intentionally named builds: the
            # existing WAN actor's detached-Unity process cleanup recognizes
            # only a bounded managed builds/ subtree and its communicator ports.
            player_root = Path(self._temporary.name) / "builds" / "worker"
            player_root.parent.mkdir()
            shutil.copytree(self.source.parent, player_root, symlinks=True)
            executable = player_root / self.source.name
            if unity_worker_tree_sha256(executable) != original_hash:
                raise ValueError("Staged Unity player differs from its immutable source.")
            return executable
        except Exception:
            self.close()
            raise

    def close(self) -> None:
        temporary = self._temporary
        self._temporary = None
        if temporary is not None:
            temporary.cleanup()
