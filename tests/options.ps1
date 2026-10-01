# Functional test of the Options model: loads the built exe by reflection, asks it for the ExifTool arguments of each
# preset, runs ExifTool with them on a JPEG fixture and checks which tags survive.
# Run it with Windows PowerShell 5.1 (the exe targets the .NET Framework):
#   powershell -File .\tests\options.ps1 -ExifDir C:\path\to\exiftool-13.59_64 [-Exe .\dist\CleanMetadata.exe]
param(
    [Parameter(Mandatory = $true)][string]$ExifDir,
    [string]$Exe = (Join-Path $PSScriptRoot '..\dist\CleanMetadata.exe')
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$exif = @("$ExifDir\exiftool.exe", "$ExifDir\exiftool(-k).exe") | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $exif) { throw "exiftool not found in $ExifDir" }

$asm = [Reflection.Assembly]::LoadFile((Resolve-Path $Exe).Path)
$optsT = $asm.GetType('Opts'); $optionT = $asm.GetType('Option')
$items = $optsT.GetField('Items').GetValue($null)
function SetPreset($p) { $optsT.GetMethod('Apply').Invoke($null, @($p)) | Out-Null }
function SetField($name, $v) { $optsT.GetField($name).SetValue($null, $v) }
function SetOn($key, $v) { foreach ($o in $items) { if ($o.Key -eq $key) { $o.On = $v } } }
function Args($image) { return @($optsT.GetMethod('Build').Invoke($null, @($image))) }

$dir = Join-Path $env:TEMP ('cm_opts_' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $dir | Out-Null
$failed = $false
function Check($ok, $what) { if ($ok) { "PASS  $what" } else { "FAIL  $what"; $script:failed = $true } }

# keep the user's real settings.ini safe: the persistence test overwrites it and restores it afterwards
$ini = Join-Path $env:LOCALAPPDATA 'CleanMetadata\settings.ini'
$iniBackup = if (Test-Path $ini) { [IO.File]::ReadAllBytes($ini) } else { $null }

try {
    $base = Join-Path $dir 'base.jpg'
    $bmp = New-Object Drawing.Bitmap 200, 120; $bmp.Save($base, [Drawing.Imaging.ImageFormat]::Jpeg)
    $th = Join-Path $dir 'thumb.jpg'; $t2 = New-Object Drawing.Bitmap 32, 20; $t2.Save($th, [Drawing.Imaging.ImageFormat]::Jpeg); $t2.Dispose(); $bmp.Dispose()
    & $exif -q -overwrite_original -Make=Canon -Model=EOS5 -LensModel=L50 -SerialNumber=12345 -Artist=Me -Copyright=CopyMe -Software=PhotoshopX -ImageDescription=desc `
        '-DateTimeOriginal=2020:01:01 10:00:00' '-CreateDate=2020:01:01 10:00:00' '-ModifyDate=2020:01:02 10:00:00' `
        -GPSLatitude=10 -GPSLatitudeRef=N -GPSLongitude=20 -GPSLongitudeRef=E -IPTC:Keywords=kw -IPTC:City=Paris `
        -XMP-dc:Creator=MeX -XMP-dc:Subject=subj -XMP-xmp:CreatorTool=PhotoshopX -XMP-xmpMM:DocumentID=xmp.did:123 `
        "-ThumbnailImage<=$th" '-Orientation#=6' $base

    function Run($name) {
        $out = Join-Path $dir ($name + '.jpg')
        $a = Args $true
        $r = & $exif -q -o $out @a $base 2>&1
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path $out)) { return $null }
        return (& $exif -s -s -a -G0 $out) | ForEach-Object { ($_ -split '\s*:\s*', 2)[0].Trim() -replace '^\[.*?\]\s*', '' }
    }
    function Has($tags, $names) { foreach ($n in $names) { if ($tags -contains $n) { return $true } }; return $false }

    # all
    SetPreset 'all'
    $t = Run 'all'
    Check ($null -ne $t) 'preset all: exiftool succeeded'
    Check (-not (Has $t 'Make', 'Artist', 'GPSLatitude', 'CreateDate', 'Keywords', 'CreatorTool', 'ThumbnailImage')) 'preset all: metadata is gone'
    Check ($t -contains 'Orientation') 'preset all: orientation kept by default'
    SetField 'KeepOri' $false
    $t = Run 'all_noori'
    Check (-not ($t -contains 'Orientation')) 'preset all: orientation removed when "keep" is off'
    SetField 'KeepOri' $true

    # privacy
    SetPreset 'privacy'
    $t = Run 'privacy'
    Check ($null -ne $t) 'preset privacy: exiftool succeeded'
    Check (-not (Has $t 'GPSLatitude', 'Make', 'Model', 'CreateDate', 'DateTimeOriginal', 'Artist', 'Copyright', 'Keywords', 'ThumbnailImage')) 'preset privacy: location, device, dates, author, thumbnails gone'
    Check (Has $t 'Software', 'CreatorTool', 'DocumentID') 'preset privacy: editing software left alone'

    # provenance
    SetPreset 'provenance'
    $t = Run 'prov'
    Check ($null -ne $t) 'preset provenance: exiftool succeeded'
    Check (-not (Has $t 'Software', 'CreatorTool', 'DocumentID')) 'preset provenance: edit history and software gone'
    Check (Has $t 'Make', 'GPSLatitude', 'Artist') 'preset provenance: personal data left alone'

    # custom: only location
    SetPreset 'custom'
    foreach ($o in $items) { $o.On = $false }
    SetOn 'location' $true
    $t = Run 'custom_loc'
    Check (-not (Has $t 'GPSLatitude', 'GPSLongitude')) 'custom: location removed'
    Check (Has $t 'Make', 'Artist', 'CreateDate') 'custom: everything else kept'

    # custom: extra tags, one of them invalid
    SetField 'TagText' 'Artist, Copyright; bad!tag'
    $mArgs = New-Object object[] 1
    $tagsM = $optsT.GetMethod('Tags')
    $res = $tagsM.Invoke($null, $mArgs)
    Check (($res -contains 'Artist') -and ($res -contains 'Copyright')) 'custom tags: valid names are accepted'
    Check ($mArgs[0].Count -eq 1 -and $mArgs[0][0] -eq 'bad!tag') 'custom tags: invalid name is rejected, not passed on'
    $t = Run 'custom_tags'
    Check (-not (Has $t 'Artist', 'Copyright')) 'custom tags: Artist and Copyright removed'
    Check (-not (Args $true | Where-Object { $_ -like '*bad*' })) 'custom tags: rejected text never reaches ExifTool'

    # nothing selected
    SetField 'TagText' ''
    foreach ($o in $items) { $o.On = $false }
    Check ((Args $true).Count -eq 0) 'nothing selected: no arguments (the app reports an error instead of copying the file)'

    # C2PA flag
    SetPreset 'privacy'; Check (-not $optsT.GetMethod('WantsC2pa').Invoke($null, @())) 'privacy: C2PA check not requested'
    SetPreset 'all'; Check ($optsT.GetMethod('WantsC2pa').Invoke($null, @())) 'all: C2PA check requested'

    # persistence round trip
    SetPreset 'custom'; foreach ($o in $items) { $o.On = $false }; SetOn 'dates' $true; SetOn 'c2pa' $true; SetField 'TagText' 'Software'; SetField 'Replace' $true
    $optsT.GetMethod('Save').Invoke($null, @()) | Out-Null
    SetPreset 'all'; SetField 'TagText' ''; SetField 'Replace' $false
    $optsT.GetMethod('Load').Invoke($null, @()) | Out-Null
    $on = ($items | Where-Object { $_.On } | ForEach-Object { $_.Key }) -join ','
    Check (($optsT.GetField('Preset').GetValue($null) -eq 'custom') -and ($on -eq 'dates,c2pa') -and ($optsT.GetField('TagText').GetValue($null) -eq 'Software') -and ($optsT.GetField('Replace').GetValue($null) -eq $true)) 'settings survive a save and load (including Replace originals)'
}
finally {
    if ($null -ne $iniBackup) { [IO.File]::WriteAllBytes($ini, $iniBackup) } elseif (Test-Path $ini) { Remove-Item -LiteralPath $ini -Force }
    Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue
}
if ($failed) { exit 1 } else { 'options test passed' }
