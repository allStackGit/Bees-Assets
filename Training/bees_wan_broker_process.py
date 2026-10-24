"""Isolate the WAN broker's HTTP ingestion from the CUDA/PPO learner interpreter.

The broker owns the actor leases, control epochs, policy snapshots, bounded trajectory queue
and WAN upload HTTP server in a spawned Python process. The trainer retains optimizer and
checkpoint ownership and receives only selected, current-policy trajectory batches at drain
boundaries. No WAN HTTP handler, zlib decoder, or incoming pickle.load runs in the learner.
"""
from __future__ import annotations

import multiprocessing
import mmap
import os
import pickle
import queue
import secrets
import threading
import time
from multiprocessing.managers import BaseManager
from multiprocessing.shared_memory import SharedMemory
from typing import Any

import bees_wan_actor_training as base
from bees_elastic_wan_training import ElasticWanBroker


# Only coherent broker operations are exposed. In particular, no raw queue or
# cross-process lock is shared with the trainer. Large trajectory objects cross
# the process boundary only when the trainer explicitly drains accepted batches.
_BROKER_METHODS = (
    "start",
    "close",
    "active_actor_snapshot",
    "registered_actor_ids",
    "merged_behavior_specs",
    "set_reference_behavior_specs",
    "initialize_control",
    "wait_for_minimum_registrations",
    "request_reset",
    "request_parameters",
    "wait_for_change",
    "next_consumable_batch",
    "next_trajectory_cohort",
    "drain_current_batches",
    "drain_to_shared_memory",
    "next_to_shared_memory",
    "release_shared_memory",
    "has_pending_remote_experiences",
    "release_queued_batch",
    "record_consumed_batch",
    "validate_versioned_trajectory",
    "record_versioned_trajectory_consumed",
    "record_ppo_trajectory_delivered",
    "record_versioned_trajectory_discarded",
    "release_trajectory_reservation",
    "is_ghost_trajectory",
    "observe_learning_team",
    "publish_policy_wire",
    "policy_publication_epoch",
    "complete_policy_cycle",
    "observe_trainer_step",
    "observe_optimizer_busy_seconds",
    "observe_pipeline",
    "report_capacity",
)


def _exit_with_parent() -> None:
    # A forced learner termination must not leave an orphan WAN listener owning
    # the broker port. Python's spawn parent sentinel detects the actual parent
    # process lifetime rather than guessing from a possibly reused PID.
    parent = multiprocessing.parent_process()
    if parent is None:
        return
    while parent.is_alive():
        time.sleep(2.0)
    os._exit(1)


class _SharedMemoryWanBroker(ElasticWanBroker):
    """Own shared-memory buffers until the learner acknowledges consumption."""

    def __init__(self, *args, **kwargs):
        super().__init__(*args, **kwargs)
        self._shared_memory_transfers = {}
        self._shared_memory_lock = threading.Lock()
        self._shutdown_ingestion = False

    def begin_graceful_shutdown(self):
        """Stop admitting trajectories while keeping checkpoint/control RPC available."""
        with self._condition:
            if self._shutdown_ingestion:
                return
            self._shutdown_ingestion = True
            self._condition.notify_all()
        print(
            "[Bees WAN] managed learner stop detected; quiescing remote trajectory "
            "ingestion before PPO checkpoint finalization.",
            flush=True,
        )

    def can_accept_upload_without_decoding(self):
        with self._condition:
            if self._shutdown_ingestion:
                return False
        return super().can_accept_upload_without_decoding()

    def submit_trajectory_batch(self, payload, *, timings=None):
        with self._condition:
            if self._shutdown_ingestion:
                # The HTTP handler maps queue.Full to transient backpressure.
                # Remote workers retain their current payload until the new
                # broker session becomes available; no learner data is lost.
                raise queue.Full
        return super().submit_trajectory_batch(payload, timings=timings)

    def _export_to_shared_memory(self, batches):
        if not batches:
            return {"empty": True, "batches": 0, "bytes": 0}
        started = time.perf_counter()
        encoded = pickle.dumps(batches, protocol=pickle.HIGHEST_PROTOCOL)
        serialized_seconds = time.perf_counter() - started
        shared = SharedMemory(create=True, size=len(encoded))
        try:
            shared.buf[:len(encoded)] = encoded
            token = secrets.token_hex(16)
            with self._shared_memory_lock:
                self._shared_memory_transfers[token] = shared
        except BaseException:
            shared.close()
            shared.unlink()
            raise
        return {
            "token": token,
            "name": shared.name,
            "bytes": len(encoded),
            "batches": len(batches) if isinstance(batches, (list, tuple)) else 1,
            "serialization_seconds": serialized_seconds,
        }

    def drain_to_shared_memory(self, maximum: int):
        return self._export_to_shared_memory(self.drain_current_batches(maximum))

    def next_to_shared_memory(self, timeout_seconds: float):
        return self._export_to_shared_memory(
            self.next_consumable_batch(timeout_seconds)
        )

    def release_shared_memory(self, token: str):
        with self._shared_memory_lock:
            shared = self._shared_memory_transfers.pop(token)
        try:
            shared.close()
        finally:
            shared.unlink()

    def close(self):
        try:
            super().close()
        finally:
            with self._shared_memory_lock:
                remaining = list(self._shared_memory_transfers.values())
                self._shared_memory_transfers.clear()
            for shared in remaining:
                try:
                    shared.close()
                finally:
                    shared.unlink()


def _quiesce_when_learner_stops(broker):
    # This lives outside the learner interpreter, so it still reacts if the
    # main learner thread is blocked in an IPC call or model update.
    path = os.environ.get("BEES_TRAINING_STOP_FILE", "").strip()
    if not path:
        return
    while not os.path.isfile(path):
        with broker._condition:
            if broker._closed:
                return
        time.sleep(0.1)
    broker.begin_graceful_shutdown()


def _new_broker(options, run_options, auth_token, local_envs, threaded_mode, policy_lag):
    broker = _SharedMemoryWanBroker(
        options,
        run_options,
        auth_token,
        local_envs,
        threaded_mode=threaded_mode,
        max_policy_lag=policy_lag,
    )
    threading.Thread(
        target=_exit_with_parent,
        name="bees-wan-ingest-parent-owner",
        daemon=True,
    ).start()
    threading.Thread(
        target=_quiesce_when_learner_stops,
        args=(broker,),
        name="bees-wan-ingest-shutdown",
        daemon=True,
    ).start()
    return broker


class _BrokerManager(BaseManager):
    pass


_BrokerManager.register("create_broker", callable=_new_broker, exposed=_BROKER_METHODS)


class IsolatedElasticWanBroker:
    """Learner-side handle. Method calls act on the broker process's single state owner."""

    def __init__(
        self,
        options,
        run_options,
        auth_token: str,
        local_envs: int,
        *,
        threaded_mode: bool,
        max_policy_lag: int,
    ) -> None:
        self.max_policy_lag = int(max_policy_lag)
        self._drain_metrics = {
            "started": time.perf_counter(), "calls": 0, "batches": 0,
            "bytes": 0, "rpc_seconds": 0.0, "decode_seconds": 0.0,
        }
        # spawn is mandatory: Windows requires it, and fork after CUDA
        # initialization can poison the learner's GPU runtime.
        manager = _BrokerManager(
            address=None,
            authkey=secrets.token_bytes(32),
            ctx=multiprocessing.get_context("spawn"),
        )
        manager.start()
        self._manager = manager
        try:
            self._broker = manager.create_broker(
                options, run_options, auth_token, int(local_envs),
                bool(threaded_mode), self.max_policy_lag,
            )
            self._broker.start()
        except BaseException:
            manager.shutdown()
            raise
        print(
            f"[Bees WAN] isolated trajectory-ingestion broker process "
            f"pid={manager._process.pid} learner_pid={os.getpid()}.",
            flush=True,
        )

    def __getattr__(self, name: str) -> Any:
        return getattr(self._broker, name)

    def publish_policy(self, behavior_name: str, policy: Any) -> int:
        # Transfer the already serialized inference weights, not a CUDA-bearing
        # TorchPolicy or the learner's optimizer state.
        wire = dict(base._policy_wire_payload(policy))
        return self._broker.publish_policy_wire(
            behavior_name,
            base.encode_payload(wire),
            base._policy_identity_digest(wire),
        )

    def _transfer(self, method: str, *args) -> Any:
        started = time.perf_counter()
        descriptor = getattr(self._broker, method)(*args)
        rpc_elapsed = time.perf_counter() - started
        if descriptor.get("empty"):
            return ()
        token = descriptor["token"]
        shared = None
        view = None
        mapping = None
        decode_started = time.perf_counter()
        try:
            size = int(descriptor["bytes"])
            if os.name == "posix" and os.path.isdir("/dev/shm"):
                # Linux: map the POSIX shm file read-only without registering a
                # second independent resource_tracker owner. The broker alone
                # creates/unlinks the named segment.
                fd = os.open(
                    os.path.join("/dev/shm", descriptor["name"]), os.O_RDONLY
                )
                try:
                    mapping = mmap.mmap(fd, size, access=mmap.ACCESS_READ)
                finally:
                    os.close(fd)
                result = pickle.loads(mapping)
            else:
                shared = SharedMemory(name=descriptor["name"], create=False)
                view = shared.buf[:size]
                result = pickle.loads(view)
        finally:
            if view is not None:
                view.release()
            if mapping is not None:
                mapping.close()
            if shared is not None:
                shared.close()
            # Owner process unlinks the allocation even if deserialization fails.
            self._broker.release_shared_memory(token)

        finished = time.perf_counter()
        elapsed = finished - started
        # The blocking next_to_shared_memory RPC includes waiting for a
        # producer; only measure non-blocking drains as ingestion overhead.
        if method == "drain_to_shared_memory":
            metrics = self._drain_metrics
            metrics["calls"] += 1
            metrics["batches"] += int(descriptor["batches"])
            metrics["bytes"] += int(descriptor["bytes"])
            metrics["rpc_seconds"] += rpc_elapsed
            metrics["decode_seconds"] += finished - decode_started
            interval = finished - metrics["started"]
            if interval >= 30.0:
                print(
                    "[Bees WAN shared-memory drain] "
                    f"calls={metrics['calls']} batches={metrics['batches']} "
                    f"bytes={metrics['bytes']} "
                    f"rpc_s={metrics['rpc_seconds']:.3f} "
                    f"mapping_and_unpickle_s={metrics['decode_seconds']:.3f}",
                    flush=True,
                )
                self._drain_metrics = {
                    "started": finished, "calls": 0, "batches": 0,
                    "bytes": 0, "rpc_seconds": 0.0, "decode_seconds": 0.0,
                }
        if elapsed >= 0.5:
            print(
                f"[Bees WAN IPC] operation={method} seconds={elapsed:.3f} "
                f"serialize={descriptor['serialization_seconds']:.3f} "
                f"bytes={descriptor['bytes']} batches={descriptor['batches']}",
                flush=True,
            )
        return result

    def drain_current_batches(self, maximum: int) -> Any:
        return self._transfer("drain_to_shared_memory", maximum)

    def next_consumable_batch(self, timeout_seconds: float) -> Any:
        return self._transfer("next_to_shared_memory", timeout_seconds)

    def close(self) -> None:
        try:
            self._broker.close()
        finally:
            self._manager.shutdown()
