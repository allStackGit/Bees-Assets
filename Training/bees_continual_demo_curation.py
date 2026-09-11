"""Explicitly curate validated public Human demonstrations for behavioral cloning.

Authenticated public uploads remain untrusted after ingestion. This module adds a separate,
operator-controlled approval boundary and materializes only explicitly selected approved batches
into an immutable PolicyV<ABI>/Human directory that the existing continual trainer can validate
and snapshot normally.

Approvals and revocations are append-only immutable records. Materialization never changes the
source archive and never automatically selects every approved public batch.
"""

from __future__ import annotations

import argparse
import json
import math
import os
import re
import sys
from pathlib import Path
from typing import Any, Dict, Mapping, Optional, Sequence

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
from bees_continual_native_demo import _copy_immutable, _write_bytes_immutable


PUBLIC_DEMO_CURATION_SCHEMA_VERSION = 1
PUBLIC_DEMO_TRAINING_SET_SCHEMA_VERSION = 1
_CENTRAL_BATCH_ID = re.compile(r"^demo-[0-9a-f]{24}$")


def _required_text(value: object, label: str, maximum: int) -> str:
    if not isinstance(value, str) or not value.strip() or len(value.strip()) > maximum:
        raise ValidationError(f"{label} must be a non-empty string up to {maximum} characters.")
    return value.strip()


def _quality_score(value: object) -> float:
    if isinstance(value, bool):
        raise ValidationError("quality_score must be a finite number in [0,1].")
    try:
        score = float(value)
    except (TypeError, ValueError) as exc:
        raise ValidationError("quality_score must be a finite number in [0,1].") from exc
    if not math.isfinite(score) or score < 0.0 or score > 1.0:
        raise ValidationError("quality_score must be a finite number in [0,1].")
    return score


def _batch_id(value: object) -> str:
    batch_id = _required_text(value, "batch_id", 64)
    if not _CENTRAL_BATCH_ID.fullmatch(batch_id):
        raise ValidationError("batch_id must identify an archived native demonstration batch.")
    return batch_id


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


def _native_batch(store: ContinualLearningStore, batch_id: str) -> Mapping[str, object]:
    with store._connect() as db:
        row = db.execute(
            "SELECT * FROM demonstration_batches WHERE batch_id = ?",
            (batch_id,),
        ).fetchone()
    if row is None:
        raise ValidationError(f"Demonstration batch is not registered: {batch_id}")
    return dict(row)


def _archive_paths(store: ContinualLearningStore, batch_id: str) -> Mapping[str, Path]:
    root = store.experience_dir / "human-demos" / "native"
    return {
        "demo": root / f"{batch_id}.demo",
        "manifest": root / f"{batch_id}.capture-manifest.json",
        "metadata": root / f"{batch_id}.json",
    }


def _public_provenance(store: ContinualLearningStore, batch_id: str) -> Sequence[Mapping[str, object]]:
    directory = (
        store.experience_dir
        / "human-demos"
        / "public-quarantine-provenance"
        / batch_id
    )
    records = []
    if directory.is_dir():
        for path in sorted(directory.glob("*.json")):
            record = _read_json_object(path, "Public demonstration provenance")
            if (
                record.get("central_batch_id") != batch_id
                or record.get("source") != "Human"
                or record.get("source_trust") != "authenticated-quarantine"
                or record.get("native_structure_validated") is not True
                or record.get("approved_for_training") is not False
            ):
                raise ValidationError(
                    f"Public demonstration provenance is incompatible with curation: {path}"
                )
            records.append(record)
    if not records:
        raise ValidationError(
            f"Demonstration batch {batch_id} has no validated public-quarantine provenance."
        )
    return records


def _validated_archive(store: ContinualLearningStore, batch_id: str) -> Mapping[str, object]:
    row = _native_batch(store, batch_id)
    paths = _archive_paths(store, batch_id)
    demo = paths["demo"]
    manifest = paths["manifest"]
    metadata = paths["metadata"]
    if not demo.is_file() or not manifest.is_file() or not metadata.is_file():
        raise ValidationError(f"Native demonstration archive is incomplete for {batch_id}.")
    if Path(str(row["archive_path"])).resolve() != demo.resolve():
        raise ValidationError(f"Native demonstration registry/archive path mismatch for {batch_id}.")

    envelope = _read_json_object(metadata, "Native demonstration metadata")
    if envelope.get("payload_sha256") != row["payload_sha256"]:
        raise ValidationError(f"Native demonstration payload identity mismatch for {batch_id}.")
    native = envelope.get("native_demo")
    capture = envelope.get("capture_manifest")
    if not isinstance(native, dict) or not isinstance(capture, dict):
        raise ValidationError(f"Native demonstration metadata is incomplete for {batch_id}.")
    demo_hash = str(native.get("sha256", ""))
    manifest_hash = str(capture.get("sha256", ""))
    if sha256_file(demo) != demo_hash:
        raise ValidationError(f"Native demonstration archive hash mismatch for {batch_id}.")
    if sha256_file(manifest) != manifest_hash:
        raise ValidationError(f"Native capture-manifest archive hash mismatch for {batch_id}.")

    _public_provenance(store, batch_id)
    return {
        "row": row,
        "demo": demo,
        "manifest": manifest,
        "metadata": metadata,
        "envelope": envelope,
        "demo_sha256": demo_hash,
        "manifest_sha256": manifest_hash,
    }


def _decision_path(store: ContinualLearningStore, decision: str, batch_id: str) -> Path:
    return (
        store.experience_dir
        / "human-demos"
        / f"public-{decision}s"
        / f"{batch_id}.json"
    )


def _write_decision(path: Path, body: Mapping[str, object], stable_fields: Sequence[str]) -> Mapping[str, object]:
    payload = (json.dumps(body, indent=2, sort_keys=True, ensure_ascii=False) + "\n").encode("utf-8")
    if path.exists():
        existing = _read_json_object(path, "Public demonstration curation decision")
        if all(existing.get(key) == body.get(key) for key in stable_fields):
            return existing
        raise ContinualLearningError(
            f"Refusing to overwrite a different immutable public demonstration decision: {path}"
        )
    _write_bytes_immutable(path, payload)
    return dict(body)


def approve_public_batch(
    store: ContinualLearningStore,
    batch_id: str,
    *,
    reviewer: str,
    reason: str,
    quality_score: float,
) -> Mapping[str, object]:
    """Approve one validated public batch as eligible for explicit future training-set selection."""
    store._require_initialized()
    batch_id = _batch_id(batch_id)
    reviewer = _required_text(reviewer, "reviewer", 128)
    reason = _required_text(reason, "reason", 2048)
    score = _quality_score(quality_score)
    archive = _validated_archive(store, batch_id)

    if _decision_path(store, "revocation", batch_id).exists():
        raise ValidationError(f"Demonstration batch {batch_id} has been revoked and cannot be approved.")

    body = {
        "schema_version": PUBLIC_DEMO_CURATION_SCHEMA_VERSION,
        "decision": "approved",
        "batch_id": batch_id,
        "payload_sha256": archive["row"]["payload_sha256"],
        "demo_sha256": archive["demo_sha256"],
        "capture_manifest_sha256": archive["manifest_sha256"],
        "example_count": int(archive["row"]["example_count"]),
        "reviewer": reviewer,
        "reason": reason,
        "quality_score": score,
        "approved_at": utc_now(),
    }
    return _write_decision(
        _decision_path(store, "approval", batch_id),
        body,
        (
            "schema_version",
            "decision",
            "batch_id",
            "payload_sha256",
            "demo_sha256",
            "capture_manifest_sha256",
            "example_count",
            "reviewer",
            "reason",
            "quality_score",
        ),
    )


def revoke_public_batch(
    store: ContinualLearningStore,
    batch_id: str,
    *,
    reviewer: str,
    reason: str,
) -> Mapping[str, object]:
    """Permanently exclude an approved public batch from future materialized training sets."""
    store._require_initialized()
    batch_id = _batch_id(batch_id)
    reviewer = _required_text(reviewer, "reviewer", 128)
    reason = _required_text(reason, "reason", 2048)
    approval_path = _decision_path(store, "approval", batch_id)
    if not approval_path.is_file():
        raise ValidationError(f"Demonstration batch {batch_id} is not approved.")
    approval = _read_json_object(approval_path, "Public demonstration approval")
    body = {
        "schema_version": PUBLIC_DEMO_CURATION_SCHEMA_VERSION,
        "decision": "revoked",
        "batch_id": batch_id,
        "approved_payload_sha256": approval.get("payload_sha256"),
        "reviewer": reviewer,
        "reason": reason,
        "revoked_at": utc_now(),
    }
    return _write_decision(
        _decision_path(store, "revocation", batch_id),
        body,
        (
            "schema_version",
            "decision",
            "batch_id",
            "approved_payload_sha256",
            "reviewer",
            "reason",
        ),
    )


def _approved_archive(store: ContinualLearningStore, batch_id: str) -> Mapping[str, object]:
    approval_path = _decision_path(store, "approval", batch_id)
    if not approval_path.is_file():
        raise ValidationError(f"Demonstration batch {batch_id} has not been explicitly approved.")
    if _decision_path(store, "revocation", batch_id).exists():
        raise ValidationError(f"Demonstration batch {batch_id} has been revoked.")
    approval = _read_json_object(approval_path, "Public demonstration approval")
    archive = _validated_archive(store, batch_id)
    checks = {
        "payload_sha256": archive["row"]["payload_sha256"],
        "demo_sha256": archive["demo_sha256"],
        "capture_manifest_sha256": archive["manifest_sha256"],
        "example_count": int(archive["row"]["example_count"]),
    }
    if approval.get("schema_version") != PUBLIC_DEMO_CURATION_SCHEMA_VERSION or approval.get("decision") != "approved":
        raise ValidationError(f"Public demonstration approval is incompatible for {batch_id}.")
    for key, expected in checks.items():
        if approval.get(key) != expected:
            raise ValidationError(f"Public demonstration approval/archive mismatch for {batch_id}: {key}.")
    return {**archive, "approval": approval, "approval_path": approval_path}


def materialize_approved_training_set(
    store: ContinualLearningStore,
    batch_ids: Sequence[str],
) -> Mapping[str, object]:
    """Create an immutable trainer-compatible directory from explicitly selected approved batches."""
    store._require_initialized()
    if not batch_ids:
        raise ValidationError("At least one approved public demonstration batch must be selected.")
    normalized = sorted({_batch_id(value) for value in batch_ids})
    if len(normalized) != len(batch_ids):
        raise ValidationError("Public demonstration training-set selection contains duplicate batch IDs.")

    selected = []
    manifest_bytes: Optional[bytes] = None
    manifest_hash: Optional[str] = None
    for batch_id in normalized:
        archive = _approved_archive(store, batch_id)
        current_manifest = archive["manifest"].read_bytes()
        if manifest_bytes is None:
            manifest_bytes = current_manifest
            manifest_hash = str(archive["manifest_sha256"])
        elif current_manifest != manifest_bytes:
            raise ValidationError(
                "Approved public demonstration batches do not share the exact same capture manifest."
            )
        selected.append(archive)

    assert manifest_bytes is not None and manifest_hash is not None
    entries = []
    for batch_id, archive in zip(normalized, selected):
        approval_bytes = archive["approval_path"].read_bytes()
        entries.append(
            {
                "batch_id": batch_id,
                "payload_sha256": archive["row"]["payload_sha256"],
                "demo_sha256": archive["demo_sha256"],
                "example_count": int(archive["row"]["example_count"]),
                "approval_sha256": sha256_bytes(approval_bytes),
            }
        )

    identity = {
        "schema_version": PUBLIC_DEMO_TRAINING_SET_SCHEMA_VERSION,
        "source": "explicitly-approved-public-human-demonstrations",
        "policy_abi_version": store.compatibility.policy_abi_version,
        "capture_manifest_sha256": manifest_hash,
        "batches": entries,
    }
    identity_hash = sha256_bytes(canonical_json(identity).encode("utf-8"))
    set_root = (
        store.experience_dir
        / "human-training-sets"
        / f"public-approved-set-{identity_hash[:24]}"
    )
    policy_root = set_root / f"PolicyV{store.compatibility.policy_abi_version}"
    human_dir = policy_root / "Human"
    human_dir.mkdir(parents=True, exist_ok=True)

    created = []
    try:
        for batch_id, archive in zip(normalized, selected):
            target = human_dir / f"{batch_id}.demo"
            if _copy_immutable(archive["demo"], target, str(archive["demo_sha256"])):
                created.append(target)
        capture_target = policy_root / "capture-manifest.json"
        if _write_bytes_immutable(capture_target, manifest_bytes):
            created.append(capture_target)

        set_manifest = {
            **identity,
            "identity_sha256": identity_hash,
            "human_demo_dir": str(human_dir),
        }
        set_manifest_bytes = (
            json.dumps(set_manifest, indent=2, sort_keys=True, ensure_ascii=False) + "\n"
        ).encode("utf-8")
        set_manifest_path = set_root / "manifest.json"
        if _write_bytes_immutable(set_manifest_path, set_manifest_bytes):
            created.append(set_manifest_path)
    except Exception:
        for path in reversed(created):
            try:
                path.unlink()
            except FileNotFoundError:
                pass
        raise

    return {
        "training_set_id": f"public-approved-set-{identity_hash[:24]}",
        "identity_sha256": identity_hash,
        "batch_count": len(entries),
        "example_count": sum(int(entry["example_count"]) for entry in entries),
        "human_demo_dir": str(human_dir),
        "manifest_path": str(set_root / "manifest.json"),
    }


def _build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Approve/revoke validated public Human demos and materialize explicit BC training sets."
    )
    parser.add_argument("--root", required=True, help="Continual-learning store root.")
    parser.add_argument(
        "--config",
        default=str(Path(__file__).with_name("continual_learning_config.json")),
        help="Continual-learning configuration JSON.",
    )
    subparsers = parser.add_subparsers(dest="command", required=True)

    approve = subparsers.add_parser("approve", help="Approve one validated public batch.")
    approve.add_argument("batch_id")
    approve.add_argument("--reviewer", required=True)
    approve.add_argument("--reason", required=True)
    approve.add_argument("--quality-score", required=True, type=float)

    revoke = subparsers.add_parser("revoke", help="Revoke one previously approved public batch.")
    revoke.add_argument("batch_id")
    revoke.add_argument("--reviewer", required=True)
    revoke.add_argument("--reason", required=True)

    materialize = subparsers.add_parser(
        "materialize",
        help="Materialize only the explicitly listed approved batches into a trainer-compatible set.",
    )
    materialize.add_argument("batch_ids", nargs="+")
    return parser


def main(argv: Optional[Sequence[str]] = None) -> int:
    args = _build_parser().parse_args(argv)
    try:
        store = ContinualLearningStore(args.root, load_config(args.config))
        if args.command == "approve":
            result = approve_public_batch(
                store,
                args.batch_id,
                reviewer=args.reviewer,
                reason=args.reason,
                quality_score=args.quality_score,
            )
        elif args.command == "revoke":
            result = revoke_public_batch(
                store,
                args.batch_id,
                reviewer=args.reviewer,
                reason=args.reason,
            )
        else:
            result = materialize_approved_training_set(store, args.batch_ids)
        print(json.dumps(result, indent=2, sort_keys=True, ensure_ascii=False))
        return 0
    except ContinualLearningError as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
