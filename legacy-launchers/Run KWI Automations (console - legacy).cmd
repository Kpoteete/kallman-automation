@echo off
setlocal
title KWI Automation Sequence

powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "C:\kwi-automations\Run-KwiAutomationSequence.ps1"
set "KWI_EXIT_CODE=%ERRORLEVEL%"

echo.
if "%KWI_EXIT_CODE%"=="0" (
    echo All automations completed successfully.
) else if "%KWI_EXIT_CODE%"=="2" (
    echo Run cancelled.
) else (
    echo The automation sequence did not complete. See the log path shown above.
)

echo.
pause
exit /b %KWI_EXIT_CODE%
