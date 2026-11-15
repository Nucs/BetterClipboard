---
parent: command-line-for-scripts-and-ai-agents
---
# BetterClipboard.exe's own switches

`bclip` reads and writes your history. `BetterClipboard.exe` itself also takes a few switches, for scripts, macro tools
and installers: open the panel, quit, start into the tray, or set a shortcut before the app starts.

| Command | What it does |
|---|---|
| `BetterClipboard.exe` | Starts BetterClipboard and opens Settings. When it runs already, brings its Settings forward; a newer version started this way takes over from an older one that runs. |
| `BetterClipboard.exe --background` | Starts into the notification area without opening anything (only the language question, until a language is chosen): what "Start with Windows" runs. When BetterClipboard runs already, it ends at once and changes nothing. |
| `BetterClipboard.exe --show-flyout` | Opens the panel in the running BetterClipboard, where Win+V opens it, so pasting goes back to the window you were in: bind it to a mouse button or a macro key. With nothing running, it starts BetterClipboard like a plain start. |
| `BetterClipboard.exe --exit` | Asks the running BetterClipboard to quit. It saves the copies it has queued first. With nothing running, it does nothing. |
| `BetterClipboard.exe --set-hotkeys Win+V Ctrl+Alt+F9` | Saves the shortcuts that open the panel, replacing the old ones, while BetterClipboard is not running. Add `--validate` to only check them. |
| `BetterClipboard.exe --set-update-check off` | Switches the daily update check off (`on` switches it on), while BetterClipboard is not running. |

`--set-hotkeys` and `--set-update-check` are what the installer uses for `-Hotkey`, `-NoTakeOverWinV` and
`-NoUpdateCheck`. They exit with `0` when done, `2` for something that is not a shortcut (or not `on` or `off`), `3`
when BetterClipboard runs (nothing is saved: the running app would overwrite the file), and `4` when the settings
could not be saved. Their output (the saved shortcuts, or `error:` lines) goes to standard output; BetterClipboard is a
Windows program without a console, so redirect it to see it:

```powershell
$exe = "$env:LOCALAPPDATA\Programs\BetterClipboard\BetterClipboard.exe"
$p = Start-Process $exe -ArgumentList '--set-hotkeys', '--validate', 'Win+Alt+V' -NoNewWindow -PassThru -RedirectStandardOutput "$env:TEMP\bc-out.txt"
$null = $p.Handle   # Windows PowerShell 5.1 reports the exit code only when the handle was read early
$p.WaitForExit()
Get-Content "$env:TEMP\bc-out.txt"; $p.ExitCode
```

Shortcuts are written as in *Settings › Shortcut*, for example `Win+V`, `Ctrl+Shift+K`, `Ctrl+Alt+Num5` or `F9`
([Choosing shortcuts](choosing-shortcuts.md)).

## A second, separate BetterClipboard

The environment variable `BETTERCLIPBOARD_DATA_DIR` points a BetterClipboard at another data folder, with its own
history and settings. Such a copy is separate from your usual one: it runs next to it, and `--exit` or `--show-flyout`
given with the same variable set reach only that copy. It is made for trying things out and for tests, so your real
history stays clean; it does not take Win+V while your usual BetterClipboard runs.

```powershell
$env:BETTERCLIPBOARD_DATA_DIR = "$env:TEMP\bc-try"
& "$env:LOCALAPPDATA\Programs\BetterClipboard\BetterClipboard.exe" --background
```

Other switches you may see in a process list (`--all-users-startup`, `--restart-after`, `--clipboard-sequence`,
`--apply-portable-update`, `--read-console-history`) are BetterClipboard's own, passed between its processes; they are
not meant to be typed.
