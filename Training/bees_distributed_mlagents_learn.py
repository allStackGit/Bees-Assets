"""Launch one authoritative Bees ML-Agents trainer with local and remote rollout workers.

Remote workers use the normal ML-Agents Unity protocol through loopback SSH forwards. The central
trainer remains the only optimizer/checkpoint owner.
"""

from __future__ import annotations

import sys
from typing import Optional, Sequence

import bees_distributed_training as distributed
import bees_mlagents_learn as launcher


def main(argv: Optional[Sequence[str]] = None) -> int:
    raw_args = list(sys.argv[1:] if argv is None else argv)
    trainer_args, options = distributed.extract_distributed_options(raw_args)
    total_envs, base_port, external_worker_ids = distributed.training_topology(
        trainer_args, options
    )

    original_factory = None
    if options.enabled:
        original_factory = distributed.install_external_worker_factory(
            total_envs=total_envs,
            external_worker_ids=external_worker_ids,
        )
        print(
            "[Bees distributed] "
            + distributed.describe_topology(base_port, total_envs, external_worker_ids)
        )
        print(
            "[Bees distributed] External ML-Agents ports bind to 127.0.0.1 only; "
            "connect remote machines through SSH/VPN forwarding rather than exposing gRPC."
        )

    original_argv = sys.argv
    try:
        sys.argv = [original_argv[0], *trainer_args]
        launcher.main()
        return 0
    finally:
        sys.argv = original_argv
        distributed.restore_external_worker_factory(original_factory)


if __name__ == "__main__":
    raise SystemExit(main())
