"""Compile approved Human demonstrations into immutable scripted adversarial replay artifacts.

This module is deliberately separate from PPO and from scenario registration. A reviewed immutable
adversarial scenario may have at most one replay attachment. The attachment points at one of the
scenario's already-approved public Human demonstrations and compiles only the frozen ABI-v7
movement, turret-aim and fire actions into a compact deterministic binary. Capability/target actions
fail closed instead of being silently dropped.

The compiled replay is an opponent script, not PPO experience. Runtime integration keeps the
scripted side out of learner action/reward ownership while the opposing policy generates fresh
on-policy responses.
"""

from __future__ import annotations

import argparse
import io
import json
import math
import os
import re
import struct
import sys
from pathlib import Path
from typing import Callable, Mapping, Optional, Sequence, Tuple

from bees_continual_adversarial import (
    _parse_composition,
    _read_scenario,
    _validate_registered_sources,
)
from bees_continual_adversarial_mine import (
    EXPECTED_CONTINUOUS_ACTIONS,
    EXPECTED_DISCRETE_ACTIONS,
    SHIP_TYPE_NAMES,
    _decode_enum_bits,
    _default_action_reader,
    _enemy_ship_type,
)
from bees_continual_adversarial_suggest import (
    EXPECTED_OBSERVATION_SIZE,
    LEVEL_SIZE_NORMALIZATION_SCALE,
    LEVEL_SIZE_X_INDEX,
    LEVEL_SIZE_Y_INDEX,
    SELF_POSITION_X_INDEX,
    SELF_POSITION_Y_INDEX,
    SUPPORTED_POLICY_ABI_VERSION,
    _default_observation_reader,
    _invert_positive_normalization,
)
from bees_continual_demo_curation import _approved_archive
from bees_continual_learning import (
    ContinualLearningError,
    ContinualLearningStore,
    ValidationError,
    canonical_json,
    load_config,
    sha256_bytes,
    utc_now,
)
from bees_continual_native_demo import (
    NativeDemoLoader,
    _default_native_demo_loader,
    _validate_native_behavior,
    _write_bytes_immutable,
)


REPLAY_REGISTRATION_SCHEMA_VERSION = 1
REPLAY_ARTIFACT_SCHEMA_VERSION = 1
REPLAY_CATALOG_SCHEMA_VERSION = 1
REPLAY_MODE = "movement-aim-fire-prefix"
REPLAY_TERMINAL_BEHAVIOR = "neutral"
REPLAY_MAGIC = b"BEESRPL1"
REPLAY_FIXED_STEP_INTERVAL = 5
MIN_REPLAY_RECORDS = 8
MAX_REPLAY_RECORDS = 2400
DEFAULT_REPLAY_RECORDS = 1200
WEAPON_FIRE_BRANCH_COUNT = 16
SPECIAL_ACTION_BRANCH = 16
ALLY_TARGET_BRANCH = 17
ENEMY_TARGET_BRANCH = 18
MAP_OBJECT_TARGET_BRANCH = 19
SELF_SHIP_BIT_START = 0
SHIP_TYPE_BIT_COUNT = 6
_REPLAY_ID = re.compile(r"^advreplay-[0-9a-f]{24}$")
_BATCH_ID = re.compile(r"^demo-[0-9a-f]{24}$")
_SCENARIO_ID = re.compile(r"^adv-[0-9a-f]{24}$")

ObservationReader = Callable[[object, object], Sequence[float]]
ActionReader = Callable[[object], Tuple[Sequence[float], Sequence[int]]]

SHIP_TYPE_IDS = {name: value for value, name in SHIP_TYPE_NAMES.items()}


def _required_scenario_id(value: object) -> str:
    scenario_id = str(value).strip() if value is not None else ""
    if not _SCENARIO_ID.fullmatch(scenario_id):
        raise ValidationError(f"Invalid adversarial scenario ID: {value!r}.")
    return scenario_id


def _required_batch_id(value: object) -> str:
    batch_id = str(value).strip() if value is not None else ""
    if not _BATCH_ID.fullmatch(batch_id):
        raise ValidationError(f"Invalid approved demonstration batch ID: {value!r}.")
    return batch_id


def _replay_side(value: object) -> str:
    normalized = str(value).strip().lower() if value is not None else ""
    if normalized == "bee":
        return "Bee"
    if normalized == "human":
        return "Human"
    raise ValidationError("Replay side must be Bee or Human.")


def _record_count(value: object) -> int:
    if not isinstance(value, int) or isinstance(value, bool):
        raise ValidationError(
            f"Replay record_count must be an integer between {MIN_REPLAY_RECORDS} and {MAX_REPLAY_RECORDS}."
        )
    if value < MIN_REPLAY_RECORDS or value > MAX_REPLAY_RECORDS:
        raise ValidationError(
            f"Replay record_count must be between {MIN_REPLAY_RECORDS} and {MAX_REPLAY_RECORDS}."
        )
    return value


def _registration_path(store: ContinualLearningStore, scenario_id: str) -> Path:
    return (
        store.experience_dir
        / "adversarial-replays"
        / "registrations"
        / f"{scenario_id}.json"
    )


def _artifact_path(store: ContinualLearningStore, replay_id: str) -> Path:
    return store.experience_dir / "adversarial-replays" / "artifacts" / f"{replay_id}.brpl"


def _artifact_metadata_path(store: ContinualLearningStore, replay_id: str) -> Path:
    return store.experience_dir / "adversarial-replays" / "artifacts" / f"{replay_id}.json"


def _read_json_object(path: Path, label: str) -> Mapping[str, object]:
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except FileNotFoundError as exc:
        raise ValidationError(f"{label} does not exist: {path}") from exc
    except json.JSONDecodeError as exc:
        raise ValidationError(f"{label} is invalid JSON: {path}: {exc}") from exc
    if not isinstance(value, dict):
        raise ValidationError(f"{label} must contain a JSON object: {path}")
    return value


def _scenario_sources(identity: Mapping[str, object]) -> Mapping[str, Mapping[str, object]]:
    values = identity.get("sources")
    if not isinstance(values, list):
        raise ValidationError("Adversarial scenario replay requires immutable source provenance.")
    result = {}
    for source in values:
        if not isinstance(source, Mapping):
            raise ValidationError("Adversarial scenario replay source provenance is malformed.")
        batch_id = source.get("batch_id")
        if not isinstance(batch_id, str) or not _BATCH_ID.fullmatch(batch_id):
            raise ValidationError("Adversarial scenario replay source batch identity is malformed.")
        result[batch_id] = source
    return result


def _validate_replay_scenario(identity: Mapping[str, object]) -> Tuple[Sequence[str], Sequence[str]]:
    bees = _parse_composition(identity.get("bee_composition"), "bee_composition")
    humans = _parse_composition(identity.get("human_composition"), "human_composition")
    if len(bees) != 1 or len(humans) != 1:
        raise ValidationError(
            "Scripted action replay currently supports only reviewed 1v1 adversarial scenarios."
        )
    return bees, humans


def register_action_replay(
    store: ContinualLearningStore,
    scenario_id: str,
    *,
    source_batch_id: str,
    side: str,
    record_count: int = DEFAULT_REPLAY_RECORDS,
) -> Mapping[str, object]:
    """Attach exactly one immutable action-replay definition to a reviewed 1v1 scenario."""
    store._require_initialized()
    scenario_id = _required_scenario_id(scenario_id)
    source_batch_id = _required_batch_id(source_batch_id)
    side = _replay_side(side)
    record_count = _record_count(record_count)

    scenario = _read_scenario(store, scenario_id)
    identity = scenario["identity"]
    _validate_registered_sources(store, scenario_id, identity)
    _validate_replay_scenario(identity)
    sources = _scenario_sources(identity)
    if source_batch_id not in sources:
        raise ValidationError(
            f"Replay source {source_batch_id} is not one of scenario {scenario_id}'s approved sources."
        )

    archive = _approved_archive(store, source_batch_id)
    source = sources[source_batch_id]
    if source.get("demo_sha256") != archive["demo_sha256"] or source.get(
        "payload_sha256"
    ) != archive["row"]["payload_sha256"]:
        raise ValidationError(
            f"Replay source {source_batch_id} no longer matches scenario {scenario_id}'s immutable provenance."
        )

    replay_identity = {
        "schema_version": REPLAY_REGISTRATION_SCHEMA_VERSION,
        "scenario_id": scenario_id,
        "scenario_identity_sha256": scenario["identity_sha256"],
        "source_batch_id": source_batch_id,
        "source_demo_sha256": archive["demo_sha256"],
        "source_payload_sha256": archive["row"]["payload_sha256"],
        "policy_abi_version": store.compatibility.policy_abi_version,
        "side": side,
        "mode": REPLAY_MODE,
        "start_record": 0,
        "record_count": record_count,
        "fixed_step_interval": REPLAY_FIXED_STEP_INTERVAL,
        "terminal_behavior": REPLAY_TERMINAL_BEHAVIOR,
    }
    identity_hash = sha256_bytes(canonical_json(replay_identity).encode("utf-8"))
    replay_id = f"advreplay-{identity_hash[:24]}"
    body = {
        "schema_version": REPLAY_REGISTRATION_SCHEMA_VERSION,
        "replay_id": replay_id,
        "identity_sha256": identity_hash,
        "identity": replay_identity,
        "created_at": utc_now(),
    }
    path = _registration_path(store, scenario_id)
    payload = (json.dumps(body, indent=2, sort_keys=True, ensure_ascii=False) + "\n").encode("utf-8")
    if path.exists():
        existing = _read_registered_replay(store, scenario_id)
        if existing["identity_sha256"] == identity_hash:
            return {
                "replay_id": replay_id,
                "duplicate": True,
                "path": str(path),
                "registration": existing,
            }
        raise ContinualLearningError(
            f"Scenario {scenario_id} already has a different immutable replay attachment. "
            "Register a new adversarial scenario before selecting a different replay source or segment."
        )
    _write_bytes_immutable(path, payload)
    return {
        "replay_id": replay_id,
        "duplicate": False,
        "path": str(path),
        "registration": body,
    }


def _read_registered_replay(
    store: ContinualLearningStore,
    scenario_id: str,
) -> Mapping[str, object]:
    scenario_id = _required_scenario_id(scenario_id)
    path = _registration_path(store, scenario_id)
    value = _read_json_object(path, "Adversarial replay registration")
    if value.get("schema_version") != REPLAY_REGISTRATION_SCHEMA_VERSION:
        raise ValidationError(f"Adversarial replay registration schema is incompatible: {path}")
    replay_id = value.get("replay_id")
    identity = value.get("identity")
    identity_hash = value.get("identity_sha256")
    if (
        not isinstance(replay_id, str)
        or not _REPLAY_ID.fullmatch(replay_id)
        or not isinstance(identity, dict)
        or not isinstance(identity_hash, str)
    ):
        raise ValidationError(f"Adversarial replay registration identity is incomplete: {path}")
    expected_hash = sha256_bytes(canonical_json(identity).encode("utf-8"))
    if identity_hash != expected_hash or replay_id != f"advreplay-{expected_hash[:24]}":
        raise ValidationError(f"Adversarial replay registration hash mismatch: {path}")
    if identity.get("scenario_id") != scenario_id:
        raise ValidationError(f"Adversarial replay scenario identity mismatch: {path}")
    if identity.get("policy_abi_version") != store.compatibility.policy_abi_version:
        raise ValidationError(
            f"Adversarial replay {replay_id} targets policy ABI {identity.get('policy_abi_version')!r}, "
            f"not current ABI {store.compatibility.policy_abi_version}."
        )
    if identity.get("mode") != REPLAY_MODE or identity.get(
        "terminal_behavior"
    ) != REPLAY_TERMINAL_BEHAVIOR:
        raise ValidationError(f"Adversarial replay mode is incompatible: {path}")
    if identity.get("start_record") != 0:
        raise ValidationError("Adversarial action replay currently requires start_record=0.")
    if identity.get("fixed_step_interval") != REPLAY_FIXED_STEP_INTERVAL:
        raise ValidationError("Adversarial action replay fixed-step interval is incompatible.")
    _record_count(identity.get("record_count"))
    _required_batch_id(identity.get("source_batch_id"))
    _replay_side(identity.get("side"))

    scenario = _read_scenario(store, scenario_id)
    if identity.get("scenario_identity_sha256") != scenario["identity_sha256"]:
        raise ValidationError(
            f"Adversarial replay {replay_id} no longer matches scenario {scenario_id}'s immutable identity."
        )
    _validate_registered_sources(store, scenario_id, scenario["identity"])
    _validate_replay_scenario(scenario["identity"])
    return value


def read_registered_replay_if_present(
    store: ContinualLearningStore,
    scenario_id: str,
) -> Optional[Mapping[str, object]]:
    path = _registration_path(store, _required_scenario_id(scenario_id))
    if not path.is_file():
        return None
    return _read_registered_replay(store, scenario_id)


def _expected_ship_type_id(ship_name: str) -> int:
    if ship_name not in SHIP_TYPE_IDS:
        raise ValidationError(
            f"Replay scenario ship type {ship_name!r} is not part of the frozen ABI-v7 ship mapping."
        )
    return SHIP_TYPE_IDS[ship_name]


def _source_start_direction(observation: Sequence[float]) -> Tuple[float, float]:
    if len(observation) != EXPECTED_OBSERVATION_SIZE:
        raise ValidationError(
            f"Replay source observation has {len(observation)} values; expected {EXPECTED_OBSERVATION_SIZE}."
        )
    usable_x = _invert_positive_normalization(
        float(observation[LEVEL_SIZE_X_INDEX]),
        LEVEL_SIZE_NORMALIZATION_SCALE,
        "replay source level-size-x",
    )
    usable_y = _invert_positive_normalization(
        float(observation[LEVEL_SIZE_Y_INDEX]),
        LEVEL_SIZE_NORMALIZATION_SCALE,
        "replay source level-size-y",
    )
    x = float(observation[SELF_POSITION_X_INDEX]) * usable_x * 0.5
    y = float(observation[SELF_POSITION_Y_INDEX]) * usable_y * 0.5
    length = math.hypot(x, y)
    if not math.isfinite(length) or length <= 0.001:
        raise ValidationError(
            "Replay source first record is too close to map center to derive deterministic spawn-axis rotation."
        )
    return x / length, y / length


def _validate_action_record(
    continuous: Sequence[float],
    discrete: Sequence[int],
    index: int,
) -> Tuple[Tuple[float, ...], int]:
    if len(continuous) != EXPECTED_CONTINUOUS_ACTIONS:
        raise ValidationError(
            f"Replay action {index} has {len(continuous)} continuous values; expected {EXPECTED_CONTINUOUS_ACTIONS}."
        )
    if len(discrete) != EXPECTED_DISCRETE_ACTIONS:
        raise ValidationError(
            f"Replay action {index} has {len(discrete)} discrete values; expected {EXPECTED_DISCRETE_ACTIONS}."
        )
    normalized = tuple(float(value) for value in continuous)
    if any(not math.isfinite(value) or abs(value) > 1.0001 for value in normalized):
        raise ValidationError(
            f"Replay action {index} contains a non-finite or out-of-range continuous action."
        )
    fire_mask = 0
    for branch in range(WEAPON_FIRE_BRANCH_COUNT):
        value = int(discrete[branch])
        if value not in (0, 1):
            raise ValidationError(
                f"Replay action {index} weapon-fire branch {branch} is outside the frozen 0/1 action space."
            )
        if value == 1:
            fire_mask |= 1 << branch
    if int(discrete[SPECIAL_ACTION_BRANCH]) != 0:
        raise ValidationError(
            "Scripted replay does not accept capability-event actions; use a normal continuous Human demo."
        )
    if any(
        int(discrete[branch]) != 0
        for branch in (ALLY_TARGET_BRANCH, ENEMY_TARGET_BRANCH, MAP_OBJECT_TARGET_BRANCH)
    ):
        raise ValidationError(
            "Scripted replay currently requires zero ally/enemy/map target branches."
        )
    return normalized, fire_mask


def _pair_is_terminal(pair_info: object) -> bool:
    agent_info = getattr(pair_info, "agent_info", None)
    return bool(getattr(agent_info, "done", False)) if agent_info is not None else False


def _first_episode_trainable_pairs(pair_infos: Sequence[object]) -> Tuple[Sequence[object], bool]:
    """Return trainable records from only the first native episode.

    ML-Agents trains action/observation record i against record i+1, so a terminal record is needed as
    the next-state boundary but is not itself a replay action. If the file has no explicit terminal
    record (for example recording closed mid-episode), mirror the native demo loader by dropping the
    final record because it has no successor.
    """
    records = []
    terminal_found = False
    for pair_info in pair_infos:
        if _pair_is_terminal(pair_info):
            terminal_found = True
            break
        records.append(pair_info)
    if not terminal_found and records:
        records = records[:-1]
    return tuple(records), terminal_found


def _binary_payload(
    frames: Sequence[Tuple[Sequence[float], int]],
    source_start_direction: Tuple[float, float],
) -> bytes:
    output = io.BytesIO()
    output.write(
        struct.pack(
            "<8sii2f",
            REPLAY_MAGIC,
            len(frames),
            REPLAY_FIXED_STEP_INTERVAL,
            float(source_start_direction[0]),
            float(source_start_direction[1]),
        )
    )
    for continuous, fire_mask in frames:
        output.write(struct.pack("<34fH", *continuous, int(fire_mask)))
    return output.getvalue()


def compile_action_replay(
    store: ContinualLearningStore,
    scenario_id: str,
    *,
    loader: Optional[NativeDemoLoader] = None,
    observation_reader: Optional[ObservationReader] = None,
    action_reader: Optional[ActionReader] = None,
) -> Mapping[str, object]:
    """Compile one registered replay into a deterministic compact binary artifact."""
    store._require_initialized()
    if store.compatibility.policy_abi_version != SUPPORTED_POLICY_ABI_VERSION:
        raise ValidationError("Scripted action replay currently understands only policy ABI v7.")

    registration = _read_registered_replay(store, scenario_id)
    replay_id = str(registration["replay_id"])
    replay_identity = registration["identity"]
    scenario = _read_scenario(store, scenario_id)
    scenario_identity = scenario["identity"]
    bees, humans = _validate_replay_scenario(scenario_identity)
    source_batch_id = _required_batch_id(replay_identity.get("source_batch_id"))
    source_map = _scenario_sources(scenario_identity)
    source = source_map.get(source_batch_id)
    if source is None:
        raise ValidationError(
            f"Replay source {source_batch_id} is no longer part of scenario {scenario_id}."
        )

    archive = _approved_archive(store, source_batch_id)
    if (
        replay_identity.get("source_demo_sha256") != archive["demo_sha256"]
        or replay_identity.get("source_payload_sha256") != archive["row"]["payload_sha256"]
        or source.get("demo_sha256") != archive["demo_sha256"]
    ):
        raise ValidationError(
            f"Replay source {source_batch_id} no longer matches its immutable approved archive."
        )

    native_loader = loader or _default_native_demo_loader
    obs_reader = observation_reader or _default_observation_reader
    act_reader = action_reader or _default_action_reader
    behavior_spec, pair_infos, total_expected = native_loader(str(Path(archive["demo"])))
    if (
        not isinstance(total_expected, int)
        or isinstance(total_expected, bool)
        or total_expected != len(pair_infos)
    ):
        raise ValidationError("Native demonstration count mismatch while compiling replay.")
    _validate_native_behavior(behavior_spec, archive["capture_manifest_metadata"])

    trainable_pairs, terminal_found = _first_episode_trainable_pairs(pair_infos)
    requested_count = _record_count(replay_identity.get("record_count"))
    if len(trainable_pairs) < MIN_REPLAY_RECORDS:
        raise ValidationError(
            "Replay source first episode has only "
            f"{len(trainable_pairs)} trainable records; at least {MIN_REPLAY_RECORDS} are required."
        )
    selected_pairs = trainable_pairs[:requested_count]

    side = _replay_side(replay_identity.get("side"))
    expected_self_name = humans[0] if side == "Human" else bees[0]
    expected_enemy_name = bees[0] if side == "Human" else humans[0]
    expected_self_type = _expected_ship_type_id(expected_self_name)
    expected_enemy_type = _expected_ship_type_id(expected_enemy_name)

    observations = []
    for index, pair_info in enumerate(selected_pairs):
        observation = tuple(float(value) for value in obs_reader(pair_info, behavior_spec))
        if len(observation) != EXPECTED_OBSERVATION_SIZE:
            raise ValidationError(
                f"Replay source observation {index} has {len(observation)} values; "
                f"expected {EXPECTED_OBSERVATION_SIZE}."
            )
        if any(not math.isfinite(value) for value in observation):
            raise ValidationError(f"Replay source observation {index} contains a non-finite value.")
        self_ship_type = _decode_enum_bits(observation, SELF_SHIP_BIT_START, SHIP_TYPE_BIT_COUNT)
        if self_ship_type != expected_self_type:
            raise ValidationError(
                f"Replay source changed self ship to {_ship_name(self_ship_type)} at record {index}; "
                f"scenario {side} ship is {expected_self_name}."
            )
        observed_enemy_type = _enemy_ship_type(observation)
        if observed_enemy_type is not None and observed_enemy_type != expected_enemy_type:
            raise ValidationError(
                f"Replay source observed enemy {_ship_name(observed_enemy_type)} at record {index}; "
                f"scenario opponent is {expected_enemy_name}."
            )
        observations.append(observation)

    first_observation = observations[0]
    frames = []
    for index, pair_info in enumerate(selected_pairs):
        continuous, discrete = act_reader(pair_info)
        frames.append(_validate_action_record(continuous, discrete, index))

    source_direction = _source_start_direction(first_observation)
    payload = _binary_payload(frames, source_direction)
    artifact_sha256 = sha256_bytes(payload)
    artifact_path = _artifact_path(store, replay_id)
    metadata_path = _artifact_metadata_path(store, replay_id)
    _write_bytes_immutable(artifact_path, payload)

    metadata = {
        "schema_version": REPLAY_ARTIFACT_SCHEMA_VERSION,
        "replay_id": replay_id,
        "scenario_id": scenario_id,
        "source_batch_id": source_batch_id,
        "policy_abi_version": store.compatibility.policy_abi_version,
        "side": side,
        "mode": REPLAY_MODE,
        "terminal_behavior": REPLAY_TERMINAL_BEHAVIOR,
        "fixed_step_interval": REPLAY_FIXED_STEP_INTERVAL,
        "frame_count": len(frames),
        "requested_record_count": requested_count,
        "available_trainable_record_count": len(trainable_pairs),
        "source_first_episode_terminal_found": terminal_found,
        "truncated": len(trainable_pairs) > len(frames),
        "artifact_sha256": artifact_sha256,
        "source_start_direction": [source_direction[0], source_direction[1]],
        "self_ship_type": expected_self_name,
        "opponent_ship_type": expected_enemy_name,
    }
    metadata_payload = (
        json.dumps(metadata, indent=2, sort_keys=True, ensure_ascii=False) + "\n"
    ).encode("utf-8")
    _write_bytes_immutable(metadata_path, metadata_payload)
    return {
        **metadata,
        "artifact_path": str(artifact_path),
        "metadata_path": str(metadata_path),
    }


def _ship_name(ship_type_id: int) -> str:
    return SHIP_TYPE_NAMES.get(ship_type_id, f"ship-{ship_type_id}")


def build_replay_catalog(
    store: ContinualLearningStore,
    scenario_ids: Sequence[str],
    *,
    loader: Optional[NativeDemoLoader] = None,
    observation_reader: Optional[ObservationReader] = None,
    action_reader: Optional[ActionReader] = None,
) -> Mapping[str, object]:
    """Build an immutable Unity-ready catalog for selected scenarios that have replay attachments."""
    store._require_initialized()
    if not scenario_ids:
        raise ValidationError("Replay catalog requires at least one selected adversarial scenario.")
    normalized = sorted(set(scenario_ids))
    if len(normalized) != len(scenario_ids):
        raise ValidationError("Replay catalog scenario selection contains duplicate IDs.")

    compiled = []
    for scenario_id in normalized:
        registration = read_registered_replay_if_present(store, scenario_id)
        if registration is None:
            continue
        compiled.append(
            compile_action_replay(
                store,
                scenario_id,
                loader=loader,
                observation_reader=observation_reader,
                action_reader=action_reader,
            )
        )

    identity_entries = []
    catalog_dir = store.root / "metadata" / "adversarial-replay-catalogs"
    for item in compiled:
        artifact_path = Path(str(item["artifact_path"]))
        relative_path = os.path.relpath(artifact_path, catalog_dir).replace(os.sep, "/")
        identity_entries.append(
            {
                "scenarioId": item["scenario_id"],
                "side": item["side"],
                "replayId": item["replay_id"],
                "replayPath": relative_path,
                "replaySha256": item["artifact_sha256"],
                "frameCount": item["frame_count"],
                "fixedStepInterval": item["fixed_step_interval"],
            }
        )

    identity = {
        "schemaVersion": REPLAY_CATALOG_SCHEMA_VERSION,
        "entries": identity_entries,
    }
    identity_hash = sha256_bytes(canonical_json(identity).encode("utf-8"))
    catalog_path = catalog_dir / f"catalog-{identity_hash[:24]}.json"
    body = {
        **identity,
        "catalogSha256": identity_hash,
    }
    payload = (json.dumps(body, indent=2, sort_keys=True, ensure_ascii=False) + "\n").encode("utf-8")
    _write_bytes_immutable(catalog_path, payload)
    return {
        "catalog_path": str(catalog_path),
        "catalog_sha256": identity_hash,
        "entry_count": len(identity_entries),
        "catalog": body,
    }


def _build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Register/compile immutable scripted replays for reviewed adversarial scenarios."
    )
    parser.add_argument("--root", required=True, help="Continual-learning store root.")
    parser.add_argument(
        "--config",
        default=str(Path(__file__).with_name("continual_learning_config.json")),
        help="Continual-learning configuration JSON.",
    )
    subparsers = parser.add_subparsers(dest="command", required=True)

    register = subparsers.add_parser("register")
    register.add_argument("scenario_id")
    register.add_argument("--source-batch", required=True)
    register.add_argument("--side", required=True, choices=("Bee", "Human", "bee", "human"))
    register.add_argument("--record-count", type=int, default=DEFAULT_REPLAY_RECORDS)

    compile_parser = subparsers.add_parser("compile")
    compile_parser.add_argument("scenario_id")

    catalog = subparsers.add_parser("catalog")
    catalog.add_argument("scenario_ids", nargs="+")
    return parser


def main(argv: Optional[Sequence[str]] = None) -> int:
    args = _build_parser().parse_args(argv)
    try:
        store = ContinualLearningStore(args.root, load_config(args.config))
        if args.command == "register":
            result = register_action_replay(
                store,
                args.scenario_id,
                source_batch_id=args.source_batch,
                side=args.side,
                record_count=args.record_count,
            )
        elif args.command == "compile":
            result = compile_action_replay(store, args.scenario_id)
        else:
            result = build_replay_catalog(store, args.scenario_ids)
        print(json.dumps(result, indent=2, sort_keys=True, ensure_ascii=False))
        return 0
    except ContinualLearningError as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
