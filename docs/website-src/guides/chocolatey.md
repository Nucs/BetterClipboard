---
parent: install
after: intune-and-rmm
---
# Chocolatey

> [!NOTE]
> **Not on the Chocolatey Community Repository yet,** so `choco install betterclipboard` does not find it. The package
> is built and tested with every release, and each release attaches it. Until it is published, install that file as
> shown below.

The `betterclipboard` package carries the release's program for both x64 and ARM64 and unpacks the one your PC needs.

## Install the package from a release

1. From the [latest release](https://github.com/Nucs/BetterClipboard/releases/latest), download
   `betterclipboard.<version>.nupkg` into a folder of its own, for example `C:\Packages`. Use 0.2.6 or newer: the
   package attached to 0.2.5 removes other apps' "Start with Windows" entries.
2. In PowerShell as administrator:

   ```powershell
   choco install betterclipboard --source C:\Packages
   ```

Once the package is published, the `--source` part goes away.

## What it sets up

- **The program** in `%ChocolateyInstall%\lib\betterclipboard\tools\app`, and `bclip` and `BetterClipboard` on the
  PATH (Chocolatey's shims).
- **A Start menu shortcut,** in every user's Start menu when installed as administrator (the usual way).
- **"Start with Windows"** for the user at the PC who installs it. Installed as SYSTEM or with another account's
  credentials (or with `/AllUsersStartup`), it is for every user of the PC instead, and each user can turn it off for
  themselves.
- **It starts BetterClipboard** without administrator rights, for the user at the PC.
- **Win+V is answered by BetterClipboard's keyboard hook** while it runs: the package does not release Win+V from
  Explorer (that would restart Explorer during every unattended install). *Settings › Shortcut › Release from
  Explorer* does it on request ([Taking over Win+V](taking-over-winv.md)).

## Package parameters

```powershell
choco install betterclipboard --source C:\Packages --params "'/NoStartup /NoShortcut'"
```

| Parameter | Effect |
|---|---|
| `/NoStartup` | No "Start with Windows" entry. |
| `/AllUsersStartup` | Start BetterClipboard at every user's sign-in (needs administrator rights). |
| `/NoShortcut` | No Start menu shortcut. |
| `/NoLaunch` | Do not start BetterClipboard after installing. |
| `/KeepWinVReleased` | For `choco uninstall`: leave the shortcuts released from Explorer as they are. |

## Updates

A Chocolatey copy is updated by Chocolatey: `choco upgrade betterclipboard` (with `--source` until the package is
published) closes BetterClipboard, replaces it, and starts it again when it was running. The app never asks GitHub for
updates by itself; *Settings › Updates › Check now* still says whether a newer release exists, and its update dialog
copies the upgrade command.

## Uninstall

`choco uninstall betterclipboard` closes BetterClipboard, removes it, and gives back to Windows Win+V and the other
Win+letter and Win+digit shortcuts that were released from Explorer for it (unless `/KeepWinVReleased`). Your history
stays in `%LOCALAPPDATA%\BetterClipboard`.

## Next to the PowerShell installer's copy

Install one or the other. They can live side by side (they share one history, and the installer warns when it finds
the Chocolatey copy), but only one runs at a time. When you uninstall one of them, its "Start with Windows" entry is
handed to the other copy instead of being removed, and Win+V stays released while the other copy still uses it.
