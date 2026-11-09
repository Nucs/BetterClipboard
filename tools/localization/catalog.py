"""Edit BetterClipboard's UI string catalogs (src/BetterClipboard.Core/Localization/*.json) without breaking them.

Keys are the English text. XAML names it ({loc:Tr Text='Paste'}), C# passes it (UiText.Translate("Paste"),
UiText.Format($"Add to {name}"), UiText.Plural(n, $"{n:N0} item", $"{n:N0} items")), and each catalog maps it to its
translation. en.json maps every text to itself: it is the list of every UI string, which the tests check against the
code and which says how much of each language is translated. A plural string has one key per form, its English other
form + "#" + the CLDR category ("{0:N0} items#one"); each language gives the forms its header's "plurals" rules name.
A few texts may sit under a hash key instead, "T" + the first 12 hex digits of the SHA-256 of the English text (XAML:
{loc:Tr Key=T...}), for a text better kept out of the markup.

A language may lack strings (they show in English); one that lacks more than one string in ten is not offered in the
app. When an English text changes, rename moves its translations and lists them in the catalog's "$review" until a
reviewer confirms them with reviewed.

Every row of en.json ends with a // comment: the string's description for translators (where it shows, what each
{0} holds, how long it may be, words to keep). "check" fails while a row has none; add, add-file and add-plural take
the description with the string, describe changes one, and rename and remove carry it along. A plural string is
described once: its #one row carries the description, its #other row a fixed note.

Commands:
  python tools/localization/catalog.py add "English text" [--description "Where it shows…"] [zh-Hans="中文"]...
      Adds a string to en.json, and a translation to each catalog named.
  python tools/localization/catalog.py add --hash "English text" [--description "…"] [zh-Hans="中文"]...
      The same under a hash key, which it prints (for {loc:Tr Key=...}).
  python tools/localization/catalog.py add-file strings.json
      Adds many strings at once from a UTF-8 JSON object {"English text": {"$description": "…", "zh-Hans": "中文"}, ...}:
      the same rules as add for each one. Quoting many strings with apostrophes or Chinese on a command line is
      error-prone; a file is not.
  python tools/localization/catalog.py add-plural "{0:N0} item" "{0:N0} items" [--description "…"] [zh-Hans#other="{0:N0} 项"]...
      Adds a plural string: English's one and other forms, and any language's forms as TAG#CATEGORY="text".
  python tools/localization/catalog.py describe "English text" "Where it shows…"
      Sets (or replaces) a string's description; for a plural string, name its other form.
  python tools/localization/catalog.py rename "Old English text" "New English text"
      Changes a string's (or a plural string's) English text in every catalog and in XAML, keeping each translation
      and its description, and marking the translations for review. Change the C# literal by hand.
  python tools/localization/catalog.py remove "English text"
      Deletes a string (or a plural string, by its other form) from every catalog; refuses while XAML still names it.
  python tools/localization/catalog.py reviewed TAG "Key" | --all
      Confirms translations marked for review.
  python tools/localization/catalog.py missing TAG
      Lists the strings and plural forms a language lacks, each with its description, and those marked for review: a
      translator's to-do list.
  python tools/localization/catalog.py check
      Rewrites nothing. Reports broken entries and en.json rows without a description (exit 1) and, per language, how
      many strings it lacks or has marked for review (not errors). The tests check the code against the catalogs.
  python tools/localization/catalog.py migrate
      Converts hash keys to English-text keys in every catalog and every XAML file; running it again changes nothing.
      Use it after merging work written with the old hash keys.

A right-to-left language says "direction": "rtl" in its header (Hebrew does). The windows keep their left-to-right
layout, and the app embeds each of the language's translations that holds right-to-left text in a right-to-left run
when it loads the catalog (U+202B ... U+202C), so its sentences read in their own order: write the text plainly,
without direction marks. The one exception is a template with no letter of its own ("{0} ({1})"): a leading
right-to-left mark (U+200F) asks for the embedding, so the whole line is ordered right to left. check refuses
embedding, override and isolate controls in any catalog, and a direction other than "ltr" or "rtl".

Files keep their layout: "$language" first, then "$review", then the keys in ordinal order, two-space indent, UTF-8,
LF and a final newline (what json.dumps(indent=2, ensure_ascii=False) writes), with two additions: a row's description
follows its comma as "// text", and invisible format characters (category Cf, such as that right-to-left mark) are
written as \\uXXXX escapes, so a reviewer can see them.
"""

import hashlib
import json
import re
import string
import sys
import unicodedata
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
CATALOGS = ROOT / "src" / "BetterClipboard.Core" / "Localization"
SOURCES = ROOT / "src"
METADATA = "$language"
REVIEW = "$review"
# In memory only: a catalog's descriptions by key, written back as the // comment of each row (never as a property).
DESCRIPTIONS = "$descriptions"
# The #other row of a plural string carries this note; its #one row carries the string's description plus ONE_NOTE.
OTHER_NOTE = ("The same plural string's form for every other count; its text is the string's key. Each language gives "
              "the forms its plural rules name (#one, #few, #many, #other…).")
ONE_NOTE = " This row: the form for a count of 1."
CATEGORIES = ("zero", "one", "two", "few", "many", "other")
# Explicit direction controls: embeddings, overrides, isolates. The app wraps right-to-left text itself (UiText), so a
# catalog never holds them; check refuses them.
DIRECTION_CONTROLS = frozenset(chr(c) for c in (*range(0x202A, 0x202F), *range(0x2066, 0x206A)))
ENGLISH_FORMS = ("one", "other")
HASH_KEY = re.compile(r"T[0-9a-f]{12}")
TR_KEY = re.compile(r"\{loc:Tr Key=(T[0-9a-f]{12})\}")


def key_of(english: str) -> str:
    """The hash key of an English text (only for strings kept under a hash key).

    Args:
        english: The English text, exactly as the code and en.json hold it.

    Returns:
        "T" followed by the first 12 hex digits of the text's UTF-8 SHA-256.
    """
    return "T" + hashlib.sha256(english.encode("utf-8")).hexdigest()[:12]


def plural_parts(key: str):
    """Splits a plural form's key into its plural string and its category.

    Args:
        key: A catalog key.

    Returns:
        (English other form, category) when the key ends in "#" and a CLDR category, else None.
    """
    head, sep, tail = key.rpartition("#")
    return (head, tail) if sep and head and tail in CATEGORIES else None


def categories_of(catalog: dict) -> list[str]:
    """The plural categories a language uses, in CLDR's order, from its header's rules.

    Args:
        catalog: A parsed catalog.

    Returns:
        The categories its "plurals" names, plus "other", which every language has.
    """
    rules = catalog.get(METADATA, {}).get("plurals", {})
    return [c for c in CATEGORIES if c in rules or c == "other"]


def split_comment(line: str) -> tuple[str, str | None]:
    """Splits one line of a catalog into its JSON and its // comment.

    A "//" inside a string (a URL in a text) is part of the string: the scan follows the quotes. JSON strings never span
    lines, so a line-by-line scan is exact.

    Args:
        line: One line of a catalog file, without its line break.

    Returns:
        (the JSON part with trailing spaces removed, the comment's text trimmed), or (the line, None) without a comment.
    """
    in_string = False
    i = 0
    while i < len(line):
        c = line[i]
        if in_string:
            if c == "\\":
                i += 2
                continue
            if c == '"':
                in_string = False
        elif c == '"':
            in_string = True
        elif c == "/" and line[i + 1:i + 2] == "/":
            return line[:i].rstrip(), line[i + 2:].strip()
        i += 1
    return line, None


def parse(text: str, name: str) -> dict:
    """Parses a catalog file: JSON whose rows may end with a // comment (en.json's descriptions).

    Args:
        text: The file's text.
        name: The file's name, for messages.

    Returns:
        The catalog's properties, plus DESCRIPTIONS ({key: text}) when any row has a comment. Comments on lines that are
        not a top-level row (inside the header, or on a line of their own) are not kept: the canonical layout has none.

    Raises:
        ValueError: The text without its comments is not valid JSON (json.JSONDecodeError), or a top-level row's key
            cannot be read.
    """
    code_lines = []
    descriptions = {}
    decoder = json.JSONDecoder()
    for line in text.split("\n"):
        code, comment = split_comment(line)
        code_lines.append(code)
        # A top-level row starts with exactly two spaces and a quote; the header's own rows are indented deeper.
        if comment is not None and code.startswith('  "') and not code.startswith('   '):
            try:
                key, _ = decoder.raw_decode(code, 2)
            except json.JSONDecodeError as error:
                raise ValueError(f"{name}: a commented row's key cannot be read: {code[:60]!r}") from error
            descriptions[key] = comment
    catalog = json.loads("\n".join(code_lines))
    if descriptions:
        catalog[DESCRIPTIONS] = descriptions
    return catalog


def load() -> dict[str, dict]:
    """Reads every catalog.

    Returns:
        The parsed catalogs, keyed by tag (the file name without .json: "en", "zh-Hans"); each row's // comment is kept
        under DESCRIPTIONS.
    """
    return {path.stem: parse(path.read_text(encoding="utf-8"), path.name) for path in sorted(CATALOGS.glob("*.json"))}


def dump_string(text: str) -> str:
    """A JSON string literal as the catalogs write it: UTF-8 without \\u escapes, except for format characters.

    Format characters (Unicode category Cf: U+200E/U+200F direction marks, zero-width joiners, bidi controls) are
    invisible in every editor and diff; written as \\uXXXX they can be reviewed. JSON reads both forms the same.

    Args:
        text: The key or value.

    Returns:
        The literal, quotes included.
    """
    literal = json.dumps(text, ensure_ascii=False)
    return "".join(f"\\u{ord(c):04x}" if unicodedata.category(c) == "Cf" else c for c in literal)


def save(tag: str, catalog: dict) -> None:
    """Writes one catalog in the canonical layout: header, review marks, then the keys in ordinal order.

    Without descriptions and format characters the output is byte for byte what json.dumps(indent=2,
    ensure_ascii=False) writes; a row with a description gets " // text" after its comma.

    Args:
        tag: The catalog's tag, which names its file.
        catalog: Its strings by key, plus the "$language" header, the optional "$review" list and DESCRIPTIONS.

    Raises:
        ValueError: A description holds a line break (it would end the comment and break the file).
    """
    lines = ["{"]
    rows = []
    if METADATA in catalog:
        rows.append((METADATA, json.dumps(catalog[METADATA], ensure_ascii=False, indent=2), None))
    review = sorted(set(catalog.get(REVIEW, [])))
    if review:
        rows.append((REVIEW, json.dumps(review, ensure_ascii=False, indent=2), None))
    descriptions = catalog.get(DESCRIPTIONS, {})
    for name in sorted(k for k in catalog if not k.startswith("$")):
        rows.append((name, dump_string(catalog[name]), descriptions.get(name)))
    for index, (name, value, description) in enumerate(rows):
        # A nested value (the header, the review list) keeps json.dumps' own layout, indented one level more.
        value = value.replace("\n", "\n  ")
        row = f"  {dump_string(name)}: {value}{',' if index < len(rows) - 1 else ''}"
        if description:
            if "\n" in description or "\r" in description:
                raise ValueError(f"{tag}: the description of {name[:60]!r} holds a line break")
            row += f" // {description}"
        lines.append(row)
    lines.append("}")
    text = "\n".join(lines) + "\n"
    (CATALOGS / f"{tag}.json").write_bytes(text.encode("utf-8"))


def describe_plural(catalog: dict, family: str, description: str) -> None:
    """Describes a plural string in a catalog: its #one row gets the description plus ONE_NOTE, its #other row OTHER_NOTE.

    Args:
        catalog: The catalog (en.json), changed in place.
        family: The plural string's English other form (its key without "#category").
        description: What the string is, where it shows and what its values hold.
    """
    descriptions = catalog.setdefault(DESCRIPTIONS, {})
    descriptions[f"{family}#one"] = description.rstrip() + ONE_NOTE
    descriptions[f"{family}#other"] = OTHER_NOTE


def strings_of(catalog: dict) -> dict[str, str]:
    """A catalog's UI strings, without its "$" properties.

    Args:
        catalog: A parsed catalog.

    Returns:
        Its strings by key.
    """
    return {k: v for k, v in catalog.items() if not k.startswith("$")}


def placeholders(text: str) -> set[int]:
    """The argument indices a composite format string uses ("{0:N0} of {1}" -> {0, 1}).

    Args:
        text: A composite format string.

    Returns:
        The indices of its placeholders; doubled braces are literal and not counted.
    """
    found = set()
    for _, field, _, _ in string.Formatter().parse(text.replace("{{", "").replace("}}", "")):
        if field is None:
            continue
        index = field.split(",")[0]
        if index.isdigit():
            found.add(int(index))
    return found


def xaml_files():
    """Every XAML file of the app's sources.

    Yields:
        Each path under src, in sorted order. Build output is skipped, and so is a folder whose name ends in .xaml (a
        published app has one named Microsoft.UI.Xaml).
    """
    for path in sorted(SOURCES.rglob("*.xaml")):
        if "bin" not in path.parts and "obj" not in path.parts and path.is_file():
            yield path


def xaml_text(english: str) -> str | None:
    """The {loc:Tr Text=...} markup for an English text, XML-escaped.

    WinUI's XAML compiler takes no backslash escapes in a markup extension's quoted value, so a text with an apostrophe is
    quoted with XML-escaped double quotes (Text=&quot;...&quot;).

    Args:
        english: The English text.

    Returns:
        The markup, or None when no quoting can carry the text (both quote marks, a backslash or a brace): such a text
        keeps a hash key.
    """
    if "\\" in english or "{" in english or "}" in english or ("'" in english and '"' in english):
        return None
    value = (english.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")
             .replace("\n", "&#10;").replace("\r", "&#13;").replace("\t", "&#9;"))
    if "'" in english:
        return "{loc:Tr Text=&quot;" + value + "&quot;}"
    return "{loc:Tr Text='" + value.replace('"', "&quot;") + "'}"


def rewrite_xaml(replace) -> None:
    """Applies a text replacement to every XAML file, printing the files it changed.

    Args:
        replace: A function from a file's text to its new text.
    """
    for path in xaml_files():
        content = path.read_bytes().decode("utf-8")
        updated = replace(content)
        if updated != content:
            path.write_bytes(updated.encode("utf-8"))
            print(f"{path.relative_to(ROOT)}: updated")


def xaml_uses(english: str) -> list[Path]:
    """The XAML files that name a text, by its English text or by its hash key.

    Args:
        english: The English text.

    Returns:
        The files that would show a missing label if the string went away.
    """
    markup = xaml_text(english)
    key = f"Key={key_of(english)}}}"
    uses = []
    for path in xaml_files():
        text = path.read_bytes().decode("utf-8")
        if (markup and markup in text) or key in text:
            uses.append(path)
    return uses


def parse_translations(args: list[str], catalogs: dict, plural: bool) -> dict | None:
    """Reads TAG="text" (or, for a plural string, TAG#CATEGORY="text") command-line arguments.

    Args:
        args: The arguments.
        catalogs: The catalogs, to check the tags against.
        plural: Whether each tag carries a category.

    Returns:
        {tag: {category or "": text}}, or None (after a message) when an argument names no translation catalog.
    """
    result: dict[str, dict] = {}
    for arg in args:
        name, sep, text = arg.partition("=")
        tag, _, category = name.partition("#") if plural else (name, "", "")
        if not sep or tag not in catalogs or tag == "en" or (plural and category not in CATEGORIES):
            print(f"Not a translation: {arg!r} (catalogs: {', '.join(t for t in catalogs if t != 'en')})", file=sys.stderr)
            return None
        result.setdefault(tag, {})[category] = text
    return result


def take_description(args: list[str]) -> tuple[list[str], str | None, bool]:
    """Takes "--description TEXT" (or "-d TEXT") out of a command's arguments.

    Args:
        args: The command's arguments.

    Returns:
        (the other arguments in order, the description or None, whether the option was well formed: False when it ends
        the arguments without its text).
    """
    rest = []
    description = None
    i = 0
    while i < len(args):
        if args[i] in ("--description", "-d"):
            if i + 1 >= len(args):
                return rest, None, False
            description = args[i + 1]
            i += 2
            continue
        rest.append(args[i])
        i += 1
    return rest, description, True


def warn_undescribed(key: str) -> None:
    """Reminds that a new string still needs its description (check and the tests fail until it has one).

    Args:
        key: The string's key, or a plural string's other form.
    """
    print(f"note: {key[:60]!r} has no description yet; add one with: catalog.py describe {json.dumps(key, ensure_ascii=False)} \"…\"",
          file=sys.stderr)


def add_one(catalogs: dict, english: str, translations: dict[str, str], hashed: bool, description: str | None = None) -> int:
    """Adds one string to the loaded catalogs (the caller saves them).

    Args:
        catalogs: The loaded catalogs, changed in place.
        english: The English text.
        translations: A translation per catalog tag; a catalog not named lacks the string (it shows in English there).
        hashed: Whether to keep it under a hash key instead of its English text.
        description: The string's description for en.json's // comment; None leaves an existing one (and a new string
            undescribed, which check reports).

    Returns:
        0, or 2 when the text cannot be a key as it is or the description holds a line break.
    """
    key = key_of(english) if hashed else english
    if plural_parts(key) or key.startswith("$") or (not hashed and HASH_KEY.fullmatch(key)):
        print(f"{english!r} cannot be a key as it is (it looks like a plural form, a hash key or a header)", file=sys.stderr)
        return 2
    if description is not None and ("\n" in description or "\r" in description):
        print(f"{english!r}: a description is one line (it becomes a // comment)", file=sys.stderr)
        return 2
    for tag, catalog in catalogs.items():
        text = english if tag == "en" else translations.get(tag)
        if text is None:
            continue
        if catalog.get(key, text) != text:
            print(f"{tag}: {key!r} already holds another text; left as it is", file=sys.stderr)
            continue
        catalog[key] = text
    if description is not None:
        catalogs["en"].setdefault(DESCRIPTIONS, {})[key] = description.strip()
    elif key not in catalogs["en"].get(DESCRIPTIONS, {}):
        warn_undescribed(key)
    return 0


def add(args: list[str]) -> int:
    """The add command: add [--hash] "English" [--description "…"] [TAG="text"]...

    Args:
        args: The command's arguments.

    Returns:
        A process exit code: 0, or 2 for a usage error or a text that cannot be a key.
    """
    hashed = bool(args) and args[0] == "--hash"
    args = args[1:] if hashed else args
    args, description, well_formed = take_description(args)
    if not args or not well_formed:
        print(__doc__, file=sys.stderr)
        return 2
    english, catalogs = args[0], load()
    translations = parse_translations(args[1:], catalogs, plural=False)
    if translations is None:
        return 2
    code = add_one(catalogs, english, {tag: forms[""] for tag, forms in translations.items()}, hashed, description)
    if code == 0:
        for tag, catalog in catalogs.items():
            save(tag, catalog)
        print(key_of(english) if hashed else "added")
    return code


def add_file(path: str) -> int:
    """The add-file command: adds every string of a JSON file {"English": {"$description": "…", "tag": "translation"}}.

    Args:
        path: The UTF-8 JSON file. "$description" is optional per string (check reports strings left without one).

    Returns:
        A process exit code: 0, or 2 when the file's shape is wrong, a string names an unknown catalog, or a text cannot
        be a key (nothing is saved then).
    """
    entries = json.loads(Path(path).read_text(encoding="utf-8"))
    if not isinstance(entries, dict) or not all(isinstance(v, dict) for v in entries.values()):
        print('The file must hold one object: {"English text": {"$description": "...", "zh-Hans": "..."}, ...}', file=sys.stderr)
        return 2
    catalogs = load()
    for english, translations in entries.items():
        translations = dict(translations)
        description = translations.pop("$description", None)
        unknown = sorted((set(translations) - set(catalogs)) | ({"en"} & set(translations)))
        if unknown:
            print(f"{english!r}: no translation catalog named {', '.join(unknown)}", file=sys.stderr)
            return 2
        if add_one(catalogs, english, translations, hashed=False, description=description) != 0:
            return 2
    for tag, catalog in catalogs.items():
        save(tag, catalog)
    print(f"added {len(entries)} string(s)")
    return 0


def add_plural(args: list[str]) -> int:
    """The add-plural command: add-plural "one" "other" [--description "…"] [TAG#CATEGORY="text"]...

    Args:
        args: The command's arguments.

    Returns:
        A process exit code: 0, or 2 for a usage error, a one form with values the other form lacks, a form a language
        does not have, or a description with a line break.
    """
    args, description, well_formed = take_description(args)
    if len(args) < 2 or not well_formed:
        print(__doc__, file=sys.stderr)
        return 2
    one, other, catalogs = args[0], args[1], load()
    translations = parse_translations(args[2:], catalogs, plural=True)
    if translations is None:
        return 2
    if not placeholders(one) <= placeholders(other):
        print("The one form may only use values the other form uses", file=sys.stderr)
        return 2
    if description is not None and ("\n" in description or "\r" in description):
        print("A description is one line (it becomes a // comment)", file=sys.stderr)
        return 2
    for tag, forms in translations.items():
        wrong = set(forms) - set(categories_of(catalogs[tag]))
        if wrong:
            print(f"{tag} has no {', '.join(sorted(wrong))} form (its categories: {', '.join(categories_of(catalogs[tag]))})", file=sys.stderr)
            return 2
    catalogs["en"][f"{other}#one"] = one
    catalogs["en"][f"{other}#other"] = other
    if description is not None:
        describe_plural(catalogs["en"], other, description.strip())
    elif f"{other}#one" not in catalogs["en"].get(DESCRIPTIONS, {}):
        warn_undescribed(other)
    save("en", catalogs["en"])
    for tag, forms in translations.items():
        for category, text in forms.items():
            catalogs[tag][f"{other}#{category}"] = text
        save(tag, catalogs[tag])
    print("added")
    return 0


def describe(english: str, description: str) -> int:
    """The describe command: sets or replaces the description of a string, or of a plural string (by its other form).

    Args:
        english: The string's English text, its hash key, or a plural string's other form.
        description: The new description, one line.

    Returns:
        A process exit code: 0, or 2 when en.json has no such string or the description is empty or holds a line break.
    """
    description = description.strip()
    if not description or "\n" in description or "\r" in description:
        print("A description is one non-empty line (it becomes a // comment)", file=sys.stderr)
        return 2
    catalogs = load()
    english_catalog = catalogs["en"]
    if any((plural_parts(k) or ("", ""))[0] == english for k in english_catalog):
        describe_plural(english_catalog, english, description)
    elif english in english_catalog and not english.startswith("$"):
        english_catalog.setdefault(DESCRIPTIONS, {})[english] = description
    elif english_catalog.get(key_of(english)) == english:
        english_catalog.setdefault(DESCRIPTIONS, {})[key_of(english)] = description
    else:
        print(f"en.json has no string {english!r}", file=sys.stderr)
        return 2
    save("en", english_catalog)
    print("described")
    return 0


def rename(old: str, new: str) -> int:
    """The rename command: moves a string (or a plural string's forms) to its new English text everywhere.

    Args:
        old: The English text the catalogs hold now (for a plural string, its other form).
        new: The new English text (the C# literal is changed by hand).

    Returns:
        A process exit code: 0, or 2 when en.json lacks the old text or already has the new one.
    """
    catalogs = load()
    english = catalogs["en"]
    family = [k for k in english if (plural_parts(k) or ("", ""))[0] == old]
    if old in english and not family:
        moves = {old: new}
    elif family:
        moves = {k: f"{new}#{plural_parts(k)[1]}" for k in family}
    elif key_of(old) in english and english[key_of(old)] == old:
        moves = {key_of(old): key_of(new)}
    else:
        print(f"en.json has no string {old!r}", file=sys.stderr)
        return 2
    clash = [target for target in moves.values() if target in english]
    if clash:
        print(f"en.json already has {clash[0]!r}; remove one of them by hand", file=sys.stderr)
        return 2
    for tag, catalog in catalogs.items():
        moved = False
        descriptions = catalog.get(DESCRIPTIONS, {})
        for source, target in moves.items():
            if source not in catalog:
                continue
            text = catalog.pop(source)
            if tag == "en":
                # English's own text follows the rename; a plural string's one form is the developer's to change.
                parts = plural_parts(target)
                text = text if parts and parts[1] != "other" else new
            else:
                catalog.setdefault(REVIEW, []).append(target)
                moved = True
            catalog[target] = text
            # The description describes the string, not its wording: it moves with it (review it if the meaning moved).
            if source in descriptions:
                descriptions[target] = descriptions.pop(source)
        save(tag, catalog)
        if moved:
            print(f"{tag}: translation kept and marked for review")
    if old in moves:
        before, after = xaml_text(old), xaml_text(new)
        if before and after:
            rewrite_xaml(lambda content: content.replace(before, after))
        elif before:
            print(f"XAML cannot quote the new text; use {{loc:Tr Key={key_of(new)}}} and catalog.py add --hash", file=sys.stderr)
    for source, target in moves.items():
        if HASH_KEY.fullmatch(source):
            rewrite_xaml(lambda content, s=source, t=target: content.replace(f"Key={s}}}", f"Key={t}}}"))
    if family:
        print(f"en: the one form still reads {english.get(f'{new}#one', '')!r}; change it with the C# literal if needed")
    print("renamed; now change the C# literal, if any, to the new text")
    return 0


def remove(english: str) -> int:
    """The remove command: deletes a string (or a plural string's forms, or a hash-keyed string) from every catalog.

    Args:
        english: The English text (for a plural string, its other form).

    Returns:
        A process exit code: 0, or 2 when en.json lacks it or a XAML file still names it (its label would show the bare
        key once the entry is gone).
    """
    catalogs = load()
    keys = {k for k in catalogs["en"] if k == english or (plural_parts(k) or ("", ""))[0] == english}
    if key_of(english) in catalogs["en"] and catalogs["en"][key_of(english)] == english:
        keys.add(key_of(english))
    if not keys:
        print(f"en.json has no string {english!r}", file=sys.stderr)
        return 2
    uses = xaml_uses(english)
    if uses:
        print(f"{uses[0].relative_to(ROOT)} still names it; change it first", file=sys.stderr)
        return 2
    for tag, catalog in catalogs.items():
        if any(k in catalog for k in keys) or any(m in keys for m in catalog.get(REVIEW, [])):
            for key in keys:
                catalog.pop(key, None)
                catalog.get(DESCRIPTIONS, {}).pop(key, None)
            catalog[REVIEW] = [m for m in catalog.get(REVIEW, []) if m not in keys]
            save(tag, catalog)
    print(f"removed {len(keys)} key(s)")
    return 0


def reviewed(tag: str, key: str) -> int:
    """The reviewed command: clears one review mark, or all of a catalog's with --all.

    Args:
        tag: The catalog's tag.
        key: The reviewed entry's key, or "--all".

    Returns:
        A process exit code: 0, or 2 when the catalog does not exist or the key is not marked.
    """
    catalogs = load()
    if tag not in catalogs:
        print(f"No catalog {tag}", file=sys.stderr)
        return 2
    catalog = catalogs[tag]
    marks = catalog.get(REVIEW, [])
    if key == "--all":
        catalog[REVIEW] = []
    elif key in marks:
        catalog[REVIEW] = [m for m in marks if m != key]
    else:
        print(f"{tag}: {key!r} is not marked for review", file=sys.stderr)
        return 2
    save(tag, catalog)
    print("ok")
    return 0


def missing_entries(english: dict, catalog: dict) -> list[str]:
    """The keys a language lacks.

    Args:
        english: The English catalog.
        catalog: The language's catalog.

    Returns:
        Its missing plain strings, then each form its own rules need of each plural string it lacks.
    """
    needed = []
    families = set()
    for key in strings_of(english):
        parts = plural_parts(key)
        if parts:
            families.add(parts[0])
        else:
            needed.append(key)
    for family in sorted(families):
        needed.extend(f"{family}#{category}" for category in categories_of(catalog))
    return [k for k in needed if k not in catalog]


def missing(tag: str) -> int:
    """The missing command: prints a language's to-do list (missing strings and forms, then review marks).

    Args:
        tag: The language's tag.

    Returns:
        A process exit code: 0, or 2 when there is no translation catalog of that tag.
    """
    catalogs = load()
    if tag not in catalogs or tag == "en":
        print(f"No translation catalog {tag}", file=sys.stderr)
        return 2
    catalog = catalogs[tag]
    todo = missing_entries(catalogs["en"], catalog)
    descriptions = catalogs["en"].get(DESCRIPTIONS, {})
    for key in todo:
        # A plural string's description sits on its #one row (its #other row has the fixed note), and a form such as
        # #few has no row of its own in en.json.
        parts = plural_parts(key)
        description = descriptions.get(f"{parts[0]}#one") if parts else descriptions.get(key)
        print(f"missing: {key}" + (f"\n         // {description}" if description else ""))
    for key in sorted(set(catalog.get(REVIEW, []))):
        print(f"review:  {key}")
    print(f"{len(todo)} missing, {len(set(catalog.get(REVIEW, [])))} to review")
    return 0


def check() -> int:
    """The check command: reports broken entries and each language's missing strings and review marks.

    Returns:
        A process exit code: 1 when an entry is broken (missing strings are counted, not errors), else 0.
    """
    catalogs = load()
    english = catalogs["en"]
    problems = 0

    def problem(message: str) -> None:
        """Prints one broken entry and counts it.

        Args:
            message: What is wrong.
        """
        nonlocal problems
        problems += 1
        print(message)

    if categories_of(english) != ["one", "other"]:
        problem("en: the header's plurals must name one (English has one and other)")
    # Every row of en.json is described for translators; a description of no row is a leftover of an edit by hand.
    descriptions = english.get(DESCRIPTIONS, {})
    for key in strings_of(english):
        if not descriptions.get(key, "").strip():
            problem(f"en: {key[:60]!r} has no description (catalog.py describe)")
    for key in descriptions:
        if key not in english:
            problem(f"en: the description of {key[:60]!r} belongs to no row")
    families: dict[str, set[str]] = {}
    for key, text in strings_of(english).items():
        parts = plural_parts(key)
        if parts:
            families.setdefault(parts[0], set()).add(parts[1])
            if parts[1] == "other" and text != parts[0]:
                problem(f"en: {key!r} must read the same as its key without #other")
        elif HASH_KEY.fullmatch(key):
            if key_of(text) != key:
                problem(f"en: {key} is not the hash key of its text {text[:60]!r} (expected {key_of(text)})")
        elif text != key:
            problem(f"en: {key[:60]!r} must map to itself")
    for family, forms in families.items():
        if forms != set(ENGLISH_FORMS):
            problem(f"en: the plural string {family[:60]!r} needs exactly the forms one and other")
        elif not placeholders(english[f"{family}#one"]) <= placeholders(family):
            problem(f"en: the one form of {family[:60]!r} uses a value the other form does not")
    for tag, catalog in catalogs.items():
        # The app embeds a right-to-left language's text itself; a control written here would come on top of it, and one
        # left unclosed would turn the direction of everything after the string.
        for key, text in strings_of(catalog).items():
            if any(c in DIRECTION_CONTROLS for c in text):
                problem(f"{tag}: {key[:60]!r} holds a direction control (U+202A-U+202E, U+2066-U+2069); the app adds what "
                        "it needs, a direction mark (U+200F) is all a catalog may hold")
        if tag == "en":
            continue
        if METADATA in catalog and catalog[METADATA].get("direction", "ltr") not in ("ltr", "rtl"):
            problem(f"{tag}: the header's direction must be \"ltr\" or \"rtl\" (the app refuses the catalog otherwise)")
        if "plurals" not in catalog.get(METADATA, {}):
            problem(f"{tag}: the header has no plurals (write {{}} for a language with only the other form)")
        own = set(categories_of(catalog))
        for key, text in strings_of(catalog).items():
            parts = plural_parts(key)
            source = parts[0] if parts else key
            if parts and parts[0] not in families:
                problem(f"{tag}: {key[:60]!r} is a form of no English plural string")
                continue
            if parts and parts[1] not in own:
                problem(f"{tag}: {key[:60]!r} is a form {tag} does not have (its categories: {', '.join(sorted(own))})")
                continue
            if not parts and key not in english:
                problem(f"{tag}: {key[:60]!r} is not in en.json (renamed or removed: catalog.py rename, or delete it)")
                continue
            allowed = placeholders(source if parts else english[key])
            if not placeholders(text) <= allowed:
                problem(f"{tag}: {key[:60]!r} uses a value English does not")
        todo = missing_entries(english, catalog)
        marks = set(catalog.get(REVIEW, []))
        stale = sorted(m for m in marks if m not in catalog)
        for mark in stale:
            problem(f"{tag}: the review mark {mark[:60]!r} names no entry")
        print(f"{tag}: {len(todo)} missing, {len(marks)} to review")
    print("ok" if problems == 0 else f"{problems} problem(s)")
    return 1 if problems else 0


def migrate() -> int:
    """The migrate command: converts hash keys to English-text keys in every catalog and XAML file.

    A text that is already a key, reads like a plural form, a header or a hash key, or that XAML cannot quote, stays
    under its hash key: hash keys are allowed for exactly such texts. Running it again changes nothing.

    Returns:
        A process exit code: always 0.
    """
    catalogs = load()
    english = catalogs["en"]
    moves = {k: v for k, v in strings_of(english).items() if HASH_KEY.fullmatch(k) and key_of(v) == k}
    in_xaml = set()
    for path in xaml_files():
        in_xaml.update(TR_KEY.findall(path.read_bytes().decode("utf-8")))
    kept = [k for k, v in moves.items()
            if v in english or plural_parts(v) or v.startswith("$") or HASH_KEY.fullmatch(v) or (k in in_xaml and xaml_text(v) is None)]
    for key in kept:
        print(f"{key} stays a hash key: {moves.pop(key)[:60]!r}")
    for tag, catalog in catalogs.items():
        converted = 0
        descriptions = catalog.get(DESCRIPTIONS, {})
        for key, text in list(catalog.items()):
            if key in moves:
                del catalog[key]
                catalog[moves[key]] = text if tag != "en" else moves[key]
                if key in descriptions:
                    descriptions[moves[key]] = descriptions.pop(key)
                converted += 1
        if REVIEW in catalog:
            catalog[REVIEW] = [moves.get(m, m) for m in catalog[REVIEW]]
        if converted:
            save(tag, catalog)
            print(f"{tag}: {converted} hash key(s) converted")
    rewrite_xaml(lambda content: TR_KEY.sub(lambda m: xaml_text(moves[m.group(1)]) if m.group(1) in moves else m.group(0), content))
    print(f"{len(moves)} hash key(s) of en.json converted" if moves else "nothing to convert")
    return 0


def main(argv: list[str]) -> int:
    """Dispatches one command (see the module summary).

    Args:
        argv: The command line without the script's name.

    Returns:
        The command's exit code, or 2 (after the usage text) for an unknown command.
    """
    command, args = (argv[0], argv[1:]) if argv else ("", [])
    if command == "add":
        return add(args)
    if command == "add-file" and len(args) == 1:
        return add_file(args[0])
    if command == "add-plural":
        return add_plural(args)
    if command == "describe" and len(args) == 2:
        return describe(args[0], args[1])
    if command == "rename" and len(args) == 2:
        return rename(args[0], args[1])
    if command == "remove" and len(args) == 1:
        return remove(args[0])
    if command == "reviewed" and len(args) == 2:
        return reviewed(args[0], args[1])
    if command == "missing" and len(args) == 1:
        return missing(args[0])
    if command == "check" and not args:
        return check()
    if command == "migrate" and not args:
        return migrate()
    if command == "key" and len(args) == 1:
        print(key_of(args[0]))
        return 0
    print(__doc__, file=sys.stderr)
    return 2


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
