<#
.SYNOPSIS
    End-to-end test of install.ps1's install for every user of a PC, on a disposable Windows machine: real installs
    and uninstalls from a packaged release zip, as the signed-in administrator with -AllUsers and as SYSTEM, which
    must pick the install for every user by itself.

.DESCRIPTION
    The installer under test is the install.ps1 inside the zip, as it ships. Three passes:

      1. As this account, which owns the desktop: install.ps1 -AllUsers -Package <zip> -NoLaunch -AddToPath.
         Checked: exit code 0; the app in Program Files\BetterClipboard with the package's version; the folder's rights
         protected (Administrators and SYSTEM full control, Users read and run, nothing inherited, no ordinary account
         able to write); the "Start with Windows" entry for every user ('"<exe>" --background --all-users-startup')
         and none in this user's own Run key; the PC's Installed-apps entry, whose uninstall command has -AllUsers;
         the Start menu shortcut for every user and none of this user's own; the folder on the PATH of every user and
         not on this user's; Win+V taken over for this user (Explorer's DisabledHotkeys gets V); every other startup
         entry of the user and of the PC kept. Then install.ps1 -Uninstall -AllUsers: all of it gone, Win+V given
         back, the neighbors still there.
      2. As SYSTEM (a scheduled task), with -Hotkey: refused, since a shortcut is one person's and SYSTEM is nobody
         who uses the PC; nothing installed.
      3. As SYSTEM, without -AllUsers: the install for every user all the same (the folder, the entry for every user,
         the PC's Installed-apps entry, the shortcut for every user), nothing of SYSTEM's own (no Run value in its
         registry), this user's DisabledHotkeys untouched and Explorer not restarted (its process id unchanged). Then
         install.ps1 -Uninstall as SYSTEM: everything gone again.

    It changes the machine - Program Files, the PC's Run key, Installed-apps entries and PATH, every user's Start menu,
    this user's Explorer settings (put back), a scheduled task (removed), Explorer restarts - so it refuses to run
    unless the CI variable is "true" (GitHub sets it) or -ConfirmMachineChanges is given: use a VM, never a machine
    someone works on.

.PARAMETER Version
    The packaged version, as in the zip's file name.

.PARAMETER Architecture
    Which package to test: x64 or arm64. It must run on this PC.

.PARAMETER PackageDirectory
    Folder with BetterClipboard-<version>-win-<arch>.zip and SHA256SUMS.txt (tools/release/package.ps1's output).

.PARAMETER ConfirmMachineChanges
    Run outside CI anyway (a disposable VM).

.EXAMPLE
    pwsh tools/release/test-all-users.ps1 -Version 0.0.0-ci -PackageDirectory artifacts/ci

    In CI after package.ps1 -Version 0.0.0-ci -Architectures x64 -OutputDirectory artifacts/ci.

.NOTES
    Needs administrator rights. Exit code 0 = every check passed, 1 = a check failed; throws when it cannot run (not
    elevated, no package, a copy for every user already installed).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $Version,

    [ValidateSet('x64', 'arm64')]
    [string] $Architecture = 'x64',

    [string] $PackageDirectory = 'artifacts/release',

    [switch] $ConfirmMachineChanges
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$packages = [IO.Path]::GetFullPath([IO.Path]::Combine($root, $PackageDirectory))
$zipName = "BetterClipboard-$Version-win-$Architecture.zip"
$zip = Join-Path $packages $zipName
$results = New-Object System.Collections.Generic.List[object]
$work = Join-Path ([IO.Path]::GetTempPath()) "betterclipboard-allusers-test-$([Guid]::NewGuid().ToString('N').Substring(0, 8))"
$programFiles = if ($env:ProgramW6432) { $env:ProgramW6432 } else { $env:ProgramFiles }
$installDir = Join-Path $programFiles 'BetterClipboard'
$exe = Join-Path $installDir 'BetterClipboard.exe'
$userRunKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$machineRunKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run'
$machineEntryKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\BetterClipboard'
$advancedKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced'
$allUsersShortcut = Join-Path ([Environment]::GetFolderPath('CommonPrograms')) 'BetterClipboard.lnk'
$userShortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'BetterClipboard.lnk'
$taskName = 'BetterClipboard-AllUsers-Test'
$neighborName = 'BC-TEST neighbor'

function Add-Check([string] $Step, [string] $What, [bool] $Passed, [string] $Detail = '') {
    <#
    .SYNOPSIS
        Records one check for the summary and prints it at once, so a CI log shows progress.
    .PARAMETER Step
        Which pass and step it belongs to.
    .PARAMETER What
        What must hold, in words.
    .PARAMETER Passed
        Whether it held.
    .PARAMETER Detail
        Observed value or explanation, printed next to the result.
    #>
    $results.Add([pscustomobject]@{ Step = $Step; What = $What; Passed = $Passed; Detail = $Detail })
    $mark = if ($Passed) { 'ok  ' } else { 'FAIL' }
    Write-Host ("{0} {1,-16} {2}{3}" -f $mark, $Step, $What, $(if ($Detail) { " ($Detail)" } else { '' }))
}

function Get-Value([string] $Key, [string] $Name) {
    <#
    .SYNOPSIS
        A registry value as text, or '' when the key or the value is missing.
    .PARAMETER Key
        A registry provider path (this test runs in a 64-bit PowerShell, so HKLM:\SOFTWARE is the 64-bit view).
    .PARAMETER Name
        The value's name.
    .OUTPUTS
        System.String.
    #>
    $item = Get-ItemProperty -Path $Key -ErrorAction SilentlyContinue
    if ($item -and $item.PSObject.Properties[$Name]) { return [string] $item.$Name }
    return ''
}

function Get-ValueNames([string] $Key) {
    <#
    .SYNOPSIS
        The names of a key's values, to check afterwards that an install or uninstall changed only its own.
    .PARAMETER Key
        A registry provider path.
    .OUTPUTS
        System.String[] (none when the key does not exist).
    #>
    if (-not (Test-Path -LiteralPath $Key)) { return @() }
    return @((Get-Item -LiteralPath $Key).GetValueNames())
}

function Get-ShortcutTarget([string] $Path) {
    <#
    .SYNOPSIS
        The target of a shortcut, or '' when there is none.
    .PARAMETER Path
        The .lnk file.
    .OUTPUTS
        System.String.
    #>
    if (-not (Test-Path -LiteralPath $Path)) { return '' }
    return [string] (New-Object -ComObject WScript.Shell).CreateShortcut($Path).TargetPath
}

function Test-OnPath([string] $Scope) {
    <#
    .SYNOPSIS
        Whether the install folder is on the PATH of every user ('Machine') or of this user ('User').
    .PARAMETER Scope
        Machine or User.
    .OUTPUTS
        System.Boolean.
    #>
    $entries = @(([string] [Environment]::GetEnvironmentVariable('Path', $Scope)) -split ';' | ForEach-Object { $_.Trim().TrimEnd('\') })
    return $entries -contains $installDir
}

function Get-DisabledHotkeys {
    <#
    .SYNOPSIS
        Explorer's DisabledHotkeys for this user, or '' when it is not set.
    .OUTPUTS
        System.String.
    #>
    return Get-Value $advancedKey 'DisabledHotkeys'
}

function Invoke-Installer([string] $Name, [string[]] $Arguments) {
    <#
    .SYNOPSIS
        Runs the packaged install.ps1 in Windows PowerShell 5.1 as this account (like a user who downloaded it), its
        output into <work>\<Name>.log, and returns the exit code.
    .PARAMETER Name
        Log file name.
    .PARAMETER Arguments
        The installer's arguments; each with a space is quoted.
    .OUTPUTS
        System.Int32.
    #>
    $log = Join-Path $work "$Name.log"
    $quoted = @($Arguments | ForEach-Object { if ($_ -match '\s') { "`"$_`"" } else { $_ } })
    $powershell = Join-Path ([Environment]::SystemDirectory) 'WindowsPowerShell\v1.0\powershell.exe'
    $process = Start-Process -FilePath $powershell -ArgumentList (@('-NoLogo', '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', "`"$script`"") + $quoted) `
        -NoNewWindow -PassThru -RedirectStandardOutput $log -RedirectStandardError "$log.err"
    $null = $process.Handle
    if (-not $process.WaitForExit(600000)) {
        $process.Kill()
        throw "The installer ($Name) did not finish within 10 minutes."
    }

    if ($process.ExitCode -ne 0) {
        Write-Host "---- last lines of $log ----"
        Get-Content -LiteralPath $log, "$log.err" -Tail 30 -ErrorAction SilentlyContinue | Out-Host
    }

    return $process.ExitCode
}

function Invoke-InstallerAsSystem([string] $Name, [string] $Arguments) {
    <#
    .SYNOPSIS
        Runs the packaged install.ps1 as SYSTEM through a scheduled task - how Intune and RMM agents run installers -
        and returns its exit code; the output goes to <work>\<Name>.log.
    .DESCRIPTION
        The task runs cmd.exe, which redirects Windows PowerShell's output into the log; the task's last result is the
        installer's exit code. The task is removed again whatever happens.
    .PARAMETER Name
        Log file name.
    .PARAMETER Arguments
        The installer's arguments as one command-line string.
    .OUTPUTS
        System.Int32.
    #>
    $log = Join-Path $work "$Name.log"
    $powershell = Join-Path ([Environment]::SystemDirectory) 'WindowsPowerShell\v1.0\powershell.exe'
    $command = "/c `"`"$powershell`" -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$script`" $Arguments > `"$log`" 2>&1`""
    $action = New-ScheduledTaskAction -Execute (Join-Path ([Environment]::SystemDirectory) 'cmd.exe') -Argument $command
    # "SYSTEM" resolves on every display language; "NT AUTHORITY\SYSTEM" does not.
    $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
    Register-ScheduledTask -TaskName $taskName -Action $action -Principal $principal -Force | Out-Null
    try {
        Start-ScheduledTask -TaskName $taskName
        $deadline = (Get-Date).AddMinutes(10)
        # Until the task has run and ended: 267011 = has not run yet, 267009 = still running (SCHED_S_TASK_*).
        do {
            Start-Sleep -Seconds 2
            $task = Get-ScheduledTask -TaskName $taskName
            $info = Get-ScheduledTaskInfo -TaskName $taskName
            $pending = $task.State -in @('Running', 'Queued') -or $info.LastTaskResult -in @(267011, 267009)
        } while ($pending -and (Get-Date) -lt $deadline)

        if ($pending) {
            throw "The installer as SYSTEM ($Name) did not finish within 10 minutes."
        }

        $code = $info.LastTaskResult
        if ($code -ne 0) {
            Write-Host "---- last lines of $log ----"
            Get-Content -LiteralPath $log -Tail 30 -ErrorAction SilentlyContinue | Out-Host
        }

        return [int] $code
    }
    finally {
        Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
    }
}

function Test-FolderRights {
    <#
    .SYNOPSIS
        Whether the install folder's rights are the protected set: nothing inherited, Administrators and SYSTEM full
        control, and no account but those two allowed to write, delete or change permissions.
    .OUTPUTS
        PSCustomObject with Passed (System.Boolean) and Detail (System.String).
    #>
    $acl = Get-Acl -LiteralPath $installDir
    # Rules with SIDs straight away: "ALL APPLICATION PACKAGES" cannot be translated back from its name.
    $rules = @($acl.GetAccessRules($true, $true, [System.Security.Principal.SecurityIdentifier]))
    $writeRights = [System.Security.AccessControl.FileSystemRights] 'Write, AppendData, CreateFiles, CreateDirectories, Delete, DeleteSubdirectoriesAndFiles, ChangePermissions, TakeOwnership'
    $privileged = @('S-1-5-32-544', 'S-1-5-18')
    $writers = @($rules | Where-Object {
            $_.AccessControlType -eq 'Allow' -and ($_.FileSystemRights -band $writeRights) -ne 0 -and $privileged -notcontains $_.IdentityReference.Value
        } | ForEach-Object { $_.IdentityReference.Value })
    $users = @($rules | Where-Object { $_.IdentityReference.Value -eq 'S-1-5-32-545' })
    $passed = $acl.AreAccessRulesProtected -and $writers.Count -eq 0 -and $users.Count -gt 0 -and @($rules | Where-Object { $_.IsInherited }).Count -eq 0
    return [pscustomobject]@{ Passed = $passed; Detail = "protected: $($acl.AreAccessRulesProtected); other writers: $($writers -join ', '); Users entries: $($users.Count)" }
}

if ($env:CI -ne 'true' -and -not $ConfirmMachineChanges) {
    throw 'This test installs and uninstalls BetterClipboard for every user of this PC and restarts Explorer. It runs in CI; elsewhere only on a disposable VM, with -ConfirmMachineChanges.'
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not (New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run elevated: an install for every user needs an administrator.'
}

if (-not (Test-Path -LiteralPath $zip -PathType Leaf)) {
    throw "No $zip; run package.ps1 first."
}

if ((Get-Value $machineEntryKey 'InstallLocation') -or (Test-Path -LiteralPath $installDir)) {
    throw "BetterClipboard is already installed for every user ($installDir); the test needs a machine without it."
}

New-Item -ItemType Directory -Path $work | Out-Null
# The installer as it ships: the one in the zip's root.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($zip)
try {
    $entry = $archive.GetEntry('install.ps1')
    if (-not $entry) { throw "$zipName has no install.ps1 at its root." }
    $script = Join-Path $work 'install.ps1'
    [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $script)
}
finally {
    $archive.Dispose()
}

$originalHotkeys = Get-DisabledHotkeys
try {
    Write-Host "Testing install.ps1 -AllUsers with $zipName (as $($identity.Name))"
    foreach ($key in @($userRunKey, $machineRunKey)) {
        if (-not (Test-Path -LiteralPath $key)) { New-Item -Path $key -Force | Out-Null }
        Set-ItemProperty -Path $key -Name $neighborName -Value "`"$env:SystemRoot\System32\BC-TEST-neighbor-does-not-exist.exe`"" -Type String
    }

    $userRunBefore = Get-ValueNames $userRunKey
    $machineRunBefore = Get-ValueNames $machineRunKey
    $userPathBefore = [Environment]::GetEnvironmentVariable('Path', 'User')

    # 1. As the administrator who owns the desktop, asking for it.
    $code = Invoke-Installer 'admin-install' @('-AllUsers', '-Package', $zip, '-NoLaunch', '-AddToPath')
    Add-Check 'admin install' 'exit code 0' ($code -eq 0) "$code"
    $productVersion = if (Test-Path -LiteralPath $exe) { (Get-Item -LiteralPath $exe).VersionInfo.ProductVersion } else { '' }
    Add-Check 'admin install' 'the app in Program Files with the package''s version' ($productVersion -like "$Version*") "$installDir, $productVersion"
    $rights = if (Test-Path -LiteralPath $installDir) { Test-FolderRights } else { [pscustomobject]@{ Passed = $false; Detail = 'no folder' } }
    Add-Check 'admin install' 'no ordinary account may change the folder' $rights.Passed $rights.Detail
    Add-Check 'admin install' '"Start with Windows" for every user' ((Get-Value $machineRunKey 'BetterClipboard') -eq "`"$exe`" --background --all-users-startup") (Get-Value $machineRunKey 'BetterClipboard')
    Add-Check 'admin install' 'no "Start with Windows" entry of this user''s own' (-not (Get-Value $userRunKey 'BetterClipboard').Contains($exe))
    $uninstallCommand = Get-Value $machineEntryKey 'UninstallString'
    Add-Check 'admin install' 'the PC''s Installed-apps entry, uninstalling with -AllUsers' ((Get-Value $machineEntryKey 'InstallLocation') -eq $installDir -and $uninstallCommand -match '-Uninstall -AllUsers') $uninstallCommand
    Add-Check 'admin install' 'the Start menu shortcut for every user' ((Get-ShortcutTarget $allUsersShortcut) -eq $exe) $allUsersShortcut
    Add-Check 'admin install' 'no Start menu shortcut of this user''s own' ((Get-ShortcutTarget $userShortcut) -ne $exe)
    Add-Check 'admin install' 'on the PATH of every user, not on this user''s' ((Test-OnPath 'Machine') -and [Environment]::GetEnvironmentVariable('Path', 'User') -eq $userPathBefore)
    Add-Check 'admin install' 'Win+V taken over for this user' ((Get-DisabledHotkeys).ToUpperInvariant().Contains('V')) "DisabledHotkeys '$(Get-DisabledHotkeys)'"
    $lost = @($userRunBefore | Where-Object { $_ -ne 'BetterClipboard' -and (Get-ValueNames $userRunKey) -notcontains $_ }) + @($machineRunBefore | Where-Object { $_ -ne 'BetterClipboard' -and (Get-ValueNames $machineRunKey) -notcontains $_ })
    Add-Check 'admin install' 'every other startup entry of the user and the PC kept' ($lost.Count -eq 0) ($lost -join ', ')

    $code = Invoke-Installer 'admin-uninstall' @('-Uninstall', '-AllUsers', '-InstallDir', $installDir)
    Add-Check 'admin uninstall' 'exit code 0' ($code -eq 0) "$code"
    Add-Check 'admin uninstall' 'the folder removed' (-not (Test-Path -LiteralPath $installDir))
    Add-Check 'admin uninstall' 'the entry for every user removed' (-not (Get-Value $machineRunKey 'BetterClipboard'))
    Add-Check 'admin uninstall' 'the PC''s Installed-apps entry removed' (-not (Test-Path -LiteralPath $machineEntryKey))
    Add-Check 'admin uninstall' 'the shortcut for every user removed' (-not (Test-Path -LiteralPath $allUsersShortcut))
    Add-Check 'admin uninstall' 'off the PATH of every user' (-not (Test-OnPath 'Machine'))
    Add-Check 'admin uninstall' 'Win+V given back to Windows' (-not (Get-DisabledHotkeys).ToUpperInvariant().Contains('V')) "DisabledHotkeys '$(Get-DisabledHotkeys)'"
    Add-Check 'admin uninstall' 'the neighbors still there' ((Get-ValueNames $userRunKey) -contains $neighborName -and (Get-ValueNames $machineRunKey) -contains $neighborName)

    # 2. As SYSTEM, with an option that sets one person's shortcuts: refused before anything changes.
    $hotkeysBefore = Get-DisabledHotkeys
    $code = Invoke-InstallerAsSystem 'system-hotkey' "-Package `"$zip`" -NoLaunch -Hotkey Ctrl+Alt+F9"
    $log = Join-Path $work 'system-hotkey.log'
    # For this reason and no other: a run that fails earlier (an empty Start menu path stopped every run as SYSTEM until
    # 2026-10-18) must not pass as a refusal.
    # Whitespace folded: PowerShell wraps an error at the width of the console it thinks it has.
    $refusedForTheShortcut = (Test-Path -LiteralPath $log) -and (((Get-Content -LiteralPath $log -Raw) -replace '\s+', ' ') -match 'set the shortcuts of the account that runs the installer')
    Add-Check 'system -Hotkey' 'refused, because a shortcut is one person''s' ($code -ne 0 -and $refusedForTheShortcut) "$code"
    Add-Check 'system -Hotkey' 'nothing installed' (-not (Test-Path -LiteralPath $installDir) -and -not (Test-Path -LiteralPath $machineEntryKey))

    # 3. As SYSTEM without -AllUsers: the install for every user all the same, and nothing of anyone's own changed.
    $explorerBefore = @(Get-Process -Name explorer -ErrorAction SilentlyContinue | ForEach-Object { $_.Id } | Sort-Object)
    $code = Invoke-InstallerAsSystem 'system-install' "-Package `"$zip`" -NoLaunch"
    Add-Check 'system install' 'exit code 0' ($code -eq 0) "$code"
    Add-Check 'system install' 'the app in Program Files' (Test-Path -LiteralPath $exe)
    Add-Check 'system install' '"Start with Windows" for every user' ((Get-Value $machineRunKey 'BetterClipboard') -eq "`"$exe`" --background --all-users-startup")
    Add-Check 'system install' 'the PC''s Installed-apps entry' ((Get-Value $machineEntryKey 'InstallLocation') -eq $installDir)
    Add-Check 'system install' 'the Start menu shortcut for every user' ((Get-ShortcutTarget $allUsersShortcut) -eq $exe)
    Add-Check 'system install' 'no "Start with Windows" entry in SYSTEM''s own registry' (-not (Get-Value 'Registry::HKEY_USERS\S-1-5-18\Software\Microsoft\Windows\CurrentVersion\Run' 'BetterClipboard'))
    Add-Check 'system install' 'this user''s Explorer shortcuts untouched' ((Get-DisabledHotkeys) -ceq $hotkeysBefore) "'$(Get-DisabledHotkeys)'"
    $explorerAfter = @(Get-Process -Name explorer -ErrorAction SilentlyContinue | ForEach-Object { $_.Id } | Sort-Object)
    Add-Check 'system install' 'Explorer not restarted' (($explorerBefore -join ',') -eq ($explorerAfter -join ',')) "$($explorerBefore -join ',') -> $($explorerAfter -join ',')"
    $log = Join-Path $work 'system-install.log'
    Add-Check 'system install' 'the log says why it is for every user' ((Test-Path -LiteralPath $log) -and (((Get-Content -LiteralPath $log -Raw) -replace '\s+', ' ') -match 'runs as SYSTEM'))

    $code = Invoke-InstallerAsSystem 'system-uninstall' "-Uninstall -InstallDir `"$installDir`""
    Add-Check 'system uninstall' 'exit code 0' ($code -eq 0) "$code"
    Add-Check 'system uninstall' 'the folder removed' (-not (Test-Path -LiteralPath $installDir))
    Add-Check 'system uninstall' 'the entry for every user removed' (-not (Get-Value $machineRunKey 'BetterClipboard'))
    Add-Check 'system uninstall' 'the PC''s Installed-apps entry removed' (-not (Test-Path -LiteralPath $machineEntryKey))
    Add-Check 'system uninstall' 'the shortcut for every user removed' (-not (Test-Path -LiteralPath $allUsersShortcut))
    Add-Check 'system uninstall' 'the neighbors still there' ((Get-ValueNames $userRunKey) -contains $neighborName -and (Get-ValueNames $machineRunKey) -contains $neighborName)
}
finally {
    # Whatever happened: no test copy left running or installed, and what the test seeded or changed put back.
    Get-Process -Name BetterClipboard -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($installDir, [StringComparison]::OrdinalIgnoreCase) } |
        Stop-Process -Force -ErrorAction SilentlyContinue
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
    foreach ($key in @($userRunKey, $machineRunKey)) {
        try { Remove-ItemProperty -Path $key -Name $neighborName -ErrorAction Stop } catch { Write-Host "cleanup: $($_.Exception.Message)" }
    }

    if ((Get-Value $machineRunKey 'BetterClipboard').Contains($installDir)) { Remove-ItemProperty -Path $machineRunKey -Name 'BetterClipboard' -ErrorAction SilentlyContinue }
    if ((Get-Value $machineEntryKey 'InstallLocation') -eq $installDir) { Remove-Item -LiteralPath $machineEntryKey -Recurse -ErrorAction SilentlyContinue }
    if ((Get-ShortcutTarget $allUsersShortcut) -eq $exe) { Remove-Item -LiteralPath $allUsersShortcut -ErrorAction SilentlyContinue }
    if (Test-Path -LiteralPath $installDir) { Remove-Item -LiteralPath $installDir -Recurse -Force -ErrorAction SilentlyContinue }
    try {
        if ($originalHotkeys) {
            Set-ItemProperty -Path $advancedKey -Name DisabledHotkeys -Value $originalHotkeys -Type String -ErrorAction Stop
        }
        else {
            Remove-ItemProperty -Path $advancedKey -Name DisabledHotkeys -ErrorAction SilentlyContinue
        }
    }
    catch {
        Write-Host "cleanup: $($_.Exception.Message)"
    }
}

$failed = @($results | Where-Object { -not $_.Passed })
Write-Host ("{0} checks, {1} failed. Logs: {2}" -f $results.Count, $failed.Count, $work)
exit $(if ($failed.Count -gt 0) { 1 } else { 0 })
