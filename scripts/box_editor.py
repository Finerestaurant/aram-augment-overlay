"""Pixel-accurate region editor, at http://127.0.0.1:8779/.

Every coordinate the detector uses was measured by hand and then argued about
from screenshots. This lets the boxes be drawn on the real frame instead --
live from OBS or on any frame kept under state/debug/ -- and writes them where
the tool will pick them up.

    python scripts/box_editor.py

Saved boxes go to state/boxes.json and override the defaults in config.py.
"""
from __future__ import annotations

import io
import json
import sys
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import parse_qs, urlparse

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from aram_overlay import config                                      # noqa: E402
from aram_overlay.obs import ObsCapture, read_obs_websocket_config   # noqa: E402

PORT = 8779
STORE = config.STATE / "boxes.json"

REGIONS = [
    ("reroll1", "리롤 1", "#ffb400"),
    ("reroll2", "리롤 2", "#ffb400"),
    ("reroll3", "리롤 3", "#ffb400"),
    ("title_L", "제목 L", "#78b4ff"),
    ("title_M", "제목 M", "#78b4ff"),
    ("title_R", "제목 R", "#78b4ff"),
    ("hide",    "숨김 버튼", "#00ffb4"),
    ("tooltip", "호버 툴팁", "#ff8fa3"),
]


def defaults() -> dict:
    bw, bh = config.REROLL_SIZE
    out = {}
    for i, (bx, by) in enumerate(config.REROLL_BOXES, 1):
        out[f"reroll{i}"] = [bx, by, bx + bw, by + bh]
    for slot, box in config.CARD_TITLES.items():
        out[f"title_{slot}"] = list(box)
    out["hide"] = list(getattr(config, "HIDE_BOX", (857, 823, 1064, 896)))
    out["tooltip"] = list(config.HOVER_TOOLTIP)
    return out


def current() -> dict:
    boxes = defaults()
    if STORE.exists():
        try:
            boxes.update(json.loads(STORE.read_text(encoding="utf-8")))
        except Exception:
            pass
    return boxes


def frame_list() -> list[str]:
    d = config.STATE / "debug"
    if not d.exists():
        return []
    return sorted((p.name for p in d.glob("*_full.png")), reverse=True)[:60]


def grab_live() -> bytes | None:
    try:
        pw = (read_obs_websocket_config() or {}).get("server_password", "")
        cap = ObsCapture(port=config.OBS_PORT, password=pw, source=config.OBS_SOURCE)
        im = cap.grab(width=config.BASE_W, height=config.BASE_H, quality=95, fmt="png")
        if im is None:
            return None
        buf = io.BytesIO()
        im.save(buf, format="PNG")
        return buf.getvalue()
    except Exception:
        return None


PAGE = r"""<!doctype html>
<meta charset="utf-8">
<title>영역 편집기</title>
<style>
  body { margin:0; background:#0e1016; color:#e6e9ef; user-select:none;
         font:14px "Malgun Gothic", system-ui, sans-serif; display:flex; height:100vh; }
  #side { width:250px; flex:none; border-right:1px solid #232733; padding:10px;
          overflow:auto; }
  #main { flex:1; position:relative; overflow:hidden; }
  canvas#view { position:absolute; inset:0; width:100%; height:100%;
                display:block; }
  canvas#loupe { position:absolute; right:10px; top:10px; width:200px; height:200px;
                 border:1px solid #333d50; background:#000; pointer-events:none; }
  h2 { font-size:12px; margin:12px 0 5px; color:#8b93a7; font-weight:600; }
  .row { display:flex; align-items:center; gap:6px; padding:3px 5px; border-radius:4px;
         cursor:pointer; }
  .row.sel { background:#2a3446; }
  .sw { width:10px; height:10px; border-radius:2px; flex:none; }
  .nm { flex:1; font-size:13px; }
  .co { font-variant-numeric:tabular-nums; color:#8b93a7; font-size:11px; }
  button, select { background:#232a38; color:#e6e9ef; border:1px solid #333d50;
                   border-radius:4px; padding:3px 8px; font:inherit; cursor:pointer;
                   font-size:13px; }
  button.pri { background:#1f7a3f; border-color:#2a9d52; }
  #nums { display:grid; grid-template-columns:auto 1fr auto 1fr; gap:4px 6px;
          align-items:center; margin-top:8px; }
  #nums input { width:100%; background:#171b25; color:#e6e9ef; border:1px solid #333d50;
                border-radius:3px; padding:2px 4px; font:inherit; font-size:13px;
                font-variant-numeric:tabular-nums; }
  .hint { color:#6f7783; font-size:11px; line-height:1.7; margin-top:10px; }
  #msg { margin-top:6px; color:#9ef5c0; font-size:12px; min-height:15px; }
  .zoom { display:flex; gap:4px; margin-top:8px; }
  .zoom button { flex:1; }
</style>
<div id="side">
  <div style="display:flex;gap:5px">
    <select id="src" style="flex:1;min-width:0"></select>
    <button id="reload">갱신</button>
  </div>
  <div class="zoom">
    <button data-z="fit">맞춤</button><button data-z="1">1:1</button>
    <button data-z="2">2×</button><button data-z="4">4×</button>
  </div>
  <h2>영역</h2>
  <div id="list"></div>
  <div id="nums">
    <span>x0</span><input id="x0" type="number"><span>y0</span><input id="y0" type="number">
    <span>x1</span><input id="x1" type="number"><span>y1</span><input id="y1" type="number">
  </div>
  <div style="margin-top:9px;display:flex;gap:5px;flex-wrap:wrap">
    <button id="save" class="pri">저장</button>
    <button id="undoBtn" disabled>↶ 취소</button>
    <button id="redoBtn" disabled>↷ 재실행</button>
    <button id="reset">기본값</button>
    <button id="copy">config 복사</button>
  </div>
  <div id="msg"></div>
  <div class="hint">
    박스 클릭 → 선택 · 안쪽 드래그 → 이동<br>
    모서리/변 핸들 드래그 → 크기 조절<br>
    빈 곳 드래그 → 선택된 영역 새로 그리기<br>
    화살표 1px 이동 · Shift+화살표 크기<br>
    휠 확대 · 가운데버튼(또는 Space) 화면 이동<br>
    Shift+휠 좌우 이동 · Alt+휠 상하 이동<br>
    Ctrl+Z 실행취소 · Ctrl+Shift+Z 재실행
  </div>
</div>
<div id="main">
  <canvas id="view"></canvas>
  <canvas id="loupe" width="200" height="200"></canvas>
</div>
<script>
const view = document.getElementById("view"), g = view.getContext("2d");
const loupe = document.getElementById("loupe"), lg = loupe.getContext("2d");
let img = new Image(), boxes = {}, regions = [], sel = null;
let zoom = 1, ox = 0, oy = 0;
let act = null, pan = null, cursor = null, space = false;
const HANDLE = 7;                       // 화면 픽셀 기준 핸들 반경

// 실행취소: 바꾸기 직전 상태를 쌓는다. 드래그는 시작할 때 한 번만 쌓아서
// 한 번의 드래그가 한 번의 취소로 되돌아가게 한다.
let undoStack = [], redoStack = [], lastPushAt = 0;
const snap = () => JSON.stringify(boxes);
function pushUndo(coalesceMs){
  const now = Date.now();
  if (coalesceMs && now - lastPushAt < coalesceMs) { lastPushAt = now; return; }
  lastPushAt = now;
  undoStack.push(snap());
  if (undoStack.length > 200) undoStack.shift();
  redoStack.length = 0;
  updateUndoUi();
}
function applyState(json){
  boxes = JSON.parse(json);
  sync(); draw();
}
function undo(){
  if (!undoStack.length) { msg.textContent = "되돌릴 것이 없습니다"; return; }
  redoStack.push(snap());
  applyState(undoStack.pop());
  msg.textContent = "실행취소 (남은 " + undoStack.length + ")";
  updateUndoUi();
}
function redo(){
  if (!redoStack.length) { msg.textContent = "다시 실행할 것이 없습니다"; return; }
  undoStack.push(snap());
  applyState(redoStack.pop());
  msg.textContent = "다시 실행 (남은 " + redoStack.length + ")";
  updateUndoUi();
}
function updateUndoUi(){
  const u = document.getElementById("undoBtn"), r = document.getElementById("redoBtn");
  if (u) u.disabled = !undoStack.length;
  if (r) r.disabled = !redoStack.length;
}

function fit(){
  const r = view.getBoundingClientRect();
  view.width = Math.max(1, Math.round(r.width));
  view.height = Math.max(1, Math.round(r.height));
  draw();
}
addEventListener("resize", fit);

const S = (x, y) => [(x + ox) * zoom, (y + oy) * zoom];
const toImg = (sx, sy) => [Math.round(sx / zoom - ox), Math.round(sy / zoom - oy)];

function setZoom(z, cx, cy){
  cx = cx === undefined ? view.width/2 : cx;
  cy = cy === undefined ? view.height/2 : cy;
  const before = toImg(cx, cy);
  zoom = Math.max(0.2, Math.min(16, z));
  const after = toImg(cx, cy);
  ox += after[0] - before[0]; oy += after[1] - before[1];
  draw();
}
function fitView(){
  if (!img.width) return;
  zoom = view.width / img.width;                 // 폭에 맞춰 크게
  ox = 0;
  oy = -(img.height - view.height/zoom) / 2;     // 세로 가운데
  draw();
}
for (const b of document.querySelectorAll(".zoom button")) b.onclick = () => {
  const z = b.dataset.z;
  if (z === "fit") fitView();
  else { const k = +z; ox = -(img.width/2 - view.width/(2*k));
         oy = -(650 - view.height/(2*k)); zoom = k; draw(); }
};

function handles(b){
  const [x0,y0] = S(b[0],b[1]), [x1,y1] = S(b[2],b[3]);
  return {nw:[x0,y0], n:[(x0+x1)/2,y0], ne:[x1,y0], e:[x1,(y0+y1)/2],
          se:[x1,y1], s:[(x0+x1)/2,y1], sw:[x0,y1], w:[x0,(y0+y1)/2]};
}
function hitTest(sx, sy){
  if (sel && boxes[sel]) {
    const hs = handles(boxes[sel]);
    for (const k in hs) {
      const [hx,hy] = hs[k];
      if (Math.abs(sx-hx) <= HANDLE && Math.abs(sy-hy) <= HANDLE)
        return {id: sel, mode: k};
    }
  }
  const order = sel ? [sel].concat(regions.map(r=>r.id).filter(i=>i!==sel))
                    : regions.map(r=>r.id);
  for (const id of order) {
    const b = boxes[id]; if (!b) continue;
    const [x0,y0] = S(b[0],b[1]), [x1,y1] = S(b[2],b[3]);
    if (sx>=x0 && sx<=x1 && sy>=y0 && sy<=y1) return {id, mode:"move"};
  }
  return null;
}
const CURSORS = {move:"move", nw:"nwse-resize", se:"nwse-resize", ne:"nesw-resize",
                 sw:"nesw-resize", n:"ns-resize", s:"ns-resize", e:"ew-resize", w:"ew-resize"};

function draw(){
  g.fillStyle = "#0b0d12"; g.fillRect(0,0,view.width,view.height);
  if (img.width) {
    g.imageSmoothingEnabled = zoom < 2;
    g.drawImage(img, ox*zoom, oy*zoom, img.width*zoom, img.height*zoom);
  }
  for (const r of regions) {
    const b = boxes[r.id]; if (!b) continue;
    const [x0,y0] = S(b[0],b[1]), [x1,y1] = S(b[2],b[3]);
    const on = r.id === sel;
    g.strokeStyle = r.color; g.lineWidth = on ? 2.5 : 1.2;
    g.strokeRect(x0, y0, x1-x0, y1-y0);
    if (on) {
      g.fillStyle = r.color + "1f"; g.fillRect(x0, y0, x1-x0, y1-y0);
      const hs = handles(b);
      g.fillStyle = r.color;
      for (const k in hs) g.fillRect(hs[k][0]-4, hs[k][1]-4, 8, 8);
    }
    g.fillStyle = r.color; g.font = "12px sans-serif";
    g.fillText(r.name, x0, y0 - 5);
  }
  if (act && act.mode === "new") {
    const [x0,y0] = S(act.x0,act.y0), [x1,y1] = S(act.x1,act.y1);
    g.strokeStyle = "#fff"; g.setLineDash([4,3]); g.lineWidth = 1;
    g.strokeRect(Math.min(x0,x1), Math.min(y0,y1), Math.abs(x1-x0), Math.abs(y1-y0));
    g.setLineDash([]);
  }
  drawLoupe();
}
function drawLoupe(){
  lg.fillStyle = "#000"; lg.fillRect(0,0,200,200);
  if (!img.width || !cursor) return;
  const Z = 8, half = 200/Z/2;
  lg.imageSmoothingEnabled = false;
  lg.drawImage(img, cursor[0]-half, cursor[1]-half, half*2, half*2, 0, 0, 200, 200);
  lg.strokeStyle = "#ff3b6b"; lg.lineWidth = 1;
  lg.beginPath(); lg.moveTo(100,0); lg.lineTo(100,200);
  lg.moveTo(0,100); lg.lineTo(200,100); lg.stroke();
  lg.fillStyle = "#0e1016cc"; lg.fillRect(0,182,200,18);
  lg.fillStyle = "#e6e9ef"; lg.font = "12px monospace";
  lg.fillText(cursor[0] + ", " + cursor[1], 6, 195);
}

view.onmousedown = e => {
  const r = view.getBoundingClientRect();
  const sx = e.clientX - r.left, sy = e.clientY - r.top;
  if (e.button === 1 || space) { pan = [e.clientX, e.clientY]; e.preventDefault(); return; }
  const h = hitTest(sx, sy);
  const p = toImg(sx, sy);
  if (h) {
    if (h.id !== sel) { sel = h.id; sync(); }
    pushUndo();
    act = {mode: h.mode, from: p, orig: boxes[h.id].slice(), id: h.id};
  } else if (sel) {
    pushUndo();
    act = {mode: "new", x0: p[0], y0: p[1], x1: p[0], y1: p[1]};
  }
  draw();
};
addEventListener("mousemove", e => {
  const r = view.getBoundingClientRect();
  const sx = e.clientX - r.left, sy = e.clientY - r.top;
  cursor = toImg(sx, sy);
  if (pan) { ox += (e.clientX - pan[0]) / zoom; oy += (e.clientY - pan[1]) / zoom;
             pan = [e.clientX, e.clientY]; draw(); return; }
  if (act) {
    if (act.mode === "new") { act.x1 = cursor[0]; act.y1 = cursor[1]; }
    else {
      const dx = cursor[0] - act.from[0], dy = cursor[1] - act.from[1];
      const o = act.orig, b = boxes[act.id];
      if (act.mode === "move") { b[0]=o[0]+dx; b[1]=o[1]+dy; b[2]=o[2]+dx; b[3]=o[3]+dy; }
      else {
        b[0]=o[0]; b[1]=o[1]; b[2]=o[2]; b[3]=o[3];
        if (act.mode.includes("w")) b[0] = Math.min(o[0]+dx, o[2]-2);
        if (act.mode.includes("e")) b[2] = Math.max(o[2]+dx, o[0]+2);
        if (act.mode.includes("n")) b[1] = Math.min(o[1]+dy, o[3]-2);
        if (act.mode.includes("s")) b[3] = Math.max(o[3]+dy, o[1]+2);
      }
      sync();
    }
    draw();
  } else {
    const h = hitTest(sx, sy);
    view.style.cursor = h ? CURSORS[h.mode] : (sel ? "crosshair" : "default");
  }
});
addEventListener("mouseup", () => {
  pan = null;
  if (act && act.mode === "new" && sel) {
    const b = [Math.min(act.x0,act.x1), Math.min(act.y0,act.y1),
               Math.max(act.x0,act.x1), Math.max(act.y0,act.y1)];
    if (b[2]-b[0] > 3 && b[3]-b[1] > 3) { boxes[sel] = b; sync(); }
  }
  act = null; draw();
});
view.onwheel = e => {
  e.preventDefault();
  if (e.shiftKey) {                     // Shift+휠: 좌우 이동
    ox -= (e.deltaY || e.deltaX) / zoom;
    draw(); return;
  }
  if (e.altKey) {                       // Alt+휠: 위아래 이동
    oy -= e.deltaY / zoom;
    draw(); return;
  }
  const r = view.getBoundingClientRect();
  setZoom(zoom * (e.deltaY > 0 ? 0.85 : 1.18), e.clientX - r.left, e.clientY - r.top);
};
addEventListener("keydown", e => {
  if (e.code === "Space") { space = true; return; }
  const mod = e.ctrlKey || e.metaKey;
  if (mod && e.key.toLowerCase() === "z") {
    e.preventDefault(); e.shiftKey ? redo() : undo(); return;
  }
  if (mod && e.key.toLowerCase() === "y") { e.preventDefault(); redo(); return; }
  if (e.target.tagName === "INPUT" || !sel || !boxes[sel]) return;
  const d = {ArrowLeft:[-1,0], ArrowRight:[1,0], ArrowUp:[0,-1], ArrowDown:[0,1]}[e.key];
  if (!d) return;
  e.preventDefault();
  pushUndo(500);              // 연속 방향키는 한 덩어리로
  const b = boxes[sel];
  if (e.shiftKey) { b[2] += d[0]; b[3] += d[1]; }
  else { b[0]+=d[0]; b[1]+=d[1]; b[2]+=d[0]; b[3]+=d[1]; }
  sync(); draw();
});
addEventListener("keyup", e => { if (e.code === "Space") space = false; });

function sync(){
  const b = boxes[sel];
  if (b) { x0.value=b[0]; y0.value=b[1]; x1.value=b[2]; y1.value=b[3]; }
  for (const el of document.querySelectorAll(".row")) {
    const id = el.dataset.id;
    el.classList.toggle("sel", id === sel);
    const bb = boxes[id];
    el.querySelector(".co").textContent = bb ? (bb[2]-bb[0]) + "×" + (bb[3]-bb[1]) : "–";
  }
}
for (const el of [x0,y0,x1,y1]) el.oninput = () => {
  if (!sel) return;
  pushUndo(700);
  boxes[sel] = [+x0.value, +y0.value, +x1.value, +y1.value]; sync(); draw();
};

async function load(){
  const d = await (await fetch("/boxes")).json();
  boxes = d.boxes; regions = d.regions;
  document.getElementById("list").innerHTML = regions.map(r =>
    '<div class="row" data-id="' + r.id + '"><span class="sw" style="background:' +
    r.color + '"></span><span class="nm">' + r.name + '</span><span class="co"></span></div>'
  ).join("");
  for (const el of document.querySelectorAll(".row"))
    el.onclick = () => { sel = el.dataset.id; sync(); draw(); };
  sel = sel || regions[0].id;
  const s = document.getElementById("src");
  const frames = await (await fetch("/frames")).json();
  s.innerHTML = ['<option value="live">실시간 캡처</option>']
    .concat(frames.map(f => '<option value="' + f + '">' + f + '</option>')).join("");
  if (frames.length) s.value = frames[0];        // 게임이 꺼져 있어도 뭔가 보이도록
  sync(); loadImage();
}
function loadImage(){
  const v = document.getElementById("src").value;
  img = new Image();
  img.onload = () => { fit(); fitView(); };
  img.src = "/frame?src=" + encodeURIComponent(v) + "&t=" + Date.now();
}
document.getElementById("reload").onclick = loadImage;
document.getElementById("src").onchange = loadImage;
document.getElementById("save").onclick = async () => {
  await fetch("/boxes", {method:"POST", body: JSON.stringify(boxes)});
  msg.textContent = "저장됨 → state/boxes.json";
};
document.getElementById("reset").onclick = async () => {
  pushUndo();
  const d = await (await fetch("/boxes?defaults=1")).json();
  boxes = d.boxes; sync(); draw(); msg.textContent = "기본값으로 되돌림 (저장 안 됨)";
};
document.getElementById("copy").onclick = () => {
  const b = boxes;
  const txt =
    "REROLL_BOXES = [(" + b.reroll1[0] + ", " + b.reroll1[1] + "), (" +
      b.reroll2[0] + ", " + b.reroll2[1] + "), (" + b.reroll3[0] + ", " + b.reroll3[1] + ")]\n" +
    "REROLL_SIZE = (" + (b.reroll1[2]-b.reroll1[0]) + ", " + (b.reroll1[3]-b.reroll1[1]) + ")\n" +
    'CARD_TITLES = {"L": (' + b.title_L + '), "M": (' + b.title_M + '), "R": (' + b.title_R + ")}\n" +
    "HIDE_BOX = (" + b.hide + ")\n" +
    "HOVER_TOOLTIP = (" + b.tooltip + ")";
  navigator.clipboard.writeText(txt);
  msg.textContent = "config 스니펫 복사됨";
};
document.getElementById("undoBtn").onclick = undo;
document.getElementById("redoBtn").onclick = redo;
fit(); load();
</script>
"""


class H(BaseHTTPRequestHandler):
    def log_message(self, *a):
        pass

    def _send(self, body: bytes, ctype: str):
        self.send_response(200)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        u = urlparse(self.path)
        q = parse_qs(u.query)
        if u.path == "/boxes":
            boxes = defaults() if q.get("defaults") else current()
            self._send(json.dumps({
                "boxes": boxes,
                "regions": [{"id": i, "name": n, "color": c} for i, n, c in REGIONS],
            }).encode(), "application/json")
        elif u.path == "/frames":
            self._send(json.dumps(frame_list()).encode(), "application/json")
        elif u.path == "/frame":
            src = (q.get("src") or ["live"])[0]
            if src == "live":
                data = grab_live()
                if data is None:
                    self.send_error(503, "OBS capture unavailable")
                    return
            else:
                p = config.STATE / "debug" / src
                if not p.exists():
                    self.send_error(404)
                    return
                data = p.read_bytes()
            self._send(data, "image/png")
        else:
            self._send(PAGE.encode("utf-8"), "text/html; charset=utf-8")

    def do_POST(self):
        n = int(self.headers.get("Content-Length", 0))
        try:
            boxes = json.loads(self.rfile.read(n).decode("utf-8"))
        except Exception:
            self.send_error(400)
            return
        STORE.parent.mkdir(parents=True, exist_ok=True)
        STORE.write_text(json.dumps(boxes, indent=2), encoding="utf-8")
        self._send(b'{"ok":true}', "application/json")


def main() -> int:
    print(f"영역 편집기: http://127.0.0.1:{PORT}/   (Ctrl+C 로 종료)")
    print(f"  저장 위치: {STORE}")
    try:
        ThreadingHTTPServer(("127.0.0.1", PORT), H).serve_forever()
    except KeyboardInterrupt:
        pass
    return 0


if __name__ == "__main__":
    sys.exit(main())
