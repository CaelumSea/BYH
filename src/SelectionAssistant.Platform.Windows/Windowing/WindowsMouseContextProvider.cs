using System.Runtime.InteropServices;

namespace SelectionAssistant.Platform.Windows.Windowing;

/// <summary>Looks up the root top-level window and process at a screen point.</summary>
public sealed class WindowsMouseContextProvider
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

    [DllImport("user32.dll")]
    private static extern nint WindowFromPoint(NativePoint point);

    [DllImport("user32.dll")]
    private static extern nint GetAncestor(nint windowHandle, uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint windowHandle, out uint processId);
}

public readonly record struct WindowsWindowContext(nint RootWindowHandle, uint ProcessId)
{
    public static WindowsWindowContext Empty { get; } = new(0, 0);
}
