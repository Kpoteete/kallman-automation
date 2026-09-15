@echo off
setlocal
set "NODE=%USERPROFILE%\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe"
if not exist "%NODE%" set "NODE=node"
cd /d "%~dp0"
"%NODE%" scripts\serve_event_calendar.mjs
pause
