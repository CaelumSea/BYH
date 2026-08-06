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

    // ── IsMostlyChinese: CJK ratio heuristic (>30% → Chinese voice) ──

    [Fact]
    public void IsMostlyChinese_PureChinese_True()
    {
        Assert.True(MiniMaxTtsProvider.IsMostlyChinese("你好世界，今天天气真好"));
    }

    [Fact]
    public void IsMostlyChinese_PureEnglish_False()
    {
        Assert.False(MiniMaxTtsProvider.IsMostlyChinese("Hello world, how are you today?"));
    }

    [Fact]
    public void IsMostlyChinese_MixedMostlyChinese_True()
    {
        // 6 CJK + some Latin/punct → CJK dominates.
        Assert.True(MiniMaxTtsProvider.IsMostlyChinese("今天用了 iPhone，感觉很 nice"));
    }

    [Fact]
    public void IsMostlyChinese_MixedMostlyEnglish_False()
    {
        // One CJK char in a sea of English → below 15%.
        Assert.False(MiniMaxTtsProvider.IsMostlyChinese("The character 茶 is interesting"));
    }

    [Fact]
    public void IsMostlyChinese_MixedOneQuarterChinese_True()
    {
        // ~25% CJK content — above the 15% threshold, so it routes to the
        // Chinese voice even though most words are English. This is the
        // "中英混杂以中文为主" intent.
        // 4 CJK chars (今天用了) + ~12 Latin chars/punct ≈ 25%.
        Assert.True(MiniMaxTtsProvider.IsMostlyChinese("今天用了 iPhone 感觉不错"));
    }

    [Fact]
    public void IsMostlyChinese_EmptyOrWhitespace_False()
    {
        Assert.False(MiniMaxTtsProvider.IsMostlyChinese(""));
        Assert.False(MiniMaxTtsProvider.IsMostlyChinese("   \t\n  "));
    }

    // ── ResolveVoice: auto heuristic + explicit override + language_boost ──

    [Fact]
    public void ResolveVoice_Auto_ChineseText_UsesChineseVoiceAndZhBoost()
    {
        var settings = TtsSettings.Default;
        (string voice, string? boost) = MiniMaxTtsProvider.ResolveVoice("你好世界", settings);
        Assert.Equal(settings.ChineseVoice, voice);
        Assert.Equal("zh", boost);
    }

    [Fact]
    public void ResolveVoice_Auto_EnglishText_UsesEnglishVoiceNoBoost()
    {
        var settings = TtsSettings.Default;
        (string voice, string? boost) = MiniMaxTtsProvider.ResolveVoice("Hello world", settings);
        Assert.Equal(settings.EnglishVoice, voice);
        Assert.Null(boost);
    }

    [Fact]
    public void ResolveVoice_ExplicitVoice_PassesThroughNoBoost()
    {
        var settings = TtsSettings.Default with { Voice = "Japanese_CalmLady" };
        (string voice, string? boost) = MiniMaxTtsProvider.ResolveVoice("你好世界", settings);
        Assert.Equal("Japanese_CalmLady", voice);
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
