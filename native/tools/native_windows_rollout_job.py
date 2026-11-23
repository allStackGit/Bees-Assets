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

def run_owned_native_process(command, **kwargs):
    """Run one native host/collector and retire its entire owned process tree.

    On Windows a completed Rust parent must not leave a resident Python
    inference descendant consuming GPU/CPU resources. POSIX callers keep
    subprocess.run(check=True) behavior and existing process supervision.
    """
    if os.name != "nt":
        return subprocess.run(command, check=True, **kwargs)
    process = subprocess.Popen(
        command, creationflags=subprocess.CREATE_NEW_PROCESS_GROUP, **kwargs
    )
    try:
        job = NativeWindowsRolloutJob(process)
    except BaseException:
        # Assignment failure is fatal: this process is not safely owned.
        # At least stop its still-live parent before propagating the error.
        if process.poll() is None:
            process.kill()
        process.wait()
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
