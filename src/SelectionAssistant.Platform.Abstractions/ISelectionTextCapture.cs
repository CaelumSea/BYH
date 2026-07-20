namespace SelectionAssistant.Platform.Abstractions;

/// <summary>
/// 文本取词抽象(v4 §6 四级降级链)。
/// 实现按顺序尝试: UIA → Ctrl+Insert → Ctrl+C → 手动 fallback。
/// </summary>
public interface ISelectionTextCapture
{
    /// <summary>
    /// Phase-1 取词：UIA → 剪贴板。快路径（~100-400ms），有文本就立即返回。
    /// 视觉 OCR（R24 Tier 4）不在这里——它在 <see cref="CaptureVisionAsync" />
    /// 里，由会话管理器在 phase 1 空结果后才调用。
    /// </summary>
    Task<CaptureResult> CaptureAsync(SelectionGesture gesture, CancellationToken token);

    /// <summary>
    /// R24 track B: whether the vision OCR tier is enabled and wired. The session
    /// manager checks this (cheap, synchronous) BEFORE showing a "识别中…"
    /// toolbar, so disabled/no-vision setups never flash the toolbar. Default
    /// false; <c>WindowsSelectionTextCapture</c> overrides when configured.
    /// </summary>
    bool VisionTierAvailable => false;

    /// <summary>
    /// R24 track B Phase-2 取词：视觉 OCR（截图 → 云端 OCR）。慢路径（1-3s），
    /// 只在 <see cref="CaptureAsync" /> 返回空文本后才由会话管理器调用。默认实现
    /// 返回 null（不支持视觉），由 <c>WindowsSelectionTextCapture</c> 重写。
    /// 返回 null 表示禁用/未接线/无文本。
    /// </summary>
    Task<CaptureResult?> CaptureVisionAsync(SelectionGesture gesture, CancellationToken token)
        => Task.FromResult<CaptureResult?>(null);
}

/// <summary>取词结果。</summary>
public sealed record CaptureResult(
    string? Text,                    // 取到的文本,null/空表示失败
    CaptureSource Source,            // 来自哪一级
    bool IsAmbiguous);               // 文本可能不完整/不可信(如 UIA 返回整个控件)

public enum CaptureSource
{
    None,
    Accessibility,                   // Tier 1: UIA / AX
    SimulatedCopyCtrlInsert,         // Tier 2
    SimulatedCopyCtrlC,              // Tier 3
    Vision,                          // R24 Tier 4: 截图 → 云端 OCR 专项模型
    ManualFallback,                  // Tier 5: UIA+剪贴板都失败且未开启视觉识别
}

/// <summary>
/// 选词手势数据(从鼠标钩子判定得来,传给会话管理器和取词服务)。
/// </summary>
public sealed record SelectionGesture(
    int MouseUpX,
    int MouseUpY,
    int MouseDownX,
    int MouseDownY,
    long MouseDownTimestampMs,
    long MouseUpTimestampMs,
    nint SourceRootHwnd,             // 源应用根窗口(用于 UIA 缓存 + 双击同窗口判定)
    uint SourceProcessId);
