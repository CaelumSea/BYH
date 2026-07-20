namespace SelectionAssistant.Core.Translation;

/// <summary>
/// A built-in vendor template (baseUrl + default model) that users can pick
/// when adding a new provider, so they only need to fill in the API key.
/// Inspired by CC Switch's seed providers, but trimmed to the OpenAI-compatible
/// chat-completion fields BYH uses.
/// </summary>
public sealed record ProviderPreset(
    string Id,
    string Name,
    string BaseUrl,
    string DefaultModel,
    string ChatCompletionsPath);

/// <summary>
/// Built-in vendor presets for the "add provider" flow. Each preset fills in
/// the known baseUrl + model so the user only needs to paste an API key.
/// The preset id doubles as the provider id and the secret reference suffix
/// (secret://provider/{Id}).
/// </summary>
public static class ProviderPresets
{
    public static readonly IReadOnlyList<ProviderPreset> BuiltIn =
    [
        new("deepseek", "DeepSeek",
            "https://api.deepseek.com", "deepseek-v4-flash", "chat/completions"),
        new("siliconflow", "SiliconFlow",
            "https://api.siliconflow.cn/v1", "deepseek-ai/DeepSeek-V3", "chat/completions"),
        new("openai", "OpenAI",
            "https://api.openai.com/v1", "gpt-4o-mini", "chat/completions"),
        new("zhipu", "Zhipu GLM",
            "https://open.bigmodel.cn/api/paas/v4", "glm-4-flash", "chat/completions"),
        new("moonshot", "Moonshot Kimi",
            "https://api.moonshot.cn/v1", "moonshot-v1-8k", "chat/completions"),
    ];

    /// <summary>Special id for fully-custom providers (no preset template).</summary>
    public const string CustomPresetId = "custom";

    /// <summary>
    /// Builds the secret reference for a provider id. All keys are addressed
    /// uniformly so the DPAPI store can find them.
    /// </summary>
    public static string BuildSecretReference(string providerId) =>
        $"secret://provider/{providerId}";
}
