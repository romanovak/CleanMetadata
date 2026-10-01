# Builds every brand asset from one vector logo:
#   assets\logo.svg, assets\logo-white.svg   clean single-color SVGs (black / white), square viewBox
#   assets\icon.ico, assets\icon.png         app icon: dark rounded tile with the gradient glyph (16-256 px and 512)
#   assets\logo.png                          128 px tile used in the app header
#   assets\social-preview.png                1280x640 image for the GitHub repository
#
# With -Source it first imports a logo exported from a vector editor. Only the visible vector shapes are kept, so the
# editor's XMP block, generator comment, hidden layers and any embedded raster images are dropped and nothing from the
# source file's metadata survives. The shapes are expected in this order (the order of the original export):
#   bristles, handle, ferrule, 3 bullets, 3 line bars, 3 sparkles, document outline.
#   powershell -STA -File make-brand.ps1 -Source path\to\logo.svg      (import + build)
#   powershell -STA -File make-brand.ps1                               (rebuild from assets\logo.svg)
param([string]$Source, [string]$OutDir)
$ErrorActionPreference = 'Stop'
if (-not $OutDir) { $OutDir = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'assets' }
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase
New-Item -ItemType Directory -Force $OutDir | Out-Null
$inv = [Globalization.CultureInfo]::InvariantCulture
function Num($v) { [double]::Parse($v, $inv) }
function F($v) { ([double]$v).ToString('0.##', $inv) }

# ---------------- import ----------------
if ($Source) {
    [xml]$raw = Get-Content -LiteralPath $Source -Raw -Encoding UTF8
    $vis = $raw.DocumentElement.ChildNodes | Where-Object { $_.NodeType -eq 'Element' -and $_.LocalName -eq 'g' -and $_.GetAttribute('class') -ne 'st0' -and $_.SelectNodes('.//*[local-name()="image"]').Count -eq 0 } | Select-Object -First 1
    if (-not $vis) { throw 'no visible vector group found in the source SVG' }
    $shapes = @($vis.SelectNodes('.//*[local-name()="path" or local-name()="circle" or local-name()="rect"]'))
    if ($shapes.Count -ne 13) { throw "expected 13 shapes, found $($shapes.Count); adjust the role mapping in make-brand.ps1" }
    function Elem($e) {
        switch ($e.LocalName) {
            'path' { '<path d="' + $e.GetAttribute('d') + '"/>' }
            'circle' { '<circle cx="' + $e.GetAttribute('cx') + '" cy="' + $e.GetAttribute('cy') + '" r="' + $e.GetAttribute('r') + '"/>' }
            'rect' { '<rect x="' + $e.GetAttribute('x') + '" y="' + $e.GetAttribute('y') + '" width="' + $e.GetAttribute('width') + '" height="' + $e.GetAttribute('height') + '" rx="' + $e.GetAttribute('rx') + '" ry="' + $e.GetAttribute('ry') + '"/>' }
        }
    }
    $grp = @{ broom = 0..2; lines = 3..8; sparks = 9..11; doc = 12..12 }
    # square viewBox around the artwork
    $all = New-Object Windows.Media.GeometryGroup
    foreach ($e in $shapes) {
        switch ($e.LocalName) {
            'path' { $all.Children.Add([Windows.Media.Geometry]::Parse($e.GetAttribute('d'))) }
            'circle' { $r = Num $e.GetAttribute('r'); $all.Children.Add((New-Object Windows.Media.EllipseGeometry (New-Object Windows.Point (Num $e.GetAttribute('cx')), (Num $e.GetAttribute('cy'))), $r, $r)) }
            'rect' { $all.Children.Add((New-Object Windows.Media.RectangleGeometry (New-Object Windows.Rect (Num $e.GetAttribute('x')), (Num $e.GetAttribute('y')), (Num $e.GetAttribute('width')), (Num $e.GetAttribute('height'))))) }
        }
    }
    $b = $all.Bounds; $side = [Math]::Max($b.Width, $b.Height) + 80
    $vx = $b.X + $b.Width / 2 - $side / 2; $vy = $b.Y + $b.Height / 2 - $side / 2
    $nl = "`n"
    $svg = '<svg xmlns="http://www.w3.org/2000/svg" viewBox="' + (F $vx) + ' ' + (F $vy) + ' ' + (F $side) + ' ' + (F $side) + '" role="img" aria-label="CleanMetadata">' + $nl + '  <g fill="#000">' + $nl
    foreach ($name in 'doc', 'lines', 'broom', 'sparks') {
        $svg += '    <g id="' + $name + '">' + $nl
        foreach ($i in $grp[$name]) { $svg += '      ' + (Elem $shapes[$i]) + $nl }
        $svg += '    </g>' + $nl
    }
    $svg += '  </g>' + $nl + '</svg>' + $nl
    [IO.File]::WriteAllText((Join-Path $OutDir 'logo.svg'), $svg, (New-Object Text.UTF8Encoding($false)))
}

$logoPath = Join-Path $OutDir 'logo.svg'
$svgText = [IO.File]::ReadAllText($logoPath)
[IO.File]::WriteAllText((Join-Path $OutDir 'logo-white.svg'), $svgText.Replace('fill="#000"', 'fill="#fff"'), (New-Object Text.UTF8Encoding($false)))

# ---------------- geometry of the clean logo ----------------
[xml]$doc = $svgText
$glyph = New-Object Windows.Media.GeometryGroup
foreach ($e in $doc.SelectNodes('//*[local-name()="path" or local-name()="circle" or local-name()="rect"]')) {
    switch ($e.LocalName) {
        'path' { $glyph.Children.Add([Windows.Media.Geometry]::Parse($e.GetAttribute('d'))) }
        'circle' { $r = Num $e.GetAttribute('r'); $glyph.Children.Add((New-Object Windows.Media.EllipseGeometry (New-Object Windows.Point (Num $e.GetAttribute('cx')), (Num $e.GetAttribute('cy'))), $r, $r)) }
        'rect' { $glyph.Children.Add((New-Object Windows.Media.RectangleGeometry (New-Object Windows.Rect (Num $e.GetAttribute('x')), (Num $e.GetAttribute('y')), (Num $e.GetAttribute('width')), (Num $e.GetAttribute('height'))), (Num $e.GetAttribute('rx')), (Num $e.GetAttribute('ry')))) }
    }
}
$gb = $glyph.Bounds

function Color($hex) { [Windows.Media.ColorConverter]::ConvertFromString($hex) }
function Brush($hex) { $b = New-Object Windows.Media.SolidColorBrush (Color $hex); $b.Freeze(); $b }
function GradBrush() {
    $g = New-Object Windows.Media.LinearGradientBrush (Color '#D4FF4A'), (Color '#6FF3FF'), (New-Object Windows.Point 0, 0), (New-Object Windows.Point 1, 1)
    $g.Freeze(); $g
}

# draws the glyph centered in a square of `size` px, occupying `fraction` of it
function Draw-Glyph($dc, $size, $fraction, $brush) {
    $s = $size * $fraction / [Math]::Max($gb.Width, $gb.Height)
    $m = New-Object Windows.Media.Matrix
    $m.Translate(-($gb.X + $gb.Width / 2), -($gb.Y + $gb.Height / 2)); $m.Scale($s, $s); $m.Translate($size / 2, $size / 2)
    $dc.PushTransform((New-Object Windows.Media.MatrixTransform $m))
    $dc.DrawGeometry($brush, $null, $glyph)
    $dc.Pop()
}

function Render-Tile($size) {
    $dv = New-Object Windows.Media.DrawingVisual; $dc = $dv.RenderOpen()
    $r = $size * 0.225
    $bg = New-Object Windows.Media.LinearGradientBrush (Color '#1F1F26'), (Color '#0B0B0D'), (New-Object Windows.Point 0, 0), (New-Object Windows.Point 1, 1)
    $pen = New-Object Windows.Media.Pen (New-Object Windows.Media.SolidColorBrush ([Windows.Media.Color]::FromArgb(90, 212, 255, 74))), ([Math]::Max(1, $size / 170))
    $inset = $pen.Thickness / 2
    $dc.DrawRoundedRectangle($bg, $pen, (New-Object Windows.Rect $inset, $inset, ($size - 2 * $inset), ($size - 2 * $inset)), $r, $r)
    Draw-Glyph $dc $size 0.62 (GradBrush)
    $dc.Close()
    $rtb = New-Object Windows.Media.Imaging.RenderTargetBitmap $size, $size, 96, 96, ([Windows.Media.PixelFormats]::Pbgra32)
    $rtb.Render($dv); $rtb
}
function Png-Bytes($bmp) { $enc = New-Object Windows.Media.Imaging.PngBitmapEncoder; $enc.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bmp)); $ms = New-Object IO.MemoryStream; $enc.Save($ms); , $ms.ToArray() }

# ---------------- icon ----------------
$sizes = 16, 24, 32, 48, 64, 128, 256
$frames = foreach ($s in $sizes) { , (Png-Bytes (Render-Tile $s)) }
[IO.File]::WriteAllBytes((Join-Path $OutDir 'icon.png'), (Png-Bytes (Render-Tile 512)))
[IO.File]::WriteAllBytes((Join-Path $OutDir 'logo.png'), (Png-Bytes (Render-Tile 128)))
$fs = [IO.File]::Create((Join-Path $OutDir 'icon.ico')); $w = New-Object IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$off = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $d = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$d); $w.Write([byte]$d); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$frames[$i].Length); $w.Write([uint32]$off)
    $off += $frames[$i].Length
}
foreach ($f in $frames) { $w.Write($f) }
$w.Close(); $fs.Close()

# ---------------- GitHub social preview (1280x640) ----------------
$W = 1280; $H = 640
$dv = New-Object Windows.Media.DrawingVisual; $dc = $dv.RenderOpen()
$dc.DrawRectangle((Brush '#0B0B0D'), $null, (New-Object Windows.Rect 0, 0, $W, $H))
$dot = Brush '#17FFFFFF'
for ($y = 18; $y -lt $H; $y += 32) { for ($x = 18; $x -lt $W; $x += 32) { $dc.DrawEllipse($dot, $null, (New-Object Windows.Point $x, $y), 1.2, 1.2) } }
$dc.PushTransform((New-Object Windows.Media.TranslateTransform 150, ($H / 2 - 190)))
Draw-Glyph $dc 380 1.0 (Brush '#FFFFFF')
$dc.Pop()
$face = New-Object Windows.Media.Typeface 'Segoe UI Semibold'
$mono = New-Object Windows.Media.Typeface 'Cascadia Mono, Consolas'
function Text($s, $tf, $sz, $hex) { New-Object Windows.Media.FormattedText $s, $inv, ([Windows.FlowDirection]::LeftToRight), $tf, $sz, (Brush $hex), 1.0 }
$dc.DrawText((Text 'CleanMetadata' $face 92 '#FFFFFF'), (New-Object Windows.Point 590, 218))
$dc.DrawText((Text 'A simple GUI for ExifTool' $face 36 '#A9A9B3'), (New-Object Windows.Point 594, 330))
$dc.DrawText((Text 'strip EXIF, XMP and C2PA from videos and photos' $mono 22 '#74747F'), (New-Object Windows.Point 596, 392))
$dc.Close()
$rtb = New-Object Windows.Media.Imaging.RenderTargetBitmap $W, $H, 96, 96, ([Windows.Media.PixelFormats]::Pbgra32)
$rtb.Render($dv)
[IO.File]::WriteAllBytes((Join-Path $OutDir 'social-preview.png'), (Png-Bytes $rtb))

Get-ChildItem $OutDir -File | Select-Object Name, Length
