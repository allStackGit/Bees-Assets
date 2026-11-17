#!/usr/bin/env python3
"""Register a verified Experiment Hub Bees round as a native playable game.

The Hub owns the process, resource lease, immutable round result and model
continuation. This publication is only the existing native game-gallery
handoff: no extra optimizer, model conversion or gameplay implementation.
"""
import argparse
import json
from pathlib import Path

from native_game_registration import register_playable
from native_session_selection import _playable_run
from native_training_artifacts import (
    atomic_json, check_learner, file_sha256,
)


def read_json(file: Path) -> dict:
    document = json.loads(file.read_text(encoding="utf-8"))
    if not isinstance(document, dict):
        raise ValueError(f"Expected a native JSON object: {file}")
    return document


def publish(
    run_path: Path, round_directory: Path, save_root: Path,
    game_id: str, learner_slot: int, source_sha256: str,
) -> dict:
    run_path = run_path.resolve(strict=True)
    round_directory = round_directory.resolve(strict=True)
    result = read_json(round_directory / "round-result.json")
    metrics = result.get("learner_metrics")
    if (result.get("schema_version") != 1 or
            result.get("kind") != "native_training_round_complete" or
            not isinstance(metrics, dict) or
            metrics.get("source") != "bees-native-ma-poca-v1" or
            metrics.get("learner_slot") != learner_slot):
        raise ValueError("Hub job has no completed authenticated native learner round")
    if file_sha256(run_path) != source_sha256:
        raise ValueError("Hub source RunConfig changed after its machine allocation")
    checkpoint = round_directory / "learner/checkpoint.pt"
    manifest = round_directory / "learner/manifest.json"
    checkpoint_sha = file_sha256(checkpoint)
    manifest_sha = file_sha256(manifest)
    if (checkpoint_sha != result.get("next_checkpoint_sha256") or
            manifest_sha != result.get("next_manifest_sha256") or
            checkpoint_sha != metrics.get("next_checkpoint_sha256") or
            manifest_sha != metrics.get("next_manifest_sha256")):
        raise ValueError("Hub model or checkpoint differs from the completed learner receipt")
    check_learner(manifest, checkpoint, learner_slot)

    # The existing session publisher binds the same physical source Levels,
    # source asset paths and verified model identity for ordinary native play.
    playable = _playable_run(run_path, {
        "checkpoint_sha256": checkpoint_sha,
        "manifest": str(manifest),
    })
    playable["run_id"] += "-" + game_id
    config_path = round_directory / "latest-playable-run.json"
    receipt_path = round_directory / "latest-playable.json"

    def write_once(file: Path, value: dict) -> None:
        if file.is_symlink():
            raise ValueError("native Hub publication cannot use symbolic links")
        if file.exists():
            if read_json(file) != value:
                raise ValueError(f"previous Hub gameplay publication changed: {file}")
        else:
            atomic_json(file, value)

    write_once(config_path, playable)
    receipt = {
        "schema_version": 1,
        "kind": "native_training_latest_playable",
        "selection": "experiment_hub_verified_completed_round",
        "completed_rounds": 1,
        "learner_slot": learner_slot,
        "checkpoint": str(checkpoint),
        "checkpoint_sha256": checkpoint_sha,
        "manifest": str(manifest),
        "manifest_sha256": manifest_sha,
        "source_run_config": str(run_path),
        "source_run_config_sha256": source_sha256,
        "playable_run": str(config_path),
        "playable_run_sha256": file_sha256(config_path),
    }
    write_once(receipt_path, receipt)
    source = read_json(run_path)
    title = source["run_id"][:97] + " (Experiment Hub)"
    registration = register_playable(save_root, game_id, title, receipt_path)
    return {
        "kind": "native_hub_game_registered",
        "game_id": game_id,
        "source_run": str(run_path),
        "gallery_entry": str(registration),
        "playable_run": str(config_path),
        "checkpoint_sha256": checkpoint_sha,
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source_run", type=Path)
    parser.add_argument("completed_round", type=Path)
    parser.add_argument("player_save_root", type=Path)
    parser.add_argument("game_id")
    parser.add_argument("learner_slot", type=int)
    parser.add_argument("source_run_sha256")
    args = parser.parse_args()
    if not 0 <= args.learner_slot < (1 << 32):
        parser.error("learner slot must fit the native uint32 policy contract")
    if not (
        len(args.source_run_sha256) == 64 and
        all(ch in "0123456789abcdef" for ch in args.source_run_sha256)
    ):
        parser.error("Hub source run requires a pinned SHA-256 digest")
    result = publish(
        args.source_run, args.completed_round, args.player_save_root,
        args.game_id, args.learner_slot, args.source_run_sha256,
    )
    print(json.dumps(result, sort_keys=True), flush=True)


if __name__ == "__main__":
    main()
