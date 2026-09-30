"""Run one elastic Bees WAN rollout actor.

Each remote machine chooses its own --envs count from 1 through 64. Managed workers present a
persistent machine key and the central learner assigns an available actor slot automatically; manual
--actor-id remains available only for debugging/nonstandard launches. The learner reserves 64
global worker IDs per slot, so changing one machine's environment count never renumbers another
actor. The actor talks to the WAN broker through the private tailnet forwarding owned by the managed
remote supervisor; no SSH tunnel is created here.
"""

from __future__ import annotations

import argparse
import json
import math
import os
import secrets
import signal
import sys
import threading
import time
import traceback
from pathlib import Path
from typing import Any, Mapping, Optional, Sequence

import bees_elastic_wan_actor_session as elastic_session
import bees_elastic_wan_training as elastic
import bees_wan_actor_training as wan
import bees_wan_actor_worker as worker
from bees_process_safety import atomic_write_text, write_managed_health


MANAGED_STOP_FILE_ENV = "BEES_TRAINING_STOP_FILE"


MAX_RECONNECT_BACKOFF_SECONDS = 30.0
HEALTHY_SESSION_RESET_SECONDS = 60.0
SESSION_FAILURE_STATE_FILE = "worker-session-failures.json"
RUNTIME_VERSION_FILE = "bees-runtime-version.txt"


def _runtime_version_identity() -> str:
    path = Path(__file__).resolve().with_name(RUNTIME_VERSION_FILE)
    try:
        value = path.read_text(encoding="ascii").strip().lower()
    except OSError:
        return ""
    if len(value) != 64 or any(ch not in "0123456789abcdef" for ch in value):
        return ""
    return value


class _ReconnectBackoff:
    def __init__(self, base_seconds: float, max_seconds: float = MAX_RECONNECT_BACKOFF_SECONDS):
        self.base_seconds = max(0.1, float(base_seconds))
        self.max_seconds = max(self.base_seconds, float(max_seconds))
        self._next_seconds = self.base_seconds

    def reset(self) -> None:
        self._next_seconds = self.base_seconds

    def next_delay(self) -> float:
        delay = self._next_seconds
        self._next_seconds = min(self.max_seconds, max(self.base_seconds, delay * 2.0))
        return delay


class _StartupHealthHeartbeat:
    def __init__(self, *, actor_id: int, env_count: int, interval_seconds: float = 5.0) -> None:
        self.actor_id = int(actor_id)
        self.env_count = int(env_count)
        self.interval_seconds = max(1.0, float(interval_seconds))
        self._phase = "starting-session"
        self._state = "starting"
        self._error = ""
        self._phase_started_unix_seconds = time.time()
        self._last_progress_unix_seconds: Optional[float] = None
        self._lock = threading.Lock()
        # Environment stepping can call mark_progress hundreds of times per second. Keep that hot
        # path off the publication lock so the 5-second health writer cannot be starved by rollout.
        self._progress_pending = threading.Event()
        self._stop = threading.Event()
        self._thread = threading.Thread(
            target=self._run,
            name="bees-wan-startup-health",
            daemon=True,
        )

    def _publish(self) -> None:
        with self._lock:
            if (
                self._state == "ready"
                and self._phase == "running"
                and self._progress_pending.is_set()
            ):
                self._last_progress_unix_seconds = time.time()
                self._progress_pending.clear()
            phase = self._phase
            state = self._state
            error = self._error
            actor_id = self.actor_id
            phase_started = self._phase_started_unix_seconds
            last_progress = self._last_progress_unix_seconds
        details = {
            "component": "elastic-wan-actor",
            "phase": phase,
            "phase_started_unix_seconds": phase_started,
            "env_count": self.env_count,
        }
        if last_progress is not None:
            details["progress_unix_seconds"] = last_progress
        if actor_id >= 0:
            details["actor_id"] = actor_id
        write_managed_health(state, error=error, details=details)

    def start(self) -> None:
        self._publish()
        self._thread.start()

    def set_phase(self, phase: str) -> None:
        now = time.time()
        phase = str(phase)
        with self._lock:
            self._state = "starting"
            if phase != self._phase:
                self._phase_started_unix_seconds = now
            self._phase = phase
            self._error = ""
            self._last_progress_unix_seconds = None
            self._progress_pending.clear()
        self._publish()

    def set_ready(self, phase: str, *, actor_id: Optional[int] = None) -> None:
        now = time.time()
        phase = str(phase)
        with self._lock:
            self._state = "ready"
            if phase != self._phase:
                self._phase_started_unix_seconds = now
            self._phase = phase
            self._error = ""
            if phase == "running":
                self._last_progress_unix_seconds = now
            else:
                self._last_progress_unix_seconds = None
            self._progress_pending.clear()
            if actor_id is not None:
                self.actor_id = int(actor_id)
        self._publish()

    def set_env_count(self, env_count: int) -> None:
        value = int(env_count)
        if not 1 <= value <= elastic.MAX_ENVS_PER_ACTOR:
            raise ValueError(
                f"startup health env_count must be in 1-{elastic.MAX_ENVS_PER_ACTOR}"
            )
        with self._lock:
            self.env_count = value
        self._publish()

    def mark_progress(self) -> None:
        # The rollout loop can advance many times per second. Signal progress without competing
        # with the health publisher; the publisher timestamps and persists it at bounded cadence.
        self._progress_pending.set()

    def set_error(self, exc: BaseException) -> None:
        with self._lock:
            self._state = "error"
            self._phase = "session-error"
            self._phase_started_unix_seconds = time.time()
            self._last_progress_unix_seconds = None
            self._progress_pending.clear()
            self._error = f"{type(exc).__name__}: {exc}"
        self._publish()

    def stop(self) -> None:
        self._stop.set()
        if self._thread.is_alive():
            self._thread.join(timeout=self.interval_seconds + 1.0)

    def _run(self) -> None:
        while not self._stop.wait(self.interval_seconds):
            self._publish()


class _SessionFailureTelemetry:
    def __init__(
        self,
        *,
        state_path: Optional[Path] = None,
        run_id: str = "",
        runtime_version: str = "",
    ) -> None:
        self._lock = threading.Lock()
        self._state_path = state_path
        self._run_id = str(run_id or "")
        self._runtime_version = str(runtime_version or "").strip().lower()
        self._count = 0
        self._last_failure_unix_seconds: Optional[float] = None
        self._last_failure_type = ""
        self._last_failure_message = ""
        loaded = self._load()
        if self._state_path is not None and self._run_id and not loaded:
            self._persist()

    def _load(self) -> bool:
        path = self._state_path
        if path is None or not path.is_file():
            return False
        try:
            value = json.loads(path.read_text(encoding="utf-8"))
        except (OSError, UnicodeDecodeError, json.JSONDecodeError):
            return False
        if not isinstance(value, Mapping):
            return False
        if (
            value.get("schema_version") != 2
            or str(value.get("run_id") or "") != self._run_id
            or str(value.get("runtime_version") or "").strip().lower()
            != self._runtime_version
        ):
            return False
        count = value.get("session_failures_total")
        last_failure = value.get("last_failure_unix_seconds")
        failure_type = value.get("last_session_failure_type")
        failure_message = value.get("last_session_failure_message", "")
        if (
            not isinstance(count, int)
            or isinstance(count, bool)
            or count < 0
            or (
                last_failure is not None
                and (
                    not isinstance(last_failure, (int, float))
                    or isinstance(last_failure, bool)
                    or not math.isfinite(float(last_failure))
                    or float(last_failure) < 0.0
                )
            )
            or not isinstance(failure_type, str)
            or not isinstance(failure_message, str)
        ):
            return False
        self._count = count
        self._last_failure_unix_seconds = (
            None if last_failure is None else float(last_failure)
        )
        self._last_failure_type = failure_type
        self._last_failure_message = failure_message
        return True

    def _persist(self) -> None:
        path = self._state_path
        if path is None or not self._run_id:
            return
        payload = {
            "schema_version": 2,
            "run_id": self._run_id,
            "runtime_version": self._runtime_version,
            "session_failures_total": self._count,
            "last_failure_unix_seconds": self._last_failure_unix_seconds,
            "last_session_failure_type": self._last_failure_type,
            "last_session_failure_message": self._last_failure_message,
        }
        try:
            atomic_write_text(
                path,
                json.dumps(payload, sort_keys=True) + "\n",
                encoding="utf-8",
            )
        except OSError:
            # Failure history is diagnostic. Never stop rollout work because persistence failed.
            return

    def record(self, exc: BaseException) -> None:
        with self._lock:
            self._count += 1
            self._last_failure_unix_seconds = time.time()
            self._last_failure_type = type(exc).__name__
            self._last_failure_message = " ".join(str(exc).split())[:240]
            self._persist()

    def snapshot(self) -> dict[str, Any]:
        with self._lock:
            age = (
                None
                if self._last_failure_unix_seconds is None
                else max(0.0, time.time() - self._last_failure_unix_seconds)
            )
            return {
                "runtime_version": self._runtime_version,
                "session_failures_total": self._count,
                "seconds_since_last_session_failure": age,
                "last_session_failure_type": self._last_failure_type,
                "last_session_failure_message": self._last_failure_message,
            }


def _watch_managed_stop_request(
    path: Path,
    stop: threading.Event,
    *,
    poll_seconds: float = 0.25,
) -> None:
    while not stop.is_set():
        if path.is_file():
            stop.set()
            return
        stop.wait(poll_seconds)


def _safe_shape(value: Any) -> Optional[list[int]]:
    shape = getattr(value, "shape", None)
    if shape is None:
        return None
    try:
        return [int(item) for item in shape]
    except (TypeError, ValueError):
        return None


def _actor_failure_context(session: Any) -> dict[str, Any]:
    context: dict[str, Any] = {
        "actor_id": getattr(session, "actor_id", None),
        "env_count": getattr(session, "env_count", None),
        "worker_offset": getattr(session, "worker_offset", None),
        "total_envs": getattr(session, "total_envs", None),
        "policy_epoch": getattr(session, "policy_epoch", None),
        "control_epoch": getattr(session, "control_epoch", None),
        "topology_epoch": getattr(session, "topology_epoch", None),
        "policy_versions": dict(getattr(session, "policy_versions", {}) or {}),
    }
    manager = getattr(session, "manager", None)
    if manager is None:
        return context

    worker_states = []
    for index, env_worker in enumerate(getattr(manager, "env_workers", ()) or ()):
        item: dict[str, Any] = {
            "index": index,
            "waiting": bool(getattr(env_worker, "waiting", False)),
        }
        previous = getattr(env_worker, "previous_step", None)
        if previous is not None:
            item["worker_id"] = getattr(previous, "worker_id", None)
            results = getattr(previous, "current_all_step_result", None)
            if isinstance(results, Mapping):
                behaviors = {}
                for behavior, pair in results.items():
                    try:
                        decision, terminal = pair
                    except (TypeError, ValueError):
                        continue
                    behaviors[str(behavior)] = {
                        "decision_count": len(decision),
                        "terminal_count": len(terminal),
                        "decision_agent_ids_shape": _safe_shape(
                            getattr(decision, "agent_id", None)
                        ),
                        "terminal_agent_ids_shape": _safe_shape(
                            getattr(terminal, "agent_id", None)
                        ),
                    }
                item["behaviors"] = behaviors

            action_infos = getattr(previous, "brain_name_to_action_info", None)
            if isinstance(action_infos, Mapping):
                actions = {}
                for behavior, info in action_infos.items():
                    action = getattr(info, "action", None)
                    actions[str(behavior)] = {
                        "agent_ids_shape": _safe_shape(getattr(info, "agent_ids", None)),
                        "continuous_shape": _safe_shape(
                            getattr(action, "continuous", None)
                        ),
                        "discrete_shape": _safe_shape(
                            getattr(action, "discrete", None)
                        ),
                    }
                item["actions"] = actions
        worker_states.append(item)
    context["workers"] = worker_states
    return context


def _write_waiting_for_central_health(exc: BaseException) -> None:
    write_managed_health(
        "ready",
        details={
            "component": "elastic-wan-actor",
            "phase": "waiting-for-central",
            "last_broker_error": f"{type(exc).__name__}: {exc}",
        },
    )


def _report_session_failure(exc: BaseException, session: Any) -> None:
    print(
        f"[Bees WAN actor] session failed: {type(exc).__name__}: {exc}; reconnecting.",
        file=sys.stderr,
    )
    if session is not None:
        try:
            print(
                "[Bees WAN actor] failure context: "
                + json.dumps(_actor_failure_context(session), sort_keys=True),
                file=sys.stderr,
            )
        except Exception as context_exc:
            print(
                f"[Bees WAN actor] failure context unavailable: "
                f"{type(context_exc).__name__}: {context_exc}",
                file=sys.stderr,
            )
    traceback.print_exception(type(exc), exc, exc.__traceback__, file=sys.stderr)


class ElasticBrokerClient(worker.BrokerClient):
    def __init__(
        self,
        *args: Any,
        actor_id: Optional[int],
        actor_key: Optional[str],
        env_count: int,
        build_id: str,
        run_id: str,
        compatibility_key: str,
        environment_id: str,
        **kwargs: Any,
    ):
        super().__init__(*args, **kwargs)
        self.requested_actor_id = actor_id
        self.actor_id = actor_id
        self.actor_key = actor_key
        self.env_count = env_count
        self.release_identity = {
            "build_id": str(build_id),
            "run_id": str(run_id),
            "compatibility_key": str(compatibility_key).lower(),
            "environment_id": str(environment_id).lower(),
        }
        self.actor_instance_id = secrets.token_hex(16)

    def claim(self, session_id: str) -> int:
        if self.requested_actor_id is not None:
            self.actor_id = self.requested_actor_id
            return self.actor_id
        if not self.actor_key:
            raise RuntimeError("automatic actor allocation requires actor_key")
        status, _headers, body = self._request(
            "POST",
            "/claim",
            payload={
                "session_id": session_id,
                "actor_key": self.actor_key,
                "actor_instance_id": self.actor_instance_id,
                "env_count": self.env_count,
                **self.release_identity,
            },
        )
        if status != 200:
            raise RuntimeError(f"Unexpected WAN broker claim status {status}")
        value = json.loads(body.decode("utf-8"))
        actor_id = value.get("actor_id") if isinstance(value, Mapping) else None
        if not isinstance(actor_id, int) or isinstance(actor_id, bool) or actor_id < 0:
            raise RuntimeError("WAN broker returned an invalid actor slot")
        self.actor_id = actor_id
        return actor_id

    def _owned_payload(self, payload: Mapping[str, Any]) -> dict[str, Any]:
        if self.actor_id is None:
            raise RuntimeError("WAN actor has not claimed a central slot")
        enriched = dict(payload)
        enriched["actor_id"] = self.actor_id
        enriched["actor_instance_id"] = self.actor_instance_id
        enriched.update(self.release_identity)
        if self.actor_key:
            enriched["actor_key"] = self.actor_key
        return enriched

    def register(self, payload: Mapping[str, Any]) -> None:
        enriched = self._owned_payload(payload)
        enriched["env_count"] = self.env_count
        super().register(enriched)

    def trajectories(self, payload: Mapping[str, Any]) -> None:
        super().trajectories(self._owned_payload(payload))

    def reset_ack(self, payload: Mapping[str, Any]) -> None:
        super().reset_ack(self._owned_payload(payload))


def _parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Run a persistent elastic WAN Bees rollout actor with local policy inference."
    )
    parser.add_argument(
        "--actor-id",
        type=int,
        default=None,
        help="Optional fixed actor slot for manual/debug use. Managed workers omit this.",
    )
    parser.add_argument(
        "--actor-key",
        default=None,
        help="Persistent remote-machine identity used for automatic central slot allocation.",
    )
    parser.add_argument(
        "--envs",
        type=int,
        default=32,
        help="Local Unity environment count for this machine (1-64; default 32).",
    )
    parser.add_argument(
        "--broker-host",
        default="127.0.0.1",
        help="WAN broker host. Managed remotes use the local tailnet forward.",
    )
    parser.add_argument("--broker-port", type=int, default=wan.DEFAULT_BROKER_PORT)
    parser.add_argument("--env", required=True, help="Path to the matching Bees training build.")
    parser.add_argument("--auth-token-file", required=True)
    parser.add_argument("--local-base-port", type=int, default=5005)
    parser.add_argument("--torch-device", default="cpu")
    parser.add_argument("--graphics", action="store_true")
    parser.add_argument("--reconnect-seconds", type=float, default=worker.DEFAULT_RECONNECT_SECONDS)
    parser.add_argument("--upload-queue", type=int, default=worker.DEFAULT_LOCAL_UPLOAD_QUEUE)
    return parser


def _managed_release_identity() -> dict[str, str]:
    build_id = os.environ.get(elastic.BUILD_ID_ENV, "").strip()
    run_id = os.environ.get(elastic.RUN_ID_ENV, "").strip()
    compatibility_key = os.environ.get(elastic.COMPATIBILITY_KEY_ENV, "").strip().lower()
    environment_id = os.environ.get(elastic.ENVIRONMENT_ID_ENV, "").strip().lower()
    if not build_id or not run_id:
        raise RuntimeError("managed WAN actor is missing build/run identity")
    if len(compatibility_key) != 64 or any(
        ch not in "0123456789abcdef" for ch in compatibility_key
    ):
        raise RuntimeError("managed WAN actor is missing a valid compatibility identity")
    if len(environment_id) != 64 or any(
        ch not in "0123456789abcdef" for ch in environment_id
    ):
        raise RuntimeError("managed WAN actor is missing a valid environment identity")
    return {
        "build_id": build_id,
        "run_id": run_id,
        "compatibility_key": compatibility_key,
        "environment_id": environment_id,
    }


def _validate_session_release_identity(
    session: Mapping[str, Any],
    expected: Mapping[str, str],
) -> None:
    actual = session.get("release_identity")
    if not isinstance(actual, Mapping):
        raise RuntimeError("Elastic WAN session is missing release identity")
    run_id = str(actual.get("run_id", "")).strip()
    compatibility_key = str(actual.get("compatibility_key", "")).strip().lower()
    environment_id = str(actual.get("environment_id", "")).strip().lower()
    if (
        run_id != str(expected.get("run_id", "")).strip()
        or compatibility_key != str(expected.get("compatibility_key", "")).strip().lower()
        or environment_id != str(expected.get("environment_id", "")).strip().lower()
    ):
        raise RuntimeError(
            "Elastic WAN learner semantic release identity does not match this managed actor"
        )


def _elastic_session(
    session: Mapping[str, Any],
    *,
    actor_id: int,
    env_count: int,
) -> tuple[dict[str, Any], int, int]:
    max_actors = session.get("max_actors")
    max_envs = session.get("max_envs_per_actor")
    worker_base = session.get("remote_worker_base")
    stride = session.get("worker_stride")
    capacity_envs = session.get("capacity_envs")
    if not isinstance(max_actors, int) or not 1 <= max_actors <= elastic.MAX_BROKER_ACTORS:
        raise RuntimeError("Elastic WAN session has an invalid max_actors value")
    if max_envs != elastic.MAX_ENVS_PER_ACTOR or stride != elastic.MAX_ENVS_PER_ACTOR:
        raise RuntimeError("Elastic WAN worker-slot contract is incompatible with this actor helper")
    if not isinstance(worker_base, int) or worker_base < 0:
        raise RuntimeError("Elastic WAN session has an invalid remote_worker_base")
    if not isinstance(capacity_envs, int) or capacity_envs <= worker_base:
        raise RuntimeError("Elastic WAN session has an invalid capacity_envs value")
    if not 0 <= actor_id < max_actors:
        raise RuntimeError(f"actor id {actor_id} is outside central slot count {max_actors}")
    if not 1 <= env_count <= max_envs:
        raise RuntimeError(f"--envs must be between 1 and {max_envs}")

    compatible = dict(session)
    compatible["actor_count"] = max_actors
    compatible["envs_per_actor"] = env_count
    return compatible, worker_base + actor_id * stride, capacity_envs


def main(argv: Optional[Sequence[str]] = None) -> int:
    args = _parser().parse_args(argv)
    if args.actor_id is not None and args.actor_id < 0:
        print("error: --actor-id must be non-negative", file=sys.stderr)
        return 2
    if args.actor_id is None and not args.actor_key:
        print("error: --actor-key is required when --actor-id is omitted", file=sys.stderr)
        return 2
    if not 1 <= args.envs <= elastic.MAX_ENVS_PER_ACTOR:
        print(f"error: --envs must be in 1-{elastic.MAX_ENVS_PER_ACTOR}", file=sys.stderr)
        return 2
    if not 1 <= args.broker_port <= 65535:
        print("error: --broker-port must be in 1-65535", file=sys.stderr)
        return 2
    if not str(args.broker_host).strip():
        print("error: --broker-host is required", file=sys.stderr)
        return 2
    if not 1 <= args.local_base_port <= 65535:
        print("error: --local-base-port must be in 1-65535", file=sys.stderr)
        return 2
    if args.local_base_port + args.envs - 1 > 65535:
        print("error: local ML-Agents worker ports would exceed 65535", file=sys.stderr)
        return 2
    if args.reconnect_seconds <= 0 or args.upload_queue <= 0:
        print("error: reconnect/upload values are outside valid bounds", file=sys.stderr)
        return 2

    env_path = Path(args.env).expanduser().resolve()
    if not env_path.is_file():
        print(f"error: Unity training executable does not exist: {env_path}", file=sys.stderr)
        return 2
    try:
        token = wan.load_auth_token(args.auth_token_file)
        release_identity = _managed_release_identity()
    except (ValueError, RuntimeError) as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 2

    stop = threading.Event()
    stop_request_value = os.environ.get(MANAGED_STOP_FILE_ENV, "").strip()
    stop_request_file = (
        Path(stop_request_value).expanduser().resolve() if stop_request_value else None
    )
    stop_watcher: Optional[threading.Thread] = None
    if stop_request_file is not None:
        stop_watcher = threading.Thread(
            target=_watch_managed_stop_request,
            args=(stop_request_file, stop),
            name="bees-wan-managed-stop",
            daemon=True,
        )
        stop_watcher.start()

    def request_stop(_signum: int, _frame: Any) -> None:
        stop.set()

    old_sigint = signal.signal(signal.SIGINT, request_stop)
    old_sigterm = signal.signal(signal.SIGTERM, request_stop)
    try:
        throughput_value = os.environ.get(worker.THROUGHPUT_METRICS_ENV, "").strip()
        telemetry_run_id = os.environ.get(worker.TRAINING_RUN_ID_ENV, "").strip()
        failure_state_path = (
            Path(throughput_value).expanduser().resolve().with_name(
                SESSION_FAILURE_STATE_FILE
            )
            if throughput_value and telemetry_run_id
            else None
        )
        failure_telemetry = _SessionFailureTelemetry(
            state_path=failure_state_path,
            run_id=telemetry_run_id,
            runtime_version=_runtime_version_identity(),
        )
        reconnect_backoff = _ReconnectBackoff(args.reconnect_seconds)
        client = ElasticBrokerClient(
            args.broker_host,
            args.broker_port,
            token,
            actor_id=args.actor_id,
            actor_key=args.actor_key,
            env_count=args.envs,
            build_id=release_identity["build_id"],
            run_id=release_identity["run_id"],
            compatibility_key=release_identity["compatibility_key"],
            environment_id=release_identity["environment_id"],
        )
        while not stop.is_set():
            actor_session = None
            session_started_monotonic: Optional[float] = None
            session_env_count = int(client.env_count)
            startup_health = _StartupHealthHeartbeat(
                actor_id=(args.actor_id if args.actor_id is not None else -1),
                env_count=session_env_count,
            )
            startup_health.start()
            startup_health.set_ready("waiting-for-central")
            try:
                raw_session = worker._wait_for_broker(client, stop, args.reconnect_seconds)
                session_id = raw_session.get("session_id")
                if not isinstance(session_id, str) or not session_id:
                    raise RuntimeError("Elastic WAN session is missing session_id")
                _validate_session_release_identity(raw_session, release_identity)
                startup_health.set_phase("claiming-session")
                actor_id = client.claim(session_id)
                print(f"[Bees WAN actor] learner assigned actor slot {actor_id}.")
                session, worker_offset, _capacity_envs = _elastic_session(
                    raw_session,
                    actor_id=actor_id,
                    env_count=session_env_count,
                )
                startup_health.set_ready("session-claimed", actor_id=actor_id)
                startup_health.set_phase("starting-session")
                actor_session = elastic_session.ElasticActorSession(
                    client,
                    session,
                    actor_id=actor_id,
                    env_path=env_path,
                    local_base_port=args.local_base_port,
                    torch_device=args.torch_device,
                    graphics=args.graphics,
                    stop=stop,
                    upload_queue_size=args.upload_queue,
                    startup_health=startup_health.set_phase,
                    runtime_progress=startup_health.mark_progress,
                    env_count_changed=startup_health.set_env_count,
                )
                actor_session.worker_offset = worker_offset
                actor_session.total_envs = (
                    int(raw_session["remote_worker_base"]) + session_env_count
                )
                actor_session._session_failure_telemetry = failure_telemetry
                try:
                    actor_session.start()
                    startup_health.set_ready("running", actor_id=actor_id)
                    session_started_monotonic = time.monotonic()
                    actor_session.run()
                finally:
                    actor_session.close()
            except worker.BrokerSessionChanged:
                reconnect_backoff.reset()
                print("[Bees WAN actor] central generation changed; reconnecting to the next trainer session.")
                stop.wait(0.25)
            except worker.BrokerStaleActor:
                # Fallback for a freshness race that escaped the in-session resynchronizer.
                # Do not count this as a session failure; central state simply advanced while
                # an old-epoch request was in flight.
                reconnect_backoff.reset()
                startup_health.set_ready("resynchronizing")
                print("[Bees WAN actor] central actor state advanced; resynchronizing.")
                stop.wait(0.1)
            except worker.BrokerUnavailable as exc:
                # The central broker is intentionally absent during release/publish phases.
                # The actor process is healthy and should remain ready to reconnect rather than
                # advertising a false child failure to the training-control supervisor.
                startup_health.set_ready("waiting-for-central")
                delay = reconnect_backoff.next_delay()
                print(
                    f"[Bees WAN actor] central trainer unavailable: {exc}; "
                    f"retrying in {delay:.1f}s."
                )
                stop.wait(delay)
            except KeyboardInterrupt:
                stop.set()
            except Exception as exc:
                if (
                    session_started_monotonic is not None
                    and time.monotonic() - session_started_monotonic
                    >= HEALTHY_SESSION_RESET_SECONDS
                ):
                    reconnect_backoff.reset()
                failure_telemetry.record(exc)
                startup_health.set_error(exc)
                if actor_session is not None:
                    try:
                        actor_session._write_throughput_metrics(force=True)
                    except Exception:
                        pass
                _report_session_failure(exc, actor_session)
                stop.wait(reconnect_backoff.next_delay())
            finally:
                startup_health.stop()
        return 0
    finally:
        stop.set()
        if stop_watcher is not None:
            stop_watcher.join(timeout=1.0)
        signal.signal(signal.SIGINT, old_sigint)
        signal.signal(signal.SIGTERM, old_sigterm)


if __name__ == "__main__":
    raise SystemExit(main())
