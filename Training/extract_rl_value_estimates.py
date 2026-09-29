#!/usr/bin/env python3
"""Summarize the ML-Agents critic value estimate history from TensorBoard events."""

from __future__ import annotations

import argparse
from pathlib import Path
from typing import Iterable, Sequence

DEFAULT_RUN_DIR = Path("results/bees-full-001/BeesRL1v1")
DEFAULT_TAG = "Policy/Extrinsic Value Estimate"
DEFAULT_INTERVAL = 100_000
DEFAULT_THRESHOLDS = (20, 50, 100, 500, 1_000, 5_000, 10_000)


def parse_args(argv: Sequence[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description=(
            "Extract Policy/Extrinsic Value Estimate from all TensorBoard event "
            "files in an ML-Agents behavior results directory."
        )
    )
    parser.add_argument(
        "run_dir",
        nargs="?",
        type=Path,
        default=DEFAULT_RUN_DIR,
        help=(
            "Behavior results directory containing events.out.tfevents.* "
            f"(default: {DEFAULT_RUN_DIR})"
        ),
    )
    parser.add_argument(
        "--tag",
        default=DEFAULT_TAG,
        help=f"TensorBoard scalar tag to extract (default: {DEFAULT_TAG!r})",
    )
    parser.add_argument(
        "--interval",
        type=int,
        default=DEFAULT_INTERVAL,
        help=(
            "Approximate step interval for the compact history "
            f"(default: {DEFAULT_INTERVAL})"
        ),
    )
    return parser.parse_args(argv)


def _event_accumulator_type():
    try:
        from tensorboard.backend.event_processing.event_accumulator import EventAccumulator
    except ImportError as exc:
        raise SystemExit(
            "TensorBoard is not installed in this Python environment. "
            "Activate the Bees ML-Agents virtual environment and try again."
        ) from exc
    return EventAccumulator


def load_points(run_dir: Path, tag: str) -> tuple[list[tuple[int, float, float]], list[str]]:
    """Return deduplicated (step, wall_time, value) points and skipped-file messages."""
    event_files = sorted(run_dir.glob("events.out.tfevents.*"))
    if not event_files:
        raise SystemExit(f"No TensorBoard event files found in: {run_dir}")

    EventAccumulator = _event_accumulator_type()
    points: list[tuple[int, float, float]] = []
    skipped: list[str] = []

    for path in event_files:
        try:
            accumulator = EventAccumulator(str(path), size_guidance={"scalars": 0})
            accumulator.Reload()
            scalar_tags = accumulator.Tags().get("scalars", [])
            if tag not in scalar_tags:
                continue
            points.extend(
                (event.step, event.wall_time, event.value)
                for event in accumulator.Scalars(tag)
            )
        except Exception as exc:  # One stale/corrupt event file should not hide the rest.
            skipped.append(f"SKIP {path.name}: {exc}")

    if not points:
        raise SystemExit(f"No scalar values found for tag {tag!r} in: {run_dir}")

    latest_by_step: dict[int, tuple[int, float, float]] = {}
    for point in points:
        step = point[0]
        previous = latest_by_step.get(step)
        if previous is None or point[1] > previous[1]:
            latest_by_step[step] = point

    return [latest_by_step[step] for step in sorted(latest_by_step)], skipped


def compact_history(
    data: Iterable[tuple[int, float, float]], interval: int
) -> list[tuple[int, float, float]]:
    if interval <= 0:
        raise ValueError("interval must be greater than zero")

    selected: list[tuple[int, float, float]] = []
    next_step = 0
    for point in data:
        if point[0] >= next_step:
            selected.append(point)
            next_step = point[0] + interval
    return selected


def print_report(
    run_dir: Path,
    tag: str,
    data: list[tuple[int, float, float]],
    skipped: Iterable[str],
    interval: int,
) -> None:
    print(f"Run directory: {run_dir}")
    print(f"Found {len(data)} unique value-estimate samples")
    print(f"Tag: {tag}")

    skipped = list(skipped)
    if skipped:
        print()
        for message in skipped:
            print(message)

    print()
    print(f"Approx. {interval:,}-step history:")
    for step, _wall_time, value in compact_history(data, interval):
        print(f"{step:9d}  value={value:14.3f}")

    print()
    for threshold in DEFAULT_THRESHOLDS:
        hit = next((point for point in data if abs(point[2]) >= threshold), None)
        if hit is None:
            print(f"First |value| >= {threshold:5}: never")
        else:
            print(
                f"First |value| >= {threshold:5}: "
                f"step={hit[0]:9d} value={hit[2]:14.3f}"
            )

    worst = max(data, key=lambda point: abs(point[2]))
    latest = data[-1]
    print()
    print(f"Latest value: step={latest[0]} value={latest[2]:.3f}")
    print(f"Largest |value|: step={worst[0]} value={worst[2]:.3f}")


def main(argv: Sequence[str] | None = None) -> int:
    args = parse_args(argv)
    run_dir = args.run_dir.expanduser()
    data, skipped = load_points(run_dir, args.tag)
    print_report(run_dir, args.tag, data, skipped, args.interval)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
