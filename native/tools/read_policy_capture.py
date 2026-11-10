"""Read completed native v26 decision batches alongside a reward journal."""

import argparse
import json
from pathlib import Path
import struct

import numpy as np


FRAME = struct.Struct("<IQ")
ROW = np.dtype([
    ("world", "<u8"), ("episode", "<u8"), ("tick", "<u8"), ("ship", "<u8"),
    ("binding_revision", "<u8"), ("group_agent", "<u8"), ("model_slot", "<u4"),
    ("team", "u1"), ("fire_allowed", "u1", (5,)), ("special_allowed", "u1", (5,)),
    ("observations", "<f4", (7743,)), ("continuous", "<f4", (16,)),
    ("discrete", "u1", (6,)),
])
BEHAVIOR_FLAG = 1 << 31
TERMINAL_FLAG = 1 << 30
TERMINAL_ROW = np.dtype(ROW.descr[:6] + [("observations", "<f4", (7743,))])
BEHAVIOR_ROW = np.dtype(ROW.descr + [
    ("sampled_continuous", "<f4", (16,)),
    ("continuous_log_probs", "<f4", (16,)),
    ("discrete_log_probs", "<f4", (6,)),
])


def _frame_layout(field, version):
    if version == b"BEESACT3" and field & TERMINAL_FLAG:
        return field ^ TERMINAL_FLAG, TERMINAL_ROW
    if version != b"BEESACT1" and field & BEHAVIOR_FLAG:
        return field ^ BEHAVIOR_FLAG, BEHAVIOR_ROW
    return field, ROW


def capture_frames(path, journal_id):
    """Yield (generation, rows), with generation None for terminal observations.

    Pass the paired reward journal's ID. Group-agent ID zero means absent.
    The tick is the observation tick, so the action advances tick + 1.
    """
    if path.stat().st_size < 40 + FRAME.size:
        raise ValueError("truncated policy capture")
    # Borrow immutable frame data from a file-backed mapping rather than
    # materializing a second potentially huge Python bytes buffer. The
    # learner still owns copies of the rows retained past each frame.
    mapped = np.memmap(path, dtype=np.uint8, mode="r")
    end = len(mapped)
    header = bytes(mapped[:40])
    version = header[:8]
    if version not in (b"BEESACT1", b"BEESACT2", b"BEESACT3") or header[8:] != journal_id.encode("ascii"):
        raise ValueError("policy capture header does not match reward journal")

    offset = 40
    total = 0
    # Check all frame boundaries and the terminal footer before exposing
    # even the first batch. Mapping the file does not relax this contract.
    while True:
        if offset + FRAME.size > end:
            raise ValueError("truncated policy capture")
        field, generation_or_total = FRAME.unpack_from(mapped, offset)
        offset += FRAME.size
        if field == 0:
            if generation_or_total != total or offset != end:
                raise ValueError("invalid policy capture footer")
            break
        rows, dtype = _frame_layout(field, version)
        size = rows * dtype.itemsize
        if not 0 < rows <= 65536 or offset + size + FRAME.size > end:
            raise ValueError("invalid or incomplete policy capture frame")
        offset += size
        if dtype != TERMINAL_ROW:
            total += rows

    footer = end - FRAME.size
    offset = 40
    while offset < footer:
        field, generation = FRAME.unpack_from(mapped, offset)
        offset += FRAME.size
        rows, dtype = _frame_layout(field, version)
        size = rows * dtype.itemsize
        if not 0 < rows <= 65536 or offset + size > footer:
            raise ValueError("policy capture changed while reading")
        yield (None if dtype == TERMINAL_ROW else generation,
               np.frombuffer(mapped, dtype=dtype, count=rows, offset=offset))
        offset += size


def applied_batches(path, journal_id):
    for generation, rows in capture_frames(path, journal_id):
        if generation is not None:
            yield generation, rows


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("capture", type=Path)
    parser.add_argument("journal", type=Path)
    args = parser.parse_args()
    try:
        with args.journal.open(encoding="utf-8") as stream:
            journal_id = json.loads(stream.readline())["journal_id"]
        counts = {}
        for generation, batch in applied_batches(args.capture, journal_id):
            counts[generation] = counts.get(generation, 0) + len(batch)
    except (KeyError, OSError, ValueError) as error:
        parser.error(str(error))
    print(json.dumps({"journal_id": journal_id, "rows_by_generation": counts}))


if __name__ == "__main__":
    main()
