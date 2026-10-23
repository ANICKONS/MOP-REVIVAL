<#
.SYNOPSIS
    Publishes a GitHub release of MOP - REVIVAL: builds MOPR.dll, asks for the title and the description, uploads.

.DESCRIPTION
    1. Checks: GitHub CLI logged in, on the main branch, no uncommitted changes in the mod's code, everything pushed,
       and the version in src/MOPR/Properties/AssemblyInfo.cs (BuildInfo.Version) not released yet.
    2. Builds MOPR.dll (Release) against the real game assemblies. A stub build can't be published.
    3. Asks for the title and the description in the console (Markdown, several lines).
    4. Shows everything and asks for confirmation.
    5. Creates the release as a draft, uploads MOPR.dll, checks the upload, publishes it (GitHub creates the tag
       v<version> on the pushed commit), downloads the public file again and compares its SHA-256.
       If something fails before publishing, the draft is deleted: nothing half-done stays on GitHub.

    New version: raise BuildInfo.Version, update CHANGELOG.md and src/MOPR/Resources/changelog.txt, commit, push,
    then run this script.

    Same version again (-Update): the release of the current version already exists and gets the new build. Same
    checks and build; the title and the description can be kept (the SHA-256 in the footer is updated) or replaced.
    The new MOPR.dll is uploaded next to the old one first, then the text is changed, the tag moves to the pushed
    commit, and only then the old file is deleted and the new one renamed to MOPR.dll. If something fails before the
    old file is deleted, everything is put back. Editing a release notifies nobody.
    Players who already have this version are not told about the update: the mod's update check compares the
    version number only.

.PARAMETER Title
    Release title (otherwise asked; default "MOP - REVIVAL <version>").
.PARAMETER Notes
    Release description (otherwise asked). "." = the section of this version from the in-game changelog.
.PARAMETER NotesFile
    Release description from a file (Markdown).
.PARAMETER NoFooter
    Don't add the standard footer (installation, compatibility site, SHA-256).
.PARAMETER Prerelease
    Mark as a pre-release (automatic when MOP.SubVersion is set, e.g. BETA_1).
.PARAMETER Draft
    Upload, but don't publish: the draft stays on GitHub for review (Releases -> Edit -> Publish release).
.PARAMETER DryRun
    Checks, build and the preview only. Nothing is uploaded.
.PARAMETER Yes
    Don't ask for confirmation.
.PARAMETER AllowDirty
    For tests only: build even with uncommitted changes (the tag would point to a commit without them).
.PARAMETER Update
    Update the existing release of this version instead of publishing a new one (see above).
.PARAMETER KeepNotes
    With -Update: keep the current description without asking (only its SHA-256 line is updated).

.EXAMPLE
    .\scripts\release.ps1
.EXAMPLE
    .\scripts\release.ps1 -DryRun
.EXAMPLE
    .\scripts\release.ps1 -Title 'MOP - REVIVAL 5.1.1' -Notes '.' -Yes
.EXAMPLE
    .\scripts\release.ps1 -Update
.EXAMPLE
    .\scripts\release.ps1 -Update -Notes '.'
#>
[CmdletBinding()]
param(
    [string]$Title = '',
    [string]$Notes = '',
    [string]$NotesFile = '',
    [switch]$NoFooter,
    [switch]$Prerelease,
    [switch]$Draft,
    [switch]$DryRun,
    [switch]$Yes,
    [switch]$AllowDirty,
    [switch]$Update,
    [switch]$KeepNotes,
    [string]$GameDir = ''
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # Windows PowerShell draws a progress bar for every uploaded chunk
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

$Root = Split-Path -Parent $PSScriptRoot
$CompatSite = 'https://anickons.github.io/MOP-REVIVAL/'
$warnings = New-Object System.Collections.Generic.List[string]

function Step([string]$m, [string]$c = 'Cyan') { Write-Host "[release] $m" -ForegroundColor $c }
function Warn([string]$m) { $warnings.Add($m); Write-Host "[release] WARNING: $m" -ForegroundColor Yellow }
function Fail([string]$m) { Write-Host "[release] $m" -ForegroundColor Red; exit 1 }
function Ask([string]$q) {
    if ($Yes) { return $true }
    Write-Host "$q [y/N] " -ForegroundColor White -NoNewline
    return (Read-Host) -match '^(y|yes|д|да)$'
}
# Native tools write progress to stderr; in Windows PowerShell that must not count as a failure, only the exit code does.
function Native([scriptblock]$Command) {
    $old = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { $out = @(& $Command 2>&1 | ForEach-Object { "$_" }) } finally { $ErrorActionPreference = $old }
    return [pscustomobject]@{ Code = $LASTEXITCODE; Out = $out }
}
function Git {
    $a = $args
    $r = Native { git.exe -C $Root @a }   # git.exe: "git" would be this function (names are case-insensitive)
    if ($r.Code -ne 0) { throw "git $($a -join ' ') failed: $($r.Out -join ' ')" }
    return $r.Out
}

# ===== 1. Checks =====================================================================================

# --- GitHub CLI -------------------------------------------------------------------------------------
$gh = (Get-Command gh -ErrorAction SilentlyContinue).Source
if (-not $gh) { $gh = Join-Path $env:ProgramFiles 'GitHub CLI\gh.exe' }
if (-not (Test-Path $gh)) { Fail 'GitHub CLI not found. Install it: winget install GitHub.cli' }
if ((Native { & $gh auth status }).Code -ne 0) { Fail 'GitHub CLI is not logged in. Run: gh auth login' }

$remote = (Git remote get-url origin) | Select-Object -First 1
$m = [regex]::Match($remote, 'github\.com[:/]([^/]+)/([^/]+?)(\.git)?$')
if (-not $m.Success) { Fail "origin is not a GitHub repository: $remote" }
$Repo = "$($m.Groups[1].Value)/$($m.Groups[2].Value)"

# --- Version ----------------------------------------------------------------------------------------
$info = [IO.File]::ReadAllText((Join-Path $Root 'src\MOPR\Properties\AssemblyInfo.cs'))
$Version = [regex]::Match($info, 'const string Version = "([^"]+)"').Groups[1].Value
if (-not $Version) { Fail 'BuildInfo.Version not found in src\MOPR\Properties\AssemblyInfo.cs' }
$mopCs = [IO.File]::ReadAllText((Join-Path $Root 'src\MOPR\src\MOP.cs'))
$Sub = [regex]::Match($mopCs, 'SubVersion = "([^"]*)"').Groups[1].Value
$Tag = if ($Sub) { "v$Version-$Sub" } else { "v$Version" }
if ($Sub) { $Prerelease = $true }
Step "MOP - REVIVAL $Version$(if ($Sub) { " $Sub" }) -> tag $Tag on $Repo"

# --- Git state --------------------------------------------------------------------------------------
# --force: a tag moved on GitHub by -Update replaces the local one (a plain fetch would fail on it).
Git fetch origin --tags --force --quiet | Out-Null
$branch = (Git rev-parse --abbrev-ref HEAD) | Select-Object -First 1
$default = ((Git symbolic-ref --short refs/remotes/origin/HEAD) | Select-Object -First 1) -replace '^origin/', ''
if ($branch -ne $default) {
    if ($DryRun) { Warn "on branch $branch, releases are made from $default" } else { Fail "Switch to $default first (now on $branch)." }
}

$dirty = @(Git status --porcelain) | Where-Object { $_.Length -gt 3 }
# Porcelain: "XY path" (renames "XY old -> new"). Everything under src/MOPR/ and the stub overlay goes into the DLL.
$codeDirty = @($dirty | Where-Object { $_.Substring(3).Trim('"') -match '^(src/MOPR/|refs/overlay/)' -or $_ -match ' -> "?(src/MOPR/|refs/overlay/)' })
if ($codeDirty.Count -gt 0) {
    $list = ($codeDirty | Select-Object -First 8) -join "`n  "
    if ($DryRun -or $AllowDirty) { Warn "uncommitted changes in the mod's code (the release would not match the tag):`n  $list" }
    else { Fail "Commit and push these changes first, the release must match its tag:`n  $list" }
}
$otherDirty = @($dirty | Where-Object { $codeDirty -notcontains $_ })
if ($otherDirty.Count -gt 0) { Warn "$($otherDirty.Count) other uncommitted file(s) (not part of the DLL), e.g. $($otherDirty[0].Trim())" }

$ahead = [int]((Git rev-list --count "origin/$default..HEAD") | Select-Object -First 1)
$behind = [int]((Git rev-list --count "HEAD..origin/$default") | Select-Object -First 1)
if ($behind -gt 0) { Fail "Your $branch is $behind commit(s) behind origin. Pull first (git pull)." }
if ($ahead -gt 0) {
    if ($DryRun) { Warn "$ahead commit(s) not pushed yet" }
    elseif (Ask "$ahead commit(s) are not pushed yet. Push them now?") {
        Git push origin $branch | Out-Null
        Step "Pushed $ahead commit(s)." 'Green'
    }
    else { Fail 'The release must point to a pushed commit. Push first (git push).' }
}
$Commit = (Git rev-parse HEAD) | Select-Object -First 1
$CommitInfo = (Git log -1 --format='%h %s') | Select-Object -First 1

# --- Already released? ------------------------------------------------------------------------------
$tokenRun = Native { & $gh auth token }
if ($tokenRun.Code -ne 0 -or -not $tokenRun.Out) { Fail 'Could not get the GitHub token from the GitHub CLI (gh auth token).' }
$token = ($tokenRun.Out | Select-Object -First 1).Trim()
$Headers = @{
    Authorization          = "Bearer $token"
    Accept                 = 'application/vnd.github+json'
    'X-GitHub-Api-Version' = '2022-11-28'
    'User-Agent'           = 'mopr-release-script'
}
function Api([string]$Method, [string]$Path, $Body = $null) {
    $uri = if ($Path -like 'https://*') { $Path } else { "https://api.github.com/$Path" }
    $p = @{ Method = $Method; Uri = $uri; Headers = $Headers }
    if ($null -ne $Body) {
        $p.Body = [Text.Encoding]::UTF8.GetBytes(($Body | ConvertTo-Json -Depth 6))
        $p.ContentType = 'application/json; charset=utf-8'
    }
    try { return Invoke-RestMethod @p -ErrorAction Stop }
    catch {
        $detail = if ($_.ErrorDetails -and $_.ErrorDetails.Message) { $_.ErrorDetails.Message } else { $_.Exception.Message }
        throw "GitHub API $Method $Path failed: $detail"
    }
}

$existing = @(Api GET "repos/$Repo/releases?per_page=100") | Where-Object { $_.tag_name -eq $Tag }
$Old = $null
$OldTagCommit = ''
if ($Update) {
    if ($Draft) { Fail '-Draft does not go with -Update: the release stays as it is (published or draft).' }
    if (-not $existing) { Fail "There is no release for $Tag to update. Run without -Update to publish it." }
    $Old = $existing | Select-Object -First 1
    # The commit the tag points to now (an annotated tag points to a tag object first).
    try {
        $ref = Api GET "repos/$Repo/git/ref/tags/$Tag"
        $OldTagCommit = if ($ref.object.type -eq 'tag') { (Api GET "repos/$Repo/git/tags/$($ref.object.sha)").object.sha } else { $ref.object.sha }
    }
    catch { $OldTagCommit = '' }   # a draft has no tag yet
    $oldAsset = $Old.assets | Where-Object { $_.name -eq 'MOPR.dll' } | Select-Object -First 1
    Step "Updating the existing $(if ($Old.draft) { 'draft' } else { 'release' }) $Tag ($($Old.html_url))."
}
else {
    if ($KeepNotes) { Fail '-KeepNotes only goes with -Update.' }
    if (@(Git ls-remote --tags origin "refs/tags/$Tag") | Where-Object { $_ }) {
        Fail "$Tag is already released. To replace its file and description run: .\scripts\release.ps1 -Update`n" +
             "For a new version raise BuildInfo.Version in src\MOPR\Properties\AssemblyInfo.cs (and the changelogs), commit, push, run again."
    }
    if ($existing) {
        $e = $existing | Select-Object -First 1
        Fail "There is already a $(if ($e.draft) { 'draft ' })release for $Tag ($($e.html_url)). Publish or delete it on GitHub first, or run with -Update."
    }
}

# --- Changelogs -------------------------------------------------------------------------------------
$changelog = [IO.File]::ReadAllText((Join-Path $Root 'CHANGELOG.md'), [Text.Encoding]::UTF8)
$h = [regex]::Match($changelog, "(?m)^## $([regex]::Escape($Version))\b(.*)$")
if (-not $h.Success) { Warn "CHANGELOG.md has no section for $Version" }
elseif ($h.Groups[1].Value -match 'unreleased') { Warn "CHANGELOG.md still says ""$($h.Value.Trim())""" }
$gameLog = [IO.File]::ReadAllText((Join-Path $Root 'src\MOPR\Resources\changelog.txt'), [Text.Encoding]::UTF8)
$g = [regex]::Match($gameLog, "(?ms)^### $([regex]::Escape($Version))\b[^\r\n]*\r?\n(.*?)(?=^### |\z)")
if (-not $g.Success) { Warn "the in-game changelog (src\MOPR\Resources\changelog.txt) has no section for $Version" }

# ===== 2. Build ======================================================================================

function Find-GameDir {
    $roots = New-Object System.Collections.Generic.List[string]
    foreach ($key in 'HKCU:\Software\Valve\Steam', 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam', 'HKLM:\SOFTWARE\Valve\Steam') {
        try {
            $p = Get-ItemProperty -Path $key -ErrorAction Stop
            foreach ($name in 'SteamPath', 'InstallPath') { if ($p.$name) { $roots.Add(($p.$name -replace '/', '\')) } }
        } catch { }
    }
    $roots.Add("${env:ProgramFiles(x86)}\Steam")
    $libraries = New-Object System.Collections.Generic.List[string]
    foreach ($r in ($roots | Select-Object -Unique)) {
        $libraries.Add($r)
        $vdf = Join-Path $r 'steamapps\libraryfolders.vdf'
        if (Test-Path $vdf) {
            foreach ($x in (Select-String -Path $vdf -Pattern '"path"\s+"([^"]+)"')) { $libraries.Add(($x.Matches[0].Groups[1].Value -replace '\\\\', '\')) }
        }
    }
    foreach ($lib in ($libraries | Select-Object -Unique)) {
        $c = Join-Path $lib 'steamapps\common\My Summer Car'
        if (Test-Path (Join-Path $c 'mysummercar_Data\Managed\UnityEngine.dll')) { return $c }
    }
    return ''
}
if (-not $GameDir) { $GameDir = Find-GameDir }
$ManagedDir = if ($GameDir) { Join-Path $GameDir 'mysummercar_Data\Managed' } else { '' }
if (-not $ManagedDir -or -not (Test-Path (Join-Path $ManagedDir 'MSCLoader.dll'))) {
    Fail 'My Summer Car with MSCLoader not found: a release must be built against the real game assemblies. Pass -GameDir.'
}

$Dotnet = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
if (-not (Test-Path $Dotnet)) { $Dotnet = 'dotnet' }
$OutDir = Join-Path ([IO.Path]::GetTempPath()) "mopr-release-$Version"
if (Test-Path $OutDir) { Remove-Item -Recurse -Force $OutDir }
Step "Building MOPR.dll (Release, real game assemblies from $GameDir)..."
$project = Join-Path $Root 'src\MOPR\MOPR.csproj'
$build = Native { & $Dotnet build $project -c Release -nologo --no-incremental -v q "-p:ManagedDir=$ManagedDir" "-p:OutDir=$OutDir\" }
if ($build.Code -ne 0) {
    $build.Out | Where-Object { $_ -match 'error' } | Select-Object -First 15 | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    Fail 'Build FAILED.'
}
$Dll = Join-Path $OutDir 'MOPR.dll'
if (-not (Test-Path $Dll)) { Fail "Build output missing: $Dll" }

$asm = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($Dll))
$built = ($asm.GetCustomAttributes([Reflection.AssemblyInformationalVersionAttribute], $false) | Select-Object -First 1).InformationalVersion
if ($built -ne $Version) { Fail "The built DLL says version $built, expected $Version (a stub build can't be published)." }
$Size = (Get-Item $Dll).Length
$Sha = (Get-FileHash $Dll -Algorithm SHA256).Hash.ToLowerInvariant()
Step ("Build OK: {0:N0} KB, SHA-256 {1}" -f ($Size / 1KB), $Sha) 'Green'

# ===== 3. Title and description ======================================================================

$defaultTitle = if ($Update -and $Old.name) { $Old.name } else { "MOP - REVIVAL $Version$(if ($Sub) { " $Sub" })" }
if (-not $Title) {
    Write-Host ''
    Write-Host "Title (Enter = $(if ($Update) { 'keep ' })""$defaultTitle""):" -ForegroundColor White
    Write-Host '> ' -NoNewline
    $Title = (Read-Host).Trim()
}
if (-not $Title) { $Title = $defaultTitle }

function FromGameChangelog {
    if (-not $g.Success) { return $null }
    $lines = $g.Groups[1].Value -split '\r?\n' | Where-Object { $_.Trim() } | ForEach-Object {
        $l = $_.Trim()
        if ($l.StartsWith('-')) { '- ' + $l.Substring(1).Trim() } else { $l }
    }
    return ($lines -join "`n")
}

$keep = $false
if ($NotesFile) {
    $Notes = [IO.File]::ReadAllText((Resolve-Path $NotesFile), [Text.Encoding]::UTF8)
}
elseif ($KeepNotes) { $keep = $true }
elseif (-not $Notes) {
    Write-Host ''
    Write-Host 'Description (Markdown is fine, several lines). Finish with a line containing only "."' -ForegroundColor White
    Write-Host "Just ""."" right away = the $Version section of the in-game changelog." -ForegroundColor DarkGray
    if ($Update) { Write-Host 'Just Enter right away = keep the current description (its SHA-256 line is updated).' -ForegroundColor DarkGray }
    $buffer = New-Object System.Collections.Generic.List[string]
    $first = $true
    while ($true) {
        Write-Host '> ' -NoNewline
        $line = Read-Host
        if ($first -and $Update -and $line -eq '') { $keep = $true; break }
        $first = $false
        if ($line -eq '.') { break }
        $buffer.Add($line)
    }
    if (-not $keep) {
        $Notes = ($buffer -join "`n").Trim()
        if (-not $Notes) { $Notes = '.' }
    }
}
if (-not $keep -and $Notes.Trim() -eq '.') {
    $Notes = FromGameChangelog
    if (-not $Notes) { Fail "No $Version section in src\MOPR\Resources\changelog.txt to take the description from." }
}

$shaPattern = '(SHA-256 of MOPR\.dll: `)[0-9a-fA-F]{64}(`)'
if ($keep) {
    $Notes = if ($Old.body) { [string]$Old.body } else { '' }
    if ($Notes -match $shaPattern) { $Notes = [regex]::Replace($Notes, $shaPattern, "`${1}$Sha`${2}") }
    else { Warn 'the current description has no SHA-256 line; it is kept as it is' }
}
elseif (-not $NoFooter) {
    $Notes = $Notes.TrimEnd() + "`n`n---`n" +
        "**Install:** put ``MOPR.dll`` from below into the game's ``Mods`` folder. Requires [MSCLoader](https://github.com/piotrulos/MSCModLoader/releases) 1.4.2 or newer.`n`n" +
        "**Other mods:** see the [compatibility list]($CompatSite). Full list of changes: [CHANGELOG.md](https://github.com/$Repo/blob/main/CHANGELOG.md)`n`n" +
        "<sub>SHA-256 of MOPR.dll: ``$Sha``</sub>"
}

# ===== 4. Preview ====================================================================================

function Short([string]$sha) { if ($sha) { $sha.Substring(0, [Math]::Min(7, $sha.Length)) } else { '(none)' } }
$moveTag = $Update -and $OldTagCommit -and $OldTagCommit -ne $Commit

Write-Host ''
Write-Host '==================================================================' -ForegroundColor DarkGray
if ($Update) {
    Write-Host " UPDATE:  $(if ($Old.draft) { 'draft' } else { 'published release' }) $Tag  ($($Old.html_url))" -ForegroundColor White
    Write-Host " Title:   $(if ($Title -eq $Old.name) { "$Title  (unchanged)" } else { "$($Old.name)  ->  $Title" })"
    if (-not $OldTagCommit) { Write-Host " Tag:     $Tag  on  $CommitInfo  (created when the draft is published)" }
    elseif ($moveTag) { Write-Host " Tag:     $Tag  moves  $(Short $OldTagCommit)  ->  $CommitInfo" -ForegroundColor Yellow }
    else { Write-Host " Tag:     $Tag  stays on  $CommitInfo" }
    $oldSize = if ($oldAsset) { '{0:N0} bytes' -f $oldAsset.size } else { 'none' }
    Write-Host (" File:    MOPR.dll  {0}  ->  {1:N0} bytes" -f $oldSize, $Size)
    Write-Host " Text:    $(if ($keep) { 'current description kept' } elseif ($Notes -eq $Old.body) { 'unchanged' } else { 'new description' })"
}
else {
    Write-Host " Title:   $Title"
    Write-Host " Tag:     $Tag  on  $CommitInfo"
    Write-Host (" File:    MOPR.dll  ({0:N0} bytes)" -f $Size)
    Write-Host " Type:    $(if ($Prerelease) { 'pre-release' } else { 'release (shown as Latest)' })$(if ($Draft) { ', kept as draft' })"
}
Write-Host '------------------------------------------------------------------' -ForegroundColor DarkGray
Write-Host $Notes
Write-Host '==================================================================' -ForegroundColor DarkGray
foreach ($w in $warnings) { Write-Host " ! $w" -ForegroundColor Yellow }

if ($DryRun) { Remove-Item -Recurse -Force $OutDir -ErrorAction SilentlyContinue; Step 'Dry run: nothing uploaded.' 'Green'; exit 0 }
$what = if ($Update) { "Update $Tag on GitHub (file, text$(if ($moveTag) { ', tag' }); nobody gets notified)" }
        elseif ($Draft) { "Upload $Tag as a draft" } else { "Publish $Tag (public: watchers get notified)" }
if (-not (Ask "$what?")) { Remove-Item -Recurse -Force $OutDir -ErrorAction SilentlyContinue; Step 'Cancelled, nothing uploaded.' 'Yellow'; exit 0 }

# ===== 5. Upload =====================================================================================

function Upload-Dll([long]$ReleaseId, [string]$Name) {
    $uri = "https://uploads.github.com/repos/$Repo/releases/$ReleaseId/assets?name=$Name"
    try { $a = Invoke-RestMethod -Method Post -Uri $uri -Headers $Headers -ContentType 'application/octet-stream' -InFile $Dll -ErrorAction Stop }
    catch { throw "Upload failed: $(if ($_.ErrorDetails) { $_.ErrorDetails.Message } else { $_.Exception.Message })" }
    if ($a.size -ne $Size -or $a.state -ne 'uploaded') { throw "Upload incomplete: $($a.size) of $Size bytes, state $($a.state)." }
    return $a
}

$release = $null
$public = $null
if ($Update) {
    $TempName = 'MOPR.dll.new'
    $temp = $null
    $textChanged = $false
    $tagMoved = $false
    $oldDeleted = $false
    try {
        # A file left by an earlier failed run.
        foreach ($a in @($Old.assets | Where-Object { $_.name -eq $TempName })) { Api DELETE "repos/$Repo/releases/assets/$($a.id)" | Out-Null }

        Step 'Uploading the new MOPR.dll next to the old one...'
        $temp = Upload-Dll $Old.id $TempName

        if ($Title -ne $Old.name -or $Notes -ne $Old.body) {
            Step 'Updating the title and the description...'
            Api PATCH "repos/$Repo/releases/$($Old.id)" ([ordered]@{ name = $Title; body = $Notes }) | Out-Null
            $textChanged = $true
        }

        if ($moveTag) {
            Step "Moving the tag $Tag to $(Short $Commit)..."
            Api PATCH "repos/$Repo/git/refs/tags/$Tag" ([ordered]@{ sha = $Commit; force = $true }) | Out-Null
            $tagMoved = $true
        }
        elseif ($Old.draft -and $Old.target_commitish -ne $Commit) {
            # A draft has no tag yet: it is created on this commit when the draft is published.
            Api PATCH "repos/$Repo/releases/$($Old.id)" ([ordered]@{ target_commitish = $Commit }) | Out-Null
        }

        if ($oldAsset) {
            Step 'Replacing the old file...'
            Api DELETE "repos/$Repo/releases/assets/$($oldAsset.id)" | Out-Null
            $oldDeleted = $true
        }
    }
    catch {
        Write-Host "[release] $($_.Exception.Message)" -ForegroundColor Red
        # Nothing of the old release is lost yet: put everything back.
        try {
            if ($tagMoved) { Api PATCH "repos/$Repo/git/refs/tags/$Tag" ([ordered]@{ sha = $OldTagCommit; force = $true }) | Out-Null }
            if ($textChanged) { Api PATCH "repos/$Repo/releases/$($Old.id)" ([ordered]@{ name = $Old.name; body = $Old.body }) | Out-Null }
            if ($temp) { Api DELETE "repos/$Repo/releases/assets/$($temp.id)" | Out-Null }
            Step 'Put back: the release is as it was.' 'Yellow'
        }
        catch { Write-Host "[release] Could not put everything back ($($_.Exception.Message)). Check the release: $($Old.html_url)" -ForegroundColor Red }
        exit 1
    }

    try { $temp = Api PATCH "repos/$Repo/releases/assets/$($temp.id)" ([ordered]@{ name = 'MOPR.dll' }) }
    catch {
        Write-Host "[release] The new file is uploaded as $TempName but could not be renamed: $($_.Exception.Message)" -ForegroundColor Red
        Write-Host "[release] Rename it to MOPR.dll on GitHub (Edit release): $($Old.html_url)" -ForegroundColor Red
        exit 1
    }
    $release = Api GET "repos/$Repo/releases/$($Old.id)"
    $public = $temp.browser_download_url
}
else {
    try {
        Step 'Creating the draft...'
        $release = Api POST "repos/$Repo/releases" ([ordered]@{
            tag_name         = $Tag
            target_commitish = $Commit
            name             = $Title
            body             = $Notes
            draft            = $true
            prerelease       = [bool]$Prerelease
        })

        Step 'Uploading MOPR.dll...'
        $asset = Upload-Dll $release.id 'MOPR.dll'

        if ($Draft) {
            Step "Draft ready: $($release.html_url)" 'Green'
            Step 'Publish it on GitHub (Edit -> Publish release) or delete it there.' 'Green'
            exit 0
        }

        Step 'Publishing...'
        $release = Api PATCH "repos/$Repo/releases/$($release.id)" ([ordered]@{
            draft       = $false
            make_latest = $(if ($Prerelease) { 'false' } else { 'true' })
        })
    }
    catch {
        Write-Host "[release] $($_.Exception.Message)" -ForegroundColor Red
        if ($release -and $release.draft) {
            try { Api DELETE "repos/$Repo/releases/$($release.id)" | Out-Null; Step 'The draft was deleted, nothing is published.' 'Yellow' }
            catch { Write-Host "[release] Could not delete the draft, delete it on GitHub: $($release.html_url)" -ForegroundColor Red }
        }
        exit 1
    }
    $public = ($release.assets | Where-Object { $_.name -eq 'MOPR.dll' } | Select-Object -First 1).browser_download_url
}

# --- Check what the players download ----------------------------------------------------------------
$check = Join-Path $OutDir 'downloaded.dll'
$ok = $false
for ($i = 0; $i -lt 5 -and -not $ok; $i++) {
    try {
        Invoke-WebRequest -Uri $public -OutFile $check -UseBasicParsing -Headers @{ 'User-Agent' = 'mopr-release-script' } -ErrorAction Stop
        $ok = (Get-FileHash $check -Algorithm SHA256).Hash.ToLowerInvariant() -eq $Sha
    } catch { }
    if (-not $ok) { Start-Sleep -Seconds 3 }
}
if ($ok) { Step 'Checked: the public MOPR.dll is byte-identical to the build.' 'Green' }
else { Write-Host "[release] WARNING: could not verify the public file ($public). Check the release page." -ForegroundColor Yellow }

try { Copy-Item $Dll (Join-Path $Root 'dist\MOPR.dll') -Force } catch { }
# --force: the local tag follows a tag that -Update moved.
try { Git fetch origin --tags --force --quiet | Out-Null } catch { Write-Host "[release] git fetch --tags failed: $($_.Exception.Message)" -ForegroundColor Yellow }
Remove-Item -Recurse -Force $OutDir -ErrorAction SilentlyContinue

Step "$(if ($Update) { 'Updated' } else { 'Released' }) $Tag`: $($release.html_url)" 'Green'
