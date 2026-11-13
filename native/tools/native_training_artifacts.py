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
    data = path.read_bytes().replace(b"\r\n", b"\n").replace(b"\r", b"\n")
    return hashlib.sha256(data).hexdigest()


def native_source_contract_sha256(path: Path) -> str:
    """Content identity for a native collector's *physical* training task.

    A learner on Linux and a collector on Windows cannot share literal
    `python`, `policy_worker`, scene or output paths. Hash the exact
    source configuration with only those machine-local locations replaced
    by the immutable content they select. Inference weights are independently
    bound by the manifest digest in the distributed collection work order.
    Reject missing files rather than trusting path spellings or empty hashes.
    """
    path = path.resolve(strict=True)
    source = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(source, dict) or not isinstance(source.get("levels"), list) or not source["levels"]:
        raise ValueError("native source contract requires explicit physical Levels")
    if (source.get("hivemind") is not None or source.get("model_updates") or
            source.get("recover_notifications") or
            source.get("recover_outcome_journals") or
            "rollout_bundles" in source):
        raise ValueError("distributed native source contract requires fresh model-only Levels")

    file_cache = {}
    def sha_file(value, *, python_source=False, json_source=False):
        if not isinstance(value, str) or not value:
            raise ValueError("native source contract requires a real local source file")
        file = Path(value)
        if not file.is_absolute():
            file = path.parent / file
        file = file.resolve(strict=True)
        if file not in file_cache:
            file_cache[file] = {}
        kind = "python" if python_source else "json" if json_source else "bytes"
        if kind not in file_cache[file]:
            if python_source:
                digest = normalized_python_source_sha256(file)
            elif json_source:
                # Git CRLF checkouts, indentation and field order must not
                # prevent equivalent native Stage/scene JSON from sharing
                # one learning task. Hash the parsed source values exactly.
                document = json.loads(file.read_text(encoding="utf-8"))
                packed = json.dumps(document, sort_keys=True, separators=(",", ":"))
                digest = hashlib.sha256(packed.encode("utf-8")).hexdigest()
            else:
                digest = file_sha256(file)
            file_cache[file][kind] = digest
        return file_cache[file][kind]

    # Output journals, interpreter installation, and model locations are
    # execution plumbing. No gameplay/simulation property is dropped.
    portable = {
        key: value for key, value in source.items()
        if key not in (
            "python", "policy_worker", "model_manifest",
            "connection_checkpoint", "notifications", "rollout_summary",
            "policy_capture",
        )
    }
    portable["policy_worker_source_sha256"] = sha_file(
        source.get("policy_worker"), python_source=True,
    )
    for key in ("collision_matrix", "stage_palette"):
        if key in portable and portable[key] is not None:
            portable[key] = {"source_sha256": sha_file(portable[key], json_source=True)}
    portable["levels"] = []
    for level in source["levels"]:
        if not isinstance(level, dict) or not isinstance(level.get("scene"), str):
            raise ValueError("native source contract requires every Level scene")
        row = dict(level)
        row["scene"] = {"source_sha256": sha_file(level["scene"], json_source=True)}
        portable["levels"].append(row)
    payload = json.dumps(portable, sort_keys=True, separators=(",", ":"))
    return hashlib.sha256(payload.encode("utf-8")).hexdigest()


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

if __name__ == "__main__":
    # Narrow operator/Hub probe: standard-library-only and no GPU/training
    # initialization. The same canonical function owns work orders and
    # collector receipts. Print only the validated physical-task SHA-256.
    import argparse
    parser = argparse.ArgumentParser(description="Native Bees source contract identity")
    parser.add_argument("source_contract", type=Path)
    arguments = parser.parse_args()
    print(native_source_contract_sha256(arguments.source_contract), flush=True)
