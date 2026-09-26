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
from bees_process_safety import write_managed_health


MANAGED_STOP_FILE_ENV = "BEES_TRAINING_STOP_FILE"


MAX_RECONNECT_BACKOFF_SECONDS = 30.0
HEALTHY_SESSION_RESET_SECONDS = 60.0


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


class _SessionFailureTelemetry:
    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._count = 0
        self._last_failure_monotonic: Optional[float] = None
        self._last_failure_type = ""

    def record(self, exc: BaseException) -> None:
        with self._lock:
            self._count += 1
            self._last_failure_monotonic = time.monotonic()
            self._last_failure_type = type(exc).__name__

    def snapshot(self) -> dict[str, Any]:
        with self._lock:
            age = (
                None
                if self._last_failure_monotonic is None
                else max(0.0, time.monotonic() - self._last_failure_monotonic)
            )
            return {
                "session_failures_total": self._count,
                "seconds_since_last_session_failure": age,
                "last_session_failure_type": self._last_failure_type,
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
    build_id = str(actual.get("build_id", "")).strip()
    run_id = str(actual.get("run_id", "")).strip()
    compatibility_key = str(actual.get("compatibility_key", "")).strip().lower()
    environment_id = str(actual.get("environment_id", "")).strip().lower()
    if (
        build_id != str(expected.get("build_id", "")).strip()
        or run_id != str(expected.get("run_id", "")).strip()
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
    if not isinstance(max_actors, int) or not 1 <= max_actors <= elastic.MAX_REMOTE_ACTORS:
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
        failure_telemetry = _SessionFailureTelemetry()
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
            try:
                raw_session = worker._wait_for_broker(client, stop, args.reconnect_seconds)
                session_id = raw_session.get("session_id")
                if not isinstance(session_id, str) or not session_id:
                    raise RuntimeError("Elastic WAN session is missing session_id")
                _validate_session_release_identity(raw_session, release_identity)
                actor_id = client.claim(session_id)
                print(f"[Bees WAN actor] learner assigned actor slot {actor_id}.")
                session, worker_offset, _capacity_envs = _elastic_session(
                    raw_session,
                    actor_id=actor_id,
                    env_count=args.envs,
                )
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
                )
                actor_session.worker_offset = worker_offset
                actor_session.total_envs = int(raw_session["remote_worker_base"]) + args.envs
                actor_session._session_failure_telemetry = failure_telemetry
                try:
                    actor_session.start()
                    write_managed_health(
                        "ready",
                        details={
                            "component": "elastic-wan-actor",
                            "actor_id": int(actor_id),
                            "env_count": int(args.envs),
                        },
                    )
                    session_started_monotonic = time.monotonic()
                    actor_session.run()
                finally:
                    actor_session.close()
            except worker.BrokerSessionChanged:
                reconnect_backoff.reset()
                print("[Bees WAN actor] central generation changed; reconnecting to the next trainer session.")
                stop.wait(0.25)
            except worker.BrokerUnavailable as exc:
                write_managed_health(
                    "error",
                    error=f"BrokerUnavailable: {exc}",
                    details={"component": "elastic-wan-actor"},
                )
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
                write_managed_health(
                    "error",
                    error=f"{type(exc).__name__}: {exc}",
                    details={"component": "elastic-wan-actor"},
                )
                if actor_session is not None:
                    try:
                        actor_session._write_throughput_metrics(force=True)
                    except Exception:
                        pass
                _report_session_failure(exc, actor_session)
                stop.wait(reconnect_backoff.next_delay())
        return 0
    finally:
        stop.set()
        if stop_watcher is not None:
            stop_watcher.join(timeout=1.0)
        signal.signal(signal.SIGINT, old_sigint)
        signal.signal(signal.SIGTERM, old_sigterm)


if __name__ == "__main__":
    raise SystemExit(main())
