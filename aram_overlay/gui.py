"""Status window, settings tab and tray icon around the detection loop.

A console window scrolling debug lines reads as something gone wrong to anyone
who did not write it, and closing it was the only visible way to stop the tool.
This wraps the same loop in a window: connection state, the augments picked so
far, the last few log lines, and the settings that used to mean editing Python.

The loop is not modified by any of this. It runs unchanged on a worker thread
and is observed through the three hooks in `__main__` -- LOG_SINKS, RUNTIME and
STOP -- so `python -m aram_overlay` from a terminal still behaves exactly as it
did. Tk owns the main thread because it must; the tray icon runs on its own and
marshals every callback back with `after`.

Settings apply by restarting that worker rather than being live-patched: the
language decides which data file is loaded and the resolution decides every box
coordinate, and both are read once during startup.
"""
from __future__ import annotations

import queue
import socket
import sys
import threading
import tkinter as tk
import webbrowser
from tkinter import ttk

from . import __main__ as core
from . import config, icon, settings


def _installed_ocr_languages() -> list[str]:
    """Imported here rather than at module scope: a broken winrt install should
    surface in the log with everything else, not stop the window from opening."""
    try:
        from . import ocr
        return ocr.installed_languages()
    except Exception:
        return []

# A second launch should raise the window that is already running, not start a
# rival that then fails to bind the widget port. The listening socket doubles as
# the single-instance lock, so there is no lock file to go stale.
SIGNAL_PORT = 8778

BG      = "#12161d"
PANEL   = "#171c25"
FIELD   = "#1e2530"
FG      = "#e8edf3"
DIM     = "#8a94a3"
LINE    = "#232b36"
OK      = "#4ec9a0"
WAIT    = "#8a94a3"
BAD     = "#e05561"
WARN    = "#e8c07a"
RARITY  = {"silver": "#b8c4d0", "gold": "#e8c07a",
           "prismatic": "#d9a8e8", "unknown": "#8a8f98"}
KO      = {"silver": "실버", "gold": "골드", "prismatic": "프리즘", "unknown": "미상"}

LOG_ROWS = 6
POLL_MS = 400
PRESETS = ["1920 × 1080", "2560 × 1440", "3840 × 2160", "1600 × 900", "1280 × 720"]
UI = ("Malgun Gothic", 9)


def _detect_screen() -> tuple[int, int, int]:
    """Physical pixels and the display scaling, read without making this process
    DPI-aware -- doing that after Tk starts leaves the window itself mis-scaled."""
    try:
        import ctypes
        u, g = ctypes.windll.user32, ctypes.windll.gdi32
        hdc = u.GetDC(0)
        phys_w = g.GetDeviceCaps(hdc, 118)      # DESKTOPHORZRES
        phys_h = g.GetDeviceCaps(hdc, 117)      # DESKTOPVERTRES
        u.ReleaseDC(0, hdc)
        logical = u.GetSystemMetrics(0)
        return phys_w, phys_h, round(phys_w / logical * 100) if logical else 100
    except Exception:
        return 0, 0, 100


class App:
    def __init__(self, argv: list[str], lock: socket.socket | None = None):
        self.argv = argv
        self.q: queue.Queue = queue.Queue()
        self.worker: threading.Thread | None = None
        self.exit_code: int | None = None
        self.tray = None
        self.last_key = None
        self.told_about_tray = False
        self.values = settings.load()

        self.root = tk.Tk()
        self.root.title("아수라장 증강 오버레이")
        self.root.configure(bg=BG)
        self.root.geometry("400x640")
        self.root.minsize(380, 560)
        ico = icon.ensure_ico(config.STATE / "icon.ico")
        if ico:
            try:
                self.root.iconbitmap(default=str(ico))
            except Exception:
                pass
        self._style()
        self._build()

        core.LOG_SINKS.append(self.q.put)
        self.root.protocol("WM_DELETE_WINDOW", self.hide)
        self._start_worker()
        self._start_tray()
        self._serve_signal(lock)
        self.root.after(POLL_MS, self._poll)

    # ---------------------------------------------------------------- layout
    def _style(self):
        st = ttk.Style()
        st.theme_use("clam")                 # the only built-in theme that recolours
        st.configure("TNotebook", background=BG, borderwidth=0)
        st.configure("TNotebook.Tab", background=BG, foreground=DIM,
                     padding=(14, 6), font=UI, borderwidth=0)
        st.map("TNotebook.Tab", background=[("selected", PANEL)],
               foreground=[("selected", FG)])
        st.configure("TCombobox", fieldbackground=FIELD, background=FIELD,
                     foreground=FG, arrowcolor=DIM, bordercolor=LINE,
                     lightcolor=FIELD, darkcolor=FIELD, selectbackground=FIELD,
                     selectforeground=FG)
        # A readonly combobox draws its value as selected text, which lands as
        # black on the system highlight colour unless every state is pinned.
        st.map("TCombobox",
               fieldbackground=[("readonly", FIELD)], background=[("readonly", FIELD)],
               foreground=[("readonly", FG)], arrowcolor=[("readonly", DIM)],
               selectbackground=[("readonly", FIELD)], selectforeground=[("readonly", FG)])
        self.root.option_add("*TCombobox*Listbox.background", FIELD)
        self.root.option_add("*TCombobox*Listbox.foreground", FG)
        self.root.option_add("*TCombobox*Listbox.selectBackground", LINE)

    def _build(self):
        self.nb = nb = ttk.Notebook(self.root)
        nb.pack(fill="both", expand=True)
        self.tab_status = tk.Frame(nb, bg=BG)
        self.tab_config = tk.Frame(nb, bg=BG)
        nb.add(self.tab_status, text="상태")
        nb.add(self.tab_config, text="설정")
        self._build_status(self.tab_status)
        self._build_config(self.tab_config)

    def _build_status(self, root):
        head = tk.Frame(root, bg=BG)
        head.pack(fill="x", padx=14, pady=(14, 8))
        self.dot_obs = tk.Label(head, text="●", fg=WAIT, bg=BG, font=("Segoe UI", 10))
        self.dot_obs.pack(side="left")
        self.lbl_obs = tk.Label(head, text="시작하는 중", fg=FG, bg=BG, font=UI)
        self.lbl_obs.pack(side="left", padx=(4, 14))
        self.dot_game = tk.Label(head, text="●", fg=WAIT, bg=BG, font=("Segoe UI", 10))
        self.dot_game.pack(side="left")
        self.lbl_game = tk.Label(head, text="게임 대기 중", fg=FG, bg=BG, font=UI)
        self.lbl_game.pack(side="left", padx=4)

        self.lbl_count = tk.Label(root, text="획득한 증강 0개", fg=DIM, bg=BG,
                                  font=UI, anchor="w")
        self.lbl_count.pack(fill="x", padx=14)

        self.picks = tk.Frame(root, bg=PANEL, highlightthickness=1,
                              highlightbackground=LINE)
        self.picks.pack(fill="both", expand=True, padx=14, pady=(6, 10))

        btns = tk.Frame(root, bg=BG)
        btns.pack(fill="x", padx=14)
        self.btn_reset = self._button(btns, "목록 초기화", self.reset)
        self.btn_reset.pack(side="left")
        self.btn_retry = self._button(btns, "다시 시도", self.restart)
        self._button(btns, "종료", self.quit).pack(side="right")

        self.log = tk.Text(root, height=LOG_ROWS, bg=PANEL, fg=DIM, bd=0,
                           font=("Consolas", 8), state="disabled", wrap="none",
                           highlightthickness=1, highlightbackground=LINE)
        self.log.pack(fill="x", padx=14, pady=(10, 12))

    def _build_config(self, root):
        wrap = tk.Frame(root, bg=BG)
        wrap.pack(fill="both", expand=True, padx=14, pady=12)

        # --- language
        self._section(wrap, "언어")
        locales = list(settings.LOCALES.items())
        self._locale_labels = [f"{name}  ({code})" for code, name in locales]
        self._locale_codes = [code for code, _ in locales]
        cur = self.values["locale"]
        self.var_locale = tk.StringVar(
            value=self._locale_labels[self._locale_codes.index(cur)]
            if cur in self._locale_codes else self._locale_labels[0])
        row = self._row(wrap, "증강 이름")
        ttk.Combobox(row, values=self._locale_labels, textvariable=self.var_locale,
                     state="readonly", font=UI, width=22).pack(side="left")

        installed = _installed_ocr_languages()
        self.var_ocr = tk.StringVar(value=self.values["ocr_language"] or "자동")
        row = self._row(wrap, "OCR 언어")
        ttk.Combobox(row, values=["자동"] + installed, textvariable=self.var_ocr,
                     state="readonly", font=UI, width=22).pack(side="left")
        self.lbl_ocr = tk.Label(wrap, text="", fg=DIM, bg=BG,
                                font=("Malgun Gothic", 8), anchor="w", justify="left")
        self.lbl_ocr.pack(fill="x", pady=(0, 8))
        self.var_locale.trace_add("write", lambda *_: self._check_ocr(installed))
        self.var_ocr.trace_add("write", lambda *_: self._check_ocr(installed))

        # --- resolution
        self._section(wrap, "해상도")
        pw, ph, scale = _detect_screen()
        note = f"이 PC 화면: {pw}×{ph}, 배율 {scale}%" if pw else "화면 정보를 읽지 못했습니다"
        tk.Label(wrap, text=note, fg=DIM, bg=BG, font=("Malgun Gothic", 8),
                 anchor="w").pack(fill="x")
        res = tk.Frame(wrap, bg=BG)
        res.pack(fill="x", pady=(4, 2))
        tk.Label(res, text="게임 해상도", fg=FG, bg=BG, font=UI, width=11,
                 anchor="w").pack(side="left")
        self.var_w = tk.StringVar(value=str(self.values["screen_width"]))
        self.var_h = tk.StringVar(value=str(self.values["screen_height"]))
        self._entry(res, self.var_w, 6).pack(side="left")
        tk.Label(res, text="×", fg=DIM, bg=BG, font=UI).pack(side="left", padx=4)
        self._entry(res, self.var_h, 6).pack(side="left")
        self.var_preset = tk.StringVar(value="")
        preset = ttk.Combobox(res, values=PRESETS, textvariable=self.var_preset,
                              state="readonly", font=UI, width=12)
        preset.pack(side="left", padx=8)
        preset.bind("<<ComboboxSelected>>", self._use_preset)
        self.lbl_res = tk.Label(wrap, text="", fg=DIM, bg=BG, anchor="w",
                                font=("Malgun Gothic", 8), justify="left", wraplength=340)
        self.lbl_res.pack(fill="x", pady=(2, 8))
        self.var_w.trace_add("write", lambda *_: self._check_res())
        self.var_h.trace_add("write", lambda *_: self._check_res())

        # --- OBS and widget
        self._section(wrap, "OBS")
        self.var_obs_port = self._field(wrap, "websocket 포트", "obs_port", 8)
        self.var_obs_source = self._field(wrap, "게임 캡처 소스", "obs_source", 22)
        self.var_obs_pw = self._field(wrap, "비밀번호", "obs_password", 22, secret=True)
        tk.Label(wrap, text="비워 두면 OBS 설정 파일에서 자동으로 읽습니다.",
                 fg=DIM, bg=BG, font=("Malgun Gothic", 8), anchor="w").pack(fill="x")

        self._section(wrap, "오버레이")
        self.var_widget_port = self._field(wrap, "위젯 포트", "widget_port", 8)
        self.var_rows = self._field(wrap, "표시 행 수", "widget_rows", 8)
        self.var_maxw = self._field(wrap, "최대 폭 (px)", "widget_max_width", 8)

        self.lbl_saved = tk.Label(wrap, text="", fg=OK, bg=BG, font=("Malgun Gothic", 8),
                                  anchor="w")
        self.lbl_saved.pack(fill="x", pady=(10, 2))
        bar = tk.Frame(wrap, bg=BG)
        bar.pack(fill="x")
        self._button(bar, "저장하고 다시 시작", self.save_settings).pack(side="left")
        self._button(bar, "기본값", self.reset_settings).pack(side="left", padx=8)

        self._check_ocr(installed)
        self._check_res()

    def _section(self, parent, text):
        tk.Label(parent, text=text, fg=WARN, bg=BG, anchor="w",
                 font=("Malgun Gothic", 9, "bold")).pack(fill="x", pady=(8, 4))

    def _row(self, parent, label):
        row = tk.Frame(parent, bg=BG)
        row.pack(fill="x", pady=2)
        tk.Label(row, text=label, fg=FG, bg=BG, font=UI, width=11,
                 anchor="w").pack(side="left")
        return row

    def _entry(self, parent, var, width, secret=False):
        return tk.Entry(parent, textvariable=var, width=width, bg=FIELD, fg=FG,
                        insertbackground=FG, relief="flat", font=UI,
                        show="•" if secret else "",
                        highlightthickness=1, highlightbackground=LINE,
                        highlightcolor=WARN)

    def _field(self, parent, label, key, width, secret=False):
        var = tk.StringVar(value=str(self.values[key]))
        row = tk.Frame(parent, bg=BG)
        row.pack(fill="x", pady=2)
        tk.Label(row, text=label, fg=FG, bg=BG, font=UI, width=13,
                 anchor="w").pack(side="left")
        self._entry(row, var, width, secret).pack(side="left")
        return var

    def _button(self, parent, text, cmd):
        return tk.Button(parent, text=text, command=cmd, bg=PANEL, fg=FG,
                         activebackground=LINE, activeforeground=FG, font=UI,
                         bd=0, padx=12, pady=6, cursor="hand2", relief="flat")

    # ------------------------------------------------------------- settings
    def _use_preset(self, _event=None):
        try:
            w, h = self.var_preset.get().replace(" ", "").split("×")
        except ValueError:
            return
        self.var_w.set(w)
        self.var_h.set(h)

    def _check_res(self):
        try:
            w, h = int(self.var_w.get()), int(self.var_h.get())
        except ValueError:
            self.lbl_res.config(text="숫자를 입력하세요.", fg=BAD)
            return
        if settings.is_supported_shape(w, h):
            self.lbl_res.config(fg=DIM, text="16:9 — 좌표가 비율대로 환산되어 그대로 동작합니다.")
        else:
            self.lbl_res.config(
                fg=WARN,
                text="16:9가 아닙니다. 클라이언트가 HUD를 다르게 배치하므로 카드 위치가 "
                     "어긋날 수 있습니다. scripts\\box_editor.py 로 상자를 다시 그리세요.")

    def _check_ocr(self, installed: list[str]):
        chosen = self.var_ocr.get()
        if chosen != "자동":
            self.lbl_ocr.config(fg=DIM, text=f"'{chosen}' 로 읽습니다.")
            return
        code = self._locale_codes[self._locale_labels.index(self.var_locale.get())]
        tags = settings.OCR_FOR_LOCALE.get(code, ())
        have = [t for t in tags if t in installed]
        if have:
            self.lbl_ocr.config(fg=DIM, text=f"'{have[0]}' 로 읽습니다.")
        else:
            want = tags[0] if tags else "?"
            self.lbl_ocr.config(
                fg=BAD,
                text=f"'{want}' OCR 언어 팩이 없습니다. 관리자 PowerShell에서\n"
                     f"Add-WindowsCapability -Online -Name 'Language.OCR~~~{want}~0.0.1.0'")

    def collect(self) -> dict | None:
        code = self._locale_codes[self._locale_labels.index(self.var_locale.get())]
        ocr_tag = "" if self.var_ocr.get() == "자동" else self.var_ocr.get()
        try:
            return {
                "locale": code,
                "ocr_language": ocr_tag,
                "screen_width": int(self.var_w.get()),
                "screen_height": int(self.var_h.get()),
                "obs_port": int(self.var_obs_port.get()),
                "obs_password": self.var_obs_pw.get().strip(),
                "obs_source": self.var_obs_source.get().strip(),
                "widget_port": int(self.var_widget_port.get()),
                "widget_rows": int(self.var_rows.get()),
                "widget_max_width": int(self.var_maxw.get()),
            }
        except ValueError:
            self.lbl_saved.config(fg=BAD, text="숫자 칸에 숫자가 아닌 값이 있습니다.")
            return None

    def save_settings(self):
        values = self.collect()
        if values is None:
            return
        self.values = settings.save(values)
        self.lbl_saved.config(fg=OK, text="저장했습니다. 다시 시작합니다...")
        self._append("설정을 저장했습니다. 다시 시작합니다...")
        self.restart()

    def reset_settings(self):
        self.values = settings.save(dict(settings.DEFAULTS))
        self.lbl_saved.config(fg=OK, text="기본값으로 되돌렸습니다. 창을 다시 열면 반영됩니다.")
        self._append("설정을 기본값으로 되돌렸습니다.")
        self.restart()

    # ---------------------------------------------------------------- worker
    def _start_worker(self):
        core.STOP.clear()
        self.exit_code = None
        self.btn_retry.pack_forget()
        self.worker = threading.Thread(target=self._run_core, daemon=True)
        self.worker.start()

    def _run_core(self):
        try:
            code = core.main(self.argv)
        except Exception as exc:                      # noqa: BLE001 - shown in the log
            core.log(f"오류로 중단됐습니다: {exc}")
            code = 1
        self.q.put(("__exit__", code))

    def restart(self, tries: int = 0):
        """Stop the loop if it is running, then start it again once it is gone."""
        if self.worker and self.worker.is_alive():
            core.STOP.set()
            if tries < 20:
                self.root.after(300, self.restart, tries + 1)
                return
        server = core.RUNTIME.get("server")
        if server:
            try:
                server.stop()
            except Exception:
                pass
        core.RUNTIME.clear()
        self.last_key = None
        self._start_worker()

    # ---------------------------------------------------------------- actions
    def reset(self):
        state, server = core.RUNTIME.get("state"), core.RUNTIME.get("server")
        if not state:
            return
        state.picks.clear()
        if server:
            server.save()
        self.last_key = None
        self._append("목록을 비웠습니다.")

    def open_widget(self):
        url = core.RUNTIME.get("url")
        if url:
            webbrowser.open(url)

    def copy_url(self):
        url = core.RUNTIME.get("url")
        if not url:
            return
        self.root.clipboard_clear()
        self.root.clipboard_append(url)
        self._append(f"위젯 주소를 복사했습니다: {url}")

    def show(self):
        self.root.deiconify()
        self.root.lift()
        self.root.focus_force()

    def hide(self):
        """✕ leaves it running in the tray -- closing the window used to be the
        only way to stop the tool, which is precisely the confusion to avoid.

        Windows files a new tray icon under the overflow chevron, so the first
        time this happens it says where the window went; otherwise it just looks
        like the program quit."""
        if not self.tray:
            self.quit()
            return
        self.root.withdraw()
        if not self.told_about_tray:
            self.told_about_tray = True
            try:
                self.tray.notify("트레이에서 계속 실행 중입니다. "
                                 "아이콘을 두 번 누르면 다시 열립니다.",
                                 "아수라장 증강 오버레이")
            except Exception:
                pass

    def quit(self):
        core.STOP.set()
        if self.tray:
            try:
                self.tray.stop()
            except Exception:
                pass
        # The loop can be mid-sleep; give it a moment to save and close the
        # server, then go regardless so the window never hangs on exit.
        self.root.after(300, self._finish, 0)

    def _finish(self, tries: int):
        if self.worker and self.worker.is_alive() and tries < 12:
            self.root.after(300, self._finish, tries + 1)
            return
        server = core.RUNTIME.get("server")
        if server:
            try:
                server.stop()
            except Exception:
                pass
        self.root.destroy()

    # -------------------------------------------------------- single instance
    def _serve_signal(self, lock: socket.socket | None):
        if lock is None:
            return

        def serve():
            while True:
                try:
                    conn, _ = lock.accept()
                except OSError:
                    return
                try:
                    conn.settimeout(1.0)
                    cmd = conn.recv(16)
                except OSError:
                    cmd = b""
                conn.close()
                self.root.after(0, self.quit if cmd.startswith(b"quit") else self.show)

        threading.Thread(target=serve, daemon=True).start()

    # ------------------------------------------------------------------ tray
    def _start_tray(self):
        try:
            import pystray
        except Exception:
            self._append("pystray가 없어 트레이 아이콘 없이 실행합니다.")
            return
        call = lambda fn: (lambda *_: self.root.after(0, fn))   # noqa: E731
        menu = pystray.Menu(
            pystray.MenuItem("열기", call(self.show), default=True),
            pystray.MenuItem("위젯 주소 복사", call(self.copy_url)),
            pystray.MenuItem("목록 초기화", call(self.reset)),
            pystray.Menu.SEPARATOR,
            pystray.MenuItem("종료", call(self.quit)),
        )
        self.tray = pystray.Icon("aram_overlay", icon.image(64),
                                 "아수라장 증강 오버레이", menu)
        threading.Thread(target=self.tray.run, daemon=True).start()

    # ------------------------------------------------------------------ poll
    def _poll(self):
        while True:
            try:
                item = self.q.get_nowait()
            except queue.Empty:
                break
            if isinstance(item, tuple) and item and item[0] == "__exit__":
                self.exit_code = item[1]
            else:
                self._append(item)
        self._refresh()
        self.root.after(POLL_MS, self._poll)

    def _append(self, line: str):
        self.log.configure(state="normal")
        self.log.insert("end", line + "\n")
        self.log.delete("1.0", f"end-{LOG_ROWS * 3}l")
        self.log.see("end")
        self.log.configure(state="disabled")

    def _refresh(self):
        alive = bool(self.worker and self.worker.is_alive())
        state = core.RUNTIME.get("state")
        running = alive and state is not None

        if running:
            self.dot_obs.config(fg=OK)
            self.lbl_obs.config(text="OBS 연결됨")
        elif alive:
            self.dot_obs.config(fg=WAIT)
            self.lbl_obs.config(text="시작하는 중")
        else:
            self.dot_obs.config(fg=BAD)
            self.lbl_obs.config(text="OBS 연결 안 됨")
            if not self.btn_retry.winfo_ismapped():
                self.btn_retry.pack(side="left", padx=8)

        if state is not None and state.connected:
            self.dot_game.config(fg=OK)
            if state.game_mode == config.MAYHEM_GAME_MODE:
                lv = f" (Lv {state.level})" if state.level else ""
                self.lbl_game.config(text=f"게임 감지{lv}")
            else:
                self.lbl_game.config(text=f"{state.game_mode} — 감지 안 함")
        else:
            self.dot_game.config(fg=WAIT)
            self.lbl_game.config(text="게임 대기 중")

        picks = list(state.picks) if state is not None else []
        key = [(p.name, p.rarity, p.level) for p in picks]
        if key == self.last_key:
            return
        self.last_key = key
        for child in self.picks.winfo_children():
            child.destroy()
        self.lbl_count.config(text=f"획득한 증강 {len(picks)}개")
        if not picks:
            tk.Label(self.picks, text="아직 없습니다", fg=DIM, bg=PANEL,
                     font=UI).pack(pady=18)
            return
        for p in picks:
            colour = RARITY.get(p.rarity, RARITY["unknown"])
            row = tk.Frame(self.picks, bg=PANEL)
            row.pack(fill="x", padx=10, pady=4)
            tk.Frame(row, bg=colour, width=3, height=26).pack(side="left", fill="y")
            tk.Label(row, text=p.name, fg=FG, bg=PANEL, anchor="w",
                     font=("Malgun Gothic", 10, "bold")).pack(side="left", padx=8)
            sub = KO.get(p.rarity, KO["unknown"])
            if p.level:
                sub += f" · {p.level}레벨"
            tk.Label(row, text=sub, fg=colour, bg=PANEL, anchor="e",
                     font=("Malgun Gothic", 8)).pack(side="right")


def signal_running(command: bytes) -> bool:
    """Send one word to the instance already running. False if there is none."""
    try:
        with socket.create_connection(("127.0.0.1", SIGNAL_PORT), timeout=2) as s:
            s.sendall(command)
        return True
    except OSError:
        return False


def stop_running() -> bool:
    """Ask a running overlay to shut down the way the Quit button does.

    Killing the process instead leaves its tray icon behind as a ghost until
    something makes Windows notice it is dead, which is what `--stop` is for.
    """
    return signal_running(b"quit")


def run(argv: list[str] | None = None) -> int:
    lock = socket.socket()
    lock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 0)
    try:
        lock.bind(("127.0.0.1", SIGNAL_PORT))
        lock.listen(1)
    except OSError:
        # Already running: ask that window to come forward and step aside.
        lock.close()
        signal_running(b"show")
        return 0

    app = App(list(argv or []), lock)
    app.root.mainloop()
    lock.close()
    return app.exit_code or 0


if __name__ == "__main__":
    sys.exit(run(sys.argv[1:]))
