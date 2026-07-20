using System.Net;
using System.Text;
using SelectionAssistant.Core.Translation;
using SelectionAssistant.Infrastructure.Translation;
using Xunit;

namespace SelectionAssistant.Core.Tests.Translation;

public sealed class MyMemoryTranslationProviderTests
{
    [Fact]
    public async Task TranslateAsync_EncodesRequestAndParsesHtmlEncodedTranslation()
    {
        var handler = new RecordingHandler(_ => JsonResponse(
            """
            {"responseData":{"translatedText":"你好&amp;世界"},"responseStatus":200}
            """));
        using var client = new HttpClient(handler);
        using var provider = new MyMemoryTranslationProvider(client);
        var request = new TranslationRequest("Hello & world", "en", "zh-CN");

        TranslationResult result = await provider.TranslateAsync(request, CancellationToken.None);

        Assert.Equal("你好&世界", result.TranslatedText);
        Assert.Contains("q=Hello%20%26%20world", handler.LastRequestUri?.Query);
        Assert.Contains("langpair=en%7Czh-CN", handler.LastRequestUri?.Query);
        Assert.Contains("BYH/0.1", handler.LastUserAgent);
    }

    [Fact]
    public async Task TranslateAsync_RejectsOversizedTextBeforeNetworkAccess()
    {
        var handler = new RecordingHandler(_ => throw new InvalidOperationException("Network must not be called."));
        using var client = new HttpClient(handler);
        using var provider = new MyMemoryTranslationProvider(client);
        var request = new TranslationRequest(new string('界', 167), "zh-CN", "en");

        TranslationProviderException exception = await Assert.ThrowsAsync<TranslationProviderException>(
            () => provider.TranslateAsync(request, CancellationToken.None));

        Assert.Contains("500", exception.UserMessage);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task TranslateAsync_ReportsProviderDetailsWhenApiRejectsRequest()
    {
        var handler = new RecordingHandler(_ => JsonResponse(
            """
            {"responseData":{"translatedText":null},"responseDetails":"INVALID LANGUAGE PAIR","responseStatus":"403"}
            """));
        using var client = new HttpClient(handler);
        using var provider = new MyMemoryTranslationProvider(client);

        TranslationProviderException exception = await Assert.ThrowsAsync<TranslationProviderException>(
            () => provider.TranslateAsync(
                new TranslationRequest("hello", "invalid", "zh-CN"),
                CancellationToken.None));

        Assert.Contains("INVALID LANGUAGE PAIR", exception.UserMessage);
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public Uri? LastRequestUri { get; private set; }

        public string? LastUserAgent { get; private set; }

        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            LastRequestUri = request.RequestUri;
            LastUserAgent = request.Headers.UserAgent.ToString();
            return Task.FromResult(responseFactory(request));
        }
    }
}
