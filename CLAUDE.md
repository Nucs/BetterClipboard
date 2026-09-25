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
| Is there an "atomic", never-miss clipboard subscription? | **No — none exists in user mode [verified, §1.9].** Win+V's service, WinRT `Clipboard.ContentChanged` and Chromium's new web `clipboardchange` event all sit on the same `WM_CLIPBOARDUPDATE` notification we use; every consumer reads the clipboard *after* being told. The legacy viewer chain is **not** synchronous either (producer's `CloseClipboard` returned in 0.01 ms while the viewer slept 150 ms). What we control: read immediately (no debounce) → copies ≥ 0.5 ms apart are all captured; faster bursts are counted, not silently lost. |
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
[`probe_sequence.cs`](tools/probes/probe_sequence.cs) (sequence-number bumps, delayed rendering, §1.9).
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
  rounds up to the 15.6 ms tick): 0 ms → 1 captured (the whole burst takes ~2 ms); ~0.1–0.3 ms → 3–47;
  **≥ 0.5 ms → 100/100** (99–100 at 1 ms); 15 ms → 100/100. The old 60 ms debounce would have merged
  anything < 60 ms apart into one item.
- **Isolation trick for tests/probes:** `CreateWindowStation(NULL, …)` (named stations need elevation) →
  `SetProcessWindowStation` → `CreateDesktop` → MTA threads call `SetThreadDesktop` before any other user32
  call (STA fails with `ERROR_BUSY`: COM's hidden window). The station has its own clipboard; the user's
  clipboard and Win+V history (RAM-only, unrecoverable) stay untouched.

---

## 2. BetterClipboard architecture

### 2.1 Solution layout

| Project | TFM | Role |
|---|---|---|
| [`src/BetterClipboard.Core`](src/BetterClipboard.Core) | `net10.0` | OS-agnostic heart: models (`Model/`), codecs + classifier + hashing (`Content/`), encrypted SQLite store + machine-bound store opener (`Storage/`), key hierarchy (`Security/`: UUIDv5, HKDF machine binding, sealed key vault), capture pipeline (`Services/ClipHistoryService`), command line (`Cli/`: protocol, pipe naming + framing, argument grammar, command processor, output — §2.9), settings, logging, presentation helpers. **CS1591 = error.** |
| [`src/BetterClipboard.Windows`](src/BetterClipboard.Windows) | `net10.0-windows10.0.26100.0` | Everything OS: `Interop/` (LibraryImport P/Invoke, `MessageWindowThread`), `Clipboard/` (listener/reader/writer, source attribution), `Input/` (hotkey + WH_KEYBOARD_LL takeover, paste injection, placement), `Imaging/` (DIB math + WIC, PNG export for the CLI), `Import/` (DPAPI-NG, pinned store, WinRT history), `Shell/` (tray icon, Run key, Windows clipboard/Explorer settings, user PATH), `Security/` (MachineGuid + SID, DPAPI key protector), `Cli/` (ACL'd named-pipe server). **CS1591 = error.** |
| [`src/BetterClipboard.Cli`](src/BetterClipboard.Cli) | `net10.0-windows` console | `bclip`: parses arguments, gates on the app's `EnableCommandLine`, talks to the running app over the pipe (starting it if needed), prints text/JSON with exit codes (§2.9). Published self-contained next to `BetterClipboard.exe`. **CS1591 = error.** |
| [`src/BetterClipboard.App`](src/BetterClipboard.App) | `net10.0-windows10.0.26100.0` WinUI 3 | Windows App SDK **2.5.1** as component packages (Base/Foundation/InteractiveExperiences/WinUI/DWrite — the metapackage's AI/ML/Search/Widgets add ~57 MB we don't use), unpackaged (`WindowsPackageType=None`), `WindowsAppSDKSelfContained=true`, custom `Program.Main` (single instance + commands). `AppController` = composition root. Views: `ClipboardFlyout` (acrylic Win+V replacement), `SettingsWindow` (Mica). |
| [`tests/BetterClipboard.Core.Tests`](tests/BetterClipboard.Core.Tests) | `net10.0` | xunit.v3 on Microsoft.Testing.Platform (157 tests: content, store, **encryption at rest**, key-hierarchy known-answer tests, CLI grammar/protocol/processor/output, one-time data fix-ups, password-manager catalog seeding). |
| [`tests/BetterClipboard.Windows.Tests`](tests/BetterClipboard.Windows.Tests) | `net10.0-windows…` | Hotkeys, interceptor, placement, DIB/WIC, DPAPI-NG, synthetic pinned store, real DPAPI/MachineGuid, **clipboard capture in a private window station** (bursts, watchdog, echo, delayed rendering), CLI pipe server (real pipes: refusal of a 2nd server, hang-up, malformed input, 124-connection stress: 100 sequential + 24 parallel) + CLI end-to-end through the real monitor, user-PATH rules, flyout drag tracker, opt-in real-clipboard round trip (62 tests). |
| [`tools/`](tools) | scripts | `probes/` (research), `e2e/` (UI harness — see §4), [`release/package.ps1`](tools/release/package.ps1) (release zips + SHA256SUMS, shared with CI), [`make_icon.py`](tools/make_icon.py) (app icon). |
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
   > rich-only) → image analysis (dims, 720×400 PNG thumbnail, **pixel hash** — images dedupe by decoded
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
thumbnail) · `clip_formats` (raw payloads by name, cascade) · `clips_fts` (FTS5 **trigram**, external
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
  `list`/`search` (`QueryAsync`, not pinned-first, 1–1000, `--since` = `UsedSince`); `grep` (.NET regex,
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

---

## 3. Build · run · test

```bash
dotnet build BetterClipboard.sln                               # everything (App builds win-x64)
dotnet test --solution BetterClipboard.sln                     # 219 tests (1 opt-in skipped)
BETTERCLIPBOARD_CLIPBOARD_TESTS=1 dotnet test --project tests/BetterClipboard.Windows.Tests   # + real clipboard
```

Do **not** add `-v q` to `dotnet test --solution` (Microsoft.Testing.Platform then reports "Zero tests
ran", exit 5). A test executable can also be run directly, e.g.
`tests/BetterClipboard.Windows.Tests/bin/Debug/net10.0-windows10.0.26100.0/BetterClipboard.Windows.Tests.exe -class <FQN> -showliveoutput`.
The solution build puts the app in `src/BetterClipboard.App/bin/x64/Debug/net10.0-windows10.0.26100.0/win-x64/`.

Run: plain launch = start + open Settings (or activate the running instance); `--background` = tray only
(used by "Start with Windows"); `--show-flyout` = open the flyout in the running instance; `--exit` =
graceful quit (drains queued captures). **The exe is locked while running — `--exit` before rebuilding.**
Isolated dev run: `BETTERCLIPBOARD_DATA_DIR=<scratch>/data BetterClipboard.exe --background` — a scoped
instance (§2.9), so `--exit` with the same variable set stops only it; **without** the variable, `--exit`
stops the user's installed app.

`bclip` from the solution build: `src/BetterClipboard.Cli/bin/Debug/net10.0-windows/bclip.exe`
(framework-dependent; it can auto-start only a `BetterClipboard.exe` in its own folder, so start a dev app
yourself). CLI end-to-end next to the user's app (2026-09-25): publish, seed an isolated store with
`BC-TEST` items only (settings: `EnableCommandLine` on, `ImportWindowsHistoryOnStartup` off,
`IsCapturePaused` on), export `BETTERCLIPBOARD_DATA_DIR`, run the published `bclip` (it auto-starts the
scoped instance), print only `BC-TEST` lines, `--exit` the scoped instance, and compare the list of
`BetterClipboard.exe` PIDs before/after — the user's must be unchanged.

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
- **Glyphs:** Segoe Fluent Icons via `\uE8xx` escapes in C# and `&#xE8xx;` in XAML (raw PUA characters are
  invisible in diffs). Code points used: Paste E77F, Copy E8C8, Pin E718, Unpin E77A, PinnedFill E842,
  Delete E74D, Setting E713, Link E71B, Photo E91B, Folder E8B7, Font E8D2, FontColor E8D3, Color E790,
  History E81C, Clock E917, Pause E769, Play E768, Keyboard E765, KeyboardShortcut EDA7, FileExplorer EC50,
  Personalize E771, Shield EA18, Import E8B5, Info E946, OpenInNewWindow E8A7, Code E943, Power E7E8.
- **Privacy in tooling:** never print clipboard content in probes/logs/test output; `tools/e2e/dbq.py`
  masks non-test rows. Test data is prefixed `BC-TEST`.
- **Clipboard tests never touch the real clipboard:** put them in the `IsolatedClipboardCollection`
  (private window station, see §1.9) and drive them with `ClipboardProducer`. Writing unmarked content to
  the real clipboard pushes items out of the user's RAM-only Win+V history — that loss is unrecoverable.
- **Gaps in timing tests:** busy-wait on a `Stopwatch` — `Thread.Sleep(n)` rounds up to the 15.6 ms tick.
- **Never disturb the user's installed app** (it runs in the same session as every experiment): test
  instances get their own `BETTERCLIPBOARD_DATA_DIR` (⇒ scoped lock/events/pipe, no LL hook), `--exit` is
  only ever sent with that variable set, and scripts record the `BetterClipboard.exe` PIDs before and
  check them after.
- **Starting long-lived processes from a console tool:** `UseShellExecute = true` — with `false` the child
  inherits the tool's stdout/stderr pipes and whoever reads them (a shell pipe, an AI agent) waits for EOF
  until that child exits.
- **Quote-dense scripts:** write them to a scratch file and run the file; big inline heredocs break the
  Bash tool's `eval` wrapper (`unexpected EOF while looking for matching '`).
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
| Unit tests | `dotnet test --solution` | 218 pass + 1 opt-in locally (non-elevated); CI (elevated runner) green. One-off: `ClientHangUp_CancelsHandler` exceeded its 5 s wait once in a full run right after a build (0 of 30 isolated and 0 of 6 further full runs failed) |
| Drag the flyout background to move it: header drag moves exactly (120, 60); no sticking after release; search-box drag doesn't move; Esc mid-drag restores and keeps it open | `tools/e2e/drag.py`, isolated instance, mouse | ✅ 4/4 checks, 4 consecutive runs (touch/pen untested) |
| Password-manager catalog: names normalized + unique, fresh/existing settings seeded, user entries kept (`keepass.EXE` covers `KeePass`), deletions stick, later catalog names arrive once, `settings.json` round trip | tests | ✅ |
| Settings › Ignored apps: scrollable list + *Add known password managers* | XAML builds | ⚠️ not visually verified (opening Settings would steal the user's focus) |
| `bclip` published build next to the user's running app: status (auto-starts the scoped instance), list, search, grep, get (exact bytes, Hebrew/✓, HTML fragment, file list), image → exit 2 without `-o` / PNG with `-o`, `--json`, pin + pinned filter, `--since`, wait timeout (1), not found (1), usage (2), access off (3, starts nothing); audit log; no LL hook; user's PID unchanged | isolated seeded store + `run.sh` | ✅ (after the ShellExecute fix) |
| `put`/`copy` write the clipboard, `wait` sees the next copy | CLI end-to-end tests in the isolated window station | ✅ |
| `install.ps1 -AddToPath` / uninstall PATH helpers | AST-loaded functions on a scratch key, PS 5.1 + 7 | ✅ (15/15) |
| "Windows clipboard history" source label gone (importer, caption, one-time fix-up of v0.1.0 rows) | tests | ✅ (the running v0.1.0 install still shows it until updated) |
| Encryption at rest: no plaintext in db/WAL, wrong key ⇒ unreadable, in-place migration, quarantine | tests + real app on a copy of a v0.1 dir | ✅ (0 of 16 entries lost) |
| Capture: 25 copies 20 ms apart all captured; 100 copies ≥ 0.5 ms apart all captured; bursts accounted | isolated window station | ✅ (10/10 runs green) |
| Watchdog recovers a deaf listener; own writes ignored; delayed rendering | isolated window station | ✅ |
| `DisabledHotkeys=V` ⇒ `RegisterHotKey` path; restore ⇒ Explorer owns Win+V again | real Explorer restarts | ✅ |
| Release zips (x64 + ARM64 native DLLs), published app starts (WinUI window) and exits | `package.ps1` + launch | ✅ |
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
- An MCP server (stdio) speaking the same pipe protocol, so agents get typed tools instead of shelling out
  to `bclip`; `bclip` itself could gain `--null`-separated output and `get --all-formats` export.
- Day grouping, collections/favorites, snippets, OCR for images (`Windows.Media.Ocr`), paste transforms
  (trim, case, JSON pretty), large preview pane, drag-out, sync between PCs, MSIX packaging.
