#!/usr/bin/env python3
"""Native continual-learning candidate/incumbent gate.

Run the same authored Levels with both assignments of the two immutable
checkpoints. Score actual terminal policy ownership, including recycled
self-play episodes, not a fixed faction or PPO reward. This is the direct
incumbent gate, not authority to publish a champion: competency and
historical-model gates must also pass before deployment.
"""
import argparse
from collections import Counter
import json
import math
from pathlib import Path

from evaluate_policy import evaluate
from native_training_artifacts import (
    atomic_json, check_learner, file_sha256, native_manifest_model_dependencies,
    native_input_dependencies,
)
from train_policy_session import verify_evaluation, session_lock


def document(path):
    value = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(value, dict):
        raise ValueError(f"expected an object: {path}")
    return value


def pinned_model(manifest, checkpoint, slot):
    check_learner(manifest, checkpoint, slot)
    source = document(manifest)
    matches = [model for model in source["models"] if model["slot"] == slot]
    if len(matches) != 1:
        raise ValueError("ambiguous native champion model slot")
    model = dict(matches[0])
    artifact = (manifest.parent / model["path"]).resolve(strict=True)
    if file_sha256(artifact) != model["sha256"]:
        raise ValueError("native champion model does not match its manifest")
    model["path"] = str(artifact)
    return source, model


def terminal_matchup(directory, result, candidate_slot, incumbent_slot):
    """Read only the terminal records; reconcile them with the verified summary."""
    journal = directory / "events.jsonl"
    counts = Counter()
    by_scene = {}
    physical_wins = Counter()
    episodes = set()
    with journal.open("r", encoding="utf-8") as stream:
        for line in stream:
            event = json.loads(line)
            if event.get("kind") not in ("episode_final", "episode_aborted"):
                continue
            if (event.get("kind") != "episode_final" or
                    event.get("run_id") != result["run_id"]):
                raise ValueError("native champion evaluation has an aborted or unrelated episode")
            identity = (event["world_id"], event["episode_revision"])
            if identity in episodes:
                raise ValueError("repeated native champion terminal episode")
            episodes.add(identity)
            context = event["evaluation_context"]
            if context.get("policy_manifest_sha256") != result["model_manifest_sha256"]:
                raise ValueError("native champion terminal policy identity mismatch")
            bee, human = context["bee_side"], context["human_side"]
            if bee == human:
                raise ValueError("invalid native champion factions")
            # Authored routes describe the real policy controller slots even
            # on non-group Levels. Group routing also alternates by episode.
            routes = context["authored_policy_slots_by_side"]
            actual = {side: routes.get(str(side)) for side in (bee, human)}
            if any(not isinstance(slots, list) or len(slots) != 1
                   for slots in actual.values()):
                raise ValueError("champion matches require one policy slot per faction")
            owners = {side: slots[0] for side, slots in actual.items()}
            if set(owners.values()) != {candidate_slot, incumbent_slot}:
                raise ValueError("champion match did not use both distinct model slots")
            if context.get("group_episode_number") is not None:
                teams = context["group_team_by_side"]
                slots = context["model_slots_by_team"]
                if any(slots[str(teams[str(side)])] != owner
                       for side, owner in owners.items()):
                    raise ValueError("champion group routes disagree with authored ownership")
            candidate_side = next(side for side, owner in owners.items()
                                  if owner == candidate_slot)
            payload = event["payload"]
            winner, timeout = payload["winner_side"], payload["timed_out"]
            if winner not in (0, bee, human) or type(timeout) is not bool:
                raise ValueError("invalid native champion outcome")
            behavior = payload.get("faction_behavior")
            if not isinstance(behavior, list) or len(behavior) != 2:
                raise ValueError("native champion terminal lacks physical behavior evidence")
            candidate_behavior = behavior[0 if candidate_side == bee else 1]
            for field in ("starting_tsv", "final_tsv", "shots", "hits", "damage"):
                value = candidate_behavior.get(field)
                if type(value) is not int or value < 0:
                    raise ValueError("invalid native champion physical behavior counter")
                counts["candidate_" + field] += value
            if (candidate_behavior["starting_tsv"] <= 0 or
                    (candidate_behavior["hits"] == 0) != (candidate_behavior["damage"] == 0)):
                raise ValueError("inconsistent native champion physical behavior evidence")
            scene = context["scene_sha256"]
            scene_counts = by_scene.setdefault(scene, Counter())
            for row in (counts, scene_counts):
                row["matches"] += 1
                row["candidate_as_bee" if candidate_side == bee else
                    "candidate_as_human"] += 1
                row["wins" if winner == candidate_side else
                    "draws" if winner == 0 else "losses"] += 1
                row["timeouts"] += int(timeout)
                row["non_timeout_draws"] += int(winner == 0 and not timeout)
            if winner:
                physical_wins[str(winner)] += 1
    totals = result["evaluation"]
    if (counts["matches"] != result["episodes"] or
            counts["draws"] != totals["draws"] or
            counts["timeouts"] != totals["episodes_timed_out"] or
            any(physical_wins.get(side, 0) != wins
                for side, wins in totals["victories_by_side"].items()) or
            any(totals["victories_by_side"].get(side, 0) != wins
                for side, wins in physical_wins.items()) or
            set(by_scene) != set(result["by_scene_sha256"])):
        raise ValueError("native champion terminal counts disagree with verified evaluation")
    for scene, row in by_scene.items():
        expected = result["by_scene_sha256"][scene]
        if (row["matches"] != expected["episodes"] or
                row["draws"] != expected["draws"] or
                row["timeouts"] != expected["timed_out"]):
            raise ValueError("native champion scene counts disagree with verified evaluation")
    return counts, by_scene


def gate(source, candidate_manifest, candidate_checkpoint, candidate_slot,
         incumbent_manifest, incumbent_checkpoint, incumbent_slot,
         promotion_config, output, binary, seed, physics_workers, device):
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
    minimum = config["promotion"]["min_matches_vs_champion"]
    threshold = config["promotion"]["min_win_rate_vs_champion"]
    latency_limit = config["promotion"]["max_inference_batch_milliseconds"]
    if (type(minimum) is not int or minimum < 1 or
            type(threshold) not in (int, float) or not math.isfinite(threshold) or
            not 0 <= threshold <= 1 or
            type(latency_limit) not in (int, float) or not math.isfinite(latency_limit) or
            latency_limit <= 0):
        raise ValueError("invalid continual-learning incumbent gate")
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
    }
    with session_lock(output):
        recipe_path = output / "gate-config.json"
        if recipe_path.exists():
            if document(recipe_path) != recipe:
                raise ValueError("native champion gate inputs changed")
        else:
            atomic_json(recipe_path, recipe)
        total = Counter()
        scenes = {}
        comparisons = []
        latency_calls = 0
        latency_max = 0
        for index, (candidate_route, incumbent_route) in enumerate(((first, second), (second, first))):
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
            # Reuse the native evaluator's durable receipt after interruption.
            # An incomplete attempt is retained and never mistaken for success.
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
            counts, per_scene = terminal_matchup(directory, result, candidate_route, incumbent_route)
            timing = result.get("policy_forward_latency", {}).get("by_slot", {}).get(str(candidate_route))
            if not timing:
                raise ValueError("native champion gate has no complete candidate inference latency")
            latency_calls += timing["calls"]
            latency_max = max(latency_max, timing["max_nanoseconds"])
            total.update(counts)
            for scene, row in per_scene.items():
                scenes.setdefault(scene, Counter()).update(row)
            comparisons.append({
                "candidate_slot": candidate_route, "incumbent_slot": incumbent_route,
                "receipt": str(directory / "evaluation-result.json"),
                "receipt_sha256": file_sha256(directory / "evaluation-result.json"),
                "journal_sha256": file_sha256(directory / "events.jsonl"),
                "counts": dict(counts),
                "candidate_forward_latency": timing,
            })
        if (total["matches"] != expected or total["matches"] < minimum or
                any(row["candidate_as_bee"] != row["candidate_as_human"]
                    for row in scenes.values())):
            raise ValueError("native champion gate requires complete balanced faction assignments")
        score = (total["wins"] + 0.5 * total["non_timeout_draws"]) / total["matches"]
        receipt = {
            **recipe, "kind": "native_continual_incumbent_gate_complete",
            "assignments": comparisons, "counts": dict(total),
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
    parser.add_argument("--inference-device", choices=("cpu", "cuda:0"))
    args = parser.parse_args()
    if not 0 <= args.policy_seed < (1 << 63):
        parser.error("policy seed must fit nonnegative int63")
    result = gate(args.source, args.candidate_manifest, args.candidate_checkpoint,
                  args.candidate_slot, args.incumbent_manifest, args.incumbent_checkpoint,
                  args.incumbent_slot, args.promotion_config, args.output,
                  args.rollout_binary, args.policy_seed, args.physics_workers, args.inference_device)
    print(json.dumps(result, sort_keys=True), flush=True)


if __name__ == "__main__":
    main()
