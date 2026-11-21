# Release notes in several languages: how the update dialog, the release page and the Store could show them

User request (2026-11-16): "The release notes should somehow have support for multiple languages (showing by default
the language the app is in) / You are tasked with discovering how can we do that".

This report says where release notes appear today, what limits every design (the copies already installed above all),
the ways translations could travel, the one that works, and what would change where. It was written on 2026-11-16,
before anything was built. **Built on 2026-11-21 for 0.2.12, in Simplified Chinese:** §5 records the owner's decisions
and how what was built differs from the proposal in §4; the summary for the code is `CLAUDE.md` §2.67.

**How it was learned.**
- **The code** at `683ed40`: `GitHubReleases`, `ReleaseInfo`, `UpdateCatalog.SelectChangelog`, `UpdateView.RenderNotes`,
  `MarkdownParser` (`Core/Presentation/Markdown.cs`), `MarkdownRenderer`, `UiText`; the notes step of
  [`release.yml`](../.github/workflows/release.yml) and [`store.yml`](../.github/workflows/store.yml);
  [`submit-store.ps1`](../tools/release/submit-store.ps1); the Store's listings template; `publish-dev.ps1`; the
  Chocolatey nuspec. The parser of every release tag was read from git.
- **GitHub, nothing published:** the release list as the app asks for it (12 releases, 2026-11-16), v0.2.11's page as
  GitHub draws it (`body_html`), and GitHub's renderer (`/markdown`) for candidate bodies.
- **A new probe,** [`tools/probes/probe_release_notes.py`](../tools/probes/probe_release_notes.py): it builds the update
  dialog's Markdown parser exactly as each released version has it, parses a body with and without its translation
  blocks, and compares what the dialog would draw; with `--github` it compares GitHub's rendering the same way.

Legend: **[verified]** = measured or run, **[code]** = read in the source at `683ed40`, **[docs]** = public
documentation or reports, **[inferred]** = deduced, not measured.

---

## 0. Summary

- **Today everything is English.** The release body is the annotated tag's message plus a code-signing footer. The update
  dialog draws that body. The Store gets a shortened English "What's new" in its `en-us` listing and an empty one in
  the other four.
- **The constraint that decides the design:** every copy installed since 0.2.6 draws the bodies of newer releases in its
  update dialog, HTML tags as plain text. So whatever goes into a body, they show it — except an HTML comment block.
  Their parser skips from a line that starts with `<!--` to the next line that contains `-->`, and GitHub's page drops
  such a block too **[verified]**.
- **Recommended:** the English notes stay visible. Each translation goes into the same body as a hidden block:
  `<!-- release-notes zh-Hans` … `-->`. Then:
  - no new network request is needed;
  - installed copies and the release page show exactly what they show today;
  - a version that knows the blocks shows its interface language, English when a release has no translation;
  - the Store step fills each listing's "What's new" from the same body.
- **The release page cannot show hidden blocks.** It can get one line under the title, like the README's language line,
  that links to each translation as a file in the repository at the release's tag.
- **The cost:**
  - a body about five times as long: some 31,000 characters, against GitHub's limit of 125,000;
  - a translation of the final notes at every release;
  - right-to-left drawing of Hebrew notes in the dialog.
- **When readers see it:** the version that learns the blocks shows translations from its next update on. The Store
  edition, which has no update dialog, gets them with the next Store submission after the Store step learns them; that
  needs no app change.

## 1. Where release notes appear today

| Where | What it shows | Language | Source |
|---|---|---|---|
| The release page on GitHub | The body, drawn by GitHub | English | `release.yml`: the annotated tag's message, then `---` and a code-signing footer **[code]** |
| The update dialog (0.2.6 and later; every copy that checks, also Chocolatey and all-users copies) | The whole body of each release newer than the running one, newest first, at most 10; else the running release's own notes; else the latest's | English | The body from GitHub's release list, the request the update check makes anyway (`GitHubReleases.Parse`, `UpdateCatalog.SelectChangelog`, `UpdateView.RenderNotes`) **[code]** |
| The Microsoft Store's "What's new" | At most 1,500 characters of plain text | English in `en-us`; emptied in `zh-hans`, `ru`, `es` and `he` | `submit-store.ps1 -ReleaseNotesFile`: the release job's notes file, shortened by `ConvertTo-StoreNotes` **[code]** |
| The Store edition | Only the Store's "What's new" (it has no update service) | as above | — |
| Chocolatey | A link to the release page (`<releaseNotes>` in the nuspec) | — | — |
| Development builds | `notes.md`: the commit subjects since the previous build | English | `publish-dev.ps1` |
| The documentation site | Nothing | — | — |

The dialog draws everything in the body: the install command, the downloads table and the code-signing footer included
(the probe lists 31 blocks for v0.2.11) **[verified]**.

## 2. What limits the design

### 2.1 Installed copies read every new release's body **[verified]**

- **What they do with it:** a copy that checks for updates draws the notes of every newer release in its dialog. It
  takes the body as it is: `MarkdownParser` shows HTML as the text it is (`<br>` excepted), so nothing in it can run, and
  nothing in it can be hidden from those copies with HTML.
- **The one exception: an HTML comment block.** The parser drops a block that starts on a line beginning with `<!--` and
  ends on the next line containing `-->` ("A comment is the author's note to themselves"). It has done so since the update
  dialog came (`7d7df8d`, in v0.2.6).
- **Two parser versions are installed:** v0.2.6 to v0.2.10 share one file (blob `9fb869bbf663`), and v0.2.11 and `HEAD`
  another (`c9c02529aeca`). The probe built both and ran three bodies through them:
  - translations in comment blocks: **hidden** in both, on a sample and on v0.2.11's real body with two blocks added (31
    blocks drawn, the same as without them);
  - translations in `<details>` sections: both draw `<details><summary>简体中文</summary>` as a paragraph of text, then
    the translation, then `</details>`. A `<!-- -->` in front of the opening tag hides that line, never the translation
    under it;
  - a `-->` inside a translation ends the block early: the rest of that translation shows in every installed copy.
- **Who is affected:** every installed copy. The checksum list, which the installer and the update button download,
  shows 7 to 35 downloads for each of 0.2.6 to 0.2.11 (GitHub's counts on 2026-11-16).

### 2.2 GitHub's release page **[verified]**

- **How it draws a body:** GitHub's renderer in `gfm` mode with the repository's context gives v0.2.11's page its
  structure: the same 31 line breaks and blocks. Only `dir="auto"` attributes and the code block's copy button differ.
  `markdown` mode (the README's) has no line breaks, so it is the wrong stand-in.
- **What it shows of the candidates:**
  - a comment block disappears from the page;
  - `<details>` sections render as closed sections, with Markdown inside after a blank line;
  - a `-->` **and also a `--!>`** inside a block end it early, and the rest shows on the page. The app ends a comment
    only at `-->`, so a `--!>` leaks on the page alone. Both must be refused in a translation.
- **Nothing in GitHub localizes a release.** There is one body per release, one page, no language switch.

### 2.3 Size **[verified]**

- **GitHub's limit:** a release body may hold 125,000 characters ("body is too long (maximum is 125000 characters)",
  HTTP 422; reported by many projects, §8).
- **Today:** 2,729 to 7,808 characters per release (v0.2.11: 6,346).
- **With translations:** the README's translations give the ratio to the English: Chinese 0.53, Russian 1.15, Spanish
  1.18, Hebrew 0.97. A 6,500-character English body would grow to about 31,000 characters, a quarter of the limit.
  The probe prints a body's size against the limit.
- **For installed copies:** the release list they fetch (30 releases a page) holds every release's body. Today it is 194
  KB for 12 releases. With five languages each release adds about 44 KB in UTF-8, so at most about 1.3 MB for 30,
  against the client's 8 MB cap (`UpdateClient.MaxFeedBytes`). An unchanged list costs one "304 Not Modified" a day,
  as now. The parser and the reader both cut at 200,000 characters (`MarkdownParser.MaxLength`,
  `GitHubReleases.MaxNotesLength`), above GitHub's limit.

### 2.4 The app's language and right-to-left text **[code]**

- **Which language:** `UiText.Current` is the interface language in effect: a catalog tag, `en`, `zh-Hans`, `ru`, `es`
  or `he`. A block can name its language with that same tag, and a missing one falls back to English. A future catalog
  needs no change in the notes' format.
- **Hebrew:**
  - the notes' renderer (`MarkdownRenderer`) has no right-to-left handling;
  - the parser decodes only `&amp; &lt; &gt; &quot; &#39; &apos; &nbsp;`;
  - the panel's own Hebrew texts are wrapped in a right-to-left embedding (U+202B … U+202C, `UiText.EmbedRightToLeft`),
    with a left-to-right mark after a Latin word's trailing signs ("Notepad++", `KeepTrailingSignsWithTheirWord`), in a
    left-to-right layout (the owner's choice, §2.37);
  - the Hebrew README follows GitHub's rules: `<div dir="rtl">`, `&rlm;` and `&lrm;` (CLAUDE.md §4).

  So Hebrew notes need the same embedding per block in the dialog, and either clean Markdown in the block or a parser
  that skips the `<div dir>` lines and decodes the two marks.

### 2.5 The Store **[code]**

- **Five listings, five "What's new":** the listings are `en-us`, `zh-hans`, `ru`, `es` and `he` (the listings
  template), and each has its own `ReleaseNotes`, at most 1,500 characters.
- **What fills them today:** `submit-store.ps1` writes the English notes into `-NotesLanguage` (default `en-us`) and
  empties the others, "so that no listing keeps the notes of an older version". Both the release job and `store.yml` pass
  it the notes file.
- **`ConvertTo-StoreNotes` is partly English-specific:**
  - it finds the code-signing note by `code[- ]sign|SmartScreen`. That works in every language only while translations
    keep the product name "SmartScreen", as the UI's rule for product names says (§2.37);
  - it finds "Fixed" by a lead-in that starts with `Fix`, and writes "1 fix." / "N fixes.";
  - it cuts a first sentence at `.`, `!` or `?`, which misses Chinese `。`.
- **The Store edition depends on it entirely:** it has no update dialog, so the Store's "What's new" is its users' only
  release notes.

### 2.6 Who sees translations first **[inferred]**

- **Installed copies cannot learn the format.** Up to the version that implements it, they show English (and the language
  line, §4.6, if there is one).
- **The implementing version** shows translations in its dialog from its next update on, and its own notes in "What is
  new in this version" when its body has blocks.
- **The Store edition** gets per-language "What's new" from the next submission after `submit-store.ps1` learns it,
  without any app change.

## 3. Ways to carry the translations

| | Installed copies (0.2.6 – the last version without it) | Release page | New request / Privacy section | Store | Effort |
|---|---|---|---|---|---|
| **A. Hidden blocks in the body** | English only, as today **[verified]** | English (plus a language line, §4.6) | None | From the same notes file | Small: a splitter, the dialog, the release and Store steps |
| **B. Visible `<details>` sections in the body** | All languages, with raw `<details>` tags **[verified]** | Closed sections per language | None | From the same file | Small, but every installed copy's dialog breaks |
| **C. A file attached to each release** (`release-notes.json`) | English only | English, plus a download nobody reads | One more request when the dialog opens in another language; the README's *Privacy* must name it | From the file | Medium: download, trust, cache |
| **D. Files fetched from the repository** (`raw.githubusercontent.com`) | English only | Links to the files render | Another host; the *Privacy* section must name it | From the files | Medium |
| **E. Notes embedded in the app** | — | — | None | — | Covers only the running version's own notes, never an offered newer one |

**A wins:** it needs no request, no new host and no trust rule, and it changes nothing for installed copies or the page.
B breaks every installed copy's dialog. C and D add network traffic that the privacy policy would have to name. E cannot
show the notes of an update, which is what the dialog is for.

## 4. The proposal (for the owner's decisions in §5)

### 4.1 The block format

```text
…the English notes, unchanged…

<!-- release-notes zh-Hans
…the same notes in Simplified Chinese, Markdown…
-->

<!-- release-notes he
…
-->

---

**Code signing policy:** …
```

- **The opening line:** `<!-- release-notes <tag>` alone, at the start of a line, after a blank line. `<tag>` is a
  catalog tag (`zh-Hans`, `ru`, `es`, `he`); English is the visible part, never a block. One block per language.
- **The closing line:** `-->` alone.
- **Inside:** any Markdown the notes use, but never `-->` or `--!>` (§2.1, §2.2). A translation that needs an arrow
  writes `→`. Blank lines and code fences are fine: neither GitHub nor the released parser ends the block at them.
- **The probe checks all of it,** and that the body stays under GitHub's limit.

### 4.2 Where translations are written

- **One file per language and release,** `docs/release-notes/<version>/<tag>.md`, committed before the tag. English stays
  the annotated tag's message, as in today's procedure (CLAUDE.md §3.1); the owner's question in §5 offers the
  alternatives.
- **A stamp against translations of an older draft.** Each file starts with a hidden line that records the SHA-256 of the
  English it was translated from: `<!-- source sha256:<hex> -->`, hidden on GitHub too. The notes change many times
  before a tag (0.2.12's draft went through a dozen commits), so a translation of an earlier draft must never ship.
- **Hebrew's file is written for GitHub:** `<div dir="rtl">`, `&rlm;`, `&lrm;`, the Hebrew README's rules. The release
  job removes the `<div>` lines and turns the two entities into their marks when it copies the text into the body, so
  the app gets plain Markdown.

### 4.3 The release job

- **Composing the body:** one script, used by `release.yml` and `store.yml` alike (today each builds its notes file
  itself). It reads the tag's message, then each `docs/release-notes/<version>/<tag>.md` at the tagged commit. It checks
  each file's stamp against the message, adds the blocks after the English, then the footer.
- **It stops in seconds,** before the tests, like a tag without notes does today: on a stale stamp, a malformed block, or
  a body over 125,000 characters.
- **Running the probe** on the composed file in the job would add the proof against both released parsers. It needs
  only `git` and the .NET SDK, both there already.

### 4.4 The app

- **Core (pure, tested):** a splitter that takes a body apart into the visible notes and the blocks by language, bounded
  like the rest of the reader (network input). `ReleaseInfo` keeps the body as it is; the splitting is done for the
  dialog.
- **The dialog:**
  - each release's notes in the reader's language (`UiText.Current`), else English;
  - a release shown in English for want of a translation says so in one line;
  - the language line (§4.6) is left out of the drawn notes, since the dialog has its own way to switch (§5);
  - *View on GitHub* can open the translated file instead of the English page.
- **Hebrew:** the blocks drawn with the panel's right-to-left embedding, in the left-to-right layout the owner chose for
  the interface.
- **Tests:** the splitter on good and broken bodies (unclosed, `-->` inside, duplicates, unknown tags, huge blocks), the
  fallback, and a fixture that runs the composed format through a copy of the released comment rule.

### 4.5 The Store

- **Each listing gets its own "What's new":** `en-us` ← English, `zh-hans` ← `zh-Hans`, `ru`, `es`, `he` ← their
  blocks; a listing without a translation keeps the English, or stays empty as today (a detail to settle when built).
- **`ConvertTo-StoreNotes` loses its English words:**
  - "Fixed" is any lead-in alone over bullets;
  - the count of fixes comes from a small table of words per language;
  - a first sentence also ends at `。`, `！` and `？`;
  - the code-signing note is found by its position after the table, or by "SmartScreen", kept in every translation.
- **Its tests** (`test-submit-store.ps1`) grow by the four languages.

### 4.6 The release page

- **A language line under the title,** as the README's: `**ENG** / [简体中文](…) / [Русский](…) / [Español](…) /
  [עברית](…)`. Each link opens `https://github.com/Nucs/BetterClipboard/blob/v<version>/docs/release-notes/<version>/<tag>.md`,
  which GitHub draws (with `dir="rtl"` for Hebrew, as the Hebrew README).
- **Installed copies show the line too, as links** (the probe draws it as a paragraph of links in both parser versions),
  so a reader of an old copy can open the notes in their language in the browser.
- **The release job writes it** from the files there are, so drafts stay as they are.

### 4.7 Checks

- **A release-notes checker** (`tools/release/`), in the manner of `tools/readme/check-translations.py`:
  - every translation has the English's structure: paragraphs, list items, code blocks equal to the character, table
    rows, link targets;
  - the stamp matches the English;
  - no `-->` or `--!>`;
  - Hebrew's direction marks;
  - each language's Store text fits in 1,500 characters;
  - the body fits in 125,000.
- **The probe** before tagging, with `--github`.

### 4.8 The release procedure, changed

1. The English notes are final (the draft, as today).
2. Claude translates them into each language of §5, writes the files with their stamps, runs the checker.
3. Commit, tag as today. The release job composes, checks, publishes, and sends each listing its "What's new".

## 5. Decisions, and what was built

The questions of 2026-11-16 were never answered. On 2026-11-21 the owner asked for the feature itself: "Can the release
notes have a on first rows a En/Cn but in chinese and when clicked the release notes are replaces with your
translation". Three questions settled the rest:

- **The line's words:** "ENG / 简体中文 (Recommended)", the README's own language line, with the note "You are using
  "zh-hans" right? making sure" — yes: `zh-Hans`, the tag of the app's catalog (`zh-Hans.json`) and of the Store's
  `zh-hans` listing.
- **What the update dialog opens in:** "The app's language (Recommended)": a Chinese interface opens a release's
  Chinese notes, any other its English; a click on the line switches in place.
- **The Store:** "Yes, in 0.2.12": the `zh-hans` listing's "What's new" comes from the Chinese notes.

**Built** (2026-11-21; `CLAUDE.md` §2.67 is the summary). Where it differs from §4:

- **The language line is the switch, also in the dialog** (§4.4 proposed leaving it out). The body's first row is
  `**ENG** / [简体中文](…/blob/v<version>/docs/release-notes/<version>/zh-Hans.md)`; the hidden block holds the
  translation with its own first row, `[ENG](…/releases/tag/v<version>) / **简体中文**`. Installed copies (0.2.6–0.2.11)
  and the release page draw the English line as links, so their readers open the Chinese page in the browser. From 0.2.12
  on the dialog swaps the notes in place, for every release it shows, as long as the app runs; *View on GitHub* then opens
  the Chinese page. `Core/Updates/ReleaseNotesLanguages` splits a body exactly where the parser hides a comment (fences
  first); `UpdateView` draws the chosen language.
- **A file reads like its page** (§4.2): the language line first, the stamp under it (`<!-- source sha256:<hex> -->`,
  hidden on GitHub), then the notes and the translated footer after a `---` line. One line per paragraph: GitHub's file
  pages join wrapped lines with a space, which Chinese text must not get.
- **One composer, `tools/release/release_notes.py`** (§4.3, §4.7): `stamp` prints a file's stamp line for a draft, `check`
  runs every test the release job runs, `compose` writes the body, the English notes and each translation's notes for
  the Store. It refuses a stale stamp, another language line, `-->`, `--!>` or `<!--` in a file, a missing rule or footer
  link, a right-to-left language, a file of no catalog's name, English that leaves a code fence or an HTML comment open,
  and a body over GitHub's limit. `release.yml` composes before anything is built; `store.yml` composes when the tag has
  the script. It does not compare a translation's structure with the English (§4.7's first point); the owner reads it.
- **Hebrew is refused** until the dialog draws right-to-left notes (§4.2's `<div dir="rtl">` way is not built). Russian
  and Spanish files would work today: their catalogs exist.
- **The Store** (§4.5): `submit-store.ps1 -TranslatedNotesDirectory` gives each listing with a translation its own "What's
  new", shortened by `ConvertTo-StoreNotes -Language`: a sentence also ends at `。`, `！` and `？`, "修复" is the
  Chinese lead-in of the fixes and its count reads "共 N 项。". Listings without a translation are emptied, as before. For
  0.2.12: 1,440 characters in English, 602 in Chinese.
- **Not built:** a line saying that a release is shown in English for want of a translation (an older release in a
  Chinese dialog is simply English); the README and its translations do not mention the feature (the owner's rule since
  2026-11-20: README changes only when asked).
- **Proven** with this report's probe on the composed 0.2.12 body: the block is hidden in v0.2.6–v0.2.11's dialogs, at
  HEAD and on GitHub; and live, in a claude-desktops Windows desktop with a stand-in feed (`CLAUDE.md` §5).

## 6. What would change where

| Area | Change |
|---|---|
| `Core/Updates` or `Core/Presentation` | The block splitter; the choice of a release's notes by language |
| `App/Views/UpdateView.xaml(.cs)`, `MarkdownRenderer.cs` | The language switch (§5), the "in English" line, right-to-left blocks |
| `.github/workflows/release.yml`, `store.yml` | One composing script instead of two notes steps |
| `tools/release/submit-store.ps1` (+ its tests) | Per-language "What's new", language-neutral shortening |
| `tools/release/` | The composer and the checker |
| `docs/release-notes/<version>/` | The translations, from the first release that has them |
| `CLAUDE.md` §2.29, §3.1, §3.5; README *Updates*; the four README translations | What the dialog shows; the release procedure; the Store step |
| Catalogs | The new dialog texts in every language |

## 7. How to reproduce

```bash
python tools/probes/probe_release_notes.py                       # the built-in sample: hidden everywhere (exit 0)
python tools/probes/probe_release_notes.py --sample leak --github # "-->" and "--!>" inside a translation (exit 1)
python tools/probes/probe_release_notes.py --sample details --show --github  # what <details> sections do
python tools/probes/probe_release_notes.py BODY.md --github       # a real or composed body
python tools/release/release_notes.py stamp NOTES.md              # the stamp line for a translation of these notes
python tools/release/release_notes.py check --version 0.2.12 NOTES.md   # every check the release job runs
python tools/release/release_notes.py compose --version 0.2.12 --notes NOTES.md --body BODY.md --english EN.md
```

The probe needs `git` and the .NET 10 SDK, and builds in a temporary folder that it deletes. With `--github` it makes
two requests to GitHub's `/markdown` per body (60 an hour without a token; it uses the GitHub CLI's stored sign-in when
there is one) and publishes nothing.

## 8. Sources

- GitHub's release body limit, 125,000 characters: the 422 answer "body is too long (maximum is 125000 characters)"
  quoted in [helm/chart-releaser#676](https://github.com/helm/chart-releaser/pull/676),
  [changesets/action#304](https://github.com/changesets/action/issues/304) and
  [cli/cli#7815](https://github.com/cli/cli/issues/7815).
- GitHub's renderer: `POST /markdown` (REST API), modes `gfm` and `markdown`.
- CommonMark's HTML blocks: a block that starts with `<!--` ends at the first line containing `-->` (type 2), blank lines
  included.
- Microsoft Store: "What's new in this version", at most 1,500 characters per listing language (Partner Center; also
  `submit-store.ps1`).
