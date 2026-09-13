"""Compatibility entry point for authoritative continual-learning candidate evaluation.

Behavior sanity is now part of the canonical ``bees_continual_evaluate`` path. This module is
kept only so existing operator commands that invoke the former checked evaluator continue to
use exactly the same evaluation, recording, league-update, and promotion behavior without a
second implementation drifting from the canonical path.
"""

from __future__ import annotations

import json
import sys
from typing import Any, Dict, Optional, Sequence

from bees_continual_evaluate import (
    _load_env_args_file,
    build_parser,
    evaluate_and_record,
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
    """Delegate to the canonical evaluator, which already applies behavior sanity."""
    return evaluate_and_record(store, **kwargs)


def main(argv: Optional[Sequence[str]] = None) -> int:
    parser = build_parser()
    parser.description = (
        "Evaluate an immutable Bees RL candidate with the canonical authoritative runtime and "
        "behavioral sanity checks before recording promotion evidence."
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
