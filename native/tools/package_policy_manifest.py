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


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("manifest", type=Path, help="completed native model manifest")
    parser.add_argument("output", type=Path, help="new destination directory (must not exist)")
    args = parser.parse_args()
    print(json.dumps(export_model(args.manifest, args.output)), flush=True)


if __name__ == "__main__":
    main()
