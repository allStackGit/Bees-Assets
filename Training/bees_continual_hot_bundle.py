"""Publish Unity-built champion AssetBundles for authenticated desktop hot rollout.

The continual registry remains the deployment authority. Unity Editor imports the verified champion
ONNX and builds a platform-specific AssetBundle containing the resulting ModelAsset plus deployment
manifest. This publisher accepts that opaque bundle only when its sidecar metadata exactly matches
the registry's current published champion, then copies it into an immutable server distribution tree
and atomically advances one platform pointer. Clients never receive an unpromoted model.
"""

from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import sys
import tempfile
from pathlib import Path
from typing import Mapping, Optional, Sequence

from bees_continual_deployment import OnnxValidator, publish_current_champion
from bees_continual_learning import (
    ContinualLearningError,
    ContinualLearningStore,
    ValidationError,
    canonical_json,
    load_config,
    sha256_bytes,
    sha256_file,
    utc_now,
)


HOT_BUNDLE_METADATA_SCHEMA_VERSION = 1
HOT_BUNDLE_POINTER_SCHEMA_VERSION = 1
HOT_BUNDLE_FILE = "champion.bundle"
HOT_BUNDLE_METADATA_FILE = "bundle-metadata.json"
EXPECTED_MODEL_ADDRESS = "BeesRL1v1"
EXPECTED_MANIFEST_ADDRESS = "BeesRL1v1Deployment"
MAX_HOT_BUNDLE_BYTES = 512 * 1024 * 1024
SUPPORTED_PLATFORMS = {
    "WindowsPlayer": "StandaloneWindows64",
    "OSXPlayer": "StandaloneOSX",
    "LinuxPlayer": "StandaloneLinux64",
}
_SAFE_PLATFORM = re.compile(r"^[A-Za-z0-9._-]{1,64}$")
_DEPLOYMENT_ID = re.compile(r"^deploy-[0-9a-f]{24}$")
_MODEL_ID = re.compile(r"^bees-rl-v\d+-[0-9a-f]{24}$")
_SHA256 = re.compile(r"^[0-9a-f]{64}$")


def _read_json_object(path: Path, label: str) -> Mapping[str, object]:
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except FileNotFoundError as exc:
        raise ValidationError(f"{label} does not exist: {path}") from exc
    except json.JSONDecodeError as exc:
        raise ValidationError(f"{label} is invalid JSON: {path}: {exc}") from exc
    if not isinstance(value, dict):
        raise ValidationError(f"{label} must contain a JSON object: {path}")
    return value


def _required_string(value: object, label: str, maximum: int = 512) -> str:
    if not isinstance(value, str) or not value.strip() or len(value) > maximum:
        raise ValidationError(f"{label} must be a non-empty string up to {maximum} characters.")
    return value.strip()


def _required_sha256(value: object, label: str) -> str:
    normalized = _required_string(value, label, 64).lower()
    if not _SHA256.fullmatch(normalized):
        raise ValidationError(f"{label} must be a lowercase SHA-256 hex digest.")
    return normalized


def _write_bytes_immutable(path: Path, payload: bytes) -> bool:
    path.parent.mkdir(parents=True, exist_ok=True)
    if path.exists():
        if path.read_bytes() != payload:
            raise ValidationError(f"Immutable hot-bundle publication conflict: {path}")
        return False
    try:
        with path.open("xb") as handle:
            handle.write(payload)
            handle.flush()
            os.fsync(handle.fileno())
    except FileExistsError:
        if path.read_bytes() != payload:
            raise ValidationError(f"Immutable hot-bundle publication conflict: {path}")
        return False
    return True


def _copy_file_immutable(source: Path, destination: Path, expected_sha256: str) -> bool:
    destination.parent.mkdir(parents=True, exist_ok=True)
    if destination.exists():
        if sha256_file(destination) != expected_sha256:
            raise ValidationError(f"Immutable hot-bundle hash conflict: {destination}")
        return False
    fd, temp_name = tempfile.mkstemp(
        prefix=destination.name + ".",
        suffix=".tmp",
        dir=str(destination.parent),
    )
    os.close(fd)
    temp = Path(temp_name)
    try:
        shutil.copyfile(source, temp)
        if sha256_file(temp) != expected_sha256:
            raise ValidationError(f"Unity hot bundle changed while publishing: {source}")
        try:
            os.link(temp, destination)
            return True
        except FileExistsError:
            if sha256_file(destination) != expected_sha256:
                raise ValidationError(f"Immutable hot-bundle hash conflict: {destination}")
            return False
        except OSError:
            # Cross-filesystem / Windows link restrictions: exclusive create preserves the same
            # immutable winner semantics without assuming hard-link support.
            try:
                with destination.open("xb") as output, temp.open("rb") as input_stream:
                    shutil.copyfileobj(input_stream, output)
                    output.flush()
                    os.fsync(output.fileno())
                if sha256_file(destination) != expected_sha256:
                    destination.unlink(missing_ok=True)
                    raise ValidationError(f"Published hot bundle failed integrity verification: {destination}")
                return True
            except FileExistsError:
                if sha256_file(destination) != expected_sha256:
                    raise ValidationError(f"Immutable hot-bundle hash conflict: {destination}")
                return False
    finally:
        temp.unlink(missing_ok=True)


def _write_json_atomic(path: Path, value: Mapping[str, object]) -> None:
    payload = (json.dumps(value, indent=2, sort_keys=True, ensure_ascii=False) + "\n").encode("utf-8")
    path.parent.mkdir(parents=True, exist_ok=True)
    fd, temp_name = tempfile.mkstemp(prefix=path.name + ".", suffix=".tmp", dir=str(path.parent))
    try:
        with os.fdopen(fd, "wb") as handle:
            handle.write(payload)
            handle.flush()
            os.fsync(handle.fileno())
        os.replace(temp_name, path)
    finally:
        try:
            os.unlink(temp_name)
        except FileNotFoundError:
            pass


def _validate_unity_metadata(
    metadata: Mapping[str, object],
    *,
    deployment: Mapping[str, object],
    deployment_manifest: Mapping[str, object],
    bundle_path: Path,
    store: ContinualLearningStore,
) -> Mapping[str, object]:
    if metadata.get("schema_version") != HOT_BUNDLE_METADATA_SCHEMA_VERSION:
        raise ValidationError("Unity hot-bundle metadata schema is incompatible.")

    platform = _required_string(metadata.get("platform"), "platform", 64)
    if not _SAFE_PLATFORM.fullmatch(platform) or platform not in SUPPORTED_PLATFORMS:
        raise ValidationError(f"Unsupported hot-bundle platform: {platform!r}.")
    build_target = _required_string(metadata.get("build_target"), "build_target", 64)
    if build_target != SUPPORTED_PLATFORMS[platform]:
        raise ValidationError(
            f"Unity hot-bundle build_target {build_target!r} does not match platform {platform!r}."
        )

    deployment_id = _required_string(metadata.get("deployment_id"), "deployment_id", 64)
    model_id = _required_string(metadata.get("model_id"), "model_id", 96)
    if not _DEPLOYMENT_ID.fullmatch(deployment_id) or deployment_id != deployment.get("deployment_id"):
        raise ValidationError("Unity hot bundle does not target the registry's current deployment.")
    if not _MODEL_ID.fullmatch(model_id) or model_id != deployment.get("model_id"):
        raise ValidationError("Unity hot bundle does not contain the registry's current champion model ID.")

    identity = deployment_manifest.get("identity")
    if not isinstance(identity, Mapping):
        raise ValidationError("Current deployment manifest identity is malformed.")
    model_sha256 = _required_sha256(metadata.get("model_sha256"), "model_sha256")
    manifest_sha256 = _required_sha256(metadata.get("manifest_sha256"), "manifest_sha256")
    if model_sha256 != identity.get("model_sha256"):
        raise ValidationError("Unity hot-bundle model hash does not match the current deployment package.")
    if manifest_sha256 != deployment.get("manifest_sha256"):
        raise ValidationError("Unity hot-bundle manifest hash does not match the current deployment package.")

    if metadata.get("policy_abi_version") != store.compatibility.policy_abi_version:
        raise ValidationError("Unity hot-bundle policy ABI does not match the continual-learning store.")
    expected_signature = store.config.get("policy_signature")
    if metadata.get("policy_signature") != expected_signature:
        raise ValidationError("Unity hot-bundle policy signature does not match the current frozen policy.")
    if metadata.get("model_address") != EXPECTED_MODEL_ADDRESS or metadata.get(
        "manifest_address"
    ) != EXPECTED_MANIFEST_ADDRESS:
        raise ValidationError("Unity hot-bundle asset addresses do not match the runtime loader contract.")

    bundle_sha256 = _required_sha256(metadata.get("bundle_sha256"), "bundle_sha256")
    bundle_size = metadata.get("bundle_size_bytes")
    if (
        not isinstance(bundle_size, int)
        or isinstance(bundle_size, bool)
        or bundle_size <= 0
        or bundle_size > MAX_HOT_BUNDLE_BYTES
    ):
        raise ValidationError(f"Unity hot-bundle size must be in [1, {MAX_HOT_BUNDLE_BYTES}] bytes.")
    if not bundle_path.is_file() or bundle_path.stat().st_size != bundle_size:
        raise ValidationError("Unity hot-bundle byte size does not match its metadata.")
    if sha256_file(bundle_path) != bundle_sha256:
        raise ValidationError("Unity hot bundle failed SHA-256 verification.")

    return {
        "schema_version": HOT_BUNDLE_POINTER_SCHEMA_VERSION,
        "platform": platform,
        "deployment_id": deployment_id,
        "model_id": model_id,
        "bundle_sha256": bundle_sha256,
        "bundle_size_bytes": bundle_size,
        "manifest_sha256": manifest_sha256,
        "model_sha256": model_sha256,
        "policy_abi_version": store.compatibility.policy_abi_version,
        "policy_signature": expected_signature,
    }


def publish_hot_bundle(
    store: ContinualLearningStore,
    metadata_path: str | os.PathLike[str],
    distribution_root: str | os.PathLike[str],
    *,
    onnx_validator: Optional[OnnxValidator] = None,
) -> Mapping[str, object]:
    """Validate one Unity-built AssetBundle and atomically publish it for one desktop platform."""
    store._require_initialized()
    metadata_file = Path(metadata_path).expanduser().resolve()
    metadata = _read_json_object(metadata_file, "Unity hot-bundle metadata")
    bundle_file = _required_string(metadata.get("bundle_file"), "bundle_file", 128)
    if Path(bundle_file).name != bundle_file:
        raise ValidationError("Unity hot-bundle metadata bundle_file must be a filename, not a path.")
    source_bundle = metadata_file.parent / bundle_file

    deployment = publish_current_champion(store, onnx_validator=onnx_validator)
    deployment_manifest = _read_json_object(
        Path(str(deployment["manifest_path"])),
        "Current deployment manifest",
    )
    identity = dict(
        _validate_unity_metadata(
            metadata,
            deployment=deployment,
            deployment_manifest=deployment_manifest,
            bundle_path=source_bundle,
            store=store,
        )
    )

    root = Path(distribution_root).expanduser().resolve()
    package_dir = root / "packages" / identity["deployment_id"] / identity["platform"]
    destination_bundle = package_dir / HOT_BUNDLE_FILE
    _copy_file_immutable(source_bundle, destination_bundle, str(identity["bundle_sha256"]))
    relative_bundle = destination_bundle.relative_to(root).as_posix()
    identity["bundle_path"] = relative_bundle

    normalized_metadata = {
        "schema_version": HOT_BUNDLE_METADATA_SCHEMA_VERSION,
        "identity": identity,
        "unity_version": _required_string(metadata.get("unity_version"), "unity_version", 128),
        "build_target": metadata["build_target"],
        "model_address": EXPECTED_MODEL_ADDRESS,
        "manifest_address": EXPECTED_MANIFEST_ADDRESS,
    }
    metadata_payload = (
        json.dumps(normalized_metadata, indent=2, sort_keys=True, ensure_ascii=False) + "\n"
    ).encode("utf-8")
    _write_bytes_immutable(package_dir / HOT_BUNDLE_METADATA_FILE, metadata_payload)

    identity_sha256 = sha256_bytes(canonical_json(identity).encode("utf-8"))
    pointer = {
        "schema_version": HOT_BUNDLE_POINTER_SCHEMA_VERSION,
        "identity_sha256": identity_sha256,
        "identity": identity,
        "published_at": utc_now(),
    }
    pointer_path = root / f"current-{identity['platform']}.json"
    pointer_changed = True
    if pointer_path.exists():
        existing = _read_json_object(pointer_path, "Current hot-bundle pointer")
        if existing.get("identity") == identity and existing.get("identity_sha256") == identity_sha256:
            pointer_changed = False
        else:
            _write_json_atomic(pointer_path, pointer)
    else:
        _write_json_atomic(pointer_path, pointer)

    return {
        "deployment_id": identity["deployment_id"],
        "model_id": identity["model_id"],
        "platform": identity["platform"],
        "bundle_path": str(destination_bundle),
        "bundle_sha256": identity["bundle_sha256"],
        "bundle_size_bytes": identity["bundle_size_bytes"],
        "pointer_path": str(pointer_path),
        "pointer_identity_sha256": identity_sha256,
        "pointer_changed": pointer_changed,
    }


def _build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Publish a Unity-built Bees RL champion AssetBundle for authenticated hot rollout."
    )
    parser.add_argument("--root", required=True, help="Initialized continual-learning store root.")
    parser.add_argument("--bundle-metadata", required=True, help="Unity-generated hot-bundle metadata JSON.")
    parser.add_argument("--distribution-root", required=True, help="BeesServer RL model distribution root.")
    parser.add_argument(
        "--config",
        default=str(Path(__file__).with_name("continual_learning_config.json")),
        help="Continual-learning configuration JSON.",
    )
    return parser


def main(argv: Optional[Sequence[str]] = None) -> int:
    args = _build_parser().parse_args(argv)
    try:
        store = ContinualLearningStore(args.root, load_config(args.config))
        result = publish_hot_bundle(store, args.bundle_metadata, args.distribution_root)
        print(json.dumps(result, indent=2, sort_keys=True, ensure_ascii=False))
        return 0
    except (ContinualLearningError, OSError, ValueError) as exc:
        print(f"Champion hot-bundle publication failed: {exc}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
