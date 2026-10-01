# Shipping BetterClipboard through Chocolatey

Research from 2026-10-01; nothing has been built or published yet. This page answers two questions: what the
Chocolatey Community Repository (`choco install <id>` with no `--source`) requires, and what a BetterClipboard
package must do to satisfy those rules and Chocolatey's own behavior.

**Legend.**
- **[verified]**: observed on this PC (Windows 11 26200, Chocolatey CLI 2.3.0) with
  [`tools/probes/probe_chocolatey.ps1`](../tools/probes/probe_chocolatey.ps1), or read from the live feed's API.
- **[source]**: read in Chocolatey's code at tag 2.7.4, the latest release (2026-08-19). The helpers and the shim
  code read here match the 2.3.0 installed on this PC.
- **[docs]**: docs.chocolatey.org (repo `chocolatey/docs`, master of 2026-09-28) or a Chocolatey team statement.

## TL;DR

- **What it takes:**
  - a community.chocolatey.org account (its user name goes into `<owners>`) and an API key;
  - a package in this repo: one nuspec plus three PowerShell scripts;
  - a step in the release workflow that fills in the version, the URLs and the SHA-256s, then runs `choco pack`
    and `choco push`.

  Chocolatey 2.7.4 is preinstalled on GitHub's Windows runners, so the step needs no setup.
- **The first version is held for review.** It waits for a human moderator (days to weeks). It will also fail
  the automated install test, which runs on Windows Server 2019 (build 17763), while the app needs build 19041.
  Answer that in a review comment asking for a verifier exemption: Windows Terminal and PowerToys are both
  "Exempted" today. Every later version is human-reviewed until a moderator marks the package *trusted*.
- **Recommended package:**
  - **ID:** `betterclipboard` (free).
  - **Payload:** downloads the release zip for the OS architecture from GitHub Releases, checked against the
    SHA-256 pinned in the script.
  - **Location:** extracts it into `%ChocolateyInstall%\lib\betterclipboard\tools\app`.
  - **PATH:** shims put `bclip` and `BetterClipboard` on the PATH.
  - **Start menu, sign-in, launch:** the same shortcut and per-user "Start with Windows" entry as `install.ps1`.
    It starts the app unelevated.
  - **Upgrade and uninstall:** stops the app first and keeps the history.
- **Five traps found by testing**, each designed around below:
  1. Chocolatey's helpers treat ARM64 as 32-bit.
  2. It shims *every* `.exe`, including the runtime's `createdump.exe` and `RestartAgent.exe`.
  3. It does not detect GUI apps.
  4. An upgrade while the app runs "succeeds" but leaves the old version running from a backup folder.
  5. Uninstall deletes only the files Chocolatey recorded right after install. A file our scripts write later
     stays behind, with the package folder, while Chocolatey reports success.

## 1. The Community Repository and its gates **[docs]**

Every version passes these automated checks, then a human moderator. A trusted package skips only the human.

| Gate | What it does | What it means for us |
|---|---|---|
| Validator | Static rules CPMR0001-0076 on the nuspec, the scripts and the files (section 2). | Requirements must be fixed; guidelines, suggestions and notes may be argued. |
| Verifier | A clean **Windows Server 2019** VM (updates to 2024-05-28, .NET 4.8, WSL). It runs `choco install --x86`, then upgrade, then rolls back and runs install and uninstall. Each step has 20 minutes; exit codes 0, 1605, 1614, 1641 and 3010 pass. It re-tests every package every two weeks. | Our package must refuse builds before 19041 (section 3.5), so this fails by design. Ask for an exemption. |
| Scanner | VirusTotal (50-60 engines) on the package **and on what it downloads at install time**. | The unsigned release zips get scanned; a false positive holds the version. |
| Moderator | Checks what automation can't. The download must come from the official location (`projectUrl`), match the package version, and be silent. Distribution rights. Nothing malicious, no `choco` calls inside scripts. | GitHub Releases of the project URL. |
| Cleaner | Sends a reminder after 20 days in "Waiting for maintainer"; rejects 15 days later. | Answer review comments within 35 days. |

- **Flow:** submit → *Pending* (automated checks start after about 30 minutes) → *Ready* or *Waiting* → reviewer
  → *Approved*. A fix is resubmitted as the **same** version while under review. A package-only fix to an
  approved version takes the date notation in the fourth segment: `0.2.4.20261001`.
- **Trusted:** "a package will only be switched to trusted after a few versions have been approved by moderators
  without any changes being required". The switch is manual, also for software authors. Trusted versions are
  still validated, verified and scanned.
- **Limits:**
  - **200 MB per package.** The server enforces it with "413 Request too large". The Chocolatey team confirmed it
    in an org discussion ([#163](https://github.com/orgs/chocolatey/discussions/163)); raising it is still open
    ([chocolatey/home#82](https://github.com/chocolatey/home/issues/82)).
  - **No SemVer 2.0.0 versions.** "The Chocolatey Community Repository does not support SemVer 2.0.0 at this time",
    so our `-rc.1` tags cannot go there as they are. `choco pack` 2.3.0 does accept `9.9.4-rc.1` [verified].
  - **Version normalization** in 2.x: `0.2.4` stays `0.2.4`. The docs advise scripts to hold the version
    themselves instead of reading `$env:ChocolateyPackageVersion`.
- **ID:** `betterclipboard` has no versions on the feed. The same query returns 40 for `ditto` [verified]. Ditto
  (vendor-maintained) and CopyQ are the clipboard managers already there.

## 2. Validator rules that apply to us **[docs; thresholds from the archived validator source]**

The validator's source ([chocolatey/package-validator](https://github.com/chocolatey/package-validator)) was
archived in 2021. Thresholds marked "2021" come from it and may have changed since.

| Rule | Level | Asks for | Our answer |
|---|---|---|---|
| CPMR0009 | Requirement | `projectUrl` | `https://github.com/Nucs/BetterClipboard` |
| CPMR0002, 0032, 0026, 0030 | Requirement | A description of 30-4000 characters; Markdown headings need a space after `#` | Draft in section 4.3 (~1,300 characters). |
| CPMR0001, 0020 | Requirement | A copyright of 4+ characters; no e-mail address in `authors` or `copyright` | `Copyright (c) 2026 Eli Belash` (as in `LICENSE`). |
| CPMR0007, 0039 | Requirement, guideline | `licenseUrl`; `requireLicenseAcceptance` false unless there is a license URL | MIT `LICENSE` at the release tag, acceptance false. |
| CPMR0014, 0023, 0048 | Requirement, guideline | Tags present, space-separated, without "chocolatey" | `clipboard clipboard-manager clipboard-history win-v productivity foss` |
| CPMR0019, 0022 | Requirement | No template placeholders in the nuspec; template comments removed (2021: the text `# main helper`) | The release job replaces every placeholder before `choco pack`. |
| CPMR0005, 0006, 0060 | Requirement, note | **Only when binaries are inside the package:** `LICENSE.txt` and `VERIFICATION.txt` (vendors too) | Not needed when downloading. If we embed, Ditto's two-line vendor statement is enough. |
| CPMR0027, 0073 | Requirement | Every download carries a checksum (`-Checksum` for `-Url`) | SHA-256 from `SHA256SUMS.txt`, pinned per version. |
| CPMR0075 | Requirement | No GitHub *comment* attachments (`/files/`, `/assets/`) | Release assets are fine. |
| CPMR0010, 0011, 0012, 0016, 0072 | Requirement | No `choco` commands, no import of Chocolatey's module, no internal variables, no `$env:chocolateyInstallArguments`, no private variables (`chocolateyPackageFolder`, …) | Use `Get-ChocolateyPath -PathType PackagePath` if the folder is needed; `$MyInvocation` covers the script's own folder. |
| CPMR0003, 0015 | Requirement | Scripts named `chocolateyInstall.ps1` / `chocolateyUninstall.ps1` | (Before-modify: `chocolateyBeforeModify.ps1`.) |
| CPMR0076 + moderation | Requirement | `iconUrl` through a CDN, not `raw.githubusercontent.com`; hosted where the maintainer has control; PNG preferred, 128 px or more | `https://cdn.jsdelivr.net/gh/Nucs/BetterClipboard@v0.2.4/src/BetterClipboard.App/Assets/AppIcon.png` (256 px). Pinned to a tag, because jsDelivr caches; the URL answered 200 for `v0.2.3` [verified]. |
| CPMR0040, 0047, 0049, 0042, 0057 | Guideline, suggestion | `packageSourceUrl`, `summary`, `title`, `releaseNotes`, `docsUrl` / `bugTrackerUrl` / `projectSourceUrl` | All set (section 4.3). |
| CPMR0041 | Guideline | `projectSourceUrl` must not equal `projectUrl` (2021: exact string match) | `https://github.com/Nucs/BetterClipboard/tree/main/src` |
| CPMR0037 | Guideline | A script that calls a helper with no automatic undo (`Install-ChocolateyShortcut`, PATH, file associations) also ships an uninstall script | We remove the shortcut and the Run value. |
| CPMR0043 | Guideline | Scripts under 100 lines (2021) | Keep the install script lean. |
| CPMR0046 | Guideline | Any literal `Start-Process`, even in a comment (2021) | Start the app with `& explorer.exe "<exe>"`. |
| CPMR0055 | Guideline | No `Invoke-WebRequest`, `.DownloadFile`, BITS (2021) | Download only through `Install-ChocolateyZipPackage -Url`. |
| CPMR0063, 0064 | Note | `WScript` / `.CreateShortcut` | Use `Install-ChocolateyShortcut` (it wraps the same COM object). |
| CPMR0068 | Note | Flags `owners` = `authors` | Expected for a vendor package; nothing to change. |
| CPMR0008 | Requirement | No Program Files in IDs ending `.portable` (2021) | Not our ID. The moderators' rule "a portable package must not put itself in Program Files" still applies, and we don't. |

Moderators also list "anything that would not work with PowerShell v2" as a requirement. Scripts should stick to
plain syntax: `New-Object` instead of `::new()`, no ternary, no `??`. Chocolatey runs them in Windows PowerShell
5.1 on our supported Windows anyway.

## 3. How Chocolatey behaves: what the package must design around

### 3.1 Shims **[source + verified]**

- **What gets shimmed:** `ShimGenerationService.Install` shims every `*.exe` under the package folder,
  recursively, into `%ChocolateyInstall%\bin` (on the machine PATH). It skips a file with `<exe>.ignore` next to it
  and makes a GUI shim (which does not wait) for `<exe>.gui`.
- **No GUI detection:** "Chocolatey will automatically detect GUI applications" in the docs is not implemented
  (`//todo: #2586 v2 be able to determine gui automatically` in the code).
- **Our zip** holds four executables: `BetterClipboard.exe`, `bclip.exe`, `createdump.exe` (the .NET runtime's
  crash dumper, part of every self-contained publish) and `RestartAgent.exe` (Windows App SDK) [verified,
  0.2.4 zip, 475 entries, 177.8 MB unpacked].
- **Without marker files**, `createdump` and `RestartAgent` land on every user's PATH. A shim takes its exe's name,
  so another package shipping a `createdump.exe` overwrites or deletes ours.
- **The design:**
  - `createdump.exe.ignore` and `RestartAgent.exe.ignore`;
  - `BetterClipboard.exe.gui` (so `betterclipboard --show-flyout` works from any terminal);
  - `bclip.exe` gets a normal console shim, so `bclip` is on the PATH with no `-AddToPath`.

  The probe saw exactly `bclip.exe` and a "gui shim for BetterClipboard.exe" created, and both removed on uninstall.
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
- **Given only the 32-bit-slot input** (`-Url` + `-Checksum`, or `-File`), the helpers use it on x64, on ARM64
  (scenario G) and under `--x86` (scenario H, `ChocolateyForceX86='true'`).
- **The design:** the package asks the OS itself, like `install.ps1` does:
  `[System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture`. That property is right even in an
  emulated x64 process, and is available on .NET Framework 4.7.1+, which Chocolatey 2.x requires. The package then
  passes that architecture's zip in the 32-bit slot.
- **`--x86`:** there is no 32-bit build, so the package installs the 64-bit one and says so.

### 3.3 Upgrades and a running app **[verified, Chocolatey's debug log]**

**What `choco upgrade` does:**
1. Runs the **installed** version's `chocolateyBeforeModify.ps1`. That script sees the *old* version in
   `ChocolateyPackageVersion`.
2. Moves `lib\<id>` to `lib-bkp\<id>\<old version>`.
3. Copies every file back.
4. Deletes each file whose checksum still matches the snapshot taken after the old install
   (`.chocolatey\<id>.<version>\.files`). The extracted app is included, so files a new release drops leave
   nothing behind (probe: `only-9.9.1.txt` gone after the upgrade).
5. Unpacks the new package and runs its install script.
6. Deletes `lib-bkp\<id>`.

**Files written after install:**
- A file that before-modify writes into the package folder is not in the snapshot, so step 4 keeps it and the new
  install script finds it (probe: "marker found (from 9.9.1)"). That is how before-modify can tell the new
  version that the app was running.
- Uninstall also runs before-modify first, and then deletes only the snapshot's files. Such a file stays, and
  with it `lib\<id>`, while Chocolatey reports "successfully uninstalled". A later fresh install then finds the
  old file. The uninstall script must delete it.

**When the app is still running:**
- A running exe can be moved but not deleted. The move succeeds, the new version installs, and only step 6 fails:
  "Access to the path 'BetterClipboard.exe' is denied" as a warning, with **exit code 0** (scenario B).
- The old binary keeps running from `lib-bkp`, and the next start finds a second instance. The new version
  starts only after the old one exits.
- `lib-bkp` (about 180 MB for us) stays until a later upgrade succeeds cleanly.

**So `chocolateyBeforeModify.ps1` must stop the app** (scenario D: clean).

**How:**
1. Send `BetterClipboard.exe --exit` first. It signals the session's `Local\` event, and the app saves queued
   copies before it exits.
2. Wait up to 15 s.
3. Then `Stop-Process -Force` every `BetterClipboard.exe` whose path is inside the package folder.

**What `--exit` cannot reach:**
- an instance of another user in the same session (over-the-shoulder elevation), which the event's DACL
  refuses;
- other sessions.

Only the forced stop reaches those, and only when Chocolatey runs as administrator.

### 3.4 Who "the user" is during an install **[docs + inference]**

- **Install location:** a portable package installs machine-wide into `%ChocolateyInstall%\lib`, and moderators
  reject one that writes Program Files. Chocolatey also supports **non-admin** installs, where its root sits in
  the user's profile. The probe ran entirely non-elevated.
- **Our app is per user:** the history is in `%LOCALAPPDATA%\BetterClipboard`, and "Start with Windows" is
  `HKCU\…\Run`.
- **Same user (the common case):** an administrator elevates through the UAC consent prompt. HKCU, `LOCALAPPDATA`
  and the Start menu are the user's own, and everything matches `install.ps1`.
- **Over-the-shoulder elevation:** a standard user types an admin's credentials. HKCU and `LOCALAPPDATA` are the
  **admin's**, and the docs call this out explicitly. The Run value lands in the admin's hive, so the real user
  turns "Start with Windows" on in Settings.
- **SYSTEM** (Intune, RMM, Ansible): HKCU is SYSTEM's, and no Explorer of that user exists.
- **The design:**
  - the Start menu shortcut goes to all users (`CommonPrograms`) when elevated, else to the user's `Programs`;
  - the Run value is written for the installing user only;
  - the app is started only when that user has an Explorer in this session.

  The description says all of this.

### 3.5 The verifier's Windows is older than ours **[verified]**

- **The gap:** `BetterClipboard.App.csproj` sets `SupportedOSPlatformVersion` 10.0.19041.0 (Windows 10 2004).
- **What Chocolatey asks:** "check the version of Windows and throw an error if it is not a supported version.
  Under no circumstances should you bypass with a warning".
- **The consequence:** the verifier's Server 2019 (17763) fails our install by design.
- **The precedent:** the feed's `PackageTestResultStatus` reads **Exempted** for `microsoft-windows-terminal` and
  `powertoys`, and Passing for `ditto` and `copyq` (2026-10-01). Ask for the exemption in the first review
  comment, with this reason.

### 3.6 Download or embed **[docs + verified sizes]**

| | Download from GitHub Releases (recommended) | Embed the zips |
|---|---|---|
| Package size | About 10 KB | Both zips are 145 MB (0.2.4: 73.9 + 71.3 MB) of the 200 MB cap, and grow with every Windows App SDK update |
| What a user downloads | One zip (~70 MB) | Both zips |
| Validator | Checksums required (CPMR0073) | `LICENSE.txt` + `VERIFICATION.txt` required (CPMR0005/0006) |
| Moderation | The URL must match `projectUrl`: it does | Moderators compare the zips' checksums with the release |
| Weak spot | GitHub must be reachable at install time | Size, and duplicated storage on Chocolatey's CDN |

The release assets download anonymously (HTTP 200, 73,772,495 bytes for `v0.2.3`), and GitHub publishes a
`sha256:` digest per asset [verified]. Most community packages download ("Most Chocolatey packages … do not
contain actual software distributions"); embedding is the more deterministic choice where the size allows.

## 4. Recommended package design (not built)

### 4.1 Layout

```text
packaging/chocolatey/
├── betterclipboard.nuspec             version, URLs and release notes filled in by the release job
└── tools/
    ├── chocolateyInstall.ps1          URLs + SHA-256s filled in by the release job
    ├── chocolateyBeforeModify.ps1
    └── chocolateyUninstall.ps1
```

Every `.ps1` is UTF-8 **with** BOM: Windows PowerShell reads BOM-less files as ANSI.

### 4.2 What the scripts do

- **Install:**
  1. Throw below build 19041.
  2. Pick the zip by `OSArchitecture`; throw on anything but X64 or Arm64.
  3. `Install-ChocolateyZipPackage -Url -Checksum -ChecksumType sha256 -UnzipLocation tools\app`.
  4. Write `.gui` for the app and `.ignore` for `createdump.exe` and `RestartAgent.exe`.
  5. Delete `install.ps1` from `tools\app`. Its only job, the per-user uninstall entry, does not exist here, and it
     would offer a second way to install.
  6. Add the Start menu shortcut with `Install-ChocolateyShortcut`; skipped by `/NoShortcut`.
  7. Write the Run value `BetterClipboard` = `"<exe>" --background`, the exact format of the app's own toggle;
     skipped by `/NoStartup`.
  8. Start the app unelevated (`& explorer.exe "<exe>"`) when the session is interactive and the installing user
     has an Explorer there; skipped by `/NoLaunch`. After an upgrade, start it only if before-modify stopped it.
  9. Warn when an `install.ps1` install also exists (HKCU `Uninstall\BetterClipboard`): both work against the
     same history, but Start shows two entries and the one Run value points at whichever wrote last.
- **Before-modify:** stop the app as in 3.3, and leave a marker file in the package folder when it was running.
  The new install script finds the marker (3.3), restarts the app and deletes the marker.
- **Uninstall:**
  - delete that marker, which before-modify has just written: otherwise it keeps `lib\betterclipboard` alive
    (3.3);
  - remove the shortcut from both Start menu locations;
  - remove the Run value only when it points into the package folder;
  - keep `%LOCALAPPDATA%\BetterClipboard`.
  - Win+V: when `DisabledHotkeys` holds `V` (released from Settings), give it back and restart Explorer like
    `install.ps1 -Uninstall` does. Without the app, a released Win+V does nothing at all. Only for the current
    user, and only in an interactive session; otherwise print how.
- **Not in the package:**
  - `-TakeOverWinV` (an Explorer restart during a package install; Settings has the toggle);
  - `-AddToPath` (the shims cover it);
  - the HKCU Installed-apps entry (`choco uninstall` is the uninstaller);
  - `installer.json` (the app never reads it).

The critical part, sketched:

```powershell
$ErrorActionPreference = 'Stop'
$appDir = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Definition) 'app'

# Chocolatey asks packages to fail on Windows the software does not support.
if ([Environment]::OSVersion.Version.Build -lt 19041) { throw 'BetterClipboard needs Windows 10 version 2004 (build 19041) or later.' }

# Chocolatey's helpers treat ARM64 as 32-bit, so choose the zip here and hand it over in the 32-bit slot,
# which they use on every architecture. OSArchitecture is right even in an emulated x64 process.
$downloads = @{
    X64   = @{ Url = 'https://github.com/Nucs/BetterClipboard/releases/download/v0.2.4/BetterClipboard-0.2.4-win-x64.zip'; Sha256 = '7aa7a6b1a185c836244f538f4b969c032c2b4745eb34058d16b9b24146d1a823' }
    Arm64 = @{ Url = 'https://github.com/Nucs/BetterClipboard/releases/download/v0.2.4/BetterClipboard-0.2.4-win-arm64.zip'; Sha256 = '7e73ec57db47b3c489f5851a0779dd06cdddbe8ae1df3f88943da044e7f28a9e' }
}
$os = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture
$download = $downloads["$os"]
if (-not $download) { throw "BetterClipboard runs on 64-bit Windows on x64 or ARM64 only (this PC: $os)." }
Install-ChocolateyZipPackage -PackageName $env:ChocolateyPackageName -Url $download.Url -Checksum $download.Sha256 -ChecksumType sha256 -UnzipLocation $appDir

# Shims: a GUI shim for the app, a console shim for bclip, none for the runtime's helper executables.
New-Item -ItemType File -Force -Path (Join-Path $appDir 'BetterClipboard.exe.gui') | Out-Null
foreach ($helper in 'createdump.exe', 'RestartAgent.exe') { New-Item -ItemType File -Force -Path (Join-Path $appDir "$helper.ignore") | Out-Null }
```

The hashes above are the 0.2.4 zips built on 2026-10-01. A real package takes them from the release's
`SHA256SUMS.txt`.

### 4.3 The nuspec

```xml
<?xml version="1.0" encoding="utf-8"?>
<package xmlns="http://schemas.microsoft.com/packaging/2015/06/nuspec.xsd">
  <metadata>
    <id>betterclipboard</id>
    <version>0.2.4</version>
    <packageSourceUrl>https://github.com/Nucs/BetterClipboard/tree/main/packaging/chocolatey</packageSourceUrl>
    <owners>CHOCOLATEY-USER-NAME</owners>
    <title>BetterClipboard</title>
    <authors>Eli Belash</authors>
    <projectUrl>https://github.com/Nucs/BetterClipboard</projectUrl>
    <iconUrl>https://cdn.jsdelivr.net/gh/Nucs/BetterClipboard@v0.2.4/src/BetterClipboard.App/Assets/AppIcon.png</iconUrl>
    <copyright>Copyright (c) 2026 Eli Belash</copyright>
    <licenseUrl>https://github.com/Nucs/BetterClipboard/blob/v0.2.4/LICENSE</licenseUrl>
    <requireLicenseAcceptance>false</requireLicenseAcceptance>
    <projectSourceUrl>https://github.com/Nucs/BetterClipboard/tree/main/src</projectSourceUrl>
    <docsUrl>https://github.com/Nucs/BetterClipboard#readme</docsUrl>
    <bugTrackerUrl>https://github.com/Nucs/BetterClipboard/issues</bugTrackerUrl>
    <tags>clipboard clipboard-manager clipboard-history win-v productivity foss</tags>
    <summary>Persistent, searchable clipboard history that takes over Win+V</summary>
    <description><![CDATA[BetterClipboard replaces Windows' Win+V clipboard history, which keeps 25 items and forgets them at every restart. It records what you copy in a searchable history that survives restarts, encrypted on disk and bound to your PC and Windows account. Win+V opens it.

## Features

- Persistent history of text, links, colors, files and images, with search and filter tabs
- Pins, groups, and "Forget forever" for what must never be recorded again
- Ignores password managers and copies marked as private
- Imports what Windows' own clipboard history still remembers
- `bclip`, a command line for scripts and AI agents (turn it on in Settings > Command line)

## Package parameters

- `/NoStartup` - don't start BetterClipboard when you sign in
- `/NoShortcut` - no Start menu shortcut
- `/NoLaunch` - don't start it after installing

Example: `choco install betterclipboard --params "'/NoStartup'"`

## Notes

- Requires Windows 10 version 2004 (build 19041) or later, x64 or ARM64.
- Installed to `%ChocolateyInstall%\lib\betterclipboard\tools\app`; `bclip` and `BetterClipboard` are on the PATH.
- "Start with Windows" is set for the user who installs the package; other users turn it on in Settings.
- Your history stays in `%LOCALAPPDATA%\BetterClipboard`; uninstalling keeps it.]]></description>
    <releaseNotes>https://github.com/Nucs/BetterClipboard/releases/tag/v0.2.4</releaseNotes>
  </metadata>
  <files>
    <file src="tools\**" target="tools" />
  </files>
</package>
```

`choco pack` 2.3.0 accepted every element above in the probe's package (same fields, BC-TEST values)
[verified].

## 5. Publishing

- **Once:**
  1. Create the community.chocolatey.org account. Its user name goes into `<owners>`; make sure its mail can
     reach you, since reviews arrive by mail.
  2. Copy the API key from the account page into a GitHub secret (`CHOCOLATEY_API_KEY`).
- **Per release,** in `release.yml` after `gh release create`:
  1. Fill the version, both URLs, both SHA-256s (from `SHA256SUMS.txt`) and the release-notes URL into
     `packaging/chocolatey`, then run `choco pack`.
  2. `choco push betterclipboard.<v>.nupkg --source https://push.chocolatey.org/ --api-key $env:CHOCOLATEY_API_KEY`.
  3. Attach the `.nupkg` to the GitHub release too. Before a version is approved, users can install it with
     `choco install betterclipboard --source <folder>`. Installing from a `.nupkg` path is not supported: point
     `--source` at its folder.
  4. **Prereleases** (`-rc.1`): skip them, because the repository refuses SemVer 2. The alternative, mapping to
     `-rc1`, sorts `rc10` before `rc2`.
- **Testing before a push:** the docs say "in a VM and not on your machine", because `choco install` changes the
  whole machine. The probe tests Chocolatey's mechanics in a private root without the real app. For the real
  package, use Windows Sandbox (it opens a window, so only with the user's go-ahead) or a VM:
  `choco install betterclipboard --source <folder> --debug --verbose`, then upgrade and uninstall.
- **Automatic updating** ([chocolatey-au](https://github.com/chocolatey-community/chocolatey-au)) is for
  maintainers who are not the vendor. We publish from our own pipeline, so it is not needed.

## 6. Decisions for the maintainer

1. **Download or embed** (3.6). Recommended: download, ~10 KB per version, one zip per user.
2. **Start with Windows by default** for the installing user (like `install.ps1`), with `/NoStartup`. Recommended.
3. **Start the app after install and after an upgrade** (interactive sessions only), with `/NoLaunch`.
   Recommended. CopyQ's package starts its app too.
4. **Give Win+V back on uninstall** when it was released, which restarts Explorer like `install.ps1 -Uninstall`.
   Recommended, since otherwise Win+V does nothing.
5. **Prereleases:** stable versions only on Chocolatey. Recommended.
6. **Names:** `<authors>` "Eli Belash" (the `LICENSE`) or "BetterClipboard contributors" (`install.ps1`'s
   publisher); `<owners>` = the account's user name.
7. **Code signing:** not required by Chocolatey. It lowers the chance of a VirusTotal false positive holding a
   version.

Follow-ups in the app, independent of the package:
- Settings › *Add bclip to PATH* is redundant under Chocolatey. It could hide itself when the exe runs from a
  Chocolatey `lib` folder.
- An update check (roadmap) must not compete with `choco upgrade` there.

## 7. The probe's observations (2026-10-01, Chocolatey CLI 2.3.0, not elevated)

`pwsh tools/probes/probe_chocolatey.ps1` prints 28 observations; all matched. Scenarios use the BC-TEST package
`bctest-clip` 9.9.1-9.9.3 in a private root.

| | Scenario | Observed |
|---|---|---|
| A | install with `--params "'/NoStartup /Shortcut:none'"` | Shims `bclip.exe` + a GUI shim for `BetterClipboard.exe`, none for the two `.ignore`d exes; `Get-PackageParameters` returned `NoStartup=True`, `Shortcut=none` |
| B | upgrade while the dummy app runs; 9.9.1's before-modify does not stop it | Exit 0, 9.9.2 installed, the old process still running, "Access to the path 'BetterClipboard.exe' is denied" on deleting `lib-bkp`, `lib-bkp` left; before-modify saw `ChocolateyPackageVersion=9.9.1`; `only-9.9.1.txt` gone; the marker before-modify wrote reached 9.9.2's install script |
| D | upgrade while it runs; 9.9.2's before-modify stops it | Exit 0, process stopped, no warning, `lib-bkp` gone (B's leftover too), only `only-9.9.3.txt` |
| E | uninstall | Before-modify ran first; shims gone; exit 0 and "successfully uninstalled", yet `lib\bctest-clip\tools\bctest-marker.txt` (written by before-modify) is left, so the folder stays |
| F | `PROCESSOR_ARCHITECTURE=ARM64`, only `-File64` | `Get-OSArchitectureWidth` = 32; "This package does not support 32 bit architecture.", install failed |
| G | the same, the package passes its own choice as `-File` | Exit 0, the arm64 payload extracted |
| H | `--x86` on x64, `-File` | `ChocolateyForceX86='true'`, exit 0, the x64 payload extracted |

The user's installed BetterClipboard kept its PID through every run. The machine's Chocolatey still had 28
packages in `lib` afterwards.

## Sources

- docs.chocolatey.org:
  - [Create packages](https://docs.chocolatey.org/en-us/create/create-packages/)
  - [Moderation](https://docs.chocolatey.org/en-us/community-repository/moderation/)
  - [Validator rules](https://docs.chocolatey.org/en-us/community-repository/moderation/package-validator/rules/)
  - [Verifier](https://docs.chocolatey.org/en-us/community-repository/moderation/package-verifier/)
  - [FAQ: trusted packages, scanner, portable vs install](https://docs.chocolatey.org/en-us/faqs/)
  - [Shims](https://docs.chocolatey.org/en-us/features/shim/)
  - [Distribution rights](https://docs.chocolatey.org/en-us/information/legal/)
  - [Version normalization](https://docs.chocolatey.org/en-us/choco/features/version-number-normalization/)
  - [Chocolatey v2 upgrade guide: SemVer 2](https://docs.chocolatey.org/en-us/guides/upgrading-to-chocolatey-v2-v6/)
  - [Automatic packaging](https://docs.chocolatey.org/en-us/create/automatic-packages/)
- chocolatey/choco at tag 2.7.4:
  - `src/chocolatey.resources/helpers/functions/{Get-OSArchitectureWidth,Get-ChocolateyWebFile,Get-ChocolateyUnzip,Install-ChocolateyZipPackage,Install-ChocolateyShortcut,Get-PackageParameters}.ps1`
  - `src/chocolatey/infrastructure.app/services/ShimGenerationService.cs`
  - `src/chocolatey/infrastructure.app/ApplicationParameters.cs`
- Other repositories and threads:
  - [chocolatey/chocolatey-test-environment](https://github.com/chocolatey/chocolatey-test-environment), the
    verifier's Vagrant setup
  - [chocolatey/package-validator](https://github.com/chocolatey/package-validator), archived 2021
  - [chocolatey/home#82](https://github.com/chocolatey/home/issues/82) and
    [org discussion #163](https://github.com/orgs/chocolatey/discussions/163), the 200 MB limit
  - [actions/runner-images](https://github.com/actions/runner-images): `Windows2025-Readme.md` and
    `Windows2022-Readme.md` both list Chocolatey 2.7.4
- The feed API `https://community.chocolatey.org/api/v2/`:
  - `FindPackagesById()` for ID availability;
  - `Packages()` with `PackageTestResultStatus` for the verifier exemptions.
- Packages read for precedent: `ditto`, `copyq`, `powertoys`, `microsoft-windows-terminal`, latest versions on
  2026-10-01.
