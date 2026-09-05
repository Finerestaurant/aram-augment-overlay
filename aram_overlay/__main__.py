"""Entry point: watch the augment window and publish picks to the OBS widget.

Loop shape follows what the calibration runs showed:
  * the Live Client API gives game mode and level cheaply, so the screen only
    needs watching while a Mayhem game is actually running
  * detection frames are small JPEGs (~69 ms); a full-resolution frame is pulled
    only at the moment a pick is confirmed, for OCR
  * the window gate needs hysteresis, or it reads as shut about a second in and
    everything after that is missed
"""
from __future__ import annotations

import argparse
import json
import statistics
import sys
import time
from concurrent.futures import ThreadPoolExecutor

import cv2
import numpy as np
import requests
import urllib3

from . import config, detect
from .augments import AugmentDB
from .items import ItemNames
from .obs import ObsCapture, read_obs_websocket_config
from .ocr import TooltipOCR
from .server import Pick, RunState, WidgetServer

urllib3.disable_warnings()

# A full-resolution screenshot costs ~250 ms and runs on its own connection, so
# the pacing here only bounds how hard we hit OBS, not the detection loop. It now
# stops as soon as all three card titles are read, which is usually the first
# frame -- the titles do not change while the window is open.
TOOLTIP_PROBE_S = 0.35


def log(msg: str) -> None:
    print(f"[{time.strftime('%H:%M:%S')}] {msg}", flush=True)


def _new_kept() -> dict:
    """The tooltip frame carried between the probe thread and the loop.

    Built here rather than inline so resetting it on a new window cannot quietly
    drop a key the loop then reads.
    """
    return {"cards": {}, "complete": False, "rerolls": [], "frame": None,
            "at": 0.0, "full_at": 0.0, "last": None, "hover": None,
            "hover_at": 0.0, "hover_raw": "", "tip_misses": []}


def _new_diag() -> dict:
    """Per-window record of why selection did or did not trigger.

    A window that closes with no pick tells you nothing on its own: the flare may
    have been missed because the loop was sampling too slowly to see it, or it
    may never have reached the thresholds at all. Those two are fixed in opposite
    directions, so the log has to say which one happened.
    """
    return {"frames": 0, "settled": 0, "peak": 0.0, "peak_losers": (0.0, 0.0),
            "gaps": [], "last_tick": 0.0, "gate_last": (0.0, 0.0, 0.0),
            "gate_min": 9.9, "hide_last": 0.0, "hide_max": -9.9}


def _diag_summary(d: dict, baseline: float) -> str:
    gaps = d["gaps"]
    avg = sum(gaps) / len(gaps) * 1000 if gaps else 0.0
    worst = max(gaps) * 1000 if gaps else 0.0
    lo, hi = d["peak_losers"]
    return (f"프레임 {d['frames']}회(안정 {d['settled']}) "
            f"루프간격 평균 {avg:.0f}ms 최대 {worst:.0f}ms | "
            f"게이트 마지막 {list(d['gate_last'])} 최저 {d['gate_min']:.2f} "
            f"| 숨김 마지막 {d['hide_last']:.2f} 최고 {d['hide_max']:.2f} | "
            f"baseline {baseline:.1f} 최고 winner {d['peak']:.2f} "
            f"그때 losers {lo:.2f}/{hi:.2f}")


def _hover_summary(win) -> str:
    """How settled the hover was, and what the last stretch of it looked like.

    The card is chosen by whichever slot was hovered last, so a hover that
    flickers between cards near the close is the difference between the right
    name and a confident wrong one.
    """
    h = win.hover_history
    if not h:
        return "호버 이력 없음"
    counts = {k: h.count(k) for k in sorted(set(h))}
    tail = "".join(h[-25:])
    return f"호버 {counts} 마지막25={tail}"


def live_game() -> dict | None:
    try:
        r = requests.get(config.LIVE_URL, verify=False, timeout=4)
        return r.json() if r.status_code == 200 else None
    except Exception:
        return None


def load_password(cli_password: str | None) -> str:
    if cli_password:
        return cli_password
    cfg = config.ROOT / "config.json"
    if cfg.exists():
        try:
            pw = json.loads(cfg.read_text(encoding="utf-8")).get("obs_password")
            if pw:
                return pw
        except Exception:
            pass
    ws = read_obs_websocket_config()
    if ws and ws.get("server_password"):
        log("OBS 설정 파일에서 websocket 비밀번호를 읽었습니다.")
        return ws["server_password"]
    return ""


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description="아수라장 증강 오버레이")
    ap.add_argument("--password", help="OBS websocket 비밀번호 (생략 시 OBS 설정에서 자동 인식)")
    ap.add_argument("--port", type=int, default=config.OBS_PORT)
    ap.add_argument("--source", default=config.OBS_SOURCE)
    ap.add_argument("--widget-port", type=int, default=config.WIDGET_PORT)
    ap.add_argument("--refresh-data", action="store_true", help="증강 데이터를 새로 받습니다")
    args = ap.parse_args(argv)

    log("증강 데이터 불러오는 중...")
    db = AugmentDB.load(refresh=args.refresh_data)
    log(f"  증강 {len(db.augments)}종")
    items = ItemNames.load(refresh=args.refresh_data)

    password = load_password(args.password)
    try:
        obs = ObsCapture(port=args.port, password=password, source=args.source)
        log(obs.version())
    except Exception as exc:
        log(f"OBS 연결 실패: {exc}")
        log("  OBS가 실행 중인지, 도구 > WebSocket 서버 설정에서 서버가 켜져 있는지 확인하세요.")
        return 2

    if obs.ensure_game_capture():
        log(f"게임 캡처 소스 '{args.source}' 를 새로 만들었습니다.")
    elif not obs.has_source():
        log(f"소스 '{args.source}' 를 찾을 수 없습니다.")
        return 2

    log("OCR 준비 중...")
    ocr = TooltipOCR(log=log)
    gate = detect.TemplateGate()
    hide_btn = detect.HideButton()

    state = RunState()
    server = WidgetServer(state, port=args.widget_port)
    url = server.start()
    log(f"위젯 주소: {url}   <- OBS 브라우저 소스에 이 주소를 넣으세요 (권장 크기 420x600)")
    refreshed = obs.refresh_browser_sources(url)
    if refreshed:
        log(f"브라우저 소스 새로고침: {', '.join(refreshed)}")

    # The tooltip grab runs off the detection loop -- it costs ~250 ms and the
    # selection flare is only a few frames long, so blocking on it loses the very
    # event we are waiting for. obs-websocket clients are not safe to share
    # between threads, hence a second connection rather than reusing `obs`.
    probe_obs = ObsCapture(port=args.port, password=password, source=args.source)
    pool = ThreadPoolExecutor(max_workers=1)
    probe_future = None

    win = detect.WindowState()
    baseline_samples: list[float] = []
    kept: dict = _new_kept()      # newest full-res frame showing a tooltip
    flare_hist: list = []         # (t, card means, window alive, thumb) while open
    game_tag = ""                 # decisions are filed under the game they came from
    sized_seq = 0                 # last widget size already pushed to OBS
    anvil = False           # this window is an item anvil, not an augment window
    seen_raw = None         # last tooltip text already classified
    diag = _new_diag()
    last_probe_at = 0.0
    current_game_id = None
    log("대기 중... 아수라장 게임을 시작하세요.")

    try:
        while True:
            if server.size["seq"] != sized_seq and server.size["h"]:
                sized_seq = server.size["seq"]
                w_, h_ = server.size["w"], server.size["h"]
                if obs.resize_browser_sources(url, w_, h_):
                    log(f"위젯 크기에 맞춰 브라우저 소스 조정: {w_}x{h_}")

            game = live_game()
            if not game:
                if state.connected:
                    log("게임이 종료되었습니다.")
                state.connected = False
                win = detect.WindowState()
                time.sleep(3)
                continue

            mode = (game.get("gameData") or {}).get("gameMode", "")
            level = ((game.get("activePlayer") or {}).get("level"))
            game_id = (game.get("gameData") or {}).get("gameTime", 0)

            if not state.connected:
                state.connected = True
                state.game_mode = mode
                game_tag = time.strftime("%Y%m%d-%H%M%S")
                log(f"게임 감지: gameMode={mode}, level={level}")
                if mode != config.MAYHEM_GAME_MODE:
                    log(f"  아수라장(KIWI)이 아닙니다. 증강 감지를 건너뜁니다.")

            # a fresh game rewinds gameTime; start a new list
            if current_game_id is not None and isinstance(game_id, (int, float)) \
                    and game_id + 5 < current_game_id:
                log("새 게임이 시작되어 목록을 초기화합니다.")
                game_tag = time.strftime("%Y%m%d-%H%M%S")
                state.picks.clear()
                server.save()
            current_game_id = game_id

            if mode != config.MAYHEM_GAME_MODE:
                time.sleep(3)
                continue

            frame = obs.grab_cv()
            if frame is None:
                time.sleep(1)
                continue

            scores = gate.scores(frame)
            strong = min(scores) >= config.GATE_OPEN
            # The screen can be tucked away: the cards and the reroll buttons go,
            # the hide button stays. Closing on the reroll buttons alone read that
            # as a finished pick and then read the screen coming back as a new
            # one -- a single augment logged three times, once per card the cursor
            # crossed. It is over only when the hide button goes too.
            hide = hide_btn.score(frame)
            weak = (min(scores) >= config.GATE_STAY
                    or sum(1 for sc in scores if sc >= config.GATE_STAY_TWO) >= 2
                    or hide >= config.HIDE_PRESENT)

            if not win.open:
                win.open_streak = win.open_streak + 1 if strong else 0
                if win.open_streak >= 2:
                    win = detect.WindowState(open=True, opened_at=time.time())
                    baseline_samples = []
                    kept = _new_kept()
                    diag = _new_diag()
                    flare_hist = []
                    anvil = False
                    seen_raw = None
                    log(f"=== 증강 선택창 (레벨 {level}) === 게이트 "
                        f"{[round(x, 2) for x in scores]}")
                    _dump_gate_open(frame, scores, level, log)
            else:
                win.miss_streak = 0 if weak else win.miss_streak + 1
                if win.miss_streak >= config.CLOSE_MISSES:
                    log("=== 선택창 종료 ===")
                    log(f"    {_diag_summary(diag, win.baseline)}")
                    log(f"    {_hover_summary(win)}")
                    _on_window_closed(ocr, db, items, state, server, win, kept,
                                      diag, anvil, level, log, flare_hist, game_tag)
                    win = detect.WindowState()
                    kept = _new_kept()
                    time.sleep(0.3)
                    continue

            if not win.open:
                time.sleep(0.35)
                continue

            diag["gate_last"] = tuple(round(x, 2) for x in scores)
            diag["gate_min"] = min(diag["gate_min"], min(scores))
            diag["hide_last"] = round(hide, 2)
            diag["hide_max"] = max(diag["hide_max"], hide)

            tick = time.time()
            if diag["last_tick"]:
                diag["gaps"].append(tick - diag["last_tick"])
            diag["last_tick"] = tick
            diag["frames"] += 1

            means = detect.card_means(frame)
            # A half-size copy rides along so the decision can be shown as the
            # frames it was made from, rather than described in numbers.
            flare_hist.append((tick, means, weak,
                               cv2.resize(frame, (frame.shape[1] // 2,
                                                  frame.shape[0] // 2))))
            if len(flare_hist) > 45:             # ~4 s at the loop's cadence
                del flare_hist[:10]
            settled = detect.interior_darkness(frame) < 60 and \
                (time.time() - win.opened_at) > config.ENTRY_ANIM_S
            if settled:
                diag["settled"] += 1
                baseline_samples.append(statistics.median(means.values()))
                if len(baseline_samples) > 30:
                    baseline_samples.pop(0)
                win.baseline = statistics.median(baseline_samples)

                hover, _ = detect.hovered_card(means)
                if hover:
                    win.hover_history.append(hover)
                    win.last_hover = hover

            # Keep the newest frame that still shows a tooltip, for as long as the
            # window is open. Earlier this only fired when a *new* card was
            # hovered, which missed whenever the player settled on a card and
            # clicked before the next probe was allowed: 6 of 13 recorded picks
            # fell back to a post-click grab, and every one of those was useless
            # (gate already shut, no tooltip in frame). Hover detection is not a
            # precondition either -- the tooltip on screen belongs to whichever
            # card the cursor is on, which is the card about to be taken.
            if ((probe_future is None or probe_future.done())
                    and time.time() - last_probe_at > TOOLTIP_PROBE_S):
                last_probe_at = time.time()
                probe_future = pool.submit(_scan_cards, probe_obs, ocr, db, items, kept)

            # Classify the window from its own tooltip while it is still open.
            # Mayhem also hands out item anvils behind a near-identical screen;
            # deciding after a selection is detected is too late and leaves the
            # rest of the window burning probes on a screen we will discard.
            # An anvil offers items on the same three-card layout, so the titles
            # are the thing to judge. Both scores are meaningless on a garbled
            # read, so the item side has to actually look like an item.
            if kept["cards"] and len(kept["cards"]) != seen_raw:
                seen_raw = len(kept["cards"])
                items_win = sum(1 for c in kept["cards"].values()
                                if c["item"] > c["score"] and c["item"] >= config.OCR_MIN_SCORE)
                if items_win >= 2 and not anvil:
                    anvil = True
                    log("  모루(아이템) 화면으로 판단 — 이 창은 건너뜁니다: "
                        + ", ".join(f"{k}={v['raw']!r}" for k, v in kept["cards"].items()))

            if anvil:
                continue

            # Card brightness is still tracked, but only as telemetry. It no
            # longer decides anything -- see _on_window_closed.
            if win.baseline > 0:
                ranked = sorted(means.values(), reverse=True)
                ratio = ranked[0] / win.baseline
                l1, l2 = ranked[1] / win.baseline, ranked[2] / win.baseline
                if ratio > diag["peak"]:
                    diag["peak"] = ratio
                    diag["peak_losers"] = (l1, l2)

    except KeyboardInterrupt:
        log("종료합니다.")
    finally:
        pool.shutdown(wait=False)
        server.save()
        server.stop()
    return 0


def _read_card(ocr, db, items, frame, slot):
    """Best reading of one card's title, escalating scale until it is confident."""
    best = {"name": None, "score": 0.0, "raw": "", "item": 0.0, "scale": 0}
    for scale in config.CARD_SCALES:
        raw = ocr.read_box(frame, config.CARD_TITLES[slot], scale)
        if not raw:
            continue
        aug, score = db.match(raw, None)
        if score > best["score"]:
            best = {"name": aug.name if aug else None, "score": score, "raw": raw,
                    "item": items.best_score(raw), "scale": scale, "aug": aug}
        if best["score"] >= config.OCR_MIN_SCORE:
            break
    return best


def _scan_cards(obs, ocr, db, items, kept: dict) -> None:
    """Read all three card titles off one full-resolution frame.

    Rescans for as long as the window is open, and the newest complete reading
    replaces the previous one. An earlier version stopped once all three had been
    read, on the assumption that the offer is fixed for the life of the window --
    it is not. Rerolling swaps all three, and the reroll button is on that very
    screen. Two of three picks in one game came out as augments from the offer
    before the reroll: 전능의 영혼 and 빛의 인도자 결의 against a match history of
    판도라의 상자 / 속행 / 천상의 신체 / 위력 추구. Only the window that was not
    rerolled came out right.
    """
    try:
        shot = obs.grab(width=config.BASE_W, height=config.BASE_H,
                        quality=config.OCR_QUALITY, fmt="jpg")
    except Exception:
        return
    if shot is None:
        return
    kept["last"] = shot

    # Per card, newest confident reading wins. All-or-nothing was wrong twice
    # over: each card has its own reroll button so they change one at a time,
    # and discarding a whole frame because one title would not read left the
    # others stale -- L was still reporting 곰 투하 while the screen showed
    # 끝없는 학살.
    read_now = 0
    for slot in config.CARD_TITLES:
        got = _read_card(ocr, db, items, shot, slot)
        if got["score"] < config.OCR_MIN_SCORE:
            continue
        read_now += 1
        was = kept["cards"].get(slot)
        if was and was["name"] != got["name"]:
            kept["rerolls"].append((slot, was["name"], got["name"]))
        kept["cards"][slot] = got
        kept["frame"], kept["at"] = shot, time.time()
    got_all = sum(1 for c in kept["cards"].values()
                  if c["score"] >= config.OCR_MIN_SCORE) == len(config.CARD_TITLES)
    if got_all and read_now == len(config.CARD_TITLES):
        # every title read on THIS frame, so the offer is confirmed as of now
        kept["full_at"] = time.time()
    kept["complete"] = len(kept["cards"]) == len(config.CARD_TITLES)

    # Which card the cursor is on, read off the tooltip rather than guessed from
    # brightness. Only counts when the tooltip names one of the three titles we
    # are holding, so a stale or unrelated read cannot claim a slot.
    names = {slot: c["name"] for slot, c in kept["cards"].items()}
    if names:
        miss = None
        for scale in config.CARD_SCALES:
            raw = ocr.read_box(shot, config.HOVER_TOOLTIP, scale)
            if not raw:
                continue
            aug, score = db.match(raw, None)
            slot = (next((s for s, n in names.items() if n == aug.name), None)
                    if aug and score >= config.OCR_MIN_SCORE else None)
            if slot:
                kept["hover"], kept["hover_at"] = slot, time.time()
                kept["hover_raw"] = raw
                miss = None
                break
            # something was read but it is not one of the three on offer
            miss = (raw, aug.name if aug else "-", round(score, 2))
        if miss and (not kept["tip_misses"] or kept["tip_misses"][-1] != miss):
            kept["tip_misses"].append(miss)


def _cards_done(kept: dict) -> bool:
    """Whether every card has been read confidently. Not a reason to stop
    scanning -- a reroll replaces all three."""
    return (len(kept["cards"]) == len(config.CARD_TITLES)
            and all(c["score"] >= config.OCR_MIN_SCORE for c in kept["cards"].values()))


_GATE_DUMPS = 0
GATE_DUMP_MAX = 40


def _dump_decision(hist, now, chosen, via, aug, level, log, game_tag="",
                   cards=None, score=0.0, raw="", rerolls=(), tip_slot=None,
                   tip_raw="") -> None:
    """Save the frames the pick was decided from, annotated.

    The choice comes from a stretch of history rather than one picture, so the
    log line alone cannot be checked. This lays that stretch out: every frame
    inside the lookback, its three card readings, and which one was taken.
    """
    try:
        rows = [(t, mm, alive, fr) for t, mm, alive, fr in hist
                if now - t <= config.FLARE_LOOKBACK_S]
        if not rows:
            return
        if len(rows) > 14:                       # keep the strip readable
            step = len(rows) / 14
            rows = [rows[int(i * step)] for i in range(14)]
        # 저장된 프레임은 검출 프레임(960x540)의 절반 = 480x270 이므로
        # 1920 기준 카드 영역 (430,180)-(1520,720) 을 4로 나눈다
        crop = (107, 45, 380, 180)
        cw, ch = crop[2] - crop[0], crop[3] - crop[1]
        tw, th = cw, ch
        cols = min(len(rows), 7)
        rows_n = (len(rows) + cols - 1) // cols
        pad, head, cap = 8, 34, 46
        W = cols * (tw + pad) + pad
        H = head + rows_n * (th + cap + pad) + pad
        canvas = np.full((H, W, 3), 22, np.uint8)
        cv2.putText(canvas, f"lv{level}  ->  {aug.name}   [{via}]", (pad, 23),
                    cv2.FONT_HERSHEY_SIMPLEX, 0.6, (235, 235, 240), 1, cv2.LINE_AA)
        for i, (t, mm, alive, fr) in enumerate(rows):
            r_, c_ = divmod(i, cols)
            x = pad + c_ * (tw + pad)
            y = head + r_ * (th + cap + pad)
            piece = fr[crop[1]:crop[3], crop[0]:crop[2]]
            if piece.size:
                canvas[y:y + th, x:x + tw] = cv2.resize(piece, (tw, th))
            order = sorted(mm.items(), key=lambda kv: -kv[1])
            ratio = order[0][1] / order[1][1] if order[1][1] > 0 else 0.0
            best = order[0][0] == chosen and ratio >= config.SELECT_FLARE and alive
            colour = ((120, 240, 140) if best else
                      (90, 90, 100) if not alive else (200, 200, 210))
            cv2.rectangle(canvas, (x - 2, y - 2), (x + tw + 1, y + th + 1),
                          colour, 2 if best else 1)
            yy = y + th + 14
            cv2.putText(canvas, f"-{now - t:.2f}s {'alive' if alive else 'gone'}",
                        (x, yy), cv2.FONT_HERSHEY_SIMPLEX, 0.38, colour, 1, cv2.LINE_AA)
            cv2.putText(canvas, "L%.0f M%.0f R%.0f" % (mm["L"], mm["M"], mm["R"]),
                        (x, yy + 13), cv2.FONT_HERSHEY_SIMPLEX, 0.38, colour, 1, cv2.LINE_AA)
            cv2.putText(canvas, f"{order[0][0]} x{ratio:.2f}", (x, yy + 26),
                        cv2.FONT_HERSHEY_SIMPLEX, 0.38, colour, 1, cv2.LINE_AA)
        d = config.STATE / "debug" / "decisions" / (game_tag or "unknown")
        d.mkdir(parents=True, exist_ok=True)
        stem = f"{time.strftime('%H%M%S')}_lv{level}"
        cv2.imwrite(str(d / f"{stem}.png"), canvas)

        # every frame of the lookback, in order, so the viewer can play it back
        shots = [(t, mm, alive, fr) for t, mm, alive, fr in hist
                 if now - t <= config.FLARE_LOOKBACK_S]
        frames = []
        fdir = d / stem
        fdir.mkdir(exist_ok=True)
        for i, (t, mm, alive, fr) in enumerate(shots):
            piece = fr[crop[1]:crop[3], crop[0]:crop[2]]
            if not piece.size:
                continue
            fn = f"f{i:03d}.jpg"
            cv2.imwrite(str(fdir / fn), cv2.resize(piece, (cw * 2, ch * 2)),
                        [cv2.IMWRITE_JPEG_QUALITY, 82])
            order = sorted(mm.items(), key=lambda kv: -kv[1])
            ratio = order[0][1] / order[1][1] if order[1][1] > 0 else 0.0
            frames.append({"file": fn, "dt": round(now - t, 2),
                           "means": {k: round(v, 1) for k, v in mm.items()},
                           "alive": bool(alive), "top": order[0][0],
                           "ratio": round(ratio, 2),
                           "picked": bool(order[0][0] == chosen and alive
                                          and ratio >= config.SELECT_FLARE)})

        (d / f"{stem}.json").write_text(json.dumps({
            "frames": frames,
            "at": time.strftime("%H:%M:%S"), "level": level, "slot": chosen,
            "name": aug.name, "rarity": aug.rarity, "icon": aug.icon_url,
            "via": via, "score": round(score, 2), "ocr": raw,
            "cards": cards or {},
            "rerolls": [list(r) for r in rerolls],
            "tooltip": {"slot": tip_slot, "raw": tip_raw},
        }, ensure_ascii=False, indent=1), encoding="utf-8")
        log(f"    판정 근거: state/debug/decisions/{game_tag}/{stem}.png")
    except Exception as exc:
        log(f"    판정 근거 저장 실패: {type(exc).__name__}: {exc}")


def _dump_gate_open(frame_bgr, scores, level, log) -> None:
    """Keep the frame that opened the gate, capped per run.

    Tuning the gate needs the frames it got wrong, and those are exactly the ones
    nothing else saves: a false open that never reaches a pick leaves no trace
    but a log line.
    """
    global _GATE_DUMPS
    if _GATE_DUMPS >= GATE_DUMP_MAX:
        return
    try:
        import cv2
        d = config.STATE / "debug" / "gate"
        d.mkdir(parents=True, exist_ok=True)
        tag = "-".join(f"{s:.2f}" for s in scores)
        cv2.imwrite(str(d / f"{time.strftime('%H%M%S')}_lv{level}_{tag}.png"), frame_bgr)
        _GATE_DUMPS += 1
    except Exception as exc:
        log(f"  게이트 프레임 저장 실패: {exc}")


def _dump_failure(frame, level, log) -> None:
    """Keep the frame a failed read came from, plus the exact crop fed to the
    recogniser. An empty read says nothing about *why* it was empty; the crop
    does."""
    try:
        d = config.STATE / "debug"
        d.mkdir(parents=True, exist_ok=True)
        stamp = time.strftime("%H%M%S")
        frame.save(d / f"{stamp}_lv{level}_full.png")
        x0 = min(b[0] for b in config.CARD_TITLES.values())
        x1 = max(b[2] for b in config.CARD_TITLES.values())
        y0 = min(b[1] for b in config.CARD_TITLES.values())
        y1 = max(b[3] for b in config.CARD_TITLES.values())
        frame.crop((x0, y0, x1, y1)).save(d / f"{stamp}_lv{level}_titles.png")
        log(f"  진단 프레임 저장: state/debug/{stamp}_lv{level}_*.png")
    except Exception as exc:
        log(f"  진단 프레임 저장 실패: {exc}")


def _flare_slot(hist, now):
    """Which card lit up, found by walking back from the close.

    Reading this off one full-resolution frame was wrong: that frame is whenever
    the titles last happened to be read, which is somewhere in the middle of the
    window. On one it caught the reroll animation instead -- the freshly rerolled
    card glowed at 1.69x and got published, while the augment actually taken was
    the dimmest of the three.

    The detection loop already measures all three cards every ~85 ms, so the
    moment of the pick is in that history. Only the stretch just before the close
    is searched, because a reroll earlier in the window flares just as brightly.
    """
    best = None
    for t, means, alive, _frame in reversed(hist):
        if now - t > config.FLARE_LOOKBACK_S:
            break
        if not alive:
            continue
        order = sorted(means.items(), key=lambda kv: -kv[1])
        if len(order) < 2 or order[1][1] <= 0:
            continue
        ratio = order[0][1] / order[1][1]
        if ratio >= config.SELECT_FLARE and (best is None or ratio > best[1]):
            best = (order[0][0], ratio, now - t)
    if best is None:
        return None, ""
    return best[0], f"카드 밝기 {best[1]:.2f}배, 종료 {best[2]:.1f}초 전"


def _on_window_closed(ocr, db, items, state, server, win, kept, diag, anvil,
                      level, log, hist=(), game_tag="") -> None:
    """The window shutting IS the pick.

    The original design watched for a confirmation animation -- the taken card
    flaring while the other two collapsed to about half their baseline. That
    animation does not happen here. Across three confirmed windows the winner
    crossed its 1.20 threshold 39 times and the losers never once dropped below
    0.96, against a 0.75 requirement; they sat at baseline throughout, which is
    the signature of a hover, not a selection. Raising the threshold to fit would
    make every hover a pick.

    What is reliable is the window itself: the gate opened on all three at 0.71
    or better and closed cleanly, with no false opens the whole game. So the pick
    is read off the last tooltip seen before the window went away -- the card the
    cursor was resting on, which is the card that was taken.
    """
    if anvil:
        log("  모루 화면이었으므로 기록하지 않습니다.")
        return
    if diag["settled"] == 0:
        log("  카드가 안정된 프레임이 없었습니다 — 증강창이 아니라고 보고 기록하지 않습니다.")
        return
    # Two signals, both measured against known answers. Card brightness over the
    # window as a whole is not a third: it was wrong on the windows it was asked
    # to decide, and a confident wrong augment on stream is worse than a gap.
    slot, via = _flare_slot(hist, time.time())
    if not slot and kept["hover"]:
        slot, via = kept["hover"], f"툴팁 '{kept['hover_raw']}'"
    if not slot:
        log("  어느 카드를 골랐는지 확정할 수 없습니다 "
            "(카드 밝기 차이가 작고 툴팁도 못 읽음). 기록하지 않습니다.")
        if kept["last"] is not None:
            _dump_failure(kept["last"], level, log)
        return
    card = kept["cards"].get(slot)
    if not card or card["score"] < config.OCR_MIN_SCORE:
        got = f"{card['raw']!r} {card['score']:.2f}" if card else "읽은 값 없음"
        log(f"  {slot} 카드 제목을 확정하지 못했습니다: {got}")
        if kept["last"] is not None:
            _dump_failure(kept["last"], level, log)
        return

    # The name is what the rarity is read off, not the card border. Border colour
    # has been wrong repeatedly -- 적응형 능력치 (silver) read as prismatic here and
    # gold on an earlier frame -- while an exact name gives the real rarity from
    # the data. The border only settles ties: 처형자 exists as both gold and
    # silver, and nothing but the screen can say which one is on offer.
    aug = card.get("aug")
    same_name = [a for a in db.augments if a.name == aug.name]
    if len({a.rarity for a in same_name}) > 1:
        seen = detect.rarity_of(np.array(kept["frame"])[:, :, ::-1].copy(), slot)
        aug = next((a for a in same_name if a.rarity == seen), aug)
    for slot_, was, now in kept["rerolls"]:
        log(f"    리롤 감지: {slot_} 카드 {was} -> {now}")
    if kept["tip_misses"]:
        shown = kept["tip_misses"][-3:]
        log("    툴팁 대조 실패: "
            + " | ".join(f"{r!r}->{n}({sc})" for r, n, sc in shown)
            + f"  (총 {len(kept['tip_misses'])}종)")
    elif kept["hover"] is None:
        log("    툴팁이 한 번도 읽히지 않았습니다 (커서가 카드 밖이었거나 미출현).")
    age = time.time() - (kept["full_at"] or kept["at"])
    if age > 2.0:
        log(f"    [주의] 세 장이 모두 확정된 것은 {age:.1f}초 전입니다 — "
            "리롤 직후라면 낡았을 수 있습니다.")
    others = ", ".join(f"{k}={v['name']}" for k, v in kept["cards"].items() if k != slot)
    state.picks.append(Pick(name=aug.name, rarity=aug.rarity, icon_url=aug.icon_url,
                            level=level, slot=slot,
                            confidence=round(card["score"], 2), ocr_raw=card["raw"]))
    server.save()
    log(f"  선택: {aug.name} ({aug.rarity}, 일치도 {card['score']:.2f}, "
        f"OCR '{card['raw']}' x{card['scale']}, {slot} 카드 / {via})")
    log(f"    나머지 선택지: {others}")
    _dump_decision(hist, time.time(), slot, via, aug, level, log, game_tag,
                   {k: v["name"] for k, v in kept["cards"].items()},
                   card["score"], card["raw"], kept["rerolls"],
                   kept["hover"], kept["hover_raw"])
    if kept["frame"] is not None:      # 호버 검증용, 안정화되면 조건부로 되돌린다
        _dump_failure(kept["frame"], level, log)
    # The frame above is whichever scan last read a title; the one that actually
    # triggered the close is a different moment and is what shows whether the
    # window was really gone.
    if kept["last"] is not None and kept["last"] is not kept["frame"]:
        _dump_failure(kept["last"], f"{level}-close", log)


if __name__ == "__main__":
    sys.exit(main())
