# Ditto: what it is, how it works, and what it means for BetterClipboard

User request (2026-10-16): "Learn all about Ditto" (after "do we support anything with ditto?": no, nothing).

Ditto is the classic open-source clipboard manager for Windows, and the app most BetterClipboard users will come
from. This report covers what it is, where it keeps its data, how it captures and pastes, its network feature,
how it behaves next to BetterClipboard, and what an "Import from Ditto" would take.

**How it was learned.**
- **Source:** `sabrogden/Ditto` at `a80fd35` (master, 2026-09-30), cloned into `refs/Ditto` (git-ignored). It is
  GPL-3.0: it was read for interoperability only (formats, names, behavior), and nothing was copied. Line numbers
  below are from that commit.
- **The wiki:** `sabrogden/Ditto.wiki` at `6c3581c` (2022-08-17), in `refs/Ditto.wiki`.
- **The running program:** release 3.25.113.0, run in a disposable claude-desktops Windows 11 Pro 25H2 desktop
  (26200.8037, 1600×900, administrator, UAC off). The desktop was deleted afterwards.
- **Public numbers:** GitHub's, SourceForge's and Chocolatey's APIs, all read on 2026-10-16.

Legend: **[verified]** = seen in the running 3.25.113 (or its files), **[source]** = read in the code at `a80fd35`,
**[docs]** = the wiki or the README, **[api]** = a public API.

---

## 0. Summary

| Question | Answer |
|---|---|
| What is it? | A clipboard manager for Windows since 2003, by Scott Brogden. It is written in C++ with MFC and licensed GPL-3.0. It keeps every copy in a SQLite database and shows it in a small list at the caret (default Ctrl+\`). |
| How used? | 7,314 GitHub stars. Downloads: 4.15 M from SourceForge (since 2003) and about 2.65 M from GitHub releases (since 2020). Chocolatey: `ditto` 459 k, `ditto.install` 112 k, `ditto.portable` 84 k. Also on winget (`Ditto.Ditto`) and in the Microsoft Store (`9NBLGGH3ZBJQ`). **[api]** |
| Latest release | **3.25.113.0, 2025-09-27.** `master` is 70 commits ahead (2026-09-30), and no release has come since. Its binaries are signed by **SignPath Foundation** **[verified]**, the program `docs/code-signing.md` plans for us. |
| Where is the history? | `%APPDATA%\Ditto\Ditto.db` for the installed copy, next to `Ditto.exe` for the portable one (§2). It is **plain SQLite, not encrypted** **[verified]**. |
| Can BetterClipboard read it? | Yes, read-only, even while Ditto runs: open it with `FileShare.ReadWrite`. The format names are the ones BetterClipboard already uses (`CF_UNICODETEXT`, `CF_DIB`, `CF_HDROP`, `HTML Format`, `Rich Text Format`, `PNG`) **[verified]**. §8 has the design. |
| Does it miss copies? | **Yes, by design.** A copy that comes within 500 ms of the last one it saved is dropped, and so is the latest copy when it was one of those. In 10 copies: 1 kept at 50 ms apart, 4 at 200 ms, 5 at 400–450 ms, all 10 from 550 ms. BetterClipboard, running next to it on the same copies, kept 10 of 10 at 5 ms and at 200 ms **[verified]** (§3.4). |
| Does it respect "don't record me" markers? | **3.25.113 respects only `Clipboard Viewer Ignore`.** It **stored** copies marked `ExcludeClipboardContentFromMonitorProcessing` and `CanIncludeInClipboardHistory = 0` **[verified]**. `master` added both on 2025-12-05 (`b0533af`), after the last release (§3.3). |
| Network? | The installed copy **listens on TCP 0.0.0.0:23443 by default** to receive clips from "friends". On its first start, Windows asks whether to allow Ditto on public and private networks **[verified]**. The traffic is AES-256 with a shared password whose default is `LetMeIn` **[source]** (§6). |
| Next to BetterClipboard? | They get along. Ditto marks its own pastes `Clipboard Viewer Ignore`, so BetterClipboard skips them; BetterClipboard's pastes become Ditto entries. Their default hotkeys do not clash (Win+V and Ctrl+\`) **[verified]** (§7). |
| Win+V? | Ditto uses `RegisterHotKey` only, so it cannot have Win+V while Explorer holds it. Its wiki suggests AutoHotkey for that **[docs]**. |

---

## 1. The project

- **Age:** the SourceForge project `ditto-cp` was created on 2003-06-24, and the first commit is 2003-06-27
  (imported from CVS). The code moved to GitHub in 2020 (repository created 2020-11-13). **[api]**
- **Activity:** 1,583 commits, about 1,467 of them by Scott Brogden under four spellings of his name. There were 151
  commits in 2025 and 39 in 2026 so far: translations, themes, special-paste actions, a few fixes. **[source]**
- **License:** GPL-3.0. The `LICENSE` file was added in 3.25.73 (PR #790). **[source]**
- **Platforms:** one 64-bit installer (Inno Setup, `ArchitecturesAllowed=x64compatible`, so x64 and ARM64;
  `MinVersion=6.1`; 3.25 offers no 32-bit download any more), a portable zip, the Microsoft Store (an appx),
  Chocolatey (three packages) and winget. **[source]**, **[api]**
- **Contents of 3.25.113** **[verified]**:
  - `Ditto.exe` (5.7 MB), `ICU_Loader.dll` (Unicode regular expressions), `Addins\DittoUtil.dll`;
  - the MSVC and MFC runtimes (`mfc140u`, `msvcp140`, `vcruntime140`, `vcruntime140_1`);
  - 30 language files and 13 themes.
- **Version resource:** FileDescription and ProductName "Ditto", **CompanyName empty**. `Ditto.exe` and the installer are
  Authenticode-signed by "SignPath Foundation" (GlobalSign GCC R45 CodeSigning CA 2020), status Valid. **[verified]**
- **Built on:** MFC; SQLite through CppSQLite3, linked as **SQLite3 Multiple Ciphers 2.3.5** (SQLite 3.53.2) on
  `master`; ChaiScript (scripts); zlib (exports, backups); TinyXml; a QR-code generator; a ruler rich-edit control
  (the clip editor). **[source]**
- **Its own claims:** "Local First: No login, No cloud, No telemetry". Its wiki's privacy policy: "Absolutly nothing,
  there is no tracking of any kind". **[docs]**
  - The source agrees. The `CheckForUpdates` setting (on) is read and written, but nothing acts on it.
  - The bundled `httplib.h` is included by no `.cpp` file, and `sqcloud.c` (SQLite Cloud) is not in the project.
  - The only network code is the friends feature (§6), and links that open in the browser. **[source]**

## 2. Install kinds and where things live

Ditto decides how it was installed from **marker files next to `Ditto.exe`** (`Options.cpp` `LoadSettings`, line 185):

| Kind | Marker | Settings | Database (default) | Receives clips by default |
|---|---|---|---|---|
| Installed (the installer; Chocolatey's `ditto` and `ditto.install` run it with `/VERYSILENT`) | none | Registry `HKCU\Software\Ditto` | `%APPDATA%\Ditto\Ditto.db` | **yes** (port 23443) |
| Portable zip | `portable` | `Ditto.Settings` next to the exe | `Ditto.db` next to the exe (`DBPath3=Ditto.db`, relative) | no |
| Chocolatey `ditto.portable` (the portable zip with `portable` renamed to `chocolatey`) | `chocolatey` | `%LOCALAPPDATA%\Ditto_ChocolateyApp\Ditto.Settings` | the same folder; **the package's uninstall deletes it, history included** | yes |
| Microsoft Store | `WindowsApp` | `%LOCALAPPDATA%\Ditto_WindowsApp\Ditto.Settings` (inside the package: `Packages\<pkg>\LocalCache\Local\Ditto_WindowsApp`) | the same folder | no |

- **The database path** is the setting `DBPath3`: a registry value, or a key in the ini's `[Ditto]` section. It may
  hold environment variables (`%COMPUTERNAME%`, `%USERNAME%`, since 3.21.196), which lets people keep one database per
  PC in a Dropbox folder. A database on a network share or another drive letter is waited for, not recreated.
  **[source]**, **[docs]**
- **Without a marker,** an ini file still wins: `Ditto.Settings` next to the exe, else in `%APPDATA%\Ditto`.
  Otherwise the registry is used. **[source]**
- **`Ditto.Settings` is UTF-16LE with a BOM.** In the portable copy its first run wrote `[Ditto]`,
  `CheckForMaxEntries=1`, `SimpleTextSearch=1`, `DBPath3=Ditto.db` and `MainhWnd=…` **[verified]**.
  - Other sections hold per-app keystrokes: `[PasteStrings]`, `[CopyStrings]`, `[CutStrings]`.
- **The installed copy's first run** wrote these registry values **[verified]**:
  - `CheckForMaxEntries=1`, `SimpleTextSearch=1`;
  - `DBPath3=C:\Users\<user>\AppData\Roaming\Ditto\Ditto.db`;
  - `MainhWnd`, the main window handle, which later command-line calls message.

  The installer itself wrote `LanguageFile` and `SetFocus_iexplore.exe`, and the subkeys `PasteStrings`, `CopyStrings`
  and `CutStrings`.
- **The installer** (`DittoSetup_10.iss`) **[source]**, with **[verified]** marking what the run showed:
  - it needs administrator rights and installs to `C:\Program Files\Ditto`;
  - "Run Ditto on Windows startup" is checked by default: `HKCU\…\Run\Ditto = C:\Program Files\Ditto\Ditto.exe`,
    unquoted **[verified]**;
  - "Add Windows Firewall exception for Ditto on port 23443" is unchecked by default;
  - it associates `.dto` (exported clips) with Ditto;
  - it registers Windows Error Reporting **full** crash dumps for `Ditto.exe` (`LocalDumps`, `DumpType 2`) into
    `%APPDATA%\Ditto\Dumps`. A crash therefore leaves a memory image that can hold clipboard data;
  - it starts Ditto when done, also after `/VERYSILENT`.
- **Other files:**
  - `%APPDATA%\Ditto\ditto.log`, only while debug logging is on (Ctrl+D then Ctrl+F, or Options › Advanced). That log
    **contains clip descriptions**: "Added clip to main table, Id: 1, … Desc: BC-TEST plain one" **[verified]**;
  - temp folders for drag and drop, received files, compares and edits.
- **One instance per kind:** the mutex is `Ditto Is Now Running`. For portable, Store and Chocolatey copies the exe's
  path is appended, with `\` turned into `_`. So a portable Ditto runs next to an installed one: both ran at the same
  time in the test **[verified]** (no copy was made while both ran). The second one's Ctrl+\` fails, and only its log
  would say so (§5.3).
- **Windows:** the main window class is `Ditto`; the popup list is `QPasteClass`, titled "Ditto" **[verified]**.

## 3. Capture

### 3.1 How it listens **[source, verified]**

- A hidden window, "Ditto Clipboard Viewer", on a copy thread calls `AddClipboardFormatListener` (the log line
  "Connecting to clipboard with function AddClipboardFormatListener" **[verified]**). Before Vista it joined the
  viewer chain.
- **Every notification restarts a 100 ms timer** (`ProcessDrawClipboardDelay`). Only when it fires does Ditto read, so
  a burst is read once, at its end.
- **Then the 500 ms rule** (`SaveClipDelay`, `ClipboardViewer.cpp` line 396): when less than 500 ms have passed since
  the last copy it *saved*, the change is **dropped**. Nothing retries it, and nothing is counted except a log line,
  "Clip copy to fast difference from last copy = 344" **[verified]**.
- **A safety ping every 5 minutes** (`EnsureConnected2`, off by default) would add "Ditto Ping Format" and `Clipboard
  Viewer Ignore` to the current clipboard to test the listener. Not seen in the test (it is off).
- **The source app** is the clipboard owner's process, else the foreground window's. Ditto uses it only for the include
  and exclude lists, the regex filters and scripts. **It is not stored** (`Main` has no column for it).

### 3.2 What it keeps **[source, verified]**

- **Formats:** the `Types` table, empty by default, which means `CF_TEXT`, `Rich Text Format`, `CF_UNICODETEXT`,
  `CF_HDROP`, `CF_DIB`, `HTML Format` and `PNG` (`CP_Main.cpp` `LoadTypesFromDB`). "Support all types" (off) keeps every
  format.
- **Text is kept twice:** a Unicode copy also stores the ANSI `CF_TEXT` that Windows synthesizes. 68 of 68 text clips
  had both **[verified]**.
- **Images are stored raw:** `CF_DIB` (12,328 bytes for a 64×48 32-bit picture), plus `PNG` when the source offers it.
  `CF_DIBV5` is not in the list.
- **Payloads** are the raw `GlobalSize` bytes, uncompressed, terminators and slack included.
- **The description** (`Main.mText`) is the first 500 characters of the text (`DescTextSize`); the full text is only in
  `Data`. For a file list it reads "Copied Files - a.txt - C:\…\a.txt\n…"; for an image, the format's name (`CF_DIB`).
- **Filters before saving:**
  - include and exclude lists of process names with wildcards (`CopyAppInclude` "*", `CopyAppExclude` "");
  - "Exclude clips by Regular Expressions" (text plus optional process);
  - ChaiScript "on copy" scripts that can cancel, edit or route a copy into a group;
  - a maximum size (`MaxClipSizeInBytes`, 0 = no limit);
  - a list of apps whose `CF_DIB` is ignored when they also offer text (`IgnoreAnnoyingCFDIB`).
- **Copies go into the open group:** while a group is open in Ditto's list, new copies are put into that group
  (`CopyThread.cpp`).

### 3.3 Privacy markers

| Marker | 3.25.113 (the release) | `master` |
|---|---|---|
| `Clipboard Viewer Ignore` | ignored the copy **[verified]** | ignored |
| `ExcludeClipboardContentFromMonitorProcessing` | **stored it** **[verified]** | ignored since `b0533af` (2025-12-05) |
| `CanIncludeInClipboardHistory` = 0 | **stored it** **[verified]** | ignored since `b0533af` |
| `CanIncludeInClipboardHistory` = 1 | stored | stored |

- `master` puts the two newer markers behind the setting "EnforceClipboardIgnoreFormats" (on by default, `105f198`,
  2026-01-04).
- **Until a release ships them, a password manager that sets only the Windows 10 markers has its copies written to
  Ditto's plain database.** Many managers set `ExcludeClipboardContentFromMonitorProcessing`; Ditto also has no list of
  password-manager processes (BetterClipboard has 65 names, §2.8 of CLAUDE.md).

### 3.4 How many copies it keeps, measured

Ten BC-TEST copies per gap, written with plain Win32 calls and busy-waited, with 3 s between series. Read back from
`Ditto.db`. The runs with and without Ditto's debug log gave the same pattern **[verified]**:

| Gap between copies | Ditto kept | Which | BetterClipboard next to it |
|---|---|---|---|
| 5 ms | 1 of 10 | #10 | **10 of 10** |
| 50 ms | 1 of 10 | #10 | — |
| 200 ms | 4 of 10 | #1, #4, #7, #10 | **10 of 10** |
| 400 ms | 5 of 10 | #1, #3, #5, #7, #9 — **the last copy is lost** | — |
| 450 ms | 5 of 10 | #1, #3, #5, #7, #9 | — |
| 550 ms | 10 of 10 | all | — |
| 600, 1,000 ms | 10 of 10 | all | — |

- **Two quick copies:** copy A, then B 343 ms later. Ditto kept A, never B, and B stayed on the clipboard. Ditto's
  history then disagrees with what Ctrl+V pastes.
- **The pattern follows from §3.1 exactly:** the timer fires 100 ms after a copy, and a copy saved at *t* blocks
  everything until *t* + 500 ms.
- **People do copy that fast** (a selection, then a corrected selection), and so do scripts. BetterClipboard reads at
  once and keeps every copy 2 ms or more apart (CLAUDE.md §1.9). That difference is worth saying in the README.
- **The duplicate rule:** Ditto computes a CRC-32 over all stored formats, with RTF fields that Word changes removed.
  With `AllowDuplicates` off, a copy whose CRC exists anywhere in the database moves that entry to the top
  (`clipOrder` = max + 1) and is not stored again. Its `lDate` stays the first copy's **[verified]**: "Found duplicate
  clip in db, Id: 1".

## 4. Storage

### 4.1 Schema **[verified with tools/probes/probe_ditto.py on the test database; source: `DatabaseUtilities.cpp` `CreateDB`]**

- **SQLite settings:** `encoding = UTF-16le` (Ditto opens the file with `sqlite3_open16`), `page_size` 4096,
  `auto_vacuum = 1` (full), `journal_mode = delete`, `user_version` 0. The schema is upgraded by trying columns and
  adding the missing ones, never by version number.
- **`Main`**, one row per clip or group:
  - `lID` (autoincrement);
  - `lDate` (Unix seconds of the first copy, 64-bit since PR #806);
  - `mText` (the description, ≤ 500 characters; a group's name);
  - `lShortCut`, `globalShortCut` (a hotkey per clip, and whether it works system-wide);
  - `lDontAutoDelete` (0, or the Unix time it was set: "Never auto delete", the "starred" filter on `master`);
  - `CRC`;
  - `bIsGroup`, `lParentID` (−1 = top level, else the group's `lID`; groups nest);
  - `QuickPasteText` (a short name to search by);
  - `clipOrder`, `clipGroupOrder` (REAL counters: the list order, **not times**);
  - `lastPasteDate` (Unix seconds; the creation time until the clip is pasted);
  - `stickyClipOrder`, `stickyClipGroupOrder` (−2147483647 = not sticky; sticky clips stay on top);
  - `MoveToGroupShortCut`, `GlobalMoveToGroupShortCut`.
- **`Data`**, one row per stored format: `lID`, `lParentID` (the clip), `strClipBoardFormat` (a name: `CF_UNICODETEXT`,
  `CF_TEXT`, `CF_DIB`, `CF_HDROP`, … for standard formats, the registered name for the rest), `ooData` (BLOB).
- **The other tables:** `Types` (the format list), `CopyBuffers` (`lClipID`, `lCopyBuffer`), `MainDeletes`
  (`clipID`, `modifiedDate`).
- **13 indexes** (on the orders, groups, CRC and shortcuts) and **2 triggers**:
  - deleting a `Main` row inserts it into `MainDeletes`;
  - deleting a `MainDeletes` row deletes its `Data` and `CopyBuffers` rows.
- **Full-text search on a blob works because of the encoding:** "search full text" runs
  `Data.ooData LIKE '%…%'` on `CF_UNICODETEXT` rows. SQLite reads the blob as text in the database encoding, UTF-16LE,
  which is what `CF_UNICODETEXT` holds.

### 4.2 Order, retention and deletion **[source]**

- **The list order** is `stickyClipOrder DESC, clipOrder DESC`. A paste moves the clip to the top and sets
  `lastPasteDate` (`UpdateTimeOnPaste`, on) **[verified]**.
- **Retention**, from Options › General:
  - "Maximum number of saved copies" (500 for the installed copy, 100 for the portable one; switched on at the first
    run **[verified]**);
  - "Expire after N days" (5, off).
  - Kept anyway: groups, clips in groups, "never auto delete" clips, clips with a hotkey, sticky clips.
- **Deleting is two-step.** A delete removes the `Main` row at once. Its data stays in `Data` until a background pass
  deletes the `MainDeletes` entries: 100 per pass, and only after the PC has been idle for 10 minutes
  (`IdleSecondsBeforeDelete`). Until then, deleted clips' contents are still in the file.
- **Backup, Restore, Export:**
  - "Backup Database" writes a gzip of the file; "Restore" unpacks and validates one.
  - "Export Clip(s)" writes a `.dto` file: a SQLite database with `Main(lID, lVersion, mText)` and
    `Data(lID, lParentID, strClipBoardFormat, lOriginalSize, ooData)`, each blob zlib-compressed.
  - Opening a `.dto` puts its clips on the clipboard (`Ditto.exe file.dto`) or imports them.
- **Reading the file while Ditto runs** works when the reader shares write access. A copy with
  `FileShare.ReadWrite` succeeded; an open with `FileShare.Read` failed with a sharing violation. **[verified]**
- **Encryption:** none. `master` links SQLite3 Multiple Ciphers since 3.25.73 ("to start to be able to encrypt"), but
  every key call is commented out (`CppSQLite3.cpp` line 790). Anyone who can read the user's profile can read the
  history, and so can backup tools and other accounts with administrator rights.

## 5. The list, pasting and hotkeys

### 5.1 The list **[docs, source, verified]**

- **Opening it:** Ctrl+\` opens a small, resizable list at the caret (or the cursor, or the last position), over any
  window **[verified]**.
- **In the list:**
  - the first ten rows carry numbers; Ctrl+1…0 pastes them;
  - Enter pastes, Shift+Enter pastes plain text, F3 shows the whole clip, F7 makes a group, Ctrl+G shows the group tree;
  - typing searches.
- **Search** covers the description by default ("Simple text search", switched on at the first run); full text and
  quick-paste text are options. The modes are "contains", wildcard (with AND / OR / NOT and quotes) and regular
  expressions; the `/f` and `/q` prefixes search full text and quick-paste text.
- **Clip menu:**
  - special paste: plain text, upper, lower, capitalize, sentence and inverted case, trim, slugify, CamelCase,
    typoglycemia, "posixify paths", ASCII only, remove or add line feeds, add the current time, a new GUID, several
    images side by side or stacked, paste as image;
  - multi-paste of several clips, with a separator (default a line feed);
  - properties: a hotkey per clip, global or local; "never auto delete"; quick-paste text;
  - sticky clips; move to a group;
  - compare two clips (WinMerge, Beyond Compare, VS Code); the internal editor or an external one;
  - export to a text file, an image file, a QR code, e-mail, Gmail, Google Translate or a web search;
  - "Delete clip data" (individual formats).
- **The rest:**
  - 13 themes; 30 languages;
  - add-ins: `DittoUtil.dll` pastes any format as text, an image as an HTML image, picks a format, removes line feeds;
  - ChaiScript "on paste" scripts.

### 5.2 How it pastes **[source, verified]**

1. It puts the clip on the clipboard through OLE (`COleDataSource`, rendered at once) and adds `Clipboard Viewer Ignore`.
   - A multi-paste with "save multi-paste" on adds "Ditto Delay Saving Data" instead.
2. It brings the target window back (`AttachThreadInput` + `SetForegroundWindow`). It follows the active window every
   2 s and when the hotkey is pressed.
3. It sends the paste keys with `keybd_event`: `^{VKEY86}` (Ctrl+V) by default, or a per-app string from
   `PasteStrings`, such as `gvim.exe` = `"+gP`.

- **What was on the clipboard after a Ditto paste into Notepad** **[verified]**: owner `Ditto`, formats `DataObject`,
  `CF_TEXT`, `CF_UNICODETEXT`, `Clipboard Viewer Ignore`, `Ole Private Data`, `CF_LOCALE`, `CF_OEMTEXT`.
- **Elevated windows** (`PasteAsAdmin`, on) **[source]**: when the target runs as administrator and Ditto does not, Ditto
  starts a second copy of itself with `runas` (a UAC prompt) and `/uacpaste:<pid>`. That copy sends the keys from then
  on, until Ditto exits. BetterClipboard instead leaves the item on the clipboard and says "press Ctrl+V"
  (CLAUDE.md §2.5).
- **Copy buffers** (up to 5, each with copy, paste and cut hotkeys) fake extra clipboards. Ditto saves the clipboard,
  loads the buffer, sends Ctrl+V, waits, and puts the old contents back, again marked `Clipboard Viewer Ignore`.
  **[docs, source]**

### 5.3 Hotkeys **[source, verified]**

- **Only `RegisterHotKey`; no keyboard hook.** The default is Ctrl+\` (`DittoHotKey` = 704, which is
  `HOTKEYF_CONTROL` and `VK_OEM_3`). Ctrl+\` was taken while Ditto ran (error 1409) and free before **[verified]**.
- **What can have a hotkey:** up to three keys that open the list, paste-position keys 1–10, the copy buffers,
  "text-only paste", "save clipboard", a key per clip and a key per group. Only Ctrl+\` is set by default.
- **A taken hotkey fails without a message:** only a log line, "Error Registering …".
- **The Windows key:** the options dialog has a "Win" box (`HOTKEYF_EXT` → `MOD_WIN`). Win+V stays Explorer's unless
  `DisabledHotkeys` frees it, which is BetterClipboard's §2.25 method. The wiki recommends AutoHotkey running
  `Ditto.exe /Open` instead.
- **The command line** **[docs]**: `/Open`, `/Close`, `/Exit`, `/Connect`, `/Disconnect`, `/PlainTextPaste`,
  `/Paste:<id>`, `/Edit:<id>`. Messages 1257 and 1258 to the main window paste or edit a clip by id. `/Exit` closed
  Ditto in 0.75 s **[verified]**.

## 6. The network: "friends"

**[source, verified]**
- **A TCP server on `NetworkBindIPAddress` (`*` = every interface) and `SendRecvPort` (23443).** It runs unless
  "Disable receiving clips" is on, which is the default only for the portable and Store copies.
  - The installed copy was `LISTENING` on `0.0.0.0:23443` within seconds of its first start. The portable copy did not
    listen. **[verified]**
- **Windows asked at once:** "Windows Firewall has blocked some features of Ditto on all public and private networks.
  Publisher: Unknown" — probably the empty CompanyName. Cancel added two inbound **Block** rules named "Ditto".
  **[verified]**
- **Sending:**
  - up to 15 friends by IP or name, each optionally receiving **every** copy ("send all");
  - a manual "Send to friend" for one clip;
  - received clips are saved with "(computer)(IP)" appended to the description, and put on the clipboard when the
    sender is in `ListToPutOnClipboard`;
  - pasting a received file list asks the sender for the files (up to 64 MB each, MD5-checked).
- **Encryption:** KeePass-1-style AES-256 (Rijndael CBC; the key from SHA-256 of the password). The password is
  `NetworkStringPassword`, **default `LetMeIn`**, plus a list of extra passwords to accept.
  - Consequence: a receiving Ditto that kept the default password accepts clips from anyone who can reach port 23443
    and runs Ditto. The firewall prompt is the only thing in the way. Not tried in the test.

## 7. Next to BetterClipboard

Both ran in the test desktop: BetterClipboard 0.2.8 with Win+V released from Explorer, and Ditto 3.25.113 with
Ctrl+\`. Every item below is **[verified]**.

- **A normal copy reaches both.** Both read the clipboard after each change; BetterClipboard's counters showed
  0 locked out and 0 overwritten in 22 changes.
- **Bursts:** BetterClipboard kept 10 of 10 at 5 ms and 200 ms gaps; Ditto kept 1 and 4 (§3.4).
- **A paste from Ditto is not recorded by BetterClipboard.** BetterClipboard's log: "Skipped a copy marked private by
  'Ditto'." The item is in Ditto already, so nothing is lost.
- **A paste from BetterClipboard becomes a Ditto entry.** BetterClipboard writes no ignore marker. "gap 5 #9", which
  Ditto had dropped, arrived in Ditto with the paste, as a new clip.
- **Hotkeys:** Win+V opened BetterClipboard's panel and Ctrl+\` opened Ditto's list, over the same Notepad.
- **Footprint at rest** (the same desktop, no GPU; CPU over 60 s, three samples):

  | | Ditto | BetterClipboard |
  |---|---|---|
  | Private memory | 4.9 MB | 106.6 MB |
  | Working set | 6.1–6.7 MB | 216–220 MB |
  | Threads | 9 | 32 |
  | CPU over 60 s at rest | 47–109 ms | 297–844 ms |

  BetterClipboard's panel is kept rendered and cloaked for an instant Win+V (CLAUDE.md §2.27), and the WinUI runtime
  costs most of the memory.
- **Should BetterClipboard mark its own pastes `Clipboard Viewer Ignore`?** Not by default. Other managers, Windows'
  Win+V included, would then lose every paste made from BetterClipboard. If asked, it could be an option.

## 8. Importing Ditto's history into BetterClipboard (proposal, not built)

**Why:** Ditto users have years of history: clips with no limit, groups, never-delete clips. `docs/code-signing.md`
already plans an AlternativeTo listing "as an alternative to Ditto". An import removes the cost of switching.

**Finding the database:**
1. `HKCU\Software\Ditto\DBPath3`, with environment variables expanded.
2. `Ditto.Settings` (UTF-16, section `[Ditto]`, `DBPath3`): in `%APPDATA%\Ditto`,
   `%LOCALAPPDATA%\Ditto_ChocolateyApp`, and `%LOCALAPPDATA%\Packages\*\LocalCache\Local\Ditto_WindowsApp`.
3. A running `Ditto.exe`'s folder (portable copies): its `Ditto.Settings`, where a relative `DBPath3` means the exe's
   folder.
4. "Choose a file…" for anything else: a copied `Ditto.db`, a `.dto` export, or a backup `.gz`.

`tools/probes/probe_ditto.py` does steps 1–3 and describes a database without printing content.

**Reading:**
- Read-only, through the same SQLite3MC engine (a plain file needs no key), opened with `FileShare.ReadWrite | Delete`
  or copied first. Never write to it, and never hold it open while Ditto may want it.
- Stream rows newest first (`ORDER BY stickyClipOrder DESC, clipOrder DESC`) in slices through the history worker,
  like the prompt archive's first import (CLAUDE.md §2.21). A Ditto database can be several GB when its limit was
  switched off.
- Skip `MainDeletes` entries: their `Main` rows are gone already, so a join through `Main` never sees them.

**Mapping:**

| Ditto | BetterClipboard |
|---|---|
| `Data` rows | `ClipFormatData` by name. The names are identical: `CF_UNICODETEXT`, `CF_DIB`, `CF_HDROP`, `HTML Format`, `Rich Text Format`, `PNG`. |
| `CF_TEXT` next to `CF_UNICODETEXT` | dropped (a synthesized twin, as in BetterClipboard's own capture) |
| `CF_TEXT` alone (old clips) | decoded with the system ANSI code page into `CF_UNICODETEXT` |
| "Ditto Remote CF_HDROP", "Ditto File Data", "Ditto Delay Saving Data" | dropped (Ditto's own markers) |
| `lDate` | first seen |
| last used | `max(lDate, lastPasteDate)`. Ditto keeps no time for a re-copy, only `clipOrder`: to keep Ditto's order exactly, space the imported times by order between neighbors (as the Win+R import does). |
| `stickyClipOrder` set, or `lDontAutoDelete > 0` | pinned (both mean "keep and show first" to a Ditto user) |
| groups (`bIsGroup = 1`) | groups, named after `mText`, with nested names joined ("Work › Clients"); a default icon. Ditto's single parent becomes one membership. |
| `lShortCut`, `globalShortCut`, `QuickPasteText` | no equivalent: leave them out, and say so in the import's summary |
| source app | none (Ditto keeps none), like the Windows import |

**Rules** (those of the Windows import, CLAUDE.md §2.7):
- a new origin, `ClipOrigin.Ditto` (12), with import semantics: duplicates by content hash are left alone (except
  gaining a pin), tombstones and the last clear are respected, pause does not apply;
- Forget forever and the size limit apply; images go through the analyzer, so a Ditto `CF_DIB` merges with the same
  picture copied later.

**Where:** Settings › Integrations › "Import from Ditto…", shown when a database is found. The button shows the count
first ("12,431 clips, 6 groups, 2 starred"), then runs the import with progress. It runs once per database (a marker
in the store, keyed by the path's hash). Running it again adds only what is new.

**Tests:** a database built the way `probe_ditto.py --self-test` builds one (BC-TEST rows, both encodings of text,
groups, sticky and never-delete clips, a `.dto` file).

## 9. Ditto compared with BetterClipboard

| | Ditto 3.25.113 | BetterClipboard 0.2.8 |
|---|---|---|
| Keeps every copy | No: drops copies within 500 ms of the last saved one, the latest included | Yes: ≥ 2 ms apart, the latest always |
| Respects privacy markers | `Clipboard Viewer Ignore` only (the rest on `master`) | All three, plus 65 password-manager processes |
| History on disk | Plain SQLite | Encrypted (SQLite3MC, a DPAPI-sealed key) |
| Win+V | Not possible while Explorer has it | Takes it over (keyboard hook, or released from Explorer) |
| Imports Windows' clipboard history | No | Yes, pinned items included |
| Remembers which app copied | No | Yes, shown on every card |
| Search | Description (500 chars); full text by `LIKE`; regex | Full text by trigram index; match case, whole word, regex |
| Other apps' histories | No | ShareX, Snipping Tool, Win+R, PowerShell, Command Prompt, Claude Code, Codex, Everything |
| Command line | Open/close/paste by id | `bclip`: list, search, grep, get, copy, put, wait, … |
| Footprint at rest | ~5 MB private | ~107 MB private |
| Network | LAN friends, port 23443 open by default (installed) | One daily update check (switchable); no listening port |
| Ditto has, BetterClipboard does not | See the list below | — |

**What Ditto has and BetterClipboard does not** (checked against BetterClipboard's code on 2026-10-16):
- **Paste keys per app:** BetterClipboard always sends Ctrl+V (`KeyboardInput.SendCtrlV`). Ditto takes a keystroke per
  process (`PasteStrings`, e.g. `gvim.exe` = `"+gP`). Apps where Ctrl+V does something else (vim in a terminal, mintty,
  PuTTY) need that.
- **Special paste:** BetterClipboard has plain text and paths as text. Ditto also has case changes, trim, slugify,
  CamelCase, line feeds added or removed, the time, a new GUID, ASCII only, images side by side.
- **Several items at once:** the panel's list selects one card (`SelectionMode="Single"`). Ditto pastes a selection
  joined by a separator.
- **Editing an item** before pasting it.
- **Export, import, backup:** BetterClipboard has none (its store is bound to the PC on purpose). Ditto has `.dto`
  export/import and a database backup and restore.
- **Hotkeys that paste without opening the panel:** BetterClipboard's Ctrl+1…9 work inside the panel only. Ditto has
  global keys for the Nth newest item, a key per item, and a text-only paste key.
- **Dragging a card into another app:** BetterClipboard's drag carries only its own ids, for its groups.
- **A full view of a long text** (Ditto's F3); BetterClipboard previews images only.
- **Manual order:** move up and down, sticky order. BetterClipboard has pinned-first only.
- **Exclusion by regular expression** on the text (BetterClipboard: ignored apps, and Forget forever's exact matching).
- **Copy buffers** (extra clipboards on hotkeys).
- **Group extras:** nested groups, a hotkey per group, new copies going into the open group.
- **Pasting into elevated windows by itself** (an elevated helper behind a UAC prompt); BetterClipboard leaves the item on
  the clipboard and says "press Ctrl+V".
- **Saving the current clipboard by hand,** also while paused.
- **Smaller ones:** 30 languages (BetterClipboard is English only), 13 themes (BetterClipboard: system, light, dark), a
  portable mode, comparing two items, QR codes, e-mail / web search / translate, ChaiScript scripts, sync to other PCs
  on the LAN.

**Ideas worth taking from Ditto** (a pick by usefulness to BetterClipboard's users, not a measure of demand):
1. Paste keys per app, at least Shift+Insert for terminals where Ctrl+V does something else.
2. Special paste (case, trim, slug, line feeds; plain text already exists) as a submenu.
3. Paste several selected cards at once, joined by a separator.
4. Editing an item's text before pasting it.
5. Export and import of selected items (a file another PC can open), and "Import from Ditto" (§8).
6. Hotkeys that paste the Nth newest item without opening the panel (Ditto's "positions").
7. Exclusion rules by regular expression on the text (next to Forget forever's exact matching).

## 10. How to reproduce

- **Read-only probe on any PC:**
  - `python tools/probes/probe_ditto.py` finds Ditto's settings and databases for the current user, without opening a
    database;
  - `--db <file>` describes one (structure, counts, formats, payload checks; never content);
  - `--self-test` checks the probe against a synthetic BC-TEST database.
- **The behavior (§3–§7) needs a disposable Windows desktop,** never the user's PC: Ditto reads the real clipboard and
  opens a firewall prompt. The scripts used are in session 46390d28's scratchpad (`ditto/vm/`):
  - `BcClip.cs` / `bc.ps1`: BC-TEST copies through plain Win32 calls, the privacy markers, a DIB, a file list, timed
    bursts, a clipboard description that does not open the clipboard, and a hotkey probe;
  - `scen1.ps1` (kinds, markers, pairs), `rate.ps1` (gaps), `scen2.ps1` (next to BetterClipboard).
- **Lesson:** `Start-Process -Wait` on Ditto's installer never returns in Windows PowerShell 5.1. It waits for the whole
  process tree, and the installer starts Ditto. Wait for the setup process alone, or use the desktop agent's `launch`.

## 11. Sources

- Source: https://github.com/sabrogden/Ditto (`a80fd35`, 2026-09-30); release notes of 3.24.246, 3.25.73, 3.25.76 and
  3.25.113.
- Wiki: https://github.com/sabrogden/Ditto/wiki (Command-Line-Options, Copy-Buffers, Custom-Key-Strokes,
  Custom-Shortcuts, Debug-Logging, Environment-Variables, Excluding-clip-from-saving, Privacy-Policy, Scripting, …).
- Home page: https://ditto-cp.sourceforge.io/ ; SourceForge project `ditto-cp` (REST API, download statistics).
- Chocolatey OData API (`ditto`, `ditto.install`, `ditto.portable`); winget manifests `manifests/d/Ditto/Ditto`.
