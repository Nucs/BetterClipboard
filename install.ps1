<#
.SYNOPSIS
    Installs, updates or uninstalls BetterClipboard from its official GitHub releases.

.DESCRIPTION
    Per-user install, no administrator rights needed:

      1. Resolves the release (latest, or -Version) through the GitHub API and picks the zip for this
         machine's architecture (x64 or ARM64 - detected from the OS, not from this PowerShell's bitness).
      2. Downloads it and verifies its SHA-256 against the release's SHA256SUMS.txt (and against GitHub's
         own asset digest when the API provides one). A mismatch aborts before anything is touched.
      3. Asks a running BetterClipboard to exit (it drains queued captures first), then swaps the new
         files in via a staging folder so a failed update leaves the previous version in place.
      4. Registers: Start menu shortcut, "Start with Windows" (the same Run entry the app's own
         setting uses) and an entry in Settings > Apps > Installed apps (uninstall button).
      5. Takes over Win+V: Explorer is told to stop registering it (HKCU ...\Explorer\Advanced\DisabledHotkeys
         gets 'V') and restarts once - the taskbar blinks and open folder windows close - so BetterClipboard
         registers Win+V directly, also over admin windows. -NoTakeOverWinV leaves Win+V to Windows, and
         -Hotkey picks your own shortcuts, taken over the same way.
      6. Optionally (-AddToPath) puts the install folder on your user PATH, so terminals can run bclip,
         BetterClipboard's command line for scripts and AI agents.

    The installer works from your temp folder, so it runs from any folder - also one you cannot write to - and
    puts your PowerShell back in the folder it came from when it is done.

    Your clipboard history lives in %LOCALAPPDATA%\BetterClipboard (encrypted, bound to this PC and
    your Windows account) and is never touched by install or update; -Uninstall keeps it unless
    -RemoveData is given.

.PARAMETER Version
    Release to install: 'latest' (default) or a version such as 0.1.0 / v0.1.0.

.PARAMETER InstallDir
    Target folder. Default: %LOCALAPPDATA%\Programs\BetterClipboard. A relative path is relative to the folder
    you run the installer from.

.PARAMETER TakeOverWinV
    Kept for older command lines: taking over Win+V is what the installer does by default now. Given on an
    update whose shortcuts no longer include Win+V (removed in Settings > Shortcut), it adds Win+V back.

.PARAMETER NoTakeOverWinV
    Leave Win+V to Windows (its own clipboard history panel). BetterClipboard then opens with the -Hotkey
    shortcuts, or with Win+Alt+V when none are given. If Win+V was released from Explorer, it is given back
    (Explorer restarts once). Needs BetterClipboard 0.2.6 or newer.

.PARAMETER Hotkey
    One or more shortcuts that open BetterClipboard, e.g. -Hotkey Ctrl+Alt+F9 or -Hotkey Win+Alt+V, 'Ctrl+`'.
    Each is taken over like Win+V: a Win+letter or Win+digit shortcut is released from Explorer (Explorer
    restarts once), and one another app owns is intercepted by BetterClipboard's keyboard hook while it runs.
    Win+V stays the first shortcut unless -NoTakeOverWinV is given. They replace the shortcuts set before;
    Settings > Shortcut changes them later. Needs BetterClipboard 0.2.6 or newer.

.PARAMETER AddToPath
    Add the install folder to your user PATH so new terminals (and this PowerShell window) can run
    bclip. bclip still answers only after you turn on Settings > Command line in the app - it is off by
    default because, while on, any program running as you can read your clipboard history through it.
    -Uninstall removes the PATH entry again.

.PARAMETER NoStartup
    Do not start BetterClipboard when you sign in.

.PARAMETER NoShortcut
    Do not create the Start menu shortcut.

.PARAMETER NoLaunch
    Do not start BetterClipboard after installing.

.PARAMETER Uninstall
    Remove BetterClipboard (files, shortcut, startup entry, Installed-apps entry). While BetterClipboard's Chocolatey
    package stays installed, the startup entry starts that copy instead of being removed. Win+V - and every other
    Win+ shortcut BetterClipboard released from Explorer - is given back to Windows (use -KeepWinVReleased
    to keep them released).

.PARAMETER RemoveData
    With -Uninstall: also delete the history, settings and logs folder. Irreversible.

.PARAMETER KeepWinVReleased
    With -Uninstall: leave Explorer's DisabledHotkeys as it is.

.PARAMETER Repository
    GitHub repository to install from (owner/name), for forks. Default: Nucs/BetterClipboard.

.EXAMPLE
    irm https://raw.githubusercontent.com/Nucs/BetterClipboard/main/install.ps1 | iex

    Installs or updates to the latest release and takes over Win+V.

.EXAMPLE
    & ([scriptblock]::Create((irm https://raw.githubusercontent.com/Nucs/BetterClipboard/main/install.ps1))) -NoTakeOverWinV -Hotkey Win+Alt+V, Ctrl+Alt+F9

    Same, but Win+V stays with Windows: BetterClipboard opens with Win+Alt+V or Ctrl+Alt+F9 instead.

.EXAMPLE
    & ([scriptblock]::Create((irm https://raw.githubusercontent.com/Nucs/BetterClipboard/main/install.ps1))) -AddToPath

    Installs or updates, and makes bclip runnable by name.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\install.ps1 -Uninstall

    Removes the app but keeps the history.
#>
[CmdletBinding(DefaultParameterSetName = 'Install')]
param(
    [Parameter(ParameterSetName = 'Install')]
    [string] $Version = 'latest',

    [string] $InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\BetterClipboard'),

    [Parameter(ParameterSetName = 'Install')]
    [switch] $TakeOverWinV,

    [Parameter(ParameterSetName = 'Install')]
    [switch] $NoTakeOverWinV,

    [Parameter(ParameterSetName = 'Install')]
    [Alias('Hotkeys')]
    [string[]] $Hotkey,

    [Parameter(ParameterSetName = 'Install')]
    [switch] $AddToPath,

    [Parameter(ParameterSetName = 'Install')]
    [switch] $NoStartup,

    [Parameter(ParameterSetName = 'Install')]
    [switch] $NoShortcut,

    [Parameter(ParameterSetName = 'Install')]
    [switch] $NoLaunch,

    [Parameter(ParameterSetName = 'Uninstall', Mandatory = $true)]
    [switch] $Uninstall,

    [Parameter(ParameterSetName = 'Uninstall')]
    [switch] $RemoveData,

    [Parameter(ParameterSetName = 'Uninstall')]
    [switch] $KeepWinVReleased,

    [string] $Repository = 'Nucs/BetterClipboard'
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
# Windows PowerShell 5.1 renders Invoke-WebRequest's progress bar so slowly that it throttles downloads.
$ProgressPreference = 'SilentlyContinue'

$AppName = 'BetterClipboard'
$ExeName = 'BetterClipboard.exe'
$RunKeyPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$UninstallKeyPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$AppName"
$ExplorerAdvancedPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced'
# HKCU subkey holding the user environment (the user PATH). A variable only so tests can point the PATH
# helpers at a scratch key instead of the real PATH.
$UserEnvironmentKey = 'Environment'
$ShortcutPath = Join-Path ([Environment]::GetFolderPath('Programs')) "$AppName.lnk"

# The first release whose BetterClipboard.exe knows --set-hotkeys, which -Hotkey and -NoTakeOverWinV need. Older
# versions ignore unknown arguments and start normally (a plain start opens Settings), so they are never asked.
$HotkeyOptionsMinimumVersion = [version] '0.2.6'
# What BetterClipboard opens with when Win+V stays with Windows and no -Hotkey is given: free on a stock Windows 11,
# and no app expects it (Ctrl+Alt+V is Office's Paste Special). The app's canonical spelling of Win+Alt+V.
$AlternativeHotkey = 'Alt+Win+V'
# Named location stack, so leaving the temp folder pops exactly what entering it pushed.
$LocationStack = 'BetterClipboardInstaller'

# Same resolution as the app (AppPaths.ResolveDefault): the override variable exists so tests and dev
# runs never touch the real history - the installer honors it for the same reason.
$DataDir = if ($env:BETTERCLIPBOARD_DATA_DIR) { $env:BETTERCLIPBOARD_DATA_DIR } else { Join-Path $env:LOCALAPPDATA $AppName }
$StatePath = Join-Path $DataDir 'installer.json'

function Write-Step([string] $Message) { Write-Host "==> $Message" -ForegroundColor Cyan }
function Write-Note([string] $Message) { Write-Host "    $Message" }

function Confirm-RegistryKey([string] $Path) {
    <#
    .SYNOPSIS
        Creates the registry key $Path (and missing parents) when it does not exist; an existing key is left untouched.
    .DESCRIPTION
        Never "New-Item -Path <key> -Force" on a key that may exist: in the registry provider that deletes the existing
        key with every value and subkey in it and creates it empty (PowerShell 5.1 and 7 alike, verified 2026-10-03).
        Installers up to 0.2.5 did exactly that to HKCU\...\Run before adding their own value, which erased every other
        app's "start with Windows" entry, and the Win+V takeover did it to Explorer\Advanced (Explorer's settings).
    .PARAMETER Path
        A registry provider path such as HKCU:\Software\Microsoft\Windows\CurrentVersion\Run.
    #>
    if (-not (Test-Path -LiteralPath $Path)) {
        New-Item -Path $Path -Force | Out-Null
    }
}

function Resolve-FileSystemPath([string] $Path) {
    <#
    .SYNOPSIS
        Full file-system path of $Path, a relative one taken relative to the folder the installer was started from.
    .DESCRIPTION
        Called before the installer moves to the temp folder, so "-InstallDir .\Apps\BetterClipboard" still means the
        caller's folder. "Here" is PowerShell's current file-system location, not the process's current directory,
        which PowerShell does not keep in step - and when the caller sits in another provider (HKCU:\, Cert:\), the
        last file-system location PowerShell knows.
    .PARAMETER Path
        An absolute or relative path.
    .OUTPUTS
        System.String.
    #>
    if ([IO.Path]::IsPathRooted($Path)) {
        return [IO.Path]::GetFullPath($Path)
    }

    $here = (Get-Location -PSProvider FileSystem).ProviderPath
    return [IO.Path]::GetFullPath((Join-Path $here $Path))
}

function Enter-WorkingFolder {
    <#
    .SYNOPSIS
        Moves this PowerShell and the process's current directory to the temp folder for the rest of the run.
    .DESCRIPTION
        The installer never needs the caller's folder, and nothing it starts may depend on it: "irm | iex" runs in
        whatever folder the terminal is in, which can be one the user cannot write to (C:\Program Files, a folder of
        another account), a share that goes away, or the very install folder the uninstall deletes. Downloads and
        staging already use the temp folder; this makes the working folder of every process the installer starts
        (the app's --exit signal, its --set-hotkeys check, Explorer) one that exists and is writable too.
    #>
    $temp = [IO.Path]::GetTempPath()
    $script:CallerProcessDirectory = [Environment]::CurrentDirectory
    Push-Location -LiteralPath $temp -StackName $LocationStack
    [Environment]::CurrentDirectory = $temp
}

function Exit-WorkingFolder {
    <#
    .SYNOPSIS
        Puts this PowerShell back in the folder it was in before Enter-WorkingFolder.
    .DESCRIPTION
        "irm | iex" runs the installer inside the caller's own session, so a location left in the temp folder would
        stay there after the install. A folder that no longer exists (the uninstall deleted the install folder the
        caller was in) is skipped: the session then stays in the temp folder rather than in a deleted one.
    #>
    try {
        Pop-Location -StackName $LocationStack -ErrorAction Stop
    }
    catch {
        Write-Note 'Staying in the temp folder: the folder this installer was started from no longer exists.'
    }

    $previous = $script:CallerProcessDirectory
    if ($previous -and (Test-Path -LiteralPath $previous -PathType Container)) {
        [Environment]::CurrentDirectory = $previous
    }
}

function Get-OsArchitecture {
    # OSArchitecture reports the real OS even from an emulated x64 PowerShell on ARM64, where
    # PROCESSOR_ARCHITECTURE would say AMD64 and we would install the slower, emulated build.
    try {
        $arch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
    }
    catch {
        $arch = if ($env:PROCESSOR_ARCHITEW6432) { $env:PROCESSOR_ARCHITEW6432 } else { $env:PROCESSOR_ARCHITECTURE }
    }

    switch -Regex ($arch) {
        '^(Arm64|ARM64)$' { return 'arm64' }
        '^(X64|AMD64)$' { return 'x64' }
        default { throw "BetterClipboard supports 64-bit Windows on x64 or ARM64 only (this OS reports '$arch')." }
    }
}

function Get-Release([string] $Requested) {
    # TLS 1.2 is not enabled by default for .NET Framework 4.x on some Windows 10 builds; GitHub requires it.
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    $headers = @{ 'User-Agent' = "$AppName-installer"; 'Accept' = 'application/vnd.github+json' }
    $uri = if ($Requested -eq 'latest') {
        "https://api.github.com/repos/$Repository/releases/latest"
    }
    else {
        "https://api.github.com/repos/$Repository/releases/tags/v$($Requested.TrimStart('v', 'V'))"
    }

    try {
        return Invoke-RestMethod -Uri $uri -Headers $headers -UseBasicParsing
    }
    catch {
        throw "Could not find release '$Requested' of $Repository ($uri): $($_.Exception.Message)"
    }
}

function Get-AssetSha256($Asset, [string] $SumsText) {
    # Expected hash from SHA256SUMS.txt ("<hex>  <file name>" per line, sha256sum format).
    foreach ($line in $SumsText -split "`r?`n") {
        if ($line -match '^\s*([0-9a-fA-F]{64})\s+\*?(.+?)\s*$' -and $Matches[2] -eq $Asset.name) {
            return $Matches[1].ToLowerInvariant()
        }
    }

    throw "SHA256SUMS.txt has no entry for $($Asset.name); refusing to install an unverified download."
}

function ConvertTo-ComparableVersion([string] $SemanticVersion) {
    <#
    .SYNOPSIS
        The x.y.z part of a release version as [version] (0.2.6-dev.abc and 0.2.6+sha compare as 0.2.6), or $null.
    .PARAMETER SemanticVersion
        A release version without the 'v' (the tag name, trimmed).
    .OUTPUTS
        System.Version, or $null when the text is not a version.
    #>
    $core = ($SemanticVersion -split '[-+]', 2)[0]
    $parsed = $null
    if ([version]::TryParse($core, [ref] $parsed)) { return $parsed }
    return $null
}

function Stop-RunningApp([string] $PreferredExe) {
    <#
    .SYNOPSIS
        Closes every BetterClipboard of this session (gracefully with --exit, forcibly after 15 s) and says whether one ran.
    .PARAMETER PreferredExe
        The exe to send --exit with; any copy works, since the running instance listens on a session-wide event.
    .OUTPUTS
        $true when an instance was running (so a failed update can start the previous version again), else $false.
    #>
    $session = (Get-Process -Id $PID).SessionId
    $running = @(Get-Process -Name $AppName -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -eq $session })
    if ($running.Count -eq 0) {
        return $false
    }

    Write-Step 'Closing the running BetterClipboard (it saves queued copies first)'
    $signal = if ($PreferredExe -and (Test-Path $PreferredExe)) { $PreferredExe } else { $running[0].Path }
    if ($signal) {
        # --exit signals the running instance through its session-wide named event, from any copy of the exe.
        Start-Process -FilePath $signal -ArgumentList '--exit' -WindowStyle Hidden -Wait
    }

    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-Date) -lt $deadline -and @($running | Where-Object { -not $_.HasExited }).Count -gt 0) {
        Start-Sleep -Milliseconds 250
        $running | ForEach-Object { $_.Refresh() }
    }

    $stuck = @($running | Where-Object { -not $_.HasExited })
    if ($stuck.Count -gt 0) {
        Write-Warning 'BetterClipboard did not exit within 15 s; stopping it.'
        $stuck | Stop-Process -Force
    }

    return $true
}

function Rename-WithRetry([string] $Path, [string] $NewName) {
    <#
    .SYNOPSIS
        Renames a folder, retrying for about 10 seconds while another process holds a file in it.
    .DESCRIPTION
        Right after BetterClipboard exits, its folder can stay locked for a moment: an antivirus scan of the exe that just
        ran, a helper process of the Cmd tab finishing, Explorer reading the icon. Found in the QA pass of 2026-10-09 on a
        Windows 11 VM: an update failed with "The process cannot access the file because it is being used by another
        process", and a moment later nothing held the folder.
    .PARAMETER Path
        The folder to rename.
    .PARAMETER NewName
        Its new leaf name.
    #>
    for ($attempt = 1; ; $attempt++) {
        try {
            Rename-Item -Path $Path -NewName $NewName -ErrorAction Stop
            return
        }
        catch {
            if ($attempt -ge 20) { throw }
            Start-Sleep -Milliseconds 500
        }
    }
}

function Undo-FailedSwap([string] $Staging, [string] $Previous, [bool] $WasRunning, [string] $Exe, [string] $Reason) {
    <#
    .SYNOPSIS
        Leaves the installed version as it was after a swap that failed, starts it again when it was running, and throws.
    .PARAMETER Staging
        The new version's folder ("<install>.new"): removed.
    .PARAMETER Previous
        The old version's backup name ("<install>.old"): renamed back when the old folder was moved already.
    .PARAMETER WasRunning
        Whether BetterClipboard ran before the update (Stop-RunningApp's answer).
    .PARAMETER Exe
        The installed BetterClipboard.exe, to start again.
    .PARAMETER Reason
        The rename's error message, quoted in the thrown error.
    #>
    if ((Test-Path $Previous) -and -not (Test-Path $InstallDir)) {
        Rename-WithRetry -Path $Previous -NewName (Split-Path $InstallDir -Leaf)
    }

    Remove-Item $Staging -Recurse -Force -ErrorAction SilentlyContinue

    # The update stopped the app; leaving it stopped would leave the user without their clipboard history until sign-in.
    $restarted = $false
    if ($WasRunning -and (Test-Path $Exe)) {
        Start-AppUnelevated $Exe
        $restarted = $true
    }

    $again = if ($restarted) { ' BetterClipboard was started again.' } else { '' }
    throw "The install folder $InstallDir stayed in use by another program for 10 s, so it was not updated; the installed version is unchanged.$again Close programs that use that folder (a terminal or File Explorer window open in it), then run the installer again. ($Reason)"
}

function Restart-Explorer {
    $session = (Get-Process -Id $PID).SessionId
    $running = @(Get-Process -Name explorer -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -eq $session })
    if ($running.Count -eq 0) {
        # No shell in this session (a remote or service session, a replaced shell): nothing to restart, and starting
        # one here would open a stray File Explorer window. Explorer reads the change whenever it next starts.
        Write-Note 'Explorer is not running in this session; the change applies when it next starts.'
        return
    }

    Write-Step 'Restarting Explorer so the change takes effect (the taskbar blinks once; open folder windows close)'
    $running | Stop-Process -Force

    # Winlogon restarts the shell by itself (AutoRestartShell); starting one too would open a stray
    # File Explorer window, so only start it if it did not come back.
    $deadline = (Get-Date).AddSeconds(6)
    while ((Get-Date) -lt $deadline) {
        if (Get-Process -Name explorer -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -eq $session }) {
            return
        }

        Start-Sleep -Milliseconds 250
    }

    Start-Process explorer.exe
}

function Get-DisabledHotkeys {
    # StrictMode makes reading a missing registry value (or a property of $null) an error, hence the
    # explicit property check; an absent value means "Explorer registers every Win+<key> it knows".
    $item = Get-ItemProperty -Path $ExplorerAdvancedPath -ErrorAction SilentlyContinue
    if ($item -and $item.PSObject.Properties['DisabledHotkeys']) { return [string] $item.DisabledHotkeys }
    return ''
}

function Test-KeyReleased([char] $Key) {
    <#
    .SYNOPSIS
        Whether Explorer's DisabledHotkeys releases Win+$Key (case-insensitive, like Explorer).
    .PARAMETER Key
        A letter or digit.
    .OUTPUTS
        System.Boolean.
    #>
    return (Get-DisabledHotkeys).ToUpperInvariant().Contains([string] [char]::ToUpperInvariant($Key))
}

function Set-ExplorerHotkeysReleased([char[]] $Keys, [bool] $Release) {
    <#
    .SYNOPSIS
        Adds ($Release) or removes Win+<key> shortcuts in Explorer's DisabledHotkeys, keeping every other character.
    .DESCRIPTION
        Each character of the value disables one Win+<char> combination in Explorer; the user's other letters stay
        intact and in place, and a key already listed in either case is not added again - the same rules as the app's
        ExplorerHotkeys.WithKeys, so the two never fight over the value. Explorer reads it when it (re)starts.
    .PARAMETER Keys
        Letters or digits.
    .PARAMETER Release
        $true to release them from Explorer, $false to give them back.
    .OUTPUTS
        System.Boolean: whether the value changed (Explorer must restart to notice).
    #>
    $current = Get-DisabledHotkeys
    $upper = @($Keys | ForEach-Object { [char]::ToUpperInvariant($_) } | Select-Object -Unique)
    if ($Release) {
        $next = $current
        foreach ($key in $upper) {
            if (-not $next.ToUpperInvariant().Contains([string] $key)) { $next += $key }
        }
    }
    else {
        $next = -join ($current.ToCharArray() | Where-Object { $upper -notcontains [char]::ToUpperInvariant($_) })
    }

    if ($next -ceq $current) {
        return $false
    }

    if ($next.Length -eq 0) {
        Remove-ItemProperty -Path $ExplorerAdvancedPath -Name DisabledHotkeys -ErrorAction SilentlyContinue
    }
    else {
        # Explorer\Advanced holds every Explorer setting: only ever add the value (Confirm-RegistryKey says why).
        Confirm-RegistryKey $ExplorerAdvancedPath
        Set-ItemProperty -Path $ExplorerAdvancedPath -Name DisabledHotkeys -Value $next -Type String
    }

    return $true
}

function Get-ReleasableKey([string] $Shortcut) {
    <#
    .SYNOPSIS
        The key Explorer's DisabledHotkeys would need to free $Shortcut: its letter or digit when the shortcut is exactly
        Win plus one letter or digit; otherwise $null.
    .DESCRIPTION
        Only such shortcuts can be released from Explorer: the value names a character, not a set of modifiers (V freed
        Win+V alone - Win+Ctrl+V stayed with Windows). Every other shortcut someone owns is taken over by the app's
        keyboard hook. Accepts the spellings the app does for the Windows key, any case, spaces around '+'.
    .PARAMETER Shortcut
        A shortcut as written in settings or on the command line.
    .OUTPUTS
        System.Char, or $null.
    #>
    if ($Shortcut -match '^\s*(win|windows|meta|super|cmd)\s*\+\s*([a-z0-9])\s*$') {
        return [char]::ToUpperInvariant($Matches[2][0])
    }

    return $null
}

function Test-IsWinV([string] $Shortcut) {
    return (Get-ReleasableKey $Shortcut) -eq [char] 'V'
}

function Format-KeyNames([char[]] $Keys) {
    <#
    .SYNOPSIS
        "Win+V", "Win+V and Win+C", "Win+V, Win+C and Win+1" - for the installer's messages.
    .PARAMETER Keys
        Letters or digits.
    .OUTPUTS
        System.String.
    #>
    $names = @($Keys | ForEach-Object { "Win+$([char]::ToUpperInvariant($_))" })
    if ($names.Count -le 1) { return -join $names }
    return "$($names[0..($names.Count - 2)] -join ', ') and $($names[-1])"
}

function Get-ConfiguredHotkeys {
    <#
    .SYNOPSIS
        The shortcuts BetterClipboard's settings name (main one first), read without changing anything.
    .DESCRIPTION
        No settings file yet (a first install) means the app's default, Win+V. A file this PowerShell cannot read
        (the app's own reader also skips comments) gives $null - "unknown" - so nothing is taken over or given back
        on a guess.
    .OUTPUTS
        System.String[] (one array, never unrolled), or $null.
    #>
    $path = Join-Path $DataDir 'settings.json'
    if (-not (Test-Path -LiteralPath $path)) {
        return , @('Win+V')
    }

    try {
        $json = [IO.File]::ReadAllText($path) | ConvertFrom-Json
    }
    catch {
        return $null
    }

    $main = 'Win+V'
    if ($json.PSObject.Properties['OpenHotkey'] -and -not [string]::IsNullOrWhiteSpace([string] $json.OpenHotkey)) {
        $main = ([string] $json.OpenHotkey).Trim()
    }

    $list = @($main)
    if ($json.PSObject.Properties['ExtraOpenHotkeys'] -and $json.ExtraOpenHotkeys) {
        $list += @($json.ExtraOpenHotkeys | ForEach-Object { ([string] $_).Trim() } | Where-Object { $_ })
    }

    return , $list
}

function Get-RequestedHotkeys($Configured) {
    <#
    .SYNOPSIS
        The shortcuts this run must give BetterClipboard, or $null to keep the configured ones.
    .DESCRIPTION
        -Hotkey replaces the list (Win+V first unless -NoTakeOverWinV); -NoTakeOverWinV alone keeps the configured
        shortcuts except Win+V (Win+Alt+V when none is left); -TakeOverWinV adds Win+V back when the configured ones
        lack it. Whitespace is dropped from every shortcut: it never matters inside one, and an argument without spaces
        reaches BetterClipboard.exe unchanged on PowerShell 5.1 and 7 alike, which join -ArgumentList unquoted.
    .PARAMETER Configured
        Get-ConfiguredHotkeys' answer.
    .OUTPUTS
        System.String[] (one array, never unrolled), or $null.
    #>
    if ($Hotkey) {
        $list = @($Hotkey | ForEach-Object { ([string] $_) -replace '\s+', '' } | Where-Object { $_ })
        if ($list.Count -eq 0) {
            throw '-Hotkey names no shortcut. Example: -Hotkey Win+Alt+V, Ctrl+Alt+F9'
        }

        if (-not $NoTakeOverWinV) { $list = @('Win+V') + $list }
        return , $list
    }

    if ($NoTakeOverWinV) {
        $kept = @(@($Configured) | Where-Object { $_ -and -not (Test-IsWinV $_) } | ForEach-Object { $_ -replace '\s+', '' })
        if ($kept.Count -eq 0) { $kept = @($AlternativeHotkey) }
        return , $kept
    }

    if ($TakeOverWinV -and $null -ne $Configured -and @(@($Configured) | Where-Object { Test-IsWinV $_ }).Count -eq 0) {
        return , (@('Win+V') + @(@($Configured) | ForEach-Object { $_ -replace '\s+', '' }))
    }

    return $null
}

function Invoke-HotkeyCommand([string] $Exe, [string[]] $Shortcuts, [switch] $Validate) {
    <#
    .SYNOPSIS
        Runs "BetterClipboard.exe --set-hotkeys [--validate] <shortcuts>" and returns the canonical shortcuts it printed.
    .DESCRIPTION
        The app's own parser decides what a shortcut is and spells it canonically (win + alt + v -> Alt+Win+V); without
        -Validate the app's own settings store saves them (it refuses while that BetterClipboard runs). It is a GUI
        program, so the call operator would neither wait for it nor see its output: Start-Process redirects its
        standard output to a temp file instead.
    .PARAMETER Exe
        The BetterClipboard.exe to ask (the staging copy for -Validate, the installed one to save).
    .PARAMETER Shortcuts
        The shortcuts, main one first, without whitespace.
    .PARAMETER Validate
        Check only; save nothing.
    .OUTPUTS
        System.String[] (one array, never unrolled).
    #>
    $arguments = @('--set-hotkeys')
    if ($Validate) { $arguments += '--validate' }
    $arguments += $Shortcuts
    $outFile = [IO.Path]::GetTempFileName()
    try {
        $process = Start-Process -FilePath $Exe -ArgumentList $arguments -WorkingDirectory (Split-Path -Parent $Exe) -NoNewWindow -PassThru -RedirectStandardOutput $outFile
        # Windows PowerShell reports no exit code for a process whose handle was never taken while it ran.
        $null = $process.Handle
        if (-not $process.WaitForExit(60000)) {
            $process.Kill()
            throw "$ExeName did not answer within 60 s while checking the shortcuts."
        }

        $lines = @([IO.File]::ReadAllLines($outFile) | Where-Object { $_ })
        $code = $process.ExitCode
    }
    finally {
        Remove-Item -LiteralPath $outFile -Force -ErrorAction SilentlyContinue
    }

    if ($code -ne 0) {
        $reasons = @($lines | ForEach-Object { $_ -replace '^error:\s*', '' })
        if ($reasons.Count -eq 0) { $reasons = @("exit code $code") }
        throw ($reasons -join ' ')
    }

    return , $lines
}

function Read-InstallerState {
    <#
    .SYNOPSIS
        installer.json from an earlier run as an object, or $null when there is none or it cannot be read.
    .OUTPUTS
        PSCustomObject, or $null.
    #>
    if (-not (Test-Path $StatePath)) {
        return $null
    }

    try {
        return Get-Content $StatePath -Raw | ConvertFrom-Json
    }
    catch {
        Write-Note 'Ignoring an unreadable installer.json from a previous install.'
        return $null
    }
}

function Get-RecordedReleasedKeys($State) {
    <#
    .SYNOPSIS
        The Win+<key> shortcuts earlier installer runs released from Explorer, as upper-case letters and digits.
    .DESCRIPTION
        installer.json's releasedKeys, plus V when only the older releasedWinV flag says so (installers before
        -Hotkey recorded nothing else).
    .PARAMETER State
        Read-InstallerState's answer.
    .OUTPUTS
        System.Char[] (one array, never unrolled).
    #>
    $keys = @()
    if ($State) {
        if ($State.PSObject.Properties['releasedKeys'] -and $State.releasedKeys) {
            $keys += @(([string] $State.releasedKeys).ToUpperInvariant().ToCharArray())
        }

        if ($State.PSObject.Properties['releasedWinV'] -and $State.releasedWinV) {
            $keys += [char] 'V'
        }
    }

    return , @($keys | Where-Object { $_ -match '^[A-Z0-9]$' } | Select-Object -Unique)
}

# User PATH helpers - the same rules as the app's "Add bclip to PATH" button (Shell\UserPath.cs), so the
# two never disagree about whether the folder is listed.

function Get-UserPathEntries {
    # Raw value: reading it expanded and writing it back would freeze every %USERPROFILE%-style entry.
    # Callers wrap the result in @() - PowerShell unrolls a returned array (none -> $null, one -> a string).
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($UserEnvironmentKey)
    if (-not $key) { return @() }
    try {
        $raw = [string] $key.GetValue('Path', '', [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
    }
    finally {
        $key.Dispose()
    }

    return @($raw -split ';' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
}

function Test-SamePath([string] $Entry, [string] $Directory) {
    # Entries may be spelled with variables, other casing or a trailing slash; compare what they name.
    $a = [Environment]::ExpandEnvironmentVariables($Entry).TrimEnd('\', '/')
    return [string]::Equals($a, $Directory.TrimEnd('\', '/'), [StringComparison]::OrdinalIgnoreCase)
}

function Set-UserPathEntries([string[]] $Entries) {
    # REG_EXPAND_SZ like Windows' own value. [Environment]::SetEnvironmentVariable(..., 'User') would
    # write REG_SZ and silently stop every %VAR% entry from expanding.
    $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($UserEnvironmentKey)
    try {
        $key.SetValue('Path', ($Entries -join ';'), [Microsoft.Win32.RegistryValueKind]::ExpandString)
    }
    finally {
        $key.Dispose()
    }

    # Explorer re-reads the user environment only when told; without this, terminals started from it
    # would not see the change until the next sign-in.
    if (-not ('BetterClipboardInstaller.NativeMethods' -as [type])) {
        Add-Type -Namespace BetterClipboardInstaller -Name NativeMethods -MemberDefinition '[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, UIntPtr wParam, string lParam, uint flags, uint timeout, out UIntPtr result);'
    }

    $ignored = [UIntPtr]::Zero
    # HWND_BROADCAST, WM_SETTINGCHANGE "Environment", SMTO_ABORTIFHUNG + 2 s: a hung window cannot stall us.
    [void] [BetterClipboardInstaller.NativeMethods]::SendMessageTimeout([IntPtr] 0xFFFF, 0x1A, [UIntPtr]::Zero, 'Environment', 2, 2000, [ref] $ignored)
}

function Test-InUserPath([string] $Directory) {
    return @(@(Get-UserPathEntries) | Where-Object { Test-SamePath $_ $Directory }).Count -gt 0
}

function Add-ToUserPath([string] $Directory) {
    $entries = @(Get-UserPathEntries)
    if (@($entries | Where-Object { Test-SamePath $_ $Directory }).Count -gt 0) {
        return $false
    }

    Set-UserPathEntries ($entries + $Directory.TrimEnd('\', '/'))
    return $true
}

function Remove-FromUserPath([string] $Directory) {
    $entries = @(Get-UserPathEntries)
    $kept = @($entries | Where-Object { -not (Test-SamePath $_ $Directory) })
    if ($kept.Count -eq $entries.Count) {
        return $false
    }

    Set-UserPathEntries $kept
    return $true
}

function Get-ChocolateyCopy {
    <#
    .SYNOPSIS
        BetterClipboard.exe of a Chocolatey install (the betterclipboard package), or $null without one.
    .DESCRIPTION
        Both copies use the same history, so neither may undo the other's "Start with Windows" entry or Win+V
        release; the package (packaging/chocolatey) applies the same rule the other way round.
    .OUTPUTS
        System.String, or $null.
    #>
    $root = if ($env:ChocolateyInstall) { $env:ChocolateyInstall } else { Join-Path $env:ProgramData 'chocolatey' }
    $exe = Join-Path $root 'lib\betterclipboard\tools\app\BetterClipboard.exe'
    if (Test-Path -LiteralPath $exe) { return $exe }
    return $null
}

function Get-RunTarget {
    <#
    .SYNOPSIS
        Executable the "Start with Windows" value starts, or '' when there is none or it has another shape.
    .DESCRIPTION
        The value is '"<exe>" --background', the format of the app's own toggle and of this installer; the quoted
        path is returned.
    .OUTPUTS
        System.String.
    #>
    $item = Get-ItemProperty -Path $RunKeyPath -ErrorAction SilentlyContinue
    if ($item -and $item.PSObject.Properties[$AppName] -and ([string] $item.$AppName) -match '^\s*"([^"]+)"') {
        return $Matches[1]
    }

    return ''
}

function Test-Elevated {
    $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Start-AppUnelevated([string] $Exe) {
    if (Test-Elevated) {
        # An app started from an elevated prompt runs elevated; asking the shell to start it gives it the
        # normal (unelevated) token it gets at sign-in.
        Start-Process -FilePath explorer.exe -ArgumentList "`"$Exe`""
    }
    else {
        # Its own folder as the working folder, like the Start menu shortcut: the installer's is the temp folder.
        Start-Process -FilePath $Exe -WorkingDirectory (Split-Path -Parent $Exe)
    }
}

function Install-BetterClipboard {
    if (Test-Elevated) {
        Write-Warning 'Running elevated. BetterClipboard installs per user; a normal (non-admin) PowerShell is recommended.'
    }

    # Contradictions fail here, before anything is downloaded or changed.
    if ($TakeOverWinV -and $NoTakeOverWinV) {
        throw '-TakeOverWinV and -NoTakeOverWinV contradict each other; give one of them (taking over Win+V is the default).'
    }

    if ($NoTakeOverWinV -and @(@($Hotkey) | Where-Object { Test-IsWinV $_ }).Count -gt 0) {
        throw '-NoTakeOverWinV leaves Win+V to Windows, so Win+V cannot be one of the -Hotkey shortcuts too.'
    }

    $chocolateyCopy = Get-ChocolateyCopy
    if ($chocolateyCopy) {
        Write-Warning "BetterClipboard is also installed with Chocolatey ($chocolateyCopy); 'choco upgrade betterclipboard' updates that copy. Both copies use the same history."
    }

    $arch = Get-OsArchitecture
    Write-Step "Looking up $Repository release '$Version' for win-$arch"
    $release = Get-Release $Version
    $tag = [string] $release.tag_name
    $semver = $tag.TrimStart('v', 'V')
    $zipName = "$AppName-$semver-win-$arch.zip"
    $asset = @($release.assets | Where-Object { $_.name -eq $zipName }) | Select-Object -First 1
    $sumsAsset = @($release.assets | Where-Object { $_.name -eq 'SHA256SUMS.txt' }) | Select-Object -First 1
    if (-not $asset) { throw "Release $tag has no $zipName." }
    if (-not $sumsAsset) { throw "Release $tag has no SHA256SUMS.txt; refusing to install an unverified download." }

    # The shortcuts this run sets, if any. Settings are read now, while the running app still owns them unchanged.
    $configured = Get-ConfiguredHotkeys
    $requested = Get-RequestedHotkeys $configured
    if ($null -ne $requested) {
        $comparable = ConvertTo-ComparableVersion $semver
        if ($null -eq $comparable -or $comparable -lt $HotkeyOptionsMinimumVersion) {
            throw "-Hotkey and -NoTakeOverWinV need BetterClipboard $HotkeyOptionsMinimumVersion or newer, and this release is $tag. Install it without them and pick shortcuts in Settings > Shortcut."
        }
    }

    $temp = Join-Path ([IO.Path]::GetTempPath()) "$AppName-install-$([Guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $temp | Out-Null
    $canonical = $null
    try {
        Write-Step "Downloading $zipName ($([Math]::Round($asset.size / 1MB, 1)) MB)"
        $zip = Join-Path $temp $zipName
        Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $zip -UseBasicParsing -Headers @{ 'User-Agent' = "$AppName-installer" }
        $sums = (Invoke-WebRequest -Uri $sumsAsset.browser_download_url -UseBasicParsing -Headers @{ 'User-Agent' = "$AppName-installer" }).Content
        if ($sums -is [byte[]]) { $sums = [Text.Encoding]::ASCII.GetString($sums) }

        Write-Step 'Verifying SHA-256'
        $actual = (Get-FileHash -Path $zip -Algorithm SHA256).Hash.ToLowerInvariant()
        $expected = Get-AssetSha256 $asset $sums
        if ($actual -ne $expected) {
            throw "Checksum mismatch for $zipName (expected $expected, got $actual). Nothing was installed."
        }

        # GitHub computes its own digest at upload time; when present it independently confirms the file.
        if ($asset.PSObject.Properties['digest'] -and $asset.digest -and $asset.digest -ne "sha256:$actual") {
            throw "GitHub's digest for $zipName ($($asset.digest)) does not match the download. Nothing was installed."
        }

        Write-Note "OK $actual"

        $staging = "$InstallDir.new"
        $previous = "$InstallDir.old"
        foreach ($leftover in @($staging, $previous)) {
            if (Test-Path $leftover) { Remove-Item $leftover -Recurse -Force }
        }

        Expand-Archive -Path $zip -DestinationPath $staging -Force
        Get-ChildItem -Path $staging -Recurse -File | Unblock-File
        if (-not (Test-Path (Join-Path $staging $ExeName))) {
            throw "The archive does not contain $ExeName at its root."
        }

        if ($null -ne $requested) {
            # Checked by the new version's own parser before the running app is closed: a typo fails with nothing changed.
            Write-Step "Checking the shortcuts: $($requested -join ', ')"
            try {
                $canonical = Invoke-HotkeyCommand -Exe (Join-Path $staging $ExeName) -Shortcuts $requested -Validate
            }
            catch {
                Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
                throw "Shortcuts not accepted: $($_.Exception.Message) Nothing was installed."
            }

            # An exit code of 0 with nothing printed means the program never ran the command (a build without it
            # starts normally instead): never save an empty list on that.
            if (@($canonical).Count -eq 0) {
                Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
                throw "$ExeName of $tag did not answer --set-hotkeys, so the shortcuts cannot be set. Nothing was installed."
            }

            if ($NoTakeOverWinV -and @($canonical | Where-Object { Test-IsWinV $_ }).Count -gt 0) {
                Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
                throw '-NoTakeOverWinV leaves Win+V to Windows, so Win+V cannot be one of the -Hotkey shortcuts too. Nothing was installed.'
            }
        }

        $exe = Join-Path $InstallDir $ExeName
        $wasRunning = Stop-RunningApp $exe

        Write-Step "Installing $tag to $InstallDir"
        # Swap via renames: if the new files cannot be moved in, the previous version is put back - and started again
        # when it ran before (Undo-FailedSwap). Each rename retries while another process briefly holds the folder.
        New-Item -ItemType Directory -Path (Split-Path $InstallDir -Parent) -Force | Out-Null
        try {
            if (Test-Path $InstallDir) { Rename-WithRetry -Path $InstallDir -NewName (Split-Path $previous -Leaf) }
            Rename-WithRetry -Path $staging -NewName (Split-Path $InstallDir -Leaf)
        }
        catch {
            Undo-FailedSwap -Staging $staging -Previous $previous -WasRunning $wasRunning -Exe $exe -Reason $_.Exception.Message
        }

        if (Test-Path $previous) { Remove-Item $previous -Recurse -Force -ErrorAction SilentlyContinue }
    }
    finally {
        Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
    }

    # What BetterClipboard answers to from now on: the shortcuts just set, else the ones it already had.
    $effective = $configured
    if ($null -ne $canonical) {
        Write-Step "Setting the shortcuts that open BetterClipboard: $($canonical -join ', ')"
        try {
            $effective = Invoke-HotkeyCommand -Exe $exe -Shortcuts $canonical
        }
        catch {
            # The new version is in place; only the shortcuts stay as they were. Explorer is then left alone too.
            Write-Warning "The shortcuts were not changed ($($_.Exception.Message)). Set them in Settings > Shortcut."
        }
    }

    if (-not $NoShortcut) {
        Write-Step 'Adding the Start menu shortcut'
        $shell = New-Object -ComObject WScript.Shell
        $link = $shell.CreateShortcut($ShortcutPath)
        $link.TargetPath = $exe
        $link.WorkingDirectory = $InstallDir
        $link.IconLocation = "$exe,0"
        $link.Description = 'Persistent, searchable clipboard history (Win+V)'
        $link.Save()
    }

    if (-not $NoStartup) {
        $startupTarget = Get-RunTarget
        if ($startupTarget -and $chocolateyCopy -and [string]::Equals($startupTarget, $chocolateyCopy, [StringComparison]::OrdinalIgnoreCase)) {
            # One value for both copies: taking it over would leave no copy starting once this one is uninstalled.
            Write-Note "Start with Windows stays with the Chocolatey copy ($chocolateyCopy); both use the same history."
        }
        else {
            Write-Step 'Starting with Windows (Settings > Start with Windows toggles this)'
            # Exactly the format StartupRegistration writes, so the app's own toggle shows it as on.
            # The Run key holds every other app's startup entry: only ever add ours (Confirm-RegistryKey says why).
            Confirm-RegistryKey $RunKeyPath
            Set-ItemProperty -Path $RunKeyPath -Name $AppName -Value "`"$exe`" --background" -Type String
        }
    }

    Write-Step 'Registering in Settings > Apps > Installed apps'
    $sizeKb = [int] ((Get-ChildItem -Path $InstallDir -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1KB)
    Confirm-RegistryKey $UninstallKeyPath
    $uninstallCommand = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$(Join-Path $InstallDir 'install.ps1')`" -Uninstall -InstallDir `"$InstallDir`""
    $values = @{
        DisplayName          = $AppName
        DisplayVersion       = $semver
        Publisher            = 'BetterClipboard contributors'
        DisplayIcon          = "$exe,0"
        InstallLocation      = $InstallDir
        UninstallString      = $uninstallCommand
        QuietUninstallString = $uninstallCommand
        URLInfoAbout         = "https://github.com/$Repository"
        HelpLink             = "https://github.com/$Repository/issues"
    }
    foreach ($name in $values.Keys) { Set-ItemProperty -Path $UninstallKeyPath -Name $name -Value $values[$name] -Type String }
    Set-ItemProperty -Path $UninstallKeyPath -Name NoModify -Value 1 -Type DWord
    Set-ItemProperty -Path $UninstallKeyPath -Name NoRepair -Value 1 -Type DWord
    Set-ItemProperty -Path $UninstallKeyPath -Name EstimatedSize -Value $sizeKb -Type DWord

    if ($AddToPath) {
        Write-Step 'Adding the install folder to your user PATH (for bclip, the command line)'
        if (-not (Add-ToUserPath $InstallDir)) { Write-Note 'It was already on your PATH.' }

        # This window too: "irm ... | iex" runs the installer inside the caller's own PowerShell process,
        # which keeps the PATH it started with - without this, bclip would only work in new terminals.
        if (@($env:Path -split ';' | Where-Object { $_ -and (Test-SamePath $_ $InstallDir) }).Count -eq 0) {
            $env:Path = "$($env:Path.TrimEnd(';'));$InstallDir"
        }
    }

    # Take over the Win+letter shortcuts BetterClipboard answers to (Win+V by default): Explorer stops registering
    # them, so BetterClipboard registers them directly. And with -NoTakeOverWinV, a released Win+V goes back to
    # Windows: no BetterClipboard shortcut uses it, so it would do nothing at all.
    $state = Read-InstallerState
    # Not wrapped in @(): the function returns its array as one object, and @() would nest it.
    $recorded = Get-RecordedReleasedKeys $state
    $released = @()
    $givenBack = @()
    if ($null -eq $effective) {
        Write-Note "BetterClipboard's settings could not be read here, so Explorer's shortcuts were left as they are."
    }
    else {
        $wanted = @(@($effective) | ForEach-Object { Get-ReleasableKey $_ } | Where-Object { $null -ne $_ } | Select-Object -Unique)
        $released = @($wanted | Where-Object { -not (Test-KeyReleased $_) })
        if ($released.Count -gt 0) {
            Write-Step "Taking over $(Format-KeyNames $released): Explorer stops registering $(if ($released.Count -eq 1) { 'it' } else { 'them' }) (DisabledHotkeys)"
            Set-ExplorerHotkeysReleased $released $true | Out-Null
        }
        elseif ($wanted.Count -gt 0) {
            Write-Note "$(Format-KeyNames $wanted) $(if ($wanted.Count -eq 1) { 'was' } else { 'were' }) already released from Explorer."
        }

        if ($NoTakeOverWinV -and $wanted -notcontains [char] 'V' -and (Test-KeyReleased 'V')) {
            Write-Step 'Giving Win+V back to Windows (Explorer registers it again)'
            Set-ExplorerHotkeysReleased @([char] 'V') $false | Out-Null
            $givenBack = @([char] 'V')
        }
    }

    if ($released.Count -gt 0 -or $givenBack.Count -gt 0) {
        Restart-Explorer
    }

    # Record what was installed where (support/diagnostics; the app never reads it). Keys released by an earlier run
    # stay recorded across updates, so -Uninstall knows to give them back.
    $releasedKeys = -join @(@($recorded) + @($released) | Where-Object { $givenBack -notcontains $_ } | Select-Object -Unique)
    New-Item -ItemType Directory -Path $DataDir -Force | Out-Null
    $newState = [ordered]@{
        version      = $semver
        installDir   = $InstallDir
        releasedWinV = $releasedKeys.Contains('V')
        releasedKeys = $releasedKeys
        onUserPath   = Test-InUserPath $InstallDir
        installedUtc = (Get-Date).ToUniversalTime().ToString('o')
    }
    $newState | ConvertTo-Json | Set-Content -Path $StatePath -Encoding UTF8

    if (-not $NoLaunch) {
        Write-Step 'Starting BetterClipboard'
        Start-AppUnelevated $exe
    }

    Write-Host ''
    Write-Host "BetterClipboard $semver is installed." -ForegroundColor Green
    $press = if ($null -ne $effective) { @($effective) -join ' or ' } else { 'your shortcut' }
    Write-Note "Press $press to open it. Your history is encrypted in:"
    Write-Note "  $DataDir"
    if ($null -ne $effective -and @(@($effective) | Where-Object { Test-IsWinV $_ }).Count -gt 0) {
        Write-Note 'To leave Win+V to Windows instead, re-run with -NoTakeOverWinV (and pick your own shortcuts with -Hotkey).'
    }
    elseif ($NoTakeOverWinV) {
        Write-Note 'Win+V stays with Windows. Settings > Shortcut adds or removes shortcuts any time.'
    }

    if ($AddToPath) {
        Write-Note 'bclip is on your PATH. Turn it on in Settings > Command line (off by default), then: bclip help'
    }
    else {
        Write-Note 'Optional: re-run with -AddToPath to use bclip, the command line for scripts and AI agents.'
    }
}

function Uninstall-BetterClipboard {
    $exe = Join-Path $InstallDir $ExeName
    $null = Stop-RunningApp $exe

    Write-Step 'Removing the startup entry, shortcut and Installed-apps entry'
    # The startup entry stays when it starts another copy that still exists (the Chocolatey one). This copy's entry,
    # or a stale one pointing at a missing file, goes - or, while the Chocolatey copy stays installed, is handed to it:
    # both copies share one history, one settings file and this one value, so the user's choice to start
    # BetterClipboard with Windows carries over (the package's own uninstall hands it to this copy the same way).
    $startupTarget = Get-RunTarget
    $chocolateyCopy = Get-ChocolateyCopy
    if ($startupTarget -and -not $startupTarget.StartsWith($InstallDir, [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $startupTarget)) {
        Write-Note "Kept the startup entry: it starts the copy at $startupTarget."
    }
    elseif ($startupTarget -and $chocolateyCopy) {
        # The app's own format, so its Settings toggle shows the entry as on.
        Set-ItemProperty -Path $RunKeyPath -Name $AppName -Value "`"$chocolateyCopy`" --background" -Type String
        Write-Note "Start with Windows now starts the Chocolatey copy ($chocolateyCopy)."
    }
    else {
        Remove-ItemProperty -Path $RunKeyPath -Name $AppName -ErrorAction SilentlyContinue
    }

    Remove-Item -Path $ShortcutPath -Force -ErrorAction SilentlyContinue
    Remove-Item -Path $UninstallKeyPath -Recurse -Force -ErrorAction SilentlyContinue

    # The folder is about to disappear; a PATH entry for it (from -AddToPath or the app's "Add bclip to
    # PATH" button - either way) would leave every shell probing a missing directory.
    if (Remove-FromUserPath $InstallDir) {
        Write-Note "Removed $InstallDir from your user PATH."
    }

    # Without BetterClipboard, a released Win+<key> would do nothing at all - give back every one BetterClipboard used:
    # Win+V (what every version's installer and Settings release), the keys installer runs recorded, and the Win+letter
    # shortcuts in its settings (Settings > Release from Explorer releases those). Unless the Chocolatey copy remains:
    # it still uses them ($chocolateyCopy, read above with the startup entry).
    $configured = Get-ConfiguredHotkeys
    $recorded = Get-RecordedReleasedKeys (Read-InstallerState)
    $candidates = @([char] 'V') + @($recorded) + @(@($configured) | ForEach-Object { Get-ReleasableKey $_ } | Where-Object { $null -ne $_ })
    $toGiveBack = @($candidates | Select-Object -Unique | Where-Object { Test-KeyReleased $_ })
    if (-not $KeepWinVReleased -and $toGiveBack.Count -gt 0) {
        if ($chocolateyCopy) {
            Write-Note "$(Format-KeyNames $toGiveBack) $(if ($toGiveBack.Count -eq 1) { 'stays' } else { 'stay' }) released from Explorer: the Chocolatey copy ($chocolateyCopy) still uses $(if ($toGiveBack.Count -eq 1) { 'it' } else { 'them' })."
        }
        else {
            Write-Step "Giving $(Format-KeyNames $toGiveBack) back to Windows"
            Set-ExplorerHotkeysReleased $toGiveBack $false | Out-Null
            Restart-Explorer
        }
    }

    Write-Step "Removing $InstallDir"
    # The uninstall command runs this very script from the install folder (the script is already in
    # memory, so deleting its file is fine), and a console may have been opened there. Set-Location only
    # moves PowerShell's own location; the process's Win32 current directory is a separate open handle
    # that would keep the folder undeletable, so both are moved out.
    $neutral = [IO.Path]::GetTempPath()
    Set-Location $neutral
    [Environment]::CurrentDirectory = $neutral
    Remove-Item -Path $StatePath -Force -ErrorAction SilentlyContinue
    if (Test-Path $InstallDir) {
        try {
            Remove-Item -Path $InstallDir -Recurse -Force
        }
        catch {
            # Everything else is already undone; only the files remain. Say so plainly instead of failing
            # with a raw sharing violation.
            Write-Warning "Could not delete $InstallDir ($($_.Exception.Message)). Close any window or console open in that folder and delete it manually."
        }
    }

    if ($RemoveData) {
        Write-Step "Deleting history, settings and logs in $DataDir"
        if (Test-Path $DataDir) { Remove-Item -Path $DataDir -Recurse -Force }
    }
    else {
        Write-Note "Your history was kept in $DataDir (use -RemoveData to delete it)."
    }

    Write-Host ''
    Write-Host 'BetterClipboard was uninstalled.' -ForegroundColor Green
}

# Paths the caller typed are relative to the caller's folder, so they are resolved before the installer moves to the
# temp folder; the data folder override too (tests and dev runs pass relative ones).
$InstallDir = Resolve-FileSystemPath $InstallDir
$DataDir = Resolve-FileSystemPath $DataDir
$StatePath = Join-Path $DataDir 'installer.json'

Enter-WorkingFolder
try {
    if ($Uninstall) { Uninstall-BetterClipboard } else { Install-BetterClipboard }
}
finally {
    Exit-WorkingFolder
}
