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
from urllib.error import URLError
from urllib.request import urlopen

from native_training_artifacts import atomic_json, check_learner, file_sha256
from native_bundle_receiver import BundleReceiver
from transfer_policy_inputs import export_inputs
from native_distributed_collection import (
    read_plan, make_orders, wait_for_collections, balanced_round_plan,
    derive_hub_resource_plan,
)
from native_session_selection import publish_best_evaluated


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
        jobs = {job["id"]: job for job in orders["jobs"]}
        reported = dispatched.get("collectors")
        if not isinstance(reported, list) or len(reported) != len(jobs):
            raise ValueError("Hub dispatch did not return every issued collector")
        self.members = {}
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
            self.members[name] = (hub_job, machine, jobs[name]["collection_id"])
        self.last_checked = float("-inf")
        self.checks = 0
        self.coordinator_unavailable = 0
        self.last_reported_unavailable = False

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
            if (job.get("project_id") != "bees_native_collect" or
                    job.get("assigned_machine_id") != machine or
                    not isinstance(params, dict) or
                    params.get("collector_id") != collector or
                    params.get("collection_id") != collection_id):
                raise ValueError(f"Hub native collector {collector} changed its frozen job identity")
            if job.get("status") in ("failed", "cancelled", "stopped"):
                reason = str(job.get("status_reason") or "")[:240]
                raise RuntimeError(
                    f"native collector {collector} on {machine} ({job_id}) "
                    f"is {job['status']} in Hub: {reason}"
                )
        # If the coordinated Start becomes review-required, no additional
        # collector can safely be launched under this approval.
        plans = snapshot.get("bundle_start_plans") or []
        active_ids = {self.members[name][0] for name in missing}
        for plan in plans:
            if not isinstance(plan, dict):
                continue
            ids = plan.get("member_job_ids")
            if (isinstance(ids, list) and active_ids.intersection(ids) and
                    plan.get("status") in ("needs_review", "disarmed", "expired")):
                raise RuntimeError(
                    f"native Hub collection Start requires review: "
                    f"{str(plan.get('reason') or plan['status'])[:240]}"
                )


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


def verify_round(directory, manifest_sha, checkpoint_sha, learner_slot,
                 refresh_opponent_slot=None):
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
    checkpoint = directory / "learner" / "checkpoint.pt"
    manifest = directory / "learner" / "manifest.json"
    for key, path in (("next_checkpoint", checkpoint), ("next_manifest", manifest)):
        if Path(record[key]).resolve() != path.resolve(strict=True):
            raise ValueError(f"round receipt points outside its own learner: {receipt_path}")
        if file_sha256(path) != record[key + "_sha256"]:
            raise ValueError(f"round completion artifact changed: {path}")
    metrics = record["learner_metrics"]
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
                    self_play=None):
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
            break
        record, manifest, checkpoint = verify_round(
            valid[0], manifest_sha, checkpoint_sha, learner_slot,
            scheduled_opponent_slot(self_play, number),
        )
        completed.append((valid[0], record))
        manifest_sha = record["next_manifest_sha256"]
        checkpoint_sha = record["next_checkpoint_sha256"]
    else:
        next_attempt = 1
    return completed, manifest, checkpoint, next_attempt


def verify_evaluation(directory, manifest_sha, source_sha, learner_side, policy_seed):
    result = read_json(directory / "evaluation-result.json")
    summary_path = directory / "rollout-summary.json"
    if (result.get("kind") != "native_policy_evaluation_complete" or
            result.get("schema_version") != 1 or
            result.get("model_manifest_sha256") != manifest_sha or
            result.get("source_run_config_sha256") != source_sha or
            result.get("learner_side") != learner_side or
            result.get("policy_sampling_seed") != policy_seed or
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
                                 recipe["evaluation_side"], recipe["evaluation_policy_seed"])
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
    print(json.dumps({"kind":"native_training_session_evaluation_started",
                      "round_directory":str(round_directory), "attempt":attempt}),flush=True)
    launch_round(command, round_directory / f"evaluation-attempt-{attempt:03d}.log")
    return verify_evaluation(output, manifest_sha, source_sha,
                             recipe["evaluation_side"], recipe["evaluation_policy_seed"])



def evaluation_due(index, target, recipe):
    """Always evaluate the final completed candidate, even off cadence.

    Already-issued holdout attempts are also completed on recovery;
    changing --rounds must not strand a previously committed evaluation.
    """
    return index % recipe.get("evaluation_every", 1) == 0 or index == target


def evaluation_attempt_exists(directory):
    return any(EVALUATION_ATTEMPT.fullmatch(path.name)
               for path in directory.iterdir())


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
        measured.append({
            "round": number,
            "world_steps": steps,
            "policy_rows": rows,
            "work_order_to_last_receipt_wall_seconds": seconds,
            "world_steps_per_wall_second": steps / seconds,
            "policy_rows_per_wall_second": rows / seconds,
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
    anchor_wins = baseline_holdout["side_victories"]
    best_wins = anchor_wins
    last_meaningful_round = 0
    last_meaningful_evaluation = 0
    evaluated = 0
    every = recipe.get("evaluation_every", 1)
    for index, (directory, _) in enumerate(completed, 1):
        result = evaluations.get(str(directory))
        if result is None:
            if index % every == 0:
                raise ValueError(f"missing scheduled native holdout: {directory}")
            continue
        if (result["episodes"] != episodes or
                result["learner_side"] != baseline_holdout["learner_side"]):
            raise ValueError("early stopping cannot compare different holdout conditions")
        evaluated += 1
        wins = result["side_victories"]
        best_wins = max(best_wins, wins)
        if wins >= anchor_wins + policy["minimum_extra_victories"]:
            anchor_wins = wins
            last_meaningful_round = index
            last_meaningful_evaluation = evaluated
    no_gain = evaluated - last_meaningful_evaluation
    stop = (len(completed) >= policy["minimum_rounds"] and
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
        "patience_rounds": policy["patience_rounds"],
        "minimum_extra_victories": policy["minimum_extra_victories"],
        "best_holdout_victories": best_wins,
        "holdout_episodes": episodes,
        "last_meaningful_gain_round": last_meaningful_round,
        "evaluations_without_meaningful_gain": no_gain,
    }


def progress_snapshot(completed, target, evaluations=None, baseline_holdout=None,
                      best_evaluated=None, stopping=None):
    evaluations = evaluations or {}
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
    optimizer_steps = sum(record["learner_metrics"]["optimizer_input_steps"]
                          for _, record in completed)
    optimizer_seconds = sum(record["learner_metrics"]["total_wall_seconds"]
                            for _, record in completed)
    latest = completed[-1][1]["learner_metrics"].get("training_outcomes", {}) if completed else {}
    rollout_scope = (
        "sum_of_independent_collector_wall_seconds"
        if any("collection_orders" in record for _, record in completed)
        else "local_rollout_wall_seconds"
    )
    fleet_window = measured_distributed_collection_windows(completed)
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
            "useful_world_steps": holdout_steps,
            "sum_holdout_simulation_wall_seconds": holdout_wall,
            "world_steps_per_holdout_simulation_wall_second":
                holdout_steps / holdout_wall if holdout_wall > 0 else None,
        }} if all_holdouts else {}),
        "latest_holdout_win_rate_gain": holdout_win_rate_gain,
        **({"holdout_stopping": stopping} if stopping is not None else {}),
        **({"best_evaluated": {
            "round": best_evaluated["selected"]["round"],
            "side_win_rate": best_evaluated["selected"]["win_rate"],
            "checkpoint_sha256": best_evaluated["selected"]["checkpoint_sha256"],
            "playable_run": best_evaluated["playable_run"],
            "evaluated_rounds": best_evaluated["evaluated_rounds"],
        }} if best_evaluated is not None else {}),
        "completed_rounds": len(completed), "target_rounds": target,
        "total_world_steps": world_steps,
        "total_policy_rows": policy_rows,
        "total_optimizer_input_steps": optimizer_steps,
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
             "holdout": evaluations.get(str(path))}
            for index, (path, record) in enumerate(completed, 1)
        ],
    }


def launch_round(command, log_path):
    with log_path.open("x", encoding="utf-8") as stream:
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
    parser.add_argument("--evaluation-run-config", type=Path,
                        help="independent fixed-seed Levels evaluated after each completed update")
    parser.add_argument("--evaluation-side", type=int,
                        help="source side 1 or 2 whose victories are measured")
    parser.add_argument("--evaluation-policy-seed", type=int, default=0,
                        help="fixed policy action-sampling RNG seed during holdout evaluation")
    parser.add_argument("--evaluation-every", type=int, default=1,
                        help="run a pinned holdout every N optimizer rounds and at the final target (default: 1)")
    parser.add_argument("--early-stop-patience", type=int,
                        help="stop after this many evaluated rounds without meaningful fixed-reference victories")
    parser.add_argument("--early-stop-minimum-rounds", type=int, default=3,
                        help="never stop before this many completed optimizer rounds (default: 3)")
    parser.add_argument("--early-stop-minimum-extra-victories", type=int, default=1,
                        help="minimum additional holdout wins for meaningful progress (default: 1)")
    parser.add_argument("--learner-slot", type=int)
    parser.add_argument("--self-play-opponent-slot", type=int,
                        help="existing MA-POCA slot to refresh from the completed learner checkpoint")
    parser.add_argument("--self-play-refresh-every", type=int,
                        help="number of finished optimizer rounds between opponent promotions")
    parser.add_argument("--rollout-binary", type=Path)
    parser.add_argument("--rollout-workers", type=int)
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
    parser.add_argument("--stage-diagnostics", action="store_true")
    args = parser.parse_args()
    if not 1 <= args.rounds <= 99999:
        parser.error("--rounds must be between 1 and 99999")
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
            args.physics_workers,
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
    round_python = (str((Path(run["path"]).parent / python_path).resolve())
                    if python_path.is_absolute() or len(python_path.parts) > 1
                    else python_value)
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
    # Preserve existing session fingerprints when this new resource
    # selection was not specified; older recipes have no such field.
    if args.physics_workers is not None:
        recipe["physics_workers"] = args.physics_workers
    if self_play is not None:
        recipe["self_play"] = self_play
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
    if args.early_stop_patience is not None:
        recipe["early_stop"] = {
            "patience_rounds": args.early_stop_patience,
            "minimum_rounds": args.early_stop_minimum_rounds,
            "minimum_extra_victories": args.early_stop_minimum_extra_victories,
        }
    recipe["source_dependencies"] = native_input_dependencies(
        run, recipe.get("evaluation_config"),
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
                if not allocation_path.is_file() or read_json(allocation_path) != derived:
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
            recipe.get("self_play"),
        )
        evaluations = {}
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
            for index, (directory, record) in enumerate(completed, 1):
                if evaluation_due(index, args.rounds, recipe) or evaluation_attempt_exists(directory):
                    evaluations[str(directory)] = ensure_evaluation(
                        directory, Path(record["next_manifest"]), recipe,
                    )
            best_evaluated = publish_best_evaluated(
                root, recipe, completed, evaluations, baseline_holdout,
            )
        stop_state = early_stop_decision(
            completed, evaluations, baseline_holdout, recipe,
        )
        if stop_state is not None and (
                stop_state["minimum_extra_victories"] > stop_state["holdout_episodes"]):
            raise ValueError("early-stop meaningful improvement exceeds available holdout episodes")
        atomic_json(root / "session-progress.json", progress_snapshot(
            completed, args.rounds, evaluations, baseline_holdout, best_evaluated, stop_state,
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
            check_native_input_dependencies(recipe)
            attempt = next_attempt if index == first_pending_round else 1
            if attempt > 999:
                raise ValueError(f"round {index} exhausted its attempt numbers")
            directory = root / f"round-{index:05d}-attempt-{attempt:03d}"
            log = root / (directory.name + ".log")
            seed_offset = args.seed_offset_start + (index - 1) * args.seed_offset_stride
            print(json.dumps({"kind": "native_training_session_round_started",
                              "round": index, "target_rounds": args.rounds,
                              "directory": str(directory), "seed_offset": seed_offset}), flush=True)
            command = [
                round_python, str(Path(__file__).with_name("train_policy_round.py")),
                str(template if distributed else run["path"]),
                str(next_checkpoint), trainer["path"], str(directory),
                "--model-manifest", str(next_manifest),
                "--device", args.device,
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
                    run, next_checkpoint, next_manifest, args.learner_slot,
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
                    if archive.exists() or receipt_path.exists():
                        if not archive.is_file() or not receipt_path.is_file():
                            raise ValueError("previous native round source export is incomplete")
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
                    else:
                        result = export_inputs(
                            order_path, "@round", Path(run["path"]),
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
                if "hub_dispatch_plan" in recipe:
                    dispatch_started = time.monotonic()
                    pinned_plan = recipe["hub_dispatch_plan"]
                    if file_sha256(Path(pinned_plan["path"])) != pinned_plan["sha256"]:
                        raise ValueError("approved native Hub machine/resource dispatch plan changed")
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
                                      ("--physics-workers", args.physics_workers),
                                      ("--rollout-worlds", args.rollout_worlds),
                                      ("--rollout-world-start", args.rollout_world_start)):
                    if value is not None:
                        command.extend([option, str(value)])
                if binary is not None:
                    command.extend(["--rollout-binary", binary["path"]])
            if args.stage_diagnostics:
                command.append("--stage-diagnostics")
            launch_round(command, log)
            record, next_manifest, next_checkpoint = verify_round(
                directory, file_sha256(next_manifest), file_sha256(next_checkpoint),
                args.learner_slot, refreshing,
            )
            completed.append((directory, record))
            # A successfully learned checkpoint remains committed even if
            # its subsequent holdout run fails. Resume evaluates this exact
            # artifact rather than collecting another training round.
            atomic_json(root / "session-progress.json", progress_snapshot(
                completed, args.rounds, evaluations, baseline_holdout, best_evaluated,
            ))
            if "evaluation_config" in recipe and evaluation_due(index, args.rounds, recipe):
                check_native_input_dependencies(recipe)
                evaluations[str(directory)] = ensure_evaluation(directory, next_manifest, recipe)
                best_evaluated = publish_best_evaluated(
                    root, recipe, completed, evaluations, baseline_holdout,
                )
            stop_state = early_stop_decision(
                completed, evaluations, baseline_holdout, recipe,
            )
            snapshot = progress_snapshot(
                completed, args.rounds, evaluations, baseline_holdout, best_evaluated,
                stop_state,
            )
            atomic_json(root / "session-progress.json", snapshot)
            print(json.dumps({"kind": "native_training_session_round_complete",
                              "round": index, "directory": str(directory),
                              "world_steps": record["rollout_metrics"]["successful_world_steps"],
                              "optimizer_input_steps": record["learner_metrics"]["optimizer_input_steps"],
                              "next_checkpoint_sha256": record["next_checkpoint_sha256"],
                              "latest_training_outcomes": snapshot["latest_training_outcomes"]}),
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
                          "best_playable_run":
                              best_evaluated["playable_run"] if best_evaluated else None}),
              flush=True)


if __name__ == "__main__":
    main()
