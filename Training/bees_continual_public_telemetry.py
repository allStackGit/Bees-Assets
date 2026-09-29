"""Validate BeesServer live-RL telemetry quarantine before central archival.

BeesServer authenticates, rate-limits, hashes, and quarantines public telemetry uploads, but those
bytes remain untrusted player input. This importer independently verifies the server sidecar and
payload, checks the server batch identity, then delegates to the strict live-telemetry validator and
fail-closed central archive boundary.

The authenticated Steam user ID remains in BeesServer quarantine for abuse handling and is
intentionally not copied into the continual-learning experience store or provenance record. A
store-local HMAC contributor bucket is retained separately so later curation can prevent one
contributor from dominating scenario-mining selections.
"""

from __future__ import annotations

import argparse
import json
import os
import re
import sys
import tempfile
from pathlib import Path
from typing import Any, Dict, Mapping, Optional, Sequence

from bees_continual_learning import (
    ContinualLearningError,
    ContinualLearningStore,
    ValidationError,
    load_config,
    sha256_bytes,
    sha256_file,
)
from bees_continual_live_telemetry_ingest import ingest_live_telemetry_payload
from bees_continual_telemetry_contributors import write_public_telemetry_contributor_record


PUBLIC_TELEMETRY_QUARANTINE_SCHEMA_VERSION = 1
_BATCH_ID = re.compile(r"^rl-telemetry-[0-9a-f]{32}$")
_SHA256 = re.compile(r"^[0-9a-f]{64}$")
_DEPLOYMENT_ID = re.compile(r"^deploy-[0-9a-f]{24}$")


def _required_string(value: object, label: str, maximum: int = 4096) -> str:
    if not isinstance(value, str) or not value.strip() or len(value.strip()) > maximum:
        raise ValidationError(f"{label} must be a non-empty string up to {maximum} characters.")
    return value.strip()


def _read_json_object(path: Path, label: str) -> Dict[str, Any]:
    try:
        raw = json.loads(path.read_text(encoding="utf-8"))
    except FileNotFoundError as exc:
        raise ValidationError(f"{label} does not exist: {path}") from exc
    except json.JSONDecodeError as exc:
        raise ValidationError(f"{label} is invalid JSON: {path}: {exc}") from exc
    if not isinstance(raw, dict):
        raise ValidationError(f"{label} must contain a JSON object.")
    return raw


def _expected_server_batch_id(uploader_user_id: str, match_id: str) -> str:
    identity = f"{uploader_user_id}\n{match_id}\n".encode("utf-8")
    return f"rl-telemetry-{sha256_bytes(identity)[:32]}"


def validate_public_telemetry_quarantine(
    metadata_path: str | os.PathLike[str],
    *,
    max_payload_bytes: int,
) -> Mapping[str, object]:
    """Verify one immutable BeesServer telemetry quarantine pair without mutating the store."""
    metadata_file = Path(metadata_path).expanduser().resolve()
    metadata = _read_json_object(metadata_file, "Public telemetry quarantine metadata")

    if metadata.get("schemaVersion") != PUBLIC_TELEMETRY_QUARANTINE_SCHEMA_VERSION:
        raise ValidationError(
            "Public telemetry quarantine schemaVersion must be "
            f"{PUBLIC_TELEMETRY_QUARANTINE_SCHEMA_VERSION}."
        )
    batch_id = _required_string(metadata.get("batchId"), "batchId", 64)
    if not _BATCH_ID.fullmatch(batch_id):
        raise ValidationError("Public telemetry quarantine batchId is invalid.")
    if metadata_file.name != f"{batch_id}.metadata.json":
        raise ValidationError(
            f"Public telemetry metadata filename must be {batch_id}.metadata.json."
        )
    if metadata.get("trust") != "authenticated-quarantine":
        raise ValidationError(
            "Public telemetry quarantine must retain authenticated-quarantine trust state."
        )
    if metadata.get("readyForIngestion") is not False:
        raise ValidationError(
            "Public telemetry quarantine must not claim readyForIngestion before central validation."
        )

    uploader_user_id = _required_string(metadata.get("uploaderUserId"), "uploaderUserId", 32)
    if not uploader_user_id.isdigit():
        raise ValidationError(
            "Public telemetry uploaderUserId must be the authenticated numeric Steam identity."
        )
    match_id = _required_string(metadata.get("matchId"), "matchId", 256)
    if batch_id != _expected_server_batch_id(uploader_user_id, match_id):
        raise ValidationError(
            "Public telemetry batchId does not match its authenticated uploader/match identity."
        )

    game_build_version = _required_string(
        metadata.get("gameBuildVersion"), "gameBuildVersion", 256
    )
    payload_sha256 = _required_string(metadata.get("payloadSha256"), "payloadSha256", 64).lower()
    model_id = _required_string(metadata.get("modelId"), "modelId", 256)
    model_sha256 = _required_string(metadata.get("modelSha256"), "modelSha256", 64).lower()
    deployment_id = _required_string(metadata.get("deploymentId"), "deploymentId", 64)
    policy_signature = _required_string(
        metadata.get("policySignature"), "policySignature", 4096
    )
    if not _SHA256.fullmatch(payload_sha256) or not _SHA256.fullmatch(model_sha256):
        raise ValidationError("Public telemetry quarantine hashes must be lowercase SHA-256 values.")
    if not _DEPLOYMENT_ID.fullmatch(deployment_id):
        raise ValidationError("Public telemetry quarantine deploymentId is invalid.")

    payload_bytes = metadata.get("payloadBytes")
    if not isinstance(payload_bytes, int) or isinstance(payload_bytes, bool) or payload_bytes <= 0:
        raise ValidationError("Public telemetry payloadBytes must be a positive integer.")
    if payload_bytes > int(max_payload_bytes):
        raise ValidationError("Public telemetry quarantine payload exceeds configured ingestion size.")

    policy_abi_version = metadata.get("policyAbiVersion")
    if not isinstance(policy_abi_version, int) or isinstance(policy_abi_version, bool):
        raise ValidationError("Public telemetry policyAbiVersion must be an integer.")

    payload_path = metadata_file.with_name(f"{batch_id}.json")
    if not payload_path.is_file():
        raise ValidationError(f"Public telemetry quarantine payload is missing for {batch_id}.")
    actual_size = payload_path.stat().st_size
    if actual_size != payload_bytes:
        raise ValidationError(
            f"Public telemetry size mismatch for {batch_id}: metadata={payload_bytes}, file={actual_size}."
        )
    if sha256_file(payload_path) != payload_sha256:
        raise ValidationError(f"Public telemetry SHA-256 mismatch for {batch_id}.")

    payload = _read_json_object(payload_path, "Public telemetry payload")
    exact = {
        "match_id": match_id,
        "game_build_version": game_build_version,
        "model_id": model_id,
        "model_sha256": model_sha256,
        "deployment_id": deployment_id,
        "policy_abi_version": policy_abi_version,
        "policy_signature": policy_signature,
    }
    for key, expected in exact.items():
        if payload.get(key) != expected:
            raise ValidationError(
                f"Public telemetry payload {key} does not match authenticated quarantine metadata."
            )

    return {
        "metadata_path": metadata_file,
        "payload_path": payload_path,
        "batch_id": batch_id,
        "match_id": match_id,
        "uploader_user_id": uploader_user_id,
        "game_build_version": game_build_version,
        "payload_sha256": payload_sha256,
        "payload_bytes": payload_bytes,
        "model_id": model_id,
        "model_sha256": model_sha256,
        "deployment_id": deployment_id,
        "policy_abi_version": policy_abi_version,
        "policy_signature": policy_signature,
        "payload": payload,
    }


def _write_public_provenance(
    store: ContinualLearningStore,
    *,
    central_batch_id: str,
    quarantine: Mapping[str, object],
) -> Path:
    server_batch_id = str(quarantine["batch_id"])
    destination = (
        store.experience_dir
        / "raw-live"
        / "public-quarantine-provenance"
        / central_batch_id
        / f"{server_batch_id}.json"
    )
    body = {
        "schema_version": PUBLIC_TELEMETRY_QUARANTINE_SCHEMA_VERSION,
        "central_batch_id": central_batch_id,
        "server_batch_id": server_batch_id,
        "source_trust": "authenticated-quarantine",
        "strict_live_schema_validated": True,
        "trusted_for_on_policy_rl": False,
        "match_id": quarantine["match_id"],
        "game_build_version": quarantine["game_build_version"],
        "payload_sha256": quarantine["payload_sha256"],
        "model_id": quarantine["model_id"],
        "model_sha256": quarantine["model_sha256"],
        "deployment_id": quarantine["deployment_id"],
    }
    payload = (json.dumps(body, indent=2, sort_keys=True, ensure_ascii=False) + "\n").encode("utf-8")
    destination.parent.mkdir(parents=True, exist_ok=True)
    if destination.exists():
        if destination.read_bytes() != payload:
            raise ContinualLearningError(
                f"Refusing to overwrite conflicting public telemetry provenance: {destination}"
            )
        return destination

    fd, temp_name = tempfile.mkstemp(
        prefix=destination.name + ".",
        suffix=".tmp",
        dir=str(destination.parent),
    )
    try:
        with os.fdopen(fd, "wb") as handle:
            handle.write(payload)
            handle.flush()
            os.fsync(handle.fileno())
        os.replace(temp_name, destination)
    finally:
        try:
            os.unlink(temp_name)
        except FileNotFoundError:
            pass
    return destination


def ingest_public_telemetry_quarantine(
    store: ContinualLearningStore,
    metadata_path: str | os.PathLike[str],
) -> Mapping[str, object]:
    """Validate one authenticated public telemetry upload and archive it centrally as untrusted data."""
    store._require_initialized()
    quarantine = validate_public_telemetry_quarantine(
        metadata_path,
        max_payload_bytes=int(store.config["ingestion"]["max_payload_bytes"]),
    )
    result = dict(ingest_live_telemetry_payload(store, quarantine["payload"]))
    archive = result["archive"]
    if not isinstance(archive, Mapping):
        raise ContinualLearningError("Public telemetry central archive result is malformed.")
    central_batch_id = str(archive["batch_id"])
    provenance_path = _write_public_provenance(
        store,
        central_batch_id=central_batch_id,
        quarantine=quarantine,
    )
    contributor = write_public_telemetry_contributor_record(
        store,
        central_batch_id=central_batch_id,
        server_batch_id=str(quarantine["batch_id"]),
        uploader_user_id=str(quarantine["uploader_user_id"]),
    )
    result["public_quarantine_batch_id"] = quarantine["batch_id"]
    result["public_provenance_path"] = str(provenance_path)
    result["public_contributor_record_path"] = str(contributor["path"])
    return result


def _build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Validate/archive one authenticated BeesServer public live-RL telemetry batch."
    )
    parser.add_argument("--root", required=True, help="Continual-learning store root.")
    parser.add_argument(
        "--config",
        default=str(Path(__file__).with_name("continual_learning_config.json")),
        help="Continual-learning configuration JSON.",
    )
    parser.add_argument(
        "metadata",
        help="Path to BeesServer incoming/<batch>.metadata.json quarantine sidecar.",
    )
    return parser


def main(argv: Optional[Sequence[str]] = None) -> int:
    args = _build_parser().parse_args(argv)
    try:
        store = ContinualLearningStore(args.root, load_config(args.config))
        result = ingest_public_telemetry_quarantine(store, args.metadata)
        print(json.dumps(result, indent=2, sort_keys=True, ensure_ascii=False))
        return 0
    except ContinualLearningError as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
