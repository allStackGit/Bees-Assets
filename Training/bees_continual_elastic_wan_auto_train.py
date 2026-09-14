"""Elastic-WAN variant of the automatic continual Bees trainer.

Exeter keeps the ordinary ``--num-envs`` local rollout processes. Zero to the configured maximum
remote actors may join or leave the same generation without restarting the learner; each remote
machine declares its own 1-64 environment count.
"""

from __future__ import annotations

import sys
from typing import Optional, Sequence

import bees_continual_auto_train as continual_auto
import bees_elastic_wan_training as elastic


def main(argv: Optional[Sequence[str]] = None) -> int:
    raw_args = list(sys.argv[1:] if argv is None else argv)
    trainer_args, options = elastic.extract_elastic_wan_options(raw_args)
    if not options.enabled:
        raise SystemExit(
            f"{elastic.WAN_ACTORS_FLAG} is required for the elastic WAN continual trainer."
        )

    patch = elastic.install_elastic_wan_env_manager(options)
    try:
        return continual_auto.main(trainer_args)
    finally:
        elastic.restore_elastic_wan_env_manager(patch)


if __name__ == "__main__":
    raise SystemExit(main())
