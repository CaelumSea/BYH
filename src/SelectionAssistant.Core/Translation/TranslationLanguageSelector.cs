namespace SelectionAssistant.Core.Translation;

/// <summary>
/// Minimal v0.1 language routing: Chinese text goes to English; other text is
/// treated as English and goes to Simplified Chinese. A detector can replace
/// this class without changing the provider or UI contracts.
/// </summary>
public static class TranslationLanguageSelector
{
    public static TranslationRequest CreateRequest(string sourceText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceText);

        string normalized = sourceText.Trim();
        return ContainsCjkUnifiedIdeograph(normalized)
            ? new TranslationRequest(normalized, "zh-CN", "en")
            : new TranslationRequest(normalized, "en", "zh-CN");
    }

    private static bool ContainsCjkUnifiedIdeograph(string text)
    {
        foreach (char value in text)
        {
            if (value is >= '\u3400' and <= '\u4DBF' or
                >= '\u4E00' and <= '\u9FFF' or
                >= '\uF900' and <= '\uFAFF')
            {
                return true;
            }
        }

        return false;
    }
}
