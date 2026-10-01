<#
.SYNOPSIS
    End-to-end test of the Chocolatey package on a disposable Windows machine: a real install, an upgrade with the
    app running, a real uninstall.

.DESCRIPTION
    Chocolatey's own verifier cannot test this package (its Windows Server 2019 is older than the app's minimum,
    docs/chocolatey.md section 3.5), so the release workflow tests the exact .nupkg it pushes, on GitHub's
    disposable runner, before pushing it:
      1. install: exit code 0; the app extracted with the expected version; the embedded archives and install.ps1
         gone; shims for bclip and BetterClipboard only ("bclip --version" through its shim); the Start menu
         shortcut; the "Start with Windows" entry (when this account is the one the package writes it for).
      2. upgrade to the same archives repacked one version higher, with the app running (started with a scratch
         data folder when the install did not start it): exit code 0; the new version in place; the old process
         closed by before-modify, no lib-bkp left behind, no state file left.
      3. uninstall, after marking Win+V as released from Explorer: exit code 0; the package folder, shims,
         shortcut, "Start with Windows" entry and the Win+V release gone; the history folder kept.
    Whether the app could run at all depends on the session (a runner without a desktop may not keep a WinUI app
    alive): what follows from it is reported, the rest must hold.

    It changes the machine: Chocolatey's lib and bin folders, every user's Start menu, this user's Run key and
    Explorer settings, a restart of Explorer. So it refuses to run unless the CI variable is "true" (GitHub sets
    it) or -ConfirmMachineChanges is given - use a VM, never a machine someone works on.

.PARAMETER Version
    App version of the package under test (as given to package.ps1 and package-chocolatey.ps1).

.PARAMETER PackageVersion
    Version of the package under test; default: Version.

.PARAMETER Architectures
    Architectures the package carries, passed on when the upgrade package is built; default both.

.PARAMETER PackageDirectory
    Folder with betterclipboard.<PackageVersion>.nupkg, the .7z archives and SHA256SUMS.txt. Default:
    artifacts/release.

.PARAMETER ConfirmMachineChanges
    Run outside CI anyway (a disposable VM).

.EXAMPLE
    pwsh tools/release/test-chocolatey.ps1 -Version 1.2.3

    In CI after package-chocolatey.ps1 -Version 1.2.3.

.NOTES
    Needs administrator rights and the Chocolatey CLI. Exit code 0 = every check passed, 1 = a check failed;
    throws when it cannot run (not elevated, no package, betterclipboard already installed).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $Version,

    [string] $PackageVersion,

    [ValidateSet('x64', 'arm64')]
    [string[]] $Architectures = @('x64', 'arm64'),

    [string] $PackageDirectory = 'artifacts/release',

    [switch] $ConfirmMachineChanges
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
# Combine, not Join-Path: an absolute folder must not be glued onto the repository root.
$packages = [IO.Path]::GetFullPath([IO.Path]::Combine($root, $PackageDirectory))
if (-not $PackageVersion) { $PackageVersion = $Version }
$prerelease = if ($PackageVersion.Contains('-')) { @('--prerelease') } else { @() }
$results = New-Object System.Collections.Generic.List[object]
$work = Join-Path ([IO.Path]::GetTempPath()) "betterclipboard-choco-test-$([Guid]::NewGuid().ToString('N').Substring(0, 8))"

# The package's own helpers: the test decides "is this the desktop user?" exactly as the package does.
. (Join-Path $root 'packaging\chocolatey\tools\helpers.ps1')

function Add-Check([string] $Step, [string] $What, [bool] $Passed, [string] $Detail = '') {
    <#
    .SYNOPSIS
        Records one check for the summary (and prints it at once, so a CI log shows progress).
    .PARAMETER Step
        install, upgrade or uninstall.
    .PARAMETER What
        What must hold, in words.
    .PARAMETER Passed
        Whether it held.
    .PARAMETER Detail
        Observed value or explanation, printed next to the result.
    .OUTPUTS
        None.
    #>
    $results.Add([pscustomobject]@{ Step = $Step; What = $What; Passed = $Passed; Detail = $Detail })
    $mark = if ($Passed) { 'ok  ' } else { 'FAIL' }
    Write-Host ("{0} {1,-9} {2}{3}" -f $mark, $Step, $What, $(if ($Detail) { " ($Detail)" } else { '' }))
}

function Add-Note([string] $Step, [string] $Text) {
    <#
    .SYNOPSIS
        Prints an observation that depends on the session (e.g. whether a WinUI app can run here), not a check.
    .PARAMETER Step
        install, upgrade or uninstall.
    .PARAMETER Text
        The observation.
    .OUTPUTS
        None.
    #>
    Write-Host ("note {0,-9} {1}" -f $Step, $Text)
}

function Invoke-Choco([string] $Name, [string[]] $Arguments) {
    <#
    .SYNOPSIS
        Runs choco once, its output into <work>\<Name>.log, and returns the exit code.
    .PARAMETER Name
        Log file name.
    .PARAMETER Arguments
        choco arguments; -y and --no-progress are added.
    .OUTPUTS
        System.Int32.
    #>
    $log = Join-Path $work "$Name.log"
    & choco @Arguments -y --no-progress *> $log
    $code = $LASTEXITCODE
    if ($code -ne 0) {
        Write-Host "---- last lines of $log ----"
        Get-Content -LiteralPath $log -Tail 40 | Out-Host
    }

    return $code
}

function Get-RunEntry {
    <#
    .SYNOPSIS
        This user's "Start with Windows" command for BetterClipboard, or '' without one.
    .OUTPUTS
        System.String.
    #>
    $run = Get-ItemProperty -Path $BetterClipboardRunKey -ErrorAction SilentlyContinue
    if ($run -and $run.PSObject.Properties[$BetterClipboardRunValue]) { return [string] $run.$BetterClipboardRunValue }
    return ''
}

function Get-NextVersion([string] $Current) {
    <#
    .SYNOPSIS
        A package version one step above Current, in Chocolatey's package-fix notation.
    .DESCRIPTION
        1.2.3 becomes 1.2.3.1 and 1.2.3.4 becomes 1.2.3.5; a prerelease label stays (1.2.3-ci becomes
        1.2.3.1-ci, which sorts above 1.2.3-ci because the numbers decide first).
    .PARAMETER Current
        The package version under test.
    .OUTPUTS
        System.String.
    #>
    $numbers, $label = $Current -split '-', 2
    $parts = @($numbers.Split('.') | ForEach-Object { [int] $_ })
    if ($parts.Count -eq 4) { $parts[3]++ } else { $parts += 1 }
    $next = $parts -join '.'
    if ($label) { $next += "-$label" }
    return $next
}

if ($env:CI -ne 'true' -and -not $ConfirmMachineChanges) {
    throw 'This test installs and uninstalls BetterClipboard machine-wide and restarts Explorer. It runs in CI; elsewhere only on a disposable VM, with -ConfirmMachineChanges.'
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not (New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run elevated: the test installs the package machine-wide, as Chocolatey users do.'
}

if (-not (Get-Command choco -ErrorAction SilentlyContinue)) {
    throw 'The Chocolatey CLI (choco) is not on PATH.'
}

$nupkg = Join-Path $packages "betterclipboard.$PackageVersion.nupkg"
if (-not (Test-Path -LiteralPath $nupkg)) {
    throw "No $nupkg; run package-chocolatey.ps1 first."
}

$chocoRoot = if ($env:ChocolateyInstall) { $env:ChocolateyInstall } else { 'C:\ProgramData\chocolatey' }
$lib = Join-Path $chocoRoot 'lib\betterclipboard'
if (Test-Path -LiteralPath $lib) {
    throw "betterclipboard is already installed ($lib); the test needs a machine without it."
}

$appDir = Join-Path $lib "tools\$BetterClipboardAppFolderName"
$exe = Join-Path $appDir 'BetterClipboard.exe'
$bin = Join-Path $chocoRoot 'bin'
$shortcut = Join-Path ([Environment]::GetFolderPath('CommonPrograms')) $BetterClipboardShortcutName
$isDesktopUser = Test-BetterClipboardDesktopUser
$expectRunEntry = -not $identity.IsSystem -and -not ((-not $isDesktopUser) -and (Get-BetterClipboardDesktopSid))
New-Item -ItemType Directory -Path $work | Out-Null
$advanced = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced'
$scratchData = Join-Path $work 'data'

try {
    Write-Host "Testing $nupkg (desktop user: $isDesktopUser, SYSTEM: $($identity.IsSystem))"

    # 1. install
    $code = Invoke-Choco 'install' (@('install', 'betterclipboard', '--version', $PackageVersion, '--source', $packages) + $prerelease)
    Add-Check 'install' 'choco install exit code 0' ($code -eq 0) "$code"
    $productVersion = if (Test-Path -LiteralPath $exe) { (Get-Item -LiteralPath $exe).VersionInfo.ProductVersion } else { '' }
    Add-Check 'install' 'app extracted with the package''s app version' ([bool] $productVersion -and $productVersion.StartsWith($Version)) $productVersion
    Add-Check 'install' 'embedded archives deleted after extraction' (@(Get-ChildItem -Path (Join-Path $lib 'tools') -Filter *.7z -ErrorAction SilentlyContinue).Count -eq 0)
    Add-Check 'install' 'install.ps1 removed from the app folder' (-not (Test-Path -LiteralPath (Join-Path $appDir 'install.ps1')))
    $shims = @(Get-ChildItem -Path $bin -Filter *.exe -ErrorAction SilentlyContinue | ForEach-Object { $_.Name })
    Add-Check 'install' 'shims for bclip and BetterClipboard' (($shims -contains 'bclip.exe') -and ($shims -contains 'BetterClipboard.exe'))
    Add-Check 'install' 'no shims for the runtime''s helpers' (-not ($shims -contains 'createdump.exe') -and -not ($shims -contains 'RestartAgent.exe'))
    $bclip = if (Test-Path -LiteralPath (Join-Path $bin 'bclip.exe')) { (& (Join-Path $bin 'bclip.exe') --version 2>&1 | Out-String).Trim() } else { '' }
    Add-Check 'install' 'bclip --version through its shim' ($bclip -eq "bclip $Version") $bclip
    Add-Check 'install' 'Start menu shortcut for every user' ((Get-BetterClipboardShortcutTarget $shortcut) -eq $exe) $shortcut
    if ($expectRunEntry) {
        Add-Check 'install' '"Start with Windows" entry' ((Get-RunEntry) -eq "`"$exe`" --background") (Get-RunEntry)
    }
    else {
        Add-Check 'install' 'no "Start with Windows" entry for a foreign account' (-not (Get-RunEntry).Contains($exe))
    }

    $running = @(Get-BetterClipboardProcess -Exe $exe)
    Add-Note 'install' "app started by the install: $($running.Count -gt 0)"

    # 2. upgrade, with the app running when it can run here
    if ($running.Count -eq 0) {
        # A scratch data folder makes it a separate, scoped instance with its own history (CLAUDE.md section 2.9).
        $env:BETTERCLIPBOARD_DATA_DIR = $scratchData
        & $exe --background
        $global:LASTEXITCODE = 0
        Start-Sleep -Seconds 8
        $running = @(Get-BetterClipboardProcess -Exe $exe)
        Add-Note 'upgrade' "app started by the test with a scratch data folder: $($running.Count -gt 0)"
    }

    $feed = Join-Path $work 'feed'
    $next = Get-NextVersion $PackageVersion
    & (Join-Path $PSScriptRoot 'package-chocolatey.ps1') -Version $Version -PackageVersion $next -Architectures $Architectures `
        -ArtifactsDirectory $packages -OutputDirectory $feed | Out-Null
    $code = Invoke-Choco 'upgrade' (@('upgrade', 'betterclipboard', '--version', $next, '--source', $feed) + $prerelease)
    Add-Check 'upgrade' 'choco upgrade exit code 0' ($code -eq 0) "$code"
    $installed = if (Test-Path -LiteralPath (Join-Path $lib 'betterclipboard.nuspec')) { ([xml](Get-Content -LiteralPath (Join-Path $lib 'betterclipboard.nuspec') -Raw)).package.metadata.version } else { '' }
    Add-Check 'upgrade' 'new package version installed' ($installed -eq $next) $installed
    Add-Check 'upgrade' 'state file consumed' (-not (Test-Path -LiteralPath (Join-Path $lib "tools\$BetterClipboardStateFileName")))
    if ($running.Count -gt 0) {
        Add-Check 'upgrade' 'old app process closed by before-modify' (@($running | Where-Object { -not $_.HasExited }).Count -eq 0)
        Add-Check 'upgrade' 'no lib-bkp left behind' (-not (Test-Path -LiteralPath (Join-Path $chocoRoot 'lib-bkp\betterclipboard')))
        Add-Note 'upgrade' "app started again after the upgrade: $(@(Get-BetterClipboardProcess -Exe $exe).Count -gt 0)"
    }
    else {
        Add-Note 'upgrade' 'the app cannot run in this session; the running-app checks were skipped'
    }

    # 3. uninstall, with Win+V marked as released (what Settings' "Release Win+V from Explorer" writes)
    $before = Get-ItemProperty -Path $advanced -ErrorAction SilentlyContinue
    $hotkeys = if ($before -and $before.PSObject.Properties['DisabledHotkeys']) { [string] $before.DisabledHotkeys } else { '' }
    New-Item -Path $advanced -Force | Out-Null
    Set-ItemProperty -Path $advanced -Name DisabledHotkeys -Value ($hotkeys + 'V') -Type String
    $dataFolders = @($scratchData, (Join-Path $env:LOCALAPPDATA 'BetterClipboard')) | Where-Object { Test-Path -LiteralPath $_ }

    $code = Invoke-Choco 'uninstall' @('uninstall', 'betterclipboard')
    Add-Check 'uninstall' 'choco uninstall exit code 0' ($code -eq 0) "$code"
    Add-Check 'uninstall' 'package folder removed' (-not (Test-Path -LiteralPath $lib))
    $shims = @(Get-ChildItem -Path $bin -Filter *.exe -ErrorAction SilentlyContinue | ForEach-Object { $_.Name })
    Add-Check 'uninstall' 'shims removed' (-not ($shims -contains 'bclip.exe') -and -not ($shims -contains 'BetterClipboard.exe'))
    Add-Check 'uninstall' 'Start menu shortcut removed' (-not (Test-Path -LiteralPath $shortcut))
    Add-Check 'uninstall' '"Start with Windows" entry removed' (-not (Get-RunEntry).Contains($exe))
    $after = Get-ItemProperty -Path $advanced -ErrorAction SilentlyContinue
    $afterHotkeys = if ($after -and $after.PSObject.Properties['DisabledHotkeys']) { [string] $after.DisabledHotkeys } else { '' }
    Add-Check 'uninstall' 'Win+V given back to Windows' (-not $afterHotkeys.ToUpperInvariant().Contains('V')) "DisabledHotkeys '$afterHotkeys'"
    Add-Check 'uninstall' 'no BetterClipboard process left from the package' (@(Get-BetterClipboardProcess -Exe $exe).Count -eq 0)
    foreach ($folder in $dataFolders) {
        Add-Check 'uninstall' 'history folder kept' (Test-Path -LiteralPath $folder) $folder
    }
}
finally {
    # Whatever happened: no test app left running, the variable gone. The machine itself is disposable.
    Get-BetterClipboardProcess -Exe $exe | Stop-Process -Force -ErrorAction SilentlyContinue
    Remove-Item Env:\BETTERCLIPBOARD_DATA_DIR -ErrorAction SilentlyContinue
}

$failed = @($results | Where-Object { -not $_.Passed })
Write-Host ("{0} checks, {1} failed. Logs: {2}" -f $results.Count, $failed.Count, $work)
exit $(if ($failed.Count -gt 0) { 1 } else { 0 })
