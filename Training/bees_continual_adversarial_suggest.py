"""Suggest reproducible tactical geometry from an approved public Human demonstration.

This is deliberately an operator-assist tool, not automatic tactic registration. It revalidates the
approved immutable demonstration, decodes policy ABI v7 observations, and reports geometry that can
help reconstruct the situation in headless training. The observation stream does not prove that an
estimated setup parameter caused the tactic, so the result must still be reviewed before registration.
"""

from __future__ import annotations

import argparse
import json
import math
import sys
from pathlib import Path
from typing import Callable, Mapping, Optional, Sequence

from bees_continual_adversarial import MAX_SPAWN_SEPARATION_RATIO
from bees_continual_demo_curation import _approved_archive
from bees_continual_learning import (
    ContinualLearningError,
    ContinualLearningStore,
    ValidationError,
    load_config,
)
from bees_continual_native_demo import (
    NativeDemoLoader,
    _default_native_demo_loader,
    _validate_native_behavior,
)


SUGGESTION_SCHEMA_VERSION = 1
SUPPORTED_POLICY_ABI_VERSION = 7
EXPECTED_OBSERVATION_SIZE = 4701
SELF_POSITION_X_INDEX = 6
SELF_POSITION_Y_INDEX = 7
LEVEL_SIZE_X_INDEX = 8
LEVEL_SIZE_Y_INDEX = 9
FIRST_ENEMY_SLOT_INDEX = 29 + 12 + 19 + 64 * 19
FIRST_ENEMY_X_INDEX = FIRST_ENEMY_SLOT_INDEX + 1
FIRST_ENEMY_Y_INDEX = FIRST_ENEMY_SLOT_INDEX + 2
LEVEL_SIZE_NORMALIZATION_SCALE = 100.0
LOCAL_DISTANCE_SQUASH_SCALE = 40.0
# ABI v7 observations use Level.Min/Max, which subtract ConfigData.MapEdgePadding=(5,5)
# from each edge. Keep this guarded by a focused source-level test.
MAP_EDGE_PADDING_PER_SIDE = 5.0


ObservationReader = Callable[[object, object], Sequence[float]]


def _default_observation_reader(pair_info: object, behavior_spec: object) -> Sequence[float]:
    try:
        import numpy as np
        from mlagents_envs.rpc_utils import steps_from_proto
    except ImportError as exc:
        raise ValidationError(
            "Tactical geometry suggestion requires the project's ML-Agents Python environment."
        ) from exc

    try:
        decision_steps, terminal_steps = steps_from_proto(
            [pair_info.agent_info], behavior_spec
        )
        steps = terminal_steps if len(terminal_steps) == 1 else decision_steps
        if len(steps) != 1:
            raise ValueError(f"expected one decoded agent step, got {len(steps)}")
        observations = list(steps.values())[0].obs
        if len(observations) != 1:
            raise ValueError(f"expected one vector observation, got {len(observations)}")
        values = np.asarray(observations[0], dtype=np.float64).reshape(-1)
    except Exception as exc:
        raise ValidationError(
            f"Could not decode native demonstration observation: {type(exc).__name__}: {exc}"
        ) from exc
    return tuple(float(value) for value in values)


def _invert_positive_normalization(value: float, scale: float, label: str) -> float:
    if not math.isfinite(value) or value < 0.0 or value >= 1.0:
        raise ValidationError(f"{label} observation {value!r} is outside [0,1).")
    if value == 0.0:
        return 0.0
    return scale * value / (1.0 - value)


def _invert_signed_distance(value: float, label: str) -> float:
    if not math.isfinite(value) or value <= -1.0 or value >= 1.0:
        raise ValidationError(f"{label} observation {value!r} is outside (-1,1).")
    absolute = abs(value)
    if absolute == 0.0:
        return 0.0
    return math.copysign(
        LOCAL_DISTANCE_SQUASH_SCALE * absolute / (1.0 - absolute),
        value,
    )


def infer_geometry_from_observations(
    observations: Sequence[Sequence[float]],
) -> Mapping[str, object]:
    """Infer map/setup hints from already-decoded ABI v7 vector observations."""
    if not observations:
        raise ValidationError("Demonstration contains no observations to inspect.")

    decoded = []
    for index, observation in enumerate(observations):
        values = tuple(float(value) for value in observation)
        if len(values) != EXPECTED_OBSERVATION_SIZE:
            raise ValidationError(
                f"Observation {index} has {len(values)} values; expected {EXPECTED_OBSERVATION_SIZE}."
            )
        if any(not math.isfinite(value) for value in values):
            raise ValidationError(f"Observation {index} contains a non-finite value.")
        decoded.append(values)

    usable_sizes = []
    for index, values in enumerate(decoded):
        usable_x = _invert_positive_normalization(
            values[LEVEL_SIZE_X_INDEX],
            LEVEL_SIZE_NORMALIZATION_SCALE,
            f"observation {index} level-size-x",
        )
        usable_y = _invert_positive_normalization(
            values[LEVEL_SIZE_Y_INDEX],
            LEVEL_SIZE_NORMALIZATION_SCALE,
            f"observation {index} level-size-y",
        )
        usable_sizes.append((usable_x, usable_y))

    first_usable_x, first_usable_y = usable_sizes[0]
    max_size_drift = max(
        max(abs(x - first_usable_x), abs(y - first_usable_y))
        for x, y in usable_sizes
    )
    if max_size_drift > 0.05:
        raise ValidationError(
            "Demonstration level-size observations changed during one recording; "
            "refusing to infer one tactical map size."
        )

    map_width_estimate = first_usable_x + MAP_EDGE_PADDING_PER_SIDE * 2.0
    map_height_estimate = first_usable_y + MAP_EDGE_PADDING_PER_SIDE * 2.0
    square_tolerance = max(0.05, max(map_width_estimate, map_height_estimate) * 0.01)
    square_training_map_compatible = (
        abs(map_width_estimate - map_height_estimate) <= square_tolerance
    )
    map_size_estimate = (
        (map_width_estimate + map_height_estimate) * 0.5
        if square_training_map_compatible
        else None
    )

    first = decoded[0]
    normalized_x = first[SELF_POSITION_X_INDEX]
    normalized_y = first[SELF_POSITION_Y_INDEX]
    if not -1.0 <= normalized_x <= 1.0 or not -1.0 <= normalized_y <= 1.0:
        raise ValidationError("First self-position observation is outside normalized map bounds.")
    center_x = normalized_x * first_usable_x * 0.5
    center_y = normalized_y * first_usable_y * 0.5
    first_center_radius = math.hypot(center_x, center_y)
    spawn_separation_estimate = first_center_radius * 2.0
    spawn_ratio_estimate = (
        spawn_separation_estimate / map_size_estimate
        if map_size_estimate is not None and map_size_estimate > 0.0
        else None
    )

    first_enemy_distance = None
    first_enemy_record = None
    for index, values in enumerate(decoded):
        if values[FIRST_ENEMY_SLOT_INDEX] < 0.5:
            continue
        relative_x = _invert_signed_distance(
            values[FIRST_ENEMY_X_INDEX],
            f"observation {index} first-enemy-x",
        )
        relative_y = _invert_signed_distance(
            values[FIRST_ENEMY_Y_INDEX],
            f"observation {index} first-enemy-y",
        )
        first_enemy_distance = math.hypot(relative_x, relative_y)
        first_enemy_record = index
        break

    registration_eligible = (
        map_size_estimate is not None
        and map_size_estimate >= 10.0
        and spawn_ratio_estimate is not None
        and spawn_ratio_estimate > 0.0
        and spawn_ratio_estimate <= MAX_SPAWN_SEPARATION_RATIO
    )
    return {
        "usable_level_size": {
            "x": first_usable_x,
            "y": first_usable_y,
        },
        "map_width_estimate": map_width_estimate,
        "map_height_estimate": map_height_estimate,
        "square_training_map_compatible": square_training_map_compatible,
        "map_size_estimate": map_size_estimate,
        "first_record_center_distance": first_center_radius,
        "spawn_separation_estimate": spawn_separation_estimate,
        "spawn_separation_ratio_estimate": spawn_ratio_estimate,
        "first_visible_enemy_distance": first_enemy_distance,
        "first_visible_enemy_record": first_enemy_record,
        "registration_candidate": (
            {
                "map_size": map_size_estimate,
                "spawn_separation_ratio": spawn_ratio_estimate,
            }
            if registration_eligible
            else None
        ),
        "observation_count": len(decoded),
    }


def suggest_tactical_geometry(
    store: ContinualLearningStore,
    batch_id: str,
    *,
    loader: Optional[NativeDemoLoader] = None,
    observation_reader: Optional[ObservationReader] = None,
) -> Mapping[str, object]:
    """Revalidate one approved batch and derive non-authoritative geometry hints from its demo."""
    store._require_initialized()
    if store.compatibility.policy_abi_version != SUPPORTED_POLICY_ABI_VERSION:
        raise ValidationError(
            "Tactical geometry suggestion currently understands only policy ABI v7."
        )

    archive = _approved_archive(store, batch_id)
    demo_path = Path(archive["demo"])
    capture_manifest = archive["capture_manifest_metadata"]
    native_loader = loader or _default_native_demo_loader
    behavior_spec, pair_infos, total_expected = native_loader(str(demo_path))
    if (
        not isinstance(total_expected, int)
        or isinstance(total_expected, bool)
        or total_expected != len(pair_infos)
    ):
        raise ValidationError(
            "Native demonstration metadata count does not match parsed records during suggestion."
        )
    _validate_native_behavior(behavior_spec, capture_manifest)
    if capture_manifest.get("observationSize") != EXPECTED_OBSERVATION_SIZE:
        raise ValidationError(
            "Tactical geometry suggestion requires the exact ABI v7 observation size."
        )

    reader = observation_reader or _default_observation_reader
    observations = [reader(pair_info, behavior_spec) for pair_info in pair_infos]
    inferred = infer_geometry_from_observations(observations)
    return {
        "schema_version": SUGGESTION_SCHEMA_VERSION,
        "batch_id": batch_id,
        "policy_abi_version": SUPPORTED_POLICY_ABI_VERSION,
        "inferred": inferred,
        "authoritative": False,
        "caveats": [
            "Map dimensions are reconstructed from ABI v7 usable Level bounds plus the current 5-unit edge padding per side.",
            "Only square captures can become a direct registration_candidate because the current dedicated tactical replay arena is square.",
            "Spawn separation is estimated from the first recorded ship position and is strongest for 1v1 captures before meaningful movement.",
            "Multi-ship formation offsets or delayed recording can make the spawn-separation estimate approximate.",
            "First visible enemy distance is first recorded contact distance, not necessarily initial spawn separation.",
            "Review the demonstration before copying registration_candidate into an adversarial scenario.",
        ],
    }


def _build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Suggest tactical replay geometry from an approved ABI v7 Human demonstration."
    )
    parser.add_argument("--root", required=True, help="Continual-learning store root.")
    parser.add_argument("batch_id", help="Approved central demo-<id> batch.")
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
        suggestion = suggest_tactical_geometry(store, args.batch_id)
        print(json.dumps(suggestion, indent=2, sort_keys=True, ensure_ascii=False))
        return 0
    except ContinualLearningError as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
