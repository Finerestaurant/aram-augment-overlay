"""Local HTTP server for the OBS browser source.

Keeps the picked-augment list in memory, writes it to disk so a crash mid-game
does not lose the run, and serves both the widget page and its state.
"""
from __future__ import annotations

import json
import threading
from dataclasses import asdict, dataclass, field
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

from . import config


@dataclass
class Pick:
    name: str
    rarity: str
    icon_url: str
    level: int | None = None
    slot: str = ""            # L / M / R
    confidence: float = 0.0
    ocr_raw: str = ""


@dataclass
class RunState:
    picks: list[Pick] = field(default_factory=list)
    game_mode: str = ""
    connected: bool = False

    def to_json(self) -> str:
        return json.dumps({
            "picks": [asdict(p) for p in self.picks],
            "game_mode": self.game_mode,
            "connected": self.connected,
        }, ensure_ascii=False)


class WidgetServer:
    def __init__(self, state: RunState, host=None, port=None):
        self.state = state
        self.host = host or config.WIDGET_HOST
        self.port = port or config.WIDGET_PORT
        self._httpd = None
        self._thread = None

    def _handler(self):
        state = self.state
        page = (config.ASSETS / "widget.html").read_bytes()

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
                if self.path.startswith("/state.json"):
                    self._send(state.to_json().encode("utf-8"), "application/json; charset=utf-8")
                elif self.path in ("/", "/index.html", "/widget.html"):
                    self._send(page, "text/html; charset=utf-8")
                else:
                    self.send_error(404)
        return H

    def start(self):
        self._httpd = ThreadingHTTPServer((self.host, self.port), self._handler())
        self._thread = threading.Thread(target=self._httpd.serve_forever, daemon=True)
        self._thread.start()
        return f"http://{self.host}:{self.port}/"

    def stop(self):
        if self._httpd:
            self._httpd.shutdown()

    def save(self):
        config.STATE.mkdir(parents=True, exist_ok=True)
        (config.STATE / "run.json").write_text(self.state.to_json(), encoding="utf-8")
