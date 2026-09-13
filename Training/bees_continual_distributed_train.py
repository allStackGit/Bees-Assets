"""Run Bees continual learning with remote ML-Agents rollout workers.

All candidate registration, historical-league behavior and behavioral cloning remain owned by
bees_continual_train. This wrapper only changes where selected Unity environment workers run and
publishes the content-hashed remote session spec that pins their exact environment arguments.
"""

from __future__ import annotations

import sys
from typing import Optional, Sequence

import bees_continual_train as continual
import bees_distributed_training as distributed


def main(argv: Optional[Sequence[str]] = None) -> int:
    raw_args = list(sys.argv[1:] if argv is None else argv)
    trainer_args, options = distributed.extract_distributed_options(raw_args)
    total_envs, base_port, external_worker_ids = distributed.training_topology(
        trainer_args, options
    )

    original_factory = None
    if options.enabled:
        spec_path = distributed.write_remote_worker_spec(
            options.remote_spec,
            trainer_args,
            base_port=base_port,
            worker_ids=external_worker_ids,
        )
        original_factory = distributed.install_external_worker_factory(
            total_envs=total_envs,
            external_worker_ids=external_worker_ids,
        )
        print(
            "[Bees distributed] "
            + distributed.describe_topology(base_port, total_envs, external_worker_ids)
        )
        print(f"[Bees distributed] remote_session_spec={spec_path}")
        print(
            "[Bees distributed] One central PPO learner owns optimizer/checkpoints; "
            "external machines contribute ordinary fresh ML-Agents rollouts pinned by the session spec."
        )

    try:
        return continual.main(trainer_args)
    finally:
        distributed.restore_external_worker_factory(original_factory)


if __name__ == "__main__":
    raise SystemExit(main())
