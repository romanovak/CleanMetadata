# Prepares the pixel-art sprites used by the cleaning and inspector animations from four source PNGs (document with
# metadata lines, brush, magnifier, dust particles). For each one it
#   - crops to the artwork and re-samples it onto its real pixel grid, so it stays crisp when scaled up with nearest neighbor,
#   - re-draws everything into fresh bitmaps, so no metadata from the source files (for example C2PA manifests) survives,
#   - derives a clean document (lines removed) and splits the particle sheet into one PNG per particle.
#   powershell -File make-sprites.ps1 -Doc Sheet.png -Brush Brush.png -Lupa Lupa.png -Data Data.png
param(
    [Parameter(Mandatory = $true)][string]$Doc,
    [Parameter(Mandatory = $true)][string]$Brush,
    [Parameter(Mandatory = $true)][string]$Lupa,
    [Parameter(Mandatory = $true)][string]$Data,
    [string]$OutDir
)
$ErrorActionPreference = 'Stop'
if (-not $OutDir) { $OutDir = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'assets\sprites' }
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @"
using System; using System.Drawing; using System.Drawing.Imaging; using System.Collections.Generic;
public static class Sprites {
  static byte[] Bytes(Bitmap b, out int stride) {
    var d = b.LockBits(new Rectangle(0,0,b.Width,b.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
    stride = d.Stride; var a = new byte[d.Stride * b.Height];
    System.Runtime.InteropServices.Marshal.Copy(d.Scan0, a, 0, a.Length); b.UnlockBits(d); return a;
  }
  public static int[] Bbox(Bitmap b) {
    int st; var a = Bytes(b, out st);
    int minX=b.Width, minY=b.Height, maxX=-1, maxY=-1;
    for (int y=0;y<b.Height;y++) for (int x=0;x<b.Width;x++) if (a[y*st+x*4+3] >= 40) { if(x<minX)minX=x; if(x>maxX)maxX=x; if(y<minY)minY=y; if(y>maxY)maxY=y; }
    return new int[]{ minX, minY, maxX-minX+1, maxY-minY+1 };
  }
  // size of one art pixel = most common length of horizontal runs of identical color
  public static int BlockSize(Bitmap b) {
    int st; var a = Bytes(b, out st); var hist = new Dictionary<int,int>();
    for (int y=0;y<b.Height;y+=7) {
      int run=1;
      for (int x=1;x<b.Width;x++) {
        int i=y*st+x*4, j=i-4;
        bool same = Math.Abs(a[i]-a[j])<10 && Math.Abs(a[i+1]-a[j+1])<10 && Math.Abs(a[i+2]-a[j+2])<10 && Math.Abs(a[i+3]-a[j+3])<10;
        if (same) run++; else { if (run>=8 && run<=60) { int v; hist.TryGetValue(run, out v); hist[run]=v+1; } run=1; }
      }
    }
    int best=0, bc=0; foreach (var kv in hist) if (kv.Value>bc) { bc=kv.Value; best=kv.Key; } return best;
  }
  // crop and take the center of every art pixel
  public static Bitmap Sample(Bitmap src, int[] r, int gw, int gh) {
    var o = new Bitmap(gw, gh, PixelFormat.Format32bppArgb);
    for (int j=0;j<gh;j++) for (int i=0;i<gw;i++)
      o.SetPixel(i, j, src.GetPixel(r[0] + (int)((i+0.5)*r[2]/gw), r[1] + (int)((j+0.5)*r[3]/gh)));
    return o;
  }
  public static List<Rectangle> Components(Bitmap b) {
    var seen = new bool[b.Width, b.Height]; var list = new List<Rectangle>();
    for (int y=0;y<b.Height;y++) for (int x=0;x<b.Width;x++) {
      if (seen[x,y] || b.GetPixel(x,y).A < 40) continue;
      var q = new Stack<Point>(); q.Push(new Point(x,y)); seen[x,y]=true; int x0=x,y0=y,x1=x,y1=y;
      while (q.Count>0) { var p=q.Pop(); if(p.X<x0)x0=p.X; if(p.X>x1)x1=p.X; if(p.Y<y0)y0=p.Y; if(p.Y>y1)y1=p.Y;
        for (int dy=-1;dy<=1;dy++) for (int dx=-1;dx<=1;dx++) { int nx=p.X+dx, ny=p.Y+dy;
          if (nx>=0&&ny>=0&&nx<b.Width&&ny<b.Height&&!seen[nx,ny]&&b.GetPixel(nx,ny).A>=40) { seen[nx,ny]=true; q.Push(new Point(nx,ny)); } } }
      list.Add(new Rectangle(x0,y0,x1-x0+1,y1-y0+1));
    }
    return list;
  }
  public static Bitmap Crop(Bitmap b, Rectangle r) {
    var o = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
    for (int j=0;j<r.Height;j++) for (int i=0;i<r.Width;i++) o.SetPixel(i, j, b.GetPixel(r.X+i, r.Y+j));
    return o;
  }
}
"@

function Grid($path) {
    # load into memory so the source file is never locked
    $ms = New-Object IO.MemoryStream (, [IO.File]::ReadAllBytes($path))
    $src = New-Object Drawing.Bitmap ([Drawing.Image]::FromStream($ms))
    $r = [Sprites]::Bbox($src); $bs = [Sprites]::BlockSize($src)
    $gw = [int][Math]::Round($r[2] / $bs); $gh = [int][Math]::Round($r[3] / $bs)
    $sp = [Sprites]::Sample($src, $r, $gw, $gh)
    $src.Dispose(); $ms.Dispose()
    return $sp
}

New-Item -ItemType Directory -Force $OutDir | Out-Null
$bDoc = Grid $Doc; $bBrush = Grid $Brush; $bLupa = Grid $Lupa; $bData = Grid $Data
$bDoc.Save((Join-Path $OutDir 'doc.png'), [Drawing.Imaging.ImageFormat]::Png)
$bBrush.Save((Join-Path $OutDir 'brush.png'), [Drawing.Imaging.ImageFormat]::Png)
$bLupa.Save((Join-Path $OutDir 'lupa.png'), [Drawing.Imaging.ImageFormat]::Png)

# clean document: paint the interior fill color over the area that holds the metadata lines
$fill = $bDoc.GetPixel([int]($bDoc.Width * 0.45), [int]($bDoc.Height * 0.25))
$bClean = New-Object Drawing.Bitmap $bDoc
$x0 = [int]($bDoc.Width * 0.18); $x1 = [int]($bDoc.Width * 0.80); $y0 = [int]($bDoc.Height * 0.34); $y1 = [int]($bDoc.Height * 0.86)
for ($y = $y0; $y -lt $y1; $y++) { for ($x = $x0; $x -lt $x1; $x++) { $bClean.SetPixel($x, $y, $fill) } }
$bClean.Save((Join-Path $OutDir 'doc_clean.png'), [Drawing.Imaging.ImageFormat]::Png)

# one PNG per dust particle, biggest first
$n = 0
foreach ($rect in ([Sprites]::Components($bData) | Sort-Object { $_.Width * $_.Height } -Descending)) {
    $p = [Sprites]::Crop($bData, $rect)
    $p.Save((Join-Path $OutDir ("p$n.png")), [Drawing.Imaging.ImageFormat]::Png); $p.Dispose(); $n++
}
"doc {0}x{1}  brush {2}x{3}  lupa {4}x{5}  particles {6}  (metadata-line area x {7}-{8}, y {9}-{10})" -f $bDoc.Width, $bDoc.Height, $bBrush.Width, $bBrush.Height, $bLupa.Width, $bLupa.Height, $n, $x0, $x1, $y0, $y1
foreach ($b in $bDoc, $bClean, $bBrush, $bLupa, $bData) { $b.Dispose() }
Get-ChildItem $OutDir | Select-Object Name, Length