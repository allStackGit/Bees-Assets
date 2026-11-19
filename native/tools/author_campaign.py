#!/usr/bin/env python3
"""Package independently authored native battles as one persistent campaign.

This is only a source-catalog publisher. Native BattleCatalog and CombatWorld
remain the sole owners of mission unlocks, saved fleets, minerals, purchases,
and simulation. A cross-mission hull identity mismatch fails before publish.
"""

import argparse
import copy
import hashlib
import json
import os
from pathlib import Path
import re
import tempfile

from native_training_artifacts import atomic_json, file_sha256, sync_directory
from challenge_policy import existing_path, selected_run


def read(path: Path) -> dict:
    value = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(value, dict):
        raise ValueError(f"expected JSON object: {path}")
    return value


def trained_opponent(run_path: Path, run: dict, scene_path: Path,
                     player_side: int) -> dict:
    """Allow only a checkpoint-pinned challenge from the native publisher.

    A raw policy training scene is not itself a safe campaign mission:
    it may still control the human side, have stale group participants or
    point at an unevaluated model. The challenge receipt proves that those
    ownership changes came from the verified native selection boundary.
    """
    provenance_path = run_path.parent / "challenge-receipt.json"
    source = read(provenance_path)
    if (source.get("kind") != "native_policy_human_challenge" or
            source.get("run_sha256") != file_sha256(run_path) or
            source.get("scene_sha256") != file_sha256(scene_path) or
            source.get("player_side") != player_side or
            source.get("opponent_side") != 3 - player_side):
        raise ValueError("policy campaign mission needs a matching verified challenge receipt")
    selection_path = Path(source["selection_receipt"]).resolve(strict=True)
    if file_sha256(selection_path) != source.get("selection_receipt_sha256"):
        raise ValueError("native policy selection receipt changed after challenge publication")
    receipt, selected, learner_slot, original_run_path = selected_run(selection_path)
    challenge_manifest = existing_path(run_path, run.get("model_manifest"))
    selected_manifest = existing_path(
        original_run_path, selected.get("model_manifest")
    )
    checkpoint_sha = (
        receipt["selected"]["checkpoint_sha256"]
        if "selected" in receipt else receipt["checkpoint_sha256"]
    )
    if (source.get("selected_checkpoint_sha256") != checkpoint_sha or
            source.get("learner_slot") != learner_slot or
            challenge_manifest != selected_manifest):
        raise ValueError("campaign opponent model differs from the verified training selection")
    return {
        "challenge_receipt": str(provenance_path),
        "challenge_receipt_sha256": file_sha256(provenance_path),
        "selected_checkpoint_sha256": checkpoint_sha,
        "selected_model_manifest_sha256": (
            receipt["selected"]["manifest_sha256"]
            if "selected" in receipt else receipt["manifest_sha256"]
        ),
    }


def input_mission(run_path: Path, player_side: int) -> tuple[dict, dict, dict]:
    run = read(run_path)
    levels = run.get("levels")
    if (not isinstance(levels, list) or len(levels) != 1 or
            not isinstance(levels[0], dict) or
            run.get("episodes_per_world") != 1 or
            not isinstance(run.get("native_stage"), dict) or
            run.get("hivemind") is not None or
            run.get("model_updates") or
            run.get("recover_notifications") or
            run.get("recover_outcome_journals")):
        raise ValueError(
            f"campaign mission needs a single offline authored native Level: {run_path}"
        )
    scene_name = levels[0].get("scene")
    if not isinstance(scene_name, str) or not scene_name:
        raise ValueError("native campaign mission has no scene path")
    scene_path = (run_path.parent / scene_name).resolve(strict=True)
    scene = read(scene_path)
    if (scene.get("native_game_schema") != 1 or
            scene.get("stage_name") != run.get("stage_name") or
            scene.get("bounds") != run.get("fragment_spawn", {}).get("map_bounds") or
            not isinstance(scene.get("ships"), list) or
            not isinstance(scene.get("squads"), list)):
        raise ValueError("campaign source must contain the matching complete Unity-free game scene")
    if player_side not in (scene.get("bee_side"), scene.get("human_side")):
        raise ValueError("campaign player side is not a faction in its authored mission")
    has_model = run.get("model_manifest") is not None
    if has_model != (scene.get("policy_episode") is not None):
        raise ValueError("a native campaign policy scene requires its authentic model manifest")
    model_provenance = (trained_opponent(run_path, run, scene_path, player_side)
                        if has_model else {})
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
        **model_provenance,
    }


# Physical class identity is independent of the transient battle entity,
# starting coordinates, player control and authored SavedSquad preferences.
# These are the fields cloned by the ONE native FleetShip installer; the
# source class must not silently become a different weapon/ability on the
# next mission. The special weapon's contact ID is runtime-allocated.
FLEET_CLASS_FIELDS = (
    "ship_type", "max_health", "max_tsv", "original_health",
    "rotation_speed_degrees_per_second", "base_speed", "height",
    "turning_radius", "size_class", "path_clearance", "sight",
    "max_weapon_range", "firepower", "ship_type_letter",
    "target_max_range", "front_offset", "combat_window_seconds",
    "is_bomber", "is_carrier_ship", "can_override_bounds",
    "turrets", "special", "special_weapon", "support_zone",
)


def native_fleet_class_sha256(ship: dict) -> str:
    """Immutable authored physical source, not a source-scene entity ID."""
    source = {key: copy.deepcopy(ship.get(key)) for key in FLEET_CLASS_FIELDS}
    if isinstance(source["special_weapon"], dict):
        source["special_weapon"].pop("weapon_id", None)
    payload = json.dumps(source, sort_keys=True, separators=(",", ":"),
                         allow_nan=False).encode("utf-8")
    return hashlib.sha256(payload).hexdigest()


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
    per_mission_sources = []
    per_mission_class_contracts = []
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
        class_prices = {}
        class_contracts = {}
        # Live original player hulls and inert authored reserve templates
        # are both real physical source archetypes. Reward-only classes
        # need not be deployed before the mission that unlocks them.
        for ship in [*scene["ships"], *scene.get("fleet_templates", [])]:
            if (ship["side"] == args.player_side and
                    ship["health"] == ship["max_health"] and
                    ship["max_tsv"] > 0 and
                    (ship.get("persistent_fleet") or
                     ship in scene.get("fleet_templates", []))):
                ship_type = ship["ship_type"]
                class_prices.setdefault(ship_type, set()).add(ship["max_tsv"])
                class_contracts.setdefault(ship_type, set()).add(
                    native_fleet_class_sha256(ship)
                )
        for ship_type, prices in class_prices.items():
            shop_catalog.setdefault(ship_type, set()).update(prices)
        per_mission_sources.append(class_prices)
        per_mission_class_contracts.append(class_contracts)
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
        grants = [grant for grant in args.victory_grant if grant[0] == index]
        if grants:
            allowed = {squad["saved_squad_id"] for squad in scene["squads"]
                       if squad["side"] == args.player_side}
            classes = set(args.shop_types) | set(args.grant_only_types)
            if any(kind not in classes or saved_id not in allowed
                   for _, kind, _, saved_id in grants):
                raise ValueError(
                    f"mission {index} rewards require a source-priced fleet class and "
                    "an authored player SavedSquad"
                )
            if sum(count for _, _, count, _ in grants) > 1024:
                raise ValueError(f"mission {index} exceeds 1024 awarded ships")
            mission["victory_grants"] = [
                {"ship_type": kind, "count": count, "saved_squad_id": saved_id}
                for _, kind, count, saved_id in grants
            ]
        if previous is not None:
            mission["requires"] = [previous]
        missions.append({
            "title": scene["level_name"], "config": run_file,
            "mission": mission,
        })
        outputs.extend(((scene_file, scene), (run_file, run)))
        sources.append({"mission_id": ident, **provenance})
        previous = ident
    fleet_classes = []
    classes = [*args.shop_types, *args.grant_only_types]
    for ship_type in classes:
        prices = shop_catalog.get(ship_type)
        specs = [items.get(ship_type) for items in per_mission_class_contracts]
        if (prices is None or len(prices) != 1 or any(
                item.get(ship_type) != prices for item in per_mission_sources) or
                any(not spec or len(spec) != 1 for spec in specs) or
                any(spec != specs[0] for spec in specs[1:])):
            raise ValueError(
                f"native FleetShip class {ship_type} must have identical "
                "source-authored health, weapons, movement, vision and "
                "special ability across every campaign mission; use "
                "--fleet-template where the class is not deployed"
            )
        purchasable = ship_type in args.shop_types
        fleet_classes.append({
            "player_side": args.player_side, "ship_type": ship_type,
            "price_tsv": next(iter(prices)),
            "purchasable": purchasable,
            "unlocked_after": ["mission-001"] if purchasable else [],
        })
    catalog = {
        "campaign_id": slug, "battles": missions,
        "buildable_ships": fleet_classes,
    }
    if fleet_classes:
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
        "grant_only_types": args.grant_only_types,
        "fleet_class_sha256": {
            str(ship_type): next(iter(per_mission_class_contracts[0][ship_type]))
            for ship_type in classes
        },
        "victory_grants": [
            {"mission_index": index, "ship_type": kind,
             "count": count, "saved_squad_id": saved_id}
            for index, kind, count, saved_id in args.victory_grant
        ],
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


def victory_grant(value: str) -> tuple[int, int, int, int]:
    """MISSION_INDEX:SHIP_TYPE:COUNT:SAVED_SQUAD_ID.

    All rewarded hulls are real native FleetShips with their original
    source-priced prototype, placed into an existing persistent squad.
    """
    if not re.fullmatch(r"\d+:\d+:\d+:\d+", value):
        raise argparse.ArgumentTypeError(
            "--victory-grant needs MISSION_INDEX:SHIP_TYPE:COUNT:SAVED_SQUAD_ID"
        )
    mission, kind, count, saved_id = map(int, value.split(":"))
    if (not 1 <= mission <= 40 or not 0 <= kind < 24 or
            not 1 <= count <= 1024 or not 1 <= saved_id < 2**63):
        raise argparse.ArgumentTypeError(
            "victory grant requires mission 1..40, ship type 0..23, "
            "count 1..1024 and a positive SavedSquad ID"
        )
    return mission, kind, count, saved_id


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("output_dir", type=Path,
                        help="new directory for a self-contained native campaign catalog")
    parser.add_argument("--mission", type=Path, action="append", required=True,
                        help="authored game-only battle.run.json; repeat in mission order")
    parser.add_argument("--player-side", type=int, choices=(1, 2), default=1)
    parser.add_argument("--title", default="Native Fleet Campaign")
    parser.add_argument("--shop-types", type=ship_types, default=[],
                        help="optional purchasable hull classes: comma-separated ship types")
    parser.add_argument("--grant-only-types", type=ship_types, default=[],
                        help="source-backed reward classes that can never be purchased")
    parser.add_argument("--max-squad-ships", type=int, default=10,
                        help="saved-squad ship cap when a fleet shop or reward is enabled (default: 10)")
    parser.add_argument("--victory-grant", action="append", type=victory_grant,
                        default=[], metavar="MISSION:TYPE:COUNT:SQUAD",
                        help="one-time victory FleetShip rewards, using a --shop-types or --grant-only-types class")
    args = parser.parse_args()
    if any(mission > len(args.mission) for mission, _, _, _ in args.victory_grant):
        parser.error("--victory-grant refers to a mission not included by --mission")
    if set(args.shop_types) & set(args.grant_only_types):
        parser.error("a fleet class cannot be both purchasable and grant-only")
    if set(args.grant_only_types) - {
            kind for _, kind, _, _ in args.victory_grant}:
        parser.error("--grant-only-types must be awarded by at least one --victory-grant")
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
        "grant_only_types": receipt["grant_only_types"],
        "victory_grants": receipt["victory_grants"],
        "player_side": args.player_side,
    }, sort_keys=True))


if __name__ == "__main__":
    main()
