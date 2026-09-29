"""Launch one authoritative ML-Agents learner backed by WAN actors.

Remote machines simulate and infer locally; this process alone owns PPO/optimizer/checkpoint state.
The existing direct-gRPC distributed launcher remains available for low-latency topologies.
"""

from __future__ import annotations

import sys
from typing import Optional, Sequence

import bees_mlagents_learn as launcher
import bees_wan_actor_training as wan


def main(argv: Optional[Sequence[str]] = None) -> int:
    raw_args = list(sys.argv[1:] if argv is None else argv)
    trainer_args, options = wan.extract_wan_actor_options(raw_args)
    if not options.enabled:
        raise SystemExit(
            f"{wan.WAN_ACTORS_FLAG} is required for the WAN actor learner launcher."
        )

    original_manager = wan.install_wan_actor_env_manager(options)
    original_argv = sys.argv
    try:
        sys.argv = [original_argv[0], *trainer_args]
        launcher.main()
        return 0
    finally:
        sys.argv = original_argv
        wan.restore_wan_actor_env_manager(original_manager)


if __name__ == "__main__":
    raise SystemExit(main())
