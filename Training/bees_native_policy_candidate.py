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
from pathlib import Path
import re
import sys
from typing import Any

SHA = re.compile(r"^[0-9a-f]{64}$")
UUID = re.compile(r"^[0-9a-f]{8}-(?:[0-9a-f]{4}-){3}[0-9a-f]{12}$")
MAX_MODEL_SIZE = 4 * 1024 ** 3
MAX_MANIFEST_SIZE = 8192
MAX_EVALUATION_SIZE = 32768
PROFILES = {"scripted-maps-v1": 4, "scripted-coverage-v2": 48}
MANIFEST_FIELDS = {
    "schema_version", "kind", "deployment_status", "job_id",
    "lineage_root_id", "continuation_of_job_id", "model_file",
    "model_sha256", "model_size_bytes", "optimizer_checkpoint_sha256",
    "unity_worker_sha256", "trainer_sha256", "runtime_version",
    "seed", "hyperparameters", "environment_args", "evaluation",
}


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
    if set(manifest) != MANIFEST_FIELDS | extra or manifest["schema_version"] != 1 or (
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
    evaluation = _check_evaluation(root, manifest)
    if any(item.name not in {"manifest.json", "model.onnx", "evaluation-summary.json"}
           or item.is_symlink() or not item.is_file() for item in root.iterdir()):
        raise ValueError("Bees policy package contains an unexpected file.")
    return {
        "status": "verified",
        "promotion_authorized": False,
        "job_id": job_id,
        "lineage_root_id": lineage,
        "model_sha256": manifest["model_sha256"],
        "model_size_bytes": size,
        **{key: manifest[key] for key in extra},
        "evaluation": evaluation,
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path, help="Exported policy-candidate directory")
    args = parser.parse_args()
    try:
        print(json.dumps(verify_candidate(args.directory), sort_keys=True))
        return 0
    except (OSError, ValueError, TypeError, KeyError, json.JSONDecodeError) as error:
        print("Bees policy candidate verification failed: " + str(error),
              file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
