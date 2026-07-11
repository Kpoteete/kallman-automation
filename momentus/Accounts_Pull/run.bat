@echo off
cd /d "%~dp0"
echo Starting Accounts Pull...
dotnet run --project Accounts_Pull.csproj
exit /b %ERRORLEVEL%
