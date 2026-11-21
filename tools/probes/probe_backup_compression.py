#!/usr/bin/env python3
"""Measure how a BetterClipboard backup compresses: which algorithm, and which preparation of the history.

Why: the cloud backup (docs/website-src/guides/cloud-backup.md, CLAUDE.md section 2.68) compresses before it encrypts,
because encrypted data does not compress. The history store itself is encrypted at rest (SQLite3 Multiple Ciphers), so
the backup must read the history's content and compress that, not the database file. This probe answers three design
questions with numbers instead of guesses:

1. which compressor gives the smallest backup at a speed a background job can afford;
2. what to leave out or transform first (data the app can rebuild: the search index, previews, thumbnails, content
   hashes, a picture's DIB next to its PNG; copied text as UTF-8 instead of the clipboard's UTF-16);
3. how small the nightly increments are, and whether a dictionary trained on the earlier history helps them.

The history cannot be measured here: it is the owner's, and it is encrypted. So the probe builds a clipboard-like corpus
from public material only:

- copied text: line ranges sampled from this repository's own tracked files (C#, XAML, Markdown, PowerShell, Python,
  JSON; the language catalogs bring Chinese, Hebrew, Russian and Spanish), stored as the clipboard's UTF-16 text, and a
  share of the code copies also as a VS Code-style HTML copy (a span with an inline color per token);
- copied pictures: the repository's screenshots (docs/images, the Store listing) and the demo data's pictures
  (tools/readme/demo-data.zip), each stored the way BetterClipboard keeps a screenshot copy: the PNG plus a 32-bit DIBV5
  of the same pixels, and a 720 x 400 thumbnail.

It prints sizes, ratios and speeds only, never content. Every compression is checked by decompressing it again.

Requires Python 3.10+ with zstandard, brotli, Pillow and numpy (pip install zstandard brotli pillow numpy); lzma, zlib
and sqlite3 (with FTS5) come with Python. Single-threaded speeds unless a line says otherwise.

Usage:
    python tools/probes/probe_backup_compression.py [--quick] [--webp] [--dump DIR] [--seed N] [--repo PATH]

PPMd is measured through 7-Zip when it is installed (7z.exe on the PATH, in Program Files or Chocolatey's tools).

Exit codes: 0 measured; 1 a compressor did not give back its input, or a PNG rebuilt from its prepared rows had other
pixels or chunks (the numbers would be wrong); 2 bad usage or a missing module.
"""

from __future__ import annotations

import argparse
import hashlib
import io
import json
import lzma
import os
import random
import sqlite3
import struct
import subprocess
import sys
import tempfile
import time
import zipfile
import zlib
from dataclasses import dataclass
from pathlib import Path
from typing import Callable

try:
    import brotli
    import numpy as np
    import zstandard
    from PIL import Image
except ImportError as missing:  # The probe cannot measure without them; say which and how to get it.
    print(f'Missing module: {missing.name}. Install with: pip install zstandard brotli pillow numpy', file=sys.stderr)
    sys.exit(2)

# File kinds sampled as copied text. Binary and generated files are left out; JSON brings the language catalogs.
TEXT_SUFFIXES = {'.cs', '.xaml', '.md', '.ps1', '.py', '.json', '.yml', '.yaml', '.csproj', '.props', '.txt'}

# The store's caps (CLAUDE.md sections 2.6 and 2.57): the indexed search text and the card preview, both derivable.
SEARCH_TEXT_CAP = 32_768
PREVIEW_CAP = 16_000

# A thumbnail's box (CLAUDE.md section 2.3): derivable from the picture, so a backup can leave it out.
THUMBNAIL_BOX = (720, 400)


@dataclass
class Copy:
    """One copied item of the corpus, with what the store keeps for it.

    kind: "text" or "picture".
    group: what a solid stream would sort by to keep similar items together (the file suffix, or "picture").
    formats: format name -> payload bytes exactly as the clipboard gave them (what a restore must give back).
    derivable: data the store keeps but can rebuild from the formats (search text, preview, thumbnail, content hash).
    metadata: the small per-entry record a backup must keep (times, source app, flags), as JSON bytes.
    """

    kind: str
    group: str
    formats: dict[str, bytes]
    derivable: dict[str, bytes]
    metadata: bytes


@dataclass
class Compressor:
    """A compression setting under test.

    name: how the report names it.
    compress: bytes -> compressed bytes.
    decompress: compressed bytes -> bytes (used to prove the round trip and to time a restore).
    """

    name: str
    compress: Callable[[bytes], bytes]
    decompress: Callable[[bytes], bytes]


def tracked_text_files(repo: Path) -> list[Path]:
    """Lists the repository's tracked text files that copied text is sampled from.

    Args:
        repo: the repository's root folder.

    Returns:
        Existing files with a suffix in TEXT_SUFFIXES, in git's order (stable, so a seed reproduces the corpus).

    Raises:
        subprocess.CalledProcessError: git is missing or the folder is not a repository.
    """
    listed = subprocess.run(['git', '-C', str(repo), 'ls-files', '-z'], check=True, capture_output=True).stdout
    files = []
    for name in listed.decode('utf-8').split('\0'):
        path = repo / name
        if name and path.suffix.lower() in TEXT_SUFFIXES and path.is_file():
            files.append(path)
    return files


def sample_text_copies(files: list[Path], count: int, rng: random.Random) -> list[tuple[str, str]]:
    """Samples what people copy: mostly a few lines (a name, a path, a command), sometimes a block, rarely a whole file.

    The mix (60 % one to three lines, 30 % four to forty, 10 % up to four hundred) is an assumption, not a measurement;
    the report's conclusions do not hinge on it (they hold for each share alone). Copies are made unique, as the store
    keeps one entry per content.

    Args:
        files: the files to sample from (tracked_text_files).
        count: how many distinct copies to return.
        rng: the random source (seeded, so runs compare).

    Returns:
        (group, text) pairs: the file's suffix and the copied text.
    """
    texts = []
    for path in files:
        try:
            lines = path.read_text(encoding='utf-8', errors='strict').splitlines()
        except (UnicodeDecodeError, OSError):
            continue  # A file that is not UTF-8 text is not a realistic copy source; skip it.
        if lines:
            texts.append((path.suffix.lower().lstrip('.'), lines, max(1, sum(map(len, lines)))))
    weights = [size for _, _, size in texts]
    seen: set[str] = set()
    copies: list[tuple[str, str]] = []
    attempts = 0
    while len(copies) < count and attempts < count * 20:
        attempts += 1
        suffix, lines, _ = rng.choices(texts, weights=weights)[0]
        roll = rng.random()
        length = rng.randint(1, 3) if roll < 0.6 else rng.randint(4, 40) if roll < 0.9 else rng.randint(41, 400)
        start = rng.randrange(0, max(1, len(lines) - length + 1))
        text = '\r\n'.join(lines[start:start + length]).strip()  # The clipboard's line breaks are CRLF.
        if text and text not in seen:
            seen.add(text)
            copies.append((suffix, text))
    return copies


def vscode_html(text: str, rng: random.Random) -> bytes:
    """Builds the "HTML Format" payload a code editor puts next to copied code: a span with an inline color per token.

    Args:
        text: the copied code.
        rng: picks token colors (an editor's palette has a handful).

    Returns:
        The CF_HTML payload: its Version/StartHTML/EndHTML header and the UTF-8 document, as Windows stores it.
    """
    palette = ['#569cd6', '#4ec9b0', '#ce9178', '#dcdcaa', '#9cdcfe', '#6a9955', '#c586c0', '#d4d4d4']
    rows = []
    for line in text.split('\r\n'):
        tokens = line.split(' ')
        spans = ''.join(f'<span style="color: {rng.choice(palette)};">{escape_html(token)}</span>&nbsp;'
                        for token in tokens)
        rows.append(f'<div><span>{spans}</span></div>')
    fragment = ('<div style="color: #d4d4d4;background-color: #1e1e1e;font-family: Consolas, \'Courier New\', '
                'monospace;font-weight: normal;font-size: 14px;line-height: 19px;white-space: pre;">'
                + ''.join(rows) + '</div>')
    return cf_html(fragment)


def escape_html(text: str) -> str:
    """Escapes the characters HTML gives a meaning to.

    Args:
        text: plain text.

    Returns:
        The text safe inside an element.
    """
    return text.replace('&', '&amp;').replace('<', '&lt;').replace('>', '&gt;')


def cf_html(fragment: str) -> bytes:
    """Wraps an HTML fragment the way Windows' "HTML Format" clipboard format carries it.

    Args:
        fragment: the copied HTML.

    Returns:
        The header (byte offsets of the document and the fragment, zero-padded to ten digits) and the UTF-8 document.
    """
    head = 'Version:0.9\r\nStartHTML:{0:010d}\r\nEndHTML:{1:010d}\r\nStartFragment:{2:010d}\r\nEndFragment:{3:010d}\r\n'
    prefix = '<html><body>\r\n<!--StartFragment-->'
    suffix = '<!--EndFragment-->\r\n</body>\r\n</html>'
    header_length = len(head.format(0, 0, 0, 0).encode('ascii'))
    body = (prefix + fragment + suffix).encode('utf-8')
    start_fragment = header_length + len(prefix.encode('utf-8'))
    end_fragment = start_fragment + len(fragment.encode('utf-8'))
    header = head.format(header_length, header_length + len(body), start_fragment, end_fragment).encode('ascii')
    return header + body


def metadata_record(index: int, kind: str, rng: random.Random) -> bytes:
    """The per-entry record a backup keeps besides the content: times, source app, flags.

    Args:
        index: the entry's position (older entries have earlier times).
        kind: "text" or "picture".
        rng: picks a source app and flags.

    Returns:
        The record as compact JSON bytes (the real format's size is close: a few dozen bytes of values per entry).
    """
    apps = [('Windows Terminal', r'C:\Program Files\WindowsApps\Microsoft.WindowsTerminal\WindowsTerminal.exe'),
            ('Visual Studio Code', r'C:\Users\user\AppData\Local\Programs\Microsoft VS Code\Code.exe'),
            ('Google Chrome', r'C:\Program Files\Google\Chrome\Application\chrome.exe'),
            ('Notepad++', r'C:\Program Files\Notepad++\notepad++.exe'),
            ('Snipping Tool', r'C:\Program Files\WindowsApps\Microsoft.ScreenSketch\SnippingTool\SnippingTool.exe')]
    name, path = rng.choice(apps)
    base = 1_790_000_000_000 + index * 37_000  # Unix milliseconds, a copy every half minute or so.
    record = {'first': base, 'used': base + rng.randint(0, 9_000_000), 'kind': kind, 'app': name, 'path': path,
              'pinned': rng.random() < 0.01, 'groups': [rng.randint(1, 4)] if rng.random() < 0.03 else [],
              'uses': rng.randint(1, 5)}
    return json.dumps(record, separators=(',', ':')).encode('utf-8')


def text_copy(index: int, group: str, text: str, rng: random.Random, html_share: float) -> Copy:
    """Stores a copied text the way the history does, with its derivable parts.

    Args:
        index: the entry's position.
        group: the file suffix it came from.
        text: the copied text.
        rng: decides whether a code copy also carries HTML.
        html_share: the share of code copies that also carry an HTML rendition.

    Returns:
        The entry: UTF-16LE text with its terminator (CF_UNICODETEXT), sometimes HTML, and the derivable search text,
        preview and content hash.
    """
    formats = {'CF_UNICODETEXT': text.encode('utf-16-le') + b'\0\0'}
    if group in {'cs', 'xaml', 'py', 'ps1'} and rng.random() < html_share:
        formats['HTML Format'] = vscode_html(text, rng)
    derivable = {'search_text': text[:SEARCH_TEXT_CAP].encode('utf-8'),
                 'preview': text[:PREVIEW_CAP].encode('utf-8'),
                 'hash': hashlib.sha256(formats['CF_UNICODETEXT']).digest()}
    return Copy('text', group, formats, derivable, metadata_record(index, 'text', rng))


def dibv5(image: Image.Image) -> bytes:
    """Encodes pixels as the CF_DIBV5 payload Windows and BetterClipboard keep for a screenshot copy.

    Args:
        image: the picture (any mode; converted to 32-bit BGRA).

    Returns:
        A BITMAPV5HEADER (124 bytes, BI_BITFIELDS with the BGRA masks) followed by bottom-up BGRA rows.
    """
    rgba = image.convert('RGBA')
    width, height = rgba.size
    # Pillow gives RGBA top-down; a DIB is BGRA bottom-up. Swap the channels with Pillow, then flip the rows.
    pixels = rgba.transpose(Image.Transpose.FLIP_TOP_BOTTOM).tobytes('raw', 'BGRA')
    header = struct.pack('<IiiHHIIiiII', 124, width, height, 1, 32, 3, len(pixels), 3780, 3780, 0, 0)
    header += struct.pack('<IIII', 0x00FF0000, 0x0000FF00, 0x000000FF, 0xFF000000)  # The R, G, B, A masks.
    header += struct.pack('<I', 0x73524742)  # LCS_sRGB.
    header += bytes(124 - len(header) - 16) + struct.pack('<IIII', 4, 0, 0, 0)  # Endpoints and gamma zero; intent.
    return header + pixels


def thumbnail_png(image: Image.Image) -> bytes:
    """The card thumbnail the store keeps for a picture: derivable, so a backup can leave it out.

    Args:
        image: the picture.

    Returns:
        A PNG no larger than THUMBNAIL_BOX.
    """
    small = image.convert('RGBA')
    small.thumbnail(THUMBNAIL_BOX)
    buffer = io.BytesIO()
    small.save(buffer, format='PNG')
    return buffer.getvalue()


def collect_pictures(repo: Path, limit: int | None) -> list[tuple[str, bytes]]:
    """Gathers public pictures: the repository's screenshots and the demo data's pictures.

    Args:
        repo: the repository's root folder.
        limit: at most this many (the quick run), or None for all.

    Returns:
        (name, PNG bytes) pairs, in a stable order.
    """
    pictures: list[tuple[str, bytes]] = []
    for folder in ('docs/images', 'docs/images/gallery', 'packaging/msix/Listing/Screenshots'):
        for path in sorted((repo / folder).glob('*.png')):
            pictures.append((path.name, path.read_bytes()))
    demo = repo / 'tools/readme/demo-data.zip'
    if demo.is_file():
        with zipfile.ZipFile(demo) as archive:
            for info in sorted(archive.infolist(), key=lambda item: item.filename):
                if info.filename.lower().endswith('.png'):
                    pictures.append((Path(info.filename).name, archive.read(info)))
    return pictures[:limit] if limit else pictures


def picture_copy(index: int, png: bytes, rng: random.Random) -> tuple[Copy, Image.Image]:
    """Stores a picture the way a screenshot copy is kept: its PNG, its DIBV5, a thumbnail and its pixel hash.

    Args:
        index: the entry's position.
        png: the picture's PNG bytes.
        rng: for the metadata record.

    Returns:
        The entry and the decoded picture (for the image measurements).
    """
    image = Image.open(io.BytesIO(png))
    image.load()
    dib = dibv5(image)
    derivable = {'thumbnail': thumbnail_png(image), 'hash': hashlib.sha256(image.convert('RGBA').tobytes()).digest()}
    return Copy('picture', 'picture', {'PNG': png, 'CF_DIBV5': dib}, derivable,
                metadata_record(index, 'picture', rng)), image


def frame(chunks: list[bytes]) -> bytes:
    """Joins records with a length before each, as a backup stream would.

    Args:
        chunks: the records.

    Returns:
        The stream.
    """
    out = io.BytesIO()
    for chunk in chunks:
        out.write(struct.pack('<I', len(chunk)))
        out.write(chunk)
    return out.getvalue()


def text_stream(copies: list[Copy], *, keep_derivable: bool, utf8: bool, grouped: bool) -> bytes:
    """Serializes the text entries as one backup stream under a choice of preparations.

    Args:
        copies: the text entries.
        keep_derivable: keep the search text, preview and content hash (as the store has them) or leave them out.
        utf8: store CF_UNICODETEXT as UTF-8 (restored exactly by converting back) instead of the clipboard's UTF-16.
        grouped: sort the entries by where they came from, so similar text sits together in the stream.

    Returns:
        The stream's bytes.
    """
    ordered = sorted(copies, key=lambda copy: copy.group) if grouped else copies
    records = []
    for copy in ordered:
        records.append(copy.metadata)
        for name, payload in copy.formats.items():
            if utf8 and name == 'CF_UNICODETEXT':
                payload = payload[:-2].decode('utf-16-le').encode('utf-8')  # The terminator is added back on restore.
            records.append(name.encode('ascii'))
            records.append(payload)
        if keep_derivable:
            records.extend(copy.derivable.values())
    return frame(records)


def zstd(level: int, long_window: int | None = None, dictionary: zstandard.ZstdCompressionDict | None = None,
         threads: int = 0) -> Compressor:
    """A zstd setting (the app already ships ZstdSharp, a port of the same library).

    Args:
        level: the compression level (1 to 22; above 19 is "ultra").
        long_window: log2 of the long-distance matching window (27 = 128 MiB), or None for zstd's default.
        dictionary: a trained dictionary, or None.
        threads: worker threads (0 = single-threaded; -1 = one per logical processor).

    Returns:
        The compressor and its matching decompressor.
    """
    parameters = zstandard.ZstdCompressionParameters.from_level(
        level, window_log=long_window or 0, enable_ldm=bool(long_window), threads=threads)
    compressor = zstandard.ZstdCompressor(compression_params=parameters, dict_data=dictionary)
    decompressor = zstandard.ZstdDecompressor(dict_data=dictionary, max_window_size=2 ** 31)
    name = f'zstd -{level}' + (f' --long={long_window}' if long_window else '') + (' +dictionary' if dictionary else '')
    name += f' ({os.cpu_count()} threads)' if threads else ''
    return Compressor(name, compressor.compress, lambda data: decompressor.decompress(data, max_output_size=2 ** 31))


def standard_compressors(quick: bool) -> list[Compressor]:
    """The algorithms compared on the prepared text stream.

    Args:
        quick: leave out the slowest settings.

    Returns:
        zlib (the baseline), zstd at four settings, brotli 11 with a 16 MiB window and xz at three presets.
    """
    compressors = [
        Compressor('zlib -9 (deflate)', lambda d: zlib.compress(d, 9), zlib.decompress),
        zstd(3), zstd(9), zstd(19), zstd(19, 27),
        Compressor('xz -6 (LZMA2)', lambda d: lzma.compress(d, preset=6), lzma.decompress),
        Compressor('xz -9 (LZMA2)', lambda d: lzma.compress(d, preset=9), lzma.decompress),
    ]
    if not quick:
        compressors += [
            zstd(22, 27),
            Compressor('brotli -11 (16 MiB window)', lambda d: brotli.compress(d, quality=11, lgwin=24),
                       brotli.decompress),
            Compressor('xz -9e (LZMA2 extreme)', lambda d: lzma.compress(d, preset=9 | lzma.PRESET_EXTREME),
                       lzma.decompress),
        ]
    return compressors


def measure(compressor: Compressor, data: bytes) -> tuple[int, float, float]:
    """Compresses, decompresses and checks the round trip.

    Args:
        compressor: the setting.
        data: the input.

    Returns:
        (compressed size, seconds to compress, seconds to decompress).

    Raises:
        SystemExit: with code 1 when the round trip does not give the input back.
    """
    start = time.perf_counter()
    packed = compressor.compress(data)
    middle = time.perf_counter()
    unpacked = compressor.decompress(packed)
    end = time.perf_counter()
    if unpacked != data:
        print(f'{compressor.name} did not give its input back: the numbers would be wrong.', file=sys.stderr)
        sys.exit(1)
    return len(packed), middle - start, end - middle


def mb(size: float) -> str:
    """Formats a byte count in decimal megabytes.

    Args:
        size: bytes.

    Returns:
        The count with two decimals and "MB".
    """
    return f'{size / 1e6:8.2f} MB'


def row(label: str, size: int, base: int, compress_s: float | None = None, decompress_s: float | None = None,
        raw: int | None = None) -> str:
    """Formats one line of a report table.

    Args:
        label: what was measured.
        size: the result's size in bytes.
        base: the size the percentage is relative to.
        compress_s, decompress_s: timings, or None when not timed.
        raw: the uncompressed input's size, for the speeds (defaults to base).

    Returns:
        The aligned line.
    """
    line = f'  {label:<44}{mb(size)}  {100 * size / base:6.1f} %'
    if compress_s is not None and decompress_s is not None:
        source = raw or base
        line += f'   compress {source / 1e6 / max(compress_s, 1e-9):7.1f} MB/s   restore {source / 1e6 / max(decompress_s, 1e-9):7.1f} MB/s'
    return line


def physical_database(copies: list[Copy], folder: Path) -> int:
    """Builds a plaintext SQLite database shaped like the history store, for the "back up the file" comparison.

    Args:
        copies: every entry (texts and pictures).
        folder: a scratch folder for the file.

    Returns:
        The database file's size after VACUUM.
    """
    path = folder / 'history-plaintext.db'
    connection = sqlite3.connect(path)
    connection.executescript("""
        PRAGMA page_size = 4096;
        CREATE TABLE clips(id INTEGER PRIMARY KEY, content_hash BLOB, kind INTEGER, preview TEXT, search_text TEXT,
                           metadata TEXT, thumbnail BLOB);
        CREATE TABLE clip_formats(clip_id INTEGER, format TEXT, data BLOB);
        CREATE VIRTUAL TABLE clips_fts USING fts5(search_text, content='clips', content_rowid='id', tokenize='trigram');
    """)
    for index, copy in enumerate(copies, start=1):
        search = copy.derivable.get('search_text', b'').decode('utf-8')
        connection.execute('INSERT INTO clips VALUES (?, ?, ?, ?, ?, ?, ?)',
                           (index, copy.derivable['hash'], 1 if copy.kind == 'text' else 3,
                            copy.derivable.get('preview', b'').decode('utf-8'), search,
                            copy.metadata.decode('utf-8'), copy.derivable.get('thumbnail')))
        connection.execute('INSERT INTO clips_fts(rowid, search_text) VALUES (?, ?)', (index, search))
        for name, payload in copy.formats.items():
            connection.execute('INSERT INTO clip_formats VALUES (?, ?, ?)', (index, name, payload))
    connection.commit()
    connection.execute('VACUUM')
    connection.close()
    return path.stat().st_size


def find_seven_zip() -> Path | None:
    """Finds 7-Zip, which brings the PPMd algorithm (the strongest of the common ones on text) to the comparison.

    Returns:
        The path of 7z.exe (on the PATH, in Program Files, or Chocolatey's copy), or None when 7-Zip is not installed:
        the PPMd line is then left out of the report.
    """
    candidates = [Path(p) / '7z.exe' for p in os.environ.get('PATH', '').split(os.pathsep) if p]
    candidates += [Path(os.environ.get('ProgramFiles', r'C:\Program Files')) / '7-Zip' / '7z.exe',
                   Path(os.environ.get('ProgramData', r'C:\ProgramData')) / 'chocolatey' / 'tools' / '7z.exe']
    return next((path for path in candidates if path.is_file()), None)


def seven_zip_ppmd(seven_zip: Path, order: int, memory_mb: int) -> Compressor:
    """PPMd (variant H, as in 7-Zip) at a model order and memory, run through 7-Zip's command line.

    The compressed size includes 7-Zip's small container (a few hundred bytes), which a backup would not carry; on
    megabytes of output that is noise. The data goes through temporary files: 7-Zip has no in-memory interface.

    Args:
        seven_zip: 7z.exe.
        order: the context model's order (2 to 32; 7-Zip's strongest text setting uses 32).
        memory_mb: the model's memory in MB.

    Returns:
        The compressor and its decompressor.
    """
    def compress(data: bytes) -> bytes:
        with tempfile.TemporaryDirectory(prefix='bc-ppmd-') as scratch:
            source, archive = Path(scratch) / 'stream.bin', Path(scratch) / 'stream.7z'
            source.write_bytes(data)
            subprocess.run([str(seven_zip), 'a', '-t7z', f'-m0=PPMd:mem={memory_mb}m:o={order}', '-mx=9', '-mmt=1',
                            str(archive), str(source)], check=True, capture_output=True)
            return archive.read_bytes()

    def decompress(packed: bytes) -> bytes:
        with tempfile.TemporaryDirectory(prefix='bc-ppmd-') as scratch:
            archive = Path(scratch) / 'stream.7z'
            archive.write_bytes(packed)
            return subprocess.run([str(seven_zip), 'e', '-so', str(archive)], check=True, capture_output=True).stdout

    return Compressor(f'PPMd o={order} {memory_mb} MB (7-Zip)', compress, decompress)


def xz_chain(name: str, filters: list[dict]) -> Compressor:
    """An xz setting with an explicit filter chain (a delta filter in front of LZMA2, for pixels).

    Args:
        name: how the report names it.
        filters: the lzma module's filter chain.

    Returns:
        The compressor and its decompressor.
    """
    return Compressor(name, lambda d: lzma.compress(d, format=lzma.FORMAT_XZ, filters=filters), lzma.decompress)


def png_chunks(png: bytes) -> list[tuple[bytes, bytes]]:
    """Splits a PNG into its chunks.

    Args:
        png: the file's bytes.

    Returns:
        (type, data) pairs in file order (the CRCs are dropped: they are recomputed when the file is rebuilt).

    Raises:
        ValueError: the bytes are not a PNG.
    """
    if png[:8] != b'\x89PNG\r\n\x1a\n':
        raise ValueError('not a PNG')
    chunks, position = [], 8
    while position < len(png):
        length, kind = struct.unpack('>I4s', png[position:position + 8])
        chunks.append((kind, png[position + 8:position + 8 + length]))
        position += 12 + length
    return chunks


def png_unpacked(png: bytes) -> bytes:
    """Prepares a PNG for the stream compressor: its pixel rows inflated, every other chunk kept as it is.

    A PNG's IDAT chunks hold its pixel rows (after the encoder's per-row filters) deflated with zlib, which the stronger
    stream compressor cannot see through. Inflating them hands the compressor the filtered rows themselves; a restore
    deflates them again (png_repacked), giving a PNG with the same pixels and the same metadata chunks (color profile,
    gamma, resolution), though not the same bytes.

    Args:
        png: the PNG as the app saved it.

    Returns:
        A framed record: the chunks in file order with an empty IDAT marking where the first IDAT stood, then the
        inflated IDAT data.
    """
    chunks = png_chunks(png)
    pixels = zlib.decompress(b''.join(data for kind, data in chunks if kind == b'IDAT'))
    return frame(png_kept_chunks(chunks) + [pixels])


def png_kept_chunks(chunks: list[tuple[bytes, bytes]]) -> list[bytes]:
    """The chunks a prepared PNG record keeps as they are: every chunk but the IDATs, and one marker where they stood.

    Args:
        chunks: png_chunks' list.

    Returns:
        Each kept chunk as its type followed by its data, in file order, with an empty IDAT marking where the first
        IDAT stood (some chunks must come before the pixel data, some may follow it; png_repacked puts it back there).
    """
    kept, marked = [], False
    for kind, data in chunks:
        if kind != b'IDAT':
            kept.append(kind + data)
        elif not marked:  # One marker keeps the IDAT's place.
            kept.append(b'IDAT')
            marked = True
    return kept


def png_refiltered(png: bytes) -> bytes:
    """Prepares a PNG like png_unpacked, with every pixel row filtered again by the pixel to its left.

    An app's PNG encoder picks each row's filter (none, the pixel to the left, the row above, their average, or Paeth's
    guess) to suit deflate. To a stronger compressor those mixed choices are noise: the 50 screenshots' rows, as their
    apps filtered them, packed about 10 % larger with xz than the same pixels as a DIB with each pixel stored as its
    difference from the one to its left. So this preparation decodes the pixels and filters every row with PNG's own Sub
    filter (each byte less the same channel of the pixel to its left). Its rows are valid PNG scanlines (filter type 1 in
    each row's first byte), so png_repacked restores the record unchanged: a PNG with the original's pixels and every
    other chunk, deflated again.

    Palette pictures keep their indices unfiltered (an index is a label, and the difference between two labels says
    nothing). Formats this probe does not decode itself keep their own rows, as png_unpacked does: bit depths other
    than 8, interlaced files, and anything Pillow decodes to other than one byte a channel. The corpus has none of those
    (29 RGB, 18 RGBA and 3 palette screenshots, all 8-bit and not interlaced); BetterClipboard would unfilter every
    kind itself, as any PNG decoder does.

    Args:
        png: the PNG as the app saved it.

    Returns:
        A framed record in png_unpacked's shape: the kept chunks, then the re-filtered rows.
    """
    chunks = png_chunks(png)
    header = next(data for kind, data in chunks if kind == b'IHDR')
    width, height, depth, color, _, _, interlace = struct.unpack('>IIBBBBB', header)
    channels = {0: 1, 2: 3, 3: 1, 4: 2, 6: 4}.get(color)  # Grey, RGB, palette, grey + alpha, RGBA.
    if depth != 8 or interlace or channels is None:
        return png_unpacked(png)
    image = Image.open(io.BytesIO(png))
    image.load()
    samples = np.frombuffer(image.tobytes(), dtype=np.uint8)
    if samples.size != width * height * channels:  # Pillow decoded to another layout: keep the app's own rows.
        return png_unpacked(png)
    rows = samples.reshape(height, width * channels)
    filtered = np.empty((height, 1 + width * channels), dtype=np.uint8)
    if color == 3:
        filtered[:, 0] = 0  # Filter type None.
        filtered[:, 1:] = rows
    else:
        filtered[:, 0] = 1  # Filter type Sub: the row's first pixel has nothing to its left, so it stays as it is.
        filtered[:, 1:1 + channels] = rows[:, :channels]
        # uint8 arrays wrap modulo 256 on subtraction, exactly as PNG's filter arithmetic does.
        filtered[:, 1 + channels:] = rows[:, channels:] - rows[:, :-channels]
    return frame(png_kept_chunks(chunks) + [filtered.tobytes()])


def png_repacked(record: bytes) -> bytes:
    """Rebuilds a PNG from png_unpacked's or png_refiltered's record (what a restore would do).

    Args:
        record: the framed record.

    Returns:
        A valid PNG: the kept chunks in their order, and one IDAT, where the first one stood, holding the rows
        deflated at zlib level 9.
    """
    parts, position = [], 0
    while position < len(record):
        (length,) = struct.unpack('<I', record[position:position + 4])
        parts.append(record[position + 4:position + 4 + length])
        position += 4 + length
    *others, pixels = parts
    out = io.BytesIO()
    out.write(b'\x89PNG\r\n\x1a\n')

    def chunk(kind: bytes, data: bytes) -> None:
        """Writes one chunk: its length, type, data and CRC (over the type and the data, as PNG defines it)."""
        out.write(struct.pack('>I', len(data)) + kind + data + struct.pack('>I', zlib.crc32(kind + data)))

    for other in others:
        kind, data = other[:4], other[4:]
        chunk(kind, zlib.compress(pixels, 9) if kind == b'IDAT' else data)
    return out.getvalue()


def main() -> int:
    """Builds the corpus, runs every measurement and prints the report.

    Returns:
        The exit code (0; failures exit from inside with 1 or 2).
    """
    parser = argparse.ArgumentParser(description=__doc__.split('\n\n')[0])
    parser.add_argument('--quick', action='store_true', help='a smaller corpus and only the faster settings')
    parser.add_argument('--seed', type=int, default=20261121, help='the random seed (default: the design date)')
    parser.add_argument('--repo', type=Path, default=Path(__file__).resolve().parents[2], help='the repository root')
    parser.add_argument('--webp', action='store_true', help='also re-encode the pictures as lossless WebP (slow)')
    parser.add_argument('--dump', type=Path, help='write the prepared text and picture streams into this folder, '
                        'to try other compressors on exactly the same input (public material only)')
    arguments = parser.parse_args()
    rng = random.Random(arguments.seed)
    quick = arguments.quick

    print(f'Python {sys.version.split()[0]}, zstd {".".join(map(str, zstandard.ZSTD_VERSION))}, '
          f'{os.cpu_count()} logical processors, seed {arguments.seed}{", quick" if quick else ""}')

    # Text: the base history, and a later day's new copies (the nightly increment), never overlapping.
    files = tracked_text_files(arguments.repo)
    base_count, day_count = (3000, 150) if quick else (12000, 150)
    sampled = sample_text_copies(files, base_count + day_count, rng)
    texts = [text_copy(i, group, text, rng, html_share=0.15) for i, (group, text) in enumerate(sampled[:base_count])]
    day = [text_copy(base_count + i, group, text, rng, html_share=0.15)
           for i, (group, text) in enumerate(sampled[base_count:])]
    print(f'Text corpus: {len(texts)} copies sampled from {len(files)} tracked files, '
          f'{sum("HTML Format" in c.formats for c in texts)} of them also as HTML.')

    # 1. Preparations of the text stream, all with one strong setting.
    print('\n1. Text: what the stream holds (zstd -19 --long=27 on each; % of the stream as stored)')
    reference = zstd(19, 27)
    variants = [
        ('as stored (UTF-16, search text, preview, hash)', dict(keep_derivable=True, utf8=False, grouped=False)),
        ('derivable data left out', dict(keep_derivable=False, utf8=False, grouped=False)),
        ('... and text as UTF-8', dict(keep_derivable=False, utf8=True, grouped=False)),
        ('... and grouped by source', dict(keep_derivable=False, utf8=True, grouped=True)),
    ]
    streams = {}
    as_stored = None
    for label, options in variants:
        stream = text_stream(texts, **options)
        streams[label] = stream
        as_stored = as_stored or len(stream)
        size, _, _ = measure(reference, stream)
        print(f'  {label:<48}raw {mb(len(stream))} -> {mb(size)}  ({100 * size / as_stored:5.1f} % of the raw stream)')

    # 2. Algorithms on the best preparation.
    prepared = streams['... and grouped by source']
    print(f'\n2. Text: algorithms on the prepared stream ({mb(len(prepared)).strip()} raw; % of raw)')
    results = []
    for compressor in standard_compressors(quick):
        size, c_s, d_s = measure(compressor, prepared)
        results.append((compressor.name, size))
        print(row(compressor.name, size, len(prepared), c_s, d_s))
    threaded = zstd(19, 27, threads=-1)
    size, c_s, d_s = measure(threaded, prepared)
    print(row(threaded.name, size, len(prepared), c_s, d_s))
    seven_zip = find_seven_zip()
    ppmd = [seven_zip_ppmd(seven_zip, order, 512) for order in (8, 32)] if seven_zip else []
    for compressor in ppmd:
        size, c_s, d_s = measure(compressor, prepared)
        print(row(compressor.name, size, len(prepared), c_s, d_s))
    if not seven_zip:
        print('  (PPMd left out: 7-Zip is not installed)')

    # 3. The nightly increment: a day's new copies alone, and with a dictionary trained on the earlier history.
    day_stream = text_stream(day, keep_derivable=False, utf8=True, grouped=True)
    print(f'\n3. Text: a day of {len(day)} new copies ({mb(len(day_stream)).strip()} raw; % of raw)')
    samples = [text_stream([copy], keep_derivable=False, utf8=True, grouped=False) for copy in texts]
    dictionary = zstandard.train_dictionary(112_640, samples, level=19)
    xz9e = Compressor('xz -9e (LZMA2 extreme)', lambda d: lzma.compress(d, preset=9 | lzma.PRESET_EXTREME),
                      lzma.decompress)
    for compressor in [zstd(19), zstd(19, dictionary=dictionary), xz9e,
                       Compressor('brotli -11', lambda d: brotli.compress(d, quality=11), brotli.decompress),
                       *ppmd[-1:]]:
        size, c_s, d_s = measure(compressor, day_stream)
        print(row(compressor.name, size, len(day_stream)))
    print(f'  (the dictionary itself: {len(dictionary.as_bytes()):,} bytes, kept once per backup)')

    # 4. Pictures.
    pictures = collect_pictures(arguments.repo, 12 if quick else None)
    picture_copies = []
    decoded = []
    for i, (_, png) in enumerate(pictures):
        copy, image = picture_copy(base_count + day_count + i, png, rng)
        picture_copies.append(copy)
        decoded.append(image)
    png_total = sum(len(c.formats['PNG']) for c in picture_copies)
    dib_total = sum(len(c.formats['CF_DIBV5']) for c in picture_copies)
    thumbs = sum(len(c.derivable['thumbnail']) for c in picture_copies)
    pixels = sum(image.size[0] * image.size[1] for image in decoded)
    print(f'\n4. Pictures: {len(pictures)} screenshots, {pixels / 1e6:.1f} megapixels; as stored: PNG {mb(png_total).strip()}, '
          f'DIBV5 {mb(dib_total).strip()}, thumbnails {mb(thumbs).strip()}')
    stored = frame([part for c in picture_copies for part in (c.metadata, *c.formats.values(), *c.derivable.values())])
    size, c_s, _ = measure(zstd(19, 27), stored)
    print(f'  everything as stored, zstd -19 --long=27:           {mb(size)}  ({100 * size / png_total:5.1f} % of the PNGs, {c_s:5.1f} s)')
    # (preparation, compressor name) -> size, for section 5 to take the smallest preparation of each compressor.
    best_pictures: dict[tuple[str, str], int] = {}
    png_only = frame([part for c in picture_copies for part in (c.metadata, c.formats['PNG'])])
    preparations = {
        'PNG rows inflated, filtered as the app chose': png_unpacked,
        'PNG rows inflated, re-filtered by the pixel to the left': png_refiltered,
    }
    prepared_pictures = {label: frame([part for c in picture_copies for part in (c.metadata, prepare(c.formats['PNG']))])
                         for label, prepare in preparations.items()}
    if arguments.dump:
        # The exact inputs of sections 2 and 4, for other compressors (a .NET library, a native one) to be compared on.
        arguments.dump.mkdir(parents=True, exist_ok=True)
        (arguments.dump / 'text-prepared.bin').write_bytes(prepared)
        (arguments.dump / 'pictures-png-rows.bin').write_bytes(
            prepared_pictures['PNG rows inflated, filtered as the app chose'])
        (arguments.dump / 'pictures-png-sub.bin').write_bytes(
            prepared_pictures['PNG rows inflated, re-filtered by the pixel to the left'])
        print(f'  (the streams are in {arguments.dump})')
    for c, image in zip(picture_copies, decoded):
        # The restore's promise: the rebuilt PNG has the original's pixels and every other chunk as it was. Checked for
        # every picture and every preparation.
        kept = [chunk for chunk in png_chunks(c.formats['PNG']) if chunk[0] != b'IDAT']
        for label, prepare in preparations.items():
            rebuilt_png = png_repacked(prepare(c.formats['PNG']))
            rebuilt = Image.open(io.BytesIO(rebuilt_png))
            if (rebuilt.convert('RGBA').tobytes() != image.convert('RGBA').tobytes()
                    or [chunk for chunk in png_chunks(rebuilt_png) if chunk[0] != b'IDAT'] != kept):
                print(f'A PNG rebuilt from its {label} has other pixels or chunks: the numbers would be wrong.',
                      file=sys.stderr)
                return 1
    print('  Each picture as its PNG (DIB and thumbnail rebuilt on restore; pixels and other chunks checked exact for '
          'every one), per encoding (% of the PNGs as saved):')
    for label, stream in [('PNG bytes as saved', png_only), *prepared_pictures.items()]:
        for compressor in [zstd(19, 27), xz9e]:
            size, c_s, _ = measure(compressor, stream)
            best_pictures[(label, compressor.name)] = size
            print(f'    {label + ", " + compressor.name:<82}{mb(size)}  ({100 * size / png_total:5.1f} %, {c_s:6.1f} s)')
    # A picture never changes once copied, so a picture packed alone could be packed once, kept in the (encrypted)
    # history store and reused by every later full backup, instead of being packed again each week. Alone, it loses the
    # matches the solid stream finds in the pictures before it; this line measures that price.
    alone, start = 0, time.perf_counter()
    for c in picture_copies:
        record = frame([c.metadata, png_refiltered(c.formats['PNG'])])
        packed = xz9e.compress(record)
        if xz9e.decompress(packed) != record:
            print(f'{xz9e.name} did not give a picture back: the numbers would be wrong.', file=sys.stderr)
            return 1
        alone += len(packed)
    label = 'PNG rows re-filtered by the pixel to the left, each picture packed alone, ' + xz9e.name
    print(f'    {label:<82}{mb(alone)}  ({100 * alone / png_total:5.1f} %, {time.perf_counter() - start:6.1f} s)')
    dibs = frame([c.formats['CF_DIBV5'] for c in picture_copies])
    if arguments.dump:
        # The DIBs with each byte less the one 4 bytes before it (the same channel of the pixel to the left), the input
        # of the xz line below with its delta filter: for the .NET probe's PPMd and LZMA, which have no such filter.
        raw = np.frombuffer(dibs, dtype=np.uint8)
        delta = raw.copy()
        delta[4:] -= raw[:-4]  # uint8 arrays wrap modulo 256, as xz's delta filter does.
        (arguments.dump / 'pictures-dib-delta.bin').write_bytes(delta.tobytes())
    print('  A copy that came only as a DIB (no PNG), per encoding (% of the PNGs as saved, for scale):')
    delta = xz_chain('xz -9e with a 4-byte delta filter',
                     [{'id': lzma.FILTER_DELTA, 'dist': 4}, {'id': lzma.FILTER_LZMA2, 'preset': 9 | lzma.PRESET_EXTREME}])
    for compressor in [zstd(19, 27), Compressor('xz -9 (LZMA2)', lambda d: lzma.compress(d, preset=9), lzma.decompress),
                       xz9e, delta]:
        size, c_s, _ = measure(compressor, dibs)
        print(f'    raw DIB, {compressor.name:<73}{mb(size)}  ({100 * size / png_total:5.1f} %, {c_s:6.1f} s)')
    codecs = [('PNG, Pillow optimize', dict(format='PNG', optimize=True))]
    if arguments.webp:
        codecs.append(('WebP lossless, method 6', dict(format='WEBP', lossless=True, quality=100, method=6)))
    for label, options in codecs:
        start = time.perf_counter()
        total = 0
        for image in decoded:
            buffer = io.BytesIO()
            image.convert('RGBA').save(buffer, **options)
            total += len(buffer.getvalue())
        print(f'    re-encoded, {label:<70}{mb(total)}  ({100 * total / png_total:5.1f} %, {time.perf_counter() - start:6.1f} s)')

    # 5. The whole history: the database file itself (plaintext; the real one is encrypted and would not compress at
    # all) against a backup, prepared, with the two strongest general-purpose settings.
    with tempfile.TemporaryDirectory(prefix='bc-probe-') as scratch:
        file_size = physical_database(texts + picture_copies, Path(scratch))
        file_bytes = (Path(scratch) / 'history-plaintext.db').read_bytes()
        size_file, c_s, _ = measure(zstd(19, 27), file_bytes)
    print('\n5. The whole history: the database file against a backup')
    print(f'  plaintext SQLite file (index, previews, thumbnails, DIBs), zstd -19 --long=27: {mb(file_size)} -> {mb(size_file)}')
    for name, compressor in [('zstd -19 --long=27', zstd(19, 27)), ('xz -9e (LZMA2 extreme)', xz9e)]:
        text_size = measure(compressor, prepared)[0]
        picture_size, label = min((size, label) for (label, used), size in best_pictures.items()
                                  if used == compressor.name and label in preparations)
        print(f'  backup, {name:<24} text {mb(text_size)} + pictures {mb(picture_size)} = {mb(text_size + picture_size)}'
              f'  (pictures: {label})')
    print(f'  The real file is encrypted (ChaCha20-Poly1305): compressed it stays about {mb(file_size).strip()}.')
    return 0


if __name__ == '__main__':
    sys.exit(main())
