param(
    [string]$OutDir = (Join-Path $PSScriptRoot "")
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

# Palette matches the widget: Xbox green on the dark acrylic background.
$Green = [System.Drawing.Color]::FromArgb(255, 16, 124, 65)
$GreenDark = [System.Drawing.Color]::FromArgb(255, 11, 92, 46)
$White = [System.Drawing.Color]::FromArgb(255, 255, 255, 255)
$Dark = [System.Drawing.Color]::FromArgb(255, 11, 15, 25)

function New-Brushed {
    param([int]$Size, [double]$SuperSample = 4.0)

    $bmp = New-Object System.Drawing.Bitmap $Size, $Size
    $bmp.SetResolution(96, 96)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $g.Clear([System.Drawing.Color]::Transparent)
    return @($bmp, $g)
}

function New-GradientBrush {
    param([System.Drawing.RectangleF]$Rect, [System.Drawing.Color]$Top, [System.Drawing.Color]$Bottom)
    $b = New-Object System.Drawing.Drawing2D.LinearGradientBrush $Rect, $Top, $Bottom, 90.0
    return $b
}

<#
    Draws the app mark: an eighth note in white on an Xbox green tile.
    Coordinates are normalised (0..1) against the tile so every size stays identical.
#>
function Draw-Note {
    param(
        [System.Drawing.Graphics]$G,
        [System.Drawing.RectangleF]$Tile,
        [System.Drawing.Color]$Color,
        [double]$Scale = 1.0
    )

    $s = $Tile.Width * $Scale
    $ox = $Tile.X + ($Tile.Width - $s) / 2
    $oy = $Tile.Y + ($Tile.Height - $s) / 2

    function Px([double]$x) { return $ox + $x * $s }
    function Py([double]$y) { return $oy + $y * $s }

    $note = New-Object System.Drawing.SolidBrush $Color

    # Stem: sits on the right shoulder of the note head and reaches the top of the tile.
    $stemLeft = Px 0.508
    $stemTop = Py 0.185
    $stemBottom = Py 0.645
    $stemW = 0.058 * $s
    $stem = New-Object System.Drawing.RectangleF $stemLeft, $stemTop, $stemW, ($stemBottom - $stemTop)
    $G.FillRectangle($note, $stem)

    # Flag: a slim ribbon that leaves the top of the stem, arcs right and tapers to a point.
    # It starts slightly inside the stem so the antialiased edge cannot show a seam.
    $flag = New-Object System.Drawing.Drawing2D.GraphicsPath
    $flag.StartFigure()
    $flag.AddBezier(
        [single](Px 0.540), [single](Py 0.185),
        [single](Px 0.690), [single](Py 0.188),
        [single](Px 0.800), [single](Py 0.225),
        [single](Px 0.840), [single](Py 0.300))
    $flag.AddBezier(
        [single](Px 0.840), [single](Py 0.300),
        [single](Px 0.790), [single](Py 0.392),
        [single](Px 0.690), [single](Py 0.428),
        [single](Px 0.540), [single](Py 0.468))
    $flag.CloseFigure()
    $G.FillPath($note, $flag)

    # Note head: a tilted ellipse whose right shoulder meets the stem.
    $rx = 0.190 * $s
    $ry = 0.132 * $s
    $state = $G.Save()
    $G.TranslateTransform([single](Px 0.365), [single](Py 0.705))
    $G.RotateTransform(-20.0)
    $G.FillEllipse($note, [single](-$rx), [single](-$ry), [single](2 * $rx), [single](2 * $ry))
    $G.Restore($state)

    $note.Dispose()
    $flag.Dispose()
}

<#
    Renders the square app mark at one size.
    Plated variants get a rounded tile, unplated ones a full-bleed square.
#>
function New-AppIcon {
    param([int]$Size, [switch]$Unplated)

    $bmp = New-Object System.Drawing.Bitmap $Size, $Size
    $bmp.SetResolution(96, 96)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    $rect = New-Object System.Drawing.RectangleF 0, 0, $Size, $Size
    if ($Unplated) {
        $brush = New-GradientBrush $rect $Green $GreenDark
        $g.FillRectangle($brush, $rect)
        $brush.Dispose()
    }
    else {
        $radius = $Size * 0.22
        $path = New-Object System.Drawing.Drawing2D.GraphicsPath
        $d = $radius * 2
        $path.AddArc(0, 0, $d, $d, 180, 90)
        $path.AddArc($Size - $d, 0, $d, $d, 270, 90)
        $path.AddArc($Size - $d, $Size - $d, $d, $d, 0, 90)
        $path.AddArc(0, $Size - $d, $d, $d, 90, 90)
        $path.CloseFigure()
        $brush = New-GradientBrush $rect $Green $GreenDark
        $g.FillPath($brush, $path)
        $brush.Dispose()
        $path.Dispose()
    }

    # The note keeps a comfortable margin so it stays readable down to 16 px.
    $inner = $Size * 0.78
    $tile = New-Object System.Drawing.RectangleF (($Size - $inner) / 2), (($Size - $inner) / 2), $inner, $inner
    Draw-Note -G $g -Tile $tile -Color $White

    $g.Dispose()
    return $bmp
}

<#
    Renders the wide tile / splash: dark background, green mark, wordmark.
    The wordmark is auto-fitted so it always stays on a single line.
#>
function New-WideLogo {
    param([int]$Width, [int]$Height, [string]$Title)

    $bmp = New-Object System.Drawing.Bitmap $Width, $Height
    $bmp.SetResolution(96, 96)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $g.Clear($Dark)

    $mark = [int]($Height * 0.46)
    $markX = $Height * 0.06
    $icon = New-AppIcon -Size $mark
    $g.DrawImage($icon, (New-Object System.Drawing.RectangleF $markX, (($Height - $mark) / 2), $mark, $mark))
    $icon.Dispose()

    $textX = $markX + $mark * 1.12
    $available = $Width - $textX - ($Height * 0.05)

    $brush = New-Object System.Drawing.SolidBrush $White
    $format = New-Object System.Drawing.StringFormat
    $format.Alignment = [System.Drawing.StringAlignment]::Near
    $format.LineAlignment = [System.Drawing.StringAlignment]::Center
    $format.FormatFlags = [System.Drawing.StringFormatFlags]::NoWrap
    $format.Trimming = [System.Drawing.StringTrimming]::EllipsisCharacter

    $size = $Height * 0.28
    while ($size -gt 10) {
        $font = New-Object System.Drawing.Font "Segoe UI", $size, ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
        $measured = $g.MeasureString($Title, $font, [int]10000, $format)
        $font.Dispose()
        if ($measured.Width -le $available) { break }
        $size -= 1
    }

    $font = New-Object System.Drawing.Font "Segoe UI", $size, ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
    $textRect = New-Object System.Drawing.RectangleF $textX, 0, $available, $Height
    $g.DrawString($Title, $font, $brush, $textRect, $format)

    $font.Dispose()
    $brush.Dispose()
    $format.Dispose()
    $g.Dispose()
    return $bmp
}

function Save-Png {
    param([System.Drawing.Bitmap]$Bitmap, [string]$Path)
    $Bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    $Bitmap.Dispose()
    Write-Host ("  {0,-52} {1}x{2}" -f (Split-Path $Path -Leaf), $Bitmap.Width, $Bitmap.Height)
}

if (-not (Test-Path -LiteralPath $OutDir)) { throw "Output folder not found: $OutDir" }

Write-Host "Generating logo assets in $OutDir"

# --- square app icon ---------------------------------------------------------
$sizes = @{
    "Square150x150Logo.png"            = 150
    "Square150x150Logo.scale-100.png"  = 150
    "Square150x150Logo.scale-200.png"  = 300
    "Square44x44Logo.png"              = 44
    "Square44x44Logo.scale-100.png"    = 44
    "Square44x44Logo.scale-200.png"    = 88
    "Square44x44Logo.targetsize-16.png" = 16
    "Square44x44Logo.targetsize-24.png" = 24
    "Square44x44Logo.targetsize-32.png" = 32
    "Square44x44Logo.targetsize-48.png" = 48
    "Square44x44Logo.targetsize-256.png" = 256
    "StoreLogo.png"                     = 50
}
foreach ($name in $sizes.Keys) {
    Save-Png (New-AppIcon -Size $sizes[$name]) (Join-Path $OutDir $name)
}

# Unplated variants fill the whole square, so the note is drawn a little larger.
foreach ($name in @(
        "Square44x44Logo.targetsize-16_altform-unplated.png",
        "Square44x44Logo.targetsize-24_altform-unplated.png",
        "Square44x44Logo.targetsize-32_altform-unplated.png",
        "Square44x44Logo.targetsize-48_altform-unplated.png",
        "Square44x44Logo.targetsize-256_altform-unplated.png")) {
    $base = [System.IO.Path]::GetFileNameWithoutExtension($name)
    $size = [int]($base -replace '.*targetsize-(\d+).*', '$1')
    Save-Png (New-AppIcon -Size $size -Unplated) (Join-Path $OutDir $name)
}

# --- wide tile and splash ----------------------------------------------------
Save-Png (New-WideLogo -Width 310 -Height 150 -Title "Scrcpy Music") (Join-Path $OutDir "Wide310x150Logo.png")
Save-Png (New-WideLogo -Width 620 -Height 300 -Title "Scrcpy Music") (Join-Path $OutDir "SplashScreen.png")

Write-Host "Done."
