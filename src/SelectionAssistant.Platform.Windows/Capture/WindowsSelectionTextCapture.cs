using SelectionAssistant.Core.Capture;
using SelectionAssistant.Platform.Abstractions;
using SelectionAssistant.Platform.Windows.Clipboard;

namespace SelectionAssistant.Platform.Windows.Capture;

/// <summary>Windows Tier 1 → Tier 2/3 text-capture chain.</summary>
public sealed class WindowsSelectionTextCapture : ISelectionTextCapture, IDisposable
{
    private static readonly SimulatedCopyChord[] CtrlInsertOnly =
    [
        SimulatedCopyChord.CtrlInsert,
    ];

    private static readonly SimulatedCopyChord[] CtrlInsertThenCtrlC =
    [
        SimulatedCopyChord.CtrlInsert,
        SimulatedCopyChord.CtrlC,
    ];

    private static readonly SimulatedCopyChord[] CtrlShiftCOnly =
    [
        SimulatedCopyChord.CtrlShiftC,
    ];

    private readonly IProcessCapturePolicyProvider _policyProvider;
    private readonly ISelectionTextCapture _accessibilityCapture;
    // Reassignable via SetSharedClipboard: when the process-wide Win32Clipboard
    // is injected, this wrapper is rebuilt around it WITHOUT taking ownership
    // (the shared instance is owned by ClipboardHistoryService). Guarded by
    // _captureRebuildGate so the swap can't race an in-flight CaptureAsync.
    private IConfiguredClipboardCapture _clipboardCapture;
    private readonly IDisposable[] _ownedDependencies;
    private readonly Action<string>? _diagnosticSink;
    private readonly object _captureRebuildGate = new();
    // The self-owned Win32Clipboard created in the 2-arg ctor, disposed when
    // SetSharedClipboard swaps in the shared instance and on Dispose. Null once
    // ownership moves to the shared instance. Win32ClipboardCapture itself does
    // not dispose its injected clipboard, so this layer must.
    private Win32Clipboard? _ownedClipboard;
    private int _disposed;

    // R24 track B: the optional screenshot→OCR tier (Tier 4). Null when vision
    // capture is disabled (setting off, or no OCR client wired). Injected by the
    // App composition root after construction so this layer stays free of the
    // Providers project.
    private VisionTextCapture? _visionCapture;
    private volatile bool _visionEnabled;
    // 剪贴板历史抑制回调，透传给内部 Win32ClipboardCapture。非 readonly：由
    // SetHistoryChangeSuppressor 在 App 组合期设置，并在 rebuild 时复用到新 wrapper。
    private Action<int>? _historyChangeSuppressor;

    public WindowsSelectionTextCapture()
        : this(WindowsDefaultCapturePolicies.CreateProvider())
    {
    }

    public WindowsSelectionTextCapture(
        IProcessCapturePolicyProvider policyProvider,
        Action<string>? diagnosticSink = null)
    {
        _policyProvider = policyProvider ?? throw new ArgumentNullException(nameof(policyProvider));
        _diagnosticSink = diagnosticSink;
        var accessibility = new UIAutomationTextCapture();
        var clipboard = new Win32Clipboard();
        var clipboardCapture = new Win32ClipboardCapture(
            clipboard,
            new SendInputHelper(),
            diagnosticSink: diagnosticSink);

        _accessibilityCapture = accessibility;
        _clipboardCapture = clipboardCapture;
        _ownedDependencies = [clipboardCapture, clipboard, accessibility];
        _ownedClipboard = clipboard;
    }

    public WindowsSelectionTextCapture(
        IProcessCapturePolicyProvider policyProvider,
        ISelectionTextCapture accessibilityCapture,
        IConfiguredClipboardCapture clipboardCapture,
        Action<string>? diagnosticSink = null)
    {
        _policyProvider = policyProvider ?? throw new ArgumentNullException(nameof(policyProvider));
        _accessibilityCapture = accessibilityCapture ?? throw new ArgumentNullException(nameof(accessibilityCapture));
        _clipboardCapture = clipboardCapture ?? throw new ArgumentNullException(nameof(clipboardCapture));
        _diagnosticSink = diagnosticSink;
        _ownedDependencies = [];
    }

    /// <summary>
    /// Rebuilds the internal <see cref="Win32ClipboardCapture"/> around the
    /// process-wide shared <see cref="Win32Clipboard"/> owned by
    /// <c>ClipboardHistoryService</c>. Each <see cref="Win32Clipboard"/> runs its
    /// own message thread + clipboard-format-listener window, so a second
    /// instance here raced the history service's listener on every clipboard
    /// change (Ctrl+C send + concurrent WM_CLIPBOARDUPDATE backup read) — the
    /// same dispose/callback stack corruption that caused the 0xc0000409 crashes
    /// on the screenshot-save path. Collapsing to one instance removes the race.
    /// The previously self-owned wrapper and its private clipboard are disposed;
    /// the shared instance is NOT owned here and will not be disposed by this
    /// capture's <see cref="Dispose"/>. Idempotent; safe to call again with a
    /// different shared instance. Call once during startup before any capture.
    /// </summary>
    public void SetSharedClipboard(Win32Clipboard clipboard)
    {
        ArgumentNullException.ThrowIfNull(clipboard);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        // Swap under the rebuild gate so an in-flight CaptureAsync on the UI/STA
        // thread can't dereference a wrapper mid-swap. CaptureAsync itself does
        // not hold this gate (it would serialize captures); it reads
        // _clipboardCapture once into a local at entry, so a swap only takes
        // effect on the next capture.
        lock (_captureRebuildGate)
        {
            var rebuilt = new Win32ClipboardCapture(
                clipboard,
                new SendInputHelper(),
                diagnosticSink: _diagnosticSink);
            // 把已接线的 suppressor 复用到新 wrapper（rebuild 后旧 wrapper 被丢弃，
            // suppressor 必须重连，否则 rebuild 后取词注入会再次污染历史）。
            rebuilt.SetHistoryChangeSuppressor(_historyChangeSuppressor);

            IConfiguredClipboardCapture oldWrapper = _clipboardCapture;
            Win32Clipboard? oldClipboard = _ownedClipboard;
            _clipboardCapture = rebuilt;
            _ownedClipboard = null;

            // Dispose the OLD self-owned wrapper + its private clipboard ONLY.
            // The injected shared instance is owned by ClipboardHistoryService
            // and must NOT be disposed here. Win32ClipboardCapture.Dispose does
            // not touch its injected clipboard, so the old private clipboard is
            // torn down explicitly to release its message thread + listener
            // window (otherwise a second WM_CLIPBOARDUPDATE listener would keep
            // running, defeating the whole point of the swap).
            if (oldWrapper is IDisposable oldDisposable)
            {
                oldDisposable.Dispose();
            }
            oldClipboard?.Dispose();
        }
    }

    /// <summary>
    /// 注入剪贴板历史变更抑制回调，透传给当前活跃的 Win32ClipboardCapture。每次注入
    /// 复制前会调用 <paramref name="suppress"/> 传 2，让 ClipboardHistoryService 忽略
    /// 接下来 2 次 WM_CLIPBOARDUPDATE（注入复制 + restore backup）。传 null 取消接线。
    /// 后续 <see cref="SetSharedClipboard"/> rebuild 时会把同一回调复用到新 wrapper。
    /// </summary>
    public void SetHistoryChangeSuppressor(Action<int>? suppress)
    {
        _historyChangeSuppressor = suppress;
        if (_clipboardCapture is Win32ClipboardCapture win32Capture)
        {
            win32Capture.SetHistoryChangeSuppressor(suppress);
        }
    }

    /// <summary>
    /// R24 track B: enables/disables the vision OCR tier at runtime. When the
    /// vision capture is disabled, UIA + clipboard only (pre-R24 behaviour).
    /// Thread-safe; reads via <see cref="_visionEnabled" />.
    /// </summary>
    public void SetVisionEnabled(bool enabled) => _visionEnabled = enabled;

    /// <summary>
    /// R24 track B: true only when the vision tier is both enabled and wired
    /// (a capture + OCR client injected). The session manager reads this before
    /// showing a "识别中…" toolbar so disabled setups never flash it.
    /// </summary>
    public bool VisionTierAvailable => _visionEnabled && _visionCapture is not null;

    /// <summary>
    /// R24 track B: injects the screenshot→OCR tier. The capture must also be
    /// enabled via <see cref="SetVisionEnabled" /> for it to fire.
    /// </summary>
    public void SetVisionCapture(VisionTextCapture? visionCapture) => _visionCapture = visionCapture;

    public ProcessCapturePolicy ResolvePolicy(uint processId) =>
        _policyProvider.Resolve(processId);

    public async Task<CaptureResult> CaptureAsync(
        SelectionGesture gesture,
        CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        ProcessCapturePolicy policy = ResolvePolicy(gesture.SourceProcessId);
        Trace($"policy proc={gesture.SourceProcessId} mode={policy.CopyMode} accessibility={policy.AccessibilityEnabled} detection={policy.DetectionEnabled}");
        if (!policy.DetectionEnabled)
        {
            return NoCapture();
        }

        CaptureResult accessibility = NoCapture();
        if (policy.AccessibilityEnabled)
        {
            accessibility = await _accessibilityCapture
                .CaptureAsync(gesture, token)
                .ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(accessibility.Text) && !accessibility.IsAmbiguous)
            {
                Trace($"accessibility success length={accessibility.Text.Length}");
                return accessibility;
            }
            Trace($"accessibility empty ambiguous={accessibility.IsAmbiguous}");
        }

        IReadOnlyList<SimulatedCopyChord>? chords = policy.CopyMode switch
        {
            SimulatedCopyMode.CtrlInsertOnly => CtrlInsertOnly,
            SimulatedCopyMode.CtrlInsertThenCtrlC => CtrlInsertThenCtrlC,
            SimulatedCopyMode.CtrlCOnly => [SimulatedCopyChord.CtrlC],
            SimulatedCopyMode.CtrlShiftCOnly => CtrlShiftCOnly,
            _ => null,
        };

        if (chords is not null)
        {
            TimeSpan? stabilization = policy.ClipboardStabilizationMs > 0
                ? TimeSpan.FromMilliseconds(policy.ClipboardStabilizationMs)
                : null;
            CaptureResult clipboard = await _clipboardCapture
                .CaptureAsync(
                    gesture,
                    new ClipboardCaptureInvocation(
                        chords,
                        stabilization,
                        AllowOwnerlessResult: policy.CopyMode == SimulatedCopyMode.CtrlShiftCOnly,
                        PreserveCapturedClipboard: policy.PreserveCapturedClipboard),
                    token)
                .ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(clipboard.Text))
            {
                Trace($"clipboard result source={clipboard.Source} length={clipboard.Text.Length}");
                return clipboard;
            }
            Trace("clipboard result empty");
        }

        // Never return ambiguous accessibility text as a successful selection.
        return policy.ManualFallbackEnabled
            ? new CaptureResult(null, CaptureSource.ManualFallback, false)
            : NoCapture();
    }

    /// <summary>
    /// R24 track B Tier 4 (phase 2): the slow vision OCR path. Only invoked by
    /// the session manager AFTER <see cref="CaptureAsync" /> (UIA + clipboard)
    /// comes back empty, so it never runs on the fast path. Returns null when
    /// vision is disabled, unwired, or yields no text. Runs under its own 5s
    /// timeout (independent of the UIA worker's 400ms) so a stalled OCR service
    /// can't hang the session.
    /// </summary>
    public async Task<CaptureResult?> CaptureVisionAsync(
        SelectionGesture gesture,
        CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        if (!_visionEnabled || _visionCapture is null)
        {
            return null;
        }

        // Independent timeout: vision OCR is allowed 5s, separate from the UIA
        // worker's 400ms cap. The provider's own timeout (default 60s) is the
        // outer bound; this is the user-facing "give up and show nothing" cap.
        using var visionTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        visionTimeout.CancelAfter(TimeSpan.FromSeconds(5));

        try
        {
            return await _visionCapture
                .CaptureAsync(gesture, visionTimeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            // Vision timed out (5s) — non-fatal; phase 1 already returned empty,
            // so the session shows no toolbar (R20 guard).
            return null;
        }
        catch
        {
            // Any OCR failure is non-fatal.
            return null;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // _ownedDependencies only ever holds the 2-arg-ctor objects (the
        // self-owned wrapper + its private clipboard + accessibility capture).
        // After SetSharedClipboard swaps in the process-wide clipboard, that
        // private clipboard has already been disposed there and the shared
        // instance is NEVER added here — so iterating is safe: at worst it
        // re-disposes already-disposed (idempotent) objects. The shared
        // Win32Clipboard remains owned by ClipboardHistoryService.
        foreach (IDisposable dependency in _ownedDependencies)
        {
            dependency.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    private static CaptureResult NoCapture() =>
        new(null, CaptureSource.None, false);

    private void Trace(string message)
    {
        try
        {
            _diagnosticSink?.Invoke(message);
        }
        catch
        {
            // Diagnostics must never affect capture.
        }
    }
}
