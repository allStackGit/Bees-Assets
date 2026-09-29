"""Fail-closed ingestion for validated public live-RL telemetry.

The central store intentionally keeps its older trusted/local telemetry API backward compatible.
Public telemetry must enter through this module so malformed data, incompatible model identities,
and conflicting retries are rejected before they can be treated as archived experience.
"""

from __future__ import annotations

import copy
import json
from pathlib import Path
from typing import Any, Dict, Mapping

from bees_continual_learning import ContinualLearningStore, ValidationError, canonical_json
from bees_continual_live_telemetry import validate_live_telemetry_payload


def _canonical_payload(value: Mapping[str, Any]) -> str:
    return canonical_json(dict(value))


def _verify_duplicate_is_exact_retry(
    archived: Mapping[str, Any],
    payload: Mapping[str, Any],
) -> None:
    """Reject a reused match id unless the archived payload is byte-for-byte equivalent logically."""
    archive_path = archived.get("archive_path")
    if not isinstance(archive_path, str) or not archive_path.strip():
        raise ValidationError("Duplicate live telemetry is missing its immutable archive path.")
    path = Path(archive_path)
    try:
        existing = json.loads(path.read_text(encoding="utf-8"))
    except FileNotFoundError as exc:
        raise ValidationError(
            "Duplicate live telemetry points at a missing immutable archive."
        ) from exc
    except json.JSONDecodeError as exc:
        raise ValidationError(
            "Duplicate live telemetry points at a malformed immutable archive."
        ) from exc
    if not isinstance(existing, dict):
        raise ValidationError("Duplicate live telemetry archive must contain a JSON object.")
    if _canonical_payload(existing) != _canonical_payload(payload):
        raise ValidationError(
            "Live telemetry match_id already exists with different payload content."
        )


def ingest_live_telemetry_payload(
    store: ContinualLearningStore,
    payload: Mapping[str, Any],
) -> Dict[str, Any]:
    """Validate and immutably archive one public live-RL match payload.

    Validation runs against a private snapshot before any store mutation. Exact retries are
    idempotent. A reused match id with different content fails closed instead of inheriting the
    legacy trusted/local duplicate behavior.
    """
    if not isinstance(payload, Mapping):
        raise ValidationError("Live telemetry payload must be an object.")

    snapshot = copy.deepcopy(dict(payload))
    validation = validate_live_telemetry_payload(store, snapshot)
    archived = store.ingest_telemetry(snapshot)

    if archived.get("match_id") != validation["match_id"]:
        raise ValidationError("Archived live telemetry match identity changed during ingestion.")
    if bool(archived.get("trusted_for_on_policy_rl")):
        raise ValidationError("Public live telemetry must never be trusted for on-policy PPO updates.")
    if archived.get("duplicate") is True:
        _verify_duplicate_is_exact_retry(archived, snapshot)

    return {
        "validation": validation,
        "archive": dict(archived),
    }
