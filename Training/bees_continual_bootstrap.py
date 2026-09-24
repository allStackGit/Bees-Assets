"""One-time generation-zero champion bootstrap for Bees continual learning.

This administrative operation exists only to establish the first trusted champion in an
otherwise unbootstrapped continual-learning registry. Every later champion transition must
use the normal evaluator and promotion gate.
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
    """Promote exactly one trusted generation-zero candidate without fabricating an evaluation."""
    store._require_initialized()
    model_id = str(candidate_model_id).strip()
    bootstrap_reason = str(reason).strip()
    if not model_id:
        raise ValidationError("candidate_model_id is required for champion bootstrap.")
    if not bootstrap_reason:
        raise ValidationError("A non-empty bootstrap reason is required.")

    cleanup = None
    with store._connect() as db:
        db.execute("BEGIN IMMEDIATE")
        current = store._state(db, STATE_CHAMPION)
        previous = store._state(db, STATE_PREVIOUS_CHAMPION)
        prior_generation = db.execute(
            """
            SELECT model_id, status
            FROM models
            WHERE status IN ('champion', 'historical')
            ORDER BY created_at ASC, model_id ASC
            LIMIT 1
            """
        ).fetchone()
        if current is not None or previous is not None or prior_generation is not None:
            raise PromotionError(
                "Champion bootstrap is permitted only before any champion history exists."
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

        source = Path(candidate["artifact_path"])
        target = Path(store._stage_artifact_for_status(candidate, "champion"))
        metadata["champion_bootstrap"] = {
            "created_at": utc_now(),
            "reason": bootstrap_reason,
            "source_status": candidate["status"],
        }
        db.execute(
            """
            UPDATE models
            SET status='champion', artifact_path=?, metadata_json=?
            WHERE model_id=?
            """,
            (str(target), canonical_json(metadata), model_id),
        )
        store._set_state(db, STATE_CHAMPION, model_id)
        store._set_state(db, STATE_PREVIOUS_CHAMPION, None)
        result = store._row_dict(store._model_row(db, model_id))
        cleanup = (source, target, candidate["artifact_sha256"])

    if cleanup is not None:
        store._cleanup_staged_source(*cleanup)
    return result


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description=(
            "Establish the one-time generation-zero champion for an empty Bees continual-learning "
            "registry. Later champions must use normal evaluation and promote()."
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
