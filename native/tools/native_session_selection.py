"""Publish a playable best-evaluated native checkpoint without copying model weights.

Native training, holdout evaluation and interactive gameplay all use the same
physical Levels. This module only selects an already verified checkpoint and
resolves source paths for a standalone native launcher. The fixed evaluation
suite ranks completed models, not training rewards or optimizer loss.
"""

import json
import math
from pathlib import Path

from native_training_artifacts import atomic_json, check_learner, file_sha256
from native_curriculum import selected_curriculum_run


def _candidate(round_number, checkpoint, evaluation, learner_slot, episodes):
    if not isinstance(evaluation, dict) or evaluation.get("kind") != "native_policy_evaluation_complete":
        raise ValueError("best native checkpoint requires a completed holdout receipt")
    wins = evaluation.get("side_victories")
    draws = evaluation.get("draws")
    if (type(wins) is not int or type(draws) is not int or
            not 0 <= wins <= episodes or not 0 <= draws <= episodes - wins or
            evaluation.get("episodes") != episodes or
            type(evaluation.get("side_win_rate")) not in (float, int) or
            not math.isfinite(evaluation["side_win_rate"]) or
            evaluation["side_win_rate"] != wins / episodes):
        raise ValueError("native holdout wins, draws and episode count disagree")
    manifest = Path(evaluation["model_manifest"]).resolve(strict=True)
    manifest_digest = file_sha256(manifest)
    if manifest_digest != evaluation["model_manifest_sha256"]:
        raise ValueError("native holdout model manifest changed after verification")
    checkpoint = Path(checkpoint).resolve(strict=True)
    # The actual selected policy slot must still point to the verified
    # checkpoint. A holdout may derive a fixed-reference opponent manifest,
    # but it cannot substitute a different learner policy.
    check_learner(manifest, checkpoint, learner_slot)
    return {
        "round": round_number,
        "checkpoint": str(checkpoint),
        "checkpoint_sha256": file_sha256(checkpoint),
        "manifest": str(manifest),
        "manifest_sha256": manifest_digest,
        "wins": wins, "draws": draws, "episodes": episodes,
        "win_rate": evaluation["side_win_rate"],
        "evaluation_run_id": evaluation["run_id"],
        "evaluation_summary_sha256": evaluation["rollout_summary_sha256"],
    }


def verified_mission_holdout(receipt):
    """Only verified role-aware physical mission evidence can rank models.

    A side victory is not a goal delivery, and stopping the opponent from
    completing its authored objective counts as an interception success.
    Require complete episode coverage; never silently score elimination
    episodes as failed missions or reconstruct roles from visible objects.
    """
    episodes = receipt.get("episodes")
    success = receipt.get("learner_mission_successes")
    attempts = receipt.get("mission_attempts")
    rate = receipt.get("learner_mission_success_rate")
    scenes = receipt.get("by_scene_sha256")
    if (type(episodes) is not int or episodes <= 0 or
            type(attempts) is not int or attempts != episodes or
            type(success) is not int or not 0 <= success <= attempts or
            type(rate) not in (int, float) or not math.isfinite(rate) or
            rate != success / attempts or not isinstance(scenes, dict) or not scenes):
        raise ValueError("physical mission selection needs complete verified holdout objectives")
    counted = counted_success = 0
    for scene in scenes.values():
        n = scene.get("episodes")
        total = scene.get("mission_attempts")
        successes = scene.get("learner_mission_successes")
        ratio = scene.get("learner_mission_success_rate")
        if (type(n) is not int or n <= 0 or
                type(total) is not int or total != n or
                type(successes) is not int or not 0 <= successes <= n or
                type(ratio) not in (int, float) or
                not math.isfinite(ratio) or ratio != successes / n):
            raise ValueError("physical mission scene has incomplete target-side attribution")
        counted += n
        counted_success += successes
    if counted != attempts or counted_success != success:
        raise ValueError("physical mission completion counts disagree with scenes")
    return success


def _playable_run(source_path, selected):
    source = json.loads(source_path.read_text(encoding="utf-8"))
    if (not isinstance(source, dict) or
            not isinstance(source.get("run_id"), str) or
            not source["run_id"].strip() or
            not isinstance(source.get("levels"), list) or not source["levels"] or
            source.get("hivemind") is not None or
            source.get("model_updates") or
            source.get("recover_notifications") or
            source.get("recover_outcome_journals") or
            "rollout_bundles" in source):
        raise ValueError("best native gameplay run requires a model-only source with physical Levels")
    # The training round driver consumes these machine-allocation fields
    # before launching Rust. They are not physical-game RunConfig fields,
    # whose schema deliberately rejects unknown training instructions.
    # Both standalone sessions and Experiment Hub use this sole publisher.
    for field in ("rollout_worlds", "rollout_world_start", "rollout_workers"):
        source.pop(field, None)
    base = source_path.parent
    def absolute(field):
        value = source.get(field)
        if value is None:
            return
        if not isinstance(value, str) or not value:
            raise ValueError(f"invalid native gameplay source {field}")
        source[field] = str((base / value).resolve(strict=True))

    for level in source["levels"]:
        if not isinstance(level, dict) or not isinstance(level.get("scene"), str) or not level["scene"]:
            raise ValueError("best native gameplay run has a Level without an authored scene")
        level["scene"] = str((base / level["scene"]).resolve(strict=True))
    absolute("collision_matrix")
    absolute("stage_palette")
    absolute("policy_worker")
    if source.get("policy_worker") is None:
        raise ValueError("best native gameplay run requires the resident policy worker")
    python = source.get("python")
    if not isinstance(python, str) or not python:
        raise ValueError("best native gameplay run requires a Python interpreter")
    if Path(python).is_absolute() or len(Path(python).parts) > 1:
        source["python"] = str((base / python).resolve(strict=True))

    # Derive an immutable model identity while retaining the original native
    # map, command, reward and simulation contracts. Gameplay never uses a
    # separate training simulator, and this export never writes a checkpoint.
    source["run_id"] = source["run_id"] + "-selected-" + selected["checkpoint_sha256"][:16]
    source["model_manifest"] = selected["manifest"]
    source["model_updates"] = []
    source["policy_capture"] = None
    source["connection_checkpoint"] = "selected-game-connection.json"
    source["notifications"] = "selected-game-events.jsonl"
    source["rollout_summary"] = "selected-game-summary.json"
    return source


def _rank_evaluation_suites(round_number, checkpoint, primary,
                            recipe, monitor_baseline, monitor_results):
    """Rank only models actually evaluated on every pinned independent suite.

    The existing primary holdout still controls the default. Alternative
    selection uses equal weighting per suite (not per episode), so an easy
    large suite cannot drown out harder missions with fewer episodes.
    """
    learner_slot = recipe["learner_slot"]
    primary_candidate = _candidate(
        round_number, checkpoint, primary, learner_slot, primary["episodes"],
    )
    mode = recipe.get("checkpoint_selection", "primary")
    if mode == "primary":
        return primary_candidate
    if mode == "mission":
        successes = verified_mission_holdout(primary)
        primary_candidate.update({
            "mission_successes": successes,
            "mission_attempts": primary["episodes"],
            "mission_success_rate": successes / primary["episodes"],
            "selection_score": successes / primary["episodes"],
        })
        return primary_candidate
    if mode not in ("mean", "worst", "mean-scene", "worst-scene",
                    "mission-mean-scene", "mission-worst-scene"):
        raise ValueError("unknown native checkpoint ranking")
    suites = recipe.get("monitor_evaluations", [])
    if len(monitor_results) != len(suites) or (
            mode in ("mean", "worst") and not suites):
        raise ValueError("native checkpoint selection requires all scheduled holdouts")
    source_receipts = [("primary", primary)]
    all_suites = [{
        "suite_id": "primary",
        "source_run_config_sha256": recipe["evaluation_config"]["sha256"],
        "wins": primary_candidate["wins"],
        "episodes": primary_candidate["episodes"],
        "win_rate": primary_candidate["win_rate"],
    }]
    for index, source in enumerate(suites, 1):
        suite_id = f"monitor-{index:03d}"
        baseline = monitor_baseline.get(suite_id)
        result = monitor_results.get(suite_id)
        if (not isinstance(baseline, dict) or not isinstance(result, dict) or
                baseline.get("source_run_config_sha256") != source["sha256"] or
                result.get("source_run_config_sha256") != source["sha256"] or
                baseline.get("episodes") != result.get("episodes") or
                baseline.get("learner_side") != primary["learner_side"] or
                result.get("learner_side") != primary["learner_side"] or
                baseline.get("policy_sampling_seed") != primary["policy_sampling_seed"] or
                result.get("policy_sampling_seed") != primary["policy_sampling_seed"]):
            raise ValueError(f"native checkpoint selection has incomparable suite {suite_id}")
        candidate = _candidate(
            round_number, checkpoint, result, learner_slot, result["episodes"],
        )
        source_receipts.append((suite_id, result))
        all_suites.append({
            "suite_id": suite_id,
            "source_run_config_sha256": source["sha256"],
            "wins": candidate["wins"],
            "episodes": candidate["episodes"],
            "win_rate": candidate["win_rate"],
        })
    if mode in ("mean-scene", "worst-scene",
                "mission-mean-scene", "mission-worst-scene"):
        mission_based = mode.startswith("mission-")
        if mission_based:
            for _, receipt in source_receipts:
                verified_mission_holdout(receipt)
        scene_rows = []
        distinct_scenes = set()
        for suite_id, receipt in source_receipts:
            scenes = receipt.get("by_scene_sha256")
            if not isinstance(scenes, dict) or not scenes:
                raise ValueError("per-scene checkpoint selection requires authentic physical scene receipts")
            for scene_sha, scene in sorted(scenes.items()):
                episodes, wins = scene.get("episodes"), scene.get("side_victories")
                if (not isinstance(scene_sha, str) or len(scene_sha) != 64 or
                        type(episodes) is not int or episodes <= 0 or
                        type(wins) is not int or not 0 <= wins <= episodes or
                        scene.get("side_win_rate") != wins / episodes):
                    raise ValueError("invalid native per-scene model selection outcome")
                distinct_scenes.add(scene_sha)
                row = {
                    "suite_id": suite_id, "scene_sha256": scene_sha,
                    "episodes": episodes, "wins": wins, "win_rate": wins / episodes,
                }
                if mission_based:
                    successes = scene["learner_mission_successes"]
                    row["mission_successes"] = successes
                    row["mission_success_rate"] = successes / episodes
                scene_rows.append(row)
        if len(distinct_scenes) < 2:
            raise ValueError("per-scene checkpoint ranking requires at least two different physical scenes")
        rates = [row["mission_success_rate"] if mission_based else row["win_rate"]
                 for row in scene_rows]
        primary_candidate["selection_score"] = (
            sum(rates) / len(rates) if mode in ("mean-scene", "mission-mean-scene")
            else min(rates)
        )
        primary_candidate["evaluation_scenes"] = scene_rows
        if mission_based:
            successes = verified_mission_holdout(primary)
            primary_candidate.update({
                "mission_successes": successes,
                "mission_attempts": primary["episodes"],
                "mission_success_rate": successes / primary["episodes"],
            })
    else:
        rates = [suite["win_rate"] for suite in all_suites]
        primary_candidate["selection_score"] = (
            sum(rates) / len(rates) if mode == "mean" else min(rates)
        )
    primary_candidate["evaluation_suites"] = all_suites
    return primary_candidate


def publish_best_evaluated(root, recipe, completed, evaluations, baseline_holdout,
                           monitor_baseline=None, monitor_observations=None):
    """Use identical holdout episodes to select a checkpoint and a runnable run.

    A candidate replaces the current selection only if it wins more of the
    same fixed-seed battles. Ties retain the earlier model, including the
    untrained baseline. No in-progress or unevaluated checkpoint is selected.
    """
    if baseline_holdout is None or "evaluation_config" not in recipe:
        raise ValueError("best native checkpoint needs an initial fixed-seed holdout")
    episodes = baseline_holdout.get("episodes")
    if type(episodes) is not int or episodes <= 0:
        raise ValueError("native holdout must contain positive episode count")
    if not isinstance(evaluations, dict):
        raise ValueError("invalid native holdout evaluation registry")
    learner_slot = recipe["learner_slot"]
    monitor_baseline = monitor_baseline or {}
    monitor_observations = monitor_observations or {}
    initial_checkpoint = recipe["initial_checkpoint"]
    baseline = _rank_evaluation_suites(
        0, initial_checkpoint["path"], baseline_holdout, recipe,
        monitor_baseline, monitor_baseline,
    )
    if baseline["checkpoint_sha256"] != initial_checkpoint["sha256"]:
        raise ValueError("native holdout baseline does not match pinned initial learner weights")
    # Self-play holdouts deliberately replace the mutable opponent with
    # the pinned initial reference model and resolve both artifact paths
    # to absolute paths. That scientifically necessary derived manifest
    # has different bytes from initial_manifest even for round zero.
    # _candidate already authenticated the actual learner checkpoint;
    # ensure_evaluation pins the original opponent and every artifact.
    if ("self_play" not in recipe and
            baseline["manifest_sha256"] != recipe["initial_manifest"]["sha256"]):
        raise ValueError("native holdout baseline manifest changed")
    best = baseline
    evaluated = 0
    for number, (directory, record) in enumerate(completed, 1):
        result = evaluations.get(str(directory))
        if result is None:
            continue
        if result.get("learner_side") != baseline_holdout.get("learner_side"):
            raise ValueError("native holdouts have inconsistent learner sides")
        candidate = _rank_evaluation_suites(
            number, record["next_checkpoint"], result, recipe,
            monitor_baseline, monitor_observations.get(str(directory), {}),
        )
        if candidate["checkpoint_sha256"] != record["next_checkpoint_sha256"]:
            raise ValueError("native candidate checkpoint changed after round completion")
        evaluated += 1
        if (candidate["selection_score"] > best["selection_score"]
                if "selection_score" in candidate else
                candidate["wins"] > best["wins"]):
            best = candidate

    # A curriculum's playable environment must advance with the session.
    # Keep best-policy selection on one fixed holdout, but load that policy
    # into the *active* authored Level rather than resetting to stage one.
    source_run, active_stage_start = selected_curriculum_run(
        recipe["run_config"], recipe.get("curriculum"), max(1, len(completed)),
    )
    source = Path(source_run["path"]).resolve(strict=True)
    if file_sha256(source) != source_run["sha256"]:
        raise ValueError("native playable source run changed during checkpoint selection")
    selected_run = _playable_run(source, best)
    root = Path(root)
    atomic_json(root / "best-evaluated-run.json", selected_run)
    # Publish the selection receipt last, after the runnable configuration is
    # durable. It binds the current selected run bytes and immutable source
    # artifacts; in-flight or resumed rounds can safely republish this view.
    receipt = {
        "schema_version": 1,
        "kind": "native_training_best_evaluated",
        "selection": {
            "primary": "most_fixed_seed_side_victories_with_earliest_tie",
            "mean": "highest_equal_weight_mean_fixed_suite_win_rate_with_earliest_tie",
            "worst": "highest_minimum_fixed_suite_win_rate_with_earliest_tie",
            "mean-scene": "highest_equal_weight_mean_fixed_scene_win_rate_with_earliest_tie",
            "worst-scene": "highest_minimum_fixed_scene_win_rate_with_earliest_tie",
            "mission": "most_role_adjusted_physical_goal_successes_with_earliest_tie",
            "mission-mean-scene": "highest_equal_weight_mean_fixed_scene_role_adjusted_goal_rate",
            "mission-worst-scene": "highest_minimum_fixed_scene_role_adjusted_goal_rate",
        }[recipe.get("checkpoint_selection", "primary")],
        "learner_side": baseline_holdout["learner_side"],
        "learner_slot": learner_slot,
        "evaluated_rounds": evaluated,
        "completed_rounds": len(completed),
        "baseline_wins": baseline["wins"],
        "baseline_win_rate": baseline["win_rate"],
        **({"baseline_mission_success_rate": baseline["mission_success_rate"]}
           if "mission_success_rate" in baseline else {}),
        **({"baseline_selection_score": baseline["selection_score"]}
           if "selection_score" in baseline else {}),
        "selected": best,
        "source_run_config": str(source),
        "source_run_config_sha256": source_run["sha256"],
        **({"curriculum_stage_start_round": active_stage_start}
           if "curriculum" in recipe else {}),
        "playable_run": str(root / "best-evaluated-run.json"),
        "playable_run_sha256": file_sha256(root / "best-evaluated-run.json"),
    }
    atomic_json(root / "best-evaluated.json", receipt)
    return receipt


def publish_latest_playable(root, recipe, completed, manifest, checkpoint):
    """Export the latest verified learner as a runnable native game.

    Unlike best-evaluated, this is not a quality ranking. It uses the
    checkpoint's *current* opponent manifest and the active curriculum scene,
    keeping self-play and authored gameplay configuration intact.
    """
    root = Path(root)
    manifest = Path(manifest).resolve(strict=True)
    checkpoint = Path(checkpoint).resolve(strict=True)
    manifest_sha = file_sha256(manifest)
    checkpoint_sha = file_sha256(checkpoint)
    if completed:
        last = completed[-1][1]
        if (Path(last["next_manifest"]).resolve(strict=True) != manifest or
                Path(last["next_checkpoint"]).resolve(strict=True) != checkpoint or
                last["next_manifest_sha256"] != manifest_sha or
                last["next_checkpoint_sha256"] != checkpoint_sha):
            raise ValueError("latest learner model does not match completed native round")
    elif (manifest_sha != recipe["initial_manifest"]["sha256"] or
          checkpoint_sha != recipe["initial_checkpoint"]["sha256"]):
        raise ValueError("initial latest-playable model differs from pinned session input")
    check_learner(manifest, checkpoint, recipe["learner_slot"])
    source_run, stage_start = selected_curriculum_run(
        recipe["run_config"], recipe.get("curriculum"), max(1, len(completed)),
    )
    source = Path(source_run["path"]).resolve(strict=True)
    if file_sha256(source) != source_run["sha256"]:
        raise ValueError("native playable environment changed during session")
    playable = _playable_run(source, {
        "checkpoint_sha256": checkpoint_sha,
        "manifest": str(manifest),
    })
    output = root / "latest-playable-run.json"
    atomic_json(output, playable)
    receipt = {
        "schema_version": 1,
        "kind": "native_training_latest_playable",
        "selection": "latest_verified_checkpoint_without_quality_ranking",
        "completed_rounds": len(completed),
        "learner_slot": recipe["learner_slot"],
        "checkpoint": str(checkpoint),
        "checkpoint_sha256": checkpoint_sha,
        "manifest": str(manifest),
        "manifest_sha256": manifest_sha,
        "source_run_config": str(source),
        "source_run_config_sha256": source_run["sha256"],
        **({"curriculum_stage_start_round": stage_start}
           if "curriculum" in recipe else {}),
        "playable_run": str(output),
        "playable_run_sha256": file_sha256(output),
    }
    # Receipt last: its presence means the game config has been fsynced.
    atomic_json(root / "latest-playable.json", receipt)
    return receipt
