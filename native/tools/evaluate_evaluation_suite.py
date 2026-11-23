#!/usr/bin/env python3
"""Evaluate one policy on heterogeneous authentic native Levels and Stages.

Native Stage collision palettes, fragment bounds, and fixed-step contracts
can differ across source games. Each source is therefore run through the
existing evaluate_policy.py in its OWN real CombatWorld process, instead of
silently forcing different maps into one shared Stage configuration.
"""

import argparse
import json
import math
from pathlib import Path
import time

from evaluate_policy import evaluate
from native_training_artifacts import (
    atomic_json, check_native_input_dependencies, file_sha256,
    native_manifest_model_dependencies, native_headless_environment,
    sync_directory,
)


def evaluate_suite(sources: list[Path], model_manifest: Path, output: Path,
                   learner_side: int, seed: int, binary: Path | None,
                   physics_workers: int | None, device: str | None,
                   resume: bool = False) -> dict:
    if not 2 <= len(sources) <= 64:
        raise ValueError("heterogeneous holdout needs 2..64 authored native source runs")
    if learner_side not in (1, 2) or not 0 <= seed < (1 << 63):
        raise ValueError("invalid learner faction or fixed policy sampler seed")
    sources = [source.resolve(strict=True) for source in sources]
    if len(set(sources)) != len(sources):
        raise ValueError("a heterogeneous holdout cannot silently double-count one source")
    manifest = model_manifest.resolve(strict=True)
    manifest_sha = file_sha256(manifest)
    original_sha = [file_sha256(source) for source in sources]
    env = native_headless_environment()
    model_store = env.get("BEES_NATIVE_MODEL_STORE")
    model_dependencies = native_manifest_model_dependencies(manifest, model_store)
    binary = binary.resolve(strict=True) if binary is not None else None
    request = {
        "schema_version": 1,
        "kind": "native_heterogeneous_evaluation_request",
        "source_runs": [
            {"path": str(path), "sha256": digest}
            for path, digest in zip(sources, original_sha)
        ],
        "model_manifest": str(manifest),
        "model_manifest_sha256": manifest_sha,
        "model_dependencies": model_dependencies,
        "model_store": model_store,
        "learner_side": learner_side,
        "policy_sampling_seed": seed,
        "rollout_binary": str(binary) if binary else None,
        "rollout_binary_sha256": file_sha256(binary) if binary else None,
        "physics_workers": physics_workers,
        "inference_device": device,
    }
    output = output.resolve()
    if not output.parent.is_dir() or output.is_symlink():
        raise ValueError("heterogeneous holdout parent or destination is unsafe")
    if resume:
        if not output.is_dir() or output.is_symlink():
            raise FileNotFoundError("resume needs the existing owned native evaluation directory")
        if json.loads((output / "suite-request.json").read_text(encoding="utf-8")) != request:
            raise ValueError("cannot resume against changed model, source, native binary or settings")
        if (output / "evaluation-suite-result.json").exists():
            raise FileExistsError("heterogeneous evaluation is already fully published")
    else:
        if output.exists():
            raise FileExistsError("heterogeneous holdout requires a fresh output directory")
        output.mkdir()
        atomic_json(output / "suite-request.json", request)
        sync_directory(output)
        sync_directory(output.parent)
    begun = time.monotonic()
    reused = 0
    results = []
    for index, source in enumerate(sources, start=1):
        # Every scenario uses a separate native Stage; only verified,
        # immutable completed receipts may be reused after interruption.
        # Leave incomplete attempts intact for diagnostics and start a
        # fresh attempt rather than deleting an interrupted rollout.
        base = output / f"scenario-{index:03d}"
        destination = base
        attempt = 0
        while destination.exists() or destination.is_symlink():
            if destination.is_symlink() or not destination.is_dir():
                raise ValueError("native scenario output path has invalid ownership")
            result_path = destination / "evaluation-result.json"
            if result_path.is_file() and not result_path.is_symlink():
                result = json.loads(result_path.read_text(encoding="utf-8"))
                if (result.get("kind") != "native_policy_evaluation_complete" or
                        result.get("source_run_config_sha256") != original_sha[index - 1] or
                        result.get("model_manifest_sha256") != manifest_sha or
                        result.get("learner_side") != learner_side or
                        result.get("policy_sampling_seed") != seed or
                        result.get("source_run_config") != str(source) or
                        result.get("model_dependencies") != model_dependencies or
                        result.get("inference_device_override") != device or
                        result.get("requested_physics_workers") != (
                            physics_workers if physics_workers is not None
                            else json.loads(source.read_text(encoding="utf-8")).get("physics_workers", 1)
                        )):
                    raise ValueError(f"native scenario {index} has a conflicting completed receipt")
                check_native_input_dependencies({
                    "source_dependencies": result["source_dependencies"],
                })
                reused += 1
                break
            if result_path.exists() or result_path.is_symlink():
                raise ValueError(f"native scenario {index} has an invalid completion marker")
            attempt += 1
            destination = output / f"scenario-{index:03d}-attempt-{attempt:03d}"
        else:
            # No complete result was reusable. The existing evaluator
            # exclusively claims a new directory and publishes its own
            # completion marker after the actual physical rollout.
            result = evaluate(
                source, manifest, destination, learner_side, binary, seed,
                physics_workers, device,
            )
            result_path = destination / "evaluation-result.json"
        if (result.get("kind") != "native_policy_evaluation_complete" or
                result.get("source_run_config_sha256") != original_sha[index - 1] or
                result.get("model_manifest_sha256") != manifest_sha or
                result.get("learner_side") != learner_side or
                result.get("policy_sampling_seed") != seed or
                not result_path.is_file()):
            raise ValueError(f"heterogeneous holdout scenario {index} is unverified")
        results.append({
            "scenario": index,
            "source_run": str(source),
            "source_run_sha256": original_sha[index - 1],
            "result_path": result_path.relative_to(output).as_posix(),
            "result_sha256": file_sha256(result_path),
            "result": result,
        })
    # Recheck ALL pinned physical scenes, Stage files, model inputs and
    # source configurations at publication, including early scenarios.
    for source, digest, row in zip(sources, original_sha, results):
        if file_sha256(source) != digest:
            raise ValueError(f"{source}: source changed after native evaluation")
        check_native_input_dependencies({
            "source_dependencies": row["result"]["source_dependencies"],
        })
    if (file_sha256(manifest) != manifest_sha or
            native_manifest_model_dependencies(manifest, model_store) != model_dependencies):
        raise ValueError("policy manifest or weights changed during heterogeneous evaluation")
    for row in results:
        if file_sha256(output / row["result_path"]) != row["result_sha256"]:
            raise ValueError("native scenario result changed before suite publication")
    receipts = [row["result"] for row in results]
    episodes = sum(row["episodes"] for row in receipts)
    victories = sum(row["side_victories"] for row in receipts)
    draws = sum(row["draws"] for row in receipts)
    steps = sum(row["world_steps"] for row in receipts)
    policy_rows = sum(row["policy_rows"] for row in receipts)
    measured = sum(row["wall_seconds"] for row in receipts)
    execution_seconds = sum(row["execution_wall_seconds"] for row in receipts)
    if (episodes <= 0 or not 0 <= victories + draws <= episodes or steps <= 0 or
            policy_rows <= 0 or not math.isfinite(measured) or measured <= 0 or
            not math.isfinite(execution_seconds) or execution_seconds <= 0):
        raise ValueError("native heterogeneous holdout has incomplete measured results")
    mission_totals = {}
    for result in receipts:
        for kind, counts in result.get("mission_outcomes", {}).items():
            total = mission_totals.setdefault(kind, {"attempts": 0, "completions": 0})
            total["attempts"] += counts["attempts"]
            total["completions"] += counts["completions"]
    for total in mission_totals.values():
        total["completion_rate"] = total["completions"] / total["attempts"]
    all_attributed = all(
        row.get("mission_attempts") == row["episodes"] and
        type(row.get("learner_mission_successes")) is int
        for row in receipts
    )
    mission_successes = sum(row["learner_mission_successes"] for row in receipts) if all_attributed else None
    # Every row was executed sequentially. Unlike parallel machine
    # occupancy, these measured intervals can be summed meaningfully.
    elapsed = time.monotonic() - begun
    result = {
        "schema_version": 1,
        "kind": "native_heterogeneous_evaluation_complete",
        "model_manifest": str(manifest),
        "model_manifest_sha256": manifest_sha,
        "model_dependencies": model_dependencies,
        "learner_side": learner_side,
        "policy_sampling_seed": seed,
        "scenarios": len(receipts),
        "completed_scenarios_reused": reused,
        "resumed": resume,
        "episodes": episodes,
        "side_victories": victories, "draws": draws,
        "side_win_rate": victories / episodes,
        "draw_rate": draws / episodes,
        # A macro rate gives each original authored scenario one vote,
        # unlike the pooled episode rate. Publish both; unequal episode
        # counts must not conceal failure on a smaller campaign map.
        "mean_scenario_win_rate": sum(
            row["side_win_rate"] for row in receipts
        ) / len(receipts),
        "worst_scenario_win_rate": min(
            row["side_win_rate"] for row in receipts
        ),
        "world_steps": steps, "policy_rows": policy_rows,
        "measured_simulation_wall_seconds": measured,
        "measured_simulation_wall_scope": "sum_of_sequential_native_evaluation_wall_seconds",
        "measured_simulation_world_steps_per_second": steps / measured,
        "measured_execution_wall_seconds": execution_seconds,
        "measured_execution_world_steps_per_second": steps / execution_seconds,
        # A resume covers only new execution in this invocation. Claiming
        # a suite-wide elapsed SPS here would count reused work without its
        # historical elapsed cost.
        "current_invocation_wall_seconds": elapsed,
        **({"total_elapsed_wall_seconds": elapsed,
            "elapsed_world_steps_per_second": steps / elapsed}
           if reused == 0 else {}),
        "by_source": [{
            "scenario": row["scenario"],
            "source_run": row["source_run"],
            "source_run_sha256": row["source_run_sha256"],
            "result_path": row["result_path"],
            "result_sha256": row["result_sha256"],
            "episodes": row["result"]["episodes"],
            "side_win_rate": row["result"]["side_win_rate"],
            "draw_rate": row["result"]["draw_rate"],
            "by_scene_sha256": row["result"]["by_scene_sha256"],
            "world_steps": row["result"]["world_steps"],
            "measured_simulation_wall_seconds": row["result"]["wall_seconds"],
            **({"learner_mission_success_rate": row["result"]["learner_mission_success_rate"]}
               if "learner_mission_success_rate" in row["result"] else {}),
        } for row in results],
        **({"mission_outcomes": mission_totals} if mission_totals else {}),
        **({"mission_attempts": episodes,
            "learner_mission_successes": mission_successes,
            "learner_mission_success_rate": mission_successes / episodes,
            "mean_scenario_mission_success_rate": sum(
                row["learner_mission_success_rate"] for row in receipts
            ) / len(receipts),
            "worst_scenario_mission_success_rate": min(
                row["learner_mission_success_rate"] for row in receipts
            )}
           if all_attributed else {}),
    }
    # Publication marker is LAST. An interrupted source or failed native
    # process leaves the completed individual receipts inspectable but
    # cannot masquerade as a fully evaluated suite.
    atomic_json(output / "evaluation-suite-result.json", result)
    sync_directory(output)
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("model_manifest", type=Path)
    parser.add_argument("output_dir", type=Path)
    parser.add_argument("run_configs", nargs="+", type=Path)
    parser.add_argument("--learner-side", type=int, required=True)
    parser.add_argument("--policy-seed", type=int, default=0)
    parser.add_argument("--rollout-binary", type=Path)
    parser.add_argument("--physics-workers", type=int)
    parser.add_argument("--inference-device", choices=("cpu", "cuda:0"))
    parser.add_argument("--resume", action="store_true",
                        help="continue unfinished scenarios from an unchanged pinned suite request")
    args = parser.parse_args()
    print(json.dumps(evaluate_suite(
        args.run_configs, args.model_manifest, args.output_dir, args.learner_side,
        args.policy_seed, args.rollout_binary, args.physics_workers,
        args.inference_device, resume=args.resume,
    ), sort_keys=True), flush=True)


if __name__ == "__main__":
    main()
