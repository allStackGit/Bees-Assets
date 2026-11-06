#!/usr/bin/env python3
"""Verify one leased ROCm device in the Ants training runtime."""

from __future__ import annotations

import argparse
import ctypes
import json
import os
from pathlib import Path
import re
import sys


PCI = re.compile(r"[0-9a-f]{4}:[0-9a-f]{2}:[0-9a-f]{2}\.[0-7]")
HIP_LIBRARY = re.compile(r"libamdhip64\.so(?:\.[0-9]+)*")


def loaded_hip_library():
    libraries = set()
    with Path("/proc/self/maps").open(encoding="utf-8") as mappings:
        for row in mappings:
            fields = row.split(maxsplit=5)
            if len(fields) == 6:
                filename = Path(fields[5].strip())
                if filename.is_absolute() and HIP_LIBRARY.fullmatch(filename.name):
                    libraries.add(filename.resolve(strict=True))
    if len(libraries) != 1:
        raise RuntimeError("could not identify exactly one HIP library loaded by PyTorch")
    return ctypes.CDLL(str(libraries.pop()), mode=os.RTLD_NOLOAD | os.RTLD_NOW)


def hip_pci_address(library) -> str:
    count_fn = library.hipGetDeviceCount
    count_fn.argtypes = [ctypes.POINTER(ctypes.c_int)]
    count_fn.restype = ctypes.c_int
    count = ctypes.c_int()
    if count_fn(ctypes.byref(count)) != 0 or count.value != 1:
        raise RuntimeError("HIP must expose exactly one compute device")
    address_fn = library.hipDeviceGetPCIBusId
    address_fn.argtypes = [ctypes.POINTER(ctypes.c_char), ctypes.c_int, ctypes.c_int]
    address_fn.restype = ctypes.c_int
    address = ctypes.create_string_buffer(32)
    if address_fn(address, len(address), 0) != 0:
        raise RuntimeError("HIP could not resolve the visible device's PCI address")
    value = address.value.decode("ascii").lower()
    if not PCI.fullmatch(value):
        raise RuntimeError("HIP returned an invalid PCI address")
    return value


def verify(config_path: str, report_path: str, expected: str) -> str:
    # Match the checkout/module search order of the adapter's python -m antsim.
    sys.path.insert(0, os.getcwd())
    import torch

    if not torch.version.hip:
        raise RuntimeError("the Ants Python environment is not a ROCm PyTorch build")
    from antsim.config import load_config
    from antsim.training_authorization import validate_training_authorization

    config = load_config(config_path)
    if torch.cuda.device_count() != 1:
        raise RuntimeError("ROCm PyTorch must expose exactly one compute device")
    torch.cuda.init()
    actual = hip_pci_address(loaded_hip_library())
    if actual != expected:
        raise RuntimeError(f"ROCm selected {actual}; the Hub leased {expected}")
    report = validate_training_authorization(config, report_path)
    runtime = report["runtime_device_identity"]
    if "cuda" not in {
        runtime["ppo_training_device_resolved"], runtime["policy_inference_device_resolved"]
    }:
        raise RuntimeError("the qualified Ants configuration does not use the GPU")
    value = torch.ones(1, device="cuda:0").add_(1)
    torch.cuda.synchronize(0)
    if value.item() != 2:
        raise RuntimeError("ROCm workload sanity check failed")
    return actual


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("config")
    parser.add_argument("qualification_report")
    parser.add_argument("pci_address")
    args = parser.parse_args()
    try:
        if not PCI.fullmatch(args.pci_address):
            raise ValueError("the leased GPU needs a full PCI address")
        actual = verify(args.config, args.qualification_report, args.pci_address)
        print(json.dumps({"pci_addresses": [actual]}))
        return 0
    except (ImportError, OSError, RuntimeError, TypeError, ValueError, KeyError) as exc:
        print(f"Ants ROCm verification failed: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
