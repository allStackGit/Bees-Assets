"""Generation release boundaries for the existing native training session.

The training session remains the only optimizer/checkpoint owner. Each
completed generation uses the existing continual registry, authoritative
native match evaluator and portable champion publisher. No extra training
loop, process supervisor or scheduler is introduced here.
"""
import hashlib
import json
import random
import time
import os
import shutil
import tempfile
from contextlib import nullcontext
from pathlib import Path

from native_continual_release import (
    competency_contract, document, immutable_json, load_config, native_model,
    physical_suite, publish_champion, register, release, ContinualLearningStore,
    canonical_json,
)
from native_training_artifacts import (
    atomic_json, file_sha256, check_learner, sync_directory,
    native_manifest_model_dependencies,
)
from bees_continual_historical import _historical_training_weights, _validate_ratio, HistoricalOpponentScheduler
from train_policy_session import session_lock


def load_plan(path, initial_checkpoint, self_play, *, standalone_round=False):
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
    ratio = _validate_ratio(store.config["historical_league"]["training_ratio"])
    if standalone_round and source["rounds_per_generation"] != 1:
        raise ValueError("a Hub learner release owns exactly one completed round per generation")
    if ratio > 0 and self_play is None and not standalone_round:
        raise ValueError("native continual historical league requires the existing self-play opponent refresh schedule")
    return {
        "historical_training_ratio": ratio,
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


def select_round_manifest(root, plan, self_play, round_number, previous_manifest,
                          checkpoint, learner_slot, *, create,
                          portable=False, registry_locked=False,
                          initial_seed_offset=None):
    """Freeze one historical opponent at the existing self-play swap boundary.

    The original GhostTrainer samples a whole non-learning team queue at
    a swap, not an independent opponent for each action. Here the native
    swap interval is the existing completed-round refresh schedule. All
    local and remote collectors of the interval use the same selected
    frozen model; the original next refresh restores normal self-play.
    """
    if (not plan or not plan["historical_training_ratio"] or not self_play or
            (round_number - 1) % self_play["every"] != 0):
        return previous_manifest
    sidecar = root / f"historical-round-{round_number:05d}.json"
    effective = (Path(previous_manifest).parent / "collection-manifest.json"
                 if portable else root / f"historical-round-{round_number:05d}.manifest.json")
    previous_manifest = Path(previous_manifest).resolve(strict=True)
    checkpoint = Path(checkpoint).resolve(strict=True)
    lineage = {
        "schema_version": 1, "kind": "native_historical_opponent_selection",
        "round": round_number, "input_manifest_sha256": file_sha256(previous_manifest),
        "input_checkpoint_sha256": file_sha256(checkpoint),
        "opponent_slot": self_play["opponent_slot"],
        **({"plan_sha256": plan["plan_sha256"]}
           if portable or initial_seed_offset is not None else {}),
        **({"seed_offset": initial_seed_offset}
           if initial_seed_offset is not None else {}),
    }
    if sidecar.exists():
        receipt = document(sidecar)
        if any(receipt.get(key) != value for key, value in lineage.items()):
            raise ValueError("historical opponent selection belongs to another native round")
        selected = effective if receipt["opponent_model_id"] is not None else previous_manifest
        if file_sha256(selected) != receipt["effective_manifest_sha256"]:
            raise ValueError("historical opponent selection manifest changed")
        check_learner(selected, checkpoint, learner_slot)
        if portable or initial_seed_offset is not None:
            native_manifest_model_dependencies(selected)
        return selected
    if not create:
        raise ValueError("completed native historical swap lacks its pinned selection")
    store = ContinualLearningStore(Path(plan["registry"]), load_config(Path(plan["config"])))
    lock = store.root / "native-release"
    lock.mkdir(exist_ok=True)
    selected_id = None
    selected_row = None
    chosen = previous_manifest
    with (nullcontext() if registry_locked else session_lock(lock)):
        if file_sha256(Path(plan["config"])) != plan["config_sha256"]:
            raise ValueError("historical league configuration changed during training")
        champion_id = store.current_compatible_champion_id()
        if not champion_id:
            raise ValueError("historical league has no compatible champion")
        # Existing draw-aware pressure calculation includes evaluated
        # regressions of rejected candidates, not only prior champions.
        weighted = _historical_training_weights(store, champion_id)
        seed = f"{plan['evaluation_seed']}:native-swap:{round_number}"
        if portable:
            # Every Hub job owns one round. Include immutable model identity
            # so independent generations do not repeat the round-one draw.
            seed += ":" + lineage["input_checkpoint_sha256"]
        if initial_seed_offset is not None:
            seed += f":initial:{lineage['input_checkpoint_sha256']}:{initial_seed_offset}"
        rng = random.Random(seed)
        if weighted and rng.random() < plan["historical_training_ratio"]:
            selected_row = HistoricalOpponentScheduler._choose_weighted(weighted, rng)
            selected_id = selected_row["model_id"]
            _, frozen_manifest, frozen_checkpoint = native_model(store, selected_id)
            policy = document(frozen_manifest)["models"][0]
            previous = document(previous_manifest)
            models = []
            for row in previous["models"]:
                if row["slot"] == self_play["opponent_slot"]:
                    # Change only the opponent route; the learner and
                    # any other permitted slots retain their exact bytes.
                    if portable or initial_seed_offset is not None:
                        directory = previous_manifest.parent if portable else root
                        location = portable_opponent(directory, frozen_checkpoint, policy["sha256"])
                        if not portable:
                            location = str(directory / location)
                    else:
                        location = str(frozen_checkpoint)
                    models.append({**policy, "slot": row["slot"], "path": location})
                else:
                    models.append(dict(row) if portable else {**row, "path": str(
                        (previous_manifest.parent / row["path"]).resolve(strict=True))})
            if len([row for row in models if row["slot"] == self_play["opponent_slot"]]) != 1:
                raise ValueError("historical self-play route is missing")
            previous["models"] = models
            # A crash before the small sidecar is committed leaves only
            # an unissued manifest: it can be replaced before collection.
            atomic_json(effective, previous)
            chosen = effective
        check_learner(chosen, checkpoint, learner_slot)
        if portable or initial_seed_offset is not None:
            native_manifest_model_dependencies(chosen)
        receipt = {
            **lineage, "champion_model_id": champion_id,
            "opponent_model_id": selected_id,
            "selected_weight": selected_row["weight"] if selected_row else None,
            "selected_regression": selected_row["regression"] if selected_row else None,
            "effective_manifest_sha256": file_sha256(chosen),
        }
        immutable_json(sidecar, receipt)
        print(json.dumps(receipt, sort_keys=True), flush=True)
    return chosen


def portable_opponent(directory, source, digest):
    """Publish the registry-selected policy in the existing SHA-addressed
    learner package. Never put registry-local absolute paths on the wire.
    """
    opponents = directory / "opponents"
    opponents.mkdir(exist_ok=True)
    target = opponents / (digest + ".model")
    if not target.exists():
        try:
            os.link(source, target)
        except OSError:
            # A cross-volume publication is atomic and durable, including
            # recovery after interrupted copies on Windows.
            with tempfile.NamedTemporaryFile(dir=opponents, delete=False) as stream:
                pending = Path(stream.name)
                try:
                    with source.open("rb") as original:
                        shutil.copyfileobj(original, stream)
                    stream.flush()
                    os.fsync(stream.fileno())
                except BaseException:
                    stream.close()
                    pending.unlink(missing_ok=True)
                    raise
            try:
                if file_sha256(pending) != digest:
                    raise ValueError("historical opponent changed during copy")
                os.replace(pending, target)
            finally:
                pending.unlink(missing_ok=True)
        sync_directory(opponents)
    if target.is_symlink() or file_sha256(target) != digest:
        raise ValueError("published historical opponent does not match registry")
    return "opponents/" + digest + ".model"


def release_measurements(store, report_id, model_id, passed):
    """Project actual immutable registry evidence, never re-evaluate a model
    or infer success from the promotion decision alone.
    """
    report = document(store.evaluation_dir / "reports" / (report_id + ".json"))
    digest = hashlib.sha256(canonical_json(report).encode("utf-8")).hexdigest()
    if (report_id != "eval-" + digest[:24] or
            report["candidate_model_id"] != model_id or
            report["decision"]["passed"] is not passed):
        raise ValueError("native release measurement belongs to another registry decision")
    comparison = report["candidate_vs_champion"]
    historical = report["historical"]
    competencies = report["competencies"]
    latency = report["evaluator"]["runtime_latency"]
    result = {
        "report_sha256": digest,
        "evaluated_champion_model_id": report["champion_model_id"],
        "champion_matches": comparison["matches"],
        "champion_wins": comparison["wins"],
        "champion_losses": comparison["losses"],
        "champion_draws": comparison["draws"],
        "champion_timeouts": comparison["timeouts"],
        "historical_opponents": len(historical),
        "historical_candidate_matches": sum(row["matches"] for row in historical),
        "competency_cases": len(competencies),
        "competency_cases_passed": sum(row["score"] >= row["minimum"] for row in competencies),
        "candidate_inference_calls": latency["candidate_inference_calls"],
        "candidate_max_inference_milliseconds": latency["observed_candidate_max_milliseconds"],
        "latency_passed": latency["passed"],
        "champion_gate_short_circuited": "short_circuit" in report["evaluator"],
    }
    if historical:
        result["historical_min_score_delta"] = min(
            row["candidate_score_rate"] - row["baseline_score_rate"]
            for row in historical
        )
    return result


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
        checked_plan = False
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
                registered = store.get_model(result["model_id"])
                if registered["artifact_sha256"] != checkpoint_sha:
                    raise ValueError("released checkpoint differs from the registry")
                if ("evaluation_summary" in result and
                        result["evaluation_summary"] != release_measurements(
                            store, result["evaluation_report_id"], result["model_id"],
                            result["passed"])):
                    raise ValueError("committed release measurements differ from the registry")
            else:
                release_started = time.perf_counter()
                if not checked_plan:
                    check_plan(plan, store)
                    checked_plan = True
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
                measurements = release_measurements(store, decision_id, model_id, passed)
                deployment = publish_champion(store)
                selection = None
                if row.get("hub_historical_selection"):
                    slot = row["learner_metrics"]["refreshed_opponent_slot"]
                    select_round_manifest(
                        directory, plan, {"every": 1, "opponent_slot": slot},
                        1, Path(row["next_manifest"]), Path(row["next_checkpoint"]),
                        recipe["learner_slot"], create=True, portable=True,
                        registry_locked=True,
                    )
                    selection = document(directory / "historical-round-00001.json")
                result = {
                    "schema_version": 1, "kind": "native_continual_generation_complete",
                    "round": index, "training_step": step,
                    "checkpoint_sha256": checkpoint_sha,
                    "parent_model_id": parent, "model_id": model_id,
                    "evaluation_report_id": decision_id,
                    "passed": passed, "evaluation_summary": measurements,
                    "deployment_id": deployment["deployment_id"],
                    "deployed_model_id": deployment["model_id"],
                    "release_attempt_wall_seconds": time.perf_counter() - release_started,
                    **({"collection_selection": selection} if selection is not None else {}),
                }
                immutable_json(receipt_path, result)
                print(json.dumps(result, sort_keys=True), flush=True)
            parent = result["model_id"]
            receipts.append(result)
    return receipts
