"""Dump difflib ratios for the C# port to check itself against.

The thresholds in config.py are measurements taken against Python's
difflib.SequenceMatcher. A reimplementation that is merely close puts every one
of them on a different footing, so the port is held to exact agreement on a
corpus built from the real augment names and the ways OCR actually breaks them.

    python scripts/gen_parity.py        # writes tests/difflib_parity.json
"""
from __future__ import annotations

import json
import random
import sys
from difflib import SequenceMatcher
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from aram_overlay import config, settings  # noqa: E402
from aram_overlay.augments import AugmentDB, _squash, _strip_final  # noqa: E402

HANGUL_START, HANGUL_END = 0xAC00, 0xD7A3
# Real misreads seen in play, kept verbatim -- these are the cases the
# thresholds were drawn around.
OBSERVED = [
    ("지명적인 공격", "치명적인 공격"),
    ("과층전", "과충전"),
    ("무 리 호", "무리한 진입"),
    ("민음적한두>", "믿음직한 무기"),
    ("끄어느하사", "끝없는 학살"),
    ("버라", "범람"),
    ("피보", "핀볼"),
    ("상급조춘경부착", "상급 조준경 부착"),
    ("", "궁극의 히드라"),
    ("궁극의 히드라", ""),
    ("", ""),
]


def corrupt(name: str, rng: random.Random) -> str:
    """One plausible OCR failure: a dropped syllable, a swap, a lost 받침, junk."""
    if not name:
        return name
    mode = rng.randrange(5)
    chars = list(name)
    if mode == 0:
        del chars[rng.randrange(len(chars))]
    elif mode == 1 and len(chars) > 1:
        i = rng.randrange(len(chars) - 1)
        chars[i], chars[i + 1] = chars[i + 1], chars[i]
    elif mode == 2:
        i = rng.randrange(len(chars))
        chars[i] = chr(rng.randint(HANGUL_START, HANGUL_END))
    elif mode == 3:
        return _strip_final(name)
    else:
        chars.append(rng.choice("<>[]{}|"))
    return "".join(chars)


def main() -> int:
    settings.apply()
    names = [a.name for a in AugmentDB.load().augments]
    rng = random.Random(20260906)

    pairs: list[tuple[str, str]] = list(OBSERVED)
    for _ in range(1500):
        name = rng.choice(names)
        pairs.append((corrupt(name, rng), name))
    for _ in range(700):
        pairs.append((rng.choice(names), rng.choice(names)))
    for _ in range(300):
        a, b = rng.choice(names), rng.choice(names)
        pairs.append((_strip_final(_squash(a)), _strip_final(_squash(b))))

    rows = [{"a": a, "b": b, "ratio": SequenceMatcher(None, a, b).ratio()}
            for a, b in pairs]
    out = config.ROOT / "tests" / "difflib_parity.json"
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(json.dumps(rows, ensure_ascii=False), encoding="utf-8")
    print(f"{len(rows)}쌍 -> {out}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
