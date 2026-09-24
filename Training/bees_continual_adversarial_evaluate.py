"""Paired diagnostic evaluation for player-derived adversarial scenarios.

This evaluator is intentionally separate from the permanent promotion competency suite. It runs a
candidate and a baseline through the same immutable player-derived fleet/geometry scenario and, when
registered, the same deterministic Human action replay. Replay cases load the candidate (or baseline)
into both physical self-play team slots while Unity reports the non-scripted policy side as logical
team 0, so team-ID alternation cannot attribute scripted outcomes to the evaluated policy.
"""

from __future__ import annotations

import argparse
import json
import math
import os
import sys
from pathlib import Path
from typing import Callable, Mapping, Optional, Sequence

from bees_continual_adversarial import (
    _parse_composition,
    _parse_tactical_geometry,
    _read_scenario,
    _validate_registered_sources,
)
from bees_continual_adversarial_replay import (
    build_replay_catalog,
    read_registered_replay_if_present,
)
from bees_continual_evaluate import (
    DEFAULT_MAX_ENVIRONMENT_STEPS_PER_MATCH,
    MatchSummary,
    _merge_env_args,
    _model_path,
    _safe_model,
    _validate_match_summary,
    run_match_group,
)
from bees_continual_learning import (
    ContinualLearningError,
    ContinualLearningStore,
    ValidationError,
    canonical_json,
    load_config,
    sha256_bytes,
    utc_now,
)
from bees_continual_native_demo import _write_bytes_immutable


ADVERSARIAL_EVALUATION_SCHEMA_VERSION = 1
UNITY_PRESSURE_FLAG = "--bees-adversarial-matchups"
UNITY_GEOMETRY_CATALOG_FLAG = "--bees-adversarial-geometry-catalog"
UNITY_FIXED_GEOMETRY_FLAG = "--bees-rl-fixed-geometry"
UNITY_REPLAY_CATALOG_FLAG = "--bees-adversarial-replay-catalog"
MatchRunner = Callable[..., MatchSummary]


def _positive_integer(value: object, label: str) -> int:
    if not isinstance(value, int) or isinstance(value, bool) or value <= 0:
        raise ValidationError(f"{label} must be a positive integer.")
    return value


def _is_flag(value: str, flag: str) -> bool:
    return value == flag or value.startswith(flag + "=")


def _validate_base_env_args(values: Sequence[str]) -> Sequence[str]:
    result = []
    for value in values:
        if not isinstance(value, str) or not value.strip():
            raise ValidationError("Adversarial evaluation base env args must be non-empty strings.")
        stripped = value.strip()
        if _is_flag(stripped, UNITY_PRESSURE_FLAG):
            raise ValidationError(
                f"Do not pass {UNITY_PRESSURE_FLAG} to diagnostic evaluation; exact fixed "
                "scenario compositions are derived from the immutable registry."
            )
        if _is_flag(stripped, UNITY_GEOMETRY_CATALOG_FLAG) or _is_flag(
            stripped, UNITY_FIXED_GEOMETRY_FLAG
        ):
            raise ValidationError(
                "Do not pass player-derived tactical geometry manually to diagnostic evaluation; "
                "geometry is derived from the immutable scenario registry."
            )
        if _is_flag(stripped, UNITY_REPLAY_CATALOG_FLAG):
            raise ValidationError(
                "Do not pass player-derived action replay manually to diagnostic evaluation; "
                "replay artifacts are derived from immutable registered attachments."
            )
        result.append(stripped)
    return tuple(result)


def _scenario_env_args(identity: Mapping[str, object]) -> Sequence[str]:
    bees = _parse_composition(identity.get("bee_composition"), "bee_composition")
    humans = _parse_composition(identity.get("human_composition"), "human_composition")
    if len(bees) != len(humans):
        raise ValidationError("Adversarial evaluation scenario has asymmetric team sizes.")

    result = [
        "--rl-matchup-mode=fixed",
        f"--rl-ships-per-side={len(bees)}",
        "--rl-bee-ship-types=" + ",".join(bees),
        "--rl-human-ship-types=" + ",".join(humans),
    ]
    geometry = _parse_tactical_geometry(identity.get("geometry"))
    if geometry is not None:
        map_size = float(geometry["map_size"])
        separation = float(geometry["spawn_separation_ratio"])
        # The standard map-size option keeps all other training metadata/fallbacks consistent; the
        # fixed geometry flag additionally reproduces the non-default center-to-center separation.
        result.append(f"--rl-map-size={map_size:g}")
        result.append(f"{UNITY_FIXED_GEOMETRY_FLAG}={map_size:g},{separation:g}")
    return tuple(result)


def _validated_summary(summary: MatchSummary, matches: int) -> MatchSummary:
    return _validate_match_summary(summary, expected_matches=matches)


def _write_report(store: ContinualLearningStore, report: Mapping[str, object]) -> Mapping[str, object]:
    report_hash = sha256_bytes(canonical_json(report).encode("utf-8"))
    report_id = f"adv-eval-{report_hash[:24]}"
    path = store.evaluation_dir / "adversarial" / f"{report_id}.json"
    body = {
        **report,
        "report_id": report_id,
        "report_sha256": report_hash,
    }
    payload = (json.dumps(body, indent=2, sort_keys=True, ensure_ascii=False) + "\n").encode("utf-8")
    _write_bytes_immutable(path, payload)
    return {**body, "report_path": str(path)}


def _replay_evaluation_setup(
    store: ContinualLearningStore,
    scenario_id: str,
) -> tuple[Optional[Mapping[str, object]], Sequence[str]]:
    registration = read_registered_replay_if_present(store, scenario_id)
    if registration is None:
        return None, ()

    catalog = build_replay_catalog(store, [scenario_id])
    if int(catalog["entry_count"]) != 1:
        raise ValidationError(
            f"Adversarial replay registration for {scenario_id} did not compile to exactly one catalog entry."
        )
    entry = catalog["catalog"]["entries"][0]
    metadata = {
        "replay_id": entry["replayId"],
        "side": entry["side"],
        "replay_sha256": entry["replaySha256"],
        "frame_count": entry["frameCount"],
        "fixed_step_interval": entry["fixedStepInterval"],
        "catalog_sha256": catalog["catalog_sha256"],
    }
    return metadata, (
        UNITY_REPLAY_CATALOG_FLAG + "=" + str(Path(catalog["catalog_path"]).resolve()),
    )


def evaluate_adversarial_scenarios(
    store: ContinualLearningStore,
    *,
    candidate_model_id: str,
    baseline_model_id: str,
    opponent_model_id: str,
    environment_path: os.PathLike[str] | str,
    scenario_ids: Sequence[str],
    matches_per_scenario: int,
    base_env_args: Sequence[str] = (),
    seed: int = 0,
    worker_id: int = 0,
    timeout_wait: int = 300,
    onnx_provider: Optional[str] = None,
    no_graphics: bool = True,
    max_environment_steps_per_match: int = DEFAULT_MAX_ENVIRONMENT_STEPS_PER_MATCH,
    match_runner: MatchRunner = run_match_group,
) -> Mapping[str, object]:
    """Evaluate candidate-vs-baseline deltas on selected immutable adversarial scenarios."""
    store.initialize()
    matches = _positive_integer(matches_per_scenario, "matches_per_scenario")
    base_args = _validate_base_env_args(base_env_args)
    if not scenario_ids:
        raise ValidationError("At least one adversarial scenario ID is required for evaluation.")
    normalized_ids = sorted(set(scenario_ids))
    if len(normalized_ids) != len(scenario_ids):
        raise ValidationError("Adversarial evaluation contains duplicate scenario IDs.")
    if not isinstance(seed, int) or isinstance(seed, bool):
        raise ValidationError("seed must be an integer.")
    if not isinstance(worker_id, int) or isinstance(worker_id, bool) or worker_id < 0:
        raise ValidationError("worker_id must be a non-negative integer.")

    candidate = _safe_model(store, candidate_model_id)
    baseline = _safe_model(store, baseline_model_id)
    opponent = _safe_model(store, opponent_model_id)
    candidate_path = _model_path(store, candidate_model_id)
    baseline_path = _model_path(store, baseline_model_id)
    opponent_path = _model_path(store, opponent_model_id)
    behavior_name = store.compatibility.behavior_name

    results = []
    weighted_candidate = 0.0
    weighted_baseline = 0.0
    selected_weight = 0.0

    for index, scenario_id in enumerate(normalized_ids):
        scenario = _read_scenario(store, scenario_id)
        identity = scenario["identity"]
        _validate_registered_sources(store, scenario_id, identity)
        replay_metadata, replay_args = _replay_evaluation_setup(store, scenario_id)
        scenario_args = tuple(_scenario_env_args(identity)) + tuple(replay_args)
        merged_args = _merge_env_args(base_args, scenario_args)
        paired_seed = seed + index

        # With a scripted side, physical ML-Agents team IDs still alternate normally. Load the same
        # evaluated model into both team slots; the replay side never requests model actions, and the
        # evaluator side channel reports the live policy side as logical team 0. Non-replay cases keep
        # the historical candidate-vs-opponent contract unchanged.
        candidate_opponent_path = candidate_path if replay_metadata is not None else opponent_path
        baseline_opponent_path = baseline_path if replay_metadata is not None else opponent_path

        candidate_summary = _validated_summary(
            match_runner(
                environment_path=environment_path,
                candidate_model_path=candidate_path,
                opponent_model_path=candidate_opponent_path,
                matches=matches,
                behavior_name=behavior_name,
                env_args=merged_args,
                seed=paired_seed,
                worker_id=worker_id + index * 2,
                timeout_wait=timeout_wait,
                onnx_provider=onnx_provider,
                no_graphics=no_graphics,
                max_environment_steps_per_match=max_environment_steps_per_match,
            ),
            matches,
        )
        baseline_summary = _validated_summary(
            match_runner(
                environment_path=environment_path,
                candidate_model_path=baseline_path,
                opponent_model_path=baseline_opponent_path,
                matches=matches,
                behavior_name=behavior_name,
                env_args=merged_args,
                seed=paired_seed,
                worker_id=worker_id + index * 2 + 1,
                timeout_wait=timeout_wait,
                onnx_provider=onnx_provider,
                no_graphics=no_graphics,
                max_environment_steps_per_match=max_environment_steps_per_match,
            ),
            matches,
        )

        target_fraction = float(identity["target_fraction"])
        if not math.isfinite(target_fraction) or target_fraction <= 0:
            raise ValidationError(f"Adversarial scenario {scenario_id} has invalid target_fraction.")
        candidate_score = candidate_summary.score_rate
        baseline_score = baseline_summary.score_rate
        delta = candidate_score - baseline_score
        weighted_candidate += candidate_score * target_fraction
        weighted_baseline += baseline_score * target_fraction
        selected_weight += target_fraction
        results.append(
            {
                "scenario_id": scenario_id,
                "target_fraction": target_fraction,
                "geometry": identity.get("geometry"),
                "replay": replay_metadata,
                "model_opponent_used": replay_metadata is None,
                "env_args": list(merged_args),
                "candidate_score_rate": candidate_score,
                "baseline_score_rate": baseline_score,
                "score_rate_delta": delta,
                "improved": delta > 0,
                "candidate_summary": candidate_summary.to_dict(),
                "baseline_summary": baseline_summary.to_dict(),
            }
        )

    if selected_weight <= 0:
        raise ValidationError("Adversarial evaluation selected zero total scenario weight.")
    candidate_weighted_score = weighted_candidate / selected_weight
    baseline_weighted_score = weighted_baseline / selected_weight
    report = {
        "schema_version": ADVERSARIAL_EVALUATION_SCHEMA_VERSION,
        "created_at": utc_now(),
        "candidate_model_id": candidate["model_id"],
        "baseline_model_id": baseline["model_id"],
        "opponent_model_id": opponent["model_id"],
        "behavior_name": behavior_name,
        "policy_abi_version": store.compatibility.policy_abi_version,
        "matches_per_scenario": matches,
        "base_env_args": list(base_args),
        "seed": seed,
        "scenarios": results,
        "aggregate": {
            "pressure_weighted_candidate_score_rate": candidate_weighted_score,
            "pressure_weighted_baseline_score_rate": baseline_weighted_score,
            "pressure_weighted_score_rate_delta": candidate_weighted_score - baseline_weighted_score,
            "improved_scenario_count": sum(1 for item in results if item["improved"]),
            "scenario_count": len(results),
        },
        "promotion_eligible": False,
        "note": (
            "Diagnostic player-derived scenario evaluation only. It reproduces registered fleet "
            "composition, tactical map/separation geometry, and the reviewed movement/aim/fire replay "
            "prefix when attached. Replay cases compare candidate and baseline independently against "
            "the same deterministic script. This evidence does not replace the pinned permanent "
            "competency suite or production promotion gate."
        ),
    }
    return _write_report(store, report)


def _build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Compare a candidate with a baseline on immutable player-derived scenarios."
    )
    parser.add_argument("--root", required=True, help="Continual-learning store root.")
    parser.add_argument("--candidate", required=True)
    parser.add_argument("--baseline", required=True)
    parser.add_argument("--opponent", required=True)
    parser.add_argument("--env", required=True, help="Dedicated RL Unity executable.")
    parser.add_argument("--scenario", action="append", dest="scenarios", required=True)
    parser.add_argument("--matches", type=int, default=100)
    parser.add_argument("--config")
    parser.add_argument("--env-arg", action="append", default=[])
    parser.add_argument("--seed", type=int, default=0)
    parser.add_argument("--worker-id", type=int, default=0)
    parser.add_argument("--timeout-wait", type=int, default=300)
    parser.add_argument("--onnx-provider")
    parser.add_argument("--graphics", action="store_true")
    parser.add_argument(
        "--max-environment-steps-per-match",
        type=int,
        default=DEFAULT_MAX_ENVIRONMENT_STEPS_PER_MATCH,
    )
    return parser


def main(argv: Optional[Sequence[str]] = None) -> int:
    args = _build_parser().parse_args(argv)
    try:
        config = load_config(args.config) if args.config else load_config()
        store = ContinualLearningStore(args.root, config)
        report = evaluate_adversarial_scenarios(
            store,
            candidate_model_id=args.candidate,
            baseline_model_id=args.baseline,
            opponent_model_id=args.opponent,
            environment_path=args.env,
            scenario_ids=args.scenarios,
            matches_per_scenario=args.matches,
            base_env_args=args.env_arg,
            seed=args.seed,
            worker_id=args.worker_id,
            timeout_wait=args.timeout_wait,
            onnx_provider=args.onnx_provider,
            no_graphics=not args.graphics,
            max_environment_steps_per_match=args.max_environment_steps_per_match,
        )
        print(json.dumps(report, indent=2, sort_keys=True, ensure_ascii=False))
        return 0
    except (ContinualLearningError, ValueError, OSError) as exc:
        print(f"Adversarial evaluation failed: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
