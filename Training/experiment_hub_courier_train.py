#!/usr/bin/env python3
"""Run Courier with optional policy export at its checkpoint cache boundary."""

import _thread
import json
import os
from pathlib import Path
import runpy
import shutil
import signal
import sys
import tempfile
import threading

from experiment_hub_eval_io import UUID, read_bounded


def publish_policy(cache, inbox):
    checkpoint = json.loads(read_bounded(cache / "checkpoint.json", 256 * 1024))
    number = checkpoint["round"]
    if type(number) is not int or not 1 <= number < 10**15:
        return
    destination = inbox / f"round-{number}.zip"
    if destination.exists():
        return  # The trainer may publish the same round again on shutdown.
    # Keep the most recent policy while replacing its predecessor. On
    # Windows an evaluator can hold that predecessor open. If so, skip this
    # export and allow the trainer's durable checkpoint to stand.
    previous = sorted(inbox.glob("round-*.zip"), key=lambda file: int(file.stem[6:]))
    for stale in previous[:-1]:
        stale.unlink()
    fd, pending = tempfile.mkstemp(prefix=".policy-", suffix=".tmp", dir=inbox)
    try:
        with os.fdopen(fd, "wb") as output, (cache / "lineage-0.zip").open("rb") as source:
            shutil.copyfileobj(source, output)
            output.flush()
            os.fsync(output.fileno())
        os.replace(pending, destination)
    finally:
        Path(pending).unlink(missing_ok=True)


def install_snapshot_publisher(root, dispatch):
    if not UUID.fullmatch(dispatch):
        raise ValueError("Invalid Courier snapshot dispatch ID.")
    root = Path(root)
    if root.resolve(strict=True) != root:
        raise ValueError("Courier snapshot root was redirected.")
    inbox = root / dispatch
    inbox.mkdir()

    import checkpoint_cache

    # The standalone trainer imports this cache class before starting its
    # coordinator. Wrap only its publisher in this dedicated process.
    class EvaluationCheckpointCache(checkpoint_cache.CheckpointCache):
        def publish(self, directory, names):
            super().publish(directory, names)
            try:
                publish_policy(self.path, inbox)
            except (OSError, ValueError, KeyError) as error:
                print(f"Courier evaluation snapshot skipped: {error}", file=sys.stderr)

    checkpoint_cache.CheckpointCache = EvaluationCheckpointCache


def watch_stop(path, done):
    while not done.wait(0.25):
        handler = signal.getsignal(signal.SIGINT)
        if callable(handler) and handler is not signal.default_int_handler and path.is_file():
            # An OS SIGINT interrupts Windows multiprocessing pipe waits.
            # Queue the Python handler without signaling the kernel wait.
            _thread.interrupt_main(signal.SIGINT)
            return


def main():
    source, root, dispatch, stop = sys.argv[1:5]
    script = Path(source).resolve(strict=True)
    sys.path.insert(0, str(script.parent))
    if root != '-':
        install_snapshot_publisher(root, dispatch)
    done = threading.Event()
    if stop != '-':
        threading.Thread(target=watch_stop, args=(Path(stop), done), daemon=True).start()
    sys.argv = [str(script), *sys.argv[5:]]
    try:
        runpy.run_path(str(script), run_name="__main__")
    finally:
        done.set()


if __name__ == "__main__":
    main()
