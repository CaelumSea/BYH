using SelectionAssistant.Platform.Abstractions;

namespace SelectionAssistant.Core.Selection;

/// <summary>
/// Owns the latest selection session. Capture starts immediately, concurrently
/// with the anti-flicker delay, and every UI write is protected by a stale-id guard.
/// </summary>
public sealed class SelectionSessionManager : IDisposable, IAsyncDisposable
{
    public static readonly TimeSpan DefaultAntiFlickerDelay = TimeSpan.FromMilliseconds(75);

    private readonly object _gate = new();
    private readonly ISelectionTextCapture _capture;
    private readonly ISelectionSessionView _view;
    private readonly ISelectionUiDispatcher _uiDispatcher;
    private readonly TimeSpan _antiFlickerDelay;
    private readonly HashSet<Task<CaptureResult>> _detachedCaptures = [];

    // Optional diagnostic sink. When wired (App composition root), captures the
    // Source + length + short preview of every CaptureResult so we can trace
    // which tier (UIA / clipboard / vision / manual-fallback) surfaced text.
    private readonly Action<string>? _diagnosticSink;

    private SessionState? _current;
    private Task _runningTask = Task.CompletedTask;
    private long _currentSessionId;
    private string? _lastCapturedText;   // text from the most recent successful capture
    private bool _disposed;

    public SelectionSessionManager(
        ISelectionTextCapture capture,
        ISelectionSessionView view,
        ISelectionUiDispatcher uiDispatcher,
        TimeSpan? antiFlickerDelay = null,
        Action<string>? diagnosticSink = null)
    {
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
        _view = view ?? throw new ArgumentNullException(nameof(view));
        _uiDispatcher = uiDispatcher ?? throw new ArgumentNullException(nameof(uiDispatcher));
        _antiFlickerDelay = antiFlickerDelay ?? DefaultAntiFlickerDelay;
        _diagnosticSink = diagnosticSink;

        if (_antiFlickerDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(antiFlickerDelay));
        }
    }

    public long CurrentSessionId => Volatile.Read(ref _currentSessionId);

    public Task RunningTask
    {
        get
        {
            lock (_gate)
            {
                return _runningTask;
            }
        }
    }

    /// <summary>
    /// Returns the text captured by the most recent successful selection
    /// session, or null if none. Used by the chord/quick-tools flow.
    /// </summary>
    public string? GetLastCapturedText()
    {
        lock (_gate)
        {
            return _lastCapturedText;
        }
    }

    /// <summary>
    /// R40 Ocean Eyes: seeds the "last captured text" without going through a
    /// full selection session. Used when OCR produces the text out-of-band (the
    /// region-select path captures the screen, OCRs it asynchronously, then
    /// feeds the result here so the toolbar's F/J/Z/R/C shortcuts see it).
    /// Mirrors the assignment in <see cref="SessionCoreAsync"/>: null/empty →
    /// null (so the shortcuts correctly treat it as "no text captured" and
    /// pass through), otherwise trimmed non-whitespace text.
    /// </summary>
    public void SeedLastCapturedText(CaptureResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        string? seeded = string.IsNullOrWhiteSpace(result.Text) ? null : result.Text.Trim();
        lock (_gate)
        {
            _lastCapturedText = seeded;
        }
    }

    public Task StartOrReplaceSessionAsync(SelectionGesture gesture)
    {
        ArgumentNullException.ThrowIfNull(gesture);

        SessionState? previous;
        SessionState current;

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            previous = _current;
            long sessionId = Interlocked.Increment(ref _currentSessionId);
            current = new SessionState(sessionId);
            _current = current;
            _runningTask = current.Completion.Task;
        }

        // Cancellation callbacks are external code, so never run them under _gate.
        CancelSafely(previous?.Cancellation);

        current.RunnerTask = RunSessionAndCompleteAsync(current, gesture);
        return current.Completion.Task;
    }

    /// <summary>
    /// Invalidates the active capture and hides the ephemeral toolbar. The UI
    /// action is generation-guarded so a newer selection cannot be hidden by a
    /// delayed dismissal queued for the previous selection.
    /// </summary>
    public Task DismissCurrentSessionAsync()
    {
        SessionState? current;
        long dismissalId;

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            dismissalId = Interlocked.Increment(ref _currentSessionId);
            current = _current;
            _current = null;
            _runningTask = Task.CompletedTask;
        }

        CancelSafely(current?.Cancellation);

        return _uiDispatcher.InvokeAsync(() =>
        {
            if (dismissalId == Volatile.Read(ref _currentSessionId))
            {
                _view.HideToolbar();
            }
        });
    }

    private async Task RunSessionAndCompleteAsync(SessionState session, SelectionGesture gesture)
    {
        try
        {
            await SessionCoreAsync(session, gesture).ConfigureAwait(false);
            session.Completion.TrySetResult();
        }
        catch (OperationCanceledException) when (session.Cancellation.IsCancellationRequested)
        {
            // Being superseded is normal completion from the caller's perspective.
            session.Completion.TrySetResult();
        }
        catch (Exception exception)
        {
            session.Completion.TrySetException(exception);
        }
        finally
        {
            CompleteSession(session);
        }
    }

    private async Task SessionCoreAsync(SessionState session, SelectionGesture gesture)
    {
        CancellationToken token = session.Cancellation.Token;
        token.ThrowIfCancellationRequested();

        // Phase 1 capture starts immediately. UIA implementations must return a
        // task quickly and perform potentially blocking native work on their
        // dedicated worker.
        Task<CaptureResult> captureTask = _capture.CaptureAsync(gesture, token);
        session.CaptureTask = captureTask ?? throw new InvalidOperationException("Capture returned a null task.");

        // Anti-flicker delay: wait a beat so a quick click-drag-release (that
        // turns out to have no selection) doesn't flash the toolbar. Capture
        // runs concurrently during this delay.
        await Task.Delay(_antiFlickerDelay, token).ConfigureAwait(false);

        CaptureResult result = await captureTask.WaitAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();

        // DIAGNOSTIC (R33): log which tier returned text so we can trace false
        // positives. Preview is capped at 40 chars; selected text is sensitive
        // but length + short prefix is needed to identify the captured element.
        if (_diagnosticSink is { } sink)
        {
            string preview = result.Text is { Length: > 0 } t
                ? "\"" + (t.Length <= 40 ? t : t.Substring(0, 40) + "…") + "\""
                : "<null>";
            sink($"capture source={result.Source} len={result.Text?.Length ?? 0} preview={preview} proc={gesture.SourceProcessId}");
        }

        // Remember the captured text so the chord/quick-tools flow can reuse it
        // without a fresh capture (the chord fires after selection, not during).
        _lastCapturedText = string.IsNullOrWhiteSpace(result.Text) ? null : result.Text.Trim();

        // FAST PATH: phase 1 (UIA + clipboard) yielded text → show toolbar with
        // the result immediately. Visual OCR is NOT part of the selection path
        // (R24 redesign): unselectable content is handled by the chord → region-
        // select overlay flow, not auto-screenshot on every selection.
        if (_lastCapturedText is not null)
        {
            await InvokeForCurrentSessionAsync(
                session,
                () =>
                {
                    _view.ShowToolbar(gesture);
                    _view.SetCaptureResult(result);
                }).ConfigureAwait(false);
            return;
        }

        // CRITICAL (R20 guard): no phase-1 text → no toolbar, full stop. The
        // ManualFallback source is intentionally NOT "show anyway" — it fires
        // on double-click of empty space too, so honoring it would re-introduce
        // the "opens on any click/drag" misfire. Unselectable content now goes
        // through the explicit chord → draw-region OCR path, not auto-OCR here.
        return;
    }

    private async Task InvokeForCurrentSessionAsync(SessionState session, Action action)
    {
        if (!IsCurrent(session))
        {
            return;
        }

        await _uiDispatcher.InvokeAsync(() =>
        {
            // Re-check inside the queued UI action. The session may have become stale
            // between scheduling and execution on the UI thread.
            if (IsCurrent(session))
            {
                action();
            }
        }).ConfigureAwait(false);
    }

    private bool IsCurrent(SessionState session) =>
        !session.Cancellation.IsCancellationRequested &&
        session.Id == Volatile.Read(ref _currentSessionId);

    private void CompleteSession(SessionState session)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_current, session))
            {
                _current = null;
                _runningTask = Task.CompletedTask;
            }
        }

        Task<CaptureResult>? captureTask = session.CaptureTask;
        if (captureTask is null || captureTask.IsCompleted)
        {
            ObserveCaptureFailure(captureTask);
            session.Cancellation.Dispose();
            return;
        }

        TrackDetachedCapture(captureTask, session.Cancellation);
    }

    private void TrackDetachedCapture(Task<CaptureResult> captureTask, CancellationTokenSource cancellation)
    {
        lock (_gate)
        {
            _detachedCaptures.Add(captureTask);
        }

        _ = captureTask.ContinueWith(
            completedTask =>
            {
                ObserveCaptureFailure(completedTask);
                lock (_gate)
                {
                    _detachedCaptures.Remove(completedTask);
                }

                cancellation.Dispose();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static void ObserveCaptureFailure(Task<CaptureResult>? captureTask)
    {
        if (captureTask?.IsFaulted == true)
        {
            _ = captureTask.Exception;
        }
    }

    public void Dispose()
    {
        SessionState? current;

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Interlocked.Increment(ref _currentSessionId);
            current = _current;
            _current = null;
            _runningTask = Task.CompletedTask;
        }

        CancelSafely(current?.Cancellation);
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        Task runningTask = RunningTask;
        Dispose();

        try
        {
            await runningTask.ConfigureAwait(false);
        }
        catch
        {
            // Disposal only guarantees cancellation and observation.
        }
    }

    private static void CancelSafely(CancellationTokenSource? cancellation)
    {
        if (cancellation is null)
        {
            return;
        }

        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // A completed session may dispose itself between replacement and cancel.
        }
    }

    private sealed class SessionState
    {
        public SessionState(long id)
        {
            Id = id;
        }

        public long Id { get; }

        public CancellationTokenSource Cancellation { get; } = new();

        public TaskCompletionSource Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task RunnerTask { get; set; } = Task.CompletedTask;

        public Task<CaptureResult>? CaptureTask { get; set; }
    }
}
