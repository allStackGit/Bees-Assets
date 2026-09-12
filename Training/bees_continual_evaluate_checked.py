"""Behavior-checked entry point for authoritative continual-learning candidate evaluation.

This wraps the existing evaluator, derives conservative catastrophic-behavior evidence from the
same authoritative match summaries, and only then records promotion evidence.
"""

from __future__ import annotations

import json
import sys
from typing import Any, Dict, Optional, Sequence

from bees_continual_behavior_sanity import apply_behavior_sanity
from bees_continual_evaluate import (
    _load_env_args_file,
    build_parser,
    evaluate_candidate,
)
from bees_continual_learning import (
    ContinualLearningError,
    ContinualLearningStore,
    load_config,
)


def evaluate_and_record_checked(
    store: ContinualLearningStore,
    **kwargs: Any,
) -> Dict[str, Any]:
    """Evaluate, attach behavior evidence, record it, then update league-pressure evidence."""
    report = apply_behavior_sanity(evaluate_candidate(store, **kwargs))
    recorded = store.record_evaluation(report)
    league_updates = []
    for historical in report["historical"]:
        baseline = historical.get("baseline_score_rate")
        if baseline is None:
            continue
        tags = [
            "authoritative_candidate_evaluation",
            f"evaluation:{recorded['report_id']}",
        ]
        update = {
            "current_model_id": report["candidate_model_id"],
            "opponent_model_id": historical["opponent_model_id"],
            "current_win_rate": historical["candidate_score_rate"],
            "previous_win_rate": baseline,
            "match_count": historical["matches"],
            "tags": tags,
        }
        store.record_historical_matchup(**update)
        league_updates.append(update)
    return {"report": report, "recorded": recorded, "league_updates": league_updates}


def main(argv: Optional[Sequence[str]] = None) -> int:
    parser = build_parser()
    parser.description = (
        "Evaluate an immutable Bees RL candidate with authoritative runtime and behavioral "
        "sanity checks before recording promotion evidence."
    )
    args = parser.parse_args(argv)
    try:
        config = load_config(args.config) if args.config else load_config()
        store = ContinualLearningStore(args.store, config=config)
        env_args = _load_env_args_file(args.env_args_file) + list(args.env_arg)
        value = evaluate_and_record_checked(
            store,
            candidate_model_id=args.candidate,
            environment_path=args.env,
            env_args=env_args,
            competency_suite=args.competency_suite,
            champion_matches=args.champion_matches,
            historical_matches=args.historical_matches,
            competency_default_matches=args.competency_default_matches,
            historical_model_ids=args.historical_opponents,
            seed=args.seed,
            worker_id=args.worker_id,
            timeout_wait=args.timeout_wait,
            onnx_provider=args.onnx_provider,
            no_graphics=not args.graphics,
            max_environment_steps_per_match=args.max_environment_steps_per_match,
        )
        recorded = value["recorded"]
        if args.promote_if_qualified and recorded["passed"]:
            value["promoted"] = store.promote(args.candidate, recorded["report_id"])
        elif args.promote_if_qualified:
            value["promoted"] = None
        print(json.dumps(value, indent=2, sort_keys=True))
        return 0 if recorded["passed"] else 2
    except (ContinualLearningError, ValueError, OSError) as exc:
        print(f"Evaluation failed: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
