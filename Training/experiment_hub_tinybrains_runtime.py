#!/usr/bin/env python3
"""Select a locally installed TinyBrains engine before importing the trainer."""

import os
from pathlib import Path
import runpy
import subprocess
import sys


def configure_engine(engine):
    if engine is None:
        if os.name != "nt":
            raise ValueError("Set --engine-path to the installed native TinyBrains executable.")
        return
    if not engine.is_absolute():
        raise ValueError("TinyBrains engine path must be absolute.")
    engine = engine.resolve(strict=True)
    if not engine.is_file() or not os.access(engine, os.X_OK):
        raise ValueError("TinyBrains engine is not executable.")
    try:
        version = subprocess.check_output([str(engine), "--version"], text=True, timeout=5)
    except subprocess.SubprocessError as error:
        raise ValueError("TinyBrains engine version probe failed.") from error
    if version.strip() != "tinybrains 0.5.0":
        raise ValueError("The installed TinyBrains engine did not report supported version 0.5.0.")
    os.environ["TINYBRAINS"] = str(engine)
    from ants_mapoca import runtime

    # The upstream trainer imports CLI directly in its environment module.
    # Set the local executable before importing that module or the CLI.
    runtime.CLI = engine


def verify_cuda(uuid, requested_vram_mb=None):
    # The parent Windows owner pinned CUDA_VISIBLE_DEVICES to this physical
    # GPU UUID before creating the suspended job-owned child.
    if os.environ.get("CUDA_VISIBLE_DEVICES") != uuid:
        raise RuntimeError("CUDA device identity changed before trainer launch.")
    import torch

    if not torch.version.cuda or not torch.cuda.is_available() or torch.cuda.device_count() != 1:
        raise RuntimeError("One usable CUDA device is required.")
    if requested_vram_mb is not None:
        total_bytes = torch.cuda.get_device_properties(0).total_memory
        quota_bytes = requested_vram_mb * 1048576
        if not isinstance(total_bytes, int) or not 0 < quota_bytes <= total_bytes:
            raise RuntimeError("Shared PyTorch VRAM budget exceeds the CUDA device.")
        # PyTorch caching-allocator ceiling only: other CUDA users and
        # non-PyTorch allocations are outside this cooperative contract.
        torch.cuda.set_per_process_memory_fraction(quota_bytes / total_bytes, 0)
    probe = torch.ones((16,), device="cuda")
    if float(probe.sum().item()) != 16.0:
        raise RuntimeError("CUDA compute preflight failed.")
    torch.cuda.synchronize()


if __name__ == "__main__":
    engine, stderr = sys.argv[1:3]
    if stderr != "-":
        sys.stdout = open(os.devnull, "w", encoding="utf-8")
        sys.stderr = open(stderr, "x", encoding="utf-8", buffering=1)
    args = sys.argv[3:]
    if args[:1] == ["--hub-cuda"]:
        if len(args) < 3:
            raise RuntimeError("Missing pinned CUDA UUID or training command.")
        uuid = args[1]
        args = args[2:]
        vram_mb = None
        if args[:1] == ["--hub-gpu-vram-mb"]:
            if len(args) < 3 or not args[1].isdecimal() or not 1 <= int(args[1]) <= 1000000:
                raise RuntimeError("Invalid pinned PyTorch VRAM ceiling.")
            vram_mb = int(args[1])
            args = args[2:]
        verify_cuda(uuid, vram_mb)
    configure_engine(Path(engine))
    sys.argv = [sys.argv[0], *args]
    runpy.run_module("ants_mapoca.cli", run_name="__main__", alter_sys=True)
