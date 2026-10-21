"""Supervise independent elastic WAN actors under one managed remote-worker lease.

The managed environment count is a machine total. Actor slots, Unity worker ports,
policy inference, upload queues, and child-health files are independent.
"""
from __future__ import annotations

import hashlib
import json
import os
import signal
import sys
import tempfile
import threading
import time
from pathlib import Path
from typing import Any, Mapping

from bees_process_safety import (
    atomic_write_text, configure_child_health, popen_owned,
    read_managed_health, write_managed_health,
)
import bees_elastic_wan_actor_session as session_module
import bees_wan_actor_worker as worker


def partition_environments(total: int, actors: int) -> list[int]:
    if not 1 <= actors <= 8 or not actors <= total <= 64:
        raise ValueError("multi-actor configuration requires 1-8 actors and actors <= total envs <= 64")
    per, remainder = divmod(total, actors)
    return [per + (index < remainder) for index in range(actors)]


def _read_json(path: Path) -> dict[str, Any]:
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
        return value if isinstance(value, dict) else {}
    except (OSError, UnicodeError, ValueError):
        return {}


def _sum_metrics(values: list[Mapping[str, Any]], actor_count: int) -> dict[str, Any]:
    """Merge per-actor local counters; don't sum broker-wide counters."""
    ints = (
        "accepted_steps_total", "accepted_trajectories_total", "staged_steps_total",
        "learner_consumed_steps_total", "upload_queue_depth", "upload_pending_batches",
        "upload_inflight_requests", "upload_workers", "accepted_upload_requests_total",
        "coalesced_upload_batches_total", "network_sent_bytes_total",
        "network_received_bytes_total",
    )
    floats = (
        "accepted_steps_per_sec", "staged_steps_per_sec",
        "learner_consumed_steps_per_sec", "network_mib_per_s",
    )
    result: dict[str, Any] = {
        "pid": os.getpid(),
        "env_count": sum(int(value["env_count"]) for value in values),
        "actor_count": actor_count,
        "worker_diagnostics_enabled": all(
            value.get("worker_diagnostics_enabled", True) for value in values
        ),
    }
    for field in ints:
        if all(isinstance(value.get(field), int) for value in values):
            result[field] = sum(int(value[field]) for value in values)
    for field in floats:
        if all(isinstance(value.get(field), (int, float)) for value in values):
            result[field] = sum(float(value[field]) for value in values)
    for field in ("learner_step_total", "policy_cycle", "optimizer_busy_seconds_total"):
        available = [value[field] for value in values if field in value]
        if available:
            result[field] = max(available)
    for field, value in values[0].items():
        if field.startswith("broker_"):
            result[field] = value
    profiles = [value.get("rollout_profile") for value in values]
    if all(isinstance(profile, dict) and profile.get("window_seconds", 0) > 0
           for profile in profiles):
        combined: dict[str, Any] = {
            "window_seconds": sum(float(p["window_seconds"]) for p in profiles) / len(profiles)
        }
        for name in profiles[0]:
            if name == "window_seconds":
                continue
            nums = [p.get(name) for p in profiles]
            if not all(isinstance(x, (float, int)) for x in nums):
                continue
            if name.endswith("_max_ms") or name in (
                "inference_batch_max_agents", "upload_compressed_bytes_max",
                "host_cpu_percent", "host_cpu_iowait_percent", "host_cpu_steal_percent",
                "host_load_1m",
            ):
                combined[name] = max(nums)
            elif name == "inference_batch_mean_agents":
                continue
            else:
                combined[name] = sum(nums)
        count = combined.get("inference_batches_count", 0)
        if count:
            combined["inference_batch_mean_agents"] = (
                combined["inference_agents_total"] / count
            )
        result["rollout_profile"] = combined
    return result


def run_group(args: Any) -> int:
    """Own all actor children as one managed process, with one aggregate health signal."""
    try:
        allocations = partition_environments(args.envs, args.actors)
    except ValueError as exc:
        print(f"error: {exc}", file=sys.stderr, flush=True)
        return 2
    if args.actor_id is not None:
        print("error: --actor-id cannot be combined with multiple actors", file=sys.stderr)
        return 2
    if args.local_base_port + args.actors * 64 - 1 > 65535:
        print("error: independent actors require nonoverlapping Unity port ranges", file=sys.stderr)
        return 2

    stop = threading.Event()
    def request_stop(_signal: int, _frame: Any) -> None:
        stop.set()
    previous_int = signal.signal(signal.SIGINT, request_stop)
    previous_term = signal.signal(signal.SIGTERM, request_stop)
    managed_stop = os.environ.get("BEES_TRAINING_STOP_FILE", "").strip()
    total_target_value = os.environ.get(session_module.WORKER_ENVS_TARGET_ENV, "").strip()
    parent_metrics_value = os.environ.get(worker.THROUGHPUT_METRICS_ENV, "").strip()
    parent_metrics = Path(parent_metrics_value).expanduser().resolve() if parent_metrics_value else None

    children: list[dict[str, Any]] = []
    phase_started = time.time()
    previous_state = ""
    current_target = args.envs
    last_report = 0.0
    # Avoid N PyTorch CPU thread pools each assuming they own every host core.
    try:
        available_cpus = len(os.sched_getaffinity(0))
    except (AttributeError, OSError):
        available_cpus = os.cpu_count() or 1
    torch_threads_per_actor = max(1, available_cpus // args.actors)
    try:
        with tempfile.TemporaryDirectory(prefix="bees-wan-actors-") as temp:
            state_root = Path(temp)
            target_paths: list[Path] = []
            for index, envs in enumerate(allocations):
                actor_key = (args.actor_key if index == 0 else hashlib.sha256(
                    f"{args.actor_key}:actor:{index}".encode("utf-8")
                ).hexdigest()[:32])
                child_env = os.environ.copy()
                if args.torch_device.lower() == "cpu":
                    if args.torch_threads is not None:
                        child_env["OMP_NUM_THREADS"] = str(args.torch_threads)
                        child_env["MKL_NUM_THREADS"] = str(args.torch_threads)
                    else:
                        child_env.setdefault("OMP_NUM_THREADS", str(torch_threads_per_actor))
                        child_env.setdefault("MKL_NUM_THREADS", str(torch_threads_per_actor))
                health_file = state_root / f"health-{index}.json"
                health_token = configure_child_health(child_env, health_file)
                metrics_file = state_root / f"throughput-{index}.json"
                child_env[worker.THROUGHPUT_METRICS_ENV] = str(metrics_file)
                target_file = state_root / f"env-target-{index}.txt"
                atomic_write_text(target_file, f"{envs}\n", encoding="ascii")
                target_paths.append(target_file)
                child_env[session_module.WORKER_ENVS_TARGET_ENV] = str(target_file)
                command = [
                    sys.executable, "-u", str(Path(__file__).resolve().with_name(
                        "bees_elastic_wan_actor_worker.py")),
                    "--actor-key", actor_key, "--actors", "1",
                    "--envs", str(envs),
                    "--local-base-port", str(args.local_base_port + 64 * index),
                    "--broker-host", args.broker_host,
                    "--broker-port", str(args.broker_port),
                    "--env", str(args.env),
                    "--auth-token-file", str(args.auth_token_file),
                    "--torch-device", args.torch_device,
                    "--upload-queue", str(args.upload_queue),
                    "--upload-workers", str(args.upload_workers),
                    "--reconnect-seconds", str(args.reconnect_seconds),
                ]
                if args.torch_threads is not None:
                    command.extend(["--torch-threads", str(args.torch_threads)])
                if args.graphics:
                    command.append("--graphics")
                process = popen_owned(
                    command, env=child_env, start_new_session=(os.name != "nt")
                )
                children.append({
                    "process": process, "health_file": health_file,
                    "health_token": health_token, "metrics_file": metrics_file,
                })
                print(
                    f"[Bees WAN multi] actor={index + 1}/{args.actors} "
                    f"envs={envs} base_port={args.local_base_port + 64 * index} "
                    f"key={actor_key[:8]} "
                    f"cpu_threads={child_env.get('OMP_NUM_THREADS', 'default')}", flush=True,
                )

            while not stop.wait(1.0):
                if managed_stop and Path(managed_stop).is_file():
                    break
                for index, child in enumerate(children):
                    exit_code = child["process"].poll()
                    if exit_code is not None:
                        raise RuntimeError(
                            f"independent actor {index + 1} exited with code {exit_code}"
                        )

                if total_target_value:
                    try:
                        raw = Path(total_target_value).read_text(encoding="ascii").strip()
                        desired = int(raw)
                    except (OSError, ValueError):
                        desired = current_target
                    if desired != current_target:
                        if not args.actors <= desired <= 64:
                            print(
                                f"[Bees WAN multi] invalid total env target {desired}; "
                                f"must be between {args.actors} and 64; preserving {current_target}.",
                                file=sys.stderr, flush=True,
                            )
                        else:
                            next_allocations = partition_environments(desired, args.actors)
                            for target_file, envs in zip(target_paths, next_allocations):
                                atomic_write_text(target_file, f"{envs}\n", encoding="ascii")
                            print(
                                f"[Bees WAN multi] live env target {current_target}->{desired} "
                                f"per_actor={next_allocations}", flush=True,
                            )
                            current_target = desired

                health = [read_managed_health(child["health_file"], child["health_token"])
                          for child in children]
                all_running = all(
                    isinstance(h, dict) and h.get("state") == "ready"
                    and h.get("details", {}).get("phase") == "running"
                    and isinstance(h.get("details", {}).get("progress_unix_seconds"), (int, float))
                    for h in health
                )
                all_ready = all(isinstance(h, dict) and h.get("state") == "ready"
                                for h in health)
                state = "ready" if all_ready else "starting"
                phase = ("running" if all_running else
                         "waiting-for-actors" if all_ready else "starting-actors")
                if state != previous_state:
                    phase_started = time.time()
                    previous_state = state
                details: dict[str, Any] = {
                    "component": "elastic-wan-actor", "phase": phase,
                    "phase_started_unix_seconds": phase_started,
                    "env_count": current_target, "actor_count": args.actors,
                }
                if all_running:
                    details["progress_unix_seconds"] = min(
                        float(h["details"]["progress_unix_seconds"]) for h in health
                    )
                write_managed_health(state, details=details)

                values = [_read_json(child["metrics_file"]) for child in children]
                if all(isinstance(v.get("env_count"), int) and
                       1 <= v["env_count"] <= 64 for v in values):
                    aggregated = _sum_metrics(values, args.actors)
                    if parent_metrics is not None:
                        try:
                            atomic_write_text(
                                parent_metrics, json.dumps(aggregated, sort_keys=True) + "\n",
                                encoding="utf-8",
                            )
                        except OSError:
                            pass
                    now = time.monotonic()
                    if now - last_report >= 30:
                        last_report = now
                        rates = [round(float(v.get("accepted_steps_per_sec", 0)), 1)
                                 for v in values]
                        print(
                            f"[Bees WAN multi] actors={args.actors} "
                            f"registered_envs={aggregated['env_count']} "
                            f"accepted_steps_per_sec={sum(rates):.1f} "
                            f"per_actor={rates} "
                            f"upload_queue={aggregated.get('upload_queue_depth', 0)}",
                            flush=True,
                        )
            return 0
    except Exception as exc:
        write_managed_health("error", error=f"{type(exc).__name__}: {exc}",
                             details={"component": "elastic-wan-actor", "actor_count": args.actors})
        print(f"[Bees WAN multi] {type(exc).__name__}: {exc}", file=sys.stderr, flush=True)
        return 1
    finally:
        stop.set()
        for child in children:
            process = child["process"]
            if process.poll() is None:
                process.terminate()
        deadline = time.monotonic() + 35.0
        for child in children:
            process = child["process"]
            if process.poll() is None:
                try:
                    process.wait(timeout=max(0.1, deadline - time.monotonic()))
                except Exception:
                    process.kill()
                    process.wait()
        signal.signal(signal.SIGINT, previous_int)
        signal.signal(signal.SIGTERM, previous_term)
