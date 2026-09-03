"""Bees launcher for ML-Agents 1.1.0 training.

ML-Agents 1.1.0 loads PyTorch checkpoints without a map_location. A checkpoint
saved while CUDA was active can therefore fail to resume on a CPU-only run
before ML-Agents has a chance to restore the policy, optimizer, or running
normalization state. This launcher keeps normal ML-Agents behavior but remaps
all torch.load() checkpoint tensors to the device selected by --torch-device.

It also accepts --bees-torch-threads N (or --bees-torch-threads=N) so small
policy-inference workloads can benchmark PyTorch intra-op thread counts without
patching the virtual environment.

--bees-batch-inference enables a Bees-specific optimization for ML-Agents 1.1.0:
idle subprocess workers that need an action for the same behavior are evaluated
as one policy batch instead of one TorchPolicy.evaluate() call per worker. The
result is split back into worker-local ActionInfo objects before Unity steps.
Worker-qualified global agent IDs are preserved, so recurrent memory ownership
and AgentProcessor indexing remain compatible with normal ML-Agents behavior.
All other arguments are passed unchanged to mlagents-learn.
"""

from __future__ import annotations

import sys
from typing import Dict, List, Optional, Sequence, Tuple


EXPECTED_MLAGENTS_VERSION = "1.1.0"
THREAD_FLAG = "--bees-torch-threads"
BATCH_INFERENCE_FLAG = "--bees-batch-inference"


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
) -> Tuple[List[str], Optional[int], bool]:
    trainer_args: List[str] = []
    torch_threads: Optional[int] = None
    batch_inference = False
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

        trainer_args.append(argument)
        index += 1

    return trainer_args, torch_threads, batch_inference


def _install_batched_inference():
    """Patch ML-Agents 1.1.0 to batch policy inference across idle workers.

    Returns the original SubprocessEnvManager._queue_steps method so the caller
    can restore it after training. Imports stay local so parsing/tests for this
    launcher do not require ML-Agents to be imported at module import time.
    """

    import numpy as np

    from mlagents.torch_utils import torch
    from mlagents.trainers.action_info import ActionInfo
    from mlagents.trainers.behavior_id_utils import get_global_agent_id
    from mlagents.trainers.policy.torch_policy import TorchPolicy
    from mlagents.trainers.subprocess_env_manager import (
        EnvironmentCommand,
        SubprocessEnvManager,
    )
    from mlagents_envs.base_env import ActionTuple, DecisionSteps, _ActionTupleBase

    original_queue_steps = SubprocessEnvManager._queue_steps

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

    def slice_action_tuple(value, start: int, end: int):
        continuous = value.continuous
        discrete = value.discrete
        return type(value)(
            continuous=(
                continuous[start:end] if continuous is not None else None
            ),
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

    def batched_queue_steps(self) -> None:
        idle_workers = [worker for worker in self.env_workers if not worker.waiting]
        if not idle_workers:
            return

        worker_actions: Dict[int, Dict[str, ActionInfo]] = {
            worker.worker_id: {} for worker in idle_workers
        }
        behavior_entries = {}

        # Preserve the original _take_step contract: every behavior with a policy
        # gets an ActionInfo entry, including behaviors with zero decision agents.
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

            # This optimization relies on TorchPolicy.evaluate() and its memory
            # helpers. Fall back to ML-Agents' public get_action() contract for
            # any other policy implementation.
            if not isinstance(policy, TorchPolicy):
                for worker, decision_steps in entries:
                    worker_actions[worker.worker_id][behavior_name] = policy.get_action(
                        decision_steps, worker.worker_id
                    )
                continue

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

            run_out = policy.evaluate(batched_steps, global_agent_ids)
            policy.save_memories(global_agent_ids, run_out.get("memory_out"))
            policy.check_nan_action(run_out.get("action"))

            for worker, decision_steps, start, end in ranges:
                local_outputs = split_outputs(run_out, start, end)
                worker_actions[worker.worker_id][behavior_name] = ActionInfo(
                    action=local_outputs.get("action", ActionTuple()),
                    env_action=local_outputs.get("env_action", ActionTuple()),
                    outputs=local_outputs,
                    agent_ids=list(decision_steps.agent_id),
                )

        # Compute all available actions first, then release workers together. This
        # retains the original manager's parallel Unity stepping behavior while
        # replacing serial per-worker policy evaluation with per-behavior batches.
        for worker in idle_workers:
            all_action_info = worker_actions[worker.worker_id]
            worker.previous_all_action_info = all_action_info
            worker.send(EnvironmentCommand.STEP, all_action_info)
            worker.waiting = True

    SubprocessEnvManager._queue_steps = batched_queue_steps
    return original_queue_steps


def main() -> None:
    trainer_args, torch_threads, batch_inference = _extract_bees_options(sys.argv[1:])

    import mlagents.trainers
    from mlagents import torch_utils
    from mlagents.trainers import learn
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
    if batch_inference:
        original_queue_steps = _install_batched_inference()
        print("[Bees RL] Cross-worker policy inference batching: enabled")

    original_torch_load = torch_utils.torch.load

    def device_safe_torch_load(*args, **kwargs):
        # learn.run_training() selects --torch-device before TorchModelSaver loads
        # checkpoints, so resolve default_device() at load time rather than here.
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


if __name__ == "__main__":
    main()
