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
[`probe_shellhistory.cs`](tools/probes/probe_shellhistory.cs) (PowerShell and cmd command history, §2.16).
Run C# probes with `dotnet run tools/probes/<name>.cs`, Python ones with `python tools/probes/<name>.py`.

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
| [`src/BetterClipboard.Core`](src/BetterClipboard.Core) | `net10.0` | OS-agnostic heart: models (`Model/`), codecs + classifier + hashing + path detector (`Content/`, §2.13), encrypted SQLite store + machine-bound store opener (`Storage/`), key hierarchy (`Security/`: UUIDv5, HKDF machine binding, sealed key vault), capture pipeline (`Services/ClipHistoryService`), command line (`Cli/`: protocol, pipe naming + framing, argument grammar, command processor, output — §2.9), Win+R list logic (`Integrations/RunMru`: parse, fingerprints, runs since a snapshot — §2.17), settings, logging, presentation helpers. **CS1591 = error.** |
| [`src/BetterClipboard.Windows`](src/BetterClipboard.Windows) | `net10.0-windows10.0.26100.0` | Everything OS: `Interop/` (LibraryImport P/Invoke, `MessageWindowThread`), `Clipboard/` (listener/reader/writer, source attribution), `Input/` (hotkey + WH_KEYBOARD_LL takeover, paste injection, placement), `Imaging/` (DIB math + WIC, PNG export for the CLI), `Import/` (DPAPI-NG, pinned store, WinRT history), `Shell/` (tray icon, Run key, Windows clipboard/Explorer settings, user PATH, running a command like Win+R), `Security/` (MachineGuid + SID, DPAPI key protector), `Cli/` (ACL'd named-pipe server), `Integrations/` (ShareX: locator, folder-pattern rules, screenshot watcher, integration life cycle — §2.10; Win+R history: `RunMRU` reader, change watch, integration life cycle — §2.17). **CS1591 = error.** |
| [`src/BetterClipboard.Cli`](src/BetterClipboard.Cli) | `net10.0-windows` console | `bclip`: parses arguments, gates on the app's `EnableCommandLine`, talks to the running app over the pipe (starting it if needed), prints text/JSON with exit codes (§2.9). Published self-contained next to `BetterClipboard.exe`. **CS1591 = error.** |
| [`src/BetterClipboard.App`](src/BetterClipboard.App) | `net10.0-windows10.0.26100.0` WinUI 3 | Windows App SDK **2.5.1** as component packages (Base/Foundation/InteractiveExperiences/WinUI/DWrite — the metapackage's AI/ML/Search/Widgets add ~57 MB we don't use), unpackaged (`WindowsPackageType=None`), `WindowsAppSDKSelfContained=true`, custom `Program.Main` (single instance + commands). `AppController` = composition root. Views: `ClipboardFlyout` (acrylic Win+V replacement), `SettingsWindow` (Mica). |
| [`tests/BetterClipboard.Core.Tests`](tests/BetterClipboard.Core.Tests) | `net10.0` | xunit.v3 on Microsoft.Testing.Platform (494 tests, one class at a time — §4: Win+R list logic (parse, fingerprint known answers, runs since a snapshot for every kind of list change, planned captures), the run column, Run filter, merge rules and tombstones of both Win+R origins, a store from before the column, pause/ignore/Forget forever for runs, `-f run`; paths copied as text (a 234-case detector corpus: every form, prose, commands, URLs, escapes, whitespace; Files/Text filters; backfill and rules version), content, store, **encryption at rest**, key-hierarchy known-answer tests, CLI grammar/protocol/processor/output, one-time data fix-ups, password-manager catalog seeding, ShareX origin/filter/state semantics, groups: CRUD, membership filter, kept-like-pinned retention, reset clock, schema added to an older store, service events, icon catalog; Forget forever: fingerprint normalization, known answers and chunking, the look-alike sweep, list life cycle, blocking across channels, Settings wording). |
| [`tests/BetterClipboard.Windows.Tests`](tests/BetterClipboard.Windows.Tests) | `net10.0-windows…` | Hotkeys, interceptor, placement, DIB/WIC, DPAPI-NG, synthetic pinned store, real DPAPI/MachineGuid, **clipboard capture in a private window station** (bursts, watchdog, echo, delayed rendering), CLI pipe server (real pipes: refusal of a 2nd server, hang-up, malformed input, 124-connection stress: 100 sequential + 24 parallel) + CLI end-to-end through the real monitor, user-PATH rules, flyout drag tracker, ShareX (pattern rules, locator against fake ShareX layouts, screenshot watcher on temp folders, integration marker life cycle over a real history), groups column growing/shrinking on the left, Forget forever end to end (a real copy of forgotten text is read and kept out), opt-in real-clipboard round trip, explicit capture-rate measurement, Win+R history on scratch HKCU keys (reader, settle wait, change watch incl. a key that appears later, integration: first import, live runs, re-runs, restart catch-up, off/on, pause, a missing list, keeping more than Windows' 26, a rescan racing the watch, runs from the panel) and Run-dialog parsing + hidden launches (169 tests with the Everything work's). |
| [`tools/`](tools) | scripts | `probes/` (research), `e2e/` (UI harness — see §4), [`release/package.ps1`](tools/release/package.ps1) (release zips + SHA256SUMS, shared with CI), [`release/install-local.ps1`](tools/release/install-local.ps1) (installs those zips on this PC with the real installer before a release, §3.1), [`launch_dev.py`](tools/launch_dev.py) (runs a copy of the dev build next to the installed app for the user to try, §3), [`make_icon.py`](tools/make_icon.py) (app icon). |
| [`install.ps1`](install.ps1), [`.github/workflows/`](.github/workflows) | PowerShell / Actions | Installer from GitHub releases (§3.1) · CI (build, test, package) · release on `v*` tags. |

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

"Win+R history watch" thread (only while Settings › Win+R history is on) ── RegNotifyChangeKeyValue on RunMRU
 (or its parent while missing) → Changed → 150 ms debounce → rescan on the pool under one gate: read once the key
 is quiet 150 ms → runs since the snapshot → ImportAsync / AddAsync (the worker) → snapshot (state.runmru.snapshot),
 §2.17. Entering the Run tab rescans too. Running a command: a short-lived STA thread per ShellExecuteEx.
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
  there they pan it.
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
copy lifts the tombstone) · `meta.last_clear_utc` (imports older than the last clear are skipped).
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
  start — a re-copy of an existing item; 1–3600 s); `status` (version, counts, capture statistics).
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
  `ClipQuery.GroupId` filters with `EXISTS`, and it combines with filter tabs and search.
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
  summon always starts in the regular view, like the filter tabs.
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
  - Card: "Remove from Name" in a group view, plus a *Groups* submenu of toggles (the keyboard and
    screen-reader route).
  - Group icon: Rename… (flyout), Change icon… (the picker), Delete group (confirm; it says the items
    stay).
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

### 2.14 voidtools Everything — verified IPC facts for an integration (2026-10-01; nothing built yet)

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
    Everything exits, but it is complete while Everything is not running.
  - The history came back after a restart.
- **The typed searches:** `Search History.csv` holds search, count and last search date as a FILETIME
  **[docs/forum]**.
  - It is saved only when a search window closes, and there is no IPC for it.
  - Our IPC queries never produced a search-history file, not even in 1.5 where it is on.
- **Tab bar:** the 7 tabs take 351–366 of 384 px (§2.10). An "Everything" tab needs ~83 px (a Pillow estimate
  calibrated on the ShareX tab: 60 px estimated vs 61 measured), so the bar would be 50 px over; even "Runs"
  is 15 px over.
  - All 8 tabs fit only at 4–5 px item padding (from 9), or with a wider flyout or icon tabs.

**Proposal.** The ranked options are in §6. Everything only answers *where* a path is and *whether* it exists;
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

### 2.16 PowerShell and cmd.exe history — verified facts for "Pwsh"/"Cmd" tabs (2026-10-01; nothing built yet)

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
- Run tab (`RunFilter`, a text tab after ShareX) while the setting is on; the window widens when the visible tabs
  need more than the bar (the Everything work's `MeasureTabsExtraDip`). Footer: `FlyoutViewModel.KeyHint` = "↵
  paste · Ctrl+↵ run · Ctrl+⇧↵ run as admin" in the Run tab, status "N commands kept". Empty state explains
  both setting states.
- Settings card status: "Watching Windows' Win+R list: Windows remembers N of its 26 commands." plus runs
  recorded this session.
- `bclip list -f run` (also `runs`): origins `run` / `run-history`, JSON `lastRun`.

**Not built (yet):** the modern Run dialog's own history (its location cannot be verified: 26200 GA has no
modern dialog); an opt-in delete-through to `RunMRU`.

---

## 3. Build · run · test

```bash
dotnet build BetterClipboard.sln                               # everything (App builds win-x64)
dotnet test --solution BetterClipboard.sln                     # 663 tests (660 run; opt-in tests + 1 explicit measurement skipped)
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

### 3.1 Release & install

- **Package:** [`tools/release/package.ps1`](tools/release/package.ps1) `-Version X.Y.Z` → for win-x64 and
  win-arm64: `dotnet publish -c Release -r win-<arch> --self-contained true -p:Platform=<x64|ARM64>
  -p:DebugType=none` (no .NET or WinAppSDK runtime needed on the target), then `bclip` published
  self-contained into the same folder (it shares the app's runtime files: the x64 zip grew by 0.17 MB) + `install.ps1`, `LICENSE`,
  `THIRD-PARTY-NOTICES.md` at the zip root → `BetterClipboard-X.Y.Z-win-<arch>.zip` (~70 MB, ~180 MB
  unpacked) + `SHA256SUMS.txt` (sha256sum format, LF). The same script runs in CI and in the release job.
- **Release:** push an **annotated** tag `vX.Y.Z` whose message is the release notes (`git tag -a vX.Y.Z -F
  notes.md`) → [`.github/workflows/release.yml`](.github/workflows/release.yml) tests, packages, and
  `gh release create --notes-from-tag` with both zips, `SHA256SUMS.txt` and `install.ps1`. Tags with a
  pre-release suffix (`-rc.1`) become pre-releases. CI ([`ci.yml`](.github/workflows/ci.yml)) builds,
  tests and packages x64 on every push/PR.
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
  needs the user's request. Procedure used for 0.2.4 (2026-10-01):
  1. `pwsh tools/release/package.ps1 -Version X.Y.Z` (both architectures; 84 s).
  2. `env -u GH_TOKEN -u GITHUB_TOKEN -u GH_DEBUG -u BETTERCLIPBOARD_DATA_DIR -u BETTERCLIPBOARD_SHAREX_DIR
     -u PSModulePath powershell -NoProfile -ExecutionPolicy Bypass -File tools/release/install-local.ps1
     -Version X.Y.Z -NoLaunch` (Windows PowerShell 5.1, like `irm | iex` users).
  3. Start the app through Explorer: a temporary `.lnk` with `--background`, opened by `explorer.exe`.
     The app then gets the user's environment, not this shell's (`CLAUDECODE`, `MSYSTEM` must be absent).
  4. Check the file version, `installer.json`, the Run entry, the Installed-apps `DisplayVersion`, and the
     log after "starting": 0 WRN/ERR.
  - Tested against a stub installer in PS 5.1 and 7: served lookup, a zip copy matching `SHA256SUMS.txt`,
    `-NoLaunch` passed on, other lookups refused, a clear error for a missing package, and the real cmdlets
    back afterwards.
  - Bump commits keep `Directory.Build.props` alone, and their message carries the drafted tag notes
    (`git log -1 --format=%B`) until the release.

---

## 4. Conventions (must follow)

- **Documentation is mandatory** (user rule): every member gets XML docs whose summary explains
  consequence/tradeoff; every param/return/exception documented; non-obvious bodies get *why* comments.
  Core and Windows fail the build on CS1591.
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
  Blocked E733 (Forget forever; a circle with a slash, checked by rendering).
  Group icons: `Core/Presentation/GroupIconCatalog`. Raw PUA characters slip into sources easily: twice
  on 2026-09-25 they landed in string literals, once a raw U+2009 thin space did. Sweep new C# files with an
  escape script before committing (never XAML files: there the escape is `&#xE8xx;`).
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
  (2026-09-25). Put escape sequences in with the Edit tool. **Never with `sed`:** GNU sed reads `\u` in a
  replacement as "uppercase the next character", so `s/x/"\\uE768"/` wrote `"E768"` (2026-10-01). A Python
  script that builds the backslash with `chr(92)` is safe too.
- **The real Win+R list is user data.** Tests and isolated e2e instances point the integration at a scratch key
  through `BETTERCLIPBOARD_RUNMRU_KEY` (under `HKCU\Software\BetterClipboard-Tests` or `-E2E`, deleted
  afterwards), or switch `RecordRunHistory` off; without either, an instance imports and watches the user's
  `RunMRU` (the user's own `launch_dev.py` copy does so on purpose, §3). Never print a real command.
- **Registry watch tests: one parent key per test.** While a key is missing, the watch observes its parent, and
  test classes run in parallel: with a shared parent, another test's keys coming and going raised spurious
  changes (6 of 6 runs), and one test's cleanup could delete the parent under another's watch.
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
- Commits: per the user's global rules (message file in scratchpad, `git add` + `git commit` in one
  command, extensive messages, never amend).

---

## 5. Verification status (2026-09-25, Windows 11 25H2 26200.8875, 1920×1080 @100 %)

| Feature | How | Result |
|---|---|---|
| Run tab, headless, on an isolated instance next to the user's app (2026-10-01; a scratch Win+R key with BC-TEST commands through `BETTERCLIPBOARD_RUNMRU_KEY`, capture not paused, the scratch store deleted afterwards). The first activation imported 3 of 3 (origin `run-history`, newest first). A live run was listed by `bclip list -f run` 295–703 ms after the write (polling included), origin `run`. A re-run moved to the top. 30 runs 400 ms apart: Windows' list held 26, the Run tab all 34, including the evicted "fill 01". JSON has `lastRun`, source `Win+R`. User's PID unchanged | `run_e2e/run.sh` (scratch) | ✅ (30 runs 50 ms apart were one batch: 26 of 30, the oldest 4 evicted before the first read — a scripted-burst limit, §6) |
| Run tab UI on the same kind of instance. The tab shows after ShareX. Entering it lists "Win+R command: …" cards (command-prompt glyph, "Win+R · just now"); the footer reads "↵ paste · Ctrl+↵ run · Ctrl+⇧↵ run as admin" and "34 commands kept". Ctrl+Enter, sent only while the test panel was the foreground window, on a `wscript` BC-TEST command ran it (marker file written), hid the panel, and moved the entry to the top with a new run time. Log: "Ran entry 3 as a Win+R command: Started." (no command text) | UIA select + one guarded key chord (`run_e2e/ui.sh`) | ✅ (twice). Lesson: take the test PID from `Start-Process -PassThru` — "the new `BetterClipboard.exe`" was once another agent's short-lived process, and UIA then found "no window" |
| Tab bar with the ShareX and Run tabs (8 tabs): the window grows to 421 px, "Run" gets its full 43 px with ~4 px to spare | UIA rects + a guarded screenshot of the committed build (`4c91613`) | ✅ Earlier builds (15:50, 16:06) clipped it to "Rur" at 405 px. Cause: the widening ignored the window's 14-DIP invisible frame; the Everything work's `WindowFrameDip` fixed it before that commit |
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
| Unit tests | `dotnet test --solution` | 660 pass + 3 skipped (opt-in, explicit measurement) locally (2026-10-01, non-elevated, with the Run tab and the Everything work in progress): 5 of 5 full runs green; one earlier full run right after a build failed `QuickSuccessiveCopies_AreAllCaptured` once (5.5 s under load; 5 of 5 green alone). Core alone: 0 of 30 runs failed after making it run one class at a time; before, 4 of 25 failed with a pooled-connection `ObjectDisposedException` (§4). Earlier: CI (elevated runner) green; one-off `ClientHangUp_CancelsHandler` exceeded its 5 s wait once in a full run right after a build (0 of 30 isolated and 0 of 6 further full runs failed) |
| Settings › Shortcut box shows the saved shortcut (custom and preset); preset menu saves; invalid text shows the error and saves nothing; typed text saved canonically; menu labels canonical | screenshots + guarded input on an isolated instance | ✅ (fixed after v0.2.0, where the box was blank) |
| Drag the flyout background to move it: header drag moves exactly (120, 60); no sticking after release; search-box drag doesn't move; Esc mid-drag restores and keeps it open | `tools/e2e/drag.py`, isolated instance, mouse | ✅ 4/4 checks, 4 consecutive runs (touch/pen untested) |
| Password-manager catalog: names normalized + unique, fresh/existing settings seeded, user entries kept (`keepass.EXE` covers `KeePass`), deletions stick, later catalog names arrive once, `settings.json` round trip | tests | ✅ |
| Settings › Ignored apps: scrollable list + *Add known password managers* | XAML builds | ⚠️ not visually verified (opening Settings would steal the user's focus) |
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
- Code signing (SmartScreen reputation), winget manifest, in-app update check against GitHub releases.
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
  - Snipping Tool / Greenshot folders with the same watcher.
- voidtools Everything integration (proposal 2026-10-01, facts in §2.14), most value first:
  1. **Client:** `Integrations/Everything`, with WM_COPYDATA on a `MessageWindowThread`. Owner check, state
     check (401/402), a deadline per query and latest-wins per reply window. The query builder goes in Core:
     pure, normalizing (slashes, trailing separators, `\\?\`, `file:`, `%VAR%`, `~`, `/c/`, `/mnt/c/`) and
     name-led. Tests against a fake IPC window (our own window class) plus an opt-in real portable Everything.
  2. **Path cards and file lists:** found / missing / moved states (one name-led batch per page, cached).
     Actions: *Paste the file* (`CF_HDROP`), *Show in Explorer*, *Open*, *Show in Everything*. Relative paths
     (`src/x.cs`) resolve to the real file, with a picker when several match. Absolute paths are checked
     without touching the disk or a dead share.
  3. **"Files on this PC" in the flyout's search:** paste a file you never copied.
  4. **`bclip resolve <id>`** for agents.
  5. **An "Everything" tab with what you picked there** (asked for 2026-10-01; facts in §2.14, run history):
     - Picks come from a live `runcount:` query by date run (~1 ms when the flyout opens), or from
       `Run History.csv` while Everything is not running.
     - Next to them, copies made in Everything, via a source-app filter like the ShareX tab's.
     - Open choices: a live view (Everything owns the data; a rename drops a pick) or stored entries like
       ShareX screenshots (searchable, pinnable; a catch-up marker on the date run); and room in the tab bar.
  - Not planned: classifying by the index (verdicts must stay deterministic), imports from the index journal,
    and bundling Everything or the SDK DLLs.
- Run tab (Win+R history): **built 2026-10-01, §2.17.** Next steps:
  - the modern Run dialog's own history once a build that has it can be checked (CmdPal keeps `state.json`
    `RunHistory` and never writes `RunMRU`, §2.15);
  - an opt-in delete-through to `RunMRU` (never writing BetterClipboard's history back into it);
  - `bclip run <id>` for scripts and agents, with the same "only commands run before" rule;
  - a maximum wait for the watch's debounce: scripted bursts (writes < 150 ms apart) are read as one batch, and
    with more than 26 in one burst the oldest are lost (measured: 26 of 30 at 50 ms; 30 of 30 at 400 ms).
- "Pwsh" and "Cmd" tabs (asked for 2026-10-01; facts in §2.16):
  - **PowerShell, store don't mirror:**
    - **Watch and read:** watch the PSReadLine folder (`*_history.txt`) and read the new bytes from a stored
      offset (`FileShare.ReadWrite | Delete`, never the mutex). Take complete records only and decode the
      backtick breaks.
    - **Marker:** one per file in `state.pwsh.<host>`, holding the offset plus a hash of the bytes before it. A
      shorter or different file means a resync from 0; the content hash dedupes.
    - **Times:** a new record's time is accurate (written at Enter). The backlog has order only, so import it
      once as an import origin with synthesized times, like the Run tab.
    - **Privacy:** opt-in, with a secret filter before storing (2.0.0's regex plus `ghp_`, `github_pat_`, `sk-`,
      `AKIA`, `xox?-`, Bearer, `-p<pass>`, `curl -u`). Forget forever and pause apply, and the file is never
      written. Settings must say that deleting `ConsoleHost_history.txt` does not delete BetterClipboard's copy.
    - **Keys:** Enter pastes; no "run" (no safe target for an arbitrary history line).
  - **cmd, best effort:**
    - **When:** on tab entry, and every 30–60 s while interactive cmd windows exist.
    - **How:** a helper process per console reads its 50 entries, after tool-started consoles are filtered out
      by parent. Diff per console (PID + start time) with the Run tab's rule.
    - **Limits:** commands from a window closed between scans are lost.
    - **Clink:** when installed, its file takes the PowerShell path.
    - **Or** no Cmd tab, with a hint about Clink.
  - **Git Bash:** `~/.bash_history` the PowerShell way; it arrives at shell exit.
  - **Tab bar:** one "Commands" tab with chips (Win+R, PowerShell, cmd, Bash) instead of four tabs.
- An MCP server (stdio) speaking the same pipe protocol, so agents get typed tools instead of shelling out
  to `bclip`; `bclip` itself could gain `--null`-separated output and `get --all-formats` export.
- Day grouping, collections/favorites, snippets, OCR for images (`Windows.Media.Ocr`), paste transforms
  (trim, case, JSON pretty), large preview pane, drag-out, sync between PCs, MSIX packaging.
