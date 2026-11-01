<#
.SYNOPSIS
    Runs an automated in-game test of MOP - REVIVAL with the MOPR TestBot and collects the report.

.DESCRIPTION
    1. Builds MOPR + TestBot against the real game.
    2. Backs up the save folder and the game's screen PlayerPrefs (registry).
    3. Installs the variant under test (MOPR or vanilla = no MOPR) + TestBot + run.json.
    4. Launches the game through Steam in a window, waits for the bot to finish (it quits without saving).
    5. Collects report.json, snapshots and output_log.txt into _testruns\<timestamp>-<variant>.
    6. ALWAYS restores the save folder, the PlayerPrefs and the Mods folder, even on failure.

.EXAMPLE
    .\scripts\test-run.ps1 -Scenario MOPR5\tests\scenarios\smoke.json -Variant mopr
    .\scripts\test-run.ps1 -Scenario MOPR5\tests\scenarios\smoke.json -Variant vanilla
#>
[CmdletBinding()]
param(
    [string]$Scenario = 'MOPR5\tests\scenarios\smoke.json',
    # mopr = MOPR 5.x (-MoprDll), mopr6 = the MOPR6 rewrite (-Mopr6Dll); both are installed as Mods\MOPR.dll.
    # vanilla = neither of them.
    [ValidateSet('mopr', 'mopr6', 'vanilla')]
    [string]$Variant = 'mopr',
    [string]$Label = '',
    [int]$Width = 1280,
    [int]$Height = 720,
    # Extra Unity command line arguments for the game (e.g. "-popupwindow": a borderless window, for runs at the
    # monitor's own resolution).
    [string]$ExtraArgs = '',
    [int]$TimeoutMinutes = 25,
    [string]$GameDir = 'D:\SteamLibrary\steamapps\common\My Summer Car',
    [switch]$NoBuild,
    # The game's first scene shows "PRESS ANY KEY TO CONTINUE" before the main menu (and before any mod is loaded).
    # The runner clicks the centre of the game window until the main menu loads. -NoSplashClick disables it.
    [switch]$NoSplashClick,
    [int]$SplashClickIntervalSeconds = 2,
    [int]$SplashTimeoutSeconds = 120,
    # Test another MOPR build (e.g. a saved older DLL for an A/B against it) instead of MOPR5\mod\bin\Release.
    [string]$MoprDll = '',
    # MOPR6 build under test (default MOPR6\bin\Release\MOPR.dll), used by -Variant mopr6. Installed as Mods\MOPR.dll,
    # like 5.x (the user's Mods\MOPR.dll is stashed and put back after the run).
    [string]$Mopr6Dll = '',
    # -Variant mopr6 only: also install MOPR 5.x (-MoprDll) as Mods\MOPR_5x.dll, to test that MOPR6 then refuses to run.
    [switch]$WithMopr5,
    # -Variant mopr6 only: MOPR6 console commands run before the scenario's steps, separated by ';'
    # (e.g. "sys engine.fsm on;status").
    [string]$Mopr6Steps = '',
    # Files copied into Mods\Config\Mod Settings\MOPR for this run only (rule files, MopData.json...).
    # That folder is ALWAYS backed up before the run and restored after it, with or without this parameter.
    [string]$ModConfig = '',
    # Other mods left out of this run, comma separated DLL names without ".dll" (e.g. "BetterMSC"): moved out of
    # Mods for the run and moved back after it. The settings of every other mod are backed up and restored anyway.
    [string]$DisableMods = ''
)

$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)   # scripts -> the repository
function Step([string]$m, [string]$c = 'Cyan') { Write-Host "[test] $m" -ForegroundColor $c }
. (Join-Path $RepoRoot 'scripts\GameWindows.ps1')

$SaveDir    = Join-Path $env:USERPROFILE 'AppData\LocalLow\Amistech\My Summer Car'
$PrefsKey   = 'HKCU\Software\Amistech\My Summer Car'
$ModsDir    = Join-Path $GameDir 'Mods'
$ManagedDir = Join-Path $GameDir 'mysummercar_Data\Managed'
$BotCfgDir  = Join-Path $ModsDir 'Config\Mod Settings\MOPR_TestBot'
# Several scenarios separated by commas run in ONE game session (one launch instead of several): their steps are
# joined, with a "reloadgame" (back to the menu without saving, load again) between two of them.
$ScenarioPaths = @($Scenario -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ } |
    ForEach-Object { if ([IO.Path]::IsPathRooted($_)) { $_ } else { Join-Path $RepoRoot $_ } })
$ScenarioPath = $ScenarioPaths[0]

# --- Preconditions ------------------------------------------------------------------------------
if (Get-Process mysummercar -ErrorAction SilentlyContinue) { Step 'The game is already running. Close it first.' 'Red'; exit 1 }
if (-not (Get-Process steam -ErrorAction SilentlyContinue)) { Step 'Steam is not running.' 'Red'; exit 1 }
if (-not (Test-Path (Join-Path $ManagedDir 'MSCLoader.dll'))) { Step 'MSCLoader is not installed in the game.' 'Red'; exit 1 }
foreach ($p in $ScenarioPaths) { if (-not (Test-Path $p)) { Step "Scenario not found: $p" 'Red'; exit 1 } }
$SteamExe = (Get-ItemProperty 'HKCU:\Software\Valve\Steam' -ErrorAction SilentlyContinue).SteamExe
if (-not $SteamExe) { $SteamExe = "${env:ProgramFiles(x86)}\Steam\steam.exe" }

# --- Build --------------------------------------------------------------------------------------
$Dotnet = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
if (-not (Test-Path $Dotnet)) { $Dotnet = 'dotnet' }
if (-not $NoBuild) {
    if ($Variant -eq 'mopr6') {
        Step 'Building MOPR6...'
        & $Dotnet build (Join-Path $RepoRoot 'MOPR6\MOPR6.csproj') -c Release -nologo -v q "-p:ManagedDir=$ManagedDir" | Out-Null
        if ($LASTEXITCODE -ne 0) { Step 'MOPR6 build failed.' 'Red'; exit 1 }
    } else {
        Step 'Building MOPR...'
        & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $RepoRoot 'MOPR5\scripts\build.ps1') -GameDir $GameDir | Out-Null
        if ($LASTEXITCODE -ne 0) { Step 'MOPR build failed.' 'Red'; exit 1 }
    }
    Step 'Building TestBot...'
    & $Dotnet build (Join-Path $RepoRoot 'TestBot\MOPR.TestBot.csproj') -c Release -nologo -v q "-p:ManagedDir=$ManagedDir" | Out-Null
    if ($LASTEXITCODE -ne 0) { Step 'TestBot build failed.' 'Red'; exit 1 }
}
if (-not $MoprDll) { $MoprDll = Join-Path $RepoRoot 'MOPR5\mod\bin\Release\MOPR.dll' }
elseif (-not [IO.Path]::IsPathRooted($MoprDll)) { $MoprDll = Join-Path $RepoRoot $MoprDll }
if ($WithMopr5 -and $Variant -ne 'mopr6') { Step '-WithMopr5 is only for -Variant mopr6.' 'Red'; exit 1 }
if (($Variant -eq 'mopr' -or $WithMopr5) -and -not (Test-Path $MoprDll)) { Step "MOPR DLL not found: $MoprDll" 'Red'; exit 1 }
if (-not $Mopr6Dll) { $Mopr6Dll = Join-Path $RepoRoot 'MOPR6\bin\Release\MOPR.dll' }
elseif (-not [IO.Path]::IsPathRooted($Mopr6Dll)) { $Mopr6Dll = Join-Path $RepoRoot $Mopr6Dll }
if ($Variant -eq 'mopr6' -and -not (Test-Path $Mopr6Dll)) { Step "MOPR6 DLL not found: $Mopr6Dll" 'Red'; exit 1 }
$MoprCfgDir = Join-Path $ModsDir 'Config\Mod Settings\MOPR'
if ($ModConfig -and -not [IO.Path]::IsPathRooted($ModConfig)) { $ModConfig = Join-Path $RepoRoot $ModConfig }
if ($ModConfig -and -not (Test-Path $ModConfig)) { Step "ModConfig folder not found: $ModConfig" 'Red'; exit 1 }
$Disabled = @($DisableMods -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
foreach ($d in $Disabled) {
    if ($d -match '[\\/:*?"<>|]' -or $d -ieq 'MOPR' -or $d -ieq 'MOPR6' -or $d -ieq 'MOPR.TestBot') { Step "DisableMods: invalid mod name '$d'" 'Red'; exit 1 }
    if (-not (Test-Path (Join-Path $ModsDir "$d.dll"))) { Step "DisableMods: $d.dll is not in $ModsDir" 'Red'; exit 1 }
}
$BotDll  = Join-Path $RepoRoot 'TestBot\bin\Release\MOPR.TestBot.dll'
if (-not (Test-Path $BotDll)) { Step "TestBot not built: $BotDll" 'Red'; exit 1 }

# --- Run folder + backups -------------------------------------------------------------------------
$stamp  = Get-Date -Format 'yyyyMMdd-HHmmss'
$name   = if ($Label) { "$stamp-$Variant-$Label" } else { "$stamp-$Variant" }
$RunDir = Join-Path $RepoRoot "_testruns\$name"
$Stash  = Join-Path $RunDir 'stash'
New-Item -ItemType Directory -Force -Path $RunDir, $Stash | Out-Null

Step "Backing up the save folder -> $RunDir\save-backup"
Copy-Item -Recurse -Force $SaveDir (Join-Path $RunDir 'save-backup')
$srcCount = (Get-ChildItem $SaveDir -Recurse -File).Count
$bakCount = (Get-ChildItem (Join-Path $RunDir 'save-backup') -Recurse -File).Count
if ($srcCount -ne $bakCount) { Step "Save backup incomplete ($bakCount of $srcCount files). Aborting." 'Red'; exit 1 }
& reg.exe export $PrefsKey (Join-Path $RunDir 'prefs.reg') /y | Out-Null
if ($LASTEXITCODE -ne 0) { Step 'Could not back up the game PlayerPrefs. Aborting.' 'Red'; exit 1 }

$userMopr = Join-Path $ModsDir 'MOPR.dll'
$hadUserMopr = Test-Path $userMopr
if ($hadUserMopr) { Copy-Item $userMopr (Join-Path $Stash 'MOPR.dll') -Force }
if ($WithMopr5 -and (Test-Path (Join-Path $ModsDir 'MOPR_5x.dll'))) { Step 'Mods\MOPR_5x.dll exists already; remove it first.' 'Red'; exit 1 }
$userMopr5x = Join-Path $ModsDir 'MOPR_5x.dll'
# MOPR6 settings are backed up with the other mods' settings below; a folder the run creates is removed after it.
$Mopr6CfgDir = Join-Path $ModsDir 'Config\Mod Settings\MOPR6'
$hadMopr6Cfg = Test-Path $Mopr6CfgDir

# The MOPR settings folder (settings.json, MopData.json, rule files): the run may change or overlay it.
$CfgBackup = Join-Path $RunDir 'mopr-config-backup'
$hadMoprCfg = Test-Path $MoprCfgDir
if ($hadMoprCfg) {
    Copy-Item -Recurse -Force $MoprCfgDir $CfgBackup
    $cfgCount = (Get-ChildItem $MoprCfgDir -Recurse -File -Force).Count
    $cfgBak = (Get-ChildItem $CfgBackup -Recurse -File -Force).Count
    if ($cfgCount -ne $cfgBak) { Step "MOPR settings backup incomplete ($cfgBak of $cfgCount files). Aborting." 'Red'; exit 1 }
}

# The settings of the other mods (the bot may change them, e.g. "modsetting" in the menu): backed up, restored after.
$ModSettingsDir = Join-Path $ModsDir 'Config\Mod Settings'
$OtherCfgBackup = Join-Path $RunDir 'mod-settings-backup'
$otherCfg = @(Get-ChildItem $ModSettingsDir -Directory -Force -ErrorAction SilentlyContinue | Where-Object { $_.Name -ne 'MOPR' -and $_.Name -ne 'MOPR_TestBot' })
New-Item -ItemType Directory -Force -Path $OtherCfgBackup | Out-Null
foreach ($dir in $otherCfg) { Copy-Item -Recurse -Force $dir.FullName (Join-Path $OtherCfgBackup $dir.Name) }
$otherCount = @($otherCfg | ForEach-Object { Get-ChildItem $_.FullName -Recurse -File -Force }).Count
$otherBak = @(Get-ChildItem $OtherCfgBackup -Recurse -File -Force).Count
if ($otherCount -ne $otherBak) { Step "Mod settings backup incomplete ($otherBak of $otherCount files). Aborting." 'Red'; exit 1 }
$DisabledStash = Join-Path $Stash 'disabled-mods'

$exitCode = 0
try {
    # --- Install the variant under test ---------------------------------------------------------
    if ($Variant -eq 'mopr') { Copy-Item $MoprDll $userMopr -Force }
    elseif ($Variant -eq 'mopr6') { Copy-Item $Mopr6Dll $userMopr -Force }
    elseif ($hadUserMopr) { Remove-Item $userMopr -Force }
    if ($WithMopr5) { Copy-Item $MoprDll $userMopr5x -Force }
    Copy-Item $BotDll (Join-Path $ModsDir 'MOPR.TestBot.dll') -Force
    if ($Disabled.Count -gt 0) {
        New-Item -ItemType Directory -Force -Path $DisabledStash | Out-Null
        foreach ($d in $Disabled) { Move-Item (Join-Path $ModsDir "$d.dll") (Join-Path $DisabledStash "$d.dll") -Force }
        Step "Left out for this run: $($Disabled -join ', ')"
    }
    if ($ModConfig) {
        New-Item -ItemType Directory -Force -Path $MoprCfgDir | Out-Null
        Copy-Item -Recurse -Force (Join-Path $ModConfig '*') $MoprCfgDir
        Step "Overlay for this run: $ModConfig -> $MoprCfgDir"
    }

    if (Test-Path $BotCfgDir) { Remove-Item -Recurse -Force $BotCfgDir }
    New-Item -ItemType Directory -Force -Path $BotCfgDir | Out-Null
    $scenarioJson = Get-Content $ScenarioPath -Raw | ConvertFrom-Json
    if ($ScenarioPaths.Count -gt 1) {
        # One session: the settings of the first scenario, every scenario's steps without its "quit", one quit at the end.
        $steps = New-Object System.Collections.ArrayList
        for ($s = 0; $s -lt $ScenarioPaths.Count; $s++) {
            $part = Get-Content $ScenarioPaths[$s] -Raw | ConvertFrom-Json
            if ($s -gt 0) { [void]$steps.Add([pscustomobject]@{ Op = 'reloadgame' }) }
            [void]$steps.Add([pscustomobject]@{ Op = 'log'; Text = "===== scenario $($part.Name) ($($s + 1)/$($ScenarioPaths.Count))" })
            foreach ($st in @($part.Steps)) { if ($st.Op -ne 'quit') { [void]$steps.Add($st) } }
        }
        [void]$steps.Add([pscustomobject]@{ Op = 'quit' })
        $scenarioJson.Steps = $steps.ToArray()
        $scenarioJson.Name = (($ScenarioPaths | ForEach-Object { [IO.Path]::GetFileNameWithoutExtension($_) }) -join '+')
        Step "One session for $($ScenarioPaths.Count) scenarios: $($scenarioJson.Name)"
    }
    if ($Mopr6Steps -and $Variant -eq 'mopr6') {
        # MOPR6 console commands before the scenario's own steps (e.g. "sys engine.fsm on"): the mod run of a harness
        # comparison with a system switched on; vanilla runs of the same scenario do not get them.
        $pre = @($Mopr6Steps -split ';' | ForEach-Object { $_.Trim() } | Where-Object { $_ } |
                 ForEach-Object { [pscustomobject]@{ Op = 'mopr'; Text = $_ } })
        $scenarioJson.Steps = @($pre) + @($scenarioJson.Steps)
        Step "MOPR6 steps first: $($Mopr6Steps)"
    }
    $scenarioJson | Add-Member -NotePropertyName Variant -NotePropertyValue $name -Force
    $scenarioJson | ConvertTo-Json -Depth 10 | Set-Content -Encoding UTF8 (Join-Path $BotCfgDir 'run.json')

    # --- Launch -----------------------------------------------------------------------------------
    Step "Launching the game ($Width x $Height, windowed) - please don't touch mouse/keyboard in the game window."
    $launchTime = Get-Date
    Start-Process $SteamExe -ArgumentList ("-applaunch 516750 -screen-width $Width -screen-height $Height -screen-fullscreen 0 " + $ExtraArgs).TrimEnd()

    $deadline = (Get-Date).AddMinutes($TimeoutMinutes)
    $startDeadline = (Get-Date).AddMinutes(2)
    $proc = $null
    while (-not $proc -and (Get-Date) -lt $startDeadline) {
        Start-Sleep -Seconds 2
        $proc = Get-Process mysummercar -ErrorAction SilentlyContinue | Select-Object -First 1
    }
    if (-not $proc) { throw 'The game did not start within 2 minutes.' }
    Step "Game started (pid $($proc.Id)). Waiting for the bot..."

    $doneFlag  = Join-Path $BotCfgDir 'done.flag'
    $menuFlag  = Join-Path $BotCfgDir 'menu.flag'
    $menuReady = Join-Path $BotCfgDir 'menu.ready'
    $gameLog   = Join-Path $GameDir 'output_log.txt'
    $handledDialogs = @{}
    $menuHandled = $false
    $splashDone = [bool]$NoSplashClick
    $splashClicks = 0
    $lastSplashClick = [datetime]::MinValue
    $splashDeadline = (Get-Date).AddSeconds($SplashTimeoutSeconds)
    $shot = 0
    while ((Get-Date) -lt $deadline) {
        if (Test-Path $doneFlag) { break }
        if ($proc.HasExited) { break }

        # 1. Startup dialogs of the game process (Unity's configuration window): press OK.
        foreach ($w in @(Get-GameWindows $proc.Id | Where-Object { $_.Visible -and $_.Class -eq '#32770' })) {
            $key = $w.Handle.ToInt64()
            if ($handledDialogs.ContainsKey($key)) { continue }
            $handledDialogs[$key] = $true
            $shot++
            [void](Save-Screenshot (Join-Path $RunDir ("dialog-$shot.png")))
            $controls = @(Get-DialogControls $w.Handle | Where-Object { $_.Visible -and $_.Text } | ForEach-Object { "$($_.Class)#$($_.Id) '$($_.Text)'" })
            Step "Dialog '$($w.Title)' ($($w.Width)x$($w.Height)): $($controls -join ', ')"
            $pressed = Invoke-DialogConfirm $w.Handle
            if ($pressed) { Step "  pressed '$($pressed.Text)' (id $($pressed.Id))" 'Green' } else { Step '  no OK button found - please press it manually' 'Yellow' }
        }

        # 2. "PRESS ANY KEY TO CONTINUE" splash (first scene, before the main menu and before mods load):
        #    click the centre of the game window until the main menu is loaded. The menu counts as loaded when the
        #    fresh game log says "LoadLevel: MainMenu" or the bot (a mod, so already in the menu) wrote menu.flag.
        if (-not $splashDone) {
            if ((Test-Path $menuFlag) -or (Test-LogContains $gameLog 'LoadLevel: MainMenu' $launchTime)) {
                $splashDone = $true
                Step "Splash screen passed ($splashClicks click(s)), main menu loaded." 'Green'
            } elseif ((Get-Date) -gt $splashDeadline) {
                $splashDone = $true
                Step 'Splash screen: main menu did not load in time - please press a key in the game window.' 'Yellow'
            } elseif (((Get-Date) - $lastSplashClick).TotalSeconds -ge $SplashClickIntervalSeconds) {
                $unity = Get-GameWindows $proc.Id | Where-Object { $_.Visible -and $_.Class -eq 'UnityWndClass' -and $_.Width -gt 200 } | Select-Object -First 1
                $dialogOpen = @(Get-GameWindows $proc.Id | Where-Object { $_.Visible -and $_.Class -eq '#32770' }).Count -gt 0
                if ($unity -and -not $dialogOpen) {
                    $lastSplashClick = Get-Date
                    $rc = New-Object MoprWin32+RECT
                    [void][MoprWin32]::GetClientRect($unity.Handle, [ref]$rc)
                    $cx = [int](($rc.Right - $rc.Left) / 2); $cy = [int](($rc.Bottom - $rc.Top) / 2)
                    if ($splashClicks -eq 0) { [void](Save-Screenshot (Join-Path $RunDir 'splash.png')) }
                    if ([MoprWin32]::ClickClientIfOnTop($unity.Handle, $cx, $cy)) {
                        $splashClicks++
                        Step "Splash screen: clicked the game window (#$splashClicks)."
                    } else {
                        Step 'Splash screen: the game window is covered or minimized, not clicking (bring it to front).' 'Yellow'
                    }
                }
            }
        }

        # 3. Main menu is up (written by the bot): release the bot.
        if ($splashDone -and -not $menuHandled -and (Test-Path $menuFlag)) {
            $menuHandled = $true
            Start-Sleep -Milliseconds 800
            [void](Save-Screenshot (Join-Path $RunDir 'menu.png'))
            Set-Content -Path $menuReady -Value 'ok'
        }
        Start-Sleep -Seconds 1
    }

    if (Test-Path $doneFlag) {
        Step "Bot finished: $(Get-Content $doneFlag -Raw)" 'Green'
        if (-not $proc.WaitForExit(60000)) { Step 'Game did not quit, killing it.' 'Yellow'; Stop-Process -Id $proc.Id -Force }
    } elseif ($proc.HasExited) {
        Step "The game exited before the bot finished (exit code $($proc.ExitCode))." 'Red'; $exitCode = 2
    } else {
        Step 'Timeout - killing the game.' 'Red'; Stop-Process -Id $proc.Id -Force; $exitCode = 3
    }
    Start-Sleep -Seconds 3
}
catch {
    Step "Run failed: $($_.Exception.Message)" 'Red'
    $exitCode = 4
    Get-Process mysummercar -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 3
}
finally {
    # --- Collect ----------------------------------------------------------------------------------
    foreach ($f in 'report.json', 'run.active.json') {
        $p = Join-Path $BotCfgDir $f
        if (Test-Path $p) { Copy-Item $p $RunDir -Force }
    }
    foreach ($pattern in 'dumpui_*.txt', 'find_*.txt', 'dumpfsm_*.txt', 'probe_*.txt', 'settings_*.txt', 'fsmhist_*.txt', 'tree_*.txt', 'mopstate_*.txt', 'fsmcache_*.txt', 'partdiff_*.txt', 'fsmmark_*.txt', 'guardmark_*.txt', 'guardcheck_*.txt', 'lightinfo_*.txt', 'visible_*.txt', 'fsmgrep_*.txt') {
        Get-ChildItem $BotCfgDir -Filter $pattern -ErrorAction SilentlyContinue | Copy-Item -Destination $RunDir -Force
    }
    foreach ($pattern in 'snapshot_*.tsv', 'watch_*.tsv', 'shot_*.png', 'partsnap_*.tsv', 'boltsnap_*.tsv', 'boltdiff_*.txt', 'fullsnap_*.tsv') {
        Get-ChildItem $BotCfgDir -Filter $pattern -ErrorAction SilentlyContinue | Copy-Item -Destination $RunDir -Force
    }
    # Copies of the save folder taken by the bot ("savecopy", MOPR6 harness).
    Get-ChildItem $BotCfgDir -Directory -Filter 'savecopy_*' -ErrorAction SilentlyContinue | Copy-Item -Destination $RunDir -Recurse -Force
    foreach ($pattern in 'moprapi_*.txt', 'proptrace_*.txt', 'writers_*.txt', 'inventory_*.jsonl', 'autodrive_*.tsv', 'ledger_*.txt') {
        Get-ChildItem $BotCfgDir -Filter $pattern -ErrorAction SilentlyContinue | Copy-Item -Destination $RunDir -Force
    }
    foreach ($log in 'output_log.txt') {
        $p = Join-Path $GameDir $log
        if (Test-Path $p) { Copy-Item $p $RunDir -Force }
    }
    # MOPR6's own log (errors with stacks): the settings restore below would put the user's old one back.
    $m6log = Join-Path $Mopr6CfgDir 'mopr.log'
    if (Test-Path $m6log) { Copy-Item $m6log (Join-Path $RunDir 'mopr6.log') -Force }
    $mopLogs = Join-Path $GameDir 'MOP_Logs'
    if (Test-Path $mopLogs) {
        Get-ChildItem $mopLogs -File | Where-Object { $_.LastWriteTime -gt (Get-Item $RunDir).CreationTime } | Copy-Item -Destination $RunDir -Force
    }

    # --- Restore ----------------------------------------------------------------------------------
    # Every restore step runs independently: one failure must never skip the others.
    Step 'Restoring the save folder, PlayerPrefs and Mods folder...'
    $restoreSteps = @(
        @{ Name = 'save';  Do = {
            Get-ChildItem $SaveDir -Force | Remove-Item -Recurse -Force
            Copy-Item -Recurse -Force (Join-Path (Join-Path $RunDir 'save-backup') '*') $SaveDir } },
        # reg.exe prints its success message to stderr; run it through cmd so PowerShell does not treat it as an error.
        @{ Name = 'prefs'; Do = {
            & cmd.exe /c "reg import `"$(Join-Path $RunDir 'prefs.reg')`" >nul 2>&1"
            if ($LASTEXITCODE -ne 0) { throw "reg import exit code $LASTEXITCODE" } } },
        @{ Name = 'bot';   Do = {
            Remove-Item (Join-Path $ModsDir 'MOPR.TestBot.dll') -Force -ErrorAction SilentlyContinue
            if (Test-Path $BotCfgDir) { Remove-Item -Recurse -Force $BotCfgDir } } },
        @{ Name = 'mopr';  Do = {
            if ($hadUserMopr) { Copy-Item (Join-Path $Stash 'MOPR.dll') $userMopr -Force }
            elseif (Test-Path $userMopr) { Remove-Item $userMopr -Force } } },
        @{ Name = 'mopr6'; Do = {
            if ($WithMopr5 -and (Test-Path $userMopr5x)) { Remove-Item $userMopr5x -Force }
            if (-not $hadMopr6Cfg -and (Test-Path $Mopr6CfgDir)) {
                Copy-Item -Recurse -Force $Mopr6CfgDir (Join-Path $RunDir 'mopr6-config-after')
                Remove-Item -Recurse -Force $Mopr6CfgDir
            } } },
        @{ Name = 'disabled-mods'; Do = {
            foreach ($d in $Disabled) {
                $s = Join-Path $DisabledStash "$d.dll"
                if (Test-Path $s) { Move-Item $s (Join-Path $ModsDir "$d.dll") -Force }
                if (-not (Test-Path (Join-Path $ModsDir "$d.dll"))) { throw "$d.dll is not back in Mods" }
            } } },
        @{ Name = 'mod-settings'; Do = {
            foreach ($dir in @(Get-ChildItem $OtherCfgBackup -Directory -Force)) {
                $dst = Join-Path $ModSettingsDir $dir.Name
                if (Test-Path $dst) { Remove-Item -Recurse -Force $dst }
                Copy-Item -Recurse -Force $dir.FullName $dst
            } } },
        @{ Name = 'mopr-config'; Do = {
            # The run's state of the folder is kept for inspection, then the user's folder comes back exactly.
            if (Test-Path $MoprCfgDir) { Copy-Item -Recurse -Force $MoprCfgDir (Join-Path $RunDir 'mopr-config-after') }
            if ($hadMoprCfg) {
                if (Test-Path $MoprCfgDir) { Remove-Item -Recurse -Force $MoprCfgDir }
                Copy-Item -Recurse -Force $CfgBackup $MoprCfgDir
            } elseif (Test-Path $MoprCfgDir) { Remove-Item -Recurse -Force $MoprCfgDir } } }
    )
    foreach ($s in $restoreSteps) {
        try { & $s.Do } catch { Step "Restore step '$($s.Name)' FAILED: $($_.Exception.Message)" 'Red'; $exitCode = 5 }
    }

    # Verify the save byte-for-byte against the backup.
    $hashList = { param($root) Get-ChildItem $root -Recurse -File | ForEach-Object { $_.FullName.Substring($root.Length) + '|' + (Get-FileHash $_.FullName -Algorithm MD5).Hash } | Sort-Object }
    $now = & $hashList $SaveDir
    $bak = & $hashList (Join-Path $RunDir 'save-backup')
    $diff = @(Compare-Object @($now) @($bak)).Count
    if ($diff -eq 0) { Step "Save verified: $(@($now).Count) files identical to the backup. Run folder: $RunDir" 'Green' }
    else { Step "SAVE MISMATCH ($diff differences)! Backup is in $RunDir\save-backup" 'Red'; $exitCode = 6 }

    if ($hadMoprCfg) {
        $nowCfg = & $hashList $MoprCfgDir
        $bakCfg = & $hashList $CfgBackup
        $cfgDiff = @(Compare-Object @($nowCfg) @($bakCfg)).Count
        if ($cfgDiff -eq 0) { Step "MOPR settings verified: $(@($nowCfg).Count) files identical to the backup." 'Green' }
        else { Step "MOPR SETTINGS MISMATCH ($cfgDiff differences)! Backup is in $CfgBackup" 'Red'; $exitCode = 7 }
    }

    $nowOther = @(Get-ChildItem $OtherCfgBackup -Directory -Force | ForEach-Object { & $hashList (Join-Path $ModSettingsDir $_.Name) | ForEach-Object { $_ } })
    $bakOther = @(Get-ChildItem $OtherCfgBackup -Directory -Force | ForEach-Object { & $hashList $_.FullName | ForEach-Object { $_ } })
    $otherDiff = @(Compare-Object $nowOther $bakOther).Count
    if ($otherDiff -eq 0) { Step "Other mods' settings verified: $($nowOther.Count) files identical to the backup." 'Green' }
    else { Step "OTHER MOD SETTINGS MISMATCH ($otherDiff differences)! Backup is in $OtherCfgBackup" 'Red'; $exitCode = 8 }
}

# --- Summary --------------------------------------------------------------------------------------
$reportPath = Join-Path $RunDir 'report.json'
if (Test-Path $reportPath) {
    $r = Get-Content $reportPath -Raw | ConvertFrom-Json
    Step "Report: variant=$($r.Variant) MOPR=$($r.MoprPresent) MOPR6=$($r.Mopr6Present) load=$([math]::Round($r.LoadSeconds,1))s error=$($r.Error)"
    if ($r.InputFrames -gt 0 -and $r.InputLocked) { Step "  real input in $($r.InputFrames) frame(s) ($($r.InputFirst)), kept from the game by the input lock" }
    elseif ($r.InputFrames -gt 0) { Step "  USER INPUT reached the game in $($r.InputFrames) frame(s), first: $($r.InputFirst) - not valid for a comparison" 'Yellow' }
    foreach ($m in $r.Measures) { Step ("  measure {0,-14} [{6,-6}] {1,6:0.0} fps  1%low {2,6:0.0}  p99 {3,6:0.0} ms  GC {4}  FSMs {5}" -f $m.Label, $m.AvgFps, $m.Low1PercentFps, $m.FrameMsP99, $m.GcCollections, $m.ActiveFsms, $m.EngineMode) }
    foreach ($p in $r.Profiles) { Step ("  profile {0,-14} [{6,-6}] frame {1:0.00} ms  scripts {2:0.00} ms (corr. {4:0.00})  PlayMaker {3:0.00} ms (corr. {5:0.00})" -f $p.Label, $p.FrameMsAvg, $p.ScriptMsPerFrame, $p.PlayMakerMsPerFrame, $p.ScriptMsPerFrameCorrected, $p.PlayMakerMsPerFrameCorrected, $p.EngineMode) }
    foreach ($c in $r.Compares) { Step ("  compare {0} -> {1}: {2} changed / {3}" -f $c.A, $c.B, $c.Changed, $c.Common) }
    foreach ($e in $r.Engine) { Step ("  engine  {0,-14} mode {1}, shadow mismatches {2}" -f $e.Label, $e.Mode, $e.ShadowMismatches) }
    foreach ($a in $r.AbTests) { Step ("  ab      {0,-14} {1} {2:0.0} fps / {3:0.00} ms / {9:0.00} MB/s  vs  {4} {5:0.0} fps / {6:0.00} ms / {10:0.00} MB/s  -> {7:+0.0;-0.0}% avg, {8:+0.0;-0.0}% median" -f $a.Label, $a.A, $a.AvgFpsA, $a.MedianMsA, $a.B, $a.AvgFpsB, $a.MedianMsB, $a.FpsGainPercent, $a.MedianGainPercent, $a.AllocMBPerSecA, $a.AllocMBPerSecB) }
    if ($r.GcPauseMs) { Step ("  gc pause (full collection): {0} ms" -f (($r.GcPauseMs | ForEach-Object { '{0:0.0}' -f $_ }) -join ', ')) }
}
# Written last: the run folder is complete (collected and restored) - for scripts that wait for a run.
Set-Content -Path (Join-Path $RunDir 'run.done') -Value $exitCode
exit $exitCode
