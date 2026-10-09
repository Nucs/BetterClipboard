# Code signing through the SignPath Foundation

User request (2026-10-02): "I want you to go ahead and submit to get SignPath Foundation's signing for this project.
Let me know if we need to do some changes in order to pass their minimum requirements".

This page has five parts:
- what the SignPath Foundation requires, condition by condition, and where BetterClipboard stands;
- the application form with every answer written out;
- the one condition the repository cannot meet by itself;
- how signing gets wired into the release once the application is accepted;
- what to do if it is declined.

## TL;DR

| | |
|---|---|
| What it is | Free Authenticode signing for open-source projects. [SignPath.io](https://about.signpath.io) signs with a key that never leaves its hardware security module, and the [SignPath Foundation](https://signpath.org) holds the certificate. |
| What Windows shows | Publisher **SignPath Foundation**. Neither Eli Belash nor BetterClipboard appears, because "the code signing certificate is issued to SignPath Foundation". The form adds: "A short URL including this Handle is part of your certificate (sig.fo/Handle)". |
| What it costs | Money: nothing. In practice, every release is approved by hand in SignPath, and only binaries built by GitHub Actions from this repository can be signed. |
| The repository | Prepared and pushed 2026-10-02 (`543d2fd`): the README's [Privacy](../README.md#privacy) and [Code signing policy](../README.md#code-signing-policy), the policy link on every release page (the five published pages corrected the same day), and the policy link in the Chocolatey description. The product metadata was already consistent. |
| The application | **Not submitted.** The user decided on 2026-10-02: publish the preparation now, and apply once there is reputation evidence (§2.1). The name will be **Nucs BetterClipboard** (handle `nucs-betterclipboard`). |
| The deciding gap | **Reputation.** The terms say "we cannot sign binaries based on source code that nobody knows. For executable programs that may be downloaded and executed based on our signature, we require a certain verifiable reputation." On 2026-10-02 the repository was 7 days old, with 0 stars and 1–4 downloads per release file. |
| The second gap | **The name.** "Better Clipboard" is also a commercial Mac app (betterclipboard.com), a Minecraft mod and an Electron library. The form wants a name a search finds first, and asks to qualify generic ones. |

## 1. The conditions

Source: [signpath.org/terms.html](https://signpath.org/terms.html), read in full on 2026-10-02. It is still marked as a
draft.

| Condition | BetterClipboard | Status |
|---|---|---|
| **No malware**, no potentially unwanted programs | A clipboard manager whose whole source is public | ✅ |
| **OSS license** (OSI-approved, no commercial dual-licensing) "for all components" | [MIT](../LICENSE) | ✅ |
| **No proprietary code**, apart from System Libraries in the sense of [GPLv3 §1](https://www.gnu.org/licenses/gpl-3.0.html) | See the note below the table | ⚠️ argued, not confirmed |
| **Maintained** | Commits every day since 2026-09-25 | ✅ |
| **Released** in the form to be signed | v0.1.0–v0.2.3: self-contained zips built by [`release.yml`](../.github/workflows/release.yml) | ✅ |
| **Documented**: the functionality is described on the download page | The README is both home page and download page. The release pages show their release notes (corrected 2026-10-02; the second note below the table) | ✅ |
| **Sign your own project, your own binaries only** | Six files (§4), all built from this repository. Nothing of another maker's is ever signed | ✅ |
| **No hacking tools** | The keyboard hook only watches for the panel's shortcut and swallows that one key ([CLAUDE.md](../CLAUDE.md) §2.4). It records nothing | ✅ |
| **Respect privacy**: a transfer of user data needs a privacy policy, shown at install, with an opt-out | One request leaves the PC by itself since 2026-10-09: the daily update check (GitHub's public release list; the request carries no user data, only the app's version). All three demands are met for it: README › [Privacy](../README.md#privacy) describes it and links GitHub's privacy statement, the installer shows a note about it with that link when it finishes, and `-NoUpdateCheck` installs with it off (*Settings › Updates* switches it later). The same section lists every local source, its default and its switch | ✅ |
| **Announce system changes** | `install.ps1` documents its Start menu shortcut, startup entry and Installed-apps entry (`-NoStartup`, `-NoShortcut`). Releasing Win+V (`DisabledHotkeys` plus an Explorer restart) is opt-in, and Settings warns about it | ✅ |
| **Provide uninstallation** | Installed apps, `install.ps1 -Uninstall`, `choco uninstall` | ✅ |
| **MFA** "for both SignPath and source code repository access" | GitHub: on (confirmed by the user, 2026-10-02). SignPath: turn it on when invited | ✅ GitHub; SignPath at onboarding |
| **Team roles**: authors, reviewers, approvers | README › Code signing policy (one maintainer holds all three) | ✅ |
| **"Code signing policy"** on the home page and on the download/release pages: the SignPath sentence, the roles, a privacy policy | README section. Every new release page gets a footer from `release.yml`, and the Chocolatey description links it too | ✅ (the sentence in its "not yet" form, §3) |
| **Metadata**: product name and version set on every signed binary, enforced by metadata restrictions | All six files: `ProductName` BetterClipboard, `ProductVersion` `X.Y.Z+<commit>`, `FileVersion` X.Y.Z.0 | ✅ |
| **Don't fight the system**: built from source verifiably, manual approval for each release | GitHub-hosted `windows-latest` runner. Approval is part of the plan (§4) | ✅ |
| **A certain verifiable reputation** (the terms' "Common misunderstandings") | §2 | ❌ |

**Proprietary components: the argument.** Our own code is MIT. Most of what the release bundles is open source:
- the .NET runtime and CommunityToolkit.Mvvm (MIT);
- SQLite3 Multiple Ciphers and ZstdSharp (MIT);
- SQLitePCLRaw (Apache-2.0);
- the WebView2 SDK (BSD-3-Clause).

Two Microsoft components ship as binaries under Microsoft's license terms:
- the Windows App SDK runtime (WinUI 3);
- the Windows SDK projection (`Microsoft.Windows.SDK.NET.dll`).

[THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md) has the details. Their source is on GitHub under MIT
(microsoft/WindowsAppSDK, microsoft/microsoft-ui-xaml).

Both qualify as System Libraries in GPLv3's sense. They exist only to use Windows' own window system and APIs from
the app: "serves only to enable use of the work with that Major Component". The release carries them unmodified,
signed by Microsoft. Measured in the 0.2.4 release folder, 247 of its 257 files carry a signature:
- 183 by .NET;
- 59 by Microsoft Corporation;
- 3 by .NET DAC;
- 1 by Microsoft Windows;
- 1 by the .NET Foundation.

The 10 unsigned files are our six, plus SQLite3MC and three SQLitePCLRaw files. From 0.2.5 on, ZstdSharp is unsigned
too. The terms allow exactly that: "You may include unsigned binaries of upstream OSS projects". SignPath has not
confirmed this reading. If a reviewer asks, this paragraph is the answer.

**The old release pages.** v0.1.0–v0.2.3 were published with the tagged commit's message as their notes, not with
the notes in the tag annotation. `actions/checkout@v5` fetches a pushed tag by its commit, so the clone's tag is
lightweight. `gh release create --notes-from-tag` then falls back to the commit's message. checkout fixed this in v6
([actions/checkout#2356](https://github.com/actions/checkout/pull/2356)).

`release.yml` now fetches the tag object itself and refuses a tag that is not annotated. The five existing pages were
corrected on 2026-10-02 (§4, "Re-publishing the old notes"): each now shows its annotation plus the footer, which is
the same text the workflow builds today.

## 2. The application form

The live form at [signpath.org/apply](https://signpath.org/apply) is a HubSpot form (portal 145110231, form
`bf62807d-bb72-4e45-9bde-1f3a53ba2472`, EU region). Its fields and their guidance come from SignPath's published
[`OSSRequestForm-v4.xlsx`](https://signpath.org/assets/OSSRequestForm-v4.xlsx). `*` marks a required field.

| Field | Answer | Guidance from the form |
|---|---|---|
| Name `*` | `Nucs BetterClipboard` (the user's choice, 2026-10-02; see below) | "a Google search for that name should present your project … at the top"; generic names "must be qualified"; "You might use your own name (real name or pseudonym, e.g. GitHub username) as a prefix or suffix" |
| Handle `*` | `nucs-betterclipboard` | valid in file names and URLs; "A short URL including this Handle is part of your certificate (sig.fo/Handle)" |
| Type `*` | `Program` | Library only for packages "meant to be used by developers" |
| License `*` | `MIT License — https://opensource.org/license/mit` | "name incl. version number … permanent license URL"; OSI-approved only |
| Repository URL `*` | `https://github.com/Nucs/BetterClipboard` | "must be the same URL as you're using in your CI system. It will be verified for every build" |
| Homepage URL `*` | `https://github.com/Nucs/BetterClipboard` | the repository is allowed if it introduces the project to users (the README does) |
| Download URL | `https://github.com/Nucs/BetterClipboard/releases` | "This page must provide signing information according to SignPath Foundation Terms of Use" |
| Privacy Policy URL | `https://github.com/Nucs/BetterClipboard#privacy` | required when the program transfers user data. The update check sends none, but it is a request the user did not make, so the URL is given and the section describes it |
| Wikipedia URL | — | |
| Tagline `*` | `Persistent, searchable, encrypted clipboard history for Windows that takes over Win+V` | shown as "Name – Tagline" |
| Description `*` | the paragraph below | one paragraph that "must not change when new (major) versions are released" |
| Reputation `*` | §2.1 | "Describe how we can verify that your project is used and trusted" |
| User Full Name `*` | `Eli Belash` | "the user to be registered in SignPath" |
| User Email `*` | the maintainer's notification address | not written here: this file is public |
| Build System `*` | `GitHub Actions` | AppVeyor or GitHub Actions |
| Accept terms of use `*` | `I hereby accept the terms of use` | typed literally |

**Description:**

> BetterClipboard is an open-source clipboard history manager for Windows that takes over the Win+V shortcut from
> Windows' built-in clipboard history. It keeps what you copy in a history that survives restarts, searches it
> instantly and pastes the item you pick back into the app you came from. The history is encrypted on disk and bound
> to your PC and Windows account, and nothing is sent anywhere. It is written in C# on .NET with WinUI 3, and
> released under the MIT license.

**The name.** Searched on 2026-10-02, "BetterClipboard" gives:
- first, betterclipboard.com, "Better Clipboard", a Mac app also sold on the App Store;
- second, this repository;
- further down, a CurseForge/Modrinth Minecraft mod, `simo-an/better-clipboard` (Electron) and
  `TrendingTechnology/BetterClipboard`.

Options:
1. Qualify only the application's name and keep the product name. The binaries say `BetterClipboard`.
2. Rename the product. That touches the binaries, the data folder, the installer, the Chocolatey ID and the docs.

**Decided (the user, 2026-10-02): option 1, as `Nucs BetterClipboard`.** The GitHub handle is the prefix, as the
form suggests, and the handle is `nucs-betterclipboard`. Still open: whether SignPath accepts a qualified project
name over binaries whose product name is `BetterClipboard`. The terms' "Set all product name attributes to your
project's name" can be read either way. If a reviewer asks, offer `product-name="BetterClipboard"` in the artifact
configuration with the qualified name only on SignPath's side, or change `<Product>` in `Directory.Build.props`
(every shipped binary inherits it).

### 2.1 Reputation

The form names the evidence it accepts:
- media reports and blog articles;
- Wikipedia articles in other languages, Softpedia and the like;
- usage data such as downloads, forks or dependencies;
- GitHub Insights;
- proof of trademark ownership for a trademarked name.

What exists on 2026-10-02:

| | |
|---|---|
| Repository | Public since 2026-09-25: 0 stars, 0 forks, 0 watchers, no issues |
| Downloads | 1–4 per release file over five releases (GitHub's asset counters), most of them the maintainer's own installs |
| Traffic (14 days) | 15 views from 1 visitor; 187 clones from 99 cloners, typical of automated mirrors; referrer github.com only |
| Coverage | none |
| The maintainer | GitHub user since 2011, 91 followers. Top contributor of [SciSharp/NumSharp](https://github.com/SciSharp/NumSharp) (2,400 commits; 1,481 stars). Author of [JsonSettings](https://github.com/Nucs/JsonSettings) (84 stars) and [cryptocurrency-ticks-data](https://github.com/Nucs/cryptocurrency-ticks-data) (94 stars) |

The maintainer's record is real, but the terms ask about the program's own reputation. Two applicants in the same
position, found on 2026-10-02:
- [jordanfelle/nicti#289](https://github.com/jordanfelle/nicti/issues/289) deferred its own application: "5-day-old
  repo with 0 stars";
- [wslkit/skrog#358](https://github.com/wslkit/skrog/issues/358) was declined "for now": "It is for projects with
  an established user base".

The terms also ask applicants not to argue: "we can only provide this service if we can keep the manual work for
each applicant to a minimum".

**Building it, without Chocolatey first** (the user's call, 2026-10-02: "Can we do without choco first?"). Chocolatey
was only ever one source of public download numbers: SignPath does not ask for any package manager. GitHub keeps the
numbers itself:
- stars, forks and issues;
- release download counters, public through the API;
- GitHub Insights traffic (views, unique visitors, referrers), which the form names. Only the owner sees it, so quote
  it or attach a screenshot.

None of this is done yet. In order:
1. **Posts where Windows users look:** r/Windows11, r/windowsapps, r/software, r/opensource; a Show HN on Hacker News.
   They bring the users whose stars, downloads and referrers then show on GitHub. They have to come from the
   maintainer's own accounts.
2. **Software directories:** Softpedia (the form names it), AlternativeTo (as an alternative to Ditto, CopyQ and
   Windows' Win+V), MajorGeeks. Each listing is a link for the Reputation field.
3. **Optional: a winget manifest** ([CLAUDE.md](../CLAUDE.md) §6). It is a reviewed listing, but install counts are
   not public, so it adds little evidence.
4. **Later: Chocolatey** ([chocolatey.md](chocolatey.md) §5). It has its own human review, and the verifier exemption
   to ask for.

Apply once these show real numbers. Put the links and the numbers in the Reputation field.

## 3. The README section in its three states

The terms ask for the sentence "Free code signing provided by SignPath.io, certificate by SignPath Foundation". It is
only true once the certificate is granted, so the section changes twice:

1. **Now** (this commit): "**Not signed yet.** … Signing is planned through the SignPath Foundation … Once it is
   granted, this paragraph will read: *Free code signing provided by SignPath.io, certificate by SignPath
   Foundation.*"
2. **On applying:** replace "Signing is planned through the SignPath Foundation, which signs open-source projects for
   free" with "Free code signing provided by SignPath.io, certificate by SignPath Foundation — **applied for on
   yyyy-MM-dd, not granted yet**". The form's Download URL "must provide signing information", so the reviewers
   read this text.
3. **Once granted:** lead with "Free code signing provided by [SignPath.io](https://about.signpath.io), certificate
   by [SignPath Foundation](https://signpath.org)". Name the first signed release, and drop "Not signed yet".

The roles, the list of signed files and the privacy paragraph stay the same in all three. Since 2026-10-09 that
paragraph is no longer SignPath's bare sentence: it links the README's *Privacy* section, names the update check and
its two switches, and gives the sentence for everything apart from the check.

## 4. After acceptance: signing in the release

**In SignPath** (the Foundation creates the organization and invites the maintainer):
1. Turn on MFA.
2. Link the predefined trusted build system *GitHub.com* to the project, and install the SignPath GitHub App. It is
   "Required for audit log evaluation".
3. Create the project `betterclipboard` with two signing policies:
   - `test-signing`, using SignPath's test certificate, without approval, for trying the pipeline;
   - `release-signing`, using the Foundation's certificate, approved by the Approver.
4. Create a CI user with the submitter role, and its API token.

**In GitHub:** the secret `SIGNPATH_API_TOKEN` and the variables `SIGNPATH_ORGANIZATION_ID`,
`SIGNPATH_PROJECT_SLUG` (`betterclipboard`) and `SIGNPATH_SIGNING_POLICY_SLUG` (`release-signing`).

**The artifact configuration** (draft; validate it in SignPath's editor, which has the XSD). Only our six files, each
held to the product name and the build's version:

```xml
<artifact-configuration xmlns="http://signpath.io/artifact-configuration/v1">
  <parameters>
    <!-- "X.Y.Z+<40-hex commit>": the SDK (SourceLink) appends the commit to the informational version, which is what
         a PE file reports as ProductVersion. Measured: 0.2.4's release build says 0.2.4+98addc6…, all six files alike. -->
    <parameter name="version" required="true" />
  </parameters>
  <zip-file>
    <directory path="win-x64">
      <pe-file-set product-name="BetterClipboard" product-version="${version}">
        <include path="BetterClipboard.exe" />
        <include path="BetterClipboard.dll" />
        <include path="BetterClipboard.Core.dll" />
        <include path="BetterClipboard.Windows.dll" />
        <include path="bclip.exe" />
        <include path="bclip.dll" />
        <for-each><authenticode-sign /></for-each>
      </pe-file-set>
    </directory>
    <directory path="win-arm64">
      <!-- the same <pe-file-set> -->
    </directory>
  </zip-file>
</artifact-configuration>
```

Restrictions left off on purpose:
- `original-filename`: the apphost `.exe` reports its `.dll`'s name (`BetterClipboard.exe` says `BetterClipboard.dll`);
- `company-name` and `copyright`: they add nothing here.

**The release workflow** changes from "publish, then archive" to "publish, sign, then archive":
1. Run [`package.ps1`](../tools/release/package.ps1)'s publish half for both architectures into
   `publish/win-x64` and `publish/win-arm64`. The script needs a split for this: today it publishes and archives in one
   go.
2. `actions/upload-artifact` the `publish` folder. Its id goes to the next step.
3. Run `signpath/github-action-submit-signing-request@v3` with the API token, organization, project and policy, the
   artifact id, `wait-for-completion: true` and `output-artifact-directory: signed`, and
   `parameters: version: "<X.Y.Z>+${{ github.sha }}"`.
   - Check the value against the built `BetterClipboard.exe` first, so a drift fails before signing.
   - The default wait is 600 s, and a hand approval can take longer. Raise
     `wait-for-completion-timeout-in-seconds`, or approve while the job waits.
4. Archive the *signed* folders: zips, `.7z`, `SHA256SUMS.txt`. Then the Chocolatey package and the release, as
   today. The checksums must be computed after signing, because signing changes the bytes.
5. Every job before the signing request must run on GitHub-hosted runners, which SignPath checks for OSS projects.
   `release.yml` already does.

`install.ps1` could be signed too (`<powershell-file>`). Users run it as `irm … | iex` from the repository's raw file,
where a signature does nothing, so it is not worth an approval.

**Re-publishing the old notes** (done 2026-10-02 for v0.1.0–v0.2.3, with the user's go-ahead). For each tag:
1. Back up the current body.
2. Build the notes the way the workflow's "Release notes from the tag" step does.
3. Run `gh release edit vX.Y.Z --notes-file notes.md`.
4. Read the body back through the API (`gh api repos/Nucs/BetterClipboard/releases/tags/vX.Y.Z --jq .body`).
   `gh release view --json` has no `isLatest` field: asking for one prints nothing, which reads like a failed edit.

Run gh without `GH_TOKEN`/`GITHUB_TOKEN` in this shell: those tokens are invalid here.

## 5. If the answer is no

| Option | Publisher shown | Cost | Notes |
|---|---|---|---|
| Re-apply later | SignPath Foundation | free | after §2.1's evidence exists |
| [Certum Open Source Code Signing](https://shop.certum.eu/open-source-code-signing.html) | the maintainer's name | from €69 (the shop, 2026-10-02) | identity check, cloud HSM (SimplySign); validity at most 459 days from 2026-02-27 |
| [Azure Artifact Signing](https://learn.microsoft.com/azure/artifact-signing/) (formerly Trusted Signing) | the maintainer's name | ~$10 a month | individuals in the US, Canada, the EU and the UK only (2026-10) |
| Stay unsigned | — | — | what releases do today: SHA-256 checked by the installer, `SHA256SUMS.txt` for manual downloads |

## 6. Verified on 2026-10-02

- **The terms:** read in full. The form's fields and guidance came from SignPath's own `OSSRequestForm-v4.xlsx`.
  The live form is HubSpot, rendered by script, so it was not filled in.
- **The binaries:**
  - version resources and signatures of the 0.2.4 release folder (257 PE files) and of the 0.2.5 Debug build;
  - our six files agree on `ProductName` and `ProductVersion` in both;
  - every other file except SQLite3MC, SQLitePCLRaw and ZstdSharp is signed by its maker.
- **The network:** a search of `src/` found no HTTP client, socket or WebView use. A link opens in the browser only
  when the user asks: the link card's *Open link* (`Launcher.LaunchUriAsync`), Settings' third-party links
  (`NavigateUri`).
  - **Changed 2026-10-09:** the update system ([CLAUDE.md](../CLAUDE.md) §2.29) added one HTTP client,
    `Core/Updates/UpdateClient`. It requests GitHub's release list once a day (anonymous; `User-Agent:
    BetterClipboard/<version>`; no cookies), and the package and its checksum list after the user approves an update.
    Downloads are accepted only from this repository's release downloads. There is still no socket, WebView or
    telemetry code. Re-run the search before applying: every other request would need its paragraph in the README.
- **The release-notes step:**
  - its script, taken from `release.yml`, ran in a scratch clone whose tag was forced lightweight, as checkout@v5
    leaves it. The tag came back annotated, and the notes were v0.2.3's annotation plus the footer, with "—", "…" and
    "›" intact under a 437 console;
  - a tag missing on the remote failed the step.
- **Reference sources:**
  - gh 2.85's `--notes-from-tag` (`gitTagInfo`: `%(contents)` minus `%(contents:signature)`);
  - `actions/checkout`: `testRef` without `^{commit}` in v5, with it in v6 and v7 (commit `de0fac2`, #2356).

## Sources

- SignPath Foundation: [terms](https://signpath.org/terms.html), [apply](https://signpath.org/apply),
  [projects](https://signpath.org/projects), [OSS request form](https://signpath.org/assets/OSSRequestForm-v4.xlsx)
- SignPath documentation: [GitHub trusted build system](https://docs.signpath.io/trusted-build-systems/github),
  [artifact configuration reference](https://docs.signpath.io/artifact-configuration/reference),
  [syntax and parameters](https://docs.signpath.io/artifact-configuration/syntax),
  [examples](https://docs.signpath.io/artifact-configuration/examples)
- Other applicants: [kicad-ultra#141](https://github.com/danielmeza/kicad-ultra/pull/141) (form fields, two policy
  variants), [nicti#289](https://github.com/jordanfelle/nicti/issues/289),
  [skrog#358](https://github.com/wslkit/skrog/issues/358)
- [actions/checkout#2356](https://github.com/actions/checkout/pull/2356) (annotated tags kept since v6)
