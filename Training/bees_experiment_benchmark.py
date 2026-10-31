"""Evaluate exported Bees policies against a versioned, fixed-seed combat benchmark.

Uses the existing authoritative Unity evaluation side-channel and ONNX policy adapter.
This tool never changes training state, champions or checkpoints.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import sqlite3
import tempfile
import uuid
from datetime import datetime, timezone
from pathlib import Path


BENCHMARK_SCHEMA = 1
RESULT_SCHEMA = 1
DEFAULT_REPORT_DIR = Path(__file__).resolve().parents[2] / "Training" / "Experiments" / "Benchmarks"


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for chunk in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def read_benchmark(path: Path) -> tuple[dict, str]:
    value = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(value, dict) or value.get("schema_version") != BENCHMARK_SCHEMA:
        raise ValueError("Benchmark must be an object with schema_version=1.")
    name = value.get("id")
    version = value.get("revision")
    behavior = value.get("behavior_name")
    matches = value.get("matches")
    seed = value.get("seed")
    arguments = value.get("env_args")
    step_limit = value.get("max_environment_steps_per_match", 200000)
    if not isinstance(name, str) or not re.fullmatch(r"[a-z0-9][a-z0-9_-]{0,63}", name):
        raise ValueError("Benchmark id must be a lowercase slug (up to 64 characters).")
    if type(version) is not int or not 1 <= version <= 999:
        raise ValueError("Benchmark revision must be an integer in 1..999.")
    if not isinstance(behavior, str) or not re.fullmatch(r"[A-Za-z0-9._-]{1,80}", behavior):
        raise ValueError("Benchmark behavior_name is invalid.")
    if type(matches) is not int or not 1 <= matches <= 10000:
        raise ValueError("Benchmark matches must be an integer in 1..10000.")
    if type(seed) is not int or not 0 <= seed <= 2**31 - 1:
        raise ValueError("Benchmark seed must be an integer in 0..2^31-1.")
    if type(step_limit) is not int or not 1 <= step_limit <= 2000000:
        raise ValueError("Benchmark step limit must be an integer in 1..2000000.")
    if (not isinstance(arguments, list) or len(arguments) > 64 or
            any(not isinstance(a, str) or not a or len(a) > 512 for a in arguments)):
        raise ValueError("Benchmark env_args must be an array of short nonempty strings.")
    if any(not arg.startswith("--rl-") for arg in arguments):
        raise ValueError("Only --rl-* scenario arguments may be set in a benchmark.")
    if len({arg.split("=", 1)[0] for arg in arguments}) != len(arguments):
        raise ValueError("Benchmark includes duplicate environment flags.")
    canonical = json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False)
    return value, hashlib.sha256(canonical.encode("utf-8")).hexdigest()


def evaluate(recipe: Path, candidate: Path, opponent: Path, environment: Path,
             build_id: str, run_id: str, output_root: Path) -> Path:
    benchmark, recipe_hash = read_benchmark(recipe)
    artifacts = {"candidate": candidate, "opponent": opponent, "environment": environment}
    for name, source in artifacts.items():
        if not source.is_file():
            raise ValueError(f"{name} file does not exist: {source}")
    hashes = {key: sha256_file(value) for key, value in artifacts.items()}

    # The canonical evaluator is imported only for actual evaluation, so recipe
    # validation and --help do not need the ML-Agents/ONNX runtime installed.
    from bees_continual_evaluate import EVALUATION_PROTOCOL_VERSION, run_match_group

    result = run_match_group(
        environment_path=environment,
        candidate_model_path=candidate,
        opponent_model_path=opponent,
        matches=benchmark["matches"],
        behavior_name=benchmark["behavior_name"],
        env_args=benchmark["env_args"],
        seed=benchmark["seed"],
        worker_id=0,
        timeout_wait=300,
        onnx_provider="CPUExecutionProvider",
        max_environment_steps_per_match=benchmark.get(
            "max_environment_steps_per_match", 200000
        ),
    )
    stamp = datetime.now(timezone.utc)
    record = {
        "schema_version": RESULT_SCHEMA,
        "evaluated_at": stamp.isoformat(),
        "benchmark": {
            "id": benchmark["id"], "revision": benchmark["revision"],
            "sha256": recipe_hash, "seed": benchmark["seed"],
            "matches": benchmark["matches"], "env_args": benchmark["env_args"],
            "behavior_name": benchmark["behavior_name"],
        },
        "evaluation_protocol_version": EVALUATION_PROTOCOL_VERSION,
        "evaluation_code_sha256": sha256_file(Path(__file__).with_name("bees_continual_evaluate.py")),
        "run_id": run_id,
        "candidate_path": str(candidate),
        "candidate_sha256": hashes["candidate"],
        "opponent_path": str(opponent),
        "opponent_sha256": hashes["opponent"],
        "build_id": build_id,
        "environment_path": str(environment),
        "environment_executable_sha256": hashes["environment"],
        "metrics": result.to_dict(),
    }
    output_root.mkdir(parents=True, exist_ok=True)
    name = stamp.strftime("%Y%m%dT%H%M%S") + "-" + uuid.uuid4().hex + ".json"
    destination = output_root / name
    fd, temporary = tempfile.mkstemp(prefix=".benchmark-", suffix=".json", dir=output_root)
    try:
        with os.fdopen(fd, "w", encoding="utf-8") as out:
            json.dump(record, out, indent=2, sort_keys=True)
            out.write("\n")
        os.replace(temporary, destination)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)
    return destination


def candidate_for_run(registry: Path, run_id: str) -> Path:
    # The registry already owns model/run provenance. Read it without creating
    # another model index or changing promotion/checkpoint state.
    with sqlite3.connect(registry.resolve().as_uri() + "?mode=ro", uri=True) as db:
        row = db.execute(
            "SELECT artifact_path, artifact_sha256 FROM models "
            "WHERE training_run_id = ? ORDER BY training_step DESC, created_at DESC, model_id DESC LIMIT 1",
            (run_id,),
        ).fetchone()
    if row is None:
        raise ValueError(f"No exported checkpoint is registered for run {run_id}.")
    candidate = Path(row[0]).resolve()
    if not candidate.is_file() or sha256_file(candidate) != row[1]:
        raise ValueError(f"Registered checkpoint is missing or changed: {candidate}")
    return candidate


def main() -> int:
    parser = argparse.ArgumentParser(description="Fixed-seed independent Bees combat evaluation.")
    parser.add_argument("--benchmark", type=Path, required=True, help="Versioned benchmark JSON.")
    selection = parser.add_mutually_exclusive_group(required=True)
    selection.add_argument("--candidate", type=Path, help="Exported candidate ONNX model.")
    selection.add_argument("--candidate-run", help="Use the latest registered export from this stopped run.")
    parser.add_argument("--registry", type=Path, default=DEFAULT_REPORT_DIR.parents[1] / "registry.sqlite3")
    parser.add_argument("--opponent", type=Path, required=True, help="Fixed reference ONNX model.")
    parser.add_argument("--env", type=Path, required=True, help="Matching Unity evaluation executable.")
    parser.add_argument("--build-id", required=True, help="Unity build identity; keeps results comparable.")
    parser.add_argument("--run-id", default="", help="Optional training run for web cross-reference.")
    parser.add_argument("--output-root", type=Path, default=DEFAULT_REPORT_DIR)
    args = parser.parse_args()
    if not args.build_id.strip():
        parser.error("--build-id must not be empty.")
    if args.candidate_run and args.run_id and args.candidate_run != args.run_id:
        parser.error("--run-id must match --candidate-run.")
    candidate = candidate_for_run(args.registry, args.candidate_run) if args.candidate_run else args.candidate.resolve()
    result = evaluate(
        args.benchmark.resolve(), candidate, args.opponent.resolve(),
        args.env.resolve(), args.build_id.strip(), (args.candidate_run or args.run_id).strip(), args.output_root.resolve(),
    )
    print(result)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
