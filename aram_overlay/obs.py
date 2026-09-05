"""OBS websocket access and one-time scene setup.

Note the CLI flags --websocket_port / --websocket_password only override values;
they do not switch the server on. `server_enabled` in obs-websocket's config.json
is the only thing that does, which is why setup writes that file directly.
"""
from __future__ import annotations

import base64
import io
import json
import os
from pathlib import Path

import numpy as np
from PIL import Image

from . import config

WS_CONFIG = Path(os.environ.get("APPDATA", "")) / "obs-studio" / "plugin_config" / \
    "obs-websocket" / "config.json"
OBS_USER_INI = Path(os.environ.get("APPDATA", "")) / "obs-studio" / "user.ini"


def read_obs_websocket_config() -> dict | None:
    if not WS_CONFIG.exists():
        return None
    try:
        return json.loads(WS_CONFIG.read_text(encoding="utf-8"))
    except Exception:
        return None


class ObsCapture:
    def __init__(self, host=None, port=None, password=None, source=None):
        import obsws_python as obsws
        self.source = source or config.OBS_SOURCE
        self.client = obsws.ReqClient(
            host=host or config.OBS_HOST,
            port=port or config.OBS_PORT,
            password=password or config.OBS_PASSWORD,
            timeout=20,
        )

    def version(self) -> str:
        v = self.client.get_version()
        return f"OBS {v.obs_version} / websocket {v.obs_web_socket_version}"

    def has_source(self) -> bool:
        names = {i["inputName"] for i in self.client.get_input_list().inputs}
        return self.source in names

    def ensure_game_capture(self) -> bool:
        """Create the Game Capture source if it is missing. Returns True if created."""
        if self.has_source():
            return False
        scene = self.client.get_scene_list().current_program_scene_name
        self.client.create_input(scene, self.source, "game_capture", {
            "capture_mode": "window",
            "window": "::League of Legends.exe",
            "priority": 2,               # match by executable, so borderless or windowed both hook
            "capture_cursor": True,
            "anti_cheat_hook": True,
        }, True)
        return True

    def refresh_browser_sources(self, url: str) -> list[str]:
        """Reload every browser source pointing at `url`, returning their names.

        CEF does not retry a load that failed, so a browser source that was
        showing the widget while this tool was stopped stays blank for good once
        it reloads against a dead port. Starting up is exactly when that has
        happened, so refresh on the way up rather than making the user find
        "현재 페이지 새로고침" in the source properties.
        """
        want = url.rstrip("/")
        done = []
        try:
            inputs = self.client.get_input_list().inputs
        except Exception:
            return done
        for inp in inputs:
            if inp.get("inputKind") != "browser_source":
                continue
            name = inp["inputName"]
            try:
                settings = self.client.get_input_settings(name).input_settings
                if str(settings.get("url", "")).rstrip("/") != want:
                    continue
                self.client.press_input_properties_button(name, "refreshnocache")
                done.append(name)
            except Exception:
                continue
        return done

    def resize_browser_sources(self, url: str, width: int, height: int) -> list[str]:
        """Set every browser source showing `url` to this size.

        The widget is a short list that grows as augments are taken, so a fixed
        box is either too small or mostly empty. The page measures itself and
        this follows, which keeps the source outline in OBS matching what is
        actually drawn.
        """
        want = url.rstrip("/")
        done = []
        try:
            inputs = self.client.get_input_list().inputs
        except Exception:
            return done
        for inp in inputs:
            if inp.get("inputKind") != "browser_source":
                continue
            name = inp["inputName"]
            try:
                cur = self.client.get_input_settings(name).input_settings
                if str(cur.get("url", "")).rstrip("/") != want:
                    continue
                if (cur.get("width"), cur.get("height")) == (width, height):
                    continue
                self.client.set_input_settings(
                    name, {"width": width, "height": height}, True)
                done.append(name)
            except Exception:
                continue
        return done

    def grab(self, width=None, height=None, quality=None, fmt="jpg") -> Image.Image | None:
        """One frame from the game capture source.

        Returns None while the game is not hooked: the source then has zero size
        and obs-websocket answers 402 rather than handing back a black frame, so
        "not attached yet" is distinguishable from "attached but black".
        """
        try:
            raw = self.client.get_source_screenshot(
                self.source, fmt,
                width or config.DET_W, height or config.DET_H,
                quality if quality is not None else config.DET_QUALITY,
            ).image_data
        except Exception:
            return None
        b64 = raw.split(",", 1)[1] if raw.startswith("data:") else raw
        return Image.open(io.BytesIO(base64.b64decode(b64))).convert("RGB")

    def grab_cv(self, **kw) -> np.ndarray | None:
        im = self.grab(**kw)
        if im is None:
            return None
        return np.array(im)[:, :, ::-1].copy()      # RGB -> BGR for OpenCV
