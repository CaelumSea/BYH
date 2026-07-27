using System.Runtime.InteropServices;

namespace SelectionAssistant.Platform.Windows.Windowing;

/// <summary>Looks up the root top-level window and process at a screen point.</summary>
public sealed partial class WindowsMouseContextProvider
{
    private const uint GaRoot = 2;

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
