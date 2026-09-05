@echo off
chcp 65001 >nul
setlocal
cd /d "%~dp0"

rem PC-bang machines reset on reboot, so this rebuilds the venv when missing and
rem reuses it when it is still there.

where python >nul 2>&1
if errorlevel 1 (
  echo [!] Python을 찾을 수 없습니다.
  echo     https://www.python.org/downloads/ 에서 설치하고,
  echo     설치 화면에서 "Add python.exe to PATH" 를 반드시 체크하세요.
  pause
  exit /b 1
)

if not exist ".venv\Scripts\python.exe" (
  echo [*] 처음 실행이라 가상환경을 만듭니다. 1~3분 걸립니다.
  python -m venv .venv || (echo [!] 가상환경 생성 실패 & pause & exit /b 1)
  .venv\Scripts\python.exe -m pip install --upgrade pip -q
  .venv\Scripts\python.exe -m pip install -r requirements.txt || (echo [!] 패키지 설치 실패 & pause & exit /b 1)
)

echo.
echo [*] 아수라장 증강 오버레이를 시작합니다. 종료하려면 Ctrl+C.
echo.
.venv\Scripts\python.exe -m aram_overlay %*
pause
