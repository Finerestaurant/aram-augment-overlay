"""Item names, used only to tell an anvil apart from an augment.

Mayhem hands out item anvils as well as augments, and the two selection screens
are close enough that the reroll-button gate opens for both. The tooltip then
holds an item name, which fuzzy-matched against the augment list alone still
clears the threshold and publishes a wrong augment: 밴시의 장막 scores 0.80
against 감시의 장막, 무한의 대검 scores 0.67 against 무한의 대검 업그레이드.

Matching against both closed sets settles it without needing to recognise the
anvil screen visually -- whichever set the text is closer to is the set it came
from. Measured on eight item names the item side won every time (1.00 against
0.33-0.80), and on six augment names the augment side won or tied.
"""
from __future__ import annotations

import difflib
import json
import time

import requests

from . import config
from .augments import _strip_final


class ItemNames:
    def __init__(self, names: list[str]):
        # Same 받침 fallback as the augment side, so the two sets stay comparable
        # when deciding whether a screen is an anvil.
        self._norm = [(_squash(n), _strip_final(_squash(n))) for n in names]

    @classmethod
    def load(cls, refresh: bool = False, max_age_days: int = 7) -> "ItemNames":
        config.DATA.mkdir(parents=True, exist_ok=True)
        cache = config.DATA / "items.json"
        fresh = cache.exists() and (time.time() - cache.stat().st_mtime) < max_age_days * 86400
        if fresh and not refresh:
            raw = json.loads(cache.read_text(encoding="utf-8"))
        else:
            resp = requests.get(config.CDRAGON_ITEMS_URL, timeout=60)
            resp.raise_for_status()
            raw = resp.json()
            cache.write_text(json.dumps(raw, ensure_ascii=False), encoding="utf-8")
        return cls([n for n in ((row.get("name") or "").strip() for row in raw) if n])

    def best_score(self, text: str) -> float:
        """How closely the text matches any item name, 0..1."""
        if not text:
            return 0.0
        q = _squash(text)
        qs = _strip_final(q)
        return max((max(difflib.SequenceMatcher(None, q, n).ratio(),
                        difflib.SequenceMatcher(None, qs, ns).ratio())
                    for n, ns in self._norm), default=0.0)


def _squash(s: str) -> str:
    return "".join(s.split())
