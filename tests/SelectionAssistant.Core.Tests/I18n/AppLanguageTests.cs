using System.Globalization;
using SelectionAssistant.Core.I18n;
using Xunit;

namespace SelectionAssistant.Core.Tests.I18n;

public sealed class AppLanguageTests
{
    [Theory]
    [InlineData("zh-CN", true)]
    [InlineData("zh-Hans", true)]
    [InlineData("zh-Hant", true)]
    [InlineData("zh-SG", true)]
    [InlineData("zh-TW", true)]
    [InlineData("ZH-cn", true)]    // case-insensitive
    [InlineData("en-US", false)]
    [InlineData("en", false)]
    [InlineData("ja-JP", false)]
    [InlineData("de-DE", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void FromCultureName_MapsChineseVariantsAndOthers(string? name, bool expectChinese)
    {
        AppLanguage lang = AppLanguage.FromCultureName(name);
        Assert.Equal(expectChinese, lang.IsChinese);
    }

    [Fact]
    public void DetectFromOS_ReturnsSupportedLanguage()
    {
        // Whatever the OS culture is, DetectFromOS must always return one of
        // the two supported languages (never throw, never return an arbitrary
        // code). This is the contract the App startup depends on.
        AppLanguage lang = AppLanguage.DetectFromOS();
        Assert.Contains(lang, AppLanguage.Supported);
    }

    [Fact]
    public void Supported_ContainsExactlyEnglishAndChinese()
    {
        Assert.Equal(2, AppLanguage.Supported.Count);
        Assert.Equal(AppLanguage.English, AppLanguage.Supported[0]);
        Assert.Equal(AppLanguage.Chinese, AppLanguage.Supported[1]);
    }

    [Fact]
    public void Codes_AreStableIdentifiers()
    {
        // Persisted to ui-language.json — never change these without a
        // migration. Lock them down so a careless rename breaks the test
        // suite before it ships.
        Assert.Equal("en", AppLanguage.English.Code);
        Assert.Equal("zh-CN", AppLanguage.Chinese.Code);
    }

    [Fact]
    public void Set_UpdatesCurrent()
    {
        AppLanguage original = AppLanguage.Current;
        try
        {
            AppLanguage.Set(AppLanguage.Chinese);
            Assert.Equal(AppLanguage.Chinese, AppLanguage.Current);

            AppLanguage.Set(AppLanguage.English);
            Assert.Equal(AppLanguage.English, AppLanguage.Current);
        }
        finally
        {
            // Restore — Strings' static dict was already initialized against
            // the original Current, so we must not leave a different value
            // for tests that run later in the same process.
            AppLanguage.Set(original);
        }
    }

    [Fact]
    public void Set_RejectsNull()
    {
        Assert.Throws<ArgumentNullException>(() => AppLanguage.Set(null!));
    }

    [Fact]
    public void RecordEquality_ByCode()
    {
        // Two instances with the same code are equal — this is what lets the
        // SettingsWindow "same language as current" no-op-restart check work.
        Assert.Equal(new AppLanguage("en"), AppLanguage.English);
        Assert.Equal(new AppLanguage("zh-CN"), AppLanguage.Chinese);
        Assert.NotEqual(AppLanguage.English, AppLanguage.Chinese);
    }
}
