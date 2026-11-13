"""Run one native rollout and one MA-POCA update through the existing executables.

A single-round entry point for operators or the Experiment Hub, not a new
scheduler, simulator or optimizer. Each round uses fresh output paths and
never converts a failed or incomplete rollout into training data.
"""

import argparse
import copy
from contextlib import ExitStack
import hashlib
import json
import math
import os
from pathlib import Path
import shutil
import signal
import subprocess
from tempfile import TemporaryDirectory
import time
import traceback
import uuid

from policy_worker import file_sha256


def resolved(base: Path, value: str) -> str:
    path = Path(value)
    return str((path if path.is_absolute() else base / path).resolve())


def source_config(path: Path, manifest: Path, output: Path) -> dict:
    config = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(config, dict):
        raise ValueError("native run configuration must be an object")
    if config.get("model_updates"):
        raise ValueError("one training round cannot include scheduled model updates")
    if not isinstance(config.get("run_id"), str) or not config["run_id"].strip():
        raise ValueError("native run requires a nonempty run_id")
    base = path.parent
    for key in ("collision_matrix", "stage_palette", "policy_worker"):
        if not isinstance(config.get(key), str) or not config[key]:
            raise ValueError(f"run configuration requires {key}")
        config[key] = resolved(base, config[key])
    if (config.get("hivemind") is not None or config.get("recover_notifications")
            or config.get("recover_outcome_journals")):
        previous = config.get("connection_checkpoint")
        if not isinstance(previous, str) or not previous:
            raise ValueError("network or recovery runs require the original connection checkpoint")
        config["connection_checkpoint"] = resolved(base, previous)
    else:
        # No cross-run HiveMind history belongs to independent model-only training.
        config["connection_checkpoint"] = str(output / "connection-checkpoint.json")
    interpreter = config.get("python")
    if not isinstance(interpreter, str) or not interpreter:
        raise ValueError("run configuration requires its native policy Python interpreter")
    if Path(interpreter).is_absolute() or len(Path(interpreter).parts) > 1:
        config["python"] = resolved(base, interpreter)
    levels = config.get("levels")
    if not isinstance(levels, list) or not levels:
        raise ValueError("run configuration requires at least one Level")
    for level in levels:
        if not isinstance(level, dict) or not isinstance(level.get("scene"), str):
            raise ValueError("each native Level requires a source scene path")
        level["scene"] = resolved(base, level["scene"])
    for index, previous in enumerate(config.get("recover_notifications", [])):
        config["recover_notifications"][index] = resolved(base, previous)
    for previous in config.get("recover_outcome_journals", []):
        previous["path"] = resolved(base, previous["path"])
    config["model_manifest"] = str(manifest)
    return config


def shifted_seeds(config: dict, offset: int) -> None:
    """Shift only source episode RNG seeds, never authored entity identities."""
    if not offset:
        return
    for level in config["levels"]:
        if level.get("seed") is not None:
            fields = [level["seed"]]
            label = "seed"
        elif isinstance(level.get("episode_seeds"), list):
            fields = level["episode_seeds"]
            label = "episode_seeds"
        else:
            raise ValueError("seed offset requires authored seeds for every Level")
        if any(type(seed) is not int or not 0 <= seed < 1 << 64 for seed in fields):
            raise ValueError("seed offset requires unsigned 64-bit source seeds")
        updated = [(seed + offset) % (1 << 64) for seed in fields]
        level[label] = updated[0] if label == "seed" else updated


def expand_rollout_worlds(config: dict, requested_worlds: int, world_start: int = 0) -> None:
    """Allocate a reproducible window of authentic source Level instances.

    Global world indices are stable across independent machine collectors.
    Index 0..source_count-1 uses the exact authored IDs and seed plans;
    later indices get noncolliding IDs and deterministic derived seeds.
    Native scene construction still remaps the source squad lifetimes.
    """
    originals = tuple(config["levels"])
    source_count = len(originals)
    if (type(requested_worlds) is not int or
            not 1 <= requested_worlds <= 1024 or
            type(world_start) is not int or world_start < 0):
        raise ValueError("rollout world window requires 1..1024 Worlds and a nonnegative start")
    if world_start == 0 and requested_worlds == source_count:
        return

    if config.get("hivemind") is not None or any(config.get(key) for key in (
        "recover_notifications", "recover_outcome_journals", "model_updates",
    )):
        raise ValueError("partitioned native rollouts require model-only, unrecovered Levels")
    for key, maximum in (("world_id", (1 << 64) - 1),
                         ("level_id", (1 << 63) - 1)):
        ids = [level.get(key) for level in originals]
        last = world_start + requested_worlds - 1
        # The source's own IDs are always reused for its first occurrence.
        # Replica IDs occupy one continuous interval above their maximum.
        extra = max(0, last - source_count + 1)
        if (any(type(value) is not int or not 1 <= value <= maximum for value in ids)
                or len(set(ids)) != source_count
                or max(ids) > maximum - extra):
            raise ValueError(f"native partition has invalid or exhausted {key} identities")

    episodes = config.get("episodes_per_world", 1)
    for level in originals:
        has_seed = type(level.get("seed")) is int and level.get("episode_seeds") is None
        has_episode_seeds = (
            level.get("seed") is None and isinstance(level.get("episode_seeds"), list)
            and len(level["episode_seeds"]) == episodes
        )
        if not has_seed and not has_episode_seeds:
            raise ValueError("partitioned Level needs an authored seed or episode seed plan")
        values = [level["seed"]] if has_seed else level["episode_seeds"]
        if any(type(seed) is not int or not 0 <= seed < (1 << 64) for seed in values):
            raise ValueError("partitioned Level seeds must be unsigned 64-bit integers")

    highest_world = max(level["world_id"] for level in originals)
    highest_level = max(level["level_id"] for level in originals)
    selected = []
    for index in range(world_start, world_start + requested_worlds):
        source_index = index % source_count
        replica = index // source_count
        clone = copy.deepcopy(originals[source_index])
        if replica:
            clone["world_id"] = highest_world + index - source_count + 1
            clone["level_id"] = highest_level + index - source_count + 1

            def derive(seed: int, episode: int) -> int:
                label = (f"bees-native-world-v1:{config['run_id']}:{source_index}:"
                         f"{replica}:{episode}:{seed}")
                return int.from_bytes(hashlib.sha256(label.encode("utf-8")).digest()[:8], "little")

            if clone.get("seed") is not None:
                clone["seed"] = derive(clone["seed"], 0)
            else:
                clone["episode_seeds"] = [
                    derive(seed, episode) for episode, seed in enumerate(clone["episode_seeds"])
                ]
        selected.append(clone)
    config["levels"] = selected


def check_learner(manifest: Path, checkpoint: Path, slot: int | None) -> str:
    record = json.loads(manifest.read_text(encoding="utf-8"))
    if record.get("format") != "bees-native-model-v1" or not isinstance(record.get("models"), list):
        raise ValueError("native training requires a generated model manifest")
    digest = file_sha256(checkpoint)
    models = record["models"]
    matching = {
        item["slot"] for item in models
        if item["backend"] == "mlagents" and item["sha256"] == digest
    }
    if slot is None:
        if not matching or matching != {item["slot"] for item in models}:
            raise ValueError("mixed-checkpoint training requires --learner-slot")
    elif slot not in matching:
        raise ValueError("learner slot must identify the supplied input checkpoint")
    return digest


def run_stage(command, diagnostic_path):
    if diagnostic_path is None:
        subprocess.run(command, check=True)
    else:
        with diagnostic_path.open("x", encoding="utf-8") as stream:
            try:
                subprocess.run(command, check=True, stdout=stream, stderr=subprocess.STDOUT)
            except Exception:
                traceback.print_exc(file=stream)
                raise


def rollout_metrics(summary_path, config, manifest_sha):
    summary = json.loads(summary_path.read_text(encoding="utf-8"))
    expected_episodes = len(config["levels"]) * config.get("episodes_per_world", 1)
    if (summary["kind"] != "run_final" or summary["run_id"] != config["run_id"] or
            summary["policy_manifest_sha256"] != manifest_sha or
            summary["completed_episodes"] != expected_episodes or
            any(summary[key] != 0 for key in ("aborted_worlds", "failed_worlds", "running_worlds")) or
            summary["failed_window"] is not None or summary["cleanup_errors"]):
        raise ValueError("native rollout summary does not identify a completed training rollout")
    throughput = summary["throughput"]
    counts = {key: throughput[key] for key in ("successful_world_steps", "successful_policy_rows")}
    times = {key: throughput[key] for key in (
        "wall_seconds", "startup_wall_seconds", "execution_wall_seconds", "finalization_wall_seconds",
    )}
    if (any(type(value) is not int or value < 1 for value in counts.values()) or
            summary["captured_policy_rows"] != counts["successful_policy_rows"] or
            any(type(value) not in (float, int) or not math.isfinite(value) or value < 0
                for value in times.values()) or times["wall_seconds"] <= 0 or
            times["execution_wall_seconds"] <= 0):
        raise ValueError("native rollout summary contains invalid work or timing")
    metrics = {
        **counts, **times,
        "worlds": len(config["levels"]),
        "episodes": expected_episodes,
        # Overall wall includes child startup/finalization. The execution
        # denominator isolates actual simulator work on this shard.
        "world_steps_per_wall_second": counts["successful_world_steps"] / times["wall_seconds"],
        "policy_rows_per_wall_second": counts["successful_policy_rows"] / times["wall_seconds"],
        "world_steps_per_execution_second":
            counts["successful_world_steps"] / times["execution_wall_seconds"],
        "policy_rows_per_execution_second":
            counts["successful_policy_rows"] / times["execution_wall_seconds"],
    }
    model_timing = summary.get("sampled_model_timing")
    if model_timing is not None:
        calls, rows, seconds = (model_timing[key] for key in ("calls", "rows", "wall_seconds"))
        if (type(calls) is not int or type(rows) is not int or
                not 0 < calls <= rows <= counts["successful_policy_rows"] or
                type(seconds) not in (float, int) or not math.isfinite(seconds) or seconds < 0):
            raise ValueError("native rollout summary contains invalid sampled model timing")
        metrics["sampled_model_timing"] = model_timing
    return metrics


def prepare_rollout(config, directory):
    config["run_id"] = f"{config['run_id']}-{uuid.uuid4().hex}"
    config["notifications"] = str(directory / "events.jsonl")
    config["policy_capture"] = str(directory / "decisions.bin")
    config["rollout_summary"] = str(directory / "rollout-summary.json")
    config_path = directory / "run.json"
    with config_path.open("x", encoding="utf-8") as stream:
        json.dump(config, stream, indent=2)
        stream.write("\n")
    return config_path


def combined_rollout_metrics(shards, wall_seconds):
    if not math.isfinite(wall_seconds) or wall_seconds <= 0:
        raise ValueError("parallel native rollout has invalid wall timing")
    metrics = {
        "workers": len(shards),
        "wall_seconds": wall_seconds,
        **{key: sum(shard[key] for shard in shards)
           for key in ("successful_world_steps", "successful_policy_rows", "worlds", "episodes")},
    }
    # The parallel wall denominator includes launch/join overhead and can
    # be compared against a one-process run without adding together
    # overlapping worker execution times.
    metrics["world_steps_per_wall_second"] = metrics["successful_world_steps"] / wall_seconds
    metrics["policy_rows_per_wall_second"] = metrics["successful_policy_rows"] / wall_seconds
    # Summary-only imbalance evidence. No per-world/per-frame logging or
    # scheduler feedback; each shard keeps its frozen share of Levels.
    metrics.update({
        "worker_execution_min_seconds": min(shard["execution_wall_seconds"] for shard in shards),
        "worker_execution_max_seconds": max(shard["execution_wall_seconds"] for shard in shards),
        "worker_world_steps_min": min(shard["successful_world_steps"] for shard in shards),
        "worker_world_steps_max": max(shard["successful_world_steps"] for shard in shards),
        "worker_world_steps_per_execution_second_min":
            min(shard["world_steps_per_execution_second"] for shard in shards),
        "worker_world_steps_per_execution_second_max":
            max(shard["world_steps_per_execution_second"] for shard in shards),
    })
    # Retain per-process evidence so a resource scheduler can distinguish
    # an overloaded process, a scene-dependent straggler and accelerator
    # inference time without requiring frame-level logs or guessed CPU use.
    metrics["worker_shards"] = [
        {
            "worker": index,
            "worlds": shard["worlds"],
            "episodes": shard["episodes"],
            "world_steps": shard["successful_world_steps"],
            "policy_rows": shard["successful_policy_rows"],
            "wall_seconds": shard["wall_seconds"],
            "execution_wall_seconds": shard["execution_wall_seconds"],
            "world_steps_per_execution_second":
                shard["world_steps_per_execution_second"],
            "policy_rows_per_execution_second":
                shard["policy_rows_per_execution_second"],
            **({"sampled_model_timing": shard["sampled_model_timing"]}
               if "sampled_model_timing" in shard else {}),
        }
        for index, shard in enumerate(shards)
    ]
    samples = [shard["sampled_model_timing"] for shard in shards if "sampled_model_timing" in shard]
    if samples:
        metrics["sampled_model_timing"] = {
            key: sum(sample[key] for sample in samples) for key in ("calls", "rows", "wall_seconds")
        }
    return metrics


def stop_parallel_rollout_workers(processes):
    """Retire only subprocesses launched by this collector, including their
    resident inference children. Do not abandon expensive orphaned workers
    when a sibling fails or operator interruption ends the round.
    """
    live = [process for process in processes if process.poll() is None]
    if not live:
        return
    if os.name == "nt":
        # Windows Popen.terminate only kills the Rust parent, leaving its
        # Python model worker behind. /T follows this owned process tree.
        for process in live:
            try:
                subprocess.run(
                    ["taskkill", "/PID", str(process.pid), "/T", "/F"],
                    stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                    check=False, timeout=5,
                )
            except (OSError, subprocess.TimeoutExpired):
                process.kill()
    else:
        # Each launched rollout has a separate session/process group.
        # A child policy backend inherits that group, so stopping its
        # owner does not leave a GPU inference process running.
        for process in live:
            try:
                os.killpg(process.pid, signal.SIGTERM)
            except ProcessLookupError:
                pass
        deadline = time.monotonic() + 1.0
        while time.monotonic() < deadline and any(
            process.poll() is None for process in live
        ):
            time.sleep(0.05)
        for process in live:
            try:
                os.killpg(process.pid, signal.SIGKILL)
            except ProcessLookupError:
                pass
    for process in live:
        try:
            process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            # Cancellation remains a failure; never publish a receipt.
            pass


def run_parallel_rollouts(shards, command, manifest_sha, diagnostics):
    """Supervise one immutable rollout subprocess per process allocation.

    Any failed exit OR invalid completed summary makes the collection
    unsalvageable; stop all remaining workers rather than burning their
    CPU/GPU budget and accepting an incomplete aggregate.
    """
    started = time.perf_counter()
    processes = []
    metrics = {}
    with ExitStack() as files:
        try:
            for shard, config_path in shards:
                log = (files.enter_context(
                    config_path.with_name("rollout.log").open("x", encoding="utf-8")
                ) if diagnostics else None)
                processes.append(subprocess.Popen(
                    [*command, str(config_path)],
                    stdout=log, stderr=subprocess.STDOUT if log is not None else None,
                    start_new_session=os.name != "nt",
                    creationflags=(subprocess.CREATE_NEW_PROCESS_GROUP if os.name == "nt" else 0),
                ))
            while len(metrics) != len(shards):
                for index, (shard, _) in enumerate(shards):
                    if index in metrics:
                        continue
                    returncode = processes[index].poll()
                    if returncode is None:
                        continue
                    if returncode != 0:
                        raise subprocess.CalledProcessError(
                            returncode, processes[index].args,
                        )
                    metrics[index] = rollout_metrics(
                        Path(shard["rollout_summary"]), shard, manifest_sha,
                    )
                if len(metrics) != len(shards):
                    time.sleep(0.1)
            if len(shards) == 1:
                # Retain the historical single-process receipt layout while
                # giving it the same supervised process-tree lifetime.
                return metrics[0]
            return combined_rollout_metrics(
                [metrics[index] for index in range(len(shards))],
                time.perf_counter() - started,
            )
        except BaseException:
            stop_parallel_rollout_workers(processes)
            raise


def sync_directory(path):
    if os.name == "posix":
        directory = os.open(path, os.O_RDONLY | getattr(os, "O_DIRECTORY", 0))
        try:
            os.fsync(directory)
        finally:
            os.close(directory)


def publish_receipt(output, filename, receipt, *, announce=True):
    pending = output / (filename + ".pending")
    with pending.open("x", encoding="utf-8") as stream:
        json.dump(receipt, stream, indent=2)
        stream.write("\n")
        stream.flush()
        os.fsync(stream.fileno())
    os.replace(pending, output / filename)
    sync_directory(output)
    if announce:
        print(json.dumps(receipt), flush=True)


def deliver_bundle(output, inbox, receipt):
    destination = inbox / output.name
    if destination.exists():
        raise FileExistsError(f"rollout inbox already contains {destination.name}")
    started = time.perf_counter()
    transferred = 0
    with TemporaryDirectory(prefix=".rollout-", dir=inbox) as temporary:
        stage = Path(temporary) / "bundle"
        stage.mkdir()
        for capture in receipt["captures"]:
            for record in capture.values():
                relative = Path(record["path"])
                target = stage / relative
                target.parent.mkdir(parents=True, exist_ok=True)
                digest = hashlib.sha256()
                with (output / relative).open("rb") as source, target.open("xb") as sink:
                    for chunk in iter(lambda: source.read(1024 * 1024), b""):
                        digest.update(chunk)
                        sink.write(chunk)
                        transferred += len(chunk)
                    sink.flush()
                    os.fsync(sink.fileno())
                if digest.hexdigest() != record["sha256"]:
                    raise ValueError("rollout artifact changed before inbox publication")
                if target.parent != stage:
                    sync_directory(target.parent)
        receipt["delivery"] = {"bytes": transferred, "copy_wall_seconds": time.perf_counter() - started}
        publish_receipt(stage, "rollout-bundle.json", receipt, announce=False)
        stage.rename(destination)
        sync_directory(inbox)
    return destination


def optimize(args, output, python, manifest, checkpoint, trainer_config, result, rollout_args):
    learner_dir = output / "learner"
    primary = [result["capture"], result["journal"]] if "capture" in result else []
    train_command = [
        python, str(Path(__file__).with_name("train_policy_capture.py")),
        *primary, str(manifest), str(checkpoint), str(trainer_config), str(learner_dir),
        "--device", args.device,
        *([] if args.torch_threads is None else ["--torch-threads", str(args.torch_threads)]),
        *rollout_args,
    ]
    if args.learner_slot is not None:
        train_command += ["--learner-slot", str(args.learner_slot)]
    run_stage(train_command, output / "optimizer.log" if args.stage_diagnostics else None)
    next_checkpoint = learner_dir / "checkpoint.pt"
    next_manifest = learner_dir / "manifest.json"
    metrics = json.loads((learner_dir / "metrics.json").read_text(encoding="utf-8"))
    input_checkpoint_sha = result["input_checkpoint_sha256"]
    input_manifest_sha = result["input_manifest_sha256"]
    if (file_sha256(checkpoint) != input_checkpoint_sha or
            file_sha256(manifest) != input_manifest_sha):
        raise ValueError("round input checkpoint or manifest changed after training began")
    for bundle in result.get("rollout_bundles", []):
        if file_sha256(Path(bundle["path"])) != bundle["sha256"]:
            raise ValueError("rollout bundle receipt changed during training")
    checkpoint_sha = file_sha256(next_checkpoint)
    manifest_sha = file_sha256(next_manifest)
    if (not isinstance(metrics, dict) or metrics.get("schema_version") != 1 or
            metrics.get("source") != "bees-native-ma-poca-v1" or
            metrics.get("input_checkpoint_sha256") != input_checkpoint_sha or
            metrics.get("input_manifest_sha256") != input_manifest_sha or
            metrics.get("next_checkpoint_sha256") != checkpoint_sha or
            metrics.get("next_manifest_sha256") != manifest_sha or
            metrics.get("learner_slot") != args.learner_slot or
            type(metrics.get("optimizer_input_steps")) is not int or
            metrics["optimizer_input_steps"] < 1 or
            type(metrics.get("episodes")) is not int or metrics["episodes"] < 1):
        raise ValueError("native learner completion receipt disagrees with its model artifacts")
    publish_receipt(output, "round-result.json", {
        "schema_version": 1,
        "kind": "native_training_round_complete",
        **result,
        "learner_metrics": metrics,
        "next_checkpoint": str(next_checkpoint),
        "next_checkpoint_sha256": checkpoint_sha,
        "next_manifest": str(next_manifest),
        "next_manifest_sha256": manifest_sha,
    })


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("run_config", type=Path)
    parser.add_argument("checkpoint", type=Path)
    parser.add_argument("trainer_config", type=Path)
    parser.add_argument("output_dir", type=Path)
    parser.add_argument("--model-manifest", type=Path,
                        help="override the run configuration's manifest for the next round")
    parser.add_argument("--learner-slot", type=int)
    parser.add_argument("--device", default="cpu")
    parser.add_argument("--torch-threads", type=int,
                        help="fixed optimizer intra-op threads, not rollout parallelism")
    parser.add_argument("--rollout-workers", type=int,
                        help="fixed number of parallel native rollout processes")
    parser.add_argument("--rollout-worlds", type=int,
                        help="independent native Worlds in this collector's reproducible window")
    parser.add_argument("--rollout-world-start", type=int,
                        help="zero-based first global World index for a distributed collection")
    parser.add_argument(
        "--seed-offset", type=int, default=0,
        help="unsigned offset to all authored episode seeds for a new reproducible round",
    )
    parser.add_argument("--rollout-binary", type=Path,
                        help="use a compiled policy_rollout binary instead of cargo run")
    parser.add_argument("--stage-diagnostics", action="store_true",
                        help="capture stage output in separate files for supervised execution")
    parser.add_argument("--collect-only", action="store_true",
                        help="publish a portable rollout bundle without running the optimizer")
    parser.add_argument("--bundle-inbox", type=Path,
                        help="also deliver the collected bundle to an existing shared inbox")
    parser.add_argument("--pinned-rollout-bundle", nargs=2, action="append", default=[],
                        metavar=("RECEIPT", "SHA256"), help="supply an approved receipt to an empty bundle template")
    args = parser.parse_args()
    if args.torch_threads is not None and not 1 <= args.torch_threads <= 256:
        parser.error("--torch-threads must be between 1 and 256")
    if args.rollout_workers is not None and not 1 <= args.rollout_workers <= 256:
        parser.error("--rollout-workers must be between 1 and 256")
    if not 0 <= args.seed_offset < (1 << 64):
        parser.error("--seed-offset must fit uint64")
    inbox = args.bundle_inbox.resolve(strict=True) if args.bundle_inbox else None
    if inbox is not None and (not args.collect_only or not inbox.is_dir()):
        parser.error("--bundle-inbox requires --collect-only and an existing directory")

    run_path = args.run_config.resolve(strict=True)
    checkpoint = args.checkpoint.resolve(strict=True)
    trainer_config = args.trainer_config.resolve(strict=True)
    run_bytes = run_path.read_bytes()
    run_document = json.loads(run_bytes)
    if not isinstance(run_document, dict):
        parser.error("run configuration must be an object")
    if args.pinned_rollout_bundle and run_document.get("rollout_bundles") != []:
        parser.error("--pinned-rollout-bundle requires an empty rollout_bundles template")
    manifest_value = args.model_manifest or run_document.get("model_manifest")
    if not manifest_value:
        parser.error("run configuration needs model_manifest or --model-manifest")
    manifest = Path(manifest_value)
    if not manifest.is_absolute():
        manifest = run_path.parent / manifest
    manifest = manifest.resolve(strict=True)
    input_checkpoint_sha = check_learner(manifest, checkpoint, args.learner_slot)
    input_manifest_sha = file_sha256(manifest)
    output = args.output_dir.resolve()
    result = {
        "seed_offset": args.seed_offset,
        "input_checkpoint_sha256": input_checkpoint_sha,
        "input_manifest_sha256": input_manifest_sha,
    }
    if "rollout_bundles" in run_document:
        if (args.rollout_workers is not None or args.rollout_worlds is not None or
                args.rollout_world_start is not None or
                "rollout_world_start" in run_document or "rollout_worlds" in run_document):
            parser.error("imported rollout bundles cannot allocate local Worlds or workers")
        if (args.collect_only or args.seed_offset or run_document.get("levels") or
                any(run_document.get(key) for key in (
                    "hivemind", "model_updates", "recover_notifications", "recover_outcome_journals",
                ))):
            parser.error("bundle rounds use completed captures without live Levels or seed shifts")
        bundles = run_document["rollout_bundles"] or [
            {"path": path, "sha256": digest} for path, digest in args.pinned_rollout_bundle
        ]
        if not isinstance(bundles, list) or not bundles:
            parser.error("rollout_bundles must list at least one pinned receipt")
        run_id = run_document.get("run_id")
        python = run_document.get("python")
        if not isinstance(run_id, str) or not run_id.strip() or not isinstance(python, str) or not python:
            parser.error("bundle rounds require a run ID and Python interpreter")
        pinned = []
        rollout_args = []
        for bundle in bundles:
            path = resolved(run_path.parent, bundle["path"])
            digest = file_sha256(Path(path))
            if digest != bundle["sha256"]:
                raise ValueError("rollout bundle receipt differs from the approved digest")
            pinned.append({"path": path, "sha256": digest})
            rollout_args.extend(["--pinned-rollout-bundle", path, digest])
        output.mkdir(parents=True, exist_ok=False)
        result.update(
            run_id=f"{run_id}-{uuid.uuid4().hex}",
            run_config=str(run_path),
            run_config_sha256=hashlib.sha256(run_bytes).hexdigest(),
            rollout_bundles=pinned,
        )
        optimize(args, output, python, manifest, checkpoint, trainer_config, result, rollout_args)
        return
    if args.rollout_binary is None and shutil.which("cargo") is None:
        parser.error("cargo was not found; provide --rollout-binary")
    if args.rollout_binary is not None:
        binary = args.rollout_binary.resolve(strict=True)
        command = [str(binary)]
    else:
        project = Path(__file__).resolve().parents[1] / "Cargo.toml"
        command = [
            "cargo", "run", "--release", "--manifest-path", str(project),
            "-p", "bees-host", "--example", "policy_rollout", "--",
        ]

    config = source_config(run_path, manifest, output)
    shifted_seeds(config, args.seed_offset)
    authored_level_count = len(config["levels"])
    template_worlds = config.pop("rollout_worlds", None)
    template_start = config.pop("rollout_world_start", 0)
    requested_worlds = args.rollout_worlds if args.rollout_worlds is not None else template_worlds
    world_start = args.rollout_world_start if args.rollout_world_start is not None else template_start
    if requested_worlds is not None or world_start != 0:
        expand_rollout_worlds(
            config, requested_worlds if requested_worlds is not None else authored_level_count,
            world_start,
        )
    template_workers = config.pop("rollout_workers", 1)
    workers = args.rollout_workers if args.rollout_workers is not None else template_workers
    levels = config["levels"]
    if type(world_start) is not int or world_start < 0:
        parser.error("rollout_world_start must be a nonnegative integer")
    if type(workers) is not int or not 1 <= workers <= min(len(levels), 256):
        parser.error("rollout_workers must be between one and the number of Levels (maximum 256)")
    if workers > 1 or args.collect_only:
        if args.rollout_binary is None or config.get("hivemind") is not None or any(
            config.get(key) for key in ("recover_notifications", "recover_outcome_journals")
        ):
            parser.error("parallel or collection-only rollouts require a compiled binary and model-only Levels")
        for field in ("world_id", "level_id"):
            identities = [level.get(field) for level in levels]
            if (any(type(identity) is not int or identity <= 0 for identity in identities)
                    or len(set(identities)) != len(levels)):
                parser.error(f"parallel rollouts require distinct positive {field} values")
    # Rotate worker assignment separately for each authored Level, but
    # advance one shared worker cursor across sources. This guarantees
    # that both total world counts and each source's replica counts differ
    # by at most one across processes. Preserve the original Level order
    # within every shard for deterministic source-order processing.
    assignments = [None] * len(levels)
    next_worker = 0
    for source in range(authored_level_count):
        first = (source - world_start) % authored_level_count
        for level_index in range(first, len(levels), authored_level_count):
            assignments[level_index] = next_worker % workers
            next_worker += 1
    buckets = [[] for _ in range(workers)]
    for index, level in enumerate(levels):
        buckets[assignments[index]].append(level)

    output.mkdir(parents=True, exist_ok=False)
    shards = []
    for index, allocated_levels in enumerate(buckets):
        directory = output if workers == 1 else output / f"rollout-{index}"
        shard = {**config, "levels": allocated_levels}
        if workers > 1:
            directory.mkdir()
            shard["connection_checkpoint"] = str(directory / "connection-checkpoint.json")
        shards.append((shard, prepare_rollout(shard, directory)))
    first, config_path = shards[0]

    print(json.dumps({"kind": "native_training_round_started", "run_config": str(config_path),
                      "rollout_worlds": len(levels), "rollout_world_start": world_start,
                      "rollout_processes": workers,
                      "seed_offset": args.seed_offset,
                      "input_manifest_sha256": input_manifest_sha,
                      "input_checkpoint_sha256": input_checkpoint_sha}), flush=True)
    rollout = run_parallel_rollouts(
        shards, command, input_manifest_sha, args.stage_diagnostics,
    )
    if args.collect_only:
        captures = []
        for shard, _ in shards:
            files = {}
            for key in ("policy_capture", "notifications"):
                artifact = Path(shard[key])
                files[key] = {
                    "path": artifact.relative_to(output).as_posix(),
                    "sha256": file_sha256(artifact),
                }
            captures.append(files)
        if (file_sha256(checkpoint) != input_checkpoint_sha or
                file_sha256(manifest) != input_manifest_sha):
            raise ValueError("rollout input checkpoint or manifest changed during collection")
        receipt = {
            "schema_version": 1,
            "kind": "native_rollout_bundle",
            "input_checkpoint_sha256": input_checkpoint_sha,
            "input_manifest_sha256": input_manifest_sha,
            "learner_slot": args.learner_slot,
            "rollout_worlds": len(levels),
            "rollout_processes": workers,
            "partition": {
                "source_run_id": config["run_id"],
                "world_start": world_start,
                "world_count": len(levels),
            },
            "captures": captures,
            "rollout_metrics": rollout,
        }
        if inbox is not None:
            destination = deliver_bundle(output, inbox, receipt)
            print(json.dumps({"kind": "native_rollout_delivered", "directory": str(destination)}), flush=True)
        publish_receipt(output, "rollout-bundle.json", receipt)
        return
    rollout_args = []
    for shard, _ in shards[1:]:
        rollout_args += ["--rollout", shard["policy_capture"], shard["notifications"], str(manifest)]
    result.update(
        run_id=first["run_id"],
        rollout_worlds=len(levels),
        rollout_world_start=world_start,
        rollout_processes=workers,
        run_config=str(config_path),
        run_config_sha256=file_sha256(config_path),
        capture=first["policy_capture"],
        journal=first["notifications"],
        rollout_metrics=rollout,
    )
    if workers > 1:
        result["rollout_shards"] = [
            {"run_config": str(path), "run_config_sha256": file_sha256(path),
             "capture": shard["policy_capture"], "journal": shard["notifications"]}
            for shard, path in shards
        ]
    optimize(args, output, config["python"], manifest, checkpoint, trainer_config, result, rollout_args)


if __name__ == "__main__":
    main()
