param([int]$ProcId, [string]$OutPath)
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
public class W {
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
}
"@
$p = Get-Process -Id $ProcId
$h = $p.MainWindowHandle
$r = New-Object RECT
[W]::GetWindowRect($h, [ref]$r) | Out-Null
$w = $r.Right - $r.Left
$ht = $r.Bottom - $r.Top
if ($w -le 0 -or $ht -le 0) { Write-Host "window rect invalid"; exit 1 }
$bmp = New-Object System.Drawing.Bitmap($w, $ht)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
$ok = [W]::PrintWindow($h, $hdc, 2)  # 2 = PW_RENDERFULLCONTENT
$g.ReleaseHdc($hdc)
$g.Dispose()
if (-not $ok) { Write-Host "PrintWindow failed"; exit 1 }
$bmp.Save($OutPath)
$bmp.Dispose()
Write-Host ("saved {0}x{1} -> {2}" -f $w, $ht, $OutPath)
