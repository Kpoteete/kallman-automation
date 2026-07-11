@echo off
cd /d "%~dp0"
dotnet run --project ServiceOrderItemsPull.csproj
exit /b %ERRORLEVEL%
