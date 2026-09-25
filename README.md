# BetterClipboard

**The Win+V clipboard history Windows should have shipped.** Same shortcut, same panel by your cursor —
but it remembers everything, survives restarts, searches instantly, and keeps it all encrypted on your PC.

[![CI](https://github.com/Nucs/BetterClipboard/actions/workflows/ci.yml/badge.svg)](https://github.com/Nucs/BetterClipboard/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/Nucs/BetterClipboard?sort=semver)](https://github.com/Nucs/BetterClipboard/releases/latest)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

| | Windows' Win+V | BetterClipboard |
|---|---|---|
| History size | 25 items | 10,000 items (configurable, plus a size budget) |
| After a restart | **Gone** (only pinned items survive) | Everything is still there |
| Item size | ≤ 4 MB | ≤ 64 MB (configurable) |
| Search | None | Instant substring search, any language |
| Content | Text, HTML, bitmaps | Text, rich text/HTML, links, colors, images with previews, copied files |
| At rest | Pinned items encrypted | Everything encrypted (ChaCha20-Poly1305), bound to your PC and account |

## Install

Windows 10 version 2004 or newer / Windows 11, x64 or ARM64. Nothing else to install (the release is
self-contained). In PowerShell:

```powershell
irm https://raw.githubusercontent.com/Nucs/BetterClipboard/main/install.ps1 | iex
```

The installer downloads the latest [release](https://github.com/Nucs/BetterClipboard/releases/latest) for
your architecture, **verifies its SHA-256**, installs per user into `%LOCALAPPDATA%\Programs\BetterClipboard`
(no admin rights), adds a Start menu shortcut, starts BetterClipboard with Windows and registers it under
*Settings › Apps › Installed apps*. Running it again updates in place; your history is never touched.

Options (pass them through a script block):

```powershell
& ([scriptblock]::Create((irm https://raw.githubusercontent.com/Nucs/BetterClipboard/main/install.ps1))) -TakeOverWinV
```

| Option | Effect |
|---|---|
| `-TakeOverWinV` | Tell Explorer to release Win+V (see [Taking over Win+V](#taking-over-winv)) and restart Explorer once. |
| `-Version 0.1.0` | Install a specific release instead of the latest. |
| `-InstallDir <path>` | Install somewhere else. |
| `-NoStartup` / `-NoShortcut` / `-NoLaunch` | Skip starting with Windows / the Start menu shortcut / launching after install. |
| `-Uninstall [-RemoveData]` | Remove the app (history is kept unless `-RemoveData`). |

Prefer doing it by hand? Download the zip for your architecture and `SHA256SUMS.txt` from the release,
check the hash (`Get-FileHash .\BetterClipboard-*.zip`), extract anywhere and run `BetterClipboard.exe`.

## Use

Press **Win+V**. The panel opens by your text cursor with the search box focused — just type.

| Key | Action |
|---|---|
| *type* | Search (substring, case-insensitive, Hebrew/CJK/emoji included) |
| `↑` `↓` `PgUp` `PgDn` | Move the selection |
| `Enter` / click | Paste into the app you came from |
| `Shift+Enter` | Paste as plain text |
| `Ctrl+1` … `Ctrl+9` | Paste the n-th item |
| `Ctrl+P` | Pin / unpin (pinned items ignore retention and "clear") |
| `Del` (or `Shift+Del` while searching) | Delete the item |
| `Ctrl+F` | Back to the search box |
| `Menu` / `Shift+F10` / right-click | Item menu: paste, paste as plain text, copy only, pin, open link / show in Explorer, delete |
| `Esc` | Clear the search, then close — focus returns to where you were |

The tray icon opens the panel and Settings: shortcut, retention (items, days, size), what to record,
ignored apps (e.g. your password manager), pause, theme, start with Windows, and **Import from Windows**,
which pulls in everything Win+V still remembers — including its pinned items.

## Taking over Win+V

Explorer owns Win+V. BetterClipboard supports two ways to take it:

- **Default — no system change.** While BetterClipboard runs, a low-level keyboard hook intercepts Win+V
  before Explorer sees it. Exit BetterClipboard and Windows' own panel is back instantly.
- **Clean mode** (`-TakeOverWinV`, or *Settings › Release Win+V from Explorer*). Explorer is told not to
  register Win+V (`DisabledHotkeys` = `V`) and BetterClipboard registers it normally, so it also works
  while an elevated (admin) window is focused. Trade-off: while BetterClipboard isn't running, Win+V does
  nothing. Uninstalling gives Win+V back to Windows.

Any other shortcut works too (Settings › Shortcut).

## How it works

- **Capture.** BetterClipboard listens with `AddClipboardFormatListener` — the same change notification
  Windows' own clipboard-history service uses (there is no "atomic, never miss" clipboard API in
  Windows; every consumer reads the clipboard right after being told it changed). It reads each change
  immediately on a high-priority thread, so copies made even half a millisecond apart are captured
  (measured), and it keeps an exact tally of anything a program overwrote faster than that (shown in
  Settings › Capture reliability). A watchdog re-arms the listener if Windows ever stops delivering.
- **Privacy markers.** Copies flagged by apps as private (`ExcludeClipboardContentFromMonitorProcessing`,
  `CanIncludeInClipboardHistory = 0`, `Clipboard Viewer Ignore` — used by password managers) are never
  recorded.
- **Storage.** One SQLite database, fully encrypted with [SQLite3 Multiple Ciphers](https://github.com/utelle/SQLite3MultipleCiphers)
  (ChaCha20-Poly1305: every page, the write-ahead log, the full-text index and thumbnails). Search uses an
  FTS5 trigram index on the decrypted pages in memory.
- **The key.** A random 256-bit key, stored only sealed with Windows DPAPI for your account, with extra
  entropy derived (HKDF-SHA256) from this PC's `MachineGuid` and your account's SID. The history folder
  name is a UUIDv5 computed from the same binding. Result: the history opens only for BetterClipboard, on
  this Windows installation, signed in as you. It protects the data at rest — stolen disks, backups,
  other accounts — but, like every DPAPI-based app, not against malware already running as you.
  A history that can no longer be decrypted (e.g. after reinstalling Windows) is set aside, never deleted.

Data lives in `%LOCALAPPDATA%\BetterClipboard` (`stores\<id>\history.db` + `history.key`, `settings.json`,
`logs\`). Logs never contain clipboard content.

The research behind all this — how Win+V is built (service `cbdhsvc`, the `TextInputHost` panel,
Explorer's hotkey), why it forgets, its on-disk format for pins, and the measurements quoted above — is
in [CLAUDE.md](CLAUDE.md).

## Uninstall

*Settings › Apps › Installed apps › BetterClipboard › Uninstall*, or:

```powershell
powershell -ExecutionPolicy Bypass -File "$env:LOCALAPPDATA\Programs\BetterClipboard\install.ps1" -Uninstall
```

Your history stays in `%LOCALAPPDATA%\BetterClipboard` unless you add `-RemoveData`.

## Build from source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) on Windows.

```powershell
dotnet build BetterClipboard.sln
dotnet test --solution BetterClipboard.sln        # clipboard tests run in a private window station,
                                                  # so they never touch your real clipboard
pwsh tools/release/package.ps1 -Version 0.1.0     # the exact release zips + SHA256SUMS.txt
```

Set `BETTERCLIPBOARD_DATA_DIR` to a scratch folder when experimenting so your real history stays clean.
Releases are built by [`.github/workflows/release.yml`](.github/workflows/release.yml) from a `v*` tag.

## License

[MIT](LICENSE). Third-party components are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
BetterClipboard is not affiliated with Microsoft.
