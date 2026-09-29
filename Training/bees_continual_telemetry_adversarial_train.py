"""Launch continual training with reviewed live-telemetry-derived scenario pressure.

Only matchup/geometry descriptors reach Unity. Recorded live observations/actions remain archived
off-policy evidence and are never inserted into PPO minibatches.
"""

from __future__ import annotations

import sys
from pathlib import Path
from typing import List, Optional, Sequence, Tuple

import bees_continual_train as continual_train
from bees_continual_adversarial_train import inject_unity_pressure_arg, record_run_selection
from bees_continual_learning import ContinualLearningError, ContinualLearningStore, load_config
from bees_continual_telemetry_adversarial import encode_telemetry_pressure_for_unity


TELEMETRY_SCENARIOS_FLAG = "--continual-telemetry-scenarios"


def extract_telemetry_scenarios(argv: Sequence[str]) -> Tuple[List[str], Tuple[str, ...]]:
    cleaned: List[str] = []
    scenario_ids: Optional[Tuple[str, ...]] = None
    index = 0
    while index < len(argv):
        argument = argv[index]
        value = None
        if argument == TELEMETRY_SCENARIOS_FLAG:
            if index + 1 >= len(argv) or argv[index + 1].startswith("--"):
                raise SystemExit(f"{TELEMETRY_SCENARIOS_FLAG} requires a comma-separated value.")
            value = argv[index + 1]
            index += 2
        else:
            prefix = TELEMETRY_SCENARIOS_FLAG + "="
            if argument.startswith(prefix):
                value = argument[len(prefix):]
                index += 1
            else:
                cleaned.append(argument)
                index += 1
                continue

        if scenario_ids is not None:
            raise SystemExit(f"{TELEMETRY_SCENARIOS_FLAG} may be specified only once.")
        values = tuple(token.strip() for token in value.split(",") if token.strip())
        if not values:
            raise SystemExit(f"{TELEMETRY_SCENARIOS_FLAG} requires at least one scenario ID.")
        if len(set(values)) != len(values):
            raise SystemExit(f"{TELEMETRY_SCENARIOS_FLAG} contains duplicate scenario IDs.")
        scenario_ids = values

    if scenario_ids is None:
        raise SystemExit(
            f"{TELEMETRY_SCENARIOS_FLAG} is required when using the telemetry adversarial launcher."
        )
    return cleaned, scenario_ids


def prepare_telemetry_adversarial_training_args(
    argv: Sequence[str],
) -> Tuple[List[str], Path, Tuple[str, ...]]:
    cleaned, scenario_ids = extract_telemetry_scenarios(argv)
    trainer_args, options = continual_train.extract_continual_options(cleaned)
    if not options.enabled:
        raise SystemExit(
            f"{continual_train.CONTINUAL_ROOT_FLAG} is required for telemetry-derived adversarial training."
        )

    config = load_config(options.config) if options.config else load_config()
    store = ContinualLearningStore(options.root, config)
    store.initialize()
    encoded, geometry_catalog = encode_telemetry_pressure_for_unity(store, scenario_ids)
    _, run_id, _ = continual_train.infer_run_context(trainer_args)
    record = record_run_selection(
        store,
        run_id=run_id,
        scenario_ids=scenario_ids,
        encoded=encoded,
        geometry_catalog=geometry_catalog,
    )
    return (
        inject_unity_pressure_arg(cleaned, encoded, geometry_catalog),
        record,
        scenario_ids,
    )


def main(argv: Optional[Sequence[str]] = None) -> int:
    raw_args = list(sys.argv[1:] if argv is None else argv)
    try:
        prepared, record, scenario_ids = prepare_telemetry_adversarial_training_args(raw_args)
    except ContinualLearningError as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 2

    print(
        f"[Bees continual] telemetry-derived adversarial scenarios={len(scenario_ids)} "
        f"selection_record={record}"
    )
    return continual_train.main(prepared)


if __name__ == "__main__":
    raise SystemExit(main())
