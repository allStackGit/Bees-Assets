"""Create privacy-safe semantic fingerprints for public live telemetry.

The fingerprint intentionally ignores raw agent identifiers and exact trajectories. It summarizes
coarse tactical behavior so curation can suppress one contributor repeatedly submitting nearly the
same match while still allowing independent contributors to corroborate a tactic.
"""

from __future__ import annotations

from collections import defaultdict
from typing import Dict, Mapping, Sequence

from bees_continual_adversarial_mine import MIN_TACTIC_RECORDS, analyze_tactical_signature
from bees_continual_learning import ValidationError, canonical_json, sha256_bytes


TELEMETRY_SEMANTIC_FINGERPRINT_SCHEMA_VERSION = 1


def _agent_streams(payload: Mapping[str, object]) -> Sequence[Sequence[Mapping[str, object]]]:
    steps = payload.get("steps")
    if steps is None:
        return ()
    if not isinstance(steps, list):
        raise ValidationError("Telemetry steps must be a list for semantic fingerprinting.")

    grouped: Dict[str, list[Mapping[str, object]]] = defaultdict(list)
    for index, step in enumerate(steps):
        if not isinstance(step, Mapping):
            raise ValidationError(f"Telemetry steps[{index}] must be an object.")
        agent_key = step.get("agent_key")
        decision_index = step.get("decision_index")
        if not isinstance(agent_key, str) or not agent_key.strip():
            raise ValidationError(f"Telemetry steps[{index}] has an invalid agent_key.")
        if (
            not isinstance(decision_index, int)
            or isinstance(decision_index, bool)
            or decision_index < 0
        ):
            raise ValidationError(f"Telemetry steps[{index}] has an invalid decision_index.")
        grouped[agent_key].append(step)

    ordered = []
    for agent_key in sorted(grouped):
        stream = sorted(grouped[agent_key], key=lambda item: int(item["decision_index"]))
        indices = [int(item["decision_index"]) for item in stream]
        if len(indices) != len(set(indices)):
            raise ValidationError(
                f"Telemetry agent {agent_key!r} contains duplicate decision indices."
            )
        ordered.append(tuple(stream))
    return tuple(ordered)


def _profile(stream: Sequence[Mapping[str, object]]) -> Mapping[str, object]:
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


def telemetry_semantic_fingerprint(payload: Mapping[str, object]) -> Mapping[str, object]:
    """Return a deterministic coarse match fingerprint without raw trajectory or identity data."""
    if not isinstance(payload, Mapping):
        raise ValidationError("Telemetry payload must be an object for semantic fingerprinting.")

    signatures = set()
    for stream in _agent_streams(payload):
        if len(stream) < MIN_TACTIC_RECORDS:
            continue
        signatures.add(str(_profile(stream)["signature"]))

    # Result is retained because repeated wins/losses/draws of the same tactic carry meaningfully
    # different evidence. Mode is retained because the same tactic in Campaign and Fish Tank can
    # arise under materially different surrounding game conditions. Exact trajectories, counts,
    # match IDs, model IDs, agent keys, geometry and contributor identity are deliberately absent.
    identity = {
        "schema_version": TELEMETRY_SEMANTIC_FINGERPRINT_SCHEMA_VERSION,
        "mode": payload.get("mode"),
        "result": payload.get("result"),
        "tactical_signatures": sorted(signatures),
    }
    digest = sha256_bytes(canonical_json(identity).encode("utf-8"))
    return {
        "fingerprint": f"telemetry-semantic-{digest[:24]}",
        "identity": identity,
    }
