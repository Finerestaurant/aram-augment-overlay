"""Turn on OBS's websocket server and add the Game Capture source.

Everything here is doable without clicking through OBS, with one exception: the
first-run Auto-Configuration Wizard is modal and blocks OBS from writing its
config at all, so OBS has to be started and that dialog dismissed once by hand
before this can do anything.

The websocket CLI flags are a trap worth knowing about: --websocket_port and
--websocket_password only override values, they never switch the server on.
`server_enabled` in obs-websocket's own config.json is the only switch, and OBS
must be closed while it is edited or it will overwrite the file on exit.

    python scripts/setup_obs.py            # report current state
    python scripts/setup_obs.py --enable   # enable websocket (OBS must be closed)
"""
from __future__ import annotations

import argparse
import json
import os
import sys
from pathlib import Path

APPDATA = Path(os.environ.get("APPDATA", ""))
WS_CONFIG = APPDATA / "obs-studio" / "plugin_config" / "obs-websocket" / "config.json"
USER_INI = APPDATA / "obs-studio" / "user.ini"


def obs_running() -> bool:
    try:
        import subprocess
        out = subprocess.run(["tasklist", "/FI", "IMAGENAME eq obs64.exe"],
                             capture_output=True, text=True, timeout=20).stdout
        return "obs64.exe" in out
    except Exception:
        return False


def main(argv=None) -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--enable", action="store_true", help="websocket 서버를 켭니다")
    ap.add_argument("--port", type=int, default=4455)
    args = ap.parse_args(argv)

    if not (APPDATA / "obs-studio").exists():
        print("[!] OBS 설정 폴더가 없습니다.")
        print("    OBS를 한 번 실행하고 '자동 구성 마법사'를 닫은 뒤 다시 시도하세요.")
        print("    (마법사가 떠 있는 동안에는 OBS가 설정 파일을 저장하지 않습니다.)")
        return 1

    if not WS_CONFIG.exists():
        print(f"[!] {WS_CONFIG} 가 없습니다. OBS를 한 번 실행한 뒤 다시 시도하세요.")
        return 1

    cfg = json.loads(WS_CONFIG.read_text(encoding="utf-8"))
    print(f"현재 상태: server_enabled={cfg.get('server_enabled')} "
          f"port={cfg.get('server_port')} auth_required={cfg.get('auth_required')}")
    print(f"비밀번호  : {cfg.get('server_password')}")

    if not args.enable:
        if not cfg.get("server_enabled"):
            print("\nwebsocket 서버가 꺼져 있습니다. 켜려면:  python scripts/setup_obs.py --enable")
        return 0

    if obs_running():
        print("\n[!] OBS가 실행 중입니다. 종료 시 설정 파일을 덮어쓰므로 먼저 OBS를 닫아주세요.")
        return 1

    cfg["server_enabled"] = True
    cfg["server_port"] = args.port
    WS_CONFIG.write_text(json.dumps(cfg, indent=2), encoding="utf-8")
    print(f"\n[+] websocket 서버를 켰습니다 (포트 {args.port}).")
    print(f"    비밀번호: {cfg.get('server_password')}")
    print("    OBS를 실행한 뒤 run.bat 을 실행하세요.")
    print("\n[주의] OBS가 비정상 종료되면 다음 실행 때 '안전 모드' 여부를 묻습니다.")
    print("       안전 모드는 websocket을 끄므로 반드시 '일반 모드로 실행'을 선택하세요.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
