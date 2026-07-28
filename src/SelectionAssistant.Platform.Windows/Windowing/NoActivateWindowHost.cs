using System.ComponentModel;
using System.Runtime.InteropServices;
using SelectionAssistant.Platform.Abstractions;

namespace SelectionAssistant.Platform.Windows.Windowing;

/// <summary>
/// Applies and preserves the Win32 no-activation contract for an existing HWND.
/// The HWND remains owned by the UI layer.
/// </summary>
public sealed partial class NoActivateWindowHost : IWindowFocusController
{
    private const int GwlExStyle = -20;
    private const long WsExTopmost = 0x00000008L;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExNoActivate = 0x08000000L;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private const uint SwpShowWindow = 0x0040;
    private const int SwHide = 0;
    private const int SwShowNoActivate = 4;

    private static readonly nint HwndTopmost = new(-1);

    private readonly nint _windowHandle;

    public NoActivateWindowHost(nint windowHandle)
    {
        if (windowHandle == 0)
        {
            throw new ArgumentException("A valid HWND is required.", nameof(windowHandle));
        }

        _windowHandle = windowHandle;
        ApplyNoActivateStyles();
    }

    public bool IsVisible => IsWindowVisible(_windowHandle);

    public bool ContainsScreenPoint(int x, int y)
    {
        if (!IsVisible || !GetWindowRect(_windowHandle, out NativeRect rect))
        {
            return false;
        }

        return x >= rect.Left && x < rect.Right &&
               y >= rect.Top && y < rect.Bottom;
    }

    public void ShowAtNoActivate(int x, int y)
    {
        // Legacy anchor-based placement: window top-left = (anchor + 16, anchor + 16).
        ShowAtNoActivatePoint(checked(x + 16), checked(y + 16));
    }

    /// <summary>
    /// Places the window so its top-left is exactly at (left, top) in physical
    /// screen pixels. No offset is applied. R35: the toolbar caller computes
    /// the final top-left via ToolbarWindow.ClampAnchor (which handles the
    /// +16 offset, screen-edge flip, and working-area clamp) and hands it
    /// here directly.
    /// </summary>
    public void ShowAtNoActivatePoint(int left, int top)
    {
        ApplyNoActivateStyles();

        if (!SetWindowPos(
                _windowHandle,
                HwndTopmost,
                left,
                top,
                0,
                0,
                SwpNoActivate | SwpShowWindow | SwpNoSize))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetWindowPos failed.");
        }

        ShowWindow(_windowHandle, SwShowNoActivate);
    }

    public void Hide()
    {
        ShowWindow(_windowHandle, SwHide);
    }

    /// <summary>
    /// Re-shows the window at its current top-left position (no relocation),
    /// using the same WS_EX_NOACTIVATE / SWP_NOACTIVATE semantics as
    /// <see cref="ShowAtNoActivatePoint"/>. Used to restore a window that was
    /// temporarily <see cref="Hide"/>den (e.g. hiding the toolbar so it isn't
    /// captured into a screenshot, then showing it again at the same spot).
    /// </summary>
    public void ShowAtCurrentPosition()
    {
        if (!GetWindowRect(_windowHandle, out NativeRect rect))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "GetWindowRect failed.");
        }
        ShowAtNoActivatePoint(rect.Left, rect.Top);
    }

    private void ApplyNoActivateStyles()
    {
        nint style = GetWindowLongPtr(_windowHandle, GwlExStyle);
        nint desiredStyle = style | (nint)(WsExNoActivate | WsExToolWindow | WsExTopmost);

        if (desiredStyle != style)
        {
            Marshal.SetLastPInvokeError(0);
            nint previousStyle = SetWindowLongPtr(_windowHandle, GwlExStyle, desiredStyle);
            int error = Marshal.GetLastPInvokeError();
            if (previousStyle == 0 && error != 0)
            {
                throw new Win32Exception(error, "SetWindowLongPtrW(GWL_EXSTYLE) failed.");
            }

            SetWindowPos(
                _windowHandle,
                0,
                0,
                0,
                0,
                0,
                SwpNoActivate | SwpNoSize | SwpNoMove | SwpNoZOrder | SwpFrameChanged);
        }
    }

    private static nint GetWindowLongPtr(nint windowHandle, int index) =>
        nint.Size == 8
            ? GetWindowLongPtr64(windowHandle, index)
            : new nint(GetWindowLong32(windowHandle, index));

    private static nint SetWindowLongPtr(nint windowHandle, int index, nint value) =>
        nint.Size == 8
            ? SetWindowLongPtr64(windowHandle, index, value)
            : new nint(SetWindowLong32(windowHandle, index, value.ToInt32()));

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static partial int GetWindowLong32(nint windowHandle, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static partial int SetWindowLong32(nint windowHandle, int index, int newValue);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static partial nint GetWindowLongPtr64(nint windowHandle, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static partial nint SetWindowLongPtr64(nint windowHandle, int index, nint newValue);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(
        nint windowHandle,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(nint windowHandle, int command);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(nint windowHandle);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint windowHandle, out NativeRect rect);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativeRect(int Left, int Top, int Right, int Bottom);
}
