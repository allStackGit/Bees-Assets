"""Actor-session behavior that adapts WAN trajectory segmentation to the live elastic topology."""

from __future__ import annotations

import time
from typing import Any, Mapping

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

    def _heartbeat(self) -> None:
        self.client.reset_ack(
            {
                "session_id": self.session_id,
                "actor_id": self.actor_id,
                "control_epoch": self.control_epoch,
            }
        )

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
