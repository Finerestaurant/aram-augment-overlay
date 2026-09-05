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

    def scores(self, img: np.ndarray) -> list[float]:
        """Best match per button, one score for each of the three."""
        gray = _gray(img)
        h, w = gray.shape
        sx, sy = w / config.BASE_W, h / config.BASE_H
        tpls = self.templates
        if abs(sx - 1.0) > 1e-3 or abs(sy - 1.0) > 1e-3:
            tpls = [cv2.resize(t, (max(4, int(t.shape[1] * sx)), max(4, int(t.shape[0] * sy))),
                               interpolation=cv2.INTER_AREA) for t in tpls]
        pad = max(4, int(config.REROLL_PAD * sx))
        y0 = int(config.REROLL_ROW[0] * sy) - pad
        y1 = int(config.REROLL_ROW[1] * sy) + pad
        out = []
        for cx in config.REROLL_CENTERS:
            cxs = int(cx * sx)
            best = 0.0
            for tpl in tpls:
                th, tw = tpl.shape
                band = gray[max(0, y0):y1, max(0, cxs - tw // 2 - pad):cxs + tw // 2 + pad]
                if band.shape[0] >= th and band.shape[1] >= tw:
                    best = max(best, float(cv2.matchTemplate(band, tpl,
                                                             cv2.TM_CCOEFF_NORMED).max()))
            out.append(best)
        return out


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


def selected_card(means: dict[str, float], baseline: float) -> str | None:
    """The confirmation animation, which is distinct from a hover.

    On hover the two other cards hold their baseline; on selection they collapse
    to roughly half of it while the chosen card flares.
    """
    if baseline <= 0:
        return None
    winner = max(means, key=means.get)
    if means[winner] < baseline * config.SELECT_HIGH:
        return None
    losers = [v for k, v in means.items() if k != winner]
    if all(v < baseline * config.SELECT_LOW for v in losers):
        return winner
    return None


def rarity_of(img: np.ndarray) -> str:
    """Rarity from the card border colour.

    Gold and prismatic separate on hue; silver is simply darker, so it separates
    on value. Saturation does not work -- a prismatic frame measured 18.8, below
    an observed silver at 35.4.
    """
    if img.ndim == 2:
        return "unknown"
    h, w = img.shape[:2]
    hues, vals = [], []
    for box in config.CARD_BORDERS.values():
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
