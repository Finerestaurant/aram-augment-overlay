"""Dump what the Python detector computes on each fixture, for the C# port.

The gate thresholds (0.45 / 0.30 / 0.38) are measurements of what OpenCV's
TM_CCOEFF_NORMED returned on these frames. The port reimplements cvtColor,
Sobel, INTER_AREA and matchTemplate, so it has to land on the same numbers or
those thresholds no longer mean what docs/FINDINGS.md says they mean.

    python scripts/gen_detect_expect.py     # writes tests/detect_expect.json
"""
from __future__ import annotations

import json
import sys
from pathlib import Path

import cv2
import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from aram_overlay import config, detect, settings  # noqa: E402


def main() -> int:
    settings.apply()
    gate = detect.TemplateGate()
    hide = detect.HideButton()

    rows = []
    for path in sorted((config.ROOT / "tests" / "fixtures").glob("*")):
        if path.suffix.lower() not in (".png", ".jpg", ".jpeg"):
            continue
        full = cv2.imread(str(path), cv2.IMREAD_COLOR)
        if full is None:
            continue

        # The loop gates on small JPEGs and only pulls full resolution for OCR,
        # so both sizes are checked here.
        small = cv2.resize(full, (config.DET_W, config.DET_H), interpolation=cv2.INTER_AREA)
        for label, img in (("full", full), ("det", small)):
            gray = detect._gray(img)
            means = detect.card_means(img)
            slot, spread = detect.hovered_card(means)
            rows.append({
                "file": path.name,
                "size": label,
                "width": int(img.shape[1]),
                "height": int(img.shape[0]),
                "gate": [float(s) for s in gate.scores(img)],
                "hide": float(hide.score(img)),
                "means": {k: float(v) for k, v in means.items()},
                "interior": float(detect.interior_darkness(img)),
                "hover": slot,
                "spread": float(spread),
                "rarity": {s: detect.rarity_of(img, s) for s in ("L", "M", "R")},
                "gray_mean": float(np.mean(gray)),
                # Per-channel means separate a decoding difference from an
                # arithmetic one: if these already disagree, the two image
                # decoders disagree and nothing downstream can be exact.
                "bgr_mean": [float(img[:, :, c].mean()) for c in range(3)],
            })

    out = config.ROOT / "tests" / "detect_expect.json"
    out.write_text(json.dumps(rows, ensure_ascii=False, indent=1), encoding="utf-8")
    print(f"{len(rows)}건 -> {out}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
