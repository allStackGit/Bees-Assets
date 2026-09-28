"""Process-tree ownership and managed child health for Bees training.

Training supervisors are long-lived authority owners. Children must not outlive the owning
supervisor, and the control plane must distinguish "PID exists" from "child initialized and healthy".
"""

from __future__ import annotations

import atexit
import ctypes
import errno
import json
import math
import os
from pathlib import Path
import secrets
import signal
import subprocess
import sys
import time
from typing import Any, Mapping, MutableMapping, Optional, Sequence


HEALTH_FILE_ENV = "BEES_TRAINING_CHILD_HEALTH_FILE"
HEALTH_TOKEN_ENV = "BEES_TRAINING_CHILD_HEALTH_TOKEN"
VALID_HEALTH_STATES = frozenset(("starting", "ready", "error"))
OWNED_CHILD_TERMINATION_GRACE_SECONDS = 10.0
MANAGED_HEALTH_FUTURE_CLOCK_SKEW_SECONDS = 5.0
ATOMIC_REPLACE_RETRY_DELAYS = (0.01, 0.025, 0.05, 0.1, 0.2, 0.4)

_windows_job_handle: Optional[int] = None



def atomic_write_text(
    path: Path,
    value: str,
    *,
    encoding: str = "utf-8",
) -> None:
    """Atomically publish text while tolerating brief Windows sharing violations."""
    path = path.expanduser().resolve()
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(
        path.name + f".tmp-{os.getpid()}-{secrets.token_hex(6)}"
    )
    try:
        temporary.write_text(value, encoding=encoding)
        for attempt in range(len(ATOMIC_REPLACE_RETRY_DELAYS) + 1):
            try:
                os.replace(temporary, path)
                return
            except OSError as exc:
                transient = (
                    isinstance(exc, PermissionError)
                    or getattr(exc, "winerror", None) in (5, 32, 33)
                    or getattr(exc, "errno", None)
                    in (errno.EACCES, errno.EPERM, errno.EBUSY)
                )
                if not transient or attempt >= len(ATOMIC_REPLACE_RETRY_DELAYS):
                    raise
                time.sleep(ATOMIC_REPLACE_RETRY_DELAYS[attempt])
    finally:
        try:
            temporary.unlink()
        except FileNotFoundError:
            pass
        except OSError:
            pass

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
    try:
        atomic_write_text(
            path,
            json.dumps(value, indent=2, sort_keys=True) + "\n",
            encoding="utf-8",
        )
        return True
    except OSError:
        return False


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
    try:
        updated_seconds = float(updated)
    except (OverflowError, ValueError):
        return None
    if not math.isfinite(updated_seconds):
        return None
    if updated_seconds - time.time() > MANAGED_HEALTH_FUTURE_CLOCK_SKEW_SECONDS:
        return None
    return dict(value)


def _owned_child_main(argv: Sequence[str]) -> int:
    if len(argv) < 3 or argv[0] != "--owned-child" or argv[2] != "--":
        raise SystemExit("invalid owned-child invocation")
    try:
        parent_pid = int(argv[1])
    except ValueError as exc:
        raise SystemExit("invalid owned-child parent pid") from exc
    command = list(argv[3:])
    if not command:
        raise SystemExit("owned-child invocation requires a command")

    child: Optional[subprocess.Popen] = None
    termination_started: Optional[float] = None
    child_group_kill_sent = False
    interrupt_requested = False

    def signal_child_group(signal_number: int) -> None:
        if child is None:
            return
        try:
            os.killpg(child.pid, signal_number)
        except ProcessLookupError:
            pass

    def owner_terminated(_signum, _frame) -> None:
        nonlocal termination_started
        if termination_started is None:
            termination_started = time.monotonic()
            signal_child_group(signal.SIGTERM)

    def owner_interrupted(_signum, _frame) -> None:
        nonlocal interrupt_requested
        if child is None:
            interrupt_requested = True
            return
        signal_child_group(signal.SIGINT)

    # Keep this process as the owner guardian instead of execing the learner. The learner runs in
    # its own process group so its ML-Agents environment workers can be stopped as one unit.
    signal.signal(signal.SIGTERM, owner_terminated)
    signal.signal(signal.SIGINT, owner_interrupted)

    libc = ctypes.CDLL(None, use_errno=True)
    pr_set_pdeathsig = 1
    if libc.prctl(pr_set_pdeathsig, int(signal.SIGTERM), 0, 0, 0) != 0:
        errno_value = ctypes.get_errno()
        raise OSError(errno_value, "prctl(PR_SET_PDEATHSIG) failed")
    # Close the race where the owner dies between spawn and PR_SET_PDEATHSIG.
    if os.getppid() != parent_pid:
        return 74

    child = subprocess.Popen(
        command,
        close_fds=False,
        start_new_session=True,
    )
    if termination_started is not None:
        signal_child_group(signal.SIGTERM)
    if interrupt_requested:
        signal_child_group(signal.SIGINT)

    while True:
        try:
            return_code = child.wait(timeout=0.25)
            break
        except subprocess.TimeoutExpired:
            if (
                termination_started is not None
                and not child_group_kill_sent
                and time.monotonic() - termination_started >= OWNED_CHILD_TERMINATION_GRACE_SECONDS
            ):
                signal_child_group(signal.SIGKILL)
                child_group_kill_sent = True

    # A learner may exit while one of its environment workers remains alive. Retire any such
    # descendants before reporting that the owned launch has finished.
    try:
        os.killpg(child.pid, signal.SIGTERM)
    except ProcessLookupError:
        # No descendants remain, but still preserve the learner's signal exit below.
        pass

    deadline = time.monotonic() + 0.25
    while time.monotonic() < deadline:
        try:
            os.killpg(child.pid, 0)
        except ProcessLookupError:
            break
        except PermissionError:
            pass
        time.sleep(0.025)
    else:
        try:
            os.killpg(child.pid, signal.SIGKILL)
        except ProcessLookupError:
            pass

    if return_code < 0:
        child_signal = -return_code
        try:
            if child_signal not in (signal.SIGKILL, signal.SIGSTOP):
                signal.signal(child_signal, signal.SIG_DFL)
            os.kill(os.getpid(), child_signal)
        except OSError:
            # Preserve the usual shell status if the signal cannot be redelivered.
            return min(255, 128 + child_signal)

    return return_code


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
    kernel32.CloseHandle.restype = wintypes.BOOL
    kernel32.CloseHandle.argtypes = (wintypes.HANDLE,)
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

    handle_value = getattr(handle, "value", handle)
    _windows_job_handle = int(handle_value)

    def close_job() -> None:
        global _windows_job_handle
        current = _windows_job_handle
        _windows_job_handle = None
        if current:
            kernel32.CloseHandle(wintypes.HANDLE(current))

    atexit.register(close_job)
    return _windows_job_handle


def close_windows_owned_child_job() -> None:
    """Close this process's kill-on-close job so all currently owned Windows children exit."""
    global _windows_job_handle
    current = _windows_job_handle
    if not current:
        return
    from ctypes import wintypes

    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel32.CloseHandle.restype = wintypes.BOOL
    kernel32.CloseHandle.argtypes = (wintypes.HANDLE,)
    _windows_job_handle = None
    if not kernel32.CloseHandle(wintypes.HANDLE(current)):
        raise ctypes.WinError(ctypes.get_last_error())


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


def _is_windows() -> bool:
    return os.name == "nt"


def popen_owned(
    command: Sequence[str],
    **kwargs: Any,
) -> subprocess.Popen:
    """Launch a child that the OS tears down when this owning process disappears."""
    if _is_windows():
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

    if "preexec_fn" in kwargs:
        raise ValueError("popen_owned does not accept preexec_fn")
    wrapper = [
        sys.executable,
        str(Path(__file__).resolve()),
        "--owned-child",
        str(os.getpid()),
        "--",
        *[str(item) for item in command],
    ]
    return subprocess.Popen(wrapper, **kwargs)


if __name__ == "__main__":
    raise SystemExit(_owned_child_main(sys.argv[1:]))
