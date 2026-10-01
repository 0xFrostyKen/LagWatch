@echo off
rem Check that LagWatch works on this machine (environment, counters, recorder, autostart).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0LagWatch.ps1" doctor
pause
