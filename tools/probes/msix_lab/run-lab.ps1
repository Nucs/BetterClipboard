<#
.SYNOPSIS
    Registers msix_lab as a loose full-trust package, runs its measurements and prints what an outside process sees.

.DESCRIPTION
    For a claude-desktops Windows desktop only: it switches Developer Mode on, registers a package, and writes BC-TEST
    keys and files into the signed-in user's profile. It refuses to run elsewhere unless -ConfirmDisposableMachine.

    Steps: fixtures made by this (unpackaged) shell; the package registered from -LabDirectory; each msixlab command
    run through the package's alias; then the real registry and the real folders listed from this shell, which is
    what tells a redirected write from a real one. -Unregister removes the package again and lists what stayed.

.PARAMETER LabDirectory
    The folder that holds msixlab.exe, AppxManifest.xml, child.cmd and Assets (the published lab).

.PARAMETER PackagePath
    A signed msixlab package to install instead of registering -LabDirectory as a loose package. Installed that way
    the lab runs from Windows' own package folder (C:\Program Files\WindowsApps), with the access rules a Store app
    has: the measurement that matters for a child process that is the package's own program. Its signer must be
    trusted on this PC first (Import-Certificate into Cert:\LocalMachine\TrustedPeople), and its languages are the
    manifest's own (-Languages is ignored).

.PARAMETER OutDirectory
    Where the result file goes. Outside AppData on purpose: nothing there is redirected.

.PARAMETER Languages
    The package's declared languages for this run, e.g. 'en-US' or 'en-US','ru-RU','he-IL' (the language test).

.PARAMETER Only
    Run only these sections (identity, apps, registry, children, selfchild, files, filewatch, dpapi, objects, notify,
    languages, cliphistory, startup). Default: all of them.

.PARAMETER Unregister
    Remove the package and the fixtures, then list what stayed behind.

.PARAMETER ConfirmDisposableMachine
    Run although this does not look like a claude-desktops desktop.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File C:\lab\run-lab.ps1 -LabDirectory C:\lab

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File C:\lab\run-lab.ps1 -Only languages -Languages en-US,ru-RU

.NOTES
    Windows PowerShell 5.1 and PowerShell 7. CLAUDE.md §2.47 has the results.
#>
[CmdletBinding()]
param(
    [string]$LabDirectory = $PSScriptRoot,
    [string]$PackagePath = '',
    [string]$OutDirectory = 'C:\lab-out',
    [string[]]$Languages = @('en-US'),
    [string[]]$Only = @(),
    [switch]$Unregister,
    [switch]$ConfirmDisposableMachine
)

Set-StrictMode -Version 3
$ErrorActionPreference = 'Stop'

# With -File, PowerShell hands "a,b,c" over as one text, not as a list: split it here so both spellings work.
$Only = @($Only | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
$Languages = @($Languages | ForEach-Object { $_ -split ',' } | Where-Object { $_ })

# The guard: a claude-desktops guest has its agent's folder; anything else needs the explicit switch.
if (-not (Test-Path 'C:\ProgramData\claude-desktop') -and -not $ConfirmDisposableMachine) {
    throw 'msix_lab registers a package and writes test data into the profile: run it in a claude-desktops Windows desktop (or pass -ConfirmDisposableMachine on a disposable machine).'
}

$packageName = 'BCTest.MsixLab'
$testKey = 'HKCU:\Software\BC-TEST-MSIX'
$watchKey = 'HKCU:\Software\BC-TEST-MSIX-Watch'
$local = $env:LOCALAPPDATA
$alias = Join-Path $local 'Microsoft\WindowsApps\msixlab.exe'
$result = Join-Path $OutDirectory 'result.txt'

function Write-Section([string]$Title) {
    <#
    .SYNOPSIS
        Writes one heading per step to the result file and the console, so the two read alike.
    .PARAMETER Title
        The step's name.
    .OUTPUTS
        None. The heading goes to the host and is appended to the result file.
    #>
    $line = "`n== $Title"
    Write-Host $line
    Add-Content -Path $result -Value $line -Encoding UTF8
}

function Write-Result([string[]]$Lines) {
    <#
    .SYNOPSIS
        Records what this shell measured itself, outside the package (the lab writes its own lines).
    .PARAMETER Lines
        The lines to show and to append to the result file.
    .OUTPUTS
        None.
    #>
    foreach ($line in $Lines) {
        Write-Host $line
        Add-Content -Path $result -Value $line -Encoding UTF8
    }
}

function Invoke-Lab([string[]]$Arguments) {
    <#
    .SYNOPSIS
        Runs the lab through its alias and waits, so the process starts with package identity; the lab appends its
        lines to the result file.
    .PARAMETER Arguments
        The lab's command and options (without --out, which is added here).
    .OUTPUTS
        None. A lab that fails says so in the result file; this function never throws for it.
    #>
    & $alias @Arguments --out $result | Out-Null
}

function Start-LabInBackground([string[]]$Arguments) {
    <#
    .SYNOPSIS
        Starts the lab through its alias without waiting, for measurements where this shell must act while the lab
        waits (a registry notification, a named object).
    .PARAMETER Arguments
        The lab's command and options (without --out, which is added here).
    .OUTPUTS
        System.Diagnostics.Process. The started lab, hidden; the caller waits for it.
    #>
    Start-Process -FilePath $alias -ArgumentList ($Arguments + @('--out', $result)) -PassThru -WindowStyle Hidden
}

function Test-Section([string]$Name) {
    <#
    .SYNOPSIS
        Whether a section of the lab is to run: every section without -Only, else the named ones.
    .PARAMETER Name
        The section's name, as -Only spells it.
    .OUTPUTS
        System.Boolean.
    #>
    $Only.Count -eq 0 -or $Only -contains $Name
}

function Get-Package {
    <#
    .SYNOPSIS
        The lab's registered package for this user, or nothing when it is not registered. Reads only.
    .OUTPUTS
        Microsoft.Windows.Appx.PackageManager.Commands.AppxPackage, or nothing.
    #>
    Get-AppxPackage -Name $packageName -ErrorAction SilentlyContinue
}

if ($Unregister) {
    New-Item -ItemType Directory -Path $OutDirectory -Force | Out-Null
    $package = Get-Package
    $family = if ($package) { $package.PackageFamilyName } else { '' }
    if ($package) { $package | Remove-AppxPackage }
    Write-Section 'After Remove-AppxPackage'
    $privateRoot = if ($family) { Join-Path $local "Packages\$family" } else { '' }
    Write-Result @(
        "package data folder still there = $([bool]($privateRoot -and (Test-Path $privateRoot)))"
        "real BC-TEST-MSIX-new = $(Test-Path (Join-Path $local 'BC-TEST-MSIX-new'))"
        "real BC-TEST-MSIX-existing = $(Test-Path (Join-Path $local 'BC-TEST-MSIX-existing'))"
        "real BC-TEST-MSIX-files = $(Test-Path (Join-Path $local 'BC-TEST-MSIX-files'))"
        "real registry values = $(((Get-Item $testKey -ErrorAction SilentlyContinue) | ForEach-Object { $_.Property | Sort-Object }) -join ', ')"
    )
    Remove-Item $testKey, $watchKey -Recurse -Force -ErrorAction SilentlyContinue
    Get-ChildItem $local -Filter 'BC-TEST-MSIX-*' -Directory -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
    return
}

New-Item -ItemType Directory -Path $OutDirectory -Force | Out-Null
Set-Content -Path $result -Value "msix_lab $(Get-Date -Format s) languages=$($Languages -join ',') only=$($Only -join ',')" -Encoding UTF8

# --- Developer Mode, so an unsigned loose package may be registered -------------------------------------------------
$unlock = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock'
if (-not (Test-Path $unlock)) { New-Item -Path $unlock | Out-Null }
Set-ItemProperty -Path $unlock -Name AllowDevelopmentWithoutDevLicense -Value 1 -Type DWord

# --- Fixtures this unpackaged shell makes: they are real by definition ----------------------------------------------
if (-not (Test-Path $testKey)) { New-Item -Path $testKey | Out-Null }
Set-ItemProperty -Path $testKey -Name 'outside' -Value '1'
Set-ItemProperty -Path $testKey -Name 'outside-todelete' -Value '1'
if (-not (Test-Path $watchKey)) { New-Item -Path $watchKey | Out-Null }
$existing = Join-Path $local 'BC-TEST-MSIX-existing'
$doomed = Join-Path $local 'BC-TEST-MSIX-doomed'
New-Item -ItemType Directory -Path $existing, $doomed -Force | Out-Null
foreach ($name in 'existing.txt', 'todelete.txt', 'torename.txt', 'atomic.txt') {
    Set-Content -Path (Join-Path $existing $name) -Value 'BC-TEST made outside' -Encoding ASCII
}
Set-Content -Path (Join-Path $doomed 'file.txt') -Value 'BC-TEST' -Encoding ASCII

Add-Type -AssemblyName System.Security
$entropy = [Text.Encoding]::UTF8.GetBytes('BC-TEST')
$blobOutside = Join-Path $OutDirectory 'dpapi-outside.bin'
[IO.File]::WriteAllBytes($blobOutside, [Security.Cryptography.ProtectedData]::Protect([Text.Encoding]::UTF8.GetBytes('BC-TEST sealed outside the package'), $entropy, 'CurrentUser'))

# --- The package: a signed one installed as the Store would, or this run's languages registered loose ---------------
Get-Package | Remove-AppxPackage
if ($PackagePath) {
    Add-AppxPackage -Path $PackagePath
}
else {
    $manifestPath = Join-Path $LabDirectory 'AppxManifest.xml'
    $manifest = Get-Content $manifestPath -Raw
    $resources = ($Languages | ForEach-Object { "    <Resource Language=`"$_`" />" }) -join "`r`n"
    $manifest = [regex]::Replace($manifest, '(?s)<Resources>.*?</Resources>', "<Resources>`r`n$resources`r`n  </Resources>")
    Set-Content -Path $manifestPath -Value $manifest -Encoding UTF8
    Add-AppxPackage -Register $manifestPath
}

$package = Get-Package
if (-not $package) { throw 'The lab package did not register.' }
$family = $package.PackageFamilyName
Write-Section "Registered $($package.PackageFullName) in $($package.InstallLocation)"

# --- Measurements ----------------------------------------------------------------------------------------------------
if (Test-Section 'identity') { Write-Section 'identity'; Invoke-Lab identity }
if (Test-Section 'apps') { Write-Section 'apps (reads of the real registry)'; Invoke-Lab apps }
if (Test-Section 'registry') {
    Write-Section 'registry in process'
    Invoke-Lab regwrite, 'in-process'
    Invoke-Lab regdelete, 'outside-todelete'
    Invoke-Lab regread
}

if (Test-Section 'children') {
    Write-Section 'children'
    foreach ($mode in 'plain', 'shell', 'policy1', 'policy2', 'policy4', 'explorer', 'shellcom', 'runas', 'schtask') {
        Invoke-Lab child, $mode, $LabDirectory
    }
    Start-Sleep -Seconds 4   # the explorer and shell-automation children are not waited for by the lab
}

if (Test-Section 'selfchild') {
    # A child that is the package's own program: does it keep the package identity (an app restarting itself)?
    Write-Section 'children that are the package''s own program'
    foreach ($mode in 'plain', 'shell', 'policy1', 'policy4') {
        Invoke-Lab selfchild, $mode, $OutDirectory
    }
}

if (Test-Section 'files') { Write-Section 'files in process'; Invoke-Lab files }
if (Test-Section 'filewatch') {
    # File change notifications for folders this shell makes and writes to: one below Roaming AppData, one below Local
    # AppData, one outside AppData (the control).
    $watched = @{
        roaming = Join-Path $env:APPDATA 'BC-TEST-MSIX-watch'
        local   = Join-Path $local 'BC-TEST-MSIX-watch'
        outside = Join-Path $OutDirectory 'BC-TEST-MSIX-watch'
    }
    foreach ($place in 'roaming', 'local', 'outside') {
        $folder = $watched[$place]
        New-Item -ItemType Directory -Path $folder -Force | Out-Null
        Set-Content -Path (Join-Path $folder 'torename.txt') -Value 'BC-TEST' -Encoding ASCII
        Write-Section "file changes in a folder $place of AppData ($folder)"
        $watcher = Start-LabInBackground filewatch, $folder, '10'
        Start-Sleep -Seconds 4
        Set-Content -Path (Join-Path $folder 'new.txt') -Value 'BC-TEST made outside' -Encoding ASCII
        Add-Content -Path (Join-Path $folder 'new.txt') -Value 'BC-TEST appended outside' -Encoding ASCII
        Rename-Item -Path (Join-Path $folder 'torename.txt') -NewName 'renamed.txt'
        $watcher.WaitForExit(25000) | Out-Null
        Remove-Item -Path $folder -Recurse -Force -ErrorAction SilentlyContinue
    }
}

if (Test-Section 'dpapi') {
    Write-Section 'dpapi'
    Invoke-Lab dpapi, $blobOutside
    $blobInside = Join-Path $OutDirectory 'dpapi-inside.bin'
    Invoke-Lab 'dpapi-make', $blobInside
    if (Test-Path $blobInside) {
        $plain = [Text.Encoding]::UTF8.GetString([Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($blobInside), $entropy, 'CurrentUser'))
        Write-Result "dpapi.unprotectedOutside = $plain"
    }
}

if (Test-Section 'objects') {
    Write-Section 'named objects: the package holds, this shell opens'
    $holder = Start-LabInBackground 'mutex', 'BC-TEST-MSIX-Mutex', '15'
    Start-Sleep -Seconds 3
    $opened = $null
    $visible = [Threading.Mutex]::TryOpenExisting('Local\BC-TEST-MSIX-Mutex', [ref]$opened)
    if ($opened) { $opened.Dispose() }
    $signal = $null
    $eventVisible = [Threading.EventWaitHandle]::TryOpenExisting('Local\BC-TEST-MSIX-Mutex.Event', [ref]$signal)
    if ($signal) { [void]$signal.Set(); $signal.Dispose() }
    $holder.WaitForExit(20000) | Out-Null
    Write-Result "outside.mutexVisible = $visible", "outside.eventVisible = $eventVisible"

    Write-Section 'named objects: this shell holds, the package opens'
    $created = $false
    $mine = New-Object Threading.Mutex($true, 'Local\BC-TEST-MSIX-Mutex2', [ref]$created)
    $mineEvent = New-Object Threading.EventWaitHandle($false, 'AutoReset', 'Local\BC-TEST-MSIX-Mutex2.Event')
    Invoke-Lab 'mutex-open', 'BC-TEST-MSIX-Mutex2'
    Write-Result "outside.eventSignalledByPackage = $($mineEvent.WaitOne(1000))"
    $mine.ReleaseMutex(); $mine.Dispose(); $mineEvent.Dispose()
}

if (Test-Section 'notify') {
    # Three ways to learn that an outside process wrote to a key: a notification on HKCU, one on HKEY_USERS\<SID>,
    # and polling the key's last-write time.
    foreach ($command in 'regnotify', 'regnotify-users', 'regpoll') {
        Write-Section "registry change: $command"
        $watcher = Start-LabInBackground $command, '12'
        Start-Sleep -Seconds 4
        Set-ItemProperty -Path $watchKey -Name "written-outside-$command" -Value (Get-Date -Format o)
        $watcher.WaitForExit(20000) | Out-Null
    }
}

if (Test-Section 'languages') {
    Write-Section 'languages'
    Write-Result "outside.userLanguageList = $((Get-WinUserLanguageList | ForEach-Object { $_.LanguageTag }) -join ', ')", "outside.uiCulture = $((Get-UICulture).Name)"
    Invoke-Lab lang
}

if (Test-Section 'cliphistory') {
    Write-Section 'clipboard history'
    Write-Result "outside.EnableClipboardHistory = $((Get-ItemProperty 'HKCU:\Software\Microsoft\Clipboard' -ErrorAction SilentlyContinue).EnableClipboardHistory)"
    Invoke-Lab cliphistory
}

if (Test-Section 'startup') { Write-Section 'startup task'; Invoke-Lab startup, 'MsixLabStartup' }

# --- What this unpackaged shell sees: the truth about where things landed -------------------------------------------
Write-Section 'OUTSIDE VIEW'
$privateLocal = Join-Path $local "Packages\$family\LocalCache\Local"
Write-Result @(
    "real registry values = $(((Get-Item $testKey).Property | Sort-Object) -join ', ')"
    "real BC-TEST-MSIX-new exists = $(Test-Path (Join-Path $local 'BC-TEST-MSIX-new'))"
    "real BC-TEST-MSIX-existing files = $((Get-ChildItem $existing -ErrorAction SilentlyContinue | Sort-Object Name | ForEach-Object { $_.Name }) -join ', ')"
    "real existing.txt = $(Get-Content (Join-Path $existing 'existing.txt') -ErrorAction SilentlyContinue)"
    "real atomic.txt = $(Get-Content (Join-Path $existing 'atomic.txt') -ErrorAction SilentlyContinue)"
    "real BC-TEST-MSIX-doomed exists = $(Test-Path $doomed)"
    "real BC-TEST-MSIX-files = $((Get-ChildItem (Join-Path $local 'BC-TEST-MSIX-files') -ErrorAction SilentlyContinue | Sort-Object Name | ForEach-Object { $_.Name }) -join ', ')"
    "real temp file exists = $(Test-Path (Join-Path $env:TEMP 'BC-TEST-MSIX-temp.txt'))"
    "private LocalCache\Local entries = $((Get-ChildItem $privateLocal -Recurse -File -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName.Substring($privateLocal.Length + 1) } | Sort-Object) -join ', ')"
    "LocalState files = $((Get-ChildItem (Join-Path $local "Packages\$family\LocalState") -ErrorAction SilentlyContinue | ForEach-Object { $_.Name }) -join ', ')"
)
Write-Host "`nResult file: $result"
