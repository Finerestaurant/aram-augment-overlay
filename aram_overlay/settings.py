"""User settings: the few things worth changing without editing code.

Stored in `config.json` next to run.bat -- the same file the OBS password already
lived in, so an existing install keeps working untouched. Applying a setting
overwrites the matching name in `config`, which keeps the rest of the code
reading plain module constants and knowing nothing about any of this.

Resolution deserves a word. Every box in `config` is written in 1920x1080 pixels
and `detect._scaled` divides by BASE_W/BASE_H, so the geometry is really
*proportional*: the frames come from OBS scaled to whatever size we ask for, and
a 16:9 screen of any size lands on the same layout. Setting a resolution here
therefore rescales the boxes and grabs the OCR frame at that size -- at 1440p or
2160p that is sharper text for the same coordinates. What it cannot fix is a
screen that is not 16:9, where the client anchors its HUD differently and the
boxes have to be redrawn with scripts/box_editor.py.
"""
from __future__ import annotations

import json

from . import config

# CommunityDragon publishes one folder per client language; the key is the URL
# segment. Restricted to the locales the augment data actually ships in.
LOCALES = {
    "ko_kr": "한국어",       "en_us": "English",     "ja_jp": "日本語",
    "zh_cn": "中文(简体)",   "zh_tw": "中文(繁體)",  "es_es": "Español",
    "es_mx": "Español (MX)", "fr_fr": "Français",   "de_de": "Deutsch",
    "it_it": "Italiano",    "pl_pl": "Polski",      "ru_ru": "Русский",
    "pt_br": "Português",   "tr_tr": "Türkçe",      "vi_vn": "Tiếng Việt",
    "th_th": "ไทย",
}

# Windows OCR tag that goes with each client language, most specific first --
# Windows itself falls back from ko-KR to ko, but only if ko-KR is absent.
OCR_FOR_LOCALE = {
    "ko_kr": ("ko-KR", "ko"),          "en_us": ("en-US", "en"),
    "ja_jp": ("ja-JP", "ja"),          "zh_cn": ("zh-Hans-CN", "zh-Hans"),
    "zh_tw": ("zh-Hant-TW", "zh-Hant"), "es_es": ("es-ES", "es"),
    "es_mx": ("es-MX", "es"),          "fr_fr": ("fr-FR", "fr"),
    "de_de": ("de-DE", "de"),          "it_it": ("it-IT", "it"),
    "pl_pl": ("pl-PL", "pl"),          "ru_ru": ("ru-RU", "ru"),
    "pt_br": ("pt-BR", "pt"),          "tr_tr": ("tr-TR", "tr"),
    "vi_vn": ("vi-VN", "vi"),          "th_th": ("th-TH", "th"),
}

DEFAULTS = {
    "locale": "ko_kr",
    "ocr_language": "",              # "" -> whatever matches the locale
    "screen_width": 1920,
    "screen_height": 1080,
    "obs_port": 4455,
    "obs_password": "",              # "" -> read from OBS's own config
    "obs_source": "League of Legends",
    "widget_port": 8777,
    "widget_rows": 4,
    "widget_max_width": 420,
}

PATH = config.ROOT / "config.json"

# The shipped coordinates, kept as written so rescaling always starts from them
# rather than from an already-scaled set. Captured after config applied
# state/boxes.json, so a hand-drawn box survives a resolution change.
_BASE = {
    "size": (config.BASE_W, config.BASE_H),
    "reroll_boxes": list(config.REROLL_BOXES),
    "reroll_size": tuple(config.REROLL_SIZE),
    "cards": dict(config.CARDS),
    "card_interiors": dict(config.CARD_INTERIORS),
    "card_borders": dict(config.CARD_BORDERS),
    "card_titles": dict(config.CARD_TITLES),
    "hover_tooltip": tuple(config.HOVER_TOOLTIP),
    "hide_box": tuple(config.HIDE_BOX),
}


def load() -> dict:
    out = dict(DEFAULTS)
    try:
        saved = json.loads(PATH.read_text(encoding="utf-8"))
    except Exception:
        return out
    for key, default in DEFAULTS.items():
        if key not in saved:
            continue
        try:
            out[key] = int(saved[key]) if isinstance(default, int) else str(saved[key])
        except (TypeError, ValueError):
            pass
    return out


def save(values: dict) -> dict:
    merged = load()
    merged.update({k: v for k, v in values.items() if k in DEFAULTS})
    PATH.write_text(json.dumps(merged, ensure_ascii=False, indent=2), encoding="utf-8")
    return merged


def is_supported_shape(width: int, height: int) -> bool:
    """16:9 within a pixel of rounding. Anything else needs the box editor."""
    return height > 0 and abs(width / height - 16 / 9) < 0.01


def _scale_box(box, sx: float, sy: float):
    x0, y0, x1, y1 = box
    return (round(x0 * sx), round(y0 * sy), round(x1 * sx), round(y1 * sy))


def apply(values: dict | None = None) -> dict:
    v = values if values is not None else load()

    loc = v["locale"] if v["locale"] in LOCALES else DEFAULTS["locale"]
    config.LOCALE = loc
    config.CDRAGON_URL = (f"https://raw.communitydragon.org/latest/plugins/"
                          f"rcp-be-lol-game-data/global/{loc}/v1/cherry-augments.json")
    config.CDRAGON_ITEMS_URL = (f"https://raw.communitydragon.org/latest/plugins/"
                                f"rcp-be-lol-game-data/global/{loc}/v1/items.json")
    tag = (v.get("ocr_language") or "").strip()
    config.OCR_LANGUAGES = (tag,) if tag else OCR_FOR_LOCALE.get(loc, ("en-US", "en"))

    w, h = int(v["screen_width"]), int(v["screen_height"])
    bw, bh = _BASE["size"]
    if w > 0 and h > 0:
        sx, sy = w / bw, h / bh
        config.BASE_W, config.BASE_H = w, h
        config.REROLL_BOXES = [(round(x * sx), round(y * sy))
                               for x, y in _BASE["reroll_boxes"]]
        config.REROLL_SIZE = (round(_BASE["reroll_size"][0] * sx),
                              round(_BASE["reroll_size"][1] * sy))
        for name, key in (("CARDS", "cards"), ("CARD_INTERIORS", "card_interiors"),
                          ("CARD_BORDERS", "card_borders"), ("CARD_TITLES", "card_titles")):
            setattr(config, name, {k: _scale_box(b, sx, sy) for k, b in _BASE[key].items()})
        config.HOVER_TOOLTIP = _scale_box(_BASE["hover_tooltip"], sx, sy)
        config.HIDE_BOX = _scale_box(_BASE["hide_box"], sx, sy)

    config.OBS_PORT = int(v["obs_port"])
    config.OBS_PASSWORD = v["obs_password"]
    config.OBS_SOURCE = v["obs_source"]
    config.WIDGET_PORT = int(v["widget_port"])
    config.WIDGET_ROWS = max(1, int(v["widget_rows"]))
    config.WIDGET_MAX_W = max(120, int(v["widget_max_width"]))
    return v
