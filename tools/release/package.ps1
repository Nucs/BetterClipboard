<#
.SYNOPSIS
    Builds the release zips exactly as the GitHub release workflow does (same script), so a release can be
    reproduced and smoke-tested locally before tagging.

.DESCRIPTION
    For every architecture: `dotnet publish` of the WinUI app as a self-contained, unpackaged build
    (no .NET or Windows App SDK runtime needed on the target PC), plus install.ps1 (the Installed-apps
    uninstall entry runs it from the install folder), LICENSE and THIRD-PARTY-NOTICES.md at the zip
    root. Then writes SHA256SUMS.txt in `sha256sum` format, which install.ps1 verifies before installing.

.PARAMETER Version
    Semantic version without the leading 'v' (e.g. 0.1.0); stamped into the assemblies and the file names.

.PARAMETER Architectures
    Any of x64, arm64. Default: both.

.PARAMETER OutputDirectory
    Where the zips and SHA256SUMS.txt go (created; existing release files in it are replaced).

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
$output = [IO.Path]::GetFullPath((Join-Path $root $OutputDirectory))
New-Item -ItemType Directory -Path $output -Force | Out-Null

# WinUI needs a concrete Platform that matches the runtime identifier (see BetterClipboard.App.csproj).
$platforms = @{ x64 = 'x64'; arm64 = 'ARM64' }
$zips = @()
foreach ($arch in $Architectures) {
    $publish = Join-Path $root "artifacts\publish\release-win-$arch"
    if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }

    Write-Host "==> Publishing win-$arch $Version" -ForegroundColor Cyan
    & dotnet publish $project -c Release -r "win-$arch" --self-contained true "-p:Platform=$($platforms[$arch])" `
        "-p:Version=$Version" -p:DebugType=none -o $publish --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for win-$arch (exit $LASTEXITCODE)." }

    foreach ($file in 'install.ps1', 'LICENSE', 'THIRD-PARTY-NOTICES.md') {
        Copy-Item (Join-Path $root $file) $publish
    }

    $zip = Join-Path $output "BetterClipboard-$Version-win-$arch.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    # Files at the zip root: install.ps1 checks for BetterClipboard.exe there.
    Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $zip -CompressionLevel Optimal
    $zips += Get-Item $zip
    Write-Host ("    {0} ({1:N1} MB)" -f $zip, ((Get-Item $zip).Length / 1MB))
}

# sha256sum format ("<hex>  <name>"), LF line endings, so `sha256sum -c` on Linux/macOS works as well.
$lines = $zips | ForEach-Object { '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name }
[IO.File]::WriteAllText((Join-Path $output 'SHA256SUMS.txt'), (($lines -join "`n") + "`n"), [Text.Encoding]::ASCII)
Write-Host "==> Wrote $(Join-Path $output 'SHA256SUMS.txt')" -ForegroundColor Cyan
