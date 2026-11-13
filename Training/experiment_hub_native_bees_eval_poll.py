#!/usr/bin/env python3
"""Schedule native Bees fixed-opponent evaluation for one Hub dispatch per poll.

The shared Hub poller owns immutable checkpoint snapshots, private scratch,
durable cursors, dispatch identities and atomic independent receipts. This
adapter only finds committed native round weights and runs the existing
native physics/policy evaluator against a fully pinned suite.
"""

import argparse
import http.client
import json
from pathlib import Path
import sys

from experiment_hub_eval_io import SUITE_ID, UUID, read_bounded
from experiment_hub_eval_poll import poll_checkpoint, poll_batch
from experiment_hub_native_bees_evaluate import evaluate, read_suite

_NATIVE_TOOLS = Path(__file__).resolve().parents[1] / "native" / "tools"
sys.path.insert(0, str(_NATIVE_TOOLS))
from native_training_artifacts import file_sha256


def committed_checkpoint(artifact_root: Path, job: dict):
    identity = job["job_id"]
    if not UUID.fullmatch(identity) or not UUID.fullmatch(job["dispatch_id"]):
        raise ValueError("Invalid native Bees Hub job/dispatch identity.")
    directory = artifact_root / identity
    if directory.resolve() != directory:
        raise ValueError("Native Bees candidate artifact directory was redirected.")
    receipt_file = directory / "round-result.json"
    if not receipt_file.is_file():
        return None
    if receipt_file.resolve(strict=True) != receipt_file:
        raise ValueError("Native Bees learner receipt was redirected.")
    receipt = json.loads(read_bounded(receipt_file, 65536))
    if (receipt.get("kind") != "native_training_round_complete" or
            receipt.get("schema_version") != 1):
        raise ValueError("Native Bees candidate has no committed training round.")
    checkpoint = directory / "learner" / "checkpoint.pt"
    manifest = directory / "learner" / "manifest.json"
    if (checkpoint.resolve(strict=True) != checkpoint or
            manifest.resolve(strict=True) != manifest or
            file_sha256(checkpoint) != receipt.get("next_checkpoint_sha256") or
            file_sha256(manifest) != receipt.get("next_manifest_sha256")):
        raise ValueError("Native Bees candidate checkpoint/manifest differs from its completion receipt.")
    if (job.get("final_checkpoint_sha256") is not None and
            job["final_checkpoint_sha256"] != receipt["next_checkpoint_sha256"]):
        raise ValueError("Native Bees final checkpoint does not match the Hub's approved model identity.")
    return 1, checkpoint


def poll(args, suites):
    paths = {suite["suite_id"]: path for path, suite in suites}

    def evaluate_checkpoint(job, frozen, digest, work, suite):
        source = args.artifact_root / job["job_id"] / "learner" / "manifest.json"
        return evaluate(
            frozen, digest, source, paths[suite["suite_id"]],
            suite, work, args.rollout_binary,
        )

    def find_checkpoint(job):
        return committed_checkpoint(args.artifact_root, job)

    if len(suites) == 1:
        path, suite = suites[0]
        poll_checkpoint(
            args, "bees_native", suite, suite, find_checkpoint,
            lambda job, file, digest, work:
                evaluate_checkpoint(job, file, digest, work, suite),
        )
    else:
        poll_batch(
            args, "bees_native", [(suite, suite) for _, suite in suites],
            find_checkpoint, evaluate_checkpoint,
        )


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--machine", required=True)
    parser.add_argument("--port", type=int, default=7148)
    parser.add_argument("--suite", required=True, type=Path, action="append")
    parser.add_argument("--rollout-binary", required=True, type=Path)
    for key in ("artifact-root", "scratch", "receipt-root"):
        parser.add_argument("--" + key, required=True, type=Path)
    args = parser.parse_args()
    try:
        if not SUITE_ID.fullmatch(args.machine) or not 1 <= args.port <= 65535:
            raise ValueError("Invalid native Bees evaluation worker identity.")
        directories = []
        for key in ("artifact_root", "scratch", "receipt_root"):
            directory = getattr(args, key).resolve(strict=True)
            if not directory.is_dir() or any(
                directory.is_relative_to(other) or other.is_relative_to(directory)
                for other in directories
            ):
                raise ValueError("Native Bees artifacts, scratch and receipts must use separate directories.")
            setattr(args, key, directory)
            directories.append(directory)
        args.rollout_binary = args.rollout_binary.resolve(strict=True)
        if not args.rollout_binary.is_file():
            raise ValueError("Native evaluation requires a compiled native policy_rollout executable.")
        suites = [(file.resolve(strict=True), read_suite(file)) for file in args.suite]
        if not 1 <= len(suites) <= 8 or len({
            suite["suite_id"] for _, suite in suites
        }) != len(suites):
            raise ValueError("Expected one or 2–8 distinct native evaluation suites.")
        poll(args, suites)
        return 0
    except (OSError, RuntimeError, ValueError, TypeError, KeyError,
            http.client.HTTPException) as exc:
        print(f"Scheduled native Bees evaluation failed: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
