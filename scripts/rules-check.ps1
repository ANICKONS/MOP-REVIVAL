<#
.SYNOPSIS
    Offline checks of the MOPR rule system (tools\RulesCheck).
.DESCRIPTION
    1. selftest - edge cases of the JSON and the old .mopconfig readers, the compiled rule book.
    2. verify   - every built-in rule file (src\MOPR\Rules\*.json) says exactly what its old .mopconfig said
                  (tests\rules\legacy). Exit code 0 only if both pass.
#>
[CmdletBinding()]
param(
    [string]$GameDir = 'D:\SteamLibrary\steamapps\common\My Summer Car'
)
$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$Dotnet = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
if (-not (Test-Path $Dotnet)) { $Dotnet = 'dotnet' }
$Project = Join-Path $RepoRoot 'tools\RulesCheck\RulesCheck.csproj'

# The game's Mono (2.0 profile) cannot load Newtonsoft's LINQ to JSON (JObject needs
# System.ComponentModel.INotifyPropertyChanging); .NET 8 can, so the tests below would not notice it.
$linq = Select-String -Path (Join-Path $RepoRoot 'src\MOPR\src\Rules\*.cs') -Pattern 'Newtonsoft\.Json\.Linq|\bJObject\b|\bJToken\b|\bJArray\b' |
    Where-Object { $_.Line.Trim() -notmatch '^//' }
if ($linq) { $linq | ForEach-Object { Write-Host "[rules] LINQ to JSON does not load in the game: $($_.Filename):$($_.LineNumber)" -ForegroundColor Red }; exit 1 }

& $Dotnet build $Project -c Release -nologo -v q "-p:ManagedDir=$GameDir\mysummercar_Data\Managed" | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Host '[rules] RulesCheck build failed.' -ForegroundColor Red; exit 1 }
$Tool = Join-Path $RepoRoot 'tools\RulesCheck\bin\Release\net8.0\RulesCheck.dll'

& $Dotnet $Tool selftest
$self = $LASTEXITCODE
& $Dotnet $Tool verify (Join-Path $RepoRoot 'tests\rules\legacy') (Join-Path $RepoRoot 'src\MOPR\Rules')
$verify = $LASTEXITCODE
if ($self -ne 0 -or $verify -ne 0) { Write-Host '[rules] FAILED' -ForegroundColor Red; exit 1 }
Write-Host '[rules] OK' -ForegroundColor Green
