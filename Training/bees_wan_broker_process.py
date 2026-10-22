"""Isolate the WAN broker's HTTP ingestion from the CUDA/PPO learner interpreter.

The broker owns the actor leases, control epochs, policy snapshots, bounded trajectory queue
and WAN upload HTTP server in a spawned Python process. The trainer retains optimizer and
checkpoint ownership and receives only selected, current-policy trajectory batches at drain
boundaries. No WAN HTTP handler, zlib decoder, or incoming pickle.load runs in the learner.
"""
from __future__ import annotations

import multiprocessing
import os
import secrets
import threading
import time
from multiprocessing.managers import BaseManager
from typing import Any

import bees_wan_actor_training as base


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


def _new_broker(options, run_options, auth_token, local_envs, threaded_mode, policy_lag):
    from bees_elastic_wan_training import ElasticWanBroker

    broker = ElasticWanBroker(
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
        result = getattr(self._broker, method)(*args)
        seconds = time.perf_counter() - started
        if seconds >= 1.0:
            count = (
                len(result) if isinstance(result, (list, tuple)) else
                (1 if isinstance(result, dict) else 0)
            )
            print(
                f"[Bees WAN IPC] operation={method} seconds={seconds:.3f} "
                f"batches={count}",
                flush=True,
            )
        return result

    def drain_current_batches(self, maximum: int) -> Any:
        return self._transfer("drain_current_batches", maximum)

    def next_consumable_batch(self, timeout_seconds: float) -> Any:
        return self._transfer("next_consumable_batch", timeout_seconds)

    def close(self) -> None:
        try:
            self._broker.close()
        finally:
            self._manager.shutdown()
