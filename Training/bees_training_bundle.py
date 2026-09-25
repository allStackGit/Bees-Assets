from __future__ import annotations

import argparse
import hashlib
import json
import math
import os
import re
import shutil
import tempfile
import zipfile
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Iterable, Optional

RUN_ID_RE = re.compile(r"^[A-Za-z0-9._-]+$")
TEXT_LOG_SUFFIXES = {".log", ".txt"}
MAX_METADATA_BYTES = 16 * 1024 * 1024
MAX_STEP_SCAN_BYTES = 4 * 1024 * 1024
MIN_TEXT_LOG_TAIL_BYTES = 128 * 1024
MODEL_LAG_WARNING_STEPS = 5000
TRAINER_LOG_STALE_SECONDS = 30.0
STEP_RE = re.compile(r"\bStep\s*[:=]\s*(\d+)", re.IGNORECASE)
MODEL_STEP_RE = re.compile(r"-(\d+)(?:-[^.]+)?\.onnx$", re.IGNORECASE)


def _json(path: Path) -> Optional[dict[str, Any]]:
    try:
        value = json.loads(path.read_text(encoding="utf-8-sig"))
    except (OSError, UnicodeDecodeError, json.JSONDecodeError):
        return None
    return value if isinstance(value, dict) else None


def _run_from_status(status_json: Optional[Path]) -> str:
    if not status_json or not status_json.is_file():
        return ""
    value = _json(status_json)
    desired = value.get("desired") if value else None
    run_id = desired.get("run_id") if isinstance(desired, dict) else ""
    return str(run_id or "")


def resolve_run_id(
    bees_root: Path,
    explicit: str = "",
    status_json: Optional[Path] = None,
) -> str:
    candidates: list[str] = []
    if explicit:
        candidates.append(explicit)
    status_run = _run_from_status(status_json)
    if status_run:
        candidates.append(status_run)
    lifecycle = _json(bees_root / "Training" / "RunLifecycle" / "current.json")
    if lifecycle and lifecycle.get("run_id"):
        candidates.append(str(lifecycle["run_id"]))
    for root in (
        bees_root / "Training" / "trainer-results",
        bees_root / "Training" / "TrainerLogs",
    ):
        if root.is_dir():
            directories = [path for path in root.iterdir() if path.is_dir()]
            if directories:
                directories.sort(
                    key=lambda path: (path.stat().st_mtime_ns, path.name),
                    reverse=True,
                )
                candidates.append(directories[0].name)
    for run_id in candidates:
        if not RUN_ID_RE.fullmatch(run_id):
            raise ValueError(f"unsafe training run id: {run_id!r}")
        return run_id
    raise ValueError("could not determine a training run id")


def latest_file(root: Path, pattern: str) -> Optional[Path]:
    if not root.is_dir():
        return None
    files = [
        path
        for path in root.rglob(pattern)
        if path.is_file() and not path.is_symlink()
    ]
    if not files:
        return None
    return max(files, key=lambda path: (path.stat().st_mtime_ns, path.name))


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def tail_offset(
    size: int,
    percent: float,
    minimum_bytes: int = MIN_TEXT_LOG_TAIL_BYTES,
) -> int:
    if size <= 0 or percent >= 100.0:
        return 0
    wanted = max(
        1,
        int(math.ceil(size * percent / 100.0)),
        max(0, int(minimum_bytes)),
    )
    return max(0, size - wanted)


def _copy_range(source: Path, destination: Path, offset: int = 0) -> int:
    destination.parent.mkdir(parents=True, exist_ok=True)
    copied = 0
    with source.open("rb") as inp, destination.open("wb") as out:
        inp.seek(offset)
        while True:
            chunk = inp.read(1024 * 1024)
            if not chunk:
                break
            out.write(chunk)
            copied += len(chunk)
    return copied


def _safe_rel(path: Path, root: Path) -> str:
    return path.resolve().relative_to(root.resolve()).as_posix()


def _iter_log_files(root: Path) -> Iterable[Path]:
    if not root.is_dir():
        return ()
    return (
        path
        for path in sorted(root.rglob("*"))
        if path.is_file()
        and not path.is_symlink()
        and path.suffix.lower() in (TEXT_LOG_SUFFIXES | {".json"})
    )


def _copy_metadata(
    source: Path,
    destination: Path,
    warnings: list[str],
) -> Optional[dict[str, Any]]:
    try:
        size = source.stat().st_size
    except OSError as exc:
        warnings.append(f"could not stat metadata {source}: {exc}")
        return None
    if size > MAX_METADATA_BYTES:
        warnings.append(f"skipped oversized metadata file {source} ({size} bytes)")
        return None
    destination.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(source, destination)
    return {
        "source": str(source),
        "size_bytes": size,
        "sha256": sha256_file(source),
    }


def _collect_log_group(
    source_root: Path,
    staging_root: Path,
    archive_root: str,
    percent: float,
    combined,
    records: list[dict[str, Any]],
    warnings: list[str],
) -> None:
    if not source_root.is_dir():
        return
    for source in _iter_log_files(source_root):
        try:
            size = source.stat().st_size
        except OSError as exc:
            warnings.append(f"could not stat log {source}: {exc}")
            continue
        rel = _safe_rel(source, source_root)
        suffix = source.suffix.lower()
        if suffix in TEXT_LOG_SUFFIXES:
            offset = tail_offset(size, percent)
            destination = staging_root / archive_root / rel
            included = _copy_range(source, destination, offset)
            combined.write(
                (
                    f"\n===== {archive_root}/{rel} | original={size} bytes | "
                    f"offset={offset} | included={included} =====\n"
                ).encode("utf-8")
            )
            with source.open("rb") as inp:
                inp.seek(offset)
                shutil.copyfileobj(inp, combined, length=1024 * 1024)
            combined.write(b"\n")
            records.append(
                {
                    "archive_path": f"{archive_root}/{rel}",
                    "source": str(source),
                    "original_size_bytes": size,
                    "included_offset_bytes": offset,
                    "included_size_bytes": included,
                    "kind": "log-tail",
                }
            )
        elif suffix == ".json":
            destination = staging_root / archive_root / rel
            metadata = _copy_metadata(source, destination, warnings)
            if metadata:
                records.append(
                    {
                        "archive_path": f"{archive_root}/{rel}",
                        "kind": "log-metadata",
                        **metadata,
                    }
                )


def _tail_text(path: Path, maximum_bytes: int = MAX_STEP_SCAN_BYTES) -> str:
    try:
        size = path.stat().st_size
        with path.open("rb") as handle:
            if size > maximum_bytes:
                handle.seek(size - maximum_bytes)
            return handle.read().decode("utf-8", errors="replace")
    except OSError:
        return ""


def _max_step_in_json(value: Any) -> Optional[int]:
    latest: Optional[int] = None
    if isinstance(value, dict):
        for key, child in value.items():
            if (
                str(key).lower() in {"step", "steps", "global_step"}
                and isinstance(child, (int, float))
                and not isinstance(child, bool)
                and child >= 0
            ):
                candidate = int(child)
                latest = candidate if latest is None else max(latest, candidate)
            nested = _max_step_in_json(child)
            if nested is not None:
                latest = nested if latest is None else max(latest, nested)
    elif isinstance(value, list):
        for child in value:
            nested = _max_step_in_json(child)
            if nested is not None:
                latest = nested if latest is None else max(latest, nested)
    return latest


def _learner_step(
    status_text: Optional[Path],
    learner_log_root: Optional[Path],
    results_root: Optional[Path] = None,
) -> Optional[int]:
    if status_text and status_text.is_file():
        text = _tail_text(status_text)
        match = re.search(r"Learner logs:\s*Step=(\d+)", text, re.IGNORECASE)
        if match:
            return int(match.group(1))

    latest: Optional[int] = None
    if learner_log_root and learner_log_root.is_dir():
        for path in learner_log_root.rglob("*"):
            if not path.is_file() or path.suffix.lower() not in TEXT_LOG_SUFFIXES:
                continue
            for match in STEP_RE.finditer(_tail_text(path)):
                value = int(match.group(1))
                latest = value if latest is None else max(latest, value)

    if results_root and results_root.is_dir():
        for path in results_root.rglob("training_status.json"):
            value = _json(path)
            candidate = _max_step_in_json(value) if value is not None else None
            if candidate is not None:
                latest = candidate if latest is None else max(latest, candidate)
    return latest


def _model_step(path: Optional[Path], snapshot: Optional[dict[str, Any]]) -> Optional[int]:
    if snapshot and snapshot.get("status") == "succeeded":
        try:
            return int(snapshot.get("step"))
        except (TypeError, ValueError):
            pass
    if path:
        match = MODEL_STEP_RE.search(path.name)
        if match:
            return int(match.group(1))
    return None


def _trainer_log_freshness(root: Path, now_utc: datetime) -> dict[str, dict[str, Any]]:
    result: dict[str, dict[str, Any]] = {}
    if not root.is_dir():
        return result
    for trainer_root in sorted(path for path in root.iterdir() if path.is_dir()):
        files = [path for path in trainer_root.rglob("*") if path.is_file()]
        if not files:
            result[trainer_root.name] = {
                "file_count": 0,
                "newest_modified_utc": None,
                "age_seconds": None,
            }
            continue
        newest = max(files, key=lambda path: path.stat().st_mtime_ns)
        modified = datetime.fromtimestamp(newest.stat().st_mtime, tz=timezone.utc)
        result[trainer_root.name] = {
            "file_count": len(files),
            "newest_file": _safe_rel(newest, trainer_root),
            "newest_modified_utc": modified.isoformat(),
            "age_seconds": max(0.0, (now_utc - modified).total_seconds()),
        }
    return result


def _diagnose_status(
    status: Optional[dict[str, Any]],
    cluster: Optional[dict[str, Any]],
    resolved_run: str,
    log_freshness: dict[str, dict[str, Any]],
) -> tuple[list[dict[str, Any]], list[str]]:
    diagnostics: list[dict[str, Any]] = []
    warnings: list[str] = []
    if not status:
        return diagnostics, warnings

    desired = status.get("desired")
    desired = desired if isinstance(desired, dict) else {}
    desired_build = str(desired.get("canonical_build_id", "") or "")
    desired_revision = desired.get("revision")
    training_enabled = bool(desired.get("training_enabled", False))
    status_run = str(desired.get("run_id", "") or "")
    if status_run and status_run != resolved_run:
        warnings.append(
            f"live control run {status_run} differs from bundled run {resolved_run}"
        )

    trainers = status.get("trainers")
    trainer_records = trainers if isinstance(trainers, list) else []
    present_ids: set[str] = set()
    for raw in trainer_records:
        if not isinstance(raw, dict):
            continue
        trainer_id = str(raw.get("trainer_id", "") or "")
        if not trainer_id:
            continue
        present_ids.add(trainer_id)
        issues: list[str] = []
        if bool(raw.get("stale", False)):
            issues.append(f"stale ({raw.get('age_seconds', '?')}s since heartbeat)")
        error = str(raw.get("last_error", "") or "").strip()
        if error:
            issues.append(f"last_error={error}")
        build_id = str(raw.get("build_id", "") or "")
        if desired_build and build_id and build_id != desired_build:
            issues.append(f"build mismatch {build_id} != {desired_build}")
        revision = raw.get("applied_revision")
        if desired_revision is not None and revision is not None and revision != desired_revision:
            issues.append(f"revision mismatch {revision} != {desired_revision}")
        state = str(raw.get("process_state", "") or "")
        if training_enabled and raw.get("role") == "dedicated" and state != "running":
            issues.append(f"dedicated trainer state={state or 'unknown'}")

        freshness = log_freshness.get(trainer_id)
        expects_uploaded_logs = (
            raw.get("role") == "dedicated"
            and trainer_id != "central-learner"
        )
        if expects_uploaded_logs and freshness is None:
            issues.append("no uploaded logs for bundled run")
        elif freshness and freshness.get("age_seconds") is not None:
            age = float(freshness["age_seconds"])
            if age > TRAINER_LOG_STALE_SECONDS:
                issues.append(f"uploaded logs are {age:.1f}s old")

        if issues:
            diagnostics.append(
                {
                    "kind": "trainer-health",
                    "trainer_id": trainer_id,
                    "issues": issues,
                }
            )
            warnings.append(f"trainer {trainer_id}: " + "; ".join(issues))

    if cluster:
        expected = cluster.get("expectedTrainers")
        if isinstance(expected, list):
            for value in expected:
                trainer_id = str(value)
                if trainer_id and trainer_id not in present_ids:
                    diagnostics.append(
                        {
                            "kind": "missing-trainer",
                            "trainer_id": trainer_id,
                        }
                    )
                    warnings.append(f"expected trainer is missing: {trainer_id}")

    return diagnostics, warnings


def _snapshot_model(
    snapshot: Optional[dict[str, Any]],
    results_root: Path,
    warnings: list[str],
) -> Optional[Path]:
    if not snapshot:
        return None
    status = str(snapshot.get("status", "") or "")
    if status != "succeeded":
        if status:
            reason = str(snapshot.get("error", "") or snapshot.get("reason", "") or status)
            warnings.append(f"live model snapshot {status}: {reason}")
        return None
    raw_path = str(snapshot.get("model_path", "") or "")
    if not raw_path:
        warnings.append("live model snapshot succeeded without a model_path")
        return None
    path = Path(raw_path).expanduser().resolve()
    try:
        path.relative_to(results_root.resolve())
    except ValueError:
        warnings.append(f"live model snapshot path is outside run results: {path}")
        return None
    if not path.is_file():
        warnings.append(f"live model snapshot file is missing: {path}")
        return None
    return path


def create_bundle(
    *,
    bees_root: Path,
    assets_root: Path,
    log_percent: float,
    run_id: str = "",
    status_json: Optional[Path] = None,
    status_text: Optional[Path] = None,
    snapshot_json: Optional[Path] = None,
    benchmark_json: Optional[Path] = None,
    output_root: Optional[Path] = None,
) -> Path:
    bees_root = bees_root.expanduser().resolve()
    assets_root = assets_root.expanduser().resolve()
    if not (0.1 <= log_percent <= 100.0):
        raise ValueError("log percent must be between 0.1 and 100")
    resolved_run = resolve_run_id(bees_root, run_id, status_json)
    training_root = bees_root / "Training"
    results_root = training_root / "trainer-results" / resolved_run
    trainer_logs_root = training_root / "TrainerLogs" / resolved_run
    output_root = (output_root or (bees_root / "Diagnostics")).expanduser().resolve()
    output_root.mkdir(parents=True, exist_ok=True)

    warnings: list[str] = []
    diagnostics: list[dict[str, Any]] = []
    records: list[dict[str, Any]] = []
    generated_utc = datetime.now(timezone.utc)
    timestamp = generated_utc.strftime("%Y%m%dT%H%M%SZ")
    status_value = _json(status_json) if status_json else None
    snapshot_value = _json(snapshot_json) if snapshot_json else None
    benchmark_value = _json(benchmark_json) if benchmark_json else None
    cluster_value = _json(assets_root / "Training" / "bees.cluster.json")
    log_freshness = _trainer_log_freshness(trainer_logs_root, generated_utc)
    status_diagnostics, status_warnings = _diagnose_status(
        status_value,
        cluster_value,
        resolved_run,
        log_freshness,
    )
    diagnostics.extend(status_diagnostics)
    warnings.extend(status_warnings)
    final_zip = (
        output_root
        / f"bees-training-diagnostic-{resolved_run}-{timestamp}.zip"
    )

    with tempfile.TemporaryDirectory(
        prefix=".bees-diagnostic-",
        dir=output_root,
    ) as temporary:
        staging = Path(temporary)
        combined_path = staging / "combined-logs.txt"
        with combined_path.open("wb") as combined:
            combined.write(
                (
                    "Bees training diagnostic logs\n"
                    f"Run: {resolved_run}\n"
                    f"Generated UTC: {datetime.now(timezone.utc).isoformat()}\n"
                    f"Tail percentage per text log: {log_percent:g}%\n"
                ).encode("utf-8")
            )
            _collect_log_group(
                bees_root / "Logs" / "Server",
                staging,
                "logs/server",
                log_percent,
                combined,
                records,
                warnings,
            )
            _collect_log_group(
                bees_root / "Logs" / "Training",
                staging,
                "logs/learner",
                log_percent,
                combined,
                records,
                warnings,
            )
            _collect_log_group(
                trainer_logs_root,
                staging,
                "logs/trainers",
                log_percent,
                combined,
                records,
                warnings,
            )

        metadata_sources = [
            ("status/status.json", status_json),
            ("status/status.txt", status_text),
            ("status/model-snapshot.json", snapshot_json),
            ("status/deterministic-benchmark.json", benchmark_json),
            (
                "config/bees.cluster.json",
                assets_root / "Training" / "bees.cluster.json",
            ),
            (
                "config/rl_1v1_config.yaml",
                assets_root / "Training" / "rl_1v1_config.yaml",
            ),
            (
                "run/run-lifecycle.json",
                training_root / "RunLifecycle" / "current.json",
            ),
            (
                "run/latest-training-release.json",
                bees_root / "Builds" / "latest-training-release.json",
            ),
            (
                "run/continual-state.json",
                training_root
                / "metadata"
                / "continuous-service"
                / resolved_run
                / "state.json",
            ),
            (
                "run/current-deployment.json",
                training_root / "deployment" / "current-deployment.json",
            ),
        ]
        for archive_path, source in metadata_sources:
            if not source:
                continue
            source = Path(source)
            if not source.is_file():
                continue
            metadata = _copy_metadata(source, staging / archive_path, warnings)
            if metadata:
                records.append(
                    {
                        "archive_path": archive_path,
                        "kind": "metadata",
                        **metadata,
                    }
                )

        trainer_config_root = (
            training_root
            / "metadata"
            / "continuous-service"
            / resolved_run
            / "trainer-configs"
        )
        latest_config = latest_file(trainer_config_root, "*.yaml")
        if latest_config:
            archive_path = f"run/latest-trainer-config-{latest_config.name}"
            metadata = _copy_metadata(
                latest_config,
                staging / archive_path,
                warnings,
            )
            if metadata:
                records.append(
                    {
                        "archive_path": archive_path,
                        "kind": "trainer-config",
                        **metadata,
                    }
                )

        if results_root.is_dir():
            for source in sorted(results_root.rglob("*")):
                if not source.is_file() or source.is_symlink():
                    continue
                name = source.name.lower()
                if source.suffix.lower() != ".json":
                    continue
                if "timer" not in name and name != "training_status.json":
                    continue
                rel = _safe_rel(source, results_root)
                archive_path = f"timers/{rel}"
                metadata = _copy_metadata(
                    source,
                    staging / archive_path,
                    warnings,
                )
                if metadata:
                    records.append(
                        {
                            "archive_path": archive_path,
                            "kind": "timer-or-status",
                            **metadata,
                        }
                    )

        model = _snapshot_model(snapshot_value, results_root, warnings)
        model_source = "live-snapshot" if model is not None else "latest-on-disk"
        if model is None:
            model = latest_file(results_root, "*.onnx")

        status_run = _run_from_status(status_json)
        same_live_run = not status_run or status_run == resolved_run
        learner_step = _learner_step(
            status_text if same_live_run else None,
            (bees_root / "Logs" / "Training") if same_live_run else None,
            results_root,
        )
        model_step = _model_step(model, snapshot_value if model_source == "live-snapshot" else None)
        model_lag_steps = (
            max(0, learner_step - model_step)
            if learner_step is not None and model_step is not None
            else None
        )
        if model_lag_steps is not None and model_lag_steps > MODEL_LAG_WARNING_STEPS:
            warnings.append(
                f"bundled model is {model_lag_steps} learner steps behind "
                f"(learner={learner_step}, model={model_step})"
            )
            diagnostics.append(
                {
                    "kind": "model-lag",
                    "learner_step": learner_step,
                    "model_step": model_step,
                    "lag_steps": model_lag_steps,
                }
            )

        model_info = None
        if model:
            archive_path = f"model/{model.name}"
            destination = staging / archive_path
            destination.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(model, destination)
            stat = model.stat()
            model_info = {
                "archive_path": archive_path,
                "source": str(model),
                "selection": model_source,
                "step": model_step,
                "size_bytes": stat.st_size,
                "sha256": sha256_file(model),
                "modified_utc": datetime.fromtimestamp(
                    stat.st_mtime,
                    tz=timezone.utc,
                ).isoformat(),
            }
            records.append({"kind": "latest-onnx", **model_info})
        else:
            warnings.append(f"no ONNX file found under {results_root}")

        for trainer_id, freshness in log_freshness.items():
            if freshness.get("age_seconds") is None:
                warnings.append(f"trainer {trainer_id} has no uploaded run logs")
            elif float(freshness["age_seconds"]) > TRAINER_LOG_STALE_SECONDS:
                diagnostics.append(
                    {
                        "kind": "trainer-log-freshness",
                        "trainer_id": trainer_id,
                        **freshness,
                    }
                )

        manifest = {
            "schema_version": 2,
            "generated_utc": generated_utc.isoformat(),
            "run_id": resolved_run,
            "log_percent": log_percent,
            "learner_step": learner_step,
            "model_step": model_step,
            "model_lag_steps": model_lag_steps,
            "model_snapshot": snapshot_value,
            "deterministic_benchmark": benchmark_value,
            "latest_onnx": model_info,
            "trainer_log_freshness": log_freshness,
            "diagnostics": diagnostics,
            "warnings": warnings,
            "files": records,
        }
        (staging / "manifest.json").write_text(
            json.dumps(manifest, indent=2, sort_keys=True) + "\n",
            encoding="utf-8",
        )

        temp_zip = final_zip.with_suffix(".zip.tmp")
        if temp_zip.exists():
            temp_zip.unlink()
        with zipfile.ZipFile(
            temp_zip,
            "w",
            compression=zipfile.ZIP_DEFLATED,
            allowZip64=True,
        ) as archive:
            for source in sorted(staging.rglob("*")):
                if source.is_file():
                    archive.write(
                        source,
                        source.relative_to(staging).as_posix(),
                    )
        os.replace(temp_zip, final_zip)

        # Live diagnostic exports are ephemeral. The ZIP now owns the captured copy;
        # remove the source export so repeated bundle creation cannot consume disk space.
        if (
            model_source == "live-snapshot"
            and model is not None
            and model.name.startswith("diagnostic-")
        ):
            try:
                model.unlink()
            except OSError:
                pass

    return final_zip


def parse_args(argv: Optional[list[str]] = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Create a compact Bees training diagnostic bundle."
    )
    parser.add_argument("--bees-root", required=True)
    parser.add_argument("--assets-root", required=True)
    parser.add_argument("--run-id", default="")
    parser.add_argument("--log-percent", type=float, default=10.0)
    parser.add_argument("--status-json")
    parser.add_argument("--status-text")
    parser.add_argument("--snapshot-json")
    parser.add_argument("--benchmark-json")
    parser.add_argument("--output-root")
    return parser.parse_args(argv)


def main(argv: Optional[list[str]] = None) -> int:
    args = parse_args(argv)
    archive = create_bundle(
        bees_root=Path(args.bees_root),
        assets_root=Path(args.assets_root),
        log_percent=args.log_percent,
        run_id=args.run_id,
        status_json=Path(args.status_json) if args.status_json else None,
        status_text=Path(args.status_text) if args.status_text else None,
        snapshot_json=Path(args.snapshot_json) if args.snapshot_json else None,
        benchmark_json=Path(args.benchmark_json) if args.benchmark_json else None,
        output_root=Path(args.output_root) if args.output_root else None,
    )
    print(f"Created diagnostic bundle: {archive}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
