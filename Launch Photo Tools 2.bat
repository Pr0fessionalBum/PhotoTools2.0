@echo off
setlocal
title Photo Tools 2.0 Launcher

cd /d "%~dp0"

set "PHOTOTOOLS_DOTNET=C:\Program Files\dotnet\dotnet.exe"

if not exist "%PHOTOTOOLS_DOTNET%" (
    echo Photo Tools 2.0 could not find the .NET SDK.
    echo Expected location: %PHOTOTOOLS_DOTNET%
    echo.
    pause
    exit /b 1
)

echo Starting Photo Tools 2.0...
"%PHOTOTOOLS_DOTNET%" run --project "%~dp0PhotoTools2.csproj" -p:Platform=x64

if errorlevel 1 (
    echo.
    echo Photo Tools 2.0 did not start successfully.
    echo Review the error above, then press any key to close this window.
    pause
    exit /b 1
)

endlocal
