using System.Threading.Channels;
using SelectionAssistant.Platform.Abstractions;

namespace SelectionAssistant.Platform.Windows.Capture;

/// <summary>
/// Tier 2/3 simulated-copy capture. The original clipboard is restored only
/// when the final sequence and owner still identify the injected source copy.
/// </summary>
public sealed class Win32ClipboardCapture : ISelectionTextCapture, IConfiguredClipboardCapture, IDisposable
{
    private static readonly SimulatedCopyChord[] DefaultChords =
    [
        SimulatedCopyChord.CtrlInsert,
        SimulatedCopyChord.CtrlC,
    ];

    private readonly IClipboardAccess _clipboard;
    private readonly ICopyInputInjector _input;
    private readonly ClipboardCaptureOptions _options;
    private readonly IReadOnlyList<SimulatedCopyChord> _chords;
    private readonly Action<string>? _diagnosticSink;
    private readonly SemaphoreSlim _captureGate = new(1, 1);
    private int _disposed;

    public Win32ClipboardCapture(
        IClipboardAccess clipboard,
        ICopyInputInjector input,
        ClipboardCaptureOptions? options = null,
        IReadOnlyList<SimulatedCopyChord>? chords = null,
        Action<string>? diagnosticSink = null)
    {
        _clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _options = (options ?? ClipboardCaptureOptions.Default).Validate();
        _chords = chords ?? DefaultChords;
        _diagnosticSink = diagnosticSink;

        if (_chords.Count == 0)
        {
            throw new ArgumentException("At least one simulated-copy chord is required.", nameof(chords));
        }
    }

    public async Task<CaptureResult> CaptureAsync(SelectionGesture gesture, CancellationToken token)
    {
        return await CaptureAsync(
            gesture,
            new ClipboardCaptureInvocation(_chords),
            token).ConfigureAwait(false);
    }

    public async Task<CaptureResult> CaptureAsync(
        SelectionGesture gesture,
        ClipboardCaptureInvocation invocation,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(gesture);
        ArgumentNullException.ThrowIfNull(invocation);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        invocation.Validate();
        ClipboardCaptureOptions effectiveOptions = invocation.StabilizationDelay is { } stabilization
            ? (_options with { StabilizationDelay = stabilization }).Validate()
            : _options;

        await _captureGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return await CaptureExclusiveAsync(
                gesture,
                invocation.Chords,
                invocation.AllowOwnerlessResult,
                effectiveOptions,
                token).ConfigureAwait(false);
        }
        finally
        {
            _captureGate.Release();
        }
    }

    private async Task<CaptureResult> CaptureExclusiveAsync(
        SelectionGesture gesture,
        IReadOnlyList<SimulatedCopyChord> chords,
        bool allowOwnerlessResult,
        ClipboardCaptureOptions options,
        CancellationToken callerToken)
    {
        callerToken.ThrowIfCancellationRequested();

        bool initialModifiers = _input.HasInterferingModifiers();
        bool initialTarget = _input.CanInjectInto(gesture);
        Trace($"clipboard start proc={gesture.SourceProcessId} chords={string.Join(',', chords)} modifiers={initialModifiers} target={initialTarget}");
        if (initialModifiers || !initialTarget)
        {
            Trace("clipboard skipped before backup");
            return NoCapture();
        }

        ClipboardSnapshot snapshot = _clipboard.Backup();
        Trace($"clipboard backup ok={snapshot.BackupSucceeded} empty={snapshot.WasEmpty} restorable={snapshot.HasRestorableData} seq={snapshot.SequenceNumber}");
        if (!snapshot.BackupSucceeded ||
            (!snapshot.WasEmpty && !snapshot.HasRestorableData))
        {
            // Safety wins over capture: never inject when existing clipboard
            // content cannot be restored at least in one supported format.
            return NoCapture();
        }

        var monitor = new ClipboardChangeMonitor(_clipboard);
        bool subscribed = false;
        IDisposable? scopedSubscription = null;
        bool inputCommitted = false;
        bool externalChangeObserved = false;
        uint lastBaseline = snapshot.SequenceNumber;
        uint? ownedSequence = null;

        using var overallCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        overallCancellation.CancelAfter(options.OverallTimeout);

        try
        {
            // Clipboard history keeps a long-lived listener on the shared
            // Win32Clipboard. Use a scoped lease when the implementation
            // supports it so this capture can observe the same WM_CLIPBOARDUPDATE
            // stream without replacing or colliding with history's callback.
            if (_clipboard is IScopedClipboardChangeAccess scopedClipboard)
            {
                scopedSubscription = scopedClipboard.SubscribeChangesScoped(monitor.Signal);
            }
            else
            {
                _clipboard.SubscribeChanges(monitor.Signal);
                subscribed = true;
            }

            foreach (SimulatedCopyChord chord in chords)
            {
                overallCancellation.Token.ThrowIfCancellationRequested();

                bool modifiers = _input.HasInterferingModifiers();
                bool target = _input.CanInjectInto(gesture);
                Trace($"clipboard chord={chord} modifiers={modifiers} target={target}");
                if (modifiers || !target)
                {
                    Trace("clipboard skipped before send");
                    return NoCapture();
                }

                lastBaseline = _clipboard.GetSequenceNumber();
                bool sent = _input.SendCopyChord(chord);
                Trace($"clipboard chord={chord} sent={sent} baseline={lastBaseline}");
                if (!sent)
                {
                    continue;
                }

                inputCommitted = true;
                uint? stableSequence = await monitor.WaitForStableChangeAsync(
                    lastBaseline,
                    options.ChangeTimeout,
                    options.StabilizationDelay,
                    overallCancellation.Token).ConfigureAwait(false);

                if (stableSequence is null)
                {
                    Trace($"clipboard chord={chord} no sequence change");
                    continue;
                }

                uint? ownerProcessId = _clipboard.GetOwnerProcessId();
                Trace($"clipboard chord={chord} stable={stableSequence.Value} owner={ownerProcessId?.ToString() ?? "none"}");
                bool ownerlessResult = ownerProcessId is null && allowOwnerlessResult;
                if (ownerProcessId is null && !ownerlessResult)
                {
                    // The source process may exit after placing data. Preserve
                    // the original clipboard, but do not report unowned text as
                    // a successful capture.
                    ownedSequence = stableSequence.Value;
                    return NoCapture();
                }

                if (ownerProcessId is not null && ownerProcessId != gesture.SourceProcessId)
                {
                    Trace($"clipboard rejected owner mismatch expected={gesture.SourceProcessId} actual={ownerProcessId}");
                    externalChangeObserved = true;
                    return NoCapture();
                }

                // Some GPU/WebView terminals (including Warp) place clipboard
                // data without an owner HWND, so Win32 cannot map it back to a
                // process. The caller must opt in per policy; sequence change
                // + target validation still protect the normal capture path.

                ownedSequence = stableSequence.Value;
                string? text = _clipboard.GetText();

                if (_clipboard.GetSequenceNumber() != ownedSequence.Value ||
                    (ownerlessResult
                        ? _clipboard.GetOwnerProcessId() is not null
                        : _clipboard.GetOwnerProcessId() != gesture.SourceProcessId))
                {
                    Trace("clipboard rejected because sequence/owner changed during read");
                    externalChangeObserved = true;
                    ownedSequence = null;
                    return NoCapture();
                }

                if (!string.IsNullOrWhiteSpace(text))
                {
                    Trace($"clipboard success source={chord} ownerless={ownerlessResult} length={text.Length}");
                    bool truncated = text.Length > options.MaxTextLength;
                    if (truncated)
                    {
                        text = text[..options.MaxTextLength];
                    }

                    CaptureSource source = chord switch
                    {
                        SimulatedCopyChord.CtrlInsert => CaptureSource.SimulatedCopyCtrlInsert,
                        SimulatedCopyChord.CtrlShiftC => CaptureSource.SimulatedCopyCtrlShiftC,
                        _ => CaptureSource.SimulatedCopyCtrlC,
                    };
                    return new CaptureResult(text, source, truncated);
                }
            }

            return NoCapture();
        }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
        {
            if (inputCommitted && !externalChangeObserved && ownedSequence is null)
            {
                ownedSequence = await TryObserveOwnedChangeAfterCancellationAsync(
                    monitor,
                    lastBaseline,
                    gesture.SourceProcessId,
                    options).ConfigureAwait(false);
            }

            throw;
        }
        catch (OperationCanceledException)
        {
            // Internal overall timeout. Cleanup still runs below.
            return NoCapture();
        }
        catch (Exception exception)
        {
            Trace($"clipboard failed type={exception.GetType().Name} message={exception.Message}");
            return NoCapture();
        }
        finally
        {
            if (subscribed)
            {
                try
                {
                    _clipboard.UnsubscribeChanges();
                }
                catch
                {
                    // Restoration below remains sequence guarded.
                }
            }

            try
            {
                scopedSubscription?.Dispose();
            }
            catch
            {
                // Restoration below remains sequence guarded.
            }

            if (!externalChangeObserved && ownedSequence is uint expectedSequence)
            {
                RestoreOriginalClipboard(snapshot, expectedSequence);
            }

            // P2 memory: release the ArrayPool-rented CF_DIB buffer now that
            // Restore (if it ran) has copied the bytes into a fresh HGLOBAL, or
            // the capture was abandoned (NoCapture). Without this the rented
            // buffer (up to MaxDibBytes = 32 MB) waits for GC, defeating the
            // whole point of pooling on NativeAOT's non-compacting LOH. Restore
            // completes synchronously above, so the buffer is no longer needed.
            snapshot.Dispose();
        }
    }

    private async Task<uint?> TryObserveOwnedChangeAfterCancellationAsync(
        ClipboardChangeMonitor monitor,
        uint baseline,
        uint sourceProcessId,
        ClipboardCaptureOptions options)
    {
        using var cleanupCancellation =
            new CancellationTokenSource(options.CancellationCleanupTimeout);

        try
        {
            uint? sequence = await monitor.WaitForStableChangeAsync(
                baseline,
                options.CancellationCleanupTimeout,
                options.StabilizationDelay,
                cleanupCancellation.Token).ConfigureAwait(false);

            uint? ownerProcessId = _clipboard.GetOwnerProcessId();
            return sequence is not null &&
                   (ownerProcessId is null || ownerProcessId == sourceProcessId)
                ? sequence
                : null;
        }
        catch
        {
            return null;
        }
    }

    private void RestoreOriginalClipboard(ClipboardSnapshot snapshot, uint expectedSequence)
    {
        try
        {
            if (snapshot.HasRestorableData)
            {
                _clipboard.Restore(snapshot, expectedSequence);
            }
            else if (snapshot.WasEmpty)
            {
                _clipboard.Clear(expectedSequence);
            }
        }
        catch
        {
            // Best effort. Every native mutation remains sequence guarded.
        }
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
            // Diagnostics must never affect capture or clipboard restoration.
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // Clipboard cleanup after cancellation is intentionally non-cancellable.
        // Wait for that bounded cleanup before the owning Win32Clipboard is torn down.
        TimeSpan shutdownWait = _options.OverallTimeout + _options.CancellationCleanupTimeout;
        if (_captureGate.Wait(shutdownWait))
        {
            _captureGate.Release();
        }

        GC.SuppressFinalize(this);
    }

    private sealed class ClipboardChangeMonitor
    {
        private readonly IClipboardAccess _clipboard;
        private readonly Channel<byte> _notifications = Channel.CreateUnbounded<byte>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
            });

        public ClipboardChangeMonitor(IClipboardAccess clipboard)
        {
            _clipboard = clipboard;
        }

        public void Signal() => _notifications.Writer.TryWrite(0);

        public async Task<uint?> WaitForStableChangeAsync(
            uint baseline,
            TimeSpan changeTimeout,
            TimeSpan stabilizationDelay,
            CancellationToken token)
        {
            DrainNotifications();
            uint current = _clipboard.GetSequenceNumber();

            if (current == baseline)
            {
                using var firstChangeCancellation =
                    CancellationTokenSource.CreateLinkedTokenSource(token);
                firstChangeCancellation.CancelAfter(changeTimeout);

                try
                {
                    while (current == baseline)
                    {
                        await _notifications.Reader.ReadAsync(firstChangeCancellation.Token)
                            .ConfigureAwait(false);
                        DrainNotifications();
                        current = _clipboard.GetSequenceNumber();
                    }
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    return null;
                }
            }

            while (true)
            {
                Task delay = Task.Delay(stabilizationDelay, token);
                Task<bool> notification = _notifications.Reader
                    .WaitToReadAsync(token)
                    .AsTask();

                await Task.WhenAny(delay, notification).ConfigureAwait(false);
                if (notification.IsCompleted)
                {
                    if (!await notification.ConfigureAwait(false))
                    {
                        return _clipboard.GetSequenceNumber();
                    }

                    DrainNotifications();
                    continue;
                }

                await delay.ConfigureAwait(false);
                if (_notifications.Reader.TryRead(out _))
                {
                    DrainNotifications();
                    continue;
                }

                return _clipboard.GetSequenceNumber();
            }
        }

        private void DrainNotifications()
        {
            while (_notifications.Reader.TryRead(out _))
            {
            }
        }
    }
}
