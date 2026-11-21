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

from native_mission_contract import level_has_goal, verified_policy_observations
from native_training_artifacts import (file_sha256, sync_directory, atomic_json,
                                       native_input_dependencies,
                                       check_native_input_dependencies,
                                       native_headless_environment,
                                       native_manifest_model_dependencies,
                                       check_native_model_dependencies)


def resolved(base, path):
    path = Path(path)
    return str((base / path).resolve())


def setup_config(source_path, manifest, output, physics_workers=None):
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
        # Resolve the location relative to this authored run, but preserve
        # a virtual-environment Python symlink's launch path.
        source["python"] = os.path.abspath(source_path.parent / python)
    source["run_id"] = f"{source['run_id']}-holdout-{uuid.uuid4().hex}"
    source["model_manifest"] = str(manifest)
    if physics_workers is not None:
        if type(physics_workers) is not int or not 1 <= physics_workers <= 128:
            raise ValueError("holdout CPU physics workers must be within 1..128")
        source["physics_workers"] = physics_workers
    source["policy_capture"] = None
    source["model_updates"] = []
    # Holdouts are fresh, completed episodes. Inheriting an interactive
    # source's checkpoint path would add gameplay persistence costs and
    # could attach evaluation to another live World.
    source["host_recovery_checkpoint"] = None
    source["connection_checkpoint"] = str(output / "connection-checkpoint.json")
    source["notifications"] = str(output / "events.jsonl")
    source["rollout_summary"] = str(output / "rollout-summary.json")
    return source, len(levels) * episodes


def per_scene_results(score, learner_side, episodes):
    """Verify real source-scene terminal counts before publishing comparisons.

    These counters come from durable EpisodeFinal notifications after disk
    acknowledgement. They never inspect PPO observations or infer outcomes
    from a mutable gameplay state or model reward.
    """
    scenes = score.get("by_scene_sha256")
    if not isinstance(scenes, dict) or not scenes:
        raise ValueError("native evaluation has no authoritative scene breakdown")
    totals = {"episodes": 0, "draws": 0, "timed_out": 0}
    outcome_totals = {}
    results = {}
    for digest, row in scenes.items():
        if (not isinstance(digest, str) or len(digest) != 64 or
                any(c not in "0123456789abcdef" for c in digest) or
                not isinstance(row, dict)):
            raise ValueError("invalid native evaluation scene identity")
        n = row.get("episodes_finished")
        d = row.get("draws")
        t = row.get("episodes_timed_out")
        seconds = row.get("total_episode_seconds")
        victories = row.get("victories_by_side")
        if (any(type(x) is not int for x in (n, d, t)) or
                n <= 0 or d < 0 or t < 0 or d > n or t > n or
                type(seconds) not in (int, float) or
                not math.isfinite(seconds) or seconds < 0 or
                not isinstance(victories, dict)):
            raise ValueError("invalid native evaluation scene terminal totals")
        side_wins = 0
        for side, wins in victories.items():
            if (not isinstance(side, str) or not side.isdecimal() or
                    type(wins) is not int or not 0 <= wins <= n):
                raise ValueError("invalid native scene winner attribution")
            outcome_totals[side] = outcome_totals.get(side, 0) + wins
            side_wins += wins
        if side_wins + d != n:
            raise ValueError("native scene has missing or repeated episode outcomes")
        totals["episodes"] += n
        totals["draws"] += d
        totals["timed_out"] += t
        wins = victories.get(str(learner_side), 0)
        mission_counts = row.get("mission_outcomes")
        mission_outcomes = None
        if mission_counts is not None:
            if not isinstance(mission_counts, dict):
                raise ValueError("native scene has invalid physical mission totals")
            mission_outcomes = {}
            attempts = 0
            role_attempts = 0
            learner_successes = 0
            all_roles_recorded = True
            for kind, counts in mission_counts.items():
                if (kind not in ("survival", "demolition", "escort", "extraction") or
                        not isinstance(counts, dict)):
                    raise ValueError("native scene has unknown mission outcome category")
                total, completed = counts.get("attempts"), counts.get("completions")
                if (type(total) is not int or type(completed) is not int or
                        total < 1 or not 0 <= completed <= total):
                    raise ValueError("native scene has invalid mission completion counts")
                attempts += total
                by_side = counts.get("by_objective_side")
                verified_sides = None
                if by_side is not None:
                    if not isinstance(by_side, dict):
                        raise ValueError("invalid native physical mission objective-side counters")
                    verified_sides = {}
                    side_attempts = side_completions = 0
                    for side_id, split in by_side.items():
                        if (side_id not in ("1", "2") or not isinstance(split, dict) or
                                type(split.get("attempts")) is not int or
                                type(split.get("completions")) is not int or
                                split["attempts"] < 0 or
                                not 0 <= split["completions"] <= split["attempts"]):
                            raise ValueError("invalid native mission objective-side totals")
                        count, success = split["attempts"], split["completions"]
                        side_attempts += count
                        side_completions += success
                        role_attempts += count
                        learner_successes += (
                            success if int(side_id) == learner_side else count - success
                        )
                        if count:
                            verified_sides[side_id] = {
                                "attempts": count, "completions": success,
                                "completion_rate": success / count,
                            }
                    if side_attempts != total or side_completions != completed:
                        raise ValueError("native mission goal side totals disagree with episode outcomes")
                else:
                    all_roles_recorded = False
                mission_outcomes[kind] = {
                    "attempts": total,
                    "completions": completed,
                    "completion_rate": completed / total,
                    **({"by_objective_side": verified_sides}
                       if verified_sides is not None else {}),
                }
            if attempts > n:
                raise ValueError("physical mission attempts exceed completed episodes")
            if all_roles_recorded and role_attempts != attempts:
                raise ValueError("native mission objective-side total is incomplete")
        results[digest] = {
            "episodes": n,
            "side_victories": wins,
            "draws": d,
            "timed_out": t,
            "side_win_rate": wins / n,
            "draw_rate": d / n,
            "timeout_rate": t / n,
            "mean_episode_seconds": seconds / n,
            **({"mission_outcomes": mission_outcomes}
               if mission_outcomes is not None else {}),
            **({
                "mission_attempts": attempts,
                "learner_mission_successes": learner_successes,
                "learner_mission_success_rate": learner_successes / attempts,
            } if mission_outcomes is not None and all_roles_recorded and attempts else {}),
        }
    global_wins = score.get("victories_by_side")
    if (totals["episodes"] != episodes or
            totals["draws"] != score.get("draws") or
            totals["timed_out"] != score.get("episodes_timed_out") or
            not isinstance(global_wins, dict) or
            any(outcome_totals.get(side, 0) != wins for side, wins in global_wins.items()) or
            any(global_wins.get(side, 0) != wins for side, wins in outcome_totals.items())):
        raise ValueError("native scene outcomes disagree with durable global evaluation")
    return results


def evaluate(source_path, manifest, output, learner_side, binary, policy_seed,
             physics_workers=None, inference_device=None):
    manifest = manifest.resolve(strict=True)
    source_path = source_path.resolve(strict=True)
    manifest_sha = file_sha256(manifest)
    source_sha = file_sha256(source_path)
    source_dependencies = native_input_dependencies({"path": str(source_path)})
    env = native_headless_environment()
    model_store = env.get("BEES_NATIVE_MODEL_STORE")
    model_dependencies = native_manifest_model_dependencies(manifest, model_store)
    if inference_device is not None:
        if inference_device not in ("cpu", "cuda:0"):
            raise ValueError("native evaluation inference device must be cpu or cuda:0")
        # An ONNX execution-provider manifest cannot be relocated through
        # PyTorch's device selector. Never report a CPU evaluation while an
        # ONNX opponent still requires a CUDA/ROCm execution provider.
        entries = json.loads(manifest.read_text(encoding="utf-8")).get("models", [])
        if not isinstance(entries, list) or not entries:
            raise ValueError("native evaluation manifest has no model slots")
        gpu_capable = False
        for entry in entries:
            if (not isinstance(entry, dict) or entry.get("backend") not in
                    ("torchscript", "mlagents", "onnx")):
                raise ValueError("invalid model backend in native evaluation manifest")
            if entry["backend"] == "onnx":
                providers = entry.get("providers", ["CPUExecutionProvider"])
                if (not isinstance(providers, list) or not providers or
                        (inference_device == "cpu" and
                         providers[0] != "CPUExecutionProvider")):
                    raise ValueError("native ONNX provider cannot satisfy requested CPU evaluation")
                gpu_capable |= providers[0] != "CPUExecutionProvider"
            else:
                gpu_capable = True
        if inference_device == "cuda:0" and not gpu_capable:
            raise ValueError("GPU evaluation cannot accelerate CPU-only ONNX models")
    config, expected = setup_config(source_path, manifest, output, physics_workers)
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
    # Fixed-reference comparisons must not inherit a shell's unrelated
    # model-placement override. Only the recorded --inference-device
    # may relocate PyTorch weights at evaluation time.
    env.pop("BEES_NATIVE_INFERENCE_DEVICE_OVERRIDE", None)
    env["BEES_NATIVE_EVALUATION_SEED"] = str(policy_seed)
    # Device placement does not change the signed model manifest or its
    # checkpoint hash. It is explicit in the evaluation receipt because
    # CPU and GPU floating-point sampling may not be bit-identical.
    if inference_device is not None:
        env["BEES_NATIVE_INFERENCE_DEVICE_OVERRIDE"] = inference_device
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
    scenes = per_scene_results(score, learner_side, expected)
    authored_missions = any(level_has_goal(level) for level in config["levels"])
    observation_work = verified_policy_observations(
        summary.get("policy_observations"), require_goal=authored_missions,
    )
    mission_totals = {}
    if all("mission_outcomes" in scene for scene in scenes.values()):
        for scene in scenes.values():
            for kind, counts in scene["mission_outcomes"].items():
                total = mission_totals.setdefault(kind, {
                    "attempts": 0, "completions": 0,
                })
                total["attempts"] += counts["attempts"]
                total["completions"] += counts["completions"]
        for totals in mission_totals.values():
            totals["completion_rate"] = totals["completions"] / totals["attempts"]
    fully_attributed = all(
        "mission_attempts" in scene and
        scene["mission_attempts"] == scene["episodes"]
        for scene in scenes.values()
    )
    learner_objectives = (
        sum(scene["learner_mission_successes"] for scene in scenes.values())
        if fully_attributed else None
    )
    selected_workers = config.get("physics_workers", 1)
    if (throughput.get("requested_physics_workers") != selected_workers or
            type(throughput.get("peak_parallel_physics_workers")) is not int or
            throughput["peak_parallel_physics_workers"] < 0 or
            throughput["peak_parallel_physics_workers"] > selected_workers):
        raise ValueError("native evaluation physics worker accounting differs from its source")
    if file_sha256(manifest) != manifest_sha or file_sha256(source_path) != source_sha:
        raise ValueError("evaluation input changed during the native run")
    # The same authored scene, Stage collision assets and policy code used
    # by the native trainer must still be in place when the result becomes
    # authoritative. Otherwise a fixed holdout may silently measure another
    # environment while its run.json and policy manifest hashes still match.
    check_native_input_dependencies({"source_dependencies": source_dependencies})
    if native_manifest_model_dependencies(manifest, model_store) != model_dependencies:
        raise ValueError("native evaluation model paths changed during the holdout")
    receipt = {
        "schema_version": 1, "kind": "native_policy_evaluation_complete",
        "source_run_config": str(source_path), "source_run_config_sha256": source_sha,
        "source_dependencies": source_dependencies,
        "model_dependencies": model_dependencies,
        **({"model_store": model_store} if model_store else {}),
        "model_manifest": str(manifest), "model_manifest_sha256": manifest_sha,
        "learner_side": learner_side, "policy_sampling_seed": policy_seed,
        **({"inference_device_override": inference_device} if inference_device else {}),
        "run_id": config["run_id"],
        "episodes": expected, "worlds": len(config["levels"]),
        "side_victories": wins, "draws": draws,
        "side_win_rate": wins / expected, "draw_rate": draws / expected,
        "mean_episode_seconds": summary.get("mean_episode_seconds"),
        "world_steps": throughput["successful_world_steps"],
        "policy_rows": throughput["successful_policy_rows"],
        **({"policy_observations": observation_work}
           if observation_work is not None else {}),
        "wall_seconds": throughput["wall_seconds"],
        "world_steps_per_wall_second": throughput["world_steps_per_wall_second"],
        "requested_physics_workers": selected_workers,
        "peak_parallel_physics_workers": throughput["peak_parallel_physics_workers"],
        "parallel_physics_rounds": throughput.get("parallel_physics_rounds", 0),
        "evaluation": score,
        "by_scene_sha256": scenes,
        **({"mission_outcomes": mission_totals}
           if all("mission_outcomes" in scene for scene in scenes.values()) else {}),
        **({
            "mission_attempts": expected,
            "learner_mission_successes": learner_objectives,
            "learner_mission_success_rate": learner_objectives / expected,
        } if learner_objectives is not None else {}),
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
    parser.add_argument("--physics-workers", type=int,
                        help="persistent native CPU physics workers for this holdout (1..128)")
    parser.add_argument("--inference-device", choices=("cpu", "cuda:0"),
                        help="override PyTorch model placement without changing checkpoint or manifest")
    args = parser.parse_args()
    if args.learner_side not in (1, 2):
        parser.error("--learner-side must identify the source side 1 or 2")
    if not 0 <= args.policy_seed < (1 << 63):
        parser.error("--policy-seed must be a nonnegative int63")
    if args.physics_workers is not None and not 1 <= args.physics_workers <= 128:
        parser.error("--physics-workers must be between 1 and 128")
    result = evaluate(args.run_config, args.model_manifest,
                      args.output_dir.resolve(), args.learner_side,
                      args.rollout_binary, args.policy_seed, args.physics_workers,
                      args.inference_device)
    print(json.dumps(result), flush=True)


if __name__ == "__main__":
    main()
