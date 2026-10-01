"""Run a COPY of the current dev build next to the user's installed BetterClipboard, for the user to try.

The rules this script encodes (CLAUDE.md section 3, "Side by side for the user"):

* A copy, never the build output itself. A running exe locks its files, so the next solution build (ours or
  another agent's working in the same tree) fails with "being used by another process", and stopping a process
  to get a build through is not allowed. The copy lives in <root>/app; a re-run sends the copy --exit first,
  because the copy is locked just the same while it runs.
* Its own data dir (<root>/data, BETTERCLIPBOARD_DATA_DIR) makes it a scoped instance: its own store, mutex,
  --exit/--show-flyout events and pipe, so it never signals, replaces or shares a store with the installed app.
  A new data dir is seeded with OpenHotkey Alt+Win+V (free on this PC, CLAUDE.md section 1.6) and the keyboard
  hook fallback off, so Win+V stays with the installed app.
* Real data on purpose: the copy imports Windows' clipboard history and the Win+R history, watches the real
  ShareX folders and records live copies, because the user wants to try features on their own data. All of it
  stays in <root>/data until --clean deletes it.
* The user's environment, not this shell's: the copy gets the environment Explorer would give a new process
  (CreateEnvironmentBlock with bInherit = FALSE, read from the registry) plus BETTERCLIPBOARD_DATA_DIR, and
  inherits no handle. This shell's CLAUDECODE, MSYSTEM and invalid GH_TOKEN would otherwise reach every command
  the dev app starts (the Run tab's Ctrl+Enter), and an inherited stdout pipe would keep a caller waiting.
* The panel (--show-flyout) opens only when the foreground app is a terminal, IDE, browser or Explorer. The user
  games on this PC; a panel popping up over a full-screen game steals its focus.

Usage:
  python tools/launch_dev.py                 # stop the previous copy, copy the build, start it, open its panel
  python tools/launch_dev.py --no-show       # the same, without opening the panel
  python tools/launch_dev.py --exit          # stop the copy (a scoped --exit: the installed app is never touched)
  python tools/launch_dev.py --exit --clean  # stop it, then delete <root>: the copy and its history
Options:
  --root DIR        default %TEMP%\\BetterClipboard-dev
  --hotkey GESTURE  default Alt+Win+V; written only into a NEW data dir's settings.json

Output never contains clipboard content: only log lines (the app logs no content), process ids and paths with
the profile folder replaced by %USERPROFILE%.
"""

import argparse
import ctypes
import ctypes.wintypes as wt
import datetime
import glob
import json
import os
import shutil
import sys
import time

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
BUILD = os.path.join(REPO, "src", "BetterClipboard.App", "bin", "x64", "Debug", "net10.0-windows10.0.26100.0", "win-x64")
EXE_NAME = "BetterClipboard.exe"

# Foreground apps the panel may open over: the user is at a desk tool, not in a game or a full-screen video.
SAFE_FOREGROUND = {
    "windowsterminal.exe", "openconsole.exe", "conhost.exe", "mintty.exe", "wezterm-gui.exe", "alacritty.exe",
    "cmd.exe", "pwsh.exe", "powershell.exe", "code.exe", "cursor.exe", "devenv.exe", "rider64.exe", "idea64.exe",
    "claude.exe", "chrome.exe", "msedge.exe", "firefox.exe", "explorer.exe", "betterclipboard.exe",
}

CREATE_UNICODE_ENVIRONMENT = 0x00000400
CREATE_BREAKAWAY_FROM_JOB = 0x01000000
PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
TOKEN_DUPLICATE = 0x0002
TOKEN_QUERY = 0x0008

kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
advapi32 = ctypes.WinDLL("advapi32", use_last_error=True)
userenv = ctypes.WinDLL("userenv", use_last_error=True)
user32 = ctypes.WinDLL("user32", use_last_error=True)


class STARTUPINFOW(ctypes.Structure):
    """Win32 STARTUPINFOW; all zero except cb, so the child gets default window and std-handle behavior."""

    _fields_ = [("cb", wt.DWORD), ("lpReserved", wt.LPWSTR), ("lpDesktop", wt.LPWSTR), ("lpTitle", wt.LPWSTR),
                ("dwX", wt.DWORD), ("dwY", wt.DWORD), ("dwXSize", wt.DWORD), ("dwYSize", wt.DWORD),
                ("dwXCountChars", wt.DWORD), ("dwYCountChars", wt.DWORD), ("dwFillAttribute", wt.DWORD),
                ("dwFlags", wt.DWORD), ("wShowWindow", wt.WORD), ("cbReserved2", wt.WORD),
                ("lpReserved2", ctypes.c_void_p), ("hStdInput", wt.HANDLE), ("hStdOutput", wt.HANDLE),
                ("hStdError", wt.HANDLE)]


class PROCESS_INFORMATION(ctypes.Structure):
    """Win32 PROCESS_INFORMATION; both handles must be closed by the caller."""

    _fields_ = [("hProcess", wt.HANDLE), ("hThread", wt.HANDLE), ("dwProcessId", wt.DWORD), ("dwThreadId", wt.DWORD)]


# 64-bit handles and pointers need explicit signatures; ctypes would otherwise truncate them to int.
kernel32.GetCurrentProcess.restype = wt.HANDLE
kernel32.CloseHandle.argtypes = [wt.HANDLE]
kernel32.WaitForSingleObject.argtypes = [wt.HANDLE, wt.DWORD]
kernel32.GetExitCodeProcess.argtypes = [wt.HANDLE, ctypes.POINTER(wt.DWORD)]
kernel32.OpenProcess.argtypes = [wt.DWORD, wt.BOOL, wt.DWORD]
kernel32.OpenProcess.restype = wt.HANDLE
kernel32.QueryFullProcessImageNameW.argtypes = [wt.HANDLE, wt.DWORD, wt.LPWSTR, ctypes.POINTER(wt.DWORD)]
kernel32.K32EnumProcesses.argtypes = [ctypes.POINTER(wt.DWORD), wt.DWORD, ctypes.POINTER(wt.DWORD)]
kernel32.CreateProcessW.argtypes = [wt.LPCWSTR, wt.LPWSTR, ctypes.c_void_p, ctypes.c_void_p, wt.BOOL, wt.DWORD,
                                    ctypes.c_void_p, wt.LPCWSTR, ctypes.POINTER(STARTUPINFOW),
                                    ctypes.POINTER(PROCESS_INFORMATION)]
advapi32.OpenProcessToken.argtypes = [wt.HANDLE, wt.DWORD, ctypes.POINTER(wt.HANDLE)]
userenv.CreateEnvironmentBlock.argtypes = [ctypes.POINTER(ctypes.c_void_p), wt.HANDLE, wt.BOOL]
userenv.DestroyEnvironmentBlock.argtypes = [ctypes.c_void_p]
user32.GetForegroundWindow.restype = wt.HWND
user32.GetWindowThreadProcessId.argtypes = [wt.HWND, ctypes.POINTER(wt.DWORD)]


def redact(text: str) -> str:
    """Replaces the profile folder with %USERPROFILE%, so printed paths and log lines carry no user name.

    Args:
        text: A path or a log line.

    Returns:
        The text with every occurrence of the profile folder (case-insensitive prefix match per occurrence)
        replaced.
    """
    profile = os.environ.get("USERPROFILE", "")
    if not profile:
        return text
    lowered, needle = text.lower(), profile.lower()
    out, start = [], 0
    while (i := lowered.find(needle, start)) >= 0:
        out.append(text[start:i])
        out.append("%USERPROFILE%")
        start = i + len(profile)
    out.append(text[start:])
    return "".join(out)


def user_environment() -> dict[str, str]:
    """The environment Explorer would give a new process of this user, read fresh from the registry.

    bInherit = FALSE is the point: nothing of this shell's environment (CLAUDECODE, MSYSTEM, tokens) leaks in.

    Returns:
        Variable name to value; "=C:"-style per-drive entries are skipped.

    Raises:
        OSError: The process token could not be opened or the block could not be built.
    """
    token = wt.HANDLE()
    if not advapi32.OpenProcessToken(kernel32.GetCurrentProcess(), TOKEN_QUERY | TOKEN_DUPLICATE, ctypes.byref(token)):
        raise ctypes.WinError(ctypes.get_last_error())
    block = ctypes.c_void_p()
    try:
        if not userenv.CreateEnvironmentBlock(ctypes.byref(block), token, False):
            raise ctypes.WinError(ctypes.get_last_error())
        env: dict[str, str] = {}
        pointer = block.value
        while entry := ctypes.wstring_at(pointer):
            # Skip the "=" at index 0 of hidden per-drive entries ("=C:=C:\...") when looking for the separator.
            separator = entry.find("=", 1)
            if separator > 0:
                env[entry[:separator]] = entry[separator + 1:]
            pointer += (len(entry) + 1) * ctypes.sizeof(ctypes.c_wchar)
        return env
    finally:
        if block.value:
            userenv.DestroyEnvironmentBlock(block)
        kernel32.CloseHandle(token)


def start(exe: str, data_dir: str, arguments: str, wait_ms: int = 0) -> int:
    """Starts the copied app with the user's environment plus BETTERCLIPBOARD_DATA_DIR and no inherited handles.

    Args:
        exe: The copied BetterClipboard.exe; its folder becomes the working directory.
        data_dir: The copy's data dir; it scopes the instance (lock, events, pipe) away from the installed app.
        arguments: The app's command line after the exe, e.g. "--background", "--show-flyout", "--exit".
        wait_ms: 0 to return at once; otherwise how long to wait for the process to end.

    Returns:
        The new process id when wait_ms is 0, else its exit code (259 = still running after wait_ms).

    Raises:
        OSError: CreateProcessW failed, or the environment could not be built.
    """
    env = user_environment()
    env["BETTERCLIPBOARD_DATA_DIR"] = data_dir
    # CreateProcess wants the block sorted by name, case-insensitively, and ending with an empty string.
    block = "\0".join(f"{k}={v}" for k, v in sorted(env.items(), key=lambda kv: kv[0].upper())) + "\0\0"
    env_buffer = ctypes.create_unicode_buffer(block, len(block) + 1)
    command = ctypes.create_unicode_buffer(f'"{exe}" {arguments}')
    si = STARTUPINFOW()
    si.cb = ctypes.sizeof(STARTUPINFOW)
    pi = PROCESS_INFORMATION()
    # Leave a job this shell may run in, so the copy outlives the command that started it; a job that forbids
    # breaking away fails the first call, and the plain retry is what earlier sessions' runs relied on anyway.
    for flags in (CREATE_UNICODE_ENVIRONMENT | CREATE_BREAKAWAY_FROM_JOB, CREATE_UNICODE_ENVIRONMENT):
        if kernel32.CreateProcessW(exe, command, None, None, False, flags, ctypes.cast(env_buffer, ctypes.c_void_p),
                                   os.path.dirname(exe), ctypes.byref(si), ctypes.byref(pi)):
            break
    else:
        raise ctypes.WinError(ctypes.get_last_error())
    kernel32.CloseHandle(pi.hThread)
    try:
        if not wait_ms:
            return pi.dwProcessId
        kernel32.WaitForSingleObject(pi.hProcess, wait_ms)
        code = wt.DWORD()
        kernel32.GetExitCodeProcess(pi.hProcess, ctypes.byref(code))
        return code.value
    finally:
        kernel32.CloseHandle(pi.hProcess)


def image_path(pid: int) -> str:
    """The full exe path of a process, or "" when it cannot be queried (exited, protected, other session)."""
    handle = kernel32.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, False, pid)
    if not handle:
        return ""
    try:
        size = wt.DWORD(32768)
        name = ctypes.create_unicode_buffer(size.value)
        return name.value if kernel32.QueryFullProcessImageNameW(handle, 0, name, ctypes.byref(size)) else ""
    finally:
        kernel32.CloseHandle(handle)


def app_processes() -> list[tuple[int, str]]:
    """Every running BetterClipboard.exe this user can query: the installed app, copies and dev builds.

    Returns:
        (pid, exe path) pairs, sorted by pid.
    """
    capacity = 4096
    while True:
        pids = (wt.DWORD * capacity)()
        used = wt.DWORD()
        if not kernel32.K32EnumProcesses(pids, ctypes.sizeof(pids), ctypes.byref(used)):
            raise ctypes.WinError(ctypes.get_last_error())
        count = used.value // ctypes.sizeof(wt.DWORD)
        if count < capacity:
            break
        capacity *= 2  # a full buffer may have been truncated
    found = []
    for pid in pids[:count]:
        path = image_path(pid) if pid else ""
        if os.path.basename(path).lower() == EXE_NAME.lower():
            found.append((pid, path))
    return sorted(found)


def running_from(folder: str) -> list[int]:
    """Process ids of BetterClipboard.exe instances started from inside a folder (the copy, not the others)."""
    prefix = os.path.normcase(os.path.abspath(folder)) + os.sep
    return [pid for pid, path in app_processes() if os.path.normcase(path).startswith(prefix)]


def foreground_app() -> str:
    """The exe name of the foreground window's process, or "?" when it cannot be queried (e.g. elevated)."""
    pid = wt.DWORD()
    user32.GetWindowThreadProcessId(user32.GetForegroundWindow(), ctypes.byref(pid))
    return os.path.basename(image_path(pid.value)) or "?"


def log_lines(data_dir: str, words: tuple[str, ...]) -> list[str]:
    """Lines of the copy's own logs that contain any of the words, oldest first.

    The app logs no clipboard content (CLAUDE.md section 2.8), so printing these lines is safe.

    Args:
        data_dir: The copy's data dir; logs live in its logs subfolder.
        words: Case-sensitive substrings to keep.

    Returns:
        The matching lines without line endings.
    """
    lines = []
    for path in sorted(glob.glob(os.path.join(data_dir, "logs", "*.log"))):
        with open(path, encoding="utf-8", errors="replace") as log:
            lines += [line.rstrip() for line in log if any(w in line for w in words)]
    return lines


def stop(app_dir: str, data_dir: str) -> bool:
    """Sends the copy a scoped --exit and waits until nothing runs from its folder.

    Never kills: a copy that does not quit within 30 s is reported and left alone.

    Args:
        app_dir: The copy's folder.
        data_dir: The copy's data dir; --exit with it set reaches only this instance.

    Returns:
        True when nothing runs from app_dir any more.

    Raises:
        OSError: Starting the --exit process failed.
    """
    exe = os.path.join(app_dir, EXE_NAME)
    if not running_from(app_dir):
        return True
    if not os.path.exists(exe):
        return False
    start(exe, data_dir, "--exit", wait_ms=20_000)
    deadline = time.time() + 30
    while running_from(app_dir) and time.time() < deadline:
        time.sleep(0.25)
    return not running_from(app_dir)


def remove_tree(folder: str) -> None:
    """Deletes a folder, retrying while files of a just-exited process are still being released.

    Raises:
        OSError: The folder is still in use after 10 s.
    """
    deadline = time.time() + 10
    while os.path.exists(folder):
        try:
            shutil.rmtree(folder)
        except OSError:
            if time.time() > deadline:
                raise
            time.sleep(0.25)


def newest_source_time() -> float:
    """Last-write time of the newest C# or XAML file under src/ (0 when none), to flag a stale build."""
    newest = 0.0
    for top, dirs, files in os.walk(os.path.join(REPO, "src")):
        dirs[:] = [d for d in dirs if d not in ("bin", "obj")]
        for name in files:
            if name.endswith((".cs", ".xaml")):
                newest = max(newest, os.path.getmtime(os.path.join(top, name)))
    return newest


def main(argv: list[str]) -> int:
    """Runs the launcher.

    Args:
        argv: The command-line arguments after the script name.

    Returns:
        The exit code: 0 on success, 1 when the build is missing or the previous copy would not stop.
    """
    parser = argparse.ArgumentParser(description="Run a copy of the dev build next to the installed app.")
    parser.add_argument("--root", default=os.path.join(os.environ.get("TEMP", "."), "BetterClipboard-dev"))
    parser.add_argument("--hotkey", default="Alt+Win+V")
    parser.add_argument("--exit", action="store_true", help="stop the copy")
    parser.add_argument("--clean", action="store_true", help="with --exit: delete the copy and its history")
    parser.add_argument("--no-show", action="store_true", help="do not open the panel")
    args = parser.parse_args(argv)
    app_dir, data_dir = os.path.join(args.root, "app"), os.path.join(args.root, "data")
    exe = os.path.join(app_dir, EXE_NAME)
    app_prefix = os.path.normcase(os.path.abspath(app_dir)) + os.sep
    # The installed app and any other dev instance: none of them may disappear because of this script.
    others = [(p, path) for p, path in app_processes() if not os.path.normcase(path).startswith(app_prefix)]

    if args.exit:
        stopped = stop(app_dir, data_dir)
        print(f"copy stopped: {stopped}")
        if stopped and args.clean and os.path.isdir(args.root):
            # Only ever delete a folder this script laid out: a mistyped --root must not wipe anything else.
            unexpected = set(os.listdir(args.root)) - {"app", "data"}
            if unexpected:
                print(f"not deleting {redact(args.root)}: it holds more than app and data")
                return 1
            remove_tree(args.root)
            print(f"deleted {redact(args.root)} (the copy and its history)")
        return 0 if stopped else 1

    source_exe = os.path.join(BUILD, EXE_NAME)
    if not os.path.exists(source_exe):
        print(f"no solution build at {redact(BUILD)}: run dotnet build BetterClipboard.sln first")
        return 1
    built = os.path.getmtime(source_exe)
    stamp = lambda t: datetime.datetime.fromtimestamp(t).strftime("%Y-%m-%d %H:%M")
    print(f"build: {stamp(built)}" + (f" (OLDER than the newest source, {stamp(newest_source_time())}:"
                                       " rebuild through the solution to include it)" if newest_source_time() > built else ""))

    # The previous copy is locked while it runs, so it has to quit before its folder can be replaced.
    if not stop(app_dir, data_dir):
        print("the previous copy did not quit within 30 s; left it running and changed nothing")
        return 1
    if os.path.exists(app_dir):
        remove_tree(app_dir)
    began = time.time()
    shutil.copytree(BUILD, app_dir)
    print(f"copied in {time.time() - began:.1f} s to {redact(app_dir)}")

    os.makedirs(data_dir, exist_ok=True)
    settings = os.path.join(data_dir, "settings.json")
    if not os.path.exists(settings):
        with open(settings, "w", encoding="utf-8") as file:
            json.dump({"OpenHotkey": args.hotkey, "UseKeyboardHookFallback": False}, file)

    # Ready once THIS start logged its shortcut outcome (HotkeyService.Apply logs exactly one of these); a
    # re-used data dir already holds the lines of earlier starts, so count instead of looking for presence.
    shortcut = ("registered with RegisterHotKey", "intercepting it with a keyboard hook", "Could not register")
    seen = len(log_lines(data_dir, shortcut))
    warnings_before = len(log_lines(data_dir, ("[WRN]", "[ERR]")))
    pid = start(exe, data_dir, "--background")
    print(f"started the copy: pid {pid}")
    deadline = time.time() + 30
    while time.time() < deadline and len(log_lines(data_dir, shortcut)) <= seen:
        time.sleep(0.25)
    time.sleep(1.5)  # let the first imports log too
    interesting = ("starting", *shortcut, "Win+R", "ShareX found", "Imported", "Everything", "[WRN]", "[ERR]")
    for line in log_lines(data_dir, interesting)[-12:]:
        print("  " + redact(line)[:240])
    print(f"new warnings/errors in its log: {len(log_lines(data_dir, ('[WRN]', '[ERR]'))) - warnings_before}")

    after = app_processes()
    still = sum(1 for p, _ in others if any(p == q for q, _ in after))
    print("BetterClipboard.exe now: " + "; ".join(f"{p} {redact(path)}" for p, path in after))
    print(f"other instances still running: {still} of {len(others)}")

    foreground = foreground_app()
    if args.no_show:
        print(f"panel not opened (--no-show); shortcut {args.hotkey}")
    elif foreground.lower() in SAFE_FOREGROUND:
        print(f"foreground {foreground}: opening the panel (exit code {start(exe, data_dir, '--show-flyout', wait_ms=15_000)})")
    else:
        print(f"foreground {foreground}: not opening the panel over it; the shortcut is {args.hotkey}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
