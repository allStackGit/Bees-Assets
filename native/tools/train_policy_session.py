"""Run consecutive unified-native MA-POCA training rounds without manual model handoffs.

One session owns a checkpoint lineage; each round still uses the established
native collector and learner. Interruption preserves all partial attempts and
completed artifacts. Completed round receipts, not a mutable progress cursor,
are authoritative for resumption.
"""

import argparse
from collections import defaultdict
from contextlib import contextmanager, nullcontext
import json
import math
import os
from pathlib import Path
import re
import shutil
import signal
import subprocess
import sys
import time
from tempfile import TemporaryDirectory
from urllib.error import URLError
from urllib.request import urlopen

from native_training_artifacts import atomic_json, check_learner, file_sha256
from native_bundle_receiver import BundleReceiver
from transfer_policy_inputs import export_inputs
from native_distributed_collection import (
    read_plan, make_orders, wait_for_collections, balanced_round_plan,
    derive_hub_resource_plan,
)
from native_session_selection import (
    publish_best_evaluated, publish_latest_playable, verified_mission_holdout,
)
from native_game_registration import register_playable
from native_curriculum import (load_curriculum, selected_curriculum_run,
                               curriculum_stage_metrics)


ATTEMPT = re.compile(r"round-(\d{5})-attempt-(\d{3})\Z")
EVALUATION_ATTEMPT = re.compile(r"evaluation-attempt-(\d{3})\Z")


class NativeHubCollectionBarrier:
    """Inspect Hub's OWN terminal job states while waiting for verified rollouts.

    The Hub retains all lease, Start and Stop authority. The learner only
    looks up its already-dispatched job IDs; it never retries or schedules
    replacements, and absence of a rollout is not inferred from telemetry.
    Called by the normal collection receipt barrier, not a separate monitor.
    """

    def __init__(self, port, dispatched, orders):
        self.port = port
        self.bundle_id = dispatched.get("bundle_id")
        reported_ids = dispatched.get("member_job_ids")
        if (not isinstance(self.bundle_id, str) or
                re.fullmatch(r"[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}",
                             self.bundle_id) is None or
                not isinstance(reported_ids, list) or
                len(reported_ids) != len(orders["jobs"]) or
                len(set(reported_ids)) != len(reported_ids)):
            raise ValueError("Hub dispatch did not identify one immutable native resource bundle")
        jobs = {job["id"]: job for job in orders["jobs"]}
        reported = dispatched.get("collectors")
        if not isinstance(reported, list) or len(reported) != len(jobs):
            raise ValueError("Hub dispatch did not return every issued collector")
        self.members = {}
        self.member_positions = {
            job_id: index + 1 for index, job_id in enumerate(reported_ids)
        }
        for row in reported:
            if not isinstance(row, dict) or row.get("collector_id") not in jobs:
                raise ValueError("Hub returned an unissued native collector")
            name = row["collector_id"]
            hub_job = row.get("hub_job_id")
            machine = row.get("machine_id")
            if (name in self.members or not isinstance(hub_job, str) or
                    re.fullmatch(r"[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}",
                                 hub_job) is None or
                    not isinstance(machine, str) or not machine):
                raise ValueError("Hub returned a malformed or duplicate native collector")
            if hub_job not in self.member_positions or any(
                    member[0] == hub_job for member in self.members.values()):
                raise ValueError("Hub collector is duplicated or not in its issued resource bundle")
            self.members[name] = (hub_job, machine, jobs[name]["collection_id"])
        if ({entry[0] for entry in self.members.values()} !=
                set(self.member_positions)):
            raise ValueError("Hub dispatch changed the frozen native resource-bundle roster")
        self.last_checked = float("-inf")
        self.checks = 0
        self.coordinator_unavailable = 0
        self.last_reported_unavailable = False
        # Transient authenticated Hub observations are sampled alongside the
        # existing receipt barrier, not by a second monitoring owner.
        # Store constant-size aggregates per native job attempt.
        self.resource_observations = {}

    @staticmethod
    def _observe_metric(target, key, value):
        if (type(value) not in (int, float) or not math.isfinite(value) or
                value < 0 or value > 1e12):
            return
        metric = target.setdefault(key, {"count": 0, "sum": 0.0, "peak": 0.0})
        metric["count"] += 1
        metric["sum"] += value
        metric["peak"] = max(metric["peak"], value)

    def _observe_resources(self, collector, job, snapshot):
        """Sample fresh Hub-owned process counters and physical-host devices.

        A replacement job receives its own summary; observations never span
        different native process identities or pretend GPUs are job-isolated.
        """
        live = job.get("live_execution")
        if (job.get("status") not in ("running", "stop_requested") or
                not isinstance(live, dict) or
                live.get("state") not in ("running", "stop_requested") or
                live.get("dispatch_id") != job.get("dispatch_id")):
            return
        machine_id = job.get("assigned_machine_id")
        machine = next((item for item in snapshot.get("machines", [])
                        if item.get("id") == machine_id), None)
        if not isinstance(machine, dict) or machine.get("status") != "connected":
            return
        observation_at = live.get("at")
        if not isinstance(observation_at, str) or not observation_at:
            return
        attempts = self.resource_observations.setdefault(collector, {})
        attempt = attempts.setdefault(job["id"], {
            "hub_job_id": job["id"], "machine_id": machine_id,
            "authenticated_observation_count": 0,
            "first_observed_at": observation_at,
            "last_observed_at": None,
            "job_process": {}, "physical_host": {}, "physical_gpus": {},
        })
        # The Hub state API is polled more frequently than authenticated
        # agent heartbeats. Identical source timestamps are one observation,
        # never multiple utilization samples.
        if attempt["last_observed_at"] == observation_at:
            return
        attempt["last_observed_at"] = observation_at
        attempt["authenticated_observation_count"] += 1
        add = self._observe_metric
        process = attempt["job_process"]
        add(process, "cpu_core_equivalents", live.get("cpu_cores_used"))
        add(process, "memory_current_mb", live.get("memory_current_mb"))
        # Normalize only against this job's frozen Hub lease. The ratio
        # measures observed process usage, not host-wide utilization.
        reserved = job.get("resources", {})
        cpu = live.get("cpu_cores_used")
        if type(reserved.get("cpus")) is int and reserved["cpus"] > 0 and (
                type(cpu) in (int, float)):
            add(process, "cpu_lease_utilization_pct",
                100 * cpu / reserved["cpus"])
        memory = live.get("memory_current_mb")
        if type(reserved.get("ram_mb")) is int and reserved["ram_mb"] > 0 and (
                type(memory) in (int, float)):
            add(process, "memory_lease_utilization_pct",
                100 * memory / reserved["ram_mb"])
        add(process, "process_tasks", live.get("process_tasks"))
        add(process, "cpu_throttled_seconds_cumulative",
            live.get("cpu_throttled_seconds"))
        add(process, "cpu_throttle_events_cumulative",
            live.get("cpu_throttle_events"))
        peak_memory = live.get("peak_job_memory_bytes")
        if type(peak_memory) in (int, float):
            add(process, "peak_job_memory_mb", peak_memory / 1048576)
        add(attempt["physical_host"], "cpu_busy_pct",
            machine.get("host_cpu_busy_pct"))

        # Only devices physically bound to this exact Hub lease are sampled.
        # GPU busy and VRAM occupancy describe the WHOLE device, including
        # other processes; they do not measure this job's GPU consumption.
        lease = next((item for item in snapshot.get("reservations", [])
                      if item.get("id") == job.get("lease_id") and
                         item.get("job_id") == job["id"] and
                         item.get("machine_id") == machine_id and
                         item.get("status") in ("offered", "acknowledged")), None)
        if not isinstance(lease, dict):
            return
        devices = machine.get("verified_gpus", [])
        for binding in lease.get("gpu_bindings", []):
            pci = binding.get("pci_address") if isinstance(binding, dict) else None
            device = next((item for item in devices
                           if item.get("pci_address") == pci), None)
            if not pci or device is None:
                continue
            gpu = attempt["physical_gpus"].setdefault(pci, {})
            add(gpu, "host_busy_pct", device.get("gpu_busy_pct"))
            add(gpu, "host_used_vram_mb", device.get("used_vram_mb"))
            add(gpu, "host_power_w", device.get("power_w"))

    def observed_resource_summary(self):
        def summarize(samples):
            return {
                key: ({
                    "sample_count": item["count"],
                    # Already cumulative/high-water counters must not be
                    # presented as average instantaneous usage.
                    "observed_high_water": item["peak"],
                } if key.endswith("_cumulative") or key == "peak_job_memory_mb"
                else {
                    "sample_count": item["count"],
                    "sample_mean": item["sum"] / item["count"],
                    "sample_peak": item["peak"],
                }) for key, item in samples.items() if item["count"]
            }
        return {
            collector: [{
                "hub_job_id": attempt["hub_job_id"],
                "machine_id": attempt["machine_id"],
                "authenticated_observation_count":
                    attempt["authenticated_observation_count"],
                "first_observed_at": attempt["first_observed_at"],
                "last_observed_at": attempt["last_observed_at"],
                "job_process": summarize(attempt["job_process"]),
                "physical_host": summarize(attempt["physical_host"]),
                "physical_gpus": {
                    pci: summarize(values)
                    for pci, values in attempt["physical_gpus"].items()
                },
            } for attempt in attempts.values()]
            for collector, attempts in self.resource_observations.items()
        }

    def __call__(self, missing):
        now = time.monotonic()
        if now - self.last_checked < 5.0:
            return
        self.last_checked = now
        try:
            with urlopen(
                f"http://127.0.0.1:{self.port}/api/hub/state",
                timeout=5.0,
            ) as response:
                # Bound a coordinator response, never ingest arbitrary
                # artifacts or training logs as part of this monitor.
                raw = response.read(16 * 1024 * 1024 + 1)
            if len(raw) > 16 * 1024 * 1024:
                raise ValueError("Hub state exceeds native collection status limit")
            snapshot = json.loads(raw)
        except (OSError, URLError, TimeoutError) as error:
            self.coordinator_unavailable += 1
            if not self.last_reported_unavailable:
                print(json.dumps({
                    "kind": "native_hub_collection_observation_unavailable",
                    "reason": str(error)[:200],
                }), flush=True)
            self.last_reported_unavailable = True
            return
        if not isinstance(snapshot, dict) or not isinstance(snapshot.get("jobs"), list):
            raise ValueError("Hub returned an invalid native collection registry snapshot")
        self.checks += 1
        self.last_reported_unavailable = False
        indexed = {job.get("id"): job for job in snapshot["jobs"]
                   if isinstance(job, dict) and isinstance(job.get("id"), str)}
        for collector in missing:
            job_id, machine, collection_id = self.members[collector]
            job = indexed.get(job_id)
            if not isinstance(job, dict):
                raise ValueError(f"Hub no longer has native collector {collector} ({job_id})")
            params = job.get("frozen_parameters")
            bundle = job.get("resource_bundle")
            if (job.get("project_id") != "bees_native_collect" or
                    job.get("assigned_machine_id") != machine or
                    not isinstance(bundle, dict) or
                    bundle.get("id") != self.bundle_id or
                    bundle.get("index") != self.member_positions[job_id] or
                    bundle.get("size") != len(self.member_positions) or
                    not isinstance(params, dict) or
                    params.get("collector_id") != collector or
                    params.get("collection_id") != collection_id):
                raise ValueError(f"Hub native collector {collector} changed its frozen job identity")
            # A failed collector's native systemd/Job Object identity is
            # terminal, not reusable. The Hub may queue a separately leased
            # replacement on the SAME host and with identical physical
            # source work. Keep the original bundle as the learner's fixed
            # collection identity while following only one explicitly
            # authorized replacement chain in the Hub's durable registry.
            current = job
            visited = {job_id}
            for _ in range(16):
                successors = [
                    candidate for candidate in indexed.values()
                    if candidate.get("collector_replacement_of_job_id") ==
                        current.get("id") and candidate.get("status") != "cancelled"
                ]
                if len(successors) > 1:
                    raise ValueError(
                        f"multiple native replacement owners claim collector {collector}"
                    )
                if not successors:
                    break
                successor = successors[0]
                candidate_id = successor.get("id")
                frozen = successor.get("frozen_parameters")
                predecessor_frozen = current.get("frozen_parameters")
                if (candidate_id in visited or
                        current.get("status") != "failed" or
                        not current.get("capacity_released_at") or
                        successor.get("project_id") != "bees_native_collect" or
                        successor.get("configuration_id") !=
                            current.get("configuration_id") or
                        successor.get("configuration_revision") !=
                            current.get("configuration_revision") or
                        successor.get("configuration_sha256") !=
                            current.get("configuration_sha256") or
                        successor.get("artifact_lineage_root_id") !=
                            (current.get("artifact_lineage_root_id") or current["id"]) or
                        successor.get("continuation_machine_id") != machine or
                        successor.get("assigned_machine_id") not in (None, machine) or
                        frozen != predecessor_frozen or
                        successor.get("model_source_job_id") !=
                            current.get("model_source_job_id") or
                        successor.get("expected_parent_model_sha256") !=
                            current.get("expected_parent_model_sha256") or
                        not isinstance(frozen, dict) or
                        frozen.get("collector_id") != collector or
                        frozen.get("collection_id") != collection_id):
                    raise ValueError(
                        f"Hub replacement for {collector} changed its original physical training contract"
                    )
                previous_lease = snapshot.get("reservations") or []
                if not isinstance(previous_lease, list) or not any(
                    isinstance(lease, dict) and
                    lease.get("id") == current.get("lease_id") and
                    lease.get("job_id") == current.get("id") and
                    lease.get("status") == "released"
                    for lease in previous_lease
                ):
                    raise ValueError(
                        f"Hub replacement for {collector} lacks released predecessor capacity"
                    )
                visited.add(candidate_id)
                current = successor
            else:
                raise ValueError(
                    f"collector {collector} exceeds bounded native replacement lineage"
                )
            status = current.get("status")
            if current.get("assigned_machine_id") is not None and (
                    not isinstance(snapshot.get("reservations"), list) or
                    not any(isinstance(lease, dict) and
                        lease.get("id") == current.get("lease_id") and
                        lease.get("job_id") == current.get("id") and
                        lease.get("machine_id") == machine and
                        lease.get("project_id") == "bees_native_collect" and
                        lease.get("issued_by") == "experiment-hub" and
                        lease.get("status") in ("offered", "acknowledged")
                        for lease in snapshot["reservations"])):
                raise ValueError(
                    f"collector {collector} lost its original-machine Hub lease"
                )
            # Use only the authenticated Hub's fresh native-owner telemetry,
            # after its replacement lineage and lease identity are checked.
            self._observe_resources(collector, current, snapshot)
            # A failed predecessor may await explicit operator approval.
            # Never restart it or accept it as data; simply retain the
            # existing bounded collection barrier until a newly leased
            # replacement publishes a verified bundle.
            if status in ("cancelled", "stopped", "unverified", "finished"):
                reason = str(current.get("status_reason") or "")[:240]
                detail = ("finished without a verified collection delivery"
                          if status == "finished" else f"is {status} in Hub")
                raise RuntimeError(
                    f"native collector {collector} on {machine} "
                    f"({current['id']}) {detail}: {reason}"
                )
        # Hub decisions are append-only. Use only the latest decision for
        # this exact issued resource bundle: a later explicit reauthorization
        # supersedes an older disarm, but a later cancellation cannot be
        # overridden by finding an earlier authorized entry.
        plans = snapshot.get("bundle_start_plans") or []
        if not isinstance(plans, list):
            raise ValueError("Hub returned an invalid native Start decision history")
        relevant = [
            plan for plan in plans
            if isinstance(plan, dict) and plan.get("bundle_id") == self.bundle_id
        ]
        if relevant:
            current = relevant[-1]
            if current.get("status") in (
                    "needs_review", "disarmed", "expired", "cancelled"):
                raise RuntimeError(
                    f"native Hub collection Start requires review: "
                    f"{str(current.get('reason') or current['status'])[:240]}"
                )


def measured_hub_machine_collection(approved_plan, dispatched, bundles,
                                    resource_observations=None):
    """Join verified collector work to Hub-owned machine and lease budgets.

    CPU/RAM/GPU quantities below are reserved capacity, never invented
    utilization. Throughput comes only from verified native bundle receipts.
    This is terminal-only reporting, outside the simulation and learner.
    """
    approved = {row["id"]: row for row in approved_plan["collectors"]}
    assigned = {row["collector_id"]: row for row in dispatched["collectors"]}
    if (len(approved) != len(bundles) or len(assigned) != len(bundles) or
            {bundle["collector_id"] for bundle in bundles} != set(approved) or
            set(assigned) != set(approved)):
        raise ValueError("Hub machine collection measurement lacks an issued collector")
    rows = []
    for bundle in bundles:
        name = bundle["collector_id"]
        allocation = approved[name]
        machine = assigned[name]
        if machine["machine_id"] != allocation["machine_id"]:
            raise ValueError("Hub machine collection changed its approved machine ownership")
        resources = allocation["resources"]
        metrics = bundle["metrics"]
        proof = metrics.get("gpu_inference")
        rows.append({
            "collector_id": name,
            "machine_id": machine["machine_id"],
            "hub_job_id": machine["hub_job_id"],
            "reserved_cpu_slots": resources["cpus"],
            "reserved_ram_mb": resources["ram_mb"],
            "reserved_gpu_count": resources.get(
                "gpu_count", len(resources.get("gpus", [])),
            ),
            "rollout_processes": bundle["rollout_processes"],
            "physics_workers_per_process": bundle["physics_workers_per_process"],
            "observed_world_steps": metrics["successful_world_steps"],
            "observed_policy_rows": metrics["successful_policy_rows"],
            "collector_execution_wall_seconds": metrics["wall_seconds"],
            **({} if not resource_observations or
                name not in resource_observations else {
                "hub_authenticated_resource_observations":
                    resource_observations[name],
            }),
            "observed_world_steps_per_collector_wall_second":
                metrics["successful_world_steps"] / metrics["wall_seconds"],
            **({"verified_gpu_model_forwards": proof["model_forwards"],
                "verified_gpu_agent_rows": proof["agent_rows"]}
               if isinstance(proof, dict) else {}),
        })
    return rows


def pinned(path):
    path = path.resolve(strict=True)
    if not path.is_file():
        raise ValueError(f"expected a file: {path}")
    return {"path": str(path), "sha256": file_sha256(path)}


def read_json(path):
    return json.loads(path.read_text(encoding="utf-8"))


def native_input_dependencies(*runs):
    """Pin scene, Stage and policy-worker files actually used by each Level.

    The run configuration already pins embedded native_stage bytes. Legacy
    Stage files and referenced scene JSON are separate immutable inputs.
    Do this once per session, not once per world or fixed physics tick.
    """
    paths = set()
    for source in runs:
        if source is None:
            continue
        base = Path(source["path"]).parent
        config = read_json(Path(source["path"]))
        if not isinstance(config, dict) or not isinstance(config.get("levels"), list):
            raise ValueError("native session source run must contain Levels")
        for level in config["levels"]:
            if not isinstance(level, dict) or not isinstance(level.get("scene"), str) or not level["scene"]:
                raise ValueError("native session Level has no scene path")
            paths.add((base / level["scene"]).resolve(strict=True))
        for key in ("policy_worker", "collision_matrix", "stage_palette"):
            value = config.get(key)
            if value is None:
                continue
            if not isinstance(value, str) or not value:
                raise ValueError(f"native session has an invalid {key} path")
            paths.add((base / value).resolve(strict=True))
    return [pinned(path) for path in sorted(paths)]


def check_native_input_dependencies(recipe):
    for item in recipe.get("source_dependencies", []):
        if file_sha256(Path(item["path"])) != item["sha256"]:
            raise ValueError(f"native source changed during training session: {item['path']}")


@contextmanager
def session_lock(root):
    # Native flock / locking are released automatically if the controller
    # crashes. No stale PID files or second session authority are introduced.
    with (root / ".session.lock").open("a+b") as stream:
        stream.seek(0)
        if not stream.read(1):
            stream.seek(0)
            stream.write(b"1")
            stream.flush()
        stream.seek(0)
        if os.name == "nt":
            import msvcrt
            msvcrt.locking(stream.fileno(), msvcrt.LK_NBLCK, 1)
        else:
            import fcntl
            fcntl.flock(stream.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
        try:
            yield
        finally:
            stream.seek(0)
            if os.name == "nt":
                msvcrt.locking(stream.fileno(), msvcrt.LK_UNLCK, 1)
            else:
                fcntl.flock(stream.fileno(), fcntl.LOCK_UN)


def scheduled_opponent_slot(self_play, round_number):
    return (
        self_play["opponent_slot"]
        if self_play is not None and round_number % self_play["every"] == 0
        else None
    )


def verified_optimizer_group(record):
    """Validate one completed native GPU group's globally agreed update.

    A multi-node Hub publication may be imported into a later training
    session; never infer checkpoint consensus from a mere aggregate worker
    count. This reads only the completed round receipt, with no GPU work.
    """
    metrics = record.get("learner_metrics")
    if not isinstance(metrics, dict):
        raise ValueError("native training round lacks learner measurements")
    workers = metrics.get("optimizer_gpu_workers", 0)
    nodes = metrics.get("optimizer_gpu_nodes", 0)
    local = metrics.get("optimizer_gpu_local_workers", 0)
    if workers == 0:
        if nodes != 0 or local != 0:
            raise ValueError("CPU optimizer claims an inconsistent GPU group")
        return
    if (type(workers) is not int or type(nodes) is not int or
            type(local) is not int or not 1 <= nodes <= 8 or
            local not in (1, 2) or workers != nodes * local):
        raise ValueError("native optimizer GPU allocation disagrees with its nodes")

    if workers == 1:
        return
    rows = metrics.get("optimizer_gpu_per_device")
    updates = metrics.get("optimizer_synchronized_minibatches")
    model = metrics.get("optimizer_gpu_model_consensus_sha256")
    if (not isinstance(rows, list) or len(rows) != workers or
            type(updates) is not int or updates <= 0 or
            metrics.get("optimizer_masked_loss_normalizations") != updates or
            not isinstance(model, str) or re.fullmatch(r"[0-9a-f]{64}", model) is None):
        raise ValueError("native optimizer has no completed synchronized GPU evidence")
    for rank, row in enumerate(rows):
        if (not isinstance(row, dict) or
                row.get("rank") != rank or
                row.get("node_rank") != rank // local or
                row.get("local_rank") != rank % local or
                row.get("gradient_syncs") != updates or
                row.get("masked_loss_syncs") != updates or
                row.get("device") != f"cuda:{rank % local}"):
            raise ValueError("native optimizer GPU rank lacks the agreed minibatch evidence")

    if nodes > 1:
        group = metrics.get("optimizer_group_id")
        identity = metrics.get("optimizer_global_input_sha256")
        if (not isinstance(group, str) or
                re.fullmatch(r"[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}", group) is None or
                not isinstance(identity, str) or
                re.fullmatch(r"[0-9a-f]{64}", identity) is None or
                record.get("optimizer_group_id") != group):
            raise ValueError("native optimizer checkpoint lacks its authorized group and shared source identity")
    elif metrics.get("optimizer_group_id") is not None:
        raise ValueError("local native GPU optimizer claims a remote Hub group")


def verify_round(directory, manifest_sha, checkpoint_sha, learner_slot,
                 refresh_opponent_slot=None, source_run_sha=None):
    receipt_path = directory / "round-result.json"
    record = read_json(receipt_path)
    if (record.get("kind") != "native_training_round_complete" or
            record.get("schema_version") != 1 or
            record.get("input_manifest_sha256") != manifest_sha or
            record.get("input_checkpoint_sha256") != checkpoint_sha or
            record.get("learner_metrics", {}).get("learner_slot") != learner_slot or
            record.get("learner_metrics", {}).get("refreshed_opponent_slot") !=
                refresh_opponent_slot):
        raise ValueError(f"round completion receipt does not match its lineage: {receipt_path}")
    if source_run_sha is not None and record.get("source_run_config_sha256") != source_run_sha:
        raise ValueError(f"round belongs to a different curriculum stage: {receipt_path}")
    checkpoint = directory / "learner" / "checkpoint.pt"
    manifest = directory / "learner" / "manifest.json"
    for key, path in (("next_checkpoint", checkpoint), ("next_manifest", manifest)):
        if Path(record[key]).resolve() != path.resolve(strict=True):
            raise ValueError(f"round receipt points outside its own learner: {receipt_path}")
        if file_sha256(path) != record[key + "_sha256"]:
            raise ValueError(f"round completion artifact changed: {path}")
    metrics = record["learner_metrics"]
    verified_optimizer_group(record)
    if (metrics.get("next_checkpoint_sha256") != record["next_checkpoint_sha256"] or
            metrics.get("next_manifest_sha256") != record["next_manifest_sha256"] or
            metrics.get("input_checkpoint_sha256") != checkpoint_sha or
            metrics.get("input_manifest_sha256") != manifest_sha or
            type(metrics.get("optimizer_input_steps")) is not int or
            metrics["optimizer_input_steps"] <= 0):
        raise ValueError(f"optimizer metrics do not identify these artifacts: {receipt_path}")
    check_learner(manifest, checkpoint, learner_slot)
    if refresh_opponent_slot is not None:
        models = read_json(manifest)["models"]
        promoted = [
            model for model in models if model.get("slot") == refresh_opponent_slot
        ]
        if (len(promoted) != 1 or
                promoted[0].get("backend") != "mlagents" or
                promoted[0].get("sha256") != record["next_checkpoint_sha256"] or
                (manifest.parent / promoted[0].get("path", "")).resolve(strict=True) !=
                    checkpoint.resolve() or
                metrics.get("refreshed_opponent_from_checkpoint_sha256") !=
                    record["next_checkpoint_sha256"]):
            raise ValueError(f"native self-play opponent promotion has invalid model lineage: {receipt_path}")
    if "collection_orders" in record:
        work_order = Path(record["collection_orders"])
        if (not work_order.is_file() or
                file_sha256(work_order) != record["collection_orders_sha256"]):
            raise ValueError(f"distributed collection provenance changed: {directory}")
        for bundle in record.get("rollout_bundles", []):
            if file_sha256(Path(bundle["path"])) != bundle["sha256"]:
                raise ValueError(f"distributed rollout receipt changed: {bundle['path']}")
    return record, manifest, checkpoint


def existing_rounds(root, initial_manifest, initial_checkpoint, learner_slot,
                    self_play=None, curriculum=None, base_run=None):
    attempts = defaultdict(list)
    for path in root.iterdir():
        match = ATTEMPT.fullmatch(path.name)
        if match:
            if not path.is_dir():
                raise ValueError(f"training attempt path is not a directory: {path}")
            number, attempt = map(int, match.groups())
            if number == 0 or attempt == 0:
                raise ValueError(f"invalid training attempt number: {path}")
            attempts[number].append((attempt, path))
    manifest, checkpoint = initial_manifest, initial_checkpoint
    manifest_sha, checkpoint_sha = file_sha256(manifest), file_sha256(checkpoint)
    completed = []
    next_attempt = 1
    highest = max(attempts, default=0)
    for number in range(1, highest + 1):
        candidates = sorted(attempts.get(number, []))
        valid = [path for _, path in candidates if (path / "round-result.json").is_file()]
        if len(valid) > 1:
            raise ValueError(f"multiple completed attempts exist for round {number}")
        if not valid:
            if number != highest:
                raise ValueError(f"missing committed round {number} before a later attempt")
            next_attempt = max((index for index, _ in candidates), default=0) + 1
            # The optimizer has an independent final consensus boundary.
            # A crash between writing its complete metrics and the outer
            # round receipt can be finalized without reapplying gradients.
            # Never salvage missing metrics, partial checkpoints, or node
            # results: the entire distributed group must begin a NEW
            # authorized attempt in those cases.
            if candidates:
                last_index, last_directory = candidates[-1]
                if ((last_directory / "optimizer-attempt.json").is_file() and
                        (last_directory / "learner" / "metrics.json").is_file() and
                        (last_directory / "learner" / "checkpoint.pt").is_file() and
                        (last_directory / "learner" / "manifest.json").is_file()):
                    next_attempt = last_index
            break
        # Source identity is part of every optimizer round, not only
        # curriculum transitions. Reject a different native Level even
        # when the model/manifest checkpoint lineage happens to match.
        expected_source = (
            selected_curriculum_run(base_run, curriculum, number)[0]["sha256"]
            if curriculum is not None else
            base_run["sha256"] if base_run is not None else None
        )
        record, manifest, checkpoint = verify_round(
            valid[0], manifest_sha, checkpoint_sha, learner_slot,
            scheduled_opponent_slot(self_play, number), expected_source,
        )
        completed.append((valid[0], record))
        manifest_sha = record["next_manifest_sha256"]
        checkpoint_sha = record["next_checkpoint_sha256"]
    else:
        next_attempt = 1
    return completed, manifest, checkpoint, next_attempt


def verify_evaluation(directory, manifest_sha, source_sha, learner_side, policy_seed,
                      inference_device=None):
    result = read_json(directory / "evaluation-result.json")
    summary_path = directory / "rollout-summary.json"
    if (result.get("kind") != "native_policy_evaluation_complete" or
            result.get("schema_version") != 1 or
            result.get("model_manifest_sha256") != manifest_sha or
            result.get("source_run_config_sha256") != source_sha or
            result.get("learner_side") != learner_side or
            result.get("policy_sampling_seed") != policy_seed or
            result.get("inference_device_override") != inference_device or
            result.get("rollout_summary_sha256") != file_sha256(summary_path)):
        raise ValueError(f"holdout evaluation does not match its pinned inputs: {directory}")
    summary = read_json(summary_path)
    if (summary.get("run_id") != result.get("run_id") or
            summary.get("policy_manifest_sha256") != manifest_sha or
            summary.get("completed_episodes") != result.get("episodes") or
            summary.get("evaluation") != result.get("evaluation") or
            result.get("side_victories") != summary.get("evaluation", {}).get(
                "victories_by_side", {}).get(str(learner_side), 0)):
        raise ValueError(f"holdout summary differs from published evaluation: {directory}")
    # New native summaries contain authoritative per-scene terminal
    # counters. Verify the published rates from those counters, rather
    # than accepting a standalone receipt's self-reported averages.
    if "by_scene_sha256" in summary.get("evaluation", {}):
        from evaluate_policy import per_scene_results
        scenes = per_scene_results(
            summary["evaluation"], learner_side, result["episodes"],
        )
        if result.get("by_scene_sha256") != scenes:
            raise ValueError(f"holdout scene outcomes differ from durable summary: {directory}")
    elif result.get("by_scene_sha256") is not None:
        raise ValueError(f"holdout scene outcomes lack source terminal evidence: {directory}")
    if all("mission_attempts" in scene and
           scene["mission_attempts"] == scene["episodes"]
           for scene in result.get("by_scene_sha256", {}).values()) and (
               result.get("by_scene_sha256")
           ):
        verified_mission_holdout(result)
    elif any(key in result for key in (
            "mission_attempts", "learner_mission_successes",
            "learner_mission_success_rate")):
        raise ValueError(f"partial native goal results cannot authenticate a holdout: {directory}")
    return result


def fixed_reference_evaluation_manifest(round_directory, manifest, recipe):
    """Evaluate the learner against the SAME initial opponent in every round.

    The live training manifest periodically upgrades its opponent slot for
    self-play. Reusing that mutable opponent in the holdout would invalidate
    cross-round win-rate comparisons, despite identical environment seeds.
    This derived manifest changes no training checkpoint or model routing.
    """
    initial = recipe["initial_manifest"]
    original_path = Path(initial["path"])
    if file_sha256(original_path) != initial["sha256"]:
        raise ValueError("initial reference opponent manifest changed")
    current = read_json(manifest)
    reference = read_json(original_path)
    if any(current.get(field) != reference.get(field)
           for field in ("format", "abi_version", "policy_signature")):
        raise ValueError("reference opponent and learner use different native policy ABIs")
    if not isinstance(current.get("models"), list) or not isinstance(reference.get("models"), list):
        raise ValueError("native evaluation requires verified model slot lists")
    slot = recipe["self_play"]["opponent_slot"]
    opponents = [entry for entry in reference["models"] if entry.get("slot") == slot]
    if len(opponents) != 1:
        raise ValueError("initial reference opponent slot is missing or ambiguous")
    source_model = opponents[0]
    reference_file = (original_path.parent / source_model["path"]).resolve(strict=True)
    if file_sha256(reference_file) != source_model["sha256"]:
        raise ValueError("initial reference opponent weights changed")

    found = False
    for model in current["models"]:
        identity = model.get("slot")
        if identity == slot:
            model.clear()
            model.update({**source_model, "path": str(reference_file)})
            found = True
            continue
        artifact = (manifest.parent / model["path"]).resolve(strict=True)
        if file_sha256(artifact) != model["sha256"]:
            raise ValueError(f"native holdout model weights changed: slot {identity}")
        # The derived manifest lives in a different directory from the
        # trainer's emitted manifest; preserve absolute artifact identity.
        model["path"] = str(artifact)
    if not found:
        raise ValueError("current native model manifest omitted the opponent slot")
    output = round_directory / "fixed-reference-manifest.json"
    if output.exists():
        if read_json(output) != current:
            raise ValueError("pinned reference holdout manifest changed on resume")
    else:
        atomic_json(output, current)
    return output


def ensure_evaluation(round_directory, manifest, recipe):
    eval_source = recipe["evaluation_config"]
    source_sha = eval_source["sha256"]
    if file_sha256(Path(eval_source["path"])) != source_sha:
        raise ValueError("holdout evaluation configuration changed during session")
    if "self_play" in recipe:
        manifest = fixed_reference_evaluation_manifest(round_directory, manifest, recipe)
    manifest_sha = file_sha256(manifest)
    attempts = []
    for path in round_directory.iterdir():
        match = EVALUATION_ATTEMPT.fullmatch(path.name)
        if match:
            if not path.is_dir():
                raise ValueError(f"invalid holdout attempt path: {path}")
            attempts.append((int(match.group(1)), path))
    completed = [path for _, path in attempts if (path / "evaluation-result.json").is_file()]
    if len(completed) > 1:
        raise ValueError(f"more than one completed evaluation for {round_directory}")
    if completed:
        return verify_evaluation(completed[0], manifest_sha, source_sha,
                                 recipe["evaluation_side"], recipe["evaluation_policy_seed"],
                                 recipe.get("evaluation_inference_device"))
    attempt = max((number for number, _ in attempts), default=0) + 1
    if attempt > 999:
        raise ValueError("holdout evaluation exhausted its attempt numbers")
    output = round_directory / f"evaluation-attempt-{attempt:03d}"
    command = [sys.executable, str(Path(__file__).with_name("evaluate_policy.py")),
               eval_source["path"], str(manifest), str(output),
               "--learner-side", str(recipe["evaluation_side"]),
               "--policy-seed", str(recipe["evaluation_policy_seed"])]
    if recipe["rollout_binary"] is not None:
        command.extend(["--rollout-binary", recipe["rollout_binary"]["path"]])
    if "evaluation_physics_workers" in recipe:
        command.extend(["--physics-workers", str(recipe["evaluation_physics_workers"])])
    if "evaluation_inference_device" in recipe:
        command.extend(["--inference-device", recipe["evaluation_inference_device"]])
    print(json.dumps({"kind":"native_training_session_evaluation_started",
                      "round_directory":str(round_directory), "attempt":attempt}),flush=True)
    launch_round(command, round_directory / f"evaluation-attempt-{attempt:03d}.log")
    return verify_evaluation(output, manifest_sha, source_sha,
                             recipe["evaluation_side"], recipe["evaluation_policy_seed"],
                             recipe.get("evaluation_inference_device"))



def evaluation_due(index, target, recipe):
    """Always evaluate the final completed candidate, even off cadence.

    Already-issued holdout attempts are also completed on recovery;
    changing --rounds must not strand a previously committed evaluation.
    """
    return index % recipe.get("evaluation_every", 1) == 0 or index == target


def evaluation_attempt_exists(directory):
    return any(EVALUATION_ATTEMPT.fullmatch(path.name)
               for path in directory.iterdir())


def evaluate_monitor_suites(directory, manifest, recipe, *, due):
    """Run independent, immutable monitoring suites for this exact model.

    Each suite gets its own interrupted-attempt ledger and source hash.
    The primary holdout alone owns best-checkpoint and stopping decisions;
    a monitor is evidence, not an implicit change of training objective.
    """
    evaluated = {}
    for number, suite in enumerate(recipe.get("monitor_evaluations", []), 1):
        suite_id = f"monitor-{number:03d}"
        home = directory / suite_id
        if not due and not (home.is_dir() and evaluation_attempt_exists(home)):
            continue
        if not home.exists():
            home.mkdir()
        if not home.is_dir():
            raise ValueError(f"native monitor suite output is not a directory: {home}")
        derived = {**recipe, "evaluation_config": suite}
        evaluated[suite_id] = ensure_evaluation(home, manifest, derived)
    return evaluated


def measured_distributed_collection_windows(completed):
    """Only receipt-matched observed fleet windows contribute to fleet SPS.

    A recovered round may have no original order-start timing, and older
    sessions may have no window record. Neither absence nor a malformed
    ancillary timing record turns independent worker seconds into fleet SPS.
    """
    measured = []
    missing = []
    rejected = []
    distributed = 0
    for number, (directory, record) in enumerate(completed, 1):
        if "collection_orders" not in record:
            continue
        distributed += 1
        window_path = directory.parent / f"collection-round-{number:05d}-window.json"
        if not window_path.is_file():
            missing.append(number)
            continue
        try:
            window = read_json(window_path)
            seconds = window.get("work_order_to_last_receipt_wall_seconds")
            steps = window.get("useful_world_steps")
            rows = window.get("useful_policy_rows")
            if (window.get("schema_version") != 1 or
                    window.get("kind") != "native_distributed_collection_window" or
                    window.get("round") != number or
                    window.get("orders_sha256") != record["collection_orders_sha256"] or
                    window.get("collector_count") != record["rollout_metrics"]["collector_count"] or
                    type(steps) is not int or steps <= 0 or
                    type(rows) is not int or rows <= 0 or
                    steps != record["rollout_metrics"]["successful_world_steps"] or
                    rows != record["rollout_metrics"]["successful_policy_rows"] or
                    type(seconds) not in (float, int) or
                    not math.isfinite(seconds) or seconds <= 0):
                raise ValueError("window identity or measured work differs from round receipt")
        except (OSError, ValueError, TypeError, KeyError, AttributeError):
            rejected.append(number)
            continue
        # Export, Hub dispatch and collection-barrier timing are sequential
        # subwindows of the measured order-to-receipt interval. In contrast,
        # upload/verification calls and independent collector wall time may
        # overlap. Never add either of those to fleet critical-path time.
        stage_keys = (
            "collector_source_export_wall_seconds",
            "hub_dispatch_wall_seconds",
            "collector_barrier_wall_seconds",
        )
        stages = [window.get(key) for key in stage_keys]
        stage_breakdown = None
        if all(type(value) in (int, float) and math.isfinite(value) and value >= 0
               for value in stages) and sum(stages) <= seconds + 0.01:
            stage_breakdown = {
                "source_export_wall_seconds": stages[0],
                "hub_dispatch_wall_seconds": stages[1],
                "collector_barrier_wall_seconds": stages[2],
                "other_coordinator_wall_seconds":
                    max(0.0, seconds - sum(stages)),
            }
        network = {}
        for key in (
            "collector_input_bytes_served",
            "collector_output_bytes_received",
            "collector_uploads_received",
            "collector_upload_and_verify_wall_seconds",
        ):
            value = window.get(key)
            if (type(value) in (int, float) and math.isfinite(value) and
                    value >= 0):
                network[key] = value
        measured.append({
            "round": number,
            "world_steps": steps,
            "policy_rows": rows,
            "work_order_to_last_receipt_wall_seconds": seconds,
            "world_steps_per_wall_second": steps / seconds,
            "policy_rows_per_wall_second": rows / seconds,
            **({"coordinator_critical_path": stage_breakdown}
               if stage_breakdown is not None else {}),
            **({"transfer_activity": network} if network else {}),
        })
    if distributed == 0:
        return None
    seconds = sum(row["work_order_to_last_receipt_wall_seconds"] for row in measured)
    steps = sum(row["world_steps"] for row in measured)
    rows = sum(row["policy_rows"] for row in measured)
    return {
        "scope": "work_order_issued_to_final_collector_receipt_including_dispatch_and_transfer",
        "distributed_rounds": distributed,
        "measured_rounds": len(measured),
        "missing_window_rounds": missing,
        "rejected_window_rounds": rejected,
        "useful_world_steps": steps,
        "useful_policy_rows": rows,
        "measured_wall_seconds": seconds,
        "world_steps_per_wall_second": steps / seconds if seconds > 0 else None,
        "policy_rows_per_wall_second": rows / seconds if seconds > 0 else None,
        "rounds": measured,
    }


def measured_learner_pipeline(completed):
    """Attribute verified MA-POCA learner time without timing GPU kernels twice.

    Trajectory read/ingest are subdivisions of trajectory processing, not
    independent additive phases. Gradient submission on multiple GPU ranks
    can overlap; this report uses the publishing learner's measured update
    wall time, with full per-rank evidence in the original round receipt.
    """
    phases = (
        "total_wall_seconds", "trajectory_processing_wall_seconds",
        "capture_read_and_validation_wall_seconds",
        "trajectory_assembly_and_ingest_wall_seconds",
        "optimizer_update_wall_seconds",
        "total_cpu_seconds", "trajectory_processing_cpu_seconds",
        "optimizer_update_cpu_seconds",
    )
    measured, missing = [], []
    for number, (_, receipt) in enumerate(completed, 1):
        metrics = receipt["learner_metrics"]
        if any(type(metrics.get(key)) not in (int, float) or
               not math.isfinite(metrics[key]) or metrics[key] < 0
               for key in phases):
            missing.append(number)
            continue
        total = metrics["total_wall_seconds"]
        preparation = metrics["trajectory_processing_wall_seconds"]
        update = metrics["optimizer_update_wall_seconds"]
        read = metrics["capture_read_and_validation_wall_seconds"]
        ingest = metrics["trajectory_assembly_and_ingest_wall_seconds"]
        if (total <= 0 or update <= 0 or preparation > total or
                preparation + update > total + 0.01 or
                read + ingest > preparation + 0.01):
            missing.append(number)
            continue
        remainder = max(0.0, total - preparation - update)
        measured.append({
            "round": number,
            "optimizer_device": metrics["optimizer_device"],
            "optimizer_input_steps": metrics["optimizer_input_steps"],
            "learner_wall_seconds": total,
            "trajectory_processing_wall_seconds": preparation,
            "capture_read_and_validation_wall_seconds": read,
            "trajectory_assembly_and_ingest_wall_seconds": ingest,
            "other_trajectory_processing_wall_seconds":
                max(0.0, preparation - read - ingest),
            "optimizer_update_wall_seconds": update,
            "initialization_and_checkpoint_publication_wall_seconds": remainder,
            "learner_process_cpu_seconds": metrics["total_cpu_seconds"],
            "trajectory_processing_process_cpu_seconds":
                metrics["trajectory_processing_cpu_seconds"],
            "optimizer_update_process_cpu_seconds":
                metrics["optimizer_update_cpu_seconds"],
            "optimizer_input_steps_per_update_wall_second":
                metrics["optimizer_input_steps"] / update,
        })
    sum_phase = lambda name: sum(row[name] for row in measured)
    total_steps = sum_phase("optimizer_input_steps")
    total_update = sum_phase("optimizer_update_wall_seconds")
    total_wall = sum_phase("learner_wall_seconds")
    return {
        "scope": "publishing_learner_process_not_gpu_rank_sum_or_collector_wall_time",
        "completed_rounds": len(completed),
        "measured_rounds": len(measured),
        "unmeasured_rounds": missing,
        "measured_optimizer_input_steps": total_steps,
        "sum_learner_wall_seconds": total_wall,
        "sum_trajectory_processing_wall_seconds":
            sum_phase("trajectory_processing_wall_seconds"),
        "sum_capture_read_and_validation_wall_seconds":
            sum_phase("capture_read_and_validation_wall_seconds"),
        "sum_trajectory_assembly_and_ingest_wall_seconds":
            sum_phase("trajectory_assembly_and_ingest_wall_seconds"),
        "sum_optimizer_update_wall_seconds": total_update,
        "sum_initialization_and_checkpoint_publication_wall_seconds":
            sum_phase("initialization_and_checkpoint_publication_wall_seconds"),
        "sum_learner_process_cpu_seconds":
            sum_phase("learner_process_cpu_seconds"),
        "sum_optimizer_update_process_cpu_seconds":
            sum_phase("optimizer_update_process_cpu_seconds"),
        "optimizer_input_steps_per_update_wall_second":
            total_steps / total_update if total_update > 0 else None,
        "optimizer_input_steps_per_learner_wall_second":
            total_steps / total_wall if total_wall > 0 else None,
        "rounds": measured,
    }


def early_stop_decision(completed, evaluations, baseline_holdout, recipe):
    """Stop only on verified, comparable fixed-reference holdout observations.

    Patience counts evaluations, not optimizer rounds. Skipping a holdout
    must neither count as a failed policy nor silently reset patience.
    All scheduled evaluations are required; resumption verifies the same
    immutable receipts and computes the same decision without a cursor.
    """
    policy = recipe.get("early_stop")
    if policy is None:
        return None
    if baseline_holdout is None:
        raise ValueError("early stopping requires a verified baseline holdout")
    episodes = baseline_holdout["episodes"]
    mission_based = recipe.get("checkpoint_selection", "").startswith("mission")
    anchor_wins = (verified_mission_holdout(baseline_holdout) if mission_based
                   else baseline_holdout["side_victories"])
    best_wins = anchor_wins
    last_meaningful_round = 0
    last_meaningful_evaluation = 0
    evaluated = 0
    every = recipe.get("evaluation_every", 1)
    # An early stop on the easy scene must not prevent the scheduled
    # harder environments from ever running. Once the final stage begins,
    # patience starts anew; checkpoint improvements remain measured against
    # the one fixed reference holdout.
    curriculum = recipe.get("curriculum")
    final_stage_start = (curriculum["stages"][-1]["start_round"]
                         if curriculum is not None else 1)
    for index, (directory, _) in enumerate(completed, 1):
        if curriculum is not None and index == final_stage_start:
            last_meaningful_evaluation = evaluated
        result = evaluations.get(str(directory))
        if result is None:
            if index % every == 0:
                raise ValueError(f"missing scheduled native holdout: {directory}")
            continue
        if (result["episodes"] != episodes or
                result["learner_side"] != baseline_holdout["learner_side"]):
            raise ValueError("early stopping cannot compare different holdout conditions")
        evaluated += 1
        wins = (verified_mission_holdout(result) if mission_based
                else result["side_victories"])
        best_wins = max(best_wins, wins)
        if wins >= anchor_wins + policy["minimum_extra_victories"]:
            anchor_wins = wins
            last_meaningful_round = index
            last_meaningful_evaluation = evaluated
    no_gain = evaluated - last_meaningful_evaluation
    stop = (len(completed) >= max(policy["minimum_rounds"], final_stage_start) and
            no_gain >= policy["patience_rounds"])
    return {
        "kind": "native_training_holdout_stopping",
        "enabled": True,
        "stop": stop,
        "reason": "fixed_reference_holdout_plateau" if stop else None,
        "evaluated_rounds": evaluated,
        "completed_rounds": len(completed),
        "holdout_interval_rounds": every,
        "minimum_rounds": policy["minimum_rounds"],
        **({"curriculum_final_stage_start_round": final_stage_start}
           if curriculum is not None else {}),
        "patience_rounds": policy["patience_rounds"],
        "metric": ("role_adjusted_physical_mission_successes" if mission_based
                   else "fixed_reference_side_victories"),
        "minimum_extra_victories": (policy["minimum_extra_victories"]
                                    if not mission_based else None),
        "minimum_extra_goal_successes": (policy["minimum_extra_victories"]
                                         if mission_based else None),
        "best_holdout_victories": best_wins if not mission_based else None,
        "best_holdout_goal_successes": best_wins if mission_based else None,
        "holdout_episodes": episodes,
        "last_meaningful_gain_round": last_meaningful_round,
        "evaluations_without_meaningful_gain": no_gain,
    }


def progress_snapshot(completed, target, evaluations=None, baseline_holdout=None,
                      best_evaluated=None, stopping=None, recipe=None,
                      latest_playable=None, monitor_baseline=None,
                      monitor_observations=None):
    evaluations = evaluations or {}
    monitor_baseline = monitor_baseline or {}
    monitor_observations = monitor_observations or {}
    monitor_report = []
    for number, suite in enumerate(
        (recipe or {}).get("monitor_evaluations", []), 1
    ):
        suite_id = f"monitor-{number:03d}"
        baseline = monitor_baseline.get(suite_id)
        if baseline is None:
            raise ValueError(f"missing baseline for monitored native suite {suite_id}")
        observations = [
            (index, monitor_observations[str(directory)][suite_id])
            for index, (directory, _) in enumerate(completed, 1)
            if suite_id in monitor_observations.get(str(directory), {})
        ]
        for _, result in observations:
            if (result["episodes"] != baseline["episodes"] or
                    result["source_run_config_sha256"] != suite["sha256"] or
                    result["learner_side"] != baseline["learner_side"] or
                    result["policy_sampling_seed"] != baseline["policy_sampling_seed"]):
                raise ValueError(f"incomparable monitored native suite: {suite_id}")
        latest_round, latest = observations[-1] if observations else (None, None)
        monitor_report.append({
            "suite_id": suite_id,
            "source_run_config": suite["path"],
            "source_run_config_sha256": suite["sha256"],
            "learner_side": baseline["learner_side"],
            "policy_sampling_seed": baseline["policy_sampling_seed"],
            "baseline": baseline,
            "latest": latest,
            "latest_round": latest_round,
            "evaluated_rounds": len(observations),
            "latest_win_rate_gain": (
                latest["side_win_rate"] - baseline["side_win_rate"]
                if latest is not None else None
            ),
            "results": [
                {"round": index, "receipt": receipt}
                for index, receipt in observations
            ],
        })
    world_steps = sum(record["rollout_metrics"]["successful_world_steps"]
                      for _, record in completed)
    policy_rows = sum(record["rollout_metrics"]["successful_policy_rows"]
                      for _, record in completed)
    rollout_seconds = sum(record["rollout_metrics"]["wall_seconds"]
                          for _, record in completed)
    # Rollout receipts are the source of truth. Legacy rounds lack
    # physical worker measurements, so count those separately rather
    # than presenting absent history as observed serial execution.
    measured_physics = [
        record["rollout_metrics"] for _, record in completed
        if "parallel_physics_rounds" in record["rollout_metrics"]
    ]
    # Collector-side policy encoding and learner-side optimizer decisions
    # are distinct useful-work denominators. Old receipts are unmeasured,
    # not silently treated as zero mission decisions.
    measured_observations = [
        record["rollout_metrics"]["policy_observations"]
        for _, record in completed
        if isinstance(record["rollout_metrics"].get("policy_observations"), dict)
    ]
    mission_coverage = [
        observed for observed in measured_observations
        if "native_mission_rows_by_kind" in observed
    ]
    measured_learner_kinds = [
        record["learner_metrics"]["native_mission_learner_decisions_by_kind"]
        for _, record in completed
        if isinstance(
            record["learner_metrics"].get("native_mission_learner_decisions_by_kind"), dict,
        )
    ]
    measured_learner_goals = [
        record["learner_metrics"]["native_mission_learner_decisions"]
        for _, record in completed
        if type(record["learner_metrics"].get("native_mission_learner_decisions")) is int
    ]
    optimizer_steps = sum(record["learner_metrics"]["optimizer_input_steps"]
                          for _, record in completed)
    # Preserve measurement coverage: older round receipts did not
    # distinguish grouped reinforcement admissions from ordinary policy
    # ship deployments or reused physical Agent trajectories.
    grouped_admissions = [
        row["rollout_metrics"]["reinforcement_arrivals"]["group_agents"]
        for _, row in completed
        if isinstance(row["rollout_metrics"].get("reinforcement_arrivals"), dict)
        and type(row["rollout_metrics"]["reinforcement_arrivals"].get("group_agents")) is int
    ]
    recycled_bindings = [
        row["learner_metrics"]["recycled_policy_bindings"]
        for _, row in completed
        if type(row["learner_metrics"].get("recycled_policy_bindings")) is int
    ]
    ungrouped_decisions = [
        row["learner_metrics"]["capture_ungrouped_policy_decisions"]
        for _, row in completed
        if type(row["learner_metrics"].get("capture_ungrouped_policy_decisions")) is int
    ]
    synchronized_gpu_rounds = [
        (number, record["learner_metrics"])
        for number, (_, record) in enumerate(completed, 1)
        if record["learner_metrics"].get("optimizer_gpu_workers", 0) > 1
    ]
    optimizer_seconds = sum(record["learner_metrics"]["total_wall_seconds"]
                            for _, record in completed)
    # These are the simulator's real sampled model-forward calls, not
    # estimated GPU utilization or complete inference wall time. Worker
    # samples can overlap in parallel collections: retain a sum-of-calls
    # scope instead of dividing by entire-fleet wall time.
    inference_samples = [
        record["rollout_metrics"]["sampled_model_timing"]
        for _, record in completed
        if "sampled_model_timing" in record["rollout_metrics"]
    ]
    sampled_calls = sum(sample["calls"] for sample in inference_samples)
    sampled_rows = sum(sample["rows"] for sample in inference_samples)
    sampled_seconds = sum(sample["wall_seconds"] for sample in inference_samples)
    latest = completed[-1][1]["learner_metrics"].get("training_outcomes", {}) if completed else {}
    rollout_scope = (
        "sum_of_independent_collector_wall_seconds"
        if any("collection_orders" in record for _, record in completed)
        else "local_rollout_wall_seconds"
    )
    fleet_window = measured_distributed_collection_windows(completed)
    stage_report = (curriculum_stage_metrics(
        recipe["run_config"], recipe["curriculum"], completed,
        evaluations or {}, fleet_window,
    ) if recipe is not None and "curriculum" in recipe else None)
    # An optimizer round cannot overlap its own collection window. Local
    # rollout wall seconds are directly measured; multi-machine collector
    # seconds overlap and are NEVER interchangeable with whole-fleet time.
    # The latter is known only if every round has a receipt-matched order
    # issuance-to-last-delivery window. Do not silently extrapolate missing
    # historical windows, not even from worker utilization.
    fully_measured = (fleet_window is None or (
        fleet_window["measured_rounds"] ==
        fleet_window["distributed_rounds"] == len(completed) and
        not fleet_window["missing_window_rounds"] and
        not fleet_window["rejected_window_rounds"]
    ))
    training_wall_seconds = (
        (fleet_window["measured_wall_seconds"] if fleet_window else rollout_seconds)
        + optimizer_seconds
        if fully_measured and completed else None
    )
    training_wall_scope = (
        "distributed_order_to_last_receipt_plus_optimizer"
        if fleet_window is not None else
        "local_rollout_plus_optimizer"
    )
    latest_observed = next((
        (index, evaluations[str(directory)])
        for index, (directory, _) in reversed(list(enumerate(completed, 1)))
        if str(directory) in evaluations
    ), None)
    latest_holdout = latest_observed[1] if latest_observed is not None else None
    all_holdouts = ([baseline_holdout] if baseline_holdout is not None else []) + list(
        evaluations.values()
    )
    measured_holdouts = [
        receipt for receipt in all_holdouts
        if (type(receipt.get("wall_seconds")) in (int, float) and
            math.isfinite(receipt["wall_seconds"]) and
            receipt["wall_seconds"] > 0 and
            type(receipt.get("world_steps")) is int and
            receipt["world_steps"] > 0)
    ]
    holdout_wall = sum(row["wall_seconds"] for row in measured_holdouts)
    holdout_steps = sum(row["world_steps"] for row in measured_holdouts)
    holdout_win_rate_gain = (
        latest_holdout["side_win_rate"] - baseline_holdout["side_win_rate"]
        if latest_holdout is not None and baseline_holdout is not None
        else None
    )
    holdout_mission_success_gain = (
        latest_holdout["learner_mission_success_rate"] -
        baseline_holdout["learner_mission_success_rate"]
        if latest_holdout is not None and baseline_holdout is not None and
           "learner_mission_success_rate" in latest_holdout and
           "learner_mission_success_rate" in baseline_holdout
        else None
    )
    return {
        "schema_version": 1, "kind": "native_training_session_progress",
        "evaluated_rounds": len(evaluations),
        "baseline_holdout": baseline_holdout,
        "latest_holdout": latest_holdout,
        "latest_holdout_round": latest_observed[0] if latest_observed else None,
        **({"holdout_simulation_cost": {
            "scope": "sum_of_independent_verified_holdout_simulation_wall_seconds",
            "completed_holdouts_including_baseline": len(all_holdouts),
            "holdouts_with_measured_runtime": len(measured_holdouts),
            **({"configured_physics_workers_per_holdout": recipe["evaluation_physics_workers"]}
               if recipe and "evaluation_physics_workers" in recipe else {}),
            "useful_world_steps": holdout_steps,
            "sum_holdout_simulation_wall_seconds": holdout_wall,
            "world_steps_per_holdout_simulation_wall_second":
                holdout_steps / holdout_wall if holdout_wall > 0 else None,
        }} if all_holdouts else {}),
        "latest_holdout_win_rate_gain": holdout_win_rate_gain,
        **({"latest_holdout_mission_success_rate_gain": holdout_mission_success_gain}
           if holdout_mission_success_gain is not None else {}),
        **({"monitoring_suites": monitor_report} if monitor_report else {}),
        **({"holdout_stopping": stopping} if stopping is not None else {}),
        **({"best_evaluated": {
            "round": best_evaluated["selected"]["round"],
            "side_win_rate": best_evaluated["selected"]["win_rate"],
            **({"mission_success_rate": best_evaluated["selected"]["mission_success_rate"]}
               if "mission_success_rate" in best_evaluated["selected"] else {}),
            **({"selection_score": best_evaluated["selected"]["selection_score"],
                "selection": best_evaluated["selection"]}
               if "selection_score" in best_evaluated["selected"] else {}),
            "checkpoint_sha256": best_evaluated["selected"]["checkpoint_sha256"],
            "playable_run": best_evaluated["playable_run"],
            "evaluated_rounds": best_evaluated["evaluated_rounds"],
        }} if best_evaluated is not None else {}),
        "completed_rounds": len(completed), "target_rounds": target,
        **({"latest_playable": {
            "checkpoint_sha256": latest_playable["checkpoint_sha256"],
            "manifest_sha256": latest_playable["manifest_sha256"],
            "playable_run": latest_playable["playable_run"],
            "playable_run_sha256": latest_playable["playable_run_sha256"],
            "source_run_config_sha256": latest_playable["source_run_config_sha256"],
        }} if latest_playable is not None else {}),
        **({"curriculum": stage_report} if stage_report is not None else {}),
        "total_world_steps": world_steps,
        "total_policy_rows": policy_rows,
        **({"native_policy_lifecycle": {
            "scope": "verified_source_controller_and_group_trajectory_events",
            "completed_rounds": len(completed),
            "rounds_with_group_admissions": len(grouped_admissions),
            "grouped_reinforcement_admissions_measured": sum(grouped_admissions),
            "rounds_with_recycled_bindings": len(recycled_bindings),
            "recycled_agent_binding_trajectories_measured": sum(recycled_bindings),
            "rounds_with_ungrouped_decisions": len(ungrouped_decisions),
            "ungrouped_policy_decisions_excluded_from_ppo_measured":
                sum(ungrouped_decisions),
        }} if grouped_admissions or recycled_bindings or ungrouped_decisions else {}),
        **({"native_mission_policy_features": {
            "rounds_with_collector_observations": len(measured_observations),
            "total_rounds": len(completed),
            "total_observation_rows_measured": sum(
                sample["rows_encoded"] for sample in measured_observations
            ),
            "goal_bearing_observation_rows_measured": sum(
                sample["native_mission_rows"] for sample in measured_observations
            ),
            **({
                "rounds_with_mission_coverage": len(mission_coverage),
                "rows_by_kind": {
                    kind: sum(row["native_mission_rows_by_kind"][kind]
                              for row in mission_coverage)
                    for kind in ("survival", "demolition", "escort", "extraction")
                },
                "target_position_known_rows": sum(
                    row["native_mission_visible_target_rows"] for row in mission_coverage
                ),
                "authored_destination_rows": sum(
                    row["native_mission_destination_rows"] for row in mission_coverage
                ),
            } if mission_coverage else {}),
            "rounds_with_learner_goal_counts": len(measured_learner_goals),
            "goal_bearing_learner_decisions_measured": sum(measured_learner_goals),
            **({
                "rounds_with_learner_goal_mix": len(measured_learner_kinds),
                "learner_decisions_by_kind": {
                    kind: sum(rows.get(kind, 0) for rows in measured_learner_kinds)
                    for kind in ("survival", "demolition", "escort", "extraction")
                },
            } if measured_learner_kinds else {}),
        }} if measured_observations or measured_learner_goals else {}),
        **({"sampled_model_inference": {
            "scope": "sum_of_sampled_model_forward_call_wall_seconds",
            "rounds_with_samples": len(inference_samples),
            "sampled_calls": sampled_calls,
            "sampled_rows": sampled_rows,
            "sampled_forward_wall_seconds": sampled_seconds,
            "microseconds_per_sampled_policy_row": (
                sampled_seconds * 1_000_000 / sampled_rows
                if sampled_rows > 0 else None
            ),
            "milliseconds_per_sampled_call": (
                sampled_seconds * 1_000 / sampled_calls
                if sampled_calls > 0 else None
            ),
            "not_a_fleet_wall_time_or_gpu_utilization": True,
        }} if inference_samples else {}),
        "total_optimizer_input_steps": optimizer_steps,
        **({"multi_gpu_optimizer": {
            "scope": "synchronized_native_ppo_gradient_updates_not_simulation",
            "completed_synchronized_gpu_rounds": len(synchronized_gpu_rounds),
            "completed_multi_machine_rounds": sum(
                sample["optimizer_gpu_nodes"] > 1
                for _, sample in synchronized_gpu_rounds
            ),
            "total_synchronized_minibatches": sum(
                sample["optimizer_synchronized_minibatches"]
                for _, sample in synchronized_gpu_rounds
            ),
            "total_global_masked_loss_normalizations": sum(
                sample["optimizer_masked_loss_normalizations"]
                for _, sample in synchronized_gpu_rounds
            ),
            "sum_largest_gpu_update_wall_seconds": sum(
                sample["optimizer_gpu_update_wall_seconds_max"]
                for _, sample in synchronized_gpu_rounds
            ),
            "per_gpu_process_memory_peak_bytes": {
                str(rank): max(
                    sample["optimizer_gpu_per_device"][rank][
                        "torch_cuda_process_peak_allocated_bytes"
                    ]
                    for _, sample in synchronized_gpu_rounds
                    if rank < sample["optimizer_gpu_workers"]
                )
                for rank in range(max(
                    sample["optimizer_gpu_workers"]
                    for _, sample in synchronized_gpu_rounds
                ))
            },
            # An actual optimizer group can change between completed rounds
            # as the Hub leases new machines. Preserve its rank topology,
            # synced model consensus and global input identity individually,
            # without treating GPU process wall time as learner-fleet time.
            "round_groups": [{
                "round": number,
                "hub_group_id": sample.get("optimizer_group_id"),
                "nodes": sample["optimizer_gpu_nodes"],
                "gpus_per_node": sample["optimizer_gpu_local_workers"],
                "total_gpu_ranks": sample["optimizer_gpu_workers"],
                "global_input_sha256":
                    sample.get("optimizer_global_input_sha256"),
                "model_consensus_sha256":
                    sample["optimizer_gpu_model_consensus_sha256"],
                "synchronized_minibatches":
                    sample["optimizer_synchronized_minibatches"],
                "max_rank_update_wall_seconds":
                    sample["optimizer_gpu_update_wall_seconds_max"],
            } for number, sample in synchronized_gpu_rounds],
        }} if synchronized_gpu_rounds else {}),
        "self_play_opponent_promotions": sum(
            record["learner_metrics"].get("refreshed_opponent_slot") is not None
            for _, record in completed
        ),
        "rounds_with_physics_worker_measurements": len(measured_physics),
        "total_parallel_physics_rounds": sum(
            row["parallel_physics_rounds"] for row in measured_physics
        ),
        "total_serialized_stage_arenas": sum(
            row.get("parallel_stage_id_serialized_arenas", 0)
            for row in measured_physics
        ),
        "sum_physics_worker_wall_seconds": sum(
            row.get("parallel_worker_cpu_wall_seconds_sum", 0.0)
            for row in measured_physics
        ),
        "sum_round_rollout_wall_seconds": rollout_seconds,
        "rollout_wall_seconds_scope": rollout_scope,
        **({"distributed_collection_wall_time": fleet_window}
           if fleet_window is not None else {}),
        "sum_round_optimizer_wall_seconds": optimizer_seconds,
        "learner_pipeline_wall_time": measured_learner_pipeline(completed),
        "useful_training_throughput": {
            "scope": training_wall_scope,
            "complete_round_coverage": fully_measured,
            "completed_rounds": len(completed),
            "measured_collection_rounds":
                fleet_window["measured_rounds"] if fleet_window else len(completed),
            "measured_collection_plus_optimizer_wall_seconds":
                training_wall_seconds,
            "successful_world_steps": world_steps,
            "successful_policy_rows": policy_rows,
            "world_steps_per_training_wall_second":
                (world_steps / training_wall_seconds
                 if training_wall_seconds is not None and training_wall_seconds > 0
                 else None),
            "policy_rows_per_training_wall_second":
                (policy_rows / training_wall_seconds
                 if training_wall_seconds is not None and training_wall_seconds > 0
                 else None),
            "excludes_holdout_and_setup": True,
        },
        "world_steps_per_rollout_wall_second":
            world_steps / rollout_seconds if rollout_seconds > 0 else None,
        "policy_rows_per_rollout_wall_second":
            policy_rows / rollout_seconds if rollout_seconds > 0 else None,
        "latest_training_outcomes": latest,
        "rounds": [
            {"round": index, "directory": str(path),
             "source_run_config_sha256": record.get("source_run_config_sha256"),
             "next_checkpoint_sha256": record["next_checkpoint_sha256"],
             "next_manifest_sha256": record["next_manifest_sha256"],
             "world_steps": record["rollout_metrics"]["successful_world_steps"],
             "parallel_physics_rounds":
                 record["rollout_metrics"].get("parallel_physics_rounds"),
             "stage_serialized_arenas":
                 record["rollout_metrics"].get("parallel_stage_id_serialized_arenas"),
             "optimizer_input_steps": record["learner_metrics"]["optimizer_input_steps"],
             "refreshed_opponent_slot":
                 record["learner_metrics"].get("refreshed_opponent_slot"),
             "training_outcomes": record["learner_metrics"].get("training_outcomes", {}),
             **({"policy_observations": record["rollout_metrics"]["policy_observations"]}
                if "policy_observations" in record["rollout_metrics"] else {}),
             **({"native_mission_learner_decisions":
                     record["learner_metrics"]["native_mission_learner_decisions"]}
                if "native_mission_learner_decisions" in record["learner_metrics"] else {}),
             "holdout": evaluations.get(str(path)),
             **({"monitor_holdouts": monitor_observations[str(path)]}
                if monitor_observations.get(str(path)) else {})}
            for index, (path, record) in enumerate(completed, 1)
        ],
    }


def launch_round(command, log_path, *, finish_optimizer=False):
    if finish_optimizer and not log_path.is_file():
        raise ValueError("completed native optimizer has no original round ownership log")
    with log_path.open("a" if finish_optimizer else "x", encoding="utf-8") as stream:
        if finish_optimizer:
            stream.write('{"kind":"native_optimizer_finalization_recovered"}\n')
            stream.flush()
        process = subprocess.Popen(command, stdout=stream, stderr=subprocess.STDOUT)
        try:
            code = process.wait()
        except KeyboardInterrupt:
            # A foreground Ctrl+C also reaches the child, which owns its
            # collector process trees and stops them. Allow that cleanup.
            if process.poll() is None:
                if os.name == "nt":
                    process.terminate()
                else:
                    process.send_signal(signal.SIGINT)
                try:
                    process.wait(timeout=12)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait()
            raise
    if code:
        raise subprocess.CalledProcessError(code, command)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("run_config", type=Path)
    parser.add_argument("checkpoint", type=Path)
    parser.add_argument("trainer_config", type=Path)
    parser.add_argument("output_dir", type=Path)
    parser.add_argument("--rounds", type=int, required=True,
                        help="total number of completed rounds desired, including prior rounds")
    parser.add_argument("--resume", action="store_true",
                        help="resume an existing session without changing its pinned inputs")
    parser.add_argument("--model-manifest", type=Path)
    parser.add_argument("--curriculum-plan", type=Path,
                        help="pinned source-run changes between completed optimizer rounds")
    parser.add_argument("--evaluation-run-config", type=Path,
                        help="primary fixed-seed Levels used for checkpoint selection and stopping")
    parser.add_argument("--monitor-evaluation-run-config", type=Path, action="append", default=[],
                        help="additional fixed-seed suite monitored against every evaluated checkpoint; repeatable")
    parser.add_argument("--checkpoint-selection", choices=(
                            "primary", "mean", "worst", "mean-scene", "worst-scene",
                            "mission", "mission-mean-scene", "mission-worst-scene",
                        ), default="primary",
                        help="rank on faction wins, or verified side-adjusted physical mission completions")
    parser.add_argument("--evaluation-side", type=int,
                        help="source side 1 or 2 whose victories are measured")
    parser.add_argument("--evaluation-policy-seed", type=int, default=0,
                        help="fixed policy action-sampling RNG seed during holdout evaluation")
    parser.add_argument("--evaluation-every", type=int, default=1,
                        help="run a pinned holdout every N optimizer rounds and at the final target (default: 1)")
    parser.add_argument("--evaluation-physics-workers", type=int,
                        help="independent native CPU physics workers for each holdout; does not change rollout allocation")
    parser.add_argument("--evaluation-inference-device", choices=("cpu", "cuda:0"),
                        help="fixed PyTorch inference placement for identical evaluation suites")
    parser.add_argument("--early-stop-patience", type=int,
                        help="stop after this many evaluated rounds without significant wins or goal successes")
    parser.add_argument("--early-stop-minimum-rounds", type=int, default=3,
                        help="never stop before this many completed optimizer rounds (default: 3)")
    parser.add_argument("--early-stop-minimum-extra-victories", type=int, default=1,
                        help="minimum extra holdout wins or physical goal successes for progress (default: 1)")
    parser.add_argument("--learner-slot", type=int)
    parser.add_argument("--self-play-opponent-slot", type=int,
                        help="existing MA-POCA slot to refresh from the completed learner checkpoint")
    parser.add_argument("--self-play-refresh-every", type=int,
                        help="number of finished optimizer rounds between opponent promotions")
    parser.add_argument("--rollout-binary", type=Path)
    parser.add_argument("--rollout-workers", type=int)
    parser.add_argument("--rollout-gpus",
                        help="local physical GPU ordinals assigned round-robin to rollout processes")
    parser.add_argument("--rollout-gpu-mask", choices=(
        "CUDA_VISIBLE_DEVICES", "ROCR_VISIBLE_DEVICES", "HIP_VISIBLE_DEVICES"),
        help="GPU isolation selector for local rollouts (use ROCR on ROCm Linux)")
    parser.add_argument("--physics-workers", type=int,
                        help="CPU physics threads in each local rollout process; distributed collectors use their plan")
    parser.add_argument("--rollout-worlds", type=int)
    parser.add_argument("--rollout-world-start", type=int)
    parser.add_argument("--distributed-plan", type=Path,
                        help="explicit multi-machine collector windows; Experiment Hub executes orders")
    parser.add_argument("--bundle-inbox", type=Path,
                        help="directory for verified collector bundles in distributed mode")
    parser.add_argument("--bundle-archive-inbox", type=Path,
                        help="optional directory receiving atomically uploaded *.tar collector archives")
    parser.add_argument("--bundle-receiver-listen",
                        help="start an authenticated native collector upload receiver on HOST:PORT; requires BEES_NATIVE_BUNDLE_TOKEN")
    parser.add_argument("--collector-input-outbox", type=Path,
                        help="automatically export one shared verified input archive for each issued round")
    parser.add_argument("--hub-dispatch-plan", type=Path,
                        help="opt-in machine/resource allocation and preapproved native Hub Start for each issued round")
    parser.add_argument("--hub-dispatch-port", type=int, default=7148,
                        help="loopback Experiment Hub operator API port (default: 7148)")
    parser.add_argument("--hub-auto-worlds", type=int,
                        help="derive the native collector plan and fixed total World budget from the approved Hub machine CPU allocations")
    parser.add_argument("--auto-balance-collectors", action="store_true",
                        help="redistribute contiguous native arena windows conservatively between verified distributed rounds")
    parser.add_argument("--collector-timeout-seconds", type=int, default=0,
                        help="time to await each distributed round (0 means no deadline)")
    parser.add_argument("--seed-offset-start", type=int, default=0)
    parser.add_argument("--seed-offset-stride", type=int, default=1)
    parser.add_argument("--torch-threads", type=int)
    parser.add_argument("--device", default="cpu")
    parser.add_argument("--learner-gpus", type=int, choices=(1, 2), default=1,
                        help="run coordinated native PPO learner updates on two allocated GPUs")
    parser.add_argument("--stage-diagnostics", action="store_true")
    parser.add_argument("--library-save-root", type=Path,
                        help="opt-in native player save directory for playable training publications")
    parser.add_argument("--library-game-id",
                        help="stable per-job game ID; fixed while the session advances")
    parser.add_argument("--library-game-title",
                        help="game library display title (defaults to native run ID)")
    args = parser.parse_args()
    if (args.library_save_root is None) != (args.library_game_id is None):
        parser.error("--library-save-root and --library-game-id must be used together")
    if args.library_game_title is not None and args.library_game_id is None:
        parser.error("--library-game-title requires --library-game-id")
    if args.library_game_id is not None and (
            not 1 <= len(args.library_game_id) <=
                (35 if args.evaluation_run_config is not None else 40) or
            any(c not in "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_"
                for c in args.library_game_id)):
        parser.error("invalid native game ID (1..40 ASCII characters; max 35 with best holdout)")
    if not 1 <= args.rounds <= 99999:
        parser.error("--rounds must be between 1 and 99999")
    if args.rollout_gpus is not None:
        from native_gpu_allocation import parse_rollout_gpus
        try:
            parse_rollout_gpus(args.rollout_gpus)
        except ValueError as error:
            parser.error(str(error))
    elif args.rollout_gpu_mask is not None:
        parser.error("--rollout-gpu-mask requires --rollout-gpus")
    if (args.self_play_opponent_slot is None) != (args.self_play_refresh_every is None):
        parser.error("self-play requires both --self-play-opponent-slot and --self-play-refresh-every")
    if args.self_play_opponent_slot is not None and (
        args.learner_slot is None or args.self_play_opponent_slot == args.learner_slot or
        not 0 <= args.self_play_opponent_slot < (1 << 32) or
        not 1 <= args.self_play_refresh_every <= 99999
    ):
        parser.error("self-play requires a distinct uint32 opponent slot, explicit learner slot and positive refresh interval")
    if ((args.evaluation_run_config is None) != (args.evaluation_side is None) or
            args.evaluation_side is not None and args.evaluation_side not in (1, 2)):
        parser.error("specify both --evaluation-run-config and --evaluation-side 1 or 2")
    if (not 0 <= args.evaluation_policy_seed < (1 << 63) or
            args.evaluation_run_config is None and args.evaluation_policy_seed != 0):
        parser.error("--evaluation-policy-seed requires a holdout and a nonnegative int63 seed")
    if args.monitor_evaluation_run_config and args.evaluation_run_config is None:
        parser.error("additional monitoring suites require a primary --evaluation-run-config")
    if args.checkpoint_selection.startswith("mission") and args.evaluation_run_config is None:
        parser.error("physical mission ranking requires a pinned primary evaluation suite")
    if len(args.monitor_evaluation_run_config) > 16:
        parser.error("no more than 16 independent monitoring suites per session")
    if args.checkpoint_selection in ("mean", "worst") and not args.monitor_evaluation_run_config:
        parser.error("mean/worst suite ranking requires additional monitoring suites")
    if args.evaluation_physics_workers is not None and (
            args.evaluation_run_config is None or not 1 <= args.evaluation_physics_workers <= 128):
        parser.error("--evaluation-physics-workers requires a holdout and 1..128 CPU workers")
    if not 1 <= args.evaluation_every <= 99999 or (
            args.evaluation_every != 1 and args.evaluation_run_config is None):
        parser.error("--evaluation-every must be positive and requires a fixed holdout suite")
    if args.early_stop_patience is not None:
        if args.evaluation_run_config is None or not 1 <= args.early_stop_patience <= 99999:
            parser.error("early stopping requires an evaluation suite and positive patience")
        if not 1 <= args.early_stop_minimum_rounds <= args.rounds:
            parser.error("--early-stop-minimum-rounds must be between 1 and --rounds")
        if not 1 <= args.early_stop_minimum_extra_victories <= 1000000:
            parser.error("--early-stop-minimum-extra-victories must be a positive integer")
    elif (args.early_stop_minimum_rounds != 3 or
          args.early_stop_minimum_extra_victories != 1):
        parser.error("early-stop thresholds require --early-stop-patience")
    if (args.seed_offset_start < 0 or args.seed_offset_stride < 1 or
            args.seed_offset_start + (args.rounds - 1) * args.seed_offset_stride >= (1 << 64)):
        parser.error("session seed offsets must be distinct and fit uint64")
    if args.rollout_workers is not None and not 1 <= args.rollout_workers <= 256:
        parser.error("rollout workers must be between 1 and 256")
    if args.physics_workers is not None and not 1 <= args.physics_workers <= 128:
        parser.error("physics workers must be between 1 and 128")
    if args.rollout_worlds is not None and not 1 <= args.rollout_worlds <= 1024:
        parser.error("rollout worlds must be between 1 and 1024")
    if args.rollout_world_start is not None and args.rollout_world_start < 0:
        parser.error("rollout world start must be nonnegative")
    if args.torch_threads is not None and not 1 <= args.torch_threads <= 256:
        parser.error("torch threads must be between 1 and 256")
    if args.hub_auto_worlds is not None and (
            args.hub_dispatch_plan is None or args.distributed_plan is not None or
            not 2 <= args.hub_auto_worlds <= 16384):
        parser.error("--hub-auto-worlds requires a Hub dispatch plan, no separate distributed plan, and 2..16384 total Worlds")
    if args.learner_gpus > 1 and args.device not in ("cuda", "cuda:0"):
        parser.error("native multi-GPU optimizer requires CUDA/ROCm learner execution")
    if args.torch_threads is not None and args.torch_threads < args.learner_gpus:
        parser.error("optimizer threads must accommodate every leased learner GPU")
    distributed_requested = args.distributed_plan is not None or args.hub_auto_worlds is not None
    if distributed_requested != (args.bundle_inbox is not None):
        parser.error("distributed sessions require a collector plan or --hub-auto-worlds and --bundle-inbox")
    if args.bundle_archive_inbox is not None and not distributed_requested:
        parser.error("--bundle-archive-inbox requires distributed collection and --bundle-inbox")
    if args.bundle_receiver_listen is not None and not distributed_requested:
        parser.error("--bundle-receiver-listen requires distributed collection and --bundle-inbox")
    if args.collector_input_outbox is not None and not distributed_requested:
        parser.error("--collector-input-outbox requires distributed collection and --bundle-inbox")
    if args.collector_input_outbox is not None and not args.collector_input_outbox.is_dir():
        parser.error("--collector-input-outbox must be an existing directory")
    if args.hub_dispatch_plan is not None and (
            not distributed_requested or
            args.bundle_receiver_listen is None or
            args.collector_input_outbox is None):
        parser.error("--hub-dispatch-plan requires distributed collection, a receiver and published collector inputs")
    if not 1 <= args.hub_dispatch_port <= 65535:
        parser.error("--hub-dispatch-port must be a valid TCP port")
    if args.collector_timeout_seconds < 0:
        parser.error("--collector-timeout-seconds cannot be negative")
    if args.auto_balance_collectors and not distributed_requested:
        parser.error("--auto-balance-collectors requires distributed collection")
    if distributed_requested and any(
        value is not None for value in (
            args.rollout_workers, args.rollout_worlds, args.rollout_world_start,
            args.physics_workers, args.rollout_gpus, args.rollout_gpu_mask,
        )
    ):
        parser.error("distributed collector windows/workers belong to --distributed-plan")

    run = pinned(args.run_config)
    trainer = pinned(args.trainer_config)
    checkpoint = pinned(args.checkpoint)
    config = read_json(Path(run["path"]))
    if not isinstance(config, dict) or "rollout_bundles" in config or config.get("hivemind"):
        parser.error("session requires a model-only native Level configuration")
    python_value = config.get("python")
    if not isinstance(python_value, str) or not python_value.strip():
        parser.error("native run configuration must specify its Python interpreter")
    python_path = Path(python_value)
    # Keep the interpreter's invocation path: a venv's python executable
    # may be a symlink to the system binary, and resolving the last link
    # bypasses the venv prefix used to import PyTorch and MA-POCA.
    round_python = (
        "python" if os.name == "nt" else "python3"
    ) if python_value == "auto" else (
        os.path.abspath(Path(run["path"]).parent / python_path)
        if python_path.is_absolute() or len(python_path.parts) > 1
        else python_value
    )
    manifest_value = args.model_manifest or config.get("model_manifest")
    if manifest_value is None:
        parser.error("run requires model_manifest or --model-manifest")
    manifest_path = Path(manifest_value)
    if not manifest_path.is_absolute():
        manifest_path = Path(run["path"]).parent / manifest_path
    manifest = pinned(manifest_path)
    check_learner(Path(manifest["path"]), Path(checkpoint["path"]), args.learner_slot)
    self_play = None
    if args.self_play_opponent_slot is not None:
        models = read_json(Path(manifest["path"]))["models"]
        opponent = [
            model for model in models if model.get("slot") == args.self_play_opponent_slot
        ]
        if len(opponent) != 1 or opponent[0].get("backend") != "mlagents":
            parser.error("self-play opponent must reference one existing native MA-POCA model")
        if not any(
            {args.learner_slot, args.self_play_opponent_slot}.issubset(
                set(level.get("model_slots", {}).values())
            )
            for level in config.get("levels", [])
        ):
            parser.error("self-play source Levels must route learner and opponent model slots")
        self_play = {
            "opponent_slot": args.self_play_opponent_slot,
            "every": args.self_play_refresh_every,
        }
    curriculum = (load_curriculum(args.curriculum_plan, run, args.rounds,
                                  round_python, args.learner_slot, self_play)
                  if args.curriculum_plan is not None else None)
    if args.rollout_binary is not None:
        binary = pinned(args.rollout_binary)
    else:
        binary = None
    recipe = {
        "schema_version": 1, "kind": "native_training_session",
        "run_config": run, "trainer_config": trainer,
        "initial_checkpoint": checkpoint, "initial_manifest": manifest,
        "learner_slot": args.learner_slot,
        "round_python": round_python,
        "rollout_binary": binary,
        "rollout_workers": args.rollout_workers,
        "rollout_worlds": args.rollout_worlds,
        "rollout_world_start": args.rollout_world_start,
        "seed_offset_start": args.seed_offset_start,
        "seed_offset_stride": args.seed_offset_stride,
        "torch_threads": args.torch_threads,
        "device": args.device,
        "stage_diagnostics": args.stage_diagnostics,
    }
    # Existing single-GPU/CPU session recipes retain their stable identity.
    # A dual-GPU session pins its real optimizer allocation through resume.
    if args.learner_gpus > 1:
        recipe["learner_gpus"] = args.learner_gpus
    # Preserve existing session fingerprints when this new resource
    # selection was not specified; older recipes have no such field.
    if args.physics_workers is not None:
        recipe["physics_workers"] = args.physics_workers
    if args.evaluation_inference_device is not None:
        if args.evaluation_run_config is None:
            parser.error("--evaluation-inference-device requires fixed evaluation suites")
        recipe["evaluation_inference_device"] = args.evaluation_inference_device
    if args.rollout_gpus is not None:
        recipe["rollout_gpus"] = args.rollout_gpus
        recipe["rollout_gpu_mask"] = args.rollout_gpu_mask or "CUDA_VISIBLE_DEVICES"
    if self_play is not None:
        recipe["self_play"] = self_play
    if curriculum is not None:
        recipe["curriculum"] = curriculum
    if distributed_requested:
        if args.distributed_plan is not None:
            recipe["distributed_plan"] = pinned(args.distributed_plan)
        else:
            recipe["hub_auto_worlds"] = args.hub_auto_worlds
        recipe["bundle_inbox"] = str(args.bundle_inbox.resolve(strict=True))
        if args.bundle_archive_inbox is not None:
            archive_inbox = args.bundle_archive_inbox.resolve(strict=True)
            if not archive_inbox.is_dir():
                parser.error("--bundle-archive-inbox must be an existing directory")
            recipe["bundle_archive_inbox"] = str(archive_inbox)
        recipe["collector_timeout_seconds"] = args.collector_timeout_seconds
        if args.distributed_plan is not None:
            read_plan(Path(recipe["distributed_plan"]["path"]))
        if args.auto_balance_collectors or args.hub_auto_worlds is not None:
            recipe["auto_balance_collectors"] = True
        if args.hub_dispatch_plan is not None:
            recipe["hub_dispatch_plan"] = pinned(args.hub_dispatch_plan)
    if args.evaluation_run_config is not None:
        recipe["evaluation_config"] = pinned(args.evaluation_run_config)
        recipe["evaluation_side"] = args.evaluation_side
        recipe["evaluation_policy_seed"] = args.evaluation_policy_seed
        if args.evaluation_every != 1:
            recipe["evaluation_every"] = args.evaluation_every
        if args.evaluation_physics_workers is not None:
            recipe["evaluation_physics_workers"] = args.evaluation_physics_workers
    if args.monitor_evaluation_run_config:
        suites = [pinned(path) for path in args.monitor_evaluation_run_config]
        hashes = [recipe["evaluation_config"]["sha256"], *(suite["sha256"] for suite in suites)]
        if len(set(hashes)) != len(hashes):
            parser.error("primary and monitoring evaluation suites must have distinct pinned source bytes")
        recipe["monitor_evaluations"] = suites
    if args.checkpoint_selection != "primary":
        recipe["checkpoint_selection"] = args.checkpoint_selection
    if args.early_stop_patience is not None:
        recipe["early_stop"] = {
            "patience_rounds": args.early_stop_patience,
            "minimum_rounds": args.early_stop_minimum_rounds,
            "minimum_extra_victories": args.early_stop_minimum_extra_victories,
        }
    recipe["source_dependencies"] = native_input_dependencies(
        run, recipe.get("evaluation_config"),
        *recipe.get("monitor_evaluations", []),
        *([] if curriculum is None else [item["run_config"] for item in curriculum["stages"]]),
    )
    root = args.output_dir.resolve()
    if args.resume:
        if not root.is_dir():
            parser.error("cannot resume a session without its output directory")
    else:
        root.mkdir(parents=True, exist_ok=False)
    # Listener address and credentials are transport settings, not model
    # lineage: a resumed session may bind a new machine or private port.
    # The receiver is live only while this learner owns the session lock.
    receiver_context = (
        BundleReceiver(
            args.bundle_receiver_listen, Path(recipe["bundle_inbox"]),
            on_event=lambda kind, details: print(json.dumps({
                "kind": kind, **details,
            }), flush=True),
        ) if args.bundle_receiver_listen is not None else nullcontext(None)
    )
    with session_lock(root), receiver_context as receiver:
        if args.hub_auto_worlds is not None:
            hub_plan = recipe["hub_dispatch_plan"]
            if file_sha256(Path(hub_plan["path"])) != hub_plan["sha256"]:
                raise ValueError("Hub resource allocation changed before native session setup")
            derived = derive_hub_resource_plan(
                read_json(Path(hub_plan["path"])), args.hub_auto_worlds,
            )
            allocation_path = root / "hub-derived-collectors.json"
            if args.resume:
                if not allocation_path.is_file():
                    raise ValueError("native Hub-derived World allocation missing on resume")
                previous_allocation = read_json(allocation_path)
                if previous_allocation != derived:
                    # Pre-GPU-pinning Hub sessions issued the same CPU/World
                    # windows without storing Hub lease counts in native
                    # orders. Resume their ORIGINAL signed orders verbatim;
                    # never retrofit a new identity into an active round.
                    legacy = {"collectors": [
                        {key: value for key, value in item.items()
                         if key != "hub_gpu_count"}
                        for item in derived["collectors"]
                    ]}
                    if previous_allocation != legacy:
                        raise ValueError("native Hub-derived World allocation changed on resume")
            else:
                if allocation_path.exists():
                    raise ValueError("native Hub allocation file already exists")
                atomic_json(allocation_path, derived)
            recipe["distributed_plan"] = pinned(allocation_path)
            print(json.dumps({
                "kind": "native_hub_derived_collector_plan",
                "total_worlds": args.hub_auto_worlds,
                "collectors": derived["collectors"],
                "measured_world_balancing": True,
            }, sort_keys=True), flush=True)
        config_path = root / "session-config.json"
        if args.resume:
            previous = read_json(config_path)
            # Sessions created before scene/Stage pinning retain their
            # original immutable recipe and complete checkpoint lineage.
            # Do not invent historical hashes or invalidate their records.
            if "source_dependencies" not in previous:
                recipe.pop("source_dependencies")
            if previous != recipe:
                raise ValueError("session inputs or resource allocation changed; start a new session")
        else:
            if config_path.exists():
                raise ValueError("session configuration already exists")
            atomic_json(config_path, recipe)
        check_native_input_dependencies(recipe)
        distributed = "distributed_plan" in recipe
        if distributed:
            # One imported-bundle template for the entire checkpoint
            # lineage. The original run.json remains the collector's
            # authoritative source of physical scenes and seed plans.
            template = root / "distributed-bundle-template.json"
            expected_template = {
                "run_id": config["run_id"] + "-distributed",
                "python": round_python,
                "rollout_bundles": [],
            }
            if template.exists():
                if read_json(template) != expected_template:
                    raise ValueError("distributed learner template changed")
            else:
                atomic_json(template, expected_template)
        completed, next_manifest, next_checkpoint, next_attempt = existing_rounds(
            root, Path(manifest["path"]), Path(checkpoint["path"]), args.learner_slot,
            recipe.get("self_play"), recipe.get("curriculum"), run,
        )
        if any(
            (record["learner_metrics"].get("optimizer_gpu_workers") == 2) !=
            (args.learner_gpus == 2)
            for _, record in completed
        ):
            raise ValueError(
                "resumed native training session changed the actual optimizer GPU allocation"
            )
        latest_playable = publish_latest_playable(
            root, recipe, completed, next_manifest, next_checkpoint,
        )
        if args.library_save_root is not None:
            display_title = args.library_game_title or config["run_id"]
            register_playable(args.library_save_root, args.library_game_id,
                              display_title, root / "latest-playable.json")
        evaluations = {}
        monitor_baseline = {}
        monitor_observations = {}
        baseline_holdout = None
        best_evaluated = None
        if "evaluation_config" in recipe:
            check_native_input_dependencies(recipe)
            # Capture model quality BEFORE the first PPO update, using the
            # exact same native fixed-seed Levels as every later candidate.
            # The root attempt directories are separate from round attempts;
            # interrupted baseline evaluation is resumed without touching
            # the original model or completed checkpoints.
            baseline_holdout = ensure_evaluation(
                root, Path(manifest["path"]), recipe,
            )
            monitor_baseline = evaluate_monitor_suites(
                root, Path(manifest["path"]), recipe, due=True,
            )
            for index, (directory, record) in enumerate(completed, 1):
                due = evaluation_due(index, args.rounds, recipe)
                if due or evaluation_attempt_exists(directory):
                    evaluations[str(directory)] = ensure_evaluation(
                        directory, Path(record["next_manifest"]), recipe,
                    )
                checked = evaluate_monitor_suites(
                    directory, Path(record["next_manifest"]), recipe, due=due,
                )
                if checked:
                    monitor_observations[str(directory)] = checked
            best_evaluated = publish_best_evaluated(
                root, recipe, completed, evaluations, baseline_holdout,
                monitor_baseline, monitor_observations,
            )
            if args.library_save_root is not None:
                display_title = args.library_game_title or config["run_id"]
                register_playable(
                    args.library_save_root, args.library_game_id + "-best",
                    display_title[:104] + " (Best holdout)",
                    root / "best-evaluated.json",
                )
        stop_state = early_stop_decision(
            completed, evaluations, baseline_holdout, recipe,
        )
        if stop_state is not None and (
                stop_state["minimum_extra_victories"] > stop_state["holdout_episodes"]):
            raise ValueError("early-stop meaningful improvement exceeds available holdout episodes")
        atomic_json(root / "session-progress.json", progress_snapshot(
            completed, args.rounds, evaluations, baseline_holdout, best_evaluated, stop_state,
            recipe, latest_playable,
            monitor_baseline=monitor_baseline,
            monitor_observations=monitor_observations,
        ))
        if len(completed) > args.rounds:
            raise ValueError("target round count is smaller than the completed checkpoint lineage")
        first_pending_round = len(completed) + 1
        for index in range(first_pending_round, args.rounds + 1):
            if stop_state is not None and stop_state["stop"]:
                # A resumed session reaches exactly the same stop decision
                # as its original invocation from immutable evaluations.
                # No extra collectors, learner update or work orders start.
                break
            # A source scene, trainer settings, or rollout binary changed
            # between rounds would make throughput and learning quality
            # incomparable, even when the model lineage remains intact.
            for label in ("run_config", "trainer_config", "rollout_binary"):
                source = recipe[label]
                if source is not None and file_sha256(Path(source["path"])) != source["sha256"]:
                    raise ValueError(f"{label} changed during this training session")
            stage_run, stage_start = selected_curriculum_run(run, curriculum, index)
            if curriculum is not None:
                plan = curriculum["plan"]
                if file_sha256(Path(plan["path"])) != plan["sha256"]:
                    raise ValueError("curriculum plan changed during training")
                for item in curriculum["stages"]:
                    source = item["run_config"]
                    if file_sha256(Path(source["path"])) != source["sha256"]:
                        raise ValueError("curriculum source run changed during training")
            check_native_input_dependencies(recipe)
            attempt = next_attempt if index == first_pending_round else 1
            if attempt > 999:
                raise ValueError(f"round {index} exhausted its attempt numbers")
            directory = root / f"round-{index:05d}-attempt-{attempt:03d}"
            finish_optimizer = bool(
                args.resume and args.learner_gpus > 1 and
                (directory / "optimizer-attempt.json").is_file() and
                (directory / "learner" / "metrics.json").is_file()
            )
            log = root / (directory.name + ".log")
            seed_offset = args.seed_offset_start + (index - 1) * args.seed_offset_stride
            print(json.dumps({"kind": "native_training_session_round_started",
                              "round": index, "target_rounds": args.rounds,
                              "directory": str(directory), "seed_offset": seed_offset,
                              "curriculum_stage_start_round": stage_start,
                              "source_run_config_sha256": stage_run["sha256"]}), flush=True)
            command = [
                round_python, str(Path(__file__).with_name("train_policy_round.py")),
                str(template if distributed else stage_run["path"]),
                str(next_checkpoint), trainer["path"], str(directory),
                "--model-manifest", str(next_manifest),
                "--device", args.device,
                *([] if args.learner_gpus == 1 else
                  ["--learner-gpus", str(args.learner_gpus)]),
            ]
            refreshing = scheduled_opponent_slot(self_play, index)
            if refreshing is not None:
                command.extend(["--refresh-opponent-slot", str(refreshing)])
            if args.learner_slot is not None:
                command.extend(["--learner-slot", str(args.learner_slot)])
            if args.torch_threads is not None:
                command.extend(["--torch-threads", str(args.torch_threads)])
            if distributed:
                plan_path = Path(recipe["distributed_plan"]["path"])
                if file_sha256(plan_path) != recipe["distributed_plan"]["sha256"]:
                    raise ValueError("distributed collector allocations changed during session")
                work_order_existed = (
                    root / f"collection-round-{index:05d}.json"
                ).exists()
                collection_start = time.monotonic()
                allocation_plan = read_plan(plan_path)
                if recipe.get("auto_balance_collectors"):
                    allocation_plan, balancing = balanced_round_plan(
                        root, allocation_plan, completed, index,
                    )
                    print(json.dumps({
                        "kind": "native_distributed_allocation_selected",
                        "round": index, **balancing,
                        "allocations": allocation_plan["collectors"],
                    }), flush=True)
                orders, order_path = make_orders(
                    root, index, seed_offset, allocation_plan,
                    stage_run, next_checkpoint, next_manifest, args.learner_slot,
                )
                prepared_inputs = {}
                export_started = time.monotonic()
                if args.collector_input_outbox is not None:
                    outbox = args.collector_input_outbox.resolve(strict=True)
                    order_digest = file_sha256(order_path)
                    # Every collector's physical source and model weights
                    # are identical for the issued round. Export once and
                    # serve the immutable archive to all authorized jobs.
                    # Only their already-issued World windows differ.
                    archive = outbox / f"collection-round-{index:05d}.inputs.tar"
                    receipt_path = outbox / f"collection-round-{index:05d}.inputs.json"
                    identity = {
                        "kind": "native_distributed_round_input_archive",
                        "orders_sha256": order_digest,
                        "checkpoint_sha256": orders["input_checkpoint_sha256"],
                        "manifest_sha256": orders["input_manifest_sha256"],
                    }
                    if archive.exists() or archive.is_symlink():
                        if (archive.is_symlink() or not archive.is_file() or
                                receipt_path.is_symlink()):
                            raise ValueError("previous native round source archive is not a regular file")
                        if not receipt_path.is_file():
                            # Publication is two durable operations: the
                            # immutable tar arrives before its small index.
                            # A crash between them must not strand a Hub
                            # collection. Reconstruct the exact deterministic
                            # tar in private temporary storage, compare its
                            # complete bytes, then publish only the missing
                            # index. Never accept an unverified old tar.
                            with TemporaryDirectory(
                                    prefix=".native-input-recovery-", dir=outbox,
                            ) as recovery:
                                expected = export_inputs(
                                    order_path, "@round", Path(stage_run["path"]),
                                    Path(trainer["path"]), Path(next_checkpoint),
                                    Path(next_manifest),
                                    Path(recovery) / "issued-inputs.tar",
                                )
                                if (expected["archive_bytes"] != archive.stat().st_size or
                                        expected["archive_sha256"] != file_sha256(archive)):
                                    raise ValueError(
                                        "unindexed native round archive differs from issued source"
                                    )
                                atomic_json(receipt_path, {
                                    **identity,
                                    "archive_sha256": expected["archive_sha256"],
                                })
                        previous = read_json(receipt_path)
                        if (any(previous.get(key) != value
                                for key, value in identity.items()) or
                                previous.get("archive_sha256") != file_sha256(archive)):
                            raise ValueError("issued round input archive differs from previous export")
                        result = {
                            "archive": str(archive),
                            "archive_sha256": previous["archive_sha256"],
                            "archive_bytes": archive.stat().st_size,
                            "reused": True,
                        }
                    elif receipt_path.exists() or receipt_path.is_symlink():
                        raise ValueError("previous native round source export has no archive")
                    else:
                        result = export_inputs(
                            order_path, "@round", Path(stage_run["path"]),
                            Path(trainer["path"]), Path(next_checkpoint),
                            Path(next_manifest), archive,
                        )
                        atomic_json(receipt_path, {
                            **identity, "archive_sha256": result["archive_sha256"],
                        })
                    for job in orders["jobs"]:
                        prepared_inputs[job["id"]] = (
                            str(archive), result["archive_sha256"], result["archive_bytes"],
                        )
                        print(json.dumps({
                            "kind": "native_distributed_collector_inputs_ready",
                            "round": index, "collector_id": job["id"],
                            "shared_round_archive": True,
                            **result,
                        }, sort_keys=True), flush=True)
                export_wall_seconds = time.monotonic() - export_started
                if receiver is not None:
                    receiver.activate(orders, prepared_inputs)
                hub_dispatch_wall_seconds = 0.0
                hub_dispatch_job_count = 0
                hub_barrier = None
                approved_hub_plan = None
                dispatched_hub_round = None
                if "hub_dispatch_plan" in recipe:
                    dispatch_started = time.monotonic()
                    pinned_plan = recipe["hub_dispatch_plan"]
                    if file_sha256(Path(pinned_plan["path"])) != pinned_plan["sha256"]:
                        raise ValueError("approved native Hub machine/resource dispatch plan changed")
                    approved_hub_plan = read_json(Path(pinned_plan["path"]))
                    node = shutil.which("node")
                    if node is None:
                        raise ValueError("Node.js is required for native Experiment Hub round dispatch")
                    bridge = Path(__file__).resolve().parents[2] / (
                        "Training/experiment_hub_native_round_dispatch.js"
                    )
                    dispatched = subprocess.run([
                        node, str(bridge), "--orders", str(order_path),
                        "--plan", pinned_plan["path"],
                        "--port", str(args.hub_dispatch_port),
                    ], capture_output=True, text=True, timeout=120)
                    if dispatched.returncode != 0:
                        raise ValueError("native Hub round dispatch failed: " +
                                         dispatched.stderr.strip()[-1200:])
                    lines = dispatched.stdout.splitlines()
                    if len(lines) != 1:
                        raise ValueError("native Hub dispatcher returned no unique frozen round receipt")
                    for line in lines:
                        report = json.loads(line)
                        if (report.get("kind") != "native_hub_collection_round_dispatched" or
                                report.get("round") != index or
                                report.get("orders_sha256") != file_sha256(order_path)):
                            raise ValueError("Hub did not confirm the exact native round dispatch")
                        hub_barrier = NativeHubCollectionBarrier(
                            args.hub_dispatch_port, report, orders,
                        )
                        dispatched_hub_round = report
                        hub_dispatch_job_count = len(report.get("member_job_ids", []))
                        print(json.dumps(report, sort_keys=True), flush=True)
                    hub_dispatch_wall_seconds = time.monotonic() - dispatch_started
                print(json.dumps({"kind": "native_distributed_collection_orders_ready",
                                  "round": index, "orders": str(order_path),
                                  "jobs": orders["jobs"]}), flush=True)
                barrier_started = time.monotonic()
                bundles = wait_for_collections(
                    Path(recipe["bundle_inbox"]), orders,
                    args.collector_timeout_seconds,
                    progress=lambda kind, details: print(json.dumps(
                        {"kind": kind, "round": index, **details},
                    ), flush=True),
                    archive_inbox=(
                        Path(recipe["bundle_archive_inbox"])
                        if "bundle_archive_inbox" in recipe else None
                    ),
                    check_unfinished=hub_barrier,
                )
                collector_barrier_wall_seconds = time.monotonic() - barrier_started
                if receiver is not None:
                    receiver.deactivate()
                # Only a newly published order yields a real end-to-end
                # measurement. A restarted learner can collect receipts
                # that arrived while it was offline; never divide those
                # historic world steps by a near-zero recovery wait.
                if not work_order_existed:
                    elapsed = time.monotonic() - collection_start
                    steps = sum(bundle["metrics"]["successful_world_steps"]
                                for bundle in bundles)
                    rows = sum(bundle["metrics"]["successful_policy_rows"]
                               for bundle in bundles)
                    if elapsed <= 0 or steps <= 0 or rows <= 0:
                        raise ValueError("invalid native distributed collection timing")
                    window = {
                        "schema_version": 1,
                        "kind": "native_distributed_collection_window",
                        "collector_input_archive_count": 1 if prepared_inputs else 0,
                        "collector_source_export_wall_seconds": export_wall_seconds,
                        "hub_dispatch_wall_seconds": hub_dispatch_wall_seconds,
                        "hub_dispatched_collectors": hub_dispatch_job_count,
                        **({} if approved_hub_plan is None else {
                            "hub_machine_collection": measured_hub_machine_collection(
                                approved_hub_plan, dispatched_hub_round, bundles,
                                hub_barrier.observed_resource_summary(),
                            ),
                        }),
                        "hub_barrier_registry_checks": hub_barrier.checks if hub_barrier else 0,
                        "hub_barrier_unavailable_observations": (
                            hub_barrier.coordinator_unavailable if hub_barrier else 0
                        ),
                        "collector_barrier_wall_seconds": collector_barrier_wall_seconds,
                        "collector_input_archive_bytes_published": (
                            next(iter(prepared_inputs.values()))[2] if prepared_inputs else 0
                        ),
                        **({} if receiver is None else receiver.transfer_metrics()),
                        "round": index,
                        "orders_sha256": file_sha256(order_path),
                        "source_run_contract_sha256":
                            orders.get("source_run_contract_sha256"),
                        "collector_count": len(bundles),
                        "work_order_to_last_receipt_wall_seconds": elapsed,
                        "useful_world_steps": steps,
                        "useful_policy_rows": rows,
                        "world_steps_per_wall_second": steps / elapsed,
                        "policy_rows_per_wall_second": rows / elapsed,
                        "sum_independent_collector_wall_seconds": sum(
                            bundle["metrics"]["wall_seconds"] for bundle in bundles
                        ),
                    }
                    atomic_json(
                        root / f"collection-round-{index:05d}-window.json", window,
                    )
                    print(json.dumps(window, sort_keys=True), flush=True)
                command.extend(["--collection-orders", str(order_path)])
                for bundle in bundles:
                    command.extend([
                        "--pinned-rollout-bundle", bundle["path"], bundle["sha256"],
                    ])
            else:
                command.extend(["--seed-offset", str(seed_offset)])
                for option, value in (("--rollout-workers", args.rollout_workers),
                                      ("--rollout-gpus", args.rollout_gpus),
                                      ("--rollout-gpu-mask", args.rollout_gpu_mask),
                                      ("--physics-workers", args.physics_workers),
                                      ("--rollout-worlds", args.rollout_worlds),
                                      ("--rollout-world-start", args.rollout_world_start)):
                    if value is not None:
                        command.extend([option, str(value)])
                if binary is not None:
                    command.extend(["--rollout-binary", binary["path"]])
            if args.stage_diagnostics:
                command.append("--stage-diagnostics")
            if finish_optimizer:
                command.append("--resume-optimizer")
            launch_round(command, log, finish_optimizer=finish_optimizer)
            # The learner can finish successfully even if an operator edits
            # an authored scene during the rollout. Never accept such mixed
            # environment observations into a verified checkpoint lineage.
            if file_sha256(Path(stage_run["path"])) != stage_run["sha256"]:
                raise ValueError("native curriculum source changed during rollout")
            check_native_input_dependencies(recipe)
            record, next_manifest, next_checkpoint = verify_round(
                directory, file_sha256(next_manifest), file_sha256(next_checkpoint),
                args.learner_slot, refreshing, stage_run["sha256"],
            )
            completed.append((directory, record))
            latest_playable = publish_latest_playable(
                root, recipe, completed, next_manifest, next_checkpoint,
            )
            # A successfully learned checkpoint remains committed even if
            # its subsequent holdout run fails. Resume evaluates this exact
            # artifact rather than collecting another training round.
            atomic_json(root / "session-progress.json", progress_snapshot(
                completed, args.rounds, evaluations, baseline_holdout, best_evaluated,
                recipe=recipe, latest_playable=latest_playable,
                monitor_baseline=monitor_baseline,
                monitor_observations=monitor_observations,
            ))
            if "evaluation_config" in recipe:
                due = evaluation_due(index, args.rounds, recipe)
                if due:
                    check_native_input_dependencies(recipe)
                    evaluations[str(directory)] = ensure_evaluation(
                        directory, next_manifest, recipe,
                    )
                    checked = evaluate_monitor_suites(
                        directory, next_manifest, recipe, due=True,
                    )
                    if checked:
                        monitor_observations[str(directory)] = checked
                # A scene transition changes the playable world even if
                # there is no holdout at this optimizer round. Re-publish
                # the already verified best checkpoint for the new Level.
                if due or (curriculum is not None and stage_start == index):
                    best_evaluated = publish_best_evaluated(
                        root, recipe, completed, evaluations, baseline_holdout,
                        monitor_baseline, monitor_observations,
                    )
            stop_state = early_stop_decision(
                completed, evaluations, baseline_holdout, recipe,
            )
            snapshot = progress_snapshot(
                completed, args.rounds, evaluations, baseline_holdout, best_evaluated,
                stop_state, recipe, latest_playable,
                monitor_baseline=monitor_baseline,
                monitor_observations=monitor_observations,
            )
            atomic_json(root / "session-progress.json", snapshot)
            print(json.dumps({"kind": "native_training_session_round_complete",
                              "round": index, "directory": str(directory),
                              "curriculum_stage_start_round": stage_start,
                              "source_run_config_sha256": stage_run["sha256"],
                              "world_steps": record["rollout_metrics"]["successful_world_steps"],
                              "optimizer_input_steps": record["learner_metrics"]["optimizer_input_steps"],
                              "next_checkpoint_sha256": record["next_checkpoint_sha256"],
                              "latest_playable_run": latest_playable["playable_run"],
                              "latest_training_outcomes": snapshot["latest_training_outcomes"],
                              "monitoring_suite_count": len(monitor_baseline)}),
                  flush=True)
        stopped_early = (len(completed) < args.rounds and
                         stop_state is not None and stop_state["stop"])
        if stopped_early:
            print(json.dumps({
                "kind": "native_training_session_early_stopped",
                "completed_rounds": len(completed),
                "target_rounds": args.rounds,
                "stopping": stop_state,
                "best_playable_run": best_evaluated["playable_run"],
                "best_checkpoint_sha256":
                    best_evaluated["selected"]["checkpoint_sha256"],
            }), flush=True)
        print(json.dumps({"kind": "native_training_session_complete",
                          "progress": str(root / "session-progress.json"),
                          "completed_rounds": len(completed),
                          "target_rounds": args.rounds,
                          "stopped_early": stopped_early,
                          "stop_reason": stop_state["reason"] if stopped_early else None,
                          "checkpoint": str(next_checkpoint),
                          "manifest": str(next_manifest),
                          "latest_playable_run": latest_playable["playable_run"],
                          "best_playable_run":
                              best_evaluated["playable_run"] if best_evaluated else None}),
              flush=True)


if __name__ == "__main__":
    main()
