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
    def __init__(self, *, max_pending: int = 8, lease_seconds: float = 180.0):
        if max_pending < 1 or lease_seconds <= 0:
            raise ValueError("Invalid distributed gradient work capacity")
        self.max_pending = int(max_pending)
        self.lease_seconds = float(lease_seconds)
        self._condition = threading.Condition()
        self._available: collections.deque[str] = collections.deque()
        self._jobs: dict[str, dict[str, Any]] = {}
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
                record["state"] = "queued"
                self._available.append(job_id)
                self.expired += 1

    def submit(self, payload: Mapping[str, Any]) -> str | None:
        with self._condition:
            if self._closed or len(self._jobs) >= self.max_pending:
                return None
            job_id = uuid.uuid4().hex
            self._jobs[job_id] = {
                "payload": dict(payload),
                "state": "queued",
                "worker": None,
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
                    record["lease_until"] = time.monotonic() + self.lease_seconds
                    return {"job_id": job_id, "payload": record["payload"]}
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    return None
                self._condition.wait(min(remaining, 1.0))
            return None

    def complete(
        self, worker: str, job_id: str, result: Mapping[str, Any]
    ) -> bool:
        with self._condition:
            self._expire()
            record = self._jobs.get(job_id)
            if (
                not record
                or record["state"] != "claimed"
                or record["worker"] != worker
            ):
                self.duplicates += 1
                return False
            if not isinstance(result, Mapping):
                raise ValueError("Remote gradient result must be a mapping")
            record["result"] = dict(result)
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
                    return record["result"]
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    return None
                self._condition.wait(min(remaining, 0.25))
            return None

    def cancel(self, job_id: str) -> bool:
        with self._condition:
            if job_id not in self._jobs:
                return False
            self._jobs.pop(job_id)
            self.cancelled += 1
            self._condition.notify_all()
            return True

    def metrics(self) -> dict[str, int]:
        with self._condition:
            self._expire()
            states = collections.Counter(r["state"] for r in self._jobs.values())
            return {
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
            self._worker_seen.clear()
            self._available.clear()
            self._condition.notify_all()
