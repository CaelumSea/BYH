#!/usr/bin/env python3
"""Capture BYH Settings window across all five navigation tabs.

Nav-button coordinates are relative to the window client area for the
default 1320x800 logical window at 175% DPI.
"""
import ctypes
import subprocess
import sys
import time
from ctypes import wintypes
from pathlib import Path

from PIL import ImageGrab

user32 = ctypes.windll.user32
gdi32 = ctypes.windll.gdi32

# Explicit Win32 signatures for 64-bit safety.
user32.SetWindowPos.argtypes = [wintypes.HWND, wintypes.HWND,
                                wintypes.INT, wintypes.INT,
                                wintypes.INT, wintypes.INT, wintypes.UINT]
user32.SetWindowPos.restype = wintypes.BOOL
user32.SetForegroundWindow.argtypes = [wintypes.HWND]
user32.SetForegroundWindow.restype = wintypes.BOOL
user32.ShowWindow.argtypes = [wintypes.HWND, wintypes.INT]
user32.ShowWindow.restype = wintypes.BOOL
user32.GetWindowRect.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.RECT)]
user32.GetWindowRect.restype = wintypes.BOOL
user32.SetCursorPos.argtypes = [wintypes.INT, wintypes.INT]
user32.SetCursorPos.restype = wintypes.BOOL
user32.mouse_event.argtypes = [wintypes.DWORD, wintypes.DWORD, wintypes.DWORD, wintypes.DWORD, wintypes.LONG]
user32.mouse_event.restype = None

HWND_TOPMOST = -1
HWND_NOTOPMOST = -2
SWP_NOMOVE = 0x0002
SWP_NOSIZE = 0x0001

# Make THIS process per-monitor DPI aware so coordinates are physical pixels.
try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)
except Exception:
    user32.SetProcessDPIAware()

# Navigation button centers relative to the window CLIENT area.
# Estimated for the default layout at 175% DPI.
NAV_BUTTONS = [
    ("general", 444, 300),
    ("provider", 444, 370),
    ("actions", 444, 440),
    ("vision", 444, 510),
    ("launcher", 444, 590),
]

# Title bar height in physical pixels.
TITLEBAR_H = 52


def find_settings_window(pid, timeout=25.0):
    deadline = time.time() + timeout
    EnumWindowsProc = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
    while time.time() < deadline:
        found = []

        def cb(hwnd, _):
            if not user32.IsWindowVisible(hwnd):
                return True
            wpid = wintypes.DWORD()
            user32.GetWindowThreadProcessId(hwnd, ctypes.byref(wpid))
            if wpid.value != pid:
                return True
            length = user32.GetWindowTextLengthW(hwnd)
            if length == 0:
                return True
            buf = ctypes.create_unicode_buffer(length + 1)
            user32.GetWindowTextW(hwnd, buf, length + 1)
            rect = wintypes.RECT()
            user32.GetWindowRect(hwnd, ctypes.byref(rect))
            w, h = rect.right - rect.left, rect.bottom - rect.top
            if w > 400 and h > 300:
                found.append((hwnd, buf.value, w, h))
            return True

        user32.EnumWindows(EnumWindowsProc(cb), 0)
        if found:
            return found[0]
        time.sleep(0.4)
    raise RuntimeError("settings window not found")


def capture(hwnd, path):
    user32.ShowWindow(hwnd, 9)  # SW_RESTORE
    fg = user32.SetForegroundWindow(hwnd)
    top = user32.SetWindowPos(hwnd, wintypes.HWND(HWND_TOPMOST), 0, 0, 0, 0,
                              SWP_NOMOVE | SWP_NOSIZE)
    print(f"foreground={fg} topmost={top}")
    time.sleep(1.0)
    rect = wintypes.RECT()
    user32.GetWindowRect(hwnd, ctypes.byref(rect))
    box = (rect.left, rect.top, rect.right, rect.bottom)
    # Keep the pointer off the navigation rail before the grab. Otherwise the
    # freshly clicked active tab is captured in its transient hover state,
    # which makes resting-state comparisons between tabs misleading.
    user32.SetCursorPos(rect.left + 260, rect.top + 24)
    time.sleep(0.2)
    img = ImageGrab.grab(bbox=box, all_screens=True)
    user32.SetWindowPos(hwnd, wintypes.HWND(HWND_NOTOPMOST), 0, 0, 0, 0,
                        SWP_NOMOVE | SWP_NOSIZE)
    img.save(path)
    print(f"saved {path} rect={box} size={img.size}")


def click_client(hwnd, rel_x, rel_y):
    rect = wintypes.RECT()
    user32.GetWindowRect(hwnd, ctypes.byref(rect))
    x = rect.left + rel_x
    y = rect.top + TITLEBAR_H + rel_y
    user32.SetCursorPos(x, y)
    time.sleep(0.05)
    user32.mouse_event(0x0002, 0, 0, 0, 0)
    time.sleep(0.05)
    user32.mouse_event(0x0004, 0, 0, 0, 0)
    time.sleep(0.05)


def main():
    exe = Path(sys.argv[1])
    out_dir = Path(sys.argv[2])
    out_dir.mkdir(parents=True, exist_ok=True)

    proc = subprocess.Popen([str(exe), "--open-settings"])
    print(f"Launched BYH.exe pid={proc.pid}")
    time.sleep(2.5)

    hwnd, title, w, h = find_settings_window(proc.pid)
    print(f"window hwnd={hwnd} title={title!r} {w}x{h}")

    try:
        for tab, rel_x, rel_y in NAV_BUTTONS:
            if tab != "general":
                click_client(hwnd, rel_x, rel_y)
                time.sleep(1.5)
            capture(hwnd, out_dir / f"v25-unified-tabs-{tab}-default-nativeaot.png")
    finally:
        subprocess.run(["taskkill", "/F", "/PID", str(proc.pid)], capture_output=True)
        print("terminated BYH.exe")


if __name__ == "__main__":
    main()
