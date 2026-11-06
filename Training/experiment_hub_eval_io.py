#!/usr/bin/env python3
"""Project-neutral bounded evaluation receipt file utilities."""

import json
import os
from pathlib import Path
import re
import tempfile


UUID = re.compile(r"[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}")
SUITE_ID = re.compile(r"[a-z][a-z0-9_-]{0,47}")
SHA256 = re.compile(r"[a-f0-9]{64}")


def read_bounded(path: Path, limit: int) -> bytes:
    with path.open("rb") as stream:
        data = stream.read(limit + 1)
    if not data or len(data) > limit:
        raise ValueError(f"unexpected file size: {path}")
    return data


def publish(directory: Path, receipt: dict) -> Path:
    destination = directory / f'{receipt["dispatch_id"]}.{receipt["suite_id"]}.json'
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
