#!/usr/bin/env python3
"""Author Titania II's obstacle-free reinforcement lanes from real squad IDs.

Usage:
    python native/tools/author-titania2-waves.py run.json waves.json run-with-waves.json

The wave manifest lists authored source squad IDs, times, and directional
coordinates. A wave may name a later physical Unity capture containing its
actual spawned ships and squad. --scene-output writes their merged native
scene alongside a new run config; no ship attributes or IDs are synthesized.
"""
import argparse
import json
import math
from pathlib import Path


def fail(message):
    raise ValueError(message)


def number(value, name):
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        fail(f"{name} must be a number")
    value = float(value)
    if not math.isfinite(value):
        fail(f"{name} must be finite")
    return value


def pair(value, name):
    if not isinstance(value, list) or len(value) != 2:
        fail(f"{name} must be [x, y]")
    return [number(value[i], f"{name}[{i}]") for i in range(2)]


def lane_candidates(bounds, direction):
    """Source GetTitania2OffMapEntry's ordered candidate scan.

    Later live captures can encounter moving asteroids even when the
    starting scene has no obstacles. Reuse the source's thirteen candidate
    lanes and select the one confirmed by actual Unity point orders.
    """
    min_x, min_y, max_x, max_y = bounds
    x, y = direction
    horizontal = abs(x) >= abs(y)
    if not horizontal and y > 0:
        # Source never sends Beenoculars reinforcements from the top edge.
        x, y = (-1.0 if x < 0 else 1.0), max(0.35, min(0.85, abs(x)))
        horizontal = True
    positive = horizontal and x >= 0
    tangent = y if horizontal else x
    lo, hi = (min_y + 24.0, max_y - 24.0) if horizontal else (min_x + 24.0, max_x - 24.0)
    if hi < lo:
        fail("scene is too narrow for Titania II's 24-unit edge margins")
    fraction = (max(-1.0, min(1.0, tangent)) + 1.0) * 0.5
    preferred = lo + (hi - lo) * fraction
    for step in range(13):
        signed_step = 0 if step == 0 else ((step + 1) // 2) * (1 if step % 2 else -1)
        offset = min(max(preferred + signed_step * 28.0, lo), hi)
        if horizontal:
            spawn = [max_x + 80.0 if positive else min_x - 80.0, offset]
            entry = [max_x - 28.0 if positive else min_x + 28.0, offset]
        else:
            spawn, entry = [offset, min_y - 80.0], [offset, min_y + 28.0]
        if not all(map(math.isfinite, spawn + entry)):
            fail("generated reinforcement coordinates overflowed")
        yield spawn, entry


def lane(bounds, direction):
    """First source lane, used only without a live wave capture."""
    return next(lane_candidates(bounds, direction))


def verify_entry_orders(members, ships, entry, bounds, label):
    """Verify the source's actual pre-movement Squad.Move point orders.

    Without an obstacle map, Squad.MoveInternal applies Level.ForceBounds
    to entry + Ship.OffsetFromCenter for each mobile member. Even a moving
    collision asteroid may divert the source entry lane; only the capture's
    actual orders can identify which of the source's scanned lanes was used.
    """
    min_x, min_y, max_x, max_y = bounds
    for eid in members:
        ship = ships[eid]
        if ship.get("mobile") is not True:
            fail(f"{label} ship {eid} is not a mobile reinforcement")
        offset = ship.get("offset_from_center")
        target = ship.get("target_coordinates")
        if not isinstance(offset, dict) or not isinstance(target, dict):
            fail(f"{label} ship {eid} is missing an authored formation or entry order")
        offset_x = number(offset.get("x"), f"{label} ship {eid} offset.x")
        offset_y = number(offset.get("y"), f"{label} ship {eid} offset.y")
        target_x = number(target.get("x"), f"{label} ship {eid} target.x")
        target_y = number(target.get("y"), f"{label} ship {eid} target.y")
        expected_x = min(max(entry[0] + offset_x, min_x), max_x)
        expected_y = min(max(entry[1] + offset_y, min_y), max_y)
        if not (math.isclose(target_x, expected_x, rel_tol=1e-6, abs_tol=0.005) and
                math.isclose(target_y, expected_y, rel_tol=1e-6, abs_tol=0.005)):
            return False
    return True


def _source_records(scene, label):
    if not all(isinstance(scene.get(key), list)
               for key in ("ships", "squads", "obstacles", "map_objects")):
        fail(f"{label} needs real ship, squad, obstacle, and MapObject lists")
    squads = {}
    ships = {}
    entities = set()
    for item in scene["squads"]:
        if not isinstance(item, dict):
            fail(f"{label} contains a malformed squad")
        sid = item.get("item_id")
        if type(sid) is not int or sid <= 0 or sid in squads:
            fail(f"{label} has duplicate/invalid source squad IDs")
        squads[sid] = item
    for key in ("ships", "obstacles", "map_objects"):
        for item in scene[key]:
            if not isinstance(item, dict):
                fail(f"{label} contains a malformed physical entity")
            eid = item.get("entity_id")
            if type(eid) is not int or eid <= 0 or eid in entities:
                fail(f"{label} has duplicate/invalid physical entity IDs")
            entities.add(eid)
            if key == "ships":
                ships[eid] = item
    return squads, ships, entities


def _verify_capture(initial, capture):
    # Capture provenance cannot be established from legacy Unity exports.
    # Enforce every stable source-world field we can, and keep all conflicting
    # identities fatal. Never import the later capture's moved initial units.
    # Current HiveMind command authority and parent/carrier source identity
    # are not available in legacy scene exports. Those cannot safely
    # establish authentic deferred non-policy squads.
    if type(initial.get("schema_version")) is not int or initial["schema_version"] < 28:
        fail("future-wave captures require current Unity scene schema 28 or later")
    for key in ("schema_version", "stage_name", "level_name", "bee_side",
                "human_side", "fixed_dt_seconds", "has_obstacles", "min",
                "max", "beehive_size_pixels", "navigation_map_width",
                "navigation_map_height", "navigation_origin_world",
                "navigation_x_axis_world", "navigation_y_axis_world"):
        if key not in initial or key not in capture or capture[key] != initial[key]:
            fail(f"wave capture has a missing or different source {key}")
    start_frame = initial.get("source_frame_count")
    capture_frame = capture.get("source_frame_count")
    if (type(start_frame) is not int or type(capture_frame) is not int or
        start_frame < 0 or capture_frame <= start_frame):
        fail("wave capture needs a later source frame than the starting scene")
    initial_squads, initial_ships, _ = _source_records(initial, "starting scene")
    later_squads, later_ships, _ = _source_records(capture, "wave capture")
    # Stage IDs are never reassigned in one run. Motion, health and roster
    # membership may evolve, but these identities must not change.
    for entity_id in initial_ships.keys() & later_ships.keys():
        first = initial_ships[entity_id]
        later = later_ships[entity_id]
        for key in ("ship_type", "side", "fleet_ship_id", "persistent_fleet"):
            if key not in first or key not in later or first[key] != later[key]:
                fail(f"wave capture reuses physical ship {entity_id} with different {key}")
    for item_id in initial_squads.keys() & later_squads.keys():
        first = initial_squads[item_id]
        later = later_squads[item_id]
        for key in ("side", "user_controlled"):
            if first.get(key) != later.get(key):
                fail(f"wave capture reuses squad {item_id} with different {key}")


def expand(run, scene, manifest, read_capture=None):
    levels = run.get("levels")
    if not isinstance(levels, list) or len(levels) != 1:
        fail("Titania II authoring requires a one-Level run configuration")
    level = levels[0]
    dialogue = level.get("campaign_dialogue", {})
    if "titania_beenoculars" not in dialogue:
        fail("run configuration must select Titania Beenoculars dialogue")
    if level.get("reinforcements"):
        fail("run configuration already has reinforcements; refusing to replace them")
    if scene.get("has_obstacles") is not False:
        fail("automatic lanes require Titania II's cleared obstacle-free battlefield")
    # Unity checks real Physics2D colliders, not merely HasObstacles.
    # The current mission clears both physical obstacle populations.
    # Refuse first-lane prediction if source obstacles might block it.
    if any(not isinstance(scene.get(key), list) or scene[key]
           for key in ("obstacles", "map_objects")):
        fail("automatic lanes need empty captured obstacle and MapObject lists")
    mn = scene.get("min")
    mx = scene.get("max")
    if not isinstance(mn, dict) or not isinstance(mx, dict):
        fail("source scene needs authored min and max coordinates")
    min_x = number(mn.get("x"), "scene.min.x")
    min_y = number(mn.get("y"), "scene.min.y")
    max_x = number(mx.get("x"), "scene.max.x")
    max_y = number(mx.get("y"), "scene.max.y")
    if min_x >= max_x or min_y >= max_y:
        fail("source scene bounds are invalid")
    bounds = (min_x, min_y, max_x, max_y)

    source_squads, ships, entity_ids = _source_records(scene, "starting scene")
    bee_side = scene.get("bee_side")
    if not isinstance(bee_side, int) or bee_side <= 0:
        fail("source scene has no valid Bee side")
    waves = manifest.get("waves")
    if not isinstance(waves, list) or not waves:
        fail("manifest must contain at least one wave")
    prior = 0.0
    prior_capture_frame = None
    prior_capture_time = None
    used = set()
    output = []
    for wi, wave in enumerate(waves):
        label = f"wave {wi + 1}"
        allowed = {"after_seconds", "direction", "squad_item_ids", "capture"}
        if not {"after_seconds", "direction", "squad_item_ids"} <= set(wave) or (
            set(wave) - allowed
        ):
            fail(f"{label} needs time, direction, captured squad IDs and optional capture")
        capture = None
        if "capture" in wave:
            name = wave["capture"]
            if not isinstance(name, str) or not name.strip() or read_capture is None:
                fail(f"{label} needs an accessible Unity wave capture")
            capture = read_capture(name)
            _verify_capture(scene, capture)
        at = number(wave["after_seconds"], f"{label}.after_seconds")
        if at <= 0 or at < prior:
            fail(f"{label} must have a positive, nondecreasing time")
        if capture is not None:
            frame = capture["source_frame_count"]
            if (prior_capture_frame is not None and
                (frame < prior_capture_frame or
                 (frame == prior_capture_frame and at != prior_capture_time))):
                fail(f"{label} has a capture frame inconsistent with wave order")
            prior_capture_frame, prior_capture_time = frame, at
        prior = at
        dx, dy = pair(wave["direction"], f"{label}.direction")
        ids = wave["squad_item_ids"]
        if not isinstance(ids, list) or not ids or all(sid is None for sid in ids):
            fail(f"{label} must name at least one captured squad")
        # A null entry denotes an unavailable saved squad at that *original*
        # Unity list slot. AddTitania2BeeWave uses the source list length and
        # index for its lane offset even when a squad lookup returned null.
        horizontal = abs(dx) >= abs(dy)
        captured_squads, captured_ships = ({}, {})
        if capture is not None:
            captured_squads, captured_ships, _ = _source_records(
                capture, f"{label} capture"
            )
        for si, squad_id in enumerate(ids):
            if squad_id is None:
                continue
            if isinstance(squad_id, bool) or not isinstance(squad_id, int) or squad_id <= 0:
                fail(f"{label} has an invalid source squad ID")
            if squad_id in used:
                fail(f"source squad {squad_id} appears in multiple waves")
            used.add(squad_id)
            squad = (captured_squads if capture is not None else source_squads).get(squad_id)
            if squad is None or squad.get("side") != bee_side or squad.get("user_controlled") is not False:
                fail(f"{label} requires an authentic, non-player Bee squad {squad_id}")
            members = squad.get("member_ids")
            if not isinstance(members, list) or not members or (
                any(type(member) is not int or member <= 0 for member in members)
            ) or len(set(members)) != len(members):
                fail(f"source squad {squad_id} has invalid physical membership")
            selected_ships = captured_ships if capture is not None else ships
            for entity_id in members:
                ship = selected_ships.get(entity_id)
                if ship is None or ship.get("side") != bee_side or (
                    ship.get("is_rl_policy_controlled") is not False or
                    ship.get("has_bound_policy") is not False
                ):
                    fail(f"source squad {squad_id} has missing or policy-controlled physical ships")
            # Source AddTitania2BeeWave offsets one tangent lane per squad.
            lane_offset = (si - (len(ids) - 1) * 0.5) * 0.16
            direction = [dx if horizontal else dx + lane_offset,
                         dy + lane_offset if horizontal else dy]
            if capture is not None:
                matches = [(spawn, entry) for spawn, entry in lane_candidates(bounds, direction)
                           if verify_entry_orders(members, captured_ships, entry, bounds, label)]
                if not matches:
                    fail(f"{label} source movement orders match no authored entry lane")
                spawn, entry = matches[0]
                if any(not all(math.isclose(a, b, rel_tol=1e-6, abs_tol=0.005)
                               for a, b in zip(entry, other_entry))
                       for _, other_entry in matches[1:]):
                    fail(f"{label} has ambiguous captured entry orders")
                if squad_id in source_squads or any(eid in entity_ids for eid in members):
                    fail(f"{label} capture conflicts with an existing source identity")
                # Unity's GameState.GetId uses a monotonically advancing
                # Stage allocator. A ship spawned in a future wave must be
                # newer than every entity already created in the source
                # scene or an earlier captured wave.
                highest_created = max(entity_ids, default=0)
                if any(eid <= highest_created for eid in members):
                    fail(f"{label} includes a nonmonotonic future ship ID")
                # Retain the real source records, including weapons, movement,
                # HiveMind authority and each member's authored formation.
                scene["squads"].append(squad)
                source_squads[squad_id] = squad
                for eid in members:
                    ship = dict(captured_ships[eid])
                    # Unity has already issued the entry MoveToPoint when
                    # a live wave is captured. A fresh native scene cannot
                    # import that in-progress command: the deferred native
                    # wave will issue its own equivalent order on arrival.
                    # Require the capture to precede the first physics
                    # movement tick, preserving its real spawn position,
                    # speed, health, weapons and physical identity.
                    # Ship.ClearData assigns CurrentSpeed = Speed and
                    # does not establish zero rigidbody velocity as a
                    # source invariant. Preserve the actual velocity;
                    # only require the pre-FixedUpdate movement flags.
                    if (ship.get("is_moving") is not False or
                        ship.get("has_target_coordinates") is not True):
                        fail(f"{label} ship {eid} must be captured after its entry order but before movement")
                    ship["has_target_coordinates"] = False
                    ship["target_coordinates"] = {"x": 0.0, "y": 0.0}
                    scene["ships"].append(ship)
                    ships[eid] = ship
                    entity_ids.add(eid)
            else:
                spawn, entry = lane(bounds, direction)
            output.append({
                "after_seconds": at,
                "squad_item_ids": [squad_id],
                # A live Unity capture already contains the authoritative
                # off-map physical placement. Do not move it again (or
                # re-apply its adjusted formation offsets) at native setup.
                "spawn": None if capture is not None else spawn,
                "entry": entry,
            })
    level["reinforcements"] = output
    return output


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("run_config", type=Path)
    parser.add_argument("wave_manifest", type=Path)
    parser.add_argument("output_config", type=Path)
    parser.add_argument(
        "--scene-output", type=Path,
        help="new native scene JSON, required when any wave names a later capture",
    )
    args = parser.parse_args()
    run_path = args.run_config.resolve()
    output_path = args.output_config.resolve()
    if run_path == output_path or output_path.exists():
        fail("output must be a new file; existing configurations are not overwritten")
    if output_path.parent != run_path.parent:
        fail("output must stay beside the run config so relative asset paths remain valid")
    run = json.loads(run_path.read_text(encoding="utf-8"))
    manifest = json.loads(args.wave_manifest.read_text(encoding="utf-8"))
    if not isinstance(manifest, dict) or not isinstance(manifest.get("waves"), list):
        fail("wave manifest must have a waves array")
    capture_requested = any(
        isinstance(wave, dict) and "capture" in wave for wave in manifest["waves"]
    )
    if capture_requested != (args.scene_output is not None):
        fail("--scene-output is required exactly when captures are supplied")
    scene_output = args.scene_output.resolve() if args.scene_output else None
    if scene_output is not None:
        if (scene_output.parent != run_path.parent or
            scene_output == run_path or scene_output == output_path or
            scene_output.exists()):
            fail("new native scene must be a nonexistent file beside the run config")
    levels = run.get("levels", [])
    if not isinstance(levels, list) or len(levels) != 1:
        fail("expected exactly one Level")
    scene_path = (run_path.parent / levels[0]["scene"]).resolve()
    scene = json.loads(scene_path.read_text(encoding="utf-8"))
    captures = {}
    def read_capture(name):
        path = (args.wave_manifest.resolve().parent / name).resolve()
        if path == scene_path:
            fail("a wave capture cannot be the starting scene")
        if path not in captures:
            captures[path] = json.loads(path.read_text(encoding="utf-8"))
        return captures[path]

    plans = expand(run, scene, manifest, read_capture)
    if scene_output is not None:
        highest = max(squad["item_id"] for squad in scene["squads"])
        next_id = run.get("next_squad_item_id")
        if type(next_id) is not int or next_id <= highest:
            fail(
                "next_squad_item_id must be the real Stage pool successor "
                "above every captured squad; do not invent an ID"
            )
        level = run["levels"][0]
        level["scene"] = scene_output.name
    # Serialize both outputs before creating either one, preserving inputs.
    result = json.dumps(run, indent=2, ensure_ascii=False, allow_nan=False) + "\n"
    scene_result = (
        json.dumps(scene, indent=2, ensure_ascii=False, allow_nan=False) + "\n"
        if scene_output is not None else None
    )
    # The pair is a single logical output. If either new file cannot be
    # written, remove only the files this invocation itself created.
    created = []
    try:
        if scene_output is not None:
            with scene_output.open("x", encoding="utf-8") as handle:
                created.append(scene_output)
                handle.write(scene_result)
        with output_path.open("x", encoding="utf-8") as handle:
            created.append(output_path)
            handle.write(result)
    except OSError:
        for path in reversed(created):
            path.unlink(missing_ok=True)
        raise
    print(f"Authored {len(plans)} captured squad arrival plans in {output_path}")
    if scene_output is not None:
        print(f"Merged authentic deferred physical records into {scene_output}")


if __name__ == "__main__":
    try:
        main()
    except (ValueError, KeyError, TypeError, OSError, json.JSONDecodeError) as exc:
        raise SystemExit(f"Titania II wave authoring: {exc}")
