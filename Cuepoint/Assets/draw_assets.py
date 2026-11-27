"""Draws Cuepoint's images as PNGs: the logo (title bar and window icon), the list's small icons, the triangles
used for arrows (the folder list's, the loop buttons', the nudge pads'), the options button's cog, the headphones
button, the mixer's knob caps and the dots drawn over them, the search box's clearing cross, and the folder list's
icons. The logo's sizes are also gathered into logo.ico, the executable's icon in Explorer and the taskbar.

Run with any Python 3; it needs nothing installed. Shapes are distance fields, sampled 8x8 times per pixel so edges
come out smooth at every size, and written with zlib as straight-alpha RGBA. Small icons are white, for the app to
tint.

    python draw_assets.py [preview]

"preview" also writes a large copy of each, for looking at.
"""

import math
import os
import struct
import sys
import zlib

HERE = os.path.dirname(os.path.abspath(__file__))
PREVIEWS = os.environ.get("PREVIEWS", HERE)
SAMPLES = 8


def write_ico(path, pngs):
    """An icon file holding PNG images, as Windows Vista and later read them: pngs is (size, path) pairs."""
    images = [(size, open(png, "rb").read()) for size, png in pngs]
    out = bytearray(struct.pack("<HHH", 0, 1, len(images)))
    offset = 6 + 16 * len(images)
    for size, data in images:
        # A size of 256 is written as 0.
        out += struct.pack("<BBBBHHII", size % 256, size % 256, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
    for _, data in images:
        out += data
    with open(path, "wb") as f:
        f.write(out)


def write_png(path, width, height, pixels):
    """pixels: rows of (r, g, b, a) floats from 0 to 1, straight alpha, sRGB."""
    raw = bytearray()
    for row in pixels:
        raw.append(0)
        for r, g, b, a in row:
            raw += bytes(max(0, min(255, round(c * 255))) for c in (r, g, b, a))

    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)

    with open(path, "wb") as f:
        f.write(b"\x89PNG\r\n\x1a\n")
        f.write(chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0)))
        f.write(chunk(b"IDAT", zlib.compress(bytes(raw), 9)))
        f.write(chunk(b"IEND", b""))


def hex_colour(value, alpha=1.0):
    return ((value >> 16 & 255) / 255, (value >> 8 & 255) / 255, (value & 255) / 255, alpha)


# Blending is done in linear light, as the app does, so edges don't darken.
def to_linear(c):
    return c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4


def to_srgb(c):
    return c * 12.92 if c <= 0.0031308 else 1.055 * c ** (1 / 2.4) - 0.055


def over(top, bottom):
    """Straight-alpha 'over', in linear light."""
    ta, ba = top[3], bottom[3]
    a = ta + ba * (1 - ta)
    if a <= 0:
        return (0.0, 0.0, 0.0, 0.0)
    rgb = tuple((to_linear(t) * ta + to_linear(b) * ba * (1 - ta)) / a for t, b in zip(top[:3], bottom[:3]))
    return tuple(to_srgb(c) for c in rgb) + (a,)


# Distance fields, in the drawing's own units: negative inside.
def circle(cx, cy, r):
    return lambda x, y: math.hypot(x - cx, y - cy) - r


def ring(cx, cy, r, width):
    return lambda x, y: abs(math.hypot(x - cx, y - cy) - r) - width / 2


def segment(ax, ay, bx, by, width):
    def d(x, y):
        px, py, vx, vy = x - ax, y - ay, bx - ax, by - ay
        t = max(0.0, min(1.0, (px * vx + py * vy) / (vx * vx + vy * vy)))
        return math.hypot(px - vx * t, py - vy * t) - width / 2

    return d


def polygon(points):
    """A convex or concave polygon, by the even-odd rule."""

    def d(x, y):
        best = float("inf")
        inside = False
        n = len(points)
        for i in range(n):
            (ax, ay), (bx, by) = points[i], points[(i + 1) % n]
            vx, vy, px, py = bx - ax, by - ay, x - ax, y - ay
            t = max(0.0, min(1.0, (px * vx + py * vy) / (vx * vx + vy * vy)))
            best = min(best, math.hypot(px - vx * t, py - vy * t))
            if (ay > y) != (by > y) and x < ax + (y - ay) * (bx - ax) / (by - ay):
                inside = not inside
        return -best if inside else best

    return d


def rounded(shape, radius):
    return lambda x, y: shape(x, y) - radius


def intersect(a, b):
    return lambda x, y: max(a(x, y), b(x, y))


def union(*shapes):
    return lambda x, y: min(shape(x, y) for shape in shapes)



def render(layers, size, units, background=None):
    """layers: (shape, colour) pairs, bottom first, or (shape, function of x, y giving a colour). The drawing is
    `units` across, scaled to `size` pixels, over `background` if given."""
    scale = units / size
    if background is not None:
        layers = [(lambda x, y: -1.0, background)] + layers
    rows = []
    for py in range(size):
        row = []
        for px in range(size):
            # A pixel no edge comes near is one colour throughout, so one sample does.
            cx, cy = (px + 0.5) * scale, (py + 0.5) * scale
            edge = min(abs(shape(cx, cy)) for shape, _ in layers)
            samples = SAMPLES if edge < scale else 1
            total = [0.0, 0.0, 0.0, 0.0]
            for sy in range(samples):
                for sx in range(samples):
                    x = (px + (sx + 0.5) / samples) * scale
                    y = (py + (sy + 0.5) / samples) * scale
                    colour = (0.0, 0.0, 0.0, 0.0)
                    for shape, fill in layers:
                        if shape(x, y) <= 0:
                            colour = over(fill(x, y) if callable(fill) else fill, colour)
                    # Averaged premultiplied, in linear light.
                    a = colour[3]
                    total[0] += to_linear(colour[0]) * a
                    total[1] += to_linear(colour[1]) * a
                    total[2] += to_linear(colour[2]) * a
                    total[3] += a
            n = samples * samples
            a = total[3] / n
            rgb = (to_srgb(c / n / a) for c in total[:3]) if a > 0 else (0, 0, 0)
            row.append(tuple(rgb) + (a,))
        rows.append(row)
    return rows


# ---- The logo ----
# The Waveform C, in the style of Cuedeck's Spotlight C, in the two decks' colours: a blue C on a dark rounded tile
# with a faint edge, a lit spindle at its centre, and a red waveform spilling out of the C's opening, fading as it
# goes. Drawn in a 32 unit square.
# The title bar's 16 and 22 pixel sizes are drawn bolder, with fewer, wider bars and a bigger spindle, so they still
# read.

TILE = 0x16171D
EDGE = 0x2E3039
DECK_A = 0x2F8FFF
DECK_B = 0xEE4444
SPINDLE = 0xE4E5EC
WHITE = 0xFFFFFF


def rounded_box(x0, y0, x1, y1, radius):
    """A rectangle with rounded corners."""
    cx, cy, hx, hy = (x0 + x1) / 2, (y0 + y1) / 2, (x1 - x0) / 2, (y1 - y0) / 2

    def d(x, y):
        qx, qy = abs(x - cx) - (hx - radius), abs(y - cy) - (hy - radius)
        return math.hypot(max(qx, 0), max(qy, 0)) + min(max(qx, qy), 0) - radius

    return d


def c_shape(cx, cy, radius, stroke, gap):
    """A thick arc open to the right by `gap` degrees either side, its ends rounded."""
    caps = [(cx + radius * math.cos(math.radians(a)), cy + radius * math.sin(math.radians(a))) for a in (gap, -gap)]

    def d(x, y):
        if abs(math.degrees(math.atan2(y - cy, x - cx))) < gap:
            return min(math.hypot(x - ex, y - ey) for ex, ey in caps) - stroke / 2
        return abs(math.hypot(x - cx, y - cy) - radius) - stroke / 2

    return d


def logo(style):
    """The logo's layers for one of the styles below."""
    inside = rounded_box(0.6, 0.6, 31.4, 31.4, 6.4)
    near, far = style["near"], style["far"]
    first, last = style["bars"][0][0], style["bars"][-1][0]

    def wave_fill(x, y):
        t = max(0.0, min(1.0, (x - first) / max(1e-6, last - first)))
        return hex_colour(DECK_B, near + (far - near) * t)

    bars = union(*(rounded_box(x - style["width"] / 2, 16 - h, x + style["width"] / 2, 16 + h, style["width"] / 2) for x, h in style["bars"]))
    return [
        (rounded_box(0, 0, 32, 32, 7), hex_colour(EDGE)),
        (inside, hex_colour(TILE)),
        (intersect(bars, inside), wave_fill),
        (c_shape(15, 16, 9, style["stroke"], style["gap"]), hex_colour(DECK_A)),
        (circle(15, 16, style["spindle"]), hex_colour(SPINDLE)),
    ]


# Each bar of the waveform as (where it is across, half its height).
LOGO = logo(dict(gap=45, stroke=4.5, spindle=2.6, width=1.5, near=0.9, far=0.35, bars=[(21.8, 2.6), (24.6, 5.0), (27.4, 3.6), (30.0, 6.0)]))
LOGO_SMALL = logo(dict(gap=40, stroke=5.6, spindle=3.3, width=2.4, near=0.95, far=0.5, bars=[(23.0, 2.6), (26.6, 5.6), (30.0, 4.0)]))

# ---- Icons ----
# A tick, for a song played this session.
PLAYED = [(segment(3.2, 8.4, 6.4, 11.6, 2.4), hex_colour(WHITE)), (segment(6.4, 11.6, 12.8, 4.4, 2.4), hex_colour(WHITE))]


def wave(y, amplitude, x0, x1, width, cycles=1.0, steps=14):
    """A line rising and falling as a sine wave from x0 to x1 about y, drawn as short segments."""
    points = [(x0 + (x1 - x0) * i / steps, y + amplitude * math.sin(cycles * 2 * math.pi * i / steps)) for i in range(steps + 1)]
    return union(*(segment(ax, ay, bx, by, width) for (ax, ay), (bx, by) in zip(points, points[1:])))


# A song separated into its stems: four waves one above another, one for each stem, as a multitrack shows them. They
# rise and fall together, so at 14 pixels they read as four lines rather than a crosshatch.
STEMS = [(union(*(wave(2.4 + 3.75 * i, 0.85, 1.6, 14.4, 1.45) for i in range(4))), hex_colour(WHITE))]

# Arrows: small triangles with their corners softened, pointing right, down and left.
TRIANGLE_RIGHT = [(rounded(polygon([(3.6, 1.8), (8.6, 5), (3.6, 8.2)]), 0.5), hex_colour(WHITE))]
TRIANGLE_DOWN = [(rounded(polygon([(1.8, 3.4), (8.2, 3.4), (5, 8.4)]), 0.5), hex_colour(WHITE))]
TRIANGLE_LEFT = [(rounded(polygon([(6.4, 1.8), (1.4, 5), (6.4, 8.2)]), 0.5), hex_colour(WHITE))]


def outline(shape, width):
    """A line of `width` just inside `shape`'s edge."""
    return lambda x, y: abs(shape(x, y) + width / 2) - width / 2


def cog(cx, cy, teeth, body, tip, base_width, tip_width):
    """A cog's outline shape: a disc of radius `body` with `teeth` tapering teeth out to `tip`."""
    shapes = [circle(cx, cy, body)]
    for i in range(teeth):
        angle = 2 * math.pi * i / teeth
        ux, uy = math.cos(angle), math.sin(angle)
        vx, vy = -uy, ux
        inner, outer = body - 0.8, tip
        shapes.append(
            polygon(
                [
                    (cx + ux * inner + vx * base_width / 2, cy + uy * inner + vy * base_width / 2),
                    (cx + ux * outer + vx * tip_width / 2, cy + uy * outer + vy * tip_width / 2),
                    (cx + ux * outer - vx * tip_width / 2, cy + uy * outer - vy * tip_width / 2),
                    (cx + ux * inner - vx * base_width / 2, cy + uy * inner - vy * base_width / 2),
                ]
            )
        )
    return rounded(union(*shapes), 0.25)


# The options button: a cog in outline, six teeth (more blur together this small), with a ring at its middle, in
# lines as thin as the window buttons' beside it.
COG = [(union(outline(cog(8, 8, 6, 4.6, 7.4, 3.6, 2.6), 1.1), ring(8, 8, 2.0, 1.1)), hex_colour(WHITE))]


# The headphones button: a headband over two ear cups, in lines like the cog's.
HEADPHONES = [
    (
        union(
            intersect(ring(8, 8.6, 5.6, 1.3), lambda x, y: y - 9.0),
            rounded(polygon([(1.9, 8.6), (4.3, 8.6), (4.3, 13.4), (1.9, 13.4)]), 0.7),
            rounded(polygon([(11.7, 8.6), (14.1, 8.6), (14.1, 13.4), (11.7, 13.4)]), 0.7),
        ),
        hex_colour(WHITE),
    )
]


# A mixer knob's cap, 20 units across, in colour: a dark rim, and a grey top lit from the top left, as a mixer's
# knobs are. Its value and pointer are drawn over it by the app.
def knob_fill(x, y):
    t = max(0.0, min(1.0, ((x - 3) + (y - 3)) / 16))
    a, b = hex_colour(0x5A5B68), hex_colour(0x2A2B33)
    return tuple(a[i] + (b[i] - a[i]) * t for i in range(3)) + (1.0,)


KNOB = [
    (circle(10, 10, 9.8), hex_colour(0x0E0F13)),
    (circle(10, 10, 8.8), knob_fill),
    (circle(10, 10, 6.6), hex_colour(0x34353F)),
]

# A soft round dot, drawn small for a knob's arc and pointer.
DOT = [(circle(4, 4, 3.7), hex_colour(WHITE))]


# Auto-Mix's and the decks' pause buttons: two bars.
PAUSE = [(union(polygon([(2, 1.2), (4.2, 1.2), (4.2, 8.8), (2, 8.8)]), polygon([(5.8, 1.2), (8, 1.2), (8, 8.8), (5.8, 8.8)])), hex_colour(WHITE))]

# The search box's clear button: a cross.
CROSS = [(union(segment(2.5, 2.5, 7.5, 7.5, 1.5), segment(7.5, 2.5, 2.5, 7.5, 1.5)), hex_colour(WHITE))]

# ---- The folder list's icons, 14 units square ----

# The music library: a pair of quavers.
NOTE = [
    (
        union(
            rounded(polygon([(3.2, 9.6), (5.6, 9.6), (5.6, 12.0), (3.2, 12.0)]), 0.8),
            rounded(polygon([(8.6, 8.4), (11.0, 8.4), (11.0, 10.8), (8.6, 10.8)]), 0.8),
            segment(5.6, 10.6, 5.6, 2.8, 1.3),
            segment(11.0, 9.4, 11.0, 1.6, 1.3),
            segment(5.6, 3.0, 11.0, 1.8, 2.0),
        ),
        hex_colour(WHITE),
    )
]

# A drive: a box with its light.
DRIVE = [
    (
        union(
            outline(rounded(polygon([(1.6, 4.0), (12.4, 4.0), (12.4, 10.0), (1.6, 10.0)]), 1.0), 1.3),
            circle(9.8, 7.0, 0.9),
            segment(3.6, 7.0, 6.6, 7.0, 1.1),
        ),
        hex_colour(WHITE),
    )
]

# A folder: a tab and its body.
FOLDER = [(rounded(polygon([(1.4, 3.0), (5.4, 3.0), (6.6, 4.4), (12.6, 4.4), (12.6, 11.4), (1.4, 11.4)]), 0.7), hex_colour(WHITE))]

# A virtual folder: lines of a playlist, with a note.
PLAYLIST = [
    (
        union(
            segment(1.6, 3.2, 9.6, 3.2, 1.3),
            segment(1.6, 6.4, 9.6, 6.4, 1.3),
            segment(1.6, 9.6, 6.0, 9.6, 1.3),
            rounded(polygon([(8.0, 10.6), (10.4, 10.6), (10.4, 12.6), (8.0, 12.6)]), 0.7),
            segment(10.4, 11.4, 10.4, 6.2, 1.2),
            segment(10.4, 6.6, 12.6, 7.6, 1.2),
        ),
        hex_colour(WHITE),
    )
]

# A list: three bulleted lines.
LIST = [
    (
        union(
            circle(2.4, 3.4, 1.1),
            circle(2.4, 7.0, 1.1),
            circle(2.4, 10.6, 1.1),
            segment(5.0, 3.4, 12.4, 3.4, 1.3),
            segment(5.0, 7.0, 12.4, 7.0, 1.3),
            segment(5.0, 10.6, 12.4, 10.6, 1.3),
        ),
        hex_colour(WHITE),
    )
]

# History: a clock.
CLOCK = [(union(ring(7, 7, 5.2, 1.3), segment(7, 7, 7, 3.8, 1.3), segment(7, 7, 9.4, 8.4, 1.3)), hex_colour(WHITE))]

# A filter: a funnel.
FUNNEL = [(rounded(polygon([(1.6, 2.4), (12.4, 2.4), (8.2, 7.4), (8.2, 11.8), (5.8, 10.6), (5.8, 7.4)]), 0.5), hex_colour(WHITE))]


def main():
    preview = "preview" in sys.argv[1:]
    jobs = [
        ("logo-22.png", LOGO_SMALL, 22, 32),
        ("logo-16.png", LOGO_SMALL, 16, 32),
        ("logo-32.png", LOGO, 32, 32),
        ("logo-48.png", LOGO, 48, 32),
        ("logo-256.png", LOGO, 256, 32),
        ("played-14.png", PLAYED, 14, 16),
        ("stems-14.png", STEMS, 14, 16),
        ("triangle-right-10.png", TRIANGLE_RIGHT, 10, 10),
        ("triangle-down-10.png", TRIANGLE_DOWN, 10, 10),
        ("triangle-left-10.png", TRIANGLE_LEFT, 10, 10),
        ("cog-18.png", COG, 18, 16),
        ("cross-10.png", CROSS, 10, 10),
        ("headphones-18.png", HEADPHONES, 18, 16),
        ("knob-18.png", KNOB, 18, 20),
        ("knob-32.png", KNOB, 32, 20),
        ("dot-8.png", DOT, 8, 8),
        ("icon-library-14.png", NOTE, 14, 14),
        ("icon-drive-14.png", DRIVE, 14, 14),
        ("icon-folder-14.png", FOLDER, 14, 14),
        ("icon-playlist-14.png", PLAYLIST, 14, 14),
        ("icon-list-14.png", LIST, 14, 14),
        ("icon-history-14.png", CLOCK, 14, 14),
        ("icon-filter-14.png", FUNNEL, 14, 14),
        # The folder list's arrows and icons again, larger, for its large text.
        ("triangle-right-14.png", TRIANGLE_RIGHT, 14, 10),
        ("pause-14.png", PAUSE, 14, 10),
        # The decks' play button: play, and pause while playing.
        ("play-16.png", TRIANGLE_RIGHT, 16, 10),
        ("pause-16.png", PAUSE, 16, 10),
        ("triangle-down-14.png", TRIANGLE_DOWN, 14, 10),
        ("icon-library-20.png", NOTE, 20, 14),
        ("icon-drive-20.png", DRIVE, 20, 14),
        ("icon-folder-20.png", FOLDER, 20, 14),
        ("icon-playlist-20.png", PLAYLIST, 20, 14),
        ("icon-list-20.png", LIST, 20, 14),
        ("icon-history-20.png", CLOCK, 20, 14),
        ("icon-filter-20.png", FUNNEL, 20, 14),
    ]
    # Previews go over the title bar's colour, since the icons are white.
    previews = [("preview-logo.png", LOGO, 512, 32), ("preview-logo-small.png", LOGO_SMALL, 22, 32), ("preview-played.png", PLAYED, 256, 16), ("preview-stems.png", STEMS, 256, 16), ("preview-triangle.png", TRIANGLE_RIGHT, 160, 10), ("preview-cog.png", COG, 256, 16), ("preview-headphones.png", HEADPHONES, 256, 16), ("preview-knob.png", KNOB, 200, 20)]
    for name, layers, size, units in jobs:
        path = os.path.join(HERE, name)
        write_png(path, size, size, render(layers, size, units))
        print("wrote", path)
    ico = os.path.join(HERE, "logo.ico")
    write_ico(ico, [(size, os.path.join(HERE, "logo-%d.png" % size)) for size in (16, 32, 48, 256)])
    print("wrote", ico)
    if preview:
        for name, layers, size, units in previews:
            path = os.path.join(PREVIEWS, name)
            write_png(path, size, size, render(layers, size, units, hex_colour(0x0B0C0F)))
            print("wrote", path)


if __name__ == "__main__":
    main()
