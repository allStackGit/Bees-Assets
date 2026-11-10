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
    parser.add_argument("--device", choices=("cpu", "cuda"), default="cuda")
    parser.add_argument("--rocm-pci")
    parser.add_argument("--gpu-vram-mb", type=int)
    args = parser.parse_args()
    if min(args.updates, args.threads, args.live_matches, args.waves, args.actions) < 1:
        parser.error("All training limits must be positive.")
    if args.live_matches % args.waves:
        parser.error("Live matches must divide evenly into waves.")
    if (args.device == "cuda") != bool(args.rocm_pci):
        parser.error("ROCm PCI identity is required for GPU and forbidden for CPU.")
    if args.gpu_vram_mb is not None and (args.device != "cuda" or args.gpu_vram_mb < 1):
        parser.error("A positive GPU memory ceiling requires the ROCm device.")
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
    # Both the Hub job and the Linux TinyBrains --max-ppo-updates option
    # count additional updates from the verified seed checkpoint.
    if args.device == "cuda":
        from experiment_hub_tinybrains_rocm_verify import verify_runtime_device
        verify_runtime_device(args.rocm_pci)
        if args.gpu_vram_mb is not None:
            # Cooperative allocator limit, not physical GPU memory isolation.
            total = torch.cuda.get_device_properties(0).total_memory
            fraction = args.gpu_vram_mb * 1048576 / total
            if not 0 < fraction <= 1:
                raise SystemExit("Shared GPU memory request exceeds the device capacity.")
            torch.cuda.set_per_process_memory_fraction(float(fraction), 0)
    sys.argv = [
        "ants_mapoca.learning_path",
        "--config", str(spec),
        "--out", str(run),
        "--persistent",
        "--self-play-device", args.device,
        "--self-play-threads", str(args.threads),
        "--live-matches", str(args.live_matches),
        "--waves", str(args.waves),
        "--rollout-actions", str(args.actions),
        "--max-ppo-updates", str(args.updates),
    ]
    runpy.run_module("ants_mapoca.learning_path", run_name="__main__", alter_sys=True)


if __name__ == "__main__":
    main()
