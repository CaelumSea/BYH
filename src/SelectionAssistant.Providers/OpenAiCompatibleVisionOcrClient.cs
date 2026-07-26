using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SelectionAssistant.Core.Capture;
using SelectionAssistant.Core.Translation;
using SelectionAssistant.Platform.Abstractions.Secrets;
using SelectionAssistant.Providers.Sse;

namespace SelectionAssistant.Providers;

/// <summary>
/// R24 track B: cloud OCR over the OpenAI-compatible chat-completion surface.
/// Sends a single image (as a <c>data:image/png;base64,...</c> URI) plus an OCR
/// prompt, and aggregates the streamed text completion. Reuses the same provider
/// config / DPAPI secret store / SSE parsing / redirect-disabled HTTP client as
/// <see cref="OpenAiCompatibleStreamingProvider" /> (§9.4 security).
/// </summary>
/// <remarks>
/// Thinking control is per-model: hybrid reasoning vision models (Qwen3.x)
/// need <c>enable_thinking:false</c> to avoid multi-second reasoning_content
/// latency, while pure OCR models (DeepSeek-OCR, PaddleOCR-VL) reject that
/// param with HTTP 400. The caller passes <c>disableThinking</c> based on the
/// configured model. See <see cref="BuildRequestBody" />.
/// </remarks>
public sealed partial class OpenAiCompatibleVisionOcrClient : IVisionOcrClient, IDisposable
{
    private readonly OpenAiCompatibleProviderOptions _options;
    private readonly ISecretStore _secretStore;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;
    private readonly string _ocrPrompt;
    private readonly bool _disableThinking;
    private int _disposed;

    /// <summary>
    /// Strips &lt;think&gt;...&lt;/think&gt; reasoning blocks that some vision
    /// models emit mid-stream even on a pure extraction prompt (DeepSeek-VL
    /// reasoning variants, Qwen-VL-Thinking, GLM-4V). DOTALL so newlines inside
    /// the block are matched; non-greedy so multiple blocks in one response are
    /// each removed. Without this, the user sees the model's internal monologue
    /// prepended to the OCR text ("OCR 多余文字" bug). Compiled + cached for
    /// reuse across the streaming aggregate.
    /// </summary>
    [GeneratedRegex(@"<think>.*?</think>", RegexOptions.Singleline)]
    private static partial Regex ThinkBlockPattern();

    /// <param name="ocrPrompt">Instruction sent as the user text part. Defaults
    /// to DeepSeek-OCR's official <c>"Free OCR."</c> when null/whitespace.</param>
    /// <param name="disableThinking">When true, sends <c>enable_thinking: false</c>
    /// in the request body. Required for hybrid reasoning vision models (Qwen3.x,
    /// DeepSeek-VL) where the model spends seconds generating reasoning_content
    /// before the actual OCR text. Pure OCR models (DeepSeek-OCR, PaddleOCR-VL)
    /// reject this param with HTTP 400, so leave false for those.</param>
    public OpenAiCompatibleVisionOcrClient(
        OpenAiCompatibleProviderOptions options,
        ISecretStore secretStore,
        string? ocrPrompt = null,
        bool disableThinking = false,
        HttpClient? httpClient = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
        _ocrPrompt = string.IsNullOrWhiteSpace(ocrPrompt) ? "Free OCR." : ocrPrompt!;
        _disableThinking = disableThinking;

        if (httpClient is null)
        {
            var handler = new SocketsHttpHandler { AllowAutoRedirect = false };
            _httpClient = new HttpClient(handler) { Timeout = _options.Timeout };
            _ownsClient = true;
        }
        else
        {
            _httpClient = httpClient;
            _ownsClient = false;
        }
    }

    public async Task<string> RecognizeAsync(string imageDataUri, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(imageDataUri);

        string apiKey = await ResolveApiKeyAsync(cancellationToken).ConfigureAwait(false);
        string requestUri = ProviderUriBuilder.Build(_options.BaseUrl, _options.ChatCompletionsPath);
        byte[] body = BuildRequestBody(imageDataUri, _options, _ocrPrompt, _disableThinking);

        using var message = new HttpRequestMessage(HttpMethod.Post, requestUri);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        message.Headers.UserAgent.ParseAdd("BYH/0.1 (selection translation)");
        message.Content = new ByteArrayContent(body);
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.Timeout);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient
                .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TranslationProviderException("OCR 服务响应超时，请重试。");
        }
        catch (HttpRequestException)
        {
            throw new TranslationProviderException("无法连接 OCR 服务，请检查网络或 API 地址。");
        }

        if (!response.IsSuccessStatusCode)
        {
            int code = (int)response.StatusCode;
            // Read the error body so the user can see WHY the OCR call failed
            // (e.g. wrong model for this provider, unsupported param). Without it
            // a 400 is opaque. Best-effort: never let a read failure mask the code.
            string detail;
            try
            {
                detail = await response.Content
                    .ReadAsStringAsync(timeout.Token)
                    .ConfigureAwait(false);
            }
            catch
            {
                detail = string.Empty;
            }

            response.Dispose();

            string baseMsg = code == 401 ? "API 密钥无效或已过期，请重新设置。" :
                code == 429 ? "请求过于频繁，请稍后重试。" :
                $"OCR 服务暂不可用（HTTP {code}）。";
            throw new TranslationProviderException(
                string.IsNullOrWhiteSpace(detail) ? baseMsg : $"{baseMsg}\n服务返回：{detail}");
        }

        Stream bodyStream;
        try
        {
            bodyStream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or JsonException)
        {
            response.Dispose();
            throw new TranslationProviderException("OCR 服务返回了无法识别的数据。");
        }

        var sb = new StringBuilder();
        try
        {
            await foreach (var delta in OpenAiChatStream
                .EnumerateDeltasAsync(bodyStream, cancellationToken)
                .ConfigureAwait(false))
            {
                sb.Append(delta.Content);
            }
        }
        finally
        {
            await bodyStream.DisposeAsync().ConfigureAwait(false);
            response.Dispose();
        }

        return CleanOcrText(sb.ToString());
    }

    /// <summary>
    /// Diagnostic variant of <see cref="RecognizeAsync"/>: captures a screen
    /// region, sends the OCR request, and returns the <b>raw HTTP body</b>
    /// (the unparsed SSE event stream as text) plus the cleaned text. Used by
    /// the <c>--probe-ocr-raw</c> CLI probe to answer "is the extra text model
    /// output, an SSE parsing bug, or client-side concatenation?". Never throws
    /// on non-2xx — the raw error body is returned in <see cref="OcrRawResult.RawBody"/>.
    /// </summary>
    public async Task<OcrRawResult> RecognizeRawAsync(
        string imageDataUri, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(imageDataUri);

        string apiKey = await ResolveApiKeyAsync(cancellationToken).ConfigureAwait(false);
        string requestUri = ProviderUriBuilder.Build(_options.BaseUrl, _options.ChatCompletionsPath);
        byte[] body = BuildRequestBody(imageDataUri, _options, _ocrPrompt, _disableThinking);

        using var message = new HttpRequestMessage(HttpMethod.Post, requestUri);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        message.Headers.UserAgent.ParseAdd("BYH/0.1 (selection translation)");
        message.Content = new ByteArrayContent(body);
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.Timeout);

        HttpResponseMessage response = await _httpClient
            .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
            .ConfigureAwait(false);

        string rawBody;
        try
        {
            rawBody = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        }
        finally
        {
            response.Dispose();
        }

        // If 2xx, also re-parse with the same SSE layer the real client uses,
        // so the probe can show side-by-side: raw server bytes vs aggregated
        // deltas vs final cleaned text.
        string? cleaned = null;
        if (response.IsSuccessStatusCode)
        {
            var sb = new StringBuilder();
            using var ms = new MemoryStream(Encoding.UTF8.GetBytes(rawBody));
            await foreach (var delta in OpenAiChatStream
                .EnumerateDeltasAsync(ms, cancellationToken).ConfigureAwait(false))
            {
                sb.Append(delta.Content);
            }
            cleaned = CleanOcrText(sb.ToString());
        }

        return new OcrRawResult(
            StatusCode: (int)response.StatusCode,
            RawBody: rawBody,
            CleanedText: cleaned);
    }

    /// <summary>
    /// Removes reasoning noise that some vision models emit on extraction
    /// prompts: closed <c>&lt;think&gt;...&lt;/think&gt;</c> blocks, an
    /// unterminated <c>&lt;think&gt;</c> opening tag (stream truncated mid-
    /// reasoning), or a dangling <c>&lt;/think&gt;</c> close with no opening
    /// (the opening lived in a chunk the SSE layer dropped). Collapses runs of
    /// whitespace left behind. The OCR result for "free OCR." prompts is pure
    /// visible text — anything else here is model leakage.
    /// </summary>
    public static string CleanOcrText(string raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return string.Empty;
        }

        string cleaned = ThinkBlockPattern().Replace(raw, string.Empty);

        // Unterminated <think> at the end: drop everything from the tag to EOF
        // (the response cut off mid-reasoning, the visible answer never came).
        int openIdx = cleaned.LastIndexOf("<think>", StringComparison.Ordinal);
        if (openIdx >= 0 && cleaned.IndexOf("</think>", openIdx, StringComparison.Ordinal) < 0)
        {
            cleaned = cleaned[..openIdx];
        }

        // Dangling </think> with no opening (opening tag was in a lost chunk).
        cleaned = cleaned.Replace("</think>", string.Empty);

        return cleaned.Trim();
    }

    private async Task<string> ResolveApiKeyAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(_options.ApiKeyReference))
        {
            throw new TranslationProviderException("未配置 API 密钥引用。");
        }

        string? key = await _secretStore
            .GetAsync(_options.ApiKeyReference, cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(key))
        {
            throw new TranslationProviderException(
                $"未设置密钥。请运行：BYH.exe --set-secret {_options.ApiKeyReference} <你的key>");
        }

        return key;
    }

    // Multimodal request: a user message whose content is a [text, image_url]
    // array. DeepSeek-OCR / PaddleOCR-VL / Qwen-VL / GPT-4o all accept this shape.
    //
    // Thinking control: hybrid reasoning vision models (Qwen3.x, DeepSeek-VL)
    // spend seconds generating reasoning_content before the actual answer —
    // visible to the user as latency, even though we discard reasoning_content
    // (the SSE parser only reads delta.content, not delta.reasoning_content).
    // Sending enable_thinking:false tells the model to skip reasoning and
    // answer directly. Pure OCR models (DeepSeek-OCR, PaddleOCR-VL) reject
    // unknown params with HTTP 400 (SiliconFlow code 20015), so this is opt-in
    // per model via VisionCaptureSettings.DisableThinking.
    private static byte[] BuildRequestBody(
        string imageDataUri,
        OpenAiCompatibleProviderOptions options,
        string ocrPrompt,
        bool disableThinking)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);

        writer.WriteStartObject();
        writer.WriteString("model", options.DefaultModel);
        writer.WriteBoolean("stream", true);
        if (disableThinking)
        {
            // SiliconFlow / Qwen convention. Other providers may use "thinking":
            // false or "chat_template_kwargs": {"enable_thinking": false}. The
            // Qwen/SiliconFlow shape covers our current provider set; expand
            // here if a future provider needs a different key.
            writer.WriteBoolean("enable_thinking", false);
        }

        writer.WriteStartArray("messages");

        writer.WriteStartObject();
        writer.WriteString("role", "user");

        writer.WriteStartArray("content");

        writer.WriteStartObject();
        writer.WriteString("type", "text");
        writer.WriteString("text", ocrPrompt);
        writer.WriteEndObject();

        writer.WriteStartObject();
        writer.WriteString("type", "image_url");
        writer.WriteStartObject("image_url");
        writer.WriteString("url", imageDataUri);
        writer.WriteEndObject();
        writer.WriteEndObject();

        writer.WriteEndArray(); // content
        writer.WriteEndObject(); // message

        writer.WriteEndArray(); // messages
        writer.WriteEndObject();
        writer.Flush();

        return stream.ToArray();
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

/// <summary>
/// Diagnostic result of <see cref="OpenAiCompatibleVisionOcrClient.RecognizeRawAsync"/>.
/// Carries the raw HTTP body (so the user can see exactly what the model
/// returned, including reasoning tags / SSE framing) and, on 2xx, the cleaned
/// text the real client would surface.
/// </summary>
public sealed record OcrRawResult(int StatusCode, string RawBody, string? CleanedText);
