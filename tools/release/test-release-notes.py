"""Tests of release_notes.py, the composer of a release's body in several languages (CLAUDE.md section 2.67).

What a release page and every update dialog show comes out of this composer, so it is run here the way the release job
runs it, against small releases in a temporary folder and against the repository's own translation files:

    a release without translations gets exactly the body every release had (notes, rule, footer);
    with a translation, the English's language line comes first and the translation follows in a hidden block that
    holds the file without its stamp, and the Store gets each translation's notes alone;
    a stale stamp, another language line, a "-->", "--!>" or "<!--", a missing footer link or rule, a right-to-left
    language, a file of no catalog's name, English that leaves a code fence or an HTML comment open, and a body over
    GitHub's limit are refused, with exit code 1 and nothing written; a stamp survives the whitespace git's
    --cleanup=whitespace changes;
    every translation file in docs/release-notes is well formed.

No network and nothing outside a temporary folder. Usage:  python tools/release/test-release-notes.py
Exit code 0 when every check passes, 1 otherwise (each failed check is printed with what was found).
"""

import contextlib
import io
import sys
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))

import release_notes  # noqa: E402  (after the path line above, on purpose)

failures = []
checks = 0

# The English notes of the sample release, as a tag's message holds them.
ENGLISH = """BetterClipboard 9.9.9

```powershell
irm https://raw.githubusercontent.com/Nucs/BetterClipboard/main/install.ps1 | iex
```

**A new tab.** It lists what you asked for.
- One detail.

**Fixed**
- A thing that was broken."""

# The Chinese notes and footer of the sample, as the translator writes them under the language line and the stamp.
CHINESE_CONTENT = """BetterClipboard 9.9.9

```powershell
irm https://raw.githubusercontent.com/Nucs/BetterClipboard/main/install.ps1 | iex
```

**新标签页。** 列出你要的内容。
- 一个细节。

**修复**
- 修好了一处问题。

---

**代码签名政策：** 请参阅 [README](https://github.com/Nucs/BetterClipboard/blob/main/docs/translations/README.zh-CN.md#code-signing-policy)。"""

VERSION = "9.9.9"


def check(name, condition, detail=""):
    """Counts one check and prints it when it fails.

    Args:
        name: What is checked, in a sentence.
        condition: Whether it holds.
        detail: What was found, printed when it does not hold.
    """
    global checks
    checks += 1
    if not condition:
        failures.append(name)
        print(f"FAIL  {name}\n      {detail}")


def chinese_file(english=ENGLISH, content=CHINESE_CONTENT, line=None, stamp=None):
    """The text of a zh-Hans translation file of the sample release.

    Args:
        english: The English it claims to be translated from (its stamp).
        content: Its notes and footer.
        line: Its language line; None for the right one.
        stamp: Its stamp line; None for the one of english.

    Returns:
        The file's text.
    """
    line = line if line is not None else release_notes.language_line(VERSION, "zh-Hans", ["zh-Hans"])
    stamp = stamp if stamp is not None else release_notes.stamp_line(english)
    return f"{line}\n\n{stamp}\n\n{content}\n"


def run(folder, files, english=ENGLISH, store=True):
    """Runs `compose` the way the release job does, on a release whose translation files are given.

    Args:
        folder: A temporary folder for the release's files and the outputs.
        files: {file name: text} for docs/release-notes/9.9.9/.
        english: The English notes (the tag's message).
        store: Whether to ask for the Store's files too.

    Returns:
        (exit code, the printed text, {output name: text or None}).
    """
    folder = Path(folder)
    translations = folder / "docs" / "release-notes" / VERSION
    translations.mkdir(parents=True, exist_ok=True)
    for name, text in files.items():
        (translations / name).write_text(text, encoding="utf-8", newline="\n")
    notes = folder / "notes.md"
    notes.write_text(english, encoding="utf-8", newline="\n")
    body, plain, store_dir = folder / "out" / "body.md", folder / "out" / "english.md", folder / "out" / "store"
    arguments = ["compose", "--version", VERSION, "--notes", str(notes), "--body", str(body), "--english", str(plain)]
    if store:
        arguments += ["--store-dir", str(store_dir)]

    saved = release_notes.TRANSLATIONS_ROOT
    release_notes.TRANSLATIONS_ROOT = folder / "docs" / "release-notes"
    printed = io.StringIO()
    try:
        with contextlib.redirect_stdout(printed), contextlib.redirect_stderr(printed):
            code = release_notes.main(arguments)
    finally:
        release_notes.TRANSLATIONS_ROOT = saved

    def read(path):
        return path.read_text(encoding="utf-8") if path.exists() else None

    outputs = {"body": read(body), "english": read(plain), "zh-Hans": read(store_dir / "zh-Hans.md")}
    return code, printed.getvalue(), outputs


def test_stamps():
    """A stamp is the same for every spelling of the same notes that git's whitespace cleanup makes equal."""
    stamp = release_notes.stamp_of(ENGLISH)
    variants = {
        "Windows line ends": ENGLISH.replace("\n", "\r\n"),
        "trailing blanks": ENGLISH.replace("\n", "  \n"),
        "blank lines around": "\n\n" + ENGLISH + "\n\n\n",
        "doubled blank lines": ENGLISH.replace("\n\n", "\n\n\n"),
        "a byte order mark": "\ufeff" + ENGLISH,
    }
    for name, text in variants.items():
        check(f"the stamp ignores {name}", release_notes.stamp_of(text) == stamp, release_notes.stamp_of(text))
    check("a changed word changes the stamp", release_notes.stamp_of(ENGLISH.replace("tab", "tabs")) != stamp)
    check("the stamp line is a comment with 64 hex digits", release_notes.STAMP.match(release_notes.stamp_line(ENGLISH)) is not None,
          release_notes.stamp_line(ENGLISH))


def test_language_lines():
    """The language line names each language by its own name, the shown one bold, the others links to their pages."""
    english = release_notes.language_line(VERSION, "en", ["zh-Hans"])
    check("the English line", english == "**ENG** / [简体中文](https://github.com/Nucs/BetterClipboard/blob/v9.9.9/docs/release-notes/9.9.9/zh-Hans.md)",
          english)
    chinese = release_notes.language_line(VERSION, "zh-Hans", ["zh-Hans"])
    check("the Chinese line", chinese == "[ENG](https://github.com/Nucs/BetterClipboard/releases/tag/v9.9.9) / **简体中文**", chinese)
    many = release_notes.language_line(VERSION, "en", ["ru", "es", "zh-Hans"])
    check("several languages in the README's order", many.index("简体中文") < many.index("Русский") < many.index("Español"), many)


def test_compose():
    """The body without translations is the old one; with one, the line, the English, the footer and the hidden block."""
    with tempfile.TemporaryDirectory() as folder:
        code, printed, outputs = run(folder, {})
        expected = f"{ENGLISH}\n\n---\n\n{release_notes.FOOTER}\n"
        check("no translations: exit code 0", code == 0, printed)
        check("no translations: the body every release had", outputs["body"] == expected, outputs["body"])
        check("no translations: the English file is the same text", outputs["english"] == expected, outputs["english"])

    with tempfile.TemporaryDirectory() as folder:
        code, printed, outputs = run(folder, {"zh-Hans.md": chinese_file()})
        body = outputs["body"] or ""
        chinese_line = release_notes.language_line(VERSION, "zh-Hans", ["zh-Hans"])
        expected = (f"{release_notes.language_line(VERSION, 'en', ['zh-Hans'])}\n\n{ENGLISH}\n\n---\n\n{release_notes.FOOTER}\n"
                    f"\n<!-- release-notes zh-Hans\n{chinese_line}\n\n{CHINESE_CONTENT}\n-->\n")
        check("a translation: exit code 0", code == 0, printed)
        check("a translation: the line, the English, the footer, then the hidden block", body == expected, body)
        check("the block holds no stamp", "sha256" not in body, body)
        check("the Store's English is the English with its footer, without the line", outputs["english"] == f"{ENGLISH}\n\n---\n\n{release_notes.FOOTER}\n",
              outputs["english"])
        store_notes = CHINESE_CONTENT.split("\n---\n")[0].strip() + "\n"
        check("the Store's Chinese is the notes alone", outputs["zh-Hans"] == store_notes, outputs["zh-Hans"])
        check("the summary names the translation", "zh-Hans" in printed, printed)

    with tempfile.TemporaryDirectory() as folder:
        code, printed, outputs = run(folder, {"zh-Hans.md": chinese_file().replace("\n", "\r\n")})
        check("a file with Windows line ends composes the same block", code == 0 and "\r" not in (outputs["body"] or "\r"), printed)


def test_refusals():
    """Everything that may not ship is refused with exit code 1 and the file's name, and nothing is written."""
    stale = chinese_file(english=ENGLISH + "\n- One more fix.")
    cases = {
        "a stale stamp": ({"zh-Hans.md": stale}, "translated from other English notes"),
        "no stamp": ({"zh-Hans.md": chinese_file(stamp="BetterClipboard 9.9.9")}, "stamp"),
        "another language line": ({"zh-Hans.md": chinese_file(line="**简体中文**")}, "language line"),
        "a comment's end in the notes": ({"zh-Hans.md": chinese_file(content=CHINESE_CONTENT.replace("一个细节", "a --> b"))}, "-->"),
        "GitHub's other comment end": ({"zh-Hans.md": chinese_file(content=CHINESE_CONTENT.replace("一个细节", "a --!> b"))}, "--!>"),
        "a comment inside": ({"zh-Hans.md": chinese_file(content=CHINESE_CONTENT.replace("一个细节", "<!-- x"))}, "<!--"),
        "no footer link": ({"zh-Hans.md": chinese_file(content=CHINESE_CONTENT.replace("#code-signing-policy", "#license"))}, "code-signing-policy"),
        "no rule before the footer": ({"zh-Hans.md": chinese_file(content=CHINESE_CONTENT.replace("\n---\n", "\n"))}, "---"),
        "no notes": ({"zh-Hans.md": chinese_file(content="---\n\n**代码签名政策：** [README](https://x/#code-signing-policy)")}, "no notes"),
        "a right-to-left language": ({"he.md": chinese_file()}, "right-to-left"),
        "a name that is no catalog tag": ({"zh_Hans.md": chinese_file()}, "not a translation's name"),
        "a language without a catalog": ({"xx.md": chinese_file()}, "no catalog"),
        "English as a file": ({"en.md": chinese_file()}, "not a translation's name"),
        "a body over GitHub's limit": ({"zh-Hans.md": chinese_file(content=CHINESE_CONTENT.replace("一个细节", "细" * 130_000))}, "at most"),
    }
    for name, (files, says) in cases.items():
        with tempfile.TemporaryDirectory() as folder:
            code, printed, outputs = run(folder, files)
            check(f"{name}: refused with exit code 1", code == 1, f"exit {code}: {printed}")
            check(f"{name}: the message says why", says in printed, printed)
            check(f"{name}: nothing is written", outputs["body"] is None and outputs["english"] is None, str(outputs))

    # English that leaves a fence or a comment open would draw the footer and the blocks as code, or hide them: refused
    # with a translation stamped from that very English (so the stamp is not what fails) and without any translation.
    open_english = {
        "an English fence left open": (ENGLISH + "\n\n```text\nsome code", "code fence"),
        "an English tilde fence closed by backticks": (ENGLISH + "\n\n~~~\ncode\n```", "code fence"),
        "an English comment left open": (ENGLISH + "\n\n<!-- a note to self\nmore", "HTML comment"),
    }
    for name, (english, says) in open_english.items():
        for files in ({"zh-Hans.md": chinese_file(english=english)}, {}):
            label = f"{name} ({'with' if files else 'without'} a translation)"
            with tempfile.TemporaryDirectory() as folder:
                code, printed, outputs = run(folder, files, english=english)
                check(f"{label}: refused with exit code 1", code == 1, f"exit {code}: {printed}")
                check(f"{label}: the message says why", says in printed, printed)
                check(f"{label}: nothing is written", outputs["body"] is None, str(outputs))

    # What the parser closes is fine: a comment on one line, one over several lines, a fence closed by a longer fence,
    # and a fence inside a comment (hidden with it).
    closed_english = ENGLISH + "\n\n<!-- one line -->\n\n<!-- several\nlines -->\n\n````\ncode\n`````\n\n<!-- x\n```\n-->"
    with tempfile.TemporaryDirectory() as folder:
        code, printed, outputs = run(folder, {"zh-Hans.md": chinese_file(english=closed_english)}, english=closed_english)
        check("English that closes what it opens composes", code == 0 and outputs["body"] is not None, printed)


def test_commands():
    """stamp prints the line, check passes a good file and fails a stale one, and bad arguments exit with 2."""
    with tempfile.TemporaryDirectory() as folder:
        notes = Path(folder) / "notes.md"
        notes.write_text(ENGLISH, encoding="utf-8")
        printed = io.StringIO()
        with contextlib.redirect_stdout(printed):
            code = release_notes.main(["stamp", str(notes)])
        check("stamp prints the stamp line", code == 0 and printed.getvalue().strip() == release_notes.stamp_line(ENGLISH), printed.getvalue())

        root = Path(folder) / "docs" / "release-notes"
        (root / VERSION).mkdir(parents=True)
        (root / VERSION / "zh-Hans.md").write_text(chinese_file(), encoding="utf-8")
        saved = release_notes.TRANSLATIONS_ROOT
        release_notes.TRANSLATIONS_ROOT = root
        try:
            printed = io.StringIO()
            with contextlib.redirect_stdout(printed), contextlib.redirect_stderr(printed):
                code = release_notes.main(["check", "--version", VERSION, str(notes)])
            check("check passes a translation of these notes", code == 0 and "zh-Hans: translated from these notes" in printed.getvalue(), printed.getvalue())
            notes.write_text(ENGLISH + "\n\n**One more.** Change.", encoding="utf-8")
            printed = io.StringIO()
            with contextlib.redirect_stdout(printed), contextlib.redirect_stderr(printed):
                code = release_notes.main(["check", "--version", VERSION, str(notes)])
            check("check fails once the English changed, and prints the new stamp", code == 1 and release_notes.stamp_line(notes.read_text(encoding="utf-8")) in printed.getvalue(),
                  printed.getvalue())
        finally:
            release_notes.TRANSLATIONS_ROOT = saved

        for name, arguments in {
            "a version with its v": ["check", "--version", "v9.9.9", str(notes)],
            "notes that do not exist": ["stamp", str(Path(folder) / "missing.md")],
        }.items():
            printed = io.StringIO()
            with contextlib.redirect_stdout(printed), contextlib.redirect_stderr(printed):
                code = release_notes.main(arguments)
            check(f"{name}: exit code 2", code == 2, f"exit {code}: {printed.getvalue()}")


def test_repository_files():
    """Every translation file in the repository is well formed (its stamp is checked by the release job against the tag)."""
    root = release_notes.TRANSLATIONS_ROOT
    folders = sorted(path for path in root.iterdir() if path.is_dir()) if root.is_dir() else []
    for folder in folders:
        try:
            translations = release_notes.read_translations(folder.name)
            check(f"docs/release-notes/{folder.name}: every file reads", len(translations) > 0, "no files")
            for translation in translations:
                check(f"docs/release-notes/{folder.name}/{translation.tag}.md: has notes for the Store", len(translation.notes) > 100,
                      f"{len(translation.notes)} characters")
        except release_notes.NotesError as error:
            check(f"docs/release-notes/{folder.name}: every file reads", False, str(error))


def main():
    """Runs every test and reports.

    Returns:
        The exit code: 0 when every check passed, 1 otherwise.
    """
    for test in (test_stamps, test_language_lines, test_compose, test_refusals, test_commands, test_repository_files):
        test()
    if failures:
        print(f"\n{len(failures)} of {checks} checks failed.")
        return 1
    print(f"release_notes.py composes and refuses what it should: {checks} checks passed.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
