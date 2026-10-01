# Registers LagWatch to start recording automatically at login (no admin needed)
# and starts recording now. Uses the PowerShell host (works with Smart App Control on).
$ErrorActionPreference = 'Stop'
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
$ps1 = Join-Path $dir 'LagWatch.ps1'

$vbsLocal = Join-Path $dir 'LagWatch-bg.vbs'
# NOTE: build the VBS as ONE here-string with placeholder expansion. Do NOT build it
# as an array of concatenated strings: after a comma inside @( ), PowerShell leaves
# expression mode and 'a' + $b silently splits into separate elements.
$vbs = @"
Set sh = CreateObject("WScript.Shell")
sh.CurrentDirectory = "$dir"
sh.Run "powershell.exe -NoProfile -ExecutionPolicy Bypass -File ""$ps1"" record", 0, False
"@
Set-Content -Path $vbsLocal -Value $vbs -Encoding ASCII

$startup = [Environment]::GetFolderPath('Startup')
Copy-Item $vbsLocal (Join-Path $startup 'LagWatch-bg.vbs') -Force

& powershell -NoProfile -ExecutionPolicy Bypass -File $ps1 start
Write-Host "Autostart installed: $(Join-Path $startup 'LagWatch-bg.vbs')"
