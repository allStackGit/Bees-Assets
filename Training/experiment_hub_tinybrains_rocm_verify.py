"""Fixed, local TinyBrains ROCm pre-start probe.

Linux systemd applies the Hub's selected render-node device filter before
running this probe. No request data can select arbitrary Python commands.
"""
import os
import re
import sys


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
    import torch

    if not torch.version.hip or not torch.cuda.is_available():
        raise SystemExit("PyTorch ROCm is unavailable in the approved Python environment.")
    # Conservative: when HIP still enumerates excluded devices, refuse to
    # train rather than guess which ordinal the policy will use.
    if torch.cuda.device_count() != 1:
        raise SystemExit("ROCm must expose exactly the single selected GPU.")
    sample = torch.ones((16,), device="cuda")
    if float(sample.sum().item()) != 16.0:
        raise SystemExit("Selected ROCm device failed its compute probe.")
    torch.cuda.synchronize()


if __name__ == "__main__":
    main()
