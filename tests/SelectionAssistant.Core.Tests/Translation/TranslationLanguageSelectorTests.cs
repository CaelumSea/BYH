using SelectionAssistant.Core.Translation;
using Xunit;

namespace SelectionAssistant.Core.Tests.Translation;

/// <summary>
/// Audit M11: the selector is a binary router (CJK-family → en, otherwise →
/// zh-CN). These tests pin the routing for the script blocks it must detect.
/// </summary>
[Trait("Category", "Translation")]
public sealed class TranslationLanguageSelectorTests
{
    [Theory]
    [InlineData("你好世界")]              // Chinese (CJK Unified Ideographs)
    [InlineData("ありがとう")]            // Japanese — pure Hiragana (audit M11 regression case)
    [InlineData("アリガトウ")]            // Japanese — pure Katakana
    [InlineData("今日はいい天気ですね")]   // Japanese — Kanji + Hiragana mix
    [InlineData("감사합니다")]            // Korean — pure Hangul Syllables (audit M11 regression case)
    [InlineData("안녕하세요 세계")]       // Korean — Hangul + space
    public void CjkFamilyText_RoutesToEnglish(string text)
    {
        var request = TranslationLanguageSelector.CreateRequest(text);
        Assert.Equal("en", request.TargetLanguage);
        Assert.Equal("zh-CN", request.SourceLanguage);
    }

    [Theory]
    [InlineData("Hello world")]          // English
    [InlineData("Bonjour le monde")]     // French
    [InlineData("Hola mundo")]           // Spanish
    [InlineData("Привет мир")]           // Cyrillic — not CJK, routes to zh-CN
    public void NonCjkText_RoutesToChinese(string text)
    {
        var request = TranslationLanguageSelector.CreateRequest(text);
        Assert.Equal("zh-CN", request.TargetLanguage);
        Assert.Equal("en", request.SourceLanguage);
    }

    [Fact]
    public void EmptyString_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            TranslationLanguageSelector.CreateRequest(""));
        Assert.Throws<ArgumentException>(() =>
            TranslationLanguageSelector.CreateRequest("   "));
    }
}
