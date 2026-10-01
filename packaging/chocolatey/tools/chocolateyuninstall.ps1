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
      - this user's "Start with Windows" entry, if it points into this package;
      - Win+V's release: when BetterClipboard's setting released Win+V from Explorer (DisabledHotkeys contains V),
        Win+V would otherwise do nothing at all, so it goes back to Windows and Explorer restarts - unless
        /KeepWinVReleased is given or install.ps1's copy of BetterClipboard remains to use it.
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

if ((Get-BetterClipboardRunTarget).StartsWith($appDir, [StringComparison]::OrdinalIgnoreCase)) {
    Remove-ItemProperty -Path $BetterClipboardRunKey -Name $BetterClipboardRunValue
}

$advanced = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced'
$settings = Get-ItemProperty -Path $advanced -ErrorAction SilentlyContinue
$disabled = if ($settings -and $settings.PSObject.Properties['DisabledHotkeys']) { [string] $settings.DisabledHotkeys } else { '' }
if ($disabled.ToUpperInvariant().Contains('V')) {
    $otherCopy = Get-BetterClipboardOtherInstall
    if ($pp.KeepWinVReleased) {
        Write-Host 'Win+V stays released from Explorer (/KeepWinVReleased).'
    }
    elseif ($otherCopy) {
        Write-Host "Win+V stays released from Explorer: the copy of BetterClipboard at '$otherCopy' still uses it."
    }
    else {
        # Each character disables one Win+<char> combination; keep the user's other letters.
        $rest = -join ($disabled.ToCharArray() | Where-Object { [char]::ToUpperInvariant($_) -ne 'V' })
        if ($rest) {
            Set-ItemProperty -Path $advanced -Name DisabledHotkeys -Value $rest -Type String
        }
        else {
            Remove-ItemProperty -Path $advanced -Name DisabledHotkeys
        }

        if (Test-BetterClipboardDesktopUser) {
            Write-Host 'Giving Win+V back to Windows: restarting Explorer (the taskbar blinks once).'
            Restart-BetterClipboardDesktopShell
        }
        else {
            Write-Host 'Win+V goes back to Windows at the next sign-in.'
        }
    }
}

Write-Host 'BetterClipboard was uninstalled. Your history was kept in %LOCALAPPDATA%\BetterClipboard; delete that folder to remove it.'
