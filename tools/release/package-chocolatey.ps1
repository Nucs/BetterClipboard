<#
.SYNOPSIS
    Builds the Chocolatey package betterclipboard.<version>.nupkg from packaging/chocolatey and the release's
    7-Zip archives.

.DESCRIPTION
    Chocolatey recommends embedding the software when its license allows ("the most reliable and deterministic
    packages"), and BetterClipboard is MIT-licensed by its own author, so the package carries the release
    archives instead of downloading them at install time. The archives are the LZMA2 .7z files package.ps1 writes
    next to the zips (82 MiB for both architectures in 0.2.4, against 138 MiB as zips), which keeps the package
    well under the Community Repository's size limit. Steps:
      1. Checks the versions: the Community Repository refuses SemVer 2.0.0 versions such as 1.0.0-rc.1.
      2. Checks every archive against SHA256SUMS.txt, the list the GitHub release publishes, so the package can
         only carry exactly what the release offers - which is what VERIFICATION.txt tells moderators to check.
      3. Copies packaging/chocolatey to a staging folder, fills in the placeholders, strips the template's comments
         from the nuspec, writes legal\LICENSE.txt from the repository's LICENSE and the archive list into
         legal\VERIFICATION.txt, saves the scripts as UTF-8 with a byte order mark, and refuses any leftover
         placeholder or non-ASCII script.
      4. Runs choco pack and checks the result's size.

.PARAMETER Version
    The app version the archives were built for, as given to package.ps1 (e.g. 1.2.3). It selects the archives
    (BetterClipboard-<Version>-win-<arch>.7z) and the release tag v<Version> the package links to.

.PARAMETER PackageVersion
    The package's own version; default: Version. Use Chocolatey's package-fix notation to republish a fixed
    package for the same app version (1.2.3.20261001). Must be a version the Community Repository accepts:
    3 or 4 numbers, optionally with a prerelease label without dots (1.2.3-beta1).

.PARAMETER Architectures
    Any of x64, arm64; default both. CI builds an x64-only package for its install test; a package missing an
    architecture refuses to install there, so only a both-architecture package is ever published.

.PARAMETER ArtifactsDirectory
    Folder holding the archives and SHA256SUMS.txt (package.ps1's -OutputDirectory). Default: artifacts/release.

.PARAMETER OutputDirectory
    Where the .nupkg goes. Default: ArtifactsDirectory. An existing package of the same version is replaced.

.OUTPUTS
    System.String - the full path of the .nupkg.

.EXAMPLE
    pwsh tools/release/package-chocolatey.ps1 -Version 1.2.3

    After package.ps1 -Version 1.2.3: writes artifacts/release/betterclipboard.1.2.3.nupkg.

.EXAMPLE
    pwsh tools/release/package-chocolatey.ps1 -Version 1.2.3 -PackageVersion 1.2.3.20261001

    A fixed package for the already released 1.2.3.

.NOTES
    Needs the Chocolatey CLI (choco) on PATH; GitHub's Windows runners have it. Throws on any failed check.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+([-+][0-9A-Za-z.-]+)?$')]
    [string] $Version,

    [string] $PackageVersion,

    [ValidateSet('x64', 'arm64')]
    [string[]] $Architectures = @('x64', 'arm64'),

    [string] $ArtifactsDirectory = 'artifacts/release',

    [string] $OutputDirectory
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

# The documented ceiling (validator rule CPMR0028: "packages on the community repository can be up to 150MB");
# the server has been reported to take 200 MB, but a release must not depend on the undocumented number.
$MaxPackageBytes = 150MB

$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$template = Join-Path $root 'packaging\chocolatey'
# Combine, not Join-Path: an absolute folder (the test script passes one) must not be glued onto the repository root.
$artifacts = [IO.Path]::GetFullPath([IO.Path]::Combine($root, $ArtifactsDirectory))
$output = if ($OutputDirectory) { [IO.Path]::GetFullPath([IO.Path]::Combine($root, $OutputDirectory)) } else { $artifacts }
if (-not $PackageVersion) { $PackageVersion = $Version }
$tag = "v$Version"

function Get-ReleaseChecksums([string] $Path) {
    <#
    .SYNOPSIS
        Reads SHA256SUMS.txt (sha256sum format, as package.ps1 writes it) into a file name -> hash table.
    .PARAMETER Path
        Full path of SHA256SUMS.txt.
    .OUTPUTS
        System.Collections.Hashtable - lower-case hex hashes keyed by file name (case-insensitive keys).
    .NOTES
        Throws when the file is missing: without it nothing ties the archives to a release.
    #>
    if (-not (Test-Path -LiteralPath $Path)) {
        throw "No $Path; run package.ps1 first (it writes the archives and their checksums)."
    }

    $sums = @{}
    foreach ($line in [IO.File]::ReadAllLines($Path)) {
        if ($line -match '^\s*([0-9a-fA-F]{64})\s+\*?(.+?)\s*$') {
            $sums[$Matches[2]] = $Matches[1].ToLowerInvariant()
        }
    }

    return $sums
}

function Write-TextFile([string] $Path, [string] $Text, [bool] $ByteOrderMark) {
    <#
    .SYNOPSIS
        Writes text as UTF-8 with CRLF line endings, with or without a byte order mark.
    .PARAMETER Path
        File to (over)write.
    .PARAMETER Text
        Content; its line endings are normalized to CRLF (what Windows tools and Notepad users expect).
    .PARAMETER ByteOrderMark
        Whether to start with the UTF-8 BOM. Windows PowerShell reads a BOM-less script as ANSI.
    .OUTPUTS
        None.
    #>
    $normalized = ($Text -replace "`r`n", "`n") -replace "`n", "`r`n"
    [IO.File]::WriteAllText($Path, $normalized, (New-Object Text.UTF8Encoding($ByteOrderMark)))
}

if ($PackageVersion -notmatch '^\d+(\.\d+){2,3}(-[0-9A-Za-z-]+)?$') {
    throw "Package version '$PackageVersion' is not one the Chocolatey Community Repository accepts: 3 or 4 numbers, optionally followed by a prerelease label without dots (it does not support SemVer 2.0.0)."
}

# The version as choco pack writes it into the file name: Chocolatey 2.x drops leading zeros and a fourth number
# that is 0 (1.02.3.0 -> 1.2.3), so the expected .nupkg name must follow the same rule.
$numbers, $label = $PackageVersion -split '-', 2
$parts = @($numbers.Split('.') | ForEach-Object { [string] [int] $_ })
if ($parts.Count -eq 4 -and $parts[3] -eq '0') { $parts = $parts[0..2] }
$normalizedVersion = ($parts -join '.') + $(if ($label) { "-$label" } else { '' })

$choco = Get-Command choco -ErrorAction SilentlyContinue
if (-not $choco) {
    throw 'The Chocolatey CLI (choco) is not on PATH. Install it (https://chocolatey.org/install) or build on a GitHub Windows runner, which has it.'
}

# 1-2: the archives, each exactly as SHA256SUMS.txt lists it.
$sums = Get-ReleaseChecksums (Join-Path $artifacts 'SHA256SUMS.txt')
$archives = @()
foreach ($arch in $Architectures) {
    $name = "BetterClipboard-$Version-win-$arch.7z"
    $path = Join-Path $artifacts $name
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Missing $path; package.ps1 -Version $Version writes it next to the zip."
    }

    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    if (-not $sums.ContainsKey($name)) {
        throw "SHA256SUMS.txt does not list $name; the package must carry only what the release publishes."
    }

    if ($sums[$name] -ne $hash) {
        throw "$name does not match SHA256SUMS.txt (listed $($sums[$name]), file $hash)."
    }

    $archives += [pscustomobject]@{ Name = $name; Path = $path; Hash = $hash; Arch = $arch }
}

# 3: the staging copy, filled in.
$staging = Join-Path ([IO.Path]::GetTempPath()) "betterclipboard-choco-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $staging | Out-Null
try {
    Copy-Item -Path (Join-Path $template 'betterclipboard.nuspec') -Destination $staging
    Copy-Item -Path (Join-Path $template 'tools'), (Join-Path $template 'legal') -Destination $staging -Recurse
    foreach ($archive in $archives) {
        Copy-Item -LiteralPath $archive.Path -Destination (Join-Path $staging 'tools')
    }

    # The nuspec: placeholders, then without the template's comments (they address this repository's readers,
    # e.g. "never pack this file directly", and would only confuse a moderator reading the package).
    $nuspecPath = Join-Path $staging 'betterclipboard.nuspec'
    $nuspecText = [IO.File]::ReadAllText($nuspecPath).Replace('{{PACKAGE_VERSION}}', $PackageVersion).Replace('{{RELEASE_TAG}}', $tag)
    $xml = New-Object Xml.XmlDocument
    $xml.PreserveWhitespace = $true
    $xml.LoadXml($nuspecText)
    foreach ($comment in @($xml.SelectNodes('//comment()'))) {
        [void] $comment.ParentNode.RemoveChild($comment)
    }

    $settings = New-Object Xml.XmlWriterSettings
    $settings.Encoding = New-Object Text.UTF8Encoding($false)
    $writer = [Xml.XmlWriter]::Create($nuspecPath, $settings)
    try { $xml.Save($writer) } finally { $writer.Dispose() }

    # legal\: the license as the release tag has it, and how to verify each archive against the release.
    $licenseUrl = "https://github.com/Nucs/BetterClipboard/blob/$tag/LICENSE"
    $license = [IO.File]::ReadAllText((Join-Path $root 'LICENSE'))
    Write-TextFile (Join-Path $staging 'legal\LICENSE.txt') "From: $licenseUrl`n`nLICENSE`n`n$license" $false
    $list = ($archives | ForEach-Object {
            "  tools\$($_.Name)`n    https://github.com/Nucs/BetterClipboard/releases/download/$tag/$($_.Name)`n    SHA256: $($_.Hash)`n"
        }) -join "`n"
    $verificationPath = Join-Path $staging 'legal\VERIFICATION.txt'
    $verification = [IO.File]::ReadAllText($verificationPath).Replace('{{ARCHIVES}}', $list).Replace('{{RELEASE_TAG}}', $tag)
    Write-TextFile $verificationPath $verification $false

    # Scripts: ASCII only (a non-ASCII character would depend on the BOM surviving every editor), saved with BOM.
    foreach ($script in Get-ChildItem -Path (Join-Path $staging 'tools') -Filter *.ps1) {
        $text = [IO.File]::ReadAllText($script.FullName)
        if ($text -match '[^\x00-\x7F]') {
            throw "$($script.Name) contains a non-ASCII character; keep package scripts ASCII."
        }

        Write-TextFile $script.FullName $text $true
    }

    $leftover = @(Get-ChildItem -Path $staging -Recurse -File -Include *.nuspec, *.ps1, *.txt |
            Select-String -Pattern '\{\{[A-Z0-9_]+\}\}' -List)
    if ($leftover.Count -gt 0) {
        throw "Unfilled placeholder in $($leftover[0].Path): $($leftover[0].Line.Trim())"
    }

    # 4: pack and check the size.
    New-Item -ItemType Directory -Path $output -Force | Out-Null
    $nupkg = Join-Path $output "betterclipboard.$normalizedVersion.nupkg"
    if (Test-Path -LiteralPath $nupkg) { Remove-Item -LiteralPath $nupkg -Force }
    & $choco.Source pack $nuspecPath --outputdirectory $output --limit-output | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "choco pack failed (exit $LASTEXITCODE)." }
    if (-not (Test-Path -LiteralPath $nupkg)) { throw "choco pack did not write $nupkg." }

    $size = (Get-Item -LiteralPath $nupkg).Length
    if ($size -gt $MaxPackageBytes) {
        throw ("{0} is {1:N1} MB, over the Community Repository's documented {2:N0} MB." -f $nupkg, ($size / 1MB), ($MaxPackageBytes / 1MB))
    }

    Write-Host ("==> {0} ({1:N1} MB; {2})" -f $nupkg, ($size / 1MB), (($archives | ForEach-Object { "$($_.Arch) $($_.Hash.Substring(0, 12))" }) -join ', ')) -ForegroundColor Cyan
    return $nupkg
}
finally {
    Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
}
