# BetterClipboard — CLAUDE.md

> **Mission:** kill the pain of Windows' built-in `Win+V` clipboard history (25 items, wiped on every
> restart, 4 MB/item cap, no search) by replacing it with our own **persistent, searchable, beautiful
> .NET 10 + WinUI 3** clipboard manager that **takes over `Win+V`** and **imports whatever Windows still
> remembers**.

Legend used below: **[verified]** = observed/probed on this machine (Windows 11 25H2, build
26200.8875, 2026-09-25) · **[docs]** = Microsoft documentation · **[inferred]** = deduced from binary
strings/registrations, not proven by execution.

---

## 0. TL;DR of the research

| Question | Answer |
|---|---|
| Is Win+V open source? | **No.** It is closed Windows code split across an OS service (`cbdhsvc.dll`) and a Store-serviced UI package (`TextInputHost.exe`). Nothing to fork or "refactor". |
| Where is it coded? | Microsoft-internal trees: service = `onecoreuap\windows\cbdhsvc\{dll,lib}\*.cpp` (C++ / WRL / WIL); UI = TextInput repo `Src\Components\TextInput\SuggestionUI\ClipboardAdapter.cpp` (C++/CX + XAML) **[verified — embedded source paths]**. |
| Where is it hosted? | Backend: per-user service `cbdhsvc_<LUID>` in `svchost.exe -k ClipboardSvcGroup`. UI: `C:\Windows\SystemApps\MicrosoftWindows.Client.CBS_cw5n1h2txyewy\TextInputHost.exe` (package `MicrosoftWindows.Client.CBS_cw5n1h2txyewy`). Hotkey: `explorer.exe` **[verified]**. |
| Reverse-engineerable? | Yes, for **interoperability** (registry, WinRT registrations, on-disk format, strings, public PDBs on the Microsoft symbol server). The Windows EULA forbids RE beyond what law permits, and copying their code would be pointless anyway. We did black-box/interop observation only (no disassembly) and re-implement on **public** Win32/WinRT APIs. |
| Why does it forget on restart? | By design: unpinned items live in the service's **memory** (`ClipboardHistoryBuffer`); only **pinned** items are written to disk (DPAPI-NG encrypted) **[verified]**. After this morning's boot the WinRT history held just 3 new items + 1 pinned one. |
| Can we take over? | **Yes.** (1) Capture ourselves via `AddClipboardFormatListener`; (2) persist in SQLite; (3) hijack `Win+V` with a low-level keyboard hook (or `DisabledHotkeys=V` + Explorer restart); (4) import Windows' current history (WinRT API works from a **background unpackaged** process **[verified]**) and its pinned items (decryptable with `NCryptUnprotectSecret` as the same user **[verified]**). |
| Is there an "atomic", never-miss clipboard subscription? | **No — none exists in user mode [verified, §1.9].** Win+V's service, WinRT `Clipboard.ContentChanged` and Chromium's new web `clipboardchange` event all sit on the same `WM_CLIPBOARDUPDATE` notification we use; every consumer reads the clipboard *after* being told. The legacy viewer chain is **not** synchronous either (producer's `CloseClipboard` returned in 0.01 ms while the viewer slept 150 ms). What we control: read immediately (no debounce) → copies ≥ 2 ms apart are all captured (1 ms: usually; below: mostly overwritten before anyone can read them); overwritten copies are counted, never silently lost, and the last copy of a burst is always captured. |
| Can Win+V be replaced for good? | **Yes, both ways [verified 2026-09-25]:** default LL-hook interception (no system change), or `DisabledHotkeys=V` + Explorer restart → Win+V becomes free and BetterClipboard gets it via plain `RegisterHotKey`. Original state restored afterwards (value absent, Explorer owns Win+V again). |

---

## 1. How Windows clipboard history (Win+V) actually works

### 1.1 Component map **[verified]**

| Layer | Binary / location | Role | Tech |
|---|---|---|---|
| Hotkey owner | `C:\Windows\explorer.exe` | Registers `Win+V` (telemetry event `ClipboardHistoryHotkeyRegistration`), activates `ClipboardHistoryServer`, reads policy `AllowClipboardHistory`/`AllowCrossDeviceClipboard` | C++ |
| Hotkey router | `C:\Windows\System32\twinui.pcshell.dll` | `ShellHotKeyRequestReceived` → shows `SuggestionUIClipboardHistory` | C++ |
| UI host | `C:\Windows\SystemApps\MicrosoftWindows.Client.CBS_cw5n1h2txyewy\TextInputHost.exe` ("Windows Input Experience", package `MicrosoftWindows.Client.CBS` v1000.26100.334.0) | Hosts the emoji/GIF/kaomoji/symbols/**clipboard** panel | XAML |
| UI module | `C:\Windows\SystemApps\MicrosoftWindows.Client.CBS_cw5n1h2txyewy\WindowsInternal.ComposableShell.Experiences.SuggestionUIUndocked.dll` (v2605.22000.400.0) + `.winmd` | `ClipboardAdapter` / `ClipboardHistoryItem` (pin/unpin/delete/select/upload, `GetMaxPinnedClipboardHistoryItemsAsync`, `PasswordFieldClipboardHistory`) | **C++/CX** (hat `^` types, PPL tasks) |
| UI → service wrapper | `C:\Windows\System32\windowsudk.shellcommon.dll` ("Windows **Undocked Dev Kit** Shellcommon") | In-proc WinRT classes `WindowsUdk.ApplicationModel.DataTransfer.{ClipboardHistory, ClipboardHistoryItem, ClipboardSettings, ClipboardViewManager, ClipboardHistoryPromotionManager}` | WinRT |
| Public API | `C:\Windows\System32\windows.applicationmodel.datatransfer.dll` | `Windows.ApplicationModel.DataTransfer.Clipboard` (+ `ClipboardContentOptions`, internal `ClipboardPolicy`) → talks to the OOP server | WinRT |
| **Backend service** | `C:\Windows\System32\cbdhsvc.dll` ("Microsoft (R) Clipboard History", 10.0.26100.8117) in `svchost.exe -k ClipboardSvcGroup -p` | Out-of-proc WinRT server **`CBDHSvc`** (`ServerType=2` service) hosting `Windows.ApplicationModel.Internal.DataTransfer.{ClipboardHistoryServer, ClipboardBrokerProvider, ClipboardHistoryItemInternal, ClipboardOperationAppInfo, ClipboardSettings, ClipboardSettingsProvider, ClipboardSignalProducer, ClipboardViewManager}` + `WindowsInternal.SmartActionPlatform.SmartClipboardProxy` | C++ (WRL/WIL, `Windows.Data.Json`, `Windows.Storage.Compression`, `DataProtectionProvider`) |
| Cloud sync | `C:\Windows\System32\cdprt.dll` (Connected Devices Platform runtime) | `Windows.ApplicationModel.Internal.DataTransfer.{CloudClipboard, ClipboardChannel}` — Microsoft-account sync | C++ |
| Smart actions / AI | `C:\Windows\System32\SmartActionPlatform.dll`, `C:\Windows\System32\TaskFlowDataEngine.dll` | `SmartClipboard` suggested actions, `ClipboardSignalListener`; cbdhsvc also references `Microsoft.Windows.AugLoop.CBS` packages **[inferred: AI "augmentation loop" features]** | — |

Service registration **[verified]**: template `cbdhsvc` (Type `0x60` = `USER_SHARE_PROCESS | TEMPLATE`,
auto-start delayed, `UserServiceFlags=2`, `RequiredPrivileges=SeImpersonatePrivilege`) → per-user
instance `cbdhsvc_<hex>` (suffix = logon-session LUID, changes every sign-in). `ServiceDll = %SystemRoot%\System32\cbdhsvc.dll`.
Early Windows 10 builds (1809–1909) hosted the UI in
`SystemApps\InputApp_cw5n1h2txyewy\WindowsInternal.ComposableShell.Experiences.TextInput.InputApp.exe`
(from memory, unverified).

### 1.2 Data flow **[verified structure / inferred sequencing]**

```text
Any app ──SetClipboardData──► win32k clipboard ──(listener)──► cbdhsvc ClipboardMonitor
                                                     │  exclusion formats? size limits? duplicate?
                                                     ▼
                                   ClipboardHistoryBuffer (in-memory, 25 items)  ── pin ──► ClipboardHistoryDataStore
                                                     │                                        └► %LOCALAPPDATA%\Microsoft\Windows\Clipboard\Pinned (DPAPI-NG)
                                                     └── roaming enabled ──► CloudClipboard (cdprt.dll) ──► Microsoft account
Win+V ─► explorer.exe hotkey ─► twinui.pcshell ─► TextInputHost (SuggestionUIUndocked.ClipboardAdapter)
      ─► WindowsUdk...ClipboardHistory (windowsudk.shellcommon.dll) ─► OOP WinRT server "CBDHSvc"
Pick an item ─► SelectHistoryItemAsync ─► service re-places item on clipboard ─► paste injected into focused app
                (cbdhsvc talks to the input stack over ALPC: "Input\Injection.AlpcPort\Server", monitors paste keys)
```

### 1.3 Internal source tree (from embedded `__FILE__` paths in `cbdhsvc.dll`) **[verified]**

`onecoreuap\windows\cbdhsvc\dll\{userservice.cpp, ntuserservice.cpp}` and
`onecoreuap\windows\cbdhsvc\lib\`: `clipboardactivitymonitor.cpp`, `clipboardactivityfeedmanagement.cpp`,
`clipboardbrokerprovider.cpp`, `clipboarddatacompressor.cpp`, `clipboarddataencrypter.cpp`,
`clipboardhistorybuffer.cpp`, `clipboardhistorydatastore.cpp`, `clipboardhistoryitem.cpp`,
`clipboardhistoryserver.cpp`, `clipboardmonitor.cpp`, `clipboardnotificationssender.cpp`,
`clipboardoperationappinfo.cpp`, `clipboardsettings.cpp`, `clipboardsignalproducer.cpp`,
`clipboardviewmanager.cpp`, `cloudclipboard.cpp`, `datapackagedeserializer.cpp`, `datapackagehelper.cpp`,
`datapackageserializer.cpp`, `datapackagesize.cpp`, `identityhelpers.cpp`, `localcontentchangelistener.cpp`,
`smartclipboardproxy.cpp`, `storagefolderremover.cpp` (+ WIL headers `onecore\internal\sdk\inc\wil\...`).
UI: `C:\__w\1\s\Src\Components\TextInput\SuggestionUI\{ClipboardAdapter.cpp,.h, ClipboardHistoryItem.cpp}`
(1ES/Azure-Pipelines build agent path → separate "TextInput" repo shipped via the feature-experience pack).

Behavior revealed by telemetry/event names in `cbdhsvc.dll` **[inferred]**:
`ClipboardHistoryBuffer_{PinItem, UnpinItem, DeleteItem, SelectHistoryItem, RestorePinnedItems, ScheduleCleanupForOldItems, UploadItem}`,
`ClipboardMonitor_RemovedDuplicateFromBuffer` (dedupes identical items), `ItemsCountLimitExceeded`,
`OverMaxItemSizeHistoryItem`, `PinnedItemSizeExceeded`, `UnpinnedItemSizeExceeded`, `ZeroSizeHistoryItem`,
`ClipboardHistoryDataStore_TryRestorePersistedSession_*` (restores the newest `HistoryData\{session}` folder
after a *service* restart — not across reboots), `StorageFolderRemover.RemoveFoldersComplementaryAsync`
(wipes stale session folders), `ClipboardDataDecrypter_DecryptionFailure`, telemetry fields
`maxHistoryItemSizeInBytes`, `maxHistorySizeInBytes`, `inMemoryBufferSizeInBytes`.

### 1.4 On-disk format **[verified]**

```text
%LOCALAPPDATA%\Microsoft\Windows\Clipboard\
├── HistoryData\{session-guid}\          ← created at logon; EMPTY in practice (unpinned items stay in RAM)
└── Pinned\{store-guid}\
    ├── metadata.json                     ← UTF-16LE + BOM: {"items":{"{item-guid}":{"timestamp":"2026-03-07T10:17:25Z","source":"Local"}}}
    └── {item-guid}\
        ├── metadata.json                 ← {"formatMetadata":{"Locale":{"dataType":"Stream","collectionType":"None","isEncrypted":true},
        │                                     "Text":{"dataType":"String","collectionType":"None","isEncrypted":true}},
        │                                     "sourceAppId":"","property":{}}
        ├── VGV4dA==                      ← file name = Base64(format name) → "Text"
        └── TG9jYWxl                      ← "Locale"
```

- Each payload file is a **DPAPI-NG blob** (`NCryptProtectSecret`): CMS `EnvelopedData` (OID
  1.2.840.113549.1.7.3) → `KEKRecipientInfo` with Microsoft DPAPI-NG OID `1.3.6.1.4.1.311.74.1` and
  protection descriptor **`LOCAL=user`**, key wrap `aes256-wrap`, content `aes-256-gcm`.
  (Public forensic write-ups from 2018–2020 only guessed at this; they also saw plain Base64 on 1809.)
- **Decryption works for any process running as the same user**:
  `NCryptUnprotectSecret(NULL, NCRYPT_SILENT_FLAG, blob, len, NULL, NULL, &out, &cb)` → `LocalFree(out)`.
  `Text` decrypts to UTF-16LE without terminator (dataType `String`), `Locale` to a 4-byte LCID (`0x0409`).
- Other `dataType` values seen in strings: `String, StringArray, Stream, StreamReference(File|FilePath|Uri),
  RandomAccessStream, StorageItem, StorageFile(Content|Data|Path), StorageFolder, Iterable`; formats
  referenced: `Text/PlainText/AnsiText, HTML Format, Rich Text Format (via DataPackage), Bitmap,
  DeviceIndependentBitmap(V5), UniformResourceLocator(W), ApplicationLink, Locale`.
- `metadata.json` timestamps equal the WinRT `ClipboardHistoryItem.Timestamp` of the same item → usable to
  mark imported WinRT items as pinned.
- Older artifact: `%LOCALAPPDATA%\ConnectedDevicesPlatform\<id>\ActivitiesCache.db` (Timeline) stored synced
  clipboard text (ActivityType 10/16, Base64 `ClipboardPayload`, ~12 h expiry) **[docs/forensics]**.

### 1.5 Registry & policy knobs

`HKCU\Software\Microsoft\Clipboard` — seen on this machine **[verified]**: `EnableClipboardHistory`
(DWORD 1/0), `ShellHotKeyUsed`, `PastedFromClipboardUI`, `HistoryOldItemsLastCleanupTimestamp`
(QWORD FILETIME, e.g. 2026-09-24 10:52:59Z). Referenced in `cbdhsvc.dll` strings **[inferred]**:
`CloudClipboardAutomaticUpload`, `ClipboardTipRequired`, `DoubleCopyGestureEnabled`,
`ClipboardSvcDebugWaitInSec`, `CloudClipRDPOverride`, `CloudContentValueWindowInSec`.
Also `...\CurrentVersion\SmartActionPlatform\SmartClipboard` and `...\CurrentVersion\WindowsAugLoop`.

Policy `HKLM\SOFTWARE\Policies\Microsoft\Windows\System`: `AllowClipboardHistory`, `AllowCrossDeviceClipboard`
(DWORD 0 = disabled) **[docs + strings]**. **There is no knob for the 25-item limit** (none documented, none
in strings).

Explorer hotkey kill-switch: `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced`
`DisabledHotkeys` (REG_SZ, each char = one `Win+<char>` combo, e.g. `"V"`), effective after Explorer
restart/sign-out **[verified 2026-09-25]**: with `"V"` + Explorer restart, `RegisterHotKey(MOD_WIN,'V')`
succeeds (was 1409) and the app logs `Win+V registered with RegisterHotKey (no keyboard hook needed)`;
deleting the value + restart gives Win+V back to Explorer. `explorer.exe` contains the string
`DisabledHotkeys`, so the knob is still read by this build. (Value absent on this machine by default.)
A letter releases only the exact `Win+<char>` chord **[verified 2026-10-03]**: with `"V"` set (since 2026-09-25 on this
PC), `probe_hotkeys.cs` still found Win+Ctrl+V (Windows' sound output flyout) TAKEN. So BetterClipboard releases only
shortcuts that are exactly Win plus a letter or digit (§2.25). On a fresh Windows 11 25H2 test desktop (26200, a
`claude-desktops` VM), `"CER1"` + Explorer restart freed Win+E, Win+R and Win+1, but **not Win+C** (Copilot): another
part of Windows holds it, so BetterClipboard takes Win+C over with the hook. Removing the letters + restart took the
freed ones back. **Never write this value with `New-Item -Path …\Advanced -Force`**: on an existing key that deletes
every Explorer setting in it (§4).

### 1.6 Hotkey ownership probe (`RegisterHotKey`, 2026-09-25) **[verified]**

| Combo | Result | Likely owner |
|---|---|---|
| `Win+V` | TAKEN (`ERROR_HOTKEY_ALREADY_REGISTERED` 1409) | explorer.exe (clipboard history) |
| `Win+Shift+V` | TAKEN | PowerToys Advanced Paste (PowerToys is running) |
| `Win+Ctrl+V` | TAKEN | Windows (sound output flyout) |
| `Win+C` | TAKEN | Windows (Copilot) |
| `Win+Alt+V`, `Ctrl+Shift+V`, `Ctrl+Alt+V`, ``Ctrl+` `` | FREE | — |
| `Alt+R` (probed 2026-10-01) | TAKEN | NVIDIA Overlay (its performance overlay's default key; AMD's Radeon overlay also defaults to Alt+R) |
| `Alt+C`, `Alt+W`, `Alt+E`, `Alt+X`, `Alt+O`, `Alt+Z`, `Alt+Q` (2026-10-01) | FREE | — (the search box uses Alt+C / Alt+W / Alt+E, §2.6.2) |

→ To own `Win+V` we must either intercept it with `WH_KEYBOARD_LL` (swallow `V` while Win is down + inject a
dummy key so releasing Win doesn't open Start — the PowerToys Keyboard-Manager trick) or free it via
`DisabledHotkeys` and then `RegisterHotKey(MOD_WIN,'V')`.

### 1.7 Public APIs we build on

- **Win32 clipboard** **[docs]**: `AddClipboardFormatListener` + `WM_CLIPBOARDUPDATE` (Vista+; replaces the
  fragile `SetClipboardViewer` chain — which, measured, is *not* synchronous either, §1.9),
  `GetClipboardSequenceNumber` (per window station; bumps: see §1.9), `OpenClipboard` (fails while another
  process holds it → retry), `EnumClipboardFormats` (original formats first, then synthesized ones),
  `GetClipboardData`/`SetClipboardData` (HGLOBAL `GMEM_MOVEABLE`; system owns it after success),
  `EmptyClipboard` (needs a non-NULL owner HWND or `SetClipboardData` fails), `RegisterClipboardFormat`
  (IDs are per-session atoms → **persist names, not IDs**), `GetClipboardFormatName`, `GetClipboardOwner`.
  Synthesized pairs: `CF_TEXT↔CF_UNICODETEXT↔CF_OEMTEXT`, `CF_BITMAP↔CF_DIB↔CF_DIBV5`,
  `CF_ENHMETAFILE↔CF_METAFILEPICT` → store only one of each family.
- **"Don't record me" formats** **[docs]**: `ExcludeClipboardContentFromMonitorProcessing` (any data →
  exclude from history *and* cloud), `CanIncludeInClipboardHistory` (DWORD 0 = exclude, 1 = include),
  `CanUploadToCloudClipboard` (DWORD 0/1, cloud only), legacy convention `Clipboard Viewer Ignore`.
  Password managers set these; **we must honor them**.
- **WinRT `Windows.ApplicationModel.DataTransfer.Clipboard`** **[docs]**: `GetHistoryItemsAsync()`
  (`ClipboardHistoryItemsResultStatus` = Success/AccessDenied/ClipboardHistoryDisabled),
  `SetHistoryItemAsContent`, `DeleteItemFromHistory`, `ClearHistory`, `IsHistoryEnabled`,
  `IsRoamingEnabled`, events `HistoryChanged`/`HistoryEnabledChanged`/`RoamingEnabledChanged`/`ContentChanged`,
  `SetContentWithOptions(pkg, new ClipboardContentOptions { IsAllowedInHistory, IsRoamable, HistoryFormats, RoamingFormats })`.
  The "must be foreground" rule applies to UWP/AppContainer apps; **an unpackaged desktop process in the
  background got `Success` with 4 items [verified]**.
- **Documented limits** **[docs]**: 25 items, ≤ 4 MB per item, text/HTML/bitmap only, cleared on restart
  except pinned, sync via Microsoft account (1809 notes said 1 MB/item, 5 MB total, synced items live 12 h).
  Clearing history keeps pinned items. Sleep/hibernate keep history.

### 1.8 Prior art (ideas only — **do not copy GPL code into this repo**)

- [Ditto](https://github.com/sabrogden/Ditto) (C++/MFC, GPL-3.0) — classic, DB-backed, very configurable. Studied in
  depth on 2026-10-16 (source, wiki, and the 3.25.113 release running in a test desktop): §2.32 and
  [`docs/ditto.md`](docs/ditto.md).
- [CopyQ](https://github.com/hluk/CopyQ) (C++/Qt, GPL-3.0) — scripting, tabs, documents the ignore formats.
- [PowerToys Advanced Paste](https://github.com/microsoft/PowerToys) (C#, MIT) — WinUI 3, uses the WinRT
  history API, owns `Win+Shift+V` here.
- [ClipboardHistoryThief](https://github.com/netero1010/ClipboardHistoryThief) — reads cbdhsvc heap memory
  (shows history items live unencrypted in the service's RAM).
- Research: [ThinkDFIR "Clippy History"](https://thinkdfir.com/2018/10/14/clippy-history/),
  [Inversecos clipboard forensics](https://www.inversecos.com/2022/05/how-to-perform-clipboard-forensics.html),
  [Raymond Chen: enumerating clipboard history](https://devblogs.microsoft.com/oldnewthing/20230302-00/?p=107889),
  [Clipboard Formats (Learn)](https://learn.microsoft.com/en-us/windows/win32/dataxchg/clipboard-formats),
  [Using the clipboard (Support)](https://support.microsoft.com/en-us/windows/using-the-clipboard-30375039-ce71-9fe4-5b30-21b7aab6b13f).

Reproduction probes that produced the verified facts live in
[`tools/probes/`](tools/probes) — re-run them after Windows updates
(they print metadata only, never clipboard content):
[`probe_dpapi.cs`](tools/probes/probe_dpapi.cs) (pinned-blob decryption),
[`probe_hotkeys.cs`](tools/probes/probe_hotkeys.cs) (hotkey ownership),
[`probe_history.cs`](tools/probes/probe_history.cs) (WinRT history from background),
[`winrt_classes.py`](tools/probes/winrt_classes.py) (WinRT class hosting),
[`binary_strings.py`](tools/probes/binary_strings.py) (strings of a binary),
[`probe_viewer_chain.cs`](tools/probes/probe_viewer_chain.cs) (viewer chain vs listener timing, §1.9),
[`probe_sequence.cs`](tools/probes/probe_sequence.cs) (sequence-number bumps, delayed rendering, §1.9),
[`probe_everything.cs`](tools/probes/probe_everything.cs) (voidtools Everything IPC in a private instance, §2.14),
[`probe_runmru.py`](tools/probes/probe_runmru.py) (Win+R history structure, change watch, §2.15),
[`probe_shellhistory.cs`](tools/probes/probe_shellhistory.cs) (PowerShell and cmd command history, §2.16),
[`probe_screenshots.cs`](tools/probes/probe_screenshots.cs) (Win+PrtScn and Snipping Tool's auto-save: folders, names,
settings, a passive live watch, §2.18),
[`probe_chocolatey.ps1`](tools/probes/probe_chocolatey.ps1) (Chocolatey's shims, ARM64 handling, upgrades and
uninstalls, in a private Chocolatey root, §3.2),
[`probe_agent_prompts.py`](tools/probes/probe_agent_prompts.py) (Claude Code's and Codex's prompt histories: formats,
identities, whose prompts, rewrites — structure only, §2.21),
[`probe_append_notify.cs`](tools/probes/probe_append_notify.cs) (what a folder watcher and the folder's listing see while
another process appends to a file, §2.21),
[`probe_summon.cs`](tools/probes/probe_summon.cs) (how fast the panel appears after Win+V, from screen pixels; where a
letter typed right after Win+V goes; DWM's latency per way of revealing a window — injects keys, so it runs only inside a
claude-desktops Windows desktop, §2.27),
[`probe_ditto.py`](tools/probes/probe_ditto.py) (where Ditto keeps its settings and history, and what a `Ditto.db`
holds — structure, counts, formats, never content; `--self-test` builds a BC-TEST database shaped like Ditto's, §2.32),
[`probe_rich_formats.cs`](tools/probes/probe_rich_formats.cs) (what a copy from Word, Teams, LibreOffice or a browser
holds: every format, its size and how long the app took to render it, and the structure of its HTML, RTF and metafiles —
never content; `--watch` for live copies, `--save` keeps BC-TEST copies only, `--render-rtf` and `--render-emf` draw
with Windows' RichEdit and GDI, §2.33),
[`preview_lab`](tools/probes/preview_lab) (a WinUI 3 app: RTF in a read-only `RichEditBox`, HTML in a WebView2 with
scripts off and every request blocked; times, memory, requests; it opens a window, so it runs only inside a
claude-desktops Windows desktop, §2.33),
[`probe_dpi_move.cs`](tools/probes/probe_dpi_move.cs) (what Windows suggests when a program moves a window onto a monitor
with another scale; a cloaked tool window, never seen, so it runs on a PC in use; needs two monitors with different
scales, §2.38),
[`dpi_move_lab`](tools/probes/dpi_move_lab) (a WinUI 3 app: whether a window set up like the panel keeps the bounds
`AppWindow.MoveAndResize` gave it on such a monitor, and with `--fix` whether asking again holds; cloaked and never
activated, so it runs on a PC in use, §2.38),
[`probe_memmap.cs`](tools/probes/probe_memmap.cs) (what a running BetterClipboard's memory is: committed, charged and resident per
kind — native heap, .NET heap, each DLL, GPU memory, stacks; read-only, with no window, no injected thread and no dump,
so it can map the owner's own app, §2.45),
[`msix_lab`](tools/probes/msix_lab) (what Windows changes for a program that runs from an MSIX package: registry and
file redirection, change notifications, child processes, DPAPI, named objects, the startup task, WinRT clipboard
history, languages, and what uninstalling removes; it registers a package, so it runs only inside a claude-desktops
Windows desktop, never on the owner's PC, §2.47).
Run C# probes with `dotnet run tools/probes/<name>.cs`, Python ones with `python tools/probes/<name>.py`, PowerShell
ones with `pwsh tools/probes/<name>.ps1`.

### 1.9 Clipboard change notification — "can we never miss a copy?" (2026-09-25) **[verified]**

Question (user): Microsoft reportedly added an "atomic" clipboard subscription that never misses a copy —
use it instead of Win+V's mechanism? Findings:

- **No such native API.** The recent announcement is the **web** `clipboardchange` event (Chromium/Edge),
  implemented on Windows over `AddClipboardFormatListener`. WinRT `Clipboard.ContentChanged` and Win+V's
  cbdhsvc use that same listener. All of them are *notifications*; the content is read afterwards by
  opening the clipboard, so a producer that replaces the content faster than the consumer can open it
  wins — for every consumer, Win+V included. We never polled; we already used the Win+V mechanism.
- **The "synchronous" viewer chain is a myth on modern Windows.** Probed inside a private window station
  (`probe_viewer_chain.cs`): with the viewer sleeping 150 ms in `WM_DRAWCLIPBOARD`, the producer's
  `CloseClipboard` still returned in **0.01 ms** (delivered like `SendNotifyMessage`). In a 40-copy
  zero-delay burst both mechanisms got 40 notifications and read ~2 distinct states. The chain is also
  fragile (a crashed member cuts off everyone after it), so BetterClipboard does not join it. Our own
  write re-enters `WM_DRAWCLIPBOARD` inside our `CloseClipboard` (same thread).
- **Sequence numbers** (`probe_sequence.cs`): `EmptyClipboard` +1, each `SetClipboardData` +1,
  `CloseClipboard` **+2** (synthesized formats) → read the self-write number *after* close. A staged add
  (owner adds formats without emptying) gets its own notification. A delayed render (`WM_RENDERFORMAT` →
  `SetClipboardData`) neither bumps the number nor notifies.
- **Capture rate vs gap** (real `ClipboardMonitor`, 100 copies with busy-waited gaps — `Thread.Sleep`
  rounds up to the 15.6 ms tick). The old 60 ms debounce would have merged anything < 60 ms apart into
  one item.
  - **Re-measured 2026-09-25** (evening, the explicit test `ClipboardCaptureTests.CaptureRate_BySpeedOfCopying`,
    `-explicit only`; 4 sweeps × 3 rounds of 100 per gap, on a machine busy with builds). Captured of 100:

    | gap | 0 ms | 0.05 | 0.1 | 0.25 | 0.5 | 0.75 | 1 ms | **2 ms** | 5 ms |
    |---|---|---|---|---|---|---|---|---|---|
    | captured | 1–3 | 2–7 | 3–77 | 4–97 | 37–100 | 79–100 | 62–100 (mostly 100) | **100 every round** | 100 every round |

  - The morning's single run ("≥ 0.5 ms → 100/100") was optimistic: below 2 ms the rate swings with
    scheduling. That swing is a 0.2–0.5 ms read racing the producer's next write.
  - In every round every notification was read or counted as overwritten (locked out: 0), and the final
    copy was captured. That is the real guarantee: at any speed, nothing disappears unaccounted, and the
    latest state is never lost.
  - Honest one-liner: humans and scripts that pause ≥ 2 ms between copies lose nothing; a tight loop of
    copies loses the intermediate ones — for every reader, Win+V included — and we say how many.
- **Isolation trick for tests/probes:** `CreateWindowStation(NULL, …)` (named stations need elevation) →
  `SetProcessWindowStation` → `CreateDesktop` → MTA threads call `SetThreadDesktop` before any other user32
  call (STA fails with `ERROR_BUSY`: COM's hidden window). The station has its own clipboard; the user's
  clipboard and Win+V history (RAM-only, unrecoverable) stay untouched.

---

## 2. BetterClipboard architecture

### 2.1 Solution layout

| Project | TFM | Role |
|---|---|---|
| [`src/BetterClipboard.Core`](src/BetterClipboard.Core) | `net10.0` | OS-agnostic heart: models (`Model/`), codecs + classifier + hashing + path detector (`Content/`, §2.13), encrypted SQLite store + machine-bound store opener (`Storage/`; what each integration stored, its parts counted and deleted: `Storage/StoredData`, `ClipStore.Stored.cs`, `Services/ClipHistoryService.Stored.cs`, §2.43), key hierarchy (`Security/`: UUIDv5, HKDF machine binding, sealed key vault), capture pipeline (`Services/ClipHistoryService`; whether the Run and Snipping tabs have anything to show, `ClipHistoryService.TabContent.cs`, §2.36), command line (`Cli/`: protocol, pipe naming + framing, argument grammar, command processor, output — §2.9), Win+R list logic (`Integrations/RunMru`: parse, fingerprints, runs since a snapshot — §2.17), Windows screenshot rules (`Integrations/WindowsScreenshots`: the tool by a file name's shape, fresh writes, completeness from the bytes, Snipping Tool's saving settings — §2.22), Everything tab logic (`Everything/`: IPC wire format, queries, `Run History.csv`, merge and hide rules — §2.14), the prompt archive's logic (`Prompts/`: Claude Code and Codex parsers, keys, the incremental JSONL reader `JsonlTail` — §2.21; its tables in `Storage/ClipStore.Prompts.cs`), settings (incl. the remembered panel size, §2.23, and the tab last chosen, §2.28), logging and the memory planner (`Diagnostics/MemoryReclaim`: when to give memory back, §2.45) and the silent restart's rules (`Diagnostics/SilentRestart`: when the app replaces itself after Settings closed, §2.46), presentation helpers (incl. `Presentation/ThirdPartyCatalog`, the source of Settings › Third party, §2.20, `Presentation/SettingsSearch`, Settings' fuzzy search, and `Presentation/StoredText`, the *Delete stored…* dialog's words, §2.43, `Presentation/TabStripScroll`, the filter-tab carousel's arithmetic, §2.23, `Presentation/TabArrangement`, the tabs' order and hiding from their right-click menu, §2.40, `Presentation/CollectionPatch`, the panel list's remove/move/insert patching, §2.27, `Presentation/MergedTabOrder`, the time order of the tabs that list kept entries next to another app's items, §2.44, and `Presentation/PanelNavigation` and `Presentation/PointerSelectionGate`, the arrow keys' rules and when the mouse may select a row, §2.28 and §2.31, and for the update dialog `Presentation/Markdown`, the release-notes parser, `Presentation/UpdateText` and `Presentation/UpdateDialogLayout`, §2.29), the rule by which a newer copy takes over from an older one (`Startup/InstanceTakeover`, §2.48), the sign-in rules (`Startup/SignInStartup`: the user's own and the all-users "Start with Windows" entry, when a start from the latter steps aside, what the switch shows and changes — §2.34), and the update system (`Updates/`: GitHub's release list, SemVer precedence, which release is offered and which notes are shown, `SHA256SUMS.txt`, the release feed and its download-trust rule, the HTTP client, the update service with its schedule and cache, how a copy was installed, the installer contract, the `--set-update-check` command — §2.29; `Updates/UpdateReminder`, when the panel reminds of a waiting update — §2.30; `Updates/PortableUpdate`, which offers a portable copy installs in its own folder and its helper's command line — §2.48; `Updates/LocalBuildFeed`, the dev-builds folder's development builds read as releases and copied with a checksum check, and the dev rule in `UpdateCatalog` — §2.39), and the interface languages (`Localization/`: one catalog per language and its flag, embedded in the DLL, keyed by the English text; `UiText` finds them, looks strings up, words plurals and decides which languages are translated enough to offer, `UiLanguage`, `PluralRules` (CLDR's rules from each catalog's header), `LanguageChoice` for the language picker, asked until a language is chosen — §2.37), tracked documents (`Documents/`, `Content/TextLoss`, `Storage/ClipStore.Documents.cs`: an editor's unsaved buffer kept as one entry, updated in place or split into a new copy by the text it lost) and Notepad++'s files (`NotepadPlusPlus/`: session, backups, config, command line, the opened-files tracker — §2.41), and the rules for other copies of BetterClipboard on the PC (`Editions/`: `OtherEdition`, `EditionSetupPlan`, which picks the Microsoft Store edition's next setup step, and `EditionDataMigrator`, which copies another edition's history with checksums, proves the copy, and commits or rolls the move back; `AppPaths.ResolveDefault(packageFamilyName)` and `PackagedDataDirectory`, that edition's own data folder — §2.47), and a picture's file (`Model/SourceFile`, `Content/ImageFileFormat`, `Content/TempImageFiles`, `Storage/ClipStore.SourceFiles.cs`, `Services/ClipHistoryService.SourceFiles.cs`, `Presentation/SourceFileText`, `Presentation/ByteSizeText` — §2.49), and long texts (`TextView/`: the language detector, the syntax highlighter, the XML and JSON formatter, the RTF writer and its palettes, and the text viewer's sources, views and pages; `Presentation/Markdown`'s parse options — §2.50). **CS1591 = error.** |
| [`src/BetterClipboard.Windows`](src/BetterClipboard.Windows) | `net10.0-windows10.0.26100.0` | Everything OS: `Interop/` (LibraryImport P/Invoke, `MessageWindowThread`), `Diagnostics/ProcessMemory` (the process's memory counters, the reclaim and the working-set trim, §2.45), the silent restart's Windows side (§2.46: `Shell/QuietProcess`, a start without the busy pointer; `Input/UserActivity`, idle time and held keys; the shortcut hold `HotkeyService.HoldWithHookAsync`; the clipboard hand-over `ClipboardMonitor.Start(uint?)`; the tray icon's hidden start and `HandOverTo`), `Clipboard/` (listener/reader/writer, source attribution), `Input/` (hotkeys + WH_KEYBOARD_LL takeover of several shortcuts at once, the installer's `--set-hotkeys` command — §2.25, paste injection, placement, the panel's remembered size in pixels and DIPs — `FlyoutSizing`, §2.23), `Imaging/` (DIB math + WIC, PNG export for the CLI), `Import/` (DPAPI-NG, pinned store, WinRT history), `Shell/` (tray icon, Run key, Windows clipboard/Explorer settings incl. the `DisabledHotkeys` rules — `ExplorerHotkeys`, §2.25, user PATH, running a command like Win+R, the other BetterClipboard processes and a program's version — `RunningCopies`, `ProgramVersion`, §2.48), `Security/` (MachineGuid + SID, DPAPI key protector), `Cli/` (ACL'd named-pipe server), `Integrations/` (ShareX: locator, folder-pattern rules, screenshot watcher, integration life cycle — §2.10; Windows' screenshots: Screenshots-folder locator (known folder, Snipping Tool's package and saving settings), a folder watcher that never locks a writer out, integration life cycle — §2.22; Win+R history: `RunMRU` reader, change watch, integration life cycle — §2.17; voidtools Everything: IPC client, owner check (Authenticode, voidtools signer), install locator, integration life cycle — §2.14; the prompt archive's readers: agent folders, file access (shared, lock retries, NTFS file id, zstd), watchers + hot poll + reconcile on a background-mode thread — §2.21; Notepad++: the plugin-message client with its owner check (Authenticode, NOTEPAD++ signer), the locator, the unsaved-tabs watcher, the opened-files watcher, life cycle — §2.41), `Updates/` (the Installed-apps record of the running copy, the OS architecture, and `ScriptUpdateInstaller`, which takes `install.ps1` out of a verified package and runs it in update mode — §2.29; `PortableUpdateInstaller` and `PortableUpdateCommand`, which unpack the package and let the new version replace a portable folder's files, all or nothing — §2.48; `LocalBuildsWatcher`, which tells the update service when the dev-builds folder changed — §2.39), and the Microsoft Store edition's Windows side (§2.47: `Packaging/PackageIdentity`, whether the process runs from the Store package, one kernel32 call, also linked into `bclip`; `Packaging/PackagedRestart`, which asks Windows to start the app again after a Store update; `Shell/PackagedStartup`, the package's startup task; `Store/OtherEditionLocator`, the other copies on the PC; `Store/EditionUninstaller`, the checks before and the run of the package's own `install.ps1 -Uninstall` on a copy from the installer; `RunMruWatcher`'s polling mode), and a picture's file (§2.49: `Shell/FileOnDisk`, whether a file is still there without touching a network path; `Integrations/ScreenshotFileFinder`, the one-time search for earlier screenshots' files; `ImageCodec.ContentHashAsync`). **CS1591 = error.** |
| [`src/BetterClipboard.Cli`](src/BetterClipboard.Cli) | `net10.0-windows` console | `bclip`: parses arguments, gates on the app's `EnableCommandLine`, talks to the running app over the pipe (starting it if needed), prints text/JSON with exit codes (§2.9). Published self-contained next to `BetterClipboard.exe`; in the Microsoft Store package it is the app's console alias `bclip.exe` (an execution alias with an executable of its own) and reads that edition's data folder (§2.47). **CS1591 = error.** |
| [`src/BetterClipboard.App`](src/BetterClipboard.App) | `net10.0-windows10.0.26100.0` WinUI 3 | Windows App SDK **2.5.1** as component packages (Base/Foundation/InteractiveExperiences/WinUI/DWrite — the metapackage's AI/ML/Search/Widgets add ~57 MB we don't use), unpackaged (`WindowsPackageType=None`), `WindowsAppSDKSelfContained=true`, custom `Program.Main` (single instance + commands). `AppController` = composition root (`AppController.Memory.cs` gives memory back after each use, §2.45; `AppController.SilentRestart.cs` and `RestartBridge` replace the process, unseen, a few seconds after Settings closed, §2.46). Views: `ClipboardFlyout` (acrylic Win+V replacement), `SettingsWindow` (Mica, under Windows' own title bar since 2026-11-12, §2.45), `UpdateView` in an `UpdateDialog` flyout (the update dialog, §2.29), `LanguageWindow` (the language picker, shown until a language is chosen, §2.37; `Localization/TrExtension` and `FlagImages` serve XAML's labels and the flags). The panel's weekly update reminder and its keyboard guard for clicked tooltips are §2.30. The Microsoft Store edition (§2.47): `AppController.Store.cs` (its setup: other copies, the move, the welcome), `StoreSetupWindow` with `StoreSetupView` and `StoreSetupViewModel` (the setup's one window and its pages), `SettingsWindow.Store.cs` and `SettingsViewModel.Store.cs` (the cards that edition keeps), `Program.ContinueStoreEditionInBackground`, and `SingleInstance`'s Store marker and setup claim. A picture's file (§2.49): `AppController.ImageFiles.cs` (copy the path or the file, open it or its folder off the UI thread, *Save to temp and*, the start's clean-up) and `Views/ClipboardFlyout.ImageFiles.cs` (the card menu's items and the image viewer's band). Long texts (§2.50): `Views/ClipboardFlyout.TextPreview.cs` (the hover peek and the text viewer), `Views/TextPreviewPresenter.cs` (their RichEdit surface with its line-number gutter), `AppController.TextView.cs` (a card's whole text) and `ViewModels/ClipItemViewModel.TextView.cs` (when a card is cut off). |
| [`tests/BetterClipboard.Core.Tests`](tests/BetterClipboard.Core.Tests) | `net10.0` | xunit.v3 on Microsoft.Testing.Platform (1,893 tests, one class at a time — §4: long texts (§2.50, 107 cases: `TextLanguageDetectorTests` 62, `SyntaxHighlighterTests` 19, `StructuredTextFormatterTests` 9, `TextViewRenderingTests` 17 — code recognized without taking prose for it, each language's colors, XML and JSON re-indented with every token kept and no external entity ever resolved, every character through RTF, Markdown drawn, the views, pages); a picture's file (§2.49, 41 cases: `SourceFileTests` — the file kept on insert, replaced whole or kept on a bump, added by a bump after the copy, filled only when missing by an import; the columns added to an older store; the search's candidates and `SetSourceFile` filling only what is missing; the service's events; `bclip`'s `file`; the setting's default, clamp and round trip; the format from the first bytes (17 cases); a file described as read; temporary pictures' folders, names, reuse that never overwrites, expiry by last use and the clean-up; the menu's label and the viewer's facts and dates; sizes like Explorer (6 cases)); the Microsoft Store edition's rules (§2.47, 36 cases: `EditionTests` — the package's data folder and instance scope; the setup plan for every mix of copies, other data and own history; what a move lists, reads and needs; a whole move that brings a history over, proves it and removes the old folder last; a rollback that leaves the original; copies that are no history, or another machine's, refused; the welcome off until given); the silent restart's rules (§2.46, 14 cases: `SilentRestartTests` — restart at once when quiet; not wanted without Settings, while exiting, or when a language would be asked; each blocker named in the log's words; the pauses inclusive to the millisecond; the most lasting reason named first; what calls a started restart off, and that input does not); giving memory back (§2.45, 11 cases: `MemoryReclaimTests` — when a reclaim falls due, one reason's burst postponing it within two minutes while other reasons keep their own time, nothing while a window shows, the log's wording, and the one image format a picture's reader asks for, from a real store); the merged tabs' time order (§2.44, 24 cases: `MergedTabOrderTests` 19 — an old kept entry no longer leading, a recent one leading, a twin shown once at the newer time, pins only when asked, ties, pages that place each entry where its time falls and a twin pulling its entry to a later page, items without times, repeats, a deleted entry left out, arguments, and random lists keeping every rule in one page or many; 4 in `ShellTabTests` — the Cmd and Pwsh merges and the live cap; a kept prompt on real archive data in `PromptStoreTests`); Settings' search and *Delete stored…* (§2.43, 57 cases: `SettingsSearchTests` 40 — the query, every kind of match and what it refuses, letters in order never across a space, fuzzy matches only as the fallback, weights, highlights through embeddings and combining marks, the typo measure; `StoredDataTests` 17 — every source's parts, one-pass counting, the newest examples, deletion keeping pins, groups and other tabs and tombstoning, the service's preview, counts and deletion of archives and lists, shortcuts kept on one line); hiding a tab with its clipboard rows (§2.42, 6 cases: the setting's defaults, normalization and round trip; the rows left out of All, the kind tabs, Pinned and searches while grouped entries stay; two tabs at once; entries a filter cannot decide staying listed; the footer's count); the right-to-left languages and the descriptions for translators (§2.37, `RightToLeftTextTests` 44: Hebrew alone right to left, every Hebrew string embedded once or one of the few without Hebrew, values inside the embedding, English fallbacks untouched, `JoinLine`, the header's direction, `AsSentence` (with the ideographic full stop after a Han or kana title), a Latin word's trailing `+` and `#` kept with it (Notepad++ in Hebrew, and no shipped Hebrew string without its mark); every `en.json` row described, and the check itself on small catalogs; group names stored without direction controls); the tabs' arrangement (§2.40, 25 cases: the stored order completed and "All" kept first, moves past tabs that do not show and never past "All", what is stored, the settings' names, the XAML declaring one tab per filter); local dev builds (§2.39, 44 cases: the folder's reader — complete builds only, staging folders, names, checksum lists, architectures, bounds — the verified copy and its refusals, the owner's table of what is offered, release builds and release candidates, the notes of local builds, the words of a local build and of the way back, Chinese, no reminder for a development build, and the service: offered at start without a request, the folder followed, a changed package refused, the way back offered); the translation guards (§2.37, `TranslationCoverageTests` 14: every text and constant the code hands to UiText has a catalog entry and every entry is still used, both read from `src` by `CSharpSourceScanner` (next to `LocalizationTests.CodeAndCatalogsAgree`, below); Core's template lists in the catalogs; group icon names unique in every language; date patterns valid; `TranslateFormatted`, `DateCulture`, dates, the forgotten list, source names, update failures and the sign-in description in Chinese); the panel position (§2.38, 8 cases: `PanelPositionSettingsTests`); the interface languages (§2.37, 111 cases: the shipped languages and their headers, flags Direct2D can draw, English-text keys and hash keys, catalogs well formed with valid placeholders and what each lacks reported, the code and the catalogs agreeing (a lexer over every XAML and C# file), plurals in each language with English fallback, CLDR plural rules in five languages with their samples and broken rules refused, which languages are offered, Windows' language to a catalog with Traditional Chinese kept English, explicit choices, a text in a given language, when a start asks (until a language is chosen, also after an update from a version that never asked), `HasHistory`, relative times and bclip's tables English on request, and no log line built from translated text); the Run and Snipping tabs showing only with something in them (§2.36, 8 cases: the store's lookup of a tab's newest entry and of one entry, the history service through a first and a last run, a first import, Snipping Tool's pictures and text, a picture copied again from Paint, a clear, a start over a store that has both, a prune and Forget forever); Start with Windows for every user (§2.34, 31 cases: the entry formats, which program an entry names, what the switch shows for every mix of the user's entry, the entry for every user and Windows' marks, when a start from the entry for every user steps aside, each flip's change and all 192 flips reaching the asked state, the card's words, the setting, a copy for every user recognized before the per-user records, its update words and command); the weekly update reminder (§2.30, 26 cases: once a week to the second, a clock that was wrong, who is never reminded, the room over the window, the setting); the update system (§2.29, 265 tests: SemVer precedence, GitHub's release JSON incl. drafts and hostile input, which release is offered and which notes are shown, `SHA256SUMS.txt`, the download-trust rule and the stand-in feed's rules, how a copy was installed, the settings and `--set-update-check`, the wording of every state, the dialog's placement, the release-notes Markdown parser (80), the HTTP client against a fake handler (headers, entity tags, rate limits, size limits, stalls, partial files), and the service on a manual clock: schedule, cache, backoff, verification, the hand-over to the installer, cancellation, every failure); getting around the panel (§2.28: the row every load selects — the newest copy below the pins, only while they lead the list, also on the store's own pages — and Up from it reaching the lowest pin; tabs in a circle, held keys stopping at the ends, when Left and Right belong to the search text, the walk from the top row into the groups column and through it (§2.31: onto the first group shown or the clipboard icon, as a walk that shows its groups or one that only picks, when Space and Enter pick or go back with the group, when Down leaves from the lowest group, and held keys acting once), one group shown alone without hiding the shown one, the cursor having to leave its spot before the mouse selects, the remembered tab's name read leniently); the panel list's patching (§2.27: no change for an unchanged list, one insert and one remove for a new copy, one move for a re-copy, random reorders); the shortcuts in settings (§2.25: the main one plus extras, normalization, the cap, round trip, files from before the extras; §2.26: the history behind "Used before": order, cleaning, cap, round trip); the tab carousel's arithmetic and the remembered panel size (§2.23: arrow steps tab by tab both ways, order-independence, ends and out-of-range offsets, reveal, wheel and tilt, drag, clamps; defaults, persistence, clamping of a hand-edited size); the Snipping tab (§2.22: file names by shape, localized and right-to-left ones included, freshness, completeness per format, Snipping Tool's settings, the filter, the hybrid merge rules, pause and ignored apps, the "SnippingTool.exe" relabel, the CLI names, the setting); the prompt archive (§2.21: Claude Code and Codex parsers, key known answers, `JsonlTail` for appends, partial lines, truncation, trims, filters, replacement, CRLF, long lines and unseekable streams, the store's merges, Codex twin records in either order, tombstones, rewrites, forget, listing and search, checkpoints, schema on an older store, the service's pause/ignore/size rules and slices, `bclip prompts`/`prompt`); the Third party catalog (link wording, the official-link rule, every restored package credited, both directions of agreement with `THIRD-PARTY-NOTICES.md`); the Pwsh and Cmd tabs (§2.19); the Everything tab (IPC wire format incl. replies that lie about their size, queries, `Run History.csv` incl. a write cut off mid-path, merge and hide rules, the Everything filter and origin, forgetting files by path, pick formats, `-f everything`); Win+R list logic (parse, fingerprint known answers, runs since a snapshot for every kind of list change, planned captures), the run column, Run filter, merge rules and tombstones of both Win+R origins, a store from before the column, pause/ignore/Forget forever for runs, `-f run`; paths copied as text (a 234-case detector corpus: every form, prose, commands, URLs, escapes, whitespace; Files/Text filters; backfill and rules version), content, store, **encryption at rest**, key-hierarchy known-answer tests, CLI grammar/protocol/processor/output, one-time data fix-ups, password-manager catalog seeding, ShareX origin/filter/state semantics, groups: CRUD, membership filter, merged views of several groups (one list in the usual order, paging, search and toggles, the union count), the Ctrl/Shift click rules and their wording, kept-like-pinned retention, reset clock, schema added to an older store, service events, icon catalog; Forget forever: fingerprint normalization, known answers and chunking, the look-alike sweep, list life cycle, blocking across channels, Settings wording; search toggles: match case, VS Code's whole-word rule (punctuation edges, overlaps, runes), regex lines/engines/timeout, the prefilter superset, the SQL function inside real queries: order, paging, filters, groups, failures keeping their type, persisted toggles). |
| [`tests/BetterClipboard.Windows.Tests`](tests/BetterClipboard.Windows.Tests) | `net10.0-windows…` | Hotkeys, interceptor, placement, DIB/WIC, DPAPI-NG, synthetic pinned store, real DPAPI/MachineGuid, **clipboard capture in a private window station** (bursts, watchdog, echo, delayed rendering), CLI pipe server (real pipes: refusal of a 2nd server, hang-up, malformed input, 124-connection stress: 100 sequential + 24 parallel) + CLI end-to-end through the real monitor, user-PATH rules, flyout drag tracker, ShareX (pattern rules, locator against fake ShareX layouts, screenshot watcher on temp folders, integration marker life cycle over a real history), groups column growing/shrinking on the left, Forget forever end to end (a real copy of forgotten text is read and kept out), opt-in real-clipboard round trip, explicit capture-rate measurement, Win+R history on scratch HKCU keys (reader, settle wait, change watch incl. a key that appears later, integration: first import, live runs, re-runs, restart catch-up, off/on, pause, a missing list, keeping more than Windows' 26, a rescan racing the watch, runs from the panel) and Run-dialog parsing + hidden launches, Everything (the client against a fake IPC window in this process: trust, state, reply matching, latest-wins, deadlines, garbled replies, a hung window, the command line; the integration: live picks, the saved file while gone, loading or garbled, never an impostor; the owner check: other names, unsigned, another publisher; locator hints; quoting checked with `CommandLineToArgvW`; opt-in real Everything), the Pwsh and Cmd tabs (§2.19: the PowerShell source on temp files, and when its newest command was typed, §2.44; the helper's wire format), the prompt archive's readers on temp agent folders (§2.21: first import + watcher, rename-over prune, whose Codex threads, a writer that keeps its file open, archive move + zstd compression, the mandatory lock, pause and off/on, restart, file ids across moves), Windows' screenshots (§2.22: each tool's name, a writer reopening its file while the watcher polls, files copied or moved in, skips, renames, catch-up, a folder created later, the marker's life cycle, a clipboard copy and its file merging for a DIBV5 and a zero-alpha BI_RGB DIB, the locator, the display-name rule), the panel's remembered size (§2.23: pixels to DIPs and back at every Windows scale without drift, the groups column left out of the remembered width, clamping and bad scales, the per-scale minimum), several shortcuts (§2.25: the list parser, one hook for several gestures on one key, Explorer's `DisabledHotkeys` rules and the Settings card's plan, the `--set-hotkeys` command, real registrations of obscure keys and the probe the Settings card waits with), any key as a shortcut (§2.26: every usable code round-trips through its text, 30 spellings, 31 refusals with their reasons, the key kinds, the recorder's decisions), source attribution of owner-less copies (never this process), integrity levels for pasting into elevated windows (§2.5), the sign-in entries on scratch keys (§2.34: both entries and both marks read, odd values tolerated, writes that touch only BetterClipboard's values, plan and registry together, the PC's Installed-apps entry), and the update installer (§2.29: the Installed-apps record read from a scratch key, an isolated instance ignoring it, the installer script taken out of a package and refused when missing, empty, oversized or in a subfolder, the contract command line, and a stand-in installer run through the real Windows PowerShell: every argument whole in a folder name with a space, a quote, an ampersand, percent signs and a non-ASCII letter, the exit code, the temp working folder, and no `PSModulePath` from the caller), and the dev-builds folder's watcher (§2.39: the folder appearing, a build renamed into it, the folder deleted and created again) (445 tests, all in English whatever Windows' language; a picture's file, §2.49: `ImageFileTests` 4 — `FileOnDisk` on this PC's drives, the content hash equal to the analyzer's, the search matching by time then pixels, a file gone meanwhile — and `ShareXIntegrationTests.EarlierScreenshot_GetsItsFileOnce`, with the watchers' captures carrying their files: `TestLanguage`, §2.37; `TranslationTemplateTests` 6 check the Windows template lists in the catalogs and produce every shortcut problem, registration state and run result; `PanelPlacementTests` 27, §2.38; `ProcessMemoryTests` 2, §2.45: the memory counters, and a reclaim that collects a dropped array and trims, in a collection of its own that runs alone; the silent restart, §2.46: `SilentRestartTests` 4 — a quiet start of a console program and its end, refusals, the idle-time read, the shortcut hold refusing nothing to hold — and two take-over cases in `ClipboardCaptureTests`: a copy made while nobody listened is read at once, an unchanged clipboard is not; the Microsoft Store edition's uninstaller, §2.47: `EditionUninstallerTests` 13 — the check and the uninstall through the real Windows PowerShell against a stand-in `install.ps1` in a folder named with a space, a quote, an ampersand, percent signs and a non-ASCII letter; success proven by the program being gone; failures in their words; a folder this account may not change named; and the real `install.ps1`'s uninstall parameter set). |
| [`tools/`](tools) | scripts | `probes/` (research), `e2e/` (UI harness — see §4), [`release/package.ps1`](tools/release/package.ps1) (release zips + `.7z` archives + SHA256SUMS, shared with CI), [`release/package-chocolatey.ps1`](tools/release/package-chocolatey.ps1) / [`release/test-chocolatey.ps1`](tools/release/test-chocolatey.ps1) (the Chocolatey package and its real install test, §3.2), [`release/install-local.ps1`](tools/release/install-local.ps1) (installs those zips on this PC with the real installer before a release, §3.1), [`release/publish-dev.ps1`](tools/release/publish-dev.ps1) (builds a commit in a temporary worktree and publishes it to this PC's dev-builds folder, where the installed app's update button offers it; `-Install` bootstraps — §2.39, skill `local-dev`), [`release/test-update.ps1`](tools/release/test-update.ps1) (runs a package's `install.ps1` in update mode with the app's own command line, in scratch folders — CI and the release job run it, §2.29), [`release/test-iex.ps1`](tools/release/test-iex.ps1) (checks that `install.ps1`'s param block survives the README's `irm \| iex` in 5.1 and 7 — CI runs it, §3.1), [`release/test-all-users.ps1`](tools/release/test-all-users.ps1) (installs and uninstalls for every user for real, as administrator and as SYSTEM — CI or a disposable VM only, §2.34), [`launch_dev.py`](tools/launch_dev.py) (runs a copy of the dev build next to the installed app for the user to try, §3), [`make_icon.py`](tools/make_icon.py) (app icon), [`make_flags.py`](tools/make_flags.py) (the language flags, §2.37), [`localization/`](tools/localization) (`catalog.py`: add (one, or a file of many), add-plural, rename, remove, review and check UI strings, list a language's missing ones, convert hash keys; `smoke-windows.ps1`: the PR's UI smoke test in every language the catalogs hold, CI or a disposable VM only — §2.37), [`readme/`](tools/readme) (the README photos: demo data, window capture and finishing, and screen capture and composing for a flyout that leaves its window, for a claude-desktops Windows desktop, §3.4; [`seed-demo.cs`](tools/readme/seed-demo.cs) and [`demo-data.zip`](tools/readme/demo-data.zip), the Store screenshots' data — the owner's own history screened by hand — and its loader, §3.5; and [`check-translations.py`](tools/readme/check-translations.py), which compares each README translation with README.md section by section and through GitHub's renderer, and the Hebrew one's direction marks, §4), [`release/package-msix.ps1`](tools/release/package-msix.ps1) (the Microsoft Store package per architecture, checked after the build; unsigned for the Store, or signed with a test certificate for a test desktop — §3.5), [`release/submit-store.ps1`](tools/release/submit-store.ps1) (sends a release's Store packages to the Microsoft Store with Microsoft's submission API: the release run's last step, §3.5) and [`release/test-submit-store.ps1`](tools/release/test-submit-store.ps1) (that script against a stand-in service on this PC, 39 checks, in CI), [`release/check-store-listing.py`](tools/release/check-store-listing.py) (the listing kit's texts against the Store's limits), [`release/make-store-listing-csv.py`](tools/release/make-store-listing-csv.py) (fills Partner Center's exported listings file from the kit, each caption under its own picture: one import instead of some forty pasted fields per language; with `--check` it says whether an export is what the kit says), [`release/order-store-screenshots.js`](tools/release/order-store-screenshots.js) (puts a listing page's screenshots in order with the page's own drag-and-drop, for the browser tools: an import does not keep their order), [`release/store_listing.py`](tools/release/store_listing.py) (their shared reader of the kit, which also finishes a right-to-left listing's texts) and [`release/test-store-listing.py`](tools/release/test-store-listing.py) (52 checks, in CI), [`readme/screen-shot.py`](tools/readme/screen-shot.py) (rounds a window's corners in a whole-screen capture: the Store listing's pictures, §3.5), [`make_store_assets.py`](tools/make_store_assets.py) (the package's 35 logos and the listing's tile icon, from the app icon's own drawing). |
| [`packaging/chocolatey`](packaging/chocolatey) | nuspec / PowerShell | The `betterclipboard` Chocolatey package's template: install, before-modify and uninstall scripts, shared helpers, verification text (§3.2, [`docs/chocolatey.md`](docs/chocolatey.md)). |
| [`packaging/msix`](packaging/msix) | manifest / JSON / PNG | The Microsoft Store package's template: `Package.appxmanifest` (filled in by the build), `identity.json` (the values Partner Center assigned when the name was reserved, 2026-11-13), `Assets/` (the package's logos), `Listing/` (the listing's tile icon, its eight screenshots, and a pristine listings export that the listing tools are tested with) (§3.5, [`docs/microsoft-store.md`](docs/microsoft-store.md), [`docs/store-listing.md`](docs/store-listing.md)). |
| [`install.ps1`](install.ps1), [`.github/workflows/`](.github/workflows) | PowerShell / Actions | Installer from GitHub releases (§3.1) · CI (build, test, package, Chocolatey install test, a test-signed Store package, the Store scripts' tests) · release on `v*` tags (+ Chocolatey push, + the Microsoft Store submission, §3.5) · *Chocolatey package*, run by hand: a package-only fix for a released version (§3.2) · *Microsoft Store*, run by hand: check the Store's secrets, or submit a released version again (§3.5). |

Shared build config: [`Directory.Build.props`](Directory.Build.props) (docs on,
nullable, version; obj and bin folders of their own, under `artifacts/store-build`, for the Microsoft Store
package build of the app, §3.5), [`Directory.Packages.props`](Directory.Packages.props)
(central package versions), [`global.json`](global.json) (SDK 10.0.1xx +
`"test": {"runner": "Microsoft.Testing.Platform"}` — required: xunit.v3 4.x no longer supports VSTest on .NET 10).

### 2.2 Runtime topology (threads)

```text
UI thread (WinUI DispatcherQueue) ── AppController, ClipboardFlyout, SettingsWindow, view models
   ▲ TryEnqueue                     ▲ TryEnqueue              ▲ TryEnqueue            ▲ TryEnqueue
"Clipboard" STA msg-window     "Input" msg-window          "Tray" hidden top-level  "Commands" thread
 (AboveNormal priority)         RegisterHotKey / LL hook    Shell_NotifyIcon v4      named events from
 AddClipboardFormatListener     (hook callback = decide,    TaskbarCreated re-add    2nd launches
 read at once + 2 s watchdog    tap mask key, post, return)                          (--show-flyout/--exit)
 → ClipCapture
   │ TryEnqueueCapture
   ▼
History worker (single consumer Channel) ── classify → WIC analyze (thumbnail + pixel hash) → SQLite upsert → prune → Changed event

"Cli" named pipe (only while Settings › Command line is on) ── accept loop + ≤ 8 connections on the thread
 pool → CliCommandProcessor → reads via ClipHistoryService, writes via the worker, clipboard writes via
 ClipboardMonitor (IClipboardWriter)

"ShareX" FileSystemWatchers (only while ShareX is installed and Settings › ShareX screenshots is on) ── pool
 thread events → 250 ms debounce per path → one file at a time: wait for the writer, decode (WIC) →
 ClipHistoryService.AddAsync (the worker) → Handled → catch-up marker (state.sharex.last_seen_utc), §2.10

"Screenshots" FileSystemWatcher (only while Settings › Snipping Tool and Win+PrtScn is on) ── the Screenshots folder,
 not recursive → pool thread events → 250 ms debounce per path → one file at a time: read with every sharing mode
 granted until its bytes are complete, fresh-write check, decode (WIC) → ClipHistoryService.AddAsync (the worker) →
 Handled → catch-up marker (state.screenshots.last_seen_utc); the locator on the pool every 5 min, §2.22

"Win+R history watch" thread (only while Settings › Win+R history is on) ── RegNotifyChangeKeyValue on RunMRU
 (or its parent while missing) → Changed → 150 ms debounce → rescan on the pool under one gate: read once the key
 is quiet 150 ms → runs since the snapshot → ImportAsync / AddAsync (the worker) → snapshot (state.runmru.snapshot),
 §2.17. Entering the Run tab rescans too. Running a command: a short-lived STA thread per ShellExecuteEx.

"Everything" message-only reply window (only while Settings › Everything tab is on) ── the Everything tab's load
 runs on the pool: refresh the status (FindWindow, owner check cached per file version, state questions ≤ 500 ms
 each) → QUERY2 sent from the pool (WM_COPYDATA, ≤ 2 s to accept) → the LIST2 reply arrives here as WM_COPYDATA,
 is copied and matched by reply id → the awaiting load (3 s deadline; a newer load cancels it) → merge with the
 stored half on the pool → UI. Not running, loading or garbled: Run History.csv read on the pool instead, §2.14

"Claude Code prompts" / "Codex prompts" threads (each while its Settings switch is on; Windows background mode) ── one
 pass at a time: watcher paths (debounced 200 ms), the hot poll (every 5 s: files written within 6 h opened, real length
 vs checkpoint), the reconcile (start, every 5 min, watcher overflow) → JsonlTail reads only the new bytes → prompts in
 slices of 1,000 through the history worker (ClipHistoryService.IngestPromptsAsync) with the file's checkpoint, §2.21

"Updates" timer (pool threads; only while the copy checks by itself) ── a tick 60 s after start, then hourly: when the
 last successful check is older than 24 h (or a failed one's retry time has come) → GET GitHub's release list with
 If-None-Match → cache (updates\releases.json + releases-state.json) → a new UpdateSnapshot → Changed → UI (the update
 button). An approved update: a pool thread downloads to updates\<zip>.part (SHA-256 while streaming) → verify →
 powershell.exe install.ps1 -Update in its own process, which closes the app, swaps its folder and starts it, §2.29

Notepad++ unsaved tabs (only while Settings › Notepad++ unsaved tabs is on) ── FileSystemWatchers on its settings and
 backup folders (pool threads) → a change only marks the tab → a 5-s timer reads the tabs quiet for 1 min (at most 5 min
 after the first unread change) → ClipHistoryService.RecordDocumentsAsync (the worker: one transaction per version), §2.41

"Notepad++ files" message-window thread (only while Settings › Files opened in Notepad++ is on) ── a WinEvent hook on each
 running Notepad++'s title + a 5-s scan → plugin messages (SendMessageTimeout, 500 ms) → an opened file's path →
 ClipHistoryService.AddAsync (the worker), §2.41
```

Rules: nothing heavy on the hook thread (Windows silently drops slow LL hooks); the clipboard thread only
copies bytes then closes the clipboard ASAP; all writes are serialized through the history worker; reads
(`QueryAsync` etc.) hit SQLite directly (WAL) from the thread pool.

### 2.3 Capture pipeline

1. `WM_CLIPBOARDUPDATE` → snapshot **source attribution now** (owner window, else foreground window —
   short-lived producers like scripts exit quickly — but never one of BetterClipboard's own windows: an owner-less copy
   made while its panel or Settings is in front is "Unknown app", not "BetterClipboard", since its own writes always have
   an owner window; `SourceAppResolver.ResolveForegroundGuess`, found in the 2026-10-09 QA pass) → **read immediately**
   in the handler (no debounce:
   the old 60 ms debounce merged any copies < 60 ms apart; §1.9 has the measured capture rates).
   Staged producers (formats added in a 2nd session) are harmless: same text ⇒ same hash ⇒ one entry,
   and "latest copy wins" keeps the complete format set.
2. Skip if the sequence number is unchanged (state already read — counted as **superseded**) or equals our
   own last write (echo suppression; that number is read after `CloseClipboard`, which bumps it by 2).
3. `OpenClipboard` with timer-based retries (10 ms × 50; a newer notification cancels the retry and reads
   the newest state). The authoritative sequence number is re-read while holding the clipboard. **Privacy
   markers checked before reading any content**: `ExcludeClipboardContentFromMonitorProcessing`,
   `Clipboard Viewer Ignore`, `CanIncludeInClipboardHistory` = 0 (unreadable value ⇒ treated as exclude).
   Accounting (`ClipboardMonitor.Statistics`, shown in Settings › Capture reliability) — once idle:
   `Notifications == Read + SelfWrites + LockedOut − Recovered + Superseded`. Watchdog: every 2 s, a
   sequence number unhandled for a whole period with no notification ⇒ capture it, re-register the
   listener, log a warning (`WM_TIMER` is only generated when no posted message waits, so a queued
   notification always wins).
4. Portable formats first (text, file list + drop effect, HTML, RTF, URL, PNG, the *original* DIB flavor),
   app-private formats only with `PreserveAllFormats`, all under a byte budget checked via `GlobalSize`
   **before** copying. GDI handle formats and synthesized duplicates are never stored.
5. Worker: rules (pause, ignored apps — pre-seeded with the password-manager catalog, §2.8 — size) → `ContentClassifier` (Files > Text[Link/Color/Rich] > Image
   > rich-only; text that is nothing but paths also gets a path count, §2.13) → image analysis (dims, 720×400 PNG thumbnail, **pixel hash** — images dedupe by decoded
   pixels, so DIB-vs-PNG encodings of one picture merge) → `ClipStore.Upsert` → retention prune.

### 2.4 Win+V takeover (`Input/HotkeyService`)

`RegisterHotKey` first; on `ERROR_HOTKEY_ALREADY_REGISTERED` (Win+V ⇒ Explorer) and
`UseKeyboardHookFallback`, a `WH_KEYBOARD_LL` hook runs `HotkeyInterceptor`: exact-modifier match only,
swallow key-down/repeats/key-up of `V`, tap unassigned VK `0xE8` so releasing Win doesn't open Start, post
to the input window, return. Our own synthetic events carry `dwExtraInfo = 0x0B0CC11B` and are ignored;
keys injected by *other* tools (PowerToys KBM, AutoHotkey) are intercepted like physical keys. Quitting the
app unhooks → Win+V instantly returns to Windows (verified with `probe_hotkeys.cs`). "Release from Explorer"
(Settings, and what `install.ps1` does by default since 2026-10-03) writes `DisabledHotkeys` and
restarts Explorer; `RegisterHotKey` then succeeds and no hook is needed — works over elevated windows too,
but Win+V is dead while the app is not running (uninstall restores it) **[verified 2026-09-25, §1.5]**.
The log names the path: `… registered with RegisterHotKey …` vs `… intercepting it with a keyboard hook`.
Since 2026-10-03 any number of shortcuts open the panel, each wired this way, one hook serving all that need it, and the
installer can leave Win+V to Windows (§2.25).

### 2.5 Summon & paste flow

Hotkey → `ForegroundContext.Capture()` on the input thread (target HWND + Win32 caret via
`GetGUIThreadInfo` + cursor + a Stopwatch timestamp, `CapturedAt`) → UI at **High** dispatcher priority: the panel,
already drawn with its list current while it was cloaked, is moved to the caret (or where *Settings › Shortcut › Panel
position* says: next to the mouse, or centered on the main or the current screen, §2.38), activated and uncloaked in one
frame (§2.27; focus to the search box; keystrokes must never leak into the app below); only a list that is not current is
reloaded afterwards → Enter/click → `ReplayFormats.Prepare` (plain text strips formats; file lists as plain text become
paths; a stored "cut" drop effect is rewritten to "copy") → write clipboard while still foreground → **re-activate the
target while we still own the foreground, then conceal** (`Conceal` hides the window once, which hands the foreground
on; doing that first loses the right to set focus) → wait for foreground → release held modifiers (mask-key first for
Win/Alt) → inject Ctrl+V.

**The first summon of a session** (fixed 2026-10-09, `0718a9b`, `db9750c`; found in the QA pass).
- **Typed keys went nowhere.** `ShowAt` focuses the search box before the XAML tree is loaded (a silent no-op), and
  `OnActivated`'s fallback runs during `Activate()`, also too early. UIA showed the focus on the window's
  `InputSiteWindowClass` pane. Now `SearchBox.Loaded` focuses the box while the panel shows, and `ShowAt` focuses it after
  the first reload when `FocusManager.GetFocusedElement` is still null.
- **Typed keys leaked into the app below.** The window and its tree were built on the first Win+V. Measured on a VM
  after a `--background` start (letter sent 100 / 250 / 500 ms after Win+V): Notepad / Notepad / search box. Now
  `AppController.WarmUpFlyoutAsync` builds the panel 3 s after startup at low priority and `ClipboardFlyout.WarmUp`
  renders it once off every screen (-32000, `AppWindow.Show(false)`, no activation) and hides it ("Panel prepared for the
  first Win+V in 265–363 ms" there). Then: 100 / 250 / 500 ms → search box; 30 ms still leaks on that first summon
  (10 ms on later ones), faster than people type after a chord. `IsOpen` is false while only the warm-up holds the
  window visible, so a Win+V in that moment opens the panel; a summon during the warm-up keeps it shown.
  - Since §2.27 (same day) the warm-up shows the window **cloaked** at the size the next summon will use, loads the list,
    and leaves it shown for good; `IsOpen` is the `presented` flag. A summon before the warm-up activates the cloaked
    window at once and uncloaks it once its tree has loaded and drawn (≤ 500 ms + 2 frames): "Panel on screen 573 ms after
    the shortcut … (first summon)" on the VM, where the old path showed an empty window for that time.

**Enter waits for the search** (2026-10-09, QA pass). The search runs 120 ms after the last key, and Enter pasted whatever
was selected then: "delta" + Enter at once pasted the newest item ("echo"). `ReloadAsync` now keeps its task
(`currentReload`) and the text the shown list was loaded for (`loadedSearchText`, set by `MarkLoaded` unless a newer load
took over); Enter runs `PasteBestMatchAsync`, which awaits `FlyoutViewModel.SettleSearchAsync` (a pending search runs at
once; a running load is awaited) and pastes the armed match: the first one, or since 2026-11-08 the first one below
the pinned matches while pins are listed first (§2.28). A second Enter while it waits is ignored; a panel dismissed
meanwhile pastes nothing. Ctrl+1…9 and clicks still act on what is shown.

**Pasting into an elevated window** (2026-10-09, QA pass, UAC on). A non-elevated BetterClipboard's Ctrl+V is dropped by
UIPI, and `SendInput` still reports all four events as injected (Microsoft documents that neither the return value nor
`GetLastError` shows it), so the old "blocked" check never fired and the paste silently did nothing.
`PasteInjector.PasteIntoWithOutcomeAsync` compares integrity levels first (`ProcessIntegrity`: Medium 0x2000, High
0x3000), after activating the window: for a higher target it injects nothing and returns `PasteOutcome.TargetElevated`;
`AppController.InjectPasteAsync` then shows a tray notification once per window and session ("Copied: press Ctrl+V to
paste"). Verified on the VM: Win+V over an elevated Notepad (High) opens the panel (`RegisterHotKey` path), Enter
re-activates that Notepad and leaves the item on the clipboard. An elevated BetterClipboard still pastes into elevated
windows.

**Moving the flyout** (2026-09-25, on request: dragging the background should move it like a regular
window). It has no title bar, so any background press can drag it.
- **What counts as background** (`ClipboardFlyout.IsDragSurface`): walk from the pressed element up to
  `Root`. Buttons, text boxes, `AutoSuggestBox`, `SelectorItem` (cards), `SelectorBarItem`, `RangeBase`,
  `Thumb` and `ToggleSwitch` are not background. Everything else is: title, icons, chip, footer, gaps, and
  the empty space of the list and the filter bar. For touch and pen the whole list is excluded, because
  there they pan it. The filter bar's strip (`TabsScroller`) is excluded for every pointer while it can scroll,
  because a drag there pulls the tabs sideways (§2.23); while every tab fits, its empty space moves the window.
- **Setup:** `Root` needs `Background="Transparent"`, or its empty areas aren't hit-testable. The press
  handler is registered with `handledEventsToo`.
- **The drag itself:** `CapturePointer` + `WindowDragTracker` (pure, unit-tested: 4-DIP threshold, 10 for
  touch; full offset after the threshold) + `AppWindow.Move`. Esc mid-drag moves the window back and only
  ends the drag. `ShowAt` ends any leftover drag. The next summon re-anchors at the caret.
- **Pitfall 1, the system move loop:** in a WinUI window, the classic `ReleaseCapture` +
  `SendMessage(WM_NCLBUTTONDOWN, HTCAPTION)` **never ends**. WinUI takes mouse input as pointer messages,
  so the loop never sees the button-up and the window stays glued to the cursor until the next click.
  Verified live: after a header drag, a plain cursor move of (−50, +44) plus a (+100, 0) drag moved the
  window by (+50, +44).
- **Pitfall 2, window-relative positions:** a pointer position inside the moving window feeds the window's
  own movement back into the drag. Screen positions come from Win32 instead (`ScreenPointer`:
  `GetCursorPos` for the mouse, `GetPointerInfo(pointerId)` for touch and pen). No screen position means
  no drag, never a guessed one.
- **Verified:** `tools/e2e/drag.py` on an isolated instance, 4/4 checks in 4 consecutive runs. Touch and
  pen are untested (no hardware here).

### 2.6 Storage (`Storage/ClipStore`, SQLite3 Multiple Ciphers via Microsoft.Data.Sqlite.Core, `stores\{id}\history.db`)

`clips` (one row per content hash, preview, search text ≤ 32 K chars, recency, pin, origin, source app,
thumbnail, path count — §2.13) · `clip_formats` (raw payloads by name, cascade) · `clips_fts` (FTS5 **trigram**, external
content, triggers — substring search incl. Hebrew/CJK; < 3-char terms use escaped `LIKE`) ·
`deleted_hashes` (tombstones: a deleted item is never resurrected by the next Windows import; a new live
copy lifts the tombstone) · `meta.last_clear_utc` (imports older than the last clear are skipped) · the prompt
archive's `prompts`, `prompts_fts`, `prompt_files`, `prompt_tombstones` (not history, §2.21).
WAL, `synchronous=NORMAL`, `auto_vacuum=INCREMENTAL` + `incremental_vacuum` after prunes, schema version
in `PRAGMA user_version` (newer ⇒ refuse, never downgrade). Merge rules: live duplicate ⇒ bump + replace
formats (latest copy wins); import duplicate ⇒ untouched except adding a pin.
Engine: `Microsoft.Data.Sqlite.Core` 10 + `SQLite3MC.PCLRaw.bundle` 2.4 (built on SQLitePCLRaw 3.x while
MDS 10 targets 2.1 — verified compatible by the tests; re-verify when bumping either). The `.Core` package
ships no engine, so `ClipStore`'s static constructor calls `SQLitePCL.Batteries_V2.Init()`. Every
connection sets `foreign_keys=ON`, `synchronous=NORMAL` and **`temp_store=MEMORY`** (temp spill files are
not covered by the cipher). `Open()` disposes a connection whose setup pragmas fail: with a wrong key they
are the first statements to touch page 1, and a leaked connection kept the file open and broke quarantine.

### 2.6.1 Encryption at rest & machine binding (`Core/Security`, `Storage/MachineBoundHistory`) **[verified]**

```text
MachineGuid (HKLM\SOFTWARE\Microsoft\Cryptography, 64-bit view) + user SID
   └─ HKDF-SHA256(ikm="machine:<guid D>", salt="BetterClipboard/MachineBinding/v1", info="user:<SID upper>")
        = 32-byte binding (never stored; zeroed after use)
            ├─ storeId = UUIDv5(fixed BetterClipboard namespace, "store:" + hex(SHA-256(binding)))
            └─ DPAPI CurrentUser entropy
random 32-byte DEK ──DPAPI(CurrentUser, entropy=binding)──► stores\{storeId}\history.key (JSON, "dpapi-user")
DEK (hex passphrase → MDS Password → PRAGMA key) ──SQLite3MC ChaCha20-Poly1305──► stores\{storeId}\history.db
```

- The DEK is random (not derived) so it can be re-sealed later (export password) without re-encrypting.
  Known-answer tests (independent Python HKDF/uuid5) pin the derivation: **changing the salt or labels
  orphans every store** — only ever with a migration.
- `MachineBoundHistory.Open`: resolve the store folder → adopt the v0.1 plaintext `DataDirectory\history.db`
  (moved with its `-wal`/`-shm`, sidecars first) → unseal or create the key → `ClipStore.Initialize`
  encrypts a plaintext file in place (`journal_mode=DELETE`, checked, then `PRAGMA rekey` with MDS's
  `quote()` literal). Key unavailable (`HistoryKeyUnavailableException`) or database unreadable
  (`SQLITE_NOTADB` 26 → `HistoryUnreadableException`) ⇒ **quarantine**: rename the folder to
  `{storeId}.unreadable-yyyyMMdd-HHmmss[-n]` and start fresh — never delete (renaming it back on the
  original machine and account recovers it).
- Threat model: protects data at rest (stolen disk, backups, other accounts incl. admins without the
  password), not against malware running as the same user (true for every DPAPI consumer). Footguns: an
  admin *resetting* a local account's password loses its DPAPI keys; a Windows reinstall or unprepared disk
  clone changes MachineGuid ⇒ new empty store (the old folder stays untouched). `MachineIdentity.ToString()`
  is redacted so the identifiers never reach a log.
- Real-app migration check: copy of a v0.1 plaintext data dir → log `adopted legacy: True, encrypted legacy
  in place: True`; reopened through real DPAPI: 0 of 16 original entries missing; no plaintext header left.

### 2.6.2 Search toggles — `Storage/SearchOptions`, `SearchMatcher`, the search box's Aa / W / .*

User request (2026-10-01): "The search box, i want to add more icons on the right, toggleables: "Aa" for
case-sensitive (default off), ".*" for regex (default off), "W" for whole word".

**Semantics** (`SearchMatcher`, pure, unit-tested). All three off is the classic search, unchanged (words
ANDed, trigram index + `LIKE`, no matcher at all).
- **Words** (no `.*`): the classic split (whitespace, ≤ 8 words). Duplicates are dropped case-sensitively
  under Aa, so "Foo foo" needs both. Each word is found with `Ordinal` (Aa) or `OrdinalIgnoreCase`.
- **Whole word**, VS Code's rule on each side: the text's edge, or a neighbor that is not a word character,
  or a match whose own edge character is not one. So `.cs` stands alone in "file.cs" and `-v` in "run -v",
  where `\b` fails.
  - Word characters: letters, decimal digits, Mn/Mc marks, connector punctuation (`_`: "log" is not whole in
    "my_log", but is in "my-log"). Read as runes, so a letter outside the BMP is one letter.
  - A literal word retries one char later (overlaps count: "a-a" in "xa-a-a" at 3). A regex retries after
    the match, like VS Code's global search: retrying one char later would make a long greedy run quadratic.
- **Regex**: the whole text is one .NET pattern (spaces included, not trimmed): `CultureInvariant` +
  `Multiline`, plus `IgnoreCase` unless Aa.
  - CRLF and lone CR become LF first, so `^`/`$` work per line of a Windows text. The price: a pattern that
    spells out `\r` finds nothing.
  - `NonBacktracking` first, linear in the text, so `(a+)+b` cannot hang. Lookarounds, backreferences, atomic
    groups, conditionals and `\G` throw `NotSupportedException` there (probed); they fall back to the
    backtracking engine with a 250 ms timeout per match attempt, which surfaces as `SearchTooSlowException`.
  - A pattern that does not parse throws `SearchPatternException`; its `Reason` drops .NET's
    "Invalid pattern '…' at offset N." preamble ("Not enough )'s.").
- Entries without text (images) never match, not even `.*`.

**Store** (`ClipStore.Query(query, token)`).
- The matcher is compiled before the connection opens, so a broken pattern fails before any database work.
- The index narrows what it can (`SearchQueryBuilder.BuildPrefilter`, which must be a superset):
  - the classic trigram terms (the index folds case, and a whole word is also a substring);
  - a short word's `LIKE` only when it is ASCII or caseless (`LIKE` folds ASCII only, so "éa" would drop
    "ÉA" and is left to the matcher alone; Hebrew keeps its `LIKE`);
  - nothing for a pattern.
- Then a SQL function, `bc_search_match(c.search_text)`, is the last predicate. SQLite still orders, pages and
  stops at `LIMIT`, and its sorter holds only the small entry columns of matches.
- **Verified with a probe:** Microsoft.Data.Sqlite drops a function when the connection goes back to the pool
  (the next rent says "no such function"), so it is registered per query and no query can reach a stale
  matcher. An exception thrown inside it comes back as SQLite error 1 with only its message, so the closure
  keeps the original (`OperationCanceledException`, `SearchTooSlowException`) and the query rethrows it.
- `ClipHistoryService.QueryAsync` passes its token into the function: a superseded scan stops at the next entry.
- **Cost** (probe, 10,000 synthetic items, encrypted, 44 MB, Release): toggled searches 1–55 ms. A regex that
  matches nothing scans everything in ~46 ms; the classic search for a common word takes ~58 ms. Expect
  ~0.5 s per 100,000 items for a full scan.
- Only the indexed `search_text` is searched (≤ 32 K chars), like the classic search.

**UI** (`ClipboardFlyout`, `FlyoutViewModel`, `App.xaml`).
- **Inside the box:** the toggles are the `AutoSuggestBox`'s `Description`. `SearchBoxTextBoxStyle` is WinUI
  2.3.9's `AutoSuggestBoxTextBoxStyle` template, copied with one more column between the clear (X) button and
  the magnifier: `[text][X][Aa][W][.*][🔍]`. The X appears only while there is text, to their left, so the
  toggles never move. Order as in VS Code and Rider. Keep the copy in step when upgrading WinUI.
- **`SearchOptionToggleStyle`:** a 24-DIP text toggle. On = accent outline, accent glyph and a subtle fill (VS
  Code's look). `AllowFocusOnInteraction=False` and `IsTabStop=False`, so a click keeps the caret in the box.
- **Keys:** Alt+C / Alt+W / Alt+E in `Root_PreviewKeyDown` (Visual Studio's keys), Alt without Ctrl: AltGr
  arrives as Ctrl+Alt and types letters on many layouts.
  - **Not VS Code's Alt+R:** GPU overlays register it globally. Here `RegisterHotKey(Alt+R)` fails with 1409
    (NVIDIA Overlay runs; AMD's Radeon overlay defaults to Alt+R too). The panel's log showed Alt arriving and
    R never (2026-10-01).
- **Remembered** in settings (`SearchMatchCase`, `SearchWholeWord`, `SearchUseRegex`, all off by default), like
  an IDE's find box; the text is still cleared on every summon. A change reloads at once (no debounce), and
  only when there is search text, so an empty box keeps its scroll position and selection.
- **Empty states name the toggles:** "Nothing in your history contains “foo” (whole words, match case)." and
  "… matches “^\d+$” (regular expression)". An invalid pattern shows "Not a valid regular expression", the
  reason and "Turn off .* (Alt+E) to search for these characters as they are."; a slow one has its own title.
  Neither is logged as an error: an unfinished pattern is normal while typing.
- **Red outline on an error** (user request 2026-10-01: "On regex error, make the circle around regex red"):
  - When: while the pattern does not parse or ran out of time. `FlyoutViewModel.HasPatternError` is set by
    `ShowSearchProblem` and cleared by the next load that succeeds and by every summon.
  - Not cleared when a load starts: while an invalid pattern is being typed, the outline would flicker back to
    blue for every keystroke's debounce.
  - How: the ".*" toggle swaps to `SearchOptionToggleErrorStyle` (x:Bind `RegexToggleStyle`), which only sets
    `BorderBrush` to the theme's `SystemFillColorCriticalBrush`. Measured from the outline's pixels: #FF99A4 dark,
    #C42B1C light (the accent outline: #4CC2FF dark, #0067C0 light).
  - The toggle template draws the outline as an overlay border, colored by the style and shown by the checked
    states' `Opacity`. The first version blanked the brush in the unchecked states instead, and failed (§4).
- **UI Automation:** three buttons with `TogglePattern` named "Match case", "Match whole word" and "Use regular
  expression", with their `AcceleratorKey`. The search box's `HelpText` stays empty: the `Description` content
  is not read as a description.

**Not covered:**
- `bclip search` keeps the classic search (`bclip grep` already takes a regex).
- Any caller that builds its own `ClipQuery` gets the classic search unless it passes `SearchOptions`. That
  includes the Everything tab (§2.14): `FlyoutViewModel.LoadEverythingRowsAsync` queries its stored half itself, and
  Everything's picks are searched by Everything. Joining them up means passing `CurrentSearchOptions` there and
  mapping the toggles to Everything's own match-case / whole-word / regex search flags.
- Cards do not highlight the matches.

### 2.7 Importing Windows' history (`Import/`)

At startup (setting, default on) and on demand: WinRT `GetHistoryItemsAsync` (text/HTML/RTF/bitmap/files;
bitmaps re-encoded as PNG + DIBV5) + decrypted on-disk pins (`WindowsPinnedStore` + `DpapiNg`); WinRT
items are marked pinned when their timestamp (second precision) matches a pin. Deduped by hash,
never reorders existing history. Imported items carry **no source app** (Windows does not record the
producer): v0.1.0 labelled them "Windows clipboard history", which the cards showed as if it were an app;
`ClipStore.ApplyDataFixups` clears that label once per store (marker `meta` key `fixup.import_source.v1`,
only rows with origin import, no source path and exactly that name), and the card caption shows no
source for imports ("Unknown app" is reserved for live copies whose producer could not be identified).

### 2.8 Data & privacy decisions

Data dir: `%LOCALAPPDATA%\BetterClipboard\` (`stores\{id}\history.db` + `history.key`, `settings.json`,
`logs\betterclipboard-*.log` with 14-day retention, `installer.json` from `install.ps1`, `updates\` with the
updater's cached release list and, during an update, its download — §2.29); override with env
`BETTERCLIPBOARD_DATA_DIR` (tests, dev runs; the installer honors it too — **always use it when
experimenting** so the real history stays clean). An override also **scopes the instance** (§2.9): its own
single-instance lock, `--exit`/`--show-flyout` events and pipe, so an isolated run coexists with the
user's installed app instead of signalling it. Everything is encrypted at rest (§2.6.1); Windows itself
encrypts only pins. Logs never contain clipboard content, keys, the binding or the identifiers. The Microsoft Store
edition keeps the same files in its package's own folder,
`%LOCALAPPDATA%\Packages\<family>\LocalState\BetterClipboard`, which Windows removes with the app (§2.47).

**The network** (since 2026-10-09, §2.29). The update check is the only request the app makes by itself: once a
day, GitHub's public release list, with nothing but the app's version in the request. `AppSettings.CheckForUpdates`
(on by default) and `install.ps1 -NoUpdateCheck` switch it off; Chocolatey copies and isolated instances never check
by themselves. Everything else that uses the network is asked for by the user: *Check now*, *Update and restart*,
and links that open in the browser. The README's *Privacy* section is the privacy policy and must name every such
use (§4). The Microsoft Store edition makes no request by itself at all: it has no update service (§2.47).

**Password managers are ignored by default** (`Core/Settings/KnownPasswordManagers`, added 2026-09-25 on
request: "Add to Ignored apps all known password managers you can find").
- **Why it's needed:** the privacy markers (§1.7) cover only apps that set them, and many managers
  (Electron-based ones especially) don't.
- **What's in it:** 46 products (42 password managers + 4 authenticator apps, since OTP codes are
  credentials too), 65 process names including helpers (tray agents, browser-integration hosts, old major
  versions: 1Password 7 = `AgileBits.OnePassword.Desktop`, 4 = `Agile1pAgent`).
- **Evidence:** every name was checked, not guessed. Open-source apps: their build files read through
  `gh api`:
  - Electron `productName`/`executableName` (Proton Pass is `ProtonPass.exe` on Windows only);
  - Flutter `BINARY_NAME` (AuthPass `authpass`, Yubico `authenticator`, Ente Auth `auth`);
  - Qt `TARGET` (`qtpass`);
  - MSBuild `AssemblyName` (Passbolt `passbolt`).

  Others: Brave Search over file.net / process databases / vendor docs. The per-name sources are in
  [`docs/password-managers.md`](docs/password-managers.md), which also lists what can't be covered:
  - browser-extension managers (the copy is attributed to the browser);
  - CLI tools (attributed to `clip`/the terminal);
  - Ente Auth's too-generic `auth`;
  - versioned or unverifiable names.
- **Seeding without a migration:** `AppSettings.Normalize` runs `KnownPasswordManagers.Seed`. Every catalog
  name not yet in `AppSettings.SeededIgnoredApps` is appended once (unless a user entry already covers it,
  e.g. `keepass.exe`) and recorded as seen.
  - Result: new and existing installs get the catalog, a deleted name stays deleted, and names added to
    the catalog later arrive exactly once. No revision number to remember to bump.
  - Footgun: an older version saving the file drops `SeededIgnoredApps`, so the next newer version offers
    the whole catalog again.
- **Settings UI:** *Add known password managers* (`AddMissing`) deliberately restores deleted names too.
  The text box scrolls (`MaxHeight` 240) because the list alone is ~65 lines.

### 2.9 Command line (`bclip`) — `Core/Cli`, `Windows/Cli/CliPipeServer`, `src/BetterClipboard.Cli`

Purpose: let terminals, scripts and AI agents list/search/grep the history, read an item exactly, put an
item (or new text) on the clipboard, and wait for the next copy. **Off by default** (`AppSettings.EnableCommandLine`):
while on, any process running as the user can read the whole history through it — Settings says so.

- **Client/server, never a second DB opener.** The store is single-writer (history worker) and its key is
  DPAPI-sealed; `bclip` asks the running app over a named pipe and the app stays the only process holding
  the key. The pipe exists only while the setting is on (toggled live from `OnSettingsChanged`).
- **Pipe security.** Name `BetterClipboard.Cli.<hex(SHA-256("BetterClipboard/CliPipe/v1:" + SID upper))[..16]>.<session>[.<scope>]`
  (per user and session, SID not readable from it). Server: owner = user, allow the user
  ReadWrite|CreateNewInstance|Synchronize, **deny NETWORK**, `FirstPipeInstance` on the first instance (if
  someone squatted the name, `Start` throws and Settings shows the error). Client: `CliClient.VerifyServerOwner`
  — pipe owner must equal the token's **user** SID before anything is written (else `CliPipeOwnerException`
  ⇒ exit 3). **Not** `PipeOptions.CurrentUserOnly`: .NET compares the owner with the token's *default
  owner*, which is `BUILTIN\Administrators` for an elevated admin — the first CI run (GitHub runners test
  elevated) failed all 8 pipe tests with "not owned by the current user", and an admin terminal would have
  been refused by its own app. `Owner_IsTheUser_EvenWhenElevated` guards it (it logs whether the token was
  elevated). ≤ 8 instances, 64 KB buffers.
- **Protocol v1.** One request line, one response line: newline-delimited JSON (source-generated
  System.Text.Json, camelCase, nulls omitted), each line bounded at 64 MB (`CliWire.ReadLineAsync` refuses
  more instead of buffering). Client deadline = `wait` timeout + 30 s, else 2 min. A pending 1-byte read
  detects the client hanging up (Ctrl+C on `bclip wait`) and cancels the handler.
- **Commands** (`CliCommandProcessor`, pure over `ClipHistoryService` + `IClipboardWriter` + `IImageExporter`, unit-tested):
  `list`/`search` (`QueryAsync`, not pinned-first, 1–1000, `--since` = `UsedSince`; `-f files` also lists
  text that is nothing but paths: JSON `paths: N`, table kind `path`, §2.13); `grep` (.NET regex,
  CultureInvariant, **250 ms match timeout** ⇒ bad_request, newest 10,000 items' search text, full text
  reloaded when the indexed copy hit the 32 K cap, ≤ 20 matches/item, lines cut at 400 chars); `get`
  (auto/text/html = CF_HTML fragment by byte offsets/rtf/files/png = stored PNG or DIB → WIC/formats);
  `copy` (`ReplayFormats.Prepare` like a paste, then MarkUsed when MoveToTopOnPaste); `put` (UnicodeText to
  the clipboard + `AddAsync` with source "bclip"; not recorded while paused); `pin`/`unpin`/`delete`
  (latest by default; delete needs an id or `-r`); `wait` (`Changed`: Added, or Updated with LastUsed ≥ the
  start — a re-copy of an existing item; 1–3600 s); `status` (version, counts, capture statistics, archived
  prompts); `prompts` / `prompt` (the prompt archive, not history items: its own ids, §2.21).
  Ids are the numbers `list` shows, or `-r N`; no `#1` syntax (`#` starts a comment in bash/PowerShell).
- **Output.** `get`/`wait` print the payload byte-exact (a final newline only for a human's terminal);
  everything else is line-terminated. Images never go to stdout (exit 2 with a hint; `-o file.png`).
  `--json` everywhere. Exit codes 0 ok · 1 nothing found/timeout · 2 usage · 3 unreachable/off/not ours ·
  4 other · 130 Ctrl+C. Terminal: `Console.OutputEncoding = Unicode` (⇒ `WriteConsoleW`, console code page
  untouched); redirected: UTF-8 without BOM; stdin UTF-8 with BOM detection, `put` trims one trailing newline.
- **Gate + auto-start.** `bclip` reads `settings.json` with `SettingsStore.TryReadSnapshot` (read-only,
  never quarantines — unlike `Load`) and exits 3 when the setting is off. If the pipe does not answer in
  800 ms and the instance's mutex does not exist, it starts `BetterClipboard.exe --background` from its own
  folder via **ShellExecute** and retries for 20 s. (With `UseShellExecute=false` the long-lived app
  inherited bclip's stdout, so `bclip status | grep` hung until the app exited — found in the e2e run.)
- **Instance scoping** (`AppPaths.InstanceScope`): null for the default data dir, else
  `dir-<hex(SHA-256(UPPER(path)))[..12]>`; scopes the mutex, command events and pipe name. A scoped instance
  does not install the LL hook while the default instance runs (it would steal Win+V from the real app).
  Lesson (2026-09-25): before scoping existed, an e2e script's `--exit` shut down the user's installed app.
- **Audit + PATH.** Each command logs `Command line: <command> (client <process> pid N)` — never arguments or
  content. Settings › *Add bclip to PATH* (`Shell/UserPath`: REG_EXPAND_SZ read/written unexpanded,
  `WM_SETTINGCHANGE "Environment"`) or `install.ps1 -AddToPath`; uninstall removes the entry.
  - **Footgun (found 2026-11-08, §4):** the entry is the whole install folder, 200+ DLLs, and Windows' DLL search
    falls back to the PATH: ShareX had loaded BetterClipboard's `DWriteCore.dll` from it and held it for days. A
    PATH folder with only `bclip` in it is a next step (§6).
- **Race lesson.** The accept loop once did `Task.Run(() => ServeAsync(listener))` and then reassigned
  `listener` to the next, unconnected instance — the lambda captured the *variable*, so ~1 in 3 test runs
  served the wrong pipe and a client write blocked forever. Found with `dotnet-dump analyze` → `dumpasync`;
  fixed by capturing a local; `ManyConnections_AreAllServed` fails with the bug reintroduced.

### 2.10 ShareX screenshots — `Windows/Integrations/`, `ClipOrigin.ShareX`, `ClipFilter.ShareX`

User request (2026-09-25): every image ShareX takes should be in the history immediately, and ShareX gets
its own tab when it is installed. ShareX's source was cloned to `refs/ShareX` (commit 94838f6, 2026-09-24;
git-ignored, **GPL-3.0: studied for interoperability, nothing copied**). Facts below are **[verified from
that source]** unless marked.

**How ShareX saves a screenshot.**
- `WorkerTask` writes the image to its final path right after capture, **before** any upload.
- `History.db` (SQLite: `History(Id, FileName, FilePath, DateTime, Type, Host, URL, …)`) gets its row only
  when the whole task completes, and only with `HistorySaveTasks` on (default) and `HistoryCheckURL` off
  (default).
- So the file is the earliest reliable signal, and the integration watches folders, not the database.

**Where ShareX saves** (reproduced by `ShareXLocator.Resolve`, pure over `ShareXLocatorInputs`):
- **Personal folder**, first match wins:
  1. a `Portable` file next to `ShareX.exe` → `<exe dir>\ShareX`;
  2. the registry value `SOFTWARE\ShareX\PersonalPath` (HKLM, then HKCU);
  3. `PersonalPath.cfg` next to the exe, then in `Documents\ShareX`, then the legacy `%LOCALAPPDATA%\ShareX`
     (relative entries are relative to the exe folder);
  4. `Documents\ShareX`.
- **Screenshots parent** (`AppPaths.ScreenshotsParentFolder`): with `UseCustomScreenshotsPath`, the expanded
  `CustomScreenshotsPath` is used if `CustomScreenshotsPath2` is empty or the primary exists; otherwise the
  fallback if it exists. Else `<personal>\Screenshots`.
- **Folder of one capture** (`TaskHelpers.GetScreenshotsFolder`): a task's `OverrideScreenshotsFolder` +
  `ScreenshotsFolder` pattern (in `DefaultTaskSettings` in `ApplicationConfig.json`, or per hotkey in
  `HotkeysConfig.json`, whose path `CustomHotkeysConfigPath` can move). Otherwise `parent\` + the parsed
  `SaveImageSubFolderPatternWindow` (when set and a window title is known) or `SaveImageSubFolderPattern`
  (default `%y-%mo`). Then `GetAbsolutePath`: `%SpecialFolder%` names, environment variables, and
  relative paths against the exe folder.
- **Name tokens** (`NameParser`): plain, case-sensitive `StringBuilder.Replace` of `%token`s in a fixed
  order. `%t`/`%pn` stay literal when unknown, `%width`/`%height` become "", `%n` is replaced only in text,
  never in paths, and `{…}` holds arguments (`%ra{10}`, `%rf{file}`).
- **Other facts:** thumbnails are saved as `<name><ImageSettings.ThumbnailName>` (default `-thumbnail`); the
  installer is Inno Setup with AppId `82E6AC09-0FEF-4390-AD9F-0DD3F5561EFC`, so the uninstall key is
  `{AppId}_is1`, which has `InstallLocation`. Other uninstall entries named ShareX (Steam) and a running
  `ShareX.exe` are accepted too.
- **Never read:** `UploadersConfig.json`, which holds upload credentials. Settings files are opened with
  `FileShare.ReadWrite|Delete`. Bad JSON degrades to ShareX's defaults.

**Folder rules** (`ShareXFolderRule` = fixed root + relative pattern → a `NonBacktracking` regex).
- **Why patterns:** users point ShareX at broad folders (`Pictures`), and a plain recursive watch would
  import every image any app saves there (phone sync, downloads).
- **Token mapping:** date/time tokens become digit classes; free-text tokens become `[^\\]*`; arguments are
  skipped; everything else is literal. `%y-%mo` → `^\d{4}-\d{2}$`.
- **The root** ends at the first *real* token (`FirstTokenIndex`). A literal `%` (`D:\100%\Shots`) must not
  end it, or the watch widens to the whole drive.
- **Watch set:** `MinimalRoots` watches each root once, and never one inside another.

**Watcher** (`ShareXScreenshotWatcher`).
- **Watching:** recursive `FileSystemWatcher`s (64 KB buffer) on the existing roots; Created, Changed and
  Renamed events are debounced 250 ms per path.
- **Reading:** only once the writer is done — an open with `FileShare.Read` fails while ShareX still holds
  its write handle. Retries run up to 10 s.
- **Skipped:** thumbnails, non-images, folders outside the patterns, animated GIFs (`GetFrameCountAsync` > 1:
  a ShareX screen recording) and files over the item-size limit.
- **What is stored:** the PNG byte-for-byte plus a DIBV5 (`ToClipboardFormatsAsync`), with source
  `SourceAppInfo("ShareX", exe, "ShareX")` and origin `ShareX`.
- **Duplicate events:** a path|size|mtime key (pre-checked before decoding) stops the several events of one
  save from importing it twice.
- **`Handled` fires for stored *and* deliberately skipped files.** A screenshot taken while paused must not
  come back through the catch-up. Storage failures leave the marker alone, so the next catch-up retries.
- **Lost events:** a watcher `Error` means events were lost, so it runs a catch-up.

**Store semantics.**
- The origin is a hybrid:
  - Like a live copy: pause, ignored apps (`ShareX`) and the size limit apply, and a duplicate is bumped.
    The pixel hash merges the file with the clipboard copy ShareX made of the same picture.
  - Like an import: it never lifts a tombstone, and it is skipped when older than the last clear. The
    catch-up can rediscover a file the user deleted from history.
- **The tab's filter:** origin ShareX, **or** a source path ending `\ShareX.exe`, **or** the source name
  `ShareX`. The last two cover ShareX's clipboard copies, whose row keeps origin Captured when it came
  first.
- **Integration state** lives in `meta` under `state.<name>` (`ClipStore.Get/SetStateValue`). Names are
  validated, and the prefix keeps them away from `last_clear_utc`/`fixup.*`. It lives in the encrypted
  store, not `settings.json`, so it resets with a quarantined store.

**Life cycle** (`ShareXIntegration`, owned by `AppController`).
- **Catch-up marker** `state.sharex.last_seen_utc`, the newest handled write time (monotonic, enqueued in
  order under a lock):
  - first activation: the marker is "now", so no backfill of an archive (the write is awaited, so
    `StartAsync` returns with it stored);
  - each start: files newer than the marker, **at most the 100 newest**, oldest first;
  - setting off: the watch stops and the marker is cleared, so switching it on again starts fresh.
    Exiting the app is not "off".
- **Re-location:** on `*.json` writes in the personal folder (1 s debounce; non-recursive, since backups
  live in a subfolder), when Settings opens, and every 5 minutes (a new install, a folder that did not
  exist yet). When the watched folders change, a catch-up picks up whatever was saved there meanwhile.
- **UI:**
  - The flyout's `ShareXFilter` tab is visible only while `controller.ShareX.IsInstalled`. It falls back to
    All when it disappears while selected.
  - Settings › Integrations › *ShareX screenshots*: a toggle plus a status line (found via…, watched
    folders or "waiting for the folder", screenshots added this session).
  - CLI: `-f sharex`, origin `sharex`.
- **Tab width** (measured with GDI, then checked with UI Automation):
  - The problem: seven labels at WinUI's 12 px item padding need 393–408 px, but the bar has 384 (400-DIP
    flyout − 24 padding + 8 negative margin), and it does not scroll. The first screenshot showed "Shar".
  - The fix: 9 px padding (implicit style in `SelectorBar.Resources`); the tabs need 351–366 px now.
  - Measured afterwards: the ShareX tab is 61 px wide with 28 px to spare.
  - Superseded 2026-10-01 (§2.23): the tabs scroll sideways (a carousel) and the panel is resizable; the 9 px
    padding stays, so more tabs show before the strip has to scroll.
- **Limits:**
  - Captures that ShareX only uploads or only copies are not files. The copies still arrive through
    normal capture, into the same tab.
  - A ShareX installed while BetterClipboard runs shows up within 5 minutes, or when Settings opens.
  - The Microsoft Store (MSIX) build of ShareX is not verified.
  - Cost: each screenshot keeps PNG + DIBV5 (a 1080p DIB is 8 MB) and counts against the size budget like
    any image copy.
- **Dev/test:**
  - `BETTERCLIPBOARD_SHAREX_DIR` points the locator at a fake personal folder and skips the registry, so
    the real ShareX is never read.
  - Never trigger real ShareX captures: its after-capture tasks may upload the user's screen.
  - Verified 2026-09-25 (§5), headless through `bclip` next to the user's app.
  - Note: a test instance *without* the override variable finds the user's real ShareX, reads its settings
    and watches its folders (seen in the groups e2e run). Screenshots are skipped there while capture is
    paused, and the scratch store is deleted afterwards.

### 2.11 Groups — `ClipGroup`, `ClipStore` groups, the flyout's groups column

User request (2026-09-25):
- a bookmark button right of Pause opens a left area by widening the panel to the left;
- that area is a vertical row of font icons: the clipboard icon at the top (it and the "Clipboard" label
  slide left when the area opens) shows the regular view, and "+" at the bottom lets the user pick an icon
  for a new group;
- cards dragged onto an icon join that group, a right-click removes one from a group, and picking a group
  shows only its items;
- a grouped item is not deleted, like a pinned one, and when it is ungrouped from all groups it "reenters
  the retention cycle from reset".

**Data** (`Core/Storage/ClipStore`, `Core/Model/ClipGroup`).
- **Tables:** `groups(id, name, glyph, sort_order, created_utc)` and `clip_groups(clip_id, group_id,
  added_utc)`. The primary key is `(clip_id, group_id)`, both sides cascade, and `ix_clip_groups_group`
  serves the group filter and counts.
- **Additive, unversioned:** `EnsureGroupsSchema` runs at every `Initialize` inside the migration
  transaction (`CREATE … IF NOT EXISTS`; the column is added after a `pragma_table_info` check).
  - Why no `user_version` bump: an older build must still open the file.
  - The cost: while an older build runs, its retention does not know groups exist.
- **Entries:** `ClipEntry.GroupIds` comes from a `group_concat` subquery in `EntryColumns`, parsed and sorted.
  `ClipQuery.GroupIds` filters with `EXISTS (… group_id IN (…))`: one group, or several merged (each entry
  once, see "Several groups at once" below). It combines with filter tabs, search and the search toggles.
- **Validation:** 1–40 characters for the name (trimmed); the icon must be exactly one Private Use Area
  character (`ClipGroup.IsValidGlyph`), since anything else renders as a box in the 36-DIP button.

**Retention semantics.**
- **"Protected" = pinned or grouped** (the `Unprotected` SQL predicate). Retention by age, count and size
  skips protected entries, and `ClearUnpinned` keeps them. `ClearAll` deletes them, while the groups stay,
  empty. An explicit delete always works, and memberships cascade.
- **"Reenters the retention cycle from reset":** the nullable `clips.retain_from_utc` is set when an entry
  leaves its last group (`RemoveFromGroup`, or `DeleteGroup` for the members of no other group).
- **Retention key:** `max(last_used_utc, coalesce(retain_from_utc, 0))` (`RetentionKey`) for age and for
  the count and size order. So an item grouped long ago is neither pruned for its old age nor last in line
  right after ungrouping.
- **List order:** `last_used_utc` is not touched, so the item keeps its place in the list. The choice was
  deliberate: resetting last-used would have moved it to the top.

**Service** (`ClipHistoryService`):
- the write APIs `Create/Update/DeleteGroupAsync`, `AddToGroupAsync(ids, group)` and
  `RemoveFromGroupAsync` go through the worker; `GetGroupsAsync` reads on the pool;
- events: `GroupsChanged` for the column, `Changed` `Updated` per entry whose membership changed (in-place
  badges), and one `Reset` when a group is deleted;
- `AppController.GroupsChanged` forwards the event to the UI thread.

**Panel** (`ClipboardFlyout`, `FlyoutViewModel`).
- **Column:** `GroupsColumn` is 0 or 44 DIP (a 36-DIP icon plus an 8-DIP gap).
  - The window grows on its left (`FlyoutPositioner.ExtendLeft`, pure and unit-tested; it grows right
    only at the work area's left edge), so the list, search box and buttons stay put on screen.
  - The logo button grows from 24 to 36 DIP, so it tops the icon column. The title lands on the main
    content's left edge, 56 DIP.
  - `PlayGroupsPaneAnimation` slides the logo and title from their old screen position (window shift plus
    the in-window shift of 6 and 12 DIP) and fades the column in.
  - The state is kept in `AppSettings.ShowGroupsPane` and toggled by `Ctrl+G` as well. Closing the column
    leaves a group view.
- **Toggle icon:** Segoe Fluent Icons has **no bookmark ribbon** (E8A4 "Bookmarks" draws a bulleted list;
  checked by rendering every mapped glyph E700–F8CC). The toggle is therefore a 16-DIP `PathIcon` ribbon:
  an even-odd outline while the column is closed, filled while it is open.
  - Both `PathIcon`s carry `Width="16" Height="16"`, the design box; the ribbon (x 3–13, y 1.5–14.25) is
    centered in it. Without that box the ribbon was drawn 2 px right of and ~0.8 px below the button's
    center (reported 2026-10-01; the `PathIcon` sizing trap is in §4).
- **Icons:** `GroupIconCatalog`: 64 glyphs, each verified by rendering, with names that become the default
  group name. Code points are escapes; the raw-character lesson is in §4.
- **States are whole styles** (`GroupButton[Selected|DropTarget]Style`, `IconButtonActiveStyle` in
  App.xaml). A brush looked up in code from `Application.Resources` would ignore the flyout's own
  `RequestedTheme`.
- **Views:** clicking an icon shows that group; clicking it again, or the logo, returns to everything. A
  summon always starts in the regular view (unlike the filter tab, which is remembered since 2026-10-09, §2.28).
  Ctrl+click and Shift+click show several groups at once (below). The arrow keys reach the column too: Up from
  the top row (§2.28).
- **Header:** the header reads "Clipboard › Name". `TitlePanel` is a grid whose name column is `*` only
  while it has text, so the "Paused" chip always fits (a StackPanel cut it to "Pau"). The placeholder reads
  "Search in Name…" and the footer "N in Name".
- **Drag and drop:**
  - `ItemsList.CanDragItems` is on only while the column is open.
  - `DragItemsStarting` stores the ids and marks the package with the custom format
    `BetterClipboard.ClipIds` (Copy).
  - Group buttons (`AllowDrop`) accept only that format, with the caption "Add to Name" and a highlight.
  - `Drop` copies the ids before awaiting, because `DragItemsCompleted` clears them.
  - Other apps and our own text boxes see no format they understand, so the drop is refused.
- **Menus:**
  - Card: "Remove from Name" in a group view ("Remove from Work and Home" in a merged one), plus a *Groups*
    submenu of toggles (the keyboard and screen-reader route).
  - Group icon: Add to view / Remove from view (only while other groups are shown), Rename… (flyout), Change
    icon… (the picker), Delete group (confirm; it says the items stay).
  - Follow-up flyouts are queued through `DispatcherQueue`, so they open after the menu closed. Each one
    counts in `openPopups`, so the flyout does not dismiss itself.
  - **Keys inside popups** (`IsPopupKey`, found in the e2e run): a flyout's popup is parented to its
    placement target, so every key typed into it tunnels through `Root_PreviewKeyDown` first. Esc on a card
    menu closed the whole panel (the log showed `Dismiss` called from `Root_PreviewKeyDown` with a
    `MenuFlyoutItem` as the source). Enter or Delete in a group-name box would have pasted or deleted the
    selected card. The key model now skips a key while one of our popups is open, or when its source sits
    inside any open popup (`VisualTreeHelper.GetOpenPopupsForXamlRoot` lists the windowed menus too; that
    also covers the search box's own Cut/Copy/Paste menu). `ShowAt` clears a stale count, which would
    otherwise mute every key.
  - **Menu key / Shift+F10** open the selected card's menu. The same key also becomes a context request for
    the search box's inner `TextBox`, which handles it itself before it bubbles (it reached Root already
    handled), so its "Paste" menu closed ours at once. Moving the focus to the card first did not help, and
    a `ContextRequested` handler on the search box never ran. So the text box's `ContextFlyout` is taken away
    until the card menu's `Closed` (`MuteContextFlyoutUntilClosed`; a style value comes back via
    `ClearValue`). A right-click in the search box still gets the text box's menu.
- **Cards:** a card shows its groups' glyphs in the header row, with the group names in a tooltip.

**Several groups at once** (user request 2026-10-01: "Use can hold (ctrl or shift) and multi-select groups which
merge by the order we have, search applying to the selected groups and rest of necessary integrations").
- **Clicks, Explorer's selection keys** (`Core/Presentation/GroupSelection`, immutable, pure, unit-tested):
  - plain click: that group alone; on the only group shown, back to everything (the old rule); on one of several
    merged groups, that group alone;
  - Ctrl+click: add the group or take it out; taking out the last one is the regular view;
  - Shift+click: the run of groups (column order) from the anchor, the last group clicked without Shift;
    Ctrl+Shift+click adds the run. A Ctrl+click that takes a group out still moves the anchor there (Explorer
    does too). The anchor is dropped with the last group shown, and when its group is deleted.
  - The icon menu's *Add to view* / *Remove from view* (accelerator text "Ctrl+Click") is the route for touch,
    pen and screen readers (an invoke is a plain click). Offered only while some other group is shown.
  - The modifiers are read in the button's `Click` (`InputKeyboardSource`), like a card's Shift+click.
- **"Merge by the order we have":** a union in the list's usual order, never group by group.
  - `ClipStore.Query` adds one `EXISTS (… group_id IN ($group0, …))` predicate (`BindIds` binds the ids). An
    entry in two of the groups is one row; `PinnedFirst` and recency order the whole list; paging stays a plain
    offset.
  - The search, its Aa / W / .* toggles and the filter tabs apply to the merged list as they do anywhere.
  - `GroupSelection.Ids` follow the column's order whatever the click order. That order shapes texts only.
- **Texts** (`Core/Presentation/GroupViewText`, pure, unit-tested):
  - header: every name, "Clipboard › Work + Home + Ideas". It trims early: next to the "Paused" chip "Work + Home"
    read "Work +…" (e2e screenshot), so the title's tooltip names them all (`GroupTitleTooltip`);
  - placeholder "Search in Work + Home…" and footer "5 in Work + Home": names while they fit (≤ 3 groups,
    ≤ 24 characters of names together), else "4 groups". The footer's count sits in an `Auto` column next to the
    key hints, so two 40-character names would clip the count itself. One group is always named, as before.
  - empty state "Nothing in Work or Home yet" / "Nothing in Work or Home contains “foo”." ("these 4 groups"
    when they don't fit), and "… drag cards onto their icons";
  - card menu "Remove from Work and Home": takes the card out of every shown group it is in, one after the other
    (the last removal restarts its retention clock if it is then in no group).
- **Footer count:** `ClipStore.CountInGroups` = `count(DISTINCT clip_id)`, through `CountInGroupsAsync` (the ids
  are copied on the caller's thread). The groups' own counts would count a shared entry twice. A result for a
  view that changed during the await is dropped.
- **Highlights:** every shown icon gets the selected style, and its UI Automation `ItemStatus` reads "Shown"
  (screen readers can't see the highlight; UI tests read it). The tooltip's second line and the help text say
  "Ctrl+click or Shift+click to show several groups together".
- **Keeping the view right:** `FlyoutViewModel.GroupSelection` replaces `SelectedGroupId`.
  - A new selection with the same ids (only the anchor moved) does not reload.
  - `LoadGroupsAsync` drops deleted groups (`Retain`) and refreshes the footer: a membership change reaches the
    list before the groups reload, so the one-group footer used to keep the old count after "Remove from Work".
  - `TryApplyInPlace` takes a card out only once it is in none of the shown groups.
  - Deleting a shown group takes only it out of the view (`Without`).
  - Closing the column, the logo and every summon go back to the regular view.
- **Not built:** `bclip list --group` (the store takes `ClipQuery.GroupIds` already); keeping a merged view across
  summons; keyboard selection of groups beyond Space/Ctrl+Space on a focused icon (not verified).

### 2.12 Forget forever — `ForgetFingerprint`, the `forgotten` table, `ClipHistoryService.ForgetAsync`

User request (2026-09-25): "a feature 'Forget Forever' which is using a hash of the clipboard/clipboard
content to detect if we should never keep this kind of clipboard copy in this app and so it is never
recorded."

**The fingerprint** (`Core/Content/ForgetFingerprint`).
- **Text:** SHA-256 over `N\n` + UTF-8 of the normalized text: `\r\n` and lone `\r` become `\n`, then
  leading and trailing whitespace is trimmed (`char.IsWhiteSpace`, i.e. `string.Trim()`).
  - Why normalize: `ContentHasher.ForText` (the dedupe key) is exact on purpose. A rule that must never let
    something back in cannot be: the same secret arrives with and without a trailing newline depending on
    the app (terminals, editors copying a whole line).
  - Everything else must match, letter case included. Whitespace-only text falls back to the exact content
    hash, or forgetting one blank copy would keep out every blank copy.
- **Other kinds** use their existing identity: file lists by their paths (already case-insensitive), images
  by the analyzer's **pixel hash** (the check runs after image analysis), rich-only content by its bytes.
- **Hashing:** chunked (16 K chars, a stateful UTF-8 `Encoder` carries a surrogate over a chunk boundary),
  so a 50 MB text is not encoded into one buffer. A partly consumed chunk throws instead of storing a
  fingerprint later copies would never match.
- **Frozen:** known answers from Python `hashlib` pin the scheme (`ForgetFingerprintTests`). Changing it
  would silently un-forget everything, and it cannot be migrated: the content is gone by design.

**Storage** (`ClipStore`, additive like the groups: an older build opens the file but records forgotten
content again while it runs).
- `forgotten(id AUTOINCREMENT, fingerprint UNIQUE, kind, text_length, file_count, image_width,
  image_height, source_app_name, forgotten_utc, blocked_count, last_blocked_utc)`. Content-free except the
  fingerprint, which never leaves the store (not in `ForgottenColumns`, never logged).
- **Encrypted store, not `settings.json`:** a bare SHA-256 of a short password can be guessed offline.
- **`Forget(id)`**, one transaction: recompute the fingerprint from the stored formats (re-classified, so a
  file list whose formats also carry text is still fingerprinted by its paths; images use the stored
  content hash = pixel hash), `INSERT OR IGNORE` (an already-forgotten fingerprint keeps its first entry and
  counters), delete the entry **and its stored look-alikes**.
  - Why the look-alikes: the cards trim whitespace, so `secret` and `secret\r\n` look identical. Leaving
    one would look like the forget failed.
  - The sweep: SQL narrows (`instr` with the normalized text's first line, then the same normalization in
    SQL over `search_text`, with `trim(…, $ws)` given exactly .NET's whitespace set), then C# verifies each
    candidate's fingerprint from its full stored text. Only texts up to the 32 K `search_text` cap are
    swept.
  - Pins and groups do not protect: forgetting is an explicit request. No tombstone is written (the list
    is stronger and never expires).
- **No clear or prune touches the list.** Only "Allow again" (`RemoveForgotten`) and "Allow all again"
  (`ClearForgotten`) do.

**Capture path** (`ClipHistoryService.StoreAsync`).
- After pause, ignored apps, size, classification and image analysis, before `Upsert`. Every channel is
  covered: live copies, ShareX screenshots, `bclip put`, and the Windows import.
- `anyForgotten` (worker-thread cache; `null` = recount on next need) skips fingerprinting entirely while
  the list is empty, so the feature costs nothing until used.
- Only **new events** (Captured, ShareX) count as "kept out" (`RecordForgottenBlock`) and log
  `Skipped a copy that was forgotten forever.`. The Windows import offers the same old item at every start;
  counting it would inflate the number.
- The single worker orders everything: a copy queued right after "forget" is processed after it and kept
  out (`ACopyQueuedRightAfterForget_IsKeptOut`).
- Events: `Changed` `Removed` per deleted entry, and `ForgottenChanged` (forwarded to the UI as
  `AppController.ForgottenChanged`), also when a copy was kept out, so Settings' counters move live.

**UI and CLI.**
- **Card menu:** *Forget forever…* (Blocked `E733`) under Delete, queued like the group follow-ups. A
  confirmation flyout at the card (`ShowForgetFlyout`) is worth the click: the effect outlives the item, and
  only Settings can undo it. `ForgetItemAsync` moves the selection to a neighbor like `DeleteItem`.
- **Settings › Privacy › Forgotten forever:** the list (`ForgottenText.Title`/`Details`, pure and tested:
  "Text · 20 characters", "From Notepad · forgotten just now · not copied since"), *Allow again* per row
  (the button's accessible name says which row), *Allow all again…* with a confirmation. The stats chip
  adds "· N forgotten".
- **`bclip forget <ID | -r N>`:** needs an explicit target like `delete`. It reports look-alikes removed.
  There is deliberately no CLI "allow again": undoing is the user's call, in Settings. `status` has a
  `Forgotten` count (printed only when > 0), and `put` says when its text was not saved.

**Limits.**
- Windows' own clipboard history is not touched (it forgets unpinned items on restart anyway). The
  forgotten item is only never re-imported.
- Matching is exact apart from line endings and surrounding whitespace: `secret1` is not `secret`.
- Look-alikes of texts over 32 K characters are not swept (they are still kept out when copied).
- Images too large for the analyzer to pixel-hash are matched by their bytes, so the same huge picture in
  another encoding is not recognized.

### 2.13 Paths copied as text in the Files tab — `Content/PathDetector`, `clips.path_count`

User request (2026-10-01): the Files tab showed only file lists (`CF_HDROP`); text such as `folder/file.cs`,
`C:/a/path`, `/var/path/file` "and other variations like \ and confident whitespace detector (no mistakes)"
should be listed too. Design rule: **precision first**. A wrong "yes" puts a sentence or a command into the
Files tab; a wrong "no" only leaves a path in the Text tab, where it is listed anyway.

**What counts** (`PathDetector.GetPaths`/`CountPaths`, pure; class remarks are the full spec).
- **Shape:** every non-empty line is one path, or tab-separated cells that each are one (a table row).
  Surrounding whitespace and the kind of line ending don't matter; one line that is not paths ⇒ not paths.
  Quotes `"…"` (Explorer's "Copy as path"), `'…'`, `` `…` ``, `“…”`, `‘…’` around a path are removed. Texts
  over 16,384 chars (`MaxTextLength`, half the search cap) are never paths.
- **Never split at spaces:** paths in a row after spaces are a command line (`./lint.sh src/a.sh`,
  `C:\x.exe C:\in.txt`). Found by the corpus scan below; the first version split them.
- **Strong anchors suffice:** drive (`C:\`, `C:/`), UNC (`\\server\share`, server ≥ 2 chars: `\\n\\t` is an
  escape), device (`\\?\`, `\\.\`), `file:` URI, `~/`, `./`/`../`, `%VAR%\`, `$env:VAR\`, `${VAR}/`. Doubled
  separators are allowed there (escaped JSON/C#).
- **`/a/b`:** at least 2 segments, not all digits, no spaces in the first segment (slash commands:
  `/load-file docs/a.md`). A single-letter root needs a file name (`/r/programming`, `/c/Users` refused;
  `/c/notes.txt` counts). Regex literals (`/abc/gi`) are refused. URL routes (`/api/v1/users`) count.
- **`\a\b`:** a file name at the end or ≥ 3 segments; refused with any single-letter segment (`\d\w`), only
  escapes (`\x41\x42`) or a LaTeX command (`\alpha\beta`).
- **Bare relative:** at least 2 segments and a file name at the end. That is an extension in one case
  (`.cs`, `.JPG`; `items.Count` and `obj.toString` are code), a dotfile with a lowercase letter
  (`.gitignore`; `.NET` is not), or a well-known bare name (`Makefile`, `LICENSE`, …). Refused:
  - a host in front (`example.com/x.html`, IPv4, `localhost`);
  - `=` or `-` in front (`PY=/c/…`, `-Iinclude/x.h`);
  - dotted abbreviations (`Ph.D/M.Sc`);
  - technology pairs (`React/Next.js`, a case-sensitive list, so `src/Node.js` counts);
  - two file names (`self.x/self.y`).
- **Characters:** never `<>"|?*`, a backtick, curly double quotes, control or format characters (bidi
  marks), a lone surrogate, or any space but U+0020. A segment never ends with `.`, `,`, `;` or `=`, and `:`
  appears only in a drive.
- **Spaces inside a segment:** only after a strong anchor, after the first segment of `/a/b`, or quoted.
  Single spaces, never next to a separator.
  - Always refused: an option word (`-r`), a file name before more words (`script.sh args`,
    `file.txt is here`, `~/.bashrc (user)`), a verb after the first word (`ClauseWords`: is, contains, …).
  - Unquoted, every word after the first must look like a name: capitalized, a digit, a symbol, a bracketed
    name (`(x86)`, not `(copy)`), brand case (`iPhone`), a version (`v2`) or a title connector (`of`, `the`,
    … — never last). Windows' `New folder (N)` is accepted as is.
  - Quoted, the words may be anything else, except in the first segment of a bare relative path
    (`` `python scripts/build.py` `` is a command).

**Known misses, by design:**
- lowercase names with spaces, unquoted (`C:\Users\me\my stuff`);
- space-separated lists;
- relative paths without a file name (`src/app`);
- `/c/Users`, `\Windows\System32`;
- `file:line` references (`src/x.cs:10`).

**Accepted although not file paths:** URL routes, Claude Code `@`-file references
(`@${CLAUDE_PLUGIN_ROOT}/x.json`), and lowercase member access (`a/b.length`).

**Storage.** `clips.path_count` is a nullable INTEGER, added idempotently like the groups' column. `NULL`
means undecided and 0 means not paths. The kind stays Text/RichText, so paste, Forget forever, `bclip get` and
the Text tab treat these entries as text.
- `Upsert` writes the count on insert and on bump.
- `BackfillPathCounts` runs at every `Initialize`. It decides `NULL` rows (a store from before, rows an
  older build wrote) from `search_text`. That equals the text for anything ≤ 16 K, and a cut search text is
  longer, so it is rejected like the full text and no payload is loaded.
- The meta flag `fixup.path_text.v{RulesVersion}` makes a new rules version reset every verdict once.
  **Bump `PathDetector.RulesVersion` with any rule change that turns a verdict**, or old rows disagree with
  new copies.
- Files filter = `kind = Files OR path_count > 0` (`grep -f files` scans the same slice).

**UI and CLI.**
- Card: Folder glyph, label "Path"/"Paths" (screen readers), caption "path"/"N paths", monospace body.
- Files tab: a tooltip, and an empty state with examples.
- `bclip list -f files`: JSON `paths: N` (omitted otherwise), table kind `path`; `bclip help list` says so.

**Verification (2026-10-01).**
- **Tests:** `PathDetectorTests` (86 positives with counts, 148 negatives, whitespace, line endings,
  length limit, classifier) and `PathTextStoreTests` (filters, older-store backfill, rules version,
  refresh on re-copy), plus CLI tests.
- **Precision on real text:** a scratch probe ran the detector on every line and every adjacent pair of
  lines of 951,234 lines: this repo incl. `refs/ShareX`, four other repos on `K:\source` (AngouriMath's
  LaTeX among them), and the `~/.claude` skills/plugins docs. It found 60 texts, all paths or `@`-file
  references. Its first pass caught command lines, assignments (`PY=/c/…`, `&path=/src/…`) and a remark
  (`~/.bashrc (user)`), all now regression cases.
- **Live:** see §5.

### 2.14 voidtools Everything — the Everything tab (built 2026-10-01) and the verified IPC facts under it

User requests (2026-10-01): "consider Everything.exe integration", then "things you searched in everything and
clicked/picked to be in a tab", then "Lets add Everything integration to completion". The code landed in `4c91613`
(the Run-tab commit took the shared working tree with it); the commit after it describes it and adds the last
fixes. The research it stands on follows below ("Research").

**What the user gets.**
- An **Everything** tab (last, after Run) while voidtools Everything is installed or running and Settings ›
  Integrations › *Everything tab* is on (`AppSettings.ShowEverythingTab`, **on by default**: it only reads, and
  only while the tab loads). Off: no IPC window, no lookups — BetterClipboard does not talk to Everything at all.
- It lists the files and folders opened from Everything's results ("picks": Everything keeps one row per path,
  with how often and when last), newest first, merged with the stored half: whatever was copied in Everything
  (source app `Everything.exe`) and the picks pasted, copied or kept in the tab. Search words narrow both (in
  Everything each word becomes a `path:"…"` term).
- Pick card: document or folder glyph (E8A5/E8B7), label "File/Folder opened in Everything", caption
  "Everything · 5 min ago · opened 3 times", the name over its folder (UI font, not monospace).
- On a pick: Enter pastes the file (`CF_HDROP` + drop effect copy, like Explorer's Copy), Shift+Enter its path as
  text, *Copy only*, Ctrl+P keeps it as a pinned history entry (which then takes the pick's place), *Open*, *Show
  in Explorer*, *Show in Everything* (Everything's window searching that path), the Groups submenu or a drag onto
  a group (kept first, then grouped), Delete hides it until it is opened in Everything again, *Forget forever…*.
- Stored file cards anywhere get *Show in Everything* while the tab is available ("where is that file now?").
- Footer "N opened in Everything · M kept" ("… from Everything's saved history" when not live). Empty states: not
  running (with *Start Everything* when an installed or earlier seen Everything can be started), loading its
  index, not answering, nothing opened yet, no matches.
- Settings card (Search glyph E721) with a live status: installed (path, version) or why not; running (version,
  signer, loading or updating its index) or what the tab shows meanwhile.
- `bclip list -f everything`: the stored half, origin `everything`. Picks are not history entries.

**Design** (`Core/Everything/`, `Windows/Integrations/Everything*`, owned by `AppController`).
- **A live view; stored only on action.** Everything owns the run history: each load asks it again
  (`EverythingIntegration.GetPicksAsync`: one `runcount:` query by date run, the 200 newest, 3 s deadline). Only
  what the user acts on becomes history: `ClipOrigin.Everything` (6) behaves like a live copy — pause, ignored
  apps ("Everything"), the size limit and Forget forever apply; a duplicate is bumped; it lifts a tombstone (it is
  always an explicit action).
- **Merge** (`EverythingTab.Merge`, pure): `ClipFilter.Everything` entries (origin Everything, or a source path
  `…\Everything.exe`, or the source name `Everything`) plus picks. A pick whose single-file hash
  (`ContentHasher.ForFiles`, case-insensitive) equals a stored entry's shows once, as the entry (pins, groups).
  Forgotten picks never show (`ClipHistoryService.FindForgottenAsync`), hidden ones neither. Newest first by last
  used / last opened, pinned on top when that setting asks. Pick cards carry synthetic negative ids.
  - Since 2026-11-11 (§2.44) a stored file sorts by the newer of its last use and its pick's last opening, and its card
    shows that time. Until then only the last use counted: a file kept last month and opened again today sorted as
    last month's.
- **Hide, not delete.** Nothing in Everything is ever changed. Delete on a pick hides it until a later opening:
  state `everything.hidden` in the encrypted store (UTC ticks + path, newest 500; exact ticks — a rounded time
  would compare earlier than the opening it recorded and un-hide the pick at once). Delete on a stored file card
  in this tab also hides that file's pick, or the pick would take the card's place at the next load.
- **Forget forever on a pick** works without storing it, also while paused (`ClipStore.ForgetFiles`: a file
  list's fingerprint is its hash; a stored copy is deleted).
- **Not live** (not running, loading, hung, an unreadable reply): `Run History.csv` stands in
  (`EverythingRunHistoryFile`: the newest of `%APPDATA%\Everything\` and the exe's folder, ≤ 16 MB, columns found
  by header name, a record cut off inside its quoted path dropped). Paths on local fixed disks are checked (gone
  ones dropped, size and folder flag filled in); other paths are shown unchecked (a dead share would block).
- **Status** is refreshed when the panel or Settings opens and before each picks query, never on a timer. The
  installation (`EverythingLocator`: uninstall entries in HKLM 64/32 and HKCU named "Everything…"; the exe from the
  install folder, the display icon — quoted, with an icon index — or the uninstaller's folder; it must pass the
  owner check) at most once a minute, and whenever Settings opens.

**Safety.**
- **Owner check before anything is sent** (`EverythingOwnerVerifier`). Any process can create a window with
  Everything's class; it would receive the search words and could answer with made-up files to paste. The
  window's process must be named `Everything*.exe` and carry a valid Authenticode signature (`WinVerifyTrust`:
  no UI, no revocation lookup, cached URLs only, so it never stalls offline) by organization `voidtools` (2019–2024
  builds) or `voidtools PTY LTD` (2025+). Verdicts are cached per file version. An untrusted window is never
  queried, its claimed folder is never read, and the tab is not offered for it.
- **Read-only:** state questions, queries, and (on request) a `-s "path"` command line. Never run counts,
  settings or the index.
- **One query per reply window** (Everything cancels the older one silently): a newer query cancels the pending
  one, replies are matched by id, late ones dropped, every query has a deadline. `EverythingIpc.DecodeList2`
  checks every count, offset and length against the buffer (`FormatException`, never a read past it, no huge
  allocation from a lying header).
- Reply window: message-only on its own `MessageWindowThread`, `ChangeWindowMessageFilterEx(WM_COPYDATA)` so an
  elevated BetterClipboard still hears a normal Everything.
- Logs: versions, states and the signer only — never a path or a search word.
- **Dev/test:** `BETTERCLIPBOARD_EVERYTHING_INSTANCE=<name>` pins the client to one named instance and skips the
  registry, so the user's Everything is never contacted (§3).

**Tab width** (history; superseded 2026-10-01 by the tab carousel and the resizable panel, §2.23 — the code below is
gone). With ShareX and Run, nine tabs needed 482 DIP and the bar did not scroll, so the window grew
(`MeasureTabsExtraDip`): the labels (a detached `TextBlock` with the template's font + 2 × 9 padding; once laid out
the tabs measured exactly the same) against the content's room — `WidthDip` minus the window frame
(`WindowFrameDip` = `AppWindow.Size − ClientSize`, 14 DIP: `MoveAndResize` sizes the outer window) − 24 + 8.
- Measured with all nine tabs: window 518 px outer / 504 visible, "Everything" 83 px with 13 to spare.
- Without the frame the selected tab read "Everythin" (and "Run" read "Rur" with eight tabs, `4c91613`).
  Measuring the tab items before the first layout read 536 DIP: the bar's 9-DIP padding style is not applied yet.

**Run history saving** (refines "Written only on a save" below).
- **[docs]** `Everything_SaveRunHistory`: "The run history is only saved to disk when you close an Everything
  search window or exit Everything". 1.5 also saves it when auto-saving since 1.5.0.1276a **[forum]**.
- **[verified]** 1.5.0.1423b wrote it at every exit (IPC exit and `-exit`). 1.4.1.1032 with no search window ever
  open (picks made over IPC) wrote it only on `SAVE_RUN_HISTORY` (408): IPC exit, `-exit`, `WM_CLOSE` and 150 s
  of waiting wrote nothing. Real picks happen in a search window, whose closing saves; while Everything runs,
  the live query is the truth anyway.

**Verified** (rows in §5): unit tests on both sides (the Windows client runs against a fake IPC window in the
test process — a `MessageWindowThread` with Everything's class through its `className` override), an opt-in
end-to-end test against real private instances (1.4.1.935, 1005, 1026, 1032 and 1.5.0.1423b), and the panel on an
isolated instance.

**Limits.**
- Everything Lite has no IPC; an Everything in another session is unreachable; the Microsoft Store build and an
  elevated Everything are not verified.
- Picks are what Everything keeps: one row per path. A renamed file loses its history; a deleted one drops out of
  the live query (and is dropped from the saved file when it is on a local disk).
- 1.4: picks made in a search window that is still open are live, but not in the saved file yet.
- No live update while the tab is open (reopening reloads); no paging (the 200 newest picks + 200 stored); pick
  cards have no thumbnails.

**Research** (2026-10-01, before the build).

User request (2026-10-01): "consider Everything.exe integration". Everything is **not installed on this PC**
(no process, service, install entry or portable copy on any local drive). Every fact below was measured against
the official portable builds 1.4.1.1032 and 1.5.0.1423b (SHA-256 matched voidtools' published lists; signer
"voidtools PTY LTD"), run by [`probe_everything.cs`](tools/probes/probe_everything.cs) as private named instances:
- **Setup:** each instance indexed only a BC-TEST tree, with no NTFS volumes, no tray icon, no service and no
  elevation.
- **Checks:** 35 (1.4) and 40 (1.5) checks passed.
- **Side effects:** none. 0 visible windows, the foreground unchanged, the work folder removed.

**[verified]** unless marked.

**Versions and discovery.**
- **Versions:** stable 1.4.1.1032. 1.5 has been a beta since 2026-05-14 (1.5.0.1423b); it installs over 1.4
  **[docs]**.
- **Instances:** 1.4 and the 1.5 beta both run as the unnamed instance, the 1.5 alpha as `1.5a`.
- **Window class:** `EVERYTHING_TASKBAR_NOTIFICATION`, or `…_(<instance>)` for a named instance
  (voidtools/es source). The window exists even with the tray icon hidden, and appeared 0.2–0.45 s after
  start.
- **Waiting for it:** the `EVERYTHING_IPC_CREATED` broadcast never reached a hidden top-level window, and ES
  itself polls `FindWindow` every 10 ms. So poll (it costs microseconds) when needed.
- **Limits:** the **Lite** build has no IPC **[docs: FAQ]**. An Everything in another session is unreachable
  **[forum]**.
- **Elevation:** a standard-user client can query an Everything that runs as admin **[forum, the developer]**.
  ES additionally calls `ChangeWindowMessageFilterEx(WM_COPYDATA)` on its reply window for the reverse case.
  Untested here, because elevating would need UAC.

**The 1.4 IPC: `WM_COPYDATA`, answered the same by 1.4 and 1.5.**
- **State:** `EVERYTHING_WM_IPC` (`WM_USER`) answers 0–3 version, 5 machine, 401 `IS_DB_LOADED`,
  402 `IS_DB_BUSY`, 403 admin, 410 fast sort and 411 indexed info; 4 exits the instance.
- **Query:** `WM_COPYDATA`, dwData 18 (`QUERY2W`): seven packed DWORDs (reply HWND as 32 bits, reply id, search
  flags, offset, max, request flags, sort), then NUL-terminated UTF-16.
- **Reply:** a `WM_COPYDATA` back, with dwData = the reply id. Its `LIST2` header is {total, count, offset,
  request flags, sort}, then count × {flags, data offset}.
  - Each item's fields come in **request-bit order**. The header comment in `everything_ipc.h` lists another
    order; ES's `_es_ipc2_get_column_data` is authoritative.
  - Strings are a DWORD length + UTF-16 + NUL; numbers are 8 bytes, unaligned.
  - A folder's size is −1 in 1.4 and real in 1.5.
- **Reply window:** a message-only window works. The reply never arrives inside the `SendMessage` call (0/30),
  so the thread must keep pumping afterwards.
- **One query per reply window.** A second query on the same window silently cancels the first, which then
  never gets a reply. Two windows get both answers.
- **No answers while the database loads** (`db_loaded=0 db_busy=1`).
  - Queries are queued and answered when loading ends: a query sent at start was answered after 3.6–16 s
    with C:\Windows indexed.
  - A first folder-index scan of `K:\source` was still loading after 6 minutes.
  - So: check 401/402 first, put a deadline on every query, and drop late replies. A folder index scans
    slowly; NTFS indexes read the MFT, which needs admin or the Everything service.
- **Latency** (measured with a spinning pump):
  - ~350 items: an exact path 0.09 ms (1.4) / 0.2–0.3 ms (1.5); a 20-path batch ~1 ms.
  - 278,181 items (a folder index of C:\Windows): an exact path 1.5–2.9 ms, and **0.7–0.9 ms with the file
    name as a leading term**. A 20-path OR batch 18–25 ms, and **2.2–2.8 ms with name terms**
    (`<wfn:"a.cs" path:wfn:"C:\…\a.cs">|…`).
  - Same index: name + path suffix 0.5–1.0 ms; path-contains only 1.4–6.1 ms; `ext:png` newest first (top 50
    of 9,038) 0.6–0.7 ms. 300 results with path, size, date and attributes take 60 KB in one reply.

**Query semantics (both versions).**
- **Exact path:** `path:wfn:"<full path>"`. It is case-insensitive and matches folders too.
  - Quoting is enough for every name character: spaces, parentheses, Hebrew and `! ; & % # ' ^ , $ { } [ ]
    + = ~ @` all matched. Windows forbids `" < > | ? * :` in names.
  - Plain `path:"…"` is *contains*: it also matched `notes.md.bak`.
- **Normalize first:** forward slashes give 0 results (1.5 maps `/` only in free text), and so does a
  trailing backslash.
- **Relative path:** `wfn:"name" path:"\a\b\name"`, then a client-side EndsWith check. `path:endwith:` works
  too (1.4.1+).
- **Moved file:** `wfn:"name" size:N`.
- **Index ≠ disk:** a file outside the index is simply not found. Say "not found by Everything", never
  "deleted".

**The 1.5 named pipe ("IPC3").**
- **Framing:** `\\.\pipe\Everything IPC[ (<instance>)]`, frames {DWORD code, DWORD size} + payload in both
  directions. Responses: 200 OK, 100 more data, 404 not found.
- **`GET_FILE_ATTRIBUTES`** (19, a UTF-8 path) answers from the index in 0.03–0.13 ms. It returned 404 for a
  missing file and also for a real file outside the index.
- **Owner:** `GetNamedPipeServerProcessId` returned the IPC window's PID.
- **Also available** **[docs]**: folder sizes, run counts, and an index journal of creates, renames, moves and
  deletes (1.5.0.1397+).
- **Sources:** SDK3 and ES are MIT (voidtools/everything_sdk3, voidtools/es). Re-implement the protocol; no
  native DLL is needed.

**Privacy and trust.**
- Neither version wrote the probe's query texts to any file: no search history for IPC queries, and the ini
  and the database stayed clean.
- Any process, a low-integrity one included, can create a window with that class name and receive the copied
  paths sent in queries. Verify the owner first: image `Everything*.exe`, signed by voidtools.

**Run history: what you picked in Everything.** Follow-up request (2026-10-01): "things you searched in
everything and clicked/picked to be in a tab". Discovery only; picks were simulated with the run-count IPC, the
same counter the result list increments.
- **What counts** **[docs]**: an item *executed* (opened) from Everything's result list gets run count + 1 and a
  new last run date.
  - It is not an event log: one row per path, holding the count and the last date only.
  - Copying a result (Ctrl+C = the items, Ctrl+Shift+C = the full path) is not a run. Those copies already
    reach BetterClipboard as normal captures, source app "Everything" (FileDescription; the exe is
    `Everything.exe`).
- **Defaults** (the ini Everything rewrites at exit): run history is on and kept forever in both versions.
  Search history is off in 1.4 but **on in 1.5** (`search_history_enabled=1`); the docs page still says "off".
- **Live query, both versions:**
  - `runcount:` = every pick, sorted by date run newest first (sort 26) or by run count (20).
  - Request flags 0x400 (run count, DWORD) and 0x800 (date run, FILETIME) come back in one reply (0xC04).
  - A pick shows up in the very next query.
  - `runcount:>1`, `daterun:today` and `dr:today` work in both versions. The 1.5 spellings `run-count:` and
    `date-run:` return 0 in 1.4, so use the short ones.
  - Cost of the tab's query over 278,182 indexed items (C:\Windows plus BC-TEST): `runcount:` by date run
    2.1 ms (1.4) / 3.1 ms (1.5), by run count 2.0 / 2.6 ms. It grows with the index (sub-millisecond at
    ~350 items), so expect tens of ms on a multi-million-file disk index: run it off the UI thread.
- **Run-count IPC:** `WM_COPYDATA`, dwData 20 = get (the answer is the count), 24 = increment (the answer is the
  new count), 22 = set ({DWORD count, path}). Set also stamps the date run.
  - 1.4 refuses a path that is not indexed (answer 0, nothing kept).
  - 1.5 keeps such picks (they reach the file), but search results only ever contain indexed, existing items.
- **Renames and deletes:** the history is keyed by path.
  - A renamed pick loses its count: the new name has 0, and the old name keeps 1 but drops out of results.
  - A deleted pick drops out of results but stays in the file.
- **`Run History.csv`:** in `%APPDATA%\Everything`, or next to the exe with app_data=0; a named instance adds
  `-<instance>`.
  - Header `Filename,Run Count,Last Run Date`; each row is a quoted path, the count, and a FILETIME in decimal.
  - Written only on a save (IPC 408) or at exit, never on a pick. Watching the file misses every pick until
    Everything exits, but it is complete while Everything is not running. (Refined after the build: 1.4 saves
    when a search window closes, and only then — see "Run history saving" above.)
  - The history came back after a restart.
- **The typed searches:** `Search History.csv` holds search, count and last search date as a FILETIME
  **[docs/forum]**.
  - It is saved only when a search window closes, and there is no IPC for it.
  - Our IPC queries never produced a search-history file, not even in 1.5 where it is on.
- **Tab bar:** the 7 tabs take 351–366 of 384 px (§2.10). An "Everything" tab needs ~83 px (a Pillow estimate
  calibrated on the ShareX tab: 60 px estimated vs 61 measured), so the bar would be 50 px over; even "Runs"
  is 15 px over.
  - All 8 tabs fit only at 4–5 px item padding (from 9), or with a wider flyout or icon tabs.
  - Built: the window grows to fit the visible tabs instead ("Tab width" above); the tab measured 83 px.

**Proposal.** The ranked options are in §6 (the tab, option 5, is built). Everything only answers *where* a path is and *whether* it exists;
`PathDetector` stays pure, so verdicts stay deterministic and storable.

### 2.15 Win+R run history — verified facts for a "Run" tab (2026-10-01; built in §2.17)

User request (2026-10-01): "What about the Win+R's as a new tab 'Run' history?" Discovery only, measured with
[`probe_runmru.py`](tools/probes/probe_runmru.py). The probe prints structure, never a typed command.

**Where Windows keeps it.**
- **Key:** `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\RunMRU`.
  - Values `a`–`z` are `REG_SZ`; `MRUList` holds their letters, most recent first.
  - Each value is the command as typed plus `\1`: a backslash and the show-command digit (PowerToys
    `RunHistory.cpp`: "old MRU format has a slash at the end with the show cmd").
- **Cap: 26 entries** (comctl32's MRU list, `uMax` 26). Entries carry no timestamps; only the key's last-write
  time exists **[DFIR handbook]**.
- **What gets in:** only commands that **succeeded**, including Run as administrator and Run as another user,
  with nothing marking them as such. Source: PowerToys CmdPal's port of the classic `RunDlg_OkPushed` adds to
  history only `if (success)`.
- **Re-runs and eviction:** a re-run moves the existing letter to the front, with no duplicate. When the list is
  full, the least recent letter is overwritten **[docs/forensics]**. The probe's self-test reproduces both on a
  scratch key, but never on Explorer itself.
- **Shared by** other entry points of the same shell32 dialog (Task Manager's *Run new task*) **[forensics]**.

**On this PC** **[verified]**:
- **Full:** 26 of 26 entries, all with `\1`. Every new Win+R command now evicts the oldest; Win+V's 25-item
  pain, again.
- **Contents:** 23 absolute paths, 1 `shell:`/URI, 1 bare name, 1 command with arguments.
- **Last written** 2026-09-25 21:18Z.
- **Nothing blocks it:** `Start_TrackDocs`/`Start_TrackProgs` are absent (the defaults), and HKLM
  `NoRecentDocsHistory` is 0.
- **Neighbours:** File Explorer's address-bar history (`TypedPaths`) is full at 26 too. PowerToys Run's
  `QueryHistory.json` and `UserSelectedRecord.json` are empty. Command Palette (0.8.10371, PowerToys 0.97.2)
  has no run history of its own. So classic Win+R is the history in use.

**The modern Run dialog** (May 2026, Insider Experimental channel only).
- **Where:** Settings › System › Advanced › *Run Dialog*. It is WinUI 3 / C# with AOT, and the legacy dialog
  remains **[docs: Learn, devblog]**.
- **Origin:** "the run command provider in CmdPal is exactly the same code as the new Run Dialog" (devblog).
- **How CmdPal (MIT) keeps history** **[verified from source]**:
  - It reads `RunMRU` once (`CreateMRUListW`, max 26), as a seed when its own list is empty.
  - After that it keeps its own list in `state.json` (`AppStateModel.RunHistory`).
  - It **never writes `RunMRU`**: `AddRunHistoryItem` only updates that state.
- **Risk:** if Windows' build stores history the same way, commands run through the modern dialog never reach
  `RunMRU`, and a `RunMRU`-only tab would go quiet for those users. This is **[inferred]**: 26200 GA has no
  modern dialog to check against.

**Watching for changes.**
- `RegNotifyChangeKeyValue(REG_NOTIFY_CHANGE_LAST_SET)` on the key is event-driven, with no polling.
- One write sequence can notify twice (the value, then `MRUList`), so compare states instead of counting
  notifications. Verified with the self-test on a scratch key: a new letter, a re-run reorder and an eviction
  were all classified correctly.

**Tab bar.** A "Run" tab needs ~42 px. With the ShareX tab the bar would hold 393 of 384 px (9 over); without
ShareX it fits (333 px). Run + Everything would be 476 px (92 over), so more source tabs need a tab-bar
redesign: a "Sources" overflow, icon tabs, or a wider flyout.

### 2.16 PowerShell and cmd.exe history — verified facts for "Pwsh"/"Cmd" tabs (2026-10-01; built in §2.19)

User request (2026-10-01): "What if we use the powershell history and cmd history? "Pwsh" "Cmd" tabs? discover".
Discovery only, measured with [`probe_shellhistory.cs`](tools/probes/probe_shellhistory.cs) (commit `46a0dd0` has
the full record):
- `--inventory` is read-only and prints structure and counts, never a command.
- `--isolated` runs pwsh 7, Windows PowerShell 5.1 and cmd in hidden pseudoconsoles (no window, never handed to a
  terminal) on BC-TEST input. Each PowerShell session gets a scratch `HistorySavePath` before its first prompt,
  so the user's file is never opened; it was checked unchanged afterwards.
- Sources, studied for interoperability only: PSReadLine (BSD-2) v2.3.6 `d2e770f` and v2.0.0 `6b5e9ff`;
  microsoft/terminal (MIT) `2b5336c`.

**PowerShell: one shared file** **[verified]**.
- **Hosts here:** pwsh 7.5.8 with PSReadLine 2.3.6; Windows PowerShell 5.1.26100.8875 with the inbox
  PSReadLine 2.0.0 (the 2020 GA).
- **Where:** `%APPDATA%\Microsoft\Windows\PowerShell\PSReadLine\<HostName>_history.txt`, built from
  `Environment.GetFolderPath(ApplicationData)` (the known folder, not the variable).
  - Both console hosts are `ConsoleHost`, so 5.1 and 7 **share one file** (Windows Terminal, conhost, a plain
    VS Code terminal). VS Code's PowerShell extension writes `Visual Studio Code Host_history.txt`.
  - A profile can move it (`Set-PSReadLineOption -HistorySavePath`). None of this PC's three profiles mentions a
    history option.
- **Format:** UTF-8 without BOM, one record per line, CRLF at the end.
  - A multi-line command keeps each inner break as a backtick + bare LF: ``line`<LF>line`<LF>line<CRLF>``.
  - The reading rule (PSReadLine's own): a line that ends with a backtick continues. A one-line command that
    really ends with a backtick is misread by PSReadLine too.
  - No timestamp, host, version, directory or exit status per record.
- **When:** at Enter, **before the command runs**. The record was there 7–13 ms after Enter while its 3 s
  `Start-Sleep` was still running.
  - The first file-change event came 4.6–6.5 ms after the keystrokes (median of 5), with 1–2 `Changed` events
    per command.
  - Failing commands are recorded, unlike Win+R.
- **Growth:** `SaveIncrementally` (the default) appends and never trims.
  - `MaximumHistoryCount` (4096) limits memory only: set to 3, five more commands all reached the file.
  - A new session loads the newest 4096 records, so on this PC 1,741 of 5,837 are beyond PowerShell's own Up
    arrow and Ctrl+R.
  - `exit` is recorded like any line; nothing is rewritten at exit. `SaveAtExit` rewrites the whole file when
    the console closes **[source]**, so a reader must survive a shorter file.
- **Duplicates:** `HistoryNoDuplicates` (on) drops only a line equal to the one before. A repeat of an older
  line is appended again (51% of this PC's records are distinct).
- **Secret filter** (the file only; the session keeps the line in memory):
  - 2.0.0 keeps out any line matching `password|asplaintext|token|key|secret`, so also `monkey`, `HKEY_…`,
    `-Password $var` and `Get-Secret`.
  - 2.3.6 matches `password|asplaintext|token|apikey|secret`, then lets some through by syntax tree: a variable
    as the argument, SecretManagement commands and `Get-AzAccessToken`, switch parameters.
  - **Both write** `curl -u user:pass`, `mysql -pPASS`, `$env:GH_PAT = '…'`, `Authorization = 'Bearer …'` and
    `sqlcmd -P …`. Checked by asking the default handler with BC-TEST lines, then in live sessions.
- **Locking: what a reader must do.**
  - Open the file with `FileShare.ReadWrite | FileShare.Delete` and close it fast. A reader holding only
    `FileShare.Read` made PSReadLine print "Error reading or writing history file" **into the user's console**,
    and the record arrived with the next command.
  - Don't take the mutex. Its name is `PSReadLineHistoryFile_` + FNV-1a 32 over the UTF-16 code units (low
    byte first) of the lower-cased path, the same in both versions, so 5.1 and 7 coordinate. Holding it for
    1.5 s silently postponed a record to the next command.
  - PSReadLine keeps no handle open between writes. Deleting the file under a running session worked, and the
    next command re-created it with only that record (events Deleted, Created, Changed).
- **This PC** (counts only):
  - `ConsoleHost_history.txt`: 295 KB, 5,837 records since 2024-02-22, ~6 a day. 47 are multi-line (the
    longest has 44 lines); median 30 chars, max 2,685. A full read + parse takes 2.5 ms.
  - Credential-like records already in it: 50 match 2.3.6's raw pattern, 95 match 2.0.0's, 192 a broader
    pattern.
  - The VS Code host file has 81 records.

**cmd.exe: no file, only the console's memory** **[verified]**.
- **Where:** the console host (conhost, or OpenConsole in Windows Terminal) keeps it in memory, per console and
  per client exe name. `HKCU\Console` here: `HistoryBufferSize` 50, `NumberOfHistoryBuffers` 4, `HistoryNoDup` 0.
- **Reading:** only from inside that console: `AttachConsole(pid)`, then `GetConsoleCommandHistoryW` with
  `cmd.exe`. That is the kernel32 export `doskey /history` uses; lengths are in bytes, entries NUL-separated,
  oldest first.
  - A command repeated right away is stored once, a repeat of an older one again; unknown commands are kept.
  - After 60 more commands, 50 were left.
- **Lifetime:** gone when cmd.exe exits, even while the console lives on (0 entries after `exit`).
- **No notification.** Console WinEvents need a real console window: conhost's `AccessibilityNotifier` enables
  MSAA events only with an `hwnd`.
  - Measured with one hook: a pseudoconsole and a `CREATE_NO_WINDOW` console raised 0 events, a hidden classic
    window (`SW_HIDE`) raised 4.
  - Windows Terminal tabs are pseudoconsoles. This PC's default terminal is "Let Windows decide" (= Windows
    Terminal), so its cmd windows never notify.
  - Where the events do fire, they mean "a program started": `cd`, `dir`, `set` start nothing **[inferred]**.
- **Attaching can kill.** A process attached to a console when that console closes is terminated (0xC000013A
  within 9–15 ms). That held with `SetConsoleCtrlHandler(NULL, TRUE)` too, and with a handler returning TRUE
  (it saw `CTRL_CLOSE_EVENT`).
  - So BetterClipboard must never attach itself: one helper process per read costs 56 ms (.NET apphost), where
    in-process would be 0.2 ms.
- **Noise:** 158 `cmd.exe` ran in this session, all started by tools (parents `node.exe` 71, `claude.exe` 43,
  `codex.exe` 43, `chrome.exe` 1). A scan must filter them before spawning helpers.
- **Hand-off:** conhost never hands a console to Windows Terminal for a pseudoconsole, `CREATE_NO_WINDOW`, or
  `SW_HIDE`/minimized (`_shouldAttemptHandoff`). That is why the probe's consoles never appeared.
- **Clink** (keeps `%LOCALAPPDATA%\clink\clink_history`) is not installed here.
  `HKCU\Software\Microsoft\Command Processor\AutoRun` is set (value not read); the probe's cmd ran with `/d`.

**Other shells.**
- **Git Bash** `~/.bash_history` (132 lines, last written 2026-03-12): bash writes it only at exit (no
  `histappend` or `PROMPT_COMMAND`).
- **WSL** was not probed (`\\wsl$` starts the VM).
- **Windows Terminal** keeps no command history of its own **[docs]**.

**Tab bar** (the §2.15 estimate). "Pwsh" needs ~50 px and "Cmd" ~47 px, and either alone overflows the 384-px
bar (the 7 tabs use 351). Both are 64 px over; with Run and Everything, 189 over (still 79 at 4 px padding).

### 2.17 Run tab — the Win+R history, kept for good (built 2026-10-01)

User request (2026-10-01): "Do that plus rescan on entering the tab and keep history" — the §6 proposal built on
the §2.15 facts. Every command run with Win+R becomes a history entry and stays after Windows' list of 26 forgets
it. Settings › Integrations › *Win+R history* (`AppSettings.RecordRunHistory`, **on by default**: Windows already
keeps these commands in plain text, the encrypted copy adds no exposure).

**Pieces.**
- **Core `Integrations/RunMru`** (pure, tested): `Parse` (letters in `MRUList` order, `\1` stripped, defensive
  against hand edits), `Fingerprint` (first 8 bytes of SHA-256 over the upper-cased command; Python known answers
  pin it), `RunsSince`, the snapshot (`FormatSnapshot`/`TryParseSnapshot`), `PlanCaptures`, `CreateCapture`.
- **Windows `Integrations/`**: `RunMruReader` (reads the key, `ReadSettledAsync`), `RunMruWatcher` (the change
  watch), `RunHistoryIntegration` (life cycle, owned by `AppController`). **`Shell/RunCommandLauncher`** runs a
  command like the dialog.
- **Store:** `clips.run_last_utc` (nullable INTEGER, added like the groups' column: `EnsureRunColumn` + the
  partial index `ix_clips_run`; an older build opens the file). `ClipEntry.LastRunUtc`/`HasRunHistory`;
  `ClipFilter.Run` = `run_last_utc IS NOT NULL`; `StoreStats.RunCount`.

**Origins and merge rules** (`ClipOrigin`).
- `RunDialog` (4) — a run seen since the last look, or run again from the panel: a **new event**. Pause skips it, a
  duplicate is bumped to the top, it lifts a tombstone. Safe because a look only ever reports runs newer than its
  snapshot.
- `RunDialogHistory` (5) — what Windows remembered at the first activation: an **import**. No pause, no
  reordering of an existing duplicate (it only gains a run time), tombstones and the last clear suppress it.
- Both stamp `run_last_utc` (never moving it back). Other origins keep a row's run time, so a later copy of a
  command leaves it in the Run tab. Ignored apps match the source `Win+R`; Forget forever applies to both.

**Telling runs apart without times.** Windows keeps no per-entry times and reorders the list only on a run (move
or add to the front; evictions only drop from the end).
- `RunsSince(snapshot, list)` = the shortest prefix of the list after which the rest keeps the snapshot's
  relative order. Theory tests cover new, evicting, re-run, several, cleared and hand-deleted lists.
- **Snapshot:** state `runmru.snapshot` = `v1:` + comma-separated fingerprints, in the encrypted store (never a
  command's text). No snapshot = first activation; `v1:` alone = an empty list (Win+R never used: the first run
  is then a run, not an import).
- **Deliberate limits:** re-running the newest command is invisible (nothing moves); so is a sequence that
  restores the old order. More than 26 runs between two looks lose the oldest ones (only possible while the app
  is not running, or by script).
- **Times:** the key's last write = when the newest command ran. The first import spaces the list one second
  apart below it; runs found later get it minus a millisecond each. Captures are stored oldest first.

**Never read a half-written list** (`RunMruReader.ReadSettledAsync`).
- One run is two writes (the command into its letter, the new `MRUList`). A read between them shows either the
  evicted command in front or the new command at the back, and `RunsSince` takes both for real runs (at worst
  "all 26 ran again").
- So a read is accepted only when the key had been quiet for 150 ms (`SettleTime`, by its last-write time) and
  was not written during the read (last write before = after). At most 10 attempts; a clock that moved backwards
  falls back to the during-read check.

**The watch** (`RunMruWatcher`, thread "Win+R history watch").
- `RegNotifyChangeKeyValue` (LAST_SET | NAME) on the key; while the key is missing, NAME on its parent, reporting
  only when *our* key appeared (Explorer's other subkeys come and go). Neither key nor parent, or a failed
  registration: a 5 s poll on the last-write stamp.
- Re-open and re-arm **before** raising `Changed`: a write after the event is reported again, one in between is
  read by the rescan the event triggers.
- **Footgun found while writing it:** the watched key must stay open until the wait ends. Closing a watched key
  signals the event, so a loop that closed it before waiting would spin.
- The integration debounces (150 ms), then rescans under one gate; the snapshot moves only after the captures
  were handed to the history (a failure is retried by the next look). A run made while paused is skipped by the
  history and still moves the snapshot: it is never recorded.

**Life cycle** (`RunHistoryIntegration`).
- Start: watch first, then the first look. Off: stop and clear the snapshot, so on again re-imports what Windows
  remembers then — existing entries only gain a run time, nothing is duplicated. Exit keeps the snapshot: the
  next start finds the runs made meanwhile.
- **Rescan on entering the Run tab** (`ClipboardFlyout.Filters_SelectionChanged` → `RescanRunHistoryAsync`); the
  list reloads when that stored anything (history events alone would update a bumped card in place, without
  moving it up).
- `BETTERCLIPBOARD_RUNMRU_KEY` points it at another key under HKCU (tests and isolated runs use scratch keys with
  BC-TEST commands). `HKCU\`, `HKCU:\` and `HKEY_CURRENT_USER\` prefixes are accepted.
- **Read only:** Windows' list is never written — not by runs from the panel, not by deletes here.

**Running a command again** (`RunCommandLauncher`, `AppController.RunCommandAsync`).
- Only cards with a run time offer it: Ctrl+Enter, Ctrl+Shift+Enter (as administrator), and the card menu's *Run*
  (Play `E768`) / *Run as administrator* (`E7EF`, a window with the shield, checked by rendering). Enter still
  pastes. Win+R cards show the command-prompt glyph `E756` everywhere, labeled "Win+R command".
- **Parsing, like the dialog:** expand variables; a URI (a scheme of 2+ characters, never UNC) opens whole;
  `SHEvaluateSystemCommandTemplate` splits program commands and resolves bare names through App Paths.
  Measured on 26200: `notepad`, `chrome`, `winword`, `msedge <url>`, `explorer.exe /select,…`, `devmgmt.msc`,
  `appwiz.cpl` and `C:\Windows\win.ini` pass; unquoted paths with spaces, folders, `%VAR%` (unexpanded), URIs,
  `code .` and `.` fail. Then the fallbacks: a quoted file, the longest run of words naming something existing
  (path info only; relative to the profile; `D:` = the drive root), else the first word for ShellExecute's own
  PATH/PATHEXT search.
- **Working directory:** the program's folder when it was named with a path, else the profile (what `cmd` from
  Win+R starts in).
- `ShellExecuteEx` on a short-lived STA thread with the dialog's flags (`NOASYNC | DOENVSUBST | INVOKEIDLIST |
  FLAG_LOG_USAGE`) plus `FLAG_NO_UI`: errors show in the panel's footer ("Not run: Windows cannot find it."),
  1223 = declined at the administrator prompt. Tests use `RunQuietlyAsync`: hidden, and no usage logging (no
  trace in Start's "most used").
- **Launch first, hide after:** the panel still holds the foreground while the command starts, so the new window
  may come to the front; the panel then hides without re-activating the app below. The run is recorded
  (`RecordRunAsync`: a `RunDialog` capture now), moving the command to the top. The command never reaches the
  log ("Ran entry N as a Win+R command: Started.").

**UI and CLI.**
- Run tab (`RunFilter`, a text tab after ShareX) while the setting is on and, since 2026-11-08, the history holds a command
  run with Win+R (§2.36); the tab strip scrolls when the visible tabs
  need more than its width (§2.23; until then the window widened, `MeasureTabsExtraDip`). Footer: `FlyoutViewModel.KeyHint` = "↵
  paste · Ctrl+↵ run · Ctrl+⇧↵ run as admin" in the Run tab, status "N commands kept". Empty state explains
  both setting states.
- Settings card status: "Watching Windows' Win+R list: Windows remembers N of its 26 commands." plus runs
  recorded this session.
- `bclip list -f run` (also `runs`): origins `run` / `run-history`, JSON `lastRun`.

**Not built (yet):** the modern Run dialog's own history (its location cannot be verified: 26200 GA has no
modern dialog); an opt-in delete-through to `RunMRU`.

### 2.18 Windows' own screenshots: Win+PrtScn and Snipping Tool's auto-save (2026-10-01; built in §2.22)

User request (2026-10-01): "We have many integrations in the app, consider integration with: Snipping Tool auto-save
(on by default), Win+PrtScn". Discovery only, measured with [`probe_screenshots.cs`](tools/probes/probe_screenshots.cs):
- `--inventory` is read-only and content-free: no pixel, no clipboard data, and no file name of an unknown kind
  (Game Bar names carry window titles).
- `--self-test` uses BC-TEST data only, in a scratch folder and a private window station: 8 of 8 checks.
- `--watch` is the live check, run while the user takes the screenshots. It never opens the clipboard and never
  denies a writer. **Not run yet.**
- No screenshot was taken for this research: one would put the user's screen into their Pictures folder, their
  clipboard and their RAM-only Win+V history.

Facts are **[verified]** on this PC unless marked.

**Win+PrtScn.**
- **Who:** `twinui.dll` (10.0.26100.8328), loaded by explorer.exe. It is the only one of 4,944 binaries in System32,
  Windows and SystemApps that mentions `ScreenshotIndex`. It also holds `Shell_ScreenshotOverlay` and imports
  `OpenClipboard`, `EmptyClipboard`, `SetClipboardData` and `SHGetKnownFolderPath`.
- **Name:** string 7122 of `twinui.dll.mui` + `.png`. en-US `Screenshot (%d)`; he-IL starts with two U+200F
  right-to-left marks before "צילום מסך (%d)". `%d` is `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer`
  `ScreenshotIndex`, the *next* number (2 here, next to the one file "(1)").
- **What:** the whole virtual desktop. Here that is 3840×1080 (both monitors): a 1.1 MB PNG, colour type 6, chunks
  `IHDR sRGB gAMA IDAT IEND`.
- **Refused for DRM:** the same MUI holds "There is protected content in %1. Close it and try again."

**Snipping Tool** (package `Microsoft.ScreenSketch` 11.2607.23.0: full trust, `SnippingTool\SnippingTool.exe`,
capability `picturesLibrary`).
- **Auto-save** is on by default since 11.2209.2.0 **[forum]**. Win+Shift+S, PrtScn (below) and the app's own
  New all snip through it.
- **Settings** (names found in the exe): `AutoSaveScreenshots`, `AutoSaveScreenshotsLocation`,
  `AutoSaveImageFilePrefix`, `AutoSaveRecordings`, `AutoSaveRecordingsLocation`, `AutoSaveRecordingFilePrefix`,
  `AutoSaveCaptures`.
  - They live in the app-data hive `%LOCALAPPDATA%\Packages\Microsoft.ScreenSketch_8wekyb3d8bbwe\Settings\settings.dat`
    (container `LocalState`; ApplicationData's value types, each value followed by an 8-byte timestamp).
  - None is set here, so the defaults apply: on, the Screenshots folder.
  - The probe reads a private copy: `RegLoadAppKey` on the live file could replay its logs into it, a write to
    another app's data. While Snipping Tool runs, the copy may fail.
  - A custom folder (Settings › Change) is kept through the app's FutureAccessList ("custom auto-save screenshots
    FAL metadata"). How it is stored is unknown: none is set here.
- **Name:** `<AutoSaveImageFilePrefix> yyyy-MM-dd HHmmss.png`. The prefix is a localized resource ("Screenshot" in
  `resources.pri` for en-US and en-GB, the only languages installed).
  - Unknown: a suffix for two snips in one second.
  - Recordings: "Screen Recording" + `.mp4`, in `Videos\Screen Recordings` **[forum]** (missing here: never
    recorded).
- **Clipboard: yes.** Its own toast reads "Screenshot copied to clipboard" / "Automatically saved to screenshots
  folder." (resources). The copy is a `ClipboardCopyService` with WinRT data providers ("Rendering PNG",
  "Rendering Bitmap": delayed rendering) and `Clipboard.Flush` **[inferred from strings]**.
- **Its PNG** has a `pHYs` chunk (`IHDR sRGB gAMA pHYs IDAT IEND`), which Win+PrtScn's has not.
- **Mis-clicks are saved too:** all 3 auto-saved snips here (2025-11 to 2026-06) are 3×2, 15×6 and 11×49 px.
- **Today's label:** BetterClipboard already records snips through the clipboard, as "SnippingTool.exe": the exe's
  FileDescription is its own file name (ProductName "Snipping Tool"), and `SourceAppResolver.ReadDisplayName` takes
  the description first.

**PrtScn and the folder.**
- **PrtScn alone** opens Snipping Tool's overlay, by default since 2023 **[news: made the default in KB5025310]**:
  a snip is copied and auto-saved.
  - `HKCU\Control Panel\Keyboard\PrintScreenKeyForSnippingEnabled` is absent here, i.e. the default.
  - `twinui.pcshell.dll` reads it and launches `ms-screenclip`.
  - Switched off, PrtScn only copies the screen and writes no file.
- **The folder:** `FOLDERID_Screenshots` {B7BEDE81-DF94-4682-A7D8-57A52620B86F} = Pictures + "Screenshots".
  - It follows a Pictures redirection (OneDrive folder backup) unless it is redirected itself (a
    `User Shell Folders` value under its GUID; absent here). OneDrive is not signed in here.
  - Resolve it with `KF_FLAG_DONT_VERIFY`: a profile that never took a screenshot has no folder yet.
- **Nearby, not asked for:** Xbox Game Bar (7.326.8061.0, Win+Alt+PrtScn) saves into the "Captures" known folder
  {EDC0FE71-…} = `Videos\Captures`, and NVIDIA's overlay into `Videos\NVIDIA`. Both are empty here.

**Open: is a Win+PrtScn also on the clipboard?** twinui imports the clipboard calls, but Microsoft's pages only
say it saves a file, and no capture was taken. **Answered 2026-10-09: yes**, on a claude-desktops VM (Windows 11 Pro
25H2, 26200.8037): after one Win+PrtScn the clipboard held a 1920×1080 bitmap (`Bitmap`, `DeviceIndependentBitmap`,
`Format17` = DIBV5), and BetterClipboard's history held one entry for it, labeled "Win+PrtScn": the clipboard copy and
the saved file (imported when the panel opened, since the folder did not exist at start) merged by the pixel hash. The live check settles it, with the user's own screenshots:
```bash
dotnet run tools/probes/probe_screenshots.cs -- --watch 120    # then one Win+PrtScn and one Win+Shift+S
```
It prints:
- per clipboard notification: the sequence number, owner process and window class, format names and the
  foreground process;
- per file: the events, which processes hold it, and when it is released and complete;
- ScreenshotIndex's bump.

That answers the clipboard question and the order and delay. It also shows the formats, and with them whether the
pixel hash (straight BGRA, alpha included) merges the file with its clipboard copy. And it shows how each tool
writes its file.

**Watching a folder** (self-test, scratch folder).
- A file **copied** in arrives as Created, at size 0 and written "now". The source's old write time appears only at
  the end of the copy, so a rule that skips stale files must judge once the file is complete.
- A file **moved** in from another folder of the same volume arrives as Created (not Renamed), with its old write
  time at once. A rename inside the folder is Renamed.
- `FileProcessIdsUsingFileInformation` (which processes have a file open) took ~137 ms per call. That is fine once,
  but too slow to poll, and never belongs in an event handler. The watcher raises events one after another, so the
  probe's first draft, which asked per event, delayed every later event by hundreds of milliseconds.
- **Writer done?** `ShareXScreenshotWatcher` opens the file with `FileShare.Read`, which denies writing. A writer
  that closes and reopens its file (a WinRT `StorageFile` pattern) would fail if its reopen landed inside that open
  **[inferred risk, not observed]**. The probe reads with every sharing mode granted instead, waits for IEND, and
  needs two polls in which no other process holds the file.
- `GetUpdatedClipboardFormats` + `GetClipboardOwner` describe the clipboard without opening it: synthesized formats
  are listed, and a delayed format is never rendered.

**What an integration adds.**
- **Snipping Tool:** its snips reach the history through the clipboard already. New: snips taken while
  BetterClipboard was not running (catch-up), the right name, a tab, a link to the saved file.
- **Win+PrtScn:** the same, plus the screenshots themselves if it turns out not to copy them.
- **Cost,** as for any image copy: PNG + DIBV5. This PC's 3840×1080 Win+PrtScn is 1.1 MB of PNG plus 16.6 MB of
  DIBV5.

The proposal was one "Screenshots" integration in the ShareX mould. Built as the **Snipping tab**, with ShareX keeping its
own tab (§2.22); the live check with real screenshots is still open (§6).

### 2.19 Pwsh and Cmd tabs — PowerShell and Command Prompt history (built 2026-10-01)

User request (2026-10-01), after the §2.16 discovery: "I dont see Pwsh or Cmd in the dev build version, fix". Two
tabs after Run: **Pwsh** (PowerShell's own history file) and **Cmd** (open Command Prompt windows, kept by
BetterClipboard). Settings › Integrations › *Pwsh tab* / *Cmd tab* (`AppSettings.ShowPowerShellTab`,
`ShowCmdTab`, **both on by default**).

**A live view, stored only on action** — the Everything tab's design (§2.14), not the Run tab's.
- **Why:** PowerShell's file holds thousands of commands (5,837 here). Importing them as history entries would bury
  the clipboard history in "All" and spend its 10,000-item limit (retention would start deleting real copies).
- **What is stored:** a command becomes an entry only when the user pastes, copies, pins or groups it:
  `ClipOrigin.PowerShell` (7) / `ClipOrigin.Cmd` (8), source `ShellSources.For` (process `powershell` / `cmd`,
  display "PowerShell" / "Command Prompt"). They behave like an Everything keep: a new event (pause, ignored apps,
  size, Forget forever apply), a duplicate is bumped, a tombstone is lifted.
- **Stored half of a tab:** `ClipFilter.PowerShell` (9) / `Cmd` (10) = that origin, or that source name. A keep that
  bumped an older copy keeps the old origin but takes the source name, which nothing else produces (a copy made in a
  terminal belongs to the terminal's window).

**Pieces.**
- **Core `Shells/`** (pure, tested): `ShellCommand` (text, count, last seen, source; `ClipboardText` with CRLF, and
  `ContentHash`/`Fingerprint` computed from it exactly like a stored copy), `PsReadLineHistory` (PSReadLine's
  reading rules: CRLF/LF/CR end a line, a trailing backtick continues, an unfinished last record is dropped),
  `CmdHistory` (`NewSince`, the kept list), `ShellTab` (merge, hides), `InteractiveConsoles`, `ShellSources`.
- **Windows:** `Integrations/PowerShellHistorySource`, `Integrations/CmdHistorySource`, `Shell/ConsoleCommandHistory`
  (the helper). **Store:** `ClipStore.ForgetText` + `ClipHistoryService.ForgetTextAsync` (forget a text that is not
  stored: list it, delete stored look-alikes).
- **App:** partial files `AppController.Shells.cs`, `ClipboardFlyout.Shells.cs`, `FlyoutViewModel.Shells.cs`,
  `ClipItemViewModel.Shells.cs`, `SettingsViewModel.Shells.cs`, plus small hooks next to the Everything picks'.

**Pwsh** (`PowerShellHistorySource`).
- Reads every `*_history.txt` in `%APPDATA%\Microsoft\Windows\PowerShell\PSReadLine` (the known folder) when the tab
  loads, never on a timer; a file is parsed again only when its size or write time changed. The most recently written
  file comes first; a command typed in two hosts shows once, counts added; the caption names the host
  ("PowerShell", "PowerShell in VS Code").
- Opens with `FileShare.ReadWrite | Delete` and never takes PSReadLine's mutex (§2.16: anything else shows errors in
  the user's console or delays its writes). Files over 32 MB are read from their end. Never written.
- The tab shows while the setting is on and the folder has a history file (looked up on the pool when the panel
  opens: the folder may be redirected to a share). Cards have no time — the file keeps none — only "typed N times".
- `BETTERCLIPBOARD_PSREADLINE_DIR` replaces the folder (tests, isolated runs), like the ShareX/RunMRU overrides.

**Cmd** (`CmdHistorySource`).
- **Reading:** every 30 s while on, and when the tab loads (at most every 2 s): a process snapshot, then for each
  *interactive* cmd.exe of this session one helper, `BetterClipboard.exe --read-console-history <pid>`
  (`Program.Main` runs it before anything else: no lock, no XAML), at most 4 at a time, 3 s each. The helper
  attaches, reads `GetConsoleCommandHistoryW("cmd.exe")`, detaches, and writes UTF-8 + NUL to the stdout pipe it was
  given. Never in-process: a console closing while attached ends the process (§2.16). Measured on the dev build: the
  helper read 3 BC-TEST commands from a hidden pseudoconsole cmd in 59–61 ms (206 ms cold); a pid without a console
  exits 3.
- **Interactive** (`InteractiveConsoles`): walk up through cmd/PowerShell/conhost to the first other ancestor; Explorer,
  Windows Terminal, IDEs, terminal emulators, Task Manager and BetterClipboard itself (the Run tab's Ctrl+Enter) count,
  anything else (node, claude, codex, a browser) does not; an exited parent counts. This PC's 158 tool-started cmd.exe
  are skipped (§2.16).
- **Kept list:** state `cmd.kept` (one line per command: ticks, count, text; newest 1,000), written only when it
  changed and never while paused. A window read before is diffed (`NewSince`: eviction from the front, appends, a
  moved command, a cleared history) so a command typed again counts again; a window seen first adds its commands
  without counting known ones again (the app restarted, the window did not). Off stops the timer and deletes the list.
- **Limits:** cmd notifies nobody, so a window closed within 30 s of its last read loses what came after. Times are
  when BetterClipboard saw a command, not when it was typed. Clink's own history file is not read (not installed here).

**The tabs** (`ShellTab.Merge`, `FlyoutViewModel.Shells.cs`).
- Rows: newest first by each row's newest activity, pinned entries on top when that setting asks, the live commands at
  most 500 (no paging; every stored entry stays). A live command that is also stored shows once, as the entry, at the
  newer of the two times; forgotten ones never show (fingerprints via `FindForgottenAsync`); the search box narrows both
  halves with the same toggles (`SearchMatcher`, on the pool).
  - Since 2026-11-11 (§2.44). Until then the stored half came first, whatever its age: a command pasted from the tab once
    stayed its first row for good.
  - Cmd: a live command's time is when BetterClipboard saw it. Pwsh: its commands carry no times and keep the file's
    order; a kept command shows where PowerShell's history has it, above them all only when it was used after the newest
    command was typed (the history file's last write, `PowerShellHistorySource.LastNewestCommandUtc`).
- **Delete hides** a live command until it is typed again: state `pwsh.hidden` / `cmd.hidden` = count + content hash
  (never the text), newest 1,000; a higher count shows it again. Delete on a stored command in its tab hides its live
  twin too, or it would take the card's place at the next load.
- Cards: the command-prompt glyph E756 (live and kept ones, everywhere), monospace, label "PowerShell command" /
  "Command Prompt command", caption "PowerShell · typed 3 times" or "Command Prompt · 5 min ago · typed once". Menu:
  Paste, Copy only, Pin (keep in history), Groups, Hide until typed again (ED1A), Forget forever… No "Run": a history
  line has no safe place to run (its directory and variables are gone).
- Footer: "500 of 2,952 commands in PowerShell's history · 3 kept" / "12 Command Prompt commands · 1 open window";
  key hint "↵ paste · Ctrl+P pin · Del hide". The tab strip scrolls to make room for the two labels like for any tab (§2.23).
- `bclip list -f pwsh` (also `powershell`) / `-f cmd`: the stored halves, origins `powershell` / `cmd`.

**Verified:** Core tests (`ShellHistoryTests.cs`: 32 — parsing, keys, `NewSince` cases, kept list, merge, hides,
interactive walk, filters, tombstone, `ForgetText`, pause and ignore) and Windows tests (6: the PowerShell source on
temp BC-TEST files incl. a file held open for writing, the helper's wire format and argument checks), the helper end
to end above, and the dev copy started with "Reading Command Prompt windows' history (the Cmd tab)" and 0 WRN/ERR.

### 2.20 Third party — Settings' credits and official links (built 2026-10-01)

User request (2026-10-01): "We have many integrations, some of them are other projects. Have in settings at the
bottom sort of third party section with official links to all we use or integrate with".

**What the user gets** (the last section of `SettingsWindow.xaml`).
- **Works with** (Puzzle `EA86`): Claude Code (Anthropic) and Codex (OpenAI; the prompt archive, §2.21), Everything
  (voidtools), PowerShell (Microsoft; the Pwsh tab, §2.19) and ShareX (ShareX team), each "· separate app", one line on
  what BetterClipboard reads from it, and its official site.
  Listed whether or not they are installed: the card is also where to get them from their makers.
- **Built with** (Library `E8F1`): the eleven components inside the download — .NET, C#/WinRT,
  CommunityToolkit.Mvvm, Microsoft.Data.Sqlite, SQLite, SQLite3 Multiple Ciphers, SQLitePCLRaw, WebView2 SDK,
  Windows App SDK, Windows SDK, ZstdSharp (Codex's compressed session files, §2.21) — each "maker · license", one line
  on its job, and its official link.
  - *License notices* (the card's action) opens `THIRD-PARTY-NOTICES.md` on GitHub (`main`). The copy next to the
    installed exe was not chosen: a `.md` file may have no app registered to open it, and a browser always does.
- **A row** (`ThirdPartyRowTemplate` in the window's resources):
  - the name and the credit as two runs of one text block (the credit's two leading spaces are in the bound value,
    so XAML whitespace rules cannot swallow them), then the use as a caption;
  - a `HyperlinkButton` with `NavigateUri`, showing the site — the host without `www.`, plus owner/repository on a
    code host (`ThirdPartyCatalog.LinkText`) — and the open-in-new-window glyph `E8A7`;
  - the tooltip is the whole address. The accessible name leads with the project ("SQLite: sqlite.org"), because
    sites repeat: learn.microsoft.com serves four rows.

**Scope.**
- Integrations are other makers' apps that BetterClipboard reads from. Windows' own features — Win+V's history,
  Win+R, Command Prompt (the Cmd tab), Explorer — are the platform, have their own Settings cards, and are not listed.
- Components are what the release zip redistributes, transitive packages included. Build- and test-only packages
  (`Microsoft.Windows.SDK.BuildTools`, its `.MSIX` dependency, `xunit.v3`) are not.
- Also not listed: the password-manager catalog (§2.8; its sources are in `docs/password-managers.md`), Windows'
  own fonts (Segoe Fluent Icons, Cascadia Mono: never shipped), and the research tools.

**The official-link rule** (`ThirdPartyCatalog` remarks).
- The project's own website when it has one, otherwise its source repository. For a NuGet package: the project URL
  the package itself declares.
- Always the final https address: no `aka.ms` (it can be repointed), no language segment (`/en-us/`, so Microsoft's
  sites pick the viewer's language), no tracking parameters.
- One exception: `Microsoft.Windows.SDK.NET.Ref` declares another package's NuGet page (`aka.ms/WinSDKProjectURL` →
  `Microsoft.Windows.SDK.Contracts`), so the Windows SDK's own page is used.
- Checked 2026-10-01: all 14 links answered 200. The only redirects were learn.microsoft.com and
  dotnet.microsoft.com adding the viewer's language. The three added with the prompt archive answered 200 at their
  final addresses: `claude.com/product/claude-code` (where `claude.com/claude-code` and `anthropic.com/claude-code`
  redirect), `openai.com/codex/`, `github.com/oleg-st/ZstdSharp` (the package's declared project URL).

**Keeping it complete** (`ThirdPartyCatalogTests`: 9 tests, 29 cases).
- **Packages.** Every package in `Directory.Packages.props` is credited by a component or listed as build-only. So
  is every package the App and `bclip` restore: their `obj/project.assets.json`, transitive ones included; the test
  skips on a fresh clone. Mutation check: dropping WebView2's package failed with `Not found: "Microsoft.Web.WebView2"`.
- **Notices.** Every component has exactly one `THIRD-PARTY-NOTICES.md` row starting `| [Name](`, whose last cell
  is the same license text. Every row there is a component, and integrations have none.
- **Form.** Names are unique and alphabetical within each role (ordinal, ignoring case); uses end with a period;
  links follow the checkable half of the rule.
- **Footgun: no test can see a new integration.** A new Settings card under Integrations for another maker's app
  needs its catalog entry by hand. PowerShell was added this way, while the Pwsh tab was still being built.

**Found on the way** (fixed in `THIRD-PARTY-NOTICES.md`).
- **WebView2 was missing.** `Microsoft.Web.WebView2` 1.0.3719.77 comes with `Microsoft.WindowsAppSDK.WinUI` 2.3.9,
  so `Microsoft.Web.WebView2.Core.dll` and `WebView2Loader.dll` ship in every build, under BSD-3-Clause.
  BetterClipboard hosts no WebView2.
- **The Windows App SDK is not MIT.** Each of its packages' `license.txt` is the "Microsoft Software License Terms —
  Microsoft Windows App SDK" (distributable code: section 3). The MIT license on GitHub covers the source.
- **Nor is the Windows SDK projection.** `Microsoft.Windows.SDK.NET.dll` comes from `Microsoft.Windows.SDK.NET.Ref`
  10.0.26100.57, which the TFM downloads, under the Windows SDK license terms (`aka.ms/WinSDKLicenseURL`). It has
  its own row now; C#/WinRT (`WinRT.Runtime.dll`) stays MIT.

**Verified:** see §5.

### 2.21 Claude Code and Codex prompts — the prompt archive and the Claude / Codex tabs (built 2026-10-01)

User request (2026-10-01): "Add support for claude code and codex prompts user sent, storing them, fully supported,
including full history parse and ability to keep track and diff read over time efficiently and by keeping bytes of file
as version control or even just watcher watching filesystem using os builtin watching efficiently". Every prompt the user
sends to Claude Code or Codex is kept in BetterClipboard's encrypted store, read from the agents' own files the moment
they write them (only the new bytes), and listed in two tabs.

Facts are **[verified]** with [`probe_agent_prompts.py`](tools/probes/probe_agent_prompts.py) (structure only, never a
prompt) and [`probe_append_notify.cs`](tools/probes/probe_append_notify.cs) unless marked. The agents' code was studied
for interoperability only: Claude Code 2.1.282 (the JavaScript in its binary) and `openai/codex` at `57ac6f5`
(Apache-2.0, 2026-10-01). Nothing was copied.

**What the user gets.**
- **Claude and Codex tabs** (after Cmd), each while its Settings switch is on and the agent's folder or the archive has
  prompts (an agent uninstalled after use keeps its tab). One card per prompt text, newest first, 60 per page:
  - glyph Message `E8BD` (checked by rendering), label "Claude Code prompt" / "Codex prompt" / "… slash command";
  - caption "Claude Code · BetterClipboard · 5 min ago · sent 3 times · 1 image" (project folder of the last send;
    images are counted, never archived);
  - the search box and its toggles work as everywhere (FTS trigram + the matcher, §2.6.2).
- **On a card:** Enter pastes the full text (CRLF), *Copy only*, Ctrl+P keeps it pinned, the Groups submenu or a drag
  onto a group keeps then groups — a kept prompt is a history entry (origin `ClaudeCode` 9 / `Codex` 10, source
  `claude` / `codex` — a new event like a shell keep: pause, ignored apps, size, Forget forever) and takes its card's
  place. *Delete until sent again* (Del) deletes every send of the text from the archive; *Forget forever…* forgets it
  everywhere (history, both archives, and every later read).
  - **Where a kept prompt shows** (since 2026-11-11, §2.44): once, at the newer of its last use and its last send, among
    the archived prompts by time; pinned ones on top while "Show pinned items first" is on. Until then every kept prompt
    was listed above the whole archive, so the first prompt ever pasted from the tab stayed its first row for good
    (reported: "The first row in Claude tab is always old one").
- **Footer** "14,156 prompts (17,169 sent) · 3 kept"; a search shows "60+ matching of …"; the first import shows its
  progress; key hint "↵ paste · Ctrl+P pin · Del delete". Entering a tab catches the archive up first (≤ 600 ms wait).
- **Settings › Integrations** › *Claude Code prompts* / *Codex prompts* (`AppSettings.KeepClaudeCodePrompts` /
  `KeepCodexPrompts`, **on by default**: the agents already keep these prompts in plain text; the encrypted copy adds no
  exposure and stops the loss), Robot glyph `E99A` (checked by rendering), a status line (what is read, how much is
  kept, first-import progress, files left out), *Delete stored prompts…* (confirmed).
- **Command line:** `bclip prompts [WORDS] [-a claude|codex] [--all] [--full] [-n] [--offset] [-s]` lists the archive
  (one row per text, or every send with `--all`; JSON `agent`, `sends`, `project`, `session`, `images`, `command`);
  `bclip prompt ID [-o FILE]` prints one exactly; `bclip status` counts them; `list -f claude|codex` lists kept prompts.
- **Settings › Third party:** Claude Code (Anthropic) and Codex (OpenAI) under *Works with*, ZstdSharp under *Built with*.

**Claude Code: `history.jsonl` in its config folder** (`CLAUDE_CONFIG_DIR`, else `~/.claude`).
- **Format:** one JSON object per line, LF: `{"display": text as typed, "pastedContents": {"N": {"id", "type": "text",
  "content" | "contentHash"}}, "timestamp": Unix ms, "project": cwd, "sessionId"}`. Here 17,381 lines since 2025-10-01;
  the first 166 (all on 2025-10-01) end with CRLF and have no session id; times never go backwards; 205 lines are exact
  duplicates by timestamp + session, which is Claude Code's own identity of an entry (`readLogEntries`); 2,595 are slash
  commands.
- **Pastes:** the display keeps placeholders, grammar `\[(Pasted text|Image|Audio|\.\.\.Truncated text) #(\d+)(?:
  \+\d+ lines)?\.*\]`. Text pastes are inline (388) or by `contentHash` in `paste-cache/<hash>.txt` (449; the name is the
  first 16 hex characters of the text's SHA-256), written before the history line (`pendingPasteWrites` are awaited).
  183 pastes have no placeholder left (deleted before sending) and are not part of the prompt. Images and audio are not
  in the history.
- **Writes and rewrites:** appended under a `proper-lockfile`-style lock, mode 0600. Claude Code also *rewrites* the
  file: a **retention prune** drops entries older than the cleanup period through a staging copy beside it, and
  `claude purge` filters a project's lines out. So the file is not append-only, and old prompts vanish from it — the
  archive keeps them.
- **The transcripts are not read** (`projects/**/*.jsonl`, 8 GB here): every user message Claude Code marks
  `promptSource: typed|queued` (6,611) is in the history too. The rest are generated (interrupt notices, task and
  teammate messages, command output) or come from scripts (`claude -p`, no history entry).
- `claude.exe` describes itself as "Claude Code" (FileDescription), so anything it copies (its `/copy`) lands in the
  Claude tab's stored half, like Everything's own copies in its tab.

**Codex: session files and the CLI history** (`CODEX_HOME`, else `~/.codex`).
- **CLI history** `history.jsonl`: `{"session_id", "ts" (Unix s), "text"}` per line, appended under `File::try_lock` —
  on Windows a **mandatory** byte-range lock, so a read during a write fails with `ERROR_LOCK_VIOLATION` (33) and is
  retried — and trimmed **in place** from the front when `history.max_bytes` is set (same file id, shorter). Only the
  TUI writes it (and some app builds): 564 lines here, last written 2026-09-10, while the app kept writing session files.
- **Session files** `sessions/YYYY/MM/DD/rollout-<time>-<thread>.jsonl` (archived: `archived_sessions/`, flat): first
  line `session_meta` (`id`, `cwd`, `originator`, `cli_version`, `source`, `thread_source`, `forked_from_id`), then
  events. A typed prompt is a `user_message` event (legacy history mode) or a completed `UserMessage` item (paginated
  mode: every file here, 2,810 items; text = the text inputs joined, as Codex's `message()` does). `response_item`
  messages with `role: user` also carry what Codex injects (AGENTS.md, environment, plugin lists) and are not read.
- **Whose prompts** (`CodexSessions.ReadThread`): of 719 files, `(source, thread_source)` = (subagent, subagent) 386,
  (vscode, user) 236, (vscode, —) 41, (exec, user) 31, (cli, user/—) 21, (vscode, realtime_voice) 4. Subagent, internal,
  guardian-review and memory threads, `codex exec` runs (here: another agent's helper calls) and MCP sessions are left
  out; the app, the TUI, the IDE extension and custom clients are the user's.
- **Identity:** a forked thread's file repeats its parent's `UserMessage` items with the same id and text and a new time
  (52); short item ids are per-thread counters that collide across threads with other texts (316). Key = item id + text
  hash, no thread. The two records of one prompt (CLI history line, session item: 530 of 564 lines have a twin, median
  0.7 s apart, 95% within 2.3 s) are merged by session + text hash + 2 minutes, in either order to the same row.
- **Compression:** since 2026-06 Codex can compress cold session files (7+ days) to `.jsonl.zst` (zstd level 3) behind a
  flag, and decompresses one to `.jsonl` when the thread is resumed. None here; read with ZstdSharp (.NET 10 has no zstd).
- Codex itself tails its session files by byte offset (`thread_history_1.sqlite` › `thread_history_projection_state`:
  `next_rollout_byte_offset`) — the same approach as the archive's.

**Knowing when: three triggers, because no single one sees everything** (`AgentPromptsIntegration`).
- **Measured** (`probe_append_notify.cs`): a file another process keeps open for appending raises **no** change
  notification and keeps its **old size in the folder** (8 appends over 2.5 s: 0 events, 0 bytes listed) until the
  writer closes it or another process opens it. Open-append-close raises `Changed` per append. Codex writes its session
  files the first way; Claude Code's and Codex's histories the second.
- **Watchers** (`FileSystemWatcher` = `ReadDirectoryChangesW`, 64 KB buffers): Claude's folder (`history.jsonl`), Codex's
  home (`history.jsonl`), `sessions` (recursive) and `archived_sessions`. They see creations, renames (archiving,
  compression, rename-over) and close-flushed appends. A watcher error (overflow) triggers a reconcile.
- **Hot poll** every 5 s: the histories and the session files written or started within 6 h are opened and their real
  length (from the handle) compared with the checkpoint; nothing is read unless it moved. Opening also syncs the folder's
  copy (one `Changed` follows, which finds nothing new: no loop).
- **Reconcile** at start, every 5 minutes, after a watcher overflow: every file opened once and verified — catches what
  happened while the app was not running, and an old Codex thread resumed without its file being "hot".

**Knowing what: diff reading with fingerprints, not copies** (`Core/Prompts/JsonlTail`).
- Per file a `TailCheckpoint`: offset (end of the last complete line), length, write time, NTFS file id (volume serial +
  file index, from the open handle), SHA-256 of the first 4 KB and of the 4 KB before the offset, the newest prompt time
  read (high-water mark). Kept in `prompt_files`, written in the same transaction as the prompts read up to it.
- **Classify:** another file id → *Replaced*; shorter than the offset → *Truncated*; fingerprints differ → *Rewritten*
  (a trim from the front changes the head, a filtered line shifts the anchor); else only appended: read from the offset.
  A reset reads from the start, and the store keeps only prompts newer than the old high-water mark minus 5 minutes
  (older ones were stored, deleted or skipped while paused). Keys merge whatever is read twice.
- Bytes after the last LF are a line still being written: not consumed. CR before LF is dropped. Lines over 64 MB are
  consumed and counted, never parsed. A `.zst` file (cannot seek) is read whole when its length, time or id changed.
- **Cost:** an unchanged file costs one open (poll) or two 4 KB reads (verify); an append, the new bytes in 256 KB chunks
  with a byte-search prefilter (`"UserMessage"`, `"user_message"`) before any JSON parsing.
- Session files of threads that are not the user's are read up to their first line, once; afterwards only their size is
  followed. Session files are tracked by their plain name (`rollout-….jsonl`), so archiving, compressing and resuming
  are not new files.

**Storage** (`Storage/ClipStore.Prompts.cs`, in the encrypted store, added idempotently like the groups).
- `prompts` (one row per send: agent, unique `prompt_key`, source, session, project, `sent_utc`, full text, search text
  ≤ 32 K, preview, text hash = a kept copy's content hash, fingerprint, image count, slash command) + `prompts_fts`
  (trigram, external content) + `prompt_files` + `prompt_tombstones`. **Not history:** no retention, not in "All", not
  counted against the item limit — 17,000+ prompts would bury the clipboard history.
- **Keys** (frozen; a change would duplicate every prompt): Claude `c1:{session}:{ms}:{hash12(display)}` (the display,
  not the expansion, so a cleaned paste cache changes nothing); Codex history `x1:h:…`, item `x1:i:{item}:{hash12}`,
  legacy event `x1:e:{ms}:{hash12}`. A key read again keeps the earliest time.
- **Rules at ingest:** forgotten fingerprints never stored; a deleted text (tombstone: agent + text hash + time) stays out
  for sends up to the deletion; the item size limit; ignored apps (`claude`, `codex`); **pause** skips prompts read
  live (the checkpoint moves: never recorded, the Win+R rule) but not the agent's first import.
- **Listing:** grouped by text (newest send's row via `ix_prompts_text`), paged by text. Until 2026-11-11 the panel left
  the kept prompts' hashes out of its pages; it now reads every text, and its merge (§2.44) shows a kept text once, where
  its archive row says it was last sent when that is newer than its last use.
- A first import is stored in slices of 1,000 through the history worker (copies queued meanwhile are not held up);
  only the last slice moves the checkpoint.
- Forgetting anything (a card, a text, a stored entry) deletes archived prompts with that fingerprint too.

**Life cycle.** Per agent one reader on a dedicated thread in Windows' **background mode** (`THREAD_MODE_BACKGROUND_BEGIN`:
lower CPU, I/O and memory priority), files one at a time. First activation = first import (`state.prompts.<agent>.imported`
set when the pass completes). Off stops reading and keeps the archive and checkpoints (on again catches up); *Delete
stored prompts* stops the reader, clears the agent's archive, files, tombstones and mark, and restarts it (while on,
what the agent still has is imported again — the confirmation says so). Exit keeps everything. Logs: counts and file
kinds only, never a prompt, a project or a file name.

**Measured on this PC.** First import in the app: Claude Code 17,170 prompts in 8.6 s, Codex 2,279 prompts from 720
files (417 left out) in 18.6 s, both at once in background mode. Restart catch-up: ~0.5 s wall, 0.02–0.08 s CPU. Queries
on the 17,000-prompt archive: first page, page 50 and a word search 8–10 ms; a regular expression ~0.25 s (full scan).
The archive with FTS: ~69 MB.

**Limits.**
- Prompts sent through Claude Code's IDE panel or the desktop app are archived only if Claude Code writes them to
  `history.jsonl` (not verified: none here); `claude -p` runs never are (by design, like scripts).
- Images are counted, not kept. Slash commands are archived like prompts (labeled, searchable).
- A Codex thread resumed after days is noticed within 5 minutes (the reconcile), or when its tab is entered.
- `codex exec` typed by hand in a terminal is left out with the agents' runs (they look the same in the file).
- Prompts sent while paused are never archived, even after a later rewrite.
- Dev/test: `BETTERCLIPBOARD_CLAUDE_DIR` / `BETTERCLIPBOARD_CODEX_DIR` replace the folders (tests, isolated instances).

**Verified:** 88 tests (Core 77: parsers, keys' known answers, `JsonlTail` for every kind of change, the store's merges,
tombstones, rewrites, forget, listing, search, checkpoints, schema on an older store, the service's rules and slices,
the CLI; Windows 11 on temp folders: first import + watcher, rename-over prune, whose threads, a writer that keeps its
file open, archive move + zstd compression, the mandatory lock, pause and off/on, restart), a real-data run in the app,
and both tabs on screen on an isolated instance with BC-TEST prompts: cards, captions, footers, placeholder, key hint
(15 checks, §5).

### 2.22 Snipping tab — Windows' own screenshots: Snipping Tool's auto-save and Win+PrtScn (built 2026-10-01)

User request (2026-10-01), after the §2.18 research: "ShareX stays and "Snipping" is for snipping tool". Every screenshot
Windows saves into the Screenshots folder becomes a history entry the moment its file is complete, and the panel gets a
**Snipping** tab right after ShareX's. Settings › Integrations › *Snipping Tool and Win+PrtScn*
(`AppSettings.ImportWindowsScreenshots`, **on by default**, like ShareX's: the files are on disk anyway, and Snipping
Tool already copies every snip to the clipboard). Win+PrtScn shots share the tab, since they share the folder.

**The ShareX model, not a live view.** Every file is stored (unlike the Everything and Pwsh tabs), because a snip
reaches the history through the clipboard anyway, and the pixel hash merges the file with that copy into one entry.

**Pieces.**
- **Core `Integrations/WindowsScreenshots`** (pure, tested):
  - `Classify`: Snipping Tool's `<prefix> yyyy-MM-dd HHmmss[ (N)].png` first, then Win+PrtScn's `<prefix>[ ](N).png`, else
    other. Shapes only: the prefixes are localized, and a Hebrew UI starts with two U+200F marks.
  - `SourceFor`: Snipping Tool = process `SnippingTool` (the process name its clipboard copies carry, so one Ignored apps
    entry skips both), display "Snipping Tool", path of the installed exe; `WinPrtScnSource` = "Win+PrtScn" with no
    process, so Explorer is not blamed; any other image = "Screenshots folder" (process `Screenshots`).
  - `IsFresh` (2-minute window), `IsImageFile`, `LooksComplete`.
  - `SnippingToolSettings.Parse` (the two saving values, timestamps stripped).
- **Windows `Integrations/`:**
  - `WindowsScreenshotsLocator`: `FOLDERID_Screenshots` via `SHGetKnownFolderPath(KF_FLAG_DONT_VERIFY)`; Snipping
    Tool via the per-user AppModel package list; its settings from a private copy of `settings.dat` (`RegLoadAppKey` +
    raw `RegQueryValueEx`, cached by the hive's size and write time).
  - `WindowsScreenshotWatcher`; `WindowsScreenshotsIntegration` (ShareX's life cycle, without its config watch).
  - The display-name rule in `Clipboard/SourceAppResolver.ChooseDisplayName`.
- **Store:**
  - `ClipOrigin.WindowsScreenshot` (11): ShareX's hybrid. It is bumped and paused like a live copy, never lifts a
    tombstone, and is skipped when older than the last clear.
  - `ClipFilter.Snipping` (13) = that origin, or a source path ending `\SnippingTool.exe`, or the source names
    "Snipping Tool" / "Win+PrtScn".
  - Fix-up `fixup.snipping_tool_name.v1`: rows labeled "SnippingTool.exe" with Snipping Tool's path become
    "Snipping Tool". `ApplyDataFixups` now runs each fix-up under its own flag; the old one returned early on its single
    flag.
- **App:** partial files `AppController.Snipping.cs`, `ClipboardFlyout.Snipping.cs`, `SettingsViewModel.Snipping.cs`,
  plus the tab, the card, an empty state and the event wiring.

**The watcher** (§2.18 has why each rule exists).
- **Watch:** the one folder, not recursive (both tools save straight into it; subfolders are the user's own sorting).
  Image files only; Created/Changed/Renamed are debounced 250 ms per path.
- **Never locks a writer out:** reads with `FileShare.ReadWrite | Delete`. A file is complete when its bytes say so
  (PNG's IEND chunk, JPEG's FFD9, GIF's trailer, BMP's declared size), and for formats without an end marker when its
  size and write time held still 300 ms. It is never read with `FileShare.Read`, ShareX's way, which denies writing.
- **Fresh writes only:** a live event's file older than 2 minutes once complete was copied or moved in, and is skipped
  (logged, not "handled"). The catch-up decides by its marker instead.
- **Skips:** cloud placeholders (never opened: reading one downloads it), animated GIFs, files over the size limit, and
  undecodable files (logged).
- **Dedupe** by size + write time, not the path, so a rename right after the save does not import the file again.
- **Catch-up marker** `state.screenshots.last_seen_utc`:
  - the first activation starts at now, so the folder's archive is never imported;
  - each start imports at most the 100 newest files written since;
  - off clears the marker; exiting does not.
- **Locate again:** every 5 minutes, when Settings opens, and when the panel opens while nothing is watched. A folder that
  did not exist is waited for, and the screenshot that created it is caught up.

**UI and CLI.**
- **Tab:** "Snipping" after ShareX (tooltip names both tools). It shows while the setting is on and, since 2026-11-08, the
  history holds a picture it lists (§2.36), and falls back to All when it disappears while selected; the tab strip scrolls to make room for it like for any tab (§2.23). Cards are ordinary image cards: "Snipping
  Tool · just now · 480 × 270", "Win+PrtScn · …", "Screenshots folder · …".
- **Empty state:** "No screenshots yet" — "Snip with Win+Shift+S or PrtScn, or press Win+PrtScn — the screenshot shows up
  here the moment Windows saves it."
- **Settings card:** Cut glyph `E8C6` (scissors, checked by rendering). The status line names the watched or awaited
  folder and what Snipping Tool's own switches mean: it saves here and copies (one entry), auto-save is off (clipboard
  copies only), or a folder of its own (not watched). It counts the screenshots added this session.
- **CLI:** `bclip list -f snipping` (also `snip`, `screenshots`), origin `screenshot`.
- **Dev/test:** `BETTERCLIPBOARD_SCREENSHOTS_DIR` replaces the folder.

**Verified** (rows in §5):
- **Tests:** Core 34 and Windows 22. The Windows watcher and integration classes passed 6 of 6 repeated runs.
- **Merging:** a clipboard copy and its file become one entry, both for a DIBV5 and for a 32-bit BI_RGB DIB whose fourth
  bytes are 0 (a GDI screen capture's layout). So a Win+PrtScn clipboard copy, if Windows makes one, merges.
- **Headless on an isolated instance:** 16 of 16 checks.
- **On screen:** 10 of 10 UI checks, plus 5 Settings-only checks.

**Not built (yet):**
- the live check with real Win+PrtScn and Win+Shift+S shots (`probe_screenshots.cs --watch`, the user's own);
- following a custom Snipping Tool folder, kept in its FutureAccessList in a form not known;
- Snipping Tool recordings (`.mp4`), Game Bar and NVIDIA captures;
- *Show in Explorer* / *Paste as file* (needs a stored path);
- storing only the PNG and making the DIBV5 at paste.

### 2.23 Resizable panel and the tab carousel — `ClipboardFlyout.Size.cs`, `ClipboardFlyout.TabStrip.cs` (built 2026-10-01)

User request (2026-10-01): "1. make the width resizable and height resizable, both persisted. 2. make the All, Pinned,
Text and so on to be a horizontal scroll sort of carrousel with arrows > and < if scrolled and no on edge appearing.
resizing expands this and this area responds to middle mouse scrolls and drag and drop scroll."

Read as: the mouse wheel scrolls the strip ("middle mouse scrolls"), and dragging it scrolls it ("drag and drop
scroll"); a middle-button drag pulls it too.

**Resizing** (`ConfigureChrome`: `OverlappedPresenter.IsResizable = true`).
- **Windows' own border drag.** WM_NCHITTEST, measured: LEFT / RIGHT / BOTTOM in the invisible frame just outside the
  visible edges, BOTTOMRIGHT at the corner, and TOP on the top pixel row *inside* the visible top. WinAppSDK provides
  that row for a window without a title bar, so no `InputNonClientPointerSource` region was needed.
- **Remembered:** `AppSettings.FlyoutWidth` / `FlyoutHeight`.
  - Whole DIPs of the outer window, frame included; the width without the groups column.
  - Defaults 400 × 560, the old fixed size. `Normalize` clamps them to 396..8192 × 320..8192 (360 until 2026-10-09, §2.29).
- **Applied at every summon:** `FlyoutSizing.ToPixels` at the anchor monitor's scale → `FlyoutPositioner.Compute`
  (which still shrinks the size to the work area, without saving that) → `ExtendLeft` for an open column.
- **Saved once per resize, and only the user's** (`Interop/WindowSizeHook`, comctl32 `SetWindowSubclass`):
  - A `WM_SIZING` between `WM_ENTERSIZEMOVE` and `WM_EXITSIZEMOVE` means the user resized (a move loop never gets
    `WM_SIZING`), so `UserResized` fires once at the end.
  - Then `FlyoutSizing.ToDips` → `Settings.Update` from the dispatcher queue. One write per resize, never per mouse
    move: `SettingsStore.Update` writes the file on every call.
  - Size changes the panel makes itself are never saved: the summon, the groups column, a move to another monitor's
    scale, the work-area shrink.
- **Minimum:** 396 × 320 DIPs (+44 with the column), answered in `WM_GETMINMAXINFO` from the window's scale at that
  moment (`FlyoutSizing.MinimumTrackSize`).
  - 396 is the header: logo, "Clipboard", the "Paused" chip and five buttons take 348 of the 358 content DIPs. It
    was 360 (four buttons, 312 of 322) until the update button joined them on 2026-10-09 (§2.29); a width saved
    below 396 is raised when the settings load.
  - Not `OverlappedPresenter.PreferredMinimumWidth`: it takes raw pixels and keeps them when the window moves to a
    monitor with another scale (microsoft-ui-xaml issues 10452, 10475).
- **Gone:** the window no longer grows for the tabs (`MeasureTabsExtraDip`, `WindowFrameDip`, `ApplyTabsWidth`, the
  `applyWidth` parameters; §2.14 "Tab width"). The width is the user's, and the tabs scroll.

**The carousel** (`TabStrip` in the XAML).
- **Layout:** the `SelectorBar` sits in `TabsScroller`, a horizontal `ScrollViewer` with its scroll bar hidden, which
  gives the bar its full width.
- **Arrows:** `TabsBackButton` / `TabsForwardButton` lie over the strip's edges.
  - RepeatButtons in `TabScrollButtonStyle`: chevrons E76B / E76C (thin "<" ">", checked by rendering), 24 DIP wide,
    WinUI TabView's subtle fills, Delay 350 / Interval 150, never focused.
  - Shown only while there is more that way (`TabStripScroll.CanScrollBack` / `CanScrollForward`, ½-DIP tolerance).
  - The strip is clipped under a shown arrow (`TabsScroller.Clip`), so no label runs beneath one.
- **Gestures:**
  - An arrow reveals the next hidden tab right next to it (`TabStripScroll.Step`), and snaps to the end when less than an
    arrow's width would be left. Held, it walks on tab by tab.
  - The wheel scrolls 60 DIPs a notch, down = later tabs; a tilt wheel goes sideways. Quick notches add up
    (`tabsScrollTarget`: the target of the scroll still animating).
  - A left- or middle-button drag pulls the strip past the 4-DIP threshold. It captures the pointer, so the release
    never reaches the tab: ItemsView selects on `PointerReleased` (microsoft-ui-xaml source, `ItemsViewInteractions.cpp`).
    A click without travel still picks the tab. After a drag the search box gets the keyboard back.
  - The capture goes through the pressed tab (`TakeOverTabPress`): the tab captures, the viewer's first attempt fails
    (WinUI never moves a capture from one element to another), the tab gives the capture back — its
    `PointerCaptureLost` makes it forget the press — and the viewer captures. Capturing on the viewer alone left the tab
    drawn pressed (dimmed) after the drag, seen in the live test's captures, and still counting as pressed: a later
    release over it would have picked it. (Until 2026-11-08 this said the viewer takes the capture over from the tab; the
    test desktop's log showed the order above.)
  - A press that leaves its tab any other way is forgotten as the pointer leaves (`Tab_PointerExited`, since
    2026-11-08): "Dragging a tab off itself" below.
  - Touch pans natively.
- **Window drag:** `IsDragSurface` treats `TabsScroller` as a control while it can scroll; while every tab fits, its
  empty space moves the window, as before.
- **Bring into view:**
  - A clicked or focused tab raises `BringIntoViewRequested`. `Filters_BringIntoViewRequested` widens the rect by an
    arrow on each side before the outer viewer acts, so the tab ends clear of the arrows.
  - A selection from code (`SelectFilter`) reveals the tab itself (`RevealSelectedTab`).
  - Every summon places the strip for the tab it starts in, without animation (`PlaceTabStripForShow`, since
    2026-10-09, §2.28): at its start when that tab lies in the first screenful, otherwise just far enough to show it.
    Until the user scrolls the strip, it keeps that tab in view when its width or the tabs change
    (`tabStripFollowsSelection`). Before, every summon reset the strip to its start, where "All" is.

**The SelectorBar's template, adjusted** (`PrepareTabBarTemplate`, at `Filters.Loaded`; both found as descendants, so a
future template without them only loses the fix).
- **Its `ItemsView.ScrollView`** (InteractionTracker-based) would redirect touch, pen and the wheel to its compositor
  tracker (`CapableTouchpadAndPointerWheel`, ScrollPresenter source) although it never has anything to scroll here. Its
  scroll modes are set to Disabled, and `IgnoredInputKinds = Touch | Pen | MouseWheel`. The keyboard stays with it: the
  arrow-key navigation between tabs is ItemsView's.
- **Its `ItemsRepeater` virtualized** (`HorizontalCacheLength="0"` in ItemsView's template).
  - Inside a scrolling viewer it realized only the tabs in view and estimated the rest's width.
  - Found by the first live run: 9 of 11 tabs in the UIA tree, arrow steps of a few DIPs, 15 presses to reach the end,
    a wheel turn jumping to 87 %.
  - `HorizontalCacheLength = 64` viewports now realizes every tab: the width is exact, and every tab can be measured,
    focused and read by screen readers.

**Dragging a tab off itself** (fixed 2026-11-08). User report: "If you drag and drop a tab (Files, ShareX or any really)
then it gets bugged, unclickable and grayed out."
- **The cause** **[verified]** on a claude-desktops Windows 11 desktop, with every tab's visual state and pointer events
  logged by a temporary build (never committed), and in the WinUI source:
  - A tab is a WinUI `ItemContainer`. It draws a mouse press as `UnselectedPressed` (dimmed foreground: the "grayed out")
    until the button comes up over it. The WinUI we ship (`Microsoft.WindowsAppSDK.WinUI` 2.3.9) never forgets a mouse
    press when the pointer leaves: its exit only cleared the pointer-over flag. So a release anywhere else left the tab
    drawn pressed.
  - With the right button the tab was also unclickable: it picks itself on a left release only when no button is
    flagged pressed, and the right-button flag clears only on a right release over it or a cancel. Reproduced: after a
    right-button drag, two left clicks reached the tab (`PointerReleased` in the log) and picked nothing.
  - After a left drag a click still picked it.
  - Microsoft fixed this in `ItemContainer.cpp` on 2026-07-25 (microsoft-ui-xaml PR 16243757, "Fix ItemContainer stuck
    in pressed visual state after mouse release outside": the exit resets the press), not in 2.3.9.
- **Which drags:** only the sideways drag that pulls a scrollable strip was clean (the hand-over above; the log showed it
  working). Every other one left its tab grayed:
  - down into the list (the sideways travel stays under the 4-DIP threshold, so the pull never starts);
  - along a strip where every tab fits (no pull at all; the strip fits on a wide panel), released on another tab;
  - out of the window;
  - any right-button drag (the pull uses the left and middle buttons only).
- **The fix** (`ClipboardFlyout.TabStrip.cs`): `TabsScroller_PointerPressed` remembers the tab a left or right mouse press
  lands on (`heldTab`), whatever the strip can do. Each tab's own `PointerExited` (wired once per tab in
  `InitializeTabStrip`) makes that tab forget the press (`ForgetTabPress`: the tab captures the pointer and gives the
  capture straight back; the `PointerCaptureLost` is where `ItemContainer` resets, its state being private). A press that
  ends inside the strip clears `heldTab`; tabs merely crossed by a drag are left alone.
  - Consequence, as with WinUI's own fix: a press that leaves its tab and comes back picks nothing on release.
  - Harmless once a WinUI with PR 16243757 ships: the tab has then forgotten the press already, and the capture adds a
    second, idempotent reset.
- **Facts found on the way** **[verified from microsoft-ui-xaml source]**:
  - WinUI never moves a capture: `PointerInputProcessor::SetPointerCapture` refuses while another element holds the
    pointer (the result is true only for the element that holds it already).
  - `CUIElement::CapturePointer` returns E_INVALIDARG (an `ArgumentException` in C#) for an element that is not hit-test
    visible, a collapsed one included.
  - Under a capture, `PointerEntered`/`PointerExited` fire only on the capturing element's chain
    (`ProcessPointerCaptureEnterLeave`), so no tab gets an exit during a pull.
  - `PointerExited` comes once per element the pointer leaves: the log had one exit per tab crossed.
- **Verified:** see §5.

**Pieces** (all unit-tested except the App's):
- Core `Presentation/TabStripScroll` (`Step`, `Reveal`, `Wheel`, `Drag`, `Clamp`, the arrow rules; `TabExtent`).
- Windows `Input/FlyoutSizing` (`ToDips`, `ToPixels`, `MinimumTrackSize`).
- App `Interop/WindowSizeHook`, `ClipboardFlyout.Size.cs`, `ClipboardFlyout.TabStrip.cs`; `TabScrollButtonStyle` in
  App.xaml.

**Verified:** see §5.

**Not built:**
- a "Reset size" button in Settings;
- keyboard shortcuts that switch tabs (Ctrl+Tab);
- a fade under the arrows instead of the hard clip (the panel's acrylic has no solid color to fade to);
- inertia after a drag.

### 2.24 Image overlays — the hover peek and the eye-icon zoom/pan viewer (built 2026-10-02)

User request (2026-10-02): hovering an image row should open the image as a full-screen overlay (up to 80% of the screen,
sizing to fit, like a tooltip) after the mouse rests a moment, gone the instant the mouse moves and the rest timer reset;
and an eye icon at an image card's bottom-right should open an overlay "until closed with zoom ability and panning". The
user chose (AskUserQuestion) **both overlays monitor-wide**, and the viewer with **Esc + click-outside + a close button**,
**mouse-wheel zoom and drag-to-pan**.

**What the user gets.**
- **Hover peek:** rest the mouse on an image card; after ~450 ms the full image appears centered on the monitor (fitted
  into 80% of the work area, never upscaled past 100%), framed like a tooltip. A real mouse move (the cursor actually
  leaving the spot it rested on), a press, a wheel turn or a key dismisses it at once; resting again re-opens it. It shows
  the full-resolution image, not the 720×400 card thumbnail.
- **Eye-icon viewer:** a scrim eye button at each image card's bottom-right opens a full-screen dimmed viewer. The mouse
  wheel zooms toward the cursor, a left-drag pans, a double-click toggles fit ↔ zoomed-in, a chip shows the zoom %. Close
  with Esc, a click on the dim area outside the image, or the X button. It stays open until closed.

**How it is built** (`Views/ClipboardFlyout.ImagePreview.cs`, pure maths in `Core/Presentation/ImagePreviewLayout.cs`).
- **Both are windowed popups** (`Popup.ShouldConstrainToRootBounds = false`) sized to the panel monitor's work area, so
  they are not clipped to the small panel yet live in the panel's own `XamlRoot` (its theme, brushes, dispatcher) and are
  counted by `GetOpenPopupsForXamlRoot` — so the existing key model (`IsPopupKey`) and the "don't hide while a popup is
  open" rule (`openPopups`) already cover them. Placed with `WindowInterop.GetClientOrigin` (`ClientToScreen`) +
  `MonitorLookup` so the popup's DIP offset lands on the monitor's work-area origin whatever the caption-less resize border.
- **Activation footgun:** a windowed popup can deactivate the panel as it appears, and the panel hides on deactivation
  when `openPopups == 0`. So `openPopups` is incremented **before** `IsOpen = true` (not in a `Popup.Opened` handler), for
  both overlays; the peek balances it in `HidePeek`, the viewer via `Popup_Closed`.
- **Peek dismissal — by the cursor's position, not by a move event.** The surface covers the monitor and is hit-testable
  (a transparent — but still hit-testable — grid). It receives a *synthetic* `PointerMoved` at the cursor's unchanged spot
  the instant it appears, so dismissing on any move event closed the peek at birth (the symptom the user reported: "it
  disappears while the coordinates did not move"). Instead, the cursor's screen position is recorded on open
  (`peekAnchor = ScreenPointer.Cursor()`), and a mouse move dismisses the peek only once the cursor has travelled past
  `PeekMoveThresholdPx` (3 px) from it (a press or wheel dismisses at once; touch/pen moves are always real). After it
  hides the pointer is over the card again, whose `PointerEntered` restarts the rest timer — so resting re-peeks. A key
  (`Root_PreviewKeyDown` calls `HidePeek` first) also dismisses it. `CanShowPeek` blocks it during a window drag, a card
  drag, an open menu or the viewer.
- **Viewer** uses a `ScrollViewer` (`ZoomMode=Enabled`, bars hidden) holding the image at its natural DIP size
  (pixels ÷ monitor scale, so zoom 1.0 = 1:1); the wheel and double-click call `ChangeView` with offsets from
  `ImagePreviewLayout.ZoomOffset` (zoom toward the cursor), a left-button drag on the image pans by `ChangeView`. A tap on
  the dim backdrop — or the letterbox, which bubbles to it — closes; a tap on the image is swallowed so it does not.
  - **The wheel is handled on the image, not the ScrollViewer**, so it *always* zooms. Once the content is larger than the
    viewport the ScrollViewer consumes the wheel for its own scrolling and marks the event handled before a handler on the
    ScrollViewer would run, so the wheel panned instead of zooming once zoomed in (seen on win-1, 2026-10-02). The image is
    the innermost element and gets the wheel first; handling it there (and marking it handled) pre-empts the scroll.
    Panning stays the drag's job.
- **Resolution + cache:** both show the full image from `AppController.GetImagePngAsync` (the stored PNG as-is, else a WIC
  encode of the DIB — the same preference as `bclip get png`), decoded to a `BitmapImage`; a shared one-entry cache makes a
  peek then the viewer (or repeated peeks of one card) instant, and the load is kicked off on hover so it is usually ready
  by the time the peek's delay elapses or the eye is clicked. The card thumbnail is the fallback when the full image cannot
  be loaded.
- **Pure maths** (`ImagePreviewLayout`, unit-tested): `FitWithin` (the peek's "fit to 80%, never upscale"), `FitZoom` (the
  viewer's initial fit, capped at 100%), `ClampZoom` (tolerates reversed bounds and NaN), `ZoomOffset` (keep the cursor's
  pixel anchored while zooming).
- **The eye button** (`ImageViewButtonStyle` in App.xaml): a 28-DIP circular dark scrim with a white eye glyph (E7B3,
  checked by rendering) so it stays legible over any picture — its colors are fixed, not theme brushes. It lives inside the
  image border, so it shows only on image cards, and a click on it opens the viewer without pasting (a button inside a
  ListView item suppresses the item click). The viewer's close button is E711 (checked by rendering).

**Verified (2026-10-02):** build clean (solution, 0 doc warnings in Core/Windows/App); `ImagePreviewLayout` unit-tested
(12 tests: fit down, no-upscale, upscale, empty-input guards, fit zoom and its fallback, clamp with reversed bounds and
NaN, zoom-toward-point anchoring, zero-old-zoom guard); the full Core suite 729 pass. The glyphs (E7B3 eye, E711 close)
were rendered from Segoe Fluent Icons and confirmed.
**Live, on a Windows 11 desktop (the `claude-desktops` win-1 VM, Windows 11 Pro 25H2, 1600×900), 2026-10-02:** a
self-contained build ran there as a scoped instance (its own data dir, alongside an unrelated BetterClipboard already on
that VM) with a seeded 1800×1100 image. Confirmed by screenshots: the eye button renders at the card's bottom-right; the
peek appears on rest and — the user's bug — **stays while the mouse is still (two screenshots 1.5 s apart, cursor
unmoved), then hides the moment the cursor actually moves**; the eye opens the full-screen viewer; drag-to-pan works; all
three close paths work (Esc, the X button, click-outside). The wheel exposed a second bug (it panned once zoomed in) that
was fixed (handle it on the image) and re-verified live: 70%→145%→300% zooming in while already zoomed in, then 300%→120%
back out, both toward the cursor. The two fixes are commit `e75b095`; a screen recording is at the VM's
`/persist/recordings/bc-image-overlays-qa.mp4`.

**Limits / not built:** the peek is a tooltip (no interaction); the viewer has no rotate, no copy-from-viewer, no
next/previous between images; a huge image is decoded whole (fine for a transient viewer). Only image cards (not
Everything picks or other kinds) get the peek and the eye.

### 2.25 Several shortcuts, and the installer's takeover of Win+V (built 2026-10-03)

User report and request (2026-10-03), after running the `irm | iex` installer:
1. "the folder i was cd in did not have write permissions (non admin), use temp in that case";
2. "-TakeOverWinV must always be checked, have a flag to not to take over that";
3. "Say we dont take over, can we be able to pick one or multiple other hotkeys please, same technique of take over".

**What changed for the user.**
- **The installer works from the temp folder.** It resolves `-InstallDir` and the data-folder override against the
  caller's folder first, then pushes the temp folder (PowerShell location and process directory) for the whole run, and
  pops it at the end: `irm | iex` runs in the caller's session, so their prompt is back where it was. The app is started
  with its own folder as working folder (like the Start menu shortcut).
  - **Not reproduced:** the old installer finished from a folder with a deny-write ACL for Everyone, in PowerShell 7.5
    and 5.1, both `iex` and script-block forms; an app started there ran too. Nothing in the script writes to the
    current folder. The user's error text was not available. The switch to temp stays as asked; if the error comes
    back, its text is needed.
- **Taking over Win+V is the default.** No flags: the Win+letter shortcuts BetterClipboard answers to (Win+V on a first
  install) are added to `DisabledHotkeys` and Explorer restarts once — only when something changed, and never when
  Explorer is not running in the session (it would open a stray window). `-TakeOverWinV` stays accepted; on an update
  whose shortcuts lack Win+V it adds Win+V back.
- **`-NoTakeOverWinV`** leaves Win+V to Windows: Win+V is taken out of BetterClipboard's shortcuts (Alt+Win+V when none
  is left), and a released Win+V is given back (it would do nothing otherwise).
- **`-Hotkey A, B, …`** sets the shortcuts (replacing the old ones; Win+V first unless `-NoTakeOverWinV`), each taken
  over "the same way": an exact Win+letter/digit one is released from Explorer by the installer; any other one someone
  owns is intercepted by the app's keyboard hook. Contradictions (`-NoTakeOverWinV -Hotkey Win+V`, both flags) fail
  before any download.
- **Uninstall** gives back Win+V (as before), the keys installer runs recorded (`installer.json` › `releasedKeys`, new;
  `releasedWinV` kept), and the Win+letter shortcuts in the settings — unless the Chocolatey copy remains or
  `-KeepWinVReleased`.
- **A data-loss bug found and fixed on the way (in every installer since v0.1.0).** `New-Item -Path <key> -Force` on a
  registry key that exists deletes the key with all its values and subkeys and creates it empty (PowerShell 5.1 and 7,
  verified on a scratch key). The installer did that to `HKCU\…\Run` before adding its own value, so **every install
  erased every other app's "start with Windows" entry**, and `-TakeOverWinV` (now the default) did it to
  `Explorer\Advanced`, Explorer's own settings. On the test desktop that one failed with "Attempted to perform an
  unauthorized operation" before deleting anything. `Confirm-RegistryKey` now creates a key only when it is missing; the
  Chocolatey install script (its Run key) and `test-chocolatey.ps1` (`Explorer\Advanced`) had the same line and are
  fixed too. On this PC, `HKCU\…\Run` held only `BetterClipboard`, while
  `Explorer\StartupApproved\Run` still remembered 19 other entries. Six of them were enabled (IDMan, Spotify,
  Mozilla-Firefox, RuneApps Alt1, Teams, OpenVPN-GUI); the user was told, nothing was restored.
  - **A third case, in the installed uninstallers** (found 2026-10-03 by the second Chocolatey audit, §3.2): the old
    `Set-WinVReleased` served the install *and* `-Uninstall`. Giving Win+V back while `DisabledHotkeys` also holds other
    letters runs `New-Item -Force` on `Explorer\Advanced` (with only `V` it removes the value: safe). Every tag from
    v0.1.0 to v0.2.5 has it, and every release zip carries that `install.ps1`, which the Installed-apps entry runs. So
    uninstalling an `install.ps1` install of ≤ 0.2.5 can empty Explorer's settings; only updating to a release built
    after `34b1270` replaces the uninstaller. The v0.2.5 release page also offers that `install.ps1` as an asset
    (1 download): replace it with `main`'s after pushing.
- **The Settings card's "Give back to Explorer" raced Explorer** (since the Win+V button existed): the app re-registered
  its shortcuts 1.5 s after the restart, and if it came first, Explorer could not register the key and it stayed dead
  after BetterClipboard quit (seen with Win+E on the test desktop). Giving back now suspends every shortcut, waits until
  the keys are taken again (`HotkeyService.IsTakenElsewhereAsync`, polled every 250 ms, at most 10 s), then re-applies:
  a key still in the list is then hooked, and Explorer has it the moment BetterClipboard quits.
- **The app takes any number of shortcuts** (up to `AppSettings.MaxOpenHotkeys` = 8): Settings › Shortcut became a list
  (one row per shortcut with its state, Remove — never for the last one —, an add box with Enter / Add / presets that
  toggle). "Release Win+V from Explorer" became **Release from Explorer** for every Win+letter shortcut in the list.

**Pieces.**
- **Core `AppSettings`:** `OpenHotkey` stays the main shortcut (older versions keep working with it alone), new
  `ExtraOpenHotkeys`, derived `OpenHotkeys` (`[JsonIgnore]`: STJ would write a get-only property), `WithOpenHotkeys`,
  `SameHotkeyText`, `AlternativeOpenHotkey` = `Alt+Win+V`. `Normalize` trims, drops blanks and repeats (text, ignoring
  case and spaces), caps.
- **Windows `Input/`:** `HotkeyList.Parse` (gestures by keys, so `win+alt+v` = `Alt+Win+V`; invalid texts reported;
  cap); `HotkeyInterceptorSet` (one interceptor per hooked gesture; the first non-pass-through decision wins, so
  Win+V and Alt+Win+V on one key stay apart); `HotkeyService.ApplyAsync(IReadOnlyList<HotkeyGesture>, bool)` (ids
  `0xB00C + i`, one hook for all that need it, `Registrations`); `HotkeySetupCommand`.
- **Windows `Shell/ExplorerHotkeys`:** `ReleasableKey` (exactly Win + A–Z/0–9), `WithKeys` (keeps the user's
  characters; adding a key already listed in either case changes nothing, so Explorer does not restart for nothing),
  `Plan` (Release / GiveBack / None, with the status line). Of keys BetterClipboard did not record, only V counts as its
  own: a released Win+V that is no shortcut is offered back; a user's own letter never is.
- **`BetterClipboard.exe --set-hotkeys [--validate] SHORTCUT...`** (`HotkeySetupCommand`, run by `Program.Main` before
  the single-instance lock): prints canonical shortcuts (UTF-8, LF) or `error:` lines; exit 0 / 2 invalid / 3 that
  instance runs (saving only: the running app would overwrite the file) / 4 save failed. The log line names the
  shortcuts. **Versions before it ignore unknown arguments and start normally** (Settings opens), so the installer
  gates `-Hotkey`/`-NoTakeOverWinV` on the release being ≥ 0.2.6 (`$HotkeyOptionsMinimumVersion`) and refuses an
  empty answer.
- **Installer** (`install.ps1`): the order is release lookup → shortcut plan + version gate → download, SHA-256,
  extract → `--validate` with the staging copy (a typo fails with nothing changed) → close the app, swap → `--set-hotkeys`
  with the installed copy → shortcut, Run, Installed apps, PATH → Explorer (release / give back, one restart) →
  `installer.json` → launch. Settings are read with `ConvertFrom-Json`; a file PowerShell 5.1 cannot parse (the app
  allows comments; PowerShell 7 does too) is "unknown", and Explorer is then left alone.

**Facts found on the way** **[verified]**:
- With `DisabledHotkeys` = `V`, Win+Ctrl+V stayed taken: the letter releases only the exact chord (§1.5).
- `RegisterHotKey` fails with 1459 `ERROR_REQUIRES_INTERACTIVE_WINDOWSTATION` in a private window station, so the
  `HotkeyService` test registers Ctrl+Alt+Shift+F21–F23 on the session desktop for milliseconds (skipped where there is
  no interactive station).
- PowerShell's call operator neither waits for a GUI-subsystem exe nor captures its output (both versions: no exit code,
  no lines); `Start-Process -NoNewWindow -PassThru -RedirectStandardOutput` does, with `$process.Handle` read so 5.1
  reports the exit code. Python's `subprocess.run` waits and captures as usual.

**Verified:** see §5.

**Not built:**
- an interactive picker in the installer (the shortcuts come from `-Hotkey` or Settings);
- ~~the Chocolatey package's uninstall still gives back only Win+V~~ — done 2026-10-03 (§3.2): it gives back Win+V and
  the settings' Win+letter shortcuts, like `install.ps1 -Uninstall`;
- the installer releases a Win+letter key without checking that Explorer was its owner: for Win+C on 25H2 the `C` it
  writes changes nothing (the hook takes Win+C over), and uninstall removes it again.

### 2.26 Any key as a shortcut: the shortcut box records, and "Used before" (built 2026-10-09)

User request (2026-10-09): "Fix the hotkey assignment to work on any key + have dropdown like now + history of hotkeys
used before below fixed list." Code `e9eb799`; QA fixes `0718a9b`, `db9750c`.

**What was broken.**
- The box only took typed names. Pressing a combination in it did nothing (a `TextBox` never sees Ctrl+Shift+K as text),
  and combinations another program owns went to their owner: Win+E opened File Explorer, Win+V opened our panel.
- `HotkeyGesture` knew letters, digits, F1–F24 and ~25 named keys. Numpad keys, Pause, the Locks, PrintScreen, the Menu
  key, Backspace, media/volume/browser/launch keys, `OEM_102`, IME keys and unassigned codes had no name, and
  `ToString` wrote `0xNN` for them, which `TryParse` could not read back.
- `Ctrl+V` and `Shift+V` were accepted. A registered Ctrl+V catches our own injected paste (`RegisterHotKey` cannot tell
  it apart), and Shift+V makes capital V untypable everywhere.

**Key vocabulary** (`Windows/Input/HotkeyGesture`). One table for 0x01–0xFE drives parsing, formatting and
`HotkeyKeyKind`: `Unusable` (modifiers, mouse buttons, `VK_PACKET`, `VK_PROCESSKEY`, `VK_NONAME`, our mask key 0xE8),
`Typing`, `Editing`, `InputMethod` (incl. the unnamed OEM codes 0xE9–0xF5: Japanese Eisu, Katakana, Hankaku/Zenkaku),
`Function`, `Standalone` (default for codes not in the table).
- Names fold case, spaces, dashes and underscores (`Page Up`, `page-down`, `Num 5`, `Numpad5`, `PrtScn`,
  `VK_SNAPSHOT`); single characters keep theirs (`-` is the minus key). Each key's Win32 constant is an alias, with and
  without `VK_`. Sided modifier names (`LCtrl`, `RAlt`) parse as the modifier.
- Any code can be written `0xNN`; `ToString` uses it for unnamed codes, so every usable code round-trips (a test walks
  all 234).
- Punctuation names are the US layout's (the extra ISO key is `Oem102`). The code is stored, not the character, so a
  shortcut survives a layout change; only its name may look foreign.

**Safety rules** (`HotkeyGesture.Problem`, used by `TryParse(text, out gesture, out problem)` and `TryCreate`), each with
a sentence for the user that Settings shows under the box and `--set-hotkeys` prints after `error:`:
- typing, editing and input-method keys need Ctrl, Alt or Win (alone or with Shift only they would stop working in every
  app); function and standalone keys may stand alone;
- Windows keeps: Ctrl+Alt+Delete (with or without Shift), Win+L, Alt+Tab (any with Alt, without Win), Alt+Esc
  (Alt or Alt+Shift), Ctrl+Esc, Ctrl+Shift+Esc, Alt+F4;
- the clipboard keys: Ctrl+C, Ctrl+X, Ctrl+V, Ctrl+Insert (Shift+Insert and Shift+Delete fall under the first rule);
- Win+Tab and Win+Esc stay possible: they are Explorer's, like Win+V, so they can be taken over.
- Footgun: an entry an older version accepted (`Ctrl+V`) no longer parses. `ApplyHotkeyAsync` skips it with a warning
  (Win+V stands in when nothing is left), and its Settings row says why and to remove it.

**The recorder** (`Windows/Input/HotkeyRecorder.cs`: `HotkeyCapture` is the pure decision logic, `HotkeyRecorder` the
`WH_KEYBOARD_LL` hook on its own `MessageWindowThread`). It listens while the box has focus in the active Settings window
(`HotkeyBox_GotFocus` / `LostFocus`, `SettingsWindow.OnActivated`).
- A key-down with Ctrl, Alt or Win held, or of a function/standalone key, becomes the gesture: its main key is swallowed
  with its repeats and key-up, never a modifier (the modifiers are read from the key state, like
  `HotkeyInterceptor`). The mask key is tapped for Win/Alt, so Start does not open and no menu bar activates.
- Everything else reaches the box: letters, digits, Shift+letters, Backspace, Enter (adds), Tab (leaves). So do the
  refused gestures, which keep their meaning: Alt+Tab switches windows, Ctrl+C/Ctrl+V copy and paste the box's text.
  Own injections and unusable codes (AutoHotkey's 0xE8 too) always pass.
- Recording saves nothing: the box shows the text, the hint says "Enter adds Ctrl+Shift+K.", and Enter or Add saves.
  "Already in the list" shows at once. Editing the box by hand resets the hint and the last problem
  (`OnNewHotkeyTextChanged`).
- **Hook order:** low-level hooks run before registered hotkeys (that is how Win+E is recorded instead of opening
  Explorer), and the newest hook runs first. `HotkeyService` re-installs its takeover hook on every apply, so
  `SettingsWindow.OnHotkeyStatusChanged` calls `HotkeyRecorder.RestartAsync`: the new hook goes in before the old one goes
  out. Without it, a shortcut just added and hooked (Win+E) pressed in the box opened the panel.
- **Safety:** the hook takes nothing unless the owner window is the foreground window, checked per key; it exists only
  between `StartAsync` and `StopAsync`; a stop waits at most 2 s for the key-up of a swallowed key, then removes the hook
  and forgets the key (`Reset`), so a later recording never swallows a stale key-up; the window's close disposes it, and a
  late focus event after the close cannot create one (`closed`).

**History** (`AppSettings.HotkeyHistory`, `MaxHotkeyHistory` = 16, most recently added first).
- `WithOpenHotkeys` puts newly added shortcuts in front; removing keeps them; `Normalize` always keeps the current
  shortcuts in it (a file from before starts with them) and caps by dropping the oldest entry that is not current.
  Blanks and repeats (by text, ignoring case and spaces) are dropped. The installer's `--set-hotkeys` feeds it too.
- Footgun: an older version saving the file drops the member; the next newer version starts again from the current
  shortcuts.

**UI** (`SettingsWindow.xaml(.cs)`, `SettingsViewModel`). The box's placeholder is "Press a shortcut, or type one"; a
hint line under it while it listens. The dropdown keeps the presets on top, then a separator, a disabled "Used before"
heading, the history minus presets (by keys, so `Win+Alt+V` = `Alt+Win+V`) as toggles checked while in use, and "Clear
shortcuts used before" (the current shortcuts stay listed). The menu is rebuilt on each `Opening`. No icon on the clear
item: one icon makes `MenuFlyout` reserve an icon column for every item.

**Verified:** see §5 (unit tests, and the recorder live on a Windows 11 VM as an administrator and as a standard user).

**Not built:**
- live display of held modifiers while recording ("Ctrl+Shift+…"): the box also takes typed text, and holding Shift to
  type a capital would flicker it;
- localized key names (the layout's own character for OEM keys);
- an accessible announcement of the hint (it is a plain text line; a screen reader reads it when focused).

### 2.27 Instant summon: the panel is cloaked, not hidden (built 2026-10-09, `4d62d35`)

User request (2026-10-09): "Can you now optimize the performance of the app? I want it to instantly appear when Win+V is
clicked. Right now it shows but has some delay."

**Measured first** with [`probe_summon.cs`](tools/probes/probe_summon.cs) (new) on a claude-desktops Windows 11 VM:
1920×1080 at 100 %, no GPU, "Animation effects" on like the user's PC (checked read-only), Win+V released
(`RegisterHotKey` path, like the user's), 41 BC-TEST items incl. two pictures. 15 summons each, times from the injected
Win+V; the probe copies the panel's rectangle from the screen every ~23 ms (one screen `BitBlt` costs ~20–25 ms in the VM).

| | In front | First pixels | Picture complete | Pictures on the way |
|---|---|---|---|---|
| Before (`8ea00ff`) | 30 ms (p90 50) | 78 ms (p90 129) | **630 ms** (p90 664) | 9–18 |
| After | 25 ms (p90 44) | 102 ms (p90 134) | **102 ms** (p90 134) | 1 |

A letter sent N ms after Win+V (15 tries each), before → after: 0 ms 14 → 15 leaked into the app below; 15 ms 10 → 11
leaked; 30 ms 1 → 0; 60 and 120 ms 0 → 0. Unchanged: activation takes as long as before.

**Where the 630 ms went** (the old `ShowAt`):
- a hidden window was shown: WinUI renders it from nothing, and DWM shows a window slower than it uncloaks one (below);
- the content faded in from opacity 0 (150 ms) and slid 10 px (220 ms): `PlayEntranceAnimation`, now removed;
- the list was reloaded after the show — `Items.Clear()` and a new card per entry, each image card decoding its
  thumbnail again — and ListView's default `ItemContainerTransitions` (AddDelete, Content, Reorder, Entrance; WinUI 2.3.9
  `generic.xaml`) animated every card in.

**What it does now** (`Views/ClipboardFlyout.Summon.cs`; the class remarks have the whole design):
- **Never hidden, only cloaked.** `ConfigureChrome` cloaks the window (`DWMWA_CLOAK`) and turns DWM's show/hide
  animations off (`DWMWA_TRANSITIONS_FORCEDISABLED`). The warm-up shows it once, cloaked, at the size the next summon will
  use. `Conceal` (every way the panel goes away) cloaks it, hides it (Windows then hands the foreground on — a cloaked
  foreground window would keep the keyboard, invisibly) and shows it again without activation, so XAML keeps drawing into
  it. PowerToys' Command Palette hides its window the same way.
- **Prepared while nobody looks.** `PrepareForNextSummon` (after every conceal) resets what a summon shows: empty search,
  the tab last chosen ("All" until 2026-10-09, §2.28), no group, the strip showing that tab, the groups column as
  remembered, the newest copy selected with the list at its top (below the pins while they are listed first, since
  2026-11-08, §2.28). A history change while concealed queues one refresh at Low priority (`ScheduleConcealedRefresh`:
  an import's burst costs one).
- **Current or not.** `FlyoutViewModel.IsListCurrent` compares the view the shown list was loaded for (`ViewKey`: search,
  tab, groups, toggles, and since 2026-11-08 the "Show pinned items first" order, §2.28) and a history version
  (`MarkHistoryChanged`, bumped by every change, also one applied to a card in place) with now. Only views of stored history qualify; the Everything, shell and prompt tabs always reload. Since a
  summon can start in one of those (§2.28), the hidden panel loads such a tab once after concealing and not after every
  copy (`IsStoredHistoryView`).
- **Patched, not rebuilt.** Reloads go through `FlyoutViewModel.ApplyEntries`: the card of every entry still listed is
  kept (`Update`), and `Core/Presentation/CollectionPatch` (pure, unit-tested) turns the list into the new one with
  removes, moves and inserts — a new copy costs one insert and one remove. Cards of things not stored (Everything picks,
  shell commands, prompts) are never reused: their ids mean nothing across loads.
  - **Never `Items.Clear()`** (found 2026-10-09 while the README photos were retaken, fixed in `2f86791`). The tabs
    of things that are not stored cleared the list and added their cards. A list control that is reset while a row
    is selected hands that row's container on with its selection bar still drawn, so the next list showed a second
    "selected" row: All, Left to the Codex tab, Right back to All gave the bar on the first and the second card
    (2 of 2; UI Automation reported one selected row). A row that is removed by itself is deselected properly: the
    tabs that patch their list never showed it. So those loaders build their cards first and swap them in with
    `FlyoutViewModel.ReplaceItems` (`CollectionPatch.Apply` with nothing to keep), and so does the "search cannot
    run" state. Verified on the VM: one bar after All → Codex → All, Codex → Claude, and
    Pwsh → Cmd → Claude → Codex → All. Whether 0.2.5 showed it is not known (it cleared every list, but still
    played the item transitions).
  - **Footgun found in review:** the old clear also dropped the selection and the scroll position, so "the first card is
    armed after every reload" held by itself. A patched list keeps a card's selection even when it is no longer first:
    Down ×2, then a search that card still matches, then Enter, would have pasted it instead of the best match. The
    `Reloaded` handler now selects and scrolls to the first card after every reload, unconditionally; callers that keep
    a card selected (a history change, a prompt tab) select it again after awaiting the reload, as before. Verified on
    the VM: that sequence pasted the first match. Since 2026-11-08 the handler arms the newest copy, below the pins
    while they are listed first (`ArmCard`, §2.28), and the history handler also scrolls the card it selects again
    into view.
- **No item animations:** the list's `ItemContainerTransitions` is an empty `TransitionCollection`. Cards appear, move
  and leave in one frame (also on delete and while searching).
- **Presenting** (`PresentAsync`): move (still cloaked) → activate → uncloak. A new size (another monitor scale, the
  groups column toggled) and a window never shown before (a summon before the warm-up) wait for two rendered frames
  (≤ 100 ms), the latter also for its tree to load (≤ 500 ms), already active so keys typed meanwhile reach it.
- **The shortcut runs at High priority** on the UI thread (`AppController`), ahead of a queued refresh.
- **Logged:** the session's first summon and any slower than 100 ms from the key to the uncloak: "Panel on screen N ms
  after the shortcut, its list ready (first summon)." Content-free.

**Fallbacks:**
- DWM refuses to cloak the window: logged once; the panel hides and shows like any window (the old way, minus the
  animations).
- The shell still cloaks the window after our uncloak: it belongs to another virtual desktop (the panel is not a tool
  window: ex-style 0x100, so virtual desktops apply). It is hidden and shown once, which moves it to the current desktop.

**Facts found on the way** **[verified on the VM]**:
- A cloaked window stays `WS_VISIBLE`, but `WindowFromPoint` skips it, and a real click inside its rectangle went to
  Notepad below. Nothing else needs parking it off-screen.
- DWM's latency per way of revealing a ready window (`probe_summon.cs --reveal-test`, a plain red 400×560 window,
  transitions off, 15 each): `SW_SHOWNA` after `SW_HIDE` 85 ms (p90 106), uncloak 43 ms (p90 53), moving it into view
  from -32000 39 ms (p90 53). Uncloaking is as fast as moving and twice as fast as showing.
- Trace of a summon (temporary instrumentation, removed): `ShowAt` starts 1.5 ms after the key is captured; activation
  (`Activate` + `ForegroundHelper.Activate`) takes ~20 ms in the VM; the uncloak follows at +28 ms. Uncloaking before
  activating measured 102.5 ms against 108 ms median (12 summons each): inside the ~10 ms between identical runs, so the
  safer order stays (the first frame already has the active look).
- Footgun: while any `CompositionTarget.Rendering` handler is attached, XAML renders every frame (the trace showed a frame
  every 16 ms for as long as one stayed attached). `RenderedFramesAsync` removes its handler the moment the wait ends.
- Idle cost of the shown-but-cloaked window: see §5 (CPU time of the whole app over 40 s, old and new build).

**QA on the VM** (all ✅, §5): paste via search + Enter into Notepad; clicks on the concealed panel's area reach the
window below; virtual desktops (a new desktop, and back); Win+D then Win+V; a copy made while concealed is on top at the
next summon; search + another tab + a click elsewhere → the next summon starts clean; the groups column opening and
closing across summons; the first summon right after a start.

**Not built / open:**
- keys typed within ~15 ms of Win+V still reach the app below (activation is the floor; a key buffer in the LL hook
  would close it, §6);
- not verified on a real GPU, a high-refresh monitor or a multi-monitor mixed-DPI setup (the size-change path is the one
  a monitor with another scale takes). Update 2026-11-08 (§2.38): on mixed scales a window set up like the panel came out
  at the wrong size — WinUI rescales a window the program moves onto such a monitor — and `MoveAndResizeExactly` now
  asks again;
- a summon that comes while a concealed refresh is still queued (a Win+V a few ms after a copy) shows the previous list
  for a moment, then patches it.

### 2.28 Getting around the panel: the remembered tab, arrow keys through tabs and groups, hover selection (built 2026-10-09)

User request (2026-10-09): "1. Remember the last tab that was selected across opens of the betterclipboard 2. When using
up/down arrows, i want left/right arrows to support in a circular (0 to left moves to end and end to 0). Clicking up when
you are at the top moves you to a selection on the group where the last(lowest) gets selected and moving up. user will
need to click to the right-arrow to exit or use the mouse to hover another row. (add that on mouse-hover, we select the
row we are hovering over, make sure to make it performant as mouse event will spam as user moves mouse over). Mouse over
the categories doesn't select them, only click selects them."

**What the user gets.**
- **The panel opens in the tab last chosen,** also after a restart (`AppSettings.LastTab`). Only the tab is kept: every
  summon still starts with an empty search over the whole history (no group).
- **Left / Right** select the previous / next tab among those that show, in a circle: Left on the first tab goes to the
  last one, Right on the last tab to the first.
- **Up on the top row** walks into the groups column, and the icon the keys are on gets a ring. Up and Down then step
  through the groups; Up from the first group shows the whole history again (the logo). Right goes back to the rows. So
  does the mouse over a row, typing, or a click on an icon.
  - **Where the keys land changed on 2026-10-10 (§2.31):** on the first group shown, or on the logo when none is
    shown, so walking in changes nothing that is shown. Until then: always on the lowest group, which was then shown
    alone.
  - **With several groups merged, the keys only move the ring** (§2.31): Space or Enter adds the group under it to the
    view or takes it out.
- **The mouse selects the row it is over.** Tabs and group icons are still selected by a click only.
- **Since 2026-11-08 every load selects the newest copy:** row 0, or the first row below the pins while they are listed
  first, so Up reaches the lowest pin (see "Where every load puts the selection" below).

**Decisions where the request left room** (told to the user in the reply; change them here if they ask):
- **Left / Right against the search box's caret.** The box keeps the focus, so the keys serve two masters. They are the
  text's while it is edited: the box holds text and the last key went into it. They are the tabs' with an empty box, and
  after an arrow key steered the panel (Up, Down, Page Up, Page Down, Left, Right) until something is typed or the box is
  clicked. With Ctrl, Shift, Alt or Win they are never the tabs' (word jumps, selections).
- **A held key stops at an edge.** A key that repeats (`KeyStatus.WasKeyDown`) does not wrap from the last tab to the
  first and does not walk from the top row into the column. Holding Up to return from far down a list would otherwise
  run on through every group. A new press crosses.
- **Into the column only when there is one:** it must be open and hold at least one group. Closed, Up on the top row
  does nothing, as before. Page Up never enters.
- ~~**Always the lowest group,** as asked, also when another group is shown at that moment.~~ Replaced 2026-10-10 on
  the user's request: the first group shown, else the logo (§2.31).
- **Positions in the column:** the logo (the whole history) on top, then the groups. "+" is not one. Down stopped at
  the lowest group, and only Right, the mouse or typing left. Since 2026-10-10 a new press of Down on the lowest group
  goes back to the list, unless several groups are merged (§2.31).
- **In the column, the other keys keep their meaning:** Ctrl+P and Del act on the selected card (the card the group's
  load armed: its first card, or since 2026-11-08 its newest copy below its pins), Esc closes. Until 2026-10-10 Enter pasted that card too, and the footer said "↵ paste". Since then
  Space and a plain Enter belong to the groups in the column: they go back to the list with the group, or pick in a
  merged view, and the footer shows `GroupsKeyHint` ("↑ ↓ groups · ↵ or → back to the list") or `GroupsPickKeyHint`
  (§2.31).
- **Kept in `settings.json`,** as a tab name, not only for the session.

**Pieces.**
- **Core `Presentation/PanelNavigation`** (pure, tested): `CycleTab`, `HorizontalArrow` (→ `HorizontalArrowAction`),
  `MoveInList` (→ `ListMove`), `EnterGroups`, `MoveInGroups`, `PageStep`. Since §2.31: `EnterGroups(column, shown)` →
  `GroupsEntry` (position and `GroupWalkMode`), and `PickKey` (→ `GroupPickAction`).
- **Core `Presentation/PointerSelectionGate`** (pure, tested): `Suspend` (the keyboard or the panel set the selection;
  remember where the cursor is), `Observe` (the mouse owns the selection once the cursor is farther than the threshold
  from that spot on either axis), `Activate` (the wheel).
- **Core `GroupSelection.Only`:** one group alone. Unlike a plain `Click` it never hides the only group shown.
- **Core `AppSettings`:** `LastTab` (a `ClipFilter` name), `LastTabFilter`, `ParseTab`, `DefaultTab`.
- **App `Views/ClipboardFlyout.Navigation.cs`** (new): the remembered tab, the arrow keys, the hover selection. Its class
  remarks are the full design.
- **App, changed:** `FlyoutViewModel.ResetForShow(filter)`, `IsStoredHistoryView`, `ShowOnlyGroup`, `SetGroupsNavigation`;
  `ClipboardFlyout.SelectFilter` (below), `PlaceTabStripForShow` (replaces `ResetTabStrip`), `EnterTab`,
  `Popup_Closed`; `AppController.IsExiting`; `GroupButtonKeyboardStyle` in `App.xaml` (the ring).

**The remembered tab** (`rememberedTab`, `StartFilter`, `ResetViewForShow`).
- **Only a choice changes it:** a click on a tab, Left / Right, a screen reader's select. `Filters_SelectionChanged`
  counts a selection as a choice only while the panel is presented and `SelectFilter` is not running
  (`selectingFilterFromCode`). A reset, the bar's own selection while it loads, and the fallback to "All" when the
  selected tab disappears are not choices.
- **A tab that is not there** (ShareX gone, its switch off, another app's tab not known yet right after a start, the Run
  or Snipping tab with nothing in it, §2.36): the
  summon starts in "All" and the remembered tab stays. `IsTabAvailable` asks the controller the same questions the
  `Update…Tab` methods ask.
- **It comes back while the panel is hidden:** every status handler ends with `ReturnToRememberedTabWhileConcealed`,
  which prepares the hidden panel again when `StartFilter()` is no longer the tab shown. An open panel is left alone.
- **Saved once per use,** when the panel is concealed and only when it changed (`SaveRememberedTab`), queued at Low
  priority: a paste's conceal is on the way to its Ctrl+V, and stepping through a dozen tabs must not write the file a
  dozen times (every `SettingsStore.Update` writes it and re-applies the settings).
- **A name, read leniently** (`ParseTab`: names only, any case): an enum member written by name makes the whole file
  unreadable for a version that does not know it (`JsonException`, and `SettingsStore.Load` sets the file aside as
  corrupt). `Enum.TryParse` would also take "3" and "Pinned, Text". `Normalize` writes the canonical name back.
- **A summon enters its tab** (`EnterTab`, after presenting): the Run tab's rescan and a prompt tab's catch-up used to
  run only on a click.
- **Tabs of other apps** (Everything, Pwsh, Cmd, Claude, Codex) are never "current" (§2.27). The hidden panel loads such
  a tab once after concealing, and not after every history change: that would ask Everything, read the shells (helper
  processes for Command Prompt windows) or query the archive after every copy. A summon reloads the tab right after it
  showed. A hidden prompt tab is also refreshed when its agent's archive changes (`OnPromptsStatusChanged`).
- **The strip** is placed for the tab without animation, measured from the strip's start, so the same tab always lands
  at the same place (`PlaceTabStrip`). It follows that tab on size changes until the user scrolls it (§2.23).

**Selecting a tab before the bar has loaded** (a summon before the warm-up) **[verified from source and live]**.
- The SelectorBar's inner `ItemsView` selects every tab it finds flagged `IsSelected` when it first prepares its
  containers (`ItemsView::OnItemsRepeaterElementPrepared`: "the ItemsSource may be a list of ItemContainers, some of them
  having IsSelected==True"). The XAML flags "All". So a `SelectedItem` set before the load lost to "All" when the tree
  loaded.
- `SelectFilter` therefore also sets the tabs' own `IsSelected` flags while `Filters.IsLoaded` is false. Once loaded,
  the bar keeps the flags in step itself.
- `SelectorBar.SelectionChanged` is raised inside the `SelectedItem` setter (`SelectorBar::OnPropertyChanged`), which is
  what makes the `selectingFilterFromCode` flag reliable.

**Hover selection: cheap, and never against the keyboard.**
- **A pointer event is not proof of movement** **[verified from source]**. After every frame that changed the picture,
  WinUI replays the last pointer position, at most every 500 ms (`CCoreServices::ReplayPreviousPointerUpdate`,
  `MIN_POINTER_REPLAY_PERIOD_IN_MS`), and again when a wheel scroll has settled. The row now under a mouse that lies
  still gets `PointerEntered`. A replay raises `PointerMoved` only when the entered elements changed, and marks its events
  `PointerRoutedEventArgs.IsGenerated`. Windows also sends a mouse move to a window that appears under the cursor.
- **So the cursor's position decides** (`GetCursorPos`, like the image peek, §2.24). The keyboard, a reload that arms
  its card, a summon and a closing popup call `SuspendPointerSelection`. The mouse selects again once the cursor is
  more than 4 DIPs (the drag threshold) from the spot it was on then.
- **Per row, not per move.** Each row's container gets one `PointerEntered` handler (`WatchCardPointer`, marked in the
  row's `Tag`, attached in `ContainerContentChanging`). That is the event WinUI's own hover highlight follows, so
  highlight and selection agree. Handled on the container, not on the card's content: the content starts 10 to 12 DIPs
  inside the row, and the selection would lag the highlight by that much.
- **A move handler only while the keyboard owns the selection** (`ItemsList_PointerMovedWhileSuspended`): it notices the
  mouse taking over inside the row it was resting on (no `PointerEntered` comes for that row), selects it, and removes
  itself. While the mouse owns the selection, nothing of this runs per mouse move.
- **The wheel over the list** hands the selection to the mouse without a move. The rows that scroll under the cursor
  are then selected as WinUI reports them.
- **Hovering never scrolls** (`SelectByPointer` sets `SelectedItem` only): a row peeking in at the bottom would pull its
  neighbors under the cursor one after the other.
- **Not while** a menu or flyout of ours is open, the image viewer shows, the window is dragged or a card is dragged
  onto a group (`CanPointerSelect`). Touch and pen select by tapping, as before.

**Found on the way.**
- **An error at exit** (QA, 2026-10-09): with the panel in the Cmd tab, `--exit` logged "Loading the flyout list failed"
  (`COMException 0x8001010E`, wrong thread). The stopping Cmd reader announced its status, the panel answered with a
  reload, and that load ended after the dispatcher was gone. `FlyoutViewModel.ReloadAsync` now starts no load while
  `AppController.IsExiting`. A panel that opens in the tab last chosen is in such a tab far more often.
- **After a mouse click on a tab, the keyboard focus is on the tab** (not new): typing then does not reach the search
  box until Ctrl+F or a click into it. Left / Right move that focus along with the selection (`CycleTab`).
- **A closing menu** left the selection to the row under the mouse (the mouse was the active device). `Popup_Closed`
  now suspends the mouse, so the card the menu belonged to stays selected until the mouse moves.
- **A summon before the warm-up with a far tab remembered:** 481–767 ms to the screen in 4 runs (Cmd 481, 514 and 540;
  Pwsh 767), as before (573 ms, §2.27). One earlier run took 3,195 ms: the first restart, right after the exit that
  logged the error above. It did not recur.

**Where every load puts the selection: the newest copy, under the pins** (changed 2026-11-08). User report: "When having
pinned tab and Win+V is pressed so that BetterClipboard gets opened, the top pinned clipboard row is selected where the
unpinned should be selected, pressing up after open should to to the lowest pinned row."
- **Before:** every load selected row 0. With "Show pinned items first" on (the default) that is a pin, so Win+V + Enter
  pasted a pin, maybe weeks old, instead of the copy just made.
- **Now** (`PanelNavigation.ArmedRow`, pure, tested): the first row that is not pinned, while the setting puts pins first
  **and** the pins are the list's leading rows; otherwise row 0 (also for a list of nothing but pins: the Pinned tab, a
  search only pins match); -1 for an empty list. Up from it goes to the lowest pin, the way the user asked; Up on row 0
  still walks into the groups column, so with pins that walk is that many presses longer.
- **Every load,** not only a summon: a search, another tab, another group, the Enter fallback (`PasteBestMatchAsync`).
  A search lists its pinned matches first too, and the selection stays on the first unpinned match while the user types
  instead of jumping into the pins with the first letter. `Ctrl+1…9` stay positional (the n-th row, pins included).
- **Decisions where the request left room** (told to the user in the reply; change them here if they ask):
  - "pinned tab" read as pinned rows listed on top (in the Pinned tab every row is a pin, so it starts on its top pin);
  - searches, tabs and group views follow the same rule as a summon (a summon-only rule would make the selection jump
    into the pins with the first typed letter);
  - only with "Show pinned items first" on: in recency order row 0 is the newest item and stays selected;
  - a pin pasted last (it then heads the pins, being the most recently used item) does not take the selection: the
    request names the unpinned row, and the next copy is what Win+V users paste.
- **Scrolling** (`ArmCard`, `ScrollToTopShowing` in `ClipboardFlyout.Navigation.cs`; `ResetListPosition`):
  - the list stands at its top while that shows the armed card whole; otherwise just low enough to show it on the
    lower edge, with as many pins above as fit. Row 0 keeps the old path (`SelectIndex(0)`, `ScrollIntoView`);
  - measured, not left to `ScrollIntoView`, which brings a row in from wherever the list stands: after a use that left
    the list scrolled down, the armed card would sit on the top edge with every pin out of sight;
  - how: `ChangeView(0)`, `UpdateLayout()`, the row's lower edge in the scroll viewer's content coordinates, then
    `ChangeView` to `max(0, bottom − viewport)` — **set again after the layout pass, also when it is 0**. Found in the QA
    pass: Ctrl+P on the armed copy moved it to the top of the pins, and the layout pass kept the visible rows in place
    (rows inserted above them), answering the request for the top with offset 32, the whole scroll range;
  - fallback `ScrollIntoView`: no scroll viewer yet, or a row without a container after the layout. **A list the panel
    has not shown since it started lays out no rows while cloaked** (measured: no container and no scroll range after
    `UpdateLayout`, in the warm-up and in a refresh before the first summon); once shown, a concealed list lays out
    normally. The summon positions the list again once it is on screen.
- **Found on the way (fixed):**
  - **The order was not part of "is the list current".** No event reloads the hidden panel when "Show pinned items first"
    changes, and its list kept the old order until the next copy, also at HEAD. With the new rule, the setting turned on
    over a list still in recency order selected row 1, under a pin of the old order. Now `FlyoutViewModel.ViewKey` has
    `PinnedFirst`, so the next summon reloads, and the rule checks the rows (pins must lead the list).
  - **A card re-selected after a history reload could end off-screen.** `OnHistoryChanged` reloads and selects the
    user's card again; with many pins the reload's scroll goes to the armed card a screenful away from it (Ctrl+P on the
    armed copy with 9 pins). It now also scrolls that card into view (`ScrollIntoView`, which moves only when needed).
- **Limits:** the panel loads 60 rows at a time, so with more pins than that the first page holds no copy and the list
  starts on its top pin. Not checked: whether the first summon of a session — its list never laid out while cloaked —
  shows one frame at the top before the measured scroll (only with more pins than the top shows; not seen in the QA).
- **Settings says so:** the "Show pinned items first" card's description adds "The panel still selects your newest copy,
  just below them." It is the localized key `T7ffed93d48ee` in `Core/Localization/en.json` and `zh-Hans.json` (the
  keys are the first 12 hex digits of the English text's SHA-256; the old sentence's `Tc907e7af7ff5` is gone from both).
  The change was written before the Simplified Chinese UI landed (`8c5814a`) and rebased onto it: the QA ran the
  English text from before the catalogs; the Chinese sentence was not seen on screen.

**Verified:** see §5.

**Not built / open:**
- a Settings switch for "always open in All" (not asked for);
- remembering the groups shown;
- giving the focus back to the search box after a click on a tab;
- the Menu key in the column opening the group's menu (it opens the card's), and creating a group from the keyboard;
- an announcement for screen readers when the keys step through the groups (the focus stays in the search box);
- a tab chosen in a panel that is still open when the app exits is not saved (the queued save no longer runs);
- not verified on real hardware: a touchpad, a mouse with a high polling rate, touch and pen.

### 2.29 Updates from GitHub releases: the update button, its dialog, and `install.ps1 -Update` (built 2026-10-09)

User request (2026-10-09): "We need an update system that uses releases in github. We will have an icon left to the pause
icon that is highlighted to update and on click it will open dialog approval for update and the changelog is loaded and
displayed below the buttons of decision."

**What the user gets.**
- **The update button** in the panel's header, left of Pause (`UpdateButton`; glyph E895 = Sync, two arrows in a circle,
  checked by rendering). It is always there:
  - like its neighbors while there is nothing to do;
  - with the accent fill of a primary button while an update waits that the user did not skip
    (`IconButtonAttentionStyle`, a whole style, based on `AccentButtonStyle`);
  - a 16-DIP ring instead of the glyph while an approved update downloads, is verified or is installed, and while a
    check runs with no offer known. A check that runs while an update is already offered keeps the highlight.
  - Its tooltip and accessible name say the state (`UpdateText.ButtonTip`: "Update available: BetterClipboard 0.2.7",
    "BetterClipboard 0.2.6 is up to date", "Downloading BetterClipboard 0.2.7: 33 %").
  - Once a week, while an update waits, that tooltip opens by itself for a few seconds (§2.30).
- **The update dialog** (a click on the button; also the tray menu's *Update to x.y.z…* and *Settings › Updates*): a
  title, one or two sentences, the decision buttons, and **below them** the release notes.
  - An update on an installed copy: *Update and restart* (accent), *Later*, and the link *Skip this version*.
  - Up to date: "What is new in this version" (or "The latest release" for a build newer than every release),
    *Check now*, *Close*.
  - Downloading: a progress bar, "23.4 of 70.8 MB", *Cancel*. Verifying and installing: a moving bar. Installing has no
    buttons: the installer owns the update then.
  - A failed check shows a warning bar, a failed installation an error bar with the reason; the buttons stay.
- **Nothing is installed without approval.** Opening the dialog asks GitHub at most once in 5 minutes, and only while
  this copy checks by itself.
- **After the update** the app is back in the notification area, and a notification says "BetterClipboard was updated to
  0.2.7" (or "The update to 0.2.7 was not installed" with the reason).
- **Settings › Updates** (a section before *Third party*): the switch *Check for updates automatically*
  (`AppSettings.CheckForUpdates`, on by default), a status line (`UpdateText.SettingsStatus`), *Check now*, and a button
  that opens the same dialog ("Update to 0.2.7…" with the accent style, or "Release notes…").
- **The installer:** `-NoUpdateCheck` (installs with the check off, through `BetterClipboard.exe --set-update-check off`),
  `-Package <zip>` (an offline install), and a closing note that names the check, both ways to switch it off and the
  privacy section.
- **The panel's minimum width** is 396 DIPs (360 before): the header has a fifth button (§2.23).

**Who updates how** (`UpdateInstallKind`; `Core/Updates/InstalledCopy`, `Windows/Updates/InstalledCopyLocator`).

| Copy | How it is recognized | Checks by itself | Installs by itself |
|---|---|---|---|
| Installed by `install.ps1` | The Installed-apps entry's `InstallLocation`, or `installer.json`'s `installDir`, names the exe's folder | Yes | Yes |
| Extracted by hand | Neither record names its folder | Yes | Since 0.2.11: in its own folder, file by file, when this user may write the folder and the offer is 0.2.11 or later (§2.48). Otherwise *Download page*, *Copy install command*, *Later* |
| Chocolatey | Its folder ends `\lib\betterclipboard\tools\app` (checked first) | No | No: *Copy command* (`choco upgrade betterclipboard`), *Later* |
| For every user (`install.ps1 -AllUsers`, since 2026-10-18, §2.34) | The PC's Installed-apps entry (HKLM) names the exe's folder (checked before the per-user records) | No | No: *Copy command* (the install command with `-AllUsers`, for PowerShell as administrator), *Later* |

- **Why a copy extracted by hand is never swapped as a whole:** an installer's update renames the whole folder aside and
  deletes it. That is safe only for a folder the installer made. The zip ships `install.ps1` next to the exe, so that file
  proves nothing; the installer's own records do. Since 0.2.11 such a copy updates itself file by file instead, touching
  only the release's own files (§2.48).
- **What its two ways out did before 0.2.11** (run 2026-11-13 from the released 0.2.9 zip to 0.2.10, §5; both traps are
  fixed for copies of 0.2.11 and later, §2.48):
  - *Copy install command* is clean. The pasted `irm … | iex` closes the copy, installs into `%LOCALAPPDATA%\Programs`
    and takes the history, pins, settings and *Start with Windows* with it; that copy then updates itself.
  - *Download page* is clean only when the user closes BetterClipboard and extracts the new zip over the same folder. Two
    traps otherwise (§6). The new exe started while the old copy runs hands over to it and exits: nothing changes.
    Extracted into a new folder (Explorer's *Extract All* default), the old copy's *Start with Windows* entry keeps
    starting the old exe at every sign-in, and the new copy's switch reads Off.
  - Each new exe from a browser download meets SmartScreen's "Windows protected your PC" until releases are signed (§3.3).
- **An isolated instance** (`BETTERCLIPBOARD_DATA_DIR`) never checks by itself, so tests and dev runs do not use up the
  user's 60 requests an hour at GitHub. It is judged by its own `installer.json` alone, never by the Installed-apps
  entry: started from the installed folder, it would otherwise replace that folder under the regular instance, which
  the installer could then only end by force.

**The check** (`Core/Updates`: `UpdateService`, `UpdateClient`, `GitHubReleases`, `UpdateCatalog`, `SemanticVersion`).
- **The request:** `GET https://api.github.com/repos/Nucs/BetterClipboard/releases?per_page=30`. Anonymous;
  `User-Agent: BetterClipboard/<version>`; `Accept: application/vnd.github+json`; no cookies; the system proxy; at most 5
  redirects; 30 s per request; an answer over 8 MB is refused.
- **The schedule:** a first tick 60 s after start, then one tick an hour; a check runs when the last success is older
  than 24 h. A failed check is tried again after 1, 2, 4 … hours (at most 24), and never before a rate limit's reset.
- **Log levels:** no connection, a rate limit and a server error are logged at Info, because they are everyday events
  and WRN/ERR lines stay a health signal. An answer that is not a release list, and a failed installation, are warnings.
- **The cache:** the answer and its entity tag are kept in `updates\releases.json` and `releases-state.json`. A restart
  shows a known update at once, and an unchanged list costs one "304 Not Modified". *Check now* sends no entity tag.
- **What is offered:** the newest stable release above the running version (SemVer 2.0 precedence:
  `0.2.6-dev.52539eb` < `0.2.6`). Drafts, prereleases and tags that are not `vX.Y.Z` are never offered. The release must
  hold the zip for this PC (`BetterClipboard-<version>-win-<x64|arm64>.zip`, by the OS's architecture) and
  `SHA256SUMS.txt`.
- **Which notes are shown** (`UpdateCatalog.SelectChangelog`): every release newer than the running one, newest first, at
  most 10; else the running release's own notes; else the latest release's.
- **State:** one immutable `UpdateSnapshot`, replaced as a whole. `Changed` is raised on the thread that made the change
  (up to ten times a second during a download); `AppController` forwards it to the UI thread as `UpdateStatusChanged`.

**The trust chain of an approved update.**
1. The package and the checksum list must lie under `https://github.com/Nucs/BetterClipboard/releases/download/`
   (`UpdateSource.IsTrustedDownload`). Redirects from there are followed (GitHub's file servers).
2. `SHA256SUMS.txt` must list the package. When GitHub's API gives a digest for it (`assets[].digest`, computed at
   upload), the two must agree.
3. The download goes to `updates\<zip>.part`, is hashed while it streams, and is moved into place only when complete. A
   hash that differs deletes the file. A complete package of the offered version from an earlier attempt is hashed and
   used again; packages of other versions are deleted when an installation starts.
4. The installer script comes out of that verified zip (the `install.ps1` at its root, at most 2 MB, written under a
   fixed name into `updates\`): never from the installed copy, never from the network.
5. The script checks the zip against `-PackageSha256` once more before it uses it.
- **Not covered:** a GitHub account that is taken over and publishes a release with matching checksums. An Authenticode
  check of the package's `BetterClipboard.exe` would cover it once releases are signed (§3.3, §6).

**The installer contract** (`install.ps1`, `Windows/Updates/ScriptUpdateInstaller`). A running program cannot replace
its own folder, so another process must. The app runs the **new release's own** installer:

```text
%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass
    -File <updates\install.ps1> -Update -Package <zip> -PackageSha256 <hash> -InstallDir <folder>
    -ResultPath <updates\result.json> -LogPath <logs\betterclipboard-update.log>
```

- **This command line is a contract** with every released version. A later `install.ps1` must keep accepting it. A later
  app may add arguments only when every package it can update to accepts them.
  [`tools/release/test-update.ps1`](tools/release/test-update.ps1) runs it against the packaged zip, in CI and in the
  release job before publishing.
- **How it is started:** `UseShellExecute = false`, no window, the temp folder as working folder (never the install
  folder, which is renamed), and nothing redirected: the app closes while the script runs, and a pipe to a closed process
  would end the script. `PSModulePath` is removed from its environment: an app that was started from PowerShell 7
  inherits that shell's module path, with which Windows PowerShell cannot load its own modules.
- **`-Update` changes one copy and nothing else.** It closes the programs that run from `-InstallDir` (`--exit`, then
  by force after 20 s), swaps the folder by renames (a failed swap puts the old folder back and starts the app again),
  refreshes the version in the Installed-apps entry and in `installer.json` **only when they name that folder**, writes
  the result file, and starts the app again when it was running. No shortcut, Run value, PATH or Explorer change, and no
  Explorer restart. Options that set an install up (`-Hotkey`, `-AddToPath`, …) are refused with it.
- **The restart is direct** (`Start-AppAsBefore`): the app gets the rights and the environment of the app that asked,
  and starts in the background. Not through Explorer, which gives a process its own environment: in the first test, an
  isolated instance came back as the regular instance. `PSModulePath` is removed for the app too, through .NET (a cmdlet
  of a module that is not loaded yet could not be found without that variable).
- **The result file** (`version`, `ok`, `error`, `finishedUtc`) is written before the app is started again. The app reads
  it once at its next start (`UpdateService.TakeLastResult`, before `Start`, which empties `updates\` except the cache)
  and shows the notification.
- **The log** is a PowerShell transcript (`-LogPath`). Its name matches the app log's pattern, so the 14-day cleanup
  removes it. The log of an earlier run is deleted before the installer starts, so a failure never points at the log of
  the last success.
- **How the app reads a failure:** an installer that ends while the app still runs gave up before the swap. The message
  is the result file's error; else it names the log; else (no log: the script never ran its first line, which is what a
  policy that blocks scripts looks like) it says where to get the new version.
- **Footgun:** an execution policy set by Group Policy overrides `-ExecutionPolicy Bypass`. The update then fails with
  that last message. `irm … | iex` still works on such a PC, because it runs no script file.
- **Footgun:** the installers of 0.2.5 and older have no `-Update` and fail on it (exit code 1). No released app can ask
  them: the first version with the updater is 0.2.6, and it only offers newer releases.

**`-Package`** installs a release zip from disk. The version comes from the file name (or `-Version`). An ARM64 zip is
refused on an x64 PC. The zip is verified against `-PackageSha256`, else against a `SHA256SUMS.txt` next to it; with
neither, it is installed with a warning that states its hash.

**The dialog** (`App/Views`: `UpdateView.xaml(.cs)`, `UpdateDialog.cs`, `MarkdownRenderer.cs`,
`ClipboardFlyout.Update.cs`; `App/AppController.Updates.cs`).
- **One view, two owners.** `UpdateView` only shows a snapshot and raises events. `UpdateDialog` puts it in a flyout
  and turns the events into controller calls. The panel and Settings each own one.
- **The button's own flyout** (`Button.Flyout`), so a second click closes it. A `Click` handler that called `ShowAt`
  would open it again at once: the click that dismisses the bubble also reaches the button.
- **Its own window** (`ShouldConstrainToRootBounds = false`): the panel can be 396 × 320 DIPs, no room for release notes.
- **Placement** (`Core/Presentation/UpdateDialogLayout`, pure, tested): under the button when that side has 440 DIPs or
  is the larger one, else over it; never taller than the room there (300 to 600 DIPs). The notes scroll, so a shorter
  bubble only shows fewer lines. Decided in the flyout's `Opening` event from `Flyout.Target`. Left to itself, WinUI
  moved a bubble that did not fit to the left of its button and up to the top of the screen (seen on a 900-px screen).
- **Popup accounting:** the bubble is counted in `openPopups` from `Opening`, not `Opened`: its window can take the
  activation first, and a panel that is deactivated with no popup counted conceals itself. When the bubble closes, the
  panel waits 80 ms and conceals itself unless its own window is in front (`ForegroundHelper.IsForeground`). A link
  conceals the panel directly (`LeavingForBrowser`): an always-on-top panel must not stay in front of the browser.
- **Release notes** are Markdown. `Core/Presentation/Markdown` parses what release notes use: paragraphs, headings,
  nested and task lists, fenced code, tables, rules, quotes, bold, italic, code, strikethrough, links, bare URLs and
  entities. It never throws, and its work is bounded (200,000 characters, 12 levels of nesting). `MarkdownRenderer`
  draws each block with plain text controls; HTML is shown as the text it is.
- **Links** are http and https only (the parser drops others, and `AppController.OpenWebLink` checks again). A
  `Hyperlink` gets no `NavigateUri`: the host opens the link, after the panel got out of the way.
- **The notes are redrawn only when their text changed** (`UpdateView.SameNotes`). A download reports progress ten times a
  second, and a check that confirms the list would otherwise throw the reader back to the top.
- **Theme:** the notes' brushes are styles in `UpdateView.xaml`, applied from code, so they resolve in the flyout's own
  theme (§4).

**Facts found on the way** **[verified]**:
- **GitHub's answer** for this repository: 6 releases, 77.5 KB, in 880 ms; with the entity tag, "not modified" in
  100 ms. `assets[].digest` is `sha256:<hex>` and equals the `SHA256SUMS.txt` line. The app's client downloaded the
  74.3 MB zip of 0.2.5 through GitHub's redirect in 4.0 s, with 29 progress reports and the right hash.
- **Tray notifications are not kept** in Windows 11's notification center. A screenshot must come within about 5 s.
- **`ScrollViewer.ChangeView` on a scroller that is not on screen yet is dropped.** The notes kept their last position
  until the reset moved from the flyout's `Opening` to its `Opened` event.
- **`Start-Transcript` works in a `CreateNoWindow` PowerShell.** It also logs every caught terminating error: 20 lines
  for the 20 rename attempts of a locked folder.
- **Timing in the test VM** (4 cores, no GPU): Windows PowerShell needed about 11 s from process start to the script's
  first line, and the script about 10 s to hash and extract the 71 MB zip. The app closed 11 to 21 s after the download
  was verified, and the new version started 7 s later.
- **SignPath's terms on privacy** (read 2026-10-09): software that transfers data to systems the user did not specify
  must describe that in a privacy policy, display the policy during installation, and offer an installation option to
  disable it. Hence the README's *Privacy* section, the installer's closing note and `-NoUpdateCheck` (§3.3).

**Test and dev.**
- `BETTERCLIPBOARD_UPDATE_FEED=<url>` names a stand-in feed: https anywhere, http on this PC only, and every download
  must have the feed's scheme, host and port. **Only an isolated instance honors it**, so a variable in the user's
  environment can never redirect the installed app's updates (the log says when it is ignored).
- An isolated instance updates a scratch copy when its own data folder has an `installer.json` whose `installDir` is
  that copy's folder.
- **Never test an update on the user's installed copy, unless the user asks for exactly that** (2026-10-10; the
  procedure and its guards are in §4). `tools/release/test-update.ps1` is the safe test on this PC: it works in
  scratch folders and proves that nothing else changed.
- Scratch tools (session 3103c128's scratchpad): `vm/feed-server.ps1` (an `HttpListener` feed with a request log and a
  throttle), `vm/make_feed.py` and `vm/make_variants.py` (feed folders from packages), `probe/github_probe.cs` (the
  app's client against the real GitHub).

**Verified:** see §5.

**Limits / not built:**
- **The regular instance against a real newer release:** done on 2026-10-10 on the owner's own install, with the
  real 0.2.6 and 0.2.7 (§5): four updates from four ways of installing, each 11 to 12 s from the click to the
  running new version, plus *Later*, *Skip this version*, *Cancel* and an install folder that was in use. (Before
  those releases existed, a build labelled 0.2.3 ran the path up to the installer of the real 0.2.5 on a test
  desktop, 2026-10-09.)
- ARM64: built by the release job, never run. A standard user account: not run (the owner's PC is an administrator
  account with UAC on; the app, the installer and the updater ran there without elevation). A copy that runs as
  administrator: only on the VMs, where UAC is off.
- **After a failed installation, the reason is easy to miss.** The restarted app reports it in a notification
  (Windows shows one for about 5 s; whether it stays in the notification center was not checked) and in the log;
  Settings and the dialog then show the plain offer again (§6).
- No delta updates (71 MB each time), no silent or automatic installation, no update at exit.
- No Authenticode check of the package: releases are unsigned (§3.3).
- The README's update photo shows a build labelled 0.2.3 that is offered the real 0.2.5 (§3.4): retake it with the
  first real update.
- **Since 2026-11-08 (§2.39):** a copy the installer installed also offers the development builds of its dev-builds
  folder, before GitHub's releases, and a development build is always offered GitHub's latest release (the way back).

### 2.30 The weekly update reminder, and tooltips that took the keyboard (built 2026-10-10)

User request (2026-10-10): "Once a week, can we show a tooltip over the update icon (preferably above the window to not
hide anything) when theres an new version. No dismiss required, just check and show weekly. no "dont show again" or
anything as its a simple tooltip".

**What the user gets.**
- While a newer release waits, the update button's own tooltip ("Update available: BetterClipboard x.y.z") opens by
  itself 0.4 s after a summon, stays 6 s, and closes by itself or with the panel. At most once in seven days.
- **Above the panel's window:** centered on the button, 4 DIPs over the window's top edge, where it covers nothing of
  the panel. With less than 56 DIPs of room there (the panel at the top of the screen) it opens to the left of the
  button, over the header's title. Never under the button: the search box is there.
- Typing, Enter and Esc work as always while it shows. A click on it opens the update dialog.
- **No switch of its own:**
  - *Check for updates automatically* off: no reminder. The same holds for copies that never check by themselves (a
    Chocolatey copy, an isolated instance without a stand-in feed).
  - *Skip this version* silences it for that version.
  - Nothing shows while an approved update downloads or is installed.
  - A development build is never reminded (since 2026-11-08, §2.39): its button is lit on purpose, as the way back.
- **Opening the update dialog starts a new week** (from the panel or from Settings, while an update is offered): *Later*
  means "in a week", not "at the next Win+V".

**Pieces.**
- **Core `Updates/UpdateReminder`** (pure, tested): `IsWanted(snapshot)`, `IsDue(snapshot, lastShown, now)`, `Interval`
  7 days, `ClockSlack` 1 day, `FitsAbove(room)` with `RoomAboveDip` 56.
  - A remembered time more than a day ahead of the clock counts as never: believing it would silence the reminder
    until that time plus a week. A time a little ahead is believed, so a clock correction does not show it twice.
  - One clock for every version: a release that arrives two days after a reminder waits five days.
- **`AppSettings.UpdateReminderShownUtc`** (`DateTimeOffset?`, written as UTC). An older version that saves the settings
  file drops it; the next newer version then reminds once more, at most a week early.
- **`AppController`:** `IsUpdateReminderDue`, and `NoteUpdateReminderShown(byReminder)`, which queues the settings
  write at low priority (the callers are showing a tooltip or opening a dialog) and keeps the time in
  `updateReminderNoted` until then. One Info line per shown reminder; it names the offered version and nothing else.
- **`ClipboardFlyout.UpdateReminder.cs`:** stages None, Waiting, Unseen, Seen, driven by one `DispatcherQueueTimer`.
  - `ScheduleUpdateReminder` runs at the very end of `ShowAt`, after everything a summon owes the user.
  - `CancelUpdateReminder` ends it from: `Conceal` (every way the panel goes away), the start of `ShowAt`, the update
    dialog's `Opening`, a window move or resize (`AppWindow.Changed`, subscribed only while the tooltip shows), an
    update state that no longer wants one (`ApplyUpdateButton`), and the tooltip's own `Closed`.
- **`UpdateDialog`:** `OnOpening` notes the new week; `OnClosed` notes it when the offer arrived while the dialog was
  open.
- **`ClipboardFlyout.ToolTipKeyboard.cs`:** the keyboard guard (below).

**What counts as shown.** Most uses of the panel last a second: Win+V, Enter. So the week starts once the reminder has
been on screen 1.5 s in total; `updateReminderSeenFor` adds up the glimpses (per session). Measured: Esc 1.0 s and
1.1 s after Win+V left the setting empty both times; at the third summon the reminder counted 0.7 s after Win+V; the
fourth summon showed nothing.

**Facts about WinUI tooltips** **[verified on a claude-desktops VM, WinUI 2.3.9]**:
- **Opening one from code:** `IsOpen = true` works for the tooltip an element owns, set as an object
  (`ToolTipService.SetToolTip(element, toolTip)`). So the update button's tooltip is an object now, and
  `ApplyUpdateButton` changes its `Content` instead of setting a new string (which would close an open one).
- **It then stays** until `IsOpen = false`: no timeout, and neither a key press nor the pointer moving about closes it.
- **It is a window of its own** and leaves the panel's bounds. It follows the panel's theme.
- **Its default place** (`Placement` Top) is flush on its button, across the window's top edge: tooltip 270–301,
  window edge 293, button from 302 (pixels at 100 %).
- **`PlacementRect`** (in the owner's coordinates) is the area the tooltip is placed against, flush and centered. The
  reminder uses the button stretched up to 4 DIPs over the content's top: tooltip bottom 287, window edge 292, button
  886–919 against tooltip 784–1020. At 150 %: gap 6 px, centers 913 and 911.5. `CancelUpdateReminder` clears it, so
  the hover tooltip keeps WinUI's own place.
- **Its content gets no pointer events:** a `PointerPressed` handler added with `handledEventsToo` never ran.
- **A click on any tooltip took the keyboard from the panel.**
  - What happens: `Root.LostFocus` fires with the search box's `TextBox` as its source, and
    `FocusManager.GetFocusedElement` is then the `ScrollViewer` around the whole window content, an *ancestor* of
    `Root`. Win32 focus stays inside the window, and the window stays in front.
  - The effect: the panel's key model listens on `Root`, so it hears nothing. Typed letters were lost until the next
    summon. UI Automation reports the focus on the `InputSiteWindowClass` pane.
  - This was there for every hover tooltip (they stay open under the pointer, so they can be clicked). A tooltip that
    opens by itself and says "Update available" makes the click likely.
- **The guard:** on `Root.LostFocus` while a popup that is not ours is open, a queued check gives the keyboard to the
  search box when the focused element is neither inside `Root` nor inside an open popup. When the pointer is over the
  update button's tooltip it opens the update dialog instead, as a click on the button would.
  - The tooltip's screen rectangle comes from `tip.TransformToVisual(null)` plus the window's client origin:
    computed 787, 256, 231 × 32 against 784–1020 × 256–287 in a screenshot.
  - It only acts while a tooltip is open, with no popup of ours and no drag: every other way the keyboard moves in
    the panel is as before.

**Test and dev** (session 3103c128's scratchpad, `tip/`).
- A build labelled 0.2.6 from the working tree (`package.ps1 -Version 0.2.6`), installed on the test desktop with
  `install.ps1 -Package`: the real feed then offers the real 0.2.7, and the button is lit a minute after the start.
- `vm-helpers/helper-reset.ps1` closes the app, edits `settings.json` (when last reminded, skipped version, the check,
  theme, groups column, panel height), starts it and waits for "Panel prepared for the first Win+V".
- `vm-helpers/helper-shoot.ps1` presses Win+V and runs a timeline measured from that key: screenshots, keys, typing,
  mouse moves, clicks and drags. `helper-who.ps1` prints the foreground window and the focused element.
- `sheet.py` puts the frames of a run on one contact sheet.

**Verified:** see §5.

**Limits / not built:**
- Narrator's reading of the tooltip that opens by itself was not listened to.
- Glimpses are added up per session only: a restart costs at most one more short showing.
- A panel that is moved or resized while the reminder shows closes it; it is not shown again in that summon.
- Not run: a real GPU, several monitors, Windows' text size raised (the 56 DIPs of room are sized for it, and WinUI
  moves a tooltip that does not fit by itself), touch.
- No photo of it in the README.

### 2.31 Arrow keys in the groups column: land on what is shown, pick groups for a merged view (changed 2026-10-10)

User request (2026-10-10): "We have integration with up-arrow going to the groups area and picks the lowest group Kindly
want you to instead of picking lowest/last group pick the first selected one (if none the top). If multiple selected, only
space/enter press or click on group will add the group into the list of selected groups and thus we get. but if we have
one group selected then maintain current behavior where we default to top now."

**Corrected by the user the same day**, after the first version ran on their PC as the 0.2.8 release candidate: "Oh no,
up-arrow picks the 1st group, i meant pick the icon of Clipboard which is basically show all, no groups. not the 0
index."
- **"The top" is the Clipboard icon** (the logo: the whole history, no group). It is not the first group, which the user
  calls "the 0 index".
- The first version (`9ca6425`) read "the top" as the top group, opened that group, and said so in its reply. The
  correction is one rule in `PanelNavigation.EnterGroups`: no group shown answers position 0.
- **The rule that follows: walking in never changes what is shown.** The ring lands on the icon that the column
  highlights already: the first group shown, or the logo when none is shown.

**Extended by the user the same evening**, after the corrected candidate ran on their PC: "If not multi-select and user
is on the last group existing and he presses down the move. If no multi-select and user uses enter/space then select
that group and go back to focusing on the tabs row. multiselect remains same".
- The first sentence stops in the middle. I asked one question, and the user chose: **Down on the last group leaves the
  groups** (the other offer was a wrap to the top of the column).
- **Not multi-select = a walk that shows.** There, two more keys go back to the list with the group the keys are on:
  a new press of Down on the lowest group, and Space or Enter. Before, only Right did; Enter pasted the selected card,
  and Space was typed into the search box.
- **"Go back to focusing on the tabs row"** was built as the end of the walk: Up and Down move through the rows again,
  Left and Right through the tabs, and the search box keeps the keyboard, so typing still searches. The tab strip
  itself gets no focus rectangle. That reading was told to the user in the reply.
- **Multi-select (a walk that picks) is unchanged:** Down stops on the lowest group, and Space and Enter pick.

**The 0.2.8 release candidate** (state on 2026-10-10, evening).
- Another session bumped the version (`fe3367b`, "Version 0.2.8") and installed that build on the user's PC before the
  correction. That copy (`0.2.8+fe3367b`) had the first version's rule: with no group shown, Up opened the first group.
- The correction is `c0db2d1`. With the user's go-ahead, 0.2.8 was built again from it and installed on their PC the
  same evening (`0.2.8+c0db2d1`, §5). The extension above is `03f5058`; it came after that install and was installed
  the same way: the PC runs `0.2.8+03f5058`.
- **Released as v0.2.8 on 2026-10-10:** the tag is on `ccde6fb`, which has the app code of `03f5058` (only CLAUDE.md
  changed after it). So the build on the user's PC (`0.2.8+03f5058`) has the released code. §5 has the release row.
- The tag notes' draft in `fe3367b` says "No group open: the top group opens and gets the ring." The current draft is
  in the message of the newest commit that carries the marker line (§3.1's anchored lookup finds it).

**What the user gets.**
- **Up on the top row lands on the first group shown** (the topmost one in the column), with the ring on its icon.
  Nothing else on screen changes.
- **No group shown:** the keys land on the logo, and the list stays the whole history. Down then shows the first group.
  Before: the lowest group was shown at once.
- **One group shown, or none: a walk that shows** (the behavior since 2026-10-09, §2.28). Every step shows what it lands
  on: a group alone, or the whole history on the logo.
  - Space, Enter, Right, or a new press of Down on the lowest group go back to the list with that group. The next
    Enter pastes.
  - A held Down stops on the lowest group, and a held Space or Enter goes back once and does nothing more.
  - The footer shows `GroupsKeyHint` ("↑ ↓ groups · ↵ or → back to the list").
- **Several groups merged: a walk that picks.** Up, Down and the page keys move only the ring. The merged view stays.
  - Space or Enter adds the group under the ring to the view, or takes it out when it is shown: a Ctrl+click from the
    keyboard. On the logo: the whole history.
  - The footer shows `GroupsPickKeyHint` ("Space/↵ add or remove · → back · ↑ ↓ groups").
  - The ring has two looks: on a shown icon (highlight and ring), and on an icon that is not shown (ring only,
    `GroupButtonKeyboardUnselectedStyle`).
- Right, typing, the mouse over a row and a click on an icon end both kinds of walk, as before.

**Decisions where the request left room** (told to the user in the reply; change them here if they ask):
- ~~**"The top" is the top group,** not the logo.~~ Wrong: the user meant the logo (the correction above). The reason
  for the wrong reading was that the request sets "the top" against "lowest/last group". In this user's words, the
  groups area includes the Clipboard icon, and "the top" of it is that icon.
- **"The first selected one" is the topmost shown group in column order,** not the group that was clicked first. The
  selection keeps no click order (`GroupSelection.Ids` follow the column).
- **The mode is decided when the keys walk in, and holds for that walk** (`GroupWalkMode`).
  - A walk that picks goes on picking after a pick left one group or none, so the next group can be added at once.
  - A mode that followed the count at every key would replace the one group left at the next arrow key.
  - The next walk decides anew.
- **Space and Enter toggle** (add, or take out), not only add: the keyboard's Ctrl+click. Without that, the keyboard
  could not take a group out of the view.
- **Enter acts on the group only when pressed alone**, in both walks. Shift+Enter (paste as plain text) and Ctrl+Enter
  (run) keep working on the selected card, like Ctrl+P and Del. A plain Enter never pastes while the keys are in the
  column: it goes back first (or picks), and the next Enter pastes.
- **Space acts with or without Ctrl and Shift** (Ctrl+Space is Explorer's key for a pick). Never with Alt or Win: those
  are Windows' and other programs' shortcuts, and AltGr arrives as Ctrl+Alt.
- **A held key acts once** (`GroupPickAction.Swallow` for the repeats). Every pick reloads the list. And the Enter that
  ended a walk is still down when the walk is over: its repeats would paste the selected card, so the panel remembers
  the key the groups took (`groupKeyHeld`) until it is pressed anew.
- **Down leaves only from the lowest group, only for a new press, only in a walk that shows.** A held Down stops there,
  like every held key at an edge (§2.28). Page Down never leaves, as Page Up never enters. In a walk that picks, the
  keys are busy choosing groups, and the way out is Right.
- **On the logo, Space and Enter go back with the whole history.** The rule is one for every position: keep what the
  keys are on, and go back.
- **The mouse is unchanged.** A plain click shows that group alone, Ctrl+click adds or takes out, Shift+click shows a
  run. The request's "click on group" was read as the Ctrl+click that exists. **Open question for the user:** must a
  plain click add while several groups are shown?
- **A walk that shows has no "add" key.** A merged view starts with the mouse (Ctrl+click, Shift+click) or the icon
  menu. The keyboard then changes it.

**Pieces.**
- **Core `Presentation/PanelNavigation`** (pure, tested):
  - `GroupWalkMode` (Show, Pick), `GroupsEntry` (position and mode), `GroupsMove` (position, or the way out),
    `GroupPickAction` (NotHandled, Pick, Swallow, SelectAndLeave);
  - `EnterGroups(column, shown)` replaces `EnterGroups(groupCount)`. No group shown answers position 0, the logo.
    Shown ids that are not in the column count neither as "the first" nor towards "several";
  - `StepInGroups(position, groupCount, delta, mode, isRepeat)`: a move, or the way out at the lower end.
    `MoveInGroups` stays the arithmetic under it;
  - `PickKey(…, isRepeat, repeatsGroupKey)`: Pick in a walk that picks, SelectAndLeave in a walk that shows, Swallow
    for every repeat that is the groups'.
- **App `ClipboardFlyout.Navigation.cs`:**
  - `groupWalkMode`, and `pickCursorGroupId`: the ring of a walk that picks, kept by group id, so it stays on its group
    when the column reloads. A walk that shows still derives its position from the group shown;
  - `PickGroupByKey`, `PickGroupAtCursor`, `SelectGroupAndLeave`, `GroupIdAt`, `BringGroupIconIntoView`;
  - `groupKeyHeld`: the Space or Enter whose last press the groups took, so that its repeats do nothing after the
    walk ended;
  - `keysChangingGroups` (was `movingGroupCursor`) and `EndGroupsNavigationUnlessOwnChange` (was `…UnlessMoving`): a
    pick is the keys' own change too, and must not end their walk.
- **App `ClipboardFlyout.xaml.cs`:** `Root_PreviewKeyDown` sends Enter and Space through `PickGroupByKey`; `GroupStyle`
  puts the ring on the look that fits; `GroupItemStatus`.
- **App `FlyoutViewModel`:** `SetGroupsNavigation(GroupWalkMode?)`, `GroupsPickKeyHint`, and `GroupsKeyHint` without
  "↵ paste" (Enter goes back there).
- **UI Automation:** an icon's `ItemStatus` reads "Shown", "Arrow keys here", "Shown, arrow keys here", or nothing.
  The logo's status says only where the keys are. An icon the keys are not on reads as it did before.

**Facts found on the way** **[verified on a claude-desktops Windows desktop, 2026-10-10]**:
- **A key marked handled in `Root_PreviewKeyDown` is not typed into the search box.** Space taken there left the box
  empty (UI Automation value of length 0), also for a held key and with Shift. Before this change, the panel took no
  key there that produces a character.
- **The footer's hint has room for about 41 characters at the default width** while it shares the line with the count
  of three named groups ("3 in Work + Home + Ideas"). The hint trims its end. The first wording ("↑ ↓ groups · Space
  or ↵ add or remove · → back to the list") lost the way back, so the final one starts with the pick keys and the way
  back.
- **A group icon under the pointer loses its highlight:** the button's pointer-over look replaces the style's fill and
  text color. That is not new. Move the pointer away before a screenshot is judged.
- **The documentation hook needs every line between a doc comment and its method to end with `]`** (§4).

**Verified:** see §5.

**Limits / not built:**
- Nothing announces the ring to a screen reader (the focus stays in the search box). In a walk that picks, the ring is
  the only thing a step changes. The item status says it, but only to a reader that asks.
- Shift+Space as a Shift+click (a run from the anchor), and a key that starts a merged view from a walk that shows.
- Not run: the dark theme's look of the ring-only style; a column longer than the panel (the ring's icon is scrolled
  into view by the same call as before); deleting the group under the ring (the ring then shows on the logo).
- The README's photos do not show it.

### 2.32 Ditto: the clipboard manager users come from (research 2026-10-16; nothing built yet)

User requests (2026-10-16): "do we support anything with ditto?" (no: nothing in `src/` or `tests/`), then "Learn all
about Ditto". The full report is [`docs/ditto.md`](docs/ditto.md); this section keeps what BetterClipboard's code and
decisions need. How it was learned:
- Source `sabrogden/Ditto` at `a80fd35` (2026-09-30) and its wiki at `6c3581c`, in `refs/Ditto`, `refs/Ditto.wiki`
  (git-ignored, **GPL-3.0: read for interoperability, nothing copied**).
- Release 3.25.113.0 (2025-09-27; SHA-256 equal to GitHub's digests; signed by SignPath Foundation), run in a
  claude-desktops Windows 11 desktop with BC-TEST data only. The desktop was deleted afterwards.
- Public numbers from GitHub, SourceForge and Chocolatey.

**What it is.**
- A C++/MFC clipboard manager by Scott Brogden, on SourceForge since 2003-06-24 and on GitHub since 2020.
- Reach: 7,314 stars; 4.15 M SourceForge downloads; ~2.65 M GitHub release downloads; Chocolatey `ditto` 459 k
  (+ `ditto.install` 112 k, `ditto.portable` 84 k); winget `Ditto.Ditto`; the Microsoft Store.
- **No release for a year:** `master` is 70 commits past 3.25.113.

**Facts BetterClipboard depends on** **[verified]** unless marked:
- **Where the history is:** plain SQLite, **not encrypted**. `%APPDATA%\Ditto\Ditto.db` for the installed copy, next to
  `Ditto.exe` for the portable one.
  - The path is the setting `DBPath3`: `HKCU\Software\Ditto`, or `[Ditto]` in a UTF-16LE `Ditto.Settings` ini (relative
    = the exe's folder). Environment variables are allowed.
  - Marker files next to the exe pick the kind: `portable`, `chocolatey` (`%LOCALAPPDATA%\Ditto_ChocolateyApp`),
    `WindowsApp` (the Store) **[source]**. The table is in the doc's §2.
- **Schema:** `encoding = UTF-16le` (opened with `sqlite3_open16`), `auto_vacuum` full, `journal_mode` delete,
  `user_version` 0.
  - `Main` holds one row per clip or group:
    - `lDate`: Unix seconds of the first copy;
    - `mText`: the first 500 characters;
    - `clipOrder`: a REAL counter that orders the list, not a time;
    - `lastPasteDate`;
    - `lParentID`: −1, or the group's `lID`;
    - `bIsGroup`;
    - `lDontAutoDelete`;
    - `stickyClipOrder`: −2147483647 when not sticky;
    - hotkeys, quick-paste text.
  - `Data` holds one row per format: `strClipBoardFormat` + `ooData`, raw bytes.
  - The format names are BetterClipboard's own (`ClipFormatNames`): `CF_UNICODETEXT`, `CF_DIB`, `CF_HDROP`,
    `HTML Format`, `Rich Text Format`, `PNG`. Text also keeps the synthesized `CF_TEXT`.
  - Deletes go through a `MainDeletes` trigger: data stays in the file until a pass when the PC has been idle 10 minutes
    **[source]**.
- **Reading it while Ditto runs:** a reader that shares write access works (`FileShare.ReadWrite`); `FileShare.Read`
  fails with a sharing violation.
- **Ditto drops copies by design.** Every notification restarts a 100 ms timer, and a copy within 500 ms of the last
  saved one is dropped, logged only as "Clip copy to fast" (`SaveClipDelay`).
  - Measured with 10 copies per gap: 1 kept at 5 and 50 ms; 4 at 200 ms; 5 at 400 and 450 ms (**the last one lost**);
    10 at 550 ms and up.
  - BetterClipboard, side by side on the same copies: 10 of 10 at 5 ms and at 200 ms.
  - Two copies 343 ms apart: the second never reached Ditto.
- **Privacy markers:** 3.25.113 honors only `Clipboard Viewer Ignore`.
  - It **stored** copies marked `ExcludeClipboardContentFromMonitorProcessing` and `CanIncludeInClipboardHistory = 0`.
  - `master` added both on 2025-12-05 (`b0533af`), unreleased. Until a release, password managers that set only those
    markers end up in Ditto's plain database.
- **Network:** the installed copy listens on **TCP 0.0.0.0:23443** from its first start, to receive clips from
  "friends".
  - Windows showed its firewall prompt at once ("Publisher: Unknown": the version resource has no CompanyName); Cancel
    added inbound Block rules. The portable copy does not listen.
  - Encryption: AES-256 with a shared password, default `LetMeIn` **[source]**.
- **Pasting:** Ditto writes the clip through OLE **plus `Clipboard Viewer Ignore`**, re-activates the target and sends
  Ctrl+V with `keybd_event`.
  - After a Ditto paste the clipboard held `DataObject`, `CF_TEXT`, `CF_UNICODETEXT`, `Clipboard Viewer Ignore`,
    `Ole Private Data`, `CF_LOCALE`, `CF_OEMTEXT`; the owner was Ditto.
  - Into an elevated window it starts an elevated copy of itself with `runas` (a UAC prompt) **[source]**.
- **Hotkeys:** `RegisterHotKey` only, default Ctrl+\`; it cannot get Win+V while Explorer holds it (its wiki suggests
  AutoHotkey). A hotkey that is taken fails silently (a log line).
- **No source app is stored,** and Windows' own clipboard history is not imported **[source]**.
- **Footprint at rest** in the test desktop: Ditto 4.9 MB private and 47–109 ms CPU a minute; BetterClipboard 106.6 MB
  and 297–844 ms (its panel is kept rendered for an instant Win+V, §2.27).

**Next to BetterClipboard.**
- Both record every normal copy; the two kept counting with 0 locked out.
- Ditto's pastes are skipped by BetterClipboard: its log says "Skipped a copy marked private by 'Ditto'". The item is
  in Ditto already.
- BetterClipboard's pastes become new Ditto entries: it writes no ignore marker. Keep it that way; Win+V's history would
  lose them too. An option at most.
- Win+V and Ctrl+\` do not clash.

**Proposed, not built:** "Import from Ditto", read-only, in Settings › Integrations. The doc's §8 has the locating
order, the mapping and the rules:
- sticky or never-delete clips become pinned;
- groups become groups;
- `CF_TEXT` next to `CF_UNICODETEXT` is dropped;
- a new `ClipOrigin.Ditto` = 12 with the Windows import's semantics;
- `.dto` exports too.

**Tooling.**
- [`tools/probes/probe_ditto.py`](tools/probes/probe_ditto.py) finds Ditto's databases, describes one (structure,
  counts, formats, payload checks; never content) and has a `--self-test`. It read the test desktop's real databases
  correctly.
- The desktop scripts are in session 46390d28's scratchpad, `ditto/vm/`: BC-TEST copies through plain Win32 calls,
  markers, bursts, a clipboard description that does not open the clipboard, a hotkey probe.

**Found on the way: the README's install command was broken** (§3.1; fixed 2026-10-16, reaches users with a push). The
`irm …/install.ps1 | iex` command failed in Windows PowerShell 5.1 and PowerShell 7 with "The attribute cannot be added
because variable PackageSha256 with value would no longer be valid".
- The cause: `[ValidatePattern]` on `-PackageSha256`, added in `7d7df8d` (2026-10-09). `| iex` checks it against the
  empty value.
- Since the push of 2026-10-10 (v0.2.6), the README's first command printed only that error. Its other form,
  `& ([scriptblock]::Create(…))`, kept working, and every test used `-File` or that form.

### 2.33 Rich previews: Word, Teams, LibreOffice and web copies (research 2026-10-16; nothing built yet)

User request (2026-10-16): "When someone copies from teams or microsoft word or open office or libre office or even
html — The clipboard preview is not supporting a preview.. can we think about that and other types of clipboard data
showing a preview or even potentially tooltip and opening the preview like an image now supports. Task: Discover." The
full report is [`docs/rich-previews.md`](docs/rich-previews.md); this section keeps what the code and the decisions need.
- Measured with the new [`probe_rich_formats.cs`](tools/probes/probe_rich_formats.cs) and
  [`preview_lab`](tools/probes/preview_lab) in a claude-desktops Windows 11 desktop (LibreOffice 26.2.5.2, the owner's
  build; Edge 155; BetterClipboard 0.2.8; BC-TEST documents only). The desktop was deleted afterwards.
- Word, Teams, Outlook and Excel were not run (a license and an account do not belong in a test desktop): their facts
  are **[docs]** or **[inferred]** until the owner runs the live check below.

**Today** **[verified, 0.2.8]**:
- A copy with HTML or RTF next to its text is a "Formatted text" card that shows only its plain text (4 lines, caption
  "· formatted"). There is no peek and no viewer: the overlays of §2.24 exist for image cards only. HTML or RTF without
  text reads "Formatted content", with an empty search text.
- Stored: text, HTML, RTF, PNG, one DIB; never a metafile (a handle format). Calc's and Excel's picture of the range is
  stored but never shown: image analysis runs for `Image` cards only.
- Writer's copy was drawn in the code font: `CodeHeuristics.LooksLikeCode` counted its tab-indented list items and one
  line ending in `;`.
- A file copied from inside a .zip (`FileGroupDescriptorW` + `FileContents`, no path, no text) was not recorded at all.
  Outlook attachments and phone (MTP) files arrive the same way **[inferred]**.

**What the apps give.**
- **LibreOffice Writer** **[verified]**: RTF (103 KB, all pictures inside as `\pngblip`); CF_HTML 1.0 whose pictures
  are `file:` paths into LibreOffice's temp folder (gone when it exits); a plain HTML document; and **`Markdown`** (new in
  26.2). No picture of the selection. Everything is rendered on request through OLE (the plain HTML took 118–173 ms).
- **LibreOffice Calc** **[verified]**: 24 formats, among them a PNG and a DIB of the range (both stored by
  BetterClipboard), an EMF+ dual EMF (rendered on request in 30 ms) and an SVG.
- **Edge, i.e. Chromium** **[verified]**: CF_HTML 0.9 with `SourceURL`, every element's computed style inline (24
  properties), `data:` pictures inline and every other picture as an absolute `http(s)` URL; text; `Chromium internal
  source URL`. No RTF and no picture. New Teams is a WebView2 app, so its copies have the same shape; its pictures are
  links that need the signed-in session **[inferred]**.
- **Word and Outlook** **[docs]**: RTF with the pictures (`\shppict`, plus a `\nonshppict` WMF copy), HTML whose pictures
  are `msohtmlclip1` temp files, an EMF of the selection on request. **Excel** **[docs]**: a bitmap (stored as a DIB), an
  EMF, Biff, XML Spreadsheet, HTML, RTF, Csv. **VS Code** **[docs]**: HTML with the syntax colors, plus
  `vscode-editor-data` naming the language.

**Engines** **[verified]**:
- **WinUI `RichEditBox`**, read-only, with RTF: LibreOffice's RTF with all its PNG pictures, the table, both lists,
  colors, the highlight, the link and a color emoji. 103 KB: 855 ms the first time in a session, 11 ms after; 2 MB with 40
  pictures: 92 ms, then 45 ms; +30–40 MB. Nothing to fetch.
- **The classic RichEdit window** (`RICHEDIT50W`, what a thumbnail off the UI thread would use): pictures only with an
  `IRichEditOleCallback`, and even then `\pngblip` is dropped (`\dibitmap` draws).
- **WebView2** with scripts off and a `WebResourceRequested` filter on `*` that answers 403: Edge's copy exactly as the
  page. 4 picture requests blocked, 0 reached the local server; the control run (requests allowed) let 4 through.
  Start-up 3.4–18.8 s, first page 1.6–3.8 s, next 0.25–0.34 s (GPU-less VM); 5 processes, ~257 MB; `NavigateToString`
  takes at most 2 MB **[docs]**.

**Rules for any preview:** never fetch a copy's pictures (tracking pixels; Teams' links need the session), never run its
scripts; show `data:` pictures and alt text only. Word's and LibreOffice's HTML pictures are temp files, so for office
copies the RTF is the source that lasts.

**Proposal, cheapest first** (the owner decides; docs §7):
1. Show what is stored: a thumbnail and the §2.24 peek and viewer for `RichText` entries that carry a PNG or DIB
   (spreadsheets); plain text from the HTML of HTML-only copies (preview, search, plain paste); no code font for a
   formatted copy unless it is code; "· 3 pictures · table" in the caption.
2. A peek for every card, a viewer for formatted copies: RTF in a read-only `RichEditBox`, prepared with the panel
   (§2.27); HTML through an own HTML → XAML converter (like `MarkdownRenderer`, §2.29); WebView2 only in the viewer, on
   request, with every request blocked; the full text for long text and code, shell thumbnails for files, a large swatch
   for colors, the parts of a link.
3. Capture more: virtual files (`FileGroupDescriptorW`), LibreOffice's `Markdown`, perhaps Word's and Excel's EMF for
   small selections (its cost is unknown).

**Live check on the owner's PC** (content-free): `dotnet run tools/probes/probe_rich_formats.cs -- --watch 180`, then
copy from Word (a paragraph with a picture and a table), Teams (a message with an emoji and a picture), Excel (a range)
and Outlook. Each copy is read 750 ms after its notification, so BetterClipboard reads it first. The probe prints
formats, sizes, render times and structure, never content, and never writes to the clipboard.

**Found on the way** (§4): richedit.h's structures are 4-byte packed; a WinUI 3 app needs an App.xaml for
`XamlControlsResources` ("Cannot find a resource with the given key: AcrylicBackgroundFillColorDefaultBrush");
LibreOffice's HTML import fetches remote pictures; a desktop's `run_command` with `Start-Process
-RedirectStandardOutput` blocks the agent until the child exits.

### 2.34 Start with Windows for every user, and `install.ps1 -AllUsers` (built 2026-10-18)

User request (2026-10-18): "Are we supporting startup by default after installation? meaning that user installing
BetterClipboard by default opts in to launch on startup. Support user way or machine way if not the user is
installing". Asked one question (scope): the user chose "Also install.ps1 for all", over the Chocolatey-only option,
knowing that updates of such a copy needed their own design.

**The answer to the question, before this change [verified from the code]:**
- `install.ps1`: yes, always the user way. It writes `HKCU\…\Run` `BetterClipboard` = `"<exe>" --background` unless
  `-NoStartup`.
- The Chocolatey package: yes for the signed-in user who installs. As SYSTEM or with another account's credentials it
  wrote nothing and only warned.
- `install.ps1` could not run as SYSTEM at all: SYSTEM's profile has no Start menu folder, `GetFolderPath('Programs')`
  answers `''`, and the top-level `Join-Path` stopped every run before its first step (found by the first SYSTEM test,
  fixed here).

**"Not the user" and "the machine way"** (`Resolve-InstallScope`, and the same rule in the package):
- **The user way:** the account that installs owns this session's desktop (Explorer). Also with no desktop at all (a
  remote shell as a regular account): nothing tells whether it is the one who uses the PC, so it stays as before.
- **The machine way:** the installer runs as SYSTEM, or with administrator rights on another account's desktop
  (over-the-shoulder credentials), or it is asked for: `install.ps1 -AllUsers`, the package's `/AllUsersStartup`.
  `install.ps1` also picks it for an elevated run when a copy for every user is registered and no other
  `-InstallDir` is given (re-running the install command updates that copy).
- **The entry for every user:** `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run` `BetterClipboard` =
  `"<exe>" --background --all-users-startup`, always in the 64-bit view (`RegistryKey.OpenBaseKey(…, Registry64)`).
  A 32-bit PowerShell (an RMM agent's, Chocolatey's `--x86`) would otherwise write a WOW6432Node copy that the 64-bit
  app never reads.

**Why the per-user "off" lives in BetterClipboard [verified on this PC, 2026-10-18]:**
- Windows keeps the on/off mark of an `HKLM` Run value in `HKLM\…\Explorer\StartupApproved\Run`, never in the user's.
  On this PC every `HKLM\…\Run` value (4, plus 2 in the 32-bit view) had its mark there, and none in the user's
  `HKCU\…\StartupApproved\Run`. So only an administrator can turn an all-users entry off in Task Manager or
  Settings › Apps › Startup, and then for everyone.
- Marks are 12 bytes. An odd first byte means off: 3 and 7, and 9 when a policy locked it. Even means on (2, 6). No
  value means on. Forensic write-ups, checked against this PC's values.

**The app** (`Core/Startup/SignInStartup.cs`, pure and tested; `Windows/Shell/StartupRegistration.cs` reads and writes):
- **`AppSettings.SkipAllUsersStartup`** (default false): this user turned Start with Windows off while the entry for
  every user starts BetterClipboard. Footgun: an older version that saves the settings file drops it, and the entry
  starts the app again from the next sign-in.
- **Stepping aside** (`Program.ContinueFromAllUsersEntry`, before the single-instance lock, COM and XAML): a process
  started with `--all-users-startup` ends at once when this user turned it off, or has a Run value of their own whose
  program exists. Their entry then starts BetterClipboard, or the user turned it off in Windows' list; an "off" mark
  there still means off. Both cases are logged (content-free).
  - Before the lock on purpose: a process that took the lock and then ended could have turned the user's own start
    away as "already running".
  - It fails open: when the facts cannot be read, the process starts.
- **A second `--background` start no longer opens Settings.** Until now it signalled Activate to the running instance,
  so two entries (the user's and the one for every user) would have opened Settings at every sign-in. A start into the
  tray that finds BetterClipboard running now ends without a signal.
- **The switch** (Settings › App › Start with Windows) shows `StartsThisCopy`:
  - when the user has a live entry, that entry decides alone: it starts this copy and Windows has it on;
  - otherwise the entry for every user starts this copy, it is not off in Windows, and the user did not turn it off.
- **Flipping the switch** (`Plan`):
  - Off: removes the user's entry for this copy, and declines the entry for every user (sets the setting), also while
    an administrator has it off.
  - On: uses the entry for every user when it starts this copy and is on. It then writes nothing; it removes a stale
    entry of the user's and clears an "off" mark on one for this copy. Otherwise it writes the user's own entry, as
    always.
  - A value written again keeps Windows' "off" mark under its name, so "on" also clears that mark: the switch used to
    show "on" for an entry that Task Manager had turned off.
  - A unit test applies every plan to every combination (192) and checks that the switch then shows what was asked.
- **The card's description** (`Describe`) adds "It is set up to start for every user of this PC; turning this off
  changes that only for you." When an administrator turned the entry off, it says that turning on adds an entry just
  for this user.

**`install.ps1 -AllUsers`:**
- **Program Files:** `$env:ProgramW6432`, the 64-bit one also from a 32-bit PowerShell, plus `\BetterClipboard`; or
  the registered copy's folder. Needs an elevated run, and refuses before anything is downloaded otherwise.
- **The folder's rights** (`Set-AllUsersFolderSecurity`, on the staging folder before extraction):
  - protected, with nothing inherited: Administrators and SYSTEM full control, Users and ALL APPLICATION PACKAGES read
    and run;
  - why: every user's sign-in runs that exe. Under a parent such as `C:\` (whose subfolders give Authenticated Users
    Modify), any user could replace the program that then runs as every other user.
- **The PC's Installed-apps entry** (`HKLM`, 64-bit). Its uninstall command has `-AllUsers`, and the uninstall asks
  for administrator rights itself (`Invoke-ElevatedUninstall`: the same file, `-Verb RunAs`): Settings › Apps starts
  it unelevated.
- **The Start menu shortcut for every user** has the name the elevated Chocolatey package uses. Its target is checked
  first: another existing copy's shortcut is neither replaced nor removed. The same holds for the entry for every
  user.
- **`-AddToPath`** changes the PATH of every user (`HKLM\SYSTEM\…\Session Manager\Environment`, raw
  `REG_EXPAND_SZ`).
- **One person's things only for the signed-in user's own run:**
  - Win+V's takeover and its give-back, `installer.json`, and the launch;
  - `-Hotkey`, `-TakeOverWinV` and `-NoTakeOverWinV` are refused otherwise, before anything changes;
  - SYSTEM's run writes nothing into its own profile, and for every user the app's keyboard hook takes Win+V over.
- **Closing the app:** other sessions' copies running from the folder are stopped (`Stop-AllUsersCopyElsewhere`,
  through WMI, which gives an administrator every process's path). Their unsaved queue is lost, and the message says
  so.
- **The uninstall for every user** (`Uninstall-BetterClipboardForAllUsers`):
  - hands the entry for every user to the Chocolatey copy while that stays;
  - removes the signed-in user's own entry for this copy, or hands it to the Chocolatey copy;
  - gives back that user's Explorer shortcuts unless another copy of theirs stays;
  - leaves other users' own registry alone.
- **Messages:** a closing summary (`Write-AllUsersSummary`), and a warning when a copy of one user is installed next to
  a copy for every user (the user's own entry then goes first).
- **Never under `-Update`** (the update contract, §2.29): `-AllUsers` with `-Update` is refused, `-Update` never
  picks this scope, and `Complete-Update` refreshes the PC's entry only when it names the folder.

**Updates of a copy for every user** (`UpdateInstallKind.AllUsers`):
- **Recognized** by the PC's Installed-apps entry naming its folder, checked before the per-user records. The
  installing administrator's own `installer.json` may name the same folder, and read first it would make the copy try
  to replace Program Files.
- **No self-update and no automatic check,** like a Chocolatey copy: its users may not be administrators. The
  Settings switch is disabled, and *Check now* still asks.
- **The dialog** copies `UpdateSource.AllUsersInstallCommand`, `& ([scriptblock]::Create((irm …install.ps1))) -AllUsers`,
  to run in PowerShell as administrator. `-NoUpdateCheck` with `-AllUsers` has nothing to switch off and says so.

**The Chocolatey package:** the entry for every user as SYSTEM, for another account's desktop, or with
`/AllUsersStartup`. Without administrator rights it warns. The uninstall removes it, or hands it to `install.ps1`'s
copy for every user while that stays (`Get-BetterClipboardAllUsersInstall`), and it warns when such a copy is
installed too.

**Verified** (rows in §5): unit tests (Core 1,220, Windows 344); a harness of the new installer functions on scratch
keys (35 checks, PowerShell 5.1 and 7); `test-update.ps1`; and on a disposable Windows 11 VM:
- `test-all-users.ps1` (new, in CI): 36 of 36;
- the sign-in itself over four reboots;
- the uninstall through the PC's Installed-apps command;
- Chocolatey: 32 of 32 twice, and as SYSTEM.

**Limits / not built:**
- **The self-elevation of the uninstall** (`Invoke-ElevatedUninstall`) did not run: UAC is off in the test desktops.
  With UAC on, Settings › Apps may also ask for consent by itself; not checked.
- **A second account** signing in to a copy for every user was not run; its rights were checked through the ACL.
- **The update dialog of a copy for every user with an offer** was not seen on screen. Its words are unit-tested, and
  the Settings card's text was read on the VM.
- **Users' own leftovers:** the uninstall cannot clean other users' `DisabledHotkeys` or own entries.
- **Not started for another account's desktop:** after an install as SYSTEM or by another account, the app starts at
  the next sign-in, not at once.
- ARM64 was not run.

### 2.36 The Run and Snipping tabs show only with something in them (built 2026-11-08)

User request (2026-11-08), on the README's Tabs table, whose "Shown" column read "Always" for both: "Run — Commands you
ran with Win+R — Always - we should now always show but only if there is Run history" and "Snipping — Screenshots that
Snipping Tool and Win+PrtScn saved — Always - only when there are images from snipping tool." (§2.35 is left to the
uncommitted AI conversation in the working tree.)

**What the user gets.**
- **Run** shows while *Win+R history* is on and the history holds at least one entry with a Win+R run time: imported from
  Windows' list at the first activation, a run seen live, or a command run again from the panel. A PC whose Win+R list
  is empty gets the tab with its first run.
- **Snipping** shows while *Snipping Tool and Win+PrtScn* is on and the history holds at least one *picture* the tab
  lists: a screenshot file from the Screenshots folder (origin `WindowsScreenshot`, any tool), or a picture Snipping Tool
  copied (its path or the names "Snipping Tool" / "Win+PrtScn"). Text Snipping Tool copied (its text actions) is listed
  in the tab, but does not make it show: the request says images.
- Each tab goes with its last entry: deleted, forgotten, pruned, cleared — or, for Snipping, a picture Snipping Tool
  copied that is copied again from another app, which then takes that app's name (a screenshot file keeps its origin and
  stays). An open panel in that tab falls back to All; the remembered tab stays, so the panel returns to it when the tab
  has entries again (§2.28).
- The README's Tabs table and its Snipping and Win+R sections say so.

**How** (`Core/Services/ClipHistoryService.TabContent.cs`; `ClipStore.FindNewestInFilter` and `IsInFilter`).
- **A witness per tab, kept by the history worker:** the id of one entry the tab lists, the newest at the last lookup —
  the one retention prunes last. Every change passes through `Raise` on the worker after it was stored:
  - an Added or Updated entry is checked (one primary-key lookup) only while its tab has no witness, or when it is the
    witness (it may have left the tab);
  - a Removed entry only when it is the witness;
  - a Reset only by checking that the witness is still there and still listed. An empty tab is not looked up on a reset:
    a full history prunes an entry with every copy, and the Snipping lookup walks every picture when there is no snip.
- **Lookups** (`FindNewestInFilter`, newest first) only at the worker's start, after an import while a tab has no
  witness, and when the witness went. `ImportAsync` raises one Reset and no event per entry, so it looks up the empty
  tabs itself: that is how the first import of Windows' Win+R list fills the Run tab.
- **Costs**, measured with a scratch probe on an encrypted store of 10,000 texts and 2,000 pictures with 30 KB thumbnails
  (BC-TEST, session a6fd5958's scratchpad `probe_tabs/`):
  - the Run lookup: 0.002 ms, through its partial index `ix_clips_run` (`SCAN c USING INDEX ix_clips_run`);
  - the Snipping lookup with the kind: `ix_clips_kind` newest first, 0.003 ms with a recent snip, 37 ms with none or only
    an old one;
  - one entry by its id: 0.002–0.04 ms;
  - the Snipping filter without the kind: a table scan, 150 ms. Its columns lie behind `search_text`, in overflow pages
    for long texts.
- **Answers:** `ClipHistoryService.HasRunHistory` and `HasSnippingImages` (volatile, read from any thread), and
  `TabContentChanged` (worker thread, with the tab's `ClipFilter`). An awaited `AddAsync`, `DeleteAsync` or `ImportAsync`
  returns with the answer up to date. Failures are logged and keep the old witness.
- **App:** `AppController.IsRunTabAvailable` and `IsSnippingTabAvailable` are the setting and the answer.
  `TabContentChanged` is forwarded as `RunHistoryStatusChanged` / `SnippingStatusChanged`, whose handlers were there
  already (`UpdateRunTab`, `UpdateSnippingTab`, `ReturnToRememberedTabWhileConcealed`).
- **`RefreshSnippingIfWaiting` asks the setting, not the tab:** while the Screenshots folder does not exist there are no
  screenshots either, so the tab is away exactly when that look is needed. The live check went through this path.

**Verified** (rows in §5): 8 Core tests with a mutation check, and on a Windows 11 desktop: a fresh start, the first
Win+R run, the first Win+PrtScn, deleting the last run in the open Run tab, a restart, and a run that brings the
remembered tab back.

**Not built / open:**
- The **Cmd** tab still shows while its switch is on (not asked). Its live half — the open windows — is not in the store,
  so a witness alone would not serve it.
- The two tabs' empty states (`FlyoutViewModel.UpdateEmptyState`) are now reached only for a moment, when the last entry
  goes from an open tab. They were left in place.
- Between the app's start and the worker's first lookup (milliseconds) both tabs count as empty.

### 2.37 Interface languages: Simplified Chinese (PR #1), the first-start picker with flags, English-only logs (2026-11-08)

The first external contribution: [PR #1](https://github.com/Nucs/BetterClipboard/pull/1) by Johnson-Tang2019, "Add
Simplified Chinese UI with English fallback and language selection" (4 commits on `1ef565b`, head `29032d9`). The
owner's plan (2026-11-07): merge it on GitHub almost as it is, and develop our own changes on a branch made from it.
On 2026-11-08 it was merged into the **local** `main` only (`8c5814a`, not pushed), and our work went onto the branch
`feat/localization`, to be merged to `main` after the PR. The contributor-facing guide is
[`docs/localization.md`](docs/localization.md).

**What the PR brought.**
- `Core/Localization/UiText`: one static class; two JSON catalogs (`en.json`, `zh-Hans.json`, 559 strings each)
  embedded in `BetterClipboard.Core.dll`. Key = `T` + the first 12 hex digits of the English text's SHA-256.
- XAML: `{loc:Tr Key=T…}` (`App/Localization/TrExtension`), resolved once when a window is built (207 uses).
- C#: about 420 call sites wrapped in `UiText.Translate("English")` / `UiText.Format($"…")`, looked up in reverse by
  their exact English text; a template is translated before its values go in, so user content is never translated.
  English plural endings are passed as values (`{1}` = "s"), which Chinese templates leave out.
- Settings › App › Language (`AppSettings.InterfaceLanguage`: `system`, `en`, `zh-Hans`), read once at startup: a
  change shows fully after a restart. Traditional Chinese (`zh-TW`, `zh-HK`) gets English.
- `LocalizationTests` (13 cases) and a module initializer pinning the Core tests to English.
- Also a workflow that ran only on the fork's branch (it built a separate "zh" package), a UI smoke script
  (`tools/localization/smoke-windows.ps1`) and `README.zh-CN.md` (notes about the fork's build, not a translation).

**The owner's decisions (2026-11-08)** and what was built on `feat/localization`:
- **(a) One build, embedded resources.** Every language is one catalog embedded in `BetterClipboard.Core.dll`,
  **found by its resource name** (`UiText.LoadLanguages`): a new language is a new `Localization/{tag}.json`, with no
  code. Each catalog starts with a `"$language"` header: own name, English name, flag code, and `alsoFor` (Windows
  names that pick it exactly, `zh` for `zh-Hans`). Flags are SVGs embedded next to the catalogs
  (`Localization/Flags/{flag}.svg`), drawn by [`tools/make_flags.py`](tools/make_flags.py) (BetterClipboard's own
  drawings: no third-party licence). `LogicalName` in the csproj pins the resource names. The fork's separate-build
  workflow was deleted.
- **(b) A language picker at the first start** (`App/Views/LanguageWindow`): every language as
  *own name · flag · English name* (English shows its name once: the two names are the same word), the one that fits
  Windows' language selected (`LanguageChoice.Suggest` → `UiText.MatchSystem`), and a round green check
  (`#107C10`, white `E73E`, 5.4:1). The heading and hint switch to the selected row's language
  (`UiText.Translate(text, language)`). The check, `Enter`, `Esc`, a double click and the close button all keep the
  selection. **It runs before any other window** (`AppController.AskForLanguageAsync`): the panel's warm-up and
  Settings wait for the choice, so the chosen language applies everywhere at once, with no restart; meanwhile
  `ShowFlyout` and `ShowSettings` bring the picker forward instead.
  - **When it asks** (`LanguageChoice.ShouldAsk`): since 2026-11-09, at every start until the user answered
    (`AppSettings.LanguageChosen`) while the settings name no language: a first start, and the first start after an
    update from a version that never asked (the last paragraph of this section). Until then: only when the data folder
    held no history before this start (`AppPaths.HasHistory`: anything in `stores\`, or a v0.1 `history.db`), so users
    who updated from a version without languages were never asked. The settings file alone is not the signal:
    `install.ps1` writes it before the first start with `-Hotkey`, `-NoTakeOverWinV` or `-NoUpdateCheck`.
  - **Settings › App › Language** lists *Use Windows setting* (globe, and the language it picks now) and every
    shipped language with its flag (`SettingsViewModel.LanguageOptions`, `LanguageOptionViewModel`, `FlagImages`):
    a new catalog shows up there by itself.
- **(3) Logs and `bclip` stay English.** The PR had routed log lines through `UiText` (the update install kind, the
  Windows-import summary, half of the store-opened line, the bclip pipe error, the update-failure line, the summon
  timing fragment, "Not started." exceptions): each now logs English and shows the translation. `bclip` pins
  `UiText.Language = "en"` in `Main`, and its tables ask `RelativeTimeFormatter.Format(…, localized: false)`.
  `LocalizationTests.NoLogLineIsBuiltFromTranslatedText` scans `src/**/*.cs` for a log call that passes `UiText` (a
  planted offender made it fail; a translated value handed over in a variable is not seen).
- **(4) Gaps in the translation were deferred,** then closed on request ("Complete the gaps", the same day; below).
- **(c) READMEs in other languages live in `docs/translations/`.** `README.md` starts with a language line
  (`**ENG** / [简体中文](docs/translations/README.zh-CN.md)`); [`docs/translations/README.zh-CN.md`](docs/translations/README.zh-CN.md)
  is a full translation (it replaced the fork note), with the English section ids as explicit anchors (`#privacy` works
  in both) and the app's own Chinese labels, and it says the English README prevails.

**Language resolution** (`UiText.Resolve`, `MatchSystem`): an explicit selection that ships (any letter case), else
Windows' display language matched by exact tag or `alsoFor`, then by its parent cultures against tags only
(`zh-SG` → `zh-Hans`, `en-GB` → `en`; `zh-TW`'s parents end in `zh`, which must not reach `zh-Hans` through
`alsoFor`), else English. `UiText.Current` caches the resolution until `Language` or the display language changes.

**Robustness changes.** A catalog other than English that does not load is left out with a warning instead of
breaking every lookup; duplicate English texts no longer throw in the type initializer (first wins; a test forbids
them); the Windows tests are pinned to English like the Core tests (`HotkeyTakeoverTests` assert English status
texts). The PR's smoke script refuses to run outside GitHub Actions without `-ConfirmDisposableMachine`: it copies
the whole screen.

**Tools.** [`tools/localization/catalog.py`](tools/localization/catalog.py): `add "English" zh-Hans="中文"` (adds a
string with its key to every catalog), `rename "Old" "New"` (moves a string to its new key in every catalog and XAML
file, translations kept for review), `key`, `check`. It writes the catalogs' exact layout (verified byte for byte on
the PR's files before the first write).

**The translation gaps, closed** (2026-11-08, "Complete the gaps"). An inventory of the branch (a scan of every
literal handed to `UiText`, every catalog entry, every XAML literal and every prose literal in UI code; 413 candidates
sorted by hand) found the Chinese interface still showing English in these ways:
- **213 texts with no catalog entry**: the code translated them, the catalog did not have them, so they fell back to
  English without anything failing. Among them: the group icons' names (also a new group's default name), 24 shortcut
  problems, 22 update failures, the third-party rows' uses, the Everything and ShareX status reasons, the Win+R run
  results, the date patterns, and the regex time-out, whose template carries `{0:0}` while the catalog had `{0}`.
- **About 40 entries the code never looked up**: the search box's placeholder, the footer's key hints, "BetterClipboard —
  paused", "1 item", "1 image", "New group", the group icons' item status ("Shown", "Arrow keys here").
- **English words inside Chinese templates**: Release from Explorer's "it/them" and "is/are", the forgotten list's
  "characters" and "times", the search qualifiers ("(whole words, match case)"), the Everything tab's problem cut into
  a first letter and a rest, the Explorer confirmation's sentence about the restart, the import summary's "read".
- **Text the app does not word itself**: WinUI's own controls (a text box's Cut/Copy/Paste), which follow Windows'
  language, and the day and month names in dates ("Wed 14:02", "8 Nov"), which followed Windows' format language.

What was built:
- **Every gap wired and translated:** the catalogs went from 563 to 737 strings (217 new, the regex time-out renamed, 43
  that nothing used any more removed: log-only lines, product names, the pieces the whole sentences replaced);
  `catalog.py` gained `add-file` and `remove`. Sentences that differed for one and several keys became whole sentences, counts became one phrase per form
  (`"1 file"` / `"{0:N0} files"`), lists join with the catalog's `", "` (Chinese `、`) and `"{0} and {1}"`, the search
  qualifiers go in a `"{0} ({1})"` template (full-width brackets in Chinese), and a card's screen-reader name joins the
  kind and the preview with `"{0}: {1}"` (a full-width colon in Chinese).
- **`UiText.TranslateFormatted(text, templates)`**: translates a text that a component already worded in English for the
  log, the installer or its tests, by matching it against the component's template list (regex per template, anchored,
  cached, 100 ms timeout). The matched parts go into the translation unchanged; a part is translated only when it is
  itself in the list ("the release list"). Lists: `HotkeyGesture.ProblemTemplates`, `HotkeyRegistration.StateTemplates`,
  `RunCommandResult.DescriptionTemplates`, `EverythingOwnerVerifier.ReasonTemplates`, `EverythingLocator` and
  `ShareXLocator.DetectedByTemplates`, `EverythingIntegration.ProblemTemplates`, `UpdateMessages.Templates`,
  `SourceNames.Templates`. The update failures, the Everything reasons and the ShareX wording now come from named
  constants (`Core/Updates/UpdateMessages`), so producer and list cannot drift; the shortcut problems, registration
  states and run results are checked by tests that produce every one of them.
- **Source names BetterClipboard makes up** ("Command Prompt", "Snipping Tool", "Screenshots folder", "PowerShell in VS
  Code", "PowerShell ({0})") stay English in the store, because the tabs filter by them as SQL literals; they are
  translated where they are shown (`Core/Presentation/SourceNames.Display`: card captions, the shell tabs, the
  forgotten list).
- **Dates:** `UiText.DateCulture` gives the culture for day and month names: Windows' regional culture while it belongs
  to the interface language (`en-GB` keeps its 24-hour clock), else the interface language's own (`UiLanguage.Culture`);
  the invariant culture stays English for English, so tests that pin it keep their output. The two date patterns are
  catalog entries (`M月d日`, `yyyy年M月d日`); a pattern .NET refuses falls back to the English one.
- **WinUI's own text:** `AppController.ApplyLanguageToWindowsText` sets the process's preferred UI languages
  (`Windows/Shell/ProcessLanguages`, `SetProcessPreferredUILanguages` with `UiLanguage.WindowsLanguageNames`: `zh-CN`
  first, because WinUI ships `zh-CN\Microsoft.ui.xaml.dll.mui`, then `zh-Hans`) and the Windows App SDK's
  `PrimaryLanguageOverride`, when the settings are read and after the first-start choice, before any window is built.
  Logged once: "Interface language: zh-Hans (WinUI's own text: zh-CN, zh-Hans)."
- **Guards** (CLAUDE.md §4): `TranslationCoverageTests` (Core, 14) scan `src` with a small C# lexer
  (`CSharpSourceScanner`): every literal and constant handed to `UiText.Translate`/`Format` has an entry, every entry
  is still a literal in `src` or a key in XAML; Core's template lists have entries; group icon names stay unique in
  every language; the date patterns parse; and the Chinese output of the new pieces. `TranslationTemplateTests`
  (Windows, 6) check the Windows lists and produce every shortcut problem, registration state and run result. Mutation
  check: a planted untranslated literal, constant and template, and a renamed literal, made both guards fail.
- **English on purpose** (`docs/localization.md`): product and tab names (Everything, ShareX, PowerShell, Pwsh, Cmd,
  Claude, Codex, Win+R, Win+PrtScn), key names and menu accelerators (`Ctrl+P`, `Enter`), the search toggles' `Aa`, `W`
  and `.*`, text from outside the app (Windows' and .NET's error messages such as a regex's "Not enough )'s.", the
  network stack's reasons, the installer's report), and stored content.
- **Also fixed:**
  - Everything's status read "Not installed (not installed)." in English and Chinese; it says "Not installed." now.
  - The prompt tabs' "60+ matching of …" passed its "+" as a value, which the Chinese template left out (it claimed
    exactly 60): two whole sentences now (Chinese "至少匹配 60 条").
  - Two features shared the word 忽略: "Forget forever" (永久忽略) and "Ignored apps" (忽略的应用). "Forget forever" is
    永久忘记 now, Windows' own Chinese verb for "Forget" (忘记此网络), in the catalog's 17 strings of the feature and
    in the Chinese README; 忽略 keeps meaning "ignore".
  - `catalog.py`'s XAML scans read only files under `src` (a published app's `Microsoft.UI.Xaml` folder under
    `artifacts/` broke them).
  - Documentation gaps the docs hook never saw (it skips the scratchpad, where the branch's worktree lives) were filled
    in every file the branch touches.

**Merging with the uncommitted AI work** (§2.35 in the shared tree, not committed): the local merge carried it
forward. Five of its files were also changed by the PR; they were backed up, set to `HEAD`, merged, and written back as
a three-way merge (4 clean; in `ClipboardFlyout.xaml` the AI editor row kept, the PR's keys moved onto the search box
and its toggles). Its strings are English only until they get catalog entries; when it lands, the guards above fail
until they do.

**Verified:** see §5.

**Not built / open:**
- ~~The owner's open choices from the review~~: answered the same day and built on `feat/localization-choices` (below);
  WinUI's own control text follows the choice since the gap work.
- Right-to-left layout for a future Hebrew or Arabic catalog: decided against (below).
- .NET's regex parse errors ("Not enough )'s.") stay English; `RegexParseException.Error` would allow our own sentences.

**The second round of choices (2026-11-08, later), built on the branch `feat/localization-choices`.** The owner asked:
"Use ask html questions tool for the choices. And what else we need to complete and finish supporting any language
(not just two hardcoded, we will add Spanish, Russian, Hebrew later on." Two rounds of HTML questions (pages in session
7f64d66b's scratchpad, `choices/`) gave:
- **Plurals: one key per form.** `UiText.Plural(n, $"{n:N0} item", $"{n:N0} items")`; each catalog gives the forms its
  language needs under `{English other form}#{category}`. Every catalog's header carries its CLDR rules in CLDR's own
  syntax, samples included (`"plurals": { "one": "i = 1 and v = 0 @integer 1", … }`), so a new language needs no code.
  `Core/Localization/PluralRules` parses and evaluates them for whole counts (all CLDR operators; `n`/`i` = the count,
  decimal operands 0); the tests run every `@integer` sample through its rule. 35 English "s"-suffix plurals and
  1-vs-N ternaries became `UiText.Plural` (footers, captions, Settings status lines, the forgotten list, the group
  tooltip, the update dialog); a missing form, or one whose placeholders do not fit, falls back to English. Two counts in
  one sentence are two plurals (the prompts card's sends are worded first, then placed).
- **Right to left: "Hebrew words, LTR layout".** Nothing to build: a Hebrew catalog will show its words in the current
  layout.
- **Translators: "Claude + native review"**, with the owner's comment "Only for English, Simplified Chinese which was
  contributed / You review and you approve, we wont do the other languages right now." So no Spanish, Russian or Hebrew
  now, and Claude reviews the contributed Chinese. `catalog.py rename` marks every moved translation for review
  (`"$review"` in the catalog, cleared by `catalog.py reviewed`), so nothing goes stale silently.
- **Keys: "English text as key"**, with the owner's comment "And if we need a hash key then allow hash key too". All 563
  hash keys became the English text (`catalog.py migrate`, in the catalogs and in 188 XAML uses); `en.json` maps each
  text to itself and is the list of every UI string; XAML reads `{loc:Tr Text='Paste'}` again. Hash keys stay allowed
  (`{loc:Tr Key=T…}`, `catalog.py add --hash`) for a text XAML cannot quote or a very long one; C#'s `Translate` still
  finds a hash-keyed text by its English. **WinUI's XAML compiler takes no backslash escapes in a markup extension's
  quoted value** (`Text='Windows\' list'` failed with WMC0615 "Type 'loc' used after '{' must be a Markup Extension");
  `Text=&quot;Windows' list&quot;` compiles and showed right on screen, so the 12 texts with an apostrophe use it.
- **Switching: a "Restart now" button.** Settings › App › Language saves the choice at once and shows *Restart now*
  (accent, left of the list) while the saved language resolves to another one than the one on screen
  (`SettingsViewModel.IsLanguageRestartPending`). `AppController.RestartAsync` starts `BetterClipboard.exe
  --restart-after pid:startTicks` through ShellExecute (no inherited handles; same environment, so an isolated instance
  comes back as itself) and exits the usual way; the new process waits up to 30 s for that exact process (id **and**
  start time: an id alone can be reused) before it takes the single-instance lock, then opens Settings in the new
  language. A failed start keeps the old process running and logs why. `UiText.Language` is still set only at startup
  and by the first-start picker: a language never changes under windows that are up.
- **WinUI's own text and dates: "Follow the app".** Not built on this branch: the other session that works on the
  translation gaps (below) has `UiText.DateCulture` and `UiLanguage.WindowsLanguageNames` (the resource loader's names
  for WinUI's own control text) in its uncommitted work.
- **Partial languages: "Partial, English fallback"**, with the owner's comment "But dont show any percentages". A
  catalog may lack strings (English shows); `UiText.Coverage` counts what it lacks (a plural string counts once and is
  lacking until every form its rules need is there); a language lacking more than one string in ten
  (`UiText.MaximumMissingShare`) is not offered (`UiText.Languages`; `UiText.AllShipped` lists all), and the log says
  how many strings it lacks. No percentage anywhere in the UI; the tests print counts.

**Also on the branch:** `UiText.UsesSimplifiedChinese` is gone (the one Chinese-only helper; the tests use `Resolve`);
`tools/localization/smoke-windows.ps1` reads its languages and each one's word for "Shortcut" from the catalogs (with a
pattern: two English texts that differ only in letter case would be duplicate keys to PowerShell's JSON reader);
`catalog.py` gained `add --hash`, `add-plural`, `remove`, `missing`, `reviewed` and `migrate`, and `check` now reports
broken entries only (missing strings are counted).

**The code and the catalogs are checked against each other** (`LocalizationTests.CodeAndCatalogsAgree`, with
`UiTextSourceScan`, a small C# lexer: comments, char literals, regular, verbatim and interpolated strings with nested
holes): every XAML `Text=` and `Key=`, every literal passed to `UiText.Translate` / `Format`, and every `Plural` pair
must be in `en.json` as written (interpolated strings compared as the composite format the runtime looks up), and every
`en.json` string must still appear somewhere in the sources. Its first run found two old bugs: the regular-expression
timeout message was never translated (the code formats `{0:0}`, the catalog had `{0}`), and "{0:0.0} MB" (the update
download) was not in the catalogs; and five strings nothing used any more ("Copy", "English", "OK", "Save", the summon
log fragment). A planted file with a new `Translate`, `Plural` and `Format` literal made it fail on all three.

**Two sessions, one result.** While this round was asked, another session (c18a1e65) took `feat/localization` into its
own worktree, merged `main`'s `3f0ff87` into it (`be6d30d`), and closed the translation gaps there (`bde5c83`, the
section above). This round was built on its own branch, `feat/localization-choices` (`0720940`, made from `be6d30d`).
On the owner's "Pull main and merge to main your changes" (2026-11-08) the two were joined, and then landed on `main`:
- **`bde5c83` into `feat/localization-choices`.** The catalogs: the gap work's (737 strings, hash keys), re-keyed by
  `catalog.py migrate`, with this round's plural rules, plural strings and "Restart now" on top; the gap work's
  whole-phrase counts ("1 file" / "{0:N0} files", "kept out 1 time, last {0}") and its two "60+ matching" sentences
  became plural strings (the "+" one keeps its Chinese 至少匹配), and "Forget forever"'s new 永久忘记 carried into the
  forgotten list's plural string. `UiText`: this round's file with the gap work's purely additive members
  (`TranslateFormatted`, `DateCulture`, the template shapes). `UiLanguage`: both (plural rules, culture, the Windows
  language names). `catalog.py`: this round's, with the gap work's `add-file`, its XAML-scan fix (files under `src`
  only) and its documentation. The gap work's `TranslationCoverageTests` and `TranslationTemplateTests` learned
  English-text keys (an entry is the text itself, or its hash key; XAML's `Text=` counts as a use); both source scans
  stay (`CSharpSourceScanner` follows constants, `UiTextSourceScan` checks plural strings and XAML). The plural forms
  passed to `UiText.Plural` no longer count as uses of a plain entry with the same text.
- **Then into `main`,** after a pull of GitHub's merge of PR #1 (`a347f06`, a tree equal to the PR head already in
  `main`). `main`'s own work since `3f0ff87` met it there: the tabs row's menu (§2.40) and the local dev builds (§2.39)
  had added 19 strings under hash keys, now English-text keys with their Chinese; the dev builds' 11 failure sentences
  (`LocalBuildFeed`, `UpdateService`), written as plain English, are `UpdateMessages` templates now, with Chinese, so
  the dialog shows them translated like every other update failure; `UpdateText` keeps both `main`'s `OfferAction` /
  `ApproveButton` and the gap work's `ProblemText`. The gap work's `CSharpSourceScanner.StripComments` skipped strings
  without knowing interpolation holes, so a hole holding a string of its own (`main`'s new
  `$"… {(local ? "a local build" : …)} …"`) made it read the rest of `AppController.Updates.cs` inside out and miss its
  literals; it skips strings with the hole-aware reader now. The shared working tree's uncommitted AI work (§2.35) was
  kept, its `{loc:Tr Key=T…}` lines rewritten to the English-text keys so its labels still resolve. Its own texts do
  not go through the catalogs yet (plain English in `ClipboardFlyout.AI.cs` and its XAML), so the guards pass with it
  (built with this merge in a scratch checkout: 0 errors, Core 1,493 and Windows 380 passed) and its panel shows English
  in Simplified Chinese until those texts go through `UiText.Translate` / `{loc:Tr Text=…}` with catalog entries
  (`catalog.py add`, [`docs/localization.md`](docs/localization.md)).

**Verified** (rows in §5): on the branch, Core 1,363 (57 new: `PluralRulesTests` 50, with CLDR samples of English,
Russian, Hebrew, Spanish and Arabic; `LocalizationTests` 7 new) and Windows 371, all green, and the app on a disposable
Windows 11 desktop (deleted afterwards); after joining the gap work, Core 1,377 and Windows 377; on `main` with its
own work since `3f0ff87`, Core 1,446 and Windows 380 (2 skipped, 1 explicit), all green.

**Russian, Spanish and Hebrew, a description on every row, right-to-left text (2026-11-09).** User request: "Learn the
localization system, understand each translation row by reading the code / And start producing a description in
English per translation row / And a translation to new languages - Russian, Spanish, Hebrew (UI stays RTL)". Two HTML
questions settled what the request left open: Hebrew's layout, **"Keep left-to-right"**; where descriptions go,
**"A // comment at the end of the json translation line"** (the owner's own answer, over a `$descriptions` object and a
separate file).
- **Descriptions:** every `en.json` row (790, plural forms included) ends with `// …`, written from the code that shows
  the string: where it shows, what each `{0}` holds, how much room there is, which words to keep, and which sentence a
  fragment belongs to. A plural string's `#one` row carries its description plus a fixed sentence, its `#other` row a
  fixed note. The loader skips comments (`UiText.CatalogParseOptions`, `JsonCommentHandling.Skip`: trailing commas stay
  an error). `catalog.py` parses and writes them in its canonical layout (the old catalogs round-tripped byte for byte
  before the first write), and gained `describe`, `--description`/`-d` on `add` and `add-plural`, `$description` in
  `add-file`, descriptions in `missing`, and `check` failing on a row without one.
  `LocalizationTests.EnglishCatalog_EveryRowHasItsDescription` reads `en.json` with comments as tokens and fails on a
  row without a same-line `//` description, or a comment anywhere else (the tool's layout would drop it).
- **Three catalogs:** `ru.json`, `es.json`, `he.json`, complete (CLDR plural rules in each header: Russian one/few/many/
  other, Spanish one/many/other, Hebrew one/two/other), translated by Claude from the code and the descriptions, no
  native speaker's review yet. Russian templates that would have to inflect an inserted translation use a colon or the
  nominative ("в группах: 4"). Flags drawn by `tools/make_flags.py`: Russia, Spain (the civil flag, without the coat of
  arms, a blur at 36 × 24 DIPs), Israel (stripes and star from the official 160-unit height, the star as two even-odd
  triangle bands). Windows' `he-IL`, `ru-RU`, `es-MX`, `es-419` reach them through their parent cultures.
- **Right-to-left text in left-to-right windows.** A first pass put a right-to-left mark after each Hebrew sentence's
  final punctuation (357 rows). Insufficient by UAX #9: in a left-to-right paragraph a Hebrew sentence with an English
  word in the middle shows its Hebrew stretches in the wrong order around it, and one that starts with a number reads
  backwards ("5 פריטים" as "items 5"). Built instead (`UiLanguage.IsRightToLeft`): the header's `"direction": "rtl"`
  (anything but `ltr`/`rtl` leaves the catalog out), and at load every translation holding a strong right-to-left
  character (`UiText.HoldsRightToLeftText`: Hebrew, Arabic and the other right-to-left blocks, beyond the BMP too, or
  U+200F / U+061C) is wrapped in U+202B … U+202C. Values without one stay plain (`", "`, the date patterns,
  `"PowerShell ({0})"`, `"{0}: {1}"`); `"{0} ({1})"` opts in with a leading U+200F. The marks of the first pass were
  removed (360). Code that joins translated pieces into one line goes through `UiText.JoinLine`: the card captions
  (`ClipItemViewModel`, `.Prompts`, `.Shells`), the forgotten list's details (`ForgottenText`), the third-party credit,
  and two sentence pairs in `UpdateText`. `UpdateText.AsSentence` looks past trailing format characters and puts its
  period inside the embedding. `ClipStore.ValidGroupName` drops explicit direction controls (U+202A–U+202E,
  U+2066–U+2069), because a group created in Hebrew gets its icon's embedded name. `catalog.py check` refuses those
  controls in any catalog.
- **A Latin word's trailing signs, and the Chinese full stop** (found in the release QA of 2026-11-09, fixed in
  `9c7ba87`). Hebrew showed "Notepad++" as "++Notepad" wherever the name ended a stretch of right-to-left text:
  by UAX #9 a run of neutral signs between a left-to-right word and right-to-left text, or the end of an embedding,
  takes the embedding's direction. It hit 26 places in 21 Hebrew rows (Settings' "Files opened in Notepad++"), the
  captions `JoinLine` builds, and values put into Hebrew templates. `UiText.KeepTrailingSignsWithTheirWord` puts
  U+200E after a run of `+` or `#` that ends a Latin word, in every text that holds a right-to-left embedding
  (`EmbedRightToLeft`, and the `Finish` step of `Format`, `Plural` and `TranslateFormatted`). Signs inside a word
  (`Win+V`, `C++17`) and after a digit (`60+`) are left alone, and the other languages come back as the same string.
  `UpdateText.AsSentence` ends a title that closes in a Han or kana character with 。 (Settings read "是最新发布版.").
  Left as the translator wrote it: Chinese sentences joined with a space after 。 (`UpdateText`'s two joins, and
  catalog templates such as `"{0} 此副本…"`).
- **Found on the way, fixed:** two code spots showed English in every language: Settings › Command line's PATH line
  (`" · on your PATH (new terminals run it as bclip)"` concatenated in code; now `"{0} · on your PATH …"` /
  `"{0} · not on your PATH"`), and an archived prompt card's label (`$"{agent} {"slash command"|"prompt"}"`; now
  `"{0} slash command"` / `"{0} prompt"`). Chinese `"{0} prompt"` was `"{0} 条提示词"`: 条 counts, and `{0}` is an
  agent's name, so it read "Claude Code 条提示词"; now `"{0} 提示词"`.
- **Room, measured:** with Segoe UI at 14 px (Pillow), the search box shows about 222 px of text before its toggles
  (calibrated on the Russian placeholder the test desktop showed cut: "Поиск по всему, что вы копировал"). Nine
  translations were shortened to fit (Russian 4, Spanish 3, Hebrew 2); English itself is cut in two of them (the
  Everything tab's 238 px, the prompt tabs' 262 px). The six placeholder descriptions now state the room.
- **Docs:** [`docs/localization.md`](docs/localization.md) (descriptions, right-to-left, the tool, known gaps), the
  README's language passages and their Chinese translation.

**Verified** (rows in §5): Core 1,566 and Windows 394 (2 skipped, 1 explicit) on HEAD plus only this change, in a
private worktree; in the shared tree with the uncommitted AI work, Core 1,613 and Windows 396 + 1 known timing test
(`ClientHangUp_CancelsHandler`, 3 of 3 alone). A disposable claude-desktops Windows 11 desktop showed the picker with
all five languages and flags, Settings and the panel in Hebrew, Russian and Spanish, and the fixes live.

**Settings' language box: flags in the closed box and on "Use Windows setting" (2026-11-09).** User request: "In settings
the language dropdown box, selected item should be saying "<EngFlag> English" and Use Windows settings should also have
a flag". One HTML question with three rendered mockups settled what it left open; the owner chose **"Plus a Windows
hint"**:
- **The open list:** "Use Windows setting" shows the flag of the language Windows' setting picks now (it had a globe),
  then its label and that language's name; every language as before (flag, own name, English name).
- **The closed box:** the language in effect with its flag: "🇺🇸 English · Windows setting" while BetterClipboard follows
  Windows (the hint dimmed; a new catalog string "Windows setting", in all five catalogs), "🇨🇳 简体中文" for a language
  picked.
- **How:** a WinUI ComboBox draws its closed box with its items' template. `ComboBox::SetContentPresenter`
  (microsoft-ui-xaml, `ComboBox_Partial.cpp`) copies the chosen container's ContentTemplate and selector onto the closed
  box's presenter at every selection change, so a second template set from outside would be overwritten. The one item
  template holds `Views/LanguageOptionView`, which looks where it sits once Loaded (a `ComboBoxItem` above it: a list
  row; the `ComboBox` first: the closed box) and draws itself for that place. `LanguageOptionViewModel` gives the Windows
  row its flag and both rows `SelectedName`, `SelectedNameLanguageTag` and `SelectedHint`; `HasFlag` is gone (always
  true now).
- **Two faults the test desktop showed, fixed:** (1) the hint "· לפי הגדרת Windows" drew its dot at the right end: a
  TextBlock takes its paragraph direction from its first strong letter, so the line became right to left; the detail
  text now has `TextReadingOrder="UseFlowDirection"`, and the dot stays out of the translation (inside the embedded
  Hebrew it would land at the run's right end). (2) In Hebrew with *Restart now* beside it, the closed box ran past the
  card's edge with its chevron: the card holds its content to 380 px and a horizontal StackPanel gives the box unlimited
  width. The card's content is a Grid now (the button `Auto`, the box `*`, `MinWidth` 200 instead of 240), and the view
  is a Grid whose detail column trims with an ellipsis.

**Verified** (rows in §5): the localization guards (116) and both suites; a disposable claude-desktops Windows 11
desktop with HEAD (`5956bda`) plus only this change: English picked, following Windows, 简体中文 picked with
*Restart now*, and the Hebrew interface following Windows with Hebrew's *Restart now*.

**The language question, asked until it is answered (2026-11-09).** User request: "Make sure that on first launch,
user gets a dialog for choosing language / Also should be true for users that just updated and never saw the dialog."
- **Before:** the picker asked only in a data folder without a history (`AppPaths.HasHistory`). Every user who updated
  kept a history and was never asked: from 0.2.9, which has no languages, and from the development builds since
  2026-11-08. Windows' language was then used without a word. No settings value could tell either, because `"system"`
  is both the default (never asked) and *Use Windows setting* chosen in Settings.
- **Now:** `AppSettings.LanguageChosen` (bool, false by default) records that the question was answered. The picker
  sets it whichever way it closes (each way saves a language), and so does any change in *Settings › App › Language*
  (`SettingsViewModel.OnLanguageIndexChanged`; `Update` is muted while the settings load, so loading never sets it).
  `LanguageChoice.ShouldAsk(languageChosen, interfaceLanguage)` asks while it is false and the saved language is blank
  or `system`. An explicit language is never asked over: an administrator's pre-seeded file, and every user who
  answered the earlier picker (it saved an explicit tag).
- **Who is asked once more:**
  - users of 0.2.9 and older, at the first start after the update. The update restarts the app with `--background`;
    the picker shows anyway, before the panel's warm-up and before Settings;
  - users of the development builds since 2026-11-08 who never saw it;
  - users who chose *Use Windows setting* in Settings of a build from before this change (it saved `system` without
    the flag: development builds only);
  - after an exit from the tray while the picker is open, which saves nothing. Before, the next start never asked
    again, because the history existed by then.
- **The log** is the only use left of `HasHistory`: "Interface language chosen at the first start: …" or "Interface
  language chosen at a start with a history already kept (an update from a version that did not ask, or an earlier
  start that closed without a choice): …".
- **Footgun:** a version that does not know `LanguageChosen` drops it when it saves `settings.json` (0.2.9 drops
  `InterfaceLanguage` too). A user who goes back to 0.2.9 and updates again is asked once more. An administrator who
  wants "follow Windows" without the picker pre-seeds `"InterfaceLanguage": "system"` with `"LanguageChosen": true`.
- **Pieces:** `AppSettings.LanguageChosen`; `LanguageChoice.ShouldAsk`; `AppController.Start` and
  `AskForLanguageAsync(showSettingsAfter, afterUpdate)`; `SettingsViewModel.OnLanguageIndexChanged`;
  `LocalizationTests.Start_AsksUntilALanguageIsChosen` (10 cases) and
  `SettingsFiles_FromBeforeTheLanguageAsk_UntilAnswered` (a 0.2.9-style file asks; answered and read back, it does
  not; `system` answered does not).

**Verified** (rows in §5): Core 1,648 of 1,648 on HEAD (`9324621`) plus only this change, in a private worktree, and
1,695 of 1,695 in the shared tree with the uncommitted AI work; a disposable claude-desktops Windows 11 desktop
updated from the released 0.2.9 to this change the way the update button does, and showed the picker.

### 2.38 Where the panel opens: next to the text cursor, next to the mouse, or centered on the main or the current screen (built 2026-11-08)

User request (2026-11-08): "Add setting that chooses where the BetterClipboard menu will appear 1. Near cursor (like now,
default like now) 2. center of main screen 3. center of current screen (supporting multiple monitors)". (§2.37 is left to
the localization branch, `feat/localization`.)
- **One question asked** (the term had two readings, see the memory note on asking): which monitor counts as current when
  the window the user types in and the mouse are on different monitors. The owner chose **"Where I'm typing"**, over
  "Where the mouse is" and "Both, as two choices".
- **What existed:** *Settings › Pasting › Panel position* already had "Next to the text cursor" (the default), "Next to
  the mouse" and "Center of the screen" (the monitor of the foreground window, else the mouse's). Neither CLAUDE.md nor
  the README's Settings list named it beyond "where the panel opens", and the owner did not find it. "Next to the mouse"
  stays: removing a choice someone may use would be a regression.

**What the user gets.**
- **Settings › Shortcut › Panel position**, moved there from *Pasting*, right under the shortcuts: four choices.
  - *Next to the text cursor*: the default, unchanged.
  - *Next to the mouse*: unchanged.
  - *Center of the main screen* (new): the middle of the main display's work area, whatever was in front.
  - *Center of the current screen*: the screen the user is typing on (the rule below).
- The card's description says what "current" means: "The current screen is the one you are typing on, or the one with
  the mouse when you are on the desktop."
- **A centered panel is centered as a whole, groups column included** (`FlyoutPositioner.Place`). The column opened or
  closed while the panel shows still grows or shrinks it on the left, so the list stays put; the next summon centers it
  again. Before, a centered panel centered the list and hung the column on its left.
- **Centered on the main screen, the warm-up puts the cloaked window exactly where every summon will** (`WarmUpBounds`):
  the first Win+V then needs no move and no resize ("Panel on screen 62 ms after the shortcut … (first summon)" on the
  test desktop).
- **Every placement keeps the remembered size across monitors with different scales** (the fix below).

**"The current screen", the owner's rule** (`FlyoutPositioner.ChooseScreen`, pure):
1. The caret's monitor, while the caret lies inside the window the user was in (its middle point, `CaretMiddle`). Why
   "inside": some apps park their caret in a hidden helper window at the screen's corner, which would pull the panel to
   the main monitor. A caret on the desktop (renaming an icon) counts, because the desktop's window spans every monitor.
2. Else the monitor showing most of that window (`MonitorFromWindow`), when the window marks a screen: visible, not
   minimized, and not the desktop (`Progman`, `WorkerW`: `MonitorLookup.IsDesktopWindowClass`). The desktop's window
   spans every monitor, and "the monitor showing most of it" was arbitrary: before, a click on the second monitor's
   desktop could put the panel on the first.
3. Else the mouse's monitor: after a click on the desktop, from the tray, with nothing active.
- The facts about the window come from `MonitorLookup.DescribeTarget` (`TargetWindowFacts`: bounds, marks a screen), read
  at the summon only for this placement. The choice is a `PanelScreen` (a point, a window, or the main monitor), looked up
  by `MonitorLookup.FromScreen`; the main monitor is `MonitorFromPoint((0, 0), MONITOR_DEFAULTTOPRIMARY)`, since its
  top-left corner is the screen origin by definition.
- Next to the text cursor and next to the mouse keep their old monitor rule exactly (the point below the caret's left
  end, else the mouse).

**Stored as before, plus one member** (`AppSettings`).
- `Placement` keeps its three names (`NearCaret`, `NearCursor`, `CenterScreen`); the new `CenterOnMainScreen` (bool, off by
  default) tells the two centered choices apart. `SettingsViewModel.PlacementIndex` maps the list's four entries onto
  the two members (`PlacementIndexOf`, `OnPlacementIndexChanged`; both are written in one update).
- **Why not a fourth enum member:** the placement is written by name (`JsonStringEnumConverter`), and a name an older
  version does not know makes it throw while reading and set the **whole** settings file aside as corrupt (§2.28's
  footgun). With a member of its own, an older version reads `CenterScreen` and centers on the current screen.
  `PanelPositionSettingsTests.Placement_KeepsItsThreeReleasedNames` fails if a member is ever added; the enum's remarks
  say why.
- Verified on the test desktop: with "Center of the main screen" chosen, 0.2.9 started with every setting, logged no
  "unreadable" warning, set nothing aside, and centered the panel its own way. 0.2.9 rewrites the file only when a setting
  changes, so the choice was still there after updating back. Footgun: once an older version saves the file, the member
  is dropped, and the next newer version centers on the current screen until the main screen is chosen again.

**Monitors with different scales: the panel came out 25 % too large or 20 % too small** (found 2026-11-08 on the
owner's PC: two monitors at 100 %, one at 125 %).
- **Windows' part** ([`probe_dpi_move.cs`](tools/probes/probe_dpi_move.cs), a cloaked tool window): a
  per-monitor-v2 window moved by `SetWindowPos` onto a monitor with another scale gets `WM_DPICHANGED` **inside** the
  move, suggesting the asked rectangle scaled once more by the ratio of the two scales. Asked 500 × 700 on the 125 %
  monitor, suggested 625 × 875; asked 400 × 560 back on 100 %, suggested 320 × 448. The top-left corner is kept.
- **WinUI's part** ([`dpi_move_lab`](tools/probes/dpi_move_lab), a WinUI 3 window set up like the panel, cloaked,
  never activated): it takes the suggestion. `AppWindow.MoveAndResize` to the 125 % monitor left 625 × 875 right after the
  call and 300 ms later; back on the main monitor, 320 × 448. Asking a second time held exactly, both ways.
- **So every summon that moved the panel onto a monitor with another scale opened it at the wrong size** **[inferred
  from the lab: the panel makes the same calls]**, in every placement since the panel existed: next to a caret on the
  125 % monitor after a summon on a 100 % one, and back. Too large, it also runs off the work area, since the corner is
  kept: the owner's remembered 729 × 754 DIPs would become about 1,140 × 1,180 pixels on a monitor whose work area is
  1,020 pixels high (computed, not seen). Nobody reported it; the right monitor may rarely be used for Win+V.
- **The fix** (`ClipboardFlyout.MoveAndResizeExactly`, ClipboardFlyout.Placement.cs): move and resize, compare the bounds,
  ask once more when they differ. On the second call the window is already on its monitor, so nothing rescales it. Used
  by `PresentAsync` (both of its moves) and the warm-up. The first correction of a session is logged, content-free:
  "The panel came out 625x875 instead of 500x700 (moved to a monitor with another scale?); its bounds were set again:
  500x700." Bounds that still differ come from a limit (the minimum size on a smaller work area), which a third call
  would not change.
- The size the user remembers is never touched: `WindowSizeHook.UserResized` fires only for a resize the user made with
  a border, never for these programmatic moves (§2.23).

**Pieces.**
- Core: `AppSettings.CenterOnMainScreen`, `FlyoutPlacement` remarks (never add a member).
- Windows: `Input/FlyoutPositioner.ChooseScreen` and `Place`; `PanelScreen`, `PanelScreenKind`, `TargetWindowFacts`;
  `MonitorLookup.FromScreen`, `Main`, `DescribeTarget`, `IsDesktopWindowClass`; `NativeMethods.IsWindowVisible`,
  `GetWindowRect`, `GetClassName`.
- App: `Views/ClipboardFlyout.Placement.cs` (new: `SummonBounds`, `MoveAndResizeExactly`); `ShowAt(context, placement,
  centerOnMainScreen)`; `WarmUpBounds`; `PresentAsync`; `AppController.ShowFlyout`; `SettingsViewModel.PlacementIndex`;
  the card in `SettingsWindow.xaml`.
- Strings: three new catalog keys in English and Simplified Chinese ("Center of the main screen" `Ted615dd586a6` 主屏幕中央,
  "Center of the current screen" `T4b75609443c3` 当前屏幕中央, the new description `T88640766017a`); the unused "Center of
  the screen" `T13e9b2fec983` and "Where the clipboard panel opens." `T6173999ce066` are gone. The keys follow PR #1's
  scheme ("T" + the first 12 hex digits of the English text's SHA-256).

**Verified** (rows in §5): 35 new tests (Core 8, Windows 27), three mutations caught; the whole suite 1,636 (1,633
passed, 3 opt-in skipped); a Windows 11 test desktop (one monitor, 1600 × 900) for every choice, the column, the warm-up,
a restart, the way to 0.2.9 and back, and the Chinese card; the lab and the probe on the owner's three real monitors,
invisibly.

**Limits / not built:**
- **Not run on several monitors in the app itself:** the test desktops have one monitor, and a summon on the owner's PC
  would have put a panel over their work (they were busy in Chrome). The rule is unit-tested on the owner's layout
  (negative coordinates of the left monitor, the 125 % right one), the real lookups are tested on this PC's three
  monitors, and the scale fix with the same calls in the lab.
- A remembered position ("where I last dragged it"), and a choice of a specific monitor, were not asked for.
- A centered panel whose groups column is toggled while it shows is off-center by the column until the next summon.
- The tray's *Open clipboard* and `--show-flyout` from a script use the mouse or the foreground window like any summon:
  centered on the current screen, the tray's panel opens on the mouse's monitor.

### 2.39 Local dev builds: the dev-builds folder, `publish-dev.ps1`, and the dev rule (built 2026-11-08)

User request (2026-11-08): "We made many changes and now i want my local app to update to a local version through the
update button. Any time we are in a dev install, latest version from github is higher than the dev we have - unless we
have a newer dev then it suggests the newer dev. Have for local updates a dedicated user folder that we can publish to and
then our local can update easily. Also make a local skill 'local-dev' (commit it too)".
- **Two questions asked** (memory: ask when a term flips behavior). The second sentence reads two ways that differ in the
  most common case, a dev build ahead of the last release with no newer local build: the owner chose **"Always, unless a
  newer dev"** (a dev install is always offered GitHub's latest release, also when it is older) over "Only when it's
  newer". And **"Yes, install it now"** for the first dev build on this PC (their 0.2.9 cannot read the folder).
- **The skill:** [`.claude/skills/local-dev/SKILL.md`](.claude/skills/local-dev/SKILL.md), a project skill in the repo.

**The rule** (`UpdateCatalog.FindUpdate(current, releases, localBuilds, architecture)`, pure, tested):
1. A **local build** newer than the running version is offered, whatever GitHub has.
2. Otherwise **GitHub's newest complete stable release**: for a **development build** (`UpdateCatalog.IsDevBuild`: the
   first pre-release identifier is `dev`, any case) **always**, also when it is older, as the way back to the official
   version; for any other build only when it is newer (the rule since §2.29).

| Installed | GitHub | Local folder | Offered |
|---|---|---|---|
| 0.2.10-dev.12 | 0.2.9 | none newer | 0.2.9 (the way back) |
| 0.2.10-dev.12 | 0.2.9 | 0.2.10-dev.15 | 0.2.10-dev.15 |
| 0.2.10-dev.12 | 0.2.10 | none newer | 0.2.10 |
| 0.2.10-dev.12 | 0.2.10 | 0.2.10-dev.15 | 0.2.10-dev.15 |
| 0.2.10-dev.12 | 0.2.10 | 0.2.11-dev.3 | 0.2.11-dev.3 |

- **Consequences, all deliberate:** a dev install's update button is lit whenever GitHub's list is known (*Skip this
  version* quiets it for that release); dev builds never get the weekly reminder (`UpdateReminder.IsWanted`, §2.30); a
  **release** install on a PC with a dev-builds folder is offered newer dev builds too, so the cycle goes on after an
  official release was installed through the button — **for releases that have this code, 0.2.10 on.** 0.2.9 predates
  the folder: the way back to it leaves dev builds until `publish-dev.ps1 -Install` runs again (it happened, see the
  limits below).
- `FindUpdate(current, releases, architecture)` (GitHub alone) applies the same dev rule. The one existing test that
  expected nothing for a dev build ahead of every release (`FindUpdate_OffersNothingWhenUpToDate`, case
  `0.2.8-dev.abc`) now expects the latest release (`FindUpdate_ADevelopmentBuildIsAlwaysOfferedTheLatestRelease`).

**What the user sees** (`UpdateText`; 14 new strings in both catalogs, `en.json` and `zh-Hans.json`):
- **A local build:** tooltip "Dev build available: BetterClipboard 0.2.10-dev.15.g77faa1f"; dialog title "BetterClipboard
  … is available"; detail "You have 0.2.10-dev.14.g3e48a10. Published 10 min ago. It is a development build from the
  dev-builds folder on this PC: updating copies it from there, checks it, and restarts BetterClipboard. Your history is
  kept."; *Update and restart*, *Later*, *Skip this version*; "What is new" = the notes of every local build newer than
  the running one, newest first (`UpdateCatalog.SelectChangelog(current, releases, localBuilds, offer)`); no "View on
  GitHub" (`UpdateView`: a local build has no page). While it is copied: "Copying BetterClipboard …: 25 %",
  "Checking the copy…", "Comparing the copy with the build's SHA-256 checksum."
- **The way back:** tooltip "Back to the release: BetterClipboard 0.2.9"; title "BetterClipboard 0.2.9 is the latest
  release"; detail "You have 0.2.10-dev…, a development build that is newer than this release. Installing it replaces the
  development build with the official release from GitHub and restarts BetterClipboard. Your history is kept.";
  *Install and restart* (`UpdateText.ApproveButton`); Settings' button and the tray menu: "Install 0.2.9…"
  (`UpdateText.OfferAction`, which also fixes Settings' untranslated "Release notes…" fallback).
- `UpdateSnapshot`: `LocalBuildCount`, `IsDevBuild`, `IsLocalOffer`, `OffersOlderVersion`. Log lines name the kind:
  "Update check: the latest release, 0.2.9, is offered as the way back from this development build (running …)";
  "Updates: … a development build (GitHub's latest release is always offered as the way back)."

**The folder** (`AppPaths.DevBuildsDirectory` = `<data directory>\dev-builds`; `Core/Updates/LocalBuildFeed`):

```text
%LOCALAPPDATA%\BetterClipboard\dev-builds\
  0.2.10-dev.15.g77faa1f\
    BetterClipboard-0.2.10-dev.15.g77faa1f-win-x64.zip   the release-shaped zip (package.ps1)
    SHA256SUMS.txt                                        sha256sum format; must list the zip
    install.ps1                                           the build's own installer (for -Install)
    notes.md                                              the commits since the previous build here
    build.json                                            {"version", "commit", "subject", "publishedUtc"}; written last
```

- **Complete builds only:** the folder's name equals `build.json`'s version, the zip for this PC's architecture exists
  and is not empty, and a `SHA256SUMS.txt` lists it. Dot-folders (`.publishing-…`) and anything else are ignored; every
  failure is per build and swallowed (`Read` never throws). Caps: 64 folders, `build.json` 64 KB, notes 256 KB (cut to
  GitHub's 200,000 characters), checksum list 64 KB.
- **Who reads it:** only a copy the installer installed (`UpdateServiceOptions.LocalBuildsDirectory`, set by
  `AppController.StartUpdates` for `UpdateInstallKind.Installer`). Chocolatey, all-users and hand-extracted copies never:
  they could only point at a download page, and a local build has none. An isolated instance reads its own data
  directory's folder. Nothing creates the folder but the publish script, so on other PCs it costs one look at a missing
  folder and one name-filtered watch on the data folder.
- **Trust: integrity, not authenticity.** The folder belongs to the user, like the install folder, which the same user
  can replace at will (§2.6.1's threat model). Checked: a file to read lies inside the folder (`IsInside`, full paths, so
  `..` cannot step out), the package's size is still the offered one, and the copy's SHA-256 is the one the build's own
  list states. A local build's asset has no second witness (`Sha256` is `null`; GitHub's digest exists only for releases).

**When the app looks** (`UpdateService.RefreshLocalBuilds`, under a lock of its own; cheap, blocking, never on the UI
thread): at `Start`; at every check (so *Check now* looks too) and every hourly tick; when the dialog opens
(`EnsureFreshAsync`, on the pool, also with automatic checks off: the folder is not the network); and when
`Windows/Updates/LocalBuildsWatcher` reports. Not while an approved update installs. A change raises `Changed` once and
logs "Local builds: N in the dev-builds folder; BetterClipboard X is offered (running Y)." (or "…, none newer than the
running Y."); an unchanged folder logs nothing.
- **The watcher:** the folder recursively while it exists, plus the data directory non-recursively for the folder's own
  name only (`NotifyFilters.DirectoryName`), so its creation by the first publish and its removal are seen; a report once
  the folder was quiet 500 ms (a publish raises dozens of events); a failed watch is set up again and reported.
  Measured on the VM: the log line 0.5 s after the build folder was renamed into place.

**Installing a local build** (`UpdateService.RunInstallAsync`, local branch): the build's `SHA256SUMS.txt` is read from
disk (`LocalBuildFeed.ReadChecksumsAsync`), the zip is copied into `updates\` (`CopyPackageAsync`: `.part` first, SHA-256
while copying, progress ten times a second, refused when outside the folder, resized or gone), verified, and handed to the
same `ScriptUpdateInstaller` with the same contract command line (§2.29): the build's own `install.ps1 -Update`. No
request leaves the PC. Messages say "the build's checksum list" and "Publish the build again" where GitHub's say
"the release's".

**Publishing** ([`tools/release/publish-dev.ps1`](tools/release/publish-dev.ps1); pwsh 7 or Windows PowerShell 5.1):
1. **Version** `<next release>-dev.<commits since the last v* tag>.g<short sha>`. The next release is
   `Directory.Build.props`' `<Version>` at that commit, or its next patch when `v<that>` is tagged already (released).
   The count makes later commits sort higher (SemVer compares numeric identifiers as numbers, of any length); the `g`
   keeps the hash from being a number (a numeric identifier with a leading zero is not SemVer, and the app would
   normalize it away from the zip's name). A release outranks every dev build of its own number.
2. **Builds the commit, never the working tree:** a temporary `git worktree` in `%TEMP%`, then that commit's
   `package.ps1` with the new `-SkipArchives` switch (no `.7z`, no 7-Zip; the release workflow never passes it; an older
   commit's `package.ps1` without the switch builds the archives too, which are not published). Uncommitted work —
   yours or another session's — is never in a published build; the script warns when the tree has changes.
3. **Publishes** into `.publishing-<id>`, then renames it to `<version>`: the app never sees half a build. `notes.md`
   lists the commits since the newest published build whose commit is an ancestor (else since the last release tag),
   decoded as UTF-8 (`[Console]::OutputEncoding` is set around every git call: commit subjects hold dashes and arrows).
4. **Prunes:** the newest `-Keep` (5) builds stay; builds that an official release supersedes go; `.publishing-…`
   leftovers older than an hour go.
5. **Reports** what the installed copy (`installer.json`) will do: offer it, has it already, or predates the folder.
6. **`-Install`** (the bootstrap): the build's own `install.ps1 -Package <zip> -NoLaunch` in Windows PowerShell 5.1
   without `PSModulePath`, then BetterClipboard started with `--background` through Explorer and a temporary shortcut
   (the user's environment; an isolated instance is started directly, Explorer would drop its data-folder variable).
- Measured on this PC (x64, warm NuGet cache): 63 to 96 s per build, most of it `dotnet publish` in Release.

**Verified:** see §5.

**Limits / not built:**
- `-Install` cannot run in a test desktop (it needs the repository and the .NET SDK): it ran here, as the bootstrap.
  Its two halves ran in the VM: `install.ps1 -Package` of a dev build, and the update path.
- The way back installs an older version over a newer one. The store is safe (`ClipStore.SchemaVersion` has been 1
  since the start; every schema change is additive), but an older version drops settings members it does not know when it
  saves (the documented footguns, e.g. `SkipAllUsersStartup`, `CenterOnMainScreen`, `HotkeyHistory`).
- **The way back to 0.2.9 leaves dev builds** (found 2026-11-08; the owner: "I think i accidentally updated to real
  release version and now i dont get suggested to upgrade to a dev version").
  - **What happened:** the owner had installed three dev builds through the lit button, each approved 0.2–2.3 s after
    its offer. At 17:48:56 the same lit button offered the way back ("the latest release, 0.2.9, is offered as the way
    back"), and it was approved 0.6 s later. 0.2.9 cannot read the folder, so nothing offered dev builds after that.
  - **Asked** whether to guard the way back, with three mockups: a button lit only for something newer, with the way
    back as a plain button in the dialog; a confirmation; or no way back. The owner chose none ("0.29.0 doesnt have dev
    intergration, just install latest dev instead for me please").
  - **Cure:** `publish-dev.ps1 -Install -Ref <the newest build's commit>`, which installs a build that is published
    already without building it (§5).
  - **Temporary:** once 0.2.10 is released, the way back lands on a release that reads the folder, and the next dev
    build published after it is offered again.
- Builds are for this PC only: no shared folder for other PCs, no signing, no Settings line about the folder.
- `notes.md` lists commit subjects; a build of an unrelated branch starts its notes at the last release tag.

### 2.40 Arranging the tabs: right-click › Move left, Move right, Hide, Show all hidden (built 2026-11-08)

User request (2026-11-08): "Now lets work on the tabs row, i want you to be able to right click and: Move left, Move
right, Hide, Show all hidden. And the order to be persisted."
- **Since 2026-11-09** *Hide* reads *Hide tab*, and *Hide tab & clipboard rows* follows it; *Show all hidden* brings the
  rows back too (§2.42).
- **Two questions asked** (HTML pages with the rendered menu, memory "HTML questions for choices"). The owner chose:
  - **"Hide the tab only"** over "also switch its reading off". Hiding takes a tab out of the row and nothing else: the
    Settings switches of the other apps' tabs stay on, Command Prompt windows are still read, prompts still archived,
    and nothing is ever deleted. Switching Cmd off would have deleted its kept commands.
  - **"All always stays"**, with the comment **"And cant be moved"**. So "All" is never hidden, never moved, and no tab
    moves before it.

**What the user gets.**
- **Right-click a tab** (or a touch press-and-hold; or `Menu` / `Shift+F10` while a tab has the keyboard focus, which it
  has after a click on it): *Move left* (Back `E72B`), *Move right* (Forward `E72A`), a separator, *Hide* (Hide `ED1A`),
  *Show all hidden (N)* (View `E890`). All four glyphs checked by rendering.
- **Right-click the row's empty space:** *Show all hidden (N)* alone.
- **Grayed out, never left out:** the moves at either end and on "All", *Hide* on "All", and *Show all hidden* while
  nothing is hidden (then without a count).
- **A move** goes one place among the tabs that show. Tabs not shown right now (hidden, or another app's tab that is
  not there) are passed over and keep their places, so one that comes back stands where it stood. A tab moved beyond the
  strip's visible part is scrolled into view.
- **Hiding the tab you are in** switches the panel to "All", which then also becomes the tab the next Win+V opens in.
- **Saved** at once in `settings.json`: `TabOrder`, the whole order after a move, written empty again once it equals
  the default; and `HiddenTabs`. Both survive restarts. The Left and Right keys follow the order and skip hidden tabs.

**Pieces.**
- **Core `Presentation/TabArrangement`** (pure, unit-tested):
  - `Arrange(defaults, stored)`: the stored order completed (a tab it lacks goes right after its default predecessor),
    names read leniently, "All" first;
  - `CanMove` and `Move`: before the nearest shown tab on the left, after the nearest shown tab on the right, never past
    "All";
  - `CanHide`; `ToStored` (empty for the default order, so a later version's default reaches users who never moved a tab).
- **Core `AppSettings`:** `TabOrder` and `HiddenTabs` (filter names); `TryParseTab` (names only, unlike `ParseTab` it
  tells an unknown name apart); `Normalize` keeps known names once and never lets "All" be hidden.
- **App `Views/ClipboardFlyout.TabArrangement.cs`** (new): the default order is read from the XAML once, before
  anything moves (`defaultTabOrder`), so the XAML stays the one place it is written; the menu (`ShowTabMenu`),
  `MoveTab`, `HideTab`, `ShowAllHiddenTabs`, `SaveTabArrangement`, and `ApplyTabOrder`, which moves the SelectorBar's own
  items (it has no other way to order them).
- **Visibility:** a tab shows while its own conditions hold and it is not hidden. `SetTabVisible`, `UpdateShareXTab`,
  `UpdateRunTab` and `UpdateEverythingTab` check `IsTabHidden`, so does `IsTabAvailable` (a hidden remembered tab opens the
  panel in "All"), and the new `UpdateBuiltInTabs` does it for Pinned, Text, Images, Links and Files.
- **Strings:** five new catalog entries in `en.json` and `zh-Hans.json` (向左移动, 向右移动, 隐藏, 显示所有隐藏的标签页,
  显示所有隐藏的标签页（{0}）).

**Moving the bar's items, and the selection** **[verified on the test desktop]**. `ApplyTabOrder` takes an item out of
`Filters.Items` and puts it back in its place; the item is the same element with its handlers (the per-tab
`PointerExited` and `RightTapped`). Taking the selected tab out can drop or move the bar's selection, so
`Filters_SelectionChanged` ignores everything while the items move (`rearrangingTabs`), and the shown tab is selected
again inside that guard. Then nothing counts as the user's choice and nothing reloads. Moving the selected tab (Images,
then Pwsh by the keyboard) kept it selected with its list unchanged. Before the bar loads (the stored order at startup)
no selection care is needed: `SelectFilter` sets the items' own flags until then (§2.28).

**Verified** (rows in §5): 25 Core tests (`TabArrangementTests`, two mutations caught), the whole Core suite, and the
menu, every action, persistence across a restart, the keyboard route and the Chinese UI on a Windows 11 test desktop.

**Limits / not built:**
- No Settings page for the order (the menu is the only way; *Show all hidden* undoes every hide).
- No "Reset order" (moving the tabs back stores the default again, empty).
- No drag and drop of tabs to reorder them.
- A tab name a version does not know is dropped from the lists when the settings load. An older version that saves the
  file drops both members (the usual footgun): the default order, every tab shown.

### 2.41 Notepad++: its unsaved "new" tabs kept and updated, and the files it opens (built 2026-11-08)

User request (2026-11-08): "learn how we do integrations and Add notepad++ integration, showing 2 kinds of collected info,
1. is all the "new NNN" tabs that it remembers, their content and also capability to update if the content changes. if
content changes more than 20% then create a new copy. 2. When a file is opened in notepad++, its full path is as if was
copied to the notepad++ tab. also atomically, moving from old date to new when same file reopened."

**The owner's choices** (three HTML questions; the pages are in session 9f3b7bdf's scratchpad, `q/`):
- **"More than 20%": "Only lost text counts"**, with the comment "But dont monitor every 7s".
  - Added text never splits. Text that disappears (deleted or rewritten) counts, added up since the copy started, against
    the most text the copy held.
  - A tab is read once it rests (a minute), not at each of Notepad++'s 7-second backups.
- **Placement: "Like copies, edits stay put".**
  - Entries live in All, Text, Files and the Notepad++ tab like any copy, and an opened or reopened file goes to the top.
  - A tab's update changes its card in place, with an "edited …" time. Only a new copy (the split) goes to the top.
- **Detection: "Ask Notepad++ (exact)".** Plugin messages to a running Notepad++, plus a 64 KB buffer BetterClipboard places
  in Notepad++'s process to read paths, instead of guessing from window titles or recent-file lists.

**What the user gets.**
- **A Notepad++ tab** after Snipping (tooltip "Unsaved Notepad++ tabs and the files you open in Notepad++").
  - Shown while one of its two switches is on and the history holds something it lists (the witness of §2.36).
  - It lists (`ClipFilter.NotepadPlusPlus` = 14): origin `NotepadPlusPlusTab` (12) or `NotepadPlusPlusFile` (13), a source
    path `…\notepad++.exe`, or the source name "Notepad++". So copies made in Notepad++ show there too.
- **Unsaved tabs:** one card per tab, labeled "Notepad++ tab".
  - Caption "Notepad++ · new 3 · 5 min ago · edited 1 min ago". The tab's name is `clips.source_title`; "edited" shows while
    `clips.edited_utc` is newer than the card's time.
  - Renaming the tab renames the card's title; the entry stays the same.
- **Opened files:** the full path as text (`CF_UNICODETEXT`; a path card, so the Files tab lists it too, §2.13), caption
  "Notepad++ · 2 min ago · path". Opening the file again bumps the same entry (its content hash is the path's).
- **Settings › Integrations:** two cards, both on by default (`AppSettings.KeepNotepadPlusPlusTabs`,
  `RecordNotepadPlusPlusFiles`).
  - *Notepad++ unsaved tabs* (E70B QuickNote): "Notepad++ 8.9.1 found. / It remembers 2 unsaved tabs; a tab is read once
    it has rested for a minute. / 4 tab versions recorded since BetterClipboard started.", or why not: not installed, off,
    Notepad++'s snapshot setting off, still looking for its folder.
  - *Files opened in Notepad++* (E8E5 OpenFile): "Watching Notepad++ (3 files open). / 4 files recorded since
    BetterClipboard started.", or not running, or why it cannot be asked (translated through
    `NotepadPlusPlusClient.ProblemTemplates`, §2.37).
- **Settings › Third party:** Notepad++ (Don Ho) under *Works with*.
- **`bclip list -f notepad++`** (also `notepadpp`, `npp`): origins `notepad++-tab` and `notepad++-file`; JSON `sourceTitle`
  and `edited`.

**Notepad++'s facts.** **[verified]** in Notepad++ 8.9.1's source (`refs/notepad-plus-plus` at `f617e4e`, GPL-3.0: studied
for interoperability, nothing copied), on this PC's Notepad++ with a structure-only inventory (`npp_inventory.py` and
`npp_ipc_probe.cs` in session 9f3b7bdf's scratchpad; no tab name, path or text printed), and live in a test desktop.
- **Backups.** An unsaved tab's text lives in `<settings folder>\backup\<tab name>@yyyy-MM-dd_HHmmss` (local time of its
  first backup).
  - Untitled tabs are written to `<name>.tmp` and swapped in with `ReplaceFile`: a reader sees the old file or the new
    one, never half of one.
  - A renamed tab's backup is renamed and keeps its stamp. A name may hold '@', so the stamp is after the last one.
  - Encoding: UTF-8 without BOM by default (199 of this PC's 208 backups), UTF-8 or UTF-16 with a BOM, or the session's
    code page.
- **The snapshot timer.** Notepad++ rewrites a changed tab's backup every 7 s (`snapshotBackupTiming`) while "Enable
  session snapshot and periodic backup" is on (the default). Off: no backup files, nothing to read; Settings says so.
- **`session.xml`** names each tab (`filename`: a full path, or the untitled name) and its backup's full path.
  - Rewritten on exit and whenever a backup file is created or deleted: not on a rename, not when a file opens.
  - TinyXML writes it in place, so a read can catch it half-written. The parse then fails, and it is read again after
    the next change.
- **Startup order** (`Notepad_plus_Window::init`): the last session is loaded before the window is shown, and the
  command line's files are opened after. So the first look once the window is visible is a baseline: the restored session
  is not an opening, the command line's files are.
- **Plugin messages** (`NPPMSG` = `WM_USER + 1000`):
  - `NPPM_GETNBOPENFILES` (+7). With `ALL_OPEN_FILES` it counts both views, and the hidden second view always holds an
    empty placeholder document.
  - `NPPM_GETNPPVERSION` (+50), `NPPM_GETBUFFERIDFROMPOS` (+59), `NPPM_GETCURRENTBUFFERID` (+60).
  - `NPPM_GETFULLPATHFROMBUFFERID` (+58) copies into a pointer in Notepad++'s own address space, without a bound: the
    length is asked first with a null pointer.
  - A buffer id is the buffer's memory address, so a closed document's id can come back for another one.
- **Settings folder:** `-settingsDir=`; else the exe's folder when `doLocalConf.xml` lies next to it (the portable build;
  ignored directly under Program Files); else `%APPDATA%\Notepad++`. Cloud settings move the backups, not the session.
- **Signature:** `notepad++.exe` is Authenticode-signed `CN="NOTEPAD++", O="NOTEPAD++"`.
- **Measured on this PC:** 329 open documents listed in 24 ms, all their paths in 0.27 s.

**Design.**
- **Tracked documents** (`Core/Documents`, `Storage/ClipStore.Documents.cs`): a generic mapping `tracked_documents(scope,
  doc_key, clip_id ON DELETE SET NULL, content_hash, lost_chars, peak_length, title, seen_utc)`.
  - `ClipStore.RecordDocument` reads the mapping and the entry's text, decides (`TrackedDocumentRule`), writes the entry
    (in place, or a new copy through `UpsertCore`) and moves the mapping, in one SQLite transaction: the request's
    "atomically". Outcomes: Created, Updated, NewCopy, Unchanged, Suppressed, Skipped.
  - **In place:** text, kind, preview, search text, hash, size and formats are replaced; the place (`last_used_utc`), the
    pin and the groups stay; `clips.edited_utc` is set. When another entry already holds exactly the new text (hashes are
    unique), the mapping moves there and both versions stay.
  - **A deleted entry** leaves the mapping's last hash: that text is never recorded again, and the document's next change
    becomes a new entry.
- **The rule** (`TrackedDocumentRule`, `Content/TextLoss`).
  - Lost = the stored text's characters that are not in a longest common subsequence with the new text, from the edit
    distance D as `(stored − current + D) / 2`.
  - Cost: the common prefix and suffix are skipped, then Myers' O((N+M)·D), stopped once the loss is past what is allowed
    and at a work budget (40 M comparisons, about ¼ s). Past the budget the loss is estimated from sampled 16-character
    shingles (one in eight, chosen by hash).
  - Split when `(lost so far + lost) × 5 > peak`, in integers: exactly a fifth does not split. The old entry keeps the
    text it last held; the new copy starts at lost 0, peak = its length.
- **Origins** (`ClipOrigin`).
  - `NotepadPlusPlusTab` (12), ShareX's hybrid. A new event: pause (except for the first activation's import), ignored apps
    (`notepad++`), the size limit and Forget forever apply, and a duplicate is bumped. Like an import, it never lifts a
    tombstone and is skipped when older than the last clear, because every start reads the tabs again.
  - `NotepadPlusPlusFile` (13), a new event like `RunDialog`: bumped, skipped while paused, and it lifts a tombstone
    (opening a file is explicit).
- **The tabs half** (`NotepadPlusPlusTabWatcher`).
  - Folder watchers on the settings folders (`session.xml`) and their backup folders. A change only marks a tab, which is
    read once it was quiet for `QuietTime` (1 minute), at the latest `MaxDelay` (5 minutes) after its first unread change.
    A 5-second timer looks at the marks; nothing is polled.
  - Files are opened with every sharing mode and closed at once. Only the session's untitled tabs count: a saved file's
    backup with unsaved changes is not a "new" tab.
  - Renames: the watcher's Renamed event moves the tab (`ClipStore.RenameDocument`: the mapping's key and the entry's
    title). A rename made while BetterClipboard was not running is matched by the stamp.
  - A tab the session no longer names is forgotten: its mapping goes, the entry stays. Versions go to the store in slices
    of 50.
- **The files half** (`NotepadPlusPlusFileWatcher`, `NotepadPlusPlusClient`, `Core/NotepadPlusPlus/NotepadPlusPlusOpenTracker`).
  - One window thread. A WinEvent hook (`EVENT_OBJECT_NAMECHANGE`, Notepad++'s process only) looks 150 ms after a title
    change, and every 5 s a scan finds Notepad++ windows and asks each one's document count.
  - A look whose title and count did not change asks nothing more. A title change with the same count reads only the
    front document, which still catches a Save As.
  - The tracker: a new buffer id whose path is a full path is an opening, an untitled one never. The first visible look is
    the baseline. The command line's files (`NotepadPlusPlusCommandLine`: Notepad++'s own splitting rules re-implemented,
    the line read with `NtQueryInformationProcess` class 60) count for a process started while BetterClipboard watched.
- **Owner check** (`NotepadPlusPlusOwnerVerifier`): the window's process must be `notepad++.exe` with a valid Authenticode
  signature by O=NOTEPAD++. It shares the Everything check's code (`EverythingOwnerVerifier.VerifySignature` and
  `Organization`, now internal). No message goes to any other window of class "Notepad++".
- **The client.** Every message goes with `SendMessageTimeout` (500 ms, aborted at once when Notepad++ hangs).
  `VirtualAllocEx` once, at the first path read (64 KB, `PAGE_READWRITE`), `ReadProcessMemory` per path, `VirtualFreeEx` on
  dispose. A Notepad++ running as administrator while BetterClipboard is not refuses `OpenProcess` (error 5): reported,
  not retried in a loop.
- **Life cycle** (`NotepadPlusPlusIntegration`, owned by `AppController.NotepadPlusPlus.cs`).
  - The first activation imports every tab Notepad++ remembers (state `notepadpp.tabs.imported`); later starts catch up,
    recording only what changed.
  - Off forgets the mappings and the marker; the entries stay, and on again imports without duplicates.
  - Located again every 5 minutes and when Settings opens. `BETTERCLIPBOARD_NOTEPADPP_DIR` replaces every real Notepad++
    folder (tests, isolated instances).

**Pieces.**
- Core: `Content/TextLoss`, `Documents/` (`TrackedDocument`, `TrackedDocumentRule`), `NotepadPlusPlus/` (`Sources`,
  `Session`, `Backup`, `Config`, `CommandLine`, `OpenTracker`), `Storage/ClipStore.Documents.cs` (with `clips.source_title`
  and `clips.edited_utc`, added idempotently like the groups' column), `Services/ClipHistoryService.Documents.cs`, the
  tab's witness in `ClipHistoryService.TabContent.cs`.
- Windows: `Integrations/NotepadPlusPlus{Client,Locator,TabWatcher,FileWatcher,Integration}.cs`,
  `Interop/NativeMethods.NotepadPlusPlus.cs`.
- App: `AppController.NotepadPlusPlus.cs`, `Views/ClipboardFlyout.NotepadPlusPlus.cs`,
  `ViewModels/SettingsViewModel.NotepadPlusPlus.cs`, the two Settings cards, the tab, the card's caption and label.
- Strings: 24 entries and 4 plural strings in both catalogs (`catalog.py add-file`, `add-plural`).

**Found by the live check, fixed:**
- **Every card of a known app read "… · Unknown app".** The tab-name branch went between the app's `if` and its `else if`,
  which then belonged to the new `if`. Seen as "Notepad++ · Unknown app · 6 min ago" on a copy made in Notepad++. Fixed in
  `ClipItemViewModel.BuildCaption`; a scan of the whole change (§4) found no other case.
- **"6 documents open" over 5 tabs:** Notepad++'s count includes the hidden second view's placeholder. The status counts
  files now (`NotepadPlusPlusOpenTracker.OpenFileCount`: distinct full paths).

**Verified:** see §5.

**Limits / not built:**
- A file opened again while it is still open in Notepad++ (a double-click in Explorer on it) only switches tabs: no
  reopen is seen. No signal tells it apart without a plugin.
- Files opened while BetterClipboard was not running, and the files Notepad++ restores at its start, are not openings.
- A Notepad++ running as administrator while BetterClipboard is not cannot be asked (its unsaved tabs are still read:
  they are files).
- Not run live: `-multiInst` with several settings folders, cloud settings, the portable build, a legacy code page.
- No Notepad++ plugin, and nothing is ever written to Notepad++.

### 2.42 Hide tab & clipboard rows: a tab hidden together with its entries (built 2026-11-09)

User request (2026-11-09): "Rename Hide -> Hide Tab. Add another option Hide Tab & Clipboard Rows, the show all hidden
restores this hide too."
- **Four questions asked** (HTML pages per option, memory "HTML questions for choices"; the pages are in session a6fd5958's
  scratchpad, `q/`). The owner chose:
  - **Where the rows go:** "Other": **"They become hidden, everywhere except for ones dragged/copied to groups"**. Read as:
    an entry the tab lists is left out of every list the panel shows — unless it is in a group, which keeps it wherever
    it shows (All included). Pinned entries are hidden like the rest: the offered option that also spared pinned ones
    was narrowed to groups.
  - **Which tabs:** every tab but All (Recommended).
  - **Labels:** sentence case, "Hide tab" and "Hide tab & clipboard rows" (Recommended), like the menu's other items.
  - **The footer** says how many are hidden (Recommended).

**What the user gets.**
- **The tab menu:** *Move left*, *Move right*, a separator, *Hide tab*, *Hide tab & clipboard rows*, *Show all hidden
  (N)*. Both hides use the Hide glyph (`ED1A`), are grayed out only on All, and N counts hidden tabs, either kind.
- ***Hide tab & clipboard rows*** hides the tab like *Hide tab*, and leaves its entries out of every list: All, the
  other tabs (Text, Files, Pinned…), searches, the stored halves of the Everything, shell and prompt tabs. Entries in a
  group stay. Hiding the tab you are in goes to All, as before.
- **The footer** of the stored-history tabs reads "1,240 items · 6 pinned · 37 hidden" while rows are hidden; the total
  still counts them.
- ***Show all hidden*** brings back the tabs and their rows, and reloads the list.
- **Nothing else changes:** nothing is deleted or kept from being recorded, the integrations keep reading, and `bclip`
  lists everything (the hiding is the panel's).
- **Kept** in `settings.json`: `HiddenTabRows`, always a subset of `HiddenTabs`.

**How.**
- **Core `AppSettings.HiddenTabRows`** (tab names): `Normalize` keeps known names that are also in `HiddenTabs`, so a tab
  in the row never has its rows hidden. Footgun: an older version saving the file drops it, and the rows show while their
  tabs stay hidden.
- **Core `ClipQuery.HiddenRows`** and `ClipStore.HiddenRowsCondition`: `(coalesce((p1) OR (p2) …, 0) AND NOT EXISTS
  (SELECT 1 FROM clip_groups hg WHERE hg.clip_id = c.id))`, each `pN` the tab's own `FilterPredicate`, so a tab's rows
  are exactly what the tab listed; `Query` adds `NOT` of it.
  - **The `coalesce` is load-bearing:** ShareX's `source_app_path LIKE …` is NULL for an entry without a path, and Files'
    `path_count > 0` for a row the backfill has not decided. `NOT NULL` would have dropped such entries from every list
    (a mutation without it fails `Query_AnEntryTheFilterCannotDecide_StaysListed`).
- **Core `ClipStore.GetStats(hiddenRows)`** counts the same condition in its one pass (`StoreStats.HiddenCount`);
  `ClipHistoryService.GetStatsAsync(hiddenRows)` copies the list on the caller's thread.
- **App `FlyoutViewModel`:** `HiddenRows` (set by the panel, sorted and distinct) goes into every query it makes — the
  main list, and the stored halves of the Everything, shell and prompt tabs — and into its `ViewKey`, so a list loaded
  before a change is stale, also in the hidden panel. The footer appends " · {0:N0} hidden".
- **App `ClipboardFlyout.TabArrangement.cs`:** `hiddenRowTabs`, `HideTab(tab, withRows)` (the rows are set before the
  tabs change, so the switch to All loads without them; otherwise the list shown is reloaded), `ShowAllHiddenTabs` clears
  both sets and reloads when rows come back, `SaveTabArrangement` writes `HiddenTabRows` with the rest.
- **Strings** (`catalog.py remove "Hide"` and `add-file`): "Hide tab", "Hide tab & clipboard rows" and " · {0:N0} hidden"
  in all five catalogs, with descriptions; the two "Show all hidden" descriptions say the rows come back. Translations
  (by Claude, like §2.37's): 隐藏标签页 / 隐藏标签页及其剪贴板条目 / 已隐藏 {0:N0} 项; Скрыть вкладку / Скрыть вкладку и её
  строки в буфере обмена / скрыто: {0:N0}; Ocultar pestaña / Ocultar pestaña y sus filas del portapapeles / {0:N0} ocultos;
  הסתר את הכרטיסייה / הסתר את הכרטיסייה ואת השורות שלה בלוח / {0:N0} הוסתרו.

**Verified** (rows in §5): 6 Core tests (`HiddenTabRowsTests`) with a mutation check, the suites, and the menu on a
Windows 11 test desktop.

**Limits / not built:**
- No way to show one tab back alone (*Show all hidden* is the only undo, as for *Hide tab*).
- A group's own footer ("5 in Work") and the Everything, shell and prompt tabs' footers do not mention hidden rows; group
  views never hide any.
- The Run tab's footer ("N commands kept") counts every Win+R command, hidden ones included.

### 2.43 Settings search, and *Delete stored…* on every integration card (built 2026-11-09)

User request (2026-11-09): "1. Add search to settings which fuzzly match to anything of that setting. 2. Every integration
should have Delete stored button with a confimation dialog with preview and examples 3. Manaul QA but DO NOT DELETE
ANYTHING ON THIS PC". Four HTML questions (pages in session b8a49ba2's scratchpad, `q/`); the owner chose:
- **Search: "Filter the page"** (Recommended): a box pinned under the title bar; the page keeps only the matching
  settings, in their order under their section titles, with the matched letters highlighted, a count beside the box, Esc
  clears.
- **Scope: "Everything its tab lists"** (Recommended), with the comment **"But we leave the source alone, always. for
  example sharex screenshots are still in sharex's folder"**: a source app's own files are never touched; only
  BetterClipboard's copies go. Pinned and grouped items are kept unless ticked.
- **Dialog: "Parts, counts, examples"** (Recommended): one row per part with a checkbox, its count and size and its newest
  examples (thumbnails or lines, "+N more"), a note on what is never touched, "Also turn off …", a red "Delete N items",
  Cancel the default.
- **QA: "Delete the VM after QA"**: every deletion in a claude-desktops Windows desktop with BC-TEST data; nothing on the
  owner's PC.

**The search** (`Core/Presentation/SettingsSearch`, pure and tested; `App/Views/SettingsWindow.Search.cs`).
- **The box:** `SearchBox` in the window's second row (`MaxWidth` 880, like the page), `Ctrl+F` / `Ctrl+E` from anywhere in
  the window; spelling checker and text prediction off (`SearchBox_Loaded`: "claude", "sharex" and "pwsh" were underlined
  as misspelled). The count beside it ("4 settings match", "No matches") is a polite live region. Esc clears the search
  and puts the page back at the scroll position it had when the search started; Esc on an empty box gives the keyboard
  to the page.
- **The filter:** a matching card stays where it is; other cards and sections without a match collapse, the hero hides;
  none: "No setting matches “…”." with a hint. A new query scrolls to the top; a status line that changes while a search
  shows searches again after 250 ms, keeping the position.
- **What a setting is searched by** (`DocumentOf`): its title and description (the two highlighted fields), its section's
  title, translator-written keywords (`SettingsCard.Keywords`, a catalog string on 41 cards, never shown: "screenshots,
  print screen, PrtScn, snip, Win+Shift+S"), everything else on the card — text blocks, button and option contents, list
  rows (`ISearchableItem`), tooltips, accessible names, placeholders; never a switch's On/Off or a number box's digits,
  which would find every card — and the English of every translated text (`UiText.EnglishOf`, reverse catalogs built on
  first use), so "shortcut" finds 快捷键's card.
- **Matching** (class remarks are the full spec): texts folded (case, base letters of diacritics, format characters and
  marks dropped — Hebrew's embedding, combining accents), at most 8 words, every word must match somewhere (AND). Per word
  the best of: a whole word (1.0), a word's start (0.85), initials of consecutive words (0.7, "rfe" → Release from
  Explorer), inside a word (0.6; a single letter only at a word's start) — the ways *as written*; then, only when the
  setting has the word as written nowhere, the *fuzzy* ways: its letters in order from a word's start within
  `max(2n, n + 3)` characters and never across white space (≤ 0.5; "clpbrd" → clipboard, "winv" → Win+V), or a typo
  (optimal string alignment: one from 5 letters, two from 8; a word's start with one typo, "paxsword"). Weights: title 3,
  keywords 2.2, section 1.8, description 1, other 0.7. Highlights map back to the shown text through embeddings and
  combining marks (`FoldedText.ToOriginal`).
- **Which settings show** (`SettingsSearch.Shown`): when any setting has every word as written, only those; fuzzy matches
  are the fallback for a mistyped word. Found in the QA: "theme" listed 17 settings ("these" by a typo, "the mouse" by
  letters running across a space) around the one titled "Theme"; now it lists that one, and "pasword" still finds its
  two settings. The score decides nothing else: the page keeps its own order.

**Delete stored…** (`Core/Storage/StoredData`, `ClipStore.Stored.cs`, `Services/ClipHistoryService.Stored.cs`,
`Presentation/StoredText`; `App/AppController.Stored.cs`, `ViewModels/DeleteStoredViewModel.cs`,
`SettingsViewModel.Stored.cs`, `Views/DeleteStoredView.xaml`, `SettingsWindow.Stored.cs`; `DangerButtonStyle` in App.xaml).
- **Ten cards** (`StoredSource`), each a union of *parts* (`StoredData.PartsOf`). An entry part is slices of a tab's own
  filter (`FilterPredicate`, NULL-safe) narrowed by origins and by protection, so a part never reaches beyond its tab:

  | Card | Parts (ticked by default unless marked) |
  |---|---|
  | ShareX screenshots | Screenshots ShareX saved (origin ShareX) · Copied by ShareX (the tab's other entries) |
  | Snipping Tool and Win+PrtScn | Screenshots Windows saved (origin WindowsScreenshot) · Copied by Snipping Tool or Win+PrtScn |
  | Notepad++ unsaved tabs | Versions of unsaved tabs · Copied in Notepad++ (on both Notepad++ cards) |
  | Files opened in Notepad++ | Paths of files opened in Notepad++ · Copied in Notepad++ |
  | Everything | Picks you kept from the tab · Copied in Everything · Picks you hid (unticked; the `everything.hidden` list) |
  | Win+R history | Commands run with Win+R (both Win+R origins) · Copied first, then run with Win+R |
  | Pwsh tab | Commands you kept from the tab · Commands you hid (unticked; `pwsh.hidden`) |
  | Cmd tab | Commands kept from Command Prompt windows (`cmd.kept`) · Commands you kept from the tab · Commands you hid (unticked) |
  | Claude Code prompts | Archived prompts · Prompts you kept from the tab · Copied in Claude Code |
  | Codex prompts | Archived prompts · Prompts you kept from the tab |

  Every card with entry parts also has **Pinned or in a group** (the same slices, protected; unticked). Empty parts are
  left out of the dialog.
- **Counting:** `ClipStore.SummarizeStored` counts every card's unions in one pass (two `MATERIALIZED` CTEs: each row's
  protection and each distinct tab filter evaluated once; written naively, 27 unions took 0.9 s over 20,000 entries, this
  way ~0.5 s — the probe in session b8a49ba2's scratchpad, `probe_stored/`). Settings counts on open and again 2 s after
  the last history, prompt or shell change; a card's button is enabled only while it has something (`Stored`), and is
  named "Delete stored: {card title}" for UI Automation and screen readers.
- **Deleting** (`ClipStore.DeleteStored`, one transaction): the entries go exactly as if deleted one by one from the panel
  — their content is tombstoned (30 days), so the next ShareX or Screenshots catch-up, the Windows import and a re-import
  of Windows' Win+R list do not bring them back, while a new live copy is recorded again; groups lose them; a tracked
  document (a Notepad++ tab) keeps its last text, which is not recorded again until it changes; then an incremental
  vacuum. Archives: the agent's reader is stopped, its prompts, files, tombstones and import mark are cleared, and it is
  started again when its switch is on — which reads what the agent still has at once, as a first import (the dialog's
  note says so). Lists: a hidden list is emptied (its commands or picks show in their tab again); the Cmd tab's kept list
  is emptied and `CmdHistorySource.ForgetKeptAsync` forgets it in memory while remembering what each open window held, so
  an open window's old commands are not kept again (checked past its next 30-second read). Never touched: the source
  apps' files and lists (ShareX's and the Screenshots folder's pictures, Notepad++'s backups, PowerShell's history file,
  Windows' `RunMRU`, Claude Code's and Codex's files — each checked by hash or listing in the QA).
- **The dialog** (`ContentDialog`, `ContentDialogMaxWidth` 640): "Delete what BetterClipboard stored from {source}?", a
  summary ("5 items stored · 6.2 MB · newest 11 min ago"), the parts (count · size, up to 4 newest examples: thumbnails for
  pictures, monospace lines for paths and commands, a count only for hidden lists, which keep fingerprints), the note,
  "Also turn off “{card title}”" while the switch is on (flipped before the deletion, so nothing is recorded meanwhile), and
  the red "Delete N items" (plural strings; disabled with nothing ticked) next to Cancel, the default button — Enter and
  Esc cancel. A failed deletion keeps the dialog open with the reason. Texts that may name a shortcut go through
  `StoredText.KeepShortcutsTogether` (word joiners around the "+" of "Win+PrtScn": DirectWrite broke the title as "…Win" /
  "+PrtScn?").
- **Logs** (content-free): "Deleted stored data: N history entries, N archived prompt(s), N list(s) emptied." and
  "Settings › {source}: Delete stored removed …". The old *Delete stored prompts…* button of the two prompt cards, its view
  model members and its 4 strings are gone: *Delete stored…* replaces it.

**Found and fixed in the QA** (rows in §5): the page shrank to the width of its content and was centered when a search
left only short texts or none (WinUI's scroller lays its content out at the content's own width and centers it: the page
now gets an explicit width from the scroller's size, `Scroller_SizeChanged`); the fuzzy noise above; the title break inside
"Win+PrtScn"; the Snipping part labeled "Copied by Snipping Tool" although its entry was a Win+PrtScn screenshot (a
clipboard copy whose file merged in: now "… or Win+PrtScn"); Russian's "Также выключить ««Ножницы» и Win+PrtScn»"
(nested guillemets; now a colon, and the string's description says why); "reading stopped" logged twice when "Also turn
off" and the deletion both stopped a prompt reader (`AgentPromptsIntegration.SetEnabledAsync` is serialized now); the
spelling checker in the search box.

**Also on the way:** `catalog.py remove` left a plural string's other languages' forms (`#few`, `#many`, `#two`)
behind; it removes them now. 85 new strings and 3 plural strings in all five catalogs (translated by Claude, like §2.37's),
each with its description.

**Limits / not built:**
- The Everything card's hidden picks were not seen live (no Everything in the test desktop); unit-tested.
- Hebrew and Chinese dialogs were not seen on screen (Russian, the longest, was).
- Deleting cannot be undone (as a delete in the panel); the counts are not refreshed while a dialog is open.
- Line breaks inside "Win+PrtScn" elsewhere in the app (a card's description in Russian showed "…от Win" / "+PrtScn") are
  older than this change and not fixed: §6.

### 2.44 The merged tabs in time order: a kept item no longer leads the Claude, Codex, Pwsh and Cmd tabs (fixed 2026-11-11)

User report (2026-11-11): "The first row in Claude tab is always old one for some reason, identify why and if its in other
tabs too and fix".

**Why** **[verified]**.
- **Pasting from a prompt tab keeps the prompt.** `AppController.PastePromptAsync` (Enter, *Copy only*) stores the text as a
  history entry (origin `ClaudeCode` / `Codex`), as do Ctrl+P and a group (§2.21).
- **And every kept entry led the tab.** `FlyoutViewModel.ReloadPromptsAsync` listed the tab's stored entries (up to 200)
  above the whole archive, whatever their age, and left their texts out of the archive's pages. So the first prompt ever
  pasted from the tab stayed its first row for good, above every prompt sent since. It was also selected (§2.28): Win+V,
  Enter pasted that old prompt.
- **The same in other tabs:**
  - Codex shares the loader.
  - Pwsh and Cmd: `ShellTab.Merge` put the stored half first (§2.19's documented order).
  - Everything sorted by time already (§2.14), but a file both kept and picked sorted by its last use only: a file kept
    last month and opened again today sorted as last month's.
  - The tabs of stored history (All, Text, Run, ShareX, Snipping, Notepad++, …) list one source, by recency: not
    affected.
- **Reproduced** on the released 0.2.10 in a test desktop (§5): two prompts kept from the Claude tab sat above two
  prompts sent after them, and a command kept from the Pwsh tab above two commands typed after it.

**The rule now** (`Core/Presentation/MergedTabOrder<TItem>`, pure; its remarks are the full spec): newest first by each
row's newest activity, one row per content.
- **Times:** a stored entry's is its last use; an item's is its own — a prompt's last send, a Cmd command's last time seen,
  a pick's last opening.
- **One row per content:** an item with a stored entry's content hash (its "twin": the same prompt, command or file) shows
  once, as the entry (pins, groups), at the newer of the two times.
  - An entry used after its twin's time is placed by its own time; the twin is dropped.
  - One used before is placed where its twin is, and its card shows the twin's time
    (`ClipItemViewModel.ShowNewerTime`). Without that, a kept "continue" sent again a minute ago read "3 days ago" among
    rows of minutes ago.
- **Pins:** with "Show pinned items first" on, pinned entries lead (most recently used first), as everywhere in the panel.
  Their twins are dropped, and ArmCard still selects the newest unpinned row (§2.28).
- **Ties:** the stored entry first.
- **Items without times** (PowerShell's file keeps none) keep their order. A kept command shows at its command's place in
  the file. It leads only when it was used after the newest command was typed: the history file's last write
  (`PowerShellHistorySource.LastNewestCommandUtc`, via `AppController.PowerShellNewestCommandUtc`). One with no twin in the
  700 commands read and used before that goes after the live commands; `ShellTab.CapCommands` keeps every stored row past
  the 500-command cap.
- **Paging** (the prompt tabs, 60 texts a page): the order is one object per load (`FlyoutViewModel.promptOrder`).
  - An entry older than every prompt of the loaded pages waits for its page; the last page (fewer than 60 rows) places
    what is left.
  - The archive is no longer told to leave the kept texts out (`PromptQuery.ExcludeTextHashes` stays, unused by the
    panel): a kept text's archive row is where the order learns that it was sent again.
  - A waiting entry deleted meanwhile is forgotten (`TryApplyInPlace` → `Forget`), so it never comes back with its page.
  - The footer's "· N kept" counts the waiting ones too (`WaitingCount`).

**Pieces.**
- Core: `Presentation/MergedTabOrder.cs` (new), `Shells/ShellTab.Merge` (+ `newestTypedUtc`, `CapCommands`,
  `ShellTabRow.Time`), `Everything/EverythingTab.Merge` (`EverythingTabRow.Time` is now the merge's time).
- Windows: `PowerShellHistorySource.LastNewestCommandUtc`.
- App: `FlyoutViewModel.Prompts.cs` (the order, `PromptCards`), `FlyoutViewModel.Shells.cs`, `FlyoutViewModel.cs`
  (Everything's cards, `CreateItem(entry, rowTime)`, `Forget` on a removal), `ClipItemViewModel.ShowNewerTime`,
  `AppController.PowerShellNewestCommandUtc`.

**Verified** (rows in §5): 24 Core tests and 1 Windows test; two mutations — every stored entry first again, and no twin
handling — failed 22 and 17 tests; the whole suite; and the fault reproduced on the released 0.2.10 and gone with the fix,
in a Windows 11 test desktop.

**Limits / not built:**
- A pinned entry's place among the pins follows its last use only (its twin's time may lie on a page not loaded yet).
- Pwsh: a kept command used before the newest command was typed and no longer among the 700 commands read lands after
  the live commands, also when it was used a minute before that command.
- Cmd's times are when BetterClipboard saw a command (§2.19), so a kept command typed again is placed by that sighting.

### 2.45 Memory: Settings no longer stays in memory, and memory is given back after every use (built 2026-11-12)

User report (2026-11-12): "We have memory usage problem / 1. In general the memory we use is too large, 400mb / 2. Also in
general the memory when opening settings and closing it rises and doesnt go back down. / Optimize memory end to end". Two
HTML questions (pages in session f1d22507's scratchpad, `q/`); the owner chose:
- **Settings' title bar: "Standard Windows title bar"**, over keeping the custom one with its handle leak (below);
- **The working-set trim: "After every use"**, over only after a long idle, or never.

**What was measured first.** Tools: VMMap, dotnet-counters, dotnet-gcdump and dotnet-dump. Places: a claude-desktops
Windows 11 desktop ("bc-mem": no GPU, a seeded BC-TEST store of 3,300 entries and 15,000 prompts), and this PC's real GPU
with isolated instances only.
- **The installed 0.2.10 on the owner's PC** after two days: 267 MB private, ~9 MB of it live .NET objects. The rest was
  native: the process heap (~93 MB: WinUI's XAML objects, text layouts, decoded images) and private allocations outside
  any heap (~93 MB, 22 MB of it GPU write-combined memory); "What is left" below has the measured split.
- **Settings in 0.2.10** (VM):
  - the first open added 103 MB of private memory (76 → 179 MB), and every later open and close added another 17 to
    22 MB for good (4 more cycles: 172 → 249 MB);
  - a forced full collection gave nothing back, and a heap dump after five closes held all five Settings windows;
  - GDI handles grew by 32 per open and close (52 → 191).
- **Thumbnails** were decoded at the stored 720 × 400, whatever size they were drawn at.

**Why a closed Settings window stayed in memory** **[verified]**:
- **The cycle.** The XAML compiler's generated x:Bind code subscribes to the window's own `Activated` event
  (`element1.Activated += bindings.Activated`, and the bindings refer back to the window) and never unsubscribes. Settings
  also had handlers on itself (`Closed`, `Activated`) and on two `DispatcherQueueTimer`s (the search refresh and the
  stored-data refresh); the update dialog had one more (its "Copied" timer).
- **Why nothing collected it.** Those are native ↔ managed cycles that C#/WinRT's reference tracking cannot see through:
  `Window` and `DispatcherQueueTimer` are not XAML elements, so the collector never learns that the window is unreachable.
- **The fix.** `SettingsWindow.OnClosed` takes all of them off:
  - `DetachGeneratedBindings` stops the bindings' tracking and removes the generated `Activated` handler, which it finds
    by reflection (a log warning names it if a future XAML compiler renames it);
  - the timers' `Tick` handlers are named methods and are removed (`CloseSearch`, `CloseStoredRefresh`);
  - `UpdateDialog.Detach` stops the update dialog's timer;
  - `AppController.OnSettingsWindowClosed` removes its own handler. `LanguageWindow` removes its `Closed` handler too.
- **Afterwards** a heap dump holds no closed Settings window, and memory levels off (measured below).

**The handle leak was the extended title bar** **[verified]**:
- A WinUI window that extends its content into the title bar leaves 24 to 32 GDI handles behind per open and close.
  Both APIs do it: `ExtendsContentIntoTitleBar` with `SetTitleBar`, and `AppWindow.TitleBar.ExtendsContentIntoTitleBar`.
- With Windows' own title bar the growth is 3 per cycle. A diagnostic build without the app icon was never combined with
  the standard title bar; the window icon is the likely source **[inferred]**.
- A process may hold 10,000 GDI handles, so at 32 per cycle about 300 opens of Settings would have broken the app.
- **The owner's choice:** Windows draws Settings' title bar now (the app icon, "BetterClipboard", Mica below it). It is
  dark while the app's theme is dark (`DWMWA_USE_IMMERSIVE_DARK_MODE`, `WindowInterop.SetDarkTitleBar`), and follows a
  theme change (`Root_ActualThemeChanged`).

**Giving memory back** (`AppController.Memory.cs`; `Core/Diagnostics/MemoryReclaim`; `Windows/Diagnostics/ProcessMemory`):
- **Why a reclaim is needed.** What a window or a decoded picture holds is native memory, freed only when the .NET
  wrappers that own it are collected, and a tray app that allocates little may not collect for hours.
- **When it is asked for, and how long it waits:**

  | Reason (as logged) | Waits |
  |---|---|
  | "Settings closed", "the language picker closed" | 3 s |
  | "using the panel" (the panel put away, `ClipboardFlyout.Conceal`) | 30 s |
  | "a list refresh" (the hidden panel reloaded after copies) | 60 s |
  | "the start" (imports and warm-up done) | 90 s |

- **The plan** (`MemoryReclaimPlanner`, pure, tested):
  - each reason waits for its own quiet period;
  - a second request for the same reason postpones it, so a burst of summons ends first, but never past 2 minutes after
    the burst's first request;
  - nothing runs while the panel or Settings is on screen (a check again every 10 s).
- **The reclaim** (`ProcessMemory.Reclaim`, on a pool thread):
  - never on the UI thread, because a finalizer may need that thread to release a WinUI object;
  - an aggressive, compacting full collection, the finalizers, a second collection, `HeapCompact` of the process heap,
    then the working-set trim (`SetProcessWorkingSetSizeEx(-1, -1)`).
- **Logged** (content-free) when it gave back 8 MB or more. The VM's line: "Memory after Settings closed: gave back 39 MB
  of private memory (176 → 137 MB); now private 137 MB, memory 1 MB (working set 2 MB), .NET heap 4 MB." Since then
  values below 10 MB carry one decimal, because a freshly trimmed working set read "0 MB".
- **The trim, the owner's "After every use".** Task Manager's "Memory" column (the private working set) reads a few
  megabytes while BetterClipboard waits: 3.7 to 6.7 MB on the VM, against 25 to 36 without the trim.
  - The price, measured on the VM: the next summon brings its pages back from RAM, a median of 41 ms instead of 28.
  - After Windows gave that RAM to another program, the pages come back from the page file, which Windows' own trimming
    under memory pressure costs anyway.

**Smaller pieces:**
- **Thumbnails** (the cards; the *Delete stored…* dialog's examples): the `BitmapImage` goes to its `Image` before
  `SetSourceAsync`, so WinUI decodes it at the size it is drawn.
- **The image peek and viewer** drop their decoded full picture (up to 33 MB for a 4K one) when the panel is put away
  (`ReleaseImageCache`).
- **`GetImagePngAsync`** reads only the format it needs, the first of `ContentClassifier.PrimaryImageFormats` in the
  classifier's order (`ClipStore.GetFirstFormat`). Before, it loaded every format of the entry, and a 4K copy holds a PNG
  and a 33 MB DIB.
- **No spelling checker or text prediction** in the search box, the shortcut box, the ignored-apps box and the group-name
  boxes. Once a box with them had the keyboard, Windows' spelling engine with every installed language's dictionary stayed
  loaded in the app.
- **The segment heap** (`app.manifest`, `heapType` SegmentHeap) for the process heap: it hands freed memory back to
  Windows, where the classic heap kept it committed after a burst. UWP apps and Edge use it.

**Measured after** (rows in §5):
- **8 Settings opens and closes** (VM, final build `0.2.11-dev.mem`):
  - 87 MB private at the start, 179 MB with Settings open, 137 MB after the first close;
  - then a plateau of 154 to 160 MB through the 8th close;
  - Task Manager's "Memory" 6 to 12 MB six seconds after each close;
  - GDI 44 → 71 (+3 per cycle), USER +1 per cycle.
- **The images tab paged through, then the panel hidden for 45 s** (VM): 67.6 MB private, against 98.8 MB in 0.2.10.
- **Idle after the start's reclaim:** 46 MB private on the VM, against 87 MB in 0.2.10. On this PC's real GPU 114 MB
  private and a 3 MB .NET heap ("gave back 49 MB of private memory (163 → 114 MB)"), against 166 MB in 0.2.10.
- **Settings cycles on this PC's real GPU:** a diagnostic build, never committed, opened Settings cloaked so nothing showed.
  0.2.10 grew 166 → 262 MB in five cycles. The fix gave back 51 MB three seconds after the first close and levelled off at
  190 to 193 MB.

**What is left, measured 2026-11-12** with [`probe_memmap.cs`](tools/probes/probe_memmap.cs) on this PC (NVIDIA GPU). "Charged"
is what Private Bytes and Task Manager's "Commit size" count; "resident" is the private working set, Task Manager's
"Memory" column. The new build ran isolated on the seeded BC-TEST store, idle after its reclaim; 0.2.10 is the owner's
installed copy after 2.5 days of use.

| Kind (charged / resident) | New build, idle | 0.2.10, after 2.5 days |
|---|---|---|
| Native heap: WinUI's XAML trees, text layouts, DirectWrite, COM, SQLite caches | 34.8 / 7.1 MB | 97.1 / 51.3 MB |
| NVIDIA driver DLLs' writable data (`nvwgf2umx.dll` 25.0, `nvppex.dll` 3.7, `nvgpucomp64.dll` 3.3) | 32.0 / 0.1 MB | 32.0 / 0.1 MB |
| Write-combined memory the GPU reads (the driver's upload heaps) | 20.1 / 0.0 MB | 21.7 / 2.0 MB |
| Other private allocations, owner not visible from outside | 6.5 / 1.1 MB | 72.7 / 4.9 MB |
| Other DLLs' writable data | 4.2 / 1.1 MB | 5.3 / 2.1 MB |
| .NET GC heap | 4.2 / 1.4 MB | 11.9 / 10.6 MB |
| Thread stacks (66 and 75 threads) | 1.7 / 0.1 MB | 2.0 / 0.3 MB |
| **Private Bytes / "Memory"** | **110 / 12 MB** (108 / 6.8 right after the reclaim) | **255 / 77 MB** |

- **The rest of the address space is not charged:** DLL code (445–474 MB committed, shared with other processes), mapped
  files (fonts, the font cache, locale data) and pagefile-backed sections (245–257 MB, mostly the CFG bitmap).
- **0.2.10's "other private allocations"** are two 32 MB reservations (27.3 and 11.2 MB charged) and five 5.2 MB
  blocks: no heap header, no visible owner, almost nothing resident. In the new build the reservations hold 1.6 MB and the
  blocks are gone; most likely graphics resources of the Settings windows 0.2.10 kept alive **[inferred]**.
- **The new build's heap is the segment heap** (`app.manifest`). Its page segments carry encoded signatures; the probe
  groups them by the (signature ^ address) that every segment of one context shares, and a check of three 16 MB
  reservations (29 MB) confirmed them as one context's segments.
- **What is left is mostly the price of two things.** The instant summon keeps the panel's window and its XAML tree
  alive, cloaked (§2.27): most of the 35 MB heap. Rendering with the GPU loads the NVIDIA driver: 52 MB charged and
  almost nothing resident. The GPU's own memory ("Dedicated GPU memory" in Task Manager: 84 MB for 0.2.10) is video
  memory, not RAM.

**Found on the way: a timing test that followed the test assembly's folder.**
- `CliPipeServerTests.ClientHangUp_CancelsHandler` hung up on a fixed 300-ms timer. When the request had not reached the
  handler by then, the handler never ran and the test timed out.
- With the same binaries it failed in every full run from one folder and in none from others (10 of 10 against 0 of 7):
  xunit's test order follows the test assembly's path, and in that order the pipe's first connection waited behind the
  tests beside it. The diagnosis: the handler started at 1–2 ms in the passing order, and never in the failing one.
- It now hangs up once the handler runs (CLAUDE.md §4). Mutation check: with the server's hang-up handling removed, it
  still fails.

**Limits / not built:**
- The first open of Settings leaves one-time caches behind (+50 to 60 MB private: XAML templates and resources, and five
  window-size surfaces that later opens reuse). They are not given back by the process itself; since the same day the
  silent restart (§2.46) replaces the process a few seconds after Settings closes.
- The NVIDIA driver's share (52 MB of the idle 110 on this PC: its DLLs' writable data and write-combined GPU memory,
  charged but hardly resident) is outside the app's reach while it renders with the GPU.
- 3 GDI handles per Settings open remain (most likely the window icon).
- The README's Settings photo (§3.4) still shows the old title bar.
- The trim makes the first summon after a pause slower by about 13 ms (median, VM); more once Windows has reused the RAM.
- Not run: a standard user account, ARM64, several monitors.

### 2.46 The silent restart after Settings: the memory only a new process gives back (built 2026-11-12)

User request (2026-11-12): "After opening settings, can the app silently restart while user doesn't have the clipboard panel
open / The reason we want to restart is to free the loaded memory when settings opened which is permanent and we can't let
go so a restart clears it well. / User must not see or feel or have UI flash or open accidentally during this silent
restart." Two HTML questions; the owner chose **"Right after Settings closes"** (a few seconds after it closes, whenever
the panel is not open) and **"No switch, always on"**.

**What the user gets.**
- A few seconds after Settings closes, BetterClipboard replaces itself with a fresh process. Its private memory goes back
  to the start's level: on the test desktop 131 to 146 MB before, 44 to 49 MB after (45 MB after the first start).
- Nothing shows: no window, no change of the window in front, no busy pointer, the tray icon stays in its place (also when
  pinned to the taskbar), the shortcuts keep working through the switch, and a copy made in the gap is recorded.
- No setting. The log says it happened (content-free): "Restarting silently to give back the memory Settings left
  (private 131 MB, …); the new process holds the shortcuts; the tray icon was handed over.", and in the new process "…
  starting (…, restarted silently after Settings)" and "Silent restart: the shortcuts were held during the switch; N
  press(es) caught."
- The README says so after its Settings list (and its Chinese translation).

**When** (`Core/Diagnostics/SilentRestart`: `SilentRestartRules`, pure, tested). Asked once a second (a
`DispatcherQueueTimer`, `AppController.SilentRestart.cs`) from the moment Settings closes:
- only after Settings was opened in this process (the language picker alone does not count);
- at least 5 s after Settings closed (`AfterSettingsClosed`: a quick reopen of a window closed by mistake does not restart);
- never while Settings, the language picker or the panel shows, while an approved update downloads or installs (its
  installer restarts the app itself), or while a command-line request is served (a `bclip wait` would lose its answer);
- not while a modifier key or a mouse button is held, and only after 2 s without keyboard or mouse input anywhere in the
  session (`QuietInput`, `GetLastInputInfo`): someone in the middle of something is left alone;
- **never when the new process would ask for a language** (no language chosen yet, §2.37): that would open a window.
- Opening Settings again stops the timer; closing it starts the count again.

**How, in order.**
1. **The old process starts the new one quietly** (`Windows/Shell/QuietProcess`: `CreateProcessW` with
   `STARTF_FORCEOFFFEEDBACK`, no inherited handles, the same environment, so an isolated instance comes back as itself):
   `BetterClipboard.exe --background --restart-after pid:startTicks --clipboard-sequence N`.
2. **The new process prepares the switch before anything else** (`Program.Main`, before COM and XAML): it keeps the
   instance's command events open (`SingleInstance.KeepCommandsAlive`: a `--show-flyout` sent during the switch is kept),
   then `RestartBridge.Start` adds its tray icon **hidden** and holds the shortcuts with a keyboard hook only
   (`HotkeyService.HoldWithHookAsync`: a hook installed later is called first, so the old process, still registered, no
   longer sees them), and sets the event `Local\<instance>.RestartReady`. Then it waits for the old process to end, or
   for `Local\<instance>.RestartCancel` (at most 2 minutes), and ends quietly when called off.
3. **The old process waits up to 3 s for "ready"** (on the pool, so a Win+V in that moment still opens the panel at
   once). If the new process ended meanwhile, it gives up for its lifetime (logged; no loop of starts). If the panel,
   Settings or the picker opened, an update began or a command-line request came in (`SilentRestartRules.CallOffReason`),
   it sets the cancel event and tries again once it is quiet ("Silent restart called off: the panel opened. Trying again
   once it is quiet."). Otherwise it hands the tray icon over (below), leaves later commands to the new process
   (`SingleInstance.StopListening`), and exits the usual way: queued captures drained, integrations stopped at their
   checkpoints.
4. **The new process becomes the app**: it takes the instance lock, starts as a background start (no Settings, no picker;
   the Windows import is skipped, the old process captured until a moment ago), starts the clipboard listener with the
   old process's last handled sequence number (`ClipboardMonitor.Start(uint?)`: a newer number is read at once, as if
   notified), registers its shortcuts, then lets the bridge go (`TakeOverShortcuts`) and opens the panel for a press the
   bridge caught meanwhile. It wires the bridge's tray icon and reveals it (a no-op when the old process did).

**The tray icon: handed over, never doubled** (`TrayIcon` `startHidden`, `Reveal`, `HandOverTo`) **[verified]**:
- An icon belongs to its window, and two processes cannot share one. The first version added the new icon visibly and
  removed the old one when "ready" came: an icon pinned to the taskbar then showed twice for one captured frame (~60 ms,
  the chevron jumping) — the user would have seen it.
- A GUID-identified icon cannot be moved either: `NIM_MODIFY` with the new process's window was accepted, but the icon
  vanished when the first window went (a frame capture shows it gone).
- **Built:** the new process adds its icon with `NIS_HIDDEN` (registered, shown nowhere). At the hand-over the old process
  finds the new one's icon window (`FindWindowByTitle` "BetterClipboard Tray" + its process id), reveals that icon
  (`NIM_MODIFY` with `NIF_STATE` on the other process's window: Explorer accepts it) and deletes its own, the two calls
  0.8 ms apart, on the tray thread. Captured every 24–34 ms with the icon pinned: no frame changed in two restarts (the
  only other frame was the clock turning a minute).
- `Shell_NotifyIconGetRect` answers S_OK for a shown icon and S_FALSE for a hidden one: what the tray sampler of the test
  used.
- When the hand-over cannot happen (no icon window found, Explorer refusing), the old icon goes and the new process
  reveals its own as its controller starts: a moment without an icon, logged.

**Facts found on the way** **[verified]** (test desktop, Windows 11 25H2):
- **The busy pointer:** the pointer probe (`GetCursorInfo` every ~20 ms) caught the app-starting pointer for a start with
  `STARTF_FORCEONFEEDBACK` from a foreground window, and none with `STARTF_FORCEOFFFEEDBACK`. A start with neither flag
  (what `Process.Start` does) showed none there, neither from a hidden script nor through Explorer — Windows decides by
  circumstances; the flag makes it certain.
- **The shortcuts during the switch:** a Win+V sent 0.6 s into the switch, after the old process had ended, was caught by
  the bridge ("1 press(es) caught") and opened the panel in the new process ("Panel on screen 1039 ms after the
  shortcut"). One sent 0.14 s in, before the bridge held, opened the panel in the old process within 64 ms and called the
  restart off; after Esc, the restart went through at the next quiet moment.
- **Copies during the switch:** a copy made 64 ms (and in a second run 414 ms) after the old process ended, before the
  new one listened, was recorded by the new process (hand-over of the sequence number; mutation-checked by a test).
- **Times** (VM, no GPU; five restarts): the decision 5.3–6.4 s after Settings closed; the new process ready 0.3 s after
  its start; the old process gone 0.2–0.3 s after the decision; the new process's shortcuts registered ~0.5 s after the
  old one ended. Once, with three capture loops busy on the VM's four cores: ready after 1.3 s, the old process gone 1.6 s
  after the decision.

**Pieces.** Core `Diagnostics/SilentRestart.cs`; Windows `Shell/QuietProcess.cs`, `Input/UserActivity.cs`,
`Interop/NativeMethods.Restart.cs`, `HotkeyService.HoldWithHookAsync`, `ClipboardMonitor.Start(uint?)` and
`LastHandledSequence`, `CliPipeServer.ActiveConnections`, `TrayIcon` (hidden start, reveal, hand-over); App
`AppController.SilentRestart.cs`, `RestartBridge.cs`, `Program.Main` (`WaitForSilentlyReplacedProcess`),
`SingleInstance` (`KeepCommandsAlive`, the two event names, `StopListening`), `StartupOptions` (`--clipboard-sequence`,
`IsSilentRestart`).

**Verified:** see §5.

**Limits / not built:**
- The switch takes about a second: a shortcut pressed in it opens the panel up to a second late (never lost).
- Without the keyboard-hook fallback (Settings › Shortcut) and for an isolated instance next to the installed app, the
  bridge holds no shortcut: a Win+V in that second does nothing (released from Explorer) or opens Windows' own panel.
- A new process that fails to start ends the attempts for the old process's lifetime (logged); the next start of the app
  tries again.
- Not run: the owner's PC (a real GPU), several monitors, a standard user account, ARM64. The language-picker and
  update-installing cases are unit-tested only.

### 2.47 The Microsoft Store edition: the same app as an MSIX package, a welcome with consent, and the move from another copy (built 2026-11-13; a filled draft in Partner Center, not submitted)

User request (2026-11-12): "I want to publish my product to microsoft marketplace". The full report is
[`docs/microsoft-store.md`](docs/microsoft-store.md) (where to publish, Microsoft's rules, the measurements, the design,
the package, what was verified, the owner's steps); the texts to paste into Partner Center are
[`docs/store-listing.md`](docs/store-listing.md). This section keeps what the code and later decisions need; the package
and the submission are §3.5.

**Where.** "Microsoft Marketplace" is Microsoft's storefront for cloud and AI offers. A Windows desktop app goes to the
**Microsoft Store**, as an MSIX package (Microsoft signs, hosts and updates it) or as an EXE/MSI installer (needs a
CA-issued certificate on the installer and every PE file, and a versioned link on our side). Releases are unsigned (§3.3)
and the installer is a PowerShell script, so the MSIX route is the one built. The developer account is free **[docs]**.

**The owner's decisions** (three HTML questions; the pages are in session 66cdd808's scratchpad, `q/`):
- **First start: "Welcome with consent (Recommended)"**, with the comment **"After language selection dialog"**.
- **The history of a copy from the installer: "Bring it over (Recommended)"**, with the note "What will we do if store
  edition is installing and we have a running any other edition? can store edition require uninstalling it?"
- **The follow-up, in their own words:** "Can't we uninstall it? But we migrate user's data successfully, validate and
  cleanup. We must validate all access permissions before starting (preflight checks)".
- Read as: only one BetterClipboard is meant to be installed. The Store edition removes a copy from the installer
  itself: it checks first that it can, brings the history over, proves the copy, uninstalls the other copy and removes
  the old folder.

**What a package changes** (measured 2026-11-12 with [`msix_lab`](tools/probes/msix_lab) in a test desktop; the whole
table is in the doc's §2). A Store package of a desktop app runs at medium integrity (`runFullTrust`), and still:
- **Its own `HKCU` writes and deletes go to a private hive.** They "succeed", and the real registry is unchanged. Reads
  see the real registry.
- **`RegNotifyChangeKeyValue` on a real key is never signalled** when another process writes. The key's last-write time
  does move, so a packaged process polls.
- **A new folder directly under `%LOCALAPPDATA%` is redirected** into the package. `…\Packages\<family>\LocalState` is
  written directly, and Windows removes it with the package. File change notifications for real folders arrive.
- **A child that is another program runs outside the package** (cmd, PowerShell: its registry and file writes are
  real). **A child that is the package's own program keeps the package identity**, also for a signed package installed
  in `C:\Program Files\WindowsApps`: the silent restart (§2.46) works unchanged.
- **DPAPI and `Local\` named objects are shared** with unpackaged processes: the moved history opens with the same key
  hierarchy (§2.6.1), and one instance lock serves every edition.
- **`StartupTask.RequestEnableAsync()` from a desktop package** answers `Enabled` at once, without a prompt.

**What is different in this edition.** A process is the Store edition when it has a package identity
(`PackageIdentity.IsPackaged`, one kernel32 call) and no data-folder override; nothing else is compiled differently.

| | Installer and Chocolatey editions | Microsoft Store edition |
|---|---|---|
| Data | `%LOCALAPPDATA%\BetterClipboard` | `%LOCALAPPDATA%\Packages\<family>\LocalState\BetterClipboard` (`AppPaths.PackagedDataDirectory`), removed by Windows with the app |
| Win+V | Released from Explorer (`DisabledHotkeys`), or the hook | Only the keyboard hook, only after the welcome's switch. No *Release from Explorer* card |
| Start with Windows | A Run value | The package's startup task `BetterClipboardStartup` (`Shell/PackagedStartup`); Windows' *Startup apps* list can overrule it |
| Updates | The update service asks GitHub | The Store. No update service, **no network request at all**; `PackagedRestart.RegisterAfterUpdate` asks Windows to start the app again after an update |
| `bclip` | A folder on the PATH (optional) | The package's one app execution alias, `bclip.exe`. The app itself has none ("The Store's upload check", below) |
| Win+R history | A registry notification | `RunMruWatcher(pollOnly: true)`: the key's last-write time every 5 s |
| Windows' own clipboard history | A switch (a registry write) | A button that opens Windows' Settings page |
| Version shown by Windows | The release's | The package's, (X+1).Y.Z.0 (§3.5) |

- **One place resolves the data folder:** `Program.ResolvePaths()` (`AppPaths.ResolveDefault(PackageIdentity.FamilyName)`).
  `bclip` links `PackageIdentity.cs` and resolves the same folder.
- **The instance lock is shared with every edition** (`Local\<instance>.Instance`), so two editions never run at once. The
  Store edition marks the lock while it holds it (`Local\<instance>.StoreEdition`, `SingleInstance.PrimaryIsStoreEdition`).
- **Settings:** `SettingsWindow.ApplyEdition` takes the cards that are not true for the edition **out of the page**
  (*Release from Explorer* and *Updates* there; *Updates from the Microsoft Store* everywhere else). The version line
  reads "Version … · Microsoft Store edition"; the PATH button is a sentence about the alias; the startup switch drives
  the task and shows *Open Startup apps* while Windows has it off (`SettingsViewModel.Store.cs`).
- **An isolated instance** (`BETTERCLIPBOARD_DATA_DIR`) of a packaged build is never "the Store edition" for the setup:
  a test copy must not find, close or uninstall the user's installed app.

**The first start** (`AppController.Store.cs`: `NeedsStoreSetup`, `RunStoreSetupAsync`; one window, `StoreSetupWindow`,
whose pages are `StoreSetupView` + `StoreSetupViewModel`):

```text
Start by hand
  └─ other copies and their data, one step at a time until nothing is left   (below)
       └─ language picker, when no language was ever chosen (§2.37)
            └─ welcome: what is kept, "Open with Win+V" (on), "Start with Windows" (on), Privacy, Not now | Start
                 └─ Start: AppSettings.StoreWelcomeAccepted, the choices applied, and only now StartCore
                    (the store, the clipboard listener, the shortcut, the tray icon)
```

- **Nothing before Start** (policies 10.2.8 and 10.5.5): until then the data folder holds a log and at most the
  settings file. *Not now* and closing the window end the app; the welcome shows again at the next start by hand.
- **The switches.** *Open with Win+V* off: Alt+Win+V opens the panel and Win+V stays Windows'. A user who came from
  another copy keeps their shortcuts; the switch only adds or removes Win+V. *Start with Windows* shows the task's real
  state (`PackagedStartupState`: also "off in Windows' list" and "by policy").
- **Language and theme of the setup:** this edition's saved ones, else those of the copy it replaces, else Windows'
  (`ApplySetupLook`). The setup runs before `StartCore`, so nothing in it may ask `Settings`.
- **Only on a start by hand.** A start by Windows (the startup task, which passes no arguments:
  `PackagedStartup.WasStartedByTask`; or again after a Store update) or by `bclip` never opens a window.
  `Program.ContinueStoreEditionInBackground` ends such a start **before the instance lock** while the welcome was not
  accepted or another copy is installed: a Store edition holding the lock would turn the other copy's own sign-in start
  away, and then none would run. `AppController.Start` gives the same answer for what that check cannot see (a copy
  that merely runs, a history to bring over). A silent restart skips the setup too. Each case logs one line.

**Another copy on the PC** (`Windows/Store/OtherEditionLocator`: the Installed-apps records of the user and of the PC,
Chocolatey's folder shape, and a copy that merely holds the lock; `Core/Editions/EditionSetupPlan.Next`, pure and
tested, picks one step, and the setup asks again after each):

| Found | Step (`EditionSetupStep`) | What happens |
|---|---|---|
| A copy this user installed with the installer | `MoveFromInstaller` | A page with the checks; on *Move to this edition* the move |
| A copy for every user, or the Chocolatey copy | `GuideUninstall` | The page says how an administrator removes it (*Open Installed apps*, or `choco uninstall betterclipboard` to copy) and waits until it is gone |
| A running copy without an install record | `CloseRunning` | Asked to exit, on the user's go-ahead; never ended by force |
| No copy, but a history one left behind, and this edition has none | `ImportData` | Copied, checked, old folder removed, without a question |

- **The checks before a move** change nothing: (1) the history can be read and the drive has room
  (`EditionDataMigrator.EnsureReadable`, `RequiredFreeBytes`: the data, a tenth more, 64 MB); (2) this edition's folder
  can be written; (3) the other copy can be uninstalled from here (`EditionUninstaller.CheckAsync`: the script is in the
  package, Windows PowerShell exists, a script file really runs, and a probe script — run outside the package, where the
  uninstaller will run — opens the Run key, the copy's Installed-apps entry, Explorer's settings and the environment key
  for writing, writes and deletes a file in the copy's folder and opens its shortcut); (4) the old data folder can be
  removed. A failed check names what failed in words (`DescribeDenied`) and offers *Check again*.
- **The move** (`RunMoveAsync`): close the other copy and take the lock → copy `settings.json` and everything below
  `stores` with a checksum per file (`EditionDataMigrator.CopyAsync`; **the files are listed right before each copy
  attempt**, since closing the copy removes its database's write-ahead log) → open the copy with this account's key
  and run SQLite's own check (`Validate`, `ClipStore.QuickCheck`; the app goes on with this very store) → uninstall the
  other copy (`EditionUninstaller.UninstallAsync`, proven by its program and its entry being gone) → remove the old
  data folder, and only now (`RemoveSource`).
- **All or nothing.** The copy is marked (`edition-move.pending`) until the last step begins. A move that stops earlier
  (the user quits, the uninstall fails, power is lost) is rolled back at once or at the next start
  (`EditionDataMigrator.Rollback`), and quitting starts the closed copy again. A second launch during the setup brings
  the same window forward (`SingleInstance.TryClaimStoreSetup`: the mutex `Local\<instance>.StoreSetup` and the event
  `…StoreSetupShow`; the lock still belongs to the other copy then).
- **Two histories are never merged.** An edition that already has a history uninstalls the other copy and leaves that
  copy's history where it is; the page says so and shows no history lines.
- **The uninstall** runs the `install.ps1` **this package carries**, with Windows PowerShell, outside the package:
  `-Uninstall -InstallDir <folder> -KeepProcessId <this process> -ResultPath <file> -LogPath <file>`. `-KeepProcessId`
  (new) keeps the uninstall from closing the Store edition itself; the two report parameters are now valid in the
  uninstall's parameter set, and the uninstall writes its result file. Not the installed copy's own script: older ones
  close every BetterClipboard of the session, and those up to 0.2.5 can empty Explorer's settings key (§2.25).
- **The installer says it too:** `install.ps1` warns before it installs when the Store edition's data folder exists for
  the account (`Get-StoreEditionData`), and says what the Store edition will then do.

**Pieces.**
- Core: `Editions/` (`OtherEdition`, `OtherEditionKind`, `EditionSetupStep`, `EditionSetupPlan`, `EditionData`,
  `EditionDataMigrator`), `AppPaths` (`ResolveDefault(packageFamilyName)`, `PackagedDataDirectory`, `HomeDirectory`),
  `AppSettings.StoreWelcomeAccepted`, `ClipStore.QuickCheck`, `UpdateSource.ChocolateyUninstallCommand`.
- Windows: `Packaging/PackageIdentity`, `Packaging/PackagedRestart`, `Shell/PackagedStartup`, `Store/OtherEditionLocator`,
  `Store/EditionUninstaller`, `Interop/NativeMethods.Packaging.cs`, `RunMruWatcher`'s `pollOnly`.
- App: `AppController.Store.cs`, `StoreSetupWindow`, `StoreSetupView.xaml(.cs)`, `StoreSetupViewModel`,
  `SettingsWindow.Store.cs`, `SettingsViewModel.Store.cs`, `Program` (`ResolvePaths`,
  `ContinueStoreEditionInBackground`), `SingleInstance` (the marker, the setup claim, `TryBecomePrimary`, `StepDown`),
  `AppController.Start` / `StartCore`, the App project's `WindowsPackageType=MSIX` part.
- Strings: the setup's and Settings' texts in all five catalogs, each with its description.

**Faults the live runs found, fixed:**
- the setup crashed on the theme (the settings are not loaded yet at that point);
- it ignored the replaced copy's language: `SettingsStore.TryReadSnapshot` returns the defaults for a missing file, so
  "this edition has no settings" was never seen;
- the copy step used a file list made while the other copy ran ("Could not find file …history.db-wal");
- PowerShell refused the uninstall's command line before the script's first line ("ended with code 1", no log, no
  result): the two report parameters belonged to the install parameter set only;
- a failed check named its finding with an internal word, and the window was too short for four lines and an error;
- the first page promised to bring a history over when this edition already had one;
- a background start with a setup due would have opened a window at sign-in and held the lock.

**The Store's upload check, and what it changed (2026-11-13).** The owner registered as a developer, and the first
packages went into a draft submission (§3.5). Partner Center refused both at upload: "The package file … specifies a
headless app. You don't have permission to create a headless app. Please update AppListEntry="none" in the
AppxManifest file".
- **The cause:** `bclip` was a second `Application`, hidden from Start. The Store allows that only with a waiver it
  grants on request. MakeAppx had packed the manifest and Windows had installed it: only the Store objects.
- **The second rule:** an application may declare one `windows.appExecutionAlias` extension (MakeAppx: "must only be
  declared once", also for one `uap3` next to one `uap5`). All aliases of an extension start that extension's one
  `Executable`, which may differ from the application's.
- **So the package is one application** whose one alias is the console alias `bclip.exe`, started from `bclip.exe`.
  The app's own alias `BetterClipboard.exe` is gone; nothing in the app used it. A script starts the Store edition
  with `explorer.exe shell:AppsFolder\<family>!App`.
- **The build checks it** (`package-msix.ps1` › `Get-ManifestStoreProblems`): one application, none hidden, the one
  alias `bclip.exe` with `Subsystem="console"`. The refused manifest fails each check; the rebuilt package passes.
- **Verified** with the test-signed twin in two test desktops (rows in §5): it installs; Start has one entry; `bclip`
  runs by name in a console, with its output and exit codes; with the app stopped, `bclip status` starts it from the
  `WindowsApps` folder and answers from the Store edition's data; silent restarts work. The Store then accepted both
  rebuilt packages ("Validated").

**Verified:** see §5.

**Limits / not built:**
- **Not submitted yet.** The name is reserved and the first submission is a filled draft. One tick, the version rule
  and *Submit for certification* are the owner's (§3.5).
- A copy for every user and a Chocolatey copy are not removed by the Store edition; their two pages were not seen on
  screen (the plan is unit-tested).
- The move needs Windows PowerShell and script files that run; where a policy blocks them, the check says so.
- Not run: a package served by the Store (its signature, an update, the restart after one), the Windows App
  Certification Kit, a standard user account, UAC on, a real GPU, ARM64 on a device, the Spanish and Chinese setup pages.
- The listing's screenshots are not made (§3.5).

### 2.48 A portable copy updates itself; a newer copy takes over; the sign-in entry follows; a tray icon Explorer added late (built 2026-11-13)

User request (2026-11-13): "try in a win desktop to download a portable older version, and check for update and see that
update worked cleanly on the portable". That QA (§5) found two traps on a portable copy's *Download page* way and a dead
tray icon. Asked with one HTML question (pages in session 4886d50a's scratchpad, `q/`), the owner chose **"Portable updates
itself"**, **"Newer copy takes over (Recommended)"** and **"Sign-in entry follows (Recommended)"** (not "Clearer portable
dialog"), and for the tray icon **"Fix it now (Recommended)"**.

**What the user gets.**
- **A copy extracted from the release zip updates itself in its own folder** (its own *Update and restart*, the dialog's
  words: "Updating downloads it from GitHub, checks it, puts it into this copy's folder and restarts BetterClipboard. Your
  history is kept."): when this user may write the folder and the offer is 0.2.11 or later. Otherwise the dialog keeps
  the download page and the install command (Program Files without administrator rights, read-only media, and every offer
  older than 0.2.11, whose program does not know the helper).
- **Only the release's files are touched.** Each is renamed into a backup folder inside the portable folder, then the new
  one is copied in; files the release does not ship stay (the user's own, and files an older release had and this one no
  longer ships: no list of them exists). All or nothing: a file that cannot be replaced puts every file back.
- **A newer copy takes over:** a plain start of a newer BetterClipboard while an older one runs asks the older one to exit
  and becomes BetterClipboard (its Settings come up), instead of opening the older copy's Settings.
- **"Start with Windows" follows the copy that runs:** at each start, the user's own entry is rewritten for this copy when
  it starts a BetterClipboard that is gone or older. The entry for every user is never written, and Windows' "off" mark
  for the user's entry stays (an entry turned off stays off).
- **A tray icon Explorer added late now works:** a refused `NIM_ADD` is followed by a `NIM_MODIFY` that asks whether the
  icon is there anyway; a real refusal is tried again on a timer.

**The portable update, step by step** (`Core/Updates/PortableUpdate`, `Windows/Updates/PortableUpdateInstaller`,
`Windows/Updates/PortableUpdateCommand`).
1. The update service downloads and verifies the package as for an installed copy (§2.29), then
   `PortableUpdateInstaller.StartAsync` unpacks it into `updates\portable-<version>` (a refused zip, or one without
   `BetterClipboard.exe`, leaves nothing) and starts the **new** version's exe from there:
   `BetterClipboard.exe --apply-portable-update --target <folder> --wait-for <pid>:<start ticks> --result <updates\result.json> --log <logs\betterclipboard-update.log>`.
   Started directly (CreateProcess), with the app's environment (an isolated instance stays isolated), in the temp folder.
2. `Program.Main` recognizes the switch before anything else (`ApplyPortableUpdate`): no lock, no XAML, no store.
   `PortableUpdateHelper.Run`: a folder without `BetterClipboard.exe` is refused untouched (the old copy is not even asked
   to exit); the old copy is asked to exit through its instance's `Exit` event (`SingleInstance.TrySignal`), waited for
   30 s, then ended (10 s more).
3. Files are replaced in name order with the program last; each held file is retried for 10 s. The backup folder
   (`.betterclipboard-update-backup-<UTC stamp>`, hidden) is removed at the end; files another program still holds (a DLL
   loaded through the PATH, §2.9) stay there until `PortableUpdateCommand.CleanLeftovers` removes them at a later start.
4. The result goes into `result.json` in `install.ps1`'s shape (`UpdateResult.Write`), and BetterClipboard is started
   again from the same path with `--background` (`QuietProcess`): the new version, or the previous one after a rollback.
   The new start reads the result ("The update to X was installed.") and `UpdateService.Start` removes the staging folder.
- **Exit codes:** 0 updated, 2 bad arguments, 3 not a BetterClipboard folder, 4 the old copy did not close, 5 rolled
  back, 6 rollback incomplete. The seven failure sentences are `UpdateMessages` templates, translated in the dialog and the
  notification (`ReportLastUpdate` now runs the result's error through `UiText.TranslateFormatted`).
- **The helper's command line is a contract** (§4), like `install.ps1 -Update`'s.
- **`PortableUpdate.MinimumVersion` = 0.2.11** must name the first release that ships the helper. If 0.2.11 is skipped,
  raise it; an offer below it would start as a plain launch from the staging folder.

**The take-over** (`Core/Startup/InstanceTakeover`, `Windows/Shell/RunningCopies`, `Program.TakeOverFromOlderCopy`).
- Only for a plain start (`Activate`, not `--background`) of a non-Store copy that finds the lock taken.
- **Conservative:** it takes over only when every other `BetterClipboard.exe` of the session runs (a leftover process
  object without threads does not count, §4), reports a version strictly older than this one, runs from another path and
  is not packaged. Anything unclear keeps the hand-over.
- It signals the instance's `Exit` (every version understands it), polls `TryBecomePrimary` for up to 2 minutes, then
  `DiscardPendingCommands`. A copy that does not exit in time is left as it is: the request is withdrawn and the launch
  handed over as before. The controller logs it from `StartupOptions.TookOverFrom` ("… starting (…, took over from
  BetterClipboard 0.2.11 (process 4948), which was asked to exit)"), because Program runs before the log exists.

**The sign-in entry** (`SignInStartup.FollowThisCopy`, `AppController.SignIn.cs`, `Windows/Shell/ProgramVersion`).
- At every start but a silent restart, on the UI thread before any window (Settings then shows the switch as it now is):
  not for the Store edition, not for an isolated instance.
- Rewritten only when the user's own entry starts a program named `BetterClipboard.exe` that no longer exists or whose
  product version is older; the same or a newer version elsewhere, an unreadable version and another program are left
  alone. Logged: "Start with Windows now starts this copy (0.2.12): its entry started an older BetterClipboard (0.2.11)
  from another folder."

**The tray icon** (`Windows/Shell/TrayIcon`).
- `Shell_NotifyIcon` goes through an injectable delegate (an internal constructor), so tests play Explorer.
- A refused `NIM_ADD` is followed by `NIM_MODIFY` (the tooltip again): when it succeeds, the icon is there, `added` is set
  and `NIM_SETVERSION` sent ("Shell_NotifyIcon(NIM_ADD) reported a failure, but the icon is there (Explorer answered
  late); it is set up now."). Otherwise a timer (id `0xB0C1`) tries again every 2 s for 30 tries, then every 30 s, besides
  `TaskbarCreated`; one warning per outage, and an info line when it is in place.

**Verified:** see §5 (unit tests with two mutations caught; every case live in a Windows 11 desktop, a real sign-in after
a reboot included). The tray's refusal itself could not be provoked live: Windows 11 waited for a suspended Explorer.

**Limits / not built:**
- Files an older release had and the new one no longer ships stay in the folder (harmless for a self-contained app: the
  host loads what its deps file names). A file list in the release would let the next helper remove them.
- The first release with the helper is the first that can be updated in place to a *later* one: a 0.2.10 or older copy
  still sees the download page, and needs one manual step (extract 0.2.11 over it, or run the install command).
- A copy that runs as administrator, a standard user, ARM64 and a folder on a network share were not run.
- The take-over does not apply to `--background` starts (two sign-in entries naming two copies: the first wins, as before).

### 2.49 Pictures keep their files: copy the path or the file, open it or its folder, *Save to temp and*, the file in the viewer (built 2026-11-13)

User request (2026-11-13): "Images are usually coming from either clipboard or from sources like ShareX saves all to
files at C:\Users\ELI\Documents\ShareX\Screenshots\2026-11 / So any images we detect and has a path - we should keep the
path persisted with the image / And then right click has copy path which copies original path (only shown if file exists
on disk). also has "Copy image as a file" / also have Open file, Open folder which rely on the original path existing /
And Save to temp and -> Copy path, copy image as a file, Open file, Open folder". Three HTML questions (pages in session
2fc2c47a's scratchpad, `q/`); the owner chose:
- **Screenshots stored before:** "Find their files once (Recommended)", with the comment "As a migration" and the note
  "Also keep the file's metadata, dates, size, ext, format and so on".
- **Temporary pictures:** an answer of their own, "Window's on restart delete temp after a month + restart
  (configurable)": deleted when BetterClipboard starts, once a month old, the number of days a setting.
- **Where the file shows:** "Both" (the menu and the viewer), with "At viewer: but must fit to the left of the image or
  above it cleanly, less rows. and path and file are clickable".

**What the user gets.**
- **A picture read from a file keeps that file:** ShareX's screenshots (§2.10) and the Screenshots folder's (Snipping
  Tool, Win+PrtScn, §2.22) remember the file's path, size, creation and last-change times, and its format as its bytes
  say ("PNG", "JPEG", …), in the encrypted history. A clipboard copy of the same picture stored first gets the file when
  the file arrives: the two are one entry by the pixel hash (§2.22).
- **The card menu of a picture whose file is known**, after *Pin*: a dimmed line naming the file, then its format, size
  and last change ("PNG · 5.1 KB · modified 5 min ago"), or "moved or deleted". While the file is on disk, four items
  follow: *Copy path* (Segoe Fluent Icons E62F), *Copy image as a file* (E8A5), *Open file* (E8E5) and *Open folder*
  (E838, File Explorer with the file selected).
- **Every picture**, one copied from an app too, gets *Save to temp and ›* (E78C) with the same four items. They act on
  a PNG written from the history into `%TEMP%\BetterClipboard\Images` first.
- **The image viewer** (§2.24) shows the file on two rows above the picture, on a dark band: the name (a link that opens
  the file) · format · size · pixel size, then the folder (a link: File Explorer with the file selected) · its dates.
  With the file gone, the name and folder are plain text and the first row ends "moved or deleted".
- **Settings › History › Keep temporary pictures for (days):** 0 to 3650, 30 by default; 0 deletes them at every start.
- **`bclip list --json`** gives a picture's `file`: `path`, `name`, `extension`, `format`, `sizeBytes`, `created`,
  `modified`.
- **`install.ps1 -Uninstall -RemoveData`** deletes `%TEMP%\BetterClipboard` too (`Remove-TemporaryPictures`), best
  effort: a picture another program holds open stays, and the note says where.

**Storage** (`Core/Model/SourceFile`, `Storage/ClipStore.SourceFiles.cs`).
- `clips.file_path`, `file_size`, `file_created_utc`, `file_modified_utc`, `file_format`: added idempotently like the
  groups' column (an older build opens the file, ignores the columns, and a row it bumps keeps them). `NULL` path = no
  file known. The five are always written together: a path never carries another file's dates.
- `SourceFile.FromDisk` reads the details right after the bytes were read; a time before 1980 (Windows' "no time kept")
  is left out. The format comes from the first bytes (`Content/ImageFileFormat.Detect`: PNG, JPEG, GIF, TIFF, JPEG XR,
  WebP, AVIF, HEIF, BMP, ICO), so a file named after the wrong format still says what it is.
- **Merge rules** (`UpsertCore`): a live duplicate with a file replaces the row's file whole; one without a file keeps
  the row's (`coalesce`). An import (the Windows import, the catch-ups) fills a missing file only, and never reorders.
- The capture carries the file (`ClipCapture.File`): `ShareXScreenshotWatcher` and `WindowsScreenshotWatcher` describe
  the file they read. Clipboard copies carry none: the clipboard does not say where a picture came from.

**The one-time search for earlier screenshots' files** (`Windows/Integrations/ScreenshotFileFinder`; the owner's "as a
migration").
- Per tab, once: ShareX (`state.sharex.files_searched`) and Snipping (`state.screenshots.files_searched`), 30 s after
  that integration starts watching (`FileSearchDelay`, so the start's own work goes first), and only when the store does
  not mark it done. An exit halfway is not marked, so the next start goes on; the search is idempotent anyway.
- **Candidates:** the tab's pictures without a file (`ClipStore.FindPicturesWithoutFile`: kind Image, the tab's own
  filter). **Files:** ShareX's watched folders recursively, through the same pattern rules as its catch-up; the
  Screenshots folder's image files.
- **By time first, then pixels:** only files written within 2 minutes of an entry's first-stored time are read, closest
  first (a screenshot's entry is stored when its file is written, or when the tool copied it, seconds apart). A file
  matches when its content hash (`ImageCodec.ContentHashAsync`: the analyzer's pixel hash) is the entry's. A file is
  given to one entry at most, and each is read once. Never read: cloud placeholders and files over 256 MB.
- `ClipStore.SetSourceFile` fills only what is missing, so the search can never replace a newer file, also when it races
  a live import. Logged with counts only: "Earlier ShareX screenshots: found the files of 2 of 2 in 124 ms (searched
  once; new ones keep their file as they are saved)."

**Copying and opening** (`App/AppController.ImageFiles.cs`, `Views/ClipboardFlyout.ImageFiles.cs`).
- **Is the file there?** `Windows/Shell/FileOnDisk.Check` answers OnDisk, Missing or Unknown. It looks only on local
  fixed, removable and RAM drives; a network path (UNC, a mapped drive) or an optical drive is never touched (a dead
  share would block the menu) and counts as there. Asked when the menu or the viewer opens, and again when an item is
  chosen: a file gone meanwhile keeps the panel up with "The file was moved or deleted." in the footer.
- **Copy path / Copy image as a file** write through the clipboard monitor (`ReplayFormats.ForFiles`: text, or
  `CF_HDROP` with the drop effect "copy", like Explorer's Copy), so they are BetterClipboard's own writes and are not
  recorded as copies. Like *Copy only*, they count as using the picture (`MoveToTopOnPaste`), and the panel goes away
  with the focus handed back.
- **Open file / Open folder** start the file's app, or `explorer.exe /select,"<path>"`, while the panel still holds the
  foreground, and the panel hides after (the Run tab's order, §2.17). Failures: "Could not open it: …" in the footer.
- Logs never name a path or a file.

**Temporary pictures** (`Core/Content/TempImageFiles`).
- **Where:** `%TEMP%\BetterClipboard\Images\<the first 12 hex digits of the content hash>\` (an isolated instance's
  under `%TEMP%\BetterClipboard\<scope>\Images`, so its clean-up never deletes the regular instance's). The name is the
  original file's with `.png`, else `Image yyyy-MM-dd HHmmss.png` at the copy's local time: the name travels with the
  file into a chat or a mail. `%TEMP%` is real also for the Microsoft Store edition (`docs/microsoft-store.md` §2).
- **Saving again** the same picture finds the earlier file (equal bytes) and touches it; a differing file of that name
  (edited since in an image editor) is never overwritten: the next save is `name (2).png`. Written as `.part`, then
  moved.
- **Plain files, deliberately:** they exist because the user asked for a file. The README's *Privacy* says so.
- **Clean-up** (`CleanTemporaryPictures`): at a start, never at a silent restart (§2.46) and never while running (a
  picture just pasted as a file must not vanish under the app that reads it). It deletes pictures whose last write is
  older than `AppSettings.TempImageDays` (all of them at 0), `.part` leftovers an hour old, and empty folders; a file
  another program holds stays for a later start. On the pool; logged with a count.

**The viewer band** (`BuildFileDetailsBand`, `ShowViewerAsync`).
- Two rows of 12.5-DIP text on a dark rounded band, centered 16 DIPs below the screen's top, at most the screen's width
  less 72 DIPs a side (clear of the close button). The name and folder take at most 60 % of the band and trim at their
  end, so the facts after them stay readable.
- The band is measured before the picture is laid out, and its height plus 16 DIPs is kept free above the scroll host
  (`viewerTopReserve`): the picture fits below it, and zooming never puts the picture on it.
- Links are a light blue (#9FD8FF) that reads on the dark band in either theme; a tap on the band does not close the
  viewer.

**A crash found in the QA, fixed before landing.** A click on the viewer's file-name link ended BetterClipboard: 2 of 2
times, exception 0xC000027B in `Microsoft.UI.Xaml.dll` (a WinUI fail-fast, so `Application.UnhandledException` never saw
it and the log has no line).
- A WER dump (§4: how to get one in a test desktop) showed the UI thread in `Interop+Shell32.ShellExecuteExW`, under
  `Process.Start`, under `AppController.OpenImageFile`, under the Hyperlink's Click handler.
- The cause: `ShellExecuteEx` runs a message loop of its own while Windows activates a packaged app such as Photos, and
  on the UI thread WinUI re-entered from inside that click, whose popup (the viewer) had just been closed.
- The folder link (`explorer.exe`, plain process creation) and the menu's *Open file* (no popup closed in the same
  click) had not crashed.
- **The fix:** `LaunchAsync` runs the start on the thread pool and awaits it; .NET then calls `ShellExecuteEx` on an STA
  thread of its own. Afterwards 0 crashes in three link clicks (Photos running, and started cold) and the menu's *Open
  file*.
- The convention is in §4; the app's older starts on the UI thread are in §6.

**Pieces.**
- Core: `Model/SourceFile`, `ClipCapture.File`, `ClipEntry.File`; `Content/ImageFileFormat`, `Content/TempImageFiles`;
  `Storage/ClipStore.SourceFiles.cs` (`SetSourceFile`, `FindPicturesWithoutFile`, `PictureWithoutFile`);
  `Services/ClipHistoryService.SourceFiles.cs` (`GetPicturesWithoutFileAsync`, `SetSourceFilesAsync`: one `Updated`
  event per recorded entry); `Presentation/SourceFileText` (the menu's label, the viewer's facts and dates) and
  `Presentation/ByteSizeText` (sizes like Explorer, shared with Settings); `AppSettings.TempImageDays`; `CliItem.File`
  (`CliFile`).
- Windows: `Shell/FileOnDisk`, `Integrations/ScreenshotFileFinder`, `ImageCodec.ContentHashAsync`; the two watchers and
  integrations (`FilesSearchedStateName`, `FileSearchDelay`, `StartFileSearch`, `ListScreenshotFiles`).
- App: `AppController.ImageFiles.cs`, `Views/ClipboardFlyout.ImageFiles.cs`, `ShowItemMenu`'s call to
  `AddImageFileItems`, the viewer's band in `ClipboardFlyout.ImagePreview.cs`, the Settings card and
  `SettingsViewModel.TempImageDays`.
- `install.ps1`: `Remove-TemporaryPictures`. Strings: 15 entries in all five catalogs, each with its description.

**Verified:** see §5.

**Limits / not built:**
- The path is a snapshot: a file renamed or moved afterwards is "moved or deleted", not followed.
- Only screenshots read from their folders have a file. A picture copied from an app (a browser's *Copy image*, Paint)
  has none, and a picture file copied in Explorer is a Files entry, not a picture. Paths in a copy's HTML (`<img src>`)
  are not used.
- Not run: the Microsoft Store edition with this feature, a file on a network share, a standard user account.

### 2.50 Long texts: the dotted line and the eye, the hover peek, and the text viewer (built 2026-11-13)

User request (2026-11-13), about a Codex-tab card whose four lines showed only the start of a long XML prompt: "we need
similar functionality to an image where we get markdown rendered and highlighter with autodetect code language so that
this xml would've been formatted and we can see the entire content if mouse pauses hover and we click the eye". Four
HTML questions (pages in session c073d86d's scratchpad, `q/`); the owner chose:
- **Which cards: "Only cut-off cards"**, with "But via right-click we can open via 'View'" and "Also add a thin 5th line
  with three dots impying there is more".
- **The peek: "Centered, wheel scrolls"**, with "Appear Centered to the app" and "And support very long texts and
  selection and right-click menu buttons".
- **Formatting: "Formatted + "As copied" switch"**.
- **The viewer's extras:** *Wrap switch*, *Line numbers*, *Language picker* ("making markdown default and as fallback to
  undetected") and *Text size* ("persisted"), and in their own words "Also be capable of handling large (megabytes)
  copies". *Find (Ctrl+F)* was not chosen.
- The prompt the owner pasted holds their own data: it is in no test, mockup or document. The tests and the QA use an
  invented BC-TEST prompt of the same shape.

**What the user gets.**
- **A card that shows less than its text says so:** a thin "⋯" line under its four lines and the eye (E7B3, as on
  picture cards) at its corner. `ClipItemViewModel.IsCutOff`: the card's text block reported trimming, or the stored
  preview is itself cut (`ContentClassifier.PreviewMaxChars`), or a file list's preview says "N more". Other cards look
  as before.
- **The peek:** the mouse resting 450 ms on a cut-off card's text opens the whole text centered on the panel, at most
  900 DIPs wide and 80 % of the work area, drawn like the viewer.
  - The mouse's first real move closes it, wherever the mouse goes, and the wheel scrolls it from anywhere without
    closing it (the owner's follow-up, below). A right-click with the mouse still shows *Copy* and *Select all* above
    the card's own menu (whose *View* opens the viewer), and the peek stays while that menu shows.
  - Esc and a click outside the frame close it too.
  - It shows at most one page, 64,000 characters, with "Showing the first N of M characters. F3 opens all of it."
- **The viewer** (the eye, F3, or *View* in a text card's menu): the screen dims, and the text opens in a card of 90 %
  of the work area (at most 1,400 DIPs wide) centered on the panel.
  - A header (the card's kind and caption), a toolbar, the text, and a status line: "N lines · N characters", plus a
    note when the text is limited.
  - *Formatted* / *As copied*, only when they differ (Markdown, XML, JSON).
  - *Wrap*, and *Line numbers* in a gutter left of the text (code only).
  - The language button: every language (`TextLanguages.All`: Markdown and plain text first), the recognized one marked
    "(detected)".
  - A− / A+ (Ctrl+Minus, Ctrl+Plus, Ctrl+0, Ctrl+wheel), 50 to 300 %.
  - A code text over 64,000 characters: ‹ Page N of M › (Ctrl+PageUp, Ctrl+PageDown); the line numbers count on.
  - Wrap, line numbers and the size are remembered (`AppSettings.TextViewerWrap`, `TextViewerLineNumbers`,
    `TextViewerTextSize`). Esc, the X, or a click on the dim area closes it.
- **The whole text** (`AppController.TextView.cs`): a stored entry's `CF_UNICODETEXT`, else its HTML fragment, else its
  RTF source; a file list's paths, one per line; an archived prompt's full text. Read on the pool, one format only.
- **Copying** from the peek or the viewer puts plain text on the clipboard, and the history records it like any copy.

**How a text is drawn** (`Core/TextView`, pure and unit-tested).
- **`TextLanguageDetector`, precision over recall.** Structure first (JSON that parses, a leading tag, a diff's headers,
  a shebang, Markdown code fences), then each of the first 200 lines of the first 64 K characters is scored per language
  against prose and Markdown. A language wins with at least 3 points, more than the prose and Markdown signals together,
  on at least 30 % of the lines (half of a text of one or two lines). Anything else is Markdown, the owner's fallback.
  Each pattern runs with a 50 ms timeout.
- **`SyntaxHighlighter`:** one small lexer per family (C-like, scripting, markup, data, diff) for the 22 languages of
  `TextLanguage` besides Markdown and plain text: XML, HTML, JSON, YAML, INI and TOML, C#, Java, Kotlin, JavaScript,
  TypeScript, Python, Ruby, PHP, Go, Rust, C and C++, PowerShell, shell scripts, batch files, SQL, CSS, diffs. One pass,
  left to right; the first 2,000,000 characters are colored (`DefaultMaxChars`), the rest is in the default color.
- **`StructuredTextFormatter`:** re-indents XML and JSON, and every token keeps its characters (entities, escapes, the
  spelling of numbers). An element with text in it, such as an agent prompt's `<instructions>`, stays exactly as copied.
  DTDs are ignored and nothing is resolved; at most 4,000,000 characters; invalid input is "not formattable".
- **`RtfWriter` and `SyntaxPalette`:** an RTF document per page, VS Code's Dark+ and Light+ colors for tokens and
  Windows' text colors for prose. Markdown is drawn as a document; a fenced block is colored in its fence's language
  (`TextLanguages.FromFenceInfo`). Links are drawn, never live. One character of text is one character of RichEdit, so
  the gutter maps positions to lines.
- **`TextViewSource`** normalizes line breaks to `\n` and cuts the text to the caller's limit at a line break;
  **`TextViewRendering`** is one view of it (a language, formatted or not), with its tokens or blocks and its `Pages`.
- **`Presentation/Markdown`:** `MarkdownParseOptions` (`KeepLineBreaks`, since a prompt's line breaks are meant, and a
  larger `MaxLength`), and the fence's info string on `MarkdownCodeBlock.Info`.

**Megabyte texts: why pages** (measured 2026-11-13).
- **The Core work is cheap:** detection, preparation and the RTF of a 6 MB log and of 2.3 MB of C# each took under
  100 ms on this PC (session c073d86d's scratchpad, `bench/bench_textview.cs`).
- **RichEdit is not.** In the QA desktop (no GPU) it took about 2.5 µs per character as plain text and 7 µs as RTF, on
  the UI thread. The 6 MB log as one plain document: 15.4 s (12.0 s to set, 3.3 s to lay out), about 30 s with a format
  pass over it; 2.3 MB of C# as RTF: 15.9 s.
- **So RichEdit gets one page.** A code document is cut into pages of at most 64,000 characters at line breaks
  (`TextViewRendering.Pages`; a longer line is cut at the limit, never inside a surrogate pair), and its tokens are cut
  at the pages' edges.
  - A page without tokens goes in as plain text, with the default character format set first instead of a format pass.
  - Markdown is drawn whole, up to 100,000 characters (0.74 s); a longer text is shown as plain text.
  - The peek and the viewer share one `TextPreviewPresenter`, warmed up with the panel: on a fresh one the viewer's
    first colored page took 1.4 s.
- **Afterwards:** the 6 MB log opens in 2 to 3 s as page 1 of 94, and 2.3 MB of C# as page 1 of 36; turning a page takes
  under 0.5 s. A show of 50,000 characters or more that takes 500 ms or more is logged, without content: "Text surface:
  N characters as RTF took N ms to show (set N ms, first layout N ms)".

**Found by the live QA, fixed** (§4 has each lesson):
- the overlays took their colors from Windows' app theme instead of the panel's: light over a dark panel, the prose
  unreadable. The image peek's frame had the same fault since §2.24;
- the peek closed at the first move of the mouse over its text, where the first version meant to keep it open (the
  rule changed the same day, below);
- the viewer's first text came out in the interface font, without colors;
- the language menu did not open;
- Esc did nothing in a viewer opened from a menu's *View*, the image viewer's too;
- megabyte texts froze the panel for 15 to 30 s (pages, above).

**The owner's follow-up the same day** (after trying `0.2.11-dev.17.g2fe6f72` on their PC): "When the text viewer
appears because i paused my mouse (hover) above the text - it close immediately if i move the mouse BUT it should react
to middlemouse scrolling just fine. Only keep open ignoring mouse movement when clicking the eye at the corner." and "I
dont think Markdown renders at this with a scrollbar for some reason (on the local install)", about a 3,600-character
note of theirs (32 lines).
- **Any real move closes the peek.** The first version kept it while the mouse moved over it or its card, so that its
  text could be selected. Now a move past 3 pixels from where it opened closes it, wherever the mouse goes
  (`TextPeek_PointerMoved`), as the image peek does. Only its own right-click menu keeps it while the mouse goes to the
  menu; once that menu closes, the next move counts from where the mouse is. Selecting with the mouse is the viewer's.
- **The wheel scrolls it from anywhere** on its surface, which covers the panel's monitor, and no longer closes it. Over
  the text box RichEdit scrolls by itself (91 pixels a notch for that note in the QA desktop); anywhere else
  `TextPreviewPresenter.ScrollByWheel` scrolls it (three lines a notch at the text's size, 53 pixels there; quick
  notches add up). Told apart by where the mouse is: RichEdit leaves the event unhandled, and the fix's first build,
  which went by the handled flag, scrolled the text twice per notch over the text.
- **A text of up to 20,000 characters taller than the frame was cut off, without a scrollbar.** Its row was an Auto row,
  which measures the text box with unlimited height: the box grew to its whole text, the frame cut it at its
  `MaxHeight`, and RichEdit's scroller had nothing to scroll. The owner's note was such a text: recognized as Markdown
  and drawn as a document (checked locally on the note itself), with its end out of reach. The row is a star row now,
  and a short text's frame still fits its text (§4).
- Verified live with an invented look-alike of the note (§5). The note itself stayed on this PC: only the verdict was
  read from it, in the scratchpad.

**Pieces.**
- Core: `TextView/` (`TextLanguage` and `TextLanguages`, `TextLanguageDetector`, `SyntaxToken`, `SyntaxHighlighter`
  with its `.Code`, `.Markup` and `.Data` parts, `StructuredTextFormatter`, `SyntaxPalette`, `RtfWriter` with
  `TextViewStyle`, and `TextViewDocument.cs` with `TextViewSource`, `TextViewRendering` and `TextViewPage`);
  `Presentation/Markdown`; three members of `AppSettings`.
- App: `Views/ClipboardFlyout.TextPreview.cs` (the peek, the viewer, their menus and keys),
  `Views/TextPreviewPresenter.cs` (the RichEdit surface and its gutter), `AppController.TextView.cs`,
  `ViewModels/ClipItemViewModel.TextView.cs`; the card template's "⋯" line and eye (`ClipboardFlyout.xaml`); F3 and
  *View* (`ClipboardFlyout.xaml.cs`, `.Prompts.cs`, `.Shells.cs`); the overlay styles and `TextViewButtonStyle`
  (`App.xaml`).
- Strings: 28 entries and one plural string in all five catalogs, each with its description.

**Verified:** see §5.

**Limits / not built:**
- No Find (not chosen). A selection, and *Select all*, stay within one page.
- The peek recognizes the language from the start it shows: a megabyte log can read "Markdown" in the peek, while the
  viewer says "Too long to draw as Markdown: shown as plain text."
- The status line counts the copied text's lines: a flat XML copy reads "1 line" while the formatted view shows twenty.
- A copy from the peek or the viewer becomes a new history entry (CRLF line breaks, source BetterClipboard). From a
  Markdown document it is the drawn text (list numbers and bullets with a tab, no blank lines between paragraphs), not
  the Markdown source; the card's *Copy only* copies the source.
- Ctrl+wheel steps once per wheel event, so a high-resolution wheel changes the size quickly.
- Not run: a real GPU, Narrator, touch and pen, the toolbar in a right-to-left language, a monitor with another scale
  than the panel's.

---

## 3. Build · run · test

```bash
dotnet build BetterClipboard.sln                               # everything (App builds win-x64)
dotnet test --solution BetterClipboard.sln                     # 2,338 tests (Core 1,893, Windows 445; opt-in tests + 1 explicit measurement skipped)
pwsh tools/release/test-update.ps1 -Version X.Y.Z -PackageDirectory artifacts/release   # install.ps1's update mode
                                                               # against a package; scratch folders only (§2.29)
pwsh tools/release/test-iex.ps1                                # install.ps1's param block survives `irm | iex`
                                                               # (5.1 and 7; changes nothing, §3.1)
pwsh tools/release/test-all-users.ps1 -Version X.Y.Z -PackageDirectory artifacts/release -ConfirmMachineChanges
                                                               # install.ps1 -AllUsers for real, as administrator and
                                                               # as SYSTEM: CI or a disposable VM only (§2.34)
pwsh tools/release/publish-dev.ps1 [-Install]                  # a dev build of HEAD's commit into this PC's
                                                               # dev-builds folder: the installed app's update
                                                               # button offers it (§2.39, skill local-dev)
pwsh tools/release/package-msix.ps1 -Version X.Y.Z -Architectures x64 -NewTestCertificate   # the Microsoft Store
                                                               # package, signed for a test desktop; -ForStore for a
                                                               # submission (§3.5). Never register it on this PC
pwsh tools/release/test-submit-store.ps1                       # the Store submission script against a stand-in
                                                               # service on this PC: no network (§3.5)
python tools/release/test-store-listing.py                     # the listing kit fits the Store's limits, and the
                                                               # generator still fills Partner Center's export (§3.5)
BETTERCLIPBOARD_CLIPBOARD_TESTS=1 dotnet test --project tests/BetterClipboard.Windows.Tests   # + real clipboard
tests/BetterClipboard.Windows.Tests/bin/Debug/net10.0-windows10.0.26100.0/BetterClipboard.Windows.Tests.exe \
  -method BetterClipboard.Windows.Tests.ClipboardCaptureTests.CaptureRate_BySpeedOfCopying -explicit only -showliveoutput
                                                               # capture rate by gap (§1.9), private window station
```

Do **not** add `-v q` to `dotnet test --solution` (Microsoft.Testing.Platform then reports "Zero tests
ran", exit 5). A test executable can also be run directly, e.g.
`tests/BetterClipboard.Windows.Tests/bin/Debug/net10.0-windows10.0.26100.0/BetterClipboard.Windows.Tests.exe -class <FQN> -showliveoutput`.
The solution build puts the app in `src/BetterClipboard.App/bin/x64/Debug/net10.0-windows10.0.26100.0/win-x64/`.
A project-level `dotnet build src/BetterClipboard.App` (no `Platform`) writes to `bin/Debug/…` instead, so a
script running the `bin/x64` exe tests a **stale** binary. Lesson (2026-09-25): the first ShareX-tab
screenshot showed the old padding. Rebuild through the solution before any end-to-end run.

Run: plain launch = start + open Settings (or activate the running instance); `--background` = tray only
(used by "Start with Windows"); `--show-flyout` = open the flyout in the running instance; `--exit` =
graceful quit (drains queued captures). **The exe is locked while running — `--exit` before rebuilding.**
Isolated dev run: `BETTERCLIPBOARD_DATA_DIR=<scratch>/data BetterClipboard.exe --background` — a scoped
instance (§2.9), so `--exit` with the same variable set stops only it; **without** the variable, `--exit`
stops the user's installed app.

**The silent restart** (§2.46) runs about 5 s after Settings closes in any instance, an isolated one included: a test that
opens Settings sees the process id change afterwards (the log says "Restarting silently …"). Its internal command line,
`--background --restart-after pid:startTicks --clipboard-sequence N`, is for the app itself; started by hand it waits for
that process and replaces it.

**The language picker** (§2.37) shows at every start until a language is chosen: a new scratch folder
(`BETTERCLIPBOARD_DATA_DIR=<a new scratch folder> BetterClipboard.exe`), or a `settings.json` that names no language
and has no `"LanguageChosen": true` (what the installer's `-Hotkey` and 0.2.9 leave). Strings: `python tools/localization/catalog.py
add "English" -d "Where it shows, what {0} holds" zh-Hans="中文"` / `describe "English" "…"` / `rename "Old" "New"` /
`check` ([`docs/localization.md`](docs/localization.md)).

**Side by side for the user** ("Launch for me quickly", 2026-10-01): `python tools/launch_dev.py` runs a
**copy** of the solution build next to the installed app. `--exit` stops it, `--exit --clean` also deletes it
and its history, and `--no-show` skips the panel.
- **A copy, never the build output.** A running exe locks its files, so the next build (yours or another
  agent's in the same tree) fails with "being used by another process", and stopping a process to get a build
  through is not allowed.
  - The copy lives in `%TEMP%\BetterClipboard-dev\app`. A re-run sends the copy `--exit` first, because it is
    locked too while it runs.
  - It prints the build time and warns when a source file is newer: copying never rebuilds.
- **Its own data dir** (`…\data`): a scoped instance (§2.9).
  - A new data dir is seeded with `OpenHotkey` `Alt+Win+V` (free on this PC, §1.6) and
    `UseKeyboardHookFallback` off, so Win+V stays with the installed app.
  - Real data on purpose: it imports Windows' history and the Win+R history, watches the real ShareX folders
    and records live copies. That data stays in the folder until `--clean`.
- **The user's environment, not this shell's.** It uses `CreateEnvironmentBlock(bInherit = FALSE)` +
  `BETTERCLIPBOARD_DATA_DIR`, with no inherited handles. Otherwise this shell's `CLAUDECODE`, `MSYSTEM` and
  invalid `GH_TOKEN` reach every command the dev app starts (the Run tab's Ctrl+Enter).
- **The panel opens only over a terminal, IDE, browser or Explorer.** On 2026-10-01 a game (`rs2client.exe`)
  and later another app were in front, so the script printed the shortcut instead.
- **Verified 2026-10-01**, 0 new warnings or errors in the log:
  - copied in 0.4 s and started in 0.3 s;
  - `Alt+Win+V registered with RegisterHotKey`;
  - Win+R: "imported 26 new of 26";
  - Windows: "Imported 24 new items (27 found …)";
  - ShareX folder watched.
  - The installed app's PID was unchanged. The line "other instances still running: 1 of 2" was another
    agent's own test instance, which had ended by itself: the script only ever signals its own data dir.

`bclip` from the solution build: `src/BetterClipboard.Cli/bin/Debug/net10.0-windows/bclip.exe`
(framework-dependent; it can auto-start only a `BetterClipboard.exe` in its own folder, so start a dev app
yourself). CLI end-to-end next to the user's app (2026-09-25): publish, seed an isolated store with
`BC-TEST` items only (settings: `EnableCommandLine` on, `ImportWindowsHistoryOnStartup` off,
`IsCapturePaused` on), export `BETTERCLIPBOARD_DATA_DIR`, run the published `bclip` (it auto-starts the
scoped instance), print only `BC-TEST` lines, `--exit` the scoped instance, and compare the list of
`BetterClipboard.exe` PIDs before/after — the user's must be unchanged.

ShareX end-to-end (2026-09-25), with the dev build:
- **Setup:** export `BETTERCLIPBOARD_DATA_DIR` and `BETTERCLIPBOARD_SHAREX_DIR=<scratch>/FakeShareX`
  (create `Screenshots\2026-09`). Settings: `EnableCommandLine` on, `ImportWindowsHistoryOnStartup` off, and
  `IsCapturePaused` **off** (paused skips screenshots by design). The instance then also records live
  copies, so print only `-f sharex` fields (id/kind/origin/source) and delete the data dir afterwards.
- **Run:** start the app with `--background` yourself; write BC-TEST PNGs (Python-generated) into the fake
  folder; poll `bclip list -f sharex --json`; `--exit`, write while it is stopped, restart for the catch-up;
  compare PIDs.
- **UI check:** start with capture paused, and only when the store holds nothing but BC-TEST items and the
  foreground is a terminal/IDE. `--show-flyout` (or a plain launch for Settings), then UI Automation:
  `SelectionItemPattern.Select` on the tab, `ScrollPattern` to the card — no injected input. Screenshot only
  the test window, and only while it is the foreground.

Run tab end-to-end (2026-10-01), with the dev build:
- **Setup:**
  - a scratch Win+R list `HKCU\Software\BetterClipboard-E2E\RunMRU`, written and "run" by a script with
    comctl32's rules (move to front, or overwrite the oldest letter when full);
  - export `BETTERCLIPBOARD_RUNMRU_KEY` next to the data-dir and ShareX overrides;
  - capture **not** paused (runs are new events), so print only `-f run` rows and delete the data dir afterwards.
- **Runnable test command:** `wscript "<scratch>\bc_test_run.vbs"`, a script that only writes a BC-TEST marker
  file. It opens no window, so the run steals no focus.
- **Keys:** Ctrl+Enter goes in only after a check that the test panel is the foreground window.
- **Test PID:** take it from `Start-Process … -PassThru`, never by diffing the process list.

Everything tab (2026-10-01):
- **Tests without Everything:** the Windows tests run a fake IPC window in their own process; nothing else is
  needed. **With a real Everything (opt-in):** `BETTERCLIPBOARD_EVERYTHING_EXE=<a portable Everything.exe>` runs
  `RealEverythingTests` — a private, windowless instance in a temp folder (`-instance BCTEST-<guid> -startup
  -config <ini> -db <db>`, `app_data=0` in an `Everything.ini` next to its copy so nothing reaches
  `%APPDATA%\Everything`, no tray icon, no volumes, only a BC-TEST tree indexed); picks via `INC_RUN_COUNTW` (24).
  Passed on 1.4.1.935, 1005, 1026, 1032 and 1.5.0.1423b (voidtools' portable zips, SHA-256 checked against their
  published lists). Everything is not installed on this PC.
- **Panel end-to-end:** the same kind of private instance (named, e.g. `BCTEST-E2E`) with a few picks, then an
  isolated app with `BETTERCLIPBOARD_EVERYTHING_INSTANCE=BCTEST-E2E` next to the data-dir, ShareX and Win+R
  overrides (a nonexistent scratch `RunMRU` key shows the Run tab without reading the user's). Capture **not**
  paused (Ctrl+P on a pick is a new event), `PasteOnSelect` off as a second guard, print only BC-TEST rows of
  `bclip list -f everything --json`, delete the data dir and stop the instance (IPC exit) afterwards.
- **UI Automation:** `SelectionItemPattern` only on cards, never `InvokePattern` (§4: invoking a card pastes it).
  Keys (Ctrl+P, Delete, Esc) only after a check that the test panel is the foreground window. Wait for the
  terminal to be in front for two checks before each summon; another session's own UI test can hold the
  foreground (seen 2026-10-01): wait until only the user's `BetterClipboard.exe` processes remain.

Snipping tab (2026-10-01), with a copy of the dev build:
- **Overrides for every source**, so nothing of the user's is read or watched:
  - `BETTERCLIPBOARD_SCREENSHOTS_DIR` = a scratch "Screenshots" folder;
  - the ShareX, PSReadLine, Claude and Codex folders as empty scratch folders;
  - a nonexistent scratch `RunMRU` key, and an Everything instance name nobody uses.
  The real Snipping Tool package and settings are still read; they are read-only, and the status line shows them.
  - **Footgun:** an existing `BETTERCLIPBOARD_SHAREX_DIR` folder counts as an installed ShareX, so its tab shows.
- **Import phase with capture on:** pause skips screenshots by design. Write BC-TEST PNGs named like each tool, check
  them through `bclip list -f snipping --json` (origin `screenshot`), and `--exit` while one is written for the catch-up.
- **UI phase with capture paused:** restart with `IsCapturePaused` true before any UI shows, so none of the user's copies
  can reach the test panel.
- **Settings window:** a plain launch with the same environment opens the running scoped instance's Settings. Its title
  is "BetterClipboard", like the panel's: find it as the instance's other window.
- **Scrolling:** `ScrollItemPattern.ScrollIntoView` leaves a card's header at the bottom edge; a few `ScrollPattern`
  small steps bring the card up.
- Scratch scripts: `snip_e2e/run.py` (headless) and `snip_e2e/ui.py` (on screen), in session 66f12277's scratchpad.

### 3.1 Release & install

- **Package:** [`tools/release/package.ps1`](tools/release/package.ps1) `-Version X.Y.Z` → for win-x64 and
  win-arm64: `dotnet publish -c Release -r win-<arch> --self-contained true -p:Platform=<x64|ARM64>
  -p:DebugType=none` (no .NET or WinAppSDK runtime needed on the target), then `bclip` published
  self-contained into the same folder (it shares the app's runtime files: the x64 zip grew by 0.17 MB) + `install.ps1`, `LICENSE`,
  `THIRD-PARTY-NOTICES.md` at the zip root → `BetterClipboard-X.Y.Z-win-<arch>.zip` (~70 MB, ~180 MB
  unpacked), the same files as `BetterClipboard-X.Y.Z-win-<arch>.7z` (7-Zip LZMA2, ~43 MB: what the
  Chocolatey package embeds; needs 7-Zip: `7z` on PATH, Program Files or Chocolatey's copy) + `SHA256SUMS.txt`
  (sha256sum format, LF, all four files). The same script runs in CI and in the release job.
- **Release:** push an **annotated** tag `vX.Y.Z` whose message is the release notes (`git tag -a vX.Y.Z -F
  notes.md`) → [`.github/workflows/release.yml`](.github/workflows/release.yml):
  1. builds the notes first ("Release notes from the tag": the tag's message plus a "Code signing policy" footer, §3.3);
  2. tests and packages, then updates to the x64 zip once with the app's own command line
     (`tools/release/test-update.ps1`, §2.29);
  3. builds and tests the Chocolatey package (stable tags only, §3.2);
  4. `gh release create --notes-file` with the zips, the `.7z` archives, `SHA256SUMS.txt`, `install.ps1` and
     `betterclipboard.X.Y.Z.nupkg`;
  5. `choco push` (with the `CHOCOLATEY_API_KEY` secret; without it only a warning);
  6. submits the two Microsoft Store packages (stable tags only; built before step 4 and kept as the run's artifact
     `store-packages`) with `tools/release/submit-store.ps1` (§3.5). Without the three `PARTNER_CENTER_*` secrets, or
     while the product's first submission is not published, the step only warns.

  Tags with a pre-release suffix (`-rc.1`) become pre-releases and skip Chocolatey and the Store. CI
  ([`ci.yml`](.github/workflows/ci.yml)) builds, tests and packages x64 on every push/PR, runs the installer's update
  mode against that zip, installs, upgrades and uninstalls an x64 Chocolatey package, builds a test-signed x64 Store
  package, and runs the tests of the Store submission script and of the listing tools.
  - **Lesson (2026-10-02): v0.1.0–v0.2.3 were all published with the wrong notes.** Each shows its tagged commit's
    message ("Version 0.2.3"), although the tags on GitHub are annotated with the real notes.
    - Cause: `actions/checkout@v5` fetches a pushed tag by its commit (`+<sha>:refs/tags/<tag>`), so the runner's tag is
      lightweight, and `--notes-from-tag` falls back to the commit's message. Fixed in checkout v6
      (actions/checkout#2356, `testRef` with `^{commit}`).
    - The workflow now fetches the tag object again and refuses a tag that is still not annotated.
    - Its notes are what gh's `gitTagInfo` reads (`%(contents)` minus `%(contents:signature)`), with
      `[Console]::OutputEncoding` set to UTF-8: PowerShell decodes git's output with the console code page.
    - Verified in a scratch clone with the tag forced lightweight, under code page 437 (`docs/code-signing.md` §6).
    - The five published pages were corrected on 2026-10-02 with the user's go-ahead. Each was set with
      `gh release edit --notes-file` to the notes the new step builds (annotation + footer), then read back through
      the API: 5 of 5 identical, footer present, no mojibake. The old bodies are backed up in session 79bc5201's
      scratchpad (`signpath/release-pages/backup`). Titles and the Latest flag were unchanged.
    - The first real tag through the new step, v0.2.5 (2026-10-03, run 37100517389), was published with its own
      notes plus the footer, read back through the API with no mojibake (§5).
  - **Release procedure used for 0.2.5** (2026-10-03, "bump last time to latest commit and do actual release and push
    of the new version"):
    1. Scan the commits to push: no binaries, no secret-like added lines, not the user's email.
    2. `git push origin main`, with `GH_TOKEN`, `GITHUB_TOKEN`, `GH_DEBUG` and also `DEBUG` unset. `DEBUG` is set in
       this shell and makes `gh` print its request trace on stderr (stdout stays clean). Captured with `2>&1`, that
       trace broke `--json` parsing.
    3. Wait for CI on that exact commit (`gh run view <id> --json status,conclusion`).
    4. Extract the newest notes draft with the anchored lookup above, everything after the marker line, into a file.
    5. `git tag -a vX.Y.Z --cleanup=whitespace -F notes.md <the CI-tested sha>`. Check that it is annotated
       (`git cat-file -t`) and that `%(contents)` equals the file, then `git push origin refs/tags/vX.Y.Z`.
    6. Poll the Release run (~5.5 min for 0.2.5). Read the page back through `gh api …/releases/tags/vX.Y.Z`: the body
       is the notes plus the footer, and every `SHA256SUMS.txt` line matches its asset's GitHub `digest`.
    - The `install.ps1` asset has CRLF line endings: the Windows runner checks it out with `core.autocrlf`. The blob
      and `irm | iex` (raw.githubusercontent.com) have LF. The content is identical, and both run.
  - **Through a merge request** (0.2.10, 2026-11-10; the earlier releases pushed `main` straight):
    1. Push local `main`'s head as `release/X.Y.Z` and open a pull request into GitHub's `main`, not a draft; CI
       runs on it. Check `main` again right before merging: on 2026-11-10 two commits of another session had
       landed after the request was opened, and the owner chose in one question to include them.
    2. Merge with a merge commit, never a squash (the commit messages carry the notes draft): `gh pr merge N --repo
       Nucs/BetterClipboard --merge --match-head-commit <full sha> --subject … --body …`, run from a folder outside
       the repository, so `gh` cannot check out or pull in the shared tree. The guard needs the full hash
       (`git rev-parse`): a guessed one was refused with "Head branch was modified", and nothing changed.
    3. Wait for CI on the merge commit, then tag the merge commit with the anchored lookup's draft (`git log -1
       --grep='^RELEASE NOTES (draft…' --format=%B <merge commit>`: GitHub's merge message has no marker line).
    4. Afterwards fast-forward local `main` (`git merge --ff-only origin/main`: the merge commit has the same tree,
       so no file of the shared tree changes) and delete the release branch on GitHub.
- **Installer** ([`install.ps1`](install.ps1), Windows PowerShell 5.1 and PowerShell 7, StrictMode 3):
  GitHub API → zip for the **OS** architecture (`RuntimeInformation.OSArchitecture`, correct under x64
  emulation on ARM64) → SHA-256 vs `SHA256SUMS.txt` **and** GitHub's asset `digest` → `--exit` the running
  instance (graceful, 15 s, then kill) → extract to `<dir>.new`, `Unblock-File`, swap via renames
  (failure puts the old version back) → Start-menu shortcut, Run entry in exactly the app's own format
  (`"<exe>" --background`), HKCU `Uninstall\BetterClipboard` entry (runs `install.ps1 -Uninstall` from the
  install folder) → `installer.json` in the data dir → launch (via `explorer.exe` when elevated, so the app
  never runs elevated; otherwise with its own folder as working folder). The whole run works from the temp folder
  and puts the caller's PowerShell back afterwards (§2.25). It takes over the Win+letter shortcuts BetterClipboard
  answers to (Win+V by default): `DisabledHotkeys` + one Explorer restart, skipped when nothing changed or no Explorer
  runs in the session; `-NoTakeOverWinV` / `-Hotkey` change the shortcuts through `BetterClipboard.exe --set-hotkeys`
  (needs ≥ 0.2.6, §2.25); `-TakeOverWinV` is the default and stays accepted. `-AddToPath` puts the
  install folder on the user PATH (same rules as `Shell/UserPath`: raw REG_EXPAND_SZ, `%VAR%` entries kept,
  `WM_SETTINGCHANGE` via `Add-Type`, and `$env:Path` of the calling window because `irm | iex` runs in it).
  `-Uninstall` removes everything but the history (`-RemoveData` for that), removes the PATH entry (whoever
  added it) and gives back to Explorer Win+V, the keys `installer.json` › `releasedKeys` records, and the Win+letter
  shortcuts in the settings, whichever of them are released. Registry keys are created only when missing
  (`Confirm-RegistryKey`): up to 0.2.5, `New-Item -Path $RunKeyPath -Force` emptied the user's Run key on every install
  (§2.25, §4).
  - **Since 2026-10-09 (§2.29):**
    - `-Package <zip>` installs a release zip from disk, verified against `-PackageSha256`, else against a
      `SHA256SUMS.txt` next to it;
    - `-NoUpdateCheck` installs with the automatic update check off (`BetterClipboard.exe --set-update-check off`,
      needs ≥ 0.2.6), and the closing note names the check, both ways to switch it off and the privacy section;
    - `-Update` is the app's own update mode: one copy and nothing else. Its command line is a contract (§4);
    - `-ResultPath` and `-LogPath` report a run that has no window.
  - **Since 2026-10-10 (`b14a76c`):** `Stop-RunningApp` counts only processes that really run (`Test-ProcessRuns`:
    in the process list, same name, at least one thread) and warns instead of failing when one cannot be stopped.
    Before, the leftover process object of a crashed BetterClipboard, or a copy running as administrator, ended a
    full install with "Cannot stop process … Access is denied" after the running app had been closed (§4, §5).
    The update mode was never affected: it matches processes by their program's path.
  - **Since 2026-10-16: `irm | iex` works again.**
    - **The bug:** from `7d7df8d` (2026-10-09; pushed with v0.2.6 on 2026-10-10) the param block had
      `[ValidatePattern]` on `-PackageSha256`. `Invoke-Expression` does not bind parameters: it runs the param block as
      variable declarations and checks each attribute against the empty value. So the README's first command,
      `irm …/install.ps1 | iex`, printed "The attribute cannot be added because variable PackageSha256 with value
      would no longer be valid" and did nothing, in Windows PowerShell 5.1 and PowerShell 7.
    - **How it was found:** in the Ditto research's test desktop (§2.32). Nothing noticed it earlier: the README's
      second form (`& ([scriptblock]::Create(…)) …`), `-File`, `install-local.ps1` and every test bind parameters
      normally.
    - **The fix:** the pattern is checked in the script body ("-PackageSha256 must be a SHA-256 checksum: 64
      hexadecimal digits."), before anything is downloaded.
    - **Verified:** in a test desktop, the fixed script piped into `Invoke-Expression` installed 0.2.8 over a running
      copy in 52 s, and a malformed hash was refused with that message. Locally
      [`tools/release/test-iex.ps1`](tools/release/test-iex.ps1) fails on the old `install.ps1` and passes on the
      fixed one, in both shells. CI runs it on every push.
    - **Users have it since the push of 2026-10-26** (`79d5d59`, with v0.2.9): the README downloads `main`'s
      `install.ps1`. From 2026-10-10 to 2026-10-26 the README's first command failed for everyone.
  - **Since 2026-10-18: for every user (`-AllUsers`, §2.34).**
    - Program Files with protected rights, the PC's Run value with `--all-users-startup`, the PC's Installed-apps
      entry (its uninstall asks for administrator rights itself), the shortcut for every user and the PATH of every
      user.
    - Picked by itself as SYSTEM, with administrator rights on another user's desktop, and for an elevated run that
      finds a copy for every user.
    - `-InstallDir` defaults by scope now: the parameter is empty until `Resolve-InstallScope` has run.
    - Fixed on the way: every run as SYSTEM stopped at its first line. SYSTEM has no Start menu folder, and
      `Join-Path ''` refuses an empty path.
    - [`tools/release/test-all-users.ps1`](tools/release/test-all-users.ps1) runs it for real in CI, as administrator
      and as SYSTEM.
  - **Since 2026-11-08: a leftover that cannot be removed is set aside** (`Remove-Leftover`, `Remove-StaleLeftovers`).
    - **The bug:** an install or update started by deleting `<install>.new` and `<install>.old` from earlier runs, and
      failed on the first file it could not delete. On the owner's PC, ShareX held `DWriteCore.dll` of the folder
      the update to 0.2.9 had renamed to `.old` (the folder is on the user PATH, §2.9), so that update's own clean-up
      (silent) left `.old` with that one file, and from then on every install and every update failed at its start:
      "Cannot remove item …\BetterClipboard.old\DWriteCore.dll: Access to the path is denied" (found by the
      first `publish-dev.ps1 -Install`, §2.39, which stopped before changing anything).
    - **Now:** a leftover that cannot be deleted is renamed to `<install>.old-stale-<time>` (a loaded DLL does not
      keep its folder from being renamed) and a later run, or the uninstall, removes it once nothing holds it. Only a
      folder that can be neither deleted nor renamed (a file open without delete sharing) still stops the run, with a
      sentence that says what to close. After a successful swap the clean-up is best effort and never fails the run.
    - **Who gets it:** every install and update that runs this `install.ps1`. The update button runs the new
      package's own installer, so a copy updates past the trap once the package it updates to has the fix.
  - **Since 2026-11-13: the Microsoft Store edition** (§2.47).
    - `-Uninstall` takes `-KeepProcessId <pid>` (that process is never closed: it is the Store edition, which runs
      the uninstall), `-ResultPath` and `-LogPath` (both valid in the uninstall's parameter set now), and writes its
      result file. This is the command line the Store edition runs, with the script its own package carries.
    - An install warns, before it changes anything, when the Store edition is installed for the account
      (`Get-StoreEditionData`), and says what that edition does when it is opened.
  - **A Chocolatey copy** (`lib\betterclipboard\tools\app`, §3.2) is respected:
    - install warns about it and leaves a startup entry that starts it;
    - `-Uninstall` keeps a startup entry that starts that copy; its own entry or a stale one goes, or, while that copy
      stays, is handed to it (since 2026-10-03: the migration the package's warning recommends left the Chocolatey copy
      without autostart); and keeps Win+V (and the other keys it would give back) released while that copy remains.

    The package treats `install.ps1`'s copy the same way. Tested with the functions loaded from the AST and the
    system calls stubbed: 9 of 9 in PS 5.1 and 7.
- **Installer tests (offline):** shadow `Invoke-RestMethod`/`Invoke-WebRequest` with functions that serve
  the local `artifacts/release` zips (PowerShell resolves functions before cmdlets, also inside the called
  script; share state via `$global:`, not `$script:`). Verified 2026-09-25: fresh install (5.1), update over
  a running instance (7), tampered checksum refused with the install untouched, uninstall launched from
  *inside* the install folder (needed `[Environment]::CurrentDirectory` — `Set-Location` alone keeps the
  folder's process-CWD handle open), DisabledHotkeys helpers against a scratch key. PATH helpers
  (2026-09-25, 15 checks, PS 5.1 + 7): load only those functions from the script's AST
  (`Parser::ParseFile` → `FunctionDefinitionAst`) and point `$UserEnvironmentKey` at a scratch HKCU key — the
  real PATH is compared before/after. **Now that the user has a real install, never run the full installer
  as a test:** it `--exit`s every BetterClipboard in the session and rewrites the Run key, shortcut and
  Installed-apps entry. When invoking Windows PowerShell 5.1 from this bash, clear `PSModulePath`
  (`env -u PSModulePath …`), or 5.1 picks up PowerShell 7's modules and even `Get-FileHash` is "not recognized".
  - **The whole installer on this PC, safely (2026-10-03):** a harness (`installer_harness.ps1`, session 7579fd35's
    scratchpad) rewrites a copy's Run/Uninstall/Explorer\Advanced/Environment paths, shortcut and default install folder
    to scratch locations (each substitution must match once), replaces `Restart-Explorer` with a counter, and shadows
    `Get-Process` (hides BetterClipboard and explorer, so nothing is signalled), `Stop-Process` (refuses), `Start-Process`
    (records; lets only the scratch install's `--set-hotkeys` run) and the two downloads. It compares the user's Run value,
    Installed-apps entry, `DisabledHotkeys`, shortcut, PATH and the app and Explorer PIDs before and after. 18 scenarios
    in `run_scenarios.sh` (§5).
- **Pre-release install on this PC** ("install it locally, don't release yet"):
  [`tools/release/install-local.ps1`](tools/release/install-local.ps1) `-Version X.Y.Z` runs the real
  `install.ps1` with the same shadowing, packaged as a script: the lookup and both downloads come from
  `artifacts/release`. Everything else is the real installer (SHA-256 vs `SHA256SUMS.txt`, graceful
  `--exit`, swap, shortcut, Run and Installed-apps entries, `installer.json`). It is a real install, so it
  needs the user's request. Procedure used for 0.2.4 (2026-10-01) and 0.2.5 (2026-10-02):
  1. `pwsh tools/release/package.ps1 -Version X.Y.Z` (both architectures; 84 s for 0.2.4, 124–163 s for 0.2.5).
     - Since 0.2.5: in a worktree at the bump commit, like the dev build below. The exe then carries that commit's
       revision (`0.2.5+ac65856…`), and no other session's uncommitted work can ship.
     - "Move X.Y.Z to the latest changes" (before the tag): the same, at `main`'s newest commit, under the same version
       (0.2.5 again at `5cf1eef`). The installer has no same-version check: it simply installs again.
     - Run `dotnet test --solution BetterClipboard.sln` there first. `package-chocolatey.ps1 -Version X.Y.Z` works
       here too: the Chocolatey CLI (2.3.0) is installed on this PC, and the script only packs.
     - Since 0.2.8, also `test-update.ps1 -Version X.Y.Z -PackageDirectory artifacts/release`: the release job's check
       of the installer's update mode (the contract command line). It is safe on this PC, because it works in scratch
       folders only (0.2.8: 17 of 17 in 20 s).
  2. `env -u GH_TOKEN -u GITHUB_TOKEN -u GH_DEBUG -u BETTERCLIPBOARD_DATA_DIR -u BETTERCLIPBOARD_SHAREX_DIR
     -u PSModulePath powershell -NoProfile -ExecutionPolicy Bypass -File tools/release/install-local.ps1
     -Version X.Y.Z -NoLaunch` (Windows PowerShell 5.1, like `irm | iex` users). From a worktree, pass Windows
     paths and `-ReleaseDirectory` / `-Installer` explicitly (the dev build's trap, below).
  3. Start the app through Explorer: a temporary `.lnk` with `--background`, opened by `explorer.exe`.
     The app then gets the user's environment, not this shell's (`CLAUDECODE`, `MSYSTEM` must be absent).
  4. Check the file version, `installer.json`, the Run entry, the Installed-apps `DisplayVersion`, and the
     log after "starting": 0 WRN/ERR.
  - Tested against a stub installer in PS 5.1 and 7: served lookup, a zip copy matching `SHA256SUMS.txt`,
    `-NoLaunch` passed on, other lookups refused, a clear error for a missing package, and the real cmdlets
    back afterwards.
  - Bump commits keep `Directory.Build.props` alone, and their message carries the drafted tag notes
    (`git log -1 --format=%B`) until the release. A bump past a version that was never released carries that
    version's notes too: 0.2.4 was never tagged, so 0.2.5's notes cover everything since v0.2.3.
  - Commits after the bump update the draft without an amend: the next commit's message carries the whole updated
    draft under the same "RELEASE NOTES (draft for the annotated tag vX.Y.Z; …)" line, and the newest one wins
    (`git log -1 --grep='^RELEASE NOTES (draft for the annotated tag vX.Y.Z' --format=%B`). 0.2.5's first draft is
    in `ac65856`; the one with the image overlays is in the commit that records the `5cf1eef` reinstall.
  - Keep the `^`: `--grep` matches line by line, so the anchor skips a message that merely quotes the command
    mid-line. Without it, a later commit message that quotes it becomes "the newest draft" although it holds no notes
    (nearly happened 2026-10-02). Never start a line of a commit message with the marker unless the notes follow it.
  - **The notes follow the earlier releases' format, minus anything about older versions** (the owner, 2026-11-08:
    "Go over our last 10 releases, follow their format", then, after editing the v0.2.10 draft themselves: "Also no
    mentions of older versions"): the title line `BetterClipboard X.Y.Z` alone (the owner's edit cut its summary);
    the install command; one bold lead-in paragraph per feature, with bullets; **Fixed**; the files table; the
    code-signing note. Nothing that names an older release: no "You have 0.2.x" lines, no "What came in X.Y.Z …"
    pointer, no "An older BetterClipboard can still open your history", and no "since an earlier update" in a fix.
    No headings and no summary list. Wrapped at 117 columns like the earlier notes, never inside a link, a code span
    or an emphasis: GitHub shows a line break at each wrap (v0.2.9's `body_html`), which every release page has, and
    the update dialog follows CommonMark and joins the lines.
  - **An outside contributor is thanked in one line, right after the title line**, naming what they brought and
    their bare `@login` (the owner, 2026-11-08, of a five-line paragraph and a language section: "Horrible, simplify
    and minimize the language addition to a single line"). Keep that line within the 117 columns, so GitHub never
    breaks it. Read the login with `gh pr view <n> --json author,commits`. On the release page GitHub links the
    mention, notifies the person and lists them under the release's contributors; the update dialog shows plain
    text. Check a draft with GitHub's own renderer (`gh api markdown --input <json>`, `mode` `gfm`, `context`
    `Nucs/BetterClipboard`), which publishes nothing. First used for v0.2.10 (pull request #1, Simplified Chinese).
- **Dev build on this PC** ("Build and install dev", 2026-10-01): the same install, of `main`'s latest commit.
  - **Since 2026-11-08: `tools/release/publish-dev.ps1`** (§2.39, skill `local-dev`). It does the worktree, the
    version (`X.Y.Z-dev.<count>.g<sha>`) and the package, and publishes to the dev-builds folder; the installed app's
    update button installs it. `-Install` once for a copy that predates the folder (0.2.9 and older). The steps below
    are what it automates.
  - Build from a separate worktree at that commit (`git worktree add --detach <scratch>/wt <sha>`), never from the
    shared tree: other sessions' uncommitted, half-done work would ship into the user's real app. Remove the worktree
    afterwards.
  - Version `X.Y.Z-dev.<short sha>`, a pre-release of the upcoming release (`-Version 0.2.4-dev.74594fb`
    `-Architectures x64`, 68 s). Installed apps and `installer.json` show it; the exe's product version is
    `0.2.4-dev.74594fb+<full sha>`; its file version stays `0.2.4.0`.
  - **Close the side-by-side dev copy first** (`python tools/launch_dev.py --exit`), and wait until no test instance
    of another session runs. `install.ps1`'s `Stop-RunningApp` sends `--exit` to the default instance only, then
    waits 15 s for *every* `BetterClipboard.exe` in the session and force-kills the rest: the scoped dev copy and
    any test instance would be killed.
  - Pass Windows paths (`cygpath -w`): run as `-File C:/…/install-local.ps1` with forward slashes, Windows
    PowerShell 5.1 left `$PSScriptRoot` empty in its parameter defaults and stopped at binding, before changing
    anything. Passing `-ReleaseDirectory` and `-Installer` explicitly avoids the defaults altogether.
  - Then steps 3 and 4 above. The first start of a build with new integrations imports for real (the user's prompt
    histories, Win+R list); see the §5 row.

### 3.2 Chocolatey: the `betterclipboard` package (built 2026-10-01; not published yet)

User requests (2026-10-01):
1. "Learn about chocolatey's requirements to be used as a package manager so we can deliver installs for
   BetterClipboard through choco".
2. With the decisions answered: "Bring our repository to perfect preparation".

The full report (rules, Chocolatey's behavior, the package, the publishing checklist with the exemption text,
verification, sources) is [`docs/chocolatey.md`](docs/chocolatey.md). The package source is
[`packaging/chocolatey`](packaging/chocolatey).

**Decisions (the user, 2026-10-01).**
- Embed, as Chocolatey itself recommends when the license allows: "Chocolatey works best when the packages contain
  the software it is managing and doesn't require downloads".
- Start with Windows and launch by default.
- Give Win+V back on uninstall.
- Stable versions only.
- Owners and authors: Eli Belash.

**The gates** **[docs]**.
- **Automated, every version:**
  - the validator (rules CPMR0001-0076);
  - the verifier: a Windows Server 2019 (17763) VM runs `install --x86`, upgrade, install and uninstall, 20 min
    each, and re-tests every 2 weeks;
  - VirusTotal on the package.
- **Human, until trusted:** a moderator reviews every version until the package is marked trusted, a manual
  decision after a few versions approved without changes (also for vendors).
- **Deadlines:** an unanswered review gets a reminder after 20 days and is rejected after 35.
- **Limits:**
  - 150 MB per package documented (CPMR0028); the server takes 200 MB;
  - **no SemVer 2.0.0**, so `-rc.1` tags are left out;
  - the ID `betterclipboard` was free on 2026-10-01.
- **The verifier fails our install by design** (the app needs 19041). Ask for an exemption in the first review;
  `microsoft-windows-terminal` and `powertoys` are exempted today.

**Chocolatey behavior the package designs around** **[source 2.7.4 + verified]**. Measured with
[`probe_chocolatey.ps1`](tools/probes/probe_chocolatey.ps1) (28 of 28; re-run it after a Chocolatey upgrade).
- **Shims:**
  - every `*.exe` under the package folder is shimmed, recursively, unless `<exe>.ignore` exists;
  - `<exe>.gui` makes a GUI shim; GUI apps are not detected (a TODO in `ShimGenerationService`).
- **ARM64 counts as 32-bit** (`Get-OSArchitectureWidth`), so a 64-bit-only `-File64`/`-Url64bit` fails there. The
  package picks the archive by `RuntimeInformation.OSArchitecture` and passes it as the only file.
- **Upgrade, step by step:**
  1. the installed version's before-modify runs (it sees the old version);
  2. `lib\<id>` is moved to `lib-bkp` and copied back;
  3. files unchanged since the old install are deleted (`.files` snapshot);
  4. the new install script runs;
  5. `lib-bkp` is deleted.
- **A running app** is moved along with the folder: the upgrade still reports success, and the old binary keeps
  running from `lib-bkp`. Before-modify must close it.
- **Files written after install** survive the upgrade's copy-back (a before-modify state file reaches the new
  install script) and also the uninstall, which then leaves the folder. The uninstall script deletes them.
- **Exit codes:** Chocolatey fails a script on `$?` (its last statement), not on `$LASTEXITCODE`.
  `explorer.exe` exits 1 even when it worked.
- **Who installs:**
  - usually elevated, into `lib`;
  - HKCU and `LOCALAPPDATA` belong to whoever elevated (an admin typing credentials for a standard user: the
    admin's);
  - SYSTEM under Intune or an RMM tool;
  - non-admin installs work too.

**The package.**
- **Payload:** the release's `BetterClipboard-X.Y.Z-win-<arch>.7z` for both architectures, LZMA2 (0.2.4: 43.0 +
  39.0 MiB, against 70.5 + 68.0 as zips). The package is 82.0 MiB. The archives are GitHub release assets in
  `SHA256SUMS.txt`, and `legal/VERIFICATION.txt` lists their URLs and SHA-256 for moderators.
- **Install:**
  1. throws below build 19041;
  2. extracts the OS architecture's archive to `tools\app` with `Get-ChocolateyUnzip`, then deletes the archives
     and `install.ps1`;
  3. writes `BetterClipboard.exe.gui` and `.ignore` for every exe but `bclip.exe`;
  4. fresh install only:
     - a Start menu shortcut (every user's when elevated);
     - the app's own Run value for the desktop's user who installs; as SYSTEM, for another account's desktop, or
       with `/AllUsersStartup`, the PC's Run value for every user instead (`--all-users-startup`, needs administrator
       rights; since 2026-10-18, §2.34; before, such an install wrote nothing);
     - never taking over a shortcut or Run value that opens another existing copy;
  5. starts the app unelevated through Explorer and a temporary shortcut, only for the desktop's own user and only
     when no copy already runs in the session: plain after an install, `--background` after an upgrade when it
     was running;
  6. parameters `/NoStartup`, `/AllUsersStartup` (since 2026-10-18), `/NoShortcut`, `/NoLaunch`.
- **Before-modify:**
  - sends `--exit` (graceful) only when the package's copy is the only BetterClipboard in the session; force-stops
    after 15 s or otherwise;
  - writes `tools\upgrade-state.txt` (`running`/`stopped`);
  - never fails.
- **Uninstall:**
  - deletes the state file;
  - removes the shortcut and Run value where they point into the package; while `install.ps1`'s copy stays, the Run
    value is handed to that copy instead (since 2026-10-03, the second audit: both copies share one history, one
    settings file and this one value); the PC's Run value for every user likewise, handed to `install.ps1`'s copy for
    every user while that stays (since 2026-10-18);
  - gives back what `DisabledHotkeys` lists of Win+V and the Win+letter/digit shortcuts in the settings
    (`OpenHotkey`, `ExtraOpenHotkeys`, read from `settings.json` with `ConvertFrom-Json`; unreadable = Win+V only),
    keeping the user's other letters, and restarts Explorer only for the desktop's user, unless `/KeepWinVReleased`
    is given or `install.ps1`'s copy remains (since 2026-10-03; before, Win+V only);
  - keeps the history.
- **Not taking Win+V over at install** (unlike `install.ps1` since 2026-10-03): the hook answers Win+V anyway
  (`UseKeyboardHookFallback` defaults to on), and an Explorer restart in every unattended `choco install` would close
  the user's folder windows. Settings releases it on request. The owner's call if that should change (docs §4.2).
- **`install.ps1` respects a Chocolatey copy the same way** (§3.1).
- **Build and release:**
  - [`package.ps1`](tools/release/package.ps1) writes the `.7z` archives;
  - [`package-chocolatey.ps1`](tools/release/package-chocolatey.ps1) checks the archives against
    `SHA256SUMS.txt`, fills the template, strips its comments, writes `legal\`, saves the scripts ASCII + BOM,
    refuses SemVer 2 and over 150 MB, and packs;
  - [`test-chocolatey.ps1`](tools/release/test-chocolatey.ps1) installs, upgrades (with the app running) and
    uninstalls the exact `.nupkg`, on CI's and the release job's disposable runner only: it refuses to run
    elsewhere unless `-ConfirmMachineChanges`. Since 2026-10-03 it seeds BC-TEST neighbors in the Run key and in
    `Explorer\Advanced` and checks they survive (the wipe bug, §2.25, passed it before: it checked no other values in
    the key), and checks the give-back rule with Win+V, Win+Q (a settings shortcut) and Win+J (the user's own); it
    puts back everything it seeds. 28 checks; 32 since 2026-10-18, with a neighbor in the PC's Run key and
    `-AllUsersStartup` (CI runs it once without and once with it, §2.34);
  - `release.yml` pushes after `gh release create`, with the `CHOCOLATEY_API_KEY` secret;
  - [`chocolatey.yml`](.github/workflows/chocolatey.yml), run by hand (*Actions › Chocolatey package*): a package-only
    fix `<version>.<yyyyMMdd>` for a released version, from that release's `.7z` archives and the branch's scripts,
    tested the same way, attached to the release (unless it is immutable) and kept as a run artifact, pushed when the
    secret exists. The run summary has the link the verifier exemption request needs. Its `gh release` calls pass
    `--repo` (§4), and it refuses releases without one `.7z` per architecture (before v0.2.5).

**Verified (2026-10-01, Chocolatey CLI 2.3.0).**
- **The real package, 0.2.4 payload, in a private root, not elevated, next to the user's own `install.ps1`
  install:** 28 of 28 checks. Installed; upgraded to `0.2.4.1` with a scoped test instance running, which
  before-modify closed (no `lib-bkp`); uninstalled with `/KeepWinVReleased`, because the user has Win+V released.
  - The user's Start menu shortcut and Run value were not taken over. `DisabledHotkeys` and the app PIDs were
    unchanged.
  - Lesson: a non-elevated install writes the same Start menu path `install.ps1` uses. Hence the
    "never take over another copy" rule.
- **Build:** the refusals (SemVer 2, a missing architecture, a tampered archive), and `package.ps1`'s 7-Zip step
  (x64).
- **On GitHub:** `test-chocolatey.ps1` passed 22 of 22 checks on its first run (2026-10-02, CI run 37007600619,
  `543d2fd`, x64, as administrator):
  - the install for every user, with its shims, Start menu shortcut and Run value;
  - an upgrade with the app running, which before-modify closed;
  - the uninstall, with Win+V given back and the history kept.

  Whether that close was the graceful `--exit` or the forced stop, and the relaunch after the upgrade, are not
  asserted: they depend on whether the runner's session can run a WinUI app.
- **Re-review for submission (2026-10-03, "Rereview the repo for choco submittion readiness. fix any gaps"),** on a
  disposable claude-desktops VM (Windows 11 Pro 25H2, Chocolatey 2.7.4 like GitHub's runners, Windows PowerShell 5.1,
  elevated; deleted afterwards). The package-only fix `0.2.5.20261003`, built from `main`'s scripts and the v0.2.5
  release's archives:
  - `test-chocolatey.ps1` 28 of 28; the app started after the install and again after the upgrade; both closes were
    the graceful `--exit`; the uninstall gave back "Win+V and Win+Q" and restarted Explorer;
  - the original v0.2.5 package against the same test: "every other 'Start with Windows' entry kept" failed
    (`gone: BC-TEST neighbor`), exit 1 — the wipe bug reproduced;
  - SYSTEM (scheduled task) 13 of 13 and a second administrator in the desktop user's session 12 of 12: no Run value
    anywhere, nothing started, no Explorer restart. Found there: the log promised "It starts at the next sign-in"
    after a SYSTEM install, which writes no Run value — fixed;
  - the last message names the configured shortcuts (4 of 4).
- **Second audit (2026-10-03, "Second pass audit").**
  - **Validator:** the 78 archived rules' matching logic read (tarball of chocolatey/package-validator) and every
    script, file and nuspec rule applied to the built package: no Requirement fires; the expected Notes and the
    "too many lines" / "fourth script" Guidelines do. The 2021 code counts only the three named scripts (adding
    referenced ones throws), and its CPMR0018 and CPMR0046 could never fire (docs §2).
  - **Workflow fixes:** inputs read as `[string]` (an empty input can arrive as an absent variable); the release read
    through the REST API; `--repo` on `gh release` (without it the download failed outside a git folder); one `.7z`
    per architecture required; immutable releases skip the attachment; a run artifact. The two steps were run for
    real against v0.2.5 / v0.2.3 / a missing tag, and the version step with 10 cases.
  - **Coexistence gap fixed:** "Start with Windows" belongs to one copy; uninstalling that copy (the migration the
    package's warning recommends) left the other copy without autostart. Both uninstallers now hand the value over.
    `install.ps1`'s side: session 7579fd35's harness (copied, with a `-SeedRun` option) ran 5 hand-over cases and the
    peer's s16-s18 plus a full 0.2.4 install in PowerShell 5.1 and 7: all as expected, the user's real state unchanged.
    Then for real on a second VM (deleted afterwards): both channels in both orders, 26 of 26, each hand-over to the
    exact command line of the copy that stays.
  - **CI parity:** `test-chocolatey.ps1` under PowerShell 7.6.6 on that VM, 28 of 28 (the first pass ran it in 5.1
    only; CI runs pwsh). The community `powershell-core` package hung in the VM; Microsoft's MSI installed it.
  - **Found, outside the package:** every released `install.ps1` (v0.1.0-v0.2.5) wipes `Explorer\Advanced` on
    uninstall when `DisabledHotkeys` holds other letters besides V (§2.25), and the v0.2.5 page's `install.ps1` asset
    has both wipes.
  - **Moderation:** no rule against starting the app or a startup entry by default; the docs' silent-install
    expectation is answered with a prepared fallback (start into the tray, docs §5).

**Open: the user's steps** (the doc's §5).
1. Create the community.chocolatey.org account and the `CHOCOLATEY_API_KEY` secret.
2. ~~Push; check that CI's Chocolatey test passed.~~ Done 2026-10-02: 22 of 22.
3. ~~Tag a stable release.~~ Done 2026-10-03: v0.2.5. Its release job built `betterclipboard.0.2.5.nupkg` (82.1 MB),
   passed `test-chocolatey.ps1` (22 of 22) and attached the package to the release. It was **not pushed**: there is no
   `CHOCOLATEY_API_KEY` secret yet (the step's warning), as the user chose ("Can we do without choco first?",
   2026-10-02). **That package must never be pushed:** it carries the Run-key wipe (§2.25; the tag predates
   `34b1270`). 0 downloads on 2026-10-03.
4. Push the first package: *Actions › Chocolatey package* with version `0.2.5` (a package-only fix from `main`), or
   the next stable tag. Delete the old asset from the v0.2.5 page (`gh release delete-asset v0.2.5
   betterclipboard.0.2.5.nupkg`): its release notes still list it.
5. Answer the first review, asking for the verifier exemption, with the link from the pushing run's summary.
6. After approval, add `choco install betterclipboard` to the README.
7. Replace the v0.2.5 page's `install.ps1` asset with `main`'s after pushing (`gh release upload v0.2.5 install.ps1
   --clobber`), and release 0.2.6 soon: only an update replaces the installed copies' uninstaller (§2.25).

### 3.3 Code signing: SignPath Foundation (prepared 2026-10-02; not applied yet)

User request (2026-10-02): "I want you to go ahead and submit to get SignPath Foundation's signing for this project.
Let me know if we need to do some changes in order to pass their minimum requirements". The full packet is
[`docs/code-signing.md`](docs/code-signing.md): the conditions with their status, every form field with its answer,
reputation, the README section's three states, the artifact configuration and the release changes after acceptance,
the alternatives, what was verified, and the sources.

**What it is.**
- Free Authenticode signing for open-source projects. The certificate belongs to the SignPath Foundation, so Windows
  shows it as the publisher.
- Every release is approved by hand, and only binaries built by GitHub Actions from this repository can be signed.

**The application.**
- A HubSpot form at signpath.org/apply. Its 16 fields and their guidance are in SignPath's `OSSRequestForm-v4.xlsx`.
- Answers: Type Program, MIT, GitHub Actions, the repository as homepage, releases as the download page.
- **Not submitted.** The user decided on 2026-10-02, after the two gaps below:
  - publish the preparation now, and apply once there is reputation evidence;
  - the application's name: **Nucs BetterClipboard** (handle `nucs-betterclipboard`).

**The gaps.**
- **Reputation decides, and the repository cannot fix it.** "we cannot sign binaries based on source code that nobody
  knows … we require a certain verifiable reputation". The form wants evidence: media, Softpedia, downloads, GitHub
  Insights.
  - On 2026-10-02: 7 days old, 0 stars, 1–4 downloads per release file.
  - Two applicants with this profile were deferred or declined.
  - Building it, **without Chocolatey first** (the user, 2026-10-02: "Can we do without choco first?"). SignPath
    never asks for a package manager; Chocolatey was only one source of public download counts. GitHub's own
    numbers serve instead: stars, release download counters, Insights traffic and referrers.
  - The order: posts (r/Windows11, r/windowsapps, r/software, Show HN; from the maintainer's own accounts), then
    directory listings (Softpedia, which the form names; AlternativeTo), winget optionally, Chocolatey later.
- **The name.** "Better Clipboard" is also betterclipboard.com's Mac app, a Minecraft mod and an Electron library. The
  form asks to qualify such names. Decided: "Nucs BetterClipboard" for the application only; the binaries keep their
  product name.

**Already compliant, measured.**
- **Our six signed files agree on their metadata:** `BetterClipboard.exe`/`.dll`, `.Core.dll`, `.Windows.dll`,
  `bclip.exe`/`.dll`. `ProductName` is BetterClipboard; `ProductVersion` is `X.Y.Z+<commit>` (SourceLink appends the
  commit). Checked in the 0.2.4 release build and the 0.2.5 Debug build.
- **Everything else is the maker's own:**
  - of the 257 PE files in the 0.2.4 release, 247 carry their makers' signatures (.NET, Microsoft, the .NET
    Foundation);
  - only SQLite3MC, SQLitePCLRaw and, from 0.2.5, ZstdSharp are unsigned, which the terms allow for upstream OSS;
  - the Windows App SDK and the Windows SDK projection (Microsoft's license terms) are argued as GPLv3 System
    Libraries.
- **Network code: one HTTP client, since 2026-10-09** (§2.29). `Core/Updates/UpdateClient` asks GitHub for the
  release list once a day and downloads an update the user approved. There are still no sockets, no WebView and no
  telemetry, and links open only on a click.
  - SignPath's standard sentence ("This program will not transfer any information to other networked systems
    unless specifically requested by the user or the person installing or operating it") is therefore true only
    apart from the update check, and the README says it that way.
  - Their terms ask three things of software that transfers data to systems the user did not specify. All three
    are in place: the README's *Privacy* section describes the check and links GitHub's privacy statement; the
    installer shows a note about it, with that link, when it finishes; and `-NoUpdateCheck` installs with it off
    (*Settings › Updates* switches it later).
  - Until 2026-10-09 `src/` had no HTTP client at all, and the sentence stood alone.

**Changed for it** (this commit):
- README › *Privacy*: the sentence, plus a table of every local source with its default and switch. The Claude Code
  and Codex prompts, the shell histories and the Win+R list were not in the README before.
- README › *Code signing policy*: the status ("Not signed yet", signing planned), the six files, other makers' files
  never signed, manual approval, team roles, privacy.
- Every release page gets a "Code signing policy" footer (`release.yml`). The five published pages were corrected by
  hand (§3.1's lesson).
- The Chocolatey description links Privacy and the policy.
- Pushed on 2026-10-02 (`543d2fd`, 34 commits incl. other sessions' work, scanned for secrets first), so the README
  that reviewers will read is live.

**Open.**
- Reputation evidence, then the application (docs/code-signing.md §2 has every answer; §3's "applied for" wording
  goes into the README the same day).
- MFA: GitHub 2FA is on (confirmed by the user, 2026-10-02). SignPath's own MFA is set up at onboarding.
- After acceptance: the signing step in `release.yml` (publish → upload artifact → SignPath → archive the signed files
  → checksums), which needs `package.ps1` split into publish and archive halves.

### 3.4 README photos (made 2026-10-03, retaken 2026-10-09)

User request (2026-10-03): "Install current on dev and orchestrate two photos plus one settings photo for the
README.md". The README shows four photos, made on a claude-desktops Windows desktop with invented demo data, never on
this PC (§4):
- `docs/images/panel.png`, the hero: the All tab with the groups column (692×947).
- `docs/images/prompts.png`, under *Tabs for other apps*: the Claude tab, the strip scrolled to the other apps' tabs
  (626×947).
- `docs/images/settings.png`, after the Settings list under *Use*: the top of Settings (1388×1307).
- `docs/images/update.png`, under *Updates* (new 2026-10-09): the update dialog under the highlighted update button,
  on the wallpaper (960×1033).

They replace `flyout.png` (2026-09-25: six tabs, the removed "Windows clipboard history" label, real clipboard
content). "Current" was v0.2.5: `main` had no app change after it (docs only).

**Retaken 2026-10-09** with a package of `7d7df8d`: the header got its fifth button, the update button (§2.29), and
Settings its shortcut list (§2.25, §2.26). The three window photos kept their sizes to the pixel.

**How they were made.** Repeat it when the panel or Settings change visibly. The tools are in
[`tools/readme/`](tools/readme).
1. **Docker Desktop** must run on this PC (it does not start at sign-in, and here it opens its dashboard at start:
   `OpenUIOnStartupDisabled` false). Started with ShellExecute, then every new Docker window was minimized with
   `SW_SHOWMINNOACTIVE`, so the user's terminal kept the focus. The Windows host answered again after ~24 s.
2. **The desktop:** `create_desktop(os="windows", width=2560, height=1440)`. Settings › Display › Scale 150 % (UIA:
   expand the Scale combo box, select "150%"), then `launch C:\Windows\Resources\Themes\dark.theme` (dark mode and the
   Dark Bloom wallpaper). At 150 % the photos stay sharp on high-DPI screens. The Settings window (940×880 DIP) needs
   1,320 px of height, hence 1440p.
3. **Demo data:** copy `tools/readme/*` to the desktop's persist folder (`Q:\claude-desktops\windows\desktops\<name>\persist`
   = `Z:`) and run `demo-data.ps1` in the desktop before the app's first start, so that its first import finds the
   prompt histories. With the app already running, restart it afterwards (`--exit`, `--background`).
4. **Install.**
   - A released version: the README's own command (`irm …/install.ps1 | iex`; v0.2.5 on 2026-10-03, SHA-256 checked,
     12 s).
   - Code that is not released: `tools/release/package.ps1 -Version X.Y.Z -Architectures x64`, then the zip,
     `SHA256SUMS.txt` and `install.ps1` in a new top-level folder of the persist folder, and in the desktop
     `install.ps1 -Package <zip> -NoLaunch` (29 s) and `BetterClipboard.exe --background`.
5. **Copies from real apps,** so every card names its app: Notepad lines (Home, Shift+End, Ctrl+C), a selection in
   Windows Terminal (drag, Ctrl+C), Explorer files (Ctrl+A, Ctrl+C), a Snipping Tool snip of the wallpaper, Edge's
   address (Ctrl+L, Ctrl+C). Spread over ~20 minutes, so the captions read "just now" to "19 min ago". Then pin the
   reply (Ctrl+P), make three groups with **+**, and drag cards onto them. On 2026-10-09 the Terminal selection was
   left out (7 items): no photo shows that card.
6. **Size:** `FlyoutWidth` 432 and `FlyoutHeight` 640 in `settings.json`, written while the app is stopped. At 432 DIP
   both strips end on whole tabs ("… Files Snipping ›" at the start, "‹ Files … Codex" at the end); at 400 they read
   "Snippi›" and "‹ s Snipping".
7. **The snip's shape** must match the card's thumbnail box (about 2.85:1 at 432 DIP, so 1426×500 px), or gray bars
   frame the picture.
8. **Capture** with `capture-window.ps1`, the pointer away from the window (no hover, no image peek):
   - hero: Win+D, pointer at (800, 250), Win+V (the panel opens at the pointer, over the wallpaper), the groups
     column open (Ctrl+G; it is remembered from the drags), Ctrl+F;
   - Claude tab: close the column (Ctrl+G), then Left twice from the All tab (the tabs go round, §2.28: Codex, then
     Claude). The strip follows the selected tab and the search box keeps the keyboard. Before the tabs went round:
     click › until it is gone, select the Claude tab through UIA, Ctrl+F;
   - Settings: a plain launch, then one click in its hero area, and the pointer off the window.
9. `finish-shot.py` on each capture.
10. **The update dialog** is a window of its own and larger than the panel, so its photo is a piece of the screen.
    - **The offer.** A build labelled with an older version (`package.ps1 -Version 0.2.3`), installed as the regular
      copy, is offered the real latest release by the real feed after *Settings › Updates › Check now*. The dialog
      then shows a real release with its real notes: nothing is invented. *Update and restart* would hand over to
      that release's installer (§5 has what 0.2.5's does).
    - **A plain button for the other photos.** `install.ps1` gates `-NoUpdateCheck` on the release being ≥ 0.2.6,
      so a build labelled below that is switched with `BetterClipboard.exe --set-update-check off` before its first
      start. For this photo the switch goes on again in Settings.
    - **Two captures** with `capture-screen.ps1`: the panel with the dialog open (it prints both windows'
      rectangles), then the same screen after Esc, Esc (the wallpaper alone). The pointer stays off both.
    - `compose-shot.py <with> <without> docs/images/update.png --window <the panel's rectangle> --popup <the
      dialog's>`: the panel's corners become round and DWM's border band goes, the dialog keeps the corners and
      shadow XAML drew, and the cut leaves 32 px of wallpaper around what is visible.
11. `delete_desktop(delete_persist=True)`.

**Lessons** (each one cost a retake):
- **No GPU in the VM:** acrylic and Mica draw their solid fallback colors, and DWM draws square corners with a
  half-transparent 2-px border band. `finish-shot.py` trims the band and rounds the corners (8 DIP, the hardware look).
- **Selecting a tab through UIA does not scroll the strip to it** (only `SelectFilter` from code reveals the tab), and
  it leaves a keyboard focus rectangle on the tab. Scroll with the arrows first; Ctrl+F gives the focus back to the
  search box.
- **Settings opened after keyboard input** shows a focus rectangle around its whole scroll area
  (`Scroller.Focus(FocusState.Programmatic)` keeps the keyboard look). One click in the window clears it.
- **Win+Shift+S:** wait ~2 s for the overlay before the drag, or the drag does nothing. One snip failed: Snipping Tool
  exited and left a 0-byte file and clipboard formats without data. The app logged "Gave up on a screenshot file that
  stayed incomplete or locked for 10 s", which is correct.
- **Units:** the desktop's UIA bounds are DIPs (×1.5 for pixels); its screenshots, clicks and drags are pixels. Its
  `scroll` takes dy from −50 to 50, positive = down.
- **Found on the way:** the first Win+V after a start does not focus the search box (§6).
- **A flyout's shadow changes bright wallpaper more than its body changes dark wallpaper,** so no amount of
  difference tells the two apart. `compose-shot.py` finds the body by direction: a shadow only darkens, and a
  dark-theme bubble is lighter than a dark wallpaper in at least one color.
- **Found on the way (2026-10-09):** a second selection bar after leaving a prompt or shell tab (§2.27, fixed in
  `2f86791`). Look at each capture for it before it becomes a photo.

**Retake `update.png` with the first real update** (§6). It shows a build labelled 0.2.3 that is offered 0.2.5. With
0.2.6 installed and a newer release out, steps 10 and 11 give a true pair without a labelled build.

### 3.5 Microsoft Store: the MSIX package, the submission and the release pipeline (2026-11-13; a filled draft, not submitted)

User request (2026-11-12): "I want to publish my product to microsoft marketplace". The app side is §2.47; this section
is the package and the way into the Store. The full report is [`docs/microsoft-store.md`](docs/microsoft-store.md), the
texts for Partner Center are [`docs/store-listing.md`](docs/store-listing.md), and the package source is
[`packaging/msix`](packaging/msix).

**The gates** **[docs]**, read 2026-11-12 (the doc's §9 has every address).
- **Account:** free for individuals; registration at `https://storedeveloper.microsoft.com` (a Microsoft account, an ID,
  a selfie).
- **Name:** reserved in Partner Center before anything is submitted; a reservation unused for three months is removed.
  "Better Clipboard" is already the title of another publisher's app (PAST-E Clipboard, `9NR75C5JPVVX`). Partner
  Center reserved "BetterClipboard" all the same (2026-11-13); certification may still ask. The fallback is "Nucs
  BetterClipboard" (the name chosen for code signing, §3.3).
- **Package:** MSIX, at most 25 GB; the Store signs it after certification; its identity must be the reserved one.
- **Version:** four numbers, the fourth must be 0, and **the first cannot be 0**.
- **Listing:** a description of at most 10,000 characters without web addresses, up to 20 features of at most 200
  characters, at least one PNG screenshot of 1366 × 768 or larger, a privacy policy address (the README's *Privacy*
  section), an age rating from the IARC questionnaire, and a justification for the restricted capability
  `runFullTrust`.
- **Policies** (Microsoft Store Policies 7.20) that shaped the edition: 10.2.8 (consent and supported methods for any
  change to Windows), 10.5.5 (express consent before sensitive data is stored), 10.5.1 (privacy policy), 10.1.1 (a clear
  first run, a unique name), 10.2.7 (clean uninstall), 10.6 (capabilities), 10.7 (localized descriptions), 10.2.2 (no
  code downloaded that changes the product).

**The package.**
- **Manifest** [`Package.appxmanifest`](packaging/msix/Package.appxmanifest), a template the build fills in: one
  application (`BetterClipboard.exe`, with the startup task `BetterClipboardStartup` and the console alias `bclip.exe`,
  which starts `bclip.exe`), `runFullTrust` only, the five languages, Windows 10 2004 (19041) or later. Until
  2026-11-13 `bclip` was a second, hidden application: the Store refused that (§2.47).
- **Identity** [`identity.json`](packaging/msix/identity.json), as Partner Center assigned it (none of it a secret):
  name `Nucs.BetterClipboard`, publisher `CN=B31AB4B1-B96C-4DB9-83AE-B799D5192208`, display name "Nucs", family name
  `Nucs.BetterClipboard_mgez7y7xa0my4`, Store ID `9PHL574X9BF9`. The build computes the family name from name and
  publisher and refuses a Store package when it differs from the file's.
- **Version: (X+1).Y.Z.0** for the release X.Y.Z (0.2.11 ships as 1.2.11.0), for every release, for good. The Store
  refuses a first number of 0 and only ever offers the highest version, so a rule that stopped adding at 1.0.0 would
  make that package older than the last 0.x one. The owner chose this over releasing 1.0.0 first (2026-11-13,
  below).
- **Logos:** [`tools/make_store_assets.py`](tools/make_store_assets.py) draws the 35 PNGs of `packaging/msix/Assets` from
  the app icon's own drawing, and the listing's 300-pixel tile icon into `packaging/msix/Listing`.
- **Build:** [`tools/release/package-msix.ps1`](tools/release/package-msix.ps1) `-Version X.Y.Z`, per architecture:
  1. publishes `bclip` self-contained;
  2. fills in the manifest (identity, version, architecture);
  3. publishes the app with `WindowsPackageType=MSIX`, `StoreAppxManifest` and `StoreExtraFilesDir` (`bclip` and
     `install.ps1` ride along in the package root), into `artifacts/store-build` (obj and bin of their own:
     `Directory.Build.props`), which makes the `.msix`;
  4. checks the package: identity, version and architecture as asked; nothing in the manifest that the Store refuses
     at upload (one application, none hidden from Start, `bclip` as its console alias); the app, `bclip`, `install.ps1`
     and the resource index inside; under 250 MB.
  - Output: `artifacts/store/BetterClipboard-<version>-store-<arch>.msix` (x64 70.6 MB, ARM64 68.5 MB).
  - `-ForStore`: unsigned (the Store signs), and refused while the identity is a placeholder.
  - `-NewTestCertificate` / `-TestCertificate`: signed for a test desktop; the certificate's subject becomes the
    publisher. A new certificate is made in memory and written to a `.pfx` and a `.cer`: nothing is added to a
    certificate store of the build PC.
- **Trying a package: only in a disposable test desktop** (§4). `Import-Certificate` of the `.cer` into
  `Cert:\LocalMachine\TrustedPeople`, then `Add-AppxPackage <msix>`. For repeated tries, unpack it and register the
  folder (`Add-AppxPackage -Register <folder>\AppxManifest.xml`, Developer Mode): a changed file is then replaced in
  place.
- **The listing kit** [`docs/store-listing.md`](docs/store-listing.md) is the source of every text in Partner Center.
  [`check-store-listing.py`](tools/release/check-store-listing.py) checks it against the Store's limits,
  [`make-store-listing-csv.py`](tools/release/make-store-listing-csv.py) fills Partner Center's exported listings file
  from it, and with `--check` says whether an export is what the kit says (below).

**The submission in Partner Center (2026-11-13).** Made through the owner's Chrome, in a tab of its own (skill
`browser`); `docs/microsoft-store.md` §8 has the table of pages.
- **The draft** ("Submission 1") holds: free, all markets; Utilities + tools and Productivity, with the privacy,
  website and support links; both packages (`1.2.11.0`, the v0.2.11 release's own since 2026-11-13); five listings
  with texts, search terms, eight screenshots, captions and the tile icon; the `runFullTrust` justification; the
  notes for certification.
- **Submitted for certification on 2026-11-13, about 22:53 local**, by Claude in the owner's browser on the owner's
  word ("You tick and submit … you the asisstant do it all for me through browser"): the IARC agreement ticked and
  saved (the rating: Everyone, 3+ and All ages in every system), every section *Complete*, then *Submit for
  certification*. The overview read "Product submission: In certification" and every section *Read only*.
  It publishes itself once certified (Submission options).

**Facts about Partner Center** **[verified 2026-11-13]**:
- **Listing languages appear only after packages are uploaded**: they are the manifest's `Resource` languages.
- **The upload check** names what the Store refuses (the hidden application, §2.47) within about two minutes. A
  restricted capability is only a notice there.
- **The `runFullTrust` justification holds at most 500 characters**: "The justification must be 500 characters or
  fewer", in red under the box, and the page saves nothing. The kit's first text had 993.
- **The notes for certification** have a page of their own (*Supplemental info › Additional Testing Information*)
  with its own *Save description* link.
- **Utilities + tools has two subcategories** (*Backup + manage*, *File managers*); none is required.
- **The age ratings** are saved only with a tick: "I agree to the IARC Terms of Use and I am the age of majority".
- **Listings can be exported and imported** (the overview's *Export listings* and *Import listings*): a `.csv` with
  one row per field (`Field, ID, Type (Type), default, <languages>`; 430 rows), written and read with PapaParse:
  UTF-8 with a byte order mark, quoted fields may hold line breaks. An import saves nothing unless the whole file is
  valid. Pictures are web addresses in an export and can be copied between the language columns: uploaded once in
  the English listing, they served all five.
- **An import does not keep the order of the screenshots** (three imports, each followed by an export).
  - After the import that added the captions, the English listing showed Settings first and the main panel last,
    although the file had them in the kit's order. After the next import the Spanish and the Hebrew listing were
    out of order too, although the file had not changed them, and the English one was still wrong, although the
    file asked for the kit's order. Two exports in a row were identical, so it is the import.
  - A caption stayed with its picture every time: the import keeps the pairs of a file's rows, not their order.
  - So `make-store-listing-csv.py` writes each caption under its own picture. It tells the pictures apart by the
    captions they already have, by `--screenshots-in-order LANGUAGE`, or by the languages agreeing, and stops
    when it cannot. `--check` compares an export with the kit and names the languages that are out of order.
  - The script's first version wrote captions by position. Its file for the third import would have put five
    English captions under the wrong pictures; a cell-by-cell comparison of that file with the export showed it
    before anything was uploaded.
- **The order is put right on the listing page of each language.** A screenshot dragged onto another one changes
  places with it, and the page saves that at once, without *Save*. Until the page is loaded again it shows *Add
  image caption* on the cards it moved; the captions are still saved with their pictures.
  [`order-store-screenshots.js`](tools/release/order-store-screenshots.js) does it with the page's own drag
  events. The browser tools' mouse drag exchanged the first two cards instead of the sixth and the first.
- **A Hebrew Store page shows "Notepad++" as "++Notepad"** unless a left-to-right mark (U+200E) follows the
  signs: the app's own fault of 2026-11-09 (§2.37), found again in a render of the Hebrew listing. The kit's reader
  adds the marks to a right-to-left listing's shown texts (never to the kit itself, and never to search terms), and
  an export showed that Partner Center keeps them.
- **A listing page's picture inputs are hidden** (`input[type=file]#image-upload-input`, one file each; the first is
  the desktop screenshots', the fifth the 300 × 300 tile icon's).
- **Replacing every language's screenshots** (done 2026-11-13, six old ones for eight new):
  - On the English listing page: each screenshot card (`div.listing-image-inner` in a `.screenshot-swap-container`)
    has a delete icon (`he-icon[name=delete]`); a click removes the card at once, without a question. The empty
    card after them has the upload input: shown and labelled, it takes one file per `upload_file`, keeps its
    snapshot uid between uploads, and every upload adds a card at the end. *Save* goes back to the overview.
  - In the other languages the old pictures stay until an import says otherwise: export, empty every
    `DesktopScreenshot*` cell of the other languages (session 66cdd808's scratchpad,
    `store/empty_other_pictures.py`), then `make-store-listing-csv.py --copy-pictures-from en-us --screenshots 8
    --screenshots-in-order en-us`. One import then gave all five languages the same eight assets with their
    captions.
  - The import shuffled the English order again (4, 5, 6, 1, 2, 8, 7, 3; the other languages stayed in order):
    `order-store-screenshots.js` put it right in four swaps, and `--check --screenshots 8` passed.
- **The import dialog** (*Import listings*) holds `input#listing-import-filename`: open the dialog, then show and
  label the input, take a snapshot and `upload_file` on it. `upload_file` on the dialog's *Upload .csv* button
  fails ("clicking it did not trigger a file chooser"), and so does the link itself; neither left a native dialog
  (checked by listing Chrome's `#32770` windows). The import ran although the page showed no message: export again
  to see what it did.
- **chrome-devtools-mcp could not attach for ~10 minutes** that evening: every call answered "Log.enable timed
  out" while the owner had 41 tabs open. Stopping this session's own backend (its `node.exe`, a child of this
  session's `lazymcp.exe`) made the next call start a new one, which took three minutes to answer and then worked.
  A screenshot of a background tab (`take_screenshot`) timed out too; reading the page with `evaluate_script` did
  not.

**The listing's screenshots** (retaken 2026-11-13 on the released 0.2.11, the owner's choice "Retake on 0.2.11,
English": eight PNGs of 2560 × 1440 in `packaging/msix/Listing/Screenshots`, replacing the first six of the same
day). Whole screens, because the Store wants 1366 × 768 or more. Repeat it when the panel, Settings or the welcome
change visibly. The README photos' way (§3.4), with these differences:
1. **The data** is [`tools/readme/demo-data.zip`](tools/readme/demo-data.zip), loaded by
   [`tools/readme/seed-demo.cs`](tools/readme/seed-demo.cs) (both since 2026-11-13). The owner asked for it: "Copy
   the data from my computer (export and import, idk, figure it out) and then delete the sensitive in view and just
   keep the one that open source and have it cool, also populate then zip and commit it … after removing ALL
   sensitive and guids and strings that are private or keys and so on. Maybe even take a period and a minimum of 30
   of each tab (cleaned 30)".
   - **Where it came from:** the owner's own store, exported read-only by a scratch reader that opens it with the
     store's own key hierarchy (session 66cdd808's scratchpad, `demo/owner_store.cs`; nothing written). Then a
     screen of rules (people, accounts, addresses, keys, private projects and hosts; URLs only to public hosts;
     paths only inside the four public repositories) and every kept item read by eye. The automatic screen alone was
     not enough: it passed NSFW model names, a street address, trading notes, a router password and pentest notes,
     all of which the reading caught.
   - **What it holds:** 214 entries (184 texts, 30 pictures), 2 pins, 4 groups, 55 Claude Code and 44 Codex
     prompts, 40 PowerShell commands. Items marked `"generic": true` are not the owner's: 20 common Windows
     commands, 5 public documentation links and 17 paths of real files in the public repositories, added where the
     owner's history held fewer than 30 clean items. The pictures are the public repositories' screenshots and
     documentation images. `ABOUT.md` in the zip names every source and license, and why Pinned, Snipping and
     Notepad++ hold fewer than 30.
   - **Its last checks** (the scratch builder `demo/build_demo.py`): the screen's rules again on every text, a word
     list, and identifier patterns (GUIDs, a GUID's 8-digit head, long hex, key prefixes) with public URLs left out.
     A kept Notepad++ note held a session id's head ("fork 358db618") and was dropped.
   - **The loader** writes the store through `ClipHistoryService` (the app's own classifier, picture analysis and
     path counts), the agents' history files and PSReadLine's file (the app's first start imports them), the
     screenshots into `Pictures\Screenshots` with their files, and the settings (English, dark, the welcome
     answered, 432 × 640). It refuses anywhere but a claude-desktops desktop and over existing data; `--check`
     reads the whole set and writes nothing.
2. **The app** is the release's own Store package, test-signed: `package-msix.ps1`'s two helper functions (taken
   from its syntax tree) make a certificate for the subject `CN=B31AB4B1-…` (the Store's publisher) and SignTool
   signs a copy (session 66cdd808's scratchpad, `demo/sign_release.ps1`). It then installs with the Store's identity
   and family name (`Nucs.BetterClipboard_mgez7y7xa0my4`), so the loader's `--package-family` finds the data folder.
   `Import-Certificate` into `Cert:\LocalMachine\TrustedPeople`, `Add-AppxPackage`, then
   `explorer.exe shell:AppsFolder\<family>!App`.
3. **The desktop:** `create_desktop(os="windows", width=2560, height=1440, memory_gb=6, host="pc")`, the dark theme
   (`launch C:\Windows\Resources\Themes\dark.theme`), scale 150 % (Settings › Display, the *Scale* combo box through
   UI Automation). On the worker a package install took 8.5 minutes the same day (its drive was starved).
4. **Order of work:** start the app by hand; the language picker; the welcome, captured before anything else;
   *Not now* (nothing recorded); the loader; start by hand again (Settings opens: close it, the silent restart
   follows); then a short document in Notepad (`NumSharp 0.80 release.txt`, zoom 140 %, the caret at the end of
   "Release notes: ").
5. **The scenes**, in the order of the pictures: (1) the panel at the caret, the newest copy selected under two
   pins; (2) the regular expression `np\.\w+` with `.*` on; (3) F3 on the "missing dtype slots" note: the text
   viewer drawing its Markdown; (4) F3 on the hourglass screenshot: the image viewer, zoomed to 173 % with the
   wheel, its file band on top; (5) Ctrl+G and a click on the NumSharp group; (6) Left twice to the Claude tab, then
   the strip's ‹ once, so every tab label is whole; (7) Settings scrolled to *Privacy*, 1140 px tall so that the
   *Encrypted history* card (it shows the store's id, a GUID) stays out; (8) the welcome. For (3) and (4) Notepad
   was moved right first: the viewers are centered on the panel, and the panel opens at the caret.
6. **Captures:** `tools/readme/capture-screen.ps1` with the window showing, and again with it closed (one capture
   without serves every scene of one Notepad position); `tools/readme/screen-shot.py` rounds the window corners.
   Scenes (3) and (4) are used as captured: the viewers dim the whole screen, and rounding the panel under the dim
   would paste undimmed pixels.
7. **Lessons:**
   - A start by hand opens Settings, and closing it restarts the app silently a few seconds later (§2.46): wait.
   - The first summon of a session draws one frame of unbound defaults (the "Paused" chip, an empty list, *Start
     Everything*): §6. Wait a second before the capture.
   - The mouse wheel over Settings' ignored-apps list scrolls the list, not the page: wheel over another card.
   - Esc closes a viewer, then clears the search, then closes the panel: count them.
   - A capture lists only BetterClipboard's visible, uncloaked windows; a concealed panel is not one of them.

**The release pipeline** (`docs/microsoft-store.md` §7 has it in full).
- **In the release run:** `package-msix.ps1 -ForStore` before anything is published (the artifact `store-packages`),
  and `submit-store.ps1` as the last step: a new submission made from the published one, the two new packages,
  "What's new" from the tag's notes (1,500 characters at most), the commit, and the status followed into
  certification.
- **Exit codes:** 0 submitted; 1 the Store refused, or the commit failed; 2 wrong packages or arguments; 3 the
  `PARTNER_CENTER_*` secrets are not set; 4 the product has no published submission yet. The release run warns for
  3 and 4 and is red for the others.
- **By hand:** *Actions › Microsoft Store* (`store.yml`): `check`, `submit`, `draft`.
- **Not connected yet.** Microsoft's API needs a Microsoft Entra application with the Manager role, in a tenant
  associated with the developer account, and it only updates a product that was published once.
  - Associating a tenant asks for a *work* account of that tenant: the owner's personal Microsoft account was not
    taken, although it administers their Azure directory **[verified]**. A Global administrator is not needed for
    that step **[docs]**.
  - Adding the application asks for more **[docs]**: "You must be signed in with a Manager account that also has
    global administrator permissions for the Microsoft Entra ID tenant you're working in." Such an account has to
    set up multi-factor authentication at its first sign-in (security defaults: the Authenticator app, or any
    TOTP app).
  - The owner's Azure directory holds one user, their personal account **[measured]**, so a work account has to
    be made first. The plan is `docs/microsoft-store.md` §7: the work account and the application made with `az`,
    one sign-in by the owner, the three secrets by pipe so that the key is never shown. It waits for the owner's
    word, like the release.
- **Why a script of our own and not Microsoft's `msstore` CLI:** its `publish` takes one package file, and this
  edition ships one per architecture without a bundle.
- **Tested without the service** (`test-submit-store.ps1`, 39 checks, in CI): a stand-in on this PC answers like the
  documented API and records every request.

**The owner's decisions** (2026-11-13; four HTML questions with the whole listing as review pages, made from the
kit by a scratch generator, session 66cdd808):
- **The Store page: "Change something"**, with the comment "We must replace the images BUT only after we do
  everything else - ask me then again about it". Nothing else was asked to change: name, publisher, texts,
  category, links and price stand. **The six screenshots are to be replaced, last, and only after asking the
  owner again what they want instead.**
- **Translations: "All five languages"**, with "Open gh issue per language (except hebrew and chinese) asking for
  translator contributor to review existing translation". Done: issues #4 (Russian) and #5 (Spanish), labels
  *help wanted* and *good first issue*. Each names the app's catalog, quotes that language's Store description
  (not published yet), says what to look for and how to send corrections, and mentions nobody.
- **Version: "Keep 0.2.x"**, with "Cant the store be 0.2.11.0? if no, all good." It cannot. Microsoft: "the last
  (fourth) section of the version number is reserved for Store use and must be left as 0 when you build your
  package … The other sections must be set to an integer between 0 and 65535 (except for the first section, which
  cannot be 0)." So the rule above stands for good: release 0.2.11 is Store package 1.2.11.0.
- **Go-ahead: "Prepare, I submit"**, with "I will tell you explicitly to release and then to publish. just prepare
  all as we still have working going on and maybe use transcript of this session 2e2fae95-… to see how release
  notes are formed." So: **no push, no tag, no release and no submission until the owner says each in words.**
  The owner ticks the IARC agreement and presses *Submit for certification* themselves. Prepared: the version
  bump to 0.2.11 with the notes draft in its message, in v0.2.10's shape (that session's transcript: the bare
  title line, the install command, a bold lead-in per feature, **Fixed**, the files table, the signing note,
  nothing that names an older version).
- **Then, after v0.2.11 was released** (2026-11-13: "We have a pipeline running now and we want to release to store
  so you may continue"; two more HTML questions):
  - **Screenshots: "Retake on 0.2.11, English"**, with the data request quoted under "The listing's screenshots"
    above. Done: eight scenes, all five listings show the same English pictures.
  - **The last clicks: "You tick and submit"**, with "to clarify: you the asisstant do it all for me through
    browser". So the IARC agreement and *Submit for certification* are no longer the owner's to press: Claude
    does both in Partner Center, once the screenshots are in place. This replaces "Prepare, I submit" for this
    first submission.

**Open** (`docs/microsoft-store.md` §8).
1. ~~On the owner's word: the release (§3.1). Its `store-packages` artifact replaces the draft's packages.~~ Done
   2026-11-13: v0.2.11 released (run 43478898156); its two packages (SHA-256 checked against the run's artifact)
   replaced the draft's local builds and were validated.
2. ~~The screenshots: ask the owner what replaces them, retake, upload, order, check (`--check`).~~ Done
   2026-11-13: eight scenes, uploaded once in English, copied into every language by one import, ordered, and
   `--check --screenshots 8` passed.
3. ~~The IARC tick and *Submit for certification*.~~ Done 2026-11-13, about 22:53 local: "In certification".
4. **Certification** (up to a few business days; a first submission with a restricted capability may take
   longer). A failure comes with a report in Partner Center: fix and resubmit. It publishes itself when certified.
5. The pipeline's connection, on the owner's word and with one sign-in by them (the plan:
   `docs/microsoft-store.md` §7): a work account in the owner's Azure directory, the tenant associated with the
   developer account, the application with the Manager role, the three secrets, then *Microsoft Store › check*.
   Submitting through it only works once the first submission is published.
6. After approval: the Store link in the README and in the installer's warning.

---

## 4. Conventions (must follow)

- **Documentation is mandatory** (user rule): every member gets XML docs whose summary explains
  consequence/tradeoff; every param/return/exception documented; non-obvious bodies get *why* comments.
  Core and Windows fail the build on CS1591.
  - Count doc warnings on a full rebuild (`--no-incremental`). An incremental build prints warnings only for the
    projects it recompiles: on 2026-10-01 a "0 doc warnings" check on an App build missed seven broken `cref`s in
    Core and Windows, which the release publish showed. Doc warnings are CS1570–CS1599 plus CS0419 (an ambiguous
    `cref`, e.g. once a second overload exists: name the signature, `Read(Stream, TailFileState, …)`).
- **Namespace trap:** inside `BetterClipboard.*`, `Windows.Foundation…` resolves to our
  `BetterClipboard.Windows` namespace. Use `using Windows.X;` at file top or `global::Windows.X` inline.
  Also avoid `using Windows.UI.Core;` in WinUI files (`WindowActivatedEventArgs` becomes ambiguous).
- **P/Invoke:** `LibraryImport` only, blittable signatures (`[Out] char[]` needs runtime marshalling —
  use `char*` + `fixed`). Read `Marshal.GetLastPInvokeError()` before any other call. Never let exceptions
  escape WndProc/hook callbacks (they are wrapped). Structures from richedit.h are 4-byte packed (`pshpack4.h`): declare
  them with `Pack = 4`, or on x64 `EDITSTREAM`'s callback lands at the wrong offset and Control Flow Guard ends the
  process (0xC0000409, no message; §2.33).
- **XAML build errors cascade:** `WMC1509 No LocalAssembly…` + dozens of "Unknown type" errors mean a C#
  error broke the XAML pre-compile — fix the first `CS####` error, not the XAML.
- **A window-level key model must skip keys typed into popups.** A `Flyout`/`MenuFlyout` popup is parented
  to its placement target, so `PreviewKeyDown` on any ancestor of the target sees every key of the popup,
  and sees it first. Without a guard, Esc, Enter or Delete meant for a menu or for a text box in a flyout
  act on the window. `ClipboardFlyout.IsPopupKey` is the pattern (§2.11). The same goes for context-menu
  keys: a `TextBox` answers the Menu key with its own `ContextFlyout` before the request bubbles.
- **No editable `ComboBox` bound through `Text`.**
  - What went wrong: when its template loads, WinUI fills the inner text box from `SelectedItem`, so a
    value bound before that shows blank. In v0.2.0 the Settings shortcut box was empty for both custom
    and preset shortcuts (reported 2026-09-25).
  - What we use instead: a `TextBox` (two-way `Text`, commits on focus loss) + Enter-to-commit + a
    `DropDownButton` with a `MenuFlyout` of presets.
  - Preset labels are run through `HotkeyGesture` so they match the saved canonical form
    (Ctrl, Alt, Shift, Win, key); otherwise "Win+Alt+V" turns into "Alt+Win+V" right after picking.
  - Checked by screenshots and a guarded input script on an isolated instance.
- **Glyphs:** Segoe Fluent Icons via `\uE8xx` escapes in C# and `&#xE8xx;` in XAML (raw PUA characters are
  invisible in diffs). Code points used: Paste E77F, Copy E8C8, Pin E718, Unpin E77A, PinnedFill E842,
  Delete E74D, Setting E713, Link E71B, Photo E91B, Folder E8B7, Font E8D2, FontColor E8D3, Color E790,
  History E81C, Clock E917, Pause E769, Play E768, Keyboard E765, KeyboardShortcut EDA7, FileExplorer EC50,
  Personalize E771, Shield EA18, Import E8B5, Info E946, OpenInNewWindow E8A7, Code E943, Power E7E8,
  Camera E722 (ShareX card), Add E710 (new group), Tag E8EC (Groups submenu), Rename E8AC, Remove E738,
  Blocked E733 (Forget forever; a circle with a slash, checked by rendering), and for the Everything tab
  (checked by rendering): Document E8A5 (a file pick), Search E721 (Settings card, *Show in Everything*),
  OpenFile E8E5 (*Open*), Hide ED1A (an eye with a slash: *Hide until opened again*), and for Settings › Third
  party (checked by rendering): Puzzle EA86 (*Works with*), Library E8F1 (*Built with*), and for the image overlays
  (§2.24, checked by rendering): RedEye E7B3 (the eye that opens the image viewer), Cancel E711 (the viewer's close button), and for updates
  (§2.29, checked by rendering): Sync E895 (two arrows in a circle: the update button and the Settings card), and for
  a picture's file (§2.49, checked by rendering): E62F (a box holding "\.."; *Copy path*), FolderOpen E838 (*Open
  folder*) and SaveLocal E78C (*Save to temp and*, and its Settings card); *Copy image as a file* and *Open file* reuse
  Document E8A5 and OpenFile E8E5.
  Group icons: `Core/Presentation/GroupIconCatalog`. Raw PUA characters slip into sources easily: twice
  on 2026-09-25 they landed in string literals, once a raw U+2009 thin space did, and on 2026-10-01 nine of
  them in the pick menu (the editing tool turned `\uXXXX` written in an edit into the raw character), and five more
  in a new file written whole (the prompt card menu). Sweep new C# files with an escape script before committing (never XAML files: there the escape is `&#xE8xx;`). The
  reverse trap: `\u2014` inside a `///` comment stays those six characters — comments take no escapes.
- **A `PathIcon` needs its design box as `Width`/`Height`.**
  - Why: WinUI draws its path as a `Stretch=None` shape at the geometry's own coordinates and measures it as
    the geometry's **right/bottom edge**, not as a design box (`CShape::MeasureOverride` in
    microsoft-ui-xaml). A 16-unit icon whose ink spans x 3–13 therefore measures 13 wide, and a parent that
    centers it pushes the ink right and down.
  - What to do: give it the box the geometry was drawn in (`Width="16" Height="16"`) and keep the ink
    centered inside that box.
  - Lesson (2026-10-01): the Groups toggle's ribbon sat 2 px right of and ~0.8 px below its button's center
    (margins left 14 / right 10). The 2026-09-25 screenshots already showed it, but nobody measured them.
    Measure icon placement inside a button from a screenshot. The highlight of an "on" state or of a hover
    shows the button's bounds.
- **SendInput from Python needs the real `INPUT` layout:** 40 bytes on x64 (type + padding + a 32-byte
  union). A struct with the member inlined plus extra padding was 48 bytes; `SendInput` then returns 0 and
  **nothing happens**, while the test script printed "dragged" (2026-09-25, groups e2e). Check the return
  value (`sendinput.py` in the groups e2e scratch does), or reuse `tools/e2e/drag.py`'s layout.
- **Privacy in tooling:** never print clipboard content in probes/logs/test output; `tools/e2e/dbq.py`
  masks non-test rows. Test data is prefixed `BC-TEST`.
- **Clipboard tests never touch the real clipboard:** put them in the `IsolatedClipboardCollection`
  (private window station, see §1.9) and drive them with `ClipboardProducer`. Writing unmarked content to
  the real clipboard pushes items out of the user's RAM-only Win+V history — that loss is unrecoverable.
- **Gaps in timing tests:** busy-wait on a `Stopwatch` — `Thread.Sleep(n)` rounds up to the 15.6 ms tick.
- **A test never asserts "not yet" or "at least N" after a fixed `await Task.Delay`.** On GitHub's runners a
  continuation can come back far later than asked (a thread pool starved by the parallel test classes on few cores).
  Three Windows tests failed that way on 2026-10-10 and in 4 of 5 CI test runs on 2026-10-26; `a4aa62b` and `4da86bd`
  fixed them.
  - Wait for something the code under test makes observable: a counter, an attempt count, `WaitUntilAsync`.
  - When the order matters (unlock right after the second attempt), wait on the test's own thread with
    `SpinWait.SpinUntil`, not in a continuation.
  - A loop that must run long enough runs to a count, not only to a time.
  - Read a counter only after waiting for it: an entry is visible in the history before the code that stored it has
    counted it (`ImportedThisSession` is bumped after `AddAsync` returns).
  - This PC did not reproduce any of them, also not with `DOTNET_PROCESSOR_COUNT=1`, pinned to one core, or with the
    pool capped at 4 threads (`DOTNET_ThreadPool_ForceMaxWorkerThreads=4`). CI is the check.
  - Nor a timer that must outrun the code under test: cancel once the code under test is where the test needs it
    (`CliPipeServerTests.ClientHangUp_CancelsHandler` hangs up once its handler runs, §2.45).
  - **xunit's test order follows the test assembly's path** (2026-11-12, §2.45): the same binaries failed a timing test
    in every run from one folder and in none from another. A test that passes in one worktree and fails in another, or
    starts failing after an unrelated change, may only have met another order. Copy the test's output folder elsewhere
    and run it there before blaming the change.
- **Core tests run one class at a time** (`[assembly: Xunit.v3.Parallelization(Mode = ParallelMode.None)]` in
  `TestData.cs`; the old `CollectionBehavior.DisableTestParallelization` is a CS0619 error in xunit v3 4.x).
  - Why: `SqliteConnection.ClearAllPools` (`TempDirectory.Dispose`, `EncryptedStoreTests`) empties every pool
    of the process. A store test in another class could rent a pooled connection at that moment and fail with
    `ObjectDisposedException: … 'SQLitePCL.sqlite3'` inside `ClipStore.Open`.
  - Measured 2026-10-01: 4 of 25 runs before, 0 of 30 after; the suite still takes ~3 s.
  - The Windows suite (0 of 12 failures) keeps its parallel classes.
- **File-based probes (`dotnet run x.cs` with `#:project`) don't rebuild when only the referenced project
  changed:** pass `--no-cache` after editing it. On 2026-10-01 a probe ran stale Core code and reported a
  bug that was already fixed.
- **Never disturb the user's installed app** (it runs in the same session as every experiment): test
  instances get their own `BETTERCLIPBOARD_DATA_DIR` (⇒ scoped lock/events/pipe, no LL hook), `--exit` is
  only ever sent with that variable set, and scripts record the `BetterClipboard.exe` PIDs before and
  check them after. To let the user try a dev build, run a copy with `tools/launch_dev.py` (§3), never the
  build output itself: a running exe locks the next build, another agent's included.
- **Starting long-lived processes from a console tool:** `UseShellExecute = true` — with `false` the child
  inherits the tool's stdout/stderr pipes and whoever reads them (a shell pipe, an AI agent) waits for EOF
  until that child exits.
- **In the app, a shell start (`Process.Start` with `UseShellExecute`) never runs on the UI thread inside a XAML event**
  (found 2026-11-13, §2.49).
  - Why: `ShellExecuteEx` can run a message loop of its own while Windows activates the app (a packaged app such as
    Photos is started through COM and DDE). On the UI thread WinUI then re-enters from inside the event that asked.
  - When that event's popup had just been closed (the image viewer's file-name link), WinUI ended the process: 2 of 2
    clicks, 0xC000027B in `Microsoft.UI.Xaml.dll`, no log line.
  - What to do: run the start on the thread pool and await it, `await Task.Run(() => Process.Start(info)?.Dispose())`;
    .NET then calls `ShellExecuteEx` on an STA thread of its own. `AppController.LaunchAsync` (§2.49) does, and the Run
    tab's `RunCommandLauncher` (its own STA thread) did already. The app's older starts on the UI thread are in §6.
- **A crash with no log line is a WinUI fail-fast** (0xC000027B, "Application Error" 1000 in the event log naming
  `Microsoft.UI.Xaml.dll`): `Application.UnhandledException` never sees it. Get a dump in a test desktop (the
  claude-desktops lessons below) and read the UI thread's managed stack with `dotnet-dump analyze <dump> -c
  "clrstack -all"`: it shows the call into native code where it failed (§2.49's dump showed `ShellExecuteExW`).
- **Quote-dense scripts:** write them to a scratch file and run the file; big inline heredocs break the
  Bash tool's `eval` wrapper (`unexpected EOF while looking for matching '`). Escapes don't survive it
  either: a `\\n` inside a heredoc'd Python edit script reached the C# source as a real line break
  (2026-09-25), and on 2026-10-01 `'\\'` arrived as `'\'` (an unterminated char literal) and `\\n` again as a line break
  (the build caught both). Put escape sequences in with the Edit tool. **Never with `sed`:** GNU sed reads `\u` in a
  replacement as "uppercase the next character", so `s/x/"\\uE768"/` wrote `"E768"` (2026-10-01). A Python
  script that builds the backslash with `chr(92)` is safe too.
- **The real Win+R list is user data.** Tests and isolated e2e instances point the integration at a scratch key
  through `BETTERCLIPBOARD_RUNMRU_KEY` (under `HKCU\Software\BetterClipboard-Tests` or `-E2E`, deleted
  afterwards), or switch `RecordRunHistory` off; without either, an instance imports and watches the user's
  `RunMRU` (the user's own `launch_dev.py` copy does so on purpose, §3). Never print a real command.
- **Registry watch tests: one parent key per test.** While a key is missing, the watch observes its parent, and
  test classes run in parallel: with a shared parent, another test's keys coming and going raised spurious
  changes (6 of 6 runs), and one test's cleanup could delete the parent under another's watch.
- **Reading another app's freshly written file: never deny it writing.**
  - An open with `FileShare.Read` ("is the writer done?") makes the writer's own reopen for writing fail if it lands
    inside that open, and WinRT's StorageFile closes and reopens files.
  - Read with `FileShare.ReadWrite | Delete`, and tell completeness from the bytes (PNG's IEND, JPEG's FFD9) or from size
    and write time holding still. `WindowsScreenshotWatcher` does this; the older `ShareXScreenshotWatcher` still uses
    `FileShare.Read`.
  - Don't poll `FileProcessIdsUsingFileInformation` (which processes hold a file): ~137 ms per call here. In a
    `FileSystemWatcher` handler it delays every later event, because events are raised one after another (§2.18).
  - A file copied into a watched folder shows a write time of "now" until the copy finishes, so judge freshness once
    the file is complete.
- **Third-party source in `refs/`** (git-ignored, e.g. ShareX, GPL-3.0): study it for interoperability,
  never copy code from it, and cite the commit with any fact taken from it. Never open ShareX's
  `UploadersConfig.json` (upload credentials), and never trigger real ShareX captures; use a fake personal
  folder through `BETTERCLIPBOARD_SHAREX_DIR`.
- **E2E harness ([`tools/e2e/`](tools/e2e)) injects real keystrokes.**
  Only run it with the user's explicit OK and when they are not using the machine (2026-09-25: the user
  switched to a game mid-run and test keys/Ctrl+V landed in it). Recipe: isolated data dir →
  `PasteTarget.cs` with a unique output file → `activate.py BC-PasteTarget` → `keys.py win+v "type=…" enter`
  → `shot.py out.png fg` → afterwards `cleanup_history.cs -- <start time>` and restore the clipboard.
  Mouse tests (`drag.py`) must guard every injected press with `WindowFromPoint` → `GetAncestor(GA_ROOT)`
  and only click when that root is the test window (else SKIP). They send keys only while the test
  window is the foreground window, and they restore the cursor.
  Lesson (2026-09-25): the test flyout opens near the cursor and can end up *under* another always-on-top
  window. One unguarded run's presses (and an Esc) landed in whatever window covered it, and the flyout
  closed on deactivation. The failure looked like a product bug but wasn't.
- **UI Automation on cards: `SelectionItemPattern`, never `InvokePattern`.**
  - Why: a card's Invoke is a click, and a click pastes. The real clipboard gets the item and Ctrl+V goes to the
    window the panel came from.
  - Lesson (2026-10-01, Everything e2e): a helper that tried Invoke before Select "selected" a pick card. It put
    a BC-TEST file reference on the user's clipboard and sent Ctrl+V to their terminal (Win+V's history was
    unaffected: it keeps no file lists).
  - Use a select-only action for cards and list items, and seed `PasteOnSelect: false` in e2e settings as a
    second guard (a click then only copies). Tabs (`SelectorBarItem`) have no Invoke, but select them the same way.
- **Summon a test panel only in a quiet moment, and know which window is the terminal.**
  - Why: the panel takes the keyboard when it opens, with a card selected (the newest copy, §2.28). An Enter the user
    meant for their terminal pastes that card: a BC-TEST item through the real clipboard into the terminal. `PasteOnSelect`
    does not stop it, because it covers clicks only.
  - The guard: the terminal in front for two checks in a row **and** no user input for ≥ 3 s (`GetLastInputInfo`)
    (`prompts_ui.py`, 2026-10-01).
  - Which terminal: walk the session's parent processes instead of trusting a list of known names. On this PC the
    terminal is a custom Windows Terminal build, `Agentmaster.exe`. A guard whose list lacked it waited 10 minutes
    and skipped while the user was in that terminal (2026-10-01).
- **Screenshots of a test window: `PrintWindow`, not a screen region.**
  - Lesson (2026-10-01, Third party e2e): two captures of the Settings test window, grabbed as a screen region at its
    DWM bounds (Pillow `ImageGrab`, like `tools/e2e/shot.py`), showed the user's terminal — while
    `GetForegroundWindow` returned that very Settings window and DWM said it was not cloaked. The cause was not
    found. Both captures were deleted at once.
  - `PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT)` renders only that window's own pixels (WinUI content included),
    so it can never contain another window: reliable, and the privacy-safe choice.
  - Related trap: UIA's bounding rectangle of a WinUI `ScrollViewer` is not its viewport (3420 px tall in an 880-px
    window), so "is it scrolled into view" checks built on rectangles prove nothing. Look at a capture.
- **`AppWindow.MoveAndResize` sizes the outer window.** The content is narrower by the frame
  (`AppWindow.Size − ClientSize`: 14 DIP on Windows 11, the invisible resize borders), so layout math in DIPs must
  subtract it. Lesson (2026-10-01): the tab-bar widening assumed the full width, and the last tab was clipped ("Rur"
  with eight tabs, "Everythin" with nine) although every UIA rectangle lay inside the window. Zoom into a screenshot
  of the last tab; rectangles alone did not show it. (That widening is gone, §2.23. The remembered panel size is an
  outer size too — `FlyoutSizing` converts outer pixels, the same `AppWindow.Size` and `MoveAndResize` use.)
- **A window the program moves onto a monitor with another scale is rescaled a second time** (§2.38). Windows answers
  the move with `WM_DPICHANGED` inside it, suggesting the asked rectangle scaled once more, and WinUI takes the
  suggestion: asked 500 × 700 on a 125 % monitor, the window came out 625 × 875. Move the panel with
  `ClipboardFlyout.MoveAndResizeExactly` (it asks again when the bounds differ) wherever the move can change monitors;
  `AppWindow.MoveAndResize` alone is fine only on the panel's own monitor (the groups column). Any other window that is
  moved by code between monitors needs the same. `tools/probes/dpi_move_lab` checks it after a Windows App SDK update.
- **Never add a member to an enum the settings file stores by name** (`FlyoutPlacement`, `AppTheme`). A version that does
  not know the name throws while reading and sets the whole settings file aside as corrupt (§2.28). A new choice gets a
  member of `AppSettings` of its own that refines the old value (`CenterOnMainScreen`, §2.38), or a name read leniently
  (`LastTab`, §2.28). `PanelPositionSettingsTests.Placement_KeepsItsThreeReleasedNames` guards `FlyoutPlacement`.
- **Items of an `ItemsView`/`SelectorBar` inside a scrolling viewer are virtualized.** The ItemsView template's
  `ItemsRepeater` has `HorizontalCacheLength="0"` and follows every scrolling ancestor's viewport, so items out of view
  are not realized: they have no size, the content width is an estimate, and UIA cannot see them (their rectangles read
  (0, 0, 0, 0)). The tab carousel raises the cache (§2.23). Anything else that measures or reaches items of such a list
  must do the same, or work only with what is realized.
- **A pressed WinUI item stays pressed unless it sees its press end.** An `ItemContainer` (SelectorBarItem, ItemsView
  items) forgets a mouse press only on a release over itself, a cancel or `PointerCaptureLost` — in the WinUI we ship
  (2.3.9) not on its exit (fixed upstream 2026-07-25, PR 16243757; §2.23 "Dragging a tab off itself").
  - So a release elsewhere, and a capture taken by an ancestor, leave it drawn pressed (grayed) and still counting as
    pressed. After a right-button press it also refuses every left click.
  - Make it forget with a capture it gives straight back (`ClipboardFlyout.ForgetTabPress`): when the pointer leaves it
    (`Tab_PointerExited`), and before an ancestor captures (`TakeOverTabPress`).
  - A capture never moves between elements: `CapturePointer` returns false while another element holds the pointer
    (until 2026-11-08 this entry said to take the capture from the item, which WinUI never allows). Release the item's
    capture first, then capture the ancestor.
  - Ignore the item's `PointerCaptureLost` bubbling into the ancestor's handler (check `OriginalSource`).
- **The panel is never hidden: it is concealed** (§2.27). Every way it goes away calls `Conceal` (cloak, hide once to
  hand the foreground on, show again without activation), and `IsOpen` is the `presented` flag — `AppWindow.IsVisible` is
  true all the time once the warm-up ran. New code that hides or shows the panel's window directly breaks the instant
  summon; new code that asks "is the panel up?" asks `IsOpen`.
- **WinUI list controls animate every change by default.** ListView's `ItemContainerTransitions` (AddDelete, Content,
  Reorder, Entrance) play on every insert, remove and reload: after a summon they kept cards sliding in for ~0.3 s. Where
  a list must show at once, give it an empty `TransitionCollection`, and patch the collection (`CollectionPatch`) instead of
  clearing and refilling it.
- **A `CompositionTarget.Rendering` handler makes XAML render every frame** while it is attached, changed or not
  (measured: a frame every 16 ms). Attach one only for a bounded wait, and remove it in a `finally`.
- **A WinUI window that closes must let go of every handler on itself and on its timers** (§2.45). Handlers on the
  `Window` (`Closed`, `Activated`) and on a `DispatcherQueueTimer` that refer to the window form cycles that C#/WinRT's
  reference tracking cannot see through: neither is a XAML element. The closed window then stays in memory with its whole
  tree, 17 to 22 MB per Settings open in 0.2.10.
  - Remove them in `Closed`: name timer handlers (no lambdas), stop the timers, and take off the x:Bind code's own
    `Activated` handler (`SettingsWindow.DetachGeneratedBindings`).
  - A new window, or a new timer or window handler in one, follows `SettingsWindow.OnClosed`.
  - Check it with a heap dump (`dotnet-gcdump`) after a few opens and closes: no closed window may be in it.
- **Don't extend a window's content into its title bar when the window opens and closes repeatedly.** Each lifetime of
  such a window leaves 24 to 32 GDI handles behind (`ExtendsContentIntoTitleBar` and `AppWindow.TitleBar` alike, §2.45),
  and a process may hold 10,000. Settings uses Windows' own title bar; the panel never closes, so its window is not
  affected.
- **Give a `BitmapImage` to its `Image` before its source**: assigned first, WinUI decodes the picture at the size it is
  drawn; given its source first, at full size (`ClipItemViewModel.EnsureThumbnail`, §2.45).
- **Text boxes that take the keyboard often get no spelling checker** (`IsSpellCheckEnabled` and
  `IsTextPredictionEnabled` off): once a box with it had the keyboard, Windows' spelling engine stayed loaded in the app
  for good (§2.45). Prose boxes may keep it; search, shortcut, name and list boxes do not need it.
- **A windowed popup's colors come from Styles in `App.xaml`, never from `Application.Current.Resources` in code**
  (§2.50). A brush looked up in code follows Windows' app theme, not the panel's `RequestedTheme`: the text peek and
  the viewer came out light over a dark panel, and the image peek's frame had done so since §2.24. A style's
  `{ThemeResource}` setters resolve in the element's own theme (`OverlayFrameStyle`, `OverlaySecondaryTextStyle`, …,
  read with `ClipboardFlyout.OverlayStyle`). §2.11 found the same for the group buttons.
- **`PointerExited` bubbles:** a parent hears it whenever the pointer crosses from one of its inner elements to another.
  A hover surface that closes on it checks that the pointer really left its bounds (`ClipboardFlyout.IsInside`): the
  text peek closed at the first move over its text (§2.50).
- **A `RichEditBox` gets its text after its template:** `ApplyTemplate()` first, and a box that is not loaded yet gets
  its text at `Loaded` (`TextPreviewPresenter.Show`). An RTF document set before the template was applied lost its fonts
  and colors. Keep a loading box laid out (`Opacity` 0, no hit-testing), not collapsed (§2.50).
- **RichEdit takes text slowly, on the UI thread:** about 2.5 µs per character as plain text and 7 µs as RTF on a test
  desktop without a GPU, so 6 MB froze the panel for 15 s. Give it at most a page (`TextViewRendering.PageMaxChars`),
  and load plain text with the default character format set first: a format pass over the range cost as much again
  (§2.50).
- **A `MenuFlyout` without items does not open:** build its items up front and only change them in `Opening` (the text
  viewer's language menu, §2.50).
- **A Grid's Auto row lets a scrolling control grow to its whole content.** An Auto row measures its child with
  unlimited height, so a `RichEditBox` (or a `ScrollViewer`) in it reports its full text height, the container's
  `MaxHeight` cuts it off, and its own scroller has nothing to scroll: no scrollbar, and the wheel does nothing (the
  text peek, §2.50).
  Give the scrolling child a star row inside a top-aligned container with a `MaxHeight`: the container then fits a short
  content and caps a long one, which scrolls.
- **RichEdit scrolls on the wheel without marking the event handled.** A handler that scrolls the "unhandled" wheel
  events itself scrolls a `RichEditBox`'s text twice per notch; tell the cases apart by where the pointer is (§2.50).
- **A menu that closes over an overlay hands the keyboard back to the overlay.** `Popup_Closed` used to give it to the
  search box, where the panel's key model ignores keys while one of its popups is open (the overlay itself), so Esc did
  nothing in a viewer opened from a menu's *View*. `FocusOpenViewer` gives it to the text or image viewer when one is
  open (§2.50).
- **A background start (`--background`) must never show anything: the silent restart is one** (§2.46). A few seconds
  after every close of Settings the app starts again that way while the user works. A new window, dialog, notification
  or first-run tip in the start path shows each time, unbidden. Gate it on `StartupOptions.IsSilentRestart` (or keep it
  out of background starts), and when a start must ask the user something, add it to `SilentRestartRules` the way the
  language picker is (`LanguageWouldBeAsked`: then no restart happens at all).
- **A process the app starts while the user works gets `QuietProcess`, not `Process.Start`** (§2.46): only
  `STARTUPINFO.dwFlags` = `STARTF_FORCEOFFFEEDBACK` keeps Windows' app-starting pointer away, and
  `ProcessStartInfo` cannot set it. `QuietProcess` also inherits no handles (a script that redirected the app's output
  must not wait for the new process) and passes the caller's environment (an isolated instance comes back as itself).
- **A tray icon cannot be shared with, or moved to, another process** (§2.46): it belongs to its window, and a
  GUID-identified one vanished with its first window although `NIM_MODIFY` had accepted a new one. Two processes that
  must take turns add the second icon hidden (`TrayIcon` `startHidden`) and switch with `TrayIcon.HandOverTo`: reveal and
  delete within a millisecond. Adding the second one visibly showed two icons for ~60 ms on a pinned icon.
- **A named kernel object exists only while a handle to it is open.** The silent restart's events, and the instance's
  command events while two processes change places, are kept by a handle on purpose (`SingleInstance.KeepCommandsAlive`,
  the called-off restart's grace period); a process that closes its last handle early lets a signal sent a moment later
  fall into nothing.
- **A pointer event is not proof that the mouse moved** (§2.28, §2.24).
  - WinUI replays the last pointer position after every frame that changed the picture (at most every 500 ms), and
    Windows sends a move to a window that appears under the cursor. So `PointerEntered` and `PointerMoved` also come to
    whatever arrives under a mouse that lies still: a row that scrolled there, a list that reloaded, a popup that closed.
  - Anything a hover does must first compare `ScreenPointer.Cursor()` with a spot recorded earlier
    (`PointerSelectionGate`, the peek's `PeekCursorMoved`). Without that, the keyboard's selection jumps back to the
    row under the mouse, and Enter after a typed search pastes that row.
  - New code that sets the list's selection without the mouse calls `SuspendPointerSelection()`.
- **Hovering never scrolls a list.** A hover that brings its row into view moves the next row under the cursor, which is
  then hovered in turn. `SelectByPointer` sets `SelectedItem` only; `SelectIndex` (which scrolls) is for the keyboard.
- **To select a `SelectorBar` tab from code before the bar has loaded, set the tabs' `IsSelected` flags too** (§2.28).
  The bar's inner `ItemsView` selects every flagged tab when it first prepares its containers, so the tab flagged in
  XAML wins over a `SelectedItem` set earlier. `ClipboardFlyout.SelectFilter` is the pattern.
- **The panel starts no list load while the app exits** (`AppController.IsExiting`, checked in
  `FlyoutViewModel.ReloadAsync`). A stopping integration still announces its status, and a load started then ends after
  the dispatcher is gone: "Loading the flyout list failed" in the log, with nothing wrong (§2.28). A new way to reload
  the list goes through `ReloadAsync`, never around it.
- **A summon can start in any tab** (the one last chosen, §2.28). Code that assumed "a summon shows All" is wrong now:
  the first frame can be an Everything, shell or prompt tab, whose list always reloads after the panel showed.
- **Global hotkeys beat the foreground window, also for injected keys.** Before giving the panel an Alt+letter
  shortcut, or injecting a chord in a test, probe the chord with `RegisterHotKey` (`MOD_NOREPEAT`, released at
  once; a 1409 means someone owns it). An owned chord goes to its owner, and the foreground guard does not help.
  - Lesson (2026-10-01): VS Code's regex key Alt+R is registered on this PC — NVIDIA Overlay runs, and its
    performance overlay uses Alt+R by default (AMD's Radeon overlay does too). The panel's log showed Alt arriving
    and R never, and three test presses went to that owner instead of the test panel. The search box uses Alt+E.
- **A WinUI control template copied into `App.xaml`** (`SearchBoxTextBoxStyle`, §2.6.2) is a snapshot of the
  WinUI version it came from (2.3.9). After a WinUI upgrade, diff it against the new `generic.xaml`
  (`~/.nuget/packages/microsoft.windowsappsdk.winui/<version>/lib/net6.0-windows10.0.17763.0/Microsoft.WinUI/Themes/generic.xaml`,
  `AutoSuggestBoxTextBoxStyle`): a stale copy keeps old visuals, it does not break.
- **A visual-state setter does not hold against a `TemplateBinding` whose source changes later.**
  - What happened (2026-10-01): the search toggles' template once hid the outline with a "Normal" state setter
    (`Chrome.BorderBrush = Transparent`) over `BorderBrush="{TemplateBinding BorderBrush}"`. When the ".*" toggle's
    style was swapped back from the red error style while it was unchecked, the new style's brush flowed through the
    TemplateBinding and the off toggle kept a blue outline. Sampling the outline's pixels showed it; the eye might not
    have noticed.
  - What to do: let TemplateBindings carry colors, and show or hide template parts in the states by another
    property (`Opacity`, `Visibility`). Pixel-sample state changes that a style swap can reach.
- **A folder watcher does not see appends to a file another process keeps open.**
  - Measured (2026-10-01, `probe_append_notify.cs`): through a long-lived write handle, 8 appends over 2.5 s raised no
    `ReadDirectoryChangesW` notification, and the folder kept listing 0 bytes (`FileInfo.Length`, a directory
    enumeration) until the writer closed the file or another process opened it. Open-append-close notifies every time.
  - So for another app's log-like files (Codex's session files): poll by opening the file and reading its length from
    the handle (cheap), and keep the watcher for creations, renames and close-flushed writes (§2.21). Never decide
    "unchanged" from the folder's size or write time of a file that may be open elsewhere.
- **A worker that waits for a wake signal needs one for its first request.** The prompt readers queued their first pass
  in `Start()` but never signalled the worker, which then waited for the first 5-s poll tick: every first import
  started 5 s late. Found only because the new test class took 45 s instead of 5 (2026-10-01). Time new test classes
  that wait on background work: a round number of seconds per test points at a timer.
- **Our binaries' version resources must keep agreeing** (§3.3: code signing's metadata restrictions reject a signing
  request whose files disagree). Every shipped project inherits `Product`, `Company` and `Version` from
  `Directory.Build.props`. Don't override them per project, and don't turn SourceLink's commit suffix off in only some
  of them.
  - The `ProductVersion` of all six files must be `X.Y.Z+<commit>`.
  - A new shipped assembly (a new `.exe` or `.dll` of ours) must also join the list of signed files in
    `docs/code-signing.md` §4 and the README's *Code signing policy*.
- **The README's *Privacy* table lists every source BetterClipboard reads.** A new integration that reads another
  app's data adds its row: what is read, what is kept, the default, the switch. That table is what makes "This program
  will not transfer any information …" credible to a reader (§3.3). It is also the user's only overview of what is
  read.
- **A user-visible feature is not done until the README presents it.** The user, 2026-10-03: "We have had many many
  changes and our README.md is not presenting that". By then the README lagged 0.2.5: six tabs, one group at a time,
  and no image previews, resizing or prompt commands. Where things go:
  - every user-visible feature: a bullet in the *Features* list at the top (a dash list, at the user's request on
    2026-10-03 "a README features section made of a dash list of all our features … instead of a table"; it replaced
    the Win+V comparison table, and its Win+V numbers now sit in the bullets they belong to). A new kind of item or a
    new tab is a sub-item of *Every kind of copy*: the user moved the tabs for other apps there "as a sublist" and
    asked to "reduce duplicates", so each feature has one bullet and no bullet repeats another;
  - a new tab: the *Use › Tabs* table, plus a subsection under *Tabs for other apps*;
  - a new key or gesture: the keys table under *Use*;
  - a new menu entry: the keys table's menu row;
  - a new `bclip` command or filter: the *Command line* table;
  - a new Settings section: the Settings list under *Use*.

  Keep the anchors other files link to: `#privacy` and `#code-signing-policy` (release.yml's footer, the nuspec,
  `docs/code-signing.md`) and `#readme`. The README's four photos (`docs/images/panel.png`, `prompts.png`,
  `settings.png`, `update.png`) show the code of `7d7df8d` (2026-10-09) with invented demo data from a claude-desktops
  Windows desktop, never the user's screen.
  When a change makes one of them wrong, retake that photo the same way (§3.4).
  The title carries the app icon, `src/BetterClipboard.App/Assets/AppIcon.png` (256 px, from `tools/make_icon.py`; the
  Chocolatey `iconUrl` loads the same file, so keep it there). At 40 px with `align="top"` it sits within 2 px of the
  centre of the title's capitals (GitHub's styles, Segoe UI, measured 2026-11-08). `align="middle"` or `"center"` hangs
  it 11 px low, and no `align` raises it 5 px. GitHub links every image to its own file, so the `alt` names that link.
  The top is centered (the owner, 2026-11-13: "center the title with icon", the tagline, and the CI, Release and
  License badges above the language line). One `<div align="center">` holds, in this order, the title with its icon,
  the badges, the language line and the tagline; the panel photo follows in its own `<p align="center">`.
  - **Keep the blank lines inside the `<div>`:** GitHub reads Markdown inside an HTML block only after a blank line.
    Without them the heading, the badges and the bold text show as raw `#`, `[![…](…)]` and `**`.
  - The title stays a Markdown heading, so GitHub still gives it an anchor (`#-betterclipboard`, unchanged).
  - A new badge goes into the badges' paragraph, one per line.
  - The translations mirror the block. Each keeps its translation note left-aligned, as a quote between the language
    line and the tagline, so it needs two centered `<div>`s around it (the Hebrew README's sit inside its file-wide
    `<div dir="rtl">`).
  - Checked with GitHub's own renderer (`gh api markdown`, mode `markdown`, the way README files render: the `align`
    survived and the Markdown inside was read), then drawn with github-markdown-css in a headless Chrome.
- **Every new piece of UI text goes through `UiText`, keyed by its English text, with an entry in `en.json`** (§2.37).
  XAML: `{loc:Tr Text='…'}` (`Text=&quot;…&quot;` when the text has an apostrophe: WinUI's XAML compiler takes no
  backslash escapes); C#: `UiText.Translate("…")`, `UiText.Format($"…")`, and for a count
  `UiText.Plural(n, $"{n:N0} item", $"{n:N0} items")` — never an English "s" passed as a value. Add strings with
  `python tools/localization/catalog.py add "English" zh-Hans="中文"` (`add-plural` for counts). Changing an English text
  changes its key: `LocalizationTests.CodeAndCatalogsAgree` fails until `en.json` follows; change it with
  `catalog.py rename` (translations kept, marked for review), then the literal. A hash key (`{loc:Tr Key=T…}`) only for a
  text XAML cannot quote or a very long one.
  A new language is a catalog (with its `$language` header) and, if needed, a flag in `tools/make_flags.py`: no code.
  `TranslationCoverageTests` fail for a translated text without an entry and for an entry nothing uses any more
  (`catalog.py remove`).
  - **Whole phrases.** Never build a translated sentence from English words passed as values (`{isAre}`, `{itThem}`,
    a noun, a "+"): a translation leaves them out or keeps them English. Write one sentence per form ("1 file" /
    "{0:N0} files"; one key / several keys), join lists with `UiText.Translate(", ")` and `"{0} and {1}"`.
  - **Text a component words in English for its log, installer or tests and the UI also shows** is translated where it
    is shown, through the component's template list: `UiText.TranslateFormatted(text, Component.XTemplates)`. A new
    sentence goes into the list (best as a named constant the producer formats, as `UpdateMessages` does) and into
    every catalog; `TranslationTemplateTests` / `TranslationCoverageTests.CoreTemplateLists_HaveEntries` check them.
  - **Names BetterClipboard stores for a copy's source** ("Command Prompt", "Snipping Tool") stay English in the store:
    the tabs filter by them. Show them with `SourceNames.Display`, and add a new made-up name to `SourceNames.Templates`.
  - **Dates** go through `RelativeTimeFormatter` (day and month names follow `UiText.DateCulture`); a new date pattern
    is a catalog entry like `d MMM`.
  - **Every new string comes with its description** (2026-11-09): `catalog.py add "…" -d "…"`. It says where the string
    shows, what each `{0}` holds, how much room there is and which words to keep; `check` and
    `LocalizationTests.EnglishCatalog_EveryRowHasItsDescription` fail on a row without one. A new string also needs
    its Russian, Spanish and Hebrew (or it shows in English there, and a language lacking more than one string in ten
    is no longer offered).
  - **A line the code joins from translated pieces goes through `UiText.JoinLine`** (a caption "app · time · size",
    two sentences on one line): in Hebrew the line is then ordered right to left as a whole. A plain `string.Join` or
    `$"{a} · {b}"` lays Hebrew pieces out from the left.
  - **Never look at a translation's first or last character without skipping format characters:** a Hebrew text comes
    wrapped in U+202B … U+202C (§2.37), so `text[^1]` is the closing mark (`UpdateText.AsSentence` is the pattern). A
    translation stored as data (a group's default name) is stripped by the store.
  - **A Latin word that ends in signs keeps them only with a left-to-right mark after them** (Notepad++, C#) when
    right-to-left text follows or the line ends: Hebrew showed "++Notepad" until 2026-11-09 (§2.37). `UiText` adds
    U+200E to every right-to-left text it makes; text that reaches the screen around it (a new route that embeds
    Hebrew itself) needs the same (`KeepTrailingSignsWithTheirWord`). Write the mark as `\u200E`: the Edit tool and
    the Write tool both put the raw, invisible character into a file again that day.
  - **A translation must fit the room its description names.** Measure, don't guess: Pillow's
    `ImageFont.truetype(r"C:\Windows\Fonts\segoeui.ttf", 14).getlength(text)` against the English (the search box
    shows about 222 px of text).
- **Logs and `bclip` stay English** (§2.37). Never pass `UiText` output to `AppLog`: when a text is both shown and
  logged, build it twice. `bclip` pins English in `Main`, and its tables call `RelativeTimeFormatter` with
  `localized: false`. `LocalizationTests.NoLogLineIsBuiltFromTranslatedText` sees only a log call that names `UiText`
  itself, not a translated value handed over in a variable.
- **A README change goes into its translations** (`docs/translations/*.md`, §2.37), in the same commit when the
  wording is clear. The translations say at their top that the English README prevails, and they keep the English
  section ids as explicit `<a id=…>` anchors, so `#privacy` and `#code-signing-policy` work in every language.
  - **A merge brings main's README changes in English only.** The Chinese README was written on a branch; three
    commits made on `main` meanwhile changed only the English one, and two of them (dev builds `83c0e5a`, the tab
    menu `3cbefdc` + `7dce6ca`) stayed missing in Chinese from 2026-11-08 until `2d5371b` (2026-11-13). After merging
    a branch that adds or edits a translation, list the commits that changed `README.md` but not the translation:
    `git rev-list --no-merges <branch point>..HEAD -- README.md`, then `git show --name-only` for each.
  - **Check them with `python tools/readme/check-translations.py`** (every translation, or the files named). Section
    by section (the translation's `<a id>` equals the English heading's anchor) it compares list items per depth,
    table rows, paragraphs, images, link targets and the commands in code blocks (comments are translated); then
    GitHub's rendering of each whole file (the REST API's `/markdown`, nothing published; `--offline` skips it): the
    same tables with the same rows, list items and code blocks, nothing left as raw `**` or `](`.
    - It asks with gh's stored sign-in (`gh auth token`, with `GH_TOKEN`/`GITHUB_TOKEN` hidden), else anonymously:
      60 requests an hour per address, which a few runs used up on 2026-11-13 (HTTP 403 "rate limit exceeded").
    - A README.md that holds another session's unstaged text makes every translation differ (on 2026-11-13 the AI
      bullets did): stage your own changes, then run it with `--ref :`, which reads the index.
    - Counts cannot see a sentence missing inside a paragraph: read each commit's English diff too.
    - 16 planted faults, one or two per check, were all named on 2026-11-13, and the five READMEs passed.
  - **Five READMEs** (2026-11-13): English; Simplified Chinese (contributed with PR #1, kept up since); Russian,
    Spanish and Hebrew, translated by Claude from README.md with the app's catalogs' words for its labels and their
    tone (Russian «вы», Latin-American Spanish «tú», Hebrew in the plural), not reviewed by a native speaker yet.
    Every README's language line names all five; a new translation adds itself to each.
  - **The Hebrew README reads right to left inside a left-to-right page.** The whole file is wrapped in
    `<div dir="rtl">`: GitHub keeps the attribute, and tables, which get no `dir="auto"`, follow it. GitHub's page
    gives every paragraph, heading and list `dir="auto"`, which follows the block's first strong letter, so:
    - a block that starts with a Latin word, a code span or `&lrm;` starts with `&rlm;`; a list takes its direction
      from its first item, a nested list from its own;
    - every inline code span has `&lrm;` on both sides, or `-NoTakeOverWinV` shows as `NoTakeOverWinV-`;
    - a `+` or `#` that ends a Latin word gets `&lrm;` after it when Hebrew or the end of its block (a table cell ends
      at its `|`) comes next: `Notepad++&lrm;`, `Win+&lrm;אות`. The app marks its own texts the same way (§2.37);
    - the marks are written as the entities `&rlm;` and `&lrm;`, never as raw U+200F and U+200E, which no editor or
      diff shows.

    `check-translations.py` checks all four (the last in every translation). The page was also drawn with
    github-markdown-css in a headless Chrome and each character's position measured.
- **Every third party gets its entry in `Core/Presentation/ThirdPartyCatalog`** (Settings › Third party, §2.20).
  - A package that ships: credit it in a component's `Packages`, or add a component plus its
    `THIRD-PARTY-NOTICES.md` row (same name, same license text). `ThirdPartyCatalogTests` fail until both agree.
  - An integration with another maker's app: add its `Integration` entry with the feature. **No test notices a
    missing one.**
  - Links follow the official-link rule: the final https address, no `aka.ms`, no `/en-us/`.
- **A folder on the PATH lends its DLLs to every program on the PC** (2026-11-08): Windows' DLL search ends with the
  PATH, so a program that asks for a DLL it does not ship (ShareX: `DWriteCore.dll`) loads the copy from the install
  folder that *Add bclip to PATH* listed, and holds that file until it exits. Expect files of the install folder to
  be in use by unrelated programs: anything that deletes or replaces them (`install.ps1`, an uninstall, a test) must
  tolerate it — `install.ps1` sets such folders aside (§3.1). Find the holder with the Restart Manager
  (`RmGetList`): it names processes that `Get-Process … .Modules` cannot read.
- **Never `New-Item -Path <registry key> -Force` on a key that may exist.** In the registry provider it deletes the
  existing key with every value and subkey in it and creates it empty (PowerShell 5.1 and 7, verified 2026-10-03).
  `install.ps1` did it to `HKCU\…\Run` from v0.1.0 to 0.2.5 — every install erased every other app's startup entry —
  and to `Explorer\Advanced` with `-TakeOverWinV`; the Chocolatey install script and `test-chocolatey.ps1` copied it.
  Create a key only when `Test-Path` says it is missing (`Confirm-RegistryKey`), then `Set-ItemProperty`. Tests on
  scratch keys hid it for a week: a scratch key has no neighbours to lose, so the installer harness now seeds a
  neighbouring value and subkey and checks they survive.
- **Anything an installer writes to `HKLM\SOFTWARE` goes through the 64-bit view** (`[Microsoft.Win32.RegistryKey]::
  OpenBaseKey('LocalMachine', 'Registry64')`), not `HKLM:\` provider paths. A 32-bit PowerShell (an RMM agent's,
  Chocolatey with `--x86`) is redirected to `WOW6432Node`, which the 64-bit app never reads (§2.34).
- **Windows keeps the on/off mark of an `HKLM` Run value for the whole PC** (`HKLM\…\Explorer\StartupApproved\Run`,
  admin only), never per user (verified on this PC, 2026-10-18). A per-user "off" for a machine-wide startup entry
  must be the app's own: `AppSettings.SkipAllUsersStartup`, honored before the single-instance lock (§2.34).
- **A machine-wide startup entry must start a program no ordinary user can replace,** or one user plants code that
  runs as every other user at sign-in. `install.ps1 -AllUsers` protects its folder's rights (Administrators and
  SYSTEM full control, Users read and run) whatever the parent grants; a folder under `C:\` gives Authenticated
  Users Modify.
- **SYSTEM has no Start menu folder of its own:** `[Environment]::GetFolderPath('Programs')` answers `''`, and
  `Join-Path`, `Test-Path` and `Remove-Item` refuse an empty path outright, before `-ErrorAction` can help. Every
  `install.ps1` run as SYSTEM stopped at its first line until 2026-10-18. Guard every profile folder a script may run
  without.
- **`Set-Acl` with an ACL that `Get-Acl` returned writes the audit section too** and fails without
  `SeSecurityPrivilege`, even for the folder's owner. Build a fresh `DirectorySecurity` (only the DACL is written), or
  use `icacls`. A folder protected that way refuses a non-elevated process, so a test's cleanup must give the owner
  its rights back first (`icacls <folder> /grant *<sid>:(OI)(CI)F /T`).
- **`IdentityReference.Translate([SecurityIdentifier])` throws for "ALL APPLICATION PACKAGES"** ("Some or all identity
  references could not be translated"). Read the rules with SIDs straight away:
  `$acl.GetAccessRules($true, $true, [System.Security.Principal.SecurityIdentifier])`.
- **In a GitHub `run: |` step in pwsh, a script's `exit 1` does not stop the next line.** Only the last exit code
  counts, so a second test run that passes hides a first that failed. Add `if ($LASTEXITCODE -ne 0) { exit
  $LASTEXITCODE }` between them.
- **PowerShell functions that return an array as one object (`return , $list`) are assigned, never wrapped in `@()`.**
  `@(f)` collects f's single output — the array — into a new array, so it nests. Found by the installer harness
  (2026-10-03): `@(Get-RecordedReleasedKeys $state)` made `installer.json` say `"releasedKeys": "System.Object[]V"`.
- **Running `BetterClipboard.exe` (a GUI-subsystem program) from PowerShell for its output:** the call operator neither
  waits nor captures (no exit code, no lines, in 5.1 and 7). Use `Start-Process -NoNewWindow -PassThru
  -RedirectStandardOutput <file>`, read `$process.Handle` right after the start (5.1 reports no `ExitCode` otherwise),
  then `WaitForExit`. Arguments are joined unquoted, so pass none with spaces (`install.ps1` strips them from shortcuts).
  - To only wait (no output wanted), piping on is enough: `& $exe --exit | Out-Null` waits and sets `$LASTEXITCODE`.
    Measured 2026-10-03 with a GUI program that sleeps 2 s: 2,095 ms (5.1) and 2,063 ms (7) piped, against 3-164 ms
    and no exit code for `& $exe` and `$null = & $exe`. The Chocolatey before-modify script relies on it.
- **A Windows PowerShell started from PowerShell 7 must not inherit 7's `PSModulePath`.** It would look for its modules
  in 7's folders first and load none of its own, so even `Get-FileHash` is "not recognized".
  - Remove it: `ProcessStartInfo.EnvironmentVariables.Remove('PSModulePath')`, or `$env:PSModulePath = $null` around a
    `Start-Process`, put back in `finally`. From bash: `env -u PSModulePath` (§3.1).
  - CI's steps run in pwsh, so every test script that starts `powershell.exe` needs it. The app's updater and
    `test-update.ps1` had it; `test-all-users.ps1` did not, and its first CI run failed (2026-10-26, fixed in
    `676ec40`). A scheduled task (the SYSTEM runs) starts with its own environment and is not affected.
- **`gh release` subcommands read the repository from the current folder's git remote** unless `--repo` is given
  (`gh api` takes the repository in its path). In a workflow step after `actions/checkout` that works by accident;
  run anywhere else, `gh release download` fails with "not a git repository". Pass `--repo $env:GITHUB_REPOSITORY` in
  workflows (found 2026-10-03 by running `chocolatey.yml`'s download step from a scratch folder).
- **`RegisterHotKey` needs the interactive window station:** in the private one the clipboard tests use it fails with
  1459. Hotkey tests register keys no keyboard has (Ctrl+Alt+Shift+F21–F23) on the session desktop for milliseconds,
  and skip where there is no interactive station.
- **`SendInput` does not report UIPI blocks.** Keys a process injects into a window of a higher integrity level (an elevated
  window, from a non-elevated BetterClipboard) are dropped, and `SendInput` still returns the full count. Decide before
  injecting with `ProcessIntegrity.IsAboveCurrent(window)` (§2.5). Found 2026-10-09: the old check ("fewer events
  injected") never fired, and a paste into an elevated Notepad silently did nothing.
- **A `WH_KEYBOARD_LL` hook that must see keys before BetterClipboard's own takeover hook re-installs itself after every
  `HotkeyService.ApplyAsync`:** the newest hook is called first, and the takeover hook is installed again on each apply
  (`HotkeyRecorder.RestartAsync`, §2.26). Low-level hooks run before registered hotkeys, so a hook sees (and may keep)
  Explorer's Win+E; it cannot keep Win+L or Ctrl+Alt+Delete.
- **claude-desktops Windows QA, lessons of 2026-10-09:** `type_text` drops characters in Notepad and the Run dialog (also
  with BetterClipboard closed): type letters with `press_keys` or check the result; a file written into the persist folder
  from the host may stay invisible on the guest's `Z:` until the next mount (directory cache): write it with `write_file`
  into the guest instead, or write it before a restart; `drag` with few points is too fast for a WinUI drag-and-drop: give
  it a dozen points that hover over the target; a standard-user session needs its own agent logon task (Limited run level),
  full control of `C:\ProgramData\claude-desktop`, and auto sign-in through the Winlogon values **plus** the LSA secret
  `DefaultPassword`; with UAC on, `explorer.exe <file>` from the elevated agent starts a non-elevated process (the
  installer test of a normal admin prompt).
  - Also (2026-10-09, §2.27): OLE's clipboard fails there — WinForms `Clipboard.SetText`/`SetDataObject` throw
    `CLIPBRD_E_CANT_OPEN` (0x800401D0) every time, from the agent's job and from a process Explorer started, while nobody
    holds the clipboard (`GetOpenClipboardWindow` sampled 1.2 million times: never). Plain Win32 calls work:
    `OpenClipboard(owner window)`, `EmptyClipboard`, `SetClipboardData` (`CF_UNICODETEXT`, or `CF_DIB` built from a
    bitmap), `CloseClipboard`. With a null owner, `SetClipboardData` fails (§1.7). The agent's own `clipboard` tool works
    for text. Seeding many items: a PowerShell `Add-Type` helper doing those calls, pumping messages between copies.
  - One screen `BitBlt` costs ~20–25 ms in those desktops (no GPU), whatever its size: copy one rectangle per sample,
    not several rows (§2.27's probe).
  - Also (2026-10-09, §2.28): `move_mouse` is one jump, so one pointer event. A hover test needs a second small move:
    after the image peek (a windowed popup) closed on that one event, the row under the pointer got no
    `PointerEntered` until the next move. A real mouse sends dozens of events, so this is the test's limit, not the app's.
  - A key held down is two or more key-downs without a key-up (`keybd_event(vk, 0, KEYEVENTF_EXTENDEDKEY, 0)` in a
    loop, then one key-up): the second one arrives with `KeyStatus.WasKeyDown`. `press_keys` always sends new presses.
  - Read the panel's state with one UI Automation script instead of screenshots (selected tab, selected row, groups
    with their `ItemStatus`, header and footer texts, focus, cursor): `state.ps1` in session 805f9263's scratchpad
    (`qa/`). In PowerShell, `$T` and `$t` are the same variable: a type kept in `$T` was overwritten by a
    `foreach ($t in …)` and every later call failed silently under `SilentlyContinue`.
  - A click at a remembered coordinate pastes when the panel has moved (a click on a card pastes): take the tab's or
    row's position from the state script right before each click.
- **Editing repository files from Python: bytes, or `newline=''`.** `Path.write_text` on Windows writes CRLF; on
  2026-10-03 it turned all of CLAUDE.md into CRLF (caught by `file`, put back before committing). The repository's files
  are LF in the working tree.
- **Ask `git ls-files --eol`, `file` or Python about line endings, never `grep $'\r'`** (2026-11-13). In Git Bash that
  grep answered 0 for a working file with CRLF on every line, and "every line" for blobs without a single one (piped
  from `git show`): it proves nothing either way, and neither did the old landing script's check built on it.
  - The shared tree's working `CLAUDE.md` was CRLF that day while the index holds LF; `core.autocrlf=true` hides the
    difference from `git diff` and `git status`.
  - A three-way merge into such a working copy converts it to LF first (`git merge-file` compares bytes) and writes
    the file's own line endings back, so the other session finds its file as it left it.
- **`install.ps1 -Update …` is a contract with every released app** (§2.29). The update button of an installed version
  runs the *new* release's `install.ps1` with `-Update -Package -PackageSha256 -InstallDir -ResultPath -LogPath`.
  - Never rename or remove one of these parameters, and never add a mandatory one: an app that is already installed
    cannot pass it.
  - Keep `-Update` to one copy: its install folder, the records that name that folder, and the programs that run from
    it. No shortcut, Run value, PATH or Explorer change.
  - The result file's members (`version`, `ok`, `error`, `finishedUtc`) belong to the contract: the old app reads them.
  - [`tools/release/test-update.ps1`](tools/release/test-update.ps1) runs the command line against the packaged zip in CI
    and in the release job. It is safe on this PC too (scratch folders; it proves that nothing else changed).
- **`BetterClipboard.exe --apply-portable-update …` is a contract too** (§2.48): a portable copy starts the *new*
  release's exe with `--target --wait-for --result --log` (`PortableUpdate`'s constants). Never rename or remove one of
  these switches or change their values' format (`pid:startTicks`, full paths); a later version may add a switch only
  when every release it can update to ignores it (the parser skips unknown switches). Keep the helper to the release's
  own files, all or nothing, and keep writing `install.ps1`'s result file. `Program.Main` must keep recognizing the switch
  first, before the single-instance lock. `PortableUpdate.MinimumVersion` names the first release that ships the helper:
  raise it if that number is skipped.
- **No validation attributes in `install.ps1`'s param block** (`[ValidatePattern]`, `[ValidateSet]`,
  `[ValidateNotNullOrEmpty]`, `[ValidateScript]`, …). The README's `irm … | iex` runs the param block as variable
  declarations and checks every attribute against the empty value, so one of them stops every install before its first
  line. It did from 0.2.6 to 0.2.8 (§3.1). Check values in the script body;
  [`tools/release/test-iex.ps1`](tools/release/test-iex.ps1) (CI) fails otherwise. `-File`, `install-local.ps1` and the
  README's `[scriptblock]::Create` form bind parameters normally, so they never show this: test the `iex` form itself
  after any change to the param block.
- **`Start-Process -Wait` waits for the whole process tree** (Windows PowerShell 5.1 and PowerShell 7). Measured
  2026-10-16: a child that started a 5-second `ping` and exited at once kept `-Wait` for 6.1–6.4 s, while
  `-PassThru` + `WaitForExit()` returned after 0.4 s. An installer that starts its app (Ditto's, BetterClipboard's)
  never "finishes" for `-Wait`: a claude-desktops `run_command` hung until its timeout, and the desktop agent answered
  nothing else meanwhile. Wait for the installer process alone, or start apps with the agent's `launch`.
- **Never test an update on the user's installed copy,** and never give the regular instance another feed. Use an
  isolated instance with `BETTERCLIPBOARD_UPDATE_FEED` and an `installer.json` of its own for a scratch copy, or a
  claude-desktops Windows desktop (§2.29). **The one exception is the user asking for exactly that** ("manual QA my
  installation on this machine", 2026-10-10). Then, on a PC that the user works and plays on:
  - **One short sequence per on-screen step, never a window left open between two tool calls.** On 2026-10-10 an
    update dialog that stayed open while the next command was prepared took the keyboard from the user's browser for
    about 20 s. A sequence (session 3103c128's `qa/scenario.py`) waits until a desk program is in front (a terminal,
    an editor, a browser; nothing that covers its whole monitor, never a game) and keyboard and mouse have rested
    about 10 s, opens the window, does its steps through UI Automation (Invoke and Toggle: no mouse, no typing), stops
    when a key or a mouse button goes down or the foreground leaves the app, and closes what it opened. The
    sequences on the installed app took 5 to 17 s each.
  - **`GetLastInputInfo` cannot tell the user from the app during a sequence:** BetterClipboard sends itself an empty
    mouse input when it brings a window to the front (`ForegroundHelper.Activate`), which resets that clock. Watch
    `GetAsyncKeyState` and the foreground window instead; use the idle clock only for the guard before the sequence.
  - **Only the update controls are read.** The panel's cards are the user's copies: find controls by automation id,
    capture the dialog's own window (`PrintWindow`; a flyout is a window of its own) and of the panel only its header.
    Never press *Copy command* or *Copy install command* there: Windows' own clipboard history is on.
  - **At most one isolated copy at a time.** Each one reads the clipboard at every change. With four running, one
    real copy was not captured ("Clipboard stayed locked (held by window …); change #1529 not captured" in the
    installed app's log).
  - **A test copy's notification looks like the real app's.** "BetterClipboard was updated to 0.2.7" came from an
    isolated copy, and the user opened their own update dialog seconds later. Say what will appear before such a
    run, or keep it to a test desktop.
  - **The full installer:** `-NoLaunch`, then start the app through Explorer with `--background` (memory "Install on
    this machine"). Take a content-free snapshot before and after (`qa/state.ps1`: versions, records, the Run key's
    names, Explorer's processes, hashes of PATH and settings) and restore every setting a case changed
    (`CheckForUpdates` after `-NoUpdateCheck`, the install folder after `-InstallDir`).
  - **UI Automation facts for this app:** the panel and Settings share class and title, and both have a caption
    style; the panel is the one without `WS_MINIMIZEBOX`. A cloaked panel has no content in the automation tree, and
    the content appears a moment after the uncloak: look for a control for up to 2 s. A WinUI `ScrollViewer` reports
    the rectangle of its whole content, so scroll with `ScrollPattern.SetScrollPercent` and judge by the control's own
    rectangle against the window's. In the update dialog *Cancel* is the second button (`SecondaryButton`), not the
    first.
- **A WinUI 3 app cannot run on a second desktop of the window station** (`CreateDesktop` + `STARTUPINFO.lpDesktop`, to
  keep test windows off the user's screen). BetterClipboard crashed 3 of 3 times, 3 to 4 s after its start: a stowed
  exception (0xC000027B) in `CoreMessagingXP.dll`, E_NOTIMPL from combase. Do not try it again.
  - One crash left the error-reporting clone of the process behind: a process object with the program's name, no
    threads and no handles, which a non-elevated process can neither open nor stop. It was still there more than
    70 minutes later, with no `WerFault.exe` running and the `WerSvc` service stopped. A restart of Windows removes
    it; a sign-out probably does (not tested).
  - It keeps mapping the 76 program files the copy had loaded (its exe and DLLs, 117 MB): they can be renamed, not
    deleted (WinError 5). Right after the crash the store's `-shm` file was in use too (the next start of that copy
    failed with "disk I/O error"); an hour later that file could be deleted. It also broke the installer (next
    bullet).
  - The three crash dumps (41 MB each, `%LOCALAPPDATA%\CrashDumps`) were deleted: a dump holds what the copy had read.
- **`rm -rf` in Git Bash does not prove that files are free.** The MSYS runtime cannot delete a file that a process
  still maps, so it renames the file into the account's hidden recycle folder (`C:\$Recycle.Bin\<SID>`) under a
  generated name that starts with a dot, and reports success. The folder looks removed, the space is not freed, and
  the Recycle Bin window does not list the file. On 2026-10-10 this hid the 76 mapped files above (155 older ones,
  204 MB, moved there between April and 2026-10-02 by other work, were left alone). Test with a plain Windows
  delete (Python `os.remove`: WinError 5), and find such files by their NTFS change time, which a rename updates
  (session 3103c128's `qa/bring_back.py` moved the 76 back into one scratch folder, `qa/iso/in-use-until-restart`).
- **A process list can hold a BetterClipboard that does not run.** For such an object, and for a copy that runs as
  administrator while the script does not, PowerShell returns nothing for `HasExited` (so `-not $_.HasExited` is true
  for ever) and `Stop-Process` fails with "Access is denied", a terminating error under `$ErrorActionPreference =
  'Stop'`. Count a process as running only while the list shows it with at least one thread (`install.ps1`'s
  `Test-ProcessRuns`), and never let "could not stop it" end a script that has already closed the user's app.
- **An edit and the command that needs it do not go into one batch of tool calls:** they run at the same time, and
  the command reads the file from before the edit (2026-10-10: a "fixed" script ran unfixed).
- **A block inserted right after an `if`'s closing brace takes over the `else` that follows** (2026-11-08, §2.41). The
  Notepad++ tab's name went between the card caption's `if (source app known)` and its `else if (live copy)`: the
  "Unknown app" fallback then followed the new `if`, and every copy of a known app read "Notepad++ · Unknown app". It
  compiles, and no unit test sees a view model's caption; the live check did. Insert new statements before the `if` or
  after its whole chain, and scan a change for the shape: an added line followed by an unchanged `else`, or an added
  `else` after unchanged code (`scan_else.py` in session 9f3b7bdf's scratchpad reads a `git diff`).
- **Everything the app sends over the network is named in the README's *Privacy* section,** which is the privacy policy
  that code signing asks for (§3.3): what is requested, from whom, what the request shows, and how to switch it off. A
  new request (telemetry, a crash report, a second update source) needs its paragraph there, a Settings switch, an
  installer option and a line in the installer's closing note before it ships. Log everyday network failures at Info.
- **A flyout that leaves the panel's window** (`ShouldConstrainToRootBounds = false`, §2.29):
  - Count it in `openPopups` from its `Opening` event, not `Opened`: its window can take the activation first, and a
    panel that is deactivated with no popup counted conceals itself.
  - When it closes while another window is in front, conceal the panel: no deactivation will do it any more.
  - WinUI moves a flyout that does not fit on its side to any side that takes it whole. When it must stay at its
    button, choose the side and limit its height yourself (`UpdateDialogLayout`).
  - Make it the button's own `Flyout`. A `Click` handler that calls `ShowAt` opens it again at once: the click that
    dismisses the bubble also reaches the button.
- **`ScrollViewer.ChangeView` on a scroller that is not on screen yet is dropped,** without an error. Reset a flyout's
  scroll position in its `Opened` event, not in `Opening`.
- **A WinUI `ScrollViewer` lays its content out at the content's own width and centers it** when that is narrower than
  the viewport, whatever the content's `HorizontalAlignment="Stretch"` says (horizontal scrolling is off by default, so
  the width is limited, not stretched). A page whose children can all collapse or turn short — Settings while a search
  shows "No setting matches" or one short card — shrank and moved to the middle of the window (2026-11-09, §2.43). Give
  such a page an explicit width from the scroller's size (`SettingsWindow.Scroller_SizeChanged`).
- **DirectWrite breaks a line before the "+" of "Win+PrtScn"** ("…Win" / "+PrtScn?"; Unicode's rules forbid it, Windows'
  line breaker does it). Text that may wrap and can name a shortcut goes through `StoredText.KeepShortcutsTogether`
  (word joiners, U+2060, around each "+" between two letters or digits; screen readers skip them). The rest of the app
  does not do this yet (§6).
- **A program started through Explorer gets Explorer's environment,** not the caller's: `BETTERCLIPBOARD_DATA_DIR` and
  the other overrides are lost. That is right for a fresh install from an elevated prompt (`Start-AppUnelevated`) and
  wrong wherever an instance must come back as itself (`Start-AppAsBefore`, §2.29).
- **Tray notifications last about 5 s and are not kept** in Windows 11's notification center. To see one in a test
  desktop, wait for the log line that precedes it and take the screenshot at once.
- **claude-desktops Windows desktops, lessons of the update work (2026-10-09):**
  - Files copied into the persist folder before a `reset_desktop` are on `Z:` afterwards; so was a new top-level folder
    copied in while the desktop ran. Helper scripts written with `write_file` are gone after a reset: keep a copy.
  - Take the coordinates of a tray-menu item from a screenshot of the open menu. Two clicks 100 px left of the menu
    dismissed it, and looked like a product bug.
  - The first Win+V after a start can take close to a second there when the desktop is busy (912 ms once, right after
    an update; 47–86 ms in 16 quiet runs). A click sent sooner goes to the window below.
  - Comparing two builds' timings: alternate them, and swap which one goes first halfway. The second build of each
    pair was 10–15 ms slower whichever build it was.
  - `find_elements(app="BetterClipboard")` searches the app's first window only: the panel, which is always there
    (shown but cloaked, §2.27). Settings and a flyout's own window are not searched, and the tool takes no window:
    use coordinates from a screenshot.
  - A package labelled below 0.2.6 gets neither `-NoUpdateCheck` nor the installer's closing note about the update
    check (the version gate, §2.25). Switch such a build with `BetterClipboard.exe --set-update-check off|on`.
  - A build labelled with an older version, installed as the regular copy, is offered the real latest release:
    the way to see a real offer before a newer release exists (§3.4).
- **Never `Items.Clear()` on the panel's list** (`FlyoutViewModel.Items`). A list control that is reset while a row is
  selected hands that row's container on with its selection bar still drawn, and another row then looks selected
  too (2026-10-09, §2.27). Build the new cards first and swap them in with `ReplaceItems`, or `ApplyEntries` for
  stored entries: rows that are removed one by one are deselected properly. When a list looks wrong, ask UI Automation
  which rows are selected: it told the drawing from the state.
- **Anything of the panel that can be clicked without taking the keyboard is tested with a click and then a typed
  letter.** WinUI gives the keyboard to the scroll viewer around the window content when a click lands on something
  that takes none (a tooltip, for one). That scroll viewer is an ancestor of `Root`, so the panel's key model hears
  nothing, and the panel sits in front and ignores every key (§2.30). `ClipboardFlyout.ToolTipKeyboard.cs` repairs
  it for tooltips; `EndDrag` does for the background. A new surface (a popup with plain content, an overlay) needs
  the same check.
- **claude-desktops Windows desktops, lessons of the reminder work (2026-10-10):**
  - **Two MCP calls are seconds apart,** and not the same number of seconds each time. Anything that lasts a few
    seconds (a tooltip, an animation) is tested by a script inside the desktop that presses the key itself and
    takes its own screenshots on a timeline (`CopyFromScreen` after `SetProcessDPIAware`, files into `Z:\`).
    A first try that called `press_keys` and `screenshot` in one batch showed no tooltip although the log says
    it was shown: most likely the shot came before the tooltip had opened.
  - **`SetCursorPos` moves the pointer without the hover:** no hover highlight, no tooltip. A drag driven by it
    works (the window has the capture). For hover tooltips use the desktop's own `move_mouse`.
  - **Keys sent right after Win+V go to the window below** (`press_keys(["win+v", "t", "e", "s"])` typed only
    "s" into the panel). That is the known first 15 to 30 ms, not a new fault: send the keys a moment later.
  - **The first start of a freshly installed build is slow:** its Win+V was registered 7 s after the process
    started, and a Win+V sent 4 s after the start did nothing at all (Win+V is released from Explorer there).
    Wait for the log line "Panel prepared for the first Win+V" before the first key.
  - **A new folder made in `Z:\` from inside the guest may not exist** for the next command (`New-Item` said
    nothing, `Copy-Item` then failed). Write into a folder that is already there.
  - **The display scale** is set in Settings › Display through UI Automation (expand the *Scale* combo box, select
    "150%"); it applies at once, and a process that is not DPI-aware then sees 1067 × 600.
- **A walk of the arrow keys through the groups column has two modes** (§2.31).
  - Code that changes the groups shown as part of the walk does it while `keysChangingGroups` is set. Without the
    flag, the change event ends the walk.
  - Code that needs the keys' place asks `GroupCursorPosition()`. In a walk that picks, the ring is not always on a
    group that is shown, so "the group shown" is the wrong answer there.
  - The mode is the entry's (`PanelNavigation.EnterGroups`). Do not derive it again from how many groups are shown.
  - A plain Enter never pastes while the keys are in the column, and Space is never typed there. A key that ends the
    walk can still be down afterwards: its repeats arrive when the walk is over, where Enter pastes and Space is
    typed. `groupKeyHeld` remembers the key the groups took. A new way for a key to end the walk needs the same care.
- **A key the panel takes in `Root_PreviewKeyDown` is not typed into the search box** (verified for Space, §2.31).
  The search box gets every key the panel does not take, and people type into it at any time. So take a key that
  produces a character only in a state that the footer names, and never for a repeat of an action that reloads the
  list.
- **The footer's key hint trims its end and shares its line with the status.** At the default width, about 41
  characters fit next to the count of three named groups. Write a hint in the order of what the reader needs, and look
  at it on screen with the longest status its mode can have (the UI Automation text is never trimmed).
- **xunit data rows that need a note go into a `TheoryData` property with `[MemberData]`.** The documentation hook
  (`~/.claude/scripts/check-docs.py`) climbs from a method to its doc comment only over lines that end with `]`. A
  comment after `[InlineData(…)]` stops the climb, and the hook then reports "no /// doc comment" for a method that has
  one. Older theories have such comments: the hook checks only the lines that an edit touched.
- **The documentation hook skips the session scratchpad** (its `SKIP_PATH` matches `temp\claude\…\scratchpad\`), and
  files changed by a script are never seen by a PostToolUse hook at all. Work in a private worktree under the scratchpad
  (the way to land work beside other sessions) is therefore never checked: run the hook's `find_gaps` on the changed
  files yourself before committing (2026-11-08: it found 27 gaps in 7 files of `feat/localization`, all filled).
- **A resumable edit script must not decide "already applied" by the old text being absent** when the new text
  contains the old one: the old text is still found, and a re-run applies the edit again. On 2026-11-08 that doubled a
  line in `AppController.Start` (caught by its log line appearing twice in a test desktop).
- **claude-desktops Windows desktops, lessons of the rich-preview research (2026-10-16, §2.33):**
  - A long-lived helper started from `run_command` with `Start-Process -RedirectStandardOutput` inherits the agent's
    pipes: the call does not return, and the desktop's later calls queued behind it for minutes. Start helpers with the
    `launch` tool through `cmd.exe /c <exe> … > <log> 2>&1`, and read the log with `read_file`.
  - LibreOffice's first start shows a welcome dialog, and Calc a tip of the day; both take the keyboard (Esc).
  - The desktop's screenshot can show the Playwright-driven Edge page blank while the browser tool's own screenshot
    shows it; keys sent to that Edge after a click into the page worked.
  - A recursive `Copy-Item` of a folder tree into `Z:` failed with "Access … denied"; one zip copied there worked.
- **claude-desktops Windows desktops, lessons of the all-users work (2026-10-18, §2.34):**
  - **A guest reboot works,** and is how a sign-in is tested: `shutdown /r /t 3` from `run_command`. The guest signs
    "Docker" in by itself, and the agent's logon task brings the desktop back 60 to 100 s later. The desktop's
    `/readyz` answered 200 before the guest was back once, so check `LastBootUpTime` in the first `run_command`.
  - **SYSTEM through a scheduled task** (principal `SYSTEM`, `-LogonType ServiceAccount`, cmd.exe redirecting the output
    into a log) is how Intune and RMM agents run an installer. Wait until the task is neither Running nor Queued and
    its last result is neither 267011 (not run yet) nor 267009 (running). Its last result is the exit code.
  - **A new file in an existing `Z:` subfolder stayed invisible** in the guest, while a new top-level folder appeared
    at once. Small files went in with `write_file` instead.
  - **`find_elements` and `accessibility_tree` did not reach Settings** (the app's first window is the cloaked panel).
    A PowerShell UI Automation script run with `run_command` read and flipped its switches through `TogglePattern`.
  - `create_desktop` for Windows took longer than the MCP client's 300-s silence limit and was reported as failed,
    although the desktop came up: check `list_desktops` before creating another.
- **claude-desktops Windows desktops, lessons of the silent restart (2026-11-12, §2.46):**
  - **Windows desktops now go to a worker machine first** (a Proxmox host, `host="auto"`), whose persist folders live on
    that machine (`/mnt/drive2/claude-desktops/persist/<name>`), not on this PC's `Q:`; a build cannot be copied in from
    here. `create_desktop(…, host="pc")` still gives a desktop with its `Z:` on `Q:\claude-desktops\windows\desktops\`.
  - **`launch` of a PowerShell script opens a Windows Terminal window** that takes the foreground (Windows hands the
    console to Terminal). Background helpers go through `run_command` with `Start-Process … -WindowStyle Hidden`:
    Windows never hands a hidden console to Terminal (§2.16).
  - **A process started hidden gets its first `ShowWindow` call forced hidden** (STARTUPINFO's show state), so a test
    window it shows never appears. Spend the first call on a throwaway window (`feedback-form.ps1`).
  - **The pointer's shape is read with `GetCursorInfo`** (an I-beam over Notepad, the app-starting pointer during a start
    with feedback forced on); the desktop's screenshots do not draw the pointer.
  - **Whether a tray icon is registered**: `Shell_NotifyIconGetRect` on its window and id answers S_OK when shown, S_FALSE
    when hidden, an error when gone; sampled every ~15 ms it shows a hand-over that frame captures (24–34 ms a frame in
    a desktop, whatever the code) cannot.
  - Windows PowerShell's `Add-Content` failed with "Stream was not readable" on a log another script had just written;
    `[IO.File]::AppendAllText` does not read the file first.
  - The scripts (`probe.ps1` with watch, tray, shots, Win+V, copy and settings modes; `run.ps1`; `fastshots.ps1`;
    `trayswap.ps1`, `trayswap2.ps1`; `feedback-form.ps1`) are in session f1d22507's scratchpad, `restart-qa/`.
- **Dev builds for the owner's PC go through `tools/release/publish-dev.ps1`** (§2.39): built from a commit in a
  temporary worktree, never from the shared tree (another session's uncommitted work would ship), and offered by the
  update button instead of installed behind the owner's back. `-Install` restarts their app: only on their request.
  A development build's update button is lit by design (the way back to GitHub's latest release): not a bug to fix.
- **`git config` inside a `git worktree` writes the shared repository's config** (`.git/config`), not the worktree's,
  unless given `--worktree` (with `extensions.worktreeConfig` on). On 2026-11-09 `git config core.autocrlf false` in a
  private worktree turned it off for the main tree that other sessions work in; it was put back to `true` at once. Pass
  settings per command instead (`git -c core.autocrlf=false …`).
- **A process of the Microsoft Store edition never changes Windows through the registry** (§2.47). Its own `HKCU`
  writes and deletes land in the package's private hive and report success, so a feature that "works" there changed
  nothing.
  - Anything that writes a Windows setting (`DisabledHotkeys`, the Run key, `EnableClipboardHistory`, the user PATH)
    asks `AppController.IsStoreEdition` first, and that edition gets the documented way instead: the startup task, a
    button that opens Windows' own Settings page (`AppController.OpenWindowsSettingsPage`), or nothing.
  - A registry change notification never arrives in a package: poll the key's last-write time
    (`RunMruWatcher`'s `pollOnly`).
  - A new folder directly under `%LOCALAPPDATA%` is redirected: the edition's data goes to its `LocalState`
    (`AppPaths.PackagedDataDirectory`), through `Program.ResolvePaths()` and nowhere else.
  - What must be real is done by a child that is another program (Windows PowerShell running the package's
    `install.ps1`): such a child runs outside the package.
- **The Store edition makes no network request by itself, and records nothing before the welcome's Start.** The
  listing, the certification notes and the README's *Privacy* section say so. A new request or a new source needs the
  owner's decision first, then all three texts.
- **A Settings card an edition must not show is taken out of the page, not collapsed** (`SettingsWindow.ApplyEdition`):
  the search shows and hides every card of the page by itself, so a collapsed card comes back with the next search.
- **The Store edition's setup shows only on a start by hand** (§2.47). A start by Windows or by `bclip` that still has
  something to settle ends, before the instance lock where that can be told beforehand
  (`Program.ContinueStoreEditionInBackground`). A new question that edition must ask goes into `NeedsStoreSetup` and the
  setup's flow, never into a window opened from a background start (the same rule as the silent restart's).
- **Code that runs before `StartCore` must not ask `AppController.Settings`.** The Store edition's setup builds its
  window before the settings are loaded; the theme there is `settings?.Current.Theme ?? setupTheme`. The first live run
  crashed on exactly that.
- **`SettingsStore.TryReadSnapshot` returns the defaults for a missing file** and `null` only for an unreadable one.
  Where "there is no settings file" must be told apart, check `File.Exists` first (`ApplySetupLook`).
- **A list of another process's files is stale once that process closes.** SQLite removes a store's `-wal` and `-shm`
  files when its last connection closes, so files of another copy are listed right before they are copied, never
  before that copy was asked to exit (`BringDataOverAsync`).
- **Every parameter a mode of `install.ps1` is run with must belong to that mode's parameter set.** PowerShell refuses
  the whole command line otherwise, before the script's first line: no log, no result file, "ended with code 1". The
  Store edition runs `-Uninstall -InstallDir -KeepProcessId -ResultPath -LogPath`
  (`EditionUninstaller.UninstallParameters`); `EditionUninstallerTests.InstallScript_TakesTheUninstallParametersTogether`
  asks `Get-Command` for the real script's parameter sets, which runs nothing. The update mode's line is §2.29's
  contract.
- **The Store package's version is (X+1).Y.Z.0, computed by `package-msix.ps1`, for every release** (§3.5). Never set
  it by hand, and never change the rule after the first submission: the Store offers only the highest version.
- **Never register or start a package on the owner's PC, and never run the Store edition's setup or an uninstall flow
  there.** `msix_lab`, `Add-AppxPackage` and a packaged BetterClipboard belong in a claude-desktops Windows desktop.
  On this PC the setup would find the installed app and offer to replace it, and an uninstall gives Win+V back and
  restarts Explorer. A test certificate is written to files only, never into a certificate store of this PC.
- **Manifest lessons** (§2.47): a console alias is `uap5:AppExecutionAlias` with `desktop4:Subsystem="console"`
  (the `uap3` element refuses that attribute), and its `Application` needs `desktop4:SupportsMultipleInstances="true"`.
  The manifest template's comments may not hold `--` (XML).
  - **Never an application hidden from Start** (`AppListEntry="none"`): the Store refuses the package at upload
    ("specifies a headless app"), although MakeAppx packs it and Windows installs it.
  - **One `windows.appExecutionAlias` extension per application**; its `Executable` may be another program of the
    package. A second tool that needs a name of its own does not fit into this package.
  - `package-msix.ps1` checks both after every build (`Get-ManifestStoreProblems`).
- **Partner Center in the owner's Chrome** (2026-11-13; skill `browser`):
  - **Never click *Export listings*, or anything else that downloads.** The owner's Chrome asks where to save every
    download, so a download opens a Save As dialog on their screen. Capture the file inside the page: wrap
    `URL.createObjectURL` and `HTMLAnchorElement.prototype.click` for the length of the click, read the blob, and put
    both back.
  - **Tab numbers change when the DevTools connection restarts.** A snapshot taken by the old number landed on one of
    the owner's own tabs. Before every action on the working tab, check its address (`location.href` holds the
    submission's path), and after a pause find the tab again with `list_pages`.
  - **Never the browser tools' mouse `drag` on a listing's screenshots**: it exchanged two other cards than the
    ones named, and the page saved that at once. Dispatch the page's own drag events from a script
    (`tools/release/order-store-screenshots.js`) and read the order back after loading the page again.
  - **Hidden file inputs** take a file once they are shown and labelled (`style.display`, an `aria-label`), with
    `upload_file` on the labelled input of a new snapshot.
  - **Never tick an agreement for the owner** (the IARC terms and age-of-majority box), and never press *Submit for
    certification* without their go-ahead. Fill everything else, and leave those two to them.
  - Save each page before leaving it: only the listing import and the package upload save by themselves.
- **A submission the Store's API made must not be edited in Partner Center before it is committed**: the API can then
  no longer commit it. `submit-store.ps1` deletes a pending submission first, for the same reason.
- **The listing kit is the source of what the Store shows.** Change `docs/store-listing.md`, run
  `python tools/release/test-store-listing.py`, and import the generated file (`store-listing.md` §4). A text edited
  only in Partner Center is overwritten by the next import and was in no review.
  - **After every import, export again and run `make-store-listing-csv.py --check --template <export>
    --screenshots 6`**, then put the screenshots back in order on the listing pages it names (§3.5). An import
    shuffles them, and the first screenshot is the one the Store shows first.
  - **Compare a generated import file with the export it was made from, cell by cell, before uploading it.** The
    cells that differ must be exactly the ones you meant to change. That comparison caught captions about to go
    under the wrong pictures (2026-11-13).
  - **A right-to-left listing is checked in a right-to-left render** before it is imported: signs after a Latin
    word, numbers and brackets move.
- **When a release changes what the Store edition does** (a new data source, anything sent over the network, text
  written by an AI model): the description, the certification notes, the age-rating answers ("content that is not
  part of the download", "generated AI content") and the *generative AI* declaration under Properties change in that
  release's submission, by hand, before it is committed. The pipeline carries all of them over unchanged.
- **claude-desktops Windows desktops, lessons of the Store work (2026-11-13):**
  - `Add-AppxPackage` and `Remove-AppxPackage` from `run_command` can outlast the call: start a script with
    `start "" /min powershell … -File <script>` that writes a log, and read the log.
  - A loosely registered package (`Add-AppxPackage -Register`) takes changed files in place (`tar -xf <zip> -C
    <folder>`); register again only when the manifest changed. Start it by hand with
    `explorer.exe shell:AppsFolder\<family>!App`.
  - `%TIME%` in a cmd line with `&` is expanded when the line is parsed, not when the command runs.
  - A file uploaded to a worker desktop's persist folder needs `chown -R 100000:100000` on the worker, or the guest
    can read it but not replace it.
  - **`delete_desktop` on the worker can crash the gateway.** On 2026-11-13 the delete's config reload made Caddy in
    CT 810 panic and stay down (no automatic restart): every other worker desktop was unreachable until
    `ssh worker 'pct exec 810 -- systemctl start caddy'`. After a delete, look at `list_desktops` (`ready` of the
    others) and at the persist folder, which the interrupted delete left behind.
- **claude-desktops Windows desktops, lessons of the picture-files work (2026-11-13, §2.49):**
  - **A crash dump from a desktop:** Tiny11 has Windows Error Reporting off
    (`HKLM\SOFTWARE\Microsoft\Windows\Windows Error Reporting` `Disabled` = 1) and keeps no report. Set `Disabled` to 0
    and `DontShowUI` to 1, and add `LocalDumps\BetterClipboard.exe` with `DumpFolder`, `DumpType` 2 and `DumpCount`
    (with `reg add`: `New-Item` on that path failed). The next crash writes a full dump (466 MB), and for a moment a
    second `BetterClipboard.exe` runs: WER's copy of the process for the dump. Copy the dump to `Z:\` and read it on
    this PC with `dotnet-dump` (no WinDbg here).
  - **A new folder made in the persist folder from the host** stayed invisible to directory listings and wildcards in
    the guest (`dir Z:\`, `Copy-Item Z:\pkg2\*`) for over a minute, while its files opened by their full names at once:
    copy them one by one (`[IO.File]::Copy("Z:\pkg2\<name>", …)`).
  - **Photos** is no process with "Photo" in its name: its windows belong to `ApplicationFrameHost.exe`. Find them with
    `list_windows` (title "… - Photos") and close them with the `window` tool, so the next test starts Photos cold.
  - The first `explorer.exe /select,"<file>"` after the installer restarted Explorer opened the folder without selecting
    the file; every later one selected it (seen once; Explorer's, not the app's).
- Commits: per the user's global rules (message file in scratchpad, `git add` + `git commit` in one
  command, extensive messages, never amend).

---

## 5. Verification status (2026-09-25, Windows 11 25H2 26200.8875, 1920×1080 @100 %)

| Feature | How | Result |
|---|---|---|
| **The first submission sent for certification** (2026-11-13, about 22:53 local, §3.5; the owner: "You tick and submit … you the asisstant do it all for me through browser"). In the owner's Chrome, in a tab of my own (closed afterwards): on the English listing page the six old screenshots deleted and the eight new ones uploaded one by one (their mean colors matched the files' in order); saved; an in-page export showed English with 8 pictures and the other languages with the old 6; their picture cells emptied and the kit's import written with `--copy-pictures-from en-us --screenshots 8 --screenshots-in-order en-us`; a cell-by-cell comparison with the export: only those picture cells, the captions and the copyright row's usual form changed; imported; the next export: 8 pictures and 8 captions in every language, the same eight new assets in every language, English shuffled (4, 5, 6, 1, 2, 8, 7, 3); `order-store-screenshots.js`: 4 swaps, order 1–8; the next export: "is what the kit says, with 8 screenshots in the kit's order in every language". Age ratings: the IARC agreement ticked and saved (Everyone, 3+, All ages); the overview then showed every section *Complete* and *Submit for certification* enabled; after the click "Product submission: In certification", every section *Read only*. On the way: chrome-devtools-mcp answered "Log.enable timed out" for ~10 minutes until this session's own backend was stopped and a new one started | chrome-devtools (`evaluate_script`, `upload_file`, `click`, snapshots to files); scratch `store/empty_other_pictures.py`, `store/json_to_csv.py`, `store/chrome_dialogs.ps1` (session 66cdd808) | ✅ submitted. Certification pending |
| **The Store listing's screenshots retaken on the released 0.2.11, with the owner's own history as demo data** (2026-11-13, §3.5; the owner: "Retake on 0.2.11, English", and the data "copied from my computer … after removing ALL sensitive and guids"). **The data:** the owner's store exported read-only (2,772 entries and every prompt as candidates), screened by rules and read item by item; 214 entries (42 of them `generic`, added), 2 pins, 4 groups, 55 + 44 prompts, 40 PowerShell commands, 30 public pictures; the builder's last checks 0 problems (with a negative control: a GUID, a GUID's head and a `ghp_` key are each named), a second independent scan found nothing private, and every path lies in the four public repositories. `seed-demo.cs`: builds with 0 warnings and 0 documentation gaps; refuses on this PC (exit 2) and creates nothing; `--check` reads the zip whole. **The desktop** (claude-desktops on this PC, Tiny11 25H2, 2560 × 1440 at 150 %, dark; deleted afterwards): the release's x64 Store package (74,065,536 bytes, the run's artifact) test-signed for the Store's own publisher installed as `Nucs.BetterClipboard 1.2.11.0`, family `Nucs.BetterClipboard_mgez7y7xa0my4`; language picker → welcome (captured) → *Not now* (only a log and a settings file); the loader in 3.3 s ("214 entries (30 pictures, 31 Win+R runs, 2 pinned, 4 groups)"); the app's next start imported 55 Claude Code and 44 Codex prompts in 0.1 s each, found the screenshots' files, Win+V through the hook, 0 WRN/ERR. Eight scenes captured; Settings showed "213 items · 3.8 MB · 2 pinned" (two entries share a content hash). Six went through `screen-shot.py`; the two viewer scenes are used as captured. Seen on the way: the first summon's frame of unbound defaults (§6), again | claude-desktops MCP tools (UI Automation, keys, wheel, screenshots, `run_command`); scratch `demo/` in session 66cdd808's scratchpad (`owner_store.cs`, `screen.py`, `build_demo.py`, `extra_scan.py`, `count_kinds.cs`, `sign_release.ps1`, `vmkit/setup.ps1`, `make_zip.py`) | ✅ the pictures. Not shown: Hebrew or other languages' screens (the owner chose English for all five listings) |
| **v0.2.11 released** (2026-11-13, the owner: "I merged, lets release. push any left over too."). Pull request #6 (`release/0.2.11` at `e5b90c5`; title, chosen with two HTML questions: "BetterClipboard 0.2.11: less memory, screenshots keep their files, long texts in full with Markdown and colored code"; description the final notes plus "Refs #3", so issue #3, an outside user's memory report, is linked and left open for the owner to answer) was merged by the owner with a merge commit (`1b03a45`). `main` then gained docs only, pushed: a centered README header (`d86563a`, with the local merge `a9cd7fa`), "Light on memory" (`ea6064c`), the Chinese README's missing features (`2d5371b`, `d14c65a`) and the README in Russian, Spanish and Hebrew (`8864660`); no app, installer or version file. So the tag went on `main`'s head, `8864660`, as for 0.2.8 (only docs after the last app commit), with CI green on it; the README anchors the notes link to still exist there. Tag `v0.2.11`: annotated, `--cleanup=whitespace`, its message the newest draft (`e5b90c5`'s, the same text the pull request carried). Release run 43478898156: attempt 1 failed at Test on one Windows test, `PromptArchiveTests.Claude_PruneByRenameOver_StoresNothingTwice` ("Access to the path is denied" from the test's own rename over `history.jsonl`; §6), with nothing published; attempt 2 (`gh run rerun --failed`, unchanged code) green. Published 18:15:51Z as Latest: zips x64 75,289,152 / ARM64 72,781,950 bytes, `.7z` 45,827,113 / 41,684,200, `betterclipboard.0.2.11.nupkg` 87,558,031 (attached only: no `CHOCOLATEY_API_KEY`), `install.ps1`, `SHA256SUMS.txt`. Verified through the API: 4 of 4 sums equal GitHub's digests, the downloaded x64 zip checks, the body is the notes plus the code-signing footer with no mojibake, the `install.ps1` asset equals the tag's but for CRLF, both binaries report `0.2.11+8864660…`, and `irm \| iex` serves the same installer. The Store step was left alone on the owner's word ("Anything related to publishing to microsoft store, dont worry about it for now"). `release/0.2.11` deleted on GitHub | `gh pr view`, `gh run view --log-failed`, `gh run rerun --failed`, `gh api …/releases/tags/v0.2.11`, `gh release download`, `sha256sum -c` | ✅ |
| **0.2.11 prepared for release** (2026-11-13; the owner: "Please prepare for releasing 0.2.11, reread head and tackle the release notes"). **The notes:** the newest draft (`5831dba`'s message) read against every commit since v0.2.10 and their records: every user-visible change is in it; the fixes their messages name besides the four in *Fixed* were caught before a release or are test-only; the Store edition left out on purpose (not in the Store until Microsoft certifies it; the owner's call), and `bclip`'s new `file` field like every earlier release's CLI details. One wording change: "a copy made meanwhile is recorded" → "anything you copy meanwhile is recorded" ("a fresh copy" two lines earlier is the app). Its four README links are headings at `5831dba` (the Chinese README has the same anchors); no line over 117 columns; no span cut by a wrap; GitHub's renderer (`gh api markdown`): six lists, 21 items, the table, the code block, four links, no Markdown left in the text; the files table matches what `release.yml` attaches. **The release job's steps in a private worktree at `5831dba`** (no uncommitted work of any session): `dotnet test --solution BetterClipboard.sln -c Release` 2,338 = 2,335 passed + 3 opt-in skipped, 0 failed (Core 1,895, Windows 443); `package.ps1 -Version 0.2.11`: zips x64 75,385,661 / ARM64 72,804,467 bytes, `.7z` 46,113,639 / 41,919,234, `SHA256SUMS.txt` checked, both exes `0.2.11+5831dba…`, the zip's `install.ps1` the commit's, only the known CS0108; `test-update.ps1` 17 of 17; `test-iex.ps1` in 7.5.8 and 5.1; `package-chocolatey.ps1` 84.0 MB (pack only); `package-msix.ps1 -ForStore`: x64 74,183,320 / ARM64 71,908,242 bytes, `1.2.11.0`, `Nucs.BetterClipboard`. **CI's new Store steps** (never run on GitHub yet): the test-signed `package-msix.ps1` build, `test-submit-store.ps1` 39 of 39, `test-store-listing.py` and `check-store-listing.py` pass. Not run here (they change the machine; CI and the release job run them): `test-chocolatey.ps1`, `test-all-users.ps1`. Nothing pushed, tagged or opened as a pull request: on the owner's word (§3.1 "Through a merge request"). The other session's uncommitted AI conversation work is not part of the release | worktree + the release job's scripts, `gh api markdown`, scratch checks (session 9cd38837's scratchpad) | ✅ ready. The Store steps of `ci.yml` and `release.yml` first run on GitHub with the pull request |
| **The text peek's follow-up, live** (2026-11-13, §2.50; the owner: "it close immediately if i move the mouse BUT it should react to middlemouse scrolling just fine. Only keep open ignoring mouse movement when clicking the eye at the corner" and "I dont think Markdown renders at this with a scrollbar"). The owner's 3,596-character note, read on this PC in the scratchpad only (`repro/detect.cs`): Markdown, drawn as a document of one page. A disposable claude-desktops Windows 11 desktop on this PC (`bc-peek-pc`, 1600×900, no GPU; deleted afterwards with its persist folder), builds `0.2.11-dev.peek1` and `peek2` from a private worktree, the panel 800 DIPs tall, the morning's BC-TEST texts plus an invented look-alike of the note (3,735 characters, 32 lines, also Markdown). **Markdown and the scrollbar:** the peek drew the look-alike as a document capped at the frame's height, with a scrollbar, and three notches over the text scrolled it to its end. **The wheel:** over the text one notch moved it about 107 px in `peek1` (RichEdit's step and the redirect's together) and 91 px in `peek2` (RichEdit's alone); with the peek opened from the card's last visible line, below the frame, one notch moved it 53 px (the redirect's three lines); the peek stayed open every time. **Moves:** 10 px over the card closed it, and 7 px after its menu. **The menu:** a right-click with the mouse still opened it (*Copy* disabled, *Select all*, the card's items); moving onto *Select all* and clicking kept the peek, which then held the whole selection; a second right-click's *Copy* recorded the drawn text (all of it; list numbers with a tab, no blank lines). **Esc** closed the peek and left the panel. **The eye:** the viewer stayed open through two moves across the screen. **A short text** (`summarize.py`, 24 lines): the frame fits its text. The app's log over both builds: 87 lines, 0 WRN/ERR | claude-desktops MCP tools (`move_mouse`, `scroll` at the resting spot, `click` with the right button, region screenshots measured in pixels, `run_command`); `repro/detect.cs`, `repro/deferred-lookalike.md` and the guest's `C:\qa\compare.ps1` (session c073d86d's scratchpad) | ✅ after the one fault it found (`peek1` scrolled the text twice per notch). Not run: a real GPU, a high-resolution wheel, touch |
| **Long texts: the peek and the text viewer, live** (2026-11-13, §2.50; the owner: "we can see the entire content if mouse pauses hover and we click the eye"). A disposable claude-desktops Windows 11 desktop on this PC (`bc-textview-pc`, Tiny11 25H2, 1600×900, no GPU; the worker's drive 2 was starved; deleted afterwards with its persist folder), self-contained builds `0.2.11-dev.text1` to `text5` of a private worktree run as isolated instances, BC-TEST data only (session c073d86d's scratchpad, `qa/make_qa_data.py`: a heartbeat-shaped XML prompt, JSON, C#, Python, Markdown, YAML, SQL, a diff, a short text, a 6 MB log, 2.3 MB of C#, and Claude Code and Codex histories). **Cards:** the "⋯" line and the eye only on cut-off cards. **Peek:** centered on the panel in the dark and the light theme; moves inside it keep it open; the wheel scrolls it; its menu has *Copy* (disabled until a selection) and *Select all* above the card's own items; *Select all* + *Copy* put the text on the clipboard (equal to the file, with CRLF line breaks); Esc closes it; the 6 MB log shows one page with "Showing the first 63,944 of 6,000,031 characters. F3 opens all of it.". **Viewer:** *Formatted* / *As copied*, *Wrap*, *Line numbers*, the language menu with "(detected)", Markdown drawn as a document; the size by A±, Ctrl+Minus and Ctrl+wheel; `TextViewerTextSize` 1.1 and `TextViewerWrap` and `TextViewerLineNumbers` false were saved, then turned back on; opened by F3, by the card menu's *View* and by the peek menu's; closed by Esc and the X; the gutter in line with the text at 100 % and 150 %; a dragged selection copied exactly. **Codex tab:** the prompt card with its peek, menu and viewer. **Pages:** the 6 MB log in 2–3 s as page 1 of 94, page 2 starting at line 863; the C# as page 1 of 36 with "66,273 lines · 2,300,172 characters · Colored up to the first 2,000,000 characters."; turning a page under 0.5 s. **Pictures:** *View* on a picture opens the image viewer, and Esc closes it. The app's log over every build: 0 WRN/ERR | claude-desktops MCP tools (`move_mouse`, `scroll`, `press_keys`, `click`, `find_elements`, screenshots, `run_command`); the app's log and `settings.json` | ✅ after the six faults it found (§2.50). Not run: a real GPU, Narrator, touch and pen |
| Long texts, the suite (2026-11-13, §2.50): Core 107 new — `TextLanguageDetectorTests` 62 (code in every language recognized, prose and Markdown left as Markdown, null and huge texts, a huge JSON judged by its head, the prose weight, the JSON reader), `SyntaxHighlighterTests` 19 (each language's forms, the limits, random input keeping its tokens ordered and inside the text), `StructuredTextFormatterTests` 9 (JSON and XML re-indented with every token kept, text left as copied, input declined, an external entity never resolved, cancellation), `TextViewRenderingTests` 17 (every character through RTF, control characters, token colors, Markdown's blocks, the views and the switch, the Markdown limit, sources, line starts, pages, the style). Mutations: pages cut at the limit instead of at line breaks (it survived until the tests' line breaks moved off the limit's multiples; then 2 tests failed); page tokens not moved to the page's positions (1 failed). The whole suite in a private worktree of `6c6ef8e` plus the change: Core 1,893 passed; Windows 445 (2 opt-in skipped, 1 explicit not run); a `--no-incremental` build with only the old CS0108; the documentation hook's checker 0 gaps; `catalog.py check` 0 missing in every language | the test executables, `dotnet build --no-incremental`, session c073d86d's scratchpad (`doc_gaps.py`, `escape_sweep.py`) | ✅ |
| **Pictures keep their files, live** (2026-11-13, §2.49; the owner: "any images we detect and has a path - we should keep the path persisted with the image"). A disposable claude-desktops Windows 11 desktop on this PC (`bc-imgfiles-pc`, Tiny11 25H2, 1600×900; the worker's drive was busy; deleted afterwards with its persist folder), BC-TEST pictures only: a fake ShareX folder through `BETTERCLIPBOARD_SHAREX_DIR`, PNGs written with chosen write times, Win+PrtScn, a Paint copy. **The update and the search:** the released 0.2.10 installed with the README's installer kept two ShareX screenshots and a Win+PrtScn one; then a package of this change (`0.2.11-dev.imgfiles`) installed over it with `install.ps1 -Package`: "Earlier ShareX screenshots: found the files of 2 of 2 in 124 ms" and "Earlier Snipping Tool and Win+PrtScn screenshots: found the files of 1 of 1 in 119 ms"; `bclip list --json` gave each its path, PNG, size and both times; a new ShareX screenshot kept its file as it was saved. **The menu** (a ShareX card): the dimmed "explorer_Live01Shot.png / PNG · 5.1 KB · modified 22 min ago" and the four items; *Copy path* put the path on the clipboard as text, *Copy image as a file* a file list with the drop effect "copy" (pasted in File Explorer, it copied the file), *Open file* opened it in Photos, *Open folder* File Explorer with the file selected (the first time after the installer's Explorer restart without the selection). *Save to temp and*: a byte-identical PNG under `%TEMP%\BetterClipboard\Images\<hash>\` with the original name; the Paint copy (no file) got only *Save to temp and*, and its PNG was named "Image 2026-11-13 115426.png". **The viewer:** the band's two rows above the picture, the name opening Photos and the folder File Explorer with the file selected. **A file deleted** after it was found: the menu's "chrome_Xk29Qa81Lm.png / moved or deleted" with only *Save to temp and*; the viewer's name and folder as plain text, the first row ending "moved or deleted". **Settings:** the search "temp" found the one card; 0 days saved `TempImageDays` 0; the silent restart after Settings closed kept the two temporary pictures; a real restart deleted them ("Deleted 2 temporary pictures …"; the wording for 0 days changed afterwards) and their empty folders. **Hebrew:** the menu's label and its five items read right, the facts line keeping "PNG · 5.1 KB" together. **A crash found:** the viewer's file-name link ended the app 2 of 2 times (§2.49: a WER dump read with `dotnet-dump` on this PC showed the UI thread in `ShellExecuteExW` under the link's Click); with the fix (`0.2.11-dev.imgfiles2`, installed over it) 0 crashes in three link clicks, one with Photos started cold, and the menu's *Open file*. The app's log over the day: 143 lines, 0 WRN/ERR (a fail-fast writes none) | claude-desktops MCP tools (`press_keys`, `click`, `find_elements`, `list_windows`, `window`, `run_command`, region screenshots); guest scripts `png.ps1`, `items.ps1` (bclip, content-free), `clip.ps1`, `setup.ps1` (session 2fc2c47a's scratchpad, `qa/`); `dotnet-dump analyze -c "clrstack -all"` | ✅ after the crash fix. Not run: the Microsoft Store edition, a file on a network share, a standard user account |
| Pictures keep their files, the suite (2026-11-13, §2.49). Core `SourceFileTests` 41 cases and Windows `ImageFileTests` 4 plus `ShareXIntegrationTests.EarlierScreenshot_GetsItsFileOnce` (both watchers' tests also check the file their captures carry). Mutations: a bump that no longer keeps the row's file (`coalesce` dropped) failed `Bump_ReplacesTheFileWholeOrKeepsIt`; the search without its 2-minute window failed `Match_ByTimeThenPixels`. The whole suite on `3492df9` plus the change, in a private worktree: 2,231 = Core 1,786 + Windows 445; 2,228 passed, 3 opt-in skipped, 0 failed. A full build with only the known CS0108; the documentation hook's checker 0 gaps in 31 files; `catalog.py check`: 0 missing in every language. `install.ps1`'s `Remove-TemporaryPictures`, taken out of the script's syntax tree with `TMP` and `TEMP` pointing at a scratch folder: 9 of 9 checks in PowerShell 7.5.8 and 5.1 (the folder deleted with a scoped instance's subfolder, a neighbor of another name kept, a picture held open kept with the note, deleted at the next run); `test-iex.ps1` passes in both | the test executables, `dotnet test --solution`, scratch `test_remove_temp.ps1`, `doc_check.py` | ✅ |
| **The prepared release commit tested, and the pipeline's connection planned** (2026-11-13, §3.5). The whole suite on `87bfd49` (Version 0.2.11), in a private worktree that holds exactly that commit: 2,185 = Core 1,745 + Windows 440; 2,182 passed, 3 opt-in skipped, 0 failed. For the connection, Microsoft's pages were read: associating a tenant takes "the Microsoft Entra ID credentials for the tenant that you want to associate" and no Global administrator; managing applications takes "a Manager account that also has global administrator permissions for the Microsoft Entra ID tenant you're working in"; security defaults make every user register multi-factor authentication at the first sign-in (the 14 days of grace ended on 2024-07-29), with the Authenticator app or "any non-Microsoft application using OATH TOTP". Read-only looks at the owner's Azure directory with the Azure CLI (2.71.0): one user (the owner's personal account) and one verified domain; its sign-in policies cannot be read with the CLI's token ("required scopes are missing in the token"); `az ad app credential reset --help`: a key lasts one year by default. Nothing was created or changed there | `dotnet test --solution` through scratch `suite-on-bump.sh`; WebFetch of four Microsoft Learn pages; `az rest --method get`, `az … --help` (session 66cdd808) | ✅ the suite. The connection itself waits for the owner's word and one sign-in by them |
| **The owner's review of the Store draft, and what followed** (2026-11-13, §3.5). Four HTML questions, each option with a review page built from the kit (the English Store page with its facts, texts and six screenshots; the four translations, Hebrew first; the two version rules; the three ways to go on), rendered first with a headless Chrome and read. The answers: change the images, last, after asking again; all five languages, with review issues for Russian and Spanish; keep 0.2.x; prepare only, release and publish on the owner's explicit word. Done: issues #4 and #5 opened and read back through GitHub's API (titles and bodies whole, both labels, no mojibake, no mention); Microsoft's sentence on version numbers read on its page ("except for the first section, which cannot be 0"); the 0.2.11 notes drafted in v0.2.10's shape after reading the transcript of the session that formed those (52 lines, none over 117 columns, no wrap inside a link, code span or emphasis, no version but 0.2.11 named, rendered by GitHub's Markdown API: three lead-ins, four lists, one table, one code block) and put into the bump commit's message | `AskUserHtmlQuestion`; `gh issue create` and `gh issue view`; `gh api markdown`; `am_inspect.py turns … --minimal`; scratch `make_review.py`, `make_issues.py` (session 66cdd808) | ✅ prepared. Nothing pushed, tagged or submitted |
| **The listings in Partner Center checked against the kit, and two faults put right** (2026-11-13, §3.5; found while the review pages for the owner were made). **(1) Hebrew:** a right-to-left render of the listing showed "Notepad++" as "++Notepad", in the description and in a feature. The kit's reader now finishes a right-to-left listing's shown texts with U+200E after such signs (the app's rule of §2.37, in Python). After the third import an export held two marks in the Hebrew listing and none elsewhere. **(2) Screenshot order:** the export taken for that import showed the English listing as settings, search, welcome, groups, preview, panel: the second import had sent the kit's order (panel first). After the third import the Spanish listing ended welcome, settings and the Hebrew one welcome, preview, settings, although the file had not changed them, and English was unchanged, although the file asked for the kit's order. Two exports in a row were byte-identical, so the import does it; a caption stayed with its picture every time. The order was put right on the three listing pages with the page's own drag events (English five exchanges, after one made by the browser tools' mouse drag, which exchanged the first two cards instead of the sixth and the first and was saved at once; Spanish one; Hebrew two). Each page, loaded again, showed pictures and captions in the kit's order. The final export: `make-store-listing-csv.py --check --screenshots 6` answers "is what the kit says, with 6 screenshots in the kit's order in every language"; on the export from before the dragging it names English (5, 2, 6, 3, 4, 1), Spanish and Hebrew and exits 1. The generator's first version wrote captions by position: its file for the third import would have put five English captions under the wrong pictures, which a cell-by-cell comparison with the export showed before anything was uploaded; the file that was uploaded had every caption in its picture's row. `test-store-listing.py`: 52 of 52 (25 new: the marks rule; captions under their pictures in rearranged listings; contradicting captions; different pictures; pictures without captions and `--screenshots-in-order`; `--check` for order, texts, marks and misplaced captions). With the finishing step removed, 3 fail. `order-store-screenshots.js`, run on the English page: order 1 to 6, no exchange. The overview afterwards: every page *Complete* except Age ratings | chrome-devtools on the owner's Chrome (the same tab of its own); in-page export captures; a headless Chrome with a throw-away profile for the renders; scratch `export_to_csv.py`, `picture_order.py`, `verify_listing.py` (session 66cdd808) | ✅ the draft is what the kit says. Not seen: the Store's own rendering of the Hebrew page (nothing is published) |
| **The Store's upload check, and the one-application package** (2026-11-13, §2.47; the owner: "i registered, passed verification … Continue the rest"). **Refused first:** the two packages built with the reserved identity (`Nucs.BetterClipboard`, `1.2.11.0`), uploaded to the first submission's draft, came back with "Package acceptance validation error: The package file … specifies a headless app. You don't have permission to create a headless app. Please update AppListEntry="none" in the AppxManifest file" (the hidden second application of `bclip`), next to the expected notice that `runFullTrust` needs approval. **Why one alias:** MakeAppx on a small layout with the real manifest refused two alias extensions in one application ("The Extension element with Category attribute value "windows.appExecutionAlias" must only be declared once"; also one `uap3` next to one `uap5`) and packed the console alias alone. **The rebuilt packages** (one application; `bclip.exe` as its console alias with its own executable; x64 70.6 MB, ARM64 68.5 MB; both passed the build's new manifest check) were accepted: *Validated*, the `runFullTrust` notice only, device family Windows 10/11 Desktop ticked by Partner Center. **The test-signed twin, installed** (`Add-AppxPackage` after its certificate went into `TrustedPeople`): on a worker desktop `where bclip` found the alias, `bclip --version` printed "bclip 0.2.11" to the calling console and returned 0, `bclip status` returned 3 with "command-line access is off", and no `BetterClipboard.exe` alias existed; on a PC-host desktop `Get-StartApps` listed one entry ("BetterClipboard"), and with the app stopped and the command line switched on in its settings, `bclip status` started the app from `C:\Program Files\WindowsApps\…` and answered from the Store edition's data (9 items, 9 prompts), then `bclip list` printed its rows. The same desktop ran the first start (language picker, welcome, *Start*), Win+V through the hook, copies from Notepad, Explorer, Edge and Snipping Tool, a pin, three groups by drag and drop, a search with the whole-word toggle, the image preview and viewer, Settings, and two silent restarts: the app's log had 109 lines and 0 WRN/ERR. `Get-ManifestStoreProblems`, taken out of `package-msix.ps1`: the refused manifest named for two applications, a hidden one and two alias extensions; the rebuilt package clean; an alias that starts the app, and a manifest without an alias, named (6 of 6 in PowerShell 7.5.8 and 5.1) | Partner Center in the owner's Chrome (a tab of its own); MakeAppx 10.0.26100 on a scratch layout (`alias-test/try-variants.ps1`); claude-desktops `bc-shots` (worker) and `bc-shots-pc` (PC host), both deleted with their storage; scratch `test-manifest-check.ps1` (session 66cdd808) | ✅ after the manifest change. The worker desktop was unusable for more than the install (its drive starved: the install took 8.5 minutes, two commands hung for 70 and 90 s), so the rest ran on the PC host (the same setup in 16 s). Found there, not fixed: cards dragged onto groups kept a selection bar until the app restarted (§6) |
| **The first submission, a draft in Partner Center** (2026-11-13, §3.5). The name "BetterClipboard" reserved (Store ID `9PHL574X9BF9`; the family name computed from name and publisher equals Partner Center's). Pages saved and shown *Complete* on the product's overview: Pricing and availability (free, all markets, public), Properties, Packages (both *Validated*), Store listings (five languages), Submission options. Age ratings: every question answered "No", the preview read ESRB Everyone, PEGI 3, IARC 3+; **not saved** (the IARC tick is the owner's), so the page reads *In Progress*. Listings: the export captured inside the page (430 field rows, five language columns), filled by `make-store-listing-csv.py` and imported; the English listing then held the 1,547-character description, 11 features, the short description, 7 search terms and the copyright line. Six screenshots (2560 × 1440) and the 300 × 300 tile icon uploaded once in the English listing; a second export named them by web address, and the second import copied them into the other four languages (28 cells) with six captions each: *Store listings Complete*. The `runFullTrust` justification: the kit's 993 characters were refused ("must be 500 characters or fewer"), a 499-character one saved. The notes for certification (1,770 characters) were still there after a reload. Nothing was submitted: *Submit for certification* was not pressed | chrome-devtools on the owner's Chrome (skill `browser`): snapshots to files, guarded `evaluate_script`, `fill`, `upload_file`; no download and no dialog on the owner's screen | ✅ a complete draft. Side effect: after the DevTools connection restarted, one snapshot taken by the old tab number read one of the owner's YouTube tabs (read-only; the file was deleted at once). The packages in the draft are local builds: a release's own must replace them before the submission |
| The Store pipeline and the listing tools (2026-11-13, §3.5). `test-submit-store.ps1`: 39 of 39 against the stand-in (order of calls, one update, the packages marked, "What's new" in English only, untouched fields sent back as received, the archive uploaded in blocks byte for byte, a failed block retried, every refusal with its exit code and no request, no secret, token or upload address in the output). `test-store-listing.py`: 27 of 27 (the kit fits every limit; the generator fills the pristine export in the right rows and columns, with a byte order mark and CRLF; pictures copied between languages, never a trailer; a long justification, a long feature, an eighth search term, unequal captions, a kit without sections, a template with other languages or other row numbers refused). `package-msix.ps1 -ForStore` with the reserved identity: both packages, and the family-name check. The four workflow files parse as YAML. The documentation hook's check over the 8 changed scripts: 0 gaps; PowerShell 7.5.8 parses the three scripts, 5.1 parses `package-msix.ps1`. The whole suite in a private worktree of `fa6b3b4` plus this change, which changes no C# (so before the portable-update commit `b2aef33`, onto which the change was rebased afterwards): 2,151 = 2,147 passed + 3 opt-in skipped + `QuickSuccessiveCopies_AreAllCaptured` failed (the known timing test: also 3 of 3 alone at that moment, on a PC that was building packages and running a test desktop). **After the rebase onto `b2aef33`:** the test-signed x64 package built from the rebased tree in 65 s (70.7 MB, 511 files, 35 logos; the build's own checks passed; only the known CS0108), and again `test-submit-store.ps1` 39 of 39, `test-store-listing.py` 27 of 27, the kit check, 0 documentation gaps and the parse checks | the scripts themselves; `doc_gaps.py`, `parse_check.ps1`, `rebased-build.sh` (session 66cdd808) | ✅ the scripts. Not run: `submit-store.ps1` against Microsoft's service (it needs a published first submission and the three secrets), the new workflow steps on GitHub (they run with the next push and the next tag) |
| **A portable copy updates itself; a newer copy takes over; the sign-in entry follows** (2026-11-13, §2.48; the owner chose "Portable updates itself", "Newer copy takes over" and "Sign-in entry follows", and "Fix it now" for the tray). A disposable claude-desktops Windows 11 desktop on this PC (`bc-portupd-pc`; the worker's drive 2 was I/O-starved, "full" ~60 %; deleted with its storage), x64 packages 0.2.11 and 0.2.12 of the change, a stand-in feed in the guest (`feed.ps1`, an `HttpListener` on 127.0.0.1). **In place:** 0.2.11 extracted into `Downloads\BetterClipboard-0.2.11-win-x64` beside a file of the user's own (`notes.txt`), run as an isolated instance on the feed: "this copy is not installed by the installer (it updates itself in its own folder…); automatic check on"; 60 s later one request (`User-Agent: BetterClipboard/0.2.11`) found 0.2.12; the lit button and the weekly reminder; the dialog "You have 0.2.11. Released just now. Updating downloads it from GitHub, checks it, puts it into this copy's folder and restarts BetterClipboard. Your history is kept." with *Update and restart*, *Later*, *Skip this version*. Approved at :35.45, verified :36.46, the old copy exited :38.86 (asked through its Exit event: "It exited after 508 ms"), 476 files replaced in 1,892 ms, 0.2.12 started at :42.04 from the same folder with `--background`: "The update to 0.2.12 was installed.", the same store ("new key: False"), the BC-TEST copy made before still listed, the button plain. Afterwards 477 files (the release's 476 and the user's notes, hash unchanged), product version 0.2.12, no backup folder, no staging folder (removed by the new start), no Mark of the Web. **Rollback:** back to 0.2.11 and a hidden process holding `THIRD-PARTY-NOTICES.md` without delete sharing: after that file's 10-s retry the ~460 files already replaced were put back in 0.4 s, and 0.2.11 started again (477 files, the notes intact, no backup folder), with the WRN "The update to 0.2.12 was not installed: THIRD-PARTY-NOTICES.md could not be replaced (The process cannot access the file because it is being used by another process). The previous version was put back." **Take-over and sign-in entry** (the regular instance): 0.2.11 in folder A running in the tray, *Start with Windows* naming A; 0.2.12 extracted into folder B and opened like a double click: A logged "Exiting.", and 0.6 s later "BetterClipboard 0.2.12 starting (…, took over from BetterClipboard 0.2.11 (process 4948), which was asked to exit)" and "Start with Windows now starts this copy (0.2.12): its entry started an older BetterClipboard (0.2.11) from another folder."; B's Settings opened with *Start with Windows* On; the entry named B. A opened again while B ran: it handed over and nothing changed. A guest reboot: Windows started B at sign-in. The tray icon's left click opened the panel, its right click the menu | claude-desktops MCP tools; guest scripts `feed.ps1`, `state.ps1` (content-free), `busy-explorer.ps1` (session 4886d50a's scratchpad, `qa1/`, `qa2/`); `make_feed.py` | ✅ Not run: a copy that runs as administrator, a standard user, ARM64, a folder on a network share |
| The tray icon after a refused `NIM_ADD` (2026-11-13, §2.48): `TrayIconTests` 3 with a stand-in `Shell_NotifyIcon` (accepted at once; refused but there: a `NIM_MODIFY` finds it, it is set up, later tooltips and the removal are sent; refused and absent: tried again on the timer until accepted, then no more tries). Mutation: without the `NIM_MODIFY` probe the second test fails. Live attempts to provoke a refusal failed: with the guest's taskbar Explorer suspended (`NtSuspendProcess`) 12 s and 60 s after BetterClipboard started, and 8 s before plus 25 s after, `NIM_ADD` waited and then succeeded (Windows 11 26200; the app's start waited with it). So the starved guest's earlier refusal was not a hung Explorer timing out; most likely a notification area not ready yet at a slow start **[inferred]** | `TrayIconTests`; `busy-explorer.ps1` | ✅ unit-tested; ⚠️ the refusal not reproduced live |
| Portable update, take-over, sign-in follow, tray: the suites (2026-11-13, §2.48). Core 13 new (`PortableUpdateTests` 10: which offers install in place, the staging name, the result file's round trip, the take-over rule; `FollowThisCopy_RewritesOnlyAnEntryForAGoneOrOlderCopy`; `Install_PortableCopy_UpdatesItsOwnFolder`; `UpdateAvailable_PortableCopyInItsOwnFolder`). Windows 21 new (`PortableUpdateTests` 18: the release's files replaced and the others kept; a file held open puts everything back; a DLL mapped as an image (`LOAD_LIBRARY_AS_IMAGE_RESOURCE`, as ShareX holds `DWriteCore.dll`) renamed aside and replaced, its old copy removed by `CleanLeftovers` once free; a folder without the program untouched; a copy that does not exit ended; the command line (9 cases); the installer's unpack, refusals and contract round trip; the write probe; the version read; `TrayIconTests` 3). Mutations: no rollback → `Run_AFileHeldOpen_PutsThePreviousVersionBack` fails; no `NIM_MODIFY` probe → `Add_RefusedButThere_IsSetUp` fails. In a private worktree of `c1294f8` plus the change: Core 1,745 and Windows 440 (2 opt-in skipped, 1 explicit), 0 failed; a `--no-incremental` build with 0 errors and no warning in a changed file; the documentation checker 0 gaps (it reported a planted one); `catalog.py check`: 0 missing in every language | the test executables; `doc_check.py` (session 4886d50a's scratchpad) | ✅ |
| **A portable copy's update, from a release zip** (2026-11-13; the owner: "try in a win desktop to download a portable older version, and check for update and see that update worked cleanly on the portable"; §2.29). The released **0.2.9** x64 zip downloaded from its GitHub release page with Edge (SHA-256 `dc07a28e…` = GitHub's digest), given the Mark of the Web Edge writes (the desktops' Edge saves into the persist share by policy, which drops it), extracted with Explorer's own zip handler into *Extract All*'s default folder `Downloads\BetterClipboard-0.2.9-win-x64` (476 files, the mark on each), started from Explorer: SmartScreen "Windows protected your PC … Publisher: Unknown publisher", *More info* › *Run anyway*. Three BC-TEST copies, one pinned, *Start with Windows* on. **Detection:** "Updates: this copy is not installed by the installer (updates come from the download page)"; "Update check: BetterClipboard 0.2.10 is available (running 0.2.9)"; the panel's update button lit, the weekly reminder shown; *Settings › Updates*: "… This copy was not installed with the installer, so it does not replace itself. Get the new version from the download page, or install it with the command."; the dialog: *Download page*, *Copy install command*, *Later*, *Skip this version*, then 0.2.10's notes. **Copy install command** (a PC desktop): the button read "Copied", the clipboard held `irm https://raw.githubusercontent.com/Nucs/BetterClipboard/main/install.ps1 \| iex`, the history still 3 items; pasted into Windows PowerShell (from Start) and run: SHA-256 of 0.2.10 OK (`33c14ab5…`), "Closing the running BetterClipboard" (the portable copy: "Exiting."), installed into `%LOCALAPPDATA%\Programs\BetterClipboard`, Win+V taken over (one Explorer restart), started 9 s later: the same store (`new key: False`), "Win+V registered with RegisterHotKey", "this copy is installed by the installer (it can update itself)"; the language picker (by design after 0.2.9, §2.37); "3 items · 130 B · 1 pinned"; *Start with Windows* On with the Run value naming the installed exe; "BetterClipboard is up to date"; 0 WRN/ERR. The portable folder stayed as it was (0.2.9); its exe started afterwards only handed over to the running 0.2.10. **Download page** (a worker desktop): it opened `github.com/Nucs/BetterClipboard/releases/tag/v0.2.10` in the default browser; the 0.2.10 zip (SHA-256 OK) extracted into *Extract All*'s default, a new folder `…\BetterClipboard-0.2.10-win-x64`; started (SmartScreen again) **while 0.2.9 ran: nothing changed** — the new process handed over to 0.2.9 and exited, which brought up its own Settings ("Version 0.2.9", "0.2.10 is available"). After *Exit BetterClipboard* the new copy started: 0.2.10, the same store, "3 items · 130 B · 1 pinned", settings kept, 0 WRN/ERR, **but the Run value still named `…\BetterClipboard-0.2.9-win-x64\BetterClipboard.exe`, its *Start with Windows* switch read Off, and after a guest reboot Windows started 0.2.9 again** ("BetterClipboard 0.2.9 starting (… background: True)"; its tray menu offered *Update to 0.2.10…* again). **Extracted over the same folder after *Exit*** (a PC desktop, reset): 0.2.10 from the same path, the same store, the Run value still right, no 0.2.9 file left (both releases ship the same 476 file names). **Found on the way, not about updates:** in a starved guest the app logged "Shell_NotifyIcon(NIM_ADD) failed; waiting for TaskbarCreated", the icon appeared anyway (`Shell_NotifyIconGetRect`: shown), and it was dead: two left clicks opened nothing and a right click showed no menu, while the app responded and Win+V opened the panel; the same right click on bc-port-a's icon (no such warning) showed the menu (§6) | claude-desktops: two worker desktops (bc-port-a, bc-port-b) and one on this PC (bc-port-pc, reset once; `host="pc"` because the worker's shared drive ran at 42–58 % "full" I/O pressure from another VM); Edge on the release pages; Explorer's zip handler (`Shell.Application.CopyHere`); guest scripts `state.ps1` (content-free; kept in session 4886d50a's scratchpad), `extract.ps1`, `uia.ps1`, `trayicon.ps1` (the desktops were deleted with their storage) | ✅ *Copy install command*: clean. ⚠️ *Download page*: clean only when the user closes the app and extracts over the same folder; two traps otherwise (§6). ❌ The tray icon after a timed-out `NIM_ADD` (§6). Environment, not the app: the worker's DNS took 44.6 s to fail a `wpad` lookup (proxy auto-detection), so the first automatic check there failed with "The server did not answer in time (the release list)" and *Check now* then worked; on the PC desktop `wpad` failed in 2.8 s and the first check worked. Timings on the worker (a 0.2.9 exit that took ~5 minutes, a 9.9-s summon) are not judged |
| **The Microsoft Store edition, live** (2026-11-12 and 13, §2.47; the owner: "I want to publish my product to microsoft marketplace", then "Can't we uninstall it? But we migrate user's data successfully, validate and cleanup. We must validate all access permissions before starting (preflight checks)"). One disposable claude-desktops Windows 11 desktop ("bc-store", Tiny11 25H2 26200, 1600×900, no GPU, an administrator with UAC off; deleted afterwards with its storage), BC-TEST data only, test-signed packages `0.2.11-dev.store1` to `store7` registered loosely (and one installed, the next row). **A fresh install:** the language picker with five languages, then the welcome; before *Start* the data folder held a log and a settings file with the welcome not accepted, and no store; after *Start* the encrypted store, "Win+V is owned by another component; intercepting it with a keyboard hook", the startup task `Enabled`, 0 WRN/ERR; the real Run key empty and `DisabledHotkeys` absent. **Use:** a copy captured, Win+V opened the panel at Notepad's caret, Enter pasted; `bclip status` and `list` through the alias; `BetterClipboard.exe --show-flyout` and `--exit` through the alias; a Win+R run recorded by the poll ("Win+R: 1 new run found (watch), 1 recorded."). **Settings:** no *Release from Explorer*; "Version … · Microsoft Store edition"; *Updates from the Microsoft Store* with *Open in Microsoft Store*; Windows' clipboard history as a button; the alias sentence instead of the PATH button; the startup switch turned the task off and on. **Silent restart** after Settings closed: the new process was the Store edition with the same data folder, 0 warnings. **Sign-in** (a guest reboot): the startup task started the app 37 s after boot, without arguments, into the tray, no window. **The move from the released 0.2.10** (installed with the README's command; Russian, 3 items with a pin, the command line on, Win+V released from Explorer, on the PATH; running): the setup in Russian, four checks passed, *Move*: "3 file(s), 160,225 bytes brought over and checked (3 item(s))", uninstalled in 3 s, old folder removed; afterwards no Run value, no Installed-apps entry, no shortcut, no install folder, the PATH entry gone, `DisabledHotkeys` empty, Explorer restarted once; the welcome said the 3 items came over and asked no language; after *Start* the same 3 items with the pin, Russian, the command line on, Win+V through the hook. **A failing check** (write access to the copy's folder taken away): the third line failed and named the folder; *Check again* passed once access was back. **Quit** on the first page: everything untouched; after a failed uninstall step: the copied history removed again and the closed copy started again with its 3 items. **A second launch** during the setup: the same window came forward. **The installer run while the Store edition is installed:** it warned before installing. **A background start** with another copy installed (running, and not), and before the welcome: ended before the lock with its log line; the other copy kept running. **An edition that already has a history:** the page said the other history stays; the copy was uninstalled, its data folder left alone. **A history left behind** by an uninstalled copy: imported without a question (4 items), old folder removed. ***Not now***, then a start by hand: the welcome again. ***Open with Win+V* off:** "Alt+Win+V registered with RegisterHotKey (no keyboard hook needed)" | claude-desktops MCP tools (`launch`, `press_keys`, `click`, screenshots, `run_command` with cmd); guest scripts `state.ps1` (a content-free report of the registry, folders, settings and processes), `register-store3.ps1`, `install-0210.ps1`, `remove-store.ps1` (not kept: the desktop was deleted) | ✅ after the faults it found (§2.47 "Faults the live runs found"). Not seen: the guided pages for a copy for every user and a Chocolatey copy, the Spanish and Chinese setup pages. Not run: a standard user, UAC on, a real GPU, a package served by the Store, the Windows App Certification Kit |
| **The signed Store package, installed** (2026-11-13, the same desktop): `Add-AppxPackage` of `store7.msix` after its test certificate went into `LocalMachine\TrustedPeople` (signature kind Developer); Windows accepted package version `1.2.11.0` and ran the app from `C:\Program Files\WindowsApps\BetterClipboard.Unreserved_1.2.11.0_x64__…`. The released 0.2.10 was installed again, in **Hebrew**, and ran: the setup and the welcome showed in Hebrew; the move: "3 file(s), 160,216 bytes brought over and checked (5 item(s))", uninstalled, old folder removed, the registry clean; after *Start* the panel in Hebrew with the 5 items, Settings in Hebrew. Closing Settings: the silent restart's new process ran from the `WindowsApps` folder (`--background --restart-after … --clipboard-sequence 25`), "Silent restart: the shortcuts were held during the switch; 0 press(es) caught.", the hook installed again. Removing the package (6 s): no package, no data folder, no aliases, no process, the `WindowsApps` folder gone, the Run key empty, `DisabledHotkeys` absent; Win+V then opened Windows' own clipboard panel | the same tools; `install-signed.ps1`, `state.ps1`, `remove-store.ps1` in the guest | ✅ |
| **What a package changes** (2026-11-12, §2.47), measured with `tools/probes/msix_lab` in the same desktop: a console program registered as a full-trust package, each result compared with what an unpackaged shell saw. Own `HKCU` writes and deletes redirected, reads real; `RegNotifyChangeKeyValue` never signalled for another process's write (also through `HKEY_USERS\<SID>`), the key's last-write time moved; file change notifications arrived (Roaming, Local, outside AppData); a new folder under `%LOCALAPPDATA%` redirected, `LocalState` real; children that are other programs ran outside the package (CreateProcess, ShellExecute, a scheduled task, Explorer), the package's own program kept its identity (CreateProcess, ShellExecute, desktop-app policy 1 and 4; loosely registered and installed in `WindowsApps`); DPAPI blobs opened both ways; `Local\` mutexes and events shared; `StartupTask.RequestEnableAsync()` `Enabled` without a prompt; WinRT clipboard history `Success` without a window; uninstalling removed the package's data folder and left real folders and values | `run-lab.ps1` in the guest (result file read back) | ✅ |
| The Microsoft Store edition, unit tests and the suite (2026-11-13, §2.47): Core `EditionTests` 36 (the package's data folder and instance scope; `EditionSetupPlan.Next` for every mix of copies, other data and own history, and a walk through every step to the end; what is listed for a move; readable while a writer holds the file; nothing left by the write check; free space with its margin; a whole move that brings a history over, proves it and removes the old folder last; a rollback that leaves the original; a copy that is no history, and a history of another machine, refused; a copy that would damage a history refused; cancelling; what could not be deleted named; SQLite's check on a sound store; the welcome off until given). Windows `EditionUninstallerTests` 13 (the check and the uninstall run through the real Windows PowerShell against a stand-in `install.ps1` in a folder named with a space, a quote, an ampersand, percent signs and a non-ASCII letter: success proven by the program being gone, a reported failure in its words, "success" with the program still there refused, a missing script, every other kind of copy refused, a folder this account may not change named; and the real `install.ps1`'s uninstall parameter set read with `Get-Command`). Mutation checks: three one-line mutations in Core (`BringsDataOver` ignoring the own history, a Chocolatey copy no longer guided, a rollback that leaves the copied settings) failed 5 of the 36 `EditionTests`; with HEAD's `install.ps1` (before the parameter-set fix) `InstallScript_TakesTheUninstallParametersTogether` failed ("ResultPath", "LogPath" not found); both put back, both green. The whole suite on `1eede6f` plus this change, in a private worktree: 2,151 = Core 1,732 + Windows 419; 2,148 passed, 3 opt-in skipped, 0 failed. A `--no-incremental` rebuild: 0 errors, 0 documentation warnings; the documentation hook's check over the 36 changed code files: 0 gaps; `catalog.py check`: 0 missing and 0 to review in every language; `test-iex.ps1` passes in PowerShell 7.5.8 and 5.1; `install.ps1` and `package-msix.ps1` parse in both | the test executables, `dotnet test --solution`, scratch `doc_gaps.py`, `parse_check.ps1`, `mutate_editions.sh` (session 66cdd808) | ✅ |
| The Store package build (2026-11-13, §3.5), on this PC into scratch folders (nothing registered, no certificate store touched): test-signed x64 packages `store1` to `store7` (70.6 MB, 511 files, 35 logos), a test-signed ARM64 package (68.5 MB), and `-ForStore` with a made-up reserved identity: both unsigned packages, version `1.2.11.0` for the release `0.2.11`. Refused before any build: `-ForStore` with the placeholder identity, and `-ForStore` with a test certificate. `make_store_assets.py` rewrote the 35 logos byte for byte and wrote the listing's 300-pixel tile icon; `check-store-listing.py` passed on the kit (five languages) and failed on a kit with a 230-character feature (exit 1) and on a file that is not the kit (exit 2) | `tools/release/package-msix.ps1`, `tools/make_store_assets.py`, `tools/release/check-store-listing.py` | ✅ Not run: the ARM64 package on a device |
| The installer edition after the Store-edition change (2026-11-13, §2.47: `AppController.Start` / `StartCore`, `SingleInstance`, `Program.ResolvePaths` and `SettingsWindow.ApplyEdition` serve every edition). The solution build of exactly the landed code (`6f5a582`), as an isolated instance on this PC: its own data folder, capture paused, every source off and pointed at empty scratch folders, a shortcut nobody uses, a language already chosen; nobody had used the PC for over an hour. **Start in the tray:** the store opened encrypted, "Ctrl+Alt+Shift+F22 registered with RegisterHotKey (no keyboard hook needed)", "Updates: this copy is not installed by the installer …", "Panel prepared for the first Win+V in 472 ms", and `--exit` ended it ("Exiting."): 9 log lines, 0 WRN/ERR. **Its Settings,** read through UI Automation (no mouse, no keys): *Release from Explorer*, *Check for updates automatically* and *Start with Windows* are there, *Updates from the Microsoft Store* and *Open in Microsoft Store* are not, and the version line reads "Version 0.2.10" without the edition. **With the other session's uncommitted AI work** (a scratch checkout of this commit plus their diff and files, which merged cleanly in all nine files): 0 errors, 0 documentation warnings, 2,198 tests: 2,195 passed, 3 opt-in skipped. The installed app's process was the same before and after each run | scratch `smoke_unpackaged.ps1`, `settings_check.ps1` (it opens a window, so it runs only when nobody touched the PC for 90 s), `combo_check.sh` (session 66cdd808) | ✅ Not run: the installer edition's panel on screen, and an installed copy of this code (only the build output) |
| **The silent restart, live** (2026-11-12, §2.46; the owner: "User must not see or feel or have UI flash or open accidentally during this silent restart"). A disposable claude-desktops Windows 11 desktop ("bc-restart", Tiny11 25H2, 1600×900, no GPU, on this PC's host; deleted afterwards with its storage), self-contained packages of the change (`0.2.11-dev.restart`, then `0.2.11-dev.restart2` with the tray hand-over) installed with `install.ps1 -Package` (Win+V released from Explorer), English chosen, the command line on, BC-TEST copies only, Notepad in front. Probes in the desktop: the pointer's shape, the window in front, the app's processes and every visible uncloaked window of theirs every 15–20 ms; each process's tray icon (`Shell_NotifyIconGetRect`) every ~15 ms; the tray region captured every 24–34 ms. **Seven restarts:** the decision 5.3–6.4 s after Settings closed; private memory 131–146 MB before, 44–49 MB after (45 MB after the first start); Notepad in front throughout (except for the panel the test's own Win+V opened); no window of either process except Settings itself and that panel; the pointer unchanged through every switch; the logs 0 WRN/ERR. **Win+V 0.6 s into the switch** (the old process already gone): "the shortcuts were held during the switch; 1 press(es) caught", "Panel on screen 1039 ms after the shortcut". **Win+V 0.14 s in** (before the bridge held): the panel in the old process within 64 ms, "Silent restart called off: the panel opened. Trying again once it is quiet.", the new process gone and its hidden icon never shown; after Esc the restart went through. **Copies** 64 ms and 414 ms after the old process ended were recorded. **Tray, icon pinned:** the first version (new icon added visibly) had two icons registered for 64 ms, and a captured frame showed two; with the hand-over no frame changed in two restarts (the only other frame was the clock turning) and the sampler never saw zero icons. The new process's icon opens the panel and its menu; after an Explorer restart it came back visible | claude-desktops MCP tools; `probe.ps1`, `run.ps1`, `fastshots.ps1` (session f1d22507's scratchpad, `restart-qa/`) | ✅ Not run: the owner's PC (a real GPU), several monitors, a standard user account |
| The silent restart's controls (2026-11-12, §2.46), in the same desktop. **Pointer:** `GetCursorInfo` read an I-beam over Notepad; a start of the app with `STARTF_FORCEONFEEDBACK` from a foreground window gave 5 samples of the app-starting pointer, the same start with `STARTF_FORCEOFFFEEDBACK` none; a start with neither flag showed none from a hidden script, through Explorer and from a foreground window. **Tray:** a GUID-identified icon handed over by `NIM_MODIFY` with the new window (accepted) vanished when the first window went (frames); an icon added hidden and revealed by the other process 0.8 ms before that process deleted its own stayed in place in every frame, and Explorer accepted the reveal from the process that did not own the window | `control.ps1`, `feedback-form.ps1`, `trayswap.ps1`, `trayswap2.ps1` (same folder) | ✅ |
| The silent restart, unit tests and the suite (2026-11-12, §2.46): Core `SilentRestartTests` 14 (the rules: restart at once when quiet, not wanted without Settings / while exiting / when a language would be asked, each blocker and its log words, inclusive pauses, the most lasting reason named, what calls a started restart off); Windows `SilentRestartTests` 4 (a quiet start of a console program and its end, refusals, the idle-time read, nothing to hold refused) and two take-over cases in `ClipboardCaptureTests` (a copy made while nobody listened is read; an unchanged clipboard is not). Mutation: with the hand-over read removed, `TakingOver_ReadsACopyMadeInBetween` fails. The whole suite on `9cc1018` plus the change: 2,102 = 2,099 passed + 3 opt-in skipped; with the other session's uncommitted AI work added in a scratch checkout: 2,149, 0 failed. No warning in a changed file; the documentation hook's checker 0 gaps | the test executables, two worktrees, `doc_check.py` | ✅ |
| **What the memory is now, on this PC** (2026-11-12, §2.45; the owner: "What is consuming memory now"). `tools/probes/probe_memmap.cs` (new: read-only, no window, no injected thread, no dump) on the owner's installed 0.2.10 (2.5 days up: Private Bytes 255 MB, "Memory" 77 MB; native heap 97 MB charged / 51 MB resident, other private allocations 73 / 5 MB, NVIDIA DLL data 32 MB, write-combined 22 MB, .NET 12 MB) and on an isolated `0.2.11-dev.4.gc5dc861` with a seeded BC-TEST store (3,300 entries, 15,000 prompts), idle 60 s after its reclaim ("gave back 49 MB of private memory (158 → 109 MB)"): Private Bytes 108–110 MB, "Memory" 6.8–12 MB; native heap 34.8 / 7.1 MB, NVIDIA DLL data 32.0 MB, write-combined 20.1 MB, other private 6.5 MB, other DLL data 4.2 MB, .NET 4.2 MB, stacks 1.7 MB. The segment-heap grouping was checked separately: three 16 MB reservations share one context's (signature ^ address), the linked fourth belongs to the other context. The isolated instance's log: 0 WRN/ERR; the owner's app was only read | `probe_memmap.cs`, a signature check (session f1d22507's scratchpad, `memmap/sigcheck.cs`), GPU Process Memory counters | ✅ |
| **Memory, the final build, live** (2026-11-12, §2.45; the owner: "Optimize memory end to end"). A claude-desktops Windows 11 desktop ("bc-mem", Tiny11 25H2, no GPU, golden 20261025-1036; deleted afterwards), a self-contained Release publish of the change (`0.2.11-dev.mem+e0b40be`, before the log's one-decimal wording), isolated instances on copies of a seeded BC-TEST store (3,300 entries, 17 pinned, 30 in groups, 15,000 prompts). **8 Settings opens and closes:** private 87 MB at the start, 179 MB open, 137 MB after the first close, then 154 / 154 / 156 / 157 / 159 / 160 / 160 MB; the private working set 5.9–11.6 MB six seconds after each close; GDI 44 → 71 (+3 a cycle), USER 58 → 66. **Panel uses with the trim** (four summons through the instance's own shortcut, 40 s apart): the private working set 3.7–6.7 MB before each summon, 19–28 MB with the panel open, private 55–60 MB. **Title bar:** Windows' own, light in the light theme and dark in the dark one (screenshots), with the search box and the hero below it as before. The four runs' logs: 0 WRN/ERR; the reclaims logged "gave back 39 MB … (176 → 137 MB)" after Settings closed and "gave back 37 MB … (84 → 47 MB)" after the start's list refresh | `cycles.ps1`, `trimtest.ps1`, `shot.ps1`, `mem.ps1` (session f1d22507's scratchpad, `vm/scripts`): `GetProcessMemoryInfo` and `GetGuiResources` per step | ✅ |
| Memory before the change, and where it went (2026-11-12, §2.45). **0.2.10 on the same desktop:** start 87 MB private; Settings open 179 MB; after 4 more opens and closes 249 MB, a forced full collection giving back nothing, five Settings windows in a heap dump, GDI 52 → 191. **Experiments** with diagnostic builds (never committed): with the window's and timers' handlers removed, no window in the heap dump and a plateau; the GDI growth per open and close was 24–32 with the extended title bar (either API) and 3 without it. Images tab paged, then hidden 45 s: 98.8 MB (0.2.10) against 67.6 MB (the change); idle after the start 87 against 46 MB. Trim on against off (6 summons each): working set before a summon 3.8–6.7 against 24.6–37 MB, summon median 41 against 28 ms. **This PC's real GPU** (isolated instances, nothing on screen): the installed 0.2.10 held 267 MB private after two days with ~9 MB of live .NET objects (VMMap: process heap ~93 MB, driver ~90 MB); Settings cycled cloaked by a diagnostic build: 166 → 262 MB in five cycles (0.2.10) against 162 → 190–193 MB (the change); the final publish idle after the start: "gave back 49 MB of private memory (163 → 114 MB)", .NET heap 3 MB | VMMap (`-p PID out.csv`), dotnet-counters, dotnet-gcdump and dotnet-dump (the desktop needed the .NET 10.0.15 runtime), the scripts above, `local_run.ps1` | ✅ |
| Memory, unit tests and the suite (2026-11-12, §2.45): `MemoryReclaimTests` 11 (Core) and `ProcessMemoryTests` 2 (Windows, a collection that runs alone: a reclaim blocks every thread and trims the test process). `CliPipeServerTests.ClientHangUp_CancelsHandler` failed 10 of 10 full runs of the change's worktree and 0 of 7 of HEAD's; instrumented, its handler started at 1–2 ms in HEAD's order and never in the other; the same binaries copied to other folders passed 4 of 4 and HEAD's failed once: xunit's order follows the assembly's path. Rewritten to hang up once its handler runs: 4 of 4 full runs from the failing folder, 3 of 3 alone; with the server's hang-up handling removed it fails. The whole suite on `e0b40be` plus the change: Core 1,682 passed; Windows 400 (397 passed, 2 opt-in skipped, 1 explicit not run); a Release publish with only the known CS0108 | the test executables, two worktrees, a mutation of `CliPipeServer` | ✅ |
| **The merged tabs in time order, live** (2026-11-11, §2.44; the owner: "The first row in Claude tab is always old one"). A disposable claude-desktops Windows 11 desktop ("bc-taborder", Tiny11 25H2, 1600×900, golden 20261025-1036; deleted afterwards with its persist folder), BC-TEST data only: a Claude Code `history.jsonl` with four prompts sent 3 days to 1 hour ago, and a PowerShell history with three commands. **Before, on the released 0.2.10** (the README's installer, `-Version 0.2.10`, SHA-256 OK): "old prompt A" and "continue" pasted from the Claude tab into Notepad (each then kept); "sent after the keeps", "continue" and "newest" appended to the history afterwards, as Claude Code appends. The tab listed the two kept prompts first, above "newest" and "sent after the keeps", and selected the kept "continue". Pwsh: "old command 1" pasted from the tab, two commands appended after it; the kept command led the tab. **After, with a package of the fix** (`0.2.11-dev.taborder`, its own `install.ps1 -Package` over 0.2.10, history kept): Claude: "newest" first and selected, the kept "continue" at its re-send between "newest" and "sent after the keeps", the kept "old prompt A" below the prompts sent after it, footer "6 prompts (7 sent) · 2 kept". Pwsh: the two newer commands first, the kept "old command 1" at its place in the file ("2 min ago"). "old command 2" pasted from the tab after the newest command was typed: it led ("just now"). "BC-TEST an hour ago" pasted from the tab (kept), then sent again 2 min later: its card led with "just now" while `bclip list -f claude` gave its own last use as "3 min ago". The app's log over both versions: 42 lines, 0 WRN/ERR | claude-desktops MCP tools (`press_keys`, `type_text`, screenshots, `run_command`), `bclip prompts` / `list` in the guest | ✅ |
| The merged tabs in time order, tests (2026-11-11, §2.44): `MergedTabOrderTests` 19 (11 facts, 8 seeded random-list cases of 200 rounds each: every entry once, no content twice, every other item shown, pins first, times non-increasing, the same rows in one page or in random pages), `ShellTabTests` 4 new or changed, `EverythingTabTests` 1 changed (a kept file opened again sorts at its opening), `PromptStoreTests.KeptPrompts_JoinTheArchiveByTime` (a kept prompt's content hash equals its archived text's, on a real store), Windows `ReadCommands_KnowsWhenTheNewestCommandWasTyped`. Mutations: every stored entry first again (the old order) failed 22 tests in the four classes, no twin handling 17. The whole suite in a private worktree of `c156038` plus the change: 2,069 = 2,066 passed + 3 opt-in skipped, 0 failed (Core 1,671, Windows 398); the build 0 errors, no new warning; the documentation hook's checker 0 gaps on the changed lines | the test executables, `dotnet test --solution`, a mutation script, `doc_check.py` (session 5f26896e's scratchpad) | ✅ |
| **v0.2.10 released** (2026-11-10, the owner: "Lets merge and release a version"). Merge request #2 (`release/0.2.10` into GitHub's `main`, 37 commits: everything since v0.2.9, plus `9324621` and `5e4e731`, added after one scope question, and `27077f1`) was merged with a merge commit through `gh pr merge --merge --match-head-commit <full sha>` from outside the repository (a guessed full hash was refused first: "Head branch was modified"; nothing changed): `a2c5fd7`, parents `a347f06` and `27077f1`, its tree identical to `27077f1`'s. CI green on `27077f1` (run 42748098113) and on `a2c5fd7` (42748831071). Tag `v0.2.10`: annotated, `--cleanup=whitespace`, on `a2c5fd7`, its message the anchored lookup's draft (`27077f1`'s), equal to the notes file. Release run 42749428059 green at the first attempt; published 03:32:07Z, Latest, not a pre-release. Assets: zips x64 74,961,786 / ARM64 72,457,826 bytes, `.7z` 45,604,632 / 41,451,359, `betterclipboard.0.2.10.nupkg` 87,102,568 (attached only: no `CHOCOLATEY_API_KEY`, the step warned), `install.ps1`, `SHA256SUMS.txt`. All four sums equal GitHub's digests, and the downloaded x64 zip its own; the body is the notes plus the code-signing footer, with no mojibake; the `install.ps1` asset equals the blob except CRLF; `BetterClipboard.exe` and `bclip.exe` in the x64 zip report `0.2.10+a2c5fd7…`; the README's `irm | iex` URL serves `a2c5fd7`'s installer. Local `main` fast-forwarded to `a2c5fd7` (the shared tree's 19 uncommitted entries untouched); `release/0.2.10` deleted on GitHub. Not installed on this PC (not asked): it runs `0.2.10-dev.41.g9324621` (installed by another session on 2026-11-09 20:57Z), the newest build in the dev-builds folder, so its update button offers 0.2.10 at its next check (daily, or *Check now*); 0.2.10 reads the folder, so going back to a release no longer strands dev builds | `gh pr merge`, `gh api …/releases/tags/v0.2.10`, `gh release download`, `gh run view --log` | ✅ |
| **The 0.2.10 release candidate `5e4e731`: an update from the real 0.2.9, and the two features added after the merge request** (2026-11-10, the owner: "Lets merge and release a version"; asked one question, because `main` had gained `9324621` (Settings search, *Delete stored…*) and `5e4e731` (the language picker until a language is chosen) after merge request #2 was opened, the owner chose "Include + quick QA"). **Exactly `5e4e731`** in a private worktree: a `--no-incremental` solution build with 0 errors and 0 documentation warnings; the whole suite 0 failed, 3 opt-in skipped. **A disposable claude-desktops Windows 11 desktop** ("bc-rel-0210", Tiny11 25H2, 1600×900; deleted afterwards with its persist folder): the real 0.2.9 installed with the README's installer (`-Version 0.2.9`, SHA-256 OK, Win+V taken over); two BC-TEST copies and a Win+R `winver` recorded by it. Then a self-contained package of `5e4e731` (`0.2.10-dev.42.g5e4e731`, published into a scratch data folder, so the owner's update button saw nothing) applied as 0.2.9's update button would: the package's own `install.ps1 -Update -Package … -PackageSha256 … -InstallDir … -ResultPath … -LogPath …`, exit 0, result `ok`, 0.2.9 closed and the new copy started with `--background`. The picker ("Choose your language", English suggested) showed at once; Enter saved `en` with `LanguageChosen` true and kept 0.2.9's other settings; the log "Interface language chosen at a start with a history already kept …", 0 WRN/ERR. The panel listed 0.2.9's two copies and `winver`, with the Run tab. **Settings search:** "clpbrd" gave "8 settings match" under their section titles with the matched letters marked; Esc emptied the box. ***Delete stored: Win+R history*:** "1 item stored · 14 B", the part "Commands run with Win+R" with its example `winver`, the note on Windows' own list, *Also turn off*, a red *Delete 1 item* and *Cancel*; Esc closed it with nothing deleted; *Delete 1 item* deleted exactly one entry ("Deleted stored data: 1 history entry …"), `RunMRU` still held `winver`, and the panel lost its Run tab and kept both copies. **Hebrew** chosen in Settings, *Restart now*: no picker (`LanguageChosen`), and the search "notepad++" found 3 settings with the whole name marked (the U+200E that `9c7ba87` puts after "Notepad++" is folded away by the search). The notes draft gained a **Settings.** section for the three (in this commit's message) | claude-desktops MCP tools; the UI Automation helper of the 2026-11-09 pass (recovered from the session's log, with its fix); `review_build.sh` and `publish-dev.ps1` with a scratch `BETTERCLIPBOARD_DATA_DIR` | ✅ |
| **The language question after an update, live** (2026-11-09, §2.37). A disposable claude-desktops Windows 11 desktop ("bc-ask", Tiny11 25H2, en-US, 1600×900; deleted afterwards with its persist folder). The released 0.2.9 installed with the README's installer (`-Version 0.2.9 -NoLaunch`, SHA-256 OK), started in the background, two BC-TEST copies made; its own `settings.json` written by 0.2.9 (`--set-update-check on` while it was closed: 3,812 bytes, no language member); started again. A self-contained package of HEAD (`9324621`) plus only this change (`0.2.10-dev.ask`), applied the way the update button does: the package's own `install.ps1 -Update -Package … -PackageSha256 … -InstallDir … -ResultPath … -LogPath …` (SHA-256 OK, 0.2.9 closed gracefully, the folder swapped, the app started again with `--background`). **The picker showed at once**, before any other window, with the five languages and English suggested, next to the notification "BetterClipboard was updated to 0.2.10-dev.ask". Down ×4: the heading "Elige tu idioma"; Enter: `InterfaceLanguage` `es` and `LanguageChosen` true, 0.2.9's values kept (`CheckForUpdates`, `OpenHotkey`); the log "Interface language chosen at a start with a history already kept (…): es (Windows suggested en)", then the panel's warm-up; Win+V: the panel in Spanish with both copies made in 0.2.9. `--exit` and a plain start: no picker, Settings in Spanish. Settings › Idioma › *Usar la configuración de Windows*: saved `system` with `LanguageChosen` true, the box "🇺🇸 English · según Windows" with *Reiniciar ahora*; the restart: English, no picker. A new data folder (an isolated instance next to it): the picker; Esc kept English ("Interface language chosen at the first start: en", `LanguageChosen` true). No warning or error in the regular instance's log lines read; the isolated instance's only warning was Win+V taken (1409), which the regular instance held | claude-desktops MCP tools (`run_command`, `launch`, `press_keys`, `click`, `type_text`, screenshots) | ✅ Not run: an exit from the tray while the picker is open (the next start asks again), an update from a development build that had the earlier rule |
| The language question, unit tests (2026-11-09, §2.37): `LocalizationTests.Start_AsksUntilALanguageIsChosen` (10 cases: never chosen with `system` in any case, blank or missing asks; an explicit language, a language not shipped, and every answered case do not) and `SettingsFiles_FromBeforeTheLanguageAsk_UntilAnswered` (a 0.2.9-style `settings.json` reads as `system` and unanswered, so it asks; answered with `es` and read back, it does not; `system` answered does not). The localization guards 77 of 77; Core 1,648 of 1,648 on HEAD (`9324621`) plus only this change in a private worktree, and 1,695 of 1,695 in the shared tree with the uncommitted AI work; the solution builds with 0 errors | the test executables | ✅ |
| **Settings search and *Delete stored…*, live** (2026-11-09, §2.43; the owner: "Manaul QA but DO NOT DELETE ANYTHING ON THIS PC"). A disposable claude-desktops Windows 11 desktop ("bc-qa-stored", Tiny11 25H2, 1600×900; deleted afterwards with its persist folder), self-contained builds of the worktree (`0.2.10-dev.qa1` to `qa4`) installed with `install.ps1 -Package`, BC-TEST data only: a fake ShareX folder (`BETTERCLIPBOARD_SHAREX_DIR`, 3 screenshots) and 2 ShareX-attributed copies, Win+PrtScn shots, Notepad++ 8.9.9 (an unsaved tab, an opened file, a copy), an Everything-attributed copy, 2 Win+R runs, a PSReadLine history file, an open Command Prompt window, Claude Code's and Codex's history files; kept, pinned and hidden items made in the panel. **Search:** "sharex", "pasword" (letters in order, highlighted), "clpbrd hist", "rfe" (initials), "sqlite" (Built with's rows), "theme" (17 settings at first, 1 after the fix), "zzqx" (the empty state, centered at first, aligned with the box after the fix), "snipping" with one card (aligned after the fix), Russian "ножниц" and English "theme" in the Russian UI (through the English); Esc restored the page's scroll position; no spelling squiggles (qa3). **Dialogs:** ShareX — parts 2 / 2 / 1 with thumbnails, the red count 4 → 5 with the pinned part, gray with nothing ticked; deleting the saved screenshots kept the pinned one and the copies and the 3 PNGs; re-saving a deleted screenshot's file brought nothing back (re-saving the pinned one's bumped it); *Also turn off* switched ShareX off (logged before the deletion) and saved it; the pinned part alone, then the button gray. Snipping — `Screenshot (1).png` and `(2).png` intact; Esc cancels. Notepad++ tabs (the backup file intact) and files (Enter cancels; the file intact). Everything. Win+R (`RunMRU` identical). Pwsh — kept entry and hidden list, the history file's hash identical, the hidden command back in the tab, the pinned one kept. Cmd — kept list and entry, "0 commands kept" also after the open window's next read. Claude Code — with the switch on the archive was read again at once (5 prompts, the file's hash identical), with *Also turn off* nothing came back. Codex — *Also turn off*, the file's hash identical. Russian — the dialog, "Удалить 1 элемент", and (qa4) "Также выключить: «Ножницы» и Win+PrtScn". The app's log: 196 lines, 0 WRN/ERR | claude-desktops MCP tools (`click`, `press_keys`, `type_text`, `scroll`, region screenshots); `bclip list -f …` / `prompts`, hashes and listings of the sources in the guest; `uia_layout.ps1` (UI Automation rectangles) | ✅ after seven fixes found here (§2.43 "Found and fixed in the QA"). Not seen live: the Everything card's hidden picks, Hebrew and Chinese dialogs |
| Settings search and *Delete stored…*, unit tests (2026-11-09, §2.43): `SettingsSearchTests` 40, `StoredDataTests` 17; the whole suite in the worktree: 2,042 = 2,039 passed + 3 opt-in skipped, 0 failed (Core 1,645, Windows 397); a solution build with only the known CS0108; `catalog.py check` 0 missing and 0 to review in every language; the summarizing SQL measured with a scratch probe (20,000 encrypted BC-TEST entries: 27 unions in ~0.5 s, from 0.9 s written naively) | the test executables, `dotnet test --solution`, `probe_stored/probe.cs` (session b8a49ba2's scratchpad) | ✅ |
| **The 0.2.10 merge request: QA of every feature, and two text faults fixed** (2026-11-09, the owner: "Create a merge request (not draft) / Then install that version on my local (as dev) / Then manual qa each feature until proven stable and not misused"). Merge request #2, `release/0.2.10` from `fe1e229`, not a draft; CI passed. **On this PC:** `publish-dev.ps1 -Install` (this shell's tokens and overrides stripped; only the installed copy ran) replaced `0.2.10-dev.36.g7dce6ca` with `0.2.10-dev.38.gfe1e229`: log 0 WRN/ERR. **In a disposable claude-desktops Windows 11 desktop** ("bc-qa-0210", Tiny11 25H2, 1600×900; deleted afterwards with its persist folder), the same package through its `install.ps1 -Package`, BC-TEST data only, read through UI Automation: (1) the first-start picker's heading turned 选择语言 with the row, and Enter opened Settings in Chinese; the closed language box "🇨🇳 简体中文"; six rows (the Windows setting, English, 简体中文, עברית, Русский, Español); *Restart now* into English, Hebrew and Russian. (2) Two pins and three copies through `bclip`: Win+V selected the newest copy under the pins, Up the lowest pin, and Esc, Win+V and Enter pasted "BC-TEST copy three" into Notepad. (3) The Run tab appeared after the first Win+R (`winver`, source Win+R), the Snipping tab after the first Win+PrtScn. (4) A tab's menu: *Move left*, *Move right*, *Hide tab*, *Hide tab & clipboard rows*, *Show all hidden* (grayed while nothing is hidden); *Move right* on Pinned; *Hide tab & clipboard rows* on Run took the tab and `winver` out of All, and *Show all hidden (1)* brought both back (`HiddenTabs` and `HiddenTabRows` empty again); a left-button drag of a tab left it drawn like the others; a right-button drag opened no menu, and a click then selected the tab; the order kept over two restarts. (5) *Panel position*'s four choices: the main screen's center saved `CenterScreen` + `CenterOnMainScreen` and centered the panel in the 1600×852 work area; next to the mouse at (300, 200) gave (284,209)-(684,769). (6) Notepad++ 8.9.9 (signed O=NOTEPAD++; 8.9.1's download answered 504) installed silently: a file opened in it became a path entry, a new tab's text an entry after its minute of rest, a small edit updated that entry, replacing the text kept the old version as an entry of its own, and opening the file again moved its entry up without a second one; the Notepad++ tab between Snipping and Run. (7) A `BetterClipboard.old` whose `DWriteCore.dll` another process kept mapped (a delete refused): `install.ps1 -Package` installed over it, exit 0. (8) The dev install's update button: "Back to the release: BetterClipboard 0.2.9". **Found, text only:** the Hebrew Notepad++ tab's file caption read "++Notepad · לפני 3 דק׳ · נתיב", and Chinese Settings' update line "BetterClipboard 0.2.9 是最新发布版.". Fixed in `9c7ba87` (§2.37; 16 new cases; the whole suite 1,985 = 1,982 passed + 3 opt-in skipped). **After the fix,** `0.2.10-dev.39.g9c7ba87` in the same desktop: the Hebrew panel's three Notepad++ captions and its tab read "Notepad++", Settings' "קבצים שנפתחו ב-Notepad++" shows the signs at the word's end (pixels enlarged), and Chinese reads "是最新发布版。"; installed here with `publish-dev.ps1 -Install -Ref 9c7ba87` (16 s): log 0 WRN/ERR, Win+V by `RegisterHotKey`, the panel prepared in 523 ms. Left as is: Chinese sentences joined with a space after 。 (the catalog's own templates do it too) | claude-desktops MCP tools; a UI Automation helper in the guest; `bclip`; `publish-dev.ps1`; `bc_processes.ps1` and `verify_install.ps1` in session 2e2fae95's scratchpad | ✅ after the two text fixes |
| **Settings' language box: flags in the closed box and on "Use Windows setting"** (2026-11-09, §2.37). A disposable claude-desktops Windows 11 desktop ("bc-flags", Tiny11 25H2, en-US, 1600×900; deleted afterwards with its persist folder), a self-contained package of HEAD (`5956bda`) plus only this change, built in a worktree on C: (`0.2.10-dev.flags`, then `flags2` with the fixes). The first-start picker (a settings file saying `system` still counts as no choice), English kept: the closed box "🇺🇸 English". The open list: "🇺🇸 Use Windows setting  English" (the globe gone), then the five languages as before. Following Windows: "🇺🇸 English · Windows setting", the hint dimmed, the chevron in place. 简体中文 picked: "🇨🇳 简体中文" with *Restart now*. עברית picked and restarted: "🇮🇱 עברית"; the Hebrew list's Windows row "לפי הגדרת Windows  English". Following Windows in Hebrew, with Hebrew's *Restart now* beside it, the first build drew "English  Windows לפי הגדרת ·" (the dot at the right end) and ran past the card's edge, hiding the chevron; `flags2` drew "English · …לפי הגדרת" inside the card (the dot after the name, the trimmed Hebrew ending with its ellipsis at the left). Locally: the localization guards 116 of 116; Core 1,619; Windows 394 and `ClientHangUp_CancelsHandler` (3 of 3 alone) | claude-desktops MCP tools (`launch`, `click`, region screenshots upscaled with Pillow), UI Automation in the guest to scroll the box into view | ✅ Not run: Russian and Spanish with the hint (the guest's Windows is English, so the hint shows in English or in the language picked before a restart), the dark theme |
| **Back on dev builds a second time** (2026-11-09, the owner: "install local dev version"; §2.39's limits). The way back happened again: at 06:32:52 the update check offered 0.2.9 as the way back from `0.2.10-dev.30.g5bd6be5`, it was approved at 06:32:57, and 0.2.9 ran from 06:33:17 without dev offers. `publish-dev.ps1 -Install`, from the shared tree with this shell's tokens and overrides stripped, built `7dce6ca` in a temporary worktree (the tree's 9 uncommitted files left out, with its warning), published `0.2.10-dev.36.g7dce6ca` (zip 71.5 MB, `72ca0f04…`; the oldest build, dev.16, pruned) and installed it with the build's own `install.ps1 -Package`: 0.2.9 closed gracefully, "Win+V was already released", the app started through Explorer; 83 s in all. Afterwards: one BetterClipboard, `--background`, parent `explorer.exe`; exe `0.2.10-dev.36.g7dce6ca+7dce6ca…`; `installer.json` and the Installed-apps entry that version; Run value names unchanged (BetterClipboard, IDMan); `DisabledHotkeys` `V`; the install folder still on the user PATH; the Start menu shortcut there. Its environment: 79 names without `CLAUDECODE` and `MSYSTEM` (this shell, the control: 141 with both). Log after "starting": 0 WRN/ERR; "Local builds: 5 in the dev-builds folder, none newer than the running 0.2.10-dev.36.g7dce6ca."; the button is lit with the way back again, as the owner's rule says | `publish-dev.ps1 -Install`, session ba470c2c's `running_state.ps1` and `env_names.py` | ✅ |
| **Hide tab & clipboard rows, live** (2026-11-09, §2.42). A self-contained package (`0.2.10-dev.hiderows+8cb562e`: `8cb562e` plus the change; `c2a6aba`, which came after, changed one CLAUDE.md line) on a fresh claude-desktops Windows 11 desktop (Tiny11 25H2, 2 cores), English chosen in the first-start picker. Data: Win+R `winver` and `calc`, a plain text copy; `calc` put in a new group (card menu › Groups). Read through UI Automation after each step. (1) The Run tab's menu: *Move left*, *Move right*, *Hide tab*, *Hide tab & clipboard rows*, *Show all hidden* (grayed); the long label whole. (2) *Hide tab & clipboard rows* on Run: the tab gone; All lists the text copy and `calc` (grouped), not `winver`; footer "3 items · 1 hidden"; `HiddenTabs` and `HiddenTabRows` both `Run`; log "Tabs: Run hidden with its rows.". (3) The Text tab the same two; a search for `winver` in All: "No matches", for `calc`: `calc`. (4) `--exit` and a new start: the same tabs, rows and footer. (5) *Show all hidden (1)* from the Files tab's menu: Run and `winver` back, footer "3 items", both lists empty. (6) *Hide tab* on Run: the tab gone, all three cards stay, footer "3 items". (7) In the Run tab, *Hide tab & clipboard rows*: the panel went to All, loaded without `winver`, footer "3 items · 1 hidden", `LastTab` All. App log: 0 WRN/ERR. The desktop was deleted with its storage | claude-desktops MCP tools | ✅ |
| Unit tests (2026-11-09, §2.42): `HiddenTabRowsTests`, 6 (the setting's defaults, normalization and round trip; the rows left out of All, the kind tabs, Pinned and searches while grouped entries stay; two tabs at once; entries a filter cannot decide staying listed; the footer's count). Mutation check: without the `coalesce`, `Query_AnEntryTheFilterCannotDecide_StaysListed` fails; without the group exception, `Query_LeavesTheTabsRowsOut_ExceptGroupedOnes` and `GetStats_CountsTheHiddenRows` fail. Core 1,572 of 1,572; Windows 397 (394 passed, 2 opt-in skipped, 1 explicit not run); a `--no-incremental` solution build: 0 errors, no warning in a changed file; `catalog.py check`: 0 missing, 0 to review in every language; the documentation hook's `find_gaps` on the changed lines: 0 | the test executables in a private worktree | ✅ |
| **Review before the 0.2.10 release** (2026-11-09, the owner: "Do review before release / Then we will create merge request"). The merge request's range `origin/main..main` at `8cb562e`: 28 commits, 159 files, +27,403 / −1,990. **Build and tests of exactly that commit** in a private worktree (no uncommitted work of any session): a `--no-incremental` solution build with 0 errors and 0 documentation warnings; `dotnet test --solution` 1,963 = 1,960 passed + 3 opt-in skipped, 0 failed, twice. **Scans of the added lines:** no binary files; the largest new files are the three catalogs (ru 132 KB, he 110 KB, es 98 KB); no secret-like strings; no email address; no raw bidi control or other invisible character (the catalogs write their marks as `\uXXXX`); no new network address in `src` (only XAML/SVG namespaces and Notepad++'s site as a Settings link). One personal path, `C:\Users\ELI\…`, quoted in a CLAUDE.md row and in `9dc3a06`'s message; the message is published with the push either way. **Code read where a release user is exposed:** `install.ps1`'s leftover handling (`Remove-Leftover`, `Remove-StaleLeftovers`) on both the install and the update path (`-Update` goes through the same swap); `UpdateCatalog.FindUpdate` (a release still gets only a newer release; the dev rules apply only to `-dev` versions and to a dev-builds folder, which only `publish-dev.ps1` creates); the store (`SchemaVersion` still 1, every addition `IF NOT EXISTS` or checked before `ALTER TABLE … ADD COLUMN`, `tracked_documents.clip_id … ON DELETE SET NULL`; 0.2.9 reads the new origins 12 and 13 through a plain cast and `CliCommandProcessor`'s `_ => "copied"`, so the way back to 0.2.9 still opens the history); the startup (`--restart-after` waits only for the same process id and start time, at most 30 s; the first-start picker); `NotepadPlusPlusClient` (path length asked first and refused above the 64 KB buffer, one `PAGE_READWRITE` allocation freed in `Dispose`, the three access rights it needs, `UNICODE_STRING` offsets right on x64 and ARM64); the new `LibraryImport`s; the WinEvent hook's delegate held in a field; `package.ps1`'s default unchanged (`-SkipArchives` is opt-in); `ci.yml` runs on every pull request. README: every feature in the notes, and Notepad++'s two rows in *Privacy*. **Notes:** the draft gained Russian, Spanish and Hebrew in its one language line, which still credits Johnson-Tang2019 with the Chinese only | `git worktree` + `dotnet build` / `dotnet test`, `git diff` scans (scratch scripts in session 2e2fae95's scratchpad), reading the diffs | ✅ no blocker found. For the owner: Russian, Spanish and Hebrew are machine-translated without a native review and are chosen by itself for users whose Windows is in those languages (the default *Use Windows setting*), also when they update; the multi-monitor size fix is still unseen in the app on several monitors |
| **Russian, Spanish and Hebrew, right-to-left text, live** (2026-11-09, §2.37). A disposable claude-desktops Windows 11 desktop ("bc-rtl", Tiny11 25H2, en-US, 1600×900; deleted afterwards with its persist folder) ran a self-contained x64 package of HEAD (`2869ff4`) plus only this change, built in a private worktree (`0.2.10-dev.rtl`, then `rtl2` with the fitted placeholders), unpacked to `C:\bc` and started plainly. **Picker:** English, 简体中文, עברית, Русский, Español, each with its flag; Down ×2 put the heading and hint in Hebrew, the hint's period at the left end (where a Hebrew sentence ends; without the embedding a left-to-right paragraph puts it at the right); Enter opened Settings in Hebrew at once. **Hebrew:** Settings' cards read right to left inside the left-to-right layout ("פתיחת BetterClipboard" with the name at the left; wrapped descriptions with each line's first word at its right end and the last line's period at the far left; the stats chip "3 פריטים · 232 B · …"). The panel: the placeholder's "…" at the left; captions reading from the right ("אפליקציה לא ידועה · עכשיו · נתיב", unknown app · now · path); footer and key hints; the icon picker; the empty state with a qualifier ("… שמכיל "zzz" (מילים שלמות).", brackets mirrored: the `"{0} ({1})"` template that opts in with U+200F). UI Automation: every Hebrew label wrapped once (U+202B … U+202C), a nested pair only where a translated piece sits in a template (the group button "קבוצה כוכב, 0 פריטים"); a group made from the star with no name typed was stored as "כוכב" without marks; English-only names unmarked. **Russian:** the search box's placeholder ("Поиск по всему, что вы копировал…") and the shortcut box's ("Нажмите сочетание или введите ег") were cut; after shortening (rtl2) both fit, as does the Cmd tab's; the Cmd tab's key hint is cut at "Del скр…" next to a long status (the hint trims by design). **Spanish:** Settings from the top to Third party, and the panel, fit; the fixed PATH line read "C:\bc\bclip.exe · no está en tu PATH". Logs: "Interface language: he (WinUI's own text: he-IL, he)", 0 WRN/ERR | claude-desktops MCP tools (`launch`, `press_keys`, `click`, `scroll`, region screenshots upscaled with Pillow to read the glyph order); a UI Automation script in the guest printing each button name's bidi code points | ✅ Not run: Narrator, the dark theme, a real GPU, Arabic |
| Russian, Spanish and Hebrew, unit tests (2026-11-09, §2.37): `RightToLeftTextTests` 28 (new); `LocalizationTests` (resolution cases for he, ru and es, `es-419` and `ES-ar` included; the three languages' names and flags; every `en.json` row described; the description check on small catalogs: missing, empty, next-line and stray comments, a URL's `//`); `GroupStoreTests.Groups_NamesLoseDirectionControls`. On HEAD plus only this change, in a private worktree: Core 1,566 and Windows 394 (2 opt-in skipped, 1 explicit), 0 failed, 0 doc warnings. In the shared tree with the uncommitted AI work: Core 1,613; Windows 396 and `ClientHangUp_CancelsHandler` (3 of 3 alone); an earlier run there had `QuickSuccessiveCopies_AreAllCaptured` (1 of 3 alone at 55 % CPU), both known timing tests (§6). `catalog.py check`: 0 missing in es, he, ru and zh-Hans; planted errors on a scratch copy (U+202B in a Russian value, `"direction": "rlt"`) made it exit 1 naming both | the test executables; `catalog.py` | ✅ |
| **Notepad++, live** (2026-11-08, §2.41). A disposable claude-desktops Windows 11 desktop ("win-npp", 1600×900; deleted afterwards with its persist folder), Notepad++ 8.9.1 from its GitHub release (SHA-256 and the NOTEPAD++ signature checked), a self-contained build of the change from a private worktree (`0.2.10-dev.npp`) unpacked to `C:\bc` and started with `--background`; BC-TEST text only. (1) **First activation:** "Notepad++ tabs: 2 new, 0 updated in place, 0 kept as a new copy (first import)". (2) **Files:** a file opened in Notepad++ was recorded about a second later ("Notepad++: 1 file opened."); switching tabs, and choosing a file that was already open, recorded nothing; closing a file and opening it again bumped the same entry (use count 2). (3) **The rest rule:** after an edit, nothing at 15 s; the tab was read about 61 s after its last save. (4) **In place:** the same entry id and place, caption "… · edited …". (5) **More than a fifth lost:** a new copy, the old entry kept; the new text equaled a copy made earlier in Notepad++, so the tab's mapping moved to that entry, which took the tab's title. (6) **Rename** "new 1" → "Errands": Notepad++ renamed the backup with its stamp, the same entry was titled "Errands", and `session.xml` still said "new 1" until Notepad++ exited. (7) **Restart** with `notepad++.exe C:\bc-test\third.txt`: "started with 1 file(s) to open", `third.txt` recorded, the restored `notes.md` and `second.txt` not. (8) The panel's Notepad++ tab, its tooltip, the captions ("Notepad++ · Errands · 10 min ago · edited 7 min ago", "Notepad++ · 8 min ago · path"), both Settings cards and the Third party row. **Two bugs found here:** every copy of a known app read "Notepad++ · Unknown app · …" (an `else if` captured by the new `if`), and the files card said "6 documents open" over 5 tabs; both fixed, a second build (`npp2`) installed over it showed "Notepad++ · 14 min ago" and "Watching Notepad++ (3 files open).". The app's log: 60 lines, 0 WRN/ERR. Published as the dev build `0.2.10-dev.30.g5bd6be5` for the owner's update button; the installed `0.2.10-dev.19.g3cbefdc` logged it as offered within a second | claude-desktops MCP tools (`press_keys`, `type_text`, `launch`, `find_elements`, `element_action`, screenshots, `run_command` with `bclip list -f npp --json`) | ✅ after the two fixes. Not run: `-multiInst`, cloud settings, the portable build, a legacy code page, a Notepad++ running as administrator next to a non-elevated BetterClipboard |
| Notepad++, unit tests (2026-11-08, §2.41): Core 77 — `TrackedDocumentRuleTests` 24 (the loss against a brute-force LCS, the estimate past the budget, the length shortcut, the question page's example ending with three entries, typing from nothing never splitting, exactly a fifth not splitting, small deletions adding up, grow then delete, the peak), `TrackedDocumentStoreTests` 13 (first version and unchanged, in place keeping place, pin and groups, a new copy keeping the old version, a deleted entry's text staying gone while a change comes back, imports respecting tombstones and the last clear, a duplicate kept in place by an import and bumped live, an update onto an existing text, renames, the filter, a reopened file moving its one entry, the schema on an older store, the service's capture rules, events and the tab's witness), `NotepadPlusPlusTests` 40 (the session, the config, every backup encoding, the tracker's baseline, startup files, openings and file count, the captures, the command line's splitting). Windows 17 — `NotepadPlusPlusTabWatcherTests` 5, `NotepadPlusPlusIntegrationTests` 2, `NotepadPlusPlusClientTests` 9 (a fake Notepad++ window in the test process answering the plugin messages from a buffer, an untrusted owner refused, the file watcher's hand-over, the portable locator, the real-signature check), and `TranslationTemplateTests.NotepadPlusPlusProblems_Translate`. On `bcd1145` plus the change: Core 1,524 passed; Windows 397 (394 passed, 2 opt-in skipped, 1 explicit not run). A planted untranslated literal in the new view model failed both language guards (`EveryTextTheCodeTranslatesHasAnEntry`, `CodeAndCatalogsAgree`). The first full run failed `RunHistoryIntegrationTests.LiveRuns_AreRecorded_AndReRunsMoveUp` once (a counter read right after the list it follows; 6 of 6 alone): the test now waits for the counter (§4) | the test executables, `dotnet test --solution`, a private worktree | ✅ |
| **Arranging the tabs** (2026-11-08, §2.40). A claude-desktops Windows 11 desktop (Tiny11 25H2, 1600×900, golden 20261025-1036; deleted afterwards), a self-contained Release build of a private worktree at `effd7fa` plus the change; ten tabs (BC-TEST PowerShell and Claude Code histories in the guest's profile), two BC-TEST copies. Menus: on All every item grayed out (nothing hidden yet); on Pinned *Move left* grayed (nothing passes All); on the last shown tab (Claude, with Codex and Everything absent after it) *Move right* grayed. Pinned moved right (All Text Pinned Images…); the selected Images moved left and stayed selected, its list unchanged; Images hidden while shown, so the panel went to All; Links hidden, *Show all hidden (1)*, then *(2)*. A right-click on the row's empty space offered *Show all hidden (2)* alone. `→` ×3 from All went All, Text, Pinned, Files, skipping the hidden ones. `settings.json`: the full `TabOrder`, `HiddenTabs` [Images, Links], `LastTab` Files; after `--exit` and a restart the same row, opened in Files. A click on Pwsh, then `Shift+F10`: the tab's menu below it with the keyboard on *Move left*; Enter moved the selected Pwsh before Files. *Show all hidden* brought both back in their places (`HiddenTabs` empty). Files moved right past Cmd, beyond the strip's visible edge, and the strip scrolled to show it. In Chinese (`InterfaceLanguage` zh-Hans, edited while stopped): the arranged order kept, the menu 向左移动 / 向右移动 / 隐藏 / 显示所有隐藏的标签页（1）. The app log: 0 WRN/ERR in both runs. Locally: Core 1,334 of 1,334 (25 new; two mutations — a move past All, All not forced first — failed 7), Windows 374 (372 passed, 2 opt-in skipped), a `--no-incremental` App build with no documentation warnings, the documentation hook's check 0 gaps on every changed file | claude-desktops MCP tools (`click` with the right button, `press_keys`, region screenshots, `run_command`), the app log | ✅ Not run: touch press-and-hold, a physical mouse, the dark theme |
| **The first dev build on this PC, through `publish-dev.ps1 -Install`** (2026-11-08, the owner's "Yes, install it now"; §2.39). Only the installed 0.2.9 ran (PID 102244), so the installer closed nothing else. Run from the shared tree with this shell's tokens and overrides stripped; the script warned that the other session's 9 uncommitted files are not in the build. (1) At 83c0e5a: `0.2.10-dev.14.g83c0e5a` built and published, then its installer stopped at the held `.old` leftover (the row below) with nothing changed. (2) At 9dc3a06 (the fix): `0.2.10-dev.15.g9dc3a06` built, published and installed in 105 s: "its folder was set aside as BetterClipboard.old-stale-20261108163933", 0.2.9 closed, the folders swapped, and the app started through Explorer (`--background`, parent explorer.exe, PID 119920). Afterwards: exe `0.2.10-dev.15.g9dc3a06+9dc3a06…`; `installer.json` and the Installed-apps entry that version (`onUserPath` now true: the installer saw the PATH entry Settings had made); Run value names unchanged (BetterClipboard, IDMan); `DisabledHotkeys` still `V`, no Explorer restart; the set-aside folder stays while ShareX holds its DLL. Log: "Local builds: 2 in the dev-builds folder, none newer than the running 0.2.10-dev.15.g9dc3a06.", "… a development build (GitHub's latest release is always offered as the way back).", 0 WRN/ERR. The cached release list (checked 14:05 by 0.2.9) gives it 0.2.9 as the way back without a request; no log line said so, hence the offer in the start line from the next commit on. The commit that records this was then published as the next dev build, for the owner to install with the update button | `publish-dev.ps1`, PowerShell checks of the process, the records and the log | ✅ |
| **Back on dev builds after the way back to 0.2.9** (2026-11-08, the owner: "just install latest dev instead for me please"; §2.39's limits). The installed copy had gone 0.2.10-dev.15 → dev.18 → dev.19 → dev.30 through the button. At 17:48:57 the way back was approved, and 0.2.9 ran without dev offers. Only that 0.2.9 ran (PID 67188). `publish-dev.ps1 -Install -Ref 5bd6be5`, from the shared tree with this shell's tokens and overrides stripped: "0.2.10-dev.30.g5bd6be5 is published already … nothing to build". Then the build's own `install.ps1 -Package` checked the zip (`ca07ab80…`, as its `SHA256SUMS.txt` says), closed 0.2.9 gracefully, swapped the folders ("Win+V was already released") and started the app through Explorer; 17 s in all. Afterwards: one BetterClipboard, `--background`, whose parent (the `explorer.exe` that opened the shortcut) had exited; exe `0.2.10-dev.30.g5bd6be5+5bd6be5…`; `installer.json` and the Installed-apps entry that version; Run value names unchanged (BetterClipboard, IDMan); `DisabledHotkeys` `V`. Its environment: 79 names without `CLAUDECODE` and `MSYSTEM` (this shell, the control: 141 with both). Log after "starting": 0 WRN/ERR; "Local builds: 5 in the dev-builds folder, none newer than the running 0.2.10-dev.30.g5bd6be5."; "… a development build …; offered from what is known: BetterClipboard 0.2.9 (the way back to the latest release)" — the button is lit with the way back again, as the owner's rule says. The bootstrap's set-aside folder now holds only `DWriteCore.dll`, which ShareX (PID 21444) still has loaded | `publish-dev.ps1 -Install`, scratch `running_state.ps1`, `env_names.py` (variable names from the process's PEB, never values), `who_holds.ps1` (Restart Manager), session ba470c2c | ✅ |
| **`install.ps1` and a leftover another program holds** (2026-11-08, §3.1). The first `publish-dev.ps1 -Install` on this PC stopped at the installer's first step: "Cannot remove item C:\Users\ELI\AppData\Local\Programs\BetterClipboard.old\DWriteCore.dll: Access to the path is denied" — before the running 0.2.9 was closed or anything replaced (checked: same process, same version). The Restart Manager named the holder: ShareX (started 2026-11-06 by Explorer), whose module list showed `DWriteCore.dll` loaded from BetterClipboard's install folder, which is on the owner's user PATH (`installer.json` says `onUserPath: false`: the entry came from Settings). The fix, `Remove-Leftover` and `Remove-StaleLeftovers`, taken out of the script's syntax tree and run against a copy of `DWriteCore.dll` that a child process loaded with `LoadLibraryW` in a scratch `<install>.old`: the delete is refused as in the bug; the folder is set aside as `BetterClipboard.old-stale-<time>` with only the held DLL left in it; a later look keeps it while it is held and removes it once the child is gone; a file open without delete sharing stops the run with the new sentence, and `-BestEffort` never throws. 12 of 12 in PowerShell 7.5.8 and 12 of 12 in Windows PowerShell 5.1; `test-iex.ps1` passes in both | scratch `test_leftovers.ps1`, `who_holds.ps1` (Restart Manager), session ba470c2c | ✅ The real case: the bootstrap on this PC (its own row) |
| **Local dev builds through the update button, end to end** (2026-11-08, §2.39). A disposable claude-desktops Windows 11 desktop (Tiny11 25H2, 26200, 1600×900, golden 20261025-1036; deleted afterwards with its storage). Two builds of my private branch (the code of this change; only the log wording and the docs came after), published by the real `publish-dev.ps1`: A `0.2.10-dev.14.g3e48a10` (the feature) and B `0.2.10-dev.15.g77faa1f` (the commit after it). (1) **Bootstrap:** A's own `install.ps1 -Package <zip> -NoLaunch` from a copy on C: (SHA-256 OK against the build's `SHA256SUMS.txt`, Win+V taken over, Explorer restarted, `installer.json` 0.2.10-dev.14.g3e48a10), then started with `--background`: "Updates: … a development build (GitHub's latest release is always offered as the way back)". (2) **The way back offered:** 61 s after the start, "Update check: BetterClipboard 0.2.9 is available (running 0.2.10-dev.14…)" (wording since refined); the button lit; tooltip "Back to the release: BetterClipboard 0.2.9"; dialog "BetterClipboard 0.2.9 is the latest release", the development-build detail, *Install and restart* / *Later* / *Skip this version*, "The latest release" with 0.2.9's real notes and "View on GitHub". (3) **B published** the script's way (copied into `dev-builds\.publishing-qa1`, renamed to its version): "Local builds: 1 in the dev-builds folder; BetterClipboard 0.2.10-dev.15.g77faa1f is offered (running 0.2.10-dev.14…)" 0.5 s after the rename; tooltip "Dev build available: BetterClipboard 0.2.10-dev.15.g77faa1f"; dialog with "Published 10 min ago. It is a development build from the dev-builds folder on this PC …", *Update and restart*, "What is new" = "Since 0.2.10-dev.14.g3e48a10: wip: local-dev skill (77faa1f)", no "View on GitHub". (4) **Update A → B:** approved 14:16:46 ("copying … (74,706,941 bytes) from the dev-builds folder"), verified 14:16:48 ("matches the build's checksum list"), the app exited 14:17:13, B started 14:17:19: "The update to 0.2.10-dev.15.g77faa1f was installed.", "Local builds: 1 in the dev-builds folder, none newer than the running 0.2.10-dev.15…"; exe `0.2.10-dev.15.g77faa1f+77faa1f…`; `installer.json` and `updates\` (cache only) right; 33 s from the click. (5) **B → back to 0.2.9:** the button lit at once (cached list); *Install and restart*: 0.2.9 downloaded from GitHub and verified in 6 s, 0.2.9's own `install.ps1 -Update` swapped the folder, 0.2.9 running 30 s after the click ("The update to 0.2.9 was installed."); exe, `installer.json` and the Installed-apps entry 0.2.9. The app's log over all of it: 59 lines, 0 WRN/ERR | claude-desktops MCP tools (`run_command`, `launch`, `press_keys`, `move_mouse`, `click`, screenshots) | ✅ Not run there: `publish-dev.ps1 -Install` itself (needs the repository and the SDK in the guest; its two halves ran) |
| `publish-dev.ps1` on this PC into a scratch data folder (`BETTERCLIPBOARD_DATA_DIR`, so nothing of the owner's was read or written; 2026-11-08): three real builds, 96 s, 84 s and 63 s (x64, Release, self-contained; 71.2 MB zips); each folder held the zip, `SHA256SUMS.txt` (equal to `sha256sum`), `install.ps1`, `notes.md` and `build.json`; the first build's notes listed the 11 commits since v0.2.9, the next one's started "Since 0.2.10-dev.14.g3e48a10:"; the temporary worktree and its folder were gone afterwards; the zip held 476 entries with `install.ps1`, `BetterClipboard.exe` and `bclip.exe` at its root. The script's functions taken out of its syntax tree: 31 of 31 checks in PowerShell 7 under StrictMode 3, the script's own mode (the first version of these checks ran without it and missed a crash of the closing report, which the third publish on this PC hit after its build was in place: "The property 'Count' cannot be found" for the one-identifier pre-release `0.2.10-dev`; fixed, and the unfixed script fails the new checks) (SemVer order as the app's, versions at HEAD, at the v0.2.9 tag and at the bump commit, tag and ancestor questions, the folder's builds newest first, notes from the previous build without mojibake); Windows PowerShell 5.1 parses both changed scripts | scratch `test_publish_functions.ps1` (session ba470c2c) | ✅ The first run of those checks found `@()` around `Invoke-Git` nesting its lines (CLAUDE.md §4's trap): fixed before any publish |
| Local dev builds, unit tests (2026-11-08): Core 44 new — `LocalBuildFeedTests` 14 (complete builds newest first; 11 kinds of folder that are not a build; nothing there; notes cut and a byte order mark dropped; the copy hashed with progress; files outside the folder, a resized package and a missing one refused without a partial copy; a cancelled copy; `IsInside` with `..`), `LocalBuildCatalogTests` 21 (what a development build is; the owner's table and two more rows; release builds and release candidates; incomplete and misplaced entries; the notes of local builds and the fallback to the running build's own; the cap), `LocalBuildTextTests` 4 (a local build's words, the way back's, no reminder for development builds, Chinese), `UpdateServiceTests` 4 (offered at start and installed from the folder with no request; the folder followed, also by the dialog with automatic checks off; a changed package refused, and a copy without the folder never reads it; the way back, then a newer local build taking over), plus one expectation changed by the owner's rule (a dev build ahead of every release is offered the latest one). Windows 3 new (`LocalBuildsWatcherTests`, 3 of 3 runs green). The whole suite on the private branch: 1,683 = 1,680 passed + 3 opt-in skipped, 0 failed; a `--no-incremental` rebuild: 0 errors, 0 doc warnings, no new warning | `dotnet test --solution`, the test executables with `-class` | ✅ |
| **Where the panel opens** (2026-11-08, §2.38). A disposable claude-desktops Windows 11 Pro 25H2 desktop (26200, one monitor 1600×900 at 100 %, work area 1600×852, golden 20261025-1036; deleted afterwards), a self-contained package of a private worktree at `94ebf20` plus this change (`0.2.10-dev.placement`), installed with `install.ps1 -Package … -NoLaunch`. Settings: *Panel position* under *Shortcut*, right below the shortcut list, with the new description and four entries in order, *Next to the text cursor* selected on a fresh install; picking the mouse, the main screen and the current screen wrote `NearCursor`/false, `CenterScreen`/true and `CenterScreen`/false, and a reopened Settings showed each stored pair as its entry (the main screen, the current screen). Win+V from Notepad with its caret at Ln 7, Col 6: next to the caret, (352,251)-(752,811) as before. Next to the mouse with the pointer at (300,200): (284,209)-(684,769). Center of the main screen: (600,146)-(1000,706), the exact middle of the work area; with the groups column (Ctrl+G) it grew left to (556,146)-(1000,706) while shown, and the next summon centered it whole at (578,146)-(1022,706). Center of the current screen: centered from Notepad and after Win+D and a click on the desktop (the foreground was then `Progman`/the desktop). Restart in main-screen mode: the warm-up put the cloaked window at (600,146)-(1000,706), and the first Win+V took 62 ms with no resize. With the main screen chosen, v0.2.9 from GitHub over it: started with every setting (no "unreadable" warning, no `settings.json.corrupt-*`), centered the panel its own way, left the file as it was; after the new build again, Settings showed *Center of the main screen*. Chinese (`InterfaceLanguage` zh-Hans): 面板位置, the new description, 文本光标旁边 / 鼠标旁边 / 主屏幕中央 / 当前屏幕中央. The app's log over five starts: 77 lines, 0 WRN/ERR | claude-desktops MCP tools (`press_keys`, `click`, `move_mouse`, screenshots, `run_command`); a content-free helper reading the panel's window rectangle, its cloak, the foreground window, the mouse and the two settings (session 8cfb9b70's scratchpad, `vm/state.ps1`) | ✅ Not run in the app: several monitors (one monitor in the test desktops) |
| **Moving a WinUI window onto a monitor with another scale** (2026-11-08, §2.38), on the owner's PC: three monitors, (-1920,6)-(0,1038) at 100 %, the main one at 100 %, (1920,0)-(3840,1020) at 125 %. `tools/probes/probe_dpi_move.cs` (a cloaked, non-activating Win32 tool window): `WM_DPICHANGED` came inside `SetWindowPos`, suggesting 625×875 for an asked 500×700 on the 125 % monitor and 320×448 for 400×560 back on the main one; with the suggestion taken (`--apply`) the window ended at those sizes. `tools/probes/dpi_move_lab` (a WinUI 3 window set up like the panel, cloaked before it was shown, never activated): `AppWindow.MoveAndResize` left 625×875 and 320×448, the same 300 ms later; with `--fix` (ask again when the bounds differ) 500×700 and 400×560 exactly, both ways, also 300 ms later. The foreground window was the same before and after every run | the probe and the lab, run on the owner's real monitors while they worked; nothing showed on screen | ✅ the cause and the fix; the app's own summon across these monitors was not run (it would have shown the panel over the owner's work) |
| Panel placement, unit tests (2026-11-08, §2.38): `PanelPlacementTests` (Windows, 27: next to the caret and the mouse keep their rule; the main screen ignores what was captured; eight cases of the current screen on the owner's layout — the caret in the window, no caret, a caret parked outside, the desktop, a caret on the desktop, a minimized window, a window that closed, the tray; an unknown placement; centering with the column on the 125 % monitor, on the monitor with negative coordinates, shrunk to a small work area; the caret placement unchanged; the desktop's class names; the real main monitor and every real monitor's lookup; real windows: none, a dead handle, hidden, shown cloaked, minimized, and the desktop's window) and `PanelPositionSettingsTests` (Core, 8: defaults, a file from before, every stored pair round-trips under a name released versions know, the enum keeps its three names, an unknown placement falls back). Three mutations each failed a test: trusting any caret, counting the desktop as a screen, centering without the column. The whole suite at `94ebf20` plus this change: 1,636 = 1,633 passed + 3 opt-in skipped, 0 failed | the test executables, `dotnet test --solution`, a private worktree | ✅ |
| **The translation gaps closed, in Chinese on an English Windows** (2026-11-08, §2.37, branch `feat/localization` after the merge of `3f0ff87`). A disposable claude-desktops Windows 11 desktop (Tiny11 25H2, en-US display and formats, 1600×900; deleted with its storage afterwards), a self-contained package of the worktree (`0.2.10-dev.gaps`, then `gaps2` with the last fixes), installed with `install.ps1 -Package … -NoLaunch -NoUpdateCheck` and the command line switched on in `settings.json`; BC-TEST items through `bclip`. The first start's picker → 简体中文 → Settings in Chinese: the chip 正在采集, the Win+V row 已注册。, the Explorer card's new whole sentence (Win+V 已从资源管理器释放：资源管理器不再注册它，因此 BetterClipboard 可以注册。), the capture line (… — 读取前没有任何内容被覆盖。), Third party (· 独立应用, 公共领域, … 许可条款, the uses), the shortcut box's help text. A UI Automation dump of Settings had no English sentence left (the Ignored apps list is user data). `ctrl+v` + 添加 → "Ctrl+V 是所有应用的粘贴键，BetterClipboard 自身也用 Ctrl+V 粘贴。请选择其他快捷键，例如 Ctrl+Shift+V。". The panel: placeholder 搜索复制过的全部内容…, footer ↵ 粘贴 · ⇧↵ 纯文本 · Ctrl+P 固定 · Del 删除, the path card bclip · 刚刚 · 路径; zqx with W and Aa → 历史中没有符合条件的内容：包含“zqx”（全字匹配、区分大小写）。; `(` with .* → 正则表达式无效 (the reason "Not enough )'s." is .NET's); **a right-click in the search box showed WinUI's own menu in Chinese (粘贴 / 取消操作 / 全选)**; the icon picker 新建分组, the star → a group named 星标 with the help text Ctrl+单击或 Shift+单击可同时显示多个分组; the card menu's 分组. The forgotten list: 文本 · 24 个字符 / 来自 bclip · 于 刚刚 忽略 · 已拦截 1 次，上次为 刚刚 (before the term change to 忘记). The guest's clock moved +3 days → bclip · 周日 16:05 · 路径, +40 days → 11月8日 (then put back). Found there and fixed: the language line logged twice (a doubled call from a re-run edit script), Everything's 未安装（未安装）。; then `gaps2`: one log line "Interface language: zh-Hans (WinUI's own text: zh-CN, zh-Hans).", 未安装。, cards named 路径：… and 文本：…, the group kept across the restart, 0 WRN/ERR | claude-desktops MCP tools (`launch` through Explorer, `click`, `press_keys`, `type_text`, screenshots, `run_command`); `uia_dump.ps1` (session c18a1e65's scratchpad, `vm/`) | ✅ Not run: the forget term 忘记, the "60+" sentence and the catalog tool's scan fix (made after the desktop pass; covered by the guards and the catalog check), the dark theme, Narrator |
| Translation guards and the gap work's unit tests (2026-11-08): `TranslationCoverageTests` (Core, 14) and `TranslationTemplateTests` (Windows, 6). Mutation check: a probe file with an untranslated literal, an untranslated constant and a template, plus a renamed literal in `ClipboardFlyout.xaml.cs`, made both source guards fail with all four named; files restored byte for byte. The whole suite after the merge of `3f0ff87`: 1,697 = 1,694 passed + 3 opt-in skipped, 0 failed (Core 1,320, Windows 377); a `--no-incremental` build with no new warning (the cref slip it showed was fixed). `catalog.py check` ok; the docs hook's checker on every file the branch changes: 0 gaps (27 filled) | the test executables, `dotnet test --solution`, session c18a1e65's `mutation.py`, `run_doc_check.py` | ✅ |
| **Interface languages, first-start picker, English-only logs and bclip** (2026-11-08, §2.37, branch `feat/localization` on `94ebf20`). A disposable claude-desktops Windows 11 desktop (Tiny11 25H2, 26200, en-US, 1600×900, no GPU; deleted afterwards) ran a self-contained x64 package of the branch (`0.2.10-dev.loc`). **First start, plain launch:** the picker before any other window: "Choose your language", *English* (US flag) selected, *简体中文* (China flag) with "Chinese (Simplified)", the round green check; `↓` switched the heading and hint to "选择语言" / "以后可以在设置中更改。"; `Enter` saved `zh-Hans` and Settings opened in Chinese at once (no restart); *设置 › 应用 › 语言* listed 跟随 Windows (globe, "English"), English and 简体中文 with their flags; Win+V opened the panel in Chinese (剪贴板, 全部 / 已固定 / 文本 / 图片 / 链接 / 文件 / 截图, "未知应用 · 刚刚", "1 项"). **Logs** English throughout ("Interface language chosen at the first start: zh-Hans (Windows suggested en)", the update and import lines); the panel's warm-up ran after the choice. **Second start:** no picker. **`bclip`** with the app in Chinese: `list` printed "just now", `status` and an app-side error ("History has fewer than 9 items.") English. **A data folder with a `settings.json` but no history** (as `install.ps1 -Hotkey` leaves it): the picker showed; a click on the check saved `en` and kept the pre-written `Alt+Win+V`; Settings in English. **A `--background` first start in the dark theme:** the picker showed (after the height fix: no empty band under the list); `Esc` kept English. 0 WRN/ERR in all three logs. Gaps seen, deferred by the owner: the search box's placeholder, the footer's key hints and the "Capturing" chip stay English | `create_desktop`, `desktop_call` (`launch`, `press_keys`, `click`, `find_elements`, `screenshot`, `run_command`), `record` (297 s), `tools/release/package.ps1` | ✅ |
| Interface languages, unit tests (2026-11-08): `LocalizationTests` 54 cases (13 from PR #1, adapted to the catalogs' `$language` header). A planted `AppLog.Warn($"…{UiText.Translate(…)}…")` in a scratch file made `NoLogLineIsBuiltFromTranslatedText` fail; removed. `catalog.py` on a scratch copy: `rename` moved a string's key in both catalogs and both XAML references, `check` passed, an unknown text exited 2; its layout reproduced the PR's catalogs byte for byte before the first write. The whole suite on `94ebf20` + the branch: 1,642 tests, 1,639 passed, 3 skipped, 0 failed; a `--no-incremental` rebuild with 0 errors and no doc warnings | the test executables, `dotnet test --solution` | ✅ |
| **The localization branches joined and landed on `main`** (2026-11-08, the owner: "Pull main and merge to main your changes"). `bde5c83` (the gap work) merged into `feat/localization-choices` (`2781ec9`): Core 1,377 and Windows 377, 0 failed. Then a branch from `main` (`3cbefdc`) took GitHub's `a347f06` (an empty change) and that branch: the catalogs re-keyed and `catalog.py check` ok (zh-Hans 0 missing, 0 to review), no `{loc:Tr Key=…}` left in the XAML, Core 1,446 and Windows 380 (2 skipped, 1 explicit), 0 failed; `CSharpSourceScanner`'s comment stripping fixed (it lost the rest of a file after a hole holding a string, which `main`'s new code has). Landed on `main` with the shared tree's uncommitted AI work kept | the test executables, `catalog.py`, the merge scripts in session 7f64d66b's scratchpad | ✅ Not run in the app after the merges (the branch's own live check ran before them) |
| **Interface languages, the second round** (2026-11-08, §2.37, branch `feat/localization-choices` on `be6d30d`). Unit tests: Core 1,363 (57 new: `PluralRulesTests` 50 — categories of whole counts in English, Russian, Hebrew, Spanish and Arabic, every CLDR `@integer` sample in its category, all operators, extreme counts, 12 broken rules refused; `LocalizationTests` 7 new — English-text keys, hash keys, plural forms and fallbacks, coverage and the offer rule, the code and the catalogs agreeing), Windows 371 (2 skipped, 1 explicit), 0 failed. `CodeAndCatalogsAgree` mutation-checked: a planted file with an unregistered `Translate`, `Plural` and `Format` literal failed all three (the interpolated `$"Hello {x:X8} and {{braces}}"` read as `Hello {0:X8} and {{braces}}`). `catalog.py` on the real catalogs: `migrate` 563 keys and 188 XAML uses (again: nothing to convert), `rename` marked the Chinese for review, `reviewed` cleared it, `check` ok. Live on a disposable claude-desktops Windows 11 desktop (`0.2.10-dev.loc2`, deleted with its persist folder): the first-start picker; Settings in English with every one of the 19 texts with an apostrophe right (`Text=&quot;…&quot;`); 简体中文 chosen → *Restart now* left of the list; the language on screen chosen again → no button; *Restart now* → "Restarting (Settings' Restart now).", the new process 2.3 s later ("restarted from Settings"), Settings in Chinese; one copy → "1 项 · 34 B · 已固定 0 项 · 已分组 0 项"; *Use Windows setting* → *立即重启* → English in 0.7 s, "1 item · 34 B · 0 pinned · 0 in groups", the panel's footer "1 item", "0 commands kept (newest 1,000)."; the log 0 WRN/ERR | test executables, `catalog.py`, claude-desktops MCP tools, UI Automation scripts in the guest | ✅ Not run: a catalog below the offer threshold in a real build (unit-tested), the restart from an elevated or an isolated instance |
| **PR #1 merged into the local `main`** (2026-11-08, `8c5814a`, not pushed). Before (2026-11-07), the PR head `29032d9` in a scratch worktree: 0 build errors, 1,577 tests (1,574 passed, 3 skipped, 0 failed); no network calls, no hidden Unicode, the workflow read-only. The merge with `0447913` (README icon) had no conflicts. The uncommitted AI work in the shared tree was carried forward (5 files backed up, set to `HEAD`, three-way merged back; 1 conflict resolved in `ClipboardFlyout.xaml`); `git status` listed the same files before and after, and main + PR + that work built in a scratch copy with 0 errors, 1,624 tests, 0 failed | `git merge-file`, a scratch worktree with the working files copied in | ✅ |
| **The newest copy is selected under the pins** (2026-11-08, §2.28). A disposable claude-desktops Windows 11 Pro 25H2 desktop (26200, 1600×900 at 100 %, golden 20261025-1036), self-contained packages of a private worktree at `1ef565b` plus this change (`0.2.10-dev.armed` to `armed5`), installed with `install.ps1 -Package … -NoLaunch -NoUpdateCheck`; BC-TEST items through `bclip put` / `pin`; the list read through UI Automation (selected row, rows in view, scroll percent), Notepad as the paste target. **3 pins + 3 copies:** Win+V selects "copy three" (row 3) with the list at its top; Up → "pin alpha", the lowest pin; Esc, Win+V → row 3 again; Enter pastes "BC-TEST copy three" into Notepad (read back through UI Automation). **Searches:** "o" (one pin and three copies match) → the first copy below the pin; "pin" (pins only) → the top pin; Esc → back to row 3. **Tabs:** Pinned → its top pin; Text → row 3. **Ctrl+P on the armed copy:** it moves to the top of the pins and stays selected; first build: the list ended 32 px down with that card cut off (the layout pass kept the visible rows in place after the move and answered `ChangeView(0)` with offset 32, seen with temporary logging) — fixed by setting the offset again after `UpdateLayout`: offset 0. **9 pins:** Win+V → "copy three" (row 9) on the list's lower edge, offset 298 = row bottom 674 − viewport 376, the lowest pins above it; Up → "pin alpha". **60 more copies, list wheeled to offset 4520, Esc, Win+V:** back to offset 298 with "bulk 60" selected. **A pin pasted last** (Up, Enter on "pin alpha": now the most recently used item) → pins first: "bulk 60" still selected; "Show pinned items first" off (settings file, restart) → recency order, row 0 ("pin alpha") selected. **Setting flipped in Settings without a restart:** first build: back on → the hidden list still in recency order and row 1 selected, under a pin of the old order (the order was not part of `IsListCurrent`, at HEAD too) — fixed (`ViewKey.PinnedFirst`, and the rule checks that the pins lead the list): off → Win+V reloads in recency order and selects row 0; on → pins first, "bulk 60" under the pins. **Ctrl+P with 9 pins:** the re-selected card ("bulk 60", now a pin at row 1) was out of view after the reload's scroll — fixed (`OnHistoryChanged` scrolls it into view): row 1 at the top edge, selected. Final build: reopen → "bulk 59" under 10 pins; Up → "pin bravo". The Settings card reads "Pinned items are listed before everything else. The panel still selects your newest copy, just below them." The app log of every run: 0 WRN/ERR. Locally: Core 1,236 (16 new: `ArmedRow` cases, Up from the armed row, rows asked, nulls; a store-backed page with a pasted pin, a search, recency order, the Pinned tab), the full suite 1,580 = 1,577 passed + 3 opt-in skipped, 0 failed; a `--no-incremental` solution build with no new warnings (no doc warnings). Rebased onto `a03c1d3` before the commit (the Simplified Chinese UI `8c5814a`, the tabs that wait for content): conflicts only in CLAUDE.md and the Settings description, which moved into the catalogs (key `T7ffed93d48ee`); the rebased tree: 1,601 = 1,598 passed + 3 opt-in skipped, 0 failed (Core 1,257, the catalogs' completeness test included), 0 errors and no new warnings; not run live after the rebase (the Chinese sentence was not seen on screen) | claude-desktops MCP tools (`press_keys`, `click`, `scroll`, `find_elements`, screenshots); guest scripts `C:\qa\state.ps1`, `seed.ps1`, `toggle-pins.ps1` (session b7fa198f, not kept: the desktop was deleted with its persist folder) | ✅ after the three fixes it found. Not run: a real GPU, touch and pen, the groups column walk from row 0 (unchanged code, unit-tested), the first summon of a session with more pins than the top shows (whether one frame shows the top first) |
| **The Run and Snipping tabs wait for content, live** (2026-11-08, §2.36). A self-contained package of the change (`0.2.10-dev.tabs+1ef565b`: `1ef565b` plus the patch, from a private worktree, before the merge of the Simplified Chinese UI, `8c5814a`, which changed no tab logic) on a fresh claude-desktops Windows 11 desktop (Tiny11 25H2, 26200, 1600×900), whose Win+R list and Screenshots folder did not exist; started with `--background`. The tabs were read through UI Automation after each step (`find_elements`, and a PowerShell UIA walk that also lists tabs scrolled out of the strip). (1) Fresh: All, Pinned, Text, Images, Links, Files, Cmd — no Run, no Snipping. (2) Win+R `winver`: "Win+R: 1 new run found (watch), 1 recorded."; the next Win+V had a Run tab between Files and Cmd, with "Win+R command: winver". (3) Win+PrtScn: Windows created the folder with `Screenshot (1).png`; the next Win+V made the app look again ("Watching the Screenshots folder again", "Imported 1 screenshot saved while BetterClipboard was not watching"), and the Snipping tab appeared in the open panel, its card "Win+PrtScn · just now · 1600 × 900". (4) ← ← to the Run tab (the tabs wrap around), Del on its only card: the panel fell back to All, and the strip read All, Pinned, Text, Images, Links, Files, Snipping, Cmd. (5) `--exit` and a new start: `LastTab` was saved as Run; the panel opened in All, with Snipping (looked up at the start) and without Run. (6) Win+R `calc` while the panel was hidden: the next Win+V opened in the Run tab (back as the tab last chosen) with "Win+R command: calc" alone (the deleted `winver` did not come back). The app's log over both runs: 0 WRN/ERR. The desktop was deleted with its storage | claude-desktops MCP tools | ✅ |
| Unit tests (2026-11-08, §2.36): `TabContentTests`, 8 (the store's lookups for Run and for Snipping pictures, and of one entry; the service through a first and a last run, a first import, Snipping Tool's pictures and text, a picture copied again from Paint, a screenshot file copied again from Paint, a clear, a start over a store that has both, a prune and Forget forever). Mutation check: without the import's lookup, `Service_RunTab_FilledByTheFirstImport` fails; without the "witness left the tab" branch, `Service_SnippingTab_CountsScreenshotsAndSnippingToolPictures` fails. On `8c5814a` plus the patch: Core 1,241 of 1,241 (13 of them the Simplified Chinese UI's); Windows 344 (342 passed, 2 opt-in skipped, 1 explicit not run); a `--no-incremental` solution build: 0 errors, no warning in a changed file. Query plans and timings: a scratch probe (session a6fd5958's scratchpad, `probe_tabs/`) | the test executables in a private worktree | ✅ |
| **v0.2.9 released** (2026-10-26, "Do we have anything to release?", then "Release new version"). Bump `79d5d59` (0.2.8 → 0.2.9; the notes draft is in its message) on `1de68ea` (Start with Windows for every user, §2.34) and `b5ac0e0` (the `irm \| iex` fix, §3.1); the rest since v0.2.8 is docs and research. The push range `baca929..79d5d59` (8 commits, 42 files) was scanned first: no secret-like added lines, no personal email address, three PNGs with BC-TEST content only (looked at). `main` pushed at 05:26Z; raw.githubusercontent served the fixed `install.ps1` at once (same hash as the blob). **CI passed only after three test-only commits:** run 40274246627 on `79d5d59` failed attempt 1 (`Codex_HistoryLockedByAWriter_IsReadOnceUnlocked`), attempt 2 (`WindowsScreenshotsIntegrationTests.FirstActivation_NoBackfill_ThenLive`) and attempt 3 (tests passed; the new step "Installer for every user" failed 8 of 36: its admin install ran Windows PowerShell 5.1 with PowerShell 7's `PSModulePath`, "Get-FileHash is not recognized", fixed in `676ec40`); run 40275737053 on `676ec40` failed on the lock test again; `a4aa62b` made the lock test and both counter tests wait on conditions; run 40277011148 failed on the third listed flake (`Watch_NeverLocksTheWriterOut_AndWaitsForTheEnd`), fixed in `4da86bd` (§4, §6). Run 40277654488 on `4da86bd` passed at the first attempt: 1,564 tests (1,560 passed, 4 skipped), the update mode, `test-iex.ps1` in 7.6.7 and 5.1, Chocolatey 32 of 32 twice, and **`test-all-users.ps1` 36 of 36, its first pass on GitHub**. Tag `v0.2.9`: annotated, on `4da86bd`, message identical to `79d5d59`'s draft. Release run 40278303399 passed at the first attempt (5.5 min); published 06:16:14Z, Latest. So for 50 minutes `irm \| iex` served the new installer with the 0.2.8 app. Assets: zips x64 74,534,777 / ARM64 72,030,825 bytes, `.7z` 45,315,407 / 41,152,840, `betterclipboard.0.2.9.nupkg` 86,514,705 (attached only: no `CHOCOLATEY_API_KEY`, the step warned), `install.ps1`, `SHA256SUMS.txt`. All four sums equal GitHub's digests; the body is the notes plus the code-signing footer, no mojibake; the `install.ps1` asset and the one in the x64 zip equal the blob except CRLF; the x64 zip's `BetterClipboard.exe` and `bclip.exe` report `0.2.9+4da86bd…`. Not installed on this PC (not asked): it runs `0.2.8+03f5058` with the automatic check on, so its update button offers 0.2.9 | `gh run view --log`, `gh api …/releases/tags/v0.2.9`, `gh release download`, scratch scripts in session 1c552075's scratchpad (`release-0.2.9/`) | ✅ |
| **`install.ps1 -AllUsers`, for real** (2026-10-18, §2.34). A package of the working tree (`0.2.9-allusers`, x64) on a disposable claude-desktops VM (Windows 11 Pro 25H2, 26200, admin "Docker", UAC off, Windows PowerShell 5.1). `tools/release/test-all-users.ps1 -ConfirmMachineChanges`. The first run: 29 of 36; every run as SYSTEM stopped at its first line, because SYSTEM has no Start menu folder and `Join-Path ''` throws, a fault since v0.1.0. The `-Hotkey` refusal passed only by that accident, so the check now reads the reason. After the fix: **36 of 36**. As the administrator with `-AllUsers -AddToPath`: Program Files with the package's version; protected rights with no ordinary writer; the PC's Run value with `--all-users-startup` and none of the user's own; the PC's Installed-apps entry with `-Uninstall -AllUsers`; the shortcut for every user and none of the user's own; the PC's PATH and not the user's; Win+V taken over for that user; every other startup entry kept. Then all of it removed by `-Uninstall -AllUsers`, with Win+V given back. As SYSTEM: `-Hotkey` refused for the right reason with nothing installed; without `-AllUsers`, the install for every user by itself, nothing in SYSTEM's own registry, the user's `DisabledHotkeys` untouched, Explorer not restarted, the log naming the reason; then `-Uninstall` as SYSTEM removed everything | the test script (new, in CI) with the fixed installer put into the test zip | ✅ |
| **Start with Windows for every user at sign-in** (the same VM, an install as SYSTEM, four reboots). 1st sign-in: BetterClipboard ran in the user's session with `--background --all-users-startup`, no window; Settings showed the switch on, with "It is set up to start for every user of this PC; turning this off changes that only for you.", the update switch disabled, and "This copy is installed for every user of this PC and an administrator updates it, so BetterClipboard does not check by itself. Check now still asks GitHub." Switch off: `SkipAllUsersStartup: true`, the PC's value kept, no user value written. 2nd sign-in: nothing started; the log said "Not started by the sign-in entry for every user: this user turned Start with Windows off in Settings." Started by hand, the switch read off; on: the setting false, still no user value. A second `--background` start while it ran: ended with exit code 0, no window. A user value for the same program: 3rd sign-in, exactly one process, the user's (`--background` without the marker); the log has the step-aside line at 18:02:10 and the user's start at 18:02:11. The PC's Installed-apps command run as Settings › Apps runs it: graceful close, the PC's value, the user's leftover value, folder, entry and shortcut removed, history kept. Chocolatey as SYSTEM: the entry for every user by itself; 4th sign-in, the package's copy ran from it | UI Automation in the guest (`TogglePattern` on the switch), `Win32_Process` command lines, the app's log, `settings.json`, the registry | ✅ |
| **Chocolatey with the entry for every user** (the same VM, Chocolatey 2.7.4, the package of `0.2.9-allusers`). `test-chocolatey.ps1` (now with a neighbor in the PC's Run key): **32 of 32** as the desktop user (their own entry, none for every user) and **32 of 32** with `-AllUsersStartup` (the entry for every user, none of the installer's own, kept through the upgrade, removed by the uninstall, every other entry kept). `choco install` as SYSTEM: "BetterClipboard starts when any user of this PC signs in; each of them can turn that off in its Settings." and "It starts at the next sign-in, or from the Start menu."; `choco uninstall` as SYSTEM: the user's copy stopped, the entry and the package folder gone. The VM was deleted afterwards | scheduled tasks as SYSTEM, the registry | ✅ |
| All-users helpers of `install.ps1` on scratch keys (2026-10-18): the PC's Run value written in the app's format, read back and removed with a neighbor kept; the PC's Installed-apps entry found only with its program; scope: a plain run is this account's, `-AllUsers` is for every user, `-Update` never picks it, an all-users copy is updated only by an elevated run; the PATH of every user with `%VAR%` entries kept raw and `REG_EXPAND_SZ`, added once, removed in any spelling, each change announced; the folder's rights (protected, exactly four grants, a non-elevated write refused); shortcut targets; other copies; the 64-bit Program Files. Also: `test-update.ps1` 17 of 17 with the new installer, `test-iex.ps1` in both shells, and both scripts parsed by 5.1 and 7 | a harness that loads the functions from the script's syntax tree, with `$MachineHive`/`$MachineKeyPrefix` pointing every machine key under `HKCU\Software\BetterClipboard-Tests` (session 91d8d3d1's scratchpad, `allusers-harness.ps1`) | ✅ 35 of 35 in PowerShell 5.1 and in 7. Its first run found that `Translate()` cannot map "ALL APPLICATION PACKAGES" back to a SID, which `test-all-users.ps1` also did: both read the SIDs directly now |
| Unit tests (2026-10-18): Core 1,220 of 1,220 (31 new: `SignInStartupTests`, a copy for every user in `InstalledCopyTests` and `UpdateTextTests`); Windows 344: 340 passed, 3 opt-in or explicit skipped, and 1 failed: `QuickSuccessiveCopies_AreAllCaptured` (24 of 25 copies captured, 1 superseded), also alone, the same as recorded on 2026-10-10 on unchanged code; this change does not touch the clipboard code (7 new: `StartupRegistrationTests`, scratch keys) | `dotnet test --solution`, the test executables, `--no-incremental` build: 0 doc warnings | ✅ apart from that known timing test |
| **Rich previews researched** (2026-10-16, §2.33, `docs/rich-previews.md`). In a claude-desktops Windows 11 Pro 25H2 desktop (26200, 4 cores, no GPU) with LibreOffice 26.2.5.2, Edge 155 and BetterClipboard 0.2.8 (installed from GitHub with the `[scriptblock]::Create` form), `probe_rich_formats.cs --watch --save` described four copies: Writer (14 formats; RTF 103 KB with 3 `\pngblip`; CF_HTML 1.0 with `file:` temp pictures; `Markdown`), Calc (24 formats incl. PNG 222×68, DIB, an EMF+ dual EMF, SVG), Edge (7 formats; CF_HTML 0.9, 40 KB, inline computed styles) and a file inside a .zip (`FileGroupDescriptorW` only). `bclip get --format formats`: Writer RTF + HTML + text, Calc PNG + DIB + HTML + text + RTF, Edge HTML + text; the .zip copy was not recorded. The panel: three "formatted" plain-text cards, Writer's in the code font. `preview_lab`: Writer's RTF in WinUI's `RichEditBox` with all 3 pictures (855 ms the first time, 11 ms the second; a 2 MB RTF with 40 pictures 92 / 45 ms); Edge's HTML in WebView2 with scripts off and every request blocked (start-up 18.8 / 6.2 / 3.4 s, first page 3.8 / 1.6 / 2.3 s, next 0.25–0.34 s; 5 processes, ~257 MB; 4 requests blocked and 0 at `serve.ps1`; the control run with `--network` let 4 through). On this PC: `--render-rtf` through the classic RichEdit window (pictures only with an OLE callback; `\pngblip` dropped, `\dibitmap` drawn) and `--census` on the saved payloads | desktop `run_command`, `launch`, `press_keys`, screenshots and browser tools; `bclip`; the probe's `--census` and `--render-rtf` on this PC | ✅ The desktop was deleted with its persist folder. Not run: Word, Teams, Outlook, Excel (the live check, §2.33), Firefox, the WinUI `RichEditBox` with Word's own RTF, nested tables, a real GPU |
| **The README's `irm \| iex` install, fixed** (2026-10-16, §3.1). Before: in a claude-desktops Windows 11 desktop, `irm https://raw.githubusercontent.com/Nucs/BetterClipboard/main/install.ps1 \| iex` (Windows PowerShell 5.1) printed "The attribute cannot be added because variable PackageSha256 with value would no longer be valid" and installed nothing; a harmless copy of the param block reproduced it on this PC in 5.1.26100.8875 and 7.5.8 (and showed the `[scriptblock]::Create` form passing). After: the fixed `install.ps1`, piped into `Invoke-Expression` by a hidden PowerShell in that desktop, installed 0.2.8 over the running copy in 52 s ("Win+V was already released", the app started again); `-Package … -PackageSha256 nothex` was refused with "-PackageSha256 must be a SHA-256 checksum: 64 hexadecimal digits." before anything changed. `tools/release/test-iex.ps1`: FAIL on `HEAD`'s `install.ps1` and PASS on the fixed one, from pwsh 7.5.8 and from Windows PowerShell 5.1 (each checks both shells) On this PC, `-File install.ps1 -Update -Package <dummy> -PackageSha256 nothex -ResultPath …` (scratch folders, `BETTERCLIPBOARD_DATA_DIR` set): exit 1 and a result file with `ok: false` and that message; before, the binder refused it with no result file | desktop `run_command`; local `test-iex.ps1` against `git show HEAD:install.ps1` and the working tree | ✅ Not pushed: the README serves `main`'s `install.ps1`, so users get the fix only with a push. Not run: the full suite (no C# changed), `test-update.ps1` (it needs a package; the update mode's parameters are unchanged) |
| **Ditto 3.25.113 learned and run** (2026-10-16, §2.32, `docs/ditto.md`). Installed silently in a claude-desktops Windows 11 Pro 25H2 desktop (26200.8037), plus the portable zip; BC-TEST copies only, through plain Win32 calls. Seen: the files and registry values of a first run; `TCP 0.0.0.0:23443` LISTENING and the Windows firewall prompt (installed copy only); Ctrl+\` taken (1409); a plain `Ditto.db` (UTF-16le, auto_vacuum full, journal delete, 13 indexes, 2 triggers) read correctly by `probe_ditto.py`; markers: only `Clipboard Viewer Ignore` honored, `ExcludeClipboardContentFromMonitorProcessing` and `CanIncludeInClipboardHistory = 0` copies stored; bursts of 10: 1 kept at 5/50 ms, 4 at 200 ms, 5 at 400/450 ms, 10 at ≥ 550 ms (two runs, with and without Ditto's log); a copy 343 ms after another never stored; the duplicate rule; a paste's clipboard formats; reading the database with `FileShare.ReadWrite` while Ditto runs. With BetterClipboard 0.2.8 next to it: 10 of 10 kept by BetterClipboard at 5 and 200 ms; Ditto's paste skipped by BetterClipboard ("Skipped a copy marked private by 'Ditto'"); BetterClipboard's paste stored by Ditto; both hotkeys worked; at rest Ditto 4.9 MB private / 47–109 ms CPU a minute, BetterClipboard 106.6 MB / 297–844 ms | `desktop_call` (`run_command`, `launch`, `press_keys`, `find_elements`, screenshots); session 46390d28's `ditto/vm/` scripts; `probe_ditto.py --db` and `--self-test` on this PC | ✅ The desktop was deleted with its persist folder. Not run: Ditto's elevated paste (UAC is off in those desktops), the friends network, a `.dto` export, Ditto's `master` build |
| **v0.2.8 released** (2026-10-10, "We did some changes, make sure they are included in new release. release the new version."). The changes were `c0db2d1` and `03f5058` (the groups column's keys), made by another session after the bump `fe3367b`. Both are in the tag: v0.2.8 is on `ccde6fb`, the head of `main`, and only CLAUDE.md changed after `03f5058`. The push range `350d949..ccde6fb` (11 commits) was scanned first: 0 binary files, 0 secret-like lines in the diffs or the messages, no email address. `main` pushed; CI run 38070424994 green in ~3.5 min (1,526 = 1,522 passed + 4 skipped, the update mode, the Chocolatey smoke test). Tag `v0.2.8`: annotated, `--cleanup=whitespace`, message = the newest notes draft (`03f5058`'s, found with the anchored lookup), identical to the notes file (3,940 characters). Release run 38070699975 green at the first attempt (~5.5 min; 1,526 = 1,522 + 4 skipped); published 17:16:38Z, Latest. Assets: the zips (x64 71.1 MB `e7f8d58b…`, ARM64 68.7 MB `bcb3aeff…`), the two `.7z`, `betterclipboard.0.2.8.nupkg` (82.4 MB, attached only: no `CHOCOLATEY_API_KEY` secret), `install.ps1`, `SHA256SUMS.txt`. All four sums equal GitHub's digests; the body is the notes plus the code-signing footer, with no mojibake; the `install.ps1` asset equals the blob except for CRLF. Not reinstalled: the user's PC runs `0.2.8+03f5058` (the row below), which has the released app code. Since this push, `irm \| iex` from `main` has `b14a76c`'s `Stop-RunningApp` | `gh api …/releases/tags/v0.2.8`, `gh release download`, `gh run view --log`, scratch `verify_release.py` | ✅ |
| **0.2.8 installed on this PC a third time, from `03f5058`** (2026-10-10, the user's answer "Yes, reinstall" to the question asked before the change of the row below was written; §3.1's "move X.Y.Z to the latest changes" path). In a worktree at `03f5058`: the full suite 1,526 = 1,523 passed + 3 skipped, 0 failed; `package.ps1 -Version 0.2.8 -Architectures x64` (zip 71.2 MB, `.7z` 43.5 MB); `test-update.ps1 -Version 0.2.8` 17 of 17. `install-local.ps1` with that worktree's `install.ps1` (Windows PowerShell 5.1, env stripped, `-NoLaunch`, 17 s): SHA-256 OK (`79551c9c…`), the running `0.2.8+c0db2d1` closed gracefully ("Exiting."), "Win+V was already released from Explorer", the leftover process object (PID 97064, 0 threads) skipped. Started through Explorer with a temporary shortcut (`--background`): PID 69996, parent `explorer.exe`, 76 environment variables without `CLAUDECODE` and `MSYSTEM`. Afterwards: the exe 0.2.8.0 / `0.2.8+03f5058…`; Installed apps and `installer.json` 0.2.8; unchanged: the Run key's value names (BetterClipboard, IDMan) and command line, the Start menu shortcut, `DisabledHotkeys` (`V`), Explorer's 42 Advanced values, the user PATH (hash), the update settings, and the five Explorer processes (no restart). Log after "BetterClipboard 0.2.8 starting": 0 WRN/ERR; store opened (encrypted); "Win+V registered with RegisterHotKey"; automatic update check on; the panel prepared in 559 ms | as for the row "The corrected 0.2.8 installed on this PC" below | ✅ The PC runs the candidate with every change of §2.31. The keys were not driven on the user's screen: their live check is the row below. **Tag v0.2.8 on `03f5058` or a later commit.** Nothing is pushed or tagged |
| **Groups column: Space, Enter and Down on the last group go back to the list** (2026-10-10, §2.31: "If not multi-select and user is on the last group existing and he presses down the move. If no multi-select and user uses enter/space then select that group and go back to focusing on the tabs row. multiselect remains same"; the user chose "Leave the groups" for the unfinished first sentence). On a third claude-desktops Windows 11 desktop (1600×900 at 100 %, deleted afterwards), a self-contained build of the tree (`0.2.8-dev.walk3`), with the same BC-TEST items and three groups with one card each, the same helpers, and the state read through UI Automation. **A walk that shows:** Up ×3, Down ×3 → the ring on Ideas, the last group, and the footer reads "↑ ↓ groups · ↵ or → back to the list"; Down → the walk ends, Ideas stays shown, the footer is the tab's again; Down again → the list. Held Down (7 key-downs from Work) → Home, Ideas, and it stops there, still in the walk. Up, then Enter → the walk ends with Home shown and the panel stays open (nothing pasted); Right → the next tab; Left, Enter → the card is pasted (the panel goes away, the clipboard holds it). Space → the walk ends with Work shown and the search box stays empty. Held Enter (6 key-downs) → the walk ends once and nothing is pasted; held Space (6) → the walk ends once and nothing is typed. On the logo, Enter → the walk ends with the whole history. Shift+Enter in the walk → pastes as plain text, as before. **A walk that picks, unchanged** (Ctrl+click: Work + Ideas): Down ×4 → the ring stops on the last group and the walk stays; Space → Ideas leaves; Enter → Ideas joins. The app's log: 0 WRN/ERR. Unit tests: `PanelNavigationTests` 117 cases (87 before): `StepInGroups` 17 cases, `PickKey` 34 cases with the walk that shows and the repeats after a walk, and one whole walk that shows. Core 1,189 of 1,189. The whole suite: 1,526 = 1,522 passed + 3 opt-in skipped + 1 failed (`QuickSuccessiveCopies_AreAllCaptured`, the timing test of §6, which this change does not touch). 0 doc warnings on a `--no-incremental` rebuild (117 warnings as before) | claude-desktops MCP tools; helpers in session 6c903e69's scratchpad, `vm/` (`step.ps1`, `holdkey.ps1`, `state.ps1`, …); the test executables; `dotnet build --no-incremental` | ✅ Not run: the dark theme, a column longer than the panel, real hardware |
| **The corrected 0.2.8 installed on this PC** (2026-10-10, the user's answer "Reinstall now" to the question whether the candidate on their PC must get the groups-column correction; §3.1's "move X.Y.Z to the latest changes" path). In a worktree at `c0db2d1`: the full suite 1,496 = 1,493 passed + 3 skipped, 0 failed; `package.ps1 -Version 0.2.8 -Architectures x64` (zip 71.2 MB, `.7z` 43.5 MB; only the known CS0108); `test-update.ps1 -Version 0.2.8` ended with its success line ("install.ps1's update mode works for BetterClipboard-0.2.8-win-x64.zip."). `install-local.ps1` with that worktree's `install.ps1` (Windows PowerShell 5.1, env stripped, `-NoLaunch`, 14 s): SHA-256 OK (`fa328e55…`), the running `0.2.8+fe3367b` closed gracefully ("Exiting." in its log), "Win+V was already released from Explorer"; the leftover process object (PID 97064, 0 threads) was skipped. Started through Explorer with a temporary shortcut (`--background`): PID 63336, parent `explorer.exe`, 76 environment variables without `CLAUDECODE` and `MSYSTEM`. Afterwards: the exe 0.2.8.0 / `0.2.8+c0db2d1…`; Installed apps and `installer.json` 0.2.8; unchanged: the Run key's value names (BetterClipboard, IDMan) and command line, the Start menu shortcut, `DisabledHotkeys` (`V`), Explorer's 42 Advanced values, the user PATH (hash) and the update settings. Log after "BetterClipboard 0.2.8 starting": 0 WRN/ERR; store opened (encrypted); "Win+V registered with RegisterHotKey"; automatic update check on; the panel prepared in 557 ms. The five Explorer processes from before are unchanged (no restart). The `explorer.exe` that opened the shortcut (PID 9536, the app's parent) ended by itself within a few minutes | worktree + `dotnet test --solution` + `package.ps1` + `test-update.ps1` + `install-local.ps1` + session 7f3c8c3f's `launch_background.ps1`, `install_state.ps1`, `state_extra.ps1` (before and after), `env_names.py` | ✅ The PC runs the corrected candidate. The groups-column keys were not driven on the user's screen: their live check is the row below. Nothing is pushed or tagged |
| **Arrow keys in the groups column, corrected** (2026-10-10, §2.31: "i meant pick the icon of Clipboard which is basically show all, no groups. not the 0 index"). The user met the first version (`9ca6425`) in the 0.2.8 release candidate on their PC (`0.2.8+fe3367b`, the row below). On a second claude-desktops Windows 11 desktop (1600×900 at 100 %, deleted afterwards), a self-contained build of the corrected tree (`0.2.8-dev.walk2`), with the same BC-TEST items and three groups with one card each, the same helpers, and the state read through UI Automation. **No group shown:** Up on the top row → the ring on the logo ("Arrow keys here"), no group shown, the list unchanged (4 items, the top row selected), the footer shows the walk's keys; Up → stays; Down → Work, the first group, shown alone with the ring. **One group shown:** Down, Right, then Up with Home shown → the ring on Home and nothing else changed; Up, Up → Work, then the logo and the whole history. **Several merged** (Ctrl+click: Work + Ideas): Up → the ring on Work, the view unchanged, the footer shows the pick keys; Down, Space → Home joins (3 rows) and the search box stays empty; Enter → Home leaves; Esc, then Win+V → a clean start. A screenshot shows the ring on the logo with the four items listed. The app's log: 0 WRN/ERR. Unit tests: `PanelNavigationTests` 87 of 87 with the entry cases changed (nothing shown, and only a deleted group "shown", now answer the logo; a column of one group too); Core 1,159 of 1,159; the solution builds with 0 errors | claude-desktops MCP tools; helpers in session 6c903e69's scratchpad, `vm/`; the Core test executable | ✅ The release candidate on the user's PC had the first version; it got the correction the same evening (the row above). **Tag v0.2.8 on `c0db2d1` or a later commit, not on `fe3367b`**; the newest notes draft is in `c0db2d1`'s message |
| **0.2.8 installed on this PC before its release** (2026-10-10, "Release another version and install it", then "prepare for release, install locally and ill give you green light to actually release", §3.1). Bump `fe3367b` (0.2.7 → 0.2.8; its message holds the drafted tag notes: the weekly reminder, the groups column's keys, the tooltip-click fix and the installer fix). In a worktree at `fe3367b`: the full suite 1,496 = 1,493 passed + 3 skipped, 0 failed; `package.ps1 -Version 0.2.8` in 316 s (zips 71.2 / 68.7 MB, `.7z` 43.5 / 39.5 MB for x64 / ARM64; only the known CS0108); `test-update.ps1 -Version 0.2.8` (x64) 17 of 17; `package-chocolatey.ps1` packed `betterclipboard.0.2.8.nupkg`, 83.0 MB. `install-local.ps1` with that worktree's `install.ps1` (Windows PowerShell 5.1, env stripped, 26 s): SHA-256 OK, and the running 0.2.7 closed gracefully ("Exiting." in its log; 0 WRN/ERR in its whole run). The process list still held the update QA's leftover object (PID 97064, 0 threads), which `b14a76c`'s `Stop-RunningApp` skipped as it should. "Win+V was already released from Explorer", so no Explorer restart. Started through Explorer: parent `explorer.exe`, 76 variables without `CLAUDECODE`/`MSYSTEM` (`GH_TOKEN` is in the owner's own HKCU environment). Afterwards: the exe 0.2.8.0 / `0.2.8+fe3367b…`; Installed apps and `installer.json` 0.2.8. Unchanged: the Run key's names (BetterClipboard, IDMan), the shortcut, `DisabledHotkeys` (`V`), Explorer's 42 Advanced values and its five processes, the user PATH, the update settings (check on, nothing skipped, no reminder shown yet). Log after "starting": 0 WRN/ERR, Win+V by `RegisterHotKey`, "installed by the installer (it can update itself); automatic check on", Windows import 0 new of 27. Not pushed or tagged: the release waits for the owner's green light | worktree + `dotnet test --solution` + `package.ps1` + `test-update.ps1` + `package-chocolatey.ps1` (pack only) + `install-local.ps1` + scratch `launch_background.ps1`, `install_state.ps1`, `state_extra.ps1` (before and after: names, counts and hashes only), `env_names.py` | ✅ |
| **Arrow keys in the groups column** (2026-10-10, §2.31: "instead of picking lowest/last group pick the first selected one (if none the top). If multiple selected, only space/enter press or click on group will add the group into the list of selected groups"). On a claude-desktops Windows 11 desktop (1600×900 at 100 %, no GPU, deleted afterwards): a self-contained build of the working tree (`0.2.8-dev.walk`), started in the background with its command line on; four BC-TEST items through `bclip put`; the groups Work, Home and Ideas made with "+", and one card put into each through the card menu, all through UI Automation. A script inside the desktop sent the keys, and the state was read through UI Automation after each step: the icons' item status, the header, the footer, the rows, the search box's value. **No group shown** (the first version's rule, which the user corrected the same day: the row at the top of this table): Up on the top row → Work, the top group, shown alone with the ring ("Shown, arrow keys here", "› Work", "1 in Work"); Up → the logo and the whole history; Up → stays; Down ×4 → Work, Home, Ideas, stays. **One group shown:** Right, then Up with Home shown → the ring on Home and nothing else changed; Down → Ideas alone. **Several merged** (Ctrl+click: Work + Ideas, 2 rows): Up → the ring on Work, the view unchanged, the footer shows the pick keys; Down → the ring on Home ("Arrow keys here"), still 2 rows; Space → Home joins (3 rows, "› Work + Home + Ideas") and the search box stays empty; Enter → Home leaves and the panel stays open; 6 key-downs of a held Space and 5 of a held Enter → an odd number of picks each (one, by the rule), no space typed; Up ×2 → the ring on the logo, the view unchanged; Space there → the whole history, still picking; Down → the ring on Work, still the whole history; Space, Down → Work shown, the ring on Home, still "› Work"; Page Up → the logo; Page Down → the lowest group; Ctrl+Space and Shift+Space pick; a typed letter ends the walk and searches the merged view; Shift+Enter pastes the selected card as plain text (the panel goes away, the clipboard holds the card); a group created while the keys pick → the ring stays on its group and the keys go on; the mouse moved onto a row → the walk ends and that row is selected; Esc, then Win+V → a clean start. **A walk that shows, as before:** Space is typed and ends the walk; Enter pastes the first card of the group shown. Screenshots: the ring on a shown icon and on one that is not shown; the footer next to the count of three named groups ("Space/↵ add or remove · → back · ↑ ↓ grou…"). The app's log of the three runs: 0 WRN/ERR | claude-desktops MCP tools (`press_keys`, `click` with Ctrl, `move_mouse`, `screenshot`, `run_command`); helpers in session 6c903e69's scratchpad, `vm/` (`seed.ps1`, `mkgroups.ps1`, `addto.ps1`, `step.ps1`, `holdkey.ps1`, `state.ps1`) | ✅ after one fix it found: the first footer hint was cut before its way back at the default width. Not run: the dark theme, a column longer than the panel, deleting the group under the ring, a click on an icon during a walk (the unchanged click path), real hardware |
| Arrow keys in the groups column, unit tests: `PanelNavigationTests` 87 cases (51 before). `EnterGroups`: 12 cases (nothing shown, each single group, several in either order, ids that left the column, an id given twice), plus columns without groups or with one, the selection's own list, and nulls. The moves from the new entry point. `PickKey`: 22 cases (Space and Enter, their chords, Alt or Win, repeats, a walk that shows, the rows). One whole walk that picks, with `GroupSelection`. The whole suite: 1,496 = Core 1,159 + Windows 337. Core: 1,159 of 1,159 passed. Windows: 1 to 3 timing tests failed in each of four runs, while Rider used about 10 cores of this PC (total load about 60 %): `QuickSuccessiveCopies_AreAllCaptured` every time (24 of 25 copies captured), and of `PromptArchiveTests` `Claude_ImportsThenFollowsAppends`, `PauseAndOffOn` and `Codex_AFileKeptOpenForAppending_IsFoundByThePoll` in some runs. The first one failed the same way, 3 of 3, in a build of the unchanged `541bdce` in a separate worktree. 0 doc warnings on a `--no-incremental` rebuild (117 warnings as before: xUnit analyzers and CS0108) | `dotnet test --solution`, the test executables with `-class` and `-method`, `dotnet build --no-incremental`, a worktree at HEAD | ✅ Core. ⚠️ Windows timing tests under load. This change touches no Windows code and no clipboard or prompt code |
| **The weekly update reminder** (2026-10-10, §2.30: "Once a week, can we show a tooltip over the update icon (preferably above the window to not hide anything) when theres an new version"). On a claude-desktops Windows 11 Pro 25H2 desktop (1600×900, no GPU, deleted afterwards): a build of the working tree labelled 0.2.6, installed with `install.ps1 -Package`, so the real feed offered the real 0.2.7 a minute after the start. Times are from the injected Win+V. **Shows:** no tooltip at 0.2–0.3 s, there at 0.7–0.9 s, still there at 5.5 s, gone at 7.0–7.3 s; the log line and the setting 2.0–2.1 s after Win+V. **Place at 100 %:** wholly over the window, 4 px above its top edge (tooltip bottom 287, edge 292), centered on the button within 1 px; before `PlacementRect` it lay flush on the button, across the edge (270–301 against the edge at 293). **At 150 %:** 6 px above the edge, centered within 2 px. **Panel at the top of the screen** (the pointer at y 12; and the default height at 150 %, which fills the screen): left of the button over the title, the search box free. **While it shows:** "copy" typed 1.3 s after Win+V reached the search box and the tooltip stayed; Enter 1.1 s after Win+V pasted the first item into Notepad, and tooltip and panel were gone; Esc closed both. **Once a week:** reminded 6 days ago: nothing; 8 days ago: shown; never: shown. **Not shown:** the version skipped (button not highlighted); the automatic check off (button highlighted from the cached list). **Glimpses:** Esc 1.0 s and 1.1 s after Win+V: not counted; the third summon counted 0.7 s after Win+V; the fourth showed nothing. **A click on it:** before the guard the update dialog did not open, the search box lost its caret, and "abc" and "xyz" typed afterwards were lost, also after the tooltip had closed; with the guard the dialog opened (focus on *Update and restart*), the week started, Esc gave the keyboard back, and typed letters reached the search box. **A click on another tooltip** (*Settings*, with real mouse input): "qw" typed afterwards reached the search box. **A drag** of the header by 60, 82 px: the tooltip closed when the window moved, was not counted, and typing worked afterwards. **Also:** the dark theme (a dark tooltip), the groups column open, and the button's hover tooltip back at WinUI's own place after a reminder. The app's log of the whole session (430 lines): 0 WRN/ERR. First summons: "Panel on screen 50 / 66 / 76 ms after the shortcut" | claude-desktops MCP tools; scripts inside the desktop (`helper-reset.ps1`, `helper-shoot.ps1`, `helper-who.ps1`: session 3103c128's scratchpad `tip/vm-helpers`); frames measured with Pillow (`tip/sheet.py`, `tip/frames`) | ✅ after two fixes it found: the place (flush on the button → over the window), and the dead keyboard after a click on a tooltip (a fault of every tooltip of the panel). Not run: Narrator, a real GPU, several monitors, Windows' text size raised, touch; the tray menu's *Update to …* with a reminder due |
| Reminder unit tests: `UpdateReminderTests`, 26 cases (the first reminder is due at once; six days no, a week yes, to the second; a remembered time up to a day ahead is believed, one further ahead counts as never; any offset; never due without the update system, without an offer, for a skipped version, without automatic checks, while an update downloads, is verified or installed; still due while a check runs, for a copy extracted by hand, after a failed check; as wanted as the button is highlighted, except without automatic checks; the room over the window; the setting: unset by default, a round trip as UTC, a file from before, a time written by hand). The whole suite: 1,460 = 1,457 passed + 3 opt-in skipped, 0 failed. A run while the test desktop held 4 cores failed two timing tests (`ClientHangUp_CancelsHandler`, `QuickSuccessiveCopies_AreAllCaptured`); alone, the first then passed 3 of 3 and the second 1 of 3; both passed in the whole suite once the desktop was deleted. 0 doc warnings on a `--no-incremental` rebuild | `dotnet test --solution`, the Core test exe `-class`, `dotnet build --no-incremental` | ✅ |
| **Update QA on the owner's own install** (2026-10-10, "manual QA my installation on this machine in various installation ways … all scenarios and edge cases"; Windows 11 Pro N 26200, an administrator account with UAC on, the app, the installer and the updater at medium integrity, Defender's real-time protection on, 100 % scaling, two monitors). The app was driven through UI Automation in short guarded sequences (§4); the real GitHub releases 0.2.6 and 0.2.7. **Four real updates 0.2.6 → 0.2.7, from four ways of installing:** (1) as installed by `irm \| iex`: the panel's update button → dialog → *Update and restart*; (2) after `install.ps1 -Version 0.2.6`: the panel again; (3) after `install.ps1 -Package <zip> -InstallDir <a folder named "BetterClipboard QA é">`: Settings › Updates › *Update to 0.2.7…*, with an isolated copy running, which was not touched; (4) after `install.ps1 -Version 0.2.6 -NoUpdateCheck`: the panel, with the switch off (the offer came from the cached list, no request was made, and the switch stayed off across the update). **Each time:** download 3.0–3.8 s (74,482,994 bytes), "Update package verified", the app closed 4.4–5.7 s later (the installer checks and unpacks the package first), the new process started 1.0–1.6 s after the old one ended, Win+V was registered again 3–4 s after "Exiting.", "The update to 0.2.7 was installed.", 11–12 s from the click to the running 0.2.7. **After each:** the folder equals the release zip file by file (476 files, none missing, extra or changed), no staging folder next to it, `updates\` holds only the cached list, `result.json` consumed; Installed apps, `installer.json` and the exe say 0.2.7; unchanged: the Run key (BetterClipboard and IDMan), the shortcut's target, `DisabledHotkeys` (`V`), the 42 values of `Explorer\Advanced`, both Explorer processes (never restarted), the user PATH (hash), `settings.json` (hash), the store (202,375,168 bytes, same key file), 0 WRN/ERR after the start. **Decisions** on the reinstalled 0.2.6, in one sequence: the button is lit right after the reinstall (from the cached list); *Later* closes the dialog and leaves it lit; *Skip this version* closes it and the button says "… (you skipped it)"; the dialog then offers *Stop skipping this version*, which lights it again. **Cancel** at 4 of 71 MB: "cancelled before anything was installed", the dialog back at the offer without an error, no partial file, the same process. **The install folder in use** (another process with its current directory there): the app closed, the installer tried for 10 s, put the folder back (equal to the 0.2.6 zip, nothing left over), started 0.2.6 again (Win+V back 11.7 s after "Exiting."), and the app logged and notified "The update to 0.2.7 was not installed: The install folder … stayed in use by another program for 10 s … Close programs that use that folder …, then update again."; the retry after the folder was free worked. **Up to date afterwards:** the button "BetterClipboard 0.2.7 is up to date", the dialog "Version 0.2.7 is the latest release. Checked just now." with *Check now* / *Close* and "What is new in this version". At the end the copy is 0.2.7 in the default folder with the switch on, as before the test, and the test's custom folder is removed | session 3103c128's scratchpad `qa/`: `scenario.py` + `bcui.py` (guarded UI Automation, `PrintWindow` captures of the dialog's own window and of the panel's header only), `state.ps1` + `statediff.py` (content-free snapshots), `cmpzip.py` (folder against release zip), `watch_update.py` (timeline from the log and the process list), `launch_background.ps1` (start through Explorer) | ✅ the update works on a real install. ⚠️ Side effects of the test on the owner's PC, all reported: an update dialog of a test copy took the keyboard from the browser for about 20 s (it was left open between two tool calls); a test copy's panel stayed on top for about 30 s; a notification "BetterClipboard was updated to 0.2.7" came from a test copy; one real copy was not captured while four instances read the clipboard ("Clipboard stayed locked … change #1529 not captured"); a crashed test copy left a process object behind, which keeps 76 of that copy's program files (117 MB, in the session's scratch folder) in use until Windows restarts (§4, next rows). Not run: the tray menu's *Update to …*, a standard user account, an elevated copy, ARM64 |
| **Update QA with isolated copies on the same PC** (no change to the installed app; each copy in its own folder with its own data folder, capture paused, every source of the user's data off). (1) **Rehearsal:** an installer-kind copy of the real 0.2.6 with the real 0.2.7 package from a stand-in feed: 9 s from the click to the running 0.2.7, 1.0 s without a process, the folder equal to the 0.2.7 zip. (2) **The automatic check, nine cases, no window** (the copy starts in the background and checks 60 s later; the feed's request log and the app's log are the evidence): a good list (one request, `User-Agent: BetterClipboard/0.2.6`, "0.2.7 is available"); a fresh cache (no request); a 25-hour-old cache (one request with `If-None-Match`, answered 304); error 500, a rate limit ("Try again after 1:42 PM"), text that is not the list, an empty list ("latest release unknown"), a connection closed without an answer (four connection attempts, then "could not be reached"), no answer for 40 s ("did not answer in time" after about 30 s). The copy kept running and exited cleanly every time. (3) **Four faults after *Update and restart*:** no `SHA256SUMS.txt` (404), a checksum list that disagrees with the release's digest ("the update cannot be trusted", no package request), a download cut at 20 of 74 MB, a download of the right length with other bytes ("does not match … so it was deleted"); each left the copy running with the dialog back at the offer and nothing in `updates\` but the cached list. (4) **`tools/release/test-update.ps1`** with the real 0.2.7 zip in Windows PowerShell 5.1: 17 of 17. (5) **Chocolatey, the released packages in a private root** (not elevated): `choco install` 0.2.6, the copy's log "a Chocolatey install (Chocolatey updates it); automatic check off" and no request, `choco upgrade` to 0.2.7 closed the running copy and replaced it, still classified as Chocolatey, `choco uninstall /KeepWinVReleased`; the user's shortcut, Run key, `DisabledHotkeys` and running app unchanged: 23 of 24 (the 24th was the clipboard warning above, not an update matter). (6) A copy extracted by hand: "not installed by the installer (updates come from the download page); automatic check off" | `qa/iso.py` (isolated copies with the environment Explorer would give them), `qa/feed.py` (stand-in feed with faults set through a control file), `qa/net_checks.py`, `qa/choco_qa.ps1` | ✅ Not read on screen here: the dialogs of the hand-extracted and the Chocolatey copy (read on the VM, §5 below); a download that the app's exit interrupts |
| **Installer: a BetterClipboard that cannot be stopped** (found and fixed 2026-10-10, `b14a76c`, §3.1). With the leftover process object of a crashed test copy on the PC (no threads, cannot be opened): `HasExited` is empty for it in Windows PowerShell 5.1 and PowerShell 7, so the old `Stop-RunningApp` waited its whole 15 s, and `Stop-Process` on it throws "Access is denied" under `$ErrorActionPreference = 'Stop'`: a full install would have ended after it had closed the running app. After the fix: `Test-ProcessRuns` says True for the installed app (69 threads) and False for the leftover object in both PowerShell versions, and three real installs on this PC (`-Version`, `-Package -InstallDir`, `-Version -NoUpdateCheck`) each finished in 11–16 s with exit code 0 | `qa/test_processruns.ps1` (the function taken out of the script's syntax tree), the three installs of the first row | ✅ Not run: the 18-scenario harness (it hides every BetterClipboard process, so this code does not run in it); a copy that runs as administrator next to a non-elevated installer. Not on GitHub yet: `irm \| iex` has the old lines until `main` is pushed |
| **v0.2.6 and v0.2.7 released; 0.2.6 installed on this PC** (2026-10-10, "release a new version and install on this machine, the release a new version with no binary diff … the agent of update system will test updating our user install"). **v0.2.6**: bump `24821a4` on `68b85bc` (everything since v0.2.5, the update system `7d7df8d` included); the full suite on that commit 1,434 = 1,431 + 3 skipped; the push range (18 commits) scanned (no secrets, no email; the four new PNGs looked at: demo data only); CI 38031322511 green (1,430 + 4 skipped on the runner, update mode, Chocolatey 28/28); tag object `ed3db9a`; release run 38031633000 green; published 06:44:10Z. **Installed** with the README's `irm \| iex` (Windows PowerShell 5.1, env stripped, `-NoLaunch`; raw `install.ps1` checked byte-equal to `main` first): 22 s, the x64 zip's SHA-256 `edad3806…` OK, graceful close of `0.2.6-dev.52539eb`, "Win+V was already released from Explorer"; started through Explorer (pid 78644, 76 variables, no `CLAUDECODE`/`MSYSTEM`); the exe `0.2.6+24821a4…`; Installed apps and `installer.json` 0.2.6; Run value and shortcut unchanged, IDMan kept, `DisabledHotkeys` `V`; log 0 WRN/ERR, "Updates: this copy is installed by the installer (it can update itself); automatic check on.", and at 06:47:05Z "Update check: up to date (running 0.2.6, latest release 0.2.6)". **v0.2.7**: bump `11a2d4d`, whose only change from v0.2.6 is the `<Version>` line; CI 38032267416 failed on attempt 1 (two timing tests, §6) and passed on attempt 2; tag object `5a6dad3`; release run 38032773704 failed on attempt 1 (a third timing test; nothing published) and passed on attempt 2; published 07:05:56Z, Latest. Both pages: the tag's notes + footer, no mojibake, every `SHA256SUMS.txt` line equal to GitHub's digest; `install.ps1` byte-identical in both. 0.2.7 was **not** installed here: the installed 0.2.6 is left for the update system's agent | `gh`, `install_state.ps1`, `env_names.py`, scratch `verify_release.py` | ✅ released; the update from 0.2.6 to 0.2.7 is the update agent's test |
| **README photos retaken** (§3.4, 2026-10-09): a claude-desktops Windows 11 desktop ("bc-photos", 2560×1440 at 150 %, dark theme) with a package of `7d7df8d` labelled 0.2.3, installed with `install.ps1 -Package` (SHA-256 OK, Win+V released, 29 s). Demo data from `tools/readme/demo-data.ps1` (7 Claude Code and 2 Codex prompts imported in 0.4 s), then copies from Notepad, Windows Explorer, Microsoft Edge and Snipping Tool (a 1426×500 snip), a pin, and three groups by drag and drop (5 cards in groups). Captured: the hero (All tab, groups column, five header buttons), the Claude tab (reached with Left twice; "6 prompts (7 sent)"), the top of Settings (the shortcut list; the chips "7 items · 3.3 MB · 1 pinned · 5 in groups"), and the update dialog under its highlighted button. The three window photos kept their sizes (692×947, 626×947, 1388×1307); `update.png` is 960×1033, composed from two screen captures by the new `tools/readme/compose-shot.py`, with 32 px of wallpaper on every side. The desktop was deleted with its persist folder | claude-desktops MCP tools (UIA, keys, mouse, drag), in-desktop PowerShell (`capture-window.ps1`, the new `capture-screen.ps1`), Pillow on this PC | ✅ four photos. Found on the way: a second selection bar after leaving a prompt or shell tab (two rows below) |
| **The regular instance against the real GitHub, with an offer** (the same desktop; the build is labelled 0.2.3, so the real feed's 0.2.5 is newer). Started with the check off (`--set-update-check off`; the installer gates `-NoUpdateCheck` on ≥ 0.2.6): "automatic check off", a plain update button, no request. Settings › Updates: the switch on, then *Check now*: "BetterClipboard 0.2.5 is available. You have 0.2.3. Released Sat 8:44 AM. …", the accent button "Update to 0.2.5…", and the panel's update button filled. The dialog: the title, *Update and restart* / *Later* / *Skip this version*, then "What is new" with the real release notes (a code block, bold and italic text, a link, nested bullets). *Update and restart*: "Downloading BetterClipboard 0.2.5… 35.5 of 70.8 MB" with the ring in the header. The log: approved (74,280,969 bytes); 5 s later "Update package verified: its SHA-256 matches the release's checksum list"; "Starting the installer for 0.2.5"; 4 s later the warning "Update to 0.2.5 failed (Installer): The installer ended before it could start (code 1) …", the same text in the dialog's error bar, and the app still running under its process id. The 0.2.5 release's `install.ps1` has no update mode, so Windows PowerShell refused the arguments before the script ran. After a restart of the app, the button was filled again without a request (the cached list) | screenshots, the app's log, a listing of `updates\` | ✅ the offer, a real download, the checksum check and the hand-over, with a real release. Still open: a release whose installer has update mode (the first one after 0.2.6, §6) |
| **A second selection bar after leaving a prompt or shell tab** (found and fixed 2026-10-09, `2f86791`, §2.27). Before, on the build of `7d7df8d`: All → Left (Codex) → Right (All) showed the bar on the first and the second card, 2 of 2; Codex → Claude showed it on another row, 2 of 3; UI Automation reported only the first row as selected. Clean before the fix: Down and Up, All → Pinned → All, All → Images → All, Images → Text (the tabs that patch their list). After the fix (a package of the fixed code installed over the running copy): All → Codex → All, All → Codex → Claude, Claude → Cmd → Pwsh, and Pwsh → Cmd → Claude → Codex → All gave one bar each; an invalid regular expression showed its empty state, and the list came back with one bar; no warning in the log; the first summon took 104 ms. Solution build: 0 errors, 0 doc warnings | region screenshots of the panel after each key sequence, `find_elements` for the rows' states | ✅ on the GPU-less VM. Not checked: the Everything tab (the same code path as the shell tabs), real hardware |
| **Update system** (§2.29, 2026-10-09), end to end on a claude-desktops Windows 11 VM ("bc-update": Windows 11 Pro 25H2 26200, 1600×900 at 100 %, an administrator with UAC off, no GPU). Self-contained packages `0.2.6` and `0.2.7` built from the working tree; a stand-in feed on 127.0.0.1 (an `HttpListener` script with a request log and a throttle). Three passes, the desktop reset before the second and the third; the third ran the final build. (1) `install.ps1 -Package <0.2.6 zip> -NoLaunch`: verified against the `SHA256SUMS.txt` next to it, installed, Win+V released, the closing note about the update check with the privacy link; a seeded Run-key neighbor kept. (2) An isolated instance from the installed folder (its own data folder with a copy of `installer.json`, and `BETTERCLIPBOARD_UPDATE_FEED`): "installed by the installer (it can update itself); automatic check on"; the automatic check ran 62–64 s after the app started, 60 s after the update service did (`User-Agent: BetterClipboard/0.2.6`), and found 0.2.7; the update button had the accent fill and the name "Update available: BetterClipboard 0.2.7". (3) The dialog opened under its button: at full height with the panel in the upper half of the screen, shortened to about 500 DIPs, down to just above the taskbar, with the panel in the lower half. *Update and restart* / *Later* / *Skip this version*, then "What is new" with every kind of block drawn (headings, nested bullets, numbered items, a code block, a quote, a table, task boxes, a rule, links). (4) *Update and restart*: "13.8 of 71.1 MB", "37.1 of 71.1 MB" with the ring in the header button; "Installing…"; the app exited 11–22 s after the package was verified, and 0.2.7 started 7–15 s later as the same isolated instance (its parent: the installer's PowerShell), with the notification "BetterClipboard was updated to 0.2.7". Product version, Installed-apps `DisplayVersion` and the instance's `installer.json`: 0.2.7; `updates\` held only the cache; the transcript ended "BetterClipboard was updated to 0.2.7."; 0 WRN/ERR; a copy made before the update was still in the history. (5) Afterwards: "BetterClipboard is up to date", "What is new in this version", "0.2.7 · 9 Oct 2026". (6) Settings › Updates: the status line, "Update to 0.2.8…" as the accent button and the dialog under it; *Cancel* during a download ("cancelled before anything was installed", the partial file gone); *Skip this version* (button plain, `SkippedUpdateVersion` 0.2.8) and *Stop skipping this version*. (7) Failures: a package whose installer has no update mode (the real 0.2.5 zip offered as 0.2.8): the app kept running and showed "The installer ended before it could start (code 1) …" in an error bar, and the log of the earlier update was gone; a checksum the package does not have: "The download does not match the release's checksum, so it was deleted …"; the install folder held open by another process: 20 rename attempts, the old folder back, 0.2.7 started again in the background, and the notification "The update to 0.2.9 was not installed" ("… then update again."); another version offered after a failure: the old failure text gone. (8) The tray menu's *Update to 0.2.8…* opened the panel with the dialog. (9) A copy without the installer's record: *Download page* / *Copy install command* / *Later*; the command was on the clipboard and was not recorded as a copy. A copy in a `…\lib\betterclipboard\tools\app` folder: no request until *Check now*, then *Copy command*. (10) Requests: one per check; none when the dialog opened within 5 minutes of the last check; after 5 minutes, and across a restart, a conditional request (`If-None-Match`) answered 304; `--set-update-check off` while the app runs: exit 3 | `desktop_call` (keys, clicks, UIA, screenshots, `run_command`); recordings `bc-update-flow-1.mp4`, `-final.mp4` and `-pass3.mp4` in the desktop's persist folder; scratch `vm/feed-server.ps1`, `make_feed.py`, `make_variants.py` (session 3103c128) | ✅ after nine fixes the passes found: the bubble moved away from its button (now `UpdateDialogLayout`), the notes' scroll position not reset, uneven task boxes, a warning at exit ("Closing the update dialog failed"), the isolated instance coming back as the regular one (the restart went through Explorer), a failure pointing at the log of an earlier update, a failed installation's text under a newer offer, leftover packages of other versions, and "run the installer again" in an update's failure text. Not run: a standard user, UAC on, ARM64 |
| The regular instance against the real GitHub (the same VM, first pass; 0.2.6 installed with `-Package … -NoUpdateCheck`): settings `CheckForUpdates: false`; log "Automatic update check switched off from the command line", then "automatic check off". Settings › Updates › *Check now*: "BetterClipboard is up to date. Version 0.2.6, newer than the latest release (0.2.5). Checked just now." The dialog: "The latest release", "0.2.5 · 3 Oct 2026" and the real release notes. *View on GitHub* opened the release page in Edge, and the panel got out of the way. Esc closed the dialog and the search box took the keys again; a click on another app closed the dialog and the panel; a second click on the button closed the dialog. Before the install, six refusals (`-Update` with `-AddToPath`, `-PackageSha256` without `-Package`, a wrong hash, `-Update` without an installed copy, a missing package, a malformed hash): exit 1 each, nothing installed, no staging folder | the same desktop | ✅ An offer to the regular instance needs a release above the running version: none exists yet (§6) |
| The update button does not slow the summon (same VM, quiet): the unchanged head (`92639ea`) and the final build, each as an isolated instance from its own folder, 8 alternating runs each with the order swapped halfway. The first summon after the warm-up, from the app's log ("Panel on screen N ms"): 53–86 ms (mean 66) without the button, 47–82 ms (mean 67) with it. The warm-up: 340–1,281 ms (median 403) without, 296–632 ms (median 369) with. The second build of each pair was 10–15 ms slower whichever build it was | scratch `summon-compare.ps1` in the desktop (`--background`, 16 s, `--show-flyout`, `--exit`, the log lines) | ✅ no difference. One first summon right after an update took 912 ms on the busy desktop |
| The app's update client against the real GitHub, on this PC: the release list (6 releases, 77.5 KB) in 880 ms; again with its entity tag: "not modified" in 100 ms; for a copy "running 0.2.3" the offer is 0.2.5 with the x64 zip (74,280,969 bytes) and GitHub's digest; the checksum list (402 characters) lists the package and agrees with the digest; the download through GitHub's redirect: 4.0 s, 29 progress reports, SHA-256 equal to the list, no partial file left | scratch `probe/github_probe.cs` (file-based, `#:project` Core) | ✅ |
| `tools/release/test-update.ps1` on this PC, against the final packages: 17 of 17 checks in PowerShell 7.5.8 (the 0.2.7 package) and in Windows PowerShell 5.1 (the 0.2.6 package), and in both shells on an earlier 0.2.7 package. A wrong checksum and an option that does not go with `-Update` are refused with the copy untouched and the failure in the result file; the real command line replaces the folder as a whole, reports success with the version, moves `installer.json`'s version and keeps its other values, writes the log, leaves no staging folder and starts nothing. The user's Run values, Installed-apps entry, `DisabledHotkeys`, PATH and running app were unchanged | the script; wired into `ci.yml` and `release.yml` | ✅ locally. On GitHub it first runs after a push |
| Update system, unit tests: 281 new. Core 265: `SemanticVersionTests` 31, `GitHubReleasesTests` 10, `UpdateCatalogTests` 11, `Sha256SumsTests` 14, `UpdateSourceTests` 21, `InstalledCopyTests` 20, `UpdateSettingsTests` 11, `UpdateTextTests` 8, `UpdateDialogLayoutTests` 10, `MarkdownTests` 80, `UpdateClientTests` 21, `UpdateServiceTests` 28 (a manual clock, a fake HTTP handler, a recording installer). Windows 16: `UpdateInstallerTests` (a scratch registry key; small zips; a stand-in `install.ps1` run by the real Windows PowerShell from a folder named with a space, a quote, an ampersand, percent signs and a non-ASCII letter). The full suite: 1,434 = 1,431 passed + 3 opt-in skipped | `dotnet test --solution`; each new class with the test executables; a `--no-incremental` rebuild | ✅ 0 doc warnings, no analyzer warning in the new files. One earlier full run had `QuickSuccessiveCopies_AreAllCaptured` fail (24 of 25 copies read, 1 superseded, under load); it also failed 1 of 3 runs alone at that moment and passed in the later full runs: a load-dependent timing test this change does not touch |
| **Getting around the panel** (§2.28, 2026-10-09), live on a claude-desktops Windows 11 VM ("bc-nav": 1600×900 at 100 %, no GPU; self-contained builds `0.2.6-dev.nav1` and `nav2` from a private worktree; 17 BC-TEST items through `bclip put` and a PNG in the Screenshots folder; three groups made with **+** and filled by drag and drop). **Remembered tab:** Right ×2 → Text, Esc, Win+V → Text, `"LastTab": "Text"`; the last tab (Cmd) → Esc, Win+V → Cmd with the strip at its end ("‹" shown); after a restart, before the warm-up (Win+V 1.5–1.7 s after the start: 481, 514, 540 ms with Cmd, 767 ms with Pwsh) and after it (52 and 86 ms); "Cmd tab" switched off in the file → the summon starts in All and `LastTab` stays "Cmd"; switched on in Settings while the panel was hidden → the next summon is in Cmd. **Left / Right:** from the last tab Right → All, from All Left → Cmd; with "fox" typed they move the caret ("foZx" after Left, Left, Right, Z); after Down they switch tabs with the search kept; nine key-downs without a key-up from Text stop at Cmd; with a tab focused (after a click) the focus moves along. **Groups:** Up on the top row → Ideas (the lowest) shown, ring on its icon, footer "↑ ↓ groups · → back to the list · ↵ paste"; Up → Home → Work → the logo (16 items, ring on the logo) and stays; Down → Work; Right → the tab's own footer, Down selects row 1; from Work's view Up, Up → Ideas again; four key-downs of Up from row 1 stop at row 0; with the column closed Up does nothing; typing "33" in the column searches the group and ends the walk, Enter pastes "#3366FF" into Notepad. **Hover:** the panel opened under a mouse resting where row 3 appears → row 0 stays selected; Down ×7 scrolls rows under it → the keyboard's row stays; a 2-px nudge → nothing; a 14-px move inside the row → that row; another row → that row; a wheel turn → the row now under the cursor; "number 0" typed with the mouse over row 1 → row 0 selected, Enter pastes it; a card menu closed with Esc → its card stays selected until the mouse moves; over tabs and group icons → nothing changes; the image peek still opens on rest and closes on a move; five drags of cards onto group icons still work. **Exit** with the panel open in the Cmd tab: no error (nav1 logged one, see §2.28) | claude-desktops MCP tools (`press_keys`, `move_mouse`, `scroll`, `drag`, `click`, screenshots), `state.ps1` / `restart-summon.ps1` / `toggle.ps1` (UI Automation and `keybd_event` inside the desktop; session 805f9263's scratchpad, `qa/`) | ✅ after two fixes found here: the exit-time reload error, and a closing menu handing the selection to the row under the mouse. The wheel was turned with the mouse already live; "the wheel alone hands over" is covered by the unit test only. Not on real hardware (§2.28 "Not built / open") |
| Getting around the panel, unit tests: `PanelNavigationTests` (51 cases: the circle both ways, held keys at the ends, no selection, no tabs; 15 cases of Left / Right between tabs, text and groups; list moves and clamps; the walk into the column only for a new Up on the top row; the column's ends; extreme values), `PointerSelectionGateTests` (10: the spot, the threshold on both axes, every suspend, an unknown spot, the wheel, extremes), `LastTabSettingsTests` (19: default, by name in the file, lenient reading, every filter round-trips, an unknown name does not quarantine the file), `GroupSelectionTests.Only` (1) | Core test exe `-class`; `dotnet test --solution` and a `--no-incremental` rebuild in the worktree | ✅ 81 new; full suite 1,153: 1,150 passed, 3 opt-in skipped, 0 failed; 0 doc warnings (117 warnings as before: xUnit analyzers and CS0108) |
| **This PC, 2026-10-09, later** ("install on this machine"): `0.2.6-dev.52539eb` (x64, from a worktree at `52539eb`: the instant summon `4d62d35` and its docs on top of the installed `b28e63f`) installed over the running `0.2.6-dev.b28e63f` with `tools/release/install-local.ps1` (Windows PowerShell 5.1, env stripped, Windows paths, `-NoLaunch`): the full suite first, 1,072 = 1,069 passed + 3 skipped, 0 failed; package 96 s (zip 71.0 MB, only CS0108); install 12 s, SHA-256 OK, graceful close ("Exiting."), "Win+V was already released from Explorer", no Explorer restart. Started through Explorer (`--background`, parent explorer.exe, 75 variables, `CLAUDECODE`/`MSYSTEM` absent). Run value, shortcut, `DisabledHotkeys` `V` unchanged; Installed apps and `installer.json` `0.2.6-dev.52539eb`; the exe `0.2.6-dev.52539eb+52539eb…`; the other Run value (IDMan) kept. Log after "starting": 0 WRN/ERR, "Panel prepared for the first Win+V in 501 ms" | install-local + session 7f3c8c3f's `launch_background.ps1`, `install_state.ps1`, `env_names.py` | ✅ (no UI checks on the user's screen) |
| **Instant summon** (§2.27, 2026-10-09), on a claude-desktops Windows 11 VM ("bc-perf": 1920×1080 at 100 %, no GPU, animations on, Win+V released, 41 BC-TEST items incl. two pictures, the app started from the agent's job and seeded with plain Win32 clipboard calls). 15 summons per build, from the injected Win+V: **before** (`8ea00ff`) in front 30 ms, first pixels 78 ms, picture complete 630 ms (p90 664) through 9–18 intermediate pictures; **after** in front 25 ms, complete 102 ms (p90 134) in one step. Letter N ms after Win+V, leaked before → after: 0 ms 14 → 15 of 15, 15 ms 10 → 11, 30 ms 1 → 0, 60/120 ms 0 → 0. Plain-window reveal latency: show 85 ms, uncloak 43 ms, move 39 ms. Uncloak-before-activate 102.5 vs 108 ms (noise ±10 ms). Whole-app idle CPU over 40 s: new 234 / 344 ms, old 234 ms | `tools/probes/probe_summon.cs` (published self-contained, run in the VM), a temporary summon trace (removed), scratch `run-variant.ps1`, `seed.ps1` | ✅ |
| Instant summon, functional QA on the same VM with the final build: Enter after a search pastes into Notepad; a click and an X typed inside the concealed panel's rectangle land in Notepad (`WindowFromPoint` → Notepad); Ctrl+Win+D → Win+V opens the panel on the new desktop, and again after Ctrl+Win+Left; Win+D then Win+V; a copy made while concealed is on top ("just now", 42 items) at the next Win+V, the pasted item had moved up meanwhile; search "short" + Text tab + a click on the wallpaper → next Win+V: empty search, All, full list, first card selected; Ctrl+G open/close across summons (window grows left, then back); Win+V right after a start: "Panel on screen 573 ms after the shortcut, its list reloaded after 681 ms (first summon)" | claude-desktops `press_keys`, `click`, `type_text`, screenshots, scratch `copy-one.ps1`, `qa-restart.ps1` | ✅ |
| `CollectionPatchTests` (13: an unchanged list raises nothing; a new copy on a full page = one insert + one remove; used again = one move; delete = one remove; disjoint and empty lists; reference matching; a duplicate in the collection; refusals before any change; 5 × 200 random reorders end exactly as wanted with no more changes than clear-and-refill) | Core test exe `-class`; full suite; full rebuild | ✅ 13/13; full suite 1,072: 1,068 passed, 3 opt-in skipped, `ClientHangUp_CancelsHandler` failed once (7.3 s under load) and passed 5 of 5 alone (0.46 s); 0 doc warnings on a `--no-incremental` rebuild |
| **This PC, 2026-10-09** ("Install everything", the admin account): `0.2.6-dev.b28e63f` (x64, from a worktree at `b28e63f`) installed over the running `0.2.5+64cbff6` with `tools/release/install-local.ps1` (Windows PowerShell 5.1, env stripped, `-NoLaunch`): SHA-256 OK, graceful close, swap, no Explorer restart (Win+V was released). Started through Explorer (`--background`, parent explorer.exe, 75 variables, `CLAUDECODE`/`MSYSTEM` absent). Run value, shortcut, Installed apps (`0.2.6-dev.b28e63f`), `DisabledHotkeys` `V`; the other Run value (IDMan) kept. Log after "starting": 0 WRN/ERR, "Panel prepared for the first Win+V in 559 ms" | install-local + session 7f3c8c3f's `launch_background.ps1`, `install_state.ps1`, `env_names.py` | ✅ (no UI checks on the user's screen) |
| QA pass on a claude-desktops Windows 11 Pro 25H2 VM (26200.8037, 1920×1080), **admin "Docker", UAC off**, `0.2.6-dev.e9eb799` installed with the real installer (`-AddToPath`, Win+V released, Explorer restarted). Shortcut box: Ctrl+Shift+K recorded; Win+E recorded and File Explorer did not open; Win+V and F9 (after adding it) said "already in the list" and the panel did not open; Shift+Win+F23 (Copilot key); 11 keys via VK-level SendInput: MediaPlayPause, Pause, PrintScreen, Apps, Ctrl+Alt+Oem102, 0x97, VolumeUp, Shift+Pause, Ctrl+Break, Ctrl+Alt+NumPlus, Win+Delete; typing and Backspace still work ("ctrl + alt + page down" → Ctrl+Alt+PageDown, Registered); "ctrl+v" refused with its reason; the menu's "Used before" (newest first, checks, remove keeps it, re-add moves it to the top). F9 and Ctrl+Alt+PageDown open the panel from Notepad. Panel: search, Enter paste, Ctrl+P pin, groups (new group, drag a card, group view "1 in Work", ItemStatus "Shown"), card menu, Forget forever (a re-copy with spaces + CRLF kept out), image peek and viewer (wheel zoom, Esc), Win+PrtScn → Snipping tab, Win+R → Run tab, Command Prompt → Cmd tab, persistence across a restart, `bclip` status/list/search/get | MCP desktop tools (UIA, keys, mouse), scratch `keys.ps1` (VK-level SendInput), `tab.ps1` (UIA tab select) | ✅ after 4 fixes found here (`0718a9b`): first-summon focus, owner-less copies labeled "BetterClipboard", Settings switches without accessible names, a stale shortcut hint |
| Same VM as the **standard user "bcuser"** (Users only, Medium; its own agent logon task, auto sign-in): `0.2.6-dev.0718a9b` installed per user without admin rights (`-AddToPath`; `DisabledHotkeys` `V` in its own hive, its Explorer restarted); app at Medium, store sealed with its DPAPI keys, 0 WRN/ERR. The first Win+V after the install's start typed "bravo" into the search box (the fix); Enter pasted. Recorder: Win+E recorded and added ("taken over with a keyboard hook"), then Win+E pressed in the box still recorded ("already in the list": the recorder re-hooks after the takeover hook) and from Notepad opened the panel instead of File Explorer; removing it gave Win+E back to Explorer at once. Every Settings switch, number box and combo box now has its card's name (UIA). `bclip` status/list/search/put. Restart: "Start with Windows" started it with `--background` (~55 s after boot). Update to `db9750c` over the running app, then the uninstall command of Installed apps: Run value, Installed-apps entry, folder, shortcut and PATH entry gone, history kept, `V` removed, Explorer restarted, Win+V probe 1409 (Explorer's again) | same tools, scratch `firstsummon.ps1`, `autologon.ps1` (Winlogon values + LSA secret) | ✅ after one more fix found here (`db9750c`): keys typed 100–250 ms after the first Win+V of a session went to Notepad |
| Same VM reset, **admin "Docker" with UAC on** (the user's setup): `0.2.6-dev.db9750c` installed from a non-elevated PowerShell (started through Explorer from the elevated agent: Medium); app and Explorer at Medium; "Panel prepared … in 382 ms". Win+V over an elevated Notepad (High) opens the panel; before the fix Enter silently pasted nothing; with `7df7086` the log says "Not pasted: the target window runs as administrator…", a notification "Copied: press Ctrl+V to paste" appears once, and the user's Ctrl+V pastes the item. Typed then Enter at 0 ms ("delta", 30 or 10 ms between keys) pasted "delta" 3 of 3 (before: the newest item). Elevated `bclip` (High) talks to the Medium app. The update to `7df7086` first failed at the folder swap ("being used by another process", app left stopped, `.new` left behind); with the fixed installer (`b28e63f`) the same update over the running app succeeded | same tools, scratch `typeenter.ps1`, `install-medium.cmd` | ✅ after 3 fixes found here: Enter racing the search, the silent elevated paste (`7df7086`), the installer's swap (`b28e63f`) |
| First summon of a session after a `--background` start, letter sent N ms after Win+V (VM, standard user) | scratch `firstsummon.ps1` (restart, SendInput, UIA) | `0718a9b`: 100 / 250 ms → Notepad, 500–1500 ms → search box; `db9750c` (warm-up): 30 ms → Notepad, 100 / 250 / 500 ms → search box; later summons: 10 ms → Notepad, 30 / 60 ms → search box |
| Installer swap retry and failed-swap cleanup (`b28e63f`) | scratch `test-rename-retry.ps1`: the functions loaded from install.ps1's AST, a folder held open by another process without FILE_SHARE_DELETE, PowerShell 7.5.8 and 5.1 | ✅ a 2.5 s lock → renamed after 3.1–3.4 s; a 20 s lock → gave up after 9.8–11.0 s, old folder kept, staging removed, restart called, the new message |
| Shortcut vocabulary, safety rules, recorder decisions, history (`e9eb799`), source attribution (`0718a9b`), integrity levels (`7df7086`) | unit tests: `HotkeyRecorderTests` 82, `HotkeySettingsTests` +3, `SourceAppResolverTests` 1, `ProcessIntegrityTests` 2 | ✅; full suite 1,059: 1,055 passed, 3 opt-in skipped, `QuickSuccessiveCopies_AreAllCaptured` failed while the VM ran (it fails the same on the unchanged `HEAD` then) |
| Chocolatey second audit (2026-10-03, §3.2). The archived validator's 78 rules applied to the built package: no Requirement, only the expected Notes and the long-script / fourth-script Guidelines. The new workflow's "Versions" and "Release archives" steps run from the YAML against the real API: 10/10 version cases (absent variables included), v0.2.5's archives downloaded and checksummed, v0.2.3 and a missing tag refused; `--repo` was missing (fixed). A second disposable VM (Windows 11 25H2, Chocolatey 2.7.4, PowerShell 7.6.6): `test-chocolatey.ps1` under pwsh 28/28 (CI's shell); both install channels in both orders 26/26, "Start with Windows" handed to the copy that stays (new in both uninstallers). `install.ps1`'s uninstall through session 7579fd35's harness (copied, `-SeedRun`): 10 runs in 5.1 and 7, real state unchanged. Every released `install.ps1` (v0.1.0-v0.2.5) checked: the uninstall's give-back wipes `Explorer\Advanced` when other letters are listed (§2.25). Moderation docs: no rule against starting the app or autostart by default | scratch `audit2/` in session 90da67ad's scratchpad; VM deleted | ✅ Workflow not run on GitHub yet (after a push) |
| Chocolatey re-review (2026-10-03, §3.2): the package-only fix `0.2.5.20261003` (`main`'s scripts + the v0.2.5 release's archives, SHA-256 checked against `SHA256SUMS.txt` and GitHub's digests; 82.1 MB; description 2,380 characters; no comments or placeholders; BOM scripts). A disposable claude-desktops VM, Windows 11 Pro 25H2, Chocolatey 2.7.4, Windows PowerShell 5.1, elevated: `test-chocolatey.ps1` 28 of 28 (new: other Run values and Explorer settings kept, `DisabledHotkeys` untouched by the install, Win+V and Win+Q given back, Win+J kept), graceful closes, Explorer restarted by the uninstall; SYSTEM 13 of 13; a second administrator in the desktop's session 12 of 12; the last message 4 of 4. The original v0.2.5 package against the same test: 26 of 28, the Run neighbor gone after its install (the wipe bug reproduced). The VM was deleted. Locally: the give-back helpers 33 of 33 in 5.1 and 7 (scratch key; the user's `DisabledHotkeys` still `V`), every changed script parses in 5.1 and 7, the workflows' YAML parses, the new workflow's version step 12 of 12, `& gui.exe \| Out-Null` waits (2,095 / 2,063 ms). The validator's CPMR0010 matches command tokens only (archived source). The ID is still free; the icon, license, release and source links answer 200 | VM + scratch scripts (`choco-fix/` in session 90da67ad's scratchpad) | ✅. The new workflow first runs after a push |
| `main` pushed (2026-10-03, `e28666b..34b1270`: the README text and photos of another session, then the shortcuts/installer change). The range was scanned first: no secret-like strings, not the user's email, no desktop token, and the three PNGs were checked by eye (invented demo data). CI run 37113200714 passed: 971 = 968 passed + 3 skipped, 0 failed; the x64 package; the Chocolatey smoke test 22 of 22 with the fixed Run-key line. From now on `irm \| iex` from `main` takes over Win+V by default and no longer empties the Run key; `-Hotkey` / `-NoTakeOverWinV` wait for the 0.2.6 release (version gate) | `git push`, `gh run view --log` | ✅ |
| Installer and several shortcuts on a real Windows 11 25H2 (2026-10-03, a fresh `claude-desktops` VM `win-hotkeys`, PowerShell 5.1, the real installer with its lookup and download served from a locally packaged `0.2.6` x64 zip, deleted afterwards). (1) `irm \| iex` form from `C:\nowrite` (deny-write ACE for Everyone): installed, DisabledHotkeys `V`, Explorer restarted, the app logged `Win+V registered with RegisterHotKey`, the prompt back in `C:\nowrite`; a seeded `BC-TEST OtherApp` Run value kept, `Explorer\Advanced` identical to its baseline except `DisabledHotkeys`. Win+V opened the panel. (2) Settings › Shortcut (UIA set-text + Add): `ctrl + alt + f9` → "Ctrl+Alt+F9 · Registered.", `Win+Ctrl+V` → "Owned by Windows or another app: taken over with a keyboard hook"; Ctrl+Win+V (the hook, instead of the sound flyout) and Ctrl+Alt+F9 opened the panel. (3) `-NoTakeOverWinV -Hotkey Win+C,Ctrl+Alt+F9`: V given back and `C` written in one restart; Win+V then opened **Windows' own** clipboard panel and Win+C BetterClipboard's (hooked: Win+C stayed taken with `C` listed). (4) Probe: `CER1` freed Win+E, Win+R, Win+1, not Win+C. (5) `-NoTakeOverWinV -Hotkey Win+E`: Win+E registered directly. (6) Settings › Release from Explorer › Give back (the confirmation named Win+E): before the fix BetterClipboard re-registered Win+E ahead of Explorer; after it, the log showed the hook, and Win+E stayed taken (by Explorer) once BetterClipboard exited. (7) Uninstall through the Installed-apps command: Win+C given back, Run left with `BC-TEST OtherApp`, folder gone, history kept, `Explorer\Advanced` as at the baseline | `desktop_call`: `run_command`, `find_elements`/`element_action`, `press_keys`, screenshots | ✅ after two fixes it found: the installer's `New-Item -Force` on `Explorer\Advanced` ("Attempted to perform an unauthorized operation", first run) and the give-back race |
| The registry wipe (§2.25, §4): `New-Item -Path <existing key> -Force` emptied a scratch key (value and subkey gone) in PowerShell 7.5.8 and 5.1. The installer harness, with a neighbouring Run value and an `Explorer\Advanced` value and subkey seeded, showed the 0.2.5 installer losing the Run neighbour on a plain install and the Advanced ones with `-TakeOverWinV`, and the fixed installer keeping all three in 7 scenarios (PowerShell 7 and 5.1, install, `-NoTakeOverWinV -Hotkey`, uninstall). On this PC, `HKCU\…\Run` held only `BetterClipboard` while `StartupApproved\Run` listed 19 other names (6 enabled) | scratch keys, harness, a read-only listing of value names | ✅ fixed; the user's lost startup entries were reported, not restored |
| README photos (2026-10-03, §3.4): v0.2.5 installed with the README's `irm \| iex` command in a claude-desktops Windows 11 desktop (2560×1440 at 150 %, dark theme): SHA-256 OK, Settings opened. Demo data: `tools/readme/demo-data.ps1` (its guard refused on this PC and wrote nothing; a guard-free test copy with the histories in a scratch folder wrote 7 Claude Code lines, 2 Codex lines, PSReadLine's CRLF file and the notes), then copies from Notepad, Windows Terminal, Explorer, Snipping Tool and Edge, a pin, three groups by drag and drop. Shown as expected: the sources Notepad, Windows Terminal, Windows Explorer, Snipping Tool and Microsoft Edge; a color swatch; "2 files"; a path card; group badges; the Claude tab "6 prompts (7 sent)" with "sent 2 times"; the Codex and Pwsh tabs; the strip's arrows; the Settings chips "8 items · 3.4 MB · 1 pinned · 6 in groups". Captured per window in physical pixels, finished with `finish-shot.py` (the repository's script rebuilds all three photos byte for byte from the raw captures). The panel's first Win+V after a `--background` start left the search box unfocused and dropped the typed "git", 2 of 2 times; the second Win+V took it (§6). Docker Desktop was started for it and is still running; the desktop was deleted with its persist folder | claude-desktops MCP tools (UIA, keys, mouse), in-desktop PowerShell, Pillow | ✅ photos; ❌ the first-summon focus |
| Installer: temp working folder, Win+V taken over by default, `-NoTakeOverWinV`, `-Hotkey`, uninstall giving keys back (§2.25, 2026-10-03). The whole `install.ps1` ran on this PC through the harness (§3.1): scratch registry paths, shortcut and install folder, `Restart-Explorer` counted, the user's app never signalled, a locally packaged `0.2.6` x64 zip served. 18 scenarios, PowerShell 7.5.8 and 5.1: (1) fresh `irm \| iex` from a deny-write folder: V released, 1 restart, `releasedKeys` "V", the caller's location restored; (2) update with V released: no restart; (3) `-NoTakeOverWinV` with `XV`: settings Alt+Win+V, V given back (X kept), 1 restart; (4) `-NoTakeOverWinV -Hotkey 'Win+C,ctrl + alt + f9'`: settings Win+C + Ctrl+Alt+F9, C released and V given back in one restart; (5) `-Hotkey Win+Alt+V`: Win+V + Alt+Win+V; (6) `Win+Foo`: "Shortcuts not accepted", nothing installed, no staging left; (7) `-NoTakeOverWinV -Hotkey 'windows + v'` and (9) both flags: refused before any download; (8) `-Version 0.2.4 -Hotkey`: refused by the version gate; (10, 15) install with Win+C over `Q`, then uninstall: `QVC` → `Q`; (11) 5.1 from the deny-write folder with ``Ctrl+` ``; (12) `-TakeOverWinV` adds Win+V back; (13) 5.1 with a commented settings file: "could not be read", Explorer left alone; (14) 5.1 fresh `iex`; (16) uninstall with a Chocolatey copy: Win+V stays; (17) `-KeepWinVReleased`; (18) 5.1 uninstall giving back V, C (recorded) and 1 (configured). Every run: the user's Run value, Installed-apps entry, `DisabledHotkeys`, shortcut, PATH and app/Explorer PIDs unchanged | `installer_harness.ps1` + `run_scenarios.sh` (session 7579fd35's scratchpad) | ✅ 18/18, after one fix it found: `@()` around a function returning `, $array` nested it (`"releasedKeys": "System.Object[]V"`, §4) |
| The reported failure in a folder the user cannot write to: the 0.2.5 installer (before this change) run from a folder with a deny-write ACE for Everyone, `iex` form, PowerShell 7.5.8 and 5.1: both finished; the 0.2.4 app started from that folder (scoped, background) ran with no warning; `Start-Process` worked from an unwritable and from an unlistable current folder | harness + `launch_from_cwd.ps1`, `cwd_startprocess.ps1` (scratch) | ⚠️ not reproduced: the installer now works from the temp folder anyway (§2.25); the user's error text is needed if it recurs |
| Several shortcuts, unit tests: Core 6 (`HotkeySettingsTests`) and Windows 25 (`HotkeyTakeoverTests`: list parser, the interceptor set with Win+V and Alt+Win+V on one key, `ReleasableKey`, `WithKeys`, every `Plan` state, `--set-hotkeys` validate/save/invalid/too many/app running; `HotkeyServiceTests`: three obscure shortcuts registered for real, one owned by another window refused with 1409, re-apply releases what was dropped; `IsTakenElsewhereAsync` tells a held shortcut from a free one). Full suite 971 = 968 passed + 3 skipped: 7 of 8 full runs green; the one run started right after a build had one Windows-test failure that was not captured and did not recur (6 more Windows-only runs green). No doc warnings on a full rebuild | `dotnet test --solution`, `--no-incremental` build | ✅ |
| Several shortcuts on an isolated copy of the dev build (phase A, nothing on screen but a tray icon): `--validate` refused `Win+Foo` (exit 2, named), `--set-hotkeys` saved three canonical shortcuts keeping the other settings, the log showed Ctrl+Alt+Shift+F21 and F22 registered and Win+C refused (1409: Explorer's, and an isolated instance never hooks), saving refused while that instance ran (exit 3); the user's PIDs unchanged | `e2e/hotkeys_e2e.py` (session 7579fd35's scratchpad) | ✅ 8/8 |
| **v0.2.5 released** (2026-10-03, "bump last time to latest commit and do actual release and push of the new version", §3.1's release procedure). No commit had landed since `c5286e6` (the record of the `64cbff6` install, docs only), so the release is `c5286e6`, and its app code equals the `0.2.5+64cbff6` build installed and running on this PC, so no reinstall. The push range (5 commits) was scanned first: no binaries, no secret-like added lines, not the user's email. `main` pushed (`4e0b1ce..c5286e6`); CI run 37100304903 passed (940 = 937 + 3 skipped, x64 package smoke test, Chocolatey smoke test 22/22). Tag `v0.2.5`: annotated (object `ef329b0` → `c5286e6`), `--cleanup=whitespace`, message = the anchored lookup's draft (`13b4f56`), identical to the notes file (6,903 characters, valid UTF-8). Release run 37100517389 passed in ~5.5 min: the notes step, tests in Release (940 = 937 + 3), both packages, `test-chocolatey.ps1` 22/22, publish; Push to Chocolatey only warned (no secret, §3.2). The page (published 05:44:30Z, Latest, not a prerelease) holds the 2 zips (70.8 / 68.5 MB), 2 `.7z` (43.0 / 39.1 MB), `betterclipboard.0.2.5.nupkg` (82.1 MB), `install.ps1` and `SHA256SUMS.txt`. All four sums match GitHub's asset digests. The body is the notes, then `---` and the Code signing policy footer, with no mojibake. The `install.ps1` asset differs from the blob only by CRLF | `gh api …/releases/tags/v0.2.5`, `gh release download`, `gh run view --log`, a Python comparison of sums, digests, body and installer (scratch) | ✅ released; Chocolatey not pushed by choice |
| `0.2.5` moved to `64cbff6` and installed on this PC a third time (2026-10-02 night, the same request again, §3.1). New since `5cf1eef`: the two image-overlay fixes (`e75b095`: the peek no longer vanishes while the mouse is still, which the user had reported; the viewer's wheel always zooms) and their docs. In a worktree at `64cbff6`: 940 = 937 passed + 3 skipped, 0 failed; `package.ps1` 146 s (zips 71.0 / 68.5 MB); `package-chocolatey.ps1` 82.7 MB. `install-local.ps1` (13 s): SHA-256 OK, the running `0.2.5+5cf1eef` closed gracefully, swapped. Started through Explorer: parent `explorer.exe`, 74 variables without `CLAUDECODE`/`MSYSTEM`. Run value, shortcut, Installed apps (`0.2.5`) and `DisabledHotkeys` (`V`) unchanged; the exe `0.2.5+64cbff6…`. Log after "starting": 0 WRN/ERR. The replaced build logged the Cmd helper's timeout twice, 28 s apart (§6). The v0.2.5 notes draft of `13b4f56` still holds (its overlay text matches the fixed behaviour), so no new draft | as in the first 0.2.5 row below | ✅ |
| `0.2.5` moved to `5cf1eef` and installed on this PC again (2026-10-02 evening, "Move the 0.2.5 to latest changes and reinstall here locally", §3.1). Five commits since the first 0.2.5 install: the image overlays (`516b4ee`, the only app change), then code-signing preparation, the release workflow's notes step, and docs. In a worktree at `5cf1eef`: the full suite 940 = 937 passed + 3 skipped, 0 failed; `package.ps1 -Version 0.2.5` in 163 s (zips 71.0 / 68.5 MB, `.7z` 43.3 / 39.3 MB); `package-chocolatey.ps1` packed 82.7 MB with the nuspec's new Privacy / Code signing policy line. `install-local.ps1` (10 s): SHA-256 OK, the running `0.2.5+ac65856` closed gracefully after ~9 h with no WRN/ERR in its whole run, swapped. Started through Explorer: parent `explorer.exe`, 74 variables without `CLAUDECODE`/`MSYSTEM`. Run value, shortcut, Installed apps (`0.2.5`) and `DisabledHotkeys` (`V`) unchanged; the exe 0.2.5.0 / `0.2.5+5cf1eef…`; `installer.json` re-stamped. Log after "starting": 0 WRN/ERR. The publish still warns only CS0108 (see the first 0.2.5 row). The updated draft notes (the image overlays added, a Privacy link) are in the commit that records this | as in the first 0.2.5 row below | ✅ (the overlays themselves are still unchecked on screen: their row) |
| `main` pushed (2026-10-02, `8b65b19..543d2fd`, 34 commits, scanned for secret-like strings first: none): CI run 37007600619 passed. 940 tests, 937 passed + 3 skipped, 0 failed; the x64 package smoke test; and the Chocolatey smoke test's first GitHub run, 22 of 22 (§3.2). The five release pages v0.1.0–v0.2.3 were set to their tag annotations plus the "Code signing policy" footer (`gh release edit`) and read back through the API: 5 of 5 identical, footer present, no mojibake; titles and Latest unchanged; old bodies backed up | `git push`, `gh run view --log`, `gh release edit`, `gh api …/releases/tags/<tag>` | ✅ |
| Code signing readiness (§3.3, 2026-10-02): SignPath Foundation's terms read in full, and the form's 16 fields from its `OSSRequestForm-v4.xlsx`. Version resources and Authenticode signers read from the 0.2.4 release folder (257 PE files: our six agree on `ProductName` BetterClipboard and `ProductVersion` `0.2.4+98addc6…`; 247 others signed by .NET / Microsoft / the .NET Foundation; unsigned: SQLite3MC and three SQLitePCLRaw files) and from the 0.2.5 Debug build (adds unsigned ZstdSharp). `src/` searched for network APIs: none. The release-notes step taken out of `release.yml` and run in a scratch clone whose `v0.2.3` was forced lightweight (as checkout@v5 leaves it) with origin on GitHub: the tag came back annotated, and the notes were the annotation plus the footer, with "—", "…", "›" intact under a 437 console. A tag missing on the remote failed the step (exit 1). The YAML parsed, and `Publish release` reads `steps.notes.outputs.file`. Reference sources: gh 2.85's `gitTagInfo`; checkout's `testRef` without `^{commit}` in v5 and with it in v6/v7 (`de0fac2`, #2356). The application itself not submitted | scratch `signpath/` (`peinfo.ps1`, `signers.ps1`, `notes-step/`), `gh api`, Brave search | ✅ prepared; the workflow change runs for real at the next tag |
| Image overlays (§2.24, 2026-10-02): the hover peek and the eye-icon zoom/pan viewer, both monitor-wide windowed popups. `ImagePreviewLayout` unit-tested (12: fit-down, no-upscale, upscale, empty-input guards, fit zoom + fallback, clamp with reversed bounds and NaN, zoom-toward-point anchoring, zero-old-zoom guard); the solution builds with 0 doc warnings in Core/Windows/App; the full Core suite 729 pass. The glyphs E7B3 (eye) and E711 (close) were rendered from Segoe Fluent Icons and confirmed. **Live on win-1** (a `claude-desktops` Windows 11 Pro 25H2 VM, 1600×900): a scoped self-contained build with a seeded 1800×1100 image — eye button renders; peek appears on rest and stays while the cursor is still (two shots 1.5 s apart, unmoved) then hides on a real move (the user's bug, fixed via position-based dismissal); viewer opens; wheel zooms in/out at every level incl. while zoomed in (70→145→300→120 %, toward the cursor — a second bug, the wheel panning once zoomed in, fixed by handling it on the image); drag-pans; closes by Esc, the X and click-outside. Fixes `e75b095` | `dotnet build` + Core test exe + PIL glyph render + a manual QA pass on win-1 via `desktop_call` (move_mouse/scroll/drag/find_elements/screenshot), recording at the VM's `/persist/recordings/bc-image-overlays-qa.mp4` | ✅ build + unit + live Windows QA (peek bug and a wheel bug found and fixed on the VM) |
| `0.2.5` installed on this PC as the user's app before its release (2026-10-02, "bump version, preparing for new release, install locally here first (as release, not dev)", §3.1). Bump `ac65856`: 0.2.4 was never tagged, so 0.2.5 carries it, and that commit's message holds the drafted tag notes. In a worktree at `ac65856`: the full suite 928 = 925 passed + 3 skipped, 0 failed; `package.ps1 -Version 0.2.5` in 124 s (zips 70.9 / 68.5 MB, `.7z` 43.3 / 39.3 MB for x64 / ARM64); `package-chocolatey.ps1` packed `betterclipboard.0.2.5.nupkg`, 82.7 MB. `install-local.ps1` (6 s) verified the SHA-256, closed the running `0.2.4-dev.74594fb` gracefully ("Exiting." in its log) and swapped. Started through Explorer (`--background`): parent `explorer.exe`, 74 environment variables without `CLAUDECODE`/`MSYSTEM` (a process of this session: 142, both present). Run value and shortcut unchanged; Installed apps and `installer.json` say `0.2.5`; the exe 0.2.5.0 / `0.2.5+ac65856…`; `DisabledHotkeys` still `V`. Log after "starting": 0 WRN/ERR (store opened, every integration started, Windows import 0 new of 27, Win+V by `RegisterHotKey`). The dev build's ~15 h before it logged two warnings, both from the Cmd tab's helper (§6). The Release publish warns CS0108 (`SettingsWindow.Visible(bool)` hides `Window.Visible`), as it has since `2d822b9` (0.2.3) | worktree + `dotnet test --solution` + `package.ps1` + `package-chocolatey.ps1` (Chocolatey CLI 2.3.0, pack only) + `install-local.ps1` (Windows PowerShell 5.1, env stripped, Windows paths) + the dev install's scratch `launch_background.ps1`, `install_state.ps1` (before/after), `env_names.py` | ✅ |
| Resizable panel and tab carousel, live (2026-10-01, §2.23). Isolated copy of the dev build next to the user's app: own data dir, capture paused, `PasteOnSelect` off, ShareX / Screenshots / PSReadLine / Claude / Codex folders, the Win+R key and the Everything instance all pointed at scratch or BC-TEST data, eleven tabs. Opens at 400 × 560 with only "›"; WM_NCHITTEST LEFT / RIGHT / BOTTOM in the invisible frame, BOTTOMRIGHT at the corner, TOP on the top pixel row inside. "›" takes exactly one press per hidden tab (5), the last tab whole at the end; "‹" walks back (4) and hides. Two wheel notches scroll 120.0 DIPs, a tilt 60.0, a left drag on a tab 150.0 and a middle drag 100.0 DIPs with the pointer, picking no tab and not moving the window; a plain click still picks a tab. The right border +160 px and the top edge −120 px resize and are saved (560 × 560, then 560 × 680 DIPs); dragging the right border −900 px stops at 360 DIPs (saved 360). At 760 DIPs every tab fits: no arrows, nothing to scroll, and the strip's empty space moves the window again. Esc, re-summon: 760 × 680. The groups column grows the window 44 px on the left and closing it gives the same window back, the remembered size unchanged. 0 WRN/ERR in the test log; the user's PIDs unchanged; scratch removed | UIA invokes on the arrows and the groups toggle; real mouse input only after `WindowFromPoint` → `GA_ROOT` was the test window, stopped by any cursor movement of the user's (the run that hit the screen's bottom edge stopped there: the drag now picks the edge with room); Esc only while the panel was in front; `PrintWindow` captures; summoned only after the terminal was in front twice with 3 s of no input (`carousel_e2e.py`, scratch) | ✅ 35/35 (run 3). Runs 1–2 found the virtualized tabs (9 of 11 realized, 15 presses to the end, a wheel turn to 87 %; fixed by `HorizontalCacheLength`), and run 3's captures showed the tabs a drag started on staying drawn pressed. Added after run 3, not yet re-checked live (runs 4–8 never got a quiet moment at the terminal: the user was in a browser; the harness only summons over an idle terminal): the capture hand-over through the tab (with its fallback), the clip cache, and two new checks — the dragged-from tabs look untouched afterwards (label pixels), and a summon after leaving the strip scrolled starts at "All". Re-run: `python carousel_e2e.py <minutes to wait>` from `e2e/` in session 96d4ffaa's scratchpad (with `e2e_base.py`) |
| `0.2.4-dev.74594fb` installed on this PC as the user's app (2026-10-01, "Build and install dev", §3.1): x64 zip built from a worktree at `74594fb` (`main`, every feature through the Snipping tab), 70.9 MB. The side-by-side dev copy was closed first (scoped `--exit`). `install.ps1` verified the SHA-256, closed the running 0.2.4 gracefully and swapped. Started through Explorer (`--background`): parent `explorer.exe`, 74 environment variables without `CLAUDECODE`/`MSYSTEM` (a process of this session: 142, both present). Run value and shortcut unchanged, Installed apps and `installer.json` say `0.2.4-dev.74594fb`, `DisabledHotkeys` still `V`. Log after "starting": 0 WRN/ERR. First imports on the real data: Claude Code 17,198 prompts in 13.5 s, Codex 2,279 prompts from 720 files (417 left out) in 21.2 s, Win+R 24 new of 26, 1 new Windows item; the Screenshots folder and ShareX watched; Win+V by `RegisterHotKey` | worktree + `package.ps1` + `install-local.ps1` (Windows PowerShell 5.1, env stripped) + scratch `launch_background.ps1`, `install_state.ps1` (before/after), `env_names.py` (variable names from the PEB, checked against a control) | ✅. The first `install-local.ps1` run stopped at parameter binding (forward-slash path, §3.1) before changing anything |
| Snipping tab on screen (2026-10-01). Isolated copy of the dev build, three BC-TEST screenshots imported with capture on, then the instance restarted paused before any UI. The panel opened only after the terminal was in front for two checks with 3 s without input. The tabs read All … Files, ShareX, Snipping, Run (the fake ShareX folder counts as installed), all inside the window. The Snipping tab was selected through UIA and holds 3 cards captioned "Screenshots folder · just now · 300 × 300", "Win+PrtScn · just now · 640 × 200" and "Snipping Tool · just now · 480 × 270", with thumbnails. Settings: the card (scissors glyph, header, switch on) and its status "Watching …\Screenshots." + "Snipping Tool 11.2607.23.0 saves every snip here, and copies it to the clipboard (both become one entry).", read from the real Snipping Tool's settings. The user's PIDs were unchanged, and the scratch was removed | UIA (SelectionItemPattern, texts) + PrintWindow captures of the test windows only (`snip_e2e/ui.py`, scratch) | ✅ 10/10, then 5/5 Settings-only. One earlier run found no panel within 10 s; it did not recur in the next two |
| Snipping tab, headless (2026-10-01). Isolated copy, with overrides for every source. Three screenshots were listed by `bclip list -f snipping` 401–434 ms after their files were written (polling included), as origin `screenshot` with sources Snipping Tool / Win+PrtScn / Screenshots folder and PNG + CF_DIBV5. Not imported: an archive file from before the first activation, a file copied in with a 2020 write time (logged as skipped), and a `.txt`. A screenshot written while the instance was stopped arrived at its restart, logged as "Imported 1 screenshot saved while BetterClipboard was not watching". The log had 0 WRN/ERR, and the user's PIDs were unchanged | `snip_e2e/run.py` (scratch) | ✅ 16/16 |
| Snipping tab tests: Core 34 (name shapes incl. a Hebrew name with U+200F marks, Chinese without a space, and the clash suffix; images; sources; freshness; completeness per format; Snipping Tool's settings; the filter; the hybrid merge rules; pause and ignored apps; the relabel fix-up; CLI; the setting) and Windows 22 (each tool's name, a writer reopening its file while the watcher polls, files copied or moved in, skips, renames, catch-up, a folder created later, the marker's life cycle, the clipboard copy and the file merging for a DIBV5 and a zero-alpha BI_RGB DIB, the locator, the display-name rule) | tests; the new Windows classes 6 times in a row | ✅ all green, 6/6 repeats; full suite 912 pass + 3 skipped (915 = Core 707 + Windows 208) |
| Chocolatey package, the real 0.2.4 payload (2026-10-01): installed, upgraded to `0.2.4.1` with a scoped test instance running from the package, and uninstalled in a private Chocolatey root (a copy of `choco.exe` 2.3.0, `ChocolateyInstall` set for the process only), not elevated, next to the user's `install.ps1` install with Win+V released. The test instance had capture paused, no imports, and the Win+R, ShareX and Everything overrides | `real-package-test.ps1` (scratch), backups of the user's shortcut and Run value with restore-on-change | ✅ 28/28. Two shims and three markers; `bclip 0.2.4` through the shim; the user's shortcut and Run value not taken over; the test instance closed by before-modify; no `lib-bkp`; the state file consumed; the package folder gone after the uninstall (`/KeepWinVReleased`). The user's shortcut, Run value, `DisabledHotkeys` and PIDs were unchanged. Not covered here (CI on the next push): the admin install, the Win+V restore, the graceful `--exit`, the relaunch |
| Chocolatey mechanics (2026-10-01): shims, `.ignore`/`.gui`, ARM64 seen as 32-bit, upgrades with a running app, files written after install, uninstall leftovers | [`probe_chocolatey.ps1`](tools/probes/probe_chocolatey.ps1), private root, BC-TEST package | ✅ 28/28 on Chocolatey CLI 2.3.0. One hypothesis was corrected by it: upgrades do not leave files from older versions |
| Claude and Codex tabs on an isolated instance next to the user's app (2026-10-01, §2.21; fake agent folders through `BETTERCLIPBOARD_CLAUDE_DIR` / `_CODEX_DIR` with BC-TEST prompts only, capture paused, `PasteOnSelect` off, the other integrations off). The first import stored 6 Claude Code and 2 Codex sends: a subagent thread's prompt left out, a CLI-history line merged into its session record. The Claude tab lists 5 cards, newest first, one per text: the image prompt ("· 1 image"), "BC-TEST continue" ("sent 2 times", the project of its last send), the slash command labeled "Claude Code slash command", the expanded paste on two lines, the oldest. Footer "5 prompts (6 sent)", key hint "↵ paste · Ctrl+P pin · Del delete", placeholder "Search prompts you sent to Claude Code…". The Codex tab: 2 cards, footer "2 prompts" (twin merged; unmerged would read "(3 sent)"), caption "Codex · gamma · 10 min ago · 1 image". A line appended while paused is not shown. Window 510 px, every tab label whole ("Codex" last), the Message glyph on every card; the ShareX tab showed because ShareX is installed here (its import was off). User's PIDs unchanged, scratch removed | UIA select-only on the tabs, no injected input. The panel was summoned only after the terminal had been in front for two checks with no user input for 3 s (§4). Two `PrintWindow` captures of the test window (`prompts_ui.py`, scratch) | ✅ 15/15. The two runs before it stopped at bugs in the script, not the app: a generic UIA `Control` has no `GetSelectionItemPattern`, and GDI handles passed without `ctypes` prototypes overflowed. An earlier attempt skipped after 10 minutes because its terminal list lacked the user's terminal (§4) |
| Prompt archive on this PC's real Claude Code and Codex folders (2026-10-01, §2.21): a copy of the dev build as an isolated instance (own data dir; capture paused; Windows import, ShareX, Win+R, Everything and the shell tabs off), next to the user's two instances. First imports, both at once in background mode: Claude Code 17,170 prompts in 8.6 s, Codex 2,279 prompts from 720 files (417 left out: agents and exec runs) in 18.6 s. `bclip status`: 19,449 prompts, 0 history items. `bclip prompts -a claude/codex`, `--all`, `prompt ID -o FILE` (exact bytes), a search with no match (exit 1). 0 WRN/ERR; the user's PIDs unchanged; scratch removed. Only counts were printed | scratch `prompts_e2e.ps1` | ✅ |
| Prompt archive engine at scale (same data, a throwaway store in a scratch probe): 17,169 Claude Code prompts = the 17,370 lines then minus 201 duplicates; restart catch-up 0.5 s wall / 0.02 s CPU; listing 8–10 ms (first page, page 50, a word), a regular expression ~0.25 s; store with FTS ~69 MB | scratch `archive_probe.cs` | ✅ |
| Prompt archive tests: 88 new (Core 77, Windows 11 on temp agent folders); the Windows class runs in ~5.3 s (45 s before the missing first wake-up was fixed, §4) | tests | ✅ 3 of 3 repeated runs |
| NTFS change notifications vs a long-lived writer (no events, size listed 0 until the close; a reader's open syncs it) | `tools/probes/probe_append_notify.cs` | ✅ reproduced twice |
| Pwsh and Cmd tabs (2026-10-01, §2.19): the real helper (`BetterClipboard.exe --read-console-history <pid>` of the dev build) read 3 BC-TEST commands in order from a hidden pseudoconsole cmd, 59–61 ms per read warm (206 ms cold), and exited 3 for a process without a console; the PowerShell source read BC-TEST files of two hosts (merged, newest file first, counts added) incl. one held open for writing; the dev copy started its Cmd reading with 0 WRN/ERR next to the user's app | Core + Windows tests (38), scratch `verify_cmd_helper.cs` (built from the probe's pseudoconsole code), `tools/launch_dev.py` | ✅ (the panel itself not checked with UIA yet: the foreground was never a terminal) |
| Several groups at once, live on an isolated instance next to the user's app (2026-10-01; seven BC-TEST items in Work / Home / Ideas seeded through `MachineBoundHistory`, capture paused, `PasteOnSelect` off). A plain click shows Work (3 cards, "3 in Work"). Ctrl+click Home merges them: 5 cards newest first with the shared card once, header "› Work + Home", footer "5 in Work + Home", both icons' `ItemStatus` "Shown". The search "report" keeps the 3 matching cards of both groups; "zzz-none" shows "Nothing in Work or Home contains “zzz-none”.". Shift+click Ideas shows the run from the anchor (Home + Ideas, 4 cards); Ctrl+Shift+click Work adds Work..Home (all three, 6). The icon menu's *Remove from view* takes Ideas out; the card menu's *Remove from Work and Home* takes the shared card out of the view ("4 in Work + Home"). A plain click on Work shows it alone (2), again: everything (7). 0 WRN/ERR; the user's PIDs unchanged | UIA reads and plain invokes; real Ctrl / Shift / Ctrl+Shift clicks and two right-clicks only after checking that the test panel is in front and owns the point; cards never invoked (`groups_e2e/run.py`, scratch) | ✅ 11/11 on the first run, on the shared tree's build (the same feature code, without the header tooltip). Its screenshot showed the header trimmed to "Work +…" next to the Paused chip, hence the title's tooltip. The exact commit's build (a separate worktree) and the tooltip's hover check were not run live: three waits for the terminal to be in front skipped (the user was away with another app in front) |
| Everything tab on an isolated instance next to the user's app (2026-10-01): a private, windowless Everything 1.5.0.1423b (`BCTEST-E2E`, only a BC-TEST tree, four picks through the run-count IPC), the store seeded with BC-TEST items, overrides for Everything, ShareX and Win+R. The app verified the instance ("signed by voidtools PTY LTD"). All nine tabs fit: window 518 px outer / 504 visible, "Everything" 83 px with 13 to spare. The tab lists the four picks newest first ("File/Folder opened in Everything", "opened 3 times"), then the path copied in Everything; footer "4 opened in Everything · 1 kept". Ctrl+P on a pick: a pinned history entry in its place (`bclip`: `files everything pinned Everything`). Delete on a pick: hidden, also after reopening the panel. User's PIDs unchanged, scratch removed | UIA (select-only) + Ctrl+P/Delete/Esc sent only while the test panel was in front + two guarded screenshots (`ev_e2e/run.sh`, scratch) | ✅ after two fixes it found: the tab bar ignored the window frame ("Everythin", fixed by `WindowFrameDip`), and the first run's helper invoked a card (it pasted a BC-TEST file reference into the user's clipboard and terminal; select-only since, §4) |
| Everything against real builds: live picks (run counts, dates, newest first, search words), then the saved `Run History.csv` after the instance exited, all through the real owner check | `RealEverythingTests` with `BETTERCLIPBOARD_EVERYTHING_EXE`, one private instance per run | ✅ 1.4.1.935, 1005, 1026, 1032 and 1.5.0.1423b. 1.4 needs a save for the file (no search window ever opened; see §2.14 "Run history saving") |
| Everything unit tests: wire format (query and command-line encoding, `LIST2` field order, every field skipped by size, lying sizes refused, unset dates), queries and quoting (`CommandLineToArgvW` round trip), `Run History.csv` (format, header order, odd rows, a write cut off mid-path, candidate paths), merge and hide rules, the store filter/origin/forget-by-path and pick formats, CLI and setting; the client against a fake IPC window (trust, state, matching, latest-wins, deadlines, garbage, a hung window), the integration (live, saved file, loading, garbled, impostor), owner check, locator | tests | ✅ 55 new (Core 29, Windows 26). The locator test found a real bug: a quoted display icon with an icon index (`"…\Everything.exe",0`) was unquoted before the index was cut and never matched |
| Search toggles, live on an isolated instance next to the user's app (2026-10-01; seven seeded BC-TEST items, capture paused). The box reads `[text][X][Aa][W][.*][🔍]`; on = accent outline and glyph (dark and light theme). Through UIA: "log" lists 3 cards, with W 1 ("log file"; not "login form" or "my_log entry"); "Hello" with W lists 2, adding Aa leaves "Hello World"; the regex `\d{3}-\d{4}$` keeps "order 555-1234" and drops "call me at 555-1234 now"; "foo(" shows "Not a valid regular expression" with "Not enough )'s. Turn off .* (Alt+E) …". Alt+E / Alt+C / Alt+W flip the toggles, and the empty state then reads "… contains “foo(” (whole words)." `settings.json` keeps the toggles, the next summon restores them and clears the text, and a toggle with an empty box leaves the list alone. 0 WRN/ERR in the test log; the user's PID unchanged | UIA (Toggle/Value patterns) + guarded Alt chords and Esc, only while the test panel was in front (`search_e2e/run.py`, scratch) | ✅ 17 + 3 checks, on the first build and again after rebasing onto `f1fb074`. Alt+R never arrived (§4, global hotkeys), hence Alt+E. A WinEvent hook saw no `EVENT_SYSTEM_SOUND` for any chord, not even an unhandled Alt+Q control, so no "ding"; the hook itself was not proven against an audible beep |
| Red outline on a regex error, live on an isolated instance (2026-10-01; eight seeded BC-TEST items, one of them 40 × "a" for a timeout). The ".*" toggle's outline, sampled from screen pixels inside its UIA rectangle: "foo(" red, "foo(b" still red, `foo\(` accent, `(?=a)(a+)+b` (too slow) red, toggle off with "foo(" no outline (the box's background), on again red, the next summon accent. The Aa and W toggles stay without an outline. 0 WRN/ERR, the user's PIDs unchanged | UIA (Toggle/Value patterns, no injected input except the closing Esc while the test panel was in front) + pixel sampling (`search_e2e/outline.py`, scratch) | ✅ 10/10 in the dark theme (red #FF99A4, accent #4CC2FF) and 10/10 in the light theme (red #C42B1C, accent #0067C0). The first build failed "toggle off": an outline hidden by a state setter came back on a style swap (§4) |
| Search toggles in Core: matcher semantics (case, VS Code's whole-word rule with punctuation edges, overlaps and runes; regex lines, engines and timeout; empty texts), the prefilter superset, the SQL function inside real queries (order, paging, filters, groups), failures keeping their type through SQLite, persisted toggles; cost on 10,000 encrypted synthetic items: 1–55 ms, a full regex scan ~46 ms | tests (`SearchOptionsTests.cs`, 28) + scratch probes (`probe_regex.cs`, `probe_search_perf.cs`) | ✅ |
| Run tab, headless, on an isolated instance next to the user's app (2026-10-01; a scratch Win+R key with BC-TEST commands through `BETTERCLIPBOARD_RUNMRU_KEY`, capture not paused, the scratch store deleted afterwards). The first activation imported 3 of 3 (origin `run-history`, newest first). A live run was listed by `bclip list -f run` 295–703 ms after the write (polling included), origin `run`. A re-run moved to the top. 30 runs 400 ms apart: Windows' list held 26, the Run tab all 34, including the evicted "fill 01". JSON has `lastRun`, source `Win+R`. User's PID unchanged | `run_e2e/run.sh` (scratch) | ✅ (30 runs 50 ms apart were one batch: 26 of 30, the oldest 4 evicted before the first read — a scripted-burst limit, §6) |
| Run tab UI on the same kind of instance. The tab shows after ShareX. Entering it lists "Win+R command: …" cards (command-prompt glyph, "Win+R · just now"); the footer reads "↵ paste · Ctrl+↵ run · Ctrl+⇧↵ run as admin" and "34 commands kept". Ctrl+Enter, sent only while the test panel was the foreground window, on a `wscript` BC-TEST command ran it (marker file written), hid the panel, and moved the entry to the top with a new run time. Log: "Ran entry 3 as a Win+R command: Started." (no command text) | UIA select + one guarded key chord (`run_e2e/ui.sh`) | ✅ (twice). Lesson: take the test PID from `Start-Process -PassThru` — "the new `BetterClipboard.exe`" was once another agent's short-lived process, and UIA then found "no window" |
| Tab bar with the ShareX and Run tabs (8 tabs) | UIA rects + guarded screenshots; the committed build measured from a separate `git worktree` (never stash/checkout in the shared tree) | ❌ in `4c91613`: "Run" is clipped to "Rur" (window 405 px, the tab arranged at 31 of its 43 px), on the first and the second summon. Cause: the widening ignored the window's 14-DIP invisible frame (`MoveAndResize` sizes the outer window). The Everything session's change that subtracts it (`WindowFrameDip`, uncommitted in the working tree after `4c91613`) measured fine: window 421 px, "Run" 43 px, ~4 px to spare. ✅ since that change was committed (the commit after `3c82d3b`); with all nine tabs see the Everything rows |
| Win+R on the real list | the user's `launch_dev.py` copy (§3) | ✅ "Win+R: imported 26 new of 26" |
| Run-tab unit tests: `RunMru` (parse, known answers, runs-since theory, snapshot, plan), store (run column, Run filter, merge rules, tombstones, older store), service (pause, ignore, Forget forever, CLI), Windows (reader, settle wait, watch, integration on scratch keys, launcher parsing + hidden launches) | tests | ✅ 51 new. The Windows run-history classes passed 8 of 8 repeated runs once each scratch key had its own parent; with a shared parent the watch test failed 6 of 6 (§4) |
| Paths copied as text in the Files tab, live on an isolated instance next to the user's app (2026-10-01; nine seeded BC-TEST items, capture paused). `bclip list -f files` lists exactly the five path texts (kind `path`, JSON `paths` 1/1/2/1/1) and the file list. `-f text` still lists the path texts, but not the file list. In the panel, selecting Files (UIA) shows the same six cards ("Path: …", "Paths: …", "Files: …"). The prose, `and/or` and `./venv/bin/pip install -r …` cards are absent. Cards show the folder glyph, "path"/"2 paths" and a monospace body. User's PID unchanged | seeded through `MachineBoundHistory` (no clipboard), `bclip`, UIA select + read, one guarded screenshot (`files_e2e/run.sh`, scratch) | ✅ (the Files tab's tooltip is not exposed to UIA, so it was not checked) |
| Path detector precision: 951,234 lines of real text, each line and each adjacent pair, gave 60 hits, all paths or `@`-file references; the corpus details are in §2.13 | scratch probe (`probe_corpus.cs`) | ✅ (after fixing what its first pass found) |
| Groups toggle icon centered (2026-10-01): the ribbon's margins in its 34×32 button are left/right 12/12 (before: 14/10) and top/bottom 9.5/≈10 (before: 10.5/≈9), with the column closed (outline) and open (filled, on its highlight); user's PID unchanged | guarded screenshots of an isolated instance (`--show-flyout`, UIA invokes of the toggle, no injected input), ink edges measured with sub-pixel coverage, before = the 2026-09-25 groups e2e screenshots of the same markup | ✅ (vertical rest ≈ −0.2 px: the notch tips' faint antialiasing; geometrically −⅛ px) |
| Forget forever, live on an isolated instance next to the user's app: the card menu ends with *Forget forever…*; its confirmation ("Forget this forever?") deletes the card **and** its CRLF look-alike (log "2 history entries deleted"); `bclip status` prints "Forgotten: 1 item never recorded"; Settings › Forgotten forever shows "Text · 20 characters / From Notepad · forgotten just now · not copied since"; *Allow again* empties the list ("Nothing is forgotten…", *Allow all again…* disabled); user's PID unchanged | UIA invokes + one guarded right-click + UIA `ScrollPattern` to the Settings card (`groups_e2e/run5.sh`, scratch) | ✅ first attempt |
| Forget forever end to end in the private window station: after `bclip forget`, another program copying the same text with a trailing CRLF is read by the real listener and kept out (counted once), the next different copy is recorded | `CliEndToEndTests.Forget_KeepsRealCopiesOut` | ✅ 8/8 repeated runs |
| Forget forever in Core: normalization (CRLF, NBSP, U+3000 and U+2029 trimmed; case and inner text kept), known answers, chunk boundaries inside surrogate pairs, SQL trim set = .NET whitespace; look-alike sweep removes exactly the variants; clears/prune keep the list; first entry wins; counters only for new copies; images by pixels; worker order; allow again; CLI grammar/processor/status | tests (`ForgetTests`, CLI tests) | ✅ |
| Groups column, live on an isolated instance next to the user's app. The column opens 44 px to the left (right edge unchanged) and closing shrinks it back (444 → 400 px, remembered). The icon picker creates a group. A mouse drag of a card onto the icon adds it (log "Dropped 1 card(s) on group 1", button "…group, 1 item", badge on the card). The group view shows only its card (header "Clipboard › Name" with the chip, placeholder, footer "1 in Name"). Right-click → *Remove from Name* empties the view, and the logo shows all cards again. Rename through the icon menu works. User's PID unchanged every run | UI Automation (invoke/select/value) + guarded mouse and keys (`groups_e2e` scratch scripts) | ✅ |
| Keys inside popups: Esc on a card menu closes only the menu (before the fix the whole panel closed); Delete in the rename box edits the name and deletes no card; Esc on the search box's own menu keeps the panel open; the Menu key opens the card menu, where it used to open a lone "Paste" menu, and a right-click in the search box afterwards still gets the text box's menu | same run, guarded keys + content-free diagnostic log lines (removed afterwards) | ✅ (Enter in the rename box was not pressed: had the guard failed, it would have pasted through the real clipboard; it takes the same guard path as Delete) |
| Groups store and service: pinned-or-grouped protected from age/count/size retention and from Clear; Clear all keeps the groups (empty); the retention clock resets on leaving the last group (also when a group is deleted) without moving the item in the list; memberships cascade; events; icon catalog glyphs valid and unique | tests (`GroupTests`, `Positioner_GroupsColumnGrowsAndShrinksOnTheLeft`) | ✅ |
| ShareX, headless, dev build next to the user's app (fake ShareX folder, isolated instance, `bclip`): 2-hour-old archive file not imported on first activation; a new screenshot listed ~0.8 s after the write (bclip polling included) with origin `sharex`, source ShareX; thumbnail, `.txt` and a folder outside `%y-%mo` skipped; `bclip get -o` byte-identical to the saved PNG; a screenshot saved while the app was stopped imported on restart (catch-up logged); user's PID unchanged | `sharex_e2e.sh` (scratch) | ✅ |
| ShareX tab: all 7 tabs fit (UIA: tab 61 px, 28 px to spare) and filter to the 2 screenshots; Settings › Integrations › ShareX screenshots card shows found-via + watched folder | UI Automation + guarded screenshots of the isolated instance | ✅ (after the 9 px padding fix; before it the tab read "Shar") |
| ShareX pattern rules, locator precedence/configs/overrides, watcher (one import per save, writer still open, skip rules, recordings handled, catch-up cap, folder created later), marker life cycle | tests | ✅ |
| Unit tests | `dotnet test --solution` | With the image overlays (§2.24, 2026-10-02): 940 = Core 729 + Windows 211 (Core 12 new, `ImagePreviewLayout`). The Core test exe ran green (729); the solution builds clean and the App compiles, but the Windows suite — unchanged by this change — was not re-run this session, so its 211 is carried over. Before it: 925 pass + 3 skipped (the opt-in real-clipboard and real-Everything tests, the explicit measurement) locally (2026-10-01, non-elevated): 928 = Core 717 + Windows 211, with the tab carousel and the resizable panel (§2.23: Core 10, Windows 3; each test project's executable run whole). Before it, 912 pass + 3 skipped: 915 = Core 707 + Windows 208, with the Snipping tab (Core 34, Windows 22). Before it, 856 pass + 3 skipped: 859 = Core 673 + Windows 186, with the prompt archive (`7cb8ab2`: 88 new tests, Core 77 + Windows 11, plus 3 new cases of existing theories), in the shared tree; one full run before it had `ClientHangUp_CancelsHandler` exceed its wait again (3 of 3 alone passed). Before it, 765 pass + 3 skipped: 768 = Core 593 + Windows 175, with the Pwsh and Cmd tabs (`ec4db1f`: Core 32, Windows 6, not yet counted in its docs) and the Third party catalog (Core 29), built and run in a separate worktree holding exactly `ec4db1f` + the Third party change; before them, 701 = Core 532 + Windows 169, with merged group views (built and run in a separate worktree holding only that change, while another session's half-done work kept the shared tree from building); before them, 691 = Core 522 + Windows 169, with the Run tab, the finished Everything tab and the search toggles (`a0c6c81`, built and run in a separate worktree); before the search toggles, 663 (Core 494) with 5 of 5 full runs green; one earlier full run right after a build failed `QuickSuccessiveCopies_AreAllCaptured` once (5.5 s under load; 5 of 5 green alone). Core alone: 0 of 30 runs failed after making it run one class at a time; before, 4 of 25 failed with a pooled-connection `ObjectDisposedException` (§4). Earlier: CI (elevated runner) green; one-off `ClientHangUp_CancelsHandler` exceeded its 5 s wait once in a full run right after a build (0 of 30 isolated and 0 of 6 further full runs failed) |
| Settings › Shortcut box shows the saved shortcut (custom and preset); preset menu saves; invalid text shows the error and saves nothing; typed text saved canonically; menu labels canonical | screenshots + guarded input on an isolated instance | ✅ (fixed after v0.2.0, where the box was blank) |
| Drag the flyout background to move it: header drag moves exactly (120, 60); no sticking after release; search-box drag doesn't move; Esc mid-drag restores and keeps it open | `tools/e2e/drag.py`, isolated instance, mouse | ✅ 4/4 checks, 4 consecutive runs (touch/pen untested) |
| Password-manager catalog: names normalized + unique, fresh/existing settings seeded, user entries kept (`keepass.EXE` covers `KeePass`), deletions stick, later catalog names arrive once, `settings.json` round trip | tests | ✅ |
| Settings › Ignored apps: scrollable list + *Add known password managers* | XAML builds | ⚠️ not visually verified (opening Settings would steal the user's focus) |
| Settings › Third party (2026-10-01, §2.20): the catalog tests (9 tests, 29 cases: link wording, the official-link rule, every package in `Directory.Packages.props` and in the App's and `bclip`'s restore output credited, both directions of agreement with `THIRD-PARTY-NOTICES.md`; a mutation that dropped WebView2's credit failed); the whole suite on exactly `ec4db1f` + this change in a separate worktree (768 = Core 593 + Windows 175, 3 skipped, 0 failed); all 14 links answered 200. Live on an isolated instance next to the user's app (a copy of the dev build; capture paused, imports and the shell tabs off; ShareX, Win+R and Everything overrides; started only after the terminal had been in front for two checks): UIA read the section header, both card headers and the 14 links in order with their project-first names ("Everything: voidtools.com" … "Windows SDK: learn.microsoft.com", "License notices" between the two lists) and the credit lines ("ShareX  ShareX team · separate app", ".NET  Microsoft · MIT"). Window captures in the dark and light themes show every row (name + credit, use line, site link with `E8A7`, dividers), the Works with description, and *License notices* in the Built with card's header; 0 WRN/ERR in the test log; the installed app's PID unchanged. No link was invoked: it would open the user's browser | tests + worktree + scratch scripts: `check_urls.py` (links), `third_party_e2e.py` (UIA, `PrintWindow` captures) | ✅. Two lessons (§4): the first captures, taken as screen regions, showed the user's terminal instead of the test window although it was the foreground window (deleted at once; now `PrintWindow`); and UIA's rectangle of the Settings `ScrollViewer` is not its viewport, so the script's "last link visible" check proved nothing — the captures did. The "Third party" header line itself is in UIA only, not in a capture |
| `bclip` published build next to the user's running app: status (auto-starts the scoped instance), list, search, grep, get (exact bytes, Hebrew/✓, HTML fragment, file list), image → exit 2 without `-o` / PNG with `-o`, `--json`, pin + pinned filter, `--since`, wait timeout (1), not found (1), usage (2), access off (3, starts nothing); audit log; no LL hook; user's PID unchanged | isolated seeded store + `run.sh` | ✅ (after the ShellExecute fix) |
| `put`/`copy` write the clipboard, `wait` sees the next copy | CLI end-to-end tests in the isolated window station | ✅ |
| `install.ps1 -AddToPath` / uninstall PATH helpers | AST-loaded functions on a scratch key, PS 5.1 + 7 | ✅ (15/15) |
| "Windows clipboard history" source label gone (importer, caption, one-time fix-up of v0.1.0 rows) | tests | ✅ (the running v0.1.0 install still shows it until updated) |
| Encryption at rest: no plaintext in db/WAL, wrong key ⇒ unreadable, in-place migration, quarantine | tests + real app on a copy of a v0.1 dir | ✅ (0 of 16 entries lost) |
| Capture: 25 copies 20 ms apart all captured; bursts fully accounted (tests, 10/10 runs); capture rate by gap (explicit measurement): 100 of 100 at ≥ 2 ms in all 12 rounds, 62–100 at 1 ms, mostly overwritten below 0.5 ms | isolated window station | ✅ (corrected 2026-09-25: the earlier "≥ 0.5 ms → 100/100" was one lucky run) |
| Watchdog recovers a deaf listener; own writes ignored; delayed rendering | isolated window station | ✅ |
| `DisabledHotkeys=V` ⇒ `RegisterHotKey` path; restore ⇒ Explorer owns Win+V again | real Explorer restarts | ✅ |
| Release zips (x64 + ARM64 native DLLs), published app starts (WinUI window) and exits | `package.ps1` + launch | ✅ |
| 0.2.4 installed on this PC before its release (2026-10-01): local zips (x64 70.5 MB, ARM64 68.0 MB, version 0.2.4 in both exes). `install.ps1` verified the SHA-256, closed the running 0.2.3 gracefully and swapped. The app was started from Explorer (`--background`, user environment). `installer.json`, the Run entry and the Installed-apps `DisplayVersion` all say 0.2.4. The log shows the store opened (the path backfill runs inside `Initialize`), Win+V registered, ShareX watched and the Windows import done, with 0 WRN/ERR | `package.ps1` + the shadowing install wrapper (since then `tools/release/install-local.ps1`) + `launch_background.ps1` (scratch) | ✅ (the backfill's result on the real history was not counted: the command line is off there, and the store is never opened from a second process) |
| `install.ps1`: install / update-over-running / bad checksum / uninstall | offline harness, PS 5.1 + 7 | ✅ |
| Live capture: text, link, color, files, image (+thumbnail) | real clipboard + DB inspection | ✅ |
| Privacy markers (`Exclude…`, `CanInclude…=0`; `=1` still recorded) | WinForms DataObject from PowerShell 5.1 | ✅ |
| Win+V takeover via LL hook; Win+V returns on exit | log + `probe_hotkeys.cs` | ✅ |
| Flyout near caret (WPF target), acrylic, cards, filters, search (substring) | screenshots | ✅ |
| Enter pastes into target; Shift+Enter plain; Down/Ctrl+P/Del; Esc restores focus | `tools/e2e` | ✅ (after fixes: focus order, first-show focus, auto-select) |
| Persistence across restart; no duplicate re-import (25 found → 0 new) | restart | ✅ |
| Windows history import incl. decrypted pin; image DIB/PNG dedupe by pixels | logs + DB | ✅ |
| `--exit`, `--background` | CLI | ✅ |
| Zero-delay typing right after Win+V (show-first ordering) | VM, SendInput Win+V then a letter after N ms (2026-10-09) | ✅ from 100 ms on the first summon of a session (after the warm-up) and from 30 ms on later ones; ❌ below that (§6) |
| Windows-history toggle via registry, Start with Windows (app toggle), multi-monitor DPI | — | ⚠️ not verified |

---

## 6. Roadmap / known gaps

- **Long texts** (2026-11-13, §2.50), next steps:
  - Find (Ctrl+F) in the text viewer, across its pages (not chosen this time);
  - a selection and *Select all* across pages (copy from the source text, not from RichEdit), and a *Copy* that gives a
    Markdown document's source rather than its drawn text;
  - the peek's language from the viewer's own detection, so a megabyte log no longer reads "Markdown" in the peek;
  - measure RichEdit on a real GPU (the page size was set on a test desktop without one), and listen to Narrator in both
    overlays;
  - decide whether a copy made in the peek or the viewer belongs in the history (today it becomes a new entry, like any
    copy).
- **Pictures and their files** (2026-11-13, §2.49), next steps:
  - follow a file that was renamed or moved (its NTFS file id could be kept with the path, or the folder searched by
    pixels again), instead of calling it "moved or deleted";
  - a file for pictures copied from apps: a browser's *Copy image* puts the picture's address in its HTML (a link, not a
    file), and a picture file copied in File Explorer could offer the picture's actions next to the file list's;
  - run it in the Microsoft Store edition, on a network share and on a standard user account.
- **Shell starts still on the UI thread** (found 2026-11-13, §4): the Everything tab's *Open* and *Show in Explorer*
  (`ClipboardFlyout.OpenPath`, `RevealPath`), `AppController.OpenWebLink` (the release notes' and Settings' links), the
  data-folder button, Settings' *Restart now* and the Store edition's own starts (`OpenShellAddress`: Windows' Settings
  pages and the Store, both packaged apps) call `Process.Start` with `UseShellExecute` on the UI thread.
  None has crashed (no popup closes in the same click, as the viewer's link did), but each would be safer started from
  the thread pool like `AppController.LaunchAsync`.
- ~~**The tray icon after a timed-out `NIM_ADD`**~~ (found 2026-11-13 in the portable-update QA, §5): fixed the same day
  (§2.48). A refused `NIM_ADD` is followed by a `NIM_MODIFY` that finds an icon Explorer added late, and a real refusal is
  retried on a timer. Copies up to 0.2.10 still have the dead icon after a slow sign-in.
- **The Microsoft Store edition** (2026-11-13, §2.47, §3.5), next steps:
  - **certification** (submitted 2026-11-13, §3.5): watch Partner Center for its report; a failure is fixed and
    submitted again (a new submission made from this one). It publishes itself once certified;
  - ~~waiting for the owner's word~~ and ~~replace the listing's screenshots~~: done 2026-11-13 (v0.2.11 released;
    eight new scenes; submitted on the owner's "You tick and submit");
  - **the first summon's frame of unbound defaults** (below) showed up again in the retake: worth fixing before the
    next screenshots, since a capture taken a moment too early shows it;
  - **translation reviews**: issues #4 (Russian) and #5 (Spanish) ask native speakers; corrections go into the
    catalogs and into the kit, then one import;
  - **connect the pipeline**, on the owner's word (`docs/microsoft-store.md` §7 has the plan and what Microsoft's
    pages ask): a work account in the owner's Azure directory, one sign-in by the owner to associate the tenant,
    the application with the Manager role, the three `PARTNER_CENTER_*` secrets by pipe, then *Microsoft Store ›
    check*. `submit-store.ps1` has never met the real service; its first real run is the release after the first
    published submission;
  - **a sign-in without a stored key**: GitHub's OpenID Connect token and a federated credential on the
    application would replace `PARTNER_CENTER_CLIENT_SECRET`, which runs out after a year and then fails the Store
    step. It can only be tried once the connection exists;
  - **install the package in CI**: the CI job builds the test-signed package; installing it on the runner (as
    `test-chocolatey.ps1` does for Chocolatey) would also catch a package that builds but does not run;
  - **localized screenshots**: all five listings show the English pictures (eight scenes, the owner's choice for
    the first submission; §3.5 has the procedure; `seed-demo.cs` writes English settings, so another language is
    one setting changed in Settings after the load, and the data itself stays English);
  - optional: let the pipeline update the listing texts too (the API's submission holds them), from the kit;
  - **see the two guided pages** (a copy for every user, a Chocolatey copy) on screen, and the setup in Spanish and
    Chinese;
  - **run it** on a standard user account, with UAC on, on a real GPU and on an ARM64 device; run the Windows App
    Certification Kit on the submitted package;
  - **after the first Store release:** an update served by the Store (the restart `PackagedRestart` asks for), and the
    Store link in the README and in the installer's Store-edition warning;
  - **a native review of the listing's translations** (zh-Hans, ru, es, he: written by Claude, like the catalogs);
  - optional: let the Store edition offer the move also from a Chocolatey copy when the user is an administrator
    (today it only guides), and merge two histories (today the other one stays where it is).
- **The panel's first frame of a session can show unbound defaults** (seen once, 2026-11-13, on the GPU-less test
  desktop, in the Store edition and equally possible in every edition): the "Paused" chip and the Everything tab's
  *Start Everything* were drawn for one frame at the first summon. x:Bind in a `Window` initializes at the window's
  first `Activated` (the generated `Activated += bindings.Activated`), and the panel is first activated by that
  summon. Calling `Bindings.Initialize()` in the warm-up, before the panel is first shown cloaked, should bind earlier;
  not done here, because another session has uncommitted changes in `ClipboardFlyout.*.cs`.
- **Cards dragged onto a group keep a selection bar** (seen 2026-11-13 while the Store listing's screenshots were
  made, §3.5; in the Store edition, whose list code is every edition's): cards that had been dragged onto group
  icons were still drawn with a selection bar afterwards, next to the row that was really selected, until the app
  was restarted. Not investigated. It looks like §2.27's second selection bar (a row's container handed on with its
  bar still drawn) reached another way **[inferred]**: a drag selects the dragged row, and the membership change
  then updates that card in place. First step: ask UI Automation which rows are selected after a drag (in §2.27 it
  told the drawing from the state), then look at what `DragItemsStarting` and `DragItemsCompleted` leave selected.
- **The worker's desktop gateway has no automatic restart** (found 2026-11-13, §4): a `delete_desktop` made Caddy
  panic on its config reload ("context: internal error: missing cancel error") and stay down until it was started by
  hand. It belongs to the claude-desktops repository (`Restart=on-failure` for the unit, and a delete that finishes
  its persist-folder removal after a failed reload).
- **The silent restart** (2026-11-12, §2.46), next steps:
  - watch it on the owner's PC: a real GPU, several monitors (the log's "Restarting silently …" and "Silent restart: …"
    lines, and the new process's memory), and on a standard user account;
  - **a `WAIT` pointer over Settings, seen in the test desktop:** while Settings was open under a pointer that never
    moved, the pointer read `WAIT` the whole time (and once for ~40 ms as Settings closed). It comes before the restart,
    not from it, and was not investigated: check whether 0.2.10 does the same, and whether a real mouse ever sees it;
  - instances that may not hook (the hook fallback switched off, an isolated instance next to the installed app) keep a
    one-second gap for the shortcuts: `RegisterHotKey` cannot take a shortcut the old process still holds;
  - the new process repeats the integrations' catch-up passes and the warm-up's list load, which the old process had
    just done; skipping them after a silent restart would shorten the switch.
- **Memory** (2026-11-12, §2.45), next steps:
  - retake the README's Settings photo (§3.4): it shows the old title bar;
  - the 3 GDI handles each Settings open still leaves: try the window icon first (`AppWindow.SetIcon` from a path, against
    an icon handle the app loads once and keeps);
  - the one-time caches of the first Settings open (+50 to 60 MB private): find out which of them a smaller window, or
    fewer cards built at once, would avoid (the silent restart, §2.46, gives them back a few seconds after Settings
    closes; avoiding them would spare it the switch);
  - run it on a standard user account, ARM64 and several monitors, and watch the owner's installed copy over a few days
    (the log's "Memory after …" lines);
  - what is left (§2.45's table) costs speed to cut: tearing the cloaked panel down while idle brings back the slow first
    summon (§2.27: 630 ms against 100 ms), and rendering without the GPU slows every frame. Worth measuring first: fewer
    cards kept realized while the panel is hidden, SQLite's cache per pooled connection, thumbnails dropped while hidden.
- **Line breaks inside a shortcut, app-wide** (found 2026-11-09 in the §2.43 QA): DirectWrite breaks "Win+PrtScn" before
  its "+" at a line's end — seen in the Russian Snipping card's description ("…от Win" / "+PrtScn"). The Delete stored
  dialog glues its texts (`StoredText.KeepShortcutsTogether`); the rest of the app does not. Doing it once where
  catalogs load (like the Hebrew embedding) would cover every translated text, but tests compare exact strings, and
  `UiText.EnglishOf` would have to see the same text.
- **Settings search and *Delete stored…*** (2026-11-09, §2.43), next steps:
  - see the Everything card's hidden picks, and the dialogs in Hebrew and Chinese, on screen;
  - the search could also open the matching card's section in a future collapsed layout, and rank by score when a
    query matches many settings (the page keeps its order today);
  - a *Delete stored…* for Windows' own import (the entries Win+V's history brought in) was not asked for.
- **Notepad++** (2026-11-08, §2.41), next steps:
  - see a file opened again while it is still open in Notepad++ (today only a switch to its tab). It needs a signal from
    inside Notepad++: a tiny plugin that forwards `NPPN_FILEOPENED` and `NPPN_BUFFERACTIVATED`, which would also replace
    the 5-second count question. A plugin is installed into Notepad++'s folder, so it is the owner's call;
  - run live what the test desktop did not: `-multiInst` with `-settingsDir=`, the portable build, cloud settings, a
    legacy code page, an elevated Notepad++;
  - the same tracked-document store for other editors' unsaved buffers (VS Code's hot exit, for one; where and how it
    keeps them is not researched yet); the rule and the store are not Notepad++-specific (`scope`).
- **Local dev builds** (2026-11-08, §2.39), next steps:
  - a line in *Settings › Updates* naming the dev-builds folder and how many builds it holds, with a button that
    opens it;
  - `publish-dev.ps1 -Remote <PC>`: publish to another PC's folder over the network (its own checksum list already
    guards the copy);
  - offer every local build in the dialog, not only the newest (pick an older dev build, for bisecting).
- **`bclip` on the PATH without the app's DLLs** (found 2026-11-08, §2.9, §4): *Add bclip to PATH* lists the whole
  install folder, so other programs load its runtime DLLs (ShareX held `DWriteCore.dll`). A folder of its own with
  only a launcher for `bclip` (a small native shim, or a `bclip.cmd`) would keep that from happening; existing PATH
  entries would move over at the next install.
- **Where the panel opens** (2026-11-08, §2.38), next steps:
  - see the app's own summon move between monitors with different scales: on the owner's PC (three monitors, one at
    125 %) when they want to try it, or in a test desktop with two displays. The cause and the fix are proven with the
    lab only; look for the log line "The panel came out … instead of …" once per session;
  - the same check for the image viewer and the update dialog, windows of their own that follow the panel's monitor;
  - optional, not asked for: "where I last left it", and a fixed monitor.
- **Interface languages** (§2.37; the deferred gaps were closed on 2026-11-08, "Complete the gaps", and the owner's
  choices built the same day), next steps:
  - the uncommitted AI conversation's strings (§2.35 in the shared tree) need catalog entries when it lands: the guards
    fail until they have them (`catalog.py add -d "…"`, or `add-file` for many), each with its description, and with
    Russian, Spanish and Hebrew too;
  - a native speaker's review of Russian, Spanish and Hebrew (translated by Claude on 2026-11-09, §2.37);
  - right-aligned right-to-left paragraphs, if the owner wants Hebrew's lines to start flush at the right
    (`TextAlignment="DetectFromContent"` on the text blocks): today they are aligned left with the rest of the
    left-to-right layout, so a wrapped Hebrew sentence's lines start ragged;
  - the footer's key hint is cut at its end in Russian's Cmd tab ("Del скр…"), where the status is long;
  - our own sentences for .NET's regex parse errors (`RegexParseException.Error`), which stay English;
  - a review of the Chinese wording (the owner: "You review and you approve"), especially the new 永久忘记 for
    "Forget forever";
  - the README's translation has no marker of the English version it follows (a hash in a comment that a check
    compares would tell when it falls behind).
- **Start with Windows for every user and `install.ps1 -AllUsers`** (2026-10-18, §2.34), next steps:
  - run the uninstall's own elevation (`Invoke-ElevatedUninstall`) with UAC on, and see whether Settings › Apps asks
    for consent by itself for the PC's entry;
  - a second account (a standard user) signing in to a copy for every user;
  - the update dialog of a copy for every user with a real offer, on screen (a build labelled with an older version);
  - optionally, an update button that elevates (a UAC prompt runs the verified package's installer, and the app comes
    back unelevated), instead of copying the command;
  - optionally, start the app at once for the signed-in user when SYSTEM or another account installs (a scheduled
    task in that user's session), instead of at the next sign-in;
  - optionally, automatic update checks for a copy for every user when the signed-in user is an administrator;
  - a version check for the install for every user in `install.ps1`, like `$HotkeyOptionsMinimumVersion` (0.2.9).
    `-Version 0.2.8 -AllUsers`, or an install as SYSTEM or for another account's desktop with an older `-Version`,
    installs an app that has none of the app side: no per-user "off", no step-aside, and a copy that would try to
    update Program Files itself. Found while releasing 0.2.9; the window in which the latest release lacked it lasted
    from the push of `79d5d59` to the release (§5).
- ~~**Push the `irm | iex` fix**~~ (2026-10-16, §3.1): done 2026-10-26 with v0.2.9 (`79d5d59`). The installers inside the
  v0.2.6–v0.2.8 zips needed no release: the update button runs them with `-File`, which binds parameters normally.
- **Ditto** (§2.32, [`docs/ditto.md`](docs/ditto.md)):
  - "Import from Ditto…" in Settings › Integrations, read-only, as designed in the doc's §8: find the database
    (registry, ini files, a running portable copy, or a chosen file / `.dto` export), stream it newest first through the
    history worker in slices, map sticky and never-delete clips to pins and Ditto's groups to groups, `ClipOrigin.Ditto`
    with the Windows import's rules, a summary of what has no equivalent (per-clip hotkeys, quick-paste text);
  - the README could say what Ditto users gain: every copy kept (Ditto drops copies within 0.5 s of the last one),
    all three privacy markers honored, an encrypted history, Win+V;
  - ideas from Ditto worth building (doc §9): special paste, pasting several cards joined, hotkeys for the Nth newest
    item, export and import of selected items, editing an item before pasting, exclusion by regular expression.
- **Rich previews** (§2.33, [`docs/rich-previews.md`](docs/rich-previews.md)), the owner's call on the plan:
  - the live check on Word, Teams, Outlook and Excel (`probe_rich_formats.cs --watch`), to replace the **[docs]** and
    **[inferred]** facts with measurements;
  - step 1, small: a thumbnail and the image peek and viewer for formatted copies that carry a picture (spreadsheets),
    plain text from HTML-only copies, no code font for formatted copies;
  - step 2: a peek for every card and a viewer for formatted copies (RTF in `RichEditBox`, HTML through an own
    converter, WebView2 only on request with every request blocked);
  - step 3: virtual files (zip contents, Outlook attachments: not recorded at all today), LibreOffice's `Markdown`,
    perhaps Office's EMF.
- **A prompt-archive test that can fail on GitHub's runners** (first seen 2026-11-13, the v0.2.11 release run's first
  attempt; a re-run passed): `PromptArchiveTests.Claude_PruneByRenameOver_StoresNothingTwice` renames a staging file
  over `history.jsonl` with `File.Move(…, overwrite: true)` and got "Access to the path is denied". Most likely the
  prompt reader under test had `history.jsonl` open at that moment (the pass the staging file's write wakes, or the hot
  poll): Windows refuses to replace a file another handle holds open, even one opened with delete sharing, unless the
  rename asks for POSIX semantics, which `MoveFileEx` does not **[inferred]**.
  - The test: retry the move for a moment, as any writer meeting that window would have to.
  - The product: Claude Code's own retention prune makes the same rename (§2.21). Check whether BetterClipboard's brief
    opens of `history.jsonl` can make that prune fail, and how Claude Code then behaves (a retry, an error, a skip).
- ~~**Timing tests that fail on GitHub's runners**~~ (seen 2026-10-10 on code identical to a green run; a re-run of the
  failed job passed each time): `WindowsScreenshotWatcherTests.Watch_NeverLocksTheWriterOut_AndWaitsForTheEnd`
  (`reopened > 20` within 900 ms), `PromptArchiveTests.Codex_HistoryLockedByAWriter_IsReadOnceUnlocked` (the locked
  read had already completed after 150 ms) and `WindowsScreenshotsIntegrationTests.FirstActivation_NoBackfill_ThenLive`
  (expected 1, got 0). One of them failed v0.2.7's first release run before anything was published.
  - **Fixed 2026-10-26** (`a4aa62b`, `4da86bd`), while releasing 0.2.9: they failed 4 of 5 CI test runs that day. The
    three wait on conditions now (§4), and so does the ShareX twin of the counter test.
  - If another test starts failing on the runners, `gh run rerun <id> --failed` still gets a run through; fix the test
    the same way afterwards.
  - Candidates seen under a pool capped at 4 threads on this PC (never on GitHub so far):
    `ClipboardCaptureTests.QuickSuccessiveCopies_AreAllCaptured` and `CliPipeServerTests.ManyConnections_AreAllServed`.
  - They also fail on this PC while it is busy (2026-10-10, Rider on about 10 cores, total load about 60 %): 1 to 3 per
    run of the Windows suite. `ClipboardCaptureTests.QuickSuccessiveCopies_AreAllCaptured` failed in every run (24 of
    25 copies, 20 ms apart), also alone and on unchanged code. `PromptArchiveTests.PauseAndOffOn`,
    `Codex_AFileKeptOpenForAppending_IsFoundByThePoll` and `Claude_ImportsThenFollowsAppends` failed in some runs:
    their readers run in Windows' background mode, which a busy PC starves.
- Arrow keys in the groups column (§2.31), next steps:
  - ~~on the user's green light for 0.2.8: tag v0.2.8 on the newest commit that changed the app~~ (done 2026-10-10:
    v0.2.8 on `ccde6fb`, with `03f5058`'s notes draft, §5);
  - ask the user whether a plain click must add a group while several groups are shown (today: Ctrl+click adds, a
    plain click shows that group alone);
  - an announcement for screen readers of the group the ring is on: in a walk that picks, the ring is the only thing
    a step changes;
  - Shift+Space as a Shift+click (a run from the anchor), and a key that starts a merged view from a walk that shows;
  - look at the ring-only style in the dark theme, and at a column longer than the panel.
- Getting around the panel (§2.28), next steps:
  - give the focus back to the search box after a mouse click on a tab, so typing searches at once (today the tab keeps
    the focus until Ctrl+F or a click into the box);
  - check the hover selection on real hardware: a touchpad, a mouse with a high polling rate, touch and pen;
  - the Menu key in the groups column for the group's own menu, and a key that creates a group;
  - an announcement for screen readers when the arrow keys step through the groups;
  - save the tab last chosen also when the app exits while the panel is open;
  - optional: remember the groups shown, and a Settings switch for "always open in All";
  - the newest copy under the pins (2026-11-08): check whether the first summon of a session, with more pins than the
    list's top shows, draws one frame at the top before the measured scroll (its list is never laid out while cloaked);
    with more pins than a page (60) the list starts on its top pin, which loading on to the first copy would fix.
- ~~**Bug (found 2026-10-03, §3.4): the first Win+V after a start leaves the search box without keyboard focus.**~~
  Fixed 2026-10-09 (`0718a9b`, `db9750c`; §2.5 "The first summon of a session"): the box takes the focus once its tree
  has loaded, and the panel is built and rendered off-screen 3 s after startup, so keys typed 100 ms or more after the
  first Win+V reach the box (verified on a VM as a standard user after a `--background` start). Still open: keys typed
  within ~30 ms of the first summon (~10 ms of later ones) reach the app below; closing that needs a key buffer (an LL
  hook that holds the keys typed before the panel is ready and replays them into it). After §2.27 (same day) the panel
  appears complete ~100 ms after Win+V on the GPU-less VM (630 ms before); keys sent within 15 ms still leak there.
- The first Win+V over a window that just got the focus may open the panel at the mouse pointer instead of the caret
  (seen with `probe_summon.cs`: the first summon of 3 runs over a new stage window, old and new build alike, while the
  probe had checked that the stage's thread held its caret; every later summon over the same window opened at the
  caret). Not explained yet: log `ForegroundContext.Capture`'s foreground window and `GetGUIThreadInfo` result for one
  summon to see which part misses.
- Instant summon (§2.27), next: check it on a real GPU and a high-refresh monitor; a key buffer for the first ~15 ms; a
  summon a few ms after a copy could wait (≤ 30 ms, already active) for the queued refresh instead of showing the old list.
- The first peek or viewer of a large copied image waits for a PNG encode: a 3840×2400 copy is stored as a DIB, and
  `GetImagePngAsync` encodes it with WIC first (1.7–2.0 s on the VM, `bclip get --format png`); later views use the
  cache. A BMP stream (file header + the stored DIB) would decode at once; check BI_BITFIELDS/alpha handling first.
- Settings' shortcut hint is a plain text line: give it a live-region announcement so a screen reader says
  "Enter adds Ctrl+Shift+K." when a shortcut is recorded.
- Caret position for apps without a Win32 caret (WinUI, some Electron): UI Automation `TextPattern2.GetCaretRange`.
- LL-hook watchdog (Windows removes hooks that time out) + re-install. (Hook-free mode when `DisabledHotkeys`
  is set: done — `RegisterHotKey` succeeds then.)
- Several shortcuts and the installer's takeover (§2.25), next steps:
  - check after Explorer's restart whether a released key really became free, and take the letter out again when not
    (Win+C on 25H2 stays with another part of Windows);
  - ~~the Chocolatey package's uninstall gives back only Win+V~~ (done 2026-10-03, §3.2);
  - ~~the README's Settings photo shows 0.2.5's single shortcut box~~ (retaken 2026-10-09 with the list, §3.4);
  - the Settings remove button could offer a released Win+letter key back (today only a stray Win+V is offered);
  - an interactive shortcut picker in the installer when `-NoTakeOverWinV` comes without `-Hotkey` (today: Alt+Win+V).
- Export/backup with a user password (re-seal the DEK; the database itself need not be re-encrypted).
- Code signing through the SignPath Foundation (§3.3, [`docs/code-signing.md`](docs/code-signing.md)). The
  repository is prepared; the application waits for reputation: posts and directory listings first, judged by
  GitHub's own numbers; Chocolatey later. After acceptance: the signing step in `release.yml`.
- winget manifest. (The in-app update check against GitHub releases is built: §2.29. The README names it and both
  switches, §3.3.)
- Found during the update QA on the owner's PC (2026-10-10), outside the update system:
  - **A panel opened by `--show-flyout` that does not get the foreground stays on the screen.** While the user
    types in another app, Windows refuses the activation; the panel then never hears a deactivation, which is
    what conceals it (seen for about 30 s with a test copy, always on top). Conceal it when it is not the
    foreground window a moment after it was shown. Not seen with Win+V: Windows lets the program that receives a
    hotkey take the foreground.
  - **Startup fails when another process still holds the store's `-shm` file** ("Startup failed. SQLite Error 10:
    'disk I/O error'", seen with a crashed copy's leftover process object): retry for a moment, then say what
    holds the history instead of ending silently.
  - **Several instances read the clipboard at once.** With four running, one copy was missed by all of them
    ("Clipboard stayed locked"). A paused or isolated instance could stay away from the clipboard.
- The weekly update reminder (§2.30), next steps:
  - listen to Narrator when the tooltip opens by itself, and add an announcement if it says nothing;
  - a photo of it for the README's *Updates* section (the test desktop's helpers make one in a minute);
  - check it with Windows' text size raised, on a second monitor with another scale, and with touch.
- Updates (§2.29), next steps:
  - ~~**A portable copy's *Download page* way has two traps**~~ (found 2026-11-13, §5): built the same day (§2.48), as the
    owner chose: a portable copy of 0.2.11 or later updates itself in its own folder, a newer copy takes over from an
    older one that runs, and the sign-in entry follows the copy that runs. Not built: the dialog sentence "close
    BetterClipboard, then extract over this folder". Next: a file list in each release, so the helper can also remove
    files a release no longer ships.
  - ~~The first whole run with a real release~~: done 2026-10-10 on the owner's own install, 0.2.6 → 0.2.7, four
    times (§5).
  - Retake `docs/images/update.png` with the real pair: it shows a build labelled 0.2.3 that is offered 0.2.5
    (§3.4). On a test desktop: `install.ps1 -Version 0.2.6`, then *Settings › Updates › Check now*.
  - Run the update with a standard user account, with a copy that runs as administrator, and on ARM64.
  - **Keep the reason of a failed installation visible after the restart.** Today it is in a notification (shown
    for about 5 s) and in the log; Settings and the dialog show the plain offer again (seen after the
    folder-in-use case). Carry the installer's result into the first snapshot, and drop it at the next attempt.
  - Network failures show .NET's own words in brackets ("The response ended prematurely, with at least
    54482994 additional bytes expected. (ResponseEnded)"): word them for people.
  - The dialog opens with the keyboard focus on *Update and restart* (a stray Enter approves): decide whether
    *Later* is the better first focus.
  - An isolated instance shows *Check for updates automatically* as on while it never checks by itself (only
    tests see it).
  - The tray menu's *Update to …* was read on the VM only; the photo desktops could cover it too.
  - Once releases are signed (§3.3): check the Authenticode signer of the package's `BetterClipboard.exe` before
    its installer runs, so that matching checksums on a taken-over GitHub account are not enough.
  - A faster installer: Windows PowerShell's start and `Expand-Archive` take about 20 s in a VM. `tar` (in Windows
    since 1803) or .NET's `ZipFile` would cut the extraction.
  - Smaller downloads: the release's `.7z` is 43 MB against the zip's 71, but the installer has no 7-Zip.
  - The dialog: selectable release notes (to copy a command out of them), and an Info line in `bclip status` when
    an update waits.
  - A Group Policy that blocks script files stops the update (§2.29). Running the script's text instead of the
    file (as `irm | iex` does) would pass, at the price of quoting every argument into a command string.
- Chocolatey package: built and wired into CI and the release (§3.2, [`docs/chocolatey.md`](docs/chocolatey.md)).
  Open:
  - the user's steps: the community.chocolatey.org account and the `CHOCOLATEY_API_KEY` secret; then the first push
    through *Actions › Chocolatey package* with version `0.2.5` (a package-only fix) or the next stable tag — **never**
    the `.nupkg` attached to v0.2.5, which empties the Run key (§3.2); deleting that asset; and the
    verifier-exemption answer in the first review. The user chose to build reputation without Chocolatey first (§3.3);
  - optionally: take Win+V over from Explorer at install like `install.ps1` (docs §4.2 has the trade-off: an Explorer
    restart in every `choco install`);
  - the README's `choco install betterclipboard` line once approved;
  - in the app: hide Settings › *Add bclip to PATH* when running from a Chocolatey `lib` folder (the shim already
    puts `bclip` on the PATH);
  - if a moderator asks for a quieter default: start into the tray after a fresh install (docs §5 has the one-line
    change).
- Release 0.2.6 soon, also for safety: the `install.ps1` inside every release zip up to v0.2.5 is what Settings ›
  Apps runs to uninstall, and it empties `Explorer\Advanced` when it gives Win+V back while `DisabledHotkeys` holds
  other letters (§2.25). Only an update replaces it. Replace the v0.2.5 page's `install.ps1` asset in the meantime.
- Smaller release: trim the 26 MB `Microsoft.Windows.SDK.NET.dll` projection (needs a trim-safe audit of
  reflection-based JSON first).
- Delete-through to Windows history (`Clipboard.DeleteItemFromHistory`) when deleting here.
- Password-manager catalog: verify the executable names of Zoho Vault, Devolutions Workspace, Passwarden,
  Total Password and Securden (not found in reliable sources on 2026-09-25). Path-aware ignore rules would
  allow generic names safely (Ente Auth's `auth.exe` only under an `ente` folder).
- Groups, next steps:
  - reorder group icons by dragging them in the column;
  - `bclip list --group NAME` / `bclip group add|remove` for scripts and agents;
  - dragging a card out to other apps (a text/bitmap data provider next to the internal id format);
  - a "drop on + to create a group with this card" shortcut.
- Forget forever, next steps:
  - optional delete-through to Windows' own history (`Clipboard.DeleteItemFromHistory` for items whose
    fingerprint matches), so a forgotten secret also leaves Win+V's RAM buffer and pins;
  - pattern rules ("never record anything that looks like an AWS key / a JWT"), next to the exact list;
  - sweeping look-alikes of texts over the 32 K `search_text` cap (would need a stored fingerprint column).
- Search toggles, next steps (§2.6.2):
  - the Everything tab: pass `CurrentSearchOptions` to its stored query and map the toggles to Everything's own
    match-case / whole-word / regex search flags, so both halves of the tab search alike;
  - `bclip search` options for the same toggles (the request needs a field for them; the store already takes
    `ClipQuery.SearchOptions`);
  - highlighting the matches in the cards;
  - recent searches in the box's suggestion list (the `AutoSuggestBox` has one, unused today).
- Paths copied as text, next steps:
  - *Show in Explorer* / *Open* for path texts that are absolute and local (never probe network paths on the
    UI thread: a dead share blocks for tens of seconds);
  - `file:line` references (`src/x.cs:10`, `x.cs(10,5)`) as a path with a location;
  - an opt-in to accept lowercase names with spaces unquoted (the precision trade-off in §2.13).
- ShareX, next steps:
  - an opt-in one-time backfill of the existing screenshot archive;
  - upload URLs from `History.db` (task completion) as link items next to their screenshot;
  - watching a moved `CustomHotkeysConfigPath` file (today the 5-minute refresh catches it);
  - verifying the Microsoft Store build's folders;
  - Greenshot folders with the same watcher (Snipping Tool and Win+PrtScn: the next item).
- Windows' own screenshots: Win+PrtScn and Snipping Tool's auto-save (asked for 2026-10-01; facts in §2.18). **Built
  2026-10-01 as the Snipping tab (§2.22)**, with ShareX keeping its own tab (the user's call, against the proposal to
  merge them). Built as proposed: the stand-alone name fix, the watcher's rules, the source by name shape, the hybrid
  origin, the catch-up marker, the filter by origin and source, Settings on by default. Next:
  - **The live check** (`probe_screenshots.cs --watch`, the user's own Win+PrtScn and Win+Shift+S). It decides whether
    Win+PrtScn shots are new to the history or a second copy of a clipboard copy. The merge itself is verified offline
    for both bitmap kinds (§2.22).
  - **If Win+PrtScn does copy:** keep the tool's name when its explorer.exe-owned copy bumps the entry after the file
    (today the card would then read "Windows Explorer"; the entry stays in the tab by its origin).
  - **A custom Snipping Tool folder:** a hint is shown; *Also watch a folder…* would follow it (its FutureAccessList form
    is unknown).
  - **ShareX's watcher:** move it to the same writer-friendly read (it still opens with `FileShare.Read`, §4).
  - **Later:**
    - Snipping Tool recordings as file entries;
    - Game Bar and NVIDIA captures;
    - *Show in Explorer* / *Paste as file* on screenshot entries (needs a stored path);
    - storing only the PNG and making the DIBV5 at paste, which saves 16.6 MB per 3840×1080 shot.
- voidtools Everything (facts and the built tab in §2.14). **Built 2026-10-01:** the client (proposal option 1:
  owner check, state check, a deadline per query, latest-wins, a fake IPC window in the tests plus an opt-in real
  Everything) and the Everything tab (option 5: a live view, entries stored only when the user acts on a pick,
  *Show in Everything* on stored file lists). Next, most value first:
  1. **Path cards and file lists:** found / missing / moved states (one name-led batch per page, cached), with
     the actions the picks already have. Relative paths (`src/x.cs`) resolve to the real file, with a picker when
     several match. Absolute paths are checked without touching the disk or a dead share. Needs the normalizing
     query builder in Core (slashes, trailing separators, `\\?\`, `file:`, `%VAR%`, `~`, `/c/`, `/mnt/c/`; name-led).
  2. **"Files on this PC" in the flyout's search:** paste a file you never copied.
  3. **`bclip`:** `resolve <id>` for agents, and the live picks next to `list -f everything`'s stored half.
  4. **The tab:** a refresh while it is open (1.5's IPC3 pipe and index journal, or the run-count sort polled
     while the tab shows), paging past 200, thumbnails for image picks, and 1.5's typed searches
     (`Search History.csv`, on by default there) as "searched in Everything".
  - Not planned: classifying by the index (verdicts must stay deterministic), imports from the index journal,
    and bundling Everything or the SDK DLLs.
- Run tab (Win+R history): **built 2026-10-01, §2.17.** Next steps:
  - the modern Run dialog's own history once a build that has it can be checked (CmdPal keeps `state.json`
    `RunHistory` and never writes `RunMRU`, §2.15);
  - an opt-in delete-through to `RunMRU` (never writing BetterClipboard's history back into it);
  - `bclip run <id>` for scripts and agents, with the same "only commands run before" rule;
  - a maximum wait for the watch's debounce: scripted bursts (writes < 150 ms apart) are read as one batch, and
    with more than 26 in one burst the oldest are lost (measured: 26 of 30 at 50 ms; 30 of 30 at 400 ms).
- Pwsh and Cmd tabs, next steps (built 2026-10-01 as live views, §2.19; facts in §2.16):
  - the Cmd helper's 3-s deadline: the installed `0.2.4-dev.74594fb` hit it twice in ~15 h on this PC ("A Command
    Prompt history helper did not finish in time and was ended.", 2026-10-01 18:53Z and 2026-10-02 01:01Z, its only
    warnings). The next build, `0.2.5+ac65856`, ran ~9 h without it; `0.2.5+5cf1eef` hit it twice in ~1.8 h, 28 s
    apart (2026-10-02 19:57:02Z and 19:57:30Z: two reads in a row, as if one window stayed slow). Find out when it
    happens (sleep and resume? a console busy printing?) before raising the deadline or logging it lower;
  - live updates while the Pwsh tab is open (a `FileSystemWatcher` on the PSReadLine folder; today reopening reloads);
  - a PowerShell history moved by a profile (`Set-PSReadLineOption -HistorySavePath`): a folder setting, since reading
    the profile would mean running it;
  - Clink's `%LOCALAPPDATA%\clink\clink_history` as the Cmd tab's source when installed (a real file, no helpers);
  - Git Bash `~/.bash_history` the PowerShell way (it arrives at shell exit);
  - an optional secret filter for what the tabs show (2.0.0's regex plus `ghp_`, `github_pat_`, `sk-`, `AKIA`,
    Bearer, `-p<pass>`, `curl -u`), next to Forget forever;
  - the tab bar: with ShareX, Run, Pwsh, Cmd and Everything the window is ~600 DIP wide; one "Commands" tab with
    chips (Win+R, PowerShell, cmd, Bash) would keep it at 400.
- An MCP server (stdio) speaking the same pipe protocol, so agents get typed tools instead of shelling out
  to `bclip`; `bclip` itself could gain `--null`-separated output and `get --all-formats` export.
- Day grouping, collections/favorites, snippets, OCR for images (`Windows.Media.Ocr`), paste transforms
  (trim, case, JSON pretty), large preview pane, drag-out, sync between PCs, MSIX packaging.
- Claude Code and Codex prompts (built 2026-10-01, §2.21), next steps:
  - Claude Code's transcripts as a second source for prompts that never reach `history.jsonl` (its IDE panel or desktop
    app, if they bypass it): only records marked `promptSource: typed|queued`, merged by session + text;
  - Codex thread names (`session_index.jsonl` › `thread_name`) and Claude Code session titles in the captions, and a
    project/thread chip row to narrow a tab;
  - `bclip prompts --project PATH` / `--session ID`, and an MCP tool over the same archive for agents;
  - keeping attached images (today counted only), with the size budget in mind;
  - a third agent with the same machinery (e.g. Gemini CLI's history), now that the reader is agent-agnostic.
- Resizable panel and tab carousel (§2.23), next steps: a "Reset size" button in Settings; Ctrl+Tab / Ctrl+Shift+Tab to
  switch tabs from the keyboard (the strip would follow through `RevealSelectedTab`); a fade under the arrows instead of
  the hard clip; inertia after a drag.
- Third party (§2.20), next steps:
  - a scheduled link check (every catalog link still answers 200 at its address), e.g. a weekly CI job; the unit
    tests stay offline on purpose;
  - the full license texts in the release zip next to `THIRD-PARTY-NOTICES.md`, which links to them today (MIT and
    BSD-3-Clause ask for the notice text with binary copies).
