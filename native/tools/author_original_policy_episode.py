#!/usr/bin/env python3
"""Enroll authentic captured Unity fleets in the existing native policy Level.

This is a source-backed control/episode overlay, NOT a new simulator. The
unchanged original physical ships, weapons, collider bodies, navigation map,
squad identities, and Stage are loaded by NativePhysicalScene::parse and
the SAME canonical CombatWorld used by native game playback. The input game
Run is produced by author_original_unity_level.py.

Explicit experiment choices (actors, model slots, timeout, cadence, seed) are
never inferred from the original game or substituted for RL hyperparameters.
All source-only observations and capabilities must have been captured by the
current Unity exporter, including unbound non-turret weapon slots.
"""

import argparse
import copy
import hashlib
import json
import math
import os
from pathlib import Path
import shutil
import tempfile

from author_original_unity_level import canonical_original_ai, vector
from native_training_artifacts import atomic_json, file_sha256, sync_directory


POLICY_FIELDS = (
    "special_resource_fraction", "special_cooldown_fraction", "special_phase",
)
BOUNDED_FILES = 64 * 1024 * 1024


def read(path: Path) -> dict:
    if path.stat().st_size > BOUNDED_FILES:
        raise ValueError(f"native original source is too large: {path}")
    obj = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(obj, dict):
        raise ValueError(f"native original source must be JSON object: {path}")
    return obj


def strict_model_slots(text: str) -> dict[str, int]:
    # Exactly one independently selected model per policy team.
    # Both slots are required for source v26 self-play, even if weights
    # intentionally point to identical model manifests.
    parts = text.split(",")
    if len(parts) != 2 or any(not part.isdecimal() for part in parts):
        raise ValueError("model slots must be two nonnegative integer IDs: SLOT0,SLOT1")
    first, second = map(int, parts)
    if first == second or max(first, second) >= (1 << 32):
        raise ValueError("source policy teams require two distinct valid model slots")
    return {"0": first, "1": second}


def reward_baselines(scene: dict) -> tuple[dict[int, int], list[int]]:
    """Use only original scene-owned physical game reward denominators."""
    bee, human = scene["bee_side"], scene["human_side"]
    if (type(bee) is not int or type(human) is not int or
            {bee, human} != {1, 2}):
        raise ValueError("source native policy requires both original physical teams")
    fleet = {bee: 0, human: 0}
    for ship in scene["ships"]:
        if ship.get("persistent_fleet") is True:
            value = ship.get("max_tsv")
            if type(value) is not int or value < 0:
                raise ValueError("source persistent ship TSV is invalid")
            fleet[ship["side"]] += value
    if any(value <= 0 or value > (1 << 31) - 1 for value in fleet.values()):
        raise ValueError("both original policy teams require positive persistent starting TSV")

    mining = statics = objects = 0
    for obstacle in scene.get("obstacles", []):
        if not isinstance(obstacle, dict):
            raise ValueError("original physical obstacle is malformed")
        kind = obstacle.get("kind")
        health = obstacle.get("health")
        maximum = obstacle.get("effective_max_health")
        if type(health) is not int or type(maximum) is not int:
            raise ValueError("source obstacle health is not captured")
        if kind == "mining":
            mining += max(1, health)
        elif kind in ("box", "polygon"):
            statics += max(1, maximum if maximum > 0 else health)
        elif kind in ("full-asteroid", "shard"):
            continue  # source discovery counts mining/static objects only
        else:
            raise ValueError(f"unknown source obstacle kind: {kind}")
    for obj in scene.get("map_objects", []):
        value = obj.get("max_health")
        if type(value) is not int or value <= 0:
            raise ValueError("original MapObject reward denominator is unavailable")
        objects += value
    denominators = [
        fleet[human], mining, statics, objects,
        fleet[bee], mining, statics, objects,
    ]
    if any(value < 0 or value > (1 << 31) - 1 for value in denominators):
        raise ValueError("original discovery reward totals overflow source v26 contract")
    return fleet, denominators


def source_actors(scene: dict, ids: set[int]) -> tuple[list[dict], set[int]]:
    """Validate every requested policy actor before changing physical ownership."""
    if (scene.get("schema_version") != 35 or
            scene.get("native_game_schema") is not None or
            scene.get("native_policy_capabilities_captured") is not True or
            scene.get("has_policy_episode") is not False or
            scene.get("activate_hivemind") is not False or
            not isinstance(scene.get("ships"), list) or
            not isinstance(scene.get("squads"), list) or
            not isinstance(scene.get("targeting_context"), dict)):
        raise ValueError(
            "requires the new original Unity schema35 non-HiveMind source "
            "with complete independently captured physical policy capabilities"
        )
    by_id = {ship.get("entity_id"): ship for ship in scene["ships"]}
    if (len(by_id) != len(scene["ships"]) or
            any(type(ship_id) is not int or ship_id <= 0 for ship_id in by_id)):
        raise ValueError("original physical ship IDs are invalid or duplicate")
    if not ids or not ids.issubset(by_id):
        raise ValueError("selected actor IDs must refer to real captured source ships")
    if any(ship.get("has_bound_policy") or ship.get("is_rl_policy_controlled")
           for ship in scene["ships"]):
        raise ValueError("source must be an ordinary game before native policy authoring")
    by_side = {scene["bee_side"]: [], scene["human_side"]: []}
    for ship_id in sorted(ids):
        ship = by_id[ship_id]
        side = ship.get("side")
        if side not in by_side:
            raise ValueError("selected source ship has an unknown policy side")
        if (ship.get("health", 0) <= 0 or
                not isinstance(ship.get("observed_weapons"), list) or
                not isinstance(ship.get("self_weapons"), list) or
                not isinstance(ship.get("turrets"), list) or
                not isinstance(ship.get("has_weapons"), bool) or
                not isinstance(ship.get("has_turrets"), bool) or
                not isinstance(ship.get("has_special_action"), bool) or
                not isinstance(ship.get("special_ready"), bool) or
                type(ship.get("side_starting_tsv")) is not int or
                ship["side_starting_tsv"] <= 0 or
                not all(isinstance(ship.get(key), bool) for key in (
                    "mining_possible", "healing_possible", "warp_possible",
                    "has_living_parent_carrier", "is_carrier_child"
                )) or
                any(type(ship.get(key)) not in (int, float) or
                    not math.isfinite(ship[key]) or not 0 <= ship[key] <= 1
                    for key in POLICY_FIELDS)):
            raise ValueError(f"physical source actor {ship_id} lacks authentic v26 action inputs")
        slots = set()
        for weapon in ship["self_weapons"] + ship["turrets"]:
            slot = weapon.get("slot")
            if type(slot) is not int or not 0 <= slot < 5 or slot in slots:
                raise ValueError(f"source weapon slot is invalid or repeated on ship {ship_id}")
            slots.add(slot)
        observed = set()
        for weapon in ship["observed_weapons"]:
            slot = weapon.get("slot")
            if type(slot) is not int or not 0 <= slot < 5 or slot in observed:
                raise ValueError(f"source observed weapon slots are invalid on ship {ship_id}")
            observed.add(slot)
        if slots != observed:
            raise ValueError(f"ship {ship_id} policy weapons differ from the original source")
        by_side[side].append(ship)
    if any(not side_ships for side_ships in by_side.values()):
        raise ValueError("native group policy needs real initial participants on BOTH sides")
    selected_squads = set()
    owned = set()
    for squad in scene["squads"]:
        members = squad.get("member_ids")
        if (type(squad.get("item_id")) is not int or
                not isinstance(members, list) or not members):
            raise ValueError("source Squad identity or physical member list is invalid")
        members_set = set(members)
        if len(members_set) != len(members) or owned & members_set:
            raise ValueError("source game has overlapping or duplicate squad members")
        owned |= members_set
        if members_set & ids:
            if not members_set.issubset(ids):
                raise ValueError("train whole original command squads, not individual mixed-control members")
            selected_squads.add(squad["item_id"])
    if owned != set(by_id):
        raise ValueError("source physical ships need exact original squad membership")
    return [by_id[ship_id] for ship_id in sorted(ids)], selected_squads


def author_policy(source: dict, game: dict, ids: set[int], slots: dict[str, int],
                  timeout: float, period: int, episodes: int,
                  seed: int, tactics: str) -> tuple[dict, dict, dict]:
    if (type(timeout) not in (float, int) or
            not math.isfinite(timeout) or not 1 <= timeout <= 86400 or
            type(period) is not int or not 1 <= period <= 10000 or
            type(episodes) is not int or not 1 <= episodes <= 64 or
            type(seed) is not int or not 0 <= seed < (1 << 64)):
        raise ValueError("source experiment time, decision period, episodes or seed is invalid")
    if (game.get("hivemind") is not None or
            not isinstance(game.get("levels"), list) or len(game["levels"]) != 1 or
            game["levels"][0].get("scene") != "source-level.json" or
            game.get("model_manifest") is not None):
        raise ValueError("original policy source needs one native physical game Level and no external server")
    if (game.get("stage_name") != source.get("stage_name") or
            game["levels"][0].get("groups") is not None or
            game["levels"][0].get("model_slots")):
        raise ValueError("original game Stage or command owner has an incompatible native policy source")
    if (game.get("fragment_spawn") or {}).get("map_bounds") != {
        key: vector(source[key], key) for key in ("min", "max")
    }:
        raise ValueError("physical game Run bounds differ from original Unity capture")
    actors, policy_squads = source_actors(source, ids)
    fleet, denominators = reward_baselines(source)

    scene = copy.deepcopy(source)
    config = copy.deepcopy(game)
    by_id = {ship["entity_id"]: ship for ship in scene["ships"]}
    for ship in scene["ships"]:
        # Original gameplay and policy runs share the same static geometry,
        # hull, collider, velocity, special weapon, turret and source sight.
        # Only command ownership and MA-POCA initial metadata differ.
        ship["user_controlled"] = False
    for squad in scene["squads"]:
        squad["user_controlled"] = False
        squad["can_accept_input"] = False
        # Never carry a remote HiveMind command source into a native
        # learner-only Level, even if a source Squad retained old IDs.
        squad["is_hivemind_controlled"] = False
    scene["targeting_context"]["has_player"] = False
    scene["activate_hivemind"] = False

    # The native experiment explicitly chooses and pins the original
    # faction->team and Agent identities. Unity's captured physical field
    # schema is reused solely as a transport contract to the EXISTING
    # strict native snapshot/CombatWorld importer.
    participants = []
    for ship in actors:
        target = by_id[ship["entity_id"]]
        team = 0 if target["side"] == scene["bee_side"] else 1
        agent = 2_000_000 + target["entity_id"]
        if agent >= (1 << 64):
            raise ValueError("native source agent ID exceeds original 64-bit group identity")
        target.update({
            "has_bound_policy": True,
            "is_rl_policy_controlled": True,
            "policy_team_id": team,
            "policy_quarter_turns": team,
            "policy_movement_disabled": False,
            "side_starting_tsv": fleet[target["side"]],
            "team_retainable_mined_tsv": 0,
        })
        participants.append({
            "ship_id": target["entity_id"],
            "side": target["side"],
            "agent_id": agent,
            "team_id": team,
        })
    scene["has_policy_episode"] = True
    scene["policy_episode"] = {
        "bee_side": scene["bee_side"],
        "human_side": scene["human_side"],
        "bee_starting_tsv": fleet[scene["bee_side"]],
        "human_starting_tsv": fleet[scene["human_side"]],
        "timeout_seconds": float(timeout),
        "discovery_baselines": denominators,
        "episode_number": 1,
        "team0_movement_disabled": False,
        "team1_movement_disabled": False,
        "group_participants": participants,
    }

    level = config["levels"][0]
    level["groups"] = {
        "episode_number": 1,
        "participants": [{
            "side": item["side"], "agent_id": item["agent_id"],
        } for item in participants],
    }
    level["model_slots"] = slots
    level["seed"] = seed
    level.pop("episode_seeds", None)
    # The original game command director is the ONLY non-learner owner.
    # The same command queue executes in the actual CombatWorld that
    # produced these policy observations.
    level["scripted_queues"] = [
        queue for queue in level.get("scripted_queues", [])
        if queue["squad_item_id"] not in policy_squads
    ]
    commands = canonical_original_ai(
        scene, {
            "min": vector(scene["min"], "min"),
            "max": vector(scene["max"], "max"),
        }, level, tactics, False, frozenset(policy_squads),
    )
    level["scripted_queues"].extend(commands)
    config["decision_period"] = period
    config["episodes_per_world"] = episodes
    # At least the requested duration in exact physical fixed steps,
    # with room for terminal bookkeeping and asynchronous inference.
    dt = scene["fixed_dt_seconds"]
    if type(dt) not in (float, int) or not math.isfinite(dt) or dt <= 0:
        raise ValueError("source Level fixed dt is invalid")
    config["max_rounds"] = max(config["max_rounds"], math.ceil(timeout / dt) + 16)
    receipt = {
        "selected_policy_ship_ids": sorted(ids),
        "selected_policy_squad_ids": sorted(policy_squads),
        "source_game_ai_squad_ids": [
            entry["squad_item_id"] for entry in level["scripted_queues"]
        ],
        "original_physical_stage": scene["stage_name"],
        "episode_number": 1,
        "bee_side": scene["bee_side"],
        "human_side": scene["human_side"],
        "discovery_baselines": denominators,
        "model_slots": slots,
        "decision_period": period,
        "episodes_per_world": episodes,
        "timeout_seconds": timeout,
    }
    return scene, config, receipt


def author(output: Path, original_run: Path, model_manifest: Path,
           policy_worker: Path, python: str, ship_ids: set[int],
           slots: dict[str, int], timeout: float, period: int,
           episodes: int, seed: int, tactics: str) -> dict:
    run_path = original_run.resolve(strict=True)
    original = read(run_path)
    scene_path = (run_path.parent / original["levels"][0]["scene"]).resolve(strict=True)
    source = read(scene_path)
    model_manifest = model_manifest.resolve(strict=True)
    policy_worker = policy_worker.resolve(strict=True)
    inputs = [run_path, scene_path, model_manifest, policy_worker]
    hashes = {str(path): file_sha256(path) for path in inputs}
    annotated, run, receipt = author_policy(
        source, original, ship_ids, slots, timeout, period, episodes, seed,
        tactics,
    )
    identity = hashlib.sha256(json.dumps(
        {"inputs": hashes, "actors": sorted(ship_ids),
         "slots": slots, "timeout": timeout, "period": period,
         "episodes": episodes, "seed": seed, "tactics": tactics,
         "interpreter": python},
        sort_keys=True, separators=(",", ":"),
    ).encode("utf-8")).hexdigest()
    run["run_id"] = "native-original-policy-" + identity[:20]
    run["levels"][0]["scene"] = "original-policy.scene.json"
    run["python"] = python
    run["policy_worker"] = str(policy_worker)
    run["model_manifest"] = str(model_manifest)
    run["policy_capture"] = "policy-capture.json"
    run["rollout_summary"] = "rollout-summary.json"
    run["notifications"] = "events.jsonl"
    run["connection_checkpoint"] = "connection-checkpoint.json"
    output = output.resolve()
    if not output.parent.is_dir() or output.exists() or output.is_symlink():
        raise FileExistsError("native original-policy episode requires a fresh output directory")
    with tempfile.TemporaryDirectory(prefix=f".{output.name}.source-policy-", dir=output.parent) as temp:
        stage = Path(temp) / "complete"
        stage.mkdir()
        atomic_json(stage / "original-policy.scene.json", annotated)
        atomic_json(stage / "run.json", run)
        for path, sha in hashes.items():
            if file_sha256(Path(path)) != sha:
                raise ValueError("original physical scene, game Run or model changed during policy binding")
        receipt.update({
            "schema_version": 1,
            "kind": "native_original_source_policy_authored",
            "original_inputs_sha256": hashes,
            "run_id": run["run_id"],
            "native_policy_scene_sha256": file_sha256(stage / "original-policy.scene.json"),
            "native_policy_run_sha256": file_sha256(stage / "run.json"),
            "policy_training_uses_original_combat_world": True,
        })
        atomic_json(stage / "native-source-policy-receipt.json", receipt)
        sync_directory(stage)
        if output.exists() or output.is_symlink():
            raise FileExistsError("source policy destination was claimed during authoring")
        os.rename(stage, output)
        sync_directory(output.parent)
        return receipt


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("native_game_run", type=Path,
                        help="original physical native Run created by author_original_unity_level.py")
    parser.add_argument("output_directory", type=Path)
    parser.add_argument("--model-manifest", type=Path, required=True)
    parser.add_argument("--policy-worker", type=Path, required=True)
    parser.add_argument("--python", default="auto",
                        help="policy runtime executable; auto uses platform-native PATH")
    parser.add_argument("--policy-ship", type=int, required=True, action="append",
                        help="original live physical ship ID; repeat for all selected actors")
    parser.add_argument("--model-slots", default="0,1",
                        help="two genuine model slots for team0,team1 (default 0,1)")
    parser.add_argument("--timeout-seconds", type=float, required=True)
    parser.add_argument("--decision-period", type=int, default=1)
    parser.add_argument("--episodes-per-world", type=int, default=1)
    parser.add_argument("--seed", required=True, type=int)
    parser.add_argument("--source-ai-tactics", default="aggressive",
                        choices=("aggressive", "circle", "right-swipe", "left-swipe",
                                 "in-and-out", "mixed"))
    args = parser.parse_args()
    print(json.dumps(author(
        args.output_directory, args.native_game_run,
        args.model_manifest, args.policy_worker, args.python,
        set(args.policy_ship), strict_model_slots(args.model_slots),
        args.timeout_seconds, args.decision_period,
        args.episodes_per_world, args.seed, args.source_ai_tactics,
    ), sort_keys=True), flush=True)


if __name__ == "__main__":
    main()
