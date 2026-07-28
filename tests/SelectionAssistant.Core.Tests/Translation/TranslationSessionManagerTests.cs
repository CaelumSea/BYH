using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using SelectionAssistant.Core.Translation;
using Xunit;

namespace SelectionAssistant.Core.Tests.Translation;

public sealed class TranslationSessionManagerTests
{
    [Theory]
    [InlineData("Hello world", "en", "zh-CN")]
    [InlineData("你好，世界", "zh-CN", "en")]
    public void LanguageSelector_RoutesMinimalEnglishChinesePairs(
        string text,
        string expectedSource,
        string expectedTarget)
    {
        TranslationRequest request = TranslationLanguageSelector.CreateRequest(text);

        Assert.Equal(expectedSource, request.SourceLanguage);
        Assert.Equal(expectedTarget, request.TargetLanguage);
    }

    [Fact]
    public async Task ReplacementPreventsStaleResultFromOverwritingLatest()
    {
        var provider = new ControlledProvider();
        var view = new RecordingView();
        await using var manager = new TranslationSessionManager(provider, view, new InlineDispatcher());

        Task first = manager.StartOrReplaceAsync("first");
        await provider.WaitUntilStartedAsync("first");
        Task second = manager.StartOrReplaceAsync("second");
        await provider.WaitUntilStartedAsync("second");

        provider.Complete("second", "第二个");
        provider.Complete("first", "过期结果");
        await Task.WhenAll(first, second);

        Assert.Equal(["第二个"], view.Results);
        Assert.DoesNotContain("过期结果", view.Results);
    }

    [Fact]
    public async Task ProviderFailureShowsSafeUserMessageAndCanRetry()
    {
        var provider = new FailingProvider();
        var view = new RecordingView();
        await using var manager = new TranslationSessionManager(provider, view, new InlineDispatcher());

        await manager.StartOrReplaceAsync("hello");
        await manager.RetryAsync();

        Assert.Equal(2, provider.CallCount);
        Assert.Equal(["测试服务不可用", "测试服务不可用"], view.Errors);
    }

    [Fact]
    public async Task RetryWithTextAsync_RerunsWithEditedSourceAndRecomputesDirection()
    {
        var provider = new CapturingProvider();
        var view = new RecordingView();
        await using var manager = new TranslationSessionManager(provider, view, new InlineDispatcher());

        // Seed a request with a system prompt + thinking flag so we can assert
        // they survive the retry (only the source text + direction should change).
        TranslationRequest original = TranslationLanguageSelector.CreateRequest("hello")
            with { SystemPrompt = "custom-prompt", ThinkingEnabled = true, ActionDisplayName = "解释" };
        await manager.StartOrReplaceAsync(original);
        // Now retry with Chinese text — the direction must flip en→zh to zh→en,
        // the source must be the new text, and the action identity preserved.
        await manager.RetryWithTextAsync("你好");

        // Two calls captured: the original "hello" seed + the "你好" retry.
        // Assert against the last (the retry), not the whole list.
        Assert.Equal(2, provider.CapturedRequests.Count);
        TranslationRequest retried = provider.CapturedRequests[1];
        Assert.Equal("你好", retried.SourceText);
        Assert.Equal("zh-CN", retried.SourceLanguage);
        Assert.Equal("en", retried.TargetLanguage);
        Assert.Equal("custom-prompt", retried.SystemPrompt);
        Assert.True(retried.ThinkingEnabled);
        Assert.Equal("解释", retried.ActionDisplayName);
    }

    [Fact]
    public async Task RetryWithTextAsync_ThrowsWhenNoPriorSession()
    {
        var provider = new CapturingProvider();
        var view = new RecordingView();
        await using var manager = new TranslationSessionManager(provider, view, new InlineDispatcher());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.RetryWithTextAsync("anything"));
    }

    [Fact]
    public async Task CancelAndHidePreventsLateResultWrite()
    {
        var provider = new ControlledProvider();
        var view = new RecordingView();
        await using var manager = new TranslationSessionManager(provider, view, new InlineDispatcher());

        Task translation = manager.StartOrReplaceAsync("hello");
        await provider.WaitUntilStartedAsync("hello");
        await manager.CancelAndHideAsync();
        provider.Complete("hello", "迟到结果");
        await translation;

        Assert.Equal(1, view.HideCount);
        Assert.Empty(view.Results);
    }

    [Fact]
    public async Task Streaming_EmitsPartialChunksThenFinalResult()
    {
        var provider = new ControlledStreamingProvider();
        var view = new RecordingView();
        await using var manager = new TranslationSessionManager(provider, view, new InlineDispatcher());

        Task task = manager.StartOrReplaceAsync("hello");
        await provider.WaitUntilStartedAsync("hello");
        provider.Emit("hello", "你好");
        provider.Emit("hello", "，世界");
        provider.Complete("hello");
        await task;

        Assert.Equal(["你好", "，世界"], view.PartialChunks);
        Assert.Single(view.Results);
        Assert.Equal("你好，世界", view.Results[0]);
    }

    [Fact]
    public async Task Streaming_ReplacedMidStream_StaleChunksDoNotAppend()
    {
        var provider = new ControlledStreamingProvider();
        var view = new RecordingView();
        await using var manager = new TranslationSessionManager(provider, view, new InlineDispatcher());

        Task first = manager.StartOrReplaceAsync("first");
        await provider.WaitUntilStartedAsync("first");

        // Replace mid-stream; the first session must stop emitting.
        Task second = manager.StartOrReplaceAsync("second");
        await provider.WaitUntilStartedAsync("second");

        provider.Emit("first", "过期块");   // stale — must be dropped
        provider.Emit("second", "新块");
        provider.Complete("second");
        provider.Complete("first");
        await Task.WhenAll(first, second);

        Assert.DoesNotContain("过期块", view.PartialChunks);
        Assert.Equal(["新块"], view.PartialChunks);
    }

    [Fact]
    public async Task Streaming_ProviderError_ShowsUserMessage()
    {
        var provider = new FailingStreamingProvider();
        var view = new RecordingView();
        await using var manager = new TranslationSessionManager(provider, view, new InlineDispatcher());

        await manager.StartOrReplaceAsync("hello");

        Assert.Equal(["流式服务错误"], view.Errors);
        Assert.Empty(view.PartialChunks);
    }

    private sealed class InlineDispatcher : ITranslationUiDispatcher
    {
        public Task InvokeAsync(Action action)
        {
            action();
            return Task.CompletedTask;
        }
    }

    private sealed class ControlledProvider : ITranslationProvider
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource<TranslationResult>> _requests = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _started = new();

        public string DisplayName => "Controlled";

        public Task<TranslationResult> TranslateAsync(
            TranslationRequest request,
            CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource<TranslationResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _requests[request.SourceText] = completion;
            _started.GetOrAdd(request.SourceText, _ => NewSignal()).TrySetResult();
            return completion.Task;
        }

        public Task WaitUntilStartedAsync(string sourceText) =>
            _started.GetOrAdd(sourceText, _ => NewSignal()).Task.WaitAsync(TimeSpan.FromSeconds(2));

        public void Complete(string sourceText, string translatedText)
        {
            _requests[sourceText].TrySetResult(new TranslationResult(
                translatedText,
                "en",
                "zh-CN",
                DisplayName));
        }

        private static TaskCompletionSource NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class FailingProvider : ITranslationProvider
    {
        public string DisplayName => "Failing";

        public int CallCount { get; private set; }

        public Task<TranslationResult> TranslateAsync(
            TranslationRequest request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            throw new TranslationProviderException("测试服务不可用");
        }
    }

    /// <summary>
    /// A provider that captures every request it sees, for asserting what the
    /// session manager actually forwarded (source text, language pair, action
    /// context). Returns a fixed result so the call completes synchronously.
    /// </summary>
    private sealed class CapturingProvider : ITranslationProvider
    {
        public string DisplayName => "Capturing";

        public List<TranslationRequest> CapturedRequests { get; } = [];

        public Task<TranslationResult> TranslateAsync(
            TranslationRequest request,
            CancellationToken cancellationToken)
        {
            CapturedRequests.Add(request);
            return Task.FromResult(new TranslationResult(
                "captured-result",
                request.SourceLanguage,
                request.TargetLanguage,
                DisplayName));
        }
    }

    /// <summary>
    /// A streaming provider whose emission is externally gated via a channel,
    /// mirroring ControlledProvider for the streaming path. The session is
    /// created inside StreamAsync so the test waits on a shared signal that is
    /// set only once the real enumeration is about to begin.
    /// </summary>
    private sealed class ControlledStreamingProvider : IStreamingTranslationProvider, ITranslationProvider
    {
        private readonly ConcurrentDictionary<string, StreamSession> _sessions = new();

        public string DisplayName => "ControlledStream";

        public IAsyncEnumerable<TranslationDelta> StreamAsync(
            TranslationRequest request, CancellationToken cancellationToken)
        {
            StreamSession session = _sessions.GetOrAdd(
                request.SourceText, _ => new StreamSession());
            return EnumerateAsync(session, cancellationToken);
        }

        public Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken ct)
            => throw new NotImplementedException();

        // Wait until a session for this key has been registered by StreamAsync.
        public Task WaitUntilStartedAsync(string sourceText)
        {
            // Spin until the session appears. Bounded by the caller's timeout.
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task.Run(async () =>
            {
                while (!_sessions.ContainsKey(sourceText))
                {
                    await Task.Delay(5);
                }
                tcs.TrySetResult();
            });
            return tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }

        public void Emit(string sourceText, string content)
        {
            if (_sessions.TryGetValue(sourceText, out var session))
            {
                session.Pending.Writer.TryWrite(content);
            }
        }

        public void Complete(string sourceText)
        {
            if (_sessions.TryGetValue(sourceText, out var session))
            {
                session.Pending.Writer.TryComplete();
            }
        }

        private static async IAsyncEnumerable<TranslationDelta> EnumerateAsync(
            StreamSession session,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (string chunk in session.Pending.Reader.ReadAllAsync(cancellationToken))
            {
                yield return new TranslationDelta(chunk);
            }
        }

        private sealed class StreamSession
        {
            public Channel<string> Pending { get; } = Channel.CreateUnbounded<string>();
        }
    }

    private sealed class FailingStreamingProvider : IStreamingTranslationProvider, ITranslationProvider
    {
        public string DisplayName => "FailingStream";

        public async IAsyncEnumerable<TranslationDelta> StreamAsync(
            TranslationRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            throw new TranslationProviderException("流式服务错误");
#pragma warning disable CS0162 // unreachable after throw
            yield break;
#pragma warning restore CS0162
        }

        public Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken ct)
            => throw new TranslationProviderException("流式服务错误");
    }

    private sealed class RecordingView : ITranslationSessionView
    {
        public List<string> Results { get; } = [];

        public List<string> PartialChunks { get; } = [];

        public List<string> Errors { get; } = [];

        public int HideCount { get; private set; }

        public void ShowLoading(TranslationRequest request, string providerName)
        {
        }

        public void ShowResult(TranslationResult result) => Results.Add(result.TranslatedText);

        public void AppendPartialResult(string chunk) => PartialChunks.Add(chunk);

        public void ShowError(string userMessage) => Errors.Add(userMessage);

        public void Hide() => HideCount++;
    }
}
