namespace SelectionAssistant.Core.Capture;

/// <summary>
/// R51: parameters controlling the screenshot beautify transform.
/// All fields default to the values spec'd in <c>BACKLOG-roadmap.md §R51</c>
/// (champagne background from Ivory Jade, 32px padding, 8px corner radius,
/// soft drop shadow offset 4,4 blur 16 opacity 50%).
/// </summary>
public sealed record BeautifyOptions
{
    /// <summary>Padding added around the screenshot on all four sides, in px.</summary>
    public int Padding { get; init; } = 32;

    /// <summary>Corner radius for both the screenshot's own mask and the
    /// background fill, in px. 0 = sharp rectangle.</summary>
    public int CornerRadius { get; init; } = 8;

    /// <summary>
    /// Background fill color as a hex string. Accepts <c>#RGB</c>,
    /// <c>#RRGGBB</c>, or <c>#AARRGGBB</c> (alpha is ignored — background is
    /// always drawn opaque inside the rounded rect). Defaults to
    /// <c>#FFFCF7EA</c> (Ivory Jade champagne).
    /// </summary>
    public string BackgroundHex { get; init; } = "#FFFCF7EA";

    /// <summary>Drop shadow X offset, in px. Positive = right.</summary>
    public int ShadowOffsetX { get; init; } = 4;

    /// <summary>Drop shadow Y offset, in px. Positive = down.</summary>
    public int ShadowOffsetY { get; init; } = 4;

    /// <summary>
    /// Drop shadow blur radius, in px. Shadow alpha is blurred by a 3-pass
    /// separable box blur (radius = <c>BlurRadius / 3</c>), approximating a
    /// Gaussian. 0 = hard-edged silhouette shadow.
    /// </summary>
    public int ShadowBlurRadius { get; init; } = 16;

    /// <summary>Drop shadow opacity in [0, 1]. 0 disables the shadow.</summary>
    public double ShadowOpacity { get; init; } = 0.5;
}

/// <summary>
/// R51: pure-software screenshot beautifier. Composites a captured BGRA
/// buffer onto a larger canvas with padding, rounded corners, an opaque
/// background fill, and a soft drop shadow — without SkiaSharp (NativeAOT-
/// safe, mirrors the R48 <c>BurnInHelpers</c> approach).
/// <para>
/// The output is a fresh BGRA buffer of size
/// <c>(srcW + 2*padding) × (srcH + 2*padding)</c>. Layer order (bottom to
/// top): shadow → background → source image. The source's own edges are
/// masked by the same rounded-rect coverage so the image's corners follow
/// the background's corners.
/// </para>
/// </summary>
public static class ScreenshotBeautifier
{
    /// <summary>
    /// Beautifies a BGRA source buffer.
    /// <para>
    /// <b>Layer model</b> (bottom to top):
    /// <list type="bullet">
    ///   <item><b>Shadow</b>: blurred silhouette of the image's rounded
    ///   rect, sampled at <c>(x - offsetX, y - offsetY)</c>. Visible in
    ///   the transparent padding band around the image.</item>
    ///   <item><b>Background</b>: opaque fill of the image's rounded rect,
    ///   visible only where the source pixel is transparent (so the
    ///   background acts as backing color for transparent source regions
    ///   and shows through anti-aliased rounded corner pixels).</item>
    ///   <item><b>Image</b>: source pixels at <c>(padding, padding)</c>,
    ///   masked by the rounded-rect coverage so the image's own corners
    ///   follow the corner radius.</item>
    /// </list>
    /// This produces the CleanShot X / Shottr "floating screenshot with
    /// rounded corners and drop shadow" look — the canvas padding is
    /// transparent, giving the shadow room to spread.
    /// </para>
    /// </summary>
    /// <returns>A new BGRA buffer plus its dimensions. The caller owns the
    /// buffer and may pass it to
    /// <c>ScreenRegionCapture.EncodeBgraToPng</c>.</returns>
    public static (byte[] Bgra, int Width, int Height) Beautify(
        byte[] srcBgra, int srcW, int srcH, BeautifyOptions options)
    {
        ArgumentNullException.ThrowIfNull(srcBgra);
        ArgumentNullException.ThrowIfNull(options);
        if (srcW <= 0 || srcH <= 0)
        {
            throw new ArgumentException("Source dimensions must be positive.", $"{srcW}/{srcH}");
        }
        int expected = srcW * srcH * 4;
        if (srcBgra.Length < expected)
        {
            throw new ArgumentException(
                $"Source buffer is {srcBgra.Length} bytes, expected {expected} ({srcW}x{srcH} BGRA).",
                nameof(srcBgra));
        }

        // Clamp all user-facing inputs to safe ranges.
        int padding = Math.Max(0, options.Padding);
        int radius = Math.Max(0, options.CornerRadius);
        int shadowBlur = Math.Max(0, options.ShadowBlurRadius);
        int shadowOffsetX = options.ShadowOffsetX;
        int shadowOffsetY = options.ShadowOffsetY;
        float shadowOpacity = (float)Math.Clamp(options.ShadowOpacity, 0.0, 1.0);

        int outW = srcW + 2 * padding;
        int outH = srcH + 2 * padding;
        byte[] outBgra = new byte[outW * outH * 4]; // zero-init = fully transparent

        // Parse background color (alpha ignored — background drawn opaque
        // inside the rounded-rect coverage mask).
        (byte bgB, byte bgG, byte bgR) = ParseHex(options.BackgroundHex);

        // Step 1: build the per-pixel coverage of the IMAGE's rounded rect
        // (in source-local coordinates). This single mask drives all three
        // layers: shadow silhouette, background fill, and image clip.
        float[] imageCoverage = new float[srcW * srcH];
        for (int y = 0; y < srcH; y++)
        {
            for (int x = 0; x < srcW; x++)
            {
                imageCoverage[y * srcW + x] = (float)RoundedRectCoverage(x, y, srcW, srcH, radius);
            }
        }

        // Step 2: blur the coverage for the shadow. 3-pass separable box
        // blur approximates a Gaussian. radius = blur/3 keeps the effective
        // spread ≈ shadowBlur. Skipped when blur=0 (hard-edged silhouette)
        // or when shadowOpacity=0 (no shadow at all).
        float[]? shadowAlpha = null;
        if (shadowOpacity > 0)
        {
            shadowAlpha = (float[])imageCoverage.Clone();
            if (shadowBlur > 0)
            {
                BoxBlurAlpha(shadowAlpha, srcW, srcH, Math.Max(1, shadowBlur / 3), passes: 3);
            }
        }

        // Step 3: single-pass composite. For each output pixel we layer
        // shadow → background → image (src-over).
        for (int y = 0; y < outH; y++)
        {
            for (int x = 0; x < outW; x++)
            {
                // 3a: shadow. Sample the blurred image coverage at the
                // source pixel that casts to this output pixel — i.e.
                // subtract padding and the shadow offset. Pixels where the
                // sample falls outside the source contribute nothing.
                if (shadowAlpha is not null)
                {
                    int sx = x - padding - shadowOffsetX;
                    int sy = y - padding - shadowOffsetY;
                    if (sx >= 0 && sx < srcW && sy >= 0 && sy < srcH)
                    {
                        float a = shadowAlpha[sy * srcW + sx] * shadowOpacity;
                        if (a > 0)
                        {
                            SrcOver(outBgra, outW, outH, x, y, 0, 0, 0, a);
                        }
                    }
                }

                // 3b + 3c: only pixels inside the image rectangle (placed
                // at padding, padding) get background and image
                // contributions. Padding band stays transparent so the
                // shadow is visible.
                int lx = x - padding;
                int ly = y - padding;
                if (lx >= 0 && lx < srcW && ly >= 0 && ly < srcH)
                {
                    float cov = imageCoverage[ly * srcW + lx];
                    if (cov > 0)
                    {
                        // 3b: opaque background fill masked by the rounded-
                        // rect coverage. Visible where the source pixel is
                        // transparent (e.g., source alpha=0, or the AA band
                        // at the corners where coverage is partial).
                        SrcOver(outBgra, outW, outH, x, y, bgB, bgG, bgR, cov);

                        // 3c: source image on top, masked by the same
                        // coverage so image corners fade with the radius.
                        int srcOff = (ly * srcW + lx) * 4;
                        byte srcA = srcBgra[srcOff + 3];
                        if (srcA > 0)
                        {
                            float effA = srcA / 255f * cov;
                            SrcOver(outBgra, outW, outH, x, y,
                                srcBgra[srcOff], srcBgra[srcOff + 1], srcBgra[srcOff + 2], effA);
                        }
                    }
                }
            }
        }

        return (outBgra, outW, outH);
    }

    /// <summary>
    /// Rounded-rect coverage at pixel (x, y) inside a [0, w) × [0, h)
    /// rectangle with corner radius <paramref name="r"/>. Returns a value
    /// in [0, 1]. 1 = fully inside; 0 = fully outside; intermediate = the
    /// 1-pixel anti-aliased band at the corner arc. Radius is clamped to
    /// min(w, h)/2.
    /// </summary>
    private static double RoundedRectCoverage(int x, int y, int w, int h, int r)
    {
        if (x < 0 || x >= w || y < 0 || y >= h) return 0;
        int maxR = Math.Min(w, h) / 2;
        int radius = Math.Max(0, Math.Min(r, maxR));
        if (radius == 0) return 1; // Sharp rectangle: every interior pixel is full.

        // Determine which corner quadrant the pixel lies in (if any). The
        // "central cross" — columns [radius, w-1-radius) and rows
        // [radius, h-1-radius) — is always fully covered.
        int cx;
        if (x < radius) cx = radius;
        else if (x > w - 1 - radius) cx = w - 1 - radius;
        else return 1;

        int cy;
        if (y < radius) cy = radius;
        else if (y > h - 1 - radius) cy = h - 1 - radius;
        else return 1;

        // Pixel is in a corner quadrant. Distance from the arc center.
        double dx = x - cx;
        double dy = y - cy;
        double dist = Math.Sqrt(dx * dx + dy * dy);

        // 1px AA: dist <= radius - 1 → full; dist >= radius → zero.
        if (dist <= radius - 1) return 1;
        if (dist >= radius) return 0;
        return radius - dist;
    }

    /// <summary>
    /// Separable box blur over a single-channel (alpha) float buffer.
    /// Performs <paramref name="passes"/> iterations of horizontal+vertical
    /// blur with the given window <paramref name="radius"/>. 3 passes
    /// approximates a Gaussian. In-place.
    /// </summary>
    private static void BoxBlurAlpha(float[] alpha, int w, int h, int radius, int passes)
    {
        if (radius <= 0 || passes <= 0) return;
        float[] temp = new float[w * h];

        for (int p = 0; p < passes; p++)
        {
            // Horizontal pass: alpha → temp.
            for (int y = 0; y < h; y++)
            {
                int rowOff = y * w;
                for (int x = 0; x < w; x++)
                {
                    int lo = Math.Max(0, x - radius);
                    int hi = Math.Min(w - 1, x + radius);
                    float sum = 0;
                    for (int i = lo; i <= hi; i++)
                    {
                        sum += alpha[rowOff + i];
                    }
                    temp[rowOff + x] = sum / (hi - lo + 1);
                }
            }

            // Vertical pass: temp → alpha.
            for (int x = 0; x < w; x++)
            {
                for (int y = 0; y < h; y++)
                {
                    int lo = Math.Max(0, y - radius);
                    int hi = Math.Min(h - 1, y + radius);
                    float sum = 0;
                    for (int i = lo; i <= hi; i++)
                    {
                        sum += temp[i * w + x];
                    }
                    alpha[y * w + x] = sum / (hi - lo + 1);
                }
            }
        }
    }

    /// <summary>
    /// Source-over compositing of (srcRGB, srcAlpha) onto the BGRA buffer.
    /// <paramref name="srcAlpha"/> is a float in [0, 1] (NOT 0-255). Pixels
    /// outside the canvas are silently dropped. Source RGB is pre-multiplied
    /// by alpha on output (standard premultiplied-out formula matches
    /// <c>BurnInHelpers.SetPixelAlphaBlend</c>).
    /// </summary>
    private static void SrcOver(
        byte[] bgra, int w, int h, int x, int y,
        byte srcB, byte srcG, byte srcR, float srcAlpha)
    {
        if (srcAlpha <= 0) return;
        if (x < 0 || x >= w || y < 0 || y >= h) return;

        int off = (y * w + x) * 4;
        float sA = srcAlpha;
        float dA = bgra[off + 3] / 255f;
        float outA = sA + dA * (1 - sA);
        if (outA <= 0) return;

        bgra[off] = (byte)((srcB * sA + bgra[off] * dA * (1 - sA)) / outA);
        bgra[off + 1] = (byte)((srcG * sA + bgra[off + 1] * dA * (1 - sA)) / outA);
        bgra[off + 2] = (byte)((srcR * sA + bgra[off + 2] * dA * (1 - sA)) / outA);
        bgra[off + 3] = (byte)(outA * 255);
    }

    /// <summary>
    /// Parses a hex color string (#RGB, #RRGGBB, or #AARRGGBB) into BGRA
    /// bytes. Alpha is ignored — the beautifier draws background as opaque
    /// inside the rounded-rect mask. Returns the Ivory Jade champagne
    /// default on any parse failure so a malformed setting never breaks
    /// the B key.
    /// </summary>
    private static (byte B, byte G, byte R) ParseHex(string? hex)
    {
        const byte defB = 0xEA, defG = 0xF7, defR = 0xFC; // #FFFCF7EA champagne
        if (string.IsNullOrWhiteSpace(hex)) return (defB, defG, defR);

        string s = hex.Trim();
        if (s.StartsWith('#')) s = s[1..];
        try
        {
            if (s.Length == 3)
            {
                byte r = (byte)(Convert.ToByte(s[0..1], 16) * 17);
                byte g = (byte)(Convert.ToByte(s[1..2], 16) * 17);
                byte b = (byte)(Convert.ToByte(s[2..3], 16) * 17);
                return (b, g, r);
            }
            if (s.Length == 6)
            {
                byte r = Convert.ToByte(s[0..2], 16);
                byte g = Convert.ToByte(s[2..4], 16);
                byte b = Convert.ToByte(s[4..6], 16);
                return (b, g, r);
            }
            if (s.Length == 8)
            {
                // AARRGGBB — drop the leading AA.
                byte r = Convert.ToByte(s[2..4], 16);
                byte g = Convert.ToByte(s[4..6], 16);
                byte b = Convert.ToByte(s[6..8], 16);
                return (b, g, r);
            }
        }
        catch
        {
            // Fall through to default.
        }
        return (defB, defG, defR);
    }
}
