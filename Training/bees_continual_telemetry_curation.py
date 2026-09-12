"""Explicitly curate validated public live telemetry for offline scenario mining.

Public live telemetry remains untrusted after authenticated quarantine import. This module adds
an operator-controlled review boundary for selecting interesting matches for offline analysis and
scenario reconstruction. Approval never makes telemetry eligible for PPO/on-policy updates.
"""

from __future__ import annotations

import argparse
import json
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

PUBLIC_TELEMETRY_CURATION_SCHEMA_VERSION = 1
PUBLIC_TELEMETRY_SELECTION_SCHEMA_VERSION = 1
_CENTRAL_BATCH_ID = re.compile(r"^telemetry-[0-9a-f]{24}$")


def _required_text(value: object, label: str, maximum: int) -> str:
    if not isinstance(value, str) or not value.strip() or len(value.strip()) > maximum:
        raise ValidationError(f"{label} must be a non-empty string up to {maximum} characters.")
    return value.strip()


def _batch_id(value: object) -> str:
    batch_id = _required_text(value, "batch_id", 64)
    if not _CENTRAL_BATCH_ID.fullmatch(batch_id):
        raise ValidationError("batch_id must identify an archived live telemetry batch.")
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


def _telemetry_row(store: ContinualLearningStore, batch_id: str) -> Mapping[str, object]:
    with store._connect() as db:
        row = db.execute(
            "SELECT * FROM telemetry_batches WHERE batch_id = ?",
            (batch_id,),
        ).fetchone()
    if row is None:
        raise ValidationError(f"Live telemetry batch is not registered: {batch_id}")
    return dict(row)


def _public_provenance(store: ContinualLearningStore, batch_id: str) -> Sequence[Mapping[str, object]]:
    directory = (
        store.experience_dir
        / "raw-live"
        / "public-quarantine-provenance"
        / batch_id
    )
    records = []
    if directory.is_dir():
        for path in sorted(directory.glob("*.json")):
            record = _read_json_object(path, "Public telemetry provenance")
            if (
                record.get("central_batch_id") != batch_id
                or record.get("source_trust") != "authenticated-quarantine"
                or record.get("strict_live_schema_validated") is not True
                or record.get("trusted_for_on_policy_rl") is not False
            ):
                raise ValidationError(
                    f"Public telemetry provenance is incompatible with curation: {path}"
                )
            records.append(record)
    if not records:
        raise ValidationError(
            f"Live telemetry batch {batch_id} has no validated public-quarantine provenance."
        )
    return records


def _validated_archive(store: ContinualLearningStore, batch_id: str) -> Mapping[str, object]:
    row = _telemetry_row(store, batch_id)
    archive = Path(str(row["archive_path"])).expanduser().resolve()
    expected_archive = (store.experience_dir / "raw-live" / f"{batch_id}.json").resolve()
    if archive != expected_archive:
        raise ValidationError(f"Live telemetry registry/archive path mismatch for {batch_id}.")
    if not archive.is_file():
        raise ValidationError(f"Live telemetry archive is missing for {batch_id}.")
    payload_sha256 = str(row["payload_sha256"])
    payload = _read_json_object(archive, "Live telemetry archive")
    archive_payload_hash = sha256_bytes(canonical_json(payload).encode("utf-8"))
    if archive_payload_hash != payload_sha256:
        raise ValidationError(f"Live telemetry archive hash mismatch for {batch_id}.")
    if payload.get("match_id") != row["match_id"] or payload.get("model_id") != row["model_id"]:
        raise ValidationError(f"Live telemetry registry/archive identity mismatch for {batch_id}.")
    if bool(row["trusted_for_on_policy_rl"]):
        raise ValidationError(
            f"Public live telemetry {batch_id} must not be trusted for on-policy RL."
        )
    provenance = _public_provenance(store, batch_id)
    return {
        "row": row,
        "archive": archive,
        "payload": payload,
        "provenance": provenance,
        "payload_sha256": payload_sha256,
    }


def _decision_path(store: ContinualLearningStore, decision: str, batch_id: str) -> Path:
    return (
        store.experience_dir
        / "raw-live"
        / f"public-{decision}s"
        / f"{batch_id}.json"
    )


def _write_immutable_json(path: Path, body: Mapping[str, object]) -> None:
    ContinualLearningStore._write_json_immutable(path, body)


def approve_public_telemetry(
    store: ContinualLearningStore,
    batch_id: str,
    *,
    reviewer: str,
    reason: str,
    tags: Sequence[str] = (),
) -> Mapping[str, object]:
    """Approve one public telemetry match for explicit offline analysis/scenario selection."""
    store._require_initialized()
    batch_id = _batch_id(batch_id)
    reviewer = _required_text(reviewer, "reviewer", 128)
    reason = _required_text(reason, "reason", 2048)
    normalized_tags = sorted({_required_text(tag, "tag", 64) for tag in tags})
    if len(normalized_tags) != len(tags):
        raise ValidationError("Telemetry curation tags must not contain duplicates.")
    archive = _validated_archive(store, batch_id)
    if _decision_path(store, "revocation", batch_id).exists():
        raise ValidationError(f"Live telemetry batch {batch_id} has been revoked.")

    body = {
        "schema_version": PUBLIC_TELEMETRY_CURATION_SCHEMA_VERSION,
        "decision": "approved",
        "batch_id": batch_id,
        "match_id": archive["row"]["match_id"],
        "model_id": archive["row"]["model_id"],
        "payload_sha256": archive["payload_sha256"],
        "reviewer": reviewer,
        "reason": reason,
        "tags": normalized_tags,
        "approved_for_scenario_mining": True,
        "approved_for_on_policy_rl": False,
        "approved_at": utc_now(),
    }
    path = _decision_path(store, "approval", batch_id)
    if path.exists():
        existing = _read_json_object(path, "Public telemetry approval")
        stable = (
            "schema_version", "decision", "batch_id", "match_id", "model_id",
            "payload_sha256", "reviewer", "reason", "tags",
            "approved_for_scenario_mining", "approved_for_on_policy_rl",
        )
        if all(existing.get(key) == body.get(key) for key in stable):
            return existing
        raise ContinualLearningError(
            f"Refusing to overwrite a different immutable public telemetry approval: {path}"
        )
    _write_immutable_json(path, body)
    return body


def revoke_public_telemetry(
    store: ContinualLearningStore,
    batch_id: str,
    *,
    reviewer: str,
    reason: str,
) -> Mapping[str, object]:
    """Permanently exclude a previously approved public telemetry match from future selections."""
    store._require_initialized()
    batch_id = _batch_id(batch_id)
    reviewer = _required_text(reviewer, "reviewer", 128)
    reason = _required_text(reason, "reason", 2048)
    approval_path = _decision_path(store, "approval", batch_id)
    if not approval_path.is_file():
        raise ValidationError(f"Live telemetry batch {batch_id} is not approved.")
    approval = _read_json_object(approval_path, "Public telemetry approval")
    body = {
        "schema_version": PUBLIC_TELEMETRY_CURATION_SCHEMA_VERSION,
        "decision": "revoked",
        "batch_id": batch_id,
        "approved_payload_sha256": approval.get("payload_sha256"),
        "reviewer": reviewer,
        "reason": reason,
        "revoked_at": utc_now(),
    }
    path = _decision_path(store, "revocation", batch_id)
    if path.exists():
        existing = _read_json_object(path, "Public telemetry revocation")
        stable = (
            "schema_version", "decision", "batch_id", "approved_payload_sha256",
            "reviewer", "reason",
        )
        if all(existing.get(key) == body.get(key) for key in stable):
            return existing
        raise ContinualLearningError(
            f"Refusing to overwrite a different immutable public telemetry revocation: {path}"
        )
    _write_immutable_json(path, body)
    return body


def _approved_archive(store: ContinualLearningStore, batch_id: str) -> Mapping[str, object]:
    approval_path = _decision_path(store, "approval", batch_id)
    if not approval_path.is_file():
        raise ValidationError(f"Live telemetry batch {batch_id} is not explicitly approved.")
    if _decision_path(store, "revocation", batch_id).exists():
        raise ValidationError(f"Live telemetry batch {batch_id} has been revoked.")
    approval = _read_json_object(approval_path, "Public telemetry approval")
    archive = _validated_archive(store, batch_id)
    expected = {
        "schema_version": PUBLIC_TELEMETRY_CURATION_SCHEMA_VERSION,
        "decision": "approved",
        "batch_id": batch_id,
        "match_id": archive["row"]["match_id"],
        "model_id": archive["row"]["model_id"],
        "payload_sha256": archive["payload_sha256"],
        "approved_for_scenario_mining": True,
        "approved_for_on_policy_rl": False,
    }
    for key, value in expected.items():
        if approval.get(key) != value:
            raise ValidationError(f"Public telemetry approval/archive mismatch for {batch_id}: {key}.")
    return {**archive, "approval": approval, "approval_path": approval_path}


def materialize_scenario_selection(
    store: ContinualLearningStore,
    batch_ids: Sequence[str],
) -> Mapping[str, object]:
    """Create an immutable manifest of explicitly selected approved matches for offline mining."""
    store._require_initialized()
    if not batch_ids:
        raise ValidationError("At least one approved public telemetry batch must be selected.")
    normalized = sorted({_batch_id(value) for value in batch_ids})
    if len(normalized) != len(batch_ids):
        raise ValidationError("Public telemetry selection contains duplicate batch IDs.")

    entries = []
    for batch_id in normalized:
        archive = _approved_archive(store, batch_id)
        approval_sha256 = sha256_file(archive["approval_path"])
        entries.append(
            {
                "batch_id": batch_id,
                "match_id": archive["row"]["match_id"],
                "model_id": archive["row"]["model_id"],
                "payload_sha256": archive["payload_sha256"],
                "approval_sha256": approval_sha256,
                "tags": list(archive["approval"].get("tags", [])),
            }
        )

    identity = {
        "schema_version": PUBLIC_TELEMETRY_SELECTION_SCHEMA_VERSION,
        "purpose": "offline-scenario-mining",
        "approved_for_on_policy_rl": False,
        "batches": entries,
    }
    selection_hash = sha256_bytes(canonical_json(identity).encode("utf-8"))
    selection_id = f"telemetry-selection-{selection_hash[:24]}"
    destination = (
        store.experience_dir
        / "raw-live"
        / "public-scenario-selections"
        / selection_id
        / "manifest.json"
    )
    manifest = {
        **identity,
        "selection_id": selection_id,
        "created_at": utc_now(),
    }
    if destination.exists():
        existing = _read_json_object(destination, "Public telemetry scenario selection")
        comparable = dict(existing)
        comparable.pop("created_at", None)
        expected = dict(manifest)
        expected.pop("created_at", None)
        if comparable == expected:
            return existing
        raise ContinualLearningError(
            f"Refusing to overwrite a different immutable telemetry selection: {destination}"
        )
    _write_immutable_json(destination, manifest)
    return manifest


def _parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Curate validated public live telemetry for offline scenario mining."
    )
    parser.add_argument("--root", required=True, help="Continual-learning store root.")
    parser.add_argument("--config", default=None, help="Optional continual-learning config path.")
    sub = parser.add_subparsers(dest="command", required=True)

    approve = sub.add_parser("approve")
    approve.add_argument("batch_id")
    approve.add_argument("--reviewer", required=True)
    approve.add_argument("--reason", required=True)
    approve.add_argument("--tag", action="append", default=[])

    revoke = sub.add_parser("revoke")
    revoke.add_argument("batch_id")
    revoke.add_argument("--reviewer", required=True)
    revoke.add_argument("--reason", required=True)

    materialize = sub.add_parser("materialize")
    materialize.add_argument("batch_ids", nargs="+")

    return parser


def main(argv: Optional[Sequence[str]] = None) -> int:
    args = _parser().parse_args(argv)
    try:
        store = ContinualLearningStore(args.root, load_config(args.config))
        if args.command == "approve":
            result = approve_public_telemetry(
                store, args.batch_id, reviewer=args.reviewer, reason=args.reason, tags=args.tag
            )
        elif args.command == "revoke":
            result = revoke_public_telemetry(
                store, args.batch_id, reviewer=args.reviewer, reason=args.reason
            )
        else:
            result = materialize_scenario_selection(store, args.batch_ids)
        print(json.dumps(result, indent=2, sort_keys=True))
        return 0
    except ContinualLearningError as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
