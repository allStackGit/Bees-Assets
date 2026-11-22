#!/usr/bin/env python3
"""Prepare a complete original ten-stage native Bees Challenge career.

Calls the existing authentic stage importer and career assembler. All scenes
remain authoritative native Bees games; no gameplay or neural simulation is
implemented here. The entire career is published through one directory
rename, avoiding half-created menus or stale first-stage saves.
"""
import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile

ROOT = Path(__file__).resolve().parents[2]
TOOLS = Path(__file__).resolve().parent
SOURCE = ROOT / "Scripts/Data/challenge_levels.json"


def keyed_path(raw, label):
    if "=" not in raw:
        raise argparse.ArgumentTypeError(f"{label}: expected INDEX=PATH")
    number, path = raw.split("=", 1)
    if not number.isdigit() or not path.strip():
        raise argparse.ArgumentTypeError(f"{label}: expected INDEX=PATH")
    return int(number), Path(path).expanduser().resolve()


def keyed_fleet(raw):
    if "=" not in raw:
        raise argparse.ArgumentTypeError("--fleet: expected LEVEL=TYPE:COUNT[,TYPE:COUNT]")
    number, fleet = raw.split("=", 1)
    if not number.isdigit() or not fleet.strip():
        raise argparse.ArgumentTypeError("--fleet: expected LEVEL=TYPE:COUNT[,TYPE:COUNT]")
    return int(number), fleet


def unique_keys(items, required, label):
    assigned = {}
    for key, value in items:
        if key not in required or key in assigned:
            raise ValueError(f"{label} has a repeated or unknown index: {key}")
        assigned[key] = value
    if assigned.keys() != required:
        raise ValueError(f"{label} missing required indices: {sorted(required - assigned.keys())}")
    return assigned


def run_stage(command):
    # Python binaries are invoked directly on Windows and Linux. Each
    # authoring tool already validates the original source and returns a
    # nonzero status before publishing invalid physical scene assets.
    subprocess.run(command, check=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("output_dir", type=Path, help="new complete Challenge career directory")
    parser.add_argument("--map-scene", action="append", type=lambda x: keyed_path(x, "--map-scene"), required=True,
                        help="original native map INDEX=SCENE; repeat for source map indices")
    parser.add_argument("--map-run", action="append", type=lambda x: keyed_path(x, "--map-run"), required=True,
                        help="paired map INDEX=RUN; repeat for source map indices")
    parser.add_argument("--fleet", action="append", type=keyed_fleet, required=True,
                        help="source player fleet LEVEL=TYPE:COUNT[,TYPE:COUNT]; repeat for all 10")
    parser.add_argument("--max-squad-size", type=int, required=True,
                        help="actual Unity Configuration.MaxSquadSize for the player")
    parser.add_argument("--archetype-scene", type=Path, action="append", default=[],
                        help="additional original physical ship archetype scene")
    parser.add_argument("--stage-asteroid-minimum-rate", type=int)
    parser.add_argument("--stage-asteroid-maximum-rate", type=int)
    parser.add_argument("--seed", type=int, default=12345)
    parser.add_argument("--minutes", type=int, default=15)
    args = parser.parse_args()
    output = args.output_dir.expanduser().absolute()
    parent = output.parent.resolve()
    if output.exists() or output.is_symlink() or not parent.is_dir():
        parser.error("output must be a new directory under an existing parent")
    if not 1 <= args.max_squad_size <= 1024:
        parser.error("max squad size must be the actual source value in 1..1024")
    if args.minutes < 1 or args.seed < 0:
        parser.error("seed must be nonnegative and minutes positive")
    try:
        source = json.loads(SOURCE.read_text(encoding="utf-8"))
        levels = source["Levels"]
        if len(levels) != 10 or sorted(level["Id"] for level in levels) != list(range(10)):
            raise ValueError("source Challenge must have ten original Levels")
        maps = {level["MapIndex"] for level in levels}
        scenes = unique_keys(args.map_scene, maps, "source map scenes")
        runs = unique_keys(args.map_run, maps, "source map runs")
        fleets = unique_keys(args.fleet, set(range(10)), "source player fleets")
        for path in list(scenes.values()) + list(runs.values()) + [
            item.expanduser().resolve() for item in args.archetype_scene
        ]:
            if not path.is_file() or path.is_symlink():
                raise ValueError(f"source input is missing or not a normal file: {path}")
        has_asteroids = any(level["AsteroidOption"] for level in levels)
        if has_asteroids and (
            args.stage_asteroid_minimum_rate is None or
            args.stage_asteroid_maximum_rate is None
        ):
            raise ValueError("original asteroid stages require Stage minimum and maximum spawn rates")
        if (args.stage_asteroid_minimum_rate is not None or
                args.stage_asteroid_maximum_rate is not None):
            if (args.stage_asteroid_minimum_rate is None or
                args.stage_asteroid_maximum_rate is None or
                not 1 <= args.stage_asteroid_minimum_rate <=
                    args.stage_asteroid_maximum_rate <= 3600):
                raise ValueError("source asteroid spawn rates must satisfy 1 <= minimum <= maximum <= 3600")
        staging = Path(tempfile.mkdtemp(prefix=".bees-native-challenge-", dir=parent))
        try:
            for level in sorted(levels, key=lambda item: item["Id"]):
                index = level["Id"]
                map_index = level["MapIndex"]
                command = [
                    sys.executable, str(TOOLS / "author-challenge.py"),
                    str(staging / f"level-{index:02d}"),
                    "--level", str(index),
                    "--human-fleet", fleets[index],
                    "--source-scene", str(scenes[map_index]),
                    "--source-run", str(runs[map_index]),
                    "--seed", str(args.seed + index),
                    "--minutes", str(args.minutes),
                ]
                for path in args.archetype_scene:
                    command.extend(["--archetype-scene", str(path.expanduser().resolve())])
                if has_asteroids:
                    command.extend([
                        "--stage-asteroid-minimum-rate", str(args.stage_asteroid_minimum_rate),
                        "--stage-asteroid-maximum-rate", str(args.stage_asteroid_maximum_rate),
                    ])
                run_stage(command)
            run_stage([
                sys.executable, str(TOOLS / "assemble-challenge.py"),
                str(staging), "--max-squad-size", str(args.max_squad_size),
            ])
            # The final publication is one directory rename. No partial
            # Challenge catalog is visible to the library if a source
            # map, fleet, seed, simulator contract or career check fails.
            if output.exists() or output.is_symlink():
                raise ValueError("Challenge destination appeared during authoring")
            staging.rename(output)
        finally:
            # After publication the temporary pathname no longer exists.
            # Before publication, even a failed stage leaves no half-built
            # career directory on either Windows or Linux.
            if staging.exists():
                shutil.rmtree(staging)
        print(json.dumps({
            "kind": "native_original_challenge_ready",
            "levels": 10,
            "source_maps": sorted(maps),
            "max_squad_ships": args.max_squad_size,
            "library": str(output / "library.json"),
            "catalog": str(output / "challenge.catalog.json"),
        }, sort_keys=True))
    except (OSError, ValueError, KeyError, TypeError, subprocess.CalledProcessError) as error:
        parser.exit(2, f"original native Challenge preparation failed: {error}\n")


if __name__ == "__main__":
    main()
