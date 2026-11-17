#!/usr/bin/env python3
"""Portable, verified native MA-POCA collector inputs for another machine.

Export an issued collector's complete source scenes and model artifacts in
one immutable tar. Import materializes a machine-local run configuration
without changing its physical source contract or the model manifest bytes.
The collector then uses the existing run_distributed_collector.py unchanged.
"""
from __future__ import annotations

import argparse
import hashlib
import io
import json
import os
from pathlib import Path, PurePosixPath
import re
import sys
import tarfile
import tempfile
import time

from native_training_artifacts import (
    check_learner, file_sha256, native_source_contract_sha256, sync_directory,
)
from transfer_policy_bundle import checked_order

CHUNK = 1024 * 1024
MAX_BYTES = 8 * 1024 ** 3
MAX_FILES = 512
SHA = re.compile(r"[0-9a-f]{64}\Z")
RESERVED = {"inputs.json", "orders.json", "run.json", "manifest.json",
            "checkpoint.pt", "trainer.yaml"}


def relative_artifact(value):
    if not isinstance(value, str) or not value or chr(92) in value or ":" in value:
        raise ValueError("model manifest needs portable relative artifact paths")
    if value == "checkpoint.pt":
        return value
    path = PurePosixPath(value)
    if (path.is_absolute() or path.as_posix() != value or
            any(part in ("", ".", "..") for part in path.parts) or
            len(path.parts) > 8 or value in RESERVED or
            path.parts[0] in ("scene", "source", ".") or len(value) > 255):
        raise ValueError(f"invalid portable model artifact path: {value!r}")
    return value


def resolve_source(root, value):
    if not isinstance(value, str) or not value:
        raise ValueError("collector run has an invalid physical source file")
    candidate = Path(value)
    return (candidate if candidate.is_absolute() else root / candidate).resolve(strict=True)


def source_files(run_path, trainer_path, checkpoint_path, manifest_path, orders):
    run_path = Path(run_path).resolve(strict=True)
    trainer_path = Path(trainer_path).resolve(strict=True)
    checkpoint_path = Path(checkpoint_path).resolve(strict=True)
    manifest_path = Path(manifest_path).resolve(strict=True)
    document = json.loads(run_path.read_text(encoding="utf-8"))
    models = json.loads(manifest_path.read_text(encoding="utf-8"))
    if (not isinstance(models, dict) or
            models.get("format") != "bees-native-model-v1" or
            not isinstance(models.get("models"), list)):
        raise ValueError("invalid native model manifest")
    if orders.get("source_run_contract_version") == 2:
        contract = native_source_contract_sha256(run_path)
    elif "source_run_contract_sha256" in orders:
        contract = native_source_contract_sha256(run_path, include_allocation=True)
    else:
        contract = file_sha256(run_path)
    expected_contract = orders.get("source_run_contract_sha256",
                                   orders.get("source_run_config_sha256"))
    if contract != expected_contract:
        raise ValueError("native scene or run contract differs from issued collector order")
    for source, digest in (
        (checkpoint_path, orders["input_checkpoint_sha256"]),
        (manifest_path, orders["input_manifest_sha256"]),
    ):
        if file_sha256(source) != digest:
            raise ValueError(f"native model artifact changed from issued order: {source}")
    check_learner(manifest_path, checkpoint_path, orders["learner_slot"])
    source = {
        "orders.json": Path(orders["_orders_file"]),
        "run.json": run_path,
        "trainer.yaml": trainer_path,
        "checkpoint.pt": checkpoint_path,
        "manifest.json": manifest_path,
    }
    for index, level in enumerate(document.get("levels", [])):
        name = f"scene/{index:04d}.json"
        source[name] = resolve_source(run_path.parent, level["scene"])
    for name in ("collision_matrix", "stage_palette"):
        if document.get(name) is not None:
            source[f"source/{name}.json"] = resolve_source(run_path.parent, document[name])
    for model in models["models"]:
        if not isinstance(model, dict) or not SHA.fullmatch(str(model.get("sha256", ""))):
            raise ValueError("model manifest contains an invalid checkpoint identity")
        name = relative_artifact(model.get("path"))
        artifact = resolve_source(manifest_path.parent, name)
        if file_sha256(artifact) != model["sha256"]:
            raise ValueError("manifest model weight differs from its pinned digest")
        if name in source:
            if source[name] != artifact or file_sha256(source[name]) != model["sha256"]:
                raise ValueError("model file collides with a different pinned source file")
        else:
            source[name] = artifact
    if not 1 <= len(source) <= MAX_FILES:
        raise ValueError("portable input bundle exceeds source-file limit")
    if len(source) != len(set(source)):
        raise ValueError("portable input file name collision")
    return source, contract


def tar_info(name, size):
    info = tarfile.TarInfo(name)
    info.size = size
    info.mode = 0o600
    info.mtime = 0
    return info


class HashingReader:
    def __init__(self, file):
        self.file = file
        self.hash = hashlib.sha256()
        self.length = 0

    def read(self, size=-1):
        chunk = self.file.read(size)
        self.hash.update(chunk)
        self.length += len(chunk)
        return chunk


def export_inputs(orders_file, collector_id, run, trainer, checkpoint, manifest, destination):
    started = time.perf_counter()
    orders_path = Path(orders_file).resolve(strict=True)
    orders, job = checked_order(orders_path, collector_id)
    # The helper needs the exact original orders bytes in the tar, not a
    # second independently serialized work order.
    orders = dict(orders)
    orders["_orders_file"] = str(orders_path)
    sources, contract = source_files(run, trainer, checkpoint, manifest, orders)
    if sources["orders.json"] != orders_path:
        raise ValueError("issued order bytes could not be pinned")
    files = {}
    total = 0
    for name, source in sorted(sources.items()):
        if source.is_symlink() or not source.is_file():
            raise ValueError("native portable source is not a regular file")
        size = source.stat().st_size
        if size <= 0 or size > MAX_BYTES or total + size > MAX_BYTES:
            raise ValueError("native collector inputs exceed transfer limits")
        total += size
        files[name] = {"bytes": size, "sha256": file_sha256(source)}
    metadata = {
        "kind": "native_collector_inputs", "schema_version": 1,
        "collector_id": collector_id,
        "collection_id": job["collection_id"],
        "source_contract_sha256": contract,
        "input_checkpoint_sha256": orders["input_checkpoint_sha256"],
        "input_manifest_sha256": orders["input_manifest_sha256"],
        "files": files,
    }
    header = json.dumps(metadata, sort_keys=True, separators=(",", ":")).encode("utf-8")
    target = Path(destination).absolute()
    parent = target.parent.resolve(strict=True)
    if target.exists() or target.is_symlink():
        raise FileExistsError(f"native collector input archive already exists: {target}")
    temporary = None
    try:
        with tempfile.NamedTemporaryFile(mode="w+b", prefix=".native-inputs-",
                                         suffix=".partial", dir=parent, delete=False) as output:
            temporary = Path(output.name)
            with tarfile.open(fileobj=output, mode="w", format=tarfile.PAX_FORMAT) as archive:
                archive.addfile(tar_info("inputs.json", len(header)), io.BytesIO(header))
                for name, source in sorted(sources.items()):
                    expected = files[name]
                    with source.open("rb") as body:
                        stream = HashingReader(body)
                        archive.addfile(tar_info(name, expected["bytes"]), stream)
                        if stream.length != expected["bytes"] or stream.hash.hexdigest() != expected["sha256"]:
                            raise ValueError(f"source artifact changed while exporting: {name}")
            output.flush()
            os.fsync(output.fileno())
        os.link(temporary, target)
        sync_directory(parent)
    finally:
        if temporary is not None:
            temporary.unlink(missing_ok=True)
    return {"kind": "native_collector_inputs_exported", "collector_id": collector_id,
            "archive": str(target), "files": len(files), "source_bytes": total,
            "archive_bytes": target.stat().st_size,
            "archive_sha256": file_sha256(target),
            "wall_seconds": time.perf_counter() - started}


def import_inputs(archive_path, destination, policy_worker, interpreter=None):
    started = time.perf_counter()
    archive_path = Path(archive_path).resolve(strict=True)
    target = Path(destination).absolute()
    parent = target.parent.resolve(strict=True)
    if target.exists() or target.is_symlink():
        raise FileExistsError(f"collector source destination must be new: {target}")
    policy_worker = Path(policy_worker).resolve(strict=True)
    if not policy_worker.is_file():
        raise ValueError("native collector needs an installed local policy worker")
    with tempfile.TemporaryDirectory(prefix=".native-input-import-", dir=parent) as staging:
        root = Path(staging) / "bundle"
        root.mkdir()
        with tarfile.open(archive_path, mode="r|") as tar:
            first = next(iter(tar), None)
            if first is None or first.name != "inputs.json" or not first.isfile() or first.size > 256 * 1024:
                raise ValueError("native collector input archive has no bounded manifest")
            header_stream = tar.extractfile(first)
            if header_stream is None:
                raise ValueError("native collector input manifest cannot be read")
            info = json.loads(header_stream.read())
            if (info.get("kind") != "native_collector_inputs" or
                    info.get("schema_version") != 1 or
                    not isinstance(info.get("files"), dict) or
                    not 1 <= len(info["files"]) <= MAX_FILES):
                raise ValueError("native collector input archive has an invalid manifest")
            files = info["files"]
            remaining = set(files)
            total = 0
            for item in tar:
                name = item.name
                if name not in remaining or not item.isfile():
                    raise ValueError(f"unexpected or duplicate native collector artifact: {name}")
                record = files[name]
                if (not isinstance(record, dict) or set(record) != {"bytes", "sha256"} or
                        type(record["bytes"]) is not int or record["bytes"] <= 0 or
                        not SHA.fullmatch(str(record["sha256"])) or
                        item.size != record["bytes"] or
                        total + item.size > MAX_BYTES):
                    raise ValueError("invalid native collector artifact identity or size")
                # Tar member names are never used until validated against
                # the schema's allowable paths and the fresh staging root.
                if name not in RESERVED:
                    allowed = name.startswith("scene/") and re.fullmatch(r"scene/[0-9]{4}\.json", name) or (
                        name in ("source/collision_matrix.json", "source/stage_palette.json")
                    ) or name == relative_artifact(name)
                    if not allowed:
                        raise ValueError(f"unsafe native input artifact path: {name}")
                member_path = PurePosixPath(name)
                if (member_path.is_absolute() or len(member_path.parts) > 8 or
                        any(component in (".", "..") for component in member_path.parts)):
                    raise ValueError("native source archive path escaped its root")
                destination_file = root.joinpath(*member_path.parts)
                destination_file.parent.mkdir(parents=True, exist_ok=True)
                stream = tar.extractfile(item)
                if stream is None:
                    raise ValueError("missing native source archive bytes")
                digest = hashlib.sha256()
                with destination_file.open("xb") as output:
                    remaining_bytes = item.size
                    while remaining_bytes:
                        chunk = stream.read(min(CHUNK, remaining_bytes))
                        if not chunk:
                            raise ValueError("truncated native source archive")
                        output.write(chunk)
                        digest.update(chunk)
                        remaining_bytes -= len(chunk)
                    output.flush()
                    os.fsync(output.fileno())
                if digest.hexdigest() != record["sha256"]:
                    raise ValueError(f"native source artifact digest mismatch: {name}")
                remaining.remove(name)
                total += item.size
            if remaining:
                raise ValueError("native collector input archive is missing source artifacts")
        orders_file = root / "orders.json"
        orders, job = checked_order(orders_file, info.get("collector_id"))
        if info.get("collection_id") != job["collection_id"]:
            raise ValueError("native collector identity differs from its issued order")
        if file_sha256(root / "manifest.json") != orders["input_manifest_sha256"] or (
            file_sha256(root / "checkpoint.pt") != orders["input_checkpoint_sha256"]
        ):
            raise ValueError("native collector model inputs differ from its issued order")
        models = json.loads((root / "manifest.json").read_text(encoding="utf-8"))
        if models.get("format") != "bees-native-model-v1" or not isinstance(models.get("models"), list):
            raise ValueError("collector model manifest is invalid")
        expected_files = set(RESERVED)
        for model in models["models"]:
            name = relative_artifact(model["path"])
            expected_files.add(name)
            if file_sha256(root / name) != model["sha256"]:
                raise ValueError("portable collector model weights failed verification")
        run = json.loads((root / "run.json").read_text(encoding="utf-8"))
        for index in range(len(run.get("levels", []))):
            expected_files.add(f"scene/{index:04d}.json")
        for name in ("collision_matrix", "stage_palette"):
            if run.get(name) is not None:
                expected_files.add(f"source/{name}.json")
        if set(files) != expected_files:
            raise ValueError("collector input archive contains unissued source artifacts")
        if len(run.get("levels", [])) != len([
                name for name in files if re.fullmatch(r"scene/[0-9]{4}\.json", name)]):
            raise ValueError("collector Level source count changed")
        for index, level in enumerate(run["levels"]):
            level["scene"] = f"scene/{index:04d}.json"
        for name in ("collision_matrix", "stage_palette"):
            if run.get(name) is not None:
                run[name] = f"source/{name}.json"
        run["python"] = interpreter or sys.executable
        run["policy_worker"] = str(policy_worker)
        run["model_manifest"] = "manifest.json"
        # Rewrite only machine paths. The imported scene and policy worker
        # must reconstruct the learner's EXACT portable physical contract.
        with (root / "run.json").open("w", encoding="utf-8") as output:
            json.dump(run, output, indent=2)
            output.write("\n")
        source_version = orders.get("source_run_contract_version")
        actual = (native_source_contract_sha256(root / "run.json",
                                               include_allocation=source_version != 2)
                  if "source_run_contract_sha256" in orders
                  else file_sha256(root / "run.json"))
        expected = orders.get("source_run_contract_sha256",
                              orders.get("source_run_config_sha256"))
        if actual != expected or info.get("source_contract_sha256") != expected:
            raise ValueError("local native collector scene or policy worker differs from issued task")
        check_learner(root / "manifest.json", root / "checkpoint.pt", orders["learner_slot"])
        root.rename(target)
        sync_directory(parent)
    return {
        "kind": "native_collector_inputs_imported", "collector_id": info["collector_id"],
        "directory": str(target), "source_contract_sha256": actual,
        "files": len(files), "source_bytes": total,
        "wall_seconds": time.perf_counter() - started,
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    export = commands.add_parser("export")
    export.add_argument("--orders", required=True, type=Path)
    export.add_argument("--collector-id", required=True)
    export.add_argument("--run-config", required=True, type=Path)
    export.add_argument("--trainer-config", required=True, type=Path)
    export.add_argument("--checkpoint", required=True, type=Path)
    export.add_argument("--model-manifest", required=True, type=Path)
    export.add_argument("--archive", required=True, type=Path)
    imp = commands.add_parser("import")
    imp.add_argument("--archive", required=True, type=Path)
    imp.add_argument("--destination", required=True, type=Path)
    imp.add_argument("--policy-worker", type=Path, default=Path(__file__).with_name("policy_worker.py"))
    imp.add_argument("--python")
    args = parser.parse_args()
    if args.command == "export":
        result = export_inputs(args.orders, args.collector_id,
                               args.run_config, args.trainer_config, args.checkpoint,
                               args.model_manifest, args.archive)
    else:
        result = import_inputs(args.archive, args.destination,
                               args.policy_worker, args.python)
    print(json.dumps(result, sort_keys=True), flush=True)


if __name__ == "__main__":
    main()
