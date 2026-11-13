"""Run consecutive unified-native MA-POCA training rounds without manual model handoffs.

One session owns a checkpoint lineage; each round still uses the established
native collector and learner. Interruption preserves all partial attempts and
completed artifacts. Completed round receipts, not a mutable progress cursor,
are authoritative for resumption.
"""

import argparse
from collections import defaultdict
from contextlib import contextmanager
import json
import os
from pathlib import Path
import re
import signal
import subprocess
import sys

from native_training_artifacts import atomic_json, check_learner, file_sha256
from native_distributed_collection import read_plan, make_orders, wait_for_collections


ATTEMPT = re.compile(r"round-(\d{5})-attempt-(\d{3})\Z")
EVALUATION_ATTEMPT = re.compile(r"evaluation-attempt-(\d{3})\Z")


def pinned(path):
    path = path.resolve(strict=True)
    if not path.is_file():
        raise ValueError(f"expected a file: {path}")
    return {"path": str(path), "sha256": file_sha256(path)}


def read_json(path):
    return json.loads(path.read_text(encoding="utf-8"))


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


def verify_round(directory, manifest_sha, checkpoint_sha, learner_slot):
    receipt_path = directory / "round-result.json"
    record = read_json(receipt_path)
    if (record.get("kind") != "native_training_round_complete" or
            record.get("schema_version") != 1 or
            record.get("input_manifest_sha256") != manifest_sha or
            record.get("input_checkpoint_sha256") != checkpoint_sha or
            record.get("learner_metrics", {}).get("learner_slot") != learner_slot):
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
    if "collection_orders" in record:
        work_order = Path(record["collection_orders"])
        if (not work_order.is_file() or
                file_sha256(work_order) != record["collection_orders_sha256"]):
            raise ValueError(f"distributed collection provenance changed: {directory}")
        for bundle in record.get("rollout_bundles", []):
            if file_sha256(Path(bundle["path"])) != bundle["sha256"]:
                raise ValueError(f"distributed rollout receipt changed: {bundle['path']}")
    return record, manifest, checkpoint


def existing_rounds(root, initial_manifest, initial_checkpoint, learner_slot):
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


def ensure_evaluation(round_directory, manifest, recipe):
    eval_source = recipe["evaluation_config"]
    source_sha = eval_source["sha256"]
    if file_sha256(Path(eval_source["path"])) != source_sha:
        raise ValueError("holdout evaluation configuration changed during session")
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


def progress_snapshot(completed, target, evaluations=None):
    evaluations = evaluations or {}
    world_steps = sum(record["rollout_metrics"]["successful_world_steps"]
                      for _, record in completed)
    policy_rows = sum(record["rollout_metrics"]["successful_policy_rows"]
                      for _, record in completed)
    rollout_seconds = sum(record["rollout_metrics"]["wall_seconds"]
                          for _, record in completed)
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
    latest_holdout = evaluations.get(str(completed[-1][0])) if completed else None
    return {
        "schema_version": 1, "kind": "native_training_session_progress",
        "evaluated_rounds": len(evaluations),
        "latest_holdout": latest_holdout,
        "completed_rounds": len(completed), "target_rounds": target,
        "total_world_steps": world_steps,
        "total_policy_rows": policy_rows,
        "total_optimizer_input_steps": optimizer_steps,
        "sum_round_rollout_wall_seconds": rollout_seconds,
        "rollout_wall_seconds_scope": rollout_scope,
        "sum_round_optimizer_wall_seconds": optimizer_seconds,
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
             "optimizer_input_steps": record["learner_metrics"]["optimizer_input_steps"],
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
    parser.add_argument("--learner-slot", type=int)
    parser.add_argument("--rollout-binary", type=Path)
    parser.add_argument("--rollout-workers", type=int)
    parser.add_argument("--rollout-worlds", type=int)
    parser.add_argument("--rollout-world-start", type=int)
    parser.add_argument("--distributed-plan", type=Path,
                        help="explicit multi-machine collector windows; Experiment Hub executes orders")
    parser.add_argument("--bundle-inbox", type=Path,
                        help="shared directory for collector deliveries in distributed mode")
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
    if ((args.evaluation_run_config is None) != (args.evaluation_side is None) or
            args.evaluation_side is not None and args.evaluation_side not in (1, 2)):
        parser.error("specify both --evaluation-run-config and --evaluation-side 1 or 2")
    if (not 0 <= args.evaluation_policy_seed < (1 << 63) or
            args.evaluation_run_config is None and args.evaluation_policy_seed != 0):
        parser.error("--evaluation-policy-seed requires a holdout and a nonnegative int63 seed")
    if (args.seed_offset_start < 0 or args.seed_offset_stride < 1 or
            args.seed_offset_start + (args.rounds - 1) * args.seed_offset_stride >= (1 << 64)):
        parser.error("session seed offsets must be distinct and fit uint64")
    if args.rollout_workers is not None and not 1 <= args.rollout_workers <= 256:
        parser.error("rollout workers must be between 1 and 256")
    if args.rollout_worlds is not None and not 1 <= args.rollout_worlds <= 1024:
        parser.error("rollout worlds must be between 1 and 1024")
    if args.rollout_world_start is not None and args.rollout_world_start < 0:
        parser.error("rollout world start must be nonnegative")
    if args.torch_threads is not None and not 1 <= args.torch_threads <= 256:
        parser.error("torch threads must be between 1 and 256")
    if (args.distributed_plan is None) != (args.bundle_inbox is None):
        parser.error("distributed sessions require both --distributed-plan and --bundle-inbox")
    if args.collector_timeout_seconds < 0:
        parser.error("--collector-timeout-seconds cannot be negative")
    if args.distributed_plan is not None and any(
        value is not None for value in (
            args.rollout_workers, args.rollout_worlds, args.rollout_world_start,
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
    if args.distributed_plan is not None:
        recipe["distributed_plan"] = pinned(args.distributed_plan)
        recipe["bundle_inbox"] = str(args.bundle_inbox.resolve(strict=True))
        recipe["collector_timeout_seconds"] = args.collector_timeout_seconds
        read_plan(Path(recipe["distributed_plan"]["path"]))
    if args.evaluation_run_config is not None:
        recipe["evaluation_config"] = pinned(args.evaluation_run_config)
        recipe["evaluation_side"] = args.evaluation_side
        recipe["evaluation_policy_seed"] = args.evaluation_policy_seed
    root = args.output_dir.resolve()
    if args.resume:
        if not root.is_dir():
            parser.error("cannot resume a session without its output directory")
    else:
        root.mkdir(parents=True, exist_ok=False)
    with session_lock(root):
        config_path = root / "session-config.json"
        if args.resume:
            if read_json(config_path) != recipe:
                raise ValueError("session inputs or resource allocation changed; start a new session")
        else:
            if config_path.exists():
                raise ValueError("session configuration already exists")
            atomic_json(config_path, recipe)
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
        )
        evaluations = {}
        if "evaluation_config" in recipe:
            for directory, record in completed:
                evaluations[str(directory)] = ensure_evaluation(
                    directory, Path(record["next_manifest"]), recipe,
                )
        atomic_json(root / "session-progress.json", progress_snapshot(
            completed, args.rounds, evaluations,
        ))
        if len(completed) > args.rounds:
            raise ValueError("target round count is smaller than the completed checkpoint lineage")
        first_pending_round = len(completed) + 1
        for index in range(first_pending_round, args.rounds + 1):
            # A source scene, trainer settings, or rollout binary changed
            # between rounds would make throughput and learning quality
            # incomparable, even when the model lineage remains intact.
            for label in ("run_config", "trainer_config", "rollout_binary"):
                source = recipe[label]
                if source is not None and file_sha256(Path(source["path"])) != source["sha256"]:
                    raise ValueError(f"{label} changed during this training session")
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
            if args.learner_slot is not None:
                command.extend(["--learner-slot", str(args.learner_slot)])
            if args.torch_threads is not None:
                command.extend(["--torch-threads", str(args.torch_threads)])
            if distributed:
                plan_path = Path(recipe["distributed_plan"]["path"])
                if file_sha256(plan_path) != recipe["distributed_plan"]["sha256"]:
                    raise ValueError("distributed collector allocations changed during session")
                orders, order_path = make_orders(
                    root, index, seed_offset, read_plan(plan_path),
                    run, next_checkpoint, next_manifest, args.learner_slot,
                )
                print(json.dumps({"kind": "native_distributed_collection_orders_ready",
                                  "round": index, "orders": str(order_path),
                                  "jobs": orders["jobs"]}), flush=True)
                bundles = wait_for_collections(
                    Path(recipe["bundle_inbox"]), orders,
                    args.collector_timeout_seconds,
                    progress=lambda kind, details: print(json.dumps(
                        {"kind": kind, "round": index, **details},
                    ), flush=True),
                )
                command.extend(["--collection-orders", str(order_path)])
                for bundle in bundles:
                    command.extend([
                        "--pinned-rollout-bundle", bundle["path"], bundle["sha256"],
                    ])
            else:
                command.extend(["--seed-offset", str(seed_offset)])
                for option, value in (("--rollout-workers", args.rollout_workers),
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
                args.learner_slot,
            )
            completed.append((directory, record))
            # A successfully learned checkpoint remains committed even if
            # its subsequent holdout run fails. Resume evaluates this exact
            # artifact rather than collecting another training round.
            atomic_json(root / "session-progress.json", progress_snapshot(
                completed, args.rounds, evaluations,
            ))
            if "evaluation_config" in recipe:
                evaluations[str(directory)] = ensure_evaluation(directory, next_manifest, recipe)
            snapshot = progress_snapshot(completed, args.rounds, evaluations)
            atomic_json(root / "session-progress.json", snapshot)
            print(json.dumps({"kind": "native_training_session_round_complete",
                              "round": index, "directory": str(directory),
                              "world_steps": record["rollout_metrics"]["successful_world_steps"],
                              "optimizer_input_steps": record["learner_metrics"]["optimizer_input_steps"],
                              "next_checkpoint_sha256": record["next_checkpoint_sha256"],
                              "latest_training_outcomes": snapshot["latest_training_outcomes"]}),
                  flush=True)
        print(json.dumps({"kind": "native_training_session_complete",
                          "progress": str(root / "session-progress.json"),
                          "completed_rounds": len(completed),
                          "checkpoint": str(next_checkpoint),
                          "manifest": str(next_manifest)}), flush=True)


if __name__ == "__main__":
    main()
