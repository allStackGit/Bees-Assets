"""Narrow PPO diagnostics for the Bees RL 1v1 trainer.

This wrapper imports Training/bees_mlagents_learn.py, then instruments ML-Agents
1.1.0 without changing the environment or PPO algorithm. It samples:

* trajectory environment rewards, old value estimates, bootstrap value, GAE and returns;
* current critic values and PPO value loss at update time;
* actor/critic observation-normalizer state and normalized observation ranges;
* actor/critic parameter scale and Adam first/second moments after checkpoint load;
* optional resume with policy/critic/checkpoint state intact but fresh Adam moments.

Example:

    python Training\\bees_rl_diagnostics.py Training\\rl_1v1_config.yaml ^
      --env="F:\\RLDemo\\Bees RL Training" ^
      --run-id=bees-rl-1v1-full-001 --resume --torch-device=cuda ^
      --bees-batch-inference --bees-cpu-inference

Add --bees-fresh-optimizer-state for the checkpoint-vs-fresh-Adam A/B run.
Use --bees-diag-every=N to change periodic sampling (default: 20).
"""

from __future__ import annotations

import math
import sys
from typing import Dict, Optional, Sequence, Tuple, List

import numpy as np

import bees_mlagents_learn as launcher


FRESH_OPTIMIZER_FLAG = "--bees-fresh-optimizer-state"
DIAGNOSTIC_EVERY_FLAG = "--bees-diag-every"
DEFAULT_DIAGNOSTIC_EVERY = 20
INITIAL_DIAGNOSTIC_SAMPLES = 8


def _parse_positive_int(value: str, flag: str) -> int:
    try:
        parsed = int(value)
    except ValueError as exc:
        raise SystemExit(f"{flag} requires a positive whole number; got {value!r}.") from exc
    if parsed <= 0:
        raise SystemExit(f"{flag} requires a positive whole number; got {value!r}.")
    return parsed


def _extract_diagnostic_options(
    argv: Sequence[str],
) -> Tuple[List[str], bool, int]:
    trainer_args: List[str] = []
    fresh_optimizer_state = False
    diagnostic_every = DEFAULT_DIAGNOSTIC_EVERY
    index = 0

    while index < len(argv):
        argument = argv[index]
        if argument == FRESH_OPTIMIZER_FLAG:
            fresh_optimizer_state = True
            index += 1
            continue

        if argument == DIAGNOSTIC_EVERY_FLAG:
            if index + 1 >= len(argv) or argv[index + 1].startswith("--"):
                raise SystemExit(f"{DIAGNOSTIC_EVERY_FLAG} requires a value.")
            diagnostic_every = _parse_positive_int(
                argv[index + 1], DIAGNOSTIC_EVERY_FLAG
            )
            index += 2
            continue

        prefix = DIAGNOSTIC_EVERY_FLAG + "="
        if argument.startswith(prefix):
            value = argument[len(prefix) :]
            if not value:
                raise SystemExit(f"{DIAGNOSTIC_EVERY_FLAG} requires a value.")
            diagnostic_every = _parse_positive_int(value, DIAGNOSTIC_EVERY_FLAG)
            index += 1
            continue

        trainer_args.append(argument)
        index += 1

    return trainer_args, fresh_optimizer_state, diagnostic_every


def _should_sample(index: int, every: int) -> bool:
    return index <= INITIAL_DIAGNOSTIC_SAMPLES or index % every == 0


def _array_stats(values) -> str:
    array = np.asarray(values, dtype=np.float64).reshape(-1)
    if array.size == 0:
        return "n=0"

    finite_mask = np.isfinite(array)
    finite = array[finite_mask]
    if finite.size == 0:
        return f"n={array.size} finite=0 nonfinite={array.size}"

    return (
        f"n={array.size} finite={finite.size} nonfinite={array.size - finite.size} "
        f"min={finite.min():.6g} max={finite.max():.6g} "
        f"mean={finite.mean():.6g} std={finite.std():.6g} sum={finite.sum():.6g}"
    )


def _scalar(value) -> str:
    try:
        numeric = float(np.asarray(value).reshape(-1)[0])
    except (TypeError, ValueError, IndexError):
        return repr(value)
    if not math.isfinite(numeric):
        return str(numeric)
    return f"{numeric:.6g}"


def _module_parameter_stats(module) -> str:
    from mlagents.torch_utils import torch

    count = 0
    nonfinite = 0
    sum_squares = 0.0
    max_abs = 0.0
    with torch.no_grad():
        for parameter in module.parameters():
            tensor = parameter.detach()
            count += tensor.numel()
            finite_mask = torch.isfinite(tensor)
            finite_count = int(finite_mask.sum().item())
            nonfinite += tensor.numel() - finite_count
            if finite_count:
                finite_values = tensor[finite_mask]
                max_abs = max(max_abs, float(finite_values.abs().max().item()))
                sum_squares += float((finite_values.double() ** 2).sum().item())

    rms = math.sqrt(sum_squares / max(1, count - nonfinite))
    return (
        f"params={count} nonfinite={nonfinite} "
        f"max_abs={max_abs:.6g} rms={rms:.6g}"
    )


def _optimizer_state_stats(optimizer) -> str:
    from mlagents.torch_utils import torch

    summaries = []
    for key in ("exp_avg", "exp_avg_sq"):
        count = 0
        nonfinite = 0
        sum_squares = 0.0
        max_abs = 0.0
        with torch.no_grad():
            for state in optimizer.state.values():
                value = state.get(key)
                if not torch.is_tensor(value):
                    continue
                tensor = value.detach()
                count += tensor.numel()
                finite_mask = torch.isfinite(tensor)
                finite_count = int(finite_mask.sum().item())
                nonfinite += tensor.numel() - finite_count
                if finite_count:
                    finite_values = tensor[finite_mask]
                    max_abs = max(max_abs, float(finite_values.abs().max().item()))
                    sum_squares += float((finite_values.double() ** 2).sum().item())
        rms = math.sqrt(sum_squares / max(1, count - nonfinite))
        summaries.append(
            f"{key}:n={count},nonfinite={nonfinite},max_abs={max_abs:.6g},rms={rms:.6g}"
        )
    return " ".join(summaries)


def _log_normalizers(label: str, root_module, raw_observations=None) -> None:
    from mlagents.torch_utils import torch

    if root_module is None or not hasattr(root_module, "named_modules"):
        return

    raw_observations = raw_observations or []
    for name, module in root_module.named_modules():
        required = ("normalization_steps", "running_mean", "running_variance")
        if not all(hasattr(module, item) for item in required):
            continue

        steps_tensor = module.normalization_steps.detach()
        mean_tensor = module.running_mean.detach()
        variance_sum_tensor = module.running_variance.detach()
        try:
            steps = max(1.0, float(steps_tensor.cpu().item()))
            variance_tensor = variance_sum_tensor / steps
            print(
                f"[Bees RL diag normalization] owner={label} module={name or '<root>'} "
                f"steps={steps:.0f} running_mean=({_array_stats(mean_tensor.cpu().numpy())}) "
                f"variance=({_array_stats(variance_tensor.cpu().numpy())})"
            )

            expected_width = mean_tensor.numel()
            for obs_index, observation in enumerate(raw_observations):
                obs_array = np.asarray(observation)
                if obs_array.ndim < 2 or obs_array.shape[-1] != expected_width:
                    continue
                obs_tensor = torch.as_tensor(
                    obs_array,
                    device=mean_tensor.device,
                    dtype=mean_tensor.dtype,
                )
                with torch.no_grad():
                    normalized = module(obs_tensor).detach().cpu().numpy()
                print(
                    f"[Bees RL diag observation] owner={label} module={name or '<root>'} "
                    f"obs_index={obs_index} raw=({_array_stats(obs_array)}) "
                    f"normalized=({_array_stats(normalized)})"
                )
        except Exception as exc:
            print(
                f"[Bees RL diag normalization] owner={label} module={name or '<root>'} "
                f"inspection_failed={type(exc).__name__}:{exc}"
            )


def _log_checkpoint_modules(modules: Dict[str, object]) -> None:
    from mlagents.torch_utils import torch

    print("[Bees RL diag checkpoint] loaded module state:")
    for name, module in modules.items():
        if isinstance(module, torch.nn.Module):
            print(
                f"[Bees RL diag checkpoint] module={name} "
                f"{_module_parameter_stats(module)}"
            )
            _log_normalizers(f"checkpoint:{name}", module)
        elif isinstance(module, torch.optim.Optimizer):
            print(
                f"[Bees RL diag checkpoint] optimizer={name} "
                f"{_optimizer_state_stats(module)}"
            )


class _DiagnosticState:
    def __init__(self, every: int) -> None:
        self.every = every
        self.trajectory_sample_index = 0
        self.update_index = 0
        self.episode_steps: Dict[str, int] = {}
        self.active_trajectory: Optional[dict] = None


def _install_diagnostics(fresh_optimizer_state: bool, diagnostic_every: int):
    import mlagents.trainers

    if mlagents.trainers.__version__ != launcher.EXPECTED_MLAGENTS_VERSION:
        raise RuntimeError(
            "Training/bees_rl_diagnostics.py is written against ML-Agents "
            f"{launcher.EXPECTED_MLAGENTS_VERSION}; installed version is "
            f"{mlagents.trainers.__version__}."
        )

    from mlagents.torch_utils import torch
    from mlagents.trainers.buffer import BufferKey, RewardSignalUtil
    from mlagents.trainers.model_saver.torch_model_saver import TorchModelSaver
    from mlagents.trainers.ppo.optimizer_torch import TorchPPOOptimizer
    from mlagents.trainers.ppo.trainer import PPOTrainer
    import mlagents.trainers.ppo.trainer as ppo_trainer_module
    from mlagents.trainers.torch_entities.utils import ModelUtils
    from mlagents.trainers.trajectory import ObsUtil

    state = _DiagnosticState(diagnostic_every)
    original_get_gae = ppo_trainer_module.get_gae
    original_process_trajectory = PPOTrainer._process_trajectory
    original_update = TorchPPOOptimizer.update
    original_load_model = TorchModelSaver._load_model

    def diagnostic_process_trajectory(self, trajectory):
        key = f"{self.brain_name}:{trajectory.agent_id}"
        segment_steps = len(trajectory.steps)
        accumulated_steps = state.episode_steps.get(key, 0) + segment_steps
        state.episode_steps[key] = accumulated_steps
        previous_context = state.active_trajectory
        state.active_trajectory = {
            "behavior": self.brain_name,
            "agent_id": trajectory.agent_id,
            "segment_steps": segment_steps,
            "episode_steps": accumulated_steps if trajectory.done_reached else None,
            "done": trajectory.done_reached,
            "interrupted": trajectory.interrupted,
        }
        try:
            return original_process_trajectory(self, trajectory)
        finally:
            state.active_trajectory = previous_context
            if trajectory.done_reached:
                state.episode_steps.pop(key, None)

    def diagnostic_get_gae(
        rewards,
        value_estimates,
        value_next=0.0,
        gamma=0.99,
        lambd=0.95,
    ):
        advantages = original_get_gae(
            rewards,
            value_estimates,
            value_next=value_next,
            gamma=gamma,
            lambd=lambd,
        )
        state.trajectory_sample_index += 1
        index = state.trajectory_sample_index
        if _should_sample(index, state.every):
            reward_array = np.asarray(rewards, dtype=np.float64)
            value_array = np.asarray(value_estimates, dtype=np.float64)
            advantage_array = np.asarray(advantages, dtype=np.float64)
            returns = advantage_array + value_array

            reward_only_discounted = np.zeros_like(reward_array)
            running = 0.0
            for step in range(reward_array.size - 1, -1, -1):
                running = reward_array[step] + gamma * running
                reward_only_discounted[step] = running

            context = state.active_trajectory or {}
            print(
                f"[Bees RL diag trajectory] sample={index} "
                f"behavior={context.get('behavior', 'unknown')} "
                f"agent={context.get('agent_id', 'unknown')} "
                f"segment_steps={context.get('segment_steps', reward_array.size)} "
                f"episode_steps={context.get('episode_steps', 'open')} "
                f"done={context.get('done', 'unknown')} "
                f"interrupted={context.get('interrupted', 'unknown')} "
                f"gamma={gamma:.6g} lambda={lambd:.6g} bootstrap={_scalar(value_next)}"
            )
            print(
                f"[Bees RL diag trajectory] rewards=({_array_stats(reward_array)}) "
                f"reward_only_discounted=({_array_stats(reward_only_discounted)})"
            )
            print(
                f"[Bees RL diag trajectory] old_values=({_array_stats(value_array)}) "
                f"advantages=({_array_stats(advantage_array)}) "
                f"gae_returns=({_array_stats(returns)})"
            )
        return advantages

    def diagnostic_update(self, batch, num_sequences):
        state.update_index += 1
        index = state.update_index
        sampled = _should_sample(index, state.every)

        if sampled:
            try:
                print(
                    f"[Bees RL diag update] update={index} "
                    f"batch_advantages=({_array_stats(batch[BufferKey.ADVANTAGES].get_batch())}) "
                    f"env_rewards=({_array_stats(batch[BufferKey.ENVIRONMENT_REWARDS].get_batch())})"
                )
                for name in self.reward_signals:
                    print(
                        f"[Bees RL diag update] update={index} stream={name} "
                        f"old_values=({_array_stats(batch[RewardSignalUtil.value_estimates_key(name)].get_batch())}) "
                        f"returns=({_array_stats(batch[RewardSignalUtil.returns_key(name)].get_batch())})"
                    )

                n_obs = len(self.policy.behavior_spec.observation_specs)
                raw_observations = ObsUtil.from_buffer(batch, n_obs)
                current_obs = [
                    ModelUtils.list_to_tensor(observation)
                    for observation in raw_observations
                ]

                value_memories = [
                    ModelUtils.list_to_tensor(batch[BufferKey.CRITIC_MEMORY][i])
                    for i in range(
                        0,
                        len(batch[BufferKey.CRITIC_MEMORY]),
                        self.policy.sequence_length,
                    )
                ]
                if value_memories:
                    value_memories = torch.stack(value_memories).unsqueeze(0)

                with torch.no_grad():
                    current_values, _ = self.critic.critic_pass(
                        current_obs,
                        memories=value_memories,
                        sequence_length=self.policy.sequence_length,
                    )
                for name, value in current_values.items():
                    print(
                        f"[Bees RL diag update] update={index} stream={name} "
                        f"current_values=({_array_stats(value.detach().cpu().numpy())})"
                    )

                print(
                    f"[Bees RL diag parameters] update={index} actor "
                    f"{_module_parameter_stats(self.policy.actor)}"
                )
                print(
                    f"[Bees RL diag parameters] update={index} critic "
                    f"{_module_parameter_stats(self.critic)}"
                )
                print(
                    f"[Bees RL diag optimizer] update={index} before "
                    f"{_optimizer_state_stats(self.optimizer)}"
                )
                _log_normalizers("actor", self.policy.actor, raw_observations)
                _log_normalizers("critic", self.critic, raw_observations)
            except Exception as exc:
                print(
                    f"[Bees RL diag update] update={index} "
                    f"inspection_failed={type(exc).__name__}:{exc}"
                )

        result = original_update(self, batch, num_sequences)

        if sampled:
            print(
                f"[Bees RL diag update] update={index} "
                f"value_loss={result.get('Losses/Value Loss')} "
                f"policy_loss={result.get('Losses/Policy Loss')} "
                f"learning_rate={result.get('Policy/Learning Rate')}"
            )
            print(
                f"[Bees RL diag optimizer] update={index} after "
                f"{_optimizer_state_stats(self.optimizer)}"
            )
        return result

    def diagnostic_load_model(self, *args, **kwargs):
        if not fresh_optimizer_state:
            result = original_load_model(self, *args, **kwargs)
            _log_checkpoint_modules(self.modules)
            return result

        original_adam_load_state_dict = torch.optim.Adam.load_state_dict
        skipped = {"count": 0}

        def skip_adam_state(optimizer, state_dict):
            skipped["count"] += 1
            print(
                "[Bees RL diag checkpoint] skipping saved Adam optimizer state; "
                "policy, critic, normalization and global step still load normally."
            )

        torch.optim.Adam.load_state_dict = skip_adam_state
        try:
            result = original_load_model(self, *args, **kwargs)
        finally:
            torch.optim.Adam.load_state_dict = original_adam_load_state_dict

        print(
            f"[Bees RL diag checkpoint] fresh_optimizer_state=true "
            f"skipped_adam_loads={skipped['count']}"
        )
        _log_checkpoint_modules(self.modules)
        return result

    PPOTrainer._process_trajectory = diagnostic_process_trajectory
    ppo_trainer_module.get_gae = diagnostic_get_gae
    TorchPPOOptimizer.update = diagnostic_update
    TorchModelSaver._load_model = diagnostic_load_model

    def restore() -> None:
        PPOTrainer._process_trajectory = original_process_trajectory
        ppo_trainer_module.get_gae = original_get_gae
        TorchPPOOptimizer.update = original_update
        TorchModelSaver._load_model = original_load_model

    return restore


def main() -> None:
    (
        trainer_args,
        fresh_optimizer_state,
        diagnostic_every,
    ) = _extract_diagnostic_options(sys.argv[1:])

    previous_argv = sys.argv
    sys.argv = [previous_argv[0], *trainer_args]
    restore = _install_diagnostics(fresh_optimizer_state, diagnostic_every)
    print(
        f"[Bees RL diag] enabled: first {INITIAL_DIAGNOSTIC_SAMPLES} samples, "
        f"then every {diagnostic_every}; "
        f"fresh_optimizer_state={fresh_optimizer_state}"
    )
    try:
        launcher.main()
    finally:
        restore()
        sys.argv = previous_argv


if __name__ == "__main__":
    main()
