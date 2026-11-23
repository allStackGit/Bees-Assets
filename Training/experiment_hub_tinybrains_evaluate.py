#!/usr/bin/env python3
"""Evaluate trusted TinyBrains checkpoints against immutable benchmark opponents."""

import argparse
import hashlib
import json
import re
import shutil
import sys
import tempfile
from pathlib import Path

from experiment_hub_eval_io import SHA256, SUITE_ID, UUID, publish, read_bounded, suite_digest
from experiment_hub_evaluation_runtime import pinned_runtime
from experiment_hub_tinybrains_runtime import configure_engine

MAP = re.compile(r"[a-zA-Z0-9_-]{1,80}")


def board_seats(board):
    # The trusted installed cartridge, not a filename suffix or a browser
    # supplied player count, defines the number of independent seats.
    from ants_mapoca.season import board_path

    definition = json.loads(read_bounded(board_path(board), 1024 * 1024))
    seats = definition.get("players")
    if definition.get("id") != board or type(seats) is not int or seats not in (2, 4, 6):
        raise ValueError(f"Invalid approved TinyBrains board identity/seats: {board}")
    return seats


def suite_trials(suite):
    return sum(board_seats(board) for board in suite["maps"]) * len(suite["seeds"])


def read_suite(file):
    suite = json.loads(read_bounded(file, 8192))
    required = {"suite_id", "maps", "seeds", "max_turns"}
    optional = {"runtime_sha256", "opponent", "opponent_model_sha256",
                "opponent_manifest_sha256", "opponent_dir",
                "opponent_checkpoint_sha256", "opponent_source_job"}
    if not isinstance(suite, dict) or not required <= suite.keys() or set(suite) - required - optional:
        raise ValueError("Expected suite_id, maps, seeds, max_turns, and optional pinned opponent/runtime.")
    maps, seeds, turns = suite["maps"], suite["seeds"], suite["max_turns"]
    opponent = suite.get("opponent", "nano")
    pins = ("opponent_model_sha256", "opponent_manifest_sha256")
    if (
        not isinstance(suite["suite_id"], str) or not SUITE_ID.fullmatch(suite["suite_id"])
        or not isinstance(maps, list) or not 1 <= len(maps) <= 128
        or any(not isinstance(board, str) or not MAP.fullmatch(board) for board in maps)
        or len(set(maps)) != len(maps)
        or not isinstance(seeds, list) or not 1 <= len(seeds) <= 128
        or any(type(seed) is not int or not 0 <= seed < 2**32 for seed in seeds)
        or len(set(seeds)) != len(seeds)
        or len(maps) * len(seeds) * 6 > 3072
        or type(turns) is not int or not 1 <= turns <= 1000
        or opponent not in ("nano", "micro", "mini")
        or (opponent in ("micro", "mini") and not all(key in suite for key in pins))
        or (opponent == "mini" and (
            not all(key in suite for key in (
                "opponent_dir", "opponent_checkpoint_sha256", "opponent_source_job"))
            or not isinstance(suite["opponent_dir"], str)
            or suite["opponent_dir"] != str(
                Path(file).parent / "opponents" / suite["suite_id"])
            or not UUID.fullmatch(str(suite["opponent_source_job"]))
            or not SHA256.fullmatch(str(suite["opponent_checkpoint_sha256"]))
            or not re.fullmatch(r"tinybrains_mini_batch128_(?:season_)?v[1-9][0-9]*",
                                suite["suite_id"])))
        or (opponent != "mini" and any(key in suite for key in (
            "opponent_dir", "opponent_checkpoint_sha256", "opponent_source_job")))
        or (any(key in suite for key in pins) and not all(key in suite for key in pins))
        or any(key in suite and (not isinstance(suite[key], str)
            or not SHA256.fullmatch(suite[key]))
            for key in (*pins, "runtime_sha256"))
    ):
        raise ValueError("Invalid TinyBrains evaluation suite.")
    if suite_trials(suite) > 1024:
        raise ValueError("A TinyBrains all-map suite may run at most 1024 matches.")
    return suite


def verify_opponent(suite, directory):
    # Pin both submitted opponent files, not just an opponent name. Legacy
    # Nano suites retain their original runtime contract.
    for filename, key in (("model.onnx", "opponent_model_sha256"),
                          ("manifest.json", "opponent_manifest_sha256")):
        expected = suite.get(key)
        if expected is None:
            continue
        with (directory / filename).open("rb") as stream:
            actual = hashlib.file_digest(stream, "sha256").hexdigest()
        if actual != expected:
            raise ValueError(f"TinyBrains evaluation {filename} differs from the suite pin.")


def evaluate(checkpoint, digest, suite, work, *, on_result=None):
    with pinned_runtime("tinybrains", suite):
        return _evaluate(checkpoint, digest, suite, work, on_result=on_result)


def _evaluate(checkpoint, digest, suite, work, *, on_result=None):
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
    opponent = suite.get("opponent", "nano")
    if opponent == "mini":
        fixed = Path(suite["opponent_dir"])
        if (fixed.resolve(strict=True) != fixed or not fixed.is_dir()
                or any((fixed / name).is_symlink()
                       for name in ("model.onnx", "manifest.json"))):
            raise ValueError("Frozen Mini opponent directory is redirected or missing.")
    else:
        fixed = STARTER / f"models/{opponent}-bc"
    verify_opponent(suite, fixed)
    replay_root = work / "replays"
    match_file = work / "match.json"
    wins = trials = 0
    for board in suite["maps"]:
        seats = board_seats(board)
        for seed in suite["seeds"]:
            # One independent trial for every possible candidate seat. All
            # other seats use separate instances of the fixed opponent.
            for seat in range(seats):
                models = [candidate if index == seat else fixed
                          for index in range(seats)]
                match = f"heldout-{trials}"
                dump(match_file, {
                    "game": "ants", "vars": {"max_turns": suite["max_turns"]},
                    "rows": [{
                        "id": match, "seed": seed, "map": board, "seat_count": seats,
                        "seats": [{"seat": index, "weights": str(model / "model.onnx"),
                                   "manifest": str(model / "manifest.json"),
                                   "label": "candidate" if index == seat else opponent}
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
                if not isinstance(ranks, list) or len(ranks) != seats or any(
                        type(rank) is not int for rank in ranks):
                    raise ValueError("TinyBrains replay has invalid engine ranks.")
                best = min(ranks)
                outcome = ("win" if ranks[seat] == best and ranks.count(best) == 1
                           else "draw" if ranks[seat] == best else "loss")
                wins += outcome == "win"
                trials += 1
                if on_result is not None:
                    on_result({"map": board, "seed": seed, "player_count": seats,
                               "candidate_seat": seat,
                               "opponent": opponent, "outcome": outcome,
                               "ranks": ranks, "scores": replay.get("scores"),
                               "turns": replay.get("turns")})
    verify_opponent(suite, fixed)
    return wins, trials


def evaluate_with_metrics(checkpoint, digest, suite, work, *, current_runtime=False):
    """Evaluate a frozen checkpoint and reconcile all independent outcomes."""
    counts = {"win": 0, "loss": 0, "draw": 0}
    by_players = {2: {"win": 0, "loss": 0, "draw": 0},
                  4: {"win": 0, "loss": 0, "draw": 0},
                  6: {"win": 0, "loss": 0, "draw": 0}}

    def observe(result):
        outcome = result.get("outcome")
        if outcome not in counts:
            raise ValueError("Invalid TinyBrains evaluation outcome.")
        counts[outcome] += 1
        by_players[result["player_count"]][outcome] += 1

    with pinned_runtime("tinybrains", suite, use_current=current_runtime) as runtime_sha256:
        wins, trials = _evaluate(checkpoint, digest, suite, work, on_result=observe)
    expected = suite_trials(suite)
    if trials != expected or wins != counts["win"] or sum(counts.values()) != trials:
        raise ValueError("Unexpected TinyBrains independent match trial counts.")
    metrics = {
        "win_count": {"value": counts["win"], "unit": "count"},
        "loss_count": {"value": counts["loss"], "unit": "count"},
        "draw_count": {"value": counts["draw"], "unit": "count"},
        "score_rate": {"value": (wins + counts["draw"] / 2) / trials, "unit": "ratio"},
    }
    for players, outcomes in by_players.items():
        total = sum(outcomes.values())
        if total:
            metrics[f"score_rate_{players}p"] = {
                "value": (outcomes["win"] + outcomes["draw"] / 2) / total,
                "unit": "ratio",
            }
            metrics[f"trials_{players}p"] = {"value": total, "unit": "count"}
    return {"successes": wins, "metrics": metrics,
            **({"runtime_sha256": runtime_sha256} if current_runtime else {})}


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
            outcome = evaluate_with_metrics(checkpoint, args.checkpoint_sha256, suite, Path(work))
        trials = suite_trials(suite)
        result = {
            "schema_version": 1, "suite_id": suite["suite_id"], "suite_sha256": suite_digest(suite),
            "dispatch_id": args.dispatch_id, "checkpoint_sha256": args.checkpoint_sha256,
            "trials": trials, **outcome,
        }
        print(publish(receipts, result))
        print(f"TinyBrains vs {suite.get('opponent', 'nano')} held-out wins: {outcome['successes']}/{trials}")
        return 0
    except (OSError, ValueError, RuntimeError, KeyError, TypeError) as exc:
        print(f"TinyBrains evaluation failed: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
