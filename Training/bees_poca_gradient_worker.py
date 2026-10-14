"""Optional CPU-only MA-POCA gradient executor for the authenticated Bees WAN broker.

This process never owns optimizer state, checkpoints, or Unity environments.
The central learner must opt in by submitting minibatch jobs; an idle gradient
worker does not change existing training behavior.
"""

from __future__ import annotations

import argparse
import json
import os
import signal
import sys
import threading
import time
import urllib.parse
from collections import OrderedDict
from collections.abc import Mapping
from typing import Any, Optional, Sequence

import bees_wan_actor_training as wan
from bees_distributed_poca import (
    compute_minibatch_gradients,
    create_cpu_gradient_optimizer,
    decode_cpu_minibatch,
)
from bees_wan_actor_worker import (
    BrokerClient,
    BrokerSessionChanged,
    BrokerUnavailable,
)


def run_gradient_worker(
    *, host: str, port: int, token: str, identity: str,
    cpu_threads: int, stop: threading.Event,
) -> None:
    client = BrokerClient(host, port, token, timeout=80.0)
    optimizer = None
    optimizer_behavior = None
    previous_session = None
    snapshot_cache: OrderedDict[str, Mapping[str, Any]] = OrderedDict()
    while not stop.is_set():
        try:
            session = client.session()
            if session.get("protocol_version") != wan.WAN_PROTOCOL_VERSION:
                raise RuntimeError("Incompatible Bees WAN broker protocol")
            if session.get("mlagents_version") != wan.EXPECTED_MLAGENTS_VERSION:
                raise RuntimeError("Incompatible ML-Agents version")
            session_id = str(session["session_id"])
            if session_id != previous_session:
                optimizer = None
                optimizer_behavior = None
                snapshot_cache.clear()
                previous_session = session_id
            status, _headers, body = client._request(
                "GET", "/gradient/claim",
                query={"session": session_id, "worker": identity, "wait": 10},
            )
            if status == 204:
                continue
            if status != 200:
                raise RuntimeError("Unexpected gradient claim response")
            job = wan.decode_payload(body)
            if not isinstance(job, Mapping):
                raise RuntimeError("Malformed gradient job")
            job_id = str(job.get("job_id", ""))
            claim_token = str(job.get("claim_token", ""))
            payload = job.get("payload")
            if not job_id or not claim_token or not isinstance(payload, Mapping):
                raise RuntimeError("Gradient job missing payload/identity")

            result: dict[str, Any]
            try:
                behavior_id = str(payload["behavior_id"])
                if optimizer is None or optimizer_behavior != behavior_id:
                    optimizer = create_cpu_gradient_optimizer(
                        behavior_id,
                        payload["behavior_spec"],
                        session["run_options"],
                        cpu_threads,
                    )
                    optimizer_behavior = behavior_id
                snapshot_id = str(payload["snapshot_id"])
                snapshot_cache_hit = snapshot_id in snapshot_cache
                fetch_started = time.monotonic()
                if not snapshot_cache_hit:
                    snapshot_status, _, snapshot_blob = client._request(
                        "GET", "/gradient/snapshot",
                        query={"session": session_id, "id": snapshot_id},
                    )
                    if snapshot_status != 200:
                        raise RuntimeError("Unexpected gradient snapshot response")
                    decoded_snapshot = wan.decode_payload(snapshot_blob)
                    if not isinstance(decoded_snapshot, Mapping):
                        raise RuntimeError("Malformed remote gradient snapshot")
                    snapshot_cache[snapshot_id] = decoded_snapshot
                    while len(snapshot_cache) > 4:
                        snapshot_cache.popitem(last=False)
                snapshot = snapshot_cache[snapshot_id]
                snapshot_cache.move_to_end(snapshot_id)
                snapshot_fetch_seconds = time.monotonic() - fetch_started
                result = compute_minibatch_gradients(
                    optimizer,
                    decode_cpu_minibatch(payload["minibatch_bytes"]),
                    snapshot,
                )
                result["worker"] = identity
                result["snapshot_cache_hit"] = snapshot_cache_hit
                result["snapshot_fetch_seconds"] = snapshot_fetch_seconds
            except Exception as exc:
                # Central can run the batch itself or cancel the job; a failed
                # remote compute must never poison Adam or kill the rollout.
                result = {
                    "worker": identity,
                    "error": f"{type(exc).__name__}: {exc}",
                }
            try:
                client._request(
                    "POST", "/gradient/result",
                    payload={
                        "session_id": session_id,
                        "worker": identity,
                        "job_id": job_id,
                        "claim_token": claim_token,
                        "result": result,
                    },
                )
            except RuntimeError as exc:
                if "gradient-job-stale" not in str(exc):
                    raise
            print(
                "[Bees CPU gradient] job=" + job_id[:12] +
                " worker=" + identity + " " +
                ("failed=" + result["error"] if "error" in result else
                 "batch=" + str(result["batch_experiences"]) +
                 " compute_s=" + str(round(result["compute_seconds"], 3))),
                flush=True,
            )
        except BrokerSessionChanged:
            optimizer = None
            optimizer_behavior = None
            snapshot_cache.clear()
            previous_session = None
        except (BrokerUnavailable, TimeoutError, ConnectionError, OSError) as exc:
            print("[Bees CPU gradient] central unavailable: " + str(exc), flush=True)
            stop.wait(3.0)


def main(argv: Optional[Sequence[str]] = None) -> int:
    parser = argparse.ArgumentParser(description="Run one Bees CPU PPO gradient executor")
    parser.add_argument("--broker-host", default="127.0.0.1")
    parser.add_argument("--broker-port", type=int, default=55051)
    parser.add_argument("--auth-token-file", required=True)
    parser.add_argument("--worker-id", required=True)
    parser.add_argument("--learning-cpus", type=int, required=True)
    args = parser.parse_args(argv)
    if not (1 <= args.learning_cpus <= (os.cpu_count() or 1)):
        parser.error("--learning-cpus must be 1..number of logical CPUs")
    if not (1 <= args.broker_port <= 65535):
        parser.error("--broker-port must be 1..65535")
    token = wan.load_auth_token(args.auth_token_file)
    stop = threading.Event()
    for name in ("SIGINT", "SIGTERM"):
        sig = getattr(signal, name, None)
        if sig is not None:
            signal.signal(sig, lambda *_: stop.set())
    run_gradient_worker(
        host=args.broker_host,
        port=args.broker_port,
        token=token,
        identity=args.worker_id,
        cpu_threads=args.learning_cpus,
        stop=stop,
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
