"""Makes the number stamps 00-20 that ship as samples (stamp folder, subfolder "Numbers"/"Nummern"):
flat, modern badges - a coloured disc with a thin white ring inside and a white number in Barlow Bold, the
program's own font. They mark an order on pages (1, 2, 3 ...).

The pictures are 236 x 236 px with 300 dpi stored in the file (PNG pHYs): the program places a stamp in the size
its resolution says (here 20 mm), and the 300 dpi keep it sharp in print.

Usage: python tools/stamps/make_number_stamps.py [--color "#D62F2F"] [--size-mm 20] [--out DIR]
Needs Pillow (pip install pillow). Default output: src/EasyPDF.Core/Stamps/Samples/Numbers
"""

import argparse
import os

from PIL import Image, ImageDraw, ImageFont

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
FONT = os.path.join(ROOT, "src", "EasyPDF.App", "Assets", "Fonts", "Barlow-Bold.ttf")
OUT = os.path.join(ROOT, "src", "EasyPDF.Core", "Stamps", "Samples", "Numbers")
DPI = 300
SUPERSAMPLE = 4  # drawn larger and scaled down: smooth edges without relying on the drawing library's antialiasing


def badge(number: int, size: int, color: str) -> Image.Image:
    big = size * SUPERSAMPLE
    image = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)
    edge = big * 0.02
    draw.ellipse((edge, edge, big - edge, big - edge), fill=color)
    # Thin white ring inside the edge: reads as a badge, and the disc stays visible on dark scans too
    inset = big * 0.075
    draw.ellipse((inset, inset, big - inset, big - inset), outline="white", width=round(big * 0.028))

    text = str(number)
    # Same height for every number; two digits are narrowed only as far as needed to fit the ring
    font_size = big * 0.56
    font = ImageFont.truetype(FONT, round(font_size))
    left, top, right, bottom = draw.textbbox((0, 0), text, font=font)
    max_width = big * 0.60
    if right - left > max_width:
        font = ImageFont.truetype(FONT, round(font_size * max_width / (right - left)))
        left, top, right, bottom = draw.textbbox((0, 0), text, font=font)
    # Centred by the ink of the digits, not by the font's line box
    x = (big - (right - left)) / 2 - left
    y = (big - (bottom - top)) / 2 - top
    draw.text((x, y), text, font=font, fill="white")
    return image.resize((size, size), Image.LANCZOS)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--color", default="#D62F2F", help="colour of the disc (white numbers stay readable on dark colours)")
    parser.add_argument("--size-mm", type=float, default=20, help="size the stamp is placed in")
    parser.add_argument("--out", default=OUT)
    args = parser.parse_args()

    size = round(args.size_mm / 25.4 * DPI)
    os.makedirs(args.out, exist_ok=True)
    for number in range(21):
        path = os.path.join(args.out, f"{number:02d}.png")  # two digits, so the gallery sorts them in order
        badge(number, size, args.color).save(path, dpi=(DPI, DPI), optimize=True)
    print(f"21 stamps of {size} x {size} px ({args.size_mm} mm at {DPI} dpi) in {args.out}")


if __name__ == "__main__":
    main()
