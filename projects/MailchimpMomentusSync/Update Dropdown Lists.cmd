@echo off
setlocal
title Mailchimp to Momentus Sync - Update Dropdown Lists
powershell.exe -NoLogo -NoProfile -STA -ExecutionPolicy Bypass -File "%~dp0Update-DropdownLists.ps1"
if errorlevel 1 (
  echo.
  echo The dropdown lists were not updated.
  pause
  exit /b 1
)
echo.
pause
