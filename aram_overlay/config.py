"""Tunables and screen geometry.

Every pixel coordinate here is for 1920x1080 at 100% scale, measured from real
captures. Other resolutions need all of them rescaled -- see RESOLUTION_NOTE.
"""
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
ASSETS = Path(__file__).resolve().parent / "assets"
MODELS = ROOT / "models"
DATA = ROOT / "data"
STATE = ROOT / "state"

RESOLUTION_NOTE = "coordinates assume 1920x1080 @ 100% scale"
BASE_W, BASE_H = 1920, 1080

# --- OBS ---
OBS_HOST = "127.0.0.1"
OBS_PORT = 4455
OBS_PASSWORD = ""          # filled from config.json / env / CLI
OBS_SOURCE = "League of Legends"

# Detection runs on small JPEGs: PNG at 1920x1080 costs ~2755 ms per frame via
# GetSourceScreenshot, JPEG at 960x540 costs ~69 ms. Full-res frames are pulled
# only at the moment a selection is confirmed, for OCR.
DET_W, DET_H, DET_QUALITY = 960, 540, 80
OCR_QUALITY = 92

# --- reroll button template gate ---
# The three reroll buttons are plain UI glyphs, identical across silver/gold/
# prismatic, and TM_CCOEFF_NORMED ignores linear brightness/contrast shifts.
# That is what makes this survive map, lighting and rarity changes -- absolute
# brightness thresholds did not.
REROLL_CENTERS = [601, 968, 1337]
REROLL_ROW = (738, 790)
REROLL_PAD = 30
# The buttons have several visual states -- normal, greyed out, and cursor-
# highlighted -- so one template per state, best match wins per button. With the
# normal state alone the worst genuine window frame scored 0.226 while the worst
# gameplay frame scored 0.263: the classes actually overlapped.
TEMPLATE_GLOB = "tpl_reroll*.png"
TEMPLATE = ASSETS / "tpl_reroll.png"

# Measured over 15 labelled window frames vs 6 gameplay/HUD frames with the full
# template set: positives >= 0.659, negatives <= 0.352.
GATE_OPEN = 0.46           # all three buttons, for two consecutive frames
GATE_STAY = 0.40           # must stay ABOVE the highest observed negative
CLOSE_MISSES = 8           # consecutive failures before declaring the window shut

# --- card regions ---
CARDS = {"L": (440, 190, 760, 720), "M": (810, 190, 1130, 720), "R": (1180, 190, 1500, 720)}
CARD_INTERIORS = {"L": (520, 560, 680, 700), "M": (890, 560, 1050, 700),
                  "R": (1260, 560, 1420, 700)}
# thin strip across each card's top border -- the rarity colour lives here
CARD_BORDERS = {"L": (470, 188, 730, 206), "M": (840, 188, 1100, 206),
                "R": (1210, 188, 1470, 206)}

# Hover: the hovered card brightens while the other two hold their baseline.
# Measured within confirmed windows: no-hover spread <= 7.3, hover >= 16.4,
# across silver, gold and prismatic.
HOVER_SPREAD = 10.0

# Selection: the chosen card flares white and the other two fade out. Unlike a
# hover, the losers collapse well below baseline (0.51-0.70x observed).
SELECT_HIGH = 1.20         # winner vs baseline
SELECT_LOW = 0.75          # both losers vs baseline

ENTRY_ANIM_S = 0.6         # ignore hover during the card entry animation

# --- rarity, from the border strip ---
# Gold and prismatic separate on hue; silver is the dark one, so it separates on
# value. Saturation is NOT usable -- a prismatic frame measured sat 18.8, below
# an observed silver at 35.4.
RARITY_VALUE_SILVER = 160.0    # below this -> silver
RARITY_HUE_GOLD = (0, 50)
RARITY_HUE_PRISM = (95, 145)

# --- tooltip / OCR ---
# The tooltip box grows with description length but is anchored at a fixed top
# edge and centred on x=960. Cropping just the title band stops the recogniser
# reading a body line instead of the name.
TOOLTIP_TITLE = (620, 788, 1330, 842)
TOOLTIP_FULL = (620, 775, 1320, 1020)
OCR_UPSCALE = 3
OCR_MIN_SCORE = 0.45       # fuzzy-match score below this is treated as unknown

# --- augment data ---
CDRAGON_URL = ("https://raw.communitydragon.org/latest/plugins/rcp-be-lol-game-data/"
               "global/ko_kr/v1/cherry-augments.json")
CDRAGON_ASSET_BASE = "https://raw.communitydragon.org/latest/plugins/rcp-be-lol-game-data/global"

# --- Live Client Data API ---
LIVE_URL = "https://127.0.0.1:2999/liveclientdata/allgamedata"
MAYHEM_GAME_MODE = "KIWI"   # ARAM Mayhem's internal mode code

# --- widget ---
WIDGET_HOST = "127.0.0.1"
WIDGET_PORT = 8777
