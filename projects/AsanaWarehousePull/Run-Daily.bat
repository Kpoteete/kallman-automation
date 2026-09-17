@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Run-Daily.ps1"
set "exitCode=%errorlevel%"
endlocal & exit /b %exitCode%
