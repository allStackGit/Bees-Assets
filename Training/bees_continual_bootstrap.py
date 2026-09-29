"""Generation-zero champion bootstrap for a Bees continual-learning compatibility generation.

This administrative operation establishes the first trusted champion for the store's current
policy/reward/scenario compatibility contract. Older incompatible generations may remain in the
registry for audit/history, but they cannot become parents, historical opponents, rollback targets,
or block a new generation-zero bootstrap. Every later champion transition within one compatibility
generation must use the normal evaluator and promotion gate.
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path
from typing import Any, Dict, Optional, Sequence

from bees_continual_learning import (
    ContinualLearningError,
    ContinualLearningStore,
    PromotionError,
    STATE_CHAMPION,
    STATE_PREVIOUS_CHAMPION,
    ValidationError,
    canonical_json,
    load_config,
    utc_now,
)


def bootstrap_champion(
    store: ContinualLearningStore,
    candidate_model_id: str,
    *,
    reason: str,
) -> Dict[str, Any]:
    """Promote the first trusted candidate for the store's current compatibility generation."""
    store._require_initialized()
    model_id = str(candidate_model_id).strip()
    bootstrap_reason = str(reason).strip()
    if not model_id:
        raise ValidationError("candidate_model_id is required for champion bootstrap.")
    if not bootstrap_reason:
        raise ValidationError("A non-empty bootstrap reason is required.")

    cleanup = []
    with store._connect() as db:
        db.execute("BEGIN IMMEDIATE")
        current = store._state(db, STATE_CHAMPION)
        compatibility = store.compatibility
        prior_generation = db.execute(
            """
            SELECT model_id, status
            FROM models
            WHERE status IN ('champion', 'historical')
              AND behavior_name = ?
              AND policy_abi_version = ?
              AND observation_schema_version = ?
              AND action_schema_version = ?
              AND reward_schema_version = ?
              AND scenario_schema_version = ?
            ORDER BY created_at ASC, model_id ASC
            LIMIT 1
            """,
            (
                compatibility.behavior_name,
                compatibility.policy_abi_version,
                compatibility.observation_schema_version,
                compatibility.action_schema_version,
                compatibility.reward_schema_version,
                compatibility.scenario_schema_version,
            ),
        ).fetchone()
        if prior_generation is not None:
            raise PromotionError(
                "Champion bootstrap is permitted only before champion history exists for the "
                "current compatibility generation."
            )

        candidate = store._model_row(db, model_id)
        store._assert_model_compatible(candidate)
        if candidate["status"] != "candidate":
            raise PromotionError(
                f"Bootstrap model {model_id} has status {candidate['status']}; "
                "only a registered candidate may become the generation-zero champion."
            )

        try:
            metadata = json.loads(candidate["metadata_json"])
        except (TypeError, json.JSONDecodeError) as exc:
            raise PromotionError("Bootstrap candidate metadata is corrupted.") from exc
        if not isinstance(metadata, dict):
            raise PromotionError("Bootstrap candidate metadata must be an object.")
        if "champion_bootstrap" in metadata:
            raise PromotionError("Bootstrap candidate already contains champion bootstrap metadata.")

        retired_incompatible_champion = None
        if current is not None:
            old = store._model_row(db, current)
            expected = store.compatibility.to_dict()
            actual = {key: old[key] for key in expected}
            if actual == expected:
                raise PromotionError(
                    "A compatible current champion already exists; generation-zero bootstrap is not allowed."
                )
            if old["status"] != "champion":
                raise PromotionError(
                    f"Current champion state is inconsistent: {current} has status {old['status']}."
                )
            old_source = Path(old["artifact_path"])
            old_target = Path(store._stage_artifact_for_status(old, "retired"))
            cleanup.append((old_source, old_target, old["artifact_sha256"]))
            db.execute(
                "UPDATE models SET status='retired', artifact_path=? WHERE model_id=?",
                (str(old_target), current),
            )
            retired_incompatible_champion = current

        source = Path(candidate["artifact_path"])
        target = Path(store._stage_artifact_for_status(candidate, "champion"))
        cleanup.append((source, target, candidate["artifact_sha256"]))
        bootstrap_metadata = {
            "created_at": utc_now(),
            "reason": bootstrap_reason,
            "source_status": candidate["status"],
        }
        if retired_incompatible_champion is not None:
            bootstrap_metadata["replaced_incompatible_champion_model_id"] = (
                retired_incompatible_champion
            )
        metadata["champion_bootstrap"] = bootstrap_metadata
        db.execute(
            """
            UPDATE models
            SET status='champion', artifact_path=?, metadata_json=?
            WHERE model_id=?
            """,
            (str(target), canonical_json(metadata), model_id),
        )
        store._set_state(db, STATE_CHAMPION, model_id)
        # Rollback cannot cross a compatibility boundary. Historical incompatible models remain
        # auditable in the registry but are not the previous champion for this generation.
        store._set_state(db, STATE_PREVIOUS_CHAMPION, None)
        result = store._row_dict(store._model_row(db, model_id))

    for source, target, expected_sha256 in cleanup:
        store._cleanup_staged_source(source, target, expected_sha256)
    return result


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description=(
            "Establish the generation-zero champion for the current Bees continual-learning "
            "compatibility generation. Later compatible champions must use normal evaluation and promote()."
        )
    )
    parser.add_argument("--store", required=True, help="Initialized continual-learning store root.")
    parser.add_argument("--candidate", required=True, help="Registered candidate model id.")
    parser.add_argument(
        "--reason",
        required=True,
        help="Operator-supplied audit reason explaining why this model is trusted as generation zero.",
    )
    parser.add_argument("--config", help="Optional continual-learning config JSON.")
    return parser


def main(argv: Optional[Sequence[str]] = None) -> int:
    args = build_parser().parse_args(argv)
    try:
        config = load_config(args.config) if args.config else load_config()
        store = ContinualLearningStore(args.store, config=config)
        result = bootstrap_champion(store, args.candidate, reason=args.reason)
        print(json.dumps(result, indent=2, sort_keys=True))
        return 0
    except (ContinualLearningError, ValueError, OSError) as exc:
        print(f"Champion bootstrap failed: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
