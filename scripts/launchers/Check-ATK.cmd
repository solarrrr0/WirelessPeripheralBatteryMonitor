@echo off
setlocal
if not exist "%~dp0app\PeripheralBatteryTray.exe" (
  echo Missing app files. Extract the complete ZIP before starting.
  pause
  exit /b 1
)
start "" "%~dp0app\PeripheralBatteryTray.exe" --verify-atk-host --report "%~dp0atk-host.local.json"
