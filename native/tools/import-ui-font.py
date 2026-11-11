"""Rasterize the project's UI font into an ASCII alpha atlas for the native client."""

import math
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont


root = Path(__file__).resolve().parents[2]
font = ImageFont.truetype(str(root / "TextMesh Pro/Fonts/PathwayGothicOne-Regular.ttf"), 24)
characters = [chr(code) for code in range(32, 127)]
left = min(font.getbbox(character, anchor="ls")[0] for character in characters)
right = max(font.getbbox(character, anchor="ls")[2] for character in characters)
ascent, descent = font.getmetrics()
width, height = right - left, ascent + descent
data = bytearray([width, height, -left])
for character in characters:
    glyph = Image.new("L", (width, height))
    ImageDraw.Draw(glyph).text((-left, ascent), character, font=font, fill=255, anchor="ls")
    data.append(math.ceil(font.getlength(character)))
    data.extend(glyph.tobytes())
(root / "native/assets/ui-font.alpha").write_bytes(data)
