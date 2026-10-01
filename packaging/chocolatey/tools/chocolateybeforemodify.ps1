<#
.SYNOPSIS
    Chocolatey before-modify script of BetterClipboard: closes the app before an upgrade or an uninstall.

.DESCRIPTION
    Chocolatey runs the INSTALLED version's copy of this script before "choco upgrade" and "choco uninstall".
    An app left running would not block the upgrade - Chocolatey moves the package folder aside, a running exe
    included, and reports success - but the old version would keep running from that backup folder, which then
    cannot be deleted (docs/chocolatey.md section 3.3). So every BetterClipboard.exe started from this package
    is closed first:
      - gracefully with "BetterClipboard.exe --exit" (it saves the copies it has queued) when that cannot reach
        anything else: the command signals the single instance of this Windows session, so it is sent only when
        this package's copy is the only BetterClipboard running in the session;
      - otherwise, or if it is still running after 15 seconds, by stopping the process.
    Then it writes the state file the next install script reads: "running" when it closed the app in this session
    (the upgrade starts it again), "stopped" otherwise. Its presence also marks the next install as an upgrade.

    It never fails the operation: a copy it cannot close (another user's, without administrator rights) or any
    other problem is reported as a warning, and the state file is written regardless.
#>
$ErrorActionPreference = 'Stop'
$toolsDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
. (Join-Path $toolsDir 'helpers.ps1')
$exe = Join-Path (Join-Path $toolsDir $BetterClipboardAppFolderName) 'BetterClipboard.exe'
$state = 'stopped'

try {
    $session = (Get-Process -Id $PID).SessionId
    $ours = @(Get-BetterClipboardProcess -Exe $exe)
    # Ids, not objects: two Get-Process calls return different objects for the same process.
    $ourIds = @($ours | ForEach-Object { $_.Id })
    $oursHere = @($ours | Where-Object { $_.SessionId -eq $session })
    $othersHere = @(Get-BetterClipboardProcess | Where-Object { $_.SessionId -eq $session -and $ourIds -notcontains $_.Id })
    if ($oursHere.Count -gt 0) {
        $state = 'running'
    }

    if ($oursHere.Count -gt 0 -and $othersHere.Count -eq 0) {
        Write-Host 'Closing BetterClipboard (it saves queued copies first).'
        try {
            # Piping to Out-Null makes PowerShell wait for the GUI process, which only signals and exits.
            & $exe --exit | Out-Null
        }
        finally {
            $global:LASTEXITCODE = 0
        }

        $deadline = (Get-Date).AddSeconds(15)
        while ((Get-Date) -lt $deadline -and @($ours | Where-Object { -not $_.HasExited }).Count -gt 0) {
            Start-Sleep -Milliseconds 250
        }
    }

    foreach ($process in @($ours | Where-Object { -not $_.HasExited })) {
        try {
            Write-Host "Stopping BetterClipboard (process $($process.Id)): it could not be asked to close without reaching another copy, or did not close in time."
            Stop-Process -Id $process.Id -Force
            $process.WaitForExit(5000) | Out-Null
        }
        catch {
            Write-Warning "BetterClipboard (process $($process.Id), session $($process.SessionId)) could not be closed: $($_.Exception.Message). It keeps running the old version until it exits."
        }
    }
}
catch {
    Write-Warning "Could not close BetterClipboard before the change: $($_.Exception.Message)"
}

Set-Content -LiteralPath (Join-Path $toolsDir $BetterClipboardStateFileName) -Value $state -Encoding ASCII
