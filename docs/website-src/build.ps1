<#
.SYNOPSIS
    Builds the documentation site: the manual cut from README.md, the hand-written guides, then docfx.

.DESCRIPTION
    The one command for the site, run the same way by .github/workflows/docs.yml and on a developer's PC:

      1. dotnet tool restore - docfx, pinned in .config/dotnet-tools.json (2.81.0, the first docfx line that runs on
         .NET 10 by itself), so every build uses the same docfx;
      2. scripts/generate_pages.py - cuts README.md into pages and adds guides/, into docs/ (ignored by git);
      3. docfx - builds the site into docs/website (ignored by git);
      4. scripts/generate_llms_txt.py - llms.txt, llms-full.txt and robots.txt next to the pages;
      5. with -Serve, serves the site on http://localhost:<Port> until Ctrl+C.

    Nothing here reads or changes anything outside the repository: no BetterClipboard runs, and no clipboard or
    history is touched.

.PARAMETER Serve
    After building, serve the site locally (docfx serve) until Ctrl+C.

.PARAMETER Port
    The port -Serve listens on. Default: 8080.

.PARAMETER Strict
    Fail the build on any docfx warning (a broken link, a missing file): what CI runs, so a README change that breaks
    the site fails its pull request.

.PARAMETER SiteUrl
    The site's public address, written into llms.txt, llms-full.txt and robots.txt.
    Default: https://nucs.github.io/BetterClipboard.

.EXAMPLE
    pwsh docs/website-src/build.ps1 -Serve

    Builds the site and opens it on http://localhost:8080.

.EXAMPLE
    pwsh docs/website-src/build.ps1 -Strict

    What CI runs: a build that fails on any warning.
#>
[CmdletBinding()]
param(
    [switch] $Serve,
    [int] $Port = 8080,
    [switch] $Strict,
    [string] $SiteUrl = 'https://nucs.github.io/BetterClipboard'
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

$siteDir = $PSScriptRoot
$repoRoot = (Resolve-Path (Join-Path $siteDir '../..')).Path
$outputDir = Join-Path $siteDir '../website'

function Find-Python {
    <#
    .SYNOPSIS
        The command that runs Python 3 here: python, python3, or the Windows launcher "py -3".
    .OUTPUTS
        System.String[]. The command and its leading arguments, ready for the call operator.
    .NOTES
        Throws when none of them is Python 3: the generators need 3.10 or newer (they use "X | None" types).
    #>
    foreach ($candidate in @(@('python'), @('python3'), @('py', '-3'))) {
        $command = Get-Command $candidate[0] -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $command) { continue }
        # A Windows "python" can be the Microsoft Store stub that only opens the Store: ask for the version first.
        $arguments = @($candidate | Select-Object -Skip 1) + @('-c', 'import sys; print(sys.version_info[:2] >= (3, 10))')
        $answer = & $command.Source @arguments 2>$null
        if ($LASTEXITCODE -eq 0 -and "$answer".Trim() -eq 'True') {
            return , (@($command.Source) + @($candidate | Select-Object -Skip 1))
        }
    }
    throw 'Python 3.10 or newer is needed to build the docs site (python, python3 or py -3 on the PATH).'
}

function Invoke-Step([string] $Title, [scriptblock] $Action) {
    <#
    .SYNOPSIS
        Runs one step of the build and stops the build when the step's last native command failed.
    .PARAMETER Title
        What the step does, printed before it runs.
    .PARAMETER Action
        The step. A native command's exit code is checked: PowerShell does not stop on one by itself.
    .NOTES
        Throws "<Title> failed (exit code N)." when the step's last native command exited with another code than 0,
        and lets the step's own errors through ($ErrorActionPreference is Stop): either ends the build.
    #>
    Write-Host "==> $Title" -ForegroundColor Cyan
    $global:LASTEXITCODE = 0
    & $Action
    if ($LASTEXITCODE -ne 0) { throw "$Title failed (exit code $LASTEXITCODE)." }
}

$python = Find-Python
$pythonCommand = $python[0]
$pythonArguments = @($python | Select-Object -Skip 1)

# dotnet tool restore reads the manifest of the folder it runs in: the repository's .config/dotnet-tools.json.
Push-Location $repoRoot
try {
    Invoke-Step 'Restoring docfx (.config/dotnet-tools.json)' { dotnet tool restore }
    Invoke-Step 'Cutting README.md and guides/ into pages' {
        & $pythonCommand @pythonArguments (Join-Path $siteDir 'scripts/generate_pages.py')
    }
    $docfxArguments = @('docfx', (Join-Path $siteDir 'docfx.json'))
    if ($Strict) { $docfxArguments += '--warningsAsErrors' }
    Invoke-Step 'Building the site with docfx' { dotnet @docfxArguments }
    Invoke-Step 'Writing llms.txt, llms-full.txt and robots.txt' {
        & $pythonCommand @pythonArguments (Join-Path $siteDir 'scripts/generate_llms_txt.py') $outputDir $SiteUrl
    }
    $resolvedOutput = (Resolve-Path $outputDir).Path
    Write-Host "Site built in $resolvedOutput" -ForegroundColor Green
    if ($Serve) {
        Invoke-Step "Serving on http://localhost:$Port (Ctrl+C stops)" {
            dotnet docfx serve $resolvedOutput --port $Port
        }
    }
}
finally {
    Pop-Location
}
