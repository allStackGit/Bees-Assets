"""Validate authenticated BeesServer native-demo quarantine before central archival.

BeesServer authenticates/rate-limits public uploads and writes them into a quarantine inbox, but
those bytes are still untrusted player input. This module verifies the immutable server sidecar,
file hashes, exact capture manifest, and native ML-Agents structure before delegating to the
continual native-demo importer. It does not approve the resulting batch for behavioral cloning.

The raw authenticated Steam user ID remains server-side for abuse handling and is intentionally
not copied into the continual-learning store. A store-local HMAC contributor bucket is retained
separately so later curation can prevent one contributor from dominating a training set.
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
from typing import Any, Dict, Mapping, Optional, Sequence

from bees_continual_demo_contributors import write_public_contributor_record
from bees_continual_learning import (
    ContinualLearningError,
    ContinualLearningStore,
    ValidationError,
    load_config,
    sha256_file,
)
from bees_continual_native_demo import NativeDemoLoader, ingest_native_demonstration


PUBLIC_QUARANTINE_SCHEMA_VERSION = 1
_BATCH_ID = re.compile(r"^rl-demo-[0-9a-f]{32}$")
_SHA256 = re.compile(r"^[0-9a-f]{64}$")


def _required_string(value: object, label: str, maximum: int = 256) -> str:
    if not isinstance(value, str) or not value.strip() or len(value.strip()) > maximum:
        raise ValidationError(f"{label} must be a non-empty string up to {maximum} characters.")
    return value.strip()


def _read_quarantine_metadata(path: Path) -> Dict[str, Any]:
    try:
        raw = json.loads(path.read_text(encoding="utf-8"))
    except FileNotFoundError as exc:
        raise ValidationError(f"Public demonstration quarantine metadata does not exist: {path}") from exc
    except json.JSONDecodeError as exc:
        raise ValidationError(f"Public demonstration quarantine metadata is invalid JSON: {path}: {exc}") from exc
    if not isinstance(raw, dict):
        raise ValidationError("Public demonstration quarantine metadata must contain a JSON object.")
    return raw


def validate_quarantine_bundle(
    metadata_path: str | os.PathLike[str],
    *,
    max_payload_bytes: int,
) -> Mapping[str, object]:
    metadata_file = Path(metadata_path).expanduser().resolve()
    metadata = _read_quarantine_metadata(metadata_file)

    if metadata.get("schemaVersion") != PUBLIC_QUARANTINE_SCHEMA_VERSION:
        raise ValidationError(
            f"Public demonstration quarantine schemaVersion must be {PUBLIC_QUARANTINE_SCHEMA_VERSION}."
        )
    batch_id = _required_string(metadata.get("batchId"), "batchId", 64)
    if not _BATCH_ID.fullmatch(batch_id):
        raise ValidationError("Public demonstration quarantine batchId is invalid.")
    if metadata_file.stem != batch_id:
        raise ValidationError(
            f"Public demonstration metadata filename must match batchId {batch_id!r}."
        )
    if metadata.get("source") != "Human":
        raise ValidationError("Public demonstration quarantine source must be Human.")
    if metadata.get("trust") != "authenticated-quarantine":
        raise ValidationError(
            "Public demonstration quarantine must retain authenticated-quarantine trust state."
        )
    if metadata.get("readyForTraining") is not False:
        raise ValidationError(
            "Public demonstration quarantine must not claim readyForTraining before central validation."
        )

    uploader_user_id = _required_string(metadata.get("uploaderUserId"), "uploaderUserId")
    if not uploader_user_id.isdigit():
        raise ValidationError("Public demonstration uploaderUserId must be the authenticated numeric Steam identity.")
    demonstration_id = _required_string(metadata.get("demonstrationId"), "demonstrationId", 128)
    game_build_version = _required_string(metadata.get("gameBuildVersion"), "gameBuildVersion", 128)

    demo_sha256 = _required_string(metadata.get("demoSha256"), "demoSha256", 64).lower()
    manifest_sha256 = _required_string(metadata.get("manifestSha256"), "manifestSha256", 64).lower()
    if not _SHA256.fullmatch(demo_sha256) or not _SHA256.fullmatch(manifest_sha256):
        raise ValidationError("Public demonstration quarantine hashes must be hexadecimal SHA-256 values.")

    demo_bytes = metadata.get("demoBytes")
    if not isinstance(demo_bytes, int) or isinstance(demo_bytes, bool) or demo_bytes <= 0:
        raise ValidationError("Public demonstration demoBytes must be a positive integer.")

    demo_path = metadata_file.with_name(f"{batch_id}.demo")
    manifest_path = metadata_file.with_name(f"{batch_id}.capture-manifest.json")
    if not demo_path.is_file() or not manifest_path.is_file():
        raise ValidationError(
            f"Public demonstration quarantine bundle is incomplete for {batch_id}."
        )
    if demo_path.stat().st_size != demo_bytes:
        raise ValidationError(
            f"Public demonstration size mismatch for {batch_id}: metadata={demo_bytes}, "
            f"file={demo_path.stat().st_size}."
        )
    total_bytes = demo_path.stat().st_size + manifest_path.stat().st_size
    if total_bytes > int(max_payload_bytes):
        raise ValidationError("Public demonstration quarantine bundle exceeds configured ingestion size.")
    if sha256_file(demo_path) != demo_sha256:
        raise ValidationError(f"Public demonstration SHA-256 mismatch for {batch_id}.")
    if sha256_file(manifest_path) != manifest_sha256:
        raise ValidationError(f"Public demonstration capture-manifest SHA-256 mismatch for {batch_id}.")

    try:
        manifest_file_value = json.loads(manifest_path.read_text(encoding="utf-8"))
    except json.JSONDecodeError as exc:
        raise ValidationError(
            f"Public demonstration capture manifest is invalid JSON for {batch_id}: {exc}"
        ) from exc
    embedded_manifest = metadata.get("manifest")
    if not isinstance(embedded_manifest, dict) or manifest_file_value != embedded_manifest:
        raise ValidationError(
            f"Public demonstration capture manifest does not match its authenticated quarantine sidecar for {batch_id}."
        )

    return {
        "metadata_path": metadata_file,
        "demo_path": demo_path,
        "manifest_path": manifest_path,
        "batch_id": batch_id,
        "demonstration_id": demonstration_id,
        "uploader_user_id": uploader_user_id,
        "game_build_version": game_build_version,
        "demo_sha256": demo_sha256,
        "manifest_sha256": manifest_sha256,
        "manifest": embedded_manifest,
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
        / "human-demos"
        / "public-quarantine-provenance"
        / central_batch_id
        / f"{server_batch_id}.json"
    )
    body = {
        "schema_version": PUBLIC_QUARANTINE_SCHEMA_VERSION,
        "central_batch_id": central_batch_id,
        "server_batch_id": server_batch_id,
        "source": "Human",
        "source_trust": "authenticated-quarantine",
        "native_structure_validated": True,
        "approved_for_training": False,
        "game_build_version": quarantine["game_build_version"],
        "demo_sha256": quarantine["demo_sha256"],
        "manifest_sha256": quarantine["manifest_sha256"],
    }
    payload = (json.dumps(body, indent=2, sort_keys=True, ensure_ascii=False) + "\n").encode("utf-8")
    destination.parent.mkdir(parents=True, exist_ok=True)
    if destination.exists():
        if destination.read_bytes() != payload:
            raise ContinualLearningError(
                f"Refusing to overwrite conflicting public demonstration provenance: {destination}"
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


def ingest_public_quarantine(
    store: ContinualLearningStore,
    metadata_path: str | os.PathLike[str],
    *,
    model_id: str,
    loader: Optional[NativeDemoLoader] = None,
) -> Mapping[str, object]:
    """Validate one authenticated public upload and archive its native demonstration centrally.

    The required model_id is supplied by the operator because the current public client capture
    manifest identifies the policy ABI, not the immutable deployed champion ID.
    """
    store._require_initialized()
    quarantine = validate_quarantine_bundle(
        metadata_path,
        max_payload_bytes=int(store.config["ingestion"]["max_payload_bytes"]),
    )

    with tempfile.TemporaryDirectory(prefix="bees-public-demo-") as temp_dir:
        policy_root = Path(temp_dir) / f"PolicyV{store.compatibility.policy_abi_version}"
        human_dir = policy_root / "Human"
        human_dir.mkdir(parents=True)
        staged_demo = human_dir / f"human-public-{quarantine['batch_id']}.demo"
        staged_manifest = policy_root / "capture-manifest.json"
        shutil.copy2(quarantine["demo_path"], staged_demo)
        shutil.copy2(quarantine["manifest_path"], staged_manifest)

        result = dict(ingest_native_demonstration(
            store,
            staged_demo,
            demonstration_id=str(quarantine["batch_id"]),
            model_id=model_id,
            game_build_version=str(quarantine["game_build_version"]),
            loader=loader,
        ))

    provenance_path = _write_public_provenance(
        store,
        central_batch_id=str(result["batch_id"]),
        quarantine=quarantine,
    )
    contributor = write_public_contributor_record(
        store,
        central_batch_id=str(result["batch_id"]),
        server_batch_id=str(quarantine["batch_id"]),
        uploader_user_id=str(quarantine["uploader_user_id"]),
    )
    result["public_quarantine_batch_id"] = quarantine["batch_id"]
    result["public_provenance_path"] = str(provenance_path)
    result["public_contributor_record_path"] = str(contributor["path"])
    result["approved_for_training"] = False
    return result


def _build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Validate/archive one authenticated BeesServer public native demonstration quarantine batch."
    )
    parser.add_argument("--root", required=True, help="Continual-learning store root.")
    parser.add_argument(
        "--config",
        default=str(Path(__file__).with_name("continual_learning_config.json")),
        help="Continual-learning configuration JSON.",
    )
    parser.add_argument("metadata", help="Path to BeesServer incoming/<batch>.json quarantine sidecar.")
    parser.add_argument("--model-id", required=True, help="Compatible deployed model context for this capture batch.")
    return parser


def main(argv: Optional[Sequence[str]] = None) -> int:
    args = _build_parser().parse_args(argv)
    try:
        store = ContinualLearningStore(args.root, load_config(args.config))
        result = ingest_public_quarantine(
            store,
            args.metadata,
            model_id=args.model_id,
        )
        print(json.dumps(result, indent=2, sort_keys=True, ensure_ascii=False))
        return 0
    except ContinualLearningError as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
