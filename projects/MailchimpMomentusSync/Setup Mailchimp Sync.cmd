@echo off
setlocal
title Mailchimp to Momentus Sync - Setup
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Setup-MailchimpSync.ps1"
if errorlevel 1 (
  echo.
  echo Setup did not complete.
  pause
  exit /b 1
)
echo.
pause
