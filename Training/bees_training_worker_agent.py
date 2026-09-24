"""Persistent Bees training worker controlled by BeesServer desired state.

Dedicated workers fail closed: once the server lease expires, their managed process is terminated.
Full-game workers fail over by leaving the game process running; ordinary Bees gameplay already uses
the deployed policy for inference and records telemetry to local pending files when uploads are
unavailable. When control returns, the worker reconciles build/config state and reconnects.
"""

from __future__ import annotations

import argparse
import json
import os
import signal
import subprocess
import sys
import time
from pathlib import Path
from typing import Any, Mapping, Optional, Sequence

from bees_training_control import (
    ControlRejected,
    ControlUnavailable,
    ManagedBuildStore,
    TrainingControlClient,
    default_heartbeat,
    load_token,
)


ENV_PLACEHOLDER = "{env}"
ENV_ARGS_PLACEHOLDER = "{env_args}"


def render_command(
    template: Sequence[str],
    entrypoint: Path,
    environment_args: Sequence[str],
) -> list[str]:
    if not template:
        raise ValueError("managed worker launch command is empty")
    rendered: list[str] = []
    saw_env = False
    for token in template:
        if token == ENV_PLACEHOLDER:
            rendered.append(str(entrypoint))
            saw_env = True
        elif token == ENV_ARGS_PLACEHOLDER:
            rendered.extend(str(value) for value in environment_args)
        else:
            rendered.append(token.replace(ENV_PLACEHOLDER, str(entrypoint)))
            if ENV_PLACEHOLDER in token:
                saw_env = True
    if not saw_env:
        raise ValueError(f"managed worker launch command must contain {ENV_PLACEHOLDER}")
    return rendered


class ManagedProcess:
    def __init__(self) -> None:
        self.process: Optional[subprocess.Popen] = None
        self.command: tuple[str, ...] = ()
        self.revision = -1
        self.build_sha256 = ""

    def alive(self) -> bool:
        return self.process is not None and self.process.poll() is None

    def state(self, role: str, offline: bool = False) -> str:
        if not self.alive():
            return "stopped"
        if role == "full-game" and offline:
            return "inference-offline"
        return "running"

    def start(
        self,
        command: Sequence[str],
        *,
        revision: int,
        build_sha256: str,
        state_file: Path,
    ) -> None:
        self.stop()
        environment = os.environ.copy()
        environment["BEES_TRAINING_CONTROL_STATE_FILE"] = str(state_file)
        self.process = subprocess.Popen(list(command), env=environment)
        self.command = tuple(command)
        self.revision = revision
        self.build_sha256 = build_sha256

    def stop(self) -> None:
        process = self.process
        self.process = None
        if process is None or process.poll() is not None:
            return
        try:
            process.terminate()
            process.wait(timeout=15)
        except Exception:
            try:
                process.kill()
                process.wait(timeout=5)
            except Exception:
                pass


def write_local_state(
    path: Path,
    *,
    desired: Optional[Mapping[str, Any]],
    online: bool,
    last_error: str,
) -> None:
    value = {
        "schema_version": 1,
        "online": bool(online),
        "desired_mode": desired.get("desired_mode") if desired else "inference",
        "revision": desired.get("revision") if desired else -1,
        "training_enabled": bool(desired.get("training_enabled")) if desired else False,
        "environment_args": list(desired.get("environment_args", ())) if desired else [],
        "last_error": last_error,
        "updated_unix_seconds": time.time(),
    }
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_text(json.dumps(value, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    os.replace(temporary, path)


def _parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Keep one Bees trainer/game process reconciled with BeesServer control state."
    )
    parser.add_argument("--server-url", required=True)
    parser.add_argument("--token-file", required=True)
    parser.add_argument("--trainer-id", required=True)
    parser.add_argument("--role", choices=("dedicated", "full-game"), required=True)
    parser.add_argument("--platform", required=True)
    parser.add_argument("--install-root", required=True)
    parser.add_argument("--heartbeat-seconds", type=float, default=5.0)
    parser.add_argument("--request-timeout-seconds", type=float, default=15.0)
    parser.add_argument(
        "launch_command",
        nargs=argparse.REMAINDER,
        help=(
            "Command after '--'. Use {env} for the canonical executable and {env_args} where "
            "server-owned environment arguments should be expanded."
        ),
    )
    return parser


def _normalized_launch_command(values: Sequence[str]) -> list[str]:
    command = list(values)
    if command and command[0] == "--":
        command = command[1:]
    if not command:
        raise ValueError("a managed launch command is required after '--'")
    return command


def main(argv: Optional[Sequence[str]] = None) -> int:
    args = _parser().parse_args(argv)
    if args.heartbeat_seconds <= 0 or args.request_timeout_seconds <= 0:
        print("error: heartbeat and request timeout must be positive", file=sys.stderr)
        return 2
    try:
        command_template = _normalized_launch_command(args.launch_command)
        if not any(ENV_PLACEHOLDER in token for token in command_template):
            raise ValueError(f"launch command must contain {ENV_PLACEHOLDER}")
        token = load_token(args.token_file)
    except (OSError, ValueError) as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 2

    install_root = Path(args.install_root).expanduser().resolve()
    builds = ManagedBuildStore(install_root)
    state_file = install_root / "control-state.json"
    client = TrainingControlClient(
        args.server_url,
        token,
        timeout=args.request_timeout_seconds,
    )
    managed = ManagedProcess()
    stop = False
    last_contact = 0.0
    lease_seconds = max(1.0, args.heartbeat_seconds * 2.0)
    desired: Optional[Mapping[str, Any]] = None
    active_build: Optional[Mapping[str, Any]] = builds.current()
    applied_revision = -1
    last_error = ""

    def request_stop(_signum: int, _frame: object) -> None:
        nonlocal stop
        stop = True

    old_sigint = signal.signal(signal.SIGINT, request_stop)
    old_sigterm = signal.signal(signal.SIGTERM, request_stop)
    try:
        while not stop:
            now = time.monotonic()
            offline = last_contact > 0 and now - last_contact > lease_seconds
            heartbeat = default_heartbeat(
                trainer_id=args.trainer_id,
                role=args.role,
                platform=args.platform,
                process_state=managed.state(args.role, offline=offline),
                applied_revision=applied_revision,
                build=active_build,
                last_error=last_error,
            )
            try:
                desired = client.heartbeat(heartbeat)
                last_contact = time.monotonic()
                lease_seconds = float(desired["lease_seconds"])
                last_error = ""
                write_local_state(state_file, desired=desired, online=True, last_error="")

                mode = str(desired["desired_mode"])
                revision = int(desired["revision"])
                environment_args = tuple(str(value) for value in desired["environment_args"])
                descriptor = desired.get("build")

                if mode == "stopped":
                    managed.stop()
                    applied_revision = revision
                elif mode == "inference" and args.role == "full-game":
                    # Do not interrupt a running player session merely because central learning is
                    # disabled. The game retains its deployed policy and local telemetry recorder.
                    applied_revision = revision
                    if not managed.alive() and descriptor:
                        entrypoint, active_build = builds.ensure(client, descriptor)
                        command = render_command(command_template, entrypoint, environment_args)
                        managed.start(
                            command,
                            revision=revision,
                            build_sha256=str(active_build["archive_sha256"]),
                            state_file=state_file,
                        )
                elif mode == "training":
                    if not descriptor:
                        raise RuntimeError(
                            f"server has no canonical {args.platform} build published"
                        )
                    entrypoint, active_build = builds.ensure(client, descriptor)
                    desired_sha = str(active_build["archive_sha256"])
                    needs_restart = (
                        not managed.alive()
                        or managed.revision != revision
                        or managed.build_sha256 != desired_sha
                    )
                    if needs_restart:
                        command = render_command(command_template, entrypoint, environment_args)
                        managed.start(
                            command,
                            revision=revision,
                            build_sha256=desired_sha,
                            state_file=state_file,
                        )
                    applied_revision = revision
                else:
                    raise RuntimeError(f"unsupported desired mode {mode!r}")
            except (ControlUnavailable, ControlRejected, OSError, ValueError, RuntimeError) as exc:
                last_error = f"{type(exc).__name__}: {exc}"
                offline = last_contact <= 0 or time.monotonic() - last_contact > lease_seconds
                write_local_state(
                    state_file,
                    desired=desired,
                    online=False,
                    last_error=last_error,
                )
                if args.role == "dedicated" and offline:
                    if managed.alive():
                        print(
                            "[Bees control] server lease expired; stopping dedicated trainer.",
                            file=sys.stderr,
                        )
                    managed.stop()
                elif args.role == "full-game" and offline and managed.alive():
                    print(
                        "[Bees control] server lease expired; full game remains running in "
                        "inference/offline-recording mode.",
                        file=sys.stderr,
                    )

            deadline = time.monotonic() + args.heartbeat_seconds
            while not stop and time.monotonic() < deadline:
                if managed.process is not None and managed.process.poll() is not None:
                    code = managed.process.returncode
                    managed.process = None
                    if code not in (0, None):
                        last_error = f"managed process exited with code {code}"
                    break
                time.sleep(min(0.25, max(0.0, deadline - time.monotonic())))
        return 0
    finally:
        managed.stop()
        signal.signal(signal.SIGINT, old_sigint)
        signal.signal(signal.SIGTERM, old_sigterm)


if __name__ == "__main__":
    raise SystemExit(main())
