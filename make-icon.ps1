# Convierte un PNG en assets\icon.ico (16-256 px), assets\icon.png (512) y assets\logo.png (128).
# Recorta los margenes transparentes y vuelve a dibujar el PNG, asi no arrastra ningun metadato del archivo original
# (por ejemplo manifiestos C2PA). Uso: powershell -File make-icon.ps1 -Source ruta\al\icono.png
param(
    [string]$Source = (Join-Path $PSScriptRoot 'assets\icon.png'),
    [string]$OutDir = (Join-Path $PSScriptRoot 'assets')
)
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @"
using System; using System.Drawing; using System.Drawing.Imaging;
public static class Bbox {
  public static Rectangle Find(Bitmap b, int minAlpha) {
    var d = b.LockBits(new Rectangle(0,0,b.Width,b.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
    int minX=b.Width, minY=b.Height, maxX=-1, maxY=-1; byte[] row = new byte[d.Stride];
    for (int y=0; y<b.Height; y++) {
      System.Runtime.InteropServices.Marshal.Copy(d.Scan0 + y*d.Stride, row, 0, d.Stride);
      for (int x=0; x<b.Width; x++) if (row[x*4+3] >= minAlpha) { if (x<minX) minX=x; if (x>maxX) maxX=x; if (y<minY) minY=y; if (y>maxY) maxY=y; }
    }
    b.UnlockBits(d);
    return new Rectangle(minX, minY, maxX-minX+1, maxY-minY+1);
  }
}
"@

New-Item -ItemType Directory -Force $OutDir | Out-Null
# se carga en memoria para no dejar el archivo bloqueado (la salida puede ser el mismo archivo)
$bytes = [IO.File]::ReadAllBytes($Source)
$ms = New-Object IO.MemoryStream (,$bytes)
$src = New-Object Drawing.Bitmap ([Drawing.Image]::FromStream($ms))
$r = [Bbox]::Find($src, 40)
$side = [Math]::Max($r.Width, $r.Height)

function Render([int]$px) {
    $bmp = New-Object Drawing.Bitmap $px,$px,([Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = 'HighQualityBicubic'; $g.SmoothingMode = 'HighQuality'; $g.PixelOffsetMode = 'HighQuality'; $g.CompositingQuality = 'HighQuality'
    $dst = New-Object Drawing.Rectangle ([int](($side - $r.Width) * $px / (2 * $side))), ([int](($side - $r.Height) * $px / (2 * $side))), ([int]($r.Width * $px / $side)), ([int]($r.Height * $px / $side))
    $g.DrawImage($src, $dst, $r.X, $r.Y, $r.Width, $r.Height, [Drawing.GraphicsUnit]::Pixel)
    $g.Dispose(); $bmp
}
function Png($bmp) { $m = New-Object IO.MemoryStream; $bmp.Save($m, [Drawing.Imaging.ImageFormat]::Png); ,$m.ToArray() }

$sizes = 16,24,32,48,64,128,256
$frames = foreach ($s in $sizes) { $b = Render $s; $p = Png $b; $b.Dispose(); ,$p }
$master = Render 512; $masterBytes = Png $master; $master.Dispose()
$logo = Render 128; $logoBytes = Png $logo; $logo.Dispose()
$src.Dispose(); $ms.Dispose()

[IO.File]::WriteAllBytes((Join-Path $OutDir 'icon.png'), $masterBytes)
[IO.File]::WriteAllBytes((Join-Path $OutDir 'logo.png'), $logoBytes)

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
Get-ChildItem $OutDir | Select Name, Length
