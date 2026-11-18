#!/usr/bin/env python3
"""Compare existing TinyBrains checkpoints against fixed Hub-compatible suites.

This is an independent local audit, not a Hub job-evaluation receipt.
"""

import argparse
from datetime import datetime, timezone
import hashlib
import http.client
import json
import os
import re
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
    parser.add_argument("--publish-to-hub", action="store_true",
                        help="Import a complete checkpoint audit into the local Hub's read-only Compare history.")
    parser.add_argument("--hub-run-name", default=None,
                        help="Comparison label for the historical run in the Hub.")
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
        if scratch.is_relative_to(file.parent) or output.is_relative_to(file.parent):
            parser.error("Evaluator scratch and reports must stay outside checkpoint directories.")
        labels.add(label)
        candidates.append((label, file, checkpoint_digest(file)))

    suites = [read_suite(file.expanduser().resolve(strict=True)) for file in args.suite]
    if len(set(s["suite_id"] for s in suites)) != len(suites):
        parser.error("Use distinct immutable suite IDs.")
    if args.publish_to_hub:
        if args.hub_run_name is not None and not 1 <= len(args.hub_run_name.strip()) <= 100:
            parser.error("--hub-run-name must have 1–100 characters.")
        for label, file, digest in candidates:
            if not re.fullmatch(r"update-([0-9]{6,8})\.pt", file.name):
                parser.error("Hub historical charts require an update-NNNNNN.pt checkpoint filename "
                             "for a recorded training-update coordinate: " + str(file.name))
        if any("runtime_sha256" not in suite for suite in suites):
            parser.error("Hub imports require frozen suites with a pinned runtime_sha256.")
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
            if checkpoint_digest(file) != digest:
                raise ValueError("Source checkpoint changed during the independent evaluation.")
            result = {
                "candidate": label,
                "evaluated_at": datetime.now(timezone.utc).isoformat(timespec="milliseconds")
                    .replace("+00:00", "Z"),
                "steps_completed": int(re.fullmatch(r"update-([0-9]{6,8})\.pt", file.name)[1])
                    if re.fullmatch(r"update-([0-9]{6,8})\.pt", file.name) else None,
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
    # Rank only comparable complete suites. A ranking is descriptive, not
    # evidence that the leading candidate improved significantly.
    grouped = {}
    for result in report["results"]:
        grouped.setdefault(result["candidate"], {})[result["opponent"]] = result
    if grouped and all("micro" in value and "nano" in value for value in grouped.values()):
        ranked = sorted(grouped, key=lambda name: (
            -grouped[name]["micro"]["score_rate"],
            -grouped[name]["nano"]["score_rate"], name))
        report["provisional_rank"] = [
            {"candidate": name,
             "micro_score_rate": grouped[name]["micro"]["score_rate"],
             "nano_score_rate": grouped[name]["nano"]["score_rate"]}
            for name in ranked
        ]
        print("Provisional fixed-opponent leader:", ranked[0],
              "(not a statistical promotion decision)", flush=True)
    report["complete"] = True
    write_atomic(output, report)
    print(f"Complete independent baseline report: {output}")
    if args.publish_to_hub:
        by_suite = {suite["suite_id"]: suite for suite in suites}
        items = []
        for result in report["results"]:
            suite = by_suite[result["suite_id"]]
            items.append({
                "checkpoint_name": Path(result["checkpoint"]).name,
                "checkpoint_sha256": result["checkpoint_sha256"],
                "suite_id": result["suite_id"],
                "suite_sha256": result["suite_sha256"],
                "suite_definition_canonical": json.dumps(suite, sort_keys=True),
                "runtime_sha256": suite["runtime_sha256"],
                "steps_completed": result["steps_completed"],
                "evaluated_at": result["evaluated_at"],
                "trials": result["trials"],
                "successes": result["wins"],
                "metrics": {
                    "win_count": {"value": result["wins"], "unit": "count"},
                    "loss_count": {"value": result["losses"], "unit": "count"},
                    "draw_count": {"value": result["draws"], "unit": "count"},
                    "score_rate": {"value": result["score_rate"], "unit": "ratio"},
                },
            })
        request = {
            "project_id": "tinybrains", "complete": True,
            "run_name": (args.hub_run_name or output.stem).strip()[:100],
            "source_report_sha256": hashlib.sha256(output.read_bytes()).hexdigest(),
            "results": items,
        }
        body = json.dumps(request, separators=(",", ":"), allow_nan=False).encode("utf-8")
        if len(body) > 65536:
            raise ValueError("Historical import request exceeds the Hub API limit; "
                             "split the comparison into smaller checkpoint groups.")
        conn = http.client.HTTPConnection("127.0.0.1", 7148, timeout=20)
        try:
            conn.request("POST", "/api/hub/evaluation/historical", body=body,
                         headers={"Content-Type": "application/json"})
            with conn.getresponse() as response:
                message = response.read(65537)
                if len(message) > 65536:
                    raise RuntimeError("Hub response exceeded its size limit.")
                if response.status not in (200, 201):
                    raise RuntimeError(f"Hub import failed (HTTP {response.status}): "
                                       + message.decode("utf-8", errors="replace"))
                imported = json.loads(message)
        finally:
            conn.close()
        print("Historical evaluation archived for Compare under job",
              imported["job_id"], "with", imported["record_count"], "results; "
              "not authorized for native Stop, checkpoint selection or continuation.")


if __name__ == "__main__":
    main()
