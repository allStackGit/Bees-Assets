"""Mine repeated tactical patterns from curated public live telemetry.

This is an operator-assist layer between immutable telemetry curation and telemetry-derived
headless pressure registration. It never registers scenarios automatically and never reuses
recorded observations/actions as PPO trajectories. Recorded step data is analyzed in memory only;
the returned report contains compact tactical summaries and provenance identifiers.
"""

from __future__ import annotations

import argparse
import json
import sys
from collections import defaultdict
from typing import Dict, Mapping, Optional, Sequence

from bees_continual_adversarial_mine import (
    MIN_TACTIC_RECORDS,
    analyze_tactical_signature,
)
from bees_continual_learning import (
    ContinualLearningError,
    ContinualLearningStore,
    ValidationError,
    canonical_json,
    load_config,
)
from bees_continual_telemetry_adversarial import _validated_selection
from bees_continual_telemetry_curation import _approved_archive


TELEMETRY_TACTIC_MINING_SCHEMA_VERSION = 1


def _positive_int(value: object, label: str) -> int:
    if not isinstance(value, int) or isinstance(value, bool) or value <= 0:
        raise ValidationError(f"{label} must be a positive integer.")
    return value


def _agent_streams(payload: Mapping[str, object]) -> Mapping[str, Sequence[Mapping[str, object]]]:
    steps = payload.get("steps")
    if not isinstance(steps, list) or not steps:
        raise ValidationError("Curated live telemetry contains no step records to mine.")

    grouped: Dict[str, list[Mapping[str, object]]] = defaultdict(list)
    for index, step in enumerate(steps):
        if not isinstance(step, Mapping):
            raise ValidationError(f"Curated telemetry steps[{index}] must be an object.")
        agent_key = step.get("agent_key")
        decision_index = step.get("decision_index")
        if not isinstance(agent_key, str) or not agent_key.strip():
            raise ValidationError(f"Curated telemetry steps[{index}] has an invalid agent_key.")
        if (
            not isinstance(decision_index, int)
            or isinstance(decision_index, bool)
            or decision_index < 0
        ):
            raise ValidationError(
                f"Curated telemetry steps[{index}] has an invalid decision_index."
            )
        grouped[agent_key].append(step)

    ordered: Dict[str, Sequence[Mapping[str, object]]] = {}
    for agent_key in sorted(grouped):
        stream = sorted(grouped[agent_key], key=lambda step: int(step["decision_index"]))
        decision_indices = [int(step["decision_index"]) for step in stream]
        if len(set(decision_indices)) != len(decision_indices):
            raise ValidationError(
                f"Curated telemetry agent {agent_key!r} contains duplicate decision indices."
            )
        ordered[agent_key] = tuple(stream)
    return ordered


def _analyze_stream(
    stream: Sequence[Mapping[str, object]],
) -> Mapping[str, object]:
    observations = []
    continuous_actions = []
    discrete_actions = []
    for index, step in enumerate(stream):
        observation = step.get("observation")
        continuous = step.get("continuous_action")
        discrete = step.get("discrete_action")
        if not isinstance(observation, list):
            raise ValidationError(f"Telemetry stream record {index} observation must be a list.")
        if not isinstance(continuous, list):
            raise ValidationError(
                f"Telemetry stream record {index} continuous_action must be a list."
            )
        if not isinstance(discrete, list):
            raise ValidationError(
                f"Telemetry stream record {index} discrete_action must be a list."
            )
        observations.append(observation)
        continuous_actions.append(continuous)
        discrete_actions.append(discrete)
    return analyze_tactical_signature(observations, continuous_actions, discrete_actions)


def _geometry_candidates(profiles: Sequence[Mapping[str, object]]) -> Sequence[Mapping[str, object]]:
    unique: Dict[str, Mapping[str, object]] = {}
    for profile in profiles:
        geometry = profile.get("geometry")
        if not isinstance(geometry, Mapping):
            continue
        candidate = geometry.get("registration_candidate")
        if not isinstance(candidate, Mapping):
            continue
        normalized = {
            "map_size": candidate.get("map_size"),
            "spawn_separation_ratio": candidate.get("spawn_separation_ratio"),
        }
        key = canonical_json(normalized)
        unique[key] = normalized
    return tuple(unique[key] for key in sorted(unique))


def mine_telemetry_selection(
    store: ContinualLearningStore,
    selection_id: str,
    *,
    minimum_occurrences: int = 2,
) -> Mapping[str, object]:
    """Mine repeated, review-only tactical signatures from one curated telemetry selection."""
    store._require_initialized()
    minimum_occurrences = _positive_int(minimum_occurrences, "minimum_occurrences")
    selection = _validated_selection(store, selection_id)
    manifest = selection["manifest"]
    batches = manifest.get("batches")
    if not isinstance(batches, list) or not batches:
        raise ValidationError(f"Telemetry selection contains no batches: {selection_id}.")

    grouped: Dict[str, list[Mapping[str, object]]] = defaultdict(list)
    analyzed_streams = 0
    skipped_short_streams = 0
    source_batches = set()

    for batch_entry in batches:
        if not isinstance(batch_entry, Mapping):
            raise ValidationError(f"Telemetry selection has malformed batch entry: {selection_id}.")
        batch_id = batch_entry.get("batch_id")
        if not isinstance(batch_id, str):
            raise ValidationError(f"Telemetry selection has malformed batch identity: {selection_id}.")
        archive = _approved_archive(store, batch_id)
        payload = archive["payload"]
        if not isinstance(payload, Mapping):
            raise ValidationError(f"Telemetry archive payload is malformed: {batch_id}.")
        source_batches.add(batch_id)

        for stream_index, (_agent_key, stream) in enumerate(_agent_streams(payload).items()):
            if len(stream) < MIN_TACTIC_RECORDS:
                skipped_short_streams += 1
                continue
            profile = dict(_analyze_stream(stream))
            profile["_source"] = {
                "batch_id": batch_id,
                "agent_stream_index": stream_index,
            }
            grouped[str(profile["signature"])].append(profile)
            analyzed_streams += 1

    suggestions = []
    for signature in sorted(grouped):
        profiles = grouped[signature]
        occurrence_count = len(
            {str(profile["_source"]["batch_id"]) for profile in profiles}
        )
        if occurrence_count < minimum_occurrences:
            continue
        profiles = sorted(
            profiles,
            key=lambda profile: (
                str(profile["_source"]["batch_id"]),
                int(profile["_source"]["agent_stream_index"]),
            ),
        )
        representative = profiles[0]
        sources = [dict(profile["_source"]) for profile in profiles]
        suggestions.append(
            {
                "signature": signature,
                "occurrence_count": occurrence_count,
                "agent_stream_count": len(profiles),
                "self_ship_type": representative.get("self_ship_type"),
                "self_ship_name": representative.get("self_ship_name"),
                "first_enemy_ship_type": representative.get("first_enemy_ship_type"),
                "first_enemy_ship_name": representative.get("first_enemy_ship_name"),
                "range_style": representative.get("range_style"),
                "movement_style": representative.get("movement_style"),
                "fire_style": representative.get("fire_style"),
                "special_style": representative.get("special_style"),
                "geometry_candidates": list(_geometry_candidates(profiles)),
                "sources": sources,
            }
        )

    suggestions.sort(key=lambda item: (-int(item["occurrence_count"]), str(item["signature"])))
    return {
        "schema_version": TELEMETRY_TACTIC_MINING_SCHEMA_VERSION,
        "selection_id": selection_id,
        "selection_identity_sha256": selection["identity_sha256"],
        "authoritative": False,
        "approved_for_on_policy_rl": False,
        "recorded_actions_used_as_ppo_trajectories": False,
        "requires_operator_review": True,
        "requires_side_mapping": True,
        "minimum_occurrences": minimum_occurrences,
        "analyzed_batches": len(source_batches),
        "analyzed_agent_streams": analyzed_streams,
        "skipped_short_agent_streams": skipped_short_streams,
        "suggestions": suggestions,
        "caveats": [
            "A repeated signature must occur in distinct telemetry batches; multiple agents in one match cannot self-confirm a tactic.",
            "A repeated signature is a review hint, not an automatically approved training scenario.",
            "agent_key is intentionally treated as opaque because the validated telemetry contract does not establish a Bee/Human side mapping; raw agent_key values are not copied into the mining report.",
            "self/enemy ship identities are from the recorded agent perspective and must be oriented by an operator before pressure registration.",
            "Geometry candidates are reconstructed from policy observations and are approximate.",
            "Recorded observations/actions are analyzed only to discover tactics and must never be supplied to PPO as on-policy trajectories.",
        ],
    }


def _parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Mine review-only tactical suggestions from curated public live telemetry."
    )
    parser.add_argument("--root", required=True, help="Continual-learning store root.")
    parser.add_argument("--config", default=None, help="Optional continual-learning config path.")
    parser.add_argument("selection_id", help="Immutable telemetry-selection-<id> to inspect.")
    parser.add_argument(
        "--minimum-occurrences",
        type=int,
        default=2,
        help="Minimum distinct telemetry batches containing a signature before it is suggested.",
    )
    return parser


def main(argv: Optional[Sequence[str]] = None) -> int:
    args = _parser().parse_args(argv)
    try:
        store = ContinualLearningStore(args.root, load_config(args.config))
        report = mine_telemetry_selection(
            store,
            args.selection_id,
            minimum_occurrences=args.minimum_occurrences,
        )
        print(json.dumps(report, indent=2, sort_keys=True, ensure_ascii=False))
        return 0
    except ContinualLearningError as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
