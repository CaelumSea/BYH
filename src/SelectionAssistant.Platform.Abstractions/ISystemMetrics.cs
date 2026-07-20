namespace SelectionAssistant.Platform.Abstractions;

/// <summary>
/// 系统指标的跨平台抽象(v4 §5.2)。
/// Windows: GetSystemMetrics(SM_CXDRAG/SM_CYDRAG/SM_CXDOUBLECLK/SM_CYDOUBLECLK) + GetDoubleClickTime
/// macOS: NSEvent.doubleClickInterval; 拖拽阈值用可配置默认值(macOS 无系统指标)
/// </summary>
public interface ISystemMetrics
{
    /// <summary>水平拖拽阈值(超过此距离视为拖拽,非点击)。Windows: SM_CXDRAG</summary>
    int DragThresholdX { get; }

    /// <summary>垂直拖拽阈值。Windows: SM_CYDRAG</summary>
    int DragThresholdY { get; }

    /// <summary>双击时间窗口(毫秒)。Windows: GetDoubleClickTime</summary>
    int DoubleClickTimeMs { get; }

    /// <summary>双击矩形宽度。Windows: SM_CXDOUBLECLK</summary>
    int DoubleClickWidth { get; }

    /// <summary>双击矩形高度。Windows: SM_CYDOUBLECLK</summary>
    int DoubleClickHeight { get; }
}
