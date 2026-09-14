"""Autonomous continual-service entry point for an elastic WAN rollout pool.

The normal continual train -> release -> publish state machine remains authoritative. Exeter keeps
its configured local ``--num-envs`` environments, while zero through twelve remote actor machines
may join or leave the training phase dynamically. Remote actors do not exist during release/publish;
the persistent helpers simply wait for the next training broker session.
"""

from __future__ import annotations

import sys
from pathlib import Path
from typing import Optional, Sequence

import bees_continual_service as service
import bees_elastic_wan_training as elastic


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
        options = service.parse_options(service_args)
        if options.num_envs <= 0:
            raise ValueError("Elastic WAN service requires at least one local Exeter environment.")
    except (OSError, ValueError, SystemExit) as exc:
        print(f"Continuous-learning elastic WAN service configuration error: {exc}", file=sys.stderr)
        return 2

    original_training_command = service.training_command

    def elastic_training_command(
        options: service.ServiceOptions,
        index: int,
        *,
        resume: bool,
    ):
        command = original_training_command(options, index, resume=resume)
        command[1] = str(
            options.assets_root / "Training" / "bees_continual_elastic_wan_auto_train.py"
        )
        command.extend(
            [
                f"{elastic.WAN_ACTORS_FLAG}={actor_options.max_actors}",
                f"{elastic.WAN_MIN_ACTORS_FLAG}={actor_options.min_actors}",
                f"{elastic.WAN_BROKER_PORT_FLAG}={actor_options.broker_port}",
                f"{elastic.WAN_AUTH_TOKEN_FILE_FLAG}="
                f"{Path(actor_options.auth_token_file or '').expanduser().resolve()}",
                f"{elastic.WAN_MAX_QUEUED_BATCHES_FLAG}={actor_options.max_queued_batches}",
                f"{elastic.WAN_LEASE_SECONDS_FLAG}={actor_options.actor_lease_seconds:g}",
            ]
        )
        return command

    service.training_command = elastic_training_command
    try:
        return service.run_service(options)
    finally:
        service.training_command = original_training_command


if __name__ == "__main__":
    raise SystemExit(main())
