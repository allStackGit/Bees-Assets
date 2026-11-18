#!/usr/bin/env python3
"""Turn a saved in-game native fleet skirmish into a playable/trainable pair.

The game builder uses native_layout clones to keep interactive battle files
compact. MA-POCA deliberately requires a fixed, explicit source roster:
materialize the *same* physical ships before adding policy participants.
Combat, collision assets, commands, episode rules and seeds remain native.
"""
from __future__ import annotations

import argparse
import copy
import importlib.util
import json
import math
from pathlib import Path
from types import SimpleNamespace

from native_training_artifacts import atomic_json, file_sha256


EXAMPLES = Path(__file__).resolve().parents[1] / "examples"
POLICY_ARCHETYPES = EXAMPLES / "native-skirmish.policy.scene.json"


def read_json(path: Path) -> dict:
    value = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(value, dict):
        raise ValueError(f"expected a JSON object: {path}")
    return value


def source_battle(run_path: Path) -> tuple[dict, dict]:
    run = read_json(run_path)
    levels = run.get("levels")
    if (not isinstance(run.get("run_id"), str) or
            not run["run_id"].startswith("native-fleet-") or
            not isinstance(levels, list) or len(levels) != 1 or
            run.get("episodes_per_world") != 1):
        raise ValueError("requires one archived native fleet-builder skirmish, not a benchmark")
    level = levels[0]
    if (not isinstance(level, dict) or
            any(key in level for key in ("survival", "extraction", "reinforcements"))):
        raise ValueError("MA-POCA requires an elimination battle with a fixed starting roster")
    source = level.get("scene")
    if not isinstance(source, str) or not source:
        raise ValueError("fleet builder Level has no authored physical scene")
    scene = read_json((run_path.parent / source).resolve(strict=True))
    if (scene.get("native_game_schema") != 1 or
            "policy_episode" in scene or
            any("policy" in ship for ship in scene.get("ships", []))):
        raise ValueError("source must be an ordinary native physical scene, not an existing policy episode")
    return scene, run


def materialize(scene: dict, run: dict) -> tuple[dict, dict, int]:
    """Expand the game builder's exact clone offsets/IDs, not a new ship generator."""
    scene, run = copy.deepcopy(scene), copy.deepcopy(run)
    level = run["levels"][0]
    layout = level.pop("native_layout", {})
    if not isinstance(layout, dict):
        raise ValueError("native layout must be an object")
    supported = {"clone_squads", "remove_static_ids", "add_static_boxes"}
    if any(key not in supported and value not in ([], None)
           for key, value in layout.items()):
        raise ValueError("unsupported fleet layout operation; refusing a changed physical battle")
    ships, squads = scene.get("ships"), scene.get("squads")
    if not isinstance(ships, list) or not isinstance(squads, list):
        raise ValueError("native source needs a physical ship and squad roster")
    ship_by_id = {ship["entity_id"]: ship for ship in ships}
    squad_by_id = {squad["item_id"]: squad for squad in squads}
    if len(ship_by_id) != len(ships) or len(squad_by_id) != len(squads):
        raise ValueError("duplicate source ship or squad identity")
    max_source_ship = max(ship_by_id)
    max_source_squad = max(squad_by_id)
    bounds = scene["bounds"]
    clones = layout.get("clone_squads", [])
    if not isinstance(clones, list):
        raise ValueError("native squad clones must be a list")
    for clone in clones:
        source_id = clone["source_squad_item_id"]
        new_squad_id = clone["new_squad_item_id"]
        new_ids = clone["ship_ids"]
        by = clone["by"]
        if (type(new_squad_id) is not int or new_squad_id <= max_source_squad or
                new_squad_id in squad_by_id or
                not isinstance(new_ids, list) or not isinstance(by, list) or
                len(by) != 2 or not all(type(v) in (int, float) and math.isfinite(v)
                                        for v in by)):
            raise ValueError("invalid cloned squad identity or offset")
        source_squad = squad_by_id.get(source_id)
        if source_squad is None or len(source_squad["members"]) != len(new_ids):
            raise ValueError("cloned squad has no exact physical source membership")
        new_squad = copy.deepcopy(source_squad)
        new_squad["item_id"] = new_squad_id
        new_squad["members"] = list(new_ids)
        new_squad.pop("saved_squad_id", None)
        new_squad.pop("hotkey_number", None)
        new_squad.pop("original_squad_count", None)
        for original_id, new_id in zip(source_squad["members"], new_ids):
            source_ship = ship_by_id.get(original_id)
            if (type(new_id) is not int or new_id <= max_source_ship or
                    new_id in ship_by_id or
                    source_ship is None or
                    any(source_ship.get(key) is not None for key in
                        ("policy", "special", "special_weapon", "drone_carrier_id"))):
                raise ValueError("native squad clone has invalid ship ID or unsupported relationship")
            ship = copy.deepcopy(source_ship)
            ship["entity_id"] = new_id
            ship["position"] = [source_ship["position"][n] + by[n] for n in (0, 1)]
            if not all(math.isfinite(value) and
                       bounds["min"][n] <= value <= bounds["max"][n]
                       for n, value in enumerate(ship["position"])):
                raise ValueError("cloned physical ship would leave native map bounds")
            # The game clone has no *original* saved FleetShip. An authored
            # training scene instead gives each real persistent hull its own
            # distinct fleet identity for exact episode TSV accounting.
            if ship.get("persistent_fleet"):
                ship["fleet_ship_id"] = new_id
            else:
                ship.pop("fleet_ship_id", None)
            ships.append(ship)
            ship_by_id[new_id] = ship
        squads.append(new_squad)
        squad_by_id[new_squad_id] = new_squad

    scene_layout = scene.setdefault("layout", {})
    if not isinstance(scene_layout, dict):
        raise ValueError("native scene layout must be an object")
    removed = layout.get("remove_static_ids", [])
    added = layout.get("add_static_boxes", [])
    if not isinstance(removed, list) or not isinstance(added, list):
        raise ValueError("native static obstacle edits must be lists")
    old_boxes = scene_layout.get("add_static_boxes", [])
    removed_ids = set(removed)
    existing_ids = {box["entity_id"] for box in old_boxes}
    if len(removed_ids) != len(removed) or not removed_ids.issubset(existing_ids):
        raise ValueError("native fleet removal refers to a missing or duplicate static collider")
    active = [box for box in old_boxes if box["entity_id"] not in removed_ids]
    for box in added:
        if box["entity_id"] in existing_ids or box["entity_id"] in ship_by_id:
            raise ValueError("native fleet addition collides with a physical entity ID")
        existing_ids.add(box["entity_id"])
        active.append(box)
    scene_layout["add_static_boxes"] = active
    return scene, run, len(clones)


def policy_author():
    """Reuse the single existing native policy-authoring implementation."""
    path = Path(__file__).with_name("author-battle.py")
    spec = importlib.util.spec_from_file_location("bees_native_battle_author", path)
    if spec is None or spec.loader is None:
        raise ValueError("native battle policy author is unavailable")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module.author_policy_scene


def convert(source: Path, destination: Path, archetypes: Path) -> dict:
    source = source.resolve(strict=True)
    archetypes = archetypes.resolve(strict=True)
    scene_path = (source.parent / read_json(source)["levels"][0]["scene"]).resolve(strict=True)
    source_sha = file_sha256(source)
    scene_sha = file_sha256(scene_path)
    archetypes_sha = file_sha256(archetypes)
    scene, run = source_battle(source)
    scene, run, clone_count = materialize(scene, run)
    if not isinstance(run.get("max_rounds"), int) or run["max_rounds"] <= 10:
        raise ValueError("source battle has no valid native episode round limit")
    seconds = run["max_rounds"] * scene["fixed_dt_seconds"]
    if not math.isfinite(seconds) or seconds <= 0:
        raise ValueError("invalid native fixed-step episode duration")
    args = SimpleNamespace(policy_archetypes=archetypes, minutes=seconds / 60)
    policy_scene, policy_run = policy_author()(scene, run, args)
    run["levels"][0]["scene"] = "battle.scene.json"
    policy_run["levels"][0]["scene"] = "battle.policy.scene.json"
    if (file_sha256(source) != source_sha or
            file_sha256(scene_path) != scene_sha or
            file_sha256(archetypes) != archetypes_sha):
        raise ValueError("native fleet source changed while deriving its training contract")
    destination = destination.resolve()
    destination.mkdir(parents=True, exist_ok=False)
    files = {
        "battle.scene.json": scene,
        "battle.run.json": run,
        "battle.policy.scene.json": policy_scene,
        "battle.policy.run.json": policy_run,
    }
    for name, value in files.items():
        atomic_json(destination / name, value)
    receipt = {
        "schema_version": 1,
        "kind": "native_fleet_skirmish_training_source",
        "source_run": str(source),
        "source_run_sha256": source_sha,
        "source_scene": str(scene_path),
        "source_scene_sha256": scene_sha,
        "policy_archetypes_sha256": archetypes_sha,
        "source_squad_clones": clone_count,
        "physical_ship_count": len(scene["ships"]),
        "policy_ship_count": sum("policy" in ship for ship in policy_scene["ships"]),
        "outputs_sha256": {name: file_sha256(destination / name) for name in files},
    }
    atomic_json(destination / "training-source.json", receipt)
    return receipt


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("fleet_run", type=Path,
                        help="game's saved replay-source-run.json")
    parser.add_argument("output_dir", type=Path,
                        help="new directory for paired game and training configurations")
    parser.add_argument("--policy-archetypes", type=Path, default=POLICY_ARCHETYPES)
    args = parser.parse_args()
    print(json.dumps(convert(args.fleet_run, args.output_dir,
                             args.policy_archetypes), sort_keys=True), flush=True)


if __name__ == "__main__":
    main()
