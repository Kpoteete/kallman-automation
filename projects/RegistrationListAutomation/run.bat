@echo off
cd /d "%~dp0"
echo Starting Registration List Automation...
dotnet run
exit /b %ERRORLEVEL%
