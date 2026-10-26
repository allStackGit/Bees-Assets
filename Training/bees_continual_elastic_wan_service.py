"""Autonomous continual-service entry point for an elastic WAN rollout pool.

The normal continual train -> release -> publish state machine remains authoritative. Exeter may run
zero or more configured local ``--num-envs`` environments, while zero through twelve remote actor
machines may join or leave the training phase dynamically. With zero local environments the training
phase waits safely for remote actors; release/publish behavior is unchanged.
"""

from __future__ import annotations

from dataclasses import replace
import re
import sys
from pathlib import Path
from typing import List, Optional, Sequence, Tuple

import bees_continual_service as service
import bees_elastic_wan_training as elastic
import bees_mlagents_learn as learner_launcher



def extract_learner_optimization_args(
    argv: Sequence[str],
) -> Tuple[List[str], List[str]]:
    """Strip learner-only execution flags so the outer service parser never sees them."""

    boolean_flags = {
        learner_launcher.PPO_SYNC_CLEANUP_FLAG,
        learner_launcher.PPO_PREFETCH_FLAG,
        learner_launcher.PPO_CRITIC_BASELINE_OVERLAP_FLAG,
        learner_launcher.PPO_CUDA_GRAPHS_FLAG,
        learner_launcher.PPO_PROFILE_FLAG,
        "--bees-ppo-nsight",
        learner_launcher.PPO_DIAGNOSTICS_FLAG,
    }
    value_flags = {learner_launcher.PPO_STREAM_SHARDS_FLAG}
    cleaned: List[str] = []
    selected: List[str] = []
    index = 0
    while index < len(argv):
        argument = argv[index]
        if argument in boolean_flags:
            selected.append(argument)
            index += 1
            continue
        if any(argument.startswith(flag + "=") for flag in boolean_flags):
            selected.append(argument)
            index += 1
            continue
        if argument in value_flags:
            if index + 1 >= len(argv):
                raise ValueError(f"{argument} requires a value")
            selected.extend([argument, argv[index + 1]])
            index += 2
            continue
        if any(argument.startswith(flag + "=") for flag in value_flags):
            selected.append(argument)
            index += 1
            continue
        cleaned.append(argument)
        index += 1
    return cleaned, selected


def _normalize_zero_local_num_envs(argv: Sequence[str]) -> Tuple[List[str], bool]:
    """Let the base service parser validate everything except elastic WAN's explicit zero-local mode.

    ``bees_continual_service`` intentionally rejects zero environments for ordinary local training.
    Rather than weakening that contract, temporarily parse an explicit elastic ``--num-envs=0`` as
    one and restore zero on the immutable ServiceOptions afterward.
    """
    normalized: List[str] = []
    zero_local = False
    seen_num_envs = False
    index = 0
    while index < len(argv):
        argument = argv[index]
        if argument == "--num-envs":
            if seen_num_envs:
                raise ValueError("--num-envs may be specified only once")
            seen_num_envs = True
            if index + 1 >= len(argv):
                raise ValueError("--num-envs requires a value")
            value = argv[index + 1]
            if value == "0":
                normalized.extend(["--num-envs", "1"])
                zero_local = True
            else:
                normalized.extend([argument, value])
            index += 2
            continue
        if argument.startswith("--num-envs="):
            if seen_num_envs:
                raise ValueError("--num-envs may be specified only once")
            seen_num_envs = True
            value = argument.split("=", 1)[1]
            if value == "0":
                normalized.append("--num-envs=1")
                zero_local = True
            else:
                normalized.append(argument)
            index += 1
            continue
        normalized.append(argument)
        index += 1
    return normalized, zero_local


def parse_elastic_service_options(argv: Sequence[str]) -> service.ServiceOptions:
    normalized, zero_local = _normalize_zero_local_num_envs(argv)
    options = service.parse_options(normalized)
    return replace(options, num_envs=0) if zero_local else options


def insert_wan_args_before_environment_args(
    command: list[str],
    wan_args: Sequence[str],
) -> list[str]:
    result = list(command)
    try:
        env_args_index = result.index("--env-args")
    except ValueError:
        result.extend(wan_args)
    else:
        result[env_args_index:env_args_index] = list(wan_args)
    return result


def trainer_config_for_threading(config_path: Path, enabled: bool) -> Path:
    """Create an immutable generation-config variant with the requested threading mode."""
    source = config_path.read_text(encoding="utf-8")
    pattern = re.compile(
        r"^(?P<prefix>\s*threaded:\s*)(?:true|false)(?P<suffix>\s*(?:#.*)?)$",
        re.MULTILINE | re.IGNORECASE,
    )
    replacement = "true" if enabled else "false"
    updated, count = pattern.subn(
        lambda match: match.group("prefix") + replacement + match.group("suffix"),
        source,
    )
    if count != 1:
        raise ValueError(
            f"Expected exactly one threaded entry in trainer config; found {count}: {config_path}"
        )
    if updated == source:
        return config_path
    suffix = "-threaded-on" if enabled else "-threaded-off"
    destination = config_path.with_name(config_path.stem + suffix + config_path.suffix)
    return service._write_immutable_generation_config(
        destination,
        updated.encode("utf-8"),
    )


def main(argv: Optional[Sequence[str]] = None) -> int:
    raw_args = list(sys.argv[1:] if argv is None else argv)
    # The control is learner-side, not an ML-Agents or Unity environment argument.
    # Set it before the managed log tee starts; children inherit the selection.
    selected_log_categories = [
        item.split("=", 1)[1]
        for item in raw_args if item.startswith("--bees-log-categories=")
    ]
    if len(selected_log_categories) > 1:
        raise ValueError("--bees-log-categories may be supplied only once")
    if selected_log_categories:
        learner_launcher.parse_log_categories(selected_log_categories[0])
        os.environ[learner_launcher.LOG_CATEGORIES_ENV] = selected_log_categories[0]
        raw_args = [
            item for item in raw_args
            if not item.startswith("--bees-log-categories=")
        ]
    local_only = "--bees-local-only" in raw_args
    if local_only:
        raw_args.remove("--bees-local-only")
    raw_args, learner_optimization_args = extract_learner_optimization_args(
        raw_args
    )
    # The profiling switch belongs to the service, not ML-Agents or Unity.
    nsight_flag = "--bees-ppo-nsight"
    nsight_values = [
        arg for arg in learner_optimization_args
        if arg == nsight_flag or arg.startswith(nsight_flag + "=")
    ]
    if len(nsight_values) > 1:
        raise ValueError("Expected at most one --bees-ppo-nsight=true/false argument")
    nsight_value = (
        nsight_values[0].split("=", 1)[1].lower()
        if "=" in nsight_values[0] else "true"
    ) if nsight_values else "false"
    if nsight_value not in ("true", "false"):
        raise ValueError("--bees-ppo-nsight must be true or false")
    nsight_mode = nsight_value == "true"
    learner_optimization_args = [
        arg for arg in learner_optimization_args if arg not in nsight_values
    ]
    raw_args, threaded_mode, max_policy_lag = elastic.extract_threaded_options(raw_args)
    service_args, actor_options = elastic.extract_elastic_wan_options(raw_args)
    if local_only == actor_options.enabled:
        print(
            "Local-only mode must omit WAN actor options; distributed mode requires "
            f"{elastic.WAN_ACTORS_FLAG}.",
            file=sys.stderr,
        )
        return 2

    try:
        if not local_only:
            from bees_wan_actor_training import load_auth_token

            load_auth_token(actor_options.auth_token_file or "")
        options = parse_elastic_service_options(service_args)
        if local_only and options.num_envs <= 0:
            raise ValueError("local-only mode needs at least one direct local environment")
    except (OSError, ValueError, SystemExit) as exc:
        print(f"Continuous-learning elastic WAN service configuration error: {exc}", file=sys.stderr)
        return 2

    original_training_command = service.training_command

    def elastic_training_command(
        options: service.ServiceOptions,
        index: int,
        *,
        resume: bool,
        force_fresh: bool = False,
    ):
        command = original_training_command(
            options,
            index,
            resume=resume,
            force_fresh=force_fresh,
        )
        if not local_only:
            command[1] = str(
                options.runtime_training_root / "bees_continual_elastic_wan_auto_train.py"
            )
        command[2] = str(
            trainer_config_for_threading(Path(command[2]), threaded_mode)
        )
        wan_args = [] if local_only else [
            f"{elastic.WAN_ACTORS_FLAG}={actor_options.max_actors}",
            f"{elastic.WAN_MIN_ACTORS_FLAG}={actor_options.min_actors}",
            f"{elastic.WAN_BROKER_PORT_FLAG}={actor_options.broker_port}",
            f"{elastic.WAN_AUTH_TOKEN_FILE_FLAG}="
            f"{Path(actor_options.auth_token_file or '').expanduser().resolve()}",
            f"{elastic.WAN_MAX_QUEUED_BATCHES_FLAG}={actor_options.max_queued_batches}",
            f"{elastic.WAN_LEASE_SECONDS_FLAG}={actor_options.actor_lease_seconds:g}",
            f"{elastic.WAN_DIAGNOSTICS_FLAG}={'true' if actor_options.worker_diagnostics else 'false'}",
        ]
        if threaded_mode and not local_only:
            wan_args.extend(
                [
                    elastic.THREADED_FLAG,
                    f"{elastic.POLICY_LAG_FLAG}={max_policy_lag}",
                ]
            )
        wan_args.extend(learner_optimization_args)
        # ML-Agents --env-args consumes the remainder of the command. Keep elastic WAN
        # trainer flags before it so only the server-owned Unity arguments reach the player.
        command = insert_wan_args_before_environment_args(command, wan_args)
        if not nsight_mode:
            return command

        import datetime
        import os
        import shutil

        configured = os.environ.get("BEES_NSYS_EXE", "").strip()
        candidates = [Path(configured)] if configured else []
        discovered = shutil.which("nsys.exe" if os.name == "nt" else "nsys")
        if discovered:
            candidates.append(Path(discovered))
        if os.name == "nt":
            program_files = Path(os.environ.get("ProgramFiles", r"C:\Program Files"))
            candidates.extend(sorted(
                program_files.glob(
                    "NVIDIA Corporation/Nsight Systems */target-windows-x64/nsys.exe"
                ),
                reverse=True,
            ))
        executable = next((p for p in candidates if p.is_file()), None)
        if executable is None:
            raise ValueError(
                "Nsight Systems CLI not found; install it or set BEES_NSYS_EXE "
                "to the nsys.exe path, or omit -PpoNsight."
            )
        output_dir = options.root / "diagnostics" / "nsight"
        output_dir.mkdir(parents=True, exist_ok=True)
        stamp = datetime.datetime.now(datetime.timezone.utc).strftime(
            "%Y%m%dT%H%M%S%fZ"
        )
        output = output_dir / (
            f"{options.run_id}-{service.generation_id(index)}-{stamp}"
        )
        os.environ["BEES_POCA_NSIGHT_SAMPLE"] = "1"
        # PyTorch emits ordinary NVTX string ranges, not NVTX registered
        # strings. Enable those strings as capture triggers in Nsight.
        os.environ["NSYS_NVTX_PROFILER_REGISTER_ONLY"] = "0"
        print(
            f"[Bees Nsight] capturing one warmed-up PPO minibatch; "
            f"report_prefix={output}", flush=True
        )
        # One asynchronous report is written when the NVTX range closes,
        # without shutting down Nsight or terminating the learner.
        # The managed service must keep owning the launched process until
        # training actually ends, so avoid stop-shutdown here.
        return [
            str(executable), "profile",
            # Nsight Systems on Windows does not offer osrt tracing.
            # Keep CUDA API/kernel events and the NVTX PPO stage annotations.
            "--trace=" + (
                "cuda,nvtx" if os.name == "nt" else "cuda,nvtx,osrt"
            ),
            "--sample=none",
            # CPU context-switch collection on Windows requires elevation.
            # It is not needed for the CUDA/NVTX kernel-launch timeline.
            "--cpuctxsw=none",
            "--capture-range=nvtx",
            "--nvtx-capture=BeesPpoSample@*",
            "--capture-range-end=repeat:1:async",
            # Nsight Systems Windows CLI accepts booleans rather than the
            # Linux signals/none options. Never kill the training process.
            "--kill=" + ("false" if os.name == "nt" else "none"),
            "--output=" + str(output),
            *command,
        ]

    service.training_command = elastic_training_command
    if threaded_mode:
        print(
            "[Bees WAN] runtime threaded training enabled; "
            f"rolling policy lag <= {max_policy_lag}."
        )
    try:
        return service.run_service(options)
    finally:
        service.training_command = original_training_command


if __name__ == "__main__":
    raise SystemExit(main())
