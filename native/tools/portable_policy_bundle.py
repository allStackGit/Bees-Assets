"""Portable, content-pinned native policy opponent package shared by game publishers.

Game and campaign publications keep the SAME immutable model checkpoint,
source ABI and native inference worker; gameplay's CombatWorld is unchanged.
The chosen Python/backend installations remain host dependencies.
"""

import copy
import json
import os
from pathlib import Path
import re
import shutil

from native_training_artifacts import (
    file_sha256, normalized_python_source_sha256, sync_directory,
)


def existing_artifact(owner: Path, value: str) -> Path:
    if not isinstance(value, str) or not value:
        raise ValueError("missing native policy artifact path")
    return (owner.parent / value).resolve(strict=True)


def read_json_object(path: Path) -> dict:
    value = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(value, dict):
        raise ValueError(f"native policy source must be a JSON object: {path}")
    return value


# The installed policy worker dynamically loads the shared MA-POCA actor.
# Keep that exact implementation beside the published model on all hosts,
# independent of where the original training checkout lived.
POLICY_WORKER_SOURCES = (
    "native/tools/policy_worker.py",
    "native/tools/native_gpu_allocation.py",
)
MLAGENTS_ACTOR_SOURCES = (
    "Training/bees_mlagents_ppo_compat.py",
    "Training/bees_mlagents_structured_policy.py",
    "Training/bees_mlagents_factorized_poca.py",
    "Training/bees_learner_stall_diagnostics.py",
    "Training/bees_log_categories.py",
    "Training/bees_process_safety.py",
)


def source_policy_runtime(run_path: Path, run: dict, runtime_files: dict,
                          uses_mlagents: bool) -> dict:
    """Pin the real native inference code, not a mutable external checkout.

    The worker imports its sibling native_gpu_allocation module and, only
    for ML-Agents checkpoints, a handful of native Training actor modules.
    Python/Torch/ML-Agents are installed platform dependencies; we never
    copy a machine-specific virtual environment or executable.
    """
    worker = existing_artifact(run_path, run.get("policy_worker"))
    if (worker.name != "policy_worker.py" or worker.parent.name != "tools" or
            worker.parent.parent.name != "native"):
        raise ValueError(
            "portable native opponent requires the verified native/tools/policy_worker.py"
        )
    project_root = worker.parent.parent.parent
    sources = POLICY_WORKER_SOURCES + (
        MLAGENTS_ACTOR_SOURCES if uses_mlagents else ()
    )
    pinned = {}
    for name in sources:
        path = (project_root / name).resolve(strict=True)
        if not path.is_file():
            raise ValueError(f"native policy source file is not regular: {path}")
        digest = file_sha256(path)
        normalized = normalized_python_source_sha256(path)
        existing = runtime_files.get(name)
        # Git may check out LF on Linux and CRLF on Windows. Both are
        # identical Python source; compare normalized code identities
        # without changing strict raw-byte SHA-256 publication receipts.
        if existing is not None and existing[2] != normalized:
            raise ValueError(
                f"incompatible native opponent policy source versions: {name}"
            )
        if existing is None:
            runtime_files[name] = path, digest, normalized
        pinned[name] = normalized
    run["policy_worker"] = "native/tools/policy_worker.py"
    # Portable native host resolves auto to python3 on Linux and python
    # on Windows through PATH. No machine-specific virtualenv is copied.
    run["python"] = "auto"
    return pinned


def package_model_manifest(
    run_path: Path, run: dict, label: str,
    outputs: list, model_files: dict, runtime_files: dict, provenance: dict,
) -> dict:
    """Copy every checkpoint used by the verified opponent into its campaign.

    Challenge publication keeps source locations pinned, so simply copying
    its run JSON produces a game tied to a different folder's manifest.
    Preserve model ABI/slots/settings; only rebase the verified file paths.
    The resident native policy process still requires an installed Python
    interpreter and compatible worker/dependencies on the target platform.
    """
    manifest_path = existing_artifact(run_path, run.get("model_manifest"))
    original_sha256 = provenance["selected_model_manifest_sha256"]
    if file_sha256(manifest_path) != original_sha256:
        raise ValueError("selected campaign model manifest changed after verification")
    manifest = read_json_object(manifest_path)
    models = manifest.get("models")
    if (manifest.get("format") != "bees-native-model-v1" or
            manifest.get("abi_version") != 26 or
            not isinstance(models, list) or not models):
        raise ValueError("campaign trained opponent needs a complete native v26 model manifest")
    manifest = copy.deepcopy(manifest)
    seen_slots = set()
    saved_models = []
    for model in manifest["models"]:
        if not isinstance(model, dict):
            raise ValueError("native model slot must be an object")
        slot, digest = model.get("slot"), model.get("sha256")
        if (type(slot) is not int or not 0 <= slot < 2**32 or
                slot in seen_slots or
                not isinstance(digest, str) or
                re.fullmatch(r"[0-9a-f]{64}", digest) is None):
            raise ValueError("native opponent manifest has invalid model slot or SHA-256")
        seen_slots.add(slot)
        source_model = existing_artifact(manifest_path, model.get("path"))
        if not source_model.is_file() or file_sha256(source_model) != digest:
            raise ValueError(f"native opponent checkpoint was changed or removed: {source_model}")
        destination = f"models/{digest}.model"
        model_files.setdefault(destination, source_model)
        model["path"] = destination
        saved_models.append({"slot": slot, "sha256": digest})
    if (file_sha256(manifest_path) != original_sha256 or
            not any(row["slot"] == provenance["selected_learner_slot"] and
                    row["sha256"] == provenance["selected_checkpoint_sha256"]
                    for row in saved_models)):
        raise ValueError(
            "packaged opponent model differs from the authenticated selected training checkpoint"
        )
    runtime_source_sha256 = source_policy_runtime(
        run_path, run, runtime_files,
        any(model.get("backend") == "mlagents" for model in manifest["models"]),
    )
    manifest_name = f"{label}.model-manifest.json"
    outputs.append((manifest_name, manifest))
    run["model_manifest"] = manifest_name
    return {
        "original_manifest_sha256": original_sha256,
        "runtime_source_sha256": runtime_source_sha256,
        "packaged_manifest": manifest_name,
        "model_slots": saved_models,
    }



def write_pinned_policy_assets(
    staged: Path, model_files: dict, runtime_files: dict,
) -> None:
    """Write verified model weights and worker sources under one staged root.

    Caller owns the final atomic directory publication. Model checkpoints
    never get copied without content hashing, and executable source files
    cannot be swapped after the reference digest was pinned.
    """
    assets = [
        (name, source, name.split("/")[-1].removesuffix(".model"))
        for name, source in model_files.items()
    ]
    assets.extend(
        (name, source, digest)
        for name, (source, digest, _) in runtime_files.items()
    )
    for name, source, expected_sha in assets:
        destination = staged / name
        destination.parent.mkdir(parents=True, exist_ok=True)
        if file_sha256(source) != expected_sha:
            raise ValueError(f"native policy source changed before publication: {source}")
        with source.open("rb") as reader, destination.open("xb") as writer:
            shutil.copyfileobj(reader, writer, length=1024 * 1024)
            writer.flush()
            os.fsync(writer.fileno())
        if file_sha256(destination) != expected_sha:
            raise ValueError(f"native policy artifact changed while copying: {source}")
    for folder in sorted(
        {Path(name).parent for name, _, _ in assets},
        key=lambda item: len(item.parts), reverse=True,
    ):
        sync_directory(staged / folder)
