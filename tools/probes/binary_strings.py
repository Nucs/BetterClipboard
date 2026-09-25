"""Probe: dump ASCII and UTF-16LE strings from a Windows binary, optionally filtered by a regex.

This is how CLAUDE.md sections 1.3-1.5 were derived (embedded source paths, telemetry event names,
registry value names, WinRT class names). It only reads the file; it does not disassemble anything.

Usage:
  python tools/probes/binary_strings.py C:\\Windows\\System32\\cbdhsvc.dll "Clipboard|History|\\.cpp"
  python tools/probes/binary_strings.py C:\\Windows\\explorer.exe "ClipboardHistory|ShellHotKey"
"""
import re
import sys


def strings(data, minimum=5):
    """Yields (encoding, text) for printable ASCII and UTF-16LE runs of at least `minimum` chars."""
    for match in re.finditer(rb"[\x20-\x7e]{%d,}" % minimum, data):
        yield "ascii", match.group().decode("ascii")
    for match in re.finditer(rb"(?:[\x20-\x7e]\x00){%d,}" % minimum, data):
        yield "utf16", match.group().decode("utf-16le")


def main():
    path = sys.argv[1]
    pattern = re.compile(sys.argv[2]) if len(sys.argv) > 2 else None
    with open(path, "rb") as handle:
        data = handle.read()
    seen = set()
    for encoding, text in strings(data):
        if (pattern is None or pattern.search(text)) and (encoding, text) not in seen:
            seen.add((encoding, text))
            print(f"{encoding}: {text}")


if __name__ == "__main__":
    main()
