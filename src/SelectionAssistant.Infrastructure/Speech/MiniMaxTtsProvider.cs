using System.IO;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using SelectionAssistant.Core.Speech;

namespace SelectionAssistant.Infrastructure.Speech;

/// <summary>
/// Script classification of TTS input text, used to pick the per-script voice.
/// <see cref="MiniMaxTtsProvider.ClassifyScript"/> maps text → this enum.
/// </summary>
public enum ScriptKind
{
    /// <summary>Text has CJK ideographs but no Latin letters.</summary>
    Chinese,
    /// <summary>Text has no CJK ideographs (pure Latin/English).</summary>
    English,
    /// <summary>Text has both CJK ideographs and Latin letters.</summary>
    Mixed,
}

/// <summary>
/// Text-to-speech provider backed by MiniMax's T2A REST API
/// (<c>POST /v1/t2a_v2</c>). Mirrors <c>MyMemoryTranslationProvider</c>'s
/// pattern: sealed, owns its <see cref="HttpClient"/>, disposable. Returns the
/// synthesized audio as a raw MP3 byte array; the caller plays it.
/// <para>
/// <b>Critical gotcha</b>: MiniMax returns audio bytes <b>hex-encoded</b> in
/// <c>data.audio</c> (NOT base64, unlike most APIs). See <see cref="HexToBytes"/>.
/// </para>
/// </summary>
public sealed class MiniMaxTtsProvider : IDisposable
{
    private const string HostGlobal = "https://api.minimax.io";
    private const string HostCn = "https://api.minimaxi.com"; // note: minimax<i>.com
    private const string T2aPath = "/v1/t2a_v2";

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;
    private readonly TimeSpan _timeout;
    private int _disposed;

    public MiniMaxTtsProvider(HttpClient? httpClient = null, TimeSpan? timeout = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _ownsClient = httpClient is null;
        _timeout = timeout ?? DefaultTimeout;
        if (_timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }
    }

    /// <summary>Host chosen for the region. Public so tests can assert the
    /// global/cn split (note the cn host is minimax<b>i</b>.com).</summary>
    public static string HostForRegion(string region) =>
        string.Equals(region?.Trim(), "cn", StringComparison.OrdinalIgnoreCase)
            ? HostCn
            : HostGlobal;

    /// <summary>
    /// Synthesizes <paramref name="text"/> to MP3 bytes via MiniMax T2A.
    /// </summary>
    /// <param name="apiKey">MiniMax API key (or OAuth access token) — Bearer auth.</param>
    /// <exception cref="TtsException">Any synthesis failure (network, auth, quota, content filter, malformed response).</exception>
    public async Task<byte[]> SynthesizeAsync(
        string text,
        TtsSettings settings,
        string apiKey,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        // Resolve final voice + language boost from the auto/content heuristic.
        (string voiceId, string? languageBoost) = ResolveVoice(text, settings);

        // Build the T2A request body via hand-written Utf8JsonWriter. We avoid
        // JsonSerializer.Serialize<T> here because reflection-based JSON is
        // flagged by AOT trim analysis (IL2026/IL3050) and could fail at runtime
        // under PublishAot=true + TrimMode=full. Manual writing mirrors the
        // rest of this codebase (VisionCaptureStore, ProviderConfiguration) and
        // costs ~20 lines for full AOT safety. output_format:"hex" is mandatory
        // for synthesize (mmx forces it); stream:false returns the whole audio
        // in one JSON response.
        string body = BuildRequestBody(settings.Model, text, voiceId, settings.Speed, languageBoost);

        using var message = new HttpRequestMessage(HttpMethod.Post, HostForRegion(settings.Region) + T2aPath)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        message.Headers.UserAgent.ParseAdd("BYH/0.1 (selection tts)");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_timeout);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient
                .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TtsException("语音合成响应超时，请重试。");
        }
        catch (HttpRequestException)
        {
            throw new TtsException("无法连接语音服务，请检查网络。");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new TtsException(MapHttpError((int)response.StatusCode));
            }

            // Read the full JSON response. MiniMax returns ~10-50 KB of hex for a
            // sentence, so ReadAsStreamAsync + ParseAsync keeps it cheap.
            try
            {
                await using Stream bodyStream = await response.Content
                    .ReadAsStreamAsync(timeoutCts.Token)
                    .ConfigureAwait(false);
                using JsonDocument document = await JsonDocument
                    .ParseAsync(bodyStream, cancellationToken: timeoutCts.Token)
                    .ConfigureAwait(false);

                JsonElement root = document.RootElement;

                // base_resp.status_code == 0 means success; non-zero is a server
                // error (quota, content filter, invalid voice, etc.).
                int statusCode = ReadInt(root, "base_resp", "status_code");
                if (statusCode != 0)
                {
                    string statusMsg = ReadString(root, "base_resp", "status_msg")
                        ?? "语音合成失败";
                    throw new TtsException(MapServerError(statusCode, statusMsg));
                }

                if (!root.TryGetProperty("data", out JsonElement data) ||
                    !data.TryGetProperty("audio", out JsonElement audio) ||
                    audio.ValueKind != JsonValueKind.String)
                {
                    throw new TtsException("语音服务未返回音频数据。");
                }

                string? hex = audio.GetString();
                if (string.IsNullOrEmpty(hex))
                {
                    throw new TtsException("语音服务返回的音频为空。");
                }

                // CRITICAL: decode hex (not base64). MiniMax's documented quirk.
                try
                {
                    return HexToBytes(hex);
                }
                catch (FormatException exception)
                {
                    throw new TtsException("语音服务返回的音频数据格式异常。", exception);
                }
            }
            catch (JsonException exception)
            {
                throw new TtsException("语音服务返回了无法识别的数据。", exception);
            }
        }
    }

    /// <summary>
    /// Picks the final voice id (and optional language_boost) from settings by
    /// classifying the text's script: pure Chinese (CJK, no Latin) →
    /// <see cref="TtsSettings.ChineseVoice"/> + language_boost <c>"zh"</c>;
    /// pure English (no CJK) → <see cref="TtsSettings.EnglishVoice"/> with no
    /// boost; mixed (both CJK and Latin) → <see cref="TtsSettings.MixedVoice"/>
    /// with no boost (cross-lingual voices handle the language switch themselves).
    /// </summary>
    public static (string VoiceId, string? LanguageBoost) ResolveVoice(string text, TtsSettings settings)
    {
        return ClassifyScript(text) switch
        {
            ScriptKind.Chinese => (settings.ChineseVoice, "zh"),
            ScriptKind.Mixed => (settings.MixedVoice, null),
            _ => (settings.EnglishVoice, null),
        };
    }

    /// <summary>
    /// Classifies text into a script bucket by presence of CJK ideographs vs
    /// Latin letters (ratios are ignored — only "is there any"). Has CJK and no
    /// Latin → <see cref="ScriptKind.Chinese"/>; has CJK and Latin →
    /// <see cref="ScriptKind.Mixed"/> (e.g. "今天用了 iPhone 感觉很 nice");
    /// no CJK → <see cref="ScriptKind.English"/> (pure-Latin text, including a
    /// stray CJK char only inside whitespace-free noise, still counts as English
    /// when it has zero CJK). Empty/whitespace → English (a sane default, since
    /// synthesis of nothing is a no-op anyway). Public so tests drive the
    /// classifier directly.
    /// </summary>
    public static ScriptKind ClassifyScript(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return ScriptKind.English;
        }
        bool hasCjk = false;
        bool hasLatin = false;
        foreach (char c in text)
        {
            if (c >= '\u4E00' && c <= '\u9FFF')
            {
                hasCjk = true;
            }
            else if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'))
            {
                hasLatin = true;
            }
        }
        return (hasCjk, hasLatin) switch
        {
            (true, false) => ScriptKind.Chinese,
            (true, true) => ScriptKind.Mixed,
            _ => ScriptKind.English,
        };
    }

    /// <summary>
    /// Decodes a lowercase/uppercase hex string into a byte array. MiniMax's
    /// <c>data.audio</c> is hex-encoded (NOT base64). Throws
    /// <see cref="FormatException"/> on odd length or non-hex chars.
    /// </summary>
    /// <summary>
    /// Decodes a lowercase/uppercase hex string into a byte array. MiniMax's
    /// <c>data.audio</c> is hex-encoded (NOT base64). Throws
    /// <see cref="FormatException"/> on odd length or non-hex chars. Public so
    /// tests can verify the hex (not base64!) decode path directly.
    /// </summary>
    public static byte[] HexToBytes(string hex)
    {
        ArgumentNullException.ThrowIfNull(hex);
        // Convert.FromHexString exists on .NET 5+ and is AOT-safe + bounds-checked.
        // It throws FormatException on odd length or non-hex chars, which callers
        // catch and map to a TtsException.
        return Convert.FromHexString(hex);
    }

    private static string MapHttpError(int httpStatus) => httpStatus switch
    {
        401 or 403 => "MiniMax 密钥无效或已过期。若使用 mmx 登录的密钥，请运行 mmx auth login 重新登录。",
        429 => "语音服务请求过于频繁，请稍后再试。",
        >= 500 => "语音服务暂时不可用（HTTP " + httpStatus + "），请稍后再试。",
        _ => "语音服务返回错误（HTTP " + httpStatus + "）。",
    };

    private static string MapServerError(int statusCode, string statusMsg) => statusCode switch
    {
        1002 or 1039 => "内容审核未通过，无法朗读所选文字。",
        1028 or 1030 => "MiniMax 语音配额已耗尽。",
        2061 => "当前账号不支持所选语音模型。",
        _ => $"语音合成失败（{statusCode}）：{statusMsg}",
    };

    private static int ReadInt(JsonElement root, params string[] path)
    {
        JsonElement current = root;
        foreach (string segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object ||
                !current.TryGetProperty(segment, out current))
            {
                return 0;
            }
        }
        return current.ValueKind == JsonValueKind.Number && current.TryGetInt32(out int n) ? n : 0;
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
        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
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

    /// <summary>
    /// Builds the T2A request JSON via <see cref="Utf8JsonWriter"/> (reflection-
    /// free, AOT-safe). The wire format mirrors what the mmx CLI sends:
    /// <c>{ model, text, voice_setting:{voice_id,speed}, audio_setting:{format,
    /// sample_rate,bitrate,channel}, output_format:"hex", stream:false,
    /// language_boost? }</c>.
    /// </summary>
    private static string BuildRequestBody(
        string model, string text, string voiceId, double speed, string? languageBoost)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("model", model);
            writer.WriteString("text", text);

            writer.WriteStartObject("voice_setting");
            writer.WriteString("voice_id", voiceId);
            writer.WriteNumber("speed", speed);
            writer.WriteEndObject();

            writer.WriteStartObject("audio_setting");
            writer.WriteString("format", "mp3");
            writer.WriteNumber("sample_rate", 32000);
            writer.WriteNumber("bitrate", 128000);
            writer.WriteNumber("channel", 1);
            writer.WriteEndObject();

            writer.WriteString("output_format", "hex");
            writer.WriteBoolean("stream", false);
            if (!string.IsNullOrEmpty(languageBoost))
            {
                writer.WriteString("language_boost", languageBoost);
            }
            writer.WriteEndObject();
            writer.Flush();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}

/// <summary>Thrown when MiniMax TTS synthesis fails for any reason.</summary>
public sealed class TtsException : Exception
{
    public TtsException(string message) : base(message) { }
    public TtsException(string message, Exception innerException) : base(message, innerException) { }
}
