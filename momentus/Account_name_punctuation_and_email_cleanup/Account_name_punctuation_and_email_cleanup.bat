@echo off
cd /d "%~dp0"
dotnet run --project Account_name_punctuation_and_email_cleanup.csproj
exit /b %ERRORLEVEL%
