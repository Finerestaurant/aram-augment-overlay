# ARAM Mayhem Augment Overlay

[![CI](https://github.com/Finerestaurant/aram-augment-overlay/actions/workflows/ci.yml/badge.svg)](https://github.com/Finerestaurant/aram-augment-overlay/actions/workflows/ci.yml)
[![release](https://img.shields.io/github/v/release/Finerestaurant/aram-augment-overlay?include_prereleases)](https://github.com/Finerestaurant/aram-augment-overlay/releases)

[한국어](README.md) · **English** · [日本語](README.ja.md) · [简体中文](README.zh-CN.md)

Shows the augments you pick in League of Legends **ARAM: Mayhem** as a running list on your OBS
stream. It reads them off the screen, so there is no account linking and nothing to log into.

![The overlay over a game frame](docs/images/overlay.png)

Each augment you take joins the list within seconds. The background is transparent, so it sits
straight on the broadcast.

![Augments stacking up](docs/images/overlay.gif)

- Detects the moment an augment is taken → adds its name, rarity and icon to the overlay
- Silver / gold / prismatic are told apart by colour
- Attaches as an OBS browser source, so it goes straight onto the broadcast

> Win rates, tiers and other performance stats are not shown — Riot policy does not allow it.

> The window speaks Korean, English, Japanese or Chinese — it follows your Windows language and
> can be changed under Settings → Interface. The detection thresholds, though, were measured
> against the Korean client; see [Limits](#limits).

---

## Download

Grab the single `ARAM-Augment-Overlay.exe` from the [releases page](../../releases). There is
nothing to install and nothing else to fetch — not even a .NET runtime.

The first time you run it, Windows will say **"Windows protected your PC"**. That is because the
program is not code-signed. Click **More info → Run anyway**.

| | |
|---|---|
| OS | Windows 10 / 11, **1920×1080 at 100% scaling** |
| OBS Studio | 28 or newer |
| Game setting | **Borderless fullscreen** recommended |
| OCR | The Windows OCR language pack for your client language — the app can install it for you |

---

## First run

**1. Turn on the OBS websocket server**

Start OBS **once** and dismiss the Auto-Configuration Wizard that appears. (While that wizard is
open, OBS does not save its config at all.) Then **close OBS completely**, run this program and
press **Turn on the OBS websocket server** on the Settings tab. Doing it inside OBS under
**Tools → WebSocket Server Settings → Enable WebSocket server** is equivalent.

**2. Run it**

Start OBS again and press **Retry** on the Status tab. Once connected the
indicator turns green and the widget address appears.

The ✕ on the window does not quit — it **hides to the tray**. Double-click the tray icon, or launch
the program again, to bring the window back. To actually stop it, use the **Quit** button or
right-click the tray icon and choose Quit.

**3. Add the widget to OBS**

**Sources → + → Browser**
- URL: `http://127.0.0.1:8777/`
- Any size will do — it resizes itself at runtime to the card width and four rows
- Ticking **"Refresh browser when scene becomes active"** is recommended

The background is transparent, so it sits straight on top of the game. The game capture source is
created for you.

---

### The window

The status tab shows the connection and what has been taken so far; the settings tab covers language, resolution, OBS and the overlay.


| | |
|---|---|
| ![](docs/images/app-status.png) | ![](docs/images/app-settings.png) |

---

## Settings tab

Changes are written to `config.json` next to the exe and take effect when you press
**Save and restart**. Language and coordinates are read once at startup, which
is why a restart is needed.

| | |
|---|---|
| Game language | The language your League client is in. Augment names are fetched in it and the screen is read with it. If the matching OCR pack is missing, the app can install it |
| Game resolution | Coordinates are rescaled to this. 16:9 works as-is; anything else warns |
| OBS | websocket port · game capture source name · password (blank reads it from the OBS config) |
| Overlay | widget port · rows shown · maximum width |

Command-line flags work too. `--stop` shuts a running overlay down cleanly (tray icon included),
and flags such as `--widget-port` override the saved settings.

---

## When it does not work

**"OBS not connected"**
Check that OBS is running and its websocket server is on — the button on the Settings tab. Start
OBS, then press **Retry**; there is no need to close and reopen the window.

**Tray icons piling up**
Killing the program from Task Manager gives it no chance to remove its icon, so a dead one is left
behind. Moving the mouse across the tray makes Windows clear them. Use the **Quit** button or
`--stop` instead of killing it.

**OBS asks "Start in safe mode?"**
Always choose normal mode. Safe mode disables the websocket server, so the tool cannot connect. It
is offered after OBS was closed abnormally.

**Augments are not recognised**
- Confirm 1920×1080 at 100% scaling
- Confirm the game is in **borderless fullscreen**
- Confirm the OBS game capture source is actually capturing the game (check the preview)

---

## Limits

- Coordinates were measured at 1920×1080 and are **rescaled proportionally** to whatever resolution
  is set. A 16:9 screen of any size lands on the same layout, but 1920×1080 is the only one
  actually verified.
- Names are read by OCR. A shaky read is corrected against the augment list, but it can still be
  wrong on rare occasions. A read that cannot be confirmed is not recorded at all.
- The rarity and selection thresholds come from measurements on real games and gameplay footage.
  The silver sample is still small, so silver may be less reliable.
- Works only in ARAM Mayhem (`gameMode: KIWI`).
- **The thresholds were all measured against the Korean client.** Other client languages can be
  selected in the settings and English reads cleanly in testing, but no language other than Korean
  has been verified over a run of games.

---

## How it works

Augment data is **not** in any official API. The full Live Client Data API (`127.0.0.1:2999`) spec
was checked and has no augment field at all, and the match API is closed for Mayhem games. Reading
the screen is the only route left.

1. The Live Client Data API says whether this is a Mayhem game and what level you are
2. A frame comes over the OBS websocket and the **three reroll buttons are template-matched** → the
   augment window is detected
3. The pick is read from the window closing — card brightness just before it goes, and the tooltip
4. Rarity comes from the card border colour, the title from OCR
5. The name is confirmed against the augment list

Why template matching rather than brightness thresholds, where each threshold came from, and the
data behind them are written up in [`docs/FINDINGS.md`](docs/FINDINGS.md) (Korean).

---

## Development

```
dotnet build src/AramOverlay.slnx
dotnet run --project src/AramOverlay.SelfTest             # parity suite
dotnet run --project src/AramOverlay.SelfTest -- --obs    # check against a running OBS
dotnet publish src/AramOverlay.App -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true -p:DebugType=none -o publish
```

No NuGet packages and no native DLLs — only the BCL and WinRT. The OBS websocket runs on
`ClientWebSocket`, the widget server on `HttpListener`, OCR on `Windows.Media.Ocr`, image decoding
on `Windows.Graphics.Imaging`, and the four things OpenCV used to do are implemented in `Cv.cs`.

This tool was originally written in Python and ported to C#. What could be matched exactly and what
could not be — with the measurements — is in [`docs/PORTING.md`](docs/PORTING.md) (Korean). The
comparison data under `tests/` is what the Python implementation produced; regenerating it needs
`scripts/gen_*.py` and the Python code from before commit `ecd9cb5`.

Pushing a tag builds and publishes a release:

```
git tag v0.1.0 && git push origin v0.1.0
```

---

## Built on

- Augment data: [CommunityDragon](https://www.communitydragon.org/)
- OCR: the OCR engine built into Windows (Windows.Media.Ocr)
- OBS: [obs-websocket](https://github.com/obsproject/obs-websocket)

League of Legends is a trademark of Riot Games, Inc. This project is not affiliated with Riot Games.

## License

MIT
