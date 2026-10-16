"""Probe: where Ditto (the open-source clipboard manager) keeps its history, and what a Ditto database holds.

Facts it reproduces are in docs/ditto.md and CLAUDE.md (section 2.32). It prints structure, counts and sizes
only - never a clip's description, quick-paste text or data (a Ditto history is unencrypted user data).

Usage:
    python tools/probes/probe_ditto.py                 # locate Ditto installs, settings and databases (read-only)
    python tools/probes/probe_ditto.py --db PATH       # describe one Ditto.db (opened read-only)
    python tools/probes/probe_ditto.py --self-test     # build a BC-TEST database shaped like Ditto's, describe it, check

Where Ditto keeps things (3.25.113, from its source at a80fd35, verified in a claude-desktops VM):
    installed  settings HKCU\\Software\\Ditto, database %APPDATA%\\Ditto\\Ditto.db
    portable   a "portable" file next to Ditto.exe; settings Ditto.Settings and Ditto.db next to the exe
    chocolatey a "chocolatey" file next to Ditto.exe; %LOCALAPPDATA%\\Ditto_ChocolateyApp\\
    store      a "WindowsApp" file next to Ditto.exe; %LOCALAPPDATA%\\Ditto_WindowsApp\\ inside the package
    The database path itself is the setting "DBPath3" (environment variables allowed, e.g. %COMPUTERNAME%).
"""

import argparse
import collections
import glob
import os
import sqlite3
import struct
import sys
import tempfile
import time

STICKY_NONE = -2147483647
"""Ditto's "not sticky" value for stickyClipOrder / stickyClipGroupOrder (-(2^31 - 1))."""

INI_SECTION = "[Ditto]"
"""The section Ditto writes its own values to in Ditto.Settings (other sections hold per-app key strings)."""


def read_ini_value(path, name):
    """Read one value of the [Ditto] section of a Ditto.Settings file, whatever its encoding.

    Ditto creates the file as UTF-16LE with a BOM so that it can store Unicode; older files may be ANSI.

    Args:
        path: Full path of the Ditto.Settings file.
        name: Value name, compared case-insensitively (Windows ini semantics).

    Returns:
        The value text, or None when the file, the section or the value is missing or unreadable.
    """
    try:
        raw = open(path, "rb").read()
    except OSError:
        return None
    if raw.startswith(b"\xff\xfe"):
        text = raw[2:].decode("utf-16-le", "replace")
    elif raw.startswith(b"\xef\xbb\xbf"):
        text = raw[3:].decode("utf-8", "replace")
    else:
        text = raw.decode("mbcs" if os.name == "nt" else "latin-1", "replace")
    in_section = False
    for line in text.splitlines():
        stripped = line.strip()
        if stripped.startswith("["):
            in_section = stripped.lower() == INI_SECTION.lower()
            continue
        if in_section and "=" in stripped:
            key, _, value = stripped.partition("=")
            if key.strip().lower() == name.lower():
                return value.strip()
    return None


def registry_db_path():
    """Read the installed Ditto's database path from HKCU\\Software\\Ditto (value DBPath3).

    Returns:
        (path or None, whether the key exists). Never raises: no registry (not Windows) reads as "no key".
    """
    try:
        import winreg
    except ImportError:
        return None, False
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, r"Software\Ditto") as key:
            try:
                value, _ = winreg.QueryValueEx(key, "DBPath3")
                return value, True
            except OSError:
                return None, True
    except OSError:
        return None, False


def candidate_settings_files():
    """List every Ditto.Settings file Ditto itself could use for the current user.

    Covers the app-data copy (installed version switched to an ini, the Chocolatey portable package) and the
    Microsoft Store package's virtualized folder. Portable copies live next to their exe and are found only
    through a running Ditto.exe (see running_ditto_paths) or --db.

    Returns:
        Existing file paths, deduplicated, in a stable order.
    """
    appdata = os.environ.get("APPDATA", "")
    local = os.environ.get("LOCALAPPDATA", "")
    patterns = [
        os.path.join(appdata, "Ditto", "Ditto.Settings"),
        os.path.join(local, "Ditto_ChocolateyApp", "Ditto.Settings"),
        os.path.join(local, "Packages", "*", "LocalCache", "Local", "Ditto_WindowsApp", "Ditto.Settings"),
    ]
    found = []
    for pattern in patterns:
        for path in sorted(glob.glob(pattern)):
            if path not in found:
                found.append(path)
    return found


def running_ditto_paths():
    """Find the executable paths of running Ditto.exe processes (to reach portable copies).

    Uses PowerShell's Get-Process because Python's standard library cannot list processes on Windows.

    Returns:
        A list of exe paths (empty when none runs or PowerShell is unavailable). Never raises.
    """
    if os.name != "nt":
        return []
    import subprocess
    try:
        out = subprocess.run(
            ["powershell", "-NoProfile", "-Command",
             "Get-Process -Name Ditto -ErrorAction SilentlyContinue | ForEach-Object { $_.Path }"],
            capture_output=True, text=True, timeout=30).stdout
    except (OSError, subprocess.SubprocessError):
        return []
    return [line.strip() for line in out.splitlines() if line.strip()]


def describe_file(path):
    """One line about a file: its size and last write time, or that it is missing.

    Args:
        path: The file to describe.

    Returns:
        A short human-readable line (no content).
    """
    try:
        st = os.stat(path)
    except OSError:
        return f"{path}  (missing)"
    stamp = time.strftime("%Y-%m-%d %H:%M", time.localtime(st.st_mtime))
    return f"{path}  ({st.st_size:,} bytes, written {stamp})"


def locate():
    """Print where Ditto keeps its settings and history on this PC, without opening any database.

    Returns:
        The database paths found (resolved, existing or not), for the caller to describe with --db.
    """
    dbs = []
    reg_path, reg_key = registry_db_path()
    print("Installed Ditto (registry HKCU\\Software\\Ditto):", "key present" if reg_key else "no key")
    if reg_path:
        resolved = os.path.expandvars(reg_path)
        print("  DBPath3 ->", describe_file(resolved))
        dbs.append(resolved)
    for ini in candidate_settings_files():
        print("Settings file:", describe_file(ini))
        value = read_ini_value(ini, "DBPath3")
        if value:
            resolved = os.path.expandvars(value)
            print("  DBPath3 ->", describe_file(resolved))
            dbs.append(resolved)
    for exe in running_ditto_paths():
        folder = os.path.dirname(exe)
        kinds = [m for m in ("portable", "chocolatey", "WindowsApp") if os.path.exists(os.path.join(folder, m))]
        print("Running:", exe, "marker files:", ", ".join(kinds) or "none (installed)")
        ini = os.path.join(folder, "Ditto.Settings")
        if os.path.exists(ini):
            value = read_ini_value(ini, "DBPath3")
            if value:
                resolved = os.path.expandvars(value)
                if not os.path.isabs(resolved):
                    resolved = os.path.join(folder, resolved)
                print("  DBPath3 ->", describe_file(resolved))
                dbs.append(resolved)
    default = os.path.join(os.environ.get("APPDATA", ""), "Ditto", "Ditto.db")
    if default not in dbs and os.path.exists(default):
        print("Default database:", describe_file(default))
        dbs.append(default)
    if not dbs:
        print("No Ditto database found for this user.")
    return dbs


def open_read_only(path):
    """Open a SQLite file read-only, so that a running Ditto is never disturbed or blocked.

    Args:
        path: Database file.

    Returns:
        A sqlite3 connection that returns raw bytes for text (Ditto's encoding is checked, never assumed).

    Raises:
        sqlite3.Error: The file is missing, locked beyond the timeout, or not a database.
    """
    uri = "file:" + path.replace("\\", "/") + "?mode=ro"
    con = sqlite3.connect(uri, uri=True, timeout=5)
    con.text_factory = bytes
    return con


def utc(seconds):
    """Format a Unix time in UTC, or a dash when it is unset.

    Args:
        seconds: Seconds since 1970 (Ditto stores lDate and lastPasteDate this way), or None/0.

    Returns:
        "yyyy-mm-dd hh:mm:ssZ" or "-".
    """
    if not seconds:
        return "-"
    return time.strftime("%Y-%m-%d %H:%M:%SZ", time.gmtime(seconds))


def histogram(values):
    """Summarize numbers as count / min / median / max.

    Args:
        values: A list of numbers.

    Returns:
        A short text, or "none" for an empty list.
    """
    if not values:
        return "none"
    ordered = sorted(values)
    return f"n={len(ordered)} min={ordered[0]:,} median={ordered[len(ordered) // 2]:,} max={ordered[-1]:,}"


def describe_db(path):
    """Print the structure of a Ditto database and statistics about its clips - never their content.

    Args:
        path: The Ditto.db to read.

    Returns:
        A dict of the key numbers (used by --self-test to check the description).

    Raises:
        sqlite3.Error: The file cannot be opened or is not a Ditto database.
    """
    con = open_read_only(path)
    cur = con.cursor()
    facts = {}
    pragma = {name: cur.execute(f"PRAGMA {name}").fetchone()[0]
              for name in ("encoding", "page_size", "auto_vacuum", "journal_mode", "user_version")}
    pragma = {k: (v.decode() if isinstance(v, bytes) else v) for k, v in pragma.items()}
    print("File:", describe_file(path))
    print("SQLite:", ", ".join(f"{k}={v}" for k, v in pragma.items()))
    facts["encoding"] = pragma["encoding"]
    objects = cur.execute("SELECT type, name, tbl_name FROM sqlite_master ORDER BY type, name").fetchall()
    for kind, plural in ((b"table", "tables"), (b"index", "indexes"), (b"trigger", "triggers")):
        names = [n.decode() for t, n, _ in objects if t == kind and not n.startswith(b"sqlite_")]
        print(f"{plural} ({len(names)}):", ", ".join(names))
    for table in ("Main", "Data", "Types", "CopyBuffers", "MainDeletes"):
        try:
            cols = [c[1].decode() for c in cur.execute(f"PRAGMA table_info({table})").fetchall()]
            rows = cur.execute(f"SELECT count(*) FROM {table}").fetchone()[0]
        except sqlite3.Error:
            print(f"  {table}: missing")
            continue
        print(f"  {table} ({rows:,} rows): {', '.join(cols)}")
        facts[f"rows.{table}"] = rows
    main_cols = {c[1].decode() for c in cur.execute("PRAGMA table_info(Main)").fetchall()}

    def count(where):
        """Count Main rows matching a fixed condition of this probe (never user input).

        Args:
            where: A SQL condition written in this file.

        Returns:
            The number of matching rows.
        """
        return cur.execute(f"SELECT count(*) FROM Main WHERE {where}").fetchone()[0]

    clips = count("bIsGroup = 0")
    groups = count("bIsGroup = 1")
    facts.update(clips=clips, groups=groups)
    print(f"Clips: {clips:,}   groups: {groups:,}")
    flags = {
        "in a group (lParentID > 0)": "bIsGroup = 0 AND lParentID > 0",
        "never auto delete (lDontAutoDelete > 0)": "bIsGroup = 0 AND lDontAutoDelete > 0",
        "with a hotkey (lShortCut <> 0)": "bIsGroup = 0 AND lShortCut <> 0",
        "with quick-paste text": "bIsGroup = 0 AND QuickPasteText IS NOT NULL AND length(QuickPasteText) > 0",
    }
    if "stickyClipOrder" in main_cols:
        flags["sticky (stickyClipOrder set)"] = f"bIsGroup = 0 AND stickyClipOrder <> {STICKY_NONE}"
    if "globalShortCut" in main_cols:
        flags["hotkey works globally"] = "bIsGroup = 0 AND globalShortCut > 0"
    for label, where in flags.items():
        n = count(where)
        facts[label] = n
        print(f"  {label}: {n:,}")
    if groups:
        nested = count("bIsGroup = 1 AND lParentID > 0")
        print(f"  groups inside groups: {nested:,}")
    span = cur.execute("SELECT min(lDate), max(lDate), min(lastPasteDate), max(lastPasteDate) FROM Main "
                       "WHERE bIsGroup = 0").fetchone()
    print(f"  lDate (created, Unix s): {utc(span[0])} .. {utc(span[1])};"
          f"  lastPasteDate: {utc(span[2])} .. {utc(span[3])}")
    order = cur.execute("SELECT min(clipOrder), max(clipOrder), count(DISTINCT clipOrder) FROM Main "
                        "WHERE bIsGroup = 0").fetchone()
    print(f"  clipOrder: {order[0]} .. {order[1]} ({order[2]:,} distinct)")
    dup = cur.execute("SELECT count(*) FROM (SELECT CRC FROM Main WHERE bIsGroup = 0 "
                      "GROUP BY CRC HAVING count(*) > 1)").fetchone()[0]
    print(f"  CRC values held by more than one clip: {dup:,}")
    desc_len = [r[0] for r in cur.execute("SELECT length(CAST(mText AS BLOB)) FROM Main WHERE bIsGroup = 0")]
    print("  mText (description) bytes:", histogram(desc_len))

    formats = collections.OrderedDict()
    for name, size in cur.execute("SELECT strClipBoardFormat, length(ooData) FROM Data"):
        key = name.decode("utf-8", "replace") if isinstance(name, bytes) else str(name)
        formats.setdefault(key, []).append(size or 0)
    print("Formats stored (Data.strClipBoardFormat):")
    for name, sizes in sorted(formats.items(), key=lambda kv: -len(kv[1])):
        print(f"  {name:<40} {len(sizes):>6,} rows   bytes {histogram(sizes)}")
    facts["formats"] = {k: len(v) for k, v in formats.items()}
    per_clip = [r[0] for r in cur.execute(
        "SELECT count(Data.lID) FROM Main LEFT JOIN Data ON Data.lParentID = Main.lID "
        "WHERE Main.bIsGroup = 0 GROUP BY Main.lID")]
    print("Formats per clip:", histogram(per_clip))

    checks = collections.Counter()
    for name, blob in cur.execute("SELECT strClipBoardFormat, ooData FROM Data"):
        name = name.decode("utf-8", "replace") if isinstance(name, bytes) else str(name)
        blob = bytes(blob or b"")
        if name == "CF_UNICODETEXT":
            checks["unicode: even length" if len(blob) % 2 == 0 else "unicode: odd length"] += 1
            checks["unicode: ends with NUL" if blob.endswith(b"\0\0") else "unicode: no final NUL"] += 1
        elif name == "CF_DIB" and len(blob) >= 40:
            size, width, height, planes, bits, compression = struct.unpack_from("<IiiHHI", blob, 0)
            checks[f"dib: header {size}, {bits} bpp, compression {compression}"] += 1
        elif name == "PNG":
            checks["png: signature ok" if blob.startswith(b"\x89PNG\r\n\x1a\n") else "png: bad signature"] += 1
        elif name == "HTML Format":
            checks["html: CF_HTML header" if blob.startswith(b"Version:") else "html: no header"] += 1
        elif name == "CF_HDROP" and len(blob) >= 20:
            offset, _, _, _, wide = struct.unpack_from("<IiiII", blob, 0)
            names = blob[offset:].decode("utf-16-le" if wide else "mbcs", "replace").split("\0")
            checks[f"hdrop: wide={wide}, {len([n for n in names if n])} file(s)"] += 1
    if checks:
        print("Payload checks:")
        for label, n in sorted(checks.items()):
            print(f"  {label}: {n:,}")
    facts["checks"] = dict(checks)
    con.close()
    return facts


def build_self_test_db(path):
    """Create a small database with Ditto's table layout and BC-TEST clips, as Ditto 3.25 would write them.

    The layout matches what Ditto's CreateDB issues (names and types only); rows imitate a text copy, an HTML
    copy, an image, a file list, a group with one member, a sticky clip and a "never auto delete" clip.

    Args:
        path: Where to create the database (must not exist).

    Returns:
        None.
    """
    con = sqlite3.connect(path)
    con.execute("PRAGMA encoding = 'UTF-16le'")
    con.execute("PRAGMA auto_vacuum = 1")
    con.execute("CREATE TABLE Main(lID INTEGER PRIMARY KEY AUTOINCREMENT, lDate INTEGER, mText TEXT, "
                "lShortCut INTEGER, lDontAutoDelete INTEGER, CRC INTEGER, bIsGroup INTEGER, lParentID INTEGER, "
                "QuickPasteText TEXT, clipOrder REAL, clipGroupOrder REAL, globalShortCut INTEGER, "
                "lastPasteDate INTEGER, stickyClipOrder REAL, stickyClipGroupOrder REAL, "
                "MoveToGroupShortCut INTEGER, GlobalMoveToGroupShortCut INTEGER)")
    con.execute("CREATE TABLE Data(lID INTEGER PRIMARY KEY AUTOINCREMENT, lParentID INTEGER, "
                "strClipBoardFormat TEXT, ooData BLOB)")
    con.execute("CREATE TABLE Types(lID INTEGER PRIMARY KEY AUTOINCREMENT, TypeText TEXT)")
    con.execute("CREATE TABLE CopyBuffers(lID INTEGER PRIMARY KEY AUTOINCREMENT, lClipID INTEGER, "
                "lCopyBuffer INTEGER)")
    con.execute("CREATE TABLE MainDeletes(clipID INTEGER, modifiedDate)")
    now = 1_790_000_000

    def clip(desc, order, crc, parent=-1, dont=0, sticky=STICKY_NONE, group=0):
        """Insert one Main row the way Ditto would for a new clip or group.

        Args:
            desc: The description (mText), BC-TEST text only.
            order: clipOrder / clipGroupOrder, also used to space the times.
            crc: The CRC value Ditto would compute (any number here).
            parent: lParentID: -1 for the top level, else a group's lID.
            dont: lDontAutoDelete: 0, or the Unix time "never auto delete" was set.
            sticky: stickyClipOrder; STICKY_NONE when not sticky.
            group: bIsGroup: 1 for a group row.

        Returns:
            The new row's lID.
        """
        cur = con.execute(
            "INSERT INTO Main (lDate, mText, lShortCut, lDontAutoDelete, CRC, bIsGroup, lParentID, QuickPasteText, "
            "clipOrder, clipGroupOrder, globalShortCut, lastPasteDate, stickyClipOrder, stickyClipGroupOrder, "
            "MoveToGroupShortCut, GlobalMoveToGroupShortCut) VALUES (?,?,0,?,?,?,?,'',?,?,0,?,?,?,0,0)",
            (now + order, desc, dont, crc, group, parent, order, order, now + order, sticky, STICKY_NONE))
        return cur.lastrowid

    def data(clip_id, name, blob):
        """Insert one Data row: a clip's format, by name, with its raw bytes.

        Args:
            clip_id: The Main row it belongs to.
            name: The format name as Ditto writes it (CF_UNICODETEXT, HTML Format, ...).
            blob: The payload bytes.

        Returns:
            None.
        """
        con.execute("INSERT INTO Data VALUES (NULL, ?, ?, ?)", (clip_id, name, blob))

    text = "BC-TEST plain text"
    c1 = clip(text, 1, 11)
    data(c1, "CF_UNICODETEXT", (text + "\0").encode("utf-16-le"))
    data(c1, "CF_TEXT", (text + "\0").encode("ascii"))
    c2 = clip("BC-TEST html", 2, 12, dont=now)
    data(c2, "CF_UNICODETEXT", "BC-TEST html\0".encode("utf-16-le"))
    data(c2, "HTML Format", b"Version:0.9\r\nStartHTML:0000000000\r\n<b>BC-TEST</b>\0")
    c3 = clip("CF_DIB", 3, 13, sticky=1)
    data(c3, "CF_DIB", struct.pack("<IiiHHIIiiII", 40, 2, 2, 1, 32, 0, 16, 0, 0, 0, 0) + b"\0" * 16)
    c4 = clip("Copied File - a.txt", 4, 14)
    names = "C:\\BC-TEST\\a.txt\0\0".encode("utf-16-le")
    data(c4, "CF_HDROP", struct.pack("<IiiII", 20, 0, 0, 0, 1) + names)
    g = clip("BC-TEST group", 5, 0, group=1)
    c5 = clip("BC-TEST in group", 6, 15, parent=g)
    data(c5, "CF_UNICODETEXT", "BC-TEST in group\0".encode("utf-16-le"))
    con.commit()
    con.close()


def self_test():
    """Build the BC-TEST database in a temporary folder, describe it, and check what the description found.

    Returns:
        0 when every check passed, 1 otherwise.
    """
    failures = 0
    with tempfile.TemporaryDirectory(prefix="bc-ditto-probe-") as folder:
        path = os.path.join(folder, "Ditto.db")
        build_self_test_db(path)
        facts = describe_db(path)
        expected = {
            "encoding": "UTF-16le",
            "clips": 5,
            "groups": 1,
            "in a group (lParentID > 0)": 1,
            "never auto delete (lDontAutoDelete > 0)": 1,
            "sticky (stickyClipOrder set)": 1,
            "rows.Data": 7,
        }
        for key, want in expected.items():
            got = facts.get(key)
            ok = got == want
            failures += 0 if ok else 1
            print(f"[{'ok' if ok else 'FAIL'}] {key}: {got!r} (expected {want!r})")
        for label in ("unicode: even length", "unicode: ends with NUL", "html: CF_HTML header",
                      "dib: header 40, 32 bpp, compression 0", "hdrop: wide=1, 1 file(s)"):
            ok = facts["checks"].get(label, 0) > 0
            failures += 0 if ok else 1
            print(f"[{'ok' if ok else 'FAIL'}] payload check '{label}'")
    print("Self-test:", "passed" if failures == 0 else f"{failures} failed")
    return 0 if failures == 0 else 1


def main():
    """Parse the command line and run the chosen mode.

    Returns:
        The process exit code: 0 on success, 1 on a failed self-test or an unreadable database.
    """
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--db", help="describe this Ditto.db (read-only)")
    parser.add_argument("--self-test", action="store_true", help="check the probe against a synthetic database")
    args = parser.parse_args()
    if args.self_test:
        return self_test()
    if args.db:
        try:
            describe_db(args.db)
        except sqlite3.Error as error:
            print("Not readable as a Ditto database:", error)
            return 1
        return 0
    locate()
    return 0


if __name__ == "__main__":
    sys.exit(main())
