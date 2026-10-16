"""Guarded one-time repair of the mislabeled v24 shared-policy training run.

The 2026-10-15 a2d3cd9719e5 Unity build and live checkpoint were ABI v24,
but the persisted lifecycle/release policy fields still described ABI v23.
This command only repairs that exact mislabeling, never model tensors.
"""

from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
from typing import Any, Mapping

from bees_run_lifecycle import _atomic_json, compatibility_key, contract_payload

OLD_BUILD_SOURCE = "a2d3cd9719e5"
AUDIT_FIELDS = frozenset((
    "structured_policy_source_sha256",
    "optimizer_compat_source_sha256",
    "factorized_poca_source_sha256",
    "learner_launcher_source_sha256",
))
REPAIRED_FIELDS = frozenset(("policy_abi_version", "policy_signature"))
SHARED_SHAPES = {
    "action_model.movement.mu.weight": (2, 129),
    "action_model.communication.mu.weight": (4, 129),
    "action_model.weapon_aim.mu.weight": (2, 193),
    "action_model.weapon_fire.weight": (2, 193),
    "action_model.special.weight": (5, 129),
}


def _read(path: Path) -> dict[str, Any]:
    data = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(data, dict):
        raise ValueError(f"{path.name} must contain a JSON object")
    return data


def _backup(path: Path, original: Mapping[str, Any]) -> None:
    backup = path.with_name(path.name + ".pre-v24-policy-repair.json")
    if backup.exists():
        if _read(backup) != dict(original):
            raise ValueError(f"existing repair backup does not match original: {backup}")
        return
    backup.parent.mkdir(parents=True, exist_ok=True)
    with backup.open("x", encoding="utf-8") as handle:
        json.dump(dict(original), handle, indent=2, sort_keys=True)
        handle.write("\n")
        handle.flush()
        os.fsync(handle.fileno())


def _validate_checkpoint(path: Path) -> int:
    if not path.is_file():
        raise ValueError(f"existing optimizer checkpoint is missing: {path}")
    try:
        import torch
    except ImportError as exc:
        raise RuntimeError("repair requires PyTorch to inspect the existing checkpoint") from exc
    # Local training checkpoint. weights_only prevents executing arbitrary pickle objects.
    checkpoint = torch.load(str(path), map_location="cpu", weights_only=True)
    if not isinstance(checkpoint, dict):
        raise ValueError("checkpoint root is not a state dictionary")
    policy = checkpoint.get("Policy")
    adam = checkpoint.get("Optimizer:adam")
    critic = checkpoint.get("Optimizer:critic")
    steps = checkpoint.get("global_step")
    if not isinstance(policy, dict) or not isinstance(adam, dict) or not isinstance(critic, dict):
        raise ValueError("checkpoint is missing Policy or the actor/critic optimizer state")
    if not isinstance(adam.get("state"), dict) or not adam["state"]:
        raise ValueError("checkpoint has no trained actor optimizer state")
    if not isinstance(steps, dict):
        raise ValueError("checkpoint is missing trainer global-step state")
    step_values = []
    for value in steps.values():
        if isinstance(value, (int, float)):
            step_values.append(int(value))
        elif isinstance(value, torch.Tensor) and value.numel() == 1:
            step_values.append(int(value.item()))
    step = max(step_values, default=0)
    if step <= 0:
        raise ValueError("checkpoint has no positive training step count")
    for key, shape in SHARED_SHAPES.items():
        tensor = policy.get(key)
        if tensor is None or tuple(tensor.shape) != shape:
            raise ValueError(f"checkpoint is not the expected v24 shared actor: {key}")
    if any(key.startswith(("action_model.bee_", "action_model.human_")) for key in policy):
        raise ValueError("checkpoint still contains v23 faction-specific action heads")
    return step


def _original_pair(state_path: Path, release_path: Path) -> tuple[dict[str, Any], dict[str, Any]]:
    state_backup = state_path.with_name(state_path.name + ".pre-v24-policy-repair.json")
    release_backup = release_path.with_name(release_path.name + ".pre-v24-policy-repair.json")
    state = _read(state_backup) if state_backup.exists() else _read(state_path)
    release = _read(release_backup) if release_backup.exists() else _read(release_path)
    return state, release


def inspect(assets_root: Path, bees_root: Path) -> dict[str, Any]:
    state_path = bees_root / "Training" / "RunLifecycle" / "current.json"
    release_path = bees_root / "Builds" / "latest-training-release.json"
    state = _read(state_path)
    release = _read(release_path)
    original_state, original_release = _original_pair(state_path, release_path)
    old_contract = original_state.get("contract")
    if not isinstance(old_contract, dict) or original_release.get("contract") != old_contract:
        raise ValueError("original lifecycle and release contracts disagree")
    old_key = compatibility_key(old_contract)
    if original_state.get("compatibility_key") != old_key or original_release.get("compatibility_key") != old_key:
        raise ValueError("original persisted compatibility keys do not match their contract")
    run_id = original_state.get("run_id")
    if not isinstance(run_id, str) or not run_id or original_release.get("run_id") != run_id:
        raise ValueError("release and lifecycle do not name the same training run")
    build_id = original_release.get("build_id")
    if (original_release.get("source_commit") != OLD_BUILD_SOURCE or
            not isinstance(build_id, str) or not build_id.endswith("-" + OLD_BUILD_SOURCE)):
        raise ValueError("repair is restricted to the known v24 Unity build originally mislabeled v23")
    if old_contract.get("policy_abi_version") != 23:
        raise ValueError("original contract is not ABI v23")
    old_signature = old_contract.get("policy_signature")
    if not isinstance(old_signature, str) or not old_signature.startswith("bees-rl-v23|"):
        raise ValueError("original policy signature is not v23")

    source = contract_payload(assets_root)
    new_key = compatibility_key(source)
    if source.get("policy_abi_version") != 24 or new_key == old_key:
        raise ValueError("current source is not the expected corrected v24 policy contract")
    converted = (old_signature
        .replace("bees-rl-v23|", "bees-rl-v24|", 1)
        .replace("network=structured-dual-faction-128x3",
                 "network=structured-shared-faction-conditioned-128x3", 1)
        .replace("weapon-head=shared-per-slot-faction-specific",
                 "weapon-head=shared-per-slot-faction-conditioned", 1))
    if converted != source.get("policy_signature"):
        raise ValueError("v23-to-v24 signature discrepancy is not the known metadata mistake")
    semantic_mismatches = [
        field for field in set(old_contract) | set(source)
        if field not in REPAIRED_FIELDS | AUDIT_FIELDS and old_contract.get(field) != source.get(field)
    ]
    if semantic_mismatches:
        raise ValueError("other RL contract fields changed: " + ", ".join(sorted(semantic_mismatches)))

    for name, item in (("lifecycle", state), ("release", release)):
        key = item.get("compatibility_key")
        if item.get("run_id") != run_id or key not in (old_key, new_key):
            raise ValueError(f"{name} identity changed since the original run")
        actual = item.get("contract")
        if key == old_key and actual != old_contract:
            raise ValueError(f"{name} old contract unexpectedly changed")
        if key == new_key and compatibility_key(actual or {}) != new_key:
            raise ValueError(f"{name} repaired contract is inconsistent")
        if key == new_key:
            marker = item.get("v24_metadata_repair")
            if (not isinstance(marker, dict) or marker.get("from_key") != old_key or
                    marker.get("to_key") != new_key or marker.get("original_build") != build_id):
                raise ValueError(f"{name} has no expected metadata-repair provenance")
    if release.get("build_id") != build_id:
        raise ValueError("release build changed during metadata repair")

    checkpoint = bees_root / "Training" / "trainer-results" / run_id / "BeesRL1v1" / "checkpoint.pt"
    steps = _validate_checkpoint(checkpoint)
    return {
        "run_id": run_id,
        "build_id": build_id,
        "old_key": old_key,
        "new_key": new_key,
        "checkpoint_steps": steps,
        "state_path": str(state_path),
        "release_path": str(release_path),
        "already_local_repaired": (
            state.get("compatibility_key") == new_key
            and release.get("compatibility_key") == new_key
        ),
    }


def apply(assets_root: Path, bees_root: Path) -> dict[str, Any]:
    info = inspect(assets_root, bees_root)
    state_path = Path(info["state_path"])
    release_path = Path(info["release_path"])
    original_state, original_release = _original_pair(state_path, release_path)
    # Never overwrite the original evidence; both snapshots must exist before mutation.
    _backup(state_path, original_state)
    _backup(release_path, original_release)
    state = _read(state_path)
    release = _read(release_path)
    corrected_contract = contract_payload(assets_root)
    marker = {
        "from_key": info["old_key"],
        "to_key": info["new_key"],
        "original_build": info["build_id"],
    }
    if state.get("compatibility_key") != info["new_key"]:
        state["compatibility_key"] = info["new_key"]
        state["contract"] = corrected_contract
        state["v24_metadata_repair"] = marker
        _atomic_json(state_path, state)
    if release.get("compatibility_key") != info["new_key"]:
        original_previous_key = release.get("previous_compatibility_key")
        release["compatibility_key"] = info["new_key"]
        release["contract"] = corrected_contract
        release["previous_compatibility_key"] = info["old_key"]
        # Enable interrupted same-run cutover recovery if server staging fails.
        release["v24_metadata_repair"] = {
            **marker,
            "original_previous_run_id": release.get("previous_run_id"),
            "original_previous_compatibility_key": original_previous_key,
        }
        release["previous_run_id"] = info["run_id"]
        release["incompatible"] = True
        release["preserve_run_override"] = True
        _atomic_json(release_path, release)
    result = inspect(assets_root, bees_root)
    if not result["already_local_repaired"]:
        raise RuntimeError("local v24 metadata repair did not complete")
    return result


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("mode", choices=("check", "apply"))
    parser.add_argument("--assets-root", required=True)
    parser.add_argument("--bees-root", required=True)
    args = parser.parse_args()
    info = (inspect if args.mode == "check" else apply)(
        Path(args.assets_root), Path(args.bees_root)
    )
    print(json.dumps(info, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
