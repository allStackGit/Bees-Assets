"""Explicit local GPU placement for native rollout *processes*.

The simulator still owns physics and the Python resident policy worker owns
inference. Each native process inherits exactly one selected GPU visibility
mask; the optimizer is independently placed by its own --device setting.
No manifest, checkpoint or physical game configuration is rewritten.
"""

import json
import os
import re
import time
from pathlib import Path

from native_training_artifacts import native_headless_environment


GPU_MASK_ENVIRONMENTS = (
    "CUDA_VISIBLE_DEVICES", "ROCR_VISIBLE_DEVICES", "HIP_VISIBLE_DEVICES",
)


def rocm_visible_gpu_pci(device_index=0) -> str | None:
    """Inspect HIP's *actually loaded* device's physical PCI identity.

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
        if get_pci(address, len(address), device_index) != 0:
            return None
        value = address.value.decode("ascii").lower()
        return value if re.fullmatch(r"[0-9a-f]{4}:[0-9a-f]{2}:[0-9a-f]{2}\.[0-7]", value) else None
    except (OSError, AttributeError, UnicodeDecodeError):
        return None


def process_memory_bytes():
    """Current and peak resident memory of this process, not its device."""
    if os.name == "nt":
        import ctypes
        from ctypes import wintypes

        class Counters(ctypes.Structure):
            _fields_ = [
                ("cb", wintypes.DWORD), ("PageFaultCount", wintypes.DWORD),
                *[(name, ctypes.c_size_t) for name in (
                    "PeakWorkingSetSize", "WorkingSetSize",
                    "QuotaPeakPagedPoolUsage", "QuotaPagedPoolUsage",
                    "QuotaPeakNonPagedPoolUsage", "QuotaNonPagedPoolUsage",
                    "PagefileUsage", "PeakPagefileUsage",
                )],
            ]

        try:
            kernel = ctypes.WinDLL("kernel32")
            kernel.GetCurrentProcess.restype = wintypes.HANDLE
            get = ctypes.WinDLL("psapi").GetProcessMemoryInfo
            get.argtypes = [wintypes.HANDLE, ctypes.POINTER(Counters), wintypes.DWORD]
            get.restype = wintypes.BOOL
            counters = Counters()
            counters.cb = ctypes.sizeof(counters)
            if get(kernel.GetCurrentProcess(), ctypes.byref(counters), counters.cb):
                return {"process_resident_bytes": counters.WorkingSetSize,
                        "process_peak_resident_bytes": counters.PeakWorkingSetSize}
        except (OSError, AttributeError):
            pass
    else:
        try:
            fields = {}
            with open("/proc/self/status", encoding="ascii") as status:
                for line in status:
                    key, _, value = line.partition(":")
                    if key in ("VmRSS", "VmHWM"):
                        fields[key] = int(value.split()[0]) * 1024
            return {name: fields[key] for key, name in (
                ("VmRSS", "process_resident_bytes"),
                ("VmHWM", "process_peak_resident_bytes"),
            ) if key in fields}
        except (OSError, ValueError, IndexError):
            pass
    return {}


class OptimizerResourceSampler:
    """Constant-space, low-frequency measurements during one optimizer update.

    CPU process time counts all rank threads, not the whole host. Physical
    device busy and VRAM occupancy include other processes sharing the GPU.
    Unsupported driver counters stay absent rather than becoming zero.
    """
    def __init__(self, torch, device, interval=1.0):
        import threading

        self.torch = torch
        self.device = device
        self.interval = interval
        self.stop_event = threading.Event()
        self.thread = threading.Thread(target=self._run, daemon=True)
        self.samples = {}
        self.last_cpu_sample = None
        self.pci = None
        self.gpu_path = None
        self.gpu_power_sensor = None
        self.nvidia = False
        if device.type == "cuda":
            index = device.index
            if index is None:
                index = torch.cuda.current_device()
            if torch.version.hip:
                self.pci = rocm_visible_gpu_pci(index)
                if self.pci:
                    self.gpu_path = Path("/sys/bus/pci/devices") / self.pci
                    # Only use the hwmon belonging to the HIP-mapped
                    # physical PCI card. An unrelated hwmon0 on a dual-GPU
                    # host would silently misattribute power consumption.
                    for hwmon in sorted((self.gpu_path / "hwmon").glob("hwmon*")):
                        try:
                            if ((hwmon / "name").read_text().strip() == "amdgpu" and
                                    (hwmon / "power1_average").is_file()):
                                self.gpu_power_sensor = hwmon / "power1_average"
                                break
                        except OSError:
                            continue
            else:
                self.nvidia = True

    def _observe(self, key, value):
        import math
        if type(value) not in (float, int) or not math.isfinite(value) or value < 0:
            return
        sample = self.samples.setdefault(key, [0, 0.0, 0.0])
        sample[0] += 1
        sample[1] += value
        sample[2] = max(sample[2], value)

    def _sample(self):
        # Process CPU time includes all PyTorch/BLAS worker threads.
        # Wall normalization reports busy-core equivalents even with quotas.
        now = (time.monotonic_ns(), time.process_time_ns())
        if self.last_cpu_sample is None:
            self.last_cpu_sample = now
        else:
            wall = now[0] - self.last_cpu_sample[0]
            cpu = now[1] - self.last_cpu_sample[1]
            # Ignore noisy sub-100ms startup/exit intervals.
            if wall >= 100_000_000:
                if cpu >= 0:
                    self._observe("process_cpu_core_equivalents", cpu / wall)
                self.last_cpu_sample = now
        for key, value in process_memory_bytes().items():
            self._observe(key, value)
        if self.gpu_path is not None:
            for name, key in (
                ("gpu_busy_percent", "physical_gpu_busy_pct"),
                ("mem_info_vram_used", "physical_gpu_used_vram_bytes"),
            ):
                try:
                    self._observe(key, int((self.gpu_path / name).read_text()))
                except (OSError, ValueError):
                    pass
            if self.gpu_power_sensor is not None:
                try:
                    # Linux hwmon power1_average is in microwatts, and
                    # reports the AMD device sensor (not whole-system
                    # wall power). Missing/unavailable counters are absent.
                    self._observe("physical_gpu_hwmon_power_watts",
                                  int(self.gpu_power_sensor.read_text()) / 1_000_000)
                except (OSError, ValueError):
                    pass
        elif self.nvidia:
            # Torch's NVML helpers resolve the device visibility mask.
            # Older PyTorch/NVML installations may lack these optional APIs.
            for name, key in (
                ("utilization", "physical_gpu_busy_pct"),
                ("device_memory_used", "physical_gpu_used_vram_bytes"),
            ):
                try:
                    getter = getattr(self.torch.cuda, name, None)
                    if getter is not None:
                        self._observe(key, getter(self.device))
                except Exception:
                    # Monitoring is best-effort; never abort an update
                    # because its driver does not expose a counter.
                    pass

    def _run(self):
        while not self.stop_event.wait(self.interval):
            self._sample()

    def __enter__(self):
        self._sample()
        self.thread.start()
        return self

    def __exit__(self, *_):
        self.stop_event.set()
        self.thread.join()
        self._sample()

    def summary(self):
        return {
            "scope": "optimizer_rank_process_and_shared_physical_device",
            "sample_interval_seconds": self.interval,
            **({"gpu_pci_address": self.pci} if self.pci else {}),
            **({"gpu_power_source": "amdgpu hwmon power1_average (device sensor)"}
               if self.gpu_power_sensor is not None else {}),
            "samples": {
                key: ({"sample_count": count, "observed_high_water": peak}
                      if key == "process_peak_resident_bytes" else
                      {"sample_count": count, "sample_mean": total / count,
                       "sample_peak": peak})
                for key, (count, total, peak) in self.samples.items()
            },
        }


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
    env = native_headless_environment()
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
