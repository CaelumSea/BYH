namespace SelectionAssistant.Providers;

/// <summary>
/// Configuration for an OpenAI-compatible chat-completion provider. Mirrors the
/// fields of a single entry in <c>providers.json</c> (§9.2). Secrets are never
/// held here — only a <see cref="ApiKeyReference" /> URI resolved at runtime by
/// <c>ISecretStore</c>.
/// </summary>
public sealed record OpenAiCompatibleProviderOptions
{
    /// <summary>Stable identifier (e.g. "deepseek"). Used in secret references.</summary>
    public required string Id { get; init; }

    /// <summary>Display name shown in the result window badge.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Base URL ending in /v1 (or equivalent), e.g. "https://api.deepseek.com/v1".</summary>
    public required string BaseUrl { get; init; }

    /// <summary><c>secret://</c> reference resolved by ISecretStore. Null for local no-auth providers.</summary>
    public string? ApiKeyReference { get; init; }

    /// <summary>Model id passed in the request body, e.g. "deepseek-chat".</summary>
    public required string DefaultModel { get; init; }

    /// <summary>Path appended to BaseUrl, default "chat/completions". §9.3 URI-aware join.</summary>
    public string ChatCompletionsPath { get; init; } = "chat/completions";

    /// <summary>Per-request timeout. Default 60s for LLM streaming.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Soft character cap on source text to avoid sending unbounded input (§11.2).</summary>
    public int MaxSourceCharacters { get; init; } = 8000;

    /// <summary>
    /// Optional override of the built-in translation system prompt. When null
    /// or whitespace, the provider falls back to its built-in translation
    /// template. Set per-provider for custom actions, or override per-request
    /// via <see cref="TranslationRequest.SystemPrompt" />.
    /// </summary>
    public string? SystemPrompt { get; init; }
}
