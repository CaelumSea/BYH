using System;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace SelectionSpike;

public partial class MainWindow : Window
{
    // ── 门2 的核心:Win32 扩展样式 + 不激活显示 ──
    // 参考: https://learn.microsoft.com/en-us/windows/win32/winmsg/extended-window-styles
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;   // 关键:窗口不抢焦点
    private const int WS_EX_TOOLWINDOW = 0x00000080;    // 不进任务栏/Alt+Tab
    private const int WS_EX_TOPMOST = 0x00000008;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const int SW_SHOWNOACTIVATE = 4;
    private static readonly IntPtr HWND_TOPMOST = new(-1);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private bool _nativeFlagsApplied;

    public MainWindow()
    {
        InitializeComponent();
        // 窗口首次渲染后,拿到 Win32 HWND 并注入样式
        Opened += (_, _) =>
        {
            ApplyNoActivateFlags();
            SpikeLog.Log($"[Window] Opened,Hwnd={GetHwnd()}");
            // 启动即隐藏 —— 窗口只在鼠标抬起检测到拖拽时弹出。
            // 门2 已验证:WS_EX_NOACTIVATE 让弹窗不抢焦点(选中蓝色高亮保住)。
            Hide();
            SpikeLog.Log("[Window] 已隐藏,等待鼠标拖拽");
        };
        // 阻止窗口被关闭导致程序退出(Phase 0 用它做浮动弹窗)
        Closing += (_, e) => e.Cancel = true;
    }

    /// <summary>
    /// 给 Avalonia 创建的底层 HWND 注入 WS_EX_NOACTIVATE 等扩展样式。
    /// 这是门2 验证的核心:Avalonia 默认不带 NOACTIVATE,必须手动注入。
    /// </summary>
    private void ApplyNoActivateFlags()
    {
        var handle = GetHwnd();
        if (handle == null || _nativeFlagsApplied) return;

        var hwnd = handle.Value;
        int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        exStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST;
        SetWindowLong(hwnd, GWL_EXSTYLE, exStyle);

        _nativeFlagsApplied = true;
        System.Diagnostics.Debug.WriteLine($"[Window] 已注入 WS_EX_NOACTIVATE (hwnd={hwnd})");
    }

    /// <summary>
    /// 在指定屏幕坐标显示窗口(不激活)。
    /// 鼠标抬起时调用此方法定位弹窗。
    /// </summary>
    public void ShowAtNoActivate(int x, int y)
    {
        var handle = GetHwnd();
        if (handle == null)
        {
            SpikeLog.Log("[Window] ShowAtNoActivate:拿不到 HWND,放弃");
            return;
        }

        var hwnd = handle.Value;

        // 确保样式已注入(窗口可能被 Hide 过)
        if (!_nativeFlagsApplied) ApplyNoActivateFlags();

        // 偏移:弹在鼠标右下方一点,避免挡住光标
        int posX = x + 16;
        int posY = y + 16;

        SpikeLog.Log($"[Window] ShowAtNoActivate hwnd={hwnd} pos=({posX},{posY}) flags={_nativeFlagsApplied}");

        // SetWindowPos 定位 + SWP_NOACTIVATE 确保不激活
        bool ok = SetWindowPos(hwnd, HWND_TOPMOST, posX, posY, 0, 0,
            SWP_NOACTIVATE | SWP_SHOWWINDOW | SWP_NOSIZE | SWP_NOZORDER);
        SpikeLog.Log($"[Window] SetWindowPos 返回 {ok},Win32 错误 = {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");

        // 双保险:SW_SHOWNOACTIVATE 显示
        if (!IsVisible)
        {
            ShowWindow(hwnd, SW_SHOWNOACTIVATE);
            SpikeLog.Log("[Window] ShowWindow(SW_SHOWNOACTIVATE) 已调用");
        }
    }

    /// <summary>
    /// 从钩子线程更新坐标文本 —— 必须切到 UI 线程(Avalonia 单线程 UI 模型)。
    /// </summary>
    public void UpdateCoordFromHook(int x, int y, string phase)
    {
        Dispatcher.UIThread.Post(() =>
        {
            CoordText.Text = $"{phase}  ({x}, {y})";
        });
    }

    private IntPtr? GetHwnd()
    {
        try
        {
            // Avalonia 12.x:基类 TopLevel 提供公开的 TryGetPlatformHandle()
            var handle = TryGetPlatformHandle();
            return handle?.Handle;
        }
        catch
        {
            return null;
        }
    }
}
