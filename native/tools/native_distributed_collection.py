"""Distributed native rollout work orders and verified collection handoff.

The Experiment Hub or an operator launches the existing native collectors on
allocated machines. This module owns *only* immutable work-order identity and
the completed-bundle barrier; it does not replace the Hub, the simulation,
the MA-POCA learner or process/resource scheduling.
"""

import hashlib
import json
import math
from pathlib import Path
import re
import time

from native_training_artifacts import atomic_json, file_sha256, native_source_contract_sha256


COLLECTOR_ID = re.compile(r"[A-Za-z0-9_-]{1,48}\Z")
HUB_JOB_ID = re.compile(r"[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}\Z")


def read_plan(path):
    return _validate_collector_plan(json.loads(path.read_text(encoding="utf-8")))


def _issued_allocations(path, baseline):
    """Recover the immutable allocations of an issued round, including on resume."""
    orders = json.loads(path.read_text(encoding="utf-8"))
    if (orders.get("kind") != "native_distributed_collection_orders" or
            orders.get("schema_version") != 1 or
            not isinstance(orders.get("jobs"), list)):
        raise ValueError(f"invalid issued native collection order: {path}")
    keys = ("id", "world_start", "world_count", "rollout_workers", "physics_workers")
    previous = {"collectors": [
        {key: job[key] for key in keys if key in job} for job in orders["jobs"]
    ]}
    read_plan_document = _validate_collector_plan(previous)
    original = {item["id"]: item for item in baseline["collectors"]}
    if (len(read_plan_document["collectors"]) != len(original) or
            any(item["id"] not in original or
                item["rollout_workers"] != original[item["id"]]["rollout_workers"] or
                item.get("physics_workers") != original[item["id"]].get("physics_workers")
                for item in read_plan_document["collectors"])):
        raise ValueError(f"issued collector resources differ from pinned plan: {path}")
    return read_plan_document


def _validate_collector_plan(plan):
    if not isinstance(plan, dict) or set(plan) != {"collectors"}:
        raise ValueError("distributed plan must contain only collectors")
    collectors = plan["collectors"]
    if not isinstance(collectors, list) or not collectors or len(collectors) > 256:
        raise ValueError("distributed plan must list 1..256 collector allocations")
    seen = set()
    ranges = []
    for item in collectors:
        required = {"id", "world_start", "world_count", "rollout_workers"}
        if (not isinstance(item, dict) or
                not required.issubset(item) or
                set(item) - required - {"physics_workers"}):
            raise ValueError("collector needs id, world_start, world_count, rollout_workers and optional physics_workers")
        name, start, count, workers = (item[key] for key in (
            "id", "world_start", "world_count", "rollout_workers",
        ))
        if (not isinstance(name, str) or not COLLECTOR_ID.fullmatch(name) or
                name in seen or type(start) is not int or
                type(count) is not int or type(workers) is not int or
                start < 0 or start + count > (1 << 63) or
                not 1 <= count <= 1024 or not 1 <= workers <= min(count, 256) or
                ("physics_workers" in item and
                 (type(item["physics_workers"]) is not int or
                  not 1 <= item["physics_workers"] <= 128))):
            raise ValueError(f"invalid or duplicate collector allocation: {name!r}")
        seen.add(name)
        ranges.append((start, start + count, name))
    for left, right in zip(sorted(ranges), sorted(ranges)[1:]):
        if left[1] > right[0]:
            raise ValueError(f"collector world-index ranges overlap: {left[2]} and {right[2]}")
    return plan


def balanced_round_plan(root, baseline, completed, round_number):
    """Choose only the next unissued collector window from verified round receipts.

    Machine/process/physics resources remain exactly as allocated. Across a
    contiguous world interval, transfer at most 10% of either participant's
    worlds between rounds, once per four rounds, after three sustained
    measurements. An interrupted round reuses its *issued* order verbatim.
    This is straggler reduction, not process/concurrency autotuning.
    """
    _validate_collector_plan(baseline)
    ordered = sorted(baseline["collectors"], key=lambda item: item["world_start"])
    if any(left["world_start"] + left["world_count"] != right["world_start"]
           for left, right in zip(ordered, ordered[1:])):
        raise ValueError("automatic collector balancing requires contiguous world windows")
    issued = root / f"collection-round-{round_number:05d}.json"
    if issued.exists():
        return _issued_allocations(issued, baseline), {"reason": "issued_order_reused"}

    current = baseline
    if completed:
        last = root / f"collection-round-{len(completed):05d}.json"
        if not last.is_file():
            raise ValueError(f"completed distributed round has no pinned order: {last}")
        current = _issued_allocations(last, baseline)
    if len(ordered) < 2 or round_number < 4 or round_number % 4 != 0:
        return current, {"reason": "stable_allocation"}

    # Only three *completed*, independently measured rounds can move work.
    # Recent structural changes to process counts or the runtime invalidate
    # comparability; small/short startup-bound samples do not drive changes.
    if len(completed) < 3:
        return current, {"reason": "insufficient_history"}
    ids = {item["id"] for item in current["collectors"]}
    by_id = {item["id"]: item for item in current["collectors"]}
    rates = {name: [] for name in ids}
    runners = set()
    for _, record in completed[-3:]:
        samples = record.get("rollout_metrics", {}).get("collectors")
        if not isinstance(samples, list) or len(samples) != len(ids) or (
                {row.get("id") for row in samples if isinstance(row, dict)} != ids):
            return current, {"reason": "incomplete_collector_measurements"}
        for row in samples:
            job = by_id[row["id"]]
            partition = row.get("partition")
            worlds = partition.get("world_count") if isinstance(partition, dict) else None
            seconds = row.get("wall_seconds")
            steps = row.get("world_steps")
            if (worlds != job["world_count"] or
                    row.get("rollout_processes") != job["rollout_workers"] or
                    row.get("physics_workers_per_process") != job.get("physics_workers", 1) or
                    type(seconds) not in (float, int) or not math.isfinite(seconds) or
                    seconds < 30.0 or type(steps) is not int or steps < 2000 or
                    not isinstance(row.get("native_runner_sha256"), str)):
                return current, {"reason": "unrepresentative_collector_measurements"}
            runners.add(row["native_runner_sha256"])
            rates[row["id"]].append(worlds / seconds)
    if len(runners) != 1 or any(
        max(samples) > min(samples) * 1.15 for samples in rates.values()
    ):
        return current, {"reason": "unstable_collector_measurements"}

    # Median worlds/second is the observed time to complete real arenas,
    # not CPU clock rate or the misleading sum of parallel worker wall times.
    medians = {name: sorted(samples)[1] for name, samples in rates.items()}
    slow = max(ids, key=lambda name: (by_id[name]["world_count"] / medians[name], name))
    fast = min(ids, key=lambda name: (by_id[name]["world_count"] / medians[name], name))
    if slow == fast:
        return current, {"reason": "balanced"}
    giver, taker = by_id[slow], by_id[fast]
    limit = min(
        giver["world_count"] - giver["rollout_workers"],
        1024 - taker["world_count"],
        max(1, giver["world_count"] // 10),
        max(1, taker["world_count"] // 10),
    )
    if limit < 1:
        return current, {"reason": "allocation_limits"}
    old_makespan = max(by_id[name]["world_count"] / medians[name] for name in ids)
    best_move, best_makespan = 0, old_makespan
    for moved in range(1, limit + 1):
        candidate = max(
            (by_id[name]["world_count"] - (moved if name == slow else 0)
             + (moved if name == fast else 0)) / medians[name]
            for name in ids
        )
        if candidate < best_makespan:
            best_move, best_makespan = moved, candidate
    if best_move == 0 or best_makespan > old_makespan * 0.98:
        return current, {"reason": "insufficient_predicted_improvement"}

    counts = {name: by_id[name]["world_count"] for name in ids}
    counts[slow] -= best_move
    counts[fast] += best_move
    # Preserve the full, deterministic global world-index set, even when
    # its original base is nonzero. Only the collector partitions change.
    start = ordered[0]["world_start"]
    intervals = {}
    for item in sorted(current["collectors"], key=lambda row: row["world_start"]):
        intervals[item["id"]] = start
        start += counts[item["id"]]
    plan = {"collectors": [
        {**item, "world_start": intervals[item["id"]],
         "world_count": counts[item["id"]]}
        for item in current["collectors"]
    ]}
    _validate_collector_plan(plan)
    return plan, {
        "reason": "measured_straggler_reduction", "from": slow, "to": fast,
        "worlds_transferred": best_move,
        "predicted_longest_seconds_before": old_makespan,
        "predicted_longest_seconds_after": best_makespan,
        "collector_worlds_per_second": medians,
    }


def make_orders(root, round_number, seed_offset, plan, run, checkpoint, manifest, learner_slot):
    if type(round_number) is not int or round_number <= 0:
        raise ValueError("distributed round number must be positive")
    source = json.loads(Path(run["path"]).read_text(encoding="utf-8"))
    source_run_id = source.get("run_id")
    if not isinstance(source_run_id, str) or not source_run_id.strip():
        raise ValueError("distributed collectors require a source run ID")
    identity = {
        "source_run_id": source_run_id,
        "source_run_sha256": run["sha256"],
        "checkpoint_sha256": file_sha256(checkpoint),
        "manifest_sha256": file_sha256(manifest),
        "learner_slot": learner_slot,
        "seed_offset": seed_offset,
        "round": round_number,
        "session": str(root),
        "collectors": plan["collectors"],
    }
    digest = hashlib.sha256(json.dumps(
        identity, sort_keys=True, separators=(",", ":"),
    ).encode("utf-8")).hexdigest()
    orders = {
        "schema_version": 1,
        "kind": "native_distributed_collection_orders",
        "source_run_id": source_run_id,
        "source_run_config": run["path"],
        "source_run_config_sha256": run["sha256"],
        "source_run_contract_sha256": native_source_contract_sha256(Path(run["path"])),
        "source_run_contract_version": 2,
        "checkpoint": str(checkpoint),
        "input_checkpoint_sha256": identity["checkpoint_sha256"],
        "model_manifest": str(manifest),
        "input_manifest_sha256": identity["manifest_sha256"],
        "learner_slot": learner_slot,
        "seed_offset": seed_offset,
        "round": round_number,
        "jobs": [],
    }
    for item in plan["collectors"]:
        job_id = hashlib.sha256((digest + ":" + item["id"]).encode("utf-8")).hexdigest()
        # A Hub bees_native_collect adapter publishes a completed bundle
        # under its immutable Hub job UUID. Reuse that directory when the
        # collector ID is the Hub job, rather than adding a second inbox
        # naming/transport protocol. Standalone collectors retain the
        # session-local work-order name.
        basename = (item["id"] if HUB_JOB_ID.fullmatch(item["id"])
                    else f"native-r{round_number:05d}-{item['id']}-{job_id[:12]}")
        orders["jobs"].append({
            **item,
            "collection_id": job_id,
            "bundle_directory": basename,
            # Agent maps input paths to its own machine; the SHA-256
            # identities above remain unchanged across operating systems.
            "collector_options": [
                "--collect-only", "--collection-id", job_id,
                "--seed-offset", str(seed_offset),
                "--rollout-world-start", str(item["world_start"]),
                "--rollout-worlds", str(item["world_count"]),
                "--rollout-workers", str(item["rollout_workers"]),
                *([] if "physics_workers" not in item else [
                    "--physics-workers", str(item["physics_workers"]),
                ]),
            ],
        })
    path = root / f"collection-round-{round_number:05d}.json"
    if path.exists():
        previous = json.loads(path.read_text(encoding="utf-8"))
        if "source_run_contract_sha256" not in previous:
            # Pre-portable work orders retain exact-byte source identity.
            legacy = {k: v for k, v in orders.items()
                      if k not in ("source_run_contract_sha256",
                                   "source_run_contract_version")}
            if previous != legacy:
                raise ValueError(f"distributed round work order changed: {path}")
            return previous, path
        if "source_run_contract_version" not in previous:
            # Previously issued portable v1 orders included allocation
            # defaults in their physical digest. Retain that exact contract
            # and accept old/new collector receipts for its original hash.
            legacy = {k: v for k, v in orders.items()
                      if k != "source_run_contract_version"}
            legacy["source_run_contract_sha256"] = native_source_contract_sha256(
                Path(run["path"]), include_allocation=True,
            )
            if previous != legacy:
                raise ValueError(f"distributed round work order changed: {path}")
            return previous, path
        if previous != orders:
            raise ValueError(f"distributed round work order changed: {path}")
    else:
        atomic_json(path, orders)
    return orders, path


def verify_bundle(path, job, orders):
    raw = path.read_bytes()
    receipt = json.loads(raw)
    partition = receipt.get("partition")
    if "source_run_contract_sha256" not in orders:
        source_matches = receipt.get("run_config_sha256") == orders["source_run_config_sha256"]
    elif orders.get("source_run_contract_version") == 2:
        source_matches = receipt.get("source_run_contract_sha256") == orders["source_run_contract_sha256"]
    else:
        # v1 collectors wrote the digest in the original field; updated
        # collectors also publish it explicitly alongside the v2 value.
        source_matches = receipt.get(
            "source_run_contract_sha256_v1",
            receipt.get("source_run_contract_sha256"),
        ) == orders["source_run_contract_sha256"]
    expected = (
        receipt.get("schema_version") == 1 and
        receipt.get("kind") == "native_rollout_bundle" and
        receipt.get("collection_id") == job["collection_id"] and
        receipt.get("input_manifest_sha256") == orders["input_manifest_sha256"] and
        receipt.get("input_checkpoint_sha256") == orders["input_checkpoint_sha256"] and
        source_matches and
        receipt.get("seed_offset") == orders["seed_offset"] and
        receipt.get("learner_slot") == orders["learner_slot"] and
        receipt.get("rollout_worlds") == job["world_count"] and
        receipt.get("rollout_processes") == job["rollout_workers"] and
        # Resource constraints belong to the signed work order. Verify
        # explicit allocations rather than trusting collector assertions.
        ("physics_workers" not in job or
         receipt.get("physics_workers_per_process") == job["physics_workers"]) and
        isinstance(partition, dict) and
        partition.get("source_run_id") == orders["source_run_id"] and
        partition.get("world_start") == job["world_start"] and
        partition.get("world_count") == job["world_count"] and
        isinstance(receipt.get("captures"), list) and receipt["captures"]
    )
    if not expected:
        raise ValueError(f"distributed collector returned a mismatched or stale bundle: {path}")
    for key in ("native_runner_sha256", "policy_worker_sha256"):
        digest = receipt.get(key)
        if not isinstance(digest, str) or len(digest) != 64 or any(
            char not in "0123456789abcdef" for char in digest
        ):
            raise ValueError(f"collector did not pin its runtime implementation: {path}")
    metrics = receipt.get("rollout_metrics")
    if not isinstance(metrics, dict) or any(
        type(metrics.get(key)) is not int or metrics[key] <= 0
        for key in ("successful_world_steps", "successful_policy_rows", "worlds", "episodes")
    ) or metrics["worlds"] != job["world_count"] or (
        type(metrics.get("wall_seconds")) not in (int, float) or
        not math.isfinite(metrics["wall_seconds"]) or metrics["wall_seconds"] <= 0
    ):
        raise ValueError(f"distributed collector returned invalid work measurements: {path}")
    per_process = receipt.get("physics_workers_per_process", 1)
    if type(per_process) is not int or not 1 <= per_process <= 128:
        raise ValueError(f"distributed collector has invalid physics allocation: {path}")
    requested = metrics.get("requested_physics_worker_slots")
    capacity = receipt["rollout_processes"] * per_process
    # Historical bundles did not record model-step CPU limits. Their
    # absent field denotes the old single-worker default, not a claimed
    # measurement. Explicit allocations always require an exact matching
    # simulator receipt.
    if requested is not None or "physics_workers" in job:
        if type(requested) is not int or requested != capacity:
            raise ValueError(f"distributed collector physics allocation and measured receipt disagree: {path}")
    return {
        "path": str(path.resolve()),
        "sha256": hashlib.sha256(raw).hexdigest(),
        "collector_id": job["id"],
        "partition": partition,
        "rollout_processes": receipt["rollout_processes"],
        "physics_workers_per_process": receipt.get("physics_workers_per_process", 1),
        "native_runner_sha256": receipt["native_runner_sha256"],
        "policy_worker_sha256": receipt["policy_worker_sha256"],
        "metrics": metrics,
    }


def wait_for_collections(inbox, orders, timeout_seconds, progress=None):
    if not inbox.is_dir():
        raise ValueError(f"collector inbox must be an existing directory: {inbox}")
    started = time.monotonic()
    results = {}
    while len(results) < len(orders["jobs"]):
        for job in orders["jobs"]:
            if job["id"] in results:
                continue
            receipt = inbox / job["bundle_directory"] / "rollout-bundle.json"
            if receipt.is_file():
                results[job["id"]] = verify_bundle(receipt, job, orders)
                if progress is not None:
                    progress("native_distributed_collector_received", {
                        "collector_id": job["id"], "receipt": str(receipt),
                        "world_steps": results[job["id"]]["metrics"]["successful_world_steps"],
                    })
        if len(results) == len(orders["jobs"]):
            break
        if timeout_seconds > 0 and time.monotonic() - started >= timeout_seconds:
            missing = [job["id"] for job in orders["jobs"] if job["id"] not in results]
            raise TimeoutError(f"distributed native collectors have not delivered: {missing}")
        time.sleep(1.0)
    return [results[job["id"]] for job in orders["jobs"]]


def aggregate_collection_metrics(bundles):
    """Sum useful work and occupied collector wall time, never invent SPS.

    Collectors on different machines overlap in time. Sum of occupied wall
    seconds is a capacity-weighted denominator, *not* end-to-end elapsed
    time or the fleet's concurrently achieved SPS.
    """
    if not bundles:
        raise ValueError("at least one completed collection is required")
    worker_digests = {bundle["policy_worker_sha256"] for bundle in bundles}
    if len(worker_digests) != 1:
        raise ValueError("mixed native policy inference implementations in one optimizer round")
    rows = [bundle["metrics"] for bundle in bundles]
    steps = sum(row["successful_world_steps"] for row in rows)
    policies = sum(row["successful_policy_rows"] for row in rows)
    seconds = sum(row["wall_seconds"] for row in rows)
    collector_rates = [
        row["successful_world_steps"] / row["wall_seconds"]
        for row in rows
    ]
    sampled = [
        row["sampled_model_timing"] for row in rows
        if isinstance(row.get("sampled_model_timing"), dict)
    ]
    result = {
        "successful_world_steps": steps,
        "successful_policy_rows": policies,
        "worlds": sum(row["worlds"] for row in rows),
        "episodes": sum(row["episodes"] for row in rows),
        "wall_seconds": seconds,
        "wall_seconds_scope": "sum_of_independent_collector_wall_seconds",
        "world_steps_per_wall_second": steps / seconds,
        "policy_rows_per_wall_second": policies / seconds,
        "collector_count": len(rows),
        "total_rollout_processes": sum(
            bundle["rollout_processes"] for bundle in bundles
        ),
        # Upper bound requested by the distributed plan, not an observed
        # number of busy cores or an estimate of process utilization.
        "requested_physics_worker_slots": sum(
            bundle["rollout_processes"] * bundle["physics_workers_per_process"]
            for bundle in bundles
        ),
        "collectors_with_physics_worker_measurements":
            sum("parallel_physics_rounds" in row for row in rows),
        "collector_world_steps_per_wall_second_min": min(collector_rates),
        "collector_world_steps_per_wall_second_max": max(collector_rates),
        "policy_worker_sha256": next(iter(worker_digests)),
        "collectors": [
            {
                "id": bundle["collector_id"],
                "partition": bundle["partition"],
                "native_runner_sha256": bundle["native_runner_sha256"],
                "rollout_processes": bundle["rollout_processes"],
                "physics_workers_per_process": bundle["physics_workers_per_process"],
                **({key: row[key] for key in (
                    "parallel_physics_rounds",
                    "parallel_stage_id_serialized_arenas",
                    "parallel_worker_cpu_wall_seconds_sum",
                )} if "parallel_physics_rounds" in row else {}),
                "worker_shards": row.get("worker_shards", []),
                "sampled_model_timing": row.get("sampled_model_timing"),
                "world_steps": row["successful_world_steps"],
                "policy_rows": row["successful_policy_rows"],
                "wall_seconds": row["wall_seconds"],
                "world_steps_per_wall_second": (
                    row["successful_world_steps"] / row["wall_seconds"]
                ),
            } for bundle, row in zip(bundles, rows)
        ],
    }
    if sampled:
        result["sampled_model_timing"] = {
            key: sum(sample[key] for sample in sampled)
            for key in ("calls", "rows", "wall_seconds")
        }
    # Do not silently convert a historical unmeasured collector into
    # zero observed parallel work. Metrics describe the entire distributed
    # update only when every collector contributed the same evidence.
    if all("parallel_physics_rounds" in row for row in rows):
        result.update({
            "parallel_physics_rounds":
                sum(row["parallel_physics_rounds"] for row in rows),
            "parallel_stage_id_serialized_rounds":
                sum(row["parallel_stage_id_serialized_rounds"] for row in rows),
            "parallel_stage_id_serialized_arenas":
                sum(row["parallel_stage_id_serialized_arenas"] for row in rows),
            "parallel_worker_cpu_wall_seconds_sum":
                sum(row["parallel_worker_cpu_wall_seconds_sum"] for row in rows),
        })
    return result
