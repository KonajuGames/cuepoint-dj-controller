"""Draws the feature tour's annotations over its video.

`Cuepoint --tour <folder>` leaves tour-raw.mp4 (every frame the app drew), tour-audio.wav (the mix) and
tour-timeline.json (the captions, boxes, keys pressed and where the pretend mouse was) in <folder>. This turns the
timeline into an ASS subtitle file and has ffmpeg burn it onto the video, with the sound, as cuepoint-tour.mp4:

    python docs/tour/annotate.py <folder> [--ffmpeg path] [--out file.mp4]

It needs Python 3.8 or later, Pillow (to measure text, so the caption boxes fit it) and an ffmpeg built with libass.
"""

import argparse
import json
import os
import subprocess
import sys

from PIL import ImageFont

FONTS = os.path.join(os.environ.get("WINDIR", "C:\\Windows"), "Fonts")
REGULAR = os.path.join(FONTS, "segoeui.ttf")
BOLD = os.path.join(FONTS, "segoeuib.ttf")

WIDTH, HEIGHT = 1920, 1080
# The accent: a warm amber that reads against the app's dark panels and its blue and orange decks.
ACCENT = (255, 190, 70)
WHITE = (245, 247, 250)
PANEL = (14, 17, 22)


def colour(rgb, alpha=0):
    """An ASS colour, &HAABBGGRR, alpha 0 opaque and 255 clear."""
    r, g, b = rgb
    return "&H%02X%02X%02X%02X" % (alpha, b, g, r)


def ts(t):
    t = max(0.0, t)
    h = int(t // 3600)
    m = int(t % 3600 // 60)
    s = t % 60
    return "%d:%02d:%05.2f" % (h, m, s)


def measure(text, size, bold=False):
    font = ImageFont.truetype(BOLD if bold else REGULAR, size)
    left, _, right, _ = font.getbbox(text)
    return right - left


def rounded(x, y, w, h, r):
    """An ASS drawing of a rounded rectangle, from its top left."""
    r = min(r, w / 2, h / 2)
    k = r * 0.4477  # How far the control points sit in from a corner, for a near-circular arc.
    pts = [
        "m %d %d" % (x + r, y),
        "l %d %d" % (x + w - r, y),
        "b %d %d %d %d %d %d" % (x + w - k, y, x + w, y + k, x + w, y + r),
        "l %d %d" % (x + w, y + h - r),
        "b %d %d %d %d %d %d" % (x + w, y + h - k, x + w - k, y + h, x + w - r, y + h),
        "l %d %d" % (x + r, y + h),
        "b %d %d %d %d %d %d" % (x + k, y + h, x, y + h - k, x, y + h - r),
        "l %d %d" % (x, y + r),
        "b %d %d %d %d %d %d" % (x, y + k, x + k, y, x + r, y),
    ]
    return " ".join(pts)


def frame(x, y, w, h, r, t):
    """An outline of a rounded rectangle t thick, as one shape with a hole (the inner path wound the other way)."""
    outer = rounded(x, y, w, h, r)
    ix, iy, iw, ih, ir = x + t, y + t, w - 2 * t, h - 2 * t, max(0, r - t)
    k = ir * 0.4477
    inner = [
        "m %d %d" % (ix + ir, iy),
        "l %d %d" % (ix, iy + ir) if ir == 0 else "b %d %d %d %d %d %d" % (ix + k, iy, ix, iy + k, ix, iy + ir),
        "l %d %d" % (ix, iy + ih - ir),
        "b %d %d %d %d %d %d" % (ix, iy + ih - k, ix + k, iy + ih, ix + ir, iy + ih),
        "l %d %d" % (ix + iw - ir, iy + ih),
        "b %d %d %d %d %d %d" % (ix + iw - k, iy + ih, ix + iw, iy + ih - k, ix + iw, iy + ih - ir),
        "l %d %d" % (ix + iw, iy + ir),
        "b %d %d %d %d %d %d" % (ix + iw, iy + k, ix + iw - k, iy, ix + iw - ir, iy),
        "l %d %d" % (ix + ir, iy),
    ]
    return outer + " " + " ".join(inner)


CURSOR = "m 0 0 l 0 25 6 19 10 29 15 27 11 18 19 18 l 0 0"


class Ass:
    def __init__(self):
        self.lines = []

    def shape(self, start, end, drawing, fill, alpha=0, extra="", layer=0, border=0, border_colour=PANEL, border_alpha=0):
        self.lines.append(
            "Dialogue: %d,%s,%s,Shape,,0,0,0,,{\\an7\\pos(0,0)\\bord%g\\shad0\\1c%s\\1a&H%02X&\\3c%s\\3a&H%02X&%s\\p1}%s{\\p0}"
            % (layer, ts(start), ts(end), border, colour(fill), alpha, colour(border_colour), border_alpha, extra, drawing)
        )

    def text(self, start, end, x, y, text, size, rgb, bold=False, align=7, extra="", layer=0, alpha=0):
        text = text.replace("{", "(").replace("}", ")")
        self.lines.append(
            "Dialogue: %d,%s,%s,Text,,0,0,0,,{\\an%d\\pos(%d,%d)\\fn%s\\fs%d\\b%d\\1c%s\\1a&H%02X&\\bord0\\shad0%s}%s"
            % (layer, ts(start), ts(end), align, x, y, "Segoe UI", size, 1 if bold else 0, colour(rgb), alpha, extra, text)
        )

    def write(self, path):
        head = [
            "[Script Info]",
            "ScriptType: v4.00+",
            "PlayResX: %d" % WIDTH,
            "PlayResY: %d" % HEIGHT,
            "WrapStyle: 2",
            "ScaledBorderAndShadow: yes",
            "",
            "[V4+ Styles]",
            "Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, "
            "Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, "
            "MarginR, MarginV, Encoding",
            "Style: Text,Segoe UI,28,&H00FFFFFF,&H00FFFFFF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,0,0,7,0,0,0,1",
            "Style: Shape,Segoe UI,20,&H00FFFFFF,&H00FFFFFF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,0,0,7,0,0,0,1",
            "",
            "[Events]",
            "Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text",
        ]
        with open(path, "w", encoding="utf-8-sig") as f:
            f.write("\n".join(head + self.lines) + "\n")


def spans(events, start_type, end_types, duration):
    """Each event of start_type with when it ends: at the next of end_types or start_type, or the video's end."""
    out = []
    for i, e in enumerate(events):
        if e["type"] != start_type:
            continue
        end = duration
        for later in events[i + 1:]:
            if later["type"] in end_types or later["type"] == start_type:
                end = later["t"]
                break
        out.append((e, end))
    return out


def build(timeline, top):
    events = timeline["events"]
    duration = timeline["duration"]
    ass = Ass()
    fade = "\\fad(220,220)"

    # Title cards: the screen dimmed, a big title and a line under it.
    for e, end in spans(events, "card", ["cardOff"], duration):
        ass.shape(e["t"], end, "m 0 0 l %d 0 %d %d 0 %d" % (WIDTH, WIDTH, HEIGHT, HEIGHT), PANEL, 0x38, fade, layer=10)
        ass.text(e["t"], end, WIDTH // 2, HEIGHT // 2 - 20, e["title"], 128, WHITE, True, 2, fade, layer=11)
        ass.shape(e["t"], end, rounded(WIDTH // 2 - 60, HEIGHT // 2 + 2, 120, 6, 3), ACCENT, 0, fade, layer=11)
        ass.text(e["t"], end, WIDTH // 2, HEIGHT // 2 + 34, e["body"], 40, (205, 212, 222), False, 8, fade, layer=11)

    # Captions: a panel along the bottom, the title in the accent over the line that says more.
    for e, end in spans(events, "caption", ["captionOff", "card"], duration):
        title_w = measure(e["title"], 36, True)
        body_w = measure(e["body"], 27)
        w = max(title_w, body_w) + 2 * 34 + 10
        h = 112
        x = (WIDTH - w) // 2
        y = HEIGHT - h - 28
        ass.shape(e["t"], end, rounded(x, y, w, h, 14), PANEL, 0x1A, fade, layer=20, border=1.5, border_colour=(60, 66, 76))
        ass.shape(e["t"], end, rounded(x + 14, y + 20, 6, h - 40, 3), ACCENT, 0, fade, layer=21)
        ass.text(e["t"], end, x + 40, y + 13, e["title"], 36, ACCENT, True, 7, fade, layer=21)
        ass.text(e["t"], end, x + 40, y + 60, e["body"], 27, WHITE, False, 7, fade, layer=21)

    # Boxes round what's being shown, with a tag above saying what it is.
    for e, end in spans(events, "highlight", ["highlightsOff", "card"], duration):
        # A highlight lasts until all are cleared; a later one doesn't end an earlier.
        end = next((later["t"] for later in events if later["t"] > e["t"] and later["type"] in ("highlightsOff", "card")), duration)
        x, y, w, h = e["x"], e["y"] + top, e["w"], e["h"]
        x0, y0 = max(2, x), max(2, y)
        w = min(WIDTH - 2, x + w) - x0
        h = min(HEIGHT - 2, y + h) - y0
        appear = "\\fad(180,200)"
        ass.shape(e["t"], end, frame(x0 - 3, y0 - 3, w + 6, h + 6, 10, 7), ACCENT, 0xC0, appear, layer=15)
        ass.shape(e["t"], end, frame(x0, y0, w, h, 8, 3), ACCENT, 0, appear, layer=16)
        if e.get("label"):
            lw = measure(e["label"], 22, True) + 26
            lx = min(max(x0, 6), WIDTH - lw - 6)
            ly = y0 - 36 if y0 > 60 else y0 + h + 6
            ass.shape(e["t"], end, rounded(lx, ly, lw, 32, 8), ACCENT, 0, appear, layer=17)
            ass.text(e["t"], end, lx + 13, ly + 2, e["label"], 22, PANEL, True, 7, appear, layer=18)

    # Keys pressed: keycaps in the bottom right, above the caption.
    keys = [e for e in events if e["type"] == "key"]
    for i, e in enumerate(keys):
        end = e["t"] + 1.3
        if i + 1 < len(keys):
            end = min(end, keys[i + 1]["t"])
        caps = e["label"].split(" + ")
        widths = [max(measure(c, 30, True) + 34, 52) for c in caps]
        plus = measure("+", 28, True) + 16
        total = sum(widths) + plus * (len(caps) - 1)
        x = WIDTH - 48 - total
        y = HEIGHT - 112 - 28 - 86
        for j, (cap, cw) in enumerate(zip(caps, widths)):
            ass.shape(e["t"], end, rounded(x, y + 4, cw, 56, 10), (8, 9, 12), 0x20, "\\fad(80,160)", layer=30)
            ass.shape(e["t"], end, rounded(x, y, cw, 54, 10), (236, 239, 244), 0, "\\fad(80,160)", layer=31)
            ass.text(e["t"], end, x + cw // 2, y + 26, cap, 30, PANEL, True, 5, "\\fad(80,160)", layer=32)
            x += cw
            if j + 1 < len(caps):
                ass.text(e["t"], end, x + plus // 2, y + 26, "+", 28, WHITE, True, 5, "\\fad(80,160)", layer=32)
                x += plus

    # A jump forward in time: a moment's fade through black and a note of how long was skipped.
    for e in events:
        if e["type"] == "cut":
            ass.shape(e["t"], e["t"] + 0.5, "m 0 0 l %d 0 %d %d 0 %d" % (WIDTH, WIDTH, HEIGHT, HEIGHT), (0, 0, 0), 0, "\\fad(0,500)", layer=40)
            lw = measure(e["label"], 30, True) + 44
            ass.shape(e["t"], e["t"] + 2.4, rounded((WIDTH - lw) // 2, 120, lw, 52, 26), PANEL, 0x20, fade, layer=41)
            ass.text(e["t"], e["t"] + 2.4, WIDTH // 2, 146, e["label"], 30, WHITE, True, 5, fade, layer=42)

    # Clicks: a ring spreading out from where the mouse was pressed.
    for e in events:
        if e["type"] == "click":
            x, y = e["x"], e["y"] + top
            ring = frame(-30, -30, 60, 60, 30, 4)
            ass.lines.append(
                "Dialogue: 50,%s,%s,Shape,,0,0,0,,{\\an7\\pos(%d,%d)\\bord0\\shad0\\1c%s\\fscx30\\fscy30\\t(0,380,\\fscx110\\fscy110\\1a&HFF&)\\p1}%s{\\p0}"
                % (ts(e["t"]), ts(e["t"] + 0.4), x, y, colour(ACCENT), ring)
            )

    # The pretend mouse: an arrow at each place it was drawn, white with a dark edge.
    cursor = timeline["cursor"]
    for i, (t, x, y, down) in enumerate(cursor):
        end = cursor[i + 1][0] if i + 1 < len(cursor) else t + 1 / timeline["fps"]
        if end - t > 0.25:
            end = t + 1 / timeline["fps"]
        scale = 92 if down else 100
        ass.lines.append(
            "Dialogue: 60,%s,%s,Shape,,0,0,0,,{\\an7\\pos(%d,%d)\\bord2\\shad1.5\\4a&H90&\\1c%s\\3c%s\\fscx%d\\fscy%d\\p1}%s{\\p0}"
            % (ts(t), ts(end), x, y + top, colour(WHITE), colour((10, 10, 12)), scale, scale, CURSOR)
        )
    return ass


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("folder")
    parser.add_argument("--ffmpeg", default="ffmpeg")
    parser.add_argument("--out", default=None)
    args = parser.parse_args()

    folder = os.path.abspath(args.folder)
    with open(os.path.join(folder, "tour-timeline.json"), encoding="utf-8") as f:
        timeline = json.load(f)
    top = (HEIGHT - timeline["height"]) // 2
    ass = build(timeline, top)
    ass.write(os.path.join(folder, "tour.ass"))

    out = os.path.abspath(args.out or os.path.join(folder, "cuepoint-tour.mp4"))
    duration = timeline["duration"]
    fonts = FONTS.replace("\\", "/").replace(":", "\\:")
    video = (
        "[0:v]pad=%d:%d:0:%d:color=0x0E1116,ass=tour.ass:fontsdir='%s',"
        "fade=t=in:st=0:d=0.6,fade=t=out:st=%.2f:d=0.8,format=yuv420p[v]" % (WIDTH, HEIGHT, top, fonts, duration - 0.8)
    )
    # The mix is stretched to the video's length, keeping its pitch: with no sound card the audio runs a little
    # slower than the clock the frames are timed by, and the decks with it, so stretched it lines up throughout.
    ffprobe = os.path.join(os.path.dirname(args.ffmpeg), "ffprobe" + os.path.splitext(args.ffmpeg)[1]) if os.path.dirname(args.ffmpeg) else "ffprobe"
    sound = float(subprocess.check_output(
        [ffprobe, "-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", os.path.join(folder, "tour-audio.wav")]
    ).decode().strip())
    stretch = min(2.0, max(0.5, sound / duration))
    print("stretching the mix by %.4f (%.2fs of sound for %.2fs of video)" % (stretch, sound, duration))
    audio = "[1:a]atempo=%.6f,afade=t=in:st=0:d=0.6,afade=t=out:st=%.2f:d=0.8[a]" % (stretch, duration - 0.8)
    command = [
        args.ffmpeg, "-y", "-i", "tour-raw.mp4", "-i", "tour-audio.wav",
        "-filter_complex", video + ";" + audio, "-map", "[v]", "-map", "[a]",
        "-c:v", "libx264", "-preset", "slow", "-crf", "18", "-r", str(timeline["fps"]),
        "-c:a", "aac", "-b:a", "192k", "-t", "%.3f" % duration, "-movflags", "+faststart", out,
    ]
    print(" ".join(command))
    sys.exit(subprocess.call(command, cwd=folder))


if __name__ == "__main__":
    main()
