#!/usr/bin/env python3
"""Author Titania II's obstacle-free reinforcement lanes from real squad IDs.

Usage:
    python native/tools/author-titania2-waves.py run.json waves.json run-with-waves.json

The wave manifest lists authored source squad IDs, times, and directional
coordinates. This tool computes only Unity's off-map spawn/entry positions;
it never synthesizes a ship, squad, upgrade, weapon, or entity ID.
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


def lane(bounds, direction):
    """GetTitania2OffMapEntry's first accepted lane in a clear battlefield."""
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
    offset = lo + (hi - lo) * fraction
    if horizontal:
        spawn = [max_x + 80.0 if positive else min_x - 80.0, offset]
        entry = [max_x - 28.0 if positive else min_x + 28.0, offset]
    else:
        spawn, entry = [offset, min_y - 80.0], [offset, min_y + 28.0]
    if not all(map(math.isfinite, spawn + entry)):
        fail("generated reinforcement coordinates overflowed")
    return spawn, entry


def expand(run, scene, manifest):
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

    source_squads = {s["item_id"]: s for s in scene.get("squads", [])}
    ships = {ship["entity_id"]: ship for ship in scene.get("ships", [])}
    bee_side = scene.get("bee_side")
    if not isinstance(bee_side, int) or bee_side <= 0:
        fail("source scene has no valid Bee side")
    waves = manifest.get("waves")
    if not isinstance(waves, list) or not waves:
        fail("manifest must contain at least one wave")
    prior = 0.0
    used = set()
    output = []
    for wi, wave in enumerate(waves):
        label = f"wave {wi + 1}"
        if set(wave) != {"after_seconds", "direction", "squad_item_ids"}:
            fail(f"{label} must contain exactly after_seconds, direction and squad_item_ids")
        at = number(wave["after_seconds"], f"{label}.after_seconds")
        if at <= 0 or at < prior:
            fail(f"{label} must have a positive, nondecreasing time")
        prior = at
        dx, dy = pair(wave["direction"], f"{label}.direction")
        ids = wave["squad_item_ids"]
        if not isinstance(ids, list) or not ids:
            fail(f"{label} must name at least one captured squad")
        horizontal = abs(dx) >= abs(dy)
        for si, squad_id in enumerate(ids):
            if isinstance(squad_id, bool) or not isinstance(squad_id, int) or squad_id <= 0:
                fail(f"{label} has an invalid source squad ID")
            if squad_id in used:
                fail(f"source squad {squad_id} appears in multiple waves")
            used.add(squad_id)
            squad = source_squads.get(squad_id)
            if squad is None or squad.get("side") != bee_side or squad.get("user_controlled"):
                fail(f"{label} requires an authentic, non-player Bee squad {squad_id}")
            members = squad.get("member_ids")
            if not isinstance(members, list) or not members:
                fail(f"source squad {squad_id} has no physical members")
            for entity_id in members:
                ship = ships.get(entity_id)
                if ship is None or ship.get("side") != bee_side or ship.get("is_rl_policy_controlled"):
                    fail(f"source squad {squad_id} has missing or policy-controlled physical ships")
            # Source AddTitania2BeeWave offsets one tangent lane per squad.
            lane_offset = (si - (len(ids) - 1) * 0.5) * 0.16
            direction = [dx if horizontal else dx + lane_offset,
                         dy + lane_offset if horizontal else dy]
            spawn, entry = lane(bounds, direction)
            output.append({
                "after_seconds": at,
                "squad_item_ids": [squad_id],
                "spawn": spawn,
                "entry": entry,
            })
    level["reinforcements"] = output
    return output


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("run_config", type=Path)
    parser.add_argument("wave_manifest", type=Path)
    parser.add_argument("output_config", type=Path)
    args = parser.parse_args()
    run_path = args.run_config.resolve()
    output_path = args.output_config.resolve()
    if run_path == output_path or output_path.exists():
        fail("output must be a new file; existing configurations are not overwritten")
    if output_path.parent != run_path.parent:
        fail("output must stay beside the run config so relative asset paths remain valid")
    run = json.loads(run_path.read_text(encoding="utf-8"))
    manifest = json.loads(args.wave_manifest.read_text(encoding="utf-8"))
    levels = run.get("levels", [])
    if not isinstance(levels, list) or len(levels) != 1:
        fail("expected exactly one Level")
    scene_path = (run_path.parent / levels[0]["scene"]).resolve()
    scene = json.loads(scene_path.read_text(encoding="utf-8"))
    plans = expand(run, scene, manifest)
    with output_path.open("x", encoding="utf-8") as handle:
        json.dump(run, handle, indent=2, ensure_ascii=False, allow_nan=False)
        handle.write("\n")
    print(f"Authored {len(plans)} source-verified squad arrivals in {output_path}")


if __name__ == "__main__":
    try:
        main()
    except (ValueError, KeyError, TypeError, OSError, json.JSONDecodeError) as exc:
        raise SystemExit(f"Titania II wave authoring: {exc}")
