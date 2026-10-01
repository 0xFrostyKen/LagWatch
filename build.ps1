# Builds LagWatch.exe using the C# compiler that ships with Windows (.NET Framework 4.x).
# No SDK, no NuGet, no internet needed.
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path

$cscCandidates = @(
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe')
)
$csc = $cscCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $csc) { throw 'csc.exe not found (is .NET Framework 4.x installed?)' }

$sources = Get-ChildItem (Join-Path $here 'src') -Filter '*.cs' | ForEach-Object { $_.FullName }
$out = Join-Path $here 'LagWatch.exe'
$outDll = Join-Path $here 'LagWatch.dll'

$refs = '/r:System.Management.dll', '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll'

& $csc /nologo /target:exe /platform:anycpu /optimize+ /warn:3 `
    /out:$out `
    @refs `
    $sources

if ($LASTEXITCODE -ne 0) { throw "compile failed with exit code $LASTEXITCODE" }

# Also build the same code as a library: Smart App Control blocks unsigned exes,
# but the library can be hosted in-memory by the signed PowerShell host (LagWatch.ps1).
& $csc /nologo /target:library /platform:anycpu /optimize+ /warn:3 `
    /out:$outDll `
    @refs `
    $sources

if ($LASTEXITCODE -ne 0) { throw "library compile failed with exit code $LASTEXITCODE" }
Write-Host "Built $out"
Write-Host "Built $outDll"
