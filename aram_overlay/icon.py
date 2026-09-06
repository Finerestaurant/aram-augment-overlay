"""The app icon, drawn rather than shipped.

Modelled on the client's ARAM badge: an angular gold plate set at a diagonal
with a cut teal gem through the middle, with ARAM lettered across it. Drawing it
keeps one definition for the window icon, the tray icon and the shortcut, and
there is no binary asset in the repo to fall out of step.

The wordmark only exists to be read at 32 px and up. At the 16 px the tray
actually draws, the silhouette and the gold-on-teal contrast are what identify
it, so the shape is kept blunt enough to survive that.
"""
from __future__ import annotations

from math import cos, radians, sin
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

GOLD_LIGHT = (247, 227, 168)
GOLD_DARK = (176, 122, 32)
TEAL_LIGHT = (140, 240, 246)
TEAL_DARK = (22, 92, 130)
PLATE = (10, 16, 24)
INK = (255, 250, 235)

ICO_SIZES = [(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)]
TILT = -38.0          # the badge leans the way the client's does

_FONTS = ("segoeuib.ttf", "arialbd.ttf", "malgunbd.ttf")


def _hexagon(cx, cy, half_len, half_wid, point, angle=TILT):
    """Elongated hexagon -- a rectangle with both ends drawn to a point -- laid
    out along the x axis and then rotated into the badge's tilt."""
    pts = [(-half_len, 0), (-half_len + point, -half_wid), (half_len - point, -half_wid),
           (half_len, 0), (half_len - point, half_wid), (-half_len + point, half_wid)]
    a = radians(angle)
    ca, sa = cos(a), sin(a)
    return [(cx + x * ca - y * sa, cy + x * sa + y * ca) for x, y in pts]


def _ramp(size: int, top, bottom, angle=TILT):
    """A linear gradient across the badge's diagonal, as a full-size image to be
    pasted through a mask."""
    tall = Image.new("RGB", (1, size))
    px = tall.load()
    for y in range(size):
        t = y / max(1, size - 1)
        px[0, y] = tuple(round(top[i] + (bottom[i] - top[i]) * t) for i in range(3))
    big = tall.resize((size * 2, size * 2), Image.BILINEAR)
    return big.rotate(angle, resample=Image.BILINEAR, center=(size, size)) \
              .crop((size // 2, size // 2, size // 2 + size, size // 2 + size))


def _font(px: int):
    for name in _FONTS:
        try:
            return ImageFont.truetype(name, px)
        except Exception:
            continue
    return ImageFont.load_default()


def image(size: int = 256) -> Image.Image:
    """The icon at any size. Drawn at 4x and downsampled -- Pillow has no
    antialiased polygon, and at 16 px the raw edges are visibly ragged."""
    s = size * 4
    k = s / 256.0
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    c = s / 2

    plate = _hexagon(c, c, 118 * k, 74 * k, 44 * k)
    d.polygon(plate, fill=PLATE)

    # Gold frame: the plate outline, minus an inset copy of itself, filled with
    # the gold ramp so the edge catches light the way the client's plate does.
    frame = Image.new("L", (s, s), 0)
    fd = ImageDraw.Draw(frame)
    fd.polygon(plate, fill=255)
    fd.polygon(_hexagon(c, c, 100 * k, 56 * k, 38 * k), fill=0)
    img.paste(_ramp(s, GOLD_LIGHT, GOLD_DARK), (0, 0), frame)

    # The stepped inner rail, one thin gold line following the same shape.
    rail = Image.new("L", (s, s), 0)
    rd = ImageDraw.Draw(rail)
    rd.polygon(_hexagon(c, c, 92 * k, 49 * k, 35 * k), fill=255)
    rd.polygon(_hexagon(c, c, 86 * k, 43 * k, 33 * k), fill=0)
    img.paste(_ramp(s, GOLD_LIGHT, GOLD_DARK), (0, 0), rail)

    # The gem, cut long and narrow across the plate.
    gem = Image.new("L", (s, s), 0)
    gd = ImageDraw.Draw(gem)
    gd.polygon(_hexagon(c, c, 78 * k, 34 * k, 30 * k), fill=255)
    img.paste(_ramp(s, TEAL_LIGHT, TEAL_DARK), (0, 0), gem)
    d.polygon(_hexagon(c, c, 64 * k, 22 * k, 25 * k), fill=(14, 54, 82, 255))

    # ARAM, held horizontal so it stays readable while the plate is tilted, and
    # sized to sit inside the gem rather than across the whole plate.
    text = "ARAM"
    font = _font(int(34 * k))
    box = d.textbbox((0, 0), text, font=font)
    d.text((c - (box[2] - box[0]) / 2 - box[0], c - (box[3] - box[1]) / 2 - box[1]),
           text, font=font, fill=INK,
           stroke_width=max(1, int(3 * k)), stroke_fill=(6, 20, 32, 255))

    return img.resize((size, size), Image.LANCZOS)


def ensure_ico(path: Path, force: bool = False) -> Path | None:
    """Write the .ico Tk and the desktop shortcut need.

    Rewritten whenever this file is newer than it, so editing the drawing above
    is enough -- otherwise the shortcut keeps showing the icon from whichever
    version first generated it.
    """
    try:
        stale = not path.exists() or path.stat().st_mtime < Path(__file__).stat().st_mtime
        if force or stale:
            path.parent.mkdir(parents=True, exist_ok=True)
            image(256).save(path, format="ICO", sizes=ICO_SIZES)
        return path
    except Exception:
        return None
