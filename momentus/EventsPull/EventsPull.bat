@echo off
cd /d "%~dp0"
dotnet run --project EventsPull.csproj
exit /b %ERRORLEVEL%
