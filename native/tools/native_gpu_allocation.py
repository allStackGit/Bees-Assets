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


def rollout_worker_environment(
    gpu_ids: tuple[int, ...] | None,
    mask: str | None,
    index: int,
) -> dict[str, str] | None:
    if gpu_ids is None:
        return None  # Preserve historical process inheritance exactly.
    env = os.environ.copy()
    env[mask] = str(gpu_ids[index % len(gpu_ids)])
    # ROCm PyTorch intentionally uses the CUDA device API as well.
    # After the process mask, its sole visible GPU is always cuda:0.
    env["BEES_NATIVE_INFERENCE_DEVICE_OVERRIDE"] = "cuda:0"
    return env
