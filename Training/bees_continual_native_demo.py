"""Ingest native ML-Agents human demonstrations into the Bees continual-learning store.

This is a local/trusted ingestion boundary. It preserves the original `.demo` bytes and capture
manifest, validates them against the continual policy ABI, and registers the immutable batch in
the existing demonstration_batches table. It does not make public-client uploads trusted and it
does not treat demonstrations as PPO/on-policy trajectories.
"""

from __future__ import annotations

import argparse
import json
import os
import shutil
import sys
import tempfile
from pathlib import Path
from typing import Any, Callable, Mapping, Optional, Sequence, Tuple

from bees_continual_learning import (
    CompatibilityError,
    ContinualLearningError,
    ContinualLearningStore,
    ValidationError,
    canonical_json,
    load_config,
    sha256_bytes,
    sha256_file,
    utc_now,
)
from bees_continual_train import _load_capture_manifest


NATIVE_DEMO_ARCHIVE_SCHEMA_VERSION = 1


NativeDemoLoader = Callable[[str], Tuple[Any, Sequence[Any], int]]


def _default_native_demo_loader(path: str) -> Tuple[Any, Sequence[Any], int]:
    try:
        from mlagents.trainers.demo_loader import load_demonstration
    except ImportError as exc:
        raise ValidationError(
            "Native .demo ingestion requires the project's ML-Agents Python environment."
        ) from exc
    try:
        return load_demonstration(path)
    except Exception as exc:
        raise ValidationError(
            f"Could not parse native ML-Agents demonstration {path}: "
            f"{type(exc).__name__}: {exc}"
        ) from exc


def _required_identifier(value: object, label: str) -> str:
    if not isinstance(value, str) or not value.strip() or len(value.strip()) > 256:
        raise ValidationError(f"{label} must be a non-empty string up to 256 characters.")
    return value.strip()


def _validate_native_behavior(
    behavior_spec: Any,
    capture_manifest: Mapping[str, object],
) -> None:
    observation_specs = getattr(behavior_spec, "observation_specs", None)
    action_spec = getattr(behavior_spec, "action_spec", None)
    if observation_specs is None:
        raise CompatibilityError("Native demonstration is missing observation specifications.")
    try:
        observation_count = len(observation_specs)
    except TypeError as exc:
        raise CompatibilityError(
            "Native demonstration observation specifications are malformed."
        ) from exc
    if observation_count != 1:
        raise CompatibilityError(
            "Native demonstration must contain exactly one vector observation matching BeesRL1v1."
        )

    expected_observation_size = capture_manifest["observationSize"]
    shape = tuple(getattr(observation_specs[0], "shape", ()))
    if shape != (expected_observation_size,):
        raise CompatibilityError(
            f"Native demonstration observation shape {shape!r} does not match "
            f"capture manifest ({expected_observation_size},)."
        )

    if action_spec is None:
        raise CompatibilityError("Native demonstration is missing an action specification.")
    continuous_size = getattr(action_spec, "continuous_size", None)
    if continuous_size != capture_manifest["continuousActionCount"]:
        raise CompatibilityError(
            f"Native demonstration continuous action count {continuous_size!r} does not match "
            f"capture manifest {capture_manifest['continuousActionCount']!r}."
        )

    discrete_branches = getattr(action_spec, "discrete_branches", None)
    try:
        actual_branches = [int(value) for value in discrete_branches]
    except (TypeError, ValueError):
        actual_branches = None
    expected_branches = list(capture_manifest["discreteBranchSizes"])
    if actual_branches != expected_branches:
        raise CompatibilityError(
            f"Native demonstration discrete branches {actual_branches!r} do not match "
            f"capture manifest {expected_branches!r}."
        )


def _copy_immutable(source: Path, destination: Path, expected_sha256: str) -> bool:
    """Copy source atomically. Return True only when this call created destination."""
    destination.parent.mkdir(parents=True, exist_ok=True)
    if destination.exists():
        if sha256_file(destination) != expected_sha256:
            raise ContinualLearningError(
                f"Refusing to overwrite conflicting immutable demonstration artifact: {destination}"
            )
        return False

    fd, temp_name = tempfile.mkstemp(
        prefix=destination.name + ".",
        suffix=".tmp",
        dir=str(destination.parent),
    )
    os.close(fd)
    temp = Path(temp_name)
    try:
        shutil.copy2(source, temp)
        if sha256_file(temp) != expected_sha256:
            raise ValidationError(f"Demonstration source changed while being ingested: {source}")
        os.replace(temp, destination)
        return True
    finally:
        if temp.exists():
            temp.unlink()


def _write_bytes_immutable(path: Path, payload: bytes) -> bool:
    """Write bytes atomically. Return True only when this call created path."""
    path.parent.mkdir(parents=True, exist_ok=True)
    if path.exists():
        if path.read_bytes() != payload:
            raise ContinualLearningError(f"Refusing to overwrite immutable record: {path}")
        return False

    fd, temp_name = tempfile.mkstemp(
        prefix=path.name + ".",
        suffix=".tmp",
        dir=str(path.parent),
    )
    try:
        with os.fdopen(fd, "wb") as handle:
            handle.write(payload)
            handle.flush()
            os.fsync(handle.fileno())
        os.replace(temp_name, path)
        return True
    finally:
        try:
            os.unlink(temp_name)
        except FileNotFoundError:
            pass


def ingest_native_demonstration(
    store: ContinualLearningStore,
    demo_path: str | os.PathLike[str],
    *,
    demonstration_id: str,
    model_id: str,
    game_build_version: str,
    loader: Optional[NativeDemoLoader] = None,
) -> Mapping[str, object]:
    """Validate and archive one Human native `.demo` batch.

    `model_id` identifies the compatible deployed policy context associated with the captured match;
    it does not imply that the human action was generated by that model.
    """
    store._require_initialized()
    demonstration_id = _required_identifier(demonstration_id, "demonstration_id")
    model_id = _required_identifier(model_id, "model_id")
    game_build_version = _required_identifier(game_build_version, "game_build_version")

    source = Path(demo_path).expanduser().resolve()
    if not source.is_file() or source.suffix.lower() != ".demo":
        raise ValidationError(f"Native demonstration must be an existing .demo file: {source}")
    if source.stat().st_size <= 0:
        raise ValidationError(f"Native demonstration is empty: {source}")
    if source.name.lower().startswith("hivemind-"):
        raise ValidationError(
            f"Refusing to ingest Hive Mind demonstration as human data: {source}"
        )

    try:
        capture_manifest_path, capture_manifest, capture_manifest_hash = _load_capture_manifest(
            source.parent,
            store.config,
        )
    except SystemExit as exc:
        message = str(exc) or "Native demonstration capture manifest is invalid."
        raise ValidationError(message) from exc

    total_source_bytes = source.stat().st_size + capture_manifest_path.stat().st_size
    if total_source_bytes > int(store.config["ingestion"]["max_payload_bytes"]):
        raise ValidationError(
            "Native demonstration plus capture manifest exceeds configured maximum size."
        )

    native_loader = loader or _default_native_demo_loader
    behavior_spec, pair_infos, total_expected = native_loader(str(source))
    try:
        parsed_count = len(pair_infos)
    except TypeError as exc:
        raise ValidationError("Native demonstration parser returned a non-sized experience set.") from exc
    if (
        not isinstance(total_expected, int)
        or isinstance(total_expected, bool)
        or total_expected != parsed_count
    ):
        raise ValidationError(
            f"Native demonstration metadata expected {total_expected!r} records but parsed "
            f"{parsed_count}."
        )
    if parsed_count < 2:
        raise ValidationError(
            "Native demonstration contains fewer than two records and therefore no trainable transition."
        )
    if parsed_count > int(store.config["ingestion"]["max_steps_per_match"]):
        raise ValidationError(
            "Native demonstration record count exceeds configured maximum."
        )
    _validate_native_behavior(behavior_spec, capture_manifest)
    trainable_example_count = parsed_count - 1

    demo_hash = sha256_file(source)
    envelope = {
        "schema_version": NATIVE_DEMO_ARCHIVE_SCHEMA_VERSION,
        "demonstration_id": demonstration_id,
        "model_id": model_id,
        "game_build_version": game_build_version,
        "behavior_name": store.compatibility.behavior_name,
        "policy_abi_version": store.compatibility.policy_abi_version,
        "observation_schema_version": store.compatibility.observation_schema_version,
        "action_schema_version": store.compatibility.action_schema_version,
        "reward_schema_version": store.compatibility.reward_schema_version,
        "scenario_schema_version": store.compatibility.scenario_schema_version,
        "native_demo": {
            "source_name": source.name,
            "sha256": demo_hash,
            "record_count": parsed_count,
            "trainable_example_count": trainable_example_count,
        },
        "capture_manifest": {
            "sha256": capture_manifest_hash,
            "metadata": capture_manifest,
        },
    }
    payload_hash = sha256_bytes(canonical_json(envelope).encode("utf-8"))
    batch_id = f"demo-{payload_hash[:24]}"
    archive_dir = store.experience_dir / "human-demos" / "native"
    demo_archive = archive_dir / f"{batch_id}.demo"
    capture_archive = archive_dir / f"{batch_id}.capture-manifest.json"
    metadata_archive = archive_dir / f"{batch_id}.json"
    metadata_payload = (
        json.dumps(envelope, indent=2, sort_keys=True, ensure_ascii=False) + "\n"
    ).encode("utf-8")

    created_paths = []
    with store._connect() as db:
        db.execute("BEGIN IMMEDIATE")
        model = store._model_row(db, model_id)
        store._assert_model_compatible(model)
        duplicate = db.execute(
            """
            SELECT * FROM demonstration_batches
            WHERE demonstration_id = ? OR payload_sha256 = ?
            """,
            (demonstration_id, payload_hash),
        ).fetchone()
        if duplicate is not None:
            if (
                duplicate["demonstration_id"] == demonstration_id
                and duplicate["payload_sha256"] != payload_hash
            ):
                raise ValidationError(
                    f"demonstration_id {demonstration_id!r} was already ingested with different content."
                )
            return {
                "batch_id": duplicate["batch_id"],
                "demonstration_id": duplicate["demonstration_id"],
                "duplicate": True,
                "example_count": duplicate["example_count"],
                "archive_path": duplicate["archive_path"],
            }

        try:
            if _copy_immutable(source, demo_archive, demo_hash):
                created_paths.append(demo_archive)
            if _copy_immutable(
                capture_manifest_path,
                capture_archive,
                capture_manifest_hash,
            ):
                created_paths.append(capture_archive)
            if _write_bytes_immutable(metadata_archive, metadata_payload):
                created_paths.append(metadata_archive)

            # Re-check both input files after archival to fail closed if an active recorder changed
            # either source during ingestion.
            if sha256_file(source) != demo_hash:
                raise ValidationError(
                    f"Native demonstration changed while being ingested: {source}"
                )
            if sha256_file(capture_manifest_path) != capture_manifest_hash:
                raise ValidationError(
                    f"Capture manifest changed while being ingested: {capture_manifest_path}"
                )

            db.execute(
                """
                INSERT INTO demonstration_batches(
                    batch_id, demonstration_id, model_id, created_at,
                    payload_sha256, archive_path, example_count
                ) VALUES(?,?,?,?,?,?,?)
                """,
                (
                    batch_id,
                    demonstration_id,
                    model_id,
                    utc_now(),
                    payload_hash,
                    str(demo_archive),
                    trainable_example_count,
                ),
            )
        except Exception:
            for path in reversed(created_paths):
                try:
                    path.unlink()
                except FileNotFoundError:
                    pass
            raise

    return {
        "batch_id": batch_id,
        "demonstration_id": demonstration_id,
        "duplicate": False,
        "example_count": trainable_example_count,
        "archive_path": str(demo_archive),
        "capture_manifest_path": str(capture_archive),
        "metadata_path": str(metadata_archive),
    }


def _build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Validate/archive a native ML-Agents Human demonstration."
    )
    parser.add_argument("--root", required=True, help="Continual-learning store root.")
    parser.add_argument(
        "--config",
        default=str(Path(__file__).with_name("continual_learning_config.json")),
        help="Continual-learning configuration JSON.",
    )
    parser.add_argument("demo", help="Path to PolicyV<ABI>/Human/*.demo.")
    parser.add_argument("--demonstration-id", required=True)
    parser.add_argument("--model-id", required=True)
    parser.add_argument("--game-build", required=True)
    return parser


def main(argv: Optional[Sequence[str]] = None) -> int:
    args = _build_parser().parse_args(argv)
    try:
        store = ContinualLearningStore(args.root, load_config(args.config))
        result = ingest_native_demonstration(
            store,
            args.demo,
            demonstration_id=args.demonstration_id,
            model_id=args.model_id,
            game_build_version=args.game_build,
        )
        print(json.dumps(result, indent=2, sort_keys=True, ensure_ascii=False))
        return 0
    except ContinualLearningError as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
