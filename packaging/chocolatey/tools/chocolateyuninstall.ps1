<#
.SYNOPSIS
    Chocolatey uninstall script of BetterClipboard: undoes what the install script set up, keeps the history.

.DESCRIPTION
    Runs on "choco uninstall", after the installed version's chocolateyBeforeModify.ps1 closed the app. Chocolatey
    itself deletes the shims and the files it recorded at install; this script removes the rest:
      - the state file before-modify just wrote: Chocolatey deletes only the files it recorded right after the
        install, and this one would keep the package folder alive (docs/chocolatey.md section 3.3);
      - the Start menu shortcut, from every user's and from this user's Start menu, if it points into this package
        (install.ps1's shortcut points elsewhere and stays);
      - this user's "Start with Windows" entry, if it points into this package: removed, or, while install.ps1's copy
        stays installed, handed to that copy (both share one history and one settings file, so the user's choice to
        start with Windows carries over);
      - the shortcuts released from Explorer for BetterClipboard: Win+V and every Win+letter or Win+digit shortcut in
        its settings that Explorer's DisabledHotkeys lists (its Settings and install.ps1 release them there). Without
        the app they would do nothing at all, so they go back to Windows and Explorer restarts - unless
        /KeepWinVReleased is given or install.ps1's copy of BetterClipboard remains to use them. The user's other
        letters in DisabledHotkeys stay, as with install.ps1 -Uninstall.
    The history, settings and logs in %LOCALAPPDATA%\BetterClipboard are kept, as with install.ps1 -Uninstall.

    Package parameter (choco uninstall betterclipboard --params "'/KeepWinVReleased'"):
      /KeepWinVReleased  leave Explorer's DisabledHotkeys as it is
#>
$ErrorActionPreference = 'Stop'
$toolsDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
. (Join-Path $toolsDir 'helpers.ps1')
$appDir = Join-Path $toolsDir $BetterClipboardAppFolderName
$pp = Get-PackageParameters

Remove-Item -LiteralPath (Join-Path $toolsDir $BetterClipboardStateFileName) -Force -ErrorAction SilentlyContinue

foreach ($programs in @([Environment]::GetFolderPath('CommonPrograms'), [Environment]::GetFolderPath('Programs'))) {
    # An account without a profile folder gets '' here, which Join-Path refuses.
    if (-not $programs) {
        continue
    }

    # Only a shortcut into this package: install.ps1's has the same name in the user's Start menu. One whose
    # target cannot be read counts as not ours.
    $link = Join-Path $programs $BetterClipboardShortcutName
    if ((Get-BetterClipboardShortcutTarget $link).StartsWith($appDir, [StringComparison]::OrdinalIgnoreCase)) {
        try {
            Remove-Item -LiteralPath $link -Force
        }
        catch {
            Write-Warning "Could not remove the shortcut '$link': $($_.Exception.Message)"
        }
    }
}

# install.ps1's copy, when it stays installed: it shares the history, the settings and the one "Start with Windows" value.
$otherCopy = Get-BetterClipboardOtherInstall
if ((Get-BetterClipboardRunTarget).StartsWith($appDir, [StringComparison]::OrdinalIgnoreCase)) {
    if ($otherCopy) {
        # The user wanted BetterClipboard to start with Windows: the copy that stays takes the value over, in the app's
        # own format, instead of nothing starting at the next sign-in.
        Set-ItemProperty -Path $BetterClipboardRunKey -Name $BetterClipboardRunValue -Value "`"$otherCopy`" --background" -Type String
        Write-Host "'Start with Windows' now starts the copy of BetterClipboard at '$otherCopy'."
    }
    else {
        Remove-ItemProperty -Path $BetterClipboardRunKey -Name $BetterClipboardRunValue
    }
}

# Assigned, not wrapped in @(): both functions return their array as one object, and @() would nest it.
$configured = Get-BetterClipboardConfiguredHotkeys
$keys = Get-BetterClipboardKeysToGiveBack -DisabledHotkeys (Get-BetterClipboardDisabledHotkeys) -Configured $configured
if ($keys.Count -gt 0) {
    $names = Format-BetterClipboardKeyNames $keys
    $verb = if ($keys.Count -eq 1) { 'stays' } else { 'stay' }
    if ($pp.KeepWinVReleased) {
        Write-Host "$names $verb released from Explorer (/KeepWinVReleased)."
    }
    elseif ($otherCopy) {
        Write-Host "$names $verb released from Explorer: the copy of BetterClipboard at '$otherCopy' still uses $(if ($keys.Count -eq 1) { 'it' } else { 'them' })."
    }
    else {
        if ($null -eq $configured) {
            Write-Host "BetterClipboard's settings could not be read, so only Win+V is given back; other Win+ shortcuts released for it stay released."
        }

        # A failed write is reported, never fatal: the app is already gone, and the user can edit the value by hand.
        $changed = $false
        try {
            $changed = Remove-BetterClipboardDisabledHotkeys -Keys $keys
        }
        catch {
            Write-Warning "Could not give $names back to Windows: $($_.Exception.Message)"
        }

        if ($changed) {
            if (Test-BetterClipboardDesktopUser) {
                Write-Host "Giving $names back to Windows: restarting Explorer (the taskbar blinks once)."
                Restart-BetterClipboardDesktopShell
            }
            else {
                Write-Host "$names $(if ($keys.Count -eq 1) { 'goes' } else { 'go' }) back to Windows at the next sign-in."
            }
        }
    }
}

Write-Host 'BetterClipboard was uninstalled. Your history was kept in %LOCALAPPDATA%\BetterClipboard; delete that folder to remove it.'
