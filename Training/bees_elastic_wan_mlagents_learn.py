"""Launch one authoritative ML-Agents learner with local Exeter envs plus elastic WAN actors."""

from __future__ import annotations

import sys
from typing import Optional, Sequence

import bees_elastic_wan_training as elastic
import bees_mlagents_learn as launcher


def main(argv: Optional[Sequence[str]] = None) -> int:
    raw_args = list(sys.argv[1:] if argv is None else argv)
    trainer_args, options = elastic.extract_elastic_wan_options(raw_args)
    if not options.enabled:
        raise SystemExit(
            f"{elastic.WAN_ACTORS_FLAG} is required for the elastic WAN learner launcher."
        )

    patch = elastic.install_elastic_wan_env_manager(options)
    original_argv = sys.argv
    try:
        sys.argv = [original_argv[0], *trainer_args]
        launcher.main()
        return 0
    finally:
        sys.argv = original_argv
        elastic.restore_elastic_wan_env_manager(patch)


if __name__ == "__main__":
    raise SystemExit(main())
