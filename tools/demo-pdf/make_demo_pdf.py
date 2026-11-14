"""Writes the demo PDF demo-pdfs/Annual Report 2025.pdf: the annual report of a made-up energy cooperative in a made-up
valley. Every name, place and figure is invented; the document is written from scratch so the repository holds no
third-party content. Only the Python standard library is needed.

What the tests and the manual checks need from it (keep these when changing it):
- cover: a full-page picture, a box in a spot colour (Separation colour space, the case where pdfium's content generator
  once turned a box black, OverlayContentTests) and white text on it
- contents with dot leaders and page numbers (hard for OCR)
- pages of real text in the standard fonts (words to select, copy, search and black out, RedactionTests)
- tables drawn with lines, bar charts, a second smaller picture, one landscape page with a drawing (different page sizes)
- no bookmarks (tests that need some write their own PDFs)

Run:  python tools/demo-pdf/make_demo_pdf.py      (the output is deterministic; commit the PDF)
"""
import math
import os
import zlib

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
OUT = os.path.join(ROOT, "demo-pdfs", "Annual Report 2025.pdf")

A4 = (595.28, 841.89)
A4_LANDSCAPE = (841.89, 595.28)
MARGIN = 62
WIDTH = A4[0] - 2 * MARGIN
ORG = "Brightwater Valley Energy Cooperative"
TITLE = f"{ORG} - Annual Report 2025"

GREEN = (0.13, 0.45, 0.27)
SUN = (0.96, 0.69, 0.13)
WATER = (0.22, 0.52, 0.78)
WIND = (0.45, 0.62, 0.70)
GREY = (0.45, 0.45, 0.45)
TABLE_HEAD = (0.88, 0.93, 0.89)

# Advance widths of the standard fonts (1/1000 em), for line breaking and right alignment
_HELV = dict(zip(" !\"#$%&'()*+,-./0123456789:;<=>?@", [278, 278, 355, 556, 556, 889, 667, 191, 333, 333, 389, 584, 278, 333, 278, 278] + [556] * 10 + [278, 278, 584, 584, 584, 556, 1015]))
_HELV.update(zip("ABCDEFGHIJKLMNOPQRSTUVWXYZ", [667, 667, 722, 722, 667, 611, 778, 722, 278, 500, 667, 556, 833, 722, 778, 667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611]))
_HELV.update(zip("abcdefghijklmnopqrstuvwxyz", [556, 556, 500, 556, 556, 278, 556, 556, 222, 222, 500, 222, 833, 556, 556, 556, 556, 333, 500, 278, 556, 500, 722, 500, 500, 500]))
_BOLD = dict(_HELV)
_BOLD.update(zip("abcdefghijklmnopqrstuvwxyz", [556, 611, 556, 611, 556, 333, 611, 611, 278, 278, 556, 278, 889, 611, 611, 611, 611, 389, 556, 333, 611, 556, 778, 556, 556, 500]))
_BOLD.update({"A": 722, "B": 722, "J": 556, "K": 722, "L": 611, "!": 333, ":": 333, ";": 333})
WIDTHS = {"F1": _HELV, "F2": _BOLD}


def text_width(text, font, size):
    table = WIDTHS[font]
    return sum(table.get(c, 556) for c in text) * size / 1000


def esc(text):
    return text.replace("\\", "\\\\").replace("(", "\\(").replace(")", "\\)")


def wrap(text, font, size, width):
    lines, line = [], ""
    for word in text.split():
        candidate = word if not line else line + " " + word
        if text_width(candidate, font, size) <= width:
            line = candidate
        else:
            lines.append(line)
            line = word
    if line:
        lines.append(line)
    return lines


def num(value):
    """Thousands with a comma, negative numbers with a minus sign: 1,234 / -56."""
    return f"{value:,}"


class Page:
    def __init__(self, size=A4):
        self.size = size
        self.ops = []
        self.images = {}
        self.uses_spot = False

    def text(self, x, y, text, font="F1", size=10.5, rgb=(0.12, 0.12, 0.12)):
        """One text object per call (pdfium makes one page object per text showing operator anyway)."""
        self.ops.append(f"BT /{font} {size:g} Tf {rgb[0]:g} {rgb[1]:g} {rgb[2]:g} rg {x:.2f} {y:.2f} Td ({esc(text)}) Tj ET")

    def text_right(self, right, y, text, font="F1", size=10.5, rgb=(0.12, 0.12, 0.12)):
        self.text(right - text_width(text, font, size), y, text, font, size, rgb)

    def text_center(self, center, y, text, font="F1", size=10.5, rgb=(0.12, 0.12, 0.12)):
        self.text(center - text_width(text, font, size) / 2, y, text, font, size, rgb)

    def rect(self, x, y, w, h, fill=None, stroke=None, width=0.6):
        ops = []
        if fill:
            ops.append(f"{fill[0]:g} {fill[1]:g} {fill[2]:g} rg")
        if stroke:
            ops.append(f"{stroke[0]:g} {stroke[1]:g} {stroke[2]:g} RG {width:g} w")
        ops.append(f"{x:.2f} {y:.2f} {w:.2f} {h:.2f} re " + ("B" if fill and stroke else "f" if fill else "S"))
        self.ops.append("q " + " ".join(ops) + " Q")

    def line(self, x1, y1, x2, y2, rgb=(0.6, 0.6, 0.6), width=0.6):
        self.ops.append(f"q {rgb[0]:g} {rgb[1]:g} {rgb[2]:g} RG {width:g} w {x1:.2f} {y1:.2f} m {x2:.2f} {y2:.2f} l S Q")

    def path(self, points, fill=None, stroke=None, width=0.6, close=True, smooth=False):
        """A polygon or polyline; smooth=True draws a Catmull-Rom curve through the points (for rivers and roads)."""
        ops = ["1 J 1 j"]
        if fill:
            ops.append(f"{fill[0]:g} {fill[1]:g} {fill[2]:g} rg")
        if stroke:
            ops.append(f"{stroke[0]:g} {stroke[1]:g} {stroke[2]:g} RG {width:g} w")
        ops.append(f"{points[0][0]:.2f} {points[0][1]:.2f} m")
        if smooth:
            pts = [points[0]] + list(points) + [points[-1]]
            for i in range(1, len(pts) - 2):
                (x0, y0), (x1, y1), (x2, y2), (x3, y3) = pts[i - 1], pts[i], pts[i + 1], pts[i + 2]
                ops.append(f"{x1 + (x2 - x0) / 6:.2f} {y1 + (y2 - y0) / 6:.2f} {x2 - (x3 - x1) / 6:.2f} {y2 - (y3 - y1) / 6:.2f} "
                           f"{x2:.2f} {y2:.2f} c")
        else:
            ops += [f"{x:.2f} {y:.2f} l" for x, y in points[1:]]
        paint = "B" if fill and stroke else "f" if fill else "S"
        ops.append(("h " if close else "") + paint)
        self.ops.append("q " + " ".join(ops) + " Q")

    def circle(self, cx, cy, r, fill=None, stroke=None, width=0.6):
        k = 0.5523 * r
        ops = []
        if fill:
            ops.append(f"{fill[0]:g} {fill[1]:g} {fill[2]:g} rg")
        if stroke:
            ops.append(f"{stroke[0]:g} {stroke[1]:g} {stroke[2]:g} RG {width:g} w")
        ops.append(f"{cx + r:.2f} {cy:.2f} m "
                   f"{cx + r:.2f} {cy + k:.2f} {cx + k:.2f} {cy + r:.2f} {cx:.2f} {cy + r:.2f} c "
                   f"{cx - k:.2f} {cy + r:.2f} {cx - r:.2f} {cy + k:.2f} {cx - r:.2f} {cy:.2f} c "
                   f"{cx - r:.2f} {cy - k:.2f} {cx - k:.2f} {cy - r:.2f} {cx:.2f} {cy - r:.2f} c "
                   f"{cx + k:.2f} {cy - r:.2f} {cx + r:.2f} {cy - k:.2f} {cx + r:.2f} {cy:.2f} c "
                   + ("B" if fill and stroke else "f" if fill else "S"))
        self.ops.append("q " + " ".join(ops) + " Q")

    def spot_rect(self, x, y, w, h, tint=1.0):
        self.uses_spot = True
        self.ops.append(f"q /CS1 cs {tint:g} scn {x:.2f} {y:.2f} {w:.2f} {h:.2f} re f Q")

    def image(self, name, x, y, w, h):
        self.images[name] = True
        self.ops.append(f"q {w:.2f} 0 0 {h:.2f} {x:.2f} {y:.2f} cm /{name} Do Q")


# ---- pictures, drawn pixel by pixel -------------------------------------------------------------------------------

def _mix(a, b, t):
    t = max(0.0, min(1.0, t))
    return tuple(a[i] + (b[i] - a[i]) * t for i in range(3))


def cover_picture(w=420, h=594):
    """Sunrise over a valley: sky gradient, the sun, two rows of hills and rows of solar panels in the foreground."""
    rows = bytearray()
    horizon = 0.50 * h
    for y in range(h):
        for x in range(w):
            if y < horizon:
                c = _mix((28, 52, 110), (250, 170, 90), (y / horizon) ** 1.6)
                d = math.hypot(x - 0.68 * w, y - horizon + 18)
                if d < 26:
                    c = (255, 236, 180)
                else:
                    c = _mix(c, (255, 214, 140), 0.55 * math.exp(-(d - 26) / 60))
            else:
                c = (250, 190, 120)
            far = horizon - 22 + 16 * math.sin(x / 47 + 1.3) + 9 * math.sin(x / 19)
            near = horizon + 30 + 24 * math.sin(x / 83 + 0.4) + 6 * math.sin(x / 23 + 2)
            if y > far:
                c = _mix((86, 104, 120), (60, 92, 80), (y - far) / 80)
            if y > near:
                c = _mix((52, 104, 58), (34, 78, 44), (y - near) / 200)
            panels = 0.66 * h
            if y > panels:
                row = (y - panels) / 34
                k = row - int(row)
                if k < 0.62:
                    cell_x = (x + 0.4 * (y - panels)) % 18 < 1.4
                    cell_y = k * 34 % 7 < 1.0
                    shade = 0.25 + 0.5 * k
                    c = (235, 235, 240) if cell_x or cell_y else _mix((22, 40, 90), (60, 96, 160), shade)
            rows += bytes(int(v) for v in c)
    return w, h, bytes(rows)


def turbine_picture(w=360, h=240):
    """A wind turbine on a green hill under a light sky with two soft clouds."""
    hub = (200.0, 74.0)
    blades = [math.radians(a) for a in (100, 220, 340)]

    def dist(px, py, ax, ay, bx, by):
        dx, dy = bx - ax, by - ay
        t = max(0.0, min(1.0, ((px - ax) * dx + (py - ay) * dy) / (dx * dx + dy * dy)))
        return math.hypot(px - ax - t * dx, py - ay - t * dy), t

    rows = bytearray()
    for y in range(h):
        for x in range(w):
            c = _mix((120, 170, 225), (215, 232, 245), y / h)
            for cx, cy, rx, ry in ((80, 50, 46, 14), (290, 34, 38, 10)):
                e = ((x - cx) / rx) ** 2 + ((y - cy) / ry) ** 2
                if e < 1.6:
                    c = _mix(c, (250, 250, 252), min(1.0, 1.6 - e))
            hill = 190 - 26 * math.cos((x - 200) / 140)
            if y > hill:
                c = _mix((96, 160, 80), (60, 120, 56), (y - hill) / 60)
            if 72 < y < hill + 4 and abs(x - hub[0]) < 2 + 3 * (y - 72) / (hill - 72):
                c = (238, 240, 242) if x < hub[0] + 1 else (205, 210, 214)
            for a in blades:
                bx, by = hub[0] + 70 * math.cos(a), hub[1] - 70 * math.sin(a)
                d, t = dist(x, y, hub[0], hub[1], bx, by)
                if d < 4.2 * (1 - 0.75 * t):
                    c = (244, 245, 247)
            if (x - hub[0] - 4) ** 2 / 64 + (y - hub[1]) ** 2 / 16 < 1:
                c = (225, 228, 232)
            if math.hypot(x - hub[0], y - hub[1]) < 4:
                c = (190, 195, 200)
            rows += bytes(int(v) for v in c)
    return w, h, bytes(rows)


# ---- the figures -------------------------------------------------------------------------------------------------

MONTHS = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"]
PROFILE = {   # share of the annual production per month, in per cent
    "Solar": [2.4, 4.4, 7.9, 11.1, 13.6, 14.2, 14.1, 12.0, 9.0, 6.0, 3.2, 2.1],
    "Wind": [12.4, 11.2, 10.1, 8.0, 6.0, 5.1, 4.9, 5.6, 7.1, 8.9, 9.9, 10.8],
    "Water": [10.8, 11.4, 12.2, 10.6, 8.4, 6.3, 5.0, 4.6, 5.7, 7.6, 8.4, 9.0],
}
# name, kind, place, capacity, unit, commissioned, investment (EUR thousand), yield per kW in 2025, availability %
PLANTS = [
    ("Brackenmoor Solar Park", "Solar", "Brackenmoor", 4800, "kWp", 2019, 3950, 978, 99.2),
    ("Wrenfield School Roof", "Solar", "Wrenfield", 186, "kWp", 2016, 248, 921, 99.6),
    ("Millbrook Fire Station", "Solar", "Millbrook", 92, "kWp", 2017, 121, 905, 98.9),
    ("Ferncombe Barn Roofs", "Solar", "Ferncombe", 410, "kWp", 2023, 402, 947, 99.4),
    ("Larkhaven Wind Turbine", "Wind", "Larkhaven", 3200, "kW", 2021, 4620, 2140, 97.6),
    ("Hollin Vale Weir", "Water", "Hollin Vale", 240, "kW", 2014, 890, 4460, 95.8),
]
KIND_NAME = {"Solar": "Solar power", "Wind": "Wind power", "Water": "Run-of-river hydro"}
CO2_PER_MWH = 0.42


def production(plant):
    return round(plant[3] * plant[7] / 1000)


def monthly(total, kind):
    """Monthly values that add up to the annual total exactly."""
    values = [round(total * p / 100) for p in PROFILE[kind]]
    values[6] += total - sum(values)
    return values


BY_KIND = {k: sum(production(p) for p in PLANTS if p[1] == k) for k in PROFILE}
BY_MONTH = {k: monthly(v, k) for k, v in BY_KIND.items()}
TOTAL_MWH = sum(BY_KIND.values())
TOTAL_2024 = 12418
CO2 = round(TOTAL_MWH * CO2_PER_MWH)

MEMBERS = [   # municipality, members at year end, joined in 2025
    ("Millbrook", 684, 52), ("Wrenfield", 412, 31), ("Brackenmoor", 356, 28), ("Hollin Vale", 298, 19),
    ("Larkhaven", 271, 22), ("Ferncombe", 219, 24), ("Elsewhere", 172, 11),
]
MEMBER_TOTAL = sum(m[1] for m in MEMBERS)
MEMBER_NEW = sum(m[2] for m in MEMBERS)
MEMBER_HISTORY = [("2019", 1410), ("2020", 1582), ("2021", 1806), ("2022", 1977), ("2023", 2108), ("2024", 2225),
                  ("2025", MEMBER_TOTAL)]

INCOME = [   # EUR thousand: 2025, 2024
    ("Electricity sales", 2142, 1968),
    ("Grid services and battery", 96, 0),
    ("Other income", 38, 41),
    ("Operation and maintenance", -412, -389),
    ("Land lease and roof rent", -164, -158),
    ("Personnel", -286, -251),
    ("Depreciation", -918, -842),
    ("Interest", -133, -147),
    ("Taxes", -71, -63),
]
RESULT = sum(r[1] for r in INCOME)
RESULT_2024 = sum(r[2] for r in INCOME)
SHARES = 6030
BALANCE_ASSETS = [("Plants and equipment", 11840), ("Receivables", 312), ("Cash at bank", 1406)]
BALANCE_EQUITY = [("Members' shares", SHARES), ("Reserves", 1214), ("Result for the year", RESULT), ("Bank loans", 5540),
                  ("Other liabilities", 482)]
DIVIDEND = 4.0
assert sum(v for _, v in BALANCE_ASSETS) == sum(v for _, v in BALANCE_EQUITY)


# ---- the text ----------------------------------------------------------------------------------------------------

FOREWORD = [
    "Dear members, 2025 was the year in which our cooperative grew up. With the battery at Brackenmoor we now store "
    "our own midday sun for the evening, and for the first time the valley produced more electricity from our plants "
    "than all its households used in a whole year. None of this would have been possible without you.",
    f"Our six plants delivered {TOTAL_MWH / 1000:.1f} gigawatt hours, {(TOTAL_MWH / TOTAL_2024 - 1) * 100:.0f} per cent more "
    "than in the year before. A sunny spring made up for a calm summer, and the weir at Hollin Vale ran almost without "
    "interruption after the new screen was fitted in March. The wind turbine on Larkhaven ridge was stopped for eleven "
    "days in October for a planned gearbox inspection.",
    f"We welcomed {MEMBER_NEW} new members, among them our first school class, which bought a share with the money "
    "from its summer fair. Many of you came to the open days at the solar park, helped with the tree planting along "
    "the river or explained our work at the market stall in Millbrook. Thank you for your time and your trust.",
    f"The board proposes a dividend of {DIVIDEND:.1f} per cent on the members' shares. At the same time we keep building "
    "reserves, because the next steps are larger than anything we have done so far: a second solar park on Kestrel "
    "Field and the renewal of the turbines at the weir. We will present both projects at the general assembly.",
]

CHAPTERS = [
    ("Our plants", [
        ("Overview", [
            "The cooperative owns and runs six generating plants and one battery. All of them stand in the valley, "
            "within twelve kilometres of each other, and all of them were financed mainly by our members. The map on "
            "the landscape page shows where they are; the plant sheets in the appendix give the details.",
            "Most of the power is sold to the regional utility under long-term contracts. A growing share goes "
            "directly to local businesses, the schools and the swimming pool in Millbrook, which buy it at a fixed "
            "price for ten years.",
        ]),
        ("Solar power", [
            "Four solar plants produce about forty per cent of our electricity. The largest is the solar park on the "
            "former gravel pit at Brackenmoor, where sheep keep the grass short between the panel rows. The three roof "
            "plants on the school, the fire station and the barns at Ferncombe are smaller but very visible, and they "
            "have done a lot to make the cooperative known.",
            "In 2025 we cleaned all modules for the first time with a soft brush machine instead of water jets. The "
            "yield of the solar park rose by almost two per cent compared with the plants that were cleaned later.",
        ]),
        ("Wind and water", [
            "The wind turbine on Larkhaven ridge is our strongest single plant. It turns at wind speeds from three "
            "metres per second and reaches its rated power at twelve. A bat protection program stops it on warm, calm "
            "nights in late summer; the loss is small and the monitoring results are encouraging.",
            "The weir at Hollin Vale is the oldest plant of the cooperative. Its two turbines date from the nineteen "
            "eighties and will be replaced by a single modern unit with a fish ladder in 2027.",
        ]),
    ]),
    ("Production", [
        ("Annual production", [
            f"Together our plants produced {num(TOTAL_MWH)} megawatt hours of electricity, enough for about "
            f"{round(TOTAL_MWH / 2.9 / 100) * 100:,} average households. Compared with electricity from the national "
            f"grid mix this avoided roughly {num(CO2)} tonnes of carbon dioxide.",
            "Wind power produced the most in winter, solar power in the summer months, and the weir delivered a steady "
            "base in between. The chart and the table below show how the three sources complement each other over "
            "the year.",
        ]),
        ("Monthly figures", []),
    ]),
    ("Members", [
        ("Development", [
            f"At the end of the year the cooperative had {num(MEMBER_TOTAL)} members, {MEMBER_NEW} more than a year before. "
            "Twenty-three members left, most of them because they moved away from the valley. The average holding is "
            "ten shares of two hundred and fifty euros each.",
            "Membership remains open to everyone who lives or works in the valley. People from elsewhere may join "
            "when they are recommended by a member; this keeps the cooperative local without closing the door.",
        ]),
        ("Members by municipality", []),
        ("Community projects", [
            "Every year one per cent of the electricity sales goes to the community fund. In 2025 it paid for new "
            "lights in the sports hall at Wrenfield, a bicycle repair station in Millbrook and two hundred native "
            "trees along the river, planted by volunteers on a rainy Saturday in November.",
        ]),
    ]),
    ("Projects 2025", [
        ("Battery storage at Brackenmoor", [
            "In May the battery next to the solar park went into operation. It stores two megawatt hours and can "
            "deliver one megawatt for two hours. On sunny days it charges at noon and feeds the power back in the "
            "early evening, when demand and prices are highest.",
            "In addition the battery helps to keep the grid frequency stable. The grid operator pays for this "
            "service, and the payments already cover a good part of the running costs.",
        ]),
        ("Car sharing", [
            "Six electric cars are now available to members in four villages. They are booked with a phone or at the "
            "village shop and charged with power from our own plants. In the first full year they covered more than "
            "ninety thousand kilometres.",
        ]),
        ("Energy school", [
            "Our volunteers visited eleven school classes and showed them how a solar cell works, why a turbine has "
            "three blades and how much energy is in a slice of bread. The workshops are free of charge and fully "
            "booked for next year.",
        ]),
    ]),
    ("Finances", [
        ("Income statement", [
            f"The cooperative closes the year with a result of {num(RESULT)} thousand euros, compared with "
            f"{num(RESULT_2024)} thousand euros in 2024. Higher production and the first income from the battery more "
            "than made up for rising maintenance costs and the depreciation of the new plant.",
        ]),
        ("Balance sheet", [
            "Members' shares and reserves finance more than half of all assets. The bank loans are repaid on schedule; "
            "the last loan for the weir was paid off in December.",
        ]),
        ("Appropriation of the result", [
            f"The board proposes to pay a dividend of {DIVIDEND:.1f} per cent on the members' shares, which amounts to "
            f"{num(round(SHARES * DIVIDEND / 100))} thousand euros, and to transfer the remaining "
            f"{num(RESULT - round(SHARES * DIVIDEND / 100))} thousand euros to the reserves.",
        ]),
    ]),
    ("Governance and outlook", [
        ("Board and supervisory council", [
            "The board consists of three members elected for four years: Margaret Ellison (chair), Tobias Renwick "
            "(finance) and Priya Halden (technology). The supervisory council has seven members and met six times "
            "during the year. The general assembly in June was attended by four hundred and twelve members.",
        ]),
        ("Outlook", [
            "For Kestrel Field we expect the building permit in the spring. The planned solar park will have a "
            "capacity of six megawatts and will be offered to members as a separate share class from autumn.",
            "The renewal of the weir at Hollin Vale needs a new water permit and a fish ladder. Planning has started "
            "together with the angling club and the nature conservation authority.",
            "For 2026 we expect a similar production and a slightly lower result because of the planning costs for "
            "both projects. The board is confident that the dividend can be kept at the current level.",
        ]),
    ]),
]


# ---- layout ------------------------------------------------------------------------------------------------------

class Flow:
    """Places headings, paragraphs, tables and charts one below the other and starts new pages when needed."""

    def __init__(self, pages, toc):
        self.pages, self.toc = pages, toc
        self.page, self.y = None, 0

    def new_page(self, size=A4):
        self.page = Page(size)
        self.pages.append(self.page)
        self.y = size[1] - 90
        return self.page

    def need(self, height):
        if self.page is None or self.y - height < 70:
            self.new_page()

    def chapter(self, label, title):
        self.new_page()
        self.toc.append((f"{label}  {title.upper()}", len(self.pages), True))
        self.page.text(MARGIN, self.y, f"{label}  {title}", "F2", 20, GREEN)
        self.y -= 40

    def section(self, label, after=80):
        self.need(after)
        self.toc.append((label, len(self.pages), False))
        self.page.text(MARGIN, self.y, label, "F2", 12.5)
        self.y -= 22

    def paragraph(self, text):
        for line in wrap(text, "F1", 10.5, WIDTH):
            self.need(15)
            self.page.text(MARGIN, self.y, line, "F1", 10.5)
            self.y -= 15
        self.y -= 9

    def table(self, columns, rows, total=False, size=9.5):
        """columns: (header, width, 'l' or 'r'); the last row is bold when total is set."""
        height = 20 * (len(rows) + 1)
        self.need(height + 10)
        page, top = self.page, self.y + 4
        xs = [MARGIN]
        for _, w, _ in columns:
            xs.append(xs[-1] + w)
        page.rect(xs[0], top - 20, xs[-1] - xs[0], 20, fill=TABLE_HEAD)
        for i, row in enumerate([tuple(c[0] for c in columns)] + rows):
            base = top - 20 * i - 14
            font = "F2" if i == 0 or (total and i == len(rows)) else "F1"
            for c, (cell, (_, _, align)) in enumerate(zip(row, columns)):
                if align == "l":
                    page.text(xs[c] + 6, base, cell, font, size)
                else:
                    page.text_right(xs[c + 1] - 6, base, cell, font, size)
            page.line(xs[0], top - 20 * (i + 1), xs[-1], top - 20 * (i + 1), (0.5, 0.5, 0.5))
        page.line(xs[0], top, xs[-1], top, (0.5, 0.5, 0.5))
        for x in xs:
            page.line(x, top, x, top - height, (0.5, 0.5, 0.5))
        self.y = top - height - 24

    def chart(self, labels, series, unit, step, height=190):
        self.need(height + 50)
        bar_chart(self.page, MARGIN + 40, self.y - height, WIDTH - 40, height, labels, series, unit, step)
        self.y -= height + 54


def bar_chart(page, x, y, w, h, labels, series, unit, step):
    """Stacked bars; series: (name, colour, values). The legend goes below the month labels."""
    top = math.ceil(max(sum(v) for v in zip(*(s[2] for s in series))) / step) * step
    for v in range(0, top + 1, step):
        gy = y + h * v / top
        page.line(x, gy, x + w, gy, (0.85, 0.85, 0.85) if v else (0.3, 0.3, 0.3), 0.5)
        page.text_right(x - 6, gy - 3, num(v), "F1", 8, GREY)
    page.text(x - 40, y + h + 12, unit, "F1", 8, GREY)
    slot = w / len(labels)
    for i, label in enumerate(labels):
        base = y
        for _, colour, values in series:
            bh = h * values[i] / top
            page.rect(x + i * slot + slot * 0.18, base, slot * 0.64, bh, fill=colour)
            base += bh
        page.text_center(x + (i + 0.5) * slot, y - 14, label, "F1", 8.5)
    lx = x
    for name, colour, _ in series:
        if len(series) > 1:
            page.rect(lx, y - 36, 9, 9, fill=colour)
            page.text(lx + 14, y - 35, name, "F1", 8.5)
            lx += 30 + text_width(name, "F1", 8.5)


def header_footer(page, number):
    w, h = page.size
    page.text(MARGIN, h - 40, TITLE, "F1", 8, GREY)
    page.line(MARGIN, h - 46, w - MARGIN, h - 46)
    page.line(MARGIN, 46, w - MARGIN, 46)
    page.text_right(w - MARGIN, 34, f"Page {number}", "F1", 8, GREY)


def key_figure(page, x, y, w, value, label, colour):
    page.rect(x, y, w, 92, fill=colour)
    page.text(x + 16, y + 50, value, "F2", 26, (1, 1, 1))
    page.text(x + 16, y + 22, label, "F1", 10, (1, 1, 1))


def plant_sheet(flow, n, plant):
    name, kind, place, capacity, unit, year, invest, specific, availability = plant
    mwh = production(plant)
    flow.new_page()
    if n == 1:
        flow.toc.append(("A  PLANT SHEETS", len(flow.pages), True))
        flow.page.text(MARGIN, flow.y, "A  Plant sheets", "F2", 20, GREEN)
        flow.y -= 40
    flow.section(f"A.{n}  {name}")
    flow.y -= 8
    yield_label = "Full-load hours" if kind != "Solar" else "Specific yield"
    yield_value = f"{num(specific)} h" if kind != "Solar" else f"{num(specific)} kWh/kWp"
    facts = [("Type", KIND_NAME[kind]), ("Location", place), ("Capacity", f"{num(capacity)} {unit}"),
             ("In operation since", str(year)), ("Investment", f"{num(invest)} thousand euros"),
             ("Production 2025", f"{num(mwh)} MWh"), (yield_label, yield_value),
             ("Availability", f"{availability:.1f} %"), ("Carbon dioxide avoided", f"{num(round(mwh * CO2_PER_MWH))} t")]
    for label, value in facts:
        flow.page.text(MARGIN, flow.y, label, "F2", 10.5)
        flow.page.text(MARGIN + 170, flow.y, value, "F1", 10.5)
        flow.page.line(MARGIN, flow.y - 6, A4[0] - MARGIN, flow.y - 6, (0.82, 0.82, 0.82))
        flow.y -= 24
    flow.y -= 30
    flow.page.text(MARGIN, flow.y, "Production per month in 2025", "F2", 10.5)
    flow.y -= 30
    values = monthly(mwh, kind)
    step = next(s for s in (5, 10, 20, 50, 100, 200, 500, 1000) if max(values) / s <= 6)
    colour = {"Solar": SUN, "Wind": WIND, "Water": WATER}[kind]
    flow.chart(MONTHS, [(kind, colour, values)], "MWh", step, height=160)
    note = (f"The plant {name} was built in {year} and has been owned by the cooperative ever since. In 2025 it "
            f"was available on {availability:.1f} per cent of the time; "
            + ("planned maintenance accounted for most of the downtime. " if availability < 99 else "there were no unplanned stops. ")
            + f"Its production of {num(mwh)} megawatt hours equals the annual consumption of about "
            f"{round(mwh / 2.9 / 10) * 10:,} households in the valley.")
    flow.paragraph(note)


def valley_map(page):
    lw, lh = page.size
    page.text(MARGIN, lh - 90, "9  Map of the valley", "F2", 20, GREEN)
    left, bottom, right, top = MARGIN, 70, lw - MARGIN - 190, lh - 120
    page.rect(left, bottom, right - left, top - bottom, fill=(0.96, 0.97, 0.93), stroke=(0.5, 0.5, 0.5))
    for cx, cy, r in ((150, 380, 34), (182, 352, 26), (390, 160, 30), (420, 134, 22), (310, 160, 22)):
        page.circle(cx, cy, r, fill=(0.80, 0.90, 0.76))
    page.path([(left, 300), (130, 290), (210, 320), (300, 270), (380, 250), (460, 290), (540, 270), (right, 230)],
              stroke=WATER, width=5, close=False, smooth=True)
    page.path([(120, bottom), (170, 200), (250, 230), (330, 310), (420, 330), (480, 380), (500, top)],
              stroke=(0.62, 0.62, 0.62), width=2.2, close=False, smooth=True)
    towns = [("Millbrook", 330, 316), ("Wrenfield", 190, 226), ("Hollin Vale", 452, 300), ("Brackenmoor", 500, 400),
             ("Larkhaven", 250, 420), ("Ferncombe", 520, 200)]
    for name, x, y in towns:
        page.circle(x, y, 6, fill=(0.95, 0.95, 0.95), stroke=(0.2, 0.2, 0.2), width=1)
        page.text(x + 10, y + 6, name, "F2", 9.5)
    # Plant symbols: square = solar, triangle = wind, circle = water, bar = battery
    for x, y in ((528, 380), (206, 208), (350, 292), (536, 184)):
        page.rect(x - 6, y - 6, 12, 12, fill=SUN, stroke=(0.3, 0.3, 0.3))
    page.path([(230, 452), (222, 438), (238, 438)], fill=WIND, stroke=(0.3, 0.3, 0.3))
    page.circle(466, 286, 6, fill=WATER, stroke=(0.3, 0.3, 0.3))
    page.rect(542, 376, 16, 8, fill=GREEN, stroke=(0.3, 0.3, 0.3))
    page.path([(470, 140), (500, 160), (540, 140), (520, 100), (480, 104)], stroke=SUN, width=1.2)
    page.text(462, 86, "Kestrel Field (planned)", "F1", 8.5, GREY)
    # Scale bar and north arrow
    page.line(left + 190, bottom + 20, left + 290, bottom + 20, (0.2, 0.2, 0.2), 1.5)
    for i, label in enumerate(("0", "2", "4 km")):
        page.line(left + 190 + 50 * i, bottom + 16, left + 190 + 50 * i, bottom + 24, (0.2, 0.2, 0.2), 1.2)
        page.text(left + 187 + 50 * i, bottom + 28, label, "F1", 8)
    page.path([(right - 30, top - 20), (right - 38, top - 44), (right - 30, top - 38), (right - 22, top - 44)],
              fill=(0.2, 0.2, 0.2))
    page.text(right - 34, top - 58, "N", "F2", 10)
    # Legend
    x, y = right + 24, top - 10
    page.text(x, y, "Legend", "F2", 11)
    entries = [("Solar plant", lambda yy: page.rect(x, yy - 3, 12, 12, fill=SUN, stroke=(0.3, 0.3, 0.3))),
               ("Wind turbine", lambda yy: page.path([(x + 6, yy + 10), (x, yy - 3), (x + 12, yy - 3)], fill=WIND, stroke=(0.3, 0.3, 0.3))),
               ("Hydro plant", lambda yy: page.circle(x + 6, yy + 3, 6, fill=WATER, stroke=(0.3, 0.3, 0.3))),
               ("Battery storage", lambda yy: page.rect(x - 2, yy - 1, 16, 8, fill=GREEN, stroke=(0.3, 0.3, 0.3))),
               ("Village", lambda yy: page.circle(x + 6, yy + 3, 6, fill=(0.95, 0.95, 0.95), stroke=(0.2, 0.2, 0.2), width=1)),
               ("River Bright", lambda yy: page.line(x - 2, yy + 3, x + 14, yy + 3, WATER, 4)),
               ("Valley road", lambda yy: page.line(x - 2, yy + 3, x + 14, yy + 3, (0.62, 0.62, 0.62), 2.2)),
               ("Woodland", lambda yy: page.circle(x + 6, yy + 3, 7, fill=(0.80, 0.90, 0.76)))]
    for i, (label, draw) in enumerate(entries):
        yy = y - 30 - 26 * i
        draw(yy)
        page.text(x + 24, yy, label, "F1", 9.5)


def build():
    pages, toc = [], []

    # Cover: picture, spot-colour box, white text on it
    cover = Page()
    w, h = cover.size
    cover.image("Im1", 0, 0, w, h)
    cover.rect(0, h - 64, w, 64, fill=GREEN)
    cover.text(MARGIN, h - 40, ORG.upper(), "F2", 11, (1, 1, 1))
    cover.spot_rect(MARGIN, 150, 310, 150)
    cover.text(MARGIN + 18, 255, "ANNUAL REPORT", "F2", 28, (1, 1, 1))
    cover.text(MARGIN + 18, 222, "2025", "F2", 28, (1, 1, 1))
    cover.text(MARGIN + 18, 190, "Power from the valley, for the valley", "F1", 10, (1, 1, 1))
    cover.text(MARGIN + 18, 176, "Presented to the general assembly on 13 June 2026", "F1", 9, (1, 1, 1))
    pages.append(cover)

    contents = Page()   # filled in at the end, when the page numbers are known
    pages.append(contents)
    flow = Flow(pages, toc)

    # Foreword with the turbine picture
    flow.chapter("1", "Foreword of the board")
    flow.page.image("Im2", MARGIN, flow.y - 150, 225, 150)
    flow.page.text(MARGIN + 245, flow.y - 20, "The Larkhaven turbine", "F2", 10.5)
    for i, line in enumerate(wrap("Our strongest plant produced half of our electricity in 2025.", "F1", 9.5, WIDTH - 245)):
        flow.page.text(MARGIN + 245, flow.y - 38 - 13 * i, line, "F1", 9.5, GREY)
    flow.y -= 180
    for paragraph in FOREWORD:
        flow.paragraph(paragraph)
    flow.y -= 10
    flow.page.text(MARGIN, flow.y, "Margaret Ellison", "F2", 10.5)
    flow.page.text(MARGIN, flow.y - 15, "Chair of the board", "F1", 10.5)

    # The year at a glance
    flow.chapter("2", "The year at a glance")
    box = (WIDTH - 16) / 2
    for i, (value, label, colour) in enumerate([
            (f"{TOTAL_MWH / 1000:.1f} GWh", "electricity produced", GREEN),
            (num(MEMBER_TOTAL), f"members ({MEMBER_NEW} new)", WIND),
            (f"{num(CO2)} t", "carbon dioxide avoided", WATER),
            (f"{DIVIDEND:.1f} %", "proposed dividend", (0.80, 0.50, 0.08))]):
        key_figure(flow.page, MARGIN + (i % 2) * (box + 16), flow.y - 92 - (i // 2) * 108, box, value, label, colour)
    flow.y -= 240
    flow.page.text(MARGIN, flow.y, "Highlights", "F2", 12.5)
    flow.y -= 24
    for item in ["Battery storage at Brackenmoor in operation since May",
                 f"Production up {(TOTAL_MWH / TOTAL_2024 - 1) * 100:.0f} per cent on the previous year",
                 "Six electric cars for members in four villages",
                 "Last bank loan for the Hollin Vale weir paid off",
                 "Building permit for Kestrel Field expected in spring 2026"]:
        flow.page.circle(MARGIN + 4, flow.y + 3.5, 2.2, fill=GREEN)
        flow.page.text(MARGIN + 16, flow.y, item, "F1", 10.5)
        flow.y -= 20

    for number, (title, sections) in enumerate(CHAPTERS, 3):
        flow.chapter(str(number), title)
        for sub, (heading, paragraphs) in enumerate(sections, 1):
            flow.section(f"{number}.{sub}  {heading}", 300 if not paragraphs else 80)
            for paragraph in paragraphs:
                flow.paragraph(paragraph)
            if heading == "Monthly figures":
                flow.chart(MONTHS, [(k, c, BY_MONTH[k]) for k, c in (("Wind", WIND), ("Solar", SUN), ("Water", WATER))],
                           "MWh", 250)
                flow.need(300)
                flow.page.text(MARGIN, flow.y, "Production per month and source", "F2", 10.5)
                flow.y -= 22
                rows = [(m, num(BY_MONTH["Solar"][i]), num(BY_MONTH["Wind"][i]), num(BY_MONTH["Water"][i]),
                         num(sum(BY_MONTH[k][i] for k in PROFILE))) for i, m in enumerate(MONTHS)]
                rows.append(("Year", num(BY_KIND["Solar"]), num(BY_KIND["Wind"]), num(BY_KIND["Water"]), num(TOTAL_MWH)))
                flow.table([("Month", 115, "l"), ("Solar MWh", 85, "r"), ("Wind MWh", 85, "r"), ("Water MWh", 85, "r"),
                            ("Total MWh", WIDTH - 370, "r")], rows, total=True)
            elif heading == "Members by municipality":
                rows = [(m, num(n), str(new), f"{n / MEMBER_TOTAL * 100:.1f} %") for m, n, new in MEMBERS]
                rows.append(("Total", num(MEMBER_TOTAL), str(MEMBER_NEW), "100.0 %"))
                flow.table([("Municipality", 170, "l"), ("Members", 100, "r"), ("New in 2025", 100, "r"),
                            ("Share", WIDTH - 370, "r")], rows, total=True)
                flow.page.text(MARGIN, flow.y, "Members at the end of the year", "F2", 10.5)
                flow.y -= 30
                flow.chart([y for y, _ in MEMBER_HISTORY], [("Members", GREEN, [m for _, m in MEMBER_HISTORY])],
                           "Members", 500, height=150)
            elif heading == "Income statement":
                rows = [(label, num(a), num(b)) for label, a, b in INCOME]
                rows.append(("Result for the year", num(RESULT), num(RESULT_2024)))
                flow.table([("EUR thousand", 235, "l"), ("2025", 118, "r"), ("2024", WIDTH - 353, "r")], rows, total=True)
            elif heading == "Balance sheet":
                assets = BALANCE_ASSETS + [("", "")] * (len(BALANCE_EQUITY) - len(BALANCE_ASSETS))
                rows = [(a, num(av) if av != "" else "", e, num(ev)) for (a, av), (e, ev) in zip(assets, BALANCE_EQUITY)]
                total = sum(v for _, v in BALANCE_ASSETS)
                rows.append(("Total", num(total), "Total", num(total)))
                flow.table([("Assets", 140, "l"), ("EUR thousand", 95, "r"), ("Equity and liabilities", 140, "l"),
                            ("EUR thousand", WIDTH - 375, "r")], rows, total=True)
        if title == "Governance and outlook":
            # The map follows the last chapter, on its own landscape page
            page = flow.new_page(A4_LANDSCAPE)
            toc.append(("9  MAP OF THE VALLEY", len(pages), True))
            valley_map(page)

    for n, plant in enumerate(PLANTS, 1):
        plant_sheet(flow, n, plant)

    # Contents
    w, h = A4
    y = h - 90
    contents.text(MARGIN, y, "CONTENTS", "F2", 20, GREEN)
    y -= 36
    for label, number, chapter in toc:
        font, size, indent = ("F2", 10.5, 0) if chapter else ("F1", 10, 18)
        if chapter:
            y -= 6
        x = MARGIN + indent
        page_number = str(number)
        right = w - MARGIN
        contents.text(x, y, label, font, size)
        start = x + text_width(label, font, size) + 4
        end = right - text_width(page_number, font, size) - 4
        dots = "." * max(0, int((end - start) / text_width(".", "F1", size)))
        contents.text(end - text_width(dots, "F1", size), y, dots, "F1", size, (0.5, 0.5, 0.5))
        contents.text(right - text_width(page_number, font, size), y, page_number, font, size)
        y -= 16
    assert y > 60, "the contents do not fit on one page"

    for i, p in enumerate(pages[1:], 2):
        header_footer(p, i)
    return pages


def write(pages):
    objects = []

    def add(body):
        objects.append(body)
        return len(objects)

    def stream(dictionary, data):
        data = zlib.compress(data, 9)
        return f"<< {dictionary} /Filter /FlateDecode /Length {len(data)} >>\nstream\n".encode() + data + b"\nendstream"

    catalog = add(None)
    pages_obj = add(None)
    fonts = {name: add(f"<< /Type /Font /Subtype /Type1 /BaseFont /{base} /Encoding /WinAnsiEncoding >>".encode())
             for name, base in (("F1", "Helvetica"), ("F2", "Helvetica-Bold"))}
    tint = add(f"<< /FunctionType 2 /Domain [0 1] /C0 [1 1 1] /C1 [{GREEN[0]:g} {GREEN[1]:g} {GREEN[2]:g}] /N 1 >>".encode())
    spot = add(f"[/Separation /DemoGreen /DeviceRGB {tint} 0 R]".encode())
    pictures = {}
    for name, (iw, ih, pixels) in (("Im1", cover_picture()), ("Im2", turbine_picture())):
        pictures[name] = add(stream(f"/Type /XObject /Subtype /Image /Width {iw} /Height {ih} /ColorSpace /DeviceRGB /BitsPerComponent 8", pixels))

    kids = []
    for page in pages:
        content = add(stream("", "\n".join(page.ops).encode("cp1252")))
        resources = "/Font << " + " ".join(f"/{n} {o} 0 R" for n, o in fonts.items()) + " >>"
        if page.images:
            resources += " /XObject << " + " ".join(f"/{n} {pictures[n]} 0 R" for n in page.images) + " >>"
        if page.uses_spot:
            resources += f" /ColorSpace << /CS1 {spot} 0 R >>"
        kids.append(add(f"<< /Type /Page /Parent {pages_obj} 0 R /MediaBox [0 0 {page.size[0]:g} {page.size[1]:g}] "
                        f"/Resources << {resources} >> /Contents {content} 0 R >>".encode()))
    objects[catalog - 1] = f"<< /Type /Catalog /Pages {pages_obj} 0 R >>".encode()
    objects[pages_obj - 1] = f"<< /Type /Pages /Kids [{' '.join(f'{k} 0 R' for k in kids)}] /Count {len(kids)} >>".encode()
    info = add(f"<< /Title ({esc(TITLE)}) /Author (Demo) /Producer (make_demo_pdf.py) >>".encode())

    out = bytearray(b"%PDF-1.7\n%\xe2\xe3\xcf\xd3\n")
    offsets = []
    for i, body in enumerate(objects, 1):
        offsets.append(len(out))
        out += f"{i} 0 obj\n".encode() + body + b"\nendobj\n"
    xref = len(out)
    out += f"xref\n0 {len(objects) + 1}\n0000000000 65535 f \n".encode()
    out += b"".join(f"{o:010d} 00000 n \n".encode() for o in offsets)
    out += f"trailer\n<< /Size {len(objects) + 1} /Root {catalog} 0 R /Info {info} 0 R >>\nstartxref\n{xref}\n%%EOF\n".encode()
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    with open(OUT, "wb") as f:
        f.write(out)
    print(f"{OUT}: {len(pages)} pages, {len(out) // 1024} KB")


if __name__ == "__main__":
    write(build())
