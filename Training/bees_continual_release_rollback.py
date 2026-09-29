"""Roll back the approved Bees RL champion and reconcile player-facing release pointers.

The registry remains authoritative. A rollback first moves the requested historical model back to
champion status (or, when an explicit target is already current, treats the invocation as a retry),
then republishes the immutable deployment package, reactivates any requested retained platform
bundles, and independently verifies the resulting release chain. No automatic quality decision is
made here; this is the guarded operational rollback primitive used after a human or future monitor
has decided that rollback is warranted.
"""

from __future__ import annotations

import argparse
import json
import os
import sys
from pathlib import Path
from typing import Any, Callable, Dict, Mapping, Optional, Sequence

from bees_continual_deployment import publish_current_champion
from bees_continual_hot_bundle import SUPPORTED_PLATFORMS
from bees_continual_hot_bundle_reactivate import reactivate_current_hot_bundle
from bees_continual_learning import (
    ContinualLearningError,
    ContinualLearningStore,
    ValidationError,
    load_config,
)
from bees_continual_release_health import check_release_health


class ReleaseRollbackError(ContinualLearningError):
    """Raised when registry rollback or release reconciliation cannot finish safely."""


Publisher = Callable[[ContinualLearningStore], Mapping[str, Any]]
BundleReactivator = Callable[[ContinualLearningStore, str | os.PathLike[str], str], Mapping[str, Any]]
HealthChecker = Callable[..., Mapping[str, Any]]


def _normalized_platforms(platforms: Sequence[str]) -> tuple[str, ...]:
    result = []
    seen = set()
    for platform in platforms:
        if platform not in SUPPORTED_PLATFORMS:
            raise ValidationError(f"Unsupported hot-bundle platform: {platform!r}.")
        if platform not in seen:
            seen.add(platform)
            result.append(platform)
    return tuple(result)


def rollback_release(
    store: ContinualLearningStore,
    *,
    target_model_id: Optional[str] = None,
    distribution_root: Optional[str | os.PathLike[str]] = None,
    platforms: Sequence[str] = (),
    publisher: Publisher = publish_current_champion,
    bundle_reactivator: BundleReactivator = reactivate_current_hot_bundle,
    health_checker: HealthChecker = check_release_health,
) -> Dict[str, Any]:
    """Rollback/reconcile one champion and verify all requested release pointers."""
    selected_platforms = _normalized_platforms(platforms)
    if selected_platforms and distribution_root is None:
        raise ValidationError(
            "distribution_root is required when rollback should reactivate desktop hot bundles."
        )
    if target_model_id is not None:
        if not isinstance(target_model_id, str) or not target_model_id.strip():
            raise ValidationError("target_model_id must be a non-empty string when supplied.")
        target_model_id = target_model_id.strip()

    store.initialize()
    starting_champion_id = store.current_champion_id()
    if not starting_champion_id:
        raise ReleaseRollbackError("Cannot roll back because no current champion is established.")

    registry_changed = target_model_id != starting_champion_id
    if target_model_id == starting_champion_id:
        # Explicit-target retry after a partial release failure. Do not flip STATE_PREVIOUS_CHAMPION
        # again; simply converge deployment and client pointers on the already-selected champion.
        rolled_back = store.get_model(starting_champion_id)
    else:
        rolled_back = store.rollback(target_model_id)
    rollback_target_id = str(rolled_back["model_id"])

    root = None if distribution_root is None else Path(distribution_root).expanduser().resolve()
    try:
        deployment = dict(publisher(store))
        if deployment.get("model_id") != rollback_target_id:
            raise ReleaseRollbackError(
                "Deployment publisher did not reconcile to rollback target "
                f"{rollback_target_id}; got {deployment.get('model_id')!r}."
            )

        bundles = []
        if root is not None:
            for platform in selected_platforms:
                result = dict(bundle_reactivator(store, root, platform))
                if result.get("model_id") != rollback_target_id or result.get("platform") != platform:
                    raise ReleaseRollbackError(
                        f"Hot-bundle reactivation returned inconsistent identity for {platform}."
                    )
                bundles.append(result)

        health = dict(
            health_checker(
                store,
                distribution_root=root,
                platforms=selected_platforms,
            )
        )
        if health.get("status") != "healthy":
            raise ReleaseRollbackError(
                f"Release health checker returned non-healthy status {health.get('status')!r}."
            )
        if health.get("current_champion_model_id") != rollback_target_id:
            raise ReleaseRollbackError(
                "Release health checker champion does not match rollback target: "
                f"expected {rollback_target_id}, got {health.get('current_champion_model_id')!r}."
            )
        if health.get("deployment_id") != deployment.get("deployment_id"):
            raise ReleaseRollbackError(
                "Release health checker deployment does not match rollback publication."
            )
    except (ContinualLearningError, OSError, ValueError) as exc:
        # Never automatically reverse a deliberate registry rollback merely because publication is
        # unavailable. That could re-enable the model the operator was trying to withdraw. The target
        # remains authoritative; an explicit-target retry is idempotent and converges release pointers.
        raise ReleaseRollbackError(
            f"Registry champion is {rollback_target_id}, but release rollback reconciliation failed. "
            f"Rerun with --target {rollback_target_id} to retry without changing champion state. "
            f"Release error: {exc}"
        ) from exc

    return {
        "status": "healthy",
        "starting_champion_model_id": starting_champion_id,
        "current_champion_model_id": rollback_target_id,
        "registry_changed": registry_changed,
        "deployment_id": deployment.get("deployment_id"),
        "deployment_pointer_changed": bool(deployment.get("pointer_changed", False)),
        "platforms": list(selected_platforms),
        "bundle_pointer_changes": {
            str(item["platform"]): bool(item.get("pointer_changed", False)) for item in bundles
        },
    }


def _build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Roll back the Bees RL champion and reconcile verified release pointers."
    )
    parser.add_argument("--root", required=True, help="Initialized continual-learning store root.")
    parser.add_argument(
        "--target",
        help=(
            "Historical model ID to restore. Omit for the registry's recorded previous champion. "
            "After a partial failure, retry with the explicit target printed by this command."
        ),
    )
    parser.add_argument(
        "--distribution-root",
        help="BeesServer RL model distribution root containing retained platform bundles.",
    )
    parser.add_argument(
        "--platform",
        action="append",
        default=[],
        choices=sorted(SUPPORTED_PLATFORMS),
        help="Reactivate this retained desktop bundle; repeat for multiple platforms.",
    )
    parser.add_argument(
        "--config",
        default=str(Path(__file__).with_name("continual_learning_config.json")),
        help="Continual-learning configuration JSON.",
    )
    return parser


def main(argv: Optional[Sequence[str]] = None) -> int:
    args = _build_parser().parse_args(argv)
    try:
        store = ContinualLearningStore(args.root, load_config(args.config))
        result = rollback_release(
            store,
            target_model_id=args.target,
            distribution_root=args.distribution_root,
            platforms=args.platform,
        )
        print(json.dumps(result, indent=2, sort_keys=True, ensure_ascii=False))
        return 0
    except (ContinualLearningError, OSError, ValueError) as exc:
        print(f"Champion release rollback failed: {exc}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
