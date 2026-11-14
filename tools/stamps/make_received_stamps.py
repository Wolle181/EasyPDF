"""Makes the "received" stamps that ship as samples: Eingegangen.png and Received.png. The word is part of the picture,
the date below it a field (see StampFile in EasyPDF.Core: an iTXt chunk with JSON inside the PNG), so the program writes
the date each time the stamp is placed.

The pictures have a transparent background and 300 dpi stored in the file (PNG pHYs), like the number stamps.

Usage: python tools/stamps/make_received_stamps.py [--out DIR]
Needs Pillow (pip install pillow). Default output: src/EasyPDF.Core/Stamps/Samples
"""

import argparse
import json
import os

from PIL import Image, ImageDraw, ImageFont
from PIL.PngImagePlugin import PngInfo

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
FONT = os.path.join(ROOT, "src", "EasyPDF.App", "Assets", "Fonts", "Barlow-Bold.ttf")
OUT = os.path.join(ROOT, "src", "EasyPDF.Core", "Stamps", "Samples")
DPI = 300
SUPERSAMPLE = 4  # drawn larger and scaled down: smooth edges without relying on the drawing library's antialiasing
WIDTH, HEIGHT = 709, 190  # 60 x 16 mm at 300 dpi
WORD_COLOR = "#0B5FAE"
DATE_COLOR = "#D9161C"
WORD_TOP, WORD_HEIGHT = 0.09, 0.37  # shares of the picture height
DATE_FIELD = {"x": 0.1, "y": 0.60, "w": 0.8, "h": 0.28}

STAMPS = {
    "Eingegangen": ("EINGEGANGEN", "{$DATE:dd. MMM. yyyy}", "de"),
    "Received": ("RECEIVED", "{$DATE:dd MMM yyyy}", "en"),
}


def picture(word: str) -> Image.Image:
    big_w, big_h = WIDTH * SUPERSAMPLE, HEIGHT * SUPERSAMPLE
    image = Image.new("RGBA", (big_w, big_h), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)
    # The size whose capitals are as high as wanted, reduced if the word is too wide for the picture
    target = big_h * WORD_HEIGHT
    left, top, right, bottom = draw.textbbox((0, 0), "H", font=ImageFont.truetype(FONT, 100))
    font_size = 100.0 * target / (bottom - top)
    font = ImageFont.truetype(FONT, round(font_size))
    left, top, right, bottom = draw.textbbox((0, 0), word, font=font)
    if right - left > big_w * 0.96:
        font = ImageFont.truetype(FONT, round(font_size * big_w * 0.96 / (right - left)))
        left, top, right, bottom = draw.textbbox((0, 0), word, font=font)
    # Centred by the ink of the letters, not by the font's line box
    x = (big_w - (right - left)) / 2 - left
    y = big_h * WORD_TOP + (target - (bottom - top)) / 2 - top
    draw.text((x, y), word, font=font, fill=WORD_COLOR)
    return image.resize((WIDTH, HEIGHT), Image.LANCZOS)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--out", default=OUT)
    args = parser.parse_args()

    os.makedirs(args.out, exist_ok=True)
    for name, (word, text, language) in STAMPS.items():
        field = {**DATE_FIELD, "text": text, "color": DATE_COLOR, "upper": True, "lang": language}
        info = PngInfo()
        info.add_itxt("EasyPDF.Stamp", json.dumps({"fields": [field]}, separators=(",", ":")))
        picture(word).save(os.path.join(args.out, name + ".png"), dpi=(DPI, DPI), pnginfo=info, optimize=True)
    print(f"{len(STAMPS)} stamps of {WIDTH} x {HEIGHT} px in {args.out}")


if __name__ == "__main__":
    main()
