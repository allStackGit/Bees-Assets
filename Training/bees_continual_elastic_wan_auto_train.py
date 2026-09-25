"""Elastic-WAN variant of the automatic continual Bees trainer.

Exeter may run zero or more local ``--num-envs`` rollout processes. Zero to the configured maximum
remote actors may join or leave the same generation without restarting the learner; each remote
machine declares its own 1-64 environment count. With zero local environments Exeter is a pure PPO
learner/checkpoint authority and pauses safely whenever no remote rollout actor is connected.
"""

from __future__ import annotations

import sys
from typing import Optional, Sequence

import bees_continual_auto_train as continual_auto
import bees_elastic_wan_policy_transport as policy_transport
import bees_elastic_wan_slot_safety as slot_safety
import bees_elastic_wan_training as elastic
import bees_elastic_wan_zero_local as zero_local

def _normalize_zero_local_num_envs_for_mlagents(
    argv: Sequence[str],
) -> tuple[list[str], bool]:
    """Present ML-Agents with one env while preserving Bees' explicit zero-local mode.

    ML-Agents 1.1.0 rejects --num-envs=0 during option parsing, before Bees' custom
    EnvManager can take ownership. Rewrite only an explicit zero to one for that
    parser boundary; the returned flag tells the custom manager to create zero
    local Unity processes.
    """
    normalized: list[str] = []
    zero_local = False
    seen_num_envs = False
    index = 0
    while index < len(argv):
        argument = argv[index]
        if argument == "--num-envs":
            if seen_num_envs:
                raise ValueError("--num-envs may be specified only once")
            seen_num_envs = True
            if index + 1 >= len(argv):
                raise ValueError("--num-envs requires a value")
            value = argv[index + 1]
            if value == "0":
                normalized.extend(["--num-envs", "1"])
                zero_local = True
            else:
                normalized.extend([argument, value])
            index += 2
            continue
        if argument.startswith("--num-envs="):
            if seen_num_envs:
                raise ValueError("--num-envs may be specified only once")
            seen_num_envs = True
            value = argument.split("=", 1)[1]
            if value == "0":
                normalized.append("--num-envs=1")
                zero_local = True
            else:
                normalized.append(argument)
            index += 1
            continue
        normalized.append(argument)
        index += 1
    return normalized, zero_local



def main(argv: Optional[Sequence[str]] = None) -> int:
    raw_args = list(sys.argv[1:] if argv is None else argv)
    trainer_args, options = elastic.extract_elastic_wan_options(raw_args)
    if not options.enabled:
        raise SystemExit(
            f"{elastic.WAN_ACTORS_FLAG} is required for the elastic WAN continual trainer."
        )
    trainer_args, zero_local_requested = _normalize_zero_local_num_envs_for_mlagents(
        trainer_args
    )

    original_policy_transport = policy_transport.install_portable_policy_transport()
    original_broker = slot_safety.install_slot_safety()
    patch = None
    try:
        patch = zero_local.install_elastic_wan_env_manager(
            options,
            force_zero_local=zero_local_requested,
        )
        return continual_auto.main(trainer_args)
    finally:
        zero_local.restore_elastic_wan_env_manager(patch)
        slot_safety.restore_slot_safety(original_broker)
        policy_transport.restore_portable_policy_transport(original_policy_transport)


if __name__ == "__main__":
    raise SystemExit(main())
