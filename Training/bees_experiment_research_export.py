"""Export a compact, auditable Bees experiment/study provenance bundle.

The bundle contains frozen methods, benchmark inputs, sampled manager telemetry,
aggregate reports, and per-match CSVs. Large Unity/ONNX binaries are NOT copied.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import re
import zipfile
from datetime import datetime, timezone
from pathlib import Path

REPORT_RE = re.compile(r"\d{8}T\d{6}-[0-9a-f]{32}\.json")
EPISODES_RE = re.compile(r"\d{8}T\d{6}-[0-9a-f]{32}\.episodes\.csv")
FILE_RE = re.compile(r"[0-9a-f]{64}")


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as input_file:
        for chunk in iter(lambda: input_file.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def report_names(item: dict) -> set[str]:
    names = set()
    if item.get("benchmark_report"):
        names.add(item["benchmark_report"])
    for entry in item.get("checkpoint_history", []) + item.get("stage_history", []):
        if entry and entry.get("evaluation"):
            names.update(entry["evaluation"].get("reports", {}).values())
    heldout = item.get("heldout_validation") or {}
    names.update((heldout.get("evaluation") or {}).get("reports", {}).values())
    return names


def create_bundle(db: Path, benchmarks: Path, destination: Path,
                  experiment_id: str | None, study_id: str | None) -> dict:
    state = json.loads(db.read_text(encoding="utf-8"))
    items = [
        item for item in state.get("items", [])
        if (study_id is not None and item.get("study_id") == study_id) or
        (experiment_id is not None and item.get("id") == experiment_id)
    ]
    if not items:
        raise ValueError("No experiment or study matches the selected identifier.")
    if not any(item.get("run_id") for item in items):
        raise ValueError("The selected experiments have no recorded training run yet.")
    manifest = {
        "schema_version": 1,
        "generated_utc": datetime.now(timezone.utc).isoformat(),
        "experiment_ids": [item["id"] for item in items],
        "run_ids": [item.get("run_id") for item in items],
        "study_id": study_id,
        "files": {},
        "scope": {
            "included": "frozen run definitions, sampled training metrics, trainer YAML, "
                        "available benchmark recipes/code, aggregate reports, individual match CSVs",
            "excluded": "ONNX weights, optimizer checkpoints, Unity player binaries, "
                        "raw trajectories, arbitrary machine logs",
            "warning": "This bundle records exact hashes and methods but is NOT executable "
                       "without the referenced Unity build, model weights and runtime. "
                       "Training RNGs and worker scheduling are not fully deterministic.",
        },
    }
    destination.parent.mkdir(parents=True, exist_ok=True)
    written = set()
    with zipfile.ZipFile(destination, "w", compression=zipfile.ZIP_DEFLATED,
                         compresslevel=6, allowZip64=True) as archive:
        def add(name: str, source: Path, expected: str | None = None) -> None:
            if name in written:
                if expected and manifest["files"][name] != expected:
                    raise ValueError("Archive name reused with different contents.")
                return
            if not source.is_file():
                raise FileNotFoundError("Required research evidence unavailable: " + str(source))
            digest = sha256(source)
            if expected and digest != expected:
                raise ValueError("Research evidence changed after freezing: " + str(source))
            archive.write(source, name)
            manifest["files"][name] = digest
            written.add(name)

        archive.writestr("experiments.json", json.dumps(items, indent=2, sort_keys=True) + "\n")
        # This JSON is the one immutable snapshot of each selected item, including
        # its bounded time-series samples. Do not copy raw training histories.
        for item in items:
            config = item.get("trainer_config") or {}
            if config.get("path") and config.get("sha256"):
                digest = str(config["sha256"])
                if not FILE_RE.fullmatch(digest):
                    raise ValueError("Invalid frozen trainer configuration digest.")
                add("configs/" + digest + ".yaml", Path(config["path"]), digest)

            for plan_name in ("benchmark", "stage_benchmarks", "heldout_benchmarks"):
                plan = item.get(plan_name) or {}
                for label, value in (plan.get("files") or {}).items():
                    # Embed textual benchmark methods, not large models or
                    # executables. Their hashes remain in experiments.json.
                    if not (label.startswith("recipe") or label in
                            {"scripted", "evaluator", "runner"}):
                        continue
                    digest = (plan.get("hashes") or {}).get(label)
                    if not digest or not FILE_RE.fullmatch(digest):
                        raise ValueError("Missing frozen benchmark source hash.")
                    suffix = Path(value).suffix.lower()
                    if suffix not in {".json", ".py"}:
                        raise ValueError("Unexpected benchmark source file format.")
                    add("benchmark-inputs/" + digest + suffix, Path(value), digest)

            for filename in sorted(report_names(item)):
                if not isinstance(filename, str) or not REPORT_RE.fullmatch(filename):
                    raise ValueError("Unrecognized benchmark report filename.")
                source = benchmarks / filename
                report = json.loads(source.read_text(encoding="utf-8"))
                if report.get("run_id") != item.get("run_id"):
                    raise ValueError("Report belongs to a different training run.")
                if report.get("experiment", {}).get("id") not in (None, item["id"]):
                    raise ValueError("Report belongs to a different experiment.")
                add("reports/" + filename, source)
                detail = report.get("episode_detail")
                if detail:
                    detail_file = detail.get("file")
                    digest = detail.get("sha256")
                    if not isinstance(detail_file, str) or not EPISODES_RE.fullmatch(detail_file):
                        raise ValueError("Unrecognized per-match evidence filename.")
                    if not isinstance(digest, str) or not FILE_RE.fullmatch(digest):
                        raise ValueError("Per-match evidence checksum missing.")
                    add("episodes/" + detail_file, benchmarks / detail_file, digest)
        archive.writestr("manifest.json", json.dumps(manifest, indent=2, sort_keys=True) + "\n")
    return {"experiments": len(items), "files": len(manifest["files"]),
            "bytes": destination.stat().st_size}


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--db", type=Path, required=True)
    parser.add_argument("--benchmarks", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    choice = parser.add_mutually_exclusive_group(required=True)
    choice.add_argument("--experiment-id")
    choice.add_argument("--study-id")
    args = parser.parse_args()
    print(json.dumps(create_bundle(args.db, args.benchmarks, args.output,
                                  args.experiment_id, args.study_id)))


if __name__ == "__main__":
    main()
