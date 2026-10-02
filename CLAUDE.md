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

- [Ditto](https://github.com/sabrogden/Ditto) (C++/MFC, GPL-3.0) — classic, DB-backed, very configurable.
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
another process appends to a file, §2.21).
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
| [`src/BetterClipboard.Core`](src/BetterClipboard.Core) | `net10.0` | OS-agnostic heart: models (`Model/`), codecs + classifier + hashing + path detector (`Content/`, §2.13), encrypted SQLite store + machine-bound store opener (`Storage/`), key hierarchy (`Security/`: UUIDv5, HKDF machine binding, sealed key vault), capture pipeline (`Services/ClipHistoryService`), command line (`Cli/`: protocol, pipe naming + framing, argument grammar, command processor, output — §2.9), Win+R list logic (`Integrations/RunMru`: parse, fingerprints, runs since a snapshot — §2.17), Windows screenshot rules (`Integrations/WindowsScreenshots`: the tool by a file name's shape, fresh writes, completeness from the bytes, Snipping Tool's saving settings — §2.22), Everything tab logic (`Everything/`: IPC wire format, queries, `Run History.csv`, merge and hide rules — §2.14), the prompt archive's logic (`Prompts/`: Claude Code and Codex parsers, keys, the incremental JSONL reader `JsonlTail` — §2.21; its tables in `Storage/ClipStore.Prompts.cs`), settings (incl. the remembered panel size, §2.23), logging, presentation helpers (incl. `Presentation/ThirdPartyCatalog`, the source of Settings › Third party, §2.20, and `Presentation/TabStripScroll`, the filter-tab carousel's arithmetic, §2.23). **CS1591 = error.** |
| [`src/BetterClipboard.Windows`](src/BetterClipboard.Windows) | `net10.0-windows10.0.26100.0` | Everything OS: `Interop/` (LibraryImport P/Invoke, `MessageWindowThread`), `Clipboard/` (listener/reader/writer, source attribution), `Input/` (hotkey + WH_KEYBOARD_LL takeover, paste injection, placement, the panel's remembered size in pixels and DIPs — `FlyoutSizing`, §2.23), `Imaging/` (DIB math + WIC, PNG export for the CLI), `Import/` (DPAPI-NG, pinned store, WinRT history), `Shell/` (tray icon, Run key, Windows clipboard/Explorer settings, user PATH, running a command like Win+R), `Security/` (MachineGuid + SID, DPAPI key protector), `Cli/` (ACL'd named-pipe server), `Integrations/` (ShareX: locator, folder-pattern rules, screenshot watcher, integration life cycle — §2.10; Windows' screenshots: Screenshots-folder locator (known folder, Snipping Tool's package and saving settings), a folder watcher that never locks a writer out, integration life cycle — §2.22; Win+R history: `RunMRU` reader, change watch, integration life cycle — §2.17; voidtools Everything: IPC client, owner check (Authenticode, voidtools signer), install locator, integration life cycle — §2.14; the prompt archive's readers: agent folders, file access (shared, lock retries, NTFS file id, zstd), watchers + hot poll + reconcile on a background-mode thread — §2.21). **CS1591 = error.** |
| [`src/BetterClipboard.Cli`](src/BetterClipboard.Cli) | `net10.0-windows` console | `bclip`: parses arguments, gates on the app's `EnableCommandLine`, talks to the running app over the pipe (starting it if needed), prints text/JSON with exit codes (§2.9). Published self-contained next to `BetterClipboard.exe`. **CS1591 = error.** |
| [`src/BetterClipboard.App`](src/BetterClipboard.App) | `net10.0-windows10.0.26100.0` WinUI 3 | Windows App SDK **2.5.1** as component packages (Base/Foundation/InteractiveExperiences/WinUI/DWrite — the metapackage's AI/ML/Search/Widgets add ~57 MB we don't use), unpackaged (`WindowsPackageType=None`), `WindowsAppSDKSelfContained=true`, custom `Program.Main` (single instance + commands). `AppController` = composition root. Views: `ClipboardFlyout` (acrylic Win+V replacement), `SettingsWindow` (Mica). |
| [`tests/BetterClipboard.Core.Tests`](tests/BetterClipboard.Core.Tests) | `net10.0` | xunit.v3 on Microsoft.Testing.Platform (717 tests, one class at a time — §4: the tab carousel's arithmetic and the remembered panel size (§2.23: arrow steps tab by tab both ways, order-independence, ends and out-of-range offsets, reveal, wheel and tilt, drag, clamps; defaults, persistence, clamping of a hand-edited size); the Snipping tab (§2.22: file names by shape, localized and right-to-left ones included, freshness, completeness per format, Snipping Tool's settings, the filter, the hybrid merge rules, pause and ignored apps, the "SnippingTool.exe" relabel, the CLI names, the setting); the prompt archive (§2.21: Claude Code and Codex parsers, key known answers, `JsonlTail` for appends, partial lines, truncation, trims, filters, replacement, CRLF, long lines and unseekable streams, the store's merges, Codex twin records in either order, tombstones, rewrites, forget, listing and search, checkpoints, schema on an older store, the service's pause/ignore/size rules and slices, `bclip prompts`/`prompt`); the Third party catalog (link wording, the official-link rule, every restored package credited, both directions of agreement with `THIRD-PARTY-NOTICES.md`); the Pwsh and Cmd tabs (§2.19); the Everything tab (IPC wire format incl. replies that lie about their size, queries, `Run History.csv` incl. a write cut off mid-path, merge and hide rules, the Everything filter and origin, forgetting files by path, pick formats, `-f everything`); Win+R list logic (parse, fingerprint known answers, runs since a snapshot for every kind of list change, planned captures), the run column, Run filter, merge rules and tombstones of both Win+R origins, a store from before the column, pause/ignore/Forget forever for runs, `-f run`; paths copied as text (a 234-case detector corpus: every form, prose, commands, URLs, escapes, whitespace; Files/Text filters; backfill and rules version), content, store, **encryption at rest**, key-hierarchy known-answer tests, CLI grammar/protocol/processor/output, one-time data fix-ups, password-manager catalog seeding, ShareX origin/filter/state semantics, groups: CRUD, membership filter, merged views of several groups (one list in the usual order, paging, search and toggles, the union count), the Ctrl/Shift click rules and their wording, kept-like-pinned retention, reset clock, schema added to an older store, service events, icon catalog; Forget forever: fingerprint normalization, known answers and chunking, the look-alike sweep, list life cycle, blocking across channels, Settings wording; search toggles: match case, VS Code's whole-word rule (punctuation edges, overlaps, runes), regex lines/engines/timeout, the prefilter superset, the SQL function inside real queries: order, paging, filters, groups, failures keeping their type, persisted toggles). |
| [`tests/BetterClipboard.Windows.Tests`](tests/BetterClipboard.Windows.Tests) | `net10.0-windows…` | Hotkeys, interceptor, placement, DIB/WIC, DPAPI-NG, synthetic pinned store, real DPAPI/MachineGuid, **clipboard capture in a private window station** (bursts, watchdog, echo, delayed rendering), CLI pipe server (real pipes: refusal of a 2nd server, hang-up, malformed input, 124-connection stress: 100 sequential + 24 parallel) + CLI end-to-end through the real monitor, user-PATH rules, flyout drag tracker, ShareX (pattern rules, locator against fake ShareX layouts, screenshot watcher on temp folders, integration marker life cycle over a real history), groups column growing/shrinking on the left, Forget forever end to end (a real copy of forgotten text is read and kept out), opt-in real-clipboard round trip, explicit capture-rate measurement, Win+R history on scratch HKCU keys (reader, settle wait, change watch incl. a key that appears later, integration: first import, live runs, re-runs, restart catch-up, off/on, pause, a missing list, keeping more than Windows' 26, a rescan racing the watch, runs from the panel) and Run-dialog parsing + hidden launches, Everything (the client against a fake IPC window in this process: trust, state, reply matching, latest-wins, deadlines, garbled replies, a hung window, the command line; the integration: live picks, the saved file while gone, loading or garbled, never an impostor; the owner check: other names, unsigned, another publisher; locator hints; quoting checked with `CommandLineToArgvW`; opt-in real Everything), the Pwsh and Cmd tabs (§2.19: the PowerShell source on temp files, the helper's wire format), the prompt archive's readers on temp agent folders (§2.21: first import + watcher, rename-over prune, whose Codex threads, a writer that keeps its file open, archive move + zstd compression, the mandatory lock, pause and off/on, restart, file ids across moves), Windows' screenshots (§2.22: each tool's name, a writer reopening its file while the watcher polls, files copied or moved in, skips, renames, catch-up, a folder created later, the marker's life cycle, a clipboard copy and its file merging for a DIBV5 and a zero-alpha BI_RGB DIB, the locator, the display-name rule), the panel's remembered size (§2.23: pixels to DIPs and back at every Windows scale without drift, the groups column left out of the remembered width, clamping and bad scales, the per-scale minimum) (211 tests). |
| [`tools/`](tools) | scripts | `probes/` (research), `e2e/` (UI harness — see §4), [`release/package.ps1`](tools/release/package.ps1) (release zips + `.7z` archives + SHA256SUMS, shared with CI), [`release/package-chocolatey.ps1`](tools/release/package-chocolatey.ps1) / [`release/test-chocolatey.ps1`](tools/release/test-chocolatey.ps1) (the Chocolatey package and its real install test, §3.2), [`release/install-local.ps1`](tools/release/install-local.ps1) (installs those zips on this PC with the real installer before a release, §3.1), [`launch_dev.py`](tools/launch_dev.py) (runs a copy of the dev build next to the installed app for the user to try, §3), [`make_icon.py`](tools/make_icon.py) (app icon). |
| [`packaging/chocolatey`](packaging/chocolatey) | nuspec / PowerShell | The `betterclipboard` Chocolatey package's template: install, before-modify and uninstall scripts, shared helpers, verification text (§3.2, [`docs/chocolatey.md`](docs/chocolatey.md)). |
| [`install.ps1`](install.ps1), [`.github/workflows/`](.github/workflows) | PowerShell / Actions | Installer from GitHub releases (§3.1) · CI (build, test, package, Chocolatey install test) · release on `v*` tags (+ Chocolatey push). |

Shared build config: [`Directory.Build.props`](Directory.Build.props) (docs on,
nullable, version), [`Directory.Packages.props`](Directory.Packages.props)
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
```

Rules: nothing heavy on the hook thread (Windows silently drops slow LL hooks); the clipboard thread only
copies bytes then closes the clipboard ASAP; all writes are serialized through the history worker; reads
(`QueryAsync` etc.) hit SQLite directly (WAL) from the thread pool.

### 2.3 Capture pipeline

1. `WM_CLIPBOARDUPDATE` → snapshot **source attribution now** (owner window, else foreground window —
   short-lived producers like scripts exit quickly) → **read immediately** in the handler (no debounce:
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
app unhooks → Win+V instantly returns to Windows (verified with `probe_hotkeys.cs`). Optional
"Release Win+V from Explorer" (Settings, or `install.ps1 -TakeOverWinV`) writes `DisabledHotkeys` and
restarts Explorer; `RegisterHotKey` then succeeds and no hook is needed — works over elevated windows too,
but Win+V is dead while the app is not running (uninstall restores it) **[verified 2026-09-25, §1.5]**.
The log names the path: `… registered with RegisterHotKey …` vs `… intercepting it with a keyboard hook`.

### 2.5 Summon & paste flow

Hotkey → `ForegroundContext.Capture()` on the input thread (target HWND + Win32 caret via
`GetGUIThreadInfo` + cursor) → UI: flyout shown **first** (focus to search box; keystrokes must never leak
into the app below), then list reloads and the first card is selected → Enter/click →
`ReplayFormats.Prepare` (plain text strips formats; file lists as plain text become paths; a stored
"cut" drop effect is rewritten to "copy") → write clipboard while still foreground → **re-activate the
target while we still own the foreground, then hide** (hiding first loses the right to set focus) →
wait for foreground → release held modifiers (mask-key first for Win/Alt) → inject Ctrl+V.

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
`logs\betterclipboard-*.log` with 14-day retention, `installer.json` from `install.ps1`); override with env
`BETTERCLIPBOARD_DATA_DIR` (tests, dev runs; the installer honors it too — **always use it when
experimenting** so the real history stays clean). An override also **scopes the instance** (§2.9): its own
single-instance lock, `--exit`/`--show-flyout` events and pipe, so an isolated run coexists with the
user's installed app instead of signalling it. Everything is encrypted at rest (§2.6.1); Windows itself
encrypts only pins. Logs never contain clipboard content, keys, the binding or the identifiers.

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
  summon always starts in the regular view, like the filter tabs. Ctrl+click and Shift+click show several
  groups at once (below).
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
- Run tab (`RunFilter`, a text tab after ShareX) while the setting is on; the tab strip scrolls when the visible tabs
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
say it saves a file, and no capture was taken. The live check settles it, with the user's own screenshots:
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
- Rows: the stored half first (pinned on top when that setting asks, then most recently used), then the live commands
  in the shell's order, at most 500 (no paging). A live command that is also stored shows once, as the entry; forgotten
  ones never show (fingerprints via `FindForgottenAsync`); the search box narrows both halves with the same toggles
  (`SearchMatcher`, on the pool).
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
- **Listing:** grouped by text (newest send's row via `ix_prompts_text`), paged by text, kept prompts' hashes excluded.
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
- **Tab:** "Snipping" after ShareX (tooltip names both tools). It shows while the setting is on, and falls back to All when
  it disappears while selected; the tab strip scrolls to make room for it like for any tab (§2.23). Cards are ordinary image cards: "Snipping
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
  - Defaults 400 × 560, the old fixed size. `Normalize` clamps them to 360..8192 × 320..8192.
- **Applied at every summon:** `FlyoutSizing.ToPixels` at the anchor monitor's scale → `FlyoutPositioner.Compute`
  (which still shrinks the size to the work area, without saving that) → `ExtendLeft` for an open column.
- **Saved once per resize, and only the user's** (`Interop/WindowSizeHook`, comctl32 `SetWindowSubclass`):
  - A `WM_SIZING` between `WM_ENTERSIZEMOVE` and `WM_EXITSIZEMOVE` means the user resized (a move loop never gets
    `WM_SIZING`), so `UserResized` fires once at the end.
  - Then `FlyoutSizing.ToDips` → `Settings.Update` from the dispatcher queue. One write per resize, never per mouse
    move: `SettingsStore.Update` writes the file on every call.
  - Size changes the panel makes itself are never saved: the summon, the groups column, a move to another monitor's
    scale, the work-area shrink.
- **Minimum:** 360 × 320 DIPs (+44 with the column), answered in `WM_GETMINMAXINFO` from the window's scale at that
  moment (`FlyoutSizing.MinimumTrackSize`).
  - 360 is the header: logo, "Clipboard", the "Paused" chip and four buttons take 312 of the 322 content DIPs.
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
  - The capture goes through the pressed tab (`TakeOverTabPress`): the tab captures first, the viewer takes it over,
    and the tab's `PointerCaptureLost` makes it forget the press. Capturing on the viewer alone left the tab drawn
    pressed (dimmed) after the drag, seen in the live test's captures, and still counting as pressed: a later release
    over it would have picked it (`ItemContainer.cpp` resets only on exit, cancel or capture loss).
  - Touch pans natively.
- **Window drag:** `IsDragSurface` treats `TabsScroller` as a control while it can scroll; while every tab fits, its
  empty space moves the window, as before.
- **Bring into view:**
  - A clicked or focused tab raises `BringIntoViewRequested`. `Filters_BringIntoViewRequested` widens the rect by an
    arrow on each side before the outer viewer acts, so the tab ends clear of the arrows.
  - A selection from code (`SelectFilter`) reveals the tab itself (`RevealSelectedTab`).
  - Every summon resets the strip to its start, where "All" is.

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
  into 80% of the work area, never upscaled past 100%), framed like a tooltip. Any mouse move (after a 100 ms grace),
  press, wheel or key dismisses it at once; resting again re-opens it. It shows the full-resolution image, not the
  720×400 card thumbnail.
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
- **Peek dismissal:** its surface covers the monitor and is hit-testable (a transparent — but still hit-testable — grid),
  so the first real move over it (after the 100 ms grace that swallows the synthetic move at open) dismisses it. After it
  hides the pointer is over the card again, whose `PointerEntered` restarts the rest timer — so resting re-peeks. A key
  (`Root_PreviewKeyDown` calls `HidePeek` first), a press or a wheel also dismiss it. `CanShowPeek` blocks it during a
  window drag, a card drag, an open menu or the viewer.
- **Viewer** uses a `ScrollViewer` (`ZoomMode=Enabled`, bars hidden) holding the image at its natural DIP size
  (pixels ÷ monitor scale, so zoom 1.0 = 1:1); the wheel and double-click call `ChangeView` with offsets from
  `ImagePreviewLayout.ZoomOffset` (zoom toward the cursor), a left-button drag on the image pans by `ChangeView`. A tap on
  the dim backdrop — or the letterbox, which bubbles to it — closes; a tap on the image is swallowed so it does not.
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
were rendered from Segoe Fluent Icons and confirmed. **Live visual check still pending** (the peek timing, the
monitor-covering popup placement, and the viewer's zoom/pan): a guarded on-screen e2e like the others (§3), seeding an
image through the Snipping watcher, then reaching the eye and the viewer through UI Automation.

**Limits / not built:** the peek is a tooltip (no interaction); the viewer has no rotate, no copy-from-viewer, no
next/previous between images; a huge image is decoded whole (fine for a transient viewer). Only image cards (not
Everything picks or other kinds) get the peek and the eye.

---

## 3. Build · run · test

```bash
dotnet build BetterClipboard.sln                               # everything (App builds win-x64)
dotnet test --solution BetterClipboard.sln                     # 940 tests (937 run; opt-in tests + 1 explicit measurement skipped)
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
  2. tests and packages;
  3. builds and tests the Chocolatey package (stable tags only, §3.2);
  4. `gh release create --notes-file` with the zips, the `.7z` archives, `SHA256SUMS.txt`, `install.ps1` and
     `betterclipboard.X.Y.Z.nupkg`;
  5. `choco push` (with the `CHOCOLATEY_API_KEY` secret; without it only a warning).

  Tags with a pre-release suffix (`-rc.1`) become pre-releases and skip Chocolatey. CI
  ([`ci.yml`](.github/workflows/ci.yml)) builds, tests and packages x64 on every push/PR, and installs, upgrades and
  uninstalls an x64 Chocolatey package.
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
- **Installer** ([`install.ps1`](install.ps1), Windows PowerShell 5.1 and PowerShell 7, StrictMode 3):
  GitHub API → zip for the **OS** architecture (`RuntimeInformation.OSArchitecture`, correct under x64
  emulation on ARM64) → SHA-256 vs `SHA256SUMS.txt` **and** GitHub's asset `digest` → `--exit` the running
  instance (graceful, 15 s, then kill) → extract to `<dir>.new`, `Unblock-File`, swap via renames
  (failure puts the old version back) → Start-menu shortcut, Run entry in exactly the app's own format
  (`"<exe>" --background`), HKCU `Uninstall\BetterClipboard` entry (runs `install.ps1 -Uninstall` from the
  install folder) → `installer.json` in the data dir → launch (via `explorer.exe` when elevated, so the app
  never runs elevated). `-TakeOverWinV` sets `DisabledHotkeys` + restarts Explorer. `-AddToPath` puts the
  install folder on the user PATH (same rules as `Shell/UserPath`: raw REG_EXPAND_SZ, `%VAR%` entries kept,
  `WM_SETTINGCHANGE` via `Add-Type`, and `$env:Path` of the calling window because `irm | iex` runs in it).
  `-Uninstall` removes everything but the history (`-RemoveData` for that), removes the PATH entry (whoever
  added it) and gives Win+V back to Explorer when released.
  - **A Chocolatey copy** (`lib\betterclipboard\tools\app`, §3.2) is respected:
    - install warns about it and leaves a startup entry that starts it;
    - `-Uninstall` removes the startup entry only when it points into its own folder or at a missing file, and
      keeps Win+V released while that copy remains.

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
- **Pre-release install on this PC** ("install it locally, don't release yet"):
  [`tools/release/install-local.ps1`](tools/release/install-local.ps1) `-Version X.Y.Z` runs the real
  `install.ps1` with the same shadowing, packaged as a script: the lookup and both downloads come from
  `artifacts/release`. Everything else is the real installer (SHA-256 vs `SHA256SUMS.txt`, graceful
  `--exit`, swap, shortcut, Run and Installed-apps entries, `installer.json`). It is a real install, so it
  needs the user's request. Procedure used for 0.2.4 (2026-10-01) and 0.2.5 (2026-10-02):
  1. `pwsh tools/release/package.ps1 -Version X.Y.Z` (both architectures; 84 s for 0.2.4, 124 s for 0.2.5).
     - Since 0.2.5: in a worktree at the bump commit, like the dev build below. The exe then carries that commit's
       revision (`0.2.5+ac65856…`), and no other session's uncommitted work can ship.
     - Run `dotnet test --solution BetterClipboard.sln` there first. `package-chocolatey.ps1 -Version X.Y.Z` works
       here too: the Chocolatey CLI (2.3.0) is installed on this PC, and the script only packs.
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
    version's notes too: 0.2.4 was never tagged, so 0.2.5's notes (`ac65856`) cover everything since v0.2.3.
- **Dev build on this PC** ("Build and install dev", 2026-10-01): the same install, of `main`'s latest commit.
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
     - the app's own Run value, unless the install runs as SYSTEM or not as the desktop's user;
     - never taking over a shortcut or Run value that opens another existing copy;
  5. starts the app unelevated through Explorer and a temporary shortcut, only for the desktop's own user and only
     when no copy already runs in the session: plain after an install, `--background` after an upgrade when it
     was running;
  6. parameters `/NoStartup`, `/NoShortcut`, `/NoLaunch`.
- **Before-modify:**
  - sends `--exit` (graceful) only when the package's copy is the only BetterClipboard in the session; force-stops
    after 15 s or otherwise;
  - writes `tools\upgrade-state.txt` (`running`/`stopped`);
  - never fails.
- **Uninstall:**
  - deletes the state file;
  - removes the shortcut and Run value where they point into the package;
  - gives Win+V back (restarting Explorer, only for the desktop's user), unless `/KeepWinVReleased` is given or
    `install.ps1`'s copy remains;
  - keeps the history.
- **`install.ps1` respects a Chocolatey copy the same way** (§3.1).
- **Build and release:**
  - [`package.ps1`](tools/release/package.ps1) writes the `.7z` archives;
  - [`package-chocolatey.ps1`](tools/release/package-chocolatey.ps1) checks the archives against
    `SHA256SUMS.txt`, fills the template, strips its comments, writes `legal\`, saves the scripts ASCII + BOM,
    refuses SemVer 2 and over 150 MB, and packs;
  - [`test-chocolatey.ps1`](tools/release/test-chocolatey.ps1) installs, upgrades (with the app running) and
    uninstalls the exact `.nupkg`, on CI's and the release job's disposable runner only: it refuses to run
    elsewhere unless `-ConfirmMachineChanges`;
  - `release.yml` pushes after `gh release create`, with the `CHOCOLATEY_API_KEY` secret.

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

**Open: the user's steps** (the doc's §5).
1. Create the community.chocolatey.org account and the `CHOCOLATEY_API_KEY` secret.
2. ~~Push; check that CI's Chocolatey test passed.~~ Done 2026-10-02: 22 of 22.
3. Tag a stable release.
4. Answer the first review, asking for the verifier exemption.
5. After approval, add `choco install betterclipboard` to the README.

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
  - Building it: publish the Chocolatey package (public download counts), directory listings (Softpedia,
    AlternativeTo), posts (r/Windows11, r/windowsapps, Show HN), winget.
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
- **No network code:** `src/` has no HTTP client, sockets or WebView. Links open only on a click. So SignPath's
  standard sentence is true: "This program will not transfer any information to other networked systems unless
  specifically requested by the user or the person installing or operating it".

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
- The user: confirm GitHub 2FA.
- After acceptance: the signing step in `release.yml` (publish → upload artifact → SignPath → archive the signed files
  → checksums), which needs `package.ps1` split into publish and archive halves.

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
  escape WndProc/hook callbacks (they are wrapped).
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
  (§2.24, checked by rendering): RedEye E7B3 (the eye that opens the image viewer), Cancel E711 (the viewer's close button).
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
  - Why: the panel takes the keyboard when it opens, with the first card selected. An Enter the user meant for
    their terminal pastes that card: a BC-TEST item through the real clipboard into the terminal. `PasteOnSelect`
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
- **Items of an `ItemsView`/`SelectorBar` inside a scrolling viewer are virtualized.** The ItemsView template's
  `ItemsRepeater` has `HorizontalCacheLength="0"` and follows every scrolling ancestor's viewport, so items out of view
  are not realized: they have no size, the content width is an estimate, and UIA cannot see them (their rectangles read
  (0, 0, 0, 0)). The tab carousel raises the cache (§2.23). Anything else that measures or reaches items of such a list
  must do the same, or work only with what is realized.
- **Capturing the pointer on an ancestor leaves a pressed WinUI item stale.** An `ItemContainer` (SelectorBarItem,
  ItemsView items) forgets a press only on its own exit, cancel or `PointerCaptureLost`; a capture taken by an ancestor
  gives it none, so it stays drawn pressed and still counts as pressed. Let the item capture first and take the capture
  from it (`ClipboardFlyout.TakeOverTabPress`), and ignore the item's `PointerCaptureLost` bubbling into the ancestor's
  handler (check `OriginalSource`).
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
- **Every third party gets its entry in `Core/Presentation/ThirdPartyCatalog`** (Settings › Third party, §2.20).
  - A package that ships: credit it in a component's `Packages`, or add a component plus its
    `THIRD-PARTY-NOTICES.md` row (same name, same license text). `ThirdPartyCatalogTests` fail until both agree.
  - An integration with another maker's app: add its `Integration` entry with the feature. **No test notices a
    missing one.**
  - Links follow the official-link rule: the final https address, no `aka.ms`, no `/en-us/`.
- Commits: per the user's global rules (message file in scratchpad, `git add` + `git commit` in one
  command, extensive messages, never amend).

---

## 5. Verification status (2026-09-25, Windows 11 25H2 26200.8875, 1920×1080 @100 %)

| Feature | How | Result |
|---|---|---|
| `main` pushed (2026-10-02, `8b65b19..543d2fd`, 34 commits, scanned for secret-like strings first: none): CI run 37007600619 passed. 940 tests, 937 passed + 3 skipped, 0 failed; the x64 package smoke test; and the Chocolatey smoke test's first GitHub run, 22 of 22 (§3.2). The five release pages v0.1.0–v0.2.3 were set to their tag annotations plus the "Code signing policy" footer (`gh release edit`) and read back through the API: 5 of 5 identical, footer present, no mojibake; titles and Latest unchanged; old bodies backed up | `git push`, `gh run view --log`, `gh release edit`, `gh api …/releases/tags/<tag>` | ✅ |
| Code signing readiness (§3.3, 2026-10-02): SignPath Foundation's terms read in full, and the form's 16 fields from its `OSSRequestForm-v4.xlsx`. Version resources and Authenticode signers read from the 0.2.4 release folder (257 PE files: our six agree on `ProductName` BetterClipboard and `ProductVersion` `0.2.4+98addc6…`; 247 others signed by .NET / Microsoft / the .NET Foundation; unsigned: SQLite3MC and three SQLitePCLRaw files) and from the 0.2.5 Debug build (adds unsigned ZstdSharp). `src/` searched for network APIs: none. The release-notes step taken out of `release.yml` and run in a scratch clone whose `v0.2.3` was forced lightweight (as checkout@v5 leaves it) with origin on GitHub: the tag came back annotated, and the notes were the annotation plus the footer, with "—", "…", "›" intact under a 437 console. A tag missing on the remote failed the step (exit 1). The YAML parsed, and `Publish release` reads `steps.notes.outputs.file`. Reference sources: gh 2.85's `gitTagInfo`; checkout's `testRef` without `^{commit}` in v5 and with it in v6/v7 (`de0fac2`, #2356). The application itself not submitted | scratch `signpath/` (`peinfo.ps1`, `signers.ps1`, `notes-step/`), `gh api`, Brave search | ✅ prepared; the workflow change runs for real at the next tag |
| Image overlays (§2.24, 2026-10-02): the hover peek and the eye-icon zoom/pan viewer, both monitor-wide windowed popups. `ImagePreviewLayout` unit-tested (12: fit-down, no-upscale, upscale, empty-input guards, fit zoom + fallback, clamp with reversed bounds and NaN, zoom-toward-point anchoring, zero-old-zoom guard); the solution builds with 0 doc warnings in Core/Windows/App; the full Core suite 729 pass. The glyphs E7B3 (eye) and E711 (close) were rendered from Segoe Fluent Icons and confirmed. The live visual behaviour — the peek's rest/dismiss timing, the monitor-covering popup placement, and the viewer's wheel-zoom and drag-pan — is **not yet checked on screen** | `dotnet build BetterClipboard.sln` + the Core test exe + a PIL render of the glyphs (`render_glyphs.py`, scratch) | ✅ build + unit; ⚠️ live visual pending a guarded on-screen e2e (seed an image via the Snipping watcher, then reach the eye and the viewer through UI Automation) |
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
| Zero-delay typing right after Win+V (show-first ordering) | e2e | ⚠️ not verified (run aborted: user took focus) |
| Windows-history toggle via registry, Start with Windows (app toggle), multi-monitor DPI | — | ⚠️ not verified |

---

## 6. Roadmap / known gaps

- Caret position for apps without a Win32 caret (WinUI, some Electron): UI Automation `TextPattern2.GetCaretRange`.
- LL-hook watchdog (Windows removes hooks that time out) + re-install. (Hook-free mode when `DisabledHotkeys`
  is set: done — `RegisterHotKey` succeeds then.)
- Export/backup with a user password (re-seal the DEK; the database itself need not be re-encrypted).
- Code signing through the SignPath Foundation (§3.3, [`docs/code-signing.md`](docs/code-signing.md)). The
  repository is prepared; the application waits for reputation (Chocolatey download counts, directory listings,
  posts). After acceptance: the signing step in `release.yml`.
- winget manifest, in-app update check against GitHub releases. An update check would make the README's
  no-network privacy sentence untrue: it must then name the check and offer a switch (§3.3).
- Chocolatey package: built and wired into CI and the release (§3.2, [`docs/chocolatey.md`](docs/chocolatey.md)).
  Open:
  - the user's steps: the community.chocolatey.org account, the `CHOCOLATEY_API_KEY` secret, a stable tag, and
    the verifier-exemption answer in the first review;
  - the README's `choco install betterclipboard` line once approved;
  - in the app: hide Settings › *Add bclip to PATH* when running from a Chocolatey `lib` folder (the shim already
    puts `bclip` on the PATH).
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
    warnings). Find out when it happens (sleep and resume? a console busy printing?) before raising the deadline or
    logging it lower;
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
