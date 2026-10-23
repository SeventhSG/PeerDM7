# make-icon.ps1
# Draws the PeerDM7 application icon at several sizes and writes:
#   src\PeerDM7\Assets\PeerDM7.ico  (multi-image ICO, PNG-encoded entries)
#   src\PeerDM7\Assets\PeerDM7.png  (256 px master image)
# Windows PowerShell 5.1, System.Drawing only.

Add-Type -AssemblyName System.Drawing

$assetsDir = 'C:\Users\gamin\Documents\Projects\PeerDM7\src\PeerDM7\Assets'
$icoPath   = Join-Path $assetsDir 'PeerDM7.ico'
$pngPath   = Join-Path $assetsDir 'PeerDM7.png'

if (-not (Test-Path $assetsDir)) {
    New-Item -ItemType Directory -Force -Path $assetsDir | Out-Null
}

$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)

$colTop    = [System.Drawing.Color]::FromArgb(0x3A, 0x7B, 0xE6)
$colBottom = [System.Drawing.Color]::FromArgb(0x2A, 0x63, 0xC5)
$colWhite  = [System.Drawing.Color]::FromArgb(0xFD, 0xFE, 0xFF)

function Pt([double]$a, [double]$b) {
    return [System.Drawing.PointF]::new([single]$a, [single]$b)
}

# Rounded rectangle built from four quarter arcs.
function New-RoundRectPath([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $dia = 2 * $r
    $p.AddArc($x, $y, $dia, $dia, 180, 90)
    $p.AddArc($x + $w - $dia, $y, $dia, $dia, 270, 90)
    $p.AddArc($x + $w - $dia, $y + $h - $dia, $dia, $dia, 0, 90)
    $p.AddArc($x, $y + $h - $dia, $dia, $dia, 90, 90)
    $p.CloseFigure()
    return $p
}

# Bubble body (rounded rect) plus a triangular tail at a bottom corner.
# fx, fy, fw, fh are fractions of the canvas size.
function Get-BubbleShape([single]$S, [double]$fx, [double]$fy, [double]$fw, [double]$fh, [string]$tailSide) {
    $x  = $fx * $S
    $y  = $fy * $S
    $w  = $fw * $S
    $h  = $fh * $S
    $r  = 0.30 * $h
    $body = New-RoundRectPath ([single]$x) ([single]$y) ([single]$w) ([single]$h) ([single]$r)
    $yb = $y + $h
    $th = 0.22 * $h
    if ($tailSide -eq 'left') {
        $tip = Pt ($x + 0.06 * $w) ($yb + $th)
        $ta  = Pt ($x + 0.30 * $w) ($yb - 0.06 * $h)
        $tb  = Pt ($x + 0.46 * $w) ($yb - 0.06 * $h)
    } else {
        $tip = Pt ($x + 0.94 * $w) ($yb + $th)
        $ta  = Pt ($x + 0.70 * $w) ($yb - 0.06 * $h)
        $tb  = Pt ($x + 0.54 * $w) ($yb - 0.06 * $h)
    }
    $tail = [System.Drawing.PointF[]]@($tip, $ta, $tb)
    return @{ Body = $body; Tail = $tail }
}

function Fill-Shape($g, $shape, $brush) {
    $g.FillPath($brush, $shape.Body)
    $g.FillPolygon($brush, $shape.Tail)
}

# Draws the shape outline with a pen of width 2*d, which grows the shape by d on every side.
function Stroke-Halo($g, $shape, [single]$d, $color) {
    $pen = New-Object System.Drawing.Pen($color, [single](2 * $d))
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $g.DrawPath($pen, $shape.Body)
    $g.DrawPolygon($pen, $shape.Tail)
    $pen.Dispose()
}

function Set-Quality($g) {
    $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode   = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
}

function New-IconBitmap([int]$px) {
    $S   = [single]$px
    $bmp = New-Object System.Drawing.Bitmap($px, $px, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g   = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::Transparent)
    Set-Quality $g

    # Background rounded square: 6% margin, corner radius 22%, vertical gradient.
    $m      = 0.06 * $S
    $bgSize = $S - 2 * $m
    $bgPath = New-RoundRectPath ([single]$m) ([single]$m) ([single]$bgSize) ([single]$bgSize) ([single](0.22 * $S))
    $gradStart = [System.Drawing.PointF]::new([single]0, [single]$m)
    $gradEnd   = [System.Drawing.PointF]::new([single]0, [single]($S - $m))
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush($gradStart, $gradEnd, $colTop, $colBottom)
    $g.FillPath($grad, $bgPath)
    $grad.Dispose()

    $halo  = 0.04 * $S
    $white = New-Object System.Drawing.SolidBrush($colWhite)

    if ($px -le 20) {
        # Small sizes: one larger front bubble only.
        $front = Get-BubbleShape $S 0.22 0.26 0.56 0.44 'right'
        Stroke-Halo $g $front ([single]$halo) $colBottom
        Fill-Shape $g $front $white
    } else {
        # Back bubble at 55% opacity (alpha 140). Drawn opaque on a layer, then composited
        # with an alpha matrix so the tail/body overlap does not double the alpha.
        $back  = Get-BubbleShape $S 0.20 0.22 0.46 0.34 'left'
        $layer = New-Object System.Drawing.Bitmap($px, $px, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $lg    = [System.Drawing.Graphics]::FromImage($layer)
        $lg.Clear([System.Drawing.Color]::Transparent)
        Set-Quality $lg
        $whiteL = New-Object System.Drawing.SolidBrush($colWhite)
        Fill-Shape $lg $back $whiteL
        $whiteL.Dispose()
        $lg.Dispose()

        $cm = New-Object System.Drawing.Imaging.ColorMatrix
        $cm.Matrix00 = 1.0
        $cm.Matrix11 = 1.0
        $cm.Matrix22 = 1.0
        $cm.Matrix33 = [single](140.0 / 255.0)
        $ia = New-Object System.Drawing.Imaging.ImageAttributes
        $ia.SetColorMatrix($cm)
        $destRect = [System.Drawing.Rectangle]::new(0, 0, $px, $px)
        $g.DrawImage($layer, $destRect, 0, 0, $px, $px, [System.Drawing.GraphicsUnit]::Pixel, $ia)
        $ia.Dispose()
        $layer.Dispose()

        # Front bubble, full opacity, with a gap made by a bottom-colour halo.
        $front = Get-BubbleShape $S 0.36 0.42 0.46 0.34 'right'
        Stroke-Halo $g $front ([single]$halo) $colBottom
        Fill-Shape $g $front $white
    }

    $white.Dispose()
    $g.Dispose()
    return $bmp
}

# Render every size natively and store its PNG bytes.
$pngBytes = @{}
foreach ($px in $sizes) {
    $bmp = New-IconBitmap $px
    $ms  = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngBytes[$px] = $ms.ToArray()
    if ($px -eq 256) {
        $bmp.Save($pngPath, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    $ms.Dispose()
    $bmp.Dispose()
    Write-Output ("Rendered {0} px ({1} bytes PNG)" -f $px, $pngBytes[$px].Length)
}

# Write the ICO container: ICONDIR, then ICONDIRENTRY x N, then the PNG payloads.
$fs = [System.IO.File]::Create($icoPath)
$bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write([uint16]0)                 # reserved
$bw.Write([uint16]1)                 # type 1 = icon
$bw.Write([uint16]$sizes.Count)      # image count

$offset = 6 + 16 * $sizes.Count
foreach ($px in $sizes) {
    $data = $pngBytes[$px]
    $dim  = if ($px -eq 256) { 0 } else { $px }
    $bw.Write([byte]$dim)            # width  (256 stored as 0)
    $bw.Write([byte]$dim)            # height (256 stored as 0)
    $bw.Write([byte]0)               # colorCount
    $bw.Write([byte]0)               # reserved
    $bw.Write([uint16]1)             # planes
    $bw.Write([uint16]32)            # bitCount
    $bw.Write([uint32]$data.Length)  # bytesInRes
    $bw.Write([uint32]$offset)       # imageOffset
    $offset += $data.Length
}
foreach ($px in $sizes) {
    $bw.Write($pngBytes[$px])
}
$bw.Flush()
$bw.Close()
$fs.Close()

Write-Output "Wrote $icoPath"
Write-Output "Wrote $pngPath"
