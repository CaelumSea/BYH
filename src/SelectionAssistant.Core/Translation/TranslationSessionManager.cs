using System.Text;

namespace SelectionAssistant.Core.Translation;

/// <summary>
/// Owns the latest translation request. Replacements cancel older work and all
/// view writes are protected by a generation check.
/// </summary>
public sealed class TranslationSessionManager : IDisposable, IAsyncDisposable
{
    private readonly object _gate = new();
    private ITranslationProvider _provider;  // mutable for hot-swap (ReplaceProvider)
    private readonly ITranslationSessionView _view;
    private readonly ITranslationUiDispatcher _dispatcher;
    private CancellationTokenSource? _currentCancellation;
    private TranslationRequest? _lastRequest;
    private Task _runningTask = Task.CompletedTask;
    private long _generation;
    private bool _disposed;

    public TranslationSessionManager(
        ITranslationProvider provider,
        ITranslationSessionView view,
        ITranslationUiDispatcher dispatcher)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _view = view ?? throw new ArgumentNullException(nameof(view));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public Task StartOrReplaceAsync(string sourceText)
    {
        TranslationRequest request = TranslationLanguageSelector.CreateRequest(sourceText);
        return StartRequestAsync(request);
    }

    /// <summary>
    /// Starts a session with a caller-built request. Used by the "Prompt Now"
    /// flow to run a custom system prompt against the selected text instead of
    /// the built-in translation template.
    /// </summary>
    public Task StartOrReplaceAsync(TranslationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return StartRequestAsync(request);
    }

    public Task RetryAsync()
    {
        TranslationRequest request;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            request = _lastRequest ?? throw new InvalidOperationException("No translation is available to retry.");
        }

        return StartRequestAsync(request);
    }

    public Task CancelAndHideAsync()
    {
        CancellationTokenSource? cancellation;
        long generation;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            generation = ++_generation;
            cancellation = _currentCancellation;
            _currentCancellation = null;
            _runningTask = Task.CompletedTask;
        }

        CancelSafely(cancellation);
        return _dispatcher.InvokeAsync(() =>
        {
            if (generation == Volatile.Read(ref _generation))
            {
                _view.Hide();
            }
        });
    }

    /// <summary>
    /// Hot-swaps the translation provider at runtime (no restart). Bumps the
    /// generation so any in-flight request from the old provider has its view
    /// writes dropped, and cancels the active CTS so streaming stops promptly.
    /// The old provider's disposal is the caller's responsibility.
    /// </summary>
    public void ReplaceProvider(ITranslationProvider newProvider)
    {
        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _provider = newProvider ?? throw new ArgumentNullException(nameof(newProvider));
            ++_generation;  // invalidate any in-flight writes from the old provider
            cancellation = _currentCancellation;
            _currentCancellation = null;
            _runningTask = Task.CompletedTask;
        }

        CancelSafely(cancellation);
    }

    private Task StartRequestAsync(TranslationRequest request)
    {
        CancellationTokenSource? previous;
        CancellationTokenSource current = new();
        long generation;
        Task running;

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            previous = _currentCancellation;
            _currentCancellation = current;
            _lastRequest = request;
            generation = ++_generation;
            running = RunAsync(request, generation, current);
            _runningTask = running;
        }

        CancelSafely(previous);
        return running;
    }

    private async Task RunAsync(
        TranslationRequest request,
        long generation,
        CancellationTokenSource cancellation)
    {
        // Prevent a provider/dispatcher that completes synchronously from running
        // the finally block while StartRequestAsync still owns _gate.
        await Task.Yield();
        CancellationToken token = cancellation.Token;
        try
        {
            await InvokeIfCurrentAsync(
                generation,
                token,
                () => _view.ShowLoading(request, _provider.DisplayName)).ConfigureAwait(false);

            // Branch: streaming providers emit incremental deltas; one-shot
            // providers return a complete result. Each delta and the final
            // assembly are generation-guarded so a superseded session never
            // writes stale chunks to the view.
            if (_provider is IStreamingTranslationProvider streaming)
            {
                var assembled = new StringBuilder();
                await foreach (TranslationDelta delta in streaming
                    .StreamAsync(request, token)
                    .ConfigureAwait(false))
                {
                    token.ThrowIfCancellationRequested();
                    if (generation != Volatile.Read(ref _generation))
                    {
                        return;
                    }

                    if (!string.IsNullOrEmpty(delta.Content))
                    {
                        assembled.Append(delta.Content);
                        await InvokeIfCurrentAsync(
                            generation,
                            token,
                            () => _view.AppendPartialResult(delta.Content)).ConfigureAwait(false);
                    }
                }

                token.ThrowIfCancellationRequested();
                var result = new TranslationResult(
                    assembled.ToString(),
                    request.SourceLanguage,
                    request.TargetLanguage,
                    _provider.DisplayName);
                await InvokeIfCurrentAsync(
                    generation,
                    token,
                    () => _view.ShowResult(result)).ConfigureAwait(false);
            }
            else
            {
                TranslationResult result = await _provider
                    .TranslateAsync(request, token)
                    .ConfigureAwait(false);

                token.ThrowIfCancellationRequested();
                await InvokeIfCurrentAsync(
                    generation,
                    token,
                    () => _view.ShowResult(result)).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Replaced or explicitly dismissed.
        }
        catch (TranslationProviderException exception)
        {
            await InvokeIfCurrentAsync(
                generation,
                token,
                () => _view.ShowError(exception.UserMessage)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            await InvokeIfCurrentAsync(
                generation,
                token,
                () => _view.ShowError("翻译失败，请稍后重试。")).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_currentCancellation, cancellation))
                {
                    _currentCancellation = null;
                    _runningTask = Task.CompletedTask;
                }
            }

            cancellation.Dispose();
        }
    }

    private async Task InvokeIfCurrentAsync(
        long generation,
        CancellationToken token,
        Action action)
    {
        if (token.IsCancellationRequested || generation != Volatile.Read(ref _generation))
        {
            return;
        }

        await _dispatcher.InvokeAsync(() =>
        {
            if (!token.IsCancellationRequested && generation == Volatile.Read(ref _generation))
            {
                action();
            }
        }).ConfigureAwait(false);
    }

    public void Dispose()
    {
        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            ++_generation;
            cancellation = _currentCancellation;
            _currentCancellation = null;
            _runningTask = Task.CompletedTask;
        }

        CancelSafely(cancellation);
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        Task running;
        lock (_gate)
        {
            running = _runningTask;
        }

        Dispose();
        try
        {
            await running.ConfigureAwait(false);
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
            // A request may finish between replacement and cancellation.
        }
    }
}
