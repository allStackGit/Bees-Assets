"""Bees launcher for ML-Agents 1.1.0 training.

ML-Agents 1.1.0 loads PyTorch checkpoints without a map_location. A checkpoint
saved while CUDA was active can therefore fail to resume on a CPU-only run
before ML-Agents has a chance to restore the policy, optimizer, or running
normalization state. This launcher keeps normal ML-Agents behavior but remaps
all torch.load() checkpoint tensors to the device selected by --torch-device.

It also accepts --bees-torch-threads N (or --bees-torch-threads=N) so small
policy-inference workloads can benchmark PyTorch intra-op thread counts without
patching the virtual environment. All other arguments are passed unchanged to
mlagents-learn.
"""

from __future__ import annotations

import sys
from typing import List, Optional, Sequence, Tuple


EXPECTED_MLAGENTS_VERSION = "1.1.0"
THREAD_FLAG = "--bees-torch-threads"


def _parse_positive_int(value: str, flag: str) -> int:
    try:
        parsed = int(value)
    except ValueError as exc:
        raise SystemExit(f"{flag} requires a positive whole number; got {value!r}.") from exc
    if parsed <= 0:
        raise SystemExit(f"{flag} requires a positive whole number; got {value!r}.")
    return parsed


def _extract_bees_options(argv: Sequence[str]) -> Tuple[List[str], Optional[int]]:
    trainer_args: List[str] = []
    torch_threads: Optional[int] = None
    index = 0

    while index < len(argv):
        argument = argv[index]
        if argument == THREAD_FLAG:
            if index + 1 >= len(argv) or argv[index + 1].startswith("--"):
                raise SystemExit(f"{THREAD_FLAG} requires a value.")
            torch_threads = _parse_positive_int(argv[index + 1], THREAD_FLAG)
            index += 2
            continue

        prefix = THREAD_FLAG + "="
        if argument.startswith(prefix):
            value = argument[len(prefix) :]
            if not value:
                raise SystemExit(f"{THREAD_FLAG} requires a value.")
            torch_threads = _parse_positive_int(value, THREAD_FLAG)
            index += 1
            continue

        trainer_args.append(argument)
        index += 1

    return trainer_args, torch_threads


def main() -> None:
    trainer_args, torch_threads = _extract_bees_options(sys.argv[1:])

    import mlagents.trainers
    from mlagents import torch_utils
    from mlagents.trainers import learn

    actual_version = mlagents.trainers.__version__
    if actual_version != EXPECTED_MLAGENTS_VERSION:
        raise RuntimeError(
            "Training/bees_mlagents_learn.py patches ML-Agents checkpoint loading "
            f"for version {EXPECTED_MLAGENTS_VERSION}, but version {actual_version} is installed. "
            "Verify the newer loader before changing this version guard."
        )

    if torch_threads is not None:
        torch_utils.torch.set_num_threads(torch_threads)
        print(f"[Bees RL] PyTorch intra-op threads: {torch_threads}")

    original_torch_load = torch_utils.torch.load

    def device_safe_torch_load(*args, **kwargs):
        # learn.run_training() selects --torch-device before TorchModelSaver loads
        # checkpoints, so resolve default_device() at load time rather than here.
        kwargs.setdefault("map_location", torch_utils.default_device())
        return original_torch_load(*args, **kwargs)

    previous_argv = sys.argv
    torch_utils.torch.load = device_safe_torch_load
    sys.argv = [previous_argv[0], *trainer_args]
    try:
        learn.main()
    finally:
        sys.argv = previous_argv
        torch_utils.torch.load = original_torch_load


if __name__ == "__main__":
    main()
