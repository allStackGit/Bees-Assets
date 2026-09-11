"""Mine repeated, explainable tactical signatures from approved public Human demonstrations.

The miner is intentionally diagnostic. It never creates training scenarios automatically. It
revalidates every approved native demo and summarizes a small set of policy-ABI-v7 signals that an
operator can understand: fleet identity, range at fire decisions, map-edge occupancy, distance
trend, movement activity, firing cadence, and special-action use. Repeated signatures can then be
reviewed and converted into immutable adversarial scenarios through the normal registry.
"""

from __future__ import annotations

import argparse
import json
import math
import statistics
import sys
from pathlib import Path
from typing import Callable, Dict, Mapping, Optional, Sequence, Tuple

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
from bees_continual_adversarial_suggest import (
    EXPECTED_OBSERVATION_SIZE,
    FIRST_ENEMY_SLOT_INDEX,
    FIRST_ENEMY_X_INDEX,
    FIRST_ENEMY_Y_INDEX,
    SUPPORTED_POLICY_ABI_VERSION,
    _default_observation_reader,
    _invert_positive_normalization,
    _invert_signed_distance,
    infer_geometry_from_observations,
)


MIN_TACTIC_RECORDS = 8
EXPECTED_CONTINUOUS_ACTIONS = 34
EXPECTED_DISCRETE_ACTIONS = 20
MOVEMENT_DEAD_ZONE = 0.2
SELF_SHIP_BIT_START = 0
SHIP_TYPE_BIT_COUNT = 6
SELF_MAX_RANGE_INDEX = 18
SELF_MAX_RANGE_SCALE = 80.0
ENEMY_SHIP_BIT_START = FIRST_ENEMY_SLOT_INDEX + 13
WEAPON_FIRE_BRANCH_COUNT = 16
SPECIAL_ACTION_BRANCH = 16
EDGE_NORMALIZED_THRESHOLD = 0.75
DISTANCE_TREND_EPSILON = 0.25

# Frozen ABI-v7 enum mapping guarded by RlPolicySchema. IDs rather than display names are the actual
# grouping contract; names are included only to make review output easier to read.
SHIP_TYPE_NAMES = {
    0: "Barge",
    1: "Beacon",
    2: "Beehive",
    3: "Bumblebee",
    4: "CarpenterBee",
    5: "Carrier",
    6: "Cruiser",
    7: "Dreadnought",
    8: "Drone",
    9: "Factory",
    10: "FireBarge",
    11: "Flagship",
    12: "Frigate",
    13: "Gunship",
    14: "Honeybee",
    15: "Hornet",
    16: "Leafcutter",
    17: "Queen",
    18: "Scout",
    19: "Striker",
    20: "WarpGate",
    21: "Wasp",
    22: "YellowJacket",
    23: "HumanTarget",
}


ActionReader = Callable[[object], Tuple[Sequence[float], Sequence[int]]]
ObservationReader = Callable[[object, object], Sequence[float]]


def _default_action_reader(pair_info: object) -> Tuple[Sequence[float], Sequence[int]]:
    try:
        action_info = pair_info.action_info
        continuous = tuple(float(value) for value in action_info.continuous_actions)
        discrete = tuple(int(value) for value in action_info.discrete_actions)
    except Exception as exc:
        raise ValidationError(
            f"Could not decode native demonstration actions: {type(exc).__name__}: {exc}"
        ) from exc
    if len(continuous) != EXPECTED_CONTINUOUS_ACTIONS:
        raise ValidationError(
            f"Demonstration action record has {len(continuous)} continuous actions; "
            f"expected {EXPECTED_CONTINUOUS_ACTIONS}."
        )
    if len(discrete) != EXPECTED_DISCRETE_ACTIONS:
        raise ValidationError(
            f"Demonstration action record has {len(discrete)} discrete actions; "
            f"expected {EXPECTED_DISCRETE_ACTIONS}."
        )
    return continuous, discrete


def _decode_enum_bits(values: Sequence[float], start: int, bits: int) -> int:
    result = 0
    for bit in range(bits):
        value = float(values[start + bit])
        if not math.isfinite(value):
            raise ValidationError("Tactical signature observation contains a non-finite enum bit.")
        if value >= 0.5:
            result |= 1 << bit
    return result


def _enemy_distance(values: Sequence[float]) -> Optional[float]:
    if float(values[FIRST_ENEMY_SLOT_INDEX]) < 0.5:
        return None
    relative_x = _invert_signed_distance(
        float(values[FIRST_ENEMY_X_INDEX]),
        "tactic first-enemy-x",
    )
    relative_y = _invert_signed_distance(
        float(values[FIRST_ENEMY_Y_INDEX]),
        "tactic first-enemy-y",
    )
    return math.hypot(relative_x, relative_y)


def _enemy_ship_type(values: Sequence[float]) -> Optional[int]:
    if float(values[FIRST_ENEMY_SLOT_INDEX]) < 0.5:
        return None
    return _decode_enum_bits(values, ENEMY_SHIP_BIT_START, SHIP_TYPE_BIT_COUNT)


def _movement_style(
    movement_active_fraction: float,
    edge_fraction: float,
    approach_count: int,
    retreat_count: int,
) -> str:
    if edge_fraction >= 0.50:
        return "edge-hold"
    trend_count = approach_count + retreat_count
    if trend_count >= 3:
        retreat_share = retreat_count / trend_count
        if retreat_share >= 0.65:
            return "retreat"
        if retreat_share <= 0.35:
            return "pursuit"
    if movement_active_fraction <= 0.25:
        return "stationary"
    return "maneuvering"


def _fire_style(fire_fraction: float) -> str:
    if fire_fraction <= 0.0:
        return "none"
    if fire_fraction >= 0.50:
        return "sustained"
    if fire_fraction >= 0.15:
        return "intermittent"
    return "sparse"


def _range_style(median_fire_range_ratio: Optional[float]) -> str:
    if median_fire_range_ratio is None:
        return "unobserved"
    if median_fire_range_ratio > 1.10:
        return "beyond-range"
    if median_fire_range_ratio >= 0.75:
        return "range-edge"
    if median_fire_range_ratio < 0.40:
        return "close"
    return "mid-range"


def _ship_label(ship_type: Optional[int]) -> str:
    if ship_type is None:
        return "unknown"
    return SHIP_TYPE_NAMES.get(ship_type, f"ship-{ship_type}")


def analyze_tactical_signature(
    observations: Sequence[Sequence[float]],
    continuous_actions: Sequence[Sequence[float]],
    discrete_actions: Sequence[Sequence[int]],
) -> Mapping[str, object]:
    """Create one explainable tactical signature from aligned trainable demo records."""
    record_count = len(observations)
    if record_count < MIN_TACTIC_RECORDS:
        raise ValidationError(
            f"Tactic mining requires at least {MIN_TACTIC_RECORDS} trainable records; "
            f"got {record_count}. Short capability-event demos are intentionally excluded."
        )
    if len(continuous_actions) != record_count or len(discrete_actions) != record_count:
        raise ValidationError("Tactic observations/actions must contain the same number of records.")

    normalized_observations = []
    normalized_continuous = []
    normalized_discrete = []
    for index in range(record_count):
        observation = tuple(float(value) for value in observations[index])
        continuous = tuple(float(value) for value in continuous_actions[index])
        discrete = tuple(int(value) for value in discrete_actions[index])
        if len(observation) != EXPECTED_OBSERVATION_SIZE:
            raise ValidationError(
                f"Tactic observation {index} has {len(observation)} values; "
                f"expected {EXPECTED_OBSERVATION_SIZE}."
            )
        if len(continuous) != EXPECTED_CONTINUOUS_ACTIONS:
            raise ValidationError(
                f"Tactic action {index} has {len(continuous)} continuous values; "
                f"expected {EXPECTED_CONTINUOUS_ACTIONS}."
            )
        if len(discrete) != EXPECTED_DISCRETE_ACTIONS:
            raise ValidationError(
                f"Tactic action {index} has {len(discrete)} discrete values; "
                f"expected {EXPECTED_DISCRETE_ACTIONS}."
            )
        if any(not math.isfinite(value) for value in observation + continuous):
            raise ValidationError(f"Tactic record {index} contains a non-finite value.")
        normalized_observations.append(observation)
        normalized_continuous.append(continuous)
        normalized_discrete.append(discrete)

    geometry = infer_geometry_from_observations(normalized_observations)
    self_ship_type = _decode_enum_bits(
        normalized_observations[0], SELF_SHIP_BIT_START, SHIP_TYPE_BIT_COUNT
    )
    first_enemy_ship_type = None
    for observation in normalized_observations:
        first_enemy_ship_type = _enemy_ship_type(observation)
        if first_enemy_ship_type is not None:
            break

    movement_active = 0
    edge_occupied = 0
    fire_records = 0
    special_records = 0
    fire_distances = []
    fire_range_ratios = []
    first_fire_record = None
    first_contact_record = None
    approach_count = 0
    retreat_count = 0
    previous_enemy_distance = None

    for index, (observation, continuous, discrete) in enumerate(
        zip(normalized_observations, normalized_continuous, normalized_discrete)
    ):
        movement_magnitude = math.hypot(continuous[0], continuous[1])
        if movement_magnitude > MOVEMENT_DEAD_ZONE:
            movement_active += 1

        if max(abs(observation[6]), abs(observation[7])) >= EDGE_NORMALIZED_THRESHOLD:
            edge_occupied += 1

        enemy_distance = _enemy_distance(observation)
        if enemy_distance is not None:
            if first_contact_record is None:
                first_contact_record = index
            if previous_enemy_distance is not None:
                delta = enemy_distance - previous_enemy_distance
                if delta > DISTANCE_TREND_EPSILON:
                    retreat_count += 1
                elif delta < -DISTANCE_TREND_EPSILON:
                    approach_count += 1
            previous_enemy_distance = enemy_distance
        else:
            previous_enemy_distance = None

        firing = any(value == 1 for value in discrete[:WEAPON_FIRE_BRANCH_COUNT])
        if firing:
            fire_records += 1
            if first_fire_record is None:
                first_fire_record = index
            if enemy_distance is not None:
                fire_distances.append(enemy_distance)
                max_range = _invert_positive_normalization(
                    observation[SELF_MAX_RANGE_INDEX],
                    SELF_MAX_RANGE_SCALE,
                    f"tactic record {index} self max range",
                )
                if max_range > 0.0:
                    fire_range_ratios.append(enemy_distance / max_range)

        if discrete[SPECIAL_ACTION_BRANCH] != 0:
            special_records += 1

    movement_active_fraction = movement_active / record_count
    edge_fraction = edge_occupied / record_count
    fire_fraction = fire_records / record_count
    special_fraction = special_records / record_count
    median_fire_distance = statistics.median(fire_distances) if fire_distances else None
    median_fire_range_ratio = (
        statistics.median(fire_range_ratios) if fire_range_ratios else None
    )
    movement_style = _movement_style(
        movement_active_fraction,
        edge_fraction,
        approach_count,
        retreat_count,
    )
    fire_style = _fire_style(fire_fraction)
    range_style = _range_style(median_fire_range_ratio)
    special_style = "special-active" if special_records > 0 else "no-special"

    signature = (
        f"self={self_ship_type}|enemy="
        f"{first_enemy_ship_type if first_enemy_ship_type is not None else 'unknown'}|"
        f"range={range_style}|movement={movement_style}|fire={fire_style}|special={special_style}"
    )
    return {
        "signature": signature,
        "self_ship_type": self_ship_type,
        "self_ship_name": _ship_label(self_ship_type),
        "first_enemy_ship_type": first_enemy_ship_type,
        "first_enemy_ship_name": _ship_label(first_enemy_ship_type),
        "record_count": record_count,
        "movement_active_fraction": movement_active_fraction,
        "edge_occupancy_fraction": edge_fraction,
        "fire_decision_fraction": fire_fraction,
        "special_action_fraction": special_fraction,
        "first_contact_record": first_contact_record,
        "first_fire_record": first_fire_record,
        "approach_transition_count": approach_count,
        "retreat_transition_count": retreat_count,
        "median_fire_distance": median_fire_distance,
        "median_fire_range_ratio": median_fire_range_ratio,
        "movement_style": movement_style,
        "fire_style": fire_style,
        "range_style": range_style,
        "special_style": special_style,
        "geometry": geometry,
    }


def mine_approved_tactics(
    store: ContinualLearningStore,
    batch_ids: Sequence[str],
    *,
    minimum_repeated_count: int = 2,
    loader: Optional[NativeDemoLoader] = None,
    observation_reader: Optional[ObservationReader] = None,
    action_reader: Optional[ActionReader] = None,
) -> Mapping[str, object]:
    """Analyze approved batches and group repeated explainable tactical signatures."""
    store._require_initialized()
    if store.compatibility.policy_abi_version != SUPPORTED_POLICY_ABI_VERSION:
        raise ValidationError("Tactic mining currently understands only policy ABI v7.")
    if not batch_ids:
        raise ValidationError("At least one approved demonstration batch is required.")
    if len(set(batch_ids)) != len(batch_ids):
        raise ValidationError("Tactic mining batch selection contains duplicate IDs.")
    if (
        not isinstance(minimum_repeated_count, int)
        or isinstance(minimum_repeated_count, bool)
        or minimum_repeated_count < 2
    ):
        raise ValidationError("minimum_repeated_count must be an integer of at least 2.")

    native_loader = loader or _default_native_demo_loader
    obs_reader = observation_reader or _default_observation_reader
    act_reader = action_reader or _default_action_reader
    analyzed = []
    skipped = []

    for batch_id in batch_ids:
        archive = _approved_archive(store, batch_id)
        demo_path = Path(archive["demo"])
        capture_manifest = archive["capture_manifest_metadata"]
        behavior_spec, pair_infos, total_expected = native_loader(str(demo_path))
        if (
            not isinstance(total_expected, int)
            or isinstance(total_expected, bool)
            or total_expected != len(pair_infos)
        ):
            raise ValidationError(
                f"Native demonstration count mismatch while mining {batch_id}."
            )
        _validate_native_behavior(behavior_spec, capture_manifest)

        # Match ML-Agents demonstration training semantics: the last record has no following state
        # and is not a trainable state/action example.
        trainable_pairs = pair_infos[:-1]
        if len(trainable_pairs) < MIN_TACTIC_RECORDS:
            skipped.append(
                {
                    "batch_id": batch_id,
                    "reason": "too-few-trainable-records",
                    "trainable_record_count": len(trainable_pairs),
                }
            )
            continue

        observations = []
        continuous_actions = []
        discrete_actions = []
        for pair_info in trainable_pairs:
            observations.append(obs_reader(pair_info, behavior_spec))
            continuous, discrete = act_reader(pair_info)
            continuous_actions.append(continuous)
            discrete_actions.append(discrete)

        analysis = dict(
            analyze_tactical_signature(
                observations,
                continuous_actions,
                discrete_actions,
            )
        )
        analysis["batch_id"] = batch_id
        approval = archive.get("approval")
        if isinstance(approval, Mapping):
            analysis["quality_score"] = approval.get("quality_score")
        analyzed.append(analysis)

    groups: Dict[str, list] = {}
    for analysis in analyzed:
        groups.setdefault(str(analysis["signature"]), []).append(analysis)

    repeated = []
    for signature in sorted(groups):
        members = groups[signature]
        if len(members) < minimum_repeated_count:
            continue
        fire_ratios = [
            float(member["median_fire_range_ratio"])
            for member in members
            if member["median_fire_range_ratio"] is not None
        ]
        repeated.append(
            {
                "signature": signature,
                "count": len(members),
                "batch_ids": sorted(str(member["batch_id"]) for member in members),
                "mean_edge_occupancy_fraction": statistics.fmean(
                    float(member["edge_occupancy_fraction"]) for member in members
                ),
                "mean_fire_decision_fraction": statistics.fmean(
                    float(member["fire_decision_fraction"]) for member in members
                ),
                "mean_median_fire_range_ratio": (
                    statistics.fmean(fire_ratios) if fire_ratios else None
                ),
                "review_required": True,
            }
        )

    return {
        "policy_abi_version": SUPPORTED_POLICY_ABI_VERSION,
        "minimum_repeated_count": minimum_repeated_count,
        "analyzed": analyzed,
        "skipped": skipped,
        "repeated_tactics": repeated,
        "automatic_scenario_registration": False,
    }


def _build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Mine repeated explainable tactical signatures from approved Human demos."
    )
    parser.add_argument("--root", required=True, help="Continual-learning store root.")
    parser.add_argument("batch_ids", nargs="+", help="Approved central demo-<id> batches.")
    parser.add_argument("--minimum-count", type=int, default=2)
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
        result = mine_approved_tactics(
            store,
            args.batch_ids,
            minimum_repeated_count=args.minimum_count,
        )
        print(json.dumps(result, indent=2, sort_keys=True, ensure_ascii=False))
        return 0
    except ContinualLearningError as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
