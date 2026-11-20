#!/usr/bin/env python3
"""Evaluate a verified native MA-POCA checkpoint in the original Bees simulation.

The independent Hub poller owns the snapshot and publication cursor. This
module owns only frozen reference-model selection and source-result proof.
"""

import copy
import json
from pathlib import Path
import sys

from experiment_hub_eval_io import SHA256, SUITE_ID, read_bounded

NATIVE = Path(__file__).resolve().parents[1] / "native" / "tools"
if str(NATIVE) not in sys.path:
    sys.path.insert(0, str(NATIVE))

from evaluate_policy import evaluate as evaluate_native
from native_training_artifacts import (
    atomic_json, file_sha256, native_source_contract_sha256,
)

FIELDS = {
    "suite_id", "source_run_config", "source_run_config_sha256",
    "source_contract_sha256", "reference_manifest", "reference_manifest_sha256", "learner_slot",
    "learner_side", "policy_seed", "physics_workers", "success_metric", "trials",
}
METRICS = {"side_victories", "learner_mission_successes"}


def pinned_file(raw, digest):
    if not isinstance(raw, (str, Path)) or not Path(raw).is_absolute():
        raise ValueError("Native evaluation inputs must be installed absolute paths.")
    file = Path(raw)
    if not file.is_file() or file.resolve(strict=True) != file:
        raise ValueError("Native evaluation input is missing or redirected.")
    if not isinstance(digest, str) or not SHA256.fullmatch(digest):
        raise ValueError("Native evaluation input requires its immutable SHA-256.")
    if file_sha256(file) != digest:
        raise ValueError("Native evaluation source or reference changed.")
    return file


def read_suite(source):
    raw = json.loads(read_bounded(source, 8192))
    if not isinstance(raw, dict) or set(raw) != FIELDS:
        raise ValueError("Native Bees suite requires a fixed source, reference, seeds and trial count.")
    if (not isinstance(raw["suite_id"], str) or
        not SUITE_ID.fullmatch(raw["suite_id"]) or
        type(raw["learner_slot"]) is not int or
        not 0 <= raw["learner_slot"] < (1 << 32) or
        type(raw["learner_side"]) is not int or raw["learner_side"] not in (1, 2) or
        type(raw["policy_seed"]) is not int or
        not 0 <= raw["policy_seed"] < (1 << 63) or
        type(raw["physics_workers"]) is not int or
        not 1 <= raw["physics_workers"] <= 128 or
        raw["success_metric"] not in METRICS or
        type(raw["trials"]) is not int or not 1 <= raw["trials"] <= 1000000):
        raise ValueError("Invalid fixed native Bees evaluation parameters.")
    run = pinned_file(raw["source_run_config"], raw["source_run_config_sha256"])
    reference = pinned_file(raw["reference_manifest"], raw["reference_manifest_sha256"])
    if (not isinstance(raw["source_contract_sha256"], str) or
        not SHA256.fullmatch(raw["source_contract_sha256"]) or
        native_source_contract_sha256(run) != raw["source_contract_sha256"]):
        raise ValueError("Native evaluation physical scene or policy contract differs from frozen suite.")
    source_config = json.loads(read_bounded(run, 1024 * 1024))
    count = source_config.get("episodes_per_world", 1)
    levels = source_config.get("levels")
    if (not isinstance(source_config, dict) or type(count) is not int or
        not 1 <= count <= 10000 or
        not isinstance(levels, list) or not levels or
        count * len(levels) != raw["trials"]):
        raise ValueError("Native evaluation trials disagree with its authored physical Levels.")
    # Suite fingerprints describe the SAME work across Windows and Linux.
    # Source and reference paths belong to this trusted machine installation.
    definition = {key: value for key, value in raw.items()
                  if key not in ("source_run_config", "reference_manifest")}
    return definition, {"source": run, "reference": reference}


def fixed_reference_manifest(checkpoint, digest, installed, definition, workspace):
    """Use the exact learner model and fixed ORIGINAL opponent model slots."""
    source = installed["source"]
    reference_file = installed["reference"]
    pinned_file(source, definition["source_run_config_sha256"])
    pinned_file(reference_file, definition["reference_manifest_sha256"])
    if native_source_contract_sha256(source) != definition["source_contract_sha256"]:
        raise ValueError("Native evaluation source scene or policy worker changed.")
    if file_sha256(checkpoint) != digest:
        raise ValueError("Hub-snapshotted native learner checkpoint changed.")
    candidate_file = installed["candidate_manifest"]
    if candidate_file.resolve(strict=True) != candidate_file:
        raise ValueError("Native learned model manifest is redirected.")
    candidate = json.loads(read_bounded(candidate_file, 1024 * 1024))
    reference = json.loads(read_bounded(reference_file, 1024 * 1024))
    if any(candidate.get(key) != reference.get(key)
           for key in ("format", "abi_version", "policy_signature")):
        raise ValueError("Native fixed-reference policy ABIs differ.")
    current_models = candidate.get("models")
    reference_models = reference.get("models")
    if not isinstance(current_models, list) or not isinstance(reference_models, list):
        raise ValueError("Native evaluation needs complete model slot manifests.")
    by_slot = {model.get("slot"): model for model in current_models
               if isinstance(model, dict)}
    originals = {model.get("slot"): model for model in reference_models
                 if isinstance(model, dict)}
    if (len(by_slot) != len(current_models) or
        len(originals) != len(reference_models) or
        set(by_slot) != set(originals) or
        definition["learner_slot"] not in by_slot or
        len(by_slot) < 1):
        raise ValueError("Native learner/reference policy slot membership differs.")
    slot = definition["learner_slot"]
    learner = by_slot[slot]
    if learner.get("backend") != "mlagents" or learner.get("sha256") != digest:
        raise ValueError("Native learner slot does not own the evaluated checkpoint.")
    models = []
    for identifier in sorted(by_slot):
        if identifier == slot:
            models.append({**learner, "path": str(checkpoint)})
            continue
        opponent = originals[identifier]
        if not isinstance(opponent.get("path"), str):
            raise ValueError("Fixed opponent has no pinned model artifact.")
        original_file = pinned_file(
            reference_file.parent / opponent["path"],
            opponent.get("sha256"),
        )
        models.append({**opponent, "path": str(original_file)})
    manifest = copy.deepcopy(candidate)
    manifest["models"] = models
    destination = workspace / "fixed-reference-manifest.json"
    atomic_json(destination, manifest)
    return destination


def evaluate_checkpoint(job, checkpoint, digest, work, suite, installed, binary):
    """Return verified successes, not an estimate from rewards or learner logs."""
    if job.get("final_checkpoint_sha256") != digest:
        raise ValueError("Native Hub evaluation requires a verified final publisher checkpoint.")
    output = work / "native-evaluation"
    candidate_sha = file_sha256(installed["candidate_manifest"])
    binary_sha = file_sha256(binary)
    manifest = fixed_reference_manifest(checkpoint, digest, installed, suite, work)
    report = evaluate_native(
        installed["source"], manifest, output,
        suite["learner_side"], binary, suite["policy_seed"],
        suite["physics_workers"], inference_device="cpu",
    )
    published = json.loads(read_bounded(output / "evaluation-result.json", 1024 * 1024))
    successes = report.get(suite["success_metric"])
    if (published != report or report.get("episodes") != suite["trials"] or
        report.get("model_manifest_sha256") != file_sha256(manifest) or
        report.get("source_run_config_sha256") != suite["source_run_config_sha256"] or
        report.get("policy_sampling_seed") != suite["policy_seed"] or
        report.get("learner_side") != suite["learner_side"] or
        type(successes) is not int or not 0 <= successes <= suite["trials"]):
        raise ValueError("Native physical evaluation does not prove the pinned suite result.")
    pinned_file(installed["source"], suite["source_run_config_sha256"])
    pinned_file(installed["reference"], suite["reference_manifest_sha256"])
    if (native_source_contract_sha256(installed["source"]) != suite["source_contract_sha256"] or
        file_sha256(installed["candidate_manifest"]) != candidate_sha or
        file_sha256(binary) != binary_sha):
        raise ValueError("Native evaluator code, learned manifest or physical source changed.")
    for model in json.loads(read_bounded(manifest, 1024 * 1024))["models"]:
        if model["slot"] != suite["learner_slot"]:
            pinned_file(model["path"], model["sha256"])
    if file_sha256(checkpoint) != digest:
        raise ValueError("Evaluated checkpoint changed before receipt acceptance.")
    return successes
