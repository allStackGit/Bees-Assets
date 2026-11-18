"""Explicit local GPU placement for native rollout *processes*.

The simulator still owns physics and the Python resident policy worker owns
inference. Each native process inherits exactly one selected GPU visibility
mask; the optimizer is independently placed by its own --device setting.
No manifest, checkpoint or physical game configuration is rewritten.
"""

import json
import os
import re
from pathlib import Path


GPU_MASK_ENVIRONMENTS = (
    "CUDA_VISIBLE_DEVICES", "ROCR_VISIBLE_DEVICES", "HIP_VISIBLE_DEVICES",
)


def rocm_visible_gpu_pci() -> str | None:
    """Inspect HIP's *actually loaded* cuda:0 physical PCI identity.

    Only used for post-forward telemetry. A dual Hub lease is never accepted
    merely because two GPU masks were requested; each actor must report a
    distinct HIP-mapped physical device matching the supervisor's lease.
    """
    if os.name != "posix":
        return None
    import ctypes

    try:
        with open("/proc/self/maps", encoding="utf-8") as maps:
            loaded = {
                fields[-1] for line in maps
                if (fields := line.rstrip().split(maxsplit=5)) and len(fields) == 6
                and re.fullmatch(r"libamdhip64\.so(?:\.[0-9]+)*",
                                 os.path.basename(fields[-1]))
                and os.path.isabs(fields[-1])
            }
        if len(loaded) != 1:
            return None
        hip = ctypes.CDLL(loaded.pop())
        get_pci = hip.hipDeviceGetPCIBusId
        get_pci.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.c_int]
        get_pci.restype = ctypes.c_int
        address = ctypes.create_string_buffer(64)
        if get_pci(address, len(address), 0) != 0:
            return None
        value = address.value.decode("ascii").lower()
        return value if re.fullmatch(r"[0-9a-f]{4}:[0-9a-f]{2}:[0-9a-f]{2}\.[0-7]", value) else None
    except (OSError, AttributeError, UnicodeDecodeError):
        return None


def parse_rollout_gpus(value: str | None) -> tuple[int, ...] | None:
    if value is None:
        return None
    if not re.fullmatch(r"[0-9]+(?:,[0-9]+)*", value, flags=re.ASCII):
        raise ValueError("--rollout-gpus must be comma-separated physical GPU ordinals")
    numbers = tuple(int(part) for part in value.split(","))
    if not 1 <= len(numbers) <= 256 or any(number > 255 for number in numbers):
        raise ValueError("--rollout-gpus supports 1..256 GPU ordinals in 0..255")
    if len(set(numbers)) != len(numbers):
        raise ValueError("--rollout-gpus must not repeat a GPU; workers share GPUs round-robin")
    return numbers


def validate_gpu_allocation(
    gpu_ids: tuple[int, ...] | None,
    mask_environment: str | None,
    worker_count: int,
    manifest_path: Path,
) -> str | None:
    if gpu_ids is None:
        if mask_environment is not None:
            raise ValueError("--rollout-gpu-mask requires --rollout-gpus")
        return None
    mask = mask_environment or "CUDA_VISIBLE_DEVICES"
    if mask not in GPU_MASK_ENVIRONMENTS:
        raise ValueError("unsupported GPU visibility environment")
    if len(gpu_ids) > worker_count:
        raise ValueError("more rollout GPUs than processes would leave allocated GPUs unused")
    # Never widen an existing machine/container/HUB visibility allocation.
    for name in GPU_MASK_ENVIRONMENTS:
        inherited = os.environ.get(name)
        if inherited is None:
            continue
        if name != mask:
            raise ValueError(
                f"{name} is already set; select the matching --rollout-gpu-mask "
                "rather than combining nested visibility filters"
            )
        if not re.fullmatch(r"[0-9]+(?:,[0-9]+)*", inherited, flags=re.ASCII):
            raise ValueError(
                f"{name} is not a physical-ordinal mask; do not override an "
                "externally managed GPU allocation"
            )
        allowed = {int(part) for part in inherited.split(",")}
        if any(gpu not in allowed for gpu in gpu_ids):
            raise ValueError("--rollout-gpus exceeds the process's inherited GPU allocation")

    # A GPU mask alone cannot accelerate an ONNX model configured for CPU.
    # Torch/ML-Agents actors may be moved to the single visible device by
    # the resident worker's explicit, opt-in inference override.
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    models = manifest.get("models") if isinstance(manifest, dict) else None
    if not isinstance(models, list) or not models:
        raise ValueError("rollout GPU allocation requires a valid model manifest")
    enabled = False
    for model in models:
        if not isinstance(model, dict):
            raise ValueError("invalid native model slot for GPU allocation")
        backend = model.get("backend")
        if backend in ("mlagents", "torchscript"):
            enabled = True
        elif backend == "onnx":
            providers = model.get("providers", ["CPUExecutionProvider"])
            primary = providers[0] if isinstance(providers, list) and providers else None
            if primary in ("ROCMExecutionProvider", "MIGraphXExecutionProvider"):
                if mask == "CUDA_VISIBLE_DEVICES":
                    raise ValueError(
                        "ROCm ONNX inference requires --rollout-gpu-mask "
                        "ROCR_VISIBLE_DEVICES or HIP_VISIBLE_DEVICES"
                    )
                enabled = True
            elif primary == "CUDAExecutionProvider":
                if mask != "CUDA_VISIBLE_DEVICES":
                    raise ValueError(
                        "CUDA ONNX inference requires --rollout-gpu-mask CUDA_VISIBLE_DEVICES"
                    )
                enabled = True
    if not enabled:
        raise ValueError(
            "--rollout-gpus requires a PyTorch/ML-Agents model or an ONNX "
            "model configured with a GPU execution provider"
        )
    return mask


def validate_leased_gpu_inference(manifest_path: Path) -> None:
    """Check a supervisor-isolated single-GPU collector before any rollout.

    GPU lease ownership and device isolation belong to the OS supervisor.
    A standalone script cannot manufacture that proof. The worker still
    loads all pinned artifacts and refuses an unavailable CUDA/ROCm device.
    """
    # Linux Hub can enforce an exclusive PCI-bound device cgroup without
    # exporting any visibility variable. Its pre-start device verifier is
    # authoritative; a missing CUDA/ROCR mask does not mean an unleased GPU.
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    models = manifest.get("models") if isinstance(manifest, dict) else None
    if not isinstance(models, list) or not models:
        raise ValueError("leased GPU inference requires native model slots")
    usable = False
    for model in models:
        if not isinstance(model, dict):
            raise ValueError("invalid native model slot for leased GPU inference")
        if model.get("backend") in ("mlagents", "torchscript"):
            usable = True
        elif model.get("backend") == "onnx":
            providers = model.get("providers", ["CPUExecutionProvider"])
            if isinstance(providers, list) and providers and providers[0] in (
                "CUDAExecutionProvider", "ROCMExecutionProvider",
                "MIGraphXExecutionProvider",
            ):
                usable = True
    if not usable:
        raise ValueError("GPU lease cannot accelerate a CPU-only ONNX model manifest")


def rollout_worker_environment(
    gpu_ids: tuple[int, ...] | None,
    mask: str | None,
    index: int,
    leased_gpu_inference: bool = False,
    leased_gpu_count: int = 1,
) -> dict[str, str] | None:
    env = os.environ.copy()
    # Never inherit an unrecorded developer override into a measured
    # training round. Only the frozen allocation selects model placement.
    env.pop("BEES_NATIVE_INFERENCE_DEVICE_OVERRIDE", None)
    if gpu_ids is None and not leased_gpu_inference:
        return env
    if gpu_ids is not None:
        env[mask] = str(gpu_ids[index % len(gpu_ids)])
    elif leased_gpu_count == 2:
        # The Hub supervisor has already verified the EXACT two PCI-bound
        # render devices through HIP and restricted the service to those
        # nodes. Select different devices for successive resident actors,
        # without changing the pinned model manifest or allocating another
        # GPU. ROCm reindexes each child's sole visible GPU to cuda:0.
        for key in GPU_MASK_ENVIRONMENTS:
            env.pop(key, None)
        env.pop("GPU_DEVICE_ORDINAL", None)
        env["ROCR_VISIBLE_DEVICES"] = str(index % leased_gpu_count)
    # ROCm PyTorch intentionally uses the CUDA device API as well.
    # The OS supervisor, or the explicit numeric mask, isolates one GPU.
    # No physical GPU ordinal is guessed from a Hub PCI/UUID lease.
    env["BEES_NATIVE_INFERENCE_DEVICE_OVERRIDE"] = "cuda:0"
    return env
