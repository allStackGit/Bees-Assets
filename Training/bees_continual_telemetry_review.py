"""Review one mined live-telemetry tactic and register bounded headless training pressure.

The telemetry schema deliberately does not assign a trusted Bee/Human meaning to agent_key. This
operator-review helper therefore requires an explicit side orientation before converting a mined
perspective into a registered pressure scenario. It re-mines the immutable selection on every call,
requires an exact signature match, and delegates registration to the existing telemetry pressure
boundary. No recorded telemetry trajectory becomes PPO experience.
"""

from __future__ import annotations

import argparse
import json
import sys
from typing import Mapping, Optional, Sequence

from bees_continual_learning import (
    ContinualLearningError,
    ContinualLearningStore,
    ValidationError,
    load_config,
)
from bees_continual_telemetry_adversarial import register_telemetry_pressure
from bees_continual_telemetry_mine import mine_telemetry_selection


TELEMETRY_REVIEW_SCHEMA_VERSION = 1
_VALID_SELF_SIDES = frozenset(("bee", "human"))


def _required_text(value: object, label: str, maximum: int) -> str:
    if not isinstance(value, str) or not value.strip() or len(value.strip()) > maximum:
        raise ValidationError(f"{label} must be a non-empty string up to {maximum} characters.")
    return value.strip()


def _find_suggestion(report: Mapping[str, object], signature: str) -> Mapping[str, object]:
    suggestions = report.get("suggestions")
    if not isinstance(suggestions, list):
        raise ValidationError("Telemetry mining report contains no suggestion list.")
    matches = [
        item
        for item in suggestions
        if isinstance(item, Mapping) and item.get("signature") == signature
    ]
    if not matches:
        raise ValidationError(
            "Requested telemetry tactic signature is not currently suggested by the immutable selection."
        )
    if len(matches) != 1:
        raise ValidationError("Telemetry tactic signature is ambiguous within the mining report.")
    return matches[0]


def _ship_name(value: object, label: str) -> str:
    return _required_text(value, label, 128)


def _geometry_candidate(
    suggestion: Mapping[str, object],
    geometry_candidate_index: Optional[int],
) -> tuple[Optional[float], Optional[float]]:
    if geometry_candidate_index is None:
        return None, None
    if (
        not isinstance(geometry_candidate_index, int)
        or isinstance(geometry_candidate_index, bool)
        or geometry_candidate_index < 0
    ):
        raise ValidationError("geometry_candidate_index must be a non-negative integer.")
    candidates = suggestion.get("geometry_candidates")
    if not isinstance(candidates, list) or geometry_candidate_index >= len(candidates):
        raise ValidationError("Requested telemetry geometry candidate does not exist.")
    candidate = candidates[geometry_candidate_index]
    if not isinstance(candidate, Mapping):
        raise ValidationError("Telemetry geometry candidate is malformed.")
    try:
        map_size = float(candidate["map_size"])
        spawn_ratio = float(candidate["spawn_separation_ratio"])
    except (KeyError, TypeError, ValueError) as exc:
        raise ValidationError("Telemetry geometry candidate is malformed.") from exc
    return map_size, spawn_ratio


def review_and_register_telemetry_tactic(
    store: ContinualLearningStore,
    selection_id: str,
    signature: str,
    *,
    self_side: str,
    target_fraction: float,
    rationale: str,
    minimum_occurrences: int = 2,
    geometry_candidate_index: Optional[int] = None,
) -> Mapping[str, object]:
    """Orient one reviewed mined perspective and register immutable fresh-rollout pressure."""
    store._require_initialized()
    signature = _required_text(signature, "signature", 2048)
    self_side = _required_text(self_side, "self_side", 16).lower()
    if self_side not in _VALID_SELF_SIDES:
        raise ValidationError("self_side must be either 'bee' or 'human'.")
    rationale = _required_text(rationale, "rationale", 2048)

    report = mine_telemetry_selection(
        store,
        selection_id,
        minimum_occurrences=minimum_occurrences,
    )
    suggestion = _find_suggestion(report, signature)
    self_ship = _ship_name(suggestion.get("self_ship_name"), "suggestion self ship")
    enemy_ship = _ship_name(
        suggestion.get("first_enemy_ship_name"),
        "suggestion first enemy ship",
    )
    if self_ship.startswith("ship-") or self_ship == "unknown":
        raise ValidationError("Telemetry suggestion self ship cannot be mapped to a gameplay ship name.")
    if enemy_ship.startswith("ship-") or enemy_ship == "unknown":
        raise ValidationError("Telemetry suggestion enemy ship cannot be mapped to a gameplay ship name.")

    if self_side == "bee":
        bee_composition, human_composition = self_ship, enemy_ship
    else:
        bee_composition, human_composition = enemy_ship, self_ship
    map_size, spawn_ratio = _geometry_candidate(suggestion, geometry_candidate_index)

    registered = register_telemetry_pressure(
        store,
        selection_id,
        bee_composition=bee_composition,
        human_composition=human_composition,
        target_fraction=target_fraction,
        rationale=rationale,
        map_size=map_size,
        spawn_separation_ratio=spawn_ratio,
    )
    return {
        "schema_version": TELEMETRY_REVIEW_SCHEMA_VERSION,
        "selection_id": selection_id,
        "signature": signature,
        "minimum_occurrences": minimum_occurrences,
        "occurrence_count": suggestion.get("occurrence_count"),
        "reviewed_self_side": self_side,
        "bee_composition": [bee_composition],
        "human_composition": [human_composition],
        "geometry_candidate_index": geometry_candidate_index,
        "recorded_actions_used_as_ppo_trajectories": False,
        "fresh_on_policy_rollouts_required": True,
        "registration": registered,
    }


def _parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Review one mined live-telemetry tactic and register fresh-rollout pressure."
    )
    parser.add_argument("--root", required=True, help="Continual-learning store root.")
    parser.add_argument("--config", default=None, help="Optional continual-learning config path.")
    parser.add_argument("selection_id")
    parser.add_argument("--signature", required=True)
    parser.add_argument("--self-side", choices=sorted(_VALID_SELF_SIDES), required=True)
    parser.add_argument("--target-fraction", required=True, type=float)
    parser.add_argument("--rationale", required=True)
    parser.add_argument("--minimum-occurrences", type=int, default=2)
    parser.add_argument("--geometry-candidate-index", type=int, default=None)
    return parser


def main(argv: Optional[Sequence[str]] = None) -> int:
    args = _parser().parse_args(argv)
    try:
        store = ContinualLearningStore(args.root, load_config(args.config))
        result = review_and_register_telemetry_tactic(
            store,
            args.selection_id,
            args.signature,
            self_side=args.self_side,
            target_fraction=args.target_fraction,
            rationale=args.rationale,
            minimum_occurrences=args.minimum_occurrences,
            geometry_candidate_index=args.geometry_candidate_index,
        )
        print(json.dumps(result, indent=2, sort_keys=True, ensure_ascii=False))
        return 0
    except ContinualLearningError as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
