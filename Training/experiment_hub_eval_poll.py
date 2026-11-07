#!/usr/bin/env python3
"""Project-neutral scheduled evaluation coordination and durable cursors."""

from contextlib import contextmanager
import hashlib
import http.client
import json
import os
import shutil
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


def passed_receipt(path: Path, dispatch: str, suite: dict, job: dict, expected_digest: str) -> bool:
    try:
        receipt = json.loads(read_bounded(path, 4096))
        successes = receipt["successes"]
        trials = receipt["trials"]
        return (
            receipt["schema_version"] == 1
            and receipt["dispatch_id"] == dispatch
            and receipt["suite_id"] == suite["suite_id"]
            and receipt.get("suite_sha256") == expected_digest
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


def flush_receipt(args, suite: dict, pending: dict, expected_digest: str) -> None:
    receipt = pending["receipt"]
    dispatch = receipt["dispatch_id"]
    job = pending["target"]
    if (
        not isinstance(dispatch, str) or not UUID.fullmatch(dispatch)
        or receipt["suite_id"] != suite["suite_id"]
        or job["dispatch_id"] != dispatch or not UUID.fullmatch(job["job_id"])
        or receipt["trials"] != suite["trials"]
        or receipt.get("suite_sha256") != expected_digest
        or (job.get("suite_sha256") and receipt.get("suite_sha256") != job["suite_sha256"])
    ):
        raise ValueError("invalid pending evaluation receipt identity")
    # The dispatch was eligible before evaluation began. A result is inert
    # without the matching currently running native dispatch, which the
    # worker and Hub verify independently. Do not discard completed work
    # because a heartbeat expires during a long independent evaluation.
    destination = args.receipt_root / f"{dispatch}.{suite['suite_id']}.json"
    if not passed_receipt(destination, dispatch, suite, job, expected_digest):
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
            flush_receipt(args, receipt_suite, pending, digest)
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
                # A one-suite worker cannot satisfy a checkpoint-coherent
                # multi-suite gate. Such jobs need one coordinated batch.
                if set(job.get("required_suite_ids", [suite_id])) != {suite_id}:
                    continue
                if not UUID.fullmatch(dispatch) or receipt_suite["trials"] < job["min_trials"]:
                    continue
                if job.get("suite_sha256", digest) != digest:
                    print(f"{project} evaluation target {dispatch} requires a different suite definition.", file=sys.stderr)
                    continue
                receipt = args.receipt_root / f"{dispatch}.{suite_id}.json"
                if passed_receipt(receipt, dispatch, receipt_suite, job, digest):
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
            flush_receipt(args, receipt_suite, pending, digest)
            save_cursor(state, saved)
            return
        if saved != prior:
            save_cursor(state, saved)


def poll_batch(args, project, suites, find_checkpoint, evaluate_checkpoint):
    """Evaluate every required suite on one immutable checkpoint snapshot.

    suites: sequence of (frozen suite, receipt suite with trial count).
    A separate scheduler should invoke this worker for each all-of gate
    instead of invoking multiple independent single-suite pollers.
    """
    if not 2 <= len(suites) <= 8:
        raise ValueError("A batch needs 2–8 independent evaluation suites.")
    by_id = {suite["suite_id"]: (suite, receipt) for suite, receipt in suites}
    if len(by_id) != len(suites):
        raise ValueError("Batch suites must have distinct IDs.")
    ordered = sorted(by_id.items())
    definitions = {sid: suite_digest(suite) for sid, (suite, _) in ordered}
    identity = hashlib.sha256(json.dumps(
        definitions, sort_keys=True).encode()).hexdigest()
    state = args.scratch / f"{project}-evaluation-{args.machine}.batch-{identity[:20]}.json"

    def flush_batch(pending):
        if not isinstance(pending, list) or len(pending) != len(ordered):
            raise ValueError("Invalid pending evaluation batch.")
        common_checkpoint = None
        common_dispatch = None
        receipts = []
        for (sid, (suite, receipt_suite)), item in zip(ordered, pending):
            target, receipt = item["target"], item["receipt"]
            if (receipt["suite_id"] != sid or
                receipt.get("suite_sha256") != definitions[sid] or
                receipt["trials"] != receipt_suite["trials"] or
                type(receipt["successes"]) is not int or
                not 0 <= receipt["successes"] <= receipt["trials"] or
                not UUID.fullmatch(receipt["dispatch_id"]) or
                not UUID.fullmatch(target["job_id"]) or
                target["dispatch_id"] != receipt["dispatch_id"] or
                (target.get("suite_sha256") and
                 target["suite_sha256"] != definitions[sid]) or
                not SHA256.fullmatch(receipt.get("checkpoint_sha256", ""))):
                raise ValueError("Invalid pending batch identity.")
            if common_checkpoint is not None and (
                receipt["checkpoint_sha256"] != common_checkpoint or
                receipt["dispatch_id"] != common_dispatch
            ):
                raise ValueError("A batch must belong to one checkpoint and dispatch.")
            common_checkpoint = receipt["checkpoint_sha256"]
            common_dispatch = receipt["dispatch_id"]
            receipts.append(receipt)
        for receipt in receipts:
            destination = args.receipt_root / (
                f'{receipt["dispatch_id"]}.{receipt["suite_id"]}.json')
            try:
                existing = json.loads(read_bounded(destination, 4096))
            except (FileNotFoundError, ValueError, json.JSONDecodeError):
                existing = None
            if existing != receipt:
                print(publish(args.receipt_root, receipt))

    with exclusive(state.with_suffix(".lock")):
        try:
            prior = json.loads(read_bounded(state, 131072))
            if prior["suite_sha256"] != identity:
                raise ValueError("Batch suite definitions changed; use new suite IDs.")
        except FileNotFoundError:
            prior = {"suite_sha256": identity, "dispatches": {}}
        if (pending := prior.get("pending_receipts")) is not None:
            flush_batch(pending)
            save_cursor(state, {"suite_sha256": identity,
                                "dispatches": prior["dispatches"]})
            return

        target_sets = {sid: targets(args.port, args.machine, sid, project)
                       for sid, _ in ordered}
        expected_ids = set(by_id)
        common = set.intersection(*(set(items) for items in target_sets.values()))
        live = {}
        for dispatch in target_sets[ordered[0][0]]:
            if dispatch not in common:
                continue
            per_suite = [target_sets[sid][dispatch] for sid, _ in ordered]
            if (any(set(job.get("required_suite_ids", [])) != expected_ids
                    for job in per_suite) or
                len({job["job_id"] for job in per_suite}) != 1 or
                len({job["artifact_lineage_root_id"] for job in per_suite}) != 1):
                continue
            live[dispatch] = per_suite

        cursor = retain_dispatch_cursors(prior["dispatches"], live)
        for dispatch in live:
            cursor.setdefault(dispatch, 0)
        saved = {"suite_sha256": identity, "dispatches": cursor}
        for dispatch in list(cursor):
            if dispatch not in live:
                continue
            jobs = live[dispatch]
            try:
                if not UUID.fullmatch(dispatch):
                    continue
                if any(receipt_suite["trials"] < job["min_trials"] or
                       job.get("suite_sha256", definitions[sid]) != definitions[sid]
                       for (sid, (_, receipt_suite)), job
                       in zip(ordered, jobs)):
                    continue
                seen = []
                for sid, (_, receipt_suite) in ordered:
                    job = target_sets[sid][dispatch]
                    destination = args.receipt_root / f"{dispatch}.{sid}.json"
                    if not passed_receipt(
                        destination, dispatch, receipt_suite, job, definitions[sid]):
                        break
                    seen.append(json.loads(read_bounded(destination, 4096))[
                        "checkpoint_sha256"])
                else:
                    if len(set(seen)) == 1:
                        continue
                checkpoint = find_checkpoint(jobs[0])
                if not checkpoint or checkpoint[0] <= cursor[dispatch]:
                    continue
                update, file = checkpoint
                if not file.is_file():
                    continue
            except (OSError, ValueError, TypeError, KeyError) as exc:
                print(f"{project} batch target {dispatch} skipped: {exc}",
                      file=sys.stderr)
                continue

            # One durable snapshot is shared by every suite. The trainer
            # may prune its source at any moment; copy and verify it first.
            with tempfile.TemporaryDirectory(
                prefix=f"{project}-eval-batch-", dir=args.scratch
            ) as temp:
                frozen = Path(temp) / ("checkpoint" + file.suffix)
                shutil.copyfile(file, frozen)
                with frozen.open("rb") as stream:
                    checkpoint_digest = hashlib.file_digest(stream, "sha256").hexdigest()
                pending = []
                for (sid, (suite, receipt_suite)), job in zip(ordered, jobs):
                    successes = evaluate_checkpoint(
                        job, frozen, checkpoint_digest, Path(temp), suite)
                    if (type(successes) is not int or
                        not 0 <= successes <= receipt_suite["trials"]):
                        raise ValueError("Invalid batch evaluation success count.")
                    pending.append({"target": job, "receipt": {
                        "schema_version": 1, "suite_id": sid,
                        "suite_sha256": definitions[sid],
                        "dispatch_id": dispatch,
                        "checkpoint_sha256": checkpoint_digest,
                        "trials": receipt_suite["trials"],
                        "successes": successes,
                    }})

            cursor.pop(dispatch)
            cursor[dispatch] = update
            save_cursor(state, {**saved, "pending_receipts": pending})
            flush_batch(pending)
            save_cursor(state, saved)
            return
        if saved != prior:
            save_cursor(state, saved)
