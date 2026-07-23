@echo off
cd /d "%~dp0"
dotnet run --project WebsiteCorrectionDaily.csproj
exit /b %ERRORLEVEL%
