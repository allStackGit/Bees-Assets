"""Build and publish verified desktop hot bundles for the current Bees RL champion.

This is the platform-specific second half of automatic champion deployment. The control-plane release
worker may run on a headless trainer host; this worker runs where Unity Editor and the Bees Unity
project are available. It stages the already-approved current deployment into Assets/Resources,
invokes the existing RlLivePolicyHotBundleBuilder command-line entry point for each requested desktop
platform, then feeds Unity's sidecar into bees_continual_hot_bundle.py for independent verification
and atomic publication.

The Unity build step is never promotion authority. If Unity produces stale, corrupt, mismatched, or
unexpected bytes, publish_hot_bundle rejects them against the registry's current champion/deployment.
"""

from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
import tempfile
from pathlib import Path
from typing import Any, Callable, Dict, List, Mapping, Optional, Sequence

from bees_continual_deployment import OnnxValidator
from bees_continual_hot_bundle import SUPPORTED_PLATFORMS, publish_hot_bundle
from bees_continual_learning import (
    ContinualLearningError,
    ContinualLearningStore,
    ValidationError,
    load_config,
)
from bees_continual_unity_bundle import PROJECT_SENTINEL, install_current_deployment_assets


UNITY_EXECUTE_METHOD = "RlLivePolicyHotBundleBuilder.BuildFromCommandLine"
UNITY_OUTPUT_ARGUMENT = "--bees-rl-hot-bundle-output="
UNITY_METADATA_FILE = "bees-rl-policy.metadata.json"

Runner = Callable[..., Any]


def _required_existing_file(value: str | os.PathLike[str], label: str) -> Path:
    path = Path(value).expanduser().resolve()
    if not path.is_file():
        raise ValidationError(f"{label} does not exist or is not a file: {path}")
    return path


def _validate_assets_root(value: str | os.PathLike[str]) -> Path:
    root = Path(value).expanduser().resolve()
    if not (root / PROJECT_SENTINEL).is_file():
        raise ValidationError(
            f"Unity Assets root does not contain {PROJECT_SENTINEL.as_posix()}: {root}"
        )
    if root.name.lower() != "assets":
        raise ValidationError(
            f"Expected the Bees repository to be the Unity Assets directory, got: {root}"
        )
    return root


def _validate_platforms(platforms: Sequence[str]) -> List[str]:
    ordered = list(dict.fromkeys(platforms))
    if not ordered:
        raise ValidationError("At least one hot-release platform is required.")
    unsupported = [value for value in ordered if value not in SUPPORTED_PLATFORMS]
    if unsupported:
        raise ValidationError("Unsupported hot-release platform(s): " + ", ".join(unsupported))
    return ordered


def _unity_command(
    unity_executable: Path,
    project_root: Path,
    platform: str,
    output_directory: Path,
) -> List[str]:
    return [
        str(unity_executable),
        "-batchmode",
        "-quit",
        "-nographics",
        "-projectPath",
        str(project_root),
        "-buildTarget",
        SUPPORTED_PLATFORMS[platform],
        "-executeMethod",
        UNITY_EXECUTE_METHOD,
        "-logFile",
        "-",
        UNITY_OUTPUT_ARGUMENT + str(output_directory),
    ]


def _run_unity(
    runner: Runner,
    command: Sequence[str],
    *,
    project_root: Path,
    timeout_seconds: int,
    platform: str,
) -> str:
    try:
        completed = runner(
            list(command),
            cwd=str(project_root),
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            text=True,
            encoding="utf-8",
            errors="replace",
            timeout=timeout_seconds,
            check=False,
        )
    except subprocess.TimeoutExpired as exc:
        raise ValidationError(
            f"Unity hot-bundle build timed out for {platform} after {timeout_seconds} seconds."
        ) from exc
    except OSError as exc:
        raise ValidationError(f"Failed to launch Unity for {platform}: {exc}") from exc

    returncode = getattr(completed, "returncode", None)
    output = str(getattr(completed, "stdout", "") or "")
    if returncode != 0:
        tail = output[-8000:]
        raise ValidationError(
            f"Unity hot-bundle build failed for {platform} with exit code {returncode}. "
            f"Unity output tail:\n{tail}"
        )
    return output


def build_and_publish_hot_bundles(
    store: ContinualLearningStore,
    *,
    unity_executable: str | os.PathLike[str],
    assets_root: str | os.PathLike[str],
    distribution_root: str | os.PathLike[str],
    platforms: Sequence[str],
    unity_timeout_seconds: int = 1800,
    runner: Runner = subprocess.run,
    onnx_validator: Optional[OnnxValidator] = None,
) -> Dict[str, Any]:
    """Stage, Unity-build, verify, and publish requested desktop bundles for current champion."""
    if (
        not isinstance(unity_timeout_seconds, int)
        or isinstance(unity_timeout_seconds, bool)
        or unity_timeout_seconds <= 0
    ):
        raise ValidationError("unity_timeout_seconds must be a positive integer.")

    store._require_initialized()
    unity = _required_existing_file(unity_executable, "Unity executable")
    assets = _validate_assets_root(assets_root)
    project_root = assets.parent
    selected = _validate_platforms(platforms)
    distribution = Path(distribution_root).expanduser().resolve()

    staged = dict(
        install_current_deployment_assets(
            store,
            assets,
            onnx_validator=onnx_validator,
        )
    )
    deployment_id = staged.get("deployment_id")
    model_id = staged.get("model_id")
    if not isinstance(deployment_id, str) or not isinstance(model_id, str):
        raise ValidationError("Unity deployment staging did not return deployment/model identity.")

    published: List[Dict[str, Any]] = []
    with tempfile.TemporaryDirectory(prefix="bees-rl-hot-release-") as temp_name:
        temp_root = Path(temp_name)
        for platform in selected:
            output = temp_root / platform
            output.mkdir(parents=True, exist_ok=False)
            command = _unity_command(unity, project_root, platform, output)
            unity_output = _run_unity(
                runner,
                command,
                project_root=project_root,
                timeout_seconds=unity_timeout_seconds,
                platform=platform,
            )
            metadata = output / UNITY_METADATA_FILE
            if not metadata.is_file():
                raise ValidationError(
                    f"Unity reported success for {platform} but did not produce {UNITY_METADATA_FILE}. "
                    f"Unity output tail:\n{unity_output[-8000:]}"
                )
            publication = dict(
                publish_hot_bundle(
                    store,
                    metadata,
                    distribution,
                    onnx_validator=onnx_validator,
                )
            )
            if publication.get("platform") != platform:
                raise ValidationError(
                    f"Hot-bundle publisher returned platform {publication.get('platform')!r}; "
                    f"expected {platform!r}."
                )
            if publication.get("deployment_id") != deployment_id or publication.get("model_id") != model_id:
                raise ValidationError(
                    f"Published {platform} hot bundle does not match the staged current champion."
                )
            published.append(publication)

    return {
        "deployment_id": deployment_id,
        "model_id": model_id,
        "assets_root": str(assets),
        "project_root": str(project_root),
        "distribution_root": str(distribution),
        "platforms": published,
    }


def _positive_int(value: str) -> int:
    parsed = int(value)
    if parsed <= 0:
        raise argparse.ArgumentTypeError("value must be positive")
    return parsed


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Stage, Unity-build, verify, and publish current Bees RL champion hot bundles."
    )
    parser.add_argument("--root", required=True, help="Initialized continual-learning store root.")
    parser.add_argument("--unity", required=True, help="Unity Editor executable path.")
    parser.add_argument(
        "--assets-root",
        default=str(Path(__file__).resolve().parent.parent),
        help="Bees-Assets repository / Unity Assets directory.",
    )
    parser.add_argument(
        "--distribution-root",
        required=True,
        help="BeesServer RL model distribution root.",
    )
    parser.add_argument(
        "--platform",
        action="append",
        required=True,
        choices=tuple(sorted(SUPPORTED_PLATFORMS)),
        help="Desktop hot-rollout platform; repeat to build multiple platforms.",
    )
    parser.add_argument(
        "--unity-timeout-seconds",
        type=_positive_int,
        default=1800,
        help="Per-platform Unity batch build timeout.",
    )
    parser.add_argument(
        "--config",
        default=str(Path(__file__).with_name("continual_learning_config.json")),
        help="Continual-learning configuration JSON.",
    )
    return parser


def main(argv: Optional[Sequence[str]] = None) -> int:
    args = build_parser().parse_args(argv)
    try:
        store = ContinualLearningStore(args.root, load_config(args.config))
        result = build_and_publish_hot_bundles(
            store,
            unity_executable=args.unity,
            assets_root=args.assets_root,
            distribution_root=args.distribution_root,
            platforms=args.platform,
            unity_timeout_seconds=args.unity_timeout_seconds,
        )
        print(json.dumps(result, indent=2, sort_keys=True, ensure_ascii=False))
        return 0
    except (ContinualLearningError, OSError, ValueError) as exc:
        print(f"Champion hot-release build failed: {exc}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
