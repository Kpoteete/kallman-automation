@echo off
setlocal
cd /d "%~dp0"
title KWI Booth Availability Sync - LIVE SYNC

echo ============================================================
echo KWI Booth Availability Sync - LIVE SYNC
echo This WILL update SharePoint.
echo ============================================================
echo.

dotnet run -c Release -- sync
set EXITCODE=%ERRORLEVEL%

echo.
echo Exit code: %EXITCODE%
pause
exit /b %EXITCODE%
