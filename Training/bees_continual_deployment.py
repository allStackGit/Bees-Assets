"""Build immutable deployment packages only from the current Bees RL champion.

This is the first Phase 8 deployment boundary. It does not choose or promote models and it does not
change client networking. Instead it converts the continual registry's already-approved current
champion into a content-addressed package containing the exact ONNX bytes, frozen policy contract,
and promotion/bootstrap evidence. A small atomically replaced pointer identifies the package that a
downstream server/client rollout layer is allowed to distribute.
"""

from __future__ import annotations

import argparse
import json
import os
import sys
import tempfile
from pathlib import Path
from typing import Any, Callable, Dict, Mapping, Optional, Sequence

from bees_continual_behavior_sanity import validate_attached_behavior_sanity
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


DEPLOYMENT_PACKAGE_SCHEMA_VERSION = 1
DEPLOYMENT_POINTER_SCHEMA_VERSION = 1
DEPLOYMENT_DIRECTORY = "deployment"
DEPLOYMENT_MODEL_FILE = "model.onnx"
DEPLOYMENT_MANIFEST_FILE = "manifest.json"
CURRENT_DEPLOYMENT_FILE = "current-deployment.json"
OnnxValidator = Callable[[Path], None]


def _required_policy_signature(store: ContinualLearningStore) -> str:
    value = store.config.get("policy_signature")
    if not isinstance(value, str) or not value.strip():
        raise ValidationError(
            "Continual-learning config must define a non-empty policy_signature before a champion "
            "can be packaged for deployment."
        )
    return value.strip()


def _read_json_object(path: Path, label: str) -> Dict[str, Any]:
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except FileNotFoundError as exc:
        raise ValidationError(f"{label} does not exist: {path}") from exc
    except json.JSONDecodeError as exc:
        raise ValidationError(f"{label} is invalid JSON: {path}: {exc}") from exc
    if not isinstance(value, dict):
        raise ValidationError(f"{label} must contain a JSON object: {path}")
    return value


def _is_lower_hex(value: object, length: int) -> bool:
    return (
        isinstance(value, str)
        and len(value) == length
        and all(character in "0123456789abcdef" for character in value)
    )


def _validate_current_champion(store: ContinualLearningStore) -> Dict[str, Any]:
    store._require_initialized()
    champion_id = store.current_champion_id()
    if not champion_id:
        raise ValidationError("No current champion is available for deployment.")
    champion = store.get_model(champion_id)
    if champion.get("model_id") != champion_id or champion.get("status") != "champion":
        raise ValidationError(
            f"Current champion state is inconsistent for {champion_id}; deployment is blocked."
        )

    expected_compatibility = store.compatibility.to_dict()
    for key, expected in expected_compatibility.items():
        if champion.get(key) != expected:
            raise ValidationError(
                f"Current champion {champion_id} has incompatible {key}={champion.get(key)!r}; "
                f"expected {expected!r}."
            )

    artifact = Path(str(champion.get("artifact_path", ""))).expanduser().resolve()
    if artifact.suffix.lower() != ".onnx":
        raise ValidationError(
            f"Current champion deployment artifact must be ONNX, not {artifact.name!r}."
        )
    expected_hash = champion.get("artifact_sha256")
    if not _is_lower_hex(expected_hash, 64):
        raise ValidationError(f"Current champion {champion_id} has an invalid artifact SHA-256.")
    store._assert_artifact_hash(artifact, expected_hash)
    if artifact.stat().st_size <= 0:
        raise ValidationError(f"Current champion artifact is empty: {artifact}")
    return champion


def _default_onnx_validator(path: Path) -> None:
    """Require the release artifact to load through the same ONNX adapter used by evaluation."""
    try:
        from bees_continual_evaluate import EvaluationError, OnnxPolicy
    except ImportError as exc:
        raise ValidationError(
            "Champion deployment requires the project's evaluation/ONNX Runtime environment."
        ) from exc
    try:
        OnnxPolicy(path)
    except EvaluationError as exc:
        raise ValidationError(f"Champion ONNX deployment preflight failed: {exc}") from exc


def _evaluation_evidence(
    store: ContinualLearningStore,
    champion: Mapping[str, Any],
) -> Optional[Dict[str, Any]]:
    report_id = champion.get("evaluation_report_id")
    if report_id in (None, ""):
        return None
    if not isinstance(report_id, str) or not report_id.startswith("eval-"):
        raise ValidationError("Current champion has a malformed evaluation_report_id.")

    with store._connect() as db:
        row = db.execute(
            "SELECT candidate_model_id, passed, report_json FROM evaluations WHERE report_id = ?",
            (report_id,),
        ).fetchone()
    if row is None:
        raise ValidationError(f"Champion evaluation record is missing: {report_id}")
    if row["candidate_model_id"] != champion["model_id"] or not bool(row["passed"]):
        raise ValidationError(
            f"Champion evaluation {report_id} does not contain passing evidence for "
            f"{champion['model_id']}."
        )
    try:
        report = json.loads(row["report_json"])
    except (TypeError, json.JSONDecodeError) as exc:
        raise ValidationError(f"Champion evaluation payload is corrupted: {report_id}") from exc
    if not isinstance(report, dict):
        raise ValidationError(f"Champion evaluation payload must be an object: {report_id}")
    decision = report.get("decision")
    if not isinstance(decision, Mapping) or decision.get("passed") is not True:
        raise ValidationError(f"Champion evaluation decision is not passing: {report_id}")
    behavior_evidence = validate_attached_behavior_sanity(report)
    if behavior_evidence["passed"] is not True:
        raise ValidationError(
            f"Champion evaluation behavior-sanity gate is not passing: {report_id}"
        )
    expected_report_id = "eval-" + sha256_bytes(canonical_json(report).encode("utf-8"))[:24]
    if report_id != expected_report_id:
        raise ValidationError(
            f"Champion evaluation identity mismatch: stored {report_id}, computed {expected_report_id}."
        )

    report_path = store.evaluation_dir / "reports" / f"{report_id}.json"
    file_report = _read_json_object(report_path, "Champion evaluation report")
    if file_report != report:
        raise ValidationError(
            f"Champion evaluation file does not match registry payload: {report_path}"
        )
    policy_fingerprint = report.get("promotion_policy_fingerprint")
    if not _is_lower_hex(policy_fingerprint, 64):
        raise ValidationError(
            f"Champion evaluation {report_id} has invalid promotion-policy provenance."
        )
    return {
        "type": "passing-evaluation",
        "report_id": report_id,
        "report_sha256": sha256_file(report_path),
        "promotion_policy_fingerprint": policy_fingerprint,
    }


def _bootstrap_evidence(champion: Mapping[str, Any]) -> Optional[Dict[str, Any]]:
    metadata = champion.get("metadata")
    if not isinstance(metadata, Mapping):
        return None
    bootstrap = metadata.get("champion_bootstrap")
    if bootstrap is None:
        return None
    if not isinstance(bootstrap, Mapping):
        raise ValidationError("Generation-zero champion bootstrap metadata is malformed.")
    created_at = bootstrap.get("created_at")
    reason = bootstrap.get("reason")
    source_status = bootstrap.get("source_status")
    if (
        not isinstance(created_at, str)
        or not created_at.strip()
        or not isinstance(reason, str)
        or not reason.strip()
        or source_status != "candidate"
    ):
        raise ValidationError("Generation-zero champion bootstrap evidence is incomplete.")
    normalized = {
        "created_at": created_at,
        "reason": reason,
        "source_status": source_status,
    }
    return {
        "type": "generation-zero-bootstrap",
        **normalized,
        "evidence_sha256": sha256_bytes(canonical_json(normalized).encode("utf-8")),
    }


def _promotion_evidence(
    store: ContinualLearningStore,
    champion: Mapping[str, Any],
) -> Dict[str, Any]:
    evaluation = _evaluation_evidence(store, champion)
    bootstrap = _bootstrap_evidence(champion)
    if evaluation is not None and bootstrap is not None:
        raise ValidationError(
            "Current champion contains both generation-zero bootstrap and normal evaluation evidence."
        )
    if evaluation is not None:
        return evaluation
    if bootstrap is not None:
        return bootstrap
    raise ValidationError(
        f"Current champion {champion['model_id']} has neither passing evaluation evidence nor "
        "generation-zero bootstrap evidence."
    )


def build_current_champion_package(
    store: ContinualLearningStore,
    *,
    onnx_validator: Optional[OnnxValidator] = None,
) -> Dict[str, Any]:
    """Create or verify the deterministic package for the registry's current champion."""
    champion = _validate_current_champion(store)
    policy_signature = _required_policy_signature(store)
    evidence = _promotion_evidence(store, champion)
    source = Path(str(champion["artifact_path"])).expanduser().resolve()
    (onnx_validator or _default_onnx_validator)(source)
    artifact_size = source.stat().st_size

    identity = {
        "schema_version": DEPLOYMENT_PACKAGE_SCHEMA_VERSION,
        "model_id": champion["model_id"],
        "model_sha256": champion["artifact_sha256"],
        "model_size_bytes": artifact_size,
        "behavior_name": store.compatibility.behavior_name,
        "policy_signature": policy_signature,
        "compatibility": store.compatibility.to_dict(),
        "game_build_version": champion["game_build_version"],
        "training_run_id": champion["training_run_id"],
        "training_step": champion["training_step"],
        "promotion_evidence": evidence,
    }
    identity_sha256 = sha256_bytes(canonical_json(identity).encode("utf-8"))
    deployment_id = f"deploy-{identity_sha256[:24]}"
    package_dir = store.root / DEPLOYMENT_DIRECTORY / "packages" / deployment_id
    model_path = package_dir / DEPLOYMENT_MODEL_FILE
    manifest_path = package_dir / DEPLOYMENT_MANIFEST_FILE

    store._copy_immutable(source, model_path, champion["artifact_sha256"])
    manifest = {
        "schema_version": DEPLOYMENT_PACKAGE_SCHEMA_VERSION,
        "deployment_id": deployment_id,
        "identity_sha256": identity_sha256,
        "identity": identity,
        "model_file": DEPLOYMENT_MODEL_FILE,
    }
    store._write_json_immutable(manifest_path, manifest)
    store._assert_artifact_hash(model_path, champion["artifact_sha256"])

    return {
        "deployment_id": deployment_id,
        "model_id": champion["model_id"],
        "package_dir": str(package_dir),
        "model_path": str(model_path),
        "manifest_path": str(manifest_path),
        "manifest_sha256": sha256_file(manifest_path),
        "promotion_evidence_type": evidence["type"],
    }


def _write_json_atomic(path: Path, value: Mapping[str, Any]) -> None:
    payload = (json.dumps(value, indent=2, sort_keys=True, ensure_ascii=False) + "\n").encode(
        "utf-8"
    )
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


def publish_current_champion(
    store: ContinualLearningStore,
    *,
    onnx_validator: Optional[OnnxValidator] = None,
) -> Dict[str, Any]:
    """Publish an atomic pointer to the verified immutable current-champion package."""
    package = build_current_champion_package(store, onnx_validator=onnx_validator)
    policy_signature = _required_policy_signature(store)
    pointer_path = store.root / DEPLOYMENT_DIRECTORY / CURRENT_DEPLOYMENT_FILE
    identity = {
        "schema_version": DEPLOYMENT_POINTER_SCHEMA_VERSION,
        "deployment_id": package["deployment_id"],
        "model_id": package["model_id"],
        "manifest_sha256": package["manifest_sha256"],
        "policy_abi_version": store.compatibility.policy_abi_version,
        "policy_signature": policy_signature,
    }
    pointer_identity_sha256 = sha256_bytes(canonical_json(identity).encode("utf-8"))

    if pointer_path.exists():
        existing = _read_json_object(pointer_path, "Current deployment pointer")
        existing_identity = existing.get("identity")
        existing_hash = existing.get("identity_sha256")
        if not isinstance(existing_identity, Mapping) or not isinstance(existing_hash, str):
            raise ValidationError(f"Current deployment pointer is malformed: {pointer_path}")
        computed_existing = sha256_bytes(canonical_json(existing_identity).encode("utf-8"))
        if existing_hash != computed_existing:
            raise ValidationError(f"Current deployment pointer integrity check failed: {pointer_path}")
        if existing_identity == identity:
            return {
                **package,
                "pointer_path": str(pointer_path),
                "pointer_identity_sha256": pointer_identity_sha256,
                "pointer_changed": False,
            }

    pointer = {
        "schema_version": DEPLOYMENT_POINTER_SCHEMA_VERSION,
        "identity_sha256": pointer_identity_sha256,
        "identity": identity,
        "published_at": utc_now(),
    }
    _write_json_atomic(pointer_path, pointer)
    return {
        **package,
        "pointer_path": str(pointer_path),
        "pointer_identity_sha256": pointer_identity_sha256,
        "pointer_changed": True,
    }


def _build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Build/publish immutable deployment packages for the current Bees RL champion."
    )
    parser.add_argument("--root", required=True, help="Initialized continual-learning store root.")
    parser.add_argument(
        "--config",
        default=str(Path(__file__).with_name("continual_learning_config.json")),
        help="Continual-learning configuration JSON.",
    )
    parser.add_argument(
        "command",
        choices=("package", "publish"),
        help="package verifies/builds immutable bytes; publish also updates current-deployment.json.",
    )
    return parser


def main(argv: Optional[Sequence[str]] = None) -> int:
    args = _build_parser().parse_args(argv)
    try:
        store = ContinualLearningStore(args.root, load_config(args.config))
        result = (
            build_current_champion_package(store)
            if args.command == "package"
            else publish_current_champion(store)
        )
        print(json.dumps(result, indent=2, sort_keys=True, ensure_ascii=False))
        return 0
    except (ContinualLearningError, OSError, ValueError) as exc:
        print(f"Champion deployment failed: {exc}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
