#!/usr/bin/env python3
"""Run one Hub-owned Bees Unity WAN simulation actor, without an optimizer.

The installed adapter freezes the same Unity worker/Python runtime as the
learner and the private loopback tunnel to that learner. Jobs cannot supply
an executable, network address, port, or authentication key.
"""
from __future__ import annotations

import argparse
import ipaddress
import hashlib
import json
import os
import signal
import socket
import subprocess
import time
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
    "game_payload_sha256", "training_script", "training_sha256", "trainer_source",
    "trainer_sha256", "runtime_version", "evaluator_script",
    "evaluator_core", "opponent_script", "actor_script",
    "actor_sha256", "envs", "torch_threads", "device", "expected_gpu_pci",
    "auth_token_file", "auth_token_fingerprint", "broker_port", "slot",
}
SSH_FIELDS = {
    "learner_machine_id", "tailnet_ip", "ssh_user", "ssh_identity_file",
    "ssh_known_hosts_file", "ssh_port", "remote_broker_port", "ssh_executable",
}


def inspect_forward(job: dict) -> dict | None:
    plan = job.get("ssh_forward")
    if job["schema_version"] == 1:
        return None
    if job["schema_version"] != 2 or not isinstance(plan, dict) or (
        set(plan) != SSH_FIELDS
    ):
        raise ValueError("Bees native managed WAN forwarding has an unexpected schema.")
    if not re.fullmatch(r"[a-z][a-z0-9_-]{0,47}", str(plan["learner_machine_id"])) or (
        not re.fullmatch(r"[a-z_][a-z0-9_-]{0,31}", str(plan["ssh_user"]))
    ):
        raise ValueError("Bees managed WAN forward needs pinned machine and SSH identities.")
    try:
        address = ipaddress.ip_address(plan["tailnet_ip"])
        if address not in ipaddress.ip_network("100.64.0.0/10") or (
            str(address) != plan["tailnet_ip"]
        ):
            raise ValueError("Not a canonical Tailnet IPv4.")
    except (ValueError, TypeError) as error:
        raise ValueError("Bees WAN SSH endpoint must be a canonical Tailnet IPv4.") from error
    for key in ("ssh_port", "remote_broker_port"):
        value = plan[key]
        if type(value) is not int or not 1 <= value <= 65535 or (
            key == "remote_broker_port" and value < 1024
        ):
            raise ValueError("Invalid pinned SSH or WAN broker port.")
    for key in ("ssh_executable", "ssh_identity_file", "ssh_known_hosts_file"):
        value = plan[key]
        if not isinstance(value, str):
            raise ValueError("Invalid private WAN SSH file path.")
        file = Path(value)
        if not file.is_absolute() or file.is_symlink() or (
            file.resolve() != file or not file.is_file()
        ):
            raise ValueError("WAN SSH requires a pinned real absolute file.")
        stat = file.stat()
        if key == "ssh_identity_file":
            if not 16 <= stat.st_size <= 32768 or stat.st_mode & 0o077 or (
                stat.st_uid != os.getuid()
            ):
                raise ValueError("WAN SSH identity must be an owner-private installed key.")
        elif key == "ssh_known_hosts_file":
            if not 40 <= stat.st_size <= 1048576:
                raise ValueError("WAN SSH pinned host keys are missing.")
        elif not os.access(file, os.X_OK):
            raise ValueError("Approved system OpenSSH executable is not usable.")
    return plan


def owned_forward(job: dict, plan: dict, argv: list[str]) -> int:
    """Own both the OpenSSH forward and actor in one OS-enforced Hub job."""
    port = job["broker_port"]
    # A preexisting socket might be some other learner's broker. Refuse to
    # reuse it, even if it happens to accept the expected HMAC secret.
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as probe:
        probe.bind(("127.0.0.1", port))
    command = [
        plan["ssh_executable"],
        "-F", "/dev/null", "-N", "-T",
        "-o", "BatchMode=yes",
        "-o", "IdentitiesOnly=yes",
        "-o", "StrictHostKeyChecking=yes",
        "-o", "ExitOnForwardFailure=yes",
        "-o", "ServerAliveInterval=15",
        "-o", "ServerAliveCountMax=3",
        "-o", "ConnectTimeout=10",
        "-o", "PasswordAuthentication=no",
        "-o", "KbdInteractiveAuthentication=no",
        "-o", "UserKnownHostsFile=" + plan["ssh_known_hosts_file"],
        "-i", plan["ssh_identity_file"],
        "-p", str(plan["ssh_port"]),
        "-L", "127.0.0.1:" + str(port) + ":127.0.0.1:" +
            str(plan["remote_broker_port"]),
        plan["ssh_user"] + "@" + plan["tailnet_ip"],
    ]
    directory = Path(job["output_root"])
    log = directory / "wan-forward-stderr.log"
    child = None
    with log.open("x", encoding="utf-8") as output:
        os.chmod(log, 0o600)
        tunnel = subprocess.Popen(
            command, stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL,
            stderr=output, cwd=str(directory),
        )
        try:
            deadline = time.monotonic() + 20.0
            while time.monotonic() < deadline:
                if tunnel.poll() is not None:
                    raise RuntimeError("WAN SSH forward exited before its local listener became ready.")
                try:
                    with socket.create_connection(("127.0.0.1", port), timeout=0.3):
                        break
                except OSError:
                    time.sleep(0.25)
            else:
                raise RuntimeError("WAN SSH forward did not establish its approved local listener.")
            # The adapter passed the exact immutable actor-script path.
            # Run it as a child so that a failed forward tears down the
            # simulation before a stale learner can trust its trajectories.
            child = subprocess.Popen(
                [sys.executable, "-B", job["actor_script"], *argv],
                cwd=str(Path(job["training_script"]).parent),
            )
            while child.poll() is None:
                if tunnel.poll() is not None:
                    child.terminate()
                    try:
                        child.wait(timeout=10)
                    except subprocess.TimeoutExpired:
                        child.kill()
                        child.wait(timeout=10)
                    raise RuntimeError("WAN SSH forward terminated during an active Bees rollout.")
                time.sleep(1)
            return int(child.returncode or 0)
        finally:
            if child is not None and child.poll() is None:
                child.terminate()
                try:
                    child.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    child.kill()
                    child.wait(timeout=10)
            if tunnel.poll() is None:
                tunnel.terminate()
                try:
                    tunnel.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    tunnel.kill()
                    tunnel.wait(timeout=5)



def inspect_job(filename: Path) -> dict:
    if not filename.is_absolute() or filename.is_symlink() or (
        not filename.is_file() or filename.stat().st_size > 32768
    ):
        raise ValueError("Bees remote job definition must be a small immutable file.")
    job = json.loads(filename.read_text(encoding="utf-8"))
    if not isinstance(job, dict) or (
        job.get("schema_version") == 1 and set(job) != FIELDS
    ) or (
        job.get("schema_version") == 2 and set(job) != FIELDS | {"ssh_forward"}
    ) or job.get("schema_version") not in (1, 2):
        raise ValueError("Unexpected Bees remote job schema.")
    inspect_forward(job)
    for field in ("job_id", "learner_job_id", "lineage_root_id"):
        item = job[field]
        if not isinstance(item, str) or str(uuid.UUID(item)) != item:
            raise ValueError("Invalid Bees remote " + field)
    if job["job_id"] == job["learner_job_id"]:
        raise ValueError("A Bees remote actor cannot own the learner job.")
    if Path(job["output_root"]) != filename.parent or (
        not 1 <= job["envs"] <= 64 or
        not 1 <= job["torch_threads"] <= 64 or
        job["torch_threads"] > job["envs"] or
        type(job["slot"]) is not int or not 0 <= job["slot"] <= 7
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
    for key in ("game_sha256", "game_payload_sha256", "trainer_sha256", "training_sha256",
                "actor_sha256", "auth_token_fingerprint", "runtime_version"):
        if not isinstance(job[key], str) or SHA.fullmatch(job[key]) is None:
            raise ValueError("Invalid Bees remote SHA-256: " + key)
    verify_pinned_runtime(job)
    from bees_unity_worker_identity import unity_worker_tree_sha256
    if unity_worker_tree_sha256(Path(job["game_binary"])) != job["game_payload_sha256"]:
        raise ValueError("Remote Unity simulation files differ from the learner's pinned build.")
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
        job["game_payload_sha256"], job["trainer_sha256"], job["runtime_version"]
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
        actor_argv = [
            "--actor-key", "hub-" + job["learner_job_id"] + "-" + str(job["slot"]),
            "--envs", str(job["envs"]),
            "--broker-host", "127.0.0.1",
            "--broker-port", str(job["broker_port"]),
            "--env", job["game_binary"],
            "--auth-token-file", job["auth_token_file"],
            "--local-base-port", str(base_port),
            "--torch-device", job["device"],
            "--torch-threads", str(job["torch_threads"]),
        ]
        managed = inspect_forward(job)
        if managed is not None:
            return owned_forward(job, managed, actor_argv)
        # Existing manually provisioned private forward remains supported.
        from bees_elastic_wan_actor_worker import main as run_actor
        return run_actor(actor_argv)
    finally:
        port_lock.close()


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, ValueError, RuntimeError, TypeError, KeyError) as error:
        print("[Bees remote Hub] " + str(error), file=sys.stderr, flush=True)
        raise SystemExit(2)
