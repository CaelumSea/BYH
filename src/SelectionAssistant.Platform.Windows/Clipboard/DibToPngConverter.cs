using System.Numerics;
using SelectionAssistant.Platform.Windows.Capture;

namespace SelectionAssistant.Platform.Windows.Clipboard;

/// <summary>
/// R54 v2: converts a Windows <c>CF_DIB</c> clipboard payload (a packed
/// <c>BITMAPINFOHEADER</c> + pixel data, bottom-up scanlines, 4-byte row
/// alignment) into a PNG byte[] the history store can write to disk.
/// </summary>
/// <remarks>
/// <para>Supported formats: uncompressed <c>BI_RGB</c> 24/32-bpp (screenshots,
/// most editors) and <c>BI_BITFIELDS</c> 32/16-bpp (what .NET/System.Drawing,
/// several browsers and alpha-aware capture tools put on the clipboard — these
/// used to be rejected outright, so those images never entered the history).
/// Anything else returns null so the caller can skip the capture without
/// crashing.</para>
/// <para>BITFIELDS masks are read from wherever Windows put them: inside the
/// header for V2/V3/V4/V5 payloads (<c>biSize</c> ≥ 52) or as 12/16 trailing
/// bytes after a plain 40-byte <c>BITMAPINFOHEADER</c>. Channel order therefore
/// follows the mask, not the byte order. Producers that ship an alpha mask
/// while every alpha byte is zero (a common bug that renders the pasted image
/// fully transparent) are treated as opaque.</para>
/// <para>The resulting PNG is produced by the existing
/// <see cref="ScreenRegionCapture.EncodeBgraToPng"/> (the same encoder Ocean
/// Eyes uses), so no new image dependency is added and NativeAOT stays clean.
/// The DIB's bottom-up scan order is flipped to the top-down order
/// <see cref="ScreenRegionCapture"/> expects.</para>
/// </remarks>
public static class DibToPngConverter
{
    private const int BitmapInfoHeaderSize = 40;
    private const int BitmapV2HeaderSize = 52; // + RGB masks
    private const int BitmapV3HeaderSize = 56; // + alpha mask
    private const int BiRgb = 0;
    private const int BiBitfields = 3;
    private const int TrailingMaskBytesRgb = 12;
    private const int TrailingMaskBytesRgba = 16;
    private const int MaxDibBytes = 32 * 1024 * 1024;

    /// <summary>Converts a <c>CF_DIB</c> byte payload to PNG bytes plus the
    /// pixel dimensions. Returns null for truncated data, unsupported
    /// compression, unsupported bit depths, or a payload that is too small to
    /// hold the declared pixels (never throws).</summary>
    /// <remarks>Convenience overload for non-pooled byte arrays. Equivalent to
    /// <see cref="ConvertDibToPng(byte[], int)"/> with <c>length = dib.Length</c>.</remarks>
    public static (byte[] Png, int Width, int Height)? ConvertDibToPng(byte[] dib)
    {
        ArgumentNullException.ThrowIfNull(dib);
        return ConvertDibToPng(dib, dib.Length);
    }

    /// <summary>Converts a <c>CF_DIB</c> byte payload to PNG bytes plus the
    /// pixel dimensions. Returns null for truncated data, unsupported
    /// compression, unsupported bit depths, or a payload that is too small to
    /// hold the declared pixels (never throws).</summary>
    /// <param name="length">The number of valid bytes in <paramref name="dib"/>.
    /// P2: when <paramref name="dib"/> is an oversized ArrayPool rental, only
    /// the first <paramref name="length"/> bytes are the real DIB; reading
    /// <c>dib.Length</c> would read garbage padding.</param>
    public static (byte[] Png, int Width, int Height)? ConvertDibToPng(byte[] dib, int length)
    {
        ArgumentNullException.ThrowIfNull(dib);
        if ((uint)length > (uint)dib.Length)
        {
            return null;
        }
        if (length < BitmapInfoHeaderSize || length > MaxDibBytes)
        {
            return null;
        }

        // BITMAPINFOHEADER (little-endian): biSize(4) @0, width(4) @4,
        // height(4) @8, planes(2) @12, bitCount(2) @14, compression(4) @16.
        int biSize = ReadInt32LittleEndian(dib, 0);
        int width = ReadInt32LittleEndian(dib, 4);
        int height = ReadInt32LittleEndian(dib, 8);
        int bitCount = ReadInt16LittleEndian(dib, 14);
        int compression = ReadInt32LittleEndian(dib, 16);

        if (width <= 0 || height == 0)
        {
            return null;
        }

        bool bitfields = compression == BiBitfields;
        if (compression != BiRgb && !bitfields)
        {
            return null;
        }
        if (bitfields && (biSize < BitmapInfoHeaderSize || biSize > length))
        {
            return null;
        }

        // Negative height = top-down DIB (rare). Normalize to positive height
        // and remember whether to flip.
        bool topDown = height < 0;
        // Math.Abs(int.MinValue) throws and a hostile/malformed DIB can also
        // make the row arithmetic wrap. Reject those declarations before any
        // allocation or indexing.
        if (height == int.MinValue)
        {
            return null;
        }
        int absHeight = Math.Abs(height);
        if (absHeight <= 0)
        {
            return null;
        }

        // BI_RGB: 24-bpp BGR and 32-bpp BGRA (no color table). BI_BITFIELDS:
        // 32-bpp (masks carry channel order) and 16-bpp (e.g. 5-6-5).
        int bytesPerPixel = bitfields
            ? bitCount switch { 16 => 2, 32 => 4, _ => 0 }
            : bitCount switch { 24 => 3, 32 => 4, _ => 0 };
        if (bytesPerPixel == 0)
        {
            return null;
        }

        // DIB scanlines are padded to a 4-byte boundary.
        long rowBytes = (long)width * bytesPerPixel;
        long rowStrideLong = ((rowBytes + 3) / 4) * 4;
        if (rowBytes <= 0 || rowStrideLong <= 0 || rowStrideLong > int.MaxValue)
        {
            return null;
        }

        long pixelBytesLong = rowStrideLong * absHeight;
        if (pixelBytesLong <= 0 || pixelBytesLong > MaxDibBytes)
        {
            return null;
        }

        // Where the pixel data starts, and (for BITFIELDS) the channel masks.
        uint maskR = 0;
        uint maskG = 0;
        uint maskB = 0;
        uint maskA = 0;
        int pixelDataStart;
        if (bitfields)
        {
            (maskR, maskG, maskB, maskA, pixelDataStart) =
                ResolveBitfieldMasks(dib, length, biSize, pixelBytesLong);
            if (maskR == 0 || maskG == 0 || maskB == 0 || pixelDataStart < 0)
            {
                return null;
            }
        }
        else
        {
            pixelDataStart = BitmapInfoHeaderSize; // BI_RGB 24/32bpp has no color table
        }

        if (pixelDataStart + pixelBytesLong > length ||
            pixelDataStart + pixelBytesLong > MaxDibBytes)
        {
            return null;
        }

        int rowStride = (int)rowStrideLong;

        // Emit 32-bit BGRA (what ScreenRegionCapture.EncodeBgraToPng expects),
        // flipping bottom-up → top-down unless the DIB is already top-down.
        long bgraBytesLong = (long)width * absHeight * 4;
        if (bgraBytesLong <= 0 || bgraBytesLong > ScreenRegionCapture.MaxPixelBufferBytes ||
            bgraBytesLong > int.MaxValue)
        {
            return null;
        }

        byte[] bgra = new byte[(int)bgraBytesLong];
        if (!bitfields)
        {
            for (int dstRow = 0; dstRow < absHeight; dstRow++)
            {
                // Source row: bottom-up DIBs store the last scanline first.
                int srcRow = topDown ? dstRow : (absHeight - 1 - dstRow);
                int srcOffset = checked(pixelDataStart + srcRow * rowStride);
                int dstOffset = checked(dstRow * width * 4);
                for (int x = 0; x < width; x++)
                {
                    int s = srcOffset + x * bytesPerPixel;
                    int d = dstOffset + x * 4;
                    bgra[d] = dib[s];
                    bgra[d + 1] = dib[s + 1];
                    bgra[d + 2] = dib[s + 2];
                    bgra[d + 3] = bytesPerPixel == 4 ? dib[s + 3] : (byte)0xFF; // 24-bpp has no alpha
                }
            }
        }
        else
        {
            (int rShift, uint rMax) = MaskGeometry(maskR);
            (int gShift, uint gMax) = MaskGeometry(maskG);
            (int bShift, uint bMax) = MaskGeometry(maskB);
            (int aShift, uint aMax) = MaskGeometry(maskA);
            bool sawAlpha = false;

            for (int dstRow = 0; dstRow < absHeight; dstRow++)
            {
                int srcRow = topDown ? dstRow : (absHeight - 1 - dstRow);
                int srcOffset = checked(pixelDataStart + srcRow * rowStride);
                int dstOffset = checked(dstRow * width * 4);
                for (int x = 0; x < width; x++)
                {
                    uint sample = bytesPerPixel == 4
                        ? ReadUInt32LittleEndian(dib, srcOffset + x * 4)
                        : ReadUInt16LittleEndian(dib, srcOffset + x * 2);
                    int d = dstOffset + x * 4;
                    bgra[d] = ScaleChannel(sample & maskB, bShift, bMax);
                    bgra[d + 1] = ScaleChannel(sample & maskG, gShift, gMax);
                    bgra[d + 2] = ScaleChannel(sample & maskR, rShift, rMax);
                    byte a = maskA == 0 ? (byte)0xFF : ScaleChannel(sample & maskA, aShift, aMax);
                    bgra[d + 3] = a;
                    if (a != 0)
                    {
                        sawAlpha = true;
                    }
                }
            }

            // Alpha mask present but every pixel alpha is zero: producers that
            // hand out unwritten alpha bytes. Treat the payload as opaque
            // instead of pasting an invisible image.
            if (maskA != 0 && !sawAlpha)
            {
                for (int i = 3; i < bgra.Length; i += 4)
                {
                    bgra[i] = 0xFF;
                }
            }
        }

        byte[] png = ScreenRegionCapture.EncodeBgraToPng(bgra, width, absHeight);
        return (png, width, absHeight);
    }

    /// <summary>Locates the channel masks for a <c>BI_BITFIELDS</c> payload and
    /// returns the pixel-data offset. Windows can hand over either a plain
    /// 40-byte header with the masks appended (3 masks = 12 bytes, 4 = 16), or
    /// a V2/V3/V4/V5 header that carries them at fixed offsets inside the
    /// header itself. For the trailing-mask case the 3-vs-4 choice is decided
    /// by exact payload arithmetic (<paramref name="pixelBytes"/> comes from the
    /// declared dimensions and row stride), never by guessing at pixel data.
    /// Returns <c>PixelDataStart = -1</c> when the layout cannot be resolved.</summary>
    private static (uint MaskR, uint MaskG, uint MaskB, uint MaskA, int PixelDataStart) ResolveBitfieldMasks(
        byte[] dib, int length, int biSize, long pixelBytes)
    {
        if (biSize >= BitmapV3HeaderSize)
        {
            // V3/V4/V5: RGBA masks at fixed offsets, pixels after the header.
            return (
                ReadUInt32LittleEndian(dib, 40),
                ReadUInt32LittleEndian(dib, 44),
                ReadUInt32LittleEndian(dib, 48),
                ReadUInt32LittleEndian(dib, 52),
                biSize);
        }

        if (biSize == BitmapV2HeaderSize)
        {
            // V2: RGB masks at fixed offsets, no alpha mask.
            return (
                ReadUInt32LittleEndian(dib, 40),
                ReadUInt32LittleEndian(dib, 44),
                ReadUInt32LittleEndian(dib, 48),
                0,
                biSize);
        }

        if (biSize != BitmapInfoHeaderSize)
        {
            return (0, 0, 0, 0, -1);
        }

        long prefix = length - pixelBytes; // = header + however many trailing mask bytes follow
        long start3 = BitmapInfoHeaderSize + TrailingMaskBytesRgb;
        if (prefix < start3 || start3 > int.MaxValue)
        {
            return (0, 0, 0, 0, -1);
        }

        bool fourMasks = prefix == BitmapInfoHeaderSize + TrailingMaskBytesRgba;
        int pixelDataStart = (int)(fourMasks
            ? BitmapInfoHeaderSize + TrailingMaskBytesRgba
            : start3);
        uint alpha = fourMasks ? ReadUInt32LittleEndian(dib, 52) : 0;
        return (
            ReadUInt32LittleEndian(dib, 40),
            ReadUInt32LittleEndian(dib, 44),
            ReadUInt32LittleEndian(dib, 48),
            alpha,
            pixelDataStart);
    }

    /// <summary>Bit position and maximum value of a channel mask, used to scale
    /// non-8-bit channels (e.g. 5-6-5) up to 0..255.</summary>
    private static (int Shift, uint Max) MaskGeometry(uint mask)
    {
        if (mask == 0)
        {
            return (0, 0);
        }

        int shift = BitOperations.TrailingZeroCount(mask);
        int bits = BitOperations.PopCount(mask);
        uint max = bits >= 32 ? uint.MaxValue : (1u << bits) - 1u;
        return (shift, max);
    }

    private static byte ScaleChannel(uint maskedValue, int shift, uint max)
    {
        if (max == 0)
        {
            return 0;
        }

        uint raw = (maskedValue >> shift) & max;
        return (byte)((raw * 255u) / max);
    }

    private static int ReadInt32LittleEndian(byte[] data, int offset) =>
        data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24);

    private static int ReadInt16LittleEndian(byte[] data, int offset) =>
        data[offset] | (data[offset + 1] << 8);

    private static uint ReadUInt32LittleEndian(byte[] data, int offset) =>
        (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24));

    private static uint ReadUInt16LittleEndian(byte[] data, int offset) =>
        (uint)(data[offset] | (data[offset + 1] << 8));
}
