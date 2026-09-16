@echo off
setlocal
cd /d "%~dp0"
title KWI Event Portal SharePoint Sync - Preview

echo ============================================================
echo KWI Event Portal SharePoint Sync - PREVIEW
echo No SharePoint values will be changed.
echo ============================================================
echo.

dotnet run -c Release -- preview
set EXITCODE=%ERRORLEVEL%

echo.
echo Exit code: %EXITCODE%
pause
exit /b %EXITCODE%
