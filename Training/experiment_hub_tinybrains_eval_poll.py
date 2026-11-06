#!/usr/bin/env python3
"""Evaluate one running TinyBrains dispatch on a separately scheduled CPU worker."""

import argparse
import http.client
import re
import sys
from pathlib import Path

from experiment_hub_eval_poll import poll_checkpoint
from experiment_hub_tinybrains_evaluate import SUITE_ID, UUID, evaluate, read_suite

CHECKPOINT = re.compile(r"update-([0-9]{6,8})\.pt")


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
    latest = None
    for file in directory.iterdir():
        match = CHECKPOINT.fullmatch(file.name)
        if match and (update := int(match[1])) > 0 and (latest is None or update > latest[0]):
            latest = update, file
    if latest and (latest[1].resolve(strict=True) != latest[1] or not latest[1].is_file()):
        raise ValueError("TinyBrains checkpoint is redirected or unavailable.")
    return latest


def poll(args, suite):
    trials = 2 * len(suite["maps"]) * len(suite["seeds"])

    def evaluate_checkpoint(job, file, digest, work):
        wins, observed = evaluate(file, digest, suite, work)
        if observed != trials:
            raise ValueError("Unexpected TinyBrains evaluation trial count.")
        return wins

    poll_checkpoint(args, "tinybrains", suite, {**suite, "trials": trials},
                    lambda job: latest_numbered_checkpoint(args.artifact_root, job),
                    evaluate_checkpoint)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--machine", required=True)
    parser.add_argument("--port", type=int, default=7148)
    for option in ("artifact-root", "suite", "scratch", "receipt-root"):
        parser.add_argument("--" + option, required=True, type=Path)
    args = parser.parse_args()
    try:
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
        poll(args, read_suite(args.suite))
        return 0
    except (OSError, RuntimeError, ValueError, TypeError, KeyError, http.client.HTTPException) as exc:
        print(f"Scheduled TinyBrains evaluation failed: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
