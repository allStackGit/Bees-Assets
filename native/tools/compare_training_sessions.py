"""Compare native training checkpoints on identical independent held-out suites.

Reads completed session progress receipts; never trains, replays, or changes
model artifacts. Aggregate win-rate deltas are descriptive, not paired
episode-level significance tests.
"""
import argparse
import hashlib
import json
from pathlib import Path

from native_training_artifacts import file_sha256


def read_json(path):
    value = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(value, dict):
        raise ValueError(f"expected JSON object: {path}")
    return value


def model_opponents(receipt, learner_slot):
    """Authenticate nonlearner weights and inference settings when available.

    A suite is NOT called fixed-opponent comparable when a manifest cannot
    be read, the model's learner slot is ambiguous, or its referenced
    weight bytes changed. Source Level fingerprints remain separately pinned.
    """
    if type(learner_slot) is not int:
        return None
    path = Path(receipt.get("model_manifest", ""))
    if not path.is_file() or file_sha256(path) != receipt.get("model_manifest_sha256"):
        return None
    manifest = read_json(path)
    models = manifest.get("models")
    if not isinstance(models, list):
        return None
    opponents, seen, has_learner = {}, set(), False
    for model in models:
        if not isinstance(model, dict):
            return None
        slot = model.get("slot")
        if type(slot) is not int or slot in seen:
            return None
        seen.add(slot)
        if slot == learner_slot:
            has_learner = True
            continue
        weight = model.get("path")
        digest = model.get("sha256")
        if not isinstance(weight, str) or not isinstance(digest, str):
            return None
        file_path = (path.parent / weight).resolve()
        if not file_path.is_file() or file_sha256(file_path) != digest:
            return None
        opponents[str(slot)] = {key: value for key, value in model.items()
                                if key != "path"}
    if not opponents or not has_learner:
        return None
    return hashlib.sha256(json.dumps(
        opponents, sort_keys=True, separators=(",", ":")
    ).encode()).hexdigest()


def source_dependencies_identity(receipt, recipe):
    """Prove the source run's referenced scenes and Stage bytes are identical.

    A matching run-config SHA alone does not pin external files. Compare a
    digest over logically named dependencies, never machine-specific paths.
    """
    run_path = Path(receipt.get("source_run_config", ""))
    if not run_path.is_file() or file_sha256(run_path) != receipt.get("source_run_config_sha256"):
        return None
    pins = {
        Path(value["path"]).resolve(): value["sha256"]
        for value in recipe.get("source_dependencies", [])
        if isinstance(value, dict) and isinstance(value.get("path"), str)
    }
    run = read_json(run_path)
    dependencies = []
    for index, level in enumerate(run.get("levels", [])):
        if not isinstance(level, dict) or not isinstance(level.get("scene"), str):
            return None
        dependencies.append((f"level-{index}-scene", level["scene"]))
    if not dependencies:
        return None
    for field in ("policy_worker", "collision_matrix", "stage_palette"):
        if field in run and run[field] is not None:
            if not isinstance(run[field], str):
                return None
            dependencies.append((field, run[field]))
    observed = []
    for role, source in dependencies:
        path = (run_path.parent / source).resolve()
        expected = pins.get(path)
        if expected is None or not path.is_file() or file_sha256(path) != expected:
            return None
        observed.append((role, expected))
    return hashlib.sha256(json.dumps(
        observed, separators=(",", ":"), sort_keys=True
    ).encode()).hexdigest()


def validated_receipt(receipt, scene_sha, side, seed, episodes):
    if not isinstance(receipt, dict) or (
        receipt.get("kind") != "native_policy_evaluation_complete" or
        receipt.get("schema_version") != 1 or
        receipt.get("source_run_config_sha256") != scene_sha or
        receipt.get("learner_side") != side or
        receipt.get("policy_sampling_seed") != seed or
        receipt.get("episodes") != episodes
    ):
        raise ValueError("training progress contains an incompatible holdout receipt")
    wins, draws = receipt.get("side_victories"), receipt.get("draws")
    if (type(wins) is not int or type(draws) is not int or
            not 0 <= wins <= episodes or
            not 0 <= draws <= episodes - wins or
            receipt.get("side_win_rate") != wins / episodes or
            receipt.get("draw_rate") != draws / episodes):
        raise ValueError("training progress contains impossible holdout outcomes")
    return receipt


def suites(progress, config):
    if progress.get("kind") != "native_training_session_progress" or (
            progress.get("schema_version") != 1):
        raise ValueError("not a completed native session progress snapshot")
    results = {}
    rounds = progress.get("rounds")
    if not isinstance(rounds, list) or len(rounds) != progress.get("completed_rounds"):
        raise ValueError("incomplete native training progress rounds")
    if config.get("kind") != "native_training_session":
        raise ValueError("session recipe is not a native training session")
    candidates = []
    if config.get("evaluation_config") is not None:
        candidates.append((
            "primary", config["evaluation_config"],
            progress.get("baseline_holdout"),
            [(row["round"], row["next_checkpoint_sha256"], row["holdout"])
             for row in rounds if row.get("holdout") is not None],
        ))
    monitors = progress.get("monitoring_suites", [])
    for suite in monitors:
        candidates.append((
            suite["suite_id"],
            {"sha256": suite["source_run_config_sha256"]},
            suite["baseline"],
            [(row["round"], row["next_checkpoint_sha256"], row["monitor_holdouts"][suite["suite_id"]])
             for row in rounds
             if suite["suite_id"] in row.get("monitor_holdouts", {})],
        ))
    if not candidates:
        raise ValueError("session has no independent evaluation suites")
    for label, source, baseline, observations in candidates:
        sha = source["sha256"]
        if sha in results:
            raise ValueError("session repeats a holdout source identity")
        if not isinstance(baseline, dict):
            raise ValueError("suite lacks a verified baseline receipt")
        side = baseline.get("learner_side")
        seed = baseline.get("policy_sampling_seed")
        episodes = baseline.get("episodes")
        if (type(side) is not int or type(seed) is not int or
                type(episodes) is not int or episodes <= 0):
            raise ValueError("invalid baseline evaluation context")
        validated_receipt(baseline, sha, side, seed, episodes)
        curve, opponents = [], []
        opponents.append(model_opponents(baseline, config.get("learner_slot")))
        last_round = 0
        for number, checkpoint, receipt in observations:
            if type(number) is not int or number <= last_round or (
                    not isinstance(checkpoint, str) or len(checkpoint) != 64):
                raise ValueError("invalid holdout round identity")
            last_round = number
            validated_receipt(receipt, sha, side, seed, episodes)
            opponents.append(model_opponents(receipt, config.get("learner_slot")))
            curve.append({
                "round": number,
                "checkpoint_sha256": checkpoint,
                "model_manifest_sha256": receipt["model_manifest_sha256"],
                "victories": receipt["side_victories"],
                "draws": receipt["draws"],
                "win_rate": receipt["side_win_rate"],
                "draw_rate": receipt["draw_rate"],
                **({"by_scene_sha256": receipt["by_scene_sha256"]}
                   if "by_scene_sha256" in receipt else {}),
            })
        fixed_opponent = (opponents[0] is not None and
                          all(identity == opponents[0] for identity in opponents))
        input_identity = source_dependencies_identity(baseline, config)
        results[sha] = {
            "suite": label, "source_run_config_sha256": sha,
            "learner_side": side, "policy_sampling_seed": seed,
            "episodes": episodes,
            "baseline": {
                "victories": baseline["side_victories"],
                "draws": baseline["draws"],
                "win_rate": baseline["side_win_rate"],
                "draw_rate": baseline["draw_rate"],
                **({"by_scene_sha256": baseline["by_scene_sha256"]}
                   if "by_scene_sha256" in baseline else {}),
            },
            "rounds": curve,
            "fixed_opponent_identity": opponents[0] if fixed_opponent else None,
            "source_dependency_identity": input_identity,
        }
    return results


def describe(reference, candidate):
    shared = sorted(reference.keys() & candidate.keys())
    if not shared:
        raise ValueError("sessions share no identical pinned evaluation suite source")
    report = []
    for sha in shared:
        left, right = reference[sha], candidate[sha]
        if any(left[field] != right[field] for field in
               ("learner_side", "policy_sampling_seed", "episodes")):
            raise ValueError(f"matched suite {sha} changed its side, sampling seed or episode count")
        a = left["rounds"][-1] if left["rounds"] else left["baseline"]
        b = right["rounds"][-1] if right["rounds"] else right["baseline"]
        opponent_verified = (left["fixed_opponent_identity"] is not None and
                             left["fixed_opponent_identity"] == right["fixed_opponent_identity"])
        assets_verified = (
            left["source_dependency_identity"] is not None and
            left["source_dependency_identity"] == right["source_dependency_identity"]
        )
        scenes_left = a.get("by_scene_sha256")
        scenes_right = b.get("by_scene_sha256")
        scene_report = {}
        if isinstance(scenes_left, dict) and isinstance(scenes_right, dict):
            for scene in sorted(scenes_left.keys() | scenes_right.keys()):
                left_scene = scenes_left.get(scene)
                right_scene = scenes_right.get(scene)
                if left_scene is None or right_scene is None:
                    scene_report[scene] = {
                        "comparable": False,
                        "reason": "source_scene_not_evaluated_in_both_sessions",
                    }
                    continue
                compatible = left_scene["episodes"] == right_scene["episodes"]
                goals_left = left_scene.get("mission_outcomes")
                goals_right = right_scene.get("mission_outcomes")
                mission_differences = {}
                if compatible and isinstance(goals_left, dict) and isinstance(goals_right, dict):
                    for kind in sorted(goals_left.keys() | goals_right.keys()):
                        baseline = goals_left.get(kind)
                        observed = goals_right.get(kind)
                        if baseline is None or observed is None or (
                                baseline["attempts"] != observed["attempts"]):
                            mission_differences[kind] = {
                                "comparable": False,
                                "reason": "mission_episode_cohort_differs",
                            }
                        else:
                            mission_differences[kind] = {
                                "comparable": True,
                                "attempts": baseline["attempts"],
                                "completion_difference":
                                    observed["completions"] - baseline["completions"],
                                "completion_rate_difference":
                                    observed["completion_rate"] - baseline["completion_rate"],
                            }
                scene_report[scene] = {
                    "comparable": compatible,
                    **({"mission_completion_differences": mission_differences}
                       if mission_differences else {}),
                    "episodes": left_scene["episodes"] if compatible else None,
                    "reference": left_scene, "candidate": right_scene,
                    "win_rate_difference": (
                        right_scene["side_win_rate"] - left_scene["side_win_rate"]
                        if compatible else None
                    ),
                    "timeout_rate_difference": (
                        right_scene["timeout_rate"] - left_scene["timeout_rate"]
                        if compatible else None
                    ),
                }
        report.append({
            "by_scene_sha256": scene_report,
            "per_scene_outcomes_available": bool(scene_report),
            "source_run_config_sha256": sha,
            "reference_suite": left["suite"], "candidate_suite": right["suite"],
            "learner_side": left["learner_side"],
            "policy_sampling_seed": left["policy_sampling_seed"],
            "episodes": left["episodes"],
            "fixed_opponent_identity_verified": opponent_verified,
            "matching_source_dependencies_verified": assets_verified,
            "controlled_environment_verified": opponent_verified and assets_verified,
            "reference": left, "candidate": right,
            "reference_latest_win_rate": a["win_rate"],
            "candidate_latest_win_rate": b["win_rate"],
            "latest_win_rate_difference": b["win_rate"] - a["win_rate"],
            "reference_gain_from_baseline":
                a["win_rate"] - left["baseline"]["win_rate"],
            "candidate_gain_from_baseline":
                b["win_rate"] - right["baseline"]["win_rate"],
        })
    return {
        "kind": "native_training_fixed_suite_comparison",
        "scope": "aggregate_held_out_episode_outcomes_not_paired_episode_significance",
        "shared_suite_count": len(report),
        "unmatched_reference_suite_sha256": sorted(reference.keys() - candidate.keys()),
        "unmatched_candidate_suite_sha256": sorted(candidate.keys() - reference.keys()),
        "fixed_opponent_verified_for_all_shared_suites":
            all(s["fixed_opponent_identity_verified"] for s in report),
        "controlled_environment_verified_for_all_shared_suites":
            all(s["controlled_environment_verified"] for s in report),
        "suites": report,
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("reference", type=Path, help="session-progress.json")
    parser.add_argument("candidate", type=Path, help="session-progress.json")
    parser.add_argument("--require-fixed-opponent", action="store_true",
                        help="reject comparison unless every shared suite has verifiably identical opponent artifacts")
    parser.add_argument("--require-controlled-environment", action="store_true",
                        help="require identical verified opponent weights and scene/Stage source assets")
    args = parser.parse_args()
    ref = args.reference.resolve(strict=True)
    cand = args.candidate.resolve(strict=True)
    reference = suites(read_json(ref), read_json(ref.parent / "session-config.json"))
    candidate = suites(read_json(cand), read_json(cand.parent / "session-config.json"))
    report = describe(reference, candidate)
    if args.require_fixed_opponent and not report["fixed_opponent_verified_for_all_shared_suites"]:
        parser.error("fixed opponent artifacts cannot be verified for every matched suite")
    if args.require_controlled_environment and not report[
        "controlled_environment_verified_for_all_shared_suites"
    ]:
        parser.error("matching opponent and source assets cannot be verified for every matched suite")
    print(json.dumps(report, sort_keys=True, indent=2))


if __name__ == "__main__":
    main()
