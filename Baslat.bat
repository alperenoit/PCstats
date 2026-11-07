@echo off
chcp 65001 >nul
taskkill /F /IM PCStatsWidget.exe >nul 2>&1
echo PC Stats Overlay Widget baslatiliyor...
if exist "%~dp0PCStatsWidget\bin\Release\net9.0-windows\PCStatsWidget.exe" (
    start "" "%~dp0PCStatsWidget\bin\Release\net9.0-windows\PCStatsWidget.exe"
) else (
    start "" "%~dp0PCStatsWidget\bin\Debug\net9.0-windows\PCStatsWidget.exe"
)
