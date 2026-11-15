---
parent: install
after: powershell-installer
---
# The portable zip

Every release is also a plain zip: extract it anywhere and run `BetterClipboard.exe`. Nothing is installed or
registered, and Win+V is answered by a keyboard hook only while BetterClipboard runs, so Windows itself is not changed.
It is the same program the installer installs.

## Download and check it

1. Open the [latest release](https://github.com/Nucs/BetterClipboard/releases/latest) and download the zip for your
   PC, `BetterClipboard-<version>-win-x64.zip` (or `-win-arm64.zip` on an ARM64 PC), and `SHA256SUMS.txt`. The `.7z`
   files hold the same program and are smaller, for 7-Zip users.
2. Check the hash in PowerShell, in the download folder:

   ```powershell
   Get-FileHash .\BetterClipboard-*-win-x64.zip
   ```

   The hash shown must equal the zip's line in `SHA256SUMS.txt`.
3. Extract the zip into a folder you can write to (your *Downloads* or *Documents*, a folder on a USB stick), so that
   updates can replace its files later.
4. Run `BetterClipboard.exe`. Releases are not code-signed yet, so Windows may say *Windows protected your PC*: with the
   hash checked, choose *More info › Run anyway* ([Code signing policy](code-signing-policy.md)).

At its first start BetterClipboard asks for your language, then opens Settings, and its icon appears in the
notification area. Press Win+V.

## How it differs from an installed copy

- **Win+V:** answered by the keyboard hook while BetterClipboard runs; quit it, and Windows' own Win+V panel is back
  at once. *Settings › Shortcut › Release from Explorer* releases Win+V from Explorer the way the installer does
  ([Taking over Win+V](taking-over-winv.md)).
- **Start with Windows:** off until you turn it on in *Settings › App*. It then starts this copy, from this folder.
- **No Start menu shortcut and no *Installed apps* entry:** the folder is the whole program.
- **Updates:** like an installed copy, it asks GitHub once a day whether a newer release exists, and its update button
  is highlighted when one does. From 0.2.11 on, *Update and restart* updates the copy in its own folder: the new
  version closes BetterClipboard, replaces the release's files one by one and starts it again. Files that are not part
  of the release stay, and if one of its files cannot be replaced (another program holds it open), every file goes back
  as it was. A copy in a folder you may not change gets the download page and the install command instead.
- **Your history is not in the folder.** Like every other kind of install, it lives in `%LOCALAPPDATA%\BetterClipboard`
  on the PC, encrypted for your Windows account. A copy you carry on a USB stick to another PC starts there with that
  PC's history, not with this one's.

## Moving it, or running a newer one

- **A newer version started by hand takes over:** start a newer `BetterClipboard.exe` (a new release extracted into a
  new folder, say) while an older one runs, and the older one closes and the newer one carries on.
- ***Start with Windows* follows the copy you run:** when its entry starts an older BetterClipboard, or one that is no
  longer there (you moved or deleted its folder), it is changed to start the copy you are running. An entry you turned
  off in Windows' startup apps stays off.

## Moving to the installer

Run the [install command](powershell-installer.md) while the portable copy runs: the installer closes it, installs
BetterClipboard into `%LOCALAPPDATA%\Programs\BetterClipboard`, and the installed copy opens the same history, pins and
settings. Then delete the portable folder.

## Removing it

1. In *Settings › App*, turn off *Start with Windows*. If you released Win+V or other shortcuts from Explorer in
   *Settings › Shortcut*, give them back there.
2. Quit BetterClipboard: right-click its icon in the notification area and choose *Exit*.
3. Delete the folder.

Your history stays in `%LOCALAPPDATA%\BetterClipboard`. Delete that folder too to remove it for good.
