"""Automatic public live-telemetry ingestion, review and training-pressure generation.

The automation preserves the existing trust boundaries:

* BeesServer authentication/quarantine remains the first boundary.
* Every quarantine pair is independently revalidated by the central store.
* Recorded live actions are never inserted into PPO as on-policy trajectories.
* Only repeated tactics supported by distinct matches and privacy-safe contributors are converted
  into bounded scenario pressure.
* Because public agent_key values intentionally do not assert Bee/Human orientation, automatic
  pressure registers both orientations at half weight rather than trusting a client-side side claim.

Invalid, incompatible or suspicious uploads remain quarantined and are not approved automatically.
"""

from __future__ import annotations

import argparse
import json
import os
import sys
import tempfile
import time
from pathlib import Path
from typing import Dict, List, Mapping, Optional, Sequence, Tuple

from bees_continual_learning import (
    ContinualLearningError,
    ContinualLearningStore,
    ValidationError,
    canonical_json,
    load_config,
    sha256_bytes,
    utc_now,
)
from bees_continual_public_telemetry import ingest_public_telemetry_quarantine
from bees_continual_telemetry_contributors import load_public_telemetry_contributor_buckets
from bees_continual_telemetry_curation import (
    approve_public_telemetry,
    materialize_scenario_selection,
)
from bees_continual_telemetry_mine import mine_telemetry_selection
from bees_continual_telemetry_review import review_and_register_telemetry_tactic


AUTOMATION_SCHEMA_VERSION = 1
AUTOMATIC_REVIEWER = "automatic-policy-v1"
AUTOMATIC_TAG = "automatic-policy-v1"
DEFAULT_MAX_SELECTION_BATCHES = 128
DEFAULT_MINIMUM_OCCURRENCES = 2
DEFAULT_MINIMUM_CONTRIBUTORS = 2
DEFAULT_TOTAL_TARGET_FRACTION = 0.10
DEFAULT_WATCH_SECONDS = 30.0


def _positive_int(value: object, label: str) -> int:
    if isinstance(value, bool):
        raise ValidationError(f"{label} must be a positive integer.")
    try:
        parsed = int(value)
    except (TypeError, ValueError) as exc:
        raise ValidationError(f"{label} must be a positive integer.") from exc
    if parsed <= 0:
        raise ValidationError(f"{label} must be a positive integer.")
    return parsed


def _target_fraction(value: object) -> float:
    try:
        parsed = float(value)
    except (TypeError, ValueError) as exc:
        raise ValidationError("automatic public target fraction must be a finite number in (0,0.5].") from exc
    if not 0.0 < parsed <= 0.5:
        raise ValidationError("automatic public target fraction must be in (0,0.5].")
    return parsed


def incoming_metadata_paths(quarantine_root: str | os.PathLike[str]) -> Tuple[Path, ...]:
    root = Path(quarantine_root).expanduser().resolve()
    incoming = root / "incoming"
    if not incoming.is_dir():
        return ()
    return tuple(sorted(path for path in incoming.glob("*.metadata.json") if path.is_file()))


def _approval_path(store: ContinualLearningStore, batch_id: str) -> Path:
    return store.experience_dir / "raw-live" / "public-approvals" / f"{batch_id}.json"


def _revocation_path(store: ContinualLearningStore, batch_id: str) -> Path:
    return store.experience_dir / "raw-live" / "public-revocations" / f"{batch_id}.json"


def _selection_batch_ids(
    store: ContinualLearningStore,
    *,
    maximum_batches: int,
) -> Tuple[str, ...]:
    maximum_batches = _positive_int(maximum_batches, "maximum_batches")
    settings = store.config.get("public_live_telemetry")
    if not isinstance(settings, Mapping):
        raise ValidationError("public_live_telemetry configuration must be an object.")
    max_per_contributor = _positive_int(
        settings.get("max_batches_per_contributor"),
        "public_live_telemetry.max_batches_per_contributor",
    )

    with store._connect() as db:
        rows = db.execute(
            "SELECT batch_id, created_at FROM telemetry_batches ORDER BY created_at DESC, batch_id DESC"
        ).fetchall()

    selected: List[str] = []
    contributor_counts: Dict[str, int] = {}
    for row in rows:
        batch_id = str(row["batch_id"])
        if not _approval_path(store, batch_id).is_file() or _revocation_path(store, batch_id).exists():
            continue
        try:
            buckets = tuple(load_public_telemetry_contributor_buckets(store, batch_id))
        except ContinualLearningError:
            continue
        proposed = dict(contributor_counts)
        allowed = True
        for bucket in buckets:
            proposed[bucket] = proposed.get(bucket, 0) + 1
            if proposed[bucket] > max_per_contributor:
                allowed = False
                break
        if not allowed:
            continue
        contributor_counts = proposed
        selected.append(batch_id)
        if len(selected) >= maximum_batches:
            break
    return tuple(sorted(selected))


def _automatic_reason() -> str:
    return (
        "Passed authenticated server quarantine plus strict central ABI/model/deployment/hash/schema "
        "validation; approved automatically for offline tactic mining only."
    )


def _state_root(store: ContinualLearningStore) -> Path:
    return store.root / "metadata" / "automatic-public-learning"


def _write_atomic_json(path: Path, value: Mapping[str, object]) -> None:
    payload = (json.dumps(value, indent=2, sort_keys=True, ensure_ascii=False) + "\n").encode("utf-8")
    path.parent.mkdir(parents=True, exist_ok=True)
    fd, temp_name = tempfile.mkstemp(prefix=path.name + ".", suffix=".tmp", dir=str(path.parent))
    try:
        with os.fdopen(fd, "wb") as handle:
            handle.write(payload)
            handle.flush()
            os.fsync(handle.fileno())
        os.replace(temp_name, path)
    finally:
        try:
            os.unlink(temp_name)
        except FileNotFoundError:
            pass


def _publish_state(
    store: ContinualLearningStore,
    *,
    selection_id: Optional[str],
    scenario_ids: Sequence[str],
    source_batches: Sequence[str],
) -> Mapping[str, object]:
    identity = {
        "schema_version": AUTOMATION_SCHEMA_VERSION,
        "selection_id": selection_id,
        "scenario_ids": sorted(set(scenario_ids)),
        "source_batches": sorted(set(source_batches)),
    }
    identity_hash = sha256_bytes(canonical_json(identity).encode("utf-8"))
    generation_id = f"auto-public-{identity_hash[:24]}"
    body = {
        **identity,
        "generation_id": generation_id,
        "identity_sha256": identity_hash,
        "updated_at": utc_now(),
    }
    root = _state_root(store)
    generation_path = root / "generations" / f"{generation_id}.json"
    if generation_path.exists():
        existing = json.loads(generation_path.read_text(encoding="utf-8"))
        comparable = dict(existing)
        comparable.pop("updated_at", None)
        expected = dict(body)
        expected.pop("updated_at", None)
        if comparable != expected:
            raise ContinualLearningError(
                f"Automatic public-learning generation identity conflict: {generation_path}"
            )
    else:
        ContinualLearningStore._write_json_immutable(generation_path, body)
    _write_atomic_json(root / "current.json", body)
    return body


def load_current_state(store: ContinualLearningStore) -> Mapping[str, object]:
    path = _state_root(store) / "current.json"
    if not path.is_file():
        return {
            "schema_version": AUTOMATION_SCHEMA_VERSION,
            "selection_id": None,
            "scenario_ids": [],
            "source_batches": [],
        }
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except json.JSONDecodeError as exc:
        raise ValidationError(f"Automatic public-learning state is invalid JSON: {path}: {exc}") from exc
    if not isinstance(value, dict) or value.get("schema_version") != AUTOMATION_SCHEMA_VERSION:
        raise ValidationError(f"Automatic public-learning state is incompatible: {path}")
    if not isinstance(value.get("scenario_ids"), list) or not isinstance(value.get("source_batches"), list):
        raise ValidationError(f"Automatic public-learning state is malformed: {path}")
    return value


def process_public_learning_once(
    store: ContinualLearningStore,
    quarantine_root: str | os.PathLike[str],
    *,
    maximum_selection_batches: int = DEFAULT_MAX_SELECTION_BATCHES,
    minimum_occurrences: int = DEFAULT_MINIMUM_OCCURRENCES,
    minimum_contributors: int = DEFAULT_MINIMUM_CONTRIBUTORS,
    total_target_fraction: float = DEFAULT_TOTAL_TARGET_FRACTION,
) -> Mapping[str, object]:
    """Process all currently visible quarantine telemetry and publish current training pressure."""
    store._require_initialized()
    minimum_occurrences = _positive_int(minimum_occurrences, "minimum_occurrences")
    minimum_contributors = _positive_int(minimum_contributors, "minimum_contributors")
    total_target_fraction = _target_fraction(total_target_fraction)

    imported: List[str] = []
    rejected: List[Mapping[str, str]] = []
    for metadata_path in incoming_metadata_paths(quarantine_root):
        try:
            result = ingest_public_telemetry_quarantine(store, metadata_path)
            archive = result.get("archive")
            if not isinstance(archive, Mapping):
                raise ContinualLearningError("Public telemetry ingestion returned malformed archive metadata.")
            batch_id = str(archive["batch_id"])
            approve_public_telemetry(
                store,
                batch_id,
                reviewer=AUTOMATIC_REVIEWER,
                reason=_automatic_reason(),
                tags=(AUTOMATIC_TAG,),
            )
            imported.append(batch_id)
        except (ContinualLearningError, OSError, ValueError) as exc:
            rejected.append({"metadata": str(metadata_path), "error": f"{type(exc).__name__}: {exc}"})

    selected = _selection_batch_ids(store, maximum_batches=maximum_selection_batches)
    if len(selected) < minimum_occurrences:
        state = _publish_state(
            store,
            selection_id=None,
            scenario_ids=(),
            source_batches=selected,
        )
        return {
            "imported_or_existing": sorted(set(imported)),
            "rejected": rejected,
            "selected_batches": list(selected),
            "suggestions": 0,
            "scenario_ids": [],
            "state": state,
        }

    selection = materialize_scenario_selection(store, selected)
    selection_id = str(selection["selection_id"])
    report = mine_telemetry_selection(
        store,
        selection_id,
        minimum_occurrences=minimum_occurrences,
        minimum_contributors=minimum_contributors,
    )
    suggestions = report.get("suggestions")
    if not isinstance(suggestions, list):
        raise ContinualLearningError("Automatic telemetry mining returned a malformed suggestion list.")

    valid_suggestions = [item for item in suggestions if isinstance(item, Mapping)]
    per_orientation_fraction = (
        total_target_fraction / (2.0 * len(valid_suggestions))
        if valid_suggestions
        else 0.0
    )
    scenario_ids: List[str] = []
    registration_errors: List[Mapping[str, str]] = []
    for suggestion in valid_suggestions:
        signature = suggestion.get("signature")
        if not isinstance(signature, str) or not signature:
            continue
        for self_side in ("bee", "human"):
            try:
                reviewed = review_and_register_telemetry_tactic(
                    store,
                    selection_id,
                    signature,
                    self_side=self_side,
                    target_fraction=per_orientation_fraction,
                    rationale=(
                        "Automatic cross-contributor public tactic pressure; both side orientations "
                        "are registered because public agent identity is intentionally opaque."
                    ),
                    minimum_occurrences=minimum_occurrences,
                )
                registration = reviewed.get("registration")
                if isinstance(registration, Mapping):
                    scenario_id = registration.get("scenario_id")
                    if isinstance(scenario_id, str):
                        scenario_ids.append(scenario_id)
            except ContinualLearningError as exc:
                registration_errors.append(
                    {
                        "signature": signature,
                        "self_side": self_side,
                        "error": f"{type(exc).__name__}: {exc}",
                    }
                )

    state = _publish_state(
        store,
        selection_id=selection_id,
        scenario_ids=scenario_ids,
        source_batches=selected,
    )
    return {
        "imported_or_existing": sorted(set(imported)),
        "rejected": rejected,
        "selected_batches": list(selected),
        "selection_id": selection_id,
        "suggestions": len(valid_suggestions),
        "scenario_ids": sorted(set(scenario_ids)),
        "registration_errors": registration_errors,
        "state": state,
    }


def _parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Automatically validate public telemetry and convert repeated tactics to fresh PPO pressure."
    )
    parser.add_argument("--root", required=True, help="Continual-learning store root.")
    parser.add_argument("--config", default=None)
    parser.add_argument(
        "--telemetry-quarantine",
        required=True,
        help="BeesServer BEES_RL_TELEMETRY_UPLOAD_DIR root containing incoming/.",
    )
    parser.add_argument("--max-selection-batches", type=int, default=DEFAULT_MAX_SELECTION_BATCHES)
    parser.add_argument("--minimum-occurrences", type=int, default=DEFAULT_MINIMUM_OCCURRENCES)
    parser.add_argument("--minimum-contributors", type=int, default=DEFAULT_MINIMUM_CONTRIBUTORS)
    parser.add_argument("--total-target-fraction", type=float, default=DEFAULT_TOTAL_TARGET_FRACTION)
    parser.add_argument(
        "--watch-seconds",
        type=float,
        default=0.0,
        help="0 processes once; positive values keep scanning at this interval.",
    )
    return parser


def main(argv: Optional[Sequence[str]] = None) -> int:
    args = _parser().parse_args(argv)
    if args.watch_seconds < 0:
        print("error: --watch-seconds must be non-negative", file=sys.stderr)
        return 2
    try:
        store = ContinualLearningStore(args.root, load_config(args.config))
        store.initialize()
        while True:
            result = process_public_learning_once(
                store,
                args.telemetry_quarantine,
                maximum_selection_batches=args.max_selection_batches,
                minimum_occurrences=args.minimum_occurrences,
                minimum_contributors=args.minimum_contributors,
                total_target_fraction=args.total_target_fraction,
            )
            print(json.dumps(result, indent=2, sort_keys=True, ensure_ascii=False))
            if args.watch_seconds <= 0:
                return 0
            time.sleep(args.watch_seconds)
    except KeyboardInterrupt:
        return 0
    except (ContinualLearningError, OSError, ValueError) as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
