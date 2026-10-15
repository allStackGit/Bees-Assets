"""Bounded-staleness admission for asynchronously computed MA-POCA gradients.

The coordinator only schedules and validates results. It intentionally cannot
apply gradients, step Adam, or read live model state. One central trainer thread
owns optimizer revisions and decides whether admitted gradients may be applied.
"""

from __future__ import annotations

from dataclasses import dataclass
from typing import Any, Mapping

from bees_distributed_poca_exchange import GradientWorkExchange


@dataclass(frozen=True)
class CollectedGradient:
    status: str
    job_id: str | None = None
    lag: int | None = None
    result: Mapping[str, Any] | None = None
    detail: str = ""


class GradientJobCoordinator:
    """Reuse immutable snapshots across concurrent jobs, fencing stale results.

    The owner must increment optimizer_revision after *each* authoritative Adam
    update, independently of environment steps and rollout policy versions.
    """

    def __init__(self, exchange: GradientWorkExchange, *, session_id: str,
                 max_optimizer_lag: int) -> None:
        if not isinstance(exchange, GradientWorkExchange):
            raise TypeError("A session-scoped GradientWorkExchange is required")
        if not isinstance(session_id, str) or not session_id:
            raise ValueError("A nonempty broker session ID is required")
        if type(max_optimizer_lag) is not int or max_optimizer_lag < 0:
            raise ValueError("max_optimizer_lag must be a nonnegative integer")
        self.exchange = exchange
        self.session_id = session_id
        self.max_optimizer_lag = max_optimizer_lag
        self._snapshots: dict[str, int] = {}
        self._retired: set[str] = set()
        self._jobs: dict[str, tuple[str, int]] = {}
        self.accepted = 0
        self.rejected_stale = 0
        self.rejected_invalid = 0
        self.remote_failures = 0

    def publish_snapshot(self, encoded: bytes, *, optimizer_revision: int) -> str | None:
        if type(optimizer_revision) is not int or optimizer_revision < 0:
            raise ValueError("optimizer_revision must be a nonnegative integer")
        token = self.exchange.publish_snapshot(encoded)
        if token:
            self._snapshots[token] = optimizer_revision
        return token

    def submit(self, snapshot_id: str, *, behavior_id: str,
               behavior_spec: Any, minibatch_bytes: bytes) -> str | None:
        if snapshot_id not in self._snapshots or snapshot_id in self._retired:
            raise ValueError("Snapshot is not available for new work")
        if not isinstance(minibatch_bytes, bytes) or not minibatch_bytes:
            raise ValueError("Serialized minibatch must be nonempty bytes")
        revision = self._snapshots[snapshot_id]
        job_id = self.exchange.submit({
            "session_id": self.session_id,
            "optimizer_revision": revision,
            "snapshot_id": snapshot_id,
            "behavior_id": behavior_id,
            "behavior_spec": behavior_spec,
            "minibatch_bytes": minibatch_bytes,
        })
        if job_id:
            self._jobs[job_id] = (snapshot_id, revision)
        return job_id

    def _cleanup_snapshot(self, snapshot_id: str) -> None:
        if snapshot_id not in self._retired:
            return
        if any(item[0] == snapshot_id for item in self._jobs.values()):
            return
        self.exchange.forget_snapshot(snapshot_id)
        self._retired.remove(snapshot_id)
        self._snapshots.pop(snapshot_id, None)

    def retire_snapshot(self, snapshot_id: str) -> None:
        if snapshot_id not in self._snapshots:
            raise ValueError("Unknown snapshot")
        self._retired.add(snapshot_id)
        self._cleanup_snapshot(snapshot_id)

    def cancel(self, job_id: str) -> bool:
        record = self._jobs.pop(job_id, None)
        if record is None:
            return False
        self.exchange.cancel(job_id)
        self._cleanup_snapshot(record[0])
        return True

    def collect(self, job_id: str, *, current_optimizer_revision: int,
                timeout_seconds: float = 0.0) -> CollectedGradient | None:
        """Return None while pending, or a vetted result; never mutate Adam.

        Results computed for newer revisions, beyond the age limit, against a
        different session, or with incorrect revision metadata are rejected.
        Gradient shapes/finite values must be validated separately immediately
        before optimizer application.
        """
        if type(current_optimizer_revision) is not int or current_optimizer_revision < 0:
            raise ValueError("current_optimizer_revision must be nonnegative")
        record = self._jobs.get(job_id)
        if record is None:
            raise ValueError("Unknown or already-consumed gradient job")
        result = self.exchange.wait_result(job_id, timeout_seconds)
        if result is None:
            return None
        snapshot_id, revision = self._jobs.pop(job_id)
        self._cleanup_snapshot(snapshot_id)
        if "error" in result:
            self.remote_failures += 1
            return CollectedGradient("remote_error", job_id=job_id, detail=str(result["error"]))
        if result.get("session_id") != self.session_id:
            self.rejected_invalid += 1
            return CollectedGradient("invalid", job_id=job_id, detail="Broker session mismatch")
        if result.get("snapshot_id") != snapshot_id:
            self.rejected_invalid += 1
            return CollectedGradient("invalid", job_id=job_id, detail="Gradient snapshot mismatch")
        if type(result.get("optimizer_revision")) is not int or result["optimizer_revision"] != revision:
            self.rejected_invalid += 1
            return CollectedGradient("invalid", job_id=job_id, detail="Optimizer revision mismatch")
        lag = current_optimizer_revision - revision
        if lag < 0:
            self.rejected_invalid += 1
            return CollectedGradient("invalid", job_id=job_id, lag=lag, detail="Future optimizer revision")
        if lag > self.max_optimizer_lag:
            self.rejected_stale += 1
            return CollectedGradient("stale", job_id=job_id, lag=lag, detail="Maximum optimizer lag exceeded")
        if not isinstance(result.get("gradients"), Mapping):
            self.rejected_invalid += 1
            return CollectedGradient("invalid", job_id=job_id, lag=lag, detail="Missing gradients")
        if type(result.get("batch_experiences")) is not int or result["batch_experiences"] < 1:
            self.rejected_invalid += 1
            return CollectedGradient("invalid", job_id=job_id, lag=lag, detail="Invalid batch size")
        self.accepted += 1
        return CollectedGradient("accepted", job_id=job_id, lag=lag, result=result)

    def metrics(self) -> dict[str, int]:
        return {
            "gradient_jobs_unconsumed": len(self._jobs),
            "gradient_snapshots_managed": len(self._snapshots),
            "gradient_results_accepted": self.accepted,
            "gradient_results_rejected_stale": self.rejected_stale,
            "gradient_results_rejected_invalid": self.rejected_invalid,
            "gradient_results_remote_failed": self.remote_failures,
        }
