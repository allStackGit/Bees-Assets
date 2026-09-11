"""Privacy-preserving contributor grouping for public Human demonstrations.

The raw authenticated Steam ID remains in BeesServer quarantine. The continual-learning store keeps
only a store-local HMAC bucket so curation can bound how much one contributor supplies without
copying the raw account identifier into training provenance.
"""

from __future__ import annotations

import hashlib
import hmac
import json
import os
import re
import secrets
from pathlib import Path
from typing import Mapping, Sequence

from bees_continual_learning import ContinualLearningError, ContinualLearningStore, ValidationError
from bees_continual_native_demo import _write_bytes_immutable


PUBLIC_CONTRIBUTOR_SCHEMA_VERSION = 1
_CONTRIBUTOR_KEY_BYTES = 32
_SERVER_BATCH_ID = re.compile(r"^rl-demo-[0-9a-f]{32}$")
_CENTRAL_BATCH_ID = re.compile(r"^demo-[0-9a-f]{24}$")
_BUCKET = re.compile(r"^[0-9a-f]{64}$")


def _key_path(store: ContinualLearningStore) -> Path:
    return store.root / "metadata" / "public-demo-contributor.key"


def _read_key(path: Path) -> bytes:
    try:
        key = path.read_bytes()
    except FileNotFoundError as exc:
        raise ContinualLearningError(f"Public-demo contributor key disappeared: {path}") from exc
    if len(key) != _CONTRIBUTOR_KEY_BYTES:
        raise ContinualLearningError(
            f"Public-demo contributor key must be {_CONTRIBUTOR_KEY_BYTES} bytes: {path}"
        )
    return key


def _load_or_create_key(store: ContinualLearningStore) -> bytes:
    path = _key_path(store)
    path.parent.mkdir(parents=True, exist_ok=True)
    if path.exists():
        return _read_key(path)

    candidate = secrets.token_bytes(_CONTRIBUTOR_KEY_BYTES)
    flags = os.O_WRONLY | os.O_CREAT | os.O_EXCL
    try:
        fd = os.open(path, flags, 0o600)
    except FileExistsError:
        return _read_key(path)
    try:
        with os.fdopen(fd, "wb") as handle:
            handle.write(candidate)
            handle.flush()
            os.fsync(handle.fileno())
    except Exception:
        try:
            path.unlink()
        except FileNotFoundError:
            pass
        raise
    try:
        os.chmod(path, 0o600)
    except OSError:
        # Windows ACLs do not necessarily map to POSIX modes; the key still remains store-local.
        pass
    return candidate


def contributor_bucket(store: ContinualLearningStore, uploader_user_id: str) -> str:
    store._require_initialized()
    if not isinstance(uploader_user_id, str) or not uploader_user_id.isdigit():
        raise ValidationError("Public-demo uploader identity must be a numeric authenticated Steam ID.")
    key = _load_or_create_key(store)
    message = b"bees-public-demo-contributor-v1\0" + uploader_user_id.encode("ascii")
    return hmac.new(key, message, hashlib.sha256).hexdigest()


def _record_path(store: ContinualLearningStore, central_batch_id: str, server_batch_id: str) -> Path:
    return (
        store.experience_dir
        / "human-demos"
        / "public-contributors"
        / central_batch_id
        / f"{server_batch_id}.json"
    )


def write_public_contributor_record(
    store: ContinualLearningStore,
    *,
    central_batch_id: str,
    server_batch_id: str,
    uploader_user_id: str,
) -> Mapping[str, object]:
    if not _CENTRAL_BATCH_ID.fullmatch(str(central_batch_id)):
        raise ValidationError("Invalid central demonstration batch ID for contributor record.")
    if not _SERVER_BATCH_ID.fullmatch(str(server_batch_id)):
        raise ValidationError("Invalid server demonstration batch ID for contributor record.")
    bucket = contributor_bucket(store, uploader_user_id)
    body = {
        "schema_version": PUBLIC_CONTRIBUTOR_SCHEMA_VERSION,
        "central_batch_id": central_batch_id,
        "server_batch_id": server_batch_id,
        "contributor_bucket": bucket,
    }
    path = _record_path(store, central_batch_id, server_batch_id)
    payload = (json.dumps(body, indent=2, sort_keys=True, ensure_ascii=False) + "\n").encode("utf-8")
    _write_bytes_immutable(path, payload)
    return {**body, "path": str(path)}


def load_public_contributor_buckets(
    store: ContinualLearningStore,
    central_batch_id: str,
) -> Sequence[str]:
    if not _CENTRAL_BATCH_ID.fullmatch(str(central_batch_id)):
        raise ValidationError("Invalid central demonstration batch ID for contributor lookup.")
    directory = store.experience_dir / "human-demos" / "public-contributors" / central_batch_id
    if not directory.is_dir():
        raise ValidationError(
            f"Public demonstration batch {central_batch_id} has no contributor-bucket provenance."
        )

    buckets = set()
    for path in sorted(directory.glob("*.json")):
        try:
            value = json.loads(path.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError) as exc:
            raise ValidationError(f"Invalid public contributor record {path}: {exc}") from exc
        if not isinstance(value, dict):
            raise ValidationError(f"Public contributor record must be a JSON object: {path}")
        server_batch_id = value.get("server_batch_id")
        bucket = value.get("contributor_bucket")
        if (
            value.get("schema_version") != PUBLIC_CONTRIBUTOR_SCHEMA_VERSION
            or value.get("central_batch_id") != central_batch_id
            or not isinstance(server_batch_id, str)
            or path.stem != server_batch_id
            or not _SERVER_BATCH_ID.fullmatch(server_batch_id)
            or not isinstance(bucket, str)
            or not _BUCKET.fullmatch(bucket)
        ):
            raise ValidationError(f"Public contributor record is incompatible: {path}")
        buckets.add(bucket)
    if not buckets:
        raise ValidationError(
            f"Public demonstration batch {central_batch_id} has no contributor-bucket provenance."
        )
    return tuple(sorted(buckets))
