using System.Runtime.InteropServices;
using SelectionAssistant.Platform.Abstractions;

namespace SelectionAssistant.Platform.Windows.Capture;

/// <summary>Sends one complete copy chord in a single SendInput call.</summary>
public sealed unsafe partial class SendInputHelper : ICopyInputInjector
{
    private const uint InputKeyboard = 1;
    private const uint KeyEventKeyUp = 0x0002;
    private const ushort VkShift = 0x10;
    private const ushort VkControl = 0x11;
    private const ushort VkMenu = 0x12;
    private const ushort VkInsert = 0x2D;
    private const ushort VkC = 0x43;
    private const ushort VkV = 0x56;
    private const ushort VkLeftWindows = 0x5B;
    private const ushort VkRightWindows = 0x5C;
    private const uint GaRoot = 2;
    private const nuint InputMarker = 0x53454C41;

    public bool HasInterferingModifiers() =>
        IsPressed(VkShift) ||
        IsPressed(VkControl) ||
        IsPressed(VkMenu) ||
        IsPressed(VkLeftWindows) ||
        IsPressed(VkRightWindows);

    public bool CanInjectInto(SelectionGesture gesture)
    {
        ArgumentNullException.ThrowIfNull(gesture);

        nint foreground = GetForegroundWindow();
        if (foreground == 0)
        {
            return false;
        }

        nint root = GetAncestor(foreground, GaRoot);
        if (root == 0)
        {
            root = foreground;
        }

        GetWindowThreadProcessId(root, out uint processId);
        return processId == gesture.SourceProcessId && root == gesture.SourceRootHwnd;
    }

    public bool SendCopyChord(SimulatedCopyChord chord)
    {
        ushort key = chord switch
        {
            SimulatedCopyChord.CtrlInsert => VkInsert,
            SimulatedCopyChord.CtrlC => VkC,
            SimulatedCopyChord.CtrlShiftC => VkC,
            _ => throw new ArgumentOutOfRangeException(nameof(chord)),
        };

        if (chord == SimulatedCopyChord.CtrlShiftC)
        {
            NativeInput* shiftedInputs = stackalloc NativeInput[6]
            {
                Keyboard(VkControl, 0),
                Keyboard(VkShift, 0),
                Keyboard(key, 0),
                Keyboard(key, KeyEventKeyUp),
                Keyboard(VkShift, KeyEventKeyUp),
                Keyboard(VkControl, KeyEventKeyUp),
            };

            return SendInput(6, shiftedInputs, sizeof(NativeInput)) == 6;
        }

        NativeInput* inputs = stackalloc NativeInput[4]
        {
            Keyboard(VkControl, 0),
            Keyboard(key, 0),
            Keyboard(key, KeyEventKeyUp),
            Keyboard(VkControl, KeyEventKeyUp),
        };

        return SendInput(4, inputs, sizeof(NativeInput)) == 4;
    }

    /// <summary>
    /// Sends a Ctrl+V paste chord to the current foreground window. Used by the
    /// toolbar "粘贴" button to replace the selected text in the source app with
    /// the clipboard contents. Returns true if all 4 input events were accepted.
    /// </summary>
    public bool SendPasteChord()
    {
        NativeInput* inputs = stackalloc NativeInput[4]
        {
            Keyboard(VkControl, 0),
            Keyboard(VkV, 0),
            Keyboard(VkV, KeyEventKeyUp),
            Keyboard(VkControl, KeyEventKeyUp),
        };

        return SendInput(4, inputs, sizeof(NativeInput)) == 4;
    }

    private static bool IsPressed(ushort virtualKey) =>
        (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    private static NativeInput Keyboard(ushort virtualKey, uint flags) => new()
    {
        Type = InputKeyboard,
        Data = new InputUnion
        {
            Keyboard = new KeyboardInput
            {
                VirtualKey = virtualKey,
                Flags = flags,
                ExtraInfo = InputMarker,
            },
        },
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeInput
    {
        public uint Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public MouseInput Mouse;

        [FieldOffset(0)]
        public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int virtualKey);

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial nint GetAncestor(nint window, uint flags);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint window, out uint processId);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial uint SendInput(uint inputCount, NativeInput* inputs, int inputSize);
}
