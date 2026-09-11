"""Register approved player-derived matchup pressure for fresh headless PPO rollouts.

This module deliberately does not replay old player trajectories as PPO experience. An operator
identifies a useful tactic in one or more explicitly approved public Human demonstration batches,
registers the corresponding Bee/Human fleet matchup, and receives an immutable scenario ID. The
scenario can then be encoded into the dedicated Unity training process, where it requests a bounded
fraction of fresh on-policy episodes while normal sampled/adaptive training remains the majority.
"""

from __future__ import annotations

import argparse
import json
import math
import re
import sys
from pathlib import Path
from typing import Mapping, Optional, Sequence

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
from bees_continual_native_demo import _write_bytes_immutable


ADVERSARIAL_SCENARIO_SCHEMA_VERSION = 1
MAX_SCENARIO_SOURCE_BATCHES = 64
MAX_SCENARIO_SHIPS_PER_SIDE = 16
MAX_TARGET_FRACTION_PER_SCENARIO = 0.5
MAX_TOTAL_TARGET_FRACTION = 0.5
_SCENARIO_ID = re.compile(r"^adv-[0-9a-f]{24}$")
_BATCH_ID = re.compile(r"^demo-[0-9a-f]{24}$")
_SHIP_NAME = re.compile(r"^[A-Za-z][A-Za-z0-9]*$")


def _required_text(value: object, label: str, maximum: int) -> str:
    if not isinstance(value, str) or not value.strip() or len(value.strip()) > maximum:
        raise ValidationError(f"{label} must be a non-empty string up to {maximum} characters.")
    return value.strip()


def _parse_composition(value: object, label: str) -> Sequence[str]:
    if isinstance(value, str):
        values = [token.strip() for token in value.split(",")]
    elif isinstance(value, (list, tuple)):
        values = [str(token).strip() for token in value]
    else:
        raise ValidationError(f"{label} must be a comma-separated ship composition.")
    if not values or len(values) > MAX_SCENARIO_SHIPS_PER_SIDE:
        raise ValidationError(
            f"{label} must contain between 1 and {MAX_SCENARIO_SHIPS_PER_SIDE} ship names."
        )
    if any(not token or not _SHIP_NAME.fullmatch(token) for token in values):
        raise ValidationError(f"{label} contains an invalid ship-type token.")
    return tuple(values)


def _target_fraction(value: object) -> float:
    if isinstance(value, bool):
        raise ValidationError(
            f"target_fraction must be a finite number in (0,{MAX_TARGET_FRACTION_PER_SCENARIO:g}]."
        )
    try:
        parsed = float(value)
    except (TypeError, ValueError) as exc:
        raise ValidationError(
            f"target_fraction must be a finite number in (0,{MAX_TARGET_FRACTION_PER_SCENARIO:g}]."
        ) from exc
    if (
        not math.isfinite(parsed)
        or parsed <= 0.0
        or parsed > MAX_TARGET_FRACTION_PER_SCENARIO
    ):
        raise ValidationError(
            f"target_fraction must be a finite number in (0,{MAX_TARGET_FRACTION_PER_SCENARIO:g}]."
        )
    return parsed


def _scenario_path(store: ContinualLearningStore, scenario_id: str) -> Path:
    return store.experience_dir / "adversarial-scenarios" / f"{scenario_id}.json"


def _read_scenario(store: ContinualLearningStore, scenario_id: str) -> Mapping[str, object]:
    if not isinstance(scenario_id, str) or not _SCENARIO_ID.fullmatch(scenario_id):
        raise ValidationError(f"Invalid adversarial scenario ID: {scenario_id!r}.")
    path = _scenario_path(store, scenario_id)
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except FileNotFoundError as exc:
        raise ValidationError(f"Adversarial scenario does not exist: {scenario_id}") from exc
    except json.JSONDecodeError as exc:
        raise ValidationError(f"Adversarial scenario is invalid JSON: {path}: {exc}") from exc
    if not isinstance(value, dict):
        raise ValidationError(f"Adversarial scenario must contain a JSON object: {path}")
    if value.get("schema_version") != ADVERSARIAL_SCENARIO_SCHEMA_VERSION:
        raise ValidationError(f"Adversarial scenario schema is incompatible: {scenario_id}")
    if value.get("scenario_id") != scenario_id:
        raise ValidationError(f"Adversarial scenario identity mismatch: {scenario_id}")
    identity = value.get("identity")
    identity_sha256 = value.get("identity_sha256")
    if not isinstance(identity, dict) or not isinstance(identity_sha256, str):
        raise ValidationError(f"Adversarial scenario identity is incomplete: {scenario_id}")
    expected_hash = sha256_bytes(canonical_json(identity).encode("utf-8"))
    if identity_sha256 != expected_hash or scenario_id != f"adv-{expected_hash[:24]}":
        raise ValidationError(f"Adversarial scenario identity hash mismatch: {scenario_id}")
    if identity.get("policy_abi_version") != store.compatibility.policy_abi_version:
        raise ValidationError(
            f"Adversarial scenario {scenario_id} targets policy ABI "
            f"{identity.get('policy_abi_version')!r}, not current ABI {store.compatibility.policy_abi_version}."
        )
    return value


def _validate_registered_sources(
    store: ContinualLearningStore,
    scenario_id: str,
    identity: Mapping[str, object],
) -> None:
    sources = identity.get("sources")
    if not isinstance(sources, list) or not sources or len(sources) > MAX_SCENARIO_SOURCE_BATCHES:
        raise ValidationError(f"Adversarial scenario {scenario_id} has invalid source provenance.")

    seen = set()
    for source in sources:
        if not isinstance(source, dict):
            raise ValidationError(f"Adversarial scenario {scenario_id} has invalid source provenance.")
        batch_id = source.get("batch_id")
        if not isinstance(batch_id, str) or not _BATCH_ID.fullmatch(batch_id) or batch_id in seen:
            raise ValidationError(f"Adversarial scenario {scenario_id} has invalid source batch identity.")
        seen.add(batch_id)

        # _approved_archive rechecks current approval/revocation state plus immutable archive hashes.
        # A later revocation therefore disables every scenario derived from that batch at launch time.
        archive = _approved_archive(store, batch_id)
        approval_hash = sha256_bytes(archive["approval_path"].read_bytes())
        envelope = archive["envelope"]
        expected = {
            "payload_sha256": archive["row"]["payload_sha256"],
            "demo_sha256": archive["demo_sha256"],
            "approval_sha256": approval_hash,
            "model_id": envelope.get("model_id"),
            "game_build_version": envelope.get("game_build_version"),
        }
        for key, expected_value in expected.items():
            if source.get(key) != expected_value:
                raise ValidationError(
                    f"Adversarial scenario {scenario_id} source {batch_id} no longer matches "
                    f"its approved immutable archive ({key})."
                )


def register_player_derived_scenario(
    store: ContinualLearningStore,
    source_batch_ids: Sequence[str],
    *,
    bee_composition: Sequence[str] | str,
    human_composition: Sequence[str] | str,
    target_fraction: float,
    rationale: str,
) -> Mapping[str, object]:
    """Register one immutable matchup-pressure scenario from explicitly approved public demos."""
    store._require_initialized()
    if not source_batch_ids or len(source_batch_ids) > MAX_SCENARIO_SOURCE_BATCHES:
        raise ValidationError(
            f"source_batch_ids must contain between 1 and {MAX_SCENARIO_SOURCE_BATCHES} batches."
        )
    normalized_batches = sorted(set(source_batch_ids))
    if len(normalized_batches) != len(source_batch_ids):
        raise ValidationError("source_batch_ids contains duplicate demonstration batches.")
    for batch_id in normalized_batches:
        if not isinstance(batch_id, str) or not _BATCH_ID.fullmatch(batch_id):
            raise ValidationError(f"Invalid approved public demonstration batch ID: {batch_id!r}.")

    bees = _parse_composition(bee_composition, "bee_composition")
    humans = _parse_composition(human_composition, "human_composition")
    if len(bees) != len(humans):
        raise ValidationError("Bee and Human adversarial compositions must contain the same ship count.")
    fraction = _target_fraction(target_fraction)
    rationale = _required_text(rationale, "rationale", 2048)

    sources = []
    for batch_id in normalized_batches:
        archive = _approved_archive(store, batch_id)
        approval_bytes = archive["approval_path"].read_bytes()
        envelope = archive["envelope"]
        sources.append(
            {
                "batch_id": batch_id,
                "payload_sha256": archive["row"]["payload_sha256"],
                "demo_sha256": archive["demo_sha256"],
                "approval_sha256": sha256_bytes(approval_bytes),
                "model_id": envelope.get("model_id"),
                "game_build_version": envelope.get("game_build_version"),
            }
        )

    identity = {
        "schema_version": ADVERSARIAL_SCENARIO_SCHEMA_VERSION,
        "source": "approved-public-human-demonstrations",
        "policy_abi_version": store.compatibility.policy_abi_version,
        "bee_composition": list(bees),
        "human_composition": list(humans),
        "target_fraction": fraction,
        "rationale": rationale,
        "sources": sources,
    }
    identity_hash = sha256_bytes(canonical_json(identity).encode("utf-8"))
    scenario_id = f"adv-{identity_hash[:24]}"
    body = {
        "schema_version": ADVERSARIAL_SCENARIO_SCHEMA_VERSION,
        "scenario_id": scenario_id,
        "identity_sha256": identity_hash,
        "identity": identity,
        "created_at": utc_now(),
    }
    path = _scenario_path(store, scenario_id)
    if path.exists():
        existing = _read_scenario(store, scenario_id)
        return {
            "scenario_id": scenario_id,
            "duplicate": True,
            "path": str(path),
            "scenario": existing,
        }
    payload = (json.dumps(body, indent=2, sort_keys=True, ensure_ascii=False) + "\n").encode("utf-8")
    _write_bytes_immutable(path, payload)
    return {
        "scenario_id": scenario_id,
        "duplicate": False,
        "path": str(path),
        "scenario": body,
    }


def encode_scenarios_for_unity(
    store: ContinualLearningStore,
    scenario_ids: Sequence[str],
) -> str:
    """Encode selected immutable scenarios for --bees-adversarial-matchups."""
    store._require_initialized()
    if not scenario_ids:
        raise ValidationError("At least one adversarial scenario ID must be selected.")
    normalized = sorted(set(scenario_ids))
    if len(normalized) != len(scenario_ids):
        raise ValidationError("Adversarial scenario selection contains duplicate IDs.")

    encoded = []
    expected_ship_count = None
    total_fraction = 0.0
    for scenario_id in normalized:
        scenario = _read_scenario(store, scenario_id)
        identity = scenario["identity"]
        _validate_registered_sources(store, scenario_id, identity)
        bees = _parse_composition(identity.get("bee_composition"), "bee_composition")
        humans = _parse_composition(identity.get("human_composition"), "human_composition")
        if len(bees) != len(humans):
            raise ValidationError(f"Adversarial scenario has asymmetric team sizes: {scenario_id}")
        if expected_ship_count is None:
            expected_ship_count = len(bees)
        elif len(bees) != expected_ship_count:
            raise ValidationError(
                "Selected adversarial scenarios must use one common ships-per-side value."
            )
        fraction = _target_fraction(identity.get("target_fraction"))
        total_fraction += fraction
        encoded.append(
            f"{scenario_id}:{','.join(bees)}>{','.join(humans)}@{fraction:g}"
        )
    if total_fraction > MAX_TOTAL_TARGET_FRACTION + 1e-12:
        raise ValidationError(
            f"Selected adversarial scenarios request {total_fraction:.3f} of episodes; "
            f"the combined maximum is {MAX_TOTAL_TARGET_FRACTION:.3f}."
        )
    return ";".join(encoded)


def _build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Register/encode approved player-derived adversarial matchup pressure."
    )
    parser.add_argument("--root", required=True, help="Continual-learning store root.")
    parser.add_argument(
        "--config",
        default=str(Path(__file__).with_name("continual_learning_config.json")),
        help="Continual-learning configuration JSON.",
    )
    subparsers = parser.add_subparsers(dest="command", required=True)

    register = subparsers.add_parser("register", help="Register one player-derived matchup scenario.")
    register.add_argument("batch_ids", nargs="+")
    register.add_argument("--bee-composition", required=True)
    register.add_argument("--human-composition", required=True)
    register.add_argument("--target-fraction", required=True, type=float)
    register.add_argument("--rationale", required=True)

    encode = subparsers.add_parser("encode", help="Encode selected scenarios for Unity env args.")
    encode.add_argument("scenario_ids", nargs="+")
    return parser


def main(argv: Optional[Sequence[str]] = None) -> int:
    args = _build_parser().parse_args(argv)
    try:
        store = ContinualLearningStore(args.root, load_config(args.config))
        if args.command == "register":
            result = register_player_derived_scenario(
                store,
                args.batch_ids,
                bee_composition=args.bee_composition,
                human_composition=args.human_composition,
                target_fraction=args.target_fraction,
                rationale=args.rationale,
            )
            print(json.dumps(result, indent=2, sort_keys=True, ensure_ascii=False))
        else:
            print(encode_scenarios_for_unity(store, args.scenario_ids))
        return 0
    except ContinualLearningError as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
