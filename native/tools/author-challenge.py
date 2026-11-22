#!/usr/bin/env python3
"""Import original Challenge level fleets into the shared native Bees game.

Offline source authoring only; all physics, weapons, AI and checkpoints use
the existing author-battle and single authoritative Rust CombatWorld.
Unknown source contracts fail before the output directory is created.
"""
import argparse
from collections import Counter, defaultdict, deque
import copy
import importlib.util
import json
import math
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
LEVELS = ROOT / "Scripts/Data/challenge_levels.json"
NAMES = ("Barge", "Beacon", "Beehive", "Bumblebee", "CarpenterBee", "Carrier",
         "Cruiser", "Dreadnought", "Drone", "Factory", "FireBarge", "Flagship",
         "Frigate", "Gunship", "Honeybee", "Hornet", "Leafcutter", "Queen",
         "Scout", "Striker", "WarpGate", "Wasp", "YellowJacket", "HumanTarget")
KINDS = {name: idx for idx, name in enumerate(NAMES)}
KINDS.update({"Yellow Jacket": 22, "Carpenter Bee": 4, "Fire Barge": 10})

def xy(value, label):
    if not isinstance(value, dict) or set(value) != {"x", "y"}:
        raise ValueError(f"{label} must contain original x/y coordinates")
    out = [value["x"], value["y"]]
    if any(type(v) not in (int, float) or not math.isfinite(v) for v in out):
        raise ValueError(f"{label} coordinates must be finite")
    return out

def original_level(document, number):
    levels = document.get("Levels") if isinstance(document, dict) else None
    if not isinstance(levels, list) or len(levels) != 10 or sorted(
            x.get("Id", -1) for x in levels) != list(range(10)):
        raise ValueError("source must contain the original ten Challenge Levels")
    level = next(x for x in levels if x["Id"] == number)
    if level.get("Side") != 1 or level.get("EnemyExistingSquads") != [] or (
            level.get("EnemyReinforcementsOption") != 0):
        raise ValueError("unsupported original enemy reinforcement/reuse setting")
    if level.get("AsteroidOption") not in (0, 1, 2, 3):
        raise ValueError("unrecognized original Challenge collision-asteroid option")
    if type(level.get("MapIndex")) is not int or not 0 <= level["MapIndex"] <= 3:
        raise ValueError("original Challenge map requires Pluto, Neptune, Titania or Uranus")
    if level.get("Mining") != 0 or not isinstance(level.get("EnemySquads"), list):
        raise ValueError("unsupported Challenge resource/squad contract")
    if level.get("Obstacles") not in ("", "No") or (
            level["Obstacles"] == "" and not level.get("ObstacleList")):
        raise ValueError("Challenge requires unported random/named obstacle generation")
    if type(level.get("SupplyCapacity")) is not int or level["SupplyCapacity"] <= 0:
        raise ValueError("invalid original Challenge supply capacity")
    return level

def import_enemy(scene, run, level):
    original = level["EnemySquads"]
    bees = [s for s in scene["ships"] if s["side"] == 1]
    by_type = defaultdict(deque)
    for ship in bees:
        by_type[ship["ship_type"]].append(ship)
    old = [s for s in scene["squads"] if s["side"] == 1]
    other = [s for s in scene["squads"] if s["side"] != 1]
    old_ids = {s["item_id"] for s in old}
    initial_queues = run["levels"][0].get("scripted_queues", [])
    ai_by_type = {}
    ship_type = {ship["entity_id"]: ship["ship_type"] for ship in bees}
    for queue in initial_queues:
        squad = next((s for s in old if s["item_id"] == queue["squad_item_id"]), None)
        if squad:
            for member in squad["members"]:
                ai_by_type.setdefault(ship_type[member], queue)
    queues = [q for q in initial_queues if q["squad_item_id"] not in old_ids]
    if not old or not other:
        raise ValueError("Challenge needs both authored physical factions")
    first = min(old_ids)
    if first + len(original) >= min(s["item_id"] for s in other):
        raise ValueError("source enemy squads exceed the reserved native squad identity range")
    groups = []
    for ordinal, source in enumerate(original):
        if source.get("Side") != 1 or not source.get("Ships"):
            raise ValueError("source Challenge squad lacks Bee FleetShips")
        anchor = xy(source.get("StartingPosition"), "Challenge squad")
        members = []
        classes = []
        for source_ship in source["Ships"]:
            kind = KINDS.get(source_ship.get("ShipType"))
            if kind is None or not by_type[kind]:
                raise ValueError(f"missing authentic native ship archetype: {source_ship.get('ShipType')}")
            ship = by_type[kind].popleft()
            offset = xy(source_ship.get("Offset"), "Challenge ship offset")
            position = [anchor[j] + offset[j] for j in (0, 1)]
            if any(not scene["bounds"]["min"][j] <= position[j] <=
                   scene["bounds"]["max"][j] for j in (0, 1)):
                raise ValueError("source Challenge enemy ship lies outside the supplied original map")
            ship["position"] = position
            ship["formation_offset"] = offset
            members.append(ship["entity_id"])
            classes.append(kind)
        # Preserve the original server Game.shootingStratTypes IDs.
        # Challenge VIII's six-Bumblebee squad uses Most Powerful (7),
        # while the other original saved enemy squads use First Seen (1).
        # Reject unknown source values instead of defaulting silently.
        strategy = {"First Seen": 1, "Most Powerful": 7}.get(
            source.get("ChosenShootingStrategy"),
        )
        if strategy is None:
            raise ValueError("unknown original Challenge shooting-priority contract")
        matching = bool(source.get("IsMatchingSpeed", False))
        group = {"item_id": first + ordinal, "side": 1,
                 "members": members, "shooting_strategy_id": strategy,
                 "user_controlled": False, "can_accept_input": False,
                 "cease_fire": bool(source.get("CeaseFire", False))}
        if matching:
            group["matching_speed"] = min(
                ship["base_speed"] for ship in bees if ship["entity_id"] in members)
        groups.append(group)
        if classes[0] in ai_by_type:
            template = ai_by_type[classes[0]]
            queues.append({"squad_item_id": group["item_id"],
                           "repeat": template.get("repeat", False),
                           "orders": copy.deepcopy(template["orders"])})
    if any(by_type.values()):
        raise ValueError("generated Challenge fleet contains unassigned enemies")
    scene["squads"] = groups + other
    run["levels"][0]["scripted_queues"] = queues
    return len(groups), sum(len(s["members"]) for s in groups)

def original_obstacles(scene, level):
    obstacles = level.get("ObstacleList") or []
    if level["Obstacles"] == "No" and obstacles:
        raise ValueError("disabled original obstacle mode cannot have placements")
    if not obstacles:
        return 0
    layout = scene.setdefault("layout", {})
    if layout.get("add_static_boxes") or layout.get("add_static_polygons"):
        raise ValueError("original Challenge scene contains unrelated static obstacles")
    largest = max([s["entity_id"] for s in scene["ships"]] +
                  [s.get("special_weapon", {}).get("weapon_id", 0)
                   for s in scene["ships"]], default=0)
    placed = []
    for index, item in enumerate(obstacles):
        pos = xy(item.get("Position"), "Challenge obstacle")
        size = xy(item.get("Scale"), "Challenge obstacle size")
        if min(size) <= 0:
            raise ValueError("original Challenge obstacle has invalid scale")
        hx, hy = size[0] / 2, size[1] / 2
        placed.append({
            "entity_id": largest + index + 1,
            "prefab_name": "_Obstacle", "position": pos,
            "rotation_degrees": 0.0,
            "local_corners": [[-hx, -hy], [hx, -hy], [hx, hy], [-hx, hy]],
        })
    layout["add_static_boxes"] = placed
    return len(placed)

def builder_module():
    path = Path(__file__).with_name("author-battle.py")
    spec = importlib.util.spec_from_file_location("bees_author_battle", path)
    if spec is None or spec.loader is None:
        raise ValueError("canonical native battle author is missing")
    import sys
    sys.path.insert(0, str(path.parent))
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("output_dir", type=Path)
    parser.add_argument("--level", type=int, required=True, choices=range(10))
    parser.add_argument("--human-fleet", required=True,
                        help="existing player FleetShips as TYPE:COUNT[,TYPE:COUNT]")
    parser.add_argument("--source-scene", required=True, type=Path,
                        help="native game scene with the correct original map borders")
    parser.add_argument("--source-run", required=True, type=Path,
                        help="paired authoritative original Stage assets")
    parser.add_argument("--archetype-scene", type=Path, action="append", default=[],
                        help="extra full non-policy source ship archetypes")
    parser.add_argument("--original-levels", type=Path, default=LEVELS)
    parser.add_argument("--player-identity-map", type=Path,
                        help="shared physical class/ordinal identities for a multi-stage Challenge career")
    parser.add_argument("--seed", type=int, default=12345)
    parser.add_argument("--minutes", type=int, default=15)
    parser.add_argument("--stage-asteroid-minimum-rate", type=int,
                        help="source Stage.AsteroidMinimumSpawnRate (seconds)")
    parser.add_argument("--stage-asteroid-maximum-rate", type=int,
                        help="source Stage.AsteroidMaxSpawnRate (seconds)")
    args = parser.parse_args()
    try:
        original = json.loads(args.original_levels.read_text(encoding="utf-8"))
        level = original_level(original, args.level)
        author = builder_module()
        count = Counter(KINDS[ship["ShipType"]]
                        for squad in level["EnemySquads"] for ship in squad["Ships"])
        bees = ",".join(f"{kind}:{amount}" for kind, amount in sorted(count.items()))
        original_planet = ("pluto", "neptune", "titania", "uranus")[level["MapIndex"]]
        options = [
            str(args.output_dir), "--bees", bees, "--humans", args.human_fleet,
            "--control", "humans", "--host-side", "2",
            "--title", f"Challenge {args.level + 1}: {level['Name']}",
            "--map-background", original_planet,
            "--source-scene", str(args.source_scene),
            "--source-run", str(args.source_run), "--seed", str(args.seed),
            "--minutes", str(args.minutes), "--spacing",
            "50" if any(k in count for k in (2, 17)) else "24",
        ]
        if level["FogOfWar"]:
            options.append("--fog")
        for path in args.archetype_scene:
            options.extend(["--archetype-scene", str(path)])
        battle_args = author.battle_parser().parse_args(options)
        battle_args.ship_identity_base = 1_000_000
        if args.player_identity_map is not None:
            identity = json.loads(args.player_identity_map.read_text(encoding="utf-8"))
            if (not isinstance(identity, dict) or
                    set(identity) != {"schema_version", "side", "types"} or
                    identity["schema_version"] != 1 or identity["side"] != 2 or
                    not isinstance(identity["types"], dict)):
                raise ValueError("invalid shared Challenge player FleetShip identity map")
            by_type = {}
            for kind_text, ids in identity["types"].items():
                if not isinstance(kind_text, str) or not kind_text.isdecimal():
                    raise ValueError("invalid player FleetShip class identity")
                kind = int(kind_text)
                if kind >= 24 or kind in by_type or not isinstance(ids, list):
                    raise ValueError("invalid player FleetShip identity mapping")
                by_type[kind] = ids
            battle_args.authored_ship_ids = {2: by_type}
        # Native Challenge reuses the canonical source ship constructor.
        # Feed its original per-type, per-hull physical positions at the
        # authoring boundary rather than allocating a temporary generic
        # grid that cannot fit Queen/large and densely clustered stages.
        # The importer below still restores exact saved squad membership,
        # original offsets, target priorities and source-side commands.
        original_positions = defaultdict(list)
        for squad in level["EnemySquads"]:
            anchor = xy(squad.get("StartingPosition"), "Challenge source squad")
            for ship in squad["Ships"]:
                kind = KINDS.get(ship.get("ShipType"))
                if kind is None:
                    raise ValueError("unknown original Challenge physical ship type")
                offset = xy(ship.get("Offset"), "Challenge source ship")
                original_positions[kind].append([
                    anchor[axis] + offset[axis] for axis in range(2)
                ])
        battle_args.authored_ship_positions = {1: dict(original_positions)}
        scene, run, library = author.author(battle_args)
        # This original Challenge stage selects its planet from MapIndex.
        # Map and soundtrack are separate native source properties, but
        # the original Stage chooses both from the same MapData here.
        if scene.get("map_background") != original_planet:
            raise ValueError("Challenge battle author dropped its source planetary map")
        scene["music_location"] = original_planet
        if (scene.get("mining_asteroids") or scene.get("map_objects") or
                scene.get("layout", {}).get("add_static_boxes") or
                scene.get("layout", {}).get("add_static_polygons")):
            raise ValueError("source Challenge map must contain original borders but no unrelated terrain or resources")
        scene["activate_mining"] = False
        selected_tsv = sum(ship["max_tsv"] for ship in scene["ships"] if
                           ship["side"] == 2 and ship.get("persistent_fleet"))
        if selected_tsv > level["SupplyCapacity"]:
            raise ValueError(f"player fleet costs {selected_tsv} TSV; source Challenge cap is {level['SupplyCapacity']}")
        squads, ships = import_enemy(scene, run, level)
        boxes = original_obstacles(scene, level)
        run["levels"][0]["supply_capacity"] = {
            "player_side": 2, "max_tsv": level["SupplyCapacity"]}
        asteroid_option = level["AsteroidOption"]
        if asteroid_option:
            minimum = args.stage_asteroid_minimum_rate
            maximum = args.stage_asteroid_maximum_rate
            if (type(minimum) is not int or type(maximum) is not int or
                    not 1 <= minimum <= maximum <= 3600):
                raise ValueError(
                    "Challenge collision asteroids require verified source "
                    "Stage minimum/maximum spawn rates in seconds"
                )
            if asteroid_option == 2:
                minimum = max(1, minimum // 2)
                maximum = max(minimum, maximum // 2)
            elif asteroid_option == 3:
                minimum = 1
                maximum = max(minimum, maximum // 2)
            scene["activate_collision_asteroids"] = True
            run["levels"][0]["collision_asteroid_timer"] = {
                "minimum_seconds": minimum, "maximum_seconds": maximum,
            }
        run["levels"][0]["scene"] = "challenge.scene.json"
        # A failed/interrupted stage must resume the same physical fleet;
        # source Challenge progression is committed only on a terminal run.
        run["host_recovery_checkpoint"] = "native-host-checkpoint.json"
        library["games"][0]["config"] = "challenge.run.json"
        source_identity = {
            "schema_version": 1, "source_level_id": level["Id"],
            "source_name": level["Name"], "source_map_index": level["MapIndex"],
            "source_asteroid_option": level["AsteroidOption"],
            "source_asteroid_spawn_rates": (
                None if not asteroid_option else {
                    "minimum": args.stage_asteroid_minimum_rate,
                    "maximum": args.stage_asteroid_maximum_rate,
                }
            ),
            "original_squads": [{
                "source_id": squad["Id"], "source_name": squad["Name"],
                "native_item_id": scene["squads"][i]["item_id"],
                "original_fleet_ids": [ship["FleetId"] for ship in squad["Ships"]],
            } for i, squad in enumerate(level["EnemySquads"])],
        }
        output = args.output_dir.resolve()
        output.mkdir(parents=True, exist_ok=False)
        for name, document in (
            ("challenge.scene.json", scene),
            ("challenge.run.json", run), ("library.json", library),
            ("challenge-source.json", source_identity),
        ):
            (output / name).write_text(
                json.dumps(document, indent=2, ensure_ascii=False) + "\n",
                encoding="utf-8")
    except (OSError, ValueError, KeyError, TypeError, IndexError) as exc:
        parser.exit(2, f"original Challenge native authoring failed: {exc}\n")
    print(json.dumps({
        "kind": "native_original_challenge_authored", "level": args.level,
        "source_name": level["Name"], "source_map_index": level["MapIndex"],
        "source_supply_tsv": level["SupplyCapacity"],
        "enemy_squads": squads, "enemy_ships": ships,
        "obstacle_colliders": boxes,
        "collision_asteroid_timer": run["levels"][0].get("collision_asteroid_timer"),
        "run": str(output / "challenge.run.json"),
    }, sort_keys=True))

if __name__ == "__main__":
    main()
