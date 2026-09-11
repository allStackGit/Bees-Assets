"""Launch continual Bees training with explicit player-derived adversarial scenarios.

The selected immutable scenarios are resolved from the continual store and encoded as bounded Unity
environment arguments. The normal continual wrapper still owns PPO, historical opponents, candidate
registration and optional behavioral cloning. This layer records/injects selected player-derived
matchup pressure, optional tactical geometry, and any explicitly registered scripted replay catalog.
Old player trajectories are never reused as PPO data: replay attachments control only the scripted
opponent side while the other side generates fresh on-policy experience.
"""

from __future__ import annotations

import json
import os
import sys
from pathlib import Path
from typing import List, Optional, Sequence, Tuple

import bees_continual_train as continual_train
from bees_continual_adversarial import (
    encode_geometry_catalog_for_unity,
    encode_scenarios_for_unity,
)
from bees_continual_adversarial_replay import build_replay_catalog
from bees_continual_learning import (
    ContinualLearningError,
    ContinualLearningStore,
    ValidationError,
    canonical_json,
    load_config,
    sha256_bytes,
    utc_now,
)


ADVERSARIAL_SCENARIOS_FLAG = "--continual-adversarial-scenarios"
UNITY_PRESSURE_FLAG = "--bees-adversarial-matchups"
UNITY_GEOMETRY_CATALOG_FLAG = "--bees-adversarial-geometry-catalog"
UNITY_FIXED_GEOMETRY_FLAG = "--bees-rl-fixed-geometry"
UNITY_REPLAY_CATALOG_FLAG = "--bees-adversarial-replay-catalog"
RUN_SELECTION_SCHEMA_VERSION = 1


def extract_adversarial_scenarios(
    argv: Sequence[str],
) -> Tuple[List[str], Tuple[str, ...]]:
    cleaned: List[str] = []
    scenario_ids: Optional[Tuple[str, ...]] = None
    index = 0
    while index < len(argv):
        argument = argv[index]
        value = None
        if argument == ADVERSARIAL_SCENARIOS_FLAG:
            if index + 1 >= len(argv) or argv[index + 1].startswith("--"):
                raise SystemExit(f"{ADVERSARIAL_SCENARIOS_FLAG} requires a comma-separated value.")
            value = argv[index + 1]
            index += 2
        else:
            prefix = ADVERSARIAL_SCENARIOS_FLAG + "="
            if argument.startswith(prefix):
                value = argument[len(prefix):]
                index += 1
            else:
                cleaned.append(argument)
                index += 1
                continue

        if scenario_ids is not None:
            raise SystemExit(f"{ADVERSARIAL_SCENARIOS_FLAG} may be specified only once.")
        values = tuple(token.strip() for token in value.split(",") if token.strip())
        if not values:
            raise SystemExit(f"{ADVERSARIAL_SCENARIOS_FLAG} requires at least one scenario ID.")
        if len(set(values)) != len(values):
            raise SystemExit(f"{ADVERSARIAL_SCENARIOS_FLAG} contains duplicate scenario IDs.")
        scenario_ids = values

    if scenario_ids is None:
        raise SystemExit(
            f"{ADVERSARIAL_SCENARIOS_FLAG} is required when using the adversarial training launcher."
        )
    return cleaned, scenario_ids


def _is_flag(argument: str, flag: str) -> bool:
    return argument == flag or argument.startswith(flag + "=")


def inject_unity_pressure_arg(
    argv: Sequence[str],
    encoded: str,
    geometry_catalog: str = "",
    replay_catalog_path: str = "",
) -> List[str]:
    if not encoded:
        raise SystemExit("Encoded player-derived adversarial pressure must not be empty.")
    pressure_argument = UNITY_PRESSURE_FLAG + "=" + encoded
    geometry_argument = (
        UNITY_GEOMETRY_CATALOG_FLAG + "=" + geometry_catalog
        if geometry_catalog
        else None
    )
    replay_argument = (
        UNITY_REPLAY_CATALOG_FLAG + "=" + replay_catalog_path
        if replay_catalog_path
        else None
    )
    for argument in argv:
        if _is_flag(argument, UNITY_PRESSURE_FLAG):
            raise SystemExit(
                f"Do not pass {UNITY_PRESSURE_FLAG} manually when using "
                f"{ADVERSARIAL_SCENARIOS_FLAG}; the immutable scenario registry is authoritative."
            )
        if _is_flag(argument, UNITY_GEOMETRY_CATALOG_FLAG) or _is_flag(
            argument, UNITY_FIXED_GEOMETRY_FLAG
        ):
            raise SystemExit(
                "Do not pass player-derived tactical geometry manually when using "
                f"{ADVERSARIAL_SCENARIOS_FLAG}; the immutable scenario registry is authoritative."
            )
        if _is_flag(argument, UNITY_REPLAY_CATALOG_FLAG):
            raise SystemExit(
                "Do not pass player-derived action replay manually when using "
                f"{ADVERSARIAL_SCENARIOS_FLAG}; registered immutable replay attachments are authoritative."
            )
        if argument.startswith("--env-args="):
            raise SystemExit(
                "Use ML-Agents --env-args as a separate token before Unity arguments when using "
                "player-derived adversarial training."
            )

    result = list(argv)
    if "--env-args" not in result:
        result.append("--env-args")
    result.append(pressure_argument)
    if geometry_argument is not None:
        result.append(geometry_argument)
    if replay_argument is not None:
        result.append(replay_argument)
    return result


def _run_selection_path(store: ContinualLearningStore, run_id: str) -> Path:
    run_hash = sha256_bytes(run_id.encode("utf-8"))[:24]
    return store.root / "metadata" / "adversarial-training-runs" / f"run-{run_hash}.json"


def _validate_existing_run_selection(path: Path, identity_hash: str, run_id: str) -> Path:
    try:
        existing = json.loads(path.read_text(encoding="utf-8"))
    except json.JSONDecodeError as exc:
        raise ValidationError(f"Adversarial run-selection record is invalid JSON: {path}: {exc}") from exc
    if not isinstance(existing, dict) or existing.get("identity_sha256") != identity_hash:
        raise ValidationError(
            f"Training run {run_id!r} already has a different immutable adversarial scenario selection. "
            "Use a new --run-id when changing player-derived training pressure."
        )
    return path


def record_run_selection(
    store: ContinualLearningStore,
    *,
    run_id: str,
    scenario_ids: Sequence[str],
    encoded: str,
    geometry_catalog: str = "",
    replay_catalog_sha256: str = "",
) -> Path:
    store._require_initialized()
    if not isinstance(run_id, str) or not run_id.strip():
        raise ValidationError("Adversarial training run_id must be a non-empty string.")
    identity = {
        "schema_version": RUN_SELECTION_SCHEMA_VERSION,
        "run_id": run_id,
        "scenario_ids": sorted(scenario_ids),
        "unity_pressure_argument": UNITY_PRESSURE_FLAG + "=" + encoded,
    }
    # Keep old/no-geometry/no-replay runs byte-for-byte compatible with the original selection
    # identity. Optional replay identity is content-addressed rather than path-addressed so moving a
    # continual-learning store does not alter lineage.
    if geometry_catalog:
        identity["unity_geometry_argument"] = (
            UNITY_GEOMETRY_CATALOG_FLAG + "=" + geometry_catalog
        )
    if replay_catalog_sha256:
        identity["replay_catalog_sha256"] = replay_catalog_sha256
    identity_hash = sha256_bytes(canonical_json(identity).encode("utf-8"))
    path = _run_selection_path(store, run_id)
    if path.exists():
        return _validate_existing_run_selection(path, identity_hash, run_id)

    body = {
        **identity,
        "identity_sha256": identity_hash,
        "created_at": utc_now(),
    }
    payload = (json.dumps(body, indent=2, sort_keys=True, ensure_ascii=False) + "\n").encode("utf-8")
    path.parent.mkdir(parents=True, exist_ok=True)
    try:
        fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    except FileExistsError:
        return _validate_existing_run_selection(path, identity_hash, run_id)

    try:
        with os.fdopen(fd, "wb") as handle:
            handle.write(payload)
            handle.flush()
            os.fsync(handle.fileno())
    except Exception:
        try:
            path.unlink()
        except FileNotFoundError:
            pass
        raise
    return path


def prepare_adversarial_training_args(
    argv: Sequence[str],
) -> Tuple[List[str], Path, Tuple[str, ...]]:
    cleaned, scenario_ids = extract_adversarial_scenarios(argv)
    trainer_args, options = continual_train.extract_continual_options(cleaned)
    if not options.enabled:
        raise SystemExit(
            f"{continual_train.CONTINUAL_ROOT_FLAG} is required for player-derived adversarial training."
        )

    config = load_config(options.config) if options.config else load_config()
    store = ContinualLearningStore(options.root, config)
    store.initialize()
    encoded = encode_scenarios_for_unity(store, scenario_ids)
    geometry_catalog = encode_geometry_catalog_for_unity(store, scenario_ids)
    replay_catalog = build_replay_catalog(store, scenario_ids)
    replay_catalog_path = (
        str(Path(replay_catalog["catalog_path"]).resolve())
        if int(replay_catalog["entry_count"]) > 0
        else ""
    )
    replay_catalog_sha256 = (
        str(replay_catalog["catalog_sha256"])
        if replay_catalog_path
        else ""
    )
    _, run_id, _ = continual_train.infer_run_context(trainer_args)
    record = record_run_selection(
        store,
        run_id=run_id,
        scenario_ids=scenario_ids,
        encoded=encoded,
        geometry_catalog=geometry_catalog,
        replay_catalog_sha256=replay_catalog_sha256,
    )
    return (
        inject_unity_pressure_arg(
            cleaned,
            encoded,
            geometry_catalog,
            replay_catalog_path,
        ),
        record,
        scenario_ids,
    )


def main(argv: Optional[Sequence[str]] = None) -> int:
    raw_args = list(sys.argv[1:] if argv is None else argv)
    try:
        prepared, record, scenario_ids = prepare_adversarial_training_args(raw_args)
    except ContinualLearningError as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 2

    print(
        f"[Bees continual] player-derived adversarial scenarios={len(scenario_ids)} "
        f"selection_record={record}"
    )
    return continual_train.main(prepared)


if __name__ == "__main__":
    raise SystemExit(main())
