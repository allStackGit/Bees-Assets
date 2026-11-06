#!/usr/bin/env python3
"""Evaluate one trusted Ants checkpoint and publish an Experiment Hub receipt."""

from __future__ import annotations

import argparse
from dataclasses import replace
import hashlib
import json
import os
from pathlib import Path
import re
import sys
import tempfile


UUID = re.compile(r"[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}")
SUITE_ID = re.compile(r"[a-z][a-z0-9_-]{0,47}")
SHA256 = re.compile(r"[a-f0-9]{64}")


def read_bounded(path: Path, limit: int) -> bytes:
    with path.open("rb") as stream:
        data = stream.read(limit + 1)
    if not data or len(data) > limit:
        raise ValueError(f"unexpected file size: {path}")
    return data


def read_suite(path: Path) -> dict:
    suite = json.loads(read_bounded(path, 4096))
    if (
        not isinstance(suite, dict)
        or set(suite) != {"suite_id", "level", "trials", "seed"}
        or not isinstance(suite["suite_id"], str)
        or not SUITE_ID.fullmatch(suite["suite_id"])
        or type(suite["level"]) is not int
        or suite["level"] < 1
        or type(suite["trials"]) is not int
        or not 1 <= suite["trials"] <= 10000
        or type(suite["seed"]) is not int
        or not 0 <= suite["seed"] < 2**64
    ):
        raise ValueError("invalid Ants held-out suite definition")
    return suite


def evaluate(checkpoint: Path, manifest_digest: str, suite: dict, scratch: Path) -> int:
    from antsim.outcome_frontier_training import OutcomeFrontierTrainingSystem
    from antsim.randomness import derive_seed
    import torch

    manifest_path = checkpoint / "manifest.json"
    manifest_bytes = read_bounded(manifest_path, 65536)
    if hashlib.sha256(manifest_bytes).hexdigest() != manifest_digest:
        raise ValueError("checkpoint does not match the pinned manifest SHA-256")
    manifest = json.loads(manifest_bytes)
    for name in ("metadata.json", "configuration.json", "policy.pt"):
        if name not in manifest:
            raise ValueError(f"checkpoint manifest does not pin {name}")
    metadata = json.loads(read_bounded(checkpoint / "metadata.json", 262144))
    if str(metadata.get("reason", "")).upper() == "REGRESSION":
        raise ValueError("forensic checkpoints are ineligible for stop evidence")
    configuration = json.loads(read_bounded(checkpoint / "configuration.json", 262144))
    if configuration["training"]["inference_device"] != "cpu":
        raise ValueError("the Ants held-out evaluator requires a CPU checkpoint")

    with tempfile.TemporaryDirectory(prefix="ants-eval-", dir=scratch) as workspace:
        system = OutcomeFrontierTrainingSystem.from_checkpoint(
            checkpoint, artifact_root=workspace
        )
        levels = system.config.curriculum.levels
        level = next((item for item in levels if item.level == suite["level"]), None)
        if level is None:
            raise ValueError("suite level is absent from the checkpoint curriculum")
        seed = derive_seed(suite["seed"], "hub_ants_heldout_v1", suite["suite_id"])
        if seed == system.config.world.master_seed:
            raise ValueError("held-out and training seed namespaces collide")
        system.config = replace(
            system.config,
            world=replace(system.config.world, master_seed=seed),
            training=replace(system.config.training, evaluation_workers=1),
        )
        torch.set_num_threads(1)
        result = system.evaluate(
            suite["trials"],
            curriculum_level=level,
            record_as_latest=False,
            include_greedy=False,
        )
        outcomes = result["stochastic_episodes"]
        if len(outcomes) != suite["trials"]:
            raise ValueError("Ants did not return the full held-out cohort")
        successes = sum(bool(episode.success) for episode in outcomes)
    if read_bounded(manifest_path, 65536) != manifest_bytes:
        raise ValueError("checkpoint identity changed during evaluation")
    return successes


def publish(directory: Path, receipt: dict) -> Path:
    destination = directory / f'{receipt["dispatch_id"]}.{receipt["suite_id"]}.json'
    fd, pending = tempfile.mkstemp(prefix=".ants-receipt-", dir=directory)
    try:
        with os.fdopen(fd, "w", encoding="utf-8") as stream:
            json.dump(receipt, stream, separators=(",", ":"))
            stream.write("\n")
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(pending, destination)
    finally:
        Path(pending).unlink(missing_ok=True)
    return destination


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--checkpoint", required=True, type=Path)
    parser.add_argument("--checkpoint-sha256", required=True, help="SHA-256 of manifest.json")
    parser.add_argument("--dispatch-id", required=True)
    parser.add_argument("--suite", required=True, type=Path)
    parser.add_argument("--scratch", required=True, type=Path)
    parser.add_argument("--receipt-root", required=True, type=Path)
    args = parser.parse_args()
    try:
        if not UUID.fullmatch(args.dispatch_id) or not SHA256.fullmatch(args.checkpoint_sha256):
            raise ValueError("a Hub dispatch UUID and pinned checkpoint manifest hash are required")
        checkpoint = args.checkpoint.resolve(strict=True)
        if not checkpoint.is_dir() or not checkpoint.name.startswith("checkpoint_"):
            raise ValueError("select an immutable checkpoint directory, without a latest pointer")
        scratch = args.scratch.resolve(strict=True)
        receipt_root = args.receipt_root.resolve(strict=True)
        training_root = checkpoint.parent.parent
        for directory in (scratch, receipt_root):
            if not directory.is_dir() or directory.is_relative_to(training_root):
                raise ValueError("scratch and receipt directories must be outside the training artifacts")
        suite = read_suite(args.suite)
        successes = evaluate(checkpoint, args.checkpoint_sha256, suite, scratch)
        receipt = {
            "schema_version": 1,
            "suite_id": suite["suite_id"],
            "dispatch_id": args.dispatch_id,
            "checkpoint_sha256": args.checkpoint_sha256,
            "trials": suite["trials"],
            "successes": successes,
        }
        print(publish(receipt_root, receipt))
        return 0
    except (OSError, ValueError, RuntimeError, KeyError) as exc:
        print(f"Ants held-out evaluation failed: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
