using SelectionAssistant.Core.Capture;
using Xunit;

namespace SelectionAssistant.Core.Tests.Capture;

[Trait("Category", "Capture")]
public sealed class LongScreenshotStitcherTests
{
    // Synthetic BGRA helpers. The stitcher is buffer-agnostic, so we don't need
    // real image content — just deterministic byte patterns we can reason about.

    /// <summary>Fills a width×height BGRA buffer with one solid color.</summary>
    private static byte[] MakeSolidBgra(int width, int height, byte b, byte g, byte r)
    {
        byte[] buf = new byte[width * height * 4];
        for (int i = 0; i < buf.Length; i += 4)
        {
            buf[i] = b;
            buf[i + 1] = g;
            buf[i + 2] = r;
            buf[i + 3] = 0xFF;
        }
        return buf;
    }

    /// <summary>
    /// Builds a width×height BGRA buffer of horizontal stripes, each
    /// <paramref name="stripeHeight"/> rows tall, cycling through the given
    /// colors row by row (row index / stripeHeight). Useful for asserting which
    /// rows survive into the merged canvas.
    /// </summary>
    private static byte[] MakeStripedBgra(int width, int height, int stripeHeight,
        params (byte B, byte G, byte R)[] colors)
    {
        byte[] buf = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            var (b, g, r) = colors[(y / stripeHeight) % colors.Length];
            for (int x = 0; x < width; x++)
            {
                int off = (y * width + x) * 4;
                buf[off] = b;
                buf[off + 1] = g;
                buf[off + 2] = r;
                buf[off + 3] = 0xFF;
            }
        }
        return buf;
    }

    /// <summary>Reads the (B,G,R) triple of the first pixel of the given row.</summary>
    private static (byte B, byte G, byte R) RowColor(byte[] bgra, int width, int row)
    {
        int off = row * width * 4;
        return (bgra[off], bgra[off + 1], bgra[off + 2]);
    }

    [Fact]
    public void Append_IdenticalFrame_FullOverlap()
    {
        // Canvas and frame are byte-identical → every row overlaps.
        const int w = 40, h = 30;
        byte[] canvas = MakeSolidBgra(w, h, 10, 20, 30);
        byte[] frame = MakeSolidBgra(w, h, 10, 20, 30);

        var result = LongScreenshotStitcher.Append(canvas, w, h, frame, h);

        Assert.True(result.Success);
        Assert.Equal(h, result.OverlapRows);
        Assert.Equal(w, result.Width);
        // No new content was added — merged height equals canvas height.
        Assert.Equal(h, result.Height);
    }

    [Fact]
    public void Append_HalfOverlap_StitchesCorrectly()
    {
        // Canvas: 30 rows of color A. Frame: 15 rows of A (overlap) + 15 rows of B.
        // Expected overlap = 15, merged height = 30 + 15 = 45.
        const int w = 40, canvasH = 30, frameH = 30;
        byte[] canvas = MakeSolidBgra(w, canvasH, 10, 20, 30);

        byte[] frame = new byte[w * frameH * 4];
        // First 15 rows = color A (overlap with canvas tail).
        for (int y = 0; y < 15; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int off = (y * w + x) * 4;
                frame[off] = 10; frame[off + 1] = 20; frame[off + 2] = 30; frame[off + 3] = 0xFF;
            }
        }
        // Last 15 rows = color B (new content).
        for (int y = 15; y < frameH; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int off = (y * w + x) * 4;
                frame[off] = 40; frame[off + 1] = 50; frame[off + 2] = 60; frame[off + 3] = 0xFF;
            }
        }

        var result = LongScreenshotStitcher.Append(canvas, w, canvasH, frame, frameH);

        Assert.True(result.Success);
        Assert.Equal(15, result.OverlapRows);
        Assert.Equal(canvasH + 15, result.Height);

        // Sanity: first canvas row color preserved, last appended row = color B.
        Assert.Equal((10, 20, 30), RowColor(result.MergedBgra, w, 0));
        Assert.Equal((40, 50, 60), RowColor(result.MergedBgra, w, result.Height - 1));
    }

    [Fact]
    public void Append_NoOverlap_AppendsFullFrame()
    {
        // Canvas solid A, frame solid B — no row band matches. Expect full append
        // (Success=false, OverlapRows=NoOverlap sentinel).
        const int w = 40, canvasH = 30, frameH = 20;
        byte[] canvas = MakeSolidBgra(w, canvasH, 10, 20, 30);
        byte[] frame = MakeSolidBgra(w, frameH, 40, 50, 60);

        var result = LongScreenshotStitcher.Append(canvas, w, canvasH, frame, frameH);

        Assert.False(result.Success);
        Assert.Equal(LongScreenshotStitchResult.NoOverlap, result.OverlapRows);
        Assert.Equal(canvasH + frameH, result.Height);
    }

    [Fact]
    public void Append_PreservesCanvasContent()
    {
        // The merged buffer must begin with an exact copy of the canvas.
        const int w = 20, h = 16;
        byte[] canvas = MakeStripedBgra(w, h, stripeHeight: 4, (1, 2, 3), (4, 5, 6));
        byte[] frame = MakeSolidBgra(w, h, 200, 200, 200); // no overlap

        var result = LongScreenshotStitcher.Append(canvas, w, h, frame, h);

        // First canvas.Length bytes identical to canvas.
        for (int i = 0; i < canvas.Length; i++)
        {
            Assert.Equal(canvas[i], result.MergedBgra[i]);
        }
    }

    [Fact]
    public void Append_PreservesAppendedFrameTail()
    {
        // When overlap = N rows, the frame's rows [N..end) should appear verbatim
        // starting at offset canvas.Length in the merged buffer.
        const int w = 20, canvasH = 12, frameH = 12;
        byte[] canvas = MakeStripedBgra(w, canvasH, stripeHeight: 3, (1, 2, 3), (4, 5, 6));
        // Frame = canvas pattern continued so a 6-row overlap is plausible, then diverges.
        byte[] frame = MakeStripedBgra(w, frameH, stripeHeight: 3, (1, 2, 3), (4, 5, 6));

        var result = LongScreenshotStitcher.Append(canvas, w, canvasH, frame, frameH);
        Assert.True(result.Success);

        int appendedRows = frameH - result.OverlapRows;
        int appendedBytes = appendedRows * w * 4;
        int srcOffset = result.OverlapRows * w * 4;
        for (int i = 0; i < appendedBytes; i++)
        {
            Assert.Equal(frame[srcOffset + i], result.MergedBgra[canvas.Length + i]);
        }
    }

    [Fact]
    public void Append_BadCanvasLength_ThrowsArgument()
    {
        const int w = 10, canvasH = 4, frameH = 4;
        byte[] badCanvas = new byte[w * (canvasH + 1) * 4]; // wrong length
        byte[] frame = MakeSolidBgra(w, frameH, 0, 0, 0);

        Assert.Throws<ArgumentException>(() =>
            LongScreenshotStitcher.Append(badCanvas, w, canvasH, frame, frameH));
    }

    [Fact]
    public void Append_BadFrameLength_ThrowsArgument()
    {
        const int w = 10, canvasH = 4, frameH = 4;
        byte[] canvas = MakeSolidBgra(w, canvasH, 0, 0, 0);
        byte[] badFrame = new byte[(w + 1) * frameH * 4]; // wrong length

        Assert.Throws<ArgumentException>(() =>
            LongScreenshotStitcher.Append(canvas, w, canvasH, badFrame, frameH));
    }

    [Fact]
    public void Append_SideMargin_IgnoresSidePixels()
    {
        // Two frames differ ONLY in the left/right SideIgnoreRatio columns.
        // The stitcher should still find the full overlap (Success=true).
        const int w = 100, h = 30;
        byte[] canvas = MakeSolidBgra(w, h, 10, 20, 30);
        byte[] frame = MakeSolidBgra(w, h, 10, 20, 30);

        // Mutate the left 5% and right 5% of the frame to a different color.
        int side = (int)(w * LongScreenshotStitcher.SideIgnoreRatio);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < side; x++)
            {
                int off = (y * w + x) * 4;
                frame[off] = 200; frame[off + 1] = 200; frame[off + 2] = 200;
            }
            for (int x = w - side; x < w; x++)
            {
                int off = (y * w + x) * 4;
                frame[off] = 200; frame[off + 1] = 200; frame[off + 2] = 200;
            }
        }

        var result = LongScreenshotStitcher.Append(canvas, w, h, frame, h);

        Assert.True(result.Success);
        // Even though edge columns differ, the central columns match fully.
        Assert.Equal(h, result.OverlapRows);
    }

    // NOTE: a BottomIgnoreRatio test is intentionally absent — the bottom
    // footer-trim is deferred to v2 (see LongScreenshotStitcher.cs TODO).
}
