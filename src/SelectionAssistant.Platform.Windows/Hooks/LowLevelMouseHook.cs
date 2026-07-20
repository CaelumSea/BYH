using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using SelectionAssistant.Platform.Abstractions;

namespace SelectionAssistant.Platform.Windows.Hooks;

/// <summary>
/// Windows low-level mouse hook. The native callback deliberately does only
/// lightweight event projection before immediately continuing the hook chain.
/// </summary>
public sealed class LowLevelMouseHook : IMouseHook
{
    private const int WhMouseLl = 14;
    private const int WmLButtonDown = 0x0201;
    private const int WmLButtonUp = 0x0202;
    private const int WmRButtonDown = 0x0204;
    private const int WmRButtonUp = 0x0205;
    private const uint WmQuit = 0x0012;
    private const uint LlmhfInjected = 0x00000001;

    private readonly object _lifecycleGate = new();
    private readonly ManualResetEventSlim _startupCompleted = new(false);
    private readonly Action<string>? _log;

    private Thread? _hookThread;
    private LowLevelMouseProc? _hookProc;
    private nint _hookHandle;
    private uint _nativeThreadId;
    private int _startupError;
    private Exception? _startupException;
    private int _running;
    private int _disposed;

    public LowLevelMouseHook(Action<string>? log = null)
    {
        _log = log;
    }

    /// <inheritdoc />
    public event Action<MouseEventData>? MouseEvent;

    /// <inheritdoc />
    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        lock (_lifecycleGate)
        {
            if (Volatile.Read(ref _running) != 0)
            {
                return;
            }

            _startupError = 0;
            _startupException = null;
            _startupCompleted.Reset();
            Volatile.Write(ref _running, 1);

            _hookThread = new Thread(HookThreadMain)
            {
                IsBackground = true,
                Name = "BYH.MouseHook",
                Priority = ThreadPriority.Normal,
            };
            _hookThread.Start();
        }

        if (!_startupCompleted.Wait(TimeSpan.FromSeconds(5)))
        {
            Dispose();
            throw new TimeoutException("Timed out while starting the low-level mouse hook.");
        }

        if (_startupError != 0)
        {
            throw new Win32Exception(_startupError, "SetWindowsHookExW(WH_MOUSE_LL) failed.");
        }

        if (_startupException is not null)
        {
            throw new InvalidOperationException("The low-level mouse hook thread failed during startup.", _startupException);
        }
    }

    private void HookThreadMain()
    {
        _nativeThreadId = GetCurrentThreadId();

        try
        {
            // PostThreadMessage requires the target thread to already own a message queue.
            PeekMessageW(out _, 0, 0, 0, 0);

            _hookProc = HookCallback; // Root the delegate for the full hook lifetime.
            nint moduleHandle = GetModuleHandleW(null);
            _hookHandle = SetWindowsHookExW(WhMouseLl, _hookProc, moduleHandle, 0);

            if (_hookHandle == 0)
            {
                _startupError = Marshal.GetLastWin32Error();
                Volatile.Write(ref _running, 0);
                _startupCompleted.Set();
                return;
            }

            _log?.Invoke($"Mouse hook installed on native thread {_nativeThreadId}.");
            _startupCompleted.Set();

            while (Volatile.Read(ref _running) != 0)
            {
                int result = GetMessageW(out NativeMessage message, 0, 0, 0);
                if (result <= 0)
                {
                    break;
                }

                TranslateMessage(in message);
                DispatchMessageW(in message);
            }
        }
        catch (Exception exception)
        {
            _startupException = exception;
            Trace.TraceError($"Mouse hook thread failed: {exception}");
            _log?.Invoke($"Mouse hook thread failed: {exception.GetType().Name}.");
        }
        finally
        {
            _startupCompleted.Set();

            nint hookHandle = Interlocked.Exchange(ref _hookHandle, 0);
            if (hookHandle != 0)
            {
                UnhookWindowsHookEx(hookHandle);
            }

            _nativeThreadId = 0;
            Volatile.Write(ref _running, 0);
            _log?.Invoke("Mouse hook stopped.");
        }
    }

    private nint HookCallback(int code, nint wParam, nint lParam)
    {
        try
        {
            if (code >= 0)
            {
                int message = unchecked((int)wParam);
                // Project left- and right-button events into the platform-agnostic
                // enum. CRITICAL: we always fall through to CallNextHookEx below —
                // the hook only *observes* events, it never swallows them, so the
                // source application's own right-click context menus keep working.
                MouseMessageType? messageType = message switch
                {
                    WmLButtonDown => MouseMessageType.LeftButtonDown,
                    WmLButtonUp => MouseMessageType.LeftButtonUp,
                    WmRButtonDown => MouseMessageType.RightButtonDown,
                    WmRButtonUp => MouseMessageType.RightButtonUp,
                    _ => null,
                };

                if (messageType is { } resolved)
                {
                    MsllHookStruct nativeEvent = Marshal.PtrToStructure<MsllHookStruct>(lParam);
                    var eventData = new MouseEventData(
                        nativeEvent.Point.X,
                        nativeEvent.Point.Y,
                        resolved,
                        Environment.TickCount64,
                        (nativeEvent.Flags & LlmhfInjected) != 0,
                        nativeEvent.ExtraInfo);

                    RaiseMouseEventSafely(eventData);
                }
            }
        }
        catch (Exception exception)
        {
            // Managed exceptions must never cross the native hook boundary.
            Trace.TraceError($"Mouse hook callback failed: {exception}");
        }

        return CallNextHookEx(_hookHandle, code, wParam, lParam);
    }

    private void RaiseMouseEventSafely(MouseEventData eventData)
    {
        Delegate[] handlers = MouseEvent?.GetInvocationList() ?? [];
        foreach (Action<MouseEventData> handler in handlers.Cast<Action<MouseEventData>>())
        {
            try
            {
                handler(eventData);
            }
            catch (Exception exception)
            {
                Trace.TraceError($"Mouse event handler failed: {exception}");
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Volatile.Write(ref _running, 0);

        uint nativeThreadId = Volatile.Read(ref _nativeThreadId);
        if (nativeThreadId != 0)
        {
            PostThreadMessageW(nativeThreadId, WmQuit, 0, 0);
        }

        Thread? hookThread;
        lock (_lifecycleGate)
        {
            hookThread = _hookThread;
            _hookThread = null;
        }

        if (hookThread is not null && hookThread != Thread.CurrentThread)
        {
            hookThread.Join(TimeSpan.FromSeconds(2));
        }

        GC.SuppressFinalize(this);
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativePoint
    {
        public readonly int X;
        public readonly int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct MsllHookStruct
    {
        public readonly NativePoint Point;
        public readonly uint MouseData;
        public readonly uint Flags;
        public readonly uint Time;
        public readonly nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeMessage
    {
        public readonly nint WindowHandle;
        public readonly uint Message;
        public readonly nuint WParam;
        public readonly nint LParam;
        public readonly uint Time;
        public readonly NativePoint Point;
        public readonly uint Private;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint LowLevelMouseProc(int code, nint wParam, nint lParam);

    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    private static extern nint SetWindowsHookExW(
        int hookId,
        LowLevelMouseProc hookProc,
        nint moduleHandle,
        uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(nint hookHandle);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hookHandle, int code, nint wParam, nint lParam);

    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? moduleName);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", EntryPoint = "GetMessageW", SetLastError = true)]
    private static extern int GetMessageW(out NativeMessage message, nint windowHandle, uint min, uint max);

    [DllImport("user32.dll", EntryPoint = "PeekMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessageW(
        out NativeMessage message,
        nint windowHandle,
        uint min,
        uint max,
        uint removeMessage);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(in NativeMessage message);

    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")]
    private static extern nint DispatchMessageW(in NativeMessage message);

    [DllImport("user32.dll", EntryPoint = "PostThreadMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessageW(uint threadId, uint message, nint wParam, nint lParam);
}
