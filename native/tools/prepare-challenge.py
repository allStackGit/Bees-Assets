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


def challenge_library_install(output, library_path, gate):
    """Bind an existing native campaign and its source milestone without copying games."""
    if library_path.is_symlink() or not library_path.is_file():
        raise ValueError("existing native game library must be a regular file")
    root = library_path.parent.resolve()
    if output.parent.resolve() != root:
        raise ValueError("installed Challenge must be a direct child of the existing game root")
    original_bytes = library_path.read_bytes()
    library = json.loads(original_bytes)
    if library.get("library_schema") != 1 or not isinstance(library.get("games"), list):
        raise ValueError("existing native game library requires schema 1 and games")
    if any(game.get("id") == "original-human-challenge" for game in library["games"]):
        raise ValueError("Challenge is already installed in this game library")
    matches = [game for game in library["games"]
               if game.get("id") == gate[0] and game.get("kind") == "campaign"]
    if len(matches) != 1:
        raise ValueError("Challenge unlock campaign is absent or ambiguous")
    config = matches[0].get("config")
    if not isinstance(config, str) or not config:
        raise ValueError("unlock campaign has no native catalog path")
    relative = Path(config)
    if relative.is_absolute() or ".." in relative.parts:
        raise ValueError("unlock campaign path must remain within the game root")
    source_catalog = (root / relative).resolve()
    if not source_catalog.is_relative_to(root) or not source_catalog.is_file():
        raise ValueError("unlock campaign catalog is missing or outside the game root")
    catalog = json.loads(source_catalog.read_text(encoding="utf-8"))
    if catalog.get("campaign_id") != gate[0] or not any(
            battle.get("mission", {}).get("id") == gate[1]
            for battle in catalog.get("battles", [])):
        raise ValueError("Challenge unlock milestone is absent from its source campaign")
    return library, original_bytes


def publish_challenge_library(library_path, library, original_bytes, gate, challenge_dir):
    """Publish a visible menu entry only after every Challenge stage is complete.

    A failed write cannot expose a partial catalog. An unrelated library edit
    made while authoring is never silently overwritten.
    """
    if library_path.read_bytes() != original_bytes:
        raise ValueError("native game library changed during Challenge authoring")
    updated = dict(library)
    updated["games"] = list(library["games"]) + [{
        "id": "original-human-challenge",
        "title": "Challenge (10 original levels)",
        "kind": "challenge",
        "config": f"{challenge_dir.name}/challenge.catalog.json",
        "unlock_after": {"campaign_id": gate[0], "mission_id": gate[1]},
    }]
    temporary = None
    try:
        with tempfile.NamedTemporaryFile(
            mode="w", encoding="utf-8", dir=library_path.parent,
            prefix=".bees-library-", suffix=".json", delete=False,
        ) as stream:
            temporary = Path(stream.name)
            json.dump(updated, stream, indent=2, ensure_ascii=False)
            stream.write("\\n")
            stream.flush()
            os.fsync(stream.fileno())
        # The updated menu becomes visible atomically. The catalog and all
        # ten original scenes were already published by the directory rename.
        if library_path.read_bytes() != original_bytes:
            raise ValueError("native game library changed during Challenge publication")
        os.replace(temporary, library_path)
    finally:
        if temporary is not None and temporary.exists():
            temporary.unlink()


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
    parser.add_argument("--install-into-library", type=Path,
                        help="existing native game root library.json to extend atomically")
    parser.add_argument("--unlock-after", metavar="CAMPAIGN:MISSION",
                        help="original completed campaign milestone unlocking Challenge")
    args = parser.parse_args()
    output = args.output_dir.expanduser().absolute()
    parent = output.parent.resolve()
    if output.exists() or output.is_symlink() or not parent.is_dir():
        parser.error("output must be a new directory under an existing parent")
    if (args.install_into_library is None) != (args.unlock_after is None):
        parser.error("--install-into-library and --unlock-after must be supplied together")
    gate = None
    if args.unlock_after is not None:
        parts = args.unlock_after.split(":", 1)
        if len(parts) != 2 or not all(parts):
            parser.error("--unlock-after requires CAMPAIGN:MISSION")
        gate = parts
    if not 1 <= args.max_squad_size <= 1024:
        parser.error("max squad size must be the actual source value in 1..1024")
    if args.minutes < 1 or args.seed < 0:
        parser.error("seed must be nonnegative and minutes positive")
    try:
        install_path = (args.install_into_library.expanduser().absolute()
                        if args.install_into_library is not None else None)
        original_library = None
        original_library_bytes = None
        if install_path is not None:
            original_library, original_library_bytes = challenge_library_install(
                output, install_path, gate,
            )
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
            if install_path is not None:
                # Standalone stage assembly deliberately does not reference
                # the source campaign's paths. The original shared library
                # alone links both careers under the existing native root.
                publish_challenge_library(
                    install_path, original_library, original_library_bytes,
                    gate, output,
                )
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
            "installed_library": str(install_path) if install_path else None,
            "unlocked_after": args.unlock_after,
        }, sort_keys=True))
    except (OSError, ValueError, KeyError, TypeError, subprocess.CalledProcessError) as error:
        parser.exit(2, f"original native Challenge preparation failed: {error}\n")


if __name__ == "__main__":
    main()
