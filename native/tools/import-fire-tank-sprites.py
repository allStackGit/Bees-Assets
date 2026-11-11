"""Pack the Fire Tank prefab's body, smoke, and debris sprite slices."""

from itertools import groupby
import re
import struct
from pathlib import Path

from PIL import Image


root = Path(__file__).resolve().parents[2]
prefab = (root / "Prefabs/Entities/Objects/Fire Tank.prefab").read_text(encoding="utf-8")


def read_sheet(relative):
    sheet = root / relative
    metadata = sheet.with_suffix(".png.meta").read_text(encoding="utf-8")
    guid = re.search(r"^guid: (\w+)$", metadata, re.MULTILINE)[1]
    pixels_per_unit = int(re.search(r"^  spritePixelsToUnits: (\d+)$", metadata, re.MULTILINE)[1])
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
    with Image.open(sheet) as image:
        rgba = image.convert("RGBA")
    return guid, (pixels_per_unit, rectangles, rgba)


def pack(field, next_field, sheets, target):
    sources = dict(read_sheet(sheet) for sheet in sheets)
    references = prefab.split(f"  {field}:\n", 1)[1].split(f"\n  {next_field}:", 1)[0]
    sprites = re.findall(r"fileID: (-?\d+), guid: (\w+),", references)
    if not sprites or len(sprites) != len(references.splitlines()):
        raise ValueError(f"Invalid Fire Tank sprite references in {field}")
    (pixels_per_unit,) = {ppu for ppu, _, _ in sources.values()}
    data = bytearray(struct.pack("<4sHH", b"BDS2", pixels_per_unit, len(sprites)))
    for sprite_id, guid in sprites:
        _, rectangles, rgba = sources[guid]
        x, y, width, height = rectangles[int(sprite_id)]
        top = rgba.height - y - height
        if not (width and height and top >= 0 and x + width <= rgba.width):
            raise ValueError(f"Invalid source sprite rectangle for {sprite_id}")
        data.extend(struct.pack("<HH", width, height))
        pixels = struct.iter_unpack("<4B", rgba.crop((x, top, x + width, top + height)).tobytes())
        for color, run in groupby(pixels):
            count = sum(1 for _ in run)
            while count:
                length = min(count, 65535)
                data.extend(struct.pack("<H4B", length, *color))
                count -= length
    (root / "native/assets" / target).write_bytes(data)


pack("Sprites", "Level", ["Sprites/Objects/fire_tank_red.png", "Sprites/Objects/fire_tank_grey.png"],
     "fire-tank-body.rgba")
pack("SmokeSprites", "SmokePositions", ["Sprites/Objects/smoke_puff.png"], "fire-tank-smoke.rgba")
pack("ObstacleDebrisSprites", "ObstacleDebrisCount", ["Sprites/Obstacles/scrap_bits.png"],
     "fire-tank-debris.rgba")
