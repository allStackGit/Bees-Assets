"""Identity of a complete installed Unity simulation worker, not just its executable."""
from __future__ import annotations

import hashlib
import json
from pathlib import Path
import os
import stat

MAX_FILES = 20000
MAX_BYTES = 32 * 1024 ** 3
MAX_DEPTH = 12


def _sha256_file(filename: Path) -> str:
    digest = hashlib.sha256()
    with filename.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1048576), b""):
            digest.update(chunk)
    return digest.hexdigest()


def unity_worker_tree_sha256(entrypoint: Path) -> str:
    if not entrypoint.is_absolute() or entrypoint.is_symlink() or (
        entrypoint.resolve() != entrypoint or
        entrypoint.parent.resolve() != entrypoint.parent
    ):
        raise ValueError("Unity worker must have a real absolute executable and directory.")
    root = entrypoint.parent
    queue = [(root, 0)]
    rows = []
    total = 0
    while queue:
        folder, depth = queue.pop(0)
        for child in folder.iterdir():
            name = child.name
            if not name.isascii() or not all(" " <= ch <= "~" for ch in name) or (
                "/" in name or "\\" in name
            ):
                raise ValueError("Unity worker has an unsupported file name.")
            before = child.lstat()
            if stat.S_ISLNK(before.st_mode) or child.resolve() != child:
                raise ValueError("Unity worker contains redirected content.")
            if stat.S_ISDIR(before.st_mode):
                if depth >= MAX_DEPTH:
                    raise ValueError("Unity worker directory is too deeply nested.")
                queue.append((child, depth + 1))
            elif stat.S_ISREG(before.st_mode):
                total += before.st_size
                if len(rows) >= MAX_FILES or total > MAX_BYTES:
                    raise ValueError("Unity player exceeds bounded content identity capacity.")
                digest = _sha256_file(child)
                after = child.stat()
                if before.st_size != after.st_size or (
                    before.st_mtime_ns != after.st_mtime_ns
                ):
                    raise ValueError("Unity worker file changed while fingerprinting.")
                rows.append([child.relative_to(root).as_posix(), before.st_size, digest])
            else:
                raise ValueError("Unity worker contains an unsupported special file.")
    if len(rows) < 2 or not any(
        relative == entrypoint.name for relative, _size, _digest in rows
    ):
        raise ValueError("Unity player must have an executable and supporting data.")
    rows.sort(key=lambda row: row[0])
    return hashlib.sha256(
        json.dumps(rows, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
    ).hexdigest()
