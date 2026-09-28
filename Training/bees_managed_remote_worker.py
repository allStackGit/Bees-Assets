"""Persistent one-command remote Bees rollout worker supervisor.

The worker uses the bundled userspace tailnet bridge for both BeesServer control traffic and WAN
rollout traffic. No separate remote-login service, account, password, key, or file-copy transport is involved.
The learner-side WAN broker assigns an available actor slot automatically. Each remote installation
keeps a persistent actor key so reconnects can reclaim its current slot safely.
"""

from __future__ import annotations

import argparse
import ctypes
import hashlib
import io
import json
import math
import os
from pathlib import Path
import re
import signal
import shutil
import socket
import subprocess
import sys
import tempfile
import threading
import time
from typing import Mapping, Optional, Sequence
import urllib.error
import urllib.parse
import urllib.request
import uuid
import zipfile

from bees_process_safety import popen_owned


DEFAULT_RECONNECT_SECONDS = 5.0
MAX_ENVS_PER_ACTOR = 64
REMOTE_MEMORY_RESERVE_BYTES = 1 * 1024 * 1024 * 1024
REMOTE_MEMORY_PER_ENV_BYTES = 512 * 1024 * 1024
REMOTE_PID_FILE = "remote-worker.pid"
REMOTE_STOP_REQUEST_FILE = "remote-worker.stop"
REMOTE_WORKER_AGENT_STOP_REQUEST_FILE = "worker-agent-stop.request"
MAX_RETAINED_RUNTIME_VERSIONS = 4
MAX_RETAINED_VENV_VERSIONS = 4
RUN_ID_PATTERN = re.compile(r"^[A-Za-z0-9._-]+$")


class _RunScopedLogSink:
    """Mirror supervisor/child console output into the run-scoped uploaded log tree."""

    MAX_BYTES = 16 * 1024 * 1024
    MAX_ROTATED_FILES = 4

    def __init__(self, root: Path) -> None:
        self.root = root
        self._run_id = ""
        self._lock = threading.Lock()

    def set_run_id(self, run_id: str) -> None:
        normalized = str(run_id or "").strip()
        if normalized and (
            normalized in {".", ".."}
            or not RUN_ID_PATTERN.fullmatch(normalized)
        ):
            normalized = ""
        with self._lock:
            self._run_id = normalized

    def write(self, value: str) -> None:
        if not value:
            return
        with self._lock:
            run_id = self._run_id
            if not run_id:
                return
            path = self.root / run_id / "remote-supervisor.log"
            try:
                path.parent.mkdir(parents=True, exist_ok=True)
                encoded_size = len(value.encode("utf-8", errors="replace"))
                current_size = path.stat().st_size if path.is_file() else 0
                if current_size > 0 and current_size + encoded_size > self.MAX_BYTES:
                    rotated = path.with_name(
                        f"remote-supervisor-{time.time_ns():020d}.log"
                    )
                    os.replace(path, rotated)
                    retained = sorted(
                        path.parent.glob("remote-supervisor-*.log"),
                        key=lambda item: item.name,
                    )
                    for expired in retained[:-self.MAX_ROTATED_FILES]:
                        try:
                            expired.unlink()
                        except FileNotFoundError:
                            pass
                with path.open("a", encoding="utf-8", errors="replace") as handle:
                    handle.write(value)
            except OSError:
                pass


class _RunScopedTee:
    def __init__(self, primary, sink: _RunScopedLogSink) -> None:
        self.primary = primary
        self.sink = sink

    def write(self, value):
        result = self.primary.write(value)
        self.sink.write(str(value))
        return result

    def flush(self):
        self.primary.flush()

    def isatty(self):
        return bool(getattr(self.primary, "isatty", lambda: False)())

    def fileno(self):
        return self.primary.fileno()

    @property
    def encoding(self):
        return getattr(self.primary, "encoding", "utf-8")


def _forward_process_output(process: subprocess.Popen) -> None:
    stream = process.stdout
    if stream is None:
        return
    try:
        for line in stream:
            sys.stdout.write(line)
            sys.stdout.flush()
    finally:
        try:
            stream.close()
        except OSError:
            pass


def _start_logged_process(command: Sequence[str]) -> tuple[subprocess.Popen, threading.Thread]:
    process = popen_owned(
        list(command),
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        text=True,
        encoding="utf-8",
        errors="replace",
        bufsize=1,
    )
    thread = threading.Thread(
        target=_forward_process_output,
        args=(process,),
        name="bees-remote-console-forwarder",
        daemon=True,
    )
    thread.start()
    return process, thread


def _available_cpu_threads() -> int:
    affinity = getattr(os, "sched_getaffinity", None)
    if affinity is not None:
        try:
            count = len(affinity(0))
            if count > 0:
                return count
        except (OSError, TypeError):
            pass

    if os.name == "nt":
        try:
            process_mask = ctypes.c_size_t()
            system_mask = ctypes.c_size_t()
            kernel32 = ctypes.windll.kernel32
            if kernel32.GetProcessAffinityMask(
                kernel32.GetCurrentProcess(),
                ctypes.byref(process_mask),
                ctypes.byref(system_mask),
            ):
                count = int(process_mask.value).bit_count()
                if count > 0:
                    return count
        except (AttributeError, OSError, ValueError):
            pass

    return max(1, int(os.cpu_count() or 1))


class _MemoryStatusEx(ctypes.Structure):
    _fields_ = [
        ("dwLength", ctypes.c_ulong),
        ("dwMemoryLoad", ctypes.c_ulong),
        ("ullTotalPhys", ctypes.c_ulonglong),
        ("ullAvailPhys", ctypes.c_ulonglong),
        ("ullTotalPageFile", ctypes.c_ulonglong),
        ("ullAvailPageFile", ctypes.c_ulonglong),
        ("ullTotalVirtual", ctypes.c_ulonglong),
        ("ullAvailVirtual", ctypes.c_ulonglong),
        ("ullAvailExtendedVirtual", ctypes.c_ulonglong),
    ]


def _windows_memory_status() -> Optional[_MemoryStatusEx]:
    if os.name != "nt":
        return None
    try:
        status = _MemoryStatusEx()
        status.dwLength = ctypes.sizeof(status)
        if ctypes.windll.kernel32.GlobalMemoryStatusEx(ctypes.byref(status)):
            return status
    except (AttributeError, OSError, ValueError):
        pass
    return None


def _available_memory_bytes() -> Optional[int]:
    status = _windows_memory_status()
    if status is not None:
        available = int(status.ullAvailPhys)
        if available > 0:
            return available

    meminfo = Path("/proc/meminfo")
    if meminfo.is_file():
        try:
            for line in meminfo.read_text(encoding="ascii").splitlines():
                if line.startswith("MemAvailable:"):
                    parts = line.split()
                    if len(parts) >= 2:
                        available = int(parts[1]) * 1024
                        if available > 0:
                            return available
        except (OSError, UnicodeError, ValueError):
            pass

    try:
        pages = int(os.sysconf("SC_AVPHYS_PAGES"))
        page_size = int(os.sysconf("SC_PAGE_SIZE"))
        available = pages * page_size
        return available if available > 0 else None
    except (AttributeError, OSError, TypeError, ValueError):
        return None


def _total_memory_bytes() -> Optional[int]:
    status = _windows_memory_status()
    if status is not None:
        total = int(status.ullTotalPhys)
        if total > 0:
            return total

    meminfo = Path("/proc/meminfo")
    if meminfo.is_file():
        try:
            for line in meminfo.read_text(encoding="ascii").splitlines():
                if line.startswith("MemTotal:"):
                    parts = line.split()
                    if len(parts) >= 2:
                        total = int(parts[1]) * 1024
                        if total > 0:
                            return total
        except (OSError, UnicodeError, ValueError):
            pass

    try:
        pages = int(os.sysconf("SC_PHYS_PAGES"))
        page_size = int(os.sysconf("SC_PAGE_SIZE"))
        total = pages * page_size
        return total if total > 0 else None
    except (AttributeError, OSError, TypeError, ValueError):
        return None


def _memory_limit_for_bytes(memory_bytes: Optional[int]) -> int:
    if memory_bytes is None:
        return MAX_ENVS_PER_ACTOR
    usable = max(0, memory_bytes - REMOTE_MEMORY_RESERVE_BYTES)
    return max(1, min(MAX_ENVS_PER_ACTOR, usable // REMOTE_MEMORY_PER_ENV_BYTES))


def _memory_env_limit() -> int:
    """Safe initial environment count based on memory that is free right now."""
    return _memory_limit_for_bytes(_available_memory_bytes())


def _memory_env_capacity_limit() -> int:
    """Stable auto-tuning ceiling based on installed physical memory."""
    return _memory_limit_for_bytes(_total_memory_bytes())


def _default_envs() -> int:
    cpu_limit = 4 * _available_cpu_threads()
    return min(MAX_ENVS_PER_ACTOR, cpu_limit, _memory_env_limit())


def _parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="Run one self-healing managed remote Bees rollout worker.")
    parser.add_argument(
        "--tailnet-bridge",
        required=True,
        help="Path to the bundled bees-tailnet-bridge executable.",
    )
    parser.add_argument("--tailnet-state", required=True)
    parser.add_argument("--tailnet-hostname", required=True)
    parser.add_argument(
        "--tailnet-target",
        required=True,
        help="Learner tailnet IPv4 address baked into the generated launcher.",
    )
    parser.add_argument(
        "--envs",
        type=int,
        default=None,
        help=(
            "Fixed Unity environment count (1-64). If omitted, BeesServer auto-tunes "
            "the count for useful steps/sec."
        ),
    )
    parser.add_argument(
        "--min-envs",
        type=int,
        default=1,
        help="Minimum environment count for automatic tuning (default 1).",
    )
    parser.add_argument(
        "--max-envs",
        type=int,
        default=None,
        help="Maximum environment count for automatic tuning (default RAM-derived cap, at most 64).",
    )
    parser.add_argument(
        "--gameplay-port",
        type=int,
        default=None,
        help=argparse.SUPPRESS,
    )
    parser.add_argument("--control-port", type=int, default=7150)
    parser.add_argument("--bootstrap-port", type=int, default=7151)
    parser.add_argument("--broker-port", type=int, default=55051)
    parser.add_argument("--install-root", required=True)
    parser.add_argument("--runtime-archive", required=True)
    parser.add_argument("--bootstrap-token-file", required=True)
    parser.add_argument("--worker-token-file", required=True)
    parser.add_argument("--wan-token-file", required=True)
    parser.add_argument("--runtime-poll-seconds", type=float, default=20.0)
    parser.add_argument("--torch-device", default="cpu")
    parser.add_argument("--reconnect-seconds", type=float, default=DEFAULT_RECONNECT_SECONDS)
    return parser


def _load_actor_key(install_root: Path) -> str:
    install_root.mkdir(parents=True, exist_ok=True)
    path = install_root / "actor-key.txt"
    try:
        existing = path.read_text(encoding="ascii").strip().lower()
    except FileNotFoundError:
        existing = ""
    if existing:
        if len(existing) != 32 or any(ch not in "0123456789abcdef" for ch in existing):
            raise ValueError(f"invalid persistent actor key: {path}")
        return existing

    candidate = uuid.uuid4().hex
    try:
        with path.open("x", encoding="ascii") as handle:
            handle.write(candidate + "\n")
    except FileExistsError:
        existing = path.read_text(encoding="ascii").strip().lower()
        if len(existing) != 32 or any(ch not in "0123456789abcdef" for ch in existing):
            raise ValueError(f"invalid persistent actor key: {path}")
        return existing
    return candidate


def _terminate(process: Optional[subprocess.Popen]) -> None:
    if process is None or process.poll() is not None:
        return
    if os.name == "nt":
        try:
            subprocess.run(
                ["taskkill", "/PID", str(process.pid), "/T", "/F"],
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
                check=False,
                timeout=10,
            )
        except Exception:
            pass
        try:
            process.wait(timeout=10)
            return
        except Exception:
            pass
    try:
        process.terminate()
    except Exception:
        pass
    # The POSIX owned-child guardian gets 10 seconds to stop its process tree before
    # escalating; wait beyond that window before killing the guardian itself.
    try:
        process.wait(timeout=15)
        return
    except Exception:
        pass
    try:
        process.kill()
    except Exception:
        pass
    try:
        process.wait(timeout=5)
        return
    except Exception as exc:
        raise RuntimeError(
            f"remote supervisor child process {process.pid} did not stop"
        ) from exc


def _worker_agent_stop_request_path(args: argparse.Namespace) -> Path:
    return (
        Path(args.install_root).expanduser().resolve()
        / "ManagedBuilds"
        / REMOTE_WORKER_AGENT_STOP_REQUEST_FILE
    )


def _request_graceful_worker_stop(
    process: Optional[subprocess.Popen],
    request_path: Path,
    *,
    timeout: float = 30.0,
) -> bool:
    if process is None or process.poll() is not None:
        return True
    try:
        request_path.parent.mkdir(parents=True, exist_ok=True)
        request_path.write_text("stop\n", encoding="ascii")
    except OSError:
        return False
    try:
        process.wait(timeout=timeout)
        return True
    except Exception:
        return False


def _wait_for_ports(
    ports: Sequence[int],
    process: subprocess.Popen,
    stop: list[bool],
    timeout: float = 20.0,
) -> bool:
    pending = set(int(port) for port in ports)
    deadline = time.monotonic() + timeout
    while not stop[0] and pending and time.monotonic() < deadline:
        if process.poll() is not None:
            return False
        for port in tuple(pending):
            try:
                with socket.create_connection(("127.0.0.1", port), timeout=0.4):
                    pending.remove(port)
            except OSError:
                pass
        if pending:
            time.sleep(0.2)
    return not pending


def _tailnet_forward_command(args: argparse.Namespace) -> list[str]:
    return [
        str(Path(args.tailnet_bridge).expanduser().resolve()),
        "forward-multi",
        "--state",
        str(Path(args.tailnet_state).expanduser().resolve()),
        "--hostname",
        args.tailnet_hostname,
        "--map",
        f"127.0.0.1:{args.control_port}={args.tailnet_target}:{args.control_port}",
        "--map",
        f"127.0.0.1:{args.broker_port}={args.tailnet_target}:{args.broker_port}",
        "--map",
        f"127.0.0.1:{args.bootstrap_port}={args.tailnet_target}:{args.bootstrap_port}",
    ]


def _sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _python_executable_path(path: str | Path) -> Path:
    # Do not resolve symlinks here. On Linux, a venv's bin/python is commonly a
    # symlink to the base interpreter; resolving it discards the venv context and
    # makes installed packages appear to be missing.
    return Path(os.path.abspath(os.fspath(path)))


def _decode_release_metadata(data: bytes) -> Mapping[str, object]:
    try:
        value = json.loads(data.decode("utf-8-sig"))
    except (UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise ValueError(f"bootstrap release metadata is invalid JSON: {exc}") from exc
    if not isinstance(value, Mapping):
        raise ValueError("bootstrap release metadata must be a JSON object")
    return value


def _valid_runtime_version(value: str) -> bool:
    return (
        len(value) in (40, 64)
        and all(ch in "0123456789abcdef" for ch in value)
    )


def _runtime_version_from_root(root: Path) -> str:
    path = root / "bees-runtime-version.txt"
    try:
        value = path.read_text(encoding="ascii").strip().lower()
    except OSError:
        return ""
    return value if _valid_runtime_version(value) else ""


def _runtime_version_from_zip(runtime_zip: bytes) -> str:
    try:
        with zipfile.ZipFile(io.BytesIO(runtime_zip), "r") as bundle:
            value = bundle.read("bees-runtime-version.txt").decode("ascii").strip().lower()
    except (KeyError, UnicodeDecodeError, zipfile.BadZipFile):
        return ""
    return value if _valid_runtime_version(value) else ""


def _atomic_bytes(path: Path, data: bytes, mode: int = 0o600) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(path.name + ".tmp")
    temporary.write_bytes(data)
    try:
        os.chmod(temporary, mode)
    except OSError:
        pass
    os.replace(temporary, path)


def _watch_shutdown_request(
    path: Path,
    stop: list[bool],
    poll_seconds: float = 0.25,
) -> None:
    """Turn a launcher stop request into the supervisor's normal cleanup path."""
    while not stop[0]:
        if path.is_file():
            stop[0] = True
            return
        time.sleep(poll_seconds)


def _clear_pid_file_if_owned(path: Path, pid: int) -> None:
    try:
        recorded = path.read_text(encoding="ascii").strip()
    except OSError:
        return
    if recorded != str(pid):
        return
    try:
        path.unlink()
    except OSError:
        pass


def _safe_extract_runtime(runtime_zip: bytes, destination: Path) -> None:
    temporary = destination.with_name(destination.name + ".tmp")
    if temporary.exists():
        import shutil
        shutil.rmtree(temporary)
    temporary.mkdir(parents=True, exist_ok=False)
    try:
        with zipfile.ZipFile(io.BytesIO(runtime_zip), "r") as bundle:
            root = temporary.resolve()
            for member in bundle.infolist():
                normalized = member.filename.replace("\\", "/")
                if (
                    not normalized
                    or normalized.startswith("/")
                    or normalized.startswith("../")
                    or "/../" in normalized
                ):
                    raise ValueError(f"unsafe runtime member: {member.filename!r}")
                target = (temporary / normalized).resolve()
                target.relative_to(root)
                if member.is_dir():
                    target.mkdir(parents=True, exist_ok=True)
                    continue
                target.parent.mkdir(parents=True, exist_ok=True)
                with bundle.open(member, "r") as source, target.open("wb") as output:
                    output.write(source.read())
        if destination.exists():
            import shutil
            shutil.rmtree(destination)
        os.replace(temporary, destination)
    finally:
        if temporary.exists():
            import shutil
            shutil.rmtree(temporary, ignore_errors=True)


def _runtime_root_matches_archive(runtime_zip: bytes, runtime_root: Path) -> bool:
    """Verify a cached extracted runtime against every member of its source archive."""
    if not runtime_root.is_dir() or runtime_root.is_symlink():
        return False

    expected_files = set()
    expected_directories = set()
    try:
        with zipfile.ZipFile(io.BytesIO(runtime_zip), "r") as bundle:
            for member in bundle.infolist():
                normalized = member.filename.replace("\\", "/")
                if (
                    not normalized
                    or normalized.startswith("/")
                    or normalized.startswith("../")
                    or "/../" in normalized
                ):
                    return False
                target = runtime_root / normalized
                try:
                    target.resolve().relative_to(runtime_root.resolve())
                except (OSError, ValueError):
                    return False
                relative = Path(normalized)
                if member.is_dir():
                    expected_directories.add(relative.as_posix().rstrip("/"))
                    continue
                expected_files.add(relative.as_posix())
                expected_directories.update(
                    parent.as_posix()
                    for parent in relative.parents
                    if parent != Path(".")
                )
                if target.is_symlink() or not target.is_file():
                    return False
                expected_digest = hashlib.sha256()
                actual_digest = hashlib.sha256()
                with bundle.open(member, "r") as source, target.open("rb") as actual:
                    while True:
                        expected = source.read(1024 * 1024)
                        actual_chunk = actual.read(1024 * 1024)
                        if not expected and not actual_chunk:
                            break
                        if len(expected) != len(actual_chunk):
                            return False
                        expected_digest.update(expected)
                        actual_digest.update(actual_chunk)
                    if expected_digest.digest() != actual_digest.digest():
                        return False

        actual_files = set()
        actual_directories = set()
        for current, directories, files in os.walk(runtime_root, followlinks=False):
            current_path = Path(current)
            for name in directories:
                child = current_path / name
                if child.is_symlink():
                    return False
                actual_directories.add(child.relative_to(runtime_root).as_posix())
            for name in files:
                child = current_path / name
                if child.is_symlink() or not child.is_file():
                    return False
                actual_files.add(child.relative_to(runtime_root).as_posix())
        return actual_files == expected_files and actual_directories == expected_directories
    except (OSError, ValueError, zipfile.BadZipFile):
        return False


def _direct_version_root(root: Path, path: Path) -> Optional[Path]:
    # Do not resolve interpreter symlinks here. Linux venv/bin/python commonly points at
    # a base interpreter outside the venv; resolving it would make an active managed venv
    # look unrelated to VenvVersions and eligible for deletion.
    root_absolute = Path(os.path.abspath(os.fspath(root)))
    path_absolute = Path(os.path.abspath(os.fspath(path)))
    try:
        relative = path_absolute.relative_to(root_absolute)
    except ValueError:
        return None
    if not relative.parts:
        return None
    return root_absolute / relative.parts[0]


def _prune_version_directories(
    root: Path,
    *,
    preserve_paths: Sequence[Path],
    retain: int,
) -> None:
    if retain < 1 or not root.is_dir():
        return
    try:
        resolved_root = root.resolve()
    except OSError:
        return

    keep = set()
    for path in preserve_paths:
        candidate = _direct_version_root(root, Path(path))
        if candidate is not None:
            try:
                resolved_candidate = candidate.resolve()
            except OSError:
                continue
            if resolved_candidate.parent == resolved_root:
                keep.add(resolved_candidate)

    candidates = []
    try:
        children = list(resolved_root.iterdir())
    except OSError:
        return
    for child in children:
        if child.name.endswith(".tmp"):
            continue
        try:
            resolved_child = child.resolve()
            # Prune only actual version directories directly inside the managed root.
            # Resolving symlinks/junctions for deletion could otherwise escape this root.
            if (
                resolved_child != resolved_root / child.name
                or resolved_child.parent != resolved_root
                or not resolved_child.is_dir()
            ):
                continue
            candidates.append((resolved_child.stat().st_mtime_ns, resolved_child))
        except OSError:
            continue
    candidates.sort(reverse=True)
    keep.update(path for _, path in candidates[:retain])

    for _, path in candidates:
        if path in keep:
            continue
        shutil.rmtree(path, ignore_errors=True)


def _is_transient_transport_error(exc: BaseException) -> bool:
    if isinstance(exc, (ConnectionResetError, ConnectionAbortedError, TimeoutError)):
        return True
    if isinstance(exc, urllib.error.URLError):
        reason = getattr(exc, "reason", None)
        return isinstance(reason, BaseException) and _is_transient_transport_error(reason)
    if isinstance(exc, OSError):
        code = getattr(exc, "winerror", None)
        if code in {10053, 10054, 10060, 10061, 10064}:
            return True
        if getattr(exc, "errno", None) in {32, 54, 60, 61, 104, 110, 111}:
            return True
    return False


class RuntimeUpdater:
    def __init__(self, args: argparse.Namespace, install_root: Path) -> None:
        self.args = args
        self.install_root = install_root
        self.versions_root = install_root / "RuntimeVersions"
        self.versions_root.mkdir(parents=True, exist_ok=True)
        self.ready_build_path = install_root / "runtime-ready-build.txt"
        self._stop = threading.Event()
        self._refresh = threading.Event()
        self._lock = threading.Lock()
        self._thread = threading.Thread(target=self._run, name="bees-runtime-updater", daemon=True)
        self._started = False
        self._staging_process: Optional[subprocess.Popen] = None
        archive = Path(args.runtime_archive).expanduser().resolve()
        self.current_sha256 = _sha256_file(archive) if archive.is_file() else ""
        self.current_version = _runtime_version_from_root(Path(__file__).resolve().parent)
        self.staged_sha256 = ""
        self.staged_root: Optional[Path] = None
        self.staged_bridge: Optional[Path] = None
        self.staged_python: Optional[Path] = None
        self.staged_build_id = ""
        self.verified_build_id = ""
        self.last_error = ""

    def start(self) -> None:
        if self._started:
            self.request_refresh()
            return
        self._thread.start()
        self._started = True

    def request_refresh(self) -> None:
        self._refresh.set()

    def stop(self) -> None:
        self._stop.set()
        self._refresh.set()
        if self._started:
            # Cutover must not replace staged files while this thread is still writing them.
            # Bootstrap requests have finite timeouts; staging subprocesses are terminated
            # cooperatively below before this join completes.
            self._thread.join()
            with self._lock:
                active = self._staging_process
            if active is not None and active.poll() is None:
                _terminate(active)
                if active.poll() is None:
                    raise RuntimeError(
                        "runtime updater staging subprocess remained alive after stop"
                    )

    def verified(self) -> tuple[str, str]:
        with self._lock:
            return self.verified_build_id, self.last_error

    def staged(
        self,
    ) -> tuple[str, Optional[Path], Optional[Path], Optional[Path], str, str]:
        with self._lock:
            return (
                self.staged_sha256,
                self.staged_root,
                self.staged_bridge,
                self.staged_python,
                self.staged_build_id,
                self.last_error,
            )

    def _bootstrap_token(self) -> str:
        value = Path(self.args.bootstrap_token_file).expanduser().read_text(encoding="ascii").strip()
        if not value:
            raise ValueError("bootstrap token is empty")
        return value

    def _run_staging_command(
        self,
        command: Sequence[str | Path],
        *,
        stdout=None,
        stderr=None,
    ) -> int:
        if self._stop.is_set():
            raise RuntimeError("runtime updater stopped before a staging subprocess launched")
        process = popen_owned(
            [str(value) for value in command],
            stdout=stdout,
            stderr=stderr,
        )
        with self._lock:
            self._staging_process = process
        try:
            while process.poll() is None:
                if self._stop.wait(0.25):
                    _terminate(process)
                    if process.poll() is None:
                        raise RuntimeError(
                            "runtime updater could not stop its staging subprocess"
                        )
                    raise RuntimeError(
                        "runtime updater stopped during a staging subprocess"
                    )
            return int(process.returncode)
        finally:
            if process.poll() is not None:
                with self._lock:
                    if self._staging_process is process:
                        self._staging_process = None

    def _python_remote_dependencies_ok(self, python_path: Path) -> bool:
        return self._run_staging_command(
            [
                python_path,
                "-c",
                "import pkg_resources, mlagents, torch, numpy",
            ],
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
        ) == 0

    def _fetch_bootstrap(self) -> tuple[bytes, bytes, bytes, bytes, bytes]:
        maximum_attempts = 4
        last_error: Optional[Exception] = None
        for attempt in range(1, maximum_attempts + 1):
            request = urllib.request.Request(
                f"http://127.0.0.1:{self.args.bootstrap_port}/bootstrap",
                method="GET",
                headers={"Authorization": "Bearer " + self._bootstrap_token()},
            )
            try:
                with urllib.request.urlopen(request, timeout=120.0) as response:
                    outer = response.read()
                with zipfile.ZipFile(io.BytesIO(outer), "r") as bundle:
                    bridge_name = (
                        "bees-tailnet-bridge-windows.exe"
                        if os.name == "nt"
                        else "bees-tailnet-bridge-linux"
                    )
                    return (
                        bundle.read("bees-remote-runtime.zip"),
                        bundle.read("training-worker.token"),
                        bundle.read("wan.token"),
                        bundle.read(bridge_name),
                        bundle.read("latest-training-release.json"),
                    )
            except Exception as exc:
                if not _is_transient_transport_error(exc) or attempt >= maximum_attempts:
                    raise
                last_error = exc
                delay = 0.5 * attempt
                print(
                    "[Bees remote] bootstrap transport reset during learner cutover; "
                    f"retrying attempt {attempt + 1}/{maximum_attempts} in {delay:.1f}s.",
                    file=sys.stderr,
                    flush=True,
                )
                if self._stop.wait(delay):
                    raise RuntimeError("runtime updater stopped during bootstrap retry") from exc
        assert last_error is not None
        raise last_error

    def _prepare_python_for_requirements(
        self,
        runtime_root: Path,
    ) -> Path:
        requirements = runtime_root / "bees_remote_requirements.txt"
        if not requirements.is_file():
            return _python_executable_path(sys.executable)

        active_requirements = Path(__file__).resolve().parent / "bees_remote_requirements.txt"
        new_hash = _sha256_file(requirements)
        active_hash = _sha256_file(active_requirements) if active_requirements.is_file() else ""
        if new_hash == active_hash and self._python_remote_dependencies_ok(Path(sys.executable)):
            return _python_executable_path(sys.executable)

        venv_root = self.install_root / "VenvVersions" / new_hash
        python_path = (
            venv_root / "Scripts" / "python.exe"
            if os.name == "nt"
            else venv_root / "bin" / "python"
        )
        if python_path.is_file():
            if self._python_remote_dependencies_ok(python_path):
                return _python_executable_path(python_path)
            shutil.rmtree(venv_root, ignore_errors=True)

        venv_root.parent.mkdir(parents=True, exist_ok=True)
        temporary = venv_root.with_name(venv_root.name + ".tmp")
        if temporary.exists():
            shutil.rmtree(temporary)

        if os.name == "nt":
            completed = self._run_staging_command(
                [sys.executable, "-m", "venv", str(temporary)],
            )
            if completed != 0:
                raise RuntimeError(
                    "failed to create staged Windows Python environment "
                    f"(exit {completed})"
                )
            staged_python = temporary / "Scripts" / "python.exe"
            completed = self._run_staging_command(
                [
                    staged_python,
                    "-m",
                    "pip",
                    "install",
                    "-r",
                    requirements,
                ],
            )
        else:
            uv = shutil.which("uv")
            if not uv:
                candidate = Path.home() / ".local" / "bin" / "uv"
                uv = str(candidate) if candidate.is_file() else ""
            if not uv:
                raise RuntimeError(
                    "changed remote requirements need uv on Linux; rerun the generated "
                    "launcher once to restore the managed uv installation"
                )
            completed = self._run_staging_command(
                [uv, "venv", "--python", "3.10", temporary],
            )
            if completed != 0:
                raise RuntimeError(
                    "failed to create staged Linux Python environment "
                    f"(exit {completed})"
                )
            staged_python = temporary / "bin" / "python"
            completed = self._run_staging_command(
                [uv, "pip", "install", "--python", staged_python, "-r", requirements],
            )

        if completed != 0:
            shutil.rmtree(temporary, ignore_errors=True)
            raise RuntimeError(
                "staged remote dependency installation failed "
                f"(exit {completed})"
            )
        if not self._python_remote_dependencies_ok(staged_python):
            shutil.rmtree(temporary, ignore_errors=True)
            raise RuntimeError(
                "staged remote dependency validation failed; "
                "pkg_resources/ML-Agents runtime is incomplete"
            )
        if venv_root.exists():
            shutil.rmtree(venv_root)
        os.replace(temporary, venv_root)
        python_path = (
            venv_root / "Scripts" / "python.exe"
            if os.name == "nt"
            else venv_root / "bin" / "python"
        )
        if not python_path.is_file():
            raise RuntimeError("staged Python environment is missing its interpreter")
        if not self._python_remote_dependencies_ok(python_path):
            shutil.rmtree(venv_root, ignore_errors=True)
            raise RuntimeError(
                "staged remote dependency validation failed after activation path move"
            )
        return _python_executable_path(python_path)

    def _stage_once(self) -> None:
        runtime_zip, worker_token, wan_token, bridge_bytes, release_bytes = self._fetch_bootstrap()
        runtime_sha = hashlib.sha256(runtime_zip).hexdigest()
        runtime_version = _runtime_version_from_zip(runtime_zip)
        release = _decode_release_metadata(release_bytes)
        staged_build_id = str(release.get("build_id", ""))
        if not staged_build_id:
            raise ValueError("bootstrap release metadata has no build_id")

        release_runtime = release.get("training_runtime")
        if release_runtime is not None:
            if not isinstance(release_runtime, Mapping):
                raise ValueError("bootstrap release training_runtime metadata is invalid")
            expected_runtime_sha = str(
                release_runtime.get("archive_sha256", "")
            ).strip().lower()
            expected_runtime_version = str(
                release_runtime.get("runtime_version", "")
            ).strip().lower()
            if (
                len(expected_runtime_sha) != 64
                or any(ch not in "0123456789abcdef" for ch in expected_runtime_sha)
            ):
                raise ValueError("bootstrap release training runtime SHA-256 is invalid")
            if not _valid_runtime_version(expected_runtime_version):
                raise ValueError("bootstrap release training runtime version is invalid")
            if runtime_sha != expected_runtime_sha:
                raise ValueError(
                    "downloaded training runtime SHA-256 does not match release metadata"
                )
            if runtime_version != expected_runtime_version:
                raise ValueError(
                    "downloaded training runtime version does not match release metadata"
                )

        bridge_path = Path(self.args.tailnet_bridge).expanduser().resolve()
        bridge_sha = hashlib.sha256(bridge_bytes).hexdigest()
        current_bridge_sha = _sha256_file(bridge_path) if bridge_path.is_file() else ""
        _atomic_bytes(Path(self.args.worker_token_file).expanduser().resolve(), worker_token)
        _atomic_bytes(Path(self.args.wan_token_file).expanduser().resolve(), wan_token)

        if runtime_version:
            # Compare against the code this process is actually executing. The downloaded
            # archive may already contain a newer runtime that has not been activated yet.
            runtime_changed = runtime_version != self.current_version
        else:
            # Backward compatibility for runtimes produced before explicit version markers.
            runtime_changed = runtime_sha != self.current_sha256
        bridge_changed = bridge_sha != current_bridge_sha
        active_dependencies_ok = self._python_remote_dependencies_ok(Path(sys.executable))
        with self._lock:
            if (
                not runtime_changed
                and not bridge_changed
                and active_dependencies_ok
            ):
                self.staged_sha256 = ""
                self.staged_root = None
                self.staged_bridge = None
                self.staged_python = None
                _atomic_bytes(
                    self.ready_build_path,
                    (staged_build_id + "\n").encode("ascii"),
                    0o600,
                )
                self.staged_build_id = ""
                self.verified_build_id = staged_build_id
                self.last_error = ""
                return
            staged_bridge_matches = (
                self.staged_bridge is None and not bridge_changed
            ) or (
                self.staged_bridge is not None
                and self.staged_bridge.is_file()
                and _sha256_file(self.staged_bridge) == bridge_sha
            )
            if (
                runtime_sha == self.staged_sha256
                and self.staged_root is not None
                and staged_build_id == self.staged_build_id
                and staged_bridge_matches
            ):
                return

        destination = self.versions_root / runtime_sha
        if runtime_changed and not _runtime_root_matches_archive(runtime_zip, destination):
            _safe_extract_runtime(runtime_zip, destination)
        runtime_root = destination if runtime_changed else Path(__file__).resolve().parent
        staged_python = self._prepare_python_for_requirements(runtime_root)
        if runtime_changed:
            archive = self.install_root / "Downloads" / f"bees-remote-runtime-{runtime_sha}.zip"
            _atomic_bytes(archive, runtime_zip, 0o644)
            _atomic_bytes(Path(self.args.runtime_archive).expanduser().resolve(), runtime_zip, 0o644)

        staged_bridge = None
        if bridge_changed:
            suffix = ".exe" if os.name == "nt" else ""
            staged_bridge = bridge_path.with_name("bees-tailnet-bridge.next" + suffix)
            _atomic_bytes(staged_bridge, bridge_bytes, 0o700)

        _atomic_bytes(
            self.ready_build_path,
            (staged_build_id + "\n").encode("ascii"),
            0o600,
        )
        with self._lock:
            self.staged_sha256 = runtime_sha
            self.staged_root = runtime_root
            self.staged_bridge = staged_bridge
            self.staged_python = staged_python
            self.staged_build_id = staged_build_id
            self.verified_build_id = staged_build_id
            self.last_error = ""
        _prune_version_directories(
            self.versions_root,
            preserve_paths=[Path(__file__).resolve().parent, runtime_root],
            retain=MAX_RETAINED_RUNTIME_VERSIONS,
        )
        _prune_version_directories(
            self.install_root / "VenvVersions",
            preserve_paths=[Path(sys.executable).absolute(), staged_python],
            retain=MAX_RETAINED_VENV_VERSIONS,
        )
        update_parts = [f"runtime={runtime_sha[:12]}", f"bridge={bridge_sha[:12]}"]
        if not active_dependencies_ok:
            update_parts.append("python-dependencies=repair")
        print("[Bees remote] staged worker update " + " ".join(update_parts) + ".")

    def _run(self) -> None:
        while not self._stop.is_set():
            try:
                self._stage_once()
            except (OSError, ValueError, RuntimeError, KeyError, urllib.error.URLError, zipfile.BadZipFile) as exc:
                with self._lock:
                    self.last_error = f"{type(exc).__name__}: {exc}"
            self._refresh.wait(self.args.runtime_poll_seconds)
            self._refresh.clear()


def _control_state(args: argparse.Namespace, trainer_id: str) -> Optional[Mapping[str, object]]:
    try:
        token = Path(args.worker_token_file).expanduser().read_text(encoding="utf-8").strip()
        query = urllib.parse.urlencode({
            "trainer_id": trainer_id,
            "role": "dedicated",
            "platform": "WindowsPlayer" if os.name == "nt" else "LinuxPlayer",
        })
        request = urllib.request.Request(
            f"http://127.0.0.1:{args.control_port}/v1/state?{query}",
            headers={"Authorization": "Bearer " + token},
        )
        with urllib.request.urlopen(request, timeout=3.0) as response:
            value = json.loads(response.read().decode("utf-8"))
        return value if isinstance(value, Mapping) else None
    except (OSError, ValueError, json.JSONDecodeError, urllib.error.URLError):
        return None


def _control_status(args: argparse.Namespace) -> Optional[Mapping[str, object]]:
    try:
        token = Path(args.worker_token_file).expanduser().read_text(encoding="utf-8").strip()
        request = urllib.request.Request(
            f"http://127.0.0.1:{args.control_port}/v1/status",
            headers={"Authorization": "Bearer " + token},
        )
        with urllib.request.urlopen(request, timeout=3.0) as response:
            value = json.loads(response.read().decode("utf-8"))
        return value if isinstance(value, Mapping) else None
    except (OSError, ValueError, json.JSONDecodeError, urllib.error.URLError):
        return None


def _wait_for_private_transport(
    args: argparse.Namespace,
    process: subprocess.Popen,
    stop: list[bool],
    timeout: float = 30.0,
    required_successes: int = 2,
) -> bool:
    if required_successes < 1:
        raise ValueError("required_successes must be positive")

    if not _wait_for_ports(
        (args.control_port, args.broker_port, args.bootstrap_port),
        process,
        stop,
        timeout=min(timeout, 20.0),
    ):
        return False

    consecutive = 0
    deadline = time.monotonic() + timeout
    while not stop[0] and time.monotonic() < deadline:
        if process.poll() is not None:
            return False
        if isinstance(_control_status(args), Mapping):
            consecutive += 1
            if consecutive >= required_successes:
                return True
        else:
            consecutive = 0
        time.sleep(0.5)
    return False


def _remote_status_summary(
    args: argparse.Namespace,
    trainer_id: str,
    updater: Optional[RuntimeUpdater] = None,
    log_sink: Optional[_RunScopedLogSink] = None,
) -> str:
    status = _control_status(args)
    if not isinstance(status, Mapping):
        return (
            f"[Bees remote] status: connecting to learner; trainer={trainer_id} "
            f"envs={args.envs}"
        )

    desired = status.get("desired")
    desired_map = desired if isinstance(desired, Mapping) else {}
    if log_sink is not None:
        log_sink.set_run_id(str(desired_map.get("run_id", "") or ""))
    trainers = status.get("trainers")
    record: Optional[Mapping[str, object]] = None
    if isinstance(trainers, list):
        for candidate in trainers:
            if isinstance(candidate, Mapping) and candidate.get("trainer_id") == trainer_id:
                record = candidate
                break

    if record is None:
        build_id = str(desired_map.get("canonical_build_id", "") or "-")
        return (
            f"[Bees remote] status: learner connected; waiting for trainer registration; "
            f"trainer={trainer_id} envs={args.envs} build={build_id}"
        )

    state = str(record.get("process_state", "") or "unknown")
    if bool(record.get("stale", False)):
        state = "STALE"
    build_id = str(record.get("build_id", "") or desired_map.get("canonical_build_id", "") or "-")
    revision = record.get("applied_revision", "-")
    error = str(record.get("last_error", "") or "")
    suffix = f" error={error}" if error else ""
    capacity = record.get("worker_capacity")
    capacity_map = capacity if isinstance(capacity, Mapping) else {}
    current_envs = capacity_map.get("current_envs", args.envs)
    optimizer = record.get("env_optimizer")
    optimizer_map = optimizer if isinstance(optimizer, Mapping) else {}
    desired_envs = optimizer_map.get("desired_envs", current_envs)
    phase = str(optimizer_map.get("phase", "") or "")
    measured_sps = optimizer_map.get("measured_sps")
    baseline_sps = optimizer_map.get("baseline_sps")
    sps = measured_sps if isinstance(measured_sps, (int, float)) else baseline_sps
    env_status = str(current_envs)
    if isinstance(desired_envs, int) and desired_envs != current_envs:
        env_status += f"->{desired_envs}"
    optimizer_suffix = f" optimizer={phase}" if phase else ""
    if isinstance(sps, (int, float)):
        optimizer_suffix += f" learner_sps={float(sps):.1f}"
    runtime_suffix = ""
    if updater is not None:
        _sha, staged_root, _bridge, _python, _build, update_error = updater.staged()
        if update_error:
            runtime_suffix = f" runtime_update_error={update_error}"
        elif staged_root is not None:
            runtime_suffix = f" runtime_update=staged:{staged_root.name[:12]}"
    return (
        f"[Bees remote] status: learner=connected trainer={trainer_id} "
        f"state={state} envs={env_status} build={build_id} rev={revision}"
        f"{optimizer_suffix}{suffix}{runtime_suffix}"
    )


def _runtime_cutover_selected(
    args: argparse.Namespace,
    trainer_id: str,
    updater: RuntimeUpdater,
) -> Optional[Path]:
    _sha, staged_root, _staged_bridge, _staged_python, staged_build_id, _error = updater.staged()
    if staged_root is None:
        return None
    state = _control_state(args, trainer_id)
    if not state:
        return None
    pending = state.get("pending_release")
    if not isinstance(pending, Mapping):
        if staged_build_id and str(state.get("canonical_build_id", "")) == staged_build_id:
            return staged_root
        return None
    pending_build = str(pending.get("build_id", ""))
    if staged_build_id and staged_build_id != pending_build:
        return None
    phase = str(pending.get("phase", ""))
    incompatible = bool(pending.get("incompatible", False))
    if phase == "rolling" and str(state.get("desired_build_id", "")) == pending_build:
        return staged_root
    if incompatible and phase == "stopping" and state.get("desired_mode") == "stopped":
        phase_revision = pending.get("phase_revision")
        if (
            not isinstance(phase_revision, int)
            or isinstance(phase_revision, bool)
            or phase_revision < 0
        ):
            return None
        status = _control_status(args)
        trainers = status.get("trainers", ()) if isinstance(status, Mapping) else ()
        if isinstance(trainers, list):
            for record in trainers:
                applied_revision = record.get("applied_revision") if isinstance(record, Mapping) else None
                if (
                    isinstance(record, Mapping)
                    and record.get("trainer_id") == trainer_id
                    and record.get("stale") is False
                    and record.get("process_state") == "stopped"
                    and isinstance(applied_revision, int)
                    and not isinstance(applied_revision, bool)
                    and applied_revision >= phase_revision
                ):
                    return staged_root
    return None


def _wait_for_runtime_alignment(
    args: argparse.Namespace,
    trainer_id: str,
    updater: RuntimeUpdater,
    tailnet: subprocess.Popen,
    stop: list[bool],
) -> tuple[bool, Optional[Path]]:
    """Fail closed until this supervisor has verified the canonical Training runtime."""

    updater.start()
    next_status = 0.0
    while not stop[0] and tailnet.poll() is None:
        runtime_cutover = _runtime_cutover_selected(args, trainer_id, updater)
        if runtime_cutover is not None:
            return True, runtime_cutover

        status = _control_status(args)
        desired = status.get("desired") if isinstance(status, Mapping) else None
        canonical_build = (
            str(desired.get("canonical_build_id", "") or "")
            if isinstance(desired, Mapping)
            else ""
        )
        verified_build, update_error = updater.verified()
        _sha, staged_root, _bridge, _python, _staged_build, _staged_error = updater.staged()
        if (
            canonical_build
            and verified_build == canonical_build
            and staged_root is None
        ):
            return True, None

        updater.request_refresh()
        now = time.monotonic()
        if now >= next_status:
            waiting_for = canonical_build or "(canonical build unavailable)"
            suffix = f" error={update_error}" if update_error else ""
            print(
                "[Bees remote] waiting for matching Training runtime before rollout: "
                f"canonical={waiting_for} verified={verified_build or '-'}{suffix}",
                file=sys.stderr,
                flush=True,
            )
            next_status = now + 5.0
        time.sleep(0.5)
    return False, None


def _worker_command(args: argparse.Namespace, root: Path, actor_key: str) -> list[str]:
    trainer_id = f"remote-{socket.gethostname().lower()}-{actor_key[:8]}"
    command = [
        sys.executable,
        "-u",
        str(root / "bees_training_worker_agent.py"),
        "--server-url",
        f"http://127.0.0.1:{args.control_port}",
        "--token-file",
        str(Path(args.worker_token_file).expanduser().resolve()),
        "--trainer-id",
        trainer_id,
        "--role",
        "dedicated",
        "--platform",
        "WindowsPlayer" if os.name == "nt" else "LinuxPlayer",
        "--install-root",
        str(Path(args.install_root).expanduser().resolve() / "ManagedBuilds"),
        "--runtime-ready-file",
        str(Path(args.install_root).expanduser().resolve() / "runtime-ready-build.txt"),
        "--shutdown-request-file",
        str(_worker_agent_stop_request_path(args)),
        "--worker-envs",
        str(args.envs),
        "--worker-envs-min",
        str(args.min_envs),
        "--worker-envs-max",
        str(args.max_envs),
    ]
    if args.auto_envs:
        command.append("--auto-worker-envs")
    command.extend([
        "--",
        sys.executable,
        str(root / "bees_elastic_wan_actor_worker.py"),
        "--actor-key",
        actor_key,
        "--envs",
        "{worker_envs}",
        "--broker-host",
        "127.0.0.1",
        "--broker-port",
        str(args.broker_port),
        "--env",
        "{env}",
        "--auth-token-file",
        str(Path(args.wan_token_file).expanduser().resolve()),
        "--torch-device",
        args.torch_device,
    ])
    return command


def main(argv: Optional[Sequence[str]] = None) -> int:
    raw_argv = list(sys.argv[1:] if argv is None else argv)
    args = _parser().parse_args(raw_argv)
    memory_start_cap = _memory_env_limit()
    memory_capacity_cap = _memory_env_capacity_limit()
    if args.envs is None:
        args.auto_envs = True
        requested_max = MAX_ENVS_PER_ACTOR if args.max_envs is None else args.max_envs
        if not 1 <= args.min_envs <= requested_max <= MAX_ENVS_PER_ACTOR:
            print("error: automatic env bounds must satisfy 1 <= min <= max <= 64", file=sys.stderr)
            return 2
        # Free RAM is intentionally only a startup throttle. The advertised ceiling must
        # reflect stable machine capacity so a transient low-memory moment cannot pin the
        # server optimizer to one environment for the lifetime of this supervisor.
        args.max_envs = min(requested_max, memory_capacity_cap)
        if args.min_envs > args.max_envs:
            print(
                f"error: --min-envs={args.min_envs} exceeds the RAM-derived cap "
                f"of {args.max_envs}",
                file=sys.stderr,
            )
            return 2
        args.envs = max(args.min_envs, min(_default_envs(), args.max_envs))
        print(
            f"[Bees remote] --envs omitted; auto optimizer enabled at {args.envs} envs "
            f"(range={args.min_envs}-{args.max_envs} cpu_start={4 * _available_cpu_threads()} "
            f"memory_start_cap={memory_start_cap} memory_capacity_cap={memory_capacity_cap} "
            f"hard_cap={MAX_ENVS_PER_ACTOR})."
        )
    else:
        args.auto_envs = False
        if not 1 <= args.envs <= MAX_ENVS_PER_ACTOR:
            print(f"error: --envs must be in 1-{MAX_ENVS_PER_ACTOR}", file=sys.stderr)
            return 2
        args.min_envs = args.envs
        args.max_envs = args.envs
    if (
        not 1 <= args.control_port <= 65535
        or not 1 <= args.bootstrap_port <= 65535
        or not 1 <= args.broker_port <= 65535
    ):
        print("error: control/bootstrap/broker ports must be in 1-65535", file=sys.stderr)
        return 2
    if len({args.control_port, args.bootstrap_port, args.broker_port}) != 3:
        print("error: control/bootstrap/broker ports must be distinct", file=sys.stderr)
        return 2
    if (
        not math.isfinite(args.reconnect_seconds)
        or not math.isfinite(args.runtime_poll_seconds)
        or args.reconnect_seconds <= 0
        or args.runtime_poll_seconds <= 0
    ):
        print("error: reconnect/runtime-poll seconds must be positive", file=sys.stderr)
        return 2
    if not str(args.tailnet_target).strip():
        print("error: --tailnet-target is required", file=sys.stderr)
        return 2

    bridge = Path(args.tailnet_bridge).expanduser().resolve()
    if not bridge.is_file():
        print(f"error: bundled tailnet bridge does not exist: {bridge}", file=sys.stderr)
        return 2

    root = Path(__file__).resolve().parent
    for required in ("bees_training_worker_agent.py", "bees_elastic_wan_actor_worker.py"):
        if not (root / required).is_file():
            print(f"error: remote runtime is missing {required}", file=sys.stderr)
            return 2

    install_root = Path(args.install_root).expanduser().resolve()
    try:
        actor_key = _load_actor_key(install_root)
    except (OSError, ValueError) as exc:
        print(f"error: could not establish remote actor identity: {exc}", file=sys.stderr)
        return 2

    pid_file = install_root / REMOTE_PID_FILE
    shutdown_request_file = install_root / REMOTE_STOP_REQUEST_FILE
    try:
        _atomic_bytes(pid_file, f"{os.getpid()}\n".encode("ascii"), 0o600)
    except OSError as exc:
        print(f"error: could not record remote supervisor PID: {exc}", file=sys.stderr)
        return 2

    trainer_id = f"remote-{socket.gethostname().lower()}-{actor_key[:8]}"
    log_sink = _RunScopedLogSink(install_root / "ManagedBuilds" / "logs")
    original_stdout = sys.stdout
    original_stderr = sys.stderr
    sys.stdout = _RunScopedTee(original_stdout, log_sink)
    sys.stderr = _RunScopedTee(original_stderr, log_sink)
    updater: Optional[RuntimeUpdater] = None
    stop = [False]
    shutdown_watcher: Optional[threading.Thread] = None
    old_sigint = None
    old_sigterm = None

    def request_stop(_signum: int, _frame: object) -> None:
        stop[0] = True

    try:
        # The PID file and output tees are already active. Put every later startup
        # step under this cleanup scope so a RuntimeUpdater, watcher, or signal
        # initialization failure cannot leave stale supervisor state behind.
        updater = RuntimeUpdater(args, install_root)
        shutdown_watcher = threading.Thread(
            target=_watch_shutdown_request,
            args=(shutdown_request_file, stop),
            name="bees-remote-shutdown-watcher",
            daemon=True,
        )
        shutdown_watcher.start()
        old_sigint = signal.signal(signal.SIGINT, request_stop)
        old_sigterm = signal.signal(signal.SIGTERM, request_stop)

        while not stop[0]:
            tailnet: Optional[subprocess.Popen] = None
            worker: Optional[subprocess.Popen] = None
            worker_log_thread: Optional[threading.Thread] = None
            runtime_cutover: Optional[Path] = None
            try:
                tailnet = popen_owned(_tailnet_forward_command(args))
                if not _wait_for_private_transport(
                    args,
                    tailnet,
                    stop,
                ):
                    code = tailnet.poll()
                    print(
                        "[Bees remote] private transport failed to reach learner control"
                        + ("" if code is None else f" (exit {code})")
                        + ".",
                        file=sys.stderr,
                    )
                else:
                    print(
                        f"[Bees remote] private transport ready; identity={actor_key[:8]} "
                        f"envs={args.envs}; actor slot will be assigned by the learner."
                    )
                    print(
                        f"[Bees remote] managed worker launched; waiting for trainer registration "
                        f"and assigned build. trainer={trainer_id}",
                        flush=True,
                    )
                    current_status = _control_status(args)
                    if isinstance(current_status, Mapping):
                        current_desired = current_status.get("desired")
                        if isinstance(current_desired, Mapping):
                            log_sink.set_run_id(str(current_desired.get("run_id", "") or ""))

                    runtime_aligned, runtime_cutover = _wait_for_runtime_alignment(
                        args,
                        trainer_id,
                        updater,
                        tailnet,
                        stop,
                    )
                    if runtime_cutover is not None:
                        print(
                            f"[Bees remote] activating staged worker runtime "
                            f"{runtime_cutover.name[:12]} before rollout."
                        )
                    elif runtime_aligned and not stop[0] and tailnet.poll() is None:
                        worker, worker_log_thread = _start_logged_process(
                            _worker_command(args, root, actor_key)
                        )

                    next_status = 0.0
                    while (
                        runtime_cutover is None
                        and worker is not None
                        and not stop[0]
                        and tailnet.poll() is None
                        and worker.poll() is None
                    ):
                        now = time.monotonic()
                        if now >= next_status:
                            print(
                                _remote_status_summary(args, trainer_id, updater, log_sink),
                                flush=True,
                            )
                            next_status = now + 5.0
                        runtime_cutover = _runtime_cutover_selected(
                            args,
                            trainer_id,
                            updater,
                        )
                        if runtime_cutover is not None:
                            print(
                                f"[Bees remote] activating staged worker runtime "
                                f"{runtime_cutover.name[:12]} at this trainer's rollout turn."
                            )
                            break
                        time.sleep(0.5)
                    if not stop[0] and runtime_cutover is None:
                        if tailnet.poll() is not None:
                            print(
                                f"[Bees remote] tailnet transport exited ({tailnet.returncode}); restarting.",
                                file=sys.stderr,
                            )
                        elif worker is not None and worker.poll() is not None:
                            print(
                                f"[Bees remote] worker exited ({worker.returncode}); restarting.",
                                file=sys.stderr,
                            )
            except KeyboardInterrupt:
                stop[0] = True
            finally:
                termination_errors = []
                worker_stopped = _request_graceful_worker_stop(
                    worker,
                    _worker_agent_stop_request_path(args),
                )
                if not worker_stopped:
                    try:
                        _terminate(worker)
                    except RuntimeError as exc:
                        termination_errors.append(f"worker: {exc}")
                try:
                    _terminate(tailnet)
                except RuntimeError as exc:
                    termination_errors.append(f"tailnet: {exc}")
                if worker_log_thread is not None:
                    worker_log_thread.join(timeout=1.0)
                if termination_errors:
                    raise RuntimeError(
                        "remote supervisor cleanup could not confirm child shutdown: " +
                        "; ".join(termination_errors)
                    )

            if runtime_cutover is not None and not stop[0]:
                updater.stop()
                _sha, next_root, staged_bridge, staged_python, _staged_build_id, _error = updater.staged()
                if next_root is None:
                    raise RuntimeError("staged runtime disappeared before activation")
                active_runtime_root = Path(__file__).resolve().parent
                if next_root != active_runtime_root:
                    archive = (
                        install_root
                        / "Downloads"
                        / f"bees-remote-runtime-{_sha}.zip"
                    )
                    if not archive.is_file() or _sha256_file(archive) != _sha:
                        raise RuntimeError(
                            "staged runtime archive is missing or changed before activation"
                        )
                    runtime_zip = archive.read_bytes()
                    if not _runtime_root_matches_archive(runtime_zip, next_root):
                        _safe_extract_runtime(runtime_zip, next_root)
                        if not _runtime_root_matches_archive(runtime_zip, next_root):
                            raise RuntimeError(
                                "staged runtime contents could not be restored before activation"
                            )
                if staged_bridge is not None:
                    active_bridge = Path(args.tailnet_bridge).expanduser().resolve()
                    os.replace(staged_bridge, active_bridge)
                    try:
                        os.chmod(active_bridge, 0o700)
                    except OSError:
                        pass
                next_script = next_root / "bees_managed_remote_worker.py"
                if not next_script.is_file():
                    raise RuntimeError(f"staged runtime is missing {next_script}")
                next_python = str(staged_python or _python_executable_path(sys.executable))
                os.execv(
                    next_python,
                    [next_python, str(next_script), *raw_argv],
                )

            if not stop[0]:
                time.sleep(args.reconnect_seconds)
        return 0
    finally:
        stop[0] = True
        if shutdown_watcher is not None and shutdown_watcher.is_alive():
            shutdown_watcher.join(timeout=1.0)
        if updater is not None:
            updater.stop()
        try:
            shutdown_request_file.unlink()
        except OSError:
            pass
        _clear_pid_file_if_owned(pid_file, os.getpid())
        if old_sigint is not None:
            signal.signal(signal.SIGINT, old_sigint)
        if old_sigterm is not None:
            signal.signal(signal.SIGTERM, old_sigterm)
        sys.stdout = original_stdout
        sys.stderr = original_stderr


if __name__ == "__main__":
    raise SystemExit(main())
