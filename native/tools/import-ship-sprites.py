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
    "honeybee.png", "hornet.png", "leafcutter.png", "queen_nocrown.png",
    "scout.png", "striker.png", "warpgate.png", "wasp.png",
    "yellowjacket.png", None,
)

# (ShipTypes ID, animation variant, sheet, authored Unity animation clip).
# Barge: 0=wind-up, 1=active rush, 2=charge image, 3=charge rocket flares.
# WarpGate: 0=opening, 1=ready.
# Other classes use variant 0 for the source's working animation.
SHIP_ANIMATIONS = (
    (0, 0, "Parts/barge_charging.png", "Barge Charging.anim"),
    (0, 1, "Parts/barge_charge.png", "Barge Charge.anim"),
    (0, 2, "Parts/barge_charge_image.png", "Barge Charge Image.anim"),
    (0, 3, "Parts/barge_charge_flames.png", "Barge Charge Rocket Flare.anim"),
    (4, 0, "carpenter_anim.png", "Carpenter Bee Mining.anim"),
    (9, 0, "factory_anim.png", "Factory Mining.anim"),
    (20, 0, "warpgate_anim.png", "Warp Gate Opening.anim"),
    (20, 1, "warpgate_loop.png", "Warp Gate Loop.anim"),
)

# Queen.prefab draws its original Crown (0) and Queen Eye (1) child
# sprites above the massive source Queen hull. Their authored transforms
# are reconstructed in the common native local/LAN presentation path.
QUEEN_PART_IMAGES = ("queen_crown.png", "queen_eye.png")

# Beacon.SetBeaconStatus switches to this original alternate sprite
# whenever its authored sight collider overlaps a living enemy ship.
BEACON_ENEMY_IMAGES = ("Beacon Enemy.png",)

# Immutable prefab sprites shared by local and authenticated LAN effects.
# The original Beehive Healing Cross uses a 50x50 sprite at 8 pixels/unit.
SHIP_EFFECT_IMAGES = ("healing.png",)

# The Queen Explosion prefab has four independently sorted SpriteRenderers
# with original 40-frame, 10 FPS clips and one Kill event at 4s.
QUEEN_EXPLOSION = (
    ("queen_death_body.png", "Queen Explosion Body.anim"),
    ("queen_death_crown.png", "Queen Explosion Crown.anim"),
    ("queen_death_wings_eyes.png", "Queen Explosion Wings.anim"),
    ("queen_death_rings.png", "Queen Explosion Rings.anim"),
)

# Source Map.SpriteRenderer backgrounds, in native map type order.
MAP_BACKGROUND_IMAGES = (
    "pluto 1k resize.png", "neptune.png", "titania_rough.png", "uranus.png",
    # Uranus' distinct Ring Sparkle child, behind the main planet (sort -1).
    "Uranus_ring_bg_texture.png",
)

# Exact SpriteRenderer overrides on the original four Map prefabs.
# Their fog overlay uses uniform 56/255 gray tint at m_SortingOrder 1.
# Pre-tint RGB once while importing; no RGB multiplies in the masked
# screen-pixel loop or any headless simulation.
MAP_FOG_IMAGES = (
    "pluto_fog_of_war.png", "neptune fog of war.png",
    "titania_fow.png", "uranus_fog_bg-export.png",
)

# The original CutsceneManager.Portraits registry: two authentic
# chat frames per speaker, in the stable order decoded by the native UI.
# These are only loaded by graphical campaign dialogue, never by workers.
DIALOGUE_PORTRAITS = (
    "samuel_chat.png", "tom_chat.png", "commander_chat.png",
    "oviya_chat.png", "marco_chat.png", "yoshiko_chat.png",
    "joey_chat.png", "wesley_chat.png", "alejandra_chat.png",
    "emilia_chat.png", "fritz_chat.png", "marge_chat.png",
    "barge_pilot_chat.png", "ami_chat.png",
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
    None,
    "Projectiles/Bee Small Laser Shot.png",
    "Projectiles/pill_projectile_bee.png",
    "Projectiles/Bumblebee Laser Shot.png",
    "Projectiles/flagship_bolt.png",
    "Projectiles/Frigate Rocket In Flight.png",
    "Projectiles/Human Small Laser Shot.png",
    "Projectiles/pill_projectile.png",
    "Projectiles/beam.png",
    "Projectiles/Leafcutter Shot.png",
    "Projectiles/Queen Small Laser Shot.png",
    "Projectiles/Queen Stinger Laser Shot.png",
    "Projectiles/Striker Bomb.png",
    "Projectiles/Frigate Rocket Explosion.png",
    "Projectiles/Fireship Explosion.png",
    "Objects/fire_tank_explosion.png",
)


PROJECTILE_CLIPS = (
    None,
    "Projectiles/Animations/Bee Small Laser Shot.anim",
    "Projectiles/Animations/Bee Medium Laser Shot.anim",
    "Projectiles/Animations/Bumblebee Laser Shot.anim",
    "Projectiles/Animations/Flagship Laser Shot.anim",
    "Projectiles/Animations/Frigate Rocket In Flight.anim",
    "Projectiles/Animations/Human Small Laser Shot.anim",
    "Projectiles/Animations/Human Medium Laser Shot.anim",
    "Projectiles/Animations/Laser Beam.anim",
    "Projectiles/Animations/Leaf Cutter Shot Idle.anim",
    "Projectiles/Animations/Queen Small Laser Shot.anim",
    "Projectiles/Animations/Queen Stinger Laser Shot.anim",
    "Projectiles/Animations/Striker Bomb.anim",
    "Projectiles/Animations/Frigate Rocket Explosion.anim",
    "Projectiles/Animations/Fireship Explosion.anim",
    "Objects/Fire Tank Explosion.anim",
)


def source_frames(path: Path, animation: bool, *, large_static: bool = False):
    metadata = path.with_suffix(".png.meta").read_text(encoding="utf-8")
    source_ppu = re.search(r"^  spritePixelsToUnits: (\d+)$", metadata, re.MULTILINE)
    if source_ppu is None or not (1 <= int(source_ppu[1]) <= 65535):
        raise ValueError(f"Missing valid Unity pixels-per-unit: {path}")
    mode = re.search(r"^  spriteMode: ([12])$", metadata, re.MULTILINE)
    if mode is None:
        raise ValueError(f"Missing supported Unity sprite import mode: {path}")
    with Image.open(path) as image:
        if mode[1] == "1":
            # Unity imports the whole current texture in Single mode. The
            # legacy sprite table can still describe a pre-resize PNG.
            rects = [(0, 0, image.width, image.height)]
        else:
            rects = re.findall(
                r"^      rect:\n        serializedVersion: 2\n"
                r"        x: (\d+)\n        y: (\d+)\n"
                r"        width: (\d+)\n        height: (\d+)$",
                metadata, re.MULTILINE,
            )
            if not 1 <= len(rects) <= (64 if animation else 1):
                raise ValueError(f"Invalid Unity sprite slice count: {path}")
        boxes = []
        largest = 0
        limit = 8192 if large_static else 2048
        for rect in rects:
            x, y, width, height = map(int, rect)
            top = image.height - y - height
            if (x < 0 or top < 0 or not 1 <= width <= limit or
                    not 1 <= height <= limit or x + width > image.width or
                    top + height > image.height):
                raise ValueError(f"Out-of-range source sprite rect: {path}")
            boxes.append((x, top, x + width, top + height))
            largest = max(largest, width, height)
        # Resample each frame before retaining it. Large sheets need only
        # one decoded PNG plus the bounded output frames, even for explosions.
        ppu = int(source_ppu[1])
        divisor = 1
        if animation or large_static:
            target = 256 if animation else 2048
            while largest // divisor > target and ppu % (2 * divisor) == 0:
                divisor *= 2
            if large_static and not animation and largest // divisor > target:
                raise ValueError(f"Source sprite cannot fit the native atlas: {path}")
        frames = []
        for box in boxes:
            frame = image.crop(box)
            if frame.mode != "RGBA":
                frame = frame.convert("RGBA")
            if divisor > 1:
                frame = frame.resize((max(1, frame.width // divisor),
                                      max(1, frame.height // divisor)),
                                     Image.Resampling.BOX)
            frames.append(frame)
    return ppu // divisor, frames


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


def pack(images, directory, magic, animated, palettes=None,
         *, large_static=False, tint_rgb=None):
    entries = [(kind, name) for kind, name in enumerate(images) if name]
    data = bytearray(struct.pack("<4sH", magic, len(entries)))
    for kind, name in entries:
        ppu, frames = source_frames(
            directory / name, animated,
            large_static=large_static or (magic == b"BSS2" and kind == 17),
        )
        if tint_rgb is not None:
            tinted = []
            for frame in frames:
                rgb = frame.convert("RGB").point(
                    lambda channel: (channel * tint_rgb + 127) // 255)
                tinted.append(Image.merge("RGBA", (*rgb.split(), frame.getchannel("A"))))
            frames = tinted
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


PROJECTILE_IMPACTS = (
    (1, "Bee Small Laser Shot Explosion.png", "Bee Small Laser Shot Explosion.anim"),
    (2, "Bee Medium Laser Shot Impact Splash.png", "Bee Medium Laser Shot impact Splash.anim"),
    (3, "large_projectile_impact_24.png", "Bumblebee Laser Shot Impact.anim"),
    (4, "flagship_laser_contact_1_optimized-export.png", "Flagship Laser Shot Impact Splash.anim"),
    (6, "Human Small Laser Shot Explosion.png", "Human Small Laser Shot Explosion.anim"),
    (7, "Human Medium Laser Shot Impact Splash.png", "Human Medium Laser Shot Impact Splash.anim"),
    (9, "Leafcutter Shot.png", "Leaf Cutter Shot Explode.anim"),
    (10, "Queen Small Laser Shot Explosion.png", "Queen Small Laser Shot Explosion.anim"),
    (11, "stinger_explosion_24.png", "Queen Stinger Laser Shot Impact.anim"),
)


def pack_projectiles():
    return pack_projectile_clips([
        (kind, image, PROJECTILE_CLIPS[kind])
        for kind, image in enumerate(PROJECTILE_IMAGES) if image
    ])


def pack_projectile_clips(entries, *, one_shot=False):
    data = bytearray(struct.pack("<4sH", b"BPS2", len(entries)))
    for kind, image, clip in entries:
        texture = ROOT / "Sprites" / image
        metadata = texture.with_suffix(".png.meta").read_text(encoding="utf-8")
        authored = (ROOT / "Sprites" / clip).read_text(encoding="utf-8")
        sprite_guid = re.search(r"^guid: ([0-9a-f]{32})$", metadata, re.MULTILINE)
        ids = re.findall(r"^      internalID: (-?\d+)$", metadata, re.MULTILINE)
        rate = re.search(r"^  m_SampleRate: (\d+)$", authored, re.MULTILINE)
        loop = re.search(r"^    m_LoopTime: ([01])$", authored, re.MULTILINE)
        duration = re.search(r"^    m_StopTime: ([\d.]+)$", authored, re.MULTILINE)
        keys = re.findall(
            r"^    - time: ([\d.]+)\n      value: \{fileID: (-?\d+), guid: ([0-9a-f]{32}), type: 3\}$",
            authored, re.MULTILINE,
        )
        ppu, frames = source_frames(texture, True)
        if (not all((sprite_guid, rate, loop, duration)) or
                len(ids) != len(frames) or len(set(ids)) != len(ids) or
                not 1 <= len(keys) <= 64 or not 1 <= int(rate[1]) <= 120):
            raise ValueError(f"Invalid source projectile animation: {clip}")
        fps = int(rate[1])
        frame_count = round(float(duration[1]) * fps)
        if (not 1 <= frame_count <= 64 or
                abs(float(duration[1]) * fps - frame_count) > 0.001):
            raise ValueError(f"Invalid source projectile duration: {clip}")
        slices = dict(zip(ids, frames, strict=True))
        keyframes = {}
        for seconds, sprite_id, guid in keys:
            index = round(float(seconds) * fps)
            if (abs(float(seconds) * fps - index) > 0.001 or
                    not 0 <= index < frame_count or index in keyframes or
                    guid != sprite_guid[1] or sprite_id not in slices):
                raise ValueError(f"Invalid source projectile frame {index}: {clip}")
            keyframes[index] = sprite_id
        sprite_id = keyframes.get(0)
        if sprite_id is None:
            raise ValueError(f"Source projectile clip has no opening frame: {clip}")
        data.extend(struct.pack("<BHHHB", kind, ppu, frame_count, fps,
                                0 if one_shot else int(loop[1])))
        for index in range(frame_count):
            sprite_id = keyframes.get(index, sprite_id)
            frame = slices[sprite_id]
            data.extend(struct.pack("<HH", frame.width, frame.height))
            packed_pixels(data, frame)
    return data, len(entries)


def pack_carrier_decks():
    """Exact original thirteen carrier-deck overlays, from a 7x2 PNG.

    Source Carrier.SetColor stretches each 96x112 deck to the living carrier
    sprite bounds. Do that once, during asset conversion, rather than adding
    a second resampling pass to every rendered native combat frame.
    """
    ppu, carrier_frames = source_frames(
        ROOT / "Sprites/Ships/carrier.png", False,
    )
    if len(carrier_frames) != 1:
        raise ValueError("native carrier base must contain one source sprite")
    base_size = carrier_frames[0].size
    sheet_path = ROOT / "Sprites/Ships/Parts/carrier_alts_deck.png"
    with Image.open(sheet_path) as image:
        sheet = image.convert("RGBA")
    if sheet.size != (672, 224):
        raise ValueError(f"carrier deck sheet must be 672x224: {sheet_path}")
    data = bytearray(struct.pack(
        "<4sHHHH", b"BCD1", 13, ppu, base_size[0], base_size[1],
    ))
    for index in range(13):
        x = (index % 7) * 96
        y = (index // 7) * 112
        frame = sheet.crop((x, y, x + 96, y + 112))
        if frame.size != base_size:
            frame = frame.resize(base_size, Image.Resampling.BILINEAR)
        packed_pixels(data, frame)
    return data


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


def pack_dialogue_portraits():
    # Pair both source SpriteMode.Multiple chat frames in a small, bounded
    # file independent of the large ship animation and campaign text.
    data = bytearray(struct.pack("<4sH", b"BDP1", len(DIALOGUE_PORTRAITS)))
    for speaker, filename in enumerate(DIALOGUE_PORTRAITS):
        _, frames = source_frames(
            ROOT / "Resources/Sprites/Portraits" / filename, True,
        )
        if len(frames) != 2:
            raise ValueError(f"Source portrait must have exactly two frames: {filename}")
        data.append(speaker)
        for frame in frames:
            data.extend(struct.pack("<HH", frame.width, frame.height))
            packed_pixels(data, frame)
    return data


def pack_queen_explosion():
    """Pack all Queen death layers in the existing lazy remains envelope.

    The source QueenExplosionAnimation completes after 4 seconds. Its
    parts are rendered only after a true ShipDied combat event; no gameplay
    clock, debris physics, or extra train-time state is introduced.
    """
    data = bytearray(struct.pack("<4sH", b"BSR1", len(QUEEN_EXPLOSION)))
    for layer, (filename, clip) in enumerate(QUEEN_EXPLOSION):
        path = ROOT / "Sprites/Ships/Parts/Ship Explosions" / filename
        ppu, frames = source_frames(path, True)
        text = (ROOT / "Sprites/Ships/Animations" / clip).read_text(
            encoding="utf-8",
        )
        match = re.search(r"^  m_SampleRate: (\d+)(?:\.0+)?$", text, re.MULTILINE)
        if match is None or int(match[1]) != 10 or len(frames) != 40:
            raise ValueError(f"Queen explosion must retain 40 frames at 10FPS: {clip}")
        data.extend(struct.pack("<BHHH", layer, ppu, 10, len(frames)))
        for frame in frames:
            data.extend(struct.pack("<HH", frame.width, frame.height))
            packed_pixels(data, frame)
            # Queen explosion has no source squad-recolor surface. The
            # existing remains decoder requires one bounded zero mask.
            left = frame.width * frame.height
            while left:
                run = min(left, 65535)
                data.extend(struct.pack("<HB", run, 0))
                left -= run
    return data


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
    parser.add_argument("--map-background-output", type=Path,
                        default=ROOT / "native/assets/map-backgrounds.rgba")
    parser.add_argument("--map-fog-output", type=Path,
                        default=ROOT / "native/assets/map-fog.rgba")
    parser.add_argument("--portrait-output", type=Path,
                        default=ROOT / "native/assets/dialogue-portraits.rgba")
    parser.add_argument("--ship-output", type=Path,
                        default=ROOT / "native/assets/ship-sprites.rgba")
    parser.add_argument("--beacon-output", type=Path,
                        default=ROOT / "native/assets/beacon-enemy.rgba")
    parser.add_argument("--projectile-output", type=Path,
                        default=ROOT / "native/assets/projectile-sprites.rgba")
    parser.add_argument("--projectile-impact-output", type=Path,
                        default=ROOT / "native/assets/projectile-impacts.rgba")
    parser.add_argument("--asteroid-output", type=Path,
                        default=ROOT / "native/assets/asteroid-sprites.rgba")
    parser.add_argument("--animation-output", type=Path,
                        default=ROOT / "native/assets/ship-animations.rgba")
    parser.add_argument("--remains-output", type=Path,
                        default=ROOT / "native/assets/ship-remains.rgba")
    parser.add_argument("--deck-output", type=Path,
                        default=ROOT / "native/assets/carrier-decks.rgba")
    parser.add_argument("--queen-output", type=Path,
                        default=ROOT / "native/assets/queen-parts.rgba")
    parser.add_argument("--effects-output", type=Path,
                        default=ROOT / "native/assets/ship-effects.rgba")
    parser.add_argument("--queen-explosion-output", type=Path,
                        default=ROOT / "native/assets/queen-explosion.rgba")
    args = parser.parse_args()
    palettes = source_ship_color_palettes()
    ships, ship_count = pack(
        SHIP_IMAGES, ROOT / "Sprites/Ships", b"BSS2", False,
        palettes,
    )
    beacon_enemy, beacon_enemy_count = pack(
        BEACON_ENEMY_IMAGES, ROOT / "Sprites/Ships", b"BSS2",
        False, (palettes[1],),
    )
    backgrounds, background_count = pack(
        MAP_BACKGROUND_IMAGES, ROOT / "Sprites/Backgrounds", b"BSS1",
        False, large_static=True,
    )
    fog, fog_count = pack(
        MAP_FOG_IMAGES, ROOT / "Sprites/Backgrounds", b"BSS1",
        False, large_static=True, tint_rgb=56,
    )
    portrait_art = pack_dialogue_portraits()
    animations, animation_count = pack_ship_animations(palettes)
    remains, remains_count = pack_ship_remains(palettes)
    decks = pack_carrier_decks()
    queen_parts, queen_part_count = pack(
        QUEEN_PART_IMAGES, ROOT / "Sprites/Ships/Parts", b"BSS1", False,
    )
    queen_explosion = pack_queen_explosion()
    effects, effect_count = pack(
        SHIP_EFFECT_IMAGES, ROOT / "Sprites/Ships/Parts", b"BSS1", False,
    )
    projectiles, projectile_count = pack_projectiles()
    impacts, _ = pack_projectile_clips([
        (kind, f"Projectiles/{image}", f"Projectiles/Animations/{clip}")
        for kind, image, clip in PROJECTILE_IMPACTS
    ], one_shot=True)
    asteroids, asteroid_count = pack(
        ASTEROID_IMAGES, ROOT / "Sprites/Asteroids", b"BAS1", False,
    )
    backgrounds_path = write_atomic(args.map_background_output, backgrounds)
    fog_path = write_atomic(args.map_fog_output, fog)
    portrait_path = write_atomic(args.portrait_output, portrait_art)
    ships_path = write_atomic(args.ship_output, ships)
    beacon_path = write_atomic(args.beacon_output, beacon_enemy)
    projectiles_path = write_atomic(args.projectile_output, projectiles)
    write_atomic(args.projectile_impact_output, impacts)
    asteroids_path = write_atomic(args.asteroid_output, asteroids)
    animations_path = write_atomic(args.animation_output, animations)
    remains_path = write_atomic(args.remains_output, remains)
    decks_path = write_atomic(args.deck_output, decks)
    queen_path = write_atomic(args.queen_output, queen_parts)
    effects_path = write_atomic(args.effects_output, effects)
    queen_explosion_path = write_atomic(args.queen_explosion_output, queen_explosion)
    print(f"Queen explosion: 4 original animated layers, {len(queen_explosion)} bytes at {queen_explosion_path}")
    print(f"Queen prefab parts: {queen_part_count} sprites, {len(queen_parts)} bytes at {queen_path}")
    print(f"Source visual effects: {effect_count} sprites, {len(effects)} bytes at {effects_path}")
    print(f"Beacon warning artwork: {beacon_enemy_count} source sprites, "
          f"{len(beacon_enemy)} bytes at {beacon_path}")
    print(f"Carrier deck overlays: 13 variants, {len(decks)} bytes at {decks_path}")
    print(f"Ship remains: {remains_count} classes, "
          f"{len(remains)} bytes at {remains_path}")
    print(f"Ship animations: {animation_count} clips, "
          f"{len(animations)} bytes at {animations_path}")
    print(f"Planetary backdrops: {background_count} types, "
          f"{len(backgrounds)} bytes at {backgrounds_path}")
    print(f"Original fog map overlays: {fog_count} types, "
          f"{len(fog)} bytes at {fog_path}")
    print(f"Original campaign portraits: {len(DIALOGUE_PORTRAITS)} speakers, "
          f"{len(portrait_art)} bytes at {portrait_path}")
    print(f"Ship sprites: {ship_count} types, {len(ships)} bytes at {ships_path}")
    print(f"Projectile sprites: {projectile_count} types, "
          f"{len(projectiles)} bytes at {projectiles_path}")
    print(f"Asteroid sprites: {asteroid_count} types, "
          f"{len(asteroids)} bytes at {asteroids_path}")


if __name__ == "__main__":
    main()
