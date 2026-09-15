@echo off
setlocal
cd /d "%~dp0"
set "BOOTHS_PULL_MODE=Incremental"

echo ========================================
echo  Kallman Booths Pull - INCREMENTAL
echo ========================================
echo.

dotnet run --project BoothsPull.csproj
set "EXITCODE=%ERRORLEVEL%"

echo.
if not "%EXITCODE%"=="0" (
    echo BoothsPull failed with exit code %EXITCODE%.
) else (
    echo BoothsPull completed successfully.
)

exit /b %EXITCODE%
