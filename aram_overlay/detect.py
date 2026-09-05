"""Augment window detection: is it open, which card is hovered, which was taken.

Everything here was calibrated against labelled frames; the thresholds live in
config.py with the measurements that produced them. The one rule worth repeating
is that absolute brightness is never the gate -- an earlier version keyed on it
and broke completely when the map changed, because ordinary gameplay landed
inside the "window open" band.
"""
from __future__ import annotations

from dataclasses import dataclass, field

import cv2
import numpy as np

from . import config


def _gray(img: np.ndarray) -> np.ndarray:
    return img if img.ndim == 2 else cv2.cvtColor(img, cv2.COLOR_BGR2GRAY)


def _edges(img: np.ndarray) -> np.ndarray:
    """Gradient magnitude, normalised.

    The reroll button is a translucent panel, so the map shows through it and the
    raw pixels change with whatever is behind. TM_CCOEFF_NORMED absorbs brightness
    and contrast shifts but not a different background, which is why the same
    button scored 0.46 on one map and 0.99 on another. What does not change is the
    shape -- a fixed rectangle and the arrow glyph. Matching on gradients instead
    of intensity separates the two cases far better: over 29 confirmed windows and
    14 frames with no window, the worst positive and best negative sit 0.64 apart
    against 0.25 for raw grayscale.
    """
    gx = cv2.Sobel(img, cv2.CV_32F, 1, 0, ksize=3)
    gy = cv2.Sobel(img, cv2.CV_32F, 0, 1, ksize=3)
    mag = cv2.magnitude(gx, gy)
    return cv2.normalize(mag, None, 0, 255, cv2.NORM_MINMAX).astype(np.uint8)


def _scaled(box, w: int, h: int):
    sx, sy = w / config.BASE_W, h / config.BASE_H
    x0, y0, x1, y1 = box
    return int(x0 * sx), int(y0 * sy), int(x1 * sx), int(y1 * sy)


def _mean(gray: np.ndarray, box) -> float:
    x0, y0, x1, y1 = _scaled(box, gray.shape[1], gray.shape[0])
    region = gray[max(0, y0):y1, max(0, x0):x1]
    return float(region.mean()) if region.size else 0.0


class TemplateGate:
    """Matches the three reroll buttons.

    All three must match to open. A single button reached 0.894 on an ordinary
    gameplay frame while the other two sat at 0.559 and 0.263, so any one-button
    rule false-positives.

    Staying open uses a looser threshold because the buttons have three visual
    states -- normal, greyed out, and cursor-highlighted -- and the template only
    matches the normal one, so min-of-three dips hard mid-window (observed
    [0.69, 0.63, 0.95] on a genuinely open window). Without that hysteresis the
    window reads as shut after ~1 s and everything after it is missed.
    """

    def __init__(self, templates=None):
        if templates is None:
            paths = sorted(config.ASSETS.glob(config.TEMPLATE_GLOB))
        else:
            paths = [templates] if isinstance(templates, (str, bytes)) else list(templates)
        self.templates = []
        for p in paths:
            tpl = cv2.imread(str(p), cv2.IMREAD_GRAYSCALE)
            if tpl is not None:
                self.templates.append(tpl)
        if not self.templates:
            raise FileNotFoundError(f"no reroll templates in {config.ASSETS}")
        self._prepped: dict = {}

    def scores(self, img: np.ndarray) -> list[float]:
        """Correlation at each of the three fixed boxes, one score each.

        No spatial search. The boxes are at fixed screen coordinates, so the only
        thing a search window adds is the chance for a negative frame to find
        something button-shaped nearby and report its score instead.
        """
        gray = _gray(img)
        h, w = gray.shape
        sx, sy = w / config.BASE_W, h / config.BASE_H
        bw, bh = config.REROLL_SIZE
        tw, th = max(4, round(bw * sx)), max(4, round(bh * sy))
        key = (tw, th)
        tpls = self._prepped.get(key)
        if tpls is None:
            tpls = [_edges(cv2.resize(t, (tw, th), interpolation=cv2.INTER_AREA)
                           if t.shape != (th, tw) else t)
                    for t in self.templates]
            self._prepped[key] = tpls

        out = []
        for bx, by in config.REROLL_BOXES:
            x0, y0 = round(bx * sx), round(by * sy)
            patch = gray[y0:y0 + th, x0:x0 + tw]
            if patch.shape != (th, tw):
                out.append(0.0)
                continue
            edge = _edges(patch)
            out.append(max(float(cv2.matchTemplate(edge, t, cv2.TM_CCOEFF_NORMED)[0, 0])
                           for t in tpls))
        return out


class HideButton:
    """The button that tucks the augment screen away.

    Pressing it hides the cards and the reroll buttons while leaving itself on
    screen, which the reroll gate alone reads as "window closed". One augment was
    recorded three times that way -- close, reopen, close, reopen -- once for each
    card the cursor happened to be over. The screen is only really finished when
    this goes too.
    """

    def __init__(self):
        img = cv2.imread(str(config.HIDE_TEMPLATE), cv2.IMREAD_GRAYSCALE)
        if img is None:
            raise FileNotFoundError(f"no hide-button template at {config.HIDE_TEMPLATE}")
        self.template = img
        self._prepped: dict = {}

    def score(self, img: np.ndarray) -> float:
        gray = _gray(img)
        h, w = gray.shape
        sx, sy = w / config.BASE_W, h / config.BASE_H
        bx0, by0, bx1, by1 = config.HIDE_BOX
        x0, y0 = round(bx0 * sx), round(by0 * sy)
        tw, th = max(4, round((bx1 - bx0) * sx)), max(4, round((by1 - by0) * sy))
        t = self._prepped.get((tw, th))
        if t is None:
            base = (self.template if self.template.shape == (th, tw)
                    else cv2.resize(self.template, (tw, th), interpolation=cv2.INTER_AREA))
            t = _edges(base)
            self._prepped[(tw, th)] = t
        patch = gray[y0:y0 + th, x0:x0 + tw]
        if patch.shape != (th, tw):
            return 0.0
        return float(cv2.matchTemplate(_edges(patch), t, cv2.TM_CCOEFF_NORMED)[0, 0])


@dataclass
class WindowState:
    open: bool = False
    opened_at: float = 0.0
    open_streak: int = 0
    miss_streak: int = 0
    hover_history: list[str] = field(default_factory=list)
    last_hover: str | None = None
    baseline: float = 0.0


def card_means(img: np.ndarray) -> dict[str, float]:
    gray = _gray(img)
    return {k: _mean(gray, box) for k, box in config.CARDS.items()}


def interior_darkness(img: np.ndarray) -> float:
    """Median card-interior brightness. Low means the cards have settled.

    During the entry animation the cards are washed out and bright (90+) while
    the reroll buttons already render normally, so the template score is high and
    misleading. Classifying rarity on such a frame gives a wrong answer.
    """
    gray = _gray(img)
    return float(np.median([_mean(gray, b) for b in config.CARD_INTERIORS.values()]))


def hovered_card(means: dict[str, float]) -> tuple[str | None, float]:
    spread = max(means.values()) - min(means.values())
    if spread <= config.HOVER_SPREAD:
        return None, spread
    return max(means, key=means.get), spread


def rarity_of(img: np.ndarray, slot: str | None = None) -> str:
    """Rarity from the card border colour.

    Gold and prismatic separate on hue; silver is simply darker, so it separates
    on value. Saturation does not work -- a prismatic frame measured 18.8, below
    an observed silver at 35.4.

    With `slot` it reads that one card's border. The three cards routinely differ
    in rarity, so the median over all three answers a question nobody asked.
    """
    if img.ndim == 2:
        return "unknown"
    h, w = img.shape[:2]
    hues, vals = [], []
    boxes = ([config.CARD_BORDERS[slot]] if slot in config.CARD_BORDERS
             else list(config.CARD_BORDERS.values()))
    for box in boxes:
        x0, y0, x1, y1 = _scaled(box, w, h)
        strip = img[max(0, y0):y1, max(0, x0):x1]
        if strip.size == 0:
            continue
        hsv = cv2.cvtColor(strip, cv2.COLOR_BGR2HSV)
        ang = hsv[:, :, 0].astype(float) * 2 * np.pi / 180.0
        hues.append((np.degrees(np.arctan2(np.sin(ang).mean(), np.cos(ang).mean())) / 2) % 180)
        vals.append(float(hsv[:, :, 2].mean()))
    if not hues:
        return "unknown"
    hue, val = float(np.median(hues)), float(np.median(vals))
    if val < config.RARITY_VALUE_SILVER:
        return "silver"
    if config.RARITY_HUE_PRISM[0] <= hue <= config.RARITY_HUE_PRISM[1]:
        return "prismatic"
    if config.RARITY_HUE_GOLD[0] <= hue <= config.RARITY_HUE_GOLD[1]:
        return "gold"
    return "unknown"
