---
parent: install
after: start
---
# Ways to install

Every way to get BetterClipboard, side by side. They all install the same program, and they all keep your history in
the same encrypted folder, `%LOCALAPPDATA%\BetterClipboard`: moving from one way to another keeps your history, pins
and settings.

| Way | Made for | Admin rights | Installs to | Updates |
|---|---|---|---|---|
| [The PowerShell installer](powershell-installer.md) | You, on your own PC (the usual way) | No | `%LOCALAPPDATA%\Programs\BetterClipboard` | The update button, when you approve |
| [The portable zip](portable-zip.md) | Trying it out; no installer | No | Any folder you extract it to | The update button, in its own folder (0.2.11 and newer) |
| [For every user of a PC](for-every-user-of-a-pc.md) | Shared and family PCs | Yes | `%ProgramFiles%\BetterClipboard` | An administrator runs the installer again |
| [Intune, RMM tools and scripts](intune-and-rmm.md) | Managed PCs | Runs as SYSTEM | `%ProgramFiles%\BetterClipboard` | The same command, run again |
| [Chocolatey](chocolatey.md) | Chocolatey users (not published yet) | Usually | `%ChocolateyInstall%\lib\betterclipboard\tools\app` | `choco upgrade betterclipboard` |
| [Microsoft Store edition](microsoft-store-edition.md) | A copy signed by Microsoft, from the Store | No | Windows' own app folder | The Store |
| [From source](build-from-source.md) | Developers | No | Where you build it | Development builds through the update button |

## Which one?

- **Most people:** the PowerShell installer. One line, no admin rights; it checks the download's SHA-256, starts
  BetterClipboard with Windows, adds it to *Installed apps* and takes over Win+V.
- **No installer at all, or just trying:** the portable zip. Nothing is registered until you turn it on in Settings,
  and Win+V is answered by a keyboard hook while it runs, with no change to Windows.
- **A PC several people sign in to:** the installer with `-AllUsers`, as administrator. Every user gets BetterClipboard
  at sign-in, each with their own history.
- **Many PCs, managed centrally:** the same installer run as SYSTEM by your management tool: it installs for every user
  by itself.

## What every way has in common

- **Windows 10 version 2004 or newer, or Windows 11, on x64 or ARM64.** Nothing else to install: every download
  carries the .NET runtime and the Windows App SDK it needs.
- **One history.** It lives in `%LOCALAPPDATA%\BetterClipboard`, encrypted with a key only your Windows account on this
  PC can unseal ([How it works](how-it-works.md)). Installing, updating and uninstalling never touch it, unless you
  uninstall with `-RemoveData`.
- **One BetterClipboard at a time.** A second start hands over to the one that runs (opening its Settings), so two
  copies never fight over the clipboard or Win+V. A *newer* copy started by hand takes over from an older one instead:
  the older one closes, and the newer one carries on.
- **The same checks.** Every release lists the SHA-256 of each download in its `SHA256SUMS.txt`, and the installer
  refuses a download that does not match. Releases are not code-signed yet ([Code signing policy](code-signing-policy.md)).

To see exactly which files, folders and registry values each way creates, see
[What it changes on your PC](what-it-changes.md).
