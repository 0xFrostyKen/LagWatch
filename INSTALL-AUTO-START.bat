@echo off
rem Start recording now AND make LagWatch start automatically at every login.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install-startup.ps1"
pause
