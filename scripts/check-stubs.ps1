<#
.SYNOPSIS
    CI helper: compiles the mod against the compile-only stubs (no game required).
    Proves that the code only uses APIs that exist in the reference surface. Never publish this output.
#>
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
$env:DOTNET_NOLOGO = '1'
$RepoRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$Dotnet = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
if (-not (Test-Path $Dotnet)) { $Dotnet = 'dotnet' }

if (-not (Test-Path (Join-Path $RepoRoot 'refs\generated\bin\UnityEngine.dll'))) {
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $RepoRoot 'refs\build-stubs.ps1')
    if ($LASTEXITCODE -ne 0) { exit 1 }
}

& $Dotnet build (Join-Path $RepoRoot 'src\MOPR\MOPR.csproj') -c Release -nologo "-p:ManagedDir=" "-p:AllowStubBuild=true" "-p:OutputPath=$RepoRoot\obj\stubcheck\\" -clp:ErrorsOnly
exit $LASTEXITCODE
