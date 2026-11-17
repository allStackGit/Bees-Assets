#!/usr/bin/env python3
"""Author a playable, headless-capable Bees battle without Unity.

The native scene is composed from existing fully-authored ship archetypes.
Only positions, ownership, identities and squad orders are generated; the
canonical Rust CombatWorld remains the single physics/gameplay implementation.
Python is an authoring-time tool, not a dependency of the packaged game.
"""

import argparse
import copy
import json
import math
import re
from pathlib import Path

EXAMPLES = Path(__file__).resolve().parents[1] / "examples"
MAX_SQUAD_MEMBERS = 10
SHIP_ID_START = 10_000
SQUAD_ID_START = 20_000
WEAPON_ID_START = 100_000


def fleet(value: str) -> list[tuple[int, int]]:
    """Parse comma-separated native ship-type IDs with explicit counts."""
    counts: dict[int, int] = {}
    for token in value.split(","):
        match = re.fullmatch(r"\s*(\d+)\s*:\s*(\d+)\s*", token)
        if match is None:
            raise argparse.ArgumentTypeError(
                "fleets must be comma-separated TYPE:COUNT pairs (e.g. 21:4,18:1)"
            )
        ship_type, count = (int(number) for number in match.groups())
        if ship_type >= 24 or not 1 <= count <= 1024:
            raise argparse.ArgumentTypeError("ship types must be 0..23 and counts 1..1024")
        counts[ship_type] = counts.get(ship_type, 0) + count
        if sum(counts.values()) > 1024:
            raise argparse.ArgumentTypeError("a side may have at most 1024 ships")
    return list(counts.items())


def load_json(path: Path) -> dict:
    content = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(content, dict):
        raise ValueError(f"expected a JSON object: {path}")
    return content


def placements(bounds: dict, anchor: list[float], side: int,
               count: int, spacing: float) -> list[list[float]]:
    """Spread distinct initial hulls around the authored side's own home.

    Every grid cell is on that side of the map with a clear central buffer.
    Dense maps fail preflight instead of stacking colliders on spawn.
    """
    low, high = bounds["min"], bounds["max"]
    mid = (low[0] + high[0]) / 2
    margin = spacing * 0.6
    xs = []
    n = 0
    while low[0] + margin + n * spacing <= high[0] - margin:
        x = low[0] + margin + n * spacing
        if (side == 1 and x <= mid - spacing) or (
                side == 2 and x >= mid + spacing):
            xs.append(x)
        n += 1
    ys = []
    n = 0
    while low[1] + margin + n * spacing <= high[1] - margin:
        ys.append(low[1] + margin + n * spacing)
        n += 1
    candidates = [(x, y) for x in xs for y in ys]
    candidates.sort(key=lambda point: (
        (point[0] - anchor[0]) ** 2 + (point[1] - anchor[1]) ** 2,
        abs(point[1] - anchor[1]), point[0], point[1],
    ))
    if count > len(candidates):
        raise ValueError(
            f"fleet side {side} has {count} ships but this map fits at most "
            f"{len(candidates)} at spacing {spacing:g}; choose fewer ships "
            "or a larger authored native map"
        )
    return [[round(x, 4), round(y, 4)] for x, y in candidates[:count]]


def author(args) -> tuple[dict, dict, dict]:
    scene = load_json(args.source_scene)
    run = load_json(args.source_run)
    if (scene.get("native_game_schema") != 1 or
            scene.get("policy_episode") is not None or
            any(ship.get("policy") is not None for ship in scene.get("ships", []))):
        raise ValueError("archetype source must be a game-only native_game_schema 1 scene")
    if (scene.get("bee_side") != 1 or scene.get("human_side") != 2 or
            scene.get("activate_hivemind") or
            not isinstance(scene.get("starting_positions"), list)):
        raise ValueError("authoring requires a native game scene with sides 1/2 and home positions")
    if (run.get("fragment_spawn", {}).get("map_bounds") != scene.get("bounds") or
            "native_stage" not in run or len(run.get("levels", [])) != 1 or
            run.get("model_manifest") or run.get("hivemind")):
        raise ValueError("the paired run template must contain the same native map and game-only stage assets")
    if set(scene.get("layout", {})) - {"add_static_boxes", "add_polygon_obstacles"}:
        raise ValueError("source layout contains squad-relative changes that cannot be safely cloned")
    if not math.isfinite(args.spacing) or args.spacing < 12:
        raise ValueError("spacing must be a finite world distance of at least 12")
    name = args.title.strip()
    if not 1 <= len(name) <= 80:
        raise ValueError("battle title must contain 1..80 non-whitespace characters")
    if not 1 <= args.minutes <= 60 or not 0 <= args.seed < 1 << 64:
        raise ValueError("minutes must be 1..60 and seed must fit an unsigned 64-bit integer")
    slug = re.sub(r"[^a-z0-9]+", "-", name.lower()).strip("-")
    if not slug:
        raise ValueError("battle title must contain a letter or digit")
    dt = scene["fixed_dt_seconds"]
    if not isinstance(dt, (float, int)) or not math.isfinite(dt) or dt <= 0:
        raise ValueError("invalid authored native fixed time step")
    rounds = round(args.minutes * 60 / dt)
    if not 1 <= rounds < 1 << 32:
        raise ValueError("battle duration cannot be represented as native fixed ticks")

    # A ship blueprint is a *real authored example*, not an invented stats
    # table. Do not quietly reinterpret an authored side's weapon layers as
    # another side's collision permissions.
    blueprints: dict[tuple[int, int], dict] = {}
    for ship in scene["ships"]:
        key = (ship["side"], ship["ship_type"])
        blueprints.setdefault(key, ship)

    requested = {1: args.bees, 2: args.humans}
    for side, units in requested.items():
        missing = [ship_type for ship_type, _ in units
                   if (side, ship_type) not in blueprints]
        if missing:
            available = sorted(kind for s, kind in blueprints if s == side)
            raise ValueError(
                f"no authoritative archetype on side {side} for {missing}; "
                f"available native ship types: {available}"
            )
        if any(blueprints[(side, ship_type)]["height"] > args.spacing * 0.8
               for ship_type, _ in units):
            raise ValueError(
                f"side {side} includes a large authored hull that needs wider "
                "initial placement spacing or a larger native map"
            )
        if any(ship_type in (8, 19) for ship_type, _ in units) and (
                not any(ship_type == 5 for ship_type, _ in units)):
            raise ValueError(
                f"side {side} needs an authored Carrier (type 5) for Drone/Striker children"
            )

    controlled = {
        "bees": {1}, "humans": {2}, "both": {1, 2}, "none": set(),
    }[args.control]
    host = args.host_side
    if host is None and controlled:
        host = 1 if 1 in controlled else 2
    if host is not None and host not in controlled:
        raise ValueError("--host-side must be a player-controlled side")

    scene["level_name"] = name
    scene["stage_name"] = name
    scene["activate_hivemind"] = False
    scene["activate_collision_asteroids"] = False
    scene["activate_fog_of_war"] = args.fog
    if host is not None:
        scene["local_player_side"] = host
    else:
        scene.pop("local_player_side", None)
    if args.empty_map:
        scene["mining_asteroids"] = []
        scene["map_objects"] = []
        scene["layout"] = {}
        scene["activate_mining"] = False

    max_reserved = max(
        [0] +
        [ship["entity_id"] for ship in scene["ships"]] +
        [resource["entity_id"] for key in ("mining_asteroids", "map_objects")
         for resource in scene.get(key, [])] +
        [box["entity_id"] for box in scene.get("layout", {}).get("add_static_boxes", [])] +
        [polygon["entity_id"] for polygon in
         scene.get("layout", {}).get("add_polygon_obstacles", [])]
    )
    ship_id = max(SHIP_ID_START, max_reserved + 1)
    weapon_id = max(WEAPON_ID_START, ship_id + sum(
        count for units in requested.values() for _, count in units
    ) + 1)
    squad_id = max(SQUAD_ID_START, ship_id + sum(
        count for units in requested.values() for _, count in units
    ) + 1)
    new_ships = []
    new_squads = []
    by_side: dict[int, list[dict]] = {1: [], 2: []}
    for side, units in requested.items():
        positions = placements(
            scene["bounds"], scene["starting_positions"][side - 1],
            side, sum(count for _, count in units), args.spacing,
        )
        pos_index = 0
        for kind, count in units:
            for _ in range(count):
                ship = copy.deepcopy(blueprints[(side, kind)])
                ship["entity_id"] = ship_id
                ship["position"] = positions[pos_index]
                ship["user_controlled"] = side in controlled
                if ship.get("persistent_fleet"):
                    ship["fleet_ship_id"] = ship_id
                else:
                    ship.pop("fleet_ship_id", None)
                if ship.get("special_weapon") is not None:
                    ship["special_weapon"]["weapon_id"] = weapon_id
                    weapon_id += 1
                by_side[side].append(ship)
                new_ships.append(ship)
                pos_index += 1
                ship_id += 1

    # Carrier children refer to a real same-side generated Carrier, never
    # to the old example's entity ID or a nonexistent placeholder.
    for side, entries in by_side.items():
        carriers = [ship["entity_id"] for ship in entries
                    if ship["ship_type"] == 5]
        child_index = 0
        for ship in entries:
            if ship["ship_type"] in (8, 19):
                carrier = carriers[child_index % len(carriers)]
                if ship["ship_type"] == 19:
                    ship["special"]["carrier_id"] = carrier
                else:
                    ship["drone_carrier_id"] = carrier
                child_index += 1

    by_side_squads: dict[int, list[dict]] = {1: [], 2: []}
    for side, units in requested.items():
        for kind, _ in units:
            matching = [ship["entity_id"] for ship in by_side[side]
                        if ship["ship_type"] == kind]
            for start in range(0, len(matching), MAX_SQUAD_MEMBERS):
                group = {
                    "item_id": squad_id, "side": side,
                    "members": matching[start:start + MAX_SQUAD_MEMBERS],
                    "shooting_strategy_id": 1,
                    "user_controlled": side in controlled,
                    "can_accept_input": side in controlled,
                    "command_bans": [],
                }
                by_side_squads[side].append(group)
                new_squads.append(group)
                squad_id += 1
    for side in controlled:
        total = len(by_side_squads[side])
        for index, squad in enumerate(by_side_squads[side], 1):
            squad["hotkey_number"] = index
            squad["original_squad_count"] = total

    # Keep one source-owned command director: AI is driven by the same
    # native scripted order pipeline as the original shipped skirmish,
    # not a second Python/simulation implementation.
    queues = []
    for side in (1, 2):
        if side in controlled:
            continue
        for squad in by_side_squads[side]:
            members = [ship for ship in by_side[side]
                       if ship["entity_id"] in squad["members"]]
            example = members[0]
            if example["base_speed"] == 0:
                orders = [{"kind": "hold"}]
            elif example.get("turrets") or example.get("special_weapon"):
                orders = [
                    {"kind": "aggressive", "enemy_squad_item_id": other["item_id"]}
                    for other in by_side_squads[3 - side]
                ]
            else:
                orders = [{"kind": "move_to_point",
                           "destination": scene["starting_positions"][2 - side]}]
            queues.append({
                "squad_item_id": squad["item_id"],
                "repeat": False if orders[0]["kind"] == "hold" else True,
                "orders": orders,
            })

    scene["ships"] = new_ships
    scene["squads"] = new_squads
    run["run_id"] = slug
    run["stage_name"] = name
    run["max_rounds"] = rounds
    run["next_squad_item_id"] = max(squad_id, ship_id, weapon_id) + 1
    run["connection_checkpoint"] = "battle-connection.json"
    run["notifications"] = "battle-notifications.jsonl"
    run["levels"] = [{
        "scene": "battle.scene.json", "world_id": 1, "level_id": 1,
        "seed": args.seed, "scripted_queues": queues,
    }]
    library = {
        "library_schema": 1,
        "games": [{
            "id": slug[:40], "title": name,
            "kind": "battle", "config": "battle.run.json",
        }],
    }
    return scene, run, library


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("output_dir", type=Path,
                        help="new directory for a complete playable native battle")
    parser.add_argument("--bees", type=fleet, default=fleet("21:4,18:1"),
                        help="side 1 fleet, TYPE:COUNT comma-separated")
    parser.add_argument("--humans", type=fleet, default=fleet("13:4,10:1"),
                        help="side 2 fleet, TYPE:COUNT comma-separated")
    parser.add_argument("--control", choices=("bees", "humans", "both", "none"),
                        default="bees", help="which sides have player input authority")
    parser.add_argument("--host-side", type=int, choices=(1, 2),
                        help="local host window side when both sides are controlled")
    parser.add_argument("--title", default="Custom Native Battle")
    parser.add_argument("--seed", type=int, default=12345)
    parser.add_argument("--minutes", type=int, default=4)
    parser.add_argument("--spacing", type=float, default=24)
    parser.add_argument("--fog", action="store_true",
                        help="use the authoritative native player fog system")
    parser.add_argument("--empty-map", action="store_true",
                        help="omit source mining resources, static boxes and map objects")
    parser.add_argument("--source-scene", type=Path,
                        default=EXAMPLES / "native-queen.scene.json",
                        help="native game scene supplying exact per-side ship archetypes")
    parser.add_argument("--source-run", type=Path,
                        default=EXAMPLES / "native-skirmish.run.json",
                        help="paired native run with the same map/physics asset matrix")
    args = parser.parse_args()
    try:
        scene, run, library = author(args)
        output = args.output_dir.resolve()
        output.mkdir(parents=True, exist_ok=False)
        for name, document in (
            ("battle.scene.json", scene),
            ("battle.run.json", run),
            ("library.json", library),
        ):
            (output / name).write_text(
                json.dumps(document, indent=2, ensure_ascii=False) + "\n",
                encoding="utf-8",
            )
    except (OSError, ValueError, KeyError, TypeError) as error:
        parser.exit(2, f"native battle authoring failed: {error}\n")
    print(json.dumps({
        "kind": "native_battle_authored",
        "title": scene["level_name"],
        "directory": str(output),
        "run": str(output / "battle.run.json"),
        "library": str(output / "library.json"),
        "ships": len(scene["ships"]),
        "squads": len(scene["squads"]),
        "player_sides": sorted({s["side"] for s in scene["squads"]
                                if s["user_controlled"]}),
        "fixed_rounds": run["max_rounds"],
    }, sort_keys=True))


if __name__ == "__main__":
    main()
