using System.Net;
using System.Text;
using System.Text.Json;
using SelectionAssistant.Core.Translation;

namespace SelectionAssistant.Infrastructure.Translation;

/// <summary>
/// Credential-free test provider backed by MyMemory's public translation-memory
/// REST API. Selected text is sent over HTTPS; this is intentionally replaceable.
/// </summary>
public sealed class MyMemoryTranslationProvider : ITranslationProvider, IDisposable
{
    public const int MaximumUtf8Bytes = 500;
    private static readonly Uri Endpoint = new("https://api.mymemory.translated.net/get");
    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;
    private readonly TimeSpan _timeout;
    private int _disposed;

    public MyMemoryTranslationProvider(HttpClient? httpClient = null, TimeSpan? timeout = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _ownsClient = httpClient is null;
        _timeout = timeout ?? TimeSpan.FromSeconds(10);

        if (_timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }
    }

    public string DisplayName => "MyMemory 测试翻译";

    public async Task<TranslationResult> TranslateAsync(
        TranslationRequest request,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourceText);

        int byteCount = Encoding.UTF8.GetByteCount(request.SourceText);
        if (byteCount > MaximumUtf8Bytes)
        {
            throw new TranslationProviderException(
                $"测试翻译单次最多支持 {MaximumUtf8Bytes} 个 UTF-8 字节，请缩短选中文字。");
        }

        string uri = $"{Endpoint}?q={Uri.EscapeDataString(request.SourceText)}" +
            $"&langpair={Uri.EscapeDataString(request.SourceLanguage + "|" + request.TargetLanguage)}&mt=1";

        using var message = new HttpRequestMessage(HttpMethod.Get, uri);
        message.Headers.UserAgent.ParseAdd("BYH/0.1 (selection translation test)");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient
                .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TranslationProviderException("翻译服务响应超时，请重试。", exception);
        }
        catch (HttpRequestException exception)
        {
            throw new TranslationProviderException("无法连接翻译服务，请检查网络。", exception);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new TranslationProviderException(
                    $"翻译服务暂不可用（HTTP {(int)response.StatusCode}）。");
            }

            try
            {
                await using Stream body = await response.Content
                    .ReadAsStreamAsync(timeout.Token)
                    .ConfigureAwait(false);
                using JsonDocument document = await JsonDocument
                    .ParseAsync(body, cancellationToken: timeout.Token)
                    .ConfigureAwait(false);

                JsonElement root = document.RootElement;
                int responseStatus = ReadResponseStatus(root);
                string? translatedText = ReadString(root, "responseData", "translatedText");
                if (responseStatus != 200 || string.IsNullOrWhiteSpace(translatedText))
                {
                    string details = ReadString(root, "responseDetails") ?? "服务未返回译文";
                    throw new TranslationProviderException($"翻译失败：{details}。");
                }

                return new TranslationResult(
                    WebUtility.HtmlDecode(translatedText).Trim(),
                    request.SourceLanguage,
                    request.TargetLanguage,
                    DisplayName);
            }
            catch (JsonException exception)
            {
                throw new TranslationProviderException("翻译服务返回了无法识别的数据。", exception);
            }
        }
    }

    private static int ReadResponseStatus(JsonElement root)
    {
        if (!root.TryGetProperty("responseStatus", out JsonElement status))
        {
            return 0;
        }

        if (status.ValueKind == JsonValueKind.Number && status.TryGetInt32(out int numeric))
        {
            return numeric;
        }

        return status.ValueKind == JsonValueKind.String &&
            int.TryParse(status.GetString(), out numeric)
                ? numeric
                : 0;
    }

    private static string? ReadString(JsonElement root, params string[] path)
    {
        JsonElement current = root;
        foreach (string segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object ||
                !current.TryGetProperty(segment, out current))
            {
                return null;
            }
        }

        return current.ValueKind == JsonValueKind.String ? current.GetString() : current.ToString();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_ownsClient)
        {
            _httpClient.Dispose();
        }
    }
}
