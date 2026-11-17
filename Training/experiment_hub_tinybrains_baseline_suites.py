#!/usr/bin/env python3
"""Create immutable two-seat TinyBrains Nano/Micro benchmark suite definitions."""

import argparse
import hashlib
import json
import os
from pathlib import Path

from experiment_hub_eval_io import suite_digest
from experiment_hub_evaluation_runtime import describe


def digest(file):
    with file.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--out-dir", required=True, type=Path)
    parser.add_argument("--seed-start", type=int, default=813001)
    parser.add_argument("--seeds", type=int, default=50)
    parser.add_argument("--max-turns", type=int, default=300)
    parser.add_argument("--map", default="basic-tiny-2p")
    args = parser.parse_args()

    if not 1 <= args.seeds <= 128 or not 0 <= args.seed_start < 2**32 \
            or args.seed_start + args.seeds > 2**32:
        parser.error("Use 1–128 valid distinct uint32 seeds.")
    if not 1 <= args.max_turns <= 1000:
        parser.error("--max-turns must be between 1 and 1000.")
    if args.map != "basic-tiny-2p":
        parser.error("This first-stage benchmark is intentionally restricted to basic-tiny-2p.")

    # The pinned method includes the installed game, Python packages, and
    # adapter/export code. The selected opponent's two files are pinned too.
    from ants_mapoca.runtime import STARTER

    method_sha256 = suite_digest(describe("tinybrains"))
    output = args.out_dir.expanduser().resolve()
    output.mkdir(parents=True, exist_ok=True, mode=0o700)
    for opponent in ("nano", "micro"):
        source = STARTER / "models" / f"{opponent}-bc"
        suite = {
            "suite_id": f"tinybrains_{opponent}_tiny_v1",
            "maps": [args.map],
            "seeds": list(range(args.seed_start, args.seed_start + args.seeds)),
            "max_turns": args.max_turns,
            "opponent": opponent,
            "opponent_model_sha256": digest(source / "model.onnx"),
            "opponent_manifest_sha256": digest(source / "manifest.json"),
            "runtime_sha256": method_sha256,
        }
        destination = output / (suite["suite_id"] + ".json")
        body = json.dumps(suite, sort_keys=True, indent=2) + "\n"
        # A saved suite is an immutable experiment definition. Never silently
        # replace it when the method, opponent, maps, or seeds change.
        if destination.exists():
            if destination.read_text(encoding="utf-8") != body:
                raise ValueError(f"Existing suite differs: {destination}. Use a new suite ID.")
        else:
            fd = os.open(destination, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
            with os.fdopen(fd, "w", encoding="utf-8") as stream:
                stream.write(body)
        print(f"{destination}: {2 * args.seeds} balanced matches; SHA-256 {suite_digest(suite)}")


if __name__ == "__main__":
    main()
