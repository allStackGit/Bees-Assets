"""Asynchronous, immutable Bees checkpoint evaluation under the owning Hub job.

The learner publishes completed ONNX exports, not an in-flight optimizer.
Only one snapshot can be pending or evaluating; busy evaluators never block
PPO or accumulate an unbounded work queue.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import sys
import time
import uuid

ENV = "BEES_HUB_CHECKPOINT_EVALUATION"
SHA = re.compile(r"^[a-f0-9]{64}$")


def validate_schedule(schedule, profile, matches):
    required = {
        "every_n_checkpoints", "min_seconds", "include_baseline", "include_final",
        "cpu_threads", "stop_min_trials", "stop_min_success_rate",
    }
    if (not isinstance(schedule, dict) or
        not required <= set(schedule) <= required | {"stop_on_regression"} or
        type(schedule.get("stop_on_regression", False)) is not bool or
        (schedule.get("stop_on_regression") and not schedule["include_final"])):

        raise ValueError("Invalid frozen Bees checkpoint evaluation schedule.")
    cases = {"scripted-maps-v1": 4, "scripted-coverage-v2": 48}.get(profile)
    if cases is None or (
        type(schedule["every_n_checkpoints"]) is not int
        or not 1 <= schedule["every_n_checkpoints"] <= 1000000
        or type(schedule["min_seconds"]) is not int
        or not 0 <= schedule["min_seconds"] <= 604800
        or type(schedule["cpu_threads"]) is not int
        or not 1 <= schedule["cpu_threads"] <= 64
        or any(type(schedule[k]) is not bool for k in ("include_baseline", "include_final"))
    ):
        raise ValueError("Invalid Bees checkpoint evaluation cadence.")
    trials, rate = schedule["stop_min_trials"], schedule["stop_min_success_rate"]
    if trials is None and rate is None:
        return
    if (type(trials) is not int or not 1 <= trials <= cases * matches
        or type(rate) not in (int, float) or not 0 < rate <= 1
        or not schedule["include_final"]):
        raise ValueError("Invalid Bees independent evaluation threshold.")


def digest(path):
    value = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1048576), b""):
            value.update(block)
    return value.hexdigest()


def install_checkpoint_publisher():
    raw = os.environ.get(ENV)
    if not raw:
        return None
    config = json.loads(raw)
    root = Path(config["root"])
    schedule = config["schedule"]
    validate_schedule(schedule, config["profile"], config["matches"])
    if not root.is_absolute() or root.resolve() != root or not root.is_dir():
        raise ValueError("Invalid private checkpoint evaluation root.")
    from mlagents.trainers.trainer.rl_trainer import RLTrainer
    original_checkpoint = RLTrainer._checkpoint
    original_maybe = RLTrainer._maybe_save_model
    last_step = None
    saves = 0
    last_published = -float("inf")
    baseline = schedule["include_baseline"]

    def publish(trainer, step, exported=None):
        nonlocal last_published
        pending = root / "pending"
        if pending.exists() or time.monotonic() - last_published < schedule["min_seconds"]:
            return
        temporary = root / (".snapshot-" + uuid.uuid4().hex)
        temporary.mkdir(mode=0o700)
        try:
            model = temporary / "model.onnx"
            if exported is None:
                # First trajectory boundary, before this run's first update.
                trainer.model_saver.export(str(temporary / "model"), trainer.brain_name)
            else:
                if exported.is_symlink() or not exported.is_file():
                    raise RuntimeError("Completed checkpoint lacks an ONNX export.")
                shutil.copyfile(exported, model)
            if not model.is_file() or model.is_symlink() or model.stat().st_size < 1:
                raise RuntimeError("Empty Bees checkpoint export.")
            sha = digest(model)
            if (root / "completed" / sha).exists():
                return
            (temporary / "snapshot.json").write_text(
                json.dumps({"schema_version": 1, "step": step, "model_sha256": sha},
                           sort_keys=True) + "\n", encoding="utf-8")
            temporary.rename(pending)
            last_published = time.monotonic()
            print(f"[Bees Hub evaluation] Published {sha[:12]} at step {step}.", flush=True)
        finally:
            if temporary.exists():
                shutil.rmtree(temporary)

    def maybe_publish(trainer, step, exported=None):
        try:
            publish(trainer, step, exported)
        except (OSError, RuntimeError, ValueError) as error:
            print("[Bees Hub evaluation] Snapshot unavailable: " + str(error),
                  file=sys.stderr, flush=True)

    def checkpoint(self):
        nonlocal last_step, saves
        result = original_checkpoint(self)
        step = int(result.steps)
        if step != last_step:
            last_step = step
            saves += 1
            if saves % schedule["every_n_checkpoints"] == 0:
                maybe_publish(self, step, Path(result.file_path))
        return result

    def maybe_save(self, step_after_process):
        nonlocal baseline
        if baseline:
            baseline = False
            maybe_publish(self, int(self.get_step))
        return original_maybe(self, step_after_process)

    RLTrainer._checkpoint = checkpoint
    RLTrainer._maybe_save_model = maybe_save

    def restore():
        RLTrainer._checkpoint = original_checkpoint
        RLTrainer._maybe_save_model = original_maybe

    return restore


def evaluate_pending(job_file):
    from experiment_hub_bees_native_train import (
        inspect_job, evaluation_cases, benchmark_recipe, collect_benchmark,
        benchmark_build_id,
    )
    from bees_experiment_benchmark import evaluate
    from bees_unity_worker_runtime import StagedUnityPlayer
    from bees_unity_port_reservation import reserve_base_port

    job = inspect_job(job_file)
    schedule = job.get("evaluation_schedule")
    if schedule is None:
        raise ValueError("Checkpoint evaluator is not authorized by this job.")
    root = job_file.parent
    pending = root / "checkpoint-evaluations" / "pending"
    identity, model = pending / "snapshot.json", pending / "model.onnx"
    if (pending.resolve() != pending or not pending.is_dir()
        or identity.is_symlink() or not identity.is_file() or identity.stat().st_size > 4096
        or model.is_symlink() or not model.is_file()):
        raise ValueError("Missing immutable checkpoint evaluation inputs.")
    snapshot = json.loads(identity.read_text(encoding="utf-8"))
    sha, step = snapshot.get("model_sha256"), snapshot.get("step")
    if (snapshot.get("schema_version") != 1 or not isinstance(sha, str)
        or SHA.fullmatch(sha) is None or digest(model) != sha
        or type(step) is not int or step < 0):
        raise ValueError("Invalid immutable checkpoint evaluation identity.")
    os.environ["BEES_HUB_EVAL_THREADS"] = str(schedule["cpu_threads"])
    port, lock = reserve_base_port(root, job["job_id"] + "-evaluation", 1)
    player = None
    try:
        player = StagedUnityPlayer(Path(job["game_binary"]), root)
        binary = player.start(job.get("game_payload_sha256"))
        observed = []
        started = time.monotonic()
        for index, case in enumerate(evaluation_cases(job["evaluation_profile"])):
            label, _, _, opponent = case
            recipe = benchmark_recipe(pending, job, case, index)
            report = evaluate(recipe, model, "scripted:" + opponent, binary,
                              benchmark_build_id(job), job["job_id"], pending,
                              candidate_sha256=sha, base_port=port)
            observed.append(collect_benchmark(pending, report, job, label, opponent, sha))
            print(f"[Bees Hub evaluation] Checkpoint {step}: {index+1} cases complete.",
                  flush=True)
        if digest(model) != sha:
            raise RuntimeError("Checkpoint bytes changed during evaluation.")
        summary = {
            "schema_version": 1, "job_id": job["job_id"],
            "profile": job["evaluation_profile"], "model_sha256": sha,
            "steps_completed": step, "case_count": len(observed), "cases": observed,
            **{"total_" + key: sum(item[key] for item in observed)
               for key in ("matches", "wins", "draws", "losses", "timeouts")},
        }
        temporary = pending / "summary.tmp"
        with temporary.open("x", encoding="utf-8") as stream:
            json.dump(summary, stream, sort_keys=True, indent=2)
            stream.write("\n")
        temporary.rename(pending / "summary.json")
        print(f"[Bees Hub evaluation] Evaluated step {step} in {time.monotonic()-started:.2f}s.",
              flush=True)
        return 0
    finally:
        try:
            if player is not None:
                player.close()
        finally:
            lock.close()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--job", required=True, type=Path)
    arguments = parser.parse_args()
    try:
        sys.exit(evaluate_pending(arguments.job.absolute()))
    except (OSError, RuntimeError, ValueError) as exc:
        print("[Bees Hub evaluation] " + str(exc), file=sys.stderr, flush=True)
        sys.exit(2)
