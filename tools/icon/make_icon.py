"""Draws the icon of a brand (or takes its logo file) and writes all formats the app and the installers need into
branding/<brand>/icons/ (app.ico, app.icns, app-<size>.png), where the build picks them up.

Design: the brand mark that the header shows left of the product name (BrandMark in Views/MainWindow.axaml) - a rounded
square in the dark primary colour framed in the accent colour, inside it two white page outlines (Icon.Concatenate) and
a plus in the accent colour. Sizes up to 32 px use a thicker frame, thicker lines and a larger drawing so they stay crisp.

The colours are the brand colours from branding/<brand>/Branding.props (BrandPrimaryColor, BrandAccentColor); run this
again after changing them (the test MainWindowAndDialogsShowTheAppIcon notices icons in the old colours).

Logo file instead of the drawing: the icon is the logo, fitted into a square (transparent margins trimmed, aspect ratio
kept, centred). It is kept as branding/<brand>/icons/logo.png, so every later run (e.g. after a colour change) uses it
again; delete that file to go back to the drawing. The logo also replaces the drawn mark in the app's header: it is
written as icons/header-logo.png (Assets/BrandLogo.png in the app, Styles/BrandLogo.cs), which the drawn icon deletes
again. An optional simpler version for 16-32 px (icons/logo-small.png) stays crisp where fine details of the full logo
blur. Any format Pillow reads (PNG with transparency is best, at least 1024 px; for SVG export a PNG first).

Company logo (shown in Help > About): --company-logo <file.svg> writes only icons/company-logo.svg, which the app draws
as vector graphics. A logo with a transparent background is put on a white card (readable in the dark theme too), an
opaque one is kept as it is. A logo that only exists as a picture is traced into an SVG first (trace_logo.py).

Run:  python tools/icon/make_icon.py [--brand <id>]                      (needs Pillow; default: the selected brand)
      python tools/icon/make_icon.py [--brand <id>] --logo my-logo.png [--logo-small my-logo-16.png] [--margin 4]
      python tools/icon/make_icon.py [--brand <id>] --company-logo company.svg
"""
import argparse
import colorsys
import os
import re
import subprocess
import sys
from PIL import Image, ImageDraw

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))


def selected_brand():
    """The brand the build uses (Brand in Directory.Build.props, EASYPDF_BRAND)."""
    return subprocess.run(["dotnet", "msbuild", os.path.join(ROOT, "src", "EasyPDF.App"), "-getProperty:Brand"],
                          capture_output=True, text=True, check=True).stdout.strip()


def brand_color(props, name):
    """#RRGGBB of a property in the brand's Branding.props as (r, g, b)."""
    with open(props, encoding="utf-8") as f:
        value = re.search(rf"<{name}>\s*#([0-9A-Fa-f]{{6}})\s*</{name}>", f.read()).group(1)
    return tuple(int(value[i:i + 2], 16) for i in (0, 2, 4))


def shade(color, lighten):
    """Same as BrandColors.Shade (Styles/BrandColors.cs) with only a lightness change."""
    h, l, s = colorsys.rgb_to_hls(*(c / 255 for c in color))
    return tuple(round(c * 255) for c in colorsys.hls_to_rgb(h, min(1, max(0, l + lighten)), s))


MARK = ACCENT = None    # set by main() from the brand: Header.MarkBackground and Brand.Accent in App.axaml
WHITE = (255, 255, 255)

MASTER = 1024

# The mark's drawing on its 24 x 24 grid (Icon.Concatenate and Icon.Concatenate.Accent in Styles/Icons.axaml)
PAGES = [[(3.5, 3.5), (9.5, 3.5), (12.5, 6.5), (12.5, 14), (3.5, 14)],
         [(11.5, 10), (17.5, 10), (20.5, 13), (20.5, 20.5), (11.5, 20.5)]]
PLUS = [((15.5, 2.5), (15.5, 7)), ((13.25, 4.75), (17.75, 4.75))]


def stroke(d, points, width, fill, closed):
    """A line through the points with round joins, and round caps if it is open."""
    if closed:
        points = points + points[:2]
    d.line(points, fill=fill, width=round(width), joint="curve")
    if not closed:
        r = width / 2
        for x, y in (points[0], points[-1]):
            d.ellipse([x - r, y - r, x + r, y + r], fill=fill)


def draw_master(simple):
    """The icon at 1024 x 1024 (RGBA). In the header the mark is 42 px with a 1.5 px frame and the drawing at 26 px."""
    img = Image.new("RGBA", (MASTER, MASTER), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)

    margin = 24 if simple else 40
    px = (MASTER - 2 * margin) / 42          # one pixel of the header mark
    frame = px * (2.6 if simple else 1.5)
    d.rounded_rectangle([margin, margin, MASTER - margin, MASTER - margin], radius=8 * px, fill=ACCENT)
    d.rounded_rectangle([margin + frame, margin + frame, MASTER - margin - frame, MASTER - margin - frame],
                        radius=8 * px - frame, fill=MARK)

    content = px * (32 if simple else 26)
    unit = content / 24
    offset = (MASTER - content) / 2

    def at(p):
        return offset + p[0] * unit, offset + p[1] * unit

    for page in PAGES:
        stroke(d, [at(p) for p in page], unit * (2.2 if simple else 1.6), WHITE, closed=True)
    for a, b in PLUS:
        stroke(d, [at(a), at(b)], unit * (2.4 if simple else 1.8), ACCENT, closed=False)
    return img


def load_logo(path, needed=0):
    """The logo as RGBA; exits with a message if Pillow cannot read it, warns if it is smaller than needed (px)."""
    if not os.path.isfile(path):
        sys.exit(f"Logo not found: {path}")
    try:
        img = Image.open(path)
        img.load()
    except (OSError, ValueError) as e:
        sys.exit(f"Cannot read the logo {path}: {e}" + (" (export the SVG as PNG first)" if path.lower().endswith(".svg") else ""))
    if max(img.size) < needed:
        print(f"Warning: {path} is only {img.width} x {img.height} px; icons up to {needed} px will be blurred.")
    return img.convert("RGBA")


def logo_master(logo, margin):
    """The logo on a transparent 1024 x 1024 square: transparent border trimmed, scaled to fit inside the margin
    (percent of the side), aspect ratio kept, centred."""
    box = logo.getchannel("A").getbbox()
    if box:
        logo = logo.crop(box)
    inner = MASTER * (1 - 2 * margin / 100)
    scale = inner / max(logo.size)
    logo = logo.resize((max(1, round(logo.width * scale)), max(1, round(logo.height * scale))), Image.LANCZOS)
    img = Image.new("RGBA", (MASTER, MASTER), (0, 0, 0, 0))
    img.alpha_composite(logo, ((MASTER - logo.width) // 2, (MASTER - logo.height) // 2))
    return img


def store_logo(source, target):
    """Keep the logo in the brand's icons folder as PNG, so later runs use it again."""
    if os.path.abspath(source) != os.path.abspath(target):
        load_logo(source).save(target)


CARD_MARGIN = 0.08   # white card around a see-through company logo, share of its width


def company_logo(source, target):
    """The company logo for Help > About as SVG (the app draws it as vector graphics, sharp at every scaling). A logo
    with a see-through background gets a white card with a margin (readable in the dark theme too): its first shape
    does not cover the whole viewBox."""
    if not source.lower().endswith(".svg"):
        sys.exit("--company-logo needs an SVG; trace a raster logo first: python tools/icon/trace_logo.py logo.png logo.svg")
    with open(source, encoding="utf-8") as f:
        svg = f.read()
    x, y, w, h = map(float, re.search(r'viewBox="([^"]+)"', svg).group(1).replace(",", " ").split())
    first = re.search(r'<path [^>]*\bd="([^"]*)"', svg)
    if first is None or "transform" in svg:
        sys.exit("Unexpected SVG: write it with tools/icon/trace_logo.py (paths with absolute coordinates only)")
    numbers = [float(v) for v in re.findall(r"-?\d+(?:\.\d+)?", first.group(1))]
    xs, ys = numbers[0::2], numbers[1::2]
    opaque = min(xs) <= x + w * 0.01 and max(xs) >= x + w * 0.99 and min(ys) <= y + h * 0.01 and max(ys) >= y + h * 0.99
    if not opaque:
        pad = round(w * CARD_MARGIN, 2)
        l, t, r, b = x - pad, y - pad, x + w + pad, y + h + pad
        svg = re.sub(r'viewBox="[^"]+"', f'viewBox="{l:g} {t:g} {r - l:g} {b - t:g}"', svg, count=1)
        svg = svg.replace("<path ", f'<path fill="#FFFFFF" d="M{l:g} {t:g}L{r:g} {t:g}L{r:g} {b:g}L{l:g} {b:g}Z"/>\n<path ', 1)
    with open(target, "w", encoding="utf-8", newline="\n") as f:
        f.write(svg)
    print(f"Written: {os.path.relpath(target, ROOT)}" + ("" if opaque else " (on a white card)"))


def main():
    global MARK, ACCENT
    parser = argparse.ArgumentParser(description="Draw the icon of a brand, or make it from a logo file.")
    parser.add_argument("--brand", help="brand profile in branding/ (default: the brand the build selects)")
    parser.add_argument("--logo", help="logo file (kept as icons/logo.png of the brand and used by later runs)")
    parser.add_argument("--logo-small", help="optional simpler logo for 16-32 px (kept as icons/logo-small.png)")
    parser.add_argument("--margin", type=float, default=4, help="empty border around the logo, percent of the side (default 4)")
    parser.add_argument("--company-logo", help="company logo (SVG) for Help > About; writes only icons/company-logo.svg")
    args = parser.parse_args()
    brand = args.brand or selected_brand()
    props = os.path.join(ROOT, "branding", brand, "Branding.props")
    if not os.path.isfile(props):
        sys.exit(f"No brand profile {props}")
    icons = os.path.join(ROOT, "branding", brand, "icons")
    if args.company_logo:
        os.makedirs(icons, exist_ok=True)
        company_logo(args.company_logo, os.path.join(icons, "company-logo.svg"))
        return
    logo, logo_small = os.path.join(icons, "logo.png"), os.path.join(icons, "logo-small.png")
    header_logo = os.path.join(icons, "header-logo.png")   # header, 42 px; 256 px stays sharp at high display scaling
    MARK = shade(brand_color(props, "BrandPrimaryColor"), -0.03)
    ACCENT = brand_color(props, "BrandAccentColor")
    os.makedirs(icons, exist_ok=True)
    if args.logo_small and not (args.logo or os.path.exists(logo)):
        sys.exit("--logo-small needs a logo (--logo or icons/logo.png of the brand).")
    if args.logo:
        store_logo(args.logo, logo)
    if args.logo_small:
        store_logo(args.logo_small, logo_small)

    if os.path.exists(logo):
        large = logo_master(load_logo(logo, needed=256), args.margin)
        small = logo_master(load_logo(logo_small, needed=32), args.margin) if os.path.exists(logo_small) else large
        print(f"Icon from the logo {os.path.relpath(logo, ROOT)}" + (" and logo-small.png" if small is not large else ""))
    else:
        large, small = draw_master(simple=False), draw_master(simple=True)
        print(f"Icon drawn (brand mark in the brand colours of {os.path.relpath(props, ROOT)})")

    def render(size):
        return (small if size <= 32 else large).resize((size, size), Image.LANCZOS)

    sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256, 512, 1024]
    images = {s: render(s) for s in sizes}

    # Window/taskbar icon, the Windows .exe icon and the MSI's icon in "Installed apps"
    images[256].save(os.path.join(icons, "app.ico"),
                     sizes=[(s, s) for s in (16, 20, 24, 32, 40, 48, 64, 128, 256)],
                     append_images=[images[s] for s in (16, 20, 24, 32, 40, 48, 64, 128)])
    # About dialog (shown at 64 px, 2x for high-DPI screens) and the Linux hicolor icons
    for s in (16, 24, 32, 48, 64, 128, 256, 512):
        images[s].save(os.path.join(icons, f"app-{s}.png"))
    # macOS
    images[1024].save(os.path.join(icons, "app.icns"))
    # Header: the logo instead of the mark drawn in XAML; none for the drawn icon
    if os.path.exists(logo):
        images[256].save(header_logo)
    elif os.path.exists(header_logo):
        os.remove(header_logo)

    # Preview sheet (not shipped)
    sheet = Image.new("RGBA", (1100, 300), (240, 240, 240, 255))
    x = 10
    for s in (16, 24, 32, 48, 64, 128, 256):
        sheet.alpha_composite(images[s], (x, 280 - s))
        x += s + 20
    preview = os.path.join(ROOT, "artifacts", f"icon-preview-{brand}.png")
    os.makedirs(os.path.dirname(preview), exist_ok=True)
    sheet.save(preview)
    print(f"Written: {os.path.relpath(icons, ROOT)}; look at {os.path.relpath(preview, ROOT)}")


if __name__ == "__main__":
    main()
