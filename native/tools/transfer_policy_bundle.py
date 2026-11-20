#!/usr/bin/env python3
"""Transfer an immutable native rollout bundle between machines without shared storage.

The exported tar contains only an approved collection receipt and its captured
files. Imports verify the work order, every artifact digest, and all archive
paths before atomically publishing the existing collector inbox layout. This
is transport, not a second collector, training protocol or job scheduler.
"""

import argparse
import hashlib
import io
import json
import os
from pathlib import Path, PurePosixPath
import re
import tarfile
import time
from tempfile import TemporaryDirectory, NamedTemporaryFile

from native_distributed_collection import verify_bundle
from native_training_artifacts import file_sha256, sync_directory


DIGEST = re.compile(r"[0-9a-f]{64}\Z")
BUNDLE_NAME = re.compile(r"[A-Za-z0-9_-]{1,96}\Z")
MAX_RECEIPT_BYTES = 2 * 1024 * 1024
CHUNK = 1024 * 1024
UPLOAD_RECEIPT = ".native-upload-receipt.json"


def checked_order(path, collector_id):
    orders = json.loads(Path(path).read_text(encoding="utf-8"))
    if (not isinstance(orders, dict) or
            orders.get("kind") != "native_distributed_collection_orders" or
            orders.get("schema_version") != 1 or
            not isinstance(orders.get("jobs"), list)):
        raise ValueError("expected issued native distributed collection orders")
    jobs = [job for job in orders["jobs"]
            if isinstance(job, dict) and job.get("id") == collector_id]
    if len(jobs) != 1:
        raise ValueError("collector is not uniquely present in the issued work order")
    job = jobs[0]
    name = job.get("bundle_directory")
    if not isinstance(name, str) or not BUNDLE_NAME.fullmatch(name):
        raise ValueError("collector bundle destination is not a safe directory name")
    return orders, job


def artifact_paths(receipt):
    """Only original capture/journal files may travel with this receipt."""
    captures = receipt.get("captures")
    if not isinstance(captures, list) or not captures or len(captures) > 256:
        raise ValueError("native bundle has an invalid capture list")
    files = {}
    for capture in captures:
        if not isinstance(capture, dict) or set(capture) != {"policy_capture", "notifications"}:
            raise ValueError("native bundle has incomplete capture/journal pairs")
        for record in capture.values():
            if not isinstance(record, dict):
                raise ValueError("invalid captured artifact")
            name, sha = record.get("path"), record.get("sha256")
            if not isinstance(name, str) or not isinstance(sha, str) or not DIGEST.fullmatch(sha):
                raise ValueError("captured artifact has an invalid name or digest")
            path = PurePosixPath(name)
            if (not path.parts or path.is_absolute() or
                    path.as_posix() != name or
                    any(part in (".", "..") for part in path.parts) or
                    "\\" in name or ":" in name or
                    name in ("rollout-bundle.json", UPLOAD_RECEIPT) or name in files):
                raise ValueError("captured artifact path is unsafe or duplicated")
            files[name] = sha
    return files


class DigestReader:
    """Hash the same bytes tarfile writes, with no second artifact read."""
    def __init__(self, stream):
        self.stream = stream
        self.digest = hashlib.sha256()
        self.bytes_read = 0

    def read(self, length=-1):
        data = self.stream.read(length)
        self.digest.update(data)
        self.bytes_read += len(data)
        return data


def archive_info(name, size):
    info = tarfile.TarInfo(name)
    info.size = size
    info.mode = 0o600
    info.mtime = 0
    return info


def export_bundle_archive(receipt_path, job, orders, archive_path):
    started = time.perf_counter()
    receipt_path = Path(receipt_path).resolve(strict=True)
    bundle_root = receipt_path.parent
    archive_path = Path(archive_path).absolute()
    archive_parent = archive_path.parent.resolve(strict=True)
    if not archive_parent.is_dir() or archive_path.exists() or archive_path.is_symlink():
        raise FileExistsError(f"archive output must be a new file in an existing directory: {archive_path}")
    verify_bundle(receipt_path, job, orders)
    raw = receipt_path.read_bytes()
    if len(raw) > MAX_RECEIPT_BYTES:
        raise ValueError("rollout bundle receipt exceeds transfer limit")
    files = artifact_paths(json.loads(raw))
    temporary = None
    transferred = 0
    try:
        with NamedTemporaryFile(mode="w+b", prefix=".native-bundle-", suffix=".partial",
                                dir=archive_parent, delete=False) as sink:
            temporary = Path(sink.name)
            with tarfile.open(fileobj=sink, mode="w", format=tarfile.PAX_FORMAT) as archive:
                archive.addfile(archive_info("rollout-bundle.json", len(raw)), io.BytesIO(raw))
                for name, expected in sorted(files.items()):
                    source = bundle_root.joinpath(*PurePosixPath(name).parts)
                    # Never follow a collector artifact through a symlink or
                    # outside the approved bundle root, on any platform.
                    if (source.is_symlink() or not source.is_file() or
                            not source.resolve(strict=True).is_relative_to(bundle_root)):
                        raise ValueError(f"collector artifact escaped its bundle: {name}")
                    size = source.stat().st_size
                    with source.open("rb") as stream:
                        reader = DigestReader(stream)
                        archive.addfile(archive_info(name, size), reader)
                        if reader.bytes_read != size or reader.digest.hexdigest() != expected:
                            raise ValueError(f"collector artifact changed before export: {name}")
                    transferred += size
            sink.flush()
            os.fsync(sink.fileno())
        # Hard-link publication is atomic and refuses to overwrite a file
        # published by another collector, unlike os.replace on POSIX.
        os.link(temporary, archive_path)
        sync_directory(archive_parent)
    finally:
        if temporary is not None:
            temporary.unlink(missing_ok=True)
    return {
        "archive": str(archive_path),
        "archive_sha256": file_sha256(archive_path),
        "capture_files": len(files),
        "capture_bytes": transferred,
        "archive_bytes": archive_path.stat().st_size,
        "archive_export_wall_seconds": time.perf_counter() - started,
    }


def import_bundle_archive(archive_path, job, orders, inbox, *,
                          archive_sha256=None, archive_bytes=None):
    # The receiver supplies the checksum of bytes it already verified. Store
    # its acknowledgement inside the atomically published bundle so a lost
    # HTTP response cannot force another rollout or duplicate learner input.
    if (archive_sha256 is None) != (archive_bytes is None):
        raise ValueError("native upload identity requires both checksum and size")
    if archive_sha256 is not None and (not isinstance(archive_sha256, str) or
            not DIGEST.fullmatch(archive_sha256) or
            type(archive_bytes) is not int or archive_bytes <= 0):
        raise ValueError("invalid native upload identity")
    started = time.perf_counter()
    archive_path = Path(archive_path).resolve(strict=True)
    inbox = Path(inbox).resolve(strict=True)
    if not inbox.is_dir():
        raise ValueError("native collector inbox must exist")
    destination = inbox / job["bundle_directory"]
    if destination.exists() or destination.is_symlink():
        raise FileExistsError(f"native collector bundle already published: {destination}")
    with TemporaryDirectory(prefix=".native-bundle-import-", dir=inbox) as scratch:
        stage = Path(scratch) / "bundle"
        stage.mkdir()
        expected = None
        received = set()
        transferred = 0
        with tarfile.open(archive_path, mode="r|") as archive:
            for item in archive:
                if not item.isfile():
                    raise ValueError("transfer archive contains a non-file member")
                if expected is None:
                    if item.name != "rollout-bundle.json" or not 0 < item.size <= MAX_RECEIPT_BYTES:
                        raise ValueError("transfer archive must start with a bounded rollout receipt")
                    source = archive.extractfile(item)
                    if source is None:
                        raise ValueError("missing archived rollout receipt")
                    raw = source.read(MAX_RECEIPT_BYTES + 1)
                    if len(raw) != item.size:
                        raise ValueError("truncated archived rollout receipt")
                    receipt = stage / "rollout-bundle.json"
                    with receipt.open("xb") as sink:
                        sink.write(raw)
                        sink.flush()
                        os.fsync(sink.fileno())
                    verify_bundle(receipt, job, orders)
                    expected = artifact_paths(json.loads(raw))
                    continue
                name = item.name
                if name not in expected or name in received:
                    raise ValueError(f"transfer archive contains unexpected or duplicate artifact: {name}")
                target = stage.joinpath(*PurePosixPath(name).parts)
                target.parent.mkdir(parents=True, exist_ok=True)
                source = archive.extractfile(item)
                if source is None:
                    raise ValueError(f"missing archived artifact: {name}")
                digest = hashlib.sha256()
                total = 0
                with target.open("xb") as sink:
                    while True:
                        block = source.read(CHUNK)
                        if not block:
                            break
                        sink.write(block)
                        digest.update(block)
                        total += len(block)
                    sink.flush()
                    os.fsync(sink.fileno())
                if total != item.size or digest.hexdigest() != expected[name]:
                    raise ValueError(f"captured artifact digest mismatch: {name}")
                if target.parent != stage:
                    sync_directory(target.parent)
                received.add(name)
                transferred += total
        if expected is None or received != set(expected):
            raise ValueError("transfer archive is missing required capture files")
        if archive_sha256 is not None:
            acknowledgement = {
                "kind": "native_bundle_upload_acknowledgement",
                "collector_id": job["id"],
                "archive_sha256": archive_sha256,
                "archive_bytes": archive_bytes,
            }
            with (stage / UPLOAD_RECEIPT).open("x", encoding="utf-8") as sink:
                json.dump(acknowledgement, sink, separators=(",", ":"), sort_keys=True)
                sink.flush()
                os.fsync(sink.fileno())
        sync_directory(stage)
        if destination.exists() or destination.is_symlink():
            raise FileExistsError(f"native collector bundle already published: {destination}")
        stage.rename(destination)
        sync_directory(inbox)
    return {
        "bundle": str(destination / "rollout-bundle.json"),
        "collector_id": job["id"],
        "capture_files": len(received),
        "capture_bytes": transferred,
        "archive_import_wall_seconds": time.perf_counter() - started,
    }


def completed_uploaded_bundle(inbox, job, orders):
    """Read a durable acknowledgement only for this round's verified bundle."""
    directory = Path(inbox).resolve(strict=True) / job["bundle_directory"]
    receipt = directory / "rollout-bundle.json"
    acknowledgement = directory / UPLOAD_RECEIPT
    if (directory.is_symlink() or not directory.is_dir() or
            receipt.is_symlink() or not receipt.is_file() or
            acknowledgement.is_symlink() or not acknowledgement.is_file()):
        raise LookupError("collector upload has no durable acknowledgement")
    if acknowledgement.stat().st_size > 4096:
        raise ValueError("collector upload acknowledgement is oversized")
    identity = json.loads(acknowledgement.read_text(encoding="utf-8"))
    if (not isinstance(identity, dict) or
            set(identity) != {"kind", "collector_id", "archive_sha256", "archive_bytes"} or
            identity["kind"] != "native_bundle_upload_acknowledgement" or
            identity["collector_id"] != job["id"] or
            not isinstance(identity["archive_sha256"], str) or
            not DIGEST.fullmatch(identity["archive_sha256"]) or
            type(identity["archive_bytes"]) is not int or identity["archive_bytes"] <= 0):
        raise ValueError("collector upload acknowledgement is invalid")
    # The file identities alone cannot authorize another training input:
    # revalidate the published capture receipt against the currently issued
    # work order before acknowledging a retry after a receiver restart.
    verify_bundle(receipt, job, orders)
    return {
        "kind": "native_bundle_received",
        "collector_id": job["id"],
        "archive_sha256": identity["archive_sha256"],
        "archive_bytes": identity["archive_bytes"],
        # Bind the authenticated archive to the learner's accepted result,
        # not just to its collection_id. A restarted collector must never
        # substitute another receipt for the samples already delivered.
        "bundle_sha256": file_sha256(receipt),
        "receipt": str(receipt),
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    for command in ("export", "import", "upload"):
        sub = commands.add_parser(command)
        sub.add_argument("--orders", type=Path, required=True)
        sub.add_argument("--collector-id", required=True)
        sub.add_argument("--archive", type=Path, required=True)
        if command == "export":
            sub.add_argument("--receipt", type=Path, required=True)
        elif command == "import":
            sub.add_argument("--inbox", type=Path, required=True)
        else:
            sub.add_argument("--url", required=True,
                             help="authenticated native learner bundle receiver")
    args = parser.parse_args()
    orders, job = checked_order(args.orders, args.collector_id)
    if args.command == "export":
        result = export_bundle_archive(args.receipt, job, orders, args.archive)
        kind = "native_rollout_archive_exported"
    elif args.command == "import":
        result = import_bundle_archive(args.archive, job, orders, args.inbox)
        kind = "native_rollout_archive_imported"
    else:
        # A Hub-managed collector can finish locally and independently
        # upload its immutable bundle without spawning another simulator.
        from native_bundle_receiver import upload_bundle_archive
        result = upload_bundle_archive(args.archive, job["id"], args.url)
        kind = "native_rollout_archive_uploaded"
    print(json.dumps({"kind": kind, **result}, sort_keys=True), flush=True)


if __name__ == "__main__":
    main()
