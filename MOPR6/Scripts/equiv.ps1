<#
.SYNOPSIS
    MOPR6 equivalence harness in one command (requirement 3, design §10): every scenario of a set is run -Vanilla
    times without mods and once with the mod under test, then EquivCompare classifies every difference.
.DESCRIPTION
    Runs go through scripts\test-run.ps1 (save, PlayerPrefs and settings are backed up, restored and verified by MD5).
    Scenarios must be deterministic ("Deterministic": true). Do not use mouse or keyboard while the game window has
    the focus: the TestBot detects real input and EquivCompare then refuses the run.
    Results: _testruns\equiv-<time>\<scenario>\report.md, report.json and summary.md; exit code 0 = no unexplained
    difference in any scenario.
.EXAMPLE
    .\MOPR6\Scripts\equiv.ps1 -Set smoke
    .\MOPR6\Scripts\equiv.ps1 -Set core -Variant mopr -MoprDll _reference\baseline\MOPR-5.1.1.dll
    .\MOPR6\Scripts\equiv.ps1 -Reuse _testruns\equiv-20261028-220000
#>
[CmdletBinding()]
param(
    [ValidateSet('smoke', 'core', 'full')]
    [string]$Set = 'core',
    # Scenario files instead of a set (comma separated, relative to the repository).
    [string]$Scenarios = '',
    [int]$Vanilla = 3,
    # The mod under test: mopr6 (default) or mopr (MOPR 5.x, e.g. the 5.1.1 baseline: the harness must find its known bugs).
    [ValidateSet('mopr6', 'mopr')]
    [string]$Variant = 'mopr6',
    [string]$Mopr6Dll = '',
    [string]$MoprDll = '',
    # MOPR6 console commands before every mod run (e.g. "sys engine.fsm on"); see test-run.ps1 -Mopr6Steps.
    [string]$Mopr6Steps = '',
    [switch]$NoBuild,
    # Re-evaluate the runs of an earlier invocation (its runs.json) without starting the game.
    [string]$Reuse = '',
    # Learned noise (keys that varied between vanilla runs in earlier calibrations). '' = MOPR6\Equiv\noise.json
    # when it exists; 'none' = without.
    [string]$Noise = '',
    # Add the vanilla runs of this invocation to the learned noise (calibration, task 11).
    [switch]$LearnNoise,
    # Take the vanilla runs from an earlier invocation (its runs.json) instead of running them again: only the mod
    # runs are new. The scenario files and the TestBot snapshot code must not have changed since.
    [string]$VanillaFrom = ''
)
$ErrorActionPreference = 'Stop'
$Repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$TestRun = Join-Path $Repo 'scripts\test-run.ps1'
$Dotnet = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
$Managed = 'D:\SteamLibrary\steamapps\common\My Summer Car\mysummercar_Data\Managed'
$Tool = Join-Path $Repo 'MOPR6\Tools\EquivCompare\bin\Release\net8.0\EquivCompare.exe'
$Equiv = Join-Path $Repo 'MOPR6\Equiv'
function Say([string]$m, [string]$c = 'Cyan') { Write-Host "[equiv] $m" -ForegroundColor $c }

$sets = @{
    smoke = @('harness-smoke')
    # eq-restore joins core with the change ledger (task 19).
    core  = @('eq-home-yard', 'eq-save-load', 'eq-car-work')
    # eq-long (30 min of game time) joins full when the core scenarios are stable.
    full  = @('eq-home-yard', 'eq-save-load', 'eq-car-work', 'eq-perajarvi-trip', 'eq-teimo-buy', 'eq-walkaway', 'eq-sleep')
}
$list = if ($Scenarios) { @($Scenarios -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ }) }
        else { @($sets[$Set] | ForEach-Object { "MOPR6\Scenarios\$_.json" }) }

if (-not $NoBuild -and -not $Reuse) {
    Say 'Building TestBot, MOPR6 and EquivCompare...'
    & $Dotnet build (Join-Path $Repo 'TestBot\MOPR.TestBot.csproj') -c Release -nologo -v q "-p:ManagedDir=$Managed" | Out-Null
    if ($LASTEXITCODE -ne 0) { Say 'TestBot build failed' 'Red'; exit 2 }
    if ($Variant -eq 'mopr6') {
        & $Dotnet build (Join-Path $Repo 'MOPR6\MOPR6.csproj') -c Release -nologo -v q "-p:ManagedDir=$Managed" | Out-Null
        if ($LASTEXITCODE -ne 0) { Say 'MOPR6 build failed' 'Red'; exit 2 }
    }
}
if (-not $Reuse -or -not (Test-Path $Tool)) {
    & $Dotnet build (Join-Path $Repo 'MOPR6\Tools\EquivCompare\EquivCompare.csproj') -c Release -nologo -v q | Out-Null
    if ($LASTEXITCODE -ne 0) { Say 'EquivCompare build failed' 'Red'; exit 2 }
}

if ($Reuse) {
    $Out = if ([IO.Path]::IsPathRooted($Reuse)) { $Reuse } else { Join-Path $Repo $Reuse }
    # Windows PowerShell 5: ConvertFrom-Json writes a JSON array as ONE object; unroll it.
    $runs = @((Get-Content (Join-Path $Out 'runs.json') -Raw | ConvertFrom-Json) | ForEach-Object { $_ })
} else {
    foreach ($s in $list) { if (-not (Test-Path (Join-Path $Repo $s))) { Say "Scenario not found: $s" 'Red'; exit 2 } }
    $Out = Join-Path $Repo ("_testruns\equiv-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
    New-Item -ItemType Directory -Force -Path $Out | Out-Null
    $runs = New-Object System.Collections.ArrayList
    $earlier = @()
    if ($VanillaFrom) {
        $vf = if ([IO.Path]::IsPathRooted($VanillaFrom)) { $VanillaFrom } else { Join-Path $Repo $VanillaFrom }
        $earlier = @((Get-Content (Join-Path $vf 'runs.json') -Raw | ConvertFrom-Json) | ForEach-Object { $_ } | Where-Object { $_.Kind -eq 'vanilla' -and $_.Code -eq '0' })
    }
    foreach ($s in $list) {
        $name = [IO.Path]::GetFileNameWithoutExtension($s)
        $plan = @()
        $reused = @($earlier | Where-Object { $_.Scenario -eq $name })
        if ($reused.Count -gt 0) {
            Say "$name : $($reused.Count) vanilla runs from $VanillaFrom"
            foreach ($r in $reused) { [void]$runs.Add($r) }
        }
        else { for ($i = 1; $i -le $Vanilla; $i++) { $plan += [pscustomobject]@{ Kind = 'vanilla'; Label = "eq-$name-v$i" } } }
        $plan += [pscustomobject]@{ Kind = 'mod'; Label = "eq-$name-m" }
        foreach ($p in $plan) {
            $runArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $TestRun, '-Scenario', $s, '-Label', $p.Label, '-NoBuild')
            if ($p.Kind -eq 'vanilla') { $runArgs += @('-Variant', 'vanilla') }
            elseif ($Variant -eq 'mopr6') {
                $runArgs += @('-Variant', 'mopr6')
                if ($Mopr6Dll) { $runArgs += @('-Mopr6Dll', $Mopr6Dll) }
                if ($Mopr6Steps) { $runArgs += @('-Mopr6Steps', $Mopr6Steps) }
            }
            else { $runArgs += @('-Variant', 'mopr'); if ($MoprDll) { $runArgs += @('-MoprDll', $MoprDll) } }
            Say "$name : $($p.Label)..."
            & powershell @runArgs | Out-Null
            $dir = Get-ChildItem (Join-Path $Repo '_testruns') -Directory -Filter "*-$($p.Label)" | Sort-Object Name | Select-Object -Last 1
            $code = if ($dir -and (Test-Path (Join-Path $dir.FullName 'run.done'))) { (Get-Content (Join-Path $dir.FullName 'run.done') -Raw).Trim() } else { '?' }
            if ($code -ne '0') { Say "  ended with code $code" 'Yellow' }
            [void]$runs.Add([pscustomobject]@{ Scenario = $name; Kind = $p.Kind; Dir = if ($dir) { $dir.FullName } else { '' }; Code = $code })
        }
    }
    $runs | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $Out 'runs.json')
}

$NoiseFile = if ($Noise -eq 'none') { '' } elseif ($Noise) { if ([IO.Path]::IsPathRooted($Noise)) { $Noise } else { Join-Path $Repo $Noise } } else { Join-Path $Equiv 'noise.json' }
if ($LearnNoise) {
    # Learned before the comparison, so this invocation's own vanilla disagreements count too (they would anyway).
    $learnArgs = @('noise', 'learn', '--runs', (Join-Path $Out 'runs.json'), '--out', $(if ($NoiseFile) { $NoiseFile } else { Join-Path $Equiv 'noise.json' }),
                   '--tolerances', (Join-Path $Equiv 'tolerances.json'))
    & $Tool @learnArgs 2>&1 | Select-Object -Last 1 | ForEach-Object { Say $_ }
}
if ($NoiseFile -and -not (Test-Path $NoiseFile)) { $NoiseFile = '' }

$failed = 0
$summary = New-Object System.Text.StringBuilder
[void]$summary.AppendLine("# Equivalence summary ($Variant)").AppendLine()
if ($Mopr6Steps) { [void]$summary.AppendLine("MOPR6 steps before each mod run: ``$Mopr6Steps``").AppendLine() }
if ($NoiseFile) { [void]$summary.AppendLine("Learned noise: ``$NoiseFile``").AppendLine() }
foreach ($name in @($runs | ForEach-Object { $_.Scenario } | Select-Object -Unique)) {
    $v = @($runs | Where-Object { $_.Scenario -eq $name -and $_.Kind -eq 'vanilla' -and $_.Code -eq '0' })
    $m = @($runs | Where-Object { $_.Scenario -eq $name -and $_.Kind -eq 'mod' -and $_.Code -eq '0' })
    if ($v.Count -lt 1 -or $m.Count -lt 1) {
        Say "$name : not enough successful runs (vanilla $($v.Count), mod $($m.Count))" 'Red'
        [void]$summary.AppendLine("- $name : not enough successful runs"); $failed++; continue
    }
    if ($v.Count -lt 2) { Say "$name : only one vanilla run - no noise estimate" 'Yellow' }
    $toolArgs = @('equiv', '--a', $v[0].Dir)
    foreach ($b in ($v | Select-Object -Skip 1)) { $toolArgs += @('--b', $b.Dir) }
    $toolArgs += @('--m', $m[0].Dir, '--scenario', $name, '--out', (Join-Path $Out $name),
                   '--tolerances', (Join-Path $Equiv 'tolerances.json'), '--expected', (Join-Path $Equiv 'expected.json'))
    if ($NoiseFile) { $toolArgs += @('--noise', $NoiseFile) }
    $lines = & $Tool @toolArgs 2>&1
    $code = $LASTEXITCODE
    $lines | ForEach-Object { Write-Host "  $_" }
    if ($code -ne 0) { $failed++ }
    [void]$summary.AppendLine("- $name : " + ($(if ($code -eq 0) { 'OK' } else { "FAIL (exit $code)" })) + " - $($lines | Select-Object -Last 1)")
}
Set-Content -Encoding UTF8 (Join-Path $Out 'summary.md') $summary.ToString()
Say ("Result: " + ($(if ($failed -eq 0) { 'OK' } else { "$failed scenario(s) failed" })) + " - $Out") ($(if ($failed -eq 0) { 'Green' } else { 'Red' }))
exit ($(if ($failed -eq 0) { 0 } else { 1 }))
