# Removes LagWatch autostart and stops the recorder.
$ErrorActionPreference = 'Stop'
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
$ps1 = Join-Path $dir 'LagWatch.ps1'

& powershell -NoProfile -ExecutionPolicy Bypass -File $ps1 stop

$startupItem = Join-Path ([Environment]::GetFolderPath('Startup')) 'LagWatch-bg.vbs'
if (Test-Path $startupItem) { Remove-Item $startupItem -Force; Write-Host "Removed $startupItem" }
Write-Host "Autostart removed."
