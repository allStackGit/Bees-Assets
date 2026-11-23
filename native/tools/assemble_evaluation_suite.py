#!/usr/bin/env python3
"""Assemble one native fixed-seed evaluation across different real game fleets.

Each Level is a complete original CombatWorld scene, with its own actor roster,
mission, reinforcement plan and seeds. No extra gameplay or policy loop is
created: evaluate_policy.py consumes the resulting ordinary multi-Level Run.
"""

import argparse
import copy
import hashlib
import json
import os
from pathlib import Path
import shutil
import tempfile

from native_training_artifacts import atomic_json, file_sha256, sync_directory


PER_RUN = {
    "run_id", "stage_name", "levels", "model_manifest", "notifications",
    "connection_checkpoint", "rollout_summary", "policy_capture",
    "host_recovery_checkpoint", "next_squad_item_id",
}
PATH_FIELDS = ("policy_worker", "collision_matrix", "stage_palette")


def load(path: Path) -> dict:
    value = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(value, dict) or not isinstance(value.get("levels"), list) or not value["levels"]:
        raise ValueError(f"{path}: native evaluation requires authored Levels")
    if value.get("hivemind") is not None or value.get("model_updates") or any(
        key in value for key in ("rollout_bundles", "rollout_workers",
                                "rollout_worlds", "rollout_world_start")
    ) or value.get("recover_notifications") or value.get("recover_outcome_journals"):
        raise ValueError(f"{path}: evaluation cannot inherit live network/training/recovery state")
    if type(value.get("episodes_per_world")) is not int or not 1 <= value["episodes_per_world"] <= 10000:
        raise ValueError(f"{path}: invalid episode count")
    if type(value.get("max_rounds")) is not int or value["max_rounds"] <= 0:
        raise ValueError(f"{path}: missing physical maximum round budget")
    if type(value.get("next_squad_item_id")) is not int or value["next_squad_item_id"] <= 0:
        raise ValueError(f"{path}: missing squad allocator identity")
    if not isinstance(value.get("policy_worker"), str) or not value["policy_worker"]:
        raise ValueError(f"{path}: missing native shared policy worker")
    if not isinstance(value.get("python"), str) or not value["python"]:
        raise ValueError(f"{path}: missing policy Python runtime")
    if not isinstance(value.get("run_id"), str) or not value["run_id"]:
        raise ValueError(f"{path}: missing native source run identity")
    for field in PATH_FIELDS:
        if value.get(field) is not None:
            value[field] = str((path.parent / value[field]).resolve())
    python = Path(value["python"])
    if python.is_absolute() or len(python.parts) > 1:
        value["python"] = str((path.parent / python).resolve())
    return value


def physical_contract(config: dict) -> dict:
    """Global physical Stage, policy ABI and fixed tick contract must match."""
    return {key: value for key, value in config.items() if key not in PER_RUN}


def assemble(output: Path, source_paths: list[Path]) -> dict:
    if not 2 <= len(source_paths) <= 32:
        raise ValueError("native multi-scene evaluation requires 2..32 distinct sources")
    source_paths = [path.resolve(strict=True) for path in source_paths]
    if len(set(source_paths)) != len(source_paths):
        raise ValueError("evaluation cannot double-count one authored source run")
    runs = [load(path) for path in source_paths]
    shared = physical_contract(runs[0])
    for path, run in zip(source_paths[1:], runs[1:]):
        if physical_contract(run) != shared:
            raise ValueError(
                f"{path}: physical Stage, model worker, fixed-step or episode "
                "contract differs; separate evaluations are required"
            )
    # The root allocator is shared by all independent native Levels.
    # A fresh dynamic squad must never reuse an original serialized ID.
    output = output.resolve()
    if not output.parent.is_dir() or output.exists() or output.is_symlink():
        raise FileExistsError("new evaluation suite needs an existing parent and unused output")
    config = copy.deepcopy(runs[0])
    config["next_squad_item_id"] = max(run["next_squad_item_id"] for run in runs)
    config["model_manifest"] = None  # evaluate_policy pins the actual model
    config["policy_capture"] = None
    config["host_recovery_checkpoint"] = None
    config["notifications"] = "events.jsonl"
    config["connection_checkpoint"] = "connection-checkpoint.json"
    config["rollout_summary"] = "rollout-summary.json"
    config["levels"] = []
    source_receipts = []
    with tempfile.TemporaryDirectory(prefix=f".{output.name}.suite-", dir=output.parent) as temp:
        stage = Path(temp) / "complete"
        stage.mkdir()
        count = 0
        for path, run in zip(source_paths, runs):
            original_sha = file_sha256(path)
            recorded_levels = []
            for level in run["levels"]:
                if not isinstance(level, dict) or not isinstance(level.get("scene"), str):
                    raise ValueError(f"{path}: Level is missing its native physical scene")
                single_seed = type(level.get("seed")) is int and level.get("episode_seeds") is None
                series = level.get("seed") is None and isinstance(level.get("episode_seeds"), list)
                seeds = [level["seed"]] if single_seed else level.get("episode_seeds", [])
                if not (single_seed or series) or (series and len(seeds) != run["episodes_per_world"]) or any(
                    type(seed) is not int or not 0 <= seed < (1 << 64) for seed in seeds
                ):
                    raise ValueError(f"{path}: incomplete or invalid fixed episode seeds")
                scene = (path.parent / level["scene"]).resolve(strict=True)
                if not scene.is_file() or scene.suffix.lower() != ".json":
                    raise ValueError(f"{path}: native Level scene must be a JSON source")
                count += 1
                # Two source Levels might use identical source IDs. Rebase
                # ONLY the host identities, never physical ships/squads,
                # weapons, objective targets, mission seeds or policy IDs.
                authored = copy.deepcopy(level)
                authored["world_id"] = count
                authored["level_id"] = count
                name = f"level-{count:04d}.scene.json"
                shutil.copyfile(scene, stage / name)
                scene_sha = file_sha256(scene)
                if file_sha256(stage / name) != scene_sha:
                    raise ValueError(f"{path}: scene changed during immutable suite authoring")
                authored["scene"] = name
                config["levels"].append(authored)
                recorded_levels.append({
                    "original_world_id": level.get("world_id"),
                    "original_level_id": level.get("level_id"),
                    "suite_world_id": count, "suite_level_id": count,
                    "scene": name, "source_scene": str(scene),
                    "scene_sha256": scene_sha, "episode_seeds": seeds,
                })
            if file_sha256(path) != original_sha:
                raise ValueError(f"{path}: source run changed during evaluation assembly")
            source_receipts.append({
                "run": str(path), "run_sha256": original_sha,
                "run_id": run["run_id"], "levels": recorded_levels,
            })
        identity = hashlib.sha256(json.dumps(
            source_receipts, sort_keys=True, separators=(",", ":"),
        ).encode("utf-8")).hexdigest()
        config["run_id"] = "native-mixed-holdout-" + identity[:16]
        atomic_json(stage / "run.json", config)
        receipt = {
            "schema_version": 1,
            "kind": "native_multi_fleet_evaluation_source",
            "run_id": config["run_id"],
            "source_runs": source_receipts,
            "levels": count,
            "episodes_per_world": config["episodes_per_world"],
            "planned_episodes": count * config["episodes_per_world"],
            "evaluation_run_sha256": file_sha256(stage / "run.json"),
            "shared_physical_contract_sha256": hashlib.sha256(
                json.dumps(shared, sort_keys=True, separators=(",", ":")).encode("utf-8")
            ).hexdigest(),
        }
        atomic_json(stage / "suite-source.json", receipt)
        sync_directory(stage)
        if output.exists() or output.is_symlink():
            raise FileExistsError("evaluation suite destination was claimed during authoring")
        os.rename(stage, output)
        sync_directory(output.parent)
        return receipt


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("output_dir", type=Path)
    parser.add_argument("native_run_configs", nargs="+", type=Path,
                        help="2..32 authored native policy run JSON files with fixed seed plans")
    args = parser.parse_args()
    print(json.dumps(assemble(args.output_dir, args.native_run_configs),
                     sort_keys=True), flush=True)


if __name__ == "__main__":
    main()
