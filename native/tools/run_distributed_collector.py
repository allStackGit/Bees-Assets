"""Execute one native distributed collection order on an allocated machine.

The Experiment Hub owns CPU/GPU/RAM assignment and dispatch. The work-order
identity makes the local collector's portable output auditable by the
central multi-round MA-POCA session, without sharing absolute file paths.
"""

import argparse
import json
import re
from pathlib import Path, PurePosixPath
import subprocess
import sys
import uuid

from native_training_artifacts import (
    check_learner, file_sha256, native_source_contract_sha256,
)
from native_distributed_collection import verify_bundle
from transfer_policy_bundle import export_bundle_archive, artifact_paths, checked_order
from native_bundle_receiver import upload_bundle_archive



def reusable_collection(receipt_path, job, orders, leased_gpu_inference=False,
                        leased_gpu_count=1):
    """Accept completed local work only with its original pinned capture files.

    A previous transport failure must never turn a partial simulation or a
    different collector's outputs into a successful learner input.
    """
    receipt_path = Path(receipt_path)
    root = receipt_path.parent
    if (root.is_symlink() or not root.is_dir() or
            receipt_path.is_symlink() or not receipt_path.is_file()):
        return None
    try:
        receipt = verify_bundle(receipt_path, job, orders)
        document = json.loads(receipt_path.read_text(encoding="utf-8"))
        if bool(document.get("leased_gpu_inference", False)) != leased_gpu_inference:
            return None
        if leased_gpu_inference and document.get("leased_gpu_count", 1) != leased_gpu_count:
            return None
        captures = artifact_paths(document)
        canonical = root.resolve(strict=True)
        for name, digest in captures.items():
            file = root.joinpath(*PurePosixPath(name).parts)
            if (file.is_symlink() or not file.is_file() or
                    not file.resolve(strict=True).is_relative_to(canonical) or
                    file_sha256(file) != digest):
                return None
        return receipt
    except (OSError, ValueError, KeyError, TypeError):
        return None


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("orders", type=Path)
    parser.add_argument("collector_id")
    parser.add_argument("run_config", type=Path)
    parser.add_argument("checkpoint", type=Path)
    parser.add_argument("model_manifest", type=Path)
    parser.add_argument("trainer_config", type=Path)
    parser.add_argument("scratch_directory", type=Path)
    parser.add_argument("bundle_inbox", type=Path, nargs="?",
                        help="local/shared inbox; omit with --archive-output for direct archive delivery")
    parser.add_argument("--rollout-binary", type=Path, required=True)
    parser.add_argument("--stage-diagnostics", action="store_true")
    parser.add_argument("--leased-gpu-inference", action="store_true",
                        help="Hub-verified exclusive GPU inference placement")
    parser.add_argument("--leased-gpu-count", type=int, default=1,
                        help="exact Hub-attested count of one or two ROCm devices")
    parser.add_argument("--archive-output", type=Path,
                        help="publish a portable verified tar for transfer to a different machine")
    parser.add_argument("--upload-url",
                        help="stream completed verified archive to a native learner bundle receiver")
    parser.add_argument("--hub-artifact-job-id",
                        help="Hub's separate immutable job UUID for local verified artifact publication")
    args = parser.parse_args()
    if args.leased_gpu_inference and args.hub_artifact_job_id is None:
        parser.error("--leased-gpu-inference requires the approved Hub artifact job")
    if args.leased_gpu_count not in (1, 2) or (
            args.leased_gpu_count != 1 and not args.leased_gpu_inference):
        parser.error("dual-GPU collector needs authorized Hub GPU placement")
    if args.hub_artifact_job_id is not None and (
            args.bundle_inbox is None or
            re.fullmatch(r"[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}",
                         args.hub_artifact_job_id) is None):
        parser.error("--hub-artifact-job-id requires a shared inbox and a Hub job UUID")
    if args.bundle_inbox is None and args.archive_output is None and not args.upload_url:
        parser.error("supply a bundle inbox, --archive-output or --upload-url")

    orders_path = args.orders.resolve(strict=True)
    orders, job = checked_order(orders_path, args.collector_id)
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
    inbox = args.bundle_inbox.resolve(strict=True) if args.bundle_inbox else None
    if not scratch.is_dir() or (inbox is not None and not inbox.is_dir()):
        parser.error("scratch directory and optional bundle inbox must already exist")

    # Retries of the *same immutable order* reuse finished experience. Failed
    # simulations are left untouched; only a complete receipt whose captured
    # bytes still match every digest can bypass the expensive rollout.
    delivered_name = args.hub_artifact_job_id or job["bundle_directory"]
    recovered = None
    previous_parent = None
    if inbox is not None:
        candidate = inbox / delivered_name / "rollout-bundle.json"
        if reusable_collection(
                candidate, job, orders, args.leased_gpu_inference,
                args.leased_gpu_count,
            ) is not None:
            recovered = candidate
    else:
        prefix = f"native-collector-{job['collection_id'][:12]}-"
        for previous in sorted(scratch.glob(prefix + "*"), reverse=True):
            if previous.is_symlink() or not previous.is_dir():
                continue
            candidate = previous / job["bundle_directory"] / "rollout-bundle.json"
            if reusable_collection(candidate, job, orders, args.leased_gpu_inference) is not None:
                recovered = candidate
                previous_parent = previous
                break

    parent = (previous_parent if previous_parent is not None else
              scratch / f"native-collector-{job['collection_id'][:12]}-{uuid.uuid4().hex}")
    output = parent / job["bundle_directory"]
    if recovered is None:
        parent.mkdir()
        command = [
            sys.executable, str(Path(__file__).with_name("train_policy_round.py")),
            str(args.run_config.resolve()), str(args.checkpoint.resolve()),
            str(trainer), str(output),
            "--model-manifest", str(args.model_manifest.resolve()),
            "--rollout-binary", str(binary),
            *([] if inbox is None else ["--bundle-inbox", str(inbox)]),
            *job["collector_options"],
            *([] if not args.leased_gpu_inference else [
                "--leased-gpu-inference", "--leased-gpu-count",
                str(args.leased_gpu_count),
            ]),
            *([] if args.hub_artifact_job_id is None else [
                "--bundle-delivery-name", delivered_name,
            ]),
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
        receipt_path = (
            output / "rollout-bundle.json" if inbox is None
            else inbox / delivered_name / "rollout-bundle.json"
        )
        receipt = verify_bundle(receipt_path, job, orders)
    else:
        receipt_path = recovered
        receipt = verify_bundle(receipt_path, job, orders)
        print(json.dumps({
            "kind": "native_distributed_collector_recovered",
            "collector": job["id"], "round": orders["round"],
            "bundle": str(receipt_path),
            "worlds": job["world_count"],
        }), flush=True)
    if args.archive_output is not None or args.upload_url:
        # A recovered shared-inbox receipt has no old scratch parent, while
        # a recovered portable receipt already has its original parent.
        if recovered is not None and previous_parent is None and args.archive_output is None:
            parent.mkdir()
        # Direct upload keeps its own immutable archive in the collector's
        # already unique scratch attempt; no shared filesystem is required.
        archive_path = args.archive_output or (parent / (job["bundle_directory"] + ".tar"))
        if (recovered is not None and args.upload_url and
                archive_path.is_file() and not archive_path.is_symlink()):
            # This exact archive may have reached the learner before the
            # connection broke. The receiver still validates its bytes
            # against the issued work order before acknowledging it.
            archive = {
                "archive": str(archive_path),
                "archive_sha256": file_sha256(archive_path),
                "archive_bytes": archive_path.stat().st_size,
                "recovered_archive": True,
            }
        else:
            archive = export_bundle_archive(receipt_path, job, orders, archive_path)
        print(json.dumps({
            "kind": "native_distributed_collector_archive_ready",
            "collector": job["id"], **archive,
        }), flush=True)
        if args.upload_url:
            delivered = upload_bundle_archive(
                archive_path, job["id"], args.upload_url,
                archive_sha256=archive["archive_sha256"],
            )
            print(json.dumps({
                "kind": "native_distributed_collector_delivered",
                "collector": job["id"], **delivered,
            }), flush=True)
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
