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
    private readonly IConfiguredClipboardCapture _clipboardCapture;
    private readonly IDisposable[] _ownedDependencies;
    private int _disposed;

    // R24 track B: the optional screenshot→OCR tier (Tier 4). Null when vision
    // capture is disabled (setting off, or no OCR client wired). Injected by the
    // App composition root after construction so this layer stays free of the
    // Providers project.
    private VisionTextCapture? _visionCapture;
    private volatile bool _visionEnabled;

    public WindowsSelectionTextCapture()
        : this(WindowsDefaultCapturePolicies.CreateProvider())
    {
    }

    public WindowsSelectionTextCapture(IProcessCapturePolicyProvider policyProvider)
    {
        _policyProvider = policyProvider ?? throw new ArgumentNullException(nameof(policyProvider));
        var accessibility = new UIAutomationTextCapture();
        var clipboard = new Win32Clipboard();
        var clipboardCapture = new Win32ClipboardCapture(clipboard, new SendInputHelper());

        _accessibilityCapture = accessibility;
        _clipboardCapture = clipboardCapture;
        _ownedDependencies = [clipboardCapture, clipboard, accessibility];
    }

    public WindowsSelectionTextCapture(
        IProcessCapturePolicyProvider policyProvider,
        ISelectionTextCapture accessibilityCapture,
        IConfiguredClipboardCapture clipboardCapture)
    {
        _policyProvider = policyProvider ?? throw new ArgumentNullException(nameof(policyProvider));
        _accessibilityCapture = accessibilityCapture ?? throw new ArgumentNullException(nameof(accessibilityCapture));
        _clipboardCapture = clipboardCapture ?? throw new ArgumentNullException(nameof(clipboardCapture));
        _ownedDependencies = [];
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
                return accessibility;
            }
        }

        IReadOnlyList<SimulatedCopyChord>? chords = policy.CopyMode switch
        {
            SimulatedCopyMode.CtrlInsertOnly => CtrlInsertOnly,
            SimulatedCopyMode.CtrlInsertThenCtrlC => CtrlInsertThenCtrlC,
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
                    new ClipboardCaptureInvocation(chords, stabilization),
                    token)
                .ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(clipboard.Text))
            {
                return clipboard;
            }
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

        foreach (IDisposable dependency in _ownedDependencies)
        {
            dependency.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    private static CaptureResult NoCapture() =>
        new(null, CaptureSource.None, false);
}
