# Functional test of the inspector: loads the built exe by reflection, reads a JPEG fixture with known tags and checks
# how every tag is categorized, that file-system info is hidden, that Unicode paths work, and that comparing against a
# cleaned copy marks the right tags as removed or kept.
# Run it with Windows PowerShell 5.1 (the exe targets the .NET Framework):
#   powershell -File .\tests\inspector.ps1 -ExifDir C:\path\to\exiftool-13.59_64 [-Exe .\dist\CleanMetadata.exe]
param(
    [Parameter(Mandatory = $true)][string]$ExifDir,
    [string]$Exe = (Join-Path $PSScriptRoot '..\dist\CleanMetadata.exe')
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$exif = @("$ExifDir\exiftool.exe", "$ExifDir\exiftool(-k).exe") | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $exif) { throw "exiftool not found in $ExifDir" }

$asm = [Reflection.Assembly]::LoadFile((Resolve-Path $Exe).Path)
$insT = $asm.GetType('Inspector'); $optsT = $asm.GetType('Opts')
function Read-Tags($path) { return @($insT.GetMethod('Read').Invoke($null, @([string]$path))) }
function Compare-Tags($orig, $clean) {
    $listT = [Collections.Generic.List[object]]
    $m = $insT.GetMethod('Compare')
    $tagT = $asm.GetType('TagInfo'); $lt = [Collections.Generic.List`1].MakeGenericType($tagT)
    $lo = [Activator]::CreateInstance($lt); foreach ($t in $orig) { $lo.Add($t) }
    $lc = [Activator]::CreateInstance($lt); foreach ($t in $clean) { $lc.Add($t) }
    $m.Invoke($null, @($lo, $lc)) | Out-Null
}
function SetPreset($p) { $optsT.GetMethod('Apply').Invoke($null, @($p)) | Out-Null }
function Args($image) { return @($optsT.GetMethod('Build').Invoke($null, @($image))) }

$dir = Join-Path $env:TEMP ('cm_insp_' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $dir | Out-Null
$failed = $false
function Check($ok, $what) { if ($ok) { "PASS  $what" } else { "FAIL  $what"; $script:failed = $true } }
function TagCat($tags, $name) { ($tags | Where-Object { $_.Name -eq $name } | Select-Object -First 1).Cat }
function TagRem($tags, $name) { ($tags | Where-Object { $_.Name -eq $name } | Select-Object -First 1).Removed }

try {
    $base = Join-Path $dir 'base.jpg'
    $bmp = New-Object Drawing.Bitmap 200, 120; $bmp.Save($base, [Drawing.Imaging.ImageFormat]::Jpeg)
    $th = Join-Path $dir 'thumb.jpg'; $t2 = New-Object Drawing.Bitmap 32, 20; $t2.Save($th, [Drawing.Imaging.ImageFormat]::Jpeg); $t2.Dispose(); $bmp.Dispose()
    & $exif -q -overwrite_original -Make=Canon -Model=EOS5 -LensModel=L50 -SerialNumber=12345 -Artist=Me -Copyright=CopyMe -Software=PhotoshopX -ImageDescription=desc `
        '-DateTimeOriginal=2020:01:01 10:00:00' '-CreateDate=2020:01:01 10:00:00' -GPSLatitude=10 -GPSLatitudeRef=N -GPSLongitude=20 -GPSLongitudeRef=E `
        -IPTC:Keywords=kw -IPTC:City=Paris -XMP-dc:Creator=MeX -XMP-xmp:CreatorTool=PhotoshopX -XMP-xmpMM:DocumentID=xmp.did:123 `
        "-ThumbnailImage<=$th" '-Orientation#=6' $base

    $tags = Read-Tags $base
    Check ($tags.Count -gt 20) 'reads the tags of a JPEG'
    Check ((TagCat $tags 'GPSLatitude') -eq 'location') 'GPSLatitude is filed under Location'
    Check ((TagCat $tags 'City') -eq 'location') 'IPTC City is filed under Location'
    Check ((TagCat $tags 'Make') -eq 'device' -and (TagCat $tags 'SerialNumber') -eq 'device') 'Make and SerialNumber are Device'
    Check ((TagCat $tags 'CreateDate') -eq 'dates' -and (TagCat $tags 'DateTimeOriginal') -eq 'dates') 'dates are Dates'
    Check ((TagCat $tags 'Artist') -eq 'author' -and (TagCat $tags 'Keywords') -eq 'author' -and (TagCat $tags 'Creator') -eq 'author') 'Artist, IPTC Keywords and XMP Creator are Author'
    Check ((TagCat $tags 'ThumbnailImage') -eq 'thumbs') 'ThumbnailImage is a thumbnail'
    Check ((TagCat $tags 'Software') -eq 'history' -and (TagCat $tags 'CreatorTool') -eq 'history' -and (TagCat $tags 'DocumentID') -eq 'history') 'software and XMP history tags are History'
    Check ((TagCat $tags 'Orientation') -eq 'other') 'Orientation falls under Other'
    Check (-not ($tags | Where-Object { $_.Name -in 'FileName', 'Directory', 'FileSize', 'ExifToolVersion' })) 'file-system and ExifTool info is hidden'

    # unicode path
    $uni = Join-Path $dir (([string]::Concat([char]0x30C6, [char]0x30B9, [char]0x30C8)) + ' ' + [char]::ConvertFromUtf32(0x1F600) + '.jpg')
    Copy-Item $base -Destination $uni
    Check ((Read-Tags $uni).Count -eq $tags.Count) 'a file name with CJK and emoji can be inspected'

    # a missing file raises an error; a plain text file is readable and simply has no metadata
    $threw = $false; try { Read-Tags (Join-Path $dir 'does-not-exist.jpg') | Out-Null } catch { $threw = $true }
    Check $threw 'a missing file is reported as an error'
    $txt = Join-Path $dir 'notes.jpg'; Set-Content $txt 'not an image'
    Check ((Read-Tags $txt).Count -eq 0) 'a text file shows no metadata'

    # compare with the "all" preset copy
    SetPreset 'all'
    $out = Join-Path $dir 'all.jpg'; $a = Args $true; & $exif -q -o $out @a $base
    $orig = Read-Tags $base; $clean = Read-Tags $out; Compare-Tags $orig $clean
    Check ((TagRem $orig 'Make') -and (TagRem $orig 'GPSLatitude') -and (TagRem $orig 'Artist') -and (TagRem $orig 'Software')) 'all preset: personal tags are marked removed'
    Check (-not (TagRem $orig 'Orientation')) 'all preset: orientation is marked kept'

    # compare with the "privacy" preset copy
    SetPreset 'privacy'
    $out2 = Join-Path $dir 'privacy.jpg'; $a = Args $true; & $exif -q -o $out2 @a $base
    $orig2 = Read-Tags $base; $clean2 = Read-Tags $out2; Compare-Tags $orig2 $clean2
    Check ((TagRem $orig2 'Make') -and (TagRem $orig2 'GPSLatitude') -and (TagRem $orig2 'Artist') -and (TagRem $orig2 'CreateDate')) 'privacy preset: privacy tags are marked removed'
    Check (-not (TagRem $orig2 'Software') -and -not (TagRem $orig2 'CreatorTool')) 'privacy preset: editing software is marked kept'
}
finally {
    Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue
}
if ($failed) { exit 1 } else { 'inspector test passed' }
