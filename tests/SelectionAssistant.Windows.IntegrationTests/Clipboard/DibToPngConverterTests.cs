using SelectionAssistant.Platform.Windows.Capture;
using SelectionAssistant.Platform.Windows.Clipboard;
using Xunit;

namespace SelectionAssistant.Windows.IntegrationTests.Clipboard;

/// <summary>
/// R54 v2: tests for the CF_DIB → PNG converter. DIB payloads are built by hand
/// (BITMAPINFOHEADER + pixel bytes) so the tests are deterministic and need no
/// sample images. The PNG output is not byte-compared (the encoder is already
/// covered by the Ocean Eyes tests); instead we assert non-null result + correct
/// dimensions for valid inputs, and null for rejected formats.
/// </summary>
public sealed class DibToPngConverterTests
{
    private const int HeaderSize = 40;
    private const int BiRgb = 0;

    /// <summary>Builds a minimal BI_RGB DIB: 40-byte BITMAPINFOHEADER + pixel
    /// data with 4-byte row padding. width/height/bitCount must be consistent.</summary>
    private static byte[] BuildDib(int width, int height, int bitCount, int compression = BiRgb)
    {
        int bytesPerPixel = bitCount / 8;
        int rowStride = ((width * bytesPerPixel + 3) / 4) * 4;
        int pixelBytes = rowStride * Math.Abs(height);
        byte[] dib = new byte[HeaderSize + pixelBytes];

        // BITMAPINFOHEADER (little-endian).
        WriteInt32LE(dib, 0, HeaderSize);          // biSize
        WriteInt32LE(dib, 4, width);                // biWidth
        WriteInt32LE(dib, 8, height);               // biHeight (positive = bottom-up)
        WriteInt16LE(dib, 12, 1);                   // biPlanes
        WriteInt16LE(dib, 14, (short)bitCount);     // biBitCount
        WriteInt32LE(dib, 16, compression);         // biCompression
        WriteInt32LE(dib, 20, pixelBytes);          // biSizeImage
        // Rest of header (resolution/clrUsed/clrImportant) left zero — fine for decode.

        // Fill pixels with a recognizable pattern so we could sanity-check if needed.
        for (int i = HeaderSize; i < dib.Length; i++)
        {
            dib[i] = (byte)(i & 0xFF);
        }
        return dib;
    }

    [Fact]
    public void Convert_32BppRgb_ReturnsPngWithCorrectDimensions()
    {
        byte[] dib = BuildDib(width: 4, height: 3, bitCount: 32);
        var result = DibToPngConverter.ConvertDibToPng(dib);

        Assert.NotNull(result);
        Assert.Equal(4, result!.Value.Width);
        Assert.Equal(3, result.Value.Height);
        Assert.True(result.Value.Png.Length > 0);
        // PNG signature check.
        Assert.Equal(137, result.Value.Png[0]);
        Assert.Equal((byte)'P', result.Value.Png[1]);
        Assert.Equal((byte)'N', result.Value.Png[2]);
        Assert.Equal((byte)'G', result.Value.Png[3]);
    }

    [Fact]
    public void Convert_24BppRgb_ReturnsPngWithCorrectDimensions()
    {
        byte[] dib = BuildDib(width: 5, height: 2, bitCount: 24);
        var result = DibToPngConverter.ConvertDibToPng(dib);

        Assert.NotNull(result);
        Assert.Equal(5, result!.Value.Width);
        Assert.Equal(2, result.Value.Height);
    }

    [Fact]
    public void Convert_TopDownDib_NegativeHeight_Decodes()
    {
        // Negative height = top-down DIB (rare but valid). Build with negative height;
        // row stride is computed from abs(height).
        byte[] dib = BuildDib(width: 3, height: -2, bitCount: 32);
        var result = DibToPngConverter.ConvertDibToPng(dib);

        Assert.NotNull(result);
        Assert.Equal(3, result!.Value.Width);
        Assert.Equal(2, result.Value.Height);
    }

    [Fact]
    public void Convert_BiJpegCompression_ReturnsNull()
    {
        // 4 = BI_JPEG: the payload is a JPEG stream, not raw pixels.
        byte[] dib = BuildDib(width: 4, height: 3, bitCount: 24, compression: 4);
        Assert.Null(DibToPngConverter.ConvertDibToPng(dib));
    }

    // ── 2026-10-30: BI_BITFIELDS (what .NET/System.Drawing and several
    //    browsers/alpha-aware tools put on the clipboard) ──

    private static byte[] BuildBitfieldsDib(
        int width, int height, int bitCount, uint[] masks, byte[]? pixels = null, int headerSize = HeaderSize)
    {
        int bytesPerPixel = bitCount / 8;
        int rowStride = ((width * bytesPerPixel + 3) / 4) * 4;
        int pixelBytes = rowStride * Math.Abs(height);
        // A plain 40-byte header carries its masks as a trailing prefix; V2/V3/
        // V4/V5 headers already contain them, so pixels start right after.
        int pixelStart = headerSize == HeaderSize ? headerSize + masks.Length * 4 : headerSize;
        byte[] dib = new byte[pixelStart + pixelBytes];
        WriteInt32LE(dib, 0, headerSize);            // biSize
        WriteInt32LE(dib, 4, width);                  // biWidth
        WriteInt32LE(dib, 8, height);                 // biHeight
        WriteInt16LE(dib, 12, 1);                     // biPlanes
        WriteInt16LE(dib, 14, (short)bitCount);       // biBitCount
        WriteInt32LE(dib, 16, 3);                     // biCompression = BI_BITFIELDS
        WriteInt32LE(dib, 20, pixelBytes);            // biSizeImage
        for (int i = 0; i < masks.Length; i++)
        {
            WriteUInt32LE(dib, 40 + i * 4, masks[i]);
        }
        for (int i = 0; i < pixelBytes; i++)
        {
            dib[pixelStart + i] = pixels is not null ? pixels[i] : (byte)(i & 0xFF);
        }
        return dib;
    }

    [Fact]
    public void Convert_32BppBitfields_AlphaMask_MatchesBiRgbOutput()
    {
        // Standard BGRA masks and the same pixel bytes must decode identically
        // to the BI_RGB 32-bpp path — this pins mask honouring + alpha handling.
        byte[] rgb = BuildDib(width: 4, height: 3, bitCount: 32);
        byte[] pixels = rgb[HeaderSize..];
        byte[] bitfields = BuildBitfieldsDib(
            width: 4, height: 3, bitCount: 32,
            masks: [0x00FF0000u, 0x0000FF00u, 0x000000FFu, 0xFF000000u],
            pixels: pixels);

        var rgbResult = DibToPngConverter.ConvertDibToPng(rgb);
        var bitfieldsResult = DibToPngConverter.ConvertDibToPng(bitfields);

        Assert.NotNull(bitfieldsResult);
        Assert.Equal(4, bitfieldsResult!.Value.Width);
        Assert.Equal(3, bitfieldsResult.Value.Height);
        Assert.Equal(rgbResult!.Value.Png, bitfieldsResult.Value.Png);
    }

    [Fact]
    public void Convert_16BppBitfields565_ScalesChannelsTo8Bit()
    {
        // 5-6-5 masks on a single row (no flip) — output must match the same
        // pixels expressed as BGRA and encoded by the shared PNG encoder.
        ushort[] samples = [0xF800, 0x07E0, 0x001F]; // red, green, blue
        byte[] pixels = new byte[8]; // 3 px * 2 B = 6 B, padded to 8
        for (int i = 0; i < samples.Length; i++)
        {
            pixels[i * 2] = (byte)(samples[i] & 0xFF);
            pixels[i * 2 + 1] = (byte)(samples[i] >> 8);
        }
        byte[] dib = BuildBitfieldsDib(
            width: 3, height: 1, bitCount: 16,
            masks: [0xF800u, 0x07E0u, 0x001Fu],
            pixels: pixels);

        var result = DibToPngConverter.ConvertDibToPng(dib);

        byte[] expectedBgra =
        [
            0, 0, 255, 255, // red
            0, 255, 0, 255, // green
            255, 0, 0, 255, // blue
        ];
        byte[] expectedPng = ScreenRegionCapture.EncodeBgraToPng(expectedBgra, 3, 1);
        Assert.NotNull(result);
        Assert.Equal(3, result!.Value.Width);
        Assert.Equal(1, result.Value.Height);
        Assert.Equal(expectedPng, result.Value.Png);
    }

    [Fact]
    public void Convert_32BppBitfields_ZeroAlphaBytes_TreatedAsOpaque()
    {
        // Producers that ship an alpha mask with all-zero alpha bytes must not
        // yield an invisible image.
        byte[] rgb = BuildDib(width: 3, height: 2, bitCount: 32);
        byte[] pixels = (byte[])rgb[HeaderSize..].Clone();
        for (int i = 3; i < pixels.Length; i += 4)
        {
            pixels[i] = 0; // wipe alpha
        }
        byte[] bitfields = BuildBitfieldsDib(
            width: 3, height: 2, bitCount: 32,
            masks: [0x00FF0000u, 0x0000FF00u, 0x000000FFu, 0xFF000000u],
            pixels: pixels);

        byte[] expectedPixels = (byte[])pixels.Clone();
        for (int i = 3; i < expectedPixels.Length; i += 4)
        {
            expectedPixels[i] = 0xFF;
        }
        byte[] rgbDib = BuildDib(width: 3, height: 2, bitCount: 32);
        Array.Copy(expectedPixels, 0, rgbDib, HeaderSize, expectedPixels.Length);

        var result = DibToPngConverter.ConvertDibToPng(bitfields);
        var expected = DibToPngConverter.ConvertDibToPng(rgbDib);

        Assert.NotNull(result);
        Assert.Equal(expected!.Value.Png, result!.Value.Png);
    }

    [Fact]
    public void Convert_BitfieldsV5Header_Decodes()
    {
        // BITMAPV5HEADER (124 bytes) carries its masks inside the header and the
        // pixels start after it.
        byte[] rgb = BuildDib(width: 2, height: 2, bitCount: 32);
        byte[] pixels = (byte[])rgb[HeaderSize..].Clone();
        for (int i = 3; i < pixels.Length; i += 4)
        {
            pixels[i] = 0xFF;
        }
        byte[] v5 = BuildBitfieldsDib(
            width: 2, height: 2, bitCount: 32,
            masks: [0x00FF0000u, 0x0000FF00u, 0x000000FFu, 0xFF000000u],
            pixels: pixels,
            headerSize: 124);
        byte[] rgbDib = BuildDib(width: 2, height: 2, bitCount: 32);
        Array.Copy(pixels, 0, rgbDib, HeaderSize, pixels.Length);

        var result = DibToPngConverter.ConvertDibToPng(v5);
        var expected = DibToPngConverter.ConvertDibToPng(rgbDib);

        Assert.NotNull(result);
        Assert.Equal(2, result!.Value.Width);
        Assert.Equal(2, result.Value.Height);
        Assert.Equal(expected!.Value.Png, result.Value.Png);
    }

    [Fact]
    public void Convert_BitfieldsWithoutMasks_ReturnsNull()
    {
        // BI_BITFIELDS but no trailing/embedded masks: nothing to decode with.
        byte[] dib = BuildDib(width: 4, height: 3, bitCount: 32, compression: 3);
        Assert.Null(DibToPngConverter.ConvertDibToPng(dib));
    }

    [Fact]
    public void Convert_24BppBitfields_ReturnsNull()
    {
        // 24-bpp BITFIELDS has no meaningful mask layout for this decoder.
        byte[] dib = BuildBitfieldsDib(
            width: 4, height: 3, bitCount: 24,
            masks: [0x00FF0000u, 0x0000FF00u, 0x000000FFu]);
        Assert.Null(DibToPngConverter.ConvertDibToPng(dib));
    }

    [Fact]
    public void Convert_BitfieldsTruncatedPixels_ReturnsNull()
    {
        byte[] dib = BuildBitfieldsDib(
            width: 4, height: 3, bitCount: 32,
            masks: [0x00FF0000u, 0x0000FF00u, 0x000000FFu]);
        Array.Resize(ref dib, HeaderSize + 12 + 8); // masks kept, pixels chopped
        Assert.Null(DibToPngConverter.ConvertDibToPng(dib));
    }

    [Fact]
    public void Convert_UnsupportedBitDepth_ReturnsNull()
    {
        // 8-bpp needs a color table; not supported.
        byte[] dib = BuildDib(width: 4, height: 3, bitCount: 8);
        var result = DibToPngConverter.ConvertDibToPng(dib);
        Assert.Null(result);
    }

    [Fact]
    public void Convert_TruncatedDib_ReturnsNull()
    {
        // Just the header, no pixels.
        byte[] dib = BuildDib(width: 4, height: 3, bitCount: 32);
        Array.Resize(ref dib, HeaderSize); // chop off all pixel data
        var result = DibToPngConverter.ConvertDibToPng(dib);
        Assert.Null(result);
    }

    [Fact]
    public void Convert_TooShortForHeader_ReturnsNull()
    {
        byte[] dib = new byte[10]; // less than 40-byte header
        var result = DibToPngConverter.ConvertDibToPng(dib);
        Assert.Null(result);
    }

    [Fact]
    public void Convert_ZeroDimension_ReturnsNull()
    {
        byte[] dib = BuildDib(width: 0, height: 3, bitCount: 32);
        var result = DibToPngConverter.ConvertDibToPng(dib);
        Assert.Null(result);
    }

    [Fact]
    public void Convert_MalformedHugeDimensions_ReturnsNullWithoutAllocating()
    {
        // A clipboard producer can advertise dimensions that do not fit in the
        // payload. The converter must reject them before row-stride/pixel-size
        // arithmetic wraps or attempts a giant allocation.
        byte[] dib = new byte[HeaderSize];
        WriteInt32LE(dib, 0, HeaderSize);
        WriteInt32LE(dib, 4, int.MaxValue);
        WriteInt32LE(dib, 8, int.MaxValue);
        WriteInt16LE(dib, 12, 1);
        WriteInt16LE(dib, 14, 32);
        WriteInt32LE(dib, 16, 0);

        Assert.Null(DibToPngConverter.ConvertDibToPng(dib));
    }

    [Fact]
    public void Convert_IntMinHeight_ReturnsNullWithoutOverflow()
    {
        byte[] dib = BuildDib(width: 1, height: 1, bitCount: 32);
        WriteInt32LE(dib, 8, int.MinValue);

        Assert.Null(DibToPngConverter.ConvertDibToPng(dib));
    }

    private static void WriteInt32LE(byte[] data, int offset, int value)
    {
        data[offset] = (byte)(value & 0xFF);
        data[offset + 1] = (byte)((value >> 8) & 0xFF);
        data[offset + 2] = (byte)((value >> 16) & 0xFF);
        data[offset + 3] = (byte)((value >> 24) & 0xFF);
    }

    private static void WriteUInt32LE(byte[] data, int offset, uint value) =>
        WriteInt32LE(data, offset, unchecked((int)value));

    private static void WriteInt16LE(byte[] data, int offset, short value)
    {
        data[offset] = (byte)(value & 0xFF);
        data[offset + 1] = (byte)((value >> 8) & 0xFF);
    }
}
