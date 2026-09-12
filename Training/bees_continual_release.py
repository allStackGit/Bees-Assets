"""Run one fail-closed automatic candidate evaluation/promotion/deployment cycle.

Training already registers stable ONNX exports as immutable candidates. This module consumes that
registered queue without changing PPO itself: it first reconciles and independently verifies the
current champion's deployment, then evaluates a bounded number of compatible candidates, rejects
candidates with recorded failing evidence, promotes only candidates with recorded passing evidence,
and republishes and re-verifies the newly promoted champion. The default is deliberately one
candidate per invocation so a scheduler cannot silently create a rapid promotion cascade.

Hot AssetBundle construction/publication remains a separate platform-specific step. The hot-bundle
publisher independently refuses bytes that do not match the registry's current published champion.
"""

from __future__ import annotations

import argparse
import json
import os
import sys
from pathlib import Path
from typing import Any, Callable, Dict, List, Mapping, Optional, Sequence

from bees_continual_deployment import publish_current_champion
from bees_continual_evaluate import (
    DEFAULT_MAX_ENVIRONMENT_STEPS_PER_MATCH,
    CompetencyCase,
    evaluate_and_record,
    load_competency_suite,
)
from bees_continual_learning import (
    ContinualLearningError,
    ContinualLearningStore,
    ValidationError,
    load_config,
)
from bees_continual_release_health import check_release_health


class ReleaseError(ContinualLearningError):
    """Raised when the automatic release cycle cannot proceed safely."""


Evaluator = Callable[..., Mapping[str, Any]]
Publisher = Callable[[ContinualLearningStore], Mapping[str, Any]]
HealthChecker = Callable[[ContinualLearningStore], Mapping[str, Any]]


def _compatible_candidates(
    store: ContinualLearningStore,
    candidates: Sequence[Mapping[str, Any]],
) -> List[Dict[str, Any]]:
    """Preserve registry queue order while excluding candidates from an obsolete ABI."""
    expected = store.compatibility.to_dict()
    return [
        dict(model)
        for model in candidates
        if all(model.get(key) == value for key, value in expected.items())
    ]


def _normalized_competency_contract(cases: Sequence[CompetencyCase]) -> Dict[str, Any]:
    normalized = [
        {
            "name": case.name,
            "opponent_model_id": case.opponent_model_id,
            "matches": case.matches,
            "minimum": float(case.minimum),
            "metric": case.metric,
            "critical": case.critical,
            "env_args": list(case.env_args),
        }
        for case in cases
    ]
    normalized.sort(key=lambda item: item["name"])
    return {"schema_version": 1, "cases": normalized}


def _validate_competency_source(
    store: ContinualLearningStore,
    competency_suite: Optional[os.PathLike[str] | str],
    *,
    competency_default_matches: Optional[int],
) -> None:
    promotion = store.config["promotion"]
    minimum_cases = int(promotion.get("min_competency_cases", 1))
    pinned = store.permanent_competency_suite()

    if minimum_cases > 0 and pinned is None:
        raise ReleaseError(
            "Automatic release is blocked because no permanent competency suite is pinned."
        )
    if minimum_cases > 0 and competency_suite is None:
        raise ReleaseError(
            "Automatic release requires --competency-suite so evaluation can reproduce the pinned "
            "permanent competency contract."
        )
    if competency_suite is None:
        return

    default_matches = (
        int(competency_default_matches)
        if competency_default_matches is not None
        else int(promotion["min_historical_matches_per_opponent"])
    )
    if default_matches <= 0:
        raise ReleaseError("competency_default_matches must be positive.")
    actual = _normalized_competency_contract(
        load_competency_suite(competency_suite, default_matches=default_matches)
    )
    if pinned is not None and actual != pinned:
        raise ReleaseError(
            "The supplied competency suite does not exactly match the pinned permanent competency "
            "suite; refusing to evaluate or reject candidates under the wrong gate."
        )


def _validate_recorded_result(candidate_model_id: str, value: Mapping[str, Any]) -> Mapping[str, Any]:
    recorded = value.get("recorded")
    if not isinstance(recorded, Mapping):
        raise ReleaseError("Evaluator did not return a recorded evaluation result.")
    if recorded.get("candidate_model_id") != candidate_model_id:
        raise ReleaseError("Evaluator returned evidence for a different candidate model.")
    report_id = recorded.get("report_id")
    if not isinstance(report_id, str) or not report_id.startswith("eval-"):
        raise ReleaseError("Evaluator returned a malformed evaluation report identity.")
    if not isinstance(recorded.get("passed"), bool):
        raise ReleaseError("Evaluator returned a non-boolean promotion decision.")
    return recorded


def _require_healthy_release(
    store: ContinualLearningStore,
    *,
    expected_model_id: str,
    expected_deployment_id: object,
    health_checker: HealthChecker,
) -> Dict[str, Any]:
    try:
        health = dict(health_checker(store))
    except (ContinualLearningError, OSError, ValueError) as exc:
        raise ReleaseError(f"Release health validation failed: {exc}") from exc
    if health.get("status") != "healthy":
        raise ReleaseError(
            f"Release health checker returned non-healthy status {health.get('status')!r}."
        )
    if health.get("current_champion_model_id") != expected_model_id:
        raise ReleaseError(
            "Release health checker champion does not match the registry champion: "
            f"expected {expected_model_id}, got {health.get('current_champion_model_id')!r}."
        )
    deployment_id = health.get("deployment_id")
    if not isinstance(deployment_id, str) or not deployment_id:
        raise ReleaseError("Release health checker did not return a deployment identity.")
    if isinstance(expected_deployment_id, str) and expected_deployment_id and deployment_id != expected_deployment_id:
        raise ReleaseError(
            "Release health checker deployment does not match the publisher result: "
            f"expected {expected_deployment_id}, got {deployment_id}."
        )
    return health


def run_release_cycle(
    store: ContinualLearningStore,
    *,
    environment_path: os.PathLike[str] | str,
    env_args: Sequence[str] = (),
    competency_suite: Optional[os.PathLike[str] | str] = None,
    champion_matches: Optional[int] = None,
    historical_matches: Optional[int] = None,
    competency_default_matches: Optional[int] = None,
    seed: int = 0,
    worker_id: int = 0,
    timeout_wait: int = 300,
    onnx_provider: Optional[str] = None,
    no_graphics: bool = True,
    max_environment_steps_per_match: int = DEFAULT_MAX_ENVIRONMENT_STEPS_PER_MATCH,
    max_candidates: int = 1,
    evaluator: Evaluator = evaluate_and_record,
    publisher: Publisher = publish_current_champion,
    health_checker: HealthChecker = check_release_health,
) -> Dict[str, Any]:
    """Evaluate/promote a bounded snapshot of the candidate queue and reconcile deployment."""
    if not isinstance(max_candidates, int) or isinstance(max_candidates, bool) or max_candidates <= 0:
        raise ValidationError("max_candidates must be a positive integer.")
    if not isinstance(seed, int) or isinstance(seed, bool):
        raise ValidationError("seed must be an integer.")
    if not isinstance(worker_id, int) or isinstance(worker_id, bool) or worker_id < 0:
        raise ValidationError("worker_id must be a non-negative integer.")
    if not isinstance(timeout_wait, int) or isinstance(timeout_wait, bool) or timeout_wait <= 0:
        raise ValidationError("timeout_wait must be a positive integer.")
    if (
        not isinstance(max_environment_steps_per_match, int)
        or isinstance(max_environment_steps_per_match, bool)
        or max_environment_steps_per_match <= 0
    ):
        raise ValidationError("max_environment_steps_per_match must be a positive integer.")

    store.initialize()
    champion_id = store.current_champion_id()
    if not champion_id:
        raise ReleaseError(
            "Automatic release requires an established champion; bootstrap generation zero first."
        )
    _validate_competency_source(
        store,
        competency_suite,
        competency_default_matches=competency_default_matches,
    )

    # Reconcile registry -> deployment before touching the queue. This makes a rerun recover cleanly
    # from a prior process failure after promotion but before publication, while never distributing a
    # model that has not already become the registry's approved champion. The independent health pass
    # then verifies the complete registry -> pointer -> immutable package chain before evaluation.
    initial_deployment = dict(publisher(store))
    if initial_deployment.get("model_id") != champion_id:
        raise ReleaseError(
            "Deployment publisher did not reconcile to the registry's current champion."
        )
    initial_health = _require_healthy_release(
        store,
        expected_model_id=champion_id,
        expected_deployment_id=initial_deployment.get("deployment_id"),
        health_checker=health_checker,
    )

    all_candidates = store.list_models(status="candidate")
    eligible = _compatible_candidates(store, all_candidates)
    selected = eligible[:max_candidates]
    processed: List[Dict[str, Any]] = []

    for candidate in selected:
        candidate_id = str(candidate["model_id"])
        evaluation = evaluator(
            store,
            candidate_model_id=candidate_id,
            environment_path=environment_path,
            env_args=env_args,
            competency_suite=competency_suite,
            champion_matches=champion_matches,
            historical_matches=historical_matches,
            competency_default_matches=competency_default_matches,
            historical_model_ids=None,
            seed=seed,
            worker_id=worker_id,
            timeout_wait=timeout_wait,
            onnx_provider=onnx_provider,
            no_graphics=no_graphics,
            max_environment_steps_per_match=max_environment_steps_per_match,
        )
        recorded = _validate_recorded_result(candidate_id, evaluation)
        report_id = str(recorded["report_id"])

        if not bool(recorded["passed"]):
            rejected = store.reject(candidate_id, report_id)
            processed.append(
                {
                    "candidate_model_id": candidate_id,
                    "evaluation_report_id": report_id,
                    "decision": "rejected",
                    "reasons": list(recorded.get("reasons", [])),
                    "final_status": rejected["status"],
                }
            )
            continue

        promoted = store.promote(candidate_id, report_id)
        try:
            deployment = dict(publisher(store))
            release_health = _require_healthy_release(
                store,
                expected_model_id=candidate_id,
                expected_deployment_id=deployment.get("deployment_id"),
                health_checker=health_checker,
            )
        except (ContinualLearningError, OSError, ValueError) as exc:
            # Promotion itself is intentionally not rolled back here. At this point the candidate has
            # passed the authoritative gate and is the registry champion, but publication/health has
            # not been confirmed. Keeping that state is safer than using rollback(), which would
            # incorrectly turn the approved candidate into a historical opponent. The next cycle
            # reconciles and verifies publication before evaluating any other candidate.
            raise ReleaseError(
                f"Candidate {candidate_id} was promoted, but deployment publication/health "
                "validation failed. No further candidates were evaluated; rerun the release cycle "
                f"to reconcile the current champion before continuing. Release error: {exc}"
            ) from exc
        if deployment.get("model_id") != candidate_id:
            raise ReleaseError(
                f"Candidate {candidate_id} was promoted, but deployment publisher returned model "
                f"{deployment.get('model_id')!r}."
            )
        processed.append(
            {
                "candidate_model_id": candidate_id,
                "evaluation_report_id": report_id,
                "decision": "promoted",
                "final_status": promoted["status"],
                "deployment_id": deployment.get("deployment_id"),
                "deployment_pointer_changed": bool(deployment.get("pointer_changed", False)),
                "release_health_status": release_health["status"],
            }
        )

    return {
        "status": "processed" if processed else "idle",
        "starting_champion_model_id": champion_id,
        "current_champion_model_id": store.current_champion_id(),
        "initial_deployment_id": initial_deployment.get("deployment_id"),
        "initial_deployment_pointer_changed": bool(initial_deployment.get("pointer_changed", False)),
        "initial_release_health_status": initial_health["status"],
        "candidate_count": len(all_candidates),
        "compatible_candidate_count": len(eligible),
        "skipped_incompatible_candidate_count": len(all_candidates) - len(eligible),
        "processed": processed,
    }


def _load_env_args_file(path: Optional[str]) -> List[str]:
    if not path:
        return []
    source = Path(path)
    try:
        value = json.loads(source.read_text(encoding="utf-8"))
    except FileNotFoundError as exc:
        raise ValidationError(f"Environment args file not found: {source}") from exc
    except json.JSONDecodeError as exc:
        raise ValidationError(f"Invalid environment args JSON {source}: {exc}") from exc
    if not isinstance(value, list) or any(
        not isinstance(item, str) or not item.strip() for item in value
    ):
        raise ValidationError("Environment args file must contain a JSON array of non-empty strings.")
    return list(value)


def _positive_int(value: str) -> int:
    parsed = int(value)
    if parsed <= 0:
        raise argparse.ArgumentTypeError("value must be positive")
    return parsed


def _nonnegative_int(value: str) -> int:
    parsed = int(value)
    if parsed < 0:
        raise argparse.ArgumentTypeError("value must be non-negative")
    return parsed


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description=(
            "Run one bounded automatic Bees RL candidate evaluation, promotion, and deployment cycle."
        )
    )
    parser.add_argument("--root", required=True, help="Initialized continual-learning store root.")
    parser.add_argument("--env", required=True, help="Dedicated RL Unity evaluation executable.")
    parser.add_argument(
        "--config",
        default=str(Path(__file__).with_name("continual_learning_config.json")),
        help="Continual-learning configuration JSON.",
    )
    parser.add_argument(
        "--competency-suite",
        help="Pinned permanent competency-suite JSON used by authoritative evaluation.",
    )
    parser.add_argument("--champion-matches", type=_positive_int)
    parser.add_argument("--historical-matches", type=_positive_int)
    parser.add_argument("--competency-default-matches", type=_positive_int)
    parser.add_argument(
        "--env-arg",
        action="append",
        default=[],
        help="Unity environment argument; repeat for each token.",
    )
    parser.add_argument(
        "--env-args-file",
        help="JSON array of additional Unity environment argument tokens.",
    )
    parser.add_argument("--seed", type=int, default=0)
    parser.add_argument("--worker-id", type=_nonnegative_int, default=0)
    parser.add_argument("--timeout-wait", type=_positive_int, default=300)
    parser.add_argument("--onnx-provider")
    parser.add_argument(
        "--max-environment-steps-per-match",
        type=_positive_int,
        default=DEFAULT_MAX_ENVIRONMENT_STEPS_PER_MATCH,
    )
    parser.add_argument(
        "--max-candidates",
        type=_positive_int,
        default=1,
        help=(
            "Maximum compatible queued candidates to process this invocation. Defaults to 1 to "
            "avoid unintended promotion cascades."
        ),
    )
    parser.add_argument(
        "--graphics",
        action="store_true",
        help="Run evaluation with graphics instead of headless.",
    )
    return parser


def main(argv: Optional[Sequence[str]] = None) -> int:
    args = build_parser().parse_args(argv)
    try:
        config = load_config(args.config)
        store = ContinualLearningStore(args.root, config=config)
        env_args = _load_env_args_file(args.env_args_file) + list(args.env_arg)
        result = run_release_cycle(
            store,
            environment_path=args.env,
            env_args=env_args,
            competency_suite=args.competency_suite,
            champion_matches=args.champion_matches,
            historical_matches=args.historical_matches,
            competency_default_matches=args.competency_default_matches,
            seed=args.seed,
            worker_id=args.worker_id,
            timeout_wait=args.timeout_wait,
            onnx_provider=args.onnx_provider,
            no_graphics=not args.graphics,
            max_environment_steps_per_match=args.max_environment_steps_per_match,
            max_candidates=args.max_candidates,
        )
        print(json.dumps(result, indent=2, sort_keys=True, ensure_ascii=False))
        return 0
    except (ContinualLearningError, OSError, ValueError) as exc:
        print(f"Automatic champion release failed: {exc}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
