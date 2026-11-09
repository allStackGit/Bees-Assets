#!/usr/bin/env python3
"""Locally approved Hub entry point for a resumed TinyBrains mini-learning run.

The Hub owns a fresh, checkpoint-seeded job directory. Neither the original
Windows run nor the copied Halifax source run is modified by this process.
"""
import argparse
import os
from pathlib import Path
import runpy
import sys


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--run", type=Path, required=True)
    parser.add_argument("--updates", type=int, required=True)
    parser.add_argument("--threads", type=int, required=True)
    parser.add_argument("--live-matches", type=int, required=True)
    parser.add_argument("--waves", type=int, required=True)
    parser.add_argument("--actions", type=int, required=True)
    parser.add_argument("--rocm-pci", required=True)
    args = parser.parse_args()
    if min(args.updates, args.threads, args.live_matches, args.waves, args.actions) < 1:
        parser.error("All training limits must be positive.")
    if args.live_matches % args.waves:
        parser.error("Live matches must divide evenly into waves.")
    run = args.run.resolve(strict=True)
    checkpoint = run / "self-play" / "latest.pt"
    spec = run / "run-spec.json"
    gate = run / "gate-passed.json"
    if not all(p.is_file() for p in (checkpoint, spec, gate)):
        raise SystemExit("Hub-seeded TinyBrains run is incomplete.")
    if (run / "STOP").exists() or (run / "self-play" / "STOP").exists():
        raise SystemExit("Existing STOP marker prevents any trainer restart.")
    import json
    import torch
    start = json.loads((run / "hub-start-update.json").read_text())["update"]
    state = torch.load(checkpoint, map_location="cpu", weights_only=False)
    if type(start) is not int or start < 1 or state.get("update") != start:
        raise SystemExit("Hub PPO starting update differs from imported checkpoint.")
    # Hub's frozen `updates` is a per-job increment, while TinyBrains
    # accepts an absolute limit across the whole persistent optimizer lineage.
    target_update = start + args.updates
    from experiment_hub_tinybrains_rocm_verify import verify_runtime_device
    verify_runtime_device(args.rocm_pci)
    sys.argv = [
        "ants_mapoca.learning_path",
        "--config", str(spec),
        "--out", str(run),
        "--persistent",
        "--self-play-device", "cuda",
        "--self-play-threads", str(args.threads),
        "--live-matches", str(args.live_matches),
        "--waves", str(args.waves),
        "--rollout-actions", str(args.actions),
        "--max-ppo-updates", str(target_update),
    ]
    runpy.run_module("ants_mapoca.learning_path", run_name="__main__", alter_sys=True)


if __name__ == "__main__":
    main()
