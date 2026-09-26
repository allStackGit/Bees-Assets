"""Process-tree ownership and managed child health for Bees training.

Training supervisors are long-lived authority owners. Children must not outlive the owning
supervisor, and the control plane must distinguish "PID exists" from "child initialized and healthy".
"""

from __future__ import annotations

import atexit
import ctypes
import json
import os
from pathlib import Path
import secrets
import signal
import subprocess
import time
from typing import Any, Mapping, MutableMapping, Optional, Sequence


HEALTH_FILE_ENV = "BEES_TRAINING_CHILD_HEALTH_FILE"
HEALTH_TOKEN_ENV = "BEES_TRAINING_CHILD_HEALTH_TOKEN"
VALID_HEALTH_STATES = frozenset(("starting", "ready", "error"))

_windows_job_handle: Optional[int] = None


def configure_child_health(
    environment: MutableMapping[str, str],
    path: Path,
) -> str:
    """Give one managed child launch a unique health identity."""
    token = secrets.token_hex(16)
    path = path.expanduser().resolve()
    try:
        path.unlink()
    except FileNotFoundError:
        pass
    environment[HEALTH_FILE_ENV] = str(path)
    environment[HEALTH_TOKEN_ENV] = token
    return token


def write_managed_health(
    state: str,
    *,
    error: str = "",
    details: Optional[Mapping[str, Any]] = None,
) -> bool:
    """Publish health for the managed launch described by inherited environment variables."""
    if state not in VALID_HEALTH_STATES:
        raise ValueError(f"unsupported managed health state {state!r}")
    path_value = os.environ.get(HEALTH_FILE_ENV, "").strip()
    token = os.environ.get(HEALTH_TOKEN_ENV, "").strip()
    if not path_value or not token:
        return False
    path = Path(path_value).expanduser().resolve()
    value: dict[str, Any] = {
        "schema_version": 1,
        "token": token,
        "state": state,
        "error": str(error or ""),
        "updated_unix_seconds": time.time(),
        "pid": os.getpid(),
    }
    if details:
        value["details"] = dict(details)
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(path.name + f".tmp-{os.getpid()}")
    temporary.write_text(
        json.dumps(value, indent=2, sort_keys=True) + "\n",
        encoding="utf-8",
    )
    os.replace(temporary, path)
    return True


def read_managed_health(path: Optional[Path], token: str) -> Optional[dict[str, Any]]:
    if path is None or not token:
        return None
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeDecodeError, json.JSONDecodeError):
        return None
    if not isinstance(value, Mapping) or value.get("token") != token:
        return None
    state = value.get("state")
    updated = value.get("updated_unix_seconds")
    error = value.get("error", "")
    if (
        state not in VALID_HEALTH_STATES
        or not isinstance(updated, (int, float))
        or isinstance(updated, bool)
        or not isinstance(error, str)
    ):
        return None
    return dict(value)


def _linux_owned_child_preexec(parent_pid: int, previous) -> None:
    if previous is not None:
        previous()
    libc = ctypes.CDLL(None, use_errno=True)
    pr_set_pdeathsig = 1
    if libc.prctl(pr_set_pdeathsig, int(signal.SIGTERM), 0, 0, 0) != 0:
        errno_value = ctypes.get_errno()
        raise OSError(errno_value, "prctl(PR_SET_PDEATHSIG) failed")
    # Close the race where the parent dies between fork() and prctl().
    if os.getppid() != parent_pid:
        os._exit(74)


def _windows_kill_job() -> int:
    global _windows_job_handle
    if _windows_job_handle is not None:
        return _windows_job_handle

    from ctypes import wintypes

    class JOBOBJECT_BASIC_LIMIT_INFORMATION(ctypes.Structure):
        _fields_ = [
            ("PerProcessUserTimeLimit", ctypes.c_longlong),
            ("PerJobUserTimeLimit", ctypes.c_longlong),
            ("LimitFlags", wintypes.DWORD),
            ("MinimumWorkingSetSize", ctypes.c_size_t),
            ("MaximumWorkingSetSize", ctypes.c_size_t),
            ("ActiveProcessLimit", wintypes.DWORD),
            ("Affinity", ctypes.c_size_t),
            ("PriorityClass", wintypes.DWORD),
            ("SchedulingClass", wintypes.DWORD),
        ]

    class IO_COUNTERS(ctypes.Structure):
        _fields_ = [
            ("ReadOperationCount", ctypes.c_ulonglong),
            ("WriteOperationCount", ctypes.c_ulonglong),
            ("OtherOperationCount", ctypes.c_ulonglong),
            ("ReadTransferCount", ctypes.c_ulonglong),
            ("WriteTransferCount", ctypes.c_ulonglong),
            ("OtherTransferCount", ctypes.c_ulonglong),
        ]

    class JOBOBJECT_EXTENDED_LIMIT_INFORMATION(ctypes.Structure):
        _fields_ = [
            ("BasicLimitInformation", JOBOBJECT_BASIC_LIMIT_INFORMATION),
            ("IoInfo", IO_COUNTERS),
            ("ProcessMemoryLimit", ctypes.c_size_t),
            ("JobMemoryLimit", ctypes.c_size_t),
            ("PeakProcessMemoryUsed", ctypes.c_size_t),
            ("PeakJobMemoryUsed", ctypes.c_size_t),
        ]

    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel32.CreateJobObjectW.restype = wintypes.HANDLE
    kernel32.CreateJobObjectW.argtypes = (ctypes.c_void_p, wintypes.LPCWSTR)
    kernel32.SetInformationJobObject.restype = wintypes.BOOL
    kernel32.SetInformationJobObject.argtypes = (
        wintypes.HANDLE,
        ctypes.c_int,
        ctypes.c_void_p,
        wintypes.DWORD,
    )
    handle = kernel32.CreateJobObjectW(None, None)
    if not handle:
        raise ctypes.WinError(ctypes.get_last_error())

    job_object_extended_limit_information = 9
    job_object_limit_kill_on_job_close = 0x00002000
    info = JOBOBJECT_EXTENDED_LIMIT_INFORMATION()
    info.BasicLimitInformation.LimitFlags = job_object_limit_kill_on_job_close
    if not kernel32.SetInformationJobObject(
        handle,
        job_object_extended_limit_information,
        ctypes.byref(info),
        ctypes.sizeof(info),
    ):
        error = ctypes.get_last_error()
        kernel32.CloseHandle(handle)
        raise ctypes.WinError(error)

    _windows_job_handle = int(handle)

    def close_job() -> None:
        global _windows_job_handle
        current = _windows_job_handle
        _windows_job_handle = None
        if current:
            kernel32.CloseHandle(wintypes.HANDLE(current))

    atexit.register(close_job)
    return _windows_job_handle


def _assign_windows_owned_child(process: subprocess.Popen) -> None:
    from ctypes import wintypes

    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel32.AssignProcessToJobObject.restype = wintypes.BOOL
    kernel32.AssignProcessToJobObject.argtypes = (wintypes.HANDLE, wintypes.HANDLE)
    job = _windows_kill_job()
    process_handle = getattr(process, "_handle", None)
    if process_handle is None:
        raise RuntimeError("Windows managed child has no native process handle")
    if not kernel32.AssignProcessToJobObject(
        wintypes.HANDLE(job),
        wintypes.HANDLE(int(process_handle)),
    ):
        raise ctypes.WinError(ctypes.get_last_error())


def popen_owned(
    command: Sequence[str],
    **kwargs: Any,
) -> subprocess.Popen:
    """Launch a child that the OS tears down when this owning process disappears."""
    if os.name == "nt":
        process = subprocess.Popen(list(command), **kwargs)
        try:
            _assign_windows_owned_child(process)
        except Exception:
            try:
                process.kill()
                process.wait(timeout=5)
            except Exception:
                pass
            raise
        return process

    parent_pid = os.getpid()
    previous = kwargs.pop("preexec_fn", None)

    def preexec() -> None:
        _linux_owned_child_preexec(parent_pid, previous)

    kwargs["preexec_fn"] = preexec
    return subprocess.Popen(list(command), **kwargs)
