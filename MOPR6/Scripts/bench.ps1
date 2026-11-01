<#
.SYNOPSIS
    MOPR6 benchmark set (requirement 4, design §11): vanilla, MOPR 5.1.1 and MOPR6 on the same points, then the table
    and the release gate (EquivCompare bench).
.DESCRIPTION
    Runs MOPR6\Scenarios\bench.json -Runs times per variant through scripts\test-run.ps1 (save and settings are
    backed up and restored by it), interleaved (vanilla, 5.1.1, MOPR6, vanilla, ...) so slow drifts hit all variants.
    The 5.1.1 DLL comes from MOPR6\Equiv\baseline.json and is checked by MD5.
    Results: _testruns\bench-<time>\bench.md, bench.json; exit code 0 = gate passes.
.EXAMPLE
    .\MOPR6\Scripts\bench.ps1
    .\MOPR6\Scripts\bench.ps1 -Runs 1 -Variants vanilla,ref
#>
[CmdletBinding()]
param(
    [int]$Runs = 3,
    [string[]]$Variants = @('vanilla', 'ref', 'cand'),
    [string]$Scenario = 'MOPR6\Scenarios\bench.json',
    [string]$Mopr6Dll = '',
    [switch]$NoBuild,
    # Re-evaluate existing run folders instead of running the game: -Reuse <bench6 folder> (its runs.json).
    [string]$Reuse = ''
)
$ErrorActionPreference = 'Stop'
$Repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$TestRun = Join-Path $Repo 'scripts\test-run.ps1'
$Dotnet = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
$GameDir = 'D:\SteamLibrary\steamapps\common\My Summer Car'
$Managed = Join-Path $GameDir 'mysummercar_Data\Managed'
$Tool = Join-Path $Repo 'MOPR6\Tools\EquivCompare\bin\Release\net8.0\EquivCompare.exe'
function Say([string]$m, [string]$c = 'Cyan') { Write-Host "[bench] $m" -ForegroundColor $c }

$baseline = Get-Content (Join-Path $Repo 'MOPR6\Equiv\baseline.json') -Raw | ConvertFrom-Json
$refDll = Join-Path $Repo $baseline.Path
if ($Variants -contains 'ref') {
    if (-not (Test-Path $refDll)) { Say "Reference DLL missing: $refDll ($($baseline.Note))" 'Red'; exit 2 }
    $md5 = (Get-FileHash $refDll -Algorithm MD5).Hash
    if ($md5 -ne $baseline.Md5) { Say "Reference DLL MD5 $md5, expected $($baseline.Md5)" 'Red'; exit 2 }
}

if (-not $NoBuild) {
    Say 'Building TestBot, MOPR6 and EquivCompare...'
    & $Dotnet build (Join-Path $Repo 'TestBot\MOPR.TestBot.csproj') -c Release -nologo -v q "-p:ManagedDir=$Managed" | Out-Null
    if ($LASTEXITCODE -ne 0) { Say 'TestBot build failed' 'Red'; exit 2 }
    & $Dotnet build (Join-Path $Repo 'MOPR6\MOPR6.csproj') -c Release -nologo -v q "-p:ManagedDir=$Managed" | Out-Null
    if ($LASTEXITCODE -ne 0) { Say 'MOPR6 build failed' 'Red'; exit 2 }
    & $Dotnet build (Join-Path $Repo 'MOPR6\Tools\EquivCompare\EquivCompare.csproj') -c Release -nologo -v q | Out-Null
    if ($LASTEXITCODE -ne 0) { Say 'EquivCompare build failed' 'Red'; exit 2 }
}

if ($Reuse) {
    $Out = if ([IO.Path]::IsPathRooted($Reuse)) { $Reuse } else { Join-Path $Repo $Reuse }
    # Windows PowerShell 5: ConvertFrom-Json writes a JSON array as ONE object; unroll it.
    $runList = @((Get-Content (Join-Path $Out 'runs.json') -Raw | ConvertFrom-Json) | ForEach-Object { $_ })
} else {
    $Out = Join-Path $Repo ("_testruns\bench-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
    New-Item -ItemType Directory -Force -Path $Out | Out-Null
    $runList = New-Object System.Collections.ArrayList
    for ($i = 1; $i -le $Runs; $i++) {
        foreach ($v in $Variants) {
            $label = "bench-$v-$i"
            $runArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $TestRun, '-Scenario', $Scenario, '-Label', $label, '-NoBuild')
            switch ($v) {
                'vanilla' { $runArgs += @('-Variant', 'vanilla') }
                'ref'     { $runArgs += @('-Variant', 'mopr', '-MoprDll', $refDll) }
                'cand'    { $runArgs += @('-Variant', 'mopr6'); if ($Mopr6Dll) { $runArgs += @('-Mopr6Dll', $Mopr6Dll) } }
                default   { Say "Unknown variant $v" 'Red'; exit 2 }
            }
            Say "Run $i/$Runs ${v}..."
            $before = @(Get-ChildItem (Join-Path $Repo '_testruns') -Directory -Filter "*-$label" -ErrorAction SilentlyContinue).Count
            & powershell @runArgs | Out-Null
            $dir = Get-ChildItem (Join-Path $Repo '_testruns') -Directory -Filter "*-$label" | Sort-Object Name | Select-Object -Last 1
            if (-not $dir -or @(Get-ChildItem (Join-Path $Repo '_testruns') -Directory -Filter "*-$label").Count -eq $before) { Say "  no run folder for $label" 'Red'; continue }
            $code = if (Test-Path (Join-Path $dir.FullName 'run.done')) { (Get-Content (Join-Path $dir.FullName 'run.done') -Raw).Trim() } else { '?' }
            if ($code -ne '0') { Say "  run $($dir.Name) ended with code $code" 'Yellow' }
            [void]$runList.Add([pscustomobject]@{ Variant = $v; Dir = $dir.FullName; Code = $code })
        }
    }
    $runList | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $Out 'runs.json')
}

$toolArgs = @('bench', '--out', $Out)
foreach ($r in $runList) { if ($r.Code -eq '0') { $toolArgs += @("--$($r.Variant)", $r.Dir) } }
if (-not ($toolArgs -contains '--ref')) { $toolArgs += @('--ref', (Join-Path $Out 'none')) }
if (-not ($toolArgs -contains '--cand')) { $toolArgs += @('--cand', (Join-Path $Out 'none')) }
& $Tool @toolArgs
$gate = $LASTEXITCODE
Say "Report: $Out\bench.md" 'Green'
exit $gate
