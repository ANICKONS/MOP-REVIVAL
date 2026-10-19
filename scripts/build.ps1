<#
.SYNOPSIS
    Builds MOP - REVIVAL (MOPR.dll) and optionally installs it into the game's Mods folder.

.DESCRIPTION
    * Finds the .NET SDK (user-local ~/.dotnet first, then PATH).
    * Finds My Summer Car through the Steam registry key and every Steam library folder.
      With the game installed the mod is compiled against the REAL game assemblies (release quality).
    * Without the game it falls back to compile-only reference stubs (refs/build-stubs.ps1). Such builds
      are tagged "+stubs" and are meant for CI / code checking only.

.PARAMETER Mode
    build (default) | debug | deploy | deploy-debug

.PARAMETER GameDir
    My Summer Car folder (the one with mysummercar.exe). Auto-detected when omitted.

.EXAMPLE
    .\scripts\build.ps1
    .\scripts\build.ps1 -Mode deploy
    .\scripts\build.ps1 -Mode deploy -GameDir 'E:\Games\My Summer Car'
#>
[CmdletBinding()]
param(
    [ValidateSet('build', 'debug', 'deploy', 'deploy-debug')]
    [string]$Mode = 'build',
    [string]$GameDir = ''
)

$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

$RepoRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$Project  = Join-Path $RepoRoot 'src\MOPR\MOPR.csproj'
$DistDir  = Join-Path $RepoRoot 'dist'

function Step([string]$m, [string]$c = 'Cyan') { Write-Host "[MOPR] $m" -ForegroundColor $c }

# --- dotnet -------------------------------------------------------------------------------------
$Dotnet = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
if (-not (Test-Path $Dotnet)) { $Dotnet = 'dotnet' }
& $Dotnet --list-sdks | Out-Null
if ($LASTEXITCODE -ne 0) { Step 'No .NET SDK found. Install it from https://dot.net (any SDK >= 6).' 'Red'; exit 1 }

# --- Game detection -----------------------------------------------------------------------------
function Find-GameDir {
    $roots = New-Object System.Collections.Generic.List[string]
    foreach ($key in 'HKCU:\Software\Valve\Steam', 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam', 'HKLM:\SOFTWARE\Valve\Steam') {
        try {
            $p = (Get-ItemProperty -Path $key -ErrorAction Stop)
            foreach ($name in 'SteamPath', 'InstallPath') { if ($p.$name) { $roots.Add(($p.$name -replace '/', '\')) } }
        } catch { }
    }
    $roots.Add("${env:ProgramFiles(x86)}\Steam")

    $libraries = New-Object System.Collections.Generic.List[string]
    foreach ($r in ($roots | Select-Object -Unique)) {
        $libraries.Add($r)
        $vdf = Join-Path $r 'steamapps\libraryfolders.vdf'
        if (Test-Path $vdf) {
            foreach ($m in (Select-String -Path $vdf -Pattern '"path"\s+"([^"]+)"')) {
                $libraries.Add(($m.Matches[0].Groups[1].Value -replace '\\\\', '\'))
            }
        }
    }

    foreach ($lib in ($libraries | Select-Object -Unique)) {
        $candidate = Join-Path $lib 'steamapps\common\My Summer Car'
            if (Test-Path (Join-Path $candidate 'mysummercar_Data\Managed\UnityEngine.dll')) { return $candidate }
    }
    return ''
}

if ([string]::IsNullOrEmpty($GameDir)) { $GameDir = Find-GameDir }
$ManagedDir = if ($GameDir) { Join-Path $GameDir 'mysummercar_Data\Managed' } else { '' }

if ($ManagedDir -and (Test-Path (Join-Path $ManagedDir 'UnityEngine.dll'))) {
    Step "Game found: $GameDir (real game assemblies)" 'Green'
    if (-not (Test-Path (Join-Path $ManagedDir 'MSCLoader.dll'))) {
        # Vanilla game: use the official MSCLoader release binary as the loader reference.
        Step 'MSCLoader is not installed in the game - using the official MSCLoader 1.4.2 release as reference.' 'Yellow'
        & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $RepoRoot 'refs\fetch-deps.ps1')
        if ($LASTEXITCODE -ne 0) { Step 'Dependency download failed.' 'Red'; exit 1 }
    }
} else {
    Step 'My Summer Car not found - building against compile-only stubs (test build).' 'Yellow'
    $ManagedDir = ''
    if (-not (Test-Path (Join-Path $RepoRoot 'refs\generated\bin\UnityEngine.dll'))) {
        Step 'Generating reference stubs...'
        & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $RepoRoot 'refs\build-stubs.ps1')
        if ($LASTEXITCODE -ne 0) { Step 'Stub generation failed.' 'Red'; exit 1 }
    }
}

# --- Build --------------------------------------------------------------------------------------
$Configuration = if ($Mode -like '*debug*') { 'Debug' } else { 'Release' }
Step "Building ($Configuration)..."
$props = @("-p:ManagedDir=$ManagedDir")
# Without the game only a test build is possible (Release refuses stubs unless explicitly allowed).
if (-not $ManagedDir) { $props += '-p:AllowStubBuild=true' }
& $Dotnet build $Project -c $Configuration -nologo @props
if ($LASTEXITCODE -ne 0) { Step 'Build FAILED.' 'Red'; exit $LASTEXITCODE }

$Output = Join-Path $RepoRoot "src\MOPR\bin\$Configuration\MOPR.dll"
if (-not (Test-Path $Output)) { Step "Output missing: $Output" 'Red'; exit 1 }

New-Item -ItemType Directory -Force -Path $DistDir | Out-Null
try { Copy-Item $Output $DistDir -Force }
catch { Step "Could not update dist\MOPR.dll (file in use?): $($_.Exception.Message)" 'Yellow' }
$kb = [math]::Round((Get-Item $Output).Length / 1KB, 1)
Step "Build OK -> dist\MOPR.dll ($kb KB)" 'Green'
if (-not $ManagedDir) { Step 'NOTE: stub build. Rebuild with the game installed before publishing.' 'Yellow' }

# --- Deploy -------------------------------------------------------------------------------------
if ($Mode -like 'deploy*') {
    if (-not $GameDir) { Step 'Cannot deploy: game folder not found. Pass -GameDir.' 'Red'; exit 1 }
    $ModsDir = Join-Path $GameDir 'Mods'
    if (-not (Test-Path $ModsDir)) { Step "Mods folder not found: $ModsDir" 'Red'; exit 1 }
    Copy-Item $Output $ModsDir -Force
    Step "Deployed -> $ModsDir\MOPR.dll" 'Green'
    $legacy = Join-Path $ModsDir 'References\MOPRNET.dll'
    if (Test-Path $legacy) { Step "Note: $legacy belongs to MOPR 4.x and is no longer needed. You can delete it." 'DarkGray' }
    Step 'Close the game before deploying if the DLL is locked.' 'DarkGray'
}
