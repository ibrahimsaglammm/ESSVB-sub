@echo off
chcp 65001 >nul
setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install-windows.ps1" -LaunchOnly
if errorlevel 1 (
    echo.
    echo ESSVB-sub baslatilirken bir hata olustu. Yukaridaki mesaji kontrol edin.
    pause
)
