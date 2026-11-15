#!/usr/bin/env python3
"""Cut README.md, plus the hand-written pages in guides/, into the docs site's manual.

Why the pages are generated: README.md is BetterClipboard's user manual (CLAUDE.md §4: a feature is not done until the
README presents it), and four translations are kept in step with it. A second, hand-kept copy for the site would drift
from it, so the site's manual is cut from the README at every docs build. Only what the README cannot hold - longer
guides, such as every way to install - is written by hand, in guides/, and placed into the README's menu.

What it writes into docs/website-src/docs/ (all of it build output, ignored by git):
    <slug>.md     one page per "##" section and per "###" subsection of the README, named by the anchor GitHub gives
                  the heading ("Taking over Win+V" -> taking-over-winv.md), so the README's own "#anchor" links map
                  one to one onto pages
    <name>.md     every guide, with its links checked
    images/       the pictures the README's sections show, copied from where the README keeps them
    toc.yml       the left menu: the README's order, each guide under the section its front matter names
    pages.json    the pages in menu order (title, description, source), read by generate_llms_txt.py

Usage:
    python docs/website-src/scripts/generate_pages.py [--readme PATH] [--out DIR] [--repo-url URL] [--branch NAME]

Exit codes: 0 the pages were written; 1 the README or a guide has something the site cannot carry (a link to a heading
that does not exist, a missing picture or file, a guide placed under an unknown section) - every problem is printed
before the run fails, so one CI run shows them all; 2 bad usage (argparse).
"""

from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import sys
import unicodedata
from dataclasses import dataclass, field
from pathlib import Path

SCRIPT_DIR = Path(__file__).resolve().parent
SITE_DIR = SCRIPT_DIR.parent
REPO_ROOT = SITE_DIR.parent.parent
GUIDES_DIR = SITE_DIR / 'guides'
SITE_IMAGES_DIR = SITE_DIR / 'images'

# Where links to repository files point, and where "Edit this page" sends a reader. CI passes its own repository (a
# fork builds a site that links to itself); these defaults are the project's.
DEFAULT_REPO_URL = 'https://github.com/Nucs/BetterClipboard'
DEFAULT_BRANCH = 'main'

# Pictures a page shows are copied next to the pages; any other repository file a page links is linked on GitHub.
IMAGE_SUFFIXES = {'.png', '.jpg', '.jpeg', '.gif', '.svg', '.webp'}

# The longest meta description written: search engines show about this much.
DESCRIPTION_LIMIT = 160

# The heading of the list of child pages that ends a page with children.
CHILDREN_HEADING = 'In this section'

FENCE_OPEN = re.compile(r'^( {0,3})(`{3,}|~{3,})(.*)$')
HEADING = re.compile(r'^(#{1,6})[ \t]+(.+?)(?:[ \t]+#+)?[ \t]*$')
# [text](target "title") and ![alt](target): one level of nested brackets in the text is allowed ("[a [b] c](x)").
MD_LINK = re.compile(r'(!?\[(?:[^\[\]]|\[[^\]]*\])*\]\()\s*(<[^>]*>|[^\s)]+)((?:\s+"[^"]*")?\s*\))')
HTML_URL_ATTRIBUTE = re.compile(r'\b(src|href)=(["\'])(.*?)\2')
SCHEME = re.compile(r'^[a-zA-Z][a-zA-Z0-9+.-]*:')
CODE_SPAN = re.compile(r'(`+)(?:.+?)\1')


class SiteError(Exception):
    """A problem in the README or a guide that the site cannot carry, with the file and line it is in."""


@dataclass
class Page:
    """One page of the manual: a README section or subsection, or a guide.

    slug: file name without ".md" - the README heading's GitHub anchor, or the guide's file name.
    title: the heading as written (inline Markdown kept), shown as the page's heading.
    level: 2 or 3 for README sections (the heading level they had); 0 for guides.
    body: Markdown lines after the heading, links not yet rewritten.
    source: repository-relative path of the file the page comes from, with "/" separators.
    start_line, end_line: 1-based lines of the page in its source (a README section's heading to its last line).
    body_start: 1-based source line of body[0], so problems found in the body name their real line.
    parent: slug of the page it is listed under in the menu; None at the top level.
    after: for a guide, the slug of the sibling it follows in the menu ("start" for the first place); None for last.
    children: slugs listed under it, in menu order.
    """

    slug: str
    title: str
    level: int
    body: list[str]
    source: str
    start_line: int
    end_line: int
    body_start: int
    parent: str | None = None
    after: str | None = None
    children: list[str] = field(default_factory=list)

    @property
    def is_guide(self) -> bool:
        """True for a hand-written page from guides/, false for a page cut from the README."""
        return self.level == 0


@dataclass
class Context:
    """What rewriting a page's links needs to know, and where the problems found along the way are collected.

    pages: every page by slug, to resolve "#anchor" and "page.md" links.
    out_dir: the folder the pages are written to (pictures go to its images/ folder).
    repo_url, branch: where links to other repository files point on GitHub.
    copied_images: picture file name in the site -> repository file it came from, to refuse two different pictures
        with one name (they would overwrite each other) while letting several pages show the same picture.
    problems: every problem found, reported together at the end.
    """

    pages: dict[str, Page]
    out_dir: Path
    repo_url: str
    branch: str
    copied_images: dict[str, Path] = field(default_factory=dict)
    problems: list[str] = field(default_factory=list)


def github_slug(heading_text: str) -> str:
    """The anchor GitHub gives a heading: what README.md's own "#anchor" links use, and so the page's file name.

    Mirrors github-slugger: lower case, then every character that is not a letter, a mark, a digit, "-", "_" or a
    space is dropped, and spaces become "-". "Taking over Win+V" -> "taking-over-winv". Changing this would rename
    every page, and the README's links would no longer find them.

    Args:
        heading_text: the heading's plain text (no Markdown syntax; plain_text() strips it).

    Returns:
        The anchor, possibly empty for a heading of symbols only (the caller treats that as an error).
    """
    kept = []
    for ch in heading_text.lower():
        category = unicodedata.category(ch)
        if category[0] in ('L', 'M', 'N') or ch in ('-', '_', ' '):
            kept.append(ch)
    return ''.join(kept).replace(' ', '-')


def plain_text(markdown: str) -> str:
    """Inline Markdown reduced to the text a reader sees, for anchors, menu names and meta descriptions.

    Links keep their text, code spans their content, emphasis markers and HTML tags go, and entities such as &amp; are
    decoded. It is deliberately small: it serves headings and first sentences, not arbitrary Markdown.

    Args:
        markdown: one line of inline Markdown.

    Returns:
        The visible text, with runs of white space collapsed.
    """
    text = re.sub(r'!?\[((?:[^\[\]]|\[[^\]]*\])*)\]\([^)]*\)', r'\1', markdown)
    text = re.sub(r'<[^>]+>', '', text)
    text = re.sub(r'(`+)(.+?)\1', r'\2', text)
    text = re.sub(r'(\*\*|__)(.+?)\1', r'\2', text)
    text = re.sub(r'(?<![\w*])\*(?!\s)(.+?)(?<!\s)\*(?![\w*])', r'\1', text)
    text = text.replace('\\*', '*').replace('\\_', '_')
    text = text.replace('&amp;', '&').replace('&lt;', '<').replace('&gt;', '>').replace('&nbsp;', ' ')
    return re.sub(r'\s+', ' ', text).strip()


def split_fences(lines: list[str]) -> list[bool]:
    """For every line, whether it lies inside a fenced code block (fence lines included).

    Why: headings and links are only real outside code; the README's PowerShell blocks hold "#" comments and "](" can
    appear in a command, and neither may start a page or be rewritten.

    Args:
        lines: the file's lines, without line endings.

    Returns:
        One flag per line: True for a fence line or a line between an opening and its closing fence.
    """
    inside = []
    fence_char = ''
    fence_len = 0
    for line in lines:
        if fence_char:
            inside.append(True)
            closing = re.match(r'^ {0,3}(' + re.escape(fence_char) + r'{' + str(fence_len) + r',})[ \t]*$', line)
            if closing:
                fence_char = ''
            continue
        opening = FENCE_OPEN.match(line)
        if opening:
            fence_char = opening.group(2)[0]
            fence_len = len(opening.group(2))
            inside.append(True)
            continue
        inside.append(False)
    return inside


def split_lines(text: str) -> list[str]:
    """A file's text as lines without line endings, whatever endings it was saved with (git may check out CRLF).

    Args:
        text: the file's whole text.

    Returns:
        The lines; a text ending in a line break gives an empty last line, as str.split does.
    """
    return text.replace('\r\n', '\n').replace('\r', '\n').split('\n')


def repo_relative(path: Path) -> str:
    """The path relative to the repository root, with "/" separators, as GitHub and the messages show it.

    Args:
        path: a path inside the repository (a path outside it is returned by its file name only).

    Returns:
        The repository-relative path.
    """
    try:
        return path.resolve().relative_to(REPO_ROOT).as_posix()
    except ValueError:
        return path.name


def parse_readme(readme_path: Path) -> list[Page]:
    """Cut the README into pages: one per "##" section and one per "###" subsection, in the README's order.

    Everything before the first "##" (the centered title, badges, language line, tagline and photo) belongs to the
    site's own home page and is skipped. A "###" becomes a child of the "##" above it; its content ends where the
    next "##" or "###" starts. Deeper headings stay inside their page.

    Args:
        readme_path: the README to read (UTF-8).

    Returns:
        The pages in README order, parents before their children, each with its child slugs filled in.

    Raises:
        SiteError: a heading whose anchor is empty, a "###" before any "##", or a README without any "##".
        OSError: the README cannot be read.
    """
    lines = split_lines(readme_path.read_text(encoding='utf-8'))
    in_code = split_fences(lines)
    source = repo_relative(readme_path)
    pages: list[Page] = []
    seen_slugs: dict[str, int] = {}
    current: Page | None = None
    current_parent: Page | None = None
    for index, line in enumerate(lines):
        match = None if in_code[index] else HEADING.match(line)
        level = len(match.group(1)) if match else 0
        if level in (2, 3):
            if current is not None:
                current.end_line = index
            title = match.group(2).strip()
            base = github_slug(plain_text(title))
            if not base:
                raise SiteError(f'{source}:{index + 1}: the heading "{title}" has no letters or digits to name a page')
            # GitHub numbers repeated anchors (-1, -2, ...); the README's links to a repeat would use that form too.
            count = seen_slugs.get(base, 0)
            seen_slugs[base] = count + 1
            slug = base if count == 0 else f'{base}-{count}'
            current = Page(slug=slug, title=title, level=level, body=[], source=source,
                           start_line=index + 1, end_line=index + 1, body_start=index + 2)
            if level == 2:
                current_parent = current
            else:
                if current_parent is None:
                    raise SiteError(f'{source}:{index + 1}: "### {title}" comes before any "##" section')
                current.parent = current_parent.slug
                current_parent.children.append(slug)
            pages.append(current)
        elif current is not None:
            current.body.append(line)
    if current is None:
        raise SiteError(f'{source}: no "## " section to make a page of')
    current.end_line = len(lines)
    for page in pages:
        # Blank lines around a section belong to no page: the "Edit this page" range ends on the section's last text,
        # and the body starts right under the page's title.
        while page.body and not page.body[-1].strip():
            page.body.pop()
            page.end_line -= 1
        while page.body and not page.body[0].strip():
            page.body.pop(0)
            page.body_start += 1
    return pages


def read_front_matter(lines: list[str]) -> tuple[dict[str, str], int]:
    """Read a guide's front matter: the flat "key: value" lines between two "---" lines at its top.

    Only that flat form is read (a value may be in double quotes, as JSON); anything richer is refused on purpose, so
    a guide cannot pass docfx metadata into the build without this script knowing.

    Args:
        lines: the guide's lines.

    Returns:
        The keys and values, and the index of the first line after the front matter (0 when there is none).

    Raises:
        SiteError: a front matter that never ends, a line in it that is not "key: value", or a quoted value that is
            not a valid JSON string.
    """
    if not lines or lines[0].strip() != '---':
        return {}, 0
    meta: dict[str, str] = {}
    for index in range(1, len(lines)):
        line = lines[index]
        if line.strip() == '---':
            return meta, index + 1
        if not line.strip() or line.lstrip().startswith('#'):
            continue
        key, sep, value = line.partition(':')
        if not sep or not re.fullmatch(r'[A-Za-z_][\w-]*', key.strip()):
            raise SiteError(f'line {index + 1} of the front matter is not "key: value": {line!r}')
        value = value.strip()
        if len(value) >= 2 and value[0] == value[-1] == '"':
            try:
                value = json.loads(value)
            except json.JSONDecodeError as error:
                raise SiteError(f'line {index + 1} of the front matter: {error.msg}') from None
        meta[key.strip()] = value
    raise SiteError('the front matter never ends (no closing "---" line)')


def parse_guides(guides_dir: Path, readme_slugs: set[str]) -> list[Page]:
    """Read every hand-written guide: its title (its first "# " heading), its place in the menu and its Markdown.

    A guide names its place in front matter: "parent:" is the slug of the README page (or another guide) it is listed
    under, absent for the top level; "after:" is the sibling it follows ("start" for the first place), absent to follow
    all of them. A file name that collides with a README page would replace that page, so it is refused.

    Args:
        guides_dir: the folder of guides (docs/website-src/guides).
        readme_slugs: the slugs of the README's pages, to refuse collisions.

    Returns:
        The guides, sorted by file name (their menu order comes from parent/after, not from this list).

    Raises:
        SiteError: a guide without a "# " title, with bad front matter or unknown keys, or named like a README page.
        OSError: a guide cannot be read.
    """
    guides: list[Page] = []
    if not guides_dir.is_dir():
        return guides
    for path in sorted(guides_dir.glob('*.md')):
        rel = repo_relative(path)
        lines = split_lines(path.read_text(encoding='utf-8'))
        try:
            meta, first = read_front_matter(lines)
        except SiteError as error:
            raise SiteError(f'{rel}: {error}') from None
        unknown = sorted(set(meta) - {'parent', 'after'})
        if unknown:
            raise SiteError(f'{rel}: unknown front matter key(s) {", ".join(unknown)} (only parent and after)')
        slug = path.stem
        if slug in readme_slugs:
            raise SiteError(f'{rel}: a README section is already called "{slug}"; rename the guide')
        if slug != github_slug(slug):
            raise SiteError(f'{rel}: name guides in lower case with hyphens only (as "{github_slug(slug)}.md")')
        in_code = split_fences(lines)
        title_index = next((i for i in range(first, len(lines))
                            if not in_code[i] and re.match(r'^#[ \t]+\S', lines[i])), None)
        if title_index is None:
            raise SiteError(f'{rel}: no "# Title" line')
        title = HEADING.match(lines[title_index]).group(2).strip()
        body = lines[title_index + 1:]
        body_start = title_index + 2
        while body and not body[-1].strip():
            body.pop()
        while body and not body[0].strip():
            body.pop(0)
            body_start += 1
        guides.append(Page(slug=slug, title=title, level=0, body=body, source=rel,
                           start_line=1, end_line=len(lines), body_start=body_start,
                           parent=meta.get('parent') or None, after=meta.get('after') or None))
    return guides


def place_guides(pages: dict[str, Page], top_level: list[str], guides: list[Page]) -> None:
    """Put every guide into the menu: under its parent, after the sibling it names.

    Guides may be listed under other guides, and after other guides, so they are placed in rounds until none is left:
    a guide waits while its parent, or the guide it follows, is not placed yet. A guide still waiting when a round
    places nothing names a parent or sibling that never appears (or a cycle), which is an error.

    Args:
        pages: every page by slug, guides included; the parents' child lists are changed in place.
        top_level: the slugs of the menu's top level, changed in place.
        guides: the guides to place.

    Raises:
        SiteError: a guide under an unknown or unplaceable parent, or after a sibling that is not there.
    """
    waiting = list(guides)
    placed = {slug for slug, page in pages.items() if not page.is_guide}
    while waiting:
        progress = False
        for guide in list(waiting):
            if guide.parent is not None and guide.parent not in placed:
                continue
            siblings = top_level if guide.parent is None else pages[guide.parent].children
            if guide.after is None:
                siblings.append(guide.slug)
            elif guide.after == 'start':
                siblings.insert(0, guide.slug)
            elif guide.after in siblings:
                siblings.insert(siblings.index(guide.after) + 1, guide.slug)
            elif guide.after in pages and pages[guide.after].is_guide and guide.after not in placed:
                # It follows a guide that is not placed yet (file names decide the order of this loop, not the menu):
                # try again in the next round.
                continue
            else:
                raise SiteError(f'{guide.source}: "after: {guide.after}" is not a page listed under '
                                f'{guide.parent or "the top level"}')
            placed.add(guide.slug)
            waiting.remove(guide)
            progress = True
        if not progress:
            names = ', '.join(f'{g.source} (parent: {g.parent}, after: {g.after})' for g in waiting)
            raise SiteError(f'these guides name a parent or a sibling that is never placed: {names}')


def mask_code_spans(text: str) -> str:
    """The line with every code span (backticks included) replaced by as many "x" characters.

    Why: a code span shows its content literally, so "[x](#y)" inside backticks is an example, not a link, and must not
    be rewritten. The spans are masked rather than cut out because a link's text may itself be a code span
    ("[`release.yml`](.github/...)"): matching on the masked line still finds that link, and since the mask keeps every
    position, the matches' positions point into the original line.

    Args:
        text: one line of Markdown outside fenced code.

    Returns:
        A string of the same length, with no code span left in it.
    """
    return CODE_SPAN.sub(lambda match: 'x' * len(match.group(0)), text)


def resolve_target(target: str, page: Page, ctx: Context, line_no: int) -> str:
    """Where a link or picture of a page points on the site.

    For a README page: "#anchor" becomes the page made from that heading; a picture in the repository is copied next to
    the pages; any other repository file is linked on GitHub; web addresses stay. For a guide, the targets are already
    site paths (another page as "slug.md", a site picture as "../images/x.png") and are only checked.

    Args:
        target: the link's target as written.
        page: the page it is on.
        ctx: the pages, the output folder and the problem list.
        line_no: its line in the page's source, for messages.

    Returns:
        The target to write. A target that cannot be resolved is returned unchanged, and the problem is recorded.
    """
    where = f'{page.source}:{line_no}'
    if SCHEME.match(target) or target.startswith('//'):
        return target
    path_part, hash_sign, fragment = target.partition('#')
    if not path_part:
        if page.is_guide:
            # A guide's "#section" points into the guide itself: docfx gives its headings GitHub's anchors too.
            if fragment not in heading_anchors(page):
                ctx.problems.append(f'{where}: "#{fragment}" is not a heading of this guide')
            return target
        if fragment in ctx.pages:
            return f'{fragment}.md'
        ctx.problems.append(f'{where}: "#{fragment}" is not a section of the README (no page has that anchor)')
        return target
    if page.is_guide:
        return check_guide_target(path_part, hash_sign + fragment, ctx, where)
    source_dir = (REPO_ROOT / page.source).parent
    resolved = (source_dir / path_part).resolve()
    try:
        rel = resolved.relative_to(REPO_ROOT).as_posix()
    except ValueError:
        ctx.problems.append(f'{where}: "{target}" points outside the repository')
        return target
    if not resolved.exists():
        ctx.problems.append(f'{where}: "{target}" does not exist in the repository')
        return target
    if resolved.is_file() and resolved.suffix.lower() in IMAGE_SUFFIXES:
        return copy_image(resolved, ctx, where)
    kind = 'tree' if resolved.is_dir() else 'blob'
    return f'{ctx.repo_url}/{kind}/{ctx.branch}/{rel}' + (hash_sign + fragment if hash_sign else '')


def heading_anchors(page: Page) -> set[str]:
    """The anchors of a page's own headings (outside fenced code), as GitHub and docfx name them.

    Args:
        page: the page.

    Returns:
        The anchors; a heading repeated on the page also gets its numbered form ("-1", "-2", ...).
    """
    anchors: set[str] = set()
    counts: dict[str, int] = {}
    in_code = split_fences(page.body)
    for index, line in enumerate(page.body):
        heading = None if in_code[index] else HEADING.match(line)
        if heading:
            base = github_slug(plain_text(heading.group(2)))
            count = counts.get(base, 0)
            counts[base] = count + 1
            anchors.add(base if count == 0 else f'{base}-{count}')
    return anchors


def check_guide_target(path_part: str, suffix: str, ctx: Context, where: str) -> str:
    """Check a guide's own relative link: another page of the manual, or a picture in the site's images folder.

    Guides are written as if they lived in docs/ (where they are copied), so "privacy.md" is a sibling page and
    "../images/x.png" a site picture. Anything else relative would break on the site, so it is reported.

    Args:
        path_part: the target without its "#fragment".
        suffix: the "#fragment" (with "#"), or empty.
        ctx: the pages and the problem list.
        where: "file:line" for messages.

    Returns:
        The target unchanged (guides are copied as written).
    """
    if path_part.endswith('.md') and '/' not in path_part:
        if path_part[:-3] not in ctx.pages:
            ctx.problems.append(f'{where}: "{path_part}" is not a page of the manual')
    elif path_part.startswith('../images/'):
        if not (SITE_IMAGES_DIR / path_part[len('../images/'):]).is_file():
            ctx.problems.append(f'{where}: "{path_part}" is not in docs/website-src/images')
    else:
        ctx.problems.append(f'{where}: "{path_part}" - a guide links pages as "slug.md", pictures as '
                            f'"../images/name", and anything else by its full https address')
    return path_part + suffix


def copy_image(image: Path, ctx: Context, where: str) -> str:
    """Copy a picture a README page shows next to the pages, once, and return its path from a page.

    Args:
        image: the picture in the repository.
        ctx: the output folder, the pictures copied so far and the problem list.
        where: "file:line" for messages.

    Returns:
        "images/<name>", the picture's path relative to a page.

    Raises:
        OSError: the picture cannot be copied.
    """
    name = image.name
    earlier = ctx.copied_images.get(name)
    if earlier is not None and earlier != image:
        ctx.problems.append(f'{where}: two different pictures are called {name} '
                            f'({repo_relative(earlier)} and {repo_relative(image)})')
        return f'images/{name}'
    if earlier is None:
        target_dir = ctx.out_dir / 'images'
        target_dir.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(image, target_dir / name)
        ctx.copied_images[name] = image
    return f'images/{name}'


def rewrite_line(line: str, page: Page, ctx: Context, line_no: int) -> str:
    """Rewrite the links and picture sources of one line outside fenced code.

    Markdown links and images, and src/href attributes of inline HTML (the README shows its photos with <img>), are
    resolved by resolve_target; code spans are left alone.

    Args:
        line: the line.
        page: the page it is on.
        ctx: what resolving needs, and the problem list.
        line_no: its line in the page's source.

    Returns:
        The line with every target replaced.
    """
    masked = mask_code_spans(line)
    # (start, end, replacement) for every target, found on the masked line and applied to the real one.
    edits: list[tuple[int, int, str]] = []
    for match in MD_LINK.finditer(masked):
        start, end = match.span(2)
        raw = line[start:end]
        bracketed = raw.startswith('<') and raw.endswith('>')
        target = raw[1:-1] if bracketed else raw
        new = resolve_target(target, page, ctx, line_no)
        edits.append((start, end, f'<{new}>' if bracketed else new))
    for match in HTML_URL_ATTRIBUTE.finditer(masked):
        start, end = match.span(3)
        edits.append((start, end, resolve_target(line[start:end], page, ctx, line_no)))
    # Applied from the end, so the positions of the edits still to come do not move.
    for start, end, replacement in sorted(edits, reverse=True):
        line = line[:start] + replacement + line[end:]
    return line


def render_body(page: Page, ctx: Context) -> list[str]:
    """A page's Markdown as the site shows it: links resolved and headings lifted under the page's own title.

    A README "###" page is a page of its own, so its "####" headings become "##" (and so on): the page title is the
    only "#". Lines in fenced code are copied as they are.

    Args:
        page: the page.
        ctx: what resolving links needs, and the problem list.

    Returns:
        The rendered body lines.
    """
    shift = max(page.level - 1, 0)
    in_code = split_fences(page.body)
    out = []
    for index, line in enumerate(page.body):
        line_no = page.body_start + index
        if in_code[index]:
            out.append(line)
            continue
        heading = HEADING.match(line)
        if heading and shift:
            level = max(len(heading.group(1)) - shift, 2)
            line = '#' * level + line[len(heading.group(1)):]
        out.append(rewrite_line(line, page, ctx, line_no))
    return out


def describe(page: Page) -> str:
    """A page's meta description: its first paragraph (or first list item) as plain text, cut at a word.

    Args:
        page: the page.

    Returns:
        At most DESCRIPTION_LIMIT characters, ending in "…" when cut; empty when the page starts with no text.
    """
    in_code = split_fences(page.body)
    collected: list[str] = []
    for index, line in enumerate(page.body):
        stripped = line.strip()
        if in_code[index] or stripped.startswith(('<', '|', '#', '>')):
            if collected:
                break
            continue
        if not stripped:
            if collected:
                break
            continue
        item = re.match(r'^(?:[-*+]|\d+[.)])\s+(.*)$', stripped)
        if item:
            if collected:
                break
            collected.append(item.group(1))
            continue
        collected.append(stripped)
    text = plain_text(' '.join(collected))
    if len(text) <= DESCRIPTION_LIMIT:
        return text
    cut = text[:DESCRIPTION_LIMIT - 1]
    cut = cut[:cut.rfind(' ')] if ' ' in cut else cut
    return cut.rstrip(' ,;:—-') + '…'


def edit_url(page: Page, ctx: Context) -> str:
    """Where "Edit this page" sends a reader: the page's source on GitHub, with a README section's lines marked.

    The pages are build output, so the template's own link (to the generated file) would point at nothing; this one
    points at the text to change. "?plain=1" makes GitHub show the Markdown's lines instead of rendering it, so the
    "#L12-L40" range is highlighted.

    Args:
        page: the page.
        ctx: the repository URL and branch.

    Returns:
        The URL.
    """
    base = f'{ctx.repo_url}/blob/{ctx.branch}/{page.source}'
    if page.is_guide:
        return base
    return f'{base}?plain=1#L{page.start_line}-L{page.end_line}'


def yaml_string(value: str) -> str:
    """A YAML scalar for any text: JSON's double-quoted form, which YAML reads as the same string.

    Why: titles hold ":" and "#" ("Notepad++", "Win+V: …"), which a bare YAML scalar would misread.

    Args:
        value: the text.

    Returns:
        The quoted scalar.
    """
    return json.dumps(value, ensure_ascii=False)


def write_page(page: Page, ctx: Context) -> str:
    """Write one page to the output folder and return its description.

    The file starts with front matter that docfx uses (the meta description and the "Edit this page" target), then the
    title as the only "#" heading, the body, and - when the page has children - a list of them, so a reader on the page
    sees what is below it without opening the menu.

    Args:
        page: the page.
        ctx: the output folder, the pages and the problem list.

    Returns:
        The page's description (also written into its front matter).

    Raises:
        OSError: the page cannot be written, or a picture it shows cannot be copied.
    """
    description = describe(page)
    lines = ['---']
    if description:
        lines.append(f'description: {yaml_string(description)}')
    lines.append(f'docurl: {yaml_string(edit_url(page, ctx))}')
    origin = 'guide' if page.is_guide else 'README section'
    lines.append(f'# Generated by scripts/generate_pages.py from {page.source} ({origin}); edit the source, not this.')
    lines.append('---')
    lines.append(f'# {rewrite_line(page.title, page, ctx, page.start_line)}')
    lines.append('')
    lines.extend(render_body(page, ctx))
    if page.children:
        lines += ['', f'## {CHILDREN_HEADING}', '']
        for child in page.children:
            lines.append(f'- [{plain_text(ctx.pages[child].title)}]({child}.md)')
    (ctx.out_dir / f'{page.slug}.md').write_text('\n'.join(lines) + '\n', encoding='utf-8', newline='\n')
    return description


def write_toc(top_level: list[str], ctx: Context) -> None:
    """Write the left menu (toc.yml): the home page, then every page in menu order, children nested under parents.

    Args:
        top_level: the slugs of the menu's top level, in order.
        ctx: the pages and the output folder.

    Raises:
        OSError: the file cannot be written.
    """
    lines = ['# Generated by scripts/generate_pages.py from README.md and guides/; edit those, not this.',
             '- name: Home', '  href: ../index.md']

    def emit(slug: str, depth: int) -> None:
        """Add one menu entry to the lines and, below it, its children one level deeper.

        Args:
            slug: the page's slug.
            depth: its nesting level (0 at the top), which sets the YAML indentation.
        """
        page = ctx.pages[slug]
        indent = '  ' * depth
        lines.append(f'{indent}- name: {yaml_string(plain_text(page.title))}')
        lines.append(f'{indent}  href: {slug}.md')
        if page.children:
            lines.append(f'{indent}  items:')
            for child in page.children:
                emit(child, depth + 1)

    for slug in top_level:
        emit(slug, 0)
    (ctx.out_dir / 'toc.yml').write_text('\n'.join(lines) + '\n', encoding='utf-8', newline='\n')


def menu_order(top_level: list[str], pages: dict[str, Page]) -> list[str]:
    """Every slug in the order the menu shows it (depth first), for pages.json and the AI-friendly files.

    Args:
        top_level: the slugs of the menu's top level.
        pages: every page by slug.

    Returns:
        All slugs, each once, parents before their children.
    """
    order: list[str] = []

    def visit(slug: str) -> None:
        """Add a page to the order, then its children (depth first).

        Args:
            slug: the page's slug.
        """
        order.append(slug)
        for child in pages[slug].children:
            visit(child)

    for slug in top_level:
        visit(slug)
    return order


def generate(readme: Path, out_dir: Path, repo_url: str, branch: str) -> list[str]:
    """Build the whole manual into out_dir, replacing whatever an earlier run left there.

    Args:
        readme: the README to cut.
        out_dir: the folder to write (emptied first: every file in it is generated).
        repo_url: the GitHub repository's address, without a trailing slash.
        branch: the branch links to repository files and "Edit this page" use.

    Returns:
        The problems found; the pages are still written when there are some, so they can be looked at.

    Raises:
        SiteError: a problem that stops the cut itself (a broken heading, a bad guide, an unplaceable guide).
        OSError: a source cannot be read, or the output folder cannot be emptied or written.
    """
    readme_pages = parse_readme(readme)
    pages = {page.slug: page for page in readme_pages}
    top_level = [page.slug for page in readme_pages if page.level == 2]
    guides = parse_guides(GUIDES_DIR, set(pages))
    for guide in guides:
        pages[guide.slug] = guide
    place_guides(pages, top_level, guides)

    # The folder holds nothing but this script's output (docs/website-src/.gitignore ignores all of it), so it is
    # emptied first: a page whose README section was renamed must not linger under its old name.
    if out_dir.exists():
        shutil.rmtree(out_dir)
    out_dir.mkdir(parents=True)
    ctx = Context(pages=pages, out_dir=out_dir, repo_url=repo_url.rstrip('/'), branch=branch)

    manifest = []
    for slug in menu_order(top_level, pages):
        page = pages[slug]
        description = write_page(page, ctx)
        manifest.append({
            'slug': slug,
            'title': plain_text(page.title),
            'description': description,
            'href': f'docs/{slug}.html',
            'parent': page.parent,
            'source': page.source,
            'guide': page.is_guide,
        })
    write_toc(top_level, ctx)
    (out_dir / 'pages.json').write_text(json.dumps({'pages': manifest}, ensure_ascii=False, indent=2) + '\n',
                                        encoding='utf-8', newline='\n')
    return ctx.problems


def main(argv: list[str] | None = None) -> int:
    """Command-line entry point; see the module's docstring for the arguments and exit codes.

    Args:
        argv: the arguments (sys.argv[1:] when None).

    Returns:
        The exit code.
    """
    parser = argparse.ArgumentParser(description='Cut README.md and guides/ into the docs site\'s manual.')
    parser.add_argument('--readme', type=Path, default=REPO_ROOT / 'README.md', help='the README to cut')
    parser.add_argument('--out', type=Path, default=SITE_DIR / 'docs', help='the folder to write (emptied first)')
    default_repo = DEFAULT_REPO_URL
    if os.environ.get('GITHUB_SERVER_URL') and os.environ.get('GITHUB_REPOSITORY'):
        default_repo = f"{os.environ['GITHUB_SERVER_URL']}/{os.environ['GITHUB_REPOSITORY']}"
    parser.add_argument('--repo-url', default=default_repo, help='GitHub repository the links point to')
    parser.add_argument('--branch', default=DEFAULT_BRANCH, help='branch for repository links and "Edit this page"')
    args = parser.parse_args(argv)

    try:
        problems = generate(args.readme.resolve(), args.out.resolve(), args.repo_url, args.branch)
    except (SiteError, OSError) as error:
        # A file error (an unreadable README, an output folder another program holds) is reported like a content
        # problem: one line, exit code 1, no traceback in the CI log.
        print(f'error: {error}', file=sys.stderr)
        return 1
    for problem in problems:
        print(f'error: {problem}', file=sys.stderr)
    if problems:
        print(f'{len(problems)} problem(s): fix the README or the guide named above.', file=sys.stderr)
        return 1
    count = len(json.loads((args.out / 'pages.json').read_text(encoding='utf-8'))['pages'])
    print(f'Wrote {count} pages to {repo_relative(args.out)}.')
    return 0


if __name__ == '__main__':
    sys.exit(main())
