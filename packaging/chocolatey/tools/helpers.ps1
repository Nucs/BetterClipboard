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

# Explorer's per-user hotkey settings: each character of the DisabledHotkeys value frees one Win+<character> shortcut
# for other programs (BetterClipboard's Settings and install.ps1 release Win+V and other Win+letter shortcuts there).
# The key also holds every other Explorer setting, so the scripts only ever set or remove that one value in it.
$BetterClipboardExplorerAdvancedKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced'

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

function Get-BetterClipboardDataDir {
    <#
    .SYNOPSIS
        Folder holding BetterClipboard's settings and history for the account this script runs as, or $null.
    .DESCRIPTION
        The app's own rule (AppPaths.ResolveDefault): BETTERCLIPBOARD_DATA_DIR when set (tests and isolated runs),
        otherwise %LOCALAPPDATA%\BetterClipboard. It is the folder of the same account whose HKCU the scripts change,
        so what they read and what they change always belong together - under over-the-shoulder elevation that is the
        administrator's folder, not the desktop user's.
    .OUTPUTS
        System.String, or $null when the account has no local application data folder.
    .NOTES
        Never throws; the folder may not exist (the app never ran for this account).
    #>
    if ($env:BETTERCLIPBOARD_DATA_DIR) {
        return $env:BETTERCLIPBOARD_DATA_DIR
    }

    if ($env:LOCALAPPDATA) {
        return (Join-Path $env:LOCALAPPDATA 'BetterClipboard')
    }

    return $null
}

function Get-BetterClipboardConfiguredHotkeys {
    <#
    .SYNOPSIS
        The shortcuts that open BetterClipboard according to its settings file (the main one first), or $null when
        the file cannot be read.
    .DESCRIPTION
        Reads OpenHotkey and ExtraOpenHotkeys (BetterClipboard 0.2.6 and later; older versions only have OpenHotkey)
        straight from settings.json, never through the app, which is closed by then. No file - the app never ran for
        this account - means the app's default, Win+V. A file this PowerShell cannot parse gives $null ("unknown"),
        so callers act only on what they know: the app accepts comments and trailing commas, which ConvertFrom-Json
        in Windows PowerShell 5.1 refuses. install.ps1's Get-ConfiguredHotkeys reads the file the same way.
    .PARAMETER Path
        The settings file; default: settings.json in Get-BetterClipboardDataDir.
    .OUTPUTS
        System.String[] returned as one object - assign it, never wrap the call in @(), which would nest the array -
        or $null.
    .NOTES
        Never throws. ConvertFrom-Json needs PowerShell 3; the package runs only on Windows 10 2004 or later, which
        ships Windows PowerShell 5.1.
    #>
    param([string] $Path)
    if (-not $Path) {
        $dataDir = Get-BetterClipboardDataDir
        if (-not $dataDir) {
            return , @('Win+V')
        }

        $Path = Join-Path $dataDir 'settings.json'
    }

    if (-not (Test-Path -LiteralPath $Path)) {
        return , @('Win+V')
    }

    try {
        # The app saves to a temporary file and then replaces this one, so a read never sees half a file; a read
        # that loses the race to the replace fails and is "unknown" like any other unreadable file.
        $json = [IO.File]::ReadAllText($Path) | ConvertFrom-Json
    }
    catch {
        return $null
    }

    $main = 'Win+V'
    if ($json -and $json.PSObject.Properties['OpenHotkey'] -and -not [string]::IsNullOrWhiteSpace([string] $json.OpenHotkey)) {
        $main = ([string] $json.OpenHotkey).Trim()
    }

    $list = @($main)
    if ($json -and $json.PSObject.Properties['ExtraOpenHotkeys'] -and $json.ExtraOpenHotkeys) {
        $list += @($json.ExtraOpenHotkeys | ForEach-Object { ([string] $_).Trim() } | Where-Object { $_ })
    }

    return , $list
}

function Get-BetterClipboardReleasableKey {
    <#
    .SYNOPSIS
        The letter or digit Explorer's DisabledHotkeys lists to free a shortcut, or $null when Explorer cannot free it.
    .DESCRIPTION
        Only a shortcut that is exactly the Windows key plus one letter or digit can be released from Explorer: a
        character in DisabledHotkeys frees exactly Win+<character> (with V listed, Win+Ctrl+V stays Windows'). The
        same rule as the app (ExplorerHotkeys.ReleasableKey) and install.ps1 (Get-ReleasableKey), with the spellings
        of the Windows key they accept, any letter case, and spaces around the plus sign.
    .PARAMETER Shortcut
        A shortcut as BetterClipboard's settings store it, e.g. 'Win+V' or 'Alt+Win+V'.
    .OUTPUTS
        System.Char (upper case), or $null.
    .NOTES
        Never throws.
    #>
    param([string] $Shortcut)
    if ($Shortcut -match '^\s*(win|windows|meta|super|cmd)\s*\+\s*([a-z0-9])\s*$') {
        return [char]::ToUpperInvariant($Matches[2][0])
    }

    return $null
}

function Get-BetterClipboardDisabledHotkeys {
    <#
    .SYNOPSIS
        Explorer's DisabledHotkeys value for this account, or '' when it is not set.
    .OUTPUTS
        System.String.
    .NOTES
        Never throws: a key or value that cannot be read counts as not set.
    #>
    $item = Get-ItemProperty -Path $BetterClipboardExplorerAdvancedKey -ErrorAction SilentlyContinue
    if ($item -and $item.PSObject.Properties['DisabledHotkeys']) {
        return [string] $item.DisabledHotkeys
    }

    return ''
}

function Get-BetterClipboardKeysToGiveBack {
    <#
    .SYNOPSIS
        The Win+<key> shortcuts an uninstall gives back to Explorer: Win+V and every Win+letter or Win+digit shortcut
        in BetterClipboard's settings, each only while DisabledHotkeys lists it.
    .DESCRIPTION
        Without BetterClipboard a shortcut released from Explorer does nothing at all, so the uninstall gives back
        what BetterClipboard released: Win+V (what its Settings and every installer release) and the Win+letter
        shortcuts it opens with (what Settings' "Release from Explorer" releases since 0.2.6). Letters the user
        listed for other reasons - keys that are no BetterClipboard shortcut - stay. install.ps1 -Uninstall applies
        the same rule, plus the keys its own runs recorded in installer.json, which the package never writes.
    .PARAMETER DisabledHotkeys
        Explorer's current value ('' when not set).
    .PARAMETER Configured
        Get-BetterClipboardConfiguredHotkeys' answer; $null (unknown) leaves Win+V as the only candidate.
    .OUTPUTS
        System.Char[] returned as one object (assign it, never wrap the call in @()): upper-case keys, each once, in
        the order Win+V, then the settings' order; empty when there is nothing to give back.
    .NOTES
        Never throws; reads nothing itself (the caller passes both values), so it can be tested without a registry.
    #>
    param([string] $DisabledHotkeys, $Configured)
    $listed = ([string] $DisabledHotkeys).ToUpperInvariant()
    $fromSettings = @(@($Configured) | Where-Object { $_ } | ForEach-Object { Get-BetterClipboardReleasableKey ([string] $_) } |
            Where-Object { $null -ne $_ })
    $candidates = @([char] 'V') + $fromSettings
    return , @($candidates | Select-Object -Unique | Where-Object { $listed.Contains([string] $_) })
}

function Remove-BetterClipboardDisabledHotkeys {
    <#
    .SYNOPSIS
        Takes Win+<key> shortcuts out of Explorer's DisabledHotkeys, so Explorer registers them again when it restarts.
    .DESCRIPTION
        Every spelling of each key goes (Explorer reads the value case-insensitively), and every other character stays
        in place: the user's own letters must survive. The value is deleted when nothing is left. Only this value of
        Explorer\Advanced is ever written: the key holds all of Explorer's settings (never "New-Item -Force" on it,
        which would empty it).
    .PARAMETER Keys
        Letters or digits to give back, any case.
    .OUTPUTS
        System.Boolean - whether the value changed (Explorer must restart to notice).
    .NOTES
        Throws when the registry refuses the write, which the caller reports.
    #>
    param([char[]] $Keys)
    $current = Get-BetterClipboardDisabledHotkeys
    $upper = @($Keys | ForEach-Object { [char]::ToUpperInvariant($_) })
    $rest = -join @($current.ToCharArray() | Where-Object { $upper -notcontains [char]::ToUpperInvariant($_) })
    if ($rest -ceq $current) {
        return $false
    }

    if ($rest) {
        Set-ItemProperty -Path $BetterClipboardExplorerAdvancedKey -Name DisabledHotkeys -Value $rest -Type String
    }
    else {
        Remove-ItemProperty -Path $BetterClipboardExplorerAdvancedKey -Name DisabledHotkeys
    }

    return $true
}

function Format-BetterClipboardKeyNames {
    <#
    .SYNOPSIS
        "Win+V", "Win+V and Win+Q", "Win+V, Win+Q and Win+1": shortcut names for the scripts' messages.
    .PARAMETER Keys
        Letters or digits.
    .OUTPUTS
        System.String.
    .NOTES
        Never throws.
    #>
    param([char[]] $Keys)
    $names = @($Keys | ForEach-Object { 'Win+' + [char]::ToUpperInvariant($_) })
    if ($names.Count -le 1) {
        return (-join $names)
    }

    return '{0} and {1}' -f ($names[0..($names.Count - 2)] -join ', '), $names[-1]
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
