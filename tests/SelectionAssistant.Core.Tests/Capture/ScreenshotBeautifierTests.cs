using SelectionAssistant.Core.Capture;
using Xunit;

namespace SelectionAssistant.Core.Tests.Capture;

/// <summary>
/// R51: unit tests for <see cref="ScreenshotBeautifier"/>. All tests use
/// small synthetic BGRA buffers (no Win32 deps, no Avalonia) and assert
/// specific output pixels. The beautifier is a pure function — same inputs
/// always produce the same output buffer.
/// <para>
/// The beautify model is "floating screenshot with rounded corners and drop
/// shadow" (CleanShot X / Shottr style): the padding band around the image
/// stays transparent so the shadow has room to spread; the background
/// color only shows inside the image's rounded rect where the source pixel
/// is transparent.
/// </para>
/// </summary>
public sealed class ScreenshotBeautifierTests
{
    // Champagne #FFFCF7EA, in BGRA byte order.
    private const byte ChampagneB = 0xEA;
    private const byte ChampagneG = 0xF7;
    private const byte ChampagneR = 0xFC;

    /// <summary>Builds a w×h BGRA buffer with every pixel set to (B,G,R,A).</summary>
    private static byte[] SolidBgra(int w, int h, byte b, byte g, byte r, byte a)
    {
        byte[] buf = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            buf[i * 4] = b;
            buf[i * 4 + 1] = g;
            buf[i * 4 + 2] = r;
            buf[i * 4 + 3] = a;
        }
        return buf;
    }

    private static (byte B, byte G, byte R, byte A) Pixel(byte[] bgra, int w, int x, int y)
    {
        int off = (y * w + x) * 4;
        return (bgra[off], bgra[off + 1], bgra[off + 2], bgra[off + 3]);
    }

    // ── Output dimensions ────────────────────────────────────────────

    [Fact]
    public void OutputSize_IsOriginalPlusDoublePadding()
    {
        byte[] src = SolidBgra(5, 5, 0, 0, 0, 255);
        var opts = new BeautifyOptions { Padding = 4, CornerRadius = 0, ShadowOpacity = 0 };

        var (bgra, w, h) = ScreenshotBeautifier.Beautify(src, 5, 5, opts);

        Assert.Equal(13, w); // 5 + 2*4
        Assert.Equal(13, h);
        Assert.Equal(13 * 13 * 4, bgra.Length);
    }

    [Fact]
    public void PaddingZero_OutputEqualsSource()
    {
        byte[] src = SolidBgra(3, 3, 10, 20, 30, 255);
        var opts = new BeautifyOptions { Padding = 0, CornerRadius = 0, ShadowOpacity = 0 };

        var (bgra, w, h) = ScreenshotBeautifier.Beautify(src, 3, 3, opts);

        Assert.Equal(3, w);
        Assert.Equal(3, h);
        // No padding + no shadow → center pixel equals source pixel exactly.
        var px = Pixel(bgra, w, 1, 1);
        Assert.Equal(10, px.B);
        Assert.Equal(20, px.G);
        Assert.Equal(30, px.R);
        Assert.Equal(255, px.A);
    }

    // ── Padding band transparency ───────────────────────────────────

    [Fact]
    public void FourOuterCorners_AreTransparent()
    {
        byte[] src = SolidBgra(10, 10, 0, 0, 0, 255);
        var opts = new BeautifyOptions { Padding = 5, CornerRadius = 0, ShadowOpacity = 0 };

        var (bgra, w, h) = ScreenshotBeautifier.Beautify(src, 10, 10, opts);

        // Padding band is transparent (shadow off so it stays that way).
        Assert.Equal(0, Pixel(bgra, w, 0, 0).A);
        Assert.Equal(0, Pixel(bgra, w, w - 1, 0).A);
        Assert.Equal(0, Pixel(bgra, w, 0, h - 1).A);
        Assert.Equal(0, Pixel(bgra, w, w - 1, h - 1).A);
    }

    [Fact]
    public void PaddingBandMidEdge_IsTransparent_WhenShadowOff()
    {
        byte[] src = SolidBgra(10, 10, 0, 0, 0, 255);
        var opts = new BeautifyOptions { Padding = 5, CornerRadius = 0, ShadowOpacity = 0 };

        var (bgra, w, h) = ScreenshotBeautifier.Beautify(src, 10, 10, opts);

        // Mid-edge of each padding side — not corners, not image area. With
        // shadow off these pixels stay transparent.
        Assert.Equal(0, Pixel(bgra, w, w / 2, 0).A);       // top mid
        Assert.Equal(0, Pixel(bgra, w, w / 2, h - 1).A);   // bottom mid
        Assert.Equal(0, Pixel(bgra, w, 0, h / 2).A);       // left mid
        Assert.Equal(0, Pixel(bgra, w, w - 1, h / 2).A);   // right mid
    }

    // ── Background fill (shows through transparent source pixels) ────

    [Fact]
    public void TransparentSourcePixel_ShowsBackgroundUnderneath()
    {
        // Source is transparent everywhere. Inside the image rect, the
        // background fill should show through unmodified.
        byte[] src = SolidBgra(10, 10, 0, 0, 0, 0);
        var opts = new BeautifyOptions
        {
            Padding = 4,
            CornerRadius = 0,
            BackgroundHex = "#FFFCF7EA", // champagne
            ShadowOpacity = 0,
        };

        var (bgra, w, h) = ScreenshotBeautifier.Beautify(src, 10, 10, opts);

        // Output center = padding + src center (5,5). Source α=0 so the
        // background fill is the final pixel.
        var px = Pixel(bgra, w, 4 + 5, 4 + 5);
        Assert.Equal(ChampagneB, px.B);
        Assert.Equal(ChampagneG, px.G);
        Assert.Equal(ChampagneR, px.R);
        Assert.Equal(255, px.A);
    }

    [Fact]
    public void BackgroundHex_RRGGBB_Form()
    {
        byte[] src = SolidBgra(10, 10, 0, 0, 0, 0);
        var opts = new BeautifyOptions
        {
            Padding = 4,
            CornerRadius = 0,
            BackgroundHex = "#FF0000", // pure red
            ShadowOpacity = 0,
        };

        var (bgra, w, h) = ScreenshotBeautifier.Beautify(src, 10, 10, opts);

        var px = Pixel(bgra, w, 4 + 3, 4 + 3); // interior of image rect
        Assert.Equal(0, px.B);
        Assert.Equal(0, px.G);
        Assert.Equal(255, px.R);
    }

    [Fact]
    public void BackgroundHex_MalformedString_FallsBackToDefault()
    {
        byte[] src = SolidBgra(10, 10, 0, 0, 0, 0);
        var opts = new BeautifyOptions
        {
            Padding = 4,
            CornerRadius = 0,
            BackgroundHex = "not-a-hex-color",
            ShadowOpacity = 0,
        };

        var (bgra, w, h) = ScreenshotBeautifier.Beautify(src, 10, 10, opts);

        // Champagne default kicks in — bad input never breaks the pipeline.
        var px = Pixel(bgra, w, 4 + 3, 4 + 3);
        Assert.Equal(ChampagneB, px.B);
        Assert.Equal(ChampagneG, px.G);
        Assert.Equal(ChampagneR, px.R);
    }

    // ── Image pass-through ───────────────────────────────────────────

    [Fact]
    public void OpaqueSourcePixel_ImageOverridesBackground()
    {
        // Fully opaque red source → image wins, background invisible.
        byte[] src = SolidBgra(10, 10, 0, 0, 255, 255);
        var opts = new BeautifyOptions
        {
            Padding = 4,
            CornerRadius = 0,
            BackgroundHex = "#FFFCF7EA",
            ShadowOpacity = 0,
        };

        var (bgra, w, h) = ScreenshotBeautifier.Beautify(src, 10, 10, opts);

        var px = Pixel(bgra, w, 4 + 5, 4 + 5);
        Assert.Equal(0, px.B);
        Assert.Equal(0, px.G);
        Assert.Equal(255, px.R);
        Assert.Equal(255, px.A);
    }

    // ── Shadow ───────────────────────────────────────────────────────

    [Fact]
    public void Shadow_ProjectsToBottomRight_PaddingBandDarkened()
    {
        // Opaque source so the shadow silhouette covers the full image rect.
        // Bottom-right padding band receives the offset + blurred shadow.
        byte[] src = SolidBgra(20, 20, 0, 0, 0, 255);
        var opts = new BeautifyOptions
        {
            Padding = 10,
            CornerRadius = 0,
            BackgroundHex = "#FFFFFF", // irrelevant — outside image rect
            ShadowOffsetX = 4,
            ShadowOffsetY = 4,
            ShadowBlurRadius = 4,
            ShadowOpacity = 0.5,
        };

        var (bgra, w, h) = ScreenshotBeautifier.Beautify(src, 20, 20, opts);

        // Pixel in the bottom-right padding band: well past the image rect
        // edge, within the offset+blur shadow zone. Shadow src-over
        // transparent produces (0,0,0, partial-alpha) — black with partial
        // alpha. So B=0 (shadow color is black) and 0 < A < 255.
        var px = Pixel(bgra, w, 10 + 20 + 2, 10 + 20 + 2);
        Assert.Equal(0, px.B); // shadow color
        Assert.True(px.A > 0 && px.A < 255, $"Expected partial-alpha shadow, got A={px.A}");
    }

    [Fact]
    public void ShadowOpacityZero_NoShadowPixelsWritten()
    {
        byte[] src = SolidBgra(20, 20, 0, 0, 0, 255);
        var opts = new BeautifyOptions
        {
            Padding = 10,
            CornerRadius = 0,
            BackgroundHex = "#FFFFFF",
            ShadowOpacity = 0,
        };

        var (bgra, w, h) = ScreenshotBeautifier.Beautify(src, 20, 20, opts);

        // With shadow disabled, every padding pixel is transparent.
        Assert.Equal(0, Pixel(bgra, w, w - 2, h - 2).A);
        Assert.Equal(0, Pixel(bgra, w, 2, 2).A);
    }

    [Fact]
    public void NegativeShadowOffset_ProjectsToTopLeft()
    {
        // Negative offsets flip the shadow to top-left. A pixel just past
        // the image's top-left edge (offset inward by the shadow offset)
        // should receive the shadow.
        byte[] src = SolidBgra(20, 20, 0, 0, 0, 255);
        var opts = new BeautifyOptions
        {
            Padding = 10,
            CornerRadius = 0,
            BackgroundHex = "#FFFFFF",
            ShadowOffsetX = -4,
            ShadowOffsetY = -4,
            ShadowBlurRadius = 4,
            ShadowOpacity = 0.5,
        };

        var (bgra, w, h) = ScreenshotBeautifier.Beautify(src, 20, 20, opts);

        // Pixel just inside the top-left padding band, where negative
        // offset projects the shadow from the image's top-left corner.
        // Output coord (padding + shadowOffsetX + 1, padding + shadowOffsetY + 1)
        // = (10 - 4 + 1, 10 - 4 + 1) = (7, 7) — but we want OUTSIDE image rect
        // (image is [10..29]). So pick (8, 8) — past the image edge by 2 px,
        // within the blur radius of the projected shadow.
        var px = Pixel(bgra, w, 8, 8);
        // Shadow projects from image's top-left corner, blurred with
        // radius ~1. Sample (sx, sy) = (8 - (-4), 8 - (-4)) = (12, 12) in
        // src coords — well inside src, so coverage=1, blurred alpha > 0.
        Assert.True(px.A > 0, $"Expected partial shadow at (8,8), got A={px.A}");
    }

    // ── Rounded corners ──────────────────────────────────────────────

    [Fact]
    public void CornerRadiusZero_OpaqueSource_FillsAllImageCorners()
    {
        byte[] src = SolidBgra(10, 10, 0, 0, 0, 255); // opaque black
        var opts = new BeautifyOptions
        {
            Padding = 4,
            CornerRadius = 0,
            BackgroundHex = "#FFFCF7EA",
            ShadowOpacity = 0,
        };

        var (bgra, w, h) = ScreenshotBeautifier.Beautify(src, 10, 10, opts);

        // Inner image rect corner (padding, padding) = top-left of image.
        // radius=0 → full coverage → opaque source wins.
        var px = Pixel(bgra, w, 4, 4);
        Assert.Equal(255, px.A);
        Assert.Equal(0, px.B); // source black
    }

    [Fact]
    public void CornerRadius_ClampsToCircle_ImageCornersTransparent()
    {
        // Large radius relative to source size: src=10x10, radius=5 =
        // min(10,10)/2 → fully circular. Pixel at the corner of the image
        // rect (padding, padding) = distance 7.07 from the corner-center,
        // which is outside the radius 5 circle → coverage 0 → transparent.
        byte[] src = SolidBgra(10, 10, 0, 0, 0, 255);
        var opts = new BeautifyOptions
        {
            Padding = 4,
            CornerRadius = 5,
            BackgroundHex = "#FFFCF7EA",
            ShadowOpacity = 0,
        };

        var (bgra, w, h) = ScreenshotBeautifier.Beautify(src, 10, 10, opts);

        var px = Pixel(bgra, w, 4, 4);
        Assert.Equal(0, px.A);
    }

    // ── Robustness ───────────────────────────────────────────────────

    [Fact]
    public void InvalidDimensions_Throws()
    {
        byte[] src = SolidBgra(5, 5, 0, 0, 0, 255);
        Assert.Throws<ArgumentException>(
            () => ScreenshotBeautifier.Beautify(src, 0, 5, new BeautifyOptions()));
        Assert.Throws<ArgumentException>(
            () => ScreenshotBeautifier.Beautify(src, 5, -1, new BeautifyOptions()));
    }

    [Fact]
    public void BufferSizeMismatch_Throws()
    {
        byte[] tooSmall = new byte[10]; // 5x5 needs 100 bytes
        Assert.Throws<ArgumentException>(
            () => ScreenshotBeautifier.Beautify(tooSmall, 5, 5, new BeautifyOptions()));
    }

    [Fact]
    public void LargeRadius_StaysClamped_NoOverflow()
    {
        // Radius far larger than min(w,h)/2 → must clamp, not crash.
        byte[] src = SolidBgra(8, 8, 0, 0, 0, 255);
        var opts = new BeautifyOptions
        {
            Padding = 2,
            CornerRadius = 1000, // way too big
            ShadowOpacity = 0,
        };

        var (bgra, w, h) = ScreenshotBeautifier.Beautify(src, 8, 8, opts);

        // Smoke test: produces a circular image inside a 12×12 canvas
        // and doesn't crash.
        Assert.Equal(12, w);
        Assert.Equal(12, h);
        Assert.Equal(12 * 12 * 4, bgra.Length);
    }
}
