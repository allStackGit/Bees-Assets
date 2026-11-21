"""Rebind an immutable, verified training runtime to a new Unity build.

The file payloads and runtime version stay identical. Only release build/source
metadata and archive SHA change. No working-tree learner code is packaged.
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


def rebase_runtime(
    source: Path, output: Path, old_sha: str, old_build_id: str,
    new_build_id: str, source_commit: str,
) -> dict:
    verified = verify_runtime(
        source, expected_sha256=old_sha, expected_build_id=old_build_id,
    )
    with zipfile.ZipFile(source, "r") as archive:
        prior = json.loads(archive.read(MANIFEST_NAME))
        files = [(entry["path"], archive.read(entry["path"])) for entry in prior["files"]]
    entries = [
        {"path": name, "size": len(content), "sha256": hashlib.sha256(content).hexdigest()}
        for name, content in files
    ]
    version = _runtime_version(entries)
    if version != verified["runtime_version"]:
        raise ValueError("Rebased training runtime changed its file identity")
    manifest = {
        "schema_version": prior["schema_version"], "build_id": new_build_id,
        "source_commit": source_commit, "runtime_version": version, "files": entries,
    }
    output.parent.mkdir(parents=True, exist_ok=True)
    fd, temp_name = tempfile.mkstemp(
        prefix="bees-runtime-rebase-", suffix=".tmp", dir=output.parent,
    )
    os.close(fd)
    temp = Path(temp_name)
    try:
        with zipfile.ZipFile(temp, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
            for name, content in files:
                archive.writestr(_zip_info(name), content)
            archive.writestr(_zip_info(VERSION_NAME), (version + "\n").encode("ascii"))
            archive.writestr(_zip_info(MANIFEST_NAME), _canonical_json(manifest) + b"\n")
        os.replace(temp, output)
    finally:
        temp.unlink(missing_ok=True)
    result = verify_runtime(
        output, expected_version=verified["runtime_version"],
        expected_build_id=new_build_id,
    )
    result["reused_from_build"] = old_build_id
    return result


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", required=True, type=Path)
    parser.add_argument("--source-sha256", required=True)
    parser.add_argument("--old-build-id", required=True)
    parser.add_argument("--new-build-id", required=True)
    parser.add_argument("--source-commit", required=True)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    try:
        result = rebase_runtime(
            args.source, args.output, args.source_sha256,
            args.old_build_id, args.new_build_id, args.source_commit,
        )
    except (OSError, ValueError, zipfile.BadZipFile) as error:
        parser.exit(2, "Unity maintenance runtime rebase failed: " + str(error) + "\n")
    print(json.dumps(result, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
