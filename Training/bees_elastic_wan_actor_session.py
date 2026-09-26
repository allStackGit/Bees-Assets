"""Actor-session behavior that adapts WAN trajectory segmentation to the live elastic topology."""

from __future__ import annotations

import math
import queue
import threading
import time
from typing import Any, Mapping, Optional

import bees_wan_actor_worker as worker


class ElasticActorSession(worker.ActorSession):
    """Legacy WAN actor session plus topology-aware rollout-horizon adjustment.

    Policy/control changes retain the base class' strict behavior: in-flight old-policy actions are
    drained, partial local trajectories are discarded, and the new central policy/control state is
    applied before more rollouts are accepted. A topology-only change is different: it changes only
    trajectory segmentation, so no environment reset or policy swap is needed.
    """

    def __init__(self, *args: Any, **kwargs: Any) -> None:
        super().__init__(*args, **kwargs)
        self.topology_epoch = -1
        self._claim_keeper_stop = threading.Event()
        self._claim_keeper: Optional[threading.Thread] = None

    def start(self) -> None:
        if (
            getattr(self.client, "actor_key", None) is None
            or getattr(self.client, "requested_actor_id", None) is not None
        ):
            super().start()
            return

        lease_seconds = self.session.get("actor_lease_seconds")
        if (
            not isinstance(lease_seconds, (int, float))
            or isinstance(lease_seconds, bool)
            or not math.isfinite(float(lease_seconds))
            or float(lease_seconds) <= 0
        ):
            raise RuntimeError("Elastic WAN session has an invalid actor lease duration")

        interval = min(5.0, max(0.05, float(lease_seconds) / 3.0))
        if interval >= float(lease_seconds):
            raise RuntimeError("Elastic WAN actor lease is too short to renew safely")
        self._claim_keeper_stop.clear()
        self._claim_keeper = threading.Thread(
            target=self._maintain_claim,
            args=(float(lease_seconds), interval),
            name=f"bees-wan-claim-{self.actor_id}",
            daemon=True,
        )
        self._claim_keeper.start()
        try:
            super().start()
        except BaseException:
            self._stop_claim_keeper()
            raise

    def _maintain_claim(self, lease_seconds: float, interval: float) -> None:
        lease_deadline = time.monotonic() + lease_seconds
        while not self._claim_keeper_stop.wait(interval):
            try:
                claimed_actor_id = self.client.claim(self.session_id)
                if claimed_actor_id != self.actor_id:
                    raise RuntimeError(
                        f"Elastic WAN claim moved from actor {self.actor_id} "
                        f"to actor {claimed_actor_id} during session startup"
                    )
                lease_deadline = time.monotonic() + lease_seconds
            except worker.BrokerUnavailable as exc:
                if time.monotonic() >= lease_deadline:
                    self._thread_error.put(
                        TimeoutError(
                            f"Elastic WAN actor claim could not be renewed before lease expiry: {exc}"
                        )
                    )
                    return
                if self._claim_keeper_stop.wait(min(5.0, interval)):
                    return
            except worker.BrokerSessionChanged:
                self._session_changed.set()
                return
            except BaseException as exc:
                self._thread_error.put(exc)
                return

    def _stop_claim_keeper(self) -> None:
        self._claim_keeper_stop.set()
        if self._claim_keeper is not None:
            self._claim_keeper.join(timeout=2.0)
            self._claim_keeper = None

    def close(self) -> None:
        self._stop_claim_keeper()
        super().close()

    def _heartbeat(self) -> None:
        self.client.reset_ack(
            {
                "session_id": self.session_id,
                "actor_id": self.actor_id,
                "control_epoch": self.control_epoch,
            }
        )

    def _apply_central_throughput(self, state: Mapping[str, Any]) -> None:
        consumed = state.get("consumed_steps_by_actor")
        if not isinstance(consumed, Mapping):
            raise RuntimeError("Elastic WAN central state is missing consumed-step metrics")
        value = consumed.get(str(self.actor_id), consumed.get(self.actor_id, 0))
        if not isinstance(value, int) or isinstance(value, bool) or value < 0:
            raise RuntimeError("Elastic WAN central state has malformed consumed-step metrics")
        with self._throughput_lock:
            self._learner_consumed_steps_total = value
        self._write_throughput_metrics()

    def _apply_live_rollout_horizons(self, state: Mapping[str, Any]) -> None:
        if self.manager is None:
            return
        local_envs = state.get("local_envs")
        remote_envs = state.get("remote_envs")
        topology_epoch = state.get("topology_epoch")
        if (
            not isinstance(local_envs, int)
            or isinstance(local_envs, bool)
            or local_envs < 0
            or not isinstance(remote_envs, int)
            or isinstance(remote_envs, bool)
            or remote_envs < 0
            or not isinstance(topology_epoch, int)
            or isinstance(topology_epoch, bool)
            or topology_epoch < 0
        ):
            raise RuntimeError("Elastic WAN central state has malformed topology metadata")

        total_envs = local_envs + remote_envs
        if total_envs <= 0:
            raise RuntimeError("Elastic WAN actor received a topology with no rollout environments")
        from mlagents.trainers.behavior_id_utils import BehaviorIdentifiers

        horizons = {}
        for behavior_id, manager in self.manager.agent_managers.items():
            parsed = BehaviorIdentifiers.from_name_behavior_id(behavior_id)
            trainer_settings = self.central_run_options.behaviors[parsed.brain_name]
            horizon = worker.rollout_horizon(trainer_settings, total_envs)
            manager._max_trajectory_length = horizon
            horizons[behavior_id] = horizon

        changed = horizons != self._rollout_horizons or total_envs != self.total_envs
        self.total_envs = total_envs
        self.topology_epoch = topology_epoch
        self._rollout_horizons = horizons
        if changed:
            print(
                f"[Bees WAN actor] topology local_envs={local_envs} remote_envs={remote_envs} "
                f"total_envs={total_envs} rollout_horizons={horizons}."
            )

    def _synchronize_state(self, *, require_policy: bool = False) -> None:
        # Base synchronization handles policy/control freshness and clears state_changed. Fetch one
        # immediate state afterward so topology metadata is applied atomically before rollout resumes.
        super()._synchronize_state(require_policy=require_policy)
        state = self.client.state(
            self.session_id,
            self.policy_epoch,
            self.control_epoch,
            0.0,
        )
        self._apply_live_rollout_horizons(state)
        self._apply_central_throughput(state)
        self._heartbeat()
        self._state_changed.clear()

    def _watch_loop(self) -> None:
        while not self._upload_stop.is_set() and not self.stop.is_set():
            try:
                state = self.client.state(
                    self.session_id,
                    self.policy_epoch,
                    self.control_epoch,
                    worker.DEFAULT_STATE_WAIT_SECONDS,
                )
                remote_control_epoch = int(state.get("control_epoch", -1))
                self._apply_central_throughput(state)
                changed = (
                    int(state.get("policy_epoch", -1)) != self.policy_epoch
                    or remote_control_epoch != self.control_epoch
                    or int(state.get("topology_epoch", -1)) != self.topology_epoch
                )
                # Repeated authenticated acknowledgements are also the liveness heartbeat. Do not
                # acknowledge an epoch the main actor loop has not applied yet.
                if remote_control_epoch == self.control_epoch:
                    self._heartbeat()
                if changed:
                    self._state_changed.set()
                    while (
                        self._state_changed.is_set()
                        and not self._upload_stop.is_set()
                        and not self.stop.is_set()
                    ):
                        time.sleep(0.05)
            except worker.BrokerSessionChanged:
                self._session_changed.set()
                return
            except worker.BrokerUnavailable:
                time.sleep(1.0)
            except BaseException as exc:
                self._thread_error.put(exc)
                return
