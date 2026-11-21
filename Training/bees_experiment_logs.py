"""Export complete, available run-scoped Bees training logs without checkpoints.

Invoked by the localhost Experiment Manager. No training state is modified.
Shared operator logs are excluded because they cannot be attributed reliably to
one experiment; remote logs not yet uploaded are likewise unavailable.
"""
from __future__ import annotations

import argparse
import json
import math
import re
import stat
import sys
import zipfile
from datetime import datetime, timezone
from pathlib import Path

RUN_RE = re.compile(r"[A-Za-z0-9._-]{1,128}")
TEXT_LOG_SUFFIXES = {".log", ".txt", ".jsonl", ".csv", ".json"}


MAX_ZIP_BYTES = 500_000_000  # Decimal MB, not MiB.
CHUNK_BYTES = 32 * 1024 * 1024


def export_logs(
    bees_root: Path, assets_root: Path, run_id: str, output: Path,
    log_percent: int = 5, max_zip_bytes: int = MAX_ZIP_BYTES,
    chunk_bytes: int = CHUNK_BYTES,
) -> dict:
    if log_percent not in {1, 5, 10, 25, 50, 100}:
        raise ValueError("Log percentage must be 1, 5, 10, 25, 50 or 100")
    if max_zip_bytes < 1024 or chunk_bytes < 1 or chunk_bytes > max_zip_bytes // 3:
        raise ValueError("Invalid ZIP size or log chunk limit")
    if not RUN_RE.fullmatch(run_id) or run_id in {".", ".."}:
        raise ValueError("Invalid run ID")
    bees_root = bees_root.resolve(strict=True)
    assets_root = assets_root.resolve(strict=True)
    output = output.resolve()
    if not output.parent.is_dir():
        raise ValueError("Output directory does not exist")
    sources = (
        ("workers", bees_root / "Training" / "TrainerLogs" / run_id, TEXT_LOG_SUFFIXES),
        ("learner", bees_root / "ManagedBuilds" / "central-learner" / "logs" / run_id, TEXT_LOG_SUFFIXES),
        ("trainer-results", bees_root / "Training" / "trainer-results" / run_id, TEXT_LOG_SUFFIXES),
        ("continual-service", bees_root / "Training" / "metadata" / "continuous-service" / run_id, TEXT_LOG_SUFFIXES),
        ("server-upload", assets_root / "BeesServer~" / "training-logs" / run_id, TEXT_LOG_SUFFIXES),
    )
    selected = []
    searched = []
    for label, root, suffixes in sources:
        searched.append(label)
        if root.is_symlink() or not root.is_dir():
            continue
        for file in sorted(root.rglob("*")):
            if file.is_symlink() or not file.is_file():
                continue
            relative = file.relative_to(root)
            if any((root / Path(*relative.parts[:i])).is_symlink()
                   for i in range(1, len(relative.parts))):
                continue
            if suffixes is not None and file.suffix.lower() not in suffixes:
                continue
            if not stat.S_ISREG(file.stat().st_mode):
                continue
            selected.append((label, file, relative.as_posix()))
    if not selected:
        raise FileNotFoundError("No uploaded or locally run-scoped logs are available for this experiment")
    common = {
        "run_id": run_id,
        "generated_utc": datetime.now(timezone.utc).isoformat(),
        "complete_files_not_tails": log_percent == 100,
        "log_percent_per_file": log_percent,
        "selection": "Last specified percent of each log by bytes, starting on a complete line; oversized log excerpts are split on complete lines",
        "sources_checked": searched,
        "excluded": [
            "checkpoints and trained models",
            "shared multi-run operator/server logs that cannot be attributed to this run",
            "remote worker logs not uploaded to the central training server",
        ],
    }
    parts = []
    created = []
    files = []
    archive = None
    archive_path = None
    entries = []
    # Leave room for one complete entry, the ZIP directory and manifest.
    # Verify the actual closed size against the hard 500 MB limit.
    reserve = min(max_zip_bytes // 4, max(20_000, chunk_bytes + 10_000_000))
    target = max_zip_bytes - reserve

    def open_part() -> None:
        nonlocal archive, archive_path, entries
        archive_path = output.with_name(f"{output.stem}-part-{len(parts) + 1:04d}.zip")
        archive = zipfile.ZipFile(archive_path, "w", compression=zipfile.ZIP_DEFLATED,
                                  compresslevel=1, allowZip64=True)
        created.append(archive_path)
        entries = []

    def close_part() -> None:
        nonlocal archive
        if archive is None:
            return
        archive.writestr("manifest.json", json.dumps({
            **common, "part_number": len(parts) + 1, "files": entries,
        }, indent=2) + "\n")
        archive.close()
        archive = None
        size = archive_path.stat().st_size
        if size > max_zip_bytes:
            raise ValueError("A generated ZIP exceeds the maximum allowed size; no archive was published")
        parts.append({"file": archive_path.name, "bytes": size})

    try:
        open_part()
        for label, file, relative in selected:
            name = f"{label}/{relative}"
            total = file.stat().st_size
            start = 0 if log_percent == 100 else max(
                0, total - max(4096, math.ceil(total * log_percent / 100)))
            split = total - start > chunk_bytes
            exported_total = 0
            with file.open("rb") as incoming:
                if start:
                    incoming.seek(start - 1)
                    if incoming.read(1) != b"\n":
                        incoming.readline(max(0, total - incoming.tell()))
                else:
                    incoming.seek(0)
                segment = 0
                while True:
                    remaining = max(0, total - incoming.tell())
                    data = incoming.read(min(chunk_bytes, remaining))
                    if not data and segment:
                        break
                    if data and len(data) == chunk_bytes and data[-1:] != b"\n":
                        data += incoming.readline(max(0, total - incoming.tell()))
                    if archive is None:
                        open_part()
                    segment += 1
                    part_name = f"{name}.part{segment:05d}" if split else name
                    with archive.open(part_name, "w", force_zip64=True) as target_file:
                        target_file.write(data)
                    entries.append({
                        "path": part_name, "source_path": name,
                        "source_bytes": total, "exported_bytes": len(data),
                        "segment": segment,
                    })
                    exported_total += len(data)
                    if archive.fp.tell() >= target:
                        close_part()
                    if not data or len(data) < chunk_bytes:
                        break
            files.append({"path": name, "source_bytes": total, "exported_bytes": exported_total})
        close_part()
        if len(parts) == 1:
            only = output.with_name(parts[0]["file"])
            only.rename(output)
            parts[0]["file"] = output.name
        return {**common, "parts": parts, "files": files}
    except Exception:
        if archive is not None:
            archive.close()
        for file in created:
            file.unlink(missing_ok=True)
        raise


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--bees-root", required=True, type=Path)
    parser.add_argument("--assets-root", required=True, type=Path)
    parser.add_argument("--run-id", required=True)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--log-percent", type=int, default=5,
                        choices=[1, 5, 10, 25, 50, 100])
    args = parser.parse_args()
    try:
        info = export_logs(
            args.bees_root, args.assets_root, args.run_id, args.output,
            args.log_percent,
        )
        print(json.dumps({"run_id": info["run_id"], "files": len(info["files"]),
                          "parts": info["parts"]}))
        return 0
    except (OSError, ValueError, zipfile.BadZipFile) as exc:
        print(str(exc), file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
