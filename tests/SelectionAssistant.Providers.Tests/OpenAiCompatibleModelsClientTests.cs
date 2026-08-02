using System.Net;
using System.Text;
using SelectionAssistant.Core.Translation;
using SelectionAssistant.Platform.Abstractions.Secrets;
using SelectionAssistant.Providers;
using Xunit;

namespace SelectionAssistant.Providers.Tests;

/// <summary>
/// Tests for <see cref="OpenAiCompatibleModelsClient"/> — the GET /models
/// fetcher behind the Settings UI "Refresh Models" button. These are the first
/// HTTP-mocked tests in this project; the <c>RecordingHandler</c> inner class
/// mirrors the pattern in MyMemoryTranslationProviderTests (Core.Tests).
/// </summary>
public sealed class OpenAiCompatibleModelsClientTests
{
    private static OpenAiCompatibleProviderOptions Options(string baseUrl = "https://api.example.com/v1") =>
        new()
        {
            Id = "test",
            DisplayName = "Test",
            BaseUrl = baseUrl,
            ApiKeyReference = "secret://provider/test",
            DefaultModel = "default-model",
            Timeout = TimeSpan.FromSeconds(5),
        };

    private static OpenAiCompatibleProviderOptions OptionsNoKey() =>
        new()
        {
            Id = "test",
            DisplayName = "Test",
            BaseUrl = "https://api.example.com/v1",
            ApiKeyReference = null,
            DefaultModel = "default-model",
            Timeout = TimeSpan.FromSeconds(5),
        };

    /// <summary>Stub secret store that always returns a constant key.</summary>
    private sealed class ConstSecretStore(string key) : ISecretStore
    {
        public Task<string?> GetAsync(string reference, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(key);
        public Task SetAsync(string reference, string value, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task DeleteAsync(string reference, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    /// <summary>Stub secret store whose key is missing (null).</summary>
    private sealed class MissingSecretStore : ISecretStore
    {
        public Task<string?> GetAsync(string reference, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);
        public Task SetAsync(string reference, string value, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task DeleteAsync(string reference, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    /// <summary>HttpMessageHandler that runs a factory and records the last request.
    /// The factory receives the cancellation token the client threaded in (the
    /// linked-timeout CTS), so a test can simulate a stall by awaiting an
    /// infinite delay on that token.</summary>
    private sealed class RecordingHandler(
        Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            LastRequest = request;
            return Task.FromResult(responseFactory(request, cancellationToken));
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task ListModelsAsync_ParsesStandardOpenAiResponse_Sorted()
    {
        var handler = new RecordingHandler((_, _) => Json(
            """{"object":"list","data":[{"id":"gpt-4o"},{"id":"gpt-4o-mini"},{"id":"gpt-3.5-turbo"}]}"""));
        using var client = new OpenAiCompatibleModelsClient(
            Options(), new ConstSecretStore("test-key"), new HttpClient(handler));

        IReadOnlyList<string> models = await client.ListModelsAsync(CancellationToken.None);

        // Sorted OrdinalIgnoreCase: digits before letters, "4o" before "4o-mini".
        Assert.Equal(new[] { "gpt-3.5-turbo", "gpt-4o", "gpt-4o-mini" }, models);
    }

    [Fact]
    public async Task ListModelsAsync_SendsBearerTokenAndUserAgent()
    {
        var handler = new RecordingHandler((_, _) => Json("""{"data":[]}"""));
        using var client = new OpenAiCompatibleModelsClient(
            Options(), new ConstSecretStore("my-secret-key"), new HttpClient(handler));

        await client.ListModelsAsync(CancellationToken.None);

        Assert.NotNull(handler.LastRequest);
        Assert.Equal("Bearer", handler.LastRequest!.Headers.Authorization?.Scheme);
        Assert.Equal("my-secret-key", handler.LastRequest.Headers.Authorization!.Parameter);
        Assert.Contains("BYH/0.1", handler.LastRequest.Headers.UserAgent.ToString());
        Assert.Equal(HttpMethod.Get, handler.LastRequest.Method);
    }

    [Fact]
    public async Task ListModelsAsync_UsesEphemeralKeyOverride_WhenStoredKeyIsMissing()
    {
        var handler = new RecordingHandler((_, _) => Json("""{"data":[{"id":"draft-model"}]}"""));
        using var client = new OpenAiCompatibleModelsClient(
            Options(),
            new MissingSecretStore(),
            new HttpClient(handler),
            apiKeyOverride: "draft-only-key");

        IReadOnlyList<string> models = await client.ListModelsAsync(CancellationToken.None);

        Assert.Equal(new[] { "draft-model" }, models);
        Assert.Equal("Bearer", handler.LastRequest!.Headers.Authorization?.Scheme);
        Assert.Equal("draft-only-key", handler.LastRequest.Headers.Authorization!.Parameter);
    }

    [Fact]
    public async Task ListModelsAsync_HitsCorrectUri_TrailingSlashAgnostic()
    {
        // baseUrl WITHOUT trailing slash.
        var handler1 = new RecordingHandler((_, _) => Json("""{"data":[]}"""));
        using (var client = new OpenAiCompatibleModelsClient(
            Options("https://api.example.com/v1"), new ConstSecretStore("k"), new HttpClient(handler1)))
        {
            await client.ListModelsAsync(CancellationToken.None);
            Assert.Equal("https://api.example.com/v1/models", handler1.LastRequest!.RequestUri!.AbsoluteUri);
        }

        // baseUrl WITH trailing slash.
        var handler2 = new RecordingHandler((_, _) => Json("""{"data":[]}"""));
        using (var client = new OpenAiCompatibleModelsClient(
            Options("https://api.example.com/v1/"), new ConstSecretStore("k"), new HttpClient(handler2)))
        {
            await client.ListModelsAsync(CancellationToken.None);
            Assert.Equal("https://api.example.com/v1/models", handler2.LastRequest!.RequestUri!.AbsoluteUri);
        }
    }

    [Fact]
    public async Task ListModelsAsync_Throws401_OnUnauthorized()
    {
        var handler = new RecordingHandler((_, _) => Json("""{"error":{"message":"invalid api key"}}""", HttpStatusCode.Unauthorized));
        using var client = new OpenAiCompatibleModelsClient(
            Options(), new ConstSecretStore("bad-key"), new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<TranslationProviderException>(
            () => client.ListModelsAsync(CancellationToken.None));
        Assert.Contains("密钥", ex.Message);
        Assert.Contains("invalid api key", ex.Message);
    }

    [Fact]
    public async Task ListModelsAsync_Throws429_OnRateLimit()
    {
        var handler = new RecordingHandler((_, _) => Json("""{"error":"rate limited"}""", (HttpStatusCode)429));
        using var client = new OpenAiCompatibleModelsClient(
            Options(), new ConstSecretStore("k"), new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<TranslationProviderException>(
            () => client.ListModelsAsync(CancellationToken.None));
        Assert.Contains("频率", ex.Message);
    }

    [Fact]
    public async Task ListModelsAsync_FiltersNullIds_AndDedupes()
    {
        var handler = new RecordingHandler((_, _) => Json(
            """{"data":[{"id":null},{"id":"x"},{"id":"x"},{"id":"  "},{"id":"y"}]}"""));
        using var client = new OpenAiCompatibleModelsClient(
            Options(), new ConstSecretStore("k"), new HttpClient(handler));

        IReadOnlyList<string> models = await client.ListModelsAsync(CancellationToken.None);

        Assert.Equal(new[] { "x", "y" }, models);
    }

    [Fact]
    public async Task ListModelsAsync_HandlesEmptyDataArray()
    {
        var handler = new RecordingHandler((_, _) => Json("""{"data":[]}"""));
        using var client = new OpenAiCompatibleModelsClient(
            Options(), new ConstSecretStore("k"), new HttpClient(handler));

        IReadOnlyList<string> models = await client.ListModelsAsync(CancellationToken.None);

        Assert.Empty(models);
    }

    [Fact]
    public async Task ListModelsAsync_HandlesRootArray_ToleratesNonConformantGateway()
    {
        // Some non-conformant gateways return a bare array instead of {data:[...]}.
        var handler = new RecordingHandler((_, _) => Json("""[{"id":"a"},{"id":"b"}]"""));
        using var client = new OpenAiCompatibleModelsClient(
            Options(), new ConstSecretStore("k"), new HttpClient(handler));

        IReadOnlyList<string> models = await client.ListModelsAsync(CancellationToken.None);

        Assert.Equal(new[] { "a", "b" }, models);
    }

    [Fact]
    public async Task ListModelsAsync_ThrowsOnMalformedJson()
    {
        var handler = new RecordingHandler((_, _) => Json("this is not json"));
        using var client = new OpenAiCompatibleModelsClient(
            Options(), new ConstSecretStore("k"), new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<TranslationProviderException>(
            () => client.ListModelsAsync(CancellationToken.None));
        Assert.Contains("JSON", ex.Message);
    }

    [Fact]
    public async Task ListModelsAsync_ThrowsOnUnknownShape()
    {
        // Root is an object but has neither data[] nor is itself an array.
        var handler = new RecordingHandler((_, _) => Json("""{"object":"singleton","foo":"bar"}"""));
        using var client = new OpenAiCompatibleModelsClient(
            Options(), new ConstSecretStore("k"), new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<TranslationProviderException>(
            () => client.ListModelsAsync(CancellationToken.None));
        Assert.Contains("格式", ex.Message);
    }

    [Fact]
    public async Task ListModelsAsync_ThrowsWhenApiKeyReferenceMissing()
    {
        var handler = new RecordingHandler((_, _) => throw new InvalidOperationException("Network must not be called."));
        using var client = new OpenAiCompatibleModelsClient(
            OptionsNoKey(), new ConstSecretStore("k"), new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<TranslationProviderException>(
            () => client.ListModelsAsync(CancellationToken.None));
        Assert.Contains("密钥", ex.Message);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task ListModelsAsync_ThrowsWhenSecretNotSet()
    {
        var handler = new RecordingHandler((_, _) => throw new InvalidOperationException("Network must not be called."));
        using var client = new OpenAiCompatibleModelsClient(
            Options(), new MissingSecretStore(), new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<TranslationProviderException>(
            () => client.ListModelsAsync(CancellationToken.None));
        Assert.Contains("未设置密钥", ex.Message);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task ListModelsAsync_ThrowsTimeout_OnServerStall()
    {
        // Handler that blocks until its cancellation token fires. The client
        // threads the linked-timeout CTS into SendAsync, so when the 50ms
        // timeout trips, the blocking GetAwaiter().GetResult() throws
        // OperationCanceledException, which the client maps to
        // TranslationProviderException("...超时..."). RecordingHandler.SendAsync
        // is sync (returns Task.FromResult), so we block synchronously here.
        var handler = new RecordingHandler((_, token) =>
        {
            // Task.Delay(-1, token) throws OCE when the token cancels.
            Task.Delay(Timeout.Infinite, token).GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var client = new OpenAiCompatibleModelsClient(
            Options() with { Timeout = TimeSpan.FromMilliseconds(50) },
            new ConstSecretStore("k"),
            new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<TranslationProviderException>(
            () => client.ListModelsAsync(CancellationToken.None));
        Assert.Contains("超时", ex.Message);
    }

    [Fact]
    public async Task ListModelsAsync_ThrowsOnHttpRequestException()
    {
        var handler = new RecordingHandler((_, _) => throw new HttpRequestException("connection refused"));
        using var client = new OpenAiCompatibleModelsClient(
            Options(), new ConstSecretStore("k"), new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<TranslationProviderException>(
            () => client.ListModelsAsync(CancellationToken.None));
        Assert.Contains("无法连接", ex.Message);
    }

    [Fact]
    public async Task Dispose_IsIdempotent()
    {
        var handler = new RecordingHandler((_, _) => Json("""{"data":[]}"""));
        var client = new OpenAiCompatibleModelsClient(
            Options(), new ConstSecretStore("k"), new HttpClient(handler));

        client.Dispose();
        client.Dispose(); // must not throw

        // After dispose, further calls throw ObjectDisposedException.
        // ListModelsAsync is async — the throw surfaces when awaited.
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => client.ListModelsAsync(CancellationToken.None));
    }
}
