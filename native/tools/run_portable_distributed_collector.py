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


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--input-archive", required=True, type=Path)
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
    args = parser.parse_args()
    if not (args.upload_url or args.archive_output or args.bundle_inbox):
        parser.error("supply --upload-url, --archive-output or --bundle-inbox")
    scratch = args.scratch.resolve(strict=True)
    if not scratch.is_dir() or scratch.is_symlink():
        parser.error("--scratch must be an existing non-symlink directory")
    binary = args.rollout_binary.resolve(strict=True)
    if not binary.is_file():
        parser.error("--rollout-binary must be a compiled native rollout executable")
    if args.bundle_inbox is not None and not args.bundle_inbox.is_dir():
        parser.error("--bundle-inbox must be an existing directory")

    started = time.perf_counter()
    attempt = Path(tempfile.mkdtemp(prefix="native-portable-collector-", dir=scratch))
    inputs = attempt / "inputs"
    try:
        imported = import_inputs(args.input_archive, inputs,
                                 args.policy_worker, args.python)
        print(json.dumps(imported, sort_keys=True), flush=True)
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
            *([] if args.upload_url is None else ["--upload-url", args.upload_url]),
            *([] if not args.stage_diagnostics else ["--stage-diagnostics"]),
        ]
        subprocess.run(command, check=True)
        # Native collection receipt and uploaded archive are now durable.
        # The input checkpoint copy is a disposable staging artifact only.
        shutil.rmtree(attempt)
        print(json.dumps({
            "kind": "native_portable_collector_completed",
            "collector_id": imported["collector_id"],
            "input_bytes": imported["source_bytes"],
            "elapsed_wall_seconds": time.perf_counter() - started,
            "input_archive": str(args.input_archive.resolve()),
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
