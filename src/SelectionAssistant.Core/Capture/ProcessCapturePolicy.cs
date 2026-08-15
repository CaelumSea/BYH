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
    /// <summary>
    /// Keeps a successfully captured selection in the system clipboard instead
    /// of restoring the user's previous clipboard value. This is opt-in for
    /// applications whose selection workflow is itself a copy operation (for
    /// example Warp and the WeChat public-account surface).
    /// </summary>
    public bool PreserveCapturedClipboard { get; init; }

    /// <summary>
    /// Number of clipboard-history notifications reserved while a simulated
    /// copy is in flight. GPU/WebView terminals can publish one logical copy
    /// in several clipboard transactions; the default covers normal targets,
    /// while a process-specific policy may reserve more.
    /// </summary>
    public int HistorySuppressionCount { get; init; } = 2;

    /// <summary>
    /// Accepts a clipboard write that has no owner HWND as a capture result.
    /// Some GPU-rendered apps (Zed's GPUI, Warp) publish their copy without
    /// calling SetClipboardData with an owner window, so Win32 cannot map the
    /// write back to a process. Opt-in per process rule; the ownerless result
    /// still goes through the sequence-stability and target checks.
    /// </summary>
    public bool AllowOwnerlessClipboardResult { get; init; }

    /// <summary>默认策略:全部启用,标准稳定时长。</summary>
    public static ProcessCapturePolicy Default { get; } = new(
        DetectionEnabled: true,
        AccessibilityEnabled: true,
        CopyMode: SimulatedCopyMode.CtrlInsertThenCtrlC,
        ClipboardStabilizationMs: 0,   // 0 = 用全局默认(50ms)
        ManualFallbackEnabled: true);

    /// <summary>
    /// Returns a copy with <see cref="ClipboardStabilizationMs"/> clamped to the
    /// valid range. Mirrors the Normalize convention used by the other settings
    /// records (clamp, then Validate as a hard assertion). A deserialized value
    /// outside [0, 5000] is silently clamped instead of throwing. Audit M7.
    /// </summary>
    public ProcessCapturePolicy Normalize() => this with
    {
        ClipboardStabilizationMs = Math.Clamp(ClipboardStabilizationMs, 0, 5_000),
        HistorySuppressionCount = Math.Clamp(HistorySuppressionCount, 0, 8),
    };

    public ProcessCapturePolicy Validate()
    {
        if (ClipboardStabilizationMs is < 0 or > 5_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ClipboardStabilizationMs),
                "Clipboard stabilization must be between 0 and 5000 ms.");
        }

        if (HistorySuppressionCount is < 0 or > 8)
        {
            throw new ArgumentOutOfRangeException(
                nameof(HistorySuppressionCount),
                "History suppression count must be between 0 and 8.");
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
    /// <summary>仅 Ctrl+C（WebView/公众号等不响应 Ctrl+Insert 的应用）。</summary>
    CtrlCOnly,
    /// <summary>仅 Ctrl+Shift+C（Warp 等终端的复制快捷键）。</summary>
    CtrlShiftCOnly,
}
