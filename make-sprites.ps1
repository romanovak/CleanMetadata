# Builds the pixel-art sprites for the cleaning and inspector animations from the vector logo (assets\logo.svg), so they
# stay in the same style as the logo and no raster artwork is involved:
#   doc.png, doc_clean.png   the document with and without its metadata lines
#   brush.png                the broom
#   lupa.png                 a magnifier drawn with the same rounded geometry (it is not part of the logo)
#   spark3a/b.png, spark5a/b.png   tiny plus-shaped sparkles used as dust (lime and cyan)
# Each shape is rendered at 8x, reduced to whole art pixels (12 logo units per pixel), colored with the lime-to-cyan
# gradient and given a 1 px dark outline. The numbers printed at the end are the constants PixelStage.cs uses.
#   powershell -STA -File make-sprites.ps1
param([string]$Svg, [string]$OutDir)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $Svg) { $Svg = Join-Path $here 'assets\logo.svg' }
if (-not $OutDir) { $OutDir = Join-Path $here 'assets\sprites' }
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase, System.Drawing
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @"
using System; using System.Drawing;
public static class Px {
  public static bool[,] Mask(byte[] bgra, int stride, int W, int H, int S, double thr) {
    var m = new bool[W, H];
    for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) {
      double a = 0;
      for (int j = 0; j < S; j++) for (int i = 0; i < S; i++) a += bgra[(y * S + j) * stride + (x * S + i) * 4 + 3];
      m[x, y] = a / (255.0 * S * S) >= thr;
    }
    return m;
  }
  public static bool[,] Or(bool[,] a, bool[,] b) {
    int W = a.GetLength(0), H = a.GetLength(1); var m = new bool[W, H];
    for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) m[x, y] = a[x, y] || b[x, y];
    return m;
  }
  public static void Paint(Bitmap b, bool[,] m, int[] c1, int[] c2, int steps) {
    int W = b.Width, H = b.Height;
    for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) if (m[x, y]) {
      double t = (x + y) / (double)(W + H - 2); t = Math.Floor(t * steps) / Math.Max(1, steps - 1); if (t > 1) t = 1;
      b.SetPixel(x, y, Color.FromArgb(255, (int)(c1[0] + (c2[0] - c1[0]) * t), (int)(c1[1] + (c2[1] - c1[1]) * t), (int)(c1[2] + (c2[2] - c1[2]) * t)));
    }
  }
  public static void Fill(Bitmap b, bool[,] m, int r, int g, int bl) {
    for (int y = 0; y < b.Height; y++) for (int x = 0; x < b.Width; x++) if (m[x, y]) b.SetPixel(x, y, Color.FromArgb(255, r, g, bl));
  }
  public static void Outline(Bitmap b, int r, int g, int bl) {
    int W = b.Width, H = b.Height; var add = new bool[W, H];
    for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) if (b.GetPixel(x, y).A == 0) {
      if ((x > 0 && b.GetPixel(x - 1, y).A > 0) || (x < W - 1 && b.GetPixel(x + 1, y).A > 0) || (y > 0 && b.GetPixel(x, y - 1).A > 0) || (y < H - 1 && b.GetPixel(x, y + 1).A > 0)) add[x, y] = true;
    }
    for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) if (add[x, y]) b.SetPixel(x, y, Color.FromArgb(255, r, g, bl));
  }
}
"@

$inv = [Globalization.CultureInfo]::InvariantCulture
function Num($v) { [double]::Parse($v, $inv) }
[xml]$doc = Get-Content -LiteralPath $Svg -Raw -Encoding UTF8
function Shape-Geometry($e) {
    switch ($e.LocalName) {
        'path' { [Windows.Media.Geometry]::Parse($e.GetAttribute('d')) }
        'circle' { $r = Num $e.GetAttribute('r'); New-Object Windows.Media.EllipseGeometry (New-Object Windows.Point (Num $e.GetAttribute('cx')), (Num $e.GetAttribute('cy'))), $r, $r }
        'rect' { New-Object Windows.Media.RectangleGeometry (New-Object Windows.Rect (Num $e.GetAttribute('x')), (Num $e.GetAttribute('y')), (Num $e.GetAttribute('width')), (Num $e.GetAttribute('height'))), (Num $e.GetAttribute('rx')), (Num $e.GetAttribute('ry')) }
    }
}
function Group-Geometry($id) {
    $g = New-Object Windows.Media.GeometryGroup
    foreach ($e in $doc.SelectSingleNode("//*[@id='$id']").ChildNodes) { if ($e.NodeType -eq 'Element') { $g.Children.Add((Shape-Geometry $e)) } }
    $g
}

$u = 12.0     # logo units per art pixel
$S = 8        # supersampling

# renders geometry into a W x H cell grid (origin ox,oy in logo units) and returns the coverage mask
function Layer-Mask($geom, $ox, $oy, $W, $H, $pen) {
    $dv = New-Object Windows.Media.DrawingVisual; $dc = $dv.RenderOpen()
    $m = New-Object Windows.Media.Matrix; $m.Translate(-$ox, -$oy); $m.Scale($S / $u, $S / $u)
    $dc.PushTransform((New-Object Windows.Media.MatrixTransform $m))
    $fillBrush = if ($pen) { $null } else { [Windows.Media.Brushes]::Black }   # a pen means "stroke only" (rings, handles)
    $dc.DrawGeometry($fillBrush, $pen, $geom)
    $dc.Pop(); $dc.Close()
    $rtb = New-Object Windows.Media.Imaging.RenderTargetBitmap ($W * $S), ($H * $S), 96, 96, ([Windows.Media.PixelFormats]::Pbgra32)
    $rtb.Render($dv)
    $stride = $W * $S * 4; $buf = New-Object byte[] ($stride * $H * $S)
    $rtb.CopyPixels($buf, $stride, 0)
    , [Px]::Mask($buf, $stride, $W, $H, $S, 0.5)
}
function New-Sprite($W, $H) { New-Object Drawing.Bitmap ([int]$W), ([int]$H), ([Drawing.Imaging.PixelFormat]::Format32bppArgb) }
$lime = 212, 255, 74; $cyan = 111, 243, 255; $ink = 5, 18, 27
New-Item -ItemType Directory -Force $OutDir | Out-Null
Get-ChildItem $OutDir -Filter *.png -ErrorAction SilentlyContinue | ForEach-Object { [IO.File]::Delete($_.FullName) }
function Save($bmp, $name) { $bmp.Save((Join-Path $OutDir "$name.png"), [Drawing.Imaging.ImageFormat]::Png) }

# ---- document ----
$docGeom = Group-Geometry 'doc'; $db = $docGeom.Bounds
$ox = $db.X - $u; $oy = $db.Y - $u
$dW = [int][Math]::Ceiling(($db.Width + 2 * $u) / $u); $dH = [int][Math]::Ceiling(($db.Height + 2 * $u) / $u)
# body: the page with its folded corner
$fold = 165
$bodyPath = ("M{0},{1} L{2},{1} L{3},{4} L{3},{5} L{0},{5} Z" -f $db.X.ToString($inv), $db.Y.ToString($inv), ($db.Right - $fold).ToString($inv), $db.Right.ToString($inv), ($db.Y + $fold).ToString($inv), $db.Bottom.ToString($inv))
$body = [Windows.Media.Geometry]::Parse($bodyPath)
$mBody = Layer-Mask $body $ox $oy $dW $dH $null
$mOut = Layer-Mask $docGeom $ox $oy $dW $dH $null
$bullets = @(); $bars = @()
foreach ($e in $doc.SelectSingleNode("//*[@id='lines']").ChildNodes) { if ($e.NodeType -eq 'Element') { if ($e.LocalName -eq 'circle') { $bullets += , $e } else { $bars += , $e } } }
$bulletColors = @(@(212, 255, 74), @(200, 243, 228), @(191, 234, 240))
$docClean = New-Sprite $dW $dH
[Px]::Fill($docClean, $mBody, 6, 20, 31); [Px]::Paint($docClean, $mOut, $lime, $cyan, 7); [Px]::Outline($docClean, $ink[0], $ink[1], $ink[2])
$docFull = New-Object Drawing.Bitmap $docClean
for ($i = 0; $i -lt 3; $i++) {
    $mb = Layer-Mask (Shape-Geometry $bars[$i]) $ox $oy $dW $dH $null; [Px]::Fill($docFull, $mb, 127, 163, 168)
    $mc = Layer-Mask (Shape-Geometry $bullets[$i]) $ox $oy $dW $dH $null; [Px]::Fill($docFull, $mc, $bulletColors[$i][0], $bulletColors[$i][1], $bulletColors[$i][2])
}
Save $docFull 'doc'
Save $docClean 'doc_clean'
$lb = (Group-Geometry 'lines').Bounds
$barRows = $bars | ForEach-Object { [Math]::Round((((Num $_.GetAttribute('y')) + (Num $_.GetAttribute('height')) / 2) - $oy) / $u, 1) }

# ---- broom ----
$bg = Group-Geometry 'broom'; $bb = $bg.Bounds
$box = $bb.X - $u; $boy = $bb.Y - $u
$bW = [int][Math]::Ceiling(($bb.Width + 2 * $u) / $u); $bH = [int][Math]::Ceiling(($bb.Height + 2 * $u) / $u)
$mBr = Layer-Mask $bg $box $boy $bW $bH $null
$brush = New-Sprite $bW $bH
[Px]::Paint($brush, $mBr, $lime, $cyan, 7); [Px]::Outline($brush, $ink[0], $ink[1], $ink[2])
Save $brush 'brush'
$tipSvgX = $bb.X + $bb.Width * 0.22; $tipSvgY = $bb.Bottom - 8

# ---- magnifier (same rounded geometry as the logo) ----
$ring = New-Object Windows.Media.EllipseGeometry (New-Object Windows.Point 250, 250), 182, 182
$handle = New-Object Windows.Media.LineGeometry (New-Object Windows.Point 379, 379), (New-Object Windows.Point 566, 566)
$penRing = New-Object Windows.Media.Pen ([Windows.Media.Brushes]::Black), 58
$penHandle = New-Object Windows.Media.Pen ([Windows.Media.Brushes]::Black), 84; $penHandle.StartLineCap = 'Round'; $penHandle.EndLineCap = 'Round'
$lox = 250 - 182 - 29 - $u; $loy = $lox
$lW = [int][Math]::Ceiling((566 + 42 - $lox + $u) / $u); $lH = $lW
$mRing = Layer-Mask $ring $lox $loy $lW $lH $penRing; $mHandle = Layer-Mask $handle $lox $loy $lW $lH $penHandle
$mAll = [Px]::Or($mRing, $mHandle)
$lupa = New-Sprite $lW $lH
[Px]::Paint($lupa, $mAll, $lime, $cyan, 7); [Px]::Outline($lupa, $ink[0], $ink[1], $ink[2])
Save $lupa 'lupa'

# ---- sparkles: tiny plus shapes (3x3 and 5x5) in lime and cyan, with a bright center ----
function Spark($n, $col, $name) {
    $b = New-Sprite $n $n; $c = [int](($n - 1) / 2)
    $col2 = [Drawing.Color]::FromArgb(255, $col[0], $col[1], $col[2])
    for ($i = 0; $i -lt $n; $i++) { $b.SetPixel($c, $i, $col2); $b.SetPixel($i, $c, $col2) }
    $b.SetPixel($c, $c, [Drawing.Color]::FromArgb(255, 245, 255, 250))
    Save $b $name
}
Spark 3 $lime 'spark3a'; Spark 3 $cyan 'spark3b'; Spark 5 $lime 'spark5a'; Spark 5 $cyan 'spark5b'

"doc {0}x{1}  lines area cols {2:N1}-{3:N1} rows {4:N1}-{5:N1}  bar centers (rows) {6}" -f $dW, $dH, (($lb.X - $ox) / $u), (($lb.Right - $ox) / $u), (($lb.Y - $oy) / $u), (($lb.Bottom - $oy) / $u), ($barRows -join ', ')
"brush {0}x{1}  tip ({2:N1},{3:N1})" -f $bW, $bH, (($tipSvgX - $box) / $u), (($tipSvgY - $boy) / $u)
"lupa {0}x{1}  lens center ({2:N1},{3:N1})" -f $lW, $lH, ((250 - $lox) / $u), ((250 - $loy) / $u)
Get-ChildItem $OutDir | Select-Object Name, Length
