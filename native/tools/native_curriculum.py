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
        routed = [set(level.get("model_slots", {}).values())
                  for level in source["levels"]
                  if isinstance(level, dict) and isinstance(level.get("model_slots"), dict)]
        if learner_slot is not None and not any(
            learner_slot in slots for slots in routed
        ):
            raise ValueError("curriculum stage does not route the selected learner slot")
        if self_play is not None:
            required = {learner_slot, self_play["opponent_slot"]}
            if not any(required.issubset(slots) for slots in routed):
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


def curriculum_stage_metrics(base_run, curriculum, completed, evaluations, fleet_window):
    """Summarize useful simulation and learning at each immutable environment.

    Distributed collector wall seconds overlap, so only receipt-matched,
    observed *fleet* collection windows can supply stage throughput.
    Missing observations remain unknown instead of becoming fast samples.
    """
    stages = [{"start_round": 1, "run_config": base_run},
              *curriculum["stages"]]
    grouped = [[] for _ in stages]
    group_index = 0
    for number, (directory, record) in enumerate(completed, 1):
        while (group_index + 1 < len(stages) and
               stages[group_index + 1]["start_round"] <= number):
            group_index += 1
        grouped[group_index].append((number, directory, record))
    windows = ({row["round"]: row for row in fleet_window["rounds"]}
               if fleet_window is not None else {})
    report = []
    for index, stage in enumerate(stages):
        rounds = grouped[index]
        world_steps = sum(row["rollout_metrics"]["successful_world_steps"]
                          for _, _, row in rounds)
        policy_rows = sum(row["rollout_metrics"]["successful_policy_rows"]
                          for _, _, row in rounds)
        optimizer_steps = sum(row["learner_metrics"]["optimizer_input_steps"]
                              for _, _, row in rounds)
        optimizer_seconds = sum(row["learner_metrics"]["total_wall_seconds"]
                                for _, _, row in rounds)
        measured_collection_seconds = 0.0
        collection_observations = 0
        for number, _, record in rounds:
            if "collection_orders" in record:
                window = windows.get(number)
                if window is None:
                    continue
                seconds = window["work_order_to_last_receipt_wall_seconds"]
            else:
                seconds = record["rollout_metrics"]["wall_seconds"]
            measured_collection_seconds += seconds
            collection_observations += 1
        complete = bool(rounds) and collection_observations == len(rounds)
        total_measured = (measured_collection_seconds + optimizer_seconds
                          if complete else None)
        last_holdout = next((
            (number, evaluations[str(directory)])
            for number, directory, _ in reversed(rounds)
            if str(directory) in evaluations
        ), None)
        report.append({
            "start_round": stage["start_round"],
            "end_round": (stages[index + 1]["start_round"] - 1
                          if index + 1 < len(stages) else None),
            "source_run_config": stage["run_config"]["path"],
            "source_run_config_sha256": stage["run_config"]["sha256"],
            "completed_rounds": len(rounds),
            "successful_world_steps": world_steps,
            "successful_policy_rows": policy_rows,
            "optimizer_input_steps": optimizer_steps,
            "latest_training_outcomes": (
                rounds[-1][2]["learner_metrics"].get("training_outcomes", {})
                if rounds else None),
            "sum_optimizer_wall_seconds": optimizer_seconds,
            "collection_wall_seconds_observed_rounds": collection_observations,
            "collection_wall_seconds_complete": complete,
            "measured_collection_plus_optimizer_wall_seconds": total_measured,
            "world_steps_per_training_wall_second": (
                world_steps / total_measured if total_measured is not None
                and total_measured > 0 else None),
            "policy_rows_per_training_wall_second": (
                policy_rows / total_measured if total_measured is not None
                and total_measured > 0 else None),
            "fixed_reference_holdout_round": (
                last_holdout[0] if last_holdout else None),
            "fixed_reference_side_win_rate": (
                last_holdout[1]["side_win_rate"] if last_holdout else None),
        })
    active = selected_curriculum_run(base_run, curriculum, max(1, len(completed)))[1]
    return {"active_stage_start_round": active, "stages": report}
