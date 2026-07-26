using System.IO;
using SelectionAssistant.Core.I18n;
using SelectionAssistant.Infrastructure.Configuration;
using Xunit;

namespace SelectionAssistant.Core.Tests.Configuration;

public sealed class UiLanguageStoreTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"ui-lang-{System.Guid.NewGuid():N}.json");

    [Fact]
    public void MissingFile_AutoDetectsFromOS()
    {
        // The language store differs from the other stores: a missing file
        // on first launch means "auto-detect from OS", not "fixed default".
        // DetectFromOS always returns one of the two supported languages.
        string path = Path.Combine(Path.GetTempPath(), $"missing-{System.Guid.NewGuid():N}.json");
        AppLanguage lang = UiLanguageStore.LoadIfExists(path);
        Assert.Contains(lang, AppLanguage.Supported);
    }

    [Fact]
    public void SaveThenEnglish_RoundTrips()
    {
        string path = TempPath();
        try
        {
            UiLanguageStore.Save(AppLanguage.English, path);
            AppLanguage loaded = UiLanguageStore.LoadIfExists(path);
            Assert.Equal(AppLanguage.English, loaded);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void SaveThenChinese_RoundTrips()
    {
        string path = TempPath();
        try
        {
            UiLanguageStore.Save(AppLanguage.Chinese, path);
            AppLanguage loaded = UiLanguageStore.LoadIfExists(path);
            Assert.Equal(AppLanguage.Chinese, loaded);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void MissingLanguageField_AutoDetectsFromOS()
    {
        // A file with a valid schemaVersion but no language field (or null)
        // degrades to OS detection rather than throwing.
        string path = TempPath();
        try
        {
            File.WriteAllText(path, "{\"schemaVersion\":1}");
            AppLanguage loaded = UiLanguageStore.LoadIfExists(path);
            Assert.Contains(loaded, AppLanguage.Supported);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void NullLanguageField_AutoDetectsFromOS()
    {
        string path = TempPath();
        try
        {
            File.WriteAllText(path, "{\"schemaVersion\":1,\"language\":null}");
            AppLanguage loaded = UiLanguageStore.LoadIfExists(path);
            Assert.Contains(loaded, AppLanguage.Supported);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Theory]
    // Hand-edited values must map to a supported language, not throw.
    [InlineData("zh-Hans", true)]    // alternate code → Chinese
    [InlineData("zh-TW", true)]      // alternate code → Chinese
    [InlineData("zh", true)]         // bare zh → Chinese
    [InlineData("en-US", false)]     // alternate code → English
    [InlineData("EN", false)]        // case-insensitive → English
    public void AlternateCodes_MapToSupportedLanguage(string rawCode, bool expectChinese)
    {
        string path = TempPath();
        try
        {
            File.WriteAllText(path, $"{{\"schemaVersion\":1,\"language\":\"{rawCode}\"}}");
            AppLanguage loaded = UiLanguageStore.LoadIfExists(path);
            Assert.Equal(expectChinese, loaded.IsChinese);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void BlankLanguageField_AutoDetectsFromOS()
    {
        string path = TempPath();
        try
        {
            File.WriteAllText(path, "{\"schemaVersion\":1,\"language\":\"\"}");
            AppLanguage loaded = UiLanguageStore.LoadIfExists(path);
            Assert.Contains(loaded, AppLanguage.Supported);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void WrongSchemaVersion_Throws()
    {
        string path = TempPath();
        try
        {
            File.WriteAllText(path, "{\"schemaVersion\":99,\"language\":\"en\"}");
            Assert.Throws<ProviderConfigurationException>(
                () => UiLanguageStore.LoadIfExists(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void OversizedFile_Throws()
    {
        string path = TempPath();
        try
        {
            // Write a JSON object padded with a huge garbage string. 8 KB cap
            // is the documented MaximumFileBytes.
            string padding = new string('x', 10_000);
            File.WriteAllText(path, $"{{\"schemaVersion\":1,\"language\":\"en\",\"padding\":\"{padding}\"}}");
            Assert.Throws<ProviderConfigurationException>(
                () => UiLanguageStore.LoadIfExists(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void MalformedJson_Throws()
    {
        string path = TempPath();
        try
        {
            File.WriteAllText(path, "this is not json");
            Assert.Throws<ProviderConfigurationException>(
                () => UiLanguageStore.LoadIfExists(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Save_IsAtomicOverwrite()
    {
        // Save must not leave a half-written file if it succeeds. The
        // simplest observable contract: after a successful Save, no .tmp
        // file remains alongside the target, and a second Save cleanly
        // replaces the first.
        string path = TempPath();
        try
        {
            UiLanguageStore.Save(AppLanguage.English, path);
            UiLanguageStore.Save(AppLanguage.Chinese, path);  // overwrite
            Assert.False(File.Exists(path + ".tmp"));
            Assert.Equal(AppLanguage.Chinese, UiLanguageStore.LoadIfExists(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp");
        }
    }
}
