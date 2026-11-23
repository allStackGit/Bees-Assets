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


def _python_command(run, value, session_python):
    if not isinstance(value, str) or not value.strip():
        raise ValueError("curriculum source must specify Python")
    # Portable original-fleet Stage runs deliberately say "auto".
    # They execute under the SAME pinned Python as the session, rather
    # than accidentally launching system Python without MA-POCA/ROCm.
    if value == "auto":
        return session_python
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
            set(document) not in (
                {"schema_version", "stages"}, {"schema_version", "rotation"}
            )):
        raise ValueError("curriculum requires explicit ordered stages or a fixed rotation")
    rotated = "rotation" in document
    if not rotated and (
        not isinstance(document["stages"], list) or not document["stages"]
    ):
        raise ValueError("curriculum needs authored Stage transitions")
    base = _source(base_run["path"])
    base_worker = base.get("policy_worker")
    if not isinstance(base_worker, str) or not base_worker:
        raise ValueError("base curriculum run requires a policy worker")
    base_worker = (Path(base_run["path"]).parent / base_worker).resolve(strict=True)

    def validated_stage_run(value):
        if not isinstance(value, str) or not value:
            raise ValueError("curriculum Stage needs a source Run JSON filename")
        run = _pin(Path(plan["path"]).parent / value)
        source = _source(run["path"])
        worker = source.get("policy_worker")
        if (not isinstance(worker, str) or not worker or
                _python_command(run, source.get("python"), round_python) != round_python or
                (Path(run["path"]).parent / worker).resolve(strict=True) != base_worker):
            raise ValueError("curriculum cannot change the session Python or policy worker")
        routed = [set(level.get("model_slots", {}).values())
                  for level in source["levels"]
                  if isinstance(level, dict) and isinstance(level.get("model_slots"), dict)]
        if learner_slot is not None and not any(
            learner_slot in slots for slots in routed
        ):
            raise ValueError("curriculum Stage does not route the selected learner slot")
        if self_play is not None:
            required = {learner_slot, self_play["opponent_slot"]}
            if not any(required.issubset(slots) for slots in routed):
                raise ValueError("curriculum Stage must route both self-play model slots")
        return run

    if rotated:
        # Revisit each original Stage for the SAME number of completed
        # optimization rounds. The first source is always the positional
        # base_run. No new learner or physical simulation is constructed.
        rotation = document["rotation"]
        if (not isinstance(rotation, dict) or
                set(rotation) not in (
                    {"rounds_per_stage", "run_configs"},
                    {"rounds_per_stage", "run_configs", "monitor_run_configs"},
                ) or
                type(rotation["rounds_per_stage"]) is not int or
                not 1 <= rotation["rounds_per_stage"] <= 10000 or
                not isinstance(rotation["run_configs"], list) or
                not 1 <= len(rotation["run_configs"]) <= 15):
            raise ValueError("native Stage rotation requires 1..15 additional "
                             "runs and 1..10000 completed rounds per Stage")
        if total_rounds < (
            (len(rotation["run_configs"]) + 1) * rotation["rounds_per_stage"]
        ):
            raise ValueError("native Stage rotation requires at least one complete "
                             "cycle so all authored physical maps can be trained")
        alternatives = [validated_stage_run(value)
                        for value in rotation["run_configs"]]
        if len({base_run["path"], *(item["path"] for item in alternatives)}) != (
            len(alternatives) + 1
        ):
            raise ValueError("native Stage rotation cannot repeat an authored source Run")
        monitoring = []
        if "monitor_run_configs" in rotation:
            paths = rotation["monitor_run_configs"]
            if (not isinstance(paths, list) or
                    len(paths) != len(alternatives)):
                raise ValueError(
                    "rotated holdout monitoring requires exactly one "
                    "fixed evaluation Run for each additional physical Stage"
                )
            for source, value in zip(alternatives, paths):
                holdout = validated_stage_run(value)
                training_stage = _source(source["path"])
                measured_stage = _source(holdout["path"])
                if (training_stage.get("stage_name") != measured_stage.get("stage_name") or
                        training_stage.get("native_stage") != measured_stage.get("native_stage") or
                        training_stage.get("fragment_spawn") !=
                            measured_stage.get("fragment_spawn")):
                    raise ValueError(
                        "rotated monitoring must use the SAME source Stage "
                        "physics/palette/fragment configuration as the trained map"
                    )
                if holdout["path"] == source["path"] or (
                    file_sha256(Path(holdout["path"])) == source["sha256"]
                ):
                    raise ValueError(
                        "a native Stage's holdout must not be the training "
                        "source Run; use separately pinned evaluation seeds"
                    )
                monitoring.append(holdout)
        if monitoring and len({
            base_run["path"], *(item["path"] for item in alternatives),
            *(item["path"] for item in monitoring),
        }) != len(alternatives) * 2 + 1:
            raise ValueError("fixed native Stage monitoring must not reuse training or other holdout Runs")
        return {
            "plan": plan, "stages": [],
            "rotation": {
                "rounds_per_stage": rotation["rounds_per_stage"],
                "run_configs": alternatives,
                **({"monitor_run_configs": monitoring} if monitoring else {}),
            },
        }

    stages = []
    previous_round = 1
    for item in document["stages"]:
        if (not isinstance(item, dict) or
                set(item) != {"start_round", "run_config"} or
                type(item["start_round"]) is not int or
                not previous_round < item["start_round"] <= total_rounds):
            raise ValueError("curriculum stages need strictly increasing start_round values in 2..rounds")
        run = validated_stage_run(item["run_config"])
        stages.append({"start_round": item["start_round"], "run_config": run})
        previous_round = item["start_round"]
    return {"plan": plan, "stages": stages}


def selected_curriculum_run(base_run, curriculum, round_number):
    """Select one immutable source without modifying the round's checkpoint."""
    run, start = base_run, 1
    if curriculum is not None and curriculum.get("rotation") is not None:
        rotation = curriculum["rotation"]
        period = rotation["rounds_per_stage"]
        sources = [base_run, *rotation["run_configs"]]
        completed_periods = (round_number - 1) // period
        return sources[completed_periods % len(sources)], completed_periods * period + 1
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
    rotation = curriculum.get("rotation")
    if rotation is not None:
        period = rotation["rounds_per_stage"]
        sources = [base_run, *rotation["run_configs"]]
        stages = [
            {"start_round": index * period + 1, "run_config": source}
            for index, source in enumerate(sources)
        ]
    else:
        stages = [{"start_round": 1, "run_config": base_run},
                  *curriculum["stages"]]
    grouped = [[] for _ in stages]
    group_index = 0
    for number, (directory, record) in enumerate(completed, 1):
        if rotation is not None:
            group_index = ((number - 1) // period) % len(stages)
        else:
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
            "end_round": (None if rotation is not None else
                          stages[index + 1]["start_round"] - 1
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
    return {
        "active_stage_start_round": active, "stages": report,
        **({
            "rotation_rounds_per_stage": period,
            "rotation_cycle_rounds": period * len(stages),
            "completed_full_cycles": len(completed) // (period * len(stages)),
        } if rotation is not None else {}),
    }
