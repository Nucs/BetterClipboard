<#
.SYNOPSIS
    Builds the release zips exactly as the GitHub release workflow does (same script), so a release can be
    reproduced and smoke-tested locally before tagging.

.DESCRIPTION
    For every architecture: `dotnet publish` of the WinUI app as a self-contained, unpackaged build
    (no .NET or Windows App SDK runtime needed on the target PC) and of bclip.exe (the command line)
    into the same folder, plus install.ps1 (the Installed-apps uninstall entry runs it from the install
    folder), LICENSE and THIRD-PARTY-NOTICES.md at the zip root. Then writes SHA256SUMS.txt in
    `sha256sum` format, which install.ps1 verifies before installing.

    Each architecture also gets BetterClipboard-<version>-win-<arch>.7z: the same files, compressed with 7-Zip
    (LZMA2, solid) to about 60 % of the zip (0.2.4: 43.0 MiB against 70.5 MiB for x64). The Chocolatey package
    embeds both (package-chocolatey.ps1), which only fits the Community Repository's size limit this way, and the
    release publishes them with their checksums so moderators can verify the embedded files (docs/chocolatey.md).
    Needs 7-Zip: 7z on PATH (GitHub's runners), in Program Files, or the copy inside Chocolatey.

.PARAMETER Version
    Semantic version without the leading 'v' (e.g. 0.1.0); stamped into the assemblies and the file names.

.PARAMETER Architectures
    Any of x64, arm64. Default: both.

.PARAMETER OutputDirectory
    Where the zips, the .7z archives and SHA256SUMS.txt go (created; existing release files in it are replaced).

.EXAMPLE
    pwsh tools/release/package.ps1 -Version 0.1.0
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+([-+][0-9A-Za-z.-]+)?$')]
    [string] $Version,

    [ValidateSet('x64', 'arm64')]
    [string[]] $Architectures = @('x64', 'arm64'),

    [string] $OutputDirectory = 'artifacts/release'
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$project = Join-Path $root 'src\BetterClipboard.App\BetterClipboard.App.csproj'
$cliProject = Join-Path $root 'src\BetterClipboard.Cli\BetterClipboard.Cli.csproj'
$output = [IO.Path]::GetFullPath((Join-Path $root $OutputDirectory))
New-Item -ItemType Directory -Path $output -Force | Out-Null

function Get-SevenZip {
    <#
    .SYNOPSIS
        Path of a 7-Zip command-line executable for the .7z archives.
    .DESCRIPTION
        Looks on PATH (GitHub's Windows runners have 7z), then in the usual install folders, then for the copy
        Chocolatey ships in its tools folder (a 32-bit 7-Zip, which is why no dictionary size is forced below:
        it cannot allocate the largest ones).
    .OUTPUTS
        System.String.
    .NOTES
        Throws when none is found: a release without the archives could not have a Chocolatey package.
    #>
    $onPath = Get-Command 7z -ErrorAction SilentlyContinue
    if ($onPath) {
        return $onPath.Source
    }

    $candidates = @(
        (Join-Path $env:ProgramFiles '7-Zip\7z.exe'),
        $(if (${env:ProgramFiles(x86)}) { Join-Path ${env:ProgramFiles(x86)} '7-Zip\7z.exe' }),
        $(if ($env:ChocolateyInstall) { Join-Path $env:ChocolateyInstall 'tools\7z.exe' }),
        'C:\ProgramData\chocolatey\tools\7z.exe'
    )
    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path -LiteralPath $candidate)) {
            return $candidate
        }
    }

    throw 'Could not find 7-Zip (7z.exe) for the .7z archives; install 7-Zip or Chocolatey, or put 7z on PATH.'
}

$sevenZip = Get-SevenZip

# WinUI needs a concrete Platform that matches the runtime identifier (see BetterClipboard.App.csproj).
$platforms = @{ x64 = 'x64'; arm64 = 'ARM64' }
$zips = @()
$archives = @()
foreach ($arch in $Architectures) {
    $publish = Join-Path $root "artifacts\publish\release-win-$arch"
    if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }

    Write-Host "==> Publishing win-$arch $Version" -ForegroundColor Cyan
    & dotnet publish $project -c Release -r "win-$arch" --self-contained true "-p:Platform=$($platforms[$arch])" `
        "-p:Version=$Version" -p:DebugType=none -o $publish --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for win-$arch (exit $LASTEXITCODE)." }

    # bclip (the command line) goes into the same folder: it is self-contained too and shares the app's
    # identical runtime files, so it adds only its own few hundred KB. The Settings "Add to PATH" button
    # and install.ps1 -AddToPath put exactly this folder on PATH.
    & dotnet publish $cliProject -c Release -r "win-$arch" --self-contained true `
        "-p:Version=$Version" -p:DebugType=none -o $publish --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish of bclip failed for win-$arch (exit $LASTEXITCODE)." }

    foreach ($file in 'install.ps1', 'LICENSE', 'THIRD-PARTY-NOTICES.md') {
        Copy-Item (Join-Path $root $file) $publish
    }

    $zip = Join-Path $output "BetterClipboard-$Version-win-$arch.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    # Files at the zip root: install.ps1 checks for BetterClipboard.exe there.
    Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $zip -CompressionLevel Optimal
    $zips += Get-Item $zip
    Write-Host ("    {0} ({1:N1} MB)" -f $zip, ((Get-Item $zip).Length / 1MB))

    # The same files for the Chocolatey package: LZMA2 at the highest level, solid. Paths are relative to the
    # publish folder (the "*"), so the files sit at the archive root exactly as in the zip.
    $archive = Join-Path $output "BetterClipboard-$Version-win-$arch.7z"
    if (Test-Path $archive) { Remove-Item $archive -Force }
    & $sevenZip a -t7z -mx=9 -m0=lzma2 -ms=on -bd -bso0 -bsp0 $archive (Join-Path $publish '*')
    if ($LASTEXITCODE -ne 0) { throw "7-Zip failed for win-$arch (exit $LASTEXITCODE)." }
    $archives += Get-Item $archive
    Write-Host ("    {0} ({1:N1} MB)" -f $archive, ((Get-Item $archive).Length / 1MB))
}

# sha256sum format ("<hex>  <name>"), LF line endings, so `sha256sum -c` on Linux/macOS works as well.
# install.ps1 looks up its zip by name, so the extra .7z lines do not disturb it.
$lines = @($zips) + @($archives) | ForEach-Object { '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name }
[IO.File]::WriteAllText((Join-Path $output 'SHA256SUMS.txt'), (($lines -join "`n") + "`n"), [Text.Encoding]::ASCII)
Write-Host "==> Wrote $(Join-Path $output 'SHA256SUMS.txt')" -ForegroundColor Cyan
