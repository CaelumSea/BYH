using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;

namespace SelectionAssistant.Platform.Windows.Capture;

/// <summary>
/// R24 track B: captures a screen region via Win32 <c>BitBlt</c> and encodes it
/// to a PNG data URI for the cloud OCR tier.
/// </summary>
/// <remarks>
/// PNG encoding is hand-written (IHDR + zlib IDAT + IEND, Adler-32 checksum) so
/// the capture layer stays free of <c>System.Drawing.Common</c>/WPF, which are
/// not NativeAOT-friendly under <c>TrimMode=full</c>. Uses the framework
/// <see cref="DeflateStream"/> for the zlib body.
/// </remarks>
public static class ScreenRegionCapture
{
    private const int Srccopy = 0x00CC0020;

    // Serializes every GDI capture. CaptureRawBgra has three concurrent entry
    // points with no natural mutual exclusion: the UI-thread Ocean Eyes main
    // capture (App.axaml.cs), the UI-thread 30 Hz color-picker loupe sampling
    // (SelectionRuntime.SampleCursorRegion), and a ThreadPool lazy-OCR capture
    // (SelectionRuntime.CaptureAndRecognizeRegionAsync via Task.Run). GDI calls
    // themselves are thread-safe, but interleaved BitBlt/GetDIBits on the live
    // screen DC under NativeAOT corrupted the heap intermittently and surfaced
    // as 0xc0000409 (STATUS_STACK_BUFFER_OVERRUN FailFast) — see the 2026-08-03
    // crash investigation. A process-wide lock forces all captures to run one
    // at a time, which is cheap because each capture is sub-millisecond for the
    // loupe's 15x15 and tens of ms for a full-screen BitBlt. Hold periods never
    // overlap with the OCR network round-trip (that runs after the lock is
    // released), so this does not serialize network work.
    private static readonly object _gdiGate = new();

    // Keep one capture from allocating an unbounded native bitmap and several
    // equally large managed buffers (BGRA + PNG raw scanlines + compression
    // workspace). A 4K frame is ~33 MB and remains below this ceiling; larger
    // selections fail cleanly and let the caller show a normal capture error
    // instead of taking down the NativeAOT process under memory pressure.
    public const long MaxPixelBufferBytes = 64L * 1024 * 1024;

    /// <summary>
    /// Captures <paramref name="width"/>×<paramref name="height"/> pixels at the
    /// given screen origin and returns the raw PNG bytes, or null if the region
    /// is empty or BitBlt fails. R40: the byte stream is now the primary
    /// surface — Ocean Eyes writes it to disk + clipboard; the OCR data-URI
    /// path wraps it via <see cref="CaptureAsDataUri"/>.
    /// </summary>
    public static byte[]? CaptureAsPng(int x, int y, int width, int height)
    {
        byte[]? bgra = CaptureRawBgra(x, y, width, height);
        return bgra is null ? null : PngEncoder.Encode(bgra, width, height);
    }

    /// <summary>
    /// Captures the region and returns BOTH the PNG bytes and the raw BGRA
    /// pixel buffer. R48 annotation burn-in uses the BGRA buffer directly,
    /// avoiding Avalonia.Bitmap.CopyPixels which throws
    /// ArgumentOutOfRangeException('stride') for some PNGs in Avalonia 12.
    /// </summary>
    public static (byte[] Png, byte[] Bgra, int Width, int Height)? CaptureAsPngAndBgra(
        int x, int y, int width, int height)
    {
        byte[]? bgra = CaptureRawBgra(x, y, width, height);
        if (bgra is null) return null;
        byte[] png = PngEncoder.Encode(bgra, width, height);
        return (png, bgra, width, height);
    }

    /// <summary>
    /// R44: captures the region via BitBlt and returns the raw 32-bit BGRA byte
    /// buffer (top-down, no PNG encoding). Used by the color picker loupe,
    /// which samples a small 15×15 region around the cursor at ~30 Hz — PNG
    /// encoding at that rate would dominate CPU. The returned buffer has
    /// <c>width * height * 4</c> bytes laid out as B,G,R,A per pixel, row-major
    /// from the top-left. Returns null on any Win32 failure.
    /// </summary>
    public static byte[]? CaptureRawBgra(int x, int y, int width, int height)
    {
        // Serialize against the other two capture entry points (see _gdiGate).
        // The pre-check is outside the lock so oversized/empty regions reject
        // instantly without contending; the real GDI work happens under the
        // lock so concurrent captures never interleave BitBlt/GetDIBits.
        if (!TryGetPixelBufferLength(width, height, out _))
        {
            return null;
        }

        lock (_gdiGate)
        {
            nint screenDc = GetDC(0);
            if (screenDc == 0)
            {
                return null;
            }

            nint memoryDc = CreateCompatibleDC(screenDc);
            nint bitmap = CreateCompatibleBitmap(screenDc, width, height);
            nint oldBitmap = 0;

            try
            {
                if (memoryDc == 0 || bitmap == 0)
                {
                    return null;
                }

                oldBitmap = SelectObject(memoryDc, bitmap);
                if (oldBitmap == 0)
                {
                    return null;
                }

                if (!BitBlt(memoryDc, 0, 0, width, height, screenDc, x, y, Srccopy))
                {
                    return null;
                }

                return ReadBitmapBits(memoryDc, bitmap, width, height);
            }
            finally
            {
                if (oldBitmap != 0 && memoryDc != 0)
                {
                    SelectObject(memoryDc, oldBitmap);
                }

                if (bitmap != 0)
                {
                    DeleteObject(bitmap);
                }

                if (memoryDc != 0)
                {
                    DeleteDC(memoryDc);
                }

                ReleaseDC(0, screenDc);
            }
        }
    }

    /// <summary>
    /// R44: samples a single screen pixel and returns its 8-bit RGB components.
    /// Convenience wrapper over <see cref="CaptureRawBgra"/> for the color
    /// picker's "click to confirm" path (one-shot 1×1 capture). Returns null
    /// if the capture failed (e.g. screen DC unavailable). Coordinates are in
    /// physical screen pixels (same space as <see cref="CaptureAsPng"/>).
    /// </summary>
    public static (byte R, byte G, byte B)? SamplePixel(int x, int y)
    {
        byte[]? bgra = CaptureRawBgra(x, y, 1, 1);
        if (bgra is null || bgra.Length < 3)
        {
            return null;
        }
        // 32-bit BGRA: index 0=B, 1=G, 2=R, 3=A.
        return (R: bgra[2], G: bgra[1], B: bgra[0]);
    }

    private static byte[]? ReadBitmapBits(nint memoryDc, nint bitmap, int width, int height)
    {
        var info = new BitmapInfo
        {
            Size = BitmapInfoSize,
            Width = width,
            Height = -height, // negative => top-down DIB, no separate flip needed
            Planes = 1,
            BitCount = 32,
            Compression = 0,
        };

        if (!TryGetPixelBufferLength(width, height, out int bufferLength))
        {
            return null;
        }

        int stride = checked(width * 4);
        var bits = new byte[bufferLength];
        int copied = GetDIBits(memoryDc, bitmap, 0, height, bits, ref info, 0);
        return copied == 0 ? null : bits;
    }

    internal static bool TryGetPixelBufferLength(int width, int height, out int length)
    {
        length = 0;
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        long bytes = (long)width * height * 4;
        if (bytes <= 0 || bytes > MaxPixelBufferBytes || bytes > int.MaxValue)
        {
            return false;
        }

        length = (int)bytes;
        return true;
    }

    /// <summary>
    /// Captures the region and returns a <c>data:image/png;base64,...</c> URI
    /// for the OCR client. Thin wrapper over <see cref="CaptureAsPng"/>: null
    /// stays null, otherwise the bytes are base64-encoded with the PNG MIME
    /// prefix. Preserved so the existing OCR pipeline (which only needs the
    /// URI) doesn't have to change.
    /// </summary>
    public static string? CaptureAsDataUri(int x, int y, int width, int height)
    {
        byte[]? png = CaptureAsPng(x, y, width, height);
        return png is null ? null : "data:image/png;base64," + Convert.ToBase64String(png);
    }

    /// <summary>
    /// R47: re-encodes an existing BGRA pixel buffer to PNG bytes.
    /// Used by the annotation badge burn-in path which modifies pixels
    /// in-place then needs to re-encode. Exposes the internal
    /// <see cref="PngEncoder"/> without making it public.
    /// </summary>
    public static byte[] EncodeBgraToPng(byte[] bgra, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        if (!TryGetPixelBufferLength(width, height, out int expected) ||
            bgra.Length != expected)
        {
            throw new ArgumentException("BGRA dimensions exceed the supported capture budget or do not match the buffer length.", nameof(bgra));
        }

        return PngEncoder.Encode(bgra, width, height);
    }

    private const int BitmapInfoSize = 40;

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public int Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public int Compression;
        public int SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public int ClrUsed;
        public int ClrImportant;
    }

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint hwnd, nint dc);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleDC(nint dc);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleBitmap(nint dc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern nint SelectObject(nint dc, nint obj);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(
        nint dest, int destX, int destY, int width, int height,
        nint source, int sourceX, int sourceY, int rasterOp);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(
        nint dc, nint bitmap, int startScan, int scanLines,
        byte[] bits, ref BitmapInfo info, int usage);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(nint dc);
}

/// <summary>
/// Minimal hand-written PNG encoder for 32-bit BGRA pixels (no alpha
/// premultiplication — OCR works on opaque screen content). Emits an 8-bit
/// truecolour PNG (colour type 6) with a single zlib-compressed IDAT chunk.
/// </summary>
internal static class PngEncoder
{
    private static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };

    public static byte[] Encode(byte[] bgra, int width, int height)
    {
        if (!ScreenRegionCapture.TryGetPixelBufferLength(width, height, out int expected) ||
            bgra.Length != expected)
        {
            throw new ArgumentException("BGRA dimensions exceed the supported capture budget or do not match the buffer length.", nameof(bgra));
        }

        byte[] raw = BuildRawWithFilter(bgra, width, height);
        byte[] zlib = ZlibCompress(raw);

        using var stream = new MemoryStream();
        stream.Write(Signature, 0, Signature.Length);
        WriteChunk(stream, "IHDR", WriteIhdr(width, height));
        WriteChunk(stream, "IDAT", zlib);
        WriteChunk(stream, "IEND", Array.Empty<byte>());
        return stream.ToArray();
    }

    // PNG raw data: one filter-type byte (0 = None) per scanline, then the
    // BGRA pixels converted to RGBA. 0 filter keeps the encoder trivial and is
    // fine for OCR-grade output (Deflate still compresses well).
    private static byte[] BuildRawWithFilter(byte[] bgra, int width, int height)
    {
        int rowBytes = checked(width * 4);
        int rawLength = checked((rowBytes + 1) * height);
        var raw = new byte[rawLength];
        int src = 0;
        int dst = 0;
        for (int y = 0; y < height; y++)
        {
            raw[dst++] = 0; // filter: None
            for (int x = 0; x < width; x++)
            {
                byte b = bgra[src];
                byte g = bgra[src + 1];
                byte r = bgra[src + 2];
                byte a = bgra[src + 3];
                raw[dst++] = r;
                raw[dst++] = g;
                raw[dst++] = b;
                raw[dst++] = a;
                src += 4;
            }
        }

        return raw;
    }

    private static byte[] WriteIhdr(int width, int height)
    {
        var ihdr = new byte[13];
        WriteBigEndianInt32(ihdr, 0, width);
        WriteBigEndianInt32(ihdr, 4, height);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 6;  // colour type: truecolour with alpha
        ihdr[10] = 0; // compression: deflate
        ihdr[11] = 0; // filter: adaptive
        ihdr[12] = 0; // interlace: none
        return ihdr;
    }

    // zlib format: 0x78 0x9C header + deflate body + Adler-32 of the *raw* data.
    private static byte[] ZlibCompress(byte[] raw)
    {
        using var compressed = new MemoryStream();
        compressed.WriteByte(0x78); // CMF: deflate, window 32K
        compressed.WriteByte(0x9C); // FLG: default compression, check bits

        using (var deflate = new DeflateStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(raw, 0, raw.Length);
        }

        // zlib stores Adler-32 big-endian after the deflate stream.
        uint adler = ComputeAdler32(raw);
        compressed.WriteByte((byte)((adler >> 24) & 0xFF));
        compressed.WriteByte((byte)((adler >> 16) & 0xFF));
        compressed.WriteByte((byte)((adler >> 8) & 0xFF));
        compressed.WriteByte((byte)(adler & 0xFF));

        return compressed.ToArray();
    }

    private static uint ComputeAdler32(byte[] data)
    {
        const uint Modulo = 65521;
        uint a = 1;
        uint b = 0;
        for (int i = 0; i < data.Length; i++)
        {
            a = (a + data[i]) % Modulo;
            b = (b + a) % Modulo;
        }

        return (b << 16) | a;
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        // PNG chunk layout per spec: [4-byte length][4-byte type][data][4-byte CRC].
        // CRC is computed over type + data and written AFTER the data.
        // (Earlier this wrote CRC before data, producing an invalid PNG that the
        // image host rejected with "not a valid image".)
        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        byte[] lengthBytes = new byte[4];
        WriteBigEndianInt32(lengthBytes, 0, data.Length);
        stream.Write(lengthBytes, 0, 4);
        stream.Write(typeBytes, 0, 4);

        if (data.Length > 0)
        {
            stream.Write(data, 0, data.Length);
        }

        uint crc = ComputeCrc32(typeBytes, data);
        stream.WriteByte((byte)((crc >> 24) & 0xFF));
        stream.WriteByte((byte)((crc >> 16) & 0xFF));
        stream.WriteByte((byte)((crc >> 8) & 0xFF));
        stream.WriteByte((byte)(crc & 0xFF));
    }

    // PNG CRC-32 (polynomial 0xEDB88320) over chunk type + data.
    private static uint ComputeCrc32(byte[] typeBytes, byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        for (int i = 0; i < typeBytes.Length; i++)
        {
            crc = CrcTable[(crc ^ typeBytes[i]) & 0xFF] ^ (crc >> 8);
        }

        for (int i = 0; i < data.Length; i++)
        {
            crc = CrcTable[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFF;
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? (0xEDB88320 ^ (c >> 1)) : (c >> 1);
            }

            table[n] = c;
        }

        return table;
    }

    private static void WriteBigEndianInt32(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)((value >> 24) & 0xFF);
        buffer[offset + 1] = (byte)((value >> 16) & 0xFF);
        buffer[offset + 2] = (byte)((value >> 8) & 0xFF);
        buffer[offset + 3] = (byte)(value & 0xFF);
    }
}
