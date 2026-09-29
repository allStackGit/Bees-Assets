"""Zero-local-environment support for the elastic Bees WAN learner.

ML-Agents normally discovers trainable behavior specifications from a locally launched Unity
process during the initial reset. In WAN-only mode Exeter deliberately launches no Unity training
environments, so the first authenticated remote actor becomes the behavior-specification source for
that trainer session. The central process still creates and exclusively owns the PPO trainer,
optimizer, checkpoints, candidate lineage, and policy versions.

When local environments are configured this wrapper preserves the existing hybrid elastic behavior.
When ``--num-envs=0`` it waits without failing for the first compatible remote actor, registers the
trainer from that actor's BehaviorSpec, and then blocks efficiently for policy-current remote
trajectory batches whenever no actor is producing data. Disconnecting every remote actor therefore
pauses learning rather than terminating or advancing a stale optimizer.
"""

from __future__ import annotations

import queue
import time
from typing import Any, Dict, List, Optional

import bees_elastic_wan_training as elastic
import bees_wan_actor_training as base
from bees_process_safety import write_managed_health


class ZeroLocalElasticWanEnvManagerMixin(elastic.ElasticWanEnvManagerMixin):
    def _bees_elastic_initialize(
        self,
        options: elastic.ElasticWanOptions,
        run_options: Any,
        n_env: int,
        env_factory: Any,
        local_manager_class: Any,
    ) -> None:
        from mlagents.trainers.env_manager import EnvManager

        EnvManager.__init__(self)
        if n_env < 0:
            raise RuntimeError("Elastic WAN local environment count may not be negative.")
        self._bees_local_manager = (
            local_manager_class(env_factory, run_options, n_env) if n_env > 0 else None
        )
        self._bees_local_envs = n_env
        self._bees_wan_options = options
        self._bees_wan_timeout = max(1.0, float(run_options.env_settings.timeout_wait))
        self._bees_wan_initial_reset = False
        self._bees_wan_broker = elastic.ElasticWanBroker(
            options,
            run_options,
            base.load_auth_token(options.auth_token_file or ""),
            n_env,
        )
        self._bees_wan_broker.start()
        write_managed_health(
            "ready",
            details={
                "component": "elastic-wan-learner",
                "local_envs": int(n_env),
                "broker_port": int(options.broker_port),
            },
        )
        if n_env == 0:
            print(
                "[Bees WAN] Exeter local_envs=0: PPO learner-only mode enabled; "
                "waiting for remote rollout actors."
            )

    @property
    def _bees_has_local_envs(self) -> bool:
        return self._bees_local_manager is not None

    def set_agent_manager(self, brain_name: str, manager: Any) -> None:
        from mlagents.trainers.env_manager import EnvManager

        EnvManager.set_agent_manager(self, brain_name, manager)
        if self._bees_local_manager is not None:
            self._bees_local_manager.set_agent_manager(brain_name, manager)

    def set_policy(self, brain_name: str, policy: Any) -> None:
        from mlagents.trainers.env_manager import EnvManager

        EnvManager.set_policy(self, brain_name, policy)
        if self._bees_local_manager is not None:
            self._bees_local_manager.set_policy(brain_name, policy)
        version = self._bees_wan_broker.publish_policy(brain_name, policy)
        print(f"[Bees WAN] policy {brain_name} version={version}")

    def _wait_for_behavior_source(self) -> None:
        """Wait indefinitely for enough remote actors to define/train the zero-local session."""
        required = max(1, int(self._bees_wan_options.min_actors))
        last_report = 0.0
        while True:
            active = self._bees_wan_broker.active_actor_snapshot()
            if len(active) >= required:
                return
            now = time.monotonic()
            if now - last_report >= 30.0:
                print(
                    f"[Bees WAN] learner-only mode waiting for remote actors: "
                    f"{len(active)}/{required} connected."
                )
                self._bees_wan_broker.report_capacity()
                last_report = now
            with self._bees_wan_broker._condition:
                if self._bees_wan_broker._closed:
                    raise RuntimeError("WAN actor broker closed while waiting for remote actors")
                self._bees_wan_broker._condition.wait(timeout=1.0)

    def _behavior_discovery_step(self) -> Any:
        from mlagents.trainers.env_manager import EnvironmentStep

        specs = self._bees_wan_broker.merged_behavior_specs()
        # With no local Unity process, the first compatible actor defines this trainer session's ABI.
        # Pin it permanently before trainer creation so a later actor cannot redefine the BehaviorSpec
        # after every original actor has disconnected and its lease has expired.
        self._bees_wan_broker.set_reference_behavior_specs(specs)
        # TrainerController discovers behavior IDs from the EnvironmentStep keys. The None values are
        # a marker only and are filtered by _process_step_infos below; real experience arrives as
        # already assembled native Trajectory objects from remote actors.
        return EnvironmentStep(
            {name: (None, None) for name in specs},
            0,
            {},
            {},
        )

    def _reset_env(self, config: Optional[Dict] = None) -> List[Any]:
        if self._bees_local_manager is not None:
            local_steps = self._bees_local_manager._reset_env(config)
        else:
            local_steps = []

        if not self._bees_wan_initial_reset:
            self._bees_wan_broker.initialize_control(config)
            if self._bees_local_manager is None:
                # A behavior specification is required before TrainerController can instantiate PPO.
                # min_actors=0 still means zero actors may remain connected later; startup merely
                # waits for one actor long enough to establish the immutable session ABI.
                self._wait_for_behavior_source()
                local_steps = [self._behavior_discovery_step()]
            else:
                self._bees_wan_broker.wait_for_minimum_registrations(self._bees_wan_timeout)
            self._bees_wan_initial_reset = True
        else:
            self._bees_wan_broker.request_reset(config)
            if self._bees_local_manager is None:
                from mlagents.trainers.env_manager import EnvironmentStep

                local_steps = [EnvironmentStep.empty(0)]
        return local_steps

    def _process_step_infos(self, step_infos: List[Any]) -> int:
        """Process local Unity steps while ignoring the synthetic WAN behavior-discovery marker."""
        from mlagents.trainers.env_manager import EnvManager

        real_steps = []
        for step in step_infos:
            synthetic = bool(step.current_all_step_result) and all(
                pair[0] is None and pair[1] is None
                for pair in step.current_all_step_result.values()
            )
            if not synthetic:
                real_steps.append(step)
        if real_steps:
            return EnvManager._process_step_infos(self, real_steps)
        # Return the synthetic tick count so TrainerController continues curriculum/self-play checks.
        return len(step_infos)

    def _inject_batches(self, batches: List[Any] | tuple[Any, ...]) -> None:
        for batch in batches:
            for trajectory in batch["trajectories"]:
                manager = self.agent_managers.get(trajectory.behavior_id)
                if manager is None:
                    raise RuntimeError(
                        f"WAN actor uploaded trajectory for behavior {trajectory.behavior_id!r} "
                        "before trainer registration."
                    )
                if len(trajectory.steps) > manager._max_trajectory_length:
                    raise RuntimeError(
                        f"WAN actor trajectory length {len(trajectory.steps)} exceeds "
                        f"time_horizon {manager._max_trajectory_length} for {trajectory.behavior_id}."
                    )
                manager.trajectory_queue.put(trajectory)

    def _wait_for_current_remote_batch(self) -> Any:
        """Pause a learner-only trainer when no remote rollout data is currently available."""
        last_report = 0.0
        while True:
            try:
                batch = self._bees_wan_broker._trajectory_batches.get(timeout=1.0)
            except queue.Empty:
                now = time.monotonic()
                if now - last_report >= 30.0:
                    self._bees_wan_broker.report_capacity()
                    active = self._bees_wan_broker.active_actor_snapshot()
                    if not active:
                        print(
                            "[Bees WAN] learner-only mode paused: no remote rollout actors connected."
                        )
                    last_report = now
                with self._bees_wan_broker._condition:
                    if self._bees_wan_broker._closed:
                        raise RuntimeError("WAN actor broker closed while waiting for trajectories")
                continue
            if self._bees_wan_broker._batch_is_current(batch):
                self._bees_wan_broker.record_consumed_batch(batch)
                return batch

    def _inject_remote_batches(self) -> None:
        active = self._bees_wan_broker.active_actor_snapshot()
        batches = self._bees_wan_broker.drain_current_batches(max(1, len(active) * 2))
        self._inject_batches(batches)

    def _step(self) -> List[Any]:
        if self._bees_local_manager is not None:
            local_steps = self._bees_local_manager._step()
            self._inject_remote_batches()
            self._bees_wan_broker.report_capacity()
            return local_steps

        # With no local simulator there is intentionally nothing to advance until at least one
        # policy-current remote trajectory arrives. Waiting here applies natural backpressure and
        # prevents a busy loop or fake training progress when all actors are offline.
        first = self._wait_for_current_remote_batch()
        self._inject_batches((first,))
        self._inject_remote_batches()
        self._bees_wan_broker.report_capacity()

        from mlagents.trainers.env_manager import EnvironmentStep

        return [EnvironmentStep.empty(0)]

    def set_env_parameters(self, config: Optional[Dict] = None) -> None:
        if self._bees_local_manager is not None:
            self._bees_local_manager.set_env_parameters(config)
        if self._bees_wan_initial_reset:
            self._bees_wan_broker.request_parameters(config)

    def on_training_started(self, behavior_name: str, trainer_settings: Any) -> None:
        if self._bees_local_manager is not None:
            self._bees_local_manager.on_training_started(behavior_name, trainer_settings)

    @property
    def training_behaviors(self) -> Dict[str, Any]:
        if self._bees_local_manager is not None:
            specs = dict(self._bees_local_manager.training_behaviors)
            self._bees_wan_broker.set_reference_behavior_specs(specs)
            return specs
        return dict(self._bees_wan_broker.merged_behavior_specs())

    def close(self) -> None:
        try:
            self._bees_wan_broker.close()
        finally:
            if self._bees_local_manager is not None:
                self._bees_local_manager.close()


def install_elastic_wan_env_manager(
    options: elastic.ElasticWanOptions,
    *,
    force_zero_local: bool = False,
) -> Optional[elastic.ElasticWanPatch]:
    """Install the elastic EnvManager with optional zero local Unity processes."""
    if not options.enabled:
        return None

    import mlagents.trainers
    import mlagents.trainers.learn as learn
    from mlagents.trainers.env_manager import EnvManager
    from mlagents.trainers.trainer_controller import TrainerController

    actual = mlagents.trainers.__version__
    if actual != base.EXPECTED_MLAGENTS_VERSION:
        raise RuntimeError(
            f"Elastic WAN actor mode targets ML-Agents {base.EXPECTED_MLAGENTS_VERSION}, "
            f"but {actual} is installed."
        )
    base.load_auth_token(options.auth_token_file or "")
    original_manager = learn.SubprocessEnvManager
    original_advance = TrainerController.advance

    class ZeroLocalElasticWanEnvManager(ZeroLocalElasticWanEnvManagerMixin, EnvManager):
        def __init__(self, env_factory: Any, run_options: Any, n_env: int = 1):
            effective_n_env = 0 if force_zero_local else n_env
            self._bees_elastic_initialize(
                options,
                run_options,
                effective_n_env,
                env_factory,
                original_manager,
            )

    def monitored_advance(controller: Any, env_manager: Any) -> int:
        result = original_advance(controller, env_manager)
        broker = getattr(env_manager, "_bees_wan_broker", None)
        if broker is not None:
            steps = []
            for trainer in controller.trainers.values():
                try:
                    steps.append(int(trainer.get_step))
                except Exception:
                    continue
            if steps:
                broker.observe_trainer_step(max(steps))
        return result

    learn.SubprocessEnvManager = ZeroLocalElasticWanEnvManager
    TrainerController.advance = monitored_advance
    return elastic.ElasticWanPatch(original_manager, original_advance)


def restore_elastic_wan_env_manager(patch: Optional[elastic.ElasticWanPatch]) -> None:
    elastic.restore_elastic_wan_env_manager(patch)
