# BetterClipboard in the Microsoft Store

Owner's request (2026-11-12): "I want to publish my product to microsoft marketplace". This document is the full
report: where a desktop app is published, what Microsoft requires, how a packaged app behaves (measured), the design
of the Microsoft Store edition, how the package is built, what was verified, and what the owner does in Partner
Center. The texts to paste there are in [`store-listing.md`](store-listing.md).

Legend: **[docs]** = Microsoft documentation read on 2026-11-12 · **[measured]** = measured that day in a disposable
Windows 11 25H2 desktop (26200, Tiny11) with [`tools/probes/msix_lab`](../tools/probes/msix_lab) · **[policy]** =
Microsoft Store Policies 7.20 (published 2026-09-15, effective 2026-10-22).

**State (2026-11-14):** the Store edition and its package build are in the repository and were run end to end in test
desktops. The owner registered as a developer. The name **BetterClipboard** is reserved (Store ID `9PHL574X9BF9`,
publisher "Nucs"), and the product's first submission (1.2.11, the release 0.2.11) was **sent for certification** on
2026-11-13 (§8). Since 2026-11-14 the edition keeps its history in `%LOCALAPPDATA%\BetterClipboard`, the folder every
other kind of install uses (§4.4); that ships with the next release, whose submission needs the updated notes for
certification. The release pipeline builds the Store packages and, once the first submission is published and three
secrets are set, submits every release by itself (§7).

## 1. Where: the Microsoft Store, as an MSIX package

- **"Microsoft Marketplace" is not it.** That name (since 2025) is Microsoft's storefront for cloud and AI offers,
  the former Azure Marketplace and AppSource. Windows desktop apps go to the **Microsoft Store** **[docs]**.
- **The account is free**: for individuals since 2025-09, for companies since 2026-05. Sign up at
  `https://storedeveloper.microsoft.com` with a personal Microsoft account, a government ID and a selfie **[docs]**.
  An individual account fits a single developer (policy 10.14).
- **Two ways in** **[docs]**:

  | | MSIX package | EXE / MSI installer |
  |---|---|---|
  | Signing | Microsoft signs the package after certification | Installer and every PE file need a certificate from a CA in Microsoft's Trusted Root Program |
  | Hosting | Microsoft | A versioned https link on our side that never changes |
  | Updates | The Store, automatically | The app's own updater |
  | Installer | None (declarative package) | `.exe` or `.msi`, silent, offline |

- **The EXE route is closed today**: releases are unsigned (CLAUDE.md §3.3) and the installer is a PowerShell script.
  The **MSIX route needs neither a certificate nor an installer**, so that is the one built here. It also gives users
  what code signing would: an install without SmartScreen warnings.

## 2. What a package changes (measured)

A Store package of a desktop app runs at medium integrity ("full trust", capability `runFullTrust`), not in an
AppContainer. Windows still redirects some of what it writes. Every row is **[measured]** with `msix_lab`
(a console program registered as such a package, compared with what an unpackaged shell sees):

| What | Result |
|---|---|
| The process writes or deletes a value under `HKEY_CURRENT_USER` | Redirected to the package's private hive: the real registry is unchanged, also for a delete |
| The process reads `HKCU` / `HKLM` | Sees the real registry (merged with its own writes) |
| `RegNotifyChangeKeyValue` on a real `HKCU` key (also through `HKEY_USERS\<SID>`) | Never signalled when another process writes; polling the key's last-write time sees the change |
| File change notifications (`FileSystemWatcher`) for real folders: Roaming AppData, Local AppData, outside AppData | Arrive as for any program |
| A new folder directly under `%LOCALAPPDATA%` | Redirected to `…\Packages\<family>\LocalCache\Local\…`, deleted when the package is uninstalled |
| Anything inside a folder that already exists there for real | Real: append, new file, delete, rename, replace, delete the folder |
| A folder directly under `%LOCALAPPDATA%` made by a program the package starts (cmd's `mkdir`), then written by the package (2026-11-14) | Real, and so is everything the package writes inside it: new subfolders, a database file, a journal made and deleted next to it, a memory-mapped file, a file moved over another. A program outside the package sees all of it; nothing lands in `LocalCache` |
| A private copy of a folder (in `LocalCache\Local`) beside the real folder of that name (2026-11-14) | The package reads the **private** file first, lists both folders merged, and writes its new files and appends into the **private** copy; every other program sees only the real folder. Two folders under one name |
| That private copy moved to the real path by cmd's `move` (2026-11-14) | It becomes the real folder: the package reads its files there and writes for real afterwards; no private copy is left |
| `…\Packages\<family>\LocalState` by its real path | Written directly; deleted when the package is uninstalled |
| `%TEMP%` | Real |
| A child process that is **another program** (cmd, reg, PowerShell; by CreateProcess, ShellExecute, `runas`, a scheduled task, Explorer) | Runs **outside** the package's environment: its registry and file writes are real. Only `PROC_THREAD_ATTRIBUTE_DESKTOP_APP_POLICY` = 2 keeps descendants inside |
| A child process that is the **package's own program** | Keeps the package identity and the package's data folders, however it is started (CreateProcess, ShellExecute, desktop-app policy 1 or 4); measured for a loosely registered package and for a signed one installed in `C:\Program Files\WindowsApps` |
| DPAPI (`CurrentUser`, with entropy) | A blob sealed outside opens inside, and the reverse |
| Named mutexes and events (`Local\…`) | Shared with unpackaged processes, both ways |
| `StartupTask.RequestEnableAsync()` from a desktop package | `Enabled` at once, no prompt; `Disable()` works, and enabling again works |
| WinRT `Clipboard.GetHistoryItemsAsync()` with no window | `Success` |
| `CultureInfo.CurrentUICulture` | Windows' display language, whatever the manifest declares |
| `ApplicationLanguages.Languages` | The user's languages that the manifest declares |
| Uninstalling the package | Removes the package's data folder (redirected files, `LocalState`, its private hive); real folders and values stay, a folder made by cmd and written by the package included (2026-11-14) |

Manifest rules learned the hard way:

- A console alias is `uap5:AppExecutionAlias` with `desktop4:Subsystem="console"` (the `uap3` element refuses that
  attribute), and its `Application` must say `desktop4:SupportsMultipleInstances="true"` **[measured]**.
- **The Store refuses a package with an application hidden from Start** (`AppListEntry="none"`). Partner Center's
  upload check on 2026-11-13, for the first packages, whose `bclip` was a second, hidden application: "The package
  file … specifies a headless app. You don't have permission to create a headless app. Please update
  AppListEntry="none" in the AppxManifest file", and a waiver that Microsoft grants on request. MakeAppx packs such
  a manifest and Windows installs it; only the Store objects **[measured]**.
- **An application may declare one `windows.appExecutionAlias` extension.** MakeAppx: "The Extension element with
  Category attribute value "windows.appExecutionAlias" must only be declared once", also for one `uap3` and one
  `uap5` extension. All aliases of an extension start that extension's one `Executable`, which may differ from the
  application's **[measured]**. So one application can offer a console tool by name, or itself, not both.

## 3. Policies that shape the edition

| Policy | What it asks | What the Store edition does |
|---|---|---|
| 10.2.8 | "You are required to use supported methods and must obtain user consent to change any user's Windows settings, preferences, settings UI, or modify the user's Windows experience in any way." | A welcome window asks before Win+V is taken; Win+V is only ever taken with the documented keyboard hook. The undocumented `DisabledHotkeys` value ("Release from Explorer") is not offered |
| 10.5.5 | "Your product must also obtain express user consent before collecting, storing or transmitting such [highly sensitive] information." | Nothing is recorded until **Start** in the welcome window |
| 10.5.1 | A privacy policy URL; Desktop Bridge and Win32 products "must always have privacy policies" | The README's *Privacy* section, which names the Store edition |
| 10.1.1 | "The value proposition of your product must be clear during the first run experience"; a unique name | The welcome window; the name is reserved in Partner Center |
| 10.2.7 | "Your product must clearly communicate and enable a user's ability to cleanly uninstall and remove your product from their device." | Since 2026-11-14 the history is in `%LOCALAPPDATA%\BetterClipboard`, the folder every kind of install uses, and stays after an uninstall, as it does for those editions. *Settings › Data folder* says so, names the folder and opens it; *Clear history › Everything…* empties the history (before: the package's own folder, removed with the app) |
| 10.6 | Declared capabilities must relate to the product's functions | `runFullTrust` only, explained in the submission |
| 10.7 | The description is localized in every declared language | The package declares its five languages, and the listing has five descriptions |
| 10.2.2 | No dynamic inclusion of code that changes the described functionality | The self-updater is off in this edition; the Store updates it |

`unvirtualizedResources` (which would make registry writes real) is "designed for certain types of desktop PC games
that are published by Microsoft and our partners … not intended to be used for other scenarios" **[docs]**: not used.
Its finer form, flexible virtualization (`virtualization:ExcludedDirectories` in the manifest, Windows 11 only),
would exempt exactly `%LOCALAPPDATA%\BetterClipboard`, but needs the same capability **[docs]**, and a refused
capability fails the whole submission. The shared data folder is made by a program the package starts instead (§4.4),
on documented behavior: "If the file is opened from the real AppData location, then no virtualization for that file
occurs" **[docs]**.

## 4. The Microsoft Store edition

The same app, built as a package. A process knows it is the Store edition by its package identity
(`PackageIdentity.IsPackaged`, one kernel32 call); nothing is compiled differently except the packaging itself.

### 4.1 What is different

| | Installer and Chocolatey editions | Microsoft Store edition |
|---|---|---|
| History, settings, logs | `%LOCALAPPDATA%\BetterClipboard` | The same folder since 2026-11-14 (§4.4), kept when the app is uninstalled. Where Windows does not let the package write it for real, the package's own folder (`…\Packages\<family>\LocalState\BetterClipboard`, removed with the app); *Settings › Data folder* says which (1.2.11 always used the package's own folder) |
| Win+V | Released from Explorer by the installer (`DisabledHotkeys`), or the keyboard hook | Only the keyboard hook, only after the welcome's switch, only while the app runs. *Release from Explorer* is not in Settings |
| Start with Windows | A Run value in the registry | The package's startup task (`BetterClipboardStartup`), switched on by the welcome or Settings; Windows lists it under Settings › Apps › Startup, where the user can overrule it |
| Updates | The app asks GitHub once a day and installs with the user's approval | The Store. The app makes **no network request at all**; it asks Windows to start it again after an update (`RegisterApplicationRestart`) |
| `bclip` | A folder on the PATH (optional) | The package's one app execution alias; every new terminal runs it by name. The app itself has no alias (§2): Start, its tile and the startup task start it |
| Win+R history watch | A registry change notification | Polling the key's last-write time (notifications do not arrive in a package) |
| Windows' own clipboard history switch | A switch in Settings | A button that opens Windows' own Settings page (the app's registry write would land in its private hive) |
| Version shown by Windows | The release's (0.2.11) | The package's, (X+1).Y.Z.0 (1.2.11.0, §5.3); the app's own *About* shows the release's |

Shared with every edition: the single-instance lock (so two editions never run at once), the history's folder itself
(§4.4), the store format, the catalogs, every feature of the panel.

### 4.2 The first start

The owner's decisions (2026-11-12): **a welcome with consent, after the language picker**; **only one edition may be
installed**, and the Store edition moves the user over itself: checks first, then it brings the history over, checks
the copy, uninstalls the other copy and removes the old folder. Since 2026-11-14 (the owner's "Share it", §4.4) every
edition uses the same folder, so nothing is carried: the move is the other copy's uninstall. The old move remains for
a PC where this edition keeps its own folder.

```text
Start by hand
  └─ other copies and their data, one step at a time until nothing is left   (§4.3)
       └─ language picker, when no language was ever chosen
            └─ welcome: what is kept, "Open with Win+V" (on), "Start with Windows" (on), Privacy, Not now | Start
                 └─ Start: the choices are saved and applied, and only now does anything start
                    (the store, the clipboard listener, the shortcut, the tray icon)
```

- **Nothing before Start.** Until then this edition writes its log, at most the settings file, and the note that
  records which folder it uses (`LocalState\data-folder.json`, §4.4); a history another copy left in the shared
  folder is not opened. *Not now* and closing the window end the app; the welcome shows again at the next start.
- **The welcome's note.** After a move it says how many items came over. When the shared folder already held an
  earlier copy's history, it says "Your history and settings from the BetterClipboard you used before are kept: every
  kind of install uses the same folder." (the store is not opened to count them).
- **The switches.** *Open with Win+V* off: the panel opens with Alt+Win+V (any shortcut can be picked in Settings)
  and Win+V stays Windows'. A user who came from another copy keeps their own shortcuts; the switch only adds or
  removes Win+V. *Start with Windows* shows the startup task's real state and says so when Windows or an
  organization turned it off.
- **The setup's language and theme** are this edition's own saved choices, else those of the copy it replaces, else
  Windows'.
- **Only on a start by hand.** A start by Windows (sign-in, after a Store update) or by `bclip` never opens a window:
  while the welcome was not accepted, or while another copy of BetterClipboard is installed, such a start ends at
  once, before it takes the instance lock, so the other copy's own sign-in start is never turned away
  (`Program.ContinueStoreEditionInBackground`). A silent restart (CLAUDE.md §2.46) skips the setup too.
- **Never for an isolated instance** (`BETTERCLIPBOARD_DATA_DIR`): a test copy must not find, close or uninstall
  the user's installed app.

### 4.3 Another copy on the PC

`OtherEditionLocator` finds them from the records the updater already trusts; `EditionSetupPlan.Next` (pure,
tested) picks one step, and the setup asks again after each:

| Found | Step | What happens |
|---|---|---|
| A copy this user installed with the installer | **Move** | The page below: checks, then on *Move to this edition* the move |
| A copy for every user, or the Chocolatey copy | **Guide** | The page says how an administrator removes it (*Open Installed apps*, or the `choco uninstall` command to copy) and waits until it is gone. Such a copy belongs to the PC's administrator and every other user |
| A copy with no install record that is running | **Close** | On the user's go-ahead it is asked to exit |
| No copy, but a history one left behind, and this edition has none | **Import** | Copied, checked, old folder removed, without a question. Only while this edition keeps its own folder: in the shared folder that history simply is this edition's (§4.4) |

**The checks before a move** (nothing is changed by them):

1. *Your history can be read*: every file opens, and the drive has room (the data, a tenth more, 64 MB).
2. *This edition's folder can be written.*
3. *The other copy can be uninstalled from here*: the installer script is in the package, Windows PowerShell exists,
   a script file really runs on this PC, and this account may change what the uninstall changes. A probe script
   opens the Run key, the copy's Installed-apps entry, Explorer's settings and the environment key for writing,
   writes and deletes one file in the copy's folder, and opens its Start menu shortcut; it runs where the
   uninstaller will run (outside the package). What may not be changed is named in words.
4. *The old history folder can be removed afterwards.*

A failed check shows what failed and *Check again*. Lines 1 and 4 show only when a history is brought over: an
edition that already has a history never merges another one into it, and the page says where the other one stays.

**The move:**

1. **Close the other copy** (it stores what was copied first). This process takes the instance lock.
2. **Copy** `settings.json` and everything below `stores`, each file with a checksum against its original. The
   files are listed again right before the copy: closing the copy removes its database's write-ahead log.
3. **Check the copy**: it must open with this account's key (the same DPAPI key and machine binding, which work
   inside a package) and pass SQLite's own check. The app later goes on with this very store.
4. **Uninstall the other copy** with the `install.ps1` the package carries, in its uninstall mode
   (`-Uninstall -InstallDir … -KeepProcessId <this process> -ResultPath … -LogPath …`): startup entry, shortcut,
   Installed-apps entry, PATH entry and folder go, and Win+V is given back to Windows (Explorer restarts once).
   Success is proven: the copy's program and its entry must be gone.
5. **Remove the old data folder**, and only now.

**All or nothing.** The copy in this edition's folder is marked (`edition-move.pending`) until step 5 begins.
Whenever a move stops before that (the user quits, the uninstall fails, the PC loses power), the copy is removed
again, at once or at the next start, and the next attempt starts from the original. Quitting also starts the copy
that was closed for the move again. A second launch during the setup brings the same window forward.

**Why this package's script, not the installed copy's own:** older scripts close every BetterClipboard of the
session, which would end the Store edition mid-move, and scripts up to 0.2.5 can empty Explorer's settings key when
they give Win+V back (CLAUDE.md §2.25).

**In the shared folder (§4.4) the move is steps 1 and 4.** The page says "Your history, pins and settings stay as
they are: both copies use the same folder.", check lines 1 and 4 do not show, and nothing is copied or removed: the
uninstall keeps the folder, as every uninstall without `-RemoveData` does. The pages for a copy for every user and
for the Chocolatey copy say the history stays as it is. Guards in the code, so that the shared folder is never
treated as "the other copy's data": nothing is imported from it, the move's copy and the old folder's removal refuse
it, and a marker of an unfinished move found in it is committed, never rolled back (a rollback deletes the copied
stores, which there are the history itself).

**The installer says it too.** `install.ps1` warns, before it installs, when the Store edition is installed for
the account, and says whether the two use one history (`%LOCALAPPDATA%\BetterClipboard`) or keep separate ones. Its
`-Uninstall -RemoveData` of a per-user copy warns that the Store edition's history goes too when they share it.

### 4.4 The shared data folder (since 2026-11-14)

The owner asked (2026-11-14) whether the Store edition must stay out of the user profile, and whether it could
"actually require access to that one folder for interopability with all kinds of install a user can have on a machine
safely". The answer, chosen with an HTML question: **"Share it"**. The Store edition uses
`%LOCALAPPDATA%\BetterClipboard` like every other kind of install, through Windows' documented rule, proves that rule
on each PC, and falls back to its own folder when the proof fails. No restricted capability is asked for.

**Why it is allowed.** A full-trust package's file access is redirected only for **new** files and folders that it
creates directly in AppData; "if the file is opened from the real AppData location, then no virtualization for that
file occurs" **[docs]**. So a folder that exists for real is written for real (§2's rows, measured twice). The
documented way to opt a folder out of redirection, flexible virtualization, is a restricted capability (§3), and was
not used.

**What the app does** (`Core/Editions/StoreDataFolder.Establish`, called by `Windows/Packaging/StoreDataFolderSetup`
once per process, before anything opens the history, under the mutex `Local\BetterClipboard.StoreDataFolder`, so two
starts at the same moment never decide or move together):

1. **A private copy first.** A redirected copy of the folder (`…\LocalCache\Local\BetterClipboard`) would shadow the
   real one: the package would read its files first and write new files into it, while every other program sees the
   real folder (§2). It is moved, by cmd, to the real path when the real folder does not exist, else into the real
   folder as `recovered-private-copy-<time>`; nothing in it is deleted. When that fails, this start uses the
   package's own folder.
2. **The folder.** When `%LOCALAPPDATA%\BetterClipboard` is missing, cmd makes it (`mkdir`). cmd runs outside the
   package (§2), so the folder is real, and so is everything written into it afterwards.
3. **The proof, once per Windows build.** The app writes a probe file into the folder and asks cmd whether it exists;
   it then deletes the probe. The result goes into `LocalState\data-folder.json` with the Windows build (for example
   `10.0.26200.7171`): `"folder": "shared"`, or `"package"` with the reason. A new Windows build proves again.
4. **A history of version 1.2.11** (which kept everything in the package's own folder) moves into the shared folder,
   one rename by cmd for the stores and one for the settings, when the shared folder has no history. Shared settings
   written without a history (by an installer run with `-Hotkey`, for example) are set aside as
   `settings-set-aside-<time>.json`. When both folders hold a history, this edition keeps its own folder: two
   histories are never merged, and nothing is set aside or deleted. That holds until the Windows build changes.

**cmd, safely.** Every path reaches cmd in an environment variable (`BETTERCLIPBOARD_OUTSIDE_PATH1`, `…2`), and the
command is a constant (`/d /e:on /v:off /c mkdir "%BETTERCLIPBOARD_OUTSIDE_PATH1%"`): cmd expands the variable once
and never reads its value as syntax, so `%`, `&`, `^`, `!`, brackets and non-ASCII letters in a user name are just
characters (tested with ten such names). It is started like the silent restart's process (no busy pointer, no
window, no inherited handles), its exit code is the answer, and a move never goes to a target that exists (checked
first: cmd's `move` would ask, or put the folder inside it).

**Falling back.** Anything that fails (cmd cannot run, the folder cannot be made, the probe lands in a private copy,
a history cannot be moved) leaves this edition on the package's own folder, as in 1.2.11. A lasting reason is
recorded and not tried again until the Windows build changes; a passing one (a file in use) is tried again at the
next start. If a later Windows stopped writing such folders for real, the proof would notice it, and this edition
would start with its own, empty folder; the shared history would stay untouched for the other kinds of install.

**Who reads the record.** `bclip`, the package's console alias, never proves or moves anything: it uses the shared
folder only when the record says `"shared"` and no private copy exists (`StoreDataFolder.ResolveReadOnly`).
An isolated instance (`BETTERCLIPBOARD_DATA_DIR`) never touches any of this.

**What the user sees.** *Settings › Data folder* names the folder and says, in the Store edition, "Every kind of
BetterClipboard install uses this folder, and it stays when the app is uninstalled. To remove your history for good,
delete this folder." (or, in its own folder, "This edition's own folder: Windows deletes it when the app is
uninstalled."). The log names the decision at every start ("Microsoft Store edition: the history is in the shared
data folder, the one every kind of install uses; it stays when the app is uninstalled.").

**Uninstalling.** Windows removes the package and its own folders (the record included); the shared folder stays,
as it does after every other kind of uninstall. A Store edition installed again finds it, proves again, and uses it.

**For certification.** The notes for certification of the first submission (1.2.11) said the history is in the
package's own folder and removed with the app, which is true for that version. The kit's notes
(`docs/store-listing.md` §1, *Additional testing information*) now describe the shared folder; they are typed into
the next submission by hand before it is committed (the pipeline carries the notes over unchanged, §9).

## 5. The package

### 5.1 Manifest

[`packaging/msix/Package.appxmanifest`](../packaging/msix/Package.appxmanifest) is a template; the build fills in
the identity, the version and the architecture. Its comment explains every part. In short:

- **One application**, `BetterClipboard.exe`, full trust, `SupportsMultipleInstances` (every launch is a new
  process that finds the first through the instance lock, as in the other editions), with the startup task (off
  until the user says so).
- **`bclip`** is that application's execution alias: the console alias `bclip.exe`, whose extension names its own
  executable, `bclip.exe`. Until 2026-11-13 it was a second application hidden from Start, and the app had an
  alias `BetterClipboard.exe` of its own. The Store refused the first, and Windows allows one alias extension per
  application (§2), so the one alias went to the command line, which scripts need. Nothing in the app used its own.
- **Languages**: en-US, zh-Hans, ru, es, he.
- **Capability**: `runFullTrust` only.
- **Minimum Windows**: 10.0.19041 (Windows 10 2004), the app's own.

### 5.2 Identity

[`packaging/msix/identity.json`](../packaging/msix/identity.json) holds what Partner Center assigned when the name
was reserved (2026-11-13). None of it is a secret:

| | |
|---|---|
| Package/Identity/Name | `Nucs.BetterClipboard` |
| Package/Identity/Publisher | `CN=B31AB4B1-B96C-4DB9-83AE-B799D5192208` |
| Package/Properties/PublisherDisplayName | `Nucs` |
| Package family name | `Nucs.BetterClipboard_mgez7y7xa0my4` |
| Store ID | `9PHL574X9BF9` (`https://apps.microsoft.com/detail/9PHL574X9BF9` once it is published) |

The build computes the family name from the name and the publisher (the publisher's SHA-256, its first eight bytes
in Windows' base 32) and refuses to make a Store package when it differs from the file's `packageFamilyName`: a
mistyped publisher would otherwise show only as a refused upload. The family name is also where the edition keeps
its data (`%LOCALAPPDATA%\Packages\<family name>`).

### 5.3 Version

The package version is **(X+1).Y.Z.0** for the release X.Y.Z: 0.2.11 ships as 1.2.11.0. The Store asks for four
numbers, keeps the fourth for itself (it must be 0) and refuses a first number of 0 **[docs]**, and the releases are
0.x. One is added for every release, for good: a rule that stopped adding at 1.0.0 would make that package
(1.0.0.0) older than the last 0.x one, and the Store only ever offers the highest version.

### 5.4 Logos

[`tools/make_store_assets.py`](../tools/make_store_assets.py) draws the 35 PNGs in `packaging/msix/Assets` from the
app icon's own drawing (`tools/make_icon.py`): the 44-pixel logo at five scales and five target sizes, plated,
unplated and for the light taskbar, the 150-pixel and wide tiles, and the Store logo. It also writes the listing's
300-pixel app tile icon into `packaging/msix/Listing` (uploaded by hand; not part of the package).

### 5.5 Building

```powershell
pwsh tools/release/package-msix.ps1 -Version 0.2.11 -ForStore                             # for a submission
pwsh tools/release/package-msix.ps1 -Version 0.2.11 -Architectures x64 -NewTestCertificate # for a test desktop
```

For each architecture [`package-msix.ps1`](../tools/release/package-msix.ps1) publishes `bclip`, fills in the
manifest, publishes the app with `WindowsPackageType=MSIX` (which makes the package: the app, `bclip`, the logos,
`install.ps1`), and then checks the package: identity, version and architecture as asked; nothing in the manifest
that the Store refuses at upload (one application, none hidden from Start, `bclip` as its console alias; §2); the
app, `bclip`, the installer script and the resource index inside; everything `bclip` needs inside; under 250 MB
(the Store's limit is 25 GB; a package that size means something else got in). Output:
`artifacts/store/BetterClipboard-<version>-store-<arch>.msix` (70.6 MB for x64).

- **For the Store** the package is unsigned: the Store signs what it publishes. `-ForStore` refuses a placeholder
  identity and a test certificate.
- **For a test** the package is signed with a self-signed certificate whose subject becomes the publisher.
  `-NewTestCertificate` makes one (30 days, in memory, written to a `.pfx` and a `.cer`; nothing is added to a
  certificate store of the build PC).

### 5.6 Trying a package

Only in a disposable test desktop (CLAUDE.md, claude-desktops), never on the owner's PC:

```powershell
Import-Certificate -FilePath BetterClipboard-test.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople
Add-AppxPackage BetterClipboard-<version>-store-x64.msix
```

For repeated tries, unpack the package and register the folder (Developer Mode): `Add-AppxPackage -Register
<folder>\AppxManifest.xml`. A changed file is then replaced in place. The entries of a package are percent-encoded
and must be decoded when unpacking; the signature, the block map and `[Content_Types].xml` are left out.

## 6. Verified

Everything below ran on 2026-11-12 and 2026-11-13 in one disposable Windows 11 25H2 desktop (26200, Tiny11,
1600 × 900, no GPU, an administrator with UAC off), unless a row names another desktop of the same kind, with BC-TEST
data only. Each was deleted afterwards.

| What | Result |
|---|---|
| A fresh install: language picker (five languages), then the welcome | Before *Start* the data folder held a log and the settings file with the welcome not accepted, and no store. After *Start*: the encrypted store, "Win+V is owned by another component; intercepting it with a keyboard hook", the startup task `Enabled`, 0 warnings |
| Registry after that | The real Run key empty, `DisabledHotkeys` absent: nothing written outside the package |
| Use | A copy captured, Win+V opened the panel next to Notepad's caret, Enter pasted the second-newest item into Notepad; `bclip status` and `list` through the alias; `BetterClipboard.exe --show-flyout` and `--exit` through the app's own alias, which the manifest had until 2026-11-13; a Win+R run recorded by the poll |
| Settings | No *Release from Explorer*; *Version … · Microsoft Store edition*; *Updates from the Microsoft Store* with *Open in Microsoft Store*; Windows' clipboard history as a button; the alias sentence instead of the PATH button; the startup switch turned the task off and on |
| Silent restart after Settings closed | The new process was the Store edition with the same data folder; shortcuts held; 0 warnings |
| Sign-in (a guest reboot) | The startup task started the app 37 s after boot, without arguments, into the tray, no window |
| The move from the released 0.2.10 (installed with the README's command, Russian, 3 items with a pin, the command line on, Win+V released from Explorer, on the PATH), the app running | The setup showed in Russian. Four checks passed. *Move*: closed, 3 files and 160,225 bytes copied and checked ("3 items"), uninstalled in 3 s, old folder removed. Afterwards: no Run value, no Installed-apps entry, no shortcut, no install folder, the PATH entry gone, `DisabledHotkeys` empty, Explorer restarted once. The welcome said "Brought over 3 items and your settings", no language question. After *Start*: the same 3 items with the pin, Russian, the command line on, Win+V through the hook |
| A check that fails (write access to the copy's folder taken away) | The third line failed and named the folder; *Check again* passed once access was back |
| Quit on the first page | The app ended; the other copy and everything else untouched |
| Quit after a failed uninstall step | The copied history was removed again, the closed copy was started again with its 3 items |
| A second launch while the setup was open | The same window came forward; no second process stayed |
| The installer run while the Store edition is installed | It warned before installing |
| A background start while another copy is installed (running, and not running) | Ended at once, before the lock, with a log line; the other copy kept running |
| The move into an edition that already has a history | The page said the other history stays where it is; the copy was uninstalled, its data folder left alone, the app started with its own history |
| A history left behind by an uninstalled copy, a fresh Store edition | Imported without a question (4 items), old folder removed, English from the imported settings |
| A background start before the welcome | Ended at once, with a log line, nothing recorded |
| *Not now* on the welcome, then a start by hand | The app ended; the welcome showed again, the imported history intact |
| *Open with Win+V* switched off | "Alt+Win+V registered with RegisterHotKey (no keyboard hook needed)" |
| **The signed package, installed** (`Add-AppxPackage` of the `.msix`; it runs from `C:\Program Files\WindowsApps\…_1.2.11.0_x64__…`), with the released 0.2.10 installed again in Hebrew and running | Setup and welcome in Hebrew. The move: 3 files and 160,216 bytes copied and checked ("5 items"), uninstalled, old folder removed, the real registry clean. After *Start*: the panel in Hebrew with the 5 items, Settings in Hebrew, Win+V through the hook |
| Silent restart of the installed package | The new process ran from the `WindowsApps` folder (`--background --restart-after …`) with the same data folder: "the shortcuts were held during the switch", the hook installed again, 0 warnings |
| Removing the package (the loose registration several times, and the installed package) | No package, no data folder, no aliases, no process, the `WindowsApps` folder gone; the real Run key empty and `DisabledHotkeys` absent. Win+V opened Windows' own clipboard panel right afterwards |
| **The Store's upload check** (Partner Center, the first submission's draft, 2026-11-13) | The first packages (`bclip` as a hidden second application) were refused: "specifies a headless app" (§2). The rebuilt ones (one application, `bclip` as its alias; `1.2.11.0`, x64 70.6 MB and ARM64 68.5 MB) were accepted: *Validated*, with only the expected note that `runFullTrust` needs approval |
| **The one-application package, installed** (its test-signed twin, in two more disposable desktops, 2026-11-13) | Installed. Start lists one entry, "BetterClipboard". `where bclip` finds the alias; `bclip --version` prints to the calling console and returns 0; `bclip status` with the command line off returns 3 with its message. With the app stopped and the command line on, `bclip status` started the app by itself from the `WindowsApps` folder and answered from the Store edition's data. No `BetterClipboard.exe` alias exists |
| First start, use and silent restart in that package (the session the listing's screenshots come from) | Language picker, welcome, *Start*; Win+V through the hook; copies from Notepad, Explorer, Edge and Snipping Tool; a pin; three groups by drag and drop; a search with a toggle; the image preview; Settings. Two silent restarts ("the shortcuts were held during the switch"). The log: 109 lines, 0 warnings |
| **The shared data folder, measured** (2026-11-14, `msix_lab`'s `shared` section, in another disposable desktop) | A folder made by cmd, then written by the package: subfolders, a journal made and deleted, a memory-mapped file and a file moved over another were all real and seen from outside; nothing landed in `LocalCache`. A private copy beside the real folder: the package read and wrote the private one, the outside saw only the real one. That copy moved to the real path by cmd: real from then on. After the package was uninstalled, the folder made by cmd stayed with its files |
| **The shared data folder in the app** (2026-11-14, a Store package of 0.2.12 test-signed for the Store's publisher, in a disposable desktop on this PC) | 1.2.11 installed first, with BC-TEST copies and a pin in its own folder. 1.2.12 installed over it as an update: "Made the shared data folder", "written for real on this Windows build (10.0.26200.7171)", "Moved this edition's history …", "… settings into the shared data folder"; the same store and key ("new key: False"), no language picker, no welcome; *Settings › Data folder* named the shared folder with its note. The released 0.2.11, installed with `install.ps1`, warned that the two use the same history, ran on it and added a copy. The Store edition's setup offered the move with the shared wording and two check lines; *Move to this edition* uninstalled the installer copy ("Your history was kept"), and the Store edition went on with the same store |
| Removing the package, and installing it again (the same desktop) | The package and its own folders were gone, the record included; `%LOCALAPPDATA%\BetterClipboard` stayed with its store, key, settings and logs. Installed again over that history with the welcome not accepted (an installer copy's settings): the proof ran again, no picker, no move, and the welcome said "Your history and settings from the BetterClipboard you used before are kept…". After *Start*: the same store, the panel with all four copies (the installer copy's among them), the pin. `bclip status` started the app by itself and listed the same four |

The other editions share the changed start-up code, so the unpackaged build was run once more after the change, as
an isolated instance on the owner's PC while nobody used it: it started in the tray and ended on `--exit` with 0
warnings, and its Settings showed *Release from Explorer*, the update switch and *Start with Windows*, and none of
the Store edition's cards.

Faults the live runs found, all fixed and covered by tests where a test can see them: the setup crashed on the
theme (the settings were not loaded yet); it ignored the replaced copy's language; the copy step used a file list
made before the other copy closed; PowerShell refused the uninstall's command line (two report parameters belonged
to the install parameter set only); a failed check named its finding with an internal word; the window was too
short for four lines and an error.

Unit tests: `EditionTests` (Core, 36) and `EditionUninstallerTests` (Windows, 13: the check and the uninstall run
through the real Windows PowerShell against a stand-in script and scratch folders; the real `install.ps1`'s
parameter sets are read with `Get-Command`, which runs nothing). The whole suite: 2,151 tests, 0 failed. For the
shared data folder (2026-11-14): `StoreDataFolderTests` (Core, 24: the decision over a stand-in for cmd, every branch
of §4.4) and `StoreDataFolderSetupTests` (Windows, 13: the real cmd with ten folder names that would break a command
line, and a 1.2.11 history moved in under a folder named `Local App%Data%`); the whole suite then: 2,402 tests, 0
failed.

**Not verified:** the guided pages for a copy for every user and a Chocolatey copy (the plan is unit-tested; the
pages were not seen); the Windows App Certification Kit; ARM64 on a device (the package builds); a standard user
account and UAC on; a real GPU; a
package served by the Store (signing, updates, the restart after an update); Spanish and Chinese setup pages on
screen (English, Russian and Hebrew were); the release pipeline's Store step against the real service (§7).

## 7. The release pipeline

A release tag (`v*`, stable versions only) builds the Store packages and sends them to the Store after the GitHub
release is published ([`release.yml`](../.github/workflows/release.yml)):

1. **Packages**: `package-msix.ps1 -ForStore`, before anything is published; a release whose Store package cannot
   be built does not go out half. They are kept as the run's artifact `store-packages` for 90 days. An unsigned
   package installs nowhere, so it is not attached to the release page.
2. **Submission**: [`submit-store.ps1`](../tools/release/submit-store.ps1), the release's last step, with
   Microsoft's Store submission API **[docs]**. A new submission is made as a copy of the published one; its
   packages are replaced by the two new ones; "What's new" in the English listing gets the tag's notes, cut to the
   Store's 1,500 characters (each lead-in with its first sentence, the fixes as a count), and is emptied in the
   other languages; the packages go up in one archive; the submission is committed, and its status is followed
   until the Store has taken it into certification. Listing texts, pictures, price and age rating are carried
   over untouched from the published submission.
3. **What the step refuses**, before it changes anything: packages whose identity is not the reserved one, signed
   or missing packages, a pre-release version, a version that is not higher than the published one. A pending
   submission in Partner Center is deleted first (the Store allows one), unless `-KeepPending` is given.

**What it needs, and what happens without it.** Three repository secrets, `PARTNER_CENTER_TENANT_ID`,
`PARTNER_CENTER_CLIENT_ID` and `PARTNER_CENTER_CLIENT_SECRET`: a Microsoft Entra application that the developer
account trusts (below). Without them the step warns, and the release is complete without the Store (exit code 3).
The same holds while the product has no *published* submission (exit code 4): Microsoft's API only updates an app
that was published once, so the first submission is made in Partner Center (§8). Any other failure makes the run
red; the GitHub release is published by then.

**By hand**: *Actions › Microsoft Store › Run workflow* ([`store.yml`](../.github/workflows/store.yml)). `check`
signs in with the secrets and reads the app, changing nothing: run it after setting or renewing the secrets.
`submit` builds a released version's packages from its tag and submits them, for a release whose own step was
skipped or failed. `draft` does the same without the commit.

**One rule to remember:** a submission the API made must not be edited in Partner Center before it is committed;
the API can then no longer commit it **[docs]**. Change the listing between releases through the kit and an
import (`store-listing.md` §4), or in Partner Center on a submission started there.

**Setting it up** (once; it waits for the owner's word, and the owner has to sign in once). What Microsoft's
pages ask, read on 2026-11-13 **[docs]**:

- **To associate a tenant** with the developer account: "On the Microsoft Partner Center sign in page, enter the
  Microsoft Entra ID credentials for the tenant that you want to associate." A Global administrator is not needed
  for that ("you don't need global administrator permission in order to associate the tenant"). A personal
  Microsoft account is not taken there, also when it administers the tenant **[measured, 2026-11-13]**: it must
  be a work account of the tenant.
- **To add the application**: "You must be signed in with a Manager account that also has global administrator
  permissions for the Microsoft Entra ID tenant you're working in."
- **Such an account needs a second factor.** Security defaults, which a directory made since October 2019 may
  have switched on, make every user register multi-factor authentication at the first sign-in (the 14 days of
  grace ended on 2024-07-29) and ask administrators for it at every sign-in. The second factor is the Microsoft
  Authenticator app, or "any non-Microsoft application using OATH TOTP".

The developer account is the owner's personal Microsoft account, so neither step can be done with it alone. The
tenant to use is the owner's own Azure directory: the Azure CLI on the owner's PC is signed in as its
administrator, so the application and its key can be made there without the key ever being shown. That directory
holds one user, the owner's personal account **[measured]**, so a work account has to be made first. The plan:

1. **Claude, with `az`**, on the owner's word: a work account in the directory for this one purpose, with the
   Global Administrator role. Its first password goes into a file for the owner, never onto a screen or into a
   log. And the application with its service principal (`az ad app create`, `az ad sp create`), without a key.
2. **The owner, once**: *Partner Center › Account settings › Tenants › Associate Microsoft Entra ID*. Sign in with
   the work account, set up its second factor when asked, and confirm the tenant.
3. **The owner, or Claude in a browser of its own**, with Partner Center signed in *as the work account*:
   *Account settings › User management › Microsoft Entra applications › Add Microsoft Entra application*, the
   application of step 1, role **Manager**.
4. **Claude**: the three secrets, the key by pipe, so that it never appears on a screen or in a log; the tenant
   ID and the client ID with `gh secret set` too. Then *Microsoft Store › check*.

   ```bash
   az ad app credential reset --id <client id> --query password -o tsv \
     | gh secret set PARTNER_CENTER_CLIENT_SECRET --repo Nucs/BetterClipboard
   ```

Not known until it is done:

- whether the association makes the work account a Manager of the developer account by itself (Microsoft's pages
  do not say, and step 3 needs it);
- whether security defaults are on in the owner's directory: the Azure CLI's sign-in may not read the
  directory's sign-in policies ("required scopes are missing in the token" **[measured]**), so step 2 shows it;
- what *check* answers while the product has no published submission;
- whether the work account can lose its administrator role once the application is added.

**The key runs out.** A key made by `az ad app credential reset` lasts one year unless `--years` or `--end-date`
says otherwise. After that the Store step fails, and the run is red. Renew it with the pipe of step 4, then run
*check*.

The script was never run against the real service: a first submission must be published first. It is tested
against a stand-in that answers like the documented API and records every request
([`test-submit-store.ps1`](../tools/release/test-submit-store.ps1), 39 checks, run by CI): the order of the calls,
what is sent, the upload in blocks, every refusal, the retry of a failed block, and that neither the secret, the
token nor the upload address is ever printed.

## 8. Where the submission stands, and the owner's steps

**Submitted for certification on 2026-11-13, about 22:53 local** ("Product submission: In certification"; every
page read-only until certification ends). What was submitted:

| Page | State | What is there |
|---|---|---|
| Product | Reserved | **BetterClipboard**, an MSIX app; Store ID `9PHL574X9BF9` |
| Pricing and availability | Complete | Free, all markets, public, as soon as it is certified |
| Properties | Complete | Utilities + tools, Productivity; the privacy policy, website and support links |
| Age ratings | Complete | Every answer "No": ESRB Everyone, PEGI 3, IARC 3+; the IARC agreement ticked on the owner's word |
| Packages | Complete | `1.2.11.0` for x64 and ARM64: the v0.2.11 release's own (run 43478898156's artifact), validated |
| Store listings | Complete | Five languages: texts, search terms, eight screenshots with captions, the tile icon. Checked against the kit after the last import (`make-store-listing-csv.py --check --screenshots 8`): every text, every caption under its picture, every language in the kit's order |
| Submission options | Complete | Publish when certified; the `runFullTrust` justification |
| Additional testing information | Saved | The notes for certification |

[`store-listing.md`](store-listing.md) is what was entered, word for word.

**Decided by the owner on 2026-11-13** (review pages made from the kit; CLAUDE.md §3.5 has their words):

- The listing's name, publisher, texts, category, links and price stand. **The six screenshots are to be
  replaced**, as the last step, after asking the owner again what they want instead.
- All five languages go out. Native speakers are asked to review the Russian and the Spanish translation
  (GitHub issues #4 and #5).
- The version rule of §5.3 stands: the releases stay 0.2.x, and the Store package is (X+1).Y.Z.0. The Store
  cannot take 0.2.11.0: "The other sections must be set to an integer between 0 and 65535 (except for the first
  section, which cannot be 0)."
- **Nothing is released or submitted until the owner says so in words**: first "release", then "publish".
- **Then, after the release** (2026-11-13): the screenshots retaken on 0.2.11 in English, with data from the
  owner's own history screened by hand ([`tools/readme/demo-data.zip`](../tools/readme/demo-data.zip)); and "You
  tick and submit", with "to clarify: you the asisstant do it all for me through browser".

Left:

1. ~~The screenshots~~: done (eight scenes; `store-listing.md` §2 and §4).
2. ~~Age ratings~~: ticked and saved, on the owner's word.
3. ~~Decide the version rule~~: decided, (X+1).Y.Z.0 (above).
4. ~~Release~~: v0.2.11; its two packages replaced the draft's.
5. ~~Submit for certification~~: submitted. It takes up to a few business days; a first submission with a
   restricted capability may take longer. A failure comes with a report; fix and resubmit (a new submission made
   from this one).
6. **Afterwards**: the Store link in the README and in the installer's Store-edition warning.
7. **The pipeline's connection** (§7, "Setting it up"), on the owner's word: it needs one sign-in by the owner,
   and can be made at any time. From the first published submission on, each release then submits itself.

**The name.** "Better Clipboard", in two words, is the title of other products: a Store app (PAST-E Clipboard,
`9NR75C5JPVVX`) and a Mac app (betterclipboard.com). Partner Center reserved "BetterClipboard" without objection;
certification may still ask about it (policy 10.1.1). The fallback is a second reserved name, "Nucs
BetterClipboard", the name chosen for code signing (CLAUDE.md §3.3).

## 9. Limits and next steps

- **A copy for every user and a Chocolatey copy are not removed by the Store edition**; it waits for an
  administrator. Its two pages were not seen on screen.
- **Two histories are never merged.** An edition that has a history leaves another copy's history where it is. With
  the shared folder (§4.4) this comes up only when version 1.2.11's own history meets a history in the shared folder:
  this edition then keeps its own folder until the Windows build changes, and *Settings › Data folder* says so.
- **The shared folder rests on a documented rule** (a folder that exists for real is not virtualized), proven once
  per Windows build. A Windows that changed it would put this edition back on its own, empty folder; the shared
  history would stay untouched for the other kinds of install.
- **Two processes write one log during the move.** While the setup waits for the other copy to close, both write
  `logs\betterclipboard-<date>.log` in the shared folder, and the app's appends from two processes can overwrite each
  other's lines: the live run lost the Store edition's "starting" line. The same can happen whenever two
  BetterClipboard processes run at once (CLAUDE.md §6).
- **The move needs Windows PowerShell and script files that run.** On a PC whose policy blocks scripts, the check
  says so, and the other copy has to be uninstalled in Windows' Installed apps.
- **The package version differs from the release version** by one in its first number (§5.3).
- **The release's Store step was never run against the real service** (§7): that needs a published first
  submission and the three secrets. Until then it warns and the release goes out without the Store.
- **The pipeline signs in with a stored key**, which runs out (§7). A sign-in without one, with GitHub's OpenID
  Connect token and a federated credential on the application, would spare the renewal. Not built: it cannot be
  tried before the connection exists.
- **The pipeline updates packages and "What's new", nothing else.** Listing texts and pictures change by hand: the
  kit, then an import (`store-listing.md` §4).
- **The screenshots are English in all five listings**: the app in English, with demo data from the owner's own
  history (screened by hand; `tools/readme/demo-data.zip`). Localized pictures would be the same eight scenes once
  per language.
- **The listing's translations** (Chinese, Russian, Spanish, Hebrew) were written by Claude; no native speaker has
  read them.
- **ARM64**: the package builds and passes the script's checks (68.5 MB, version 1.2.11.0); it was never run on a
  device.

## 10. Sources

- Microsoft Store Policies 7.20: `https://learn.microsoft.com/en-us/windows/apps/publish/store-policies`
- Free registration: `https://blogs.windows.com/windowsdeveloper/2025/09/10/free-developer-registration-for-individual-developers-on-microsoft-store/`
  and `https://blogs.windows.com/windowsdeveloper/2026/05/07/publish-to-microsoft-store-as-a-company-now-with-free-registration-and-faster-onboarding/`
- Publishing a first app: `https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/publish-first-app`
- Package requirements (formats, size, versions, languages, signing), MSIX:
  `https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/app-package-requirements`;
  EXE/MSI: `https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msi/app-package-requirements`
- Reserving a name: `https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/reserve-your-apps-name`
- Store listing fields: `https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/add-and-edit-store-listing-info`
- Screenshots and images: `https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/screenshots-and-images`
- Properties: `https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/enter-app-properties`
- Age ratings: `https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/age-ratings`
- Submission options, notes for certification, restricted capabilities:
  `https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/manage-submission-options`
- How packaged desktop apps run: `https://learn.microsoft.com/en-us/windows/msix/desktop/desktop-to-uwp-behind-the-scenes`
- Desktop extensions (startup task, aliases): `https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/desktop-to-uwp-extensions`
- Capabilities: `https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/app-capability-declarations`
- The existing listing with a similar name: `https://apps.microsoft.com/detail/9NR75C5JPVVX`
- The Store submission API: `https://learn.microsoft.com/en-us/windows/uwp/monetize/create-and-manage-submissions-using-windows-store-services`
  and `https://learn.microsoft.com/en-us/windows/uwp/monetize/manage-app-submissions`
- Importing and exporting listings: `https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/import-and-export-store-listings`
- Associating a Microsoft Entra tenant with the developer account:
  `https://learn.microsoft.com/en-us/windows/apps/publish/partner-center/associate-existing-azure-ad-tenant-with-partner-center-account`;
  a new tenant made by Partner Center:
  `https://learn.microsoft.com/en-us/windows/apps/publish/partner-center/create-new-azure-ad-tenant`
- Microsoft Entra applications in a developer account (who may add one, roles, keys):
  `https://learn.microsoft.com/en-us/windows/apps/publish/partner-center/manage-azure-ad-applications-in-partner-center`;
  users of the account: `https://learn.microsoft.com/en-us/windows/apps/publish/partner-center/overview-users-groups-azure-ad-applications`
- Security defaults (multi-factor authentication for every user, at every sign-in for administrators):
  `https://learn.microsoft.com/en-us/entra/fundamentals/security-defaults`
- The Microsoft Store Developer CLI, considered for the pipeline (its `publish` takes one package file, and this
  edition ships one per architecture): `https://learn.microsoft.com/en-us/windows/apps/publish/msstore-dev-cli/commands`
- The "headless app" refusal as other projects met and solved it (2026-10 and 2026-11; third parties, no Microsoft
  page describes the rule): `https://github.com/opengeos/RustGIS/pull/374`, `https://github.com/dafer238/dfstudio/pull/28`,
  and for the waiver `https://mouri.moe/en/2021/12/27/The-way-to-submit-a-headless-app-to-the-Microsoft-Store/`
