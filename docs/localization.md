# Localization: how BetterClipboard speaks more than one language

BetterClipboard ships every interface language inside one build: there is no "Chinese build", no language pack to
download, and no language file next to the program that a copy could lose. This page is for anyone who adds a
language, fixes a translation, or adds a new piece of UI text.

The first version came from [pull request #1](https://github.com/Nucs/BetterClipboard/pull/1) by Johnson-Tang2019
(Simplified Chinese); the language picker, the flags and the English-only rules for logs and `bclip` came after it.

## What the user sees

- **The first start asks.** On the app's very first start in a data folder, a small window lists every language as
  *own name · flag · English name* (`简体中文 · 🇨🇳 · Chinese (Simplified)`), with the one that fits Windows' display
  language selected. The heading switches to the language under the selection. A green check (or `Enter`, `Esc`, a
  double click, or closing the window) keeps the selection. The picker runs before any other window exists, so the
  whole app opens in that language at once.
- **Settings › App › Language** changes it later: *Use Windows setting* (with the language it picks now) or any shipped
  language, each with its flag. A change applies at the next start, because windows read their labels when they are
  built.
- **Who is asked:** only a first start: the data folder holds no history yet. Updating from a version without
  languages never asks (a history exists), and neither does a start whose settings already name a language (an
  administrator's pre-seeded `settings.json`). The installer may write `settings.json` before the first start (with
  `-Hotkey` or `-NoUpdateCheck`), so the settings file is not the signal; the history store is.

## Where the text lives

```text
src/BetterClipboard.Core/Localization/
├── en.json            the English catalog: the source of every string
├── zh-Hans.json       one catalog per language, named by its tag
├── Flags/us.svg       the flags the catalogs name (drawn by tools/make_flags.py)
├── Flags/cn.svg
├── UiText.cs          finds the catalogs, picks the language, looks strings up
├── UiLanguage.cs      one language: names, flag, strings
└── LanguageChoice.cs  when the first start asks, and what it suggests
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
    "alsoFor": [ "zh" ]
  },
  "T8e2d0664dbbb": "选择语言",
  "…": "…"
}
```

- `$language` is the header: the language's own name, its English name, its flag (the lowercase two-letter country
  code that names `Flags/{flag}.svg`), and optionally `alsoFor`, Windows display-language names that pick this catalog
  by their exact name (`zh` for `zh-Hans`). Other `$` properties are reserved and skipped.
- Every other property is one UI string. Its key is `T` plus the first 12 hex digits of the SHA-256 of its English text.

## How a string is looked up

- **XAML** names the key: `Text="{loc:Tr Key=T8e2d0664dbbb}"` (`App/Localization/TrExtension.cs`). Resolved once, when
  the window is built.
- **C#** names the English text: `UiText.Translate("Choose your language")`, or for a sentence with values
  `UiText.Format($"Add to {group.Name}")`. `UiText` maps the English text back to its key. The sentence is translated
  before the values go in, so a group name, a path or a command is never translated.
- **The language:** an explicit choice in the settings (`InterfaceLanguage`), else the catalog that fits Windows'
  display language: its own tag, a name in `alsoFor`, or the nearest parent culture with a catalog (`zh-SG` →
  `zh-Hans`, `en-GB` → `en`). Traditional Chinese (`zh-TW`, `zh-HK`) gets English, not Simplified Chinese.
- **Fallback:** a string a catalog lacks shows in English; a key no catalog has shows as the key (the tests catch it).

**Footgun:** changing an English text in C# without updating the catalogs breaks its key: the string then shows in
English in every language. Change English texts with `catalog.py rename` (below), which moves the key in every catalog
and XAML file and keeps the translations for review; then change the C# literal.

## Never translated

- **Logs.** The developer reads them. Never pass `UiText` output to `AppLog`; when a text is both logged and shown, build
  it twice (`LocalizationTests.NoLogLineIsBuiltFromTranslatedText` checks the sources).
- **`bclip`'s output.** Scripts and AI agents parse it. `bclip` pins English at its start, and the CLI's tables ask
  `RelativeTimeFormatter` for English (`localized: false`).
- **What the user copied, names they gave** (groups), file names, paths and commands, and identifiers.

## Adding a string

```bash
python tools/localization/catalog.py add "Choose your language" zh-Hans="选择语言"
```

It adds the string to every catalog with its key and prints the key (for XAML). A catalog not named gets the English
text until someone translates it. `python tools/localization/catalog.py key "English text"` prints a key;
`python tools/localization/catalog.py rename "Old text" "New text"` changes a string's English wording everywhere;
`python tools/localization/catalog.py check` reports keys that do not match their English text and catalogs that miss
keys.

## Adding a language

1. Copy `en.json` to `{tag}.json` (the tag is what the settings store: `he`, `ja`, `zh-Hant`, `pt-BR`).
2. Fill its `$language` header: own name, English name, flag code; `alsoFor` only for a bare name Windows uses that is
   not a parent of the tag.
3. Translate every string. Keep every `{0}`-style placeholder that carries content; English plural endings passed as
   separate values (`{1}` = "s") may be left out where the language has no such endings, never added.
4. When no shipped language uses its flag yet, draw the flag in `tools/make_flags.py` (3:2, no `<use>`, `<text>` or CSS)
   and run it.
5. Run `dotnet test --solution BetterClipboard.sln`: `LocalizationTests` check the header, the flag, every key and every
   placeholder.

Nothing else: the first-start picker and *Settings › App › Language* list the new language by themselves.

**Not built yet:** right-to-left layout. A Hebrew or Arabic catalog would show its words, but the windows would still
lay out left to right.

## Known gaps

Some UI text is still English in the Chinese interface: about 40 translated strings are not looked up by the code
yet (the search box's placeholder, the footer's key hints, a few status lines), a few sentences carry English words
inside a Chinese template, and WinUI's own control text (a text box's Cut/Copy/Paste menu) follows Windows' language.
The list is in CLAUDE.md's roadmap (§6).
