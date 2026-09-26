"""Build, verify, and install immutable Bees training-runtime release payloads.

A training release must run the exact Python/configuration bytes that were pinned with the Unity
artifacts. This helper owns that payload format so PowerShell only orchestrates immutable files
instead of recreating runtime contents during every start.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import sys
import tempfile
from typing import Any, Mapping, Sequence
import zipfile


SCHEMA_VERSION = 1
MANIFEST_NAME = "bees-runtime-manifest.json"
VERSION_NAME = "bees-runtime-version.txt"
EXTRA_RUNTIME_FILES = (
    "bees_remote_requirements.txt",
    "bees_learner_requirements.txt",
    "requirements-continual.txt",
    "rl_1v1_config.yaml",
    "continual_learning_config.json",
)
REQUIRED_RUNTIME_FILES = (
    "bees_training_worker_agent.py",
    "bees_continual_elastic_wan_service.py",
    "bees_managed_remote_worker.py",
    "bees_remote_requirements.txt",
    "rl_1v1_config.yaml",
    "continual_learning_config.json",
)
SAFE_ID = re.compile(r"^[A-Za-z0-9._-]+$")


def _sha256_bytes(payload: bytes) -> str:
    return hashlib.sha256(payload).hexdigest()


def _sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        while True:
            chunk = handle.read(1024 * 1024)
            if not chunk:
                break
            digest.update(chunk)
    return digest.hexdigest()


def _canonical_json(value: Any) -> bytes:
    return json.dumps(
        value,
        sort_keys=True,
        separators=(",", ":"),
        ensure_ascii=True,
    ).encode("utf-8")


def _runtime_source_files(training_root: Path) -> list[Path]:
    files = sorted(
        (
            path
            for path in training_root.glob("*.py")
            if path.is_file() and not path.name.endswith("_tests.py")
        ),
        key=lambda path: path.name,
    )
    for name in EXTRA_RUNTIME_FILES:
        path = training_root / name
        if path.is_file():
            files.append(path)
    files = sorted({path.name: path for path in files}.values(), key=lambda path: path.name)
    names = {path.name for path in files}
    missing = [name for name in REQUIRED_RUNTIME_FILES if name not in names]
    if missing:
        raise ValueError(
            "training runtime source is missing required files: " + ", ".join(missing)
        )
    return files


def _entries_from_files(files: Sequence[Path]) -> list[dict[str, Any]]:
    entries: list[dict[str, Any]] = []
    for path in files:
        payload = path.read_bytes()
        entries.append(
            {
                "path": path.name,
                "size": len(payload),
                "sha256": _sha256_bytes(payload),
            }
        )
    return entries


def _runtime_version(entries: Sequence[Mapping[str, Any]]) -> str:
    return _sha256_bytes(_canonical_json({"files": list(entries)}))


def _zip_info(name: str) -> zipfile.ZipInfo:
    info = zipfile.ZipInfo(name, date_time=(1980, 1, 1, 0, 0, 0))
    info.compress_type = zipfile.ZIP_DEFLATED
    info.external_attr = 0o100644 << 16
    return info


def package_runtime(
    *,
    assets_root: Path,
    output: Path,
    build_id: str,
    source_commit: str,
) -> dict[str, Any]:
    if not SAFE_ID.fullmatch(build_id):
        raise ValueError(f"invalid build id: {build_id!r}")
    if not source_commit.strip():
        raise ValueError("source commit must be non-empty")

    training_root = assets_root.expanduser().resolve() / "Training"
    if not training_root.is_dir():
        raise ValueError(f"Training directory does not exist: {training_root}")

    files = _runtime_source_files(training_root)
    payloads = [(path, path.read_bytes()) for path in files]
    entries = [
        {
            "path": path.name,
            "size": len(payload),
            "sha256": _sha256_bytes(payload),
        }
        for path, payload in payloads
    ]
    runtime_version = _runtime_version(entries)
    manifest = {
        "schema_version": SCHEMA_VERSION,
        "build_id": build_id,
        "source_commit": source_commit.strip(),
        "runtime_version": runtime_version,
        "files": entries,
    }
    manifest_bytes = _canonical_json(manifest) + b"\n"
    version_bytes = (runtime_version + "\n").encode("ascii")

    output = output.expanduser().resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    fd, temp_name = tempfile.mkstemp(
        prefix=output.name + ".",
        suffix=".tmp",
        dir=str(output.parent),
    )
    os.close(fd)
    temporary = Path(temp_name)
    try:
        with zipfile.ZipFile(
            temporary,
            "w",
            compression=zipfile.ZIP_DEFLATED,
            compresslevel=9,
        ) as bundle:
            for path, payload in payloads:
                bundle.writestr(_zip_info(path.name), payload)
            bundle.writestr(_zip_info(VERSION_NAME), version_bytes)
            bundle.writestr(_zip_info(MANIFEST_NAME), manifest_bytes)
        os.replace(temporary, output)
    finally:
        try:
            temporary.unlink()
        except FileNotFoundError:
            pass

    return {
        "schema_version": SCHEMA_VERSION,
        "build_id": build_id,
        "source_commit": source_commit.strip(),
        "runtime_version": runtime_version,
        "archive": str(output),
        "archive_sha256": _sha256_file(output),
        "archive_size_bytes": output.stat().st_size,
    }


def _validate_member_name(name: str) -> None:
    if (
        not name
        or "\\" in name
        or "/" in name
        or ":" in name
        or name.startswith(("/", "\\"))
        or name.endswith(("/", "\\"))
    ):
        raise ValueError(f"runtime archive contains unsafe member path: {name!r}")
    parts = Path(name).parts
    if len(parts) != 1 or any(part in ("", ".", "..") for part in parts):
        raise ValueError(f"runtime archive contains unsafe member path: {name!r}")


def verify_runtime(
    archive: Path,
    *,
    expected_sha256: str = "",
    expected_version: str = "",
    expected_build_id: str = "",
) -> dict[str, Any]:
    archive = archive.expanduser().resolve()
    if not archive.is_file():
        raise ValueError(f"training runtime archive does not exist: {archive}")

    archive_sha256 = _sha256_file(archive)
    if expected_sha256 and archive_sha256 != expected_sha256.lower():
        raise ValueError(
            "training runtime archive SHA-256 mismatch: "
            f"expected {expected_sha256.lower()} got {archive_sha256}"
        )

    try:
        with zipfile.ZipFile(archive, "r") as bundle:
            infos = bundle.infolist()
            names = [info.filename for info in infos]
            if len(names) != len(set(names)):
                raise ValueError("training runtime archive contains duplicate members")
            for name in names:
                _validate_member_name(name)
            if MANIFEST_NAME not in names or VERSION_NAME not in names:
                raise ValueError("training runtime archive is missing its manifest/version marker")

            try:
                manifest = json.loads(bundle.read(MANIFEST_NAME).decode("utf-8"))
                marker_version = bundle.read(VERSION_NAME).decode("ascii").strip().lower()
            except (UnicodeDecodeError, json.JSONDecodeError) as exc:
                raise ValueError(f"training runtime metadata is invalid: {exc}") from exc

            if not isinstance(manifest, Mapping) or manifest.get("schema_version") != SCHEMA_VERSION:
                raise ValueError("training runtime manifest schema is unsupported")
            build_id = str(manifest.get("build_id", ""))
            source_commit = str(manifest.get("source_commit", ""))
            runtime_version = str(manifest.get("runtime_version", "")).lower()
            raw_entries = manifest.get("files")
            if not SAFE_ID.fullmatch(build_id) or not source_commit:
                raise ValueError("training runtime manifest identity is invalid")
            if not isinstance(raw_entries, list) or not raw_entries:
                raise ValueError("training runtime manifest has no files")

            entries: list[dict[str, Any]] = []
            declared_names: set[str] = set()
            for raw in raw_entries:
                if not isinstance(raw, Mapping):
                    raise ValueError("training runtime manifest file entry is invalid")
                name = str(raw.get("path", ""))
                _validate_member_name(name)
                if name in declared_names or name in (MANIFEST_NAME, VERSION_NAME):
                    raise ValueError("training runtime manifest contains duplicate/reserved file")
                declared_names.add(name)
                size = raw.get("size")
                sha256 = str(raw.get("sha256", "")).lower()
                if (
                    not isinstance(size, int)
                    or isinstance(size, bool)
                    or size < 0
                    or len(sha256) != 64
                    or any(ch not in "0123456789abcdef" for ch in sha256)
                ):
                    raise ValueError(f"training runtime manifest metadata is invalid for {name}")
                if name not in names:
                    raise ValueError(f"training runtime archive is missing declared file: {name}")
                payload = bundle.read(name)
                if len(payload) != size or _sha256_bytes(payload) != sha256:
                    raise ValueError(f"training runtime file failed integrity verification: {name}")
                entries.append({"path": name, "size": size, "sha256": sha256})

            actual_payload_names = set(names) - {MANIFEST_NAME, VERSION_NAME}
            if actual_payload_names != declared_names:
                extras = sorted(actual_payload_names - declared_names)
                missing = sorted(declared_names - actual_payload_names)
                raise ValueError(
                    "training runtime archive file set differs from manifest "
                    f"(extra={extras}, missing={missing})"
                )

            calculated_version = _runtime_version(entries)
            if runtime_version != calculated_version or marker_version != calculated_version:
                raise ValueError("training runtime version does not match manifest contents")
            if expected_version and calculated_version != expected_version.lower():
                raise ValueError(
                    "training runtime version mismatch: "
                    f"expected {expected_version.lower()} got {calculated_version}"
                )
            if expected_build_id and build_id != expected_build_id:
                raise ValueError(
                    f"training runtime build mismatch: expected {expected_build_id} got {build_id}"
                )

            for required in REQUIRED_RUNTIME_FILES:
                if required not in declared_names:
                    raise ValueError(f"training runtime is missing required file: {required}")
    except zipfile.BadZipFile as exc:
        raise ValueError(f"training runtime archive is not a valid ZIP: {archive}") from exc

    return {
        "schema_version": SCHEMA_VERSION,
        "build_id": build_id,
        "source_commit": source_commit,
        "runtime_version": calculated_version,
        "archive": str(archive),
        "archive_sha256": archive_sha256,
        "archive_size_bytes": archive.stat().st_size,
    }


def _verify_installed(root: Path, manifest: Mapping[str, Any], runtime_version: str) -> None:
    if not root.is_dir():
        raise ValueError(f"installed runtime root is missing: {root}")
    marker = root / VERSION_NAME
    if not marker.is_file() or marker.read_text(encoding="ascii").strip().lower() != runtime_version:
        raise ValueError(f"installed runtime version marker is invalid: {root}")
    for raw in manifest["files"]:
        path = root / str(raw["path"])
        if (
            not path.is_file()
            or path.stat().st_size != int(raw["size"])
            or _sha256_file(path) != str(raw["sha256"]).lower()
        ):
            raise ValueError(f"installed runtime failed integrity verification: {path}")


def install_runtime(
    archive: Path,
    *,
    destination_root: Path,
    expected_sha256: str = "",
    expected_version: str = "",
    expected_build_id: str = "",
) -> dict[str, Any]:
    metadata = verify_runtime(
        archive,
        expected_sha256=expected_sha256,
        expected_version=expected_version,
        expected_build_id=expected_build_id,
    )
    archive = archive.expanduser().resolve()
    destination_root = destination_root.expanduser().resolve()
    destination_root.mkdir(parents=True, exist_ok=True)
    destination = destination_root / (
        f"{metadata['build_id']}-{metadata['runtime_version'][:16]}"
    )

    with zipfile.ZipFile(archive, "r") as bundle:
        manifest = json.loads(bundle.read(MANIFEST_NAME).decode("utf-8"))
        if destination.exists():
            _verify_installed(destination, manifest, metadata["runtime_version"])
        else:
            temporary = Path(
                tempfile.mkdtemp(
                    prefix=destination.name + ".",
                    suffix=".tmp",
                    dir=str(destination_root),
                )
            )
            try:
                for info in bundle.infolist():
                    _validate_member_name(info.filename)
                    target = temporary / info.filename
                    target.write_bytes(bundle.read(info.filename))
                _verify_installed(temporary, manifest, metadata["runtime_version"])
                try:
                    os.replace(temporary, destination)
                except OSError:
                    if not destination.exists():
                        raise
                    _verify_installed(destination, manifest, metadata["runtime_version"])
            finally:
                if temporary.exists():
                    shutil.rmtree(temporary, ignore_errors=True)

    result = dict(metadata)
    result["installed_root"] = str(destination)
    return result


def _parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser()
    sub = parser.add_subparsers(dest="command", required=True)

    package = sub.add_parser("package")
    package.add_argument("--assets-root", required=True)
    package.add_argument("--output", required=True)
    package.add_argument("--build-id", required=True)
    package.add_argument("--source-commit", required=True)

    verify = sub.add_parser("verify")
    verify.add_argument("--archive", required=True)
    verify.add_argument("--expected-sha256", default="")
    verify.add_argument("--expected-version", default="")
    verify.add_argument("--expected-build-id", default="")

    install = sub.add_parser("install")
    install.add_argument("--archive", required=True)
    install.add_argument("--destination-root", required=True)
    install.add_argument("--expected-sha256", default="")
    install.add_argument("--expected-version", default="")
    install.add_argument("--expected-build-id", default="")
    return parser


def main(argv: Sequence[str] | None = None) -> int:
    args = _parser().parse_args(argv)
    try:
        if args.command == "package":
            result = package_runtime(
                assets_root=Path(args.assets_root),
                output=Path(args.output),
                build_id=args.build_id,
                source_commit=args.source_commit,
            )
        elif args.command == "verify":
            result = verify_runtime(
                Path(args.archive),
                expected_sha256=args.expected_sha256,
                expected_version=args.expected_version,
                expected_build_id=args.expected_build_id,
            )
        else:
            result = install_runtime(
                Path(args.archive),
                destination_root=Path(args.destination_root),
                expected_sha256=args.expected_sha256,
                expected_version=args.expected_version,
                expected_build_id=args.expected_build_id,
            )
    except (OSError, ValueError, zipfile.BadZipFile) as exc:
        print(f"training runtime error: {exc}", file=sys.stderr)
        return 2
    print(json.dumps(result, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
