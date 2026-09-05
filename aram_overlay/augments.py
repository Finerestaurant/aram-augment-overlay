"""Augment database from CommunityDragon, plus name lookup.

`cherry-augments.json` holds 657 rows covering both Arena and ARAM Mayhem, and
there is no clean field to split them: Mayhem reuses Arena icon assets, so an
icon path of /Cherry/ does not mean Arena (상급 조준경 부착, 치명적인 공격,
선동 and others were all observed in Mayhem with /Cherry/ paths), and the
ARAM_ prefix misses entries like 선동 (RabbleRousing) and 마법공학의 영혼
(HextechSoul) that also appear in Mayhem.

So we do not filter. We match against all of it and disambiguate duplicate names
with the rarity read off the screen -- 처형자 exists twice, gold in Mayhem and
silver in Arena, and the card border tells us which one we are looking at.

Raw OCR of the tooltip is noisy ('상급조춘경부착', '치적인공격'), so matching is
fuzzy. That is safe here because the candidate set is closed and small.
"""
from __future__ import annotations

import difflib
import json
import time
from dataclasses import dataclass

import requests

from . import config


@dataclass(frozen=True)
class Augment:
    id: int
    name_id: str
    name: str
    rarity: str          # silver | gold | prismatic | other
    icon_url: str


_RARITY = {"kSilver": "silver", "kGold": "gold", "kPrismatic": "prismatic"}


def _icon_url(path: str) -> str:
    if not path:
        return ""
    p = path.lower()
    p = p.replace("/lol-game-data/assets", "")
    return config.CDRAGON_ASSET_BASE + p


class AugmentDB:
    def __init__(self, augments: list[Augment]):
        self.augments = augments
        self._norm = [(_squash(a.name), a) for a in augments]

    @classmethod
    def load(cls, refresh: bool = False, max_age_days: int = 7) -> "AugmentDB":
        config.DATA.mkdir(parents=True, exist_ok=True)
        cache = config.DATA / "augments.json"
        fresh = cache.exists() and (time.time() - cache.stat().st_mtime) < max_age_days * 86400
        if fresh and not refresh:
            raw = json.loads(cache.read_text(encoding="utf-8"))
        else:
            resp = requests.get(config.CDRAGON_URL, timeout=60)
            resp.raise_for_status()
            raw = resp.json()
            cache.write_text(json.dumps(raw, ensure_ascii=False), encoding="utf-8")

        out = []
        for row in raw:
            name = (row.get("nameTRA") or "").strip()
            if not name:
                continue
            out.append(Augment(
                id=row.get("id", -1),
                name_id=row.get("augmentNameId", ""),
                name=name,
                rarity=_RARITY.get(row.get("rarity", ""), "other"),
                icon_url=_icon_url(row.get("augmentSmallIconPath", "")),
            ))
        return cls(out)

    def match(self, text: str, rarity: str | None = None) -> tuple[Augment | None, float]:
        """Best fuzzy match for OCR text, preferring the given screen rarity.

        Returns (augment, score in 0..1). Score is the similarity of the
        whitespace-stripped strings -- OCR drops and mangles spaces constantly.
        """
        if not text:
            return None, 0.0
        q = _squash(text)
        pool = [(n, a) for n, a in self._norm if rarity is None or a.rarity == rarity]
        if not pool:
            pool = self._norm

        best, best_score = None, 0.0
        for norm, aug in pool:
            score = difflib.SequenceMatcher(None, q, norm).ratio()
            if score > best_score:
                best, best_score = aug, score
        return best, best_score


def _squash(s: str) -> str:
    return "".join(s.split())
