"""Elastic WAN actor/learner support for Bees ML-Agents 1.1.0.

Exeter keeps its ordinary local Unity environments while accepting zero to twelve WAN actor
machines. Each remote actor independently declares between one and sixty-four local environments.
Remote worker IDs live in fixed 64-ID slots so actors may join, leave, or change local environment
counts without renumbering another machine's trajectories.

The central ML-Agents process remains the only optimizer/checkpoint owner. Remote actors still use
the authenticated loopback/SSH transport and policy-version checks implemented by
``bees_wan_actor_training``; this module adds elastic registration, local+remote hybrid rollout
collection, stale-actor leases, and capacity diagnostics.
"""

from __future__ import annotations

import collections
import queue
import time
from dataclasses import dataclass
from typing import Any, Deque, Dict, List, Mapping, Optional, Sequence, Tuple

import bees_wan_actor_training as base


MAX_REMOTE_ACTORS = 12
MAX_ENVS_PER_ACTOR = 64
DEFAULT_MAX_REMOTE_ACTORS = 12
DEFAULT_MIN_REMOTE_ACTORS = 0
DEFAULT_ACTOR_LEASE_SECONDS = 120.0
DIAGNOSTIC_INTERVAL_SECONDS = 60.0
CAPACITY_SETTLE_SECONDS = 180.0
CAPACITY_BENEFICIAL_GAIN = 0.08
CAPACITY_SATURATION_GAIN = 0.03
TRAINING_RATE_WINDOW_SECONDS = 180.0
REMOTE_RATE_WINDOW_SECONDS = 60.0

WAN_ACTORS_FLAG = base.WAN_ACTORS_FLAG
WAN_MIN_ACTORS_FLAG = base.WAN_MIN_ACTORS_FLAG
WAN_BROKER_PORT_FLAG = base.WAN_BROKER_PORT_FLAG
WAN_AUTH_TOKEN_FILE_FLAG = base.WAN_AUTH_TOKEN_FILE_FLAG
WAN_MAX_QUEUED_BATCHES_FLAG = base.WAN_MAX_QUEUED_BATCHES_FLAG
WAN_LEASE_SECONDS_FLAG = "--bees-wan-actor-lease-seconds"


@dataclass(frozen=True)
class ElasticWanOptions:
    max_actors: int = 0
    min_actors: int = DEFAULT_MIN_REMOTE_ACTORS
    broker_port: int = base.DEFAULT_BROKER_PORT
    auth_token_file: Optional[str] = None
    max_queued_batches: int = base.DEFAULT_MAX_QUEUED_BATCHES
    actor_lease_seconds: float = DEFAULT_ACTOR_LEASE_SECONDS

    @property
    def enabled(self) -> bool:
        return self.max_actors > 0

    # Compatibility properties used by the shared authenticated broker implementation.
    @property
    def actor_count(self) -> int:
        return self.max_actors

    @property
    def envs_per_actor(self) -> int:
        return MAX_ENVS_PER_ACTOR

    @property
    def total_envs(self) -> int:
        return self.max_actors * MAX_ENVS_PER_ACTOR


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


def _whole_number(value: str, flag: str, *, minimum: int, maximum: Optional[int] = None) -> int:
    try:
        parsed = int(value)
    except ValueError as exc:
        raise SystemExit(f"{flag} requires a whole number; got {value!r}.") from exc
    if parsed < minimum or (maximum is not None and parsed > maximum):
        bounds = f">={minimum}" if maximum is None else f"between {minimum} and {maximum}"
        raise SystemExit(f"{flag} must be {bounds}; got {parsed}.")
    return parsed


def _positive_float(value: str, flag: str) -> float:
    try:
        parsed = float(value)
    except ValueError as exc:
        raise SystemExit(f"{flag} requires a positive number; got {value!r}.") from exc
    if parsed <= 0:
        raise SystemExit(f"{flag} requires a positive number; got {value!r}.")
    return parsed


def extract_elastic_wan_options(argv: Sequence[str]) -> Tuple[List[str], ElasticWanOptions]:
    """Strip elastic WAN flags while preserving ordinary ML-Agents arguments.

    ``--bees-wan-actors`` is a slot ceiling, not the number of machines that must be online.
    Zero remote machines is therefore a normal runtime state while Exeter's local environments
    continue training.
    """
    cleaned: List[str] = []
    values: Dict[str, Any] = {
        "max_actors": 0,
        "min_actors": DEFAULT_MIN_REMOTE_ACTORS,
        "broker_port": base.DEFAULT_BROKER_PORT,
        "auth_token_file": None,
        "max_queued_batches": base.DEFAULT_MAX_QUEUED_BATCHES,
        "actor_lease_seconds": DEFAULT_ACTOR_LEASE_SECONDS,
    }
    seen = set()
    specs = (
        (
            WAN_ACTORS_FLAG,
            "max_actors",
            lambda value: _whole_number(
                value, WAN_ACTORS_FLAG, minimum=1, maximum=MAX_REMOTE_ACTORS
            ),
        ),
        (
            WAN_MIN_ACTORS_FLAG,
            "min_actors",
            lambda value: _whole_number(
                value, WAN_MIN_ACTORS_FLAG, minimum=0, maximum=MAX_REMOTE_ACTORS
            ),
        ),
        (
            WAN_BROKER_PORT_FLAG,
            "broker_port",
            lambda value: _whole_number(value, WAN_BROKER_PORT_FLAG, minimum=1, maximum=65535),
        ),
        (WAN_AUTH_TOKEN_FILE_FLAG, "auth_token_file", str),
        (
            WAN_MAX_QUEUED_BATCHES_FLAG,
            "max_queued_batches",
            lambda value: _whole_number(value, WAN_MAX_QUEUED_BATCHES_FLAG, minimum=1),
        ),
        (WAN_LEASE_SECONDS_FLAG, "actor_lease_seconds", lambda value: _positive_float(value, WAN_LEASE_SECONDS_FLAG)),
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
        if argv[index] == base.WAN_ENVS_PER_ACTOR_FLAG or argv[index].startswith(
            base.WAN_ENVS_PER_ACTOR_FLAG + "="
        ):
            raise SystemExit(
                f"{base.WAN_ENVS_PER_ACTOR_FLAG} is obsolete in elastic WAN mode; "
                "set --envs=1..64 independently on each remote actor."
            )
        cleaned.append(argv[index])
        index += 1

    options = ElasticWanOptions(**values)
    if not options.enabled:
        if seen:
            raise SystemExit(
                f"{WAN_ACTORS_FLAG} is required when any other elastic WAN option is used."
            )
        return cleaned, options
    if not options.auth_token_file:
        raise SystemExit(f"{WAN_AUTH_TOKEN_FILE_FLAG} is required when {WAN_ACTORS_FLAG} is enabled.")
    if options.min_actors > options.max_actors:
        raise SystemExit(
            f"{WAN_MIN_ACTORS_FLAG}={options.min_actors} exceeds {WAN_ACTORS_FLAG}={options.max_actors}."
        )
    return cleaned, options


def actor_worker_ids(
    options: ElasticWanOptions,
    actor_id: int,
    env_count: int,
    remote_worker_base: int,
) -> Tuple[int, ...]:
    if not options.enabled:
        raise ValueError("elastic WAN actor topology is disabled")
    if not isinstance(actor_id, int) or isinstance(actor_id, bool) or not 0 <= actor_id < options.max_actors:
        raise ValueError(f"actor_id must be in [0,{options.max_actors - 1}]")
    if not isinstance(env_count, int) or isinstance(env_count, bool) or not 1 <= env_count <= MAX_ENVS_PER_ACTOR:
        raise ValueError(f"env_count must be in [1,{MAX_ENVS_PER_ACTOR}]")
    if not isinstance(remote_worker_base, int) or isinstance(remote_worker_base, bool) or remote_worker_base < 0:
        raise ValueError("remote_worker_base must be a non-negative integer")
    start = remote_worker_base + actor_id * MAX_ENVS_PER_ACTOR
    return tuple(range(start, start + env_count))


def classify_capacity(
    baseline_rate: Optional[float],
    current_rate: Optional[float],
    *,
    queue_ratio: float,
    backpressure_events: int,
) -> Tuple[str, Optional[float]]:
    if baseline_rate is None or current_rate is None or baseline_rate <= 0:
        return "warming-up", None
    gain = (current_rate - baseline_rate) / baseline_rate
    if gain >= CAPACITY_BENEFICIAL_GAIN:
        return "beneficial", gain
    if gain >= CAPACITY_SATURATION_GAIN:
        return "diminishing-returns", gain
    if queue_ratio >= 0.25 or backpressure_events > 0:
        return "exeter-saturated", gain
    return "no-measurable-gain", gain


class CapacityDiagnostics:
    def __init__(self, local_envs: int):
        self.local_envs = int(local_envs)
        self._trainer_samples: Deque[Tuple[float, int]] = collections.deque()
        self._remote_samples: Deque[Tuple[float, int]] = collections.deque()
        self._last_report = 0.0
        self._remote_actors = 0
        self._remote_envs = 0
        self._backpressure_total = 0
        self._backpressure_at_topology = 0
        self._pending: Optional[Dict[str, Any]] = None

    @staticmethod
    def _prune(samples: Deque[Tuple[float, int]], now: float, horizon: float) -> None:
        while len(samples) > 2 and samples[1][0] < now - horizon:
            samples.popleft()

    def trainer_rate(self, now: Optional[float] = None) -> Optional[float]:
        current = time.monotonic() if now is None else now
        self._prune(self._trainer_samples, current, TRAINING_RATE_WINDOW_SECONDS)
        if len(self._trainer_samples) < 2:
            return None
        first_time, first_step = self._trainer_samples[0]
        last_time, last_step = self._trainer_samples[-1]
        elapsed = last_time - first_time
        if elapsed <= 1.0 or last_step < first_step:
            return None
        return (last_step - first_step) / elapsed

    def remote_rate(self, now: Optional[float] = None) -> float:
        current = time.monotonic() if now is None else now
        self._prune(self._remote_samples, current, REMOTE_RATE_WINDOW_SECONDS)
        if not self._remote_samples:
            return 0.0
        oldest = self._remote_samples[0][0]
        elapsed = max(1.0, current - oldest)
        return sum(value for _, value in self._remote_samples) / elapsed

    def observe_trainer_step(self, step: int) -> None:
        if not isinstance(step, int) or isinstance(step, bool) or step < 0:
            return
        now = time.monotonic()
        if self._trainer_samples and step < self._trainer_samples[-1][1]:
            self._trainer_samples.clear()
        if not self._trainer_samples or step != self._trainer_samples[-1][1]:
            self._trainer_samples.append((now, step))
        self._prune(self._trainer_samples, now, TRAINING_RATE_WINDOW_SECONDS)

    def observe_remote_batch(self, step_count: int) -> None:
        if step_count <= 0:
            return
        now = time.monotonic()
        self._remote_samples.append((now, int(step_count)))
        self._prune(self._remote_samples, now, REMOTE_RATE_WINDOW_SECONDS)

    def observe_backpressure(self) -> None:
        self._backpressure_total += 1

    def topology_changed(self, actor_count: int, remote_envs: int) -> None:
        now = time.monotonic()
        previous_envs = self._remote_envs
        baseline = self.trainer_rate(now)
        self._remote_actors = actor_count
        self._remote_envs = remote_envs
        if remote_envs > previous_envs:
            self._pending = {
                "at": now,
                "from_envs": previous_envs,
                "to_envs": remote_envs,
                "baseline": baseline,
                "backpressure": self._backpressure_total,
                "reported": False,
            }
        elif remote_envs < previous_envs:
            self._pending = None
        print(
            f"[Bees WAN capacity] topology local_envs={self.local_envs} "
            f"remote_actors={actor_count} remote_envs={remote_envs} "
            f"total_envs={self.local_envs + remote_envs}."
        )

    def maybe_report(self, queue_size: int, queue_capacity: int) -> None:
        now = time.monotonic()
        if self._last_report and now - self._last_report < DIAGNOSTIC_INTERVAL_SECONDS:
            return
        self._last_report = now
        trainer_rate = self.trainer_rate(now)
        remote_rate = self.remote_rate(now)
        queue_ratio = queue_size / max(1, queue_capacity)
        print(
            "[Bees WAN capacity] "
            f"remote_actors={self._remote_actors} remote_envs={self._remote_envs} "
            f"trainer_steps_per_sec={(f'{trainer_rate:.1f}' if trainer_rate is not None else 'warming')} "
            f"remote_rollout_steps_per_sec={remote_rate:.1f} "
            f"queue={queue_size}/{queue_capacity} backpressure={self._backpressure_total}."
        )

        pending = self._pending
        if pending is None or pending["reported"] or now - pending["at"] < CAPACITY_SETTLE_SECONDS:
            return
        backpressure = self._backpressure_total - int(pending["backpressure"])
        status, gain = classify_capacity(
            pending["baseline"],
            trainer_rate,
            queue_ratio=queue_ratio,
            backpressure_events=backpressure,
        )
        gain_text = "unknown" if gain is None else f"{gain * 100.0:+.1f}%"
        print(
            "[Bees WAN capacity] marginal-test "
            f"remote_envs={pending['from_envs']}->{pending['to_envs']} "
            f"gain={gain_text} status={status}."
        )
        if status == "exeter-saturated":
            print(
                "[Bees WAN capacity] CAPACITY LIMIT LIKELY: the newest remote capacity did not "
                "materially increase trainer steps/sec while Exeter showed queued/backpressured "
                "rollouts. Additional workers are unlikely to improve training speed."
            )
        elif status == "no-measurable-gain":
            print(
                "[Bees WAN capacity] No measurable trainer-speed gain from the newest remote "
                "capacity, but Exeter is not showing sustained queue pressure; the added actor may "
                "be slow or the sample may be noisy rather than centrally saturated."
            )
        pending["reported"] = True


class ElasticWanBroker(base.WanActorBroker):
    def __init__(self, options: ElasticWanOptions, run_options: Any, auth_token: str, local_envs: int):
        super().__init__(options, run_options, auth_token)
        self.options = options
        self.local_envs = int(local_envs)
        self.remote_worker_base = self.local_envs
        self._reference_behavior_specs: Optional[Dict[str, Any]] = None
        self._reference_signatures: Optional[Dict[str, Any]] = None
        self._claims: Dict[str, Dict[str, Any]] = {}
        self._topology_epoch = 0
        self.diagnostics = CapacityDiagnostics(self.local_envs)

    def session_payload(self) -> Mapping[str, Any]:
        return {
            "protocol_version": base.WAN_PROTOCOL_VERSION,
            "mlagents_version": base.EXPECTED_MLAGENTS_VERSION,
            "session_id": self.session_id,
            "max_actors": self.options.max_actors,
            "max_envs_per_actor": MAX_ENVS_PER_ACTOR,
            "remote_worker_base": self.remote_worker_base,
            "worker_stride": MAX_ENVS_PER_ACTOR,
            "capacity_envs": self.local_envs + self.options.max_actors * MAX_ENVS_PER_ACTOR,
            # Legacy compatibility keys for shared actor-side helpers.
            "actor_count": self.options.max_actors,
            "envs_per_actor": MAX_ENVS_PER_ACTOR,
            "min_actors": self.options.min_actors,
            "run_id": str(self.run_options.checkpoint_settings.run_id),
            "run_options": self.run_options,
        }

    def _validate_actor_id(self, value: Any) -> int:
        if not isinstance(value, int) or isinstance(value, bool) or not 0 <= value < self.options.max_actors:
            raise ValueError(f"actor_id must be in [0,{self.options.max_actors - 1}]")
        return value

    def _active_snapshot_locked(self, *, now: Optional[float] = None) -> Dict[int, int]:
        current = time.monotonic() if now is None else now
        expired = [
            actor_id
            for actor_id, record in self._registrations.items()
            if current - float(record.get("last_seen", record.get("registered_at", current)))
            > self.options.actor_lease_seconds
        ]
        if expired:
            for actor_id in expired:
                self._registrations.pop(actor_id, None)
            self._topology_epoch += 1
            snapshot = {
                int(actor_id): int(record["env_count"])
                for actor_id, record in self._registrations.items()
            }
            self.diagnostics.topology_changed(len(snapshot), sum(snapshot.values()))
        return {
            int(actor_id): int(record["env_count"])
            for actor_id, record in self._registrations.items()
        }

    def active_actor_snapshot(self) -> Dict[int, int]:
        with self._condition:
            return self._active_snapshot_locked()

    def registered_actor_ids(self) -> Tuple[int, ...]:
        return tuple(sorted(self.active_actor_snapshot()))

    def set_reference_behavior_specs(self, behavior_specs: Mapping[str, Any]) -> None:
        if not behavior_specs:
            raise RuntimeError("Local Exeter environments expose no trainable behaviors")
        signatures = {
            str(name): base._behavior_spec_signature(spec)
            for name, spec in behavior_specs.items()
        }
        with self._condition:
            if self._reference_signatures is not None and signatures != self._reference_signatures:
                raise RuntimeError("Local behavior specifications changed during one WAN trainer session")
            self._reference_behavior_specs = dict(behavior_specs)
            self._reference_signatures = signatures

    def merged_behavior_specs(self) -> Mapping[str, Any]:
        with self._condition:
            if self._reference_behavior_specs is not None:
                return dict(self._reference_behavior_specs)
            snapshot = self._active_snapshot_locked()
            if not snapshot:
                raise RuntimeError("No local or remote behavior specifications are available")
            first = self._registrations[next(iter(snapshot))]
            return dict(first["behavior_specs"])

    def _expire_claims_locked(self, now: float) -> None:
        stale = [
            actor_key
            for actor_key, claim in self._claims.items()
            if now - float(claim.get("last_seen", now)) > self.options.actor_lease_seconds
        ]
        for actor_key in stale:
            self._claims.pop(actor_key, None)

    def claim_actor(self, payload: Mapping[str, Any]) -> int:
        actor_key = payload.get("actor_key")
        if not isinstance(actor_key, str) or not actor_key or len(actor_key) > 128:
            raise ValueError("actor_key must be a non-empty string up to 128 characters")
        env_count = payload.get("env_count")
        if not isinstance(env_count, int) or isinstance(env_count, bool) or not 1 <= env_count <= MAX_ENVS_PER_ACTOR:
            raise ValueError(f"actor env_count must be in [1,{MAX_ENVS_PER_ACTOR}]")

        now = time.monotonic()
        with self._condition:
            self._active_snapshot_locked(now=now)
            self._expire_claims_locked(now)

            for actor_id, record in self._registrations.items():
                if record.get("actor_key") == actor_key:
                    record["last_seen"] = now
                    return int(actor_id)

            existing = self._claims.get(actor_key)
            if existing is not None:
                existing["last_seen"] = now
                existing["env_count"] = env_count
                return int(existing["actor_id"])

            occupied = set(self._registrations)
            occupied.update(int(claim["actor_id"]) for claim in self._claims.values())
            actor_id = next(
                (candidate for candidate in range(self.options.max_actors) if candidate not in occupied),
                None,
            )
            if actor_id is None:
                raise RuntimeError(
                    f"all {self.options.max_actors} remote actor slots are currently in use"
                )
            self._claims[actor_key] = {
                "actor_id": actor_id,
                "env_count": env_count,
                "last_seen": now,
            }
            return actor_id

    def register_actor(self, payload: Mapping[str, Any]) -> None:
        actor_id = self._validate_actor_id(payload.get("actor_id"))
        env_count = payload.get("env_count")
        if not isinstance(env_count, int) or isinstance(env_count, bool) or not 1 <= env_count <= MAX_ENVS_PER_ACTOR:
            raise ValueError(f"actor env_count must be in [1,{MAX_ENVS_PER_ACTOR}]")
        if payload.get("control_epoch") != self.control_epoch:
            raise base.StaleActorStateError(
                f"actor control epoch {payload.get('control_epoch')!r} != central epoch {self.control_epoch}"
            )
        behavior_specs = payload.get("behavior_specs")
        if not isinstance(behavior_specs, Mapping) or not behavior_specs:
            raise ValueError("actor registration requires non-empty behavior_specs")
        signatures = {
            str(name): base._behavior_spec_signature(spec)
            for name, spec in behavior_specs.items()
        }
        actor_key = payload.get("actor_key")
        if actor_key is not None and (
            not isinstance(actor_key, str) or not actor_key or len(actor_key) > 128
        ):
            raise ValueError("actor_key must be a non-empty string up to 128 characters")

        now = time.monotonic()
        with self._condition:
            self._active_snapshot_locked(now=now)
            self._expire_claims_locked(now)
            if actor_key is not None:
                claim = self._claims.get(actor_key)
                previous = self._registrations.get(actor_id)
                owns_previous = previous is not None and previous.get("actor_key") == actor_key
                if claim is None and not owns_previous:
                    raise ValueError("actor has no active claim for the requested slot")
                if claim is not None and int(claim["actor_id"]) != actor_id:
                    raise ValueError("actor claim does not match requested slot")
                if previous is not None and previous.get("actor_key") not in (None, actor_key):
                    raise ValueError("actor slot is owned by another remote machine")
            reference = self._reference_signatures
            if reference is None and self._registrations:
                reference = next(iter(self._registrations.values()))["signatures"]
            if reference is not None and signatures != reference:
                raise ValueError("actor behavior specifications differ from Exeter training environments")
            previous = self._registrations.get(actor_id)
            changed = previous is None or int(previous.get("env_count", -1)) != env_count
            self._registrations[actor_id] = {
                "behavior_specs": dict(behavior_specs),
                "signatures": signatures,
                "env_count": env_count,
                "actor_key": actor_key,
                "registered_at": now,
                "last_seen": now,
            }
            if actor_key is not None:
                self._claims.pop(actor_key, None)
            if changed:
                self._topology_epoch += 1
                snapshot = self._active_snapshot_locked(now=now)
                self.diagnostics.topology_changed(len(snapshot), sum(snapshot.values()))
            self._condition.notify_all()

    def wait_for_minimum_registrations(self, timeout_seconds: float) -> None:
        if self.options.min_actors <= 0:
            return
        deadline = time.monotonic() + timeout_seconds
        with self._condition:
            while not self._closed:
                active = self._active_snapshot_locked()
                if len(active) >= self.options.min_actors:
                    return
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    raise TimeoutError(
                        f"WAN actor startup timed out with {len(active)}/{self.options.min_actors} "
                        "required remote actors registered."
                    )
                self._condition.wait(min(remaining, 1.0))
        raise RuntimeError("WAN actor broker closed while waiting for registrations")

    def wait_state(self, policy_epoch: int, control_epoch: int, wait_seconds: float) -> Mapping[str, Any]:
        deadline = time.monotonic() + wait_seconds
        with self._condition:
            initial_topology = self._topology_epoch
            while (
                not self._closed
                and policy_epoch == self._policy_epoch
                and control_epoch == self._control_epoch
                and initial_topology == self._topology_epoch
                and wait_seconds > 0
            ):
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    break
                self._condition.wait(remaining)
            active = self._active_snapshot_locked()
            return {
                "session_id": self.session_id,
                "policy_epoch": self._policy_epoch,
                "control_epoch": self._control_epoch,
                "topology_epoch": self._topology_epoch,
                "policy_versions": self._policy_versions_locked(),
                "registered_actors": sorted(active),
                "remote_envs": sum(active.values()),
                "local_envs": self.local_envs,
            }

    def submit_trajectory_batch(self, payload: Mapping[str, Any]) -> int:
        actor_id = self._validate_actor_id(payload.get("actor_id"))
        with self._condition:
            active = self._active_snapshot_locked()
            if actor_id not in active:
                raise ValueError("actor lease expired; re-register before uploading trajectories")
            self._registrations[actor_id]["last_seen"] = time.monotonic()
            env_count = int(self._registrations[actor_id]["env_count"])
        if payload.get("control_epoch") != self.control_epoch:
            raise base.StaleActorStateError(
                f"trajectory control epoch {payload.get('control_epoch')!r} != {self.control_epoch}"
            )
        self._validate_policy_versions(payload.get("policy_versions"))
        trajectories = payload.get("trajectories")
        if not isinstance(trajectories, list) or not trajectories:
            raise ValueError("trajectory batch must contain at least one trajectory")
        if len(trajectories) > 256:
            raise ValueError("trajectory batch may contain at most 256 trajectories")

        allowed_workers = set(
            actor_worker_ids(self.options, actor_id, env_count, self.remote_worker_base)
        )
        allowed_behaviors = set(self.merged_behavior_specs())
        step_count = 0
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
            step_count += len(steps)

        item = {
            "actor_id": actor_id,
            "policy_versions": dict(payload["policy_versions"]),
            "control_epoch": int(payload["control_epoch"]),
            "trajectories": trajectories,
            "step_count": step_count,
        }
        try:
            self._trajectory_batches.put_nowait(item)
        except queue.Full:
            self.diagnostics.observe_backpressure()
            raise
        self.diagnostics.observe_remote_batch(step_count)
        return len(trajectories)

    def drain_current_batches(self, limit: int) -> Tuple[Mapping[str, Any], ...]:
        selected: List[Mapping[str, Any]] = []
        for _ in range(max(0, int(limit))):
            try:
                batch = self._trajectory_batches.get_nowait()
            except queue.Empty:
                break
            if self._batch_is_current(batch):
                selected.append(batch)
        return tuple(selected)

    def publish_policy(self, behavior_name: str, policy: Any) -> int:
        return super().publish_policy(behavior_name, policy)

    def observe_trainer_step(self, step: int) -> None:
        self.diagnostics.observe_trainer_step(step)

    def report_capacity(self) -> None:
        with self._condition:
            active = self._active_snapshot_locked()
        # Keep diagnostics topology state in sync after lease expiry even if no new registration occurs.
        if len(active) != self.diagnostics._remote_actors or sum(active.values()) != self.diagnostics._remote_envs:
            self.diagnostics.topology_changed(len(active), sum(active.values()))
        self.diagnostics.maybe_report(
            self._trajectory_batches.qsize(),
            self.options.max_queued_batches,
        )


@dataclass(frozen=True)
class ElasticWanPatch:
    env_manager_class: Any
    trainer_controller_advance: Any


class ElasticWanEnvManagerMixin:
    def _bees_elastic_initialize(
        self,
        options: ElasticWanOptions,
        run_options: Any,
        n_env: int,
        env_factory: Any,
        local_manager_class: Any,
    ) -> None:
        from mlagents.trainers.env_manager import EnvManager

        EnvManager.__init__(self)
        if n_env <= 0:
            raise RuntimeError(
                "Elastic WAN training requires at least one local Exeter environment so training "
                "continues normally when zero remote actors are online."
            )
        self._bees_local_manager = local_manager_class(env_factory, run_options, n_env)
        self._bees_local_envs = n_env
        self._bees_wan_options = options
        self._bees_wan_timeout = max(1.0, float(run_options.env_settings.timeout_wait))
        self._bees_wan_initial_reset = False
        self._bees_wan_broker = ElasticWanBroker(
            options,
            run_options,
            base.load_auth_token(options.auth_token_file or ""),
            n_env,
        )
        self._bees_wan_broker.start()

    def set_agent_manager(self, brain_name: str, manager: Any) -> None:
        from mlagents.trainers.env_manager import EnvManager

        EnvManager.set_agent_manager(self, brain_name, manager)
        self._bees_local_manager.set_agent_manager(brain_name, manager)

    def set_policy(self, brain_name: str, policy: Any) -> None:
        from mlagents.trainers.env_manager import EnvManager

        EnvManager.set_policy(self, brain_name, policy)
        self._bees_local_manager.set_policy(brain_name, policy)
        version = self._bees_wan_broker.publish_policy(brain_name, policy)
        print(f"[Bees WAN] policy {brain_name} version={version}")

    def _reset_env(self, config: Optional[Dict] = None) -> List[Any]:
        local_steps = self._bees_local_manager._reset_env(config)
        if not self._bees_wan_initial_reset:
            self._bees_wan_broker.initialize_control(config)
            self._bees_wan_broker.wait_for_minimum_registrations(self._bees_wan_timeout)
            self._bees_wan_initial_reset = True
        else:
            self._bees_wan_broker.request_reset(config)
        return local_steps

    def _inject_remote_batches(self) -> None:
        active = self._bees_wan_broker.active_actor_snapshot()
        # At most two batches per live actor per Exeter environment advance. If producers exceed
        # this rate the bounded queue/backpressure metric correctly exposes Exeter saturation.
        batches = self._bees_wan_broker.drain_current_batches(max(1, len(active) * 2))
        for batch in batches:
            for trajectory in batch["trajectories"]:
                manager = self.agent_managers.get(trajectory.behavior_id)
                if manager is None:
                    raise RuntimeError(
                        f"WAN actor uploaded trajectory for behavior {trajectory.behavior_id!r} "
                        "before trainer registration."
                    )
                if len(trajectory.steps) > manager._max_trajectory_length:
                    raise RuntimeError(
                        f"WAN actor trajectory length {len(trajectory.steps)} exceeds "
                        f"time_horizon {manager._max_trajectory_length} for {trajectory.behavior_id}."
                    )
                manager.trajectory_queue.put(trajectory)

    def _step(self) -> List[Any]:
        local_steps = self._bees_local_manager._step()
        self._inject_remote_batches()
        self._bees_wan_broker.report_capacity()
        return local_steps

    def set_env_parameters(self, config: Optional[Dict] = None) -> None:
        self._bees_local_manager.set_env_parameters(config)
        if self._bees_wan_initial_reset:
            self._bees_wan_broker.request_parameters(config)

    def on_training_started(self, behavior_name: str, trainer_settings: Any) -> None:
        self._bees_local_manager.on_training_started(behavior_name, trainer_settings)

    @property
    def training_behaviors(self) -> Dict[str, Any]:
        specs = dict(self._bees_local_manager.training_behaviors)
        self._bees_wan_broker.set_reference_behavior_specs(specs)
        return specs

    def close(self) -> None:
        try:
            self._bees_wan_broker.close()
        finally:
            self._bees_local_manager.close()


def install_elastic_wan_env_manager(options: ElasticWanOptions) -> Optional[ElasticWanPatch]:
    if not options.enabled:
        return None

    import mlagents.trainers
    import mlagents.trainers.learn as learn
    from mlagents.trainers.env_manager import EnvManager
    from mlagents.trainers.trainer_controller import TrainerController

    actual = mlagents.trainers.__version__
    if actual != base.EXPECTED_MLAGENTS_VERSION:
        raise RuntimeError(
            f"Elastic WAN actor mode targets ML-Agents {base.EXPECTED_MLAGENTS_VERSION}, "
            f"but {actual} is installed."
        )
    base.load_auth_token(options.auth_token_file or "")
    original_manager = learn.SubprocessEnvManager
    original_advance = TrainerController.advance

    class ElasticWanEnvManager(ElasticWanEnvManagerMixin, EnvManager):
        def __init__(self, env_factory: Any, run_options: Any, n_env: int = 1):
            self._bees_elastic_initialize(
                options,
                run_options,
                n_env,
                env_factory,
                original_manager,
            )

    def monitored_advance(controller: Any, env_manager: Any) -> int:
        result = original_advance(controller, env_manager)
        broker = getattr(env_manager, "_bees_wan_broker", None)
        if broker is not None:
            steps = []
            for trainer in controller.trainers.values():
                try:
                    steps.append(int(trainer.get_step))
                except Exception:
                    continue
            if steps:
                broker.observe_trainer_step(max(steps))
        return result

    learn.SubprocessEnvManager = ElasticWanEnvManager
    TrainerController.advance = monitored_advance
    return ElasticWanPatch(original_manager, original_advance)


def restore_elastic_wan_env_manager(patch: Optional[ElasticWanPatch]) -> None:
    if patch is None:
        return
    import mlagents.trainers.learn as learn
    from mlagents.trainers.trainer_controller import TrainerController

    learn.SubprocessEnvManager = patch.env_manager_class
    TrainerController.advance = patch.trainer_controller_advance
