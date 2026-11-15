---
parent: install
after: for-every-user-of-a-pc
---
# Intune, RMM tools and scripts

Any tool that runs a PowerShell command as SYSTEM (Intune, an RMM agent, a scheduled task) installs BetterClipboard for
every user of the PC: the installer sees that it does not run as the person at the PC and picks that by itself. What the
users get is described in [For every user of a PC](for-every-user-of-a-pc.md).

## Install

From GitHub, the latest release (a command line for the tool; it works in cmd and in PowerShell alike):

```cmd
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "& ([scriptblock]::Create((irm https://raw.githubusercontent.com/Nucs/BetterClipboard/main/install.ps1))) -AllUsers"
```

From files you host, without internet access on the PC: put `install.ps1`, the release zip for the PCs' architecture
and the release's `SHA256SUMS.txt` in one folder (an Intune Win32 app's package, a share), then run in that folder:

```cmd
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\install.ps1 -AllUsers -Package .\BetterClipboard-0.2.11-win-x64.zip
```

The zip is checked against `SHA256SUMS.txt` before anything changes. `-Version 0.2.11` pins a release in the first form.
The installer uses the 64-bit Program Files folder and the 64-bit view of the PC's registry, also when the tool runs a
32-bit PowerShell (Intune's agent does, for Win32 apps).

## Detect it

| Check | Value |
|---|---|
| Registry key (64-bit view) | `HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\BetterClipboard` |
| Version | That key's `DisplayVersion`, e.g. `0.2.11` |
| File (64-bit Program Files) | `C:\Program Files\BetterClipboard\BetterClipboard.exe` |

## Uninstall

```cmd
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%ProgramW6432%\BetterClipboard\install.ps1" -Uninstall -AllUsers
```

`%ProgramW6432%` is the 64-bit Program Files folder in every process: in a 32-bit one, such as the command lines
Intune's agent runs, `%ProgramFiles%` names `Program Files (x86)` instead, where BetterClipboard is not. In PowerShell
itself, write `$env:ProgramW6432`. The *Installed apps* entry's `QuietUninstallString` holds the same command, with the
folder spelled out. Each user's history stays in their own profile.

## Update

A copy installed for every user never updates itself and never asks GitHub by itself, because its users may not be
administrators. Run the install command again: run with administrator rights on a PC that has a copy for every user,
the installer updates that copy. A user's *Settings › Updates › Check now* still says whether a newer version exists,
and its update dialog copies the command for an administrator.

## What happens on the PC

- **The program** goes to `%ProgramFiles%\BetterClipboard`, in a folder only administrators may change: every user's
  sign-in starts that program, so no user may replace it.
- **Every user gets it at sign-in,** from the PC's "Start with Windows" entry. Run as SYSTEM, the installer starts it
  for no one right away: it starts at each user's next sign-in, or from the Start menu.
- **Running copies are closed:** installing or updating over a copy that runs closes it in every user's session.
  Copies that were still queued in one are lost, and the installer says so.
- **Each user decides for themselves:** turning off *Settings › App › Start with Windows* stops the start for that user
  only.
- **Shortcuts are per user:** BetterClipboard answers Win+V with its keyboard hook for everyone, and each user can
  release Win+V from Explorer in *Settings › Shortcut*. `-Hotkey`, `-TakeOverWinV` and `-NoTakeOverWinV` work only when
  the signed-in user installs, so they are refused here.
- **`-AddToPath`** puts the folder on the PATH of every user, for `bclip`.

[What it changes on your PC](what-it-changes.md) lists every file, folder and registry value.
