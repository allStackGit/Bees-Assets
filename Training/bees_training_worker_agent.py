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
import re
import signal
import subprocess
import sys
import threading
import time
from collections import deque
from pathlib import Path
from typing import Any, Mapping, Optional, Sequence

from bees_training_control import (
    ControlRejected,
    ControlUnavailable,
    ManagedBuildStore,
    TrainingControlClient,
    default_heartbeat,
    file_sha256,
    load_token,
)


ENV_PLACEHOLDER = "{env}"
ENV_ARGS_PLACEHOLDER = "{env_args}"
BUILD_ID_PLACEHOLDER = "{build_id}"
RUN_ID_PLACEHOLDER = "{run_id}"
MANAGED_STOP_FILE_ENV = "BEES_TRAINING_STOP_FILE"
GRACEFUL_CHECKPOINT_STOP_SECONDS = 120.0
EPISODE_LOG_PATTERN = re.compile(
    r"RL 1v1 episode=(\d+).*?timeout=(True|False) duration=([\d.]+)s "
    r"bee_tsv=(\d+)->(\d+) human_tsv=(\d+)->(\d+).*?"
    r"bee_fire_requests=(\d+) bee_shots=(\d+) bee_hits=(\d+) bee_damage=(\d+).*?"
    r"human_fire_requests=(\d+) human_shots=(\d+) human_hits=(\d+) human_damage=(\d+)"
)


class EpisodeLogMetrics:
    def __init__(self, root: Path, window: int = 100) -> None:
        self.root = root
        self.window = max(1, int(window))
        self._episodes = deque(maxlen=self.window)
        self._positions: dict[Path, int] = {}
        self._pending: dict[Path, str] = {}
        self._run_id = ""

    def refresh(self, run_id: str = "") -> dict[str, object]:
        run_id = str(run_id or "")
        if run_id != self._run_id:
            self._run_id = run_id
            self._episodes.clear()
            self._positions.clear()
            self._pending.clear()
        scan_root = self.root / run_id if run_id else self.root
        if scan_root.is_dir():
            for log_path in sorted(scan_root.rglob("Player-*.log")):
                self._read_new(log_path)
        return self.snapshot()

    def _read_new(self, log_path: Path) -> None:
        try:
            size = log_path.stat().st_size
        except OSError:
            return
        position = self._positions.get(log_path)
        first_read = position is None
        if position is None:
            position = max(0, size - 4 * 1024 * 1024)
        if size < position:
            position = 0
            self._pending.pop(log_path, None)
        try:
            with log_path.open("rb") as handle:
                handle.seek(position)
                data = handle.read()
        except OSError:
            return
        self._positions[log_path] = position + len(data)
        if not data:
            return
        text = self._pending.get(log_path, "") + data.decode("utf-8", errors="replace")
        complete = text.endswith("\n") or text.endswith("\r")
        lines = text.splitlines()
        if not complete and lines:
            self._pending[log_path] = lines.pop()
        else:
            self._pending[log_path] = ""
        if first_read and position > 0 and lines:
            lines = lines[1:]
        for line in lines:
            match = EPISODE_LOG_PATTERN.search(line)
            if not match:
                continue
            values = match.groups()
            timeout = values[1] == "True"
            bee_final = int(values[4])
            human_final = int(values[6])
            self._episodes.append({
                "episode": int(values[0]),
                "timeout": timeout,
                "duration": float(values[2]),
                "bee_win": (not timeout and bee_final > 0 and human_final == 0),
                "human_win": (not timeout and human_final > 0 and bee_final == 0),
                "bee_shots": int(values[8]),
                "bee_hits": int(values[9]),
                "human_shots": int(values[12]),
                "human_hits": int(values[13]),
            })

    def snapshot(self) -> dict[str, object]:
        episodes = list(self._episodes)
        count = len(episodes)
        if count == 0:
            return {"window_episodes": 0}
        timeouts = sum(1 for item in episodes if item["timeout"])
        bee_wins = sum(1 for item in episodes if item["bee_win"])
        human_wins = sum(1 for item in episodes if item["human_win"])
        draws = count - timeouts - bee_wins - human_wins
        bee_shots = sum(int(item["bee_shots"]) for item in episodes)
        bee_hits = sum(int(item["bee_hits"]) for item in episodes)
        human_shots = sum(int(item["human_shots"]) for item in episodes)
        human_hits = sum(int(item["human_hits"]) for item in episodes)
        return {
            "window_episodes": count,
            "last_episode": max(int(item["episode"]) for item in episodes),
            "timeout_pct": round(100.0 * timeouts / count, 2),
            "bee_win_pct": round(100.0 * bee_wins / count, 2),
            "human_win_pct": round(100.0 * human_wins / count, 2),
            "draw_pct": round(100.0 * draws / count, 2),
            "avg_duration_s": round(
                sum(float(item["duration"]) for item in episodes) / count, 2),
            "bee_hit_pct": round(100.0 * bee_hits / bee_shots, 2) if bee_shots else 0.0,
            "human_hit_pct": round(
                100.0 * human_hits / human_shots, 2) if human_shots else 0.0,
            "bee_shots_per_episode": round(bee_shots / count, 2),
            "human_shots_per_episode": round(human_shots / count, 2),
        }


def render_command(
    template: Sequence[str],
    entrypoint: Path,
    environment_args: Sequence[str],
    build_id: str = "",
    run_id: str = "",
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
            rendered.append(
                token.replace(ENV_PLACEHOLDER, str(entrypoint))
                .replace(BUILD_ID_PLACEHOLDER, str(build_id))
                .replace(RUN_ID_PLACEHOLDER, str(run_id))
            )
            if ENV_PLACEHOLDER in token:
                saw_env = True
    if not saw_env:
        raise ValueError(f"managed worker launch command must contain {ENV_PLACEHOLDER}")
    return rendered


class BackgroundBuildPreparer:
    def __init__(self, builds: ManagedBuildStore, client: TrainingControlClient) -> None:
        self.builds = builds
        self.client = client
        self._lock = threading.Lock()
        self._thread: Optional[threading.Thread] = None
        self._requested_build_id = ""
        self.prepared_build_id = ""
        self.last_error = ""

    def request(self, descriptor: Optional[Mapping[str, Any]]) -> None:
        if not descriptor:
            return
        build_id = str(descriptor.get("build_id", ""))
        if not build_id:
            return
        try:
            if self.builds.is_prepared(descriptor):
                with self._lock:
                    self.prepared_build_id = build_id
                    self.last_error = ""
                return
        except (OSError, ValueError) as exc:
            with self._lock:
                self.last_error = f"{type(exc).__name__}: {exc}"
            return

        with self._lock:
            if self._thread is not None and self._thread.is_alive():
                return
            self._requested_build_id = build_id
            payload = dict(descriptor)
            self._thread = threading.Thread(
                target=self._prepare,
                args=(payload,),
                name=f"bees-build-prepare-{build_id}",
                daemon=True,
            )
            self._thread.start()

    def _prepare(self, descriptor: Mapping[str, Any]) -> None:
        build_id = str(descriptor.get("build_id", ""))
        try:
            self.builds.prepare(self.client, descriptor)
            with self._lock:
                self.prepared_build_id = build_id
                self.last_error = ""
        except (ControlUnavailable, ControlRejected, OSError, ValueError, RuntimeError) as exc:
            with self._lock:
                self.last_error = f"{type(exc).__name__}: {exc}"

    def snapshot(self) -> tuple[str, str]:
        with self._lock:
            return self.prepared_build_id, self.last_error


class TrainingLogUploader:
    CHUNK_BYTES = 1024 * 1024

    def __init__(self, root: Path) -> None:
        self.root = root
        self._positions: dict[Path, int] = {}

    def flush_once(
        self,
        client: TrainingControlClient,
        *,
        trainer_id: str,
        run_id: str,
    ) -> int:
        if not run_id:
            return 0
        run_root = self.root / run_id
        if not run_root.is_dir():
            return 0
        budget = self.CHUNK_BYTES
        uploaded = 0
        for log_path in sorted(run_root.rglob("*")):
            if budget <= 0:
                break
            if not log_path.is_file() or log_path.suffix.lower() not in (".log", ".txt", ".json"):
                continue
            relative = log_path.relative_to(run_root).as_posix()
            try:
                size = log_path.stat().st_size
            except OSError:
                continue
            position = self._positions.get(log_path, 0)
            if size < position:
                next_offset = client.upload_log_chunk(
                    trainer_id=trainer_id,
                    run_id=run_id,
                    relative_path=relative,
                    offset=0,
                    data=b"",
                    reset=True,
                )
                if next_offset < 0:
                    next_offset = -next_offset - 1
                position = next_offset
                self._positions[log_path] = position
            if size <= position:
                continue
            amount = min(budget, size - position)
            try:
                with log_path.open("rb") as handle:
                    handle.seek(position)
                    data = handle.read(amount)
            except OSError:
                continue
            if not data:
                continue
            next_offset = client.upload_log_chunk(
                trainer_id=trainer_id,
                run_id=run_id,
                relative_path=relative,
                offset=position,
                data=data,
            )
            if next_offset < 0:
                self._positions[log_path] = -next_offset - 1
                continue
            self._positions[log_path] = next_offset
            budget -= len(data)
            uploaded += len(data)
        return uploaded

    def _has_pending_local_bytes(self, run_id: str) -> bool:
        run_root = self.root / run_id
        if not run_root.is_dir():
            return False
        for log_path in sorted(run_root.rglob("*")):
            if not log_path.is_file() or log_path.suffix.lower() not in (".log", ".txt", ".json"):
                continue
            try:
                size = log_path.stat().st_size
            except OSError:
                continue
            if self._positions.get(log_path, 0) != size:
                return True
        return False

    def flush_all(
        self,
        client: TrainingControlClient,
        *,
        trainer_id: str,
        run_id: str,
        maximum_passes: int = 10000,
    ) -> None:
        for _ in range(maximum_passes):
            self.flush_once(
                client,
                trainer_id=trainer_id,
                run_id=run_id,
            )
            if not self._has_pending_local_bytes(run_id):
                return
        raise RuntimeError(
            f"training log flush exceeded {maximum_passes} passes for run {run_id}"
        )


class ManagedProcess:
    def __init__(self) -> None:
        self.process: Optional[subprocess.Popen] = None
        self.command: tuple[str, ...] = ()
        self.revision = -1
        self.build_sha256 = ""
        self.run_id = ""
        self.environment_args: tuple[str, ...] = ()
        self.graceful_checkpoint = False
        self.stop_request_file: Optional[Path] = None

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
        run_id: str,
        state_file: Path,
        environment_args: Sequence[str],
        graceful_checkpoint: bool = False,
    ) -> None:
        self.stop()
        environment = os.environ.copy()
        environment["BEES_TRAINING_CONTROL_STATE_FILE"] = str(state_file)
        environment["BEES_TRAINING_ENV_ARGS_JSON"] = json.dumps(list(environment_args))
        environment["BEES_TRAINING_RUN_ID"] = str(run_id)
        environment["PYTHONUNBUFFERED"] = "1"
        if not run_id:
            raise ValueError("managed training process requires a non-empty run_id")
        log_dir = state_file.parent / "logs" / run_id
        log_dir.mkdir(parents=True, exist_ok=True)
        environment["BEES_TRAINING_LOG_DIR"] = str(log_dir)
        stop_request_file = state_file.parent / "managed-stop.request"
        try:
            stop_request_file.unlink()
        except FileNotFoundError:
            pass
        if graceful_checkpoint:
            environment[MANAGED_STOP_FILE_ENV] = str(stop_request_file)
        else:
            environment.pop(MANAGED_STOP_FILE_ENV, None)
        self.process = subprocess.Popen(
            list(command),
            env=environment,
            start_new_session=(os.name != "nt"),
        )
        self.command = tuple(command)
        self.revision = revision
        self.build_sha256 = build_sha256
        self.run_id = str(run_id)
        self.environment_args = tuple(str(value) for value in environment_args)
        self.graceful_checkpoint = bool(graceful_checkpoint)
        self.stop_request_file = stop_request_file if graceful_checkpoint else None

    def stop(self) -> None:
        process = self.process
        self.process = None
        if process is None or process.poll() is not None:
            return

        # The central learner owns optimizer/checkpoint state. Ask its continual-service child
        # to interrupt ML-Agents cleanly first so TrainerController finally saves checkpoint/ONNX
        # and learn.py writes timers/status. Forced process-tree termination is only a fallback.
        if self.graceful_checkpoint and self.stop_request_file is not None:
            try:
                self.stop_request_file.parent.mkdir(parents=True, exist_ok=True)
                self.stop_request_file.write_text("stop\n", encoding="ascii")
                deadline = time.monotonic() + GRACEFUL_CHECKPOINT_STOP_SECONDS
                while process.poll() is None and time.monotonic() < deadline:
                    time.sleep(0.25)
                if process.poll() is not None:
                    try:
                        self.stop_request_file.unlink()
                    except FileNotFoundError:
                        pass
                    return
                print(
                    "[Bees control] central learner did not finish checkpoint finalization "
                    "within the graceful stop window; forcing termination.",
                    file=sys.stderr,
                )
            except OSError as exc:
                print(
                    f"[Bees control] could not request graceful learner shutdown: {exc}",
                    file=sys.stderr,
                )

        # Dedicated wrappers may spawn Unity descendants. If graceful finalization failed or this
        # is a non-checkpoint-owning worker, terminate the whole process tree to preserve fail-closed
        # cluster behavior.
        if os.name == "nt":
            try:
                subprocess.run(
                    ["taskkill", "/PID", str(process.pid), "/T", "/F"],
                    stdout=subprocess.DEVNULL,
                    stderr=subprocess.DEVNULL,
                    check=False,
                )
                process.wait(timeout=15)
                return
            except Exception:
                pass
        else:
            try:
                os.killpg(process.pid, signal.SIGTERM)
                process.wait(timeout=15)
                return
            except Exception:
                try:
                    os.killpg(process.pid, signal.SIGKILL)
                    process.wait(timeout=5)
                    return
                except Exception:
                    pass

        try:
            process.kill()
            process.wait(timeout=5)
        except Exception:
            pass


def full_game_update_requires_deferred_restart(
    managed: ManagedProcess,
    desired_build_sha256: str,
    environment_args: Sequence[str],
) -> bool:
    """Keep a live player session intact when its executable/config becomes stale.

    The Unity runtime can switch the current process to inference immediately through the local
    control-state file. The canonical build/config is therefore applied on the next natural game
    launch instead of killing an active match.
    """
    return (
        managed.alive()
        and (
            managed.build_sha256 != desired_build_sha256
            or managed.environment_args != tuple(str(value) for value in environment_args)
        )
    )


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
        "run_id": str(desired.get("run_id", "")) if desired else "",
        "environment_args": list(desired.get("environment_args", ())) if desired else [],
        "lease_seconds": float(desired.get("lease_seconds", 20.0)) if desired else 20.0,
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
    parser.add_argument("--runtime-ready-file", default="")
    parser.add_argument("--heartbeat-seconds", type=float, default=5.0)
    parser.add_argument("--request-timeout-seconds", type=float, default=15.0)
    parser.add_argument("--shutdown-request-file", default="")
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
    raw_argv = list(sys.argv[1:] if argv is None else argv)
    args = _parser().parse_args(raw_argv)
    startup_source_sha = file_sha256(Path(__file__).resolve())
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
    shutdown_request_file = (
        Path(args.shutdown_request_file).expanduser().resolve()
        if str(args.shutdown_request_file).strip()
        else None
    )
    if shutdown_request_file is not None:
        try:
            shutdown_request_file.unlink()
        except FileNotFoundError:
            pass
    builds = ManagedBuildStore(install_root)
    state_file = install_root / "control-state.json"
    metrics = EpisodeLogMetrics(install_root / "logs")
    client = TrainingControlClient(
        args.server_url,
        token,
        timeout=args.request_timeout_seconds,
    )
    managed = ManagedProcess()
    preparer = BackgroundBuildPreparer(builds, client)
    log_uploader = TrainingLogUploader(install_root / "logs")
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
            if shutdown_request_file is not None and shutdown_request_file.is_file():
                print(
                    "[Bees control] supervisor shutdown requested; finalizing managed learner.",
                    flush=True,
                )
                break
            received_desired = False
            now = time.monotonic()
            offline = last_contact > 0 and now - last_contact > lease_seconds
            prepared_build_id, preparation_error = preparer.snapshot()
            if prepared_build_id and args.runtime_ready_file:
                try:
                    runtime_ready_build = Path(args.runtime_ready_file).expanduser().read_text(
                        encoding="ascii"
                    ).strip()
                except OSError:
                    runtime_ready_build = ""
                if runtime_ready_build != prepared_build_id:
                    prepared_build_id = ""
            heartbeat = default_heartbeat(
                trainer_id=args.trainer_id,
                role=args.role,
                platform=args.platform,
                process_state=managed.state(args.role, offline=offline),
                applied_revision=applied_revision,
                build=active_build,
                prepared_build_id=prepared_build_id,
                last_error=last_error or preparation_error,
                metrics=metrics.refresh(
                    str(desired.get("run_id", "")) if desired else managed.run_id
                ),
            )
            try:
                desired = client.heartbeat(heartbeat)
                received_desired = True
                last_contact = time.monotonic()
                lease_seconds = float(desired["lease_seconds"])
                last_error = ""

                mode = str(desired["desired_mode"])
                revision = int(desired["revision"])
                run_id = str(desired.get("run_id", ""))
                environment_args = tuple(str(value) for value in desired["environment_args"])
                descriptor = desired.get("build")
                preparer.request(desired.get("prepare_build"))

                desired_build_id = str(desired.get("desired_build_id", ""))
                active_build_id = str(active_build.get("build_id", "")) if active_build else ""
                source_changed = file_sha256(Path(__file__).resolve()) != startup_source_sha
                if source_changed and (
                    mode == "stopped"
                    or (desired_build_id and desired_build_id != active_build_id)
                ):
                    managed.stop()
                    try:
                        log_uploader.flush_all(
                            client,
                            trainer_id=args.trainer_id,
                            run_id=run_id,
                        )
                    except (ControlUnavailable, ControlRejected, OSError, ValueError, RuntimeError):
                        pass
                    os.execv(
                        sys.executable,
                        [sys.executable, str(Path(__file__).resolve()), *raw_argv],
                    )

                if mode == "stopped":
                    managed.stop()
                    pending_release = desired.get("pending_release")
                    if (
                        isinstance(pending_release, Mapping)
                        and bool(pending_release.get("incompatible"))
                    ):
                        log_uploader.flush_all(
                            client,
                            trainer_id=args.trainer_id,
                            run_id=run_id,
                        )
                    applied_revision = revision
                elif mode == "inference" and args.role == "full-game":
                    # Stop/inference changes apply live through control-state.json. Never kill an
                    # active player session merely to install a newer build or command-line config.
                    # If the game is already gone, prepare the canonical next launch immediately.
                    if descriptor and not managed.alive():
                        entrypoint, active_build = builds.ensure(client, descriptor)
                        desired_sha = str(active_build["archive_sha256"])
                        command = render_command(
                            command_template,
                            entrypoint,
                            environment_args,
                            str(active_build["build_id"]),
                            run_id,
                        )
                        managed.start(
                            command,
                            revision=revision,
                            build_sha256=desired_sha,
                            run_id=run_id,
                            state_file=state_file,
                            environment_args=environment_args,
                            graceful_checkpoint=(
                                args.role == "dedicated"
                                and args.trainer_id == "central-learner"
                            ),
                        )
                        applied_revision = revision
                    elif descriptor and full_game_update_requires_deferred_restart(
                        managed,
                        str(descriptor.get("archive_sha256", "")),
                        environment_args,
                    ):
                        # The latest desired mode is applied below, but the process revision remains
                        # intentionally stale so status shows that canonical build/config is pending.
                        pass
                    else:
                        applied_revision = revision
                elif mode == "training":
                    if not descriptor:
                        raise RuntimeError(
                            f"server has no canonical {args.platform} build published"
                        )
                    desired_sha = str(descriptor.get("archive_sha256", ""))
                    defer_full_game_update = (
                        args.role == "full-game"
                        and full_game_update_requires_deferred_restart(
                            managed,
                            desired_sha,
                            environment_args,
                        )
                    )
                    if defer_full_game_update:
                        # Preserve the running game. The Unity runtime switches it to InferenceOnly
                        # immediately; after the process exits naturally, the next heartbeat installs
                        # and launches the canonical build/config.
                        inference_desired = dict(desired)
                        inference_desired["desired_mode"] = "inference"
                        write_local_state(
                            state_file,
                            desired=inference_desired,
                            online=True,
                            last_error="canonical build/config pending next game launch",
                        )
                    else:
                        entrypoint, active_build = builds.ensure(client, descriptor)
                        desired_sha = str(active_build["archive_sha256"])
                        command = render_command(
                            command_template,
                            entrypoint,
                            environment_args,
                            str(active_build["build_id"]),
                            run_id,
                        )
                        needs_restart = (
                            not managed.alive()
                            or managed.build_sha256 != desired_sha
                            or managed.run_id != run_id
                            or managed.environment_args != environment_args
                            or managed.command != tuple(command)
                        )
                        if needs_restart:
                            managed.start(
                                command,
                                revision=revision,
                                build_sha256=desired_sha,
                                run_id=run_id,
                                state_file=state_file,
                                environment_args=environment_args,
                                graceful_checkpoint=(
                                    args.role == "dedicated"
                                    and args.trainer_id == "central-learner"
                                ),
                            )
                        applied_revision = revision
                else:
                    raise RuntimeError(f"unsupported desired mode {mode!r}")

                try:
                    log_uploader.flush_once(
                        client,
                        trainer_id=args.trainer_id,
                        run_id=run_id,
                    )
                except (ControlUnavailable, ControlRejected, OSError, ValueError) as exc:
                    print(
                        f"[Bees control] log upload deferred: {type(exc).__name__}: {exc}",
                        file=sys.stderr,
                    )

                # Publish training only after build/config reconciliation completed successfully.
                # A live full game with a pending canonical update already wrote an inference state
                # above and must remain inference until its next process launch.
                if not (
                    mode == "training"
                    and args.role == "full-game"
                    and descriptor
                    and full_game_update_requires_deferred_restart(
                        managed,
                        str(descriptor.get("archive_sha256", "")),
                        environment_args,
                    )
                ):
                    write_local_state(
                        state_file,
                        desired=desired,
                        online=True,
                        last_error="",
                    )
            except (ControlUnavailable, ControlRejected, OSError, ValueError, RuntimeError) as exc:
                last_error = f"{type(exc).__name__}: {exc}"
                offline = last_contact <= 0 or time.monotonic() - last_contact > lease_seconds
                write_local_state(
                    state_file,
                    desired=desired,
                    online=False,
                    last_error=last_error,
                )
                if args.role == "dedicated" and (offline or received_desired):
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
                if shutdown_request_file is not None and shutdown_request_file.is_file():
                    stop = True
                    break
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
        if shutdown_request_file is not None:
            try:
                shutdown_request_file.unlink()
            except FileNotFoundError:
                pass
        signal.signal(signal.SIGINT, old_sigint)
        signal.signal(signal.SIGTERM, old_sigterm)


if __name__ == "__main__":
    raise SystemExit(main())
