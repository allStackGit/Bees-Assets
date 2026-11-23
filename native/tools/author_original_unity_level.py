#!/usr/bin/env python3
"""Author a playable native Run from genuine Unity Level + Stage exports.

Uses the native host's existing strict UnitySnapshot importer and the same
CombatWorld as multiplayer and reinforcement learning. No synthetic ship
templates, alternate physics, inferred layer masks or asteroid prefabs.
An original scripted mission may additionally supply its authored Level
event/command contract; absent mission events are never fabricated.
"""

import argparse
import hashlib
import json
import math
import os
from pathlib import Path
import shutil
import tempfile

from native_training_artifacts import atomic_json, file_sha256, sync_directory


def read(path: Path) -> dict:
    source = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(source, dict):
        raise ValueError(f"{path}: source JSON must be an object")
    return source


def is_int(value: object, minimum: int, maximum: int) -> bool:
    return type(value) is int and minimum <= value <= maximum


def vector(value: object, label: str) -> list[float]:
    """Read Unity's Vector2 object, preserving exact authored float values."""
    if (not isinstance(value, dict) or set(value) != {"x", "y"} or
            any(type(value[axis]) not in (int, float) or
                not math.isfinite(value[axis]) for axis in ("x", "y"))):
        raise ValueError(f"native source {label} must be a complete finite Unity Vector2")
    return [value["x"], value["y"]]


def collision_stage(matrix: dict, palette: dict, scene: dict) -> dict:
    rows = matrix.get("rows")
    if (matrix.get("schema_version") != 1 or
            not isinstance(rows, list) or len(rows) != 32 or
            any(not is_int(row, 0, (1 << 32) - 1) for row in rows)):
        raise ValueError("Unity physics layer matrix is not a complete 32-row source")
    for first, mask in enumerate(rows):
        for second in range(first + 1, 32):
            if bool(mask & (1 << second)) != bool(rows[second] & (1 << first)):
                raise ValueError("Unity collision layer matrix is asymmetric")
    source_prefabs = palette.get("prefab_numbers")
    if (palette.get("schema_version") != 1 or
            palette.get("stage_name") != scene.get("stage_name") or
            not isinstance(source_prefabs, list) or not source_prefabs or
            len(source_prefabs) > 1024 or
            any(not is_int(value, 1, 28) for value in source_prefabs) or
            not is_int(palette.get("asteroid_max_speed_exclusive"), 0, 100000)):
        raise ValueError("Stage asteroid palette does not match the original physical Level")
    return {
        "collision_matrix_rows": rows,
        "asteroid_prefab_numbers": source_prefabs,
    }


def original_timer(source: dict) -> dict | None:
    timer = source.get("collision_asteroid_timer")
    if timer is None:
        return None
    if not source.get("activate_collision_asteroids") or not isinstance(timer, dict):
        raise ValueError("Source collision asteroid timer lacks physical Level ownership")
    minimum, maximum = timer.get("minimum_seconds"), timer.get("maximum_seconds")
    if (type(minimum) not in (int, float) or type(maximum) not in (int, float) or
            not math.isfinite(minimum) or not math.isfinite(maximum) or
            not minimum.is_integer() if isinstance(minimum, float) else False):
        raise ValueError("Source collision asteroid timer is invalid")
    # The source may use a fractional training bootstrap timer; do not
    # round it into a different asteroid field or silently disable spawns.
    if (not float(minimum).is_integer() or not float(maximum).is_integer() or
            not 1 <= minimum <= maximum <= 3600):
        raise ValueError("Source asteroid timer cannot be represented by the native integer Level timer")
    return {"minimum_seconds": int(minimum), "maximum_seconds": int(maximum)}


def required_source(scene: dict) -> tuple[dict, list[float], list[float]]:
    if (not is_int(scene.get("schema_version"), 35, 35) or
            scene.get("native_game_schema") is not None or
            not isinstance(scene.get("stage_name"), str) or
            not scene["stage_name"] or
            not isinstance(scene.get("level_name"), str) or
            not scene["level_name"] or
            scene.get("has_policy_episode") is True or
            not isinstance(scene.get("ships"), list) or not scene["ships"] or
            not isinstance(scene.get("squads"), list) or not scene["squads"] or
            not is_int(scene.get("source_pool_item_count"), 0, (1 << 63) - 1000000) or
            type(scene.get("fixed_dt_seconds")) not in (int, float) or
            not math.isfinite(scene["fixed_dt_seconds"]) or
            not 0 < scene["fixed_dt_seconds"] < 1):
        raise ValueError("Expected source schema35 full physical Level, not an artificial game or training snapshot")
    bounds_min = vector(scene.get("min"), "min")
    bounds_max = vector(scene.get("max"), "max")
    half_extents = vector(scene.get("half_map_extents"), "half_map_extents")
    if (any(a >= b for a, b in zip(bounds_min, bounds_max)) or
            any(half <= 0 for half in half_extents)):
        raise ValueError("Source Level map bounds or source half map extents are invalid")
    if not isinstance(scene.get("targeting_context"), dict):
        raise ValueError("Source Level lacks original player targeting authority")
    if any(ship.get("has_bound_policy") is True for ship in scene["ships"]):
        raise ValueError("Bound source policy agents require the existing native policy training author")
    if not all(isinstance(ship, dict) and is_int(ship.get("entity_id"), 1, (1 << 63) - 1)
               for ship in scene["ships"]):
        raise ValueError("Original physical Level has invalid ship identities")
    ids = {ship["entity_id"] for ship in scene["ships"]}
    if len(ids) != len(scene["ships"]):
        raise ValueError("Original physical Level has duplicate ship identities")
    owned = set()
    squad_ids = set()
    for squad in scene["squads"]:
        if (not isinstance(squad, dict) or
                not is_int(squad.get("item_id"), 1, (1 << 63) - 1) or
                squad["item_id"] in squad_ids or
                not isinstance(squad.get("member_ids"), list) or
                not squad["member_ids"]):
            raise ValueError("Source Level has malformed or duplicated squads")
        squad_ids.add(squad["item_id"])
        for member in squad["member_ids"]:
            if member not in ids or member in owned:
                raise ValueError("Source physical ship is missing or repeated across squads")
            owned.add(member)
    if owned != ids or scene["source_pool_item_count"] <= max(squad_ids):
        raise ValueError("Source Level has unowned physical ships or a stale Stage pool allocator")
    return {"min": bounds_min, "max": bounds_max}, half_extents, scene["ships"]


def native_game_run(
    scene: dict, matrix: dict, palette: dict, contract: dict, seed: int,
    max_rounds: int, cell: float, workers: int | None,
    navigation_workers: int | None, hivemind: dict | None,
) -> dict:
    bounds, half_extents, ships = required_source(scene)
    stage = collision_stage(matrix, palette, scene)
    if (not is_int(seed, 0, (1 << 64) - 1) or
            not is_int(max_rounds, 1, (1 << 63) - 1) or
            type(cell) not in (int, float) or
            not math.isfinite(cell) or not 0 < cell <= 10000):
        raise ValueError("Native game requires explicit valid seed, round budget and broadphase size")
    if (workers is not None and not is_int(workers, 1, 128)) or (
            navigation_workers is not None and not is_int(navigation_workers, 1, 128)):
        raise ValueError("Native CPU physics/navigation worker count must be 1..128")
    if not isinstance(contract, dict):
        raise ValueError("Native authored mission Level contract must be an object")
    forbidden = {"scene", "world_id", "level_id", "seed", "episode_seeds",
                 "model_slots", "policy_episode", "source"}
    if forbidden & set(contract):
        raise ValueError("Authored Level contract cannot override immutable physical scenario identities")
    script = contract.get("scripted_queues", [])
    if not isinstance(script, list):
        raise ValueError("Native scripted source commands must be an array")
    squad_ids = {squad["item_id"] for squad in scene["squads"]}
    scripted = set()
    for row in script:
        if (not isinstance(row, dict) or
                row.get("squad_item_id") not in squad_ids or
                row["squad_item_id"] in scripted or
                not isinstance(row.get("orders"), list) or not row["orders"]):
            raise ValueError("Authored source AI queue has an unknown, repeated or empty squad owner")
        scripted.add(row["squad_item_id"])
    if bool(scene.get("activate_hivemind")) != (hivemind is not None):
        raise ValueError("Original HiveMind activation must match a supplied authentic network connection")
    if hivemind is not None and not isinstance(hivemind, dict):
        raise ValueError("Native HiveMind source must be an existing native host configuration")
    by_ship = {ship["entity_id"]: ship for ship in ships}
    for squad in scene["squads"]:
        if squad.get("user_controlled") is True or squad["item_id"] in scripted:
            continue
        if (hivemind is not None and squad.get("is_hivemind_controlled") is True):
            continue
        # The original remote command owner is not serialized into a live
        # Unity physical scene: an imported Level cannot leave untrained,
        # mobile enemy ships idle and call it a gameplay replacement.
        if any(
            by_ship[mid].get("base_speed", 0) > 0 and
            (by_ship[mid].get("turrets") or by_ship[mid].get("special_weapon"))
            and by_ship[mid].get("parent_carrier_id", 0) == 0
            for mid in squad["member_ids"]
        ):
            raise ValueError(
                f"Original mobile squad {squad['item_id']} needs a real "
                "authored scripted queue or native HiveMind command owner"
            )
    timer = original_timer(scene)
    if (scene.get("activate_collision_asteroids") is True and timer is None
            and not any(ob.get("kind") == "collision_asteroid" for ob in scene.get("obstacles", []))):
        raise ValueError("Original enabled collision asteroids lack both a physical source timer and asteroid bodies")
    level = {
        "scene": "source-level.json",
        "world_id": 1, "level_id": 1, "seed": seed,
        **contract,
        **({"collision_asteroid_timer": timer} if timer is not None else {}),
    }
    return {
        "run_id": "native-unity-source",
        "stage_name": scene["stage_name"],
        "native_stage": stage,
        "fragment_spawn": {
            "map_bounds": bounds,
            "half_map_extents": half_extents,
            "asteroid_max_speed_exclusive": palette["asteroid_max_speed_exclusive"],
            "training_health_ratio": None,
        },
        "connection_checkpoint": "connection-checkpoint.json",
        "notifications": "events.jsonl",
        "next_squad_item_id": scene["source_pool_item_count"] + 1,
        "broadphase_cell_size": cell,
        "decision_period": 1,
        "max_rounds": max_rounds,
        "episodes_per_world": 1,
        "rounds_per_window": 16,
        "delivery_every": 16,
        "timing_sample_period": 500,
        **({"physics_workers": workers} if workers is not None else {}),
        **({"navigation_workers": navigation_workers} if navigation_workers is not None else {}),
        **({"hivemind": hivemind} if hivemind is not None else {}),
        "levels": [level],
    }


def author(output: Path, source_path: Path, matrix_path: Path,
           palette_path: Path, contract_path: Path | None, seed: int,
           max_rounds: int, cell: float, physics_workers: int | None,
           navigation_workers: int | None, hive_path: Path | None) -> dict:
    source_path, matrix_path, palette_path = (
        path.resolve(strict=True) for path in
        (source_path, matrix_path, palette_path)
    )
    contract_path = contract_path.resolve(strict=True) if contract_path else None
    hive_path = hive_path.resolve(strict=True) if hive_path else None
    originals = [source_path, matrix_path, palette_path]
    if contract_path: originals.append(contract_path)
    if hive_path: originals.append(hive_path)
    originals_sha = {str(path): file_sha256(path) for path in originals}
    scene = read(source_path)
    matrix, palette = read(matrix_path), read(palette_path)
    mission = read(contract_path) if contract_path else {}
    hive = read(hive_path) if hive_path else None
    run = native_game_run(
        scene, matrix, palette, mission, seed, max_rounds, cell,
        physics_workers, navigation_workers, hive,
    )
    identity = hashlib.sha256(json.dumps(
        [originals_sha, seed, max_rounds, cell, physics_workers,
         navigation_workers], sort_keys=True,
        separators=(",", ":"),
    ).encode("utf-8")).hexdigest()
    run["run_id"] = "native-unity-" + identity[:20]
    output = output.resolve()
    if not output.parent.is_dir() or output.exists() or output.is_symlink():
        raise FileExistsError("Native original mission output must be a new directory")
    with tempfile.TemporaryDirectory(prefix=f".{output.name}.original-", dir=output.parent) as temp:
        stage = Path(temp) / "completed"
        stage.mkdir()
        shutil.copyfile(source_path, stage / "source-level.json")
        atomic_json(stage / "run.json", run)
        for path, digest in originals_sha.items():
            if file_sha256(Path(path)) != digest:
                raise ValueError("Native source Level, Stage or mission contract changed during authoring")
        if file_sha256(stage / "source-level.json") != originals_sha[str(source_path)]:
            raise ValueError("Native physical scene changed during publication")
        receipt = {
            "schema_version": 1,
            "kind": "native_original_physical_mission_authored",
            "run_id": run["run_id"],
            "source_sha256": originals_sha,
            "original_stage": scene["stage_name"],
            "original_level": scene["level_name"],
            "fixed_dt_seconds": scene["fixed_dt_seconds"],
            "source_pool_item_count": scene["source_pool_item_count"],
            "source_collision_asteroid_timer": run["levels"][0].get("collision_asteroid_timer"),
            "native_run_sha256": file_sha256(stage / "run.json"),
            "native_scene_sha256": file_sha256(stage / "source-level.json"),
        }
        atomic_json(stage / "original-source-receipt.json", receipt)
        sync_directory(stage)
        if output.exists() or output.is_symlink():
            raise FileExistsError("Native original mission directory was claimed during authoring")
        os.rename(stage, output)
        sync_directory(output.parent)
        return receipt


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("output_directory", type=Path)
    parser.add_argument("--unity-scene", required=True, type=Path,
                        help="complete NativePhysicalSceneExporter source snapshot")
    parser.add_argument("--physics-matrix", required=True, type=Path,
                        help="authoritative Unity Physics2D matrix export")
    parser.add_argument("--stage-palette", required=True, type=Path,
                        help="exact ordered Stage asteroid prefab export")
    parser.add_argument("--level-contract", type=Path,
                        help="original authored native Level mission/events/command contract")
    parser.add_argument("--hivemind-config", type=Path,
                        help="native HiveMind host connection for original server-owned AI squads")
    parser.add_argument("--seed", type=int, required=True,
                        help="the selected physical episode's reproducible simulation seed")
    parser.add_argument("--max-rounds", type=int, required=True,
                        help="physical episode time budget, in fixed source ticks")
    parser.add_argument("--broadphase-cell-size", type=float, default=20,
                        help="non-gameplay native contact search allocation (default 20)")
    parser.add_argument("--physics-workers", type=int)
    parser.add_argument("--navigation-workers", type=int)
    args = parser.parse_args()
    print(json.dumps(author(
        args.output_directory, args.unity_scene, args.physics_matrix,
        args.stage_palette, args.level_contract, args.seed, args.max_rounds,
        args.broadphase_cell_size, args.physics_workers,
        args.navigation_workers, args.hivemind_config,
    ), sort_keys=True), flush=True)


if __name__ == "__main__":
    main()
