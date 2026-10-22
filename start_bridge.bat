@echo off
rem cmd.exe reads .bat files in the OEM codepage, so the UTF-8 Cyrillic banner below would
rem come out as mojibake without this switch.
chcp 65001 > nul
title ScrcpyBar Bridge Service (console mode)
echo ===================================================
echo   ScrcpyBar Bridge - фоновая служба для телефона
echo   Док: http://127.0.0.1:39281
echo   Логи (обычный запуск): %LOCALAPPDATA%\ScrcpyBar\bridge.log
echo   Режим --console: логи идут и в это окно
echo ===================================================
dotnet run --project "%~dp0ScrcpyBar.Bridge\ScrcpyBar.Bridge.csproj" -- --console
pause
