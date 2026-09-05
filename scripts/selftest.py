"""Offline check of the whole recognition chain against known captures.

Runs without OBS or a live game, so it also works as an install check: if this
passes, the detection thresholds, the OCR model and the augment database are all
wired up correctly on your machine.

    python scripts/selftest.py
"""
from __future__ import annotations

import sys
from pathlib import Path

import cv2

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from aram_overlay import config, detect                      # noqa: E402
from aram_overlay.augments import AugmentDB                  # noqa: E402
from aram_overlay.ocr import TooltipOCR                      # noqa: E402
from PIL import Image                                        # noqa: E402

# (file, expected augment name, expected rarity, window should be detected)
CASES = [
    ("tests/fixtures/gamecap.png", None, "gold", True),
    ("tests/fixtures/frame_05.png", "광휘의 검 업그레이드", "gold", True),
    ("tests/fixtures/aug_lv7_11.png", "궁극의 히드라", "prismatic", True),
    ("tests/fixtures/lv15_03_hovM.png", "치명적인 공격", "gold", True),
    ("tests/fixtures/01_lv7_hovL.jpg", "상급 조준경 부착", "gold", True),
    ("tests/fixtures/hud_clean_lv9.png", None, None, False),   # ordinary gameplay
    ("tests/fixtures/aug_lv11_05.png", None, None, False),     # combat, no window
]


def main() -> int:
    root = Path(__file__).resolve().parent.parent
    print("증강 데이터 불러오는 중...")
    db = AugmentDB.load()
    print(f"  {len(db.augments)}종\n")
    print("OCR 모델 준비 중...")
    ocr = TooltipOCR()
    gate = detect.TemplateGate()
    print()

    passed = failed = skipped = 0
    for rel, expect_name, expect_rarity, expect_window in CASES:
        path = root / rel
        if not path.exists():
            print(f"SKIP  {rel} (파일 없음)")
            skipped += 1
            continue
        bgr = cv2.imread(str(path))
        if bgr is None:
            print(f"SKIP  {rel} (읽기 실패)")
            skipped += 1
            continue

        scores = gate.scores(bgr)
        is_open = min(scores) >= config.GATE_OPEN
        ok = is_open == expect_window
        detail = f"gate={min(scores):.2f} open={is_open}"

        if is_open and expect_name:
            rarity = detect.rarity_of(bgr)
            raw, _ = ocr.read_title(Image.open(path).convert("RGB"))
            aug, score = db.match(raw, rarity if rarity in ("silver", "gold", "prismatic") else None)
            got = aug.name if aug else None
            ok = ok and got == expect_name
            detail += f" rarity={rarity} ocr='{raw}' -> '{got}' ({score:.2f})"
            if expect_rarity and rarity != expect_rarity:
                detail += f"  [등급 기대 {expect_rarity}]"

        print(f"{'PASS' if ok else 'FAIL'}  {rel}\n      {detail}")
        passed += ok
        failed += not ok

    total = passed + failed
    print(f"\n{passed}/{total} 통과" + (f", {skipped}건 건너뜀" if skipped else ""))
    return 0 if failed == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
