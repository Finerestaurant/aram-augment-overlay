"""Live view of the reroll-button gate scores, at http://127.0.0.1:8778/.

The gate decides whether an augment window is open, and when it gets that wrong
there is nothing to look at afterwards -- a window that never opened leaves no
log line and no frame. This samples the same three scores the tool does and
plots them against the thresholds, so a near miss is visible while it happens.

Runs as its own process on its own OBS connection; the overlay does not need to
be restarted or stopped to use it.

    python scripts/gate_monitor.py
"""
from __future__ import annotations

import json
import sys
import threading
import time
from collections import deque
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

import cv2

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from aram_overlay import config, detect                      # noqa: E402
from aram_overlay.obs import ObsCapture, read_obs_websocket_config   # noqa: E402

PORT = 8778
SAMPLE_S = 0.30
KEEP = 6000                     # ~30 minutes of history

samples: deque = deque(maxlen=KEEP)
best_miss = {"score": 0.0, "at": 0.0, "path": ""}
lock = threading.Lock()

PAGE = """<!doctype html>
<meta charset="utf-8">
<title>게이트 모니터</title>
<style>
  body { margin:0; background:#0e1016; color:#e6e9ef;
         font:14px "Malgun Gothic", system-ui, sans-serif; user-select:none; }
  header { padding:10px 16px; display:flex; gap:18px; align-items:baseline;
           border-bottom:1px solid #232733; flex-wrap:wrap; }
  h1 { font-size:15px; margin:0; font-weight:600; }
  .k { color:#8b93a7; }
  .v { font-variant-numeric:tabular-nums; }
  #open { padding:2px 10px; border-radius:4px; background:#2a2f3d; }
  #open.on { background:#1f7a3f; }
  button { background:#232a38; color:#e6e9ef; border:1px solid #333d50;
           border-radius:4px; padding:3px 10px; cursor:pointer; font:inherit; }
  button.live { background:#1f7a3f; border-color:#2a9d52; }
  canvas { display:block; width:100%; height:calc(100vh - 122px); cursor:grab; }
  canvas.drag { cursor:grabbing; }
  footer { padding:8px 16px; color:#8b93a7; border-top:1px solid #232733;
           display:flex; gap:20px; flex-wrap:wrap; }
  b.L{color:#7fb2ff} b.M{color:#ffd479} b.R{color:#ff8fa3} b.m{color:#9ef5c0}
</style>
<header>
  <h1>리롤 게이트 실시간</h1>
  <span><span class="k">L</span> <b class="L v" id="l">–</b></span>
  <span><span class="k">M</span> <b class="M v" id="m">–</b></span>
  <span><span class="k">R</span> <b class="R v" id="r">–</b></span>
  <span><span class="k">min</span> <b class="m v" id="mn">–</b></span>
  <span id="open">닫힘</span>
  <span><span class="k">60초 최고</span> <b class="v" id="peak">–</b></span>
  <span><span class="k">근접 최고</span> <b class="v" id="miss">–</b></span>
  <button id="liveBtn" class="live">실시간</button>
</header>
<canvas id="c"></canvas>
<footer>
  <span id="range">–</span>
  <span class="k">드래그: 좌우 이동 · 휠: 확대/축소 · 더블클릭: 실시간 복귀</span>
  <span id="foot"></span>
</footer>
<script>
const c = document.getElementById("c"), g = c.getContext("2d");
let all = [], meta = {thresholds:{}}, lastT = 0;
let span = 90, viewEnd = 0, follow = true;

function fit(){ c.width = c.clientWidth*devicePixelRatio; c.height = c.clientHeight*devicePixelRatio; draw(); }
addEventListener("resize", fit);

function setFollow(on){ follow = on; liveBtn.classList.toggle("live", on); }
liveBtn.onclick = () => { setFollow(true); draw(); };
c.ondblclick = () => { setFollow(true); draw(); };

let dragX = null;
c.onmousedown = e => { dragX = e.clientX; c.classList.add("drag"); };
addEventListener("mouseup", () => { dragX = null; c.classList.remove("drag"); });
addEventListener("mousemove", e => {
  if (dragX === null || !all.length) return;
  const perPx = span / c.clientWidth;
  viewEnd -= (e.clientX - dragX) * perPx;
  dragX = e.clientX;
  setFollow(false);
  clampView();
  draw();
});
c.onwheel = e => {
  e.preventDefault();
  const k = e.deltaY > 0 ? 1.25 : 0.8;
  span = Math.max(5, Math.min(1800, span * k));
  clampView(); draw();
};

function clampView(){
  if (!all.length) return;
  const first = all[0][0], last = all[all.length-1][0];
  viewEnd = Math.max(first + span*0.15, Math.min(last, viewEnd));
}

function draw(){
  const w = c.width, h = c.height, pad = 42*devicePixelRatio;
  g.clearRect(0,0,w,h);
  if (!all.length) return;
  const end = follow ? all[all.length-1][0] : viewEnd;
  const start = end - span;
  const X = t => pad + (w - pad*1.2) * (1 - (end - t)/span);
  const Y = v => h - pad - (h - pad*1.6) * Math.max(0, Math.min(1, v));

  g.font = (11*devicePixelRatio)+"px sans-serif";
  for (let v = 0; v <= 1.0001; v += 0.2) {
    g.strokeStyle = "#1c2130"; g.lineWidth = devicePixelRatio;
    g.beginPath(); g.moveTo(pad, Y(v)); g.lineTo(w-pad*0.2, Y(v)); g.stroke();
    g.fillStyle = "#5c6478"; g.fillText(v.toFixed(1), 6*devicePixelRatio, Y(v)+4*devicePixelRatio);
  }
  const th = meta.thresholds;
  for (const [name, val, col] of [["OPEN "+th.open, th.open, "#59d38a"],
                                  ["STAY2 "+th.stay_two, th.stay_two, "#d3a659"],
                                  ["STAY "+th.stay, th.stay, "#8a6fd0"]]) {
    if (val === undefined) continue;
    g.strokeStyle = col; g.setLineDash([6*devicePixelRatio,5*devicePixelRatio]);
    g.lineWidth = devicePixelRatio; g.beginPath();
    g.moveTo(pad, Y(val)); g.lineTo(w-pad*0.2, Y(val)); g.stroke(); g.setLineDash([]);
    g.fillStyle = col; g.fillText(name, w-pad*4.4, Y(val)-5*devicePixelRatio);
  }
  const view = all.filter(p => p[0] >= start - 1 && p[0] <= end + 1);
  for (const [i, col] of [[1,"#7fb2ff"],[2,"#ffd479"],[3,"#ff8fa3"]]) {
    g.strokeStyle = col; g.lineWidth = 1.4*devicePixelRatio; g.beginPath();
    view.forEach((p, k) => { const x = X(p[0]), y = Y(p[i]);
      k ? g.lineTo(x,y) : g.moveTo(x,y); });
    g.stroke();
  }
  g.strokeStyle = "#9ef5c0"; g.lineWidth = 2.4*devicePixelRatio; g.beginPath();
  view.forEach((p, k) => { const x = X(p[0]), y = Y(Math.min(p[1],p[2],p[3]));
    k ? g.lineTo(x,y) : g.moveTo(x,y); });
  g.stroke();

  const fmt = t => new Date(t*1000).toLocaleTimeString();
  range.textContent = view.length
    ? `${fmt(start)} ~ ${fmt(end)}  (${span.toFixed(0)}초)` + (follow ? "  · 실시간" : "  · 정지")
    : "표시할 구간에 샘플이 없습니다";
}

async function tick(){
  try {
    const r = await fetch("/data.json?since=" + lastT, {cache:"no-store"});
    const d = await r.json();
    meta = d;
    if (d.samples.length){
      all = all.concat(d.samples);
      lastT = all[all.length-1][0];
      const p = all[all.length-1], mn = Math.min(p[1],p[2],p[3]);
      l.textContent = p[1].toFixed(2); m.textContent = p[2].toFixed(2);
      rv.textContent = p[3].toFixed(2); mv.textContent = mn.toFixed(2);
      const on = mn >= d.thresholds.open;
      openEl.textContent = on ? "열림" : "닫힘";
      openEl.className = on ? "on" : "";
      const now = p[0];
      const recent = all.filter(x => now - x[0] <= 60).map(x => Math.min(x[1],x[2],x[3]));
      peak.textContent = recent.length ? Math.max(...recent).toFixed(2) : "–";
    }
    miss.textContent = d.best_miss && d.best_miss.score ? d.best_miss.score.toFixed(2) : "–";
    foot.textContent = d.best_miss && d.best_miss.path ? "근접 프레임: " + d.best_miss.path : "";
    draw();
  } catch(e) { foot.textContent = "샘플러 연결 끊김"; }
}
const rv = document.getElementById("r"), mv = document.getElementById("mn"),
      openEl = document.getElementById("open");
fit(); tick(); setInterval(tick, 400);
</script>
"""


def sampler():
    pw = (read_obs_websocket_config() or {}).get("server_password", "")
    cap = ObsCapture(port=config.OBS_PORT, password=pw, source=config.OBS_SOURCE)
    gate = detect.TemplateGate()
    out = config.STATE / "debug" / "gate"
    out.mkdir(parents=True, exist_ok=True)
    while True:
        t0 = time.time()
        try:
            frame = cap.grab_cv()
        except Exception:
            frame = None
        if frame is not None:
            sc = gate.scores(frame)
            with lock:
                samples.append([t0, sc[0], sc[1], sc[2]])
                lo = min(sc)
                # the frame worth keeping is the closest the gate came to opening
                # without doing so -- that is the one that says whether the
                # threshold is wrong or the window simply was not on screen
                if config.GATE_STAY <= lo < config.GATE_OPEN and lo > best_miss["score"]:
                    p = out / f"nearmiss_{lo:.2f}_{time.strftime('%H%M%S')}.png"
                    cv2.imwrite(str(p), frame)
                    best_miss.update(score=lo, at=t0, path=str(p))
        time.sleep(max(0.0, SAMPLE_S - (time.time() - t0)))


class H(BaseHTTPRequestHandler):
    def log_message(self, *a):
        pass

    def do_GET(self):
        if self.path.startswith("/data.json"):
            since = 0.0
            if "?" in self.path:
                for part in self.path.split("?", 1)[1].split("&"):
                    if part.startswith("since="):
                        try:
                            since = float(part[6:])
                        except ValueError:
                            pass
            with lock:
                fresh = [x for x in samples if x[0] > since]
                body = json.dumps({
                    "samples": fresh,
                    "oldest": samples[0][0] if samples else 0.0,
                    "best_miss": dict(best_miss),
                    "thresholds": {"open": config.GATE_OPEN, "stay": config.GATE_STAY,
                                   "stay_two": config.GATE_STAY_TWO},
                }).encode()
            ctype = "application/json"
        else:
            body, ctype = PAGE.encode("utf-8"), "text/html; charset=utf-8"
        self.send_response(200)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(body)


def main() -> int:
    threading.Thread(target=sampler, daemon=True).start()
    srv = ThreadingHTTPServer(("127.0.0.1", PORT), H)
    print(f"게이트 모니터: http://127.0.0.1:{PORT}/   (Ctrl+C 로 종료)")
    print(f"  임계값  OPEN {config.GATE_OPEN}  STAY_TWO {config.GATE_STAY_TWO}  "
          f"STAY {config.GATE_STAY}")
    try:
        srv.serve_forever()
    except KeyboardInterrupt:
        pass
    return 0


if __name__ == "__main__":
    sys.exit(main())
