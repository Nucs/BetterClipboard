"""Screenshot helper for UI verification.

Usage:
  python tools/e2e/shot.py <out.png> title=<substring>     capture the first visible top-level window whose title contains substring
  python tools/e2e/shot.py <out.png> fg                    capture the foreground window
  python tools/e2e/shot.py <out.png> list                  list visible top-level windows (hwnd, pid, title, rect)
Coordinates are physical pixels (process is made per-monitor DPI aware), bounds come from DWM's
extended frame bounds so the drop shadow is excluded.
"""
import ctypes
import sys
from ctypes import wintypes

from PIL import ImageGrab

user32 = ctypes.windll.user32
dwmapi = ctypes.windll.dwmapi
user32.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))

EnumWindowsProc = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)


def title_of(hwnd):
    n = user32.GetWindowTextLengthW(hwnd)
    buf = ctypes.create_unicode_buffer(n + 1)
    user32.GetWindowTextW(hwnd, buf, n + 1)
    return buf.value


def bounds(hwnd):
    rect = wintypes.RECT()
    if dwmapi.DwmGetWindowAttribute(hwnd, 9, ctypes.byref(rect), ctypes.sizeof(rect)) != 0:
        user32.GetWindowRect(hwnd, ctypes.byref(rect))
    return rect.left, rect.top, rect.right, rect.bottom


def visible_windows():
    found = []

    def cb(hwnd, _):
        if user32.IsWindowVisible(hwnd) and title_of(hwnd):
            pid = wintypes.DWORD()
            user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
            found.append((hwnd, pid.value, title_of(hwnd), bounds(hwnd)))
        return True

    user32.EnumWindows(EnumWindowsProc(cb), 0)
    return found


def main():
    out, mode = sys.argv[1], sys.argv[2]
    if mode == "list":
        for w in visible_windows():
            print(w)
        return
    if mode == "fg":
        hwnd = user32.GetForegroundWindow()
    else:
        needle = mode.split("=", 1)[1]
        matches = [w for w in visible_windows() if needle in w[2]]
        if not matches:
            print("no window matching", needle)
            sys.exit(2)
        hwnd = matches[0][0]
    box = bounds(hwnd)
    print("hwnd", hwnd, "title", repr(title_of(hwnd)), "bounds", box)
    ImageGrab.grab(bbox=box, all_screens=True).save(out)
    print("saved", out)


if __name__ == "__main__":
    main()
