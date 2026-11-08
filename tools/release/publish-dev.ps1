<#
.SYNOPSIS
    Builds a development build of a commit and publishes it to this PC's dev-builds folder, where the installed
    BetterClipboard's update button offers it ("Dev build available").

.DESCRIPTION
    The local half of the update system (CLAUDE.md, section 2.39). Steps:

      1. Works out the build's version: the next release's number plus "-dev.<commits since the last release tag>.g<short
         commit>", e.g. 0.2.10-dev.12.gabc1234. The number is the one in Directory.Build.props at that commit, or the
         next patch when that one is already tagged (released). SemVer orders these builds by the commit count, and a
         release outranks every development build of its own number.
      2. Builds that commit, never the working tree: a temporary git worktree at the commit, then tools/release/package.ps1
         from that worktree (Release, self-contained, the same zip a release has). Uncommitted work - yours or another
         session's - is never in a published build.
      3. Publishes it as <dev-builds>\<version>\ with the zip, SHA256SUMS.txt, install.ps1, notes.md (the commits since the
         build published before it, or since the last release tag) and build.json, written last. Everything is prepared
         in a ".publishing-..." folder and renamed into place, so the app never sees half a build.
      4. Prunes the folder: keeps the newest -Keep builds and drops the ones an official release has superseded.
      5. With -Install: installs the build right away with its own install.ps1 (the bootstrap, for a copy whose update
         button cannot offer it yet), then starts BetterClipboard through Explorer.

    The app reads the folder only in a copy the installer installed (install.ps1, Settings > Apps lists it), and offers a
    build there when it is newer than the running version - before anything GitHub has. A watcher lights the update button
    the moment the build is renamed into place.

.PARAMETER Ref
    The commit to build: anything git rev-parse takes (a branch, a tag, a commit). Default: HEAD.

.PARAMETER Architectures
    x64 and/or arm64. Default: this PC's architecture (an update installs the package of the PC it runs on).

.PARAMETER DevBuildsDirectory
    The dev-builds folder. Default: <data directory>\dev-builds, where the data directory is
    $env:BETTERCLIPBOARD_DATA_DIR when set (an isolated instance reads its own folder) and
    %LOCALAPPDATA%\BetterClipboard otherwise.

.PARAMETER Keep
    How many builds to keep in the folder after publishing (newest by version). Default: 5.

.PARAMETER Install
    Also install the build now, with its own install.ps1 (-Package), and start BetterClipboard in the background. Needed
    once for a copy that predates the dev-builds folder (BetterClipboard 0.2.9 and older): from then on, the update button
    does it. It closes the running BetterClipboard, like any install.

.PARAMETER NoStart
    With -Install: leave BetterClipboard closed afterwards.

.PARAMETER Test
    Run the whole test suite in the worktree before packaging (dotnet test --solution BetterClipboard.sln), and publish
    nothing when a test fails.

.PARAMETER Force
    Build and publish again when the folder already has this version (the same commit): its old files are replaced.

.PARAMETER Version
    The version to stamp instead of the computed one. Must be SemVer (X.Y.Z, optionally -prerelease); keep "-dev." in it
    for a development build, or the update button treats it as a release.

.EXAMPLE
    pwsh tools/release/publish-dev.ps1

    Builds HEAD and publishes it; the installed app's update button offers it within a second.

.EXAMPLE
    pwsh tools/release/publish-dev.ps1 -Install

    The same, then installs it now (the first time, while the installed copy is still a release from GitHub).
#>
[CmdletBinding()]
param(
    [string] $Ref = 'HEAD',

    [ValidateSet('x64', 'arm64')]
    [string[]] $Architectures,

    [string] $DevBuildsDirectory,

    [ValidateRange(1, 50)]
    [int] $Keep = 5,

    [switch] $Install,

    [switch] $NoStart,

    [switch] $Test,

    [switch] $Force,

    [string] $Version
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

$AppName = 'BetterClipboard'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$clock = [Diagnostics.Stopwatch]::StartNew()

function Write-Step([string] $Message) { Write-Host "==> $Message" -ForegroundColor Cyan }
function Write-Note([string] $Message) { Write-Host "    $Message" }

function Invoke-Git {
    <#
    .SYNOPSIS
        Runs git in the repository and returns its standard output as lines.
    .PARAMETER Arguments
        git's arguments.
    .PARAMETER AllowFailure
        Return $null instead of throwing when git exits with an error (for questions such as "does this tag exist").
    .OUTPUTS
        System.String[] (the output lines), or $null after an allowed failure.
    .NOTES
        Throws with git's own message otherwise: a wrong -Ref must stop the script before anything is built.
    #>
    param([string[]] $Arguments, [switch] $AllowFailure)

    # git writes progress and hints to stderr; only its exit code tells failure from success.
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'

    # PowerShell decodes a native program's output with the console's code page; git writes UTF-8, and commit subjects
    # hold dashes and arrows that would turn into mojibake in the notes (CLAUDE.md, section 3.1: the release notes step).
    $encoding = $null
    try { $encoding = [Console]::OutputEncoding; [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false) } catch { $encoding = $null }
    try {
        $output = & git -C $root @Arguments 2>&1
        $code = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previous
        if ($encoding) { try { [Console]::OutputEncoding = $encoding } catch { } }
    }

    $lines = @($output | Where-Object { $_ -isnot [System.Management.Automation.ErrorRecord] } | ForEach-Object { [string] $_ })
    if ($code -ne 0) {
        if ($AllowFailure) { return $null }
        $errors = @($output | Where-Object { $_ -is [System.Management.Automation.ErrorRecord] } | ForEach-Object { $_.ToString() })
        throw "git $($Arguments -join ' ') failed (exit $code): $($errors -join ' ')"
    }

    return , $lines
}

function ConvertTo-SemVerParts([string] $Text) {
    <#
    .SYNOPSIS
        Splits a SemVer text into its numbers and pre-release identifiers for Compare-SemVer.
    .PARAMETER Text
        A version such as 0.2.10-dev.12.gabc1234 (a leading 'v' and build metadata are allowed).
    .OUTPUTS
        PSCustomObject with Core ([version], three parts) and Pre (string[], empty for a release), or $null for other text.
    #>
    $clean = ($Text.Trim() -replace '^[vV]', '') -replace '\+.*$', ''
    if ($clean -notmatch '^(\d+)\.(\d+)\.(\d+)(?:-([0-9A-Za-z.-]+))?$') { return $null }
    $pre = if ($Matches[4]) { @($Matches[4].Split('.')) } else { @() }
    return [pscustomobject]@{ Core = [version]::new([int]$Matches[1], [int]$Matches[2], [int]$Matches[3]); Pre = $pre }
}

function Compare-SemVer([string] $Left, [string] $Right) {
    <#
    .SYNOPSIS
        Compares two versions by SemVer precedence, exactly as the app's SemanticVersion does.
    .DESCRIPTION
        The three numbers first; then a release is newer than its pre-releases; then pre-release identifiers one by one,
        numbers (compared as numbers, of any length) below words, a longer list winning a tie.
    .PARAMETER Left
        One version.
    .PARAMETER Right
        The other.
    .OUTPUTS
        System.Int32: negative when Left is older, 0 when equal, positive when newer.
    .NOTES
        Throws for a text that is not a version.
    #>
    $a = ConvertTo-SemVerParts $Left
    $b = ConvertTo-SemVerParts $Right
    if ($null -eq $a -or $null -eq $b) { throw "Not a version: '$Left' or '$Right'." }
    $byCore = $a.Core.CompareTo($b.Core)
    if ($byCore -ne 0) { return $byCore }
    if ($a.Pre.Count -eq 0 -or $b.Pre.Count -eq 0) { return [int]($a.Pre.Count -eq 0) - [int]($b.Pre.Count -eq 0) }
    for ($i = 0; $i -lt [Math]::Min($a.Pre.Count, $b.Pre.Count); $i++) {
        $x = $a.Pre[$i]; $y = $b.Pre[$i]
        $xNumeric = $x -match '^\d+$'; $yNumeric = $y -match '^\d+$'
        if ($xNumeric -and $yNumeric) {
            # As the app does: without leading zeros, the longer number is the larger; equal lengths compare digit by digit.
            $x = $x.TrimStart('0'); $y = $y.TrimStart('0')
            $result = if ($x.Length -ne $y.Length) { $x.Length.CompareTo($y.Length) } else { [string]::CompareOrdinal($x, $y) }
        }
        elseif ($xNumeric -ne $yNumeric) {
            $result = if ($xNumeric) { -1 } else { 1 }
        }
        else {
            $result = [string]::CompareOrdinal($x, $y)
        }

        if ($result -ne 0) { return [Math]::Sign($result) }
    }

    return $a.Pre.Count.CompareTo($b.Pre.Count)
}

function Get-DevVersion([string] $Commit) {
    <#
    .SYNOPSIS
        The development version of a commit: <next release>-dev.<commits since the last release tag>.g<short commit>.
    .DESCRIPTION
        The next release is the <Version> of Directory.Build.props at that commit, or its next patch when that version is
        tagged already (the release is out, and the work after it goes into the next one). The commit count makes later
        commits newer by SemVer; the short commit, prefixed with "g" as git describe does, says which commit it is and is
        never a number (a numeric identifier with a leading zero is not valid SemVer).
    .PARAMETER Commit
        The full commit id.
    .OUTPUTS
        PSCustomObject with Version, LastTag (or $null) and Count.
    #>
    $props = (Invoke-Git @('show', "${Commit}:Directory.Build.props")) -join "`n"
    if ($props -notmatch '<Version>\s*([^<\s]+)\s*</Version>') { throw "Directory.Build.props at $Commit has no <Version>." }
    $declared = ConvertTo-SemVerParts $Matches[1]
    if ($null -eq $declared) { throw "Directory.Build.props at $Commit declares '$($Matches[1])', which is not a version." }

    $next = $declared.Core
    if ($null -ne (Invoke-Git @('rev-parse', '-q', '--verify', "refs/tags/v$next") -AllowFailure)) {
        $next = [version]::new($next.Major, $next.Minor, $next.Build + 1)
    }

    $lastTag = Invoke-Git @('describe', '--tags', '--match', 'v[0-9]*', '--abbrev=0', $Commit) -AllowFailure
    $lastTag = if ($lastTag) { $lastTag[0].Trim() } else { $null }
    $range = if ($lastTag) { "$lastTag..$Commit" } else { $Commit }
    $count = [int] (Invoke-Git @('rev-list', '--count', $range))[0]
    $short = (Invoke-Git @('rev-parse', '--short=7', $Commit))[0].Trim()
    return [pscustomobject]@{ Version = "$next-dev.$count.g$short"; LastTag = $lastTag; Count = $count }
}

function Get-PublishedBuilds([string] $Folder) {
    <#
    .SYNOPSIS
        The builds in a dev-builds folder: its subfolders with a build.json, as the app reads them.
    .PARAMETER Folder
        The dev-builds folder.
    .OUTPUTS
        PSCustomObject[] with Version, Commit and Path, newest version first; empty without builds.
    #>
    if (-not (Test-Path -LiteralPath $Folder -PathType Container)) { return , @() }
    $builds = foreach ($dir in Get-ChildItem -LiteralPath $Folder -Directory) {
        if ($dir.Name.StartsWith('.')) { continue }
        $file = Join-Path $dir.FullName 'build.json'
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { continue }
        try { $info = Get-Content -LiteralPath $file -Raw | ConvertFrom-Json } catch { continue }
        if (-not ($info.PSObject.Properties['version'] -and (ConvertTo-SemVerParts ([string] $info.version)))) { continue }
        $commit = if ($info.PSObject.Properties['commit']) { [string] $info.commit } else { '' }
        [pscustomobject]@{ Version = [string] $info.version; Commit = $commit; Path = $dir.FullName }
    }

    $list = [Collections.Generic.List[object]]::new()
    foreach ($build in @($builds)) { $list.Add($build) }
    $list.Sort([Comparison[object]] { param($x, $y) Compare-SemVer $y.Version $x.Version })
    return , $list.ToArray()
}

function New-BuildNotes([string] $Commit, [string] $Version, [string] $LastTag, [object[]] $Published) {
    <#
    .SYNOPSIS
        The Markdown notes of a build: the commits since the build published before it, or since the last release tag.
    .DESCRIPTION
        The update dialog shows the notes of every local build newer than the running one, newest first, so notes that
        start where the previous build ended add up to exactly what an update brings. A previous build counts only when
        its commit is an ancestor of this one (a build of another branch would leave a gap or a repeat).
    .PARAMETER Commit
        The full commit id of the build.
    .PARAMETER Version
        The build's version.
    .PARAMETER LastTag
        The last release tag reachable from the commit, or $null.
    .PARAMETER Published
        The builds already in the folder (Get-PublishedBuilds).
    .OUTPUTS
        System.String: Markdown, LF line endings.
    #>
    $previous = $null
    foreach ($build in $Published) {
        if ((Compare-SemVer $build.Version $Version) -ge 0 -or -not $build.Commit) { continue }
        if ($null -ne (Invoke-Git @('merge-base', '--is-ancestor', $build.Commit, $Commit) -AllowFailure)) {
            $previous = $build
            break
        }
    }

    if ($previous) {
        $range = "$($previous.Commit)..$Commit"
        $intro = "Since $($previous.Version):"
    }
    elseif ($LastTag) {
        $range = "$LastTag..$Commit"
        $intro = "Since the release $($LastTag):"
    }
    else {
        $range = $Commit
        $intro = 'The newest commits:'
    }

    # Assigned, never wrapped in @(): Invoke-Git returns its lines as one array, and @() would nest it (CLAUDE.md, section 4).
    $lines = Invoke-Git @('log', '--no-merges', '--max-count=200', '--format=%h%x09%s', $range)
    $items = foreach ($line in $lines) {
        $parts = $line.Split("`t", 2)
        if ($parts.Count -eq 2) { "- $($parts[1]) ($($parts[0]))" }
    }

    if (@($items).Count -eq 0) {
        return "$intro no new commits.`n"
    }

    return ($intro + "`n`n" + (@($items) -join "`n") + "`n")
}

function Write-TextFile([string] $Path, [string] $Text) {
    <#
    .SYNOPSIS
        Writes text as UTF-8 without a byte order mark (the app and install.ps1 read either; tools prefer none).
    .PARAMETER Path
        The file.
    .PARAMETER Text
        Its content.
    #>
    [IO.File]::WriteAllText($Path, $Text, [Text.UTF8Encoding]::new($false))
}

function Get-InstalledState([string] $DataDirectory) {
    <#
    .SYNOPSIS
        What install.ps1 recorded in the data directory: the installed version and folder.
    .PARAMETER DataDirectory
        The data directory.
    .OUTPUTS
        PSCustomObject with Version and InstallDir, or $null when nothing was installed with the installer.
    #>
    $file = Join-Path $DataDirectory 'installer.json'
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { return $null }
    try {
        $state = Get-Content -LiteralPath $file -Raw | ConvertFrom-Json
        return [pscustomobject]@{ Version = [string] $state.version; InstallDir = [string] $state.installDir }
    }
    catch {
        return $null
    }
}

function Start-BetterClipboard([string] $Exe) {
    <#
    .SYNOPSIS
        Starts BetterClipboard in the background, the way a sign-in would, and waits until it runs.
    .DESCRIPTION
        Through Explorer (a temporary shortcut it opens), so the app gets the user's own environment and rights instead of
        this shell's - a script run from a developer's or an agent's shell carries variables the app must not inherit, and
        an elevated shell would start it elevated. An isolated instance (BETTERCLIPBOARD_DATA_DIR set) is started directly
        instead: Explorer would drop that variable and start the regular instance.
    .PARAMETER Exe
        Full path of BetterClipboard.exe.
    #>
    if ($env:BETTERCLIPBOARD_DATA_DIR) {
        Start-Process -FilePath $Exe -ArgumentList '--background' -WorkingDirectory (Split-Path -Parent $Exe) | Out-Null
    }
    else {
        $link = Join-Path ([IO.Path]::GetTempPath()) "$AppName-start-$([Guid]::NewGuid().ToString('N')).lnk"
        $shell = New-Object -ComObject WScript.Shell
        $shortcut = $shell.CreateShortcut($link)
        $shortcut.TargetPath = $Exe
        $shortcut.Arguments = '--background'
        $shortcut.WorkingDirectory = Split-Path -Parent $Exe
        $shortcut.Save()
        try {
            Start-Process -FilePath (Join-Path $env:SystemRoot 'explorer.exe') -ArgumentList "`"$link`"" | Out-Null
            $deadline = (Get-Date).AddSeconds(20)
            while ((Get-Date) -lt $deadline) {
                if (@(Get-Process -Name $AppName -ErrorAction SilentlyContinue | Where-Object { try { $_.Path -eq $Exe } catch { $false } }).Count -gt 0) { break }
                Start-Sleep -Milliseconds 250
            }
        }
        finally {
            # Explorer has read the shortcut once the process runs (or never will); either way it is of no more use.
            Start-Sleep -Milliseconds 500
            Remove-Item -LiteralPath $link -Force -ErrorAction SilentlyContinue
        }
    }
}

# ---------------------------------------------------------------------------------------------------------------------

$dataDirectory = if ($env:BETTERCLIPBOARD_DATA_DIR) { [IO.Path]::GetFullPath($env:BETTERCLIPBOARD_DATA_DIR) } else { Join-Path $env:LOCALAPPDATA $AppName }
$feed = if ($DevBuildsDirectory) { [IO.Path]::GetFullPath($DevBuildsDirectory) } else { Join-Path $dataDirectory 'dev-builds' }
$osArch = if ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq [Runtime.InteropServices.Architecture]::Arm64) { 'arm64' } else { 'x64' }
if (-not $Architectures) { $Architectures = @($osArch) }

$commit = (Invoke-Git @('rev-parse', '--verify', "$Ref^{commit}"))[0].Trim()
$subject = (Invoke-Git @('log', '-1', '--format=%s', $commit))[0]
$computed = Get-DevVersion $commit
$devVersion = if ($Version) { $Version.Trim().TrimStart('v', 'V') } else { $computed.Version }
if ($null -eq (ConvertTo-SemVerParts $devVersion) -or $devVersion -match '\+') {
    throw "-Version '$Version' is not a version without build metadata (X.Y.Z or X.Y.Z-prerelease)."
}

Write-Step "BetterClipboard $devVersion from $($commit.Substring(0, 7)) ($subject)"
if ($computed.LastTag) { Write-Note "$($computed.Count) commit(s) after $($computed.LastTag)." }
$dirty = Invoke-Git @('status', '--porcelain', '--untracked-files=no')
if ($dirty.Count -gt 0 -and $Ref -eq 'HEAD') {
    Write-Warning "The working tree has uncommitted changes ($($dirty.Count) file(s)). They are not in this build: it is built from commit $($commit.Substring(0, 7)) alone. Commit them first to publish them."
}

$target = Join-Path $feed $devVersion
$published = Get-PublishedBuilds $feed
$already = Test-Path -LiteralPath (Join-Path $target 'build.json') -PathType Leaf
if ($already -and -not $Force) {
    Write-Note "$devVersion is published already ($target); nothing to build (-Force builds it again)."
}
else {
    # A temporary worktree outside the repository: the build sees exactly the commit, and the shared tree's bin and obj
    # folders (another session's, maybe mid-build) are never touched.
    $work = Join-Path ([IO.Path]::GetTempPath()) "$AppName-dev-$([Guid]::NewGuid().ToString('N').Substring(0, 8))"
    $tree = Join-Path $work 'src'
    New-Item -ItemType Directory -Path $work | Out-Null
    $staging = $null
    try {
        Write-Step "Checking out $($commit.Substring(0, 7)) into a temporary worktree"
        Invoke-Git @('worktree', 'add', '--detach', $tree, $commit) | Out-Null

        if ($Test) {
            Write-Step 'Running the tests (dotnet test --solution BetterClipboard.sln)'
            Push-Location $tree
            try {
                & dotnet test --solution (Join-Path $tree 'BetterClipboard.sln')
                if ($LASTEXITCODE -ne 0) { throw "Tests failed (exit $LASTEXITCODE); nothing was published." }
            }
            finally {
                Pop-Location
            }
        }

        Write-Step "Packaging ($($Architectures -join ', '); Release, self-contained)"
        $packageScript = Join-Path $tree 'tools\release\package.ps1'
        $packageArguments = @{ Version = $devVersion; Architectures = $Architectures; OutputDirectory = 'artifacts/dev-publish' }

        # -SkipArchives exists from this script's own commit on: an older commit's package.ps1 builds the .7z archives too
        # (slower, and it needs 7-Zip), which are simply not published.
        if ((Get-Content -LiteralPath $packageScript -Raw) -match '\[switch\]\s*\$SkipArchives') { $packageArguments['SkipArchives'] = $true }
        & $packageScript @packageArguments
        $out = Join-Path $tree 'artifacts\dev-publish'

        Write-Step "Publishing to $feed"
        New-Item -ItemType Directory -Path $feed -Force | Out-Null
        $staging = Join-Path $feed ".publishing-$([Guid]::NewGuid().ToString('N').Substring(0, 8))"
        New-Item -ItemType Directory -Path $staging | Out-Null
        $sums = foreach ($arch in $Architectures) {
            $zipName = "$AppName-$devVersion-win-$arch.zip"
            $zip = Join-Path $out $zipName
            if (-not (Test-Path -LiteralPath $zip -PathType Leaf)) { throw "package.ps1 made no $zipName." }
            Copy-Item -LiteralPath $zip -Destination $staging
            '{0}  {1}' -f (Get-FileHash -LiteralPath (Join-Path $staging $zipName) -Algorithm SHA256).Hash.ToLowerInvariant(), $zipName
        }

        # sha256sum format, LF, as a release's list: the app and install.ps1 read it the same way.
        Write-TextFile (Join-Path $staging 'SHA256SUMS.txt') ((@($sums) -join "`n") + "`n")

        # The build's own installer, for -Install (and for anyone who installs the zip by hand): the same file the zip holds.
        Copy-Item -LiteralPath (Join-Path $tree 'install.ps1') -Destination $staging
        Write-TextFile (Join-Path $staging 'notes.md') (New-BuildNotes $commit $devVersion $computed.LastTag $published)

        # Written last: build.json is what makes the folder a build for the app.
        $info = [ordered]@{
            version      = $devVersion
            commit       = $commit
            subject      = $subject
            publishedUtc = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
        }
        Write-TextFile (Join-Path $staging 'build.json') (($info | ConvertTo-Json) + "`n")

        if (Test-Path -LiteralPath $target) {
            # -Force: the old build goes first. A copy that is reading it right now holds it open; that fails here, loudly.
            Remove-Item -LiteralPath $target -Recurse -Force
        }

        Rename-Item -LiteralPath $staging -NewName $devVersion
        $staging = $null
        Write-Note "Published $target"
    }
    finally {
        if ($staging -and (Test-Path -LiteralPath $staging)) { Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue }
        Invoke-Git @('worktree', 'remove', '--force', $tree) -AllowFailure | Out-Null
        Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# Pruning: the newest -Keep builds stay, and so does nothing an official release superseded (the app would never offer it
# again). Staging folders of runs that died are removed after an hour.
$latestTag = Invoke-Git @('describe', '--tags', '--match', 'v[0-9]*', '--abbrev=0', $commit) -AllowFailure
$latestRelease = if ($latestTag) { $latestTag[0].Trim().TrimStart('v') } else { $null }
$builds = Get-PublishedBuilds $feed
for ($i = 0; $i -lt $builds.Count; $i++) {
    $build = $builds[$i]
    if ($build.Version -eq $devVersion) { continue }
    $superseded = $latestRelease -and (Compare-SemVer $build.Version $latestRelease) -le 0
    if ($i -ge $Keep -or $superseded) {
        Remove-Item -LiteralPath $build.Path -Recurse -Force -ErrorAction SilentlyContinue
        Write-Note "Removed the older build $($build.Version)$(if ($superseded) { " (the release $latestRelease supersedes it)" })."
    }
}

foreach ($stale in @(Get-ChildItem -LiteralPath $feed -Directory -Filter '.publishing-*' -ErrorAction SilentlyContinue)) {
    if ($stale.LastWriteTimeUtc -lt (Get-Date).ToUniversalTime().AddHours(-1)) {
        Remove-Item -LiteralPath $stale.FullName -Recurse -Force -ErrorAction SilentlyContinue
    }
}

if ($Install) {
    if ($Architectures -notcontains $osArch) { throw "-Install needs the $osArch package of this PC; publish with -Architectures $osArch." }
    $zipPath = Join-Path $target "$AppName-$devVersion-win-$osArch.zip"
    $installer = Join-Path $target 'install.ps1'
    Write-Step "Installing $devVersion (install.ps1 -Package; BetterClipboard closes and is started again)"

    # Windows PowerShell 5.1, like the app's own updater and the README's command, and without PowerShell 7's module
    # path, with which it cannot load its own modules (CLAUDE.md, section 4).
    $info = [Diagnostics.ProcessStartInfo]::new((Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'))
    $info.UseShellExecute = $false

    # One quoted argument each (Windows' command-line rules; paths here have spaces at most, never quotes), so the script
    # also runs from Windows PowerShell 5.1, whose .NET has no ArgumentList.
    $info.Arguments = (@('-NoLogo', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $installer, '-Package', $zipPath, '-NoLaunch') |
        ForEach-Object { if ($_ -match '[\s"]') { '"' + ($_ -replace '"', '\"') + '"' } else { $_ } }) -join ' '
    $info.EnvironmentVariables.Remove('PSModulePath')
    $process = [Diagnostics.Process]::Start($info)
    $process.WaitForExit()
    if ($process.ExitCode -ne 0) { throw "install.ps1 failed (exit $($process.ExitCode)); see its output above." }

    $state = Get-InstalledState $dataDirectory
    $exe = if ($state -and $state.InstallDir) { Join-Path $state.InstallDir "$AppName.exe" } else { $null }
    if (-not $NoStart -and $exe -and (Test-Path -LiteralPath $exe)) {
        Write-Step 'Starting BetterClipboard in the background'
        Start-BetterClipboard $exe
    }
}

# What the installed copy will do with the build: offered, already installed, or not read yet.
$installed = Get-InstalledState $dataDirectory
if (-not $installed -or -not (ConvertTo-SemVerParts $installed.Version)) {
    Write-Note 'No copy installed by install.ps1 was found for this data directory: run again with -Install to install this build.'
}
elseif ((Compare-SemVer $installed.Version $devVersion) -ge 0) {
    Write-Note "The installed BetterClipboard is $($installed.Version)$(if ($installed.Version -eq $devVersion) { ', this build' } else { ', newer than this build' })."
}
elseif ((Compare-SemVer $installed.Version '0.2.10-dev') -lt 0) {
    Write-Note "The installed BetterClipboard ($($installed.Version)) predates the dev-builds folder and cannot offer this build: run again with -Install once. From then on, its update button offers each published build."
}
else {
    Write-Note "The installed BetterClipboard ($($installed.Version)) offers $devVersion on its update button now (Dev build available)."
}

Write-Step ("Done in {0:0} s." -f $clock.Elapsed.TotalSeconds)
