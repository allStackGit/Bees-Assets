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
import shutil
import stat
import sys
import zipfile
from datetime import datetime, timezone
from pathlib import Path

RUN_RE = re.compile(r"[A-Za-z0-9._-]{1,128}")
TEXT_LOG_SUFFIXES = {".log", ".txt", ".jsonl", ".csv"}


def export_logs(
    bees_root: Path, assets_root: Path, run_id: str, output: Path,
    log_percent: int = 5,
) -> dict:
    if log_percent not in {1, 5, 10, 25, 50, 100}:
        raise ValueError("Log percentage must be 1, 5, 10, 25, 50 or 100")
    if not RUN_RE.fullmatch(run_id) or run_id in {".", ".."}:
        raise ValueError("Invalid run ID")
    bees_root = bees_root.resolve(strict=True)
    assets_root = assets_root.resolve(strict=True)
    output = output.resolve()
    if not output.parent.is_dir():
        raise ValueError("Output directory does not exist")
    sources = (
        ("workers", bees_root / "Training" / "TrainerLogs" / run_id, None),
        ("learner", bees_root / "ManagedBuilds" / "central-learner" / "logs" / run_id, None),
        ("trainer-results", bees_root / "Training" / "trainer-results" / run_id, TEXT_LOG_SUFFIXES),
        ("continual-service", bees_root / "Training" / "metadata" / "continuous-service" / run_id, TEXT_LOG_SUFFIXES),
        ("server-upload", assets_root / "BeesServer~" / "training-logs" / run_id, None),
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
            # Never follow directory symlinks or include unexpected hard-linked
            # device files. Zip entries are always relative to trusted roots.
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
    manifest = {
        "run_id": run_id,
        "generated_utc": datetime.now(timezone.utc).isoformat(),
        "complete_files_not_tails": log_percent == 100,
        "log_percent_per_file": log_percent,
        "selection": "Last specified percent of each available log by bytes, rounded to full line boundaries; small logs retained in full",
        "sources_checked": searched,
        "files": [],
        "excluded": [
            "checkpoints and trained models",
            "shared multi-run operator/server logs that cannot be attributed to this run",
            "remote worker logs not uploaded to the central training server",
        ],
    }
    with zipfile.ZipFile(output, "w", compression=zipfile.ZIP_DEFLATED,
                         compresslevel=1, allowZip64=True) as archive:
        for label, file, relative in selected:
            name = f"{label}/{relative}"
            total = file.stat().st_size
            start = 0 if log_percent == 100 else max(
                0, total - max(4096, math.ceil(total * log_percent / 100)),
            )
            with file.open("rb") as incoming, archive.open(name, "w", force_zip64=True) as target:
                if start:
                    incoming.seek(start - 1)
                    if incoming.read(1) != b"\\n":
                        incoming.readline()  # skip only the incomplete first record/line
                else:
                    incoming.seek(0)
                exported = 0
                while True:
                    chunk = incoming.read(1024 * 1024)
                    if not chunk:
                        break
                    target.write(chunk)
                    exported += len(chunk)
            manifest["files"].append({
                "path": name,
                "source_bytes": total,
                "exported_bytes": exported,
            })
        archive.writestr("manifest.json", json.dumps(manifest, indent=2) + "\n")
    return manifest


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
        print(json.dumps({"run_id": info["run_id"], "files": len(info["files"])}))
        return 0
    except (OSError, ValueError, zipfile.BadZipFile) as exc:
        print(str(exc), file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
