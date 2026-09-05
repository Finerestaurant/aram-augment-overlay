"""Tunables and screen geometry.

Every pixel coordinate here is for 1920x1080 at 100% scale, measured from real
captures. Other resolutions need all of them rescaled -- see RESOLUTION_NOTE.
"""
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
ASSETS = Path(__file__).resolve().parent / "assets"
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
# Top-left of each reroll box, measured -- not guessed. The boxes do not move,
# so nothing is gained by looking around for them, and a wandering match is free
# score for a negative frame: the old padded search drifted onto the cursor when
# it sat on a button, and opened the gate on the item shop.
#
# These are the values drawn on a real frame in scripts/box_editor.py, and the
# templates in assets/ are cut to them. Earlier numbers here came from running
# that padded search over confirmed windows and taking the mode, which put the
# buttons ~4px off and 8px too narrow. state/boxes.json, if present, wins over
# these -- but the shipped defaults have to be the ones the templates match.
REROLL_BOXES = [(568, 743), (936, 743), (1304, 743)]
REROLL_SIZE = (68, 41)
# The buttons have several visual states -- normal, greyed out, and cursor-
# highlighted -- so one template per state, best match wins per button. With the
# normal state alone the worst genuine window frame scored 0.226 while the worst
# gameplay frame scored 0.263: the classes actually overlapped.
TEMPLATE_GLOB = "tpl_reroll*.png"
TEMPLATE = ASSETS / "tpl_reroll.png"

# Measured over 15 labelled window frames vs 6 gameplay/HUD frames with the full
# template set: positives >= 0.659, negatives <= 0.352.
# Nine confirmed windows score 0.71 at worst; six negatives (ordinary gameplay
# and the item shop, whose icon row sits at the same height) score 0.52 at best,
# and that 0.52 -- the 챔피언 아이템 상점 banner -- was opening the gate at the
# old 0.46. 0.60 sits in the gap with 0.11 either side.
GATE_OPEN = 0.45           # all three buttons, for two consecutive frames
GATE_STAY = 0.30           # must stay ABOVE the highest observed negative
# Staying open used to reuse GATE_OPEN for its "at least two buttons" clause, so
# raising the open threshold quietly tightened the close rule too. It is its own
# number now. Closing early is expensive: the close is what publishes the pick,
# and a window declared shut while the player is still rerolling publishes an
# augment they did not take.
GATE_STAY_TWO = 0.38
CLOSE_MISSES = 16          # consecutive failures before declaring the window shut

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

# Selection used to be read off a confirmation animation -- winner flares, the
# other two collapse to 0.51-0.70x baseline. That no longer happens. Over three
# confirmed windows the winner passed 1.20 thirty-nine times while the lowest
# loser ever seen was 0.96, nowhere near the 0.75 it needed; the losers hold
# baseline, which is a hover. The pick is now taken from the window closing
# instead, so no thresholds here.

ENTRY_ANIM_S = 0.6         # ignore hover during the card entry animation

# --- rarity, from the border strip ---
# Gold and prismatic separate on hue; silver is the dark one, so it separates on
# value. Saturation is NOT usable -- a prismatic frame measured sat 18.8, below
# an observed silver at 35.4.
RARITY_VALUE_SILVER = 160.0    # below this -> silver
RARITY_HUE_GOLD = (0, 50)
RARITY_HUE_PRISM = (95, 145)

# --- card titles ---
# The augment name is printed on the card itself and stays there for as long as
# the window is open. The tooltip only exists while a card is hovered, and
# reading that band when nothing is hovered returns whatever else is on screen --
# that is how 궁극기 봇 was published as 무리한 진입, off a stray '무 리 호'.
# Measured across ten confirmed windows: 27 of 30 titles read exactly at 1x-2x
# with the first cut of these boxes; redrawn pixel-exact on a real frame, the
# six windows kept from that set read 18 of 18.
CARD_TITLES = {"L": (460, 420, 738, 463), "M": (828, 420, 1106, 463),
               "R": (1196, 420, 1474, 463)}
# Tried in order per card, stopping at the first confident match. Long names are
# exact at 1x; short ones need far more -- 범람 first appears at 4x, 핀볼 at 5x.
CARD_SCALES = (1, 2, 3, 4, 5)

# Title band of the hover tooltip. Its name is whichever card the cursor is on,
# so matching it against the three titles says which card is about to be taken --
# directly, instead of inferring it from which card looks brightest. Brightness
# picked the wrong card on a window whose hover ran L 21 / M 13 / R 15 with no
# clear winner; the tooltip read 가속 추구, which is the R card, and the match
# history agreed. Correct on all eight frames kept from real games.
HOVER_TOOLTIP = (620, 788, 1330, 842)

# The taken card flares. On the frame the titles were last read from, the winner
# runs 1.24-1.69x the next brightest and is the right card in all nine captures
# with a known answer; the one frame where the ratio sat at 1.04 is also the one
# where brightness picked the wrong card. So the flare is trusted only when it is
# decisive, and the tooltip answers otherwise.
SELECT_FLARE = 1.15
# How far back from the close to look for it. The close is declared CLOSE_MISSES
# frames after the screen stopped answering, so the pick sits a beat before that;
# search further and a reroll animation from earlier in the window wins instead.
FLARE_LOOKBACK_S = 2.5

# The augment screen can be tucked away with a button under the cards; the cards
# and the reroll buttons vanish while that button stays. Watching only the reroll
# buttons read that as "window closed" and published a pick, then read the screen
# coming back as a fresh window -- one augment recorded three times, once per
# card the cursor passed over. The window is over only when this goes too.
HIDE_BOX = (857, 823, 1066, 890)
HIDE_TEMPLATE = ASSETS / "tpl_hide.png"
# Measured on the 960x540 detection frames the loop actually uses: the button
# reads 0.49-0.65 whenever the augment screen exists at all, and -0.05-0.24 on
# ordinary play and the item shop.
HIDE_PRESENT = 0.35

# --- name matching ---
# Eleven reads confirmed correct in play scored 1.00, except 과층전 -> 과충전 at
# 0.67. Six wrong or meaningless reads topped out at 0.50 -- '무 리 호' reaching
# 무리한 진입, which the old 0.45 let through and put on stream in place of
# 궁극기 봇. Nothing sits between 0.50 and 0.67, and on a stream a wrong name is
# worse than no name.
OCR_MIN_SCORE = 0.60       # fuzzy-match score below this is treated as unknown

# Screen rarity ranks candidates, it does not filter them. Big enough to break a
# tie between two augments sharing a name (처형자 exists as gold and silver),
# small enough that a clearly better text match still wins -- 믿음직한 무기 read
# exactly at 1.00 must beat 환영 무기 at 0.40 even when the border was misread as
# gold, which is precisely what the hard filter used to get wrong.
RARITY_BONUS = 0.05

# --- augment data ---
CDRAGON_URL = ("https://raw.communitydragon.org/latest/plugins/rcp-be-lol-game-data/"
               "global/ko_kr/v1/cherry-augments.json")
# The "/default" is not optional: without it every icon 404s, which the widget
# swallows silently because a broken <img> just hides itself.
CDRAGON_ASSET_BASE = ("https://raw.communitydragon.org/latest/plugins/rcp-be-lol-game-data/"
                      "global/default")
# Item names, used to reject anvil screens -- see items.py.
CDRAGON_ITEMS_URL = ("https://raw.communitydragon.org/latest/plugins/rcp-be-lol-game-data/"
                     "global/ko_kr/v1/items.json")

# --- Live Client Data API ---
LIVE_URL = "https://127.0.0.1:2999/liveclientdata/allgamedata"
MAYHEM_GAME_MODE = "KIWI"   # ARAM Mayhem's internal mode code

# Boxes drawn in scripts/box_editor.py win over the values above. Coordinates
# measured off a real frame beat coordinates argued from a screenshot.
def _load_box_overrides() -> None:
    import json
    path = STATE / "boxes.json"
    if not path.exists():
        return
    try:
        saved = json.loads(path.read_text(encoding="utf-8"))
    except Exception:
        return
    g = globals()
    rr = [saved.get(f"reroll{i}") for i in (1, 2, 3)]
    if all(rr):
        g["REROLL_BOXES"] = [(b[0], b[1]) for b in rr]
        g["REROLL_SIZE"] = (rr[0][2] - rr[0][0], rr[0][3] - rr[0][1])
    titles = {s_: saved.get(f"title_{s_}") for s_ in ("L", "M", "R")}
    if all(titles.values()):
        g["CARD_TITLES"] = {k: tuple(v) for k, v in titles.items()}
    for key, name in (("hide", "HIDE_BOX"), ("tooltip", "HOVER_TOOLTIP")):
        if saved.get(key):
            g[name] = tuple(saved[key])


_load_box_overrides()

# --- widget ---
WIDGET_HOST = "127.0.0.1"
WIDGET_PORT = 8777
