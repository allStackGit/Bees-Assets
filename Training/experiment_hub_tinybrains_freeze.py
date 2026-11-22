#!/usr/bin/env python3
"""Export an immutable Mini-class TinyBrains opponent from a finished checkpoint."""

import argparse
import hashlib
import json
from pathlib import Path

from experiment_hub_eval_io import SHA256, suite_digest
from experiment_hub_evaluation_runtime import describe
from experiment_hub_tinybrains_runtime import configure_engine


def digest(file):
    with file.open("rb") as source:
        return hashlib.file_digest(source, "sha256").hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--checkpoint", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--expected-sha256", required=True)
    parser.add_argument("--engine-path", type=Path, required=True)
    args = parser.parse_args()

    if not SHA256.fullmatch(args.expected_sha256):
        raise ValueError("Expected a pinned checkpoint SHA-256.")
    checkpoint = args.checkpoint.resolve(strict=True)
    output = args.output.resolve(strict=True)
    if (not checkpoint.is_file() or checkpoint.is_symlink()
            or not output.is_dir() or output.is_relative_to(checkpoint.parent)
            or tuple(output.iterdir())):
        raise ValueError("Require a regular checkpoint and empty private staging directory.")
    if digest(checkpoint) != args.expected_sha256:
        raise ValueError("Frozen opponent checkpoint differs from the finished Hub job.")

    configure_engine(args.engine_path)
    from ants_mapoca.artifacts import export
    model_dir = output / "export"
    metrics = export(checkpoint, model_dir)
    check = metrics["check"]
    bytes_used = check["size_metric_bytes"]
    if type(bytes_used) is not int or not 0 < bytes_used <= 1024 * 1024:
        raise ValueError("The exported opponent exceeds TinyBrains Mini (1 MiB) size limit.")
    if digest(checkpoint) != args.expected_sha256:
        raise ValueError("Source checkpoint changed during model export.")
    print(json.dumps({
        "checkpoint_sha256": args.expected_sha256,
        "model_sha256": digest(model_dir / "model.onnx"),
        "manifest_sha256": digest(model_dir / "manifest.json"),
        "model_size_metric_bytes": bytes_used,
        "runtime_sha256": suite_digest(describe("tinybrains")),
    }, sort_keys=True))


if __name__ == "__main__":
    main()
