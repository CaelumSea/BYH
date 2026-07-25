# QA capture: BYH Settings window at default and minimum logical sizes.
# Launches the NativeAOT BYH.exe with --open-settings, captures the window
# from the screen in physical pixels, then resizes to the 1240x680 logical
# minimum (scaled by the system DPI) and captures again.
import ctypes
import subprocess
import sys
import time
from ctypes import wintypes

from PIL import ImageGrab

EXE = sys.argv[1]
OUT_DEFAULT = sys.argv[2]
OUT_MINIMUM = sys.argv[3]

user32 = ctypes.windll.user32
gdi32 = ctypes.windll.gdi32

# Explicit signatures: passing raw Python ints for HWND on 64-bit truncates
# HWND_TOPMOST (-1) to 0xFFFFFFFF, which silently breaks SetWindowPos.
user32.SetWindowPos.argtypes = [wintypes.HWND, wintypes.HWND,
                                wintypes.INT, wintypes.INT,
                                wintypes.INT, wintypes.INT, wintypes.UINT]
user32.SetWindowPos.restype = wintypes.BOOL
user32.SetForegroundWindow.argtypes = [wintypes.HWND]
user32.SetForegroundWindow.restype = wintypes.BOOL

# Make THIS process per-monitor DPI aware so coordinates are physical pixels.
try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)
except Exception:
    user32.SetProcessDPIAware()

dc = user32.GetDC(None)
dpi = gdi32.GetDeviceCaps(dc, 88)  # LOGPIXELSX
user32.ReleaseDC(None, dc)
scale = dpi / 96.0
print(f"System DPI={dpi} scale={scale:.3f}")

MIN_LOGICAL_W, MIN_LOGICAL_H = 1240, 680
min_phys_w = round(MIN_LOGICAL_W * scale)
min_phys_h = round(MIN_LOGICAL_H * scale)

proc = subprocess.Popen([EXE, "--open-settings"])
print(f"Launched BYH.exe pid={proc.pid}")

EnumWindowsProc = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)


def find_settings_window(pid, timeout=25.0):
    deadline = time.time() + timeout
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


HWND_TOPMOST = -1
HWND_NOTOPMOST = -2
SWP_NOMOVE = 0x0002
SWP_NOSIZE = 0x0001


def capture(hwnd, path):
    user32.ShowWindow(hwnd, 9)  # SW_RESTORE
    fg = user32.SetForegroundWindow(hwnd)
    # Pin the window topmost so foreground apps (browsers etc.) cannot
    # occlude it mid-capture; drop topmost again right after the grab.
    top = user32.SetWindowPos(hwnd, wintypes.HWND(HWND_TOPMOST), 0, 0, 0, 0,
                              SWP_NOMOVE | SWP_NOSIZE)
    print(f"foreground={fg} topmost={top}")
    time.sleep(1.2)
    rect = wintypes.RECT()
    user32.GetWindowRect(hwnd, ctypes.byref(rect))
    box = (rect.left, rect.top, rect.right, rect.bottom)
    img = ImageGrab.grab(bbox=box, all_screens=True)
    user32.SetWindowPos(hwnd, wintypes.HWND(HWND_NOTOPMOST), 0, 0, 0, 0,
                        SWP_NOMOVE | SWP_NOSIZE)
    img.save(path)
    print(f"saved {path} rect={box} size={img.size}")
    return box


try:
    hwnd, title, w, h = find_settings_window(proc.pid)
    print(f"window hwnd={hwnd} title={title!r} {w}x{h}")

    # Default size capture.
    capture(hwnd, OUT_DEFAULT)

    # Resize to logical minimum (physical pixels) and capture.
    SWP = 0x0040  # SWP_SHOWWINDOW
    user32.SetWindowPos(hwnd, None, 30, 30, min_phys_w, min_phys_h, SWP)
    time.sleep(1.5)
    capture(hwnd, OUT_MINIMUM)
finally:
    subprocess.run(["taskkill", "/F", "/PID", str(proc.pid)],
                   capture_output=True)
    print("terminated BYH.exe")
