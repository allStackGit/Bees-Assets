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
    parser.add_argument("--device", choices=("cpu", "cuda", "dual", "multi"), default="cuda")
    parser.add_argument("--rocm-pci", action="append")
    parser.add_argument("--cuda-uuid", action="append")
    parser.add_argument("--cuda-pci", action="append")
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
    rocm_pcis = args.rocm_pci or []
    cuda_uuids = args.cuda_uuid or []
    cuda_pcis = args.cuda_pci or []
    if rocm_pcis and (cuda_uuids or cuda_pcis):
        parser.error("ROCm and CUDA device leases are mutually exclusive.")
    count = len(rocm_pcis) if rocm_pcis else len(cuda_uuids)
    expected_count = count if args.device == "multi" else {
        "cpu": 0, "cuda": 1, "dual": 2}[args.device]
    if (count != expected_count or
            (args.device == "multi" and not 3 <= count <= 8) or
            (cuda_uuids and (os.name != "nt" or
                len(cuda_pcis) != count or
                len(set(cuda_uuids)) != count or
                len(set(cuda_pcis)) != count)) or
            (cuda_pcis and not cuda_uuids) or
            (rocm_pcis and os.name == "nt") or
            (args.device != "cpu" and not (rocm_pcis or cuda_uuids))):
        parser.error("The pinned GPU lease must match the requested CPU, single, dual or 3–8 GPU mode.")
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
        parser.error("A positive GPU memory ceiling requires one leased CUDA/ROCm device.")
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
    local_gpu_collectors = []
    if expected_count:
        if cuda_uuids:
            # Windows native Job Object owner selects these immutable UUIDs
            # before starting Python. Verify the exact order and device count
            # before any checkpoint-backed training or model allocation.
            if os.environ.get("CUDA_VISIBLE_DEVICES") != ",".join(cuda_uuids):
                raise SystemExit("Windows CUDA UUID visibility differs from the Hub lease.")
            if not torch.version.cuda or not torch.cuda.is_available() or (
                    torch.cuda.device_count() != count):
                raise SystemExit("The Hub-leased CUDA device set is unavailable.")
            for ordinal in range(count):
                if torch.cuda.get_device_properties(ordinal).total_memory <= 0:
                    raise SystemExit("Invalid leased CUDA GPU memory capacity.")
            actual_pcis = cuda_pcis
        else:
            from experiment_hub_tinybrains_rocm_verify import verify_runtime_devices
            actual_pcis = verify_runtime_devices(rocm_pcis)
        # Exact physical lease identity follows CUDA ordinals. HIP ordinals
        # may differ from approved PCI order; Windows UUID filtering preserves
        # the selected UUID order, which is authenticated by the owner.
        os.environ["TINYBRAINS_GPU_PHYSICAL_PCI_IDS"] = ",".join(actual_pcis)
        lease_pcis = cuda_pcis if cuda_uuids else rocm_pcis
        os.environ["TINYBRAINS_GPU_LEASE_ORDINALS"] = ",".join(
            str(lease_pcis.index(pci)) for pci in actual_pcis)
        if args.device in ("dual", "multi"):
            if find_spec("ants_mapoca.gpu_roles") is not None:
                os.environ["TINYBRAINS_AUTO_GPU_ROLES"] = "1"
            optimization_device = f"cuda:{actual_pcis.index(lease_pcis[0])}"
            collection_device = f"cuda:{actual_pcis.index(lease_pcis[1])}"
            local_gpu_collectors = [
                f"cuda:{actual_pcis.index(pci)}" for pci in lease_pcis[2:]
            ]
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
    if (args.remote_rollout_endpoint or local_gpu_collectors) and not args.remote_rollout_weight:
        auto_balance = find_spec("ants_mapoca.rollout_balance") is not None
        if not auto_balance:
            print("TinyBrains backend lacks automatic collector balancing; "
                  "using existing fixed equal collector targets.", file=sys.stderr)
    auto_env_pool = find_spec("ants_mapoca.engine_pool") is not None
    if args.device == "multi" and (not auto_env_pool or
            find_spec("ants_mapoca.local_gpu_rollout") is None or
            args.ram_budget_mb is None):
        raise SystemExit("Multi-GPU TinyBrains requires persistent native pools and a fixed RAM lease.")
    sys.argv = [
        "ants_mapoca.learning_path",
        "--config", str(spec),
        "--out", str(run),
        "--persistent",
        "--self-play-device", optimization_device,
        *(["--self-play-collection-device", collection_device] if collection_device else []),
        *[arg for device in local_gpu_collectors
          for arg in ("--self-play-local-gpu-collector", device)],
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
