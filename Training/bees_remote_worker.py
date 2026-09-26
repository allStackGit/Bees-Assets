"""Launch remote Bees Unity rollout workers through one SSH tunnel.

Run this helper on a rollout machine with the content-hashed session spec emitted by the central
trainer. It forwards each selected worker's local ML-Agents port to the same loopback port on the
central trainer, then launches one Unity process per worker ID with the exact Unity environment args
pinned in that spec. Unity continues to connect to localhost as required by ML-Agents; the raw
unauthenticated gRPC protocol is never intentionally exposed to the network.
"""

from __future__ import annotations

import argparse
import json
import math
import os
import signal
import subprocess
import sys
import time
from pathlib import Path
from typing import Iterable, List, Mapping, Sequence, Tuple

import bees_distributed_training as distributed


DEFAULT_BASE_PORT = distributed.DEFAULT_BASE_PORT

CONTROL_ENV_ARGS_VARIABLE = "BEES_TRAINING_ENV_ARGS_JSON"


def controlled_environment_args(spec_args: Sequence[str]) -> Tuple[str, ...]:
    raw = os.environ.get(CONTROL_ENV_ARGS_VARIABLE)
    if raw is None or raw.strip() == "":
        return tuple(str(value) for value in spec_args)
    try:
        value = json.loads(raw)
    except json.JSONDecodeError as exc:
        raise ValueError(f"{CONTROL_ENV_ARGS_VARIABLE} is invalid JSON") from exc
    if not isinstance(value, list) or any(not isinstance(item, str) for item in value):
        raise ValueError(f"{CONTROL_ENV_ARGS_VARIABLE} must contain a JSON string list")
    return tuple(value)



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


def select_worker_ids(spec: Mapping[str, object], requested: str | None) -> Tuple[int, ...]:
    assigned = tuple(int(value) for value in spec["worker_ids"])
    if requested is None:
        return assigned
    selected = parse_worker_ids(requested)
    unknown = sorted(set(selected) - set(assigned))
    if unknown:
        raise ValueError(
            "requested worker IDs are not assigned by the central session spec: "
            + ", ".join(str(value) for value in unknown)
        )
    return selected


def worker_ports(base_port: int, worker_ids: Sequence[int]) -> Tuple[int, ...]:
    return distributed.external_worker_ports(base_port, worker_ids)


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
    except OSError:
        if process.poll() is not None:
            return
    try:
        process.wait(timeout=10)
        return
    except subprocess.TimeoutExpired:
        pass

    try:
        process.kill()
    except OSError:
        if process.poll() is not None:
            return
    try:
        process.wait(timeout=10)
    except subprocess.TimeoutExpired as exc:
        raise RuntimeError(
            f"remote worker child process {process.pid} did not stop after kill"
        ) from exc


def _build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Run remote Bees ML-Agents environments through SSH port forwards."
    )
    parser.add_argument("--ssh", required=True, help="SSH target for the central trainer, e.g. user@trainer.")
    parser.add_argument("--env", required=True, help="Path to the same Bees training build on this machine.")
    parser.add_argument(
        "--spec",
        required=True,
        help="Remote-worker session spec copied from the central --bees-remote-spec output.",
    )
    parser.add_argument(
        "--worker-ids",
        default=None,
        help=(
            "Optional assigned subset from the session spec, e.g. 8-11. Omit to launch every "
            "external worker in the spec on this machine."
        ),
    )
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
        "--tunnel-startup-seconds",
        type=float,
        default=1.0,
        help="How long to give SSH to establish forwards before launching Unity.",
    )
    return parser


def main(argv: Sequence[str] | None = None) -> int:
    args = _build_parser().parse_args(argv)
    try:
        spec = distributed.load_remote_worker_spec(args.spec)
        worker_ids = select_worker_ids(spec, args.worker_ids)
        base_port = int(spec["base_port"])
        ports = worker_ports(base_port, worker_ids)
        unity_args = controlled_environment_args(spec["unity_args"])
    except ValueError as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 2

    env_path = str(Path(args.env).expanduser().resolve())
    if not os.path.isfile(env_path):
        print(f"error: Unity environment executable does not exist: {env_path}", file=sys.stderr)
        return 2
    if not math.isfinite(args.tunnel_startup_seconds) or args.tunnel_startup_seconds < 0:
        print("error: --tunnel-startup-seconds must be non-negative", file=sys.stderr)
        return 2

    print(
        f"[Bees remote] session={spec['identity_sha256']} run_id={spec.get('run_id') or 'none'} "
        f"workers={','.join(str(value) for value in worker_ids)}"
    )
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
        if stop_requested:
            return 0
        if tunnel.poll() is not None:
            print(
                f"error: SSH tunnel exited before workers started (code {tunnel.returncode}).",
                file=sys.stderr,
            )
            return 3

        for worker_id, port in zip(worker_ids, ports):
            if stop_requested or tunnel.poll() is not None:
                break
            command = unity_command(
                env_path,
                port,
                no_graphics=not args.graphics,
                unity_args=unity_args,
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
        cleanup_errors = []
        for process in [*workers, tunnel]:
            try:
                _terminate(process)
            except RuntimeError as exc:
                cleanup_errors.append(str(exc))
        signal.signal(signal.SIGINT, old_sigint)
        signal.signal(signal.SIGTERM, old_sigterm)
        if cleanup_errors:
            raise RuntimeError(
                "remote worker cleanup could not confirm child shutdown: "
                + "; ".join(cleanup_errors)
            )


if __name__ == "__main__":
    raise SystemExit(main())
