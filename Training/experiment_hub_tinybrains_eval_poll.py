#!/usr/bin/env python3
"""Evaluate one running TinyBrains dispatch on a separately scheduled CPU worker."""

import argparse
import http.client
import json
import os
import re
import sys
import tempfile
from pathlib import Path

from experiment_hub_eval_io import read_bounded, SHA256, suite_digest
from experiment_hub_eval_poll import (
    final_checkpoint, poll_checkpoint, poll_batch, run_serialized_evaluator)
from experiment_hub_tinybrains_evaluate import SUITE_ID, UUID, evaluate_with_metrics, read_suite, suite_trials
from experiment_hub_tinybrains_runtime import configure_engine

CHECKPOINT = re.compile(r"update-([0-9]{6,8})\.pt")


def checkpoint_update(file):
    # The trusted persistent trainer reads this same checkpoint format
    # during restart. The evaluator only inspects its update number;
    # evaluation itself still uses a separate immutable private copy.
    import torch
    try:
        state = torch.load(file, map_location="cpu", weights_only=False)
    except Exception as error:
        # Training may be replacing the file at the scheduled poll instant.
        # Retry after the next checkpoint without changing the trainer.
        raise ValueError("TinyBrains checkpoint update is not readable yet.") from error
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
    # The standard Windows/Linux adapter instead writes job/latest.pt and
    # records the job's update counter directly. Keep both layouts working.
    standard = directory / "latest.pt"
    if standard.exists():
        if standard.resolve(strict=True) != standard or not standard.is_file():
            raise ValueError("TinyBrains checkpoint is redirected or unavailable.")
        update = checkpoint_update(standard)
        return update, standard, update
    latest = None
    for file in directory.iterdir():
        match = CHECKPOINT.fullmatch(file.name)
        if match and (update := int(match[1])) > 0 and (latest is None or update > latest[0]):
            latest = update, file
    if latest and (latest[1].resolve(strict=True) != latest[1] or not latest[1].is_file()):
        raise ValueError("TinyBrains checkpoint is redirected or unavailable.")
    return latest


def publish_map_breakdown(root, job, checkpoint_digest, suite, details):
    """Atomically retain observation-only per-board outcomes beside the receipt.

    A map report is never scoring, Stop, or checkpoint-promotion authority.
    Readers must compare every identity against the current accepted receipt,
    because monitoring can replace a dispatch's latest checkpoint.
    """
    dispatch = job.get("dispatch_id")
    if (not isinstance(dispatch, str) or not UUID.fullmatch(dispatch)
            or not SHA256.fullmatch(checkpoint_digest)
            or details["trials"] != suite_trials(suite)
            or not isinstance(details.get("maps"), list)
            or len(details["maps"]) != len(suite["maps"])):
        raise ValueError("Incomplete historical TinyBrains per-map identity.")
    report = {
        "schema_version": 1, "job_id": job["job_id"],
        "dispatch_id": dispatch, "suite_id": suite["suite_id"],
        "suite_sha256": suite_digest(suite),
        "checkpoint_sha256": checkpoint_digest,
        "trials": details["trials"], "successes": details["successes"],
        "maps": details["maps"],
    }
    encoded = (json.dumps(report, separators=(",", ":"), allow_nan=False) + "\n").encode()
    if len(encoded) > 16384:
        raise ValueError("TinyBrains per-map evaluation report exceeds 16 KiB.")
    destination = root / f"{dispatch}.{suite['suite_id']}.maps.json"
    fd, pending = tempfile.mkstemp(prefix=".tinybrains-map-results-", dir=root)
    try:
        with os.fdopen(fd, "wb") as stream:
            stream.write(encoded)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(pending, destination)
    finally:
        Path(pending).unlink(missing_ok=True)


def poll(args, suites):
    def verify_frozen(file, checkpoint):
        # Terminal snapshots already have a final-model SHA-256 pin.
        # Running snapshots also need to prove that their update coordinate
        # still matches the independently copied model bytes.
        if len(checkpoint) == 3 and checkpoint[0] != 10**16 and checkpoint_update(file) != checkpoint[0]:
            raise ValueError("TinyBrains checkpoint advanced during private copy; retry a stable snapshot.")

    def evaluated(job, file, digest, work, suite):
        return evaluate_with_metrics(
            file, digest, suite, work, current_runtime=True,
            on_breakdown=lambda details: publish_map_breakdown(
                args.receipt_root, job, digest, suite, details))

    def find_checkpoint(job):
        if "final_checkpoint_sha256" in job:
            source = args.artifact_root / job["job_id"] / "self-play" / "latest.pt"
            filename = "self-play/latest.pt" if source.exists() else "latest.pt"
            final = final_checkpoint(args.artifact_root, job, filename)
            observed = latest_numbered_checkpoint(args.artifact_root, job)
            if observed and len(observed) == 3 and observed[1] == final[1]:
                return final[0], final[1], observed[2]
            # Older trusted adapters may have a numbered-only layout.
            # The coordinator's final_steps_completed remains available.
            return final
        return latest_numbered_checkpoint(args.artifact_root, job)
    def select_checkpoint(job, previous, cadence):
        # Training can write retained update-N.pt checkpoints faster than
        # independent matches complete. Consume the oldest saved checkpoint
        # satisfying this run's update cadence before following latest.pt.
        # The shared poller still checks the durable receipt/archive cursor,
        # and the copied snapshot is verified against its recorded update.
        newest = find_checkpoint(job)
        if newest is None or "final_checkpoint_sha256" in job:
            return newest
        directory = newest[1].parent
        start = newest[0] - newest[2] if len(newest) == 3 else None
        minimum = previous + cadence if previous else 1
        selected = None
        for file in directory.iterdir():
            match = CHECKPOINT.fullmatch(file.name)
            if not match:
                continue
            update = int(match[1])
            if (update < minimum or update > newest[0] or
                selected is not None and update >= selected[0]):
                continue
            # Reject redirected snapshots; only the trainer-owned regular
            # checkpoint is copied into private evaluator scratch.
            if file.resolve(strict=True) != file or not file.is_file():
                raise ValueError("TinyBrains retained checkpoint is redirected or unavailable.")
            if start is not None and update < start:
                continue
            selected = (update, file, update - start) if start is not None else (update, file)
        return selected if selected is not None else newest

    pairs = [(suite, {**suite, "trials":
                      suite_trials(suite)}) for suite in suites]
    if len(pairs) == 1:
        poll_checkpoint(args, "tinybrains", *pairs[0], find_checkpoint,
                        lambda job, file, digest, work:
                            evaluated(job, file, digest, work, suites[0]),
                        verify_checkpoint=verify_frozen,
                        select_checkpoint=select_checkpoint)
    else:
        poll_batch(args, "tinybrains", pairs, find_checkpoint, evaluated,
                   verify_checkpoint=verify_frozen,
                   select_checkpoint=select_checkpoint)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--machine", required=True)
    parser.add_argument("--port", type=int, default=7148)
    parser.add_argument("--hub-host", default="127.0.0.1")
    parser.add_argument("--engine-path", type=Path)
    parser.add_argument("--suite", required=True, type=Path, action="append",
                        help="Repeat 2–8 times for one same-checkpoint evaluation batch.")
    for option in ("artifact-root", "scratch", "receipt-root"):
        parser.add_argument("--" + option, required=True, type=Path)
    args = parser.parse_args()
    try:
        configure_engine(args.engine_path)
        if not SUITE_ID.fullmatch(args.machine) or not 1 <= args.port <= 65535:
            raise ValueError("Invalid machine ID or Hub port.")
        args.artifact_root = args.artifact_root.resolve(strict=True)
        if not args.artifact_root.is_dir():
            raise ValueError("Expected the worker's artifact directory.")
        for key in ("scratch", "receipt_root"):
            directory = getattr(args, key).resolve(strict=True)
            if not directory.is_dir() or directory.is_relative_to(args.artifact_root):
                raise ValueError("Evaluator directories must be outside training artifacts.")
            setattr(args, key, directory)
        suites=[read_suite(source) for source in args.suite]
        if not run_serialized_evaluator(args.receipt_root,lambda:poll(args,suites)):
            print("TinyBrains evaluation deferred: another independent evaluator is active.",
                  file=sys.stderr)
        return 0
    except (OSError, RuntimeError, ValueError, TypeError, KeyError, http.client.HTTPException) as exc:
        print(f"Scheduled TinyBrains evaluation failed: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
