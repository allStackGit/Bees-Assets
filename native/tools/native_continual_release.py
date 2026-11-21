#!/usr/bin/env python3
"""Native continual-learning release over the existing Bees SQLite registry.

Candidates, history, competency contracts, report decisions and promotion
transactions have one owner: ContinualLearningStore. This module translates
authored native Levels and their verified paired battle receipts into the
existing release report. No Unity executable or separate league is used.
"""
import argparse
import hashlib
import json
import os
import shutil
import tempfile
from pathlib import Path
import sys

from evaluate_native_champion import document, gate, pinned_model
from native_training_artifacts import atomic_json, file_sha256, native_input_dependencies, sync_directory
from package_policy_manifest import export_model, verify_package
from train_policy_session import session_lock

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "Training"))
from bees_continual_learning import ContinualLearningStore, canonical_json, load_config, STATE_CHAMPION
from bees_continual_deployment import _promotion_evidence
from bees_continual_bootstrap import bootstrap_champion
from bees_continual_behavior_sanity import apply_behavior_sanity


def immutable_json(path, value):
    if path.exists():
        if document(path) != value:
            raise ValueError(f"immutable native release contract changed: {path}")
    else:
        path.parent.mkdir(parents=True, exist_ok=True)
        atomic_json(path, value)


def native_model(store, model_id):
    row = store.get_model(model_id)
    expected = store.compatibility.to_dict()
    if any(row.get(key) != value for key, value in expected.items()):
        raise ValueError(f"incompatible registered native policy: {model_id}")
    policy = row.get("metadata", {}).get("native_policy")
    checkpoint = Path(row["artifact_path"]).resolve(strict=True)
    if (not isinstance(policy, dict) or policy.get("backend") != "mlagents" or
            policy.get("sha256") != row["artifact_sha256"] or
            file_sha256(checkpoint) != row["artifact_sha256"]):
        raise ValueError(f"registered model lacks an authenticated native MA-POCA checkpoint: {model_id}")
    manifest = store.models_dir / "native-manifests" / (model_id + ".json")
    immutable_json(manifest, {
        "format": "bees-native-model-v1",
        "abi_version": store.config["policy_abi_version"],
        "policy_signature": store.config["policy_signature"],
        "models": [{**policy, "slot": 0, "path": str(checkpoint)}],
    })
    return row, manifest, checkpoint


def register(store, manifest, checkpoint, slot, run_id, step, build, parent, trainer_config):
    manifest = manifest.resolve(strict=True)
    checkpoint = checkpoint.resolve(strict=True)
    source, policy = pinned_model(manifest, checkpoint, slot)
    if (source["policy_signature"] != store.config["policy_signature"] or
            source["abi_version"] != store.config["policy_abi_version"]):
        raise ValueError("native registration disagrees with the registry policy contract")
    policy.pop("path")
    policy.pop("slot")
    # Training allocation is not actor identity. Promotion chooses explicit
    # inference placement, and native deployments can load on CPU by default.
    policy["device"] = "cpu"
    return store.register_model(
        checkpoint, training_run_id=run_id, training_step=step,
        game_build_version=build, parent_model_id=parent,
        training_config_path=trainer_config,
        source_checkpoint=file_sha256(checkpoint),
        metadata={"native_policy": policy},
    )


def physical_suite(path):
    path = path.resolve(strict=True)
    run = document(path)
    levels = run.get("levels")
    episodes = run.get("episodes_per_world", 1)
    if not isinstance(levels, list) or not levels or type(episodes) is not int or episodes < 1:
        raise ValueError("native promotion suite needs explicit physical episodes")
    dependencies = native_input_dependencies({"path": str(path)})
    # Store stable source content identities in the existing permanent
    # competency contract. Do not interpret legacy Unity CLI arguments
    # as native gameplay configuration.
    identity = {
        "source_sha256": file_sha256(path),
        "dependency_sha256": sorted(item["sha256"] for item in dependencies),
    }
    digest = hashlib.sha256(canonical_json(identity).encode("utf-8")).hexdigest()
    return path, len(levels) * episodes * 2, digest


def competency_contract(path):
    source = document(path)
    if source.get("schema_version") != 1 or not isinstance(source.get("cases"), list):
        raise ValueError("native competency suite must contain schema_version 1 and cases")
    paths = {}
    cases = []
    for case in source["cases"]:
        name = case["name"]
        run, matches, digest = physical_suite(path.parent / case["run_config"])
        if name in paths or matches != case["matches"]:
            raise ValueError("duplicate competency case or mismatched authored match count")
        paths[name] = run
        cases.append({
            **{key: case[key] for key in ("name", "opponent_model_id", "matches",
                                        "minimum", "metric", "critical")},
            "env_args": ["native-physical-suite-sha256:" + digest],
        })
    return {"schema_version": 1, "cases": sorted(cases, key=lambda row: row["name"])}, paths


def match_summary(receipt):
    counts = receipt["counts"]
    result = {key: counts.get(key, 0) for key in (
        "matches", "wins", "losses", "draws", "timeouts", "candidate_starting_tsv",
        "candidate_final_tsv", "candidate_shots", "candidate_hits", "candidate_damage",
    )}
    result.update({
        "score_rate": receipt["score_rate"],
        "win_rate": result["wins"] / result["matches"],
        "timeout_rate": result["timeouts"] / result["matches"],
        "candidate_inference_calls": receipt["candidate_inference_calls"],
        "candidate_inference_max_milliseconds": receipt["candidate_max_inference_batch_milliseconds"],
    })
    return result


def release(store, candidate_id, champion_source, historical_source, competency_source,
            promotion_config, binary, seed, workers, device):
    champion_id = store.current_compatible_champion_id()
    if not champion_id:
        raise ValueError("establish a trusted generation-zero native champion before automatic release")
    candidate, _, _ = native_model(store, candidate_id)
    if candidate["status"] not in ("candidate", "rejected"):
        raise ValueError("native release requires a registered candidate/rejected checkpoint")
    promotion = store.config["promotion"]
    contract, competency_paths = competency_contract(competency_source)
    if contract != store.permanent_competency_suite():
        raise ValueError("native competency suite differs from the permanent registry contract")
    historical = sorted(
        (model for model in store.list_models("historical")
         if all(model.get(key) == value for key, value in store.compatibility.to_dict().items())),
        key=lambda row: row["model_id"],
    )
    # Fail closed before expensive simulation if a compatible historical
    # policy does not yet have native actor metadata.
    for model in [*historical, store.get_model(champion_id)]:
        native_model(store, model["model_id"])
    for case in contract["cases"]:
        model, _, _ = native_model(store, case["opponent_model_id"])
        if model["status"] not in ("historical", "champion"):
            raise ValueError("native competency opponent must be an approved registered policy")
    source = champion_source.resolve(strict=True)
    historical_source = historical_source.resolve(strict=True)
    plan = {
        "champion": champion_id, "candidate": candidate_id,
        "historical": [model["model_id"] for model in historical],
        "competencies": contract,
        "champion_suite": physical_suite(source)[2],
        "historical_suite": physical_suite(historical_source)[2],
        "promotion_config_sha256": file_sha256(promotion_config),
        "binary_sha256": file_sha256(binary), "seed": seed,
        "workers": workers, "device": device,
    }
    batch_id = hashlib.sha256(canonical_json(plan).encode("utf-8")).hexdigest()
    output = store.evaluation_dir / "native" / batch_id
    output.mkdir(parents=True, exist_ok=True)
    with session_lock(output):
        immutable_json(output / "release-plan.json", plan)
        evidence = []
        candidate_latencies = []

        def compare(label, evaluated_id, opponent_id, suite, minimum, *, candidate_evidence=True):
            _, evaluated_manifest, evaluated_checkpoint = native_model(store, evaluated_id)
            _, opponent_manifest, opponent_checkpoint = native_model(store, opponent_id)
            directory = output / label
            receipt = gate(
                suite, evaluated_manifest, evaluated_checkpoint, 0,
                opponent_manifest, opponent_checkpoint, 0,
                promotion_config, directory, binary, seed, workers, device,
                minimum_matches=minimum,
            )
            evidence.append({
                "comparison": label, "evaluated_model_id": evaluated_id,
                "opponent_model_id": opponent_id,
                "receipt": str(directory / "incumbent-gate.json"),
                "receipt_sha256": file_sha256(directory / "incumbent-gate.json"),
            })
            if candidate_evidence:
                candidate_latencies.append(receipt)
            return receipt, match_summary(receipt)

        incumbent, comparison = compare(
            "champion", candidate_id, champion_id, source,
            promotion["min_matches_vs_champion"],
        )
        historical_results = []
        competency_results = []
        short_circuit = not incumbent["head_to_head_passed"]
        if not short_circuit:
            for index, opponent in enumerate(historical):
                opponent_id = opponent["model_id"]
                minimum = promotion["min_historical_matches_per_opponent"]
                _, current = compare(
                    f"historical-{index:05d}-candidate", candidate_id,
                    opponent_id, historical_source, minimum,
                )
                _, baseline = compare(
                    f"historical-{index:05d}-baseline", champion_id,
                    opponent_id, historical_source, minimum, candidate_evidence=False,
                )
                metadata = opponent.get("metadata", {})
                historical_results.append({
                    "opponent_model_id": opponent_id,
                    "matches": current["matches"],
                    "candidate_win_rate": current["win_rate"],
                    "candidate_score_rate": current["score_rate"],
                    "baseline_win_rate": baseline["win_rate"],
                    "baseline_score_rate": baseline["score_rate"],
                    "critical": bool(metadata.get("critical_regression") or
                                     "critical" in metadata.get("tags", [])),
                    "candidate_summary": current,
                    "champion_baseline_summary": baseline,
                })
            for index, case in enumerate(contract["cases"]):
                _, summary = compare(
                    f"competency-{index:05d}", candidate_id,
                    case["opponent_model_id"], competency_paths[case["name"]],
                    case["matches"],
                )
                score = {"score_rate": summary["score_rate"],
                         "win_rate": summary["win_rate"],
                         "non_timeout_rate": 1.0 - summary["timeout_rate"]}[case["metric"]]
                competency_results.append({**case, "score": score, "summary": summary})
        latency_passed = all(row["runtime_latency_passed"] for row in candidate_latencies)
        report = apply_behavior_sanity({
            "candidate_model_id": candidate_id, "champion_model_id": champion_id,
            "candidate_vs_champion": comparison,
            "historical": historical_results,
            "competencies": competency_results,
            "runtime_compatible": True,
            "runtime_checks_passed": latency_passed,
            "evaluator": {
                "kind": "native_balanced_physical_episode_evaluation",
                "authoritative_match_runner": True,
                "authoritative_telemetry_validated": True,
                "artifact_integrity_verified": True,
                "compatibility_metadata_verified": True,
                "native_evidence": evidence,
                "runtime_latency": {
                    "configured": True, "passed": latency_passed,
                    "candidate_inference_calls": sum(
                        row["candidate_inference_calls"] for row in candidate_latencies),
                    "max_inference_batch_milliseconds": promotion["max_inference_batch_milliseconds"],
                    "observed_candidate_max_milliseconds": max(
                        row["candidate_max_inference_batch_milliseconds"] for row in candidate_latencies),
                },
                **({"short_circuit": {"reason": "champion_score_below_threshold"}}
                   if short_circuit else {}),
            },
        })
        # The registry independently verifies the existing behavior,
        # champion, historical and permanent competency gates. Its
        # promotion transaction rejects changed champions or league state.
        recorded = store.record_evaluation(report)
        for row in historical_results:
            store.record_historical_matchup(
                current_model_id=candidate_id,
                opponent_model_id=row["opponent_model_id"],
                current_win_rate=row["candidate_score_rate"],
                previous_win_rate=row["baseline_score_rate"],
                match_count=row["matches"],
                tags=["authoritative_candidate_evaluation",
                      "evaluation:" + recorded["report_id"]],
            )
        if recorded["passed"]:
            model = store.promote(candidate_id, recorded["report_id"])
        elif candidate["status"] == "candidate":
            model = store.reject(candidate_id, recorded["report_id"])
        else:
            model = store.get_model(candidate_id)
        return {
            "kind": "native_continual_release_complete",
            "model": model, "evaluation": recorded,
        }


def publish_champion(store):
    """Publish only the approved registry model, using the native portable
    model package format. Legacy ONNX distribution pointers are untouched.
    """
    champion_id = store.current_compatible_champion_id()
    if champion_id is None:
        raise ValueError("no compatible native champion to deploy")
    champion, manifest, checkpoint = native_model(store, champion_id)
    evidence = _promotion_evidence(store, champion)
    identity = {
        "schema_version": 1, "kind": "native_continual_deployment",
        "model_id": champion_id, "checkpoint_sha256": champion["artifact_sha256"],
        "model_size_bytes": checkpoint.stat().st_size,
        "policy_signature": store.config["policy_signature"],
        "compatibility": store.compatibility.to_dict(),
        "training_run_id": champion["training_run_id"],
        "training_step": champion["training_step"],
        "game_build_version": champion["game_build_version"],
        "promotion_evidence": evidence,
    }
    digest = hashlib.sha256(canonical_json(identity).encode("utf-8")).hexdigest()
    deployment_id = "native-" + digest[:24]
    root = store.root / "deployment"
    packages = root / "packages"
    packages.mkdir(parents=True, exist_ok=True)
    package = packages / deployment_id
    if not package.exists():
        # Stage beside the destination, then publish the complete directory
        # atomically. An interrupted copy is never a current deployment.
        staging = Path(tempfile.mkdtemp(prefix=".native-staging-", dir=packages))
        try:
            export_model(manifest, staging / "policy")
            verified = verify_package(staging / "policy", 0)
            atomic_json(staging / "deployment.json", {
                "identity": identity, "identity_sha256": digest,
                "deployment_id": deployment_id,
                "policy_manifest_sha256": verified["manifest_sha256"],
            })
            sync_directory(staging)
            os.replace(staging, package)
            sync_directory(packages)
        finally:
            if staging.exists():
                shutil.rmtree(staging)
    record = document(package / "deployment.json")
    verified = verify_package(package / "policy", 0)
    if (record != {"identity": identity, "identity_sha256": digest,
                   "deployment_id": deployment_id,
                   "policy_manifest_sha256": verified["manifest_sha256"]} or
            file_sha256(Path(verified["learner_checkpoint"])) != identity["checkpoint_sha256"]):
        raise ValueError("native champion deployment is inconsistent with approved checkpoint")
    pointer = root / "current-native-deployment.json"
    value = {
        "schema_version": 1, "deployment_id": deployment_id,
        "model_id": champion_id,
        "deployment_sha256": file_sha256(package / "deployment.json"),
    }
    # Recheck under the registry's write transaction, so a concurrent
    # promotion cannot publish an obsolete champion as the current one.
    with store._connect() as db:
        db.execute("BEGIN IMMEDIATE")
        if store._state(db, STATE_CHAMPION) != champion_id:
            raise ValueError("champion changed while preparing native deployment")
        if not pointer.exists() or document(pointer) != value:
            atomic_json(pointer, value)
    return {
        **value, "package_dir": str(package),
        "policy_manifest": verified["manifest"],
        "checkpoint": verified["learner_checkpoint"],
        "pointer": str(pointer),
    }


def resolve_deployment(root):
    """Resolve a locally installed/copied deployment without a registry.
    The pointer names an immutable package; verify its portable weights.
    """
    root = Path(root).resolve(strict=True)
    pointer = document(root / "current-native-deployment.json")
    deployment_id = pointer.get("deployment_id")
    if (pointer.get("schema_version") != 1 or not isinstance(deployment_id, str) or
            len(deployment_id) != 31 or not deployment_id.startswith("native-") or
            any(c not in "0123456789abcdef" for c in deployment_id[7:])):
        raise ValueError("invalid native deployment pointer")
    package = root / "packages" / deployment_id
    metadata_path = package / "deployment.json"
    if file_sha256(metadata_path) != pointer.get("deployment_sha256"):
        raise ValueError("native deployment pointer does not match its package")
    record = document(metadata_path)
    identity = record["identity"]
    digest = hashlib.sha256(canonical_json(identity).encode("utf-8")).hexdigest()
    verified = verify_package(package / "policy", 0)
    if (record["identity_sha256"] != digest or
            deployment_id != "native-" + digest[:24] or
            record["deployment_id"] != deployment_id or
            identity["kind"] != "native_continual_deployment" or
            identity["model_id"] != pointer.get("model_id") or
            record["policy_manifest_sha256"] != verified["manifest_sha256"] or
            file_sha256(Path(verified["learner_checkpoint"])) != identity["checkpoint_sha256"]):
        raise ValueError("native deployment package identity differs from its pointer")
    return record, verified


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("registry", type=Path)
    parser.add_argument("--config", type=Path, default=Path(__file__).resolve().parents[2] /
                        "Training/continual_learning_config.json")
    commands = parser.add_subparsers(dest="command", required=True)
    registration = commands.add_parser("register")
    registration.add_argument("manifest", type=Path)
    registration.add_argument("checkpoint", type=Path)
    registration.add_argument("--learner-slot", type=int, required=True)
    registration.add_argument("--run-id", required=True)
    registration.add_argument("--training-step", type=int, required=True)
    registration.add_argument("--game-build-version", required=True)
    registration.add_argument("--parent-model-id")
    registration.add_argument("--trainer-config", type=Path)
    bootstrap = commands.add_parser("bootstrap")
    bootstrap.add_argument("model_id")
    bootstrap.add_argument("--reason", required=True)
    pin = commands.add_parser("pin-competencies")
    pin.add_argument("suite", type=Path)
    commands.add_parser("publish")
    evaluate = commands.add_parser("release")
    evaluate.add_argument("candidate_model_id")
    evaluate.add_argument("champion_suite", type=Path)
    evaluate.add_argument("historical_suite", type=Path)
    evaluate.add_argument("competency_suite", type=Path)
    evaluate.add_argument("--rollout-binary", type=Path, required=True)
    evaluate.add_argument("--policy-seed", type=int, default=0)
    evaluate.add_argument("--physics-workers", type=int, default=1)
    evaluate.add_argument("--inference-device", choices=("cpu", "cuda:0"), default="cpu")
    args = parser.parse_args()
    config_path = args.config.resolve(strict=True)
    store = ContinualLearningStore(args.registry, load_config(config_path))
    store.initialize()
    # Release and registry mutations initiated by this native entry point
    # serialize on the same controller lock across Windows and Linux.
    lock = store.root / "native-release"
    lock.mkdir(exist_ok=True)
    with session_lock(lock):
        if args.command == "register":
            result = register(store, args.manifest, args.checkpoint, args.learner_slot,
                              args.run_id, args.training_step, args.game_build_version,
                              args.parent_model_id, args.trainer_config)
        elif args.command == "bootstrap":
            native_model(store, args.model_id)
            result = bootstrap_champion(store, args.model_id, reason=args.reason)
            result = {"model": result, "deployment": publish_champion(store)}
        elif args.command == "pin-competencies":
            contract, _ = competency_contract(args.suite.resolve(strict=True))
            for case in contract["cases"]:
                native_model(store, case["opponent_model_id"])
            result = store.pin_competency_suite(contract)
        elif args.command == "publish":
            result = publish_champion(store)
        else:
            if not 0 <= args.policy_seed < (1 << 63) or not 1 <= args.physics_workers <= 128:
                parser.error("invalid native evaluation seed or physics allocation")
            result = release(store, args.candidate_model_id, args.champion_suite,
                             args.historical_suite, args.competency_suite, config_path,
                             args.rollout_binary.resolve(strict=True), args.policy_seed,
                             args.physics_workers, args.inference_device)
            result["deployment"] = publish_champion(store)
    print(json.dumps(result, sort_keys=True), flush=True)


if __name__ == "__main__":
    main()
