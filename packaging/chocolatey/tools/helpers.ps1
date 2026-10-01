<#
.SYNOPSIS
    Names and functions shared by the BetterClipboard Chocolatey package's scripts (dot-sourced, never run alone).

.DESCRIPTION
    chocolateyInstall.ps1, chocolateyBeforeModify.ps1 and chocolateyUninstall.ps1 load this file with
    ". (Join-Path $toolsDir 'helpers.ps1')". During an upgrade the before-modify script that runs is the
    INSTALLED version's, together with the installed version's copy of this file, while the install script that
    follows is the new version's. So a name read across versions (the state file, the app folder) must keep its
    meaning from one version to the next.

    Everything here is named BetterClipboard* so nothing collides with Chocolatey's own helper variables, which
    package scripts must not touch (validator rule CPMR0012).
#>

# Folder in tools\ the release archive is extracted to. It never changes between versions: the "Start with
# Windows" entry and the Start menu shortcut point at the exe inside it, and an upgrade must leave them valid.
$BetterClipboardAppFolderName = 'app'

# The per-user "Start with Windows" entry: exactly the value name and command format of the app's own toggle
# (src/BetterClipboard.Windows/Shell/StartupRegistration.cs), so that toggle shows it as on and can turn it off.
$BetterClipboardRunKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$BetterClipboardRunValue = 'BetterClipboard'

$BetterClipboardShortcutName = 'BetterClipboard.lnk'

# Written by before-modify (before an upgrade or an uninstall), read and deleted by the next install script: its
# presence tells an upgrade from a fresh install, its content whether the app was running. Chocolatey keeps a
# file written after install through an upgrade, and also through an uninstall - where it would keep the package
# folder alive - so the uninstall script deletes it (docs/chocolatey.md section 3.3).
$BetterClipboardStateFileName = 'upgrade-state.txt'

function Get-BetterClipboardDesktopSid {
    <#
    .SYNOPSIS
        SID of the user whose desktop (Explorer) runs in this process's Windows session, or $null without one.
    .DESCRIPTION
        Tells apart the contexts a package runs in. The signed-in user installing (often elevated through the UAC
        consent prompt): the SID is theirs. An administrator's credentials typed in for a standard user: Explorer
        belongs to someone else, and HKCU and %LOCALAPPDATA% here are the administrator's. SYSTEM (Intune, an RMM
        agent) or a remote shell: no Explorer in the session.
    .OUTPUTS
        System.String (a SID such as S-1-5-21-...), or $null.
    .NOTES
        Never throws: when the owner cannot be read, the answer is $null, i.e. "no desktop to act on".
    #>
    try {
        $session = (Get-Process -Id $PID).SessionId
        $shell = Get-CimInstance -ClassName Win32_Process -Filter "Name = 'explorer.exe' AND SessionId = $session" -ErrorAction Stop |
            Select-Object -First 1
        if (-not $shell) {
            return $null
        }

        return [string] (Invoke-CimMethod -InputObject $shell -MethodName GetOwnerSid -ErrorAction Stop).Sid
    }
    catch {
        return $null
    }
}

function Test-BetterClipboardDesktopUser {
    <#
    .SYNOPSIS
        Whether this process runs as the user whose desktop is in its session.
    .DESCRIPTION
        Only then may the package start the app (it would otherwise run as the wrong account, with the wrong
        history) or restart Explorer.
    .OUTPUTS
        System.Boolean.
    #>
    $desktop = Get-BetterClipboardDesktopSid
    return [bool] ($desktop -and $desktop -eq [Security.Principal.WindowsIdentity]::GetCurrent().User.Value)
}

function Get-BetterClipboardOtherInstall {
    <#
    .SYNOPSIS
        Path of BetterClipboard.exe installed by install.ps1 (the per-user installer) for this user, or $null.
    .DESCRIPTION
        install.ps1 registers an Installed-apps entry under HKCU whose InstallLocation names its folder (default
        %LOCALAPPDATA%\Programs\BetterClipboard). Both copies use the same history, but each would want the
        "Start with Windows" entry and Win+V, so the install script warns and the uninstall script leaves a
        released Win+V to the copy that remains.
    .OUTPUTS
        System.String, or $null.
    #>
    $entry = Get-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\BetterClipboard' -ErrorAction SilentlyContinue
    if ($entry -and $entry.PSObject.Properties['InstallLocation'] -and $entry.InstallLocation) {
        $exe = Join-Path $entry.InstallLocation 'BetterClipboard.exe'
        if (Test-Path -LiteralPath $exe) {
            return $exe
        }
    }

    if ($env:LOCALAPPDATA) {
        $default = Join-Path $env:LOCALAPPDATA 'Programs\BetterClipboard\BetterClipboard.exe'
        if (Test-Path -LiteralPath $default) {
            return $default
        }
    }

    return $null
}

function Get-BetterClipboardShortcutTarget {
    <#
    .SYNOPSIS
        Target path of a shortcut (.lnk) read through the Windows shell, or '' when there is none or it cannot be
        read.
    .DESCRIPTION
        install.ps1 puts its own BetterClipboard.lnk in the user's Start menu - the very path a non-elevated
        Chocolatey install would use - so the package checks a shortcut's target before replacing or deleting it.
    .PARAMETER Path
        Full path of the shortcut file.
    .OUTPUTS
        System.String.
    .NOTES
        Never throws: a shortcut whose target cannot be read counts as not pointing into this package, so the
        uninstall script leaves it alone.
    #>
    param([string] $Path)
    try {
        if (-not (Test-Path -LiteralPath $Path)) {
            return ''
        }

        $item = (New-Object -ComObject Shell.Application).Namespace((Split-Path -Parent $Path)).ParseName((Split-Path -Leaf $Path))
        if ($item -and $item.IsLink) {
            return [string] $item.GetLink.Path
        }
    }
    catch {
        # Fall through: unreadable is treated as "not ours".
    }

    return ''
}

function Get-BetterClipboardRunTarget {
    <#
    .SYNOPSIS
        Executable that this user's "Start with Windows" entry for BetterClipboard starts, or '' without one.
    .DESCRIPTION
        The entry is '"<exe>" --background' (the app's own format); the quoted path is returned.
    .OUTPUTS
        System.String.
    #>
    $run = Get-ItemProperty -Path $BetterClipboardRunKey -ErrorAction SilentlyContinue
    if ($run -and $run.PSObject.Properties[$BetterClipboardRunValue] -and ([string] $run.$BetterClipboardRunValue) -match '^\s*"([^"]+)"') {
        return $Matches[1]
    }

    return ''
}

function Test-BetterClipboardOtherCopy {
    <#
    .SYNOPSIS
        Whether a path names an existing BetterClipboard.exe outside this package (install.ps1's copy, say).
    .DESCRIPTION
        A shortcut or "Start with Windows" entry that opens another existing copy belongs to that copy: the
        package neither replaces it on install nor deletes it on uninstall. One that points into this package, at
        a missing file, or nowhere is the package's to manage.
    .PARAMETER Path
        The target to check ('' allowed).
    .PARAMETER AppDir
        This package's app folder (tools\app).
    .OUTPUTS
        System.Boolean.
    #>
    param([string] $Path, [string] $AppDir)
    return [bool] ($Path -and -not $Path.StartsWith($AppDir, [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $Path))
}

function Get-BetterClipboardProcess {
    <#
    .SYNOPSIS
        The running BetterClipboard.exe processes, optionally only those started from one executable.
    .PARAMETER Exe
        Full path of an executable; when given, only processes running from exactly that file are returned.
        Processes whose path cannot be read (another user's, without administrator rights) never match it.
    .OUTPUTS
        System.Diagnostics.Process objects, possibly none. PowerShell unrolls a returned array, so callers wrap the
        call in @() before counting.
    #>
    param([string] $Exe)
    $all = @(Get-Process -Name BetterClipboard -ErrorAction SilentlyContinue)
    if (-not $Exe) {
        return $all
    }

    return @($all | Where-Object {
            # Reading another user's process image can throw; such a process is simply not "ours".
            $path = $null
            try { $path = $_.Path } catch { $path = $null }
            $path -and [string]::Equals($path, $Exe, [StringComparison]::OrdinalIgnoreCase)
        })
}

function Start-BetterClipboardAsDesktopUser {
    <#
    .SYNOPSIS
        Starts BetterClipboard with the desktop user's normal (unelevated) token, through Explorer.
    .DESCRIPTION
        Started directly from a package script, the app would inherit Chocolatey's elevation, and BetterClipboard
        must never run elevated (the same rule as install.ps1). Explorer opens a temporary shortcut instead: a
        shortcut also carries the arguments, which "explorer.exe <exe>" would not pass on. The caller checks
        Test-BetterClipboardDesktopUser first.
    .PARAMETER Exe
        Full path of BetterClipboard.exe.
    .PARAMETER Arguments
        Command line for the app: '' for a plain start (it opens Settings, the first-run welcome), '--background'
        for a start into the tray.
    .OUTPUTS
        System.Boolean - whether a process from that executable was seen within 10 seconds.
    #>
    param([string] $Exe, [string] $Arguments)
    $link = Join-Path ([IO.Path]::GetTempPath()) ('BetterClipboard-start-{0}.lnk' -f [Guid]::NewGuid().ToString('N'))
    Install-ChocolateyShortcut -ShortcutFilePath $link -TargetPath $Exe -Arguments $Arguments -WorkingDirectory (Split-Path -Parent $Exe)
    try {
        & (Join-Path $env:SystemRoot 'explorer.exe') $link
        # Explorer opens the shortcut asynchronously; deleting it right away could beat it to the file.
        $deadline = (Get-Date).AddSeconds(10)
        while ((Get-Date) -lt $deadline) {
            if (@(Get-BetterClipboardProcess -Exe $Exe).Count -gt 0) {
                return $true
            }

            Start-Sleep -Milliseconds 250
        }

        return $false
    }
    finally {
        Remove-Item -LiteralPath $link -Force -ErrorAction SilentlyContinue
        # explorer.exe exits with 1 even when it worked; leave no failure code behind for Chocolatey's runner.
        $global:LASTEXITCODE = 0
    }
}

function Restart-BetterClipboardDesktopShell {
    <#
    .SYNOPSIS
        Restarts Explorer in this session so a change to Explorer's DisabledHotkeys takes effect now.
    .DESCRIPTION
        The same steps as install.ps1: stop this session's Explorer and let Winlogon start it again
        (AutoRestartShell); start one only if it did not come back within 6 seconds, because starting one while
        Winlogon also does opens a stray File Explorer window. The taskbar blinks once.
    .OUTPUTS
        None.
    #>
    $session = (Get-Process -Id $PID).SessionId
    Get-Process -Name explorer -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -eq $session } | Stop-Process -Force
    $deadline = (Get-Date).AddSeconds(6)
    while ((Get-Date) -lt $deadline) {
        if (Get-Process -Name explorer -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -eq $session }) {
            return
        }

        Start-Sleep -Milliseconds 250
    }

    & (Join-Path $env:SystemRoot 'explorer.exe')
    $global:LASTEXITCODE = 0
}
