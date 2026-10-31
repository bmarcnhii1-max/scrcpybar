@echo off
rem Launch ScrcpyBar Bridge in LAN mode: dashboard visible on the local network.
chcp 65001 > nul
title ScrcpyBar Bridge (LAN)
echo ===================================================
echo   ScrcpyBar Bridge - LAN mode
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
"%~dp0build\ScrcpyBar.Bridge.exe" --lan --console
pause
