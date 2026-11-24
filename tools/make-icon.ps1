param([Parameter(Mandatory = $true)][string]$Out)
# Draws the app icon (white "CL" clock logo on a blue rounded square) at several sizes
# and writes a PNG-based .ico. Same logo geometry as Tray.DrawLogo.
Add-Type -AssemblyName System.Drawing
$sizes = 16, 20, 24, 32, 48, 64, 256
$pngs = New-Object System.Collections.ArrayList
foreach ($s in $sizes) {
    $SS = 4; $S = [single]($s * $SS)
    $big = [System.Drawing.Bitmap]::new([int]$S, [int]$S, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($big)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)

    # rounded-square background with a soft blue gradient
    $pad = [single]($S * 0.04); $rr = [single]($S * 0.24); $side = [single]($S - 2 * $pad); $d = 2 * $rr
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $path.AddArc($pad, $pad, $d, $d, 180, 90)
    $path.AddArc($pad + $side - $d, $pad, $d, $d, 270, 90)
    $path.AddArc($pad + $side - $d, $pad + $side - $d, $d, $d, 0, 90)
    $path.AddArc($pad, $pad + $side - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $brush = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
        [System.Drawing.PointF]::new(0, 0), [System.Drawing.PointF]::new(0, $S),
        [System.Drawing.Color]::FromArgb(255, 0x4C, 0xB8, 0xFF), [System.Drawing.Color]::FromArgb(255, 0x00, 0x5F, 0xB8))
    $g.FillPath($brush, $path)

    # logo, drawn in a box 64% of the icon
    $L = [single]($S * 0.64); $o = [single](($S - $L) / 2); $c = [single]($o + $L / 2)
    $w = [single]($L * 0.135); $r = [single]($L * 0.5 - $w / 2 - $L * 0.03)
    $pen = [System.Drawing.Pen]::new([System.Drawing.Color]::White, $w)
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'; $pen.LineJoin = 'Round'
    $g.DrawArc($pen, [single]($c - $r), [single]($c - $r), [single](2 * $r), [single](2 * $r), [single]42, [single]276)
    $pts = [System.Drawing.PointF[]]@(
        [System.Drawing.PointF]::new($c - $L * 0.03, $c - $L * 0.235),
        [System.Drawing.PointF]::new($c - $L * 0.03, $c + $L * 0.015),
        [System.Drawing.PointF]::new($c + $L * 0.215, $c + $L * 0.015))
    $g.DrawLines($pen, $pts)
    $g.Dispose()

    # downscale smoothly
    $bmp = [System.Drawing.Bitmap]::new($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g2 = [System.Drawing.Graphics]::FromImage($bmp)
    $g2.Clear([System.Drawing.Color]::Transparent)
    $g2.CompositingMode = 'SourceCopy'; $g2.InterpolationMode = 'HighQualityBicubic'; $g2.PixelOffsetMode = 'HighQuality'
    $ia = [System.Drawing.Imaging.ImageAttributes]::new(); $ia.SetWrapMode('TileFlipXY')
    $g2.DrawImage($big, [System.Drawing.Rectangle]::new(0, 0, $s, $s), 0, 0, [int]$S, [int]$S, [System.Drawing.GraphicsUnit]::Pixel, $ia)
    $g2.Dispose(); $big.Dispose()

    $ms = [System.IO.MemoryStream]::new()
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    [void]$pngs.Add($ms.ToArray())
}
New-Item -ItemType Directory -Force (Split-Path -Parent $Out) | Out-Null
$fs = [System.IO.File]::Create($Out)
$bw = [System.IO.BinaryWriter]::new($fs)
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $pngs[$i].Length
    $b = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$b); $bw.Write([byte]$b); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$len); $bw.Write([uint32]$offset)
    $offset += $len
}
foreach ($p in $pngs) { $bw.Write([byte[]]$p) }
$bw.Close()
