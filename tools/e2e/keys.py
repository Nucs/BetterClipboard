"""Inject key chords like a remapper would (no BetterClipboard marker in dwExtraInfo).

!!! WARNING: this sends REAL keystrokes to whatever window is in the foreground. If the user touches the
!!! machine mid-run (clicks another app), the keys — and the Ctrl+V a paste triggers — land THERE.
!!! Only run with the user's explicit OK, and always `activate.py BC-PasteTarget` right before.

Usage: python tools/e2e/keys.py win+v "type=some text" sleep=0.5 down enter | shift+enter | esc | ctrl+p
       (several args = sequence; type= sends Unicode text, sleep= pauses in seconds)
"""
import ctypes
import sys
import time

user32 = ctypes.windll.user32
KEYUP = 0x2
VK = {"win": 0x5B, "ctrl": 0x11, "shift": 0x10, "alt": 0x12, "v": 0x56, "p": 0x50, "down": 0x28, "up": 0x26,
      "enter": 0x0D, "esc": 0x1B, "delete": 0x2E, "a": 0x41, "c": 0x43, "1": 0x31, "2": 0x32, "tab": 0x09}


def chord(spec):
    keys = [VK[k] for k in spec.lower().split("+")]
    for k in keys:
        user32.keybd_event(k, 0, 0, 0)
        time.sleep(0.02)
    for k in reversed(keys):
        user32.keybd_event(k, 0, KEYUP, 0)
        time.sleep(0.02)


class KEYBDINPUT(ctypes.Structure):
    _fields_ = [("wVk", ctypes.c_ushort), ("wScan", ctypes.c_ushort), ("dwFlags", ctypes.c_uint),
                ("time", ctypes.c_uint), ("dwExtraInfo", ctypes.c_void_p)]


class INPUT(ctypes.Structure):
    class _U(ctypes.Union):
        _fields_ = [("ki", KEYBDINPUT), ("pad", ctypes.c_byte * 32)]
    _fields_ = [("type", ctypes.c_uint), ("u", _U)]


def type_text(text):
    """Types arbitrary text with KEYEVENTF_UNICODE (4) key events."""
    for ch in text:
        for flags in (4, 4 | KEYUP):
            inp = INPUT(type=1)
            inp.u.ki = KEYBDINPUT(0, ord(ch), flags, 0, None)
            user32.SendInput(1, ctypes.byref(inp), ctypes.sizeof(INPUT))
        time.sleep(0.01)


for spec in sys.argv[1:]:
    if spec.startswith("sleep="):
        time.sleep(float(spec.split("=")[1]))
    elif spec.startswith("type="):
        type_text(spec.split("=", 1)[1])
        time.sleep(0.15)
    else:
        chord(spec)
        time.sleep(0.15)
