using System.ComponentModel;
using System.Runtime.InteropServices;
using SelectionAssistant.Core.Input;

namespace SelectionAssistant.Platform.Windows.Input;

/// <summary>Global RegisterHotKey registration hosted on a dedicated message thread.</summary>
public sealed class WindowsGlobalHotKey : IDisposable
{
    private const uint WmHotKey = 0x0312;
    private const uint WmQuit = 0x0012;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private const uint ModNoRepeat = 0x4000;
    private static int _nextId = 0x4200;

    private readonly ManualResetEventSlim _started = new(false);
    private readonly Thread _thread;
    private readonly int _id = Interlocked.Increment(ref _nextId);
    private Exception? _startError;
    private uint _threadId;
    private int _startCalled;
    private int _disposed;

    public WindowsGlobalHotKey(OceanEyesTriggerSettings settings)
    {
        Settings = (settings ?? throw new ArgumentNullException(nameof(settings))).Normalize();
        Settings.Validate();
        if (!Settings.KeyboardShortcutEnabled)
        {
            throw new ArgumentException("不能注册已禁用的键盘快捷键。", nameof(settings));
        }

        _thread = new Thread(MessageLoop)
        {
            IsBackground = true,
            Name = "BYH.GlobalHotKey",
        };
    }

    public OceanEyesTriggerSettings Settings { get; }

    public event Action<int, int>? Triggered;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _startCalled, 1) != 0)
        {
            throw new InvalidOperationException("全局快捷键只能启动一次。");
        }

        _thread.Start();
        if (!_started.Wait(TimeSpan.FromSeconds(3)))
        {
            Dispose();
            throw new TimeoutException("全局快捷键线程启动超时。");
        }

        if (_startError is not null)
        {
            throw _startError;
        }
    }

    private void MessageLoop()
    {
        _threadId = GetCurrentThreadId();
        bool registered = false;
        try
        {
            uint modifiers = ToNativeModifiers(Settings.Modifiers) | ModNoRepeat;
            uint virtualKey = ToVirtualKey(Settings.Key);
            if (!RegisterHotKey(0, _id, modifiers, virtualKey))
            {
                int error = Marshal.GetLastWin32Error();
                _startError = new GlobalHotKeyRegistrationException(
                    Settings.ToDisplayText(), error);
                return;
            }

            registered = true;
            _started.Set();
            while (GetMessage(out NativeMessage message, 0, 0, 0) > 0)
            {
                if (message.Message != WmHotKey || message.WParam != new nint(_id))
                {
                    continue;
                }

                int x = 0;
                int y = 0;
                if (GetCursorPos(out NativePoint point))
                {
                    x = point.X;
                    y = point.Y;
                }

                try { Triggered?.Invoke(x, y); } catch { }
            }
        }
        catch (Exception exception)
        {
            _startError = exception;
        }
        finally
        {
            if (!_started.IsSet) _started.Set();
            if (registered) UnregisterHotKey(0, _id);
        }
    }

    public static uint ToVirtualKey(string key)
    {
        string normalized = key.Trim();
        if (normalized.Length == 1)
        {
            char c = char.ToUpperInvariant(normalized[0]);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9') return c;
        }

        if (normalized.Equals("Space", StringComparison.OrdinalIgnoreCase)) return 0x20;
        if (normalized.StartsWith('F') &&
            int.TryParse(normalized.AsSpan(1), out int functionKey) &&
            functionKey is >= 1 and <= 12)
        {
            return checked((uint)(0x70 + functionKey - 1));
        }

        throw new ArgumentException("不支持的快捷键主键。", nameof(key));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        uint threadId = Volatile.Read(ref _threadId);
        if (threadId != 0)
        {
            PostThreadMessage(threadId, WmQuit, 0, 0);
        }

        if (_thread.IsAlive && Environment.CurrentManagedThreadId != _thread.ManagedThreadId)
        {
            _thread.Join(TimeSpan.FromSeconds(2));
        }

        _started.Dispose();
        GC.SuppressFinalize(this);
    }

    private static uint ToNativeModifiers(GlobalHotKeyModifiers modifiers)
    {
        uint result = 0;
        if (modifiers.HasFlag(GlobalHotKeyModifiers.Control)) result |= ModControl;
        if (modifiers.HasFlag(GlobalHotKeyModifiers.Alt)) result |= ModAlt;
        if (modifiers.HasFlag(GlobalHotKeyModifiers.Shift)) result |= ModShift;
        if (modifiers.HasFlag(GlobalHotKeyModifiers.Windows)) result |= ModWin;
        return result;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public nint HWnd;
        public uint Message;
        public nint WParam;
        public nint LParam;
        public uint Time;
        public NativePoint Point;
        public uint Private;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint windowHandle, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint windowHandle, int id);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out NativeMessage message, nint windowHandle, uint min, uint max);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint threadId, uint message, nint wParam, nint lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);
}

public sealed class GlobalHotKeyRegistrationException : Win32Exception
{
    public GlobalHotKeyRegistrationException(string shortcut, int nativeErrorCode)
        : base(nativeErrorCode, $"快捷键 {shortcut} 已被其他程序占用或无法注册。")
    {
        Shortcut = shortcut;
    }

    public string Shortcut { get; }
}
