#!/usr/bin/env python3
"""Capture BYH Settings window across all seven navigation tabs.

Navigation uses stable Avalonia Automation IDs through Windows UI Automation.
Measured coordinates remain as a compatibility fallback for machines where
the UIAutomationClient assembly is unavailable.
"""
import ctypes
import os
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

# Navigation button centers are relative to the complete window rectangle.
# They are used only as a fallback and were measured at 175% DPI, including
# the title bar.
NAV_BUTTONS = [
    ("dashboard", "BYH.Settings.Nav.Dashboard", 455, 305),
    ("general", "BYH.Settings.Nav.General", 455, 370),
    ("provider", "BYH.Settings.Nav.Translation", 455, 438),
    ("actions", "BYH.Settings.Nav.Actions", 455, 505),
    ("vision", "BYH.Settings.Nav.Vision", 455, 573),
    ("launcher", "BYH.Settings.Nav.Launcher", 455, 641),
    ("clipboard", "BYH.Settings.Nav.Clipboard", 455, 709),
]

# Keep every capture on the same monitor and at the same origin. BYH may be
# nudged by desktop snap helpers while automated mouse clicks move between
# tabs; re-anchoring makes the screenshots deterministic.
CAPTURE_X = 100
CAPTURE_Y = 0

# Windows PowerShell ships with Windows and can use the platform's
# UIAutomationClient assembly without adding a Python package dependency.
UIA_INVOKE_SCRIPT = r"""
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
$windowHandle = [IntPtr]([Int64]$env:BYH_UIA_HWND)
$automationId = $env:BYH_UIA_AUTOMATION_ID
$window = [System.Windows.Automation.AutomationElement]::FromHandle($windowHandle)
if ($null -eq $window) {
    throw "Window automation element was not found."
}
$condition = [System.Windows.Automation.PropertyCondition]::new(
    [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
    $automationId)
$element = $window.FindFirst(
    [System.Windows.Automation.TreeScope]::Descendants,
    $condition)
if ($null -eq $element) {
    throw "Automation ID '$automationId' was not found."
}
$pattern = $element.GetCurrentPattern(
    [System.Windows.Automation.InvokePattern]::Pattern)
([System.Windows.Automation.InvokePattern]$pattern).Invoke()
[Console]::Out.Write('invoked')
"""

UIA_SCROLL_BOTTOM_SCRIPT = r"""
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
$windowHandle = [IntPtr]([Int64]$env:BYH_UIA_HWND)
$automationId = 'BYH.Settings.ContentScroll'
$window = [System.Windows.Automation.AutomationElement]::FromHandle($windowHandle)
if ($null -eq $window) {
    throw "Window automation element was not found."
}
$condition = [System.Windows.Automation.PropertyCondition]::new(
    [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
    $automationId)
$element = $window.FindFirst(
    [System.Windows.Automation.TreeScope]::Descendants,
    $condition)
if ($null -eq $element) {
    throw "Automation ID '$automationId' was not found."
}
$pattern = $element.GetCurrentPattern(
    [System.Windows.Automation.ScrollPattern]::Pattern)
$scroll = [System.Windows.Automation.ScrollPattern]$pattern
if (-not $scroll.Current.VerticallyScrollable) {
    [Console]::Out.Write('not-scrollable')
    exit 0
}
$scroll.SetScrollPercent(
    [System.Windows.Automation.ScrollPattern]::NoScroll,
    100.0)
[Console]::Out.Write('scrolled-to-bottom')
"""


def anchor_window(hwnd):
    user32.ShowWindow(hwnd, 9)  # SW_RESTORE
    user32.SetWindowPos(hwnd, wintypes.HWND(HWND_TOPMOST),
                       CAPTURE_X, CAPTURE_Y, 0, 0, SWP_NOSIZE)
    user32.SetForegroundWindow(hwnd)
    time.sleep(0.2)


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
    anchor_window(hwnd)
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
    anchor_window(hwnd)
    rect = wintypes.RECT()
    user32.GetWindowRect(hwnd, ctypes.byref(rect))
    x = rect.left + rel_x
    y = rect.top + rel_y
    user32.SetCursorPos(x, y)
    time.sleep(0.08)
    user32.mouse_event(0x0002, 0, 0, 0, 0)
    time.sleep(0.08)
    user32.mouse_event(0x0004, 0, 0, 0, 0)
    time.sleep(0.18)


def invoke_automation_id(hwnd, automation_id):
    env = os.environ.copy()
    env["BYH_UIA_HWND"] = str(hwnd)
    env["BYH_UIA_AUTOMATION_ID"] = automation_id
    try:
        result = subprocess.run(
            [
                "powershell.exe",
                "-NoLogo",
                "-NoProfile",
                "-NonInteractive",
                "-ExecutionPolicy",
                "Bypass",
                "-Command",
                UIA_INVOKE_SCRIPT,
            ],
            capture_output=True,
            text=True,
            errors="replace",
            timeout=10,
            env=env,
        )
    except (OSError, subprocess.TimeoutExpired) as exc:
        return False, str(exc)

    if result.returncode == 0 and result.stdout.strip() == "invoked":
        return True, ""

    detail = (result.stderr or result.stdout or "unknown UIA error").strip()
    return False, " ".join(detail.splitlines())


def scroll_to_bottom_with_uia(hwnd):
    env = os.environ.copy()
    env["BYH_UIA_HWND"] = str(hwnd)
    try:
        result = subprocess.run(
            [
                "powershell.exe",
                "-NoLogo",
                "-NoProfile",
                "-NonInteractive",
                "-ExecutionPolicy",
                "Bypass",
                "-Command",
                UIA_SCROLL_BOTTOM_SCRIPT,
            ],
            capture_output=True,
            text=True,
            errors="replace",
            timeout=10,
            env=env,
        )
    except (OSError, subprocess.TimeoutExpired) as exc:
        return False, str(exc)

    status = result.stdout.strip()
    if result.returncode == 0 and status in {
        "scrolled-to-bottom",
        "not-scrollable",
    }:
        return True, status

    detail = (result.stderr or result.stdout or "unknown UIA error").strip()
    return False, " ".join(detail.splitlines())


def scroll_to_bottom_with_wheel(hwnd):
    """Compatibility fallback when ScrollPattern is unavailable."""
    anchor_window(hwnd)
    rect = wintypes.RECT()
    user32.GetWindowRect(hwnd, ctypes.byref(rect))
    width = rect.right - rect.left
    height = rect.bottom - rect.top
    # Point inside the central settings surface, away from editable controls.
    user32.SetCursorPos(
        rect.left + int(width * 0.55),
        rect.top + int(height * 0.42),
    )
    time.sleep(0.1)
    for _ in range(48):
        user32.mouse_event(0x0800, 0, 0, (-120) & 0xFFFFFFFF, 0)
    time.sleep(0.4)


def scroll_to_bottom(hwnd, require_uia):
    anchor_window(hwnd)
    scrolled, detail = scroll_to_bottom_with_uia(hwnd)
    if scrolled:
        print(f"scroll=UIA status={detail}")
        return

    if require_uia:
        raise RuntimeError(f"UI Automation scroll failed: {detail}")

    print(f"scroll=wheel-fallback reason={detail}")
    scroll_to_bottom_with_wheel(hwnd)


def click_navigation(hwnd, automation_id, rel_x, rel_y, require_uia):
    anchor_window(hwnd)
    invoked, detail = invoke_automation_id(hwnd, automation_id)
    if invoked:
        print(f"navigation=UIA automation_id={automation_id}")
        return

    if require_uia:
        raise RuntimeError(
            f"UI Automation failed for {automation_id}: {detail}")

    print(
        f"navigation=coordinate-fallback automation_id={automation_id} "
        f"reason={detail}")
    click_client(hwnd, rel_x, rel_y)


def main():
    if len(sys.argv) < 3:
        raise SystemExit(
            "usage: capture-all-tabs.py <BYH.exe> <output-dir> "
            "[--require-uia] [--include-bottom]")

    exe = Path(sys.argv[1])
    out_dir = Path(sys.argv[2])
    unknown = set(sys.argv[3:]) - {"--require-uia", "--include-bottom"}
    if unknown:
        raise SystemExit(f"unknown arguments: {sorted(unknown)}")
    require_uia = "--require-uia" in sys.argv[3:]
    include_bottom = "--include-bottom" in sys.argv[3:]
    out_dir.mkdir(parents=True, exist_ok=True)

    proc = subprocess.Popen([str(exe), "--open-settings"])
    print(f"Launched BYH.exe pid={proc.pid}")
    time.sleep(2.5)

    hwnd, title, w, h = find_settings_window(proc.pid)
    print(f"window hwnd={hwnd} title={title!r} {w}x{h}")

    try:
        for tab, automation_id, rel_x, rel_y in NAV_BUTTONS:
            click_navigation(
                hwnd, automation_id, rel_x, rel_y, require_uia)
            time.sleep(1.5)
            top_name = (
                f"settings-{tab}-top-nativeaot.png"
                if include_bottom
                else f"v25-unified-tabs-{tab}-default-nativeaot.png"
            )
            capture(hwnd, out_dir / top_name)
            if include_bottom:
                scroll_to_bottom(hwnd, require_uia)
                time.sleep(0.6)
                capture(
                    hwnd,
                    out_dir / f"settings-{tab}-bottom-nativeaot.png",
                )
    finally:
        subprocess.run(["taskkill", "/F", "/PID", str(proc.pid)], capture_output=True)
        print("terminated BYH.exe")


if __name__ == "__main__":
    main()
