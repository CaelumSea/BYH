using SelectionAssistant.Core.Speech;
using SelectionAssistant.Infrastructure.Configuration;
using Xunit;

namespace SelectionAssistant.Core.Tests.Speech;

public sealed class TtsSettingsStoreTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"byh-tts-{Guid.NewGuid():N}.json");

    [Fact]
    public void MissingFile_ReturnsDefault()
    {
        TtsSettings settings = TtsSettingsStore.LoadIfExists(
            Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.json"));
        Assert.Equal(TtsSettings.Default, settings);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsAllFields()
    {
        string path = TempPath();
        try
        {
            var original = new TtsSettings
            {
                Enabled = false,
                ApiKeyReference = "secret://tts/custom",
                Region = "cn",
                Model = "speech-2.6-hd",
                Voice = "Japanese_CalmLady",
                EnglishVoice = "English_Whispering_girl",
                ChineseVoice = "Chinese (Mandarin)_Warm_Bestie",
                Speed = 1.3,
                MaxCharacters = 5000,
            };

            TtsSettingsStore.Save(original, path);
            TtsSettings loaded = TtsSettingsStore.LoadIfExists(path);

            Assert.False(loaded.Enabled);
            Assert.Equal("secret://tts/custom", loaded.ApiKeyReference);
            Assert.Equal("cn", loaded.Region);
            Assert.Equal("speech-2.6-hd", loaded.Model);
            Assert.Equal("Japanese_CalmLady", loaded.Voice);
            Assert.Equal("English_Whispering_girl", loaded.EnglishVoice);
            Assert.Equal("Chinese (Mandarin)_Warm_Bestie", loaded.ChineseVoice);
            Assert.Equal(1.3, loaded.Speed);
            Assert.Equal(5000, loaded.MaxCharacters);
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void SaveThenLoad_NullApiKeyReference_RoundTripsAsNull()
    {
        // A null ApiKeyReference (forces mmx fallback) must survive round-trip
        // as null — not be coerced to the default reference.
        string path = TempPath();
        try
        {
            var original = TtsSettings.Default with { ApiKeyReference = null };
            TtsSettingsStore.Save(original, path);
            TtsSettings loaded = TtsSettingsStore.LoadIfExists(path);
            Assert.Null(loaded.ApiKeyReference);
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void Load_WrongSchemaVersion_Throws()
    {
        string path = TempPath();
        try
        {
            File.WriteAllText(path, "{\"schemaVersion\":99}");
            Assert.Throws<ProviderConfigurationException>(() => TtsSettingsStore.LoadIfExists(path));
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void Load_MalformedJson_Throws()
    {
        string path = TempPath();
        try
        {
            File.WriteAllText(path, "{ not json");
            Assert.Throws<ProviderConfigurationException>(() => TtsSettingsStore.LoadIfExists(path));
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
