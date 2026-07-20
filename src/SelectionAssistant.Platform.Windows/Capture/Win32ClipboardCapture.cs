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
    private readonly SemaphoreSlim _captureGate = new(1, 1);
    private int _disposed;

    public Win32ClipboardCapture(
        IClipboardAccess clipboard,
        ICopyInputInjector input,
        ClipboardCaptureOptions? options = null,
        IReadOnlyList<SimulatedCopyChord>? chords = null)
    {
        _clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _options = (options ?? ClipboardCaptureOptions.Default).Validate();
        _chords = chords ?? DefaultChords;

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
        ClipboardCaptureOptions options,
        CancellationToken callerToken)
    {
        callerToken.ThrowIfCancellationRequested();

        if (_input.HasInterferingModifiers() || !_input.CanInjectInto(gesture))
        {
            return NoCapture();
        }

        ClipboardSnapshot snapshot = _clipboard.Backup();
        if (!snapshot.BackupSucceeded ||
            (!snapshot.WasEmpty && !snapshot.HasRestorableData))
        {
            // Safety wins over capture: never inject when existing clipboard
            // content cannot be restored at least in one supported format.
            return NoCapture();
        }

        var monitor = new ClipboardChangeMonitor(_clipboard);
        bool subscribed = false;
        bool inputCommitted = false;
        bool externalChangeObserved = false;
        uint lastBaseline = snapshot.SequenceNumber;
        uint? ownedSequence = null;

        using var overallCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        overallCancellation.CancelAfter(options.OverallTimeout);

        try
        {
            _clipboard.SubscribeChanges(monitor.Signal);
            subscribed = true;

            foreach (SimulatedCopyChord chord in chords)
            {
                overallCancellation.Token.ThrowIfCancellationRequested();

                if (_input.HasInterferingModifiers() || !_input.CanInjectInto(gesture))
                {
                    return NoCapture();
                }

                lastBaseline = _clipboard.GetSequenceNumber();
                if (!_input.SendCopyChord(chord))
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
                    continue;
                }

                uint? ownerProcessId = _clipboard.GetOwnerProcessId();
                if (ownerProcessId is null)
                {
                    // The source process may exit after placing data. Preserve
                    // the original clipboard, but do not report unowned text as
                    // a successful capture.
                    ownedSequence = stableSequence.Value;
                    return NoCapture();
                }

                if (ownerProcessId != gesture.SourceProcessId)
                {
                    externalChangeObserved = true;
                    return NoCapture();
                }

                ownedSequence = stableSequence.Value;
                string? text = _clipboard.GetText();

                if (_clipboard.GetSequenceNumber() != ownedSequence.Value ||
                    _clipboard.GetOwnerProcessId() != gesture.SourceProcessId)
                {
                    externalChangeObserved = true;
                    ownedSequence = null;
                    return NoCapture();
                }

                if (!string.IsNullOrWhiteSpace(text))
                {
                    bool truncated = text.Length > options.MaxTextLength;
                    if (truncated)
                    {
                        text = text[..options.MaxTextLength];
                    }

                    CaptureSource source = chord == SimulatedCopyChord.CtrlInsert
                        ? CaptureSource.SimulatedCopyCtrlInsert
                        : CaptureSource.SimulatedCopyCtrlC;
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
        catch
        {
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

            if (!externalChangeObserved && ownedSequence is uint expectedSequence)
            {
                RestoreOriginalClipboard(snapshot, expectedSequence);
            }
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
