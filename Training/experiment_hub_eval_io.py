#!/usr/bin/env python3
"""Project-neutral bounded evaluation receipt file utilities."""

import hashlib
import http.client
import json
import os
from pathlib import Path
import re
import tempfile


UUID = re.compile(r"[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}")
SUITE_ID = re.compile(r"[a-z][a-z0-9_-]{0,47}")
SHA256 = re.compile(r"[a-f0-9]{64}")


def suite_digest(suite: dict) -> str:
    return hashlib.sha256(json.dumps(suite, sort_keys=True).encode()).hexdigest()


def read_bounded(path: Path, limit: int) -> bytes:
    with path.open("rb") as stream:
        data = stream.read(limit + 1)
    if not data or len(data) > limit:
        raise ValueError(f"unexpected file size: {path}")
    return data


def publish(directory: Path, receipt: dict, *, suffix: str = ".json") -> Path:
    if suffix not in (".json", ".failed.json"):
        raise ValueError("Invalid independent evaluation publication suffix.")
    destination = directory / f'{receipt["dispatch_id"]}.{receipt["suite_id"]}{suffix}'
    fd, pending = tempfile.mkstemp(prefix=".hub-evaluation-receipt-", dir=directory)
    try:
        with os.fdopen(fd, "w", encoding="utf-8") as stream:
            json.dump(receipt, stream, separators=(",", ":"))
            stream.write("\n")
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(pending, destination)
    finally:
        Path(pending).unlink(missing_ok=True)
    return destination


def import_historical_audit(request: dict, port: int = 7148) -> dict:
    """Explicitly publish a completed offline audit to the existing Hub archive.

    The native job registry and training authority are not involved. Retain
    the caller's already-fsynced report when the Hub is unavailable so the
    operator can import it later without rerunning model evaluations.
    """
    if type(port) is not int or not 1 <= port <= 65535:
        raise ValueError("Invalid local Experiment Hub port.")
    body = json.dumps(request, separators=(",", ":"), allow_nan=False).encode("utf-8")
    if not body or len(body) > 65536:
        raise ValueError(
            "Historical import request exceeds the Hub API limit; "
            "split the checkpoint set into smaller audits.")
    connection = http.client.HTTPConnection("127.0.0.1", port, timeout=20)
    try:
        connection.request(
            "POST", "/api/hub/evaluation/historical", body=body,
            headers={"Content-Type": "application/json"},
        )
        with connection.getresponse() as response:
            result = response.read(65537)
            if len(result) > 65536:
                raise RuntimeError("Hub historical import response exceeded 64 KiB.")
            if response.status not in (200, 201):
                raise RuntimeError(
                    f"Hub historical import failed (HTTP {response.status}): "
                    + result.decode("utf-8", errors="replace"))
            accepted = json.loads(result)
            if (not isinstance(accepted, dict) or
                    not isinstance(accepted.get("job_id"), str) or
                    type(accepted.get("record_count")) is not int or
                    accepted["record_count"] < 1):
                raise ValueError("Hub historical import acknowledgement is invalid.")
            return accepted
    finally:
        connection.close()
