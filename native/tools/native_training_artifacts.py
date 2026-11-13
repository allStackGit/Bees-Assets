"""Shared standard-library artifact and learner-lineage checks for native training.

Both the one-round worker and the long-running session controller use these
checks. In particular the controller does not import NumPy/PyTorch simply to
inspect checkpoints or recover a completed round.
"""

import hashlib
import json
import os
from pathlib import Path


def file_sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()



def normalized_python_source_sha256(path: Path) -> str:
    """Fingerprint policy code independently of Git CRLF checkout policy.

    Windows and Linux may materialize the same Python source with different
    line endings. Normalize source line endings only: checkpoints, scenario
    JSON and captured binary data retain strict raw-byte identities.
    """
    data = path.read_bytes().replace(b"\\r\\n", b"\\n").replace(b"\\r", b"\\n")
    return hashlib.sha256(data).hexdigest()


def check_learner(manifest: Path, checkpoint: Path, slot: int | None) -> str:
    record = json.loads(manifest.read_text(encoding="utf-8"))
    if record.get("format") != "bees-native-model-v1" or not isinstance(record.get("models"), list):
        raise ValueError("native training requires a generated model manifest")
    digest = file_sha256(checkpoint)
    models = record["models"]
    matching = {
        item["slot"] for item in models
        if item["backend"] == "mlagents" and item["sha256"] == digest
    }
    if slot is None:
        if not matching or matching != {item["slot"] for item in models}:
            raise ValueError("mixed-checkpoint training requires --learner-slot")
    elif slot not in matching:
        raise ValueError("learner slot must identify the supplied input checkpoint")
    return digest


def sync_directory(path: Path) -> None:
    if os.name == "posix":
        directory = os.open(path, os.O_RDONLY | getattr(os, "O_DIRECTORY", 0))
        try:
            os.fsync(directory)
        finally:
            os.close(directory)

def atomic_json(path: Path, value) -> None:
    """Publish a derived progress snapshot or a completed output receipt.

    The final path appears only after fully writing and syncing its content.
    Interrupted .pending files are never interpreted as accepted work.
    """
    pending = path.with_name(path.name + ".pending")
    with pending.open("w", encoding="utf-8") as stream:
        json.dump(value, stream, indent=2)
        stream.write("\n")
        stream.flush()
        os.fsync(stream.fileno())
    os.replace(pending, path)
    sync_directory(path.parent)
