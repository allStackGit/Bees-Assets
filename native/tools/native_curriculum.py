"""Pinned environment curricula for the unified native MA-POCA session.

Switch complete authored source Level configurations only between completed
optimizer rounds. Model lineage, worker executable and fixed holdout evaluation
remain session-owned; no second simulation or environment mutation path exists.
"""

import json
from pathlib import Path

from native_training_artifacts import file_sha256


def _pin(path):
    path = Path(path).resolve(strict=True)
    if not path.is_file():
        raise ValueError(f"expected curriculum file: {path}")
    return {"path": str(path), "sha256": file_sha256(path)}


def _source(path):
    document = json.loads(Path(path).read_text(encoding="utf-8"))
    if (not isinstance(document, dict) or
            not isinstance(document.get("levels"), list) or not document["levels"] or
            "rollout_bundles" in document or document.get("hivemind") or
            document.get("model_updates") or document.get("recover_notifications") or
            document.get("recover_outcome_journals")):
        raise ValueError(f"curriculum requires a model-only source Level run: {path}")
    return document


def _python_command(run, value):
    if not isinstance(value, str) or not value.strip():
        raise ValueError("curriculum source must specify Python")
    python = Path(value)
    if python.is_absolute() or len(python.parts) > 1:
        return str((Path(run["path"]).parent / python).resolve())
    return value


def load_curriculum(path, base_run, total_rounds, round_python, learner_slot, self_play):
    """Validate and pin complete source runs from a strictly ordered plan.

    The positional run is stage one. A plan lists only subsequent transitions:
      {"schema_version":1,"stages":[{"start_round":5,"run_config":"hard.run.json"}]}
    """
    plan = _pin(path)
    document = json.loads(Path(plan["path"]).read_text(encoding="utf-8"))
    if (not isinstance(document, dict) or document.get("schema_version") != 1 or
            set(document) != {"schema_version", "stages"} or
            not isinstance(document["stages"], list) or not document["stages"]):
        raise ValueError("curriculum requires schema_version 1 and nonempty stages")
    base = _source(base_run["path"])
    base_worker = base.get("policy_worker")
    if not isinstance(base_worker, str) or not base_worker:
        raise ValueError("base curriculum run requires a policy worker")
    base_worker = (Path(base_run["path"]).parent / base_worker).resolve(strict=True)

    stages = []
    previous_round = 1
    for item in document["stages"]:
        if (not isinstance(item, dict) or
                set(item) != {"start_round", "run_config"} or
                type(item["start_round"]) is not int or
                not previous_round < item["start_round"] <= total_rounds or
                not isinstance(item["run_config"], str) or not item["run_config"]):
            raise ValueError("curriculum stages need strictly increasing start_round values in 2..rounds")
        run = _pin(Path(plan["path"]).parent / item["run_config"])
        source = _source(run["path"])
        worker = source.get("policy_worker")
        if (not isinstance(worker, str) or not worker or
                _python_command(run, source.get("python")) != round_python or
                (Path(run["path"]).parent / worker).resolve(strict=True) != base_worker):
            raise ValueError("curriculum cannot change the session Python or policy worker")
        if self_play is not None:
            required = {learner_slot, self_play["opponent_slot"]}
            if not any(required.issubset(set(level.get("model_slots", {}).values()))
                       for level in source["levels"] if isinstance(level, dict)):
                raise ValueError("curriculum stage must route both self-play model slots")
        stages.append({"start_round": item["start_round"], "run_config": run})
        previous_round = item["start_round"]
    return {"plan": plan, "stages": stages}


def selected_curriculum_run(base_run, curriculum, round_number):
    """Select one immutable source without modifying the round's checkpoint."""
    run, start = base_run, 1
    if curriculum is not None:
        for item in curriculum["stages"]:
            if round_number < item["start_round"]:
                break
            run, start = item["run_config"], item["start_round"]
    return run, start
