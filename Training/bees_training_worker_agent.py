"""Persistent Bees training worker controlled by BeesServer desired state.

Dedicated workers fail closed: once the server lease expires, their managed process is terminated.
Full-game workers fail over by leaving the game process running; ordinary Bees gameplay already uses
the deployed policy for inference and records telemetry to local pending files when uploads are
unavailable. When control returns, the worker reconciles build/config state and reconnects.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import math
import os
import re
import signal
import shutil
import subprocess
import sys
import threading
import time
from collections import deque
from pathlib import Path
from typing import Any, Callable, Mapping, Optional, Sequence

from bees_process_safety import (
    atomic_write_text,
    configure_child_health,
    popen_owned,
    read_managed_health,
)
from bees_training_control import (
    ControlRejected,
    TrainingLogOffsetMismatch,
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
WORKER_ENVS_PLACEHOLDER = "{worker_envs}"
MANAGED_STOP_FILE_ENV = "BEES_TRAINING_STOP_FILE"
THROUGHPUT_METRICS_ENV = "BEES_TRAINING_THROUGHPUT_FILE"
WORKER_ENVS_TARGET_ENV = "BEES_TRAINING_WORKER_ENVS_FILE"
NETWORK_TRAFFIC_STATE_FILE = "worker-network-traffic.json"
BUILD_ID_ENV = "BEES_TRAINING_BUILD_ID"
COMPATIBILITY_KEY_ENV = "BEES_TRAINING_COMPATIBILITY_KEY"
ENVIRONMENT_ID_ENV = "BEES_TRAINING_ENVIRONMENT_ID"
GRACEFUL_CHECKPOINT_STOP_SECONDS = 120.0
CHILD_HEALTH_STARTUP_GRACE_SECONDS = 30.0
CHILD_HEALTH_STALE_SECONDS = 30.0
CHILD_HEALTH_STARTUP_PHASE_TIMEOUT_SECONDS = 180.0
CHILD_HEALTH_PROGRESS_STALE_SECONDS = 120.0
GRACEFUL_REMOTE_STOP_SECONDS = 20.0
MANAGED_RESTART_STABLE_SECONDS = 60.0
MANAGED_RESTART_BACKOFF_SECONDS = (1.0, 2.0, 5.0, 10.0, 30.0, 60.0, 120.0)


MAX_RETAINED_RUN_LOG_DIRS = 3
RUN_ID_RE = re.compile(r"^[A-Za-z0-9._-]+$")


def _validate_run_id(run_id: str) -> str:
    value = str(run_id or "")
    if value and (value in {".", ".."} or not RUN_ID_RE.fullmatch(value)):
        raise ValueError(f"unsafe training run id: {value!r}")
    return value


def _prune_run_log_directories(root: Path, current_run_id: str) -> None:
    if not root.is_dir():
        return
    current = str(current_run_id or "").strip()
    candidates = []
    try:
        children = list(root.iterdir())
    except OSError:
        return
    for child in children:
        if not child.is_dir() or child.name == current:
            continue
        try:
            candidates.append((child.stat().st_mtime_ns, child))
        except OSError:
            continue
    candidates.sort(reverse=True)
    for _, path in candidates[MAX_RETAINED_RUN_LOG_DIRS - 1:]:
        shutil.rmtree(path, ignore_errors=True)


def heartbeat_retry_delay(received_desired: bool, heartbeat_seconds: float) -> float:
    """Retry failed control heartbeats quickly without extending the safety lease."""
    normal = max(0.1, float(heartbeat_seconds))
    return normal if received_desired else min(1.0, normal)


def heartbeat_with_transport_retry(
    client: TrainingControlClient,
    payload: Mapping[str, object],
    *,
    attempts: int = 2,
    retry_delay_seconds: float = 0.25,
) -> Mapping[str, Any]:
    """Mask a single transient transport stall without weakening the server lease.

    Heartbeats are safe to repeat: the server treats each as the current trainer snapshot.
    A lost response can therefore be retried immediately instead of turning one short tailnet
    forwarding hiccup into a worker-visible outage.
    """
    maximum = max(1, int(attempts))
    last_error: Optional[ControlUnavailable] = None
    for attempt in range(1, maximum + 1):
        try:
            return client.heartbeat(payload)
        except ControlUnavailable as exc:
            last_error = exc
            if attempt >= maximum:
                raise
            time.sleep(max(0.0, float(retry_delay_seconds)))
    assert last_error is not None
    raise last_error


def heartbeat_last_error(
    last_error: str,
    preparation_error: str,
    managed_health_error: str,
) -> str:
    return str(last_error or preparation_error or managed_health_error or "")


def worker_health_error_after_exception(
    current_last_error: str,
    exc: BaseException,
    error_text: str,
) -> str:
    """Keep control transport loss out of the worker-process health channel."""
    if isinstance(exc, ControlUnavailable):
        return current_last_error
    return error_text


def effective_metrics_run_id(
    desired: Optional[Mapping[str, Any]],
    managed_run_id: str,
) -> str:
    if isinstance(desired, Mapping):
        desired_run_id = str(desired.get("run_id", "") or "").strip()
        if desired_run_id:
            return desired_run_id
    return str(managed_run_id or "").strip()


def environment_args_identity(environment_args: Sequence[str]) -> str:
    payload = json.dumps(
        [str(value) for value in environment_args],
        ensure_ascii=False,
        separators=(",", ":"),
    ).encode("utf-8")
    return hashlib.sha256(payload).hexdigest()
EPISODE_LOG_PATTERN = re.compile(
    r"RL 1v1 episode=(\d+).*?timeout=(True|False) duration=([\d.]+)s "
    r"bee_tsv=(\d+)->(\d+) human_tsv=(\d+)->(\d+).*?"
    r"bee_fire_requests=(\d+) bee_shots=(\d+) bee_hits=(\d+) bee_damage=(\d+).*?"
    r"human_fire_requests=(\d+) human_shots=(\d+) human_hits=(\d+) human_damage=(\d+)"
)


def _log_file_identity(file_stat: os.stat_result) -> Optional[tuple[int, int]]:
    identity = (int(file_stat.st_dev), int(file_stat.st_ino))
    return None if identity[1] == 0 else identity


def _episode_numeric_field(line: str, key: str) -> Optional[float]:
    match = re.search(
        rf"\b{re.escape(key)}=(-?[0-9]+(?:\.[0-9]+)?)(?:deg|%)?",
        line,
    )
    if not match:
        return None
    try:
        value = float(match.group(1))
    except (ValueError, OverflowError):
        return None
    return value if math.isfinite(value) else None


class EpisodeLogMetrics:
    def __init__(self, root: Path, window: int = 100) -> None:
        self.root = root
        self.window = max(1, int(window))
        self._episodes = deque(maxlen=self.window)
        self._positions: dict[Path, int] = {}
        self._file_identities: dict[Path, Optional[tuple[int, int]]] = {}
        self._pending: dict[Path, str] = {}
        self._run_id = ""

    def refresh(self, run_id: str = "") -> dict[str, object]:
        run_id = _validate_run_id(run_id)
        if run_id != self._run_id:
            self._run_id = run_id
            self._episodes.clear()
            self._positions.clear()
            self._file_identities.clear()
            self._pending.clear()
        scan_root = self.root / run_id if run_id else self.root
        if scan_root.is_dir():
            bounded_logs = sorted(
                path for path in scan_root.rglob("BeesEpisode-*.log")
                if not path.is_symlink()
            )
            log_paths = bounded_logs or sorted(
                path for path in scan_root.rglob("Player-*.log")
                if not path.is_symlink()
            )
            for log_path in log_paths:
                self._read_new(log_path)
        return self.snapshot()

    def _read_new(self, log_path: Path) -> None:
        try:
            file_stat = log_path.stat()
            size = file_stat.st_size
        except OSError:
            return
        identity = _log_file_identity(file_stat)
        position = self._positions.get(log_path)
        previous_identity = self._file_identities.get(log_path)
        identity_changed = (
            previous_identity is not None
            and identity is not None
            and previous_identity != identity
        )
        if identity_changed:
            position = 0
            self._pending.pop(log_path, None)
        first_read = position is None
        if position is None:
            position = max(0, size - 4 * 1024 * 1024)
        if size < position:
            position = 0
            self._pending.pop(log_path, None)
        try:
            with log_path.open("rb") as handle:
                opened_identity = _log_file_identity(os.fstat(handle.fileno()))
                if identity is not None and opened_identity != identity:
                    # The path was replaced after stat but before open. Do not attribute
                    # bytes from the new generation to the old generation's cursor.
                    return
                previous_byte = b""
                if first_read and position > 0:
                    handle.seek(position - 1)
                    previous_byte = handle.read(1)
                handle.seek(position)
                raw_data = handle.read()
        except OSError:
            return
        data = raw_data
        if first_read and position > 0 and previous_byte not in (b"\n", b"\r"):
            separators = [
                index for index in (data.find(b"\n"), data.find(b"\r"))
                if index >= 0
            ]
            data = data[min(separators) + 1:] if separators else b""
        self._positions[log_path] = position + len(raw_data)
        self._file_identities[log_path] = identity
        if not data:
            return
        text = self._pending.get(log_path, "") + data.decode("utf-8", errors="replace")
        complete = text.endswith("\n") or text.endswith("\r")
        lines = text.splitlines()
        if not complete and lines:
            self._pending[log_path] = lines.pop()
        else:
            self._pending[log_path] = ""
        for line in lines:
            match = EPISODE_LOG_PATTERN.search(line)
            if not match:
                continue
            values = match.groups()
            try:
                duration = float(values[2])
            except (ValueError, OverflowError):
                # Ignore malformed complete lines instead of letting diagnostics terminate
                # the training supervisor before its heartbeat error handler.
                continue
            if not math.isfinite(duration):
                continue

            timeout = values[1] == "True"
            try:
                bee_final = int(values[4])
                human_final = int(values[6])
                self._episodes.append({
                    "episode": int(values[0]),
                    "timeout": timeout,
                    "duration": duration,
                    "bee_win": (not timeout and bee_final > 0 and human_final == 0),
                    "human_win": (not timeout and human_final > 0 and bee_final == 0),
                    "bee_shots": int(values[8]),
                    "bee_hits": int(values[9]),
                    "human_shots": int(values[12]),
                    "human_hits": int(values[13]),
                    "bee_aim_samples": int(
                        _episode_numeric_field(line, "bee_aim_samples") or 0
                    ),
                    "bee_aim_error_deg": _episode_numeric_field(
                        line, "bee_aim_error"
                    ),
                    "bee_aim_within_5_pct": _episode_numeric_field(
                        line, "bee_aim_within_5deg"
                    ),
                    "bee_turret_aligned_pct": _episode_numeric_field(
                        line, "bee_turret_aligned"
                    ),
                    "human_aim_samples": int(
                        _episode_numeric_field(line, "human_aim_samples") or 0
                    ),
                    "human_aim_error_deg": _episode_numeric_field(
                        line, "human_aim_error"
                    ),
                    "human_aim_within_5_pct": _episode_numeric_field(
                        line, "human_aim_within_5deg"
                    ),
                    "human_turret_aligned_pct": _episode_numeric_field(
                        line, "human_turret_aligned"
                    ),
                })
            except (ValueError, OverflowError):
                # Oversized or malformed fields in a damaged log must not terminate the supervisor.
                continue

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

        def weighted_metric(sample_key: str, value_key: str) -> Optional[float]:
            weighted_total = 0.0
            sample_total = 0
            for item in episodes:
                samples = int(item.get(sample_key, 0) or 0)
                value = item.get(value_key)
                if samples <= 0 or value is None:
                    continue
                weighted_total += samples * float(value)
                sample_total += samples
            return (
                round(weighted_total / sample_total, 2)
                if sample_total > 0
                else None
            )

        bee_aim_samples = sum(int(item.get("bee_aim_samples", 0) or 0) for item in episodes)
        human_aim_samples = sum(
            int(item.get("human_aim_samples", 0) or 0) for item in episodes
        )
        return {
            "window_episodes": count,
            "last_episode": max(int(item["episode"]) for item in episodes),
            "timeout_pct": round(100.0 * timeouts / count, 2),
            "bee_win_pct": round(100.0 * bee_wins / count, 2),
            "human_win_pct": round(100.0 * human_wins / count, 2),
            "draw_pct": round(100.0 * draws / count, 2),
            "avg_duration_s": round(
                sum(float(item["duration"]) for item in episodes) / count, 2),
            # Retain the historical combat-effectiveness ratio for compatibility, but
            # expose it as hits-per-shot as well because explosions/bombs can produce
            # multiple damage events from one firing action.
            "bee_hit_pct": round(100.0 * bee_hits / bee_shots, 2) if bee_shots else 0.0,
            "human_hit_pct": round(
                100.0 * human_hits / human_shots, 2) if human_shots else 0.0,
            "bee_hits_per_shot": round(bee_hits / bee_shots, 3) if bee_shots else 0.0,
            "human_hits_per_shot": round(
                human_hits / human_shots, 3) if human_shots else 0.0,
            "bee_shots_per_episode": round(bee_shots / count, 2),
            "human_shots_per_episode": round(human_shots / count, 2),
            "bee_aim_samples": bee_aim_samples,
            "human_aim_samples": human_aim_samples,
            "bee_aim_error_deg": weighted_metric(
                "bee_aim_samples", "bee_aim_error_deg"
            ),
            "human_aim_error_deg": weighted_metric(
                "human_aim_samples", "human_aim_error_deg"
            ),
            "bee_aim_within_5_pct": weighted_metric(
                "bee_aim_samples", "bee_aim_within_5_pct"
            ),
            "human_aim_within_5_pct": weighted_metric(
                "human_aim_samples", "human_aim_within_5_pct"
            ),
            "bee_turret_aligned_pct": weighted_metric(
                "bee_aim_samples", "bee_turret_aligned_pct"
            ),
            "human_turret_aligned_pct": weighted_metric(
                "human_aim_samples", "human_turret_aligned_pct"
            ),
        }


def read_throughput_metrics(
    path: Optional[Path],
    *,
    expected_pid: Optional[int] = None,
    expected_env_count: Optional[int] = None,
) -> dict[str, object]:
    if path is None:
        return {}
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeDecodeError, json.JSONDecodeError):
        return {}
    if not isinstance(value, Mapping):
        return {}
    pid = value.get("pid")
    env_count = value.get("env_count")
    accepted_steps = value.get("accepted_steps_total")
    accepted_trajectories = value.get("accepted_trajectories_total")
    learner_consumed_steps = value.get("learner_consumed_steps_total")
    learner_step_total = value.get("learner_step_total")
    learner_consumed_rate = value.get("learner_consumed_steps_per_sec")
    queue_depth = value.get("upload_queue_depth")
    network_sent = value.get("network_sent_bytes_total")
    network_received = value.get("network_received_bytes_total")
    network_rate = value.get("network_mib_per_s")
    session_failures = value.get("session_failures_total")
    failure_age = value.get("seconds_since_last_session_failure")
    failure_type = value.get("last_session_failure_type")
    failure_message = value.get("last_session_failure_message")
    policy_cycle = value.get("policy_cycle")
    runtime_version = value.get("runtime_version")
    resize_failed_target = value.get("env_resize_failed_target")
    resize_error = value.get("env_resize_error")
    resize_failure_time = value.get("env_resize_failure_unix_seconds")
    if (
        not isinstance(pid, int)
        or isinstance(pid, bool)
        or pid <= 0
        or not isinstance(env_count, int)
        or isinstance(env_count, bool)
        or not 1 <= env_count <= 64
        or not isinstance(accepted_steps, int)
        or isinstance(accepted_steps, bool)
        or accepted_steps < 0
        or not isinstance(accepted_trajectories, int)
        or isinstance(accepted_trajectories, bool)
        or accepted_trajectories < 0
        or not isinstance(learner_consumed_steps, int)
        or isinstance(learner_consumed_steps, bool)
        or learner_consumed_steps < 0
        or (
            learner_step_total is not None
            and (
                not isinstance(learner_step_total, int)
                or isinstance(learner_step_total, bool)
                or learner_step_total < 0
            )
        )
        or (
            learner_consumed_rate is not None
            and (
                not isinstance(learner_consumed_rate, (int, float))
                or isinstance(learner_consumed_rate, bool)
                or not math.isfinite(float(learner_consumed_rate))
                or float(learner_consumed_rate) < 0.0
            )
        )
        or not isinstance(queue_depth, int)
        or isinstance(queue_depth, bool)
        or queue_depth < 0
    ):
        return {}
    failure_present = any(
        item is not None
        for item in (
            session_failures,
            failure_age,
            failure_type,
            failure_message,
        )
    )
    if failure_present and (
        not isinstance(session_failures, int)
        or isinstance(session_failures, bool)
        or session_failures < 0
        or (
            failure_age is not None
            and (
                not isinstance(failure_age, (int, float))
                or isinstance(failure_age, bool)
                or not math.isfinite(float(failure_age))
                or float(failure_age) < 0.0
            )
        )
        or (
            failure_type is not None
            and not isinstance(failure_type, str)
        )
        or (
            failure_message is not None
            and not isinstance(failure_message, str)
        )
    ):
        return {}

    policy_cycle_present = policy_cycle is not None
    if policy_cycle_present and (
        not isinstance(policy_cycle, int)
        or isinstance(policy_cycle, bool)
        or policy_cycle < 0
    ):
        return {}

    runtime_version_present = runtime_version is not None
    if runtime_version_present and (
        not isinstance(runtime_version, str)
        or len(runtime_version.strip()) != 64
        or any(ch not in "0123456789abcdef" for ch in runtime_version.strip().lower())
    ):
        return {}

    resize_present = any(
        item is not None
        for item in (resize_failed_target, resize_error, resize_failure_time)
    )
    if resize_present and (
        not isinstance(resize_failed_target, int)
        or isinstance(resize_failed_target, bool)
        or not 1 <= resize_failed_target <= 64
        or not isinstance(resize_error, str)
        or not resize_error.strip()
        or (
            resize_failure_time is not None
            and (
                not isinstance(resize_failure_time, (int, float))
                or isinstance(resize_failure_time, bool)
                or not math.isfinite(float(resize_failure_time))
                or float(resize_failure_time) < 0.0
            )
        )
    ):
        return {}

    traffic_present = any(
        item is not None for item in (network_sent, network_received, network_rate)
    )
    if traffic_present and (
        not isinstance(network_sent, int)
        or isinstance(network_sent, bool)
        or network_sent < 0
        or not isinstance(network_received, int)
        or isinstance(network_received, bool)
        or network_received < 0
        or not isinstance(network_rate, (int, float))
        or isinstance(network_rate, bool)
        or not math.isfinite(float(network_rate))
        or float(network_rate) < 0.0
    ):
        return {}
    if expected_pid is not None and pid != expected_pid:
        return {}
    if expected_env_count is not None and env_count != expected_env_count:
        return {}
    result = {
        "pid": pid,
        "env_count": env_count,
        "accepted_steps_total": accepted_steps,
        "accepted_trajectories_total": accepted_trajectories,
        "learner_consumed_steps_total": learner_consumed_steps,
        "upload_queue_depth": queue_depth,
    }
    if learner_step_total is not None:
        result["learner_step_total"] = int(learner_step_total)
    if learner_consumed_rate is not None:
        result["learner_consumed_steps_per_sec"] = float(learner_consumed_rate)
    if policy_cycle_present:
        result["policy_cycle"] = int(policy_cycle)
    if runtime_version_present:
        result["runtime_version"] = runtime_version.strip().lower()
    if resize_present:
        result["env_resize_failed_target"] = int(resize_failed_target)
        result["env_resize_error"] = resize_error.strip()
        if resize_failure_time is not None:
            result["env_resize_failure_unix_seconds"] = float(resize_failure_time)
    if failure_present:
        result.update(
            {
                "session_failures_total": session_failures,
                "seconds_since_last_session_failure": (
                    None if failure_age is None else float(failure_age)
                ),
                "last_session_failure_type": str(failure_type or ""),
                "last_session_failure_message": str(failure_message or "").strip(),
            }
        )
    if traffic_present:
        result.update(
            {
                "network_sent_bytes_total": network_sent,
                "network_received_bytes_total": network_received,
                "network_mib_per_s": float(network_rate),
            }
        )
    return result


def read_persisted_network_traffic(
    throughput_metrics_path: Optional[Path],
    *,
    expected_run_id: str,
    install_root: Optional[Path] = None,
) -> dict[str, object]:
    if not expected_run_id:
        return {}
    if throughput_metrics_path is not None:
        path = throughput_metrics_path.with_name(NETWORK_TRAFFIC_STATE_FILE)
    elif install_root is not None:
        path = Path(install_root).expanduser().resolve() / NETWORK_TRAFFIC_STATE_FILE
    else:
        return {}
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeDecodeError, json.JSONDecodeError):
        return {}
    if not isinstance(value, Mapping):
        return {}
    if str(value.get("run_id", "")) != str(expected_run_id):
        return {}
    sent = value.get("sent_bytes_total")
    received = value.get("received_bytes_total")
    if (
        not isinstance(sent, int)
        or isinstance(sent, bool)
        or sent < 0
        or not isinstance(received, int)
        or isinstance(received, bool)
        or received < 0
    ):
        return {}
    return {
        "network_sent_bytes_total": sent,
        "network_received_bytes_total": received,
    }


def _add_persisted_network_traffic(
    snapshot: dict[str, object],
    throughput_metrics_path: Optional[Path],
    *,
    run_id: str,
    install_root: Optional[Path] = None,
) -> None:
    current = snapshot.get("throughput")
    if (
        isinstance(current, Mapping)
        and current.get("network_sent_bytes_total") is not None
        and current.get("network_received_bytes_total") is not None
    ):
        return
    persisted = read_persisted_network_traffic(
        throughput_metrics_path,
        expected_run_id=run_id,
        install_root=install_root,
    )
    if not persisted:
        return
    merged = dict(current) if isinstance(current, Mapping) else {}
    merged.update(persisted)
    snapshot["throughput"] = merged


def render_command(
    template: Sequence[str],
    entrypoint: Path,
    environment_args: Sequence[str],
    build_id: str = "",
    run_id: str = "",
    worker_env_count: Optional[int] = None,
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
        elif token == WORKER_ENVS_PLACEHOLDER:
            if worker_env_count is None:
                raise ValueError("managed worker command requires worker env count")
            rendered.append(str(worker_env_count))
        else:
            replacement = (
                token.replace(ENV_PLACEHOLDER, str(entrypoint))
                .replace(BUILD_ID_PLACEHOLDER, str(build_id))
                .replace(RUN_ID_PLACEHOLDER, str(run_id))
            )
            if WORKER_ENVS_PLACEHOLDER in replacement:
                if worker_env_count is None:
                    raise ValueError("managed worker command requires worker env count")
                replacement = replacement.replace(
                    WORKER_ENVS_PLACEHOLDER, str(worker_env_count)
                )
            rendered.append(replacement)
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
        except Exception as exc:
            # Preparation is advisory/background work. Never let an unexpected exception
            # silently kill the preparer without surfacing a retryable error to the next heartbeat.
            with self._lock:
                self.last_error = f"{type(exc).__name__}: {exc}"

    def snapshot(self) -> tuple[str, str]:
        with self._lock:
            return self.prepared_build_id, self.last_error


def _sha256_prefix(path: Path, byte_count: int) -> Optional[str]:
    if byte_count < 0:
        return None
    digest = hashlib.sha256()
    remaining = byte_count
    try:
        with path.open("rb") as handle:
            while remaining > 0:
                chunk = handle.read(min(1024 * 1024, remaining))
                if not chunk:
                    return None
                digest.update(chunk)
                remaining -= len(chunk)
    except OSError:
        return None
    return digest.hexdigest()


class TrainingLogUploader:
    CHUNK_BYTES = 1024 * 1024
    MAX_FILE_UPLOAD_BYTES = 64 * 1024 * 1024
    FINALIZE_KEEPALIVE_SECONDS = 5.0

    def __init__(self, root: Path) -> None:
        self.root = root
        self._positions: dict[Path, int] = {}
        self._file_identities: dict[Path, Optional[tuple[int, int]]] = {}
        self._remote_paths: dict[Path, str] = {}
        self._next_path: Optional[Path] = None

    def _generation_remote_path(
        self,
        log_path: Path,
        relative_path: str,
        size: int,
    ) -> str:
        capped_size = min(max(0, int(size)), self.MAX_FILE_UPLOAD_BYTES)
        digest = _sha256_prefix(log_path, capped_size)
        if digest is None:
            raise ControlRejected(
                "could not fingerprint divergent local training log generation"
            )
        return (
            "generations/" +
            digest[:24] + "-" + str(capped_size) + "/" +
            relative_path
        )

    def flush_once(
        self,
        client: TrainingControlClient,
        *,
        trainer_id: str,
        run_id: str,
    ) -> int:
        run_id = _validate_run_id(run_id)
        if not run_id:
            return 0
        run_root = self.root / run_id
        if not run_root.is_dir():
            return 0
        budget = self.CHUNK_BYTES
        uploaded = 0
        log_paths = sorted(run_root.rglob("*"))
        if not log_paths:
            return 0
        if self._next_path in log_paths:
            start_index = log_paths.index(self._next_path)
            log_paths = log_paths[start_index:] + log_paths[:start_index]
        for path_index, log_path in enumerate(log_paths):
            if budget <= 0:
                break
            if (
                not log_path.is_file()
                or log_path.is_symlink()
                or log_path.suffix.lower() not in (".log", ".txt", ".json")
            ):
                continue
            relative = log_path.relative_to(run_root).as_posix()
            remote_relative = self._remote_paths.get(log_path, relative)
            try:
                file_stat = log_path.stat()
                size = file_stat.st_size
            except OSError:
                continue
            identity = _log_file_identity(file_stat)
            previous_identity = self._file_identities.get(log_path)
            identity_changed = (
                previous_identity is not None
                and identity is not None
                and previous_identity != identity
            )
            position = self._positions.get(log_path, 0)
            if identity_changed or size < position:
                # Unity can replace/truncate Player-N.log when an actor generation restarts.
                # Preserve the already-uploaded generation instead of destructively resetting
                # its server copy. The replacement continues under a deterministic generation
                # path so both byte streams remain available.
                remote_relative = self._generation_remote_path(
                    log_path,
                    relative,
                    size,
                )
                self._remote_paths[log_path] = remote_relative
                position = 0
                self._positions[log_path] = 0
            self._file_identities[log_path] = identity
            if size <= position or position >= self.MAX_FILE_UPLOAD_BYTES:
                continue
            amount = min(
                budget,
                size - position,
                self.MAX_FILE_UPLOAD_BYTES - position,
            )
            try:
                with log_path.open("rb") as handle:
                    opened_identity = _log_file_identity(os.fstat(handle.fileno()))
                    if identity is not None and opened_identity != identity:
                        # A rotation between stat and open must not append bytes from the
                        # replacement file at the previous generation's remote offset.
                        continue
                    handle.seek(position)
                    data = handle.read(amount)
            except OSError:
                continue
            if not data:
                continue
            try:
                next_offset = client.upload_log_chunk(
                    trainer_id=trainer_id,
                    run_id=run_id,
                    relative_path=remote_relative,
                    offset=position,
                    data=data,
                )
            except TrainingLogOffsetMismatch as mismatch:
                expected_offset = mismatch.expected_offset
                if expected_offset == 0:
                    self._positions[log_path] = 0
                    continue
                local_prefix_sha256 = (
                    _sha256_prefix(log_path, expected_offset)
                    if expected_offset <= size
                    else None
                )
                if (
                    local_prefix_sha256 is None
                    or not mismatch.expected_sha256
                    or local_prefix_sha256 != mismatch.expected_sha256
                ):
                    next_remote_relative = self._generation_remote_path(
                        log_path,
                        relative,
                        size,
                    )
                    if next_remote_relative == remote_relative:
                        # The deterministic generation identity says this is the same
                        # complete local snapshot, yet its remote prefix differs. That is
                        # genuine remote corruption/collision rather than another local
                        # log replacement, so fail closed instead of overwriting it.
                        raise ControlRejected(
                            "preserved training log generation conflicts with its "
                            "existing remote copy"
                        ) from mismatch
                    # The local path can be replaced/truncated again after it has already
                    # moved under generations/. Windows may preserve the file identity
                    # across that rewrite, and the replacement can regrow beyond our old
                    # cursor before the next scan. Advance to a fresh deterministic
                    # generation path rather than retrying the stale preserved path forever.
                    self._remote_paths[log_path] = next_remote_relative
                    self._positions[log_path] = 0
                    continue
                self._positions[log_path] = expected_offset
                continue
            self._positions[log_path] = next_offset
            budget -= len(data)
            uploaded += len(data)
            self._next_path = log_paths[(path_index + 1) % len(log_paths)]
        return uploaded

    def _has_pending_local_bytes(self, run_id: str) -> bool:
        run_id = _validate_run_id(run_id)
        run_root = self.root / run_id
        if not run_root.is_dir():
            return False
        for log_path in sorted(run_root.rglob("*")):
            if (
                not log_path.is_file()
                or log_path.is_symlink()
                or log_path.suffix.lower() not in (".log", ".txt", ".json")
            ):
                continue
            try:
                size = log_path.stat().st_size
            except OSError:
                continue
            terminal_offset = min(size, self.MAX_FILE_UPLOAD_BYTES)
            uploaded_position = min(
                self._positions.get(log_path, 0),
                self.MAX_FILE_UPLOAD_BYTES,
            )
            if uploaded_position != terminal_offset:
                return True
        return False

    def flush_all(
        self,
        client: TrainingControlClient,
        *,
        trainer_id: str,
        run_id: str,
        maximum_passes: int = 10000,
        progress_callback: Optional[Callable[[], None]] = None,
    ) -> None:
        keepalive_stop = threading.Event()
        keepalive_thread: Optional[threading.Thread] = None

        if progress_callback is not None:
            def keepalive() -> None:
                while not keepalive_stop.wait(self.FINALIZE_KEEPALIVE_SECONDS):
                    try:
                        progress_callback()
                    except Exception:
                        # Log preservation must not fail because a best-effort lease refresh
                        # raced a control cutover. The main flush request still owns success/failure.
                        pass

            try:
                progress_callback()
            except Exception:
                pass
            keepalive_thread = threading.Thread(
                target=keepalive,
                name="bees-log-flush-keepalive",
                daemon=True,
            )
            keepalive_thread.start()

        try:
            for _ in range(maximum_passes):
                self.flush_once(
                    client,
                    trainer_id=trainer_id,
                    run_id=run_id,
                )
                if progress_callback is not None:
                    try:
                        progress_callback()
                    except Exception:
                        pass
                if not self._has_pending_local_bytes(run_id):
                    return
            raise RuntimeError(
                f"training log flush exceeded {maximum_passes} passes for run {run_id}"
            )
        finally:
            keepalive_stop.set()
            if keepalive_thread is not None:
                keepalive_thread.join(timeout=1.0)


def _is_windows() -> bool:
    return os.name == "nt"


_POSIX_SIGTERM = getattr(signal, "SIGTERM", 15)
_POSIX_SIGKILL = getattr(signal, "SIGKILL", 9)


class ManagedProcess:
    def __init__(self) -> None:
        self.process: Optional[subprocess.Popen] = None
        self.command: tuple[str, ...] = ()
        self.revision = -1
        self.build_sha256 = ""
        self.build_id = ""
        self.run_id = ""
        self.compatibility_key = ""
        self.environment_args: tuple[str, ...] = ()
        self.worker_env_count: Optional[int] = None
        self.throughput_metrics_file: Optional[Path] = None
        self.worker_env_target_file: Optional[Path] = None
        self.graceful_checkpoint = False
        self.graceful_remote_stop = False
        self.stop_request_file: Optional[Path] = None
        self.health_file: Optional[Path] = None
        self.health_token = ""
        self.health_required = False
        self.started_monotonic = 0.0
        self.restart_failure_streak = 0
        self.restart_not_before_monotonic = 0.0
        self.last_exit_code: Optional[int] = None
        self.last_exit_error = ""

    def alive(self) -> bool:
        return self.process is not None and self.process.poll() is None

    def restart_delay(self, command: Sequence[str]) -> float:
        if tuple(command) != self.command:
            return 0.0
        return max(0.0, self.restart_not_before_monotonic - time.monotonic())

    def record_exit(self, code: Optional[int], error: str = "") -> float:
        now = time.monotonic()
        uptime = (
            max(0.0, now - self.started_monotonic)
            if self.started_monotonic > 0.0
            else 0.0
        )
        if uptime >= MANAGED_RESTART_STABLE_SECONDS:
            self.restart_failure_streak = 0
        self.restart_failure_streak += 1
        index = min(
            self.restart_failure_streak - 1,
            len(MANAGED_RESTART_BACKOFF_SECONDS) - 1,
        )
        delay = MANAGED_RESTART_BACKOFF_SECONDS[index]
        self.restart_not_before_monotonic = now + delay
        self.last_exit_code = code
        self.last_exit_error = str(error or "")
        self.process = None
        return delay

    def set_worker_env_target(self, env_count: Optional[int]) -> None:
        if env_count is None or self.worker_env_target_file is None:
            return
        if not 1 <= int(env_count) <= 64:
            raise ValueError("worker env target must be in 1-64")
        atomic_write_text(
            self.worker_env_target_file,
            f"{int(env_count)}\n",
            encoding="ascii",
        )

    def clear_restart_backoff(self) -> None:
        self.restart_failure_streak = 0
        self.restart_not_before_monotonic = 0.0
        self.last_exit_code = None
        self.last_exit_error = ""

    def health(self) -> Optional[dict[str, Any]]:
        if not self.health_required:
            return {"state": "ready", "error": ""}
        return read_managed_health(self.health_file, self.health_token)

    def throughput_expected_pid(self) -> Optional[int]:
        """Return the authenticated child PID that owns live throughput metrics.

        On Windows, a virtual-environment launcher can remain as the Popen-owned process while
        the real Python interpreter writes child health and throughput telemetry under a different
        PID. Child health is token-authenticated for this managed launch, so prefer its PID when
        available and fall back to the launcher PID otherwise.
        """
        process = self.process
        if process is None:
            return None
        if self.health_required:
            health = self.health()
            health_pid = health.get("pid") if isinstance(health, Mapping) else None
            if (
                isinstance(health_pid, int)
                and not isinstance(health_pid, bool)
                and health_pid > 0
            ):
                return health_pid
        pid = getattr(process, "pid", None)
        return pid if isinstance(pid, int) and not isinstance(pid, bool) and pid > 0 else None

    def terminal_health_error(self) -> str:
        """Return an authenticated child error even after the managed process has exited."""
        if not self.health_required:
            return ""
        health = self.health()
        if not isinstance(health, Mapping):
            return ""
        if str(health.get("state", "")) != "error":
            return ""
        return str(health.get("error") or "managed child reported an internal failure")

    def health_error(self) -> str:
        if not self.health_required or not self.alive():
            return ""
        terminal_error = self.terminal_health_error()
        if terminal_error:
            return terminal_error
        health = self.health()
        if health is None:
            if (
                self.started_monotonic > 0.0
                and time.monotonic() - self.started_monotonic
                >= CHILD_HEALTH_STARTUP_GRACE_SECONDS
            ):
                return (
                    "managed child has not published ready health within "
                    f"{CHILD_HEALTH_STARTUP_GRACE_SECONDS:g} seconds"
                )
            return ""
        state = str(health.get("state", ""))
        if state == "error":
            return str(health.get("error") or "managed child reported an internal failure")

        details = health.get("details")
        details_map = details if isinstance(details, Mapping) else {}
        component = str(details_map.get("component", "") or "")
        phase = str(details_map.get("phase", "") or "")
        now = time.time()
        updated = health.get("updated_unix_seconds")
        if isinstance(updated, (int, float)) and not isinstance(updated, bool):
            age = max(0.0, now - float(updated))
            # Once rollout is running, actual rollout progress is the health signal.
            # The sidecar health-file writer is diagnostic and can be delayed briefly by host
            # scheduling or filesystem activity without implying that Unity rollout is stalled.
            # Keep the short heartbeat deadline for startup/wait phases, then use the separate
            # rollout-progress deadline below for active sessions.
            requires_ready_heartbeat = (
                state == "ready"
                and component == "elastic-wan-actor"
                and phase != "running"
            )
            if (
                state == "starting" or requires_ready_heartbeat
            ) and age >= CHILD_HEALTH_STALE_SECONDS:
                phase_name = "startup" if state == "starting" else phase or state
                return (
                    f"managed child {phase_name} health has not refreshed for "
                    f"{age:.1f} seconds"
                )

        phase_started = details_map.get("phase_started_unix_seconds")
        if state == "starting" and isinstance(
            phase_started, (int, float)
        ) and not isinstance(phase_started, bool):
            phase_age = max(0.0, now - float(phase_started))
            if phase_age >= CHILD_HEALTH_STARTUP_PHASE_TIMEOUT_SECONDS:
                return (
                    "managed child startup phase "
                    f"{phase or '(unknown)'} made no phase progress for "
                    f"{phase_age:.1f} seconds"
                )

        if (
            state == "ready"
            and component == "elastic-wan-actor"
            and phase == "running"
        ):
            progress = details_map.get("progress_unix_seconds")
            if not isinstance(progress, (int, float)) or isinstance(progress, bool):
                return "managed child running health has no rollout progress timestamp"
            progress_age = max(0.0, now - float(progress))
            if progress_age >= CHILD_HEALTH_PROGRESS_STALE_SECONDS:
                return (
                    "managed child rollout has made no progress for "
                    f"{progress_age:.1f} seconds"
                )
        return ""

    def state(self, role: str, offline: bool = False) -> str:
        if not self.alive():
            return "stopped"
        if role == "full-game" and offline:
            return "inference-offline"
        if self.health_required:
            health = self.health()
            if health is None or health.get("state") != "ready":
                return "starting"
        return "running"

    def start(
        self,
        command: Sequence[str],
        *,
        revision: int,
        build_sha256: str,
        build_id: str,
        run_id: str,
        compatibility_key: str,
        state_file: Path,
        environment_args: Sequence[str],
        worker_env_count: Optional[int] = None,
        graceful_checkpoint: bool = False,
        graceful_remote_stop: bool = False,
        require_child_health: bool = False,
        stop_progress: Optional[Callable[[], None]] = None,
    ) -> None:
        restart_delay = self.restart_delay(command)
        if restart_delay > 0.0:
            previous = (
                f"; previous exit code {self.last_exit_code}"
                if self.last_exit_code is not None
                else ""
            )
            if self.last_exit_error:
                previous += f"; child error: {self.last_exit_error}"
            raise RuntimeError(
                "managed process restart deferred for "
                f"{restart_delay:.1f}s after repeated early exits{previous}"
            )
        command_changed = tuple(command) != self.command
        self.stop(progress_callback=stop_progress)
        if command_changed:
            self.clear_restart_backoff()
        environment = os.environ.copy()
        environment["BEES_TRAINING_CONTROL_STATE_FILE"] = str(state_file)
        environment["BEES_TRAINING_ENV_ARGS_JSON"] = json.dumps(list(environment_args))
        build_id = str(build_id).strip()
        compatibility_key = str(compatibility_key).strip().lower()
        if not build_id:
            raise ValueError("managed training process requires a non-empty build_id")
        if len(compatibility_key) != 64 or any(
            ch not in "0123456789abcdef" for ch in compatibility_key
        ):
            raise ValueError("managed training process requires a 64-hex compatibility_key")
        environment["BEES_TRAINING_RUN_ID"] = str(run_id)
        environment[BUILD_ID_ENV] = build_id
        environment[COMPATIBILITY_KEY_ENV] = compatibility_key
        environment[ENVIRONMENT_ID_ENV] = environment_args_identity(environment_args)
        environment["PYTHONUNBUFFERED"] = "1"
        health_file = state_file.parent / "child-health.json"
        health_token = ""
        if require_child_health:
            health_token = configure_child_health(environment, health_file)
        else:
            environment.pop("BEES_TRAINING_CHILD_HEALTH_FILE", None)
            environment.pop("BEES_TRAINING_CHILD_HEALTH_TOKEN", None)
        throughput_metrics_file = state_file.parent / "worker-throughput.json"
        try:
            throughput_metrics_file.unlink()
        except FileNotFoundError:
            pass
        environment[THROUGHPUT_METRICS_ENV] = str(throughput_metrics_file)
        worker_env_target_file: Optional[Path] = None
        if worker_env_count is not None:
            worker_env_target_file = state_file.parent / "worker-envs.target"
            atomic_write_text(
                worker_env_target_file,
                f"{int(worker_env_count)}\n",
                encoding="ascii",
            )
            environment[WORKER_ENVS_TARGET_ENV] = str(worker_env_target_file)
        else:
            environment.pop(WORKER_ENVS_TARGET_ENV, None)
        if not run_id:
            raise ValueError("managed training process requires a non-empty run_id")
        logs_root = state_file.parent / "logs"
        log_dir = logs_root / run_id
        log_dir.mkdir(parents=True, exist_ok=True)
        _prune_run_log_directories(logs_root, str(run_id))
        environment["BEES_TRAINING_LOG_DIR"] = str(log_dir)
        environment["BEES_TRAINING_MODEL_SNAPSHOT_REQUEST_FILE"] = str(
            state_file.parent / "model-snapshot.request"
        )
        environment["BEES_TRAINING_MODEL_SNAPSHOT_RESPONSE_FILE"] = str(
            state_file.parent / "model-snapshot.response.json"
        )
        stop_request_file = state_file.parent / "managed-stop.request"
        try:
            stop_request_file.unlink()
        except FileNotFoundError:
            pass
        if graceful_checkpoint or graceful_remote_stop:
            environment[MANAGED_STOP_FILE_ENV] = str(stop_request_file)
        else:
            environment.pop(MANAGED_STOP_FILE_ENV, None)
        self.process = popen_owned(
            list(command),
            env=environment,
            start_new_session=(not _is_windows()),
        )
        self.command = tuple(command)
        self.revision = revision
        self.build_sha256 = build_sha256
        self.build_id = build_id
        self.run_id = str(run_id)
        self.compatibility_key = compatibility_key
        self.environment_args = tuple(str(value) for value in environment_args)
        self.worker_env_count = worker_env_count
        self.throughput_metrics_file = throughput_metrics_file
        self.worker_env_target_file = worker_env_target_file
        self.graceful_checkpoint = bool(graceful_checkpoint)
        self.graceful_remote_stop = bool(graceful_remote_stop)
        self.stop_request_file = (
            stop_request_file if graceful_checkpoint or graceful_remote_stop else None
        )
        self.health_file = health_file if require_child_health else None
        self.health_token = health_token
        self.health_required = bool(require_child_health)
        self.started_monotonic = time.monotonic()

    def stop(self, progress_callback: Optional[Callable[[], None]] = None) -> None:
        process = self.process
        if process is None:
            return
        if process.poll() is not None:
            self.process = None
            return

        # The central learner owns optimizer/checkpoint state. Ask its continual-service child
        # to interrupt ML-Agents cleanly so TrainerController saves checkpoint/ONNX and learn.py
        # writes timers/status. Planned central shutdown is fail-closed: never force-kill unsaved
        # optimizer state merely because finalization is slow.
        if self.graceful_checkpoint and self.stop_request_file is not None:
            try:
                self.stop_request_file.parent.mkdir(parents=True, exist_ok=True)
                self.stop_request_file.write_text("stop\n", encoding="ascii")
            except OSError as exc:
                raise RuntimeError(
                    f"could not request graceful learner checkpoint shutdown: {exc}"
                ) from exc

            deadline = time.monotonic() + GRACEFUL_CHECKPOINT_STOP_SECONDS
            next_progress = 0.0
            while process.poll() is None and time.monotonic() < deadline:
                now = time.monotonic()
                if progress_callback is not None and now >= next_progress:
                    try:
                        progress_callback()
                    except Exception:
                        pass
                    next_progress = now + 2.0
                time.sleep(0.25)
            if process.poll() is not None:
                self.process = None
                try:
                    self.stop_request_file.unlink()
                except FileNotFoundError:
                    pass
                return
            raise RuntimeError(
                "central learner checkpoint finalization is still running; "
                "refusing forced termination to preserve optimizer progress"
            )

        # Remote WAN actors can tear down their own ML-Agents manager and Unity workers cleanly.
        # Give them a bounded chance to do so before retaining the existing force-kill fallback.
        if self.graceful_remote_stop and self.stop_request_file is not None:
            try:
                self.stop_request_file.parent.mkdir(parents=True, exist_ok=True)
                self.stop_request_file.write_text("stop\n", encoding="ascii")
            except OSError:
                pass
            else:
                deadline = time.monotonic() + GRACEFUL_REMOTE_STOP_SECONDS
                next_progress = 0.0
                while process.poll() is None and time.monotonic() < deadline:
                    now = time.monotonic()
                    if progress_callback is not None and now >= next_progress:
                        try:
                            progress_callback()
                        except Exception:
                            pass
                        next_progress = now + 2.0
                    time.sleep(0.25)
                if process.poll() is not None:
                    self.process = None
                    try:
                        self.stop_request_file.unlink()
                    except FileNotFoundError:
                        pass
                    return

        # Non-checkpoint-owning workers may be force-stopped, but never report them stopped
        # until wait() has confirmed that the owned process actually exited.
        if _is_windows():
            try:
                subprocess.run(
                    ["taskkill", "/PID", str(process.pid), "/T", "/F"],
                    stdout=subprocess.DEVNULL,
                    stderr=subprocess.DEVNULL,
                    check=False,
                )
            except Exception:
                pass
            try:
                process.wait(timeout=15)
                self.process = None
                return
            except Exception:
                pass
        else:
            try:
                os.killpg(process.pid, _POSIX_SIGTERM)
            except Exception:
                pass
            try:
                process.wait(timeout=15)
                self.process = None
                return
            except Exception:
                pass

            try:
                os.killpg(process.pid, _POSIX_SIGKILL)
            except Exception:
                pass
            try:
                process.wait(timeout=5)
                self.process = None
                return
            except Exception:
                pass

        try:
            process.kill()
        except Exception:
            pass
        try:
            process.wait(timeout=5)
            self.process = None
            return
        except Exception as exc:
            raise RuntimeError(
                f"managed process {process.pid} did not stop after forced termination attempts"
            ) from exc


def dedicated_process_matches_desired(
    managed: ManagedProcess,
    *,
    mode: str,
    descriptor: Optional[Mapping[str, Any]],
    run_id: str,
    compatibility_key: str,
    environment_args: Sequence[str],
    worker_env_count: Optional[int],
    allow_live_worker_env_resize: bool = False,
) -> bool:
    """Return whether the live dedicated process is still safe under the latest desired state.

    This is intentionally stricter than "the process is alive" and looser than command-text
    identity. Release runtimes are immutable per build, so build/run/compatibility/config identity
    is the durable safety boundary. A transient ancillary reconciliation failure may leave an exact
    desired process running; a stale/mismatched process must fail closed.
    """
    if mode != "training" or not isinstance(descriptor, Mapping) or not managed.alive():
        return False
    desired_build_id = str(descriptor.get("build_id", ""))
    desired_sha256 = str(descriptor.get("archive_sha256", ""))
    return (
        bool(desired_build_id)
        and bool(desired_sha256)
        and managed.build_id == desired_build_id
        and managed.build_sha256 == desired_sha256
        and managed.run_id == str(run_id)
        and managed.compatibility_key == str(compatibility_key).strip().lower()
        and managed.environment_args == tuple(str(value) for value in environment_args)
        and (
            allow_live_worker_env_resize
            or managed.worker_env_count == worker_env_count
        )
        and not managed.health_error()
    )


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
    atomic_write_text(
        path,
        json.dumps(value, indent=2, sort_keys=True) + "\n",
        encoding="utf-8",
    )


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
    parser.add_argument("--runtime-cutover-pointer", default="")
    parser.add_argument("--runtime-state-file", default="")
    parser.add_argument(
        "--runtime-cutover-entrypoint",
        choices=(
            "bees_continual_elastic_wan_service.py",
            "bees_elastic_wan_actor_worker.py",
        ),
        default="bees_continual_elastic_wan_service.py",
        help=argparse.SUPPRESS,
    )
    parser.add_argument("--heartbeat-seconds", type=float, default=5.0)
    parser.add_argument("--request-timeout-seconds", type=float, default=5.0)
    parser.add_argument("--shutdown-request-file", default="")
    parser.add_argument(
        "--owner-token",
        default="",
        help=argparse.SUPPRESS,
    )
    parser.add_argument("--worker-envs", type=int, default=None)
    parser.add_argument("--worker-envs-min", type=int, default=1)
    parser.add_argument("--worker-envs-max", type=int, default=64)
    parser.add_argument("--auto-worker-envs", action="store_true")
    parser.add_argument(
        "launch_command",
        nargs=argparse.REMAINDER,
        help=(
            "Command after '--'. Use {env} for the canonical executable, {env_args} where "
            "server-owned environment arguments should be expanded, and {worker_envs} for "
            "server-tuned remote environment count."
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


def _load_runtime_cutover_pointer(
    path_value: str,
    expected_entrypoint: str = "bees_continual_elastic_wan_service.py",
) -> Optional[dict[str, Any]]:
    if not str(path_value).strip():
        return None
    path = Path(path_value).expanduser().resolve()
    if not path.is_file():
        return None
    value = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(value, Mapping) or value.get("schema_version") != 1:
        raise ValueError("runtime cutover pointer schema is invalid")

    build_id = str(value.get("build_id", "")).strip()
    runtime_version = str(value.get("runtime_version", "")).strip().lower()
    runtime_root = Path(str(value.get("runtime_root", ""))).expanduser().resolve()
    python_executable = Path(str(value.get("python_executable", ""))).expanduser().resolve()
    launch_command = value.get("launch_command")
    if not build_id:
        raise ValueError("runtime cutover pointer has no build_id")
    if len(runtime_version) != 64 or any(
        ch not in "0123456789abcdef" for ch in runtime_version
    ):
        raise ValueError("runtime cutover pointer has invalid runtime_version")
    if not runtime_root.is_dir():
        raise ValueError(f"runtime cutover root does not exist: {runtime_root}")
    marker = runtime_root / "bees-runtime-version.txt"
    if (
        not marker.is_file()
        or marker.read_text(encoding="ascii").strip().lower() != runtime_version
    ):
        raise ValueError("runtime cutover root version marker does not match pointer")
    if not python_executable.is_file():
        raise ValueError(
            f"runtime cutover Python executable is missing: {python_executable}"
        )
    if (
        not isinstance(launch_command, list)
        or not launch_command
        or not all(isinstance(value, str) and value for value in launch_command)
    ):
        raise ValueError("runtime cutover pointer has invalid launch_command")
    if str(Path(launch_command[0]).expanduser().resolve()) != str(python_executable):
        raise ValueError(
            "runtime cutover launch_command does not use the pinned Python executable"
        )
    service_path = Path(launch_command[1]).expanduser().resolve() if len(launch_command) > 1 else None
    if (
        service_path is None
        or not service_path.is_file()
        or service_path.parent != runtime_root
        or service_path.name != expected_entrypoint
    ):
        raise ValueError(
            "runtime cutover launch_command does not use the expected pinned entrypoint "
            + expected_entrypoint
        )
    if not any(ENV_PLACEHOLDER in token for token in launch_command):
        raise ValueError(
            f"runtime cutover launch_command must contain {ENV_PLACEHOLDER}"
        )
    if (
        expected_entrypoint == "bees_elastic_wan_actor_worker.py"
        and not any(WORKER_ENVS_PLACEHOLDER in token for token in launch_command)
    ):
        raise ValueError(
            f"actor runtime cutover launch_command must contain {WORKER_ENVS_PLACEHOLDER}"
        )
    return {
        "build_id": build_id,
        "runtime_version": runtime_version,
        "runtime_root": str(runtime_root),
        "python_executable": str(python_executable),
        "launch_command": list(launch_command),
    }


def _runtime_version_for_heartbeat(
    pointer_path: str,
    active_build_id: str,
    expected_entrypoint: str,
) -> str:
    """Expose the runtime selected for the active build before child telemetry exists."""
    if not str(pointer_path).strip() or not str(active_build_id).strip():
        return ""
    try:
        pointer = _load_runtime_cutover_pointer(pointer_path, expected_entrypoint)
    except (OSError, ValueError, json.JSONDecodeError):
        return ""
    if pointer is None or pointer["build_id"] != str(active_build_id).strip():
        return ""
    return str(pointer["runtime_version"])


def _runtime_launch_template(
    pointer_path: str,
    build_id: str,
    fallback: Sequence[str],
    cache: Optional[dict[str, list[str]]] = None,
    expected_entrypoint: str = "bees_continual_elastic_wan_service.py",
) -> list[str]:
    target_build = str(build_id)
    pointer = _load_runtime_cutover_pointer(pointer_path, expected_entrypoint)
    if pointer is not None:
        pointer_command = list(pointer["launch_command"])
        if cache is not None:
            cache[pointer["build_id"]] = pointer_command
        if pointer["build_id"] == target_build:
            return pointer_command

    if cache is not None and target_build in cache:
        return list(cache[target_build])

    fallback_command = list(fallback)
    if cache is not None and target_build:
        # The stable supervisor is launched while its fallback release is canonical. Remember
        # that association so replacing the pointer with a pending release cannot make the current
        # learner fall back to some later/older runtime while the rollout barrier is still preparing.
        cache[target_build] = fallback_command
    return fallback_command


def _write_runtime_state(
    path_value: str,
    *,
    build_id: str,
    command: Sequence[str],
) -> None:
    if not str(path_value).strip() or len(command) < 2:
        return
    python_executable = Path(command[0]).expanduser().resolve()
    service_path = Path(command[1]).expanduser().resolve()
    runtime_root = service_path.parent
    version = ""
    marker = runtime_root / "bees-runtime-version.txt"
    try:
        version = marker.read_text(encoding="ascii").strip().lower()
    except OSError:
        version = ""
    value = {
        "schema_version": 1,
        "build_id": str(build_id),
        "python_executable": str(python_executable),
        "runtime_root": str(runtime_root),
        "runtime_version": version,
        "updated_unix_seconds": time.time(),
    }
    path = Path(path_value).expanduser().resolve()
    atomic_write_text(
        path,
        json.dumps(value, indent=2, sort_keys=True) + "\n",
        encoding="utf-8",
    )


def main(argv: Optional[Sequence[str]] = None) -> int:
    raw_argv = list(sys.argv[1:] if argv is None else argv)
    args = _parser().parse_args(raw_argv)
    if args.heartbeat_seconds <= 0 or args.request_timeout_seconds <= 0:
        print("error: heartbeat and request timeout must be positive", file=sys.stderr)
        return 2
    if args.worker_envs is not None:
        if (
            not 1 <= args.worker_envs_min <= args.worker_envs <= args.worker_envs_max <= 64
        ):
            print("error: worker env bounds must satisfy 1 <= min <= current <= max <= 64", file=sys.stderr)
            return 2
    elif args.auto_worker_envs:
        print("error: --auto-worker-envs requires --worker-envs", file=sys.stderr)
        return 2
    try:
        command_template = _normalized_launch_command(args.launch_command)
        if not any(ENV_PLACEHOLDER in token for token in command_template):
            raise ValueError(f"launch command must contain {ENV_PLACEHOLDER}")
        if args.worker_envs is not None and not any(
            WORKER_ENVS_PLACEHOLDER in token for token in command_template
        ):
            raise ValueError(
                f"worker-managed launch command must contain {WORKER_ENVS_PLACEHOLDER}"
            )
        token = load_token(args.token_file)
        runtime_launch_commands: dict[str, list[str]] = {}
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
    episode_metrics = EpisodeLogMetrics(install_root / "logs")
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
    control_failures_total = 0
    last_control_failure_monotonic: Optional[float] = None
    last_control_failure_type = ""

    reconciliation_phase = ""
    reconciliation_started_monotonic: Optional[float] = None

    def set_reconciliation_phase(phase: str) -> None:
        nonlocal reconciliation_phase, reconciliation_started_monotonic
        normalized = str(phase or "").strip()
        if normalized == reconciliation_phase:
            return
        reconciliation_phase = normalized
        reconciliation_started_monotonic = time.monotonic() if normalized else None

    def metrics_run_id() -> str:
        return effective_metrics_run_id(desired, managed.run_id)

    def worker_capacity() -> dict[str, object]:
        if args.worker_envs is None:
            return {}
        current = managed.worker_env_count if managed.worker_env_count is not None else args.worker_envs
        if args.auto_worker_envs and managed.alive():
            throughput = read_throughput_metrics(
                managed.throughput_metrics_file,
                expected_pid=managed.throughput_expected_pid(),
            )
            reported_envs = throughput.get("env_count") if throughput else None
            if isinstance(reported_envs, int) and not isinstance(reported_envs, bool):
                current = reported_envs
        return {
            "auto": bool(args.auto_worker_envs),
            "current_envs": int(current),
            "min_envs": int(args.worker_envs_min),
            "max_envs": int(args.worker_envs_max),
        }

    def current_metrics(run_id: str) -> dict[str, object]:
        snapshot = episode_metrics.refresh(run_id)
        process = managed.process
        if managed.alive() and process is not None:
            throughput = read_throughput_metrics(
                managed.throughput_metrics_file,
                expected_pid=managed.throughput_expected_pid(),
                expected_env_count=(
                    None if args.auto_worker_envs else managed.worker_env_count
                ),
            )
            if throughput:
                snapshot["throughput"] = throughput

        # Optimizer state must see a runtime cutover before it can issue a stale env-count
        # decision to the newly launched worker. Actor throughput telemetry appears only after
        # the child starts, so expose the active build's pinned runtime identity immediately.
        active_build_id = (
            str(active_build.get("build_id", "")).strip()
            if isinstance(active_build, Mapping)
            else ""
        )
        heartbeat_runtime_version = _runtime_version_for_heartbeat(
            args.runtime_cutover_pointer,
            active_build_id,
            args.runtime_cutover_entrypoint,
        )
        if heartbeat_runtime_version:
            existing_throughput = snapshot.get("throughput")
            merged_throughput = (
                dict(existing_throughput)
                if isinstance(existing_throughput, Mapping)
                else {}
            )
            merged_throughput.setdefault(
                "runtime_version",
                heartbeat_runtime_version,
            )
            snapshot["throughput"] = merged_throughput

        _add_persisted_network_traffic(
            snapshot,
            managed.throughput_metrics_file,
            run_id=run_id,
            install_root=install_root,
        )
        if control_failures_total > 0 and last_control_failure_monotonic is not None:
            snapshot["control"] = {
                "failures_total": control_failures_total,
                "seconds_since_last_failure": max(
                    0.0,
                    time.monotonic() - last_control_failure_monotonic,
                ),
                "last_failure_type": last_control_failure_type,
            }
        if reconciliation_phase and reconciliation_started_monotonic is not None:
            snapshot["reconciliation"] = {
                "phase": reconciliation_phase,
                "seconds_in_phase": max(
                    0.0,
                    time.monotonic() - reconciliation_started_monotonic,
                ),
            }
        return snapshot

    def request_stop(_signum: int, _frame: object) -> None:
        nonlocal stop
        stop = True

    def stopping_keepalive() -> None:
        nonlocal last_contact
        if args.role != "dedicated":
            return
        heartbeat = default_heartbeat(
            trainer_id=args.trainer_id,
            role=args.role,
            platform=args.platform,
            process_state="stopping",
            applied_revision=applied_revision,
            build=active_build,
            prepared_build_id="",
            last_error=last_error,
            metrics=current_metrics(metrics_run_id()),
            environment_id=(
                environment_args_identity(managed.environment_args)
                if managed.alive()
                else ""
            ),
            worker_capacity=worker_capacity(),
        )
        try:
            client.heartbeat(heartbeat)
            last_contact = time.monotonic()
        except (ControlUnavailable, ControlRejected, OSError, ValueError):
            pass

    def blocking_keepalive() -> None:
        nonlocal last_contact
        if args.role != "dedicated":
            return
        heartbeat = default_heartbeat(
            trainer_id=args.trainer_id,
            role=args.role,
            platform=args.platform,
            process_state=managed.state(args.role, offline=False),
            applied_revision=applied_revision,
            build=active_build,
            prepared_build_id="",
            last_error=last_error,
            metrics=current_metrics(metrics_run_id()),
            environment_id=(
                environment_args_identity(managed.environment_args)
                if managed.alive()
                else ""
            ),
            worker_capacity=worker_capacity(),
        )
        try:
            client.heartbeat(heartbeat)
            last_contact = time.monotonic()
        except (ControlUnavailable, ControlRejected, OSError, ValueError):
            pass

    old_sigint = signal.signal(signal.SIGINT, request_stop)
    old_sigterm = signal.signal(signal.SIGTERM, request_stop)
    try:
        while not stop:
            if shutdown_request_file is not None and shutdown_request_file.is_file():
                print(
                    "[Bees control] supervisor shutdown requested; finalizing managed learner.",
                    flush=True,
                )
                try:
                    managed.stop(progress_callback=stopping_keepalive)
                except RuntimeError as exc:
                    last_error = f"{type(exc).__name__}: {exc}"
                    print(f"[Bees control] {last_error}", file=sys.stderr)
                    continue
                break
            received_desired = False
            now = time.monotonic()
            offline = last_contact > 0 and now - last_contact > lease_seconds
            prepared_build_id, preparation_error = preparer.snapshot()
            if prepared_build_id and args.runtime_ready_file:
                artifact_prepared_build_id = prepared_build_id
                try:
                    runtime_ready_build = Path(args.runtime_ready_file).expanduser().read_text(
                        encoding="ascii"
                    ).strip()
                except OSError:
                    runtime_ready_build = ""
                if runtime_ready_build != artifact_prepared_build_id:
                    if not preparation_error:
                        preparation_error = (
                            "Unity artifact is prepared for "
                            f"{artifact_prepared_build_id}, but the Python runtime "
                            f"is ready for {runtime_ready_build or '(none)'}"
                        )
                    prepared_build_id = ""
                elif args.runtime_cutover_pointer:
                    try:
                        runtime_pointer = _load_runtime_cutover_pointer(
                            args.runtime_cutover_pointer,
                            args.runtime_cutover_entrypoint,
                        )
                        pointer_build = (
                            runtime_pointer["build_id"] if runtime_pointer else ""
                        )
                        if pointer_build != artifact_prepared_build_id:
                            raise ValueError(
                                "runtime cutover pointer is prepared for "
                                f"{pointer_build or '(none)'} instead of "
                                f"{artifact_prepared_build_id}"
                            )
                    except (OSError, ValueError, json.JSONDecodeError) as exc:
                        if not preparation_error:
                            preparation_error = (
                                "Unity artifact is prepared for "
                                f"{artifact_prepared_build_id}, but the managed Python runtime "
                                f"cutover is not ready: {type(exc).__name__}: {exc}"
                            )
                        prepared_build_id = ""
            heartbeat = default_heartbeat(
                trainer_id=args.trainer_id,
                role=args.role,
                platform=args.platform,
                process_state=managed.state(args.role, offline=offline),
                applied_revision=applied_revision,
                build=active_build,
                prepared_build_id=prepared_build_id,
                preparation_error=preparation_error,
                last_error=heartbeat_last_error(
                    last_error,
                    preparation_error,
                    managed.health_error(),
                ),
                environment_id=(
                    environment_args_identity(managed.environment_args)
                    if managed.alive()
                    else ""
                ),
                metrics=current_metrics(metrics_run_id()),
                worker_capacity=worker_capacity(),
            )
            desired_process_safe = False
            try:
                desired = heartbeat_with_transport_retry(client, heartbeat)
                received_desired = True
                last_contact = time.monotonic()
                lease_seconds = float(desired["lease_seconds"])
                last_error = ""

                mode = str(desired["desired_mode"])
                revision = int(desired["revision"])
                run_id = str(desired.get("run_id", ""))
                compatibility_key = str(desired.get("compatibility_key", "")).strip().lower()
                environment_args = tuple(str(value) for value in desired["environment_args"])
                worker_env_count = args.worker_envs
                if args.auto_worker_envs:
                    requested_worker_envs = desired.get("worker_env_count")
                    if requested_worker_envs is not None:
                        if (
                            not isinstance(requested_worker_envs, int)
                            or isinstance(requested_worker_envs, bool)
                            or not args.worker_envs_min <= requested_worker_envs <= args.worker_envs_max
                        ):
                            raise RuntimeError(
                                "server requested worker env count outside advertised capacity"
                            )
                        worker_env_count = requested_worker_envs
                descriptor = desired.get("build")
                preparer.request(desired.get("prepare_build"))
                if args.role == "dedicated":
                    desired_process_safe = dedicated_process_matches_desired(
                        managed,
                        mode=mode,
                        descriptor=descriptor,
                        run_id=run_id,
                        compatibility_key=compatibility_key,
                        environment_args=environment_args,
                        worker_env_count=worker_env_count,
                        allow_live_worker_env_resize=bool(args.auto_worker_envs),
                    )

                if mode == "stopped":
                    set_reconciliation_phase("")
                    managed.stop(progress_callback=stopping_keepalive)
                    pending_release = desired.get("pending_release")
                    if (
                        isinstance(pending_release, Mapping)
                        and bool(pending_release.get("incompatible"))
                    ):
                        log_uploader.flush_all(
                            client,
                            trainer_id=args.trainer_id,
                            run_id=run_id,
                            progress_callback=blocking_keepalive,
                        )
                    applied_revision = revision
                elif mode == "inference" and args.role == "full-game":
                    # Stop/inference changes apply live through control-state.json. Never kill an
                    # active player session merely to install a newer build or command-line config.
                    # If the game is already gone, prepare the canonical next launch immediately.
                    if descriptor and not managed.alive():
                        set_reconciliation_phase("ensuring canonical build")
                        entrypoint, active_build = builds.ensure(
                            client,
                            descriptor,
                            progress_callback=blocking_keepalive,
                        )
                        desired_sha = str(active_build["archive_sha256"])
                        set_reconciliation_phase("resolving managed runtime")
                        runtime_command_template = _runtime_launch_template(
                            args.runtime_cutover_pointer,
                            str(active_build["build_id"]),
                            command_template,
                            runtime_launch_commands,
                            args.runtime_cutover_entrypoint,
                        )
                        command = render_command(
                            runtime_command_template,
                            entrypoint,
                            environment_args,
                            str(active_build["build_id"]),
                            run_id,
                            worker_env_count,
                        )
                        managed.start(
                            command,
                            revision=revision,
                            build_sha256=desired_sha,
                            build_id=str(active_build["build_id"]),
                            run_id=run_id,
                            compatibility_key=compatibility_key,
                            state_file=state_file,
                            environment_args=environment_args,
                            worker_env_count=worker_env_count,
                            graceful_checkpoint=(
                                args.role == "dedicated"
                                and args.trainer_id == "central-learner"
                            ),
                            require_child_health=(args.role == "dedicated"),
                            stop_progress=stopping_keepalive,
                        )
                        _write_runtime_state(
                            args.runtime_state_file,
                            build_id=str(active_build["build_id"]),
                            command=command,
                        )
                        applied_revision = revision
                        set_reconciliation_phase("")
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
                        set_reconciliation_phase("ensuring canonical build")
                        entrypoint, active_build = builds.ensure(
                            client,
                            descriptor,
                            progress_callback=blocking_keepalive,
                        )
                        desired_sha = str(active_build["archive_sha256"])
                        set_reconciliation_phase("resolving managed runtime")
                        runtime_command_template = _runtime_launch_template(
                            args.runtime_cutover_pointer,
                            str(active_build["build_id"]),
                            command_template,
                            runtime_launch_commands,
                            args.runtime_cutover_entrypoint,
                        )
                        launch_worker_env_count = worker_env_count
                        if (
                            args.auto_worker_envs
                            and managed.alive()
                            and managed.worker_env_count is not None
                        ):
                            launch_worker_env_count = managed.worker_env_count
                        command = render_command(
                            runtime_command_template,
                            entrypoint,
                            environment_args,
                            str(active_build["build_id"]),
                            run_id,
                            launch_worker_env_count,
                        )
                        if args.auto_worker_envs and managed.alive():
                            managed.set_worker_env_target(worker_env_count)
                        needs_restart = (
                            not managed.alive()
                            or managed.build_sha256 != desired_sha
                            or managed.build_id != str(active_build["build_id"])
                            or managed.run_id != run_id
                            or managed.compatibility_key != compatibility_key
                            or managed.environment_args != environment_args
                            or managed.command != tuple(command)
                        )
                        if (
                            needs_restart
                            and args.auto_worker_envs
                            and launch_worker_env_count != worker_env_count
                        ):
                            # A real build/runtime/config restart should launch at the latest desired
                            # capacity. Using the old launch count here would make the next heartbeat
                            # see a command mismatch and restart the actor a second time.
                            command = render_command(
                                runtime_command_template,
                                entrypoint,
                                environment_args,
                                str(active_build["build_id"]),
                                run_id,
                                worker_env_count,
                            )
                        if needs_restart:
                            set_reconciliation_phase("launching managed actor")
                            managed.start(
                                command,
                                revision=revision,
                                build_sha256=desired_sha,
                                build_id=str(active_build["build_id"]),
                                run_id=run_id,
                                compatibility_key=compatibility_key,
                                state_file=state_file,
                                environment_args=environment_args,
                                worker_env_count=worker_env_count,
                                graceful_checkpoint=(
                                    args.role == "dedicated"
                                    and args.trainer_id == "central-learner"
                                ),
                                graceful_remote_stop=(
                                    args.role == "dedicated"
                                    and args.trainer_id != "central-learner"
                                ),
                                require_child_health=(args.role == "dedicated"),
                                stop_progress=stopping_keepalive,
                            )
                        if args.role == "dedicated":
                            desired_process_safe = dedicated_process_matches_desired(
                                managed,
                                mode=mode,
                                descriptor=active_build,
                                run_id=run_id,
                                compatibility_key=compatibility_key,
                                environment_args=environment_args,
                                worker_env_count=worker_env_count,
                                allow_live_worker_env_resize=bool(args.auto_worker_envs),
                            )
                        _write_runtime_state(
                            args.runtime_state_file,
                            build_id=str(active_build["build_id"]),
                            command=managed.command or command,
                        )
                        applied_revision = revision
                        set_reconciliation_phase("")
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
                set_reconciliation_phase("")
                error_text = f"{type(exc).__name__}: {exc}"
                if isinstance(exc, (ControlUnavailable, ControlRejected)):
                    control_failures_total += 1
                    last_control_failure_monotonic = time.monotonic()
                    last_control_failure_type = type(exc).__name__
                offline = last_contact <= 0 or time.monotonic() - last_contact > lease_seconds
                transient_control_error = isinstance(exc, ControlUnavailable) and not offline
                # Transport unavailability belongs in the dedicated control metrics above, not
                # in last_error. Echoing it as a worker-process error on the recovery heartbeat
                # makes BeesServer hold env optimization for 15 minutes after a connection that
                # has already healed.
                last_error = worker_health_error_after_exception(
                    last_error,
                    exc,
                    error_text,
                )
                try:
                    write_local_state(
                        state_file,
                        desired=desired,
                        online=bool(received_desired and not offline),
                        last_error=error_text,
                    )
                except OSError as state_exc:
                    print(
                        "[Bees control] could not persist local diagnostic state: "
                        f"{type(state_exc).__name__}: {state_exc}",
                        file=sys.stderr,
                    )

                if args.role == "dedicated":
                    if offline:
                        if managed.alive():
                            print(
                                "[Bees control] server lease expired; stopping dedicated trainer.",
                                file=sys.stderr,
                            )
                        managed.stop(progress_callback=stopping_keepalive)
                    elif received_desired and not desired_process_safe:
                        if managed.alive():
                            print(
                                "[Bees control] desired-state reconciliation failed and the "
                                "running trainer no longer exactly matches server intent; "
                                "stopping it until safe state can be applied.",
                                file=sys.stderr,
                            )
                        managed.stop(progress_callback=stopping_keepalive)
                    elif received_desired and desired_process_safe:
                        print(
                            "[Bees control] reconciliation error while the running trainer "
                            "still exactly matches server intent; keeping it running and retrying: "
                            + error_text,
                            file=sys.stderr,
                        )
                    elif transient_control_error and managed.alive():
                        print(
                            "[Bees control] transient control transport interruption within the "
                            "active lease; keeping the matching trainer running and retrying: "
                            + error_text,
                            file=sys.stderr,
                        )
                elif args.role == "full-game" and offline and managed.alive():
                    print(
                        "[Bees control] server lease expired; full game remains running in "
                        "inference/offline-recording mode.",
                        file=sys.stderr,
                    )

            deadline = time.monotonic() + heartbeat_retry_delay(
                received_desired,
                args.heartbeat_seconds,
            )
            while not stop and time.monotonic() < deadline:
                if shutdown_request_file is not None and shutdown_request_file.is_file():
                    stop = True
                    break
                if managed.process is not None and managed.process.poll() is not None:
                    code = managed.process.returncode
                    child_error = managed.terminal_health_error()
                    restart_delay = managed.record_exit(code, child_error)
                    detail = f"; child error: {child_error}" if child_error else ""
                    last_error = (
                        f"managed process exited with code {code}{detail}; "
                        f"retrying in {restart_delay:.0f}s unless desired launch changes"
                    )
                    break
                time.sleep(min(0.25, max(0.0, deadline - time.monotonic())))
        return 0
    finally:
        cleanup_error: Optional[RuntimeError] = None
        for attempt in range(1, 4):
            if not managed.alive():
                break
            try:
                managed.stop(progress_callback=stopping_keepalive)
                cleanup_error = None
                break
            except RuntimeError as exc:
                cleanup_error = exc
                print(
                    f"[Bees control] cleanup attempt {attempt}/3 failed: "
                    f"{type(exc).__name__}: {exc}",
                    file=sys.stderr,
                    flush=True,
                )
                if attempt < 3:
                    time.sleep(float(attempt))
        if managed.alive() and cleanup_error is not None:
            raise RuntimeError(
                "managed trainer could not be stopped after three cleanup attempts"
            ) from cleanup_error
        if shutdown_request_file is not None:
            try:
                shutdown_request_file.unlink()
            except FileNotFoundError:
                pass
        signal.signal(signal.SIGINT, old_sigint)
        signal.signal(signal.SIGTERM, old_sigterm)


if __name__ == "__main__":
    raise SystemExit(main())
