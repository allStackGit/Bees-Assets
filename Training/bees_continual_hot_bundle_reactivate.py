"""Reactivate a retained current-champion AssetBundle without rebuilding it in Unity.

Hot-bundle publication is content-addressed and immutable, so a previously deployed champion keeps
its platform bundles under the distribution package tree. After an intentional registry rollback,
this module validates that retained bundle against the newly current deployment and atomically moves
the platform pointer back to it. This is the fast rollback path for already-built desktop clients.
"""

from __future__ import annotations

import argparse
import json
import os
import sys
from pathlib import Path
from typing import Mapping, Optional, Sequence

from bees_continual_deployment import OnnxValidator, publish_current_champion
from bees_continual_hot_bundle import (
    EXPECTED_MANIFEST_ADDRESS,
    EXPECTED_MODEL_ADDRESS,
    HOT_BUNDLE_FILE,
    HOT_BUNDLE_METADATA_FILE,
    HOT_BUNDLE_METADATA_SCHEMA_VERSION,
    HOT_BUNDLE_POINTER_SCHEMA_VERSION,
    MAX_HOT_BUNDLE_BYTES,
    SUPPORTED_PLATFORMS,
    _read_json_object,
    _required_sha256,
    _required_string,
    _write_json_atomic,
)
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


def _validated_retained_identity(
    store: ContinualLearningStore,
    *,
    deployment: Mapping[str, object],
    root: Path,
    platform: str,
) -> tuple[dict[str, object], Path]:
    if platform not in SUPPORTED_PLATFORMS:
        raise ValidationError(f"Unsupported hot-bundle platform: {platform!r}.")

    deployment_id = _required_string(deployment.get("deployment_id"), "deployment_id", 64)
    model_id = _required_string(deployment.get("model_id"), "model_id", 96)
    manifest_sha256 = _required_sha256(deployment.get("manifest_sha256"), "manifest_sha256")
    deployment_manifest = _read_json_object(
        Path(str(deployment.get("manifest_path", ""))).expanduser().resolve(),
        "Current deployment manifest",
    )
    deployment_identity = deployment_manifest.get("identity")
    if not isinstance(deployment_identity, Mapping):
        raise ValidationError("Current deployment manifest identity is malformed.")
    model_sha256 = _required_sha256(
        deployment_identity.get("model_sha256"),
        "deployment model_sha256",
    )

    package_dir = root / "packages" / deployment_id / platform
    metadata_path = package_dir / HOT_BUNDLE_METADATA_FILE
    bundle_path = package_dir / HOT_BUNDLE_FILE
    metadata = _read_json_object(metadata_path, "Retained hot-bundle metadata")
    if metadata.get("schema_version") != HOT_BUNDLE_METADATA_SCHEMA_VERSION:
        raise ValidationError("Retained hot-bundle metadata schema is incompatible.")
    if metadata.get("build_target") != SUPPORTED_PLATFORMS[platform]:
        raise ValidationError("Retained hot-bundle build target does not match its platform.")
    if metadata.get("model_address") != EXPECTED_MODEL_ADDRESS or metadata.get(
        "manifest_address"
    ) != EXPECTED_MANIFEST_ADDRESS:
        raise ValidationError("Retained hot-bundle asset addresses do not match the runtime contract.")

    stored_identity = metadata.get("identity")
    if not isinstance(stored_identity, Mapping):
        raise ValidationError("Retained hot-bundle identity is missing or malformed.")
    bundle_sha256 = _required_sha256(stored_identity.get("bundle_sha256"), "bundle_sha256")
    bundle_size = stored_identity.get("bundle_size_bytes")
    if (
        not isinstance(bundle_size, int)
        or isinstance(bundle_size, bool)
        or bundle_size <= 0
        or bundle_size > MAX_HOT_BUNDLE_BYTES
    ):
        raise ValidationError(f"Retained hot-bundle size must be in [1, {MAX_HOT_BUNDLE_BYTES}] bytes.")
    if not bundle_path.is_file() or bundle_path.stat().st_size != bundle_size:
        raise ValidationError("Retained hot-bundle byte size does not match its metadata.")
    if sha256_file(bundle_path) != bundle_sha256:
        raise ValidationError("Retained hot bundle failed SHA-256 verification.")

    expected_relative_bundle = bundle_path.relative_to(root).as_posix()
    expected_identity: dict[str, object] = {
        "schema_version": HOT_BUNDLE_POINTER_SCHEMA_VERSION,
        "platform": platform,
        "deployment_id": deployment_id,
        "model_id": model_id,
        "bundle_sha256": bundle_sha256,
        "bundle_size_bytes": bundle_size,
        "manifest_sha256": manifest_sha256,
        "model_sha256": model_sha256,
        "policy_abi_version": store.compatibility.policy_abi_version,
        "policy_signature": store.config.get("policy_signature"),
        "bundle_path": expected_relative_bundle,
    }
    if dict(stored_identity) != expected_identity:
        raise ValidationError(
            "Retained hot-bundle identity does not exactly match the registry's current champion deployment."
        )
    return expected_identity, bundle_path


def reactivate_current_hot_bundle(
    store: ContinualLearningStore,
    distribution_root: str | os.PathLike[str],
    platform: str,
    *,
    onnx_validator: Optional[OnnxValidator] = None,
) -> Mapping[str, object]:
    """Validate and atomically reactivate one retained bundle for the current champion."""
    store._require_initialized()
    root = Path(distribution_root).expanduser().resolve()
    deployment = publish_current_champion(store, onnx_validator=onnx_validator)
    identity, bundle_path = _validated_retained_identity(
        store,
        deployment=deployment,
        root=root,
        platform=platform,
    )
    identity_sha256 = sha256_bytes(canonical_json(identity).encode("utf-8"))
    pointer_path = root / f"current-{platform}.json"
    pointer_changed = True
    if pointer_path.exists():
        existing = _read_json_object(pointer_path, "Current hot-bundle pointer")
        if existing.get("identity") == identity and existing.get("identity_sha256") == identity_sha256:
            pointer_changed = False
        else:
            _write_json_atomic(
                pointer_path,
                {
                    "schema_version": HOT_BUNDLE_POINTER_SCHEMA_VERSION,
                    "identity_sha256": identity_sha256,
                    "identity": identity,
                    "published_at": utc_now(),
                },
            )
    else:
        _write_json_atomic(
            pointer_path,
            {
                "schema_version": HOT_BUNDLE_POINTER_SCHEMA_VERSION,
                "identity_sha256": identity_sha256,
                "identity": identity,
                "published_at": utc_now(),
            },
        )

    return {
        "deployment_id": identity["deployment_id"],
        "model_id": identity["model_id"],
        "platform": platform,
        "bundle_path": str(bundle_path),
        "bundle_sha256": identity["bundle_sha256"],
        "pointer_path": str(pointer_path),
        "pointer_identity_sha256": identity_sha256,
        "pointer_changed": pointer_changed,
    }


def _build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Reactivate a retained Bees RL champion AssetBundle after rollback."
    )
    parser.add_argument("--root", required=True, help="Initialized continual-learning store root.")
    parser.add_argument("--distribution-root", required=True, help="BeesServer RL model distribution root.")
    parser.add_argument("--platform", required=True, choices=sorted(SUPPORTED_PLATFORMS))
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
        result = reactivate_current_hot_bundle(store, args.distribution_root, args.platform)
        print(json.dumps(result, indent=2, sort_keys=True, ensure_ascii=False))
        return 0
    except (ContinualLearningError, OSError, ValueError) as exc:
        print(f"Champion hot-bundle reactivation failed: {exc}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
