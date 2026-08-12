@echo off
setlocal
title Mailchimp to Momentus Sync
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Run-MailchimpSync.ps1"
set "SYNC_EXIT=%ERRORLEVEL%"
echo.
if not "%SYNC_EXIT%"=="0" echo The sync did not finish successfully. Do not rerun until the error is reviewed.
pause
exit /b %SYNC_EXIT%
