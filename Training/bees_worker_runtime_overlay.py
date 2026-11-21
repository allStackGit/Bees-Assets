"""Package only remote worker code changes over the pinned learner runtime.

The canonical learner release, Unity build and trainer configuration stay untouched.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import tempfile
import zipfile

from bees_release_runtime import (
    MANIFEST_NAME, VERSION_NAME, _canonical_json, _runtime_version,
    _zip_info, verify_runtime,
)

# These modules run exclusively in remote-worker processes. All learner,
# policy, WAN protocol and training configuration payloads remain pinned.
WORKER_FILES = (
    "bees_managed_remote_worker.py",
    "bees_training_worker_agent.py",
)


def package_worker_overlay(
    pinned_archive: Path, source_root: Path, output: Path,
    pinned_sha256: str, pinned_version: str, build_id: str, source_commit: str,
) -> dict:
    original = verify_runtime(
        pinned_archive, expected_sha256=pinned_sha256,
        expected_version=pinned_version, expected_build_id=build_id,
    )
    with zipfile.ZipFile(pinned_archive, "r") as source_zip:
        original_manifest = json.loads(source_zip.read(MANIFEST_NAME))
        payloads = {
            entry["path"]: source_zip.read(entry["path"])
            for entry in original_manifest["files"]
        }
    for name in WORKER_FILES:
        file = source_root / name
        if name not in payloads or not file.is_file() or file.is_symlink():
            raise ValueError("Missing trusted worker source: " + name)
        payload = file.read_bytes()
        try:
            compile(payload, str(file), "exec")
        except SyntaxError as error:
            raise ValueError("Updated worker source has invalid Python syntax: " + name) from error
        payloads[name] = payload

    entries = [
        {"path": name, "size": len(data), "sha256": hashlib.sha256(data).hexdigest()}
        for name, data in sorted(payloads.items())
    ]
    version = _runtime_version(entries)
    manifest = {
        "schema_version": original_manifest["schema_version"],
        "build_id": build_id,
        "source_commit": source_commit,
        "runtime_version": version,
        "files": entries,
    }
    output.parent.mkdir(parents=True, exist_ok=True)
    fd, name = tempfile.mkstemp(prefix="worker-overlay-", suffix=".tmp", dir=output.parent)
    os.close(fd)
    temporary = Path(name)
    try:
        with zipfile.ZipFile(temporary, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
            for filename, data in sorted(payloads.items()):
                archive.writestr(_zip_info(filename), data)
            archive.writestr(_zip_info(VERSION_NAME), (version + "\n").encode("ascii"))
            archive.writestr(_zip_info(MANIFEST_NAME), _canonical_json(manifest) + b"\n")
        os.replace(temporary, output)
    finally:
        temporary.unlink(missing_ok=True)
    result = verify_runtime(output, expected_version=version, expected_build_id=build_id)
    result["original_runtime_version"] = original["runtime_version"]
    result["worker_files"] = list(WORKER_FILES)
    return result


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--pinned", required=True, type=Path)
    parser.add_argument("--pinned-sha256", required=True)
    parser.add_argument("--pinned-version", required=True)
    parser.add_argument("--training-root", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--build-id", required=True)
    parser.add_argument("--source-commit", required=True)
    args = parser.parse_args()
    try:
        result = package_worker_overlay(
            args.pinned, args.training_root, args.output,
            args.pinned_sha256, args.pinned_version, args.build_id,
            args.source_commit,
        )
    except (OSError, ValueError, zipfile.BadZipFile) as error:
        parser.exit(2, "worker runtime package failed: " + str(error) + "\n")
    print(json.dumps(result, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
