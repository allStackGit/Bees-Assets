#!/usr/bin/env python3
"""Independent native Bees MA-POCA evaluation against a fixed reference model.

This does not train, mutate, or infer a winner from training metrics. It uses
the existing native rollout evaluator on a fixed physical scene and a
candidate-only replacement of the frozen reference manifest's learner slot.
The Hub's independent evaluator scheduler publishes the evaluation receipt.
"""

import argparse
import hashlib
import json
import os
from pathlib import Path
import sys
import tempfile

from experiment_hub_eval_io import SHA256, SUITE_ID, UUID, publish, read_bounded, suite_digest

_NATIVE_TOOLS = Path(__file__).resolve().parents[1] / "native" / "tools"
sys.path.insert(0, str(_NATIVE_TOOLS))
from native_training_artifacts import file_sha256, normalized_python_source_sha256
from evaluate_policy import evaluate as evaluate_native_policy


def read_suite(file: Path) -> dict:
    suite = json.loads(read_bounded(file, 8192))
    keys = {
        "suite_id", "run_config", "run_config_sha256", "scene_sha256",
        "reference_manifest", "reference_manifest_sha256",
        "policy_worker_sha256", "rollout_binary_sha256",
        "learner_slot", "learner_side", "policy_seed", "trials",
    }
    if (not isinstance(suite, dict) or set(suite) != keys or
            not isinstance(suite.get("suite_id"), str) or
            not SUITE_ID.fullmatch(suite["suite_id"]) or
            not all(isinstance(suite.get(key), str) and SHA256.fullmatch(suite[key])
                    for key in keys if key.endswith("_sha256")) or
            any(not isinstance(suite.get(key), str) or not suite[key] or
                "\x00" in suite[key] for key in ("run_config", "reference_manifest")) or
            type(suite.get("learner_slot")) is not int or
            not 0 <= suite["learner_slot"] < (1 << 32) or
            type(suite.get("learner_side")) is not int or
            suite["learner_side"] not in (1, 2) or
            type(suite.get("policy_seed")) is not int or
            not 0 <= suite["policy_seed"] < (1 << 63) or
            type(suite.get("trials")) is not int or
            not 1 <= suite["trials"] <= 10000):
        raise ValueError("Invalid fixed-reference native Bees evaluation suite.")
    return suite


def pinned_source(suite_file: Path, relative: str, digest: str) -> Path:
    path = (suite_file.parent.resolve(strict=True) / relative).resolve(strict=True)
    if not path.is_file() or file_sha256(path) != digest:
        raise ValueError(f"Native Bees evaluation source changed: {relative}")
    return path


def source_inputs(suite_file: Path, suite: dict):
    run_file = pinned_source(suite_file, suite["run_config"], suite["run_config_sha256"])
    reference = pinned_source(
        suite_file, suite["reference_manifest"], suite["reference_manifest_sha256"],
    )
    reference_data = json.loads(read_bounded(reference, 131072))
    reference_models = reference_data.get("models")
    if (reference_data.get("format") != "bees-native-model-v1" or
            reference_data.get("abi_version") != 26 or
            not isinstance(reference_models, list) or not reference_models or
            any(not isinstance(model, dict) or type(model.get("slot")) is not int or
                not 0 <= model["slot"] < (1 << 32)
                for model in reference_models)):
        raise ValueError("Native holdout reference requires real v26 model slots.")
    reference_slots = {model["slot"] for model in reference_models}
    if (len(reference_slots) != len(reference_models) or
            suite["learner_slot"] not in reference_slots):
        raise ValueError("Native holdout reference has duplicate or missing model slots.")
    config = json.loads(read_bounded(run_file, 8 * 1024 * 1024))
    if (not isinstance(config, dict) or not isinstance(config.get("native_stage"), dict) or
            not isinstance(config.get("levels"), list) or not config["levels"] or
            type(config.get("episodes_per_world", 1)) is not int or
            len(config["levels"]) * config.get("episodes_per_world", 1) != suite["trials"]):
        raise ValueError("Evaluation suite must use a native-authored fixed trial count.")
    # Holdouts commonly reuse one scene across hundreds of native worlds.
    # Read and parse that physical roster exactly once, then validate only
    # each world's small model routing table. No world-sized JSON copies.
    scenes = set()
    for level in config["levels"]:
        if not isinstance(level, dict) or not isinstance(level.get("scene"), str):
            raise ValueError("Every native evaluation Level needs an authored scene.")
        scenes.add((run_file.parent / level["scene"]).resolve(strict=True))
    if len(scenes) != 1:
        raise ValueError("Native evaluation requires one frozen physical scene.")
    scene = next(iter(scenes))
    if file_sha256(scene) != suite["scene_sha256"]:
        raise ValueError("Native evaluation scene differs from its frozen suite.")
    snapshot = json.loads(read_bounded(scene, 8 * 1024 * 1024))
    if not isinstance(snapshot.get("ships"), list):
        raise ValueError("Native evaluation requires physical policy-controlled ships.")
    teams_by_side = {}
    for ship in snapshot["ships"]:
        if not isinstance(ship, dict):
            raise ValueError("Native evaluation has an invalid source ship.")
        policy = ship.get("policy")
        if policy is None:
            continue
        if not isinstance(policy, dict):
            raise ValueError("Native evaluation has an invalid source policy controller.")
        teams_by_side.setdefault(ship.get("side"), set()).add(policy.get("team_id"))
    if not teams_by_side.get(suite["learner_side"]):
        raise ValueError("Native evaluation learner side has no real policy agents.")
    for level in config["levels"]:
        routed = level.get("model_slots")
        if not isinstance(routed, dict):
            raise ValueError("Every native evaluation Level requires explicit model slots.")
        for side, teams in teams_by_side.items():
            for team in teams:
                model_slot = routed.get(str(team))
                if type(model_slot) is not int or model_slot not in reference_slots:
                    raise ValueError("Native evaluation Level refers to an unavailable model slot.")
                if side == suite["learner_side"] and model_slot != suite["learner_slot"]:
                    raise ValueError("Evaluation learner side is not controlled by the learner slot.")
                if side != suite["learner_side"] and model_slot == suite["learner_slot"]:
                    raise ValueError("Evaluation learner controls both physical sides.")
    worker = (run_file.parent / config["policy_worker"]).resolve(strict=True)
    if normalized_python_source_sha256(worker) != suite["policy_worker_sha256"]:
        raise ValueError("Native policy inference implementation changed.")
    return run_file, reference, scene, worker


def evaluate(checkpoint: Path, checkpoint_digest: str, source_manifest: Path,
             suite_file: Path, suite: dict, work: Path, rollout_binary: Path) -> int:
    run_file, reference_path, scene, worker = source_inputs(suite_file, suite)
    rollout_binary = rollout_binary.resolve(strict=True)
    if file_sha256(rollout_binary) != suite["rollout_binary_sha256"]:
        raise ValueError("Native evaluation executable changed from the frozen benchmark.")
    if not SHA256.fullmatch(checkpoint_digest) or file_sha256(checkpoint) != checkpoint_digest:
        raise ValueError("Native learner checkpoint no longer matches its approved bytes.")
    manifest_digest = file_sha256(source_manifest)
    candidate = json.loads(read_bounded(source_manifest, 131072))
    reference = json.loads(read_bounded(reference_path, 131072))
    if (candidate.get("format") != "bees-native-model-v1" or
            candidate.get("abi_version") != 26 or
            any(candidate.get(field) != reference.get(field)
                for field in ("format", "abi_version", "policy_signature")) or
            not isinstance(candidate.get("models"), list) or
            not isinstance(reference.get("models"), list)):
        raise ValueError("Candidate and reference require the same frozen native v26 ABI.")
    learner = [
        m for m in candidate["models"] if m.get("slot") == suite["learner_slot"]
    ]
    if (len(learner) != 1 or learner[0].get("backend") != "mlagents" or
            learner[0].get("sha256") != checkpoint_digest):
        raise ValueError("Candidate learner slot does not match the approved checkpoint.")
    slots = [m.get("slot") for m in reference["models"]]
    if len(slots) != len(set(slots)) or suite["learner_slot"] not in slots:
        raise ValueError("Native evaluation reference has invalid model routing.")

    # Only the learner changes across candidates. Every other policy uses
    # the same immutable reference bytes, including all temporary minions.
    models = []
    for model in reference["models"]:
        if model["slot"] == suite["learner_slot"]:
            models.append({
                **learner[0], "path": str(checkpoint.resolve()), "device": "cpu",
            })
            continue
        original = (reference_path.parent / model["path"]).resolve(strict=True)
        if file_sha256(original) != model["sha256"]:
            raise ValueError("Fixed native reference opponent weights changed.")
        cpu = dict(model)
        cpu["path"] = str(original)
        if cpu["backend"] == "onnx":
            if cpu.get("providers") not in (None, ["CPUExecutionProvider"]):
                raise ValueError("Independent native benchmark requires CPU-only ONNX reference inference.")
            cpu.pop("device", None)
        else:
            cpu["device"] = "cpu"
        models.append(cpu)
    derived = {**reference, "models": models}
    frozen = work / "fixed-reference-manifest.json"
    with frozen.open("x", encoding="utf-8") as stream:
        json.dump(derived, stream, indent=2)
        stream.write("\n")
    receipt = evaluate_native_policy(
        run_file, frozen, work / "native-evaluation",
        suite["learner_side"], rollout_binary, suite["policy_seed"],
    )
    if (receipt.get("kind") != "native_policy_evaluation_complete" or
            receipt.get("episodes") != suite["trials"] or
            type(receipt.get("side_victories")) is not int or
            not 0 <= receipt["side_victories"] <= suite["trials"] or
            file_sha256(checkpoint) != checkpoint_digest or
            file_sha256(source_manifest) != manifest_digest):
        raise ValueError("Native evaluation did not complete the verified candidate trials.")
    # A running evaluator must not silently switch its scenario, reference
    # actor, or native executable partway through a held-out benchmark.
    source_inputs(suite_file, suite)
    if file_sha256(rollout_binary) != suite["rollout_binary_sha256"]:
        raise ValueError("Native evaluation executable changed while benchmarking.")
    print(json.dumps({
        "kind": "native_bees_independent_evaluation_observation",
        "candidate_checkpoint_sha256": checkpoint_digest,
        "suite_id": suite["suite_id"],
        "victories": receipt["side_victories"],
        "trials": receipt["episodes"],
        "world_steps": receipt["world_steps"],
        "policy_rows": receipt["policy_rows"],
        "wall_seconds": receipt["wall_seconds"],
        "world_steps_per_wall_second": receipt["world_steps_per_wall_second"],
    }, sort_keys=True), flush=True)
    return receipt["side_victories"]


def create_suite_main(arguments) -> int:
    """Freeze a portable suite definition from the actual native run inputs."""
    parser = argparse.ArgumentParser(
        description="Create an immutable native Bees Hub benchmark suite",
    )
    parser.add_argument("--suite-id", required=True)
    parser.add_argument("--run-config", type=Path, required=True,
                        help="fixed-seed native holdout-run.json from bootstrap")
    parser.add_argument("--reference-manifest", type=Path, required=True,
                        help="initial fixed reference policy manifest.json")
    parser.add_argument("--rollout-binary", type=Path, required=True)
    parser.add_argument("--learner-slot", type=int, required=True)
    parser.add_argument("--learner-side", type=int, required=True)
    parser.add_argument("--policy-seed", type=int, default=0)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args(arguments)
    if not SUITE_ID.fullmatch(args.suite_id):
        parser.error("invalid suite ID")
    run = args.run_config.resolve(strict=True)
    reference = args.reference_manifest.resolve(strict=True)
    binary = args.rollout_binary.resolve(strict=True)
    output = args.output.resolve()
    config = json.loads(read_bounded(run, 8 * 1024 * 1024))
    if (not isinstance(config, dict) or not isinstance(config.get("native_stage"), dict) or
            not isinstance(config.get("levels"), list) or not config["levels"] or
            not isinstance(config.get("policy_worker"), str)):
        parser.error("requires a native-authored policy run with fixed seeded Levels")
    scenes = {
        (run.parent / level["scene"]).resolve(strict=True)
        for level in config["levels"]
    }
    if len(scenes) != 1:
        parser.error("use one physical native scene per independent suite")
    episodes = config.get("episodes_per_world", 1)
    if type(episodes) is not int or not 1 <= episodes <= 10000:
        parser.error("native evaluation episodes_per_world must be 1..10000")
    def relative(file):
        try:
            return os.path.relpath(file, output.parent)
        except ValueError:
            # Windows may place project and evaluator configuration on
            # separate drives. The exact bytes remain pinned regardless.
            return str(file)
    worker = (run.parent / config["policy_worker"]).resolve(strict=True)
    suite = {
        "suite_id": args.suite_id,
        "run_config": relative(run),
        "run_config_sha256": file_sha256(run),
        "scene_sha256": file_sha256(next(iter(scenes))),
        "reference_manifest": relative(reference),
        "reference_manifest_sha256": file_sha256(reference),
        "policy_worker_sha256": normalized_python_source_sha256(worker),
        "rollout_binary_sha256": file_sha256(binary),
        "learner_slot": args.learner_slot,
        "learner_side": args.learner_side,
        "policy_seed": args.policy_seed,
        "trials": len(config["levels"]) * episodes,
    }
    if (output.exists() or not output.parent.is_dir() or
            not 1 <= suite["trials"] <= 10000):
        parser.error("suite output must be new and trial count must be 1..10000")
    # Validate its ABI/side and physical source contract before publishing.
    if (not 0 <= args.learner_slot < (1 << 32) or
            args.learner_side not in (1, 2) or
            not 0 <= args.policy_seed < (1 << 63)):
        parser.error("invalid learner slot, physical side or policy sampling seed")
    # Nothing is written until the candidate suite has passed its source
    # scene, team routing, worker and artifact hash contract.
    source_inputs(output, suite)
    with output.open("x", encoding="utf-8", newline="\n") as stream:
        json.dump(suite, stream, indent=2)
        stream.write("\n")
        stream.flush()
        os.fsync(stream.fileno())
    print(json.dumps({
        "suite": str(output), "suite_id": suite["suite_id"],
        "suite_sha256": suite_digest(suite), "trials": suite["trials"],
    }, sort_keys=True))
    return 0


def main():
    if len(sys.argv) >= 2 and sys.argv[1] == "create-suite":
        try:
            return create_suite_main(sys.argv[2:])
        except (OSError, ValueError, RuntimeError, KeyError, TypeError) as exc:
            print(f"Native Bees suite creation failed: {exc}", file=sys.stderr)
            return 1
    parser = argparse.ArgumentParser(description=__doc__)
    for key in ("checkpoint", "candidate-manifest", "suite", "rollout-binary",
                "scratch", "receipt-root"):
        parser.add_argument("--" + key, required=True, type=Path)
    parser.add_argument("--dispatch-id", required=True)
    parser.add_argument("--checkpoint-sha256", required=True)
    args = parser.parse_args()
    try:
        if not UUID.fullmatch(args.dispatch_id):
            raise ValueError("Invalid Hub native Bees dispatch identity.")
        suite = read_suite(args.suite)
        checkpoint = args.checkpoint.resolve(strict=True)
        manifest = args.candidate_manifest.resolve(strict=True)
        scratch = args.scratch.resolve(strict=True)
        receipts = args.receipt_root.resolve(strict=True)
        references = source_inputs(args.suite.resolve(strict=True), suite)
        protected = [checkpoint.parent, manifest.parent, *[p.parent for p in references]]
        if (not scratch.is_dir() or not receipts.is_dir() or
                scratch == receipts or
                scratch.is_relative_to(receipts) or receipts.is_relative_to(scratch) or
                any(scratch.is_relative_to(root) or root.is_relative_to(scratch) or
                    receipts.is_relative_to(root) or root.is_relative_to(receipts)
                    for root in protected)):
            raise ValueError("Independent evaluation scratch and receipts must be outside training and scenario artifacts.")
        with tempfile.TemporaryDirectory(prefix="native-bees-eval-", dir=scratch) as directory:
            wins = evaluate(
                checkpoint, args.checkpoint_sha256, manifest,
                args.suite.resolve(strict=True), suite, Path(directory),
                args.rollout_binary,
            )
        result = {
            "schema_version": 1, "suite_id": suite["suite_id"],
            "suite_sha256": suite_digest(suite),
            "dispatch_id": args.dispatch_id,
            "checkpoint_sha256": args.checkpoint_sha256,
            "trials": suite["trials"], "successes": wins,
        }
        print(publish(receipts, result))
        print(f"Native Bees fixed-reference victories: {wins}/{suite['trials']}")
        return 0
    except (OSError, ValueError, RuntimeError, KeyError, TypeError) as exc:
        print(f"Independent native Bees evaluation failed: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
