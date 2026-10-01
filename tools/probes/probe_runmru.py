"""Probe: what Windows remembers of Win+R (the Run dialog), for a "Run" tab in BetterClipboard.

Prints STRUCTURE ONLY: counts, value names, suffixes, shapes ("absolute path", "uri-or-shell:"...), timestamps.
It never prints a command, path or URL the user typed (privacy rule, CLAUDE.md section 4).

Usage:
  python tools/probes/probe_runmru.py                 # snapshot: RunMRU, settings/policies, neighbour histories
  python tools/probes/probe_runmru.py --watch 120     # report each RunMRU change for 120 s (press Win+R meanwhile)
  python tools/probes/probe_runmru.py --self-test     # the watcher's change classification on a scratch key only

Result on Windows 11 26200 (2026-10-01): CLAUDE.md section 2.15.
"""

import ctypes
import ctypes.wintypes as wt
import datetime
import json
import os
import platform
import re
import sys
import time
import winreg

EXPLORER = r"Software\Microsoft\Windows\CurrentVersion\Explorer"
RUN_MRU = EXPLORER + r"\RunMRU"
SCRATCH = r"Software\BetterClipboard-ProbeTest\RunMRU"

advapi32 = ctypes.WinDLL("advapi32", use_last_error=True)
kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
advapi32.RegNotifyChangeKeyValue.argtypes = [wt.HKEY, wt.BOOL, wt.DWORD, wt.HANDLE, wt.BOOL]
advapi32.RegNotifyChangeKeyValue.restype = wt.LONG
kernel32.CreateEventW.argtypes = [wt.LPVOID, wt.BOOL, wt.BOOL, wt.LPCWSTR]
kernel32.CreateEventW.restype = wt.HANDLE
kernel32.WaitForSingleObject.argtypes = [wt.HANDLE, wt.DWORD]
kernel32.WaitForSingleObject.restype = wt.DWORD
kernel32.CloseHandle.argtypes = [wt.HANDLE]
REG_NOTIFY_CHANGE_LAST_SET = 0x4  # value added, changed or deleted
WAIT_OBJECT_0 = 0


def filetime_to_utc(filetime: int) -> datetime.datetime:
    """Converts a FILETIME (100 ns since 1601, as winreg.QueryInfoKey returns it) to an aware UTC datetime."""
    return datetime.datetime(1601, 1, 1, tzinfo=datetime.timezone.utc) + datetime.timedelta(microseconds=filetime // 10)


def read_mru(subkey: str) -> tuple[str, dict[str, str], datetime.datetime] | None:
    """Reads an MRU key: (MRUList, letter -> value, key last-write time); None when the key is absent."""
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, subkey) as key:
            _, value_count, last_write = winreg.QueryInfoKey(key)
            values: dict[str, str] = {}
            mru_list = ""
            for i in range(value_count):
                name, data, _ = winreg.EnumValue(key, i)
                if name == "MRUList":
                    mru_list = str(data)
                elif re.fullmatch(r"[a-z]", name):
                    values[name] = str(data)
            return mru_list, values, filetime_to_utc(last_write)
    except FileNotFoundError:
        return None


def strip_show_command(value: str) -> str:
    r"""Drops the trailing "\1": a backslash plus the show-command digit (SW_SHOWNORMAL = 1) the dialog appends."""
    return re.sub(r"\\\d$", "", value)


def shape(command: str) -> str:
    """Classifies a command without revealing it (used for counts only)."""
    if re.match(r"^[A-Za-z]:[\\/]|^\\\\", command):
        return "absolute path"
    if re.match(r"^[a-z][a-z0-9+.-]*:", command, re.IGNORECASE):
        return "uri-or-shell:"
    if "%" in command:
        return "env-var path"
    if re.search(r"\s", command.strip('"')):
        return "command with arguments"
    return "bare name"


def snapshot() -> None:
    """Prints the structure of the Run history and of what affects or neighbours it."""
    print(f"== OS: {platform.platform()} (build {sys.getwindowsversion().build})")
    mru = read_mru(RUN_MRU)
    print("== RunMRU (Win+R history)")
    if mru is None:
        print("key absent (Win+R never used, or history cleared)")
    else:
        mru_list, values, last_write = mru
        suffixed = sum(1 for v in values.values() if re.search(r"\\\d$", v))
        shapes: dict[str, int] = {}
        for v in values.values():
            shapes[shape(strip_show_command(v))] = shapes.get(shape(strip_show_command(v)), 0) + 1
        print(f"letter values: {len(values)} of 26; MRUList length {len(mru_list)}; "
              f"MRUList letters all present: {all(c in values for c in mru_list)}; "
              f"duplicates in MRUList: {len(set(mru_list)) != len(mru_list)}")
        print(f"full (26 = each new command evicts the oldest): {len(values) >= 26}")
        print(f"values ending with a show-command suffix (\\1): {suffixed} of {len(values)}")
        print("shapes: " + "; ".join(f"{n} {s}" for s, n in sorted(shapes.items(), key=lambda kv: -kv[1])))
        print(f"key last written: {last_write:%Y-%m-%d %H:%M:%S}Z (the only timestamp: entries have none)")

    print("== settings and policies")
    for subkey, names in ((EXPLORER + r"\Advanced", ("Start_TrackDocs", "Start_TrackProgs")),
                          (r"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer",
                           ("NoRun", "ClearRecentDocsOnExit", "NoRecentDocsHistory"))):
        for hive, hive_name in ((winreg.HKEY_CURRENT_USER, "HKCU"), (winreg.HKEY_LOCAL_MACHINE, "HKLM")):
            if hive_name == "HKLM" and "Policies" not in subkey:
                continue
            try:
                with winreg.OpenKey(hive, subkey) as key:
                    found = []
                    for name in names:
                        try:
                            found.append(f"{name}={winreg.QueryValueEx(key, name)[0]}")
                        except FileNotFoundError:
                            found.append(f"{name}=(absent)")
                    print(f"{hive_name}\\...\\{subkey.split(chr(92))[-1]}: " + " ".join(found))
            except FileNotFoundError:
                print(f"{hive_name}\\...\\{subkey.split(chr(92))[-1]}: key absent")

    print("== neighbour histories (counts only)")
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, EXPLORER + r"\TypedPaths") as key:
            _, count, last_write = winreg.QueryInfoKey(key)
            print(f"TypedPaths (File Explorer address bar): {count} values, last written {filetime_to_utc(last_write):%Y-%m-%d}")
    except FileNotFoundError:
        print("TypedPaths: absent")
    local = os.environ.get("LOCALAPPDATA", "")
    cmdpal_state = os.path.join(local, r"Packages\Microsoft.CommandPalette_8wekyb3d8bbwe\LocalState\state.json")
    if os.path.exists(cmdpal_state):
        with open(cmdpal_state, encoding="utf-8-sig") as f:
            history = json.load(f).get("RunHistory") or []
        print(f"Command Palette state.json RunHistory: {len(history)} entries (its own list, seeded once from RunMRU)")
    else:
        print("Command Palette state.json: absent (no run history of its own)")
    pt_run = os.path.join(local, r"Microsoft\PowerToys\PowerToys Run\Settings")
    for name, field in (("QueryHistory.json", "Items"), ("UserSelectedRecord.json", "Records")):
        path = os.path.join(pt_run, name)
        if os.path.exists(path):
            with open(path, encoding="utf-8-sig") as f:
                data = json.load(f).get(field) or []
            print(f"PowerToys Run {name}: {len(data)} entries")


def classify(before: tuple[str, dict[str, str]], after: tuple[str, dict[str, str]]) -> str:
    """Names a change between two MRU states without revealing any text."""
    (old_list, old_values), (new_list, new_values) = before, after
    if not new_list:
        return "history cleared"
    front = new_list[0]
    if front not in old_values:
        return f"new command in a free letter '{front}' (length {len(strip_show_command(new_values[front]))})"
    if old_values.get(front) != new_values.get(front):
        return f"new command overwrote letter '{front}' (the oldest entry was evicted)"
    if old_list != new_list:
        return f"existing command '{front}' moved to the front (a re-run; no duplicate)"
    return "MRU unchanged (some other value was written)"


def watch(subkey: str, seconds: float, on_armed=None) -> list[str]:
    """Reports each change of an MRU key for a while, using RegNotifyChangeKeyValue (no polling)."""
    events: list[str] = []
    state = read_mru(subkey)
    previous = (state[0], state[1]) if state else ("", {})
    event = kernel32.CreateEventW(None, False, False, None)
    deadline = time.monotonic() + seconds
    with winreg.OpenKey(winreg.HKEY_CURRENT_USER, subkey, 0, winreg.KEY_NOTIFY | winreg.KEY_READ) as key:
        while time.monotonic() < deadline:
            # Re-armed after every change: a notification fires once.
            status = advapi32.RegNotifyChangeKeyValue(key.handle, False, REG_NOTIFY_CHANGE_LAST_SET, event, True)
            if status != 0:
                raise OSError(status, "RegNotifyChangeKeyValue failed")
            if on_armed:
                on_armed()
                on_armed = None
            remaining = max(0, int((deadline - time.monotonic()) * 1000))
            if kernel32.WaitForSingleObject(event, remaining) != WAIT_OBJECT_0:
                break
            time.sleep(0.05)  # a writer sets MRUList and the value one after the other
            state = read_mru(subkey)
            current = (state[0], state[1]) if state else ("", {})
            message = classify(previous, current)
            events.append(message)
            print(f"{datetime.datetime.now(datetime.timezone.utc):%H:%M:%S.%f}Z  {message}")
            previous = current
    kernel32.CloseHandle(event)
    return events


def self_test() -> int:
    """Exercises watch/classify on a scratch key (never on the real RunMRU) and removes it again."""
    def write(values: dict[str, str], mru_list: str) -> None:
        with winreg.CreateKey(winreg.HKEY_CURRENT_USER, SCRATCH) as key:
            for name, data in values.items():
                winreg.SetValueEx(key, name, 0, winreg.REG_SZ, data)
            winreg.SetValueEx(key, "MRUList", 0, winreg.REG_SZ, mru_list)

    try:
        letters = "abcdefghijklmnopqrstuvwxyz"
        write({c: f"BC-TEST-{c}\\1" for c in letters[:25]}, letters[:25][::-1])
        steps = [
            lambda: write({"z": "BC-TEST-new\\1"}, "z" + letters[:25][::-1]),      # new command, free letter
            lambda: write({}, "a" + "z" + letters[1:25][::-1]),                     # re-run of an old one
            lambda: write({"b": "BC-TEST-evicting\\1"}, "b" + "az" + letters[2:25][::-1]),  # full: LRU letter reused
        ]
        expected = ["new command in a free letter", "moved to the front", "overwrote letter"]
        results = []
        for step, want in zip(steps, expected):
            got = watch(SCRATCH, 2.0, on_armed=step)
            ok = bool(got) and want in got[0]
            results.append(ok)
            print(f"{'ok  ' if ok else 'FAIL'} expected '{want}'")
        return 0 if all(results) else 1
    finally:
        winreg.DeleteKey(winreg.HKEY_CURRENT_USER, SCRATCH)
        winreg.DeleteKey(winreg.HKEY_CURRENT_USER, r"Software\BetterClipboard-ProbeTest")
        print("scratch key removed")


if __name__ == "__main__":
    if "--self-test" in sys.argv:
        sys.exit(self_test())
    snapshot()
    if "--watch" in sys.argv:
        seconds = float(sys.argv[sys.argv.index("--watch") + 1])
        print(f"== watching RunMRU for {seconds:.0f} s (run something with Win+R)")
        watch(RUN_MRU, seconds)
