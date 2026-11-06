#!/usr/bin/env python3
"""Run one scheduled Ants held-out evaluation on a separate CPU worker."""

from __future__ import annotations

import argparse
from contextlib import contextmanager
import hashlib
import http.client
import json
import os
from pathlib import Path
import re
import sys
from urllib.parse import urlencode

from experiment_hub_ants_evaluate import SHA256, UUID, SUITE_ID, evaluate, publish, read_bounded, read_suite


CHECKPOINT = re.compile(r"checkpoint_[a-zA-Z0-9_-]{1,120}$")


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


def targets(port: int, machine: str, suite: str, project: str = "ants") -> dict:
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


def latest(artifacts: Path, lineage: str):
    if not UUID.fullmatch(lineage):
        raise ValueError("invalid Ants artifact lineage")
    directory = artifacts / lineage / "checkpoints"
    if directory.resolve() != directory:
        raise ValueError("checkpoint directory is redirected")
    try:
        pointer = json.loads(read_bounded(directory / "latest.json", 4096))
    except FileNotFoundError:
        return None
    name = pointer.get("checkpoint")
    if not isinstance(name, str) or not CHECKPOINT.fullmatch(name):
        raise ValueError("invalid Ants latest checkpoint pointer")
    checkpoint = directory / name
    if checkpoint.resolve(strict=True) != checkpoint or not checkpoint.is_dir():
        raise ValueError("latest checkpoint is redirected or unavailable")
    metadata = json.loads(read_bounded(checkpoint / "metadata.json", 262144))
    episode = metadata.get("episode_number")
    if type(episode) is not int or episode < 0 or str(metadata.get("reason", "")).upper() == "REGRESSION":
        raise ValueError("ineligible Ants checkpoint metadata")
    return checkpoint, episode


def retain_dispatch_cursors(previous: dict, live: dict) -> dict:
    # Dispatch IDs are immutable across native restarts. Keep watermarks
    # when a worker is temporarily absent, but bound the scratch archive
    # by the Hub's maximum of 1,000 stored jobs.
    cursor = previous.copy()
    for dispatch in live:
        if dispatch in cursor or len(cursor) < 1000:
            continue
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


def poll(args, suite: dict) -> None:
    suite_id = suite["suite_id"]
    state = args.scratch / f"ants-evaluation-{args.machine}.{suite_id}.json"
    suite_digest = hashlib.sha256(json.dumps(suite, sort_keys=True).encode()).hexdigest()
    with exclusive(state.with_suffix(".lock")):
        try:
            prior = json.loads(read_bounded(state, 131072))
            if prior["suite_sha256"] != suite_digest:
                raise ValueError("suite definition changed; use a new suite ID")
            previous = prior["dispatches"]
        except FileNotFoundError:
            prior = {}
            previous = {}
        pending = prior.get("pending_receipt")
        if pending is not None:
            flush_receipt(args, suite, pending)
            save_cursor(state, {"suite_sha256": suite_digest, "dispatches": previous})
            return
        live = targets(args.port, args.machine, suite_id)
        cursor = retain_dispatch_cursors(previous, live)
        candidates = {}
        for dispatch, job in live.items():
            try:
                if not UUID.fullmatch(dispatch):
                    raise ValueError("invalid evaluation dispatch")
                if suite["trials"] < job["min_trials"]:
                    continue
                receipt = args.receipt_root / f"{dispatch}.{suite_id}.json"
                if passed_receipt(receipt, dispatch, suite, job):
                    continue
                current = latest(args.artifact_root, job["artifact_lineage_root_id"])
                if current is None:
                    continue
                checkpoint, episode = current
                if dispatch not in cursor:
                    # Fresh jobs own their lineage. A continuation can initially
                    # point at its parent's checkpoint, so watermark it instead.
                    fresh = job["job_id"] == job["artifact_lineage_root_id"]
                    cursor[dispatch] = -1 if fresh else episode
                if episode > cursor[dispatch]:
                    candidates[dispatch] = (checkpoint, episode)
            except (OSError, ValueError, TypeError, KeyError) as exc:
                print(f"Ants evaluation target {dispatch} skipped: {exc}", file=sys.stderr)

        for dispatch in cursor:
            if dispatch not in candidates:
                continue
            checkpoint, episode = candidates[dispatch]
            try:
                digest = hashlib.sha256(read_bounded(checkpoint / "manifest.json", 65536)).hexdigest()
            except (OSError, ValueError) as exc:
                print(f"Ants evaluation target {dispatch} manifest unavailable: {exc}", file=sys.stderr)
                continue
            break
        else:
            dispatch = None
        if dispatch is not None:
            # Rotate after each attempt and skip this checkpoint on later invocations.
            cursor.pop(dispatch)
            cursor[dispatch] = episode
        if cursor != previous or dispatch is not None or not state.exists():
            save_cursor(state, {"suite_sha256": suite_digest, "dispatches": cursor})
        if dispatch is None:
            return
        successes = evaluate(checkpoint, digest, suite, args.scratch)
        pending = {
            "target": live[dispatch],
            "receipt": {
                "schema_version": 1,
                "suite_id": suite_id,
                "dispatch_id": dispatch,
                "checkpoint_sha256": digest,
                "trials": suite["trials"],
                "successes": successes,
            },
        }
        saved = {"suite_sha256": suite_digest, "dispatches": cursor}
        save_cursor(state, {**saved, "pending_receipt": pending})
        flush_receipt(args, suite, pending)
        save_cursor(state, saved)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--machine", required=True, help="Hub ID of the Ants training host")
    parser.add_argument("--port", type=int, default=7148)
    for key in ("artifact-root", "suite", "scratch", "receipt-root"):
        parser.add_argument(f"--{key}", type=Path, required=True)
    args = parser.parse_args()
    try:
        if not SUITE_ID.fullmatch(args.machine) or not 1 <= args.port <= 65535:
            raise ValueError("invalid Hub machine ID or loopback port")
        args.artifact_root = args.artifact_root.resolve(strict=True)
        for key in ("scratch", "receipt_root"):
            directory = getattr(args, key).resolve(strict=True)
            if not directory.is_dir() or directory.is_relative_to(args.artifact_root):
                raise ValueError("scratch and receipt directories must be outside the Ants artifact root")
            setattr(args, key, directory)
        poll(args, read_suite(args.suite))
        return 0
    except (OSError, ValueError, RuntimeError, TypeError, KeyError, http.client.HTTPException) as exc:
        print(f"Scheduled Ants evaluation failed: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
