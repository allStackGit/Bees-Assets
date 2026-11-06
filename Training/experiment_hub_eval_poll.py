#!/usr/bin/env python3
"""Project-neutral scheduled evaluation coordination and durable cursors."""

from contextlib import contextmanager
import hashlib
import http.client
import json
import os
from pathlib import Path
import sys
import tempfile
from urllib.parse import urlencode

from experiment_hub_eval_io import SHA256, UUID, publish, read_bounded, suite_digest


@contextmanager
def exclusive(path: Path):
    # Kernel locks are released if the scheduled process exits unexpectedly.
    with path.open("a+b") as stream:
        if os.name == "nt":
            import msvcrt

            stream.seek(0)
            msvcrt.locking(stream.fileno(), msvcrt.LK_NBLCK, 1)
        else:
            import fcntl

            fcntl.flock(stream, fcntl.LOCK_EX | fcntl.LOCK_NB)
        yield


def targets(port: int, machine: str, suite: str, project: str) -> dict:
    query = urlencode({"project_id": project, "machine_id": machine, "suite_id": suite})
    connection = http.client.HTTPConnection("127.0.0.1", port, timeout=10)
    try:
        connection.request("GET", f"/api/hub/evaluation/targets?{query}")
        with connection.getresponse() as response:
            data = response.read(524289)
            if response.status != 200 or len(data) > 524288:
                raise RuntimeError(f"Hub evaluation targets unavailable (HTTP {response.status})")
        jobs = json.loads(data)["targets"]
        return {job["dispatch_id"]: job for job in jobs}
    finally:
        connection.close()


def retain_dispatch_cursors(previous: dict, live: dict) -> dict:
    # Dispatch IDs are immutable across native restarts. Keep watermarks
    # when a worker is temporarily absent, but bound the scratch archive
    # by the Hub's maximum of 1,000 stored jobs.
    cursor = previous.copy()
    incoming = sum(dispatch not in cursor for dispatch in live)
    while len(cursor) + incoming > 1000:
        expired = next((key for key in cursor if key not in live), None)
        if expired is None:
            raise ValueError("Too many simultaneous evaluation dispatches.")
        del cursor[expired]
    return cursor


def save_cursor(path: Path, cursor: dict):
    # The fixed pending file is private to this machine/suite kernel lock.
    pending = path.with_suffix(".pending")
    with pending.open("w", encoding="utf-8") as stream:
        json.dump(cursor, stream)
        stream.flush()
        os.fsync(stream.fileno())
    os.replace(pending, path)


def passed_receipt(path: Path, dispatch: str, suite: dict, job: dict) -> bool:
    try:
        receipt = json.loads(read_bounded(path, 4096))
        successes = receipt["successes"]
        trials = receipt["trials"]
        return (
            receipt["schema_version"] == 1
            and receipt["dispatch_id"] == dispatch
            and receipt["suite_id"] == suite["suite_id"]
            and (not job.get("suite_sha256") or receipt.get("suite_sha256") == job["suite_sha256"])
            and SHA256.fullmatch(receipt.get("checkpoint_sha256", ""))
            and type(trials) is int
            and trials == suite["trials"]
            and type(successes) is int
            and 0 <= successes <= trials
            and trials >= job["min_trials"]
            and successes / trials >= job["min_success_rate"]
        )
    except (FileNotFoundError, KeyError, TypeError, ValueError):
        return False


def flush_receipt(args, suite: dict, pending: dict) -> None:
    receipt = pending["receipt"]
    dispatch = receipt["dispatch_id"]
    job = pending["target"]
    if (
        not isinstance(dispatch, str) or not UUID.fullmatch(dispatch)
        or receipt["suite_id"] != suite["suite_id"]
        or job["dispatch_id"] != dispatch or not UUID.fullmatch(job["job_id"])
        or receipt["trials"] != suite["trials"]
        or (job.get("suite_sha256") and receipt.get("suite_sha256") != job["suite_sha256"])
    ):
        raise ValueError("invalid pending evaluation receipt identity")
    # The dispatch was eligible before evaluation began. A result is inert
    # without the matching currently running native dispatch, which the
    # worker and Hub verify independently. Do not discard completed work
    # because a heartbeat expires during a long independent evaluation.
    destination = args.receipt_root / f"{dispatch}.{suite['suite_id']}.json"
    if not passed_receipt(destination, dispatch, suite, job):
        print(publish(args.receipt_root, receipt))


def poll_checkpoint(args, project, suite, receipt_suite, find_checkpoint, evaluate_checkpoint):
    suite_id = suite["suite_id"]
    state = args.scratch / f"{project}-evaluation-{args.machine}.{suite_id}.json"
    digest = suite_digest(suite)

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
        live = targets(args.port, args.machine, suite_id, project)
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
                if job.get("suite_sha256", digest) != digest:
                    print(f"{project} evaluation target {dispatch} requires a different suite definition.", file=sys.stderr)
                    continue
                receipt = args.receipt_root / f"{dispatch}.{suite_id}.json"
                if passed_receipt(receipt, dispatch, receipt_suite, job):
                    continue
                checkpoint = find_checkpoint(job)
                if not checkpoint or checkpoint[0] <= cursor[dispatch]:
                    continue
                update, file = checkpoint
                with file.open("rb") as stream:
                    checkpoint_digest = hashlib.file_digest(stream, "sha256").hexdigest()
            except (OSError, ValueError, TypeError, KeyError) as exc:
                print(f"{project} evaluation target {dispatch} skipped: {exc}", file=sys.stderr)
                continue
            # Do not consume a checkpoint before its evaluation has
            # produced a durable pending receipt. An interrupted evaluator
            # must be able to retry the same immutable checkpoint.
            try:
                with tempfile.TemporaryDirectory(prefix=f"{project}-eval-", dir=args.scratch) as temp:
                    successes = evaluate_checkpoint(job, file, checkpoint_digest, Path(temp))
                if type(successes) is not int or not 0 <= successes <= receipt_suite["trials"]:
                    raise ValueError("Invalid evaluation success count.")
            except (OSError, RuntimeError, ValueError, TypeError, KeyError):
                # Keep this checkpoint eligible, but rotate a failing dispatch
                # behind other workers so it cannot starve their evaluations.
                cursor[dispatch] = cursor.pop(dispatch)
                save_cursor(state, saved)
                raise
            cursor.pop(dispatch)
            cursor[dispatch] = update
            pending = {"target": job, "receipt": {
                "schema_version": 1, "suite_id": suite_id, "suite_sha256": digest, "dispatch_id": dispatch,
                "checkpoint_sha256": checkpoint_digest, "trials": receipt_suite["trials"], "successes": successes,
            }}
            save_cursor(state, {**saved, "pending_receipt": pending})
            flush_receipt(args, receipt_suite, pending)
            save_cursor(state, saved)
            return
        if saved != prior:
            save_cursor(state, saved)
