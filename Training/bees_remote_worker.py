"""Launch remote Bees Unity rollout workers through one SSH tunnel.

Run this helper on a rollout machine. It forwards each worker's local ML-Agents port to the same
loopback port on the central trainer, then launches one Unity process per worker ID. Unity continues
to connect to localhost as required by ML-Agents; the raw unauthenticated gRPC protocol is never
intentionally exposed to the network.
"""

from __future__ import annotations

import argparse
import os
import signal
import subprocess
import sys
import time
from pathlib import Path
from typing import Iterable, List, Sequence, Tuple


DEFAULT_BASE_PORT = 5005


def parse_worker_ids(value: str) -> Tuple[int, ...]:
    if not isinstance(value, str) or not value.strip():
        raise ValueError("worker ID list must not be empty")
    result = []
    seen = set()
    for token in value.split(","):
        token = token.strip()
        if not token:
            raise ValueError("worker ID list contains an empty item")
        if "-" in token:
            parts = token.split("-", 1)
            if len(parts) != 2 or not parts[0].isdigit() or not parts[1].isdigit():
                raise ValueError(f"invalid worker range {token!r}")
            start, end = int(parts[0]), int(parts[1])
            if end < start:
                raise ValueError(f"worker range must ascend: {token!r}")
            values: Iterable[int] = range(start, end + 1)
        else:
            if not token.isdigit():
                raise ValueError(f"invalid worker ID {token!r}")
            values = (int(token),)
        for worker_id in values:
            if worker_id < 0:
                raise ValueError("worker IDs must be non-negative")
            if worker_id in seen:
                raise ValueError(f"duplicate worker ID {worker_id}")
            seen.add(worker_id)
            result.append(worker_id)
    return tuple(result)


def worker_ports(base_port: int, worker_ids: Sequence[int]) -> Tuple[int, ...]:
    if base_port <= 0:
        raise ValueError("base port must be positive")
    ports = tuple(base_port + worker_id for worker_id in worker_ids)
    if any(port > 65535 for port in ports):
        raise ValueError("worker port exceeds 65535")
    return ports


def ssh_command(
    ssh_executable: str,
    ssh_target: str,
    ports: Sequence[int],
    ssh_options: Sequence[str],
) -> List[str]:
    command = [
        ssh_executable,
        "-N",
        "-o",
        "ExitOnForwardFailure=yes",
        "-o",
        "ServerAliveInterval=30",
        "-o",
        "ServerAliveCountMax=3",
    ]
    for option in ssh_options:
        command.extend(["-o", option])
    for port in ports:
        command.extend(["-L", f"127.0.0.1:{port}:127.0.0.1:{port}"])
    command.append(ssh_target)
    return command


def unity_command(
    env_path: str,
    port: int,
    *,
    no_graphics: bool,
    unity_args: Sequence[str],
) -> List[str]:
    command = [env_path]
    if no_graphics:
        command.extend(["-nographics", "-batchmode"])
    command.extend(["--mlagents-port", str(port)])
    command.extend(unity_args)
    return command


def _terminate(process: subprocess.Popen) -> None:
    if process.poll() is not None:
        return
    try:
        process.terminate()
        process.wait(timeout=10)
    except Exception:
        try:
            process.kill()
        except Exception:
            pass


def _build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Run remote Bees ML-Agents environments through SSH port forwards."
    )
    parser.add_argument("--ssh", required=True, help="SSH target for the central trainer, e.g. user@trainer.")
    parser.add_argument("--env", required=True, help="Path to the Bees training executable on this machine.")
    parser.add_argument(
        "--worker-ids",
        required=True,
        help="External worker IDs assigned by the central trainer, e.g. 8-15 or 8,10,12.",
    )
    parser.add_argument("--base-port", type=int, default=DEFAULT_BASE_PORT)
    parser.add_argument("--ssh-executable", default="ssh")
    parser.add_argument(
        "--ssh-option",
        action="append",
        default=[],
        help="Additional OpenSSH -o option; may be repeated.",
    )
    parser.add_argument(
        "--graphics",
        action="store_true",
        help="Do not add -nographics/-batchmode to the Unity workers.",
    )
    parser.add_argument(
        "--unity-arg",
        action="append",
        default=[],
        help="Additional argument passed to every Unity worker; may be repeated.",
    )
    parser.add_argument(
        "--tunnel-startup-seconds",
        type=float,
        default=1.0,
        help="How long to give SSH to establish forwards before launching Unity.",
    )
    return parser


def main(argv: Sequence[str] | None = None) -> int:
    args = _build_parser().parse_args(argv)
    try:
        worker_ids = parse_worker_ids(args.worker_ids)
        ports = worker_ports(args.base_port, worker_ids)
    except ValueError as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 2

    env_path = str(Path(args.env).expanduser().resolve())
    if not os.path.isfile(env_path):
        print(f"error: Unity environment executable does not exist: {env_path}", file=sys.stderr)
        return 2
    if args.tunnel_startup_seconds < 0:
        print("error: --tunnel-startup-seconds must be non-negative", file=sys.stderr)
        return 2

    tunnel = subprocess.Popen(
        ssh_command(args.ssh_executable, args.ssh, ports, args.ssh_option),
        stdin=subprocess.DEVNULL,
    )
    workers: List[subprocess.Popen] = []
    stop_requested = False

    def request_stop(_signum, _frame):
        nonlocal stop_requested
        stop_requested = True

    old_sigint = signal.signal(signal.SIGINT, request_stop)
    old_sigterm = signal.signal(signal.SIGTERM, request_stop)
    try:
        deadline = time.monotonic() + args.tunnel_startup_seconds
        while time.monotonic() < deadline and tunnel.poll() is None and not stop_requested:
            time.sleep(0.05)
        if tunnel.poll() is not None:
            print(
                f"error: SSH tunnel exited before workers started (code {tunnel.returncode}).",
                file=sys.stderr,
            )
            return 3

        for worker_id, port in zip(worker_ids, ports):
            command = unity_command(
                env_path,
                port,
                no_graphics=not args.graphics,
                unity_args=args.unity_arg,
            )
            print(f"[Bees remote] worker={worker_id} local_port={port}")
            workers.append(subprocess.Popen(command))

        while not stop_requested:
            if tunnel.poll() is not None:
                print(
                    f"error: SSH tunnel exited (code {tunnel.returncode}); stopping remote workers.",
                    file=sys.stderr,
                )
                return 4
            failed = [
                (worker_id, process.returncode)
                for worker_id, process in zip(worker_ids, workers)
                if process.poll() is not None and process.returncode != 0
            ]
            if failed:
                print(f"error: Unity rollout worker exited unexpectedly: {failed}", file=sys.stderr)
                return 5
            if workers and all(process.poll() is not None for process in workers):
                return 0
            time.sleep(0.25)
        return 0
    finally:
        for process in workers:
            _terminate(process)
        _terminate(tunnel)
        signal.signal(signal.SIGINT, old_sigint)
        signal.signal(signal.SIGTERM, old_sigterm)


if __name__ == "__main__":
    raise SystemExit(main())
