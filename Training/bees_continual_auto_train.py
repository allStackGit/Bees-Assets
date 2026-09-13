"""Continual Bees trainer with automatic public-player pressure and optional remote rollout workers.

At launch this wrapper independently processes the current BeesServer telemetry quarantine, freezes
that reviewed pressure into the training run, and then delegates PPO/checkpoint ownership to the
normal continual trainer. A background watcher keeps ingesting/reviewing newly arriving telemetry so
it is ready without manual curation for the next immutable training run. Pressure does not mutate an
already-running Unity population: changing a run's scenario selection in place would make training
lineage non-reproducible and can violate the existing run-selection contract.

When external workers are enabled, their content-hashed session spec is generated only after player-
derived pressure is injected and wrapper-only continual options are removed, so remote Unity
processes receive exactly the same final ``--env-args`` as local workers.
"""

from __future__ import annotations

import sys
import threading
from dataclasses import dataclass
from typing import List, Optional, Sequence, Tuple

import bees_continual_train as continual_train
import bees_distributed_training as distributed
from bees_continual_adversarial import (
    encode_geometry_catalog_for_unity,
    encode_scenarios_for_unity,
)
from bees_continual_adversarial_train import inject_unity_pressure_arg, record_run_selection
from bees_continual_learning import ContinualLearningError, ContinualLearningStore, load_config
from bees_continual_public_auto import (
    DEFAULT_MAX_SELECTION_BATCHES,
    DEFAULT_MAX_TACTIC_SUGGESTIONS,
    DEFAULT_MINIMUM_CONTRIBUTORS,
    DEFAULT_MINIMUM_OCCURRENCES,
    DEFAULT_TOTAL_TARGET_FRACTION,
    DEFAULT_WATCH_SECONDS,
    process_public_learning_once,
)


QUARANTINE_FLAG = "--continual-public-telemetry-quarantine"
WATCH_SECONDS_FLAG = "--continual-public-watch-seconds"
MAX_BATCHES_FLAG = "--continual-public-max-selection-batches"
MAX_SUGGESTIONS_FLAG = "--continual-public-max-tactic-suggestions"
MIN_OCCURRENCES_FLAG = "--continual-public-minimum-occurrences"
MIN_CONTRIBUTORS_FLAG = "--continual-public-minimum-contributors"
TARGET_FRACTION_FLAG = "--continual-public-target-fraction"


@dataclass(frozen=True)
class AutomaticPublicOptions:
    quarantine_root: str
    watch_seconds: float = DEFAULT_WATCH_SECONDS
    maximum_selection_batches: int = DEFAULT_MAX_SELECTION_BATCHES
    maximum_tactic_suggestions: int = DEFAULT_MAX_TACTIC_SUGGESTIONS
    minimum_occurrences: int = DEFAULT_MINIMUM_OCCURRENCES
    minimum_contributors: int = DEFAULT_MINIMUM_CONTRIBUTORS
    total_target_fraction: float = DEFAULT_TOTAL_TARGET_FRACTION


def _read_option(argv: Sequence[str], index: int, flag: str) -> Tuple[Optional[str], int]:
    argument = argv[index]
    if argument == flag:
        if index + 1 >= len(argv) or argv[index + 1].startswith("--"):
            raise SystemExit(f"{flag} requires a value.")
        return argv[index + 1], index + 2
    prefix = flag + "="
    if argument.startswith(prefix):
        value = argument[len(prefix) :]
        if not value:
            raise SystemExit(f"{flag} requires a value.")
        return value, index + 1
    return None, index


def extract_automatic_public_options(
    argv: Sequence[str],
) -> Tuple[List[str], AutomaticPublicOptions]:
    cleaned: List[str] = []
    values = {
        "quarantine_root": None,
        "watch_seconds": DEFAULT_WATCH_SECONDS,
        "maximum_selection_batches": DEFAULT_MAX_SELECTION_BATCHES,
        "maximum_tactic_suggestions": DEFAULT_MAX_TACTIC_SUGGESTIONS,
        "minimum_occurrences": DEFAULT_MINIMUM_OCCURRENCES,
        "minimum_contributors": DEFAULT_MINIMUM_CONTRIBUTORS,
        "total_target_fraction": DEFAULT_TOTAL_TARGET_FRACTION,
    }
    seen = set()
    specs = (
        (QUARANTINE_FLAG, "quarantine_root", str),
        (WATCH_SECONDS_FLAG, "watch_seconds", float),
        (MAX_BATCHES_FLAG, "maximum_selection_batches", int),
        (MAX_SUGGESTIONS_FLAG, "maximum_tactic_suggestions", int),
        (MIN_OCCURRENCES_FLAG, "minimum_occurrences", int),
        (MIN_CONTRIBUTORS_FLAG, "minimum_contributors", int),
        (TARGET_FRACTION_FLAG, "total_target_fraction", float),
    )
    index = 0
    while index < len(argv):
        matched = False
        for flag, key, converter in specs:
            raw, next_index = _read_option(argv, index, flag)
            if raw is None:
                continue
            if flag in seen:
                raise SystemExit(f"{flag} may be specified only once.")
            seen.add(flag)
            try:
                values[key] = converter(raw)
            except ValueError as exc:
                raise SystemExit(f"{flag} has invalid value {raw!r}.") from exc
            index = next_index
            matched = True
            break
        if matched:
            continue
        cleaned.append(argv[index])
        index += 1

    if not values["quarantine_root"]:
        raise SystemExit(
            f"{QUARANTINE_FLAG} is required and must point at BeesServer's "
            "BEES_RL_TELEMETRY_UPLOAD_DIR root."
        )
    if float(values["watch_seconds"]) <= 0:
        raise SystemExit(f"{WATCH_SECONDS_FLAG} must be greater than zero.")
    return cleaned, AutomaticPublicOptions(**values)


def _process(store: ContinualLearningStore, options: AutomaticPublicOptions):
    return process_public_learning_once(
        store,
        options.quarantine_root,
        maximum_selection_batches=options.maximum_selection_batches,
        maximum_tactic_suggestions=options.maximum_tactic_suggestions,
        minimum_occurrences=options.minimum_occurrences,
        minimum_contributors=options.minimum_contributors,
        total_target_fraction=options.total_target_fraction,
    )


def _watch_public_learning(
    stop: threading.Event,
    *,
    root: str,
    config_path: Optional[str],
    options: AutomaticPublicOptions,
) -> None:
    config = load_config(config_path) if config_path else load_config()
    store = ContinualLearningStore(root, config)
    store.initialize()
    while not stop.wait(options.watch_seconds):
        try:
            result = _process(store, options)
            print(
                "[Bees continual] automatic public telemetry refresh "
                f"selected={len(result.get('selected_batches', []))} "
                f"scenarios={len(result.get('scenario_ids', []))} "
                f"rejected={len(result.get('rejected', []))}"
            )
        except Exception as exc:
            # Public data automation must never corrupt/kill the authoritative PPO process. Rejected
            # or temporarily unreadable input stays quarantined and a later scan retries it.
            print(
                f"[Bees continual] automatic public telemetry refresh failed: "
                f"{type(exc).__name__}: {exc}",
                file=sys.stderr,
            )


def main(argv: Optional[Sequence[str]] = None) -> int:
    raw_args = list(sys.argv[1:] if argv is None else argv)
    try:
        auto_cleaned, auto_options = extract_automatic_public_options(raw_args)
        trainer_args, distributed_options = distributed.extract_distributed_options(auto_cleaned)
        base_trainer_args, continual_options = continual_train.extract_continual_options(trainer_args)
        if not continual_options.enabled:
            raise SystemExit(
                f"{continual_train.CONTINUAL_ROOT_FLAG} is required for automatic public learning."
            )

        config = (
            load_config(continual_options.config)
            if continual_options.config
            else load_config()
        )
        store = ContinualLearningStore(continual_options.root, config)
        store.initialize()
        initial = _process(store, auto_options)
        scenario_ids = tuple(initial.get("scenario_ids", ()))

        prepared_args = list(trainer_args)
        selection_record = None
        if scenario_ids:
            encoded = encode_scenarios_for_unity(store, scenario_ids)
            geometry_catalog = encode_geometry_catalog_for_unity(store, scenario_ids)
            prepared_args = inject_unity_pressure_arg(
                prepared_args,
                encoded,
                geometry_catalog,
                "",
            )
            _, run_id, _ = continual_train.infer_run_context(base_trainer_args)
            selection_record = record_run_selection(
                store,
                run_id=run_id,
                scenario_ids=scenario_ids,
                encoded=encoded,
                geometry_catalog=geometry_catalog,
            )

        final_trainer_args, _ = continual_train.extract_continual_options(prepared_args)
        total_envs, base_port, external_worker_ids = distributed.training_topology(
            final_trainer_args,
            distributed_options,
        )
        original_factory = None
        if distributed_options.enabled:
            spec_path = distributed.write_remote_worker_spec(
                distributed_options.remote_spec,
                final_trainer_args,
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
            "[Bees continual] automatic public learning "
            f"selected={len(initial.get('selected_batches', []))} "
            f"scenarios={len(scenario_ids)} "
            f"selection_record={selection_record or 'none'}"
        )

        stop = threading.Event()
        watcher = threading.Thread(
            target=_watch_public_learning,
            kwargs={
                "stop": stop,
                "root": continual_options.root,
                "config_path": continual_options.config,
                "options": auto_options,
            },
            name="bees-public-learning-watch",
            daemon=True,
        )
        watcher.start()
        try:
            return continual_train.main(prepared_args)
        finally:
            stop.set()
            watcher.join(timeout=max(1.0, min(5.0, auto_options.watch_seconds)))
            distributed.restore_external_worker_factory(original_factory)
    except (ContinualLearningError, OSError, ValueError) as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
