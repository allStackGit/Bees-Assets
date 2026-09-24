"""Autonomous continual-service entry point for WAN rollout actors.

This preserves ``bees_continual_service``'s train -> release -> publish state machine and changes only
the training phase. Each generation starts ``bees_continual_wan_auto_train.py`` with the same
persistent run/checkpoint lineage while remote actor machines perform simulation and inference.
"""

from __future__ import annotations

import sys
from pathlib import Path
from typing import List, Optional, Sequence

import bees_continual_service as service
import bees_wan_actor_training as wan


def _replace_num_envs(argv: Sequence[str], value: int) -> List[str]:
    cleaned: List[str] = []
    index = 0
    while index < len(argv):
        argument = argv[index]
        if argument == "--num-envs":
            if index + 1 >= len(argv):
                raise SystemExit("--num-envs requires a value")
            index += 2
            continue
        if argument.startswith("--num-envs="):
            index += 1
            continue
        cleaned.append(argument)
        index += 1
    cleaned.append(f"--num-envs={value}")
    return cleaned


def main(argv: Optional[Sequence[str]] = None) -> int:
    raw_args = list(sys.argv[1:] if argv is None else argv)
    service_args, actor_options = wan.extract_wan_actor_options(raw_args)
    if not actor_options.enabled:
        print(
            f"Continuous-learning WAN service requires {wan.WAN_ACTORS_FLAG}.",
            file=sys.stderr,
        )
        return 2

    try:
        wan.load_auth_token(actor_options.auth_token_file or "")
        service_args = _replace_num_envs(service_args, actor_options.total_envs)
        options = service.parse_options(service_args)
    except (OSError, ValueError, SystemExit) as exc:
        print(f"Continuous-learning WAN service configuration error: {exc}", file=sys.stderr)
        return 2

    original_training_command = service.training_command

    def wan_training_command(options: service.ServiceOptions, index: int, *, resume: bool):
        command = original_training_command(options, index, resume=resume)
        command[1] = str(options.assets_root / "Training" / "bees_continual_wan_auto_train.py")
        command.extend(
            [
                f"{wan.WAN_ACTORS_FLAG}={actor_options.actor_count}",
                f"{wan.WAN_ENVS_PER_ACTOR_FLAG}={actor_options.envs_per_actor}",
                f"{wan.WAN_MIN_ACTORS_FLAG}={actor_options.min_actors}",
                f"{wan.WAN_BROKER_PORT_FLAG}={actor_options.broker_port}",
                f"{wan.WAN_AUTH_TOKEN_FILE_FLAG}={Path(actor_options.auth_token_file or '').expanduser().resolve()}",
                f"{wan.WAN_MAX_QUEUED_BATCHES_FLAG}={actor_options.max_queued_batches}",
            ]
        )
        return command

    service.training_command = wan_training_command
    try:
        return service.run_service(options)
    finally:
        service.training_command = original_training_command


if __name__ == "__main__":
    raise SystemExit(main())
