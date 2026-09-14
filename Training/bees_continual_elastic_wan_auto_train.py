"""Elastic-WAN variant of the automatic continual Bees trainer.

Exeter keeps the ordinary ``--num-envs`` local rollout processes. Zero to the configured maximum
remote actors may join or leave the same generation without restarting the learner; each remote
machine declares its own 1-64 environment count.
"""

from __future__ import annotations

import sys
from typing import Optional, Sequence

import bees_continual_auto_train as continual_auto
import bees_elastic_wan_policy_transport as policy_transport
import bees_elastic_wan_slot_safety as slot_safety
import bees_elastic_wan_training as elastic


def main(argv: Optional[Sequence[str]] = None) -> int:
    raw_args = list(sys.argv[1:] if argv is None else argv)
    trainer_args, options = elastic.extract_elastic_wan_options(raw_args)
    if not options.enabled:
        raise SystemExit(
            f"{elastic.WAN_ACTORS_FLAG} is required for the elastic WAN continual trainer."
        )

    original_policy_transport = policy_transport.install_portable_policy_transport()
    original_broker = slot_safety.install_slot_safety()
    patch = None
    try:
        patch = elastic.install_elastic_wan_env_manager(options)
        return continual_auto.main(trainer_args)
    finally:
        elastic.restore_elastic_wan_env_manager(patch)
        slot_safety.restore_slot_safety(original_broker)
        policy_transport.restore_portable_policy_transport(original_policy_transport)


if __name__ == "__main__":
    raise SystemExit(main())
