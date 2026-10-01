# Builds a single CleanMetadata.exe (WPF + embedded ExifTool) with the csc.exe that ships with Windows.
# Needs the Windows 64-bit ExifTool package (https://exiftool.org): exiftool(-k).exe or exiftool.exe, plus exiftool_files\.
#   .\build.ps1 -ExifDir C:\path\to\exiftool-13.59_64
param(
    [Parameter(Mandatory = $true)][string]$ExifDir,
    [string]$Out = (Join-Path $PSScriptRoot 'dist\CleanMetadata.exe')
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'

$exif = @("$ExifDir\exiftool.exe", "$ExifDir\exiftool(-k).exe") | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $exif -or -not (Test-Path "$ExifDir\exiftool_files")) { throw "exiftool executable / exiftool_files not found in $ExifDir" }
New-Item -ItemType Directory -Force (Split-Path $Out) | Out-Null

$tmp = Join-Path $env:TEMP ('cm_build_' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $tmp | Out-Null
try {
    Copy-Item -LiteralPath $exif "$tmp\exiftool.exe"
    Copy-Item "$ExifDir\exiftool_files" "$tmp\exiftool_files" -Recurse
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($tmp, "$tmp.zip", 'Optimal', $false)
    $a = @('/nologo', '/target:winexe', '/optimize', '/platform:anycpu', '/warn:4', '/warnaserror+', "/lib:$fw\WPF",
        "/win32icon:$root\assets\icon.ico", "/win32manifest:$root\app.manifest",
        "/resource:$root\assets\icon.ico,app.ico", "/resource:$root\assets\logo.png,logo.png", "/resource:$tmp.zip,tools.zip",
        "/out:$Out",
        '/r:PresentationCore.dll', '/r:PresentationFramework.dll', '/r:WindowsBase.dll', '/r:System.Xaml.dll',
        '/r:System.IO.Compression.dll', '/r:System.IO.Compression.FileSystem.dll')
    foreach ($s in Get-ChildItem "$root\assets\sprites\*.png") { $a += "/resource:$($s.FullName),sprite.$($s.Name)" }
    $a += (Get-ChildItem "$root\*.cs").FullName
    & "$fw\csc.exe" @a
    if ($LASTEXITCODE) { throw 'csc failed' }
} finally {
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item "$tmp.zip" -Force -ErrorAction SilentlyContinue
}
Get-Item $Out | Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } }
Get-FileHash $Out -Algorithm SHA256 | Select-Object Algorithm, Hash
