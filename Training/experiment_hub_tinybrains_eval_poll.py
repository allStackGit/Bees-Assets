#!/usr/bin/env python3
"""Evaluate one running TinyBrains dispatch on a separately scheduled CPU worker."""

import argparse
import hashlib
import http.client
import json
import re
import sys
import tempfile
from pathlib import Path

from experiment_hub_eval_poll import exclusive, flush_receipt, passed_receipt, retain_dispatch_cursors, save_cursor, targets
from experiment_hub_tinybrains_evaluate import SUITE_ID, UUID, evaluate, read_bounded, read_suite

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
    suite_id = suite["suite_id"]
    receipt_suite = {**suite, "trials": 2 * len(suite["maps"]) * len(suite["seeds"])}
    state = args.scratch / f"tinybrains-evaluation-{args.machine}.{suite_id}.json"
    digest = hashlib.sha256(json.dumps(suite, sort_keys=True).encode()).hexdigest()

    def current_targets():
        return targets(args.port, args.machine, suite_id, "tinybrains")

    with exclusive(state.with_suffix(".lock")):
        try:
            prior = json.loads(read_bounded(state, 131072))
            if prior["suite_sha256"] != digest:
                raise ValueError("Suite definition changed; use a new suite ID.")
        except FileNotFoundError:
            prior = {"suite_sha256": digest, "dispatches": {}}
        if (pending := prior.get("pending_receipt")) is not None:
            flush_receipt(args, receipt_suite, pending)
            save_cursor(state, {"suite_sha256": digest, "dispatches": prior["dispatches"]})
            return
        live = current_targets()
        cursor = retain_dispatch_cursors(prior["dispatches"], live)
        saved = {"suite_sha256": digest, "dispatches": cursor}
        # Persist a rotation order across calls; a newer checkpoint on one
        # busy job should not crowd out another eligible dispatch.
        for dispatch in live:
            cursor.setdefault(dispatch, 0)
        for dispatch in list(cursor):
            if dispatch not in live:
                continue
            job = live[dispatch]
            try:
                if not UUID.fullmatch(dispatch) or receipt_suite["trials"] < job["min_trials"]:
                    continue
                receipt = args.receipt_root / f"{dispatch}.{suite_id}.json"
                if passed_receipt(receipt, dispatch, receipt_suite, job):
                    continue
                checkpoint = latest_numbered_checkpoint(args.artifact_root, job)
                if not checkpoint or checkpoint[0] <= cursor[dispatch]:
                    continue
                update, file = checkpoint
                with file.open("rb") as stream:
                    checkpoint_digest = hashlib.file_digest(stream, "sha256").hexdigest()
            except (OSError, ValueError, TypeError, KeyError) as exc:
                print(f"TinyBrains evaluation target {dispatch} skipped: {exc}", file=sys.stderr)
                continue
            cursor.pop(dispatch)
            cursor[dispatch] = update
            # Record a handled failure once, but leave the checkpoint
            # eligible if the evaluator is interrupted before a result.
            try:
                with tempfile.TemporaryDirectory(prefix="tinybrains-eval-", dir=args.scratch) as temp:
                    wins, trials = evaluate(file, checkpoint_digest, suite, Path(temp))
            except (OSError, RuntimeError, ValueError, TypeError, KeyError):
                save_cursor(state, saved)
                raise
            pending = {"target": job, "receipt": {
                "schema_version": 1, "suite_id": suite_id, "dispatch_id": dispatch,
                "checkpoint_sha256": checkpoint_digest, "trials": trials, "successes": wins,
            }}
            save_cursor(state, {**saved, "pending_receipt": pending})
            flush_receipt(args, receipt_suite, pending)
            save_cursor(state, saved)
            return
        if saved != prior:
            save_cursor(state, saved)


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
