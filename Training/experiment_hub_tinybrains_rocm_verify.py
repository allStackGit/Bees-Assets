"""Fixed, local TinyBrains ROCm pre-start probe.

Linux systemd applies the Hub's selected render-node device filter before
running this probe. No request data can select arbitrary Python commands.
"""
import ctypes
import json
import os
import re
import sys


PCI = re.compile(r"[0-9a-f]{4}:[0-9a-f]{2}:[0-9a-f]{2}\.[0-7]")


def runtime_pci_bus_id():
    # Unlike a sysfs inventory, HIP identifies the GPU actually mapped to
    # device ordinal zero after the runtime's visibility rules are applied.
    # A correct-looking DRM node plus device_count()==1 is not sufficient
    # evidence of the selected policy's physical PCI identity.
    try:
        hip = ctypes.CDLL("libamdhip64.so")
        get_pci = hip.hipDeviceGetPCIBusId
    except (OSError, AttributeError) as exc:
        raise SystemExit("HIP runtime PCI identity API is unavailable.") from exc
    get_pci.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.c_int]
    get_pci.restype = ctypes.c_int
    address = ctypes.create_string_buffer(64)
    if get_pci(address, len(address), 0) != 0:
        raise SystemExit("HIP could not report the selected GPU's PCI identity.")
    try:
        result = address.value.decode("ascii").lower()
    except UnicodeDecodeError as exc:
        raise SystemExit("HIP returned an invalid GPU PCI identity.") from exc
    if PCI.fullmatch(result) is None:
        raise SystemExit("HIP returned an unrecognized GPU PCI address.")
    return result


def verify_runtime_device(expected):
    # Reuse this exact runtime proof in ExecStartPre and the final training
    # Python process. A successful pre-start child alone is insufficient
    # evidence that the separately launched trainer selected the same GPU.
    if PCI.fullmatch(expected) is None:
        raise SystemExit("Invalid leased ROCm PCI identity.")
    import torch

    if not torch.version.hip or not torch.cuda.is_available():
        raise SystemExit("PyTorch ROCm is unavailable in the approved Python environment.")
    if torch.cuda.device_count() != 1:
        raise SystemExit("ROCm must expose exactly the single selected GPU.")
    observed = runtime_pci_bus_id()
    if observed != expected:
        raise SystemExit("HIP selected GPU PCI identity differs from the Hub lease.")
    sample = torch.ones((16,), device="cuda")
    if float(sample.sum().item()) != 16.0:
        raise SystemExit("Selected ROCm device failed its compute probe.")
    torch.cuda.synchronize()
    return observed


def main():
    if len(sys.argv) != 2 or re.fullmatch(
        r"[0-9a-f]{4}:[0-9a-f]{2}:[0-9a-f]{2}\.[0-7]", sys.argv[1]
    ) is None:
        raise SystemExit("Expected exactly one canonical GPU PCI address.")
    expected = sys.argv[1]
    found = False
    for name in os.listdir("/sys/class/drm"):
        if re.fullmatch(r"renderD[0-9]+", name) is None:
            continue
        device = os.path.realpath("/sys/class/drm/" + name + "/device")
        if os.path.basename(device).lower() == expected:
            with open("/sys/class/drm/" + name + "/device/vendor", encoding="ascii") as stream:
                if stream.read().strip().lower() != "0x1002":
                    raise SystemExit("Selected PCI device is not AMD.")
            found = True
            break
    if not found:
        raise SystemExit("Selected physical AMD GPU disappeared.")
    observed = verify_runtime_device(expected)
    # The native Linux GPU launch checker requires a machine-readable
    # attestation for the exact PCI identity reported by the HIP runtime.
    print(json.dumps({"pci_addresses": [observed]}, separators=(",", ":")))


if __name__ == "__main__":
    main()
