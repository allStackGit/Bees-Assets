#!/usr/bin/env python3
"""Evaluate one running TinyBrains dispatch on a separately scheduled CPU worker."""

import argparse
import http.client
import json
import re
import sys
from pathlib import Path

from experiment_hub_eval_io import read_bounded
from experiment_hub_eval_poll import final_checkpoint, poll_checkpoint, poll_batch
from experiment_hub_tinybrains_evaluate import SUITE_ID, UUID, evaluate_with_metrics, read_suite
from experiment_hub_tinybrains_runtime import configure_engine

CHECKPOINT = re.compile(r"update-([0-9]{6,8})\.pt")


def checkpoint_update(file):
    # The trusted persistent trainer reads this same checkpoint format
    # during restart. The evaluator only inspects its update number;
    # evaluation itself still uses a separate immutable private copy.
    import torch
    state = torch.load(file, map_location="cpu", weights_only=False)
    update = state.get("update") if isinstance(state, dict) else None
    if type(update) is not int or not 1 <= update <= 2**53 - 1:
        raise ValueError("TinyBrains checkpoint has no valid PPO update identity.")
    return update


def latest_numbered_checkpoint(artifact_root, job):
    identity = job["job_id"]
    # A continuation writes new checkpoints in its own job directory.
    if not UUID.fullmatch(identity) or not UUID.fullmatch(job["artifact_lineage_root_id"]):
        raise ValueError("Invalid TinyBrains artifact lineage.")
    directory = artifact_root / identity
    if directory.resolve() != directory:
        raise ValueError("TinyBrains artifact directory is redirected.")
    if not directory.is_dir():
        return None
    # The installed persistent TinyBrains trainer owns self-play/latest.pt,
    # not job/latest.pt or job/update-N.pt. Its checkpoint includes an
    # absolute PPO update; normalize to the Hub job's relative update axis.
    active = directory / "self-play" / "latest.pt"
    if active.exists():
        start_file = directory / "hub-start-update.json"
        if (active.resolve(strict=True) != active or not active.is_file() or
            start_file.resolve(strict=True) != start_file or
            not start_file.is_file()):
            raise ValueError("TinyBrains persistent checkpoint identity is redirected.")
        start = json.loads(read_bounded(start_file, 4096))["update"]
        if type(start) is not int or not 1 <= start <= 2**53 - 1:
            raise ValueError("TinyBrains imported starting update is invalid.")
        update = checkpoint_update(active)
        if update < start:
            raise ValueError("TinyBrains checkpoint predates the verified starting update.")
        return update, active, update - start
    latest = None
    for file in directory.iterdir():
        match = CHECKPOINT.fullmatch(file.name)
        if match and (update := int(match[1])) > 0 and (latest is None or update > latest[0]):
            latest = update, file
    if latest and (latest[1].resolve(strict=True) != latest[1] or not latest[1].is_file()):
        raise ValueError("TinyBrains checkpoint is redirected or unavailable.")
    return latest


def poll(args, suites):
    def verify_frozen(file, checkpoint):
        if len(checkpoint) == 3 and checkpoint_update(file) != checkpoint[0]:
            raise ValueError("TinyBrains checkpoint advanced during private copy; retry a stable snapshot.")

    def evaluated(job, file, digest, work, suite):
        return evaluate_with_metrics(file, digest, suite, work)

    def find_checkpoint(job):
        if "final_checkpoint_sha256" in job:
            return final_checkpoint(args.artifact_root, job, "self-play/latest.pt")
        return latest_numbered_checkpoint(args.artifact_root, job)
    pairs = [(suite, {**suite, "trials":
                      2 * len(suite["maps"]) * len(suite["seeds"])}) for suite in suites]
    if len(pairs) == 1:
        poll_checkpoint(args, "tinybrains", *pairs[0], find_checkpoint,
                        lambda job, file, digest, work:
                            evaluated(job, file, digest, work, suites[0]),
                        verify_checkpoint=verify_frozen)
    else:
        poll_batch(args, "tinybrains", pairs, find_checkpoint, evaluated,
                   verify_checkpoint=verify_frozen)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--machine", required=True)
    parser.add_argument("--port", type=int, default=7148)
    parser.add_argument("--engine-path", type=Path)
    parser.add_argument("--suite", required=True, type=Path, action="append",
                        help="Repeat 2–8 times for one same-checkpoint evaluation batch.")
    for option in ("artifact-root", "scratch", "receipt-root"):
        parser.add_argument("--" + option, required=True, type=Path)
    args = parser.parse_args()
    try:
        configure_engine(args.engine_path)
        if not SUITE_ID.fullmatch(args.machine) or not 1 <= args.port <= 65535:
            raise ValueError("Invalid machine ID or loopback port.")
        args.artifact_root = args.artifact_root.resolve(strict=True)
        if not args.artifact_root.is_dir():
            raise ValueError("Expected the worker's artifact directory.")
        for key in ("scratch", "receipt_root"):
            directory = getattr(args, key).resolve(strict=True)
            if not directory.is_dir() or directory.is_relative_to(args.artifact_root):
                raise ValueError("Evaluator directories must be outside training artifacts.")
            setattr(args, key, directory)
        poll(args, [read_suite(source) for source in args.suite])
        return 0
    except (OSError, RuntimeError, ValueError, TypeError, KeyError, http.client.HTTPException) as exc:
        print(f"Scheduled TinyBrains evaluation failed: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
