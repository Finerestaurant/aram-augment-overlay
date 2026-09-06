"""Dump what Python's OCR path reads from each fixture, for the C# port to match.

The port changes two things under the recogniser: frames are decoded by WinRT
rather than PIL, and the upscale is a hand-written Lanczos rather than PIL's. The
recognised text has to come out the same anyway, because the match thresholds
were measured on these strings.

    python scripts/gen_ocr_expect.py    # writes tests/ocr_expect.json
"""
from __future__ import annotations

import json
import sys
from pathlib import Path

from PIL import Image

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from aram_overlay import config, settings  # noqa: E402
from aram_overlay.augments import AugmentDB  # noqa: E402
from aram_overlay.ocr import TooltipOCR  # noqa: E402

SCALES = (1, 2, 3)


def main() -> int:
    settings.apply()
    ocr = TooltipOCR(log=lambda *_: None)
    db = AugmentDB.load()

    def read(frame, box, scale):
        """The text, plus what the matcher makes of it -- the decision is what
        the port actually has to reproduce."""
        text = ocr.read_box(frame, box, scale)
        aug, score = db.match(text, None)
        return {"text": text, "match": aug.name if aug else None, "score": score,
                "accepted": score >= config.OCR_MIN_SCORE}

    rows = []
    fixtures = sorted((config.ROOT / "tests" / "fixtures").glob("*"))
    for path in fixtures:
        if path.suffix.lower() not in (".png", ".jpg", ".jpeg"):
            continue
        frame = Image.open(path).convert("RGB")
        for slot, box in config.CARD_TITLES.items():
            # The fixtures are all 1920x1080, but scale the box anyway so this
            # matches what the loop does with an arbitrary frame size.
            sx, sy = frame.width / config.BASE_W, frame.height / config.BASE_H
            scaled = (int(box[0] * sx), int(box[1] * sy),
                      int(box[2] * sx), int(box[3] * sy))
            for scale in SCALES:
                rows.append({"file": path.name, "slot": slot, "scale": scale,
                             "box": list(scaled), **read(frame, scaled, scale)})
        for scale in SCALES:
            box = config.HOVER_TOOLTIP
            rows.append({"file": path.name, "slot": "tooltip", "scale": scale,
                         "box": list(box), **read(frame, box, scale)})

    out = config.ROOT / "tests" / "ocr_expect.json"
    out.write_text(json.dumps(rows, ensure_ascii=False, indent=1), encoding="utf-8")
    nonempty = sum(1 for r in rows if r["text"])
    print(f"{len(rows)}건 중 {nonempty}건이 글자를 읽음 -> {out}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
