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

    /// <summary>
    /// R41: 可选的"吞键决策"回调。在鼠标事件派发前调用：若任一订阅者返回
    /// true，钩子回调返回 1（吞掉该事件，不传给源应用）。用于 Ocean Eyes
    /// 工具栏可见时拦截右键触发"重画"而非让源应用弹右键菜单。订阅者必须
    /// 快速返回（在 hook 线程同步执行），不得阻塞。
    /// </summary>
    event Func<MouseEventData, bool>? SwallowCheck;

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
