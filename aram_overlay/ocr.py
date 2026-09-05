"""Korean tooltip OCR on the ONNX runtime.

PaddleOCR's Korean PP-OCRv5 model reads these tooltips perfectly (4/4 exact on
the test captures) but drags in paddlepaddle at 380 MB, which is unreasonable to
download on a PC-bang machine. The only Korean ONNX recogniser available is the
older mobile v2.0 at 3.14 MB, and on its own it is bad -- 0/4 exact, producing
things like '상극조존경 브착' and missing the title line entirely.

Two changes make it work anyway, 4/4:
  * crop the title band only, so it cannot read a body line by mistake
  * fuzzy-match the result against the closed augment name set

The charset ships inside the ONNX file's metadata (key 'character'), so no
separate dictionary is needed.
"""
from __future__ import annotations

import numpy as np
import requests
from PIL import Image

from . import config

MODEL_URL = ("https://huggingface.co/spaces/RapidAI/RapidOCR/resolve/main/"
             "models/text_rec/korean_mobile_v2.0_rec_infer.onnx")
MODEL_NAME = "korean_mobile_v2.0_rec_infer.onnx"


def ensure_model(log=print) -> "str":
    config.MODELS.mkdir(parents=True, exist_ok=True)
    path = config.MODELS / MODEL_NAME
    if path.exists() and path.stat().st_size > 1_000_000:
        return str(path)
    log(f"한국어 OCR 모델 내려받는 중 ({MODEL_NAME}, 약 3 MB)...")
    resp = requests.get(MODEL_URL, timeout=300)
    resp.raise_for_status()
    path.write_bytes(resp.content)
    log(f"  저장됨: {path}")
    return str(path)


class TooltipOCR:
    def __init__(self, log=print):
        from rapidocr_onnxruntime import RapidOCR
        self._ocr = RapidOCR(rec_model_path=ensure_model(log))

    def read_title(self, frame: Image.Image) -> tuple[str, float]:
        """Read the augment name from a full 1920x1080 frame.

        Returns (raw text, mean recogniser score). The text is deliberately
        returned unpolished; AugmentDB.match does the cleanup.
        """
        crop = frame.crop(config.TOOLTIP_TITLE)
        crop = crop.resize((crop.width * config.OCR_UPSCALE,
                            crop.height * config.OCR_UPSCALE), Image.LANCZOS)
        res, _ = self._ocr(np.array(crop))
        if not res:
            return "", 0.0
        # everything in this band is one line; join left to right
        items = sorted(res, key=lambda r: min(p[0] for p in r[0]))
        text = " ".join(r[1] for r in items)
        score = float(np.mean([float(r[2]) for r in items]))
        return text.strip(), score

    def has_tooltip(self, frame: Image.Image) -> bool:
        """Whether a tooltip panel is showing.

        The panel is a dark box over brighter game art, so the band drops sharply
        when it appears. Only meaningful once the window is confirmed open --
        ordinary gameplay sits between the hover and no-hover values.
        """
        band = frame.crop((900, 786, 1020, 860)).convert("L")
        return float(np.asarray(band).mean()) < 35.0
