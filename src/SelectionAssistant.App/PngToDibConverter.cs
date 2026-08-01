using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using SelectionAssistant.Platform.Windows.Capture;

namespace SelectionAssistant.App;

/// <summary>
/// R54 v2: converts a PNG byte[] into a <c>CF_DIB</c> payload
/// (BITMAPINFOHEADER + bottom-up BGRA pixels) suitable for
/// <see cref="Platform.Windows.Clipboard.Win32Clipboard.SetImageDib"/>. This is
/// the paste-back path for image entries: we store only the PNG on disk (full
/// resolution, used for thumbnails + previews), and rebuild a DIB at paste time
/// so every Windows app (Word/Paint/chat clients — all read CF_DIB) can paste
/// the image. Previously we relied on a .dib file captured at copy time, but
/// legacy entries (captured before that code shipped) had no .dib and fell back
/// to CF_PNG which most apps ignore → "copy doesn't work".
/// </summary>
/// <remarks>
/// <b>AOT safety:</b> uses <see cref="Bitmap"/> + the nint overload of
/// <c>CopyPixels</c> (the byte[] overload hits an Avalonia 12 stride bug — see
/// SelectionRuntime.DecodePngToBgra). No reflection. The Avalonia imaging path
/// is the same one Ocean Eyes / gallery already use under NativeAOT.
/// <para>
/// <b>Row order:</b> Avalonia's CopyPixels yields top-down rows; CF_DIB with a
/// positive biHeight is bottom-up, so we flip the row order when emitting.
/// </para>
/// </remarks>
public static class PngToDibConverter
{
    private const int BitmapInfoHeaderSize = 40;
    private const int BiRgb = 0;
    private const int MaxDibBytes = 32 * 1024 * 1024;

    /// <summary>Converts PNG bytes to a CF_DIB payload. Returns null on any
    /// decode failure (caller logs + falls back). Never throws.</summary>
    public static byte[]? ConvertPngToDib(byte[] png)
    {
        ArgumentNullException.ThrowIfNull(png);
        if (png.Length == 0)
        {
            return null;
        }

        byte[]? bgra;
        int width, height;
        // R54 v2 bug fix: swallow decode exceptions and return null instead of
        // propagating. Avalonia 12's Bitmap.CopyPixels throws
        // ArgumentOutOfRangeException('stride') on certain PNGs (e.g. some Ocean
        // Eyes screenshots) — this is a known framework bug, not a recoverable
        // error. Propagating it crashed SaveOceanEyesScreenshot mid-save,
        // preventing the clipboard write from ever running. Returning null lets
        // the caller degrade gracefully (CF_PNG only). Matches
        // SelectionRuntime.DecodePngToBgra's error handling.
        try
        {
            bgra = DecodePngToBgra(png, out width, out height);
        }
        catch
        {
            return null;
        }

        if (bgra is null || width <= 0 || height <= 0)
        {
            return null;
        }

        return BuildDibFromBgra(bgra, width, height);
    }

    /// <summary>
    /// R54 v2 bug fix: converts a raw top-down BGRA pixel buffer (exactly what
    /// Ocean Eyes captures via BitBlt, before any PNG round-trip) directly into a
    /// CF_DIB payload. This bypasses Avalonia's Bitmap.CopyPixels entirely,
    /// avoiding the framework's stride bug that throws on many PNGs. Used by
    /// SaveOceanEyesScreenshot when the raw BGRA buffer is available — which is
    /// always the case in the normal Ocean Eyes capture path. When annotations
    /// are present, the caller passes the annotation-burned BGRA so the DIB
    /// matches the on-screen result.
    /// </summary>
    /// <param name="bgra">Top-down BGRA buffer (row 0 = top). Length must equal
    /// <c>width * height * 4</c>.</param>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <returns>CF_DIB payload, or null if the buffer dimensions are
    /// inconsistent. Never throws.</returns>
    public static byte[]? ConvertBgraToDib(byte[] bgra, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        if (!TryGetDibSize(width, height, out int expectedBgraBytes, out _))
        {
            return null;
        }
        if (bgra.Length != expectedBgraBytes)
        {
            return null;
        }
        return BuildDibFromBgra(bgra, width, height);
    }

    /// <summary>Decodes a PNG to a top-down BGRA buffer via Avalonia, using the
    /// nint CopyPixels overload to avoid the Avalonia 12 byte[] stride bug.
    /// Mirrors SelectionRuntime.DecodePngToBgra.</summary>
    private static byte[]? DecodePngToBgra(byte[] png, out int width, out int height)
    {
        width = height = 0;
        using var stream = new MemoryStream(png);
        using var bmp = new Bitmap(stream);
        PixelSize pixelSize = bmp.PixelSize;
        width = pixelSize.Width;
        height = pixelSize.Height;
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        if (!TryGetDibSize(width, height, out int totalBytes, out _))
        {
            return null;
        }

        int stride = checked(width * 4);
        var bgra = new byte[totalBytes];
        nint nativeBuffer = Marshal.AllocHGlobal(totalBytes);
        try
        {
            bmp.CopyPixels(new PixelRect(0, 0, width, height), nativeBuffer, stride, 0);
            Marshal.Copy(nativeBuffer, bgra, 0, totalBytes);
        }
        finally
        {
            Marshal.FreeHGlobal(nativeBuffer);
        }
        return bgra;
    }

    /// <summary>Builds a CF_DIB (BITMAPINFOHEADER + bottom-up BGRA) from a
    /// top-down BGRA buffer. 32bpp rows are already 4-byte aligned (no extra
    /// padding needed). Rows are reversed so biHeight &gt; 0 = bottom-up.</summary>
    private static byte[]? BuildDibFromBgra(byte[] topDownBgra, int width, int height)
    {
        if (!TryGetDibSize(width, height, out int pixelBytes, out int rowStride))
        {
            return null;
        }

        byte[] dib = new byte[checked(BitmapInfoHeaderSize + pixelBytes)];

        // BITMAPINFOHEADER (little-endian).
        WriteInt32LE(dib, 0, BitmapInfoHeaderSize); // biSize
        WriteInt32LE(dib, 4, width);                // biWidth
        WriteInt32LE(dib, 8, height);               // biHeight (positive = bottom-up)
        WriteInt16LE(dib, 12, 1);                   // biPlanes
        WriteInt16LE(dib, 14, 32);                  // biBitCount
        WriteInt32LE(dib, 16, BiRgb);               // biCompression
        WriteInt32LE(dib, 20, pixelBytes);          // biSizeImage
        // biXPelsPerMeter/biYPelsPerMeter/biClrUsed/biClrImportant left 0.

        // Copy rows in reverse order: Avalonia gives top-down (row 0 = top);
        // CF_DIB with positive height stores row 0 = bottom.
        for (int dstRow = 0; dstRow < height; dstRow++)
        {
            int srcRow = height - 1 - dstRow;
            int srcOffset = srcRow * rowStride;
            int dstOffset = BitmapInfoHeaderSize + dstRow * rowStride;
            Buffer.BlockCopy(topDownBgra, srcOffset, dib, dstOffset, rowStride);
        }
        return dib;
    }

    private static bool TryGetDibSize(
        int width,
        int height,
        out int bgraBytes,
        out int rowStride)
    {
        bgraBytes = 0;
        rowStride = 0;
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        long rowBytes = (long)width * 4;
        long pixelBytes = rowBytes * height;
        long dibBytes = BitmapInfoHeaderSize + pixelBytes;
        if (rowBytes <= 0 || pixelBytes <= 0 || pixelBytes > ScreenRegionCapture.MaxPixelBufferBytes ||
            pixelBytes > int.MaxValue || dibBytes > MaxDibBytes)
        {
            return false;
        }

        bgraBytes = (int)pixelBytes;
        rowStride = (int)rowBytes;
        return true;
    }

    private static void WriteInt32LE(byte[] data, int offset, int value)
    {
        data[offset] = (byte)(value & 0xFF);
        data[offset + 1] = (byte)((value >> 8) & 0xFF);
        data[offset + 2] = (byte)((value >> 16) & 0xFF);
        data[offset + 3] = (byte)((value >> 24) & 0xFF);
    }

    private static void WriteInt16LE(byte[] data, int offset, short value)
    {
        data[offset] = (byte)(value & 0xFF);
        data[offset + 1] = (byte)((value >> 8) & 0xFF);
    }
}
