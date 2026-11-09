# Localization: how BetterClipboard speaks more than one language

BetterClipboard ships every interface language inside one build: there is no "Chinese build", no language pack to
download, and no language file next to the program that a copy could lose. This page is for anyone who adds a
language, fixes a translation, or adds a new piece of UI text.

The first version came from [pull request #1](https://github.com/Nucs/BetterClipboard/pull/1) by Johnson-Tang2019
(Simplified Chinese); the language picker, the flags, the English-only rules for logs and `bclip`, the English-text
keys, plurals and partial languages came after it. Russian, Spanish and Hebrew, a description for translators on every
English row, and right-to-left text in the left-to-right windows came on 2026-11-09.

## What the user sees

- **A start asks until a language is chosen.** On the app's first start, and on the first start after an update from a
  version that never asked, a small window lists every offered language as *own name · flag · English name*
  (`简体中文 · 🇨🇳 · Chinese (Simplified)`), with the one that fits Windows' display language selected. The heading
  switches to the language under the selection. A green check (or `Enter`, `Esc`, a double click, or closing the
  window) keeps the selection. The picker runs before any other window exists, so the whole app opens in that language
  at once.
- **Settings › App › Language** changes it later: *Use Windows setting* (with the flag and name of the language it picks
  now) or any offered language, each with its flag. The closed box shows the language in effect with its flag, and a
  dimmed "· Windows setting" after it while BetterClipboard follows Windows ("🇺🇸 English · Windows setting",
  "🇨🇳 简体中文"). The choice is saved at once; a **Restart now** button appears next to the list while
  the chosen language differs from the one on screen, and restarts BetterClipboard (the new process waits for the old
  one, then opens Settings in the new language). Windows read their labels when they are built, so a restart is what
  applies a language everywhere at once.
- **Who is asked:** every start until the language question is answered. The answer is `AppSettings.LanguageChosen`,
  which the picker sets whichever way it closes, and so does a change in *Settings › App › Language* (*Use Windows
  setting* included). Since 2026-11-09:
  - a first start is asked, also when the installer wrote `settings.json` before it (`-Hotkey`, `-NoUpdateCheck`):
    such a file names no language;
  - so is the first start after an update from a version that never asked: 0.2.9 and older, which have no languages,
    and the development builds of 2026-11-08 and 2026-11-09, which asked only in a data folder without a history;
  - a start whose settings name a language is never asked: an administrator's pre-seeded `settings.json`, and every
    user who answered the earlier builds' picker (it saved an explicit language). To pre-seed "follow Windows"
    without the picker, write `"InterfaceLanguage": "system"` with `"LanguageChosen": true`;
  - an exit from the tray while the picker is open saves nothing, so the next start asks again;
  - a user who goes back to 0.2.9 loses the answer (0.2.9 drops the members it does not know when it saves its
    settings) and is asked once more after updating again.

  `LanguageChoice.ShouldAsk` is the rule; the history store only words the log line ("Interface language chosen at
  the first start" or "… at a start with a history already kept").
- **A language may be partly translated.** Every string a catalog lacks shows in English. A language that lacks more
  than one string in ten is not offered at all (not in the picker, not in Settings, not picked for Windows' language),
  so a half-done translation can be merged without putting half-English screens in front of people. No percentage is
  ever shown.

## Where the text lives

```text
src/BetterClipboard.Core/Localization/
├── en.json            the English catalog: the list of every UI string, each row with its description
├── zh-Hans.json       one catalog per language, named by its tag
├── ru.json, es.json   (Russian, Spanish)
├── he.json            (Hebrew: "direction": "rtl")
├── Flags/us.svg       the flags the catalogs name (drawn by tools/make_flags.py)
├── Flags/cn.svg, ru.svg, es.svg, il.svg
├── UiText.cs          finds the catalogs, picks the language, looks strings up, words plurals, embeds right-to-left text
├── UiLanguage.cs      one language: names, flag, plural rules, strings
├── PluralRules.cs     CLDR's plural rules: which form a count takes
└── LanguageChoice.cs  when a start asks, and what it suggests
```

All of `Localization/*.json` and `Localization/Flags/*.svg` are embedded resources of `BetterClipboard.Core.dll`
(see `BetterClipboard.Core.csproj`). `UiText` finds every catalog by its resource name when it is first used.

A catalog is one JSON object:

```json
{
  "$language": {
    "name": "简体中文",
    "englishName": "Chinese (Simplified)",
    "flag": "cn",
    "alsoFor": [ "zh" ],
    "plurals": { "other": "@integer 0~15, 100, 1000, … @decimal 0.0~1.5, …" }
  },
  "$review": [ "Restart now" ],
  "Choose your language": "选择语言",
  "{0:N0} items#other": "{0:N0} 项",
  "…": "…"
}
```

- **`$language`** is the header: the language's own name, its English name, its flag (the lowercase two-letter country
  code that names `Flags/{flag}.svg`), optionally `alsoFor` (Windows display-language names that pick this catalog by
  their exact name: `zh` for `zh-Hans`), optionally `direction` (`"rtl"` for a language written right to left, below;
  `"ltr"` or nothing for every other, and any other value makes the app leave the catalog out), and `plurals` (below).
  Other `$` properties are reserved and skipped.
- **`$review`** lists translations whose English changed since they were written (`catalog.py rename` adds them, a
  reviewer clears them with `catalog.py reviewed`). The app uses them meanwhile; the list is for people.
- **Every other property is one UI string, keyed by its English text.** `en.json` maps each text to itself: it is the
  list of every string, which the tests check against the code and which says how much of each language is translated.
- **Plural strings** have one key per form: the English *other* form, `#`, and the CLDR category
  (`{0:N0} items#one`, `{0:N0} items#few`). `en.json` holds English's `#one` and `#other`; each other catalog holds the
  forms its own `plurals` name, always including `#other`.
- **Hash keys** (`T` plus the first 12 hex digits of the SHA-256 of the English text) are allowed for a text better
  kept out of the code: one XAML cannot quote (it holds both kinds of quote mark, a backslash or a brace) or a very long
  one. `en.json` maps the hash key to the English text.

## Descriptions for translators

Every row of `en.json` ends with a `//` comment: the string's description, written from the code that shows it.

```json
  "Add to {0}": "Add to {0}", // Card menu: adds the card to a group. {0} = the group's name (the user's own words).
```

- **What a description says:** where the string shows (a menu item, a Settings card's status line, a tooltip), what
  each `{0}` holds and in which language it arrives (a name stays as the user wrote it; a status may be Windows'
  English), how much room there is (a placeholder, a button, a tab label), and which words to keep (product names,
  key names). A string that is only part of a sentence says which sentence and what comes before or after it.
- **A plural string is described once:** its `#one` row carries the description (plus "This row: the form for a count
  of 1."), its `#other` row a fixed note. A translation's own forms (`#few`, `#many`) need no description.
- **Only `en.json` carries descriptions;** the translations are plain. The loader skips comments
  (`UiText.CatalogParseOptions`), so the app never reads them.
- **Kept complete:** `catalog.py check` fails on a row without a description, and so does
  `LocalizationTests.EnglishCatalog_EveryRowHasItsDescription` (a comment on a line of its own, which the tool's layout
  would drop, fails it too). `catalog.py add`, `add-file` and `add-plural` take the description with the string,
  `describe` changes one, `rename` and `remove` carry it along, and `missing` prints each missing string with it.

## Right-to-left languages in left-to-right windows

Hebrew's catalog says `"direction": "rtl"`. The windows keep their left-to-right layout in every language: the owner's
choice, confirmed 2026-11-09 ("Keep left-to-right"). The text carries its direction instead.

- **Why the text needs it:** a left-to-right paragraph lays its runs out from the left. A Hebrew sentence with an English
  name in the middle ("… ב-Claude Code …") would show its Hebrew stretches in the wrong order around the name, a
  sentence that starts with a number ("5 פריטים") would read "items 5", and its final period would land at its start.
- **What the app does:** when it loads a right-to-left catalog, `UiText` wraps each translation that holds right-to-left
  text in a right-to-left embedding (U+202B before it, U+202C after it, both invisible). Inside, the sentence is
  ordered right to left as a whole, with English names and numbers in their places, while it stays aligned left like
  everything else. Every route to a string gets the wrapped text: XAML's labels, `Translate`, `Format`, `Plural`,
  `TranslateFormatted`.
- **What a translator does: write the text plainly,** with no direction marks. `catalog.py check` refuses embedding,
  override and isolate characters in any catalog. The one exception is a template with no letter of its own, such as
  `"{0} ({1})"`: a leading right-to-left mark (U+200F, which `catalog.py` writes as `\u200f`) asks for the embedding,
  so the phrase and its parenthesis are ordered right to left as one line.
- **Values without right-to-left text stay as they are:** the list separator `", "` (embedded, it would reorder English
  items in a list), the date patterns, PowerShell's name with its host.
- **Lines the code joins from pieces** (a card's caption "Notepad · 5 min ago · 3 lines", two sentences of the update
  dialog) go through `UiText.JoinLine(" · ", parts)`: a plain join, except in a right-to-left language, where the whole
  line is embedded so it reads from its first piece, at the right. A list that goes into a template (`"In {0}"`) needs
  nothing more: the template's own embedding orders it.
- **Footgun: code that looks at a translation's first or last character sees the marks.** `UpdateText.AsSentence`
  skips them and puts its period inside the embedding; anything like it must too. A group's default name is its icon's
  translated name, which comes embedded: the store drops direction controls from every group name (`ClipStore`), so a
  name never turns the direction of the text it is set into.
- **What stays left to right:** the layout (the tab strip's order, icons, buttons), the user's own copies (a copy is
  shown as it is: one that starts with Latin letters reads left to right), and text a paragraph of several lines
  shows aligned left, so its lines start ragged.
- **Hebrew prefixes before a name:** a template that sets a name or a product after a prefix letter writes it with a
  hyphen (`ב-{0}`, `ל-{0}`), the usual form before a name that may be English or Hebrew.

## How a string is looked up

- **XAML** names the English text: `Text="{loc:Tr Text='Choose your language'}"`, or with an apostrophe in the text
  `Description="{loc:Tr Text=&quot;Windows' own clipboard history&quot;}"` — WinUI's XAML compiler takes no backslash
  escapes inside a markup extension's quoted value. A hash key is `{loc:Tr Key=T…}`
  (`App/Localization/TrExtension.cs`). Resolved once, when the window is built.
- **C#** names the English text: `UiText.Translate("Choose your language")`, or for a sentence with values
  `UiText.Format($"Add to {group.Name}")`. The sentence is translated before the values go in, so a group name, a path
  or a command is never translated.
- **Counts:** `UiText.Plural(n, $"{n:N0} item", $"{n:N0} items")`. English's own rule picks between the two forms in
  the code; another language's catalog gives the form its rules pick (Russian has four, Chinese one). Both forms must
  use the same values in the same order (the one form may leave some out: `$"opened once"`). Two counts in one sentence
  are two plurals: word the inner one first and pass it in as a value.
- **The language:** an explicit choice in the settings (`InterfaceLanguage`), else the offered catalog that fits Windows'
  display language: its own tag, a name in `alsoFor`, or the nearest parent culture with a catalog (`zh-SG` →
  `zh-Hans`, `en-GB` → `en`). Traditional Chinese (`zh-TW`, `zh-HK`) gets English, not Simplified Chinese.
- **Fallback:** a string, or a plural form, a catalog lacks shows in English.
- **Whole phrases, not pieces.** A count is a plural string (`UiText.Plural(n, $"{n:N0} file", $"{n:N0} files")`), and a
  sentence that differs for one and for several is a plural string too. Never put a translated template around an
  English word passed in as a value: `$"{names} {isAre} released"` showed "is" inside Chinese text. Lists are joined
  with the catalog's `", "` (Chinese `、`) and `"{0} and {1}"`.
- **Quotes around an inserted title.** A translated title can carry quote marks of its own (Russian «Ножницы» и
  Win+PrtScn), so a template that puts quotes around `{0}` nests them: the Delete stored dialog once read
  "Также выключить ««Ножницы» и Win+PrtScn»". Where a value may be such a title, use a colon instead
  ("Также выключить: {0}"); the description says when.
- **Text a component words in English for the log, shown translated.** Some reasons and statuses are made once, in
  English, by code that also logs them or prints them for the installer: why a shortcut cannot be used
  (`HotkeyGesture.ProblemTemplates`), a shortcut's state, why Everything was not trusted or how it and ShareX were found,
  why an update failed (`UpdateMessages`), why a Win+R command did not run. The interface translates the finished
  English back through its template list: `UiText.TranslateFormatted(text, templates)` finds the template whose shape
  matches, translates it, and carries the matched parts over unchanged (a part is translated only when it is itself in
  the list, like "the release list"). Each list is the component's contract with the catalogs.
- **Names BetterClipboard makes up for a copy's source** ("Command Prompt", "Snipping Tool", "Screenshots folder") are
  stored in English, because the tabs filter by them, and translated where they are shown (`SourceNames.Display`).
- **Dates** use the interface language's day and month names (`UiText.DateCulture`): an English Windows with the
  Chinese interface shows "周四 14:02", not "Thu 14:02". The two date patterns, `d MMM` and `d MMM yyyy`, are catalog
  entries: Chinese writes `M月d日` and `yyyy年M月d日`.
- **WinUI's own text** (a text box's Cut, Copy and Paste) comes from WinUI's `.mui` files, which Windows picks by the
  process's preferred UI languages. The app sets them to the interface language at start, before any window is built
  (`ProcessLanguages`, plus the Windows App SDK's `PrimaryLanguageOverride`), so they follow it too.

**Footgun:** a key is the English text, so editing the English in the code makes a new key, and every translation of
the old text is lost. The tests fail until `en.json` follows (below); use `catalog.py rename`, which moves the string in
every catalog and XAML file and marks each translation for review, then change the C#.

## Plural rules

Each catalog's header carries its language's cardinal plural rules in CLDR's own syntax, copied from the
[CLDR plural rules chart](https://www.unicode.org/cldr/charts/latest/supplemental/language_plural_rules.html)
(Unicode CLDR data, Unicode License v3), samples included:

```json
"plurals": {
  "one": "i = 1 and v = 0 @integer 1",
  "other": "@integer 0, 2~16, 100, 1000, 10000, 100000, 1000000, … @decimal 0.0~1.5, …"
}
```

- Every category except `other` has a condition; `other` takes none (it is whatever no rule takes) but may carry its
  samples. A language with only `other` (Chinese, Japanese, Korean) writes `"plurals": { "other": "@integer …" }` or
  `{}`; the header must still have `plurals`.
- `UiText` evaluates the conditions for whole counts (`n` and `i` are the count, the decimal operands are 0).
- The tests check that every `@integer` sample lands in its category, which catches a rule copied wrong.

## Never translated

- **Logs.** The developer reads them. Never pass `UiText` output to `AppLog`; when a text is both logged and shown, build
  it twice (`LocalizationTests.NoLogLineIsBuiltFromTranslatedText` checks the sources).
- **`bclip`'s output.** Scripts and AI agents parse it. `bclip` pins English at its start, and the CLI's tables ask
  `RelativeTimeFormatter` for English (`localized: false`).
- **What the user copied, names they gave** (groups), file names, paths and commands, and identifiers.

## The catalog tool

```bash
python tools/localization/catalog.py add "Choose your language" --description "First-start picker: heading." zh-Hans="选择语言"
python tools/localization/catalog.py add-plural "{0:N0} item" "{0:N0} items" -d "Footer: how many items." zh-Hans#other="{0:N0} 项"
python tools/localization/catalog.py describe "Choose your language" "First-start picker: heading over the languages."
python tools/localization/catalog.py rename "Old English text" "New English text"
python tools/localization/catalog.py remove "English text"
python tools/localization/catalog.py missing zh-Hans
python tools/localization/catalog.py reviewed zh-Hans "Restart now"      # or --all
python tools/localization/catalog.py check
python tools/localization/catalog.py migrate
```

- `add` puts a string in `en.json` with its description (`--description` or `-d`; without one it warns, and `check`
  fails until `describe` gives one) and the translations named (`add --hash` keeps it under a hash key and prints it);
  `add-file strings.json` adds many from a JSON object `{"English": {"$description": "…", "zh-Hans": "中文"}}` (easier
  than quoting apostrophes and Chinese on a command line).
- `add-plural` puts English's one and other form in `en.json`, and any language's forms as `TAG#CATEGORY="…"`.
- `describe` sets or replaces a string's description (a plural string by its other form).
- `rename` changes a string's or a plural string's English everywhere (catalogs and XAML), keeping each translation and
  its description, and marking each translation for review. `remove` deletes a string no code uses any more (refused
  while XAML still names it), a plural string with every form any language gives it (Russian's `#few` and `#many`,
  Hebrew's `#two`; until 2026-11-09 it left those behind).
- `missing` is a translator's to-do list: the strings and forms a language lacks, each with its description, and its
  review marks.
- `check` reports broken entries (exit 1): keys that are not their text, forms a language does not have, values English
  does not pass, review marks of nothing, `en.json` rows without a description, embedding or isolate characters in any
  catalog, a `direction` other than `ltr` or `rtl`. Missing strings are counted, not errors.
- `migrate` turns hash keys into English-text keys in the catalogs and XAML. It is for merging work written with the
  hash keys of the first version; running it again changes nothing.

Files keep their layout: `$language`, then `$review`, then the keys in ordinal order, two-space indent, UTF-8, LF and a
final newline (what `json.dumps(indent=2, ensure_ascii=False)` writes), with two additions: a row's description follows
its comma as `// text`, and invisible format characters (a right-to-left mark) are written as `\uXXXX` escapes, so a
reviewer can see them.

**The tests keep the catalogs and the code in step** (`dotnet test --solution BetterClipboard.sln`):

- `LocalizationTests.CodeAndCatalogsAgree`: every XAML `Text=` and `Key=`, every literal handed to `UiText.Translate` /
  `Format`, and every `UiText.Plural` pair is in `en.json` as written, and every plain entry is still used.
- `TranslationCoverageTests.EveryTextTheCodeTranslatesHasAnEntry`: also every constant handed to `Translate` /
  `Format`. Before these tests, 213 texts showed in English in the Chinese interface without anything failing.
- `TranslationCoverageTests.EveryEntryIsStillUsed`, `CoreTemplateLists_HaveEntries` and `TranslationTemplateTests`
  (Windows): every entry is still used, every template list is in the catalogs, and every text those components make
  matches one of their templates.
- `LocalizationTests` and `PluralRulesTests`: every catalog well formed (keys, plural forms, placeholders), each
  language's plural rules and their samples, every `en.json` row described, and how many strings each language lacks
  (printed, never a failure).
- `RightToLeftTextTests`: every Hebrew string embedded exactly once (or one of the few without Hebrew), values kept
  inside the embedding, English fallbacks and the other languages untouched, `JoinLine`, the header's `direction`.

## Adding a language

1. Create `{tag}.json` (the tag is what the settings store: `zh-Hant`, `pt-BR`, `ar`) with its `$language` header: own
   name, English name, flag code, `plurals` copied from CLDR's chart; `alsoFor` only for a bare name Windows uses that is
   not a parent of the tag; `"direction": "rtl"` for a language written right to left (Arabic, Persian, Urdu).
2. Translate strings: `catalog.py missing {tag}` lists them, each with its description. Keep every `{0}`-style
   placeholder that carries content; a translation may leave a value out, never add one. Give every plural string each
   form its rules name. Mind the room a description names (placeholders, buttons, tabs): the search box shows about
   220 pixels of text, and Russian ran past it before it was shortened. Four kinds of string need care:
   - `d MMM` and `d MMM yyyy` are .NET custom date patterns, not words: write the language's own order (`M月d日`).
   - The group icons' names (`Star`, `Work`, …) become a new group's default name: keep them distinct (a test checks).
   - A string that starts with a space continues a sentence; drop the space where the language writes none.
   - A language that inflects nouns (Russian) cannot always set a translated word into another template in the right
     case: word such a template so the inserted piece stands in its basic form ("в группах: 4", not "в 4 группах").
3. When no shipped language uses its flag yet, draw the flag in `tools/make_flags.py` (3:2, no `<use>`, `<text>` or CSS)
   and run it.
4. Run `dotnet test --solution BetterClipboard.sln`: `LocalizationTests` check the header, the flag, the plural rules
   and their samples, every key and every placeholder, and print how many strings each language lacks.

Nothing else: once at most one string in ten is missing, the first-start picker and *Settings › App › Language* list
the new language by themselves. Until then it ships but is not offered, and the app's log says how many strings it
lacks.

## English on purpose

Some words stay English in every language:

- **Names:** BetterClipboard, Everything, ShareX, PowerShell, Claude Code, Codex, Win+R, Win+PrtScn, and the short tab
  names Pwsh, Cmd, Claude and Codex.
- **Keys and shortcuts** (`Ctrl+Shift+V`, `PageDown`, `Enter` in a menu), as Windows itself writes them, and the search
  box's `Aa`, `W` and `.*`.
- **Text from outside the app,** shown as it came: Windows' own error messages, .NET's reason for a regular expression
  that does not parse ("Not enough )'s."), the network stack's reason for a failed update, and the installer's report
  (a PowerShell script's English).
- **What is stored:** a copy's text, a file list's preview, group names, and the source names of other apps.

## Known gaps

- Right-to-left layout is not built, by decision (2026-11-08, confirmed 2026-11-09): the windows keep their
  left-to-right layout, and right-to-left text is embedded so it reads in its own order (above). Its paragraphs are
  aligned left.
- Russian, Spanish and Hebrew were translated by Claude from the code and the descriptions (2026-11-09), with no
  native speaker's review yet; corrections are welcome.
- The footer's key hint is cut at its end where a long status shares its line (Russian in the Cmd tab: "Del скр…"),
  as it is in English at narrow widths.
