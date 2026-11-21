#!/usr/bin/env python3
"""Evaluate one running Courier dispatch from an immutable policy inbox."""

import argparse
import http.client
from pathlib import Path
import re
import sys

from experiment_hub_courier_evaluate import evaluate, read_suite
from experiment_hub_eval_io import SUITE_ID, UUID
from experiment_hub_eval_poll import final_checkpoint, poll_checkpoint, poll_batch

POLICY = re.compile(r"round-([1-9][0-9]{0,14})\.zip")


def latest_policy(root, job):
    dispatch = job["dispatch_id"]
    if not UUID.fullmatch(dispatch) or not UUID.fullmatch(job["job_id"]):
        raise ValueError("Invalid Courier dispatch identity.")
    directory = root / dispatch
    if directory.resolve() != directory:
        raise ValueError("Courier policy inbox is redirected.")
    if not directory.is_dir():
        return None
    latest = None
    for file in directory.iterdir():
        match = POLICY.fullmatch(file.name)
        if match and (latest is None or int(match[1]) > latest[0]):
            latest = int(match[1]), file
    if latest and (latest[1].resolve(strict=True) != latest[1] or not latest[1].is_file()):
        raise ValueError("Courier policy snapshot is redirected or unavailable.")
    return latest


def poll(args, suites):
    def evaluate_checkpoint(job, file, digest, work, suite):
        config = args.artifact_root / job["job_id"] / "hub-environment.json"
        if config.resolve(strict=True) != config or not config.is_file():
            raise ValueError("Courier environment file is redirected or unavailable.")
        return evaluate(args.project_root, file, digest, config, suite, work)

    def find_checkpoint(job):
        if "final_checkpoint_sha256" in job:
            return final_checkpoint(args.artifact_root, job, "lineage-0.zip")
        return latest_policy(args.snapshot_root, job)
    if len(suites) == 1:
        poll_checkpoint(args, "courier", suites[0], suites[0], find_checkpoint,
                        lambda job, file, digest, work:
                            evaluate_checkpoint(job, file, digest, work, suites[0]))
    else:
        poll_batch(args, "courier", [(suite, suite) for suite in suites],
                   find_checkpoint, evaluate_checkpoint)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--machine", required=True)
    parser.add_argument("--port", type=int, default=7148)
    parser.add_argument("--suite", required=True, type=Path, action="append",
                        help="Repeat 2–8 times to evaluate one checkpoint against all required suites.")
    for option in ("project-root", "artifact-root", "snapshot-root", "scratch", "receipt-root"):
        parser.add_argument("--" + option, required=True, type=Path)
    args = parser.parse_args()
    try:
        if not SUITE_ID.fullmatch(args.machine) or not 1 <= args.port <= 65535:
            raise ValueError("Invalid machine ID or loopback port.")
        directories = []
        for key in ("project_root", "artifact_root", "snapshot_root", "scratch", "receipt_root"):
            directory = getattr(args, key).resolve(strict=True)
            if not directory.is_dir() or any(directory.is_relative_to(other) or other.is_relative_to(directory)
                                             for other in directories):
                raise ValueError("Courier project, artifact and evaluator directories must be separate.")
            setattr(args, key, directory)
            directories.append(directory)
        poll(args, [read_suite(source) for source in args.suite])
        return 0
    except (OSError, RuntimeError, ValueError, TypeError, KeyError, http.client.HTTPException) as exc:
        print(f"Scheduled Courier evaluation failed: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
