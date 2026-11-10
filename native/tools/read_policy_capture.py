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
        if _read_exact(stream, 40) != b"BEESACT1" + journal_id.encode("ascii"):
            raise ValueError("policy capture header does not match reward journal")
        end = stream.seek(0, 2)
        stream.seek(40)
        total = 0
        # Check all frame boundaries before exposing even the first batch.
        while True:
            rows, generation_or_total = FRAME.unpack(_read_exact(stream, FRAME.size))
            if rows == 0:
                if generation_or_total != total or stream.tell() != end:
                    raise ValueError("invalid policy capture footer")
                break
            size = rows * ROW.itemsize
            if rows > 65536 or stream.tell() + size + FRAME.size > end:
                raise ValueError("invalid or incomplete policy capture frame")
            stream.seek(size, 1)
            total += rows
        stream.seek(40)
        while total:
            rows, generation = FRAME.unpack(_read_exact(stream, FRAME.size))
            if not 0 < rows <= min(total, 65536):
                raise ValueError("policy capture changed while reading")
            yield generation, np.frombuffer(_read_exact(stream, rows * ROW.itemsize), dtype=ROW)
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
