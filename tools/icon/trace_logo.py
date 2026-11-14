"""Turns a raster logo (PNG, WebP, ...) into an SVG made of filled paths, e.g. a company logo that only exists as a
picture. The result is what the app's SVG reader understands (Styles/SimpleSvg.cs): a viewBox and <path d fill>
elements with absolute coordinates, nothing else.

How: the colours that cover a noticeable part of the logo become its palette (or --colors), every pixel is set to the
nearest of them (removes the anti-aliased edges, which would otherwise become thin extra shapes), see-through pixels
stay see-through, and vtracer traces the areas as smooth curves. A large source traces cleaner: give the largest file
there is (very large ones are scaled down to --width first).

Shapes that are circles (within 2.5 %) are written as exact circles. Round letters trace smoother with --corner 150;
the default keeps right angles sharp.

Run:  python tools/icon/trace_logo.py logo.png logo.svg [--width 6000] [--colors "#011A42,#FFFFFF"] [--corner 60]
      (needs Pillow and vtracer: pip install pillow vtracer)
"""
import argparse
import math
import os
import re
import tempfile

import vtracer
from PIL import Image

VIEW_WIDTH = 1000   # coordinates are scaled to this width, two decimals are plenty


def palette_of(img, share=0.005):
    """The colours of the opaque pixels that cover at least `share` of them; near-identical shades count as one."""
    small = img.copy()
    small.thumbnail((1500, 1500))
    counts = [(n, c[:3]) for n, c in small.getcolors(1 << 24) if c[3] >= 128]
    total = sum(n for n, _ in counts)
    palette = []
    for n, colour in sorted(counts, reverse=True):
        if n < total * share:
            break
        if all(sum((a - b) ** 2 for a, b in zip(colour, p)) > 24 ** 2 for p in palette):
            palette.append(colour)
    return palette


def quantized(img, palette):
    pal = Image.new("P", (1, 1))
    flat = [v for c in palette for v in c]
    pal.putpalette(flat + flat[:3] * (256 - len(palette)))
    result = img.convert("RGB").quantize(palette=pal, dither=Image.Dither.NONE).convert("RGBA")
    result.putalpha(img.getchannel("A").point(lambda a: 255 if a >= 128 else 0))
    return result


def num(value):
    return f"{value:.2f}".rstrip("0").rstrip(".")


def outline(d):
    """Points along a path of M/L/C commands (absolute), eight per curve."""
    points = []
    for command, args in re.findall(r"([MLC])([^MLCZ]*)", d):
        v = [float(n) for n in args.split()]
        if command == "C":
            (x0, y0), (x1, y1, x2, y2, x3, y3) = points[-1], v
            for i in range(1, 9):
                t = i / 8
                a, b, c, e = (1 - t) ** 3, 3 * (1 - t) ** 2 * t, 3 * (1 - t) * t * t, t ** 3
                points.append((a * x0 + b * x1 + c * x2 + e * x3, a * y0 + b * y1 + c * y2 + e * y3))
        else:
            points += list(zip(v[0::2], v[1::2]))
    return points


def circle(points, tolerance=0.025):
    """An exact circle for a traced shape that is one (dots are common in logos; traced they get small dents), else
    None. points: along the outline."""
    xs, ys = [x for x, _ in points], [y for _, y in points]
    cx, cy = (min(xs) + max(xs)) / 2, (min(ys) + max(ys)) / 2
    r = (max(xs) - min(xs) + max(ys) - min(ys)) / 4
    if len(points) < 6 or r <= 0 or any(abs(math.hypot(x - cx, y - cy) - r) > r * tolerance for x, y in points):
        return None
    k = 0.5523 * r
    return (f"M{num(cx + r)} {num(cy)}C{num(cx + r)} {num(cy + k)} {num(cx + k)} {num(cy + r)} {num(cx)} {num(cy + r)}"
            f"C{num(cx - k)} {num(cy + r)} {num(cx - r)} {num(cy + k)} {num(cx - r)} {num(cy)}"
            f"C{num(cx - r)} {num(cy - k)} {num(cx - k)} {num(cy - r)} {num(cx)} {num(cy - r)}"
            f"C{num(cx + k)} {num(cy - r)} {num(cx + r)} {num(cy - k)} {num(cx + r)} {num(cy)}Z")


def clean_svg(raw, width, height, margin=0):
    """vtracer's output with the translations baked in, scaled to VIEW_WIDTH, without anything the app does not need.
    margin: the transparent border added before tracing, taken off again."""
    scale = VIEW_WIDTH / width
    paths = []
    for attrs in re.findall(r"<path ([^>]*)/>", raw):
        d = re.search(r'\bd="([^"]*)"', attrs).group(1)
        fill = re.search(r'\bfill="([^"]*)"', attrs).group(1).upper()
        dx, dy = map(float, re.search(r"translate\(([-\d.]+),([-\d.]+)\)", attrs).groups()) if "translate" in attrs else (0, 0)
        if re.search(r"[^MCLZ\d\s.\-]", d):
            raise ValueError(f"unexpected path command in {d[:80]}")
        out, axis = [], 0
        for token in re.findall(r"[MCLZ]|-?\d+(?:\.\d+)?", d):
            if token.isalpha():
                out.append(token)
                axis = 0
            else:
                out.append(num((float(token) + (dx if axis == 0 else dy) - margin) * scale))
                axis ^= 1
        d = re.sub(r" ?([MCLZ]) ?", r"\1", " ".join(out))
        d = (circle(outline(d)) if d.count("M") == 1 else None) or d
        paths.append(f'<path fill="{fill}" d="{d}"/>')
    return (f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {VIEW_WIDTH} {height * scale:.2f}">\n'
            + "\n".join(paths) + "\n</svg>\n")


def main():
    parser = argparse.ArgumentParser(description="Trace a raster logo into an SVG of filled paths.")
    parser.add_argument("source")
    parser.add_argument("target")
    parser.add_argument("--width", type=int, default=6000, help="scale larger sources down to this width first (default 6000)")
    parser.add_argument("--colors", help="palette as comma-separated #RRGGBB (default: the main colours of the logo)")
    parser.add_argument("--corner", type=int, default=60,
                        help="smallest turn in degrees that stays a sharp corner (default 60 keeps right angles sharp; "
                             "about 150 for logos of round letters, whose curves otherwise get small kinks)")
    args = parser.parse_args()

    Image.MAX_IMAGE_PIXELS = None   # logos often come as huge print files
    img = Image.open(args.source).convert("RGBA")
    if img.width > args.width:
        img = img.resize((args.width, round(img.height * args.width / img.width)), Image.LANCZOS)
    palette = ([tuple(int(c.strip().lstrip("#")[i:i + 2], 16) for i in (0, 2, 4)) for c in args.colors.split(",")]
               if args.colors else palette_of(img))
    print("Palette: " + ", ".join("#%02X%02X%02X" % c for c in palette))

    with tempfile.TemporaryDirectory() as tmp:
        flat, traced = os.path.join(tmp, "flat.png"), os.path.join(tmp, "traced.svg")
        flat_img, margin = quantized(img, palette), 0
        if flat_img.getchannel("A").getextrema()[0] == 0:
            # See-through logo: a transparent border, so shapes touching the edge are not traced with a flat side
            margin = max(4, img.width // 100)
            framed = Image.new("RGBA", (img.width + 2 * margin, img.height + 2 * margin), (0, 0, 0, 0))
            framed.paste(flat_img, (margin, margin))
            flat_img = framed
        flat_img.save(flat)
        vtracer.convert_image_to_svg_py(flat, traced, colormode="color", hierarchical="stacked", mode="spline",
                                        filter_speckle=8, color_precision=8, layer_difference=8, corner_threshold=args.corner,
                                        length_threshold=10, max_iterations=30, splice_threshold=60, path_precision=2)
        with open(traced, encoding="utf-8") as f:
            svg = clean_svg(f.read(), img.width, img.height, margin)
    with open(args.target, "w", encoding="utf-8", newline="\n") as f:
        f.write(svg)
    print(f"Written: {args.target} ({svg.count('<path')} paths, {len(svg) // 1024} KB)")


if __name__ == "__main__":
    main()
