#!/usr/bin/env python3
"""Poll Hub-authorized native Bees checkpoints using the existing evaluator owner.

The OS-owned scheduled evaluator, not the Hub or trainer, budgets this CPU-only
independent evaluation. Shared poll cursors and durable receipt publication
retain their existing interrupted-work and same-checkpoint batch semantics.
"""

import argparse
import http.client
from pathlib import Path
import sys

from experiment_hub_bees_native_evaluate import (
    evaluate_checkpoint, read_suite,
)
from experiment_hub_eval_io import SUITE_ID, UUID
from experiment_hub_eval_poll import final_checkpoint, poll_batch, poll_checkpoint


def poll(args, configurations):
    installations = {suite["suite_id"]: installed for suite, installed in configurations}
    suites = [suite for suite, _ in configurations]

    def checkpoint(job):
        # A completed native optimizer publishes checkpoint.pt exactly once,
        # alongside its manifest. Running jobs have no committed candidate;
        # waiting for the final checkpoint does not consume their cursor.
        if "final_checkpoint_sha256" not in job:
            return None
        if not UUID.fullmatch(job["job_id"]):
            raise ValueError("Unrecognized native Bees Hub job.")
        return final_checkpoint(args.artifact_root, job, "learner/checkpoint.pt")

    def evaluate(job, file, digest, work, suite):
        installed = dict(installations[suite["suite_id"]])
        manifest = args.artifact_root / job["job_id"] / "learner" / "manifest.json"
        if not manifest.is_file() or manifest.resolve(strict=True) != manifest:
            raise ValueError("Native completed learner manifest is unavailable or redirected.")
        installed["candidate_manifest"] = manifest
        return evaluate_checkpoint(job, file, digest, work, suite, installed, args.rollout_binary)

    if len(suites) == 1:
        suite = suites[0]
        poll_checkpoint(args, "bees_native", suite, suite, checkpoint,
                        lambda job, file, digest, work:
                            evaluate(job, file, digest, work, suite))
    else:
        poll_batch(args, "bees_native", [(suite, suite) for suite in suites],
                   checkpoint, evaluate)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--machine", required=True)
    parser.add_argument("--port", type=int, default=7148)
    parser.add_argument("--suite", type=Path, action="append", required=True)
    parser.add_argument("--rollout-binary", type=Path, required=True)
    for option in ("artifact-root", "scratch", "receipt-root"):
        parser.add_argument("--" + option, required=True, type=Path)
    args = parser.parse_args()
    try:
        if not SUITE_ID.fullmatch(args.machine) or not 1 <= args.port <= 65535:
            raise ValueError("Invalid native Bees evaluator machine or local port.")
        dirs = []
        for name in ("artifact_root", "scratch", "receipt_root"):
            directory = getattr(args, name).resolve(strict=True)
            if not directory.is_dir() or any(
                directory.is_relative_to(other) or other.is_relative_to(directory)
                for other in dirs
            ):
                raise ValueError("Evaluator scratch, receipts and artifacts require separate roots.")
            setattr(args, name, directory)
            dirs.append(directory)
        binary = args.rollout_binary
        if (not binary.is_absolute() or not binary.is_file() or
                binary.resolve(strict=True) != binary):
            raise ValueError("Native evaluation needs the installed authoritative rollout binary.")
        if not 1 <= len(args.suite) <= 8:
            raise ValueError("Native Bees evaluation requires 1–8 frozen suites.")
        configurations = [read_suite(source) for source in args.suite]
        suite_ids = [definition["suite_id"] for definition, _ in configurations]
        if len(set(suite_ids)) != len(suite_ids):
            raise ValueError("Every native Bees evaluator suite ID must be distinct.")
        poll(args, configurations)
        return 0
    except (OSError, RuntimeError, ValueError, TypeError, KeyError,
            http.client.HTTPException) as exc:
        print(f"Scheduled native Bees evaluation failed: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
