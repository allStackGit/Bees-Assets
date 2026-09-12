"""Install the verified current champion into Unity Assets for a player build.

ML-Agents consumes an imported Unity Inference Engine ModelAsset, not arbitrary external ONNX bytes.
This build-time bridge publishes/verifies the continual registry's current champion package and then
atomically copies its exact ONNX plus immutable deployment manifest into Resources/RlPolicy. Unity's
normal asset importer turns the ONNX into the ModelAsset used by RlLivePolicyModelBootstrap.

The installed files are build staging artifacts and are intentionally not the deployment authority;
the content-addressed continual-learning package remains authoritative.
"""

from __future__ import annotations

import argparse
import json
import os
import shutil
import sys
import tempfile
from pathlib import Path
from typing import Mapping, Optional, Sequence

from bees_continual_deployment import publish_current_champion
from bees_continual_learning import (
    ContinualLearningError,
    ContinualLearningStore,
    ValidationError,
    load_config,
    sha256_file,
)


RESOURCE_DIRECTORY = Path("Resources") / "RlPolicy"
RESOURCE_MODEL_FILE = "BeesRL1v1.onnx"
RESOURCE_MANIFEST_FILE = "BeesRL1v1Deployment.json"
PROJECT_SENTINEL = Path("Scripts") / "Scenes" / "RlPolicySchema.cs"


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


def _replace_file_atomically(source: Path, destination: Path, expected_sha256: str) -> None:
    destination.parent.mkdir(parents=True, exist_ok=True)
    fd, temp_name = tempfile.mkstemp(
        prefix=destination.name + ".",
        suffix=".tmp",
        dir=str(destination.parent),
    )
    os.close(fd)
    temp = Path(temp_name)
    try:
        shutil.copyfile(source, temp)
        if sha256_file(temp) != expected_sha256:
            raise ValidationError(f"Deployment source changed while staging: {source}")
        os.replace(temp, destination)
        if sha256_file(destination) != expected_sha256:
            raise ValidationError(f"Installed deployment file failed integrity verification: {destination}")
    finally:
        try:
            temp.unlink()
        except FileNotFoundError:
            pass


def install_current_deployment_assets(
    store: ContinualLearningStore,
    assets_root: str | os.PathLike[str],
) -> Mapping[str, object]:
    """Install exact current-champion package bytes into the Bees-Assets Resources tree."""
    root = Path(assets_root).expanduser().resolve()
    if not (root / PROJECT_SENTINEL).is_file():
        raise ValidationError(
            f"Unity Assets root does not contain {PROJECT_SENTINEL.as_posix()}: {root}"
        )

    deployment = publish_current_champion(store)
    source_model = Path(str(deployment["model_path"])).resolve()
    source_manifest = Path(str(deployment["manifest_path"])).resolve()
    if not source_model.is_file() or not source_manifest.is_file():
        raise ValidationError("Published deployment package is missing model or manifest bytes.")

    manifest = _read_json_object(source_manifest, "Deployment manifest")
    if manifest.get("deployment_id") != deployment["deployment_id"]:
        raise ValidationError("Deployment manifest ID does not match the published deployment.")
    identity = manifest.get("identity")
    if not isinstance(identity, Mapping):
        raise ValidationError("Deployment manifest identity is missing or malformed.")
    if identity.get("model_id") != deployment["model_id"]:
        raise ValidationError("Deployment manifest model ID does not match the published deployment.")
    expected_model_hash = identity.get("model_sha256")
    if not isinstance(expected_model_hash, str) or sha256_file(source_model) != expected_model_hash:
        raise ValidationError("Deployment package model hash does not match its manifest.")
    expected_manifest_hash = deployment["manifest_sha256"]
    if sha256_file(source_manifest) != expected_manifest_hash:
        raise ValidationError("Deployment package manifest hash changed after publication.")

    destination_dir = root / RESOURCE_DIRECTORY
    destination_model = destination_dir / RESOURCE_MODEL_FILE
    destination_manifest = destination_dir / RESOURCE_MANIFEST_FILE
    _replace_file_atomically(source_model, destination_model, expected_model_hash)
    _replace_file_atomically(source_manifest, destination_manifest, expected_manifest_hash)

    return {
        "deployment_id": deployment["deployment_id"],
        "model_id": deployment["model_id"],
        "model_sha256": expected_model_hash,
        "manifest_sha256": expected_manifest_hash,
        "model_asset_source": str(destination_model),
        "manifest_asset_source": str(destination_manifest),
        "resources_model_path": "RlPolicy/BeesRL1v1",
        "resources_manifest_path": "RlPolicy/BeesRL1v1Deployment",
    }


def _build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Install the verified current Bees RL champion as Unity Resources build inputs."
    )
    parser.add_argument("--root", required=True, help="Initialized continual-learning store root.")
    parser.add_argument(
        "--assets-root",
        default=str(Path(__file__).resolve().parent.parent),
        help="Bees-Assets repository root containing Scripts/ and Resources/.",
    )
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
        result = install_current_deployment_assets(store, args.assets_root)
        print(json.dumps(result, indent=2, sort_keys=True, ensure_ascii=False))
        return 0
    except (ContinualLearningError, OSError, ValueError) as exc:
        print(f"Unity deployment bundle install failed: {exc}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
