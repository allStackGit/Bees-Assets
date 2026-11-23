#!/usr/bin/env python3
"""Assemble real native Levels for joint training or fixed-seed evaluation.

Each Level retains its own physical map, original fleet, policy agents,
mission events, reinforcement plan and episode seeds. All Levels execute
through the same native CombatWorld; the shared Stage and model ABI must
match. No second gameplay or policy implementation is introduced.
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
    "run_id", "levels", "model_manifest", "notifications",
    "connection_checkpoint", "rollout_summary", "policy_capture",
    "host_recovery_checkpoint", "next_squad_item_id",
}
PATH_FIELDS = ("policy_worker", "collision_matrix", "stage_palette",
               "model_manifest")


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


def assemble(output: Path, source_paths: list[Path],
             training: bool = False, recovery: bool = False,
             bootstrap_source: bool = False,
             worlds_per_source: int = 1) -> dict:
    if not 2 <= len(source_paths) <= 64:
        raise ValueError("native joint Level run requires 2..64 distinct sources")
    if (type(worlds_per_source) is not int or not 1 <= worlds_per_source <= 1024 or
            len(source_paths) * worlds_per_source > 16384):
        raise ValueError("native Level suite needs 1..1024 persistent arenas per source, "
                         "with no more than 16384 worlds total")
    if (recovery or bootstrap_source) and not training:
        raise ValueError("bootstrap-source and host recovery require a native training suite")
    source_paths = [path.resolve(strict=True) for path in source_paths]
    if len(set(source_paths)) != len(source_paths):
        raise ValueError("evaluation cannot double-count one authored source run")
    runs = [load(path) for path in source_paths]
    shared = physical_contract(runs[0])
    for path, run in zip(source_paths[1:], runs[1:]):
        if physical_contract(run) != shared:
            raise ValueError(
                f"{path}: original Stage, collision assets, native model worker, "
                "fixed-step or experiment contract differs; separate runs are required"
            )
    manifest = None
    manifest_sha = None
    if training:
        manifest = runs[0].get("model_manifest")
        if bootstrap_source:
            if any(run.get("model_manifest") is not None for run in runs):
                raise ValueError(
                    "native cold-start Level assembly needs unbootstrapped "
                    "source policy episodes; keep all existing learner "
                    "weights outside the new model initialization"
                )
        else:
            if not isinstance(manifest, str) or not Path(manifest).is_file():
                raise ValueError("joint training needs a pinned existing model manifest")
            manifest_sha = file_sha256(Path(manifest))
            for path, run in zip(source_paths, runs):
                if run.get("model_manifest") != manifest or (
                    file_sha256(Path(manifest)) != manifest_sha
                ):
                    raise ValueError(
                        f"{path}: every training Level must use the same "
                        "frozen model and explicit self-play policy slots"
                    )
    # The root allocator is shared by all independent native Levels.
    # A fresh dynamic squad must never reuse an original serialized ID.
    output = output.resolve()
    if not output.parent.is_dir() or output.exists() or output.is_symlink():
        raise FileExistsError("new evaluation suite needs an existing parent and unused output")
    config = copy.deepcopy(runs[0])
    config["next_squad_item_id"] = max(run["next_squad_item_id"] for run in runs)
    if training:
        config["model_manifest"] = manifest
        config["policy_capture"] = "policy-capture.json"
        config["host_recovery_checkpoint"] = (
            "host-recovery-checkpoint.json" if recovery else None
        )
    else:
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
        # The same authentic scene bytes may be used by many independent
        # Worlds. Referencing ONE staged file per digest lets the existing
        # native source template cache reuse the parsed physical Level;
        # only World-owned physics state and RNG are allocated per replica.
        staged_scenes = {}
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
                scene_sha = file_sha256(scene)
                name = staged_scenes.get(scene_sha)
                if name is None:
                    name = f"level-source-{len(staged_scenes) + 1:04d}.scene.json"
                    shutil.copyfile(scene, stage / name)
                    if file_sha256(stage / name) != scene_sha:
                        raise ValueError(f"{path}: physical source changed during authoring")
                    staged_scenes[scene_sha] = name
                for replica in range(worlds_per_source):
                    count += 1
                    if count > (1 << 63) - 1:
                        raise ValueError("native Level identity allocation exhausted")
                    # Odd uint64 stride makes each persistent arena's seed
                    # distinct without changing the canonical simulation RNG
                    # implementation, reward algorithm or model policy.
                    # Replica zero preserves EXACT supplied source seeds.
                    offset = (replica * 0x9E3779B97F4A7C15) % (1 << 64)
                    episode_seeds = [
                        (value + offset) % (1 << 64) for value in seeds
                    ]
                    authored = copy.deepcopy(level)
                    authored["world_id"] = count
                    authored["level_id"] = count
                    authored["scene"] = name
                    if single_seed:
                        authored["seed"] = episode_seeds[0]
                    else:
                        authored["episode_seeds"] = episode_seeds
                    config["levels"].append(authored)
                    recorded_levels.append({
                        "source_replica": replica,
                        "original_world_id": level.get("world_id"),
                        "original_level_id": level.get("level_id"),
                        "suite_world_id": count, "suite_level_id": count,
                        "scene": name, "source_scene": str(scene),
                        "scene_sha256": scene_sha,
                        "source_episode_seeds": seeds,
                        "episode_seeds": episode_seeds,
                    })
            if file_sha256(path) != original_sha:
                raise ValueError(f"{path}: source run changed during immutable authoring")
            source_receipts.append({
                "run": str(path), "run_sha256": original_sha,
                "run_id": run["run_id"], "levels": recorded_levels,
            })
        identity = hashlib.sha256(json.dumps(
            source_receipts, sort_keys=True, separators=(",", ":"),
        ).encode("utf-8")).hexdigest()
        config["run_id"] = (
            "native-mixed-bootstrap-" if bootstrap_source
            else "native-mixed-training-" if training
            else "native-mixed-holdout-"
        ) + identity[:16]
        if training and not bootstrap_source and file_sha256(Path(manifest)) != manifest_sha:
            raise ValueError("joint training model manifest changed during suite authoring")
        atomic_json(stage / "run.json", config)
        receipt = {
            "schema_version": 1,
            "kind": ("native_multi_fleet_training_source" if training
                     else "native_multi_fleet_evaluation_source"),
            "run_id": config["run_id"],
            "training": training,
            "recovery_enabled": recovery,
            "requires_policy_bootstrap": bootstrap_source,
            **({"model_manifest": manifest, "model_manifest_sha256": manifest_sha}
               if training and not bootstrap_source else {}),
            "source_runs": source_receipts,
            "levels": count,
            "worlds_per_source": worlds_per_source,
            "distinct_physical_scene_templates": len(staged_scenes),
            "episodes_per_world": config["episodes_per_world"],
            "planned_episodes": count * config["episodes_per_world"],
            "native_run_sha256": file_sha256(stage / "run.json"),
            "shared_physical_contract_sha256": hashlib.sha256(
                json.dumps(shared, sort_keys=True, separators=(",", ":")).encode("utf-8")
            ).hexdigest(),
        }
        atomic_json(stage / "suite-source.json", receipt)
        sync_directory(stage)
        if output.exists() or output.is_symlink():
            raise FileExistsError("joint native Level destination was claimed during authoring")
        os.rename(stage, output)
        sync_directory(output.parent)
        return receipt


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("output_dir", type=Path)
    parser.add_argument("native_run_configs", nargs="+", type=Path,
                        help="2..64 authored native policy run JSON files with fixed seed plans")
    parser.add_argument("--training", action="store_true",
                        help="retain the pinned model and native policy capture for "
                             "joint self-play training across original Level rosters")
    parser.add_argument("--host-recovery", action="store_true",
                        help="enable native host transaction recovery for joint training")
    parser.add_argument("--bootstrap-source", action="store_true",
                        help="joint cold-start of uninitialized authentic policy Levels; "
                             "pass the assembled Run and its first scene to bootstrap_policy.py")
    parser.add_argument("--worlds-per-source", type=int, default=1,
                        help="persistent simulation arenas per authored source (1..1024); "
                             "each uses a different deterministic fixed seed")
    args = parser.parse_args()
    print(json.dumps(assemble(
        args.output_dir, args.native_run_configs,
        training=args.training, recovery=args.host_recovery,
        bootstrap_source=args.bootstrap_source,
        worlds_per_source=args.worlds_per_source,
    ), sort_keys=True), flush=True)


if __name__ == "__main__":
    main()
