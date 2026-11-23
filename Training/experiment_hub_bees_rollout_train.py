#!/usr/bin/env python3
"""Run one Hub-owned Bees Unity WAN simulation actor, without an optimizer.

The installed adapter freezes the same Unity worker/Python runtime as the
learner and the private loopback tunnel to that learner. Jobs cannot supply
an executable, network address, port, or authentication key.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import sys
import uuid

from experiment_hub_bees_native_train import (
    require_file, reserve_base_port, verify_pinned_runtime,
)

SHA = re.compile(r"^[0-9a-f]{64}$")
FIELDS = {
    "schema_version", "job_id", "learner_job_id", "lineage_root_id",
    "output_root", "game_binary", "game_sha256",
    "training_script", "training_sha256", "trainer_source",
    "trainer_sha256", "runtime_version", "evaluator_script",
    "evaluator_core", "opponent_script", "actor_script",
    "actor_sha256", "envs", "torch_threads", "device", "expected_gpu_pci",
    "auth_token_file", "auth_token_fingerprint", "broker_port",
}


def inspect_job(filename: Path) -> dict:
    if not filename.is_absolute() or filename.is_symlink() or (
        not filename.is_file() or filename.stat().st_size > 32768
    ):
        raise ValueError("Bees remote job definition must be a small immutable file.")
    job = json.loads(filename.read_text(encoding="utf-8"))
    if not isinstance(job, dict) or set(job) != FIELDS or job["schema_version"] != 1:
        raise ValueError("Unexpected Bees remote job schema.")
    for field in ("job_id", "learner_job_id", "lineage_root_id"):
        item = job[field]
        if not isinstance(item, str) or str(uuid.UUID(item)) != item:
            raise ValueError("Invalid Bees remote " + field)
    if job["job_id"] == job["learner_job_id"]:
        raise ValueError("A Bees remote actor cannot own the learner job.")
    if Path(job["output_root"]) != filename.parent or (
        not 1 <= job["envs"] <= 64 or
        not 1 <= job["torch_threads"] <= 64 or
        job["torch_threads"] > job["envs"]
    ):
        raise ValueError("Bees remote simulation budget or output root changed.")
    if job["device"] not in ("cpu", "cuda") or (
        type(job["broker_port"]) is not int or
        not 1024 <= job["broker_port"] <= 65535
    ):
        raise ValueError("Bees remote device or approved broker port is invalid.")
    if job["device"] == "cuda":
        if not isinstance(job["expected_gpu_pci"], str) or not re.fullmatch(
            r"[0-9a-f]{4}:[0-9a-f]{2}:[0-9a-f]{2}\.[0-7]",
            job["expected_gpu_pci"],
        ):
            raise ValueError("Bees remote ROCm actor needs a leased PCI identity.")
    elif job["expected_gpu_pci"] is not None:
        raise ValueError("Bees CPU-only actor cannot use a GPU.")
    for key in ("game_sha256", "trainer_sha256", "training_sha256",
                "actor_sha256", "auth_token_fingerprint", "runtime_version"):
        if not isinstance(job[key], str) or SHA.fullmatch(job[key]) is None:
            raise ValueError("Invalid Bees remote SHA-256: " + key)
    verify_pinned_runtime(job)
    for source, digest, executable in (
        ("game_binary", "game_sha256", True),
        ("training_script", "training_sha256", False),
        ("trainer_source", "trainer_sha256", False),
        ("actor_script", "actor_sha256", False),
    ):
        require_file(Path(job[source]), job[digest], executable)
    if Path(job["actor_script"]).parent != Path(job["training_script"]).parent:
        raise ValueError("Bees WAN actor escaped its frozen Python installation.")
    token = Path(job["auth_token_file"])
    if not token.is_absolute() or token.is_symlink() or not token.is_file() or (
        token.stat().st_mode & 0o077 or
        not 64 <= token.stat().st_size <= 128
    ):
        raise ValueError("Bees remote auth token is not a trusted private file.")
    value = token.read_text(encoding="ascii").strip()
    if SHA.fullmatch(value) is None or hashlib.sha256(
        value.encode("ascii")
    ).hexdigest() != job["auth_token_fingerprint"]:
        raise ValueError("Bees remote broker token differs from agent enrollment.")
    return job


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--job", required=True)
    args = parser.parse_args(argv)
    job = inspect_job(Path(args.job).absolute())
    if job["device"] == "cuda":
        from experiment_hub_tinybrains_rocm_verify import verify_runtime_device
        verify_runtime_device(job["expected_gpu_pci"])

    build, trainer, runtime = (
        job["game_sha256"], job["trainer_sha256"], job["runtime_version"]
    )
    os.environ.update({
        "BEES_TRAINING_BUILD_ID": build,
        "BEES_TRAINING_RUN_ID": job["lineage_root_id"],
        "BEES_TRAINING_COMPATIBILITY_KEY": hashlib.sha256(
            ("bees-native-wan-v1:" + build + ":" + trainer + ":" + runtime)
            .encode("ascii")
        ).hexdigest(),
        "BEES_TRAINING_ENVIRONMENT_ID": build,
    })
    # Lock one machine-local communicator port block for the actor lifetime.
    root = Path(job["output_root"])
    base_port, port_lock = reserve_base_port(root, job["job_id"], job["envs"])
    try:
        from bees_elastic_wan_actor_worker import main as run_actor
        return run_actor([
            "--actor-key", "hub-" + job["job_id"],
            "--envs", str(job["envs"]),
            "--broker-host", "127.0.0.1",
            "--broker-port", str(job["broker_port"]),
            "--env", job["game_binary"],
            "--auth-token-file", job["auth_token_file"],
            "--local-base-port", str(base_port),
            "--torch-device", job["device"],
            "--torch-threads", str(job["torch_threads"]),
        ])
    finally:
        port_lock.close()


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, ValueError, RuntimeError, TypeError, KeyError) as error:
        print("[Bees remote Hub] " + str(error), file=sys.stderr, flush=True)
        raise SystemExit(2)
