#!/usr/bin/env python3
"""Turn a verified native training checkpoint into a playable human-vs-policy battle.

Only controller ownership and the existing model-slot route change. The scene's
physical ship, turret, special ability, collider, reward baseline, and authored
Stage data are retained byte-for-byte as JSON values. No Unity runtime or
second battle simulation is involved.
"""

import argparse
import copy
import json
import os
from pathlib import Path
import re
import tempfile

from native_training_artifacts import atomic_json, check_learner, file_sha256, sync_directory


def document(path: Path) -> dict:
    value = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(value, dict):
        raise ValueError(f"expected JSON object: {path}")
    return value


def existing_path(owner: Path, value: str) -> Path:
    if not isinstance(value, str) or not value:
        raise ValueError("missing required native artifact path")
    return (owner.parent / value).resolve(strict=True)


def selected_run(receipt_path: Path) -> tuple[dict, dict, int, Path]:
    """Trust the published checkpoint lineage, not an editable filename."""
    receipt = document(receipt_path)
    kind = receipt.get("kind")
    if kind == "native_training_best_evaluated":
        selected = receipt.get("selected")
    elif kind == "native_training_latest_playable":
        selected = receipt
    else:
        raise ValueError("select a native best-evaluated.json or latest-playable.json receipt")
    if not isinstance(selected, dict):
        raise ValueError("native training selection has no verified checkpoint")
    run_path = existing_path(receipt_path, receipt.get("playable_run"))
    if file_sha256(run_path) != receipt.get("playable_run_sha256"):
        raise ValueError("published native playable run changed after checkpoint selection")
    manifest = existing_path(receipt_path, selected.get("manifest"))
    checkpoint = existing_path(receipt_path, selected.get("checkpoint"))
    if file_sha256(manifest) != selected.get("manifest_sha256"):
        raise ValueError("selected native model manifest has changed")
    if file_sha256(checkpoint) != selected.get("checkpoint_sha256"):
        raise ValueError("selected native checkpoint has changed")
    slot = receipt.get("learner_slot")
    if type(slot) is not int or not 0 <= slot < (1 << 32):
        raise ValueError("selected native learner model slot is invalid")
    check_learner(manifest, checkpoint, slot)
    run = document(run_path)
    if existing_path(run_path, run.get("model_manifest")) != manifest:
        raise ValueError("selected gameplay run uses a different model manifest")
    return receipt, run, slot, run_path


def rebase_run_sources(run: dict, original_run: Path) -> None:
    """Bind read-only game assets to their verified training-time locations.

    The published challenge is a *new* run file. Retaining original relative
    paths would silently resolve model/worker inputs against its new folder
    and break both live play and subsequent portable packaging.
    """
    for key in ("model_manifest", "policy_worker", "collision_matrix", "stage_palette"):
        value = run.get(key)
        if value is not None:
            source = existing_path(original_run, value)
            if not source.is_file():
                raise ValueError(f"native challenge source {key} is not a regular file")
            run[key] = str(source)
    python = run.get("python")
    if isinstance(python, str) and python and (
            Path(python).is_absolute() or len(Path(python).parts) > 1):
        source = existing_path(original_run, python)
        if not source.is_file():
            raise ValueError("native challenge Python interpreter must be a file")
        run["python"] = str(source)


def challenge(
    receipt_path: Path, player_side: int, level_index: int | None = None,
) -> tuple[dict, dict, dict, dict]:
    receipt, run, learner_slot, run_path = selected_run(receipt_path)
    levels = run.get("levels")
    if (not isinstance(levels, list) or not levels or
            not all(isinstance(level, dict) for level in levels) or
            run.get("hivemind") is not None or run.get("model_updates") or
            run.get("recover_notifications") or
            run.get("recover_outcome_journals") or
            "rollout_bundles" in run):
        raise ValueError("native interactive challenge needs offline physical Levels")
    if level_index is None:
        if len(levels) > 1:
            raise ValueError(
                f"training published {len(levels)} Levels; select one with --level-index 1..{len(levels)}"
            )
        level_index = 1
    if type(level_index) is not int or not 1 <= level_index <= len(levels):
        raise ValueError(f"--level-index must be between 1 and {len(levels)}")
    # A game challenges one original physical arena, not a whole trainer
    # pool, and the original Level's source RNG/episode seed is retained.
    run["levels"] = [levels[level_index - 1]]
    level = run["levels"][0]
    scene_path = existing_path(run_path, level.get("scene"))
    scene_sha = file_sha256(scene_path)
    scene = document(scene_path)
    if scene.get("native_game_schema") != 1:
        raise ValueError("interactive challenge needs a Unity-free native_game_schema=1 scene")
    if not isinstance(scene.get("policy_episode"), dict):
        raise ValueError("challenge source has no authentic policy episode and observation contract")
    bee, human = scene.get("bee_side"), scene.get("human_side")
    if {bee, human} != {1, 2} or player_side not in (bee, human):
        raise ValueError("native challenge requires the two authored sides 1 and 2")
    ai_side = human if player_side == bee else bee
    ships = scene.get("ships")
    squads = scene.get("squads")
    if not isinstance(ships, list) or not isinstance(squads, list):
        raise ValueError("native challenge scene has no valid ship or squad roster")
    if not any(ship.get("policy") is not None and ship.get("side") == ai_side
               for ship in ships):
        raise ValueError("opposing faction has no authentic policy-controlled ships")
    if not any(ship.get("side") == player_side for ship in ships):
        raise ValueError("selected player faction has no physical ships")

    # The source run's manifest/worker were already SHA-verified under
    # its original root. Preserve their identity after publication.
    rebase_run_sources(run, run_path)
    scene = copy.deepcopy(scene)
    scene["local_player_side"] = player_side
    players = set()
    policy_teams = set()
    for ship in scene["ships"]:
        if ship["side"] == player_side:
            players.add(ship["entity_id"])
            ship.pop("policy", None)
            ship["user_controlled"] = True
        elif ship["side"] == ai_side:
            ship["user_controlled"] = False
            if ship.get("policy") is not None:
                policy_teams.add(ship["policy"]["team_id"])
    if len(policy_teams) != 1:
        raise ValueError("trained opponent must use exactly one native MA-POCA team")
    team = next(iter(policy_teams))
    if team not in (0, 1):
        raise ValueError("opponent policy team must be 0 or 1")
    for squad in scene["squads"]:
        selected = squad["side"] == player_side
        squad["user_controlled"] = selected
        squad["can_accept_input"] = selected
        if selected:
            if not set(squad["members"]).issubset(players):
                raise ValueError("player squad references a non-player physical ship")
        else:
            squad.pop("hotkey_number", None)
            squad.pop("original_squad_count", None)
    # A human must never compete with the scripted AI command director.
    player_squads = {
        squad["item_id"] for squad in scene["squads"]
        if squad["side"] == player_side
    }
    level["scripted_queues"] = [
        order for order in level.get("scripted_queues", [])
        if order["squad_item_id"] not in player_squads
    ]
    # The authored policy episode and agent team identities are unchanged.
    # Native Level routing maps the trained slot to the remaining opponent's
    # *actual* team (not a hard-coded side or an imagined new policy).
    # One learner may have trained on the other physical side originally.
    slots = level.get("model_slots")
    if not isinstance(slots, dict) or str(team) not in slots:
        raise ValueError("selected run has no real model route for the opponent team")
    slots[str(team)] = learner_slot
    level.pop("groups", None)  # The source scene now has only opponent agents.
    level["scene"] = "challenge.scene.json"
    if (type(run.get("max_rounds")) is not int or
            type(run.get("episodes_per_world")) is not int or
            run["episodes_per_world"] <= 0 or
            run["max_rounds"] % run["episodes_per_world"]):
        raise ValueError("cannot derive a complete playable episode budget")
    episodes = run["episodes_per_world"]
    if level.get("episode_seeds") is not None:
        seeds = level["episode_seeds"]
        if not isinstance(seeds, list) or len(seeds) != episodes:
            raise ValueError("selected native Level has an invalid episode seed schedule")
        level["episode_seeds"] = seeds[:1]
    run["max_rounds"] //= episodes
    run["episodes_per_world"] = 1
    run["run_id"] += f"-human-{player_side}-level-{level_index}"
    run["connection_checkpoint"] = "challenge-connection.json"
    run["notifications"] = "challenge-events.jsonl"
    run["rollout_summary"] = "challenge-summary.json"
    run["policy_capture"] = None
    scene["level_name"] += " (Trained Policy Challenge)"
    # Preserve stage_name: it binds immutable Stage geometry and the run.
    library = {
        "library_schema": 1,
        "games": [{
            "id": "trained-policy-challenge",
            "title": scene["level_name"],
            "kind": "battle",
            "config": "challenge.run.json",
        }],
    }
    provenance = {
        "schema_version": 1,
        "kind": "native_policy_human_challenge",
        "selection_kind": receipt["kind"],
        "selection_receipt": str(receipt_path),
        "selection_receipt_sha256": file_sha256(receipt_path),
        "source_scene": str(scene_path),
        "source_scene_sha256": scene_sha,
        "source_level_index": level_index,
        "source_world_id": level.get("world_id"),
        "source_level_id": level.get("level_id"),
        "learner_slot": learner_slot,
        "opponent_team": team,
        "player_side": player_side,
        "opponent_side": ai_side,
        "selected_checkpoint_sha256": (
            receipt["selected"]["checkpoint_sha256"]
            if "selected" in receipt else receipt["checkpoint_sha256"]
        ),
    }
    return scene, run, library, provenance


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("selection_receipt", type=Path,
                        help="native training session best-evaluated.json or latest-playable.json")
    parser.add_argument("output_dir", type=Path,
                        help="new native challenge directory")
    parser.add_argument("--player-side", type=int, choices=(1, 2), default=1,
                        help="which physical faction a human commands")
    parser.add_argument("--level-index", type=int,
                        help="1-based physical Level from a multi-arena training run")
    args = parser.parse_args()
    try:
        receipt = args.selection_receipt.resolve(strict=True)
        scene, run, library, provenance = challenge(
            receipt, args.player_side, args.level_index,
        )
        output = args.output_dir.resolve()
        if output.exists():
            raise FileExistsError(f"refusing to overwrite existing native challenge: {output}")
        if not output.parent.is_dir():
            raise ValueError("challenge output's parent directory must already exist")
        with tempfile.TemporaryDirectory(
            prefix=f".{output.name}.challenge-", dir=output.parent,
        ) as temp:
            staged = Path(temp) / "published"
            staged.mkdir()
            for name, payload in (
                ("challenge.scene.json", scene),
                ("challenge.run.json", run),
                ("library.json", library),
            ):
                atomic_json(staged / name, payload)
            provenance["scene_sha256"] = file_sha256(staged / "challenge.scene.json")
            provenance["run_sha256"] = file_sha256(staged / "challenge.run.json")
            atomic_json(staged / "challenge-receipt.json", provenance)
            if output.exists():
                raise FileExistsError("challenge destination appeared before publication")
            os.rename(staged, output)
            sync_directory(output.parent)
    except (OSError, ValueError, KeyError, TypeError) as error:
        parser.exit(2, f"native challenge publication failed: {error}\n")
    print(json.dumps({
        "kind": "native_trained_policy_challenge_ready",
        "run": str(output / "challenge.run.json"),
        "library": str(output / "library.json"),
        "human_side": args.player_side,
        "source_level_index": provenance["source_level_index"],
        "source_world_id": provenance["source_world_id"],
        "opponent_policy_team": provenance["opponent_team"],
        "checkpoint_sha256": provenance["selected_checkpoint_sha256"],
    }, sort_keys=True))


if __name__ == "__main__":
    main()
