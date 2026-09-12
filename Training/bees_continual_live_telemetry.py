"""Strict schema validation for public live-RL match telemetry.

`ContinualLearningStore.ingest_telemetry()` is the long-standing trusted/local archive API and stays
backward compatible. Public client data must pass this stricter boundary first: exact frozen policy
identity, known deployment/model bytes, fixed observation/action shapes, action bounds, monotonic
per-agent decision identities, and configured payload/episode limits are all verified before the
payload can be handed to the central archive.
"""

from __future__ import annotations

import json
import math
import re
from pathlib import Path
from typing import Any, Dict, Mapping, Sequence, Tuple

from bees_continual_learning import (
    CompatibilityError,
    ContinualLearningStore,
    ValidationError,
    canonical_json,
)


LIVE_TELEMETRY_SCHEMA_VERSION = 1
_VALID_RESULTS = frozenset(("bee_win", "human_win", "draw", "timeout"))
_DEPLOYMENT_ID = re.compile(r"^deploy-[0-9a-f]{24}$")
_SHA256 = re.compile(r"^[0-9a-f]{64}$")


def _required_string(payload: Mapping[str, Any], key: str, maximum: int = 256) -> str:
    value = payload.get(key)
    if not isinstance(value, str) or not value.strip() or len(value) > maximum:
        raise ValidationError(f"{key} must be a non-empty string up to {maximum} characters.")
    return value.strip()


def _finite_number(value: Any) -> bool:
    return (
        isinstance(value, (int, float))
        and not isinstance(value, bool)
        and math.isfinite(float(value))
    )


def _required_positive_int(settings: Mapping[str, Any], key: str) -> int:
    value = settings.get(key)
    if not isinstance(value, int) or isinstance(value, bool) or value <= 0:
        raise ValidationError(f"ingestion.{key} must be a positive integer for public telemetry.")
    return value


def _telemetry_contract(store: ContinualLearningStore) -> Tuple[int, int, Tuple[int, ...]]:
    ingestion = store.config.get("ingestion")
    if not isinstance(ingestion, Mapping):
        raise ValidationError("Continual-learning config is missing ingestion settings.")
    observation_size = _required_positive_int(ingestion, "telemetry_observation_size")
    continuous_count = _required_positive_int(ingestion, "telemetry_continuous_action_count")
    raw_branches = ingestion.get("telemetry_discrete_branch_sizes")
    if not isinstance(raw_branches, list) or not raw_branches:
        raise ValidationError(
            "ingestion.telemetry_discrete_branch_sizes must be a non-empty integer list."
        )
    branches = []
    for index, value in enumerate(raw_branches):
        if not isinstance(value, int) or isinstance(value, bool) or value <= 0:
            raise ValidationError(
                f"ingestion.telemetry_discrete_branch_sizes[{index}] must be a positive integer."
            )
        branches.append(value)
    return observation_size, continuous_count, tuple(branches)


def _deployment_manifest(store: ContinualLearningStore, deployment_id: str) -> Mapping[str, Any]:
    path = store.root / "deployment" / "packages" / deployment_id / "manifest.json"
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except FileNotFoundError as exc:
        raise CompatibilityError(
            f"Live telemetry references unknown deployment {deployment_id}."
        ) from exc
    except json.JSONDecodeError as exc:
        raise ValidationError(
            f"Deployment manifest for {deployment_id} is invalid JSON."
        ) from exc
    if not isinstance(value, dict):
        raise ValidationError(f"Deployment manifest for {deployment_id} must be an object.")
    return value


def _validate_deployment_identity(
    store: ContinualLearningStore,
    payload: Mapping[str, Any],
    *,
    model: Mapping[str, Any],
) -> str:
    deployment_id = _required_string(payload, "deployment_id", 64)
    if not _DEPLOYMENT_ID.fullmatch(deployment_id):
        raise ValidationError("deployment_id must be a canonical deploy-<24 lowercase hex> identity.")

    model_sha256 = _required_string(payload, "model_sha256", 64).lower()
    if not _SHA256.fullmatch(model_sha256):
        raise ValidationError("model_sha256 must be a lowercase SHA-256 digest.")
    if model_sha256 != model.get("artifact_sha256"):
        raise CompatibilityError("Live telemetry model_sha256 does not match the registered model.")

    signature = _required_string(payload, "policy_signature", 4096)
    expected_signature = store.config.get("policy_signature")
    if not isinstance(expected_signature, str) or not expected_signature.strip():
        raise ValidationError(
            "Continual-learning config must define policy_signature before public telemetry is accepted."
        )
    if signature != expected_signature.strip():
        raise CompatibilityError("Live telemetry policy_signature does not match the frozen policy.")

    manifest = _deployment_manifest(store, deployment_id)
    if manifest.get("deployment_id") != deployment_id:
        raise ValidationError("Deployment manifest identity does not match its directory.")
    identity = manifest.get("identity")
    if not isinstance(identity, Mapping):
        raise ValidationError("Deployment manifest identity is missing or malformed.")
    if identity.get("model_id") != model.get("model_id"):
        raise CompatibilityError("Live telemetry deployment does not contain the declared model_id.")
    if identity.get("model_sha256") != model_sha256:
        raise CompatibilityError("Live telemetry deployment does not contain the declared model bytes.")
    if identity.get("policy_signature") != signature:
        raise CompatibilityError("Live telemetry deployment policy signature does not match the payload.")
    return deployment_id


def _validate_compatibility(store: ContinualLearningStore, payload: Mapping[str, Any]) -> None:
    expected = {
        "behavior_name": store.compatibility.behavior_name,
        "policy_abi_version": store.compatibility.policy_abi_version,
        "observation_schema_version": store.compatibility.observation_schema_version,
        "action_schema_version": store.compatibility.action_schema_version,
        "reward_schema_version": store.compatibility.reward_schema_version,
        "scenario_schema_version": store.compatibility.scenario_schema_version,
    }
    for key, expected_value in expected.items():
        if payload.get(key) != expected_value:
            raise CompatibilityError(
                f"Live telemetry {key}={payload.get(key)!r} is incompatible; expected {expected_value!r}."
            )


def _validate_float_vector(value: Any, *, length: int, label: str, bounded_action: bool = False) -> None:
    if not isinstance(value, list) or len(value) != length:
        raise ValidationError(f"{label} must contain exactly {length} numeric values.")
    for index, item in enumerate(value):
        if not _finite_number(item):
            raise ValidationError(f"{label}[{index}] must be finite numeric data.")
        if bounded_action and not -1.0 <= float(item) <= 1.0:
            raise ValidationError(f"{label}[{index}] must be within the ML-Agents action range [-1,1].")


def _validate_discrete_actions(value: Any, branch_sizes: Sequence[int], label: str) -> None:
    if not isinstance(value, list) or len(value) != len(branch_sizes):
        raise ValidationError(
            f"{label} must contain exactly {len(branch_sizes)} discrete branch values."
        )
    for index, (item, branch_size) in enumerate(zip(value, branch_sizes)):
        if not isinstance(item, int) or isinstance(item, bool) or item < 0 or item >= branch_size:
            raise ValidationError(
                f"{label}[{index}]={item!r} is outside branch range 0-{branch_size - 1}."
            )


def validate_live_telemetry_payload(
    store: ContinualLearningStore,
    payload: Mapping[str, Any],
) -> Dict[str, Any]:
    """Validate one public live-RL match payload without mutating the continual-learning store."""
    store._require_initialized()
    if not isinstance(payload, Mapping):
        raise ValidationError("Live telemetry payload must be an object.")
    if payload.get("schema_version") != LIVE_TELEMETRY_SCHEMA_VERSION:
        raise CompatibilityError(
            f"Live telemetry schema_version must be {LIVE_TELEMETRY_SCHEMA_VERSION}."
        )

    encoded = canonical_json(dict(payload)).encode("utf-8")
    ingestion = store.config.get("ingestion")
    if not isinstance(ingestion, Mapping):
        raise ValidationError("Continual-learning config is missing ingestion settings.")
    max_payload_bytes = _required_positive_int(ingestion, "max_payload_bytes")
    if len(encoded) > max_payload_bytes:
        raise ValidationError("Live telemetry payload exceeds configured maximum size.")

    match_id = _required_string(payload, "match_id")
    _required_string(payload, "game_build_version")
    mode = _required_string(payload, "mode")
    result = _required_string(payload, "result")
    if result not in _VALID_RESULTS:
        raise ValidationError(
            "result must be one of: " + ", ".join(sorted(_VALID_RESULTS)) + "."
        )

    model_id = _required_string(payload, "model_id")
    model = store.get_model(model_id)
    expected_model = store.compatibility.to_dict()
    actual_model = {key: model.get(key) for key in expected_model}
    if actual_model != expected_model:
        raise CompatibilityError(
            f"Live telemetry model {model_id} compatibility {actual_model} does not match {expected_model}."
        )
    _validate_compatibility(store, payload)
    deployment_id = _validate_deployment_identity(store, payload, model=model)

    observation_size, continuous_count, branch_sizes = _telemetry_contract(store)
    steps = payload.get("steps")
    max_steps = _required_positive_int(ingestion, "max_steps_per_match")
    if not isinstance(steps, list) or not steps:
        raise ValidationError("Live telemetry steps must be a non-empty list.")
    if len(steps) > max_steps:
        raise ValidationError("Live telemetry step count exceeds configured maximum.")

    last_decision_by_agent: Dict[str, int] = {}
    for index, step in enumerate(steps):
        if not isinstance(step, Mapping):
            raise ValidationError(f"steps[{index}] must be an object.")
        agent_key = _required_string(step, "agent_key", 128)
        decision_index = step.get("decision_index")
        if not isinstance(decision_index, int) or isinstance(decision_index, bool) or decision_index < 0:
            raise ValidationError(f"steps[{index}].decision_index must be a non-negative integer.")
        previous = last_decision_by_agent.get(agent_key)
        if previous is not None and decision_index <= previous:
            raise ValidationError(
                f"steps[{index}] decision_index must increase for agent {agent_key!r}."
            )
        last_decision_by_agent[agent_key] = decision_index

        _validate_float_vector(
            step.get("observation"),
            length=observation_size,
            label=f"steps[{index}].observation",
        )
        _validate_float_vector(
            step.get("continuous_action"),
            length=continuous_count,
            label=f"steps[{index}].continuous_action",
            bounded_action=True,
        )
        _validate_discrete_actions(
            step.get("discrete_action"),
            branch_sizes,
            f"steps[{index}].discrete_action",
        )

    return {
        "schema_version": LIVE_TELEMETRY_SCHEMA_VERSION,
        "match_id": match_id,
        "model_id": model_id,
        "deployment_id": deployment_id,
        "mode": mode,
        "result": result,
        "step_count": len(steps),
        "agent_count": len(last_decision_by_agent),
        "payload_bytes": len(encoded),
        "observation_size": observation_size,
        "continuous_action_count": continuous_count,
        "discrete_branch_sizes": list(branch_sizes),
    }
