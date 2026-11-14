"""Read-only, project-neutral export of locally available experiment job telemetry.

The loopback coordinator pins all job IDs and the artifact root in a private
specification. This tool never scans checkouts, executes training or copies
model/checkpoint binaries. Every ZIP part is independently readable and <=500MB.
Remote-only artifacts remain explicitly absent from the manifest.
"""
from __future__ import annotations

import argparse
import csv
import hashlib
import json
import os
import re
import stat
import sys
import tempfile
import zipfile
from datetime import datetime, timezone
from pathlib import Path

PART_LIMIT = 500_000_000
TARGET_BYTES = 450_000_000
CHUNK_BYTES = 24_000_000
UUID = re.compile(r"^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$")
PROJECT = re.compile(r"^[a-z][a-z0-9_-]{0,47}$")
METRIC = re.compile(r"^[a-z][a-z0-9_]{0,63}$")
COLLECTOR_SOURCE = re.compile(r"^collector_([0-9a-f]{32})_(host|src|logs)$")
SUFFIXES = {".json", ".jsonl", ".ndjson", ".csv", ".tsv", ".txt", ".log"}
DENIED_PARTS = {
    ".git", "private", "secrets", "credentials", "keys", "checkpoints",
    "models", "opponents", "__pycache__", "build", "node_modules",
}
# The export walks trusted job artifact folders, not just known log files.
# Credential material can be hidden under an ordinary-looking child filename
# inside a sensitive subfolder, or use unseparated key names.
DENIED_NAMES = (
    "token", "secret", "password", "credential", "checkpoint", "rollout.key",
    "api_key", "api-key", "api.key", "apikey",
    "access_key", "access-key", "accesskey",
    "private_key", "private-key", "privatekey",
    "ssh_key", "ssh-key", "sshkey",
    "key_file", "key-file", "keyfile",
    "id_rsa", "id_ed25519", "service_account", "service-account",
    "connection_string", "connection-string", "connectionstring",
    "bearer", "authorization", "authentication",
)
MAX_SOURCE_FILES = 200_000


def forbidden_part(name: str) -> bool:
    lowered = name.lower()
    return lowered in DENIED_PARTS or any(term in lowered for term in DENIED_NAMES)


def safe_source(root: Path, source: Path, include_replays: bool) -> bool:
    rel = source.relative_to(root)
    if not rel.parts or any(forbidden_part(part) for part in rel.parts):
        return False
    if not include_replays and any(part.lower() == "replays"
                                   for part in (*root.parts, *rel.parts)):
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


class SelectedObservationRows:
    """Stream one captured native observation file into a selected-metric CSV."""

    def __init__(self, job_id: str, project: str, names: set[str] | None, writer) -> None:
        self.job_id = job_id
        self.project = project
        self.names = names
        self.writer = writer
        self.pending = b""
        self.rows = 0

    def _line(self, raw: bytes) -> None:
        if len(raw) > 65536:
            raise ValueError("Observation record exceeds 64 KiB")
        if not raw:
            return
        row = json.loads(raw)
        if row.get("job_id") != self.job_id or row.get("project_id") != self.project:
            raise ValueError("Observation archive lineage does not match the selected job")
        for origin, field in (("trainer", "metrics"), ("resource", "resource_metrics")):
            for name, measurement in (row.get(field) or {}).items():
                if self.names is not None and name not in self.names:
                    continue
                self.writer.writerow((
                    self.job_id, self.project, row.get("observed_at"),
                    row.get("source_modified_at"), row.get("experiment_id"),
                    row.get("segment_id"), row.get("steps_completed"),
                    row.get("observation_kind", "trainer_update"),
                    row.get("trainer_source_status"),
                    origin, name, measurement["value"], measurement["unit"],
                ))
                self.rows += 1

    def feed(self, block: bytes) -> None:
        lines = block.split(b"\n")
        if len(lines) == 1:
            self.pending += block
        else:
            self._line(self.pending + lines[0])
            for line in lines[1:-1]:
                self._line(line)
            self.pending = lines[-1]
        if len(self.pending) > 65536:
            raise ValueError("Observation line exceeds 64 KiB")

    def finish(self) -> bool:
        # A concurrently active writer might leave a trailing incomplete row.
        # Do not present it as a complete observation.
        return bool(self.pending)


def export(spec: dict, output: Path) -> dict:
    project = spec["project_id"]
    jobs = spec["jobs"]
    root = Path(spec["artifact_root"])
    missing_local_root = spec.get("local_artifact_root_missing", False)
    if type(missing_local_root) is not bool:
        raise ValueError("Invalid local artifact availability flag")
    if not PROJECT.fullmatch(project) or not isinstance(jobs, list) or not 1 <= len(jobs) <= 1000:
        raise ValueError("Invalid selected project or job count")
    if len({job["id"] for job in jobs}) != len(jobs) or any(
        not UUID.fullmatch(str(job["id"])) or job.get("project_id") != project
        for job in jobs
    ):
        raise ValueError("Invalid selected job identifiers")
    metrics = spec.get("metrics", [])
    if not isinstance(metrics, list) or len(metrics) > 256 or any(
        not isinstance(name, str) or not METRIC.fullmatch(name)
        for name in metrics
    ) or len(set(metrics)) != len(metrics):
        raise ValueError("Invalid metric selection")
    all_recorded_metrics = spec.get("all_recorded_metrics", False)
    if type(all_recorded_metrics) is not bool or (all_recorded_metrics and metrics):
        raise ValueError("Export all metrics must not include a separate metric selection")
    additional = spec.get("additional_sources", [])
    selected_ids = {job["id"] for job in jobs}
    mounted_ids = spec.get("mounted_artifact_job_ids", [])
    if not isinstance(mounted_ids, list) or len(mounted_ids) > len(jobs) or (
        any(not isinstance(job_id, str) or job_id not in selected_ids
            for job_id in mounted_ids)
    ) or len(set(mounted_ids)) != len(mounted_ids):
        raise ValueError("Invalid selected machine-mounted job IDs")
    mounted = set(mounted_ids)
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
    # Resolve collector identities from frozen Hub job provenance. The
    # selected learner owns each linked worker; a source label alone is not
    # sufficient authority to import another job's observations.
    provenance_jobs = (spec.get("resource_provenance") or {}).get("jobs", [])
    collector_pairs = {
        (parent["job_id"], child["job_id"])
        for parent in provenance_jobs for child in parent.get("collectors", [])
    }
    collector_sources: dict[str, str] = {}
    collector_log_sources: set[str] = set()
    for entry in additional:
        match = COLLECTOR_SOURCE.fullmatch(entry["label"])
        if not match:
            continue
        key, kind = match.groups()
        child_id = (key[:8] + "-" + key[8:12] + "-" + key[12:16] +
                    "-" + key[16:20] + "-" + key[20:])
        expected = (child_id + "-resources.jsonl" if kind == "host" else
                    child_id + ".jsonl" if kind == "src" else child_id)
        if (project != "tinybrains" or
            (entry["job_id"], child_id) not in collector_pairs or
            Path(entry["path"]).name != expected):
            raise ValueError("Collector artifact source is not a managed child")
        if kind == "logs":
            collector_log_sources.add(entry["label"])
        else:
            collector_sources[entry["label"]] = child_id
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
        "all_recorded_metrics": all_recorded_metrics,
        "full_project_histories": "available local files only",
        "local_artifact_root_missing": missing_local_root,
        "include_replays": bool(spec.get("include_replays", False)),
        "replay_files_included": 0,
        "excluded": [
            "checkpoints and model binaries", "secrets and credentials",
            "symlinks and non-regular files",
            "remote worker files not present in the trusted local artifact root",
        ],
        "missing_artifact_job_ids": [],
        "missing_sources": [],
        "entries_recorded_in_each_part_manifest": True,
        "file_capture_scope": "Each source is captured through its size when opened; later appended bytes are excluded.",
        "files_extended_during_copy_count": 0,
        "files_extended_during_copy": [],
    }
    entry_count = 0
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
        nonlocal consumed, entry_count
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
            "sha256": hashlib.sha256(data).hexdigest(),
        }
        part_entries.append(item)
        entry_count += 1
        consumed += len(data) + len(target) + 1024

    def add_bytes(name: str, body: bytes) -> None:
        if not body:
            write_chunk(name, b"", 0, 1)
            return
        for i, offset in enumerate(range(0, len(body), CHUNK_BYTES), 1):
            write_chunk(name, body[offset:offset + CHUNK_BYTES], len(body), i)

    def add_file(source: Path, name: str, observer=None) -> None:
        # Keep one opened file identity throughout the read. A job may append
        # during export; capture its initial prefix and disclose that omission
        # rather than silently labelling the archive a complete final history.
        flags = os.O_RDONLY | getattr(os, "O_BINARY", 0) | getattr(os, "O_NOFOLLOW", 0)
        with os.fdopen(os.open(source, flags), "rb") as handle:
            before = os.fstat(handle.fileno())
            if not stat.S_ISREG(before.st_mode) or before.st_nlink != 1:
                raise ValueError("Telemetry source is not a safe regular file: " + name)
            current = source.lstat()
            if not stat.S_ISREG(current.st_mode) or (
                current.st_dev, current.st_ino
            ) != (before.st_dev, before.st_ino):
                raise ValueError("Telemetry source changed between validation and open: " + name)
            size = before.st_size
            index = 0
            remaining = size
            while remaining:
                block = handle.read(min(CHUNK_BYTES, remaining))
                if not block:
                    raise OSError("Telemetry file shortened during export: " + name)
                index += 1
                if observer is not None:
                    observer.feed(block)
                write_chunk(name, block, size, index)
                remaining -= len(block)
            if index == 0:
                write_chunk(name, b"", 0, 1)
            after = os.fstat(handle.fileno())
            if after.st_size < size or (
                after.st_size == size and after.st_mtime_ns != before.st_mtime_ns
            ):
                raise OSError("Telemetry source was rewritten during export: " + name)
            if after.st_size > size:
                manifest["files_extended_during_copy_count"] += 1
                if len(manifest["files_extended_during_copy"]) < 100:
                    manifest["files_extended_during_copy"].append({
                        "source": name, "captured_bytes": size,
                        "size_after_copy": after.st_size,
                    })


    def add_selected_metric_history() -> int:
        # This derived CSV is limited to Hub's bounded comparison observations.
        # The complete project-owned histories are exported separately below.
        selected = set(metrics)
        temp_path: Path | None = None
        rows = 0
        try:
            with tempfile.NamedTemporaryFile(
                mode="w", encoding="utf-8", newline="", dir=output.parent,
                prefix="hub-selected-metrics-", suffix=".csv", delete=False,
            ) as stream:
                temp_path = Path(stream.name)
                writer = csv.writer(stream)
                writer.writerow((
                    "job_id", "observed_at", "source_modified_at",
                    "experiment_id", "segment_id", "steps_completed",
                    "break_before", "metric", "value", "unit",
                ))
                for entry in spec.get("progress", {}).get("samples", []):
                    if entry.get("job_id") not in selected_ids:
                        continue
                    for point in entry.get("points", []):
                        for name, measurement in (point.get("metrics") or {}).items():
                            if not all_recorded_metrics and name not in selected:
                                continue
                            writer.writerow((
                                entry["job_id"], point.get("observed_at"),
                                point.get("source_modified_at"),
                                point.get("experiment_id"), point.get("segment_id"),
                                point.get("steps_completed"),
                                point.get("break_before", False),
                                name, measurement["value"], measurement["unit"],
                            ))
                            rows += 1
            add_file(temp_path, "hub-selected-metric-samples.csv")
            return rows
        finally:
            if temp_path is not None:
                temp_path.unlink(missing_ok=True)

    try:
        start_part()
        metadata = {
            "schema_version": 1, "project_id": project,
            "jobs": jobs, "selected_metrics": metrics,
            "all_recorded_metrics": all_recorded_metrics,
        }
        add_bytes("hub-jobs.json", json.dumps(metadata, indent=2).encode("utf-8"))
        provenance = spec.get("resource_provenance")
        if provenance is not None:
            if not isinstance(provenance, dict) or provenance.get("schema_version") != 1 or (
                not isinstance(provenance.get("jobs"), list) or
                len(provenance["jobs"]) != len(jobs)
            ):
                raise ValueError("Invalid immutable resource provenance metadata")
            add_bytes("hub-resource-claims.json",
                      json.dumps(provenance, indent=2).encode("utf-8"))
        manifest["resource_provenance_included"] = provenance is not None
        add_bytes(
            "hub-comparison-history.json",
            json.dumps(spec.get("progress", {}), indent=2).encode("utf-8"),
        )
        manifest["selected_metric_sample_rows"] = (
            add_selected_metric_history() if metrics or all_recorded_metrics else 0
        )
        manifest["selected_metric_sample_resolution"] = (
            "bounded Hub chart history; full project logs remain separate"
        )
        observation_rows = 0
        observation_sources = 0
        partial_observation_files = 0
        selected_observation_path = None
        selected_observation_stream = None
        selected_observation_writer = None
        if (metrics or all_recorded_metrics) and any(
                source["label"] in ("hub-observations", "hub-host-observations")
                or source["label"] in collector_sources
                for source in additional):
            selected_observation_stream = tempfile.NamedTemporaryFile(
                mode="w", encoding="utf-8", newline="", dir=output.parent,
                prefix="hub-selected-observations-", suffix=".csv", delete=False,
            )
            selected_observation_path = Path(selected_observation_stream.name)
            selected_observation_writer = csv.writer(selected_observation_stream)
            selected_observation_writer.writerow((
                "job_id", "project_id", "observed_at", "source_modified_at",
                "experiment_id", "segment_id", "steps_completed",
                "observation_kind", "trainer_source_status", "origin",
                "metric", "value", "unit",
            ))
        found_by_job = {job["id"]: 0 for job in jobs}
        # Hub observations prove that telemetry reached the coordinator,
        # not that the remote trainer's original histories were collected.
        trainer_files_by_job = {job["id"]: 0 for job in jobs}
        missing_sources: list[dict] = []
        # A distributed collector's original logs are still unavailable when
        # no machine mount was configured. Report that explicitly rather than
        # allowing its Hub observations to make a learner export appear complete.
        for learner_id, child_id in sorted(collector_pairs):
            label = "collector_" + child_id.replace("-", "") + "_logs"
            if label not in collector_log_sources:
                missing_sources.append({
                    "job_id": learner_id, "label": label,
                    "reason": "mount_not_configured",
                })
        def scan_directory(folder: Path, name_prefix: str, job_id: str,
                           counts_as_trainer: bool = True) -> None:
            if not spec.get("include_replays", False) and any(
                part.lower() == "replays" for part in folder.parts
            ):
                return
            for dirpath, dirs, files in os.walk(folder, followlinks=False):
                current = Path(dirpath)
                # Do not traverse sensitive folders at all; the same rule
                # also rejects individual files and nested credential paths.
                dirs[:] = sorted(d for d in dirs if not forbidden_part(d)
                    and (bool(spec.get("include_replays", False)) or d.lower() != "replays")
                    and not (current / d).is_symlink())
                for filename in sorted(files):
                    source = current / filename
                    if not safe_source(folder, source, bool(spec.get("include_replays", False))):
                        continue
                    add_file(source, name_prefix + "/" +
                             source.relative_to(folder).as_posix())
                    if any(part.lower() == "replays" for part in (
                            *folder.parts, *source.relative_to(folder).parts)):
                        manifest["replay_files_included"] += 1
                    found_by_job[job_id] += 1
                    if counts_as_trainer:
                        trainer_files_by_job[job_id] += 1
                    if entry_count > MAX_SOURCE_FILES:
                        raise ValueError("Too many export entries")

        # Legacy Bees run artifacts live in separately approved named sources,
        # not a job-ID directory under the coordinator data root.
        scanned_jobs = [] if project == "bees" or missing_local_root else [
            job for job in jobs if job["id"] not in mounted
        ]
        if missing_local_root:
            missing_sources.extend({
                "job_id": job["id"], "label": "artifacts",
                "reason": "not_available",
            } for job in jobs if job["id"] not in mounted)
        for job in scanned_jobs:
            job_id = job["id"]
            folder = root / job_id
            if not folder.is_symlink() and folder.is_dir() and folder.resolve() == folder.absolute():
                scan_directory(folder, f"jobs/{job_id}/artifacts", job_id)
                if found_by_job[job_id] == 0:
                    missing_sources.append({"job_id": job_id, "label": "artifacts",
                                            "reason": "no_approved_logs"})
            else:
                missing_sources.append({"job_id": job_id, "label": "artifacts",
                                        "reason": "not_available"})

        # These roots are composed exclusively by the trusted local Bees
        # coordinator from known run IDs, never supplied by the browser.
        for entry in additional:
            source = Path(entry["path"])
            job_id = entry["job_id"]
            prefix = f"jobs/{job_id}/external/{entry['label']}"
            count_before = found_by_job[job_id]
            if source.is_symlink() or not source.exists():
                reason = "not_available"
            elif source.is_dir() and source.resolve() == source.absolute():
                scan_directory(source, prefix, job_id,
                               entry["label"] not in collector_log_sources)
                reason = "no_approved_logs"
            elif source.is_file() and safe_source(source.parent, source, False):
                child_id = collector_sources.get(entry["label"])
                is_observation = entry["label"] in (
                    "hub-observations", "hub-host-observations"
                ) or child_id is not None
                observer = (SelectedObservationRows(
                    child_id or job_id,
                    "tinybrains_rollout" if child_id else project,
                    None if all_recorded_metrics else set(metrics),
                    selected_observation_writer)
                    if is_observation and (metrics or all_recorded_metrics)
                    else None)
                add_file(source, prefix + "/" + source.name, observer)
                if observer is not None:
                    observation_sources += 1
                    observation_rows += observer.rows
                    partial_observation_files += int(observer.finish())
                found_by_job[job_id] += 1
                if not is_observation:
                    trainer_files_by_job[job_id] += 1
                reason = None
            else:
                reason = "no_approved_logs"
            if found_by_job[job_id] == count_before:
                missing_sources.append({"job_id": job_id,
                                        "label": entry["label"], "reason": reason})

        if selected_observation_stream is not None:
            selected_observation_stream.close()
            selected_observation_stream = None
            add_file(selected_observation_path, "hub-selected-observation-metrics.csv")
        manifest["selected_observation_metric_rows"] = observation_rows
        manifest["selected_observation_source_files"] = observation_sources
        manifest["incomplete_observation_lines"] = partial_observation_files
        manifest["selected_observation_metric_resolution"] = (
            "every distinct native progress version received by the Hub; "
            "trainer-owned histories may contain additional updates"
        )
        manifest["missing_sources"] = missing_sources
        manifest["missing_artifact_job_ids"] = [
            job_id for job_id, found in trainer_files_by_job.items() if not found
        ]
        manifest["exported_segments"] = entry_count
        add_bytes("export-manifest.json", json.dumps(manifest, indent=2).encode("utf-8"))
        end_part()
        return {"project_id": project, "parts": parts,
                "replay_files_included": manifest["replay_files_included"],
                "missing_artifact_job_ids": manifest["missing_artifact_job_ids"],
                "missing_sources": missing_sources[:64],
                "missing_source_count": len(missing_sources),
                "files_extended_during_copy_count": manifest["files_extended_during_copy_count"],
                "entries": entry_count}
    except BaseException:
        if archive is not None:
            archive.close()
        for part in created:
            part.unlink(missing_ok=True)
        raise
    finally:
        if 'selected_observation_stream' in locals() and selected_observation_stream is not None:
            selected_observation_stream.close()
        if 'selected_observation_path' in locals() and selected_observation_path is not None:
            selected_observation_path.unlink(missing_ok=True)


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
