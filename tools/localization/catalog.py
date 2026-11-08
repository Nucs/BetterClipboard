"""Edit BetterClipboard's UI string catalogs (src/BetterClipboard.Core/Localization/*.json) without breaking them.

Every UI string has a key: "T" + the first 12 hex digits of the SHA-256 of its English text (UTF-8). XAML names the
key ({loc:Tr Key=T...}); C# names the English text (UiText.Translate("...") / UiText.Format($"...")), which UiText maps
back to the key. Every catalog must hold every key, or the string shows in English there; LocalizationTests check it.

Commands:
  python tools/localization/catalog.py key "English text"
      Prints the key of an English text (for XAML).
  python tools/localization/catalog.py add "English text" zh-Hans="中文"
      Adds a string to en.json and one translation per catalog named; a catalog not named gets the English text, marked
      as untranslated in the output, so the tests still pass and a translator can find it.
  python tools/localization/catalog.py rename "Old English text" "New English text"
      Changes a string's English text: moves it to its new key in every catalog (translations kept, to be reviewed for
      the new wording) and rewrites {loc:Tr Key=...} in every XAML file. Change the C# literal by hand.
  python tools/localization/catalog.py check
      Rewrites nothing; reports keys that do not match their English text, and catalogs that miss keys.

Files keep their layout: "$language" first, then the keys sorted, two-space indent, UTF-8 without escapes, LF and a
final newline (exactly what json.dumps(indent=2, ensure_ascii=False) writes).
"""

import hashlib
import json
import sys
from pathlib import Path

CATALOGS = Path(__file__).resolve().parents[2] / "src" / "BetterClipboard.Core" / "Localization"
METADATA = "$language"


def key_of(english: str) -> str:
    """The catalog key of an English text."""
    return "T" + hashlib.sha256(english.encode("utf-8")).hexdigest()[:12]


def load() -> dict[str, dict]:
    """Every catalog by tag (file name without .json)."""
    return {path.stem: json.loads(path.read_text(encoding="utf-8")) for path in sorted(CATALOGS.glob("*.json"))}


def save(tag: str, catalog: dict) -> None:
    """Writes one catalog in the canonical layout: metadata first, then the keys in ordinal order."""
    ordered = {}
    if METADATA in catalog:
        ordered[METADATA] = catalog[METADATA]
    for name in sorted(k for k in catalog if k != METADATA):
        ordered[name] = catalog[name]
    text = json.dumps(ordered, ensure_ascii=False, indent=2) + "\n"
    (CATALOGS / f"{tag}.json").write_bytes(text.encode("utf-8"))


def add(english: str, translations: dict[str, str]) -> int:
    """Adds one string to every catalog; returns a process exit code."""
    catalogs = load()
    unknown = sorted(set(translations) - set(catalogs))
    if unknown:
        print(f"No catalog named {', '.join(unknown)}; catalogs: {', '.join(catalogs)}", file=sys.stderr)
        return 2
    key = key_of(english)
    for tag, catalog in catalogs.items():
        text = english if tag == "en" else translations.get(tag)
        if text is None:
            print(f"{tag}: no translation given; the English text is used until one is added")
            text = english
        if catalog.get(key, text) != text:
            print(f"{tag}: {key} already holds another text; left as it is", file=sys.stderr)
            continue
        catalog[key] = text
        save(tag, catalog)
    print(key)
    return 0


def rename(old: str, new: str) -> int:
    """Moves a string to the key of its new English text in every catalog and XAML file; returns an exit code."""
    catalogs = load()
    old_key, new_key = key_of(old), key_of(new)
    if catalogs["en"].get(old_key) != old:
        print(f"en.json has no string {old!r}", file=sys.stderr)
        return 2
    if new_key in catalogs["en"]:
        print(f"en.json already has {new!r} ({new_key}); remove one of them by hand", file=sys.stderr)
        return 2
    for tag, catalog in catalogs.items():
        text = catalog.pop(old_key, None)
        catalog[new_key] = new if tag == "en" else (text if text is not None else new)
        save(tag, catalog)
        if tag != "en":
            print(f"{tag}: translation kept; review it for the new wording")
    # XAML names keys; rewrite every reference so no label falls back to the bare key.
    source = CATALOGS.parents[2]
    for xaml in source.rglob("*.xaml"):
        if "bin" in xaml.parts or "obj" in xaml.parts:
            continue
        content = xaml.read_bytes().decode("utf-8")
        updated = content.replace(f"Key={old_key}}}", f"Key={new_key}}}")
        if updated != content:
            xaml.write_bytes(updated.encode("utf-8"))
            print(f"{xaml.relative_to(source.parent)}: key updated")
    print(f"{old_key} -> {new_key}; now change the C# literal, if any, to the new text")
    return 0


def check() -> int:
    """Reports catalogs whose keys disagree with English, and missing keys; returns 1 when something is wrong."""
    catalogs = load()
    english = catalogs["en"]
    problems = 0
    for name, text in english.items():
        if name != METADATA and key_of(text) != name:
            print(f"en: {name} is not the key of its text {text[:60]!r} (expected {key_of(text)})")
            problems += 1
    for tag, catalog in catalogs.items():
        missing = sorted(set(english) - set(catalog))
        extra = sorted(set(catalog) - set(english))
        if missing or extra:
            print(f"{tag}: {len(missing)} missing, {len(extra)} not in en.json")
            problems += 1
    print("ok" if problems == 0 else f"{problems} problem(s)")
    return 1 if problems else 0


def main(argv: list[str]) -> int:
    """Command dispatch (see the module summary)."""
    if len(argv) >= 2 and argv[0] == "key":
        print(key_of(argv[1]))
        return 0
    if len(argv) >= 2 and argv[0] == "add":
        pairs = dict(arg.split("=", 1) for arg in argv[2:])
        return add(argv[1], pairs)
    if len(argv) == 3 and argv[0] == "rename":
        return rename(argv[1], argv[2])
    if argv == ["check"]:
        return check()
    print(__doc__, file=sys.stderr)
    return 2


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
