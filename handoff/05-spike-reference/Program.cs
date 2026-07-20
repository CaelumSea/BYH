using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using System;

namespace SelectionSpike;

class Program
{
    // 钩子和主窗口的引用(避免被 GC)
    private static LowLevelMouseHook? _hook;
    private static MainWindow? _mainWindow;

    // 手势判定所需的状态(门3:最简单的拖拽/点击判定)
    private static int _downX, _downY;
    private static long _downTime;
    private static bool _isDown;

    [STAThread]
    public static void Main(string[] args)
    {
        // App.OnFrameworkInitializationCompleted 会创建 MainWindow 并赋值给静态字段
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

        // 程序退出时清理钩子
        _hook?.Dispose();
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();

    /// <summary>
    /// 由 App.OnFrameworkInitializationCompleted 调用,把窗口和钩子接起来。
    /// </summary>
    public static void OnMainWindowCreated(MainWindow window)
    {
        _mainWindow = window;

        _hook = new LowLevelMouseHook();
        _hook.MouseEvent += OnMouseEvent;
        _hook.Start();
    }

    private static void OnMouseEvent(int x, int y, int msg)
    {
        // 这个回调在钩子线程上触发,只做轻量判定,UI 操作切到 UI 线程
        const int WM_LBUTTONDOWN = 0x0201;
        const int WM_LBUTTONUP = 0x0202;

        if (msg == WM_LBUTTONDOWN)
        {
            _isDown = true;
            _downX = x; _downY = y;
            _downTime = Environment.TickCount64;
        }
        else if (msg == WM_LBUTTONUP && _isDown)
        {
            _isDown = false;
            long elapsed = Environment.TickCount64 - _downTime;
            int dx = Math.Abs(x - _downX);
            int dy = Math.Abs(y - _downY);

            // 诊断:把每次抬起的判定数据都记下来
            SpikeLog.Log($"[Gesture] UP dx={dx} dy={dy} elapsed={elapsed}ms");

            // 最简单的拖拽判定:移动超过 8 像素 或 时长 > 150ms 视为"选词"
            // (门3 只验证链路通不通,正式判定逻辑用系统指标,见 v4 文档 5.2)
            bool isSelectionLike = dx >= 8 || dy >= 8 || elapsed > 150;

            // 钩子线程到此为止 —— 任何 UI 访问都必须 Post 到 UI 线程,
            // 否则 Avalonia 抛 InvalidOperationException("不同线程拥有此对象")。
            // 捕获到局部变量,避免闭包捕获可能被改写的字段。
            int cx = x, cy = y;

            if (isSelectionLike && _mainWindow != null)
            {
                SpikeLog.Log("[Gesture] → 判定为拖拽,准备弹窗");
                Dispatcher.UIThread.Post(() =>
                {
                    if (_mainWindow == null) return;
                    _mainWindow.ShowAtNoActivate(cx, cy);
                    _mainWindow.UpdateCoordFromHook(cx, cy, "✓ 弹窗");
                    SpikeLog.Log("[UI] 弹窗调用完成 (UI 线程)");
                });
            }
            else if (_mainWindow != null)
            {
                SpikeLog.Log("[Gesture] → 判定为单击,不弹窗");
                Dispatcher.UIThread.Post(() =>
                    _mainWindow?.UpdateCoordFromHook(cx, cy, "单击 (未弹窗)"));
            }
        }
    }
}
