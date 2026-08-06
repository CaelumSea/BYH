using SelectionAssistant.Core.Speech;
using Xunit;

namespace SelectionAssistant.Core.Tests.Speech;

public sealed class TtsSettingsTests
{
    [Fact]
    public void Default_HasCuratedVoicePicks()
    {
        TtsSettings d = TtsSettings.Default;
        Assert.True(d.Enabled);
        Assert.Equal("global", d.Region);
        Assert.Equal("speech-2.8-hd", d.Model);
        Assert.Equal("auto", d.Voice);
        // The user-curated picks (~/tts-output/voice-pickup.md):
        // Charming_Lady handles Chinese content, Spanish_CaptivatingStoryteller
        // handles English.
        Assert.Equal("Charming_Lady", d.ChineseVoice);
        Assert.Equal("Spanish_CaptivatingStoryteller", d.EnglishVoice);
        // Speed 1.1 — the rate at which the curated collection was selected.
        Assert.Equal(1.1, d.Speed);
    }

    [Fact]
    public void Normalize_RestoresBlankStringsToDefaults()
    {
        var raw = new TtsSettings
        {
            Enabled = true,
            Region = "  ",
            Model = null!,
            Voice = "",
            EnglishVoice = "",
            ChineseVoice = "",
            Speed = 0,   // invalid → default
            MaxCharacters = 0,
        };

        TtsSettings n = raw.Normalize();
        Assert.Equal(TtsSettings.Default.Region, n.Region);
        Assert.Equal(TtsSettings.Default.Model, n.Model);
        Assert.Equal(TtsSettings.Default.Voice, n.Voice);
        Assert.Equal(TtsSettings.Default.EnglishVoice, n.EnglishVoice);
        Assert.Equal(TtsSettings.Default.ChineseVoice, n.ChineseVoice);
        Assert.Equal(TtsSettings.Default.Speed, n.Speed);
        Assert.Equal(TtsSettings.Default.MaxCharacters, n.MaxCharacters);
    }

    [Fact]
    public void Normalize_ClampsSpeedToValidRange()
    {
        Assert.Equal(0.5, (TtsSettings.Default with { Speed = 0.1 }).Normalize().Speed);
        Assert.Equal(2.0, (TtsSettings.Default with { Speed = 5.0 }).Normalize().Speed);
        // A valid in-range speed passes through unchanged.
        Assert.Equal(1.5, (TtsSettings.Default with { Speed = 1.5 }).Normalize().Speed);
    }

    [Fact]
    public void Normalize_PreservesValidApiKeyReference_NullStaysNull()
    {
        // Null ApiKeyReference forces the mmx-config fallback; Normalize must
        // not coerce it to a default (that would override the user's intent).
        TtsSettings n = (TtsSettings.Default with { ApiKeyReference = null }).Normalize();
        Assert.Null(n.ApiKeyReference);
    }

    [Fact]
    public void Validate_PassOnNormalized()
    {
        // A normalized default must always validate.
        TtsSettings.Default.Normalize().Validate();
    }

    [Fact]
    public void Validate_ThrowsOnBlankModel()
    {
        TtsSettings s = TtsSettings.Default with { Model = "" };
        Assert.Throws<ArgumentException>(() => s.Validate());
    }
}
