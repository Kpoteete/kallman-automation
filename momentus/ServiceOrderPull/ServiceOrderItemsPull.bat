@echo off
cd /d "%~dp0"
dotnet run --project ServiceOrderPull.csproj
exit /b %ERRORLEVEL%
