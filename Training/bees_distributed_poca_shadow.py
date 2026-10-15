"""Opt-in shadow-only WAN pilot for concurrent remote Bees POCA gradients.

BEES_POCA_SHADOW_JOBS=N schedules N distinct PPO minibatches (up to eight)
against ONE frozen actor/critic snapshot. Exeter's normal PPO loop continues
unchanged. A daemon records completion/latency and NEVER applies gradients.
"""

from __future__ import annotations

import hashlib
import os
import threading
import time
from typing import Any

from bees_distributed_poca import encode_cpu_minibatch, snapshot_model
from bees_distributed_poca_apply import AuthoritativeGradientApplier
from bees_distributed_poca_scheduler import GradientJobCoordinator
from bees_distributed_poca_parity import LocalGradientReference
from bees_wan_actor_training import encode_payload

_LOCK = threading.Lock()
_ACTIVE_BROKER: tuple[Any, str] | None = None
_ACTIVE_PILOT = False
_PENDING_REFERENCE: LocalGradientReference | None = None


def register_gradient_exchange(exchange: Any, session_id: str) -> None:
    global _ACTIVE_BROKER
    with _LOCK:
        _ACTIVE_BROKER = (exchange, session_id)


def unregister_gradient_exchange(exchange: Any) -> None:
    global _ACTIVE_BROKER
    with _LOCK:
        if _ACTIVE_BROKER and _ACTIVE_BROKER[0] is exchange:
            _ACTIVE_BROKER = None


def _requested_jobs() -> int:
    raw = os.environ.get("BEES_POCA_SHADOW_JOBS", "").strip()
    if not raw or raw == "0":
        return 0
    value = int(raw)
    if not 1 <= value <= 8:
        raise ValueError("BEES_POCA_SHADOW_JOBS must be 0..8")
    return value



def capture_first_local_gradient(
    optimizer: Any, stats: dict, clip_report: dict | None = None,
) -> None:
    """PPO-thread capture of gradients and clip decisions for the first minibatch."""
    global _PENDING_REFERENCE
    with _LOCK:
        reference = _PENDING_REFERENCE
        _PENDING_REFERENCE = None
    if reference is not None:
        reference.capture(optimizer, stats, clip_report)


def _collect_async(
    coordinator: GradientJobCoordinator, snapshot_id: str,
    job_ids: list[str], optimizer_revision: int, started_at: float,
    prepared_seconds: float, minibatch_bytes: int, snapshot_bytes: int,
    reference: LocalGradientReference, expected_first_minibatch_sha256: str,
) -> None:
    global _ACTIVE_PILOT, _PENDING_REFERENCE
    results = []
    try:
        first_remote = None
        profiles = []
        inflight_by_job = {}
        worker_compute_seconds = 0.0
        # Shadow mode intentionally does not infer or apply the learner's
        # live revision from another thread. This tests transport only;
        # optimizer-staleness decisions belong to the central PPO thread.
        # Poll every outstanding request rather than block behind a slow first
        # job. Deadline applies to the entire pilot, not each individual job.
        pending = set(job_ids)
        deadline = started_at + 600.0
        queue_sum = 0.0
        queue_max = 0.0
        result_wire_bytes = 0
        receive_decode_seconds = 0.0
        worker_names = set()
        failure_details = []
        while pending and time.monotonic() < deadline:
            for job_id in list(pending):
                outcome = coordinator.collect(
                    job_id,
                    current_optimizer_revision=optimizer_revision,
                    timeout_seconds=0.0,
                )
                if outcome is None:
                    continue
                pending.remove(job_id)
                timing = (outcome.result or {}).get("_broker_timings", {})
                queue_s = float(timing.get("queue_seconds", 0.0))
                inflight = float(timing.get("in_flight_seconds", 0.0))
                queue_sum += queue_s
                queue_max = max(queue_max, queue_s)
                result_wire_bytes += int(timing.get("result_wire_bytes", 0))
                receive_decode_seconds += float(timing.get("result_receive_decode_seconds", 0.0))
                results.append((outcome.status, inflight))
                inflight_by_job[job_id] = inflight
                if outcome.status == "accepted" and outcome.result:
                    worker_compute_seconds += float(
                        outcome.result.get("compute_seconds", 0.0)
                    )
                if outcome.status != "accepted":
                    failure_details.append(
                        f"{job_id[:8]}:{outcome.status}:"
                        + outcome.detail.replace("\n", " ")[:120]
                    )
                if outcome.status == "accepted" and outcome.job_id == job_ids[0]:
                    first_remote = outcome.result
                if outcome.result:
                    profiles.append(outcome.result.get("transport_profile", {}))
                    worker_names.add(str(outcome.result.get("worker", "-")))
            if pending:
                time.sleep(0.1)
        for job_id in pending:
            coordinator.cancel(job_id)
            results.append(("timeout", 0.0))
        accepted = sum(status == "accepted" for status, _ in results)
        input_hash_match = (
            first_remote is not None
            and first_remote.get("minibatch_sha256") == expected_first_minibatch_sha256
        )
        parity = (
            reference.compare(first_remote)
            if input_hash_match
            else ("parity=input_hash_mismatch" if first_remote is not None
                  else "parity=no_matching_result")
        )
        cache_hits = sum(bool(item.get("snapshot_cached")) for item in profiles)
        first_inflight = inflight_by_job.get(job_ids[0], 0.0)
        later_inflight = [
            inflight_by_job[job] for job in job_ids[1:]
            if job in inflight_by_job
        ]
        snapshot_fetch_total = sum(
            float(item.get("snapshot_fetch_seconds", 0.0))
            for item in profiles
        )
        minibatch_decode_total = sum(
            float(item.get("minibatch_decode_seconds", 0.0))
            for item in profiles
        )
        worker_total = sum(float(item.get("worker_before_upload_seconds", 0.0))
                           for item in profiles)
        claim_bytes = sum(int(item.get("claim_encoded_bytes", 0)) for item in profiles)
        fetched_bytes = sum(int(item.get("snapshot_encoded_bytes", 0)) for item in profiles)
        elapsed = time.monotonic() - started_at
        print(
            "[Bees distributed POCA shadow] "
            f"jobs={len(job_ids)} completed={accepted} "
            f"status={','.join(status for status, _ in results)} "
            f"failure_details={';'.join(failure_details[:3]) or '-'} "
            f"elapsed_s={elapsed:.3f} "
            f"completed_jobs_per_s={accepted / max(elapsed, 1e-6):.3f} "
            f"prepare_s={prepared_seconds:.3f} "
            f"snapshot_mib={snapshot_bytes / 1048576:.2f} "
            f"minibatches_mib={minibatch_bytes / 1048576:.2f} "
            f"claim_total_mib={claim_bytes / 1048576:.2f} "
            f"snapshot_fetched_mib={fetched_bytes / 1048576:.2f} "
            f"results_uploaded_mib={result_wire_bytes / 1048576:.2f} "
            f"central_receive_decode_sum_s={receive_decode_seconds:.3f} "
            f"inflight_sum_s={sum(t for _, t in results):.3f} "
            f"first_inflight_s={first_inflight:.3f} "
            f"later_inflight_avg_s={sum(later_inflight) / max(len(later_inflight), 1):.3f} "
            f"worker_compute_sum_s={worker_compute_seconds:.3f} "
            f"snapshot_fetch_sum_s={snapshot_fetch_total:.3f} "
            f"minibatch_decode_sum_s={minibatch_decode_total:.3f} "
            f"queue_sum_s={queue_sum:.3f} queue_max_s={queue_max:.3f} "
            f"distinct_workers={len(worker_names)} "
            f"first_input_hash_match={input_hash_match} "
            f"snapshot_cache_hits={cache_hits}/{len(profiles)} "
            f"worker_before_upload_sum_s={worker_total:.3f} "
            f"{parity} remote_gradients_applied=0",
            flush=True,
        )
    except Exception as exc:
        print(
            "[Bees distributed POCA shadow] collector error: "
            f"{type(exc).__name__}: {exc}; remote_gradients_applied=0",
            flush=True,
        )
    finally:
        for job_id in job_ids:
            try:
                coordinator.cancel(job_id)
            except Exception:
                pass
        try:
            coordinator.retire_snapshot(snapshot_id)
        except Exception:
            pass
        with _LOCK:
            if _PENDING_REFERENCE is reference:
                _PENDING_REFERENCE = None
            _ACTIVE_PILOT = False


class ShadowTrial:
    def __init__(
        self, coordinator: GradientJobCoordinator, snapshot_id: str,
        behavior_id: str, behavior_spec: Any, optimizer_revision: int,
        started_at: float, snapshot_bytes: int, target_jobs: int,
        reference: LocalGradientReference, prepare_seconds: float,
    ) -> None:
        self.coordinator = coordinator
        self.snapshot_id = snapshot_id
        self.behavior_id = behavior_id
        self.behavior_spec = behavior_spec
        self.optimizer_revision = optimizer_revision
        self.started_at = started_at
        self.snapshot_bytes = snapshot_bytes
        self.target_jobs = target_jobs
        self.reference = reference
        self.prepare_seconds = prepare_seconds
        self.minibatch_bytes = 0
        self.first_minibatch_sha256 = ""
        self.job_ids: list[str] = []
        self.finished = False


def finish_shadow_trial(trial: ShadowTrial) -> None:
    """Finish submissions and collect results without blocking GPU PPO."""
    global _ACTIVE_PILOT, _PENDING_REFERENCE
    if trial.finished:
        return
    trial.finished = True
    if not trial.job_ids:
        try:
            trial.coordinator.retire_snapshot(trial.snapshot_id)
        except Exception:
            pass
        finally:
            with _LOCK:
                if _PENDING_REFERENCE is trial.reference:
                    _PENDING_REFERENCE = None
                _ACTIVE_PILOT = False
        return
    print(
        "[Bees distributed POCA shadow] "
        f"submitted={len(trial.job_ids)} revision={trial.optimizer_revision} "
        f"prepare_s={trial.prepare_seconds:.3f} "
        f"snapshot_mib={trial.snapshot_bytes / 1048576:.2f} "
        f"minibatches_mib={trial.minibatch_bytes / 1048576:.2f} "
        "inputs=exact_local_minibatches remote_gradients_applied=0",
        flush=True,
    )
    try:
        threading.Thread(
            target=_collect_async,
            args=(
                trial.coordinator, trial.snapshot_id, list(trial.job_ids),
                trial.optimizer_revision, trial.started_at, trial.prepare_seconds,
                trial.minibatch_bytes, trial.snapshot_bytes, trial.reference,
                trial.first_minibatch_sha256,
            ),
            name="bees-distributed-poca-shadow-collector",
            daemon=True,
        ).start()
    except Exception as exc:
        for job_id in trial.job_ids:
            try:
                trial.coordinator.cancel(job_id)
            except Exception:
                pass
        try:
            trial.coordinator.retire_snapshot(trial.snapshot_id)
        except Exception:
            pass
        with _LOCK:
            if _PENDING_REFERENCE is trial.reference:
                _PENDING_REFERENCE = None
            _ACTIVE_PILOT = False
        print(
            "[Bees distributed POCA shadow] collector launch failed: "
            f"{type(exc).__name__}: {exc}; remote_gradients_applied=0",
            flush=True,
        )


def submit_shadow_minibatch(trial: ShadowTrial, minibatch: dict) -> None:
    """Submit exactly the minibatch the local optimizer is about to consume."""
    if trial.finished or len(trial.job_ids) >= trial.target_jobs:
        return
    try:
        started = time.monotonic()
        encoded = encode_cpu_minibatch(minibatch)
        job_id = trial.coordinator.submit(
            trial.snapshot_id,
            behavior_id=trial.behavior_id,
            behavior_spec=trial.behavior_spec,
            minibatch_bytes=encoded,
        )
        if job_id is None:
            raise RuntimeError("Gradient exchange rejected minibatch submission")
        if not trial.job_ids:
            trial.first_minibatch_sha256 = hashlib.sha256(encoded).hexdigest()
        trial.job_ids.append(job_id)
        trial.minibatch_bytes += len(encoded)
        trial.prepare_seconds += time.monotonic() - started
    except Exception as exc:
        print(
            "[Bees distributed POCA shadow] minibatch submission failed: "
            f"{type(exc).__name__}: {exc}; remote_gradients_applied=0",
            flush=True,
        )
        finish_shadow_trial(trial)
        return
    if len(trial.job_ids) >= trial.target_jobs:
        finish_shadow_trial(trial)


def start_shadow_trial(
    optimizer: Any, behavior_id: str, behavior_spec: Any,
    first_minibatch: dict, available_minibatches: int,
) -> ShadowTrial | None:
    """Snapshot the optimizer immediately before its first real local minibatch.

    The first remote job and Exeter's parity reference share the exact same
    prepared minibatch. Later jobs are submitted as Exeter consumes their
    actual CPU-prepared minibatches, all against this immutable snapshot.
    """
    global _ACTIVE_PILOT, _PENDING_REFERENCE
    try:
        n = min(_requested_jobs(), available_minibatches)
    except (TypeError, ValueError) as exc:
        print(f"[Bees distributed POCA shadow] disabled: {exc}", flush=True)
        return None
    if not n:
        return None
    with _LOCK:
        if _ACTIVE_PILOT or _ACTIVE_BROKER is None:
            return None
        exchange, session_id = _ACTIVE_BROKER
        try:
            workers_active = exchange.metrics()["gradient_workers_active"]
        except Exception as exc:
            print(
                "[Bees distributed POCA shadow] broker unavailable: "
                f"{type(exc).__name__}: {exc}; remote_gradients_applied=0",
                flush=True,
            )
            return None
        if not workers_active:
            return None
        _ACTIVE_PILOT = True
    coordinator = None
    snapshot_id = None
    try:
        started_at = time.monotonic()
        revision = AuthoritativeGradientApplier(
            optimizer, max_optimizer_lag=0
        ).revision
        coordinator = GradientJobCoordinator(
            exchange, session_id=session_id, max_optimizer_lag=0,
        )
        snapshot_payload = encode_payload(
            snapshot_model(optimizer, optimizer_revision=revision)
        )
        snapshot_id = coordinator.publish_snapshot(
            snapshot_payload, optimizer_revision=revision,
        )
        if not snapshot_id:
            raise RuntimeError("Gradient snapshot registry has no free slot")
        trial = ShadowTrial(
            coordinator, snapshot_id, behavior_id, behavior_spec,
            revision, started_at, len(snapshot_payload), n,
            LocalGradientReference(), time.monotonic() - started_at,
        )
        with _LOCK:
            _PENDING_REFERENCE = trial.reference
        submit_shadow_minibatch(trial, first_minibatch)
        return trial
    except Exception as exc:
        if coordinator is not None and snapshot_id is not None:
            try:
                coordinator.retire_snapshot(snapshot_id)
            except Exception:
                pass
        with _LOCK:
            _ACTIVE_PILOT = False
            _PENDING_REFERENCE = None
        print(
            "[Bees distributed POCA shadow] submission failed: "
            f"{type(exc).__name__}: {exc}; remote_gradients_applied=0",
            flush=True,
        )
        return None
