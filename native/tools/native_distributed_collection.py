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

from native_training_artifacts import (
    atomic_json, file_sha256, native_source_contract_sha256,
    verified_source_scene_cache, combined_source_scene_cache,
    combined_rollout_lifecycle_metrics,
)
from native_gpu_allocation import GPU_MASK_ENVIRONMENTS
from native_mission_contract import (
    level_has_goal, verified_policy_observations, combined_policy_observations,
)


COLLECTOR_ID = re.compile(r"[A-Za-z0-9_-]{1,48}\Z")
HUB_JOB_ID = re.compile(r"[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}\Z")
HUB_MACHINE_ID = re.compile(r"[a-z][a-z0-9_-]{0,47}\Z")


def read_plan(path):
    return _validate_collector_plan(json.loads(path.read_text(encoding="utf-8")))


def derive_hub_resource_plan(hub_plan, total_worlds):
    """One fixed, portable source allocation from user-chosen Hub resources.

    Prefer a single native rollout process per machine: it batches neural
    inference once while persistent arena physics work fans out over the
    leased CPU cores. Large CPU leases may require several processes because
    each process supports at most 128 physical workers. A bounded,
    deterministic proportional split assigns World windows by CPU capacity;
    the existing measured between-round balancer corrects for heterogeneous
    hardware after sustained real SPS measurements.
    """
    if not isinstance(hub_plan, dict):
        raise ValueError("Hub resource plan must be a JSON object")
    entries = hub_plan.get("collectors")
    if (hub_plan.get("kind") != "native_hub_round_dispatch" or
            hub_plan.get("schema_version") != 1 or
            hub_plan.get("schedule_start") is not True or
            hub_plan.get("confirm_unattended_start") is not True or
            not isinstance(entries, list) or not 2 <= len(entries) <= 16 or
            type(total_worlds) is not int or
            not len(entries) <= total_worlds <= len(entries) * 1024):
        raise ValueError("Hub resource plan requires 2..16 machines and 1..1024 worlds per machine")
    seen = set()
    machines = set()
    cpus = []
    gpu_minima = []
    gpu_counts = []
    for entry in entries:
        if (not isinstance(entry, dict) or
                not isinstance(entry.get("id"), str) or
                not COLLECTOR_ID.fullmatch(entry["id"]) or
                entry["id"] in seen or
                not isinstance(entry.get("machine_id"), str) or
                not HUB_MACHINE_ID.fullmatch(entry["machine_id"]) or
                entry["machine_id"] in machines or
                not isinstance(entry.get("resources"), dict)):
            raise ValueError("Hub source plan needs distinct issued collector and machine IDs")
        seen.add(entry["id"])
        machines.add(entry["machine_id"])
        cpu = entry["resources"].get("cpus")
        if type(cpu) is not int or not 1 <= cpu <= 32768:
            raise ValueError("Hub CPU resources must be positive finite quotas")
        cpus.append(cpu)
        resources = entry["resources"]
        devices = resources.get("gpus", [])
        if not isinstance(devices, list):
            raise ValueError("native GPU reservation list must be an array")
        gpu_count = resources.get("gpu_count", len(devices))
        if (type(gpu_count) is not int or gpu_count not in (0, 1, 2) or
                (devices and len(devices) != gpu_count) or
                (gpu_count and resources.get("gpu_mode", "exclusive") != "exclusive") or
                cpu < max(1, gpu_count)):
            raise ValueError("native Hub collector requires CPU-only or one/two exclusive GPUs with enough CPU actors")
        gpu_counts.append(gpu_count)
        gpu_minima.append(max(1, gpu_count))
    if any(gpu_counts) and hub_plan.get("confirm_gpu_start") is not True:
        raise ValueError("native Hub GPU collection requires explicit operator confirmation")
    if sum(gpu_minima) > total_worlds:
        raise ValueError("selected native GPU resources require at least one simulated World per GPU")

    # Allocate a minimum one arena per machine, then a fair capped share
    # proportional to fixed CPU budgets. No per-machine benchmark settings
    # or CPU-architecture guesses enter training/model lineage.
    counts = gpu_minima[:]
    for _ in range(total_worlds - sum(gpu_minima)):
        eligible = [i for i, count in enumerate(counts) if count < 1024]
        if not eligible:
            raise ValueError("Hub source plan exhausted available native World slots")
        selected = max(eligible, key=lambda i: (
            cpus[i] / (counts[i] + 1), -i,
        ))
        counts[selected] += 1

    current = 0
    collectors = []
    for entry, cpus_reserved, worlds, gpu_floor in zip(
            entries, cpus, counts, gpu_minima):
        processes = min(worlds, max(gpu_floor, (cpus_reserved + 127) // 128))
        physics = min(128, max(1, cpus_reserved // processes),
                      max(1, worlds // processes))
        collectors.append({
            "id": entry["id"],
            "world_start": current,
            "world_count": worlds,
            "rollout_workers": processes,
            "physics_workers": physics,
            # Preserve the EXACT Hub GPU count in the immutable collector
            # order. Physical device identities remain owned by the Hub's
            # machine-local exclusive lease; never export PCI IDs as ordinals.
            "hub_gpu_count": entry["resources"].get(
                "gpu_count", len(entry["resources"].get("gpus", [])),
            ),
        })
        current += worlds
    return _validate_collector_plan({"collectors": collectors})


def _issued_allocations(path, baseline):
    """Recover the immutable allocations of an issued round, including on resume."""
    orders = json.loads(path.read_text(encoding="utf-8"))
    if (orders.get("kind") != "native_distributed_collection_orders" or
            orders.get("schema_version") != 1 or
            not isinstance(orders.get("jobs"), list)):
        raise ValueError(f"invalid issued native collection order: {path}")
    keys = ("id", "world_start", "world_count", "rollout_workers", "physics_workers",
            "rollout_gpus", "rollout_gpu_mask", "hub_gpu_count")
    previous = {"collectors": [
        {key: job[key] for key in keys if key in job} for job in orders["jobs"]
    ]}
    read_plan_document = _validate_collector_plan(previous)
    original = {item["id"]: item for item in baseline["collectors"]}
    if (len(read_plan_document["collectors"]) != len(original) or
            any(item["id"] not in original or
                item["rollout_workers"] != original[item["id"]]["rollout_workers"] or
                item.get("physics_workers") != original[item["id"]].get("physics_workers") or
                item.get("rollout_gpus") != original[item["id"]].get("rollout_gpus") or
                item.get("rollout_gpu_mask") != original[item["id"]].get("rollout_gpu_mask") or
                item.get("hub_gpu_count") != original[item["id"]].get("hub_gpu_count")
                for item in read_plan_document["collectors"])):
        raise ValueError(f"issued collector resources differ from pinned plan: {path}")
    initial = sorted(baseline["collectors"], key=lambda row: row["world_start"])
    actual = sorted(read_plan_document["collectors"], key=lambda row: row["world_start"])
    if (actual[0]["world_start"] != initial[0]["world_start"] or
            sum(row["world_count"] for row in actual) !=
            sum(row["world_count"] for row in initial) or
            any(left["world_start"] + left["world_count"] != right["world_start"]
                for left, right in zip(actual, actual[1:]))):
        raise ValueError(f"issued collector world domain differs from pinned plan: {path}")
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
                set(item) - required - {
                    "physics_workers", "rollout_gpus", "rollout_gpu_mask",
                    "hub_gpu_count",
                }):
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
        if "hub_gpu_count" in item:
            leased = item["hub_gpu_count"]
            if (type(leased) is not int or leased not in (0, 1, 2) or
                    leased > workers or leased > count or
                    "rollout_gpus" in item or "rollout_gpu_mask" in item):
                raise ValueError(
                    f"Hub GPU lease count conflicts with native collector allocation: {name!r}"
                )
        if "rollout_gpus" in item:
            gpus = item["rollout_gpus"]
            if (not isinstance(gpus, list) or not 1 <= len(gpus) <= workers or
                    any(type(gpu) is not int or not 0 <= gpu <= 255 for gpu in gpus) or
                    len(set(gpus)) != len(gpus) or
                    item.get("rollout_gpu_mask", "CUDA_VISIBLE_DEVICES")
                    not in GPU_MASK_ENVIRONMENTS):
                raise ValueError(f"invalid collector GPU allocation: {name!r}")
        elif "rollout_gpu_mask" in item:
            raise ValueError("collector GPU visibility selector requires rollout_gpus")
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
    runners = {name: set() for name in ids}
    for _, record in completed[-3:]:
        metrics = record.get("rollout_metrics")
        samples = metrics.get("collectors") if isinstance(metrics, dict) else None
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
            runners[row["id"]].add(row["native_runner_sha256"])
            rates[row["id"]].append(worlds / seconds)
    # Windows, Linux and distinct CPU architectures legitimately build
    # different native executables. Require implementation stability within
    # each collector, not byte-identical executables across machines.
    if any(len(hashes) != 1 for hashes in runners.values()) or any(
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
        giver["world_count"] // 10,
        taker["world_count"] // 10,
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
                *([] if "rollout_gpus" not in item else [
                    "--rollout-gpus", ",".join(str(gpu) for gpu in item["rollout_gpus"]),
                    "--rollout-gpu-mask", item.get("rollout_gpu_mask", "CUDA_VISIBLE_DEVICES"),
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


def expected_gpu_allocation(job):
    """Physical placement is part of an immutable collector order, not a
    runtime-selected or inferred allocation. Missing means no override.
    """
    gpus = job.get("rollout_gpus")
    if gpus is None:
        return None
    return {
        "visibility_environment": job.get("rollout_gpu_mask", "CUDA_VISIBLE_DEVICES"),
        "physical_gpu_ids": gpus,
        "worker_gpu_ids": [gpus[index % len(gpus)]
                           for index in range(job["rollout_workers"])],
    }


def verify_bundle(path, job, orders):
    raw = path.read_bytes()
    receipt = json.loads(raw)
    partition = receipt.get("partition")
    # A Hub-authorized one/two-GPU lease must produce its exact count of
    # verified GPU forwards. CPU-only issued work cannot silently claim
    # accelerator execution. Existing standalone orders retain their
    # original explicit-ordinal GPU identity.
    if "hub_gpu_count" in job:
        leased = job["hub_gpu_count"]
        actual = receipt.get("leased_gpu_inference") is True
        if actual != (leased > 0) or (
                actual and receipt.get("leased_gpu_count", 1) != leased):
            raise ValueError(f"native collector did not use its pinned Hub GPU lease: {path}")
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
        receipt.get("gpu_allocation") == expected_gpu_allocation(job) and
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
    observation_work = metrics.get("policy_observations")
    # Mission goals have been encoded by the physical host since native
    # objective support. Reject an old collector that would silently
    # train against a blind policy despite a valid capture file.
    source_run = Path(orders["source_run_config"])
    if file_sha256(source_run) != orders["source_run_config_sha256"]:
        raise ValueError("distributed source run changed after work-order issuance")
    source = json.loads(source_run.read_text(encoding="utf-8"))
    start = job["world_start"]
    count = job["world_count"]
    authored = source.get("levels")
    if not isinstance(authored, list) or not authored:
        raise ValueError("distributed source has no authentic authored Level templates")
    # Match train_policy_round.expand_rollout_worlds: replicas cycle the
    # source template list by GLOBAL world index. A 64-world collector can
    # legitimately be allocated from just one authored Level; slicing the
    # template list would reject such perfectly valid high-throughput jobs.
    has_mission = any(
        level_has_goal(authored[index % len(authored)])
        for index in range(start, start + count)
    )
    verified_policy_observations(observation_work, require_goal=has_mission)
    # The selected replica's original scene/goal identity is stable even
    # when its runtime world and seed are derived for this machine.
    if "source_scene_template_cache" in metrics:
        metrics["source_scene_template_cache"] = verified_source_scene_cache(

            metrics["source_scene_template_cache"],
            metrics["worlds"], metrics["episodes"],
        )
    per_process = receipt.get("physics_workers_per_process", 1)
    if type(per_process) is not int or not 1 <= per_process <= 128:
        raise ValueError(f"distributed collector has invalid physics allocation: {path}")
    if "leased_gpu_inference" in receipt and receipt["leased_gpu_inference"] is not True:
        raise ValueError(f"invalid leased-GPU collector receipt marker: {path}")
    count = receipt.get("leased_gpu_count", 1) if receipt.get("leased_gpu_inference") else None
    if receipt.get("leased_gpu_inference"):
        # Previous single-device receipts predate the explicit count field.
        # Only dual leases require its recorded identity and PCI evidence.
        if (type(count) is not int or count not in (1, 2) or
                count > receipt["rollout_processes"]):
            raise ValueError(f"GPU collector declared an impossible leased-device count: {path}")
    elif count is not None:
        raise ValueError(f"collector supplied a GPU device count without a verified lease: {path}")
    if receipt.get("gpu_allocation") is not None or receipt.get("leased_gpu_inference"):
        proof = metrics.get("gpu_inference")
        if count == 2 and (
                not isinstance(proof, dict) or
                not isinstance(proof.get("gpu_pci_addresses"), list) or
                len(proof["gpu_pci_addresses"]) != 2 or
                len(set(proof["gpu_pci_addresses"])) != 2 or
                any(not isinstance(pci, str) or
                    re.fullmatch(r"[0-9a-f]{4}:[0-9a-f]{2}:[0-9a-f]{2}\.[0-7]", pci) is None
                    for pci in proof["gpu_pci_addresses"])):
            raise ValueError(f"GPU collector did not verify both physical HIP devices: {path}")
        if (not isinstance(proof, dict) or
                type(proof.get("verified_workers")) is not int or
                proof["verified_workers"] != receipt["rollout_processes"] or
                type(proof.get("model_forwards")) is not int or
                proof["model_forwards"] < proof["verified_workers"] or
                type(proof.get("agent_rows")) is not int or
                proof["agent_rows"] < proof["model_forwards"] or
                proof["agent_rows"] > metrics["successful_policy_rows"]):
            raise ValueError(f"GPU collector has no verified model forward evidence: {path}")
    if count == 2:
        device_rows = proof.get("per_device")
        if (not isinstance(device_rows, list) or len(device_rows) != 2 or
                [item.get("gpu_pci_address") for item in device_rows
                 if isinstance(item, dict)] != sorted(proof["gpu_pci_addresses"])):
            raise ValueError(f"dual-GPU collector did not attribute actual work to both physical devices: {path}")
        for row in device_rows:
            if any(type(row.get(key)) is not int or row[key] <= 0
                   for key in ("rollout_processes", "model_forwards",
                               "gpu_agent_rows", "successful_world_steps",
                               "successful_policy_rows")):
                raise ValueError(f"invalid per-GPU native collection work counters: {path}")
        if (sum(item["rollout_processes"] for item in device_rows) != receipt["rollout_processes"] or
                sum(item["model_forwards"] for item in device_rows) != proof["model_forwards"] or
                sum(item["gpu_agent_rows"] for item in device_rows) != proof["agent_rows"] or
                sum(item["successful_world_steps"] for item in device_rows) != metrics["successful_world_steps"] or
                sum(item["successful_policy_rows"] for item in device_rows) != metrics["successful_policy_rows"]):
            raise ValueError(f"dual-GPU collector physical work attribution does not reconcile: {path}")
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


def wait_for_collections(inbox, orders, timeout_seconds, progress=None,
                         archive_inbox=None, check_unfinished=None):
    if not inbox.is_dir():
        raise ValueError(f"collector inbox must be an existing directory: {inbox}")
    from transfer_policy_bundle import verify_bundle_files
    if archive_inbox is not None:
        if not archive_inbox.is_dir():
            raise ValueError(f"collector archive inbox must already exist: {archive_inbox}")
        # Import uses this module's existing receipt validation and publishes
        # into the ordinary inbox only after all capture bytes are verified.
        from transfer_policy_bundle import import_bundle_archive
    started = time.monotonic()
    results = {}
    while len(results) < len(orders["jobs"]):
        for job in orders["jobs"]:
            if job["id"] in results:
                continue
            receipt = inbox / job["bundle_directory"] / "rollout-bundle.json"
            if not receipt.is_file() and archive_inbox is not None:
                name = job["bundle_directory"]
                if not isinstance(name, str) or re.fullmatch(r"[A-Za-z0-9_-]{1,96}", name) is None:
                    raise ValueError("invalid collector bundle directory in issued order")
                # Upload as *.tar.pending and rename to *.tar after transfer.
                # An interrupted upload is never mistaken for a completed
                # collector bundle and never becomes an optimizer input.
                archive = archive_inbox / (name + ".tar")
                if archive.is_file():
                    imported = import_bundle_archive(archive, job, orders, inbox)
                    if progress is not None:
                        progress("native_distributed_collector_archive_imported", {
                            "collector_id": job["id"], **imported,
                        })
            if receipt.is_file():
                verified = verify_bundle(receipt, job, orders)
                verify_bundle_files(receipt)
                results[job["id"]] = verified
                if progress is not None:
                    progress("native_distributed_collector_received", {
                        "collector_id": job["id"], "receipt": str(receipt),
                        "world_steps": results[job["id"]]["metrics"]["successful_world_steps"],
                    })
        if len(results) == len(orders["jobs"]):
            break
        # The Hub-aware learner can inspect already-recorded native job
        # outcomes here. This is the existing collector barrier, not a
        # separate polling owner or source of simulation truth. It runs only
        # for collector IDs whose verified rollout is still missing.
        if check_unfinished is not None:
            check_unfinished(tuple(
                job["id"] for job in orders["jobs"] if job["id"] not in results
            ))
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
                    "parallel_physics_direct_rounds",
                    "parallel_physics_queued_rounds",
                    "parallel_stage_id_serialized_rounds",
                    "parallel_stage_id_serialized_arenas",
                    "peak_parallel_physics_workers",
                    "parallel_worker_cpu_wall_seconds_sum",
                ) if key in row} if "parallel_physics_rounds" in row else {}),
                "worker_shards": row.get("worker_shards", []),
                "sampled_model_timing": row.get("sampled_model_timing"),
                "gpu_inference": row.get("gpu_inference"),
                "reinforcement_arrivals": row.get("reinforcement_arrivals"),
                "reinforcement_wakeup_work": row.get("reinforcement_wakeup_work"),
                "queen_minion_controllers": row.get("queen_minion_controllers"),
                "map_border_ships": row.get("map_border_ships"),
                "ship_slot_work": row.get("ship_slot_work"),
                "source_scene_template_cache": row.get("source_scene_template_cache"),
                "policy_observations": row.get("policy_observations"),
                "world_steps": row["successful_world_steps"],
                "policy_rows": row["successful_policy_rows"],
                "wall_seconds": row["wall_seconds"],
                "world_steps_per_wall_second": (
                    row["successful_world_steps"] / row["wall_seconds"]
                ),
            } for bundle, row in zip(bundles, rows)
        ],
    }
    if all("policy_observations" in row for row in rows):
        result["policy_observations"] = combined_policy_observations(
            [row["policy_observations"] for row in rows]
        )
    scene_cache = combined_source_scene_cache(rows)
    if scene_cache is not None:
        result["source_scene_template_cache"] = scene_cache
    result.update(combined_rollout_lifecycle_metrics(rows))
    # A distributed learner may mix old and new native collectors. Keep
    # physical border evidence only when every signed input has it, and
    # preserve per-collector totals for meaningful fault localization.
    border_keys = ("entries", "movement_stops", "override_skips",
                   "lethal_hits", "lethal_kills",
                   "confinement_position_corrections",
                   "confinement_velocity_clamps",
                   "confinement_unfittable_hulls",
                   "confinement_missing_profiles", "completed_episodes")
    if all("map_border_ships" in row for row in rows):
        result["map_border_ships"] = {
            key: sum(row["map_border_ships"][key] for row in rows)
            for key in border_keys
        }
    slot_keys = ("integrated_bodies", "inactive_ship_slots_skipped",
                 "inactive_ship_grid_slots_skipped")
    if all("ship_slot_work" in row for row in rows):
        result["ship_slot_work"] = {
            key: sum(row["ship_slot_work"][key] for row in rows)
            for key in slot_keys
        }
        obstacle_keys = ("inactive_obstacle_slots_skipped",
                         "inactive_obstacle_grid_slots_skipped")
        if all(all(key in row["ship_slot_work"] for key in obstacle_keys)
               for row in rows):
            result["ship_slot_work"].update({
                key: sum(row["ship_slot_work"][key] for row in rows)
                for key in obstacle_keys
            })
        projectile_key = "inactive_projectile_slots_skipped"
        if all(projectile_key in row["ship_slot_work"] for row in rows):
            result["ship_slot_work"][projectile_key] = sum(
                row["ship_slot_work"][projectile_key] for row in rows)
    # A partial set of timed actors cannot be presented as timing for
    # the full distributed round. Preserve individual evidence above.
    result["collectors_with_sampled_model_timing"] = len(sampled)
    if len(sampled) == len(rows):
        result["sampled_model_timing"] = {
            key: sum(sample[key] for sample in sampled)
            for key in ("calls", "rows", "wall_seconds")
        }
    # GPU proof is independently generated by the native collector's
    # resident model workers. Mixed CPU/GPU fleets aggregate GPU work only
    # across collectors that actually used a verified device; never treat
    # CPU-only collection as an accelerator stall or zero GPU utilization.
    accelerator_rows = [
        row["gpu_inference"] for row in rows
        if isinstance(row.get("gpu_inference"), dict)
    ]
    result["collectors_with_gpu_inference"] = len(accelerator_rows)
    if accelerator_rows:
        result["gpu_inference"] = {
            "collector_count": len(accelerator_rows),
            "verified_workers": sum(proof["verified_workers"]
                                    for proof in accelerator_rows),
            "model_forwards": sum(proof["model_forwards"]
                                  for proof in accelerator_rows),
            "agent_rows": sum(proof["agent_rows"]
                              for proof in accelerator_rows),
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
    dispatch_keys = ("parallel_physics_direct_rounds", "parallel_physics_queued_rounds")
    if all(all(key in row for key in dispatch_keys) for row in rows):
        result.update({key: sum(row[key] for row in rows) for key in dispatch_keys})
    return result
