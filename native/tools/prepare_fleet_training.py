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
import hashlib
import importlib.util
import json
import math
import os
import subprocess
import sys
import tempfile
from pathlib import Path
from types import SimpleNamespace

from native_training_artifacts import atomic_json, file_sha256, sync_directory


EXAMPLES = Path(__file__).resolve().parents[1] / "examples"
POLICY_ARCHETYPES = EXAMPLES / "native-queen.policy.scene.json"


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
    if not isinstance(level, dict):
        raise ValueError("native fleet training requires one physical Level")
    waves = level.get("reinforcements", [])
    if not isinstance(waves, list) or any(
        not isinstance(wave, dict) or
        not isinstance(wave.get("squad_item_ids"), list) or
        not wave["squad_item_ids"]
        for wave in waves
    ):
        raise ValueError("native training reinforcement waves need real source squad IDs")
    if (sum(key in level for key in ("survival", "demolition", "escort")) +
            int("extraction" in level and "escort" not in level) > 1 or
            ("escort" in level and "extraction" not in level)):
        raise ValueError("native training Level has conflicting physical objectives")
    if "escort" in level:
        objective = level["escort"]
        zone = level["extraction"]
        if (not isinstance(objective, dict) or
                objective.get("escorting_side") not in (1, 2) or
                type(objective.get("escorted_ship_id")) is not int or
                objective["escorted_ship_id"] <= 0 or
                type(objective.get("seconds")) not in (int, float) or
                not math.isfinite(objective["seconds"]) or
                objective["seconds"] <= 0 or
                not isinstance(zone, dict) or
                zone.get("player_side") != objective["escorting_side"]):
            raise ValueError("native escort training requires an authentic ship, exit and deadline")
    if "demolition" in level:
        objective = level["demolition"]
        if (not isinstance(objective, dict) or
                objective.get("attacking_side") not in (1, 2) or
                type(objective.get("target_map_object_id")) is not int or
                objective["target_map_object_id"] <= 0 or
                type(objective.get("seconds")) not in (int, float) or
                not math.isfinite(objective["seconds"]) or objective["seconds"] <= 0):
            raise ValueError("native demolition training needs a physical MapObject, side and deadline")
    if "survival" in level:
        objective = level["survival"]
        if ("extraction" in level or not isinstance(objective, dict) or
                objective.get("defending_side") not in (1, 2) or
                not isinstance(objective.get("protected_ship_id"), int) or
                type(objective.get("seconds")) not in (int, float) or
                not math.isfinite(objective["seconds"]) or objective["seconds"] <= 0):
            raise ValueError("native survival training needs a single valid physical defender and target")
    if "extraction" in level:
        objective = level["extraction"]
        if not isinstance(objective, dict) or objective.get("player_side") not in (1, 2):
            raise ValueError("native extraction training requires a physical faction-owned exit zone")
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
    supported = {"clone_squads", "merge_squads", "number_squads",
                 "remove_static_ids", "add_static_boxes", "add_map_borders"}
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
    # NativeLevelLayout clones can reference ONLY original squads/ships,
    # never another clone created earlier in the same request.
    source_ships = dict(ship_by_id)
    source_squads = dict(squad_by_id)
    scene_layout = scene.setdefault("layout", {})
    if not isinstance(scene_layout, dict):
        raise ValueError("native scene layout must be an object")
    if any(scene_layout.get(key) for key in
           ("clone_squads", "merge_squads", "number_squads",
            "translate_squads", "remove_squads", "remove_static_ids")):
        raise ValueError("source scene already changes its native squad roster or removes terrain")
    source_entities = [
        *ships, *scene.get("mining_asteroids", []),
        *scene.get("map_objects", []),
        *scene_layout.get("add_static_boxes", []),
        *scene_layout.get("add_map_borders", []),
        *scene_layout.get("add_static_polygons", []),
    ]
    max_source_entity = max(entity["entity_id"] for entity in source_entities)
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
        source_squad = source_squads.get(source_id)
        if source_squad is None or len(source_squad["members"]) != len(new_ids):
            raise ValueError("cloned squad has no exact physical source membership")
        new_squad = copy.deepcopy(source_squad)
        new_squad["item_id"] = new_squad_id
        new_squad["members"] = list(new_ids)
        new_squad.pop("saved_squad_id", None)
        new_squad.pop("hotkey_number", None)
        new_squad.pop("original_squad_count", None)
        for original_id, new_id in zip(source_squad["members"], new_ids):
            source_ship = source_ships.get(original_id)
            if (type(new_id) is not int or new_id <= max_source_entity or
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
            # Match the native game's authoritative clone registration:
            # legacy clones are temporary, while explicitly valued fleet
            # builder clones count toward reward/TSV and use a fresh FleetShip
            # identity without inheriting a SavedSquad.
            valued = clone.get("count_as_fleet_value", False)
            if type(valued) is not bool:
                raise ValueError("native clone fleet-value attribution must be boolean")
            if valued and not source_ship.get("persistent_fleet"):
                raise ValueError("valued native clone requires an authentic persistent source ship")
            ship["persistent_fleet"] = valued
            if valued:
                ship["fleet_ship_id"] = new_id
            else:
                ship.pop("fleet_ship_id", None)
            ships.append(ship)
            ship_by_id[new_id] = ship
        squads.append(new_squad)
        squad_by_id[new_squad_id] = new_squad

    # Convert the same source squad merge operation used by the live
    # Rust Level. No ship or fleet identity is added here; only command
    # ownership and per-member formation offsets are rebuilt. Conversion
    # intentionally rejects policy or specialist owners, matching the
    # playable game's shared NativePhysicalScene contract.
    merges = layout.get("merge_squads", [])
    if not isinstance(merges, list):
        raise ValueError("native multi-ship squad composition must be a list")
    used = set()
    plans = []
    for merge in merges:
        if not isinstance(merge, dict) or set(merge) != {
                "primary_squad_item_id", "merged_squad_item_ids"}:
            raise ValueError("native squad merge requires original source ownership IDs")
        primary_id = merge["primary_squad_item_id"]
        donor_ids = merge["merged_squad_item_ids"]
        if (type(primary_id) is not int or primary_id <= 0 or
                not isinstance(donor_ids, list) or not donor_ids or
                len(donor_ids) > 1023 or primary_id in used):
            raise ValueError("native merged squad has invalid original command ownership")
        primary = squad_by_id.get(primary_id)
        if primary is None or primary.get("hivemind") is not None or (
                primary.get("is_carrier_squad", False)):
            raise ValueError("native primary squad must be an ordinary game command owner")
        used.add(primary_id)
        members = list(primary["members"])
        for donor_id in donor_ids:
            if type(donor_id) is not int or donor_id <= 0 or donor_id in used:
                raise ValueError("native squad cannot be merged twice or into itself")
            used.add(donor_id)
            donor = squad_by_id.get(donor_id)
            if donor is None or donor.get("saved_squad_id") is not None or (
                    donor.get("is_carrier_squad", False)) or (
                    donor.get("hivemind") is not None) or (
                    donor.get("hotkey_number") is not None) or (
                    donor.get("original_squad_count") is not None) or any(
                        donor.get(field) != primary.get(field)
                        for field in (
                            "side", "user_controlled", "can_accept_input",
                            "locked_on", "shooting_strategy_id",
                            "cease_fire", "should_chase", "matching_speed",
                            "custom_color_rgb", "command_bans",
                        )):
                raise ValueError("native group members have incompatible physical command owners")
            members.extend(donor["members"])
        if len(members) > 1024 or len(set(members)) != len(members):
            raise ValueError("native merged squad duplicates a ship or exceeds game member cap")
        center = [0.0, 0.0]
        for ship_id in members:
            ship = ship_by_id.get(ship_id)
            if (ship is None or ship["side"] != primary["side"] or
                    ship["ship_type"] == 5 or
                    not isinstance(ship.get("base_speed"), (int, float)) or
                    not math.isfinite(ship["base_speed"]) or
                    ship["base_speed"] <= 0 or any(
                    ship.get(key) is not None for key in (
                        "policy", "special", "special_weapon", "drone_carrier_id")
            ) or ship.get("is_moving", False) or ship.get(
                    "has_target_coordinates", False):
                raise ValueError("native grouped squad must contain stationary ordinary ships")
            center[0] += ship["position"][0]
            center[1] += ship["position"][1]
        center = [axis / len(members) for axis in center]
        plans.append((primary_id, donor_ids, members, center))

    for primary_id, donor_ids, members, center in plans:
        squad_by_id[primary_id]["members"] = members
        for ship_id in members:
            ship = ship_by_id[ship_id]
            ship["formation_offset"] = [
                ship["position"][axis] - center[axis] for axis in (0, 1)
            ]
    if used:
        donors = {donor_id for _, donor_ids, _, _ in plans
                  for donor_id in donor_ids}
        scene["squads"] = [squad for squad in squads
                           if squad["item_id"] not in donors]

    shortcuts = layout.get("number_squads", [])
    if not isinstance(shortcuts, list):
        raise ValueError("native numbered squad shortcuts must be a list")
    used_shortcuts, assigned_squads = set(), set()
    for row in shortcuts:
        if not isinstance(row, dict) or set(row) != {
                "squad_item_id", "side", "number", "original_squad_count"}:
            raise ValueError("native squad shortcut has invalid source fields")
        squad_id, side = row["squad_item_id"], row["side"]
        number, count = row["number"], row["original_squad_count"]
        if (type(squad_id) is not int or squad_id <= 0 or
                type(side) is not int or side not in (1, 2) or
                type(number) is not int or not 1 <= number <= 10 or
                type(count) is not int or count <= 0 or
                (side, number) in used_shortcuts or squad_id in assigned_squads):
            raise ValueError("duplicate or invalid native squad hotkey source identity")
        squad = next((item for item in scene["squads"]
                      if item["item_id"] == squad_id), None)
        if (squad is None or squad["side"] != side or
                not squad.get("user_controlled") or
                not squad.get("can_accept_input") or
                squad.get("hivemind") is not None or
                squad.get("is_carrier_squad", False) or
                squad.get("hotkey_number") is not None or
                squad.get("original_squad_count") is not None):
            raise ValueError("native numbered squad must have a unique player command owner")
        used_shortcuts.add((side, number))
        assigned_squads.add(squad_id)
        squad["hotkey_number"] = number
        squad["original_squad_count"] = count

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
    # Source MapBorders are real physical trigger colliders, never ordinary
    # static obstacles, destructible cover or discovery-reward baselines.
    # Carry their exact game layout into the policy scene unchanged.
    borders = layout.get("add_map_borders", [])
    current_borders = scene_layout.get("add_map_borders", [])
    if not isinstance(borders, list) or not isinstance(current_borders, list):
        raise ValueError("native map-border placements must be lists")
    reserved_ids = {
        *ship_by_id.keys(),
        *(obj["entity_id"] for key in ("mining_asteroids", "map_objects")
          for obj in scene.get(key, [])),
        *(box["entity_id"] for box in active),
        *(polygon["entity_id"] for polygon in scene_layout.get("add_static_polygons", [])),
        *(border["entity_id"] for border in current_borders),
    }
    for border in borders:
        if not isinstance(border, dict) or type(border.get("entity_id")) is not int or (
                border["entity_id"] <= max_source_entity or
                border["entity_id"] in reserved_ids):
            raise ValueError("native map-border identity collides with an authored physical entity")
        reserved_ids.add(border["entity_id"])
    scene_layout["add_map_borders"] = [*current_borders, *borders]
    return scene, run, len(clones)


def verified_source_specialist_command(members: list[dict]) -> str | None:
    """Only two original physical Bomb/Charge classes lack policy actors.

    This strictly checks the source weapon contract; a type number alone
    cannot authorize replacing player input with a scripted combat order.
    """
    kinds = {ship["ship_type"] for ship in members}
    if kinds == {0} and all(
            isinstance(ship.get("special"), dict) and
            ship["special"].get("kind") == "barge" and
            isinstance(ship.get("special_weapon"), dict) and
            isinstance(ship["special_weapon"].get("charge_range"), dict)
            for ship in members):
        return "charge_nearest"
    if kinds == {22} and all(
            isinstance(ship.get("special"), dict) and
            ship["special"].get("kind") == "yellow_jacket" and
            isinstance(ship.get("special_weapon"), dict) and
            isinstance(ship["special_weapon"].get("proximity"), dict)
            for ship in members):
        return "bombing_nearest"
    return None


def deferred_reinforcement_ships(
        scene: dict, run: dict, require_scripted_fallback: bool = True) -> set[int]:
    """Preflight reserved physical identities and their command ownership.

    With original scripted control, mobile deferred units must already have
    real on-deployment orders. When policy reinforcements are requested, the
    common policy author validates each resulting policy-or-command owner;
    it must not reject a future learner merely for lacking a human command.
    """
    squads = {squad["item_id"]: squad for squad in scene["squads"]}
    deferred = set()
    ship_ids = set()
    for wave in run["levels"][0].get("reinforcements", []):
        for squad_id in wave["squad_item_ids"]:
            squad = squads.get(squad_id)
            if squad is None or squad_id in deferred:
                raise ValueError("native reinforcement refers to an unknown or repeated physical squad")
            deferred.add(squad_id)
            for ship_id in squad["members"]:
                if ship_id in ship_ids:
                    raise ValueError("native reinforcement shares a physical ship")
                ship_ids.add(ship_id)
    command_squads = set()
    for queue in run["levels"][0].get("scripted_queues", []):
        squad_id = queue["squad_item_id"]
        if squad_id not in deferred:
            continue
        if queue.get("activation") != {"kind": "on_deployment"}:
            raise ValueError(
                "native reinforced squad commands must activate on deployment"
            )
        command_squads.add(squad_id)
    ships_by_id = {ship["entity_id"]: ship for ship in scene["ships"]}
    if not ship_ids.issubset(ships_by_id):
        raise ValueError("native reinforcement has a missing authored hull")
    for squad_id in (deferred - command_squads if require_scripted_fallback else ()):
        squad = squads[squad_id]
        members = [ships_by_id[ship_id] for ship_id in squad["members"]]
        if (squad.get("user_controlled") and
                verified_source_specialist_command(members) is not None):
            # The training author installs the same Charge/BombingRun source
            # command on deployment, without ever stealing the live game's
            # actual human-control authority.
            continue
        for ship_id in squad["members"]:
            ship = ships_by_id[ship_id]
            # Authentic Carriers' children follow their parent without
            # command queues; immobile support also needs no active order.
            # Mobile ships previously owned by a human cannot become
            # controller-less training opponents when that human is removed.
            carrier_child = ship.get("drone_carrier_id") is not None or (
                isinstance(ship.get("special"), dict) and
                ship["special"].get("kind") == "striker"
            )
            if ship.get("base_speed", 0) > 0 and not carrier_child:
                raise ValueError(
                    f"native deferred mobile squad {squad_id} needs an "
                    "on_deployment scripted queue before training"
                )
    return ship_ids


def policy_author():
    """Reuse the single existing native policy-authoring implementation."""
    path = Path(__file__).with_name("author-battle.py")
    spec = importlib.util.spec_from_file_location("bees_native_battle_author", path)
    if spec is None or spec.loader is None:
        raise ValueError("native battle policy author is unavailable")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module.author_policy_scene


def physical_scene_identity(scene: dict) -> str:
    """Hash only authoritative physics and battle configuration.

    A player's input authority, policy bindings and policy reward bookkeeping
    differ by design. Ship archetypes, positions, weapons, collisions, maps,
    teams, squads and level commands must not change when training is enabled.
    """
    physical = copy.deepcopy(scene)
    physical.pop("policy_episode", None)
    physical.pop("local_player_side", None)
    for ship in physical["ships"]:
        ship.pop("user_controlled", None)
        ship.pop("policy", None)
        if isinstance(ship.get("special_weapon"), dict):
            ship["special_weapon"].pop("observation", None)
        special = ship.get("special")
        if isinstance(special, dict) and special.get("kind") == "queen":
            # Dynamic YellowJackets use the same physical spawn, actual
            # weapon and side population cap in training and gameplay.
            # Only their policy observation vocabulary is different.
            minion = special.get("spawn", {}).get("minion", {})
            if isinstance(minion, dict):
                minion.pop("observed_weapon_type", None)
                if isinstance(minion.get("special_weapon"), dict):
                    minion["special_weapon"].pop("observation", None)
    for squad in physical["squads"]:
        for field in ("user_controlled", "can_accept_input",
                      "hotkey_number", "original_squad_count"):
            squad.pop(field, None)
    return hashlib.sha256(json.dumps(
        physical, sort_keys=True, separators=(",", ":"), allow_nan=False,
    ).encode("utf-8")).hexdigest()


def verify_same_battle(scene: dict, run: dict,
                       policy_scene: dict, policy_run: dict) -> str:
    """Guard the gameplay/training boundary before handing bytes to PPO."""
    source_id = physical_scene_identity(scene)
    if source_id != physical_scene_identity(policy_scene):
        raise ValueError("native policy author changed physical combat, world or squad configuration")
    expected = copy.deepcopy(run)
    trained = copy.deepcopy(policy_run)
    for value in (expected, trained):
        value.pop("run_id", None)
        value["levels"][0].pop("scene", None)
    policy_ships = {ship["entity_id"] for ship in policy_scene["ships"]
                    if isinstance(ship.get("policy"), dict)}
    owned = {squad["item_id"] for squad in policy_scene["squads"]
             if any(ship in policy_ships for ship in squad["members"])}
    expected["levels"][0]["scripted_queues"] = [
        entry for entry in expected["levels"][0].get("scripted_queues", [])
        if entry["squad_item_id"] not in owned
    ]
    # The sole permitted new policy-scene commands belong to exact source
    # specialist combatants with no compatible v26 model: ordinary game
    # Barge Charge and Yellow Jacket BombingRun. These are real physical
    # game commands, not an alternative simulator or a fabricated learner.
    # Validate the additional instructions independently of their author,
    # so publisher changes cannot silently broaden the training contract.
    original_queues = expected["levels"][0]["scripted_queues"]
    original_squads = {squad["item_id"]: squad for squad in scene["squads"]}
    source_ships = {ship["entity_id"]: ship for ship in scene["ships"]}
    deferred = {
        squad_id for wave in expected["levels"][0].get("reinforcements", [])
        for squad_id in wave["squad_item_ids"]
    }
    extra_owners = set()
    for row in trained["levels"][0].get("scripted_queues", []):
        if row in original_queues:
            continue
        squad_id = row.get("squad_item_id")
        squad = original_squads.get(squad_id)
        if (squad is None or not squad.get("user_controlled") or
                squad_id in extra_owners or squad_id in owned):
            raise ValueError("policy added an unauthorized game command owner")
        extra_owners.add(squad_id)
        members = [source_ships[ship_id] for ship_id in squad["members"]]
        action = verified_source_specialist_command(members)
        allowed = {
            "squad_item_id": squad_id, "repeat": True,
            "orders": [{"kind": action}],
        }
        if squad_id in deferred:
            allowed["activation"] = {"kind": "on_deployment"}
        if action is None or row != allowed:
            raise ValueError("native policy specialist fallback changed a source game command")
        original_queues.append(row)
    if expected != trained:
        raise ValueError("native policy author changed the simulation run beyond policy-owned commands")
    return source_id


def convert(source: Path, destination: Path, archetypes: Path,
            policy_reinforcements: bool = False) -> dict:
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
    # Both gameplay and learning use the same armed survival deadline.
    # The RL safety timeout is ten physical ticks short of the global run
    # budget; it must not end the episode before the authored survival goal.
    survival = run["levels"][0].get("survival")
    if survival is not None:
        deadline_ticks = math.ceil(survival["seconds"] / scene["fixed_dt_seconds"])
        policy_timeout = round(seconds - 10 * scene["fixed_dt_seconds"], 4)
        if deadline_ticks * scene["fixed_dt_seconds"] > policy_timeout:
            raise ValueError("native survival deadline exceeds the policy episode budget")
        if not any(ship["entity_id"] == survival["protected_ship_id"] and
                   ship["side"] == survival["defending_side"]
                   for ship in scene["ships"]):
            raise ValueError("survival protected ship must be an authentic same-side source hull")
    demolition = run["levels"][0].get("demolition")
    if demolition is not None:
        deadline_ticks = math.ceil(demolition["seconds"] / scene["fixed_dt_seconds"])
        policy_timeout = round(seconds - 10 * scene["fixed_dt_seconds"], 4)
        if deadline_ticks * scene["fixed_dt_seconds"] > policy_timeout:
            raise ValueError("native demolition deadline exceeds the policy episode budget")
        if not any(obj.get("entity_id") == demolition["target_map_object_id"]
                   for obj in scene.get("map_objects", [])):
            raise ValueError("native demolition target is missing from the shared physical scene")
    escort = run["levels"][0].get("escort")
    if escort is not None:
        deadline_ticks = math.ceil(escort["seconds"] / scene["fixed_dt_seconds"])
        policy_timeout = round(seconds - 10 * scene["fixed_dt_seconds"], 4)
        if deadline_ticks * scene["fixed_dt_seconds"] > policy_timeout:
            raise ValueError("native escort deadline exceeds the policy episode budget")
        if not any(ship["entity_id"] == escort["escorted_ship_id"] and
                   ship["side"] == escort["escorting_side"] and
                   ship.get("base_speed", 0) > 0
                   for ship in scene["ships"]):
            raise ValueError("escort needs an authentic same-side mobile hull")
    reserved_ships = deferred_reinforcement_ships(
        scene, run, require_scripted_fallback=not policy_reinforcements,
    )
    if escort is not None and escort["escorted_ship_id"] in reserved_ships:
        raise ValueError("escort ship must be initially deployed")
    args = SimpleNamespace(
        policy_archetypes=archetypes, minutes=seconds / 60,
        deferred_reinforcement_ship_ids=reserved_ships,
        scripted_reinforcement_ship_ids=(
            set() if policy_reinforcements else reserved_ships
        ),
    )
    policy_scene, policy_run = policy_author()(scene, run, args)
    run["levels"][0]["scene"] = "battle.scene.json"
    policy_run["levels"][0]["scene"] = "battle.policy.scene.json"
    physical_identity = verify_same_battle(scene, run, policy_scene, policy_run)
    if (file_sha256(source) != source_sha or
            file_sha256(scene_path) != scene_sha or
            file_sha256(archetypes) != archetypes_sha):
        raise ValueError("native fleet source changed while deriving its training contract")
    destination = destination.resolve()
    if destination.exists():
        raise FileExistsError(f"native fleet training source already exists: {destination}")
    if not destination.parent.is_dir():
        raise ValueError("native fleet training source parent must exist")
    files = {
        "battle.scene.json": scene,
        "battle.run.json": run,
        "battle.policy.scene.json": policy_scene,
        "battle.policy.run.json": policy_run,
    }
    receipt = {
        "schema_version": 1,
        "kind": "native_fleet_skirmish_training_source",
        "source_run": str(source),
        "source_run_sha256": source_sha,
        "source_scene": str(scene_path),
        "source_scene_sha256": scene_sha,
        "policy_archetypes_sha256": archetypes_sha,
        "source_squad_clones": clone_count,
        "source_squad_merges": len(
            read_json(source)["levels"][0].get("native_layout", {}).get("merge_squads", [])
        ),
        "source_numbered_squads": len(
            read_json(source)["levels"][0].get("native_layout", {}).get("number_squads", [])
        ),
        "scripted_reinforcement_ships": sum(
            "policy" not in ship for ship in policy_scene["ships"]
            if ship["entity_id"] in reserved_ships
        ),
        "policy_reinforcement_ships": sum(
            "policy" in ship for ship in policy_scene["ships"]
            if ship["entity_id"] in reserved_ships
        ),
        "policy_reinforcements_requested": policy_reinforcements,
        "physical_scene_sha256": physical_identity,
        "physical_objective": (
            "survival" if "survival" in run["levels"][0]
            else "escort" if "escort" in run["levels"][0]
            else "extraction" if "extraction" in run["levels"][0]
            else "demolition" if "demolition" in run["levels"][0]
            else "elimination"
        ),
        "physical_ship_count": len(scene["ships"]),
        "policy_ship_count": sum("policy" in ship for ship in policy_scene["ships"]),
        "episode_fleet_value_by_side": {
            str(side): {
                "valued_ships": sum(
                    ship["side"] == side and ship.get("persistent_fleet", False)
                    for ship in scene["ships"]
                ),
                "initial_tsv": sum(
                    ship["max_tsv"] for ship in scene["ships"]
                    if ship["side"] == side and ship.get("persistent_fleet", False)
                ),
                "policy_ships": sum(
                    ship["side"] == side and "policy" in ship
                    for ship in policy_scene["ships"]
                ),
            } for side in (1, 2)
        },
    }
    # Publish all four files and their joint receipt as one immutable
    # directory. An interrupted authoring attempt must not leave a
    # seemingly usable but incomplete model initialization source.
    with tempfile.TemporaryDirectory(
        prefix=f".{destination.name}.fleet-", dir=destination.parent,
    ) as staging:
        complete = Path(staging) / "complete"
        complete.mkdir()
        for name, value in files.items():
            atomic_json(complete / name, value)
        receipt["outputs_sha256"] = {
            name: file_sha256(complete / name) for name in files
        }
        atomic_json(complete / "training-source.json", receipt)
        sync_directory(complete)
        if destination.exists():
            raise FileExistsError(f"native fleet training source appeared: {destination}")
        os.rename(complete, destination)
        sync_directory(destination.parent)
    return receipt


def verify_prepared_source(source_dir: Path) -> dict:
    """Read a published fleet/game/policy pair as one pinned input contract."""
    source_dir = source_dir.resolve(strict=True)
    receipt = read_json(source_dir / "training-source.json")
    names = ("battle.scene.json", "battle.run.json",
             "battle.policy.scene.json", "battle.policy.run.json")
    if (receipt.get("kind") != "native_fleet_skirmish_training_source" or
            receipt.get("schema_version") != 1 or
            not isinstance(receipt.get("outputs_sha256"), dict) or
            set(receipt["outputs_sha256"]) != set(names)):
        raise ValueError("native fleet training source has no complete published receipt")
    for name in names:
        if file_sha256(source_dir / name) != receipt["outputs_sha256"][name]:
            raise ValueError(f"native fleet training source changed after publication: {name}")
    scene = read_json(source_dir / names[0])
    run = read_json(source_dir / names[1])
    policy_scene = read_json(source_dir / names[2])
    policy_run = read_json(source_dir / names[3])
    if (run["levels"][0]["scene"] != names[0] or
            policy_run["levels"][0]["scene"] != names[2] or
            verify_same_battle(scene, run, policy_scene, policy_run) !=
            receipt.get("physical_scene_sha256")):
        raise ValueError("native game and training physical sources differ from the published receipt")
    return receipt


def bootstrap_training(source_dir: Path, output: Path, options: list[str]) -> None:
    """Delegate all model/round/Hub authority to the existing native bootstrap."""
    options = list(options)
    if options and options[0] == "--":
        options.pop(0)
    # These three arguments must always identify the exact source just
    # published. In particular, don't permit a forwarded flag to silently
    # substitute a different physical scene or existing model lineage.
    reserved = {"--scenario", "--source-scene", "--source-run", "--resume"}
    if any(option.split("=", 1)[0] in reserved for option in options):
        raise ValueError("fleet bootstrap source and scenario are fixed; resume with bootstrap_policy.py directly")
    source_dir = source_dir.resolve(strict=True)
    verify_prepared_source(source_dir)
    output = output.resolve()
    if output.exists():
        raise FileExistsError(f"native policy bootstrap destination exists: {output}")
    subprocess.run([
        sys.executable, str(Path(__file__).with_name("bootstrap_policy.py")),
        str(output), "--scenario", "custom",
        "--source-scene", str(source_dir / "battle.policy.scene.json"),
        "--source-run", str(source_dir / "battle.policy.run.json"),
        *options,
    ], check=True)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("fleet_run", type=Path,
                        help="game's saved replay-source-run.json")
    parser.add_argument("output_dir", type=Path,
                        help="new directory for paired game and training configurations")
    parser.add_argument("--policy-archetypes", type=Path, default=POLICY_ARCHETYPES)
    parser.add_argument("--policy-reinforcements", action="store_true",
                        help="enroll supported deferred ships into MA-POCA only after their "
                             "actual native reinforcement deployment (otherwise scripted AI)")
    parser.add_argument("--bootstrap-output", type=Path,
                        help="new checkpoint/session directory; optionally launch real native training")
    parser.add_argument("--bootstrap-args", nargs=argparse.REMAINDER, default=[],
                        help="forward bootstrap_policy.py options (after this flag), e.g. --train-rounds 3 --rollout-worlds 16")
    args = parser.parse_args()
    if args.bootstrap_args and args.bootstrap_output is None:
        parser.error("--bootstrap-args requires --bootstrap-output")
    if args.bootstrap_output is not None:
        if any(value.split("=", 1)[0] in
               {"--scenario", "--source-scene", "--source-run", "--resume"}
               for value in args.bootstrap_args):
            parser.error("fleet source/scenario are immutable; resume via bootstrap_policy.py")
    receipt = convert(args.fleet_run, args.output_dir, args.policy_archetypes,
                      policy_reinforcements=args.policy_reinforcements)
    print(json.dumps(receipt, sort_keys=True), flush=True)
    if args.bootstrap_output is not None:
        bootstrap_training(args.output_dir.resolve(), args.bootstrap_output,
                           args.bootstrap_args)


if __name__ == "__main__":
    main()
