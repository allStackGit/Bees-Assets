"""Bees launcher for ML-Agents 1.1.0 training.

This launcher keeps the project's ML-Agents 1.1.0 training/checkpoint behavior
while applying Bees-specific performance and device fixes:

* CUDA-saved checkpoints are remapped to the selected --torch-device on load.
* --bees-torch-threads controls PyTorch intra-op CPU threads.
* --bees-batch-inference batches idle workers by exact behavior id, keeps full
  policy outputs in the trainer process, sends Unity only the environment action,
  and samples worker timer-tree IPC.
* --bees-cpu-inference keeps PPO/optimizer state on --torch-device while using a
  synchronized CPU actor replica for environment inference.
* Training results default to .results so Unity does not import checkpoints,
  logs, and exported networks; an explicit --results-dir still takes precedence.

All other arguments are passed unchanged to mlagents-learn.
"""

from __future__ import annotations

import _thread
import copy
import json
import logging
import os
from pathlib import Path
import signal
import sys
import threading
import time
import tempfile
import traceback
from typing import Dict, List, Optional, Sequence, Tuple


EXPECTED_MLAGENTS_VERSION = "1.1.0"
THREAD_FLAG = "--bees-torch-threads"
BATCH_INFERENCE_FLAG = "--bees-batch-inference"
CPU_INFERENCE_FLAG = "--bees-cpu-inference"
RESULTS_DIR_FLAG = "--results-dir"
DEFAULT_RESULTS_DIR = ".results"
WORKER_TIMER_SAMPLE_STEPS = 64
MANAGED_STOP_FILE_ENV = "BEES_TRAINING_STOP_FILE"
MODEL_SNAPSHOT_REQUEST_FILE_ENV = "BEES_TRAINING_MODEL_SNAPSHOT_REQUEST_FILE"
MODEL_SNAPSHOT_RESPONSE_FILE_ENV = "BEES_TRAINING_MODEL_SNAPSHOT_RESPONSE_FILE"
MANAGED_STOP_POLL_SECONDS = 0.25
MANAGED_LEARNER_PROGRESS_FILE_NAME = "learner-progress.json"
MANAGED_LOG_DIR_ENV = "BEES_TRAINING_LOG_DIR"
LIVE_LEARNER_LOG_NAME = "learner-live.log"
LIVE_LEARNER_LOG_MAX_BYTES = 16 * 1024 * 1024
_ORIGINAL_MLAGENTS_WORKER = None

# Unity terminal marker values mirrored from RlOneVsOneAgent. They exist only to make stock
# ML-Agents GhostTrainer ELO follow the explicit battle outcome; the markers are removed before
# the wrapped PPO/POCA trainer receives each trajectory.
ELO_WIN_MARKER = 4096.0
ELO_DRAW_MARKER = 8192.0
ELO_LOSS_MARKER = -4096.0
ELO_MARKER_TOLERANCE = 512.0


def _decode_explicit_outcome_marker(reward: float):
    value = float(reward)
    for marker, result in (
        (ELO_DRAW_MARKER, 0.5),
        (ELO_WIN_MARKER, 1.0),
        (ELO_LOSS_MARKER, 0.0),
    ):
        if abs(value - marker) < ELO_MARKER_TOLERANCE:
            return result, value - marker
    return None, value


def _install_explicit_outcome_elo():
    from mlagents.trainers.ghost.trainer import GhostTrainer

    original = GhostTrainer._process_trajectory

    def process_trajectory_from_explicit_outcome(self, trajectory):
        if not trajectory.steps:
            return original(self, trajectory)

        explicit_result, stripped_reward = _decode_explicit_outcome_marker(
            trajectory.steps[-1].group_reward
        )
        if explicit_result is None:
            return original(self, trajectory)

        # Trajectory is a NamedTuple containing a mutable steps list; replacing the final
        # AgentExperience here also changes the same trajectory already queued for the inner
        # trainer, ensuring PPO never trains on the ELO-only group marker.
        trajectory.steps[-1] = trajectory.steps[-1]._replace(group_reward=stripped_reward)

        if (
            trajectory.done_reached
            and trajectory.all_group_dones_reached
            and not trajectory.interrupted
        ):
            change = self.controller.compute_elo_rating_changes(
                self.current_elo, explicit_result
            )
            self.change_current_elo(change)
            self._stats_reporter.add_stat("Self-play/ELO", self.current_elo)

    GhostTrainer._process_trajectory = process_trajectory_from_explicit_outcome
    return original


def _restore_explicit_outcome_elo(original) -> None:
    if original is None:
        return
    from mlagents.trainers.ghost.trainer import GhostTrainer

    GhostTrainer._process_trajectory = original



class _LiveLogSink:
    def __init__(self, path: Path) -> None:
        self.path = path
        self._lock = threading.Lock()

    def write(self, value: str) -> None:
        text = str(value)
        if not text:
            return
        encoded = text.encode("utf-8", errors="replace")
        with self._lock:
            try:
                self.path.parent.mkdir(parents=True, exist_ok=True)
                current = self.path.stat().st_size if self.path.is_file() else 0
                if current + len(encoded) > LIVE_LEARNER_LOG_MAX_BYTES:
                    self.path.write_text("", encoding="utf-8")
                with self.path.open("a", encoding="utf-8", errors="replace") as handle:
                    handle.write(text)
                    handle.flush()
            except OSError:
                pass


class _LiveLogTee:
    def __init__(self, primary, sink: _LiveLogSink) -> None:
        self.primary = primary
        self.sink = sink

    def write(self, value):
        result = self.primary.write(value)
        self.sink.write(value)
        return result

    def flush(self) -> None:
        self.primary.flush()

    def __getattr__(self, name):
        return getattr(self.primary, name)


def _install_managed_live_log():
    root = os.environ.get(MANAGED_LOG_DIR_ENV, "").strip()
    if not root:
        return None
    sink = _LiveLogSink(Path(root).expanduser().resolve() / LIVE_LEARNER_LOG_NAME)
    originals = (sys.stdout, sys.stderr)
    stdout_tee = _LiveLogTee(originals[0], sink)
    stderr_tee = _LiveLogTee(originals[1], sink)
    sys.stdout = stdout_tee
    sys.stderr = stderr_tee

    # Elastic-WAN wrappers import ML-Agents before this launcher runs, so some logging handlers
    # may already be bound to the original stderr/stdout objects. Rebind only those console
    # handlers; file handlers and other explicit destinations remain untouched.
    rebound = []
    seen_handlers = set()
    loggers = [logging.getLogger()]
    loggers.extend(
        value
        for value in logging.root.manager.loggerDict.values()
        if isinstance(value, logging.Logger)
    )
    for logger in loggers:
        for handler in logger.handlers:
            identity = id(handler)
            if identity in seen_handlers:
                continue
            seen_handlers.add(identity)
            stream = getattr(handler, "stream", None)
            replacement = None
            if stream is originals[0]:
                replacement = stdout_tee
            elif stream is originals[1]:
                replacement = stderr_tee
            if replacement is None or not hasattr(handler, "setStream"):
                continue
            try:
                handler.setStream(replacement)
                rebound.append((handler, stream))
            except (AttributeError, ValueError):
                continue
    return originals, rebound


def _restore_managed_live_log(state) -> None:
    if state is None:
        return
    originals, rebound = state
    for handler, stream in rebound:
        try:
            handler.setStream(stream)
        except (AttributeError, ValueError):
            pass
    sys.stdout, sys.stderr = originals


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


def _ensure_results_dir(argv: Sequence[str]) -> List[str]:
    """Use Unity-ignored .results unless the caller explicitly chooses a path."""

    trainer_args = list(argv)
    if any(
        argument == RESULTS_DIR_FLAG
        or argument.startswith(RESULTS_DIR_FLAG + "=")
        for argument in trainer_args
    ):
        return trainer_args
    return [*trainer_args, f"{RESULTS_DIR_FLAG}={DEFAULT_RESULTS_DIR}"]


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
    if original_worker is _bees_sampled_worker:
        return None
    if _ORIGINAL_MLAGENTS_WORKER is None:
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
        EnvironmentResponse,
        SubprocessEnvManager,
    )
    from mlagents_envs.exception import UnityCommunicationException
    from mlagents.trainers.torch_entities.utils import ModelUtils
    from mlagents_envs.base_env import ActionTuple, DecisionSteps, _ActionTupleBase
    from mlagents_envs.timers import hierarchical_timer

    original_queue_steps = SubprocessEnvManager._queue_steps
    if getattr(original_queue_steps, "_bees_batched_inference", False):
        configured_cpu_inference = bool(
            getattr(original_queue_steps, "_bees_cpu_inference", False)
        )
        if configured_cpu_inference != bool(cpu_inference):
            raise RuntimeError(
                "Bees batched inference is already installed with a different device mode."
            )
        return None
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
                try:
                    worker.send(EnvironmentCommand.STEP, ipc_action_info)
                    worker.waiting = True
                except UnityCommunicationException as exc:
                    # A Unity child can disappear after we selected it as idle but before the
                    # batched action send reaches its multiprocessing pipe. Route that race back
                    # through ML-Agents' normal ENV_EXITED recovery instead of letting a local
                    # broken pipe escape and recycle the entire WAN actor session.
                    self.step_queue.put(
                        EnvironmentResponse(
                            EnvironmentCommand.ENV_EXITED,
                            worker.worker_id,
                            exc,
                        )
                    )

    batched_queue_steps._bees_batched_inference = True
    batched_queue_steps._bees_cpu_inference = bool(cpu_inference)
    SubprocessEnvManager._queue_steps = batched_queue_steps
    return original_queue_steps


def _install_fast_env_manager():
    """Block for the first result, then drain every worker already ready."""

    from queue import Empty as EmptyQueueException

    from mlagents.trainers.env_manager import EnvManager
    from mlagents.trainers.subprocess_env_manager import (
        EnvironmentCommand,
        SubprocessEnvManager,
    )
    from mlagents_envs.timers import hierarchical_timer

    original_step = SubprocessEnvManager._step
    original_process_step_infos = EnvManager._process_step_infos
    step_installed = bool(getattr(original_step, "_bees_fast_env_manager", False))
    process_installed = bool(
        getattr(
            original_process_step_infos,
            "_bees_fast_env_manager",
            False,
        )
    )
    if step_installed or process_installed:
        if step_installed and process_installed:
            return None, None
        raise RuntimeError("Bees fast environment-manager patch is only partially installed.")

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

        with hierarchical_timer("BeesEnv.postprocess_steps"):
            return self._postprocess_steps(worker_steps)

    def timed_process_step_infos(self, step_infos):
        with hierarchical_timer("BeesEnv.process_step_infos"):
            return original_process_step_infos(self, step_infos)

    fast_step._bees_fast_env_manager = True
    timed_process_step_infos._bees_fast_env_manager = True
    SubprocessEnvManager._step = fast_step
    EnvManager._process_step_infos = timed_process_step_infos
    return original_step, original_process_step_infos


def _install_windows_break_interrupt():
    """Map a targeted Windows CTRL_BREAK to the same graceful path as Ctrl+C."""
    if os.name != "nt" or not hasattr(signal, "SIGBREAK"):
        return None
    return signal.signal(signal.SIGBREAK, signal.default_int_handler)


def _atomic_json(path: Path, value) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    fd, temporary_name = tempfile.mkstemp(
        prefix=path.name + ".",
        suffix=".tmp",
        dir=str(path.parent),
    )
    temporary = Path(temporary_name)
    try:
        with os.fdopen(fd, "w", encoding="utf-8") as handle:
            handle.write(json.dumps(value, indent=2, sort_keys=True) + "\n")
            handle.flush()
            os.fsync(handle.fileno())
        os.replace(temporary, path)
    finally:
        try:
            temporary.unlink()
        except FileNotFoundError:
            pass


def _managed_learner_progress_path() -> Optional[Path]:
    stop_value = os.environ.get(MANAGED_STOP_FILE_ENV, "").strip()
    if not stop_value:
        return None
    return (
        Path(stop_value)
        .expanduser()
        .resolve()
        .with_name(MANAGED_LEARNER_PROGRESS_FILE_NAME)
    )


def _install_managed_learner_progress_marker():
    """Persist one proof that this run has advanced beyond learner step zero.

    The continual-service shutdown path may safely retire a trainer that hangs while
    shutting down before any optimizer progress exists. Once a positive trainer step
    has ever been observed for this run, the marker remains durable across supervisor
    restarts so shutdown stays fail-closed and preserves that optimizer lineage.
    """

    path = _managed_learner_progress_path()
    if path is None:
        return None

    from mlagents.trainers.trainer_controller import TrainerController

    original = TrainerController.advance
    run_id = os.environ.get("BEES_TRAINING_RUN_ID", "").strip()
    marker_written = False
    try:
        existing = json.loads(path.read_text(encoding="utf-8"))
        marker_written = (
            isinstance(existing, dict)
            and str(existing.get("run_id", "")).strip() == run_id
            and isinstance(existing.get("step"), int)
            and not isinstance(existing.get("step"), bool)
            and int(existing["step"]) > 0
        )
    except (OSError, UnicodeDecodeError, json.JSONDecodeError):
        pass

    def advance_with_progress(controller, env_manager):
        nonlocal marker_written
        result = original(controller, env_manager)
        if marker_written:
            return result

        steps = []
        for trainer in controller.trainers.values():
            try:
                step = int(trainer.get_step)
            except Exception:
                continue
            if step > 0:
                steps.append(step)
        if steps:
            try:
                _atomic_json(
                    path,
                    {
                        "schema_version": 1,
                        "run_id": run_id,
                        "step": max(steps),
                        "updated_unix_seconds": time.time(),
                    },
                )
            except OSError as exc:
                print(
                    "[Bees RL] learner progress marker write deferred: "
                    f"{type(exc).__name__}: {exc}",
                    file=sys.stderr,
                    flush=True,
                )
            else:
                marker_written = True
        return result

    TrainerController.advance = advance_with_progress
    return original


def _handle_model_snapshot_request(trainer, request_path: Path, response_path: Path) -> bool:
    """Export the current in-memory policy at a trainer-thread trajectory boundary."""

    if not request_path.is_file():
        return False
    try:
        request = json.loads(request_path.read_text(encoding="utf-8-sig"))
        if not isinstance(request, dict):
            raise ValueError("snapshot request must be a JSON object")
        request_id = str(request.get("request_id", "")).strip()
        if not request_id or any(ch not in "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_" for ch in request_id):
            raise ValueError("snapshot request_id is invalid")
        requested_run = str(request.get("run_id", "") or "").strip()
        active_run = os.environ.get("BEES_TRAINING_RUN_ID", "").strip()
        if requested_run and active_run and requested_run != active_run:
            raise ValueError(
                f"snapshot request belongs to run {requested_run}, active run is {active_run}"
            )

        step = int(trainer.get_step)
        model_root = Path(str(trainer.model_saver.model_path)).expanduser().resolve()
        model_root.mkdir(parents=True, exist_ok=True)
        output_base = model_root / (
            f"diagnostic-{trainer.brain_name}-{step}-{request_id[:12]}"
        )
        trainer.model_saver.export(str(output_base), trainer.brain_name)
        model_path = output_base.with_suffix(".onnx")
        if not model_path.is_file():
            raise RuntimeError(f"model exporter did not create {model_path}")

        _atomic_json(
            response_path,
            {
                "schema_version": 1,
                "status": "succeeded",
                "request_id": request_id,
                "run_id": os.environ.get("BEES_TRAINING_RUN_ID", ""),
                "step": step,
                "model_path": str(model_path),
                "completed_unix_seconds": time.time(),
            },
        )
        print(
            f"[Bees RL] Diagnostic model snapshot exported at step {step}: {model_path}",
            flush=True,
        )
    except Exception as exc:
        request_id = ""
        try:
            raw = json.loads(request_path.read_text(encoding="utf-8-sig"))
            if isinstance(raw, dict):
                request_id = str(raw.get("request_id", ""))
        except Exception:
            pass
        try:
            _atomic_json(
                response_path,
                {
                    "schema_version": 1,
                    "status": "failed",
                    "request_id": request_id,
                    "run_id": os.environ.get("BEES_TRAINING_RUN_ID", ""),
                    "error": f"{type(exc).__name__}: {exc}",
                    "completed_unix_seconds": time.time(),
                },
            )
        except OSError:
            pass
        print(
            f"[Bees RL] Diagnostic model snapshot failed: {type(exc).__name__}: {exc}",
            file=sys.stderr,
            flush=True,
        )
    finally:
        try:
            request_path.unlink()
        except FileNotFoundError:
            pass
        except OSError:
            pass
    return True


def _install_model_snapshot_requests():
    """Service bundle snapshot requests from the trainer thread without stopping training."""

    request_value = os.environ.get(MODEL_SNAPSHOT_REQUEST_FILE_ENV, "").strip()
    response_value = os.environ.get(MODEL_SNAPSHOT_RESPONSE_FILE_ENV, "").strip()
    if not request_value or not response_value:
        return None

    from mlagents.trainers.trainer.rl_trainer import RLTrainer

    request_path = Path(request_value).expanduser().resolve()
    response_path = Path(response_value).expanduser().resolve()
    original = RLTrainer._maybe_save_model

    def maybe_save_model_with_snapshot(self, step_after_process: int) -> None:
        original(self, step_after_process)
        _handle_model_snapshot_request(self, request_path, response_path)

    RLTrainer._maybe_save_model = maybe_save_model_with_snapshot
    return original


def _install_threaded_trainer_failure_propagation():
    """Fail the learner process when ML-Agents' daemon trainer thread crashes.

    ML-Agents 1.1.0 otherwise prints an exception from trainer_update_func and leaves the
    environment/main thread running indefinitely. On a trainer-thread failure, interrupt the
    main thread, suppress final model/checkpoint writes from the failed in-memory state, and
    re-raise the original exception after learn.main() unwinds.
    """
    from mlagents.trainers.trainer_controller import TrainerController

    original_update = TrainerController.trainer_update_func
    original_save = TrainerController._save_models
    failure = {"exc_info": None}

    def guarded_trainer_update(controller, trainer):
        try:
            return original_update(controller, trainer)
        except BaseException:
            if failure["exc_info"] is None:
                failure["exc_info"] = sys.exc_info()
                print(
                    "[Bees RL] threaded trainer failed; aborting learner without writing "
                    "a checkpoint from the failed in-memory state.",
                    file=sys.stderr,
                    flush=True,
                )
                traceback.print_exception(*failure["exc_info"], file=sys.stderr)
                controller.kill_trainers = True
                _thread.interrupt_main()
            return None

    def guarded_save_models(controller):
        if failure["exc_info"] is not None:
            print(
                "[Bees RL] skipping final model/checkpoint save because the threaded trainer "
                "failed; the last durable checkpoint remains authoritative.",
                file=sys.stderr,
                flush=True,
            )
            return None
        return original_save(controller)

    TrainerController.trainer_update_func = guarded_trainer_update
    TrainerController._save_models = guarded_save_models
    return original_update, original_save, failure


def _restore_threaded_trainer_failure_propagation(state) -> None:
    if state is None:
        return
    from mlagents.trainers.trainer_controller import TrainerController

    original_update, original_save, _failure = state
    TrainerController.trainer_update_func = original_update
    TrainerController._save_models = original_save


def _start_managed_stop_watcher():
    """Interrupt the trainer main thread when its supervisor requests a final checkpoint."""
    path = os.environ.get(MANAGED_STOP_FILE_ENV, "").strip()
    if not path:
        return None, None
    stop_event = threading.Event()

    def watch() -> None:
        while not stop_event.wait(MANAGED_STOP_POLL_SECONDS):
            if os.path.isfile(path):
                _thread.interrupt_main()
                return

    watcher = threading.Thread(
        target=watch,
        name="bees-managed-stop-watcher",
        daemon=True,
    )
    watcher.start()
    return stop_event, watcher


def main() -> None:
    (
        trainer_args,
        torch_threads,
        batch_inference,
        cpu_inference,
    ) = _extract_bees_options(sys.argv[1:])
    trainer_args = _ensure_results_dir(trainer_args)

    if cpu_inference and not batch_inference:
        raise SystemExit(
            f"{CPU_INFERENCE_FLAG} requires {BATCH_INFERENCE_FLAG}; "
            "hybrid CPU inference is implemented by the cross-worker batching path."
        )

    # Install the managed tee before ML-Agents configures logging. Python logging handlers bind
    # their output stream when they are created; installing this later leaves ML-Agents summaries
    # pointed at the original stderr and learner-live.log sees only ordinary Bees print() calls.
    live_log_streams = _install_managed_live_log()

    import mlagents.trainers
    import mlagents.trainers.subprocess_env_manager as subprocess_env_manager_module
    from mlagents import torch_utils
    from mlagents.trainers import learn
    from mlagents.trainers.env_manager import EnvManager
    from mlagents.trainers.subprocess_env_manager import SubprocessEnvManager
    from bees_mlagents_ppo_compat import (
        install_continuous_sigma_guard,
        install_inactive_continuous_action_masking,
        install_value_estimate_key_fix,
        restore_continuous_sigma_guard,
        restore_inactive_continuous_action_masking,
        restore_value_estimate_key,
    )
    from bees_mlagents_structured_policy import (
        install_structured_policy,
        restore_structured_policy,
    )

    actual_version = mlagents.trainers.__version__
    if actual_version != EXPECTED_MLAGENTS_VERSION:
        raise RuntimeError(
            "Training/bees_mlagents_learn.py patches ML-Agents checkpoint loading "
            f"and optional batching for version {EXPECTED_MLAGENTS_VERSION}, but version "
            f"{actual_version} is installed. Verify the newer internals before changing "
            "this version guard."
        )

    structured_policy_state = install_structured_policy()
    original_explicit_outcome_elo = _install_explicit_outcome_elo()
    original_value_estimate_key = None
    original_sigma_forward = None
    try:
        original_value_estimate_key = install_value_estimate_key_fix()
        install_inactive_continuous_action_masking()
        original_sigma_forward = install_continuous_sigma_guard()
    except Exception:
        restore_continuous_sigma_guard(original_sigma_forward)
        restore_inactive_continuous_action_masking()
        restore_value_estimate_key(original_value_estimate_key)
        _restore_explicit_outcome_elo(original_explicit_outcome_elo)
        restore_structured_policy(structured_policy_state)
        raise
    print("[Bees RL] Structured dual-faction entity/weapon policy: enabled")
    print("[Bees RL] MA-POCA inverse-group-size gradient balancing: enabled")
    print("[Bees RL] Inactive weapon-action masking: enabled")
    print("[Bees RL] Continuous sigma guard: enabled")
    print("[Bees RL] PPO/POCA value-estimate/return buffer key separation: enabled")
    print("[Bees RL] Self-play ELO explicit battle-outcome classification: enabled")

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
    previous_sigbreak_handler = _install_windows_break_interrupt()
    managed_stop_event, managed_stop_watcher = _start_managed_stop_watcher()
    threaded_failure_state = _install_threaded_trainer_failure_propagation()
    original_maybe_save_model = _install_model_snapshot_requests()
    original_trainer_advance = _install_managed_learner_progress_marker()
    torch_utils.torch.load = device_safe_torch_load
    sys.argv = [previous_argv[0], *trainer_args]
    threaded_failure = None
    try:
        learn.main()
        if threaded_failure_state is not None:
            threaded_failure = threaded_failure_state[2]["exc_info"]
    finally:
        sys.argv = previous_argv
        _restore_managed_live_log(live_log_streams)
        if managed_stop_event is not None:
            managed_stop_event.set()
        if managed_stop_watcher is not None:
            managed_stop_watcher.join(timeout=1.0)
        if previous_sigbreak_handler is not None:
            signal.signal(signal.SIGBREAK, previous_sigbreak_handler)
        if original_maybe_save_model is not None:
            from mlagents.trainers.trainer.rl_trainer import RLTrainer
            RLTrainer._maybe_save_model = original_maybe_save_model
        if original_trainer_advance is not None:
            from mlagents.trainers.trainer_controller import TrainerController
            TrainerController.advance = original_trainer_advance
        _restore_threaded_trainer_failure_propagation(threaded_failure_state)
        torch_utils.torch.load = original_torch_load
        restore_continuous_sigma_guard(original_sigma_forward)
        restore_inactive_continuous_action_masking()
        restore_value_estimate_key(original_value_estimate_key)
        _restore_explicit_outcome_elo(original_explicit_outcome_elo)
        restore_structured_policy(structured_policy_state)
        if original_queue_steps is not None:
            SubprocessEnvManager._queue_steps = original_queue_steps
        if original_env_step is not None:
            SubprocessEnvManager._step = original_env_step
        if original_process_step_infos is not None:
            EnvManager._process_step_infos = original_process_step_infos
        if original_worker is not None:
            subprocess_env_manager_module.worker = original_worker

    if threaded_failure is not None:
        _exc_type, exc, tb = threaded_failure
        raise exc.with_traceback(tb)


if __name__ == "__main__":
    main()