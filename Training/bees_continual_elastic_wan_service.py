"""Autonomous continual-service entry point for an elastic WAN rollout pool.

The normal continual train -> release -> publish state machine remains authoritative. Exeter may run
zero or more configured local ``--num-envs`` environments, while zero through twelve remote actor
machines may join or leave the training phase dynamically. With zero local environments the training
phase waits safely for remote actors; release/publish behavior is unchanged.
"""

from __future__ import annotations

from dataclasses import replace
import sys
from pathlib import Path
from typing import List, Optional, Sequence, Tuple

import bees_continual_service as service
import bees_elastic_wan_training as elastic


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


def main(argv: Optional[Sequence[str]] = None) -> int:
    raw_args = list(sys.argv[1:] if argv is None else argv)
    service_args, actor_options = elastic.extract_elastic_wan_options(raw_args)
    if not actor_options.enabled:
        print(
            f"Continuous-learning elastic WAN service requires {elastic.WAN_ACTORS_FLAG}.",
            file=sys.stderr,
        )
        return 2

    try:
        from bees_wan_actor_training import load_auth_token

        load_auth_token(actor_options.auth_token_file or "")
        options = parse_elastic_service_options(service_args)
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
        command[1] = str(
            options.runtime_training_root / "bees_continual_elastic_wan_auto_train.py"
        )
        wan_args = [
            f"{elastic.WAN_ACTORS_FLAG}={actor_options.max_actors}",
            f"{elastic.WAN_MIN_ACTORS_FLAG}={actor_options.min_actors}",
            f"{elastic.WAN_BROKER_PORT_FLAG}={actor_options.broker_port}",
            f"{elastic.WAN_AUTH_TOKEN_FILE_FLAG}="
            f"{Path(actor_options.auth_token_file or '').expanduser().resolve()}",
            f"{elastic.WAN_MAX_QUEUED_BATCHES_FLAG}={actor_options.max_queued_batches}",
            f"{elastic.WAN_LEASE_SECONDS_FLAG}={actor_options.actor_lease_seconds:g}",
        ]
        # ML-Agents --env-args consumes the remainder of the command. Keep elastic WAN
        # trainer flags before it so only the server-owned Unity arguments reach the player.
        return insert_wan_args_before_environment_args(command, wan_args)

    service.training_command = elastic_training_command
    try:
        return service.run_service(options)
    finally:
        service.training_command = original_training_command


if __name__ == "__main__":
    raise SystemExit(main())
