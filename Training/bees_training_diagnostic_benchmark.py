"""Deterministic, non-learning diagnostic benchmark for a live Bees ONNX policy.

The benchmark deliberately reuses the authoritative continual evaluator and runs the same
immutable model on both ML-Agents teams.  A fixed 1v1 Wasp/Gunship scenario removes special-
ability and multi-ship confounders so its Player log provides a direct comparison between
stochastic training telemetry and the policy's deterministic action outputs.
"""

from __future__ import annotations

import argparse
import json
import os
import tempfile
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Callable, Mapping, Optional, Sequence

from bees_continual_evaluate import MatchSummary, run_match_group
from bees_continual_learning import sha256_file
from bees_training_worker_agent import EpisodeLogMetrics


BENCHMARK_SCHEMA_VERSION = 1
BENCHMARK_NAME = "deterministic-wasp-vs-gunship-v1"
DEFAULT_MATCHES = 20
DEFAULT_SEED = 24681357
DEFAULT_WORKER_ID = 2400
DEFAULT_ENV_ARGS = (
    "--bees-rl-arenas-per-env=1",
    "--rl-ships-per-side=1",
    "--rl-bee-ship-types=Wasp",
    "--rl-human-ship-types=Gunship",
    "--rl-map-size=32",
    "--rl-episode-timeout=30",
    "--rl-health-ratio=0.05",
    "--rl-matchup-mode=sampled",
)


def _safe_ratio(numerator: int, denominator: int) -> Optional[float]:
    return float(numerator) / float(denominator) if denominator > 0 else None


def run_benchmark(
    *,
    environment_path: os.PathLike[str] | str,
    model_path: os.PathLike[str] | str,
    matches: int = DEFAULT_MATCHES,
    seed: int = DEFAULT_SEED,
    worker_id: int = DEFAULT_WORKER_ID,
    timeout_wait: int = 60,
    onnx_provider: Optional[str] = None,
    match_runner: Callable[..., MatchSummary] = run_match_group,
) -> dict[str, Any]:
    environment = Path(environment_path).expanduser().resolve()
    model = Path(model_path).expanduser().resolve()
    if not environment.is_file():
        raise FileNotFoundError(f"diagnostic benchmark environment does not exist: {environment}")
    if not model.is_file():
        raise FileNotFoundError(f"diagnostic benchmark model does not exist: {model}")
    if matches <= 0:
        raise ValueError("diagnostic benchmark matches must be positive")

    warnings: list[str] = []
    with tempfile.TemporaryDirectory(prefix="bees-deterministic-benchmark-") as temporary:
        log_root = Path(temporary)
        player_log = log_root / "Player-0.log"
        env_args = (*DEFAULT_ENV_ARGS, "-logFile", str(player_log))
        summary = match_runner(
            environment_path=environment,
            candidate_model_path=model,
            opponent_model_path=model,
            matches=int(matches),
            behavior_name="BeesRL1v1",
            env_args=env_args,
            seed=int(seed),
            worker_id=int(worker_id),
            timeout_wait=int(timeout_wait),
            onnx_provider=onnx_provider,
            no_graphics=True,
        )
        aim_metrics = EpisodeLogMetrics(
            log_root,
            window=max(int(matches), 100),
        ).refresh()
        observed = int(aim_metrics.get("window_episodes", 0) or 0)
        if observed < matches:
            warnings.append(
                f"Player log exposed {observed}/{matches} benchmark episodes; "
                "aim metrics may be incomplete"
            )

    summary_dict = summary.to_dict()
    return {
        "schema_version": BENCHMARK_SCHEMA_VERSION,
        "status": "succeeded",
        "benchmark": BENCHMARK_NAME,
        "generated_utc": datetime.now(timezone.utc).isoformat(),
        "deterministic_actions": True,
        "model_path": str(model),
        "model_sha256": sha256_file(model),
        "environment_path": str(environment),
        "matches_requested": int(matches),
        "seed": int(seed),
        "worker_id": int(worker_id),
        "scenario": {
            "bee_ship": "Wasp",
            "human_ship": "Gunship",
            "ships_per_side": 1,
            "map_size": 32,
            "episode_timeout_seconds": 30,
            "health_ratio": 0.05,
            "env_args": list(DEFAULT_ENV_ARGS),
        },
        "summary": summary_dict,
        "derived": {
            "candidate_hits_per_shot": _safe_ratio(
                summary.candidate_hits,
                summary.candidate_shots,
            ),
            "opponent_hits_per_shot": _safe_ratio(
                summary.opponent_hits,
                summary.opponent_shots,
            ),
            "decisive_rate": (
                float(summary.matches - summary.timeouts) / float(summary.matches)
                if summary.matches > 0
                else None
            ),
        },
        "aim_metrics": aim_metrics,
        "warnings": warnings,
    }


def write_result(path: Path, value: Mapping[str, Any]) -> None:
    path = path.expanduser().resolve()
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_text(
        json.dumps(dict(value), indent=2, sort_keys=True) + "\n",
        encoding="utf-8",
    )
    os.replace(temporary, path)


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Run a deterministic Wasp-vs-Gunship diagnostic benchmark."
    )
    parser.add_argument("--env", required=True)
    parser.add_argument("--model", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--matches", type=int, default=DEFAULT_MATCHES)
    parser.add_argument("--seed", type=int, default=DEFAULT_SEED)
    parser.add_argument("--worker-id", type=int, default=DEFAULT_WORKER_ID)
    parser.add_argument("--timeout-wait", type=int, default=60)
    parser.add_argument("--onnx-provider")
    return parser


def main(argv: Optional[Sequence[str]] = None) -> int:
    args = build_parser().parse_args(argv)
    output = Path(args.output)
    try:
        value = run_benchmark(
            environment_path=args.env,
            model_path=args.model,
            matches=args.matches,
            seed=args.seed,
            worker_id=args.worker_id,
            timeout_wait=args.timeout_wait,
            onnx_provider=args.onnx_provider,
        )
        write_result(output, value)
        print(
            f"Deterministic diagnostic benchmark complete: "
            f"{value['summary']['matches']} matches"
        )
        return 0
    except Exception as exc:
        failure = {
            "schema_version": BENCHMARK_SCHEMA_VERSION,
            "status": "failed",
            "benchmark": BENCHMARK_NAME,
            "generated_utc": datetime.now(timezone.utc).isoformat(),
            "deterministic_actions": True,
            "error": f"{type(exc).__name__}: {exc}",
        }
        try:
            write_result(output, failure)
        except OSError:
            pass
        print(f"Diagnostic benchmark failed: {failure['error']}", file=__import__("sys").stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
