"""Composes a release's body from its English notes and their translations, and refuses a translation of other notes.

The release page on GitHub and the app's update dialog show the same body (CLAUDE.md section 2.67). With translations:

    **ENG** / [简体中文](https://github.com/Nucs/BetterClipboard/blob/v<version>/docs/release-notes/<version>/zh-Hans.md)

    <the annotated tag's message: the English notes>

    ---

    **Code signing policy:** see [the README](...#code-signing-policy) ...

    <!-- release-notes zh-Hans
    <the translation as its page shows it: its language line, its notes, its footer>
    -->

A release without translation files gets exactly the body it always had: the notes, a rule and the footer.

Where translations come from: one file per language and release, docs/release-notes/<version>/<tag>.md, <tag> one of
the app's catalog tags (zh-Hans, ru, es, he). GitHub shows each file as a page, which the English's first row links
to, so a reader of the release page clicks 简体中文 and reads the Chinese page. The composer copies the file, without its
stamp, into the body as a hidden block. Every version of the update dialog since 0.2.6 skips such a block, and so does
GitHub's page (tools/probes/probe_release_notes.py proves it for a body); 0.2.12 and later show it in place of the English
for a reader of that language, and switch with the first row's links. Each file reads:

    [ENG](https://github.com/Nucs/BetterClipboard/releases/tag/v<version>) / **简体中文**    its language line

    <!-- source sha256:<64 hex digits> -->                                                  its stamp, hidden on GitHub

    BetterClipboard <version>                                                               the notes, translated
    ...

    ---

    **代码签名政策：** ... (a link to the README's #code-signing-policy)                        the footer, translated

The stamp is the SHA-256 of the English the file was translated from, normalized as `git tag --cleanup=whitespace`
leaves a tag's message. The notes change many times before a tag (each commit carries a new draft), so the composer
refuses a stamp that is not the tag's: a translation of an older draft never ships. `stamp` prints the right line for the
notes a translator works from; `check` runs every test the release job runs, before anything is tagged.

The block must stay a comment for every reader, so a file may hold no "-->", no "--!>" (GitHub ends a comment there too)
and no "<!--". A right-to-left language (Hebrew) is refused until the dialog draws right-to-left notes. The English
itself must close every code fence and HTML comment it opens: the footer and the blocks follow it, and an open fence
would draw them as code for every reader.

Usage:
    python tools/release/release_notes.py stamp NOTES.md
    python tools/release/release_notes.py check --version 0.2.12 NOTES.md
    python tools/release/release_notes.py compose --version 0.2.12 --notes NOTES.md --body BODY.md --english ENGLISH.md
        [--store-dir DIR]

NOTES.md is the English notes: the draft that becomes the tag's message, or the message itself (the release job).
compose writes the release page's body (--body), the English notes with the footer that the Store's English listing
reads (--english, the file the release job always wrote), and with --store-dir one file per translation, <tag>.md, of
its notes alone for the Store's listing in that language.

Exit codes: 0 done; 1 a translation is stale or malformed, the English leaves a fence or a comment open, or the body is
over GitHub's limit (each problem is printed with what to do); 2 bad arguments or a file that cannot be read.
"""

import argparse
import hashlib
import json
import re
import sys
from dataclasses import dataclass
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]

# Where each release's translations live: docs/release-notes/<version>/<tag>.md.
TRANSLATIONS_ROOT = ROOT / "docs" / "release-notes"

# The app's catalogs: each one's "$language" header names the language in itself ("name") and its direction.
CATALOGS = ROOT / "src" / "BetterClipboard.Core" / "Localization"

REPOSITORY = "Nucs/BetterClipboard"

# The English's label on every language line, as the README's own line writes it.
ENGLISH_LABEL = "ENG"

# The footer every release page carries: SignPath Foundation's terms ask each download and release page to name the
# code signing policy (docs/code-signing.md). The release job wrote it itself until 0.2.12; it is here now, once.
FOOTER = (
    "**Code signing policy:** see [the README](https://github.com/Nucs/BetterClipboard#code-signing-policy) for whether "
    "this release is signed, what is signed, and who approves it."
)

# The order of the languages on a language line: the README's own line, then any other alphabetically.
LINE_ORDER = ["zh-Hans", "ru", "es", "he"]

# The most characters GitHub accepts in a release body ("body is too long (maximum is 125000 characters)", HTTP 422).
GITHUB_LIMIT = 125_000

# A catalog tag, the same pattern the app reads a block's tag with (ReleaseNotesLanguages.OpeningLine).
TAG = re.compile(r"^[A-Za-z]{2,3}(?:-[A-Za-z0-9]{2,8}){0,3}$")

# A file's stamp line.
STAMP = re.compile(r"^<!-- source sha256:([0-9a-f]{64}) -->$")

# What would end the hidden block early for some reader, or open another comment inside it.
FORBIDDEN = ("-->", "--!>", "<!--")


class NotesError(Exception):
    """A translation that may not ship, or a body that cannot be composed; the message says which file and what to do."""


@dataclass
class Translation:
    """One translation, read and checked.

    Attributes:
        tag: The catalog tag (zh-Hans), as the file is named.
        block: The text of its hidden block: the file without its stamp (its language line, notes and footer).
        notes: Its notes alone, without the language line, the stamp, the rule and the footer: what the Store's listing
            in that language gets (shortened there to its "What's new").
    """

    tag: str
    block: str
    notes: str


def normalize(text):
    """Normalizes notes the way `git tag --cleanup=whitespace` leaves a tag's message, so a draft and its tag stamp alike.

    Args:
        text: The notes.

    Returns:
        The text with LF line ends, no trailing blanks on any line, runs of blank lines collapsed into one, and no blank
        lines at the start or the end; a byte order mark at the start is dropped.
    """
    text = text.lstrip("\ufeff").replace("\r\n", "\n").replace("\r", "\n")
    lines = [line.rstrip() for line in text.split("\n")]
    kept = []
    for line in lines:
        if not line and (not kept or not kept[-1]):
            continue
        kept.append(line)
    while kept and not kept[-1]:
        kept.pop()
    return "\n".join(kept)


def stamp_of(english):
    """The stamp of English notes: the SHA-256 of their normalized text, as 64 lowercase hex digits.

    Args:
        english: The English notes.

    Returns:
        The hex digest.
    """
    return hashlib.sha256(normalize(english).encode("utf-8")).hexdigest()


def stamp_line(english):
    """The line a translation of these English notes carries under its language line.

    Args:
        english: The English notes the translation is made from.

    Returns:
        The line, an HTML comment GitHub does not show.
    """
    return f"<!-- source sha256:{stamp_of(english)} -->"


def release_page(version):
    """The release's page on GitHub, where a translation's ENG leads.

    Args:
        version: The version, without its "v".

    Returns:
        The address.
    """
    return f"https://github.com/{REPOSITORY}/releases/tag/v{version}"


def translation_page(version, tag):
    """A translation's page: its file in the repository at the release's tag, which GitHub shows as a page.

    Args:
        version: The version, without its "v".
        tag: The catalog tag.

    Returns:
        The address.
    """
    return f"https://github.com/{REPOSITORY}/blob/v{version}/docs/release-notes/{version}/{tag}.md"


def catalog_language(tag, catalogs=None):
    """Reads a language's name in itself and its direction from the app's catalog of that tag.

    Args:
        tag: The catalog tag.
        catalogs: The folder of the catalogs; None for the repository's (read when called, so tests can point it
            elsewhere).

    Returns:
        (name, right_to_left): e.g. ("简体中文", False).

    Raises:
        NotesError: The app has no catalog of that tag, or its header cannot be read.
    """
    path = Path(catalogs or CATALOGS) / f"{tag}.json"
    if not path.is_file():
        raise NotesError(f"{tag}: the app has no catalog of that language ({path}); a translation's file name is a catalog tag.")
    try:
        header = json.loads(path.read_text(encoding="utf-8-sig"))["$language"]
        return str(header["name"]), str(header.get("direction", "ltr")).lower() == "rtl"
    except (ValueError, KeyError, TypeError) as error:
        raise NotesError(f"{tag}: the catalog's \"$language\" header cannot be read ({error}).") from error


def ordered(tags):
    """Sorts tags the way a language line lists them: the README's order first, any other alphabetically.

    Args:
        tags: Catalog tags.

    Returns:
        A new list.
    """
    return sorted(tags, key=lambda tag: (LINE_ORDER.index(tag) if tag in LINE_ORDER else len(LINE_ORDER), tag.lower()))


def language_line(version, shown, tags, catalogs=None):
    """The first row of a release in one language: every language by its own name, the shown one bold, the others links.

    Args:
        version: The version, without its "v".
        shown: "en" for the English body, else the tag of the translation the line heads.
        tags: Every translation's tag.
        catalogs: The folder of the catalogs; None for the repository's (read when called, so tests can point it
            elsewhere).

    Returns:
        E.g. "**ENG** / [简体中文](...)" for the English, "[ENG](...) / **简体中文**" for the Chinese.

    Raises:
        NotesError: A tag has no catalog.
    """
    parts = [f"**{ENGLISH_LABEL}**" if shown == "en" else f"[{ENGLISH_LABEL}]({release_page(version)})"]
    for tag in ordered(tags):
        name, _ = catalog_language(tag, catalogs)
        parts.append(f"**{name}**" if tag == shown else f"[{name}]({translation_page(version, tag)})")
    return " / ".join(parts)


def translation_files(version, translations_root=None):
    """The translation files of a release, by tag.

    Args:
        version: The version, without its "v".
        translations_root: The folder that holds one folder per version; None for the repository's (read when called).

    Returns:
        {tag: path}; empty when the release has no folder or no files.

    Raises:
        NotesError: A file whose name is no catalog tag (it would never be shown, and is likely a typo).
    """
    folder = Path(translations_root or TRANSLATIONS_ROOT) / version
    if not folder.is_dir():
        return {}
    files = {}
    for path in sorted(folder.iterdir()):
        if path.suffix != ".md" or not path.is_file():
            continue
        if not TAG.match(path.stem) or path.stem.lower() == "en":
            raise NotesError(f"{path}: not a translation's name; each is <catalog tag>.md (zh-Hans.md), and English is the tag's message.")
        files[path.stem] = path
    return files


def read_translation(path, tag, version, tags, english=None, catalogs=None):
    """Reads one translation file and checks everything the release job checks.

    Args:
        path: The file.
        tag: Its catalog tag.
        version: The release's version, without its "v".
        tags: Every translation's tag of the release (the language line names them all).
        english: The English notes the translation must be made from; None checks the format alone (CI does, without
            the draft).
        catalogs: The folder of the catalogs; None for the repository's (read when called, so tests can point it
            elsewhere).

    Returns:
        The Translation.

    Raises:
        NotesError: Anything that keeps it from shipping: an unreadable file, another language line, a missing or stale
            stamp, no notes or no footer, a "-->", "--!>" or "<!--" in it, a right-to-left language.
    """
    try:
        text = Path(path).read_text(encoding="utf-8-sig").replace("\r\n", "\n").replace("\r", "\n")
    except (OSError, UnicodeDecodeError) as error:
        raise NotesError(f"{path}: cannot be read as UTF-8 ({error}).") from error

    _, right_to_left = catalog_language(tag, catalogs)
    if right_to_left:
        raise NotesError(f"{path}: the update dialog does not draw right-to-left notes yet (CLAUDE.md section 6), so a "
                         f"{tag} translation cannot ship.")

    lines = text.split("\n")
    rows = [index for index, line in enumerate(lines) if line.strip()]
    if len(rows) < 3:
        raise NotesError(f"{path}: needs its language line, its stamp and its notes.")

    expected_line = language_line(version, tag, tags, catalogs)
    if lines[rows[0]].strip() != expected_line:
        raise NotesError(f"{path}: its first line must be the language line\n    {expected_line}\n  and is\n    {lines[rows[0]].strip()}")

    stamp = STAMP.match(lines[rows[1]].strip())
    if not stamp:
        raise NotesError(f"{path}: its second line must be the stamp of the English it was translated from, "
                         f"<!-- source sha256:<hex> -->; `release_notes.py stamp NOTES.md` prints it.")
    if english is not None and stamp.group(1) != stamp_of(english):
        raise NotesError(f"{path}: translated from other English notes than these (its stamp is {stamp.group(1)[:12]}…, "
                         f"these notes' is {stamp_of(english)[:12]}…). Translate what changed, then put\n    "
                         f"{stamp_line(english)}\n  as its second line.")

    content = "\n".join(lines[rows[1] + 1:]).strip()
    for sequence in FORBIDDEN:
        if sequence in content or sequence in expected_line:
            raise NotesError(f"{path}: holds \"{sequence}\", which would end the hidden block early (or open another) for "
                             f"some reader; write an arrow as →.")

    # The footer follows the last rule ("---" alone on its line), as in the English body.
    content_lines = content.split("\n")
    rules = [index for index, line in enumerate(content_lines) if line.strip() == "---"]
    if not rules:
        raise NotesError(f"{path}: has no \"---\" line before its footer (the translated code signing policy line).")
    notes = "\n".join(content_lines[:rules[-1]]).strip()
    footer = "\n".join(content_lines[rules[-1] + 1:]).strip()
    if not notes:
        raise NotesError(f"{path}: has no notes before its \"---\" line.")
    if "#code-signing-policy" not in footer:
        raise NotesError(f"{path}: its footer must link the README's code signing policy (#code-signing-policy): "
                         f"SignPath Foundation's terms ask every release page to name it.")

    return Translation(tag=tag, block=f"{expected_line}\n\n{content}", notes=notes)


def read_translations(version, english=None, translations_root=None, catalogs=None):
    """Reads and checks every translation of a release.

    Args:
        version: The version, without its "v".
        english: The English notes they must be made from; None checks the format alone.
        translations_root: The folder that holds one folder per version; None for the repository's (read when called).
        catalogs: The folder of the catalogs; None for the repository's (read when called, so tests can point it
            elsewhere).

    Returns:
        The translations in language-line order; empty when the release has none.

    Raises:
        NotesError: Every problem found, one per line, so a release with two stale files says so once.
    """
    files = translation_files(version, translations_root)
    tags = list(files)
    translations, problems = [], []
    for tag in ordered(tags):
        try:
            translations.append(read_translation(files[tag], tag, version, tags, english, catalogs))
        except NotesError as error:
            problems.append(str(error))
    if problems:
        raise NotesError("\n".join(problems))
    return translations


def english_notes(english):
    """The English notes with the footer: the release page's body without translations, and the Store's English input.

    Args:
        english: The English notes (the tag's message).

    Returns:
        The text, ending with a line end.
    """
    return f"{english.strip()}\n\n---\n\n{FOOTER}\n"


def fence_of(trimmed):
    """The code fence a line opens, read as the update dialog's parser reads it (MarkdownParser.TryReadFence).

    Args:
        trimmed: The line without the blanks around it.

    Returns:
        (character, length) of the fence, or None when the line opens none: at least three backticks or tildes, and a
        backtick fence's info text holds no backtick (```x``` is inline code).
    """
    if not trimmed or trimmed[0] not in "`~":
        return None
    char = trimmed[0]
    length = len(trimmed) - len(trimmed.lstrip(char))
    if length < 3 or (char == "`" and "`" in trimmed[length:]):
        return None
    return char, length


def closes_fence(trimmed, fence):
    """Whether a line closes an open fence, as the parser decides (MarkdownParser.IsClosingFence).

    Args:
        trimmed: The line without the blanks around it.
        fence: The open fence, as fence_of gives it.

    Returns:
        True for at least as many of the fence's characters and nothing else.
    """
    char, length = fence
    return len(trimmed) >= length and set(trimmed) == {char}


def check_english(english):
    """Refuses English notes that would draw what must stay hidden, or hide what must show, once composed.

    The footer and the hidden blocks follow the English, so the English must end with every code fence and every HTML
    comment it opens closed. Read as the update dialog's parser reads it (fences first; a line that starts with
    "<!--" hides everything to the first line holding "-->"); GitHub's renderer reads both the same way.

    Args:
        english: The English notes (the tag's message).

    Raises:
        NotesError: A fence left open, which would draw the footer and every translation after it as code in every
            update dialog since 0.2.6 and on the release page; or a comment left open, which would hide the footer and
            swallow the first translation's block.
    """
    lines = english.replace("\r\n", "\n").replace("\r", "\n").split("\n")
    fence, fence_line, index = None, 0, 0
    while index < len(lines):
        trimmed = lines[index].strip()
        if fence is not None:
            if closes_fence(trimmed, fence):
                fence = None
        elif trimmed:
            opened = fence_of(trimmed)
            if opened is not None:
                fence, fence_line = opened, index + 1
            elif trimmed.startswith("<!--"):
                end = next((row for row in range(index, len(lines)) if "-->" in lines[row]), None)
                if end is None:
                    raise NotesError(f"The English notes' line {index + 1} opens an HTML comment that never closes: it "
                                     f"would hide the footer and the translations. Close it with -->, or remove it.")
                index = end
        index += 1
    if fence is not None:
        raise NotesError(f"The English notes' line {fence_line} opens a code fence that never closes: every update "
                         f"dialog and the release page would show the footer and the translations as code. Close it.")


def compose(english, version, translations, catalogs=None):
    """Composes the release page's body.

    Args:
        english: The English notes (the tag's message).
        version: The version, without its "v".
        translations: The checked translations (read_translations).
        catalogs: The folder of the catalogs; None for the repository's (read when called, so tests can point it
            elsewhere).

    Returns:
        The body: without translations exactly english_notes(english); with them, the English's language line first and
        one hidden block per translation after the footer.

    Raises:
        NotesError: The English leaves a fence or a comment open (check_english), or the body is over GitHub's limit.
    """
    check_english(english)
    if not translations:
        body = english_notes(english)
    else:
        tags = [translation.tag for translation in translations]
        body = f"{language_line(version, 'en', tags, catalogs)}\n\n{english_notes(english)}"
        for translation in translations:
            body += f"\n<!-- release-notes {translation.tag}\n{translation.block}\n-->\n"
    if len(body) > GITHUB_LIMIT:
        raise NotesError(f"The release body would have {len(body):,} characters; GitHub takes at most {GITHUB_LIMIT:,}.")
    return body


def read_notes(path):
    """Reads English notes from a file.

    Args:
        path: The file.

    Returns:
        The notes, trimmed.

    Raises:
        NotesError: It cannot be read, or holds nothing.
    """
    try:
        text = Path(path).read_text(encoding="utf-8-sig")
    except (OSError, UnicodeDecodeError) as error:
        raise NotesError(f"{path}: cannot be read as UTF-8 ({error}).") from error
    if not text.strip():
        raise NotesError(f"{path}: holds no notes.")
    return text.strip()


def write(path, text):
    """Writes a text file as UTF-8 without a byte order mark, with LF line ends.

    Args:
        path: The file; its folder is created.
        text: The text.
    """
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    with open(path, "w", encoding="utf-8", newline="\n") as stream:
        stream.write(text)


def main(argv=None):
    """Runs one command (see the module's docstring).

    Args:
        argv: The arguments, without the program's name; None for the command line's.

    Returns:
        The exit code: 0 done; 1 a translation may not ship, the English leaves a fence or a comment open, or the body is
        too long; 2 bad arguments or an unreadable file.
    """
    for stream in (sys.stdout, sys.stderr):
        try:
            stream.reconfigure(encoding="utf-8", errors="replace")
        except (AttributeError, ValueError):
            pass

    parser = argparse.ArgumentParser(description="Release notes in several languages: stamp, check, compose.")
    commands = parser.add_subparsers(dest="command", required=True)
    stamp = commands.add_parser("stamp", help="print the stamp line for a translation of these English notes")
    stamp.add_argument("notes")
    check = commands.add_parser("check", help="check every translation of a release against these English notes")
    check.add_argument("--version", required=True)
    check.add_argument("notes")
    build = commands.add_parser("compose", help="write the release body, the English notes and each translation's notes")
    build.add_argument("--version", required=True)
    build.add_argument("--notes", required=True)
    build.add_argument("--body", required=True)
    build.add_argument("--english", required=True)
    build.add_argument("--store-dir")
    arguments = parser.parse_args(argv)

    version = getattr(arguments, "version", None)
    if version is not None and not re.match(r"^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$", version):
        print(f"'{version}' is not a version (x.y.z, without the v).", file=sys.stderr)
        return 2
    try:
        english = read_notes(arguments.notes)
    except NotesError as error:
        print(error, file=sys.stderr)
        return 2

    if arguments.command == "stamp":
        print(stamp_line(english))
        return 0

    try:
        translations = read_translations(version, english)
        body = compose(english, version, translations)
    except NotesError as error:
        print(error, file=sys.stderr)
        return 1

    if arguments.command == "check":
        if not translations:
            print(f"BetterClipboard {version} has no translations (docs/release-notes/{version}/): its body is the English alone.")
        for translation in translations:
            print(f"{translation.tag}: translated from these notes; {len(translation.block):,} characters.")
        print(f"The release body: {len(body):,} characters of GitHub's {GITHUB_LIMIT:,}.")
        return 0

    write(arguments.body, body)
    write(arguments.english, english_notes(english))
    if arguments.store_dir:
        # Made even when empty: the Store step names the folder whatever the release holds.
        Path(arguments.store_dir).mkdir(parents=True, exist_ok=True)
        for translation in translations:
            write(Path(arguments.store_dir) / f"{translation.tag}.md", translation.notes + "\n")
    names = ", ".join(translation.tag for translation in translations) or "none"
    print(f"Release body of BetterClipboard {version}: {len(body):,} characters; translations: {names}.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
