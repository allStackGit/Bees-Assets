"""Shared standard-library artifact and learner-lineage checks for native training.

Both the one-round worker and the long-running session controller use these
checks. In particular the controller does not import NumPy/PyTorch simply to
inspect checkpoints or recover a completed round.
"""

import hashlib
import json
import math
import os
from pathlib import Path


def file_sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()




def native_input_dependencies(*runs):
    """Pin the original physical scenes, Stage files and inference code.

    One dependency list is shared by native training sessions and direct
    holdout evaluations. It identifies immutable source bytes, not
    machine-local output paths, mutable checkpoint artifacts or policy
    weights (which the native manifest/worker already verify).
    """
    paths = set()
    for source in runs:
        if source is None:
            continue
        base = Path(source["path"]).parent
        config = json.loads(Path(source["path"]).read_text(encoding="utf-8"))
        if not isinstance(config, dict) or not isinstance(config.get("levels"), list):
            raise ValueError("native source run must contain Levels")
        for level in config["levels"]:
            if not isinstance(level, dict) or not isinstance(level.get("scene"), str) or not level["scene"]:
                raise ValueError("native source Level has no scene path")
            paths.add((base / level["scene"]).resolve(strict=True))
        for key in ("policy_worker", "collision_matrix", "stage_palette"):
            value = config.get(key)
            if value is None:
                continue
            if not isinstance(value, str) or not value:
                raise ValueError(f"native source has an invalid {key} path")
            paths.add((base / value).resolve(strict=True))
    return [{"path": str(path), "sha256": file_sha256(path)}
            for path in sorted(paths)]


def check_native_input_dependencies(recipe):
    for item in recipe.get("source_dependencies", []):
        if file_sha256(Path(item["path"])) != item["sha256"]:
            raise ValueError(f"native source changed during run: {item['path']}")


def normalized_python_source_sha256(path: Path) -> str:
    """Fingerprint policy code independently of Git CRLF checkout policy.

    Windows and Linux may materialize the same Python source with different
    line endings. Normalize source line endings only: checkpoints, scenario
    JSON and captured binary data retain strict raw-byte identities.
    """
    data = path.read_bytes().replace(b"\r\n", b"\n").replace(b"\r", b"\n")
    return hashlib.sha256(data).hexdigest()


def native_source_contract_sha256(path: Path, *, include_allocation: bool = False) -> str:
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
    execution_only = {
        "python", "policy_worker", "model_manifest",
        "connection_checkpoint", "notifications", "rollout_summary",
        "policy_capture",
    }
    if not include_allocation:
        # The v2 identity excludes collector capacity. Each work order
        # separately pins World indexes, processes and physics workers.
        execution_only.update((
            "rollout_worlds", "rollout_world_start", "rollout_workers",
            "physics_workers",
        ))
    # Old in-flight orders used the v1 hash, which included those resource
    # fields. Allow checking that historical value without changing it.
    portable = {
        key: value for key, value in source.items()
        if key not in execution_only
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
        # A source with policy_gameplay=true has no frozen MA-POCA reward
        # denominators, even though a real opponent model is present.
        # Distributed collectors must reject it before any work orders
        # can identify or dispatch apparently trainable experiences.
        scene_path = Path(level["scene"])
        if not scene_path.is_absolute():
            scene_path = path.parent / scene_path
        scene_data = json.loads(scene_path.read_text(encoding="utf-8"))
        if not isinstance(scene_data, dict) or scene_data.get("policy_gameplay") is True:
            raise ValueError("distributed native training requires a frozen policy episode")
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


# Source scenes are parsed only at worker startup. These counters capture
# actual avoided episode reconstruction and are portable across Linux/
# Windows and between single-process, sharded and remote collectors.
SCENE_CACHE_COUNT_FIELDS = (
    "distinct_source_documents", "distinct_parsed_layouts",
    "worlds_initialized", "episodes_recycled", "source_parse_count",
    "recycle_source_parses_avoided",
)
SCENE_CACHE_TIME_FIELDS = (
    "source_parse_wall_seconds", "initial_template_clone_wall_seconds",
    "recycled_template_clone_wall_seconds",
)


def verified_source_scene_cache(value, worlds: int, episodes: int):
    """Retain measured template reuse without fabricating old-run data."""
    if value is None:
        return None
    if (not isinstance(value, dict) or
            any(type(value.get(key)) is not int or value[key] < 0
                for key in SCENE_CACHE_COUNT_FIELDS) or
            any(type(value.get(key)) not in (float, int) or
                not math.isfinite(value[key]) or value[key] < 0
                for key in SCENE_CACHE_TIME_FIELDS) or
            value["worlds_initialized"] != worlds or
            value["episodes_recycled"] != episodes - worlds or
            value["recycle_source_parses_avoided"] != value["episodes_recycled"] or
            not 1 <= value["distinct_source_documents"] <=
                value["distinct_parsed_layouts"] <= worlds or
            value["source_parse_count"] != value["distinct_parsed_layouts"]):
        raise ValueError("native source-scene cache counters contradict completed Worlds")
    return {key: value[key] for key in
            (*SCENE_CACHE_COUNT_FIELDS, *SCENE_CACHE_TIME_FIELDS)}


def combined_source_scene_cache(rows):
    """Aggregate per-process CPU work, never claim globally unique scenes."""
    if not rows or not all("source_scene_template_cache" in row for row in rows):
        return None
    return {
        "scope": "sum_of_process_local_source_work",
        **{key: sum(row["source_scene_template_cache"][key] for row in rows)
           for key in (*SCENE_CACHE_COUNT_FIELDS, *SCENE_CACHE_TIME_FIELDS)},
    }


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
    parser.add_argument("--include-allocation", action="store_true",
                        help="read legacy v1 portable source identity")
    arguments = parser.parse_args()
    print(native_source_contract_sha256(
        arguments.source_contract, include_allocation=arguments.include_allocation,
    ), flush=True)
