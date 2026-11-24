#!/usr/bin/env python3
"""Verify a portable Bees native Hub ONNX policy candidate before import.

Integrity validation does not authorize promotion or establish publisher
authenticity. Unity deployment still requires its separate policy-ABI checks.
No Unity Editor, GPU, learner, or third-party Python package is required.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import sys
import tempfile
from typing import Any
import zipfile

SHA = re.compile(r"^[0-9a-f]{64}$")
UUID = re.compile(r"^[0-9a-f]{8}-(?:[0-9a-f]{4}-){3}[0-9a-f]{12}$")
MAX_MODEL_SIZE = 4 * 1024 ** 3
MAX_MANIFEST_SIZE = 32768
MAX_TRAINER_SIZE = 1024 * 1024
MAX_EVALUATION_SIZE = 32768
PROFILES = {"scripted-maps-v1": 4, "scripted-coverage-v2": 48}
MANIFEST_FIELDS = {
    "schema_version", "kind", "deployment_status", "job_id",
    "lineage_root_id", "continuation_of_job_id", "model_file",
    "model_sha256", "model_size_bytes", "optimizer_checkpoint_sha256",
    "unity_worker_sha256", "trainer_sha256", "runtime_version",
    "seed", "hyperparameters", "environment_args", "evaluation",
}

POLICY_FIELDS = {"behavior_name", "policy_abi_version", "observation_schema_version",
                 "action_schema_version", "reward_schema_version", "scenario_schema_version"}
V2_FIELDS = {"training_step", "policy_compatibility", "policy_signature",
             "training_config_file", "training_config_sha256"}


def policy_contract(config: dict[str, Any]) -> tuple[dict[str, Any], str]:
    contract = {key: config.get(key) for key in POLICY_FIELDS}
    signature = config.get("policy_signature")
    if (contract["behavior_name"] != "BeesRL1v1" or
        any(type(value) is not int or value < 1 for key, value in contract.items()
            if key != "behavior_name") or
        not isinstance(signature, str) or len(signature) > 12000 or
        not signature.startswith(f"bees-rl-v{contract['policy_abi_version']}|")):
        raise ValueError("Missing or incompatible Bees policy ABI contract.")
    return contract, signature


def _required_file(path: Path, maximum: int) -> Path:
    if path.is_symlink() or not path.is_file() or (
        not 0 < path.stat().st_size <= maximum
    ):
        raise ValueError("Missing, redirected or oversized candidate file: " + str(path))
    return path


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as reader:
        for chunk in iter(lambda: reader.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _json_file(path: Path, maximum: int) -> dict[str, Any]:
    obj = json.loads(_required_file(path, maximum).read_text(encoding="utf-8"))
    if not isinstance(obj, dict):
        raise ValueError("Candidate metadata must be a JSON object: " + str(path))
    return obj


def _digest(value: Any, label: str) -> str:
    if not isinstance(value, str) or SHA.fullmatch(value) is None:
        raise ValueError("Invalid candidate " + label + " digest.")
    return value


def _uuid(value: Any, label: str) -> str:
    if not isinstance(value, str) or UUID.fullmatch(value) is None:
        raise ValueError("Invalid candidate " + label + " UUID.")
    return value


def _check_evaluation(root: Path, manifest: dict[str, Any]) -> dict[str, Any] | None:
    evidence = manifest["evaluation"]
    if evidence is None:
        if (root / "evaluation-summary.json").exists() or (
            root / "evaluation-summary.json"
        ).is_symlink():
            raise ValueError("Unscored candidate unexpectedly contains evaluation evidence.")
        return None
    if not isinstance(evidence, dict) or set(evidence) != {
        "file", "sha256", "profile", "case_count"
    } or evidence["file"] != "evaluation-summary.json":
        raise ValueError("Invalid candidate evaluation reference.")
    profile = evidence["profile"]
    if profile not in PROFILES or type(evidence["case_count"]) is not int or (
        evidence["case_count"] != PROFILES[profile]
    ):
        raise ValueError("Unknown or incomplete candidate evaluation suite.")
    filename = _required_file(root / "evaluation-summary.json", MAX_EVALUATION_SIZE)
    if _sha256(filename) != _digest(evidence["sha256"], "evaluation"):
        raise ValueError("Candidate evaluation evidence failed SHA-256 verification.")
    summary = _json_file(filename, MAX_EVALUATION_SIZE)
    cases = summary.get("cases")
    if summary.get("schema_version") != 1 or (
        summary.get("job_id") != manifest["job_id"] or
        summary.get("profile") != profile or
        summary.get("model_sha256") != manifest["model_sha256"] or
        (manifest["schema_version"] == 2 and summary.get("steps_completed") != manifest["training_step"]) or
        summary.get("case_count") != PROFILES[profile] or
        not isinstance(cases, list) or len(cases) != PROFILES[profile]
    ):
        raise ValueError("Candidate evaluation summary does not match the exported model.")
    totals = {key: 0 for key in ("matches", "wins", "draws", "losses", "timeouts")}
    unique = set()
    for item in cases:
        if not isinstance(item, dict) or (
            not isinstance(item.get("case"), str) or
            item["case"] in unique or
            item.get("opponent") not in ("pursuit", "strafe")
        ):
            raise ValueError("Candidate evaluation contains invalid or duplicate cases.")
        unique.add(item["case"])
        for key in totals:
            value = item.get(key)
            if type(value) is not int or value < 0:
                raise ValueError("Candidate evaluation has invalid " + key + " totals.")
            totals[key] += value
        if item["matches"] < 1 or item["wins"] + item["draws"] + item["losses"] != item["matches"] or (
            item["timeouts"] > item["draws"]
        ):
            raise ValueError("Candidate evaluation case outcome counts do not balance.")
    if profile == "scripted-coverage-v2":
        expected = {
            f"p{players}_map{size}_{opponent}"
            for players in range(1, 7)
            for size in (32, 64, 128, 256)
            for opponent in ("pursuit", "strafe")
        }
        if unique != expected or any(
            item["opponent"] != item["case"].rsplit("_", 1)[-1] for item in cases
        ):
            raise ValueError("Candidate evaluation does not cover the complete 48-case suite.")
    else:
        expected = {
            "small_1v1_pursuit", "medium_2v2_strafe",
            "large_4v4_pursuit", "wide_6v6_strafe",
        }
        if unique != expected or any(
            item["opponent"] != item["case"].rsplit("_", 1)[-1] for item in cases
        ):
            raise ValueError("Candidate evaluation does not cover the four-case suite.")
    if any(summary.get("total_" + key) != value for key, value in totals.items()):
        raise ValueError("Candidate evaluation aggregate outcomes do not balance.")
    return {"profile": profile, "cases": len(cases), "matches": totals["matches"]}


def verify_candidate(directory: Path) -> dict[str, Any]:
    root = directory.absolute()
    if root.is_symlink() or not root.is_dir():
        raise ValueError("Candidate path must be a real directory.")
    manifest = _json_file(root / "manifest.json", MAX_MANIFEST_SIZE)
    extra = {
        key for key in ("unity_worker_payload_sha256", "wan_release_sha256")
        if key in manifest
    }
    if "wan_release_sha256" in extra and "unity_worker_payload_sha256" not in extra:
        raise ValueError("WAN policy provenance needs both local Unity content and shared release hashes.")
    version = manifest.get("schema_version")
    if version == 2:
        extra |= V2_FIELDS
    if set(manifest) != MANIFEST_FIELDS | extra or version not in (1, 2) or (
        manifest["kind"] != "bees-unity-onnx-candidate" or
        manifest["deployment_status"] != "not-promoted" or
        manifest["model_file"] != "model.onnx"
    ):
        raise ValueError("Unknown or non-candidate Bees policy package.")
    job_id = _uuid(manifest["job_id"], "job")
    lineage = _uuid(manifest["lineage_root_id"], "lineage")
    parent = manifest["continuation_of_job_id"]
    if parent is not None:
        _uuid(parent, "parent")
        if parent == job_id or lineage == job_id:
            raise ValueError("Invalid Bees candidate checkpoint ancestry.")
    elif lineage != job_id:
        raise ValueError("Independent Bees candidate has a foreign lineage root.")
    for label in ("model_sha256", "optimizer_checkpoint_sha256",
                  "unity_worker_sha256", "trainer_sha256", "runtime_version"):
        _digest(manifest[label], label)
    if "unity_worker_payload_sha256" in extra:
        _digest(manifest["unity_worker_payload_sha256"], "complete Unity player")
    if "wan_release_sha256" in extra:
        _digest(manifest["wan_release_sha256"], "shared cross-platform Unity release")
    size = manifest["model_size_bytes"]
    if type(size) is not int or not 0 < size <= MAX_MODEL_SIZE:
        raise ValueError("Invalid Bees candidate ONNX size.")
    source = _required_file(root / "model.onnx", MAX_MODEL_SIZE)
    if source.stat().st_size != size or _sha256(source) != manifest["model_sha256"]:
        raise ValueError("Bees candidate ONNX bytes or length changed.")
    if type(manifest["seed"]) is not int or not 0 <= manifest["seed"] <= 2147483647 or (
        not isinstance(manifest["hyperparameters"], dict) or
        not isinstance(manifest["environment_args"], list) or
        any(not isinstance(value, str) or len(value) > 256
            for value in manifest["environment_args"])
    ):
        raise ValueError("Invalid frozen Bees candidate training identity.")
    allowed = {"manifest.json", "model.onnx", "evaluation-summary.json"}
    if version == 2:
        if "unity_worker_payload_sha256" not in extra:
            raise ValueError("Importable Bees candidates require complete Unity content identity.")
        contract = manifest["policy_compatibility"]
        if not isinstance(contract, dict) or set(contract) != POLICY_FIELDS:
            raise ValueError("Invalid Bees candidate compatibility contract.")
        policy_contract({**contract, "policy_signature": manifest["policy_signature"]})
        if type(manifest["training_step"]) is not int or manifest["training_step"] < 0:
            raise ValueError("Invalid exported training step.")
        if manifest["training_config_file"] != "trainer.yaml" or (
            _sha256(_required_file(root / "trainer.yaml", MAX_TRAINER_SIZE)) !=
            _digest(manifest["training_config_sha256"], "effective trainer")
        ):
            raise ValueError("Exported effective trainer configuration changed.")
        allowed.add("trainer.yaml")
    evaluation = _check_evaluation(root, manifest)
    if any(item.name not in allowed
           or item.is_symlink() or not item.is_file() for item in root.iterdir()):
        raise ValueError("Bees policy package contains an unexpected file.")
    return {
        "status": "verified",
        "promotion_authorized": False,
        "schema_version": version,
        "job_id": job_id,
        "lineage_root_id": lineage,
        "model_sha256": manifest["model_sha256"],
        "model_size_bytes": size,
        **{key: manifest[key] for key in extra},
        "evaluation": evaluation,
    }

def reconstruct_hub_candidate(parts: list[Path], job_id: str,
                              destination: Path) -> dict[str, Any]:
    """Reassemble only the selected job's policy package from Hub ZIP parts.

    Never extract archive paths. Each chunk is verified against the Hub part
    manifest, then the reconstructed directory passes the policy verifier.
    """
    _uuid(job_id, "export job")
    destination = destination.absolute()
    if destination.exists() or destination.is_symlink():
        raise ValueError("Candidate reconstruction destination already exists.")
    if not destination.parent.is_dir():
        raise ValueError("Candidate reconstruction parent directory does not exist.")
    prefixes = [
        f"jobs/{job_id}/artifacts/policy-candidate/",
        f"jobs/{job_id}/external/mounted-artifacts/policy-candidate/",
        f"jobs/{job_id}/external/agent-artifacts/policy-candidate/",
    ]
    limits = {
        "manifest.json": MAX_MANIFEST_SIZE,
        "model.onnx": MAX_MODEL_SIZE,
        "trainer.yaml": MAX_TRAINER_SIZE,
        "evaluation-summary.json": MAX_EVALUATION_SIZE,
    }
    chunk_bytes = 24_000_000  # Experiment Hub export format
    groups: dict[str, dict[str, dict[int, tuple]]] = {}
    seen_parts = set()
    for part in parts:
        part = _required_file(part.absolute(), 500_000_000)
        resolved = part.resolve(strict=True)
        if resolved in seen_parts:
            raise ValueError("Duplicate Hub export part.")
        seen_parts.add(resolved)
        with zipfile.ZipFile(part) as bundle:
            infos = bundle.infolist()
            by_name = {item.filename: item for item in infos}
            if len(infos) != len(by_name):
                raise ValueError("Duplicate ZIP member in Hub export.")
            meta = by_name.get("part-manifest.json")
            if meta is None or not 0 < meta.file_size <= 64 * 1024**2:
                raise ValueError("Missing or oversized Hub part manifest.")
            data = json.loads(bundle.read(meta))
            if (not isinstance(data, dict) or data.get("project_id") != "bees_native" or
                    not isinstance(data.get("entries"), list)):
                raise ValueError("Not a native Bees Hub export part.")
            for item in data["entries"]:
                if not isinstance(item, dict) or not isinstance(item.get("source"), str):
                    raise ValueError("Malformed Hub export entry.")
                source = item["source"]
                prefix = next((value for value in prefixes if source.startswith(value)), None)
                if prefix is None:
                    continue
                name = source[len(prefix):]
                if name not in limits:
                    raise ValueError("Unexpected Bees candidate export member.")
                size = item.get("original_file_bytes")
                index = item.get("segment")
                length = item.get("uncompressed_bytes")
                if (type(size) is not int or not 0 < size <= limits[name] or
                        type(index) is not int or not 1 <= index <= (size + chunk_bytes - 1)//chunk_bytes or
                        type(length) is not int or
                        length != min(chunk_bytes, size - (index - 1)*chunk_bytes)):
                    raise ValueError("Invalid Bees candidate export segment dimensions.")
                target = source if size <= chunk_bytes else f"{source}.part{index:05d}"
                entry = by_name.get(target)
                if item.get("name") != target or entry is None or entry.file_size != length:
                    raise ValueError("Missing or mismatched Bees candidate export segment.")
                segments = groups.setdefault(prefix, {}).setdefault(name, {})
                if index in segments:
                    raise ValueError("Duplicate Bees candidate export segment.")
                segments[index] = (part, target, size, length, _digest(item.get("sha256"), "export segment"))

    # Prefer the original local/mounted package to the asynchronous mirror.
    # A candidate is reconstructed from one source only, never from mixed
    # snapshots. Missing members will be diagnosed by the package verifier.
    prefix = next((value for value in prefixes if value in groups), None)
    if prefix is None:
        raise ValueError("Selected job has no policy candidate in these export parts.")
    with tempfile.TemporaryDirectory(prefix=".bees-candidate-", dir=destination.parent) as temp:
        staged = Path(temp) / "candidate"
        staged.mkdir(mode=0o700)
        for name, segments in groups[prefix].items():
            first = segments.get(1)
            if first is None:
                raise ValueError("Missing first segment of " + name)
            size = first[2]
            count = (size + chunk_bytes - 1)//chunk_bytes
            if len(segments) != count:
                raise ValueError("Missing export segments of " + name)
            with (staged / name).open("xb") as writer:
                os.chmod(staged / name, 0o600)
                for index in range(1, count + 1):
                    part, target, declared_size, length, digest = segments[index]
                    if declared_size != size:
                        raise ValueError("Inconsistent export length for " + name)
                    with zipfile.ZipFile(part) as bundle:
                        content = bundle.read(target)
                    if len(content) != length or hashlib.sha256(content).hexdigest() != digest:
                        raise ValueError("Export segment integrity failure for " + name)
                    writer.write(content)
                writer.flush()
                os.fsync(writer.fileno())
        result = verify_candidate(staged)
        if result["job_id"] != job_id:
            raise ValueError("Reconstructed candidate belongs to another job.")
        if destination.exists() or destination.is_symlink():
            raise ValueError("Candidate reconstruction destination appeared during export.")
        staged.rename(destination)
    return result


def import_candidate(directory: Path, store_root: Path, worker_manifest: Path,
                     expected_model_sha256: str, config_file: Path | None = None) -> dict:
    """Register only an explicitly selected, build-pinned candidate.

    A scripted Hub benchmark is useful provenance, not champion-comparison
    evidence. The existing continual evaluation/promotion gate remains the
    only authority that can replace the current champion.
    """
    from types import SimpleNamespace
    import yaml
    from bees_release_runtime import verify_runtime
    from bees_continual_learning import ContinualLearningStore, load_config
    from bees_continual_evaluate import OnnxPolicy

    root = directory.absolute()
    verified = verify_candidate(root)
    if verified["schema_version"] != 2 or verified["model_sha256"] != _digest(
        expected_model_sha256, "operator-pinned model"
    ):
        raise ValueError("Import requires a v2 candidate and the exact model digest from its Hub job.")
    manifest = _json_file(root / "manifest.json", MAX_MANIFEST_SIZE)
    if manifest["model_sha256"] != expected_model_sha256:
        raise ValueError("Candidate manifest changed during verification.")
    worker_file = worker_manifest.absolute()
    worker = _json_file(worker_file, MAX_MANIFEST_SIZE)
    release_sha = _sha256(worker_file)
    if (worker.get("schema_version") != 1 or worker.get("kind") != "offline-unity-workers" or
        worker.get("deployment_status") != "unpublished" or
        not isinstance(worker.get("artifacts"), list) or
        not any(isinstance(item, dict) and item.get("platform") == "LinuxPlayer" and
                item.get("player_tree_sha256") == manifest["unity_worker_payload_sha256"]
                for item in worker["artifacts"]) or
        ("wan_release_sha256" in manifest and manifest["wan_release_sha256"] != release_sha)):
        raise ValueError("Candidate does not belong to this locally approved Unity worker build.")
    runtime = worker.get("runtime")
    if (not isinstance(runtime, dict) or runtime.get("archive") != "bees-runtime.zip" or
        runtime.get("runtime_version") != manifest["runtime_version"]):
        raise ValueError("Candidate trainer runtime differs from the selected worker release.")
    archive = _required_file(worker_file.parent / "bees-runtime.zip", 512 * 1024**2)
    if archive.stat().st_size != runtime.get("archive_bytes"):
        raise ValueError("Worker runtime archive length changed.")
    result = verify_runtime(archive,
        expected_sha256=_digest(runtime.get("archive_sha256"), "worker runtime archive"),
        expected_version=manifest["runtime_version"],
        expected_build_id=worker["build_id"])
    if result["source_commit"] != worker.get("source_commit"):
        raise ValueError("Worker runtime and Unity source commits differ.")
    with zipfile.ZipFile(archive) as bundle:
        config = json.loads(bundle.read("continual_learning_config.json"))
        base_trainer = bundle.read("rl_1v1_config.yaml")
    if hashlib.sha256(base_trainer).hexdigest() != manifest["trainer_sha256"]:
        raise ValueError("Candidate base trainer does not match the selected build.")
    compatibility, signature = policy_contract(config)
    if (manifest["policy_compatibility"] != compatibility or
        manifest["policy_signature"] != signature):
        raise ValueError("Candidate policy ABI differs from its frozen training release.")
    receiving = load_config(config_file)
    if policy_contract(receiving) != (compatibility, signature):
        raise ValueError("Candidate cannot be imported into a different policy ABI generation.")

    # The exported trainer may change optimization settings, but not the
    # network architecture or behavior identity used by the policy ABI.
    actual = yaml.safe_load((root / "trainer.yaml").read_text(encoding="utf-8"))
    original = yaml.safe_load(base_trainer)
    a, b = actual["behaviors"], original["behaviors"]
    if (set(a) != {"BeesRL1v1"} or set(b) != {"BeesRL1v1"} or
        a["BeesRL1v1"]["trainer_type"] != b["BeesRL1v1"]["trainer_type"] or
        a["BeesRL1v1"]["network_settings"] != b["BeesRL1v1"]["network_settings"]):
        raise ValueError("Candidate effective trainer changed the frozen policy architecture.")
    ingestion = config["ingestion"]
    branches = tuple(ingestion["telemetry_discrete_branch_sizes"])
    policy = OnnxPolicy(root / "model.onnx")
    policy.validate_behavior_spec(SimpleNamespace(
        observation_specs=[SimpleNamespace(shape=(ingestion["telemetry_observation_size"],))],
        action_spec=SimpleNamespace(continuous_size=ingestion["telemetry_continuous_action_count"],
                                    discrete_branches=branches, discrete_size=len(branches))))
    store = ContinualLearningStore(store_root, receiving)
    store._require_initialized()
    # Keep foreign Hub ancestry in metadata; do not invent a legacy parent
    # model or register the scripted result as authoritative promotion evidence.
    registered = store.register_model(root / "model.onnx",
        training_run_id="hub-" + manifest["job_id"],
        training_step=manifest["training_step"],
        game_build_version=worker["build_id"],
        training_config_path=root / "trainer.yaml",
        source_checkpoint="sha256:" + manifest["optimizer_checkpoint_sha256"],
        status="candidate", expected_artifact_sha256=expected_model_sha256,
        metadata={"registration_source": "experiment_hub_bees_native",
                  "hub_candidate_manifest_sha256": _sha256(root / "manifest.json"),
                  "hub_lineage_root_id": manifest["lineage_root_id"],
                  "hub_continuation_of_job_id": manifest["continuation_of_job_id"],
                  "hub_worker_release_sha256": release_sha,
                  "hub_runtime_version": manifest["runtime_version"],
                  "hub_evaluation": verified["evaluation"]})
    return {"status": "registered", "model_id": registered["model_id"],
            "registry_status": registered["status"],
            "model_sha256": registered["artifact_sha256"],
            "promotion_authorized": False}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path, help="Policy-candidate directory (new destination with --hub-export-part).")
    parser.add_argument("--hub-export-part", type=Path, action="append", default=[],
                        help="Hub job-export ZIP part; repeat for every downloaded part.")
    parser.add_argument("--job-id", help="Selected Bees Hub job UUID when reconstructing an export.")
    parser.add_argument("--import-root", type=Path, help="Explicitly register as a candidate in an initialized continual-learning store.")
    parser.add_argument("--worker-manifest", type=Path, help="Locally trusted original worker manifest (with its bees-runtime.zip).")
    parser.add_argument("--expected-model-sha256", help="Exact final checkpoint digest observed in Experiment Hub.")
    parser.add_argument("--config", type=Path, help="Receiving continual-learning store configuration.")
    args = parser.parse_args()
    try:
        if bool(args.hub_export_part) != bool(args.job_id):
            parser.error("Reconstruction requires --hub-export-part and --job-id.")
        if args.import_root and (not args.worker_manifest or not args.expected_model_sha256):
            parser.error("Import requires --worker-manifest and --expected-model-sha256.")
        if not args.import_root and (args.worker_manifest or args.expected_model_sha256 or args.config):
            parser.error("Import options require --import-root.")
        reconstructed = (reconstruct_hub_candidate(args.hub_export_part, args.job_id, args.directory)
                         if args.hub_export_part else None)
        if args.import_root:
            result = import_candidate(args.directory, args.import_root, args.worker_manifest,
                                      args.expected_model_sha256, args.config)
        else:
            result = reconstructed if reconstructed is not None else verify_candidate(args.directory)
        print(json.dumps(result, sort_keys=True))
        return 0
    except (OSError, ValueError, TypeError, KeyError, RuntimeError, ImportError, zipfile.BadZipFile) as error:
        print("Bees policy candidate verification failed: " + str(error),
              file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
