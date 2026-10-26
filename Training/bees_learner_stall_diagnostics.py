"""Timestamped host diagnostics while learner CPU/GPU work is executing.

This sampler is independent of the training thread: unusually slow tensor
materialization or trajectory evaluation cannot prevent it from recording
memory pressure, paging-related activity, CPU scheduling, I/O and GPU clocks.
"""
from __future__ import annotations

from contextlib import contextmanager, nullcontext
from datetime import datetime, timezone
import ctypes
import os
import shutil
import subprocess
import threading
import time


from bees_log_categories import log_category_enabled


MIB = 1024 * 1024
SAMPLE_SECONDS = 5.0




class _WindowsMemoryCounters:
    """System commit and physical page-read counters (not per-process hard faults)."""

    def __init__(self):
        self._query = None
        self._counters = {}
        self._pdh = None
        self._performance = None
        if os.name != "nt":
            return
        try:
            from ctypes import wintypes

            class _PerformanceInfo(ctypes.Structure):
                _fields_ = [
                    ("cb", wintypes.DWORD),
                    ("CommitTotal", ctypes.c_size_t),
                    ("CommitLimit", ctypes.c_size_t),
                    ("CommitPeak", ctypes.c_size_t),
                    ("PhysicalTotal", ctypes.c_size_t),
                    ("PhysicalAvailable", ctypes.c_size_t),
                    ("SystemCache", ctypes.c_size_t),
                    ("KernelTotal", ctypes.c_size_t),
                    ("KernelPaged", ctypes.c_size_t),
                    ("KernelNonpaged", ctypes.c_size_t),
                    ("PageSize", ctypes.c_size_t),
                    ("HandleCount", wintypes.DWORD),
                    ("ProcessCount", wintypes.DWORD),
                    ("ThreadCount", wintypes.DWORD),
                ]

            self._performance = (ctypes.WinDLL("psapi"), _PerformanceInfo)
            pdh = ctypes.WinDLL("pdh")
            query = ctypes.c_void_p()
            if pdh.PdhOpenQueryW(None, 0, ctypes.byref(query)) != 0:
                return
            self._query = query
            self._pdh = pdh
            for name, path in (
                ("page_reads_s", r"\Memory\Page Reads/sec"),
                ("pages_input_s", r"\Memory\Pages Input/sec"),
            ):
                counter = ctypes.c_void_p()
                if pdh.PdhAddEnglishCounterW(query, path, 0, ctypes.byref(counter)) == 0:
                    self._counters[name] = counter
            pdh.PdhCollectQueryData(query)
        except (AttributeError, OSError, ValueError):
            self.close()

    def sample(self):
        result = {}
        if self._performance is not None:
            try:
                dll, type_ = self._performance
                info = type_()
                info.cb = ctypes.sizeof(type_)
                if dll.GetPerformanceInfo(ctypes.byref(info), info.cb):
                    result["sys_commit_mib"] = round(info.CommitTotal * info.PageSize / MIB, 1)
                    result["sys_commit_limit_mib"] = round(info.CommitLimit * info.PageSize / MIB, 1)
            except (OSError, ValueError):
                pass
        if self._pdh is not None and self._query is not None:
            try:
                class _Value(ctypes.Union):
                    _fields_ = [("long", ctypes.c_long), ("double", ctypes.c_double)]
                class _Formatted(ctypes.Structure):
                    _fields_ = [("status", ctypes.c_uint32), ("value", _Value)]
                if self._pdh.PdhCollectQueryData(self._query) == 0:
                    for name, counter in self._counters.items():
                        value = _Formatted()
                        if self._pdh.PdhGetFormattedCounterValue(
                            counter, 0x200, None, ctypes.byref(value)
                        ) == 0 and value.status == 0:
                            result[name] = round(max(0.0, value.value.double), 2)
            except (OSError, ValueError):
                pass
        return result

    def close(self):
        if self._pdh is not None and self._query is not None:
            try:
                self._pdh.PdhCloseQuery(self._query)
            except (OSError, ValueError):
                pass
        self._query = None
        self._pdh = None


class _LearnerStallSampler:
    def __init__(self):
        self._lock = threading.Lock()
        self._stages = {}
        self._cpu_diagnostics_enabled = os.environ.get("BEES_DIAGNOSTICS_CPU", "1") != "0"
        self._gpu_diagnostics_enabled = os.environ.get("BEES_DIAGNOSTICS_GPU", "1") != "0"
        self._windows = _WindowsMemoryCounters() if self._cpu_diagnostics_enabled else None
        self._stop = threading.Event()
        self._psutil = None
        self._process = None
        self._previous = {}
        self._previous_thread_cpu = {}
        self._previous_thread_sample_at = None
        self._last_gpu = {}
        self._last_gpu_at = 0.0
        self._smi = shutil.which("nvidia-smi") if self._gpu_diagnostics_enabled else None
        if self._gpu_diagnostics_enabled and not self._smi and os.name == "nt":
            for candidate in (
                os.path.join(os.environ.get("ProgramW6432", r"C:\Program Files"),
                             "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe"),
                os.path.join(os.environ.get("SystemRoot", r"C:\Windows"),
                             "System32", "nvidia-smi.exe"),
            ):
                if os.path.isfile(candidate):
                    self._smi = candidate
                    break
        if self._cpu_diagnostics_enabled:
            try:
                import psutil
                self._psutil = psutil
                self._process = psutil.Process(os.getpid())
                self._process.cpu_percent(None)
                psutil.cpu_percent(None)
            except (ImportError, OSError):
                pass
        self._thread = threading.Thread(
            target=self._loop, name="bees-learner-stall-sampler", daemon=True
        )
        self._thread.start()

    @contextmanager
    def stage(self, name):
        tid = threading.get_ident()
        started = time.monotonic()
        with self._lock:
            stack = self._stages.setdefault(tid, [])
            stack.append((name, started))
        try:
            yield
        finally:
            seconds = time.monotonic() - started
            with self._lock:
                stack = self._stages.get(tid, [])
                if stack:
                    stack.pop()
                if not stack:
                    self._stages.pop(tid, None)
            if seconds >= 5.0 and log_category_enabled("learner-stages"):
                print(
                    "[Bees learner stage] "
                    f"utc={datetime.now(timezone.utc).isoformat()} "
                    f"stage={name} seconds={seconds:.3f}",
                    flush=True,
                )

    def _delta(self, name, number):
        if not isinstance(number, (float, int)):
            return None
        previous = self._previous.get(name)
        self._previous[name] = number
        return None if previous is None or number < previous else number - previous

    def _sample_gpu(self, now):
        if not self._smi or now - self._last_gpu_at < 15.0:
            return self._last_gpu
        self._last_gpu_at = now
        try:
            result = subprocess.run(
                [self._smi, "--query-gpu=utilization.gpu,memory.used,clocks.current.graphics",
                 "--format=csv,noheader,nounits"],
                capture_output=True, text=True, timeout=3,
                creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
            )
            if result.returncode == 0:
                parts = result.stdout.splitlines()[0].split(",")
                self._last_gpu = {
                    "gpu_util_percent": float(parts[0].strip()),
                    "gpu_mem_mib": float(parts[1].strip()),
                    "gpu_clock_mhz": float(parts[2].strip()),
                }
        except (OSError, ValueError, IndexError, subprocess.TimeoutExpired):
            pass
        return self._last_gpu

    def _sample_thread_cpu(self):
        """Attribute CPU consumption to learner, broker and native threads.

        Request handlers run in this process, so aggregate process CPU alone
        cannot distinguish slow PPO computation from competing WAN decoding.
        The first sample establishes a baseline; later values are CPU %
        relative to the sampling interval (100% means one full logical CPU).
        """
        if self._process is None:
            return {}
        try:
            now = time.monotonic()
            native = {
                thread.native_id: (thread.name, thread.ident)
                for thread in threading.enumerate()
                if thread.native_id is not None
            }
            with self._lock:
                active_learner_threads = set(self._stages)
            samples = self._process.threads()
            current = {
                thread.id: float(thread.user_time + thread.system_time)
                for thread in samples
            }
            previous_time = self._previous_thread_sample_at
            previous = self._previous_thread_cpu
            self._previous_thread_sample_at = now
            self._previous_thread_cpu = current
            if previous_time is None or now <= previous_time:
                return {}

            elapsed = now - previous_time
            buckets = {
                "learner": 0.0,
                "wan_http": 0.0,
                "ppo_prepare": 0.0,
                "python_other": 0.0,
                "native_other": 0.0,
            }
            wan_threads = 0
            for thread_id, consumed in current.items():
                info = native.get(thread_id)
                if info is not None and "process_request_thread" in info[0]:
                    wan_threads += 1
                if thread_id not in previous or consumed < previous[thread_id]:
                    continue
                delta = consumed - previous[thread_id]
                if info is None:
                    bucket = "native_other"
                elif info[1] in active_learner_threads:
                    bucket = "learner"
                elif "process_request_thread" in info[0]:
                    bucket = "wan_http"
                elif info[0].startswith("bees-poca-cpu-prepare"):
                    bucket = "ppo_prepare"
                else:
                    bucket = "python_other"
                buckets[bucket] += delta
            result = {
                "thread_cpu_" + key + "_pct": round(100.0 * value / elapsed, 1)
                for key, value in buckets.items()
            }
            result["wan_http_threads"] = wan_threads
            return result
        except (OSError, RuntimeError, ValueError):
            # Profiling must never interrupt the learner.
            return {}

    def _sample(self):
        data = self._windows.sample() if self._windows is not None else {}
        if self._psutil is not None and self._process is not None:
            psutil = self._psutil
            process = self._process
            try:
                memory = process.memory_info()
                vm = psutil.virtual_memory()
                swap = psutil.swap_memory()
                context = process.num_ctx_switches()
                data.update({
                    "proc_cpu_percent": round(process.cpu_percent(None), 1),
                    "sys_cpu_percent": round(psutil.cpu_percent(None), 1),
                    "proc_rss_mib": round(memory.rss / MIB, 1),
                    "proc_commit_mib": round(getattr(
                        memory, "pagefile", getattr(memory, "private", memory.vms)
                    ) / MIB, 1),
                    "sys_available_mib": round(vm.available / MIB, 1),
                    "sys_swap_percent": round(swap.percent, 1),
                    "proc_threads": process.num_threads(),
                })
                counters = {
                    "proc_page_faults_delta": getattr(memory, "num_page_faults", None),
                    "ctx_voluntary_delta": context.voluntary,
                    "ctx_involuntary_delta": context.involuntary,
                    "swap_in_bytes_delta": swap.sin,
                    "swap_out_bytes_delta": swap.sout,
                }
                try:
                    io = process.io_counters()
                    counters["proc_read_bytes_delta"] = io.read_bytes
                    counters["proc_write_bytes_delta"] = io.write_bytes
                except (AttributeError, OSError):
                    pass
                try:
                    disk = psutil.disk_io_counters()
                    if disk is not None:
                        counters["sys_disk_read_bytes_delta"] = disk.read_bytes
                        counters["sys_disk_write_bytes_delta"] = disk.write_bytes
                except (AttributeError, OSError):
                    pass
                for name, count in counters.items():
                    delta = self._delta(name, count)
                    if delta is not None:
                        data[name] = int(delta)
            except (OSError, RuntimeError):
                pass
        data.update(self._sample_thread_cpu())
        data.update(self._sample_gpu(time.monotonic()))
        return data

    def _loop(self):
        while not self._stop.wait(SAMPLE_SECONDS):
            if not log_category_enabled("learner-samples"):
                continue
            try:
                with self._lock:
                    now = time.monotonic()
                    stages = [
                        f"{tid}:{'/'.join(name for name, _ in stack)}:{now - stack[-1][1]:.1f}s"
                        for tid, stack in self._stages.items() if stack
                    ]
                data = self._sample()
                details = " ".join(f"{key}={value}" for key, value in data.items())
                print(
                    "[Bees learner sample] "
                    f"utc={datetime.now(timezone.utc).isoformat()} "
                    f"active={';'.join(stages) if stages else 'idle'} "
                    + details,
                    flush=True,
                )
            except Exception as exc:
                # Diagnostics must not interrupt learner progress.
                print(f"[Bees learner sample] sampling_error={type(exc).__name__}: {exc}", flush=True)


_sampler = None
_sampler_lock = threading.Lock()


def learner_stage(name, *, enabled=True):
    """Cheap no-op when diagnostics are disabled; nested/thread-aware otherwise."""
    if not enabled or not (
        log_category_enabled("learner-stages") or log_category_enabled("learner-samples")
    ):
        return nullcontext()
    global _sampler
    if _sampler is None:
        with _sampler_lock:
            if _sampler is None:
                _sampler = _LearnerStallSampler()
    return _sampler.stage(name)
