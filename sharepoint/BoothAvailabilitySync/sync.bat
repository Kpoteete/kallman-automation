@echo off
setlocal
cd /d "%~dp0"
title KWI Event Portal SharePoint Sync - LIVE

echo ============================================================
echo KWI Event Portal SharePoint Sync - LIVE
echo This WILL update SharePoint.
echo ============================================================
echo.

dotnet run -c Release -- sync
set EXITCODE=%ERRORLEVEL%

echo.
echo Exit code: %EXITCODE%
pause
exit /b %EXITCODE%
