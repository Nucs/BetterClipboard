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
      5. Optionally (-TakeOverWinV) tells Explorer to release Win+V so BetterClipboard can register it
         directly, and restarts Explorer (the taskbar blinks once).
      6. Optionally (-AddToPath) puts the install folder on your user PATH, so terminals can run bclip,
         BetterClipboard's command line for scripts and AI agents.

    Your clipboard history lives in %LOCALAPPDATA%\BetterClipboard (encrypted, bound to this PC and
    your Windows account) and is never touched by install or update; -Uninstall keeps it unless
    -RemoveData is given.

.PARAMETER Version
    Release to install: 'latest' (default) or a version such as 0.1.0 / v0.1.0.

.PARAMETER InstallDir
    Target folder. Default: %LOCALAPPDATA%\Programs\BetterClipboard.

.PARAMETER TakeOverWinV
    Release Win+V from Explorer (HKCU ...\Explorer\Advanced\DisabledHotkeys gets 'V') and restart
    Explorer. Without it BetterClipboard still owns Win+V through a keyboard hook while it runs; with
    it, Win+V also works over elevated windows, but does nothing while BetterClipboard is not running.

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
    Remove BetterClipboard (files, shortcut, startup entry, Installed-apps entry). Win+V is given back
    to Windows if it had been released from Explorer (use -KeepWinVReleased to keep it released).

.PARAMETER RemoveData
    With -Uninstall: also delete the history, settings and logs folder. Irreversible.

.PARAMETER KeepWinVReleased
    With -Uninstall: leave Explorer's DisabledHotkeys as it is.

.PARAMETER Repository
    GitHub repository to install from (owner/name), for forks. Default: Nucs/BetterClipboard.

.EXAMPLE
    irm https://raw.githubusercontent.com/Nucs/BetterClipboard/main/install.ps1 | iex

    Installs or updates to the latest release.

.EXAMPLE
    & ([scriptblock]::Create((irm https://raw.githubusercontent.com/Nucs/BetterClipboard/main/install.ps1))) -TakeOverWinV

    Same, and releases Win+V from Explorer.

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

# Same resolution as the app (AppPaths.ResolveDefault): the override variable exists so tests and dev
# runs never touch the real history - the installer honors it for the same reason.
$DataDir = if ($env:BETTERCLIPBOARD_DATA_DIR) { $env:BETTERCLIPBOARD_DATA_DIR } else { Join-Path $env:LOCALAPPDATA $AppName }
$StatePath = Join-Path $DataDir 'installer.json'

function Write-Step([string] $Message) { Write-Host "==> $Message" -ForegroundColor Cyan }
function Write-Note([string] $Message) { Write-Host "    $Message" }

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

function Stop-RunningApp([string] $PreferredExe) {
    $session = (Get-Process -Id $PID).SessionId
    $running = @(Get-Process -Name $AppName -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -eq $session })
    if ($running.Count -eq 0) {
        return
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
}

function Restart-Explorer {
    Write-Step 'Restarting Explorer so the Win+V change takes effect (the taskbar blinks once)'
    $session = (Get-Process -Id $PID).SessionId
    Get-Process -Name explorer -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -eq $session } | Stop-Process -Force

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

function Set-WinVReleased([bool] $Release) {
    # Each character of DisabledHotkeys disables one Win+<char> combination in Explorer; keep the
    # user's other letters intact.
    $current = Get-DisabledHotkeys
    $without = -join ($current.ToCharArray() | Where-Object { [char]::ToUpperInvariant($_) -ne 'V' })
    $next = if ($Release) { $without + 'V' } else { $without }
    if ($next -eq $current) {
        return $false
    }

    if ($next.Length -eq 0) {
        Remove-ItemProperty -Path $ExplorerAdvancedPath -Name DisabledHotkeys -ErrorAction SilentlyContinue
    }
    else {
        New-Item -Path $ExplorerAdvancedPath -Force | Out-Null
        Set-ItemProperty -Path $ExplorerAdvancedPath -Name DisabledHotkeys -Value $next -Type String
    }

    return $true
}

function Test-WinVReleased {
    return (Get-DisabledHotkeys).ToUpperInvariant().Contains('V')
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
        Start-Process -FilePath $Exe
    }
}

function Install-BetterClipboard {
    if (Test-Elevated) {
        Write-Warning 'Running elevated. BetterClipboard installs per user; a normal (non-admin) PowerShell is recommended.'
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

    $temp = Join-Path ([IO.Path]::GetTempPath()) "$AppName-install-$([Guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $temp | Out-Null
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

        $exe = Join-Path $InstallDir $ExeName
        Stop-RunningApp $exe

        Write-Step "Installing $tag to $InstallDir"
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

        # Swap via renames: if the new files cannot be moved in, the previous version is put back.
        New-Item -ItemType Directory -Path (Split-Path $InstallDir -Parent) -Force | Out-Null
        if (Test-Path $InstallDir) { Rename-Item -Path $InstallDir -NewName (Split-Path $previous -Leaf) }
        try {
            Rename-Item -Path $staging -NewName (Split-Path $InstallDir -Leaf)
        }
        catch {
            if (Test-Path $previous) { Rename-Item -Path $previous -NewName (Split-Path $InstallDir -Leaf) }
            throw
        }

        if (Test-Path $previous) { Remove-Item $previous -Recurse -Force -ErrorAction SilentlyContinue }
    }
    finally {
        Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
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
        Write-Step 'Starting with Windows (Settings > Start with Windows toggles this)'
        # Exactly the format StartupRegistration writes, so the app's own toggle shows it as on.
        New-Item -Path $RunKeyPath -Force | Out-Null
        Set-ItemProperty -Path $RunKeyPath -Name $AppName -Value "`"$exe`" --background" -Type String
    }

    Write-Step 'Registering in Settings > Apps > Installed apps'
    $sizeKb = [int] ((Get-ChildItem -Path $InstallDir -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1KB)
    New-Item -Path $UninstallKeyPath -Force | Out-Null
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

    $releasedByUs = $false
    if ($TakeOverWinV) {
        $releasedByUs = Set-WinVReleased $true
        if ($releasedByUs) { Restart-Explorer } else { Write-Note 'Win+V was already released from Explorer.' }
    }

    # Record what was installed where (support/diagnostics; the app never reads it). A Win+V release
    # made by an earlier run is remembered across updates.
    $previouslyReleased = $false
    if (Test-Path $StatePath) {
        try {
            $old = Get-Content $StatePath -Raw | ConvertFrom-Json
            $previouslyReleased = [bool] ($old.PSObject.Properties['releasedWinV'] -and $old.releasedWinV)
        }
        catch {
            Write-Note 'Ignoring an unreadable installer.json from a previous install.'
        }
    }

    New-Item -ItemType Directory -Path $DataDir -Force | Out-Null
    $state = [ordered]@{
        version      = $semver
        installDir   = $InstallDir
        releasedWinV = $releasedByUs -or $previouslyReleased
        onUserPath   = Test-InUserPath $InstallDir
        installedUtc = (Get-Date).ToUniversalTime().ToString('o')
    }
    $state | ConvertTo-Json | Set-Content -Path $StatePath -Encoding UTF8

    if (-not $NoLaunch) {
        Write-Step 'Starting BetterClipboard'
        Start-AppUnelevated $exe
    }

    Write-Host ''
    Write-Host "BetterClipboard $semver is installed." -ForegroundColor Green
    Write-Note 'Press Win+V to open it. Your history is encrypted in:'
    Write-Note "  $DataDir"
    if (-not $TakeOverWinV) {
        Write-Note 'Optional: re-run with -TakeOverWinV to release Win+V from Explorer (works over elevated windows too).'
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
    Stop-RunningApp $exe

    Write-Step 'Removing the startup entry, shortcut and Installed-apps entry'
    Remove-ItemProperty -Path $RunKeyPath -Name $AppName -ErrorAction SilentlyContinue
    Remove-Item -Path $ShortcutPath -Force -ErrorAction SilentlyContinue
    Remove-Item -Path $UninstallKeyPath -Recurse -Force -ErrorAction SilentlyContinue

    # The folder is about to disappear; a PATH entry for it (from -AddToPath or the app's "Add bclip to
    # PATH" button - either way) would leave every shell probing a missing directory.
    if (Remove-FromUserPath $InstallDir) {
        Write-Note "Removed $InstallDir from your user PATH."
    }

    # Without BetterClipboard, a released Win+V would do nothing at all - give it back to Windows.
    if (-not $KeepWinVReleased -and (Test-WinVReleased)) {
        Set-WinVReleased $false | Out-Null
        Restart-Explorer
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

if ($Uninstall) { Uninstall-BetterClipboard } else { Install-BetterClipboard }
