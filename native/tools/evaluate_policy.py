"""Measure a model on fixed source Levels without updating policy weights.

The native gameplay runner, policy process, and durable evaluation sink own
simulation and outcome attribution. This wrapper only pins inputs, selects
independent authored scenarios, and publishes a verified completion receipt.
"""

import argparse
import json
import math
import os
from pathlib import Path
import shutil
import subprocess
import uuid

from native_training_artifacts import file_sha256, sync_directory, atomic_json


def resolved(base, path):
    path = Path(path)
    return str((base / path).resolve())


def setup_config(source_path, manifest, output):
    source = json.loads(source_path.read_text(encoding="utf-8"))
    if not isinstance(source, dict) or not isinstance(source.get("run_id"), str) or not source["run_id"].strip():
        raise ValueError("evaluation run configuration requires a source run ID")
    if (source.get("hivemind") is not None or source.get("model_updates") or
            source.get("recover_notifications") or source.get("recover_outcome_journals") or
            "rollout_bundles" in source or
            any(key in source for key in ("rollout_worlds", "rollout_world_start", "rollout_workers"))):
        raise ValueError("evaluation must use explicit, unrecovered model-only Levels")
    episodes = source.get("episodes_per_world", 1)
    levels = source.get("levels")
    if type(episodes) is not int or not 1 <= episodes <= 10000 or not isinstance(levels, list) or not levels:
        raise ValueError("evaluation requires Levels and a positive episode count")
    for level in levels:
        if not isinstance(level, dict) or not isinstance(level.get("scene"), str):
            raise ValueError("evaluation Level requires an authored scene")
        is_single = type(level.get("seed")) is int and level.get("episode_seeds") is None
        is_series = level.get("seed") is None and isinstance(level.get("episode_seeds"), list)
        if not (is_single or is_series):
            raise ValueError("holdout Level must have an explicit seed or complete episode seed plan")
        seeds = [level["seed"]] if is_single else level["episode_seeds"]
        if ((is_series and len(seeds) != episodes) or any(
                type(seed) is not int or not 0 <= seed < (1 << 64) for seed in seeds)):
            raise ValueError("holdout seeds must be valid unsigned 64-bit integers")
        level["scene"] = resolved(source_path.parent, level["scene"])
    # Native-authored gameplay and MA-POCA Levels share the same Stage asset
    # contract. A holdout may use an embedded, authoritative native_stage or
    # the two legacy exported Stage files, but never a mixture. Preserve the
    # embedded collision matrix/palette unchanged when updating model weights.
    native_stage = source.get("native_stage")
    if native_stage is not None:
        if (not isinstance(native_stage, dict) or
                source.get("collision_matrix") is not None or
                source.get("stage_palette") is not None):
            raise ValueError("holdout native_stage cannot mix legacy Stage asset paths")
    else:
        for key in ("collision_matrix", "stage_palette"):
            if not isinstance(source.get(key), str) or not source[key]:
                raise ValueError(f"evaluation requires {key} or native_stage")
            source[key] = resolved(source_path.parent, source[key])
    if not isinstance(source.get("policy_worker"), str) or not source["policy_worker"]:
        raise ValueError("evaluation requires policy_worker")
    source["policy_worker"] = resolved(source_path.parent, source["policy_worker"])
    python = source.get("python")
    if not isinstance(python, str) or not python:
        raise ValueError("evaluation requires native policy Python")
    if Path(python).is_absolute() or len(Path(python).parts) > 1:
        source["python"] = resolved(source_path.parent, python)
    source["run_id"] = f"{source['run_id']}-holdout-{uuid.uuid4().hex}"
    source["model_manifest"] = str(manifest)
    source["policy_capture"] = None
    source["model_updates"] = []
    source["connection_checkpoint"] = str(output / "connection-checkpoint.json")
    source["notifications"] = str(output / "events.jsonl")
    source["rollout_summary"] = str(output / "rollout-summary.json")
    return source, len(levels) * episodes


def evaluate(source_path, manifest, output, learner_side, binary, policy_seed):
    manifest = manifest.resolve(strict=True)
    source_path = source_path.resolve(strict=True)
    manifest_sha = file_sha256(manifest)
    source_sha = file_sha256(source_path)
    config, expected = setup_config(source_path, manifest, output)
    output.mkdir(parents=True, exist_ok=False)
    generated = output / "run.json"
    with generated.open("x", encoding="utf-8") as stream:
        json.dump(config, stream, indent=2)
        stream.write("\n")
        stream.flush()
        os.fsync(stream.fileno())
    sync_directory(output)
    if binary is None:
        cargo = shutil.which("cargo")
        if cargo is None:
            raise ValueError("cargo is unavailable; provide --rollout-binary")
        command = [cargo, "run", "--release", "--manifest-path",
                   str(Path(__file__).resolve().parents[1] / "Cargo.toml"),
                   "-p", "bees-host", "--example", "policy_rollout", "--", str(generated)]
    else:
        command = [str(binary.resolve(strict=True)), str(generated)]
    # Pin the stochastic MA-POCA action sampler across evaluated model
    # generations. The environment seed plans remain fixed in the source
    # Level config; a distinct inference RNG controls policy sampling.
    env = dict(os.environ)
    env["BEES_NATIVE_EVALUATION_SEED"] = str(policy_seed)
    with (output / "evaluation.log").open("x", encoding="utf-8") as log:
        subprocess.run(command, check=True, stdout=log, stderr=subprocess.STDOUT, env=env)
    summary_path = output / "rollout-summary.json"
    summary = json.loads(summary_path.read_text(encoding="utf-8"))
    score = summary.get("evaluation")
    throughput = summary.get("throughput")
    if (summary.get("kind") != "run_final" or summary.get("run_id") != config["run_id"] or
            summary.get("policy_manifest_sha256") != manifest_sha or
            summary.get("completed_episodes") != expected or
            any(summary.get(key) != 0 for key in ("failed_worlds", "aborted_worlds", "running_worlds", "model_updates_applied")) or
            summary.get("failed_window") is not None or summary.get("cleanup_errors") or
            not isinstance(score, dict) or not isinstance(throughput, dict) or
            score.get("episodes_finished") != expected or score.get("episodes_aborted") != 0 or
            type(throughput.get("successful_world_steps")) is not int or
            throughput["successful_world_steps"] <= 0 or
            type(throughput.get("successful_policy_rows")) is not int or
            throughput["successful_policy_rows"] <= 0 or
            type(throughput.get("wall_seconds")) not in (int, float) or
            not math.isfinite(throughput["wall_seconds"]) or throughput["wall_seconds"] <= 0 or
            type(throughput.get("world_steps_per_wall_second")) not in (int, float) or
            not math.isfinite(throughput["world_steps_per_wall_second"])):
        raise ValueError("native evaluation did not finish all expected policy episodes")
    wins = score.get("victories_by_side", {}).get(str(learner_side), 0)
    draws = score.get("draws")
    if (type(wins) is not int or not 0 <= wins <= expected or
            type(draws) is not int or not 0 <= draws <= expected or wins + draws > expected):
        raise ValueError("native evaluation side outcomes are invalid")
    if file_sha256(manifest) != manifest_sha or file_sha256(source_path) != source_sha:
        raise ValueError("evaluation input changed during the native run")
    receipt = {
        "schema_version": 1, "kind": "native_policy_evaluation_complete",
        "source_run_config": str(source_path), "source_run_config_sha256": source_sha,
        "model_manifest": str(manifest), "model_manifest_sha256": manifest_sha,
        "learner_side": learner_side, "policy_sampling_seed": policy_seed,
        "run_id": config["run_id"],
        "episodes": expected, "worlds": len(config["levels"]),
        "side_victories": wins, "draws": draws,
        "side_win_rate": wins / expected, "draw_rate": draws / expected,
        "mean_episode_seconds": summary.get("mean_episode_seconds"),
        "world_steps": throughput["successful_world_steps"],
        "policy_rows": throughput["successful_policy_rows"],
        "wall_seconds": throughput["wall_seconds"],
        "world_steps_per_wall_second": throughput["world_steps_per_wall_second"],
        "evaluation": score,
        "rollout_summary_sha256": file_sha256(summary_path),
    }
    atomic_json(output / "evaluation-result.json", receipt)
    return receipt


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("run_config", type=Path)
    parser.add_argument("model_manifest", type=Path)
    parser.add_argument("output_dir", type=Path)
    parser.add_argument("--learner-side", type=int, required=True)
    parser.add_argument("--policy-seed", type=int, default=0,
                        help="fixed stochastic action-sampling seed across checkpoints")
    parser.add_argument("--rollout-binary", type=Path)
    args = parser.parse_args()
    if args.learner_side not in (1, 2):
        parser.error("--learner-side must identify the source side 1 or 2")
    if not 0 <= args.policy_seed < (1 << 63):
        parser.error("--policy-seed must be a nonnegative int63")
    result = evaluate(args.run_config, args.model_manifest,
                      args.output_dir.resolve(), args.learner_side,
                      args.rollout_binary, args.policy_seed)
    print(json.dumps(result), flush=True)


if __name__ == "__main__":
    main()
