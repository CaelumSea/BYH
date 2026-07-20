using System.Runtime.InteropServices;
using SelectionAssistant.Platform.Abstractions;

namespace SelectionAssistant.Platform.Windows;

/// <summary>Win32-backed gesture thresholds.</summary>
public sealed class WindowsSystemMetrics : ISystemMetrics
{
    private const int SmCxDoubleClk = 36;
    private const int SmCyDoubleClk = 37;
    private const int SmCxDrag = 68;
    private const int SmCyDrag = 69;

    public int DragThresholdX => AtLeastOne(GetSystemMetrics(SmCxDrag));

    public int DragThresholdY => AtLeastOne(GetSystemMetrics(SmCyDrag));

    public int DoubleClickTimeMs => checked((int)GetDoubleClickTime());

    public int DoubleClickWidth => AtLeastOne(GetSystemMetrics(SmCxDoubleClk));

    public int DoubleClickHeight => AtLeastOne(GetSystemMetrics(SmCyDoubleClk));

    private static int AtLeastOne(int value) => Math.Max(1, value);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();
}
