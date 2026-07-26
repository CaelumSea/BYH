namespace SelectionAssistant.Core.Translation;

/// <summary>
/// Minimal v0.1 language routing: text containing CJK ideographs OR Japanese
/// kana OR Korean Hangul goes to English; everything else is treated as
/// (Latin-script) English and goes to Simplified Chinese. A detector can
/// replace this class without changing the provider or UI contracts.
/// </summary>
/// <remarks>
/// <b>Scope (audit M11)</b>: this is a BINARY router (CJK-family → en,
/// otherwise → zh-CN), not a multi-language detector. It does not distinguish
/// Chinese from Japanese from Korean — all three route the same way (to
/// English), because the default BYH user is Chinese and the only "other"
/// target in the v0.1 contract is English. The original implementation only
/// checked CJK Unified Ideographs, which missed pure-kana Japanese
/// (e.g. "ありがとう") and pure-Hangul Korean (e.g. "감사합니다") — those fell
/// through to the "→ zh-CN" branch, which is almost never what a Japanese or
/// Korean user wants. Extending the check to cover Hiragana/Katakana and
/// Hangul Syllables routes those correctly without introducing a third
/// language pair.
/// </remarks>
public static class TranslationLanguageSelector
{
    public static TranslationRequest CreateRequest(string sourceText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceText);

        string normalized = sourceText.Trim();
        return ContainsCjkOrJapaneseKanaOrHangul(normalized)
            ? new TranslationRequest(normalized, "zh-CN", "en")
            : new TranslationRequest(normalized, "en", "zh-CN");
    }

    private static bool ContainsCjkOrJapaneseKanaOrHangul(string text)
    {
        foreach (char value in text)
        {
            // CJK Unified Ideographs (incl. Ext A + Compatibility).
            if (value is >= '\u3400' and <= '\u4DBF' or
                >= '\u4E00' and <= '\u9FFF' or
                >= '\uF900' and <= '\uFAFF')
            {
                return true;
            }
            // Audit M11: Japanese Hiragana (U+3040–U+309F) + Katakana
            // (U+30A0–U+30FF) + CJK Symbols and Punctuation (U+3000–U+303F,
            // covers the full-width punctuation Japanese text uses).
            if (value is >= '\u3040' and <= '\u30FF')
            {
                return true;
            }
            // Audit M11: Korean Hangul Syllables (U+AC00–U+D7AF). Hangul
            // Jamo (U+1100–U+11FF) and Compat Jamo (U+3130–U+318F) are rarer
            // in user-facing text; the Syllables block covers >99% of modern
            // Korean. Add Jamo if a real corpus demands it.
            if (value is >= '\uAC00' and <= '\uD7AF')
            {
                return true;
            }
        }

        return false;
    }
}
