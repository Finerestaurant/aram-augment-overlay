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

import numpy as np
import requests
import urllib3
from PIL import Image

from . import config, detect
from .augments import AugmentDB
from .obs import ObsCapture, read_obs_websocket_config
from .ocr import TooltipOCR
from .server import Pick, RunState, WidgetServer

urllib3.disable_warnings()


def log(msg: str) -> None:
    print(f"[{time.strftime('%H:%M:%S')}] {msg}", flush=True)


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

    try:
        obs = ObsCapture(port=args.port, password=load_password(args.password), source=args.source)
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

    state = RunState()
    server = WidgetServer(state, port=args.widget_port)
    url = server.start()
    log(f"위젯 주소: {url}   <- OBS 브라우저 소스에 이 주소를 넣으세요 (권장 크기 420x600)")

    win = detect.WindowState()
    baseline_samples: list[float] = []
    current_game_id = None
    log("대기 중... 아수라장 게임을 시작하세요.")

    try:
        while True:
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
                log(f"게임 감지: gameMode={mode}, level={level}")
                if mode != config.MAYHEM_GAME_MODE:
                    log(f"  아수라장(KIWI)이 아닙니다. 증강 감지를 건너뜁니다.")

            # a fresh game rewinds gameTime; start a new list
            if current_game_id is not None and isinstance(game_id, (int, float)) \
                    and game_id + 5 < current_game_id:
                log("새 게임이 시작되어 목록을 초기화합니다.")
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
            weak = min(scores) >= config.GATE_STAY or \
                sum(1 for s in scores if s >= config.GATE_OPEN) >= 2

            if not win.open:
                win.open_streak = win.open_streak + 1 if strong else 0
                if win.open_streak >= 2:
                    win = detect.WindowState(open=True, opened_at=time.time())
                    baseline_samples = []
                    log(f"=== 증강 선택창 (레벨 {level}) ===")
            else:
                win.miss_streak = 0 if weak else win.miss_streak + 1
                if win.miss_streak >= config.CLOSE_MISSES:
                    log("=== 선택창 종료 ===")
                    win = detect.WindowState()
                    time.sleep(0.3)
                    continue

            if not win.open:
                time.sleep(0.35)
                continue

            means = detect.card_means(frame)
            settled = detect.interior_darkness(frame) < 60 and \
                (time.time() - win.opened_at) > config.ENTRY_ANIM_S
            if settled:
                baseline_samples.append(statistics.median(means.values()))
                if len(baseline_samples) > 30:
                    baseline_samples.pop(0)
                win.baseline = statistics.median(baseline_samples)

                hover, _ = detect.hovered_card(means)
                if hover:
                    win.hover_history.append(hover)
                    win.last_hover = hover

            chosen = detect.selected_card(means, win.baseline) if win.baseline else None
            if chosen:
                slot = chosen if chosen else (win.last_hover or "?")
                _record_pick(obs, ocr, db, state, server, slot, level, log)
                log("=== 선택 완료, 창 종료 대기 ===")
                win = detect.WindowState()
                time.sleep(1.5)

    except KeyboardInterrupt:
        log("종료합니다.")
    finally:
        server.save()
        server.stop()
    return 0


def _record_pick(obs, ocr, db, state, server, slot, level, log) -> None:
    """Read the tooltip on a full-resolution frame and append the pick."""
    full = obs.grab(width=config.BASE_W, height=config.BASE_H,
                    quality=config.OCR_QUALITY, fmt="jpg")
    if full is None:
        log("  전체 해상도 프레임을 받지 못했습니다.")
        return
    rarity = detect.rarity_of(np.array(full)[:, :, ::-1].copy())
    raw, _ = ocr.read_title(full)
    aug, score = db.match(raw, rarity if rarity in ("silver", "gold", "prismatic") else None)

    if aug and score >= config.OCR_MIN_SCORE:
        pick = Pick(name=aug.name, rarity=aug.rarity, icon_url=aug.icon_url,
                    level=level, slot=slot, confidence=round(score, 2), ocr_raw=raw)
        log(f"  선택: {aug.name} ({aug.rarity}, 일치도 {score:.2f}, OCR '{raw}')")
    else:
        pick = Pick(name=raw or "인식 실패", rarity=rarity, icon_url="",
                    level=level, slot=slot, confidence=round(score, 2), ocr_raw=raw)
        log(f"  인식 실패: OCR '{raw}' 최고 일치도 {score:.2f} ({rarity})")

    state.picks.append(pick)
    server.save()


if __name__ == "__main__":
    sys.exit(main())
