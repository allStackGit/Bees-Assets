"""Export a native Bees model manifest and every referenced weight file.

The input files are never modified. The output directory must not exist;
receipt.json is written last, so partial copies cannot be mistaken for a
complete portable model package.
"""

import argparse
import hashlib
import json
import os
from pathlib import Path


def digest_file(path):
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for chunk in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def write_json(path, document):
    with path.open("x", encoding="utf-8", newline="\n") as output:
        json.dump(document, output, indent=2)
        output.write("\n")
        output.flush()
        os.fsync(output.fileno())


def sync_directory(path):
    if os.name == "posix":
        fd = os.open(path, os.O_RDONLY | getattr(os, "O_DIRECTORY", 0))
        try:
            os.fsync(fd)
        finally:
            os.close(fd)


def export_model(source_manifest, destination):
    source_manifest = source_manifest.resolve(strict=True)
    content = source_manifest.read_bytes()
    if len(content) > 1024 * 1024:
        raise ValueError("native model manifest exceeds the 1 MiB protocol limit")
    manifest = json.loads(content)
    signature = manifest.get("policy_signature") if isinstance(manifest, dict) else None
    if (not isinstance(manifest, dict) or
            manifest.get("format") != "bees-native-model-v1" or
            type(manifest.get("abi_version")) is not int or manifest["abi_version"] != 26 or
            not isinstance(signature, str) or not 0 < len(signature.encode("utf-8")) <= 8192 or
            not isinstance(manifest.get("models"), list) or not manifest["models"]):
        raise ValueError("input is not a native v26 policy manifest")
    planned, slots = [], set()
    for model in manifest["models"]:
        if not isinstance(model, dict):
            raise ValueError("invalid model entry")
        slot, backend, filename, sha = (
            model.get(key) for key in ("slot", "backend", "path", "sha256")
        )
        if (type(slot) is not int or not 0 <= slot < (1 << 32) or slot in slots or
                backend not in ("mlagents", "torchscript", "onnx") or
                not isinstance(filename, str) or not filename or
                not isinstance(sha, str) or len(sha) != 64 or
                any(c not in "0123456789abcdef" for c in sha)):
            raise ValueError("invalid model slot or artifact digest")
        slots.add(slot)
        original = (source_manifest.parent / filename).resolve(strict=True)
        if not original.is_file():
            raise ValueError("model artifact is not a file")
        suffix = ".onnx" if backend == "onnx" else ".pt"
        relative = Path("models") / (sha + suffix)
        planned.append((model, original, sha, relative))
    if destination.exists():
        raise FileExistsError("portable model package destination already exists")
    destination.mkdir()
    (destination / "models").mkdir()
    written = set()
    for model, source, expected, relative in planned:
        output = destination / relative
        if relative not in written:
            actual = hashlib.sha256()
            with source.open("rb") as stream, output.open("xb") as sink:
                for chunk in iter(lambda: stream.read(1024 * 1024), b""):
                    actual.update(chunk)
                    sink.write(chunk)
                sink.flush()
                os.fsync(sink.fileno())
            if actual.hexdigest() != expected:
                raise ValueError(
                    "source artifact changed or disagrees with its manifest: " + str(source)
                )
            written.add(relative)
        model["path"] = relative.as_posix()
    manifest_path = destination / "manifest.json"
    write_json(manifest_path, manifest)
    receipt = {
        "schema_version": 1,
        "kind": "portable_bees_model_v1",
        "input_manifest_sha256": hashlib.sha256(content).hexdigest(),
        "manifest_sha256": digest_file(manifest_path),
        "model_files": len(written),
        "model_slots": len(planned),
    }
    sync_directory(destination / "models")
    write_json(destination / "receipt.json", receipt)
    sync_directory(destination)
    sync_directory(destination.parent)
    return receipt



def verify_package(package, learner_slot=None):
    """Verify a copied package without reading its original source paths.

    This proves the portable directory's internal hash consistency; the
    receipt is not a signature or a claim of trusted model provenance.
    """
    if learner_slot is not None and (type(learner_slot) is not int or not 0 <= learner_slot < (1 << 32)):
        raise ValueError("learner slot must be a native uint32 model slot")
    if package.is_symlink():
        raise ValueError("portable package root cannot be a symlink")
    package = package.resolve(strict=True)
    if not package.is_dir():
        raise ValueError("portable package must be a directory")
    manifest_file = package / "manifest.json"
    receipt_file = package / "receipt.json"
    models_dir = package / "models"
    if (models_dir.is_symlink() or not models_dir.is_dir() or
            manifest_file.is_symlink() or not manifest_file.is_file() or
            receipt_file.is_symlink() or not receipt_file.is_file()):
        raise ValueError("portable package is missing its expected regular files")
    if not 0 < manifest_file.stat().st_size <= 1024 * 1024:
        raise ValueError("portable manifest size is invalid")
    if not 0 < receipt_file.stat().st_size <= 65536:
        raise ValueError("portable receipt size is invalid")
    manifest_bytes = manifest_file.read_bytes()
    receipt = json.loads(receipt_file.read_bytes())
    manifest = json.loads(manifest_bytes)
    signature = manifest.get("policy_signature") if isinstance(manifest, dict) else None
    if (not isinstance(manifest, dict) or
            manifest.get("format") != "bees-native-model-v1" or
            type(manifest.get("abi_version")) is not int or manifest["abi_version"] != 26 or
            not isinstance(signature, str) or not 0 < len(signature.encode("utf-8")) <= 8192 or
            not isinstance(manifest.get("models"), list) or not manifest["models"]):
        raise ValueError("invalid portable native model manifest")
    if (not isinstance(receipt, dict) or receipt.get("schema_version") != 1 or
            receipt.get("kind") != "portable_bees_model_v1" or
            receipt.get("manifest_sha256") != hashlib.sha256(manifest_bytes).hexdigest() or
            not isinstance(receipt.get("input_manifest_sha256"), str) or
            len(receipt["input_manifest_sha256"]) != 64 or
            any(c not in "0123456789abcdef" for c in receipt["input_manifest_sha256"]) or
            type(receipt.get("model_slots")) is not int or
            type(receipt.get("model_files")) is not int):
        raise ValueError("portable model completion receipt is invalid")
    slots, unique_files = set(), {}
    selected_learner = None
    for model in manifest["models"]:
        if not isinstance(model, dict):
            raise ValueError("invalid portable model entry")
        slot, backend, relative, sha = (
            model.get(key) for key in ("slot", "backend", "path", "sha256")
        )
        if (type(slot) is not int or not 0 <= slot < (1 << 32) or slot in slots or
                backend not in ("mlagents", "torchscript", "onnx") or
                not isinstance(sha, str) or len(sha) != 64 or
                any(c not in "0123456789abcdef" for c in sha)):
            raise ValueError("invalid portable model slot or digest")
        suffix = ".onnx" if backend == "onnx" else ".pt"
        expected = f"models/{sha}{suffix}"
        if relative != expected:
            raise ValueError("model path escapes the verified portable package layout")
        slots.add(slot)
        unique_files[expected] = sha
        if slot == learner_slot:
            if backend != "mlagents":
                raise ValueError("selected learner must be an ML-Agents model")
            selected_learner = expected
    if (len(slots) != receipt["model_slots"] or
            len(unique_files) != receipt["model_files"]):
        raise ValueError("portable model file counts differ from the completion receipt")
    for relative, expected_sha in unique_files.items():
        file = package / relative
        if file.is_symlink() or not file.is_file() or digest_file(file) != expected_sha:
            raise ValueError("portable model artifact differs from its declared digest")
    # The model directory contains only the files referenced by the
    # manifest. It must not conceal stale or substituted model artifacts.
    if {item.name for item in models_dir.iterdir()} != {
        Path(relative).name for relative in unique_files
    }:
        raise ValueError("portable model directory contains unexpected artifacts")
    if learner_slot is not None and selected_learner is None:
        raise ValueError("selected learner slot is not in the portable manifest")
    result = {
        "kind": "portable_bees_model_verified",
        "manifest_sha256": receipt["manifest_sha256"],
        "model_files": len(unique_files),
        "model_slots": len(slots),
    }
    if selected_learner is not None:
        result.update(
            learner_slot=learner_slot,
            manifest=str(manifest_file),
            learner_checkpoint=str(package / selected_learner),
        )
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("manifest", type=Path,
                        help="source manifest, or package directory with --verify")
    parser.add_argument("output", type=Path, nargs="?",
                        help="new destination directory (must not exist)")
    parser.add_argument("--verify", action="store_true",
                        help="verify a transferred package without exporting files")
    parser.add_argument("--learner-slot", type=int,
                        help="with --verify, select the ML-Agents learner checkpoint")
    args = parser.parse_args()
    if args.verify:
        if args.output is not None:
            parser.error("--verify accepts only the package directory")
        result = verify_package(args.manifest, args.learner_slot)
    else:
        if args.output is None or args.learner_slot is not None:
            parser.error("export requires a destination and does not accept --learner-slot")
        result = export_model(args.manifest, args.output)
    print(json.dumps(result), flush=True)


if __name__ == "__main__":
    main()
