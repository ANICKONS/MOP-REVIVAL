<#
.SYNOPSIS
    Builds compile-only reference stubs of the game assemblies (UnityEngine, PlayMaker, Assembly-CSharp, ...).

.DESCRIPTION
    Used ONLY when the real game is not installed (CI, machines without My Summer Car).
    1. Downloads two public, GPL-licensed binaries that were compiled against the REAL game assemblies:
         - original MOP 3.10.4 (MOP.dll)            https://github.com/Athlon007/MOP
         - MSCLoader 1.4.2 (MSCLoader.dll + libs)   https://github.com/piotrulos/MSCModLoader
    2. Runs tools/RefStubGen to reconstruct the exact API surface they use from the game assemblies.
    3. Compiles the stubs (plus refs/overlay/*.cs) into refs/generated/bin.

    The output never contains game code — only member signatures with `throw null` bodies.
    Release builds of the mod must be compiled against the real game DLLs (see scripts/build.ps1).
#>
[CmdletBinding()]
param(
    [string]$Dotnet = ''
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

$RefsDir   = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot  = Split-Path -Parent $RefsDir
$CacheDir  = Join-Path $RefsDir 'cache'
$GenDir    = Join-Path $RefsDir 'generated'
$Overlay   = Join-Path $RefsDir 'overlay'
$ToolProj  = Join-Path $RepoRoot 'tools\RefStubGen\RefStubGen.csproj'
$ToolOut   = Join-Path $RepoRoot 'tools\RefStubGen\out'

if ([string]::IsNullOrEmpty($Dotnet)) {
    $local = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
    $Dotnet = if (Test-Path $local) { $local } else { 'dotnet' }
}

function Step([string]$m) { Write-Host "[stubs] $m" -ForegroundColor Cyan }

# --- 1. Inputs -----------------------------------------------------------------------------------
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $RefsDir 'fetch-deps.ps1')
if ($LASTEXITCODE -ne 0) { throw 'Dependency download failed' }
$MopDll    = Join-Path $CacheDir 'MOP.dll'
$LoaderDir = Join-Path $CacheDir 'MSCLoader'
$LoaderDll = Join-Path $LoaderDir 'MSCLoader.dll'

# --- 2. Generate ---------------------------------------------------------------------------------
Step 'Building RefStubGen...'
& $Dotnet build $ToolProj -c Release -o $ToolOut -nologo -v q | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'RefStubGen build failed' }

if (Test-Path $GenDir) { Remove-Item -Recurse -Force $GenDir }
$targets = 'UnityEngine,UnityEngine.UI,PlayMaker,Assembly-CSharp,Assembly-CSharp-firstpass,ES2,cInput'
Step 'Generating stubs...'
& $Dotnet (Join-Path $ToolOut 'RefStubGen.dll') --out $GenDir --overlay $Overlay --targets $targets $MopDll $LoaderDll
if ($LASTEXITCODE -ne 0) { throw 'RefStubGen failed' }

# --- 3. Compile ----------------------------------------------------------------------------------
foreach ($asm in Get-Content (Join-Path $GenDir 'build-order.txt')) {
    Step "Compiling stub $asm..."
    & $Dotnet build (Join-Path $GenDir "$asm\$asm.csproj") -c Release -nologo -v q -clp:ErrorsOnly
    if ($LASTEXITCODE -ne 0) { throw "Stub $asm failed to compile" }
}

# Real third-party libraries shipped with MSCLoader are copied next to the stubs.
Copy-Item (Join-Path $LoaderDir '*.dll') (Join-Path $GenDir 'bin') -Force
Step "Done -> $(Join-Path $GenDir 'bin')"
