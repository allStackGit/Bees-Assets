"""Snapshot one Bees training run's logs into local Git-ignored history.

The history lives under TrainingHistory~ so Unity ignores it and .gitignore keeps it out of source
control. Log files are split into 48 MiB chunks while preserving bytes. No checkpoint/model
directory is copied or deleted; optimizer/checkpoint data remains in the durable B:\\Bees\\Training
tree under its run id. This helper never stages, commits, or pushes Git data.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import shutil
import tempfile
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Iterable, Optional, Sequence


CHUNK_BYTES = 48 * 1024 * 1024
SCHEMA_VERSION = 1


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _safe_component(value: str, label: str) -> str:
    if not value or any(ch not in "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789._-" for ch in value):
        raise ValueError(f"{label} contains unsafe characters: {value!r}")
    return value


def _atomic_json(path: Path, value: dict[str, Any]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    fd, temp_name = tempfile.mkstemp(prefix=path.name + ".", suffix=".tmp", dir=str(path.parent))
    try:
        with os.fdopen(fd, "w", encoding="utf-8", newline="\n") as handle:
            json.dump(value, handle, indent=2, sort_keys=True)
            handle.write("\n")
            handle.flush()
            os.fsync(handle.fileno())
        os.replace(temp_name, path)
    finally:
        try:
            os.unlink(temp_name)
        except FileNotFoundError:
            pass


def _remove_existing_parts(destination: Path) -> None:
    for existing in destination.parent.glob(destination.name + ".part*"):
        if existing.is_file():
            existing.unlink()


def _sync_chunked(source: Path, destination: Path) -> dict[str, Any]:
    destination.parent.mkdir(parents=True, exist_ok=True)
    if destination.exists():
        destination.unlink()
    _remove_existing_parts(destination)
    parts: list[dict[str, Any]] = []
    source_digest = hashlib.sha256()
    source_bytes = 0
    with source.open("rb") as src:
        index = 1
        while True:
            data = src.read(CHUNK_BYTES)
            if not data:
                break
            source_digest.update(data)
            source_bytes += len(data)
            part = destination.with_name(destination.name + f".part{index:04d}")
            with part.open("wb") as out:
                out.write(data)
            parts.append({
                "file": part.name,
                "bytes": len(data),
                "sha256": hashlib.sha256(data).hexdigest(),
            })
            index += 1
    if not parts:
        part = destination.with_name(destination.name + ".part0001")
        part.write_bytes(b"")
        parts.append({"file": part.name, "bytes": 0, "sha256": hashlib.sha256(b"").hexdigest()})
    return {
        "source_bytes": source_bytes,
        "source_sha256": source_digest.hexdigest(),
        "parts": parts,
    }


def _sync_plain(source: Path, destination: Path) -> dict[str, Any]:
    destination.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(source, destination)
    return {"file": destination.name, "bytes": destination.stat().st_size, "sha256": _sha256(destination)}


def _iter_files(root: Path) -> Iterable[Path]:
    if not root.is_dir():
        return ()
    return (path for path in sorted(root.rglob("*")) if path.is_file())


def sync_run_history(assets_root: Path, bees_root: Path, run_id: str, reason: str) -> Path:
    run_id = _safe_component(run_id, "run id")
    destination_root = assets_root / "TrainingHistory~" / "runs" / run_id
    destination_root.mkdir(parents=True, exist_ok=True)

    sources = [
        ("trainer-logs", bees_root / "Training" / "TrainerLogs" / run_id, True),
        ("central-training-logs", bees_root / "Logs" / "Training", True),
        ("service-state", bees_root / "Training" / "metadata" / "continuous-service" / run_id, False),
    ]
    metadata_files = [
        ("run-lifecycle.json", bees_root / "Training" / "RunLifecycle" / "current.json"),
        ("latest-training-release.json", bees_root / "Builds" / "latest-training-release.json"),
    ]

    manifest_files: list[dict[str, Any]] = []
    for label, root, chunk_logs in sources:
        if not root.is_dir():
            continue
        for source in _iter_files(root):
            relative = source.relative_to(root)
            target = destination_root / label / relative
            if chunk_logs:
                captured = _sync_chunked(source, target)
                manifest_files.append({
                    "source": str(source),
                    "destination": str(target.relative_to(destination_root)),
                    **captured,
                })
            else:
                item = _sync_plain(source, target)
                manifest_files.append({
                    "source": str(source),
                    "destination": str(target.relative_to(destination_root)),
                    **item,
                })

    for name, source in metadata_files:
        if not source.is_file():
            continue
        target = destination_root / "metadata" / name
        item = _sync_plain(source, target)
        manifest_files.append({
            "source": str(source),
            "destination": str(target.relative_to(destination_root)),
            **item,
        })

    manifest = {
        "schema_version": SCHEMA_VERSION,
        "run_id": run_id,
        "snapshot_utc": datetime.now(timezone.utc).isoformat(),
        "reason": reason,
        "durable_training_data_path": str(bees_root / "Training"),
        "files": manifest_files,
    }
    _atomic_json(destination_root / "manifest.json", manifest)
    return destination_root


def _parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser()
    parser.add_argument("--assets-root", required=True)
    parser.add_argument("--bees-root", required=True)
    parser.add_argument("--run-id", required=True)
    parser.add_argument("--reason", default="pre-build")
    return parser


def main(argv: Optional[Sequence[str]] = None) -> int:
    args = _parser().parse_args(argv)
    assets = Path(args.assets_root).resolve()
    bees = Path(args.bees_root).resolve()
    history = sync_run_history(assets, bees, args.run_id, args.reason)
    print(history)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
