@echo off
setlocal
cd /d "%~dp0.."

if "%~1"=="" (
    set "PAUSE_WHEN_DONE=1"
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Invoke-PhotoToolsPerformanceSuite.ps1" -Case All -Iterations 7
) else (
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Invoke-PhotoToolsPerformanceSuite.ps1" %*
)

set "RESULT=%ERRORLEVEL%"
if defined PAUSE_WHEN_DONE pause
if not "%RESULT%"=="0" if not defined PAUSE_WHEN_DONE pause
exit /b %RESULT%
