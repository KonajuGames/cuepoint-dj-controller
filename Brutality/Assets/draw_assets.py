"""Draws Cuepoint's images as PNGs: the logo (title bar and window icon) and the list's small icons.

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
# A record seen from above, in the app's orange, with a white cue marker at the top and the playhead running down
# from it to the label: a cue point set on a record.

ORANGE_LIGHT = 0xF59A45
ORANGE_DARK = 0xD9601A
GROOVE = 0x7A2E08
LABEL = 0x1B1C22
WHITE = 0xFFFFFF


def disc_fill(x, y):
    # Lit from the top left.
    t = max(0.0, min(1.0, ((x - 6) + (y - 8)) / 52))
    a, b = hex_colour(ORANGE_LIGHT), hex_colour(ORANGE_DARK)
    return tuple(a[i] + (b[i] - a[i]) * t for i in range(3)) + (1.0,)


LOGO = [
    (circle(32, 35, 27), disc_fill),
    (ring(32, 35, 20.5, 1.6), hex_colour(GROOVE, 0.45)),
    (ring(32, 35, 15, 1.6), hex_colour(GROOVE, 0.45)),
    (circle(32, 35, 9), hex_colour(LABEL)),
    (circle(32, 35, 2.2), disc_fill),
    # The playhead, from the marker to the label, and the marker itself.
    (segment(32, 10, 32, 26.5, 2.8), hex_colour(WHITE)),
    (rounded(polygon([(24.5, 4), (39.5, 4), (32, 12.5)]), 1.1), hex_colour(WHITE)),
]

# ---- Icons ----
# A tick, for a song played this session.
PLAYED = [(segment(3.2, 8.4, 6.4, 11.6, 2.4), hex_colour(WHITE)), (segment(6.4, 11.6, 12.8, 4.4, 2.4), hex_colour(WHITE))]


def main():
    preview = "preview" in sys.argv[1:]
    jobs = [
        ("logo-22.png", LOGO, 22, 64),
        ("logo-16.png", LOGO, 16, 64),
        ("logo-32.png", LOGO, 32, 64),
        ("logo-48.png", LOGO, 48, 64),
        ("logo-256.png", LOGO, 256, 64),
        ("played-14.png", PLAYED, 14, 16),
    ]
    # Previews go over the title bar's colour, since the icons are white.
    previews = [("preview-logo.png", LOGO, 512, 64), ("preview-logo-small.png", LOGO, 22, 64), ("preview-played.png", PLAYED, 256, 16)]
    for name, layers, size, units in jobs:
        path = os.path.join(HERE, name)
        write_png(path, size, size, render(layers, size, units))
        print("wrote", path)
    if preview:
        for name, layers, size, units in previews:
            path = os.path.join(PREVIEWS, name)
            write_png(path, size, size, render(layers, size, units, hex_colour(0x0B0C0F)))
            print("wrote", path)


if __name__ == "__main__":
    main()
