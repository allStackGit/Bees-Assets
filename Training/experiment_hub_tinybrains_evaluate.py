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


# New mixed suites never accept browser-provided paths. Opponent directories
# are derived from the approved private suite root and immutable identities.
MIXED_SUITE = re.compile(r"tinybrains_mix_[a-f0-9]{20}_v[1-9][0-9]*")
FROZEN_OPPONENT = re.compile(r"tinybrains_mini_[a-f0-9]{20}")
MIXED_MAX_GAMES = 2048


def mixed_model_dir(suite_root, identity):
    from ants_mapoca.runtime import STARTER
    if identity in ("nano", "micro"):
        return STARTER / f"models/{identity}-bc"
    return suite_root / "opponents" / identity


def mixed_pins_ok(pool, suite_root):
    if not isinstance(pool, list) or not 1 <= len(pool) <= 16:
        raise ValueError("Mixed suite must have 1–16 approved opponents.")
    names = set()
    for item in pool:
        if (not isinstance(item, dict)
                or set(item) != {"id", "label", "model_sha256",
                                 "manifest_sha256", "checkpoint_sha256", "model_dir"}
                or not isinstance(item["id"], str)
                or not (item["id"] in ("nano", "micro", "tinybrains_mini_batch128_v1")
                        or FROZEN_OPPONENT.fullmatch(item["id"]))
                or not isinstance(item["label"], str)
                or not 1 <= len(item["label"]) <= 80
                or any(ord(ch) < 32 for ch in item["label"])
                or any(not SHA256.fullmatch(str(item[key])) for key in
                       ("model_sha256", "manifest_sha256"))
                or (item["id"] in ("nano", "micro")
                    and item["checkpoint_sha256"] is not None)
                or (item["id"] not in ("nano", "micro")
                    and not SHA256.fullmatch(str(item["checkpoint_sha256"])))):
            raise ValueError("Invalid frozen mixed-opponent identity.")
        if item["id"] in names:
            raise ValueError("Duplicate mixed-opponent entry.")
        names.add(item["id"])
        expected = mixed_model_dir(suite_root, item["id"]) if suite_root is not None else None
        if (not isinstance(item["model_dir"], str)
                or expected is not None and item["model_dir"] != str(expected)):
            raise ValueError("Mixed-opponent model path differs from the frozen suite root.")
        model_dir = Path(item["model_dir"])
        if (model_dir.resolve(strict=True) != model_dir or
                not model_dir.is_dir() or model_dir.is_symlink()):
            raise ValueError("Mixed-opponent directory is unavailable or redirected.")
        for name, key in (("model.onnx", "model_sha256"),
                          ("manifest.json", "manifest_sha256")):
            file = model_dir / name
            if file.is_symlink() or not file.is_file():
                raise ValueError("Frozen mixed-opponent model file is redirected.")
            with file.open("rb") as stream:
                if hashlib.file_digest(stream, "sha256").hexdigest() != item[key]:
                    raise ValueError("Frozen mixed-opponent model fingerprint changed.")


def validate_mixed_suite(file, suite):
    required = {"suite_id", "maps", "seeds", "max_turns",
                "opponents", "rotations", "schema_version"}
    optional = {"runtime_sha256", "environment_source", "environment_parameters"}
    if (set(suite) - required - optional or not required <= set(suite)
            or suite["schema_version"] != 2
            or not isinstance(suite["suite_id"], str)
            or not MIXED_SUITE.fullmatch(suite["suite_id"])):
        raise ValueError("Invalid immutable mixed evaluation definition.")
    maps, seeds = suite["maps"], suite["seeds"]
    if (not isinstance(maps, list) or not 1 <= len(maps) <= 128
            or any(not isinstance(m, str) or not MAP.fullmatch(m) for m in maps)
            or len(maps) != len(set(maps))
            or not isinstance(seeds, list) or not 1 <= len(seeds) <= 16
            or any(type(v) is not int or not 0 <= v < 2**32 for v in seeds)
            or len(seeds) != len(set(seeds))
            or type(suite["max_turns"]) is not int
            or not 1 <= suite["max_turns"] <= 1000
            or type(suite["rotations"]) is not int
            or not 1 <= suite["rotations"] <= 8):
        raise ValueError("Invalid mixed evaluation map, seed or turn controls.")
    if (suite.get("runtime_sha256") is not None and
            not SHA256.fullmatch(str(suite["runtime_sha256"]))):
        raise ValueError("Invalid mixed suite runtime pin.")
    if "environment_source" in suite:
        source = suite["environment_source"]
        if (not isinstance(source, dict)
                or set(source) != {"configuration_id", "revision", "sha256", "stage"}
                or not UUID.fullmatch(str(source["configuration_id"]))
                or type(source["revision"]) is not int or source["revision"] < 1
                or not SHA256.fullmatch(str(source["sha256"]))
                or source["stage"] not in ("basic_tiny", "tiny_two_player",
                                           "small_two_player", "all_two_player",
                                           "full_multiplayer")):
            raise ValueError("Invalid evaluation environment source identity.")
    if "environment_parameters" in suite:
        # Provenance of the selected Design's effective native environment.
        # Only maps and max_turns affect the match CLI. The remaining keys
        # (including timeout_seconds) are engine transport / training
        # scheduling settings; keeping their values must not silently alter
        # the frozen referee's match contract.
        parameters = suite["environment_parameters"]
        if (not isinstance(parameters, dict) or len(parameters) > 32
                or any(not isinstance(key, str)
                       or not re.fullmatch(r"[a-z][a-z0-9_]{0,63}", key)
                       or isinstance(value, (list, dict))
                       or type(value) not in (bool, int, float, str, type(None))
                       or isinstance(value, (int, float))
                       and (not -1_000_000_000 <= value <= 1_000_000_000)
                       or isinstance(value, str) and len(value) > 128
                       for key, value in parameters.items())):
            raise ValueError("Invalid frozen TinyBrains source environment.")
    games = sum(board_seats(board) for board in maps) * len(seeds) * suite["rotations"]
    if games > MIXED_MAX_GAMES:
        raise ValueError("Mixed suite exceeds 2048 games; reduce seeds or rotations.")
    mixed_pins_ok(suite["opponents"], Path(file).parent)
    return suite


def mixed_roster(pool, map_index, seed_index, seats, rotation, rotations_per_seed):
    # Keep the opponent lineup constant across every candidate seat.
    # Successive (seed, rotation) pairs enumerate distinct pool offsets,
    # rather than repeating an offset when seed and rotation both increase.
    # With seeds * rotations >= pool size every opponent visits every
    # opposing seat on each map at least once.
    start = (map_index + seed_index * rotations_per_seed + rotation) % len(pool)
    return [pool[(start + offset) % len(pool)] for offset in range(seats - 1)]


def suite_trials(suite):
    return (sum(board_seats(board) for board in suite["maps"]) * len(suite["seeds"])
            * suite.get("rotations", 1))


def read_suite(file):
    suite = json.loads(read_bounded(file, 32768))
    if isinstance(suite, dict) and "opponents" in suite:
        return validate_mixed_suite(file, suite)
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
                Path(file).parent / "opponents" /
                ("tinybrains_mini_batch128_v1" if "_season_" in suite["suite_id"]
                 else suite["suite_id"]))
            or not UUID.fullmatch(str(suite["opponent_source_job"]))
            or not SHA256.fullmatch(str(suite["opponent_checkpoint_sha256"]))
            or not (re.fullmatch(
                r"tinybrains_mini_batch128_(?:season_)?v[1-9][0-9]*",
                suite["suite_id"]) or re.fullmatch(
                r"tinybrains_mini_[a-f0-9]{20}", suite["suite_id"]))
            or (re.fullmatch(r"tinybrains_mini_[a-f0-9]{20}", suite["suite_id"])
                and suite["suite_id"] != "tinybrains_mini_" +
                    str(suite["opponent_source_job"]).replace("-", "")[:20])))
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
    mixed = "opponents" in suite
    opponent = suite.get("opponent", "mixed" if mixed else "nano")
    if mixed:
        frozen = {item["id"]: Path(item["model_dir"])
                  for item in suite["opponents"]}
    if not mixed and opponent == "mini":
        fixed = Path(suite["opponent_dir"])
        if (fixed.resolve(strict=True) != fixed or not fixed.is_dir()
                or any((fixed / name).is_symlink()
                       for name in ("model.onnx", "manifest.json"))):
            raise ValueError("Frozen Mini opponent directory is redirected or missing.")
    elif not mixed:
        fixed = STARTER / f"models/{opponent}-bc"
    if mixed:
        mixed_pins_ok(suite["opponents"], None)
    else:
        verify_opponent(suite, fixed)
    replay_root = work / "replays"
    match_file = work / "match.json"
    wins = trials = 0
    for map_index, board in enumerate(suite["maps"]):
        seats = board_seats(board)
        for seed_index, seed in enumerate(suite["seeds"]):
            # Every candidate seat is evaluated once per configured rotation.
            for scheduled in range(seats * suite.get("rotations", 1)):
                rotation, seat = divmod(scheduled, seats)
                roster = (mixed_roster(suite["opponents"], map_index, seed_index,
                                       seats, rotation, suite["rotations"])
                          if mixed else [])
                assigned = iter(roster)
                seats_opponents = ([None if index == seat else next(assigned)
                                    for index in range(seats)] if mixed else [])
                models = ([candidate if index == seat else frozen[seats_opponents[index]["id"]]
                           for index in range(seats)] if mixed else
                          [candidate if index == seat else fixed
                           for index in range(seats)])
                match = f"heldout-{trials}"
                dump(match_file, {
                    "game": "ants", "vars": {"max_turns": suite["max_turns"]},
                    "rows": [{
                        "id": match, "seed": seed, "map": board, "seat_count": seats,
                        "seats": [{"seat": index, "weights": str(model / "model.onnx"),
                                   "manifest": str(model / "manifest.json"),
                                   "label": "candidate" if index == seat else (
                                       seats_opponents[index]["id"] if mixed else opponent)}
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
                               "placement": 1 + sum(rank < ranks[seat] for rank in ranks),
                               "opponent": opponent, "outcome": outcome,
                               "opponent_roster": [row["id"] for row in roster] if mixed else [opponent] * (seats - 1),
                               "ranks": ranks, "scores": replay.get("scores"),
                               "turns": replay.get("turns")})
    if mixed:
        mixed_pins_ok(suite["opponents"], None)
    else:
        verify_opponent(suite, fixed)
    return wins, trials


def evaluate_with_metrics(checkpoint, digest, suite, work, *,
                          current_runtime=False, on_breakdown=None):
    """Evaluate a frozen checkpoint and reconcile all independent outcomes."""
    counts = {"win": 0, "loss": 0, "draw": 0}
    by_players = {2: {"win": 0, "loss": 0, "draw": 0},
                  4: {"win": 0, "loss": 0, "draw": 0},
                  6: {"win": 0, "loss": 0, "draw": 0}}
    placements = {2: 0, 4: 0, 6: 0}
    top_half = {2: 0, 4: 0, 6: 0}
    by_map = {board: {"win": 0, "loss": 0, "draw": 0,
                      "placement_sum": 0, "top_half": 0, "trials": 0}
              for board in suite["maps"]}
    # Keep conditional game outcomes and direct pairwise placement separate.
    # One six-player game contains five correlated pairwise comparisons,
    # never five statistically independent matches.
    by_opponent = {item["id"]: {"games": 0, "seats": 0,
        "win": 0, "loss": 0, "draw": 0,
        "pair_win": 0, "pair_loss": 0, "pair_draw": 0}
        for item in suite.get("opponents", [])}

    def observe(result):
        outcome = result.get("outcome")
        if outcome not in counts:
            raise ValueError("Invalid TinyBrains evaluation outcome.")
        counts[outcome] += 1
        players = result["player_count"]
        placement = result["placement"]
        if players not in by_players or type(placement) is not int or not 1 <= placement <= players:
            raise ValueError("Invalid native multiplayer placement in evaluator replay.")
        by_players[players][outcome] += 1
        placements[players] += placement
        top_half[players] += int(placement <= players // 2)
        map_id = result["map"]
        if map_id not in by_map:
            raise ValueError("Evaluator reported a board outside the pinned suite.")
        row = by_map[map_id]
        row[outcome] += 1
        row["placement_sum"] += placement
        row["top_half"] += int(placement <= players // 2)
        row["trials"] += 1
        if by_opponent:
            roster = result["opponent_roster"]
            ranks = result["ranks"]
            candidate_seat = result["candidate_seat"]
            other_seats = [seat for seat in range(players) if seat != candidate_seat]
            if (len(roster) != players - 1 or
                    len(ranks) != players or
                    type(candidate_seat) is not int or
                    not 0 <= candidate_seat < players or
                    any(opponent not in by_opponent for opponent in roster)):
                raise ValueError("Mixed evaluation seat/roster coverage differs from match.")
            for opponent in set(roster):
                entry = by_opponent[opponent]
                entry["games"] += 1
                entry["seats"] += roster.count(opponent)
                entry[outcome] += 1
            for seat, opponent in zip(other_seats, roster):
                key = ("pair_win" if ranks[candidate_seat] < ranks[seat]
                       else "pair_loss" if ranks[candidate_seat] > ranks[seat]
                       else "pair_draw")
                by_opponent[opponent][key] += 1

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
            metrics[f"mean_placement_{players}p"] = {
                "value": placements[players] / total, "unit": "scalar"}
            metrics[f"top_half_rate_{players}p"] = {
                "value": top_half[players] / total, "unit": "ratio"}
    metrics["mean_placement"] = {
        "value": sum(placements.values()) / trials, "unit": "scalar"}
    metrics["top_half_rate"] = {
        "value": sum(top_half.values()) / trials, "unit": "ratio"}
    if on_breakdown is not None:
        # Per-board observations belong in a separate bounded, immutable
        # checkpoint-identified detail file. The coordinator's fixed-size
        # scoring receipt remains project-neutral and unchanged.
        if sum(row["trials"] for row in by_map.values()) != trials:
            raise ValueError("Per-map evaluation coverage is incomplete.")
        on_breakdown({"trials": trials, "successes": wins,
                      "maps": [{"map": board, "players": board_seats(board),
                                **by_map[board]} for board in suite["maps"]],
                      "opponents": [{"id": key, **counts}
                                    for key, counts in by_opponent.items()]})
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
