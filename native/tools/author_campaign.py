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
from portable_policy_bundle import (
    package_model_manifest, package_stage_assets, write_pinned_policy_assets,
)
from challenge_policy import existing_path, selected_run
from prepare_fleet_training import materialize as materialize_native_fleet_layout
from carrier_family import (
    carrier_parent_id, source_carrier_groups, squad_members, has_policy,
    stationary_fleet_source,
)


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
    selected_manifest_sha = (
        receipt["selected"]["manifest_sha256"]
        if "selected" in receipt else receipt["manifest_sha256"]
    )
    packaged = source.get("packaged_model")
    if packaged is None:
        # Original nonportable challenges bind the exact source manifest.
        manifest_verified = challenge_manifest == selected_manifest
    else:
        # Standalone portable challenges replace the original manifest's
        # machine-local checkpoint paths. The challenge receipt pins BOTH
        # the selected original and the fully rebased playable manifest,
        # while individual checkpoint bytes are verified during repackaging.
        manifest_verified = (
            isinstance(packaged, dict) and
            packaged.get("original_manifest_sha256") == selected_manifest_sha and
            packaged.get("packaged_manifest") == run.get("model_manifest") and
            packaged.get("packaged_manifest_sha256") ==
                file_sha256(challenge_manifest) and
            isinstance(packaged.get("model_slots"), list) and
            any(isinstance(slot, dict) and
                slot.get("slot") == learner_slot and
                slot.get("sha256") == checkpoint_sha
                for slot in packaged["model_slots"])
        )
    if (source.get("selected_checkpoint_sha256") != checkpoint_sha or
            source.get("learner_slot") != learner_slot or
            not manifest_verified):
        raise ValueError("campaign opponent model differs from the verified training selection")
    return {
        "challenge_receipt": str(provenance_path),
        "challenge_receipt_sha256": file_sha256(provenance_path),
        "selected_checkpoint_sha256": checkpoint_sha,
        "selected_learner_slot": learner_slot,
        "selected_model_manifest_sha256": selected_manifest_sha,
        "source_model_manifest_sha256": file_sha256(challenge_manifest),
    }



def input_mission(run_path: Path, player_side: int) -> tuple[dict, dict, dict]:
    run = read(run_path)
    levels = run.get("levels")
    inline_stage = (
        isinstance(run.get("native_stage"), dict) and
        run.get("collision_matrix") is None and
        run.get("stage_palette") is None
    )
    legacy_stage = (
        run.get("native_stage") is None and
        all(isinstance(run.get(key), str) and run[key]
            for key in ("collision_matrix", "stage_palette"))
    )
    if (not isinstance(levels, list) or len(levels) != 1 or
            not isinstance(levels[0], dict) or
            run.get("episodes_per_world") != 1 or
            not (inline_stage or legacy_stage) or
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
    if (scene.get("native_game_schema") == 1 and
            isinstance(run.get("run_id"), str) and
            run["run_id"].startswith("native-fleet-") and
            run["levels"][0].get("native_layout")):
        # Native fleet skirmishes are archived compactly, with genuine
        # source-ship clones and (now) multi-ship squad composition in
        # their immutable Level layout. Reuse the EXACT same materializer
        # as the playable-game-to-PPO publisher: a campaign must save and
        # replay the full physical fleet, not only its two template hulls.
        # No separate ship stats, combat simulation or roster interpreter.
        scene, run, _ = materialize_native_fleet_layout(scene, run)
    # Original native live-HiveMind missions are playable campaign sources,
    # provided the player side remains authoritative and the opponent is
    # owned solely by the actual BeesServer command connection. This
    # permits a complete campaign or challenge series against live HiveMind
    # without introducing a scripted stand-in or duplicate simulation.
    hivemind = run.get("hivemind")
    if hivemind is not None:
        if (scene.get("native_game_schema") != 1 or
                not isinstance(hivemind, dict) or
                hivemind.get("issue_source_commands") is not True or
                hivemind.get("reconnect_game_id") is not None or
                scene.get("activate_hivemind") is not True or
                not isinstance(scene.get("squads"), list) or
                not isinstance(scene.get("ships"), list) or
                any(isinstance(ship.get("policy"), dict)
                    for ship in scene["ships"])):
            raise ValueError(
                "campaign HiveMind mission requires a fresh real native source-command Game"
            )
        player_owned, remote_owned = 0, 0
        opponent_ids = set()
        for squad in scene["squads"]:
            source = squad.get("hivemind")
            if (not isinstance(source, dict) or
                    type(source.get("opponent_id")) is not int or
                    source["opponent_id"] <= 0 or
                    type(source.get("hivemind_controlled")) is not bool or
                    type(source.get("immobile")) is not bool):
                raise ValueError("HiveMind campaign squad lacks explicit original source authority")
            opponent_ids.add(source["opponent_id"])
            if squad.get("side") == player_side:
                if (source["hivemind_controlled"] or
                        not squad.get("user_controlled") or
                        not squad.get("can_accept_input")):
                    raise ValueError("HiveMind mission must preserve campaign player command ownership")
                player_owned += 1
            elif squad.get("side") == 3 - player_side:
                if (not source["hivemind_controlled"] or
                        squad.get("user_controlled") or
                        squad.get("can_accept_input")):
                    raise ValueError("HiveMind mission opponent requires exclusive server ownership")
                remote_owned += 1
            else:
                raise ValueError("HiveMind mission has an unknown player faction")
        if player_owned == 0 or remote_owned == 0 or len(opponent_ids) != 1:
            raise ValueError("HiveMind campaign requires one verified source opponent and player")
    # Migration snapshots are the original game's observed initial state.
    # Keep their original field schema for the native importer; never
    # synthesize the missing rules from a second game model.
    captured = scene.get("native_game_schema") is None
    if captured:
        if scene.get("schema_version") != 35:
            raise ValueError("campaign migration needs schema-35 source SavedSquad identities")
        bounds = {
            end: [scene[end]["x"], scene[end]["y"]] for end in ("min", "max")
        }
        targeting = scene.get("targeting_context") or {}
        if (targeting.get("has_player") is not True or
                targeting.get("user_side") != player_side or
                scene.get("has_policy_episode") is not False or
                run.get("model_manifest") is not None or
                any(has_policy(ship) for ship in scene.get("ships", []))):
            raise ValueError("campaign migration requires an ordinary player-controlled source Level")
    else:
        if scene.get("native_game_schema") != 1:
            raise ValueError("unsupported native game scene schema")
        bounds = scene.get("bounds")
    if (scene.get("stage_name") != run.get("stage_name") or
            bounds != run.get("fragment_spawn", {}).get("map_bounds") or
            not isinstance(scene.get("ships"), list) or
            not isinstance(scene.get("squads"), list)):
        raise ValueError("campaign source must contain the matching complete Unity-free game scene")
    if player_side not in (scene.get("bee_side"), scene.get("human_side")):
        raise ValueError("campaign player side is not a faction in its authored mission")
    has_model = run.get("model_manifest") is not None
    if not captured and has_model != (scene.get("policy_episode") is not None):
        raise ValueError("a native campaign policy scene requires its authentic model manifest")
    model_provenance = (trained_opponent(run_path, run, scene_path, player_side)
                        if has_model else {})
    if has_model:
        # Older authenticated challenge receipts did not yet carry the
        # explicit playable-game switch. Preserve their source hash while
        # writing the new gameplay scene into the campaign package.
        scene["policy_gameplay"] = True
        scene["local_player_side"] = player_side
    ships = {ship["entity_id"]: ship for ship in scene["ships"]
             if ship["side"] == player_side}
    if not ships or not any(ship.get("persistent_fleet") for ship in ships.values()):
        raise ValueError("campaign needs an authored persistent player fleet")
    squads = [squad for squad in scene["squads"]
              if squad["side"] == player_side]
    # Original missions can have allied objective entities (for example
    # HumanTarget) with input deliberately disabled by their source script.
    # Require player authority for the durable fleet, not temporary allies.
    if not squads:
        raise ValueError("campaign requires source player-side squads")
    if any(has_policy(ship) or
           (ship.get("persistent_fleet") and not ship.get("user_controlled"))
           for ship in ships.values()):
        raise ValueError("campaign player FleetShips must retain human control")
    if any(ship.get("persistent_fleet") and (
            not isinstance(ship.get("fleet_ship_id"), int) or
            ship["fleet_ship_id"] <= 0)
           for ship in ships.values()):
        raise ValueError("persistent fleet ships require real FleetShip identity")
    if any(not set(squad_members(squad)).issubset(ships) or
            not squad_members(squad) for squad in squads):
        raise ValueError("player squads reference unknown physical ships")
    # SavedSquad and FleetShip are career identities, not the transient
    # physical roster. Free Carrier complements and any other transient
    # mission squads still deploy in the real scene, but must never become
    # independently owned, awarded, or permanently lost account ships.
    persistent_squads = []
    assigned_fleet = set()
    for squad in squads:
        members = tuple(sorted(ships[ship_id]["fleet_ship_id"]
                        for ship_id in squad_members(squad)
                        if ships[ship_id].get("persistent_fleet")))
        if not members:
            if squad.get("saved_squad_id") not in (None, 0):
                raise ValueError("transient player squad cannot own a SavedSquad identity")
            continue
        if not squad["user_controlled"] or not squad["can_accept_input"]:
            raise ValueError("durable player squad must own explicit input authority")
        if len(set(members)) != len(members) or assigned_fleet.intersection(members):
            raise ValueError("player FleetShip appears in multiple squad memberships")
        assigned_fleet.update(members)
        # Captured ItemId is a transient Stage allocation, not a career ID.
        saved = (squad.get("saved_squad_id") if captured else
                 squad.setdefault("saved_squad_id", squad["item_id"]))
        if type(saved) is not int or saved <= 0:
            raise ValueError("campaign SavedSquad identities must be positive")
        persistent_squads.append((saved, members))
    if len({saved for saved, _ in persistent_squads}) != len(persistent_squads):
        raise ValueError("duplicate player SavedSquad identities")
    persistent_hulls = [
        ship for ship in ships.values() if ship.get("persistent_fleet")
    ]
    if len({ship["fleet_ship_id"] for ship in persistent_hulls}) != len(persistent_hulls):
        raise ValueError("duplicate durable player FleetShip identity")
    if assigned_fleet != {ship["fleet_ship_id"] for ship in persistent_hulls}:
        raise ValueError("every durable player FleetShip must own one persistent squad")
    # Separate physical entities may legitimately use different IDs in
    # different Levels. Only the durable FleetShip/SavedSquad identities
    # and their source class/value contract persist across missions.
    canonical = {
        "ships": sorted((
            ship["fleet_ship_id"], ship["ship_type"],
            ship["max_tsv"], ship["max_health"],
        ) for ship in persistent_hulls),
        "squads": sorted(persistent_squads),
    }
    return scene, run, {
        "source_run": str(run_path),
        "source_run_sha256": file_sha256(run_path),
        "source_scene": str(scene_path),
        "source_scene_sha256": file_sha256(scene_path),
        "player_fleet_contract": canonical,
        "hivemind_source_opponent": None if hivemind is None
            else next(iter(opponent_ids)),
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


def captured_fleet_class_sha256(ship: dict) -> str:
    """Pin source-owned physical class fields, not transient battle state."""
    fields = (
        "ship_type", "max_health", "max_tsv", "original_health", "size_class",
        "size_pixels", "has_healing_zone", "healing_zone", "has_warp_zone",
        "warp_zone", "rotation_speed", "base_speed", "height", "turning_radius",
        "half_width", "half_height", "path_clearance", "mobile",
        "cannot_change_orders", "longest_side", "max_weapon_range", "firepower",
        "is_bomber", "is_carrier_ship", "source_sight", "special",
        "special_weapon", "turrets", "target_geometry", "weapon_rank_source",
    )
    source = {key: copy.deepcopy(ship[key]) for key in fields}
    source["special"].pop("carrier_id", None)
    source["special"].pop("offset_from_center", None)
    if isinstance(source["special_weapon"], dict):
        source["special_weapon"].pop("weapon_id", None)
    for turret in source["turrets"]:
        turret.pop("initial_rotation_degrees", None)
    source["target_geometry"].pop("bounds_latched", None)
    for key in ("fleet_owner_ship_id", "fleet_damage_done", "last_killed"):
        source["weapon_rank_source"].pop(key, None)
    # Format-specific pinning is intentional: two different source formats
    # cannot establish identical class definitions by comparing partial keys.
    return hashlib.sha256(json.dumps(
        {"captured_physical_class": source}, sort_keys=True,
        separators=(",", ":"), allow_nan=False,
    ).encode("utf-8")).hexdigest()


def native_fleet_class_sha256(ship: dict) -> str:
    """Immutable authored physical source, not a source-scene entity ID.

    Match the native GameShip serde defaults so omitted false/empty
    authoring options are equivalent to their explicit values.
    """
    defaults = {
        "is_bomber": False, "is_carrier_ship": False,
        "can_override_bounds": False, "turrets": [],
    }
    source = {
        key: copy.deepcopy(ship.get(key, defaults.get(key)))
        for key in FLEET_CLASS_FIELDS
    }
    if isinstance(source["sight"], dict):
        source["sight"].setdefault("enabled", True)
        source["sight"].setdefault("fog_radius", None)
    if isinstance(source["special_weapon"], dict):
        source["special_weapon"].pop("weapon_id", None)
    if isinstance(source["special"], dict) and source["special"].get("kind") == "striker":
        source["special"].pop("carrier_id", None)
    payload = json.dumps(source, sort_keys=True, separators=(",", ":"),
                         allow_nan=False).encode("utf-8")
    return hashlib.sha256(payload).hexdigest()


def campaign(args) -> tuple[dict, dict, list[tuple[str, dict]], dict, dict, dict, dict]:
    title = args.title.strip()
    slug = re.sub(r"[^a-z0-9]+", "-", title.lower()).strip("-")
    if not 1 <= len(title) <= 80 or not slug:
        raise ValueError("campaign title must have 1..80 characters and an alphanumeric ID")
    if len(args.mission) < 2 or len(args.mission) > 40:
        raise ValueError("campaign needs between 2 and 40 authored native missions")
    if len(set(args.mission)) != len(args.mission):
        raise ValueError("campaign must not replay the same source run as different missions")
    outputs = []
    model_files = {}
    runtime_files = {}
    stage_files = {}
    sources = []
    missions = []
    mission_dependencies = dict(args.mission_requires)
    mission_advances = dict(args.mission_advance)
    mission_grants = dict(args.mission_grant_on)
    mission_banks = dict(args.mission_bank_on)
    mission_stories = dict(args.mission_story)
    mission_outcomes = {}
    for index, prior, result in args.mission_unlock_any:
        mission_outcomes.setdefault(index, []).append((prior, result))
    shop_unlocks = dict(args.shop_unlock)
    variants = {
        "victory": {mission: (previous, run_path)
                    for mission, previous, run_path in args.mission_variant},
        "defeat": {mission: (previous, run_path)
                   for mission, previous, run_path in args.mission_defeat_variant},
    }
    stable_roster = None
    enemy_fleet_contract = {}
    granted_squad_origin = {}
    required_ancestors = {}
    shop_catalog = {}
    per_mission_sources = []
    per_mission_class_contracts = []

    def apply_story(index: int, scene: dict, run: dict) -> None:
        story = mission_stories.get(index)
        if story is None:
            return
        level = run["levels"][0]
        if story == "neptune_pressing_forward":
            # This authored story accompanies ordinary original elimination.
            # Its Factory dialogue choice is resolved after the native
            # launcher restores actual purchased/lost persistent FleetShips.
            if (scene.get("native_game_schema") != 1 or
                    scene.get("human_side") != args.player_side or
                    any(level.get(name) is not None for name in
                        ("survival", "demolition", "escort", "extraction")) or
                    level.get("campaign_dialogue") is not None):
                raise ValueError(
                    "Neptune Pressing Forward needs an ordinary native human "
                    "elimination Level without another authored story or objective"
                )
            level["campaign_dialogue"] = {
                "neptune_pressing_forward": {
                    "player_side": args.player_side, "has_factory": False,
                }
            }
        elif story == "neptune_of_production":
            # The original mission mines real asteroids under attack and
            # permits retreat through the same physical extraction trigger
            # used by playable battles and native policy episodes.
            zone = level.get("extraction")
            if (scene.get("native_game_schema") != 1 or
                    scene.get("human_side") != args.player_side or
                    not isinstance(zone, dict) or
                    zone.get("player_side") != args.player_side or
                    not scene.get("mining_asteroids") or
                    level.get("campaign_dialogue") is not None or
                    any(level.get(name) is not None for name in
                        ("survival", "demolition", "escort"))):
                raise ValueError(
                    "Neptune Of Production requires real mineable asteroids "
                    "and an authored native fleet extraction Level"
                )
        elif story == "pluto_bluer_pastures":
            objective = level.get("survival")
            target = next((ship for ship in scene["ships"]
                           if isinstance(objective, dict) and
                           ship.get("entity_id") == objective.get("protected_ship_id")), None)
            if (scene.get("native_game_schema") != 1 or
                    scene.get("human_side") != args.player_side or
                    not isinstance(objective, dict) or
                    objective.get("defending_side") != args.player_side or
                    not isinstance(objective.get("seconds"), (int, float)) or
                    abs(objective["seconds"] - 300.49) > 0.01 or
                    target is None or target.get("ship_type") != 23 or
                    target.get("side") != args.player_side or
                    any(level.get(name) is not None for name in
                        ("demolition", "escort", "extraction"))):
                raise ValueError(
                    "Pluto evacuation requires the native 300.49-second "
                    "physical HumanTarget survival mission"
                )
            if level.get("campaign_dialogue") is not None:
                raise ValueError("Pluto evacuation already has a conflicting story owner")
            level["campaign_dialogue"] = {
                "pluto_evacuation": {
                    "player_side": args.player_side,
                    "protected_ship_id": objective["protected_ship_id"],
                }
            }

    def record_enemy_fleet_source(scene: dict) -> None:
        if not args.persist_enemy_losses:
            return
        if scene.get("policy_episode") is not None:
            raise ValueError(
                "persistent enemy FleetShips require source-owned AI or HiveMind, not policy agents"
            )
        unique = set()
        for ship in scene["ships"]:
            if ship.get("side") != 3 - args.player_side or not ship.get("persistent_fleet"):
                continue
            fleet_id = ship.get("fleet_ship_id")
            if type(fleet_id) is not int or fleet_id <= 0 or fleet_id in unique:
                raise ValueError("enemy FleetShips need unique positive persistent IDs per encounter")
            unique.add(fleet_id)
            contract = (ship["ship_type"], ship["max_tsv"], ship["max_health"])
            previous = enemy_fleet_contract.setdefault(fleet_id, contract)
            if previous != contract:
                raise ValueError(
                    f"enemy FleetShip {fleet_id} changed identity across native campaign missions"
                )

    def record_class_source(scene: dict) -> None:
        """Require each selectable game variant to supply real identical hulls."""
        class_prices = {}
        class_contracts = {}
        fingerprint = (native_fleet_class_sha256 if scene.get("native_game_schema") == 1
                       else captured_fleet_class_sha256)
        candidates = (
            (False, (ship for ship in scene["ships"] if ship.get("persistent_fleet"))),
            (True, iter(scene.get("fleet_templates", []))),
        )
        for inert, group in candidates:
            for ship in group:
                if (ship["side"] != args.player_side or
                        ship["health"] != ship["max_health"] or
                        ship["max_tsv"] <= 0 or
                        not stationary_fleet_source(ship) or
                        ship.get("mined_cargo", 0) or
                        carrier_parent_id(ship) is not None):
                    continue
                ship_type = ship["ship_type"]
                contract = fingerprint(ship)
                if ship_type == 5:
                    family = source_carrier_groups(scene, ship, inert=inert)
                    if family is None:
                        continue
                    # A Carrier shop class includes the exact source
                    # complement composition as well as the root hull.
                    # Runtime IDs and mission-specific positions are not
                    # part of the immutable purchased class.
                    # The source's transient squad/member order can change
                    # between battles without changing a Carrier's actual
                    # purchasable family. Preserve squad boundaries, but
                    # compare each same-class deck as a multiset of hulls.
                    family_contract = sorted(
                        sorted(fingerprint(child) for child in children)
                        for _, children in family
                    )
                    contract = hashlib.sha256(json.dumps([
                        contract, family_contract,
                    ], separators=(",", ":")).encode("utf-8")).hexdigest()
                class_prices.setdefault(ship_type, set()).add(ship["max_tsv"])
                class_contracts.setdefault(ship_type, set()).add(contract)
        for ship_type, prices in class_prices.items():
            shop_catalog.setdefault(ship_type, set()).update(prices)
        per_mission_sources.append(class_prices)
        per_mission_class_contracts.append(class_contracts)

    for index, source in enumerate(args.mission, 1):
        source_path = source.resolve(strict=True)
        scene, run, provenance = input_mission(source_path, args.player_side)
        apply_story(index, scene, run)
        if stable_roster is None:
            stable_roster = provenance["player_fleet_contract"]
        elif provenance["player_fleet_contract"] != stable_roster:
            raise ValueError(
                f"mission {index} changes authored player FleetShip or SavedSquad identities; "
                "use the same player fleet and squad membership in every native mission"
            )
        # Both ordinary and conditional battles have the same persistent
        # campaign player fleet and source-priced hull class contract.
        # Any variant lacking a purchasable/earned hull is rejected before
        # publication, never at the next attempted mission launch.
        record_enemy_fleet_source(scene)
        record_class_source(scene)
        ident = f"mission-{index:03d}"
        scene_file = f"{ident}.scene.json"
        run_file = f"{ident}.run.json"
        run = copy.deepcopy(run)
        provenance["stage_assets"] = package_stage_assets(
            source_path, run, stage_files,
        )
        if run.get("model_manifest") is not None:
            provenance["packaged_model"] = package_model_manifest(
                source_path, run, ident, outputs, model_files,
                runtime_files, provenance,
            )
        run["levels"][0]["scene"] = scene_file
        run["run_id"] = f"{slug}-{ident}"
        run["connection_checkpoint"] = f"{ident}-connection.json"
        # Every published campaign mission is a resumable physical World,
        # including after a crash before the first successful episode.
        # Reuse the native host's existing atomic checkpoint and original
        # replay/career owner; do not introduce another campaign save format.
        run["host_recovery_checkpoint"] = "native-host-checkpoint.json"
        run["notifications"] = f"{ident}-notifications.jsonl"
        run["rollout_summary"] = f"{ident}-summary.json"
        run["policy_capture"] = None
        mission = {
            "id": ident, "player_side": args.player_side,
            "record_fleet_stats": True, "persist_fleet_losses": True,
            "bank_player_minerals": True,
        }
        if args.persist_enemy_losses:
            mission["persist_enemy_fleet_losses"] = True
        # Mining income is a source Level.Ending action, not an automatic
        # consequence of a victory. Neither Pluto IV nor the Neptune
        # blockade invokes CollectMinedMineralsForPlayer.
        if mission_stories.get(index) in (
                "pluto_bluer_pastures", "neptune_pressing_forward"):
            mission["bank_player_minerals"] = False
        if mission_stories.get(index) == "pluto_bluer_pastures":
            mission["evacuation_rewards"] = True
            mission["advance_on"] = "finished"
            mission["grant_on"] = "finished"
        if mission_stories.get(index) == "neptune_of_production":
            mission["advance_on"] = "finished"
            mission["bank_player_minerals"] = True
            mission["bank_on_finished"] = True
        if mission_stories.get(index) == "neptune_pressing_forward":
            # Level.Campaign.Endings.Neptune3Ending permanently marks all
            # Factory FleetShips dead on a loss, including owned reserves.
            # The host freezes their IDs from the complete original fleet
            # and commits the action with the ordinary terminal result.
            mission["retire_on_defeat_types"] = [9]
        prerequisites = mission_dependencies.get(
            index, [index - 1] if index > 1 else []
        )
        if prerequisites:
            mission["requires"] = [
                f"mission-{required:03d}" for required in prerequisites
            ]
        if mission_advances.get(index, "victory") == "finished":
            mission["advance_on"] = "finished"
        if mission_grants.get(index) == "finished":
            if mission_advances.get(index, "victory") != "finished" and \
                    mission_stories.get(index) not in (
                        "pluto_bluer_pastures", "neptune_of_production"):
                raise ValueError(
                    f"mission {index} completion-based rewards require --mission-advance {index}:finished"
                )
            mission["grant_on"] = "finished"
        if mission_banks.get(index) == "finished":
            if mission_advances.get(index, "victory") != "finished" and \
                    mission_stories.get(index) != "pluto_bluer_pastures":
                raise ValueError(
                    f"mission {index} completion-based income requires --mission-advance {index}:finished"
                )
            mission["bank_on_finished"] = True
        alternatives = mission_outcomes.get(index, [])
        if alternatives:
            mission["unlock_if_any"] = [
                {"mission": f"mission-{prior:03d}", "result": result}
                for prior, result in alternatives
            ]
        ancestors = set(prerequisites)
        for prerequisite in prerequisites:
            ancestors.update(required_ancestors[prerequisite])
        required_ancestors[index] = ancestors
        # Completing a 'finished' mission can mean defeat. Its victory
        # rewards therefore cannot be assumed available to a later mission
        # simply because that mission was unlocked. In contrast, any
        # ancestor whose unlock rule is 'victory' is guaranteed won.
        guaranteed_squads = {
            saved_id for saved_id, grant_mission in granted_squad_origin.items()
            if grant_mission in ancestors and
            mission_advances.get(grant_mission, "victory") == "victory"
        }
        authored_squads = {squad["saved_squad_id"] for squad in scene["squads"]
                           if squad["side"] == args.player_side and
                           squad.get("saved_squad_id") not in (None, 0)}
        squad_grants = [grant for grant in args.victory_squad if grant[0] == index]
        if len(squad_grants) > 64:
            raise ValueError(f"mission {index} awards too many new SavedSquads")
        awarded_this_mission = set()
        for _, new_id, template_id in squad_grants:
            if (new_id in authored_squads or new_id in granted_squad_origin or
                    new_id in awarded_this_mission or
                    template_id not in
                    authored_squads | guaranteed_squads | awarded_this_mission or
                    new_id == template_id):
                raise ValueError(
                    f"mission {index} awards a reused SavedSquad ID or uses "
                    "a command template that is not guaranteed to exist on "
                    "every path reaching this mission"
                )
            awarded_this_mission.add(new_id)
        if squad_grants:
            mission["victory_squads"] = [
                {"saved_squad_id": new_id, "template_saved_squad_id": template_id}
                for _, new_id, template_id in squad_grants
            ]
        grants = [grant for grant in args.victory_grant if grant[0] == index]
        if grants:
            allowed = authored_squads | guaranteed_squads | awarded_this_mission
            classes = set(args.shop_types) | set(args.grant_only_types)
            if any(kind not in classes or saved_id not in allowed
                   for _, kind, _, saved_id in grants):
                raise ValueError(
                    f"mission {index} ship rewards require a source-priced fleet class and "
                    "a SavedSquad guaranteed by its prerequisite victories"
                )
            if sum(count for _, _, count, _ in grants) > 1024:
                raise ValueError(f"mission {index} exceeds 1024 awarded ships")
            mission["victory_grants"] = [
                {"ship_type": kind, "count": count, "saved_squad_id": saved_id}
                for _, kind, count, saved_id in grants
            ]
        if mission_stories.get(index) == "pluto_bluer_pastures":
            quotas = {18: 5, 13: 8, 12: 4, 7: 2}
            counts = {kind: sum(count for _, actual, count, _ in grants
                                if actual == kind) for kind in quotas}
            if counts != quotas or any(kind not in quotas for _, kind, _, _ in grants):
                raise ValueError(
                    "Pluto evacuation must source its maximum Scout 5, "
                    "Gunship 8, Frigate 4, Dreadnought 2 rewards"
                )
        for new_id in awarded_this_mission:
            granted_squad_origin[new_id] = index
        variant_provenance = {}
        if (index in variants["victory"] and index in variants["defeat"] and
                variants["victory"][index][0] != variants["defeat"][index][0]):
            raise ValueError(
                f"mission {index} victory and defeat variants must branch "
                "from the same previous mission"
            )
        for outcome, configured in variants.items():
            if index not in configured:
                continue
            previous, variant_path = configured[index]
            advances_on_finish = mission_advances.get(previous, "victory") == "finished"
            prerequisite_allows = previous in prerequisites and (
                outcome == "victory" or advances_on_finish
            )
            alternative_allows = any(
                prior == previous and (result == outcome or
                    result == "finished" and
                    (outcome == "victory" or advances_on_finish))
                for prior, result in alternatives
            )
            if not prerequisite_allows and not alternative_allows:
                raise ValueError(
                    f"mission {index} {outcome} variant requires a reachable "
                    f"{outcome} or finished gate on mission {previous}"
                )
            variant_scene, variant_run, provenance_variant = input_mission(
                variant_path.resolve(strict=True), args.player_side,
            )
            apply_story(index, variant_scene, variant_run)
            if provenance_variant["player_fleet_contract"] != stable_roster:
                raise ValueError(
                    f"mission {index} conditional {outcome} battle changes the "
                    "saved player FleetShip identities or squad memberships"
                )
            record_enemy_fleet_source(variant_scene)
            record_class_source(variant_scene)
            variant_scene_file = f"{ident}.{outcome}.scene.json"
            variant_run_file = f"{ident}.{outcome}.run.json"
            variant_run = copy.deepcopy(variant_run)
            provenance_variant["stage_assets"] = package_stage_assets(
                variant_path.resolve(strict=True), variant_run, stage_files,
            )
            if variant_run.get("model_manifest") is not None:
                provenance_variant["packaged_model"] = package_model_manifest(
                    variant_path.resolve(strict=True), variant_run,
                    f"{ident}.{outcome}", outputs, model_files,
                    runtime_files, provenance_variant,
                )
            variant_run["levels"][0]["scene"] = variant_scene_file
            variant_run["run_id"] = f"{slug}-{ident}-{outcome}"
            variant_run["connection_checkpoint"] = f"{ident}-{outcome}-connection.json"
            variant_run["host_recovery_checkpoint"] = "native-host-checkpoint.json"
            variant_run["notifications"] = f"{ident}-{outcome}-notifications.jsonl"
            variant_run["rollout_summary"] = f"{ident}-{outcome}-summary.json"
            variant_run["policy_capture"] = None
            outputs.extend(((variant_scene_file, variant_scene),
                            (variant_run_file, variant_run)))
            mission[f"on_{outcome}"] = {
                "mission": f"mission-{previous:03d}",
                "config": variant_run_file,
            }
            variant_provenance[f"{outcome}_variant"] = {
                "depends_on": f"mission-{previous:03d}",
                **provenance_variant,
            }
        missions.append({
            "title": scene["level_name"], "config": run_file,
            "mission": mission,
        })
        outputs.extend(((scene_file, scene), (run_file, run)))
        sources.append({"mission_id": ident, **provenance, **variant_provenance})
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
            "unlocked_after": [
                f"mission-{required:03d}" for required in
                shop_unlocks.get(ship_type, [1] if purchasable else [])
            ],
        })
    catalog = {
        "campaign_id": slug, "battles": missions,
        "buildable_ships": fleet_classes,
    }
    if fleet_classes or args.victory_squad:
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
        "mission_progression": [{
            "mission_id": f"mission-{index:03d}",
            "requires": missions[index - 1]["mission"].get("requires", []),
            "advance_on": missions[index - 1]["mission"].get("advance_on", "victory"),
            "grant_on": missions[index - 1]["mission"].get("grant_on", "victory"),
            "bank_on_finished": missions[index - 1]["mission"].get("bank_on_finished", False),
            "on_victory": missions[index - 1]["mission"].get("on_victory"),
            "on_defeat": missions[index - 1]["mission"].get("on_defeat"),
            "unlock_if_any": missions[index - 1]["mission"].get("unlock_if_any", []),
        } for index in range(1, len(missions) + 1)],
        "shop_unlocked_after": {
            str(entry["ship_type"]): entry["unlocked_after"]
            for entry in fleet_classes if entry["purchasable"]
        },
        "fleet_class_sha256": {
            str(ship_type): next(iter(per_mission_class_contracts[0][ship_type]))
            for ship_type in classes
        },
        "victory_grants": [
            {"mission_index": index, "ship_type": kind,
             "count": count, "saved_squad_id": saved_id}
            for index, kind, count, saved_id in args.victory_grant
        ],
        "victory_squads": [
            {"mission_index": index, "saved_squad_id": new_id,
             "template_saved_squad_id": template_id}
            for index, new_id, template_id in args.victory_squad
        ],
        "mission_count": len(missions),
        **({"persistent_enemy_fleet_ids": sorted(enemy_fleet_contract)}
           if args.persist_enemy_losses else {}),
    }
    receipt["packaged_model_files"] = [
        {"file": name, "sha256": name.split("/")[-1].removesuffix(".model")}
        for name in sorted(model_files)
    ]
    receipt["packaged_runtime_files"] = [
        {"file": name, "sha256": runtime_files[name][1],
         "normalized_source_sha256": runtime_files[name][2]}
        for name in sorted(runtime_files)
    ]
    receipt["packaged_stage_files"] = [
        {"file": name, "sha256": stage_files[name][1]}
        for name in sorted(stage_files)
    ]
    return catalog, library, outputs, receipt, model_files, runtime_files, stage_files


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


def victory_squad(value: str) -> tuple[int, int, int]:
    """MISSION_INDEX:NEW_SAVED_SQUAD_ID:TEMPLATE_SAVED_SQUAD_ID."""
    if not re.fullmatch(r"\d+:\d+:\d+", value):
        raise argparse.ArgumentTypeError(
            "--victory-squad needs MISSION:NEW_SQUAD_ID:TEMPLATE_SQUAD_ID"
        )
    mission, new_id, template_id = map(int, value.split(":"))
    if not 1 <= mission <= 40 or not 1 <= new_id < 2**63 or not 1 <= template_id < 2**63:
        raise argparse.ArgumentTypeError(
            "victory squad requires mission 1..40 and positive 64-bit SavedSquad IDs"
        )
    return mission, new_id, template_id


def mission_variant(value: str) -> tuple[int, int, Path]:
    """MISSION:PREVIOUS_MISSION:RUN; a conditional native game scenario."""
    pieces = value.split(":", 2)
    if len(pieces) != 3 or not pieces[0].isdigit() or not pieces[1].isdigit() or not pieces[2]:
        raise argparse.ArgumentTypeError(
            "--mission-variant needs MISSION:PREVIOUS_MISSION:RUN_FILE"
        )
    mission, previous = int(pieces[0]), int(pieces[1])
    if not 2 <= mission <= 40 or not 1 <= previous < mission:
        raise argparse.ArgumentTypeError(
            "conditional victory source needs a previous mission in 1..39"
        )
    return mission, previous, Path(pieces[2])


def preceding_missions(value: str) -> tuple[int, list[int]]:
    """MISSION:PREREQ[,PREREQ] or MISSION:none; 1-based forward-only DAG."""
    match = re.fullmatch(r"(\d+):(none|\d+(?:,\d+)*)", value)
    if match is None:
        raise argparse.ArgumentTypeError(
            "mission requires MISSION:PREVIOUS[,PREVIOUS] or MISSION:none"
        )
    index = int(match.group(1))
    prerequisites = ([] if match.group(2) == "none" else
                     [int(part) for part in match.group(2).split(",")])
    if (not 1 <= index <= 40 or
            len(prerequisites) != len(set(prerequisites)) or
            any(not 1 <= prerequisite < index for prerequisite in prerequisites)):
        raise argparse.ArgumentTypeError(
            "prerequisite missions must be unique, positive and earlier"
        )
    return index, prerequisites


def mission_advance(value: str) -> tuple[int, str]:
    """MISSION:victory or MISSION:finished."""
    match = re.fullmatch(r"(\d+):(victory|finished)", value)
    if match is None or not 1 <= int(match.group(1)) <= 40:
        raise argparse.ArgumentTypeError(
            "--mission-advance needs MISSION:victory or MISSION:finished"
        )
    return int(match.group(1)), match.group(2)


def mission_story(value: str) -> tuple[int, str]:
    match = re.fullmatch(r"(\d+):(neptune_pressing_forward|neptune_of_production|pluto_bluer_pastures)", value)
    if match is None or not 1 <= int(match.group(1)) <= 40:
        raise argparse.ArgumentTypeError(
            "--mission-story needs MISSION:neptune_pressing_forward, neptune_of_production or pluto_bluer_pastures"
        )
    return int(match.group(1)), match.group(2)


def mission_unlock_any(value: str) -> tuple[int, int, str]:
    """MISSION:PRIOR:finished|victory|defeat; alternative forward mission gate."""
    match = re.fullmatch(r"(\d+):(\d+):(finished|victory|defeat)", value)
    if match is None:
        raise argparse.ArgumentTypeError(
            "--mission-unlock-any needs MISSION:PRIOR:finished|victory|defeat"
        )
    mission, prior, result = int(match.group(1)), int(match.group(2)), match.group(3)
    if not 2 <= mission <= 40 or not 1 <= prior < mission:
        raise argparse.ArgumentTypeError("outcome alternative requires an earlier mission")
    return mission, prior, result


def shop_unlock(value: str) -> tuple[int, list[int]]:
    """SHIP_TYPE:MISSION[,MISSION] or SHIP_TYPE:none for initial availability."""
    match = re.fullmatch(r"(\d+):(none|\d+(?:,\d+)*)", value)
    if match is None:
        raise argparse.ArgumentTypeError(
            "--shop-unlock needs TYPE:MISSION[,MISSION] or TYPE:none"
        )
    ship_type = int(match.group(1))
    missions = ([] if match.group(2) == "none" else
                [int(part) for part in match.group(2).split(",")])
    if (not 0 <= ship_type < 24 or
            len(missions) != len(set(missions)) or
            any(not 1 <= mission <= 40 for mission in missions)):
        raise argparse.ArgumentTypeError(
            "--shop-unlock requires a ship type 0..23 and unique missions 1..40"
        )
    return ship_type, missions


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("output_dir", type=Path,
                        help="new directory for a self-contained native campaign catalog")
    parser.add_argument("--mission", type=Path, action="append", required=True,
                        help="authored game-only battle.run.json; repeat in mission order")
    parser.add_argument("--player-side", type=int, choices=(1, 2), default=1)
    parser.add_argument("--title", default="Native Fleet Campaign")
    parser.add_argument("--persist-enemy-losses", action="store_true",
                        help="carry permanent, source-authored enemy FleetShip casualties between all campaign missions; temporary reinforcement ships are excluded")
    parser.add_argument("--shop-types", type=ship_types, default=[],
                        help="optional purchasable hull classes: comma-separated ship types")
    parser.add_argument("--grant-only-types", type=ship_types, default=[],
                        help="source-backed reward classes that can never be purchased")
    parser.add_argument("--mission-requires", action="append", type=preceding_missions,
                        default=[], metavar="MISSION:PREVIOUS[,PREVIOUS]",
                        help="override linear prerequisites; MISSION:none makes a parallel start")
    parser.add_argument("--mission-advance", action="append", type=mission_advance,
                        default=[], metavar="MISSION:victory|finished",
                        help="choose the finished-battle or victorious-battle unlock rule")
    parser.add_argument("--mission-grant-on", action="append", type=mission_advance,
                        default=[], metavar="MISSION:victory|finished",
                        help="award the mission's authored FleetShips and SavedSquads after a committed finish, including a defeat")
    parser.add_argument("--mission-bank-on", action="append", type=mission_advance,
                        default=[], metavar="MISSION:victory|finished",
                        help="bank surviving ships' real mined TSV after a committed finish, including defeat")
    parser.add_argument("--mission-unlock-any", action="append", type=mission_unlock_any,
                        default=[], metavar="MISSION:PRIOR:OUTCOME",
                        help="unlock a mission through any listed earlier finished, victory or defeat outcome; use --mission-requires MISSION:none for a full alternative route")
    parser.add_argument("--mission-story", action="append", type=mission_story,
                        default=[], metavar="MISSION:STORY",
                        help="attach original native campaign dialogue and terminal cues to an authored mission")
    parser.add_argument("--mission-variant", action="append", type=mission_variant,
                        default=[], metavar="MISSION:PREVIOUS:RUN",
                        help="use another native battle if the required previous mission's latest result was victory")
    parser.add_argument("--mission-defeat-variant", action="append", type=mission_variant,
                        default=[], metavar="MISSION:PREVIOUS:RUN",
                        help="use another native battle if the required previous mission's latest result was defeat; previous mission must advance on finished")
    parser.add_argument("--shop-unlock", action="append", type=shop_unlock,
                        default=[], metavar="TYPE:MISSION[,MISSION]",
                        help="unlock a purchasable class after named mission(s); TYPE:none unlocks it initially")
    parser.add_argument("--max-squad-ships", type=int, default=10,
                        help="saved-squad ship cap when a fleet shop or reward is enabled (default: 10)")
    parser.add_argument("--victory-grant", action="append", type=victory_grant,
                        default=[], metavar="MISSION:TYPE:COUNT:SQUAD",
                        help="one-time victory FleetShip rewards, using a --shop-types or --grant-only-types class")
    parser.add_argument("--victory-squad", action="append", type=victory_squad,
                        default=[], metavar="MISSION:NEW_SQUAD:TEMPLATE_SQUAD",
                        help="one-time victory SavedSquad; ships may be awarded into this ID in the same mission")
    args = parser.parse_args()
    if (any(mission > len(args.mission) for mission, _ in args.mission_story) or
            len({mission for mission, _ in args.mission_story}) != len(args.mission_story)):
        parser.error("--mission-story needs distinct existing mission indices")
    if any(mission > len(args.mission) for mission, _, _ in args.mission_unlock_any):
        parser.error("--mission-unlock-any refers to an absent mission")
    if len(set(args.mission_unlock_any)) != len(args.mission_unlock_any):
        parser.error("--mission-unlock-any contains a duplicate outcome gate")
    if any(mission > len(args.mission) for mission, _, _ in args.victory_squad):
        parser.error("--victory-squad refers to a mission not included by --mission")
    if any(mission > len(args.mission) for mission, _, _, _ in args.victory_grant):
        parser.error("--victory-grant refers to a mission not included by --mission")
    if set(args.shop_types) & set(args.grant_only_types):
        parser.error("a fleet class cannot be both purchasable and grant-only")
    if set(args.grant_only_types) - {
            kind for _, kind, _, _ in args.victory_grant}:
        parser.error("--grant-only-types must be awarded by at least one --victory-grant")
    if not 1 <= args.max_squad_ships <= 1024:
        parser.error("--max-squad-ships must be within 1..1024")
    for label, rules in (
        ("--mission-requires", args.mission_requires),
        ("--mission-advance", args.mission_advance),
        ("--mission-grant-on", args.mission_grant_on),
        ("--mission-bank-on", args.mission_bank_on),
        ("--mission-variant", [(mission, path) for mission, _, path
                              in args.mission_variant]),
        ("--mission-defeat-variant", [(mission, path) for mission, _, path
                                     in args.mission_defeat_variant]),
        ("--shop-unlock", args.shop_unlock),
    ):
        ids = [index for index, _ in rules]
        if len(set(ids)) != len(ids):
            parser.error(f"{label} may only configure each mission/class once")
        limit = 23 if label == "--shop-unlock" else len(args.mission)
        if any(index > limit for index in ids):
            parser.error(f"{label} refers to an absent mission or hull class")
    if any(kind not in args.shop_types for kind, _ in args.shop_unlock):
        parser.error("--shop-unlock requires a purchasable --shop-types class")
    if any(prerequisite > len(args.mission)
           for _, prerequisites in args.shop_unlock
           for prerequisite in prerequisites):
        parser.error("--shop-unlock refers to an absent mission")
    try:
        catalog, library, outputs, receipt, model_files, runtime_files, stage_files = campaign(args)
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
            write_pinned_policy_assets(
                staged, model_files, runtime_files, stage_files,
            )
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
        "victory_squads": receipt["victory_squads"],
        "player_side": args.player_side,
    }, sort_keys=True))


if __name__ == "__main__":
    main()
