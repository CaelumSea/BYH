using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace SelectionSpike;

/// <summary>
/// 门1 验证:Win32 低级鼠标钩子(WH_MOUSE_LL)。
/// 在专用线程上安装,带消息循环,回调只做轻量工作(记录坐标),立刻 CallNextHookEx。
/// 参考: https://learn.microsoft.com/en-us/windows/win32/winmsg/lowlevelmouseproc
/// </summary>
internal sealed class LowLevelMouseHook : IDisposable
{
    // ── Win32 常量 ──
    private const int WH_MOUSE_LL = 14;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;

    // ── P/Invoke ──
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;      // LLMHF_INJECTED = 0x04 等
        public uint time;
        public nuint dwExtraInfo;
    }

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    // ── 状态 ──
    private Thread? _hookThread;
    private IntPtr _hookHandle = IntPtr.Zero;
    private HookProc? _hookProc;          // 必须 keep alive,否则 GC 回收委托 → 崩溃
    private volatile bool _running;

    // 鼠标事件回调(在钩子线程上触发)
    public event Action<int, int, int>? MouseEvent;   // (x, y, msg)

    public void Start()
    {
        if (_running) return;
        _running = true;

        _hookThread = new Thread(ThreadProc)
        {
            IsBackground = true,
            Name = "MouseHookThread",
            // 评审纠正:默认 Normal,不抢系统资源;仅实测不够快才调高
            Priority = ThreadPriority.Normal,
        };
        _hookThread.Start();
    }

    private void ThreadProc()
    {
        // 1. 安装钩子(必须在有消息循环的线程上)
        _hookProc = HookCallback;   // 保活引用
        IntPtr hModule = GetModuleHandle(null);
        // GetModuleHandle 返回的是 HINSTANCE 的近似(IntPtr),用于 SetWindowsHookEx
        _hookHandle = SetWindowsHookEx(WH_MOUSE_LL, _hookProc, hModule, 0);

        if (_hookHandle == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            SpikeLog.Log($"[Hook] ✗ SetWindowsHookEx 失败,Win32 错误码 = {err}");
            _running = false;
            return;
        }

        SpikeLog.Log($"[Hook] ✓ 钩子已安装 (handle={_hookHandle}, thread={Environment.CurrentManagedThreadId})");

        // 2. 消息循环(低级钩子要求,否则收不到回调)
        // 低级钩子虽然不产生窗口,但需要 GetMessage 循环驱动回调分发
        while (_running)
        {
            // PeekMessage/GetMessage 循环 —— 这里用 GetMessage 阻塞等待最省 CPU
            var msg = new NativeMsg();
            int ret = GetMessage(out msg, IntPtr.Zero, 0, 0);
            if (ret <= 0) break;   // -1 错误 / 0 WM_QUIT
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        // 3. 卸载
        if (_hookHandle != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }
        System.Diagnostics.Debug.WriteLine("[Hook] 钩子已卸载");
    }

    private int _callbackCount;  // 诊断:确认回调到底有没有触发

    private IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        // code < 0 必须直接传给下一个钩子(不处理)
        if (code >= 0)
        {
            int msg = wParam.ToInt32();
            if (msg == WM_LBUTTONDOWN || msg == WM_LBUTTONUP)
            {
                var hookStruct = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                int n = System.Threading.Interlocked.Increment(ref _callbackCount);
                // 只在前 3 次和第 10/50/100 次打日志,避免刷屏
                if (n <= 3 || n == 10 || n == 50 || n == 100)
                    SpikeLog.Log($"[Hook] 回调 #{n} msg={msg} ({hookStruct.pt.X},{hookStruct.pt.Y})");
                // 回调内只做最轻量的工作:转发坐标,立即返回
                // 不在此处做任何重活(剪贴板/HTTP/UI)
                MouseEvent?.Invoke(hookStruct.pt.X, hookStruct.pt.Y, msg);
            }
        }

        // 必须调用 CallNextHookEx,否则阻断全局鼠标
        return CallNextHookEx(_hookHandle, code, wParam, lParam);
    }

    // ── 消息循环所需的 P/Invoke ──
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMsg
    {
        public IntPtr hWnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    [DllImport("user32.dll")]
    private static extern int GetMessage(out NativeMsg lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref NativeMsg lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref NativeMsg lpMsg);

    public void Dispose()
    {
        if (!_running) return;
        _running = false;
        // 投递 WM_QUIT 让 GetMessage 循环退出,线程自行卸载钩子
        PostThreadMessage(_hookThread!.ManagedThreadId, 0x0012 /*WM_QUIT*/, IntPtr.Zero, IntPtr.Zero);
    }

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(int idThread, uint msg, IntPtr wParam, IntPtr lParam);
}
