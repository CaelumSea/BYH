using System.Runtime.InteropServices;
using SelectionAssistant.Platform.Windows.Capture;

namespace SelectionAssistant.Platform.Windows.Launcher;

/// <summary>
/// Extracts an application's small icon (32×32 PNG bytes) from its executable
/// path via Win32 <c>SHGetFileInfo</c>, then converts the HICON to BGRA pixels
/// (via <c>GetIconInfo</c> + <c>GetDIBits</c>) and encodes to PNG using the
/// same hand-written <see cref="PngEncoder"/> used by ScreenRegionCapture.
/// Returns <c>null</c> on any failure — callers fall back to a default icon.
/// </summary>
/// <remarks>
/// <para>
/// All P/Invoke uses explicit <c>CharSet.Unicode</c> + <c>StructLayout</c>; no
/// reflection, no <c>System.Drawing.Common</c> (which is not NativeAOT-friendly
/// under TrimMode=full). The PNG output is decoded by Avalonia's Bitmap in the
/// UI layer via <c>new Bitmap(stream)</c>.
/// </para>
/// <para>
/// <b>Web favicons</b> are not extracted here — the UI layer fetches them over
/// HTTP from <c>https://www.google.com/s2/favicons</c> and caches the result
/// to the launcher-icons directory.
/// </para>
/// </remarks>
public static class WindowsIconExtractor
{
    private const uint SHGFIIcon = 0x000000100;
    private const uint SHGFISmallIcon = 0x000000001;
    private const uint SHGFIUseFileAttributes = 0x000000010;

    private const int IconSmall = 0;
    private const int BitmapType = 0;

    /// <summary>
    /// Extracts the small icon from <paramref name="exePath"/> and returns it
    /// as a PNG byte array (RGBA, 32-bit). Returns <c>null</c> if the path is
    /// invalid, the file has no icon, or any Win32 step fails.
    /// </summary>
    /// <param name="exePath">Absolute path to an .exe file.</param>
    public static byte[]? ExtractSmallIconPng(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
        {
            LastDiagnostic = "file-missing";
            return null;
        }

        nint iconHandle = ExtractIconHandle(exePath);
        if (iconHandle == 0)
        {
            LastDiagnostic = $"SHGetFileInfo returned 0; LastError={Marshal.GetLastPInvokeError()}";
            return null;
        }

        try
        {
            byte[]? png = ConvertIconToPng(iconHandle);
            // Note: ConvertIconToPng sets LastDiagnostic itself with the failure
            // point. Only mark success here — don't overwrite the failure trail.
            if (png is not null)
            {
                LastDiagnostic = "ok";
            }
            return png;
        }
        finally
        {
            DestroyIcon(iconHandle);
        }
    }

    /// <summary>
    /// Diagnostic string from the last call to <see cref="ExtractSmallIconPng"/>.
    /// Only populated when something failed (or "ok" on success). Useful for
    /// the <c>--probe-icon-extract</c> CLI tool.
    /// </summary>
    public static string? LastDiagnostic { get; private set; }

    private static nint ExtractIconHandle(string exePath)
    {
        // SHGetFileInfo with SHGFI_ICON | SHGFI_SMALLICON gives us an HICON for
        // the file's small icon (typically 32×32 at 96 DPI, scales with system DPI).
        var info = new ShFileInfo();
        uint flags = SHGFIIcon | SHGFISmallIcon;
        uint cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<ShFileInfo>();
        uint result = SHGetFileInfo(exePath, 0, ref info, cbSize, flags);
        // Track for diagnostics; SHGetFileInfo returns the same value passed in
        // cbFileInfo on success (non-zero), 0 on failure.
        LastShGetFileInfoResult = result;
        LastShGetFileInfoError = result == 0 ? System.Runtime.InteropServices.Marshal.GetLastPInvokeError() : 0;
        LastShGetFileInfoCbSize = cbSize;
        return result != 0 ? info.IconHandle : 0;
    }

    public static uint LastShGetFileInfoResult { get; private set; }
    public static int LastShGetFileInfoError { get; private set; }
    public static uint LastShGetFileInfoCbSize { get; private set; }

    private static byte[]? ConvertIconToPng(nint iconHandle)
    {
        // GetIconInfo returns 3 GDI handles: hbmMask (1bpp AND mask, height*2
        // to accommodate XOR+AND for cursors), hbmColor (24/32 bpp DIB), and
        // hbmColor's dimensions give us the icon size.
        var iconInfo = new IconInfo();
        if (!GetIconInfo(iconHandle, ref iconInfo))
        {
            LastDiagnostic += $" | GetIconInfo failed err={Marshal.GetLastPInvokeError()}";
            return null;
        }
        LastDiagnostic += $" | GetIconInfo ok isIcon={iconInfo.IsIcon} mask={iconInfo.MaskBitmap} color={iconInfo.ColorBitmap}";

        nint colorBitmap = iconInfo.ColorBitmap;
        nint maskBitmap = iconInfo.MaskBitmap;
        try
        {
            if (colorBitmap == 0)
            {
                LastDiagnostic += " | colorBitmap=0";
                return null;
            }

            nint screenDc = GetDC(0);
            if (screenDc == 0)
            {
                LastDiagnostic += " | GetDC(0)=0";
                return null;
            }

            int width;
            int height;
            ushort bitCount;
            byte[]? bgra;

            try
            {
                // First pass: ask GetDIBits with biWidth=biHeight=0 to fill the
                // BITMAPINFOHEADER with the source bitmap's dimensions. This
                // avoids any reliance on GetObject (which returns 0 on DIBs).
                var probe = new BitmapInfoHeader
                {
                    Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<BitmapInfoHeader>(),
                    Width = 0,
                    Height = 0,
                    Planes = 1,
                    BitCount = 0,
                    Compression = 0,
                };
                int probeResult = GetDIBits(screenDc, colorBitmap, 0, 0, nint.Zero, ref probe, 0);
                if (probeResult == 0 || probe.Width <= 0 || probe.Height == 0)
                {
                    LastDiagnostic += $" | GetDIBits probe failed probeResult={probeResult} err={Marshal.GetLastPInvokeError()} biW={probe.Width} biH={probe.Height}";
                    return null;
                }
                // probe.Height is the height of the XOR image; the absolute
                // value is the real pixel height.
                width = probe.Width;
                height = Math.Abs(probe.Height);
                bitCount = probe.BitCount == 0 ? (ushort)32 : probe.BitCount;
                LastDiagnostic += $" | dims={width}x{height} srcBits={bitCount}";

                // Second pass: request 32-bit BGRA pixels at the resolved size.
                var info = new BitmapInfoHeader
                {
                    Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<BitmapInfoHeader>(),
                    Width = width,
                    Height = -height, // negative => top-down, no flip needed
                    Planes = 1,
                    BitCount = 32,
                    Compression = 0,
                };
                bgra = new byte[width * 4 * height];
                int copied = GetDIBits(screenDc, colorBitmap, 0, (uint)height, bgra, ref info, 0);
                if (copied == 0)
                {
                    LastDiagnostic += $" | GetDIBits copy failed err={Marshal.GetLastPInvokeError()}";
                    return null;
                }
            }
            finally
            {
                ReleaseDC(0, screenDc);
            }

            // If the source color bitmap was < 32bpp, the AND mask carries the
            // transparency. Apply it: where the mask bit is 1, alpha=0.
            if (bitCount < 32 && maskBitmap != 0)
            {
                ApplyAlphaMask(bgra, width, height, maskBitmap);
            }
            else
            {
                // 32-bit icons sometimes store a bogus alpha of 0 everywhere.
                // Treat fully-transparent-looking pixels as opaque unless the
                // color is black (a common real-transparent value).
                ForceOpaqueIfTransparent(bgra);
            }

            return PngEncoder.Encode(bgra, width, height);
        }
        finally
        {
            if (iconInfo.ColorBitmap != 0) DeleteObject(iconInfo.ColorBitmap);
            if (iconInfo.MaskBitmap != 0) DeleteObject(iconInfo.MaskBitmap);
        }
    }

    private static byte[]? ReadColorBits(nint colorBitmap, int width, int height)
    {
        // Legacy helper — no longer used after switching to two-pass GetDIBits
        // inside ConvertIconToPng. Kept for reference until the dust settles.
        throw new NotSupportedException();
    }

    private static void ApplyAlphaMask(byte[] bgra, int width, int height, nint maskBitmap)
    {
        // The mask is a 1bpp bitmap, padded to 4-byte rows. Each set bit means
        // "transparent" (XOR cursor semantics). We only need this for 24bpp
        // color icons where alpha is absent.
        int maskStride = ((width + 31) / 32) * 4;
        var info = new BitmapInfoHeader
        {
            Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<BitmapInfoHeader>(),
            Width = width,
            Height = -height,
            Planes = 1,
            BitCount = 1,
            Compression = 0,
        };
        var maskBits = new byte[maskStride * height];
        nint screenDc = GetDC(0);
        if (screenDc == 0)
        {
            return;
        }
        try
        {
            if (GetDIBits(screenDc, maskBitmap, 0, (uint)height, maskBits, ref info, 0) == 0)
            {
                return;
            }
        }
        finally
        {
            ReleaseDC(0, screenDc);
        }

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int byteIndex = y * maskStride + (x >> 3);
                int bitIndex = 7 - (x & 7);
                bool transparent = (maskBits[byteIndex] & (1 << bitIndex)) != 0;
                int pixel = (y * width + x) * 4;
                bgra[pixel + 3] = transparent ? (byte)0 : (byte)255;
            }
        }
    }

    private static void ForceOpaqueIfTransparent(byte[] bgra)
    {
        // Many modern .ico files ship 32-bit pixels but with alpha=0 across the
        // board (the AND mask drives transparency). Without an AND mask lookup
        // we treat "alpha=0 AND non-black color" as opaque to avoid invisible
        // icons. Pure black stays as-is because that's a plausible transparent.
        for (int i = 0; i < bgra.Length; i += 4)
        {
            byte b = bgra[i];
            byte g = bgra[i + 1];
            byte r = bgra[i + 2];
            byte a = bgra[i + 3];
            if (a == 0 && (b != 0 || g != 0 || r != 0))
            {
                bgra[i + 3] = 255;
            }
        }
    }

    // ── P/Invoke ──

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileInfo
    {
        public nint IconHandle;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string TypeName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        public bool IsIcon;
        public int XHotspot;
        public int YHotspot;
        public nint MaskBitmap;
        public nint ColorBitmap;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Win32Bitmap
    {
        // Used by GetObject; no longer invoked since we switched to two-pass
        // GetDIBits for dimension probing. Kept for potential future use.
        public int Type;
        public int Width;
        public int Height;
        public int WidthBytes;
        public ushort Planes;
        public ushort BitCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, BestFitMapping = false, SetLastError = true)]
    private static extern uint SHGetFileInfo(
        string pszPath,
        uint dwFileAttributes,
        ref ShFileInfo psfi,
        uint cbFileInfo,
        uint uFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetIconInfo(nint hIcon, ref IconInfo piconinfo);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint hIcon);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(
        nint hdc,
        nint hbmp,
        uint uStartScan,
        uint cScanLines,
        nint lpvBits,
        ref BitmapInfoHeader lpbi,
        uint uUsage);

    [DllImport("gdi32.dll", EntryPoint = "GetDIBits")]
    private static extern int GetDIBits(
        nint hdc,
        nint hbmp,
        uint uStartScan,
        uint cScanLines,
        [Out] byte[] lpvBits,
        ref BitmapInfoHeader lpbi,
        uint uUsage);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint hObject);

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint hwnd, nint dc);
}
