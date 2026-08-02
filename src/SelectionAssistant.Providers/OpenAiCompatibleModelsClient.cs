using System.Net.Http.Headers;
using System.Text.Json;
using SelectionAssistant.Core.Translation;
using SelectionAssistant.Platform.Abstractions.Secrets;

namespace SelectionAssistant.Providers;

/// <summary>
/// Fetches a provider's model catalogue via the OpenAI-compatible
/// <c>GET {BaseUrl}/models</c> endpoint (§9.1). Used by the settings UI's
/// "Refresh Models" button so the user can pick a current upstream model id
/// without editing code. The response is parsed with <see cref="JsonDocument"/>
/// (no reflection) so it is NativeAOT/trim-safe.
/// <para>
/// Security invariants mirror <see cref="OpenAiCompatibleStreamingProvider"/>:
/// redirects disabled, no TLS-disable option, bearer never forwarded to a
/// different host (§9.4); the API key is resolved from a secret store, never
/// read from plaintext config (§11.3).
/// </para>
/// </summary>
public sealed class OpenAiCompatibleModelsClient : IDisposable
{
    private readonly OpenAiCompatibleProviderOptions _options;
    private readonly ISecretStore _secretStore;
    private readonly string? _apiKeyOverride;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;
    private int _disposed;

    public OpenAiCompatibleModelsClient(
        OpenAiCompatibleProviderOptions options,
        ISecretStore secretStore,
        HttpClient? httpClient = null,
        string? apiKeyOverride = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
        _apiKeyOverride = string.IsNullOrWhiteSpace(apiKeyOverride) ? null : apiKeyOverride;

        if (httpClient is null)
        {
            // §9.4: disable redirects by default. There is intentionally no way
            // to disable TLS verification here. Mirrors the streaming provider.
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

    /// <summary>
    /// Fetches <c>GET {BaseUrl}/models</c>, parses the standard OpenAI
    /// <c>{ "data": [{ "id": "..." }] }</c> shape, and returns the model ids
    /// sorted (OrdinalIgnoreCase) and de-duplicated. Null/blank ids are
    /// dropped. An empty <c>data</c> array yields an empty list (not an error).
    /// </summary>
    /// <exception cref="TranslationProviderException">
    /// Thrown on auth failure (401), rate-limit (429), other non-2xx, timeout,
    /// connection error, or malformed JSON. The message is user-facing.
    /// </exception>
    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        string apiKey = await ResolveApiKeyAsync(cancellationToken).ConfigureAwait(false);
        string requestUri = ProviderUriBuilder.Build(_options.BaseUrl, "models");

        using var message = new HttpRequestMessage(HttpMethod.Get, requestUri);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        message.Headers.UserAgent.ParseAdd("BYH/0.1 (selection translation)");

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
            throw new TranslationProviderException("拉取模型列表超时，请重试。");
        }
        catch (HttpRequestException)
        {
            throw new TranslationProviderException("无法连接服务，请检查网络或 API 地址。");
        }

        try
        {
            if (!response.IsSuccessStatusCode)
            {
                int code = (int)response.StatusCode;
                // Read the error body so the user can see WHY the call failed
                // (wrong scope, no models permission, ...). Best-effort: never
                // let a read failure mask the code. Mirrors the OCR client.
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

                string baseMsg = code == 401 ? "API 密钥无效或未授权。" :
                    code == 429 ? "请求频率超限，请稍后再试。" :
                    $"拉取模型列表失败（HTTP {code}）。";
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
                throw new TranslationProviderException("服务返回了无法识别的数据。");
            }

            // Parse { "data": [ { "id": "..." }, ... ] } with JsonDocument
            // (reflection-free → AOT/trim-safe). Read into a HashSet first for
            // O(1) dedup, then materialize a sorted list. We do NOT assume the
            // server returns the OpenAI shape verbatim — tolerate a missing
            // "data" wrapper by treating the root object itself as the array
            // only if it is an array (some non-conformant gateways do this).
            var ids = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                using var doc = await JsonDocument.ParseAsync(bodyStream, cancellationToken: timeout.Token).ConfigureAwait(false);

                JsonElement arrayElement;
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    arrayElement = doc.RootElement;
                }
                else if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                         doc.RootElement.TryGetProperty("data", out JsonElement dataEl) &&
                         dataEl.ValueKind == JsonValueKind.Array)
                {
                    arrayElement = dataEl;
                }
                else
                {
                    // Unknown shape — surface as a parse error so the user can
                    // see the response and file a bug rather than silently
                    // getting an empty list.
                    throw new TranslationProviderException("服务返回的模型列表格式无法识别（缺少 data 数组）。");
                }

                foreach (JsonElement item in arrayElement.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) { continue; }
                    if (!item.TryGetProperty("id", out JsonElement idEl)) { continue; }
                    if (idEl.ValueKind != JsonValueKind.String) { continue; }
                    string? id = idEl.GetString();
                    if (!string.IsNullOrWhiteSpace(id))
                    {
                        ids.Add(id.Trim());
                    }
                }
            }
            catch (TranslationProviderException) { throw; }
            catch (JsonException)
            {
                throw new TranslationProviderException("服务返回的不是合法 JSON。");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TranslationProviderException("拉取模型列表超时，请重试。");
            }

            if (ids.Count == 0)
            {
                return Array.Empty<string>();
            }

            var result = new string[ids.Count];
            ids.CopyTo(result);
            Array.Sort(result, StringComparer.OrdinalIgnoreCase);
            return result;
        }
        finally
        {
            response.Dispose();
        }
    }

    private async Task<string> ResolveApiKeyAsync(CancellationToken cancellationToken)
    {
        // Settings can test a not-yet-saved Custom Provider. Keep the entered
        // key only in this short-lived client; it is never written to config,
        // the model cache, or logs. A blank override falls back to DPAPI for
        // existing providers.
        if (_apiKeyOverride is not null)
        {
            return _apiKeyOverride;
        }

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
