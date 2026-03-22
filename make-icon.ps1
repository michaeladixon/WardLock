Add-Type -AssemblyName System.Drawing

function New-ShieldBitmap([int]$Size) {
    $bmp = New-Object System.Drawing.Bitmap($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g   = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode   = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    [float]$s = $Size / 64.0

    # ── Shield polygon ────────────────────────────────────────────────
    $pts = [System.Drawing.PointF[]]@(
        [System.Drawing.PointF]::new(32*$s,  2*$s),
        [System.Drawing.PointF]::new(61*$s,  8*$s),
        [System.Drawing.PointF]::new(61*$s, 36*$s),
        [System.Drawing.PointF]::new(32*$s, 62*$s),
        [System.Drawing.PointF]::new( 3*$s, 36*$s),
        [System.Drawing.PointF]::new( 3*$s,  8*$s)
    )
    $shield = New-Object System.Drawing.Drawing2D.GraphicsPath
    $shield.AddPolygon($pts)

    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        [System.Drawing.RectangleF]::new(0, 0, $Size, $Size),
        [System.Drawing.Color]::FromArgb(255, 137, 180, 250),
        [System.Drawing.Color]::FromArgb(255,  56,  97, 190),
        [System.Drawing.Drawing2D.LinearGradientMode]::Vertical)
    $g.FillPath($grad, $shield)
    $grad.Dispose()

    # ── Clock face (clipped to shield) ────────────────────────────────
    $g.SetClip($shield)
    [float]$cx = 32*$s; [float]$cy = 30*$s; [float]$cr = 15*$s

    $bg = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(55, 255, 255, 255))
    $g.FillEllipse($bg, $cx-$cr, $cy-$cr, $cr*2, $cr*2)
    $bg.Dispose()

    $ring = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(210, 255, 255, 255), [float](1.4*$s))
    $g.DrawEllipse($ring, $cx-$cr, $cy-$cr, $cr*2, $cr*2)
    $ring.Dispose()

    # Tick marks (12 positions)
    $tick = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(170, 255, 255, 255), [float]$s)
    for ($i = 0; $i -lt 12; $i++) {
        $a      = $i * 30 * [Math]::PI / 180
        $inner  = if ($i % 3 -eq 0) { $cr * 0.70 } else { $cr * 0.82 }
        $outer  = $cr * 0.95
        $g.DrawLine($tick,
            $cx + [float]($inner * [Math]::Sin($a)), $cy - [float]($inner * [Math]::Cos($a)),
            $cx + [float]($outer * [Math]::Sin($a)), $cy - [float]($outer * [Math]::Cos($a)))
    }
    $tick.Dispose()

    # Clock hands: minute → 12, hour → 9
    $hand = New-Object System.Drawing.Pen([System.Drawing.Color]::White, [float](2.0*$s))
    $hand.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $hand.EndCap   = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawLine($hand, $cx, $cy, $cx,            $cy - $cr*0.68)   # minute → 12
    $g.DrawLine($hand, $cx, $cy, $cx - $cr*0.52, $cy)              # hour   → 9
    $hand.Dispose()

    $dot = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
    $dr  = $s * 2.0
    $g.FillEllipse($dot, $cx-$dr, $cy-$dr, $dr*2, $dr*2)
    $dot.Dispose()

    $g.ResetClip()

    # Shield outline
    $outline = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(100, 255, 255, 255), [float]$s)
    $g.DrawPath($outline, $shield)
    $outline.Dispose()
    $shield.Dispose()
    $g.Dispose()
    return $bmp
}

# ── Render sizes as PNG bytes ─────────────────────────────────────────
$sizes   = @(16, 32, 48, 64, 256)
$pngData = @{}
foreach ($sz in $sizes) {
    $bmp = New-ShieldBitmap $sz
    $ms  = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngData[$sz] = $ms.ToArray()
    $ms.Dispose(); $bmp.Dispose()
}

# ── Assemble ICO binary ───────────────────────────────────────────────
$ico    = New-Object System.IO.MemoryStream
$offset = 6 + $sizes.Count * 16

# ICONDIR header
$ico.WriteByte(0); $ico.WriteByte(0)                           # reserved
$ico.WriteByte(1); $ico.WriteByte(0)                           # type = ICO
$ico.WriteByte([byte]$sizes.Count); $ico.WriteByte(0)          # image count

# ICONDIRENTRY for each size
foreach ($sz in $sizes) {
    $len = $pngData[$sz].Length
    $w   = if ($sz -eq 256) { [byte]0 } else { [byte]$sz }
    $ico.WriteByte($w);    $ico.WriteByte($w)                  # width, height
    $ico.WriteByte(0);     $ico.WriteByte(0)                   # colors, reserved
    $ico.WriteByte(1);     $ico.WriteByte(0)                   # planes
    $ico.WriteByte(32);    $ico.WriteByte(0)                   # bpp
    $ico.WriteByte([byte]($len         -band 0xFF))
    $ico.WriteByte([byte](($len -shr  8) -band 0xFF))
    $ico.WriteByte([byte](($len -shr 16) -band 0xFF))
    $ico.WriteByte([byte](($len -shr 24) -band 0xFF))
    $ico.WriteByte([byte]($offset         -band 0xFF))
    $ico.WriteByte([byte](($offset -shr  8) -band 0xFF))
    $ico.WriteByte([byte](($offset -shr 16) -band 0xFF))
    $ico.WriteByte([byte](($offset -shr 24) -band 0xFF))
    $offset += $len
}

# PNG image data
foreach ($sz in $sizes) { $ico.Write($pngData[$sz], 0, $pngData[$sz].Length) }

[System.IO.File]::WriteAllBytes("$PSScriptRoot\shield.ico", $ico.ToArray())
$ico.Dispose()
Write-Host "shield.ico written successfully ($($sizes.Count) sizes: $($sizes -join ', ')px)"
