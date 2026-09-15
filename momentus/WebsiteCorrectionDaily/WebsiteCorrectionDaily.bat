@echo off
dotnet run --project "C:\kwi-automations\momentus\Account_name_punctuation_and_email_cleanup\Account_name_punctuation_and_email_cleanup.csproj" -- --website-only
exit /b %ERRORLEVEL%
