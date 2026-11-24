"""Bounded, observational learner and optimizer samples for the native Hub.

The learner publishes only when a native Hub supervisor provides its private
job root. Samples never authorize checkpoints, stopping or resource release.
"""
from __future__ import annotations

import json
import math
import os
from pathlib import Path
import re
import tempfile
import time

ROOT_ENV = "BEES_HUB_LIVE_METRICS_ROOT"
JOB_ENV = "BEES_HUB_LIVE_METRICS_JOB_ID"
UUID = re.compile(r"[a-f0-9]{8}(?:-[a-f0-9]{4}){3}-[a-f0-9]{12}")
_last_step_sample = 0.0
_update_count = 0
_update_seconds = 0.0
_minibatches = 0
_failed = False


def configured() -> bool:
    return bool(os.environ.get(ROOT_ENV) and os.environ.get(JOB_ENV))


def learner_sample_due() -> bool:
    return configured() and time.monotonic() - _last_step_sample >= 2.0


def _publish(name: str, values: dict, now: float) -> None:
    global _failed
    root = Path(os.environ.get(ROOT_ENV, ""))
    job = os.environ.get(JOB_ENV, "")
    if not root.is_absolute() or root.name != job or not UUID.fullmatch(job):
        return
    temporary = None
    try:
        if root.resolve() != root or not root.is_dir():
            return
        fd, filename = tempfile.mkstemp(prefix="." + name + "-", dir=root)
        temporary = Path(filename)
        with os.fdopen(fd, "w", encoding="utf-8") as stream:
            json.dump({"schema_version": 1, "job_id": job,
                       "sampled_monotonic": now, **values},
                      stream, separators=(",", ":"), allow_nan=False)
        # This is replaceable observational telemetry, not a durable receipt.
        os.replace(temporary, root / (name + ".json"))
    except (OSError, ValueError) as error:
        if not _failed:
            print("[Bees Hub metrics] Sample unavailable: " + str(error),
                  flush=True)
        _failed = True
    else:
        _failed = False
    finally:
        if temporary is not None:
            temporary.unlink(missing_ok=True)


def publish_learner_step(step: int) -> None:
    global _last_step_sample
    if type(step) is not int or step < 0:
        return
    now = time.monotonic()
    if now - _last_step_sample < 2.0:
        return
    _last_step_sample = now
    _publish("learner-live", {"step": step}, now)


def publish_ppo_update(seconds: float, materialize_seconds: float,
                       minibatches: int, experiences: int) -> None:
    global _update_count, _update_seconds, _minibatches
    if not configured():
        return
    if (not math.isfinite(seconds) or seconds <= 0 or
            not math.isfinite(materialize_seconds) or materialize_seconds < 0 or
            type(minibatches) is not int or minibatches < 1 or
            type(experiences) is not int or experiences < 1):
        return
    _update_count += 1
    _update_seconds += seconds
    _minibatches += minibatches
    _publish("ppo-live", {
        "updates": _update_count, "total_seconds": _update_seconds,
        "total_minibatches": _minibatches, "update_seconds": seconds,
        "materialize_seconds": materialize_seconds,
        "minibatches": minibatches, "experiences": experiences,
    }, time.monotonic())
