---
parent: install
after: ways-to-install
---
# The PowerShell installer

`install.ps1` installs, updates and uninstalls BetterClipboard from its GitHub releases. It is the script the one-line
install command downloads and runs. It runs in Windows PowerShell 5.1, which every Windows has, and in PowerShell 7.

## Run it

```powershell
irm https://raw.githubusercontent.com/Nucs/BetterClipboard/main/install.ps1 | iex
```

`iex` cannot pass options. To give some, run the script as a script block:

```powershell
& ([scriptblock]::Create((irm https://raw.githubusercontent.com/Nucs/BetterClipboard/main/install.ps1))) -AddToPath -NoTakeOverWinV
```

Or from a copy on disk: every release attaches `install.ps1`, and every release zip carries it.

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1 -AddToPath
```

## What it does, step by step

1. **Picks the release:** the latest, or the one `-Version` names, through GitHub's API, and the zip for your PC's
   architecture, x64 or ARM64. It asks Windows for the architecture, not the PowerShell that runs it, so an x64
   PowerShell on an ARM64 PC still gets the ARM64 build.
2. **Checks the download:** its SHA-256 must equal the line in the release's `SHA256SUMS.txt` and the digest GitHub
   computed when the file was uploaded. A mismatch stops the installer before anything on your PC changes.
3. **Replaces the program, safely:** a running BetterClipboard is asked to exit (it saves the copies it has queued
   first), and the new files are put in place through a staging folder. If the swap fails, the previous version stays
   where it was.
4. **Registers it:** a Start menu shortcut, "Start with Windows" (the same entry the app's own switch writes) and an
   entry in *Settings › Apps › Installed apps*, whose Uninstall button runs this script with `-Uninstall`.
5. **Takes over Win+V:** Explorer is told to stop registering Win+V and restarts once (the taskbar blinks, and open
   folder windows close), so BetterClipboard registers Win+V itself, also over windows that run as administrator.
   Nothing happens here when Win+V was released already. See [Taking over Win+V](taking-over-winv.md).
6. **With `-AddToPath`,** puts the install folder on your user PATH, so terminals can run `bclip`.
7. **Starts BetterClipboard.** Run from PowerShell as administrator, it still starts BetterClipboard without
   administrator rights.

It works from your temp folder, so it runs from any folder, also one you cannot write to, and it leaves your
PowerShell in the folder it was in. It talks only to GitHub, for the release: nothing about you, your PC or your
clipboard is sent.

## Options

| Option | What it does |
|---|---|
| `-Version 0.2.11` | Install that release instead of the latest (`v0.2.11` works too). |
| `-InstallDir <path>` | Install into another folder. Default: `%LOCALAPPDATA%\Programs\BetterClipboard`, or `%ProgramFiles%\BetterClipboard` with `-AllUsers`. A relative path is relative to the folder you run the installer from. |
| `-NoTakeOverWinV` | Leave Win+V to Windows' own clipboard panel. BetterClipboard then opens with the `-Hotkey` shortcuts, or with Win+Alt+V. If Win+V was released before, Explorer gets it back (one restart). Needs 0.2.6 or newer. |
| `-Hotkey A, B, …` | The shortcuts that open BetterClipboard, e.g. `-Hotkey Win+Alt+V, Ctrl+Alt+F9`, replacing the ones set before. Win+V stays first unless `-NoTakeOverWinV` is given. A Win+letter or Win+digit shortcut is released from Explorer like Win+V; one that another app owns is intercepted by BetterClipboard's keyboard hook while it runs. Needs 0.2.6 or newer. |
| `-TakeOverWinV` | The default, kept for older command lines. It adds Win+V back when your shortcuts no longer include it, and on a re-run it takes back a Win+V you gave back to Windows in Settings. |
| `-AddToPath` | Put the install folder on your user PATH (with `-AllUsers`, on the PATH of every user), so terminals can run `bclip`. The command line itself stays off until you turn it on in *Settings › Command line*. `-Uninstall` takes the entry out again. |
| `-NoStartup` | Do not start BetterClipboard when you sign in. |
| `-NoShortcut` | No Start menu shortcut. |
| `-NoLaunch` | Do not start BetterClipboard after installing. |
| `-NoUpdateCheck` | Install with the daily update check off: BetterClipboard then never asks GitHub by itself. *Settings › Updates* has *Check now*, and the switch to turn the check on. Needs 0.2.6 or newer. |
| `-Package <zip>` | Install from a release zip on disk instead of downloading one (see [Offline, or a pinned version](#offline-or-a-pinned-version)). |
| `-PackageSha256 <hash>` | With `-Package`: the SHA-256 the zip must have. A zip with another hash is not installed. |
| `-AllUsers` | Install for every user of the PC, in PowerShell as administrator (see [For every user of a PC](for-every-user-of-a-pc.md)). |
| `-Repository owner/name` | Install from a fork's releases instead of `Nucs/BetterClipboard`. |
| `-Uninstall` | Remove BetterClipboard (see [Uninstall](uninstall.md)). `-RemoveData` deletes the history too; `-KeepWinVReleased` leaves Win+V released. |

The app runs the installer itself, with `-Update`, `-ResultPath` and `-LogPath`, when you approve an update
([Updates](updates.md)), and the Microsoft Store edition runs `-Uninstall -KeepProcessId` when it replaces this copy.
Those options are not meant to be typed.

## Running it again

Running the command again updates BetterClipboard in the same folder; your history is never touched. It keeps what you
changed in Settings since: *Start with Windows* that you turned off stays off, and a Win+V you gave back to Windows stays
with Windows (`-TakeOverWinV` takes it again). An install after an uninstall is a fresh one and sets everything up
again.

## Offline, or a pinned version

- **A specific release:** add `-Version`, for example `-Version 0.2.10`.
- **No internet on the PC:** download the zip for the PC's architecture (`BetterClipboard-<version>-win-x64.zip` or
  `-win-arm64.zip`) and `SHA256SUMS.txt` from the [release](https://github.com/Nucs/BetterClipboard/releases) into
  one folder. Take `install.ps1` from the zip or from the release, and run:

  ```powershell
  powershell -ExecutionPolicy Bypass -File .\install.ps1 -Package .\BetterClipboard-0.2.11-win-x64.zip
  ```

  The zip is checked against the `SHA256SUMS.txt` next to it, or against `-PackageSha256 <hash>` when you give one.
  With neither, it is installed with a warning that states its hash. The version is read from the file name, and an
  ARM64 zip is refused on an x64 PC.

## If something goes wrong

- **The download does not match its checksum:** nothing was installed or changed. Try again; if it keeps failing, a
  proxy or a broken download is in the way.
- **"Running scripts is disabled on this system":** the `irm … | iex` form runs no script file, so the execution policy
  does not stop it. The `-File` form needs `-ExecutionPolicy Bypass`, as shown above. Where a Group Policy forbids
  scripts altogether, the app's own update button cannot update (it runs the installer from a file): run the install
  command again instead.
- **A program holds the install folder** (a terminal whose current folder is inside it, say): the installer retries
  for a few seconds. If the folder stays in use, the previous version is left in place and started again, and the
  installer says what to close.
- **No "Windows protected your PC" warning:** that is expected. The installer downloads the release itself and unblocks
  the files it installs; the warning belongs to a zip downloaded with a browser ([The portable zip](portable-zip.md)).
