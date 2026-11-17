"""Opt-in handoff from native training receipts into the player game gallery.

Each training job publishes exactly one small registration file. Unlike a
shared JSON catalog this has no read/modify/write race across independent
Hub and local learning jobs. Games are still played by the real native Level.
"""

import json
from pathlib import Path

from native_training_artifacts import atomic_json


def register_playable(save_root: Path, game_id: str, title: str, receipt: Path):
    if (not isinstance(game_id, str) or not 1 <= len(game_id) <= 40 or
            any(c not in "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_" for c in game_id)):
        raise ValueError("native game ID requires 1..40 ASCII letters, numbers, hyphen or underscore")
    if (not isinstance(title, str) or not title.strip() or len(title) > 120 or
            any(ord(c) < 32 for c in title)):
        raise ValueError("native game title requires 1..120 printable characters")
    if not Path(save_root).is_dir():
        raise ValueError("native player save root must already exist")
    receipt = Path(receipt).resolve(strict=True)
    data = json.loads(receipt.read_text(encoding="utf-8"))
    if (data.get("schema_version") != 1 or
            data.get("kind") not in ("native_training_latest_playable",
                                     "native_training_best_evaluated") or
            not isinstance(data.get("completed_rounds"), int) or
            data["completed_rounds"] < 0):
        raise ValueError("only a completed native playable checkpoint receipt can be registered")

    # Only the game's private registration directory is mutated. Training
    # jobs never share a writable aggregate manifest or copy model weights.
    directory = Path(save_root).resolve() / "trained-models.d"
    directory.mkdir(exist_ok=True)
    path = directory / (game_id + ".json")
    value = {"schema_version": 1, "id": game_id, "title": title,
             "receipt": str(receipt)}
    if path.exists():
        previous = json.loads(path.read_text(encoding="utf-8"))
        if previous != value:
            raise FileExistsError(f"native game ID already registered with different content: {game_id}")
    else:
        atomic_json(path, value)
    return path
