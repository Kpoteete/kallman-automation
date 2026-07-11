@echo off
cd /d "%~dp0"
dotnet run --project NotesPull.csproj
exit /b %ERRORLEVEL%
