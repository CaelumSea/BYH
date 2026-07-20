namespace SelectionAssistant.Platform.Abstractions;

/// <summary>
/// 平台无关的鼠标钩子抽象。
/// Windows 实现: WH_MOUSE_LL (LowLevelMouseHook)
/// macOS 实现: CGEventTap (P1 macOS 阶段)
/// </summary>
public interface IMouseHook : IDisposable
{
    /// <summary>鼠标事件数据,在钩子线程上触发。调用方必须自行切到 UI 线程。</summary>
    event Action<MouseEventData>? MouseEvent;

    void Start();
}

/// <summary>鼠标事件数据(平台无关)。</summary>
public sealed record MouseEventData(
    int X,
    int Y,
    MouseMessageType Message,
    long TimestampMs,         // 单调时钟(Environment.TickCount64),非墙上时钟
    bool IsInjected,          // LLMHF_INJECTED (v4 §5.4)
    nuint ExtraInfo);         // dwExtraInfo,用于识别本应用自己注入的事件

public enum MouseMessageType
{
    LeftButtonDown = 0x0201,   // WM_LBUTTONDOWN
    LeftButtonUp = 0x0202,     // WM_LBUTTONUP
    RightButtonDown = 0x0204,  // WM_RBUTTONDOWN
    RightButtonUp = 0x0205,    // WM_RBUTTONUP
}
