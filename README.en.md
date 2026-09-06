# ARAM Mayhem Augment Overlay

[![CI](https://github.com/Finerestaurant/aram-augment-overlay/actions/workflows/ci.yml/badge.svg)](https://github.com/Finerestaurant/aram-augment-overlay/actions/workflows/ci.yml)
[![release](https://img.shields.io/github/v/release/Finerestaurant/aram-augment-overlay?include_prereleases)](https://github.com/Finerestaurant/aram-augment-overlay/releases)

[한국어](README.md) · **English** · [日本語](README.ja.md) · [简体中文](README.zh-CN.md)

Shows the augments you pick in League of Legends **ARAM: Mayhem** as a running list on your OBS
stream. It reads them off the screen, so there is no account linking and nothing to log into.

- Detects the moment an augment is taken → adds its name, rarity and icon to the overlay
- Silver / gold / prismatic are told apart by colour
- Attaches as an OBS browser source, so it goes straight onto the broadcast

> Win rates, tiers and other performance stats are not shown — Riot policy does not allow it.

> **The interface of the program itself is Korean.** Only this README is translated, and the
> detection thresholds were measured against the Korean client. See [Limits](#limits).

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
| OCR | The Windows OCR language pack for your client language |

---

## First run

**1. Turn on the OBS websocket server**

Start OBS **once** and dismiss the Auto-Configuration Wizard that appears. (While that wizard is
open, OBS does not save its config at all.) Then **close OBS completely**, run this program and
press **OBS websocket 서버 켜기** on the 설정 (Settings) tab. Doing it inside OBS under
**Tools → WebSocket Server Settings → Enable WebSocket server** is equivalent.

**2. Run it**

Start OBS again and press **다시 시도** (Retry) on the 상태 (Status) tab. Once connected the
indicator turns green and the widget address appears.

The ✕ on the window does not quit — it **hides to the tray**. Double-click the tray icon, or launch
the program again, to bring the window back. To actually stop it, use the **종료** (Quit) button or
right-click the tray icon and choose 종료.

**3. Add the widget to OBS**

**Sources → + → Browser**
- URL: `http://127.0.0.1:8777/`
- Any size will do — it resizes itself at runtime to the card width and four rows
- Ticking **"Refresh browser when scene becomes active"** is recommended

The background is transparent, so it sits straight on top of the game. The game capture source is
created for you.

---

## Settings tab

Changes are written to `config.json` next to the exe and take effect when you press
**저장하고 다시 시작** (Save and restart). Language and coordinates are read once at startup, which
is why a restart is needed.

| | |
|---|---|
| 증강 이름 (Augment names) | Which client language to fetch from CommunityDragon. Cached per language |
| OCR 언어 (OCR language) | Which Windows OCR language reads the screen. **자동** (auto) follows the setting above, and tells you the install command when the pack is missing |
| 게임 해상도 (Game resolution) | Coordinates are rescaled to this. 16:9 works as-is; anything else warns |
| OBS | websocket port · game capture source name · password (blank reads it from the OBS config) |
| 오버레이 (Overlay) | widget port · rows shown · maximum width |

Command-line flags work too. `--stop` shuts a running overlay down cleanly (tray icon included),
and flags such as `--widget-port` override the saved settings.

---

## When it does not work

**"OBS 연결 안 됨" (not connected to OBS)**
Check that OBS is running and its websocket server is on — the button on the 설정 tab. Start OBS,
then press **다시 시도** (Retry); there is no need to close and reopen the window.

**Tray icons piling up**
Killing the program from Task Manager gives it no chance to remove its icon, so a dead one is left
behind. Moving the mouse across the tray makes Windows clear them. Use the **종료** button or
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
- **Everything was measured against the Korean client.** Other client languages can be selected in
  the settings, but how well they read has not been verified, and the interface of the program is
  Korean only.

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
