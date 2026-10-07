"""Actor-session behavior that adapts WAN trajectory segmentation to the live elastic topology."""

from __future__ import annotations

import math
import os
import queue
import time
from pathlib import Path
from typing import Any, Mapping

import bees_wan_actor_worker as worker


WORKER_ENVS_TARGET_ENV = "BEES_TRAINING_WORKER_ENVS_FILE"
MAX_DYNAMIC_ENVS = 64
WORKER_CLOSE_SECONDS = 10.0


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
        self.policy_cycle = -1
        self.learner_step = -1
        self.optimizer_busy_seconds_total = None
        self._last_consumed_sample = None
        target_path = os.environ.get(WORKER_ENVS_TARGET_ENV, "").strip()
        self._env_target_path = (
            Path(target_path).expanduser().resolve() if target_path else None
        )
        self._capacity_registration_pending = False
        self._resize_failed_target = None
        self._resize_failure = None
        self._registered_env_count = int(self.env_count)
        self._downscale_registration_pending = False

    def _throughput_extra_metrics(self) -> Mapping[str, object]:
        failure = self._resize_failure
        payload: dict[str, object] = {
            # Optimizer-visible capacity must not move until Exeter has accepted the
            # corresponding worker range. Local downscale can lead this value while
            # pre-downscale trajectory uploads finish.
            "env_count": int(self._registered_env_count),
        }
        if self.policy_cycle >= 0:
            payload["policy_cycle"] = int(self.policy_cycle)
        if self.learner_step >= 0:
            payload["learner_step_total"] = int(self.learner_step)
        optimizer_busy = getattr(
            self,
            "optimizer_busy_seconds_total",
            None,
        )
        if (
            isinstance(optimizer_busy, (int, float))
            and not isinstance(optimizer_busy, bool)
            and math.isfinite(float(optimizer_busy))
            and float(optimizer_busy) >= 0.0
        ):
            payload["optimizer_busy_seconds_total"] = float(optimizer_busy)
        if isinstance(failure, Mapping):
            payload.update(
                {
                    "env_resize_failed_target": int(failure["target_envs"]),
                    "env_resize_error": str(failure["error"]),
                    "env_resize_failure_unix_seconds": float(failure["unix_seconds"]),
                }
            )
        return payload

    def _record_resize_failure(self, target_envs: int, exc: Exception) -> None:
        self._resize_failed_target = int(target_envs)
        self._resize_failure = {
            "target_envs": int(target_envs),
            "error": f"{type(exc).__name__}: {' '.join(str(exc).split())[:240]}",
            "unix_seconds": time.time(),
        }
        self._write_throughput_metrics(force=True)
        print(
            f"[Bees WAN actor] live resize to {target_envs} envs rejected; "
            f"continuing with {self.env_count}: {self._resize_failure['error']}",
            flush=True,
        )

    def _clear_resize_failure(self) -> None:
        if self._resize_failure is None and self._resize_failed_target is None:
            return
        self._resize_failure = None
        self._resize_failed_target = None
        self._write_throughput_metrics(force=True)

    def _desired_env_count(self) -> int:
        path = self._env_target_path
        if path is None:
            return int(self.env_count)
        try:
            raw = path.read_text(encoding="ascii").strip()
        except OSError:
            # The supervisor rewrites this live-control file atomically. On Windows a
            # replacement can briefly lose a race with a reader because of filesystem,
            # antivirus, or indexing handles. Missing/unreadable for one reconciliation
            # tick therefore means "keep the current capacity and retry", not "kill the
            # WAN actor session". Malformed content below remains a hard error.
            return int(self.env_count)
        try:
            value = int(raw)
        except ValueError as exc:
            raise RuntimeError(f"live worker env target is not an integer: {raw!r}") from exc
        if not 1 <= value <= MAX_DYNAMIC_ENVS:
            raise RuntimeError(
                f"live worker env target must be in 1-{MAX_DYNAMIC_ENVS}, got {value}"
            )
        return value

    def _register_current_capacity(self) -> bool:
        if not self._behavior_specs:
            return False
        self.client.env_count = int(self.env_count)
        registration = {
            "session_id": self.session_id,
            "actor_id": self.actor_id,
            "control_epoch": self.control_epoch,
            "behavior_specs": dict(self._behavior_specs),
        }

        def register() -> None:
            try:
                self.client.register(registration)
            except worker.BrokerClaimRequired:
                reclaimed_actor_id = self.client.claim(self.session_id)
                if int(reclaimed_actor_id) != int(self.actor_id):
                    raise worker.BrokerActorSlotChanged(
                        "expired actor claim moved from slot "
                        f"{self.actor_id} to {reclaimed_actor_id}"
                    )
                self.client.register(registration)

        try:
            self._retry_broker_unavailable(
                register,
                label="environment-capacity registration",
            )
        except worker.BrokerStaleActor:
            self._capacity_registration_pending = True
            self._state_changed.set()
            return False
        self._capacity_registration_pending = False
        self._registered_env_count = int(self.env_count)
        return True

    def _behavior_specs_match(self, candidate: Mapping[str, Any]) -> bool:
        if set(candidate) != set(self._behavior_specs):
            return False
        return all(
            worker.wan._behavior_spec_signature(candidate[name])
            == worker.wan._behavior_spec_signature(self._behavior_specs[name])
            for name in candidate
        )

    def _cleanup_worker_agent_state(self, global_worker_id: int) -> None:
        prefix = f"agent_{int(global_worker_id)}-"
        for agent_manager in self.manager.agent_managers.values():
            agent_ids = set()
            for name in (
                "_experience_buffers",
                "_last_take_action_outputs",
                "_last_step_result",
                "_episode_steps",
                "_episode_rewards",
            ):
                values = getattr(agent_manager, name, None)
                if isinstance(values, Mapping):
                    agent_ids.update(
                        key for key in values if isinstance(key, str) and key.startswith(prefix)
                    )
            for name in ("_current_group_obs", "_group_status"):
                groups = getattr(agent_manager, name, None)
                if isinstance(groups, Mapping):
                    for values in groups.values():
                        if isinstance(values, Mapping):
                            agent_ids.update(
                                key
                                for key in values
                                if isinstance(key, str) and key.startswith(prefix)
                            )
            for agent_id in agent_ids:
                agent_manager._clean_agent_data(agent_id)
                agent_manager._clear_group_status_and_obs(agent_id)

    def _close_tail_worker(self) -> None:
        from mlagents.trainers.subprocess_env_manager import EnvironmentCommand

        manager = self.manager
        local_worker_id = len(manager.env_workers) - 1
        target = manager.env_workers[local_worker_id]
        if target.waiting:
            raise RuntimeError("cannot retire a Unity worker with an in-flight step")

        target.request_close()
        buffered = []
        closed = False
        deadline = time.monotonic() + WORKER_CLOSE_SECONDS
        while time.monotonic() < deadline:
            try:
                response = manager.step_queue.get(
                    timeout=min(0.25, max(0.01, deadline - time.monotonic()))
                )
            except queue.Empty:
                if not target.process.is_alive():
                    break
                continue
            if int(response.worker_id) == local_worker_id:
                if response.cmd == EnvironmentCommand.CLOSED:
                    closed = True
                    break
                # No step is in flight for an intentionally retired worker. Discard only its
                # terminal shutdown/error notifications; its unfinished trajectory state was
                # removed explicitly before this method was called.
                continue
            buffered.append(response)

        if target.process.is_alive():
            target.process.join(timeout=0.5)
        if target.process.is_alive():
            target.process.terminate()
            target.process.join(timeout=2.0)
        if target.process.is_alive():
            for response in buffered:
                manager.step_queue.put(response)
            raise RuntimeError(f"Unity worker {local_worker_id} did not stop during live resize")

        # Remove any late shutdown notification from the retired worker while preserving every
        # response belonging to the workers that stay alive.
        while True:
            try:
                response = manager.step_queue.get_nowait()
            except queue.Empty:
                break
            if int(response.worker_id) != local_worker_id:
                buffered.append(response)
            elif response.cmd == EnvironmentCommand.CLOSED:
                closed = True
        for response in buffered:
            manager.step_queue.put(response)

        target.closed = True
        manager.workers_alive = max(0, int(manager.workers_alive) - 1)
        manager.env_workers.pop()
        manager.recent_restart_timestamps.pop()
        manager.restart_counts.pop()

        # ML-Agents launches Unity with start_new_session=True. If the Python environment
        # worker had to be terminated while UnityEnvironment.close() was still waiting,
        # the detached Unity player survives unless we reclaim this exact retired port.
        worker._terminate_detached_actor_unity(
            self.env_path,
            self.local_base_port + local_worker_id,
            reason=f"live resize worker {local_worker_id}",
            port_count=1,
        )
        if not closed:
            # Forced termination is still a successful retirement as long as the process is gone;
            # there is no remaining worker that can emit stale steps for this worker id.
            pass

    def _live_resize_startup_timeout_seconds(self) -> float:
        settings = getattr(
            getattr(self, "central_run_options", None),
            "env_settings",
            None,
        )
        if settings is None:
            settings = getattr(
                getattr(self.manager, "run_options", None),
                "env_settings",
                None,
            )
        try:
            timeout_wait = float(getattr(settings, "timeout_wait", 60.0))
        except (TypeError, ValueError):
            timeout_wait = 60.0
        # ML-Agents' Unity communicator gets timeout_wait seconds to establish the
        # player connection. Give the subsequent RESET/spec handshake a small margin,
        # but never allow a live capacity probe to block rollout indefinitely.
        return max(15.0, timeout_wait + 10.0)

    def _recv_scale_up_response(
        self,
        candidate,
        *,
        deadline: float,
        expected_command,
        local_worker_id: int,
        phase: str,
    ):
        while not self.stop.is_set():
            remaining = deadline - time.monotonic()
            if remaining <= 0.0:
                raise TimeoutError(
                    f"new Unity worker {local_worker_id} did not complete {phase} "
                    f"within {self._live_resize_startup_timeout_seconds():.1f}s"
                )
            if candidate.conn.poll(min(0.25, remaining)):
                response = candidate.recv()
                if response.cmd != expected_command:
                    raise RuntimeError(
                        f"new Unity worker {local_worker_id} returned {response.cmd!r} "
                        f"instead of {expected_command.name} during {phase}"
                    )
                return response
            if not candidate.process.is_alive():
                raise RuntimeError(
                    f"new Unity worker {local_worker_id} exited during {phase}"
                )
            self._report_runtime_progress()
            self._write_throughput_metrics()
        raise RuntimeError(
            f"new Unity worker {local_worker_id} startup interrupted"
        )

    def _retire_unmanaged_scale_up_worker(self, candidate, local_worker_id: int) -> None:
        """Retire a failed candidate that was never admitted to manager.env_workers."""

        manager = self.manager
        try:
            candidate.request_close()
        except Exception:
            pass
        try:
            candidate.process.join(timeout=0.5)
        except Exception:
            pass
        if candidate.process.is_alive():
            candidate.process.terminate()
            candidate.process.join(timeout=2.0)
        if candidate.process.is_alive():
            raise RuntimeError(
                f"Unity worker {local_worker_id} did not stop after failed live scale-up"
            )

        # A failed ML-Agents subprocess publishes ENV_EXITED/CLOSED on the shared
        # manager queue even though this candidate was never admitted to env_workers.
        # Remove only that candidate's notifications before ordinary manager stepping
        # resumes; preserve every response from the existing live environments.
        buffered = []
        quiet_until = time.monotonic() + 0.25
        while time.monotonic() < quiet_until:
            try:
                response = manager.step_queue.get(timeout=0.05)
            except queue.Empty:
                continue
            if int(response.worker_id) != int(local_worker_id):
                buffered.append(response)
        for response in buffered:
            manager.step_queue.put(response)

        try:
            candidate.conn.close()
        except Exception:
            pass
        worker._terminate_detached_actor_unity(
            self.env_path,
            self.local_base_port + local_worker_id,
            reason=f"failed live resize worker {local_worker_id}",
            port_count=1,
        )

    def _scale_up_one(self, requested_target: int) -> bool:
        from mlagents.trainers.env_manager import EnvironmentStep
        from mlagents.trainers.subprocess_env_manager import EnvironmentCommand

        manager = self.manager
        local_worker_id = len(manager.env_workers)
        if local_worker_id >= MAX_DYNAMIC_ENVS:
            return False

        # Keep the candidate outside manager.env_workers until it has completed
        # RESET + behavior-spec validation. This prevents a half-started worker from
        # participating in ordinary rollout or ML-Agents restart handling.
        new_worker = manager.create_worker(
            local_worker_id,
            manager.step_queue,
            manager.env_factory,
            manager.run_options,
        )
        admitted = False
        previous_count = int(self.env_count)
        try:
            timeout_seconds = self._live_resize_startup_timeout_seconds()
            deadline = time.monotonic() + timeout_seconds

            # Match normal SubprocessEnvManager startup ordering: establish the current
            # environment parameters and reset the Unity instance before inspecting its
            # behavior specifications. Bees assigns episode/team behavior state during reset,
            # so a pre-reset spec is not the contract the live actor is actually training on.
            parameters = manager.env_parameters
            if parameters is not None:
                new_worker.send(EnvironmentCommand.ENVIRONMENT_PARAMETERS, parameters)
            new_worker.send(EnvironmentCommand.RESET, self._current_env_config)
            reset_response = self._recv_scale_up_response(
                new_worker,
                deadline=deadline,
                expected_command=EnvironmentCommand.RESET,
                local_worker_id=local_worker_id,
                phase="RESET",
            )

            new_worker.send(EnvironmentCommand.BEHAVIOR_SPECS)
            specs_response = self._recv_scale_up_response(
                new_worker,
                deadline=deadline,
                expected_command=EnvironmentCommand.BEHAVIOR_SPECS,
                local_worker_id=local_worker_id,
                phase="behavior-spec validation",
            )
            candidate_specs = specs_response.payload
            if (
                not isinstance(candidate_specs, Mapping)
                or not self._behavior_specs_match(candidate_specs)
            ):
                raise RuntimeError(
                    f"new Unity worker {local_worker_id} behavior specifications do not match "
                    "the live actor session after reset"
                )

            # Only now make the worker visible to ML-Agents. Existing environments were
            # untouched throughout candidate startup, and a failed candidate has no agent
            # state to unwind.
            manager.env_workers.append(new_worker)
            manager.recent_restart_timestamps.append([])
            manager.restart_counts.append(0)
            manager.workers_alive += 1
            admitted = True

            initial = EnvironmentStep(reset_response.payload, local_worker_id, {}, {})
            mapped = worker._remap_step(initial, self.worker_offset)
            new_worker.previous_step = mapped
            manager.process_steps([mapped])

            self.env_count = previous_count + 1
            if not self._register_current_capacity():
                self._cleanup_worker_agent_state(self.worker_offset + local_worker_id)
                self._close_tail_worker()
                self.env_count = previous_count
                self.client.env_count = previous_count
                return False
            self._clear_resize_failure()
            self._report_env_count_changed()
            self._write_throughput_metrics(force=True)
            print(
                f"[Bees WAN actor] scaled Unity environments {previous_count}->{self.env_count}; "
                "existing workers remained running.",
                flush=True,
            )
            return True
        except Exception as exc:
            cleanup_error = None
            try:
                if admitted and manager.env_workers and manager.env_workers[-1] is new_worker:
                    self._cleanup_worker_agent_state(
                        self.worker_offset + local_worker_id
                    )
                    if not new_worker.waiting:
                        self._close_tail_worker()
                    else:
                        new_worker.request_close()
                elif not admitted:
                    self._retire_unmanaged_scale_up_worker(
                        new_worker,
                        local_worker_id,
                    )
            except Exception as cleanup_exc:
                cleanup_error = cleanup_exc

            self.env_count = previous_count
            self.client.env_count = previous_count
            if cleanup_error is not None:
                raise RuntimeError(
                    "live resize candidate failed and could not be retired cleanly: "
                    f"{type(cleanup_error).__name__}: {cleanup_error}"
                ) from cleanup_error
            self._record_resize_failure(int(requested_target), exc)
            return False

    def _scale_down_one(self) -> bool:
        if self.env_count <= 1:
            return False
        manager = self.manager
        local_worker_id = len(manager.env_workers) - 1
        target = manager.env_workers[local_worker_id]
        if target.waiting:
            return False

        previous_count = int(self.env_count)
        self._cleanup_worker_agent_state(self.worker_offset + local_worker_id)
        self._close_tail_worker()
        self.env_count = previous_count - 1
        self._downscale_registration_pending = True
        self._write_throughput_metrics(force=True)
        print(
            f"[Bees WAN actor] retired Unity environment {previous_count}->{self.env_count} "
            "locally; central capacity remains unchanged until pending uploads drain.",
            flush=True,
        )
        return True

    def _finish_pending_downscale_registration(self) -> bool:
        if not self._downscale_registration_pending:
            return True
        if not self._upload_queue.empty() or not self._upload_idle.is_set():
            # Once the requested local count is reached, ordinary rollout remains paused.
            # The pre-downscale upload set is therefore finite and can drain while Exeter
            # still authorizes the old worker range for every already-produced trajectory.
            self._report_runtime_progress()
            self._write_throughput_metrics()
            return False
        if not self._register_current_capacity():
            return False
        self._downscale_registration_pending = False
        self._report_env_count_changed()
        self._write_throughput_metrics(force=True)
        print(
            f"[Bees WAN actor] registered reduced capacity at {self.env_count} envs "
            "after pending uploads drained.",
            flush=True,
        )
        return True

    def _reconcile_env_count(self) -> bool:
        if self.manager is None or self._env_target_path is None:
            return False
        desired = self._desired_env_count()
        if self._resize_failed_target is not None:
            if desired != self._resize_failed_target:
                self._clear_resize_failure()
            else:
                # A genuine worker-retirement failure rejects this target. If earlier tail
                # workers were already retired, finish committing that smaller live capacity.
                if (
                    self._downscale_registration_pending
                    and not self._finish_pending_downscale_registration()
                ):
                    return True
                return False

        if desired < self.env_count:
            try:
                if self._scale_down_one():
                    # Reconcile again before ordinary rollout. If the next tail worker still has
                    # an in-flight step, one rollout pass will finish it before retirement.
                    return True
            except Exception as exc:
                # A failed single-worker retirement must not take down the whole WAN actor.
                # Any earlier successful retirements remain valid and are registered after
                # their finite pre-downscale upload backlog drains.
                self._record_resize_failure(desired, exc)
                return bool(self._downscale_registration_pending)
            return False

        if (
            self._downscale_registration_pending
            and not self._finish_pending_downscale_registration()
        ):
            return True

        if desired > self.env_count:
            self._scale_up_one(desired)
        return False

    def _heartbeat(self) -> bool:
        try:
            self.client.reset_ack(
                {
                    "session_id": self.session_id,
                    "actor_id": self.actor_id,
                    "control_epoch": self.control_epoch,
                }
            )
            return True
        except worker.BrokerStaleActor:
            # Central advanced while this acknowledgement was in flight. This is a normal
            # freshness race; keep rollout paused and let the main session synchronize again.
            self._state_changed.set()
            return False

    def _apply_central_throughput(self, state: Mapping[str, Any]) -> None:
        cycle = state.get("policy_cycle")
        if (
            not isinstance(cycle, int)
            or isinstance(cycle, bool)
            or cycle < 0
        ):
            raise RuntimeError("Elastic WAN central state has malformed policy-cycle metadata")
        self.policy_cycle = int(cycle)

        trainer_step = state.get("trainer_step")
        if trainer_step is not None:
            if (
                not isinstance(trainer_step, int)
                or isinstance(trainer_step, bool)
                or trainer_step < 0
            ):
                raise RuntimeError("Elastic WAN central state has malformed learner-step metadata")
            self.learner_step = int(trainer_step)

        optimizer_busy = state.get("optimizer_busy_seconds_total")
        if optimizer_busy is not None:
            if (
                not isinstance(optimizer_busy, (int, float))
                or isinstance(optimizer_busy, bool)
                or not math.isfinite(float(optimizer_busy))
                or float(optimizer_busy) < 0.0
            ):
                raise RuntimeError(
                    "Elastic WAN central state has malformed optimizer-busy metadata"
                )
            self.optimizer_busy_seconds_total = float(optimizer_busy)

        consumed = state.get("consumed_steps_by_actor")
        if not isinstance(consumed, Mapping):
            raise RuntimeError("Elastic WAN central state is missing consumed-step metrics")
        value = consumed.get(str(self.actor_id), consumed.get(self.actor_id, 0))
        if not isinstance(value, int) or isinstance(value, bool) or value < 0:
            raise RuntimeError("Elastic WAN central state has malformed consumed-step metrics")
        now = time.monotonic()
        with self._throughput_lock:
            previous = self._last_consumed_sample
            if previous is not None:
                previous_time, previous_value = previous
                elapsed = now - previous_time
                if value < previous_value:
                    self._learner_consumed_steps_per_sec = None
                elif elapsed >= 0.5:
                    self._learner_consumed_steps_per_sec = (
                        value - previous_value
                    ) / elapsed
            self._learner_consumed_steps_total = value
            self._last_consumed_sample = (now, value)
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
        if self._capacity_registration_pending:
            registered = self._register_current_capacity()
            if registered and self._downscale_registration_pending:
                self._downscale_registration_pending = False
                self._report_env_count_changed()
                self._write_throughput_metrics(force=True)
                print(
                    f"[Bees WAN actor] registered reduced capacity at {self.env_count} envs "
                    "after central state resynchronization.",
                    flush=True,
                )
        state = self.client.state(
            self.session_id,
            self.policy_epoch,
            self.control_epoch,
            0.0,
        )
        self._apply_live_rollout_horizons(state)
        self._apply_central_throughput(state)
        if not self._heartbeat():
            return
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
            except worker.BrokerStaleActor:
                self._state_changed.set()
                time.sleep(0.05)
            except worker.BrokerUnavailable:
                time.sleep(1.0)
            except BaseException as exc:
                self._thread_error.put(exc)
                return
