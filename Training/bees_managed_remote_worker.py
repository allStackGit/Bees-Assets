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
import os
from pathlib import Path
import signal
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


DEFAULT_RECONNECT_SECONDS = 5.0
MAX_ENVS_PER_ACTOR = 64


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


def _default_envs() -> int:
    return min(MAX_ENVS_PER_ACTOR, 4 * _available_cpu_threads())


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
        help="Unity environment count (1-64). Default: 4x available CPU threads, capped at 64.",
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
            )
            process.wait(timeout=10)
            return
        except Exception:
            pass
    try:
        process.terminate()
        process.wait(timeout=10)
    except Exception:
        try:
            process.kill()
            process.wait(timeout=5)
        except Exception:
            pass


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


def _atomic_bytes(path: Path, data: bytes, mode: int = 0o600) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(path.name + ".tmp")
    temporary.write_bytes(data)
    try:
        os.chmod(temporary, mode)
    except OSError:
        pass
    os.replace(temporary, path)


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


class RuntimeUpdater:
    def __init__(self, args: argparse.Namespace, install_root: Path) -> None:
        self.args = args
        self.install_root = install_root
        self.versions_root = install_root / "RuntimeVersions"
        self.versions_root.mkdir(parents=True, exist_ok=True)
        self.ready_build_path = install_root / "runtime-ready-build.txt"
        self._stop = threading.Event()
        self._lock = threading.Lock()
        self._thread = threading.Thread(target=self._run, name="bees-runtime-updater", daemon=True)
        archive = Path(args.runtime_archive).expanduser().resolve()
        self.current_sha256 = _sha256_file(archive) if archive.is_file() else ""
        self.staged_sha256 = ""
        self.staged_root: Optional[Path] = None
        self.staged_bridge: Optional[Path] = None
        self.staged_build_id = ""
        self.last_error = ""

    def start(self) -> None:
        self._thread.start()

    def stop(self) -> None:
        self._stop.set()
        self._thread.join(timeout=5)

    def staged(self) -> tuple[str, Optional[Path], Optional[Path], str, str]:
        with self._lock:
            return (
                self.staged_sha256,
                self.staged_root,
                self.staged_bridge,
                self.staged_build_id,
                self.last_error,
            )

    def _bootstrap_token(self) -> str:
        value = Path(self.args.bootstrap_token_file).expanduser().read_text(encoding="ascii").strip()
        if not value:
            raise ValueError("bootstrap token is empty")
        return value

    def _fetch_bootstrap(self) -> tuple[bytes, bytes, bytes, bytes, bytes]:
        request = urllib.request.Request(
            f"http://127.0.0.1:{self.args.bootstrap_port}/bootstrap",
            method="GET",
            headers={"Authorization": "Bearer " + self._bootstrap_token()},
        )
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

    def _stage_once(self) -> None:
        runtime_zip, worker_token, wan_token, bridge_bytes, release_bytes = self._fetch_bootstrap()
        runtime_sha = hashlib.sha256(runtime_zip).hexdigest()
        release = json.loads(release_bytes.decode("utf-8"))
        staged_build_id = str(release.get("build_id", "")) if isinstance(release, Mapping) else ""
        if not staged_build_id:
            raise ValueError("bootstrap release metadata has no build_id")
        bridge_path = Path(self.args.tailnet_bridge).expanduser().resolve()
        bridge_sha = hashlib.sha256(bridge_bytes).hexdigest()
        current_bridge_sha = _sha256_file(bridge_path) if bridge_path.is_file() else ""
        _atomic_bytes(Path(self.args.worker_token_file).expanduser().resolve(), worker_token)
        _atomic_bytes(Path(self.args.wan_token_file).expanduser().resolve(), wan_token)

        runtime_changed = runtime_sha != self.current_sha256
        bridge_changed = bridge_sha != current_bridge_sha
        with self._lock:
            if (
                not runtime_changed
                and not bridge_changed
            ):
                self.staged_sha256 = ""
                self.staged_root = None
                self.staged_bridge = None
                self.staged_build_id = ""
                self.last_error = ""
                _atomic_bytes(
                    self.ready_build_path,
                    (staged_build_id + "\n").encode("ascii"),
                    0o600,
                )
                return
            if (
                runtime_sha == self.staged_sha256
                and self.staged_root is not None
                and (not bridge_changed or self.staged_bridge is not None)
            ):
                return

        destination = self.versions_root / runtime_sha
        if runtime_changed and not (destination / "bees_managed_remote_worker.py").is_file():
            _safe_extract_runtime(runtime_zip, destination)
        runtime_root = destination if runtime_changed else Path(__file__).resolve().parent
        if runtime_changed:
            requirements = destination / "bees_remote_requirements.txt"
            if requirements.is_file():
                completed = subprocess.run(
                    [sys.executable, "-m", "pip", "install", "-r", str(requirements)],
                    check=False,
                )
                if completed.returncode != 0:
                    raise RuntimeError(
                        f"runtime dependency preparation failed with exit code {completed.returncode}"
                    )
            archive = self.install_root / "Downloads" / f"bees-remote-runtime-{runtime_sha}.zip"
            _atomic_bytes(archive, runtime_zip, 0o644)
            _atomic_bytes(Path(self.args.runtime_archive).expanduser().resolve(), runtime_zip, 0o644)

        staged_bridge = None
        if bridge_changed:
            suffix = ".exe" if os.name == "nt" else ""
            staged_bridge = bridge_path.with_name("bees-tailnet-bridge.next" + suffix)
            _atomic_bytes(staged_bridge, bridge_bytes, 0o700)

        with self._lock:
            self.staged_sha256 = runtime_sha
            self.staged_root = runtime_root
            self.staged_bridge = staged_bridge
            self.staged_build_id = staged_build_id
            self.last_error = ""
        _atomic_bytes(
            self.ready_build_path,
            (staged_build_id + "\n").encode("ascii"),
            0o600,
        )
        print(
            f"[Bees remote] staged worker update runtime={runtime_sha[:12]} "
            f"bridge={bridge_sha[:12]}."
        )

    def _run(self) -> None:
        while not self._stop.is_set():
            try:
                self._stage_once()
            except (OSError, ValueError, RuntimeError, urllib.error.URLError, zipfile.BadZipFile) as exc:
                with self._lock:
                    self.last_error = f"{type(exc).__name__}: {exc}"
            self._stop.wait(self.args.runtime_poll_seconds)


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


def _runtime_cutover_selected(
    args: argparse.Namespace,
    trainer_id: str,
    updater: RuntimeUpdater,
) -> Optional[Path]:
    _sha, staged_root, _staged_bridge, staged_build_id, _error = updater.staged()
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
        status = _control_status(args)
        trainers = status.get("trainers", ()) if isinstance(status, Mapping) else ()
        if isinstance(trainers, list):
            for record in trainers:
                if (
                    isinstance(record, Mapping)
                    and record.get("trainer_id") == trainer_id
                    and record.get("process_state") == "stopped"
                ):
                    return staged_root
    return None


def _worker_command(args: argparse.Namespace, root: Path, actor_key: str) -> list[str]:
    trainer_id = f"remote-{socket.gethostname().lower()}-{actor_key[:8]}"
    return [
        sys.executable,
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
        "--",
        sys.executable,
        str(root / "bees_elastic_wan_actor_worker.py"),
        "--actor-key",
        actor_key,
        "--envs",
        str(args.envs),
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
    ]


def main(argv: Optional[Sequence[str]] = None) -> int:
    raw_argv = list(sys.argv[1:] if argv is None else argv)
    args = _parser().parse_args(raw_argv)
    if args.envs is None:
        args.envs = _default_envs()
        print(
            f"[Bees remote] --envs omitted; using {args.envs} "
            f"(4 x {_available_cpu_threads()} available CPU threads, cap {MAX_ENVS_PER_ACTOR})."
        )
    if not 1 <= args.envs <= MAX_ENVS_PER_ACTOR:
        print(f"error: --envs must be in 1-{MAX_ENVS_PER_ACTOR}", file=sys.stderr)
        return 2
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
    if args.reconnect_seconds <= 0 or args.runtime_poll_seconds <= 0:
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

    trainer_id = f"remote-{socket.gethostname().lower()}-{actor_key[:8]}"
    updater = RuntimeUpdater(args, install_root)
    updater.start()
    stop = [False]

    def request_stop(_signum: int, _frame: object) -> None:
        stop[0] = True

    old_sigint = signal.signal(signal.SIGINT, request_stop)
    old_sigterm = signal.signal(signal.SIGTERM, request_stop)
    try:
        while not stop[0]:
            tailnet: Optional[subprocess.Popen] = None
            worker: Optional[subprocess.Popen] = None
            runtime_cutover: Optional[Path] = None
            try:
                tailnet = subprocess.Popen(_tailnet_forward_command(args))
                if not _wait_for_ports(
                    (args.control_port, args.broker_port, args.bootstrap_port),
                    tailnet,
                    stop,
                ):
                    code = tailnet.poll()
                    print(
                        "[Bees remote] tailnet forwarding failed to become ready"
                        + ("" if code is None else f" (exit {code})")
                        + ".",
                        file=sys.stderr,
                    )
                else:
                    print(
                        f"[Bees remote] private transport ready; identity={actor_key[:8]} "
                        f"envs={args.envs}; actor slot will be assigned by the learner."
                    )
                    worker = subprocess.Popen(_worker_command(args, root, actor_key))
                    while not stop[0] and tailnet.poll() is None and worker.poll() is None:
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
                        elif worker.poll() is not None:
                            print(
                                f"[Bees remote] worker exited ({worker.returncode}); restarting.",
                                file=sys.stderr,
                            )
            except KeyboardInterrupt:
                stop[0] = True
            finally:
                _terminate(worker)
                _terminate(tailnet)

            if runtime_cutover is not None and not stop[0]:
                updater.stop()
                _sha, next_root, staged_bridge, _staged_build_id, _error = updater.staged()
                if next_root is None:
                    raise RuntimeError("staged runtime disappeared before activation")
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
                os.execv(
                    sys.executable,
                    [sys.executable, str(next_script), *raw_argv],
                )

            if not stop[0]:
                time.sleep(args.reconnect_seconds)
        return 0
    finally:
        updater.stop()
        signal.signal(signal.SIGINT, old_sigint)
        signal.signal(signal.SIGTERM, old_sigterm)


if __name__ == "__main__":
    raise SystemExit(main())
