@echo off
setlocal
cd /d "%~dp0"
echo Building Liquid Music from the source files in this folder...
echo Close the running widget using its tray menu before rebuilding.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0BUILD_NATIVE.ps1" -Launch
if errorlevel 1 (
    echo.
    echo Build failed. Read the error above. Fix it and run DEV_RUN.bat again.
    pause
    exit /b 1
)
endlocal
