# Generates all WardLock MSIX package images using WPF rendering.
# Run from any directory: powershell -ExecutionPolicy Bypass -File generate-icons.ps1

Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName PresentationFramework
Add-Type -AssemblyName WindowsBase

$ImagesDir = Join-Path $PSScriptRoot "Images"

# ---- Design constants (match shield.svg) --------------------------------
# Dark background: Catppuccin Mocha base
$BG_R = 0x1e; $BG_G = 0x1e; $BG_B = 0x2e
# Shield gradient top (#89b4fa) -> bottom (#3861be)
$GRAD_TOP_R = 0x89; $GRAD_TOP_G = 0xb4; $GRAD_TOP_B = 0xfa
$GRAD_BOT_R = 0x38; $GRAD_BOT_G = 0x61; $GRAD_BOT_B = 0xbe

function New-ShieldVisual {
    param(
        [int]$CanvasW,
        [int]$CanvasH,
        # Where to place the shield (bounding box)
        [double]$ShieldX,
        [double]$ShieldY,
        [double]$ShieldSize,
        [bool]$DrawBackground = $true,
        [string]$LabelText = ""
    )

    $visual = New-Object System.Windows.Media.DrawingVisual
    $dc = $visual.RenderOpen()

    # -- Background
    if ($DrawBackground) {
        $bgColor = [System.Windows.Media.Color]::FromRgb($BG_R, $BG_G, $BG_B)
        $bgBrush = New-Object System.Windows.Media.SolidColorBrush($bgColor)
        $dc.DrawRectangle($bgBrush, $null,
            (New-Object System.Windows.Rect(0, 0, $CanvasW, $CanvasH)))
    }

    # -- Scale/offset helpers (source SVG is 64x64)
    $s  = $ShieldSize / 64.0
    $ox = $ShieldX
    $oy = $ShieldY
    function tp([double]$x, [double]$y) {
        New-Object System.Windows.Point(($ox + $x * $s), ($oy + $y * $s))
    }

    # -- Shield gradient brush
    $gradTop = [System.Windows.Media.Color]::FromRgb($GRAD_TOP_R, $GRAD_TOP_G, $GRAD_TOP_B)
    $gradBot = [System.Windows.Media.Color]::FromRgb($GRAD_BOT_R, $GRAD_BOT_G, $GRAD_BOT_B)
    $shieldBrush = New-Object System.Windows.Media.LinearGradientBrush($gradTop, $gradBot, 90.0)

    # -- Shield polygon: 32,2 61,8 61,36 32,62 3,36 3,8
    $fig = New-Object System.Windows.Media.PathFigure
    $fig.StartPoint = tp 32 2
    $segs = @(
        (tp 61 8), (tp 61 36), (tp 32 62), (tp 3 36), (tp 3 8)
    )
    foreach ($pt in $segs) {
        $fig.Segments.Add((New-Object System.Windows.Media.LineSegment($pt, $true)))
    }
    $fig.IsClosed = $true
    $shieldGeom = New-Object System.Windows.Media.PathGeometry
    $shieldGeom.Figures.Add($fig)

    $strokeColor = [System.Windows.Media.Color]::FromArgb(0x2E, 0xFF, 0xFF, 0xFF)
    $strokePen = New-Object System.Windows.Media.Pen(
        (New-Object System.Windows.Media.SolidColorBrush($strokeColor)), (1.0 * $s))

    $dc.DrawGeometry($shieldBrush, $strokePen, $shieldGeom)

    # -- Clock face (cx=32, cy=30, r=15 in source space)
    $cx = $ox + 32 * $s
    $cy = $oy + 30 * $s
    $r  = 15 * $s

    $clockFill = [System.Windows.Media.Color]::FromArgb(0x28, 0xFF, 0xFF, 0xFF)
    $clockFillBrush = New-Object System.Windows.Media.SolidColorBrush($clockFill)
    $clockStrokeColor = [System.Windows.Media.Color]::FromArgb(0xBF, 0xFF, 0xFF, 0xFF)
    $clockPen = New-Object System.Windows.Media.Pen(
        (New-Object System.Windows.Media.SolidColorBrush($clockStrokeColor)), (1.2 * $s))

    $dc.DrawEllipse($clockFillBrush, $clockPen,
        (New-Object System.Windows.Point($cx, $cy)), $r, $r)

    # -- Clock hands
    $white = [System.Windows.Media.Colors]::White
    $whiteBrush = New-Object System.Windows.Media.SolidColorBrush($white)

    $minPen = New-Object System.Windows.Media.Pen($whiteBrush, (1.8 * $s))
    $minPen.StartLineCap = [System.Windows.Media.PenLineCap]::Round
    $minPen.EndLineCap   = [System.Windows.Media.PenLineCap]::Round
    # Minute hand: 12 o'clock (32,30) -> (32,18)
    $dc.DrawLine($minPen,
        (New-Object System.Windows.Point($cx, $cy)),
        (tp 32 18))

    $hrPen = New-Object System.Windows.Media.Pen($whiteBrush, (2.2 * $s))
    $hrPen.StartLineCap = [System.Windows.Media.PenLineCap]::Round
    $hrPen.EndLineCap   = [System.Windows.Media.PenLineCap]::Round
    # Hour hand: 9 o'clock (32,30) -> (23,30)
    $dc.DrawLine($hrPen,
        (New-Object System.Windows.Point($cx, $cy)),
        (tp 23 30))

    # Centre pivot
    $dc.DrawEllipse($whiteBrush, $null,
        (New-Object System.Windows.Point($cx, $cy)), (1.5 * $s), (1.5 * $s))

    # -- Optional label (wide tile)
    if ($LabelText -ne "") {
        $typeface = New-Object System.Windows.Media.Typeface("Segoe UI")
        $fontSize = $ShieldSize * 0.38
        $formattedText = New-Object System.Windows.Media.FormattedText(
            $LabelText,
            [System.Globalization.CultureInfo]::CurrentCulture,
            [System.Windows.FlowDirection]::LeftToRight,
            $typeface,
            $fontSize,
            $whiteBrush,
            1.0)
        $textX = $ShieldX + $ShieldSize + ($ShieldSize * 0.25)
        $textY = ($CanvasH - $formattedText.Height) / 2
        $dc.DrawText($formattedText,
            (New-Object System.Windows.Point($textX, $textY)))
    }

    $dc.Close()
    return $visual
}

function Save-VisualAsPng {
    param(
        $Visual,
        [int]$Width,
        [int]$Height,
        [string]$Path
    )
    $rtb = New-Object System.Windows.Media.Imaging.RenderTargetBitmap(
        $Width, $Height, 96, 96,
        [System.Windows.Media.PixelFormats]::Pbgra32)
    $rtb.Render($Visual)

    $enc = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $enc.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($rtb))

    $stream = [System.IO.File]::Create($Path)
    $enc.Save($stream)
    $stream.Close()
    Write-Host "  Saved: $Path"
}

function New-Icon {
    param(
        [int]$W, [int]$H,
        [double]$ShieldFraction = 0.72,  # shield occupies this fraction of min(W,H)
        [bool]$Transparent = $false,
        [string]$Label = "",
        [string]$FileName
    )

    $minDim    = [Math]::Min($W, $H)
    $shieldSz  = $minDim * $ShieldFraction
    # For wide tiles, pin shield to left area
    if ($W -gt $H * 1.5) {
        $shieldSz = $H * $ShieldFraction
        $sx = ($H - $shieldSz) / 2          # centre vertically with equal margin
        $sy = ($H - $shieldSz) / 2
    } else {
        $sx = ($W - $shieldSz) / 2
        $sy = ($H - $shieldSz) / 2
    }

    $vis = New-ShieldVisual `
        -CanvasW $W -CanvasH $H `
        -ShieldX $sx -ShieldY $sy `
        -ShieldSize $shieldSz `
        -DrawBackground (-not $Transparent) `
        -LabelText $Label

    Save-VisualAsPng -Visual $vis -Width $W -Height $H `
        -Path (Join-Path $ImagesDir $FileName)
}

Write-Host "Generating WardLock package images..."

# ---- Square 150x150 (Start Menu tile) ------------------------------------
# Manifest references Images\Square150x150Logo.png; OS selects scale variant
New-Icon -W 300 -H 300 -FileName "Square150x150Logo.scale-200.png"

# ---- Square 44x44 (All Apps list / taskbar) ------------------------------
New-Icon -W 88  -H 88  -FileName "Square44x44Logo.scale-200.png"

# ---- Square 44x44 targetsize-24 (unplated — shown on light/dark coloured bars) --
New-Icon -W 24  -H 24  -ShieldFraction 0.85 -Transparent $true `
         -FileName "Square44x44Logo.targetsize-24_altform-unplated.png"

# ---- Wide 310x150 (optional wide Start tile) -----------------------------
New-Icon -W 620 -H 300 -Label "WardLock" -FileName "Wide310x150Logo.scale-200.png"

# ---- Store logo (50x50 logical -> 100x100 at 200%) ----------------------
New-Icon -W 100 -H 100 -FileName "StoreLogo.png"

# ---- Lock screen logo (24x24 logical -> 48x48 at 200%) ------------------
New-Icon -W 48  -H 48  -Transparent $true `
         -FileName "LockScreenLogo.scale-200.png"

# ---- Splash screen (620x300 logical -> 1240x600 at 200%) ----------------
New-Icon -W 1240 -H 600 -ShieldFraction 0.55 -FileName "SplashScreen.scale-200.png"

Write-Host "Done."
