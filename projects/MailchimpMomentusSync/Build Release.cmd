@echo off
setlocal
title Build Kallman Mailchimp to Momentus Sync
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Build-Release.ps1"
if errorlevel 1 (
  echo.
  echo Build failed. Review the error above.
  pause
  exit /b 1
)
echo.
pause
