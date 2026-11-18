#!/usr/bin/env python3
"""Compare existing TinyBrains checkpoints against fixed Hub-compatible suites.

This is an independent local audit, not a Hub job-evaluation receipt.
"""

import argparse
from datetime import datetime, timezone
import hashlib
import json
import os
import re
from pathlib import Path
import tempfile

from experiment_hub_eval_io import read_bounded, suite_digest, import_historical_audit
from experiment_hub_tinybrains_evaluate import evaluate, read_suite
from experiment_hub_tinybrains_eval_poll import checkpoint_update
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


def publish_report(report, suites, output, run_name):
    """Archive a fully verified local benchmark, never a native stop receipt."""
    if report.get("schema_version") != 1 or report.get("complete") is not True or \
       report.get("scope") != "Independent local checkpoint comparison; not a Hub promotion receipt." or \
       not isinstance(report.get("results"), list) or not 1 <= len(report["results"]) <= 128:
        raise ValueError("Only complete TinyBrains independent baseline reports can be imported.")
    by_suite = {suite["suite_id"]: suite for suite in suites}
    if any("runtime_sha256" not in suite for suite in suites):
        raise ValueError("Hub imports require frozen suites with pinned evaluator runtime identities.")
    items = []
    seen = set()
    lineage = {}
    verified_updates = {}
    for result in report["results"]:
        if not isinstance(result, dict):
            raise ValueError("Invalid baseline result.")
        suite = by_suite.get(result.get("suite_id"))
        if suite is None or result.get("suite_sha256") != suite_digest(suite):
            raise ValueError("Baseline result does not match its exact frozen suite definition.")
        file = Path(result.get("checkpoint", ""))
        if not file.is_absolute() or file.resolve(strict=True) != file or not file.is_file() or \
           checkpoint_digest(file) != result.get("checkpoint_sha256"):
            raise ValueError("Historical checkpoint bytes differ or are unavailable: " + str(file))
        match = re.fullmatch(r"update-([0-9]{6,8})\.pt", file.name)
        if match is None:
            raise ValueError("Checkpoint filename lacks an immutable update coordinate: " + file.name)
        update = int(match[1])
        if result.get("steps_completed", update) != update:
            raise ValueError("Historical checkpoint update does not match the file identity.")
        identity_key = (str(file), result["checkpoint_sha256"])
        if identity_key not in verified_updates:
            # Reuse the native TinyBrains update-field verifier once per
            # unique model; multiple frozen suites may test the same bytes.
            verified_updates[identity_key] = checkpoint_update(file)
        if verified_updates[identity_key] != update:
            raise ValueError(
                "Historical checkpoint filename disagrees with its saved PPO update.")
        candidate = result.get("candidate")
        if not isinstance(candidate, str) or not candidate:
            raise ValueError("Historical candidate label is missing.")
        if candidate in lineage and lineage[candidate] != result["checkpoint_sha256"]:
            raise ValueError("A candidate label refers to conflicting checkpoint bytes.")
        lineage[candidate] = result["checkpoint_sha256"]
        identity = (result["checkpoint_sha256"], suite["suite_id"])
        if identity in seen:
            raise ValueError("Repeated historical checkpoint/suite result.")
        seen.add(identity)
        matches = result.get("matches")
        expected = {(board, seed, seat) for board in suite["maps"]
                    for seed in suite["seeds"] for seat in (0, 1)}
        observed = set()
        counts = {"win": 0, "loss": 0, "draw": 0}
        if not isinstance(matches, list) or len(matches) != len(expected) or \
           result.get("trials") != len(expected):
            raise ValueError("Historical report lacks the complete balanced match evidence.")
        for item in matches:
            if not isinstance(item, dict) or item.get("outcome") not in counts or \
               item.get("opponent") != suite.get("opponent", "nano"):
                raise ValueError("Historical report contains invalid opponent or outcome evidence.")
            key = (item.get("map"), item.get("seed"), item.get("candidate_seat"))
            if key not in expected or key in observed:
                raise ValueError("Historical report contains missing or repeated map/seed/seat pairs.")
            ranks = item.get("ranks")
            if not isinstance(ranks, list) or len(ranks) != 2 or \
               any(type(rank) is not int for rank in ranks):
                raise ValueError("Historical match lacks valid engine ranks.")
            seat = key[2]
            outcome = ("win" if ranks[seat] < ranks[1 - seat] else
                       "loss" if ranks[seat] > ranks[1 - seat] else "draw")
            if outcome != item["outcome"]:
                raise ValueError("Historical outcome disagrees with recorded engine ranks.")
            observed.add(key)
            counts[outcome] += 1
        wins, losses, draws = counts["win"], counts["loss"], counts["draw"]
        if wins != result.get("wins") or losses != result.get("losses") or \
           draws != result.get("draws") or \
           abs((wins + draws / 2) / len(expected) - result.get("score_rate", -1)) > 1e-10:
            raise ValueError("Historical success/score aggregate disagrees with match outcomes.")
        timestamp = result.get("evaluated_at") or report.get("created_utc")
        when = datetime.fromisoformat(timestamp.replace("Z", "+00:00"))
        if when.tzinfo is None:
            raise ValueError("Evaluation timestamp must carry a time zone.")
        timestamp = when.astimezone(timezone.utc).isoformat(timespec="milliseconds").replace("+00:00", "Z")
        items.append({
            "checkpoint_name": file.name,
            "checkpoint_sha256": result["checkpoint_sha256"],
            "suite_id": suite["suite_id"],
            "suite_sha256": suite_digest(suite),
            "suite_definition_canonical": json.dumps(suite, sort_keys=True),
            "runtime_sha256": suite["runtime_sha256"],
            "steps_completed": update,
            "evaluated_at": timestamp,
            "trials": len(expected), "successes": wins,
            "metrics": {
                "win_count": {"value": wins, "unit": "count"},
                "loss_count": {"value": losses, "unit": "count"},
                "draw_count": {"value": draws, "unit": "count"},
                "score_rate": {"value": (wins + draws / 2) / len(expected), "unit": "ratio"},
            },
        })
    if not isinstance(run_name, str) or not 1 <= len(run_name.strip()) <= 100:
        raise ValueError("Historical comparison run name must be 1–100 characters.")
    report_bytes = read_bounded(output, 64 * 1024 * 1024)
    if json.loads(report_bytes) != report:
        raise ValueError("Baseline report changed after it was validated; no Hub import attempted.")
    request = {
        "project_id": "tinybrains", "complete": True,
        "run_name": run_name.strip(),
        "source_report_sha256": hashlib.sha256(report_bytes).hexdigest(),
        "results": items,
    }
    imported = import_historical_audit(request)
    print("Historical evaluation archived for Compare under job",
          imported["job_id"], "with", imported["record_count"], "results; "
          "not authorized for native Stop, checkpoint selection or continuation.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--candidate", action="append",
                        help="Repeat LABEL=/absolute/path/to/checkpoint.pt.")
    parser.add_argument("--suite", action="append", required=True, type=Path,
                        help="Repeat for the Nano and Micro held-out suites.")
    parser.add_argument("--scratch", type=Path)
    parser.add_argument("--out", type=Path)
    parser.add_argument("--publish-existing-report", type=Path,
                        help="Archive an already completed baseline report without rerunning matches.")
    parser.add_argument("--engine-path", type=Path)
    parser.add_argument("--publish-to-hub", action="store_true",
                        help="Import a complete checkpoint audit into the local Hub's read-only Compare history.")
    parser.add_argument("--hub-run-name", default=None,
                        help="Comparison label for the historical run in the Hub.")
    args = parser.parse_args()

    if args.publish_existing_report:
        if args.candidate or args.scratch or args.out or args.publish_to_hub:
            parser.error("--publish-existing-report uses --suite and optional --hub-run-name only.")
        output = args.publish_existing_report.expanduser().resolve(strict=True)
        if not output.is_file():
            parser.error("Existing baseline report must be a regular file.")
        report = json.loads(read_bounded(output, 64 * 1024 * 1024))
        suites = [read_suite(file.expanduser().resolve(strict=True)) for file in args.suite]
        if len(set(suite["suite_id"] for suite in suites)) != len(suites):
            parser.error("Use distinct immutable suite IDs.")
        publish_report(report, suites, output, args.hub_run_name or output.stem)
        return
    if not args.candidate or args.scratch is None or args.out is None:
        parser.error("New checkpoint evaluations require --candidate, --scratch and --out.")
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
        publish_report(report, suites, output, args.hub_run_name or output.stem)


if __name__ == "__main__":
    main()
