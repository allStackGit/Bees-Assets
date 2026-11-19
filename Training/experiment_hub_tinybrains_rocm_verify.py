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


def runtime_pci_bus_id(ordinal=0):
    # Unlike a sysfs inventory, HIP identifies the GPU actually mapped to
    # device ordinal zero after the runtime's visibility rules are applied.
    # A correct-looking DRM node plus device_count()==1 is not sufficient
    # evidence of the selected policy's physical PCI identity.
    # The ROCm PyTorch wheel can bundle a versioned HIP library without an
    # unversioned system linker name. Load the exact library already mapped
    # into this process by PyTorch, rather than guessing an installation path.
    with open("/proc/self/maps", encoding="utf-8") as mappings:
        loaded = {
            fields[-1] for line in mappings
            if (fields := line.rstrip().split(maxsplit=5)) and len(fields) == 6
            and re.fullmatch(r"libamdhip64\.so(?:\.[0-9]+)*",
                             os.path.basename(fields[-1]))
            and os.path.isabs(fields[-1])
        }
    if len(loaded) != 1:
        raise SystemExit("Expected one loaded HIP shared library for device identity.")
    try:
        hip = ctypes.CDLL(loaded.pop())
        get_pci = hip.hipDeviceGetPCIBusId
    except (OSError, AttributeError) as exc:
        raise SystemExit("HIP runtime PCI identity API is unavailable.") from exc
    get_pci.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.c_int]
    get_pci.restype = ctypes.c_int
    address = ctypes.create_string_buffer(64)
    if get_pci(address, len(address), ordinal) != 0:
        raise SystemExit("HIP could not report the selected GPU's PCI identity.")
    try:
        result = address.value.decode("ascii").lower()
    except UnicodeDecodeError as exc:
        raise SystemExit("HIP returned an invalid GPU PCI identity.") from exc
    if PCI.fullmatch(result) is None:
        raise SystemExit("HIP returned an unrecognized GPU PCI address.")
    return result


def verify_runtime_devices(expected):
    # Runtime ordinal order need not match the lease's logical ID order.
    # Every visible GPU must resolve to exactly one frozen physical PCI ID.
    if not isinstance(expected, (list, tuple)) or not 1 <= len(expected) <= 8 or (
            len(set(expected)) != len(expected) or any(PCI.fullmatch(pci) is None for pci in expected)):
        raise SystemExit("Invalid fixed ROCm GPU lease.")
    import torch

    if not torch.version.hip or not torch.cuda.is_available():
        raise SystemExit("PyTorch ROCm is unavailable in the approved Python environment.")
    if torch.cuda.device_count() != len(expected):
        raise SystemExit("ROCm visibility does not match the approved GPU count.")
    torch.cuda.init()
    observed = [runtime_pci_bus_id(ordinal) for ordinal in range(len(expected))]
    if len(set(observed)) != len(observed) or set(observed) != set(expected):
        raise SystemExit("HIP visible GPU PCI identities differ from the Hub lease.")
    for ordinal in range(len(expected)):
        sample = torch.ones((16,), device=f"cuda:{ordinal}")
        if float(sample.sum().item()) != 16.0:
            raise SystemExit("A leased ROCm GPU failed its compute probe.")
        torch.cuda.synchronize(ordinal)
    return observed


def verify_runtime_device(expected):
    return verify_runtime_devices([expected])[0]


def main():
    expected = sys.argv[1:]
    if not 1 <= len(expected) <= 8 or any(PCI.fullmatch(pci) is None for pci in expected):
        raise SystemExit("Expected one to eight canonical GPU PCI addresses.")
    for address in expected:
        found = False
        for name in os.listdir("/sys/class/drm"):
            if re.fullmatch(r"renderD[0-9]+", name) is None:
                continue
            device = os.path.realpath("/sys/class/drm/" + name + "/device")
            if os.path.basename(device).lower() == address:
                with open("/sys/class/drm/" + name + "/device/vendor", encoding="ascii") as stream:
                    if stream.read().strip().lower() != "0x1002":
                        raise SystemExit("Leased PCI device is not AMD.")
                found = True
                break
        if not found:
            raise SystemExit("A leased physical AMD GPU disappeared.")
    observed = verify_runtime_devices(expected)
    # A separate field preserves actual HIP ordinal order; the first field
    # attests the lease's frozen set, not a guessed device ordinal.
    print(json.dumps({"pci_addresses": expected, "runtime_pci_ordinals": observed},
                     separators=(",", ":")))


if __name__ == "__main__":
    main()
