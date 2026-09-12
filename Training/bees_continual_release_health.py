"""Read-only integrity/consistency monitoring for the Bees continual release path.

This checker intentionally does not promote, publish, repair, or roll back anything. It verifies the
control-plane chain from the registry's current champion through current-deployment.json and, when
requested, through selected platform hot-bundle pointers and bytes. A scheduler can use the exit code
or JSON result to detect deployment drift/corruption before attempting another automatic release.
"""

from __future__ import annotations

import argparse
import json
import os
import sys
from pathlib import Path
from typing import Any, Dict, List, Mapping, Optional, Sequence

from bees_continual_deployment import (
    CURRENT_DEPLOYMENT_FILE,
    DEPLOYMENT_DIRECTORY,
    DEPLOYMENT_MANIFEST_FILE,
    DEPLOYMENT_MODEL_FILE,
    DEPLOYMENT_POINTER_SCHEMA_VERSION,
)
from bees_continual_hot_bundle import (
    HOT_BUNDLE_POINTER_SCHEMA_VERSION,
    SUPPORTED_PLATFORMS,
)
from bees_continual_learning import (
    ContinualLearningError,
    ContinualLearningStore,
    ValidationError,
    canonical_json,
    load_config,
    sha256_bytes,
    sha256_file,
)


def _issue(issues: List[Dict[str, str]], code: str, message: str) -> None:
    issues.append({"code": code, "message": message})


def _read_json_object(path: Path, issues: List[Dict[str, str]], code: str) -> Optional[Dict[str, Any]]:
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except FileNotFoundError:
        _issue(issues, code, f"Missing JSON file: {path}")
        return None
    except (OSError, json.JSONDecodeError) as exc:
        _issue(issues, code, f"Unreadable/invalid JSON file {path}: {exc}")
        return None
    if not isinstance(value, dict):
        _issue(issues, code, f"JSON file must contain an object: {path}")
        return None
    return value


def _valid_identity_hash(
    value: Mapping[str, Any],
    issues: List[Dict[str, str]],
    code: str,
    label: str,
) -> bool:
    identity = value.get("identity")
    recorded = value.get("identity_sha256")
    if not isinstance(identity, Mapping) or not isinstance(recorded, str):
        _issue(issues, code, f"{label} is missing identity/identity_sha256.")
        return False
    computed = sha256_bytes(canonical_json(identity).encode("utf-8"))
    if recorded != computed:
        _issue(issues, code, f"{label} identity SHA-256 does not match its contents.")
        return False
    return True


def _safe_distribution_path(root: Path, relative: object) -> Optional[Path]:
    if not isinstance(relative, str) or not relative.strip():
        return None
    candidate = Path(relative)
    if candidate.is_absolute():
        return None
    resolved = (root / candidate).resolve()
    try:
        resolved.relative_to(root)
    except ValueError:
        return None
    return resolved


def _inspect_deployment(
    store: ContinualLearningStore,
    champion: Mapping[str, Any],
    issues: List[Dict[str, str]],
) -> Optional[Dict[str, Any]]:
    pointer_path = store.root / DEPLOYMENT_DIRECTORY / CURRENT_DEPLOYMENT_FILE
    pointer = _read_json_object(pointer_path, issues, "deployment_pointer_missing_or_invalid")
    if pointer is None:
        return None
    if pointer.get("schema_version") != DEPLOYMENT_POINTER_SCHEMA_VERSION:
        _issue(
            issues,
            "deployment_pointer_schema_mismatch",
            f"Current deployment pointer schema is {pointer.get('schema_version')!r}; "
            f"expected {DEPLOYMENT_POINTER_SCHEMA_VERSION}.",
        )
    _valid_identity_hash(
        pointer,
        issues,
        "deployment_pointer_integrity_failure",
        "Current deployment pointer",
    )
    identity = pointer.get("identity")
    if not isinstance(identity, Mapping):
        return None

    champion_id = champion.get("model_id")
    if identity.get("model_id") != champion_id:
        _issue(
            issues,
            "deployment_model_mismatch",
            f"Deployment points to {identity.get('model_id')!r}, but registry champion is {champion_id!r}.",
        )
    if identity.get("policy_abi_version") != store.compatibility.policy_abi_version:
        _issue(issues, "deployment_policy_abi_mismatch", "Deployment policy ABI does not match the store.")
    expected_signature = store.config.get("policy_signature")
    if identity.get("policy_signature") != expected_signature:
        _issue(
            issues,
            "deployment_policy_signature_mismatch",
            "Deployment policy signature does not match the store's frozen policy signature.",
        )

    deployment_id = identity.get("deployment_id")
    if not isinstance(deployment_id, str) or not deployment_id:
        _issue(issues, "deployment_id_invalid", "Current deployment pointer has no deployment_id.")
        return None
    package_dir = store.root / DEPLOYMENT_DIRECTORY / "packages" / deployment_id
    manifest_path = package_dir / DEPLOYMENT_MANIFEST_FILE
    model_path = package_dir / DEPLOYMENT_MODEL_FILE
    manifest = _read_json_object(manifest_path, issues, "deployment_manifest_missing_or_invalid")
    if manifest is None:
        return {
            "pointer_path": str(pointer_path),
            "deployment_id": deployment_id,
            "model_id": identity.get("model_id"),
        }

    expected_manifest_sha = identity.get("manifest_sha256")
    if not isinstance(expected_manifest_sha, str) or sha256_file(manifest_path) != expected_manifest_sha:
        _issue(
            issues,
            "deployment_manifest_hash_mismatch",
            "Current deployment manifest SHA-256 does not match the pointer.",
        )
    if manifest.get("deployment_id") != deployment_id:
        _issue(issues, "deployment_manifest_id_mismatch", "Deployment manifest ID does not match pointer.")
    manifest_identity = manifest.get("identity")
    if not isinstance(manifest_identity, Mapping):
        _issue(issues, "deployment_manifest_identity_invalid", "Deployment manifest identity is missing.")
        return {
            "pointer_path": str(pointer_path),
            "deployment_id": deployment_id,
            "model_id": identity.get("model_id"),
            "manifest_path": str(manifest_path),
        }

    if manifest_identity.get("model_id") != champion_id:
        _issue(
            issues,
            "deployment_manifest_model_mismatch",
            "Deployment manifest model ID does not match registry champion.",
        )
    expected_model_sha = manifest_identity.get("model_sha256")
    if expected_model_sha != champion.get("artifact_sha256"):
        _issue(
            issues,
            "deployment_manifest_model_hash_mismatch",
            "Deployment manifest model SHA-256 does not match registry champion metadata.",
        )
    if not model_path.is_file():
        _issue(issues, "deployment_model_missing", f"Deployment model is missing: {model_path}")
    elif not isinstance(expected_model_sha, str) or sha256_file(model_path) != expected_model_sha:
        _issue(issues, "deployment_model_hash_mismatch", "Deployment model bytes failed SHA-256 verification.")

    return {
        "pointer_path": str(pointer_path),
        "deployment_id": deployment_id,
        "model_id": identity.get("model_id"),
        "manifest_sha256": expected_manifest_sha,
        "model_sha256": expected_model_sha,
        "manifest_path": str(manifest_path),
        "model_path": str(model_path),
    }


def _inspect_hot_platform(
    store: ContinualLearningStore,
    deployment: Mapping[str, Any],
    distribution_root: Path,
    platform: str,
    issues: List[Dict[str, str]],
) -> Dict[str, Any]:
    pointer_path = distribution_root / f"current-{platform}.json"
    pointer = _read_json_object(
        pointer_path,
        issues,
        f"hot_{platform}_pointer_missing_or_invalid",
    )
    result: Dict[str, Any] = {"platform": platform, "pointer_path": str(pointer_path)}
    if pointer is None:
        return result
    if pointer.get("schema_version") != HOT_BUNDLE_POINTER_SCHEMA_VERSION:
        _issue(
            issues,
            f"hot_{platform}_pointer_schema_mismatch",
            f"{platform} hot pointer schema is {pointer.get('schema_version')!r}; "
            f"expected {HOT_BUNDLE_POINTER_SCHEMA_VERSION}.",
        )
    _valid_identity_hash(
        pointer,
        issues,
        f"hot_{platform}_pointer_integrity_failure",
        f"{platform} hot pointer",
    )
    identity = pointer.get("identity")
    if not isinstance(identity, Mapping):
        return result

    result.update(
        {
            "deployment_id": identity.get("deployment_id"),
            "model_id": identity.get("model_id"),
            "bundle_sha256": identity.get("bundle_sha256"),
            "bundle_size_bytes": identity.get("bundle_size_bytes"),
        }
    )
    if identity.get("platform") != platform:
        _issue(issues, f"hot_{platform}_platform_mismatch", f"{platform} pointer names another platform.")
    if identity.get("deployment_id") != deployment.get("deployment_id"):
        _issue(
            issues,
            f"hot_{platform}_deployment_mismatch",
            f"{platform} hot pointer does not match the current deployment.",
        )
    if identity.get("model_id") != deployment.get("model_id"):
        _issue(
            issues,
            f"hot_{platform}_model_mismatch",
            f"{platform} hot pointer does not match the current deployment model.",
        )
    if identity.get("manifest_sha256") != deployment.get("manifest_sha256"):
        _issue(
            issues,
            f"hot_{platform}_manifest_mismatch",
            f"{platform} hot pointer manifest hash does not match current deployment.",
        )
    if identity.get("model_sha256") != deployment.get("model_sha256"):
        _issue(
            issues,
            f"hot_{platform}_model_hash_mismatch",
            f"{platform} hot pointer model hash does not match current deployment.",
        )
    if identity.get("policy_abi_version") != store.compatibility.policy_abi_version:
        _issue(issues, f"hot_{platform}_policy_abi_mismatch", f"{platform} hot pointer ABI mismatch.")
    if identity.get("policy_signature") != store.config.get("policy_signature"):
        _issue(
            issues,
            f"hot_{platform}_policy_signature_mismatch",
            f"{platform} hot pointer policy signature mismatch.",
        )

    bundle_path = _safe_distribution_path(distribution_root, identity.get("bundle_path"))
    if bundle_path is None:
        _issue(
            issues,
            f"hot_{platform}_bundle_path_invalid",
            f"{platform} hot pointer bundle_path is unsafe or malformed.",
        )
        return result
    result["bundle_path"] = str(bundle_path)
    expected_size = identity.get("bundle_size_bytes")
    expected_sha = identity.get("bundle_sha256")
    if not bundle_path.is_file():
        _issue(issues, f"hot_{platform}_bundle_missing", f"Hot bundle is missing: {bundle_path}")
        return result
    if not isinstance(expected_size, int) or isinstance(expected_size, bool) or bundle_path.stat().st_size != expected_size:
        _issue(
            issues,
            f"hot_{platform}_bundle_size_mismatch",
            f"{platform} hot bundle size does not match pointer metadata.",
        )
    if not isinstance(expected_sha, str) or sha256_file(bundle_path) != expected_sha:
        _issue(
            issues,
            f"hot_{platform}_bundle_hash_mismatch",
            f"{platform} hot bundle bytes failed SHA-256 verification.",
        )
    return result


def inspect_release_health(
    store: ContinualLearningStore,
    *,
    distribution_root: Optional[str | os.PathLike[str]] = None,
    platforms: Sequence[str] = (),
) -> Dict[str, Any]:
    """Return a deterministic read-only release-health snapshot."""
    store._require_initialized()
    issues: List[Dict[str, str]] = []
    champion_id = store.current_champion_id()
    champion: Optional[Dict[str, Any]] = None
    if not champion_id:
        _issue(issues, "champion_missing", "Registry has no current champion.")
    else:
        champion = store.get_model(champion_id)
        if champion.get("status") != "champion":
            _issue(issues, "champion_status_invalid", "Registry current champion is not in champion status.")
        expected = store.compatibility.to_dict()
        if any(champion.get(key) != value for key, value in expected.items()):
            _issue(issues, "champion_compatibility_mismatch", "Registry champion is incompatible with store policy.")
        artifact = Path(str(champion.get("artifact_path", ""))).expanduser().resolve()
        expected_sha = champion.get("artifact_sha256")
        if not artifact.is_file():
            _issue(issues, "champion_artifact_missing", f"Champion artifact is missing: {artifact}")
        elif not isinstance(expected_sha, str) or sha256_file(artifact) != expected_sha:
            _issue(issues, "champion_artifact_hash_mismatch", "Champion artifact failed SHA-256 verification.")

    deployment = _inspect_deployment(store, champion, issues) if champion is not None else None

    requested_platforms = list(dict.fromkeys(platforms))
    invalid_platforms = [value for value in requested_platforms if value not in SUPPORTED_PLATFORMS]
    if invalid_platforms:
        raise ValidationError("Unsupported release-health platform(s): " + ", ".join(invalid_platforms))
    if requested_platforms and distribution_root is None:
        raise ValidationError("distribution_root is required when checking hot-bundle platforms.")

    hot: List[Dict[str, Any]] = []
    if distribution_root is not None:
        root = Path(distribution_root).expanduser().resolve()
        if not requested_platforms:
            raise ValidationError(
                "At least one platform must be specified when distribution_root is provided."
            )
        if deployment is None:
            for platform in requested_platforms:
                _issue(
                    issues,
                    f"hot_{platform}_blocked_by_deployment",
                    f"Cannot validate {platform} hot distribution without a valid current deployment.",
                )
        else:
            for platform in requested_platforms:
                hot.append(_inspect_hot_platform(store, deployment, root, platform, issues))

    candidates = store.list_models(status="candidate")
    expected = store.compatibility.to_dict()
    compatible_candidates = [
        model for model in candidates if all(model.get(key) == value for key, value in expected.items())
    ]
    previous_champion_id = None
    with store._connect() as db:
        previous_champion_id = store._state(db, "previous_champion")

    return {
        "healthy": not issues,
        "issue_count": len(issues),
        "issues": issues,
        "current_champion_model_id": champion_id,
        "previous_champion_model_id": previous_champion_id,
        "current_deployment": deployment,
        "hot_platforms": hot,
        "queue": {
            "candidate_count": len(candidates),
            "compatible_candidate_count": len(compatible_candidates),
            "incompatible_candidate_count": len(candidates) - len(compatible_candidates),
            "rejected_count": len(store.list_models(status="rejected")),
            "historical_count": len(store.list_models(status="historical")),
        },
    }


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Verify Bees continual champion/deployment/hot-bundle release health without mutation."
    )
    parser.add_argument("--root", required=True, help="Initialized continual-learning store root.")
    parser.add_argument(
        "--config",
        default=str(Path(__file__).with_name("continual_learning_config.json")),
        help="Continual-learning configuration JSON.",
    )
    parser.add_argument(
        "--distribution-root",
        help="Optional BeesServer RL model distribution root containing current-<platform>.json.",
    )
    parser.add_argument(
        "--platform",
        action="append",
        default=[],
        choices=tuple(sorted(SUPPORTED_PLATFORMS)),
        help="Hot-distribution platform to verify; repeat for multiple platforms.",
    )
    return parser


def main(argv: Optional[Sequence[str]] = None) -> int:
    args = build_parser().parse_args(argv)
    try:
        store = ContinualLearningStore(args.root, load_config(args.config))
        result = inspect_release_health(
            store,
            distribution_root=args.distribution_root,
            platforms=args.platform,
        )
        print(json.dumps(result, indent=2, sort_keys=True, ensure_ascii=False))
        return 0 if result["healthy"] else 2
    except (ContinualLearningError, OSError, ValueError) as exc:
        print(f"Release-health inspection failed: {exc}", file=sys.stderr)
        return 3


if __name__ == "__main__":
    raise SystemExit(main())
