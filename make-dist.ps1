# Builds LagWatch and packages a portable zip you can copy to any Windows 10/11 PC.
# On the target: extract, double-click "Start LagWatch (GUI).vbs". No install, no admin.
# Optional: run install-startup.ps1 on the target to auto-record at login.
$ErrorActionPreference = 'Stop'
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path

& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $dir 'build.ps1')
if ($LASTEXITCODE -ne 0) { throw 'build failed' }

$stage = Join-Path $env:TEMP ('LagWatchDist' + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $stage | Out-Null
try
{
    foreach ($f in 'LagWatch.ps1', 'LagWatch.dll', 'LagWatch.exe', 'install-startup.ps1',
        'uninstall-startup.ps1', 'Start LagWatch (GUI).vbs', 'CHECK-IT-WORKS.bat',
        'INSTALL-AUTO-START.bat', 'README.md')
    {
        Copy-Item (Join-Path $dir $f) $stage
    }
    Copy-Item (Join-Path $dir 'src') (Join-Path $stage 'src') -Recurse

    $zip = Join-Path $dir 'LagWatch-portable.zip'
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip
}
finally
{
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
}
Write-Host "Portable package: $zip"
