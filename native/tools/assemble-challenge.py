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

def load(path, root):
    if path.is_symlink() or not path.is_file() or not path.resolve().is_relative_to(root):
        raise ValueError(f"untrusted or missing native Challenge source: {path}")
    return json.loads(path.read_text(encoding="utf-8"))

def assemble(root, gate):
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
                not isinstance(source.get("source_name"), str) or
                not source["source_name"].strip() or
                scene.get("native_game_schema") != 1 or
                scene.get("bee_side") != 1 or scene.get("human_side") != 2 or
                scene.get("policy_episode") is not None or
                scene.get("activate_hivemind") is True or
                run.get("host_recovery_checkpoint") != "native-host-checkpoint.json" or
                len(run.get("levels", [])) != 1 or
                run["levels"][0].get("scene") != "challenge.scene.json" or
                run["levels"][0].get("supply_capacity") != {
                    "player_side": 2, "max_tsv":
                        next((v for v in [25, 175, 150, 500, 500,
                                           750, 750, 1500, 1500, 2500]
                              if v == run["levels"][0].get("supply_capacity", {}).get("max_tsv")), None)
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
    }
    library = {"library_schema": 1, "games": [{
        "id": "original-human-challenge",
        "title": "Challenge (10 original levels)",
        "kind": "challenge", "config": "challenge.catalog.json",
        **({"unlock_after": {
            "campaign_id": gate[0], "mission_id": gate[1],
        }} if gate else {}),
    }]}
    return catalog, library, original, persistent

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("root", type=Path,
                        help="folder containing level-00 ... level-09 authored battles")
    parser.add_argument("--unlock-after", metavar="CAMPAIGN:MISSION",
                        help="actual completed main-campaign mission that unlocks Challenge")
    args = parser.parse_args()
    gate = None
    if args.unlock_after is not None:
        split = args.unlock_after.split(":", 1)
        if len(split) != 2 or not all(split):
            parser.error("--unlock-after requires CAMPAIGN:MISSION")
        gate = split
    root = args.root.resolve()
    if not root.is_dir():
        parser.error("Challenge career folder does not exist")
    try:
        catalog, library, original, fleet = assemble(root, gate)
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
        "catalog": str(root / "challenge.catalog.json"),
        "library": str(root / "library.json"),
    }, sort_keys=True))

if __name__ == "__main__":
    main()
