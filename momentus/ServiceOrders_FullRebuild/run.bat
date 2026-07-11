@echo off
cd /d "%~dp0"
echo Starting Service Orders Pull...
dotnet run
exit /b %ERRORLEVEL%
