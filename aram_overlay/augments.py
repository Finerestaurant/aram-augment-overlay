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
        self._norm = [(_squash(a.name), _strip_final(_squash(a.name)), a)
                      for a in augments]

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

        Rarity ranks, it does not filter. It used to filter, and a misread border
        then put the right answer out of reach entirely: 믿음직한 무기 (silver)
        read exactly, but with the border seen as gold the pool held only gold
        augments and the pick came back 환영 무기. Worse, 적응형 능력치 (silver)
        landed on 능력치의 순환 at 0.50 -- over the threshold, so it was published
        as a confident wrong name rather than a failure. Ranking keeps the border
        useful for genuine ties while letting a good text match win.
        """
        if not text:
            return None, 0.0
        q = _squash(text)

        qs = _strip_final(q)

        best, best_ranked, best_score = None, 0.0, 0.0
        for norm, norm_s, aug in self._norm:
            score = max(difflib.SequenceMatcher(None, q, norm).ratio(),
                        difflib.SequenceMatcher(None, qs, norm_s).ratio())
            ranked = score + (config.RARITY_BONUS if rarity and aug.rarity == rarity else 0.0)
            if ranked > best_ranked:
                best, best_ranked, best_score = aug, ranked, score
        return best, best_score


def _squash(s: str) -> str:
    return "".join(s.split())


def _strip_final(s: str) -> str:
    """Drop the final consonant from every Hangul syllable.

    The Windows recogniser sometimes loses every 받침 in a line at once, keeping
    the syllable count and order: 끝없는 학살 comes back as 끄어느하사, 범람 as
    버라, 핀볼 as 피보. Compared as written those score 0.25, 0.40 and 0.50 and
    match the wrong augment; compared with finals removed on both sides they are
    exact. Kept as a second opinion rather than a replacement -- it collapses
    real distinctions too, so it only ever raises a score, never lowers one.
    """
    out = []
    for ch in s:
        code = ord(ch)
        if 0xAC00 <= code <= 0xD7A3:
            out.append(chr(0xAC00 + ((code - 0xAC00) // 28) * 28))
        else:
            out.append(ch)
    return "".join(out)
