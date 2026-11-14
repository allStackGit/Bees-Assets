"""Convert original Bees ship and projectile art to portable native sprite atlases.

Pillow is used only during asset preparation. The compiled game and all
native training/evaluation processes run without Python, PNG decoding or Unity.
"""

import argparse
from itertools import groupby
from pathlib import Path
import os
import re
import struct
from tempfile import NamedTemporaryFile

from PIL import Image

ROOT = Path(__file__).resolve().parents[2]

# Zero-based ConfigData.ShipTypes enum IDs, not Stage ItemIds. Queen is
# animated on a 6400x5120 sheet and requires independent animation porting.
# HumanTarget is intentionally blank; both retain physical hull rendering.
SHIP_IMAGES = (
    "barge.png", "Beacon Standard.png", "beehive.png",
    "bumblebee_bowless.png", "carpenter.png", "carrier.png",
    "cruiser.png", "dreadnought.png", "drone.png", "factory.png",
    "firebarge.png", "flagship.png", "frigate.png", "gunship.png",
    "honeybee.png", "hornet.png", "leafcutter.png", None,
    "scout.png", "striker.png", "warpgate.png", "wasp.png",
    "yellowjacket.png", None,
)

# ConfigData.ProjectileTypes. Some explosion/beam visuals require custom
# source timing or geometry; leave their authoritative collider outlines
# until those presentations can be supported without inventing physics.
PROJECTILE_IMAGES = (
    None, "Bee Small Laser Shot.png", None, "Bumblebee Laser Shot.png",
    "flagship_bolt.png", "Frigate Rocket In Flight.png",
    "Human Small Laser Shot.png", None, None,
    "Leafcutter Shot.png", "Queen Small Laser Shot.png",
    "Queen Stinger Laser Shot.png", "Striker Bomb.png",
    "Frigate Rocket Explosion.png", "Fireship Explosion.png", None,
)


def source_frames(path: Path, animation: bool):
    metadata = path.with_suffix(".png.meta").read_text(encoding="utf-8")
    source_ppu = re.search(r"^  spritePixelsToUnits: (\d+)$", metadata, re.MULTILINE)
    if source_ppu is None or not (1 <= int(source_ppu[1]) <= 65535):
        raise ValueError(f"Missing valid Unity pixels-per-unit: {path}")
    rects = re.findall(
        r"^      rect:\n        serializedVersion: 2\n"
        r"        x: (\d+)\n        y: (\d+)\n"
        r"        width: (\d+)\n        height: (\d+)$",
        metadata, re.MULTILINE,
    )
    if len(rects) > (64 if animation else 1):
        raise ValueError(f"Too many sprite slices: {path}")
    with Image.open(path) as image:
        rgba = image.convert("RGBA")
    frames = []
    for x, y, width, height in (rects or [(0, 0, rgba.width, rgba.height)]):
        x, y, width, height = map(int, (x, y, width, height))
        top = rgba.height - y - height
        if x < 0 or top < 0 or width <= 0 or height <= 0 or (
            x + width > rgba.width or top + height > rgba.height
        ) or width > 2048 or height > 2048:
            raise ValueError(f"Out-of-range source sprite rect: {path}")
        frames.append(rgba.crop((x, top, x + width, top + height)))
    # Very large authored explosion sheets use 1024px frames. Preserve
    # world-space size exactly while avoiding 170MB of decoded frames on
    # older machines: reduce texture resolution and its PPU together.
    ppu = int(source_ppu[1])
    largest = max(max(frame.size) for frame in frames)
    divisor = 1
    if animation:
        while largest // divisor > 256 and ppu % (2 * divisor) == 0:
            divisor *= 2
    if divisor > 1:
        frames = [
            frame.resize((max(1, frame.width // divisor),
                          max(1, frame.height // divisor)),
                         Image.Resampling.BOX)
            for frame in frames
        ]
        ppu //= divisor
    return ppu, frames


def packed_pixels(data: bytearray, frame):
    for color, pixels in groupby(struct.iter_unpack("<4B", frame.tobytes())):
        count = sum(1 for _ in pixels)
        while count:
            run = min(count, 65535)
            data.extend(struct.pack("<H4B", run, *color))
            count -= run


def pack(images, directory, magic, animated):
    entries = [(kind, name) for kind, name in enumerate(images) if name]
    data = bytearray(struct.pack("<4sH", magic, len(entries)))
    for kind, name in entries:
        ppu, frames = source_frames(directory / name, animated)
        if animated:
            data.extend(struct.pack("<BHH", kind, ppu, len(frames)))
            for frame in frames:
                data.extend(struct.pack("<HH", frame.width, frame.height))
                packed_pixels(data, frame)
        else:
            frame = frames[0]
            data.extend(struct.pack("<BHHH", kind, ppu, frame.width, frame.height))
            packed_pixels(data, frame)
    return data, len(entries)


def write_atomic(output: Path, data: bytearray):
    output = output.resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    with NamedTemporaryFile(dir=output.parent, prefix=".bees-art-",
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
    return output


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--ship-output", type=Path,
                        default=ROOT / "native/assets/ship-sprites.rgba")
    parser.add_argument("--projectile-output", type=Path,
                        default=ROOT / "native/assets/projectile-sprites.rgba")
    args = parser.parse_args()
    ships, ship_count = pack(
        SHIP_IMAGES, ROOT / "Sprites/Ships", b"BSS1", False,
    )
    projectiles, projectile_count = pack(
        PROJECTILE_IMAGES, ROOT / "Sprites/Projectiles", b"BPS1", True,
    )
    ships_path = write_atomic(args.ship_output, ships)
    projectiles_path = write_atomic(args.projectile_output, projectiles)
    print(f"Ship sprites: {ship_count} types, {len(ships)} bytes at {ships_path}")
    print(f"Projectile sprites: {projectile_count} types, "
          f"{len(projectiles)} bytes at {projectiles_path}")


if __name__ == "__main__":
    main()
