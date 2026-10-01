# End-to-end smoke test: launches the built exe on JPEGs with EXIF/GPS data and a non-ANSI file name, then checks
#  1. default preset (all metadata): the _clean copy lost the metadata, kept its orientation, the original is untouched
#  2. "privacy" preset read from settings.ini: personal data is gone but the editing software tag is left alone
#  3. "replace originals" (settings.ini): the original file itself is cleaned, no _clean copy and no temp file is left behind
#   .\tests\smoke.ps1 -ExifDir C:\path\to\exiftool-13.59_64 [-Exe .\dist\CleanMetadata.exe]
param(
    [Parameter(Mandatory = $true)][string]$ExifDir,
    [string]$Exe = (Join-Path $PSScriptRoot '..\dist\CleanMetadata.exe')
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$exif = @("$ExifDir\exiftool.exe", "$ExifDir\exiftool(-k).exe") | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $exif) { throw "exiftool not found in $ExifDir" }
if (-not (Test-Path $Exe)) { throw "exe not found: $Exe" }

$dir = Join-Path $env:TEMP ('cm_smoke_' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $dir | Out-Null
$failed = $false
function Check($ok, $what) { if ($ok) { "PASS  $what" } else { "FAIL  $what"; $script:failed = $true } }

# keep the user's real settings.ini safe: scenario 2 writes its own and restores the original afterwards
$ini = Join-Path $env:LOCALAPPDATA 'CleanMetadata\settings.ini'
$iniBackup = if (Test-Path $ini) { [IO.File]::ReadAllBytes($ini) } else { $null }
if (Test-Path $ini) { Remove-Item -LiteralPath $ini -Force }

# exiftool's plain command-line arguments can't take non-ANSI names, so tags are read from ASCII-named copies
function Tag($path, $arg) {
    $probe = Join-Path $dir 'probe.jpg'
    Copy-Item -LiteralPath $path $probe -Force
    $r = (& $exif -s3 $arg $probe) -join ''
    Remove-Item -LiteralPath $probe -Force
    return $r
}

# fixture: JPEG with an author, editing software, GPS position and a rotated orientation
function NewFixture($name) {
    $plain = Join-Path $dir ('f_' + [guid]::NewGuid().ToString('N') + '.jpg')
    $bmp = New-Object Drawing.Bitmap 64, 48
    $bmp.Save($plain, [Drawing.Imaging.ImageFormat]::Jpeg); $bmp.Dispose()
    & $exif -overwrite_original -Artist=SmokeTest -Software=EditorX -GPSLatitude=10 -GPSLatitudeRef=N -GPSLongitude=20 -GPSLongitudeRef=E '-Orientation#=6' $plain | Out-Null
    $src = Join-Path $dir $name
    Rename-Item -LiteralPath $plain -NewName $name
    return $src
}

function RunApp($src) {
    $out = Join-Path $dir (([IO.Path]::GetFileNameWithoutExtension($src)) + '_clean.jpg')
    $p = Start-Process $Exe -ArgumentList ('"' + $src + '"') -PassThru
    $deadline = (Get-Date).AddSeconds(40)
    while (-not (Test-Path -LiteralPath $out) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 300 }
    Start-Sleep -Seconds 1
    Stop-Process $p -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 700
    return $out
}

try {
    # ---- 1. default preset, non-ANSI name ----
    $name = ([string]::Concat([char]0x30C6, [char]0x30B9, [char]0x30C8)) + ' ' + [char]::ConvertFromUtf32(0x1F600) + ' photo.jpg'
    $src = NewFixture $name
    $out = RunApp $src
    Check (Test-Path -LiteralPath $out) 'default preset: clean copy was created (non-ANSI file name)'
    if (Test-Path -LiteralPath $out) {
        Check ((Tag $out '-ImageWidth') -eq '64') 'default preset: copy is a readable JPEG'
        Check (((Tag $out '-Artist') -eq '') -and ((Tag $out '-GPSLatitude') -eq '') -and ((Tag $out '-Software') -eq '')) 'default preset: all metadata is gone'
        Check ((Tag $out '-Orientation#') -eq '6') 'default preset: orientation was preserved'
    }
    Check ((Tag $src '-Artist') -eq 'SmokeTest') 'default preset: original file is untouched'

    # ---- 2. privacy preset from settings.ini ----
    New-Item -ItemType Directory -Force (Split-Path $ini) | Out-Null
    Set-Content -Path $ini -Value @('preset=privacy', 'keepIcc=1', 'keepOri=1', 'tags=') -Encoding ASCII
    $src2 = NewFixture 'privacy test.jpg'
    $out2 = RunApp $src2
    Check (Test-Path -LiteralPath $out2) 'privacy preset: clean copy was created'
    if (Test-Path -LiteralPath $out2) {
        Check (((Tag $out2 '-Artist') -eq '') -and ((Tag $out2 '-GPSLatitude') -eq '')) 'privacy preset: author and location are gone'
        Check ((Tag $out2 '-Software') -eq 'EditorX') 'privacy preset: editing software tag was left alone'
    }

    # ---- 3. replace originals ----
    Set-Content -Path $ini -Value @('preset=all', 'keepIcc=1', 'keepOri=1', 'tags=', 'replace=1') -Encoding ASCII
    $src3 = NewFixture 'replace test.jpg'
    $p3 = Start-Process $Exe -ArgumentList ('"' + $src3 + '"') -PassThru
    $deadline = (Get-Date).AddSeconds(45); $artist = 'SmokeTest'
    while ($artist -ne '' -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 800; try { $artist = Tag $src3 '-Artist' } catch { $artist = 'SmokeTest' } }
    Start-Sleep -Seconds 1
    Stop-Process $p3 -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 700
    Check ($artist -eq '') 'replace originals: the original file itself was cleaned'
    Check ((Tag $src3 '-GPSLatitude') -eq '') 'replace originals: GPS is gone from the original'
    Check ((Tag $src3 '-Orientation#') -eq '6') 'replace originals: orientation was preserved'
    Check (-not (Test-Path -LiteralPath (Join-Path $dir 'replace test_clean.jpg'))) 'replace originals: no _clean copy was created'
    Check (@(Get-ChildItem -LiteralPath $dir -Filter '*.cm-*').Count -eq 0) 'replace originals: no temporary file was left behind'
}
finally {
    Get-Process CleanMetadata -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 500
    if ($null -ne $iniBackup) { [IO.File]::WriteAllBytes($ini, $iniBackup) } elseif (Test-Path $ini) { Remove-Item -LiteralPath $ini -Force }
    Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue
}
if ($failed) { exit 1 } else { 'smoke test passed' }
