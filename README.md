# ARAM Mayhem Augment Overlay

<img src="docs/images/icon.png" width="128" height="128" alt="ARAM Mayhem Augment Overlay" align="right" />

**English** · [한국어](README.ko.md) · [日本語](README.ja.md) · [简体中文](README.zh-CN.md)

[![CI][ci-badge]][ci-workflow]
[![release][release-badge]][releases]
[![downloads][downloads-badge]][releases]
[![stars][stars-badge]][stargazers]
[![forks][forks-badge]][network]

[ci-badge]: https://github.com/Finerestaurant/aram-augment-overlay/actions/workflows/ci.yml/badge.svg
[ci-workflow]: https://github.com/Finerestaurant/aram-augment-overlay/actions/workflows/ci.yml
[release-badge]: https://img.shields.io/github/v/release/Finerestaurant/aram-augment-overlay?include_prereleases
[downloads-badge]: https://img.shields.io/github/downloads/Finerestaurant/aram-augment-overlay/total
[stars-badge]: https://img.shields.io/github/stars/Finerestaurant/aram-augment-overlay
[forks-badge]: https://img.shields.io/github/forks/Finerestaurant/aram-augment-overlay
[releases]: https://github.com/Finerestaurant/aram-augment-overlay/releases
[stargazers]: https://github.com/Finerestaurant/aram-augment-overlay/stargazers
[network]: https://github.com/Finerestaurant/aram-augment-overlay/network/members

Shows the augments you pick in League of Legends **ARAM: Mayhem** as a running list on your OBS
stream. It reads them off the screen, so there is no account linking and nothing to log into.

[![Download for Windows](https://img.shields.io/badge/Windows-download-0078D4?style=for-the-badge&logo=windows&logoColor=white)][releases]

- [Download](#download)
- [First run](#first-run)
  - [The window](#the-window)
- [Settings tab](#settings-tab)
- [When it does not work](#when-it-does-not-work)
- [How it works](#how-it-works)
- [Limits](#limits)
- [Development](#development)
- [Built on](#built-on)
- [License](#license)

![The overlay over a game frame](docs/images/overlay.png)

It focuses on:

- **automatic**: each augment you take joins the list within seconds, with nothing to press
- **lightness**: one exe, nothing to install — not even a .NET runtime
- **non-intrusiveness**: it reads screen pixels, never game memory, and leaves nothing in the game
- **anonymity**: no account linking, no login, no API key
- **transparency**: the background is empty, so it sits straight on the broadcast

<table>
<tr>
<td width="40%"><img src="docs/images/widget.d.png" alt="The widget on its own"></td>
<td><img src="docs/images/overlay.gif" alt="Augments stacking up"></td>
</tr>
<tr>
<td align="center"><sub>Silver · gold · prismatic told apart by colour</sub><br><sub>Three themes, switched while live</sub></td>
<td align="center"><sub>Each pick joins the list within seconds</sub></td>
</tr>
</table>

> [!NOTE]
> Win rates, tiers and other performance stats are not shown — Riot policy does not allow it.

The window speaks Korean, English, Japanese or Chinese — it follows your Windows language and can be
changed under Settings → Interface. The detection thresholds, though, were measured against the
Korean and English clients; see [Limits](#limits).

## Download

Grab the single `ARAM-Augment-Overlay.exe` from the [releases page][releases].

> [!IMPORTANT]
> The first time you run it, Windows will say **"Windows protected your PC"**. That is because the
> program is not code-signed. Click **More info → Run anyway**.

| Requirement | |
|---|---|
| OS | Windows 10 / 11 · 1920×1080 · 100% scaling. Other resolutions are mapped for but untested |
| OBS Studio | 28 or newer |
| Game setting | **Borderless fullscreen** recommended |
| OCR | The Windows OCR language pack for your client language — the app can install it for you |

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

### The window

The status tab shows the connection and what has been taken so far; the settings tab covers language, resolution, OBS and the overlay.

| Status | Settings |
|---|---|
| ![Status tab](docs/images/app-status.en.png) | ![Settings tab](docs/images/app-settings.en.png) |

## Settings tab

Changes are written to `config.json` next to the exe and take effect when you press
**Save and restart**. Language and coordinates are read once at startup, which
is why a restart is needed.

| Setting | What it does |
|---|---|
| Game language | The language your League client is in. Augment names are fetched in it and the screen is read with it. If the matching OCR pack is missing, the app can install it. It starts from what the League install says it is set to, and only falls back to the Windows display language |
| Game resolution | Fallback size for reading text when OBS cannot report the game's own size. 1920×1080 is the only tested value; card positions follow the frame on their own at any size |
| OBS | websocket port · game capture source name · password (blank reads it from the OBS config) |
| Widget theme | HUD tray (grows sideways) · one strip (smallest) · game palette (vertical). Changes on air at once, with no browser-source refresh |
| Overlay | widget port · rows shown · maximum width |

<img src="docs/images/widget.b.png" alt="HUD tray" width="392"><br><sub>HUD tray</sub>

<img src="docs/images/widget.c.png" alt="One strip" width="680"><br><sub>One strip</sub>

<img src="docs/images/widget.d.png" alt="Game palette" width="284"><br><sub>Game palette</sub>

Command-line flags work too. `--stop` shuts a running overlay down cleanly (tray icon included),
and flags such as `--widget-port` override the saved settings.

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

- Confirm 100% display scaling
- Confirm the game is in **borderless fullscreen** or fullscreen
- If the status line says **OBS has no game picture**, the game capture source is not on the game
  window. The tool re-points it once by itself; if that does not take, pick
  “League of Legends (TM) Client” in the source's window list

**The wrong augment was published**

Turn on **Record how each pick was decided** (Settings › Advanced) and play. Every augment window
is then kept from the moment it opens until it closes, and `http://127.0.0.1:8777/inspect` plays it
back frame by frame with every box the detector reads from drawn on the picture and every number it
judged against beside it — gate scores, card brightness, the two flare tests against their cutoffs,
the tooltip panel, and a timeline with the rerolls and the frame each verdict came off. Arrow keys
step one frame; hold shift for ten.

It is off by default because a window costs 15–25 MB. The last eight are kept, in
`state/inspect`, and the oldest is deleted as new ones arrive.

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

## Limits

- **Only 1920×1080 has been tested.** It is the resolution the coordinates were measured at, the
  only one a live game has ever been played at, and the only one the resolution setting offers.
  The mapping is written to work at any size and the arithmetic is checked: the client lays the
  augment screen out by height and centres it, and the same window was captured at all fifteen
  fullscreen sizes one monitor offers (16:9, 16:10, 5:4 and 4:3, from 1024×768 to 1680×1050) and
  read correctly at every one, with 1440p and 4K resamples on top, both held by the test suite.
  None of that is a game played at those sizes. 21:9 and wider has never been captured at all,
  and the HUD scale slider has not been tried.
- Names are read by OCR. A shaky read is corrected against the augment list, but it can still be
  wrong on rare occasions. A read that cannot be confirmed is not recorded at all.
- The rarity and selection thresholds come from measurements on real games and gameplay footage.
  The silver sample is still small, so silver may be less reliable.
- Works only in ARAM Mayhem (`gameMode: KIWI`).
- **The thresholds were measured against the Korean and English clients, and both have been
  played through.** Japanese and Chinese can be selected in the settings and use the same
  measurements, but neither has been verified over a run of games.

## Development

```
dotnet build src/AramOverlay.slnx
dotnet run --project src/AramOverlay.SelfTest             # parity suite
dotnet run --project src/AramOverlay.SelfTest -- --obs    # check against a running OBS
```

No NuGet packages and no native DLLs — only the BCL and WinRT. The OBS websocket runs on
`ClientWebSocket`, the widget server on `HttpListener`, OCR on `Windows.Media.Ocr`, image decoding
on `Windows.Graphics.Imaging`, and the four things OpenCV used to do are implemented in `Cv.cs`.

This tool was originally written in Python and ported to C#. What could be matched exactly and what
could not be — with the measurements — is in [`docs/PORTING.md`](docs/PORTING.md) (Korean). The
comparison data under `tests/` is what the Python implementation produced; regenerating it needs
`scripts/gen_*.py` and the Python code from before commit `ecd9cb5`.

<details>
<summary>Release build and publishing</summary>

```
dotnet publish src/AramOverlay.App -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true -p:DebugType=none -o publish
```

Pushing a tag builds and publishes a release.

```
git tag v0.1.0 && git push origin v0.1.0
```

</details>

## Built on

- Augment data: [CommunityDragon](https://www.communitydragon.org/)
- OCR: the OCR engine built into Windows (Windows.Media.Ocr)
- OBS: [obs-websocket](https://github.com/obsproject/obs-websocket)

League of Legends is a trademark of Riot Games, Inc. This project is not affiliated with Riot Games.

## License

MIT
