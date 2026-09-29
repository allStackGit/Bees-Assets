"""WAN-actor variant of the automatic continual Bees trainer.

Automatic gameplay-pressure selection still happens in ``bees_continual_auto_train``. This thin
wrapper only installs the WAN EnvManager before delegating, so the final generated ML-Agents
``RunOptions`` (including immutable generation pressure) are what remote actors receive.
"""

from __future__ import annotations

import sys
from typing import Optional, Sequence

import bees_continual_auto_train as continual_auto
import bees_wan_actor_training as wan


def main(argv: Optional[Sequence[str]] = None) -> int:
    raw_args = list(sys.argv[1:] if argv is None else argv)
    trainer_args, options = wan.extract_wan_actor_options(raw_args)
    if not options.enabled:
        raise SystemExit(
            f"{wan.WAN_ACTORS_FLAG} is required for the WAN continual trainer launcher."
        )

    original_manager = wan.install_wan_actor_env_manager(options)
    try:
        return continual_auto.main(trainer_args)
    finally:
        wan.restore_wan_actor_env_manager(original_manager)


if __name__ == "__main__":
    raise SystemExit(main())
