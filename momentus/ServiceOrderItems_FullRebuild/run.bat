@echo off
cd /d "%~dp0"
echo Starting Service Order Items Pull...
dotnet run
exit /b %ERRORLEVEL%
