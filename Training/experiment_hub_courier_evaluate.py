#!/usr/bin/env python3
"""Evaluate a pinned Courier policy on held-out survival scenarios for the Hub."""

import argparse
import hashlib
import json
import shutil
import sys
import tempfile
from pathlib import Path

from experiment_hub_eval_io import SHA256, SUITE_ID, UUID, publish, read_bounded, suite_digest


def read_suite(file):
    suite = json.loads(read_bounded(file, 8192))
    if not isinstance(suite, dict) or set(suite) != {
        "suite_id", "config_sha256", "seed", "trials", "horizon",
    }:
        raise ValueError("Expected suite_id, config_sha256, seed, trials, and horizon.")
    if (
        not isinstance(suite["suite_id"], str) or not SUITE_ID.fullmatch(suite["suite_id"])
        or not isinstance(suite["config_sha256"], str) or not SHA256.fullmatch(suite["config_sha256"])
        or type(suite["seed"]) is not int or not 0 <= suite["seed"] < 2**32
        or suite["seed"] in (981723, 172981)  # Courier's built-in reporting scenarios.
        or type(suite["trials"]) is not int or not 1 <= suite["trials"] <= 128
        or type(suite["horizon"]) is not int or not 1 <= suite["horizon"] <= 16384
    ):
        raise ValueError("Invalid Courier evaluation suite.")
    return suite


def evaluate(project, checkpoint, digest, config_file, suite, work):
    config = read_bounded(config_file, 256 * 1024)
    if hashlib.sha256(config).hexdigest() != suite["config_sha256"]:
        raise ValueError("Courier environment did not match the suite SHA-256.")
    pinned = work / "policy.zip"
    shutil.copyfile(checkpoint, pinned)
    with pinned.open("rb") as stream:
        if hashlib.file_digest(stream, "sha256").hexdigest() != digest:
            raise ValueError("Courier policy did not match the pinned SHA-256.")

    sys.path.insert(0, str(project))
    import torch
    from courier_env import evaluate as evaluate_policy, validate
    from courier_safe_ppo import SafeRecurrentPPO

    torch.set_num_threads(1)
    model = SafeRecurrentPPO.load(pinned, device="cpu")
    report = evaluate_policy(model, validate(json.loads(config)), split="report",
                             count=suite["trials"], seed=suite["seed"],
                             horizon=suite["horizon"], include_trace=False)
    alive = report["alive"]
    if type(alive) is not int or not 0 <= alive <= suite["trials"] or report["trials"] != suite["trials"]:
        raise ValueError("Invalid Courier survival counts.")
    return alive


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for option in ("project-root", "checkpoint", "config", "suite", "scratch", "receipt-root"):
        parser.add_argument("--" + option, required=True, type=Path)
    parser.add_argument("--dispatch-id", required=True)
    parser.add_argument("--checkpoint-sha256", required=True)
    args = parser.parse_args()
    try:
        if not UUID.fullmatch(args.dispatch_id) or not SHA256.fullmatch(args.checkpoint_sha256):
            raise ValueError("Expected a Hub dispatch UUID and policy SHA-256.")
        project = args.project_root.resolve(strict=True)
        checkpoint = args.checkpoint.resolve(strict=True)
        scratch = args.scratch.resolve(strict=True)
        receipts = args.receipt_root.resolve(strict=True)
        for directory in (scratch, receipts):
            if not directory.is_dir() or any(directory.is_relative_to(root) or root.is_relative_to(directory)
                                             for root in (project, checkpoint.parent)):
                raise ValueError("Use evaluator directories separate from the project and policy directory.")
        suite = read_suite(args.suite)
        with tempfile.TemporaryDirectory(prefix="courier-eval-", dir=scratch) as work:
            alive = evaluate(project, checkpoint, args.checkpoint_sha256, args.config, suite, Path(work))
        receipt = {
            "schema_version": 1, "suite_id": suite["suite_id"], "suite_sha256": suite_digest(suite),
            "dispatch_id": args.dispatch_id, "checkpoint_sha256": args.checkpoint_sha256,
            "trials": suite["trials"], "successes": alive,
        }
        print(publish(receipts, receipt))
        print(f"Courier held-out survival: {alive}/{suite['trials']} at {suite['horizon']} ticks")
        return 0
    except (OSError, ValueError, RuntimeError, KeyError, TypeError) as exc:
        print(f"Courier evaluation failed: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
