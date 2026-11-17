#!/usr/bin/env python3
"""Execute an issued native MA-POCA collection using a portable input bundle.

The importer restores the exact pinned work order, scenes and model artifacts;
the existing collector then owns simulation, receipt and upload. This command
does not create a second learner, coordinator or experience implementation.
"""
from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import time

from transfer_policy_inputs import import_inputs
from native_bundle_receiver import download_input_archive, bundle_token
from transfer_policy_bundle import checked_order


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    source = parser.add_mutually_exclusive_group(required=True)
    source.add_argument("--input-archive", type=Path,
                        help="local issued input archive")
    source.add_argument("--input-url", help="authenticated learner collector input endpoint")
    parser.add_argument("--collector-id", help="issued collector ID, required with --input-url")
    parser.add_argument("--scratch", required=True, type=Path,
                        help="existing private writable collector scratch directory")
    parser.add_argument("--rollout-binary", required=True, type=Path)
    parser.add_argument("--upload-url",
                        help="private authenticated learner collection receiver")
    parser.add_argument("--archive-output", type=Path,
                        help="optional explicit durable output archive for offline transfer")
    parser.add_argument("--bundle-inbox", type=Path,
                        help="optional existing shared collector inbox")
    parser.add_argument("--policy-worker", type=Path,
                        default=Path(__file__).with_name("policy_worker.py"))
    parser.add_argument("--python", help="worker's local Python interpreter")
    parser.add_argument("--stage-diagnostics", action="store_true")
    parser.add_argument("--token-file", type=Path,
                        help="private machine-owned transport credential, never supplied by a Hub job")
    parser.add_argument("--hub-job-id",
                        help="Hub-owned artifact/job UUID; requires exactly pinned work order")
    parser.add_argument("--cpu-quota", type=int,
                        help="Hub's acknowledged CPU quota for this job")
    parser.add_argument("--expected-collection-id")
    parser.add_argument("--expected-bundle-directory")
    parser.add_argument("--expected-source-contract")
    parser.add_argument("--expected-seed-offset", type=int)
    parser.add_argument("--expected-world-start", type=int)
    parser.add_argument("--expected-worlds", type=int)
    parser.add_argument("--expected-workers", type=int)
    parser.add_argument("--expected-physics-workers", type=int)
    args = parser.parse_args()
    if args.input_url and not args.collector_id:
        parser.error("--collector-id is required with --input-url")
    if args.hub_job_id is not None:
        if (args.collector_id is None or
                args.bundle_inbox is None or
                args.cpu_quota is None or args.cpu_quota < 1 or
                not all(value is not None for value in (
                    args.expected_collection_id, args.expected_bundle_directory,
                    args.expected_source_contract, args.expected_seed_offset, args.expected_world_start,
                    args.expected_worlds, args.expected_workers,
                    args.expected_physics_workers))):
            parser.error("Hub collector requires an exact job identity, inbox, quota and frozen work order")
    elif any(value is not None for value in (
            args.cpu_quota, args.expected_collection_id, args.expected_bundle_directory,
            args.expected_source_contract, args.expected_seed_offset, args.expected_world_start, args.expected_worlds,
            args.expected_workers, args.expected_physics_workers)):
        parser.error("frozen Hub allocation checks require --hub-job-id")
    # Local archives may be dedicated to one collector or shared by
    # every issued collector in a round. In the latter case identity is
    # mandatory and is checked against the immutable work order.
    upload_url = args.upload_url or args.input_url
    if not (upload_url or args.archive_output or args.bundle_inbox):
        parser.error("supply --upload-url, --archive-output or --bundle-inbox")
    original_scratch = args.scratch.absolute()
    if original_scratch.is_symlink():
        parser.error("--scratch cannot redirect through a symlink")
    scratch = original_scratch.resolve(strict=True)
    if not scratch.is_dir():
        parser.error("--scratch must be an existing non-symlink directory")
    binary = args.rollout_binary.resolve(strict=True)
    if not binary.is_file():
        parser.error("--rollout-binary must be a compiled native rollout executable")
    if args.bundle_inbox is not None and not args.bundle_inbox.is_dir():
        parser.error("--bundle-inbox must be an existing directory")

    transport_token = bundle_token(args.token_file) if args.token_file else None
    started = time.perf_counter()
    attempt = Path(tempfile.mkdtemp(prefix="native-portable-collector-", dir=scratch))
    inputs = attempt / "inputs"
    try:
        input_archive = args.input_archive
        if args.input_url:
            input_archive = attempt / "issued-inputs.tar"
            received = download_input_archive(
                args.input_url, args.collector_id, input_archive,
                token=transport_token,
            )
            print(json.dumps(received, sort_keys=True), flush=True)
        imported = import_inputs(input_archive, inputs,
                                 args.policy_worker, args.python, args.collector_id)
        print(json.dumps(imported, sort_keys=True), flush=True)
        if args.hub_job_id is not None:
            orders, job = checked_order(inputs / "orders.json", args.collector_id)
            if (job["bundle_directory"] != args.expected_bundle_directory or
                    job["collection_id"] != args.expected_collection_id or
                    orders.get("source_run_contract_sha256") != args.expected_source_contract or
                    orders.get("seed_offset") != args.expected_seed_offset or
                    job["world_start"] != args.expected_world_start or
                    job["world_count"] != args.expected_worlds or
                    job["rollout_workers"] != args.expected_workers or
                    job.get("physics_workers", 1) != args.expected_physics_workers):
                raise ValueError("downloaded native collector allocation differs from the immutable Hub job")
            workers = job["rollout_workers"]
            physics = job.get("physics_workers", 1)
            if (not 1 <= workers <= job["world_count"] or
                    not 1 <= physics <= 128 or
                    workers * physics > args.cpu_quota):
                raise ValueError("issued native simulations exceed the acknowledged Hub CPU lease")
            models = json.loads((inputs / "manifest.json").read_text(encoding="utf-8"))
            for model in models["models"]:
                if model.get("backend") == "onnx":
                    if any(provider != "CPUExecutionProvider"
                           for provider in model.get("providers", ["CPUExecutionProvider"])):
                        raise ValueError("CPU-only Hub collector was issued a GPU ONNX model")
                elif model.get("device", "cpu") != "cpu":
                    raise ValueError("CPU-only Hub collector was issued GPU model weights")
        command = [
            sys.executable,
            str(Path(__file__).with_name("run_distributed_collector.py")),
            str(inputs / "orders.json"), imported["collector_id"],
            str(inputs / "run.json"), str(inputs / "checkpoint.pt"),
            str(inputs / "manifest.json"), str(inputs / "trainer.yaml"),
            str(scratch),
            *([] if args.bundle_inbox is None else [str(args.bundle_inbox.resolve(strict=True))]),
            "--rollout-binary", str(binary),
            *([] if args.archive_output is None else ["--archive-output", str(args.archive_output)]),
            *([] if upload_url is None else ["--upload-url", upload_url]),
            *([] if args.hub_job_id is None else [
                "--hub-artifact-job-id", args.hub_job_id,
            ]),
            *([] if not args.stage_diagnostics else ["--stage-diagnostics"]),
        ]
        env = os.environ.copy()
        env["BEES_NATIVE_MODEL_STORE"] = str((inputs / "models").resolve(strict=True))
        if transport_token is not None:
            env["BEES_NATIVE_BUNDLE_TOKEN"] = transport_token
        subprocess.run(command, check=True, env=env)
        # Native collection receipt and uploaded archive are now durable.
        # The input checkpoint copy is a disposable staging artifact only.
        shutil.rmtree(attempt)
        print(json.dumps({
            "kind": "native_portable_collector_completed",
            "collector_id": imported["collector_id"],
            "input_bytes": imported["source_bytes"],
            "elapsed_wall_seconds": time.perf_counter() - started,
            "input_source": args.input_url or str(args.input_archive.resolve()),
        }, sort_keys=True), flush=True)
    except BaseException:
        # Failed work is never converted to success. Keep its source inputs
        # for a manual inspection/retry, with no optimizer publication.
        print(json.dumps({
            "kind": "native_portable_collector_incomplete",
            "attempt_directory": str(attempt),
        }, sort_keys=True), flush=True)
        raise


if __name__ == "__main__":
    main()
