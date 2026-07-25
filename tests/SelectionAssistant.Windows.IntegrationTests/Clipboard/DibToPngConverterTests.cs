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
    public void Convert_CompressedDib_ReturnsNull()
    {
        // BI_BITFIELDS (3) is not supported — only BI_RGB (0).
        byte[] dib = BuildDib(width: 4, height: 3, bitCount: 32, compression: 3);
        var result = DibToPngConverter.ConvertDibToPng(dib);
        Assert.Null(result);
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
