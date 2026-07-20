namespace SelectionAssistant.Core.Capture;

/// <summary>
/// 进程取词策略(v4 §6.6 —— 可组合 record,非 enum)。
/// v3 的 enum 互斥,但 PDF 阅读器需要 CopyAllowed + DelayedClipboardRead 两者;
/// 终端需要 CopyWithCtrlInsertOnly + 自定义稳定时长。record 让这些可组合。
/// </summary>
public sealed record ProcessCapturePolicy(
    bool DetectionEnabled,
    bool AccessibilityEnabled,
    SimulatedCopyMode CopyMode,
    int ClipboardStabilizationMs,
    bool ManualFallbackEnabled)
{
    /// <summary>默认策略:全部启用,标准稳定时长。</summary>
    public static ProcessCapturePolicy Default { get; } = new(
        DetectionEnabled: true,
        AccessibilityEnabled: true,
        CopyMode: SimulatedCopyMode.CtrlInsertThenCtrlC,
        ClipboardStabilizationMs: 0,   // 0 = 用全局默认(50ms)
        ManualFallbackEnabled: true);

    public ProcessCapturePolicy Validate()
    {
        if (ClipboardStabilizationMs is < 0 or > 5_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ClipboardStabilizationMs),
                "Clipboard stabilization must be between 0 and 5000 ms.");
        }

        return this;
    }
}

/// <summary>模拟复制的模式(v4 §6.6)。</summary>
public enum SimulatedCopyMode
{
    /// <summary>不模拟复制(只靠 UIA 或手动)。</summary>
    None,
    /// <summary>仅 Ctrl+Insert(终端友好,不会中断进程)。</summary>
    CtrlInsertOnly,
    /// <summary>先 Ctrl+Insert 再 Ctrl+C(最大兼容性)。</summary>
    CtrlInsertThenCtrlC,
}
