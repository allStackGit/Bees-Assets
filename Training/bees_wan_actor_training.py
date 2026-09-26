"""WAN-friendly actor/learner transport for Bees ML-Agents 1.1.0 training.

The central ML-Agents process remains the sole PPO optimizer/checkpoint owner. Remote actor machines
run Unity and policy inference locally, assemble native ML-Agents ``Trajectory`` objects locally,
and upload only complete, policy-versioned trajectories through an authenticated loopback broker.
The broker is intended to be reached through SSH local forwarding; it never binds a public socket.

This deliberately preserves PPO's on-policy contract:

* every central policy change advances an immutable policy version and drops queued older batches;
* actors tag each batch with the exact policy-version map and control epoch used to generate it;
* the broker rejects stale batches instead of replaying them under a newer policy;
* actors discard partial local trajectories before switching policy versions;
* Exeter alone owns optimizer state, updates, checkpoints and candidate registration.

The existing direct gRPC distributed mode remains separate and unchanged. This module is an
alternative transport for high-latency/WAN workers.
"""

from __future__ import annotations

import hashlib
import hmac
import http.server
import json
import pickle
import queue
from collections import OrderedDict
import secrets
import threading
import time
import urllib.parse
import zlib
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Dict, List, Mapping, Optional, Sequence, Tuple


EXPECTED_MLAGENTS_VERSION = "1.1.0"
WAN_ACTORS_FLAG = "--bees-wan-actors"
WAN_ENVS_PER_ACTOR_FLAG = "--bees-wan-envs-per-actor"
WAN_MIN_ACTORS_FLAG = "--bees-wan-min-actors"
WAN_BROKER_PORT_FLAG = "--bees-wan-broker-port"
WAN_AUTH_TOKEN_FILE_FLAG = "--bees-wan-auth-token-file"
WAN_MAX_QUEUED_BATCHES_FLAG = "--bees-wan-max-queued-batches"
WAN_PROTOCOL_VERSION = 1
DEFAULT_BROKER_PORT = 55051
DEFAULT_MIN_ACTORS = 1
DEFAULT_MAX_QUEUED_BATCHES = 32
MAX_COMPRESSED_PAYLOAD_BYTES = 512 * 1024 * 1024
MAX_DECOMPRESSED_PAYLOAD_BYTES = 1024 * 1024 * 1024
MIN_AUTH_TOKEN_CHARS = 32


@dataclass(frozen=True)
class WanActorOptions:
    actor_count: int = 0
    envs_per_actor: int = 0
    min_actors: int = DEFAULT_MIN_ACTORS
    broker_port: int = DEFAULT_BROKER_PORT
    auth_token_file: Optional[str] = None
    max_queued_batches: int = DEFAULT_MAX_QUEUED_BATCHES

    @property
    def enabled(self) -> bool:
        return self.actor_count > 0

    @property
    def total_envs(self) -> int:
        return self.actor_count * self.envs_per_actor


def _read_value(argv: Sequence[str], index: int, flag: str) -> Tuple[Optional[str], int]:
    argument = argv[index]
    if argument == flag:
        if index + 1 >= len(argv) or argv[index + 1].startswith("--"):
            raise SystemExit(f"{flag} requires a value.")
        return argv[index + 1], index + 2
    prefix = flag + "="
    if argument.startswith(prefix):
        value = argument[len(prefix) :]
        if not value:
            raise SystemExit(f"{flag} requires a value.")
        return value, index + 1
    return None, index


def _positive_int(value: str, flag: str, *, maximum: Optional[int] = None) -> int:
    try:
        parsed = int(value)
    except ValueError as exc:
        raise SystemExit(f"{flag} requires a positive whole number; got {value!r}.") from exc
    if parsed <= 0 or (maximum is not None and parsed > maximum):
        suffix = f" up to {maximum}" if maximum is not None else ""
        raise SystemExit(f"{flag} requires a positive whole number{suffix}; got {value!r}.")
    return parsed


def extract_wan_actor_options(argv: Sequence[str]) -> Tuple[List[str], WanActorOptions]:
    """Strip Bees WAN options while leaving ordinary ML-Agents arguments untouched."""
    cleaned: List[str] = []
    values: Dict[str, Any] = {
        "actor_count": 0,
        "envs_per_actor": 0,
        "min_actors": DEFAULT_MIN_ACTORS,
        "broker_port": DEFAULT_BROKER_PORT,
        "auth_token_file": None,
        "max_queued_batches": DEFAULT_MAX_QUEUED_BATCHES,
    }
    seen = set()
    specs = (
        (WAN_ACTORS_FLAG, "actor_count", lambda value: _positive_int(value, WAN_ACTORS_FLAG)),
        (
            WAN_ENVS_PER_ACTOR_FLAG,
            "envs_per_actor",
            lambda value: _positive_int(value, WAN_ENVS_PER_ACTOR_FLAG),
        ),
        (
            WAN_MIN_ACTORS_FLAG,
            "min_actors",
            lambda value: _positive_int(value, WAN_MIN_ACTORS_FLAG),
        ),
        (
            WAN_BROKER_PORT_FLAG,
            "broker_port",
            lambda value: _positive_int(value, WAN_BROKER_PORT_FLAG, maximum=65535),
        ),
        (WAN_AUTH_TOKEN_FILE_FLAG, "auth_token_file", str),
        (
            WAN_MAX_QUEUED_BATCHES_FLAG,
            "max_queued_batches",
            lambda value: _positive_int(value, WAN_MAX_QUEUED_BATCHES_FLAG),
        ),
    )

    index = 0
    while index < len(argv):
        matched = False
        for flag, key, converter in specs:
            raw, next_index = _read_value(argv, index, flag)
            if raw is None:
                continue
            if flag in seen:
                raise SystemExit(f"{flag} may be specified only once.")
            seen.add(flag)
            values[key] = converter(raw)
            index = next_index
            matched = True
            break
        if matched:
            continue
        cleaned.append(argv[index])
        index += 1

    options = WanActorOptions(**values)
    if not options.enabled:
        if seen:
            raise SystemExit(
                f"{WAN_ACTORS_FLAG} is required when any other --bees-wan-* option is used."
            )
        return cleaned, options

    if options.envs_per_actor <= 0:
        raise SystemExit(f"{WAN_ENVS_PER_ACTOR_FLAG} is required when {WAN_ACTORS_FLAG} is enabled.")
    if not options.auth_token_file:
        raise SystemExit(f"{WAN_AUTH_TOKEN_FILE_FLAG} is required when {WAN_ACTORS_FLAG} is enabled.")
    if options.min_actors > options.actor_count:
        raise SystemExit(
            f"{WAN_MIN_ACTORS_FLAG}={options.min_actors} exceeds {WAN_ACTORS_FLAG}={options.actor_count}."
        )
    return cleaned, options


def actor_worker_ids(options: WanActorOptions, actor_id: int) -> Tuple[int, ...]:
    if not options.enabled:
        raise ValueError("WAN actor topology is disabled")
    if not isinstance(actor_id, int) or isinstance(actor_id, bool) or not 0 <= actor_id < options.actor_count:
        raise ValueError(f"actor_id must be in [0,{options.actor_count - 1}]")
    start = actor_id * options.envs_per_actor
    return tuple(range(start, start + options.envs_per_actor))


def load_auth_token(path: str | Path) -> str:
    source = Path(path).expanduser().resolve()
    try:
        token = source.read_text(encoding="utf-8").strip()
    except OSError as exc:
        raise ValueError(f"WAN actor authentication token file is unreadable: {source}: {exc}") from exc
    if len(token) < MIN_AUTH_TOKEN_CHARS or len(token) > 1024 or any(ch.isspace() for ch in token):
        raise ValueError(
            f"WAN actor authentication token must be {MIN_AUTH_TOKEN_CHARS}-1024 non-whitespace characters."
        )
    return token


def encode_payload(value: Any) -> bytes:
    raw = pickle.dumps(value, protocol=pickle.HIGHEST_PROTOCOL)
    if len(raw) > MAX_DECOMPRESSED_PAYLOAD_BYTES:
        raise ValueError("WAN actor payload exceeds decompressed size limit")
    encoded = zlib.compress(raw, level=1)
    if len(encoded) > MAX_COMPRESSED_PAYLOAD_BYTES:
        raise ValueError("WAN actor payload exceeds compressed size limit")
    return encoded


def decode_payload(data: bytes) -> Any:
    if not isinstance(data, (bytes, bytearray)) or len(data) > MAX_COMPRESSED_PAYLOAD_BYTES:
        raise ValueError("WAN actor payload exceeds compressed size limit")
    decompressor = zlib.decompressobj()
    raw = decompressor.decompress(bytes(data), MAX_DECOMPRESSED_PAYLOAD_BYTES + 1)
    if len(raw) > MAX_DECOMPRESSED_PAYLOAD_BYTES:
        raise ValueError("WAN actor payload exceeds decompressed size limit")
    if decompressor.unconsumed_tail:
        raise ValueError("WAN actor payload exceeds decompressed size limit")
    raw += decompressor.flush()
    if len(raw) > MAX_DECOMPRESSED_PAYLOAD_BYTES:
        raise ValueError("WAN actor payload exceeds decompressed size limit")
    return pickle.loads(raw)


def _behavior_spec_signature(spec: Any) -> Tuple[Any, ...]:
    observations = []
    for observation in spec.observation_specs:
        observations.append(
            (
                tuple(int(value) for value in observation.shape),
                tuple(int(value) for value in getattr(observation, "dimension_property", ())),
                str(getattr(observation, "observation_type", "")),
            )
        )
    action_spec = spec.action_spec
    return (
        tuple(observations),
        int(action_spec.continuous_size),
        tuple(int(value) for value in action_spec.discrete_branches),
    )


def _policy_wire_payload(policy: Any) -> Mapping[str, Any]:
    """Produce a portable inference snapshot without serializing optimizer/checkpoint ownership."""
    from mlagents.trainers.policy.torch_policy import TorchPolicy

    if isinstance(policy, TorchPolicy):
        return {
            "kind": "torch",
            "weights": policy.get_weights(),
            "step": int(policy.get_current_step()),
        }

    engine = getattr(policy, "_engine", None)
    model_path = getattr(engine, "path", None)
    if model_path is not None:
        source = Path(model_path)
        if source.is_file():
            model_bytes = source.read_bytes()
            provider = None
            session = getattr(engine, "session", None)
            if session is not None:
                try:
                    providers = session.get_providers()
                    if providers:
                        provider = str(providers[0])
                except Exception:
                    provider = None
            return {
                "kind": "frozen_onnx",
                "model_bytes": model_bytes,
                "model_sha256": hashlib.sha256(model_bytes).hexdigest(),
                "provider": provider,
            }

    raise RuntimeError(
        "WAN actor mode encountered an unsupported policy type "
        f"{type(policy).__module__}.{type(policy).__qualname__}; refusing to generate unverified actions."
    )


def _policy_identity_digest(wire: Mapping[str, Any]) -> str:
    """Hash inference semantics, not Python pickle bookkeeping, for policy versioning."""
    kind = wire.get("kind")
    digest = hashlib.sha256(str(kind).encode("utf-8"))
    if kind == "frozen_onnx":
        model_hash = wire.get("model_sha256")
        if not isinstance(model_hash, str):
            raise RuntimeError("Frozen ONNX policy is missing its model SHA-256")
        digest.update(model_hash.encode("ascii"))
        digest.update(str(wire.get("provider") or "").encode("utf-8"))
        return digest.hexdigest()
    if kind != "torch":
        raise RuntimeError(f"Unsupported policy wire kind {kind!r}")
    weights = wire.get("weights")
    if not isinstance(weights, Mapping):
        raise RuntimeError("Torch policy snapshot is missing its state dictionary")
    for name in sorted(weights):
        value = weights[name]
        digest.update(str(name).encode("utf-8"))
        if hasattr(value, "detach"):
            array = value.detach().cpu().contiguous().numpy()
            digest.update(str(array.dtype).encode("ascii"))
            digest.update(repr(tuple(array.shape)).encode("ascii"))
            digest.update(array.tobytes(order="C"))
        else:
            digest.update(pickle.dumps(value, protocol=pickle.HIGHEST_PROTOCOL))
    return digest.hexdigest()


@dataclass(frozen=True)
class _PolicySnapshot:
    version: int
    digest: str
    encoded: bytes


class WanActorBroker:
    """Authenticated loopback broker shared by one central trainer process."""

    def __init__(self, options: WanActorOptions, run_options: Any, auth_token: str):
        self.options = options
        self.run_options = run_options
        self.auth_token = auth_token
        self.session_id = secrets.token_hex(24)
        self._condition = threading.Condition()
        self._closed = False
        self._registrations: Dict[int, Mapping[str, Any]] = {}
        self._policy_snapshots: Dict[str, _PolicySnapshot] = {}
        self._policy_identities: Dict[str, str] = {}
        self._policy_epoch = 0
        self._control_epoch = 0
        self._control_record: Mapping[str, Any] = {
            "epoch": 0,
            "kind": "none",
            "config": None,
        }
        self._trajectory_batches: queue.Queue = queue.Queue(maxsize=options.max_queued_batches)
        self._cohort_blocked_actors = set()
        self._accepted_batch_ids: Dict[int, OrderedDict] = {}
        self._server: Optional[http.server.ThreadingHTTPServer] = None
        self._server_thread: Optional[threading.Thread] = None

    def _accepted_batch_count_locked(self, actor_id: int, batch_id: Optional[str]) -> Optional[int]:
        if not batch_id:
            return None
        accepted = self._accepted_batch_ids.get(actor_id)
        if accepted is None or batch_id not in accepted:
            return None
        accepted.move_to_end(batch_id)
        return accepted[batch_id]

    def _remember_accepted_batch_locked(
        self,
        actor_id: int,
        batch_id: Optional[str],
        trajectory_count: int,
    ) -> None:
        if not batch_id:
            return
        accepted = self._accepted_batch_ids.setdefault(actor_id, OrderedDict())
        accepted[batch_id] = trajectory_count
        accepted.move_to_end(batch_id)
        while len(accepted) > 256:
            accepted.popitem(last=False)

    def session_payload(self) -> Mapping[str, Any]:
        return {
            "protocol_version": WAN_PROTOCOL_VERSION,
            "mlagents_version": EXPECTED_MLAGENTS_VERSION,
            "session_id": self.session_id,
            "actor_count": self.options.actor_count,
            "envs_per_actor": self.options.envs_per_actor,
            "min_actors": self.options.min_actors,
            "run_id": str(self.run_options.checkpoint_settings.run_id),
            "run_options": self.run_options,
        }

    def _expected_behaviors_locked(self) -> set[str]:
        if not self._registrations:
            return set()
        first = next(iter(self._registrations.values()))
        return set(first["behavior_specs"])

    def _policy_versions_locked(self) -> Dict[str, int]:
        versions = {name: snapshot.version for name, snapshot in self._policy_snapshots.items()}
        expected = self._expected_behaviors_locked()
        # During initial trainer creation policies are published one behavior at a time. Never tell
        # actors that a partial policy set is usable; otherwise an unpublished team could run random
        # initialization for a few decisions.
        if expected and set(versions) != expected:
            return {}
        return versions

    @property
    def policy_versions(self) -> Dict[str, int]:
        with self._condition:
            return self._policy_versions_locked()

    @property
    def control_epoch(self) -> int:
        with self._condition:
            return self._control_epoch

    def start(self) -> None:
        if self._server is not None:
            return
        broker = self

        class Handler(http.server.BaseHTTPRequestHandler):
            server_version = "BeesWanActor/1"

            def log_message(self, _format: str, *_args: Any) -> None:
                return

            def _authorized(self) -> bool:
                supplied = self.headers.get("Authorization", "")
                expected = "Bearer " + broker.auth_token
                return hmac.compare_digest(supplied, expected)

            def _error(self, status: int, code: str, message: str) -> None:
                body = json.dumps({"error": code, "message": message}).encode("utf-8")
                self.send_response(status)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)

            def _binary(
                self,
                body: bytes,
                *,
                status: int = 200,
                headers: Optional[Mapping[str, str]] = None,
            ) -> None:
                self.send_response(status)
                self.send_header("Content-Type", "application/octet-stream")
                self.send_header("Content-Length", str(len(body)))
                for key, value in (headers or {}).items():
                    self.send_header(key, value)
                self.end_headers()
                if body:
                    self.wfile.write(body)

            def _json(self, value: Mapping[str, Any], *, status: int = 200) -> None:
                body = json.dumps(value, sort_keys=True).encode("utf-8")
                self.send_response(status)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)

            def _session_guard(self, query: Mapping[str, List[str]]) -> bool:
                session = query.get("session", [""])[0]
                if session != broker.session_id:
                    self._error(409, "session-changed", "WAN actor trainer session changed.")
                    return False
                return True

            def _read_payload(self) -> Any:
                try:
                    length = int(self.headers.get("Content-Length", "0"))
                except ValueError as exc:
                    raise ValueError("invalid Content-Length") from exc
                if length <= 0 or length > MAX_COMPRESSED_PAYLOAD_BYTES:
                    raise ValueError("payload length is outside WAN actor bounds")
                data = self.rfile.read(length)
                if len(data) != length:
                    raise ValueError("request body ended before Content-Length")
                # Pickle is intentionally behind both loopback/SSH and a bearer credential. Possession
                # of this trainer-only token is trusted-code access, not a public-client capability.
                return decode_payload(data)

            def do_GET(self) -> None:
                if not self._authorized():
                    self._error(401, "unauthorized", "WAN actor authentication failed.")
                    return
                parsed = urllib.parse.urlparse(self.path)
                query = urllib.parse.parse_qs(parsed.query)
                try:
                    if parsed.path == "/session":
                        self._binary(encode_payload(broker.session_payload()))
                        return
                    if not self._session_guard(query):
                        return
                    if parsed.path == "/state":
                        policy_epoch = int(query.get("policy_epoch", ["-1"])[0])
                        control_epoch = int(query.get("control_epoch", ["-1"])[0])
                        wait_seconds = min(30.0, max(0.0, float(query.get("wait", ["0"])[0])))
                        state = broker.wait_state(policy_epoch, control_epoch, wait_seconds)
                        self._json(state)
                        return
                    if parsed.path == "/policy":
                        behavior = query.get("behavior", [""])[0]
                        version = int(query.get("version", ["-1"])[0])
                        snapshot = broker.policy_snapshot(behavior)
                        if snapshot is None:
                            self._error(425, "policy-not-ready", "Central policy is not available yet.")
                            return
                        if version == snapshot.version:
                            self._binary(b"", status=204)
                            return
                        self._binary(
                            snapshot.encoded,
                            headers={
                                "X-Bees-Policy-Version": str(snapshot.version),
                                "X-Bees-Policy-Sha256": snapshot.digest,
                            },
                        )
                        return
                    if parsed.path == "/control":
                        epoch = int(query.get("epoch", ["-1"])[0])
                        record = broker.control_record()
                        if epoch == int(record["epoch"]):
                            self._binary(b"", status=204)
                        else:
                            self._binary(encode_payload(record))
                        return
                    self._error(404, "not-found", "Unknown WAN actor endpoint.")
                except (ValueError, RuntimeError) as exc:
                    self._error(400, "invalid-request", str(exc))

            def do_POST(self) -> None:
                if not self._authorized():
                    self._error(401, "unauthorized", "WAN actor authentication failed.")
                    return
                parsed = urllib.parse.urlparse(self.path)
                try:
                    payload = self._read_payload()
                    if not isinstance(payload, Mapping):
                        raise ValueError("WAN actor request payload must be a mapping")
                    if payload.get("session_id") != broker.session_id:
                        self._error(409, "session-changed", "WAN actor trainer session changed.")
                        return
                    if parsed.path == "/claim":
                        claim_actor = getattr(broker, "claim_actor", None)
                        if claim_actor is None:
                            self._error(404, "not-found", "Dynamic actor allocation is not enabled.")
                            return
                        actor_id = claim_actor(payload)
                        self._json({"status": "claimed", "actor_id": actor_id})
                        return
                    if parsed.path == "/register":
                        broker.register_actor(payload)
                        self._json({"status": "registered"})
                        return
                    if parsed.path == "/trajectories":
                        accepted = broker.submit_trajectory_batch(payload)
                        self._json({"status": "accepted", "trajectories": accepted})
                        return
                    if parsed.path == "/reset-ack":
                        broker.acknowledge_reset(payload)
                        self._json({"status": "acknowledged"})
                        return
                    self._error(404, "not-found", "Unknown WAN actor endpoint.")
                except StaleActorStateError as exc:
                    self._error(409, "stale-actor-state", str(exc))
                except queue.Full:
                    self._error(429, "backpressure", "Central learner trajectory queue is full.")
                except (ValueError, RuntimeError) as exc:
                    self._error(400, "invalid-request", str(exc))

        class Server(http.server.ThreadingHTTPServer):
            daemon_threads = True
            allow_reuse_address = True

        self._server = Server(("127.0.0.1", self.options.broker_port), Handler)
        self._server_thread = threading.Thread(
            target=self._server.serve_forever,
            name="bees-wan-actor-broker",
            daemon=True,
        )
        self._server_thread.start()
        print(
            f"[Bees WAN] actor broker listening only on 127.0.0.1:{self.options.broker_port}; "
            "remote machines must use the managed authenticated private forward."
        )

    def close(self) -> None:
        with self._condition:
            self._closed = True
            self._condition.notify_all()
        if self._server is not None:
            self._server.shutdown()
            self._server.server_close()
            self._server = None
        if self._server_thread is not None:
            self._server_thread.join(timeout=5.0)
            self._server_thread = None

    def _validate_actor_id(self, value: Any) -> int:
        if not isinstance(value, int) or isinstance(value, bool) or not 0 <= value < self.options.actor_count:
            raise ValueError(f"actor_id must be in [0,{self.options.actor_count - 1}]")
        return value

    def register_actor(self, payload: Mapping[str, Any]) -> None:
        actor_id = self._validate_actor_id(payload.get("actor_id"))
        control_epoch = payload.get("control_epoch")
        if control_epoch != self.control_epoch:
            raise StaleActorStateError(
                f"actor control epoch {control_epoch!r} != central epoch {self.control_epoch}"
            )
        behavior_specs = payload.get("behavior_specs")
        if not isinstance(behavior_specs, Mapping) or not behavior_specs:
            raise ValueError("actor registration requires non-empty behavior_specs")
        signatures = {str(name): _behavior_spec_signature(spec) for name, spec in behavior_specs.items()}

        with self._condition:
            if self._registrations:
                reference = next(iter(self._registrations.values()))["signatures"]
                if signatures != reference:
                    raise ValueError("actor behavior specifications differ from already registered workers")
            self._registrations[actor_id] = {
                "behavior_specs": dict(behavior_specs),
                "signatures": signatures,
                "registered_at": time.monotonic(),
            }
            self._condition.notify_all()

    def wait_for_minimum_registrations(self, timeout_seconds: float) -> None:
        deadline = time.monotonic() + timeout_seconds
        with self._condition:
            while len(self._registrations) < self.options.min_actors and not self._closed:
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    raise TimeoutError(
                        f"WAN actor startup timed out with {len(self._registrations)}/"
                        f"{self.options.min_actors} required actors registered."
                    )
                self._condition.wait(min(remaining, 1.0))
            if self._closed:
                raise RuntimeError("WAN actor broker closed while waiting for registrations")

    def merged_behavior_specs(self) -> Mapping[str, Any]:
        with self._condition:
            if not self._registrations:
                raise RuntimeError("No WAN actors are registered")
            first = next(iter(self._registrations.values()))
            return dict(first["behavior_specs"])

    def registered_actor_ids(self) -> Tuple[int, ...]:
        with self._condition:
            return tuple(sorted(self._registrations))

    def publish_policy(self, behavior_name: str, policy: Any) -> int:
        wire = dict(_policy_wire_payload(policy))
        identity = _policy_identity_digest(wire)
        encoded = encode_payload(wire)
        transport_digest = hashlib.sha256(encoded).hexdigest()
        with self._condition:
            previous = self._policy_snapshots.get(behavior_name)
            if previous is not None and self._policy_identities.get(behavior_name) == identity:
                return previous.version
            version = 1 if previous is None else previous.version + 1
            self._policy_snapshots[behavior_name] = _PolicySnapshot(
                version,
                transport_digest,
                encoded,
            )
            self._policy_identities[behavior_name] = identity
            self._policy_epoch += 1
            self._discard_queued_batches_locked()
            self._condition.notify_all()
            return version

    def policy_snapshot(self, behavior_name: str) -> Optional[_PolicySnapshot]:
        with self._condition:
            return self._policy_snapshots.get(behavior_name)

    def _discard_queued_batches_locked(self) -> None:
        self._cohort_blocked_actors.clear()
        while True:
            try:
                self._trajectory_batches.get_nowait()
            except queue.Empty:
                return

    def initialize_control(self, config: Any) -> int:
        with self._condition:
            if self._control_epoch != 0:
                return self._control_epoch
            self._control_epoch = 1
            self._control_record = {"epoch": 1, "kind": "reset", "config": config}
            self._condition.notify_all()
            return self._control_epoch

    def request_reset(self, config: Any) -> int:
        with self._condition:
            self._control_epoch += 1
            self._control_record = {
                "epoch": self._control_epoch,
                "kind": "reset",
                "config": config,
            }
            self._discard_queued_batches_locked()
            self._condition.notify_all()
            return self._control_epoch

    def request_parameters(self, config: Any) -> int:
        with self._condition:
            self._control_epoch += 1
            self._control_record = {
                "epoch": self._control_epoch,
                "kind": "parameters",
                "config": config,
            }
            self._discard_queued_batches_locked()
            self._condition.notify_all()
            return self._control_epoch

    def control_record(self) -> Mapping[str, Any]:
        with self._condition:
            return dict(self._control_record)

    def wait_state(self, policy_epoch: int, control_epoch: int, wait_seconds: float) -> Mapping[str, Any]:
        deadline = time.monotonic() + wait_seconds
        with self._condition:
            while (
                not self._closed
                and policy_epoch == self._policy_epoch
                and control_epoch == self._control_epoch
                and wait_seconds > 0
            ):
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    break
                self._condition.wait(remaining)
            return {
                "session_id": self.session_id,
                "policy_epoch": self._policy_epoch,
                "control_epoch": self._control_epoch,
                "policy_versions": self._policy_versions_locked(),
                "registered_actors": sorted(self._registrations),
            }

    def acknowledge_reset(self, payload: Mapping[str, Any]) -> None:
        self._validate_actor_id(payload.get("actor_id"))
        if payload.get("control_epoch") != self.control_epoch:
            raise StaleActorStateError("reset acknowledgement is for a stale control epoch")
        # The current implementation does not block the learner on every actor acknowledgement.
        # Exact control epochs on trajectory uploads ensure stale actors cannot contribute until reset.

    def _validate_policy_versions(self, supplied: Any) -> None:
        expected = self.policy_versions
        if not expected:
            raise StaleActorStateError("central policy is not ready")
        if not isinstance(supplied, Mapping):
            raise StaleActorStateError("trajectory batch is missing policy_versions")
        normalized = {str(key): int(value) for key, value in supplied.items()}
        if normalized != expected:
            raise StaleActorStateError(
                f"trajectory policy versions {normalized} != current versions {expected}"
            )

    def submit_trajectory_batch(self, payload: Mapping[str, Any]) -> int:
        actor_id = self._validate_actor_id(payload.get("actor_id"))
        if actor_id not in self.registered_actor_ids():
            raise ValueError("actor must register behavior specs before uploading trajectories")
        batch_id = payload.get("batch_id")
        if batch_id is not None and (
            not isinstance(batch_id, str) or not batch_id or len(batch_id) > 64
        ):
            raise ValueError("trajectory batch_id must be a non-empty string up to 64 characters")
        with self._condition:
            duplicate_count = self._accepted_batch_count_locked(actor_id, batch_id)
        if duplicate_count is not None:
            return duplicate_count
        if payload.get("control_epoch") != self.control_epoch:
            raise StaleActorStateError(
                f"trajectory control epoch {payload.get('control_epoch')!r} != {self.control_epoch}"
            )
        self._validate_policy_versions(payload.get("policy_versions"))
        trajectories = payload.get("trajectories")
        if not isinstance(trajectories, list) or not trajectories:
            raise ValueError("trajectory batch must contain at least one trajectory")
        if len(trajectories) > 256:
            raise ValueError("trajectory batch may contain at most 256 trajectories")

        allowed_workers = set(actor_worker_ids(self.options, actor_id))
        allowed_behaviors = set(self.merged_behavior_specs())
        for trajectory in trajectories:
            behavior_id = getattr(trajectory, "behavior_id", None)
            agent_id = getattr(trajectory, "agent_id", None)
            steps = getattr(trajectory, "steps", None)
            if behavior_id not in allowed_behaviors:
                raise ValueError(f"trajectory uses unregistered behavior {behavior_id!r}")
            if not isinstance(agent_id, str) or not agent_id.startswith("agent_"):
                raise ValueError("trajectory has malformed global agent identity")
            prefix = agent_id[len("agent_") :].split("-", 1)[0]
            if not prefix.isdigit() or int(prefix) not in allowed_workers:
                raise ValueError(
                    f"trajectory agent {agent_id!r} is outside actor {actor_id}'s worker assignment"
                )
            if not isinstance(steps, list) or not steps:
                raise ValueError("trajectory must contain at least one experience step")

        item = {
            "actor_id": actor_id,
            "policy_versions": dict(payload["policy_versions"]),
            "control_epoch": int(payload["control_epoch"]),
            "trajectories": trajectories,
        }
        with self._condition:
            if actor_id in self._cohort_blocked_actors:
                raise queue.Full
            duplicate_count = self._accepted_batch_count_locked(actor_id, batch_id)
            if duplicate_count is not None:
                return duplicate_count
            if payload.get("control_epoch") != self._control_epoch:
                raise StaleActorStateError(
                    "trajectory control epoch changed while validating the batch"
                )
            self._validate_policy_versions(payload.get("policy_versions"))
            self._trajectory_batches.put_nowait(item)
            self._remember_accepted_batch_locked(actor_id, batch_id, len(trajectories))
        return len(trajectories)

    def _batch_is_current(self, batch: Mapping[str, Any]) -> bool:
        return (
            batch.get("control_epoch") == self.control_epoch
            and batch.get("policy_versions") == self.policy_versions
        )

    def next_trajectory_batch(self, timeout_seconds: float) -> Mapping[str, Any]:
        try:
            while True:
                batch = self._trajectory_batches.get(timeout=timeout_seconds)
                if self._batch_is_current(batch):
                    return batch
        except queue.Empty as exc:
            raise TimeoutError(
                f"No WAN actor trajectories arrived within {timeout_seconds:g} seconds."
            ) from exc

    def next_trajectory_cohort(self, timeout_seconds: float) -> Tuple[Mapping[str, Any], ...]:
        """Return current-policy batches from at least min_actors distinct actor machines.

        Actors already selected for a cohort receive HTTP backpressure until the learner consumes the
        cohort. This prevents one low-latency machine from filling the entire central queue while the
        configured minimum set of remote machines is still producing its first batch.
        """
        deadline = time.monotonic() + timeout_seconds
        with self._condition:
            self._cohort_blocked_actors.clear()
            required = min(self.options.min_actors, len(self._registrations))
            generation = (
                self._control_epoch,
                tuple(sorted(self._policy_versions_locked().items())),
            )
        if required <= 0:
            raise RuntimeError("WAN actor cohort requested before any actor registered")

        selected: List[Mapping[str, Any]] = []
        actors = set()
        while len(actors) < required:
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                with self._condition:
                    self._cohort_blocked_actors.clear()
                raise TimeoutError(
                    f"WAN actor cohort timed out with {len(actors)}/{required} distinct actors."
                )
            try:
                batch = self._trajectory_batches.get(timeout=remaining)
            except queue.Empty as exc:
                with self._condition:
                    self._cohort_blocked_actors.clear()
                raise TimeoutError(
                    f"WAN actor cohort timed out with {len(actors)}/{required} distinct actors."
                ) from exc

            with self._condition:
                current_generation = (
                    self._control_epoch,
                    tuple(sorted(self._policy_versions_locked().items())),
                )
                if current_generation != generation:
                    # A policy/control change invalidates batches already removed from the queue.
                    # Start a fresh cohort so the learner never mixes epochs.
                    selected.clear()
                    actors.clear()
                    self._cohort_blocked_actors.clear()
                    generation = current_generation
                    required = min(self.options.min_actors, len(self._registrations))
                if not self._batch_is_current(batch):
                    continue

                actor_id = int(batch["actor_id"])
                if actor_id in actors:
                    # This should be uncommon because selected actors are backpressured. If a second
                    # request raced the block, include it only after the distinct-actor requirement
                    # has been satisfied by a later batch rather than silently replacing another machine.
                    continue
                selected.append(batch)
                actors.add(actor_id)
                self._cohort_blocked_actors.add(actor_id)
                if len(actors) >= required:
                    return tuple(selected)

        return tuple(selected)


class StaleActorStateError(RuntimeError):
    pass


class WanActorEnvManagerMixin:
    """EnvManager implementation that injects remote native trajectories into trainer queues."""

    def _bees_wan_initialize(self, options: WanActorOptions, run_options: Any, n_env: int) -> None:
        from mlagents.trainers.env_manager import EnvManager

        EnvManager.__init__(self)
        if n_env != options.total_envs:
            raise RuntimeError(
                f"WAN actor topology requires --num-envs={options.total_envs} "
                f"({options.actor_count}x{options.envs_per_actor}), got {n_env}."
            )
        self._bees_wan_options = options
        self._bees_wan_run_options = run_options
        self._bees_wan_timeout = max(1.0, float(run_options.env_settings.timeout_wait))
        self._bees_wan_initial_reset = False
        self._bees_wan_broker = WanActorBroker(
            options,
            run_options,
            load_auth_token(options.auth_token_file or ""),
        )
        self._bees_wan_broker.start()

    def _process_step_infos(self, step_infos: List[Any]) -> int:
        # Remote actors already assembled these experiences into native Trajectory objects.
        # Synthetic EnvironmentSteps exist only so TrainerController discovers behavior IDs and
        # continues to run reset/curriculum/self-play checks.
        return len(step_infos)

    def _synthetic_steps(self) -> List[Any]:
        from mlagents.trainers.env_manager import EnvironmentStep

        behavior_names = tuple(self._bees_wan_broker.merged_behavior_specs())
        result = {name: (None, None) for name in behavior_names}
        actors = self._bees_wan_broker.registered_actor_ids()
        worker_id = actor_worker_ids(self._bees_wan_options, actors[0])[0]
        return [EnvironmentStep(result, worker_id, {}, {})]

    def _reset_env(self, config: Optional[Dict] = None) -> List[Any]:
        if not self._bees_wan_initial_reset:
            self._bees_wan_broker.initialize_control(config)
            self._bees_wan_broker.wait_for_minimum_registrations(self._bees_wan_timeout)
            self._bees_wan_initial_reset = True
        else:
            self._bees_wan_broker.request_reset(config)
        return self._synthetic_steps()

    def _step(self) -> List[Any]:
        from mlagents.trainers.env_manager import EnvironmentStep

        cohort = self._bees_wan_broker.next_trajectory_cohort(self._bees_wan_timeout)
        for batch in cohort:
            for trajectory in batch["trajectories"]:
                behavior_id = trajectory.behavior_id
                manager = self.agent_managers.get(behavior_id)
                if manager is None:
                    raise RuntimeError(
                        f"WAN actor uploaded trajectory for behavior {behavior_id!r} before trainer registration."
                    )
                if len(trajectory.steps) > manager._max_trajectory_length:
                    raise RuntimeError(
                        f"WAN actor trajectory length {len(trajectory.steps)} exceeds "
                        f"time_horizon {manager._max_trajectory_length} for {behavior_id}."
                    )
                manager.trajectory_queue.put(trajectory)
        # One synthetic environment tick is sufficient to make TrainerController advance the
        # trainer and check curriculum/self-play resets after this uploaded cohort.
        return [EnvironmentStep.empty(0)]

    def set_policy(self, brain_name: str, policy: Any) -> None:
        from mlagents.trainers.env_manager import EnvManager

        EnvManager.set_policy(self, brain_name, policy)
        version = self._bees_wan_broker.publish_policy(brain_name, policy)
        print(f"[Bees WAN] policy {brain_name} version={version}")

    def set_env_parameters(self, config: Optional[Dict] = None) -> None:
        if self._bees_wan_initial_reset:
            self._bees_wan_broker.request_parameters(config)

    def on_training_started(self, behavior_name: str, trainer_settings: Any) -> None:
        return None

    @property
    def training_behaviors(self) -> Dict[str, Any]:
        return dict(self._bees_wan_broker.merged_behavior_specs())

    def close(self) -> None:
        self._bees_wan_broker.close()


def install_wan_actor_env_manager(options: WanActorOptions):
    """Patch ML-Agents' manager construction for one WAN actor learner invocation."""
    if not options.enabled:
        return None

    import mlagents.trainers
    import mlagents.trainers.learn as learn
    from mlagents.trainers.env_manager import EnvManager

    actual = mlagents.trainers.__version__
    if actual != EXPECTED_MLAGENTS_VERSION:
        raise RuntimeError(
            f"WAN actor mode targets ML-Agents {EXPECTED_MLAGENTS_VERSION}, but {actual} is installed."
        )
    # Validate before the trainer binds ports or creates checkpoints.
    load_auth_token(options.auth_token_file or "")
    original = learn.SubprocessEnvManager

    class WanActorEnvManager(WanActorEnvManagerMixin, EnvManager):
        def __init__(self, _env_factory: Any, run_options: Any, n_env: int = 1):
            self._bees_wan_initialize(options, run_options, n_env)

    learn.SubprocessEnvManager = WanActorEnvManager
    return original


def restore_wan_actor_env_manager(original: Any) -> None:
    if original is None:
        return
    import mlagents.trainers.learn as learn

    learn.SubprocessEnvManager = original
