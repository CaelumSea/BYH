using SelectionAssistant.Core.Speech;
using SelectionAssistant.Infrastructure.Speech;
using Xunit;

namespace SelectionAssistant.Core.Tests.Speech;

public sealed class MiniMaxTtsProviderTests
{
    // ── HexToBytes: the #1 correctness gotcha (MiniMax returns hex, NOT base64) ──

    [Fact]
    public void HexToBytes_DecodesLowercaseHex()
    {
        // "Hi" in ASCII = 0x48 0x69
        byte[] result = MiniMaxTtsProvider.HexToBytes("4869");
        Assert.Equal(new byte[] { 0x48, 0x69 }, result);
    }

    [Fact]
    public void HexToBytes_DecodesUppercaseHex()
    {
        byte[] result = MiniMaxTtsProvider.HexToBytes("FFD8FFE0");
        Assert.Equal(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, result);
    }

    [Fact]
    public void HexToBytes_DecodesMixedCaseHex()
    {
        byte[] result = MiniMaxTtsProvider.HexToBytes("AbCd");
        Assert.Equal(new byte[] { 0xAB, 0xCD }, result);
    }

    [Fact]
    public void HexToBytes_EmptyString_ReturnsEmptyArray()
    {
        Assert.Empty(MiniMaxTtsProvider.HexToBytes(string.Empty));
    }

    [Fact]
    public void HexToBytes_OddLength_ThrowsFormatException()
    {
        // "486" is 3 chars — odd-length hex is invalid.
        Assert.Throws<FormatException>(() => MiniMaxTtsProvider.HexToBytes("486"));
    }

    [Fact]
    public void HexToBytes_NonHexChars_ThrowsFormatException()
    {
        // 'g' and 'z' are not hex digits.
        Assert.Throws<FormatException>(() => MiniMaxTtsProvider.HexToBytes("ggzz"));
    }

    [Fact]
    public void HexToBytes_NullInput_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => MiniMaxTtsProvider.HexToBytes(null!));
    }

    /// <summary>
    /// Sanity check that decoding a real MP3-frame header works — the first 4
    /// bytes of an MP3 with an ID3 tag would be 0x49 0x44 0x33 ("ID3"), and a
    /// naked MP3 frame starts with 0xFF 0xFB (MPEG-1 Layer III sync word).
    /// </summary>
    [Theory]
    [InlineData("49443303", new byte[] { 0x49, 0x44, 0x33, 0x03 })] // "ID3\x03"
    [InlineData("FFFB9064", new byte[] { 0xFF, 0xFB, 0x90, 0x64 })] // MPEG frame sync
    public void HexToBytes_DecodesMp3Headers(string hex, byte[] expected)
    {
        Assert.Equal(expected, MiniMaxTtsProvider.HexToBytes(hex));
    }

    // ── ClassifyScript: presence-based script bucket (CJK vs Latin) ──

    [Fact]
    public void ClassifyScript_PureChinese_Chinese()
    {
        // CJK with no Latin letters → Chinese bucket.
        Assert.Equal(ScriptKind.Chinese, MiniMaxTtsProvider.ClassifyScript("你好世界，今天天气真好"));
    }

    [Fact]
    public void ClassifyScript_PureEnglish_English()
    {
        // No CJK → English bucket.
        Assert.Equal(ScriptKind.English, MiniMaxTtsProvider.ClassifyScript("Hello world, how are you today?"));
    }

    [Fact]
    public void ClassifyScript_CjkAndLatin_Mixed()
    {
        // Has both CJK and Latin letters → Mixed, regardless of ratio.
        // This is the "中英混合" intent: even one Latin token in Chinese text
        // (or vice versa) routes to the cross-lingual Mixed voice.
        Assert.Equal(ScriptKind.Mixed, MiniMaxTtsProvider.ClassifyScript("今天用了 iPhone，感觉很 nice"));
        // Low-CJK mix still counts as Mixed — the classifier only looks at
        // presence, not ratio (deliberate change from the old 15% heuristic).
        Assert.Equal(ScriptKind.Mixed, MiniMaxTtsProvider.ClassifyScript("The character 茶 is interesting"));
    }

    [Fact]
    public void ClassifyScript_EmptyOrWhitespace_English()
    {
        // Empty/whitespace defaults to English (synthesis of nothing is a no-op;
        // English is the sane fallback bucket).
        Assert.Equal(ScriptKind.English, MiniMaxTtsProvider.ClassifyScript(""));
        Assert.Equal(ScriptKind.English, MiniMaxTtsProvider.ClassifyScript("   \t\n  "));
    }

    [Fact]
    public void ClassifyScript_CjkWithPunctuationOnly_Chinese()
    {
        // CJK + punctuation/digits (no Latin letters) still Chinese — only
        // A-Z/a-z count as Latin, not digits or punctuation.
        Assert.Equal(ScriptKind.Chinese, MiniMaxTtsProvider.ClassifyScript("第 3 季，共 12 集。"));
    }

    // ── ResolveVoice: three-way routing by script bucket ──

    [Fact]
    public void ResolveVoice_PureChinese_UsesChineseVoiceAndZhBoost()
    {
        var settings = TtsSettings.Default;
        (string voice, string? boost) = MiniMaxTtsProvider.ResolveVoice("你好世界", settings);
        Assert.Equal(settings.ChineseVoice, voice);
        Assert.Equal("zh", boost);
    }

    [Fact]
    public void ResolveVoice_PureEnglish_UsesEnglishVoiceNoBoost()
    {
        var settings = TtsSettings.Default;
        (string voice, string? boost) = MiniMaxTtsProvider.ResolveVoice("Hello world", settings);
        Assert.Equal(settings.EnglishVoice, voice);
        Assert.Null(boost);
    }

    [Fact]
    public void ResolveVoice_MixedScript_UsesMixedVoiceNoBoost()
    {
        var settings = TtsSettings.Default;
        (string voice, string? boost) = MiniMaxTtsProvider.ResolveVoice("今天用了 iPhone", settings);
        Assert.Equal(settings.MixedVoice, voice);
        // No language_boost for Mixed — the cross-lingual voice handles the
        // language switch itself; forcing "zh" would mis-segment the English.
        Assert.Null(boost);
    }

    // ── HostForRegion: global vs cn (note the cn host is minimax<b>i</b>.com) ──

    [Fact]
    public void HostForRegion_Global_ReturnsMinimaxIo()
    {
        Assert.Equal("https://api.minimax.io", MiniMaxTtsProvider.HostForRegion("global"));
    }

    [Fact]
    public void HostForRegion_Cn_ReturnsMinimaxiCom()
    {
        // CRITICAL: cn host has an extra 'i' — minimax<i>.com. Getting this
        // wrong means every cn-region request fails to connect.
        Assert.Equal("https://api.minimaxi.com", MiniMaxTtsProvider.HostForRegion("cn"));
    }

    [Fact]
    public void HostForRegion_NullOrUnknown_DefaultsToGlobal()
    {
        Assert.Equal("https://api.minimax.io", MiniMaxTtsProvider.HostForRegion(null!));
        Assert.Equal("https://api.minimax.io", MiniMaxTtsProvider.HostForRegion("unknown"));
    }
}
