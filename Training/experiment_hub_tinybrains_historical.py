#!/usr/bin/env python3
"""Evaluate existing TinyBrains update checkpoints into a read-only Hub comparison audit.

Example:
  python Training/experiment_hub_tinybrains_historical.py \
    --run-name "TinyBrains historical training" \
    --checkpoint /models/update-000073.pt \
    --checkpoint /models/update-000100.pt \
    --suite /evaluators/nano.json --suite /evaluators/micro.json \
    --scratch /evaluators/scratch --output /evaluators/historical-audit.json

Import the resulting JSON in Experiment Hub's historical evaluation panel.
This never creates a native Hub job, changes a trainer checkpoint or starts training.
"""

import argparse
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import sys
import tempfile

from experiment_hub_eval_io import SHA256, suite_digest
from experiment_hub_tinybrains_evaluate import evaluate_with_metrics, read_suite
from experiment_hub_tinybrains_runtime import configure_engine


CHECKPOINT = re.compile(r"update-([0-9]{6,8})\.pt")


def original_checkpoint(path: Path) -> tuple[int, Path]:
    match = CHECKPOINT.fullmatch(path.name)
    if not path.is_absolute() or not match or int(match[1]) <= 0:
        raise ValueError("Use absolute update-000073.pt-style checkpoint paths with positive updates.")
    if path.resolve(strict=True) != path or not path.is_file():
        raise ValueError(f"Checkpoint must be an unredirected regular file: {path}")
    return int(match[1]), path


def audit(args) -> dict:
    if not args.run_name.strip() or len(args.run_name) > 100:
        raise ValueError("Historical run label must contain 1–100 characters.")
    if not 1 <= len(args.checkpoint) <= 64 or not 1 <= len(args.suite) <= 8:
        raise ValueError("Select 1–64 checkpoints and 1–8 approved frozen suites.")
    if len(args.checkpoint) * len(args.suite) > 64:
        raise ValueError("One historical audit is limited to 64 checkpoint/suite observations.")

    checkpoints = sorted((original_checkpoint(p) for p in args.checkpoint))
    if len({update for update, _ in checkpoints}) != len(checkpoints):
        raise ValueError("Repeated historical checkpoint update.")
    suites = [read_suite(p) for p in args.suite]
    if len({suite["suite_id"] for suite in suites}) != len(suites):
        raise ValueError("Every historical suite must have a distinct ID.")
    if any(not SHA256.fullmatch(suite.get("runtime_sha256", "")) for suite in suites):
        raise ValueError("Historical comparison requires an exact pinned evaluation runtime SHA-256 for every suite.")

    scratch = args.scratch.resolve(strict=True)
    output = args.output
    if not scratch.is_dir() or not output.is_absolute() or output.exists() or not output.parent.is_dir():
        raise ValueError("Use an existing private scratch directory and a new absolute output filename.")
    if output.parent.resolve(strict=True) != output.parent or output.is_symlink():
        raise ValueError("Output directory/file must not be redirected.")
    if any(scratch.is_relative_to(path.parent) or
           output.parent.is_relative_to(path.parent) for _, path in checkpoints):
        raise ValueError("Scratch and output directories must be separate from checkpoint sources.")

    configure_engine(args.engine_path)
    results = []
    # Reuse the installed independent TinyBrains evaluator, including its
    # fixed-opponent pins, randomized seats, runtime attestation and replay
    # outcome validation. No Hub dispatch, native reservation or trainer runs.
    for update, original in checkpoints:
        with tempfile.TemporaryDirectory(prefix="tinybrains-historical-", dir=scratch) as temp:
            temp = Path(temp)
            frozen = temp / "immutable-source.pt"
            before = original.stat()
            shutil.copyfile(original, frozen)
            after = original.stat()
            if (before.st_dev != after.st_dev or before.st_ino != after.st_ino or
                before.st_size != after.st_size or before.st_mtime_ns != after.st_mtime_ns or
                not before.st_size):
                raise ValueError(f"Checkpoint changed during private copy: {original}")
            with frozen.open("rb") as stream:
                digest = hashlib.file_digest(stream, "sha256").hexdigest()
            for suite in suites:
                work = temp / ("evaluation-" + suite["suite_id"])
                work.mkdir()
                measured = evaluate_with_metrics(frozen, digest, suite, work)
                trials = 2 * len(suite["maps"]) * len(suite["seeds"])
                canonical = json.dumps(suite, sort_keys=True)
                results.append({
                    "checkpoint_name": original.name,
                    "steps_completed": update,
                    "checkpoint_sha256": digest,
                    "suite_id": suite["suite_id"],
                    "suite_definition_canonical": canonical,
                    "suite_sha256": suite_digest(suite),
                    "runtime_sha256": suite["runtime_sha256"],
                    "evaluated_at": datetime.now(timezone.utc).isoformat(
                        timespec="milliseconds").replace("+00:00", "Z"),
                    "trials": trials, "successes": measured["successes"],
                    "metrics": measured["metrics"],
                })

    identity = hashlib.sha256(json.dumps(results, sort_keys=True,
        separators=(",", ":")).encode("utf-8")).hexdigest()
    return {"project_id": "tinybrains", "complete": True,
            "run_name": args.run_name.strip(),
            "source_report_sha256": identity, "results": results}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--run-name", required=True)
    parser.add_argument("--checkpoint", required=True, type=Path, action="append",
                        help="Repeat for exact existing update-NNNNNN.pt files.")
    parser.add_argument("--suite", required=True, type=Path, action="append",
                        help="Repeat for fixed Nano/Micro suite definitions.")
    parser.add_argument("--scratch", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--engine-path", type=Path)
    args = parser.parse_args()
    try:
        report = audit(args)
        body = (json.dumps(report, ensure_ascii=False, indent=2) + "\n").encode("utf-8")
        if len(body) > 65536:
            raise ValueError("Historical evaluation report exceeds the Hub import size limit.")
        # Exclusive creation never overwrites user files or native receipts.
        # Failure before the complete audit finishes leaves no output.
        fd = os.open(args.output, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
        with os.fdopen(fd, "wb") as stream:
            stream.write(body)
            stream.flush()
            os.fsync(stream.fileno())
        print(f"Historical evaluation audit: {args.output} ({len(report['results'])} observations)")
        return 0
    except (OSError, RuntimeError, ValueError, TypeError, KeyError) as error:
        print(f"Historical evaluation failed: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
