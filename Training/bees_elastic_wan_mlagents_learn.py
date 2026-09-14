"""Launch one authoritative ML-Agents learner with optional local envs plus elastic WAN actors."""

from __future__ import annotations

import sys
from typing import Optional, Sequence

import bees_elastic_wan_policy_transport as policy_transport
import bees_elastic_wan_slot_safety as slot_safety
import bees_elastic_wan_training as elastic
import bees_elastic_wan_zero_local as zero_local
import bees_mlagents_learn as launcher


def main(argv: Optional[Sequence[str]] = None) -> int:
    raw_args = list(sys.argv[1:] if argv is None else argv)
    trainer_args, options = elastic.extract_elastic_wan_options(raw_args)
    if not options.enabled:
        raise SystemExit(
            f"{elastic.WAN_ACTORS_FLAG} is required for the elastic WAN learner launcher."
        )

    original_policy_transport = policy_transport.install_portable_policy_transport()
    original_broker = slot_safety.install_slot_safety()
    patch = None
    original_argv = sys.argv
    try:
        patch = zero_local.install_elastic_wan_env_manager(options)
        sys.argv = [original_argv[0], *trainer_args]
        launcher.main()
        return 0
    finally:
        sys.argv = original_argv
        zero_local.restore_elastic_wan_env_manager(patch)
        slot_safety.restore_slot_safety(original_broker)
        policy_transport.restore_portable_policy_transport(original_policy_transport)


if __name__ == "__main__":
    raise SystemExit(main())
