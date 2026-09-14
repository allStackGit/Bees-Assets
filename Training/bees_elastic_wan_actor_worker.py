"""Run one elastic Bees WAN rollout actor.

Each remote machine chooses its own ``--envs`` count from 1 through 64. Actor IDs are stable slots
0 through 11 by default; the central trainer reserves 64 global worker IDs per slot, so changing one
machine's environment count never renumbers another actor. The underlying actor session performs
Unity simulation and policy inference locally and never owns PPO optimizer/checkpoint state.
"""

from __future__ import annotations

import argparse
import signal
import subprocess
import sys
import threading
import time
from pathlib import Path
from typing import Any, Mapping, Optional, Sequence

import bees_elastic_wan_training as elastic
import bees_wan_actor_training as wan
import bees_wan_actor_worker as worker


class ElasticBrokerClient(worker.BrokerClient):
    def __init__(self, *args: Any, actor_id: int, env_count: int, **kwargs: Any):
        super().__init__(*args, **kwargs)
        self.actor_id = actor_id
        self.env_count = env_count

    def register(self, payload: Mapping[str, Any]) -> None:
        enriched = dict(payload)
        enriched["env_count"] = self.env_count
        super().register(enriched)


def _parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Run a persistent elastic WAN Bees rollout actor with local policy inference."
    )
    parser.add_argument("--actor-id", required=True, type=int, help="Stable remote slot, normally 0-11.")
    parser.add_argument(
        "--envs",
        type=int,
        default=32,
        help="Local Unity environment count for this machine (1-64; default 32).",
    )
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
    parser.add_argument("--reconnect-seconds", type=float, default=worker.DEFAULT_RECONNECT_SECONDS)
    parser.add_argument("--upload-queue", type=int, default=worker.DEFAULT_LOCAL_UPLOAD_QUEUE)
    parser.add_argument("--tunnel-startup-seconds", type=float, default=1.0)
    return parser


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
    if not isinstance(worker_base, int) or worker_base < 1:
        raise RuntimeError("Elastic WAN session has an invalid remote_worker_base")
    if not isinstance(capacity_envs, int) or capacity_envs <= worker_base:
        raise RuntimeError("Elastic WAN session has an invalid capacity_envs value")
    if not 0 <= actor_id < max_actors:
        raise RuntimeError(f"actor id {actor_id} is outside central slot count {max_actors}")
    if not 1 <= env_count <= max_envs:
        raise RuntimeError(f"--envs must be between 1 and {max_envs}")

    # ActorSession's transport/inference machinery predates elastic env counts. Supply compatible
    # per-session values, then override the global worker offset below with the fixed 64-ID slot.
    compatible = dict(session)
    compatible["actor_count"] = max_actors
    compatible["envs_per_actor"] = env_count
    return compatible, worker_base + actor_id * stride, capacity_envs


def main(argv: Optional[Sequence[str]] = None) -> int:
    args = _parser().parse_args(argv)
    if args.actor_id < 0:
        print("error: --actor-id must be non-negative", file=sys.stderr)
        return 2
    if not 1 <= args.envs <= elastic.MAX_ENVS_PER_ACTOR:
        print(f"error: --envs must be in 1-{elastic.MAX_ENVS_PER_ACTOR}", file=sys.stderr)
        return 2
    if not 1 <= args.broker_port <= 65535 or not 1 <= args.local_port <= 65535:
        print("error: broker/local ports must be in 1-65535", file=sys.stderr)
        return 2
    if not 1 <= args.local_base_port <= 65535:
        print("error: --local-base-port must be in 1-65535", file=sys.stderr)
        return 2
    if args.local_base_port + args.envs - 1 > 65535:
        print("error: local ML-Agents worker ports would exceed 65535", file=sys.stderr)
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

    def request_stop(_signum: int, _frame: Any) -> None:
        stop.set()

    old_sigint = signal.signal(signal.SIGINT, request_stop)
    old_sigterm = signal.signal(signal.SIGTERM, request_stop)
    tunnel: Optional[subprocess.Popen] = None
    try:
        tunnel = subprocess.Popen(
            worker.ssh_command(
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

        client = ElasticBrokerClient(
            "127.0.0.1",
            args.local_port,
            token,
            actor_id=args.actor_id,
            env_count=args.envs,
        )
        while not stop.is_set():
            if tunnel.poll() is not None:
                print(f"error: SSH tunnel exited ({tunnel.returncode}).", file=sys.stderr)
                return 4
            try:
                raw_session = worker._wait_for_broker(client, stop, args.reconnect_seconds)
                session, worker_offset, capacity_envs = _elastic_session(
                    raw_session,
                    actor_id=args.actor_id,
                    env_count=args.envs,
                )
                actor_session = worker.ActorSession(
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
                # Fixed slot assignment prevents worker-ID collisions when actors have different env counts.
                actor_session.worker_offset = worker_offset
                # Conservative horizon: short enough to remain useful even if every remote slot fills to 64 envs.
                actor_session.total_envs = capacity_envs
                try:
                    actor_session.start()
                    actor_session.run()
                finally:
                    actor_session.close()
            except worker.BrokerSessionChanged:
                print("[Bees WAN actor] central generation changed; reconnecting to the next trainer session.")
                stop.wait(0.25)
            except worker.BrokerUnavailable as exc:
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
        worker._terminate(tunnel)
        signal.signal(signal.SIGINT, old_sigint)
        signal.signal(signal.SIGTERM, old_sigterm)


if __name__ == "__main__":
    raise SystemExit(main())
