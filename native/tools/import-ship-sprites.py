"""Pack source Bees ship artwork for the standalone native game renderer.

Run after checking out the repository; Pillow is needed only for this one-time
asset conversion, never for the native runtime, graphics client or PPO worker.
The output can be copied beside a packaged native executable in assets/.
"""

import argparse
from itertools import groupby
from pathlib import Path
import re
import struct
from tempfile import NamedTemporaryFile
import os

from PIL import Image

ROOT = Path(__file__).resolve().parents[2]
# ConfigData.ShipTypes enum order, not runtime Stage ItemIds. Queen is an
# animation sheet with no single canonical battle pose; the native renderer
# retains its physical outline instead of displaying the whole 6400x5120 sheet.
# HumanTarget intentionally has no artwork.
SHIP_IMAGES = (
    "barge.png", "Beacon Standard.png", "beehive.png",
    "bumblebee_bowless.png", "carpenter.png", "carrier.png",
    "cruiser.png", "dreadnought.png", "drone.png", "factory.png",
    "firebarge.png", "flagship.png", "frigate.png", "gunship.png",
    "honeybee.png", "hornet.png", "leafcutter.png", None,
    "scout.png", "striker.png", "warpgate.png", "wasp.png",
    "yellowjacket.png", None,
)


def source_sprite(path: Path):
    metadata = path.with_suffix(".png.meta").read_text(encoding="utf-8")
    ppu = re.search(r"^  spritePixelsToUnits: (\d+)$", metadata, re.MULTILINE)
    if ppu is None or not (1 <= int(ppu[1]) <= 65535):
        raise ValueError(f"Missing valid Unity pixels-per-unit for {path}")
    with Image.open(path) as image:
        rgba = image.convert("RGBA")
    # Unity's texture importer specifies rects bottom-up; Pillow's
    # image coordinates are top-down. Use the same selection as the
    # authored sprite, not an entire animation or padding sheet.
    rectangles = re.findall(
        r"^      rect:\n        serializedVersion: 2\n"
        r"        x: (\d+)\n        y: (\d+)\n"
        r"        width: (\d+)\n        height: (\d+)$",
        metadata, re.MULTILINE,
    )
    if len(rectangles) > 1:
        raise ValueError(f"Ambiguous multiple Unity sprite slices: {path}")
    if rectangles:
        x, y, width, height = map(int, rectangles[0])
        top = rgba.height - y - height
        if x < 0 or top < 0 or width <= 0 or height <= 0 or (
            x + width > rgba.width or top + height > rgba.height
        ):
            raise ValueError(f"Out-of-range source sprite slice: {path}")
        rgba = rgba.crop((x, top, x + width, top + height))
    if not (1 <= rgba.width <= 2048 and 1 <= rgba.height <= 2048):
        raise ValueError(f"Ship sprite exceeds supported raster size: {path}")
    return int(ppu[1]), rgba


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--output", type=Path, default=ROOT / "native/assets/ship-sprites.rgba",
        help="Portable BSS1 atlas output (defaults to native/assets/)",
    )
    args = parser.parse_args()
    entries = [(index, image) for index, image in enumerate(SHIP_IMAGES)
               if image is not None]
    data = bytearray(struct.pack("<4sH", b"BSS1", len(entries)))
    for ship_type, image in entries:
        ppu, rgba = source_sprite(ROOT / "Sprites/Ships" / image)
        data.extend(struct.pack("<BHHH", ship_type, ppu, rgba.width, rgba.height))
        for color, pixels in groupby(struct.iter_unpack("<4B", rgba.tobytes())):
            count = sum(1 for _ in pixels)
            while count:
                span = min(count, 65535)
                data.extend(struct.pack("<H4B", span, *color))
                count -= span

    output = args.output.resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    # Do not expose a partial atlas to a concurrent native client.
    with NamedTemporaryFile(dir=output.parent, prefix=".ship-sprites-",
                            suffix=".tmp", delete=False) as temporary:
        pending = Path(temporary.name)
        try:
            temporary.write(data)
            temporary.flush()
            os.fsync(temporary.fileno())
        except BaseException:
            pending.unlink(missing_ok=True)
            raise
    os.replace(pending, output)
    print(f"Packed {len(entries)} source ship types into {output} ({len(data)} bytes)")


if __name__ == "__main__":
    main()
