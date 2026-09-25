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
| Where is it hosted? | Backend: per-user service `cbdhsvc_<LUID>` in `svchost.exe -k ClipboardSvcGroup`. UI: [`TextInputHost.exe`](file:///C:/Windows/SystemApps/MicrosoftWindows.Client.CBS_cw5n1h2txyewy/TextInputHost.exe) (package `MicrosoftWindows.Client.CBS_cw5n1h2txyewy`). Hotkey: `explorer.exe` **[verified]**. |
| Reverse-engineerable? | Yes, for **interoperability** (registry, WinRT registrations, on-disk format, strings, public PDBs on the Microsoft symbol server). The Windows EULA forbids RE beyond what law permits, and copying their code would be pointless anyway. We did black-box/interop observation only (no disassembly) and re-implement on **public** Win32/WinRT APIs. |
| Why does it forget on restart? | By design: unpinned items live in the service's **memory** (`ClipboardHistoryBuffer`); only **pinned** items are written to disk (DPAPI-NG encrypted) **[verified]**. After this morning's boot the WinRT history held just 3 new items + 1 pinned one. |
| Can we take over? | **Yes.** (1) Capture ourselves via `AddClipboardFormatListener`; (2) persist in SQLite; (3) hijack `Win+V` with a low-level keyboard hook (or `DisabledHotkeys=V` + Explorer restart); (4) import Windows' current history (WinRT API works from a **background unpackaged** process **[verified]**) and its pinned items (decryptable with `NCryptUnprotectSecret` as the same user **[verified]**). |

---

## 1. How Windows clipboard history (Win+V) actually works

### 1.1 Component map **[verified]**

| Layer | Binary / location | Role | Tech |
|---|---|---|---|
| Hotkey owner | [`explorer.exe`](file:///C:/Windows/explorer.exe) | Registers `Win+V` (telemetry event `ClipboardHistoryHotkeyRegistration`), activates `ClipboardHistoryServer`, reads policy `AllowClipboardHistory`/`AllowCrossDeviceClipboard` | C++ |
| Hotkey router | [`twinui.pcshell.dll`](file:///C:/Windows/System32/twinui.pcshell.dll) | `ShellHotKeyRequestReceived` → shows `SuggestionUIClipboardHistory` | C++ |
| UI host | [`TextInputHost.exe`](file:///C:/Windows/SystemApps/MicrosoftWindows.Client.CBS_cw5n1h2txyewy/TextInputHost.exe) ("Windows Input Experience", package `MicrosoftWindows.Client.CBS` v1000.26100.334.0) | Hosts the emoji/GIF/kaomoji/symbols/**clipboard** panel | XAML |
| UI module | [`WindowsInternal.ComposableShell.Experiences.SuggestionUIUndocked.dll`](file:///C:/Windows/SystemApps/MicrosoftWindows.Client.CBS_cw5n1h2txyewy/WindowsInternal.ComposableShell.Experiences.SuggestionUIUndocked.dll) (v2605.22000.400.0) + `.winmd` | `ClipboardAdapter` / `ClipboardHistoryItem` (pin/unpin/delete/select/upload, `GetMaxPinnedClipboardHistoryItemsAsync`, `PasswordFieldClipboardHistory`) | **C++/CX** (hat `^` types, PPL tasks) |
| UI → service wrapper | [`windowsudk.shellcommon.dll`](file:///C:/Windows/System32/windowsudk.shellcommon.dll) ("Windows **Undocked Dev Kit** Shellcommon") | In-proc WinRT classes `WindowsUdk.ApplicationModel.DataTransfer.{ClipboardHistory, ClipboardHistoryItem, ClipboardSettings, ClipboardViewManager, ClipboardHistoryPromotionManager}` | WinRT |
| Public API | [`windows.applicationmodel.datatransfer.dll`](file:///C:/Windows/System32/windows.applicationmodel.datatransfer.dll) | `Windows.ApplicationModel.DataTransfer.Clipboard` (+ `ClipboardContentOptions`, internal `ClipboardPolicy`) → talks to the OOP server | WinRT |
| **Backend service** | [`cbdhsvc.dll`](file:///C:/Windows/System32/cbdhsvc.dll) ("Microsoft (R) Clipboard History", 10.0.26100.8117) in `svchost.exe -k ClipboardSvcGroup -p` | Out-of-proc WinRT server **`CBDHSvc`** (`ServerType=2` service) hosting `Windows.ApplicationModel.Internal.DataTransfer.{ClipboardHistoryServer, ClipboardBrokerProvider, ClipboardHistoryItemInternal, ClipboardOperationAppInfo, ClipboardSettings, ClipboardSettingsProvider, ClipboardSignalProducer, ClipboardViewManager}` + `WindowsInternal.SmartActionPlatform.SmartClipboardProxy` | C++ (WRL/WIL, `Windows.Data.Json`, `Windows.Storage.Compression`, `DataProtectionProvider`) |
| Cloud sync | [`cdprt.dll`](file:///C:/Windows/System32/cdprt.dll) (Connected Devices Platform runtime) | `Windows.ApplicationModel.Internal.DataTransfer.{CloudClipboard, ClipboardChannel}` — Microsoft-account sync | C++ |
| Smart actions / AI | [`SmartActionPlatform.dll`](file:///C:/Windows/System32/SmartActionPlatform.dll), [`TaskFlowDataEngine.dll`](file:///C:/Windows/System32/TaskFlowDataEngine.dll) | `SmartClipboard` suggested actions, `ClipboardSignalListener`; cbdhsvc also references `Microsoft.Windows.AugLoop.CBS` packages **[inferred: AI "augmentation loop" features]** | — |

Service registration **[verified]**: template `cbdhsvc` (Type `0x60` = `USER_SHARE_PROCESS | TEMPLATE`,
auto-start delayed, `UserServiceFlags=2`, `RequiredPrivileges=SeImpersonatePrivilege`) → per-user
instance `cbdhsvc_6f5e93540` (suffix = logon-session LUID). `ServiceDll = %SystemRoot%\System32\cbdhsvc.dll`.
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
restart/sign-out **[community-reported, not yet tested here]** (value absent on this machine).

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
  fragile `SetClipboardViewer` chain), `GetClipboardSequenceNumber`, `OpenClipboard` (fails while another
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
[`tools/probes/`](file:///K:/source/BetterClipboard/tools/probes) — re-run them after Windows updates
(they print metadata only, never clipboard content):
[`probe_dpapi.cs`](file:///K:/source/BetterClipboard/tools/probes/probe_dpapi.cs) (pinned-blob decryption),
[`probe_hotkeys.cs`](file:///K:/source/BetterClipboard/tools/probes/probe_hotkeys.cs) (hotkey ownership),
[`probe_history.cs`](file:///K:/source/BetterClipboard/tools/probes/probe_history.cs) (WinRT history from background),
[`winrt_classes.py`](file:///K:/source/BetterClipboard/tools/probes/winrt_classes.py) (WinRT class hosting),
[`binary_strings.py`](file:///K:/source/BetterClipboard/tools/probes/binary_strings.py) (strings of a binary).
Run C# probes with `dotnet run tools/probes/<name>.cs`, Python ones with `python tools/probes/<name>.py`.

---

## 2. BetterClipboard architecture

### 2.1 Solution layout

| Project | TFM | Role |
|---|---|---|
| [`src/BetterClipboard.Core`](file:///K:/source/BetterClipboard/src/BetterClipboard.Core) | `net10.0` | OS-agnostic heart: models (`Model/`), codecs + classifier + hashing (`Content/`), SQLite store (`Storage/`), capture pipeline (`Services/ClipHistoryService`), settings, logging, presentation helpers. **CS1591 = error.** |
| [`src/BetterClipboard.Windows`](file:///K:/source/BetterClipboard/src/BetterClipboard.Windows) | `net10.0-windows10.0.26100.0` | Everything OS: `Interop/` (LibraryImport P/Invoke, `MessageWindowThread`), `Clipboard/` (listener/reader/writer, source attribution), `Input/` (hotkey + WH_KEYBOARD_LL takeover, paste injection, placement), `Imaging/` (DIB math + WIC), `Import/` (DPAPI-NG, pinned store, WinRT history), `Shell/` (tray icon, Run key, Windows clipboard/Explorer settings). **CS1591 = error.** |
| [`src/BetterClipboard.App`](file:///K:/source/BetterClipboard/src/BetterClipboard.App) | `net10.0-windows10.0.26100.0` WinUI 3 | Windows App SDK **2.5.1**, unpackaged (`WindowsPackageType=None`), `WindowsAppSDKSelfContained=true`, custom `Program.Main` (single instance + commands). `AppController` = composition root. Views: `ClipboardFlyout` (acrylic Win+V replacement), `SettingsWindow` (Mica). |
| [`tests/BetterClipboard.Core.Tests`](file:///K:/source/BetterClipboard/tests/BetterClipboard.Core.Tests) | `net10.0` | xunit.v3 on Microsoft.Testing.Platform (99 tests). |
| [`tests/BetterClipboard.Windows.Tests`](file:///K:/source/BetterClipboard/tests/BetterClipboard.Windows.Tests) | `net10.0-windows…` | Hotkeys, interceptor, placement, DIB/WIC, DPAPI-NG, synthetic pinned store, opt-in real-clipboard round trip (40 tests). |
| [`tools/`](file:///K:/source/BetterClipboard/tools) | scripts | `probes/` (research), `e2e/` (UI harness — see §4), [`make_icon.py`](file:///K:/source/BetterClipboard/tools/make_icon.py) (app icon). |

Shared build config: [`Directory.Build.props`](file:///K:/source/BetterClipboard/Directory.Build.props) (docs on,
nullable, version), [`Directory.Packages.props`](file:///K:/source/BetterClipboard/Directory.Packages.props)
(central package versions), [`global.json`](file:///K:/source/BetterClipboard/global.json) (SDK 10.0.1xx +
`"test": {"runner": "Microsoft.Testing.Platform"}` — required: xunit.v3 4.x no longer supports VSTest on .NET 10).

### 2.2 Runtime topology (threads)

```text
UI thread (WinUI DispatcherQueue) ── AppController, ClipboardFlyout, SettingsWindow, view models
   ▲ TryEnqueue                     ▲ TryEnqueue              ▲ TryEnqueue            ▲ TryEnqueue
"Clipboard" STA msg-window     "Input" msg-window          "Tray" hidden top-level  "Commands" thread
 AddClipboardFormatListener     RegisterHotKey / LL hook    Shell_NotifyIcon v4      named events from
 read/write clipboard           (hook callback = decide,    TaskbarCreated re-add    2nd launches
 → ClipCapture                  tap mask key, post, return)                          (--show-flyout/--exit)
   │ TryEnqueueCapture
   ▼
History worker (single consumer Channel) ── classify → WIC analyze (thumbnail + pixel hash) → SQLite upsert → prune → Changed event
```

Rules: nothing heavy on the hook thread (Windows silently drops slow LL hooks); the clipboard thread only
copies bytes then closes the clipboard ASAP; all writes are serialized through the history worker; reads
(`QueryAsync` etc.) hit SQLite directly (WAL) from the thread pool.

### 2.3 Capture pipeline

1. `WM_CLIPBOARDUPDATE` → snapshot **source attribution now** (owner window, else foreground window —
   short-lived producers like scripts exit before the debounce ends) → 60 ms debounce timer.
2. Skip if sequence number unchanged or equals our own last write (echo suppression).
3. `OpenClipboard` with timer-based retries (40 ms × 12). **Privacy markers checked before reading any
   content**: `ExcludeClipboardContentFromMonitorProcessing`, `Clipboard Viewer Ignore`,
   `CanIncludeInClipboardHistory` = 0 (unreadable value ⇒ treated as exclude).
4. Portable formats first (text, file list + drop effect, HTML, RTF, URL, PNG, the *original* DIB flavor),
   app-private formats only with `PreserveAllFormats`, all under a byte budget checked via `GlobalSize`
   **before** copying. GDI handle formats and synthesized duplicates are never stored.
5. Worker: rules (pause, ignored apps, size) → `ContentClassifier` (Files > Text[Link/Color/Rich] > Image
   > rich-only) → image analysis (dims, 720×400 PNG thumbnail, **pixel hash** — images dedupe by decoded
   pixels, so DIB-vs-PNG encodings of one picture merge) → `ClipStore.Upsert` → retention prune.

### 2.4 Win+V takeover (`Input/HotkeyService`)

`RegisterHotKey` first; on `ERROR_HOTKEY_ALREADY_REGISTERED` (Win+V ⇒ Explorer) and
`UseKeyboardHookFallback`, a `WH_KEYBOARD_LL` hook runs `HotkeyInterceptor`: exact-modifier match only,
swallow key-down/repeats/key-up of `V`, tap unassigned VK `0xE8` so releasing Win doesn't open Start, post
to the input window, return. Our own synthetic events carry `dwExtraInfo = 0x0B0CC11B` and are ignored;
keys injected by *other* tools (PowerToys KBM, AutoHotkey) are intercepted like physical keys. Quitting the
app unhooks → Win+V instantly returns to Windows (verified with `probe_hotkeys.cs`). Optional
"Release Win+V from Explorer" writes `DisabledHotkeys` + restarts Explorer (behind a confirmation dialog;
**not yet verified on this machine**).

### 2.5 Summon & paste flow

Hotkey → `ForegroundContext.Capture()` on the input thread (target HWND + Win32 caret via
`GetGUIThreadInfo` + cursor) → UI: flyout shown **first** (focus to search box; keystrokes must never leak
into the app below), then list reloads and the first card is selected → Enter/click →
`ReplayFormats.Prepare` (plain text strips formats; file lists as plain text become paths; a stored
"cut" drop effect is rewritten to "copy") → write clipboard while still foreground → **re-activate the
target while we still own the foreground, then hide** (hiding first loses the right to set focus) →
wait for foreground → release held modifiers (mask-key first for Win/Alt) → inject Ctrl+V.

### 2.6 Storage (`Storage/ClipStore`, SQLite via Microsoft.Data.Sqlite, file `history.db`)

`clips` (one row per content hash, preview, search text ≤ 32 K chars, recency, pin, origin, source app,
thumbnail) · `clip_formats` (raw payloads by name, cascade) · `clips_fts` (FTS5 **trigram**, external
content, triggers — substring search incl. Hebrew/CJK; < 3-char terms use escaped `LIKE`) ·
`deleted_hashes` (tombstones: a deleted item is never resurrected by the next Windows import; a new live
copy lifts the tombstone) · `meta.last_clear_utc` (imports older than the last clear are skipped).
WAL, `synchronous=NORMAL`, `auto_vacuum=INCREMENTAL` + `incremental_vacuum` after prunes, schema version
in `PRAGMA user_version` (newer ⇒ refuse, never downgrade). Merge rules: live duplicate ⇒ bump + replace
formats (latest copy wins); import duplicate ⇒ untouched except adding a pin.

### 2.7 Importing Windows' history (`Import/`)

At startup (setting, default on) and on demand: WinRT `GetHistoryItemsAsync` (text/HTML/RTF/bitmap/files;
bitmaps re-encoded as PNG + DIBV5) + decrypted on-disk pins (`WindowsPinnedStore` + `DpapiNg`); WinRT
items are marked pinned when their timestamp (second precision) matches a pin. Deduped by hash,
never reorders existing history.

### 2.8 Data & privacy decisions

Data dir: `%LOCALAPPDATA%\BetterClipboard\` (`history.db`, `settings.json`, `logs\betterclipboard-*.log`,
14-day log retention); override with env `BETTERCLIPBOARD_DATA_DIR` (tests, dev runs — **always use it
when experimenting** so the real history stays clean). Content is plaintext in the user profile (like
Ditto/CopyQ); Windows encrypts only pins. Logs never contain clipboard content.

---

## 3. Build · run · test

```bash
dotnet build BetterClipboard.sln                               # everything (App builds win-x64)
dotnet test --solution BetterClipboard.sln                     # 139 tests (1 opt-in skipped)
BETTERCLIPBOARD_CLIPBOARD_TESTS=1 dotnet test --project tests/BetterClipboard.Windows.Tests   # + real clipboard
```

Run (exe: [`src/BetterClipboard.App/bin/Debug/net10.0-windows10.0.26100.0/win-x64/BetterClipboard.exe`](file:///K:/source/BetterClipboard/src/BetterClipboard.App/bin/Debug/net10.0-windows10.0.26100.0/win-x64/BetterClipboard.exe)):
plain launch = start + open Settings (or activate the running instance); `--background` = tray only
(used by "Start with Windows"); `--show-flyout` = open the flyout in the running instance; `--exit` =
graceful quit (drains queued captures). **The exe is locked while running — `--exit` before rebuilding.**
Isolated dev run: `BETTERCLIPBOARD_DATA_DIR=<scratch>/data BetterClipboard.exe --background`.
Publishing/installer: not yet exercised (`dotnet publish src/BetterClipboard.App -c Release -r win-x64`
is the expected path; WinAppSDK is self-contained, .NET 10 runtime framework-dependent).

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
- **E2E harness ([`tools/e2e/`](file:///K:/source/BetterClipboard/tools/e2e)) injects real keystrokes.**
  Only run it with the user's explicit OK and when they are not using the machine (2026-09-25: the user
  switched to a game mid-run and test keys/Ctrl+V landed in it). Recipe: isolated data dir →
  `PasteTarget.cs` with a unique output file → `activate.py BC-PasteTarget` → `keys.py win+v "type=…" enter`
  → `shot.py out.png fg` → afterwards `cleanup_history.cs -- <start time>` and restore the clipboard.
- Commits: per the user's global rules (message file in scratchpad, `git add` + `git commit` in one
  command, extensive messages, never amend).

---

## 5. Verification status (2026-09-25, Windows 11 25H2 26200.8875, 1920×1080 @100 %)

| Feature | How | Result |
|---|---|---|
| Unit tests | `dotnet test --solution` | 138 pass + 1 opt-in (also passed when enabled) |
| Live capture: text, link, color, files, image (+thumbnail) | real clipboard + DB inspection | ✅ |
| Privacy markers (`Exclude…`, `CanInclude…=0`; `=1` still recorded) | WinForms DataObject from PowerShell 5.1 | ✅ |
| Win+V takeover via LL hook; Win+V returns on exit | log + `probe_hotkeys.cs` | ✅ |
| Flyout near caret (WPF target), acrylic, cards, filters, search (substring) | screenshots | ✅ |
| Enter pastes into target; Shift+Enter plain; Down/Ctrl+P/Del; Esc restores focus | `tools/e2e` | ✅ (after fixes: focus order, first-show focus, auto-select) |
| Persistence across restart; no duplicate re-import (25 found → 0 new) | restart | ✅ |
| Windows history import incl. decrypted pin; image DIB/PNG dedupe by pixels | logs + DB | ✅ |
| `--exit`, `--background` | CLI | ✅ |
| Zero-delay typing right after Win+V (show-first ordering) | e2e | ⚠️ not verified (run aborted: user took focus) |
| `DisabledHotkeys` release, Windows-history toggle via registry, Start with Windows, multi-monitor DPI | — | ⚠️ not verified |

---

## 6. Roadmap / known gaps

- Caret position for apps without a Win32 caret (WinUI, some Electron): UI Automation `TextPattern2.GetCaretRange`.
- Optional at-rest encryption (DPAPI) — conflicts with FTS; options: encrypt payloads only, or in-memory search.
- LL-hook watchdog (Windows removes hooks that time out) + re-install; hook-free mode when `DisabledHotkeys` is set.
- Delete-through to Windows history (`Clipboard.DeleteItemFromHistory`) when deleting here.
- Day grouping, collections/favorites, snippets, OCR for images (`Windows.Media.Ocr`), paste transforms
  (trim, case, JSON pretty), large preview pane, drag-out, sync between PCs, packaging (MSIX/installer, signing, updates).
