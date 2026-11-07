@echo off
chcp 65001 >nul
taskkill /F /IM PCStatsWidget.exe >nul 2>&1
echo PC Stats Overlay Widget baslatiliyor...
start "" "%~dp0PCStatsWidget\bin\Debug\net9.0-windows\PCStatsWidget.exe"
