"""Decisions, grouped by game, at http://127.0.0.1:8780/.

When a pick comes out wrong the log says what was decided but not what it was
decided from. Every pick writes the stretch of frames it was read off; this
serves them per game and refreshes while you play, so a bad call can be looked
at without leaving the match.

    python scripts/decision_viewer.py
"""
from __future__ import annotations

import json
import sys
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import parse_qs, unquote, urlparse

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from aram_overlay import config                                      # noqa: E402

PORT = 8780
ROOT = config.STATE / "debug" / "decisions"


def index() -> list[dict]:
    """Games newest first, each with its picks in the order they happened."""
    if not ROOT.exists():
        return []
    games = []
    for d in sorted((p for p in ROOT.iterdir() if p.is_dir()), reverse=True):
        picks = []
        for js in sorted(d.glob("*.json")):
            try:
                meta = json.loads(js.read_text(encoding="utf-8"))
            except Exception:
                continue
            meta["image"] = f"/img?g={d.name}&f={js.stem}.png"
            meta["stem"] = js.stem
            for fr in meta.get("frames", []):
                fr["url"] = f"/img?g={d.name}&f={js.stem}/{fr['file']}"
            picks.append(meta)
        if picks:
            games.append({"game": d.name, "picks": picks})
    return games


PAGE = r"""<!doctype html>
<meta charset="utf-8">
<title>판정 근거</title>
<style>
  body { margin:0; background:#0e1016; color:#e6e9ef;
         font:14px "Malgun Gothic", system-ui, sans-serif; }
  header { position:sticky; top:0; z-index:5; background:#0e1016ee;
           border-bottom:1px solid #232733; padding:10px 16px;
           display:flex; gap:16px; align-items:center; backdrop-filter:blur(4px); }
  h1 { font-size:15px; margin:0; font-weight:600; }
  .k { color:#8b93a7; font-size:13px; }
  label { display:flex; gap:5px; align-items:center; color:#8b93a7; font-size:13px; }
  section { padding:14px 16px 4px; }
  h2 { font-size:14px; margin:0 0 10px; color:#9fb0d0; font-weight:600;
       border-left:3px solid #3a4a66; padding-left:8px; }
  .pick { border:1px solid #232733; border-radius:6px; margin-bottom:14px;
          overflow:hidden; background:#12151d; }
  .bar { display:flex; gap:14px; align-items:baseline; padding:8px 12px;
         background:#171b25; flex-wrap:wrap; }
  .lv { color:#8b93a7; font-variant-numeric:tabular-nums; }
  .nm { font-weight:600; }
  .via { color:#9ef5c0; font-size:13px; }
  .sc { color:#8b93a7; font-size:12px; font-variant-numeric:tabular-nums; }
  .rar-silver{color:#b8c4d0} .rar-gold{color:#e8c07a} .rar-prismatic{color:#d9a8e8}
  .cards { padding:6px 12px; color:#8b93a7; font-size:12.5px; }
  .cards b { color:#c8d0de; font-weight:600; }
  .cards .sel { color:#9ef5c0; }
  .rr { padding:0 12px 8px; color:#6f7783; font-size:12px; }
  img.strip { display:block; width:100%; cursor:zoom-in; }
  .player { display:flex; gap:12px; padding:10px 12px; align-items:flex-start;
            flex-wrap:wrap; }
  .stage { position:relative; background:#000; border:1px solid #232733;
           border-radius:4px; overflow:hidden; flex:none; }
  .stage img { display:block; max-width:min(560px, 60vw); height:auto; }
  .stage .mark { position:absolute; inset:0; border:3px solid transparent;
                 pointer-events:none; }
  .stage.picked .mark { border-color:#43d17f; }
  .stage.dead img { filter:grayscale(.7) brightness(.6); }
  .ctl { display:flex; flex-direction:column; gap:7px; min-width:230px; flex:1; }
  .ctl .line { display:flex; gap:9px; align-items:center; flex-wrap:wrap; }
  .ctl button, .ctl select { background:#232a38; color:#e6e9ef;
      border:1px solid #333d50; border-radius:4px; padding:3px 9px; font:inherit;
      font-size:13px; cursor:pointer; }
  .ctl input[type=range] { flex:1; min-width:130px; }
  .read { font-variant-numeric:tabular-nums; font-size:13px; color:#c8d0de; }
  .read .dim { color:#8b93a7; }
  .read .hit { color:#9ef5c0; font-weight:600; }
  .read .dead { color:#7c8496; }
  details { border-top:1px solid #232733; }
  summary { padding:6px 12px; color:#8b93a7; font-size:12.5px; cursor:pointer; }
  #empty { padding:40px 16px; color:#6f7783; }
  dialog { background:#0e1016; border:1px solid #333d50; padding:0; max-width:98vw; }
  dialog img { width:auto; max-width:96vw; cursor:zoom-out; }
</style>
<header>
  <h1>판정 근거</h1>
  <span class="k" id="sum">–</span>
  <label><input type="checkbox" id="auto" checked> 자동 갱신</label>
  <button id="now" style="background:#232a38;color:#e6e9ef;border:1px solid #333d50;
    border-radius:4px;padding:3px 9px;font:inherit;cursor:pointer">지금 갱신</button>
</header>
<div id="body"><div id="empty">아직 기록된 판정이 없습니다. 증강을 고르면 여기에 쌓입니다.</div></div>
<dialog id="zoom"><img id="zimg"></dialog>
<script>
let lastKey = "";
const body = document.getElementById("body");

function esc(s){ return String(s ?? "").replace(/[<>&]/g, c => ({'<':'&lt;','>':'&gt;','&':'&amp;'}[c])); }

function render(games){
  const key = JSON.stringify(games.map(g => [g.game, g.picks.map(p => p.stem)]));
  if (key === lastKey) return;
  lastKey = key;
  if (!games.length) { body.innerHTML = '<div id="empty">아직 기록된 판정이 없습니다.</div>'; return; }
  let n = 0;
  body.innerHTML = games.map(g => {
    n += g.picks.length;
    const items = g.picks.map(p => {
      const cards = Object.entries(p.cards || {}).map(([s, nm]) =>
        `<span class="${s === p.slot ? 'sel' : ''}"><b>${s}</b> ${esc(nm)}</span>`).join(" &nbsp;·&nbsp; ");
      const rr = (p.rerolls || []).map(r => `${r[0]}: ${esc(r[1])} → ${esc(r[2])}`).join(" &nbsp;|&nbsp; ");
      return `<div class="pick">
        <div class="bar">
          <span class="lv">${p.at} · lv${p.level}</span>
          <span class="nm rar-${esc(p.rarity)}">${esc(p.name)}</span>
          <span class="via">${esc(p.slot)} 카드 · ${esc(p.via)}</span>
          <span class="sc">일치도 ${p.score} · OCR '${esc(p.ocr)}'</span>
        </div>
        <div class="cards">${cards}</div>
        ${rr ? `<div class="rr">리롤 ${rr}</div>` : ""}
        ${(p.frames && p.frames.length) ? `
        <div class="player" data-frames='${esc(JSON.stringify(p.frames))}'>
          <div class="stage"><img alt="프레임"><div class="mark"></div></div>
          <div class="ctl">
            <div class="line">
              <button class="play">▶ 재생</button>
              <select class="spd">
                <option value="0.25">0.25×</option>
                <option value="0.5">0.5×</option>
                <option value="1" selected>1× (실시간)</option>
                <option value="2">2×</option>
              </select>
              <button class="prev">◀</button><button class="next">▶</button>
              <button class="jump">번쩍임으로</button>
            </div>
            <input type="range" class="seek" min="0" value="0" step="1">
            <div class="read"></div>
          </div>
        </div>` : ""}
        <details><summary>전체 프레임 한 장으로 보기</summary>
          <img class="strip" loading="lazy" src="${p.image}" alt="판정 근거"></details>
      </div>`;
    }).join("");
    return `<section><h2>게임 ${g.game.replace(/^(\d{4})(\d\d)(\d\d)-/, "$2/$3 ")}
            · 픽 ${g.picks.length}건</h2>${items}</section>`;
  }).join("");
  document.getElementById("sum").textContent =
    `게임 ${games.length}판 · 판정 ${n}건`;
  for (const im of body.querySelectorAll("img.strip")) im.onclick = () => {
    document.getElementById("zimg").src = im.src;
    document.getElementById("zoom").showModal();
  };
  for (const el of body.querySelectorAll(".player")) setupPlayer(el);
}

function setupPlayer(el){
  const frames = JSON.parse(el.dataset.frames);
  const stage = el.querySelector(".stage"), img = stage.querySelector("img");
  const seek = el.querySelector(".seek"), read = el.querySelector(".read");
  const play = el.querySelector(".play"), spd = el.querySelector(".spd");
  seek.max = frames.length - 1;
  let i = 0, timer = null;
  const pickedIdx = frames.findIndex(f => f.picked);

  function show(k){
    i = Math.max(0, Math.min(frames.length - 1, k));
    const f = frames[i];
    img.src = f.url;
    seek.value = i;
    stage.classList.toggle("picked", !!f.picked);
    stage.classList.toggle("dead", !f.alive);
    const cls = f.picked ? "hit" : (f.alive ? "" : "dead");
    read.innerHTML =
      `<span class="${cls}">${i + 1}/${frames.length} · 종료 ${f.dt.toFixed(2)}초 전 ·
       ${f.alive ? "창 살아있음" : "창 사라짐"}</span><br>
       <span class="dim">L</span> ${f.means.L}
       <span class="dim">M</span> ${f.means.M}
       <span class="dim">R</span> ${f.means.R}
       &nbsp; <span class="${cls}">${f.top} ×${f.ratio.toFixed(2)}</span>
       ${f.picked ? " ← 이 프레임으로 판정" : ""}`;
  }
  function stop(){ clearInterval(timer); timer = null; play.textContent = "▶ 재생"; }
  function start(){
    stop();
    // 프레임 간격은 검출 루프 주기 그대로 -- 1× 가 실제 속도
    const gap = frames.length > 1
      ? Math.abs(frames[1].dt - frames[0].dt) * 1000 / (+spd.value) : 100;
    timer = setInterval(() => {
      show(i + 1 >= frames.length ? 0 : i + 1);
    }, Math.max(30, gap));
    play.textContent = "❚❚ 정지";
  }
  play.onclick = () => timer ? stop() : start();
  spd.onchange = () => { if (timer) start(); };
  seek.oninput = () => { stop(); show(+seek.value); };
  el.querySelector(".prev").onclick = () => { stop(); show(i - 1); };
  el.querySelector(".next").onclick = () => { stop(); show(i + 1); };
  el.querySelector(".jump").onclick = () => {
    stop(); show(pickedIdx >= 0 ? pickedIdx : 0);
  };
  show(pickedIdx >= 0 ? pickedIdx : 0);
}
document.getElementById("zoom").onclick = e => e.currentTarget.close();

async function tick(){
  try { render(await (await fetch("/index.json", {cache:"no-store"})).json()); }
  catch (e) { document.getElementById("sum").textContent = "오버레이 연결 끊김"; }
}
document.getElementById("now").onclick = () => { lastKey = ""; tick(); };
tick();
setInterval(() => { if (document.getElementById("auto").checked) tick(); }, 3000);
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
        if u.path == "/index.json":
            self._send(json.dumps(index(), ensure_ascii=False).encode(),
                       "application/json; charset=utf-8")
        elif u.path == "/img":
            q = parse_qs(u.query)
            g, f = unquote((q.get("g") or [""])[0]), unquote((q.get("f") or [""])[0])
            p = (ROOT / g / f).resolve()
            ctype = "image/jpeg" if p.suffix.lower() in (".jpg", ".jpeg") else "image/png"
            # stay inside the decisions folder no matter what the query says
            if ROOT.resolve() not in p.parents or not p.exists():
                self.send_error(404)
                return
            self._send(p.read_bytes(), ctype)
        else:
            self._send(PAGE.encode("utf-8"), "text/html; charset=utf-8")


def main() -> int:
    print(f"판정 근거 뷰어: http://127.0.0.1:{PORT}/   (Ctrl+C 로 종료)")
    print(f"  읽는 곳: {ROOT}")
    try:
        ThreadingHTTPServer(("127.0.0.1", PORT), H).serve_forever()
    except KeyboardInterrupt:
        pass
    return 0


if __name__ == "__main__":
    sys.exit(main())
