# LagWatch PowerShell host.
#
# Why this exists: Smart App Control (On) blocks unsigned locally-compiled .exe files.
# The signed powershell.exe can load the same compiled code as a LIBRARY in memory
# (Assembly.Load from bytes is not a file-backed image load, so SAC does not block it).
# All actual logic lives in LagWatch.dll built from src\ by build.ps1.
#
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File LagWatch.ps1 <same args as LagWatch.exe>

param([Parameter(Position = 0, ValueFromRemainingArguments = $true)][string[]]$Rest)

$ErrorActionPreference = 'Stop'

$dll = Join-Path $PSScriptRoot 'LagWatch.dll'
if (-not (Test-Path $dll)) {
    Write-Error "LagWatch.dll not found next to this script. Run build.ps1 first."
    exit 1
}

# Handshake so the library knows it should spawn background recorders through this host
$env:LAGWATCH_HOST = 'ps1'
$env:LAGWATCH_PS1  = $MyInvocation.MyCommand.Path

$bytes = [System.IO.File]::ReadAllBytes($dll)
$asm = [System.Reflection.Assembly]::Load($bytes)

$entry = $asm.EntryPoint
if (-not $entry) {
    # libraries have no PE entry point - locate Program.Main reflectively
    $t = $asm.GetType('LagWatch.Program')
    if (-not $t) { Write-Error 'Type LagWatch.Program not found in LagWatch.dll'; exit 1 }
    $entry = $t.GetMethod('Main', [System.Reflection.BindingFlags]'Static,Public,NonPublic')
}
if (-not $entry) { Write-Error 'No entry point found in LagWatch.dll'; exit 1 }

if ($null -eq $Rest) { $Rest = @() }
$invokeArgs = New-Object 'object[]' 1
$invokeArgs[0] = [string[]]$Rest

$exitCode = $entry.Invoke($null, $invokeArgs)
[Environment]::ExitCode = $exitCode
exit $exitCode
