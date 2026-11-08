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

Commands:
  python tools/localization/catalog.py add "English text" [zh-Hans="中文"]...
      Adds a string to en.json, and a translation to each catalog named.
  python tools/localization/catalog.py add --hash "English text" [zh-Hans="中文"]...
      The same under a hash key, which it prints (for {loc:Tr Key=...}).
  python tools/localization/catalog.py add-file strings.json
      Adds many strings at once from a UTF-8 JSON object {"English text": {"zh-Hans": "中文"}, ...}: the same rules as
      add for each one. Quoting many strings with apostrophes or Chinese on a command line is error-prone; a file is not.
  python tools/localization/catalog.py add-plural "{0:N0} item" "{0:N0} items" [zh-Hans#other="{0:N0} 项"]...
      Adds a plural string: English's one and other forms, and any language's forms as TAG#CATEGORY="text".
  python tools/localization/catalog.py rename "Old English text" "New English text"
      Changes a string's (or a plural string's) English text in every catalog and in XAML, keeping each translation
      and marking it for review. Change the C# literal by hand.
  python tools/localization/catalog.py remove "English text"
      Deletes a string (or a plural string, by its other form) from every catalog; refuses while XAML still names it.
  python tools/localization/catalog.py reviewed TAG "Key" | --all
      Confirms translations marked for review.
  python tools/localization/catalog.py missing TAG
      Lists the strings and plural forms a language lacks, and those marked for review: a translator's to-do list.
  python tools/localization/catalog.py check
      Rewrites nothing. Reports broken entries (exit 1) and, per language, how many strings it lacks or has marked for
      review (not errors). The tests check the code against the catalogs.
  python tools/localization/catalog.py migrate
      Converts hash keys to English-text keys in every catalog and every XAML file; running it again changes nothing.
      Use it after merging work written with the old hash keys.

Files keep their layout: "$language" first, then "$review", then the keys in ordinal order, two-space indent, UTF-8
without escapes, LF and a final newline (exactly what json.dumps(indent=2, ensure_ascii=False) writes).
"""

import hashlib
import json
import re
import string
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
CATALOGS = ROOT / "src" / "BetterClipboard.Core" / "Localization"
SOURCES = ROOT / "src"
METADATA = "$language"
REVIEW = "$review"
CATEGORIES = ("zero", "one", "two", "few", "many", "other")
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


def load() -> dict[str, dict]:
    """Reads every catalog.

    Returns:
        The parsed catalogs, keyed by tag (the file name without .json: "en", "zh-Hans").
    """
    return {path.stem: json.loads(path.read_text(encoding="utf-8")) for path in sorted(CATALOGS.glob("*.json"))}


def save(tag: str, catalog: dict) -> None:
    """Writes one catalog in the canonical layout: header, review marks, then the keys in ordinal order.

    Args:
        tag: The catalog's tag, which names its file.
        catalog: Its strings by key, plus the "$language" header and the optional "$review" list.
    """
    ordered = {}
    if METADATA in catalog:
        ordered[METADATA] = catalog[METADATA]
    review = sorted(set(catalog.get(REVIEW, [])))
    if review:
        ordered[REVIEW] = review
    for name in sorted(k for k in catalog if not k.startswith("$")):
        ordered[name] = catalog[name]
    text = json.dumps(ordered, ensure_ascii=False, indent=2) + "\n"
    (CATALOGS / f"{tag}.json").write_bytes(text.encode("utf-8"))


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

    Returns:
        A generator of paths under src. Build output is skipped, and so is a folder whose name ends in .xaml (a published
        app has one named Microsoft.UI.Xaml).
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


def add_one(catalogs: dict, english: str, translations: dict[str, str], hashed: bool) -> int:
    """Adds one string to the loaded catalogs (the caller saves them).

    Args:
        catalogs: The loaded catalogs, changed in place.
        english: The English text.
        translations: A translation per catalog tag; a catalog not named lacks the string (it shows in English there).
        hashed: Whether to keep it under a hash key instead of its English text.

    Returns:
        0, or 2 when the text cannot be a key as it is.
    """
    key = key_of(english) if hashed else english
    if plural_parts(key) or key.startswith("$") or (not hashed and HASH_KEY.fullmatch(key)):
        print(f"{english!r} cannot be a key as it is (it looks like a plural form, a hash key or a header)", file=sys.stderr)
        return 2
    for tag, catalog in catalogs.items():
        text = english if tag == "en" else translations.get(tag)
        if text is None:
            continue
        if catalog.get(key, text) != text:
            print(f"{tag}: {key!r} already holds another text; left as it is", file=sys.stderr)
            continue
        catalog[key] = text
    return 0


def add(args: list[str]) -> int:
    """The add command: add [--hash] "English" [TAG="text"]...

    Args:
        args: The command's arguments.

    Returns:
        A process exit code: 0, or 2 for a usage error or a text that cannot be a key.
    """
    hashed = bool(args) and args[0] == "--hash"
    args = args[1:] if hashed else args
    if not args:
        print(__doc__, file=sys.stderr)
        return 2
    english, catalogs = args[0], load()
    translations = parse_translations(args[1:], catalogs, plural=False)
    if translations is None:
        return 2
    code = add_one(catalogs, english, {tag: forms[""] for tag, forms in translations.items()}, hashed)
    if code == 0:
        for tag, catalog in catalogs.items():
            save(tag, catalog)
        print(key_of(english) if hashed else "added")
    return code


def add_file(path: str) -> int:
    """The add-file command: adds every string of a JSON file {"English": {"tag": "translation"}}.

    Args:
        path: The UTF-8 JSON file.

    Returns:
        A process exit code: 0, or 2 when the file's shape is wrong, a string names an unknown catalog, or a text cannot
        be a key (nothing is saved then).
    """
    entries = json.loads(Path(path).read_text(encoding="utf-8"))
    if not isinstance(entries, dict) or not all(isinstance(v, dict) for v in entries.values()):
        print('The file must hold one object: {"English text": {"zh-Hans": "..."}, ...}', file=sys.stderr)
        return 2
    catalogs = load()
    for english, translations in entries.items():
        unknown = sorted((set(translations) - set(catalogs)) | ({"en"} & set(translations)))
        if unknown:
            print(f"{english!r}: no translation catalog named {', '.join(unknown)}", file=sys.stderr)
            return 2
        if add_one(catalogs, english, translations, hashed=False) != 0:
            return 2
    for tag, catalog in catalogs.items():
        save(tag, catalog)
    print(f"added {len(entries)} string(s)")
    return 0


def add_plural(args: list[str]) -> int:
    """The add-plural command: add-plural "one" "other" [TAG#CATEGORY="text"]...

    Args:
        args: The command's arguments.

    Returns:
        A process exit code: 0, or 2 for a usage error, a one form with values the other form lacks, or a form a
        language does not have.
    """
    if len(args) < 2:
        print(__doc__, file=sys.stderr)
        return 2
    one, other, catalogs = args[0], args[1], load()
    translations = parse_translations(args[2:], catalogs, plural=True)
    if translations is None:
        return 2
    if not placeholders(one) <= placeholders(other):
        print("The one form may only use values the other form uses", file=sys.stderr)
        return 2
    for tag, forms in translations.items():
        wrong = set(forms) - set(categories_of(catalogs[tag]))
        if wrong:
            print(f"{tag} has no {', '.join(sorted(wrong))} form (its categories: {', '.join(categories_of(catalogs[tag]))})", file=sys.stderr)
            return 2
    catalogs["en"][f"{other}#one"] = one
    catalogs["en"][f"{other}#other"] = other
    save("en", catalogs["en"])
    for tag, forms in translations.items():
        for category, text in forms.items():
            catalogs[tag][f"{other}#{category}"] = text
        save(tag, catalogs[tag])
    print("added")
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
    for key in todo:
        print(f"missing: {key}")
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
        if tag == "en":
            continue
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
        for key, text in list(catalog.items()):
            if key in moves:
                del catalog[key]
                catalog[moves[key]] = text if tag != "en" else moves[key]
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
