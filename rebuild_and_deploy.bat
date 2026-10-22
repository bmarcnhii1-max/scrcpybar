@echo off
title Scrcpy Music - rebuild and deploy widget
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0rebuild_and_deploy.ps1"
if %ERRORLEVEL% NEQ 0 (
    echo.
    echo [FAILED] Widget build or registration failed - see the output above.
    pause
    exit /b %ERRORLEVEL%
)
echo.
echo [DONE] Open Win+G -^> Widgets -^> Scrcpy Music
pause
