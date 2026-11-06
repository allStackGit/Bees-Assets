#!/usr/bin/env python3
"""Project-neutral scheduled evaluation coordination and durable cursors."""

from contextlib import contextmanager
import http.client
import json
import os
from pathlib import Path
import sys
from urllib.parse import urlencode

from experiment_hub_eval_io import SHA256, UUID, publish, read_bounded


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
    ):
        raise ValueError("invalid pending evaluation receipt identity")
    # The dispatch was eligible before evaluation began. A result is inert
    # without the matching currently running native dispatch, which the
    # worker and Hub verify independently. Do not discard completed work
    # because a heartbeat expires during a long independent evaluation.
    destination = args.receipt_root / f"{dispatch}.{suite['suite_id']}.json"
    if not passed_receipt(destination, dispatch, suite, job):
        print(publish(args.receipt_root, receipt))
