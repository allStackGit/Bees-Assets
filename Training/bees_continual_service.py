"""Run Bees continual learning as an autonomous, generation-bounded service.

This is the long-running orchestration layer intended to be supervised by BeesServer. Gameplay
telemetry remains off-policy discovery evidence; every PPO update is still produced by fresh Unity
rollouts. The service advances one persistent ML-Agents optimizer/checkpoint lineage through bounded
generations so each generation can freeze newly mined gameplay pressure without mutating an active
population. After each generation it runs the authoritative release gate and, when the champion
changes, stages/builds/publishes a validated desktop hot bundle for authenticated clients.

The service is deliberately fail-safe. A failed training, evaluation, Unity build, or publication
phase does not replace the current champion. Persistent phase state lets the supervisor restart the
same phase rather than silently skipping work.
"""

from __future__ import annotations

import argparse
import json
import os
import re
import subprocess
import sys
import tempfile
import time
from dataclasses import dataclass
from pathlib import Path
from typing import Callable, Dict, Mapping, Optional, Sequence


SERVICE_SCHEMA_VERSION = 1
DEFAULT_RUN_ID = "bees-continuous-v8"
DEFAULT_GENERATION_STEPS = 1_000_000
DEFAULT_NUM_ENVS = 4
DEFAULT_RETRY_SECONDS = 30.0
MANAGED_STOP_FILE_ENV = "BEES_TRAINING_STOP_FILE"
MANAGED_CHILD_POLL_SECONDS = 0.25
PLATFORM_BUILD_TARGETS = {
    "WindowsPlayer": "StandaloneWindows64",
    "OSXPlayer": "StandaloneOSX",
    "LinuxPlayer": "StandaloneLinux64",
}
_DEPLOYMENT_ID = re.compile(r"^deploy-[0-9a-f]{24}$")


@dataclass(frozen=True)
class ServiceOptions:
    root: Path
    assets_root: Path
    training_env: Path
    telemetry_quarantine: Path
    model_distribution_root: Path
    game_build_version: str
    unity_editor: Path
    unity_project_root: Path
    trainer_config: Path
    continual_config: Path
    competency_suite: Optional[Path]
    python_executable: str
    run_id: str
    generation_steps: int
    num_envs: int
    platform: str
    retry_seconds: float
    once: bool
    environment_args: tuple[str, ...] = ()


Runner = Callable[..., subprocess.CompletedProcess]


def _required_path(value: str, label: str, *, file: bool = False, directory: bool = False) -> Path:
    path = Path(value).expanduser().resolve()
    if file and not path.is_file():
        raise ValueError(f"{label} does not exist or is not a file: {path}")
    if directory and not path.is_dir():
        raise ValueError(f"{label} does not exist or is not a directory: {path}")
    return path


def rewrite_max_steps(text: str, max_steps: int) -> str:
    """Replace the single ML-Agents behavior max_steps value without changing the base YAML."""
    if not isinstance(max_steps, int) or isinstance(max_steps, bool) or max_steps <= 0:
        raise ValueError("max_steps must be a positive integer")
    pattern = re.compile(r"^(?P<prefix>\s*max_steps:\s*)\d+(?P<suffix>\s*(?:#.*)?)$", re.MULTILINE)

    def replace(match: re.Match[str]) -> str:
        return f"{match.group('prefix')}{max_steps}{match.group('suffix')}"

    updated, count = pattern.subn(replace, text)
    if count != 1:
        raise ValueError(
            f"Expected exactly one integer max_steps entry in trainer config; found {count}."
        )
    return updated


def _atomic_write(path: Path, payload: bytes) -> None:
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


def _service_root(options: ServiceOptions) -> Path:
    # Run-scoped service state prevents an incompatible policy/reward/scenario contract from
    # resuming the previous optimizer lineage. Durable checkpoint/result directories are already
    # keyed by run_id; keep the orchestration phase state equally isolated.
    return options.root / "metadata" / "continuous-service" / options.run_id


def generation_id(index: int) -> str:
    if not isinstance(index, int) or isinstance(index, bool) or index < 0:
        raise ValueError("generation index must be a non-negative integer")
    return f"generation-{index:08d}"


def generation_target_steps(options: ServiceOptions, index: int) -> int:
    return (index + 1) * options.generation_steps


def training_run_dir(options: ServiceOptions) -> Path:
    return options.root / "trainer-results" / options.run_id


def training_checkpoint_exists(options: ServiceOptions) -> bool:
    """Return whether ML-Agents has written a resumable trainer checkpoint."""
    run_dir = training_run_dir(options)
    if not run_dir.is_dir():
        return False
    return any(path.is_file() for path in run_dir.glob("*/checkpoint.pt"))


def should_resume_training(
    options: ServiceOptions,
    *,
    generation_index: int,
    previously_started: bool,
) -> bool:
    """Resume only when a prior trainer invocation actually wrote a checkpoint.

    ML-Agents creates the run directory before trainer initialization, so directory existence
    alone is not evidence that --resume is safe. A failed startup before the first checkpoint
    must retry generation zero fresh instead.
    """
    if generation_index > 0:
        if not training_checkpoint_exists(options):
            raise RuntimeError(
                f"Cannot resume {generation_id(generation_index)} because the persistent "
                "ML-Agents checkpoint is missing."
            )
        return True
    return previously_started and training_checkpoint_exists(options)


def write_generation_config(options: ServiceOptions, index: int) -> Path:
    source = options.trainer_config.read_text(encoding="utf-8")
    target_steps = generation_target_steps(options, index)
    body = rewrite_max_steps(source, target_steps).encode("utf-8")
    destination = _service_root(options) / "trainer-configs" / f"{generation_id(index)}.yaml"
    if destination.exists():
        if destination.read_bytes() != body:
            raise ValueError(f"Immutable generation trainer config conflict: {destination}")
        return destination
    destination.parent.mkdir(parents=True, exist_ok=True)
    try:
        with destination.open("xb") as handle:
            handle.write(body)
            handle.flush()
            os.fsync(handle.fileno())
    except FileExistsError:
        if destination.read_bytes() != body:
            raise ValueError(f"Immutable generation trainer config conflict: {destination}")
    return destination


def _initial_state(options: ServiceOptions) -> Dict[str, object]:
    return {
        "schema_version": SERVICE_SCHEMA_VERSION,
        "run_id": options.run_id,
        "generation_index": 0,
        "phase": "train",
        "training_started": False,
        "last_hot_deployment_id": None,
    }


def load_state(options: ServiceOptions) -> Dict[str, object]:
    path = _service_root(options) / "state.json"
    if not path.is_file():
        return _initial_state(options)
    try:
        state = json.loads(path.read_text(encoding="utf-8"))
    except json.JSONDecodeError as exc:
        raise ValueError(f"Continuous-learning service state is invalid JSON: {path}: {exc}") from exc
    if not isinstance(state, dict) or state.get("schema_version") != SERVICE_SCHEMA_VERSION:
        raise ValueError(f"Continuous-learning service state is incompatible: {path}")
    if state.get("run_id") != options.run_id:
        raise ValueError(
            f"Continuous-learning state belongs to run {state.get('run_id')!r}, not {options.run_id!r}."
        )
    if (
        not isinstance(state.get("generation_index"), int)
        or isinstance(state.get("generation_index"), bool)
        or int(state["generation_index"]) < 0
        or state.get("phase") not in ("train", "release", "publish")
        or not isinstance(state.get("training_started"), bool)
    ):
        raise ValueError(f"Continuous-learning service state is malformed: {path}")
    hot = state.get("last_hot_deployment_id")
    if hot is not None and (not isinstance(hot, str) or not _DEPLOYMENT_ID.fullmatch(hot)):
        raise ValueError(f"Continuous-learning service hot-deployment state is malformed: {path}")
    return state


def save_state(options: ServiceOptions, state: Mapping[str, object]) -> None:
    payload = (json.dumps(dict(state), indent=2, sort_keys=True) + "\n").encode("utf-8")
    _atomic_write(_service_root(options) / "state.json", payload)


def current_compatible_champion_id(options: ServiceOptions) -> Optional[str]:
    """Return the registry champion only when it matches this service's compatibility contract."""
    from bees_continual_learning import ContinualLearningStore, load_config

    store = ContinualLearningStore(options.root, config=load_config(options.continual_config))
    store.initialize()
    return store.current_compatible_champion_id()


def current_deployment_id(options: ServiceOptions) -> Optional[str]:
    path = options.root / "deployment" / "current-deployment.json"
    if not path.is_file():
        return None
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except json.JSONDecodeError as exc:
        raise ValueError(f"Current deployment pointer is invalid JSON: {path}: {exc}") from exc
    identity = value.get("identity") if isinstance(value, dict) else None
    deployment_id = identity.get("deployment_id") if isinstance(identity, dict) else None
    if not isinstance(deployment_id, str) or not _DEPLOYMENT_ID.fullmatch(deployment_id):
        raise ValueError(f"Current deployment pointer has malformed deployment identity: {path}")
    return deployment_id


def parse_environment_args_json(value: str) -> tuple[str, ...]:
    try:
        parsed = json.loads(value)
    except json.JSONDecodeError as exc:
        raise ValueError(f"--environment-args-json must be valid JSON: {exc}") from exc
    if not isinstance(parsed, list) or any(not isinstance(item, str) for item in parsed):
        raise ValueError("--environment-args-json must be a JSON array of strings")
    if any(item == "" for item in parsed):
        raise ValueError("--environment-args-json may not contain empty strings")
    return tuple(parsed)


def training_command(
    options: ServiceOptions,
    index: int,
    *,
    resume: bool,
    force_fresh: bool = False,
) -> list[str]:
    config = write_generation_config(options, index)
    command = [
        options.python_executable,
        str(options.assets_root / "Training" / "bees_continual_auto_train.py"),
        str(config),
        f"--env={options.training_env}",
        f"--run-id={options.run_id}",
        f"--results-dir={options.root / 'trainer-results'}",
        f"--num-envs={options.num_envs}",
        "--no-graphics",
        f"--continual-root={options.root}",
        f"--continual-config={options.continual_config}",
        f"--continual-game-build={options.game_build_version}",
        f"--continual-public-telemetry-quarantine={options.telemetry_quarantine}",
        f"--continual-public-generation-id={generation_id(index)}",
    ]
    if resume and force_fresh:
        raise ValueError("training command cannot both resume and force a fresh run")
    if resume:
        command.append("--resume")
    elif force_fresh:
        command.append("--force")
    if options.environment_args:
        command.append("--env-args")
        command.extend(options.environment_args)
    return command


def release_command(options: ServiceOptions) -> list[str]:
    command = [
        options.python_executable,
        str(options.assets_root / "Training" / "bees_continual_release.py"),
        f"--root={options.root}",
        f"--env={options.training_env}",
        f"--training-run-id={options.run_id}",
        f"--config={options.continual_config}",
    ]
    if options.competency_suite is not None:
        command.append(f"--competency-suite={options.competency_suite}")
    return command


def stage_command(options: ServiceOptions) -> list[str]:
    return [
        options.python_executable,
        str(options.assets_root / "Training" / "bees_continual_unity_bundle.py"),
        f"--root={options.root}",
        f"--assets-root={options.assets_root}",
        f"--config={options.continual_config}",
    ]


def unity_build_command(options: ServiceOptions, output: Path) -> list[str]:
    return [
        str(options.unity_editor),
        "-batchmode",
        "-quit",
        "-projectPath",
        str(options.unity_project_root),
        "-buildTarget",
        PLATFORM_BUILD_TARGETS[options.platform],
        "-executeMethod",
        "RlLivePolicyHotBundleBuilder.BuildFromCommandLine",
        f"--bees-rl-hot-bundle-output={output}",
    ]


def hot_publish_command(options: ServiceOptions, metadata: Path) -> list[str]:
    return [
        options.python_executable,
        str(options.assets_root / "Training" / "bees_continual_hot_bundle.py"),
        f"--root={options.root}",
        f"--bundle-metadata={metadata}",
        f"--distribution-root={options.model_distribution_root}",
        f"--config={options.continual_config}",
    ]


def _managed_stop_file() -> Optional[Path]:
    value = os.environ.get(MANAGED_STOP_FILE_ENV, "").strip()
    return Path(value).expanduser().resolve() if value else None


def _managed_stop_requested() -> bool:
    path = _managed_stop_file()
    return path is not None and path.is_file()


def _run_managed_subprocess(command: Sequence[str], options: ServiceOptions) -> int:
    if _managed_stop_requested():
        raise KeyboardInterrupt

    kwargs: dict[str, object] = {
        "cwd": str(options.assets_root),
    }
    if os.name == "nt":
        kwargs["creationflags"] = subprocess.CREATE_NEW_PROCESS_GROUP
    else:
        kwargs["start_new_session"] = True

    process = subprocess.Popen(list(command), **kwargs)
    stop_requested = False
    while process.poll() is None:
        if _managed_stop_requested() and not stop_requested:
            print(
                "[Bees continuous] managed shutdown requested; "
                "waiting for the active phase to finalize checkpoint/model output.",
                flush=True,
            )
            stop_requested = True
        time.sleep(MANAGED_CHILD_POLL_SECONDS)

    return_code = int(process.wait())
    if stop_requested or _managed_stop_requested():
        raise KeyboardInterrupt
    return return_code


def _run(command: Sequence[str], options: ServiceOptions, runner: Runner) -> None:
    if runner is subprocess.run and _managed_stop_file() is not None:
        return_code = _run_managed_subprocess(command, options)
    else:
        completed = runner(
            list(command),
            cwd=str(options.assets_root),
            check=False,
        )
        return_code = int(getattr(completed, "returncode", 0))
    if return_code != 0:
        raise RuntimeError(f"Command exited with status {return_code}: {command[0]} {command[1] if len(command) > 1 else ''}")


def publish_current_hot_bundle(options: ServiceOptions, runner: Runner) -> str:
    """Stage, build and publish only the registry's already-validated current champion."""
    _run(stage_command(options), options, runner)
    deployment_id = current_deployment_id(options)
    if deployment_id is None:
        raise RuntimeError("Staging completed without publishing a current deployment pointer.")

    build_root = _service_root(options) / "hot-build-temp"
    build_root.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(
        prefix=f"{deployment_id}-{options.platform}-",
        dir=str(build_root),
    ) as temp_dir:
        output = Path(temp_dir)
        _run(unity_build_command(options, output), options, runner)
        metadata = output / "bees-rl-policy.metadata.json"
        if not metadata.is_file():
            raise RuntimeError(f"Unity did not produce hot-bundle metadata: {metadata}")
        _run(hot_publish_command(options, metadata), options, runner)
    return deployment_id


def run_service(
    options: ServiceOptions,
    *,
    runner: Runner = subprocess.run,
    sleeper: Callable[[float], None] = time.sleep,
) -> int:
    state = load_state(options)

    while True:
        try:
            # Ensure the current validated champion is server-visible immediately after supervisor
            # startup, even before the next training generation finishes. An old deployment pointer
            # may legitimately belong to a previous incompatible ABI; do not try to restage it under
            # the new contract before generation zero has produced a compatible champion.
            compatible_champion = current_compatible_champion_id(options)
            published = current_deployment_id(options)
            if compatible_champion is None:
                if state.get("last_hot_deployment_id") is not None:
                    state["last_hot_deployment_id"] = None
                    save_state(options, state)
                print(
                    "[Bees continuous] no compatible validated champion exists yet; "
                    "starting training before generation-zero bootstrap/publish."
                )
            elif published is None or published != state.get("last_hot_deployment_id"):
                published = publish_current_hot_bundle(options, runner)
                state["last_hot_deployment_id"] = published
                save_state(options, state)

            index = int(state["generation_index"])
            phase = str(state["phase"])

            if phase == "train":
                previously_started = bool(state["training_started"])
                state["training_started"] = True
                save_state(options, state)
                resume = should_resume_training(
                    options,
                    generation_index=index,
                    previously_started=previously_started,
                )
                force_fresh = (
                    index == 0
                    and previously_started
                    and not resume
                    and training_run_dir(options).is_dir()
                )
                print(
                    f"[Bees continuous] training {generation_id(index)} "
                    f"target_steps={generation_target_steps(options, index)} "
                    f"resume={resume} force_fresh={force_fresh}"
                )
                _run(
                    training_command(
                        options,
                        index,
                        resume=resume,
                        force_fresh=force_fresh,
                    ),
                    options,
                    runner,
                )
                state["phase"] = "release"
                save_state(options, state)
                phase = "release"

            if phase == "release":
                print(f"[Bees continuous] evaluating/releasing {generation_id(index)}")
                _run(release_command(options), options, runner)
                state["phase"] = "publish"
                save_state(options, state)
                phase = "publish"

            if phase == "publish":
                deployment = current_deployment_id(options)
                if deployment is None:
                    raise RuntimeError("Release phase left no validated current deployment.")
                if deployment != state.get("last_hot_deployment_id"):
                    print(f"[Bees continuous] publishing validated champion {deployment}")
                    deployment = publish_current_hot_bundle(options, runner)
                    state["last_hot_deployment_id"] = deployment

                state["generation_index"] = index + 1
                state["phase"] = "train"
                state["training_started"] = False
                save_state(options, state)
                print(
                    f"[Bees continuous] completed {generation_id(index)}; "
                    f"next={generation_id(index + 1)}"
                )
                if options.once:
                    return 0
        except KeyboardInterrupt:
            return 130
        except Exception as exc:
            print(
                f"[Bees continuous] phase failed safely: {type(exc).__name__}: {exc}",
                file=sys.stderr,
            )
            if options.once:
                return 2
            sleeper(options.retry_seconds)
            state = load_state(options)


def _parser() -> argparse.ArgumentParser:
    default_assets = Path(__file__).resolve().parent.parent
    parser = argparse.ArgumentParser(
        description="Autonomous generation-bounded Bees continual training/release/hot-publication service."
    )
    parser.add_argument("--root", required=True)
    parser.add_argument("--assets-root", default=str(default_assets))
    parser.add_argument("--training-env", required=True)
    parser.add_argument("--telemetry-quarantine", required=True)
    parser.add_argument("--model-distribution-root", required=True)
    parser.add_argument("--game-build-version", required=True)
    parser.add_argument("--unity-editor", required=True)
    parser.add_argument("--unity-project-root", required=True)
    parser.add_argument(
        "--trainer-config",
        default=str(Path(__file__).with_name("rl_1v1_config.yaml")),
    )
    parser.add_argument(
        "--continual-config",
        default=str(Path(__file__).with_name("continual_learning_config.json")),
    )
    parser.add_argument("--competency-suite", default=None)
    parser.add_argument("--python-executable", default=sys.executable)
    parser.add_argument("--run-id", default=DEFAULT_RUN_ID)
    parser.add_argument("--generation-steps", type=int, default=DEFAULT_GENERATION_STEPS)
    parser.add_argument("--num-envs", type=int, default=DEFAULT_NUM_ENVS)
    parser.add_argument(
        "--environment-args-json",
        default=os.environ.get("BEES_TRAINING_ENV_ARGS_JSON", "[]"),
    )
    parser.add_argument("--platform", choices=tuple(PLATFORM_BUILD_TARGETS), default="WindowsPlayer")
    parser.add_argument("--retry-seconds", type=float, default=DEFAULT_RETRY_SECONDS)
    parser.add_argument("--once", action="store_true")
    return parser


def parse_options(argv: Optional[Sequence[str]] = None) -> ServiceOptions:
    args = _parser().parse_args(argv)
    if args.generation_steps <= 0:
        raise ValueError("--generation-steps must be greater than zero")
    if args.num_envs <= 0:
        raise ValueError("--num-envs must be greater than zero")
    if args.retry_seconds <= 0:
        raise ValueError("--retry-seconds must be greater than zero")
    if not isinstance(args.run_id, str) or not args.run_id.strip():
        raise ValueError("--run-id must be non-empty")
    if not isinstance(args.game_build_version, str) or not args.game_build_version.strip():
        raise ValueError("--game-build-version must be non-empty")

    root = Path(args.root).expanduser().resolve()
    root.mkdir(parents=True, exist_ok=True)
    telemetry = Path(args.telemetry_quarantine).expanduser().resolve()
    telemetry.mkdir(parents=True, exist_ok=True)
    distribution = Path(args.model_distribution_root).expanduser().resolve()
    distribution.mkdir(parents=True, exist_ok=True)

    return ServiceOptions(
        root=root,
        assets_root=_required_path(args.assets_root, "Assets root", directory=True),
        training_env=_required_path(args.training_env, "Training environment", file=True),
        telemetry_quarantine=telemetry,
        model_distribution_root=distribution,
        game_build_version=args.game_build_version.strip(),
        unity_editor=_required_path(args.unity_editor, "Unity Editor executable", file=True),
        unity_project_root=_required_path(args.unity_project_root, "Unity project root", directory=True),
        trainer_config=_required_path(args.trainer_config, "Trainer config", file=True),
        continual_config=_required_path(args.continual_config, "Continual config", file=True),
        competency_suite=(
            _required_path(args.competency_suite, "Competency suite", file=True)
            if args.competency_suite
            else None
        ),
        python_executable=args.python_executable,
        run_id=args.run_id.strip(),
        generation_steps=args.generation_steps,
        num_envs=args.num_envs,
        platform=args.platform,
        retry_seconds=args.retry_seconds,
        once=args.once,
        environment_args=parse_environment_args_json(args.environment_args_json),
    )


def main(argv: Optional[Sequence[str]] = None) -> int:
    try:
        options = parse_options(argv)
        return run_service(options)
    except (OSError, ValueError) as exc:
        print(f"Continuous-learning service configuration error: {exc}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
