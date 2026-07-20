using Xunit;

namespace SelectionAssistant.Providers.Tests;

public sealed class QrDecoderTests
{
    [Fact]
    public void Decode_WithNullBuffer_ReturnsEmpty()
    {
        var result = QrDecoder.Decode(null!, 100, 100);

        Assert.False(result.Success);
        Assert.Equal(string.Empty, result.Text);
        Assert.Same(QrDecodeResult.Empty, result);
    }

    [Fact]
    public void Decode_WithEmptyBuffer_ReturnsEmpty()
    {
        var result = QrDecoder.Decode([], 100, 100);

        Assert.False(result.Success);
        Assert.Equal(string.Empty, result.Text);
    }

    [Fact]
    public void Decode_WithBufferShorterThanExpected_ReturnsEmpty()
    {
        // 10x10 * 4 = 400 bytes expected, but only supply 10.
        var bgra = new byte[10];
        var result = QrDecoder.Decode(bgra, 10, 10);

        Assert.False(result.Success);
        Assert.Equal(string.Empty, result.Text);
    }

    [Fact]
    public void Decode_WithZeroWidth_ReturnsEmpty()
    {
        var bgra = new byte[100];
        var result = QrDecoder.Decode(bgra, 0, 100);

        Assert.False(result.Success);
    }

    [Fact]
    public void Decode_WithNegativeHeight_ReturnsEmpty()
    {
        var bgra = new byte[100];
        var result = QrDecoder.Decode(bgra, 100, -1);

        Assert.False(result.Success);
    }

    [Fact]
    public void Decode_WithNoBarcode_ReturnsEmpty()
    {
        // Pure white 100x100 BGRA buffer — no barcode features at all.
        int w = 100, h = 100;
        var bgra = new byte[w * h * 4];
        // Fill with 0xFF (white in BGRA: B=FF, G=FF, R=FF, A=FF).
        bgra.AsSpan().Fill(0xFF);

        var result = QrDecoder.Decode(bgra, w, h);

        Assert.False(result.Success);
        Assert.Equal(string.Empty, result.Text);
    }

    [Fact]
    public void Decode_WithPureBlackBuffer_ReturnsEmpty()
    {
        int w = 100, h = 100;
        var bgra = new byte[w * h * 4];
        // All zeros = black in BGRA. Still no barcode pattern.
        bgra.AsSpan().Fill(0x00);

        var result = QrDecoder.Decode(bgra, w, h);

        Assert.False(result.Success);
        Assert.Equal(string.Empty, result.Text);
    }

    // ── UrlDetector tests ────────────────────────────────────────────────

    [Theory]
    [InlineData("https://example.com", true)]
    [InlineData("http://x.y", true)]
    [InlineData("HTTPS://EXAMPLE.COM", true)]
    [InlineData("HTTP://foo.bar", true)]
    [InlineData("ftp://x", false)]
    [InlineData("mailto:a@b.com", false)]
    [InlineData("hello", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("http://", false)]       // length 7, below 8-char threshold
    [InlineData("https://", true)]       // length 8, exactly at threshold
    [InlineData("http://a", true)]       // length 8
    public void UrlDetector_IsUrl_HttpHttps_Only(string? text, bool expected)
    {
        Assert.Equal(expected, UrlDetector.IsUrl(text!));
    }

    // ── QrDecodeResult record tests ──────────────────────────────────────

    [Fact]
    public void Empty_HasExpectedDefaults()
    {
        Assert.False(QrDecodeResult.Empty.Success);
        Assert.Equal(string.Empty, QrDecodeResult.Empty.Text);
        Assert.Equal(string.Empty, QrDecodeResult.Empty.Format);
        Assert.False(QrDecodeResult.Empty.IsUrl);
    }

    [Fact]
    public void Record_Equality_Works()
    {
        var a = new QrDecodeResult(true, "abc", "QR_CODE", false);
        var b = new QrDecodeResult(true, "abc", "QR_CODE", false);

        Assert.Equal(a, b);
    }
}
