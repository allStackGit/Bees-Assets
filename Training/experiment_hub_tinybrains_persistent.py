#!/usr/bin/env python3
"""Locally approved Hub entry point for a resumed TinyBrains mini-learning run.

The Hub owns a fresh, checkpoint-seeded job directory. Neither the original
Windows run nor the copied Halifax source run is modified by this process.
"""
import argparse
import os
from importlib.util import find_spec
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
    parser.add_argument("--device", choices=("cpu", "cuda", "dual"), default="cuda")
    parser.add_argument("--rocm-pci", action="append")
    parser.add_argument("--gpu-vram-mb", type=int)
    parser.add_argument("--remote-rollout-endpoint", action="append", default=[])
    parser.add_argument("--remote-rollout-weight", action="append", type=int, default=[])
    parser.add_argument("--remote-rollout-key-file")
    parser.add_argument("--ram-budget-mb", type=int)
    args = parser.parse_args()
    if min(args.updates, args.threads, args.live_matches, args.waves, args.actions) < 1:
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
    if args.ram_budget_mb is not None and not 128 <= args.ram_budget_mb <= 1000000:
        parser.error("The approved RAM lease must be 128–1000000 MiB.")
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
            # The native GPU width/prestaging code must measure its own
            # allocator headroom, not the full physical card's free VRAM.
            # This is an advisory safety bound in addition to PyTorch's
            # enforced per-process memory allocator limit.
            os.environ['TINYBRAINS_GPU_VRAM_BUDGET_MB'] = str(args.gpu_vram_mb)
    # Existing installations may have the distributed collector protocol but
    # not the newer conservative balancing runtime. Preserve their existing
    # launch behavior until that trainer version is installed.
    auto_balance = False
    if args.remote_rollout_endpoint and not args.remote_rollout_weight:
        auto_balance = find_spec("ants_mapoca.rollout_balance") is not None
        if not auto_balance:
            print("TinyBrains backend lacks automatic collector balancing; "
                  "using existing fixed equal collector targets.", file=sys.stderr)
    auto_env_pool = find_spec("ants_mapoca.engine_pool") is not None
    sys.argv = [
        "ants_mapoca.learning_path",
        "--config", str(spec),
        "--out", str(run),
        "--persistent",
        "--self-play-device", optimization_device,
        *(["--self-play-collection-device", collection_device] if collection_device else []),
        "--self-play-threads", str(args.threads),
        *(["--auto-env-pool", "--ram-budget-mb", str(args.ram_budget_mb)]
          if auto_env_pool and args.ram_budget_mb is not None else []),
        "--live-matches", str(args.live_matches),
        "--waves", str(args.waves),
        "--rollout-actions", str(args.actions),
        "--max-ppo-updates", str(args.updates),
        *[arg for endpoint in args.remote_rollout_endpoint
          for arg in ("--remote-rollout-endpoint", endpoint)],
        *[arg for weight in args.remote_rollout_weight
          for arg in ("--remote-rollout-weight", str(weight))],
        # Prefer sustained delivered-SPS balancing only when the operator
        # did not pin explicit immutable per-collector action weights.
        *(["--auto-rollout-weights"] if auto_balance else []),
        *(["--remote-rollout-key-file", args.remote_rollout_key_file,
           "--remote-rollout-job-id", run.name] if args.remote_rollout_endpoint else []),
    ]
    runpy.run_module("ants_mapoca.learning_path", run_name="__main__", alter_sys=True)


if __name__ == "__main__":
    main()
