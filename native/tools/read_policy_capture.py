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
BEHAVIOR_ROW = np.dtype(ROW.descr + [
    ("sampled_continuous", "<f4", (16,)),
    ("continuous_log_probs", "<f4", (16,)),
    ("discrete_log_probs", "<f4", (6,)),
])


def _frame_layout(field, has_behavior_version):
    if has_behavior_version and field & BEHAVIOR_FLAG:
        return field ^ BEHAVIOR_FLAG, BEHAVIOR_ROW
    return field, ROW


def _read_exact(stream, count):
    payload = stream.read(count)
    if len(payload) != count:
        raise ValueError("truncated policy capture")
    return payload


def applied_batches(path, journal_id):
    """Yield (generation, structured row array) from a completed capture.

    Pass the paired reward journal's ID. Group-agent ID zero means absent.
    The tick is the observation tick, so the action advances tick + 1.
    """
    with path.open("rb") as stream:
        header = _read_exact(stream, 40)
        if header[:8] not in (b"BEESACT1", b"BEESACT2") or header[8:] != journal_id.encode("ascii"):
            raise ValueError("policy capture header does not match reward journal")
        has_behavior_version = header[:8] == b"BEESACT2"
        end = stream.seek(0, 2)
        stream.seek(40)
        total = 0
        # Check all frame boundaries before exposing even the first batch.
        while True:
            field, generation_or_total = FRAME.unpack(_read_exact(stream, FRAME.size))
            if field == 0:
                if generation_or_total != total or stream.tell() != end:
                    raise ValueError("invalid policy capture footer")
                break
            rows, dtype = _frame_layout(field, has_behavior_version)
            size = rows * dtype.itemsize
            if not 0 < rows <= 65536 or stream.tell() + size + FRAME.size > end:
                raise ValueError("invalid or incomplete policy capture frame")
            stream.seek(size, 1)
            total += rows
        stream.seek(40)
        while total:
            field, generation = FRAME.unpack(_read_exact(stream, FRAME.size))
            rows, dtype = _frame_layout(field, has_behavior_version)
            if not 0 < rows <= min(total, 65536):
                raise ValueError("policy capture changed while reading")
            yield generation, np.frombuffer(_read_exact(stream, rows * dtype.itemsize), dtype=dtype)
            total -= rows


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
