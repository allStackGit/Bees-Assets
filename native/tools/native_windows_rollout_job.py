"""Own native rollout process trees on Windows across Rust parent failures.

The Rust simulation process can spawn a resident Python policy worker.
Unlike a PID-tree scan, a kill-on-close Windows Job Object retains its
membership after the Rust parent exits. Every spawned rollout owns one
independent job; no machine-wide process enumeration or other jobs are touched.
"""

import ctypes
from ctypes import wintypes
import os
import subprocess
import signal


class _BasicLimit(ctypes.Structure):
    _fields_ = [
        ("PerProcessUserTimeLimit", ctypes.c_int64),
        ("PerJobUserTimeLimit", ctypes.c_int64),
        ("LimitFlags", wintypes.DWORD),
        ("MinimumWorkingSetSize", ctypes.c_size_t),
        ("MaximumWorkingSetSize", ctypes.c_size_t),
        ("ActiveProcessLimit", wintypes.DWORD),
        ("Affinity", ctypes.c_size_t),
        ("PriorityClass", wintypes.DWORD),
        ("SchedulingClass", wintypes.DWORD),
    ]


class _IoCounters(ctypes.Structure):
    _fields_ = [
        (name, ctypes.c_uint64)
        for name in ("ReadOperationCount", "WriteOperationCount",
                     "OtherOperationCount", "ReadTransferCount",
                     "WriteTransferCount", "OtherTransferCount")
    ]


class _ExtendedLimit(ctypes.Structure):
    _fields_ = [
        ("BasicLimitInformation", _BasicLimit),
        ("IoInfo", _IoCounters),
        ("ProcessMemoryLimit", ctypes.c_size_t),
        ("JobMemoryLimit", ctypes.c_size_t),
        ("PeakProcessMemoryUsed", ctypes.c_size_t),
        ("PeakJobMemoryUsed", ctypes.c_size_t),
    ]


class _BasicAccounting(ctypes.Structure):
    _fields_ = [
        ("TotalUserTime", ctypes.c_int64),
        ("TotalKernelTime", ctypes.c_int64),
        ("ThisPeriodTotalUserTime", ctypes.c_int64),
        ("ThisPeriodTotalKernelTime", ctypes.c_int64),
        ("TotalPageFaultCount", wintypes.DWORD),
        ("TotalProcesses", wintypes.DWORD),
        ("ActiveProcesses", wintypes.DWORD),
        ("TotalTerminatedProcesses", wintypes.DWORD),
    ]


class NativeWindowsRolloutJob:
    """One OS-owned tree for a single native simulator and its descendants.

    The supervisor must hold this object for the entire rollout. Closing it
    terminates any surviving descendants even if Popen.poll() already reports
    the Rust parent as exited. Nested jobs work on supported modern Windows;
    unsupported restrictive supervisors fail closed rather than claim
    cancellation of a worker we cannot own.
    """

    def __init__(self, process):
        if os.name != "nt":
            raise OSError("Windows Job Objects are only available on Windows")
        kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        kernel.CreateJobObjectW.argtypes = [ctypes.c_void_p, wintypes.LPCWSTR]
        kernel.CreateJobObjectW.restype = wintypes.HANDLE
        kernel.SetInformationJobObject.argtypes = [
            wintypes.HANDLE, ctypes.c_int, ctypes.c_void_p, wintypes.DWORD,
        ]
        kernel.SetInformationJobObject.restype = wintypes.BOOL
        kernel.AssignProcessToJobObject.argtypes = [
            wintypes.HANDLE, wintypes.HANDLE,
        ]
        kernel.AssignProcessToJobObject.restype = wintypes.BOOL
        kernel.TerminateJobObject.argtypes = [wintypes.HANDLE, wintypes.UINT]
        kernel.TerminateJobObject.restype = wintypes.BOOL
        kernel.QueryInformationJobObject.argtypes = [
            wintypes.HANDLE, ctypes.c_int, ctypes.c_void_p,
            wintypes.DWORD, ctypes.POINTER(wintypes.DWORD),
        ]
        kernel.QueryInformationJobObject.restype = wintypes.BOOL
        kernel.CloseHandle.argtypes = [wintypes.HANDLE]
        kernel.CloseHandle.restype = wintypes.BOOL

        self.kernel = kernel
        self.handle = kernel.CreateJobObjectW(None, None)
        if not self.handle:
            raise ctypes.WinError(ctypes.get_last_error())
        try:
            limits = _ExtendedLimit()
            limits.BasicLimitInformation.LimitFlags = 0x00002000  # KILL_ON_JOB_CLOSE
            if not kernel.SetInformationJobObject(
                self.handle, 9, ctypes.byref(limits), ctypes.sizeof(limits)
            ):
                raise ctypes.WinError(ctypes.get_last_error())
            if not kernel.AssignProcessToJobObject(self.handle, int(process._handle)):
                raise ctypes.WinError(ctypes.get_last_error())
        except BaseException:
            self.close()
            raise

    def accounting(self):
        """Observed OS job CPU and process lifecycle, including descendants.

        Sample before closing the job. Unsupported counters are absent
        rather than misreported as zero or inferred from the Rust PID.
        """
        if self.handle is None:
            return None
        record = _BasicAccounting()
        if not self.kernel.QueryInformationJobObject(
            self.handle, 1, ctypes.byref(record), ctypes.sizeof(record), None
        ):
            return None
        return {
            "owned_processes_total": record.TotalProcesses,
            "owned_processes_active": record.ActiveProcesses,
            "owned_processes_terminated": record.TotalTerminatedProcesses,
            "owned_process_cpu_seconds": (
                record.TotalUserTime + record.TotalKernelTime
            ) / 10_000_000.0,
            "scope": "os_job_all_owned_processes_current_invocation",
        }

    def terminate(self):
        if self.handle is None:
            return
        if not self.kernel.TerminateJobObject(self.handle, 1):
            error = ctypes.WinError(ctypes.get_last_error())
            self.close()
            raise error
        self.close()

    def close(self):
        if self.handle is not None:
            handle, self.handle = self.handle, None
            if not self.kernel.CloseHandle(handle):
                raise ctypes.WinError(ctypes.get_last_error())

def stop_unassigned_windows_process(process):
    """Best-effort cleanup only when a Windows Job attachment failed.

    Unlike the successful Job Object path, taskkill can lose a child when
    its Rust parent has already exited. Callers must abort the collection
    on this fallback, never publish a successful worker receipt.
    """
    if os.name != "nt":
        raise OSError("unassigned Windows process cancellation is Windows-only")
    if process.poll() is None:
        try:
            subprocess.run(
                ["taskkill", "/PID", str(process.pid), "/T", "/F"],
                stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                check=False, timeout=5,
            )
        except (OSError, subprocess.TimeoutExpired):
            pass
        if process.poll() is None:
            process.kill()
    process.wait()


def run_owned_native_process(command, **kwargs):
    """Run one native host and own every descendant until it is reaped.

    A completed Rust parent must not leave a resident policy subprocess
    consuming accelerator resources. The simulator keeps all physics and
    model protocol ownership; this only manages OS process lifetimes.
    """
    if os.name != "nt":
        # A separate POSIX session gives the supervisor one owned process
        # group, rather than relying on the parent Rust PID alone.
        process = subprocess.Popen(command, start_new_session=True, **kwargs)
        try:
            if all(hasattr(os, name) for name in (
                    "waitid", "P_PID", "WEXITED", "WNOWAIT")):
                # Linux can observe the exited group leader WITHOUT reaping
                # its PID. That prevents its numeric process-group ID from
                # being recycled before orphan cleanup.
                os.waitid(os.P_PID, process.pid, os.WEXITED | os.WNOWAIT)
            else:
                process.wait()
        finally:
            try:
                # Only the process group created for this invocation. The
                # source host has exited, so any survivor is a leaked actor.
                os.killpg(process.pid, signal.SIGKILL)
            except ProcessLookupError:
                pass
            process.wait()
        if process.returncode != 0:
            raise subprocess.CalledProcessError(process.returncode, command)
        return subprocess.CompletedProcess(command, process.returncode)

    process = subprocess.Popen(
        command, creationflags=subprocess.CREATE_NEW_PROCESS_GROUP, **kwargs
    )
    try:
        job = NativeWindowsRolloutJob(process)
    except BaseException:
        # Attachment failure cannot be treated as successfully supervised.
        stop_unassigned_windows_process(process)
        raise
    try:
        returncode = process.wait()
        if returncode != 0:
            raise subprocess.CalledProcessError(returncode, command)
        return subprocess.CompletedProcess(command, returncode)
    finally:
        # KILL_ON_JOB_CLOSE also retires descendants of an already-exited
        # simulator. Never scan machine-wide PIDs or kill another session.
        job.close()
        process.wait()
