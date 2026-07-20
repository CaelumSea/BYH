using System.Collections.Concurrent;
using SelectionAssistant.Platform.Abstractions;

namespace SelectionAssistant.Platform.Windows.Capture;

/// <summary>
/// Tier 1 text capture with an isolated, replaceable UI Automation worker.
/// Caller timeout never claims to cancel a blocked native UIA provider.
/// </summary>
public sealed class UIAutomationTextCapture : ISelectionTextCapture, IDisposable
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMilliseconds(400);

    private readonly object _gate = new();
    private readonly Func<IUiAutomationBackend> _backendFactory;
    private readonly TimeSpan _timeout;
    private UiAutomationWorker _activeWorker;
    private long _nextRequestId;
    private bool _disposed;

    public UIAutomationTextCapture(TimeSpan? timeout = null)
        : this(() => new WindowsUiAutomationBackend(), timeout ?? DefaultTimeout)
    {
    }

    public UIAutomationTextCapture(
        Func<IUiAutomationBackend> backendFactory,
        TimeSpan timeout)
    {
        _backendFactory = backendFactory ?? throw new ArgumentNullException(nameof(backendFactory));
        _timeout = timeout;

        if (_timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        _activeWorker = CreateWorker();
    }

    public async Task<CaptureResult> CaptureAsync(SelectionGesture gesture, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(gesture);
        token.ThrowIfCancellationRequested();

        UiAutomationWorker worker;
        UiAutomationRequest request = new(
            Interlocked.Increment(ref _nextRequestId),
            gesture);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            worker = _activeWorker;

            if (!worker.TryEnqueue(request))
            {
                worker = ReplaceWorkerUnderLock(worker);
                if (!worker.TryEnqueue(request))
                {
                    return NoCapture();
                }
            }
        }

        try
        {
            UiAutomationReadResult result = await request.Completion.Task
                .WaitAsync(_timeout, token)
                .ConfigureAwait(false);

            return string.IsNullOrWhiteSpace(result.Text)
                ? NoCapture()
                : new CaptureResult(result.Text, CaptureSource.Accessibility, result.IsAmbiguous);
        }
        catch (TimeoutException)
        {
            request.Abandon();
            ReplaceWorker(worker);
            return NoCapture();
        }
        catch (OperationCanceledException)
        {
            request.Abandon();
            throw;
        }
        catch
        {
            request.Abandon();
            ReplaceWorker(worker);
            return NoCapture();
        }
    }

    private static CaptureResult NoCapture() => new(null, CaptureSource.None, false);

    private void ReplaceWorker(UiAutomationWorker expectedWorker)
    {
        lock (_gate)
        {
            if (_disposed || !ReferenceEquals(_activeWorker, expectedWorker))
            {
                return;
            }

            ReplaceWorkerUnderLock(expectedWorker);
        }
    }

    private UiAutomationWorker ReplaceWorkerUnderLock(UiAutomationWorker worker)
    {
        worker.Quarantine();
        _activeWorker = CreateWorker();
        return _activeWorker;
    }

    private UiAutomationWorker CreateWorker() => new(_backendFactory());

    public void Dispose()
    {
        UiAutomationWorker worker;

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            worker = _activeWorker;
        }

        worker.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed class UiAutomationWorker : IDisposable
    {
        private readonly BlockingCollection<UiAutomationRequest> _queue = new();
        private readonly IUiAutomationBackend _backend;
        private readonly Thread _thread;
        private int _quarantined;
        private int _disposed;

        public UiAutomationWorker(IUiAutomationBackend backend)
        {
            _backend = backend ?? throw new ArgumentNullException(nameof(backend));
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "BYH.UIAutomation",
                Priority = ThreadPriority.Normal,
            };
            _thread.SetApartmentState(ApartmentState.MTA);
            _thread.Start();
        }

        public bool TryEnqueue(UiAutomationRequest request)
        {
            if (Volatile.Read(ref _quarantined) != 0 || Volatile.Read(ref _disposed) != 0)
            {
                return false;
            }

            try
            {
                return _queue.TryAdd(request);
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        public void Quarantine()
        {
            if (Interlocked.Exchange(ref _quarantined, 1) != 0)
            {
                return;
            }

            _queue.CompleteAdding();
            FailQueuedRequests();
        }

        private void Run()
        {
            try
            {
                foreach (UiAutomationRequest request in _queue.GetConsumingEnumerable())
                {
                    if (request.IsAbandoned)
                    {
                        continue;
                    }

                    try
                    {
                        UiAutomationReadResult result = _backend.ReadSelection(request.Gesture);
                        if (!request.IsAbandoned)
                        {
                            request.Completion.TrySetResult(result);
                        }
                    }
                    catch (Exception exception)
                    {
                        if (!request.IsAbandoned)
                        {
                            request.Completion.TrySetException(exception);
                        }
                    }

                    if (Volatile.Read(ref _quarantined) != 0)
                    {
                        break;
                    }
                }
            }
            finally
            {
                FailQueuedRequests();
                if (_backend is IDisposable disposableBackend)
                {
                    disposableBackend.Dispose();
                }
            }
        }

        private void FailQueuedRequests()
        {
            while (_queue.TryTake(out UiAutomationRequest? request))
            {
                request.Abandon();
                request.Completion.TrySetException(
                    new InvalidOperationException("The UI Automation worker was quarantined."));
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            Quarantine();
            bool stopped = true;
            if (_thread != Thread.CurrentThread)
            {
                stopped = _thread.Join(TimeSpan.FromMilliseconds(250));
            }

            // A quarantined native provider may still be blocked. Do not dispose
            // its queue out from under the worker; it will become collectible after
            // the native call eventually returns and the background thread exits.
            if (stopped)
            {
                _queue.Dispose();
            }
        }
    }

    private sealed class UiAutomationRequest
    {
        private int _abandoned;

        public UiAutomationRequest(long id, SelectionGesture gesture)
        {
            Id = id;
            Gesture = gesture;
        }

        public long Id { get; }

        public SelectionGesture Gesture { get; }

        public TaskCompletionSource<UiAutomationReadResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsAbandoned => Volatile.Read(ref _abandoned) != 0;

        public void Abandon() => Interlocked.Exchange(ref _abandoned, 1);
    }
}

/// <summary>
/// Synchronous UIA backend contract. Every call for one backend instance is made
/// on its dedicated worker thread.
/// </summary>
public interface IUiAutomationBackend
{
    UiAutomationReadResult ReadSelection(SelectionGesture gesture);
}

public sealed record UiAutomationReadResult(string? Text, bool IsAmbiguous = false);
