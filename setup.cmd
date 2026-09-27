@echo off
rem Double-click me: runs the guided setup (build + desktop shortcuts).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0setup.ps1" %*
if errorlevel 1 exit /b 1
pause
