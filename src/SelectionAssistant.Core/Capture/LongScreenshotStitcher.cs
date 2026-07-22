namespace SelectionAssistant.Core.Capture;

/// <summary>
/// R53: result of appending one captured frame to the growing long-screenshot
/// canvas. Pure data — no I/O, no allocation side effects beyond the merged
/// buffer carried in <see cref="MergedBgra"/>.
/// </summary>
public readonly record struct LongScreenshotStitchResult(
    byte[] MergedBgra,
    int Width,
    int Height,
    int OverlapRows,
    bool Success)
{
    /// <summary>Overlap sentinel meaning "no row band matched — frame appended in full".</summary>
    public const int NoOverlap = -1;
}

/// <summary>
/// R53: pixel-row-overlap stitcher for scrolling screenshots. Pure function,
/// no I/O, no P/Invoke, fully unit-testable. Mirrors the core idea of ShareX's
/// <c>ScrollingCaptureManager.CombineImages</c>: find the largest row band at
/// the bottom of the canvas that equals the same-sized band at the top of the
/// new frame, then append only the non-overlapping tail. When no overlap is
/// found at all, the entire frame is appended (OverlapRows =
/// <see cref="LongScreenshotStitchResult.NoOverlap"/>, Success=false) so the
/// caller can surface a seam warning instead of silently dropping pixels.
/// </summary>
/// <remarks>
/// <para><b>Buffer contract</b>: inputs are top-down 32-bit BGRA byte arrays,
/// <c>stride = width * 4</c>, pixel (x,y) at offset <c>(y*width + x)*4</c>.
/// Identical to <c>ScreenRegionCapture.CaptureRawBgra</c> output and the
/// <c>BurnInHelpers</c> convention.</para>
/// <para><b>v1 implementation</b>: row comparison is a plain <c>for</c> loop.
/// A <c>msvcrt.memcmp</c> P/Invoke (≈10× faster) is deferred to v2 to keep v1
/// P/Invoke-free and the algorithm fully unit-testable in isolation. Typical
/// cost per frame at 1080p with ~900-row overlap is ~50–150 ms on a background
/// <c>Task.Run</c> — no UI thread impact.</para>
/// </remarks>
public static class LongScreenshotStitcher
{
    /// <summary>
    /// Fraction of width ignored on each side (left + right) during row
    /// comparison, to tolerate sidebar drift / scrollbar width changes between
    /// frames. ShareX empirical default.
    /// </summary>
    public const double SideIgnoreRatio = 0.05;

    // v2 TODO: BottomIgnoreRatio (trim the frame's bottom N% before stitching to
    // drop pinned footer bars like cookie consent / status bars that don't scroll
    // with content). Intentionally NOT in v1: the trim-vs-append semantics are
    // subtle (trimmed rows must be discarded from both comparison AND append to
    // avoid the footer re-appearing every frame), and in manual-scroll mode the
    // user controls exactly what's in the selected region, so footers are rarely
    // an issue. Revisit when adding auto-scroll (v2), where footers pollute
    // overlap detection more aggressively.

    /// <summary>
    /// Appends <paramref name="frameBgra"/> (width × <paramref name="frameHeight"/>)
    /// to <paramref name="canvasBgra"/> (width × <paramref name="canvasHeight"/>),
    /// returning the merged canvas. <paramref name="canvasWidth"/> must equal the
    /// frame width; otherwise an <see cref="ArgumentException"/> is thrown (a
    /// region whose width changes mid-capture cannot be stitched).
    /// </summary>
    /// <param name="canvasBgra">Existing canvas BGRA buffer (length = canvasWidth*canvasHeight*4).</param>
    /// <param name="canvasWidth">Shared canvas/frame width in pixels.</param>
    /// <param name="canvasHeight">Current canvas height in pixels.</param>
    /// <param name="frameBgra">New frame BGRA buffer (length = canvasWidth*frameHeight*4).</param>
    /// <param name="frameHeight">New frame height in pixels.</param>
    public static LongScreenshotStitchResult Append(
        byte[] canvasBgra, int canvasWidth, int canvasHeight,
        byte[] frameBgra, int frameHeight)
    {
        if (canvasWidth <= 0) throw new ArgumentException("Width must be positive.", nameof(canvasWidth));
        if (canvasHeight <= 0) throw new ArgumentException("Canvas height must be positive.", nameof(canvasHeight));
        if (frameHeight <= 0) throw new ArgumentException("Frame height must be positive.", nameof(frameHeight));
        if (canvasBgra.Length != canvasWidth * canvasHeight * 4)
        {
            throw new ArgumentException(
                $"canvasBgra length ({canvasBgra.Length}) != canvasWidth*canvasHeight*4 ({canvasWidth * canvasHeight * 4}).",
                nameof(canvasBgra));
        }
        if (frameBgra.Length != canvasWidth * frameHeight * 4)
        {
            throw new ArgumentException(
                $"frameBgra length ({frameBgra.Length}) != canvasWidth*frameHeight*4 ({canvasWidth * frameHeight * 4}).",
                nameof(frameBgra));
        }

        // Comparison window: ignore left/right SideIgnoreRatio of the width to
        // tolerate sidebar drift / scrollbar width jitter. (Footer-bottom trim
        // is a v2 concern — see BottomIgnoreRatio TODO above.)
        int sideIgnore = (int)(canvasWidth * SideIgnoreRatio);
        int compareStartX = sideIgnore;
        int compareEndX = canvasWidth - sideIgnore;
        if (compareEndX <= compareStartX)
        {
            // Degenerate (extremely narrow region) — fall back to full width.
            compareStartX = 0;
            compareEndX = canvasWidth;
        }

        // Search from the LARGEST candidate overlap downward so the first match
        // is the longest. Overlap cannot exceed min(canvas, frame) heights.
        int maxPossibleOverlap = Math.Min(canvasHeight, frameHeight);
        int bestOverlap = LongScreenshotStitchResult.NoOverlap;

        for (int overlap = maxPossibleOverlap; overlap >= 1; overlap--)
        {
            if (RowsMatch(
                    canvasBgra, canvasHeight - overlap,
                    frameBgra, 0,
                    overlap, canvasWidth, compareStartX, compareEndX))
            {
                bestOverlap = overlap;
                break;
            }
        }

        // Stitch: copy canvas verbatim, then append the non-overlapping tail of
        // the frame. On full-match failure (NoOverlap) the entire frame is
        // appended so the user sees a seam warning rather than missing content.
        int appendedRows = bestOverlap == LongScreenshotStitchResult.NoOverlap
            ? frameHeight
            : frameHeight - bestOverlap;
        int newHeight = canvasHeight + appendedRows;
        int bytesPerPixel = 4;
        int canvasBytes = canvasBgra.Length;
        byte[] merged = new byte[canvasWidth * newHeight * bytesPerPixel];

        Buffer.BlockCopy(canvasBgra, 0, merged, 0, canvasBytes);

        int srcFrameOffset = bestOverlap == LongScreenshotStitchResult.NoOverlap
            ? 0
            : bestOverlap * canvasWidth * bytesPerPixel;
        Buffer.BlockCopy(frameBgra, srcFrameOffset, merged, canvasBytes,
            appendedRows * canvasWidth * bytesPerPixel);

        return new LongScreenshotStitchResult(
            merged, canvasWidth, newHeight, bestOverlap, bestOverlap != LongScreenshotStitchResult.NoOverlap);
    }

    /// <summary>
    /// Checks whether <paramref name="rowCount"/> consecutive rows of the canvas
    /// (starting at <paramref name="canvasStartRow"/>) equal the same number of
    /// consecutive rows of the frame (starting at <paramref name="frameStartRow"/>),
    /// comparing only columns [<paramref name="compareStartX"/>,
    /// <paramref name="compareEndX"/>). Early-exits on the first mismatched byte.
    /// </summary>
    private static bool RowsMatch(
        byte[] canvas, int canvasStartRow,
        byte[] frame, int frameStartRow,
        int rowCount, int width, int compareStartX, int compareEndX)
    {
        int bytesPerRow = width * 4;
        int compareStartByte = compareStartX * 4;
        int compareByteLen = (compareEndX - compareStartX) * 4;

        for (int r = 0; r < rowCount; r++)
        {
            int canvasOff = (canvasStartRow + r) * bytesPerRow + compareStartByte;
            int frameOff = (frameStartRow + r) * bytesPerRow + compareStartByte;
            // v1: plain byte loop. v2: swap for msvcrt.memcmp (~10× faster).
            for (int i = 0; i < compareByteLen; i++)
            {
                if (canvas[canvasOff + i] != frame[frameOff + i])
                {
                    return false;
                }
            }
        }
        return true;
    }
}
