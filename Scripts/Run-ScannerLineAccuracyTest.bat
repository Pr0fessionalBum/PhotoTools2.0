@echo off
setlocal
cd /d "%~dp0.."

if "%~1"=="" (
    set "PAUSE_WHEN_DONE=1"
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Measure-ScannerLineAccuracy.ps1" ".\performance-fixtures\small" -Sensitivity 0.55 -TargetAccuracy 90
) else (
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Measure-ScannerLineAccuracy.ps1" %*
)

set "RESULT=%ERRORLEVEL%"
if defined PAUSE_WHEN_DONE pause
if not "%RESULT%"=="0" if not defined PAUSE_WHEN_DONE pause
exit /b %RESULT%
