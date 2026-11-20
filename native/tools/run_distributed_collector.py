"""Execute one native distributed collection order on an allocated machine.

The Experiment Hub owns CPU/GPU/RAM assignment and dispatch. The work-order
identity makes the local collector's portable output auditable by the
central multi-round MA-POCA session, without sharing absolute file paths.
"""

import argparse
import json
import os
import re
from pathlib import Path, PurePosixPath
import tempfile
import subprocess
import sys

from native_training_artifacts import (
    check_learner, file_sha256, native_source_contract_sha256,
    normalized_python_source_sha256, sync_directory,
)
from native_distributed_collection import verify_bundle
from transfer_policy_bundle import export_bundle_archive, artifact_paths, checked_order
from native_bundle_receiver import upload_bundle_archive



def acquire_collector_lease(parent):
    """OS-owned exclusive lease; a crashed collector releases it automatically.

    The marker inode persists, so removing/recreating the path cannot let
    simultaneous workers hold different locks for the same work order.
    """
    lease = (parent / ".collector.lease").open("a+b")
    try:
        if os.name == "nt":
            import msvcrt
            lease.seek(0, os.SEEK_END)
            if lease.tell() == 0:
                lease.write(b"0")
                lease.flush()
            lease.seek(0)
            msvcrt.locking(lease.fileno(), msvcrt.LK_NBLCK, 1)
        else:
            import fcntl
            fcntl.flock(lease.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
        return lease
    except BaseException:
        lease.close()
        raise


def persist_delivery_acknowledgement(inbox, hub_job_id, job, orders, bundle, delivered):
    """Durably attest the exact learner-accepted collector archive.

    A local rollout receipt proves gameplay finished, not that the learner
    received its samples. Only the authenticated receiver's acknowledgement
    permits the Hub to mark the worker successful. Publication is create-only
    so an unrelated later upload cannot rewrite that completion.
    """
    if (delivered.get("kind") != "native_bundle_received" or
            delivered.get("collector_id") != job["id"] or
            delivered.get("bundle_sha256") != bundle["sha256"] or
            not re.fullmatch(r"[0-9a-f]{64}", str(delivered.get("archive_sha256", ""))) or
            type(delivered.get("archive_bytes")) is not int or
            delivered["archive_bytes"] <= 0):
        raise ValueError("learner did not acknowledge the issued collector archive")
    root = inbox / hub_job_id
    if root.is_symlink() or not root.is_dir():
        raise ValueError("Hub collector artifact destination is not an owned directory")
    document = {
        "schema_version": 1,
        "kind": "native_collector_upload_accepted",
        "hub_job_id": hub_job_id,
        "collector_id": job["id"],
        "collection_id": job["collection_id"],
        "source_run_contract_sha256": orders["source_run_contract_sha256"],
        "bundle_sha256": bundle["sha256"],
        "archive_sha256": delivered["archive_sha256"],
        "archive_bytes": delivered["archive_bytes"],
    }
    target = root / "delivery-acknowledgement.json"
    if target.exists() or target.is_symlink():
        if (target.is_symlink() or not target.is_file() or
                target.stat().st_size > 4096 or
                json.loads(target.read_text(encoding="utf-8")) != document):
            raise ValueError("previous native collector upload acknowledgement differs")
        sync_directory(root)
        return
    temporary = None
    try:
        with tempfile.NamedTemporaryFile(mode="w", encoding="utf-8",
                dir=root, prefix=".delivery-acknowledgement-", suffix=".pending",
                delete=False) as stream:
            temporary = Path(stream.name)
            json.dump(document, stream, sort_keys=True)
            stream.write("\n")
            stream.flush()
            os.fsync(stream.fileno())
        try:
            os.link(temporary, target)
        except FileExistsError:
            if (target.is_symlink() or not target.is_file() or
                    target.stat().st_size > 4096 or
                    json.loads(target.read_text(encoding="utf-8")) != document):
                raise ValueError("concurrent collector delivery published conflicting proof")
        sync_directory(root)
    finally:
        if temporary is not None:
            temporary.unlink(missing_ok=True)


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
    if args.hub_artifact_job_id is not None and args.upload_url is None:
        parser.error("Hub collectors require a learner receiver to confirm actual delivery")
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
        candidates = [scratch / f"native-collector-{job['collection_id']}"]
        candidates += sorted(scratch.glob(prefix + "*"), reverse=True)
        for previous in candidates:
            if previous.is_symlink() or not previous.is_dir():
                continue
            candidate = previous / job["bundle_directory"] / "rollout-bundle.json"
            if reusable_collection(candidate, job, orders, args.leased_gpu_inference,
                                   args.leased_gpu_count) is not None:
                recovered = candidate
                previous_parent = previous
                break

    parent = (previous_parent if previous_parent is not None else
              scratch / f"native-collector-{job['collection_id']}")
    output = parent / job["bundle_directory"]
    attempt_lease = None
    if recovered is None:
        # Retain one original per-collector scratch identity across retries.
        # A different binary, model, source, allocation or trainer can never
        # acquire this old episode/capture state as its own rollout.
        if parent.is_symlink():
            raise ValueError("collector attempt path cannot be a symlink")
        parent.mkdir(exist_ok=True)
        attempt_lease = acquire_collector_lease(parent)
        worker = Path(source["policy_worker"])
        if not worker.is_absolute():
            worker = args.run_config.resolve(strict=True).parent / worker
        attempt = {
            "schema_version": 1,
            "kind": "native_collector_attempt",
            "collection_id": job["collection_id"],
            "collector_id": job["id"],
            "orders_sha256": file_sha256(orders_path),
            "run_sha256": file_sha256(args.run_config.resolve(strict=True)),
            "checkpoint_sha256": orders["input_checkpoint_sha256"],
            "manifest_sha256": orders["input_manifest_sha256"],
            "trainer_sha256": file_sha256(trainer),
            "runner_sha256": file_sha256(binary),
            "policy_worker_sha256": normalized_python_source_sha256(worker),
            "collector_options": job["collector_options"],
            "leased_gpu_inference": args.leased_gpu_inference,
            "leased_gpu_count": args.leased_gpu_count,
        }
        attempt_file = parent / "collector-attempt.json"
        resume = attempt_file.exists()
        if resume:
            if attempt_file.is_symlink() or not attempt_file.is_file() or (
                    json.loads(attempt_file.read_text(encoding="utf-8")) != attempt):
                raise ValueError("collector attempt changed its immutable training inputs")
        else:
            if any(item.name != ".collector.lease" for item in parent.iterdir()):
                raise ValueError("collector has an unowned interrupted scratch directory")
            with attempt_file.open("x", encoding="utf-8") as stream:
                json.dump(attempt, stream, sort_keys=True)
                stream.write("\n")
                stream.flush()
                os.fsync(stream.fileno())
            sync_directory(parent)
        command = [
            sys.executable, str(Path(__file__).with_name("train_policy_round.py")),
            str(args.run_config.resolve()), str(args.checkpoint.resolve()),
            str(trainer), str(output),
            "--model-manifest", str(args.model_manifest.resolve()),
            "--rollout-binary", str(binary),
            *([] if not resume else ["--resume-rollout"]),
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
        # A replacement Hub owner may inherit a fully committed local
        # capture from the failed service, but publish it under its NEW job
        # identity. Re-run neither gameplay nor the GPU learner when the
        # existing exact work-order receipt already verifies. This must
        # happen under the same exclusive collector attempt lease.
        local_complete = (reusable_collection(
            output / "rollout-bundle.json", job, orders,
            args.leased_gpu_inference, args.leased_gpu_count,
        ) if resume and inbox is not None else None)
        if local_complete is not None:
            from train_policy_round import deliver_bundle
            previous = json.loads(
                (output / "rollout-bundle.json").read_text(encoding="utf-8")
            )
            deliver_bundle(
                output, inbox, previous, delivered_name,
                preserve_delivery=True,
            )
            print(json.dumps({
                "kind": "native_distributed_collector_republished",
                "collector": job["id"], "round": orders["round"],
                "bundle": str(inbox / delivered_name),
                "prior_receipt_sha256": local_complete["sha256"],
            }), flush=True)
        else:
            print(json.dumps({
                "kind": "native_distributed_collector_started",
                "collector": job["id"], "round": orders["round"],
                "output": str(output), "worlds": job["world_count"],
                "rollout_workers": job["rollout_workers"],
                "resuming": resume,
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
        # The verified inbox may be durable even when the collector process
        # crashed before uploading an archive. The original scratch parent
        # can therefore already exist. Acquire its same OS-owned writer
        # lease before making or reusing a portable artifact; never create a
        # second unlocked archive publisher under the same work order.
        if attempt_lease is None and args.archive_output is None:
            if parent.is_symlink():
                raise ValueError("collector recovery directory is a symlink")
            parent.mkdir(exist_ok=True)
            attempt_lease = acquire_collector_lease(parent)
        # Direct upload keeps its own immutable archive in the collector's
        # existing work-order-owned scratch attempt.
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
            if args.hub_artifact_job_id is not None:
                persist_delivery_acknowledgement(
                    inbox, args.hub_artifact_job_id, job, orders, receipt, delivered,
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
