---
parent: install
---
# What it changes on your PC

Every file, folder and registry value BetterClipboard and its installers create, by kind of install. Uninstalling removes
all of it except your history (and the temporary pictures), unless you ask for those too ([Uninstall](uninstall.md)).

## Your history and settings (every kind of install)

| Where | What |
|---|---|
| `%LOCALAPPDATA%\BetterClipboard\stores\<id>\history.db` | The encrypted history (every copy, pins, groups, the prompt archive). |
| `%LOCALAPPDATA%\BetterClipboard\stores\<id>\history.key` | Its key, sealed with Windows DPAPI for your account on this PC. |
| `%LOCALAPPDATA%\BetterClipboard\settings.json` | Your settings. |
| `%LOCALAPPDATA%\BetterClipboard\logs\` | What happened (counts, versions, errors; never what you copied), kept for 14 days. |
| `%LOCALAPPDATA%\BetterClipboard\updates\` | The update check's cached release list and, during an update, its download. |
| `%LOCALAPPDATA%\BetterClipboard\installer.json` | What the PowerShell installer set up, so running it again and uninstalling know. |
| `%TEMP%\BetterClipboard\Images\` | Pictures *Save to temp and* wrote, deleted at a start once unused for 30 days (*Settings › History* changes the days) ([Pictures and their files](pictures-and-their-files.md)). |

The Microsoft Store edition uses the same folder. Where Windows does not let it write that folder for real, it keeps its
history in `%LOCALAPPDATA%\Packages\<its package>\LocalState\BetterClipboard` instead, and *Settings › Data folder*
says which.

## The PowerShell installer, for you

| Where | What |
|---|---|
| `%LOCALAPPDATA%\Programs\BetterClipboard\` | The program. |
| `%APPDATA%\Microsoft\Windows\Start Menu\Programs\BetterClipboard.lnk` | The Start menu shortcut. |
| `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, value `BetterClipboard` | "Start with Windows": `"<folder>\BetterClipboard.exe" --background`. |
| `HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\BetterClipboard` | The *Installed apps* entry and its Uninstall command. |
| `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced`, value `DisabledHotkeys` | Gets `V` (and the letter or digit of every other Win+letter or Win+digit shortcut you chose), so Explorer leaves Win+V to BetterClipboard. Other letters already there are kept. |
| `HKCU\Environment`, value `Path` | Only with `-AddToPath`: the program's folder, for `bclip`. |

## The PowerShell installer, for every user

| Where | What |
|---|---|
| `%ProgramFiles%\BetterClipboard\` | The program, in a folder only administrators may change. |
| `%ProgramData%\Microsoft\Windows\Start Menu\Programs\BetterClipboard.lnk` | The Start menu shortcut, for every user. |
| `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run`, value `BetterClipboard` | "Start with Windows" for every user: `"<folder>\BetterClipboard.exe" --background --all-users-startup`. Each user can turn it off in their own Settings. |
| `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\BetterClipboard` | The *Installed apps* entry; its Uninstall command asks for administrator rights. |
| `HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Environment`, value `Path` | Only with `-AddToPath`: the program's folder, for every user. |

The `HKLM` values are written in the registry's 64-bit view. Win+V is released from Explorer only for an administrator
who runs the installer on their own desktop (in their own `DisabledHotkeys`, as above). Every other user's copy answers
Win+V with its keyboard hook, and each user can release it in *Settings › Shortcut*.

## The portable zip

Nothing, until you change it in Settings: *Start with Windows* writes the same `Run` value as the installer (naming
this copy), and *Release from Explorer* writes `DisabledHotkeys`.

## Chocolatey

| Where | What |
|---|---|
| `%ChocolateyInstall%\lib\betterclipboard\tools\app\` | The program. |
| `%ChocolateyInstall%\bin\` | Chocolatey's shims for `bclip` and `BetterClipboard`. |
| The Start menu | The shortcut: every user's when Chocolatey runs as administrator. |
| The `Run` key | "Start with Windows": the installing user's `Run` value, or the PC's (`HKLM`) when SYSTEM or another account installs it, or with `/AllUsersStartup`. |

## The Microsoft Store edition

Windows installs it in its own app folder and removes it with the app. It starts with Windows through its startup task
(*Settings › Apps › Startup*), `bclip` runs in every terminal through an app execution alias, and it writes no registry
value and changes no Windows setting: Win+V is answered by its keyboard hook while it runs.

## Settings that change Windows

- *Start with Windows* writes or removes the `Run` value above (the Store edition: its startup task).
- *Release from Explorer* adds letters to `DisabledHotkeys` and restarts Explorer; giving them back removes them.
- *Add bclip to PATH* adds the program's folder to your user `Path`.
- *Windows clipboard history* switches Windows' own Win+V history on or off (the Store edition opens Windows' Settings
  page for it instead).
