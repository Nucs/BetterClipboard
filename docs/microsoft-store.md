# BetterClipboard in the Microsoft Store

Owner's request (2026-11-12): "I want to publish my product to microsoft marketplace". This document is the full
report: where a desktop app is published, what Microsoft requires, how a packaged app behaves (measured), the design
of the Microsoft Store edition, how the package is built, what was verified, and what the owner does in Partner
Center. The texts to paste there are in [`store-listing.md`](store-listing.md).

Legend: **[docs]** = Microsoft documentation read on 2026-11-12 · **[measured]** = measured that day in a disposable
Windows 11 25H2 desktop (26200, Tiny11) with [`tools/probes/msix_lab`](../tools/probes/msix_lab) · **[policy]** =
Microsoft Store Policies 7.20 (published 2026-09-15, effective 2026-10-22).

**State (2026-11-13):** the Store edition and its package build are in the repository and were run end to end in a
test desktop. Nothing was submitted: an account, a reserved name and the listing are the owner's (§7).

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
| Uninstalling the package | Removes the package's data folder (redirected files, `LocalState`, its private hive); real folders and values stay |

Manifest rules learned the hard way **[measured]**: a console alias is `uap5:AppExecutionAlias` with
`desktop4:Subsystem="console"` (the `uap3` element refuses that attribute), and its `Application` must say
`desktop4:SupportsMultipleInstances="true"`.

## 3. Policies that shape the edition

| Policy | What it asks | What the Store edition does |
|---|---|---|
| 10.2.8 | "You are required to use supported methods and must obtain user consent to change any user's Windows settings, preferences, settings UI, or modify the user's Windows experience in any way." | A welcome window asks before Win+V is taken; Win+V is only ever taken with the documented keyboard hook. The undocumented `DisabledHotkeys` value ("Release from Explorer") is not offered |
| 10.5.5 | "Your product must also obtain express user consent before collecting, storing or transmitting such [highly sensitive] information." | Nothing is recorded until **Start** in the welcome window |
| 10.5.1 | A privacy policy URL; Desktop Bridge and Win32 products "must always have privacy policies" | The README's *Privacy* section, which names the Store edition |
| 10.1.1 | "The value proposition of your product must be clear during the first run experience"; a unique name | The welcome window; the name is reserved in Partner Center |
| 10.2.7 | The product can be uninstalled cleanly | The history lives in the package's own folder, which Windows removes with the app |
| 10.6 | Declared capabilities must relate to the product's functions | `runFullTrust` only, explained in the submission |
| 10.7 | The description is localized in every declared language | The package declares its five languages, and the listing has five descriptions |
| 10.2.2 | No dynamic inclusion of code that changes the described functionality | The self-updater is off in this edition; the Store updates it |

`unvirtualizedResources` (which would make registry writes real) is "designed for certain types of desktop PC games
that are published by Microsoft and our partners … not intended to be used for other scenarios" **[docs]**: not used.

## 4. The Microsoft Store edition

The same app, built as a package. A process knows it is the Store edition by its package identity
(`PackageIdentity.IsPackaged`, one kernel32 call); nothing is compiled differently except the packaging itself.

### 4.1 What is different

| | Installer and Chocolatey editions | Microsoft Store edition |
|---|---|---|
| History, settings, logs | `%LOCALAPPDATA%\BetterClipboard` | `%LOCALAPPDATA%\Packages\<family>\LocalState\BetterClipboard`, removed by Windows with the app |
| Win+V | Released from Explorer by the installer (`DisabledHotkeys`), or the keyboard hook | Only the keyboard hook, only after the welcome's switch, only while the app runs. *Release from Explorer* is not in Settings |
| Start with Windows | A Run value in the registry | The package's startup task (`BetterClipboardStartup`), switched on by the welcome or Settings; Windows lists it under Settings › Apps › Startup, where the user can overrule it |
| Updates | The app asks GitHub once a day and installs with the user's approval | The Store. The app makes **no network request at all**; it asks Windows to start it again after an update (`RegisterApplicationRestart`) |
| `bclip` | A folder on the PATH (optional) | An app execution alias; every new terminal runs it by name |
| Win+R history watch | A registry change notification | Polling the key's last-write time (notifications do not arrive in a package) |
| Windows' own clipboard history switch | A switch in Settings | A button that opens Windows' own Settings page (the app's registry write would land in its private hive) |
| Version shown by Windows | The release's (0.2.11) | The package's, (X+1).Y.Z.0 (1.2.11.0, §5.3); the app's own *About* shows the release's |

Shared with every edition: the single-instance lock (so two editions never run at once), the store format, the
catalogs, every feature of the panel.

### 4.2 The first start

The owner's decisions (2026-11-12): **a welcome with consent, after the language picker**; **only one edition may be
installed**, and the Store edition moves the user over itself: checks first, then it brings the history over, checks
the copy, uninstalls the other copy and removes the old folder.

```text
Start by hand
  └─ other copies and their data, one step at a time until nothing is left   (§4.3)
       └─ language picker, when no language was ever chosen
            └─ welcome: what is kept, "Open with Win+V" (on), "Start with Windows" (on), Privacy, Not now | Start
                 └─ Start: the choices are saved and applied, and only now does anything start
                    (the store, the clipboard listener, the shortcut, the tray icon)
```

- **Nothing before Start.** Until then the data folder holds a log and, at most, the settings file. *Not now* and
  closing the window end the app; the welcome shows again at the next start.
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
| No copy, but a history one left behind, and this edition has none | **Import** | Copied, checked, old folder removed, without a question |

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

**The installer says it too.** `install.ps1` warns, before it installs, when the Store edition is installed for
the account.

## 5. The package

### 5.1 Manifest

[`packaging/msix/Package.appxmanifest`](../packaging/msix/Package.appxmanifest) is a template; the build fills in
the identity, the version and the architecture. Its comment explains every part. In short:

- **App**: `BetterClipboard.exe`, full trust, `SupportsMultipleInstances` (every launch is a new process that finds
  the first through the instance lock, as in the other editions), the startup task (off until the user says so),
  and the alias `BetterClipboard.exe`.
- **Bclip**: `bclip.exe`, hidden from Start, with the console alias `bclip.exe`.
- **Languages**: en-US, zh-Hans, ru, es, he.
- **Capability**: `runFullTrust` only.
- **Minimum Windows**: 10.0.19041 (Windows 10 2004), the app's own.

### 5.2 Identity

[`packaging/msix/identity.json`](../packaging/msix/identity.json) holds the four values Partner Center assigns when
the name is reserved. Until then it holds placeholders with `"reserved": false`, and the build refuses to make a
package for the Store.

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
`install.ps1`), and then checks the package: identity, version and architecture as asked; the app, `bclip`, the
installer script and the resource index inside; everything `bclip` needs inside; under 250 MB (the Store's limit
is 25 GB; a package that size means something else got in). Output:
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
1600 × 900, no GPU, an administrator with UAC off), with BC-TEST data only. It was deleted afterwards.

| What | Result |
|---|---|
| A fresh install: language picker (five languages), then the welcome | Before *Start* the data folder held a log and the settings file with the welcome not accepted, and no store. After *Start*: the encrypted store, "Win+V is owned by another component; intercepting it with a keyboard hook", the startup task `Enabled`, 0 warnings |
| Registry after that | The real Run key empty, `DisabledHotkeys` absent: nothing written outside the package |
| Use | A copy captured, Win+V opened the panel next to Notepad's caret, Enter pasted the second-newest item into Notepad; `bclip status` and `list` through the alias; `BetterClipboard.exe --show-flyout` and `--exit` through the alias; a Win+R run recorded by the poll |
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
parameter sets are read with `Get-Command`, which runs nothing). The whole suite: 2,151 tests, 0 failed.

**Not verified:** the guided pages for a copy for every user and a Chocolatey copy (the plan is unit-tested; the
pages were not seen); the Windows App Certification Kit; ARM64 on a device (the package builds); a standard user
account and UAC on; a real GPU; a
package served by the Store (signing, updates, the restart after an update); Spanish and Chinese setup pages on
screen (English, Russian and Hebrew were).

## 7. The owner's steps

1. **Account.** `https://storedeveloper.microsoft.com`, individual, free. Use the Microsoft account that should own
   the app for good. Verify the email address in Partner Center's Action Center, or certification mail is lost.
2. **Reserve the name.** Partner Center › Apps and games › *New product* › *MSIX or PWA app* › *Check
   availability* › *Reserve product name*. A reservation that is not used within three months is removed.
   - **Name risk:** "Better Clipboard" exists in the Store already (PAST-E Clipboard, `9NR75C5JPVVX`, whose title
     reads "Better Clipboard"). If "BetterClipboard" is refused or looks too close, reserve **"Nucs
     BetterClipboard"**, the name chosen for code signing (CLAUDE.md §3.3). Several names can be reserved.
3. **Copy the identity.** Product management › *Product identity*: `Package/Identity/Name`,
   `Package/Identity/Publisher`, `Package/Properties/PublisherDisplayName`. Put them, with the reserved name as
   `displayName`, into `packaging/msix/identity.json` and set `"reserved": true`. They are not secrets. Values are
   case-sensitive.
4. **Decide the version rule** before the first submission, because it cannot be taken back: (X+1).Y.Z.0 as built
   (§5.3), or release 1.0.0 first.
5. **Build**: `pwsh tools/release/package-msix.ps1 -Version <release> -ForStore`.
6. **Try the exact packages** in a test desktop (§5.6 needs a test-signed twin: build once more with
   `-NewTestCertificate` and the same identity). Optionally run the Windows App Certification Kit on it.
7. **Screenshots**: at least one PNG of 1366 × 768 or larger per language; `store-listing.md` §2 has the set.
   Take them from the test-signed twin of step 6, so that Settings shows the release's version, in a test desktop
   with invented demo data (the way the README's photos are made, CLAUDE.md §3.4), never from a real history.
8. **Submission.** Start a submission and fill its pages from [`store-listing.md`](store-listing.md): pricing
   (free, all markets), properties (category, privacy policy URL), age ratings (the IARC questionnaire), packages
   (both `.msix` files), the five listings, and the submission options (the `runFullTrust` text and the notes for
   certification).
9. **Certification** takes up to a few business days; a first submission with a restricted capability may take
   longer. A failure comes with a report; fix and resubmit.
10. **Afterwards**: add the Store link to the README, and for every release build and submit the packages next to
    the GitHub release (the Store listing's "What's new" takes the release notes, up to 1,500 characters).

## 8. Limits and next steps

- **A copy for every user and a Chocolatey copy are not removed by the Store edition**; it waits for an
  administrator. Its two pages were not seen on screen.
- **Two histories are never merged.** An edition that has a history leaves another copy's history where it is.
- **The move needs Windows PowerShell and script files that run.** On a PC whose policy blocks scripts, the check
  says so, and the other copy has to be uninstalled in Windows' Installed apps.
- **The package version differs from the release version** by one in its first number (§5.3).
- **Release wiring**: `package-msix.ps1` is not called by CI or the release workflow yet, and a Store submission is
  made by hand. Partner Center has a submission API; using it needs the owner's account.
- **Screenshots for the listing** are not made yet. They come last, from the package that is submitted: Settings
  shows the version, and the name is settled only once it is reserved (§7).
- **ARM64**: the package builds and passes the script's checks (68.5 MB, version 1.2.11.0); it was never run on a
  device.

## 9. Sources

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
