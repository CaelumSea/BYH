using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SelectionAssistant.Platform.Windows.Hooks;

/// <summary>
/// Windows low-level keyboard hook (<c>WH_KEYBOARD_LL</c>). Unlike the mouse
/// hook's start/stop pattern, this one installs the hook **once** at
/// <see cref="Start" /> and keeps a dedicated background thread alive for the
/// whole app lifetime. The toolbar show/hide cycle just toggles an
/// <see cref="SetEnabled" /> flag — cheap, race-free, no thread churn.
/// <para>
/// When disabled (the default after construction), every key passes through
/// to the focused application unchanged. When enabled, each key-down is
/// offered to <see cref="KeyPressed" /> handlers; a handler returning
/// <c>true</c> swallows the key, <c>false</c> lets it through.
/// </para>
/// <para>
/// <b>Why persistent + flag instead of start/stop:</b> the toolbar is shown
/// and hidden many times per minute. Repeatedly calling SetWindowsHookExW /
/// UnhookWindowsHookEx + spawning/joining a thread races — the previous
/// attempt timed out on the second Start and then disposed the object,
/// bricking all subsequent attempts. The flag model has zero thread lifecycle
/// overhead per toggle.
/// </para>
/// </summary>
public sealed class LowLevelKeyboardHook : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;
    private const uint WmQuit = 0x0012;
    private const uint LlkhfInjected = 0x00000010;

    private readonly object _lifecycleGate = new();
    private readonly ManualResetEventSlim _startupCompleted = new(false);
    private readonly Action<string>? _log;

    private Thread? _hookThread;
    private LowLevelKeyboardProc? _hookProc;
    private nint _hookHandle;
    private uint _nativeThreadId;
    private int _startupError;
    private Exception? _startupException;
    private int _running;     // 1 while the hook thread is alive
    private int _enabled;     // 1 while the toolbar is visible (gate inside callback)
    private int _disposed;

    public LowLevelKeyboardHook(Action<string>? log = null)
    {
        _log = log;
    }

    /// <summary>
    /// Raised on every non-injected key-down on the hook thread, but ONLY
    /// while <see cref="SetEnabled" />(true) is in effect. Handlers return
    /// <c>true</c> to swallow the key (prevent it reaching the focused
    /// application) or <c>false</c> to let it pass through. The argument is
    /// the Win32 virtual-key code (e.g. <c>0x46</c> for 'F', <c>0x1B</c> for Esc).
    /// </summary>
    public event Func<int, bool>? KeyPressed;

    /// <summary>
    /// Installs the hook on a dedicated background thread and blocks until
    /// live (or up to 5 s). Call ONCE at app startup. The hook then runs
    /// until <see cref="Dispose" />; use <see cref="SetEnabled" /> to gate
    /// whether <see cref="KeyPressed" /> is actually raised.
    /// </summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        lock (_lifecycleGate)
        {
            if (Volatile.Read(ref _running) != 0)
            {
                return;  // Already installed — Start is idempotent.
            }

            _startupError = 0;
            _startupException = null;
            _startupCompleted.Reset();
            Volatile.Write(ref _running, 1);

            _hookThread = new Thread(HookThreadMain)
            {
                IsBackground = true,
                Name = "BYH.KeyboardHook",
                Priority = ThreadPriority.Normal,
            };
            _hookThread.Start();
        }

        if (!_startupCompleted.Wait(TimeSpan.FromSeconds(5)))
        {
            // NOTE: do NOT Dispose() here on timeout — that would brick the
            // instance permanently (the previous design did this and the
            // whole feature stopped working after one failed Start). Just
            // surface the error and let the caller decide.
            throw new TimeoutException("Timed out while starting the low-level keyboard hook.");
        }

        if (_startupError != 0)
        {
            throw new Win32Exception(_startupError, "SetWindowsHookExW(WH_KEYBOARD_LL) failed.");
        }

        if (_startupException is not null)
        {
            throw new InvalidOperationException(
                "The low-level keyboard hook thread failed during startup.",
                _startupException);
        }
    }

    /// <summary>
    /// Toggles whether <see cref="KeyPressed" /> handlers are invoked at all.
    /// When <c>false</c>, every key passes through to the focused application
    /// (the hook stays installed but does nothing). Use this for the
    /// toolbar-visible/hidden transition — cheap, no thread lifecycle.
    /// Safe to call from any thread; safe to call before <see cref="Start" />.
    /// </summary>
    public void SetEnabled(bool enabled) =>
        Volatile.Write(ref _enabled, enabled ? 1 : 0);

    private void HookThreadMain()
    {
        _nativeThreadId = GetCurrentThreadId();

        try
        {
            // PostThreadMessage requires the target thread to already own a message queue.
            PeekMessageW(out _, 0, 0, 0, 0);

            _hookProc = HookCallback; // Root the delegate for the full hook lifetime.
            nint moduleHandle = GetModuleHandleW(null);
            _hookHandle = SetWindowsHookExW(WhKeyboardLl, _hookProc, moduleHandle, 0);

            if (_hookHandle == 0)
            {
                _startupError = Marshal.GetLastWin32Error();
                Volatile.Write(ref _running, 0);
                _startupCompleted.Set();
                return;
            }

            _log?.Invoke($"Keyboard hook installed on native thread {_nativeThreadId}.");
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
            Trace.TraceError($"Keyboard hook thread failed: {exception}");
            _log?.Invoke($"Keyboard hook thread failed: {exception.GetType().Name}.");
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
            _log?.Invoke("Keyboard hook stopped.");
        }
    }

    private unsafe nint HookCallback(int code, nint wParam, nint lParam)
    {
        // Default behaviour: pass the key to the next hook / focused window.
        // Returning a non-zero value from HC_ACTION swallows the key.
        // Critical: check the enable flag BEFORE doing any work — when the
        // toolbar is hidden (the common case) this must be a near-no-op so
        // the user's normal typing has zero overhead.
        if (code >= 0 && Volatile.Read(ref _enabled) != 0)
        {
            int message = unchecked((int)wParam);
            if (message == WmKeyDown || message == WmSysKeyDown)
            {
                // Audit H3: read the hook struct via Unsafe.Read<T> (a direct
                // pointer dereference) instead of Marshal.PtrToStructure<T>.
                // The hook callback runs on EVERY keypress system-wide; the
                // marshaler variant allocates + does runtime-type checks per
                // call. Unsafe.Read<T> is a zero-alloc AOT-trim-safe intrinsic.
                KbdllHookStruct nativeEvent = Unsafe.Read<KbdllHookStruct>((void*)lParam);
                // SendInput-generated chords are part of BYH's capture/paste
                // pipeline, not user toolbar shortcuts. Let them continue to
                // the focused application and never feed them into the
                // toolbar dispatcher (which would otherwise see injected C as
                // the toolbar Copy key).
                if ((nativeEvent.Flags & LlkhfInjected) != 0)
                {
                    return CallNextHookEx(_hookHandle, code, wParam, lParam);
                }

                try
                {
                    if (RaiseKeyPressedSafely((int)nativeEvent.VkCode))
                    {
                        // A subscriber claimed this key — swallow it so the
                        // source application never sees it (e.g. pressing 'F'
                        // while the toolbar is visible must not also type 'F').
                        return 1;
                    }
                }
                catch (Exception exception)
                {
                    // Managed exceptions must never cross the native hook boundary.
                    Trace.TraceError($"Keyboard hook callback failed: {exception}");
                }
            }
        }

        return CallNextHookEx(_hookHandle, code, wParam, lParam);
    }

    /// <summary>
    /// Invokes every <see cref="KeyPressed" /> handler and returns <c>true</c>
    /// only if at least one handler returned <c>true</c>. Each handler is
    /// isolated so a single throwing subscriber cannot break the chain.
    /// </summary>
    private bool RaiseKeyPressedSafely(int vkCode)
    {
        Func<int, bool>? handler = KeyPressed;
        if (handler is null)
        {
            return false;
        }

        bool claimed = false;
        Delegate[] handlers = handler.GetInvocationList();
        foreach (Func<int, bool> single in handlers.Cast<Func<int, bool>>())
        {
            try
            {
                if (single(vkCode))
                {
                    claimed = true;
                }
            }
            catch (Exception exception)
            {
                Trace.TraceError($"KeyPressed handler failed: {exception}");
            }
        }

        return claimed;
    }

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
    private readonly struct KbdllHookStruct
    {
        public readonly uint VkCode;
        public readonly uint ScanCode;
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

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativePoint
    {
        public readonly int X;
        public readonly int Y;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint LowLevelKeyboardProc(int code, nint wParam, nint lParam);

    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    private static extern nint SetWindowsHookExW(
        int hookId,
        LowLevelKeyboardProc hookProc,
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
