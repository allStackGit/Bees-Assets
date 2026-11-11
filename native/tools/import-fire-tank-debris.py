"""Pack the Fire Tank prefab's Unity sprite slices as native RGBA textures."""

import re
import struct
from pathlib import Path

from PIL import Image


root = Path(__file__).resolve().parents[2]
sheet = root / "Sprites/Obstacles/scrap_bits.png"
metadata = sheet.with_suffix(".png.meta").read_text(encoding="utf-8")
prefab = (root / "Prefabs/Entities/Objects/Fire Tank.prefab").read_text(encoding="utf-8")
guid = re.search(r"^guid: (\w+)$", metadata, re.MULTILINE)[1]
pixels_per_unit = int(re.search(r"^  spritePixelsToUnits: (\d+)$", metadata, re.MULTILINE)[1])
references = prefab.split("  ObstacleDebrisSprites:\n", 1)[1].split("\n  ObstacleDebrisCount:", 1)[0]
sprite_ids = [int(value) for value in re.findall(rf"fileID: (-?\d+), guid: {guid},", references)]
if not sprite_ids or len(sprite_ids) != len(references.splitlines()):
    raise ValueError("Fire Tank references an unexpected debris sheet")

rectangles = {}
for sprite in metadata.split("    - serializedVersion: 2\n      name: ")[1:]:
    rectangle = re.search(
        r"^      rect:\n        serializedVersion: 2\n"
        r"        x: (\d+)\n        y: (\d+)\n        width: (\d+)\n        height: (\d+)$",
        sprite, re.MULTILINE,
    )
    internal_id = re.search(r"^      internalID: (-?\d+)$", sprite, re.MULTILINE)
    if rectangle and internal_id:
        rectangles[int(internal_id[1])] = tuple(map(int, rectangle.groups()))

data = bytearray(struct.pack("<4sHH", b"BDS1", pixels_per_unit, len(sprite_ids)))
with Image.open(sheet) as source:
    rgba = source.convert("RGBA")
    for sprite_id in sprite_ids:
        x, y, width, height = rectangles[sprite_id]
        top = rgba.height - y - height
        if not (width and height and top >= 0 and x + width <= rgba.width and y < rgba.height):
            raise ValueError(f"Invalid source sprite rectangle for {sprite_id}")
        data.extend(struct.pack("<HH", width, height))
        data.extend(rgba.crop((x, top, x + width, top + height)).tobytes())

(root / "native/assets/fire-tank-debris.rgba").write_bytes(data)
