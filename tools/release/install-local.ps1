<#
.SYNOPSIS
    Installs the release zips that package.ps1 built on this PC with the real install.ps1, before the release
    exists on GitHub: a pre-release install of exactly the files that will be published.

.DESCRIPTION
    install.ps1 only knows GitHub releases. This script shadows Invoke-RestMethod and Invoke-WebRequest with
    global functions (PowerShell resolves functions before cmdlets, also inside the script it calls), so the
    installer's release lookup and both downloads are served from -ReleaseDirectory instead. Everything else
    is the real installer: the SHA-256 check against SHA256SUMS.txt, the graceful exit of the running app, the
    staged swap, the Start menu shortcut, the Run entry, the Installed-apps entry and installer.json. GitHub's
    own asset digest does not exist locally, so the installer skips that second check.

    This changes the current user's real install: it closes every BetterClipboard running in this session and
    rewrites the shortcut, the Run entry and the Installed-apps entry. Run it to install for real, never as a
    test (see CLAUDE.md, section 3.1).

.PARAMETER Version
    Version of the zips to install, exactly as passed to package.ps1 (e.g. 0.2.4).

.PARAMETER ReleaseDirectory
    Folder holding BetterClipboard-<Version>-win-<arch>.zip and SHA256SUMS.txt. Default: artifacts/release
    under the repository root (package.ps1's default output).

.PARAMETER NoLaunch
    Passed on to install.ps1: do not start BetterClipboard afterwards. Pass it from an automation shell and
    start the app through Explorer instead; otherwise the app inherits that shell's environment.

.PARAMETER Installer
    The installer to run. Default: install.ps1 at the repository root. Only worth changing to point at a stub
    that exercises the served lookup and downloads without installing anything.

.EXAMPLE
    pwsh tools/release/package.ps1 -Version 0.2.4 -Architectures x64
    powershell -NoProfile -ExecutionPolicy Bypass -File tools/release/install-local.ps1 -Version 0.2.4

    Builds the x64 zip and installs it over the current install, keeping the history.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+([-+][0-9A-Za-z.-]+)?$')]
    [string] $Version,

    [string] $ReleaseDirectory = (Join-Path $PSScriptRoot '..\..\artifacts\release'),

    [switch] $NoLaunch,

    [string] $Installer = (Join-Path $PSScriptRoot '..\..\install.ps1')
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

$releaseFull = (Resolve-Path -LiteralPath $ReleaseDirectory).Path
$installerFull = (Resolve-Path -LiteralPath $Installer).Path

# Checked up front so a missing package fails here with a clear message, not inside the installer with a
# message about a GitHub release.
foreach ($required in @('SHA256SUMS.txt')) {
    if (-not (Test-Path -LiteralPath (Join-Path $releaseFull $required))) {
        throw "$required is missing in $releaseFull; run tools/release/package.ps1 -Version $Version first."
    }
}

if (-not (Get-ChildItem -LiteralPath $releaseFull -Filter "BetterClipboard-$Version-win-*.zip" -File)) {
    throw "No BetterClipboard-$Version-win-*.zip in $releaseFull; run tools/release/package.ps1 -Version $Version first."
}

# The shadowing functions run in the installer's scope, so their inputs travel in global variables (a
# $script: variable here would be invisible there).
$global:BetterClipboardLocalRelease = $releaseFull
$global:BetterClipboardLocalVersion = $Version

function global:Invoke-RestMethod {
    <#
    .SYNOPSIS
        Answers install.ps1's GitHub "latest release" lookup with the local zips as the release's assets.
    .PARAMETER Uri
        The requested API address; anything but a latest-release lookup throws, because it means the
        installer changed and this script must be updated with it.
    .PARAMETER Headers
        Ignored (GitHub API headers).
    .PARAMETER UseBasicParsing
        Ignored (accepted for the installer's call syntax).
    .OUTPUTS
        An object shaped like the GitHub API's release: tag_name and assets (name, size, browser_download_url).
    #>
    param([string] $Uri, $Headers, [switch] $UseBasicParsing)

    if ($Uri -notmatch '/releases/latest$') { throw "install-local.ps1 does not serve this request: $Uri" }
    $version = $global:BetterClipboardLocalVersion
    $files = Get-ChildItem -LiteralPath $global:BetterClipboardLocalRelease -File |
        Where-Object { $_.Name -like "BetterClipboard-$version-win-*.zip" -or $_.Name -eq 'SHA256SUMS.txt' }
    $assets = foreach ($file in $files) {
        # The "download URL" is the local path; Invoke-WebRequest below copies or reads it.
        [pscustomobject]@{ name = $file.Name; size = $file.Length; browser_download_url = $file.FullName }
    }

    [pscustomobject]@{ tag_name = "v$version"; assets = @($assets) }
}

function global:Invoke-WebRequest {
    <#
    .SYNOPSIS
        Serves install.ps1's two downloads from disk: the zip (copied to -OutFile) and SHA256SUMS.txt (as Content).
    .PARAMETER Uri
        A local file path handed out by Invoke-RestMethod above; anything else throws.
    .PARAMETER OutFile
        Where the installer wants the zip; when empty, the file's text is returned as Content instead.
    .PARAMETER Headers
        Ignored (download headers).
    .PARAMETER UseBasicParsing
        Ignored (accepted for the installer's call syntax).
    .OUTPUTS
        Nothing when copying to -OutFile; otherwise an object with the file's text in Content.
    #>
    param([string] $Uri, [string] $OutFile, $Headers, [switch] $UseBasicParsing)

    if (-not (Test-Path -LiteralPath $Uri)) { throw "install-local.ps1 does not serve this download: $Uri" }
    if ($OutFile) {
        Copy-Item -LiteralPath $Uri -Destination $OutFile
        return
    }

    [pscustomobject]@{ Content = [IO.File]::ReadAllText($Uri) }
}

try {
    & $installerFull -NoLaunch:$NoLaunch
}
finally {
    # Leave this PowerShell session as it was: the real cmdlets come back once the functions are gone.
    Remove-Item -Path Function:\Invoke-RestMethod, Function:\Invoke-WebRequest -ErrorAction SilentlyContinue
    Remove-Variable -Name BetterClipboardLocalRelease, BetterClipboardLocalVersion -Scope Global -ErrorAction SilentlyContinue
}
