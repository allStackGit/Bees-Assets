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


def tail_offset(size: int, percent: float) -> int:
    if size <= 0 or percent >= 100.0:
        return 0
    wanted = max(1, int(math.ceil(size * percent / 100.0)))
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


def create_bundle(
    *,
    bees_root: Path,
    assets_root: Path,
    log_percent: float,
    run_id: str = "",
    status_json: Optional[Path] = None,
    status_text: Optional[Path] = None,
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
    records: list[dict[str, Any]] = []
    timestamp = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ")
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

        model = latest_file(results_root, "*.onnx")
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

        manifest = {
            "schema_version": 1,
            "generated_utc": datetime.now(timezone.utc).isoformat(),
            "run_id": resolved_run,
            "log_percent": log_percent,
            "latest_onnx": model_info,
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
        output_root=Path(args.output_root) if args.output_root else None,
    )
    print(f"Created diagnostic bundle: {archive}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
