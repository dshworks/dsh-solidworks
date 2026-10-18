# Record one window as JPEG frames named by their UTC time in milliseconds, until <Out>\..\rec-stop.flag
# appears. PrintWindow captures the window even when something covers it. -Once takes one frame.
# Run it in the desktop session that shows SOLIDWORKS; find the window with
#   Get-Process SLDWORKS | Select-Object Id, MainWindowHandle, MainWindowTitle
param([long]$Hwnd, [int]$Fps = 4, [string]$Out = (Join-Path $env:TEMP 'swclip\frames'), [double]$Scale = 0.5, [switch]$Once)
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices;
public static class Win {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
}
'@
# The VM runs at a high display scale; a DPI-unaware process sees SOLIDWORKS' window at the
# logical size and PrintWindow hands back only its top-left part. Physical pixels, please.
[Win]::SetProcessDPIAware() | Out-Null
New-Item -ItemType Directory -Force $Out | Out-Null
$stop = Join-Path (Split-Path $Out) 'rec-stop.flag'
Remove-Item $stop -ErrorAction SilentlyContinue
$h = [IntPtr]$Hwnd
$codec = [System.Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() | Where-Object { $_.MimeType -eq 'image/jpeg' }
$ep = New-Object System.Drawing.Imaging.EncoderParameters 1
$ep.Param[0] = New-Object System.Drawing.Imaging.EncoderParameter ([System.Drawing.Imaging.Encoder]::Quality), 88L
$n = 0
do {
  $t0 = [DateTime]::UtcNow
  $r = New-Object Win+RECT
  [Win]::GetWindowRect($h, [ref]$r) | Out-Null
  $w = $r.R - $r.L; $hh = $r.B - $r.T
  if ($w -gt 0 -and $hh -gt 0) {
    $bmp = New-Object System.Drawing.Bitmap $w, $hh
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $dc = $g.GetHdc(); [Win]::PrintWindow($h, $dc, 2) | Out-Null; $g.ReleaseHdc($dc); $g.Dispose()
    $ms = [long]($t0 - [DateTime]'1970-01-01').TotalMilliseconds
    $sw = [int]($w * $Scale); $sh = [int]($hh * $Scale)
    $small = New-Object System.Drawing.Bitmap $sw, $sh
    $gs = [System.Drawing.Graphics]::FromImage($small)
    $gs.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $gs.DrawImage($bmp, 0, 0, $sw, $sh); $gs.Dispose()
    $small.Save((Join-Path $Out "$ms.jpg"), $codec, $ep); $small.Dispose(); $bmp.Dispose(); $n++
  }
  if ($Once) { break }
  $left = 1000 / $Fps - ([DateTime]::UtcNow - $t0).TotalMilliseconds
  if ($left -gt 0) { Start-Sleep -Milliseconds $left }
} until (Test-Path $stop)
"frames=$n size=${w}x$hh"
