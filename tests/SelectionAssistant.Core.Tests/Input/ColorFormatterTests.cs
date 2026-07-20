using SelectionAssistant.Core.Input;
using Xunit;

namespace SelectionAssistant.Core.Tests.Input;

public sealed class ColorFormatterTests
{
    [Theory]
    [InlineData(0x00, 0x00, 0x00, "#000000")]
    [InlineData(0xFF, 0xFF, 0xFF, "#FFFFFF")]
    [InlineData(0xFF, 0xFE, 0xF0, "#FFFEF0")]
    [InlineData(0x12, 0x34, 0x56, "#123456")]
    [InlineData(0xAB, 0xCD, 0xEF, "#ABCDEF")]
    [InlineData(0x09, 0x0A, 0x0F, "#090A0F")]
    public void ToHexRgb_ProducesUppercaseSevenCharString(byte r, byte g, byte b, string expected)
    {
        string actual = ColorFormatter.ToHexRgb(r, g, b);
        Assert.Equal(expected, actual);
        Assert.Equal(7, actual.Length);
    }

    [Fact]
    public void ToHexRgb_AlwaysStartsWithHash()
    {
        Assert.StartsWith("#", ColorFormatter.ToHexRgb(0, 0, 0));
        Assert.StartsWith("#", ColorFormatter.ToHexRgb(128, 64, 32));
    }

    [Fact]
    public void ToHexRgb_UppercaseByDefault()
    {
        // Lowercase hex chars must never appear — the spec is uppercase.
        string hex = ColorFormatter.ToHexRgb(0xAB, 0xCD, 0xEF);
        Assert.Equal("#ABCDEF", hex);
        Assert.NotEqual("#abcdef", hex);
    }

    [Theory]
    [InlineData(255, 254, 240, "rgb(255, 254, 240)")]
    [InlineData(0, 0, 0, "rgb(0, 0, 0)")]
    [InlineData(128, 64, 32, "rgb(128, 64, 32)")]
    public void ToRgbDecimal_ProducesCssStyleString(byte r, byte g, byte b, string expected)
    {
        Assert.Equal(expected, ColorFormatter.ToRgbDecimal(r, g, b));
    }
}
