#!/usr/bin/env python3
"""Author one native MA-POCA training curriculum from real original fleet captures.

Uses the original source policy author for every physical Level, the existing
multi-Level assembler for compatible Stages and native_curriculum for Stage
rotation. No substitute ships, gameplay, physics or learner are introduced.
The fully staged package is published only after every source is accounted for.
"""

import argparse
import hashlib
import json
import os
from pathlib import Path
import tempfile

from assemble_evaluation_suite import assemble, load, physical_contract
from author_original_policy_episode import author as author_policy
from native_training_artifacts import atomic_json, file_sha256, sync_directory


ROOT = Path(__file__).resolve().parents[2]
POLICY_WORKER = Path(__file__).with_name("policy_worker.py")
TACTICS = {"aggressive", "circle", "right-swipe", "left-swipe", "in-and-out", "mixed"}
U64 = 1 << 64


def read(path: Path) -> dict:
    result = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(result, dict):
        raise ValueError(f"native physical source must be a JSON object: {path}")
    return result


def integer(value, low: int, high: int) -> bool:
    return type(value) is int and low <= value <= high


def selectors(item: dict, key: str, low: int, high: int) -> set[int]:
    raw = item.get(key, [])
    if (not isinstance(raw, list) or
            len(raw) > 16384 or
            any(not integer(value, low, high) for value in raw) or
            len(set(raw)) != len(raw)):
        raise ValueError(f"source policy selection has invalid {key}")
    return set(raw)


def configured_plan(plan: dict, collection: dict) -> list[dict]:
    required = {"schema_version", "missions", "model_slots", "timeout_seconds",
                "decision_period", "episodes_per_world", "rotation_rounds_per_stage"}
    allowed = required | {"python", "source_ai_tactics", "fixed_holdout"}
    if (not required.issubset(plan) or set(plan) - allowed or
            plan["schema_version"] != 1 or
            not isinstance(plan["missions"], list) or
            not 2 <= len(plan["missions"]) <= 64 or
            not isinstance(plan["model_slots"], dict) or
            set(plan["model_slots"]) != {"0", "1"} or
            any(not integer(slot, 0, (1 << 32) - 1)
                for slot in plan["model_slots"].values()) or
            len(set(plan["model_slots"].values())) != 2 or
            type(plan["timeout_seconds"]) not in (int, float) or
            not 1 <= plan["timeout_seconds"] <= 86400 or
            not integer(plan["decision_period"], 1, 10000) or
            not integer(plan["episodes_per_world"], 1, 64) or
            not integer(plan["rotation_rounds_per_stage"], 1, 10000) or
            not isinstance(plan.get("python", "auto"), str) or
            not plan.get("python", "auto") or
            plan.get("source_ai_tactics", "aggressive") not in TACTICS):
        raise ValueError(
            "original training collection requires distinct v26 model slots, "
            "bounded episode and rotation controls and at least two source missions"
        )
    try:
        from math import isfinite
        if not isfinite(plan["timeout_seconds"]):
            raise ValueError("training episode timeout is not finite")
    except (OverflowError, TypeError):
        raise ValueError("training episode timeout is not finite") from None
    if "fixed_holdout" in plan:
        holdout = plan["fixed_holdout"]
        if (not isinstance(holdout, dict) or
                set(holdout) != {
                    "seed", "worlds_per_scenario", "episodes_per_world",
                } or not integer(holdout["seed"], 0, U64 - 1) or
                not integer(holdout["worlds_per_scenario"], 1, 1024) or
                not integer(holdout["episodes_per_world"], 1, 10000)):
            raise ValueError(
                "fixed physical holdout needs a uint64 seed, 1..1024 "
                "Worlds per distinct original fleet and 1..10000 episodes"
            )
    original_missions = collection.get("missions")
    if (collection.get("kind") != "native_original_campaign_source_collection" or
            collection.get("schema_version") != 1 or
            not isinstance(original_missions, list) or
            not original_missions):
        raise ValueError("policy curriculum needs the complete source mission collection")
    selected = set()
    for item in plan["missions"]:
        if not isinstance(item, dict) or (
            set(item) - {
                "mission_index", "policy_ship_ids", "policy_squad_ids",
                "policy_sides", "seed",
            }
        ) or not integer(item.get("mission_index"), 1, len(original_missions)) or (
            item["mission_index"] in selected
        ) or not integer(item.get("seed"), 0, U64 - 1):
            raise ValueError("original training plan has invalid or repeated mission/seed identity")
        selected.add(item["mission_index"])
        ships = selectors(item, "policy_ship_ids", 1, (1 << 63) - 1)
        squads = selectors(item, "policy_squad_ids", 1, (1 << 63) - 1)
        sides = selectors(item, "policy_sides", 1, 2)
        if not (ships or squads or sides):
            raise ValueError(
                "each original policy Level needs explicit ship, squad or side ownership"
            )
    return plan["missions"]


def author_collection(original_collection: Path, source_plan: Path,
                      destination: Path) -> dict:
    original_collection = original_collection.resolve(strict=True)
    source_plan = source_plan.resolve(strict=True)
    destination = destination.resolve()
    if (destination.exists() or destination.is_symlink() or
            not destination.parent.is_dir()):
        raise FileExistsError("original policy collection needs an unused output path")
    source_receipt_path = original_collection / "collection-receipt.json"
    source_receipt_sha = file_sha256(source_receipt_path)
    originals = read(source_receipt_path)
    plan_sha = file_sha256(source_plan)
    config = read(source_plan)
    missions = configured_plan(config, originals)
    policy_worker = POLICY_WORKER.resolve(strict=True)
    worker_sha = file_sha256(policy_worker)
    sources = originals["missions"]
    prepared = []
    grouped = {}
    with tempfile.TemporaryDirectory(
        prefix=f".{destination.name}.source-training-", dir=destination.parent
    ) as temporary:
        stage = Path(temporary) / "complete"
        stage.mkdir()
        for position, item in enumerate(missions, 1):
            index = item["mission_index"]
            source = sources[index - 1]
            if (not isinstance(source, dict) or
                    source.get("mission_index") != index or
                    not isinstance(source.get("run"), str) or
                    not isinstance(source.get("scene"), str)):
                raise ValueError(f"original mission {index} lacks immutable physical source paths")
            run_file = (original_collection / source["run"]).resolve(strict=True)
            scene_file = (original_collection / source["scene"]).resolve(strict=True)
            if (not run_file.is_relative_to(original_collection) or
                    not scene_file.is_relative_to(original_collection) or
                    file_sha256(run_file) != source["run_sha256"] or
                    file_sha256(scene_file) != source["scene_sha256"]):
                raise ValueError(f"original mission {index} changed after source capture")
            unit = stage / f"source-{position:03d}"
            result = author_policy(
                unit, run_file, None, policy_worker,
                config.get("python", "auto"),
                selectors(item, "policy_ship_ids", 1, (1 << 63) - 1),
                config["model_slots"], config["timeout_seconds"],
                config["decision_period"], config["episodes_per_world"],
                item["seed"], config.get("source_ai_tactics", "aggressive"),
                squad_ids=selectors(item, "policy_squad_ids", 1, (1 << 63) - 1),
                sides=selectors(item, "policy_sides", 1, 2),
            )
            run = unit / "run.json"
            effective = load(run)
            fingerprint = hashlib.sha256(json.dumps(
                physical_contract(effective), sort_keys=True,
                separators=(",", ":")
            ).encode("utf-8")).hexdigest()
            grouped.setdefault(fingerprint, []).append(run)
            prepared.append({
                "mission_index": index,
                "source_original_run_sha256": source["run_sha256"],
                "source_original_scene_sha256": source["scene_sha256"],
                "authored_run": run.relative_to(stage).as_posix(),
                "authored_run_sha256": result["native_policy_run_sha256"],
                "authored_policy_scene_sha256": result["native_policy_scene_sha256"],
                "stage_fingerprint": fingerprint,
                "policy_ships": result["selected_policy_ship_ids"],
                "policy_squads": result["selected_policy_squad_ids"],
            })
        if len(grouped) > 16:
            raise ValueError(
                "original physical training needs at most sixteen independently "
                "authored native Stage configurations per rotation"
            )
        fixed_holdout = config.get("fixed_holdout")
        if fixed_holdout is not None:
            # Import the canonical bootstrap holdout author only when the
            # experiment explicitly requests evaluation. It retains the
            # same original game physics and seeded episode schedule.
            from bootstrap_policy import (
                distinct_physical_scenarios, write_native_holdout,
            )
        stages = []
        for index, (fingerprint, runs) in enumerate(grouped.items(), 1):
            if len(runs) > 1:
                package = stage / f"stage-{index:03d}"
                suite = assemble(
                    package, runs, training=True, bootstrap_source=True,
                    publication_origin=stage, publication_destination=destination,
                )
                run_file = package / "run.json"
                grouped_sources = [
                    row["mission_index"] for row in prepared
                    if row["stage_fingerprint"] == fingerprint
                ]
                source_count = suite["levels"]
            else:
                run_file = runs[0]
                grouped_sources = [
                    row["mission_index"] for row in prepared
                    if row["stage_fingerprint"] == fingerprint
                ]
                source_count = 1
            staged_run = read(run_file)
            holdout_info = {}
            if fixed_holdout is not None:
                original_count = len(distinct_physical_scenarios(staged_run))
                count = original_count * fixed_holdout["worlds_per_scenario"]
                if count > 1024:
                    raise ValueError(
                        "fixed Stage evaluation exceeds 1024 physical Worlds"
                    )
                seed = (
                    fixed_holdout["seed"] +
                    (index - 1) * 0x9E3779B97F4A7C15
                ) % U64
                write_native_holdout(
                    run_file.parent, staged_run, count, seed,
                    fixed_holdout["episodes_per_world"],
                )
                holdout_path = run_file.parent / "holdout-run.json"
                holdout_info = {
                    "holdout_run": holdout_path.relative_to(stage).as_posix(),
                    "holdout_run_sha256": file_sha256(holdout_path),
                    "holdout_physical_scenarios": original_count,
                    "holdout_worlds": count,
                    "holdout_episodes_per_world": fixed_holdout["episodes_per_world"],
                }
            scene_name = staged_run["levels"][0]["scene"]
            scene_path = (run_file.parent / scene_name).resolve(strict=True)
            if not scene_path.is_relative_to(stage):
                raise ValueError("original native training scene escaped the published package")
            stages.append({
                "stage_index": index,
                "run": run_file.relative_to(stage).as_posix(),
                "run_sha256": file_sha256(run_file),
                "bootstrap_scene": scene_path.relative_to(stage).as_posix(),
                "bootstrap_scene_sha256": file_sha256(scene_path),
                "original_mission_indices": grouped_sources,
                "physical_world_templates": source_count,
                "physical_contract_sha256": fingerprint,
                **holdout_info,
            })
        if len(stages) > 1:
            rotation = {
                "schema_version": 1,
                "rotation": {
                    "rounds_per_stage": config["rotation_rounds_per_stage"],
                    "run_configs": [row["run"] for row in stages[1:]],
                    **({
                        "monitor_run_configs": [
                            row["holdout_run"] for row in stages[1:]
                        ],
                    } if fixed_holdout is not None else {}),
                },
            }
            atomic_json(stage / "curriculum.json", rotation)
        if (file_sha256(source_receipt_path) != source_receipt_sha or
                file_sha256(source_plan) != plan_sha or
                file_sha256(policy_worker) != worker_sha):
            raise ValueError("original fleet, curriculum or model worker changed during authoring")
        result = {
            "schema_version": 1,
            "kind": "native_original_multi_fleet_policy_collection",
            "original_source_collection_receipt_sha256": source_receipt_sha,
            "source_plan_sha256": plan_sha,
            "canonical_policy_worker_sha256": worker_sha,
            "model_slots": config["model_slots"],
            "episodes_per_world": config["episodes_per_world"],
            "stages": stages,
            "authored_missions": prepared,
            "bootstrap_run": stages[0]["run"],
            "bootstrap_scene": stages[0]["bootstrap_scene"],
            **({
                "primary_evaluation_run": stages[0]["holdout_run"],
                "primary_evaluation_run_sha256": stages[0]["holdout_run_sha256"],
                "independent_monitor_evaluation_runs": [
                    row["holdout_run"] for row in stages[1:]
                ],
            } if fixed_holdout is not None else {}),
            **({
                "curriculum_plan": "curriculum.json",
                "curriculum_sha256": file_sha256(stage / "curriculum.json"),
            } if len(stages) > 1 else {}),
        }
        atomic_json(stage / "training-collection-receipt.json", result)
        sync_directory(stage)
        if destination.exists() or destination.is_symlink():
            raise FileExistsError("native training collection destination was claimed during authoring")
        os.rename(stage, destination)
        sync_directory(destination.parent)
        return result


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("original_collection", type=Path,
                        help="complete immutable original Level collection directory")
    parser.add_argument("source_plan", type=Path,
                        help="explicit physical mission, actor and MA-POCA schedule JSON")
    parser.add_argument("output_directory", type=Path)
    args = parser.parse_args()
    print(json.dumps(author_collection(
        args.original_collection, args.source_plan, args.output_directory,
    ), sort_keys=True), flush=True)


if __name__ == "__main__":
    main()
