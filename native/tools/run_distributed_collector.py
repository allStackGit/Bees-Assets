"""Execute one native distributed collection order on an allocated machine.

The Experiment Hub owns CPU/GPU/RAM assignment and dispatch. The work-order
identity makes the local collector's portable output auditable by the
central multi-round MA-POCA session, without sharing absolute file paths.
"""

import argparse
import json
from pathlib import Path
import subprocess
import sys
import uuid

from native_training_artifacts import (
    check_learner, file_sha256, native_source_contract_sha256,
)
from native_distributed_collection import verify_bundle


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("orders", type=Path)
    parser.add_argument("collector_id")
    parser.add_argument("run_config", type=Path)
    parser.add_argument("checkpoint", type=Path)
    parser.add_argument("model_manifest", type=Path)
    parser.add_argument("trainer_config", type=Path)
    parser.add_argument("scratch_directory", type=Path)
    parser.add_argument("bundle_inbox", type=Path)
    parser.add_argument("--rollout-binary", type=Path, required=True)
    parser.add_argument("--stage-diagnostics", action="store_true")
    args = parser.parse_args()

    orders_path = args.orders.resolve(strict=True)
    orders = json.loads(orders_path.read_text(encoding="utf-8"))
    if (orders.get("kind") != "native_distributed_collection_orders" or
            orders.get("schema_version") != 1 or
            not isinstance(orders.get("jobs"), list)):
        parser.error("expected a native distributed collection work order")
    matches = [job for job in orders["jobs"] if job.get("id") == args.collector_id]
    if len(matches) != 1:
        parser.error("collector id is not uniquely allocated in the work order")
    job = matches[0]
    if "source_run_contract_sha256" in orders:
        version = orders.get("source_run_contract_version", 1)
        if version not in (1, 2):
            raise ValueError("unsupported native distributed physical source contract version")
        local_digest = native_source_contract_sha256(
            args.run_config, include_allocation=version == 1,
        )
        if local_digest != orders["source_run_contract_sha256"]:
            raise ValueError("collector's physical native source differs from the work order")
    elif file_sha256(args.run_config.resolve(strict=True)) != orders["source_run_config_sha256"]:
        # Old work orders must retain their original exact-byte identity;
        # a partially finished distributed round cannot silently migrate.
        raise ValueError("collector's legacy source run bytes differ from the work order")
    for path, digest in (
        (args.checkpoint, orders["input_checkpoint_sha256"]),
        (args.model_manifest, orders["input_manifest_sha256"]),
    ):
        if file_sha256(path.resolve(strict=True)) != digest:
            raise ValueError(f"collector received an incorrect source artifact: {path}")
    source = json.loads(args.run_config.read_text(encoding="utf-8"))
    if source.get("run_id") != orders["source_run_id"]:
        raise ValueError("collector run identity disagrees with work order")
    check_learner(args.model_manifest, args.checkpoint, orders["learner_slot"])
    binary = args.rollout_binary.resolve(strict=True)
    if not binary.is_file():
        parser.error("compiled native rollout binary is required")
    trainer = args.trainer_config.resolve(strict=True)
    scratch = args.scratch_directory.resolve(strict=True)
    inbox = args.bundle_inbox.resolve(strict=True)
    if not scratch.is_dir() or not inbox.is_dir():
        parser.error("scratch directory and shared bundle inbox must already exist")

    # Retain every interrupted local attempt for diagnosis. Only a
    # successfully collected, atomically delivered receipt is consumed
    # by the learner; retries never overwrite a committed bundle.
    parent = scratch / f"native-collector-{job['collection_id'][:12]}-{uuid.uuid4().hex}"
    parent.mkdir()
    output = parent / job["bundle_directory"]
    command = [
        sys.executable, str(Path(__file__).with_name("train_policy_round.py")),
        str(args.run_config.resolve()), str(args.checkpoint.resolve()),
        str(trainer), str(output),
        "--model-manifest", str(args.model_manifest.resolve()),
        "--rollout-binary", str(binary),
        "--bundle-inbox", str(inbox),
        *job["collector_options"],
    ]
    if orders["learner_slot"] is not None:
        command.extend(["--learner-slot", str(orders["learner_slot"])])
    if args.stage_diagnostics:
        command.append("--stage-diagnostics")
    print(json.dumps({
        "kind": "native_distributed_collector_started",
        "collector": job["id"], "round": orders["round"],
        "output": str(output), "worlds": job["world_count"],
        "rollout_workers": job["rollout_workers"],
    }), flush=True)
    subprocess.run(command, check=True)
    receipt_path = inbox / job["bundle_directory"] / "rollout-bundle.json"
    receipt = verify_bundle(receipt_path, job, orders)
    print(json.dumps({
        "kind": "native_distributed_collector_completed",
        "collector": job["id"], "round": orders["round"],
        "bundle": str(receipt_path),
        "bundle_sha256": receipt["sha256"],
        "world_steps": receipt["metrics"]["successful_world_steps"],
        "policy_rows": receipt["metrics"]["successful_policy_rows"],
        "world_steps_per_wall_second":
            receipt["metrics"]["successful_world_steps"] / receipt["metrics"]["wall_seconds"],
    }), flush=True)


if __name__ == "__main__":
    main()
