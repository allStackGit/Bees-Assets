"""Create one atomic private-bootstrap snapshot for Bees remote workers.

The tailnet gateway serves this outer ZIP verbatim. Publishing the full payload as one file prevents
workers from observing a mixed generation while a release/runtime/token/helper set is being updated.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import tempfile
from typing import Any, Mapping, Sequence
import zipfile


ENTRY_SPECS = (
    ("bees-remote-runtime.zip", "runtime", 0o644),
    ("training-worker.token", "worker_token", 0o600),
    ("wan.token", "wan_token", 0o600),
    ("latest-training-release.json", "release", 0o600),
    ("bees-tailnet-bridge-windows.exe", "windows_bridge", 0o700),
    ("bees-tailnet-bridge-linux", "linux_bridge", 0o700),
)
RUNTIME_VERSION_NAME = "bees-runtime-version.txt"


def _sha256(payload: bytes) -> str:
    return hashlib.sha256(payload).hexdigest()


def _zip_info(name: str, mode: int) -> zipfile.ZipInfo:
    info = zipfile.ZipInfo(name, date_time=(1980, 1, 1, 0, 0, 0))
    info.compress_type = zipfile.ZIP_DEFLATED
    info.external_attr = (0o100000 | mode) << 16
    return info


def _read_required_file(path: Path, label: str) -> bytes:
    path = path.expanduser().resolve()
    if not path.is_file():
        raise ValueError(f"{label} is missing or not a file: {path}")
    payload = path.read_bytes()
    if not payload:
        raise ValueError(f"{label} is empty: {path}")
    return payload


def _runtime_version(runtime_zip: bytes) -> str:
    try:
        with zipfile.ZipFile(__import__("io").BytesIO(runtime_zip), "r") as bundle:
            value = bundle.read(RUNTIME_VERSION_NAME).decode("ascii").strip().lower()
    except (KeyError, UnicodeDecodeError, zipfile.BadZipFile) as exc:
        raise ValueError(f"training runtime archive has no valid version marker: {exc}") from exc
    if (
        len(value) != 64
        or any(ch not in "0123456789abcdef" for ch in value)
    ):
        raise ValueError("training runtime archive version marker is malformed")
    return value


def _decode_release(payload: bytes) -> Mapping[str, Any]:
    try:
        value = json.loads(payload.decode("utf-8-sig"))
    except (UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise ValueError(f"release metadata is invalid JSON: {exc}") from exc
    if not isinstance(value, Mapping):
        raise ValueError("release metadata must be a JSON object")
    build_id = str(value.get("build_id", "")).strip()
    if not build_id:
        raise ValueError("release metadata has no build_id")
    return value


def _verify_runtime_matches_release(runtime_zip: bytes, release: Mapping[str, Any]) -> tuple[str, str]:
    runtime_sha = _sha256(runtime_zip)
    runtime_version = _runtime_version(runtime_zip)
    runtime = release.get("training_runtime")
    if not isinstance(runtime, Mapping):
        raise ValueError("release metadata has no immutable training_runtime identity")
    expected_sha = str(runtime.get("archive_sha256", "")).strip().lower()
    expected_version = str(runtime.get("runtime_version", "")).strip().lower()
    if runtime_sha != expected_sha:
        raise ValueError(
            "training runtime archive SHA-256 does not match release metadata: "
            f"expected {expected_sha or '(missing)'} got {runtime_sha}"
        )
    if runtime_version != expected_version:
        raise ValueError(
            "training runtime version does not match release metadata: "
            f"expected {expected_version or '(missing)'} got {runtime_version}"
        )
    return runtime_sha, runtime_version


def create_bundle(
    *,
    output: Path,
    runtime: Path,
    worker_token: Path,
    wan_token: Path,
    release: Path,
    windows_bridge: Path,
    linux_bridge: Path,
) -> dict[str, Any]:
    inputs = {
        "runtime": _read_required_file(runtime, "training runtime archive"),
        "worker_token": _read_required_file(worker_token, "worker token"),
        "wan_token": _read_required_file(wan_token, "WAN token"),
        "release": _read_required_file(release, "release metadata"),
        "windows_bridge": _read_required_file(windows_bridge, "Windows tailnet bridge"),
        "linux_bridge": _read_required_file(linux_bridge, "Linux tailnet bridge"),
    }

    for key in ("worker_token", "wan_token"):
        try:
            text = inputs[key].decode("ascii").strip()
        except UnicodeDecodeError as exc:
            raise ValueError(f"{key.replace('_', ' ')} must be ASCII") from exc
        if not text:
            raise ValueError(f"{key.replace('_', ' ')} is empty")

    release_metadata = _decode_release(inputs["release"])
    runtime_sha, runtime_version = _verify_runtime_matches_release(
        inputs["runtime"], release_metadata
    )

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
            for archive_name, key, mode in ENTRY_SPECS:
                bundle.writestr(_zip_info(archive_name, mode), inputs[key])
        os.replace(temporary, output)
    finally:
        try:
            temporary.unlink()
        except FileNotFoundError:
            pass

    bundle_bytes = output.read_bytes()
    return {
        "schema_version": 1,
        "build_id": str(release_metadata["build_id"]),
        "runtime_sha256": runtime_sha,
        "runtime_version": runtime_version,
        "bundle_sha256": _sha256(bundle_bytes),
        "bundle_size_bytes": len(bundle_bytes),
        "output": str(output),
    }


def _parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True)
    parser.add_argument("--runtime", required=True)
    parser.add_argument("--worker-token", required=True)
    parser.add_argument("--wan-token", required=True)
    parser.add_argument("--release", required=True)
    parser.add_argument("--windows-bridge", required=True)
    parser.add_argument("--linux-bridge", required=True)
    return parser


def main(argv: Sequence[str] | None = None) -> int:
    args = _parser().parse_args(argv)
    try:
        result = create_bundle(
            output=Path(args.output),
            runtime=Path(args.runtime),
            worker_token=Path(args.worker_token),
            wan_token=Path(args.wan_token),
            release=Path(args.release),
            windows_bridge=Path(args.windows_bridge),
            linux_bridge=Path(args.linux_bridge),
        )
    except (OSError, ValueError, zipfile.BadZipFile) as exc:
        print(f"bootstrap bundle error: {exc}", file=__import__("sys").stderr)
        return 2
    print(json.dumps(result, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
