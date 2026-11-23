#!/usr/bin/env python3
"""Transfer the Unity project's non-Git Packages/ProjectSettings to Halifax.

Assets are supplied by the Bees-Assets Git checkout and are never copied here.
This transfer does not read runtime, training, secrets, Library or cache roots.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import shutil
import stat
import sys
import uuid
import zipfile

VERSION = 1
ROOTS = ("Packages", "ProjectSettings")
MANIFEST = "unity-project-metadata.json"
MAX_FILES = 10000
MAX_TOTAL = 512 * 1024 * 1024
MAX_FILE = 128 * 1024 * 1024


def _project_root(value: str) -> Path:
    root = Path(value).expanduser().absolute()
    assets = root / "Assets" / "Training" / "bees_unity_project_metadata.py"
    if not assets.is_file() or assets.is_symlink():
        raise ValueError("Unity root must contain a Bees-Assets checkout at Assets/")
    if root.is_symlink():
        raise ValueError("Unity project root must not be a symbolic link")
    return root


def _relative(name: str) -> Path:
    candidate = PurePosixPath(name)
    if (
        not name or name.startswith("/") or "\\" in name or ":" in name
        or "\x00" in name or ".." in candidate.parts
        or not candidate.parts or candidate.parts[0] not in ROOTS
        or name.endswith("/")
    ):
        raise ValueError("Unsafe Unity metadata path: " + repr(name))
    return Path(*candidate.parts)


def _source_files(root: Path) -> list[tuple[str, Path]]:
    result = []
    for name in ROOTS:
        directory = root / name
        if not directory.is_dir() or directory.is_symlink():
            raise ValueError(f"Unity project is missing a real {name}/ directory")
        for base, dirs, files in os.walk(directory, followlinks=False):
            for part in [*dirs, *files]:
                child = Path(base) / part
                if child.is_symlink():
                    raise ValueError("Refusing symlink in Unity project metadata: " + str(child))
            for filename in files:
                item = Path(base) / filename
                if not item.is_file():
                    raise ValueError("Refusing special file in project metadata: " + str(item))
                relative = item.relative_to(root).as_posix()
                _relative(relative)
                result.append((relative, item))
    result.sort(key=lambda x: x[0])
    if not (root / "Packages" / "manifest.json").is_file() or not (
        root / "ProjectSettings" / "ProjectVersion.txt"
    ).is_file():
        raise ValueError("Unity package manifest and project version are both required")
    if len(result) > MAX_FILES:
        raise ValueError("Unity project metadata contains too many files")
    return result


def export_project(root: Path, output: Path) -> None:
    files = _source_files(root)
    output = output.expanduser().absolute()
    if output.is_symlink():
        raise ValueError("Output cannot be a symlink")
    if output.is_relative_to(root / "Packages") or output.is_relative_to(
        root / "ProjectSettings"
    ):
        raise ValueError("Metadata archive must be stored outside the metadata folders")
    entries = []
    total = 0
    archive_created = False
    try:
        with zipfile.ZipFile(
            output, "x", compression=zipfile.ZIP_DEFLATED,
            compresslevel=6, allowZip64=True,
        ) as bundle:
            archive_created = True
            for relative, source in files:
                size = source.stat().st_size
                if size > MAX_FILE or total + size > MAX_TOTAL:
                    raise ValueError("Unity project metadata exceeds size limits")
                value = source.read_bytes()
                if len(value) != size:
                    raise ValueError("Unity metadata file changed while packaging: " + relative)
                total += size
                entries.append({
                    "path": relative, "bytes": size,
                    "sha256": hashlib.sha256(value).hexdigest(),
                })
                bundle.writestr(relative, value)
            manifest = {
                "schema_version": VERSION,
                "kind": "bees-unity-project-metadata",
                "unity_version": (
                    root / "ProjectSettings" / "ProjectVersion.txt"
                ).read_text(encoding="utf-8").strip(),
                "files": entries,
            }
            bundle.writestr(
                MANIFEST, json.dumps(manifest, sort_keys=True, separators=(",", ":")),
            )
    except BaseException:
        if archive_created:
            output.unlink(missing_ok=True)
        raise
    print(f"Exported {len(entries)} metadata files ({total} bytes): {output}")


def import_project(root: Path, archive: Path) -> None:
    archive = archive.expanduser().absolute()
    if not archive.is_file() or archive.is_symlink():
        raise ValueError("Metadata archive must be an existing regular file")
    root.mkdir(parents=True, exist_ok=True)
    for name in ROOTS:
        if (root / name).exists() or (root / name).is_symlink():
            raise ValueError(f"Refusing to overwrite existing Unity {name}/; inspect it first")
    staging = root / (".unity-metadata-" + uuid.uuid4().hex)
    staging.mkdir(mode=0o700)
    promoted = []
    rollback_errors = []
    try:
        with zipfile.ZipFile(archive) as bundle:
            members = bundle.infolist()
            if len(members) > MAX_FILES + 1:
                raise ValueError("Too many archive members")
            names = [member.filename for member in members]
            if len(names) != len(set(names)) or names.count(MANIFEST) != 1:
                raise ValueError("Archive members are duplicated or manifest is absent")
            if bundle.getinfo(MANIFEST).file_size > 8 * 1024 * 1024:
                raise ValueError("Manifest exceeds size limit")
            manifest_bytes = bundle.read(MANIFEST)
            manifest = json.loads(manifest_bytes)
            if not isinstance(manifest, dict) or manifest.get("schema_version") != VERSION or (
                manifest.get("kind") != "bees-unity-project-metadata"
            ):
                raise ValueError("Unsupported Unity metadata manifest")
            entries = manifest.get("files")
            if not isinstance(entries, list) or len(entries) > MAX_FILES:
                raise ValueError("Invalid metadata file list")
            expected = {}
            for entry in entries:
                if not isinstance(entry, dict):
                    raise ValueError("Invalid metadata manifest record")
                filename = entry.get("path")
                if not isinstance(filename, str):
                    raise ValueError("Invalid metadata file name")
                name = _relative(filename)
                if filename in expected:
                    raise ValueError("Duplicate metadata path")
                byte_count = entry.get("bytes")
                digest = entry.get("sha256")
                if type(byte_count) is not int or byte_count < 0 or byte_count > MAX_FILE or (
                    not isinstance(digest, str) or len(digest) != 64 or
                    any(letter not in "0123456789abcdef" for letter in digest)
                ):
                    raise ValueError("Invalid metadata size or digest")
                expected[filename] = (name, byte_count, digest)
            if set(names) != set(expected) | {MANIFEST}:
                raise ValueError("Archive content differs from declared metadata manifest")
            total = 0
            for member in members:
                if member.filename == MANIFEST:
                    continue
                name, size, digest = expected[member.filename]
                if member.is_dir() or stat.S_ISLNK(member.external_attr >> 16):
                    raise ValueError("Archive contains a directory or symbolic link entry")
                if member.file_size != size:
                    raise ValueError("Archive file size disagrees with metadata")
                if member.compress_size > MAX_FILE:
                    raise ValueError("Archive member has unexpected compressed size")
                total += size
                if total > MAX_TOTAL:
                    raise ValueError("Archive exceeds metadata size limit")
                target = staging / name
                target.parent.mkdir(parents=True, exist_ok=True)
                with bundle.open(member) as source, target.open("xb") as destination:
                    hasher = hashlib.sha256()
                    extracted = 0
                    while True:
                        block = source.read(1 << 20)
                        if not block:
                            break
                        extracted += len(block)
                        if extracted > size:
                            raise ValueError("Archive file expanded beyond declared size")
                        hasher.update(block)
                        destination.write(block)
                if extracted != size or hasher.hexdigest() != digest:
                    raise ValueError("Archive metadata checksum mismatch")
            for required in (
                staging / "Packages" / "manifest.json",
                staging / "ProjectSettings" / "ProjectVersion.txt",
            ):
                if not required.is_file():
                    raise ValueError("Archive missing required Unity metadata file")
        for name in ROOTS:
            os.replace(staging / name, root / name)
            promoted.append(name)
        print(f"Imported {len(expected)} metadata files into {root}")
    except BaseException:
        for name in reversed(promoted):
            try:
                os.replace(root / name, staging / name)
            except OSError as error:
                rollback_errors.append(f"{name}: {error}")
        if rollback_errors:
            raise RuntimeError(
                "Unity metadata promotion failed and rollback was incomplete. "
                f"Inspect the unchanged/new folders under {root} and preserved "
                f"staging directory {staging}: {'; '.join(rollback_errors)}"
            )
        raise
    finally:
        if not rollback_errors:
            shutil.rmtree(staging, ignore_errors=True)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    action = parser.add_subparsers(dest="command", required=True)
    for name, arg in (("export", "--output"), ("import", "--bundle")):
        command = action.add_parser(name)
        command.add_argument("--unity-root", required=True)
        command.add_argument(arg, required=True)
    options = parser.parse_args()
    root = _project_root(options.unity_root)
    if options.command == "export":
        export_project(root, Path(options.output))
    else:
        import_project(root, Path(options.bundle))


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, zipfile.BadZipFile) as error:
        sys.exit(f"Unity metadata transfer failed: {error}")
