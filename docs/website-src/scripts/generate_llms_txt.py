#!/usr/bin/env python3
"""Write llms.txt, llms-full.txt and robots.txt into the built site, for AI agents and crawlers.

Ported from NumSharp's docs/website-src/scripts/generate-llms-txt.sh, the starting point of this site, with one change
of approach: nothing here is hard-coded. The page list, titles and descriptions come from docs/pages.json, which
generate_pages.py writes in menu order, and the summary from the home page's description, so the files follow the
README like the rest of the site.

    llms.txt       the llmstxt.org format: a title, a one-line summary, install command, then every page with its
                   description, in menu order
    llms-full.txt  every page's Markdown in menu order, front matter removed: the whole manual in one file
    robots.txt     allows every crawler, AI crawlers included, and names the sitemap docfx writes

Run it after docfx has built the site: the files go into docfx's output folder, next to the pages they list, and the
site is published as that folder.

Usage:
    python docs/website-src/scripts/generate_llms_txt.py [OUTPUT_DIR] [SITE_URL]

Defaults: docs/website and https://nucs.github.io/BetterClipboard. Exit codes: 0 written; 1 the generated pages or the
home page are missing (run generate_pages.py first).
"""

from __future__ import annotations

import json
import re
import sys
from pathlib import Path

SCRIPT_DIR = Path(__file__).resolve().parent
SITE_DIR = SCRIPT_DIR.parent
DOCS_DIR = SITE_DIR / 'docs'
DEFAULT_OUTPUT = SITE_DIR.parent / 'website'
DEFAULT_SITE_URL = 'https://nucs.github.io/BetterClipboard'
REPO_URL = 'https://github.com/Nucs/BetterClipboard'
INSTALL_COMMAND = 'irm https://raw.githubusercontent.com/Nucs/BetterClipboard/main/install.ps1 | iex'

# The crawlers named in robots.txt, as in NumSharp's: allowed explicitly so an AI crawler that only honours its own
# name still finds itself allowed.
AI_CRAWLERS = ['GPTBot', 'ChatGPT-User', 'Claude-Web', 'ClaudeBot', 'Anthropic-AI', 'Google-Extended',
               'PerplexityBot', 'Amazonbot', 'Bytespider', 'CCBot']


def strip_front_matter(text: str) -> str:
    """A Markdown file without its leading "---" front matter block.

    Args:
        text: the file's text.

    Returns:
        The text after the closing "---", or the whole text when there is no front matter.
    """
    match = re.match(r'^---\n.*?\n---\n', text.replace('\r\n', '\n'), re.S)
    return text.replace('\r\n', '\n')[match.end():] if match else text.replace('\r\n', '\n')


def front_matter_value(text: str, key: str) -> str:
    """One value of a file's front matter (double-quoted values decoded as JSON), or "" when it has none.

    Args:
        text: the file's text.
        key: the key to read.

    Returns:
        The value.

    Raises:
        json.JSONDecodeError: a double-quoted value that is not a valid JSON string.
    """
    match = re.match(r'^---\n(.*?)\n---\n', text.replace('\r\n', '\n'), re.S)
    if not match:
        return ''
    for line in match.group(1).split('\n'):
        name, sep, value = line.partition(':')
        if sep and name.strip() == key:
            value = value.strip()
            return json.loads(value) if value.startswith('"') else value
    return ''


def load_pages() -> list[dict]:
    """The manual's pages in menu order, as generate_pages.py recorded them.

    Returns:
        One dict per page (slug, title, description, href, parent, source, guide).

    Raises:
        FileNotFoundError: generate_pages.py has not run.
        OSError: pages.json cannot be read.
        ValueError: pages.json is not the JSON generate_pages.py writes (json.JSONDecodeError is a ValueError).
        KeyError: pages.json has no "pages" list.
    """
    return json.loads((DOCS_DIR / 'pages.json').read_text(encoding='utf-8'))['pages']


def write_llms_txt(output: Path, site_url: str, pages: list[dict], summary: str) -> Path:
    """Write llms.txt: what BetterClipboard is, how to install it, and a link with a description for every page.

    Args:
        output: the built site's folder.
        site_url: the site's public address, without a trailing slash.
        pages: the pages in menu order.
        summary: the one-line summary (the home page's description).

    Returns:
        The file written.

    Raises:
        OSError: the file cannot be written.
    """
    lines = ['# BetterClipboard', '', f'> {summary}', '',
             '## Install', '', 'In PowerShell (Windows 10 2004 or newer, Windows 11; x64 or ARM64):', '',
             '```powershell', INSTALL_COMMAND, '```', '',
             '## Documentation', '']
    for page in pages:
        # Children are indented under their parent, as in the site's menu.
        indent = '  ' if page['parent'] else ''
        description = f": {page['description']}" if page['description'] else ''
        lines.append(f"{indent}- [{page['title']}]({site_url}/{page['href']}){description}")
    lines += ['', '## Optional', '',
              f'- [Full documentation in one file]({site_url}/llms-full.txt): every page above, in this order',
              f'- [GitHub repository]({REPO_URL}): source code, issues and the README',
              f'- [Releases]({REPO_URL}/releases): every version with its release notes and SHA-256 checksums', '']
    path = output / 'llms.txt'
    path.write_text('\n'.join(lines), encoding='utf-8', newline='\n')
    return path


def write_llms_full_txt(output: Path, site_url: str, pages: list[dict]) -> Path:
    """Write llms-full.txt: every page's Markdown in menu order, each preceded by its address.

    Args:
        output: the built site's folder.
        site_url: the site's public address.
        pages: the pages in menu order.

    Returns:
        The file written.

    Raises:
        OSError: the file cannot be written, or a page cannot be read.
    """
    parts = ['# BetterClipboard - complete documentation', '',
             '> Every page of the BetterClipboard manual, in the order of its menu, for AI and LLM ingestion.',
             f'> Generated from the README and the site\'s guides; the site itself: {site_url}/', '', '---', '']
    for page in pages:
        text = strip_front_matter((DOCS_DIR / f"{page['slug']}.md").read_text(encoding='utf-8')).strip()
        # Links between pages are relative ("privacy.md"); written out in full they still work when this file is read
        # on its own.
        text = re.sub(r'\]\(([a-z0-9-]+)\.md(#[^)]*)?\)', lambda m: f']({site_url}/docs/{m.group(1)}.html{m.group(2) or ""})', text)
        text = re.sub(r'\]\(images/', f']({site_url}/docs/images/', text)
        text = re.sub(r'src="images/', f'src="{site_url}/docs/images/', text)
        parts += [f"<!-- {site_url}/{page['href']} -->", text, '', '---', '']
    path = output / 'llms-full.txt'
    path.write_text('\n'.join(parts), encoding='utf-8', newline='\n')
    return path


def write_robots_txt(output: Path, site_url: str) -> Path:
    """Write robots.txt: every crawler allowed, AI crawlers by name too, and the sitemap docfx writes.

    Args:
        output: the built site's folder.
        site_url: the site's public address.

    Returns:
        The file written.

    Raises:
        OSError: the file cannot be written.
    """
    lines = ['# BetterClipboard documentation - robots.txt', '# Generated by scripts/generate_llms_txt.py.', '',
             'User-agent: *', 'Allow: /', '']
    for crawler in AI_CRAWLERS:
        lines += [f'User-agent: {crawler}', 'Allow: /', '']
    lines += ['# AI-friendly documentation files', f'# {site_url}/llms.txt - the pages, with a line on each',
              f'# {site_url}/llms-full.txt - the whole manual', '', f'Sitemap: {site_url}/sitemap.xml', '']
    path = output / 'robots.txt'
    path.write_text('\n'.join(lines), encoding='utf-8', newline='\n')
    return path


def main(argv: list[str]) -> int:
    """Command-line entry point; see the module's docstring for the arguments and exit codes.

    Args:
        argv: the arguments after the script's name.

    Returns:
        The exit code.
    """
    output = Path(argv[0]).resolve() if argv else DEFAULT_OUTPUT
    site_url = (argv[1] if len(argv) > 1 else DEFAULT_SITE_URL).rstrip('/')
    try:
        pages = load_pages()
        summary = front_matter_value((SITE_DIR / 'index.md').read_text(encoding='utf-8'), 'description')
    except FileNotFoundError as error:
        print(f'error: {error.filename} is missing: run scripts/generate_pages.py (and docfx) first.', file=sys.stderr)
        return 1
    except (OSError, ValueError, KeyError) as error:
        print(f'error: the generated pages could not be read: {error}', file=sys.stderr)
        return 1
    try:
        output.mkdir(parents=True, exist_ok=True)
        for path in (write_llms_txt(output, site_url, pages, summary), write_llms_full_txt(output, site_url, pages),
                     write_robots_txt(output, site_url)):
            print(f'Wrote {path.name} ({path.stat().st_size:,} bytes).')
    except OSError as error:
        # One line and exit code 1, not a traceback: build.ps1 stops on the exit code and CI shows the line.
        print(f'error: {error}', file=sys.stderr)
        return 1
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
