param(
    [string]$OutDir = (Join-Path $PSScriptRoot "..\src\AudioMesh\Assets")
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$OutDir = (Resolve-Path (New-Item -ItemType Directory -Force -Path $OutDir)).Path
$size = 1024
$master = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($master)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
$g.Clear([System.Drawing.Color]::Transparent)

function New-RoundedPath([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

# Dark rounded-square tile with a blue rim
$tile = New-RoundedPath 32 32 960 960 190
$tileBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
    (New-Object System.Drawing.Point 0, 0),
    (New-Object System.Drawing.Point 0, $size),
    ([System.Drawing.Color]::FromArgb(255, 22, 27, 38)),
    ([System.Drawing.Color]::FromArgb(255, 9, 11, 16)))
$g.FillPath($tileBrush, $tile)

$rim = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 52, 82, 132)), 10
$g.DrawPath($rim, $tile)

# Soft blue glow behind the speaker
$glow = New-Object System.Drawing.Drawing2D.GraphicsPath
$glow.AddEllipse(300, 300, 480, 480)
$glowBrush = New-Object System.Drawing.Drawing2D.PathGradientBrush($glow)
$glowBrush.CenterColor = [System.Drawing.Color]::FromArgb(90, 76, 141, 255)
$glowBrush.SurroundColors = @([System.Drawing.Color]::FromArgb(0, 76, 141, 255))
$g.FillPath($glowBrush, $glow)

# Speaker body + cone
$speaker = New-Object System.Drawing.Drawing2D.GraphicsPath
$speaker.AddPolygon(@(
    (New-Object System.Drawing.PointF 330, 420),
    (New-Object System.Drawing.PointF 430, 420),
    (New-Object System.Drawing.PointF 600, 300),
    (New-Object System.Drawing.PointF 600, 724),
    (New-Object System.Drawing.PointF 430, 604),
    (New-Object System.Drawing.PointF 330, 604)
))

$speakerBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
    (New-Object System.Drawing.Point 330, 0),
    (New-Object System.Drawing.Point 600, 0),
    ([System.Drawing.Color]::White),
    ([System.Drawing.Color]::FromArgb(255, 176, 208, 255)))
$g.FillPath($speakerBrush, $speaker)

# Sound waves
$waveColors = @(
    [System.Drawing.Color]::FromArgb(255, 120, 176, 255),
    [System.Drawing.Color]::FromArgb(255, 76, 141, 255),
    [System.Drawing.Color]::FromArgb(210, 76, 141, 255))
$radii = @(150, 240, 330)
$widths = @(30, 26, 22)

for ($i = 0; $i -lt 3; $i++) {
    $pen = New-Object System.Drawing.Pen $waveColors[$i], $widths[$i]
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $r = $radii[$i]
    $g.DrawArc($pen, 600 - $r, 512 - $r, $r * 2, $r * 2, -52, 104)
    $pen.Dispose()
}

$g.Dispose()

$pngPath = Join-Path $OutDir "icon.png"
$master.Save($pngPath, [System.Drawing.Imaging.ImageFormat]::Png)

# Build a multi-resolution .ico (PNG-compressed entries)
$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$images = @()

foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $sg = [System.Drawing.Graphics]::FromImage($bmp)
    $sg.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $sg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $sg.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $sg.DrawImage($master, (New-Object System.Drawing.Rectangle 0, 0, $s, $s))
    $sg.Dispose()

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    $images += ,@($s, $ms.ToArray())
    $ms.Dispose()
}

$icoPath = Join-Path $OutDir "AudioMesh.ico"
$fs = [System.IO.File]::Create($icoPath)
$bw = New-Object System.IO.BinaryWriter $fs

$bw.Write([UInt16]0)            # reserved
$bw.Write([UInt16]1)            # type: icon
$bw.Write([UInt16]$images.Count)

$offset = 6 + (16 * $images.Count)
foreach ($img in $images) {
    $s = $img[0]
    $data = $img[1]
    $bw.Write([Byte]$(if ($s -ge 256) { 0 } else { $s }))
    $bw.Write([Byte]$(if ($s -ge 256) { 0 } else { $s }))
    $bw.Write([Byte]0)          # colours
    $bw.Write([Byte]0)          # reserved
    $bw.Write([UInt16]1)        # planes
    $bw.Write([UInt16]32)       # bpp
    $bw.Write([UInt32]$data.Length)
    $bw.Write([UInt32]$offset)
    $offset += $data.Length
}

foreach ($img in $images) {
    $bw.Write($img[1])
}

$bw.Flush()
$bw.Dispose()
$fs.Dispose()
$master.Dispose()

Write-Output "Wrote $pngPath"
Write-Output "Wrote $icoPath ($([Math]::Round((Get-Item $icoPath).Length / 1KB, 1)) KB)"
