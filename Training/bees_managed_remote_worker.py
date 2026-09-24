"""Persistent one-command remote Bees rollout worker supervisor.

This process owns the training-control SSH tunnel and supervises the managed worker agent. The
learner-side WAN broker assigns an available actor slot automatically. Each remote installation keeps
a small persistent actor key so reconnects can reclaim its current slot safely.
"""

from __future__ import annotations

import argparse
import ctypes
import os
from pathlib import Path
import shutil
import signal
import socket
import subprocess
import sys
import time
from typing import Optional, Sequence
import uuid


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
    parser.add_argument("--learner", required=True, help="SSH target for the learner, e.g. user@exeter.")
    parser.add_argument("--ssh-port", type=int, default=22)
    parser.add_argument(
        "--envs",
        type=int,
        default=None,
        help="Unity environment count (1-64). Default: 4x available CPU threads, capped at 64.",
    )
    parser.add_argument("--control-port", type=int, default=7150)
    parser.add_argument("--install-root", required=True)
    parser.add_argument("--worker-token-file", required=True)
    parser.add_argument("--wan-token-file", required=True)
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


def _wait_for_port(port: int, process: subprocess.Popen, stop: list[bool], timeout: float = 15.0) -> bool:
    deadline = time.monotonic() + timeout
    while not stop[0] and time.monotonic() < deadline:
        if process.poll() is not None:
            return False
        try:
            with socket.create_connection(("127.0.0.1", port), timeout=0.5):
                return True
        except OSError:
            time.sleep(0.2)
    return False


def _ssh_tunnel_command(ssh: str, learner: str, ssh_port: int, control_port: int) -> list[str]:
    return [
        ssh,
        "-N",
        "-p",
        str(ssh_port),
        "-o",
        "ExitOnForwardFailure=yes",
        "-o",
        "ServerAliveInterval=30",
        "-o",
        "ServerAliveCountMax=3",
        "-L",
        f"127.0.0.1:{control_port}:127.0.0.1:{control_port}",
        learner,
    ]


def _worker_command(args: argparse.Namespace, root: Path, actor_key: str) -> list[str]:
    trainer_id = f"remote-{socket.gethostname().lower()}-{actor_key[:8]}"
    command = [
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
        "--",
        sys.executable,
        str(root / "bees_elastic_wan_actor_worker.py"),
        "--actor-key",
        actor_key,
        "--envs",
        str(args.envs),
        "--ssh",
        args.learner,
        "--env",
        "{env}",
        "--auth-token-file",
        str(Path(args.wan_token_file).expanduser().resolve()),
        "--torch-device",
        args.torch_device,
    ]
    if args.ssh_port != 22:
        command.extend(["--ssh-option", f"Port={args.ssh_port}"])
    return command


def main(argv: Optional[Sequence[str]] = None) -> int:
    args = _parser().parse_args(argv)
    if args.envs is None:
        args.envs = _default_envs()
        print(
            f"[Bees remote] --envs omitted; using {args.envs} "
            f"(4 x {_available_cpu_threads()} available CPU threads, cap {MAX_ENVS_PER_ACTOR})."
        )
    if not 1 <= args.envs <= MAX_ENVS_PER_ACTOR:
        print(f"error: --envs must be in 1-{MAX_ENVS_PER_ACTOR}", file=sys.stderr)
        return 2
    if not 1 <= args.ssh_port <= 65535 or not 1 <= args.control_port <= 65535:
        print("error: SSH/control ports must be in 1-65535", file=sys.stderr)
        return 2
    if args.reconnect_seconds <= 0:
        print("error: --reconnect-seconds must be positive", file=sys.stderr)
        return 2

    ssh = shutil.which("ssh")
    if ssh is None:
        print("error: OpenSSH client 'ssh' is not available on PATH", file=sys.stderr)
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

    stop = [False]

    def request_stop(_signum: int, _frame: object) -> None:
        stop[0] = True

    old_sigint = signal.signal(signal.SIGINT, request_stop)
    old_sigterm = signal.signal(signal.SIGTERM, request_stop)
    try:
        while not stop[0]:
            tunnel: Optional[subprocess.Popen] = None
            worker: Optional[subprocess.Popen] = None
            try:
                tunnel = subprocess.Popen(_ssh_tunnel_command(
                    ssh, args.learner, args.ssh_port, args.control_port
                ))
                if not _wait_for_port(args.control_port, tunnel, stop):
                    code = tunnel.poll()
                    print(
                        "[Bees remote] control SSH tunnel failed to become ready"
                        + ("" if code is None else f" (exit {code})")
                        + ".",
                        file=sys.stderr,
                    )
                else:
                    print(
                        f"[Bees remote] control tunnel ready; identity={actor_key[:8]} "
                        f"envs={args.envs}; actor slot will be assigned by the learner."
                    )
                    worker = subprocess.Popen(_worker_command(args, root, actor_key))
                    while not stop[0] and tunnel.poll() is None and worker.poll() is None:
                        time.sleep(0.5)
                    if not stop[0]:
                        if tunnel.poll() is not None:
                            print(
                                f"[Bees remote] control tunnel exited ({tunnel.returncode}); restarting.",
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
                _terminate(tunnel)

            if not stop[0]:
                time.sleep(args.reconnect_seconds)
        return 0
    finally:
        signal.signal(signal.SIGINT, old_sigint)
        signal.signal(signal.SIGTERM, old_sigterm)


if __name__ == "__main__":
    raise SystemExit(main())
