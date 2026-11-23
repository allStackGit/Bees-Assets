#!/usr/bin/env python3
"""One Hub-owned Bees Unity PPO job, isolated from the legacy Bees coordinator.

The enrolled machine's trusted adapter freezes every executable, game artifact,
training config and output path. Nothing in a Hub request supplies a command.
"""
from __future__ import annotations

import argparse
from datetime import datetime, timezone
import hashlib
import json
import math
import os
import shutil
from pathlib import Path
import signal
import subprocess
import sys
import time
import uuid

import yaml
from tensorboard.backend.event_processing.event_file_loader import EventFileLoader
from bees_unity_port_reservation import reserve_base_port
from bees_unity_worker_runtime import StagedUnityPlayer, clone_or_copy

MAX_EVENT_FILES = 64
MAX_METRIC_BYTES = 32768
MAX_EVENT_ROWS_PER_POLL = 4096
TAGS = {
    "Environment/Cumulative Reward": ("mean_reward", "scalar"),
    "Environment/Episode Length": ("mean_episode_steps", "scalar"),
    "Losses/Policy Loss": ("policy_loss", "scalar"),
    "Losses/Value Loss": ("value_loss", "scalar"),
    "Policy/Entropy": ("policy_entropy", "scalar"),
    "Self-Play/ELO": ("self_play_elo", "scalar"),
    "Self-Play/Win Rate": ("self_play_win_rate", "ratio"),
}

def sha256_file(filename: Path) -> str:
    digest = hashlib.sha256()
    with filename.open("rb") as reader:
        for chunk in iter(lambda: reader.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()

def require_file(filename: Path, expected: str, executable: bool = False) -> Path:
    if not filename.is_absolute() or filename.is_symlink() or not filename.is_file():
        raise ValueError("Missing or redirected trusted training input: " + str(filename))
    if sha256_file(filename) != expected:
        raise ValueError("Trusted Bees training input changed: " + str(filename))
    if executable and not os.access(filename, os.X_OK):
        raise ValueError("Unity worker entrypoint is not executable: " + str(filename))
    return filename

def verify_pinned_runtime(job: dict) -> None:
    source_root=Path(job["training_script"]).parent
    if not source_root.is_absolute() or source_root.is_symlink():
        raise ValueError("Bees runtime installation must be an absolute real directory.")
    version_file=source_root/"bees-runtime-version.txt"
    manifest_file=source_root/"bees-runtime-manifest.json"
    if version_file.is_symlink() or manifest_file.is_symlink() or not (
        version_file.is_file() and manifest_file.is_file()
    ):
        raise ValueError("Immutable Bees Python runtime metadata is missing or redirected.")
    version=job["runtime_version"]
    if not isinstance(version,str) or len(version)!=64 or any(
        digit not in "0123456789abcdef" for digit in version
    ):
        raise ValueError("Invalid pinned Bees Python runtime version.")
    if version_file.read_text(encoding="ascii").strip()!=version:
        raise ValueError("Pinned Bees Python runtime version marker changed.")
    manifest=json.loads(manifest_file.read_text(encoding="utf-8"))
    if manifest.get("schema_version")!=1 or manifest.get("runtime_version")!=version:
        raise ValueError("Pinned Bees Python runtime manifest differs.")
    entries=manifest.get("files")
    if not isinstance(entries,list) or not 1<=len(entries)<=2000:
        raise ValueError("Pinned Bees runtime file inventory is invalid.")
    canonical=json.dumps(
        {"files":entries},sort_keys=True,separators=(",",":"),ensure_ascii=True
    ).encode("utf-8")
    if hashlib.sha256(canonical).hexdigest()!=version:
        raise ValueError("Pinned Bees runtime file inventory hash differs.")
    names=set()
    for entry in entries:
        if not isinstance(entry,dict) or set(entry)!={"path","size","sha256"}:
            raise ValueError("Pinned runtime file record has unexpected fields.")
        name=entry["path"]
        if not isinstance(name,str) or name!=Path(name).name or (
            name in names or not name or "/" in name or chr(92) in name
        ):
            raise ValueError("Unsafe or duplicated Bees Python runtime member.")
        names.add(name)
        file=source_root/name
        expected=entry["sha256"]
        size=entry["size"]
        if type(size) is not int or size<0 or size>128*1024*1024 or (
            not isinstance(expected,str) or len(expected)!=64 or
            any(digit not in "0123456789abcdef" for digit in expected)
        ):
            raise ValueError("Invalid Bees runtime source manifest metadata.")
        if file.is_symlink() or not file.is_file() or (
            file.stat().st_size!=size or sha256_file(file)!=expected
        ):
            raise ValueError("Pinned Bees runtime file differs from captured bytes: "+name)
    allowed=names|{"bees-runtime-version.txt","bees-runtime-manifest.json"}
    installed={item.name for item in source_root.iterdir() if item.is_file()}
    if installed!=allowed:
        raise ValueError("Pinned Bees runtime directory contains unapproved file additions or omissions.")
    for key in ("training_script","trainer_source","evaluator_script",
                "evaluator_core","opponent_script"):
        candidate=Path(job[key])
        if candidate.parent!=source_root or candidate.name not in names:
            raise ValueError("Training or evaluation source escaped the pinned runtime.")

def inspect_job(filename: Path) -> dict:
    if not filename.is_absolute() or filename.is_symlink() or filename.stat().st_size > 65536:
        raise ValueError("Job definition must be a small regular private file.")
    job = json.loads(filename.read_text(encoding="utf-8"))
    keys = {
        "schema_version","job_id","game_binary","game_sha256",
        "training_script","training_sha256","trainer_source",
        "trainer_sha256","output_root","num_envs","max_steps",
        "device","seed","hyperparameters","expected_gpu_pci","environment_args",
        "runtime_version",
        "evaluation_profile","evaluation_matches","batch_inference","evaluator_script","evaluator_sha256",
        "evaluator_core","evaluator_core_sha256","opponent_script","opponent_script_sha256",
    }
    ancestry_keys = {
        "lineage_root_id", "continuation_of_job_id",
        "expected_parent_model_sha256", "expected_parent_checkpoint_sha256",
    }
    wan_keys = {"wan_actors", "wan_broker_port", "wan_key_file",
                "wan_key_fingerprint", "game_payload_sha256",
                "wan_release_sha256"}
    optional_keys = {"checkpoint_interval", "evaluation_schedule"}
    actual_keys = set(job) - optional_keys if isinstance(job, dict) else set()
    if not isinstance(job, dict) or (
        job.get("schema_version") == 1 and actual_keys != keys
    ) or (
        job.get("schema_version") == 2 and actual_keys != keys | ancestry_keys
    ) or (
        job.get("schema_version") == 3 and
        actual_keys != keys | ancestry_keys | wan_keys
    ) or job.get("schema_version") not in (1, 2, 3):
        raise ValueError("Unexpected Bees native job schema.")
    if not isinstance(job["job_id"], str):
        raise ValueError("Invalid Hub job identity.")
    uuid.UUID(job["job_id"])
    if job["schema_version"] == 1:
        job.update({
            "lineage_root_id": job["job_id"],
            "continuation_of_job_id": None,
            "expected_parent_model_sha256": None,
            "expected_parent_checkpoint_sha256": None,
        })
    lineage, parent = job["lineage_root_id"], job["continuation_of_job_id"]
    if not isinstance(lineage, str):
        raise ValueError("Invalid Bees Hub lineage identity.")
    uuid.UUID(lineage)
    if parent is not None:
        if not isinstance(parent,str):
            raise ValueError("Invalid Bees checkpoint predecessor.")
        uuid.UUID(parent)
        if parent == job["job_id"] or lineage == job["job_id"] or (
            any(not isinstance(job[field],str) or len(job[field]) != 64 or
                any(c not in "0123456789abcdef" for c in job[field])
                for field in ("expected_parent_model_sha256","expected_parent_checkpoint_sha256"))
        ):
            raise ValueError("Invalid Bees continuation parent or pinned hashes.")
    elif lineage != job["job_id"] or (
        job["expected_parent_model_sha256"] is not None or
        job["expected_parent_checkpoint_sha256"] is not None
    ):
        raise ValueError("Independent Bees jobs must start their own checkpoint lineage.")
    if filename.parent != Path(job["output_root"]):
        raise ValueError("Job directory does not match its frozen output root.")
    if not 1 <= job["num_envs"] <= 64 or not 1 <= job["max_steps"] <= 100000000000 or (
        not isinstance(job["seed"], int) or job["seed"] < 0 or job["seed"] > 2147483647
    ):
        raise ValueError("Invalid Bees training budget.")
    if job["device"] not in ("cpu","cuda"):
        raise ValueError("Unknown Bees learner device.")
    if job["schema_version"] == 3:
        from bees_unity_worker_identity import unity_worker_tree_sha256
        if not isinstance(job["game_payload_sha256"], str) or (
            len(job["game_payload_sha256"]) != 64 or
            any(ch not in "0123456789abcdef" for ch in job["game_payload_sha256"])
        ) or unity_worker_tree_sha256(Path(job["game_binary"])) != job["game_payload_sha256"]:
            raise ValueError("Complete Bees Unity player content differs from its frozen WAN job.")
        if not isinstance(job["wan_release_sha256"], str) or (
            len(job["wan_release_sha256"]) != 64 or
            any(c not in "0123456789abcdef" for c in job["wan_release_sha256"])
        ):
            raise ValueError("Bees WAN build-release identity is not pinned.")
        if type(job["wan_actors"]) is not int or not 1 <= job["wan_actors"] <= 8 or (
            type(job["wan_broker_port"]) is not int or
            not 1024 <= job["wan_broker_port"] <= 65535
        ):
            raise ValueError("Bees WAN learner requires 1–8 actors and a private broker port.")
        key_file = Path(job["wan_key_file"])
        if not key_file.is_absolute() or key_file.is_symlink() or not key_file.is_file() or (
            key_file.stat().st_mode & 0o077 or
            not 64 <= key_file.stat().st_size <= 128
        ):
            raise ValueError("Bees WAN learner requires an installed private token file.")
        import re
        key = key_file.read_text(encoding="ascii").strip()
        if not re.fullmatch(r"[a-f0-9]{64}", key) or (
            hashlib.sha256(key.encode("ascii")).hexdigest() !=
            job["wan_key_fingerprint"]
        ):
            raise ValueError("Bees WAN broker key differs from enrollment.")

    args = job["environment_args"]
    if not isinstance(args,list) or len(args) > 32 or any(
        not isinstance(arg,str) or len(arg)>256 or not arg.startswith("--rl-")
        or any(char in arg for char in "\r\n\x00") for arg in args
    ):
        raise ValueError("Invalid frozen Unity environment arguments.")
    for source, digest, executable in (
        ("game_binary","game_sha256",True),
        ("training_script","training_sha256",False),
        ("trainer_source","trainer_sha256",False),
        ("evaluator_script","evaluator_sha256",False),
        ("evaluator_core","evaluator_core_sha256",False),
        ("opponent_script","opponent_script_sha256",False),
    ):
        value = job[digest]
        if not isinstance(value,str) or len(value)!=64 or any(c not in "0123456789abcdef" for c in value):
            raise ValueError("Invalid frozen input hash.")
        require_file(Path(job[source]),value,executable)
    if type(job["batch_inference"]) is not bool:
        raise ValueError("Cross-worker inference batching must be a boolean.")
    if job["evaluation_profile"] not in ("off","scripted-maps-v1","scripted-coverage-v2") or (
        type(job["evaluation_matches"]) is not int or
        not 1 <= job["evaluation_matches"] <= 100
    ):
        raise ValueError("Unsupported frozen post-training evaluation profile.")
    interval = job.get("checkpoint_interval")
    if interval is not None and (
        type(interval) is not int or not 1000 <= interval <= 100000000000
    ):
        raise ValueError("Invalid Bees checkpoint interval.")
    if job.get("evaluation_schedule") is not None:
        from bees_hub_checkpoint_evaluation import validate_schedule
        validate_schedule(job["evaluation_schedule"], job["evaluation_profile"],
                          job["evaluation_matches"])
    verify_pinned_runtime(job)
    pci = job["expected_gpu_pci"]
    if job["device"] == "cuda":
        if not isinstance(pci,str):
            raise ValueError("ROCm GPU job requires the leased PCI identity.")
        # Same exact PCI format as the Hub's kernel device filter.
        import re
        if not re.fullmatch(r"[0-9a-f]{4}:[0-9a-f]{2}:[0-9a-f]{2}\.[0-7]",pci):
            raise ValueError("Invalid GPU PCI identity.")
    elif pci is not None:
        raise ValueError("CPU-only job cannot request a GPU.")
    return job

def stage_parent_checkpoint(root: Path, job: dict) -> bool:
    """Resume by copying an immutable parent ML-Agents state into this job.

    Each Hub job gets a distinct private artifact owner; the ML-Agents run ID
    remains the initial lineage ID so its checkpoint is a real optimizer
    continuation rather than a weights-only initialization.
    """
    parent_id = job["continuation_of_job_id"]
    if parent_id is None:
        return False
    lineage = job["lineage_root_id"]
    parent = root.parent / parent_id
    parent_job_file = parent / "job.json"
    receipt_file = parent / "model.json"
    if parent.is_symlink() or not parent.is_dir() or (
        parent_job_file.is_symlink() or not parent_job_file.is_file()
        or receipt_file.is_symlink() or not receipt_file.is_file()
        or parent_job_file.stat().st_size > 65536
        or receipt_file.stat().st_size > 4096
    ):
        raise ValueError("Parent Bees Hub job or checkpoint receipt is missing or redirected.")
    previous = json.loads(parent_job_file.read_text(encoding="utf-8"))
    receipt = json.loads(receipt_file.read_text(encoding="utf-8"))
    if not isinstance(previous, dict) or not isinstance(receipt, dict) or (
        previous.get("job_id") != parent_id
        or previous.get("lineage_root_id", parent_id) != lineage
        or receipt.get("job_id") != parent_id
        or receipt.get("lineage_root_id", parent_id) != lineage
        or receipt.get("sha256") != job["expected_parent_model_sha256"]
        or receipt.get("checkpoint_sha256") !=
            job["expected_parent_checkpoint_sha256"]
    ):
        raise ValueError("Parent Bees checkpoint identity or model receipt has changed.")
    for field in ("game_sha256", "runtime_version", "trainer_sha256", "seed"):
        if previous.get(field) != job[field]:
            raise ValueError("Parent Bees runtime or trainer differs at continuation: " + field)
    if (previous.get("schema_version") == 3 or job["schema_version"] == 3) and (
        previous.get("schema_version") != 3 or job["schema_version"] != 3 or
        previous.get("game_payload_sha256") != job.get("game_payload_sha256") or
        previous.get("wan_release_sha256") != job.get("wan_release_sha256") or
        previous.get("wan_key_fingerprint") != job.get("wan_key_fingerprint")
    ):
        raise ValueError("Bees WAN checkpoint continuation requires the same complete Unity player and trusted broker.")
    if previous.get("hyperparameters", {}) != job["hyperparameters"]:
        raise ValueError("Parent Bees PPO hyperparameters changed across exact checkpoint continuation.")
    source = parent / "results" / lineage
    target = root / "results" / lineage
    if source.is_symlink() or not source.is_dir() or (
        source.resolve() != source or target.exists() or target.is_symlink()
    ):
        raise ValueError("Bees continuation result tree is missing, redirected or already staged.")
    for field, digest in (
        ("model", "expected_parent_model_sha256"),
        ("checkpoint", "expected_parent_checkpoint_sha256"),
    ):
        name = receipt.get(field)
        if not isinstance(name, str):
            raise ValueError("Invalid parent Bees receipt path.")
        item = Path(name)
        if not item.is_absolute() or not item.is_relative_to(source):
            raise ValueError("Parent Bees receipt escapes its frozen results tree.")
        require_file(item, job[digest])
        if item.resolve() != item:
            raise ValueError("Parent Bees checkpoint has a redirected path component.")
    total, count = 0, 0
    for directory, dirs, files in os.walk(source, followlinks=False):
        for name in [*dirs, *files]:
            candidate = Path(directory) / name
            if candidate.is_symlink() or not (
                candidate.is_dir() if name in dirs else candidate.is_file()
            ):
                raise ValueError("Parent Bees checkpoint contains a symlink or special file.")
            if name in files:
                total += candidate.stat().st_size
                count += 1
                if count > 100000 or total > 128 * 1024 ** 3:
                    raise ValueError("Parent Bees training state exceeds bounded copy capacity.")
    temporary = root / ("resume-state-" + uuid.uuid4().hex)
    began = time.monotonic()
    try:
        # Reflink into independent copy-on-write inodes on supporting Linux
        # filesystems. Never hard-link an optimizer checkpoint: the resumed
        # trainer must not mutate its parent's frozen state.
        # Event logs are observations, not checkpoint state. Avoid copying
        # large old TensorBoard files or re-reporting the parent's metrics.
        shutil.copytree(
            source, temporary, copy_function=clone_or_copy,
            ignore=lambda directory, names: {
                name for name in names if name.startswith("events.out.tfevents.")
            },
        )
        for field, digest in (
            ("model", "expected_parent_model_sha256"),
            ("checkpoint", "expected_parent_checkpoint_sha256"),
        ):
            item = temporary / Path(receipt[field]).relative_to(source)
            require_file(item, job[digest])
        target.parent.mkdir(mode=0o700, parents=True, exist_ok=False)
        os.replace(temporary, target)
        print("[Bees native Hub] Verified checkpoint continuation staging in "+
              f"{time.monotonic()-began:.2f}s; "+
              f"{count} source files checked.", flush=True)
    finally:
        if temporary.exists():
            shutil.rmtree(temporary)
    return True


def freeze_trainer(job: dict, root: Path) -> Path:
    content = yaml.safe_load(Path(job["trainer_source"]).read_text(encoding="utf-8"))
    if not isinstance(content,dict) or set(content.get("behaviors",{}))!={"BeesRL1v1"}:
        raise ValueError("Unrecognized Bees training configuration.")
    behavior = content["behaviors"]["BeesRL1v1"]
    if behavior.get("trainer_type")!="poca":
        raise ValueError("Only the existing Bees MA-POCA learner is supported.")
    content["torch_settings"]["device"]=job["device"]
    behavior["max_steps"]=job["max_steps"]
    if "checkpoint_interval" in job:
        behavior["checkpoint_interval"]=job["checkpoint_interval"]
    frozen=job["hyperparameters"]
    if not isinstance(frozen,dict) or any(
        key not in ("batch_size","buffer_size","learning_rate","beta","epsilon","lambd","num_epoch")
        for key in frozen
    ):
        raise ValueError("Unrecognized Bees hyperparameter.")
    for key,value in frozen.items():
        if isinstance(value,bool) or not isinstance(value,(float,int)) or not math.isfinite(value):
            raise ValueError("Invalid Bees hyperparameter.")
        behavior["hyperparameters"][key]=value
    target=root/"trainer.yaml"
    with target.open("x",encoding="utf-8") as handle:
        yaml.safe_dump(content,handle,sort_keys=False)
    return target

def measured_metric(value, unit):
    if not math.isfinite(value) or abs(value)>1e12:
        return None
    if unit=="ratio" and not 0<=value<=1:
        return None
    if unit=="count" and (value<0 or not value.is_integer()):
        return None
    return {"value":int(value) if unit=="count" else float(value),"unit":unit}

def scalar_value(record):
    if record.HasField("simple_value"):
        return float(record.simple_value)
    if record.HasField("tensor"):
        from tensorboard.util.tensor_util import make_ndarray
        values=make_ndarray(record.tensor)
        if values.size==1:
            return float(values.reshape(-1)[0])
    return None

class EventProgress:
    def __init__(self, root: Path, job_id: str, lineage_root_id: str):
        self.root,self.job_id=root,job_id
        self.lineage_root_id=lineage_root_id
        self.loaders={}
        self.steps=0
        self.values={}
        self.first=time.monotonic()
        self.last_step=0
        self.last_at=self.first
        self.training_finished_at=None
        self.evaluation_started_at=None

    def poll(self):
        training=self.root/"results"/self.lineage_root_id
        files=sorted(training.rglob("events.out.tfevents.*"))[:MAX_EVENT_FILES] if training.is_dir() else []
        rows=0
        for filename in files:
            loader=self.loaders.get(filename)
            if loader is None:
                loader=EventFileLoader(str(filename))
                self.loaders[filename]=loader
            for event in loader.Load():
                rows+=1
                if rows>MAX_EVENT_ROWS_PER_POLL:
                    break
                step=int(event.step)
                if step<=0 or step<self.steps:
                    continue
                self.steps=max(step,self.steps)
                for value in event.summary.value:
                    mapped=TAGS.get(value.tag)
                    if not mapped:
                        continue
                    parsed=scalar_value(value)
                    if parsed is None:
                        continue
                    measurement=measured_metric(parsed,mapped[1])
                    if measurement is not None:
                        self.values[mapped[0]]=measurement
            if rows>MAX_EVENT_ROWS_PER_POLL:
                break
        if self.steps<=0:
            return
        current=time.monotonic()
        elapsed=max(0,(self.training_finished_at or current)-self.first)
        speed=0.0
        if self.steps>self.last_step and current>self.last_at:
            speed=(self.steps-self.last_step)/(current-self.last_at)
            self.last_step=self.steps
            self.last_at=current
        metrics=dict(self.values)
        status=self.root/"wan-status.json"
        if status.exists() or status.is_symlink():
            if status.is_symlink() or not status.is_file() or status.stat().st_size>2048:
                raise ValueError("Native WAN broker progress file is not a bounded regular file.")
            snapshot=json.loads(status.read_text(encoding="utf-8"))
            if not isinstance(snapshot,dict):
                raise ValueError("Native WAN telemetry must be a bounded mapping.")
            published=snapshot.get("reported_at_unix_ms")
            age=time.time()*1000-published if type(published) is int else float("inf")
            if snapshot.get("schema_version")==1 and (
                snapshot.get("job_id")==self.job_id and 0<=age<=90000
            ):
                for source,name,unit in (
                    ("active_actors","wan_active_actors","count"),
                    ("active_environments","wan_active_environments","count"),
                    ("remote_steps_per_second","wan_remote_steps_per_second","rate"),
                    ("trainer_steps_per_second","wan_trainer_steps_per_second","rate"),
                    ("trajectory_queue_batches","wan_trajectory_queue_batches","count"),
                    ("backpressure_last_minute","wan_backpressure_last_minute","count"),
                ):
                    value=snapshot.get(source)
                    if type(value) not in (int,float) or (
                        unit=="count" and (type(value) is not int or value<0)
                    ):
                        raise ValueError("Invalid native WAN broker metric: "+source)
                    measured=measured_metric(float(value),unit)
                    if measured is None or measured["value"]<0:
                        raise ValueError("Invalid WAN broker measurement: "+source)
                    metrics[name]=measured
        metrics["elapsed_training_seconds"]={"value":elapsed,"unit":"seconds"}
        metrics["training_steps"]={"value":self.steps,"unit":"count"}
        if self.evaluation_started_at is not None:
            metrics["evaluation_elapsed_seconds"]={
                "value":max(0,current-self.evaluation_started_at),"unit":"seconds",
            }
        if speed>0:
            metrics["environment_steps_per_second"]={"value":speed,"unit":"rate"}
        report={
            "schema_version":1,"source":"hub-metrics-v1",
            "experiment_id":self.job_id,"segment_id":"training",
            "source_modified_at":datetime.now(timezone.utc)
                .isoformat(timespec="milliseconds").replace("+00:00","Z"),
            "steps_completed":self.steps,"metrics":metrics,
        }
        raw=json.dumps(report,separators=(",",":"),allow_nan=False)
        if len(raw)>MAX_METRIC_BYTES:
            raise ValueError("Bees progress report exceeded the metric limit.")
        temp=self.root/("progress."+uuid.uuid4().hex+".tmp")
        try:
            temp.write_text(raw+"\n",encoding="utf-8")
            os.replace(temp,self.root/"progress.json")
        finally:
            temp.unlink(missing_ok=True)

def record_model(root: Path, job: dict):
    job_id = job["job_id"]
    run_root = root / "results" / job["lineage_root_id"]
    # The final canonical export is copied by ML-Agents from the saved
    # versioned checkpoint. A recent diagnostic ONNX may not match it.
    behavior_root = run_root / "BeesRL1v1"
    checkpoint = behavior_root / "checkpoint.pt"
    model = run_root / "BeesRL1v1.onnx"
    if model.is_symlink() or checkpoint.is_symlink() or (
            not model.is_file() or not checkpoint.is_file()
            or model.stat().st_size < 1 or checkpoint.stat().st_size < 1):
        raise RuntimeError("Bees completed model or optimizer checkpoint is missing or redirected.")
    checkpoint_sha = sha256_file(checkpoint)
    model_sha = sha256_file(model)
    exports = []
    for candidate in behavior_root.glob("BeesRL1v1-*.pt"):
        suffix = candidate.stem.removeprefix("BeesRL1v1-")
        if suffix.isdecimal() and candidate.is_file() and not candidate.is_symlink():
            exports.append((int(suffix), candidate))
    if not exports:
        raise RuntimeError("No versioned Bees checkpoint accompanies the final model.")
    saved_step, latest = max(exports, key=lambda item: item[0])
    paired = latest.with_suffix(".onnx")
    if paired.is_symlink() or not paired.is_file() or sha256_file(paired) != model_sha:
        raise RuntimeError("Final Bees ONNX does not match the latest saved checkpoint.")
    if job["continuation_of_job_id"] is not None and (
        checkpoint_sha == job["expected_parent_checkpoint_sha256"]
    ):
        raise RuntimeError(
            "Resumed Bees optimizer checkpoint did not advance beyond its parent."
        )
    receipt = {
        "schema_version": 1 if job["schema_version"] == 1 else 2,
        "job_id": job_id,
        **({"lineage_root_id": job["lineage_root_id"],
            "continuation_of_job_id": job["continuation_of_job_id"]}
           if job["schema_version"] >= 2 else {}),
        "model": str(model), "sha256": model_sha,
        "checkpoint": str(checkpoint), "checkpoint_sha256": checkpoint_sha,
        "steps_completed": saved_step,
        "completed_at": datetime.now(timezone.utc).isoformat(),
    }
    with (root / "model.json").open("x", encoding="utf-8") as handle:
        json.dump(receipt, handle, sort_keys=True)
        handle.write("\n")



def publish_policy_candidate(root: Path, job: dict) -> Path:
    """Export a portable, immutable ONNX candidate without promoting it.

    A native Hub training run owns its checkpoint and its candidate artifact.
    Importing this package into a live Unity deployment remains a separate,
    explicitly approved compatibility and release operation.
    """
    receipt_path = root / "model.json"
    if receipt_path.is_symlink() or not receipt_path.is_file() or (
        receipt_path.stat().st_size > 4096
    ):
        raise ValueError("Cannot publish Bees candidate without its verified model receipt.")
    receipt = json.loads(receipt_path.read_text(encoding="utf-8"))
    if not isinstance(receipt, dict) or (
        receipt.get("job_id") != job["job_id"] or
        receipt.get("lineage_root_id", receipt.get("job_id")) != job["lineage_root_id"]
    ):
        raise ValueError("Bees candidate model receipt does not belong to this job.")
    digest = receipt.get("sha256")
    checkpoint_digest = receipt.get("checkpoint_sha256")
    if any(not isinstance(value, str) or len(value) != 64 or
           any(char not in "0123456789abcdef" for char in value)
           for value in (digest, checkpoint_digest)):
        raise ValueError("Bees model or optimizer receipt has an invalid digest.")
    source = Path(str(receipt.get("model", "")))
    results = root / "results" / job["lineage_root_id"]
    if not source.is_absolute() or not source.is_relative_to(results) or (
        source.resolve() != source
    ):
        raise ValueError("Bees candidate source escapes the immutable job results.")
    require_file(source, digest)

    evaluation = None
    evaluation_source = root / "evaluations" / "summary.json"
    requires_final = job["evaluation_profile"] != "off" and (
        job.get("evaluation_schedule") is None or job["evaluation_schedule"]["include_final"]
    )
    if requires_final:
        if evaluation_source.is_symlink() or not evaluation_source.is_file() or (
            evaluation_source.stat().st_size > 32768
        ):
            raise ValueError("Evaluated Bees candidate requires its completed evaluation receipt.")
        summary = json.loads(evaluation_source.read_text(encoding="utf-8"))
        if not isinstance(summary, dict) or (
            summary.get("schema_version") != 1 or
            summary.get("job_id") != job["job_id"] or
            summary.get("profile") != job["evaluation_profile"] or
            summary.get("model_sha256") != digest or
            summary.get("case_count") != len(evaluation_cases(job["evaluation_profile"]))
        ):
            raise ValueError("Bees candidate evaluation is not for this model and suite.")
        evaluation = {
            "file": "evaluation-summary.json",
            "sha256": sha256_file(evaluation_source),
            "profile": job["evaluation_profile"],
            "case_count": summary["case_count"],
        }
    elif evaluation_source.exists() or evaluation_source.is_symlink():
        raise ValueError("Unevaluated Bees candidate unexpectedly contains evaluation results.")

    destination = root / "policy-candidate"
    if destination.exists() or destination.is_symlink():
        raise ValueError("Bees candidate export already exists; refusing to replace it.")
    temporary = root / ("policy-candidate-" + uuid.uuid4().hex + ".tmp")
    temporary.mkdir(mode=0o700)
    try:
        candidate_model = temporary / "model.onnx"
        with source.open("rb") as reader, candidate_model.open("xb") as writer:
            os.chmod(candidate_model, 0o600)
            shutil.copyfileobj(reader, writer, 1 << 20)
            writer.flush()
            os.fsync(writer.fileno())
        if sha256_file(candidate_model) != digest:
            raise ValueError("Bees candidate model bytes changed while exporting.")
        if evaluation is not None:
            candidate_evaluation = temporary / evaluation["file"]
            with evaluation_source.open("rb") as reader, candidate_evaluation.open("xb") as writer:
                os.chmod(candidate_evaluation, 0o600)
                shutil.copyfileobj(reader, writer, 1 << 20)
                writer.flush()
                os.fsync(writer.fileno())
            if sha256_file(candidate_evaluation) != evaluation["sha256"]:
                raise ValueError("Bees candidate evaluation changed while exporting.")
        manifest = {
            "schema_version": 1,
            "kind": "bees-unity-onnx-candidate",
            "deployment_status": "not-promoted",
            "job_id": job["job_id"],
            "lineage_root_id": job["lineage_root_id"],
            "continuation_of_job_id": job["continuation_of_job_id"],
            "model_file": "model.onnx",
            "model_sha256": digest,
            "model_size_bytes": candidate_model.stat().st_size,
            "optimizer_checkpoint_sha256": checkpoint_digest,
            "unity_worker_sha256": job["game_sha256"],
            **({
                "unity_worker_payload_sha256": job["game_payload_sha256"],
                "wan_release_sha256": job["wan_release_sha256"],
            } if job["schema_version"] == 3 else {}),
            "trainer_sha256": job["trainer_sha256"],
            "runtime_version": job["runtime_version"],
            "seed": job["seed"],
            "hyperparameters": job["hyperparameters"],
            "environment_args": job["environment_args"],
            "evaluation": evaluation,
        }
        with (temporary / "manifest.json").open("x", encoding="utf-8") as writer:
            os.chmod(temporary / "manifest.json", 0o600)
            json.dump(manifest, writer, sort_keys=True, indent=2)
            writer.write("\n")
            writer.flush()
            os.fsync(writer.fileno())
        os.replace(temporary, destination)
    finally:
        if temporary.exists():
            shutil.rmtree(temporary)
    return destination


# Versioned, independently scored scenarios. The benchmark runner owns
# match/timeout accounting; these are observations, not training rewards or
# an authorization to promote a policy. This is deliberately not described
# as exhaustive map or ship coverage.
SCRIPTED_CASES = (
    ("small_1v1_pursuit", 1, 32, "pursuit"),
    ("medium_2v2_strafe", 2, 64, "strafe"),
    ("large_4v4_pursuit", 4, 96, "pursuit"),
    ("wide_6v6_strafe", 6, 128, "strafe"),
)

# Explicit cross-product of all supported sizes in this versioned evaluation
# suite. Numerical map sizes form a continuum; these four sizes are strata,
# not a claim to exhaust every possible map or randomized spawn seed.
COVERAGE_V2_COUNTS = (1, 2, 3, 4, 5, 6)
COVERAGE_V2_MAP_SIZES = (32, 64, 128, 256)
COVERAGE_V2_OPPONENTS = ("pursuit", "strafe")
COVERAGE_V2_CASES = tuple(
    (f"p{players}_map{size}_{opponent}", players, size, opponent)
    for players in COVERAGE_V2_COUNTS
    for size in COVERAGE_V2_MAP_SIZES
    for opponent in COVERAGE_V2_OPPONENTS
)

def evaluation_cases(profile: str) -> tuple:
    if profile == "scripted-maps-v1":
        return SCRIPTED_CASES
    if profile == "scripted-coverage-v2":
        return COVERAGE_V2_CASES
    raise ValueError("Unsupported Bees scripted evaluation profile.")


def benchmark_recipe(root: Path, job: dict, case: tuple, index: int) -> Path:
    label,players,map_size,_ = case
    recipe = {
        "schema_version":1,"id":"bees-native-"+label.replace("_","-"),
        "revision":1,"behavior_name":"BeesRL1v1",
        "matches":job["evaluation_matches"],
        "seed":(job["seed"]+131071+index*65537)%(2**31),
        "max_environment_steps_per_match":200000,
        "env_args":[
            "--rl-matchup-mode=sampled",
            f"--rl-ships-per-side={players}",
            f"--rl-map-size={map_size}",
            ("--rl-bee-ship-types=Wasp,Hornet" if players==2 and map_size>=64
             else "--rl-bee-ship-types=Wasp"),
            ("--rl-human-ship-types=Gunship,Frigate" if players==2 and map_size>=64
             else "--rl-human-ship-types=Gunship"),
            "--rl-episode-timeout=60",
            "--rl-health-ratio=0.5",
        ],
    }
    file = root / ("recipe-"+label+".json")
    with file.open("x",encoding="utf-8") as handle:
        json.dump(recipe,handle,sort_keys=True)
        handle.write("\n")
    return file

def collect_benchmark(evaluation_root: Path, report_file: Path, job: dict,
                      label: str, opponent: str, model_sha256: str) -> dict:
    if report_file.is_symlink() or not report_file.is_file() or (
        report_file.parent != evaluation_root or
        report_file.resolve() != report_file or report_file.stat().st_size > 4194304
    ):
        raise RuntimeError("Benchmark output path is missing or redirected.")
    report=json.loads(report_file.read_text(encoding="utf-8"))
    source=report.get("benchmark",{})
    summary=report.get("metrics",{})
    if(report.get("schema_version")!=1 or
       report.get("candidate_sha256")!=model_sha256 or
       report.get("environment_executable_sha256")!=job["game_sha256"] or
       report.get("opponent_path")!="scripted:"+opponent or
       source.get("id")!="bees-native-"+label.replace("_","-") or
       summary.get("matches")!=job["evaluation_matches"]):
        raise RuntimeError("Independent benchmark result does not match its frozen scenario.")
    results = {}
    for name in ("matches","wins","losses","draws","timeouts"):
        value=summary.get(name)
        if(type(value) is not int or value<0 or value>job["evaluation_matches"]):
            raise RuntimeError("Invalid benchmark match count: "+name)
        results[name]=value
    if(results["wins"]+results["losses"]+results["draws"]!=results["matches"] or
       results["timeouts"]>results["draws"]):
        raise RuntimeError("Benchmark outcome accounting is inconsistent.")
    return {
        "case":label,"opponent":opponent,
        "report_file":report_file.name,"report_sha256":sha256_file(report_file),
        **results,
    }

def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--job",required=True)
    args=parser.parse_args()
    root=Path(args.job).absolute().parent
    job=inspect_job(Path(args.job).absolute())
    if job["device"]=="cuda":
        from experiment_hub_tinybrains_rocm_verify import verify_runtime_device
        verify_runtime_device(job["expected_gpu_pci"])
    resume=stage_parent_checkpoint(root,job)
    trainer=freeze_trainer(job,root)
    port,port_lock=reserve_base_port(root,job["job_id"],job["num_envs"])
    native_wan = job["schema_version"] == 3
    training_script = (Path(job["training_script"]).with_name(
        "bees_elastic_wan_mlagents_learn.py"
    ) if native_wan else Path(job["training_script"]))
    if native_wan and not training_script.is_file():
        raise ValueError("Frozen Bees runtime lacks the elastic WAN learner.")
    command=[
        sys.executable,"-B",str(training_script),str(trainer),
        "--env="+job["game_binary"],
        "--run-id="+job["lineage_root_id"],
        "--results-dir="+str(root/"results"),
        "--num-envs="+str(job["num_envs"]),
        "--base-port="+str(port),
        "--seed="+str(job["seed"]),
        "--no-graphics","--resume" if resume else "--force",
        *(["--bees-batch-inference"] if job["batch_inference"] else []),
        *(["--bees-wan-actors",str(job["wan_actors"]),
            "--bees-wan-min-actors",str(job["wan_actors"]),
            "--bees-wan-broker-port",str(job["wan_broker_port"]),
            "--bees-wan-auth-token-file",job["wan_key_file"]] if native_wan else []),
        *(["--env-args",*job["environment_args"]] if job["environment_args"] else []),
    ]
    child_env = os.environ.copy()
    if native_wan:
        build, trainer_hash, runtime = (
            job["wan_release_sha256"], job["trainer_sha256"], job["runtime_version"]
        )
        child_env.update({
            "BEES_HUB_WAN_STATUS_PATH": str(root/"wan-status.json"),
            "BEES_HUB_WAN_JOB_ID": job["job_id"],
            "BEES_TRAINING_BUILD_ID": job["wan_release_sha256"],
            "BEES_TRAINING_RUN_ID": job["lineage_root_id"],
            "BEES_TRAINING_COMPATIBILITY_KEY": hashlib.sha256(
                ("bees-native-wan-v1:" + build + ":" + trainer_hash +
                 ":" + runtime).encode("ascii")
            ).hexdigest(),
            "BEES_TRAINING_ENVIRONMENT_ID": job["wan_release_sha256"],
        })
    # At most one checkpoint evaluation shares this job's CPU/RAM
    # reservation. It uses an independent player and communicator port.
    schedule = job.get("evaluation_schedule")
    checkpoint_root = root / "checkpoint-evaluations"
    if schedule is not None:
        from bees_hub_checkpoint_evaluation import ENV
        checkpoint_root.mkdir(mode=0o700)
        (checkpoint_root / "completed").mkdir(mode=0o700)
        (checkpoint_root / "failed").mkdir(mode=0o700)
        child_env[ENV] = json.dumps({
            "root": str(checkpoint_root), "schedule": schedule,
            "profile": job["evaluation_profile"], "matches": job["evaluation_matches"],
        })
    evaluation_child = None
    pending = checkpoint_root / "pending"
    def poll_evaluation(allow_start=True):
        nonlocal evaluation_child
        if schedule is None:
            return
        if evaluation_child is not None:
            result = evaluation_child.poll()
            if result is None:
                return
            evaluation_child = None
            identity = json.loads((pending / "snapshot.json").read_text(encoding="utf-8"))
            sha = identity["model_sha256"]
            if len(sha) != 64 or any(ch not in "0123456789abcdef" for ch in sha):
                raise RuntimeError("Invalid checkpoint evaluation output identity.")
            if result == 0 and (pending / "summary.json").is_file():
                destination = checkpoint_root / "completed" / sha
                if destination.exists():
                    shutil.rmtree(pending)
                else:
                    pending.rename(destination)
            else:
                pending.rename(checkpoint_root / "failed" / (sha + "-" + uuid.uuid4().hex))
                print("[Bees Hub evaluation] Checkpoint evaluation failed; PPO continues.",
                      file=sys.stderr, flush=True)
        if allow_start and pending.is_dir():
            environment = os.environ.copy()
            threads = str(schedule["cpu_threads"])
            environment.update({
                "BEES_HUB_EVAL_THREADS": threads, "OMP_NUM_THREADS": threads,
                "OPENBLAS_NUM_THREADS": threads, "MKL_NUM_THREADS": threads,
            })
            evaluation_child = subprocess.Popen([
                sys.executable, "-B",
                str(Path(job["training_script"]).with_name("bees_hub_checkpoint_evaluation.py")),
                "--job", str(root / "job.json"),
            ], cwd=str(Path(job["training_script"]).parent),
                env=environment, start_new_session=True)
    def close_evaluation():
        nonlocal evaluation_child
        poll_evaluation(False)
        if evaluation_child is not None:
            if evaluation_child.poll() is None:
                evaluation_child.send_signal(signal.SIGINT)
                try:
                    evaluation_child.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    os.killpg(evaluation_child.pid, signal.SIGKILL)
                    evaluation_child.wait(timeout=10)
            poll_evaluation(False)

    child=None
    stopped=False
    target_stop_sha=None
    ignore_stop_marker=False
    stop_file=root/"stop.request"

    def evaluation_stop_identity():
        if schedule is None or schedule["stop_min_trials"] is None:
            return None
        proof_file = root / "evaluation-stop.json"
        if not proof_file.exists():
            return None
        if (proof_file.is_symlink() or not proof_file.is_file() or
            proof_file.stat().st_size > 4096):
            raise ValueError("Invalid Bees evaluation Stop provenance.")
        proof = json.loads(proof_file.read_text(encoding="utf-8"))
        sha = proof.get("checkpoint_sha256")
        if (proof.get("schema_version") != 1 or not isinstance(sha, str) or
            len(sha) != 64 or any(ch not in "0123456789abcdef" for ch in sha)):
            raise ValueError("Invalid Bees evaluation Stop checkpoint identity.")
        file = checkpoint_root / "completed" / sha / "summary.json"
        if (file.is_symlink() or not file.is_file() or file.stat().st_size > 32768
            or file.resolve() != file):
            raise ValueError("Bees evaluation Stop proof is missing its local benchmark.")
        summary = json.loads(file.read_text(encoding="utf-8"))
        trials, wins = summary.get("total_matches"), summary.get("total_wins")
        if (summary.get("job_id") != job["job_id"] or
            summary.get("profile") != job["evaluation_profile"] or
            summary.get("model_sha256") != sha or
            type(trials) is not int or trials < schedule["stop_min_trials"] or
            type(wins) is not int or not 0 <= wins <= trials or
            wins / trials < schedule["stop_min_success_rate"]):
            raise ValueError("Bees evaluation Stop proof does not meet the frozen threshold.")
        return sha
    def stop(_signum,_frame):
        nonlocal stopped
        if stopped: return
        stopped=True
        if child and child.poll() is None:
            child.send_signal(signal.SIGINT)
    def observe_stop_marker():
        nonlocal target_stop_sha, ignore_stop_marker
        if ignore_stop_marker:
            return False
        if not stop_file.exists() and not stop_file.is_symlink():
            return False
        if stop_file.is_symlink() or not stop_file.is_file() or (
            stop_file.stat().st_size != 0
        ):
            raise RuntimeError("Bees cooperative Stop marker is not an empty regular file.")
        if not stopped:
            try:
                target_stop_sha = evaluation_stop_identity()
            except (OSError, ValueError, RuntimeError) as error:
                print("[Bees Hub evaluation] Stop proof unavailable: " + str(error),
                      file=sys.stderr, flush=True)
                target_stop_sha = None
            if target_stop_sha and progress.training_finished_at is not None:
                # PPO already finished while the authenticated threshold
                # Stop was in transit. Complete only its final evaluation.
                ignore_stop_marker = True
                return False
            stop(signal.SIGINT,None)
        return True
    signal.signal(signal.SIGINT,stop)
    signal.signal(signal.SIGTERM,stop)
    progress=EventProgress(root,job["job_id"],job["lineage_root_id"])
    player=None
    try:
        player=StagedUnityPlayer(Path(job["game_binary"]),root)
        live_binary=player.start(
            job["game_payload_sha256"] if native_wan else None
        )
        # A Stop can arrive while a large Unity player is being staged.
        # Never launch a new learner after its owner has withdrawn Start.
        if stopped or observe_stop_marker():
            return 130
        command[4]="--env="+str(live_binary)
        child=subprocess.Popen(command,cwd=str(Path(job["training_script"]).parent.parent),
                               env=child_env,start_new_session=True)
        # Also close the narrow signal race between the prelaunch check and
        # assignment of the newly spawned child process.
        if stopped and child.poll() is None:
            child.send_signal(signal.SIGINT)
        telemetry_error = False
        while child.poll() is None:
            if not stopped:
                observe_stop_marker()
            poll_evaluation(not stopped)
            try:
                progress.poll()
            except (OSError,ValueError,RuntimeError) as error:
                if not telemetry_error:
                    print("[Bees native Hub] Progress telemetry unavailable: "+str(error),
                          file=sys.stderr,flush=True)
                    telemetry_error = True
            else:
                telemetry_error = False
            time.sleep(5)
        try:
            progress.poll()
        except (OSError,ValueError,RuntimeError):
            pass
        progress.training_finished_at=time.monotonic()
        close_evaluation()
        result=child.returncode
        if stopped:
            # The existing interrupt-safe ML-Agents launcher may have written
            # a final optimizer checkpoint. Record it only when both actual
            # immutable model and optimizer files exist; a missing or unchanged
            # checkpoint cannot authorize a continuation.
            if result not in (0,130):
                print("[Bees native Hub] Learner failed during cooperative Stop (exit "+
                      str(result)+"); checkpoint cannot be certified.",
                      file=sys.stderr,flush=True)
                return 1
            try:
                record_model(root,job)
                print("[Bees native Hub] Graceful Stop retained a verified resumable checkpoint.",
                      flush=True)
            except (OSError,ValueError,RuntimeError) as error:
                print("[Bees native Hub] Stop has no verified resumable checkpoint: "+str(error),
                      file=sys.stderr,flush=True)
                return 1
            # Manual Stops remain prompt. A success-threshold Stop is a
            # completed training sequence and must separately evaluate the
            # exact final saved model (which may differ from its trigger).
            if not target_stop_sha:
                return 0
            ignore_stop_marker = True
            stopped = False
            print("[Bees Hub evaluation] Threshold met; verifying final saved checkpoint.",
                  flush=True)
        else:
            if result!=0:
                return result or 1
            record_model(root,job)
        if job["evaluation_profile"] in ("scripted-maps-v1","scripted-coverage-v2") and (
            schedule is None or schedule["include_final"]
        ):
            evaluation_root=root/"evaluations"
            model=json.loads((root/"model.json").read_text(encoding="utf-8"))
            cached = checkpoint_root / "completed" / model["sha256"]
            if cached.is_dir() and (cached / "summary.json").is_file():
                # Identical model bytes reuse the identical benchmark; never
                # introduce a contradictory second receipt for the same suite.
                shutil.copytree(cached, evaluation_root,
                                ignore=shutil.ignore_patterns("model.onnx", "snapshot.json"))
                print("[Bees Hub evaluation] Reusing exact final checkpoint evaluation.",
                      flush=True)
            else:
                evaluation_root.mkdir(mode=0o700)
                observed=[]
                cases=evaluation_cases(job["evaluation_profile"])
                progress.evaluation_started_at=time.monotonic()
                progress.values["evaluation_cases_total"]={
                    "value":len(cases),"unit":"count",
                }
                progress.values["evaluation_cases_completed"]={
                    "value":0,"unit":"count",
                }
                progress.values["evaluation_matches_completed"]={
                    "value":0,"unit":"count",
                }
                progress.poll()
                for index,case in enumerate(cases):
                    if not stopped:
                        observe_stop_marker()
                    if stopped: return 0
                    label,_,_,opponent=case
                    recipe=benchmark_recipe(evaluation_root,job,case,index)
                    for source,digest in (
                        ("evaluator_script","evaluator_sha256"),
                        ("evaluator_core","evaluator_core_sha256"),
                        ("opponent_script","opponent_script_sha256"),
                    ):
                        require_file(Path(job[source]),job[digest])
                    output=evaluation_root/("stdout-"+label+".txt")
                    command=[
                        sys.executable,"-B",job["evaluator_script"],
                        "--benchmark",str(recipe),
                        "--candidate",model["model"],
                        "--candidate-sha256",model["sha256"],
                        "--opponent","scripted:"+opponent,
                        "--env",str(live_binary),
                        "--build-id",job["game_sha256"],
                        "--run-id",job["job_id"],
                        "--output-root",str(evaluation_root),
                        "--base-port",str(port),
                    ]
                    with output.open("xb") as stream:
                        child=subprocess.Popen(command,
                            cwd=str(Path(job["training_script"]).parent),
                            stdout=stream,start_new_session=True,
                            env={**os.environ, "BEES_HUB_EVAL_THREADS":
                                 str(schedule["cpu_threads"] if schedule else 2)})
                        while child.poll() is None:
                            if not stopped:
                                observe_stop_marker()
                            time.sleep(0.25)
                    if stopped: return 0
                    if child.returncode!=0:
                        raise RuntimeError("Independent Bees evaluation failed for "+label)
                    if output.stat().st_size>16384:
                        raise RuntimeError("Benchmark output path exceeded the expected size.")
                    lines=output.read_text(encoding="utf-8").strip().splitlines()
                    if not lines:
                        raise RuntimeError("Benchmark evaluator returned no result.")
                    reported=Path(lines[-1])
                    if reported.parent!=evaluation_root or not reported.name.endswith(".json"):
                        raise RuntimeError("Benchmark evaluator returned an unexpected output path.")
                    observed.append(collect_benchmark(
                        evaluation_root, reported, job, label, opponent, model["sha256"]
                    ))
                    progress.values["evaluation_cases_completed"]={
                        "value":len(observed),"unit":"count",
                    }
                    progress.values["evaluation_matches_completed"]={
                        "value":sum(item["matches"] for item in observed),"unit":"count",
                    }
                    # A case is only counted after its frozen benchmark receipt
                    # and outcome accounting have been verified.
                    progress.poll()
                summary={
                    "schema_version":1,"job_id":job["job_id"],
                    "profile":job["evaluation_profile"],"model_sha256":model["sha256"],
                    "case_count":len(observed),"cases":observed,
                    "steps_completed":model["steps_completed"],
                    "total_matches":sum(item["matches"] for item in observed),
                    "total_wins":sum(item["wins"] for item in observed),
                    "total_draws":sum(item["draws"] for item in observed),
                    "total_losses":sum(item["losses"] for item in observed),
                    "total_timeouts":sum(item["timeouts"] for item in observed),
                }
                with (evaluation_root/"summary.json").open("x",encoding="utf-8") as stream:
                    json.dump(summary,stream,sort_keys=True,indent=2)
                    stream.write("\n")
        publish_policy_candidate(root,job)
        return 0
    finally:
        if child and child.poll() is None:
            # Unity's environment manager must finish before the disposable
            # player directory can be removed. Terminate/kill if an
            # unresponsive learner ignores its normal checkpoint interrupt.
            child.send_signal(signal.SIGINT)
            try:
                child.wait(timeout=60)
            except subprocess.TimeoutExpired:
                child.terminate()
                try:
                    child.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    child.kill()
                    child.wait(timeout=10)
        try:
            close_evaluation()
        finally:
            try:
                if player is not None:
                    player.close()
            finally:
                port_lock.close()

if __name__=="__main__":
    try:
        sys.exit(main())
    except (OSError,ValueError,RuntimeError) as error:
        print("[Bees native Hub] "+str(error),file=sys.stderr,flush=True)
        sys.exit(2)
