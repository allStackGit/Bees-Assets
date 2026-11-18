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

# (ShipTypes ID, animation variant, sheet, authored Unity animation clip).
# Variant 0 is the working/opening animation, 1 is WarpGate's ready loop.
SHIP_ANIMATIONS = (
    (4, 0, "carpenter_anim.png", "Carpenter Bee Mining.anim"),
    (9, 0, "factory_anim.png", "Factory Mining.anim"),
    (20, 0, "warpgate_anim.png", "Warp Gate Opening.anim"),
    (20, 1, "warpgate_loop.png", "Warp Gate Loop.anim"),
)

# Original ShipRemains and Beehive Shrinking sheets. The tuple is keyed
# directly by ConfigData.ShipTypes; None denotes source classes without
# authored wreck art. The 20-slice clips hold their final frame until the
# source ShipRemains five-second Kill timer completes.
SHIP_REMAINS = (
    ("barge_remains.png", "Barge Remains.anim"), None,
    ("beehive_shrinking.png", "Beehive Shrinking.anim"),
    ("bumblebee_remains.png", None),
    ("carpenterbee_remains.png", None),
    ("carrier_remains.png", "Carrier Remains.anim"),
    ("cruiser_remains.png", "Cruiser Remains.anim"),
    ("dreadnought_remains.png", "Dreadnought Remains.anim"),
    ("drone_remains.png", "Drone Remains.anim"),
    ("factory_remains.png", "Factory Reamins.anim"),
    None,  # Fire Barge explodes rather than leaving a persistent corpse
    ("flagship_remains.png", "Flagship Remains.anim"),
    ("frigate_remains.png", "Frigate Remains.anim"),
    ("gunship_remains.png", "Gunship Remains.anim"),
    ("honeybee_remains.png", None),
    ("hornet_remains.png", None),
    ("leafcutter_remains.png", None),
    ("queen_remains.png", None),
    ("scout_remains.png", "Scout Remains.anim"),
    ("striker_remains.png", "Striker Remains.anim"),
    ("warpgate_remains.png", "Warp Gate Remains.anim"),
    ("wasp_remains.png", None),
    ("yellowjacket_remains.png", None),
    None,  # HumanTarget has no authored corpse
)

# Source CollisionAsteroidCatalog prefab numbers 1..28. Slot zero is
# unused, so native sprites retain the real authored asteroid identities.
ASTEROID_IMAGES = (None,) + tuple(f"Asteroid {n}.png" for n in range(1, 29))

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


def source_ship_color_palettes():
    """Read the original Unity recolor matching palette, not an invented tint.

    Like Utilities.GetChangablePixelsForImage, select alpha-positive pixels
    whose RGB squared distance from a ConfigData palette color is < .035².
    The native game never needs to parse C# or scan palettes per frame.
    """
    type_source = (ROOT / "Scripts/ConfigData.Types.cs").read_text(encoding="utf-8-sig")
    match = re.search(r"enum ShipTypes\s*\{([^}]+)\}", type_source, re.DOTALL)
    if match is None:
        raise ValueError("Source ConfigData.ShipTypes enumeration is missing")
    type_names = re.findall(r"^\s*([A-Za-z][A-Za-z0-9_]*)\s*,?\s*$",
                            match[1], re.MULTILINE)
    if len(type_names) != len(SHIP_IMAGES):
        raise ValueError("Ship type atlas does not match the source enumeration")
    source = (ROOT / "Scripts/ConfigData.Gameplay.cs").read_text(encoding="utf-8-sig")
    start = source.index("ChangeableShipColors =")
    end = source.index("\n        };", start)
    palette_source = source[start:end]
    entries = re.findall(
        r"\{\s*ShipTypes\.(\w+),\s*new\[\]\s*\{(.*?)\}\s*\}",
        palette_source, re.DOTALL,
    )
    palettes = {}
    for name, body in entries:
        colors = []
        for color in re.findall(r"new Color\(([^)]+)\)", body):
            values = [float(value.strip().rstrip("f"))
                      for value in color.split(",")]
            if len(values) != 4 or any(not 0 <= value <= 1 for value in values):
                raise ValueError(f"Invalid source recolor palette for {name}")
            colors.append(values[:3])
        palettes[name] = colors
    return [palettes.get(name, ()) for name in type_names]


def packed_color_mask(data: bytearray, frame, colors):
    """One source RGB match per pixel, compressed to a 1-byte mask run."""
    # Cache repeated artwork colors; multi-layer sprites often share
    # thousands of pixels of the same palette color.
    from functools import lru_cache

    @lru_cache(maxsize=16384)
    def changeable(rgb):
        red, green, blue = (value / 255.0 for value in rgb)
        return int(any(
            (red - r) ** 2 + (green - g) ** 2 + (blue - b) ** 2 < .035 ** 2
            for r, g, b in colors
        ))

    mask = (changeable(rgba[:3]) if rgba[3] else 0
            for rgba in frame.getdata())
    for color, items in groupby(mask):
        count = sum(1 for _ in items)
        while count:
            run = min(count, 65535)
            data.extend(struct.pack("<HB", run, color))
            count -= run


def pack(images, directory, magic, animated, palettes=None):
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
            if palettes is not None:
                packed_color_mask(data, frame, palettes[kind])
    return data, len(entries)


def pack_ship_animations(palettes):
    """Portable BSA1 animation sheets with genuine source recolor masks."""
    data = bytearray(struct.pack("<4sH", b"BSA1", len(SHIP_ANIMATIONS)))
    for ship_type, variant, name, clip in SHIP_ANIMATIONS:
        ppu, frames = source_frames(ROOT / "Sprites/Ships" / name, True)
        text = (ROOT / "Sprites/Ships/Animations" / clip).read_text(encoding="utf-8")
        match = re.search(r"^  m_SampleRate: (\d+)(?:\.0+)?$", text, re.MULTILINE)
        if match is None or not 1 <= int(match[1]) <= 120:
            raise ValueError(f"Missing native ship animation frame rate: {clip}")
        if not 1 <= len(frames) <= 64:
            raise ValueError(f"Invalid native ship animation frame count: {name}")
        data.extend(struct.pack("<BBHHH", ship_type, variant, ppu,
                                int(match[1]), len(frames)))
        for frame in frames:
            data.extend(struct.pack("<HH", frame.width, frame.height))
            packed_pixels(data, frame)
            packed_color_mask(data, frame, palettes[ship_type])
    return data, len(SHIP_ANIMATIONS)


def pack_ship_remains(palettes):
    """One independently lazy-loadable BSR1 atlas of original corpse art.

    The same RGBA/mask RLE as the living sprite animation atlas preserves
    Unity frame ordering, PPU and source custom-color behavior.
    """
    if len(SHIP_REMAINS) != len(SHIP_IMAGES):
        raise ValueError("Ship remains map differs from source ShipTypes")
    entries = [(kind, spec) for kind, spec in enumerate(SHIP_REMAINS) if spec]
    data = bytearray(struct.pack("<4sH", b"BSR1", len(entries)))
    for kind, (filename, clip) in entries:
        path = ROOT / "Sprites/Ships/Parts/Remains" / filename
        ppu, frames = source_frames(path, True)
        fps = 1
        if clip:
            text = (ROOT / "Sprites/Ships/Animations" / clip).read_text(encoding="utf-8")
            match = re.search(r"^  m_SampleRate: (\d+)(?:\.0+)?$", text, re.MULTILINE)
            if match is None or not 1 <= int(match[1]) <= 120:
                raise ValueError(f"Invalid native remains clip rate: {clip}")
            fps = int(match[1])
        if not 1 <= len(frames) <= 64:
            raise ValueError(f"Invalid source ship remains frames: {filename}")
        if len(frames) > 1 and not clip:
            raise ValueError(f"Animated source ship remains need a real clip: {filename}")
        data.extend(struct.pack("<BHHH", kind, ppu, fps, len(frames)))
        for frame in frames:
            data.extend(struct.pack("<HH", frame.width, frame.height))
            packed_pixels(data, frame)
            packed_color_mask(data, frame, palettes[kind])
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
    parser.add_argument("--asteroid-output", type=Path,
                        default=ROOT / "native/assets/asteroid-sprites.rgba")
    parser.add_argument("--animation-output", type=Path,
                        default=ROOT / "native/assets/ship-animations.rgba")
    parser.add_argument("--remains-output", type=Path,
                        default=ROOT / "native/assets/ship-remains.rgba")
    args = parser.parse_args()
    palettes = source_ship_color_palettes()
    ships, ship_count = pack(
        SHIP_IMAGES, ROOT / "Sprites/Ships", b"BSS2", False,
        palettes,
    )
    animations, animation_count = pack_ship_animations(palettes)
    remains, remains_count = pack_ship_remains(palettes)
    projectiles, projectile_count = pack(
        PROJECTILE_IMAGES, ROOT / "Sprites/Projectiles", b"BPS1", True,
    )
    asteroids, asteroid_count = pack(
        ASTEROID_IMAGES, ROOT / "Sprites/Asteroids", b"BAS1", False,
    )
    ships_path = write_atomic(args.ship_output, ships)
    projectiles_path = write_atomic(args.projectile_output, projectiles)
    asteroids_path = write_atomic(args.asteroid_output, asteroids)
    animations_path = write_atomic(args.animation_output, animations)
    remains_path = write_atomic(args.remains_output, remains)
    print(f"Ship remains: {remains_count} classes, "
          f"{len(remains)} bytes at {remains_path}")
    print(f"Ship animations: {animation_count} clips, "
          f"{len(animations)} bytes at {animations_path}")
    print(f"Ship sprites: {ship_count} types, {len(ships)} bytes at {ships_path}")
    print(f"Projectile sprites: {projectile_count} types, "
          f"{len(projectiles)} bytes at {projectiles_path}")
    print(f"Asteroid sprites: {asteroid_count} types, "
          f"{len(asteroids)} bytes at {asteroids_path}")


if __name__ == "__main__":
    main()
