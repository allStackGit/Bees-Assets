"""Opt-in shadow-only WAN pilot for concurrent remote Bees POCA gradients.

BEES_POCA_SHADOW_JOBS=N schedules N distinct PPO minibatches (up to eight)
against ONE frozen actor/critic snapshot. Exeter's normal PPO loop continues
unchanged. A daemon records completion/latency and NEVER applies gradients.
"""

from __future__ import annotations

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



def capture_first_local_gradient(optimizer: Any, stats: dict) -> None:
    """PPO-thread capture of gradients for the first shadowed minibatch."""
    global _PENDING_REFERENCE
    with _LOCK:
        reference = _PENDING_REFERENCE
        _PENDING_REFERENCE = None
    if reference is not None:
        reference.capture(optimizer, stats)


def _collect_async(
    coordinator: GradientJobCoordinator, snapshot_id: str,
    job_ids: list[str], optimizer_revision: int, started_at: float,
    prepared_seconds: float, minibatch_bytes: int, snapshot_bytes: int,
    reference: LocalGradientReference,
) -> None:
    global _ACTIVE_PILOT, _PENDING_REFERENCE
    results = []
    try:
        first_remote = None
        profiles = []
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
        parity = (reference.compare(first_remote)
                  if first_remote is not None else "parity=no_matching_result")
        cache_hits = sum(bool(item.get("snapshot_cached")) for item in profiles)
        worker_total = sum(float(item.get("worker_before_upload_seconds", 0.0))
                           for item in profiles)
        claim_bytes = sum(int(item.get("claim_encoded_bytes", 0)) for item in profiles)
        fetched_bytes = sum(int(item.get("snapshot_encoded_bytes", 0)) for item in profiles)
        elapsed = time.monotonic() - started_at
        print(
            "[Bees distributed POCA shadow] "
            f"jobs={len(job_ids)} completed={accepted} "
            f"status={','.join(status for status, _ in results)} "
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
            f"queue_sum_s={queue_sum:.3f} queue_max_s={queue_max:.3f} "
            f"distinct_workers={len(worker_names)} "
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


def start_shadow_trial(
    optimizer: Any, behavior_id: str, behavior_spec: Any,
    tensor_cache: dict, shuffled_indices: Any, batch_size: int,
    select_minibatch: Any,
) -> int:
    """Snapshot and submit a small group of jobs once per active pilot.

    Called only at a PPO update boundary after the epoch's shuffle but BEFORE
    local Adam starts changing model parameters. Errors cannot stop PPO.
    """
    global _ACTIVE_PILOT, _PENDING_REFERENCE
    try:
        n = _requested_jobs()
    except (TypeError, ValueError) as exc:
        print(f"[Bees distributed POCA shadow] disabled: {exc}", flush=True)
        return 0
    if not n:
        return 0
    with _LOCK:
        if _ACTIVE_PILOT or _ACTIVE_BROKER is None:
            return 0
        exchange, session_id = _ACTIVE_BROKER
        if exchange.metrics()["gradient_workers_active"] == 0:
            return 0
        _ACTIVE_PILOT = True
    coordinator = None
    snapshot_id = None
    jobs = []
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
            snapshot_payload, optimizer_revision=revision
        )
        if not snapshot_id:
            raise RuntimeError("Gradient snapshot registry has no free slot")
        batch_total_bytes = 0
        for index in range(min(n, len(shuffled_indices) // batch_size)):
            indices = shuffled_indices[index * batch_size:(index + 1) * batch_size]
            minibatch = select_minibatch(tensor_cache, indices)
            encoded = encode_cpu_minibatch(minibatch)
            batch_total_bytes += len(encoded)
            job_id = coordinator.submit(
                snapshot_id, behavior_id=behavior_id,
                behavior_spec=behavior_spec, minibatch_bytes=encoded,
            )
            if job_id is None:
                break
            jobs.append(job_id)
        if not jobs:
            raise RuntimeError("Could not submit any remote gradient jobs")
        reference = LocalGradientReference()
        with _LOCK:
            _PENDING_REFERENCE = reference
        prepared = time.monotonic() - started_at
        print(
            "[Bees distributed POCA shadow] "
            f"submitted={len(jobs)} revision={revision} "
            f"prepare_s={prepared:.3f} "
            f"snapshot_mib={len(snapshot_payload) / 1048576:.2f} "
            f"minibatches_mib={batch_total_bytes / 1048576:.2f} "
            "remote_gradients_applied=0",
            flush=True,
        )
        threading.Thread(
            target=_collect_async,
            args=(coordinator, snapshot_id, jobs, revision, started_at,
                  prepared, batch_total_bytes, len(snapshot_payload), reference),
            name="bees-distributed-poca-shadow-collector", daemon=True,
        ).start()
        return len(jobs)
    except Exception as exc:
        if coordinator is not None:
            for job_id in jobs:
                coordinator.cancel(job_id)
            if snapshot_id is not None:
                coordinator.retire_snapshot(snapshot_id)
        with _LOCK:
            _ACTIVE_PILOT = False
            _PENDING_REFERENCE = None
        print(
            "[Bees distributed POCA shadow] submission failed: "
            f"{type(exc).__name__}: {exc}; remote_gradients_applied=0",
            flush=True,
        )
        return 0
