#!/usr/bin/env python3
"""Native continual-learning candidate/incumbent gate.

Run the same authored Levels with both assignments of the two immutable
checkpoints. Score actual terminal policy ownership, including recycled
self-play episodes, not a fixed faction or PPO reward. This is the direct
incumbent gate, not authority to publish a champion: competency and
historical-model gates must also pass before deployment.
"""
import argparse
from concurrent.futures import ThreadPoolExecutor
from collections import Counter
import json
import math
from pathlib import Path
import time

from evaluate_policy import evaluate, terminal_matchup
from native_training_artifacts import (
    atomic_json, file_sha256, native_manifest_model_dependencies,
    native_input_dependencies,
)
from train_policy_session import verify_evaluation, session_lock


def document(path):
    value = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(value, dict):
        raise ValueError(f"expected an object: {path}")
    return value


def pinned_model(manifest, checkpoint, slot):
    source = document(manifest)
    if (source.get("format") != "bees-native-model-v1" or
            source.get("abi_version") != 26 or
            type(slot) is not int or not 0 <= slot < 1 << 32):
        raise ValueError("comparison needs a valid native policy manifest and slot")
    matches = [model for model in source["models"] if model["slot"] == slot]
    if len(matches) != 1:
        raise ValueError("ambiguous native champion model slot")
    model = dict(matches[0])
    artifact = (manifest.parent / model["path"]).resolve(strict=True)
    if (model["backend"] not in ("mlagents", "onnx") or
            file_sha256(artifact) != model["sha256"] or
            file_sha256(checkpoint) != model["sha256"]):
        raise ValueError("native comparison model does not match its authenticated artifact")
    model["path"] = str(artifact)
    return source, model


def gate(source, candidate_manifest, candidate_checkpoint, candidate_slot,
         incumbent_manifest, incumbent_checkpoint, incumbent_slot,
         promotion_config, output, binary, seed, physics_workers, device,
         *, minimum_matches=None, parallel_assignments=1):
    source, candidate_manifest, candidate_checkpoint, incumbent_manifest, incumbent_checkpoint, promotion_config = (
        path.resolve(strict=True) for path in (
            source, candidate_manifest, candidate_checkpoint, incumbent_manifest,
            incumbent_checkpoint, promotion_config,
        )
    )
    candidate_doc, candidate = pinned_model(candidate_manifest, candidate_checkpoint, candidate_slot)
    incumbent_doc, incumbent = pinned_model(incumbent_manifest, incumbent_checkpoint, incumbent_slot)
    contract = ("format", "abi_version", "policy_signature")
    if any(candidate_doc[key] != incumbent_doc[key] for key in contract):
        raise ValueError("native candidate and incumbent policy ABI differ")
    config = document(promotion_config)
    if (config["policy_signature"] != candidate_doc["policy_signature"] or
            config["policy_abi_version"] != candidate_doc["abi_version"]):
        raise ValueError("continual-learning promotion policy has a different ABI")
    minimum = (config["promotion"]["min_matches_vs_champion"]
               if minimum_matches is None else minimum_matches)
    threshold = config["promotion"]["min_win_rate_vs_champion"]
    latency_limit = config["promotion"]["max_inference_batch_milliseconds"]
    if (type(minimum) is not int or minimum < 1 or
            type(threshold) not in (int, float) or not math.isfinite(threshold) or
            not 0 <= threshold <= 1 or
            type(latency_limit) not in (int, float) or not math.isfinite(latency_limit) or
            latency_limit <= 0):
        raise ValueError("invalid continual-learning incumbent gate")
    if parallel_assignments not in (1, 2) or type(parallel_assignments) is not int:
        raise ValueError("incumbent gate accepts only one or two native evaluation processes")
    if parallel_assignments == 2 and (binary is None or device != "cpu"):
        raise ValueError(
            "parallel incumbent gate requires a fixed native rollout binary "
            "and explicit CPU inference; GPU evaluation remains externally scheduled"
        )
    run = document(source)
    allocations = [set(level.get("model_slots", {}).values()) for level in run["levels"]]
    if not allocations or len(allocations[0]) != 2 or any(
            slots != allocations[0] for slots in allocations):
        raise ValueError("native champion suite must route exactly the same two slots in every Level")
    first, second = sorted(allocations[0])
    expected = len(run["levels"]) * run.get("episodes_per_world", 1) * 2
    if expected < minimum:
        raise ValueError("native champion suite has fewer matches than the original promotion minimum")
    output = output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    recipe = {
        "schema_version": 1, "kind": "native_continual_incumbent_gate",
        "source": str(source), "source_sha256": file_sha256(source),
        "source_dependencies": native_input_dependencies({"path": str(source)}),
        "candidate_manifest_sha256": file_sha256(candidate_manifest),
        "candidate_checkpoint_sha256": candidate["sha256"],
        "incumbent_manifest_sha256": file_sha256(incumbent_manifest),
        "incumbent_checkpoint_sha256": incumbent["sha256"],
        "promotion_config_sha256": file_sha256(promotion_config),
        "minimum_matches": minimum, "minimum_score_rate": threshold,
        "maximum_inference_batch_milliseconds": latency_limit,
        "policy_seed": seed, "physics_workers": physics_workers, "device": device,
        "rollout_binary_sha256": file_sha256(binary) if binary else None,
        **({"parallel_assignments": 2} if parallel_assignments == 2 else {}),
    }
    gate_started = time.monotonic()
    with session_lock(output):
        recipe_path = output / "gate-config.json"
        if recipe_path.exists():
            if document(recipe_path) != recipe:
                raise ValueError("native champion gate inputs changed")
        else:
            atomic_json(recipe_path, recipe)
        def execute_assignment(index, candidate_route, incumbent_route):
            assignment_started = time.monotonic()
            # Each process owns its independent native World, scene and
            # model-output directory. Both use the one physical source and
            # identical fixed-seed policy sampling contract.
            home = output / f"assignment-{index}"
            home.mkdir(exist_ok=True)
            manifest = home / "manifest.json"
            model_doc = {key: candidate_doc[key] for key in contract}
            model_doc["models"] = [
                {**candidate, "slot": candidate_route},
                {**incumbent, "slot": incumbent_route},
            ]
            if manifest.exists():
                if document(manifest) != model_doc:
                    raise ValueError("native champion comparison manifest changed")
            else:
                atomic_json(manifest, model_doc)
            weights = native_manifest_model_dependencies(manifest)
            attempts = sorted(p for p in home.glob("attempt-*") if p.is_dir())
            completed = [p for p in attempts if (p / "evaluation-result.json").is_file()]
            if len(completed) > 1:
                raise ValueError("multiple completed native champion assignment attempts")
            if completed:
                directory = completed[0]
            else:
                directory = home / f"attempt-{len(attempts)+1:05d}"
                evaluate(source, manifest, directory, 1, binary, seed, physics_workers, device)
            result = verify_evaluation(directory, file_sha256(manifest),
                                       recipe["source_sha256"], 1, seed, device)
            if native_manifest_model_dependencies(manifest) != weights:
                raise ValueError("native champion assignment weights changed")
            counts, per_scene = terminal_matchup(
                directory, result, candidate_route, incumbent_route,
            )
            timing = result.get("policy_forward_latency", {}).get("by_slot", {}).get(
                str(candidate_route)
            )
            if not timing:
                raise ValueError("native champion gate has no complete candidate inference latency")
            comparison = {
                "candidate_slot": candidate_route, "incumbent_slot": incumbent_route,
                "receipt": str(directory / "evaluation-result.json"),
                "receipt_sha256": file_sha256(directory / "evaluation-result.json"),
                "journal_sha256": file_sha256(directory / "events.jsonl"),
                "counts": dict(counts),
                "candidate_forward_latency": timing,
            }
            return counts, per_scene, timing, comparison, (
                time.monotonic() - assignment_started
            )

        assignments = ((first, second), (second, first))
        if parallel_assignments == 2:
            with ThreadPoolExecutor(max_workers=2) as pool:
                futures = [
                    pool.submit(execute_assignment, index, candidate_route, incumbent_route)
                    for index, (candidate_route, incumbent_route) in enumerate(assignments)
                ]
                measured = [future.result() for future in futures]
        else:
            measured = [
                execute_assignment(index, candidate_route, incumbent_route)
                for index, (candidate_route, incumbent_route) in enumerate(assignments)
            ]
        total = Counter()
        scenes = {}
        comparisons = []
        latency_calls = 0
        latency_max = 0
        assignment_occupied_seconds = []
        # Aggregate in the pinned assignment order, never completion order.
        # This keeps the published promotion decision independent of host
        # thread scheduling and Windows/Linux process launch timing.
        for counts, per_scene, timing, comparison, occupied in measured:
            assignment_occupied_seconds.append(occupied)
            total.update(counts)
            for scene, row in per_scene.items():
                scenes.setdefault(scene, Counter()).update(row)
            latency_calls += timing["calls"]
            latency_max = max(latency_max, timing["max_nanoseconds"])
            comparisons.append(comparison)
        if (total["matches"] != expected or total["matches"] < minimum or
                any(row["candidate_as_bee"] != row["candidate_as_human"]
                    for row in scenes.values())):
            raise ValueError("native champion gate requires complete balanced faction assignments")
        score = (total["wins"] + 0.5 * total["non_timeout_draws"]) / total["matches"]
        receipt = {
            **recipe, "kind": "native_continual_incumbent_gate_complete",
            "assignments": comparisons, "counts": dict(total),
            "requested_parallel_assignment_processes": parallel_assignments,
            "current_invocation_wall_seconds": time.monotonic() - gate_started,
            "sum_independent_assignment_invocation_wall_seconds": sum(
                assignment_occupied_seconds
            ),
            "per_assignment_invocation_wall_seconds": assignment_occupied_seconds,
            "by_scene_sha256": {key: dict(row) for key, row in scenes.items()},
            "score_rate": score, "head_to_head_passed": score >= threshold,
            "candidate_inference_calls": latency_calls,
            "candidate_max_inference_batch_milliseconds": latency_max / 1e6,
            "runtime_latency_passed": latency_calls > 0 and latency_max / 1e6 <= latency_limit,
        }
        atomic_json(output / "incumbent-gate.json", receipt)
        return receipt


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("source", "candidate_manifest", "candidate_checkpoint",
                 "incumbent_manifest", "incumbent_checkpoint", "promotion_config", "output"):
        parser.add_argument(name, type=Path)
    parser.add_argument("--candidate-slot", type=int, required=True)
    parser.add_argument("--incumbent-slot", type=int, required=True)
    parser.add_argument("--rollout-binary", type=Path)
    parser.add_argument("--policy-seed", type=int, default=0)
    parser.add_argument("--physics-workers", type=int)
    parser.add_argument("--inference-device", choices=("cpu", "cuda:0", "cuda:1", "cuda:2", "cuda:3"))
    parser.add_argument("--parallel-assignments", action="store_true",
                        help="run both canonical faction assignments on separate CPU native processes")
    args = parser.parse_args()
    if not 0 <= args.policy_seed < (1 << 63):
        parser.error("policy seed must fit nonnegative int63")
    result = gate(args.source, args.candidate_manifest, args.candidate_checkpoint,
                  args.candidate_slot, args.incumbent_manifest, args.incumbent_checkpoint,
                  args.incumbent_slot, args.promotion_config, args.output,
                  args.rollout_binary, args.policy_seed, args.physics_workers, args.inference_device,
                  parallel_assignments=2 if args.parallel_assignments else 1)
    print(json.dumps(result, sort_keys=True), flush=True)


if __name__ == "__main__":
    main()
