#!/usr/bin/env python3
"""One Hub-owned Bees Unity PPO job, isolated from the legacy Bees coordinator.

The enrolled machine's trusted adapter freezes every executable, game artifact,
training config and output path. Nothing in a Hub request supplies a command.
"""
from __future__ import annotations

import argparse
from datetime import datetime, timezone
import fcntl
import hashlib
import json
import math
import os
from pathlib import Path
import signal
import socket
import subprocess
import sys
import time
import uuid

import yaml
from tensorboard.backend.event_processing.event_file_loader import EventFileLoader

PORT_BASE = 22000
PORT_WIDTH = 64
PORT_LAST = 54000
MAX_EVENT_FILES = 64
MAX_METRIC_BYTES = 32768
MAX_EVENT_ROWS_PER_POLL = 4096
TAGS = {
    "Environment/Cumulative Reward": ("mean_reward", "scalar"),
    "Environment/Episode Length": ("mean_episode_steps", "count"),
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

def inspect_job(filename: Path) -> dict:
    if not filename.is_absolute() or filename.is_symlink() or filename.stat().st_size > 65536:
        raise ValueError("Job definition must be a small regular private file.")
    job = json.loads(filename.read_text(encoding="utf-8"))
    keys = {
        "schema_version","job_id","game_binary","game_sha256",
        "training_script","training_sha256","trainer_source",
        "trainer_sha256","output_root","num_envs","max_steps",
        "device","seed","hyperparameters","expected_gpu_pci","environment_args",
    }
    if not isinstance(job, dict) or set(job) != keys or job["schema_version"] != 1:
        raise ValueError("Unexpected Bees native job schema.")
    if not isinstance(job["job_id"], str):
        raise ValueError("Invalid Hub job identity.")
    uuid.UUID(job["job_id"])
    if filename.parent != Path(job["output_root"]):
        raise ValueError("Job directory does not match its frozen output root.")
    if not 1 <= job["num_envs"] <= 64 or not 1 <= job["max_steps"] <= 100000000000 or (
        not isinstance(job["seed"], int) or job["seed"] < 0 or job["seed"] > 2147483647
    ):
        raise ValueError("Invalid Bees training budget.")
    if job["device"] not in ("cpu","cuda"):
        raise ValueError("Unknown Bees learner device.")
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
    ):
        value = job[digest]
        if not isinstance(value,str) or len(value)!=64 or any(c not in "0123456789abcdef" for c in value):
            raise ValueError("Invalid frozen input hash.")
        require_file(Path(job[source]),value,executable)
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

def reserve_base_port(root: Path, job_id: str, num_envs: int):
    """Keep a process-held inter-job port reservation until training exits."""
    lockroot = root.parent / "_bees_native_ports"
    lockroot.mkdir(mode=0o700,exist_ok=True)
    capacity = (PORT_LAST - PORT_BASE) // PORT_WIDTH
    start = int(hashlib.sha256(job_id.encode("ascii")).hexdigest()[:8],16) % capacity
    for offset in range(capacity):
        slot = (start + offset) % capacity
        port = PORT_BASE + slot * PORT_WIDTH
        lock = (lockroot / f"{port}.lock").open("a+b")
        try:
            fcntl.flock(lock.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
            checks = []
            try:
                for number in range(num_envs + 2):
                    sock = socket.socket(socket.AF_INET,socket.SOCK_STREAM)
                    checks.append(sock)
                    sock.bind(("127.0.0.1",port + number))
            except OSError:
                for sock in checks: sock.close()
                lock.close()
                continue
            for sock in checks: sock.close()
            return port,lock
        except BlockingIOError:
            lock.close()
    raise RuntimeError("No isolated Unity communicator port block remains available.")

def freeze_trainer(job: dict, root: Path) -> Path:
    content = yaml.safe_load(Path(job["trainer_source"]).read_text(encoding="utf-8"))
    if not isinstance(content,dict) or set(content.get("behaviors",{}))!={"BeesRL1v1"}:
        raise ValueError("Unrecognized Bees training configuration.")
    behavior = content["behaviors"]["BeesRL1v1"]
    if behavior.get("trainer_type")!="poca":
        raise ValueError("Only the existing Bees MA-POCA learner is supported.")
    content["torch_settings"]["device"]=job["device"]
    behavior["max_steps"]=job["max_steps"]
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
    def __init__(self, root: Path, job_id: str):
        self.root,self.job_id=root,job_id
        self.loaders={}
        self.steps=0
        self.values={}
        self.first=time.monotonic()
        self.last_step=0
        self.last_at=self.first

    def poll(self):
        training=self.root/"results"/self.job_id
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
        elapsed=max(0,current-self.first)
        speed=0.0
        if self.steps>self.last_step and current>self.last_at:
            speed=(self.steps-self.last_step)/(current-self.last_at)
            self.last_step=self.steps
            self.last_at=current
        metrics=dict(self.values)
        metrics["elapsed_training_seconds"]={"value":elapsed,"unit":"seconds"}
        metrics["training_steps"]={"value":self.steps,"unit":"count"}
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

def record_model(root: Path, job_id: str):
    models=sorted((root/"results"/job_id).rglob("*.onnx"))
    if not models:
        raise RuntimeError("Bees learner exited without exporting a model.")
    model=max(models,key=lambda p:p.stat().st_mtime_ns)
    receipt={
        "schema_version":1,"job_id":job_id,
        "model":str(model),"sha256":sha256_file(model),
        "completed_at":datetime.now(timezone.utc).isoformat(),
    }
    with (root/"model.json").open("x",encoding="utf-8") as handle:
        json.dump(receipt,handle,sort_keys=True)
        handle.write("\n")

def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--job",required=True)
    args=parser.parse_args()
    root=Path(args.job).absolute().parent
    job=inspect_job(Path(args.job).absolute())
    if job["device"]=="cuda":
        from experiment_hub_tinybrains_rocm_verify import verify_runtime_device
        verify_runtime_device(job["expected_gpu_pci"])
    trainer=freeze_trainer(job,root)
    port,port_lock=reserve_base_port(root,job["job_id"],job["num_envs"])
    command=[
        sys.executable,"-B",job["training_script"],str(trainer),
        "--env="+job["game_binary"],
        "--run-id="+job["job_id"],
        "--results-dir="+str(root/"results"),
        "--num-envs="+str(job["num_envs"]),
        "--base-port="+str(port),
        "--seed="+str(job["seed"]),
        "--no-graphics","--force",
        *(["--env-args",*job["environment_args"]] if job["environment_args"] else []),
    ]
    child=None
    stopped=False
    def stop(_signum,_frame):
        nonlocal stopped
        if stopped: return
        stopped=True
        if child and child.poll() is None:
            child.send_signal(signal.SIGINT)
    signal.signal(signal.SIGINT,stop)
    signal.signal(signal.SIGTERM,stop)
    progress=EventProgress(root,job["job_id"])
    try:
        child=subprocess.Popen(command,cwd=str(Path(job["training_script"]).parent.parent),
                               start_new_session=True)
        while child.poll() is None:
            progress.poll()
            time.sleep(5)
        progress.poll()
        result=child.returncode
        if result==0:
            record_model(root,job["job_id"])
            return 0
        return 130 if stopped else result or 1
    finally:
        if child and child.poll() is None:
            child.send_signal(signal.SIGINT)
            try: child.wait(timeout=60)
            except subprocess.TimeoutExpired: child.terminate()
        port_lock.close()

if __name__=="__main__":
    try:
        sys.exit(main())
    except (OSError,ValueError,RuntimeError) as error:
        print("[Bees native Hub] "+str(error),file=sys.stderr,flush=True)
        sys.exit(2)
