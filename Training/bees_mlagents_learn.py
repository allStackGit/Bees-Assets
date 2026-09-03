"""Bees launcher for ML-Agents 1.1.0 training.

This launcher keeps the project's ML-Agents 1.1.0 training/checkpoint behavior
while applying Bees-specific performance and device fixes:

* CUDA-saved checkpoints are remapped to the selected --torch-device on load.
* --bees-torch-threads controls PyTorch intra-op CPU threads.
* --bees-batch-inference batches idle workers by exact behavior id, keeps full
  policy outputs in the trainer process, sends Unity only the environment action,
  coalesces near-ready workers for 2 ms, and samples worker timer-tree IPC.
* --bees-cpu-inference keeps PPO/optimizer state on --torch-device while using a
  synchronized CPU actor replica for environment inference.

All other arguments are passed unchanged to mlagents-learn.
"""

from __future__ import annotations

import copy
import sys
from typing import Dict, List, Optional, Sequence, Tuple


EXPECTED_MLAGENTS_VERSION = "1.1.0"
THREAD_FLAG = "--bees-torch-threads"
BATCH_INFERENCE_FLAG = "--bees-batch-inference"
CPU_INFERENCE_FLAG = "--bees-cpu-inference"
WORKER_COALESCE_SECONDS = 0.002
WORKER_TIMER_SAMPLE_STEPS = 64
_ORIGINAL_MLAGENTS_WORKER = None


def _parse_positive_int(value: str, flag: str) -> int:
    try:
        parsed = int(value)
    except ValueError as exc:
        raise SystemExit(f"{flag} requires a positive whole number; got {value!r}.") from exc
    if parsed <= 0:
        raise SystemExit(f"{flag} requires a positive whole number; got {value!r}.")
    return parsed


def _extract_bees_options(
    argv: Sequence[str],
) -> Tuple[List[str], Optional[int], bool, bool]:
    trainer_args: List[str] = []
    torch_threads: Optional[int] = None
    batch_inference = False
    cpu_inference = False
    index = 0

    while index < len(argv):
        argument = argv[index]
        if argument == THREAD_FLAG:
            if index + 1 >= len(argv) or argv[index + 1].startswith("--"):
                raise SystemExit(f"{THREAD_FLAG} requires a value.")
            torch_threads = _parse_positive_int(argv[index + 1], THREAD_FLAG)
            index += 2
            continue

        prefix = THREAD_FLAG + "="
        if argument.startswith(prefix):
            value = argument[len(prefix) :]
            if not value:
                raise SystemExit(f"{THREAD_FLAG} requires a value.")
            torch_threads = _parse_positive_int(value, THREAD_FLAG)
            index += 1
            continue

        if argument == BATCH_INFERENCE_FLAG:
            batch_inference = True
            index += 1
            continue

        if argument == CPU_INFERENCE_FLAG:
            cpu_inference = True
            index += 1
            continue

        trainer_args.append(argument)
        index += 1

    return trainer_args, torch_threads, batch_inference, cpu_inference


class _CpuInferenceActorCache:
    """CPU replicas of trainer-owned actors, synchronized only when state changes."""

    def __init__(self) -> None:
        self._entries = {}

    @staticmethod
    def _parameter_signature(actor):
        return tuple(
            (name, id(parameter), parameter._version)
            for name, parameter in actor.named_parameters()
        )

    @staticmethod
    def _buffer_signature(actor):
        return tuple(
            (name, id(buffer), buffer._version)
            for name, buffer in actor.named_buffers()
        )

    @staticmethod
    def _copy_parameters(source_actor, target_actor) -> None:
        from mlagents.torch_utils import torch

        target_parameters = dict(target_actor.named_parameters())
        with torch.no_grad():
            for name, source in source_actor.named_parameters():
                target_parameters[name].copy_(source.detach(), non_blocking=False)

    @staticmethod
    def _copy_buffers(source_actor, target_actor) -> None:
        from mlagents.torch_utils import torch

        target_buffers = dict(target_actor.named_buffers())
        with torch.no_grad():
            for name, source in source_actor.named_buffers():
                target_buffers[name].copy_(source.detach(), non_blocking=False)

    def get(self, behavior_name: str, policy):
        from mlagents.torch_utils import torch
        from mlagents_envs.timers import hierarchical_timer

        source_actor = policy.actor
        parameter_signature = self._parameter_signature(source_actor)
        buffer_signature = self._buffer_signature(source_actor)
        entry = self._entries.get(behavior_name)

        if entry is None or entry["source_actor"] is not source_actor:
            with hierarchical_timer("BeesCpuInference.create_replica"):
                replica = copy.deepcopy(source_actor)
                replica.to(torch.device("cpu"))
                replica.train(source_actor.training)
            entry = {
                "source_actor": source_actor,
                "replica": replica,
                "parameter_signature": parameter_signature,
                "buffer_signature": buffer_signature,
            }
            self._entries[behavior_name] = entry
            return replica

        replica = entry["replica"]
        replica.train(source_actor.training)

        if parameter_signature != entry["parameter_signature"]:
            with hierarchical_timer("BeesCpuInference.sync_parameters"):
                self._copy_parameters(source_actor, replica)
                self._copy_buffers(source_actor, replica)
            entry["parameter_signature"] = parameter_signature
            entry["buffer_signature"] = buffer_signature
        elif buffer_signature != entry["buffer_signature"]:
            with hierarchical_timer("BeesCpuInference.sync_buffers"):
                self._copy_buffers(source_actor, replica)
            entry["buffer_signature"] = buffer_signature

        return replica


class _WorkerTimerSampler:
    """Accumulate worker timers and transfer one tree every N environment steps."""

    def __init__(self, get_timer_root, reset_timers, sample_steps: int) -> None:
        self._get_timer_root = get_timer_root
        self._reset_timers = reset_timers
        self._sample_steps = sample_steps
        self._step_count = 0
        self._sample_due = False

    def get_timer_root(self):
        self._step_count += 1
        self._sample_due = self._step_count % self._sample_steps == 0
        if self._sample_due:
            return self._get_timer_root()
        return None

    def reset_timers(self) -> None:
        # The upstream worker calls reset_timers immediately after get_timer_root.
        # Keep accumulating unsampled steps so the sampled tree still represents
        # all worker work instead of under-reporting by the sampling factor.
        if self._sample_due:
            self._reset_timers()


def _bees_sampled_worker(*args, **kwargs) -> None:
    """Process target that reduces timer-tree IPC without changing Unity steps."""

    import mlagents.trainers.subprocess_env_manager as subprocess_env_manager

    global _ORIGINAL_MLAGENTS_WORKER
    original_worker = _ORIGINAL_MLAGENTS_WORKER or subprocess_env_manager.worker
    if original_worker is _bees_sampled_worker:
        raise RuntimeError("Unable to resolve the original ML-Agents worker entry point.")

    original_get_timer_root = subprocess_env_manager.get_timer_root
    original_reset_timers = subprocess_env_manager.reset_timers
    sampler = _WorkerTimerSampler(
        original_get_timer_root,
        original_reset_timers,
        WORKER_TIMER_SAMPLE_STEPS,
    )
    subprocess_env_manager.get_timer_root = sampler.get_timer_root
    subprocess_env_manager.reset_timers = sampler.reset_timers
    try:
        original_worker(*args, **kwargs)
    finally:
        subprocess_env_manager.get_timer_root = original_get_timer_root
        subprocess_env_manager.reset_timers = original_reset_timers


def _install_sampled_worker_timers():
    """Make newly created subprocess workers use sampled timer-tree transfer."""

    import mlagents.trainers.subprocess_env_manager as subprocess_env_manager

    global _ORIGINAL_MLAGENTS_WORKER
    original_worker = subprocess_env_manager.worker
    _ORIGINAL_MLAGENTS_WORKER = original_worker
    subprocess_env_manager.worker = _bees_sampled_worker
    return original_worker


def _install_batched_inference(cpu_inference: bool = False):
    """Batch exact-behavior policy inference across all currently idle workers."""

    import numpy as np

    from mlagents.torch_utils import torch
    from mlagents.trainers.action_info import ActionInfo
    from mlagents.trainers.behavior_id_utils import get_global_agent_id
    from mlagents.trainers.policy.torch_policy import TorchPolicy
    from mlagents.trainers.subprocess_env_manager import (
        EnvironmentCommand,
        SubprocessEnvManager,
    )
    from mlagents.trainers.torch_entities.utils import ModelUtils
    from mlagents_envs.base_env import ActionTuple, DecisionSteps, _ActionTupleBase
    from mlagents_envs.timers import hierarchical_timer

    original_queue_steps = SubprocessEnvManager._queue_steps
    cpu_actor_cache = _CpuInferenceActorCache() if cpu_inference else None
    cpu_device = torch.device("cpu")

    def concatenate_decision_steps(entries, action_spec):
        first_steps = entries[0][1]
        obs = [
            np.concatenate([steps.obs[index] for _, steps in entries], axis=0)
            for index in range(len(first_steps.obs))
        ]
        reward = np.concatenate([steps.reward for _, steps in entries], axis=0)
        agent_id = np.concatenate([steps.agent_id for _, steps in entries], axis=0)
        group_id = np.concatenate([steps.group_id for _, steps in entries], axis=0)
        group_reward = np.concatenate(
            [steps.group_reward for _, steps in entries], axis=0
        )

        action_mask = None
        if action_spec.discrete_size > 0 and any(
            steps.action_mask is not None for _, steps in entries
        ):
            action_mask = []
            for branch_index, branch_size in enumerate(action_spec.discrete_branches):
                branch_masks = []
                for _, steps in entries:
                    if steps.action_mask is None:
                        branch_masks.append(
                            np.zeros((len(steps), branch_size), dtype=bool)
                        )
                    else:
                        branch_masks.append(steps.action_mask[branch_index])
                action_mask.append(np.concatenate(branch_masks, axis=0))

        return DecisionSteps(
            obs=obs,
            reward=reward,
            agent_id=agent_id,
            action_mask=action_mask,
            group_id=group_id,
            group_reward=group_reward,
        )

    def evaluate_on_cpu(policy, actor, decision_requests, global_agent_ids):
        with hierarchical_timer("BeesCpuInference.evaluate"):
            with torch.device(cpu_device):
                masks = None
                action_spec = policy.behavior_spec.action_spec
                if action_spec.discrete_size > 0:
                    num_discrete_flat = int(np.sum(action_spec.discrete_branches))
                    masks = torch.ones(
                        [len(decision_requests), num_discrete_flat],
                        device=cpu_device,
                    )
                    if decision_requests.action_mask is not None:
                        masks = torch.as_tensor(
                            1 - np.concatenate(decision_requests.action_mask, axis=1),
                            device=cpu_device,
                        )

                tensor_obs = [
                    torch.as_tensor(observation, device=cpu_device)
                    for observation in decision_requests.obs
                ]
                memories = None
                if policy.use_recurrent:
                    memories = torch.as_tensor(
                        policy.retrieve_memories(global_agent_ids),
                        device=cpu_device,
                    ).unsqueeze(0)

                # Inference mode skips autograd/version bookkeeping that is not
                # needed for environment action selection.
                with torch.inference_mode():
                    action, run_out, memories = actor.get_action_and_stats(
                        tensor_obs,
                        masks=masks,
                        memories=memories,
                    )

                run_out["action"] = action.to_action_tuple()
                if "log_probs" in run_out:
                    run_out["log_probs"] = run_out["log_probs"].to_log_probs_tuple()
                if "entropy" in run_out:
                    run_out["entropy"] = ModelUtils.to_numpy(run_out["entropy"])
                if policy.use_recurrent:
                    run_out["memory_out"] = ModelUtils.to_numpy(memories).squeeze(0)
                return run_out

    def slice_action_tuple(value, start: int, end: int):
        continuous = value.continuous
        discrete = value.discrete
        return type(value)(
            continuous=(continuous[start:end] if continuous is not None else None),
            discrete=(discrete[start:end] if discrete is not None else None),
        )

    def slice_output(value, start: int, end: int):
        if isinstance(value, _ActionTupleBase):
            return slice_action_tuple(value, start, end)
        if isinstance(value, np.ndarray):
            return value[start:end]
        if torch.is_tensor(value):
            return value[start:end]
        if isinstance(value, list):
            return value[start:end]
        return value

    def split_outputs(outputs, start: int, end: int):
        return {
            key: slice_output(value, start, end)
            for key, value in outputs.items()
        }

    def make_ipc_action_info(info: ActionInfo) -> ActionInfo:
        if not info.agent_ids:
            return ActionInfo.empty()
        # The Unity subprocess only reads agent_ids and env_action. Keep action,
        # log-probs, entropy and other training outputs in the main process where
        # AgentProcessor consumes them; do not pickle/send them across the pipe.
        return ActionInfo(
            action=[],
            env_action=info.env_action,
            outputs={},
            agent_ids=info.agent_ids,
        )

    def batched_queue_steps(self) -> None:
        idle_workers = [worker for worker in self.env_workers if not worker.waiting]
        if not idle_workers:
            return

        with hierarchical_timer("BeesBatch.collect"):
            worker_actions: Dict[int, Dict[str, ActionInfo]] = {
                worker.worker_id: {} for worker in idle_workers
            }
            behavior_entries = {}
            for worker in idle_workers:
                for behavior_name, step_tuple in worker.previous_step.current_all_step_result.items():
                    if behavior_name not in self.policies:
                        continue
                    decision_steps = step_tuple[0]
                    if len(decision_steps) == 0:
                        worker_actions[worker.worker_id][behavior_name] = ActionInfo.empty()
                        continue
                    behavior_entries.setdefault(behavior_name, []).append(
                        (worker, decision_steps)
                    )

        for behavior_name, entries in behavior_entries.items():
            policy = self.policies[behavior_name]
            if not isinstance(policy, TorchPolicy):
                for worker, decision_steps in entries:
                    worker_actions[worker.worker_id][behavior_name] = policy.get_action(
                        decision_steps, worker.worker_id
                    )
                continue

            with hierarchical_timer("BeesBatch.prepare"):
                batched_steps = concatenate_decision_steps(
                    entries, policy.behavior_spec.action_spec
                )
                global_agent_ids = []
                ranges = []
                start = 0
                for worker, decision_steps in entries:
                    end = start + len(decision_steps)
                    ranges.append((worker, decision_steps, start, end))
                    global_agent_ids.extend(
                        get_global_agent_id(worker.worker_id, int(agent_id))
                        for agent_id in decision_steps.agent_id
                    )
                    start = end

            if cpu_actor_cache is None:
                run_out = policy.evaluate(batched_steps, global_agent_ids)
            else:
                cpu_actor = cpu_actor_cache.get(behavior_name, policy)
                run_out = evaluate_on_cpu(
                    policy,
                    cpu_actor,
                    batched_steps,
                    global_agent_ids,
                )

            policy.save_memories(global_agent_ids, run_out.get("memory_out"))
            policy.check_nan_action(run_out.get("action"))

            with hierarchical_timer("BeesBatch.split"):
                for worker, decision_steps, start, end in ranges:
                    local_outputs = split_outputs(run_out, start, end)
                    worker_actions[worker.worker_id][behavior_name] = ActionInfo(
                        action=local_outputs.get("action", ActionTuple()),
                        env_action=local_outputs.get("env_action", ActionTuple()),
                        outputs=local_outputs,
                        agent_ids=list(decision_steps.agent_id),
                    )

        with hierarchical_timer("BeesBatch.send"):
            for worker in idle_workers:
                all_action_info = worker_actions[worker.worker_id]
                # This full object never leaves the trainer process and remains
                # the source of PPO trajectory action/log-prob/entropy data.
                worker.previous_all_action_info = all_action_info
                ipc_action_info = {
                    behavior_name: make_ipc_action_info(info)
                    for behavior_name, info in all_action_info.items()
                }
                worker.send(EnvironmentCommand.STEP, ipc_action_info)
                worker.waiting = True

    SubprocessEnvManager._queue_steps = batched_queue_steps
    return original_queue_steps


def _install_fast_env_manager():
    """Block for the first result and briefly coalesce near-ready workers."""

    import time
    from queue import Empty as EmptyQueueException

    from mlagents.trainers.env_manager import EnvManager
    from mlagents.trainers.subprocess_env_manager import (
        EnvironmentCommand,
        SubprocessEnvManager,
    )
    from mlagents_envs.timers import hierarchical_timer

    original_step = SubprocessEnvManager._step
    original_process_step_infos = EnvManager._process_step_infos

    def fast_step(self):
        self._queue_steps()
        worker_steps = []
        step_workers = set()

        def accept_response(step) -> bool:
            """Return True when a worker failure caused a manager restart."""
            if step.cmd == EnvironmentCommand.ENV_EXITED:
                self._restart_failed_workers(step)
                worker_steps.clear()
                step_workers.clear()
                self._queue_steps()
                return True
            if step.worker_id not in step_workers:
                self.env_workers[step.worker_id].waiting = False
                worker_steps.append(step)
                step_workers.add(step.worker_id)
            return False

        while not worker_steps:
            with hierarchical_timer("BeesEnv.wait_first_worker"):
                first_step = self.step_queue.get()
            if accept_response(first_step):
                continue

            restarted = False
            with hierarchical_timer("BeesEnv.drain_ready_workers"):
                while True:
                    try:
                        step = self.step_queue.get_nowait()
                    except EmptyQueueException:
                        break
                    if accept_response(step):
                        restarted = True
                        break
            if restarted:
                continue

            # The previous profile averaged about 25/32 workers per returned
            # manager step. Give already-running stragglers a tiny bounded window
            # to join this batch; never wait for all workers or a slow outlier.
            if any(worker.waiting for worker in self.env_workers):
                deadline = time.perf_counter() + WORKER_COALESCE_SECONDS
                with hierarchical_timer("BeesEnv.coalesce_workers"):
                    while any(worker.waiting for worker in self.env_workers):
                        remaining = deadline - time.perf_counter()
                        if remaining <= 0:
                            break
                        try:
                            step = self.step_queue.get(timeout=remaining)
                        except EmptyQueueException:
                            break
                        if accept_response(step):
                            restarted = True
                            break
            if restarted:
                continue

        with hierarchical_timer("BeesEnv.postprocess_steps"):
            return self._postprocess_steps(worker_steps)

    def timed_process_step_infos(self, step_infos):
        with hierarchical_timer("BeesEnv.process_step_infos"):
            return original_process_step_infos(self, step_infos)

    SubprocessEnvManager._step = fast_step
    EnvManager._process_step_infos = timed_process_step_infos
    return original_step, original_process_step_infos


def main() -> None:
    (
        trainer_args,
        torch_threads,
        batch_inference,
        cpu_inference,
    ) = _extract_bees_options(sys.argv[1:])

    if cpu_inference and not batch_inference:
        raise SystemExit(
            f"{CPU_INFERENCE_FLAG} requires {BATCH_INFERENCE_FLAG}; "
            "hybrid CPU inference is implemented by the cross-worker batching path."
        )

    import mlagents.trainers
    import mlagents.trainers.subprocess_env_manager as subprocess_env_manager_module
    from mlagents import torch_utils
    from mlagents.trainers import learn
    from mlagents.trainers.env_manager import EnvManager
    from mlagents.trainers.subprocess_env_manager import SubprocessEnvManager

    actual_version = mlagents.trainers.__version__
    if actual_version != EXPECTED_MLAGENTS_VERSION:
        raise RuntimeError(
            "Training/bees_mlagents_learn.py patches ML-Agents checkpoint loading "
            f"and optional batching for version {EXPECTED_MLAGENTS_VERSION}, but version "
            f"{actual_version} is installed. Verify the newer internals before changing "
            "this version guard."
        )

    if torch_threads is not None:
        torch_utils.torch.set_num_threads(torch_threads)
        print(f"[Bees RL] PyTorch intra-op threads: {torch_threads}")

    original_queue_steps = None
    original_env_step = None
    original_process_step_infos = None
    original_worker = None
    if batch_inference:
        original_worker = _install_sampled_worker_timers()
        original_queue_steps = _install_batched_inference(
            cpu_inference=cpu_inference
        )
        original_env_step, original_process_step_infos = _install_fast_env_manager()
        print("[Bees RL] Cross-worker policy inference batching: enabled")
        print("[Bees RL] Slim subprocess action IPC: enabled")
        print(
            f"[Bees RL] Worker coalescing window: "
            f"{WORKER_COALESCE_SECONDS * 1000.0:.1f} ms"
        )
        print(
            f"[Bees RL] Worker timer transfer: every "
            f"{WORKER_TIMER_SAMPLE_STEPS} steps"
        )
        print("[Bees RL] Blocking Unity-worker wait: enabled")
        if cpu_inference:
            print(
                "[Bees RL] Hybrid devices: trainer/optimizer uses --torch-device; "
                "environment inference uses synchronized CPU actor replicas"
            )

    original_torch_load = torch_utils.torch.load

    def device_safe_torch_load(*args, **kwargs):
        kwargs.setdefault("map_location", torch_utils.default_device())
        return original_torch_load(*args, **kwargs)

    previous_argv = sys.argv
    torch_utils.torch.load = device_safe_torch_load
    sys.argv = [previous_argv[0], *trainer_args]
    try:
        learn.main()
    finally:
        sys.argv = previous_argv
        torch_utils.torch.load = original_torch_load
        if original_queue_steps is not None:
            SubprocessEnvManager._queue_steps = original_queue_steps
        if original_env_step is not None:
            SubprocessEnvManager._step = original_env_step
        if original_process_step_infos is not None:
            EnvManager._process_step_infos = original_process_step_infos
        if original_worker is not None:
            subprocess_env_manager_module.worker = original_worker


if __name__ == "__main__":
    main()
