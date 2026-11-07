#!/usr/bin/env python3
"""Evaluate a trusted TinyBrains checkpoint against Nano and publish a Hub receipt."""

import argparse
import hashlib
import json
import re
import shutil
import sys
import tempfile
from pathlib import Path

from experiment_hub_eval_io import SHA256, SUITE_ID, UUID, publish, read_bounded, suite_digest
from experiment_hub_tinybrains_runtime import configure_engine

MAP = re.compile(r"[a-zA-Z0-9_-]{1,80}")


def read_suite(file):
    suite = json.loads(read_bounded(file, 8192))
    if not isinstance(suite, dict) or set(suite) != {"suite_id", "maps", "seeds", "max_turns"}:
        raise ValueError("Expected suite_id, maps, seeds, and max_turns.")
    maps, seeds, turns = suite["maps"], suite["seeds"], suite["max_turns"]
    if (
        not isinstance(suite["suite_id"], str) or not SUITE_ID.fullmatch(suite["suite_id"])
        or not isinstance(maps, list) or not 1 <= len(maps) <= 16
        or any(not isinstance(board, str) or not MAP.fullmatch(board) for board in maps)
        or len(set(maps)) != len(maps)
        or not isinstance(seeds, list) or not 1 <= len(seeds) <= 128
        or any(type(seed) is not int or not 0 <= seed < 2**32 for seed in seeds)
        or len(set(seeds)) != len(seeds)
        or len(maps) * len(seeds) * 2 > 256
        or type(turns) is not int or not 1 <= turns <= 1000
    ):
        raise ValueError("Invalid TinyBrains evaluation suite.")
    return suite


def evaluate(checkpoint, digest, suite, work):
    from ants_mapoca.artifacts import export
    from ants_mapoca.runtime import STARTER, configure, dump, run_cli

    configure(threads=1)
    pinned = work / "checkpoint.pt"
    shutil.copyfile(checkpoint, pinned)
    with pinned.open("rb") as stream:
        if hashlib.file_digest(stream, "sha256").hexdigest() != digest:
            raise ValueError("Checkpoint did not match the pinned SHA-256.")
    candidate = work / "candidate"
    export(pinned, candidate)
    nano = STARTER / "models/nano-bc"
    replay_root = work / "replays"
    match_file = work / "match.json"
    wins = trials = 0
    for board in suite["maps"]:
        for seed in suite["seeds"]:
            for seat in (0, 1):
                models = (candidate, nano) if seat == 0 else (nano, candidate)
                match = f"heldout-{trials}"
                dump(match_file, {
                    "game": "ants", "vars": {"max_turns": suite["max_turns"]},
                    "rows": [{
                        "id": match, "seed": seed, "map": board, "seat_count": 2,
                        "seats": [{"seat": index, "weights": str(model / "model.onnx"),
                                   "manifest": str(model / "manifest.json"),
                                   "label": "candidate" if index == seat else "nano"}
                                  for index, model in enumerate(models)],
                    }],
                })
                transcript = work / "match-output.txt"
                run_cli([match_file, "--out", replay_root, "--verbose"], timeout=180, log=transcript)
                output = transcript.read_text(encoding="utf-8").lower()
                if any(term in output for term in ("strike", "forfeit", "timed out", "inference error")):
                    raise ValueError("TinyBrains match reported an execution error.")
                replay = json.loads(read_bounded(replay_root / f"{match}.json", 32 * 1024 * 1024))
                ranks = replay["engine_ranks"]
                if not isinstance(ranks, list) or len(ranks) != 2 or any(type(rank) is not int for rank in ranks):
                    raise ValueError("TinyBrains replay has invalid engine ranks.")
                wins += ranks[seat] < ranks[1 - seat]
                trials += 1
    return wins, trials


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for option in ("checkpoint", "suite", "scratch", "receipt-root"):
        parser.add_argument("--" + option, required=True, type=Path)
    parser.add_argument("--dispatch-id", required=True)
    parser.add_argument("--checkpoint-sha256", required=True)
    parser.add_argument("--engine-path", type=Path)
    args = parser.parse_args()
    try:
        configure_engine(args.engine_path)
        if not UUID.fullmatch(args.dispatch_id) or not SHA256.fullmatch(args.checkpoint_sha256):
            raise ValueError("Expected a Hub dispatch UUID and checkpoint SHA-256.")
        checkpoint = args.checkpoint.resolve(strict=True)
        scratch = args.scratch.resolve(strict=True)
        receipts = args.receipt_root.resolve(strict=True)
        if not checkpoint.is_file() or not scratch.is_dir() or not receipts.is_dir():
            raise ValueError("Expected a checkpoint file and existing evaluator directories.")
        if any(directory.is_relative_to(checkpoint.parent) for directory in (scratch, receipts)):
            raise ValueError("Use evaluator directories outside the checkpoint directory.")
        suite = read_suite(args.suite)
        with tempfile.TemporaryDirectory(prefix="tinybrains-eval-", dir=scratch) as work:
            wins, trials = evaluate(checkpoint, args.checkpoint_sha256, suite, Path(work))
        result = {
            "schema_version": 1, "suite_id": suite["suite_id"], "suite_sha256": suite_digest(suite),
            "dispatch_id": args.dispatch_id, "checkpoint_sha256": args.checkpoint_sha256,
            "trials": trials, "successes": wins,
        }
        print(publish(receipts, result))
        print(f"TinyBrains held-out wins: {wins}/{trials}")
        return 0
    except (OSError, ValueError, RuntimeError, KeyError, TypeError) as exc:
        print(f"TinyBrains evaluation failed: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
