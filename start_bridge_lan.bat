@echo off
rem Launch ScrcpyBar Bridge in LAN mode: dashboard visible on the local network.
chcp 65001 > nul
title ScrcpyBar Bridge (LAN)

set "EXE=%~dp0build\ScrcpyBar.Bridge.exe"
if not exist "%EXE%" set "EXE=%~dp0ScrcpyBar.Bridge.exe"
if not exist "%EXE%" set "EXE=D:\scrcpybar-public\build\ScrcpyBar.Bridge.exe"
if not exist "%EXE%" (
  echo [!] ScrcpyBar.Bridge.exe not found.
  echo     Put this .bat next to the "build" folder from the repo,
  echo     or drop ScrcpyBar.Bridge.exe next to this .bat.
  echo     Repo: D:\scrcpybar-public
  pause
  exit /b 1
)

echo ===================================================
echo   ScrcpyBar Bridge - LAN mode
echo   Using: %EXE%
echo   This PC:      http://127.0.0.1:39281
echo   Phone/tablet: http://YOUR-PC-IP:39281  (same Wi-Fi)
echo   Your IPs are also listed in %%LOCALAPPDATA%%\ScrcpyBar\bridge.log
echo ===================================================
echo [*] Opening firewall for TCP 39281 (needs admin, once)...
netsh advfirewall firewall add rule name="ScrcpyBar Bridge 39281" dir=in action=allow protocol=TCP localport=39281 > nul 2>&1
if errorlevel 1 (
  echo [!] No admin rights - firewall rule NOT added.
  echo     Right-click this .bat -^> "Run as administrator" once,
  echo     then run it normally afterwards.
  echo.
) else (
  echo [ok] Firewall rule present.
)
echo [*] Starting bridge (this window must stay open)...
"%EXE%" --lan --console
pause
