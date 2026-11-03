# Draws Portabilizer's icon: a suitcase (carry it anywhere) on a teal-to-indigo tile, with an amber "move" badge.
# Usage: .\tools\make-icon.ps1 [-Preview <png>]   writes src\Portabilizer\Assets\Portabilizer.ico
param([string]$Preview)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$out = Join-Path $root 'src\Portabilizer\Assets\Portabilizer.ico'
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256

function RoundRect([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    $p
}

function Draw([int]$S) {
    $bmp = New-Object System.Drawing.Bitmap $S, $S, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    $f = [single]$S
    $white = [System.Drawing.Color]::White
    $indigo = [System.Drawing.Color]::FromArgb(67, 56, 202)

    $m = [single]([math]::Max(0.5, 0.03 * $f))
    $tile = RoundRect $m $m ($f - 2 * $m) ($f - 2 * $m) (0.22 * $f)
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0, 0), (New-Object System.Drawing.PointF $f, $f), ([System.Drawing.Color]::FromArgb(13, 182, 170)), $indigo
    $g.FillPath($bg, $tile)

    $handle = New-Object System.Drawing.Pen $white, ([single]([math]::Max(1.2, 0.065 * $f)))
    $g.DrawPath($handle, (RoundRect (0.37 * $f) (0.21 * $f) (0.26 * $f) (0.2 * $f) (0.07 * $f)))
    $g.FillPath((New-Object System.Drawing.SolidBrush $white), (RoundRect (0.18 * $f) (0.35 * $f) (0.64 * $f) (0.44 * $f) (0.08 * $f)))
    $strap = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(150, $indigo))
    foreach ($x in 0.32, 0.63) { $g.FillRectangle($strap, [single]($x * $f), [single](0.35 * $f), [single](0.05 * $f), [single](0.44 * $f)) }

    if ($S -ge 32) {
        $r = 0.17 * $f; $cx = 0.75 * $f; $cy = 0.75 * $f
        $g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(245, 158, 11))), [single]($cx - $r), [single]($cy - $r), [single](2 * $r), [single](2 * $r))
        $g.DrawEllipse((New-Object System.Drawing.Pen $white, ([single](0.035 * $f))), [single]($cx - $r), [single]($cy - $r), [single](2 * $r), [single](2 * $r))
        $arrow = New-Object System.Drawing.Pen $white, ([single](0.045 * $f))
        $arrow.StartCap = 'Round'; $arrow.EndCap = 'Round'
        $a = 0.065 * $f
        $g.DrawLine($arrow, [single]($cx - $a), [single]($cy + $a), [single]($cx + $a), [single]($cy - $a))
        $g.DrawLine($arrow, [single]($cx - $a * 0.1), [single]($cy - $a), [single]($cx + $a), [single]($cy - $a))
        $g.DrawLine($arrow, [single]($cx + $a), [single]($cy - $a), [single]($cx + $a), [single]($cy + $a * 0.1))
    }
    $g.Dispose()
    $bmp
}

# An .ico is a small header plus one PNG per size; Windows 10 and 11 read PNG frames at every size.
$frames = foreach ($s in $sizes) {
    $bmp = Draw $s
    $ms = New-Object IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    , @($s, $ms.ToArray())
}
New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null
$fs = [IO.File]::Create($out)
$w = New-Object IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$frames.Count)
$offset = 6 + 16 * $frames.Count
foreach ($fr in $frames) {
    $s = $fr[0]; $data = $fr[1]
    $w.Write([byte]($s % 256)); $w.Write([byte]($s % 256)); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$data.Length); $w.Write([uint32]$offset)
    $offset += $data.Length
}
foreach ($fr in $frames) { $w.Write([byte[]]$fr[1]) }
$w.Dispose()
"wrote $out ($((Get-Item $out).Length) bytes, $($frames.Count) sizes)"

if ($Preview) {
    # Every size at its real pixels, on light and dark, then the large one, so small-size legibility can be judged.
    $sheet = New-Object System.Drawing.Bitmap 760, 330
    $g = [System.Drawing.Graphics]::FromImage($sheet)
    $g.Clear([System.Drawing.Color]::White)
    $g.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(32, 32, 32))), 0, 165, 480, 165)
    $x = 10
    foreach ($s in 16, 20, 24, 32, 48, 64, 128) {
        $b = Draw $s
        $g.DrawImageUnscaled($b, $x, 150 - $s); $g.DrawImageUnscaled($b, $x, 315 - $s)
        $b.Dispose(); $x += $s + 14
    }
    $big = Draw 256
    $g.DrawImageUnscaled($big, 490, 37)
    $g.Dispose()
    $sheet.Save($Preview, [System.Drawing.Imaging.ImageFormat]::Png)
    "preview: $Preview"
}
