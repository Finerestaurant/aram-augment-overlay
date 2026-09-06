@echo off
chcp 65001 >nul
setlocal
cd /d "%~dp0"

rem PC-bang machines reset on reboot, so this rebuilds the venv when missing and
rem reuses it when it is still there. This window is only up while installing --
rem the overlay itself starts windowless (pythonw) and shows its own status
rem window, because a console full of debug lines reads as a crash to anyone who
rem did not write it.

where python >nul 2>&1
if errorlevel 1 (
  echo [!] Python을 찾을 수 없습니다.
  echo     https://www.python.org/downloads/ 에서 설치하고,
  echo     설치 화면에서 "Add python.exe to PATH" 를 반드시 체크하세요.
  pause
  exit /b 1
)

if not exist ".venv\Scripts\pythonw.exe" (
  echo [*] 처음 실행이라 가상환경을 만듭니다. 1~3분 걸립니다.
  python -m venv .venv || (echo [!] 가상환경 생성 실패 & pause & exit /b 1)
  .venv\Scripts\python.exe -m pip install --upgrade pip -q
  .venv\Scripts\python.exe -m pip install -r requirements.txt || (echo [!] 패키지 설치 실패 & pause & exit /b 1)
)

rem An older venv predates the tray icon; top it up rather than rebuilding it.
.venv\Scripts\python.exe -c "import pystray" >nul 2>&1
if errorlevel 1 (
  echo [*] 새 구성 요소를 설치합니다...
  .venv\Scripts\python.exe -m pip install -r requirements.txt -q || (echo [!] 패키지 설치 실패 & pause & exit /b 1)
)

start "" ".venv\Scripts\pythonw.exe" -m aram_overlay --gui %*
exit /b 0
