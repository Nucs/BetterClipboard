---
name: local-dev
description: Publish a development build of BetterClipboard to this PC's dev-builds folder so the installed app's update button offers it, and switch the installed app onto dev builds (bootstrap). Use when the user asks to "publish a dev build", "update my local app", "push this to my install", "install dev", "local dev", "dev update", "make the update button show the new build", to roll a dev install back to the release, or when a dev build is not offered. Covers tools/release/publish-dev.ps1 (version scheme, worktree build, notes, pruning, -Install), the folder layout, the update rule (a newer local build first; a dev install is always offered GitHub's latest release), verifying, rolling back, troubleshooting, and the guards for running it from an agent shell on the user's real install.
---

# local-dev: dev builds through the update button

Built 2026-11-08 on the owner's request: "i want my local app to update to a local version through the update button.
Any time we are in a dev install, latest version from github is higher than the dev we have - unless we have a newer
dev then it suggests the newer dev. Have for local updates a dedicated user folder that we can publish to and then our
local can update easily." Design and verification: CLAUDE.md §2.39.

## The rule (what the update button offers)

For a copy installed by `install.ps1` (Settings › Apps lists it; `installer.json` names its folder):

1. **A local build newer than the running version** — from the dev-builds folder — comes first, also when GitHub has a
   newer release (the owner chose this: "Always, unless a newer dev").
2. Otherwise **GitHub's latest release**: on a **dev install** (version `X.Y.Z-dev…`) **always**, also when it is older
   than the dev build ("Back to the release: BetterClipboard 0.2.9", button "Install and restart"); on a release install
   only when newer (as before).

| Installed | GitHub | Local folder | Offered |
|---|---|---|---|
| 0.2.10-dev.12 | 0.2.9 | none newer | 0.2.9 (the way back) |
| 0.2.10-dev.12 | 0.2.9 | 0.2.10-dev.15 | 0.2.10-dev.15 |
| 0.2.10-dev.12 | 0.2.10 | none newer | 0.2.10 |
| 0.2.10-dev.12 | 0.2.10 | 0.2.10-dev.15 | 0.2.10-dev.15 |
| 0.2.9 (release) | 0.2.9 | 0.2.10-dev.3 | 0.2.10-dev.3 |

So a dev install's button is lit whenever GitHub's list is known. *Skip this version* quiets it for that version. Dev
builds never get the weekly reminder tooltip. Chocolatey, all-users and hand-extracted copies never read the folder.

## Publish

From the repository root (pwsh 7 or Windows PowerShell 5.1):

```powershell
pwsh -NoProfile -File tools/release/publish-dev.ps1            # builds HEAD's commit, publishes, prunes (63–96 s here)
pwsh -NoProfile -File tools/release/publish-dev.ps1 -Install   # ...and installs it now (the bootstrap, below)
```

What it does:
1. **Version** `<next release>-dev.<commits since the last release tag>.g<short sha>`, e.g. `0.2.10-dev.12.gabc1234`.
   The next release is `Directory.Build.props`' `<Version>` at that commit, or its next patch when that one is tagged
   already. Later commits sort higher; a release outranks every dev build of its own number.
2. **Builds the commit, never the working tree**: a temporary `git worktree` in `%TEMP%`, then that commit's
   `tools/release/package.ps1 -SkipArchives` (Release, self-contained, the release zip). Uncommitted work — yours or
   another session's — is never in a published build; the script warns when the tree has changes. Commit first.
3. **Publishes** `<dev-builds>\<version>\` = the zip, `SHA256SUMS.txt`, `install.ps1`, `notes.md` (commits since the
   previous build in the folder, else since the last release tag) and `build.json` (written last). Everything is staged
   in `.publishing-…` and renamed into place, so the app never sees half a build.
4. **Prunes**: keeps the newest `-Keep` (5) builds, drops builds an official release supersedes, and `.publishing-…`
   leftovers older than an hour.
5. **Says what the installed copy will do**: offers it now, already has it, or predates the folder (then `-Install`).

The installed app's `LocalBuildsWatcher` sees the rename and lights the update button within about a second. Click it:
*Update and restart* copies the zip from the folder into `updates\`, checks it against the build's `SHA256SUMS.txt`, and
runs that build's own `install.ps1 -Update` (the same contract as GitHub updates, CLAUDE.md §2.29). 10–35 s from the
click to the new version running (33 s in a test desktop, most of it Windows PowerShell starting and unpacking); the
history, settings and shortcuts are kept.

Options: `-Ref <commit|branch|tag>`, `-Test` (run the suite in the worktree first; publish nothing on failure),
`-Force` (rebuild a version already published), `-Keep N`, `-Architectures x64,arm64` (default: this PC's),
`-Version X.Y.Z-dev.…` (override), `-NoStart` (with `-Install`), `-DevBuildsDirectory` (override the folder).

## The folder

`%LOCALAPPDATA%\BetterClipboard\dev-builds\` — or `<BETTERCLIPBOARD_DATA_DIR>\dev-builds\` for an isolated instance, which
then reads only its own. Only the publish script creates it, so it exists only on a developer's PC. A folder counts as a
build when its name equals `build.json`'s `version` and the zip for this PC plus a `SHA256SUMS.txt` listing it are there;
anything else (dot-folders included) is ignored. Delete builds by hand freely.

## Bootstrap: the first dev install

An installed **release** of 0.2.9 or older cannot read the folder. `publish-dev.ps1 -Install` installs the build with
its own `install.ps1 -Package <zip> -NoLaunch` (Windows PowerShell 5.1, `PSModulePath` removed), then starts the app
through Explorer with `--background` (the user's environment, never the shell's). From then on the update button does
it — also after an official release is installed (a release reads the folder too and offers newer dev builds).

`install.ps1`'s full install closes **every** BetterClipboard of the session (also a `tools/launch_dev.py` copy and other
sessions' test instances): close those first (`python tools/launch_dev.py --exit`).

## From an agent shell (Claude Code) on the owner's PC

- `-Install` (and clicking *Update and restart*) restarts the owner's real app: only on their request. The request
  "install/update my local app" is that OK. Publishing alone changes nothing they run — but it lights their button.
- Prefer publishing and letting the owner click; never drive their update dialog unless they ask (CLAUDE.md §4: "Never
  test an update on the user's installed copy").
- Build from a committed state: check `git status`; another session's uncommitted files are not in the build (by design).
- To test the script itself, point it at a scratch data folder: `BETTERCLIPBOARD_DATA_DIR=<scratch> pwsh -File
  tools/release/publish-dev.ps1` (no `-Install` here: `install.ps1` would still install into the real Programs folder).
  The whole loop (install A, publish B, click) belongs in a claude-desktops Windows desktop (skill `claude-desktops`).
- Verify after an install: `installer.json` version, the log after "starting" (0 WRN/ERR; the line "Updates: … a
  development build …"), and for an offer the log line `Local builds: N in the dev-builds folder; BetterClipboard X is
  offered`.

## Roll back to the release

On a dev install the button always offers GitHub's latest release: *Install and restart*. Or the README's
`irm https://raw.githubusercontent.com/Nucs/BetterClipboard/main/install.ps1 | iex`. Remove `dev-builds\` too if the
release should stop offering newer dev builds.

## Troubleshooting

| Symptom | Look at |
|---|---|
| Button not lit after publishing | Installed version newer or equal (`installer.json`)? Copy not installed by `install.ps1` (Chocolatey, all-users, extracted)? Installed copy ≤ 0.2.9 (`-Install` once)? Log: `Local builds: …` lines. Opening the dialog or *Check now* looks again. |
| "The copy does not match the build's checksum" | The zip changed after `SHA256SUMS.txt` was written: publish again (`-Force`). |
| "… changed in the dev-builds folder after it was offered" | Republished meanwhile: open the dialog again. |
| `install.ps1 failed` in `-Install` | Its output above; another BetterClipboard holding the install folder (CLAUDE.md §4). |
| Package step fails | Build errors of that commit; `dotnet build BetterClipboard.sln` there. `-SkipArchives` needs a commit from 2026-11-08 on (older ones build `.7z` too and need 7-Zip). |

## Code map

`Core/Updates/LocalBuildFeed.cs` (reading, copying, checks) · `UpdateCatalog.FindUpdate` (the rule) /
`SelectChangelog` · `UpdateService.RefreshLocalBuilds` + local branch of `RunInstallAsync` · `UpdateText` (wording) ·
`Windows/Updates/LocalBuildsWatcher.cs` · `App/AppController.Updates.cs` (wiring) · `tools/release/publish-dev.ps1` ·
tests `LocalBuildTests.cs`, `UpdateServiceTests` (`LocalBuild_*`, `DevelopmentBuild_*`), `LocalBuildsWatcherTests.cs`.
