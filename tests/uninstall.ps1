# Tests the built-in uninstaller (--uninstall) on a COPY of the exe: it must remove its data folder, leave the user's
# files alone, and NOT delete the exe itself (a program that deletes itself through a hidden shell trips antivirus
# heuristics, so removing the exe is left to the user).
# Note: it deletes %LOCALAPPDATA%\CleanMetadata (the ExifTool cache is re-created on the next run).
#   .\tests\uninstall.ps1 [-Exe .\dist\CleanMetadata.exe]
param([string]$Exe = (Join-Path $PSScriptRoot '..\dist\CleanMetadata.exe'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
if (-not (Test-Path $Exe)) { throw "exe not found: $Exe" }

$dir = Join-Path $env:TEMP ('cm_uninstall_' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $dir | Out-Null
$copy = Join-Path $dir 'CleanMetadata.exe'
Copy-Item $Exe $copy
$data = Join-Path $env:LOCALAPPDATA 'CleanMetadata'
$failed = $false
function Check($ok, $what) { if ($ok) { "PASS  $what" } else { "FAIL  $what"; $script:failed = $true } }

try {
    # run once on a tiny image so the data folder exists, like a real user's first launch
    $img = Join-Path $dir 'a.jpg'
    $bmp = New-Object Drawing.Bitmap 8, 8; $bmp.Save($img, [Drawing.Imaging.ImageFormat]::Jpeg); $bmp.Dispose()
    $p = Start-Process $copy -ArgumentList ('"' + $img + '"') -PassThru
    $deadline = (Get-Date).AddSeconds(40)
    while (-not (Test-Path (Join-Path $dir 'a_clean.jpg')) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 300 }
    Stop-Process $p -Force; Start-Sleep 1
    Check (Test-Path $data) 'data folder exists after first run'

    $q = Start-Process $copy -ArgumentList '--uninstall' -PassThru
    $q.WaitForExit()
    Check ($q.ExitCode -eq 0) 'uninstall exits cleanly'
    Check (-not (Test-Path $data)) 'data folder removed'
    Check (Test-Path $copy) 'the exe is left for the user to delete'
    Check ((Test-Path $img) -and (Test-Path (Join-Path $dir 'a_clean.jpg'))) 'user files left alone'
}
finally {
    Get-Process CleanMetadata -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 500
    Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue
}
if ($failed) { exit 1 } else { 'uninstall test passed' }
