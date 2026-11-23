#!/usr/bin/env python3
"""Evaluate one policy on heterogeneous authentic native Levels and Stages.

Native Stage collision palettes, fragment bounds, and fixed-step contracts
can differ across source games. Each source is therefore run through the
existing evaluate_policy.py in its OWN real CombatWorld process, instead of
silently forcing different maps into one shared Stage configuration.
"""

import argparse
from concurrent.futures import ThreadPoolExecutor
import json
import math
from pathlib import Path
import time

from evaluate_policy import evaluate, terminal_matchup
from native_training_artifacts import (
    atomic_json, check_native_input_dependencies, file_sha256,
    native_manifest_model_dependencies, native_headless_environment,
    sync_directory,
)


def evaluate_suite(sources: list[Path], model_manifest: Path, output: Path,
                   learner_side: int, seed: int, binary: Path | None,
                   physics_workers: int | None, device: str | None,
                   resume: bool = False, scenario_workers: int = 1,
                        candidate_slot: int | None = None,
                        reference_slot: int | None = None) -> dict:
    if not 2 <= len(sources) <= 64:
        raise ValueError("heterogeneous holdout needs 2..64 authored native source runs")
    if learner_side not in (1, 2) or not 0 <= seed < (1 << 63):
        raise ValueError("invalid learner faction or fixed policy sampler seed")
    if (type(scenario_workers) is not int or
            not 1 <= scenario_workers <= min(16, len(sources))):
        raise ValueError("scenario workers must be 1..16 and no more than authored scenarios")
    if scenario_workers > 1 and (binary is None or device != "cpu"):
        raise ValueError(
            "parallel native holdouts require an existing native binary and explicit CPU inference; "
            "GPU allocation remains owned by the host or Experiment Hub"
        )
    if (candidate_slot is None) != (reference_slot is None):
        raise ValueError("candidate and reference model slots must be specified together")
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
        **({"candidate_slot": candidate_slot, "reference_slot": reference_slot}
           if candidate_slot is not None else {}),
        **({"parallel_scenario_workers": scenario_workers}
           if scenario_workers > 1 else {}),
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
    def evaluate_one(index, source):
        # Every scenario owns its native World, source scene, subprocess
        # and atomic receipt. No Python or Rust gameplay state is shared.
        # Keep incomplete attempts for forensics and recovery, never
        # overwrite another worker's previously issued directory.
        base = output / f"scenario-{index:03d}"
        destination = base
        attempt = 0
        reused = False
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
                        (candidate_slot is not None and (
                            not isinstance(result.get("candidate_side_assignment"), dict) or
                            result["candidate_side_assignment"].get("candidate_slot") != candidate_slot or
                            result["candidate_side_assignment"].get("reference_slot") != reference_slot or
                            result["candidate_side_assignment"].get("candidate_physical_side") != learner_side
                        )) or
                        (candidate_slot is None and
                         result.get("candidate_side_assignment") is not None) or
                        (candidate_slot is not None and
                         not isinstance(result.get("candidate_terminal_matchup"), dict)) or
                        result.get("source_run_config") != str(source) or
                        result.get("model_dependencies") != model_dependencies or
                        result.get("inference_device_override") != device or
                        result.get("requested_physics_workers") != (
                            physics_workers if physics_workers is not None
                            else json.loads(source.read_text(encoding="utf-8")).get(
                                "physics_workers", 1
                            ))):
                    raise ValueError(
                        f"native scenario {index} has a conflicting completed receipt"
                    )
                check_native_input_dependencies({
                    "source_dependencies": result["source_dependencies"],
                })
                if candidate_slot is not None:
                    observed, by_scene = terminal_matchup(
                        destination, result, candidate_slot, reference_slot,
                    )
                    attributed = result["candidate_terminal_matchup"]
                    if (attributed.get("counts") != dict(observed) or
                            attributed.get("by_scene_sha256") != {
                                digest: dict(counts)
                                for digest, counts in by_scene.items()
                            }):
                        raise ValueError("completed model-role holdout differs from terminal journal")
                reused = True
                break
            if result_path.exists() or result_path.is_symlink():
                raise ValueError(f"native scenario {index} has an invalid completion marker")
            attempt += 1
            destination = output / f"scenario-{index:03d}-attempt-{attempt:03d}"
        else:
            # Uses the one original native evaluator. The frozen model
            # manifest and physical seeds are identical regardless of
            # evaluation concurrency or Windows/Linux process scheduling.
            result = evaluate(
                source, manifest, destination, learner_side, binary, seed,
                physics_workers, device, candidate_slot, reference_slot,
            )
            result_path = destination / "evaluation-result.json"
        if (result.get("kind") != "native_policy_evaluation_complete" or
                result.get("source_run_config_sha256") != original_sha[index - 1] or
                result.get("model_manifest_sha256") != manifest_sha or
                result.get("learner_side") != learner_side or
                result.get("policy_sampling_seed") != seed or
                not result_path.is_file()):
            raise ValueError(f"heterogeneous holdout scenario {index} is unverified")
        return ({
            "scenario": index,
            "source_run": str(source),
            "source_run_sha256": original_sha[index - 1],
            "result_path": result_path.relative_to(output).as_posix(),
            "result_sha256": file_sha256(result_path),
            "result": result,
        }, reused)

    # Workers are bounded and explicit; no unmeasured automatic oversub-
    # scription, no second learning loop, and no GPU sharing assumption.
    # Retrieve futures in source order so output is deterministic even
    # when slower maps complete after later short scenarios.
    if scenario_workers == 1:
        completed = [
            evaluate_one(index, source)
            for index, source in enumerate(sources, start=1)
        ]
    else:
        with ThreadPoolExecutor(max_workers=scenario_workers) as pool:
            futures = [
                pool.submit(evaluate_one, index, source)
                for index, source in enumerate(sources, start=1)
            ]
            completed = [future.result() for future in futures]
    results = [row for row, _ in completed]
    reused = sum(was_reused for _, was_reused in completed)
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
        "measured_simulation_wall_scope": (
            "sum_of_concurrent_native_evaluation_process_wall_seconds"
            if scenario_workers > 1
            else "sum_of_sequential_native_evaluation_wall_seconds"
        ),
        "requested_parallel_scenarios": scenario_workers,
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
            **({"candidate_terminal_matchup": row["result"]["candidate_terminal_matchup"]}
               if candidate_slot is not None else {}),
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



def evaluate_both_sides(sources: list[Path], model_manifest: Path, output: Path,
                        seed: int, binary: Path | None,
                        physics_workers: int | None, device: str | None,
                        resume: bool = False, scenario_workers: int = 1,
                        candidate_slot: int | None = None,
                        reference_slot: int | None = None) -> dict:
    """Evaluate every authentic scenario from both faction perspectives.

    The existing single-side evaluator remains the only executor. Each side
    owns its independent, immutable receipts, allowing a long evaluation to
    recover without replaying a previously completed side or scenario.
    """
    if not 2 <= len(sources) <= 64 or not 0 <= seed < (1 << 63):
        raise ValueError("paired native evaluation needs 2..64 scenarios and a fixed seed")
    if (type(candidate_slot) is not int or type(reference_slot) is not int or
            not 0 <= candidate_slot < (1 << 32) or
            not 0 <= reference_slot < (1 << 32) or
            candidate_slot == reference_slot):
        raise ValueError(
            "paired evaluation must pin distinct candidate and reference model slots "
            "so the physical policy teams can be swapped"
        )
    sources = [path.resolve(strict=True) for path in sources]
    if len(set(sources)) != len(sources):
        raise ValueError("paired native evaluation cannot duplicate a source run")
    manifest = model_manifest.resolve(strict=True)
    binary = binary.resolve(strict=True) if binary is not None else None
    env = native_headless_environment()
    model_store = env.get("BEES_NATIVE_MODEL_STORE")
    pinned = {
        "schema_version": 1,
        "kind": "native_two_sided_heterogeneous_evaluation_request",
        "source_runs": [
            {"path": str(path), "sha256": file_sha256(path)}
            for path in sources
        ],
        "model_manifest": str(manifest),
        "model_manifest_sha256": file_sha256(manifest),
        "model_store": model_store,
        "model_dependencies": native_manifest_model_dependencies(manifest, model_store),
        "policy_sampling_seed": seed,
        "rollout_binary": str(binary) if binary else None,
        "rollout_binary_sha256": file_sha256(binary) if binary else None,
        "physics_workers": physics_workers,
        "inference_device": device,
        "learner_sides": [1, 2],
        "candidate_slot": candidate_slot,
        "reference_slot": reference_slot,
        **({"parallel_scenario_workers": scenario_workers}
           if scenario_workers > 1 else {}),
    }
    output = output.resolve()
    if not output.parent.is_dir() or output.is_symlink():
        raise ValueError("paired native evaluation destination is unsafe")
    request_path = output / "paired-request.json"
    result_path = output / "paired-evaluation-result.json"
    if resume:
        if not output.is_dir() or request_path.is_symlink() or (
                json.loads(request_path.read_text(encoding="utf-8")) != pinned):
            raise ValueError("paired native evaluation resume changed immutable inputs")
        if result_path.exists() or result_path.is_symlink():
            raise FileExistsError("paired evaluation has already been published")
    else:
        if output.exists():
            raise FileExistsError("paired native evaluation requires a new directory")
        output.mkdir()
        atomic_json(request_path, pinned)
        sync_directory(output)
        sync_directory(output.parent)

    started = time.monotonic()
    sides = []
    reused_sides = 0
    for side in (1, 2):
        destination = output / f"side-{side}"
        completed = destination / "evaluation-suite-result.json"
        if completed.exists() or completed.is_symlink():
            if completed.is_symlink() or not completed.is_file():
                raise ValueError(f"side {side} has an invalid native completion marker")
            result = json.loads(completed.read_text(encoding="utf-8"))
            # A published side is reusable only if all underlying immutable
            # scenario receipts still match the exact pinned artifact set.
            side_request = json.loads(
                (destination / "suite-request.json").read_text(encoding="utf-8")
            )
            if (side_request.get("model_manifest_sha256") !=
                    pinned["model_manifest_sha256"] or
                    side_request.get("model_dependencies") != pinned["model_dependencies"] or
                    side_request.get("source_runs") != pinned["source_runs"] or
                    side_request.get("learner_side") != side or
                    side_request.get("policy_sampling_seed") != seed or
                    side_request.get("candidate_slot") != candidate_slot or
                    side_request.get("reference_slot") != reference_slot or
                    side_request.get("rollout_binary_sha256") !=
                    pinned["rollout_binary_sha256"] or
                    side_request.get("physics_workers") != physics_workers or
                    side_request.get("inference_device") != device or
                    side_request.get("parallel_scenario_workers", 1) != scenario_workers or
                    result.get("kind") != "native_heterogeneous_evaluation_complete" or
                    result.get("learner_side") != side or
                    result.get("model_manifest_sha256") != pinned["model_manifest_sha256"] or
                    result.get("policy_sampling_seed") != seed or
                    not isinstance(result.get("by_source"), list) or
                    len(result["by_source"]) != len(sources)):
                raise ValueError(f"side {side} completed evaluation changed identity")
            for index, row in enumerate(result["by_source"]):
                if (row.get("scenario") != index + 1 or
                        row.get("source_run") != str(sources[index]) or
                        row.get("source_run_sha256") != pinned["source_runs"][index]["sha256"] or
                        not isinstance(row.get("result_path"), str) or
                        not isinstance(row.get("result_sha256"), str)):
                    raise ValueError(f"side {side} source receipt differs from pinned request")
                receipt_path = destination / row["result_path"]
                if (receipt_path.is_symlink() or not receipt_path.is_file() or
                        not receipt_path.resolve().is_relative_to(destination) or
                        file_sha256(receipt_path) != row["result_sha256"]):
                    raise ValueError(f"side {side} source receipt changed after publication")
                receipt = json.loads(receipt_path.read_text(encoding="utf-8"))
                if (receipt.get("kind") != "native_policy_evaluation_complete" or
                        receipt.get("learner_side") != side or
                        receipt.get("source_run_config_sha256") !=
                        pinned["source_runs"][index]["sha256"] or
                        receipt.get("model_manifest_sha256") !=
                        pinned["model_manifest_sha256"] or
                        not isinstance(receipt.get("candidate_side_assignment"), dict) or
                        receipt["candidate_side_assignment"].get("candidate_slot") != candidate_slot or
                        receipt["candidate_side_assignment"].get("reference_slot") != reference_slot or
                        receipt["candidate_side_assignment"].get("candidate_physical_side") != side):
                    raise ValueError(f"side {side} individual evaluation receipt is invalid")
                check_native_input_dependencies({
                    "source_dependencies": receipt["source_dependencies"],
                })
                actual, scenes = terminal_matchup(
                    receipt_path.parent, receipt, candidate_slot, reference_slot,
                )
                stored = receipt.get("candidate_terminal_matchup")
                if (not isinstance(stored, dict) or
                        stored.get("counts") != dict(actual) or
                        stored.get("by_scene_sha256") != {
                            digest: dict(measured) for digest, measured in scenes.items()
                        } or
                        row.get("candidate_terminal_matchup") != stored or
                        row.get("side_win_rate") != receipt["side_win_rate"] or
                        row.get("episodes") != receipt["episodes"]):
                    raise ValueError(
                        f"side {side} published candidate model results disagree with terminal journal"
                    )
            reused_sides += 1
        else:
            result = evaluate_suite(
                sources, manifest, destination, side, seed, binary,
                physics_workers, device, resume=destination.exists(),
                scenario_workers=scenario_workers,
                candidate_slot=candidate_slot, reference_slot=reference_slot,
            )
        sides.append(result)

    # Group episodes may alternate team ownership. Count the candidate's
    # ACTUAL authored model slot from each terminal journal, not the winner
    # of whichever physical side was requested in that run. Combine both
    # role assignments per map before comparing physical faction outcomes.
    paired = []
    total_candidate_wins = total_candidate_draws = 0
    actual_side_exposure = {1: 0, 2: 0}
    actual_side_wins = {1: 0, 2: 0}
    for index, source in enumerate(sources):
        left = sides[0]["by_source"][index]
        right = sides[1]["by_source"][index]
        if (left["source_run_sha256"] != right["source_run_sha256"] or
                left["source_run_sha256"] != pinned["source_runs"][index]["sha256"]):
            raise ValueError("paired native scenario identities do not match")
        models = [
            left["candidate_terminal_matchup"]["counts"],
            right["candidate_terminal_matchup"]["counts"],
        ]
        expected_episodes = left["episodes"] + right["episodes"]
        if sum(model.get("matches", 0) for model in models) != expected_episodes:
            raise ValueError("paired native scenario has missing model-owned terminal episodes")
        counted = {}
        rates = {}
        for physical_side in (1, 2):
            prefix = f"candidate_side_{physical_side}"
            games = sum(model.get(prefix.replace("side_", "as_side_"), 0)
                        for model in models)
            wins = sum(model.get(prefix + "_wins", 0) for model in models)
            losses = sum(model.get(prefix + "_losses", 0) for model in models)
            draws_on_side = sum(model.get(prefix + "_draws", 0) for model in models)
            if games <= 0 or wins + losses + draws_on_side != games:
                raise ValueError(
                    f"paired native scenario has incomplete side {physical_side} policy ownership"
                )
            counted[physical_side] = {
                "episodes": games, "wins": wins, "losses": losses,
                "draws": draws_on_side,
            }
            rates[physical_side] = wins / games
            actual_side_exposure[physical_side] += games
            actual_side_wins[physical_side] += wins
        candidate_wins = sum(model["wins"] for model in models)
        candidate_draws = sum(model["draws"] for model in models)
        if (sum(row["wins"] for row in counted.values()) != candidate_wins or
                sum(row["draws"] for row in counted.values()) != candidate_draws):
            raise ValueError("paired faction breakdown disagrees with model-owned terminal results")
        total_candidate_wins += candidate_wins
        total_candidate_draws += candidate_draws
        paired.append({
            "scenario": index + 1,
            "source_run": str(source),
            "source_run_sha256": left["source_run_sha256"],
            "candidate_wins": candidate_wins,
            "candidate_draws": candidate_draws,
            "candidate_episodes": expected_episodes,
            "side_1_win_rate": rates[1],
            "side_2_win_rate": rates[2],
            "side_win_rate_gap": abs(rates[1] - rates[2]),
            "worst_side_win_rate": min(rates.values()),
            "by_physical_side": {
                "1": counted[1], "2": counted[2],
            },
            "side_1_scene_results": left["candidate_terminal_matchup"]["by_scene_sha256"],
            "side_2_scene_results": right["candidate_terminal_matchup"]["by_scene_sha256"],
        })
    episodes = sum(side["episodes"] for side in sides)
    # Preserve physical requested-side wins for source-level diagnostics,
    # but do NOT use those wins to evaluate the candidate's policy score.
    physical_side_victories = sum(side["side_victories"] for side in sides)
    draws = sum(side["draws"] for side in sides)
    steps = sum(side["world_steps"] for side in sides)
    measured = sum(side["measured_simulation_wall_seconds"] for side in sides)
    execution = sum(side["measured_execution_wall_seconds"] for side in sides)
    if (episodes <= 0 or steps <= 0 or measured <= 0 or execution <= 0 or
            not 0 <= physical_side_victories + draws <= episodes or
            total_candidate_draws != draws or
            not 0 <= total_candidate_wins + total_candidate_draws <= episodes or
            sum(actual_side_exposure.values()) != episodes):
        raise ValueError("paired native evaluation has incomplete measurements")
    for path, pinned_source in zip(sources, pinned["source_runs"]):
        if file_sha256(path) != pinned_source["sha256"]:
            raise ValueError("paired evaluation source changed before publication")
    if (file_sha256(manifest) != pinned["model_manifest_sha256"] or
            native_manifest_model_dependencies(manifest, model_store) !=
            pinned["model_dependencies"] or
            (binary is not None and file_sha256(binary) != pinned["rollout_binary_sha256"])):
        raise ValueError("paired evaluation model or native binary changed")
    result = {
        "schema_version": 1,
        "kind": "native_two_sided_heterogeneous_evaluation_complete",
        "model_manifest_sha256": pinned["model_manifest_sha256"],
        "model_dependencies": pinned["model_dependencies"],
        "policy_sampling_seed": seed,
        "candidate_slot": candidate_slot,
        "reference_slot": reference_slot,
        "scenarios": len(sources),
        "scenario_side_evaluations": len(sources) * 2,
        "requested_parallel_scenarios_per_side": scenario_workers,
        "completed_sides_reused": reused_sides,
        "episodes": episodes, "side_victories": physical_side_victories,
        "candidate_slot_victories": total_candidate_wins,
        "candidate_slot_draws": total_candidate_draws,
        "candidate_model_episodes": episodes,
        "draws": draws,
        "pooled_win_rate": total_candidate_wins / episodes,
        "equal_side_mean_win_rate": sum(
            row["side_1_win_rate"] + row["side_2_win_rate"]
            for row in paired
        ) / (2 * len(paired)),
        "by_candidate_physical_side": {
            str(side): {
                "episodes": actual_side_exposure[side],
                "wins": actual_side_wins[side],
                "win_rate": actual_side_wins[side] / actual_side_exposure[side],
            }
            for side in (1, 2)
        },
        "worst_side_scenario_win_rate": min(
            row["worst_side_win_rate"] for row in paired
        ),
        "mean_scenario_side_gap": sum(
            row["side_win_rate_gap"] for row in paired
        ) / len(paired),
        "world_steps": steps,
        "measured_simulation_wall_seconds": measured,
        "measured_simulation_world_steps_per_second": steps / measured,
        "measured_execution_wall_seconds": execution,
        "measured_execution_world_steps_per_second": steps / execution,
        "current_invocation_wall_seconds": time.monotonic() - started,
        "sides": [{
            "side": index,
            "receipt": f"side-{index}/evaluation-suite-result.json",
            "receipt_sha256": file_sha256(
                output / f"side-{index}" / "evaluation-suite-result.json"
            ),
            "episodes": side["episodes"],
            "requested_physical_side_win_rate": side["side_win_rate"],
            "candidate_win_rate": (
                sum(row["candidate_terminal_matchup"]["counts"]["wins"]
                    for row in side["by_source"]) / side["episodes"]
            ),
            "candidate_episodes_by_physical_side": {
                str(physical_side): sum(
                    row["candidate_terminal_matchup"]["counts"].get(
                        f"candidate_as_side_{physical_side}", 0
                    ) for row in side["by_source"]
                ) for physical_side in (1, 2)
            },
        } for index, side in enumerate(sides, start=1)],
        "by_scenario": paired,
    }
    atomic_json(result_path, result)
    sync_directory(output)
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("model_manifest", type=Path)
    parser.add_argument("output_dir", type=Path)
    parser.add_argument("run_configs", nargs="+", type=Path)
    parser.add_argument("--learner-side", type=int, choices=(1, 2))
    parser.add_argument("--both-sides", action="store_true",
                        help="route a pinned candidate and reference policy onto both physical factions")
    parser.add_argument("--candidate-slot", type=int,
                        help="candidate model slot routed onto the evaluated physical faction")
    parser.add_argument("--reference-slot", type=int,
                        help="fixed opponent model slot on the opposing physical faction")
    parser.add_argument("--policy-seed", type=int, default=0)
    parser.add_argument("--rollout-binary", type=Path)
    parser.add_argument("--physics-workers", type=int)
    parser.add_argument("--inference-device", choices=("cpu", "cuda:0", "cuda:1", "cuda:2", "cuda:3"))
    parser.add_argument("--resume", action="store_true",
                        help="continue unfinished scenarios from an unchanged pinned suite request")
    parser.add_argument("--parallel-scenarios", type=int, default=1,
                        help="explicit CPU-only parallel native evaluation processes (1..16); "
                             "requires --rollout-binary and --inference-device cpu")
    args = parser.parse_args()
    if args.both_sides:
        if args.learner_side is not None:
            parser.error("--both-sides and --learner-side are mutually exclusive")
        if args.candidate_slot is None or args.reference_slot is None:
            parser.error("--both-sides requires --candidate-slot and --reference-slot")
        result = evaluate_both_sides(
            args.run_configs, args.model_manifest, args.output_dir,
            args.policy_seed, args.rollout_binary, args.physics_workers,
            args.inference_device, resume=args.resume,
            scenario_workers=args.parallel_scenarios,
            candidate_slot=args.candidate_slot, reference_slot=args.reference_slot,
        )
    else:
        if args.learner_side is None:
            parser.error("--learner-side is required without --both-sides")
        result = evaluate_suite(
            args.run_configs, args.model_manifest, args.output_dir, args.learner_side,
            args.policy_seed, args.rollout_binary, args.physics_workers,
            args.inference_device, resume=args.resume,
            scenario_workers=args.parallel_scenarios,
            candidate_slot=args.candidate_slot, reference_slot=args.reference_slot,
        )
    print(json.dumps(result, sort_keys=True), flush=True)


if __name__ == "__main__":
    main()
