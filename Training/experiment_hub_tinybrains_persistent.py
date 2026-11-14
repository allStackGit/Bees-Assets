#!/usr/bin/env python3
"""Locally approved Hub entry point for a resumed TinyBrains mini-learning run.

The Hub owns a fresh, checkpoint-seeded job directory. Neither the original
Windows run nor the copied Halifax source run is modified by this process.
"""
import argparse
import json
import os
from pathlib import Path
import runpy
import sys


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--run", type=Path, required=True)
    parser.add_argument("--updates", type=int, required=True)
    parser.add_argument("--threads", type=int, required=True)
    parser.add_argument("--ram-mb", type=int)
    parser.add_argument("--live-matches", type=int, required=True)
    parser.add_argument("--waves", type=int, required=True)
    parser.add_argument("--actions", type=int, required=True)
    parser.add_argument("--hub-design-overrides-file", type=Path)
    parser.add_argument("--device", choices=("cpu", "cuda", "dual"), default="cuda")
    parser.add_argument("--rocm-pci", action="append")
    parser.add_argument("--gpu-vram-mb", type=int)
    parser.add_argument("--adaptive-local-engines", action="store_true")
    parser.add_argument("--remote-rollout-endpoint", action="append", default=[])
    parser.add_argument("--remote-rollout-weight", action="append", type=int, default=[])
    parser.add_argument("--remote-rollout-key-file")
    args = parser.parse_args()
    if min(args.updates, args.threads, args.live_matches, args.waves, args.actions) < 1 or (args.ram_mb is not None and args.ram_mb < 128):
        parser.error("All training limits must be positive.")
    if args.live_matches % args.waves:
        parser.error("Live matches must divide evenly into waves.")
    expected_count = {"cpu": 0, "cuda": 1, "dual": 2}[args.device]
    if len(args.rocm_pci or []) != expected_count:
        parser.error("The number of frozen ROCm PCI identities must match the execution mode.")
    if bool(args.remote_rollout_endpoint) != bool(args.remote_rollout_key_file):
        parser.error("Remote rollout requires locally approved worker endpoints and an HMAC key.")
    if args.remote_rollout_weight and (
            not args.remote_rollout_endpoint or
            len(args.remote_rollout_weight) != len(args.remote_rollout_endpoint) + 1 or
            any(not 1 <= w <= 1024 for w in args.remote_rollout_weight)):
        parser.error("Fixed rollout weights require one positive weight for the learner and each remote worker.")
    if args.gpu_vram_mb is not None and (args.device != "cuda" or args.gpu_vram_mb < 1):
        parser.error("A positive GPU memory ceiling requires the ROCm device.")
    run = args.run.resolve(strict=True)
    design_overrides = None
    if args.hub_design_overrides_file is not None:
        override_file = run / "hub-design-overrides.json"
        if (args.hub_design_overrides_file.resolve(strict=True) != override_file
                or override_file.is_symlink() or not override_file.is_file()
                or override_file.stat().st_size > 16384):
            parser.error("Frozen Design overrides must be the job-local manifest.")
        design_overrides = json.loads(override_file.read_text(encoding="utf-8"))
        if not isinstance(design_overrides, dict) or len(design_overrides) > 128:
            parser.error("Invalid frozen project Design overrides.")
    checkpoint = run / "self-play" / "latest.pt"
    spec = run / "run-spec.json"
    gate = run / "gate-passed.json"
    if not all(p.is_file() for p in (checkpoint, spec, gate)):
        raise SystemExit("Hub-seeded TinyBrains run is incomplete.")
    if (run / "STOP").exists() or (run / "self-play" / "STOP").exists():
        raise SystemExit("Existing STOP marker prevents any trainer restart.")
    import torch
    start = json.loads((run / "hub-start-update.json").read_text())["update"]
    state = torch.load(checkpoint, map_location="cpu", weights_only=False)
    if type(start) is not int or start < 1 or state.get("update") != start:
        raise SystemExit("Hub PPO starting update differs from imported checkpoint.")
    # Both the Hub job and the Linux TinyBrains --max-ppo-updates option
    # count additional updates from the verified seed checkpoint.
    optimization_device = args.device
    collection_device = None
    if expected_count:
        from experiment_hub_tinybrains_rocm_verify import verify_runtime_devices
        actual_pcis = verify_runtime_devices(args.rocm_pci)
        if args.device == "dual":
            # The lease fixes physical PCI identities, not HIP ordinal order.
            # Use its first GPU for PPO optimization and second for rollouts.
            optimization_device = f"cuda:{actual_pcis.index(args.rocm_pci[0])}"
            collection_device = f"cuda:{actual_pcis.index(args.rocm_pci[1])}"
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
        *(["--adaptive-local-engines"] if args.adaptive_local_engines else []),
        "--self-play-device", optimization_device,
        *(["--self-play-collection-device", collection_device] if collection_device else []),
        "--self-play-threads", str(args.threads),
        *(["--self-play-ram-mb", str(args.ram_mb)] if args.ram_mb else []),
        "--live-matches", str(args.live_matches),
        "--waves", str(args.waves),
        "--rollout-actions", str(args.actions),
        "--max-ppo-updates", str(args.updates),
        *(["--hub-design-overrides", json.dumps(design_overrides, separators=(",", ":"))]
          if design_overrides is not None else []),
        *[arg for endpoint in args.remote_rollout_endpoint
          for arg in ("--remote-rollout-endpoint", endpoint)],
        *[arg for weight in args.remote_rollout_weight
          for arg in ("--remote-rollout-weight", str(weight))],
        *(["--remote-rollout-key-file", args.remote_rollout_key_file,
           "--remote-rollout-job-id", run.name] if args.remote_rollout_endpoint else []),
    ]
    runpy.run_module("ants_mapoca.learning_path", run_name="__main__", alter_sys=True)


if __name__ == "__main__":
    main()
