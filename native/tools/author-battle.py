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


def reinforcement(value: str) -> tuple[float, int, int, int]:
    """A timed deferred squad allocation: SECONDS:SIDE:TYPE:COUNT."""
    parts = value.split(":")
    if len(parts) != 4:
        raise argparse.ArgumentTypeError(
            "reinforcement format is SECONDS:SIDE:TYPE:COUNT (e.g. 45:2:13:4)"
        )
    try:
        seconds = float(parts[0])
        side, kind, count = (int(part) for part in parts[1:])
    except ValueError as error:
        raise argparse.ArgumentTypeError("invalid timed reinforcement numbers") from error
    if (not math.isfinite(seconds) or seconds <= 0 or
            side not in (1, 2) or not 0 <= kind < 24 or
            not 1 <= count <= 1024):
        raise argparse.ArgumentTypeError(
            "reinforcement requires positive finite seconds, side 1/2, "
            "ship type 0..23 and count 1..1024"
        )
    return seconds, side, kind, count


def death_reinforcement(value: str) -> tuple[int, int, int, int, int]:
    """TRIGGER_SIDE:TRIGGER_TYPE:ARRIVING_SIDE:ARRIVING_TYPE:COUNT."""
    parts = value.split(":")
    if len(parts) != 5:
        raise argparse.ArgumentTypeError(
            "on-death wave format: TRIGGER_SIDE:TRIGGER_TYPE:SIDE:TYPE:COUNT"
        )
    try:
        trigger_side, trigger_kind, side, kind, count = map(int, parts)
    except ValueError as error:
        raise argparse.ArgumentTypeError("invalid on-death wave numbers") from error
    if (trigger_side not in (1, 2) or side not in (1, 2) or
            not 0 <= trigger_kind < 24 or not 0 <= kind < 24 or
            not 1 <= count <= 1024):
        raise argparse.ArgumentTypeError(
            "on-death wave needs sides 1/2, ship types 0..23 and count 1..1024"
        )
    return trigger_side, trigger_kind, side, kind, count


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


def resize_empty_arena(scene: dict, map_size: float) -> None:
    """Native equivalent of the Unity RL MapSize/MapBorder transformation.

    One source-sized arena becomes both the playable and neural-training
    physical scene. The four trigger colliders retain the actual Unity
    24-unit thickness and 48-unit overhang; no scale is inferred at runtime.
    """
    bounds = scene["bounds"]
    minimum, maximum = bounds["min"], bounds["max"]
    center_x = (minimum[0] + maximum[0]) * 0.5
    center_y = (minimum[1] + maximum[1]) * 0.5
    half = map_size * 0.5
    scene["bounds"] = {
        "min": [center_x - half, center_y - half],
        "max": [center_x + half, center_y + half],
    }
    # Game-only authoring retains its left/right faction arrangement.
    # This is the existing native game's map-local orientation, not a
    # second training-only variant of CombatWorld.
    scene["starting_positions"] = [
        [center_x - map_size * 0.25, center_y],
        [center_x + map_size * 0.25, center_y],
    ]
    border_center = half + 12.0
    half_long = (map_size + 48.0) * 0.5
    borders = [
        ("Top Border", (center_x, center_y + border_center), (half_long, 12.0)),
        ("Bottom Border Variant", (center_x, center_y - border_center), (half_long, 12.0)),
        ("Left Border Variant", (center_x - border_center, center_y), (12.0, half_long)),
        ("Right Border Variant", (center_x + border_center, center_y), (12.0, half_long)),
    ]
    existing = {
        item["entity_id"] for item in scene.get("ships", [])
    } | {
        item["entity_id"] for group in ("mining_asteroids", "map_objects")
        for item in scene.get(group, [])
    }
    if existing and max(existing) > (1 << 63) - 6:
        raise ValueError("native arena has no free runtime IDs for physical borders")
    next_id = max(existing, default=0) + 1
    scene["layout"] = {"add_map_borders": []}
    for index, (prefab, position, (half_x, half_y)) in enumerate(borders):
        # Exact BoxCollider corners in obstacle-local space. This uses
        # NativeSceneBuilder's existing live-box collider path, so all
        # contacts (projectiles, Barges, drifting asteroids and navigation)
        # go through the one authoritative CombatWorld.
        scene["layout"]["add_map_borders"].append({
            "entity_id": next_id + index,
            "prefab_name": prefab,
            "position": list(position),
            "rotation_degrees": 0.0,
            "local_corners": [
                [-half_x, -half_y], [half_x, -half_y],
                [half_x, half_y], [-half_x, half_y],
            ],
        })


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
    if ("native_stage" not in run or len(run.get("levels", [])) != 1 or
            run.get("model_manifest") or run.get("hivemind") or
            not isinstance(run.get("fragment_spawn"), dict)):
        raise ValueError("the run template must contain native game-only stage assets")
    if args.arena_size is not None:
        # Imported obstacles and resource positions cannot be rescaled from
        # a scalar map size. A deliberate empty layout can use the exact
        # source MapBorder geometry without guessing those positions.
        if not args.empty_map:
            raise ValueError("--arena-size requires --empty-map (no authored resources to relocate)")
        # Native NavigationGrid.Scale=4 and the one-million-cell safety
        # limit cap a square physical arena at 4000 world units.
        if (not math.isfinite(args.arena_size) or
                not 64 <= args.arena_size <= 4000 or
                args.arena_size % 4 != 0):
            raise ValueError("--arena-size must be a finite 4-unit step from 64 to 4000")
        original_bounds = scene.get("bounds")
        if (not isinstance(original_bounds, dict) or
                not isinstance(original_bounds.get("min"), list) or
                not isinstance(original_bounds.get("max"), list) or
                any(len(original_bounds[key]) != 2 for key in ("min", "max")) or
                any(not isinstance(v, (int, float)) or not math.isfinite(v)
                    for key in ("min", "max") for v in original_bounds[key]) or
                any(original_bounds["min"][i] >= original_bounds["max"][i]
                    for i in (0, 1))):
            raise ValueError("resized native arena requires finite original Level bounds")
    if args.empty_map:
        scene["mining_asteroids"] = []
        scene["map_objects"] = []
        scene["layout"] = {}
        scene["activate_mining"] = False
    if args.arena_size is not None:
        resize_empty_arena(scene, args.arena_size)
    if args.lethal_borders:
        # Both playable and MA-POCA runs receive the identical physical
        # rule. Source RL MapBorder damage uses the ship's own health,
        # independent of projectile MakeShotsHarmless.
        scene["lethal_map_borders"] = True
    # Stage collision layers and asteroid prefab lists are immutable authored
    # assets, but the skirmish run template uses a smaller arena than the
    # Queen fleet source. A new battle must derive BOTH native fragment
    # movement bounds and half-widths from the actual selected scene. This
    # keeps asteroid respawns inside the same map in playable and PPO runs.
    bounds = scene.get("bounds")
    try:
        low, high = bounds["min"], bounds["max"]
        extents = [(float(high[i]) - float(low[i])) / 2 for i in (0, 1)]
    except (KeyError, TypeError, ValueError, IndexError) as error:
        raise ValueError("source native map bounds must have finite min/max coordinates") from error
    if any(not math.isfinite(v) or v <= 0 for v in extents) or any(
            not math.isfinite(float(v)) for point in (low, high) for v in point):
        raise ValueError("source native map bounds must be finite and nonempty")
    run["fragment_spawn"]["map_bounds"] = copy.deepcopy(bounds)
    run["fragment_spawn"]["half_map_extents"] = extents
    if set(scene.get("layout", {})) - {
        "add_static_boxes", "add_static_polygons", "add_map_borders",
    }:
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
    if args.objective_side is not None and args.objective_side not in (1, 2):
        raise ValueError("objective owner must be side 1 or 2")
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
    requested_counts = {
        (side, kind): count for side, units in requested.items()
        for kind, count in units
    }
    waves = [
        (when, side, kind, count, None)
        for when, side, kind, count in args.reinforcements
    ] + [
        (None, side, kind, count, (trigger_side, trigger_kind))
        for trigger_side, trigger_kind, side, kind, count
        in args.death_reinforcements
    ]
    deferred_counts: dict[tuple[int, int], int] = {}
    prior_time = 0.0
    for when, side, kind, count, trigger in waves:
        key = (side, kind)
        if when is not None:
            if when < prior_time or when >= args.minutes * 60:
                raise ValueError(
                    "reinforcements must have nondecreasing times strictly before battle end"
                )
            prior_time = when
        if kind in (8, 19) or (
                kind == 5 and any(child in dict(requested[side])
                                  for child in (8, 19))):
            raise ValueError(
                "carrier children and linked carriers cannot deploy in "
                "separate waves; use ordinary initial Carrier squads"
            )
        deferred_counts[key] = deferred_counts.get(key, 0) + count
        if deferred_counts[key] > requested_counts.get(key, 0):
            raise ValueError(
                f"reinforcement ships for side {side} type {kind} exceed "
                "that faction's authored total"
            )
    if any(
        sum(count - deferred_counts.get((side, kind), 0)
            for kind, count in requested[side]) == 0 for side in (1, 2)
    ):
        raise ValueError(
            "both factions need an initially deployed squad; "
            "do not defer an entire faction into reinforcement waves"
        )
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
    max_reserved = max(
        [0] +
        [ship["entity_id"] for ship in scene["ships"]] +
        [resource["entity_id"] for key in ("mining_asteroids", "map_objects")
         for resource in scene.get(key, [])] +
        [box["entity_id"] for box in
         scene.get("layout", {}).get("add_static_boxes", [])] +
        [polygon["entity_id"] for polygon in
         scene.get("layout", {}).get("add_static_polygons", [])]
    )
    # IDs are partitioned by faction, not allocated sequentially across
    # opposing armies. Campaign missions can now vary either opponent
    # freely while retaining the player's durable FleetShip and SavedSquad
    # identities, including when the player commands side 2.
    # Each side is bounded to 1024 ships and at most 1024 weapons/squads.
    side_span = 2048
    ship_base = max(SHIP_ID_START, max_reserved + 1)
    squad_base = max(SQUAD_ID_START, ship_base + 2 * side_span)
    weapon_base = max(WEAPON_ID_START, squad_base + 2 * side_span)
    new_ships = []
    new_squads = []
    by_side: dict[int, list[dict]] = {1: [], 2: []}
    for side, units in requested.items():
        ship_id = ship_base + (side - 1) * side_span
        weapon_id = weapon_base + (side - 1) * side_span
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

    # The source scene owns every future reinforcement ship up front.
    # Partition those real hulls into disjoint deferred squads, so the
    # native Level can reserve them at construction and deploy them once.
    # The first (count - deferred) ships remain immediately playable.
    wave_members = [set() for _ in waves]
    by_kind = {
        (side, kind): [ship["entity_id"] for ship in entries
                       if ship["ship_type"] == kind]
        for side, entries in by_side.items()
        for kind, _ in requested[side]
    }
    wave_cursor = {
        key: len(ships) - deferred_counts.get(key, 0)
        for key, ships in by_kind.items()
    }
    for index, (_, side, kind, count, _) in enumerate(waves):
        key = (side, kind)
        start = wave_cursor[key]
        wave_members[index].update(by_kind[key][start:start + count])
        wave_cursor[key] += count
    deferred_owner = {
        ship_id: index for index, members in enumerate(wave_members)
        for ship_id in members
    }
    by_side_squads: dict[int, list[dict]] = {1: [], 2: []}
    wave_squads: list[list[int]] = [[] for _ in waves]
    for side, units in requested.items():
        squad_id = squad_base + (side - 1) * side_span
        for kind, _ in units:
            matching = by_kind[(side, kind)]
            for wave in [None, *range(len(waves))]:
                members = [ship_id for ship_id in matching
                           if deferred_owner.get(ship_id) == wave]
                for start in range(0, len(members), MAX_SQUAD_MEMBERS):
                    group = {
                        "item_id": squad_id, "side": side,
                        "members": members[start:start + MAX_SQUAD_MEMBERS],
                        "shooting_strategy_id": 1,
                        "user_controlled": side in controlled,
                        "can_accept_input": side in controlled,
                        "command_bans": [],
                    }
                    by_side_squads[side].append(group)
                    new_squads.append(group)
                    if wave is not None:
                        wave_squads[wave].append(squad_id)
                    squad_id += 1
    deferred_squad_ids = {id for groups in wave_squads for id in groups}
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
        for squad_index, squad in enumerate(by_side_squads[side]):
            members = [ship for ship in by_side[side]
                       if ship["entity_id"] in squad["members"]]
            example = members[0]
            if example["base_speed"] == 0:
                orders = [{"kind": "hold"}]
            elif example["ship_type"] == 18 and example.get("special", {}).get("kind") == "scout":
                # ScoutingRun alone owns source exploration, discovery and
                # ten-second Scout Beacon timers. Derive Squad.MaxSight from
                # the actual authored members, never from nominal ship type.
                sights = [ship["sight"]["source_sight"] for ship in members]
                if any(not isinstance(value, (int, float)) or
                       not math.isfinite(value) or value < 0 for value in sights):
                    raise ValueError("scouting AI requires authored finite member sight ranges")
                orders = [{"kind": "scouting",
                           "max_sight": max(sights),
                           "human_side": scene["human_side"]}]
            elif example["ship_type"] in (5, 17):
                # A Carrier is a live return/rearm platform for its Striker
                # children, while the Queen owns a timed minion wave. Neither
                # should suicide into the enemy start when it has no weapon.
                # Their real specials continue updating in CombatWorld.
                orders = [{"kind": "hold"}]
            elif (example["ship_type"] in (4, 9) and
                  scene.get("activate_mining") and scene.get("mining_asteroids")):
                # The source Mining command already owns real ore extraction,
                # timed contact, fleet credit, and command completion. Select
                # a new live asteroid when the previous one is exhausted.
                orders = [{"kind": "mining_nearest"}]
            elif example.get("turrets") or example.get("special_weapon"):
                # One native command selects the nearest presently deployed
                # enemy through the real Level roster. Its authenticated
                # Aggressive lifetime ends on target death, then repeat
                # selects another; don't author O(own * enemy) JSON orders
                # or target ships reserved for a later reinforcement wave.
                orders = []
                if (example.get("special_weapon") and example["ship_type"] == 0):
                    combat_order = "charge_nearest"
                elif (example.get("special_weapon") and
                      example["ship_type"] in (10, 19, 22)):
                    combat_order = "bombing_nearest"
                else:
                    chosen = getattr(args, "ai_tactics", "aggressive")
                    tactics = {
                        "aggressive": "aggressive_nearest",
                        "circle": "circle_nearest",
                        "right-swipe": "right_swipe_nearest",
                        "left-swipe": "left_swipe_nearest",
                        "in-and-out": "in_and_out_nearest",
                    }
                    if chosen == "mixed":
                        # Stable authored squad order; do not consume the
                        # environment's training/gameplay RNG when assigning
                        # cosmetic or scripted tactical doctrine.
                        combat_order = tuple(tactics.values())[squad_index % 5]
                    else:
                        combat_order = tactics[chosen]
                if (example["ship_type"] != 2 and
                        any(kind == 2 for kind, _ in requested[side])):
                    # Between source combat lifetimes, repair injured
                    # members at actual same-side Beehive healing zones.
                    # If everyone is healthy, Heal is unavailable and the
                    # ordinary attack starts on the following fixed tick.
                    orders.append({"kind": "heal_available"})
                orders.append({"kind": combat_order})
            else:
                orders = [{"kind": "move_to_point",
                           "destination": scene["starting_positions"][2 - side]}]
            plan = {
                "squad_item_id": squad["item_id"],
                "repeat": False if orders[0]["kind"] == "hold" else True,
                "orders": orders,
            }
            if squad["item_id"] in deferred_squad_ids:
                plan["activation"] = {"kind": "on_deployment"}
            queues.append(plan)

    scene["ships"] = new_ships
    scene["squads"] = new_squads
    if args.arena_size is not None:
        # Border IDs must be allocated AFTER all real battle ships and
        # reinforcements. The common native layout installer requires each
        # added collider to exceed the scene's original highest entity ID;
        # feeding provisional border IDs back into ship allocation would
        # invert that ownership order and make the Level unloadable.
        original_ids = [
            *[ship["entity_id"] for ship in new_ships],
            *[resource["entity_id"] for key in ("mining_asteroids", "map_objects")
              for resource in scene.get(key, [])],
            *[obj["entity_id"] for key in ("add_static_boxes", "add_static_polygons")
              for obj in scene.get("layout", {}).get(key, [])],
        ]
        next_id = max(original_ids, default=0) + 1
        for index, border in enumerate(scene["layout"]["add_map_borders"]):
            border["entity_id"] = next_id + index
    run["run_id"] = slug
    run["stage_name"] = name
    run["max_rounds"] = rounds
    # Runtime minions/child squads allocate only above all original
    # authored entity ranges; they never reuse a side's reserved identity.
    run["next_squad_item_id"] = weapon_base + 2 * side_span
    run["connection_checkpoint"] = "battle-connection.json"
    run["notifications"] = "battle-notifications.jsonl"
    run["levels"] = [{
        "scene": "battle.scene.json", "world_id": 1, "level_id": 1,
        "seed": args.seed, "scripted_queues": queues,
    }]
    if waves:
        # Each native wave with several squad IDs must represent one
        # Carrier's linked child squads, not unrelated formation groups.
        # Publish each ordinary chunk as its own same-tick wave instead.
        # Reserved hulls use distinct authored positions rather than
        # invented pathfinding through unknown authored obstacles.
        deployments = []
        for index, (when, _, _, _, trigger) in enumerate(waves):
            if trigger is None:
                activation = {"after_seconds": when}
            else:
                trigger_side, trigger_kind = trigger
                eligible = [
                    ship["entity_id"] for ship in by_side[trigger_side]
                    if ship["ship_type"] == trigger_kind and
                    ship["entity_id"] not in deferred_owner
                ]
                if not eligible:
                    raise ValueError(
                        f"on-death reinforcement trigger side {trigger_side} "
                        f"type {trigger_kind} has no initially deployed hull"
                    )
                activation = {"on_ship_destroyed": eligible[0]}
            for squad_id in wave_squads[index]:
                deployments.append({
                    **activation, "squad_item_ids": [squad_id],
                })
        run["levels"][0]["reinforcements"] = deployments
    # These are the native Level's real survival/extraction contracts,
    # consumed by the same CombatWorld/terminal as campaign and training.
    # Authoring never installs a second special-objective simulator.
    owner = args.objective_side or (host if host is not None else 1)
    level = run["levels"][0]
    # No human is required for an automated benchmark or a policy-authored
    # mission, but the objective faction must still have a real, initially
    # deployed command owner. The shared Level accepts exactly the same
    # immediate scripted squad that the gameplay command director drives.
    initial_squad_ids = {
        squad["item_id"] for squad in by_side_squads[owner]
        if squad["item_id"] not in deferred_squad_ids
    }
    scripted_controller = any(
        queue["squad_item_id"] in initial_squad_ids and
        queue.get("activation", {"kind": "immediate"}) == {"kind": "immediate"} and
        queue.get("orders")
        for queue in queues
    )
    if args.objective == "survival":
        if owner not in controlled and not scripted_controller:
            raise ValueError("survival needs an initially deployed player or scripted defending squad")
        # The protected hull must physically exist at tick zero; a hull
        # reserved for a later reinforcement wave cannot be defended before
        # it is deployed. Likewise the initially active defenders need a
        # genuine mobile ship to play or learn the objective.
        defender = [ship for ship in by_side[owner]
                    if ship["entity_id"] not in deferred_owner]
        if not any(ship["base_speed"] > 0 for ship in defender):
            raise ValueError("survival requires an initially deployed mobile defender")
        candidates = [ship for ship in defender if
                      args.protect_type is None or ship["ship_type"] == args.protect_type]
        if not candidates:
            raise ValueError("survival protected hull type is not in the defending fleet")
        # Prefer an authored stationary support/base when no hull type was
        # specified; otherwise protect the first exact ship of that type.
        target = (next((ship for ship in candidates
                        if ship["base_speed"] == 0), candidates[0])
                  if args.protect_type is None else candidates[0])
        # A policy episode has a ten-tick safety margin below the authored
        # run budget. Its genuine survival terminal must fit that margin.
        # Set the ordinary default to the full game duration, but make a
        # newly authored trainable mission's default exactly achievable.
        budget = args.minutes * 60 - (10 * dt if args.trainable else 0)
        seconds = (args.survive_seconds if args.survive_seconds is not None
                   else budget)
        if (not math.isfinite(seconds) or seconds <= 0 or
                math.ceil(seconds / dt) * dt > budget + 1e-6):
            raise ValueError("survival seconds must fit the native fixed-step policy/game budget")
        level["survival"] = {
            "defending_side": owner,
            "protected_ship_id": target["entity_id"],
            "seconds": seconds,
        }
        if owner not in controlled:
            # Use the same source Squad.Guard and Hold lifetimes as native
            # player commands. An automated defense must defend its actual
            # protected hull rather than pursue enemy squads out of range.
            # On-deployment reinforcement scripts keep their activation.
            protected_squad = next(
                squad["item_id"] for squad in by_side_squads[owner]
                if target["entity_id"] in squad["members"]
            )
            defending_squads = {
                squad["item_id"]: squad for squad in by_side_squads[owner]
            }
            for queue in queues:
                squad = defending_squads.get(queue["squad_item_id"])
                if squad is None:
                    continue
                if squad["item_id"] == protected_squad:
                    queue["orders"] = [{"kind": "hold"}]
                    queue["repeat"] = False
                elif queue["orders"][-1]["kind"] in (
                        "bombing_nearest", "charge_nearest",
                        "mining_nearest", "scouting", "hold"):
                    # Guard is an ordinary squad-following command. It
                    # cannot execute a Fire Barge/Barge Bomb or Scout
                    # Beacon lifecycle. Preserve the actual specialist
                    # command for mobile defenders and the original Hold
                    # platform for Carriers/Queens. Only conventional
                    # weapon squads should guard the physical objective.
                    continue
                elif any(ship["entity_id"] in squad["members"] and
                         ship.get("base_speed", 0) > 0
                         for ship in by_side[owner]):
                    queue["orders"] = [{
                        "kind": "guard",
                        "friendly_squad_item_id": protected_squad,
                    }]
                    queue["repeat"] = True
                else:
                    queue["orders"] = [{"kind": "hold"}]
                    queue["repeat"] = False
    elif args.objective == "extraction":
        if owner not in controlled and not scripted_controller:
            raise ValueError("extraction needs an initially deployed player or scripted extracting squad")
        if not any(ship["base_speed"] > 0 and
                   ship["entity_id"] not in deferred_owner
                   for ship in by_side[owner]):
            raise ValueError("extraction requires an initially deployed mobile player ship")
        center = (args.extraction_center
                  if args.extraction_center is not None
                  else scene["starting_positions"][2 - owner])
        lower, upper = scene["bounds"]["min"], scene["bounds"]["max"]
        if (not all(math.isfinite(v) for v in center) or
                not all(lower[i] + 5 < center[i] < upper[i] - 5
                        for i in range(2))):
            raise ValueError("extraction center must be inside the native map with room for its exit zone")
        if not math.isfinite(args.extraction_radius) or args.extraction_radius <= 0:
            raise ValueError("extraction activation radius must be finite and positive")
        level["extraction"] = {
            "player_side": owner, "center": center,
            "half_extents": [5.0, 5.0],
            "activation_radius": args.extraction_radius, "layer": 19,
        }
        if owner not in controlled:
            # A headless AI extraction must actually attempt the authored
            # objective. Use the original native command director (not an
            # extra steering/AI implementation) to send one deployed mobile
            # squad toward the real trigger; other escorts keep their
            # source combat commands and reinforcement activation clocks.
            extraction_candidates = []
            by_ship_id = {
                ship["entity_id"]: ship for ship in by_side[owner]
            }
            for index, squad in enumerate(by_side_squads[owner]):
                if squad["item_id"] in deferred_squad_ids:
                    continue
                members = [by_ship_id[ship_id] for ship_id in squad["members"]]
                candidates = [
                    ship for ship in members
                    if ship.get("base_speed", 0) > 0 and
                    ship.get("drone_carrier_id") is None and
                    not (isinstance(ship.get("special"), dict) and
                         ship["special"].get("kind") == "striker")
                ]
                if not candidates:
                    continue
                # Scout hulls are authored reconnaissance ships that can
                # reach the actual extraction collider while their heavier
                # escorts keep attacking. Prefer other mobile ships over
                # Queens and Carriers, whose live minion/rearm roles make
                # them more useful to keep in the combat zone.
                fastest = max(ship["base_speed"] for ship in candidates)
                scout = any(ship["ship_type"] == 18 for ship in candidates)
                support_parent = all(ship["ship_type"] in (5, 17)
                                     for ship in candidates)
                extraction_candidates.append((
                    support_parent, not scout, -fastest, index, squad["item_id"]
                ))
            if not extraction_candidates:
                raise ValueError("scripted extraction has no initial mobile command owner")
            preferred = [item for item in extraction_candidates if not item[0]]
            if not preferred:
                preferred = extraction_candidates
            # Retain the physical authored source order for exact priority
            # ties, and send multiple independent squads when available.
            # Fast Scouts generally escape first; attackers stay as cover.
            preferred.sort()
            escape_count = max(1, (len(preferred) + 2) // 3)
            escaping = {item[-1] for item in preferred[:escape_count]}
            for queue in queues:
                if queue["squad_item_id"] not in escaping:
                    continue
                queue["orders"] = [{"kind": "move_to_point", "destination": center}]
                queue["repeat"] = False
    library = {
        "library_schema": 1,
        "games": [{
            "id": slug[:40], "title": name,
            "kind": "battle", "config": "battle.run.json",
        }],
    }
    return scene, run, library


def author_policy_scene(scene: dict, run: dict, args) -> tuple[dict, dict]:
    """Use source-authored v26 policy identities with this same CombatWorld."""
    source = load_json(args.policy_archetypes)
    if source.get("native_game_schema") != 1:
        raise ValueError("policy archetypes require a native_game_schema 1 source")
    policies = {
        (ship["side"], ship["ship_type"]): ship
        for ship in source.get("ships", [])
        if isinstance(ship, dict) and isinstance(ship.get("policy"), dict)
    }
    scene = copy.deepcopy(scene)
    run = copy.deepcopy(run)
    active = {1: 0, 2: 0}
    # Deferred native reinforcements have no live policy controller before
    # physical deployment. A fleet converter may leave these ships under
    # their exact authored command queues; the ordinary manual authoring
    # path makes no such override.
    scripted_reinforcement_ships = set(
        getattr(args, "scripted_reinforcement_ship_ids", ())
    )
    # Both teams require a physical policy participant at tick zero.
    # Deferred participants use those real models only after deployment;
    # counting them as starting controllers would admit empty policy teams.
    deferred_reinforcement_ships = set(getattr(
        args, "deferred_reinforcement_ship_ids", scripted_reinforcement_ships,
    ))
    for ship in scene["ships"]:
        ship["user_controlled"] = False
        # Native episode starting TSV uses actual ShipVitals, not nominal
        # capacity. The generated baseline is exact only for intact,
        # empty-cargo archetypes; refuse other scenes instead of guessing.
        if (ship.get("health") != ship.get("max_health") or
                ship.get("mined_cargo", 0)):
            raise ValueError("custom native policy starting ships must be at full health with no cargo")
        source_ship = (None if ship["entity_id"] in scripted_reinforcement_ships
                       else policies.get((ship["side"], ship["ship_type"])))
        if source_ship is None:
            ship.pop("policy", None)
            continue
        if ship["ship_type"] == 17:
            # A Queen's YellowJacket is dynamically born by the original
            # physical gameplay. Its neural Bomb observation is source
            # metadata, not another physical special or a training simulator.
            # Never copy the archetype's wave timing, spawn geometry or
            # optional live-population cap into the player's battle.
            queen = ship.get("special") or {}
            reference_queen = source_ship.get("special") or {}
            if (queen.get("kind") != "queen" or
                    reference_queen.get("kind") != "queen"):
                raise ValueError("policy Queen needs a real source-matched Queen special")
            spawn = queen.get("spawn") or {}
            reference_spawn = reference_queen.get("spawn") or {}
            minion = spawn.get("minion") or {}
            reference_minion = reference_spawn.get("minion") or {}
            weapon = minion.get("special_weapon") or {}
            reference_weapon = reference_minion.get("special_weapon") or {}
            if (not all(isinstance(item, dict) for item in
                        (minion, reference_minion, weapon, reference_weapon)) or
                    not isinstance(reference_weapon.get("observation"), dict) or
                    type(reference_minion.get("observed_weapon_type")) is not int):
                raise ValueError("policy Queen needs verified child Bomb observation data")
            def physical_minion(source_minion):
                physical = copy.deepcopy(source_minion)
                # Group observation and capacity are not copied. Source
                # gameplay chooses the *real* capacity; a policy's example
                # limit must never silently change that physical battle.
                physical.pop("observed_weapon_type", None)
                physical.pop("max_living_ships_per_side", None)
                physical["special_weapon"].pop("observation", None)
                return physical
            if physical_minion(minion) != physical_minion(reference_minion):
                raise ValueError("policy Queen child Bomb and physical hull differ from reference")
            minion["observed_weapon_type"] = reference_minion["observed_weapon_type"]
            weapon["observation"] = copy.deepcopy(reference_weapon["observation"])
        if ship.get("special_weapon") is not None:
            reference = source_ship.get("special_weapon") or {}
            physical = ship["special_weapon"]
            observation = reference.get("observation")
            # A Bomb's observation has source-only weapon timing/value
            # fields. It is safe to borrow those from the policy archetype
            # only when the actual physical weapon, mount and trigger shapes
            # match; a matching hull class, power and slot alone do not prove
            # its authored range/aim parameters.
            physical_fields = ("slot", "power", "mount_offset",
                               "charge_range", "proximity")
            if (not isinstance(observation, dict) or
                    any(reference.get(key) != physical.get(key)
                        for key in physical_fields)):
                raise ValueError(
                    f"policy ship {ship['ship_type']} needs matching physical "
                    "Bomb weapon geometry and source-authored observation"
                )
            physical["observation"] = copy.deepcopy(observation)
        ship["policy"] = {
            "agent_id": 2_000_000 + ship["entity_id"],
            "team_id": 0 if ship["side"] == 1 else 1,
            "quarter_turns": 0 if ship["side"] == 1 else 1,
            "movement_disabled": False,
        }
        if ship["entity_id"] not in deferred_reinforcement_ships:
            active[ship["side"]] += 1
    if not all(active.values()):
        raise ValueError(
            "native MA-POCA needs an initially deployed source-backed policy "
            "ship on both sides; "
            f"available: {sorted(policies)}"
        )
    for squad in scene["squads"]:
        squad["user_controlled"] = False
        squad["can_accept_input"] = False
        squad.pop("hotkey_number", None)
        squad.pop("original_squad_count", None)
    scene.pop("local_player_side", None)
    fleet = {
        side: sum(ship["max_tsv"] for ship in scene["ships"]
                  if ship["side"] == side and ship.get("persistent_fleet"))
        for side in (1, 2)
    }
    if min(fleet.values()) <= 0:
        raise ValueError("policy requires both sides' positive persistent fleet TSV")
    mining = sum(max(1, obj["health"]) for obj in scene.get("mining_asteroids", []))
    objects = sum(max(1, obj["max_health"]) for obj in scene.get("map_objects", []))
    assets = Path(__file__).resolve().parents[1] / "assets"
    boxes = {
        obj["name"]: obj["authored_health"]
        for obj in load_json(assets / "box-obstacle-colliders.json")["obstacles"]
    }
    polygons = load_json(assets / "polygon-obstacle-colliders.json")["obstacles"]
    if len(polygons) != 1:
        raise ValueError("ambiguous authored native polygon obstacle value")
    layout = scene.get("layout", {})
    statics = 0
    for placement in layout.get("add_static_boxes", []):
        name = placement["prefab_name"]
        if name not in boxes:
            raise ValueError(f"unknown static box archetype {name!r}")
        statics += max(1, boxes[name])
    statics += len(layout.get("add_static_polygons", [])) * max(
        1, polygons[0]["authored_health"]
    )
    scene["policy_episode"] = {
        "episode_number": 1,
        "bee_starting_tsv": fleet[1],
        "human_starting_tsv": fleet[2],
        "timeout_seconds": round(args.minutes * 60 - 10 * scene["fixed_dt_seconds"], 4),
        "discovery_baselines": [
            [fleet[2], mining, statics, objects],
            [fleet[1], mining, statics, objects],
        ],
        "movement_disabled_by_team": [False, False],
    }
    # Script only ordinary non-policy squads, never compete with a learner.
    policy_members = {
        ship["entity_id"] for ship in scene["ships"] if "policy" in ship
    }
    policy_squads = {
        squad["item_id"] for squad in scene["squads"]
        if any(member in policy_members for member in squad["members"])
    }
    run["levels"][0]["scripted_queues"] = [
        order for order in run["levels"][0].get("scripted_queues", [])
        if order["squad_item_id"] not in policy_squads
    ]
    # An initially deployed mobile, armed ship which the source scene
    # assigns to a human cannot lose its command owner merely because
    # no source-compatible policy archetype exists. Reject that hidden
    # training/gameplay divergence rather than training against a frozen
    # combatant. Carrier-family autonomous child actors are controlled
    # by their parent runtime, and passive support hulls need no orders.
    scripted_squads = {
        queue["squad_item_id"]
        for queue in run["levels"][0]["scripted_queues"]
        if queue.get("orders")
    }
    ships_by_id = {ship["entity_id"]: ship for ship in scene["ships"]}
    for squad in scene["squads"]:
        if squad["item_id"] in scripted_squads:
            continue
        for ship_id in squad["members"]:
            ship = ships_by_id[ship_id]
            if ship_id in scripted_reinforcement_ships or "policy" in ship:
                continue
            special = ship.get("special")
            automated_child = (
                ship.get("drone_carrier_id") is not None or
                isinstance(special, dict) and
                special.get("kind") == "striker" and
                special.get("carrier_id") is not None
            )
            if (ship.get("base_speed", 0) > 0 and
                    (ship.get("turrets") or ship.get("special_weapon")) and
                    not automated_child):
                raise ValueError(
                    f"native ship {ship_id} is an armed mobile combatant "
                    "without a source policy or a scripted squad command"
                )
    run["levels"][0]["scene"] = "battle.policy.scene.json"
    run["run_id"] += "-policy"
    return scene, run


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
    parser.add_argument("--ai-tactics",
                        choices=("aggressive", "circle", "right-swipe",
                                 "left-swipe", "in-and-out", "mixed"),
                        default="aggressive",
                        help="autonomous armed squads' native command strategy (default: aggressive); specialists retain their own actions")
    parser.add_argument("--title", default="Custom Native Battle")
    parser.add_argument("--seed", type=int, default=12345)
    parser.add_argument("--minutes", type=int, default=4)
    parser.add_argument("--objective", choices=("elimination", "survival", "extraction"),
                        default="elimination",
                        help="native elimination, protect-a-ship or escape-zone terminal condition")
    parser.add_argument("--objective-side", type=int, choices=(1, 2),
                        help="defending or extracting faction; defaults to local player side")
    parser.add_argument("--protect-type", type=int,
                        help="survival only: authored ship type to defend (first instance)")
    parser.add_argument("--survive-seconds", type=float,
                        help="survival only: survival duration (default: battle duration)")
    parser.add_argument("--extraction-center", type=float, nargs=2,
                        help="extraction only: world X Y (default: enemy home)")
    parser.add_argument("--extraction-radius", type=float, default=15,
                        help="extraction only: activation distance before exit trigger appears")
    parser.add_argument("--spacing", type=float, default=24)
    parser.add_argument("--reinforcement", action="append", default=[],
                        type=reinforcement, dest="reinforcements",
                        help="defer part of a faction's fleet until SECONDS:SIDE:TYPE:COUNT; repeat in time order")
    parser.add_argument("--reinforcement-on-death", action="append", default=[],
                        type=death_reinforcement, dest="death_reinforcements",
                        help="deploy when an initially active ship dies: TRIGGER_SIDE:TRIGGER_TYPE:SIDE:TYPE:COUNT")
    parser.add_argument("--fog", action="store_true",
                        help="use the authoritative native player fog system")
    parser.add_argument("--empty-map", action="store_true",
                        help="omit source mining resources, static boxes and map objects")
    parser.add_argument("--arena-size", type=float,
                        help="with --empty-map: square physical arena in 4-unit steps (64..4000), including source-sized trigger borders")
    parser.add_argument("--lethal-borders", action="store_true",
                        help="with --arena-size: make physical border contact immediately damage/kill ships in both gameplay and training")
    parser.add_argument("--trainable", action="store_true",
                        help="also author a matching native MA-POCA policy scene and run")
    parser.add_argument("--policy-reinforcements", action="store_true",
                        help="with --trainable, enroll supported authored reinforcement ships "
                             "into their actual policy teams only on physical deployment "
                             "(otherwise they retain scripted AI)")
    parser.add_argument("--policy-archetypes", type=Path,
                        default=EXAMPLES / "native-queen.policy.scene.json",
                        help="existing native policy ship, Queen minion and Bomb observation definitions")
    parser.add_argument("--source-scene", type=Path,
                        default=EXAMPLES / "native-queen.scene.json",
                        help="native game scene supplying exact per-side ship archetypes")
    parser.add_argument("--source-run", type=Path,
                        default=EXAMPLES / "native-skirmish.run.json",
                        help="paired native run with the same map/physics asset matrix")
    args = parser.parse_args()
    if args.policy_reinforcements and not args.trainable:
        parser.error("--policy-reinforcements requires --trainable")
    if args.lethal_borders and args.arena_size is None:
        parser.error("--lethal-borders requires --arena-size")
    if args.objective != "survival" and (
            args.protect_type is not None or args.survive_seconds is not None):
        parser.error("--protect-type and --survive-seconds require --objective survival")
    if args.objective != "extraction" and (
            args.extraction_center is not None or args.extraction_radius != 15):
        parser.error("--extraction-center and --extraction-radius require --objective extraction")
    try:
        scene, run, library = author(args)
        policy = None
        if args.trainable:
            # Share the fleet converter's exact deferred-roster contract,
            # rather than enrolling a ship before its real reinforcement
            # squad and physical body exist in the native Level.
            from prepare_fleet_training import (
                deferred_reinforcement_ships, verify_same_battle,
            )
            reserved = deferred_reinforcement_ships(
                scene, run,
                require_scripted_fallback=not args.policy_reinforcements,
            )
            args.deferred_reinforcement_ship_ids = reserved
            args.scripted_reinforcement_ship_ids = (
                set() if args.policy_reinforcements else reserved
            )
            policy = author_policy_scene(scene, run, args)
            verify_same_battle(scene, run, policy[0], policy[1])
        output = args.output_dir.resolve()
        output.mkdir(parents=True, exist_ok=False)
        documents = [
            ("battle.scene.json", scene),
            ("battle.run.json", run),
            ("library.json", library),
        ]
        if policy is not None:
            documents.extend([
                ("battle.policy.scene.json", policy[0]),
                ("battle.policy.run.json", policy[1]),
            ])
        for name, document in documents:
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
        "objective": args.objective,
        "ai_tactics": args.ai_tactics,
        "arena_size": args.arena_size,
        "physical_map_borders": 4 if args.arena_size is not None else 0,
        "lethal_map_borders": args.lethal_borders,
        "reinforcement_waves": len(run["levels"][0].get("reinforcements", [])),
        "reinforcement_ships": (
            sum(wave[3] for wave in args.reinforcements) +
            sum(wave[4] for wave in args.death_reinforcements)
        ),
        "native_policy_run": (str(output / "battle.policy.run.json")
                              if args.trainable else None),
    }, sort_keys=True))


if __name__ == "__main__":
    main()
