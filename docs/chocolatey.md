# Shipping BetterClipboard through Chocolatey

Researched and built on 2026-10-01. The package lives in [`packaging/chocolatey`](../packaging/chocolatey), and the
release workflow builds, tests and pushes it for every stable version. Nothing is published yet: that needs the
community.chocolatey.org account, its API key as a GitHub secret, and a stable release ([section 5](#5-publishing)).

This page covers:
- what the Chocolatey Community Repository (`choco install <id>` without `--source`) requires, and what Chocolatey
  itself suggests;
- how Chocolatey behaves where the package depends on it;
- the package as built, how it was verified, and the steps that remain.

**Legend.**
- **[verified]**: observed on this PC (Windows 11 26200, Chocolatey CLI 2.3.0) with
  [`tools/probes/probe_chocolatey.ps1`](../tools/probes/probe_chocolatey.ps1) or with the real package
  ([section 7](#7-verification)), or read from the live feed's API.
- **[source]**: read in Chocolatey's code at tag 2.7.4, the latest release (2026-08-19). The helpers and the shim
  code read here match the 2.3.0 installed on this PC.
- **[docs]**: docs.chocolatey.org (repo `chocolatey/docs`, master of 2026-09-28) or a Chocolatey team statement.

## TL;DR

- **Chocolatey suggests embedding the software** when its license allows, and ours does (MIT, our own code).
  "Chocolatey works best when the packages contain the software it is managing and doesn't require downloads";
  embedding "makes for the most reliable and deterministic packages".
- **Both architectures are embedded** as LZMA2 `.7z` archives: 82 MiB in all for 0.2.4, against 138.5 MiB as zips,
  under the documented 150 MB limit. The archives are GitHub release assets too, listed in `SHA256SUMS.txt`, so
  moderators can check what the package carries against official files.
- **What the package does:**
  - installs into `%ChocolateyInstall%\lib\betterclipboard\tools\app`;
  - puts `bclip` and `BetterClipboard` on the PATH;
  - adds the Start menu shortcut and "Start with Windows" for the installing user;
  - starts the app unelevated, closes it before upgrades and uninstalls, and restarts it after an upgrade;
  - gives Win+V back on uninstall and keeps the history.

  It never takes over or removes the shortcut, startup entry or Win+V release of an `install.ps1` copy, and
  `install.ps1` now treats a Chocolatey copy the same way.
- **Tested:**
  - locally: the real package with the 0.2.4 payload, installed, upgraded with the app running, and uninstalled in a
    private Chocolatey root (28 of 28 checks);
  - in CI on every push, and in the release job before each push: a real install, upgrade and uninstall on GitHub's
    disposable runner. This has not run on GitHub yet: it needs the next push.
- **To publish:**
  1. create the account;
  2. add the `CHOCOLATEY_API_KEY` secret;
  3. tag a stable release;
  4. answer the first review, asking for a verifier exemption (the text is in section 5).

  Chocolatey's verifier runs Windows Server 2019 (build 17763), below our minimum of 19041; Windows Terminal and
  PowerToys are exempted for the same reason. Every version is reviewed by a human until a moderator marks the
  package *trusted*.

## 1. The Community Repository and its gates **[docs]**

Every version passes these automated checks, then a human moderator. A trusted package skips only the human.

| Gate | What it does | What it means for us |
|---|---|---|
| Validator | Static rules CPMR0001-0076 on the nuspec, the scripts and the files (section 2). | Requirements must be fixed; guidelines, suggestions and notes may be argued. |
| Verifier | A clean **Windows Server 2019** VM (updates to 2024-05-28, .NET 4.8, WSL). It runs `choco install --x86`, then upgrade, then rolls back and runs install and uninstall. Each step has 20 minutes; exit codes 0, 1605, 1614, 1641 and 3010 pass. It re-tests every package every two weeks. | Our install refuses builds before 19041 (section 3.5), so this fails by design. Ask for an exemption. Our own CI test stands in for it. |
| Scanner | VirusTotal (50-60 engines) on the package and on anything it downloads at install time. | The embedded archives are scanned; a false positive holds the version. |
| Moderator | Checks what automation can't: distribution rights, embedded binaries against official checksums, silent install, nothing malicious, no `choco` calls inside scripts. | `legal/VERIFICATION.txt` lists each archive's release URL and SHA-256. |
| Cleaner | Sends a reminder after 20 days in "Waiting for maintainer"; rejects 15 days later. | Answer review comments within 35 days. |

- **Flow:** submit → *Pending* (automated checks start after about 30 minutes) → *Ready* or *Waiting* → reviewer
  → *Approved*. A fix is resubmitted as the **same** version while under review. A package-only fix to an
  approved version takes the date notation in the fourth segment: `0.2.4.20261001`.
- **Trusted:** "a package will only be switched to trusted after a few versions have been approved by moderators
  without any changes being required". The switch is manual, also for software authors. Trusted versions are
  still validated, verified and scanned.
- **Limits:**
  - **Package size.** Rule CPMR0028 documents "up to 150MB". The server's own limit is 200 MB ("413 Request too
    large"; Chocolatey team in [org discussion #163](https://github.com/orgs/chocolatey/discussions/163); raising
    it is still open as [chocolatey/home#82](https://github.com/chocolatey/home/issues/82)). The build refuses
    anything over the documented 150 MB.
  - **No SemVer 2.0.0 versions.** "The Chocolatey Community Repository does not support SemVer 2.0.0 at this time",
    so `-rc.1` tags are left out. `choco pack` 2.3.0 accepts `9.9.4-rc.1` [verified]; the repository does not.
  - **Version normalization** in 2.x: `0.2.4` stays `0.2.4`. The docs advise scripts not to read
    `$env:ChocolateyPackageVersion` for their logic, and ours don't.
- **ID:** `betterclipboard` has no versions on the feed. The same query returns 40 for `ditto` [verified]. Ditto
  (vendor-maintained) and CopyQ are the clipboard managers already there.

## 2. Validator rules that apply to us **[docs; thresholds from the archived validator source]**

The validator's source ([chocolatey/package-validator](https://github.com/chocolatey/package-validator)) was
archived in 2021. Thresholds marked "2021" come from it and may have changed since.

| Rule | Level | Asks for | The package |
|---|---|---|---|
| CPMR0009 | Requirement | `projectUrl` | `https://github.com/Nucs/BetterClipboard` |
| CPMR0002, 0032, 0026, 0030 | Requirement | A description of 30-4000 characters; Markdown headings need a space after `#` | 1,796 characters, `## ` headings. |
| CPMR0001, 0020 | Requirement | A copyright of 4+ characters; no e-mail address in `authors` or `copyright` | `Copyright (c) 2026 Eli Belash` (as in `LICENSE`). |
| CPMR0007, 0039 | Requirement, guideline | `licenseUrl`; `requireLicenseAcceptance` false unless there is a license URL | MIT `LICENSE` at the release tag, acceptance false. |
| CPMR0014, 0023, 0048 | Requirement, guideline | Tags present, space-separated, without "chocolatey" | `clipboard clipboard-manager clipboard-history win-v productivity foss` |
| CPMR0019, 0022 | Requirement | No template placeholders in the nuspec; template comments removed (2021: the text `# main helper`) | The build fills every placeholder, refuses a leftover, and strips the template's comments. |
| CPMR0005, 0006, 0060 | Requirement, note | With binaries inside the package: `LICENSE.txt` and `VERIFICATION.txt` (vendors too) | `legal/LICENSE.txt` (from `LICENSE`) and `legal/VERIFICATION.txt` (URLs + SHA-256 of both archives). |
| CPMR0027, 0073, 0055 | Requirement, guideline | Checksums on every download; no custom downloaders | The package downloads nothing. |
| CPMR0010, 0011, 0012, 0016, 0072 | Requirement | No `choco` commands, no import of Chocolatey's module, no internal or private variables (`chocolateyPackageFolder`, …), no `$env:chocolateyInstallArguments` | None used; `$MyInvocation` gives the script's folder, and the shared names are all `BetterClipboard*`. |
| CPMR0003, 0015 | Requirement | Scripts named `chocolateyInstall.ps1` / `chocolateyUninstall.ps1` | Named as `choco new` names them (`chocolateyinstall.ps1`, `chocolateybeforemodify.ps1`, `chocolateyuninstall.ps1`). |
| CPMR0076 + moderation | Requirement | `iconUrl` through a CDN, not `raw.githubusercontent.com`; hosted where the maintainer has control; PNG preferred, 128 px or more | jsDelivr, pinned to the release tag: `.../BetterClipboard@v<version>/src/BetterClipboard.App/Assets/AppIcon.png` (256 px). It answered 200 for `v0.2.3` [verified]. |
| CPMR0040, 0047, 0049, 0042, 0057 | Guideline, suggestion | `packageSourceUrl`, `summary`, `title`, `releaseNotes`, `docsUrl` / `bugTrackerUrl` / `projectSourceUrl` | All set. |
| CPMR0041, 0050 | Guideline | `projectSourceUrl` not equal to `projectUrl`; title not equal to the ID (2021: exact, case-sensitive comparisons) | `.../tree/main/src`; title `BetterClipboard`. |
| CPMR0037 | Guideline | A script that calls a helper with no automatic undo (`Install-ChocolateyShortcut`) also ships an uninstall script | It does. |
| CPMR0046 | Guideline | Any literal `Start-Process`, even in a comment (2021) | None: the app is started through `explorer.exe` and a temporary shortcut. |
| CPMR0063, 0064 | Note | `WScript` / `.CreateShortcut` | None: `Install-ChocolateyShortcut` writes shortcuts, `Shell.Application` reads them. |
| CPMR0043, 0051 | Guideline | Scripts under 100 lines; at most 3 automation scripts (2021) | Flagged, and fine to argue: the install script is longer because every step is documented, and `helpers.ps1` is a fourth file shared by the three. |
| CPMR0068 | Note | Flags `owners` = `authors` | Expected for a vendor package. |
| CPMR0008 | Requirement | No Program Files in IDs ending `.portable` (2021) | Not our ID, and the package stays in `lib`, as moderators expect of a portable package. |

Moderators also list "anything that would not work with PowerShell v2" as a requirement. The scripts use plain
syntax: `New-Object` instead of `::new()`, no ternary, no `??`. Two exceptions: `Get-CimInstance` and
`Invoke-CimMethod` (PowerShell 3+) find the session's desktop user. Chocolatey runs the scripts in Windows
PowerShell 5.1, and every Windows the app supports has it.

## 3. How Chocolatey behaves: what the package designs around

### 3.1 Shims **[source + verified]**

- **What gets shimmed:** `ShimGenerationService.Install` shims every `*.exe` under the package folder,
  recursively, into `%ChocolateyInstall%\bin` (on the machine PATH). It skips a file with `<exe>.ignore` next to it
  and makes a GUI shim (which does not wait) for `<exe>.gui`.
- **No GUI detection:** "Chocolatey will automatically detect GUI applications" in the docs is not implemented
  (`//todo: #2586 v2 be able to determine gui automatically` in the code).
- **Our payload** holds four executables: `BetterClipboard.exe`, `bclip.exe`, `createdump.exe` (the .NET
  runtime's crash dumper, part of every self-contained publish) and `RestartAgent.exe` (Windows App SDK)
  [verified, 0.2.4, 475 files, 177.8 MiB unpacked].
- **Without marker files**, `createdump` and `RestartAgent` land on every user's PATH. A shim takes its exe's name,
  so another package shipping a `createdump.exe` overwrites or deletes ours.
- **The package:**
  - `BetterClipboard.exe.gui` (so `betterclipboard --show-flyout` works from any terminal);
  - `.ignore` for every other exe except `bclip.exe`, which gets a normal console shim, so `bclip` is on the PATH
    without `-AddToPath`.

  Verified with the real package: exactly `bclip.exe` and `BetterClipboard.exe` in `bin`, and `bclip --version`
  through its shim prints `bclip 0.2.4`.
- **Paths stay real:** a shim starts the real exe as a child process. `bclip` finds `BetterClipboard.exe` in its own
  folder for auto-start, and the app's "Start with Windows" records `Environment.ProcessPath`, the real `lib` path.

### 3.2 ARM64 is "32-bit" to Chocolatey **[source + verified]**

- **The rule:** `Get-OSArchitectureWidth` returns 32 whenever `PROCESSOR_ARCHITECTURE` or
  `PROCESSOR_ARCHITEW6432` is `ARM64`. A 2019 comment in the code explains it: Microsoft had "no current plans to
  ship 64-bit emulation for ARM64". The rule is unchanged in 2.7.4.
- **Who follows it:** `Get-ChocolateyWebFile` (behind `Install-ChocolateyZipPackage -Url/-Url64bit`) and
  `Get-ChocolateyUnzip` (`-FileFullPath/-FileFullPath64`) pick their 32- or 64-bit input with that width, or with
  `--x86`.
- **Given only a 64-bit input**, an ARM64 PC fails with "This package does not support 32 bit architecture."
  The probe saw exactly that (scenario F). The PowerToys package passes only `url64`, though PowerToys ships an
  ARM64 build.
- **Given only the 32-bit-slot input** (`-File`/`-FileFullPath`, or `-Url` + `-Checksum`), the helpers use it on
  x64, on ARM64 (scenario G) and under `--x86` (scenario H, `ChocolateyForceX86='true'`).
- **The package:**
  - It asks the OS itself, like `install.ps1`: `[System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture`,
    which is right even in an emulated x64 process. `PROCESSOR_ARCHITEW6432`/`PROCESSOR_ARCHITECTURE` are the
    fallback.
  - It hands that architecture's archive to `Get-ChocolateyUnzip -FileFullPath`, the helper Chocolatey's docs name
    for embedded files.
  - `--x86` gets a warning, and the 64-bit build is installed.

### 3.3 Upgrades, uninstalls and a running app **[verified, Chocolatey's debug log]**

**What `choco upgrade` does:**
1. Runs the **installed** version's `chocolateyBeforeModify.ps1`, which sees the *old* version in
   `ChocolateyPackageVersion`.
2. Moves `lib\<id>` to `lib-bkp\<id>\<old version>`.
3. Copies every file back.
4. Deletes each file whose checksum still matches the snapshot taken after the old install
   (`.chocolatey\<id>.<version>\.files`). The extracted app is included, so files a new release drops leave
   nothing behind (probe: `only-9.9.1.txt` gone after the upgrade).
5. Unpacks the new package and runs its install script. `ChocolateyPreviousPackageVersion` is set there [docs].
6. Deletes `lib-bkp\<id>`.

**Files written after install:**
- A file that before-modify writes into the package folder is not in the snapshot, so step 4 keeps it and the new
  install script finds it (probe: "marker found (from 9.9.1)"). The package hands over "the app was running"
  this way: `tools\upgrade-state.txt`.
- Uninstall also runs before-modify first, and then deletes only the snapshot's files. Such a file stays, and
  with it `lib\<id>`, while Chocolatey reports "successfully uninstalled". A later fresh install would then find
  the old file. The uninstall script deletes it; verified with the real package, whose folder was gone after
  the uninstall.

**When the app is still running:**
- A running exe can be moved but not deleted. The move succeeds, the new version installs, and only step 6 fails:
  "Access to the path 'BetterClipboard.exe' is denied" as a warning, with **exit code 0** (scenario B).
- The old binary keeps running from `lib-bkp`, and the next start finds a second instance. The new version
  starts only after the old one exits.
- `lib-bkp` (about 180 MB for us) stays until a later upgrade succeeds cleanly.

**So the package's before-modify closes the app** (probe scenario D; the real package in section 7):
1. **Graceful first:** `BetterClipboard.exe --exit` signals the session's `Local\` event, and the app saves the
   copies it has queued. It is sent only when the package's copy is the only BetterClipboard in the session,
   because the command reaches whichever instance owns the session.
2. Waits up to 15 s, then **force-stops** what is left (with another copy running in the session, at once).

What `--exit` cannot reach, and only a forced stop as administrator can:
- another user in the same session (over-the-shoulder elevation), whom the event's DACL refuses;
- other sessions.

A copy the script cannot close is reported, never fatal.

**Exit codes** [source, `chocolateyScriptRunner.ps1`]: Chocolatey fails a script when PowerShell's `$?` is false
after it, i.e. when its last statement failed, or when `$env:ChocolateyExitCode` is set. `$LASTEXITCODE` is only
logged. `explorer.exe` exits with 1 even when it worked, so the scripts reset `$LASTEXITCODE` after native calls
and never end on one.

### 3.4 Who "the user" is during an install **[docs + verified]**

- **Install location:** a portable package installs machine-wide into `%ChocolateyInstall%\lib`, and moderators
  reject one that writes Program Files. Chocolatey also supports **non-admin** installs, where its root sits in
  the user's profile. The probe and the real-package test ran entirely non-elevated.
- **Our app is per user:** the history is in `%LOCALAPPDATA%\BetterClipboard`, and "Start with Windows" is
  `HKCU\…\Run`.
- **Same user (the common case):** an administrator elevates through the UAC consent prompt. HKCU, `LOCALAPPDATA`
  and the Start menu are the user's own.
- **Over-the-shoulder elevation:** a standard user types an admin's credentials. HKCU and `LOCALAPPDATA` are the
  **admin's**, and the docs call this out explicitly.
- **SYSTEM** (Intune, RMM, Ansible): HKCU is SYSTEM's, and no Explorer of that user exists.
- **The package tells these apart** by the owner of the session's `explorer.exe`:
  - the shortcut goes to every user's Start menu when elevated, else to the user's own;
  - the Run value is written unless the install runs as SYSTEM or as someone other than the desktop's user;
  - the app is started only when the installing account owns the desktop.

### 3.5 The verifier's Windows is older than ours **[verified]**

- **The gap:** `BetterClipboard.App.csproj` sets `SupportedOSPlatformVersion` 10.0.19041.0 (Windows 10 2004).
- **What Chocolatey asks:** "check the version of Windows and throw an error if it is not a supported version.
  Under no circumstances should you bypass with a warning". The install script does.
- **The consequence:** the verifier's Server 2019 (17763) fails our install by design.
- **The precedent:** the feed's `PackageTestResultStatus` reads **Exempted** for `microsoft-windows-terminal` and
  `powertoys`, and Passing for `ditto` and `copyq` (2026-10-01).

### 3.6 Embed or download: what Chocolatey suggests **[docs + verified sizes]**

Chocolatey's own words, everywhere it addresses the choice:
- "Chocolatey works best when the packages contain the software it is managing and doesn't require downloads.
  However most software in the Windows world requires redistribution rights…" (`Install-ChocolateyZipPackage`
  notes).
- "This makes for the most reliable and deterministic packages, but ensure you have the legal right to distribute
  the software first" (Create Packages, *Including the Software Installer in the Package*).
- "Option 2 - Embed if Software Vendor's License Allows (Recommended)" (rule CPMR0028).
- Downloads exist because of distribution rights: "many of the packages contained on the community repository
  must download actual software from official distribution points, thus creating a dependency on those internet
  locations not changing" (Community Packages Disclaimer).

We are the vendor and the license is MIT, so the package embeds. The only obstacle was size:

| 0.2.4 | x64 | arm64 | Both |
|---|---|---|---|
| Release zip (Deflate) | 70.5 MiB | 68.0 MiB | 138.5 MiB |
| 7-Zip LZMA2, solid, `-mx=9` | **43.0 MiB** | **39.0 MiB** | **82.0 MiB** |
| One solid archive of both | | | 80.3 MiB |

- Only 74 of 475 files (43.6 MiB) are identical across the architectures: the framework assemblies are compiled
  ahead of time per architecture. So one shared archive saves just 1.7 MiB, and the package keeps one archive per
  architecture, which a user extracts in 1.8 s.
- The package is 82.0 MiB (86.0 MB), 57 % of the documented 150 MB.
- The archives are also release assets listed in `SHA256SUMS.txt`, so moderators verify them against official
  files, as CPMR0006 asks.
- **Cost:** a user downloads both architectures (82 MiB, against one 70 MiB zip). Chocolatey keeps the `.nupkg`
  in the package folder, about 82 MiB of disk next to the 179 MiB app; the install script deletes the extracted
  copies of the archives.

## 4. The package (built)

### 4.1 Files

| File | Role |
|---|---|
| [`packaging/chocolatey/betterclipboard.nuspec`](../packaging/chocolatey/betterclipboard.nuspec) | Metadata template: `{{PACKAGE_VERSION}}` and `{{RELEASE_TAG}}`, comments for this repository's readers (stripped when packed). Owners and authors: Eli Belash. |
| [`tools/chocolateyinstall.ps1`](../packaging/chocolatey/tools/chocolateyinstall.ps1) | Install and upgrade (section 4.2). |
| [`tools/chocolateybeforemodify.ps1`](../packaging/chocolatey/tools/chocolateybeforemodify.ps1) | Closes the app; writes `upgrade-state.txt` (`running` / `stopped`). |
| [`tools/chocolateyuninstall.ps1`](../packaging/chocolatey/tools/chocolateyuninstall.ps1) | Uninstall (section 4.2). |
| [`tools/helpers.ps1`](../packaging/chocolatey/tools/helpers.ps1) | Shared names and functions: desktop user, other copies, shortcut and Run targets, starting the app unelevated, restarting Explorer. |
| [`legal/VERIFICATION.txt`](../packaging/chocolatey/legal/VERIFICATION.txt) | Template; the build adds each archive's URL and SHA-256. `legal/LICENSE.txt` is generated from `LICENSE`. |
| [`tools/release/package-chocolatey.ps1`](../tools/release/package-chocolatey.ps1) | The build (section 5). |
| [`tools/release/test-chocolatey.ps1`](../tools/release/test-chocolatey.ps1) | The end-to-end test for disposable machines (section 7). |

### 4.2 What the scripts do

**Install** (fresh install and upgrade):
1. Throws below Windows build 19041.
2. Picks the archive by OS architecture (section 3.2) and extracts it to `tools\app` with `Get-ChocolateyUnzip`.
   Then deletes both archives from `tools` and `install.ps1` from `tools\app` (the per-user installer has no job
   here).
3. Writes `BetterClipboard.exe.gui`, and `.ignore` for every other exe but `bclip.exe`.
4. **Fresh install only** (an upgrade keeps the user's choices):
   - the Start menu shortcut, in every user's Start menu when elevated, otherwise the user's own (`/NoShortcut`
     skips it);
   - the "Start with Windows" value, in the app's own format `"<exe>" --background` (`/NoStartup` skips it).

   Neither is taken over when it already opens another existing copy, such as `install.ps1`'s.
5. Starts the app (`/NoLaunch` skips it), through Explorer with a temporary shortcut, so it runs unelevated and
   with arguments:
   - a plain start after a fresh install, which opens Settings like `install.ps1`;
   - `--background` after an upgrade, and only if before-modify closed it.

   Not when the installing account does not own the desktop, and not when another copy already runs in the
   session (a start would only open that copy's window).
6. Warns when `install.ps1`'s copy is installed too.

An upgrade is recognized by `upgrade-state.txt`, with `ChocolateyPreviousPackageVersion` as a second signal.

**Before-modify:** closes the app (section 3.3) and records whether it was running here.

**Uninstall** (after before-modify):
1. Deletes `upgrade-state.txt`, which would otherwise keep the package folder alive.
2. Removes the shortcut, from both Start menus, and the Run value, only where they point into the package.
3. **Win+V:** when `DisabledHotkeys` holds `V`, gives Win+V back and restarts Explorer, like
   `install.ps1 -Uninstall`, because without the app a released Win+V does nothing. Two exceptions:
   - `/KeepWinVReleased` (an uninstall parameter);
   - `install.ps1`'s copy remains, which still uses it.

   It acts only for the desktop's user; otherwise Win+V comes back at the next sign-in.
4. Keeps `%LOCALAPPDATA%\BetterClipboard`.

**`install.ps1`, the other way round:**
- It warns when a Chocolatey copy exists.
- It leaves a "Start with Windows" value that starts the Chocolatey copy.
- On `-Uninstall`:
  - it removes the value only when it points into its own folder or at a missing file (a stale value is still
    cleaned up);
  - it keeps Win+V released while the Chocolatey copy remains.

**Not in the package:**
- `-TakeOverWinV`: an Explorer restart during a package install; the app's Settings has the toggle.
- `-AddToPath`: the shims cover it.
- the Installed-apps entry: `choco uninstall` is the uninstaller.
- `installer.json`: the app never reads it.

## 5. Publishing

**What happens per release** (`.github/workflows/release.yml`, stable tags only):
1. `package.ps1` writes the zips, the `.7z` archives and `SHA256SUMS.txt` for both architectures.
2. `package-chocolatey.ps1`:
   - checks each archive against `SHA256SUMS.txt`;
   - fills the template, writes `legal\`, and saves the scripts as ASCII in UTF-8 with BOM;
   - refuses a leftover placeholder or a SemVer 2 version;
   - runs `choco pack`, and refuses a package over 150 MB.
3. `test-chocolatey.ps1` installs, upgrades (with the app running) and uninstalls that exact `.nupkg` on the
   runner. A failure stops the release before anything is published.
4. `gh release create` publishes the zips, the archives, `SHA256SUMS.txt`, `install.ps1` and the `.nupkg`.
5. `choco push` sends the package to `https://push.chocolatey.org/`, after the release exists, because
   `VERIFICATION.txt` points at its assets. Without the `CHOCOLATEY_API_KEY` secret the step only warns, and the
   `.nupkg` stays attached to the release.

CI (`.github/workflows/ci.yml`) runs steps 2 and 3 on every push, with an x64-only package built from its
`0.0.0-ci` smoke test.

**Once, before the first push:**
1. Create an account on community.chocolatey.org and confirm its e-mail: reviews arrive by mail.
2. On the account page, copy the API key into the repository secret `CHOCOLATEY_API_KEY` (Settings › Secrets and
   variables › Actions).
3. Push this commit. CI then runs the real install test on GitHub for the first time; check it passed.
4. Tag a stable release (`git tag -a v0.2.5 -F notes.md` and push the tag).
5. When the review opens (usually *Waiting* after the verifier fails), answer on the package page:

   > Hello, I am BetterClipboard's author and maintain this package. The verifier fails by design: it runs
   > Windows Server 2019 (build 17763), and BetterClipboard requires Windows 10 version 2004 (build 19041) or later,
   > so the install script refuses older Windows as the package guidelines ask. Could the package be exempted from
   > verification? Before every push, the release workflow installs, upgrades (with the app running) and
   > uninstalls the exact .nupkg on GitHub's windows-latest runner: <link to that run>. The embedded archives are
   > assets of the GitHub release, with URLs and SHA-256 checksums in legal/VERIFICATION.txt.
6. Answer every later comment within 35 days, and fix a problem by resubmitting the **same** version.

**After approval:**
- Add the install line to the README:

  ```powershell
  choco install betterclipboard
  ```

  The note to go with it: "Chocolatey installs machine-wide into its own folder; upgrade with
  `choco upgrade betterclipboard`".
- After a few versions approved without changes, a moderator may mark the package trusted.

**Package-only fixes:** to fix the package without a new app release, use the fourth version segment:
`package-chocolatey.ps1 -Version 1.2.3 -PackageVersion 1.2.3.20261001` (same archives), then test and push it.

**Before the first approval:** a release's attached `.nupkg` installs with
`choco install betterclipboard --source <folder containing it>`.

## 6. Decisions (2026-10-01)

1. **Embed, as Chocolatey suggests** (section 3.6), using LZMA2 archives, which keep both architectures within
   the documented limit.
2. **Start with Windows by default** for the installing user; `/NoStartup` opts out.
3. **Start the app after install** (and restart it after an upgrade when it was running); `/NoLaunch` opts out.
4. **Give Win+V back on uninstall**, restarting Explorer, unless `/KeepWinVReleased` is given or `install.ps1`'s
   copy remains.
5. **Stable versions only** on Chocolatey.
6. **Owners and authors:** Eli Belash.

**Follow-ups in the app,** independent of the package:
- Settings › *Add bclip to PATH* is redundant under Chocolatey; it could hide itself when the exe runs from a
  Chocolatey `lib` folder.
- An update check (roadmap) must not compete with `choco upgrade` there.

## 7. Verification

| What | How | Result |
|---|---|---|
| Chocolatey's mechanics: shims, `.ignore`/`.gui`, ARM64 as 32-bit, upgrades with a running app, files written after install, uninstall | [`probe_chocolatey.ps1`](../tools/probes/probe_chocolatey.ps1): a BC-TEST package in a private Chocolatey root, not elevated | 28 of 28 observations, Chocolatey CLI 2.3.0 (table below) |
| **The real package, 0.2.4 payload** | a private Chocolatey root, not elevated. Installed with the user's own BetterClipboard installed by `install.ps1` and running; upgraded to `0.2.4.1` with a scoped test instance running from the package; uninstalled with `/KeepWinVReleased` (the user has Win+V released) | 28 of 28 checks: version, archives and `install.ps1` gone, exactly two shims and three markers, `bclip 0.2.4` through the shim, the user's shortcut and Run value not taken over (both point at the `install.ps1` copy), the test instance closed by before-modify, no `lib-bkp`, the state file consumed, the package folder gone after the uninstall. The user's shortcut, Run value, `DisabledHotkeys` and app PIDs were unchanged |
| Building | `package-chocolatey.ps1` on the 0.2.4 archives; `package.ps1 -Architectures x64` for the 7-Zip step | 82.0 MiB package. Comments stripped, placeholders filled, scripts with BOM. Refused: a SemVer 2 version, a missing architecture, an archive not matching `SHA256SUMS.txt` |
| `install.ps1` living with a Chocolatey copy | its functions loaded from the script, Explorer and app control stubbed, a scratch Run key | 9 of 9, in both PowerShell 7.5.8 and Windows PowerShell 5.1 |
| Real install, upgrade with a running app, uninstall, Win+V restore, as administrator | `test-chocolatey.ps1` on GitHub's runner, in CI and in the release job | **Not run yet:** first run on the next push |

The probe's scenarios (Chocolatey CLI 2.3.0, not elevated):

| | Scenario | Observed |
|---|---|---|
| A | install with `--params "'/NoStartup /Shortcut:none'"` | Shims `bclip.exe` + a GUI shim for `BetterClipboard.exe`, none for the two `.ignore`d exes; `Get-PackageParameters` returned `NoStartup=True`, `Shortcut=none` |
| B | upgrade while the dummy app runs; 9.9.1's before-modify does not stop it | Exit 0, 9.9.2 installed, the old process still running, "Access to the path 'BetterClipboard.exe' is denied" on deleting `lib-bkp`, `lib-bkp` left; before-modify saw `ChocolateyPackageVersion=9.9.1`; `only-9.9.1.txt` gone; the marker before-modify wrote reached 9.9.2's install script |
| D | upgrade while it runs; 9.9.2's before-modify stops it | Exit 0, process stopped, no warning, `lib-bkp` gone (B's leftover too), only `only-9.9.3.txt` |
| E | uninstall | Before-modify ran first; shims gone; exit 0 and "successfully uninstalled", yet `lib\bctest-clip\tools\bctest-marker.txt` (written by before-modify) is left, so the folder stays |
| F | `PROCESSOR_ARCHITECTURE=ARM64`, only `-File64` | `Get-OSArchitectureWidth` = 32; "This package does not support 32 bit architecture.", install failed |
| G | the same, the package passes its own choice as `-File` | Exit 0, the arm64 payload extracted |
| H | `--x86` on x64, `-File` | `ChocolateyForceX86='true'`, exit 0, the x64 payload extracted |

## Sources

- docs.chocolatey.org:
  - [Create packages](https://docs.chocolatey.org/en-us/create/create-packages/)
  - [Moderation](https://docs.chocolatey.org/en-us/community-repository/moderation/)
  - [Validator rules](https://docs.chocolatey.org/en-us/community-repository/moderation/package-validator/rules/),
    including [CPMR0028](https://docs.chocolatey.org/en-us/community-repository/moderation/package-validator/rules/cpmr0028/)
    (embedding recommended, 150 MB)
  - [Verifier](https://docs.chocolatey.org/en-us/community-repository/moderation/package-verifier/)
  - [FAQ: trusted packages, scanner, portable vs install](https://docs.chocolatey.org/en-us/faqs/)
  - [Community packages disclaimer](https://docs.chocolatey.org/en-us/community-repository/community-packages-disclaimer/)
  - [Shims](https://docs.chocolatey.org/en-us/features/shim/)
  - [Distribution rights](https://docs.chocolatey.org/en-us/information/legal/)
  - [Install-ChocolateyZipPackage](https://docs.chocolatey.org/en-us/create/functions/install-chocolateyzippackage/)
  - [Helper reference: environment variables](https://docs.chocolatey.org/en-us/create/functions/), including
    `ChocolateyPreviousPackageVersion`
  - [Version normalization](https://docs.chocolatey.org/en-us/choco/features/version-number-normalization/)
  - [Chocolatey v2 upgrade guide: SemVer 2](https://docs.chocolatey.org/en-us/guides/upgrading-to-chocolatey-v2-v6/)
  - [Automatic packaging](https://docs.chocolatey.org/en-us/create/automatic-packages/)
- chocolatey/choco at tag 2.7.4:
  - `src/chocolatey.resources/helpers/functions/{Get-OSArchitectureWidth,Get-ChocolateyWebFile,Get-ChocolateyUnzip,Install-ChocolateyZipPackage,Install-ChocolateyShortcut,Get-PackageParameters}.ps1`
  - `src/chocolatey.resources/helpers/chocolateyScriptRunner.ps1` (read from the installed 2.3.0)
  - `src/chocolatey/infrastructure.app/services/ShimGenerationService.cs`
  - `src/chocolatey/infrastructure.app/ApplicationParameters.cs`
- Other repositories and threads:
  - [chocolatey/chocolatey-test-environment](https://github.com/chocolatey/chocolatey-test-environment), the
    verifier's Vagrant setup
  - [chocolatey/package-validator](https://github.com/chocolatey/package-validator), archived 2021
  - [chocolatey/home#82](https://github.com/chocolatey/home/issues/82) and
    [org discussion #163](https://github.com/orgs/chocolatey/discussions/163), the 200 MB server limit
  - [actions/runner-images](https://github.com/actions/runner-images): Chocolatey 2.7.4 and 7-Zip 26.03 on the
    Windows images
- The feed API `https://community.chocolatey.org/api/v2/`:
  - `FindPackagesById()` for ID availability;
  - `Packages()` with `PackageTestResultStatus` for the verifier exemptions.
- Packages read for precedent: `ditto`, `copyq`, `powertoys`, `microsoft-windows-terminal`, latest versions on
  2026-10-01.
