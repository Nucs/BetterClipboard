"""Checks that the README's translations in docs/translations still match the English README.md.

Usage:
    python tools/readme/check-translations.py [--ref REF] [--offline] [FILE ...]

Every README in docs/translations is a full translation of README.md, and a change to README.md must reach each of
them in the same commit (CLAUDE.md, section 4). A translation that misses a change still looks complete to anyone who
cannot read its language, so this tool compares structure, not words:

1. Sections. A translation keeps the English section ids: an <a id="..."> line before each heading, equal to the
   GitHub anchor of the English heading, in the same order.
2. Each section's skeleton. The same number of list items at each depth, table rows, paragraphs and images, the same
   link targets (relative paths are compared as README.md writes them; links between the README's languages count
   as one), and the same commands in its code blocks, line by line (comments are translated, so they are left out).
3. GitHub's rendering (skipped with --offline). GitHub's own renderer (the REST API's /markdown, in the mode README
   files are rendered in; it only returns HTML, nothing is published or stored) must give the same tables with the
   same rows, the same number of list items and code blocks, and leave no more `**` or `](` unrendered than in
   README.md. It is asked with the GitHub CLI's stored sign-in when there is one (5,000 requests an hour), else
   without (60 an hour, shared with every other program at the same address).
4. Right-to-left translations, the ones that wrap their text in <div dir="rtl">. A README page gives each paragraph,
   heading and list dir="auto", which follows the first strong letter of the block, and lays each block out on its
   own:
   a. Block direction (needs the rendering): a block that holds right-to-left text must not start with a Latin
      letter, or GitHub lays it out left to right. A list takes its direction from its first item. Put &rlm; first.
   b. Code spans: each one has &lrm; on both sides, or a span that starts or ends with a sign (`-NoTakeOverWinV`,
      `%LOCALAPPDATA%`) shows that sign at its other end.
   c. Trailing signs: a run of + or # after a Latin letter (Notepad++, C#, the "Win+" of "Win+letter") needs &lrm;
      right after it when right-to-left text or the end of its block (its line, or its table cell) comes before any
      Latin letter, digit or &lrm;: these take the signs to the word's other side ("++Notepad"). "Win+V", "Win+1" and
      "A+ (&lrm;`Ctrl+Plus`" need none. The app marks its own texts the same way (UiText.KeepTrailingSignsWithTheirWord).
5. Marks as entities, in every translation: no raw U+200E, U+200F or U+061C. Written out, a mark is invisible in an
   editor and in a diff, so nobody sees it to keep it, move it or remove it; write &lrm; and &rlm;.

FILE defaults to every docs/translations/README.*.md. --ref reads README.md and the translations from git instead of
the working tree: --ref HEAD for the last commit, --ref : for the index (what the next commit holds, without anybody's
unstaged edits; stage the translations first).

Exit codes: 0 every file passes; 1 a check failed; 2 a file could not be read, or GitHub's renderer could not be
reached (run with --offline to skip its checks).
"""
import argparse
import html
import http.client
import json
import os
import re
import subprocess
import sys
import urllib.error
import urllib.request
from collections import Counter
from html.parser import HTMLParser
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]

# The bidirectional marks and the right-to-left letters are built with chr(): written out they are invisible in an
# editor and a diff, and the editing tools used in this repository have turned \u escapes into raw characters before.
LRM, RLM, ALM, BOM = chr(0x200E), chr(0x200F), chr(0x061C), chr(0xFEFF)
RIGHT_TO_LEFT = re.compile("[" + "".join(chr(first) + "-" + chr(last) for first, last in (
    (0x0590, 0x08FF),  # Hebrew, Arabic, Syriac, Thaana, NKo, Samaritan, Mandaic, Arabic Extended-A
    (0xFB1D, 0xFDFF),  # Hebrew and Arabic presentation forms A
    (0xFE70, 0xFEFC),  # Arabic presentation forms B
)) + "]")
# A list item ("- ", "* ", "1. ") or the indented continuation of one.
LIST_LINE = re.compile(r"\s*(?:[-*]|\d+\.)\s|  \S")
# An inline code span between single backticks (README.md has no double-backtick spans).
CODE_SPAN = re.compile(r"(?<!`)`[^`\n]+`(?!`)")
# A link's target, "](...)": not shown, so a "#" in it ("README.md#privacy") is no sign of a word.
LINK_TARGET = re.compile(r"\]\([^)\s]*\)")
# A run of + or # after a Latin letter, with the word it ends: Notepad++, C#, and the "Win+" of "Win+V".
TRAILING_SIGNS = re.compile(r"[A-Za-z0-9]*[A-Za-z][+#]+")
# Every README's language line starts with this, and is meant to read left to right in every language.
LANGUAGE_LINE = "ENG /"


def git(*args):
    """Runs git in the repository and returns what it printed.

    Args:
        *args: git's arguments.

    Returns:
        git's standard output, decoded as UTF-8.

    Raises:
        subprocess.CalledProcessError: git failed, e.g. for an unknown revision or a file that is not in it.
        FileNotFoundError: git is not on the PATH.
    """
    return subprocess.run(["git", "-C", str(REPO), *args], capture_output=True, check=True).stdout.decode("utf-8")


def read(path, ref):
    """Returns a file of the repository as text with LF line endings.

    Args:
        path: The file's path relative to the repository root, with forward slashes.
        ref: None for the working tree, ":" for the index, or a revision such as "HEAD".

    Returns:
        The file's text, without a byte order mark.

    Raises:
        OSError: The file is not in the working tree, or cannot be read.
        UnicodeDecodeError: The file is not UTF-8.
        subprocess.CalledProcessError: The file is not in the index or in the revision.
    """
    if ref is None:
        text = (REPO / path).read_text(encoding="utf-8")
    else:
        # ":path" names a file in the index, "REV:path" a file in a commit.
        text = git("show", (":" if ref == ":" else ref + ":") + path)
    # A translation may be checked out with CRLF (core.autocrlf), and an editor may add a byte order mark: neither is
    # content, and either would make a line compare differently from README.md's.
    return text.replace("\r\n", "\n").lstrip(BOM)


def translations(ref):
    """Lists the README translations in docs/translations.

    Args:
        ref: None for the working tree, ":" for the index, or a revision such as "HEAD".

    Returns:
        Their paths relative to the repository root, with forward slashes, sorted.

    Raises:
        subprocess.CalledProcessError: git failed, e.g. for an unknown revision.
        FileNotFoundError: git is not on the PATH.
    """
    if ref is None:
        names = [p.relative_to(REPO).as_posix() for p in (REPO / "docs" / "translations").glob("README.*.md")]
    elif ref == ":":
        names = git("ls-files", "--", "docs/translations").split("\n")
    else:
        names = git("ls-tree", "-r", "--name-only", ref, "--", "docs/translations").split("\n")
    return sorted(name for name in names if re.fullmatch(r"docs/translations/README\.[^/]+\.md", name))


def github_anchor(heading):
    """The anchor GitHub gives a heading: tags dropped, lower case, every character but letters, digits, spaces,
    hyphens and underscores removed, spaces turned into hyphens.

    Args:
        heading: The heading's text after its # marks.

    Returns:
        The anchor, without its #.
    """
    text = re.sub(r"<[^>]+>", "", heading).strip().lower()
    return re.sub(r"[^\w\- ]", "", text).replace(" ", "-")


def sections(text, translated):
    """Splits a README into its sections, each named by the anchor a link to it uses.

    Args:
        text: The README's text.
        translated: False for README.md, whose sections are named by the GitHub anchors of its headings. True for a
            translation, whose sections are named by the <a id="..."> line before each heading: its headings are in
            another language, so their own anchors say nothing about which English section they translate.

    Returns:
        A dict from section name to the section's lines without its heading, in document order. The lines before the
        first heading are "(top)". Only headings of levels 2 to 4 start a section; the title (level 1) is in "(top)".
        A translation's heading without an <a id> line before it stays a line of the section above it, so that
        section's name is then missing.
    """
    out, name, pending, seen, fenced = {"(top)": []}, "(top)", None, Counter(), False
    for line in text.split("\n"):
        if line.lstrip().startswith("```"):
            fenced = not fenced
        anchor = None if fenced else re.fullmatch(r'<a id="([^"]+)"></a>', line.strip())
        heading = None if fenced else re.match(r"#{2,4} (.*)$", line)
        if translated and anchor:
            pending = anchor.group(1)
            continue
        if heading and not translated:
            base = github_anchor(heading.group(1))
            # GitHub numbers a repeated anchor: a second "Limits" heading is #limits-1.
            name = f"{base}-{seen[base]}" if seen[base] else base
            seen[base] += 1
            out[name] = []
            continue
        if heading and pending:
            name, pending = pending, None
            out[name] = []
            continue
        out[name].append(line)
    return out


def normalize_link(target):
    """Writes a link target the way README.md writes it, so the targets of the two files compare equal.

    Args:
        target: A link or image target, as README.md or a translation in docs/translations writes it.

    Returns:
        The target relative to the repository root. A link to any language version of the README becomes
        "(a README)": each version links to all the others.
    """
    if target.startswith("../../"):
        target = target[len("../../"):]
    elif target.startswith("../"):
        target = "docs/" + target[len("../"):]
    elif re.fullmatch(r"README\.[^/#]+\.md", target):
        target = "docs/translations/" + target
    if re.fullmatch(r"README\.md|docs/translations/README\.[^/#]+\.md", target):
        return "(a README)"
    return target


def facts(lines):
    """Counts what a section must keep in every language.

    Args:
        lines: The section's lines, without its heading.

    Returns:
        A dict: "list items" (from depth, 0 for an outer list, to count), "table rows", "paragraphs", "images" and
        "links" (normalized targets), and "commands" (the lines of its code blocks, without comments).
    """
    body = "\n".join(lines)
    blocks = re.findall(r"```[^\n]*\n(.*?)```", body, re.S)
    prose = re.sub(r"```.*?```", "", body, flags=re.S)
    items, rows, paragraphs, in_paragraph = Counter(), 0, 0, False
    for line in prose.split("\n"):
        item = re.match(r"( *)(?:[-*]|\d+\.) ", line)
        if item:
            # README.md indents a nested list by two spaces: depth 1 is a list inside an item.
            items[len(item.group(1)) // 2] += 1
        if line.startswith("|"):
            rows += 1
        # A paragraph is a run of lines that are neither blank, a list item, an item's indented continuation, a table
        # row, HTML nor a quote. The title and the badges count too, in every language alike.
        plain = bool(line.strip()) and not item and not line.startswith(("|", "<", ">", "  "))
        if plain and not in_paragraph:
            paragraphs += 1
        in_paragraph = plain
    links = re.findall(r"\]\(([^)\s]+)", prose) + re.findall(r'href="([^"]+)"', prose)
    images = re.findall(r'<img\b[^>]*\bsrc="([^"]+)"', prose) + re.findall(r"!\[[^\]]*\]\(([^)\s]+)", prose)
    commands = []
    for block in blocks:
        for line in block.split("\n"):
            # Comments are translated; what runs must stay as it is.
            line = re.sub(r"(^|\s)#.*$", "", line).strip()
            if line:
                commands.append(line)
    return {"list items": dict(sorted(items.items())), "table rows": rows, "paragraphs": paragraphs,
            "images": sorted(normalize_link(image) for image in images),
            "links": sorted({normalize_link(link) for link in links}), "commands": commands}


def clip(value, width=150):
    """Shortens a value's repr for a report line.

    Args:
        value: Any value.
        width: The most characters to keep.

    Returns:
        The repr, cut with "..." when longer than width.
    """
    text = repr(value)
    return text if len(text) <= width else text[:width - 3] + "..."


def difference(english, here):
    """Words how one fact of a translation differs from README.md's.

    Args:
        english: The fact in README.md: a number, a dict of counts, or a list.
        here: The same fact in the translation.

    Returns:
        A short description: the two values, or for a list what only one of them has.
    """
    if isinstance(english, list):
        only_english = list((Counter(english) - Counter(here)).elements())
        only_here = list((Counter(here) - Counter(english)).elements())
        if not only_english and not only_here:
            return "the same, in another order"
        return f"only in README.md {clip(only_english)}; only here {clip(only_here)}"
    return f"README.md {english}, here {here}"


def compare_structure(english_sections, text):
    """Checks 1 and 2 of the module's description: the sections, and each section's skeleton.

    Args:
        english_sections: README.md, split by sections().
        text: The translation's text.

    Returns:
        The findings, one sentence each; empty when nothing differs.
    """
    findings = []
    translated = sections(text, translated=True)
    missing = [name for name in english_sections if name not in translated]
    extra = [name for name in translated if name not in english_sections]
    if missing or extra:
        findings.append(f"sections: missing {missing}; not in README.md {extra}")
    elif list(translated) != list(english_sections):
        findings.append("sections: in another order than README.md's")
    for name, lines in english_sections.items():
        if name in translated:
            english, here = facts(lines), facts(translated[name])
            for key in english:
                if english[key] != here[key]:
                    findings.append(f"#{name}: {key} differ: {difference(english[key], here[key])}")
    return findings


def first_strong(text, after_latin=False):
    """The direction of the first strong character of a text: what dir="auto" gives a block that starts with it
    (rules P2 and P3 of Unicode's bidirectional algorithm), or which way the neutral signs before it resolve.

    Args:
        text: Visible text, entities decoded.
        after_latin: True when the text follows a Latin letter. A European digit then counts as left to right, as the
            algorithm resolves it (rule W7: a digit takes the direction of the strong letter before it).

    Returns:
        "R" for a right-to-left letter or mark, "L" for any other letter or a left-to-right mark (or such a digit),
        None for neither.
    """
    for ch in text:
        if ch in (RLM, ALM) or RIGHT_TO_LEFT.match(ch):
            return "R"
        if ch == LRM or ch.isalpha() or (after_latin and "0" <= ch <= "9"):
            return "L"
    return None


def visible(markdown):
    """The text a reader sees of a piece of one Markdown line, close enough to find its first strong character: link
    targets, emphasis marks, brackets and backticks removed, entities decoded. Not a renderer.

    Args:
        markdown: A piece of one line of Markdown.

    Returns:
        Its visible text.
    """
    text = re.sub(r"\]\([^)]*\)", "", markdown)
    return html.unescape(re.sub(r"[*`\[\]]", "", text))


def check_marks(text):
    """Checks 4b and 4c of the module's description, which need no rendering: the left-to-right marks around code
    spans and after a Latin word's trailing signs.

    Args:
        text: A right-to-left translation's text.

    Returns:
        The findings, one sentence each, naming the line.
    """
    findings, fenced = [], False
    for number, line in enumerate(text.split("\n"), 1):
        if line.lstrip().startswith("```"):
            fenced = not fenced
            continue
        if fenced:
            continue
        spans = [(span.start(), span.end()) for span in CODE_SPAN.finditer(line)]
        for start, end in spans:
            if not (line[:start].endswith("&lrm;") and line[end:].startswith("&lrm;")):
                findings.append(f"line {number}: {line[start:end]} needs &lrm; on both sides")
        # Only lines laid out right to left reorder trailing signs: lines with right-to-left text, table rows (the
        # <div dir="rtl"> reaches tables, which get no dir="auto"), and list items (a list takes its direction from its
        # first item, which check 4a keeps right to left).
        if not (RIGHT_TO_LEFT.search(line) or line.startswith("|") or LIST_LINE.match(line)):
            continue
        hidden = spans + [(target.start(), target.end()) for target in LINK_TARGET.finditer(line)]
        for word in TRAILING_SIGNS.finditer(line):
            if any(start <= word.start() < end for start, end in hidden):
                continue  # code keeps its own text (its marks are outside it); a link's target is not shown
            rest = line[word.end():]
            if line.startswith("|"):
                rest = rest.split("|", 1)[0]  # a table cell is a block of its own: it ends at the next |
            # The signs stay with their word only when the next strong character reads left to right: a Latin
            # letter, a digit or &lrm;. Right-to-left text, or the end of the block, takes them to the other side.
            if first_strong(visible(rest), after_latin=True) != "L":
                findings.append(f"line {number}: {word.group(0)} needs &lrm; right after it")
    return findings


def check_raw_marks(text):
    """Check 5 of the module's description: the bidirectional marks are written as entities.

    Args:
        text: A translation's text.

    Returns:
        The findings, one per line that holds a raw mark.
    """
    names = {LRM: "U+200E (write &lrm;)", RLM: "U+200F (write &rlm;)", ALM: "U+061C (write &#x61C;)"}
    findings = []
    for number, line in enumerate(text.split("\n"), 1):
        found = [name for mark, name in names.items() if mark in line]
        if found:
            findings.append(f"line {number}: a raw, invisible mark: {', '.join(found)}")
    return findings


def github_token():
    """The token of the GitHub CLI's stored sign-in, which raises the API's limit from 60 requests an hour (per
    address, shared with every other program that asks without one) to 5,000. Each run of this tool makes one request
    per file plus one, so a few runs in a row can use the anonymous hour up.

    GH_TOKEN and GITHUB_TOKEN are hidden from gh while it is asked: gh prefers them to its stored sign-in, and a token
    left in a shell's environment can be stale or meant for another account.

    Returns:
        The token, or None when gh is not installed, not signed in, or does not answer.
    """
    environment = {key: value for key, value in os.environ.items() if key not in ("GH_TOKEN", "GITHUB_TOKEN")}
    try:
        result = subprocess.run(["gh", "auth", "token"], env=environment, capture_output=True, check=True, timeout=30)
    except (OSError, subprocess.SubprocessError):
        return None
    return result.stdout.decode("utf-8").strip() or None


def render(text, token=None):
    """Renders a README the way GitHub renders README files, through the REST API's /markdown. It only returns HTML;
    nothing is published or stored.

    Args:
        text: The README's text.
        token: A GitHub token for a higher rate limit (github_token()), or None to ask without signing in. The
            renderer needs no rights, so a token GitHub refuses (expired, revoked) is dropped and the request repeated
            without one.

    Returns:
        The HTML.

    Raises:
        OSError: The request failed: no network, a timeout, a refusal or a rate limit (urllib.error.URLError and
            urllib.error.HTTPError are OSErrors).
        http.client.HTTPException: The connection broke off in the middle of the answer.
    """
    headers = {"Accept": "application/vnd.github+json", "Content-Type": "application/json",
               "X-GitHub-Api-Version": "2022-11-28", "User-Agent": "BetterClipboard-check-translations"}
    if token:
        headers["Authorization"] = f"Bearer {token}"
    request = urllib.request.Request("https://api.github.com/markdown", headers=headers,
                                     data=json.dumps({"text": text, "mode": "markdown"}).encode("utf-8"))
    try:
        with urllib.request.urlopen(request, timeout=60) as response:
            return response.read().decode("utf-8")
    except urllib.error.HTTPError as error:
        if error.code != 401 or not token:
            raise
        return render(text, None)


def rendered_shape(rendered):
    """Reduces GitHub's HTML of a README to what every language must share.

    Args:
        rendered: The HTML.

    Returns:
        A dict: "tables" (each table's rows), "list items", "code blocks", and "unrendered" (how often ** and ]( are
        left in the HTML, which shows Markdown that GitHub did not understand).
    """
    tables = [len(re.findall(r"<tr\b", table)) for table in re.findall(r"<table\b.*?</table>", rendered, re.S)]
    return {"tables": tables, "list items": len(re.findall(r"<li\b", rendered)),
            "code blocks": len(re.findall(r"<pre\b", rendered)),
            "unrendered": (rendered.count("**"), rendered.count("]("))}


class Blocks(HTMLParser):
    """Collects the visible text of every block that a README page gives dir="auto": paragraphs, headings and lists. A
    list's text runs through all its items, so its first strong letter is its first item's."""

    TAGS = ("p", "h1", "h2", "h3", "h4", "h5", "h6", "ul", "ol")

    def __init__(self):
        """Starts with no blocks."""
        super().__init__(convert_charrefs=True)
        self.open, self.blocks = [], []

    def handle_starttag(self, tag, attrs):
        """Opens a block for a tag that gets dir="auto".

        Args:
            tag: The tag's name, in lower case.
            attrs: Its attributes; not used.
        """
        if tag in self.TAGS:
            self.open.append([tag, ""])

    def handle_endtag(self, tag):
        """Closes the innermost open block of this tag and keeps it as a (tag, text) pair.

        Args:
            tag: The tag's name, in lower case.
        """
        for index in range(len(self.open) - 1, -1, -1):
            if self.open[index][0] == tag:
                self.blocks.append(tuple(self.open.pop(index)))
                break

    def handle_data(self, data):
        """Adds text to every open block: a list's text includes its items' paragraphs and nested lists.

        Args:
            data: Text, with character references already decoded.
        """
        for block in self.open:
            block[1] += data


def check_rendering(english_html, translated_html, right_to_left):
    """Checks 3 and 4a of the module's description on GitHub's HTML.

    Args:
        english_html: GitHub's HTML of README.md.
        translated_html: GitHub's HTML of the translation.
        right_to_left: True when the translation wraps its text in <div dir="rtl">.

    Returns:
        The findings, one sentence each; empty when nothing differs.
    """
    findings = []
    english, here = rendered_shape(english_html), rendered_shape(translated_html)
    labels = {"tables": "rows of each table", "list items": "list items", "code blocks": "code blocks",
              "unrendered": "** and ]( left unrendered"}
    for key in english:
        if english[key] != here[key]:
            findings.append(f"GitHub's rendering differs ({labels[key]}): README.md {english[key]}, here {here[key]}")
    if right_to_left:
        parser = Blocks()
        parser.feed(translated_html)
        parser.close()
        for tag, text in parser.blocks:
            text = " ".join(text.split())
            if RIGHT_TO_LEFT.search(text) and first_strong(text) == "L" and not text.startswith(LANGUAGE_LINE):
                # The marks are dropped from the quote: printed, they would only show as escapes.
                shown = clip(text.replace(LRM, "").replace(RLM, ""), 100)
                findings.append(f"GitHub lays out this <{tag}> left to right (put &rlm; first): {shown}")
    return findings


def main(argv=None):
    """Checks the chosen translations and prints each one's findings.

    Args:
        argv: The command line without the program's name; None for sys.argv.

    Returns:
        The exit code: 0 when every file passes, 1 when a check fails, 2 when a file cannot be read or GitHub's
        renderer cannot be reached.
    """
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("files", nargs="*", metavar="FILE",
                        help="translations to check (default: every docs/translations/README.*.md)")
    parser.add_argument("--ref", metavar="REF",
                        help="read the files from git: HEAD (or any revision), or : for the index")
    parser.add_argument("--offline", action="store_true",
                        help="skip the checks that need GitHub's renderer")
    args = parser.parse_args(argv)
    # The translations hold Chinese, Cyrillic and Hebrew, which a Windows console's code page cannot print.
    sys.stdout.reconfigure(encoding="utf-8")
    try:
        files = [Path(name).resolve().relative_to(REPO).as_posix() for name in args.files] or translations(args.ref)
        english = read("README.md", args.ref)
        texts = {name: read(name, args.ref) for name in files}
    except subprocess.CalledProcessError as error:
        # git says what it could not find (an unknown revision, a file not in it); the command line says nothing.
        print(f"git could not read the files: {error.stderr.decode('utf-8', 'replace').strip()}")
        return 2
    except (OSError, UnicodeDecodeError, ValueError) as error:
        print(f"A file could not be read: {error}")
        return 2
    if not files:
        print("No translations found in docs/translations.")
        return 2
    english_sections = sections(english, translated=False)
    english_html = None
    token = None if args.offline else github_token()
    failed = False
    for name in files:
        text = texts[name]
        right_to_left = '<div dir="rtl">' in text
        findings = compare_structure(english_sections, text) + check_raw_marks(text)
        if right_to_left:
            findings += check_marks(text)
        if not args.offline:
            try:
                english_html = english_html or render(english, token)
                findings += check_rendering(english_html, render(text, token), right_to_left)
            except (OSError, http.client.HTTPException) as error:
                print(f"GitHub's renderer could not be reached ({error}); run with --offline to skip its checks.")
                return 2
        print(f"{name}: " + (f"{len(findings)} problem(s)" if findings else "ok"))
        for finding in findings:
            print(f"  {finding}")
        failed = failed or bool(findings)
    if args.offline:
        print("Skipped (--offline): GitHub's rendering, and the direction of each block of a right-to-left file.")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
