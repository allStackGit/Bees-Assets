#!/usr/bin/env python3
"""Assemble ten source-authored native Challenge battles into one career.

This consumes only outputs of author-challenge.py, and uses the ordinary
BattleCatalog's durable victory/score/fleet/replay ledger. It refuses a
missing stage, altered original identity or colliding player FleetShip
class rather than silently granting progress for a different encounter.
"""
import argparse
import json
from pathlib import Path

LEVEL_COUNT = 10
SOURCE_LEVELS = Path(__file__).resolve().parents[2] / "Scripts/Data/challenge_levels.json"

def load(path, root):
    if path.is_symlink() or not path.is_file() or not path.resolve().is_relative_to(root):
        raise ValueError(f"untrusted or missing native Challenge source: {path}")
    return json.loads(path.read_text(encoding="utf-8"))

def assemble(root, gate, base_library, max_squad_ships):
    original_source = json.loads(SOURCE_LEVELS.read_text(encoding="utf-8"))
    levels = original_source.get("Levels")
    if not isinstance(levels, list) or len(levels) != LEVEL_COUNT or sorted(
            level.get("Id", -1) for level in levels) != list(range(LEVEL_COUNT)):
        raise ValueError("the ten original Unity Challenge definitions changed")
    original_source_by_id = {level["Id"]: level for level in levels}
    original = []
    battles = []
    persistent = {}
    for index in range(LEVEL_COUNT):
        stage = root / f"level-{index:02d}"
        if stage.is_symlink() or not stage.is_dir():
            raise ValueError(f"the original Challenge level {index} was not authored")
        source = load(stage / "challenge-source.json", root)
        scene = load(stage / "challenge.scene.json", root)
        run = load(stage / "challenge.run.json", root)
        if (source.get("schema_version") != 1 or
                source.get("source_level_id") != index or
                source.get("source_name") != original_source_by_id[index]["Name"] or
                source.get("source_map_index") != original_source_by_id[index]["MapIndex"] or
                scene.get("native_game_schema") != 1 or
                scene.get("bee_side") != 1 or scene.get("human_side") != 2 or
                scene.get("policy_episode") is not None or
                scene.get("activate_hivemind") is True or
                run.get("host_recovery_checkpoint") != "native-host-checkpoint.json" or
                len(run.get("levels", [])) != 1 or
                run["levels"][0].get("scene") != "challenge.scene.json" or
                run["levels"][0].get("supply_capacity") != {
                    "player_side": 2,
                    "max_tsv": original_source_by_id[index]["SupplyCapacity"]
                }):
            raise ValueError(f"original Challenge level {index} has a mismatched native source contract")
        # Explicit source identity and manifest are not independently
        # authored scene instructions. Every generated enemy needs one
        # original squad mapping; a missing one cannot be reconstructed.
        squads = [s for s in scene["squads"] if s["side"] == 1]
        if len(squads) != len(source["original_squads"]) or any(
                row["native_item_id"] != squads[i]["item_id"]
                for i, row in enumerate(source["original_squads"])):
            raise ValueError(f"Challenge {index} lost its original squad roster")
        active = [ship for ship in scene["ships"] if ship["side"] == 2]
        if not active or not all(ship.get("user_controlled") for ship in active):
            raise ValueError(f"Challenge {index} has no player-owned physical fleet")
        for ship in active:
            if not ship.get("persistent_fleet"):
                continue
            identity = ship.get("fleet_ship_id")
            kind = ship.get("ship_type")
            if type(identity) is not int or type(kind) is not int or identity <= 0:
                raise ValueError(f"Challenge {index} has an invalid player career FleetShip")
            if identity in persistent and persistent[identity] != kind:
                raise ValueError(
                    f"Challenge {index} changed persistent FleetShip {identity} from class "
                    f"{persistent[identity]} to {kind}")
            persistent[identity] = kind
        ident = f"challenge-{index:02d}"
        mission = {
            "id": ident,
            "player_side": 2,
            "record_fleet_stats": True,
            "persist_fleet_losses": True,
            "score_on_victory_only": False,
            "requires": [] if index == 0 else [f"challenge-{index - 1:02d}"],
        }
        if index == LEVEL_COUNT - 1:
            # Unity completes the Challenge by exposing the Queen after
            # the final successful Boss Fight, not after an attempted loss.
            mission["codex_reveals"] = [{"ship_type": 17}]
        battles.append({
            "title": f"{index + 1}. {source['source_name']}",
            "config": f"level-{index:02d}/challenge.run.json",
            "mission": mission,
        })
        original.append({
            "level": index,
            "name": source["source_name"],
            "source_map_index": source["source_map_index"],
            "squads": len(squads),
            "enemy_fleet_ships": sum(len(s["members"]) for s in squads),
            "original_supply_tsv": run["levels"][0]["supply_capacity"]["max_tsv"],
        })
    if not persistent:
        raise ValueError("Challenge career needs at least one original player FleetShip")
    catalog = {
        "campaign_id": "original-human-challenge",
        "battles": battles,
        # From Unity Configuration.MaxSquadSize. The fleet editor must
        # reject over-capacity squads before publishing a durable edit.
        **({"max_squad_ships": max_squad_ships} if max_squad_ships else {}),
    }
    # The library resolves every unlock prerequisite against a campaign
    # actually present in that same library. Do not create a Challenge that
    # appears unlocked but can never be entered.
    games = []
    if base_library is not None:
        loaded = load(base_library, root)
        if loaded.get("library_schema") != 1 or not isinstance(loaded.get("games"), list):
            raise ValueError("base native library needs schema 1 and a game list")
        games = loaded["games"].copy()
    if any(game.get("id") == "original-human-challenge" for game in games):
        raise ValueError("the base native library already contains this Challenge")
    if gate is not None:
        matched = [entry for entry in games if
                   entry.get("kind") == "campaign" and entry.get("id") == gate[0]]
        if len(matched) != 1:
            raise ValueError("Challenge unlock campaign must exist in the same supplied library")
        config = matched[0].get("config")
        if (not isinstance(config, str) or not config or
                Path(config).is_absolute() or ".." in Path(config).parts):
            raise ValueError("Challenge unlock catalog path is invalid")
        main_campaign = load(root / config, root)
        if main_campaign.get("campaign_id") != gate[0] or not any(
                battle.get("mission", {}).get("id") == gate[1]
                for battle in main_campaign.get("battles", [])):
            raise ValueError("Challenge unlock milestone does not exist in the source campaign")
    games.append({
        "id": "original-human-challenge",
        "title": "Challenge (10 original levels)",
        "kind": "challenge", "config": "challenge.catalog.json",
        **({"unlock_after": {
            "campaign_id": gate[0], "mission_id": gate[1],
        }} if gate else {}),
    })
    library = {"library_schema": 1, "games": games}
    return catalog, library, original, persistent

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("root", type=Path,
                        help="folder containing level-00 ... level-09 authored battles")
    parser.add_argument("--unlock-after", metavar="CAMPAIGN:MISSION",
                        help="actual completed main-campaign mission that unlocks Challenge")
    parser.add_argument("--base-library", type=Path,
                        help="existing native library in this output directory (required with --unlock-after)")
    parser.add_argument("--max-squad-size", type=int,
                        help="actual Unity Configuration.MaxSquadSize (enables safe native career fleet editing)")
    args = parser.parse_args()
    if args.max_squad_size is not None and not 1 <= args.max_squad_size <= 1024:
        parser.error("--max-squad-size must be the positive source Configuration.MaxSquadSize, at most 1024")
    gate = None
    if args.unlock_after is not None:
        split = args.unlock_after.split(":", 1)
        if len(split) != 2 or not all(split):
            parser.error("--unlock-after requires CAMPAIGN:MISSION")
        gate = split
    root = args.root.resolve()
    if args.unlock_after and args.base_library is None:
        parser.error("--unlock-after requires --base-library with the referenced campaign")
    if args.base_library is not None and not args.base_library.resolve().is_relative_to(root):
        parser.error("--base-library must be an existing library inside the Challenge root")
    if not root.is_dir():
        parser.error("Challenge career folder does not exist")
    try:
        catalog, library, original, fleet = assemble(
            root, gate, args.base_library, args.max_squad_size,
        )
        for name, document in (
            ("challenge.catalog.json", catalog),
            ("library.json", library),
            ("challenge-provenance.json", {
                "schema_version": 1, "source": "Scripts/Data/challenge_levels.json",
                "levels": original,
                "physical_player_fleet_classes": [
                    {"fleet_ship_id": ident, "ship_type": kind}
                    for ident, kind in sorted(fleet.items())
                ],
            }),
        ):
            target = root / name
            if target.exists() or target.is_symlink():
                raise ValueError(f"refusing to replace an existing Challenge career: {target}")
        for name, document in (
            ("challenge.catalog.json", catalog),
            ("library.json", library),
            ("challenge-provenance.json", {
                "schema_version": 1, "source": "Scripts/Data/challenge_levels.json",
                "levels": original,
                "physical_player_fleet_classes": [
                    {"fleet_ship_id": ident, "ship_type": kind}
                    for ident, kind in sorted(fleet.items())
                ],
            }),
        ):
            (root / name).write_text(json.dumps(
                document, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    except (OSError, ValueError, KeyError, TypeError) as exc:
        parser.exit(2, f"native Challenge career assembly failed: {exc}\n")
    print(json.dumps({
        "kind": "native_original_challenge_career_authored",
        "stages": len(original),
        "player_fleet_classes": len(fleet),
        "max_squad_ships": args.max_squad_size,
        "catalog": str(root / "challenge.catalog.json"),
        "library": str(root / "library.json"),
    }, sort_keys=True))

if __name__ == "__main__":
    main()
