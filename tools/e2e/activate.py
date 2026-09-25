"""Bring a window (by title substring) to the foreground despite the foreground lock.

Usage: python tools/e2e/activate.py <title-substring>
Exit code 0 when the window is foreground afterwards, 1 otherwise.
"""
import ctypes
import sys
import time
from ctypes import wintypes

user32 = ctypes.windll.user32
kernel32 = ctypes.windll.kernel32
EnumWindowsProc = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)


def find(needle):
    hits = []

    def cb(hwnd, _):
        n = user32.GetWindowTextLengthW(hwnd)
        buf = ctypes.create_unicode_buffer(n + 1)
        user32.GetWindowTextW(hwnd, buf, n + 1)
        if user32.IsWindowVisible(hwnd) and needle in buf.value:
            hits.append(hwnd)
        return True

    user32.EnumWindows(EnumWindowsProc(cb), 0)
    return hits[0] if hits else None


class MOUSEINPUT(ctypes.Structure):
    _fields_ = [("dx", ctypes.c_long), ("dy", ctypes.c_long), ("mouseData", ctypes.c_uint), ("dwFlags", ctypes.c_uint),
                ("time", ctypes.c_uint), ("dwExtraInfo", ctypes.c_void_p)]


class INPUT(ctypes.Structure):
    class _U(ctypes.Union):
        _fields_ = [("mi", MOUSEINPUT), ("pad", ctypes.c_byte * 32)]
    _fields_ = [("type", ctypes.c_uint), ("u", _U)]


def activate(hwnd):
    if user32.GetForegroundWindow() == hwnd:
        return True
    inp = INPUT(type=0)
    user32.SendInput(1, ctypes.byref(inp), ctypes.sizeof(INPUT))  # "we received the last input event"
    user32.SetForegroundWindow(hwnd)
    if user32.GetForegroundWindow() == hwnd:
        return True
    fg = user32.GetForegroundWindow()
    fg_thread = user32.GetWindowThreadProcessId(fg, None)
    me = kernel32.GetCurrentThreadId()
    user32.AttachThreadInput(me, fg_thread, True)
    user32.BringWindowToTop(hwnd)
    user32.SetForegroundWindow(hwnd)
    user32.AttachThreadInput(me, fg_thread, False)
    return user32.GetForegroundWindow() == hwnd


needle = sys.argv[1]
for _ in range(30):
    hwnd = find(needle)
    if hwnd:
        break
    time.sleep(0.2)
else:
    print("window not found:", needle)
    sys.exit(1)
ok = activate(hwnd)
time.sleep(0.3)
print("foreground:", ok)
sys.exit(0 if ok else 1)
