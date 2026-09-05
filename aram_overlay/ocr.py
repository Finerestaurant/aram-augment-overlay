"""Korean tooltip OCR on the OCR engine built into Windows.

The tool is Windows-only already -- OBS, the game and every path here assume it
-- so Windows.Media.Ocr costs nothing to reach and beats the alternatives on
this workload. Measured against the five captures with known answers
(tests/fixtures plus one silver capture taken live):

    korean_mobile_v2.0 via rapidocr   4/5, mean match 0.65, 87 ms, ~64 MB of wheels
    Windows.Media.Ocr                 5/5, every match 1.00,  6 ms, ~3 MB of wheels

The one it used to miss is the silver 믿음직한 무기, read as '민음적한두>'. The
Windows engine also ignores the augment icon sitting at the left of the crop,
which the ONNX recogniser turned into leading junk like '[*}' on every read.

Native resolution first, 3x only as a retry. Upscaling was mandatory for the
small ONNX model and mostly hurts here -- 상급 조준경 부착 reads exactly at 1x and
collapses to '사그 入 八2혜 曰 차 O 曰' at 3x -- but short names are the exception:
과충전 comes back empty at 1x and 2x, and only appears at 3x (as 과층전, which the
closed-set match still resolves). Retrying only when 1x reads nothing costs the
extra call on the rare frame that needs it.

The Korean OCR language pack ships with Korean Windows and is what makes this
work; `available()` reports whether it is present so startup can say so plainly
rather than failing at the first augment.
"""
from __future__ import annotations

import asyncio
import io

from PIL import Image

# Imported here rather than lazily: a missing namespace package must fail at
# startup. Reached through attribute access it surfaces as an empty read at the
# moment an augment is picked, which looks exactly like a recognition failure.
from winrt.windows.globalization import Language
from winrt.windows.graphics.imaging import BitmapDecoder
from winrt.windows.media.ocr import OcrEngine
from winrt.windows.storage.streams import DataWriter, InMemoryRandomAccessStream
import winrt.windows.foundation.collections  # noqa: F401  -- needed to iterate result.lines

from . import config

_LANG_TAGS = ("ko-KR", "ko")



def _engine():
    """The Korean OCR engine, or None when the language pack is missing."""
    for tag in _LANG_TAGS:
        lang = Language(tag)
        if OcrEngine.is_language_supported(lang):
            eng = OcrEngine.try_create_from_language(lang)
            if eng is not None:
                return eng
    return None


def available() -> bool:
    try:
        return _engine() is not None
    except Exception:
        return False


def installed_languages() -> list[str]:
    try:
        return [l.language_tag for l in OcrEngine.get_available_recognizer_languages()]
    except Exception:
        return []


class TooltipOCR:
    def __init__(self, log=print):
        self._log = log
        self._engine = _engine()
        if self._engine is None:
            langs = ", ".join(installed_languages()) or "(없음)"
            raise RuntimeError(
                "Windows 한국어 OCR을 사용할 수 없습니다.\n"
                f"    현재 사용 가능한 언어: {langs}\n"
                "    관리자 PowerShell에서 다음을 실행한 뒤 다시 시도하세요:\n"
                "    Add-WindowsCapability -Online -Name 'Language.OCR~~~ko-KR~0.0.1.0'"
            )

    async def _read(self, img: Image.Image) -> str:
        buf = io.BytesIO()
        img.save(buf, format="PNG")
        stream = InMemoryRandomAccessStream()
        writer = DataWriter(stream.get_output_stream_at(0))
        writer.write_bytes(buf.getvalue())
        await writer.store_async()
        decoder = await BitmapDecoder.create_async(stream)
        bitmap = await decoder.get_software_bitmap_async()
        result = await self._engine.recognize_async(bitmap)
        return " ".join(line.text for line in result.lines).strip()

    def read_box(self, frame: Image.Image, box, scale: int = 1) -> str:
        """One reading of one region, at one scale."""
        crop = frame.crop(box)
        if scale != 1:
            crop = crop.resize((crop.width * scale, crop.height * scale), Image.LANCZOS)
        try:
            return asyncio.run(self._read(crop))
        except Exception as exc:
            self._log(f"  OCR 호출 실패: {type(exc).__name__}: {exc}")
            return ""
