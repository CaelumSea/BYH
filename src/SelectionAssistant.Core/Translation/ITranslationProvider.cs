namespace SelectionAssistant.Core.Translation;

/// <summary>A replaceable, non-UI translation backend.</summary>
public interface ITranslationProvider
{
    string DisplayName { get; }

    Task<TranslationResult> TranslateAsync(
        TranslationRequest request,
        CancellationToken cancellationToken);
}

public sealed record TranslationRequest(
    string SourceText,
    string SourceLanguage,
    string TargetLanguage)
{
    /// <summary>
    /// Optional per-request override of the system prompt. When null, the
    /// provider's configured default is used (or the built-in translation
    /// template). The "Prompt Now" flow (R2) sets this to the user's typed
    /// prompt so a custom action runs without changing provider config.
    /// </summary>
    public string? SystemPrompt { get; init; }

    /// <summary>
    /// Per-request thinking-mode flag, the single source of truth for whether
    /// the model may reason before answering. Defaults to <c>false</c>. Set by
    /// the runtime from the action's prompt template — not the provider — so
    /// the same provider can think for "explain" but not "translate".
    /// </summary>
    public bool ThinkingEnabled { get; init; }
}

public sealed record TranslationResult(
    string TranslatedText,
    string SourceLanguage,
    string TargetLanguage,
    string ProviderName);

public sealed class TranslationProviderException : Exception
{
    public TranslationProviderException(string userMessage)
        : base(userMessage)
    {
        UserMessage = userMessage;
    }

    public TranslationProviderException(string userMessage, Exception innerException)
        : base(userMessage, innerException)
    {
        UserMessage = userMessage;
    }

    public string UserMessage { get; }
}
