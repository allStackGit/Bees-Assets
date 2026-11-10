"""Read-only, project-neutral export of locally available experiment job telemetry.

The loopback coordinator pins all job IDs and the artifact root in a private
specification. This tool never scans checkouts, executes training or copies
model/checkpoint binaries. Every ZIP part is independently readable and <=500MB.
Remote-only artifacts remain explicitly absent from the manifest.
"""
from __future__ import annotations

import argparse
import json
import os
import re
import stat
import sys
import zipfile
from datetime import datetime, timezone
from pathlib import Path

PART_LIMIT = 500_000_000
TARGET_BYTES = 450_000_000
CHUNK_BYTES = 24_000_000
UUID = re.compile(r"^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$")
PROJECT = re.compile(r"^[a-z][a-z0-9_-]{0,47}$")
METRIC = re.compile(r"^[a-z][a-z0-9_]{0,39}$")
SUFFIXES = {".json", ".jsonl", ".ndjson", ".csv", ".tsv", ".txt", ".log"}
DENIED_PARTS = {
    ".git", "private", "secrets", "credentials", "keys", "checkpoints",
    "models", "opponents", "__pycache__", "build", "node_modules",
}
DENIED_NAMES = ("token", "secret", "password", "credential", "rollout.key")
MAX_SOURCE_FILES = 200_000


def safe_source(root: Path, source: Path, include_replays: bool) -> bool:
    rel = source.relative_to(root)
    if not rel.parts or any(part.lower() in DENIED_PARTS for part in rel.parts):
        return False
    if not include_replays and "replays" in rel.parts:
        return False
    if any(term in source.name.lower() for term in DENIED_NAMES):
        return False
    if source.suffix.lower() not in SUFFIXES:
        return False
    if source.is_symlink() or not source.is_file():
        return False
    real = source.resolve(strict=True)
    if not real.is_relative_to(root) or real != source.absolute():
        return False
    info = source.stat()
    return stat.S_ISREG(info.st_mode) and info.st_nlink == 1


def export(spec: dict, output: Path) -> dict:
    project = spec["project_id"]
    jobs = spec["jobs"]
    root = Path(spec["artifact_root"])
    if not PROJECT.fullmatch(project) or not isinstance(jobs, list) or not 1 <= len(jobs) <= 1000:
        raise ValueError("Invalid selected project or job count")
    if len({job["id"] for job in jobs}) != len(jobs) or any(
        not UUID.fullmatch(str(job["id"])) or job.get("project_id") != project
        for job in jobs
    ):
        raise ValueError("Invalid selected job identifiers")
    metrics = spec.get("metrics", [])
    if not isinstance(metrics, list) or len(metrics) > 96 or any(
        not isinstance(name, str) or not METRIC.fullmatch(name)
        for name in metrics
    ) or len(set(metrics)) != len(metrics):
        raise ValueError("Invalid metric selection")
    additional = spec.get("additional_sources", [])
    selected_ids = {job["id"] for job in jobs}
    if not isinstance(additional, list) or len(additional) > 6000 or any(
        not isinstance(entry, dict) or
        set(entry) != {"job_id", "label", "path"} or
        entry["job_id"] not in selected_ids or
        not re.fullmatch(r"[a-z][a-z0-9_-]{0,47}", str(entry["label"])) or
        not isinstance(entry["path"], str) or
        not Path(entry["path"]).is_absolute()
        for entry in additional
    ):
        raise ValueError("Invalid operator-owned extra artifact sources")
    if output.exists() or not output.parent.is_dir():
        raise ValueError("Export location already exists or is unavailable")
    if root.is_symlink() or not root.is_dir() or root.resolve() != root.absolute():
        raise ValueError("Trusted local artifact root is missing or redirected")

    manifest = {
        "schema_version": 1,
        "project_id": project,
        "exported_at": datetime.now(timezone.utc).isoformat(),
        "selected_job_ids": [job["id"] for job in jobs],
        "selected_metrics": metrics,
        "full_project_histories": True,
        "include_replays": bool(spec.get("include_replays", False)),
        "excluded": [
            "checkpoints and model binaries", "secrets and credentials",
            "symlinks and non-regular files",
            "remote worker files not present in the trusted local artifact root",
        ],
        "missing_artifact_job_ids": [],
        "entries": [],
    }
    parts: list[dict] = []
    created: list[Path] = []
    archive = None
    archive_file: Path | None = None
    consumed = 0
    part_entries: list[dict] = []

    def start_part() -> None:
        nonlocal archive, archive_file, consumed, part_entries
        archive_file = output.with_name(
            f"{output.stem}-part-{len(parts) + 1:04d}.zip"
        )
        archive = zipfile.ZipFile(
            archive_file, "x", compression=zipfile.ZIP_DEFLATED,
            compresslevel=1, allowZip64=True
        )
        created.append(archive_file)
        consumed = 0
        part_entries = []

    def end_part() -> None:
        nonlocal archive
        if archive is None:
            return
        archive.writestr(
            "part-manifest.json",
            json.dumps({
                "project_id": project, "part": len(parts) + 1,
                "entries": part_entries,
            }, indent=2) + "\n",
        )
        archive.close()
        archive = None
        size = archive_file.stat().st_size
        if size > PART_LIMIT:
            raise ValueError("Generated part exceeds 500,000,000 bytes")
        parts.append({"name": archive_file.name, "bytes": size})

    def write_chunk(name: str, data: bytes, source_bytes: int, index: int) -> None:
        nonlocal consumed
        if archive is None:
            start_part()
        if consumed + len(data) + 5_000_000 > TARGET_BYTES:
            end_part()
            start_part()
        # Large input files are independently segmented, with their position
        # and source length retained so they can be reconstructed in order.
        target = name if source_bytes <= CHUNK_BYTES else f"{name}.part{index:05d}"
        with archive.open(target, "w", force_zip64=True) as handle:
            handle.write(data)
        item = {
            "name": target, "source": name, "segment": index,
            "original_file_bytes": source_bytes, "uncompressed_bytes": len(data),
        }
        part_entries.append(item)
        manifest["entries"].append({
            "part": len(parts) + 1, **item,
        })
        consumed += len(data) + len(target) + 1024

    def add_bytes(name: str, body: bytes) -> None:
        if not body:
            write_chunk(name, b"", 0, 1)
            return
        for i, offset in enumerate(range(0, len(body), CHUNK_BYTES), 1):
            write_chunk(name, body[offset:offset + CHUNK_BYTES], len(body), i)

    def add_file(source: Path, name: str) -> None:
        size = source.stat().st_size
        with source.open("rb") as handle:
            index = 0
            remaining = size
            while remaining:
                block = handle.read(min(CHUNK_BYTES, remaining))
                if not block:
                    raise OSError("Telemetry file shortened during export: " + name)
                index += 1
                write_chunk(name, block, size, index)
                remaining -= len(block)
            if index == 0:
                write_chunk(name, b"", 0, 1)

    try:
        start_part()
        metadata = {
            "schema_version": 1, "project_id": project,
            "jobs": jobs, "selected_metrics": metrics,
        }
        add_bytes("hub-jobs.json", json.dumps(metadata, indent=2).encode("utf-8"))
        add_bytes(
            "hub-comparison-history.json",
            json.dumps(spec.get("progress", {}), indent=2).encode("utf-8"),
        )
        found_by_job = {job["id"]: 0 for job in jobs}
        def scan_directory(folder: Path, name_prefix: str, job_id: str) -> None:
            for dirpath, dirs, files in os.walk(folder, followlinks=False):
                current = Path(dirpath)
                dirs[:] = sorted(d for d in dirs if d.lower() not in DENIED_PARTS
                    and (bool(spec.get("include_replays", False)) or d != "replays")
                    and not (current / d).is_symlink())
                for filename in sorted(files):
                    source = current / filename
                    if not safe_source(folder, source, bool(spec.get("include_replays", False))):
                        continue
                    add_file(source, name_prefix + "/" +
                             source.relative_to(folder).as_posix())
                    found_by_job[job_id] += 1
                    if len(manifest["entries"]) > MAX_SOURCE_FILES:
                        raise ValueError("Too many export entries")

        for job in jobs:
            job_id = job["id"]
            folder = root / job_id
            if not folder.is_symlink() and folder.is_dir() and folder.resolve() == folder.absolute():
                scan_directory(folder, f"jobs/{job_id}/artifacts", job_id)

        # These roots are composed exclusively by the trusted local Bees
        # coordinator from known run IDs, never supplied by the browser.
        for entry in additional:
            source = Path(entry["path"])
            job_id = entry["job_id"]
            prefix = f"jobs/{job_id}/external/{entry['label']}"
            if source.is_symlink() or not source.exists():
                continue
            if source.is_dir() and source.resolve() == source.absolute():
                scan_directory(source, prefix, job_id)
            elif source.is_file() and safe_source(source.parent, source, False):
                add_file(source, prefix + "/" + source.name)
                found_by_job[job_id] += 1

        manifest["missing_artifact_job_ids"] = [
            job_id for job_id, found in found_by_job.items() if not found
        ]
        add_bytes("export-manifest.json", json.dumps(manifest, indent=2).encode("utf-8"))
        end_part()
        return {"project_id": project, "parts": parts,
                "missing_artifact_job_ids": manifest["missing_artifact_job_ids"],
                "entries": len(manifest["entries"])}
    except BaseException:
        if archive is not None:
            archive.close()
        for part in created:
            part.unlink(missing_ok=True)
        raise


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--spec", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    try:
        spec = json.loads(args.spec.read_text(encoding="utf-8"))
        print(json.dumps(export(spec, args.output)), flush=True)
    except (KeyError, TypeError, ValueError, OSError, zipfile.BadZipFile) as exc:
        print(f"Export failed: {exc}", file=sys.stderr)
        raise SystemExit(1)


if __name__ == "__main__":
    main()
