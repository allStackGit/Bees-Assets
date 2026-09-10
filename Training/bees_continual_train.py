"""Run Bees ML-Agents with continual-learning candidate registration enabled.

This is an opt-in wrapper around Training/bees_mlagents_learn.py. Existing training commands
remain valid and unchanged. When --continual-root is supplied, stable exported ONNX checkpoints
are copied into the immutable continual-learning registry as candidates while training continues.
The configured historical-league share can also replace the non-learning GhostTrainer policy with
an immutable historical ONNX policy at ordinary self-play swap boundaries.

Example:
    python Training/bees_continual_train.py Training/rl_1v1_config.yaml \
      --env="F:\\RLDemo\\Bees RL Training" --run-id=bees-full-001 --resume \
      --torch-device=cuda --bees-batch-inference --bees-cpu-inference \
      --continual-root="F:\\RLDemo\\BeesContinual" \
      --continual-game-build="2026.09.10"
"""

from __future__ import annotations

import math
import re
import sys
import threading
from dataclasses import dataclass
from pathlib import Path
from typing import Dict, List, Mapping, Optional, Sequence, Tuple


CONTINUAL_ROOT_FLAG = "--continual-root"
CONTINUAL_CONFIG_FLAG = "--continual-config"
GAME_BUILD_FLAG = "--continual-game-build"
PARENT_MODEL_FLAG = "--continual-parent-model-id"
SCAN_SECONDS_FLAG = "--continual-scan-seconds"
DEFAULT_SCAN_SECONDS = 2.0
DEFAULT_HISTORICAL_POLICY_CACHE_SIZE = 4


@dataclass(frozen=True)
class ContinualOptions:
    root: Optional[str] = None
    config: Optional[str] = None
    game_build: Optional[str] = None
    parent_model_id: Optional[str] = None
    scan_seconds: float = DEFAULT_SCAN_SECONDS

    @property
    def enabled(self) -> bool:
        return bool(self.root)


def _read_value(
    argv: Sequence[str],
    index: int,
    flag: str,
) -> Tuple[Optional[str], int]:
    argument = argv[index]
    if argument == flag:
        if index + 1 >= len(argv) or argv[index + 1].startswith("--"):
            raise SystemExit(f"{flag} requires a value.")
        return argv[index + 1], index + 2
    prefix = flag + "="
    if argument.startswith(prefix):
        value = argument[len(prefix):]
        if not value:
            raise SystemExit(f"{flag} requires a value.")
        return value, index + 1
    return None, index


def extract_continual_options(argv: Sequence[str]) -> Tuple[List[str], ContinualOptions]:
    trainer_args: List[str] = []
    values = {
        "root": None,
        "config": None,
        "game_build": None,
        "parent_model_id": None,
        "scan_seconds": DEFAULT_SCAN_SECONDS,
    }
    index = 0
    while index < len(argv):
        argument = argv[index]
        matched = False
        for flag, key in (
            (CONTINUAL_ROOT_FLAG, "root"),
            (CONTINUAL_CONFIG_FLAG, "config"),
            (GAME_BUILD_FLAG, "game_build"),
            (PARENT_MODEL_FLAG, "parent_model_id"),
        ):
            value, next_index = _read_value(argv, index, flag)
            if value is not None:
                values[key] = value
                index = next_index
                matched = True
                break
        if matched:
            continue

        value, next_index = _read_value(argv, index, SCAN_SECONDS_FLAG)
        if value is not None:
            try:
                seconds = float(value)
            except ValueError as exc:
                raise SystemExit(f"{SCAN_SECONDS_FLAG} requires a positive number.") from exc
            if seconds <= 0:
                raise SystemExit(f"{SCAN_SECONDS_FLAG} requires a positive number.")
            values["scan_seconds"] = seconds
            index = next_index
            continue

        if argument.startswith("--continual-"):
            raise SystemExit(f"Unknown continual-learning option {argument!r}.")

        trainer_args.append(argument)
        index += 1

    options = ContinualOptions(**values)
    if options.enabled and not options.game_build:
        raise SystemExit(
            f"{GAME_BUILD_FLAG} is required when {CONTINUAL_ROOT_FLAG} is enabled."
        )
    if not options.enabled and any(
        (options.config, options.game_build, options.parent_model_id)
    ):
        raise SystemExit(
            f"{CONTINUAL_ROOT_FLAG} is required when other --continual-* options are used."
        )
    return trainer_args, options


def _trainer_arg(argv: Sequence[str], flag: str) -> Optional[str]:
    for index, argument in enumerate(argv):
        if argument == flag:
            if index + 1 < len(argv):
                return argv[index + 1]
            return None
        prefix = flag + "="
        if argument.startswith(prefix):
            return argument[len(prefix):]
    return None


def infer_run_context(argv: Sequence[str]) -> Tuple[Path, str, Path]:
    run_id = _trainer_arg(argv, "--run-id")
    if not run_id:
        raise SystemExit(
            "Continual candidate registration requires ML-Agents --run-id."
        )
    results_dir = Path(_trainer_arg(argv, "--results-dir") or "results")
    training_config = next(
        (
            Path(argument)
            for argument in argv
            if not argument.startswith("-")
            and argument.lower().endswith((".yaml", ".yml"))
        ),
        None,
    )
    if training_config is None:
        raise SystemExit(
            "Could not identify the ML-Agents trainer YAML for continual model metadata."
        )
    return results_dir / run_id, run_id, training_config


def historical_training_settings(
    config: Mapping[str, object],
    trainer_args: Sequence[str],
) -> Tuple[float, Optional[str], int, int]:
    settings = config.get("historical_league", {})
    if not isinstance(settings, dict):
        raise SystemExit("historical_league configuration must be an object.")

    raw_ratio = settings.get("training_ratio", 0.0)
    if isinstance(raw_ratio, bool):
        raise SystemExit("historical_league.training_ratio must be in [0,1].")
    try:
        ratio = float(raw_ratio)
    except (TypeError, ValueError) as exc:
        raise SystemExit("historical_league.training_ratio must be in [0,1].") from exc
    if not math.isfinite(ratio) or ratio < 0.0 or ratio > 1.0:
        raise SystemExit("historical_league.training_ratio must be in [0,1].")

    raw_provider = settings.get("training_onnx_provider")
    if raw_provider is None:
        provider = None
    elif not isinstance(raw_provider, str) or not raw_provider.strip():
        raise SystemExit(
            "historical_league.training_onnx_provider must be a non-empty string or null."
        )
    else:
        provider = raw_provider.strip()

    raw_cache_size = settings.get(
        "training_policy_cache_size",
        DEFAULT_HISTORICAL_POLICY_CACHE_SIZE,
    )
    if isinstance(raw_cache_size, bool):
        raise SystemExit(
            "historical_league.training_policy_cache_size must be a positive integer."
        )
    try:
        cache_size = int(raw_cache_size)
    except (TypeError, ValueError) as exc:
        raise SystemExit(
            "historical_league.training_policy_cache_size must be a positive integer."
        ) from exc
    if cache_size <= 0 or (
        isinstance(raw_cache_size, float) and not raw_cache_size.is_integer()
    ):
        raise SystemExit(
            "historical_league.training_policy_cache_size must be a positive integer."
        )

    seed_text = _trainer_arg(trainer_args, "--seed")
    try:
        seed = 0 if seed_text is None else int(seed_text)
    except ValueError as exc:
        raise SystemExit("ML-Agents --seed must be an integer for continual league sampling.") from exc
    return ratio, provider, seed, cache_size


_STEP_PATTERNS = (
    re.compile(r"(?:^|[-_])(?:step[-_]?)?(\d{4,})(?:$|[-_.])", re.IGNORECASE),
    re.compile(r"^(\d{4,})$"),
)


def infer_training_step(path: Path) -> Optional[int]:
    candidates = [path.stem] + [part for part in reversed(path.parts[:-1])]
    for candidate in candidates:
        for pattern in _STEP_PATTERNS:
            matches = list(pattern.finditer(candidate))
            if matches:
                return int(matches[-1].group(1))
    return None


class CandidateMonitor:
    def __init__(
        self,
        store,
        *,
        results_run_dir: Path,
        run_id: str,
        game_build: str,
        training_config: Path,
        parent_model_id: Optional[str],
        interval_seconds: float,
    ) -> None:
        self.store = store
        self.results_run_dir = results_run_dir
        self.run_id = run_id
        self.game_build = game_build
        self.training_config = training_config
        self.parent_model_id = parent_model_id
        self.interval_seconds = interval_seconds
        self._stats: Dict[str, Tuple[int, int, int]] = {}
        self._registered: Dict[str, Tuple[int, int]] = {}
        self._stop = threading.Event()
        self._thread: Optional[threading.Thread] = None
        self.errors: List[str] = []

    def prime_existing(self) -> None:
        """Treat pre-existing exports as baseline, not newly produced candidates."""
        if not self.results_run_dir.exists():
            return
        for path in sorted(self.results_run_dir.rglob("*.onnx")):
            try:
                stat = path.stat()
            except OSError as exc:
                message = f"{path}: {type(exc).__name__}: {exc}"
                if message not in self.errors:
                    self.errors.append(message)
                    print(
                        f"[Bees continual] candidate baseline scan failed: {message}",
                        file=sys.stderr,
                    )
                continue
            if stat.st_size <= 0:
                continue
            self._registered[str(path.resolve())] = (stat.st_size, stat.st_mtime_ns)

    def start(self) -> None:
        # On --resume the results tree can contain thousands of old checkpoints.
        # Their original build/config/lineage metadata is not recoverable from the
        # current command, so only exports created or changed after this point are
        # eligible for automatic registration.
        self.prime_existing()
        self._thread = threading.Thread(
            target=self._run,
            name="BeesContinualCandidateMonitor",
            daemon=True,
        )
        self._thread.start()

    def stop(self) -> None:
        self._stop.set()
        if self._thread is not None:
            self._thread.join(timeout=max(5.0, self.interval_seconds * 3))
        # Two immediate scans let a final checkpoint become stable without an artificial delay.
        self.scan_once()
        self.scan_once()

    def _run(self) -> None:
        while not self._stop.wait(self.interval_seconds):
            self.scan_once()

    def scan_once(self) -> None:
        if not self.results_run_dir.exists():
            return
        for path in sorted(self.results_run_dir.rglob("*.onnx")):
            try:
                self._consider(path)
            except Exception as exc:
                message = f"{path}: {type(exc).__name__}: {exc}"
                if message not in self.errors:
                    self.errors.append(message)
                    print(f"[Bees continual] candidate registration failed: {message}", file=sys.stderr)

    def _consider(self, path: Path) -> None:
        stat = path.stat()
        key = str(path.resolve())
        identity = (stat.st_size, stat.st_mtime_ns)
        if stat.st_size <= 0:
            return
        if self._registered.get(key) == identity:
            return

        previous = self._stats.get(key)
        if previous is None or previous[:2] != identity:
            self._stats[key] = (identity[0], identity[1], 1)
            return
        stable_count = previous[2] + 1
        self._stats[key] = (identity[0], identity[1], stable_count)
        if stable_count < 2:
            return

        step = infer_training_step(path)
        if step is None:
            return

        model = self.store.register_model(
            path,
            training_run_id=self.run_id,
            training_step=step,
            game_build_version=self.game_build,
            parent_model_id=self.parent_model_id,
            training_config_path=self.training_config,
            source_checkpoint=str(path),
            status="candidate",
            metadata={"registration_source": "bees_continual_train"},
        )
        self._registered[key] = identity
        print(
            f"[Bees continual] registered candidate model_id={model['model_id']} step={step} "
            f"artifact={path}"
        )


def main(argv: Optional[Sequence[str]] = None) -> int:
    raw_args = list(sys.argv[1:] if argv is None else argv)
    trainer_args, options = extract_continual_options(raw_args)

    if not options.enabled:
        import bees_mlagents_learn as launcher

        original_argv = sys.argv
        try:
            sys.argv = [original_argv[0], *trainer_args]
            launcher.main()
        finally:
            sys.argv = original_argv
        return 0

    from bees_continual_learning import ContinualLearningStore, load_config
    from bees_continual_historical import install_historical_opponents
    import bees_mlagents_learn as launcher

    results_run_dir, run_id, training_config = infer_run_context(trainer_args)
    continual_config = load_config(options.config) if options.config else load_config()
    store = ContinualLearningStore(options.root, continual_config)
    store.initialize()

    parent_model_id = options.parent_model_id or store.current_champion_id()
    if parent_model_id:
        store.get_model(parent_model_id)  # Fail before training if lineage metadata is invalid.

    (
        historical_ratio,
        historical_provider,
        historical_seed,
        historical_cache_size,
    ) = historical_training_settings(
        continual_config,
        trainer_args,
    )
    historical_patch = None
    if historical_ratio > 0.0:
        historical_patch = install_historical_opponents(
            store,
            ratio=historical_ratio,
            seed=historical_seed,
            provider=historical_provider,
            cache_size=historical_cache_size,
        )
        print(
            f"[Bees continual] persistent historical opponent share={historical_ratio:.1%} "
            f"provider={historical_provider or 'CPUExecutionProvider'} "
            f"policy_cache={historical_cache_size}"
        )

    monitor = CandidateMonitor(
        store,
        results_run_dir=results_run_dir,
        run_id=run_id,
        game_build=options.game_build or "",
        training_config=training_config,
        parent_model_id=parent_model_id,
        interval_seconds=options.scan_seconds,
    )
    print(
        f"[Bees continual] root={store.root} run_id={run_id} "
        f"parent_model_id={parent_model_id or 'none'} results={results_run_dir}"
    )
    monitor.start()

    original_argv = sys.argv
    try:
        sys.argv = [original_argv[0], *trainer_args]
        launcher.main()
    finally:
        sys.argv = original_argv
        if historical_patch is not None:
            historical_patch.restore()
        monitor.stop()

    if monitor.errors:
        print(
            f"[Bees continual] completed with {len(monitor.errors)} candidate-registration error(s).",
            file=sys.stderr,
        )
        return 3
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
