#!/usr/bin/env python3
"""Package independently authored native battles as one persistent campaign.

This is only a source-catalog publisher. Native BattleCatalog and CombatWorld
remain the sole owners of mission unlocks, saved fleets, minerals, purchases,
and simulation. A cross-mission hull identity mismatch fails before publish.
"""

import argparse
import copy
import json
import os
from pathlib import Path
import re
import tempfile

from native_training_artifacts import atomic_json, file_sha256, sync_directory


def read(path: Path) -> dict:
    value = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(value, dict):
        raise ValueError(f"expected JSON object: {path}")
    return value


def input_mission(run_path: Path, player_side: int) -> tuple[dict, dict, dict]:
    run = read(run_path)
    levels = run.get("levels")
    if (not isinstance(levels, list) or len(levels) != 1 or
            not isinstance(levels[0], dict) or
            run.get("episodes_per_world") != 1 or
            not isinstance(run.get("native_stage"), dict) or
            run.get("model_manifest") is not None or
            run.get("hivemind") is not None or
            run.get("model_updates") or
            run.get("recover_notifications") or
            run.get("recover_outcome_journals")):
        raise ValueError(
            f"campaign mission must be a one-episode, game-only native Stage: {run_path}"
        )
    scene_name = levels[0].get("scene")
    if not isinstance(scene_name, str) or not scene_name:
        raise ValueError("native campaign mission has no scene path")
    scene_path = (run_path.parent / scene_name).resolve(strict=True)
    scene = read(scene_path)
    if (scene.get("native_game_schema") != 1 or
            scene.get("stage_name") != run.get("stage_name") or
            scene.get("bounds") != run.get("fragment_spawn", {}).get("map_bounds") or
            scene.get("policy_episode") is not None or
            not isinstance(scene.get("ships"), list) or
            not isinstance(scene.get("squads"), list)):
        raise ValueError("campaign source must contain the matching complete Unity-free game scene")
    if player_side not in (scene.get("bee_side"), scene.get("human_side")):
        raise ValueError("campaign player side is not a faction in its authored mission")
    ships = {ship["entity_id"]: ship for ship in scene["ships"]
             if ship["side"] == player_side}
    if not ships or not any(ship.get("persistent_fleet") for ship in ships.values()):
        raise ValueError("campaign needs an authored persistent player fleet")
    squads = [squad for squad in scene["squads"]
              if squad["side"] == player_side]
    if not squads or any(not squad["user_controlled"] or
            not squad["can_accept_input"] for squad in squads):
        raise ValueError("campaign player squads must own explicit input authority")
    if any(ship.get("policy") is not None or not ship.get("user_controlled")
           for ship in ships.values()):
        raise ValueError("campaign player may not be controlled by a learning policy")
    if any(ship.get("persistent_fleet") and (
            not isinstance(ship.get("fleet_ship_id"), int) or
            ship["fleet_ship_id"] <= 0)
           for ship in ships.values()):
        raise ValueError("persistent fleet ships require real FleetShip identity")
    if any(not set(squad["members"]).issubset(ships) or
            not squad["members"] for squad in squads):
        raise ValueError("player squads reference unknown physical ships")
    # Source's SavedSquad ID must persist across every mission, unlike the
    # Level's transient ItemId. For authored standalone battles with none,
    # assign that authored squad's stable ItemId once in the packaged scene.
    for squad in squads:
        saved = squad.setdefault("saved_squad_id", squad["item_id"])
        if type(saved) is not int or saved <= 0:
            raise ValueError("campaign SavedSquad identities must be positive")
    if len({squad["saved_squad_id"] for squad in squads}) != len(squads):
        raise ValueError("duplicate player SavedSquad identities")
    canonical = {
        "ships": sorted((
            ship["entity_id"], ship["ship_type"],
            ship.get("fleet_ship_id"), ship.get("persistent_fleet", False),
            ship["max_tsv"], ship["max_health"],
        ) for ship in ships.values()),
        "squads": sorted((
            squad["saved_squad_id"],
            tuple(squad["members"]),
        ) for squad in squads),
    }
    return scene, run, {
        "source_run": str(run_path),
        "source_run_sha256": file_sha256(run_path),
        "source_scene": str(scene_path),
        "source_scene_sha256": file_sha256(scene_path),
        "player_fleet_contract": canonical,
    }


def campaign(args) -> tuple[dict, dict, list[tuple[str, dict]], dict]:
    title = args.title.strip()
    slug = re.sub(r"[^a-z0-9]+", "-", title.lower()).strip("-")
    if not 1 <= len(title) <= 80 or not slug:
        raise ValueError("campaign title must have 1..80 characters and an alphanumeric ID")
    if len(args.mission) < 2 or len(args.mission) > 40:
        raise ValueError("campaign needs between 2 and 40 authored native missions")
    if len(set(args.mission)) != len(args.mission):
        raise ValueError("campaign must not replay the same source run as different missions")
    outputs = []
    sources = []
    missions = []
    previous = None
    stable_roster = None
    shop_catalog = {}
    for index, source in enumerate(args.mission, 1):
        source_path = source.resolve(strict=True)
        scene, run, provenance = input_mission(source_path, args.player_side)
        if stable_roster is None:
            stable_roster = provenance["player_fleet_contract"]
        elif provenance["player_fleet_contract"] != stable_roster:
            raise ValueError(
                f"mission {index} changes authored player FleetShip or SavedSquad identities; "
                "use the same player fleet and squad membership in every native mission"
            )
        for ship in scene["ships"]:
            if (ship["side"] == args.player_side and
                    ship.get("persistent_fleet") and
                    ship["health"] == ship["max_health"] and
                    ship["max_tsv"] > 0):
                shop_catalog.setdefault(ship["ship_type"], set()).add(ship["max_tsv"])
        ident = f"mission-{index:03d}"
        scene_file = f"{ident}.scene.json"
        run_file = f"{ident}.run.json"
        run = copy.deepcopy(run)
        run["levels"][0]["scene"] = scene_file
        run["run_id"] = f"{slug}-{ident}"
        run["connection_checkpoint"] = f"{ident}-connection.json"
        run["notifications"] = f"{ident}-notifications.jsonl"
        run["rollout_summary"] = f"{ident}-summary.json"
        run["policy_capture"] = None
        mission = {
            "id": ident, "player_side": args.player_side,
            "record_fleet_stats": True, "persist_fleet_losses": True,
            "bank_player_minerals": True,
        }
        if previous is not None:
            mission["requires"] = [previous]
        missions.append({
            "title": scene["level_name"], "config": run_file,
            "mission": mission,
        })
        outputs.extend(((scene_file, scene), (run_file, run)))
        sources.append({"mission_id": ident, **provenance})
        previous = ident
    buyable = []
    if args.shop_types:
        for ship_type in args.shop_types:
            prices = shop_catalog.get(ship_type)
            if prices is None or len(prices) != 1:
                raise ValueError(
                    f"shop hull type {ship_type} must have one source-authored "
                    "full-health TSV price in the player fleet across missions"
                )
            # Each mission has the same player fleet identity and type; the
            # source Rust preflight validates actual prototype construction.
            buyable.append({
                "player_side": args.player_side, "ship_type": ship_type,
                "price_tsv": next(iter(prices)), "unlocked_after": ["mission-001"],
            })
    catalog = {
        "campaign_id": slug, "battles": missions,
        "buildable_ships": buyable,
    }
    if buyable:
        catalog["max_squad_ships"] = args.max_squad_ships
    library = {
        "library_schema": 1,
        "games": [{
            "id": slug[:40], "title": title,
            "kind": "campaign", "config": "campaign.json",
        }],
    }
    receipt = {
        "schema_version": 1, "kind": "native_authored_campaign",
        "campaign_id": slug, "player_side": args.player_side,
        "missions": sources,
        "shop_types": args.shop_types,
        "mission_count": len(missions),
    }
    return catalog, library, outputs, receipt


def ship_types(value: str) -> list[int]:
    result = []
    for item in value.split(","):
        if not re.fullmatch(r"\s*\d+\s*", item):
            raise argparse.ArgumentTypeError("--shop-types needs comma-separated ship type numbers")
        kind = int(item)
        if kind >= 24 or kind in result:
            raise argparse.ArgumentTypeError("shop ship types must be unique numbers in 0..23")
        result.append(kind)
    return result


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("output_dir", type=Path,
                        help="new directory for a self-contained native campaign catalog")
    parser.add_argument("--mission", type=Path, action="append", required=True,
                        help="authored game-only battle.run.json; repeat in mission order")
    parser.add_argument("--player-side", type=int, choices=(1, 2), default=1)
    parser.add_argument("--title", default="Native Fleet Campaign")
    parser.add_argument("--shop-types", type=ship_types, default=[],
                        help="optional source-backed hull shop: comma-separated type numbers")
    parser.add_argument("--max-squad-ships", type=int, default=10,
                        help="saved-squad ship cap when a shop is enabled (default: 10)")
    args = parser.parse_args()
    if not 1 <= args.max_squad_ships <= 1024:
        parser.error("--max-squad-ships must be within 1..1024")
    try:
        catalog, library, outputs, receipt = campaign(args)
        output = args.output_dir.resolve()
        if output.exists():
            raise FileExistsError(f"native campaign destination already exists: {output}")
        if not output.parent.is_dir():
            raise ValueError("native campaign parent directory must already exist")
        with tempfile.TemporaryDirectory(
            prefix=f".{output.name}.campaign-", dir=output.parent,
        ) as temporary:
            staged = Path(temporary) / "published"
            staged.mkdir()
            for name, payload in [*outputs,
                                  ("campaign.json", catalog),
                                  ("library.json", library)]:
                atomic_json(staged / name, payload)
            receipt["catalog_sha256"] = file_sha256(staged / "campaign.json")
            receipt["library_sha256"] = file_sha256(staged / "library.json")
            atomic_json(staged / "campaign-receipt.json", receipt)
            if output.exists():
                raise FileExistsError("campaign destination appeared before publication")
            os.rename(staged, output)
            sync_directory(output.parent)
    except (OSError, ValueError, KeyError, TypeError) as error:
        parser.exit(2, f"native campaign authoring failed: {error}\n")
    print(json.dumps({
        "kind": "native_authored_campaign_ready",
        "campaign": str(output / "campaign.json"),
        "library": str(output / "library.json"),
        "mission_count": receipt["mission_count"],
        "shop_types": receipt["shop_types"],
        "player_side": args.player_side,
    }, sort_keys=True))


if __name__ == "__main__":
    main()
