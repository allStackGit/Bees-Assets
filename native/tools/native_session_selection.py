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


def publish_best_evaluated(root, recipe, completed, evaluations, baseline_holdout):
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
    initial_checkpoint = recipe["initial_checkpoint"]
    baseline = _candidate(0, initial_checkpoint["path"], baseline_holdout,
                          learner_slot, episodes)
    if baseline["checkpoint_sha256"] != initial_checkpoint["sha256"] or (
            baseline["manifest_sha256"] != recipe["initial_manifest"]["sha256"]):
        raise ValueError("native holdout baseline does not match pinned initial weights")
    best = baseline
    evaluated = 0
    for number, (directory, record) in enumerate(completed, 1):
        result = evaluations.get(str(directory))
        if result is None:
            continue
        if result.get("learner_side") != baseline_holdout.get("learner_side"):
            raise ValueError("native holdouts have inconsistent learner sides")
        candidate = _candidate(number, record["next_checkpoint"], result,
                               learner_slot, episodes)
        if candidate["checkpoint_sha256"] != record["next_checkpoint_sha256"]:
            raise ValueError("native candidate checkpoint changed after round completion")
        evaluated += 1
        if candidate["wins"] > best["wins"]:
            best = candidate

    source = Path(recipe["run_config"]["path"]).resolve(strict=True)
    if file_sha256(source) != recipe["run_config"]["sha256"]:
        raise ValueError("native source run changed during checkpoint selection")
    selected_run = _playable_run(source, best)
    root = Path(root)
    atomic_json(root / "best-evaluated-run.json", selected_run)
    # Publish the selection receipt last, after the runnable configuration is
    # durable. It binds the current selected run bytes and immutable source
    # artifacts; in-flight or resumed rounds can safely republish this view.
    receipt = {
        "schema_version": 1,
        "kind": "native_training_best_evaluated",
        "selection": "most_fixed_seed_side_victories_with_earliest_tie",
        "learner_side": baseline_holdout["learner_side"],
        "learner_slot": learner_slot,
        "evaluated_rounds": evaluated,
        "completed_rounds": len(completed),
        "baseline_wins": baseline["wins"],
        "baseline_win_rate": baseline["win_rate"],
        "selected": best,
        "source_run_config": str(source),
        "source_run_config_sha256": recipe["run_config"]["sha256"],
        "playable_run": str(root / "best-evaluated-run.json"),
        "playable_run_sha256": file_sha256(root / "best-evaluated-run.json"),
    }
    atomic_json(root / "best-evaluated.json", receipt)
    return receipt
