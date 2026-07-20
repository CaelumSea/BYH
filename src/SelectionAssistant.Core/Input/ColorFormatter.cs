namespace SelectionAssistant.Core.Input;

/// <summary>
/// R44: pure formatting helpers for the color picker. Separated from the
/// <c>ColorPickerLoupe</c> window so the hex/RGB conversion logic is
/// unit-testable without spinning up Avalonia / Win32 capture.
/// </summary>
public static class ColorFormatter
{
    /// <summary>
    /// Formats an 8-bit RGB triple as an uppercase <c>#RRGGBB</c> hex string
    /// (no alpha — screen pixels are opaque). Example: <c>(255, 254, 240)</c>
    /// → <c>#FFFEF0</c>. Used for the loupe readout and the clipboard copy.
    /// </summary>
    public static string ToHexRgb(byte r, byte g, byte b) =>
        string.Create(7, (r, g, b), static (span, state) =>
        {
            span[0] = '#';
            WriteHexByte(span, 1, state.r);
            WriteHexByte(span, 3, state.g);
            WriteHexByte(span, 5, state.b);
        });

    /// <summary>
    /// Formats an 8-bit RGB triple as <c>rgb(r, g, b)</c> (CSS-style). Used for
    /// the loupe's secondary readout line.
    /// </summary>
    public static string ToRgbDecimal(byte r, byte g, byte b) =>
        $"rgb({r}, {g}, {b})";

    private static void WriteHexByte(Span<char> span, int offset, byte value)
    {
        span[offset] = HexChar(value >> 4);
        span[offset + 1] = HexChar(value & 0x0F);
    }

    private static char HexChar(int nibble) =>
        (char)(nibble < 10 ? '0' + nibble : 'A' + nibble - 10);
}
