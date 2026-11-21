"""Generation release boundaries for the existing native training session.

The training session remains the only optimizer/checkpoint owner. Each
completed generation uses the existing continual registry, authoritative
native match evaluator and portable champion publisher. No extra training
loop, process supervisor or scheduler is introduced here.
"""
import hashlib
import json
from pathlib import Path

from native_continual_release import (
    competency_contract, document, immutable_json, load_config, native_model,
    physical_suite, publish_champion, register, release, ContinualLearningStore,
)
from native_training_artifacts import atomic_json, file_sha256
from train_policy_session import session_lock


def load_plan(path, initial_checkpoint):
    path = path.resolve(strict=True)
    source = document(path)
    required = {
        "schema_version", "registry", "config", "champion_suite",
        "historical_suite", "competency_suite", "rounds_per_generation",
        "game_build_version", "evaluation_binary", "evaluation_seed",
        "evaluation_physics_workers", "evaluation_device",
    }
    if set(source) != required or source["schema_version"] != 1:
        raise ValueError("invalid native continual release plan")
    for key in ("registry", "config", "champion_suite", "historical_suite",
                "competency_suite", "evaluation_binary"):
        if not isinstance(source[key], str) or not source[key]:
            raise ValueError("invalid native continual release plan path: " + key)
        source[key] = str((path.parent / source[key]).resolve(strict=True))
    if (type(source["rounds_per_generation"]) is not int or
            source["rounds_per_generation"] < 1 or
            type(source["evaluation_seed"]) is not int or
            not 0 <= source["evaluation_seed"] < (1 << 63) or
            type(source["evaluation_physics_workers"]) is not int or
            not 1 <= source["evaluation_physics_workers"] <= 128 or
            source["evaluation_device"] not in ("cpu", "cuda:0") or
            not isinstance(source["game_build_version"], str) or
            not source["game_build_version"].strip()):
        raise ValueError("invalid native continual generation or evaluation allocation")
    config = Path(source["config"])
    store = ContinualLearningStore(Path(source["registry"]), load_config(config))
    # The initial optimizer state must belong to a registered native model.
    # Do not silently label a foreign training checkpoint a champion child.
    digest = file_sha256(initial_checkpoint)
    registered = [row for row in store.list_models()
                  if row["artifact_sha256"] == digest]
    if len(registered) != 1:
        raise ValueError("initial native training checkpoint must already be registered")
    parent, _, _ = native_model(store, registered[0]["model_id"])
    contract, _ = competency_contract(Path(source["competency_suite"]))
    if contract != store.permanent_competency_suite():
        raise ValueError("native continual plan does not match permanent competency suite")
    if not store.current_compatible_champion_id():
        raise ValueError("native continual training requires an explicit trusted bootstrap")
    return {
        **source, "plan_sha256": file_sha256(path), "plan_path": str(path),
        "initial_parent_model_id": parent["model_id"],
        "initial_parent_training_step": parent["training_step"],
        "config_sha256": file_sha256(config),
        "binary_sha256": file_sha256(Path(source["evaluation_binary"])),
        "champion_suite_sha256": physical_suite(Path(source["champion_suite"]))[2],
        "historical_suite_sha256": physical_suite(Path(source["historical_suite"]))[2],
        "competency_suite_sha256": file_sha256(Path(source["competency_suite"])),
        "competency_contract": contract,
    }


def check_plan(plan, store):
    for key, hash_key in (("config", "config_sha256"),
                          ("evaluation_binary", "binary_sha256"),
                          ("competency_suite", "competency_suite_sha256")):
        if file_sha256(Path(plan[key])) != plan[hash_key]:
            raise ValueError("native continual input changed: " + key)
    for key in ("champion_suite", "historical_suite"):
        if physical_suite(Path(plan[key]))[2] != plan[key + "_sha256"]:
            raise ValueError("native continual physical suite changed: " + key)
    contract, _ = competency_contract(Path(plan["competency_suite"]))
    if contract != plan["competency_contract"] or contract != store.permanent_competency_suite():
        raise ValueError("native continual competency contract changed")


def complete_generations(root, recipe, completed, *, final=False):
    """Release every due boundary exactly once before the next optimizer round.

    A durable per-boundary receipt is written after the registry transaction
    and publication. Recovery checks the registry's already committed
    decision when a crash happens between those boundaries.
    """
    plan = recipe.get("continual_release")
    if plan is None or not completed:
        return []
    store = ContinualLearningStore(Path(plan["registry"]), load_config(Path(plan["config"])))
    lock = store.root / "native-release"
    lock.mkdir(exist_ok=True)
    interval = plan["rounds_per_generation"]
    step = plan["initial_parent_training_step"]
    parent = plan["initial_parent_model_id"]
    receipts = []
    # Bind the training lineage to the session directory, not a source
    # scene name that may be reused by several independent training jobs.
    run_id = "native-session-" + hashlib.sha256(
        str(root.resolve()).encode("utf-8")).hexdigest()[:24]
    with session_lock(lock):
        check_plan(plan, store)
        for index, (directory, row) in enumerate(completed, 1):
            step += row["learner_metrics"]["optimizer_input_steps"]
            receipt_path = directory / "continual-release.json"
            due = index % interval == 0 or (final and index == len(completed))
            if not due and not receipt_path.exists():
                continue
            checkpoint_sha = row["next_checkpoint_sha256"]
            if receipt_path.exists():
                result = document(receipt_path)
                if (result["round"] != index or result["checkpoint_sha256"] != checkpoint_sha or
                        result["parent_model_id"] != parent or result["training_step"] != step):
                    raise ValueError("completed native release disagrees with checkpoint lineage")
                registered, _, _ = native_model(store, result["model_id"])
                if registered["artifact_sha256"] != checkpoint_sha:
                    raise ValueError("released checkpoint differs from the registry")
            else:
                # Immutable optimizer receipts are checked by the caller
                # before entering this function, including on resume.
                registered = register(
                    store, Path(row["next_manifest"]), Path(row["next_checkpoint"]),
                    recipe["learner_slot"], run_id, step, plan["game_build_version"],
                    parent, Path(recipe["trainer_config"]["path"]),
                )
                model_id = registered["model_id"]
                decision_id = registered.get("evaluation_report_id")
                if registered["status"] in ("champion", "historical", "rejected") and decision_id:
                    # Normal release already committed before controller
                    # interruption. Do not run a new gate against itself.
                    report = document(store.evaluation_dir / "reports" / (decision_id + ".json"))
                    if report["candidate_model_id"] != model_id:
                        raise ValueError("registry release report belongs to another model")
                    passed = report["decision"]["passed"]
                elif registered["status"] in ("candidate", "rejected"):
                    response = release(
                        store, model_id, Path(plan["champion_suite"]),
                        Path(plan["historical_suite"]), Path(plan["competency_suite"]),
                        Path(plan["config"]), Path(plan["evaluation_binary"]),
                        plan["evaluation_seed"], plan["evaluation_physics_workers"],
                        plan["evaluation_device"],
                    )
                    decision_id = response["evaluation"]["report_id"]
                    passed = response["evaluation"]["passed"]
                else:
                    raise ValueError("native generation has unexpected registry status")
                deployment = publish_champion(store)
                result = {
                    "schema_version": 1, "kind": "native_continual_generation_complete",
                    "round": index, "training_step": step,
                    "checkpoint_sha256": checkpoint_sha,
                    "parent_model_id": parent, "model_id": model_id,
                    "evaluation_report_id": decision_id,
                    "passed": passed, "deployment_id": deployment["deployment_id"],
                    "deployed_model_id": deployment["model_id"],
                }
                immutable_json(receipt_path, result)
                print(json.dumps(result, sort_keys=True), flush=True)
            parent = result["model_id"]
            receipts.append(result)
    return receipts
