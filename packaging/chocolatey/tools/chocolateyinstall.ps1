<#
.SYNOPSIS
    Chocolatey install script of BetterClipboard: extracts the release archive for this PC and sets it up.

.DESCRIPTION
    Runs on "choco install" and, from the NEW version, on "choco upgrade" (after the installed version's
    chocolateyBeforeModify.ps1 closed the app). Steps:
      1. Refuses a Windows older than 10 version 2004 (build 19041), the app's minimum: Chocolatey asks packages
         to fail rather than warn, because a warning still reports the software as installed.
      2. Extracts the 7-Zip archive for the OS architecture into tools\app. Chocolatey's helpers would treat ARM64
         as 32-bit (Get-OSArchitectureWidth), so the archive is chosen here and passed as the only file.
      3. Marks the executables for Chocolatey's shims: a GUI shim for BetterClipboard.exe, a console shim for
         bclip.exe, none for the runtime's helpers (createdump.exe, RestartAgent.exe, ...).
      4. Fresh install only: the Start menu shortcut and the "Start with Windows" entry. An upgrade leaves both as
         the user left them (they point at the same exe path in every version).
      5. Starts the app as the signed-in user: on a fresh install with a plain start (Settings opens, as with
         install.ps1), after an upgrade into the tray, and only if it was running before.

    Package parameters (choco install betterclipboard --params "'/NoStartup /NoShortcut'"):
      /NoStartup   no "Start with Windows" entry
      /NoShortcut  no Start menu shortcut
      /NoLaunch    do not start the app
    The design and every verified Chocolatey behavior behind it: docs/chocolatey.md.
#>
$ErrorActionPreference = 'Stop'
$toolsDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
. (Join-Path $toolsDir 'helpers.ps1')
$appDir = Join-Path $toolsDir $BetterClipboardAppFolderName
$exe = Join-Path $appDir 'BetterClipboard.exe'
$pp = Get-PackageParameters

$build = [Environment]::OSVersion.Version.Build
if ($build -lt 19041) {
    throw "BetterClipboard needs Windows 10 version 2004 (build 19041) or later; this PC runs build $build."
}

# The real OS, even from an emulated x64 process on ARM64 (where PROCESSOR_ARCHITECTURE says AMD64). The
# variables are the fallback for a .NET Framework older than 4.7.1, which lacks RuntimeInformation.
try {
    $os = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
}
catch {
    $os = if ($env:PROCESSOR_ARCHITEW6432) { $env:PROCESSOR_ARCHITEW6432 } else { $env:PROCESSOR_ARCHITECTURE }
}

$arch = switch -Regex ($os) {
    '^(X64|AMD64)$' { 'x64' }
    '^(Arm64|ARM64)$' { 'arm64' }
    default { throw "BetterClipboard runs on 64-bit Windows on x64 or ARM64; this PC reports '$os'." }
}

if ($env:ChocolateyForceX86 -eq 'true') {
    Write-Warning 'BetterClipboard has no 32-bit build: --x86 is ignored and the 64-bit build is installed.'
}

$archive = @(Get-ChildItem -Path $toolsDir -Filter "BetterClipboard-*-win-$arch.7z")
if ($archive.Count -ne 1) {
    throw "This package carries no BetterClipboard archive for $arch (found $($archive.Count))."
}

# Written by the installed version's before-modify: present = this is an upgrade; "running" = restart the app.
$statePath = Join-Path $toolsDir $BetterClipboardStateFileName
$state = if (Test-Path -LiteralPath $statePath) { (Get-Content -LiteralPath $statePath -Raw).Trim() } else { '' }
Remove-Item -LiteralPath $statePath -Force -ErrorAction SilentlyContinue
$isUpgrade = [bool] $state -or [bool] $env:ChocolateyPreviousPackageVersion

# The only file given, so the helper uses it on every architecture (passing it as the 64-bit file fails on ARM64).
Get-ChocolateyUnzip -FileFullPath $archive[0].FullName -Destination $appDir -PackageName $env:ChocolateyPackageName | Out-Null
# Both archives also stay inside the .nupkg Chocolatey keeps, so the extracted copies only cost disk space.
Remove-Item -Path (Join-Path $toolsDir '*.7z') -Force

# Chocolatey shims every .exe under the package folder; it does not detect GUI apps, and the runtime's helper
# executables have no business on the machine PATH (where another package's createdump.exe would collide).
New-Item -ItemType File -Force -Path "$exe.gui" | Out-Null
Get-ChildItem -Path $appDir -Filter *.exe -Recurse |
    Where-Object { $_.Name -ne 'BetterClipboard.exe' -and $_.Name -ne 'bclip.exe' } |
    ForEach-Object { New-Item -ItemType File -Force -Path "$($_.FullName).ignore" | Out-Null }

# The release zip's per-user installer; here Chocolatey installs, upgrades and uninstalls.
Remove-Item -LiteralPath (Join-Path $appDir 'install.ps1') -Force -ErrorAction SilentlyContinue

$me = [Security.Principal.WindowsIdentity]::GetCurrent()
$isDesktopUser = Test-BetterClipboardDesktopUser
# Someone else's Explorer in this session: an administrator's credentials were typed in for a standard user.
$foreignDesktop = (-not $isDesktopUser) -and [bool] (Get-BetterClipboardDesktopSid)

if (-not $isUpgrade -and -not $pp.NoShortcut) {
    # Elevated (the usual Chocolatey install): every user's Start menu; otherwise this user's own - where
    # install.ps1 keeps its shortcut under the same name, which is not taken over.
    $programs = if (Test-ProcessAdminRights) { [Environment]::GetFolderPath('CommonPrograms') } else { [Environment]::GetFolderPath('Programs') }
    $link = Join-Path $programs $BetterClipboardShortcutName
    $current = Get-BetterClipboardShortcutTarget $link
    if (Test-BetterClipboardOtherCopy $current $appDir) {
        Write-Host "Start menu shortcut not created: '$link' already opens another copy of BetterClipboard ($current)."
    }
    else {
        Install-ChocolateyShortcut -ShortcutFilePath $link -TargetPath $exe -WorkingDirectory $appDir -IconLocation $exe `
            -Description 'Persistent, searchable clipboard history (Win+V)'
    }
}

if (-not $isUpgrade -and -not $pp.NoStartup) {
    $current = Get-BetterClipboardRunTarget
    if ($me.IsSystem -or $foreignDesktop) {
        # HKCU here is SYSTEM's or the administrator's: an entry there would never start the app for this desktop.
        Write-Warning 'Not set to start with Windows: this install does not run as the signed-in user. Each user can turn it on in BetterClipboard''s Settings.'
    }
    elseif (Test-BetterClipboardOtherCopy $current $appDir) {
        # One value for both copies: taking it over would leave no copy starting once this package is uninstalled.
        Write-Host "'Start with Windows' stays with the other copy of BetterClipboard ($current); both use the same history."
    }
    else {
        # Only create the Run key when it is missing: New-Item -Force on an existing registry key deletes it with every
        # value in it and creates it empty, which would erase every other app's "start with Windows" entry.
        if (-not (Test-Path -LiteralPath $BetterClipboardRunKey)) {
            New-Item -Path $BetterClipboardRunKey -Force | Out-Null
        }

        Set-ItemProperty -Path $BetterClipboardRunKey -Name $BetterClipboardRunValue -Value "`"$exe`" --background" -Type String
    }
}

$otherCopy = Get-BetterClipboardOtherInstall
if ($otherCopy) {
    Write-Warning "BetterClipboard is also installed with install.ps1 at '$otherCopy'. Both use the same history; to keep only this copy, uninstall that one in Settings > Apps > Installed apps."
}

if (-not $pp.NoLaunch -and (-not $isUpgrade -or $state -eq 'running')) {
    if (-not $isDesktopUser) {
        # "At the next sign-in" only when this account's "Start with Windows" entry starts this copy: as SYSTEM or for
        # another user's desktop the entry was not written above, and nothing would start it.
        $atSignIn = [string]::Equals((Get-BetterClipboardRunTarget), $exe, [StringComparison]::OrdinalIgnoreCase)
        $next = if ($atSignIn) { 'It starts at the next sign-in, or from the Start menu.' } else { 'Start it from the Start menu.' }
        Write-Host "BetterClipboard was not started: this install does not run as the signed-in user. $next"
    }
    elseif (@(Get-BetterClipboardProcess | Where-Object { $_.SessionId -eq (Get-Process -Id $PID).SessionId }).Count -gt 0) {
        # Another copy (install.ps1's) holds the session's single-instance lock: a start would only open its window.
        Write-Host 'BetterClipboard was not started: another copy of it is already running.'
    }
    else {
        $arguments = if ($isUpgrade) { '--background' } else { '' }
        if (-not (Start-BetterClipboardAsDesktopUser -Exe $exe -Arguments $arguments)) {
            Write-Warning 'BetterClipboard did not start within 10 seconds; start it from the Start menu.'
        }
    }
}

# The shortcuts in the settings the app reads (shared with an install.ps1 copy, which can set others than Win+V), and
# "while it runs": an install as SYSTEM or for another user's desktop starts nothing.
$configured = Get-BetterClipboardConfiguredHotkeys
$press = if ($null -ne $configured) { @($configured) -join ' or ' } else { 'your BetterClipboard shortcut' }
Write-Host "BetterClipboard is installed in $appDir. While it runs, $press opens it; your history is kept in %LOCALAPPDATA%\BetterClipboard."
