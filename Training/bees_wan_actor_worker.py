"""Run one persistent Bees WAN rollout actor.

A WAN actor owns several local Unity environments and performs policy inference locally. It connects
to Exeter's loopback-only ``bees_wan_actor_training`` broker through the managed private forward, receives exact
policy snapshots, assembles native ML-Agents trajectories locally, and uploads complete batches.
No optimizer, PPO update, checkpoint, candidate registration, promotion, or release work occurs here.

The helper is intentionally persistent across central continual-learning generations. A central
trainer restart changes the broker session id; the actor tears down the old Unity session, waits for
the next broker session, and rejoins using the same actor id.
"""

from __future__ import annotations

import argparse
import copy
import hashlib
import http.client
import json
import math
import os
import queue
import signal
import subprocess
import sys
import tempfile
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path
from typing import Any, Dict, List, Mapping, Optional, Sequence, Tuple

import numpy as np

import bees_wan_actor_training as wan


DEFAULT_RECONNECT_SECONDS = 5.0
DEFAULT_LOCAL_UPLOAD_QUEUE = 8
DEFAULT_STATE_WAIT_SECONDS = 20.0
MAX_TRAJECTORIES_PER_UPLOAD = 256
MAX_UNITY_SEED = (1 << 31) - 1
THROUGHPUT_METRICS_ENV = "BEES_TRAINING_THROUGHPUT_FILE"
TRAINING_RUN_ID_ENV = "BEES_TRAINING_RUN_ID"
NETWORK_TRAFFIC_STATE_FILE = "worker-network-traffic.json"
MIB_BYTES = 1024 * 1024


def _resolve_actor_seed(
    base_seed: int,
    *,
    session_id: str,
    worker_offset: int,
    env_count: int,
) -> int:
    """Resolve ML-Agents' -1 seed sentinel and keep all Unity worker seeds int32-safe."""
    if env_count <= 0:
        raise ValueError("WAN actor environment count must be positive")
    if worker_offset < 0:
        raise ValueError("WAN actor worker offset must not be negative")
    if base_seed == -1:
        # ML-Agents normally replaces -1 with a random 0..9999 run seed in run_cli().
        # Remote actors receive the pre-resolution RunOptions instead, so derive a stable
        # per-session equivalent that survives actor reconnects.
        digest = hashlib.sha256(session_id.encode("utf-8")).digest()
        base_seed = int.from_bytes(digest[:4], "big") % 10_000
    elif base_seed < 0:
        raise ValueError(f"WAN actor received invalid ML-Agents seed {base_seed}")

    # create_environment_factory() adds each local worker_id to this value and serializes
    # the result through an int32 protobuf field. Preserve headroom for every local worker.
    max_actor_seed = MAX_UNITY_SEED - (env_count - 1)
    return (int(base_seed) + int(worker_offset)) % (max_actor_seed + 1)


def _trajectory_step_count(trajectories: Sequence[Any]) -> int:
    total = 0
    for trajectory in trajectories:
        steps = getattr(trajectory, "steps", None)
        if steps is None:
            continue
        try:
            total += len(steps)
        except TypeError:
            continue
    return total


class BrokerUnavailable(RuntimeError):
    pass


class BrokerSessionChanged(RuntimeError):
    pass


class BrokerStaleActor(RuntimeError):
    pass


class BrokerBackpressure(RuntimeError):
    pass


class BrokerClient:
    def __init__(self, host: str, port: int, token: str, timeout: float = 30.0):
        self.base = f"http://{host}:{port}"
        self.token = token
        self.timeout = timeout
        self._traffic_lock = threading.Lock()
        self._traffic_run_id = os.environ.get(TRAINING_RUN_ID_ENV, "").strip()
        throughput_path = os.environ.get(THROUGHPUT_METRICS_ENV, "").strip()
        self._traffic_state_path = (
            Path(throughput_path).expanduser().resolve().with_name(NETWORK_TRAFFIC_STATE_FILE)
            if throughput_path and self._traffic_run_id
            else None
        )
        self._traffic_sent_bytes = 0
        self._traffic_received_bytes = 0
        self._traffic_last_sample_time = time.monotonic()
        self._traffic_last_sample_bytes = 0
        self._traffic_mib_per_s = 0.0
        self._load_traffic_state()

    def _load_traffic_state(self) -> None:
        path = self._traffic_state_path
        if path is None or not path.is_file():
            return
        try:
            payload = json.loads(path.read_text(encoding="utf-8"))
            if not isinstance(payload, Mapping) or payload.get("run_id") != self._traffic_run_id:
                return
            sent = int(payload.get("sent_bytes_total", 0))
            received = int(payload.get("received_bytes_total", 0))
            if sent < 0 or received < 0:
                return
        except (OSError, ValueError, TypeError, json.JSONDecodeError):
            return
        self._traffic_sent_bytes = sent
        self._traffic_received_bytes = received
        self._traffic_last_sample_bytes = sent + received

    def _persist_traffic_state_locked(self) -> None:
        path = self._traffic_state_path
        if path is None:
            return
        try:
            path.parent.mkdir(parents=True, exist_ok=True)
            temporary = path.with_name(path.name + f".tmp-{os.getpid()}")
            temporary.write_text(
                json.dumps(
                    {
                        "run_id": self._traffic_run_id,
                        "sent_bytes_total": self._traffic_sent_bytes,
                        "received_bytes_total": self._traffic_received_bytes,
                    },
                    sort_keys=True,
                )
                + "\n",
                encoding="utf-8",
            )
            os.replace(temporary, path)
        except OSError:
            return

    def _record_traffic(self, *, sent: int = 0, received: int = 0) -> None:
        with self._traffic_lock:
            self._traffic_sent_bytes += max(0, int(sent))
            self._traffic_received_bytes += max(0, int(received))

    def traffic_snapshot(self) -> Mapping[str, object]:
        now = time.monotonic()
        with self._traffic_lock:
            total = self._traffic_sent_bytes + self._traffic_received_bytes
            elapsed = now - self._traffic_last_sample_time
            if elapsed >= 0.5:
                delta = max(0, total - self._traffic_last_sample_bytes)
                self._traffic_mib_per_s = delta / elapsed / MIB_BYTES
                self._traffic_last_sample_time = now
                self._traffic_last_sample_bytes = total
            self._persist_traffic_state_locked()
            return {
                "network_sent_bytes_total": self._traffic_sent_bytes,
                "network_received_bytes_total": self._traffic_received_bytes,
                "network_mib_per_s": self._traffic_mib_per_s,
            }

    def _request(
        self,
        method: str,
        path: str,
        *,
        query: Optional[Mapping[str, object]] = None,
        payload: Any = None,
    ) -> Tuple[int, Mapping[str, str], bytes]:
        if query:
            encoded_query = urllib.parse.urlencode({key: str(value) for key, value in query.items()})
            path = path + ("&" if "?" in path else "?") + encoded_query
        body = wan.encode_payload(payload) if payload is not None else None
        request = urllib.request.Request(
            self.base + path,
            data=body,
            method=method,
            headers={
                "Authorization": "Bearer " + self.token,
                "Content-Type": "application/octet-stream",
            },
        )
        self._record_traffic(sent=len(body) if body is not None else 0)
        try:
            with urllib.request.urlopen(request, timeout=self.timeout) as response:
                raw = response.read()
                self._record_traffic(received=len(raw))
                return int(response.status), dict(response.headers.items()), raw
        except urllib.error.HTTPError as exc:
            raw = exc.read()
            self._record_traffic(received=len(raw))
            code = "http-error"
            message = raw.decode("utf-8", errors="replace")
            try:
                parsed = json.loads(message)
                if isinstance(parsed, Mapping):
                    code = str(parsed.get("error") or code)
                    message = str(parsed.get("message") or message)
            except json.JSONDecodeError:
                pass
            if exc.code == 409 and code == "session-changed":
                raise BrokerSessionChanged(message) from exc
            if exc.code == 409 and code == "stale-actor-state":
                raise BrokerStaleActor(message) from exc
            if exc.code == 429 and code == "backpressure":
                raise BrokerBackpressure(message) from exc
            raise RuntimeError(f"WAN broker rejected {method} {path}: {exc.code} {code}: {message}") from exc
        except (urllib.error.URLError, TimeoutError, ConnectionError, http.client.HTTPException, OSError) as exc:
            raise BrokerUnavailable(str(exc)) from exc

    def session(self) -> Mapping[str, Any]:
        status, _headers, body = self._request("GET", "/session")
        if status != 200:
            raise RuntimeError(f"Unexpected WAN broker session status {status}")
        value = wan.decode_payload(body)
        if not isinstance(value, Mapping):
            raise RuntimeError("WAN broker session payload is malformed")
        return value

    def state(
        self,
        session_id: str,
        policy_epoch: int,
        control_epoch: int,
        wait_seconds: float,
    ) -> Mapping[str, Any]:
        status, _headers, body = self._request(
            "GET",
            "/state",
            query={
                "session": session_id,
                "policy_epoch": policy_epoch,
                "control_epoch": control_epoch,
                "wait": wait_seconds,
            },
        )
        if status != 200:
            raise RuntimeError(f"Unexpected WAN broker state status {status}")
        value = json.loads(body.decode("utf-8"))
        if not isinstance(value, Mapping):
            raise RuntimeError("WAN broker state response is malformed")
        return value

    def policy(self, session_id: str, behavior: str, version: int) -> Optional[Mapping[str, Any]]:
        status, headers, body = self._request(
            "GET",
            "/policy",
            query={"session": session_id, "behavior": behavior, "version": version},
        )
        if status == 204:
            return None
        if status != 200:
            raise RuntimeError(f"Unexpected WAN broker policy status {status}")
        expected_hash = headers.get("X-Bees-Policy-Sha256")
        if expected_hash and hashlib.sha256(body).hexdigest() != expected_hash:
            raise RuntimeError("WAN policy payload failed transport SHA-256 verification")
        value = wan.decode_payload(body)
        if not isinstance(value, Mapping):
            raise RuntimeError("WAN broker policy payload is malformed")
        return value

    def control(self, session_id: str, epoch: int) -> Optional[Mapping[str, Any]]:
        status, _headers, body = self._request(
            "GET", "/control", query={"session": session_id, "epoch": epoch}
        )
        if status == 204:
            return None
        if status != 200:
            raise RuntimeError(f"Unexpected WAN broker control status {status}")
        value = wan.decode_payload(body)
        if not isinstance(value, Mapping):
            raise RuntimeError("WAN broker control payload is malformed")
        return value

    def register(self, payload: Mapping[str, Any]) -> None:
        self._request("POST", "/register", payload=payload)

    def trajectories(self, payload: Mapping[str, Any]) -> None:
        self._request("POST", "/trajectories", payload=payload)

    def reset_ack(self, payload: Mapping[str, Any]) -> None:
        self._request("POST", "/reset-ack", payload=payload)


def ssh_command(
    executable: str,
    target: str,
    *,
    local_port: int,
    broker_port: int,
    options: Sequence[str],
) -> List[str]:
    command = [
        executable,
        "-N",
        "-o",
        "ExitOnForwardFailure=yes",
        "-o",
        "ServerAliveInterval=30",
        "-o",
        "ServerAliveCountMax=3",
    ]
    for option in options:
        command.extend(["-o", option])
    command.extend(
        ["-L", f"127.0.0.1:{local_port}:127.0.0.1:{broker_port}", target]
    )
    return command


def _terminate(process: Optional[subprocess.Popen]) -> None:
    if process is None or process.poll() is not None:
        return
    try:
        process.terminate()
        process.wait(timeout=10)
    except Exception:
        try:
            process.kill()
        except Exception:
            pass


def _wait_for_broker(client: BrokerClient, stop: threading.Event, delay: float) -> Mapping[str, Any]:
    last_error: Optional[str] = None
    while not stop.is_set():
        try:
            return client.session()
        except BrokerUnavailable as exc:
            text = str(exc)
            if text != last_error:
                print(f"[Bees WAN actor] waiting for central trainer: {text}")
                last_error = text
            stop.wait(delay)
    raise KeyboardInterrupt


def _validate_session(session: Mapping[str, Any], actor_id: int) -> Tuple[str, int, Any]:
    if session.get("protocol_version") != wan.WAN_PROTOCOL_VERSION:
        raise RuntimeError(
            f"WAN protocol mismatch: central={session.get('protocol_version')!r} "
            f"local={wan.WAN_PROTOCOL_VERSION}."
        )
    if session.get("mlagents_version") != wan.EXPECTED_MLAGENTS_VERSION:
        raise RuntimeError(
            f"ML-Agents mismatch: central={session.get('mlagents_version')!r} "
            f"local={wan.EXPECTED_MLAGENTS_VERSION}."
        )
    session_id = session.get("session_id")
    actor_count = session.get("actor_count")
    envs_per_actor = session.get("envs_per_actor")
    run_options = session.get("run_options")
    if not isinstance(session_id, str) or not session_id:
        raise RuntimeError("WAN broker session id is malformed")
    if not isinstance(actor_count, int) or not isinstance(envs_per_actor, int):
        raise RuntimeError("WAN broker actor topology is malformed")
    if not 0 <= actor_id < actor_count:
        raise RuntimeError(f"actor id {actor_id} is outside central actor count {actor_count}")
    if envs_per_actor <= 0 or run_options is None:
        raise RuntimeError("WAN broker session lacks a usable environment topology/run options")
    return session_id, envs_per_actor, run_options


def _build_template_policy(behavior_id: str, behavior_spec: Any, run_options: Any, seed: int):
    from mlagents.trainers.behavior_id_utils import BehaviorIdentifiers
    from mlagents.trainers.policy.torch_policy import TorchPolicy
    from mlagents.trainers.torch_entities.networks import SharedActorCritic, SimpleActor

    parsed = BehaviorIdentifiers.from_name_behavior_id(behavior_id)
    settings = run_options.behaviors[parsed.brain_name]
    hyperparameters = settings.hyperparameters
    shared_critic = bool(getattr(hyperparameters, "shared_critic", False))
    actor_cls = SharedActorCritic if shared_critic else SimpleActor
    actor_kwargs: Dict[str, Any] = {
        "conditional_sigma": False,
        "tanh_squash": False,
    }
    if shared_critic:
        actor_kwargs["stream_names"] = [key.value for key in settings.reward_signals]
    return TorchPolicy(
        seed,
        behavior_spec,
        settings.network_settings,
        actor_cls,
        actor_kwargs,
    )


def rollout_horizon(trainer_settings: Any, total_envs: int) -> int:
    """Choose short WAN segments so roughly one learning trajectory per env fills one PPO buffer."""
    if not isinstance(total_envs, int) or isinstance(total_envs, bool) or total_envs <= 0:
        raise ValueError("total_envs must be a positive integer")
    time_horizon = int(trainer_settings.time_horizon)
    buffer_size = int(getattr(trainer_settings.hyperparameters, "buffer_size", 0))
    if time_horizon <= 0 or buffer_size <= 0:
        raise ValueError("WAN actor trainer requires positive time_horizon and PPO buffer_size")
    target = int(math.ceil((buffer_size + 1) / total_envs))
    return max(1, min(time_horizon, target))


def _remap_step(step: Any, worker_offset: int):
    from mlagents.trainers.env_manager import EnvironmentStep

    return EnvironmentStep(
        step.current_all_step_result,
        int(step.worker_id) + worker_offset,
        step.brain_name_to_action_info,
        step.environment_stats,
    )


def _remap_manager_initial_steps(manager: Any, worker_offset: int) -> None:
    manager.first_step_infos = [
        _remap_step(step, worker_offset) for step in manager.first_step_infos
    ]
    for worker in manager.env_workers:
        worker.previous_step = _remap_step(worker.previous_step, worker_offset)


def _remap_completed_steps(manager: Any, steps: Sequence[Any], worker_offset: int) -> List[Any]:
    mapped = []
    for step in steps:
        global_step = _remap_step(step, worker_offset)
        mapped.append(global_step)
        manager.env_workers[int(step.worker_id)].previous_step = global_step
    return mapped


def _drain_inflight_without_training(manager: Any, worker_offset: int, timeout_seconds: float) -> None:
    """Advance pending old-policy actions to current observations without keeping their experience."""
    from mlagents.trainers.subprocess_env_manager import EnvironmentCommand

    deadline = time.monotonic() + timeout_seconds
    responses = []
    while any(worker.waiting for worker in manager.env_workers):
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            raise TimeoutError("Timed out draining in-flight WAN actor environment steps")
        try:
            response = manager.step_queue.get(timeout=min(1.0, remaining))
        except queue.Empty:
            continue
        if response.cmd == EnvironmentCommand.ENV_EXITED:
            raise RuntimeError(
                f"Unity worker {response.worker_id} exited while synchronizing policy: {response.payload}"
            )
        if response.cmd == EnvironmentCommand.STEP:
            manager.env_workers[response.worker_id].waiting = False
            responses.append(response)
    if responses:
        completed = manager._postprocess_steps(responses)
        _remap_completed_steps(manager, completed, worker_offset)


def _clear_partial_trajectories(manager: Any) -> None:
    for agent_manager in manager.agent_managers.values():
        agent_manager.end_episode()


def _drain_queue(q: queue.Queue) -> None:
    while True:
        try:
            q.get_nowait()
        except queue.Empty:
            return


class ActorSession:
    def __init__(
        self,
        client: BrokerClient,
        session: Mapping[str, Any],
        *,
        actor_id: int,
        env_path: Path,
        local_base_port: int,
        torch_device: str,
        graphics: bool,
        stop: threading.Event,
        upload_queue_size: int,
    ) -> None:
        self.client = client
        self.session = session
        self.actor_id = actor_id
        self.env_path = env_path
        self.local_base_port = local_base_port
        self.torch_device = torch_device
        self.graphics = graphics
        self.stop = stop
        self.session_id, self.env_count, self.central_run_options = _validate_session(session, actor_id)
        actor_count = int(session["actor_count"])
        self.total_envs = actor_count * self.env_count
        self.worker_offset = actor_id * self.env_count
        self.control_epoch = 0
        self.policy_epoch = -1
        self.policy_versions: Dict[str, int] = {}
        self.templates: Dict[str, Any] = {}
        self.manager = None
        self._state_changed = threading.Event()
        self._session_changed = threading.Event()
        self._stale = threading.Event()
        self._thread_error: queue.Queue = queue.Queue()
        self._upload_queue: queue.Queue = queue.Queue(maxsize=upload_queue_size)
        self._upload_stop = threading.Event()
        self._watcher: Optional[threading.Thread] = None
        self._uploader: Optional[threading.Thread] = None
        self._onnx_temp = tempfile.TemporaryDirectory(prefix=f"bees-wan-actor-{actor_id}-")
        self._current_env_config = None
        self._rollout_horizons: Dict[str, int] = {}
        throughput_path = os.environ.get(THROUGHPUT_METRICS_ENV, "").strip()
        self._throughput_metrics_path = (
            Path(throughput_path).expanduser().resolve() if throughput_path else None
        )
        self._throughput_lock = threading.Lock()
        self._accepted_steps_total = 0
        self._accepted_trajectories_total = 0
        self._learner_consumed_steps_total = 0
        self._last_throughput_write = 0.0

    def _write_throughput_metrics(self, *, force: bool = False) -> None:
        path = self._throughput_metrics_path
        if path is None:
            return
        now = time.monotonic()
        with self._throughput_lock:
            if not force and now - self._last_throughput_write < 1.0:
                return
            payload = {
                "pid": os.getpid(),
                "env_count": self.env_count,
                "accepted_steps_total": self._accepted_steps_total,
                "accepted_trajectories_total": self._accepted_trajectories_total,
                "learner_consumed_steps_total": self._learner_consumed_steps_total,
                "upload_queue_depth": self._upload_queue.qsize(),
            }
            payload.update(self.client.traffic_snapshot())
            try:
                path.parent.mkdir(parents=True, exist_ok=True)
                temporary = path.with_name(path.name + f".tmp-{os.getpid()}")
                temporary.write_text(
                    json.dumps(payload, sort_keys=True) + "\n",
                    encoding="utf-8",
                )
                os.replace(temporary, path)
                self._last_throughput_write = now
            except OSError:
                # Metrics are advisory. Never stop training because status publication failed.
                return

    def _record_accepted_trajectories(self, trajectories: Sequence[Any]) -> None:
        step_count = _trajectory_step_count(trajectories)
        with self._throughput_lock:
            self._accepted_steps_total += step_count
            self._accepted_trajectories_total += len(trajectories)
        self._write_throughput_metrics()

    def close(self) -> None:
        self._upload_stop.set()
        if self.manager is not None:
            try:
                self.manager.close()
            except Exception:
                pass
            self.manager = None
        if self._watcher is not None:
            self._watcher.join(timeout=2.0)
        if self._uploader is not None:
            self._uploader.join(timeout=2.0)
        self._write_throughput_metrics(force=True)
        self._onnx_temp.cleanup()

    def _remote_run_options(self) -> Any:
        options = copy.deepcopy(self.central_run_options)
        options.env_settings.env_path = str(self.env_path)
        options.env_settings.base_port = self.local_base_port
        options.env_settings.num_envs = self.env_count
        options.env_settings.seed = _resolve_actor_seed(
            int(options.env_settings.seed),
            session_id=self.session_id,
            worker_offset=self.worker_offset,
            env_count=self.env_count,
        )
        options.engine_settings.no_graphics = not self.graphics
        options.torch_settings.device = self.torch_device
        return options

    @staticmethod
    def _run_logs_dir(options: Any) -> str:
        managed_log_dir = os.environ.get("BEES_TRAINING_LOG_DIR", "").strip()
        if managed_log_dir:
            log_dir = Path(managed_log_dir).expanduser().resolve()
            log_dir.mkdir(parents=True, exist_ok=True)
            return str(log_dir)
        return str(options.checkpoint_settings.run_logs_dir)

    @staticmethod
    def _print_player_log_tails(run_logs_dir: str, *, lines: int = 80) -> None:
        root = Path(run_logs_dir)
        logs = sorted(root.glob("Player-*.log"))
        if not logs:
            print(
                f"[Bees WAN actor] no Unity Player-*.log files found in {root}.",
                file=sys.stderr,
            )
            return
        for log_path in logs:
            try:
                text = log_path.read_text(encoding="utf-8", errors="replace")
            except OSError as exc:
                print(
                    f"[Bees WAN actor] could not read {log_path}: {exc}",
                    file=sys.stderr,
                )
                continue
            tail = text.splitlines()[-lines:]
            print(
                f"[Bees WAN actor] Unity log tail {log_path}:",
                file=sys.stderr,
            )
            for line in tail:
                print(line, file=sys.stderr)

    def _initial_control(self) -> Mapping[str, Any]:
        deadline = time.monotonic() + max(30.0, float(self.central_run_options.env_settings.timeout_wait))
        while not self.stop.is_set() and time.monotonic() < deadline:
            record = self.client.control(self.session_id, self.control_epoch)
            if record is not None:
                if record.get("kind") != "reset" or int(record.get("epoch", 0)) <= 0:
                    raise RuntimeError("WAN actor initial central control record must be a reset")
                return record
            time.sleep(0.1)
        raise TimeoutError("WAN actor timed out waiting for initial central reset")

    def start(self) -> None:
        import mlagents.trainers
        import mlagents.trainers.learn as learn
        from mlagents.torch_utils import torch, set_torch_config
        from mlagents.trainers.agent_processor import AgentManager
        from mlagents.trainers.behavior_id_utils import BehaviorIdentifiers
        from mlagents.trainers.stats import StatsReporter
        from mlagents.trainers.subprocess_env_manager import SubprocessEnvManager

        if mlagents.trainers.__version__ != wan.EXPECTED_MLAGENTS_VERSION:
            raise RuntimeError(
                f"WAN actor requires ML-Agents {wan.EXPECTED_MLAGENTS_VERSION}; "
                f"installed={mlagents.trainers.__version__}."
            )
        options = self._remote_run_options()
        run_logs_dir = self._run_logs_dir(options)
        set_torch_config(options.torch_settings)
        np.random.seed(int(options.env_settings.seed))
        torch.manual_seed(int(options.env_settings.seed))

        factory = learn.create_environment_factory(
            options.env_settings.env_path,
            options.engine_settings.no_graphics,
            options.engine_settings.no_graphics_monitor,
            options.env_settings.seed,
            options.env_settings.num_areas,
            options.env_settings.timeout_wait,
            options.env_settings.base_port,
            options.env_settings.env_args,
            run_logs_dir,
        )
        self.manager = SubprocessEnvManager(factory, options, self.env_count)

        control = self._initial_control()
        self.control_epoch = int(control["epoch"])
        self._current_env_config = control.get("config")
        print(
            f"[Bees WAN actor] launching {self.env_count} Unity environments "
            f"base_port={options.env_settings.base_port} logs={run_logs_dir}.",
            flush=True,
        )
        try:
            self.manager.reset(config=self._current_env_config)
        except Exception:
            self._print_player_log_tails(run_logs_dir)
            raise
        _remap_manager_initial_steps(self.manager, self.worker_offset)

        behavior_specs = self.manager.training_behaviors
        if not behavior_specs:
            raise RuntimeError("WAN actor Unity environments expose no trainable behaviors")
        for behavior_id, spec in behavior_specs.items():
            parsed = BehaviorIdentifiers.from_name_behavior_id(behavior_id)
            trainer_settings = options.behaviors[parsed.brain_name]
            template = _build_template_policy(
                behavior_id,
                spec,
                options,
                seed=(int(options.env_settings.seed) + parsed.team_id) & MAX_UNITY_SEED,
            )
            horizon = rollout_horizon(trainer_settings, self.total_envs)
            manager = AgentManager(
                template,
                behavior_id,
                StatsReporter(f"WANActor{self.actor_id}/{behavior_id}"),
                horizon,
                threaded=False,
            )
            self._rollout_horizons[behavior_id] = horizon
            self.templates[behavior_id] = template
            self.manager.set_agent_manager(behavior_id, manager)
            self.manager.set_policy(behavior_id, template)

        self.client.register(
            {
                "session_id": self.session_id,
                "actor_id": self.actor_id,
                "control_epoch": self.control_epoch,
                "behavior_specs": dict(behavior_specs),
            }
        )
        self.client.reset_ack(
            {
                "session_id": self.session_id,
                "actor_id": self.actor_id,
                "control_epoch": self.control_epoch,
            }
        )
        self._synchronize_state(require_policy=True)

        self._uploader = threading.Thread(target=self._upload_loop, name="bees-wan-upload", daemon=True)
        self._watcher = threading.Thread(target=self._watch_loop, name="bees-wan-watch", daemon=True)
        self._uploader.start()
        self._watcher.start()
        print(
            f"[Bees WAN actor] joined session={self.session_id} actor={self.actor_id} "
            f"workers={self.worker_offset}-{self.worker_offset + self.env_count - 1} "
            f"local_envs={self.env_count} total_envs={self.total_envs} "
            f"rollout_horizons={self._rollout_horizons} device={self.torch_device}."
        )
        self._write_throughput_metrics(force=True)

    def _write_onnx(self, behavior: str, payload: Mapping[str, Any]) -> Path:
        model_bytes = payload.get("model_bytes")
        expected = payload.get("model_sha256")
        if not isinstance(model_bytes, bytes) or not isinstance(expected, str):
            raise RuntimeError("Frozen WAN policy payload is malformed")
        actual = hashlib.sha256(model_bytes).hexdigest()
        if actual != expected:
            raise RuntimeError("Frozen WAN policy failed model SHA-256 verification")
        safe = hashlib.sha256(behavior.encode("utf-8")).hexdigest()[:12]
        path = Path(self._onnx_temp.name) / f"{safe}-{actual[:24]}.onnx"
        if path.exists():
            if hashlib.sha256(path.read_bytes()).hexdigest() != actual:
                raise RuntimeError("Frozen WAN policy cache conflicts with immutable model identity")
        else:
            path.write_bytes(model_bytes)
        return path

    def _apply_policy(self, behavior: str, version: int) -> None:
        payload = self.client.policy(self.session_id, behavior, -1)
        if payload is None:
            raise RuntimeError(f"Central WAN policy {behavior} unexpectedly returned no payload")
        template = self.templates.get(behavior)
        if template is None:
            raise RuntimeError(f"Central WAN policy references unknown behavior {behavior!r}")
        kind = payload.get("kind")
        if kind == "torch":
            weights = payload.get("weights")
            step = payload.get("step")
            if not isinstance(weights, Mapping) or not isinstance(step, int):
                raise RuntimeError("Torch WAN policy payload is malformed")
            template.load_weights(weights)
            template.set_step(step)
            policy = template
        elif kind == "frozen_onnx":
            from bees_continual_historical import _build_frozen_onnx_policy

            path = self._write_onnx(behavior, payload)
            provider = payload.get("provider")
            if provider is not None and not isinstance(provider, str):
                raise RuntimeError("Frozen WAN policy provider is malformed")
            policy = _build_frozen_onnx_policy(template, path, provider)
        else:
            raise RuntimeError(f"Unsupported WAN policy kind {kind!r}")
        self.manager.set_policy(behavior, policy)
        self.policy_versions[behavior] = version

    def _synchronize_state(self, *, require_policy: bool = False) -> None:
        state = self.client.state(
            self.session_id,
            self.policy_epoch,
            self.control_epoch,
            0.0,
        )
        new_control = int(state.get("control_epoch", -1))
        remote_versions_raw = state.get("policy_versions")
        if not isinstance(remote_versions_raw, Mapping):
            raise RuntimeError("WAN central state has malformed policy_versions")
        remote_versions = {str(key): int(value) for key, value in remote_versions_raw.items()}
        expected_behaviors = set(self.templates)
        if require_policy and set(remote_versions) != expected_behaviors:
            deadline = time.monotonic() + max(30.0, float(self.central_run_options.env_settings.timeout_wait))
            while (
                not self.stop.is_set()
                and time.monotonic() < deadline
                and set(remote_versions) != expected_behaviors
            ):
                time.sleep(0.1)
                state = self.client.state(self.session_id, -1, self.control_epoch, 0.0)
                remote_versions_raw = state.get("policy_versions")
                if isinstance(remote_versions_raw, Mapping):
                    remote_versions = {str(key): int(value) for key, value in remote_versions_raw.items()}
                    new_control = int(state.get("control_epoch", -1))
            if set(remote_versions) != expected_behaviors:
                raise TimeoutError(
                    "WAN actor timed out waiting for the complete central policy set; "
                    f"expected={sorted(expected_behaviors)} got={sorted(remote_versions)}"
                )
        elif remote_versions and set(remote_versions) != expected_behaviors:
            raise RuntimeError(
                "Central WAN policy set does not match local behavior set: "
                f"expected={sorted(expected_behaviors)} got={sorted(remote_versions)}"
            )

        control_changed = new_control != self.control_epoch
        policy_changed = remote_versions != self.policy_versions
        if not control_changed and not policy_changed:
            self.policy_epoch = int(state.get("policy_epoch", self.policy_epoch))
            return

        # Finish already-issued old-policy environment actions so local Unity state is current, but do
        # not add those transitions to a new trajectory. Then discard only unfinished trajectory pieces.
        _drain_inflight_without_training(
            self.manager,
            self.worker_offset,
            max(1.0, float(self.central_run_options.env_settings.timeout_wait)),
        )
        _clear_partial_trajectories(self.manager)
        _drain_queue(self._upload_queue)

        if control_changed:
            record = self.client.control(self.session_id, self.control_epoch)
            if record is None or int(record.get("epoch", -1)) != new_control:
                raise RuntimeError("WAN actor could not obtain the new central control record")
            kind = record.get("kind")
            config = record.get("config")
            if kind == "reset":
                self._current_env_config = config
                self.manager.reset(config=config)
                _remap_manager_initial_steps(self.manager, self.worker_offset)
                self.client.reset_ack(
                    {
                        "session_id": self.session_id,
                        "actor_id": self.actor_id,
                        "control_epoch": new_control,
                    }
                )
            elif kind == "parameters":
                self._current_env_config = config
                self.manager.set_env_parameters(config)
            else:
                raise RuntimeError(f"Unsupported WAN central control kind {kind!r}")
            self.control_epoch = new_control

        for behavior, version in sorted(remote_versions.items()):
            if self.policy_versions.get(behavior) != version:
                self._apply_policy(behavior, version)
        missing = sorted(set(self.policy_versions) - set(remote_versions))
        if missing:
            raise RuntimeError(f"Central WAN policy set unexpectedly removed behaviors: {missing}")
        self.policy_versions = remote_versions
        self.policy_epoch = int(state.get("policy_epoch", self.policy_epoch))
        self._state_changed.clear()
        self._stale.clear()
        print(
            f"[Bees WAN actor] synchronized policies={self.policy_versions} "
            f"control_epoch={self.control_epoch}."
        )

    def _watch_loop(self) -> None:
        while not self._upload_stop.is_set() and not self.stop.is_set():
            try:
                state = self.client.state(
                    self.session_id,
                    self.policy_epoch,
                    self.control_epoch,
                    DEFAULT_STATE_WAIT_SECONDS,
                )
                if (
                    int(state.get("policy_epoch", -1)) != self.policy_epoch
                    or int(state.get("control_epoch", -1)) != self.control_epoch
                ):
                    self._state_changed.set()
                    while (
                        self._state_changed.is_set()
                        and not self._upload_stop.is_set()
                        and not self.stop.is_set()
                    ):
                        time.sleep(0.05)
            except BrokerSessionChanged:
                self._session_changed.set()
                return
            except BrokerUnavailable:
                time.sleep(1.0)
            except BaseException as exc:
                self._thread_error.put(exc)
                return

    def _upload_loop(self) -> None:
        while not self._upload_stop.is_set() and not self.stop.is_set():
            try:
                payload = self._upload_queue.get(timeout=0.5)
            except queue.Empty:
                continue
            while not self._upload_stop.is_set() and not self.stop.is_set():
                try:
                    self.client.trajectories(payload)
                    trajectories = payload.get("trajectories", ())
                    if isinstance(trajectories, Sequence):
                        self._record_accepted_trajectories(trajectories)
                    break
                except BrokerBackpressure:
                    time.sleep(0.25)
                except BrokerStaleActor:
                    self._stale.set()
                    break
                except BrokerSessionChanged:
                    self._session_changed.set()
                    return
                except BrokerUnavailable:
                    time.sleep(1.0)
                except BaseException as exc:
                    self._thread_error.put(exc)
                    return

    def _raise_thread_error(self) -> None:
        try:
            error = self._thread_error.get_nowait()
        except queue.Empty:
            return
        raise RuntimeError(f"WAN actor background task failed: {type(error).__name__}: {error}") from error

    def _collect_trajectories(self) -> List[Any]:
        trajectories = []
        for manager in self.manager.agent_managers.values():
            while True:
                try:
                    trajectories.append(manager.trajectory_queue.get_nowait())
                except manager.trajectory_queue.Empty:
                    break
        return trajectories

    def run(self) -> None:
        while not self.stop.is_set():
            self._raise_thread_error()
            if self._session_changed.is_set():
                raise BrokerSessionChanged("central WAN trainer session changed")
            if self._state_changed.is_set() or self._stale.is_set():
                self._synchronize_state()
                continue

            local_steps = self.manager.get_steps()
            mapped_steps = _remap_completed_steps(self.manager, local_steps, self.worker_offset)
            self.manager.process_steps(mapped_steps)
            trajectories = self._collect_trajectories()
            if trajectories:
                # One actor generally emits one compact cohort batch. The 256-trajectory ceiling can
                # represent 32 environments with up to eight policy agents each (e.g. 4v4) while the
                # bounded queue still provides backpressure when Exeter is saturated.
                for start in range(0, len(trajectories), MAX_TRAJECTORIES_PER_UPLOAD):
                    payload = {
                        "session_id": self.session_id,
                        "actor_id": self.actor_id,
                        "control_epoch": self.control_epoch,
                        "policy_versions": dict(self.policy_versions),
                        "trajectories": trajectories[start : start + MAX_TRAJECTORIES_PER_UPLOAD],
                    }
                    while not self.stop.is_set():
                        try:
                            self._upload_queue.put(payload, timeout=0.5)
                            break
                        except queue.Full:
                            self._raise_thread_error()
                            if self._session_changed.is_set() or self._state_changed.is_set():
                                break


def _parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Run a persistent WAN Bees rollout actor with local policy inference."
    )
    parser.add_argument("--actor-id", required=True, type=int)
    parser.add_argument("--ssh", required=True, help="SSH target for Exeter, e.g. user@exeter.")
    parser.add_argument("--env", required=True, help="Path to the matching Bees training build.")
    parser.add_argument("--auth-token-file", required=True)
    parser.add_argument("--broker-port", type=int, default=wan.DEFAULT_BROKER_PORT)
    parser.add_argument("--local-port", type=int, default=wan.DEFAULT_BROKER_PORT)
    parser.add_argument("--local-base-port", type=int, default=5005)
    parser.add_argument("--ssh-executable", default="ssh")
    parser.add_argument("--ssh-option", action="append", default=[])
    parser.add_argument("--torch-device", default="cpu")
    parser.add_argument("--graphics", action="store_true")
    parser.add_argument("--reconnect-seconds", type=float, default=DEFAULT_RECONNECT_SECONDS)
    parser.add_argument("--upload-queue", type=int, default=DEFAULT_LOCAL_UPLOAD_QUEUE)
    parser.add_argument("--tunnel-startup-seconds", type=float, default=1.0)
    return parser


def main(argv: Optional[Sequence[str]] = None) -> int:
    args = _parser().parse_args(argv)
    if args.actor_id < 0:
        print("error: --actor-id must be non-negative", file=sys.stderr)
        return 2
    if not 1 <= args.broker_port <= 65535 or not 1 <= args.local_port <= 65535:
        print("error: broker/local ports must be in 1-65535", file=sys.stderr)
        return 2
    if not 1 <= args.local_base_port <= 65535:
        print("error: --local-base-port must be in 1-65535", file=sys.stderr)
        return 2
    if args.reconnect_seconds <= 0 or args.upload_queue <= 0 or args.tunnel_startup_seconds < 0:
        print("error: reconnect/upload/tunnel values are outside valid bounds", file=sys.stderr)
        return 2

    env_path = Path(args.env).expanduser().resolve()
    if not env_path.is_file():
        print(f"error: Unity training executable does not exist: {env_path}", file=sys.stderr)
        return 2
    try:
        token = wan.load_auth_token(args.auth_token_file)
    except ValueError as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 2

    stop = threading.Event()

    def request_stop(_signum, _frame):
        stop.set()

    old_sigint = signal.signal(signal.SIGINT, request_stop)
    old_sigterm = signal.signal(signal.SIGTERM, request_stop)
    tunnel: Optional[subprocess.Popen] = None
    try:
        tunnel = subprocess.Popen(
            ssh_command(
                args.ssh_executable,
                args.ssh,
                local_port=args.local_port,
                broker_port=args.broker_port,
                options=args.ssh_option,
            ),
            stdin=subprocess.DEVNULL,
        )
        deadline = time.monotonic() + args.tunnel_startup_seconds
        while time.monotonic() < deadline and tunnel.poll() is None and not stop.is_set():
            time.sleep(0.05)
        if tunnel.poll() is not None:
            print(f"error: SSH tunnel exited during startup ({tunnel.returncode}).", file=sys.stderr)
            return 3

        client = BrokerClient("127.0.0.1", args.local_port, token)
        while not stop.is_set():
            if tunnel.poll() is not None:
                print(f"error: SSH tunnel exited ({tunnel.returncode}).", file=sys.stderr)
                return 4
            try:
                session = _wait_for_broker(client, stop, args.reconnect_seconds)
                actor_session = ActorSession(
                    client,
                    session,
                    actor_id=args.actor_id,
                    env_path=env_path,
                    local_base_port=args.local_base_port,
                    torch_device=args.torch_device,
                    graphics=args.graphics,
                    stop=stop,
                    upload_queue_size=args.upload_queue,
                )
                try:
                    actor_session.start()
                    actor_session.run()
                finally:
                    actor_session.close()
            except BrokerSessionChanged:
                print("[Bees WAN actor] central generation changed; reconnecting to the next trainer session.")
                stop.wait(0.25)
            except BrokerUnavailable as exc:
                print(f"[Bees WAN actor] central trainer unavailable: {exc}")
                stop.wait(args.reconnect_seconds)
            except KeyboardInterrupt:
                stop.set()
            except Exception as exc:
                print(
                    f"[Bees WAN actor] session failed: {type(exc).__name__}: {exc}; reconnecting.",
                    file=sys.stderr,
                )
                stop.wait(args.reconnect_seconds)
        return 0
    finally:
        _terminate(tunnel)
        signal.signal(signal.SIGINT, old_sigint)
        signal.signal(signal.SIGTERM, old_sigterm)


if __name__ == "__main__":
    raise SystemExit(main())
