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
         shortcut; the "Start with Windows" entry (when this account is the one the package writes it for); every
         other "Start with Windows" entry kept; Explorer's DisabledHotkeys untouched (the package takes nothing over).
      2. upgrade to the same archives repacked one version higher, with the app running (started with a scratch
         data folder when the install did not start it): exit code 0; the new version in place; the old process
         closed by before-modify, no lib-bkp left behind, no state file left.
      3. uninstall, after marking Win+V, Win+Q (named as a second shortcut in the settings) and Win+J (a letter of
         the user's own) as released from Explorer: exit code 0; the package folder, shims, shortcut, "Start with
         Windows" entry and the Win+V and Win+Q releases gone; Win+J still released; every other "Start with
         Windows" entry and every other Explorer setting kept; the history folder kept.
    Whether the app could run at all depends on the session (a runner without a desktop may not keep a WinUI app
    alive): what follows from it is reported, the rest must hold.

    The "kept" checks need something to keep: "New-Item -Force" on an existing registry key empties it, which is how
    the package of v0.2.5 and the installers up to it erased other apps' startup entries, and a key with nothing else
    in it cannot show that. So the test adds a BC-TEST value next to the package's in the Run key, and a BC-TEST
    value and subkey in Explorer\Advanced, and removes them again at the end.

    It changes the machine: Chocolatey's lib and bin folders, every user's Start menu, this user's Run key and
    Explorer settings (put back at the end), the settings file of this user's BetterClipboard (put back too), a
    restart of Explorer. So it refuses to run unless the CI variable is "true" (GitHub sets it) or
    -ConfirmMachineChanges is given - use a VM, never a machine someone works on.

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

function Get-KeyInventory([string] $Path) {
    <#
    .SYNOPSIS
        Names of a registry key's values and subkeys, to check later that the package changed only its own.
    .PARAMETER Path
        A registry provider path such as HKCU:\Software\Microsoft\Windows\CurrentVersion\Run.
    .OUTPUTS
        PSCustomObject with Values and Subkeys (string arrays); both empty when the key does not exist.
    .NOTES
        Throws when the key exists but cannot be read (the script's ErrorActionPreference is Stop); the test then
        stops, because its "kept" checks would mean nothing.
    #>
    $values = @()
    $subkeys = @()
    if (Test-Path -LiteralPath $Path) {
        $values = @((Get-Item -LiteralPath $Path).GetValueNames())
        $subkeys = @(Get-ChildItem -LiteralPath $Path -ErrorAction SilentlyContinue | ForEach-Object { $_.PSChildName })
    }

    return [pscustomobject]@{ Values = $values; Subkeys = $subkeys }
}

function Get-MissingFromKey($Before, [string] $Path, [string[]] $Ignore) {
    <#
    .SYNOPSIS
        The values and subkeys of an earlier inventory that the key no longer has, apart from the ignored value names.
    .PARAMETER Before
        Get-KeyInventory's answer from before the step.
    .PARAMETER Path
        The same key.
    .PARAMETER Ignore
        Value names the package may remove (its own).
    .OUTPUTS
        System.String - the missing names joined by ", ", or '' when everything is still there.
    .NOTES
        Throws like Get-KeyInventory when the key cannot be read.
    #>
    $now = Get-KeyInventory $Path
    $missing = @($Before.Values | Where-Object { $Ignore -notcontains $_ -and $now.Values -notcontains $_ }) +
        @($Before.Subkeys | Where-Object { $now.Subkeys -notcontains $_ })
    return ($missing -join ', ')
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
$advanced = $BetterClipboardExplorerAdvancedKey
$scratchData = Join-Path $work 'data'

# What the test seeds, so the finally block can put everything back (on a VM the test may run again).
$neighborName = 'BC-TEST neighbor'
$neighborKey = Join-Path $advanced 'BC-TEST neighbor key'
$originalHotkeys = Get-BetterClipboardDisabledHotkeys
$settingsPath = $null
$settingsBefore = $null
$settingsSeeded = $false

try {
    Write-Host "Testing $nupkg (desktop user: $isDesktopUser, SYSTEM: $($identity.IsSystem))"

    # 0. neighbors for the "kept" checks. A missing key is created; an existing one only ever gets a value added
    # (never "New-Item -Force" on it: on an existing key that deletes everything in it). The Run value names a file
    # that does not exist, so a later sign-in on this machine starts nothing.
    foreach ($key in @($BetterClipboardRunKey, $advanced)) {
        if (-not (Test-Path -LiteralPath $key)) { New-Item -Path $key -Force | Out-Null }
    }

    Set-ItemProperty -Path $BetterClipboardRunKey -Name $neighborName -Value "`"$env:SystemRoot\System32\BC-TEST-neighbor-does-not-exist.exe`"" -Type String
    Set-ItemProperty -Path $advanced -Name $neighborName -Value 1 -Type DWord
    if (-not (Test-Path -LiteralPath $neighborKey)) { New-Item -Path $neighborKey | Out-Null }
    $runBefore = Get-KeyInventory $BetterClipboardRunKey
    $advancedBefore = Get-KeyInventory $advanced

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

    $lost = Get-MissingFromKey $runBefore $BetterClipboardRunKey @($BetterClipboardRunValue)
    Add-Check 'install' 'every other "Start with Windows" entry kept' (-not $lost) $(if ($lost) { "gone: $lost" } else { "$($runBefore.Values.Count) values before" })
    Add-Check 'install' 'Explorer''s DisabledHotkeys untouched' ((Get-BetterClipboardDisabledHotkeys) -ceq $originalHotkeys) "'$(Get-BetterClipboardDisabledHotkeys)'"

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

    # 3. uninstall. Released from Explorer: Win+V (what every installer and Settings release), Win+Q - named in the
    # settings as a second shortcut, which Settings' "Release from Explorer" releases since 0.2.6 - and Win+J, a
    # letter of the user's own that the uninstall must leave. The settings file is the one the uninstall reads:
    # the same data folder rule, in the same environment (Get-BetterClipboardDataDir). The app writes it only when
    # the user changes a setting, never at exit, so the running app does not overwrite the seeded shortcut.
    $settingsDir = Get-BetterClipboardDataDir
    $settingsPath = Join-Path $settingsDir 'settings.json'
    $settingsBefore = if (Test-Path -LiteralPath $settingsPath) { [IO.File]::ReadAllBytes($settingsPath) } else { $null }
    $settings = if ($null -ne $settingsBefore) { [IO.File]::ReadAllText($settingsPath) | ConvertFrom-Json } else { [pscustomobject] @{} }
    $settings | Add-Member -NotePropertyName ExtraOpenHotkeys -NotePropertyValue @('Win+Q') -Force
    New-Item -ItemType Directory -Path $settingsDir -Force | Out-Null
    $settingsSeeded = $true
    [IO.File]::WriteAllText($settingsPath, ($settings | ConvertTo-Json -Depth 32), (New-Object Text.UTF8Encoding($false)))
    Set-ItemProperty -Path $advanced -Name DisabledHotkeys -Value ($originalHotkeys + 'JVQ') -Type String
    $dataFolders = @($scratchData, (Join-Path $env:LOCALAPPDATA 'BetterClipboard')) | Where-Object { Test-Path -LiteralPath $_ }

    $code = Invoke-Choco 'uninstall' @('uninstall', 'betterclipboard')
    Add-Check 'uninstall' 'choco uninstall exit code 0' ($code -eq 0) "$code"
    Add-Check 'uninstall' 'package folder removed' (-not (Test-Path -LiteralPath $lib))
    $shims = @(Get-ChildItem -Path $bin -Filter *.exe -ErrorAction SilentlyContinue | ForEach-Object { $_.Name })
    Add-Check 'uninstall' 'shims removed' (-not ($shims -contains 'bclip.exe') -and -not ($shims -contains 'BetterClipboard.exe'))
    Add-Check 'uninstall' 'Start menu shortcut removed' (-not (Test-Path -LiteralPath $shortcut))
    Add-Check 'uninstall' '"Start with Windows" entry removed' (-not (Get-RunEntry).Contains($exe))
    $lost = Get-MissingFromKey $runBefore $BetterClipboardRunKey @($BetterClipboardRunValue)
    Add-Check 'uninstall' 'every other "Start with Windows" entry kept' (-not $lost) $(if ($lost) { "gone: $lost" } else { '' })
    $lost = Get-MissingFromKey $advancedBefore $advanced @('DisabledHotkeys')
    Add-Check 'uninstall' 'every other Explorer setting kept' (-not $lost) $(if ($lost) { "gone: $lost" } else { "$($advancedBefore.Values.Count) values, $($advancedBefore.Subkeys.Count) subkeys before" })
    $afterHotkeys = Get-BetterClipboardDisabledHotkeys
    $afterUpper = $afterHotkeys.ToUpperInvariant()
    Add-Check 'uninstall' 'Win+V given back to Windows' (-not $afterUpper.Contains('V')) "DisabledHotkeys '$afterHotkeys'"
    Add-Check 'uninstall' 'Win+Q, a shortcut in the settings, given back to Windows' (-not $afterUpper.Contains('Q'))
    Add-Check 'uninstall' 'Win+J, a letter of the user''s own, still released' ($afterUpper.Contains('J'))
    Add-Check 'uninstall' 'no BetterClipboard process left from the package' (@(Get-BetterClipboardProcess -Exe $exe).Count -eq 0)
    foreach ($folder in $dataFolders) {
        Add-Check 'uninstall' 'history folder kept' (Test-Path -LiteralPath $folder) $folder
    }
}
finally {
    # Whatever happened: no test app left running, the variable gone, and what the test seeded or changed put back.
    # Each step on its own, so one failure does not skip the rest.
    Get-BetterClipboardProcess -Exe $exe | Stop-Process -Force -ErrorAction SilentlyContinue
    Remove-Item Env:\BETTERCLIPBOARD_DATA_DIR -ErrorAction SilentlyContinue
    try { Remove-ItemProperty -Path $BetterClipboardRunKey -Name $neighborName -ErrorAction Stop } catch { Write-Host "cleanup: $($_.Exception.Message)" }
    try { Remove-ItemProperty -Path $advanced -Name $neighborName -ErrorAction Stop } catch { Write-Host "cleanup: $($_.Exception.Message)" }
    # Only the BC-TEST subkey, never its parent: the name is checked, so a wrong path cannot reach Explorer\Advanced.
    if ((Split-Path -Leaf $neighborKey) -eq 'BC-TEST neighbor key' -and (Test-Path -LiteralPath $neighborKey)) {
        try { Remove-Item -LiteralPath $neighborKey -Recurse -ErrorAction Stop } catch { Write-Host "cleanup: $($_.Exception.Message)" }
    }

    try {
        if ($originalHotkeys) {
            Set-ItemProperty -Path $advanced -Name DisabledHotkeys -Value $originalHotkeys -Type String -ErrorAction Stop
        }
        else {
            Remove-ItemProperty -Path $advanced -Name DisabledHotkeys -ErrorAction SilentlyContinue
        }
    }
    catch {
        Write-Host "cleanup: $($_.Exception.Message)"
    }

    if ($settingsSeeded) {
        try {
            if ($null -ne $settingsBefore) { [IO.File]::WriteAllBytes($settingsPath, $settingsBefore) } else { Remove-Item -LiteralPath $settingsPath -ErrorAction Stop }
        }
        catch {
            Write-Host "cleanup: $($_.Exception.Message)"
        }
    }
}

$failed = @($results | Where-Object { -not $_.Passed })
Write-Host ("{0} checks, {1} failed. Logs: {2}" -f $results.Count, $failed.Count, $work)
exit $(if ($failed.Count -gt 0) { 1 } else { 0 })
