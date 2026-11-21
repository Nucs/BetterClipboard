"""Probe: what released BetterClipboard versions and GitHub's release page show of a release body that carries its
translations in hidden blocks.

The proposal (docs/release-notes-languages.md, CLAUDE.md section 2.54) keeps the English notes visible and puts each
translation into an HTML comment block of its own, after a blank line:

    <!-- release-notes zh-Hans
    ...the notes in Simplified Chinese, as Markdown...
    -->

Every version with the update dialog (0.2.6 and later) skips such a block: its Markdown parser jumps from a line that
starts with "<!--" to the next line that contains "-->". GitHub's renderer drops it too. So the release page and every
installed copy show the English notes, and a version that knows the blocks can show the one in its interface language.

The probe proves that for one body, with the code each released version runs:
  1. it reads src/BetterClipboard.Core/Presentation/Markdown.cs from every v* tag that has it (one build per distinct
     file, so v0.2.6 to v0.2.10 build once) and from HEAD;
  2. builds a tiny program around each with the .NET SDK, in a temporary folder that is deleted afterwards;
  3. parses the body and the same body without its blocks, and compares what the update dialog would draw;
  4. with --github, asks GitHub's renderer for both (mode gfm, which draws a release page's structure: its hard line
     breaks and blocks, checked on v0.2.11's page) and compares the HTML.
It also checks the blocks themselves: a "-->" inside one ends it early on GitHub and in every installed copy, and a
"--!>" ends it early on GitHub only; either puts the rest of that translation where it must not be.

Usage:
    python tools/probes/probe_release_notes.py                  # the built-in sample, through every released parser
    python tools/probes/probe_release_notes.py BODY.md --github  # a real body, also through GitHub's renderer
    python tools/probes/probe_release_notes.py --sample details  # what installed copies draw of <details> sections
    python tools/probes/probe_release_notes.py --sample leak     # what a "-->" or "--!>" in a translation does
    python tools/probes/probe_release_notes.py BODY.md --show    # also list every block each version draws

Exit code: 0 when every block is well formed and hidden everywhere it was checked; 1 when a block shows somewhere or
is malformed; 2 for bad arguments, a missing tool (git, dotnet), a failed build or a failed request to GitHub.

Needs git and the .NET 10 SDK; --github needs the network. GitHub's API allows 60 requests an hour without a token,
so the GitHub CLI's stored sign-in is used when there is one (two requests per body). Release notes are public text,
so the probe prints them.
"""

import argparse
import difflib
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import urllib.error
import urllib.request

PARSER_PATH = "src/BetterClipboard.Core/Presentation/Markdown.cs"
"""Where the update dialog's Markdown parser lives in every version that has the dialog (0.2.6 and later)."""

GITHUB_LIMIT = 125_000
"""The most characters GitHub accepts in a release body ("body is too long (maximum is 125000 characters)", 422)."""

REPOSITORY = "Nucs/BetterClipboard"
"""The repository whose context GitHub's renderer gets, as a release page of it would."""

OPENING = re.compile(r"^<!-- release-notes ([A-Za-z]{2,3}(?:-[A-Za-z0-9]{2,8})*)[ \t]*$")
"""The first line of a translation block: the marker and the language's catalog tag (zh-Hans, ru, es, he)."""

CLOSING = re.compile(r"^-->[ \t]*$")
"""The last line of a translation block: nothing but the comment's end."""

PROGRAM = r"""// Written by tools/probes/probe_release_notes.py into a temporary folder next to one released Markdown.cs. Prints,
// as JSON, what that version's update dialog draws of each release body named on the command line: one line per
// block, links written out, so two bodies can be compared block by block.
using System.Text;
using System.Text.Json;
using BetterClipboard.Core.Presentation;

var result = new List<object>();
foreach (string path in args)
{
    var lines = new List<string>();
    foreach (var block in MarkdownParser.Parse(File.ReadAllText(path, Encoding.UTF8)))
    {
        lines.Add(Describe(block));
    }

    result.Add(new { path, blocks = lines });
}

Console.OutputEncoding = new UTF8Encoding(false);
Console.WriteLine(JsonSerializer.Serialize(result));

// Quotes changed shape on 2026-11-20: up to v0.2.11 a quote is a paragraph with IsQuote, from then on a MarkdownQuote
// holding blocks of its own. Both are read by reflection, so one program builds against every version.
static string Describe(object block) => block switch
{
    MarkdownParagraph p => (p.GetType().GetProperty("IsQuote")?.GetValue(p) is true ? "quote: " : "paragraph: ") + Text(p.Inlines),
    MarkdownHeading h => $"heading {h.Level}: {Text(h.Inlines)}",
    MarkdownListItem li => $"item {li.Depth} {li.Marker} {Text(li.Inlines)}",
    MarkdownCodeBlock c => "code: " + c.Code.Replace("\n", " | "),
    MarkdownTable t => "table: " + string.Join(" || ", t.Rows.Select(r => string.Join(" | ", r.Cells.Select(Text)))),
    MarkdownRule => "rule",
    _ when block.GetType().Name == "MarkdownQuote" && block.GetType().GetProperty("Blocks")?.GetValue(block) is System.Collections.IEnumerable inner =>
        "quote: " + string.Join(" ¦ ", inner.Cast<object>().Select(Describe)),
    _ => block.GetType().Name,
};

// A run with a link is written [text](address), so a changed link target counts as a difference too.
static string Text(IReadOnlyList<MarkdownInline> runs) =>
    string.Concat(runs.Select(r => r.Link is null ? r.Text : $"[{r.Text}]({r.Link})")).Replace("\n", " / ");
"""
"""The program built around each released parser. It uses only what every version since 0.2.6 has: the block records
and their members, and MarkdownInline's Link."""

PROJECT = """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
</Project>
"""
"""The project file of that program."""

SAMPLES = {
    "hidden": """BetterClipboard 9.9.9

**ENG** / [简体中文](https://github.com/Nucs/BetterClipboard/blob/v9.9.9/docs/release-notes/9.9.9/zh-Hans.md) / [עברית](https://github.com/Nucs/BetterClipboard/blob/v9.9.9/docs/release-notes/9.9.9/he.md)

```powershell
irm https://raw.githubusercontent.com/Nucs/BetterClipboard/main/install.ps1 | iex
```

**BC-TEST faster.** Pasting no longer waits for the panel:
- `Enter` pastes at once.

**Fixed**
- BC-TEST: a re-run of the installer kept *Start with Windows* as you left it.

| Download | For |
|---|---|
| [`install.ps1`](https://github.com/Nucs/BetterClipboard/releases/download/v9.9.9/install.ps1) | The installer |

<!-- release-notes zh-Hans
BetterClipboard 9.9.9

```powershell
irm https://raw.githubusercontent.com/Nucs/BetterClipboard/main/install.ps1 | iex
```

**BC-TEST 更快。** 粘贴不再等待面板：
- `Enter` 立即粘贴。

**已修复**
- BC-TEST：再次运行安装程序会保留你设置的*随 Windows 启动*。

| 下载 | 用途 |
|---|---|
| [`install.ps1`](https://github.com/Nucs/BetterClipboard/releases/download/v9.9.9/install.ps1) | 安装程序 |
-->

<!-- release-notes he
BetterClipboard 9.9.9

**BC-TEST מהיר יותר.** ההדבקה כבר לא מחכה ללוח:
- &lrm;`Enter`&lrm; מדביק מיד.
-->

---

**Code signing policy:** see [the README](https://github.com/Nucs/BetterClipboard#code-signing-policy) for whether this release is signed, what is signed, and who approves it.
""",
    "details": """BetterClipboard 9.9.9

**BC-TEST faster.** Pasting no longer waits for the panel.

<details><summary>简体中文</summary>

**BC-TEST 更快。** 粘贴不再等待面板。

</details>

<!-- --><details><summary>Русский</summary>

**BC-TEST быстрее.** Вставка больше не ждёт панель.

<!-- --></details>
""",
    "leak": """BetterClipboard 9.9.9

**BC-TEST faster.** Pasting no longer waits for the panel.

<!-- release-notes ru
**BC-TEST быстрее.** Стрелка в тексте --> закрывает блок раньше времени.
**BC-TEST утечка.** Эту строку видят все.
-->

<!-- release-notes es
**BC-TEST más rápido.** Otra forma de cerrar --!> un comentario en HTML.
**BC-TEST fuga.** ¿Se ve esta línea?
-->

The last English line.
""",
}
"""Built-in bodies, BC-TEST text only: "hidden" is the proposed shape, "details" the visible-sections alternative that
installed copies cannot hide, "leak" two translations that end their block early."""


def run(command, cwd=None, check=True):
    """Runs a program and returns its standard output.

    Args:
        command: The program and its arguments.
        cwd: The working folder, or None for the current one.
        check: Whether a non-zero exit code raises.

    Returns:
        The output, decoded as UTF-8 (the probe's program and git both write UTF-8).

    Raises:
        subprocess.CalledProcessError: The program failed and check is true; its output is in the exception.
        OSError: The program could not be started.
    """
    result = subprocess.run(command, cwd=cwd, capture_output=True, check=check)
    return result.stdout.decode("utf-8", errors="replace")


def repository_root():
    """Finds the working tree this script belongs to.

    Returns:
        Its top folder.

    Raises:
        subprocess.CalledProcessError: The script is not inside a git working tree.
    """
    return run(["git", "rev-parse", "--show-toplevel"], cwd=os.path.dirname(os.path.abspath(__file__))).strip()


def released_parsers(root):
    """Lists the versions of the update dialog's parser: every v* tag that has one, and HEAD.

    Args:
        root: The repository's top folder.

    Returns:
        A list of (blob id, [names]) in tag order, one entry per distinct file, so tags that share a parser are built
        once. Tags from before 0.2.6 have no parser and are left out.
    """
    names = run(["git", "tag", "--list", "v*", "--sort=v:refname"], cwd=root).split() + ["HEAD"]
    parsers = []
    for name in names:
        blob = run(["git", "rev-parse", "--verify", "--quiet", f"{name}:{PARSER_PATH}"], cwd=root, check=False).strip()
        if not blob:
            continue
        for known, labels in parsers:
            if known == blob:
                labels.append(name)
                break
        else:
            parsers.append((blob, [name]))
    return parsers


def build_parser(root, blob, folder):
    """Builds the probe's program around one version of the parser.

    Args:
        root: The repository's top folder.
        blob: The parser file's git blob id.
        folder: An empty folder to build in.

    Returns:
        The path of the built program's DLL.

    Raises:
        subprocess.CalledProcessError: git could not read the file, or the build failed (its output is in the exception).
    """
    source = run(["git", "cat-file", "-p", blob], cwd=root)
    for name, text in (("Markdown.cs", source), ("Program.cs", PROGRAM), ("probe.csproj", PROJECT)):
        with open(os.path.join(folder, name), "w", encoding="utf-8", newline="\n") as file:
            file.write(text)
    output = os.path.join(folder, "out")
    run(["dotnet", "build", os.path.join(folder, "probe.csproj"), "-c", "Release", "-nologo", "-v", "q", "-o", output])
    return os.path.join(output, "probe.dll")


def draw(program, paths):
    """Runs a built program: what that version's update dialog draws of each body.

    Args:
        program: The DLL build_parser made.
        paths: The bodies' files.

    Returns:
        One list of block lines per file, in the order given.

    Raises:
        subprocess.CalledProcessError: The program failed.
        json.JSONDecodeError: It wrote something other than its JSON.
    """
    return [entry["blocks"] for entry in json.loads(run(["dotnet", program, *paths]))]


def find_blocks(text):
    """Finds the translation blocks of a body and what is wrong with them.

    A block opens with "<!-- release-notes <tag>" at the start of a line after a blank line and closes with a line that
    is "-->" alone. Inside, "-->" anywhere ends the comment early on GitHub and in every installed copy, and "--!>"
    ends it early on GitHub (an HTML parser's rule; the app only looks for "-->").

    Args:
        text: The body.

    Returns:
        (blocks, problems): blocks is a list of dicts with "language", "first" and "last" (0-based line numbers of the
        opening and closing lines, "last" None when the block never closes) and "text" (the translation); problems is
        a list of sentences.
    """
    lines = text.replace("\r\n", "\n").replace("\r", "\n").split("\n")
    blocks, problems, seen = [], [], set()
    index = 0
    while index < len(lines):
        line = lines[index]
        opening = OPENING.match(line)
        if not opening:
            if line.lstrip().startswith("<!-- release-notes"):
                problems.append(f"line {index + 1}: a block's first line must be '<!-- release-notes <tag>' alone, at the start of the line")
            index += 1
            continue

        language = opening.group(1)
        if language.lower() == "en":
            problems.append(f"line {index + 1}: English is the visible part of the notes, not a block")
        if language.lower() in seen:
            problems.append(f"line {index + 1}: a second block in {language}")
        seen.add(language.lower())
        if index > 0 and lines[index - 1].strip():
            problems.append(f"line {index + 1}: the {language} block must follow a blank line")

        first, inner = index, []
        index += 1
        while index < len(lines) and not CLOSING.match(lines[index]):
            if "-->" in lines[index]:
                problems.append(f"line {index + 1}: '-->' inside the {language} block ends it here on GitHub and in every installed copy")
            if "--!>" in lines[index]:
                problems.append(f"line {index + 1}: '--!>' inside the {language} block ends it here on GitHub")
            inner.append(lines[index])
            index += 1
        if index >= len(lines):
            problems.append(f"line {first + 1}: the {language} block never closes (a line that is '-->' alone)")
            blocks.append({"language": language, "first": first, "last": None, "text": "\n".join(inner)})
            break
        blocks.append({"language": language, "first": first, "last": index, "text": "\n".join(inner)})
        index += 1
    return blocks, problems


def without_blocks(text, blocks):
    """Removes the translation blocks from a body, which is what every installed copy and GitHub should show of it.

    Args:
        text: The body.
        blocks: Its blocks (find_blocks).

    Returns:
        The body without the blocks' lines; an unclosed block takes the rest of the body with it.
    """
    lines = text.replace("\r\n", "\n").replace("\r", "\n").split("\n")
    dropped = set()
    for block in blocks:
        last = block["last"] if block["last"] is not None else len(lines) - 1
        dropped.update(range(block["first"], last + 1))
    return "\n".join(line for number, line in enumerate(lines) if number not in dropped)


def github_token():
    """The token of the GitHub CLI's stored sign-in, for the renderer's higher rate limit.

    GH_TOKEN and GITHUB_TOKEN are hidden from gh while it is asked (gh prefers them, and one left in a shell can be
    stale), as tools/readme/check-translations.py does.

    Returns:
        The token, or None when gh is not installed, not signed in, or does not answer.
    """
    environment = {key: value for key, value in os.environ.items() if key not in ("GH_TOKEN", "GITHUB_TOKEN")}
    try:
        result = subprocess.run(["gh", "auth", "token"], env=environment, capture_output=True, check=True, timeout=30)
    except (OSError, subprocess.SubprocessError):
        return None
    return result.stdout.decode("utf-8").strip() or None


def render(text, token):
    """Renders a body the way a release page draws it, through GitHub's /markdown (mode gfm with the repository's
    context). Nothing is published or stored.

    Args:
        text: The body.
        token: A token for the higher rate limit, or None. A token GitHub refuses is dropped and the request repeated.

    Returns:
        The HTML.

    Raises:
        OSError: The request failed (no network, a timeout, a rate limit; urllib's errors are OSErrors).
    """
    headers = {"Accept": "application/vnd.github+json", "Content-Type": "application/json",
               "X-GitHub-Api-Version": "2022-11-28", "User-Agent": "BetterClipboard-probe-release-notes"}
    if token:
        headers["Authorization"] = f"Bearer {token}"
    payload = json.dumps({"text": text, "mode": "gfm", "context": REPOSITORY}).encode("utf-8")
    request = urllib.request.Request("https://api.github.com/markdown", headers=headers, data=payload)
    try:
        with urllib.request.urlopen(request, timeout=60) as response:
            return response.read().decode("utf-8")
    except urllib.error.HTTPError as error:
        if error.code != 401 or not token:
            raise
        return render(text, None)


def html_lines(html):
    """Splits rendered HTML into one tag or text per line, whitespace between tags dropped, for comparing and diffing.

    Args:
        html: The HTML.

    Returns:
        The lines.
    """
    return [line for line in re.sub(r">\s*<", ">\n<", html).strip().split("\n") if line.strip()]


def describe(name, text, blocks):
    """Prints a body's name, size and blocks.

    Args:
        name: How to call the body.
        text: The body.
        blocks: Its blocks (find_blocks).
    """
    size = f"{len(text):,} characters, {len(text.encode('utf-8')):,} bytes as UTF-8"
    print(f"Body: {name} ({size}; GitHub takes at most {GITHUB_LIMIT:,} characters)")
    if blocks:
        parts = [f"{block['language']} {len(block['text']):,} characters" for block in blocks]
        print(f"Translation blocks: {', '.join(parts)}")
    else:
        print("Translation blocks: none")
    if len(text) > GITHUB_LIMIT:
        print(f"  GitHub refuses this body: {len(text) - GITHUB_LIMIT:,} characters too long")


def main(argv=None):
    """Runs the probe.

    Args:
        argv: The arguments; None for the command line's.

    Returns:
        The exit code (see the module's documentation).
    """
    sys.stdout.reconfigure(encoding="utf-8")
    parser = argparse.ArgumentParser(description="What released versions and GitHub show of a release body with hidden translations.")
    parser.add_argument("bodies", nargs="*", help="release bodies to check (Markdown files); none: --sample")
    parser.add_argument("--sample", choices=sorted(SAMPLES), help="a built-in BC-TEST body (default when no file is given: hidden)")
    parser.add_argument("--github", action="store_true", help="also render through GitHub's /markdown (needs the network)")
    parser.add_argument("--show", action="store_true", help="list every block each version draws")
    options = parser.parse_args(argv)

    for tool in ("git", "dotnet"):
        if shutil.which(tool) is None:
            print(f"{tool} was not found on the PATH.", file=sys.stderr)
            return 2

    try:
        root = repository_root()
    except subprocess.CalledProcessError:
        print("This script must be run from inside the BetterClipboard repository.", file=sys.stderr)
        return 2

    work = tempfile.mkdtemp(prefix="bc-release-notes-probe-")
    try:
        bodies = []
        for path in options.bodies:
            with open(path, encoding="utf-8-sig") as file:
                bodies.append((path, file.read().replace("\r\n", "\n")))
        if not bodies or options.sample:
            name = options.sample or "hidden"
            bodies.append((f"built-in sample '{name}'", SAMPLES[name]))

        parsers = released_parsers(root)
        if not parsers:
            print(f"No tag or HEAD has {PARSER_PATH}.", file=sys.stderr)
            return 2
        programs = []
        for number, (blob, labels) in enumerate(parsers):
            folder = os.path.join(work, f"parser{number}")
            os.makedirs(folder)
            try:
                programs.append((blob, labels, build_parser(root, blob, folder)))
            except subprocess.CalledProcessError as error:
                print(f"Building the parser of {', '.join(labels)} failed:\n{error.stdout.decode('utf-8', 'replace')}", file=sys.stderr)
                return 2

        token = github_token() if options.github else None
        failed = False
        # Whether any body had blocks: a body without them has nothing to hide, and must not read as "hidden".
        any_blocks = False
        for number, (name, text) in enumerate(bodies):
            blocks, problems = find_blocks(text)
            any_blocks |= bool(blocks)
            print()
            describe(name, text, blocks)
            for problem in problems:
                print(f"  PROBLEM {problem}")
            failed |= bool(problems)

            full = os.path.join(work, f"body{number}.md")
            bare = os.path.join(work, f"body{number}-without-blocks.md")
            with open(full, "w", encoding="utf-8", newline="\n") as file:
                file.write(text)
            with open(bare, "w", encoding="utf-8", newline="\n") as file:
                file.write(without_blocks(text, blocks))

            print("The update dialog of each version (its own Markdown.cs):")
            for blob, labels, program in programs:
                drawn, expected = draw(program, [full, bare])
                label = f"{', '.join(labels)} (parser {blob[:12]})"
                if not blocks:
                    print(f"  {label}: {len(drawn)} blocks drawn (no translation blocks to hide)")
                elif drawn == expected:
                    print(f"  {label}: translations hidden ({len(drawn)} blocks drawn, as without them)")
                else:
                    failed = True
                    print(f"  {label}: TRANSLATIONS SHOW")
                    for line in difflib.unified_diff(expected, drawn, "without blocks", "with blocks", n=0, lineterm=""):
                        print(f"    {line}")
                if options.show or not blocks:
                    for line in drawn:
                        print(f"    | {line}")

            if options.github:
                try:
                    shown, wanted = render(text, token), render(without_blocks(text, blocks), token)
                except OSError as error:
                    print(f"GitHub's renderer could not be asked: {error}", file=sys.stderr)
                    return 2
                if not blocks:
                    print(f"GitHub (mode gfm, a release page's structure): {len(html_lines(shown))} lines of HTML (no translation blocks to hide)")
                    if options.show:
                        for line in html_lines(shown):
                            print(f"    | {line}")
                elif html_lines(shown) == html_lines(wanted):
                    print("GitHub (mode gfm, a release page's structure): translations hidden")
                else:
                    failed = True
                    print("GitHub (mode gfm, a release page's structure): TRANSLATIONS SHOW")
                    for line in difflib.unified_diff(html_lines(wanted), html_lines(shown), "without blocks", "with blocks", n=0, lineterm=""):
                        print(f"    {line}")

        print()
        if failed:
            print("Result: something shows or is malformed (see above).")
        elif not any_blocks:
            print("Result: no translation blocks, so nothing was checked for hiding (the blocks drawn are listed above).")
        else:
            print("Result: hidden everywhere it was checked.")
        return 1 if failed else 0
    finally:
        shutil.rmtree(work, ignore_errors=True)


if __name__ == "__main__":
    sys.exit(main())
