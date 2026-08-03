using System.Runtime.InteropServices;

namespace SelectionAssistant.Platform.Windows.Windowing;

/// <summary>Looks up the root top-level window and process at a screen point.</summary>
public sealed partial class WindowsMouseContextProvider
{
    private const uint GaRoot = 2;

    // Cached once: the process id never changes during the run. When the cursor
    // is over a BYH-owned window (toolbar, result window, pinned screenshot /
    // sticker, gallery, settings, region overlay) WindowFromPoint resolves to a
    // HWND whose root belongs to this process. Returning Empty there stops the
    // selection runtime from launching a capture chain against BYH itself —
    // which otherwise ran UIA (empty, on a pure-image window) plus the full
    // Ctrl+Insert/Ctrl+C chord chain (~1s of timeouts) on every selection that
    // started over a sticker, producing the reported lag, and historically the
    // second-listener clipboard crash. Selections always target other apps.
    private static readonly uint CurrentProcessId = (uint)Environment.ProcessId;

    public WindowsWindowContext GetContext(int x, int y)
    {
        nint childWindow = WindowFromPoint(new NativePoint(x, y));
        if (childWindow == 0)
        {
            return WindowsWindowContext.Empty;
        }

        nint rootWindow = GetAncestor(childWindow, GaRoot);
        if (rootWindow == 0)
        {
            rootWindow = childWindow;
        }

        GetWindowThreadProcessId(rootWindow, out uint processId);
        if (processId == CurrentProcessId)
        {
            return WindowsWindowContext.Empty;
        }

        return new WindowsWindowContext(rootWindow, processId);
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativePoint(int X, int Y);

    [LibraryImport("user32.dll")]
    private static partial nint WindowFromPoint(NativePoint point);

    [LibraryImport("user32.dll")]
    private static partial nint GetAncestor(nint windowHandle, uint flags);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint windowHandle, out uint processId);
}

public readonly record struct WindowsWindowContext(nint RootWindowHandle, uint ProcessId)
{
    public static WindowsWindowContext Empty { get; } = new(0, 0);
}
