<#
.SYNOPSIS
    Downloads the public, GPL-licensed binaries needed to build without a modded game install:
      - MSCLoader 1.4.2 (MSCLoader.dll + Newtonsoft.Json, 0Harmony, ...)  https://github.com/piotrulos/MSCModLoader
      - original MOP 3.10.4 (MOP.dll, only used as metadata input for the stub generator)  https://github.com/Athlon007/MOP
    Files go to refs/cache. The script is idempotent.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
Add-Type -AssemblyName System.IO.Compression.FileSystem

$RefsDir  = Split-Path -Parent $MyInvocation.MyCommand.Path
$CacheDir = Join-Path $RefsDir 'cache'
New-Item -ItemType Directory -Force -Path $CacheDir | Out-Null

function Step([string]$m) { Write-Host "[deps] $m" -ForegroundColor Cyan }

$MopDll = Join-Path $CacheDir 'MOP.dll'
if (-not (Test-Path $MopDll)) {
    Step 'Downloading original MOP 3.10.4...'
    $zip = Join-Path $CacheDir 'MOP-3.10.4.zip'
    Invoke-WebRequest -UseBasicParsing 'https://github.com/Athlon007/MOP/releases/download/3.10.4/MOP.zip' -OutFile $zip
    $z = [IO.Compression.ZipFile]::OpenRead($zip)
    try { [IO.Compression.ZipFileExtensions]::ExtractToFile(($z.Entries | Where-Object Name -eq 'MOP.dll'), $MopDll, $true) }
    finally { $z.Dispose() }
}

$LoaderDir = Join-Path $CacheDir 'MSCLoader'
$LoaderDll = Join-Path $LoaderDir 'MSCLoader.dll'
if (-not (Test-Path $LoaderDll)) {
    Step 'Downloading MSCLoader 1.4.2...'
    $zip = Join-Path $CacheDir 'MSCLoader-1.4.2.zip'
    Invoke-WebRequest -UseBasicParsing 'https://github.com/piotrulos/MSCModLoader/releases/download/v1.4.2/MSCLoader.zip' -OutFile $zip
    $tmp = Join-Path $CacheDir 'msclzip'
    if (Test-Path $tmp) { Remove-Item -Recurse -Force $tmp }
    [IO.Compression.ZipFile]::ExtractToDirectory($zip, $tmp)
    New-Item -ItemType Directory -Force -Path $LoaderDir | Out-Null
    $packs = Join-Path $tmp 'packs'
    [IO.Compression.ZipFile]::ExtractToDirectory((Join-Path $tmp 'main_msc.pack'), $packs)
    [IO.Compression.ZipFile]::ExtractToDirectory((Join-Path $packs 'Managed.zip'), (Join-Path $packs 'managed'))
    [IO.Compression.ZipFile]::ExtractToDirectory((Join-Path $tmp 'main_ref.pack'), (Join-Path $packs 'ref'))
    Copy-Item (Join-Path $packs 'managed\MSCLoader.dll') $LoaderDir -Force
    foreach ($lib in '0Harmony.dll', 'Newtonsoft.Json.dll', 'Ionic.Zip.Reduced.dll', 'INIFileParser.dll') {
        Copy-Item (Join-Path $packs "ref\$lib") $LoaderDir -Force
    }
    Remove-Item -Recurse -Force $tmp
}

Step "Ready: $CacheDir"
