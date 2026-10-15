"""Bounded, session-scoped remote POCA gradient job exchange.

The owner of the optimizer submits immutable work; authenticated workers claim
it and return gradients. Result consumption is one-shot and ignores late,
duplicate, or cancelled work. The exchange has no access to Adam or checkpoints.
"""

from __future__ import annotations

import collections
import threading
import time
import uuid
from typing import Any, Mapping


class GradientWorkExchange:
    def __init__(self, *, max_pending: int = 8, lease_seconds: float = 600.0):
        if max_pending < 1 or lease_seconds <= 0:
            raise ValueError("Invalid distributed gradient work capacity")
        self.max_pending = int(max_pending)
        self.lease_seconds = float(lease_seconds)
        self._condition = threading.Condition()
        self._available: collections.deque[str] = collections.deque()
        self._jobs: dict[str, dict[str, Any]] = {}
        self._snapshots: collections.OrderedDict[str, dict[str, Any]] = collections.OrderedDict()
        self._max_snapshots = 4
        self._worker_seen: dict[str, float] = {}
        self._closed = False
        self.completed = 0
        self.expired = 0
        self.cancelled = 0
        self.duplicates = 0

    def _expire(self) -> None:
        now = time.monotonic()
        for job_id, record in list(self._jobs.items()):
            if record["state"] == "claimed" and record["lease_until"] <= now:
                record["worker"] = None
                record["claim_token"] = None
                record["claimed_at"] = None
                record["state"] = "queued"
                self._available.append(job_id)
                self.expired += 1

    def publish_snapshot(self, encoded: bytes) -> str | None:
        """Hold a compressed immutable actor/critic snapshot for multiple jobs."""
        if not isinstance(encoded, bytes) or not encoded:
            raise ValueError("Gradient snapshot must be nonempty encoded bytes")
        with self._condition:
            if self._closed:
                return None
            if len(self._snapshots) >= self._max_snapshots:
                for token, record in tuple(self._snapshots.items()):
                    if record["users"] == 0:
                        self._snapshots.pop(token)
                        break
                else:
                    return None
            token = uuid.uuid4().hex
            self._snapshots[token] = {"encoded": encoded, "users": 0}
            return token

    def snapshot(self, token: str) -> bytes | None:
        with self._condition:
            record = self._snapshots.get(token)
            if record is None or self._closed:
                return None
            self._snapshots.move_to_end(token)
            return record["encoded"]

    def _release_job_snapshot(self, record: Mapping[str, Any]) -> None:
        token = record["payload"].get("snapshot_id")
        if token is not None:
            snapshot = self._snapshots.get(token)
            if snapshot is not None:
                snapshot["users"] -= 1

    def forget_snapshot(self, token: str) -> None:
        with self._condition:
            record = self._snapshots.get(token)
            if record is not None and record["users"] == 0:
                self._snapshots.pop(token)

    def submit(self, payload: Mapping[str, Any]) -> str | None:
        with self._condition:
            if self._closed or len(self._jobs) >= self.max_pending:
                return None
            token = payload.get("snapshot_id")
            if token is not None:
                snapshot = self._snapshots.get(token)
                if snapshot is None:
                    raise ValueError("Gradient job references an unknown snapshot")
                snapshot["users"] += 1
            job_id = uuid.uuid4().hex
            self._jobs[job_id] = {
                "payload": dict(payload),
                "state": "queued",
                "worker": None,
                "claim_token": None,
                "created_at": time.monotonic(),
                "claimed_at": None,
                "completed_at": None,
                "lease_until": 0.0,
                "result": None,
            }
            self._available.append(job_id)
            self._condition.notify_all()
            return job_id

    def claim(self, worker: str, wait_seconds: float = 0.0) -> Mapping[str, Any] | None:
        if not isinstance(worker, str) or not (1 <= len(worker) <= 128):
            raise ValueError("Remote gradient worker identity must be 1-128 characters")
        if not worker.isascii() or any(c not in "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_." for c in worker):
            raise ValueError("Remote gradient worker identity contains invalid characters")
        deadline = time.monotonic() + min(20.0, max(0.0, wait_seconds))
        with self._condition:
            self._worker_seen[worker] = time.monotonic()
            while not self._closed:
                self._worker_seen[worker] = time.monotonic()
                self._expire()
                while self._available:
                    job_id = self._available.popleft()
                    record = self._jobs.get(job_id)
                    if not record or record["state"] != "queued":
                        continue
                    record["state"] = "claimed"
                    record["worker"] = worker
                    record["claim_token"] = uuid.uuid4().hex
                    record["claimed_at"] = time.monotonic()
                    record["lease_until"] = record["claimed_at"] + self.lease_seconds
                    return {
                        "job_id": job_id,
                        "claim_token": record["claim_token"],
                        "payload": record["payload"],
                    }
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    return None
                self._condition.wait(min(remaining, 1.0))
            return None

    def complete(
        self, worker: str, job_id: str, claim_token: str,
        result: Mapping[str, Any], *,
        result_wire_bytes: int = 0,
        result_receive_decode_seconds: float = 0.0,
    ) -> bool:
        with self._condition:
            self._expire()
            record = self._jobs.get(job_id)
            if (
                not record
                or record["state"] != "claimed"
                or record["worker"] != worker
                or record["claim_token"] != claim_token
            ):
                self.duplicates += 1
                return False
            if not isinstance(result, Mapping):
                raise ValueError("Remote gradient result must be a mapping")
            record["result"] = dict(result)
            record["result_wire_bytes"] = result_wire_bytes
            record["result_receive_decode_seconds"] = result_receive_decode_seconds
            record["completed_at"] = time.monotonic()
            record["state"] = "completed"
            self.completed += 1
            self._condition.notify_all()
            return True

    def wait_result(
        self, job_id: str, timeout_seconds: float
    ) -> Mapping[str, Any] | None:
        deadline = time.monotonic() + max(0.0, float(timeout_seconds))
        with self._condition:
            while not self._closed:
                record = self._jobs.get(job_id)
                if record is None:
                    return None
                if record["state"] == "completed":
                    self._jobs.pop(job_id)
                    self._release_job_snapshot(record)
                    # All timings use the *central* monotonic clock; "in_flight"
                    # covers response transport as well as worker computation.
                    now = time.monotonic()
                    claimed = record["claimed_at"]
                    completed = record["completed_at"]
                    return {
                        **record["result"],
                        "_broker_timings": {
                            "queue_seconds": max(0.0, claimed - record["created_at"]),
                            "in_flight_seconds": max(0.0, completed - claimed),
                            "poll_delay_seconds": max(0.0, now - completed),
                            "result_wire_bytes": record["result_wire_bytes"],
                            "result_receive_decode_seconds": record["result_receive_decode_seconds"],
                        },
                    }
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    return None
                self._condition.wait(min(remaining, 0.25))
            return None

    def cancel(self, job_id: str) -> bool:
        with self._condition:
            if job_id not in self._jobs:
                return False
            self._release_job_snapshot(self._jobs.pop(job_id))
            self.cancelled += 1
            self._condition.notify_all()
            return True

    def metrics(self) -> dict[str, int]:
        with self._condition:
            self._expire()
            states = collections.Counter(r["state"] for r in self._jobs.values())
            return {
                "gradient_snapshots_cached": len(self._snapshots),
                "gradient_jobs_queued": states["queued"],
                "gradient_jobs_claimed": states["claimed"],
                "gradient_jobs_completed_pending": states["completed"],
                "gradient_jobs_completed_total": self.completed,
                "gradient_workers_active": sum(
                    time.monotonic() - seen <= 45.0
                    for seen in self._worker_seen.values()
                ),
                "gradient_jobs_cancelled": self.cancelled,
                "gradient_jobs_expired": self.expired,
                "gradient_results_rejected": self.duplicates,
            }

    def close(self) -> None:
        with self._condition:
            self._closed = True
            self._jobs.clear()
            self._snapshots.clear()
            self._worker_seen.clear()
            self._available.clear()
            self._condition.notify_all()
