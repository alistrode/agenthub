@echo off
chcp 65001 >nul
setlocal
cd /d "%~dp0"

where powershell >nul 2>nul
if errorlevel 1 (
  echo [错误] 找不到 powershell，无法启动。
  pause
  exit /b 1
)

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0start.ps1"
if errorlevel 1 (
  echo.
  echo [错误] AgentHub 启动失败，请查看上面的提示。
  echo 日志目录: %LOCALAPPDATA%\\AgentHub\\logs
  pause
  exit /b 1
)
