using SelectionAssistant.Platform.Windows.Capture;

namespace SelectionAssistant.Platform.Windows.Clipboard;

/// <summary>
/// R54 v2: converts a Windows <c>CF_DIB</c> clipboard payload (a packed
/// <c>BITMAPINFOHEADER</c> + pixel data, bottom-up scanlines, 4-byte row
/// alignment) into a PNG byte[] the history store can write to disk. Only the
/// common, uncompressed (<c>BI_RGB</c>) 24- and 32-bpp variants produced by
/// screenshots and image editors are supported; anything else returns null so
/// the caller can skip the capture without crashing.
/// </summary>
/// <remarks>
/// The resulting PNG is produced by the existing <see cref="ScreenRegionCapture.EncodeBgraToPng"/>
/// (the same encoder Ocean Eyes uses), so no new image dependency is added and
/// NativeAOT stays clean. The DIB's bottom-up scan order is flipped to the
/// top-down order <see cref="ScreenRegionCapture"/> expects.
/// </remarks>
public static class DibToPngConverter
{
    private const int BitmapInfoHeaderSize = 40;
    private const int BiRgb = 0;

    /// <summary>Converts a <c>CF_DIB</c> byte payload to PNG bytes plus the
    /// pixel dimensions. Returns null for truncated data, unsupported
    /// compression, unsupported bit depths, or a payload that is too small to
    /// hold the declared pixels (never throws).</summary>
    public static (byte[] Png, int Width, int Height)? ConvertDibToPng(byte[] dib)
    {
        ArgumentNullException.ThrowIfNull(dib);
        if (dib.Length < BitmapInfoHeaderSize)
        {
            return null;
        }

        // BITMAPINFOHEADER (little-endian): width(4) @4, height(4) @8,
        // planes(2) @12, bitCount(2) @14, compression(4) @16.
        int width = ReadInt32LittleEndian(dib, 4);
        int height = ReadInt32LittleEndian(dib, 8);
        int bitCount = ReadInt16LittleEndian(dib, 14);
        int compression = ReadInt32LittleEndian(dib, 16);

        if (width <= 0 || height == 0 || compression != BiRgb)
        {
            return null;
        }

        // Negative height = top-down DIB (rare). Normalize to positive height
        // and remember whether to flip.
        bool topDown = height < 0;
        int absHeight = Math.Abs(height);
        if (absHeight <= 0)
        {
            return null;
        }

        // Only 24-bpp (BGR) and 32-bpp (BGRA) BI_RGB are handled. Other depths
        // need a color table and are uncommon for clipboard image copies.
        int bytesPerPixel = bitCount switch
        {
            24 => 3,
            32 => 4,
            _ => 0,
        };
        if (bytesPerPixel == 0)
        {
            return null;
        }

        // DIB scanlines are padded to a 4-byte boundary.
        int rowStride = ((width * bytesPerPixel + 3) / 4) * 4;
        int pixelDataStart = BitmapInfoHeaderSize; // BI_RGB 24/32bpp has no color table
        int expectedBytes = pixelDataStart + rowStride * absHeight;
        if (dib.Length < expectedBytes)
        {
            return null;
        }

        // Emit 32-bit BGRA (what ScreenRegionCapture.EncodeBgraToPng expects),
        // flipping bottom-up → top-down unless the DIB is already top-down.
        byte[] bgra = new byte[width * absHeight * 4];
        for (int dstRow = 0; dstRow < absHeight; dstRow++)
        {
            // Source row: bottom-up DIBs store the last scanline first.
            int srcRow = topDown ? dstRow : (absHeight - 1 - dstRow);
            int srcOffset = pixelDataStart + srcRow * rowStride;
            int dstOffset = dstRow * width * 4;
            for (int x = 0; x < width; x++)
            {
                byte b = dib[srcOffset + x * bytesPerPixel];
                byte g = dib[srcOffset + x * bytesPerPixel + 1];
                byte r = dib[srcOffset + x * bytesPerPixel + 2];
                byte a = bytesPerPixel == 4
                    ? dib[srcOffset + x * bytesPerPixel + 3]
                    : (byte)0xFF; // 24-bpp has no alpha → opaque
                int d = dstOffset + x * 4;
                bgra[d] = b;
                bgra[d + 1] = g;
                bgra[d + 2] = r;
                bgra[d + 3] = a;
            }
        }

        byte[] png = ScreenRegionCapture.EncodeBgraToPng(bgra, width, absHeight);
        return (png, width, absHeight);
    }

    private static int ReadInt32LittleEndian(byte[] data, int offset) =>
        data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24);

    private static int ReadInt16LittleEndian(byte[] data, int offset) =>
        data[offset] | (data[offset + 1] << 8);
}
