#!/usr/bin/env python3
"""Compare existing TinyBrains checkpoints against fixed Hub-compatible suites.

This is an independent local audit, not a Hub job-evaluation receipt.
"""

import argparse
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import tempfile

from experiment_hub_eval_io import suite_digest
from experiment_hub_tinybrains_evaluate import evaluate, read_suite
from experiment_hub_tinybrains_runtime import configure_engine


def checkpoint_digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def write_atomic(path, report):
    body = json.dumps(report, indent=2, sort_keys=True, allow_nan=False) + "\n"
    fd, name = tempfile.mkstemp(prefix=".tinybrains-baseline-", dir=path.parent)
    try:
        with os.fdopen(fd, "w", encoding="utf-8") as stream:
            stream.write(body)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(name, path)
    finally:
        if os.path.exists(name):
            os.unlink(name)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--candidate", action="append", required=True,
                        help="Repeat LABEL=/absolute/path/to/checkpoint.pt.")
    parser.add_argument("--suite", action="append", required=True, type=Path,
                        help="Repeat for the Nano and Micro held-out suites.")
    parser.add_argument("--scratch", required=True, type=Path)
    parser.add_argument("--out", required=True, type=Path)
    parser.add_argument("--engine-path", type=Path)
    args = parser.parse_args()

    configure_engine(args.engine_path)
    scratch = args.scratch.expanduser().resolve(strict=True)
    if not scratch.is_dir():
        parser.error("--scratch must be an existing directory.")
    output = args.out.expanduser().absolute()
    if output.exists():
        parser.error("--out already exists; use a new immutable report path.")
    output.parent.mkdir(parents=True, exist_ok=True, mode=0o700)

    candidates = []
    labels = set()
    for raw in args.candidate:
        label, sep, location = raw.partition("=")
        if not sep or not label or not location or label in labels:
            parser.error("Each candidate must be a unique LABEL=/absolute/checkpoint.pt.")
        file = Path(location)
        if not file.is_absolute() or file.resolve(strict=True) != file or not file.is_file():
            parser.error(f"Invalid, missing, or redirected checkpoint: {label}")
        labels.add(label)
        candidates.append((label, file, checkpoint_digest(file)))

    suites = [read_suite(file.expanduser().resolve(strict=True)) for file in args.suite]
    if len(set(s["suite_id"] for s in suites)) != len(suites):
        parser.error("Use distinct immutable suite IDs.")
    report = {
        "schema_version": 1,
        "scope": "Independent local checkpoint comparison; not a Hub promotion receipt.",
        "created_utc": datetime.now(timezone.utc).isoformat(),
        "complete": False,
        "results": [],
    }
    for label, file, digest in candidates:
        for suite in suites:
            matches = []
            with tempfile.TemporaryDirectory(prefix="tinybrains-compare-", dir=scratch) as work:
                wins, trials = evaluate(file, digest, suite, Path(work),
                                        on_result=matches.append)
            if len(matches) != trials:
                raise RuntimeError("The fixed benchmark ended without complete match evidence.")
            draws = sum(match["outcome"] == "draw" for match in matches)
            losses = trials - wins - draws
            result = {
                "candidate": label,
                "checkpoint": str(file),
                "checkpoint_sha256": digest,
                "suite_id": suite["suite_id"],
                "suite_sha256": suite_digest(suite),
                "opponent": suite.get("opponent", "nano"),
                "wins": wins,
                "draws": draws,
                "losses": losses,
                "trials": trials,
                "score_rate": (wins + draws / 2) / trials,
                "matches": matches,
            }
            report["results"].append(result)
            write_atomic(output, report)
            print(f"{label} vs {result['opponent']}: W {wins}, L {losses}, D {draws}"
                  f" / {trials}; score {result['score_rate']:.1%}", flush=True)
    report["complete"] = True
    write_atomic(output, report)
    print(f"Complete independent baseline report: {output}")


if __name__ == "__main__":
    main()
